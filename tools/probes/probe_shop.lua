-- One-off probe: car carousels (VehicleShop) and their state; owners count; who is shopping.
local env = MP.env
local C = env.component
local shops, owners, shopping, keen = {}, 0, 0, 0
for index = 0, 40000 do
    for version = 0, 255 do
        local id = index + version * 1048576
        if env.valid(id) then
            if C.VehicleShop.has(id) then
                local parts = { "shop " .. id }
                for _, c in ipairs({ "VehicleShopOpen", "VehicleShopReady", "VehicleShopEmpty", "Pricing", "GoodsStorage", "Placed", "Persistent" }) do
                    if C[c] and C[c].has(id) then parts[#parts + 1] = c end
                end
                local ok, n = pcall(function() return env.system.Goods.getStorageCount(id) end)
                parts[#parts + 1] = "storage=" .. tostring(ok and n or "?")
                shops[#shops + 1] = table.concat(parts, " ")
            end
            if C.VehicleOwner.has(id) then owners = owners + 1 end
            if C.Person.has(id) then
                local s = C.PersonFSM.getFor(id).state
                if s == env.PersonState.CAR_SHOPPING or s == env.PersonState.RUN_TO_CAROUSEL then shopping = shopping + 1 end
                if C.PersonStats.getFor(id).buyACar >= 1.0 then keen = keen + 1 end
            end
            break
        end
    end
end
return "[shop] role=" .. tostring(MP.role) .. " owners=" .. owners .. " shopping now=" .. shopping .. " buyACar>=1: " .. keen ..
    " CAR_SHOPPING=" .. tostring(env.PersonState.CAR_SHOPPING) .. " | " .. table.concat(shops, " ; ")
