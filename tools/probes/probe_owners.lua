-- One-off probe: who owns which car model (by owner name), for comparing host and client.
local env = MP.env
local C = env.component
local list, n = {}, 0
for index = 0, 40000 do
    for version = 0, 255 do
        local id = index + version * 1048576
        if env.valid(id) then
            if C.VehicleOwner.has(id) then
                n = n + 1
                local v = C.VehicleOwner.getFor(id).vehicle
                local tpl = env.valid(v) and C.Persistent.has(v) and C.Persistent.getFor(v).applyTemplateOnLoad or "?"
                list[#list + 1] = tostring(env.getName(id)) .. "=" .. tostring(tpl) .. (C.Transform.has(id) and "*" or "")
            end
            break
        end
    end
end
table.sort(list)
return "owners " .. n .. ": " .. table.concat(list, "; ")
