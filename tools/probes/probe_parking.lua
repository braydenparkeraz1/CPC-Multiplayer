-- One-off probe (not part of the mod): how parking and drive-thrus pay, over time.
-- Run with:  lua return loadfile("multiplayer/probe_parking.lua", "t", MP.env)()
-- The first run installs a transactMoney wrapper and starts watching for 120 game seconds; every run returns
-- what was seen since the last one (and keeps watching for another 120 s).
--
-- Every 0.5 s, for each owned car's owner: their ParkingEntryTicket (every field), the car's
-- ParkingSpaceClaim (bay key), drive-thru components and the owner's state; a change is logged. Each
-- ParkingExit's and ParkingEntry's numbers likewise. Every transactMoney call is logged with its category.
-- On a friend's game, MP.room.parkBlock = { DRIVE_THRU = true } (by category name) drops those calls.
-- probe_parking_paid.lua marks people's tickets; their group shows in the log as [A], [B] or [C].
local env = MP.env
local C = env.component
local S = MP.room
local WATCH_SECONDS = 120
local SAMPLE = 0.5

S.parkLog = S.parkLog or {}
S.parkBlock = S.parkBlock or {}
S.parkGroups = S.parkGroups or {}
S.parkCalls = S.parkCalls or {}

local function now() return env.getSimulationTime() end
local function log(s)
    if #S.parkLog < 600 then
        S.parkLog[#S.parkLog + 1] = string.format("%.1f %s", now(), s)
    elseif #S.parkLog == 600 then
        S.parkLog[#S.parkLog + 1] = "(log full)"
    end
end

local function members(v)
    local keys = {}
    local mt = getmetatable(v)
    if type(mt) == "table" then
        for k in pairs(mt) do
            local s = tostring(k)
            if not s:match("^__") and s ~= "class_cast" and s ~= "class_check" then keys[#keys + 1] = s end
        end
    end
    table.sort(keys)
    return keys
end

local function keyOf(e)
    if e == nil then return "nil" end
    local ok, v = pcall(env.valid, e)
    if not ok or not v then return "invalid(" .. tostring(e) .. ")" end
    return (S.known and S.known[e]) or ("entity " .. tostring(e))
end

-- Every non-function member of a component, entities shown by key.
local function fields(comp)
    local parts = {}
    for _, k in ipairs(members(comp)) do
        local ok, v = pcall(function() return comp[k] end)
        -- Skip engine containers (thoughts): they print as a pointer that differs on every read.
        if ok and type(v) ~= "function" and not tostring(v):match("^sol%.") then
            if type(v) == "number" and math.type(v) == "integer" and v > 1000000 and (k:match("[Ee]ntrance") or k:match("[Ss]pace") or k:match("[Ee]xit")) then
                v = keyOf(v)
            elseif type(v) == "number" and math.type(v) == "float" then
                v = string.format("%.2f", v)
            else
                v = MP.inspect(v, 1)
            end
            parts[#parts + 1] = k .. "=" .. tostring(v)
        end
    end
    return table.concat(parts, " ")
end

-- Category names, from the game's TransactionCategory table.
-- (The table may not be iterable, so known names are looked up too.)
local categoryName = {}
pcall(function()
    for name, value in pairs(env.TransactionCategory) do categoryName[value] = name end
end)
for _, name in ipairs({ "MISCELLANEOUS", "CONSTRUCTION", "REFUND", "PARKING", "DRIVE_THRU", "OIL_CONSUMPTION",
    "UPKEEP", "VEHICLE_SALES", "LAND", "LOAN", "INTEREST", "TAX", "SALARY", "SALARIES", "SHOP", "RESTROOM" }) do
    local ok, value = pcall(function() return env.TransactionCategory[name] end)
    if ok and value ~= nil and categoryName[value] == nil then categoryName[value] = name end
end

if not S.parkWrapped then
    S.parkWrapped = true
    local rawHas = rawget(env, "transactMoney") ~= nil
    local original = env.transactMoney
    env.transactMoney = function(amount, category, position, showText, ...)
        local name = categoryName[category] or tostring(category)
        S.parkCalls[name] = (S.parkCalls[name] or 0) + 1
        local where = position and string.format("%.0f,%.0f,%.0f", position.x, position.y, position.z) or "-"
        if MP.role == "client" and S.parkBlock[name] then
            log(string.format("transact BLOCKED %s %.2f at %s", name, amount, where))
            return
        end
        -- The mod's own markers are MISCELLANEOUS 500s; leave them out.
        if not (name == "MISCELLANEOUS" and math.abs(amount) == 500) then
            log(string.format("transact %s %.2f at %s text=%s", name, amount, where, tostring(showText)))
        end
        return original(amount, category, position, showText, ...)
    end
    log("wrapped transactMoney (in the room env itself: " .. tostring(rawHas) .. ")")
end

local function personState(p)
    local ok, st = pcall(function() return C.PersonFSM.getFor(p).state end)
    return ok and tostring(st) or "?"
end

local function group(name)
    local g = S.parkGroups[name]
    return g and ("[" .. g .. "] ") or ""
end

-- One snapshot: person name -> description, and lot key -> numbers.
local function snapshot()
    local people, lots = {}, {}
    for v in pairs(S.vehicles or {}) do
        if env.valid(v) and C.OwnedVehicle.has(v) then
            local p = C.OwnedVehicle.getFor(v).owner
            if p and env.valid(p) then
                local name = env.getName(p) or tostring(p)
                local parts = {}
                if C.ParkingEntryTicket.has(p) then
                    parts[#parts + 1] = "ticket{" .. fields(C.ParkingEntryTicket.getFor(p)) .. "}"
                end
                if C.ParkingSpaceClaim.has(v) then
                    parts[#parts + 1] = "claim=" .. keyOf(C.ParkingSpaceClaim.getFor(v).parkingSpace)
                end
                for _, c in ipairs({ "InDriveThru", "GoingToDriveThru", "LeavingParkWithoutPaying" }) do
                    if C[c] and (C[c].has(v) or C[c].has(p)) then parts[#parts + 1] = c end
                end
                if #parts > 0 then
                    parts[#parts + 1] = "state=" .. personState(p) .. " driver=" .. tostring(C.Driver.has(p))
                    people[name] = table.concat(parts, " ")
                end
            end
        end
    end
    for e, key in pairs(S.known or {}) do
        if env.valid(e) then
            if C.ParkingExit.has(e) then lots[key] = "exit{" .. fields(C.ParkingExit.getFor(e)) .. "}" end
            if C.ParkingEntry.has(e) then lots[key] = "entry{" .. fields(C.ParkingEntry.getFor(e)) .. "}" end
        end
    end
    return people, lots
end

-- Descriptions hold no positions, so only real changes are logged.
local function compare(old, new, label)
    for k, v in pairs(new) do
        if old[k] ~= v then log(label(k) .. (old[k] and "" or "NEW ") .. v) end
    end
    for k in pairs(old) do
        if new[k] == nil then log(label(k) .. "GONE") end
    end
end

local function sample()
    local people, lots = snapshot()
    compare(S.parkPeople or {}, people, function(name) return group(name) .. name .. ": " end)
    compare(S.parkLots or {}, lots, function(key) return key .. ": " end)
    S.parkPeople, S.parkLots = people, lots
end

-- A newer run of this file replaces the loop (and its code).
S.parkGen = (S.parkGen or 0) + 1
local gen = S.parkGen

local function watch()
    if gen ~= S.parkGen then return end
    if now() > (S.parkUntil or 0) then
        S.parkWatching = false
        log("stopped watching")
        return
    end
    local ok, err = pcall(sample)
    if not ok then log("sample error: " .. tostring(err)) end
    env.setTimeout(MP.hostEntity, SAMPLE, watch)
end

S.parkUntil = now() + WATCH_SECONDS
do
    S.parkWatching = true
    S.parkPeople, S.parkLots = nil, nil
    -- First snapshot in full.
    local people, lots = snapshot()
    local n = 0
    for name, v in pairs(people) do n = n + 1; log("start " .. group(name) .. name .. ": " .. v) end
    for key, v in pairs(lots) do log("start " .. key .. ": " .. v) end
    local bays, claimed = 0, 0
    for e in pairs(S.known or {}) do
        if env.valid(e) and C.ParkingSpace.has(e) then
            bays = bays + 1
            if C.ClaimedParkingSpace.has(e) then claimed = claimed + 1 end
        end
    end
    log(string.format("role=%s: %d owners with a ticket/claim; %d keyed parking bays (%d claimed)", tostring(MP.role), n, bays, claimed))
    S.parkPeople, S.parkLots = people, lots
    env.setTimeout(MP.hostEntity, SAMPLE, watch)
end

local calls = {}
for name, n in pairs(S.parkCalls) do calls[#calls + 1] = name .. "=" .. n end
table.sort(calls)
local out = "[parking] calls: " .. table.concat(calls, " ") .. "\n" .. table.concat(S.parkLog, "\n")
S.parkLog = {}
return out
