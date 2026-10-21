-- One-off probe: entities including reused slots (id = index + version * 2^20), protests and cars.
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
    if not cc then return nil end
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
local watch = { "Person", "Vehicle", "OwnedVehicle", "VehicleOwner", "Protesting", "ProtestLeader", "ProtestBanner",
    "ProtestBlockingTarget", "ProtestBlockTarget", "ProtestBlockedTarget", "ProtestFireStarter", "ShouldStartProtest",
    "RiotFight", "RiotUnit", "InRiotFight", "ParkingEntryTicket", "InDriveThru", "Persistent", "Placeable" }
local counts, first, tpl = {}, {}, {}
local live, scanned = 0, 0
local valid = env.valid
for index = 0, 40000 do
    for version = 0, 255 do
        local id = index + version * 1048576
        scanned = scanned + 1
        if valid(id) then
            live = live + 1
            for _, c in ipairs(watch) do
                local cc = C[c]
                if cc and cc.has(id) then
                    counts[c] = (counts[c] or 0) + 1
                    first[c] = first[c] or id
                end
            end
            if C.Persistent.has(id) and not C.Placeable.has(id) then
                local t = tostring(C.Persistent.getFor(id).applyTemplateOnLoad)
                tpl[t] = (tpl[t] or 0) + 1
            end
            break
        end
    end
end
add("role=" .. tostring(MP.role) .. " time=" .. tostring(env.getSimulationTime()) .. " live=" .. live)
local parts = {}
for _, c in ipairs(watch) do parts[#parts + 1] = c .. "=" .. tostring(counts[c] or 0) end
add("counts: " .. table.concat(parts, " "))
local tl = {}
for k, n in pairs(tpl) do tl[#tl + 1] = k .. "=" .. n end
table.sort(tl)
add("templates: " .. table.concat(tl, ", "))
for _, label in ipairs({ "Protesting", "ProtestLeader", "ProtestBanner", "ProtestBlockingTarget", "ProtestFireStarter", "ShouldStartProtest" }) do
    local e = first[label]
    if e then
        add("== " .. label .. " sample " .. tostring(e) .. " name=" .. tostring(env.getName(e)))
        for _, c in ipairs({ "Persistent", "Transform", "Protesting", "ProtestLeader", "ProtestBanner", "ProtestBlockingTarget",
            "ProtestBlockTarget", "ProtestFireStarter", "ShouldStartProtest", "PersonFSM", "HP", "DispatchRequest" }) do
            local ok, r = pcall(dump, e, c)
            if ok and r then add("   " .. r) end
        end
    end
end
return table.concat(out, "\n")
