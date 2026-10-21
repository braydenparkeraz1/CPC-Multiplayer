-- Car Park Capital Multiplayer - player-set values (prices) sync.
-- The host's game holds the true values. Local changes are reported as "setting"; the helper sends
-- the host's values back as inbox "settings", which we apply where they differ.
-- See docs/api-findings.md for where these values live.
local MP = ...

local S = MP.room

-- Only these fields are synced. ParkingEntry.ratePerMinute is verified in game (the UI writes
-- every entrance; each stores its own value). Pricing.price (shops, drive-thrus, restrooms) is
-- the same kind of field but not tried in game yet.
local WHITELIST = {
    { component = "ParkingEntry", fields = { "ratePerMinute" } },
    { component = "Pricing", fields = { "price" } },
}

local SCAN_PER_TICK = 200      -- tracked objects re-read per tick for local changes
local PENDING_TICKS = 300      -- values for objects that don't exist here yet wait ~30 s for them
-- After we write a value, a change this soon is the game's doing, not our player's: with "apply to all"
-- ticked for that kind of price, the game copies its one stored price (money.syncedPrices) back onto
-- every object of the kind. Reported as "settingrevert", never as the player's edit.
local REVERT_TICKS = 5

S.setLast = S.setLast or {}          -- key -> { ["Component.field"] = value } as last seen or applied
S.setList = S.setList or {}          -- tracked keys, scanned round-robin
S.setPos = S.setPos or 1
S.setTracked = S.setTracked or {}    -- key -> true
S.setPending = S.setPending or {}    -- key -> { fields = {...}, untilTick = n }
S.setProtect = S.setProtect or {}    -- key -> { seq = n, fields = {...} } (our own recent edits)
S.setSeq = S.setSeq or 0             -- numbers our "setting" reports; the helper echoes the newest as "seen"
S.setApplied = S.setApplied or {}    -- key -> { ["Component.field"] = { value = v, tick = n } } we wrote lately
S.pricingNames = S.pricingNames or {} -- Pricing.syncId -> its name ("restroom", "fries_stall", ...)

-- Float fields come back as float32 values, so compare loosely.
local function same(a, b)
    if a == b then return true end
    if type(a) ~= "number" or type(b) ~= "number" then return false end
    return math.abs(a - b) <= 1e-6 * math.max(1, math.abs(a), math.abs(b))
end

-- Reads the whitelisted fields of one entity; nil if it has none.
local function read(e)
    local values
    for _, w in ipairs(WHITELIST) do
        local comp = component[w.component]
        if comp and comp.has(e) then
            local ok, c = pcall(comp.getFor, e)
            if ok and c then
                for _, f in ipairs(w.fields) do
                    local okField, v = pcall(function() return c[f] end)
                    if okField and type(v) == "number" then
                        values = values or {}
                        values[w.component .. "." .. f] = v
                    end
                end
            end
        end
    end
    return values
end

-- Writes one "Component.field" value. Checks has() first: getFor would add the component.
local function write(e, name, value)
    local cname, field = name:match("^([%w_]+)%.([%w_]+)$")
    if not cname then return false end
    local allowed = false
    for _, w in ipairs(WHITELIST) do
        if w.component == cname then
            for _, f in ipairs(w.fields) do if f == field then allowed = true end end
        end
    end
    local comp = allowed and component[cname]
    if not comp or not comp.has(e) then return false end
    return (pcall(function() comp.getFor(e)[field] = value end))
end

