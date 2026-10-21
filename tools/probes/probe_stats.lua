-- One-off probe (not part of the mod): averages of every person's stats, and who is in town where.
-- Run with:  lua return loadfile("multiplayer/probe_stats.lua", "t", MP.env)()
local env = MP.env
local C = env.component
local out = {}
local function add(s) out[#out + 1] = s end

local statFields = { "happiness", "carbrained", "buyACar", "amused", "cleanliness", "drinks", "energy", "food", "fuel",
    "health", "maintenance", "paint", "parking", "propaganda", "restroom" }
local sums, n = {}, 0
local states = {}
local inTown = {}
local vehicles = {}
local misses, maxId = 0, 0
for i = 0, 3000000 do
    local ok, v = pcall(env.valid, i)
    if ok and v then
        misses = 0; maxId = i
        if C.Person.has(i) then
            n = n + 1
            local st = C.PersonStats.getFor(i)
            for _, f in ipairs(statFields) do
                local okf, x = pcall(function() return st[f] end)
                if okf and type(x) == "number" then sums[f] = (sums[f] or 0) + x end
            end
            local s = C.PersonFSM.getFor(i).state
            states[s] = (states[s] or 0) + 1
            if C.Transform.has(i) then
                local p = C.Transform.getFor(i).position
                inTown[#inTown + 1] = string.format("%s|%d|%.1f,%.1f,%.1f", tostring(env.getName(i)), s, p.x, p.y, p.z)
            end
        elseif C.OwnedVehicle.has(i) and C.Transform.has(i) then
            local p = C.Transform.getFor(i).position
            vehicles[#vehicles + 1] = string.format("%s|%.1f,%.1f,%.1f", tostring(env.getName(i)), p.x, p.y, p.z)
        end
    else
        misses = misses + 1
        if misses > 300000 then break end
    end
end
add("role=" .. tostring(MP.role) .. " time=" .. tostring(env.getSimulationTime()) .. " persons=" .. n .. " maxId=" .. maxId)
local avg = {}
for _, f in ipairs(statFields) do
    if sums[f] then avg[#avg + 1] = string.format("%s=%.4f", f, sums[f] / math.max(n, 1)) end
end
add("averages: " .. table.concat(avg, " "))
local st = {}
for s, c in pairs(states) do st[#st + 1] = s .. ":" .. c end
table.sort(st)
add("states: " .. table.concat(st, " "))
table.sort(inTown)
table.sort(vehicles)
add("in town (" .. #inTown .. "): " .. table.concat(inTown, "; "))
add("vehicles placed (" .. #vehicles .. "): " .. table.concat(vehicles, "; "))
return table.concat(out, "\n")
