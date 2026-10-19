-- Car Park Capital Multiplayer - object sync.
-- Detects objects the local player places or demolishes and reports them to the helper,
-- and replays objects placed or demolished by other players.
-- Objects are matched between PCs by a key built from template name, tile position, rotation and variant,
-- because entity ids differ on every machine.
local MP = ...

local S = MP.room

local BASELINE_TICKS = 20      -- objects that exist in the first 2s are the loaded world, not new builds
local GHOST_WINDOW = 20        -- a placement counts as the player's if their build ghost was seen in the last 2s
local REMOVE_DELAY = 30        -- wait 3s before reporting a vanished object, in case the same object reappears
local SCAN_PER_TICK = 800      -- known objects re-checked per tick, so a demolition is seen before it finishes
local EXPECT_TICKS = 100       -- how long a replayed placement may take to appear
local CLEAR_TICKS = 10         -- replayed placements wait this long after a replayed demolition, so the tile is free
local MAX_PLACE_TRIES = 4      -- a blocked replayed placement is retried this many times...
local RETRY_TICKS = 15         -- ...this far apart, long enough for a late removal to arrive

S.known = S.known or {}            -- entity -> key
S.byKey = S.byKey or {}            -- key -> entity
S.scanList = S.scanList or {}
S.scanPos = S.scanPos or 1
S.ghosts = S.ghosts or {}          -- templateIndex -> tick a player ghost was last seen
S.expectPlace = S.expectPlace or {}
S.expectRemoval = S.expectRemoval or {}
S.removeAfterPlace = S.removeAfterPlace or {}
S.pendingRemovals = S.pendingRemovals or {}   -- key -> tick the object vanished
S.deferred = S.deferred or {}
S.delayed = S.delayed or {}                   -- { tick = <run at>, msg = <place message> }
S.placing = S.placing or {}                   -- key -> replayed placement in progress
S.lastRemoteRemoval = S.lastRemoteRemoval or -CLEAR_TICKS
S.tick = S.tick or 0
-- Ticks counted only while the game runs. Timeouts that wait for the game to finish something
-- (a replayed placement, a repaint) use these, so a long pause doesn't make them give up.
S.simTick = S.simTick or 0
S.readyTick = S.readyTick or (S.tick + BASELINE_TICKS)

local function keyFor(tpl, x, y, z, rot, var)
    return string.format("%s@%d,%d,%d/%d/%d", tpl, x, y, z, rot, var)
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
    if MP.onIndexed then MP.onIndexed(e, key) end
end

local function unindex(e)
    local key = S.known[e]
    S.known[e] = nil
    if key and S.byKey[key] == e then
        S.byKey[key] = nil
        if MP.onUnindexed then MP.onUnindexed(key) end
    end
    return key
end

-- Removals wait a moment before being reported; a new placement must not overtake them,
-- or other players would try to place it while the old object still blocks the tile.
local function flushRemovals(olderThan)
    for key, tick in pairs(S.pendingRemovals) do
        if S.tick - tick >= olderThan then
            S.pendingRemovals[key] = nil
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
                    elseif S.expectPlace[key] then
                        S.expectPlace[key] = nil
                        -- Its removal arrived while the placement was still finishing.
                        if S.removeAfterPlace[key] then
                            S.removeAfterPlace[key] = nil
                            S.expectRemoval[key] = true
                            component.Demolished.getFor(e)
                        end
                    elseif byPlayer or isPlayerPlacement(e) then
                        flushRemovals(0)
                        info.key = key
                        MP.emit("place", info)
                    end
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
            list[S.scanPos] = list[#list]
            list[#list] = nil
            if key and not S.leaving then
                if S.expectRemoval[key] then
                    S.expectRemoval[key] = nil
                elseif demolished then
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
        elseif not valid(p.entity) then
            S.placing[key] = nil
            if S.removeAfterPlace[key] then
                S.removeAfterPlace[key] = nil
                S.expectPlace[key] = nil
            elseif p.tries < MAX_PLACE_TRIES then
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

function MP.onTick()
    S.tick = S.tick + 1
    if not MP.paused then S.simTick = S.simTick + 1 end
    -- Lets the helper know the mod is running before it touches the tile map.
    if S.tick % 10 == 0 then MP.emit("hb", { tick = S.tick, paused = MP.paused == true }) end
    processCandidates()
    scanKnown()
    flushPending()
    runPaints()
    if MP.settingsTick then
        local ok, err = pcall(MP.settingsTick)
        if not ok then MP.log("settings error: " .. tostring(err)) end
    end
    if not S.readyAnnounced and S.tick >= S.readyTick then
        S.readyAnnounced = true
        local count = 0
        for _ in pairs(S.known) do count = count + 1 end
        MP.emit("ready", { objects = count, seen = S.seen, hookedBy = S.installedBy, map = mapSize() })
        runDeferred()
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
    if existing and valid(existing) then return end
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
        error(err)
    end
    S.placing[msg.key] = { entity = e, msg = msg, tries = msg.tries or 0 }
end

-- Both go through the game's own demolition (animation and cleanup). A demolition that finishes
-- between two scans is reported as "remove", so the sender can't always tell them apart.
local function demolish(msg)
    if deferIfLoading(msg) then return end
    local e = S.byKey[msg.key]
    if e and valid(e) then
        if not component.Demolished.has(e) then
            S.expectRemoval[msg.key] = true
            S.lastRemoteRemoval = S.tick
            component.Demolished.getFor(e)
        end
    elseif S.expectPlace[msg.key] then
        S.removeAfterPlace[msg.key] = true
    end
end
MP.handlers.demolish = demolish
MP.handlers.remove = demolish

-- Stop reporting removals once the level starts unloading.
onEvent("OnStartLevelLoad", function() S.leaving = true end)
setOnDestroyCallback(MP.hostEntity, function() S.leaving = true end)
