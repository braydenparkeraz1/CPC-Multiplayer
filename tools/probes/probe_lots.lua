-- One-off probe (not part of the mod): vehicles, parking, drive-thrus and protests.
-- Run with:  lua return loadfile("multiplayer/probe_lots.lua", "t", MP.env)()
local env = MP.env
local C = env.component
local out = {}
local function add(s) out[#out + 1] = s end

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

local function dump(e, name)
    local cc = C[name]
    if not cc then return name .. ": (no such component)" end
    local okh, has = pcall(cc.has, e)
    if not okh or not has then return nil end
    local comp = cc.getFor(e)
    local parts = {}
    for _, k in ipairs(members(comp)) do
        local ok, v = pcall(function() return comp[k] end)
        if ok and type(v) ~= "function" then parts[#parts + 1] = k .. "=" .. MP.inspect(v, 1) end
    end
    return name .. " {" .. table.concat(parts, ", ") .. "}"
end

local watch = { "Vehicle", "OwnedVehicle", "VehiclePassengers", "Driver", "VehicleOwner", "Persistent", "ParkingEntryTicket",
    "ParkingSpaceClaim", "ClaimedParkingSpace", "InDriveThru", "GoingToDriveThru", "InQueue", "Protesting", "ProtestLeader",
    "ProtestBanner", "ProtestBlockingTarget", "ShouldStartProtest", "RiotFight", "ParkingEntry", "ParkingExit", "ParkingSpace",
    "DriveThru", "VehicleShop", "LeavingParkWithoutPaying", "Park", "FindPark", "DespawnAfter" }
local counts, firstOf, lists = {}, {}, {}
local ownerCarb, owners, inTownCarb, inTown = 0, 0, 0, 0
local misses = 0
for i = 0, 3000000 do
    local ok, v = pcall(env.valid, i)
    if ok and v then
        misses = 0
        for _, c in ipairs(watch) do
            local cc = C[c]
            if cc then
                local okh, has = pcall(cc.has, i)
                if okh and has then
                    counts[c] = (counts[c] or 0) + 1
                    firstOf[c] = firstOf[c] or i
                    lists[c] = lists[c] or {}
                    if #lists[c] < 400 then lists[c][#lists[c] + 1] = i end
                end
            end
        end
        if C.Person.has(i) then
            local cb = C.PersonStats.getFor(i).carbrained
            if C.VehicleOwner.has(i) then owners = owners + 1; ownerCarb = ownerCarb + cb end
            if C.Transform.has(i) then inTown = inTown + 1; inTownCarb = inTownCarb + cb end
        end
    else
        misses = misses + 1
        if misses > 300000 then break end
    end
end
add("role=" .. tostring(MP.role) .. " time=" .. tostring(env.getSimulationTime()))
local parts = {}
for _, c in ipairs(watch) do parts[#parts + 1] = c .. "=" .. tostring(counts[c] or 0) end
add("counts: " .. table.concat(parts, " "))
add(string.format("carbrained: car owners %d avg %.4f; in town %d avg %.4f", owners, ownerCarb / math.max(owners, 1), inTown, inTownCarb / math.max(inTown, 1)))

-- Vehicles that aren't anyone's own car: visitors?
local visitors, sampleVisitor = 0, nil
for _, e in ipairs(lists.Vehicle or {}) do
    if not C.OwnedVehicle.has(e) then
        visitors = visitors + 1
        sampleVisitor = sampleVisitor or e
    end
end
add("vehicles not owned: " .. visitors)

local function sample(label, e)
    if not e then add("== " .. label .. ": none"); return end
    add("== " .. label .. ": entity " .. tostring(e) .. " name=" .. tostring(env.getName(e)))
    for _, c in ipairs({ "Persistent", "Transform", "Vehicle", "VehiclePassengers", "OwnedVehicle", "ParkingEntryTicket",
        "ParkingSpaceClaim", "InDriveThru", "GoingToDriveThru", "InQueue", "Protesting", "ProtestLeader", "ProtestBanner",
        "ProtestBlockingTarget", "ParkingEntry", "ParkingExit", "DriveThru", "Driver", "VehicleOwner", "PersonFSM",
        "DespawnAfter", "VehiclePathSteering", "Park", "FindPark" }) do
        local ok, r = pcall(dump, e, c)
        if ok and r then add("   " .. r) elseif not ok then add("   " .. c .. ": ERROR " .. tostring(r)) end
    end
end
sample("visiting vehicle", sampleVisitor)
sample("owned vehicle", firstOf.OwnedVehicle)
sample("ticket holder", firstOf.ParkingEntryTicket)
sample("in drive-thru", firstOf.InDriveThru)
sample("protester", firstOf.Protesting)
sample("protest banner", firstOf.ProtestBanner)
sample("parking entry", firstOf.ParkingEntry)
sample("parking exit", firstOf.ParkingExit)
sample("drive-thru", firstOf.DriveThru)
return table.concat(out, "\n")
