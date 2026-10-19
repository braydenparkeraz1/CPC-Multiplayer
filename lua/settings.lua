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
local PROTECT_TICKS = 50       -- a local edit isn't overwritten by an older host value for ~5 s
local PENDING_TICKS = 300      -- values for objects that don't exist here yet wait ~30 s for them

S.setLast = S.setLast or {}          -- key -> { ["Component.field"] = value } as last seen or applied
S.setList = S.setList or {}          -- tracked keys, scanned round-robin
S.setPos = S.setPos or 1
S.setTracked = S.setTracked or {}    -- key -> true
S.setPending = S.setPending or {}    -- key -> { fields = {...}, untilTick = n }
S.setProtect = S.setProtect or {}    -- key -> { tick = n, fields = {...} } (our own recent edits)

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

-- Our own edit wins for a few seconds over an incoming value, until the host sends it back.
local function protected(key, name, value)
    local protect = S.setProtect[key]
    if not protect then return false end
    if S.tick - protect.tick >= PROTECT_TICKS then
        S.setProtect[key] = nil
        return false
    end
    local mine = protect.fields[name]
    if mine == nil then return false end
    if same(mine, value) then
        -- The host has our edit now.
        protect.fields[name] = nil
        return false
    end
    return true
end

-- Applies incoming values to one object; returns true if it exists here.
local function applyTo(key, fields)
    local e = S.byKey[key]
    if not (e and valid(e)) then return false end
    local last = S.setLast[key] or {}
    S.setLast[key] = last
    local current = read(e) or {}
    for name, value in pairs(fields) do
        if type(value) == "number" and not protected(key, name, value) then
            if not same(current[name], value) then
                -- Recorded first, so the next scan doesn't report it back as a local change.
                last[name] = value
                write(e, name, value)
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
        applyTo(key, pending.fields)
    end
end

local function untrack(key)
    S.setTracked[key] = nil
    S.setLast[key] = nil
    S.setPending[key] = nil
    S.setProtect[key] = nil
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
                local changed
                for name, value in pairs(now) do
                    if not same(last[name], value) then
                        changed = changed or {}
                        changed[name] = value
                    end
                end
                S.setLast[key] = now
                if changed then
                    local protect = S.setProtect[key] or { fields = {} }
                    protect.tick = S.tick
                    for name, value in pairs(changed) do protect.fields[name] = value end
                    S.setProtect[key] = protect
                    MP.emit("setting", { key = key, fields = changed })
                end
            end
        end
    end
end

local function applyPending()
    for key, pending in pairs(S.setPending) do
        if S.simTick > pending.untilTick then
            S.setPending[key] = nil
        elseif S.byKey[key] and valid(S.byKey[key]) then
            S.setPending[key] = nil
            applyTo(key, pending.fields)
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

-- { entries = { { key = ..., fields = { ["Component.field"] = value } }, ... }, full = bool }
MP.handlers.settings = function(msg)
    for _, entry in ipairs(msg.entries or {}) do
        if type(entry.key) == "string" and type(entry.fields) == "table" then
            if not S.readyAnnounced or not applyTo(entry.key, entry.fields) then
                -- Not loaded yet, or a friend's object that hasn't been placed here yet.
                S.setPending[entry.key] = { fields = entry.fields, untilTick = S.simTick + PENDING_TICKS }
            end
        end
    end
end

MP.handlers["settings?"] = function()
    MP.emit("settings", { entries = snapshot(), full = true })
end

MP.settingsTick = function()
    if not S.readyAnnounced then return end
    scan()
    applyPending()
end
