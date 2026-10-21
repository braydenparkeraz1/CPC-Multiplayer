-- One-off probe (not part of the mod): what Lua can see of cars, people, protests and city stats.
-- Run in the helper window:  lua return loadfile("multiplayer/probe_agents.lua", "t", MP.env)()
-- Writes multiplayer/runtime/probe_<role>_<session>.txt (or returns the text if files can't be written).
local env = MP.env
local C = env.component
local out = {}
local function add(s) out[#out + 1] = s end
local function try(name, fn)
    local ok, r = pcall(fn)
    add("## " .. name .. ": " .. (ok and tostring(r) or ("ERROR " .. tostring(r))))
end

-- Member names of a sol usertype value or table (pairs doesn't work on usertypes).
local function members(v)
    local keys, seen = {}, {}
    local function addKeys(t, prefix)
        if type(t) ~= "table" then return end
        for k in pairs(t) do
            local s = prefix .. tostring(k)
            if not seen[s] then seen[s] = true; keys[#keys + 1] = s end
        end
    end
    if type(v) == "table" then addKeys(v, "") end
    local mt = getmetatable(v)
    if type(mt) == "table" then
        addKeys(mt, "mt.")
        if type(mt.__index) == "table" then addKeys(mt.__index, "") end
    end
    table.sort(keys)
    return table.concat(keys, ", ")
end

-- Reads the given fields of a component, skipping the ones that don't exist.
local function fields(comp, names)
    local parts = {}
    for _, f in ipairs(names) do
        local ok, v = pcall(function() return comp[f] end)
        if ok and v ~= nil then parts[#parts + 1] = f .. "=" .. MP.inspect(v, 2) end
    end
    return table.concat(parts, ", ")
end

local function dumpComp(e, name, names)
    local cc = C[name]
    if not cc or not cc.has(e) then return name .. ": (none)" end
    local comp = cc.getFor(e)
    return name .. ": {" .. members(comp) .. "} " .. (names and fields(comp, names) or "")
end

add("role=" .. tostring(MP.role) .. " context=" .. tostring(MP.context) .. " session=" .. tostring(MP.sessionArg))
try("entity id type", function() return type(MP.hostEntity) .. " " .. tostring(MP.hostEntity) end)
try("simulation time", function() return table.concat({ tostring(env.getSimulationTime and env.getSimulationTime()), tostring(env.getDayTime and env.getDayTime()), tostring(env.DAY_LENGTH), tostring(env.DAYTIME_LENGTH) }, " ") end)

for _, pat in ipairs({ "[Ss]pawn", "[Pp]erson", "[Pp]eople", "[Cc]itizen", "[Vv]ehicle", "[Cc]ar[A-Z]", "[Pp]rotest", "[Rr]iot",
    "[Ss]tat", "[Uu]nrest", "[Cc]arbrain", "[Dd]ependen", "[Pp]opulation", "[Ee]ntit", "[Ee]ach", "[Vv]iew", "[Qq]uery",
    "[Ff]ind", "[Ss]imulat", "[Tt]ime", "[Pp]ause", "[Ss]peed", "[Nn]ame", "[Ss]ystem", "[Ee]vent", "[Ff]sm", "[Ss]tate",
    "[Pp]ath", "[Ss]teer", "[Tt]eleport", "[Dd]espawn", "[Rr]egistry" }) do
    try("functions " .. pat, function() return table.concat(MP.findFunctions(pat), ", ") end)
end
try("system", function() return MP.inspect(env.system, 2) end)
try("system members", function() return members(env.system) end)
try("scenario", function() return members(env.scenario) end)
try("PersonState", function() return MP.inspect(env.PersonState, 1) .. " / " .. members(env.PersonState) end)
try("VehicleType", function() return members(env.VehicleType) end)
try("component.Person table", function() return members(C.Person) end)

-- Every live entity: entt ids with version 0 are plain small numbers; a long run of misses ends the scan.
local comps = { "Person", "PersonFSM", "PersonStats", "Resident", "Driver", "VehiclePassenger", "VehicleOwner",
    "Protesting", "ProtestLeader", "InRiotFight", "OwnedVehicle", "VehiclePassengers", "VehicleWreck", "Bus",
    "GarageVehicle", "PathFinding", "SimpleSteering", "DespawnAfter", "Persistent", "Placeable", "Placed",
    "ParkingSpaceClaim", "InDriveThru", "InStorage", "Fire", "HP" }
local counts, first, templates, maxId, live = {}, {}, {}, 0, 0
try("scan", function()
    local valid = env.valid
    local misses = 0
    local firstErr
    for i = 0, 3000000 do
        local ok, v = pcall(valid, i)
        if not ok then firstErr = firstErr or tostring(v) end
        if ok and v then
            live = live + 1; maxId = i; misses = 0
            for _, c in ipairs(comps) do
                local cc = C[c]
                if cc then
                    local okh, has = pcall(cc.has, i)
                    if okh and has then counts[c] = (counts[c] or 0) + 1; first[c] = first[c] or i end
                end
            end
            if C.Persistent.has(i) and not C.Placeable.has(i) then
                local tpl = C.Persistent.getFor(i).applyTemplateOnLoad
                templates[tostring(tpl)] = (templates[tostring(tpl)] or 0) + 1
            end
        else
            misses = misses + 1
            if misses > 300000 then break end
        end
    end
    local parts = {}
    for _, c in ipairs(comps) do parts[#parts + 1] = c .. "=" .. tostring(counts[c] or 0) end
    return "live " .. live .. ", highest id " .. maxId .. (firstErr and (", valid() error: " .. firstErr) or "") .. "\n  " .. table.concat(parts, ", ")
end)
try("non-placeable templates", function()
    local list = {}
    for k, n in pairs(templates) do list[#list + 1] = { k, n } end
    table.sort(list, function(a, b) return a[2] > b[2] end)
    local parts = {}
    for i = 1, math.min(#list, 60) do parts[#parts + 1] = list[i][1] .. "=" .. list[i][2] end
    return table.concat(parts, ", ")
end)

local function sample(label, e)
    if not e then add("## sample " .. label .. ": none"); return end
    add("## sample " .. label .. ": entity " .. tostring(e) .. " name=" .. tostring(env.getName and env.getName(e)))
    for _, spec in ipairs({
        { "Persistent", { "applyTemplateOnLoad" } },
        { "Transform", { "position", "rotation", "scale" } },
        { "Person", { "age", "walkSpeed", "state", "home", "vehicle" } },
        { "PersonStats", { "happiness", "carbrained", "buyACar", "anger", "frustration", "patience" } },
        { "PersonFSM", { "state", "currentState", "previousState", "timeInState" } },
        { "Driver", { "vehicle" } },
        { "VehicleOwner", { "vehicle" } },
        { "VehiclePassenger", { "vehicle" } },
        { "OwnedVehicle", { "owner" } },
        { "VehiclePassengers", { "passengers", "driver" } },
        { "Protesting", { "protest", "target", "leader" } },
        { "PathFinding", { "goal", "path" } },
        { "SimpleSteering", { "speed", "velocity", "target" } },
        { "Resident", { "house", "home" } },
    }) do
        local ok, r = pcall(dumpComp, e, spec[1], spec[2])
        add("   " .. (ok and r or (spec[1] .. ": ERROR " .. tostring(r))))
    end
end
sample("person", first.Person)
sample("driver", first.Driver)
sample("protester", first.Protesting)
sample("vehicle", first.OwnedVehicle or first.VehiclePassengers)

local text = table.concat(out, "\n")
local path = MP.ROOT .. "runtime/probe_" .. tostring(MP.role) .. "_" .. tostring(MP.sessionArg) .. ".txt"
local okIo, f = pcall(function() return io and io.open(path, "w") end)
if okIo and f then
    f:write(text)
    f:close()
    return "probe written to " .. path .. " (" .. #text .. " chars, " .. live .. " entities)"
end
return text
