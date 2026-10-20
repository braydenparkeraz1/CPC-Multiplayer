-- Car Park Capital Multiplayer - object sync.
-- Detects objects the local player places or demolishes and reports them to the helper,
-- and replays objects placed or demolished by other players.
-- Objects are matched between PCs by a key built from template name, tile position, rotation and variant,
-- because entity ids differ on every machine.
--
-- Shared objects ("tracked") are the ones every PC must agree on: the loaded city, our player's
-- builds and other players' builds. Objects the game makes by itself (spills and the like) are
-- each PC's own business. A digest of the tracked keys (a count and a hash sum per bucket) lets
-- the helper check our city against the host's; "dump" and "reconcile" repair a bucket.
local MP = ...

local S = MP.room

local BASELINE_TICKS = 20      -- objects that exist in the first 2s are the loaded world, not new builds
local GHOST_WINDOW = 20        -- a placement counts as the player's if their build ghost was seen in the last 2s
local REMOVE_DELAY = 15        -- wait 1.5s before reporting a vanished object, in case the same object reappears
local SCAN_PER_TICK = 2000     -- known objects re-checked per tick, so a demolition is seen before it finishes
local EXPECT_TICKS = 100       -- how long a replayed placement or demolition may take
local CLEAR_TICKS = 10         -- replayed placements wait this long after a replayed demolition, so the tile is free
local RETRY_TICKS = 15         -- a blocked replayed placement is retried this far apart...
local MIN_PLACE_TRIES = 2      -- ...at least this many times...
local MAX_PLACE_TRIES = 20     -- ...and up to this many while something may still free its tile (worthRetrying)
local OWN_BUILD_WAIT = 150     -- a client's own build may be in the way for this long: the host takes it back
                               -- if the host's city has something else there
local DIGEST_TICKS = 20        -- how often the digest goes to the helper
local BUCKETS = 64
local RECENT_LOCAL_TICKS = 100 -- a repair never takes out what our player built in the last 10s

S.known = S.known or {}            -- entity -> key
S.byKey = S.byKey or {}            -- key -> entity
S.scanList = S.scanList or {}
S.scanPos = S.scanPos or 1
S.ghosts = S.ghosts or {}          -- templateIndex -> tick a player ghost was last seen
S.expectPlace = S.expectPlace or {}           -- key -> simTick the replayed placement gives up
S.expectRemoval = S.expectRemoval or {}       -- key -> simTick the replayed demolition gives up
S.removeAfterPlace = S.removeAfterPlace or {}
S.pendingRemovals = S.pendingRemovals or {}   -- key -> tick the object vanished
S.demolishing = S.demolishing or {}           -- entity -> tick its demolition started (it stays until done)
S.deferred = S.deferred or {}
S.delayed = S.delayed or {}                   -- { tick = <run at>, msg = <place message> }
S.placing = S.placing or {}                   -- key -> replayed placement in progress
S.tracked = S.tracked or {}                   -- key -> hash, for shared objects
S.localAt = S.localAt or {}                   -- key -> tick our player built it
S.lastLocalBuild = S.lastLocalBuild or -OWN_BUILD_WAIT
S.lastRemoteRemoval = S.lastRemoteRemoval or -CLEAR_TICKS
S.tick = S.tick or 0
-- Ticks counted only while the game runs. Timeouts that wait for the game to finish something
-- (a replayed placement, a repaint) use these, so a long pause doesn't make them give up.
S.simTick = S.simTick or 0
S.readyTick = S.readyTick or (S.tick + BASELINE_TICKS)

if not S.counts then
    S.counts, S.sums = {}, {}
    for i = 1, BUCKETS do S.counts[i], S.sums[i] = 0, 0 end
end

local function keyFor(tpl, x, y, z, rot, var)
    return string.format("%s@%d,%d,%d/%d/%d", tpl, x, y, z, rot, var)
end