-- The kind of price an object has, as the game names it in money.syncedPrices: "parking" for
-- entrances, and the name behind Pricing.syncId (from the helper's "pricingids") for the rest.
local function priceType(e)
    if component.ParkingEntry and component.ParkingEntry.has(e) then return "parking" end
    if component.Pricing and component.Pricing.has(e) then
        local ok, id = pcall(function() return component.Pricing.getFor(e).syncId end)
        if ok and id ~= nil then return S.pricingNames[id] or ("id" .. tostring(id)) end
    end
    return nil
end

-- A value the helper wrote before it had our own later edit of that field ("seen" is the newest
-- of our reports it had). It's older than the edit, so the edit stays. Anything written after
-- that is newer than the edit and applies, even while the edit is fresh.
local function stale(key, name, seen)
    local protect = S.setProtect[key]
    return protect ~= nil and protect.fields[name] ~= nil and seen < protect.seq
end

-- Applies incoming values to one object; returns true if it exists here.
local function applyTo(key, fields, seen)
    local e = S.byKey[key]
    if not (e and valid(e)) then return false end
    local last = S.setLast[key] or {}
    S.setLast[key] = last
    local current = read(e) or {}
    for name, value in pairs(fields) do
        if type(value) == "number" and stale(key, name, seen) then
            if not same(current[name], value) then
                MP.trace("price kept", { key = key, field = name, ours = current[name], theirs = value, seen = seen })
            end
        elseif type(value) == "number" then
            if not same(current[name], value) then
                -- Recorded first, so the next scan doesn't report it back as a local change.
                last[name] = value
                local applied = S.setApplied[key] or {}
                applied[name] = { value = value, tick = S.tick }
                S.setApplied[key] = applied
                local ok = write(e, name, value)
                MP.trace("price applied", { key = key, field = name, from = current[name], to = value, ok = ok })
            else
                last[name] = current[name]
            end
        end
    end
    return true
end

local function track(e, key)
    local values = read(e)
    if not values then return end
    if not S.setTracked[key] then
        S.setTracked[key] = true
        S.setList[#S.setList + 1] = key
    end
    S.setLast[key] = values
    -- A friend's new object whose price arrived before the object did.
    local pending = S.setPending[key]
    if pending then
        S.setPending[key] = nil
        applyTo(key, pending.fields, pending.seen)
    end
end

local function untrack(key)
    S.setTracked[key] = nil
    S.setLast[key] = nil
    S.setPending[key] = nil
    S.setProtect[key] = nil
    S.setApplied[key] = nil
end

-- Called by sync.lua as objects get indexed and dropped.
MP.onIndexed = function(e, key) pcall(track, e, key) end
MP.onUnindexed = function(key) untrack(key) end

-- Objects indexed before this module loaded.
for e, key in pairs(S.known) do pcall(track, e, key) end

local function scan()
    local list = S.setList
    local budget = math.min(SCAN_PER_TICK, #list)
    for _ = 1, budget do
        if S.setPos > #list then S.setPos = 1 end
        local key = list[S.setPos]
        local e = S.setTracked[key] and S.byKey[key]
        if not (e and valid(e)) then
            -- Demolished or removed (sync.lua untracks it) or gone some other way.
            list[S.setPos] = list[#list]
            list[#list] = nil
            if S.setTracked[key] then untrack(key) end
        else
            S.setPos = S.setPos + 1
            local now = read(e)
            if now then
                local last = S.setLast[key] or {}
                local applied = S.setApplied[key]
                local changed
                for name, value in pairs(now) do
                    local mine = applied and applied[name]
                    if mine and S.tick - mine.tick > REVERT_TICKS then
                        applied[name] = nil
                        mine = nil
                    end
                    if not same(last[name], value) then
                        if mine and same(value, mine.value) then
                            -- What we wrote, taking effect late (e.g. the helper changed the game's stored price).
                        elseif mine then
                            -- The game put back its own value: not our player's edit.
                            local kind = priceType(e)
                            MP.trace("price undone by the game", { key = key, field = name, ours = mine.value, game = value, kind = kind })
                            MP.emit("settingrevert", { key = key, field = name, value = value, wanted = mine.value, kind = kind })
                        else
                            changed = changed or {}
                            changed[name] = value
                        end
                    end
                end
                S.setLast[key] = now
                if changed then
                    S.setSeq = S.setSeq + 1
                    local protect = S.setProtect[key] or { fields = {} }
                    protect.seq = S.setSeq
                    for name, value in pairs(changed) do protect.fields[name] = value end
                    S.setProtect[key] = protect
                    MP.emit("setting", { key = key, fields = changed, seq = S.setSeq })
                end
            end
        end
    end
end

local function applyPending()
    for key, pending in pairs(S.setPending) do
        if S.simTick > pending.untilTick then
            MP.trace("price dropped", { key = key, why = "the object never showed up here" })
            S.setPending[key] = nil
        elseif S.byKey[key] and valid(S.byKey[key]) then
            S.setPending[key] = nil
            applyTo(key, pending.fields, pending.seen)
        end
    end
end

local function snapshot()
    local entries = {}
    for key in pairs(S.setTracked) do
        local e = S.byKey[key]
        local values = e and valid(e) and read(e)
        if values then entries[#entries + 1] = { key = key, fields = values } end
    end
    return entries
end

-- { entries = { { key = ..., fields = { ["Component.field"] = value } }, ... }, full = bool, seen = n }
MP.handlers.settings = function(msg)
    local seen = type(msg.seen) == "number" and msg.seen or 0
    for _, entry in ipairs(msg.entries or {}) do
        if type(entry.key) == "string" and type(entry.fields) == "table" then
            if not S.readyAnnounced or not applyTo(entry.key, entry.fields, seen) then
                -- Not loaded yet, or a friend's object that hasn't been placed here yet.
                if not S.setPending[entry.key] then MP.trace("price waits", { key = entry.key, why = "object not here yet" }) end
                S.setPending[entry.key] ={ fields = entry.fields, seen = seen, untilTick = S.simTick + PENDING_TICKS }
            end
        end
    end
    -- The helper has these edits, so nothing it writes from now on is older than them.
    for key, protect in pairs(S.setProtect) do
        if protect.seq <= seen then S.setProtect[key] = nil end
    end
end

MP.handlers["settings?"] = function()
    MP.emit("settings", { entries = snapshot(), full = true })
end

-- { names = { "restroom", "soda_vending", ... } }: the price kinds the helper found in the game's scripts.
MP.handlers.pricingids = function(msg)
    local lookup = getPricingId
    if type(lookup) ~= "function" then return end
    for _, name in ipairs(msg.names or {}) do
        if type(name) == "string" then
            local ok, id = pcall(lookup, name)
            if ok and id ~= nil then S.pricingNames[id] = name end
        end
    end
end

-- { req = n, kind = "parking", field = "ParkingEntry.ratePerMinute" }: every object of one kind's value,
-- so the helper can tell whether a change it made to the game's stored price took.
MP.handlers["typevalue?"] = function(msg)
    local count, lo, hi = 0, nil, nil
    for key in pairs(S.setTracked) do
        local e = S.byKey[key]
        if e and valid(e) and priceType(e) == msg.kind then
            local values = read(e)
            local v = values and values[msg.field]
            if type(v) == "number" then
                count = count + 1
                lo = lo and math.min(lo, v) or v
                hi = hi and math.max(hi, v) or v
            end
        end
    end
    MP.emit("typevalue", { req = msg.req, kind = msg.kind, count = count, min = lo, max = hi })
end

MP.settingsTick = function()
    if not S.readyAnnounced then return end
    scan()
    applyPending()
end