-- A 32-bit hash of a key; plain arithmetic so it is the same in every Lua version (and in the
-- helper's tests). The helper only compares hashes, it never computes them.
local function hashKey(key)
    local h = 0
    for i = 1, #key do
        h = (h * 131 + key:byte(i)) % 4294967296
    end
    return h
end

local function bucketOf(hash)
    return math.floor(hash / 4096) % BUCKETS
end
MP.bucketOfKey = function(key) return bucketOf(hashKey(key)) end

-- The keys the helper accepts (src/Protocol/BuildEvent.cs); other objects can't be shared, so they stay local.
local KEY_PATTERN = "^[%w_]+@%d+,%d+,%d+/%d+/%d+$"

local function track(key)
    if S.tracked[key] or not key:match(KEY_PATTERN) then return end
    local h = hashKey(key)
    S.tracked[key] = h
    local b = bucketOf(h) + 1
    S.counts[b] = S.counts[b] + 1
    S.sums[b] = (S.sums[b] + h) % 4294967296
end

local function untrack(key)
    local h = S.tracked[key]
    if not h then return end
    S.tracked[key] = nil
    S.localAt[key] = nil
    local b = bucketOf(h) + 1
    S.counts[b] = S.counts[b] - 1
    S.sums[b] = (S.sums[b] - h) % 4294967296
end

-- Only call on entities known to have Placed (getFor would otherwise add the component).
local function describePlaced(e)
    if not component.Persistent.has(e) then return nil end
    local tpl = component.Persistent.getFor(e).applyTemplateOnLoad
    if type(tpl) ~= "string" or tpl == "" or tpl:find("^MPPaint") then return nil end
    local placed = component.Placed.getFor(e)
    local p = placed.tilePosition
    local x, y, z, rot, var = p.x, p.y, p.z, placed.rotation, placed.variantId
    return keyFor(tpl, x, y, z, rot, var), { key = nil, tpl = tpl, x = x, y = y, z = z, rot = rot, var = var }
end

local function index(e, key)
    S.known[e] = key
    S.byKey[key] = e
    S.scanList[#S.scanList + 1] = e
    -- The same object vanishing and coming straight back (the game re-creating it) is not a removal.
    S.pendingRemovals[key] = nil
end

local function unindex(e)
    local key = S.known[e]
    S.known[e] = nil
    if key and S.byKey[key] == e then S.byKey[key] = nil end
    return key
end

-- Starts the game's own demolition of a shared object for another player (or a repair);
-- it isn't reported back when it finishes.
local function demolishFor(key, e)
    S.expectRemoval[key] = S.simTick + EXPECT_TICKS
    S.lastRemoteRemoval = S.tick
    component.Demolished.getFor(e)
end

-- Removals wait a moment before being reported; a new placement must not overtake them,
-- or other players would try to place it while the old object still blocks the tile.
local function flushRemovals(olderThan)
    for key, tick in pairs(S.pendingRemovals) do
        if S.tick - tick >= olderThan then
            S.pendingRemovals[key] = nil
            untrack(key)
            MP.emit("remove", { key = key })
        end
    end
end

local function isPlayerPlacement(e)
    local seen = S.ghosts[component.Placeable.getFor(e).templateIndex]
    return seen ~= nil and S.tick - seen <= GHOST_WINDOW
end

local function processCandidates()
    for e, age in pairs(S.candidates) do
        if not valid(e) then
            S.candidates[e] = nil
            S.playerPlaced[e] = nil
        elseif component.Placed.has(e) then
            S.candidates[e] = nil
            local byPlayer = S.playerPlaced[e]
            S.playerPlaced[e] = nil
            if not S.known[e] then
                local key, info = describePlaced(e)
                if key then
                    index(e, key)
                    if S.tick < S.readyTick then
                        -- part of the loaded world
                        track(key)
                    elseif S.expectPlace[key] then
                        S.expectPlace[key] = nil
                        track(key)
                        MP.emit("placed", { key = key })
                        -- Its removal arrived while the placement was still finishing.
                        if S.removeAfterPlace[key] then
                            S.removeAfterPlace[key] = nil
                            demolishFor(key, e)
                        end
                    elseif byPlayer or isPlayerPlacement(e) then
                        flushRemovals(0)
                        track(key)
                        S.localAt[key] = S.tick
                        S.lastLocalBuild = S.tick
                        info.key = key
                        MP.emit("place", info)
                    end
                    -- Anything else the game made by itself. A shared object it re-created stays
                    -- tracked (its key never left S.tracked).
                end
            end
        elseif component.Placing.has(e) then
            -- The build tool's preview ghost has no Persistent component; scripted placements do.
            if not component.Persistent.has(e) then
                S.ghosts[component.Placeable.getFor(e).templateIndex] = S.tick
            end
        elseif age >= 30 then
            S.candidates[e] = nil
        else
            S.candidates[e] = age + 1
        end
    end
end

local function scanKnown()
    local list = S.scanList
    local budget = math.min(SCAN_PER_TICK, #list)
    for _ = 1, budget do
        if S.scanPos > #list then S.scanPos = 1 end
        local e = list[S.scanPos]
        local gone = not valid(e)
        local demolished = not gone and component.Demolished.has(e)
        if gone or demolished or not S.known[e] then
            local key = unindex(e)
            if demolished then S.demolishing[e] = S.tick end
            list[S.scanPos] = list[#list]
            list[#list] = nil
            local other = key and S.byKey[key]
            if other and other ~= e and valid(other) then
                -- The game re-created this object (the new entity is already indexed): nothing left.
                S.expectRemoval[key] = nil
            elseif key and not S.leaving then
                if S.expectRemoval[key] then
                    S.expectRemoval[key] = nil
                    untrack(key)
                elseif not S.tracked[key] then
                    -- not shared: nobody else needs to know
                elseif demolished then
                    untrack(key)
                    MP.emit("demolish", { key = key })
                else
                    S.pendingRemovals[key] = S.tick
                end
            end
        else
            S.scanPos = S.scanPos + 1
        end
    end
end

-- A blocked placement is only worth another try if something may still free its tile: a removal
-- in progress, or (for a build the host already has) one of our own new builds, which the host
-- takes back when its city has something else there. A friend's build on the host ("proposal")
-- doesn't wait for the host's own builds: those stay, so the friend's build is taken back instead.
local function worthRetrying(p)
    if p.tries + 1 < MIN_PLACE_TRIES then return true end
    if p.tries + 1 >= MAX_PLACE_TRIES then return false end
    if next(S.expectRemoval) ~= nil or next(S.pendingRemovals) ~= nil or next(S.demolishing) ~= nil then return true end
    if S.tick - S.lastRemoteRemoval < 3 * RETRY_TICKS then return true end
    return not p.msg.proposal and S.tick - S.lastLocalBuild < OWN_BUILD_WAIT
end

local function flushPending()
    flushRemovals(REMOVE_DELAY)
    if #S.delayed > 0 then
        local due, later = {}, {}
        for _, item in ipairs(S.delayed) do
            if S.tick >= item.tick then due[#due + 1] = item.msg else later[#later + 1] = item end
        end
        S.delayed = later
        for _, msg in ipairs(due) do
            local ok, err = pcall(MP.handlers.place, msg)
            if not ok then MP.emit("error", { id = msg.id, type = msg.type, err = tostring(err) }) end
        end
    end
    -- A replayed placement whose Placing entity vanished without producing the object was blocked,
    -- usually by an old object that another player removed but whose removal hasn't arrived yet.
    for key, p in pairs(S.placing) do
        local placed = S.byKey[key]
        if placed and valid(placed) then
            S.placing[key] = nil
        elseif valid(p.entity) and S.simTick - p.simTick > EXPECT_TICKS then
            -- The game never finished or dropped it: give up, without retrying.
            destroyEntity(p.entity)
            p.hung = true
        end
        if S.placing[key] and not valid(p.entity) then
            S.placing[key] = nil
            if S.removeAfterPlace[key] then
                S.removeAfterPlace[key] = nil
                S.expectPlace[key] = nil
            elseif not p.hung and worthRetrying(p) then
                local retry = {}
                for k, v in pairs(p.msg) do retry[k] = v end
                retry.tries = p.tries + 1
                S.delayed[#S.delayed + 1] = { tick = S.tick + RETRY_TICKS, msg = retry }
            else
                S.expectPlace[key] = nil
                MP.emit("blocked", { key = key })
            end
        end
    end
    for key, untilTick in pairs(S.expectPlace) do
        if S.simTick > untilTick and not S.placing[key] then
            S.expectPlace[key] = nil
            S.removeAfterPlace[key] = nil
        end
    end
    for key, untilTick in pairs(S.expectRemoval) do
        if S.simTick > untilTick then S.expectRemoval[key] = nil end
    end
    for e, tick in pairs(S.demolishing) do
        if not valid(e) or S.tick - tick > 600 then S.demolishing[e] = nil end
    end
end

local function runDeferred()
    local deferred = S.deferred
    S.deferred = {}
    for _, msg in ipairs(deferred) do
        local handler = MP.handlers[msg.type]
        if handler then
            local ok, err = pcall(handler, msg)
            if not ok then MP.emit("error", { id = msg.id, type = msg.type, err = tostring(err) }) end
        end
    end
end

-- Roads and floors: the helper writes tiles straight into the game's memory, which the game doesn't
-- notice. For each changed area it picks an anchor tile (left one material off) and asks us to
-- repaint it with an invisible MPPaint<material> object. That real floor change makes the game
-- redraw the area and update where cars and people can go, including the helper's tiles around it.
local PAINTS_PER_TICK = 40
local PAINT_TIMEOUT = 30
S.paintQueue = S.paintQueue or {}
S.painting = S.painting or {}

MP.handlers.paint = function(msg)
    for _, a in ipairs(msg.anchors or {}) do S.paintQueue[#S.paintQueue + 1] = a end
end

local function runPaints()
    local results = {}
    local started = 0
    while #S.paintQueue > 0 and started < PAINTS_PER_TICK do
        local a = table.remove(S.paintQueue, 1)
        started = started + 1
        local e = createEntity()
        local ok = pcall(function()
            local p = component.Placing.getFor(e)
            p.at.tilePosition = uvec3(a.x, a.y + 1, a.z)
            p.bNoCost = true
            p.bTryFinish = true
            p.bIgnoreLandStatus = true
            p.bIgnoreNavConnection = true
            p.bIgnoreFocusAfterPlacing = true
            p.bIgnoreInspectAfterPlacing = true
            p.bAutoPaintFloor = true
            p.bDestroyOnBlockedFinish = false
            applyTemplate(e, "MPPaint" .. a.mat, {}, false)
        end)
        if ok then
            S.painting[a.id] = { entity = e, tick = S.tick, simTick = S.simTick }
        else
            if valid(e) then destroyEntity(e) end
            results[#results + 1] = { id = a.id, ok = false }
        end
    end
    for id, p in pairs(S.painting) do
        local e = p.entity
        if not valid(e) then
            -- The Placing entity is replaced by the placed painter once the floor is painted.
            S.painting[id] = nil
            results[#results + 1] = { id = id, ok = true }
        elseif S.tick - p.tick >= 2 then
            local placing = component.Placing.has(e) and component.Placing.getFor(e)
            if not placing or (placing.bEvaluated and placing.bBlocked) or S.simTick - p.simTick > PAINT_TIMEOUT then
                destroyEntity(e)
                S.painting[id] = nil
                results[#results + 1] = { id = id, ok = false }
            end
        end
    end
    if #results > 0 then MP.emit("painted", { results = results }) end
end

-- The tile map size, which the helper needs to find the map in the game's memory.
local function mapSize()
    local ok, size = pcall(function()
        local s = tileMapSize
        if type(s) == "function" then s = s() end
        return { x = s.x, y = s.y, z = s.z }
    end)
    if ok and type(size.x) == "number" and type(size.y) == "number" and type(size.z) == "number" then return size end
    return nil
end

-- Something we were asked to do is still running, so the digest may not show it yet.
local function busy()
    return next(S.placing) ~= nil or #S.delayed > 0 or next(S.pendingRemovals) ~= nil
        or next(S.expectPlace) ~= nil or next(S.expectRemoval) ~= nil or #S.deferred > 0
end

function MP.onTick()
    S.tick = S.tick + 1
    if not MP.paused then S.simTick = S.simTick + 1 end
    -- Lets the helper know the mod is running before it touches the tile map.
    if S.tick % 10 == 0 then MP.emit("hb", { tick = S.tick, paused = MP.paused == true }) end
    processCandidates()
    scanKnown()
    flushPending()
    runPaints()
    if not S.readyAnnounced and S.tick >= S.readyTick then
        S.readyAnnounced = true
        local count = 0
        for _ in pairs(S.known) do count = count + 1 end
        MP.emit("ready", { objects = count, seen = S.seen, hookedBy = S.installedBy, map = mapSize() })
        runDeferred()
    end
    if S.readyAnnounced and S.tick % DIGEST_TICKS == 0 then
        MP.emit("digest", { counts = S.counts, sums = S.sums, busy = busy(), msg = MP.lastMsgId })
    end
end

-- Remote messages that arrive before the loaded world is indexed wait until it is.
local function deferIfLoading(msg)
    if S.readyAnnounced then return false end
    S.deferred[#S.deferred + 1] = msg
    return true
end

MP.handlers.place = function(msg)
    if deferIfLoading(msg) then return end
    local existing = S.byKey[msg.key]
    if existing and valid(existing) then
        if component.Demolished.has(existing) then
            -- The same object is on its way out; place it again once it's gone.
            S.delayed[#S.delayed + 1] = { tick = S.tick + RETRY_TICKS, msg = msg }
            return
        end
        -- Already here (built here too, or never gone): now it's shared.
        track(msg.key)
        MP.emit("placed", { key = msg.key })
        return
    end
    if S.tick - S.lastRemoteRemoval < CLEAR_TICKS then
        -- Something was just demolished for another player, maybe on this very tile.
        S.delayed[#S.delayed + 1] = { tick = S.lastRemoteRemoval + CLEAR_TICKS, msg = msg }
        return
    end
    S.expectPlace[msg.key] = S.simTick + EXPECT_TICKS
    local e = createEntity()
    local ok, err = pcall(function()
        local placing = component.Placing.getFor(e)
        placing.at.tilePosition = uvec3(msg.x, msg.y, msg.z)
        placing.at.rotation = msg.rot
        placing.at.variantId = msg.var
        placing.bNoCost = true
        placing.bTryFinish = true
        placing.bIgnoreLandStatus = true
        placing.bIgnoreFocusAfterPlacing = true
        placing.bIgnoreInspectAfterPlacing = true
        placing.bDestroyOnBlockedFinish = true
        applyTemplate(e, msg.tpl, {}, true)
    end)
    if not ok then
        S.expectPlace[msg.key] = nil
        if valid(e) then destroyEntity(e) end
        MP.emit("blocked", { key = msg.key })
        error(err)
    end
    S.placing[msg.key] = { entity = e, msg = msg, tries = msg.tries or 0, simTick = S.simTick }
end

-- Drops replayed placements of `key` that are waiting to run.
local function cancelDelayed(key)
    local keep = {}
    for _, item in ipairs(S.delayed) do
        if item.msg.key ~= key then keep[#keep + 1] = item end
    end
    S.delayed = keep
end

-- Both go through the game's own demolition (animation and cleanup). A demolition that finishes
-- between two scans is reported as "remove", so the sender can't always tell them apart.
-- "undo" is the host taking back a build that didn't fit in its city; it works the same way.
local function demolish(msg)
    if deferIfLoading(msg) then return end
    cancelDelayed(msg.key)
    local e = S.byKey[msg.key]
    if e and valid(e) then
        if not component.Demolished.has(e) then demolishFor(msg.key, e) end
    elseif S.placing[msg.key] then
        -- Still being placed: take it out once it's in.
        S.removeAfterPlace[msg.key] = true
    else
        -- Its retry (if any) was cancelled above; nothing is on its way.
        S.expectPlace[msg.key] = nil
        S.removeAfterPlace[msg.key] = nil
    end
end
MP.handlers.demolish = demolish
MP.handlers.remove = demolish
MP.handlers.undo = demolish

-- The keys of our shared objects in one bucket (the host answering a client's repair).
MP.handlers.dump = function(msg)
    if deferIfLoading(msg) then return end
    local keys, here = {}, {}
    for key, h in pairs(S.tracked) do
        if bucketOf(h) == msg.bucket then keys[#keys + 1] = key end
    end
    -- Objects we have but don't share (the game made them, e.g. after loading): a joiner that
    -- loaded them with our save keeps them as its own too, instead of taking them out.
    for key, e in pairs(S.byKey) do
        if not S.tracked[key] and valid(e) and key:match(KEY_PATTERN) and MP.bucketOfKey(key) == msg.bucket then
            here[#here + 1] = key
        end
    end
    MP.emit("keys", { req = msg.req, bucket = msg.bucket, keys = keys, here = here })
end

-- Makes one bucket of our shared objects match the host's keys: adds what's missing, takes out
-- what the host doesn't have (unless the host has it unshared, "here"). Leaves alone anything still in flight and our player's newest builds.
MP.handlers.reconcile = function(msg)
    if deferIfLoading(msg) then return end
    local want, here = {}, {}
    for _, key in ipairs(msg.keys or {}) do want[key] = true end
    for _, key in ipairs(msg.here or {}) do here[key] = true end
    local placed, removed = 0, 0
    for key, h in pairs(S.tracked) do
        local recent = S.localAt[key] and S.tick - S.localAt[key] < RECENT_LOCAL_TICKS
        if bucketOf(h) == msg.bucket and here[key] then
            -- The host has it too, as its own: so is ours.
            untrack(key)
        elseif bucketOf(h) == msg.bucket and not want[key] and not S.expectRemoval[key] and not recent then
            local e = S.byKey[key]
            if e and valid(e) and not component.Demolished.has(e) then
                demolishFor(key, e)
            else
                S.pendingRemovals[key] = nil
                untrack(key)
            end
            removed = removed + 1
        end
    end
    for key in pairs(want) do
        if not S.tracked[key] and not S.placing[key] and not S.expectPlace[key] then
            local existing = S.byKey[key]
            if existing and valid(existing) then
                -- We have it, the game made it by itself here: it's shared after all.
                track(key)
            else
                local tpl, x, y, z, rot, var = key:match("^([%w_]+)@(%d+),(%d+),(%d+)/(%d+)/(%d+)$")
                if tpl then
                    local ok, err = pcall(MP.handlers.place, { key = key, tpl = tpl, x = tonumber(x), y = tonumber(y),
                        z = tonumber(z), rot = tonumber(rot), var = tonumber(var) })
                    if ok then placed = placed + 1
                    else MP.emit("error", { type = "reconcile", err = tostring(err) }) end
                end
            end
        end
    end
    MP.emit("reconciled", { bucket = msg.bucket, placed = placed, removed = removed })
end

-- Stop reporting removals once the level starts unloading.
onEvent("OnStartLevelLoad", function() S.leaving = true end)
setOnDestroyCallback(MP.hostEntity, function() S.leaving = true end)
