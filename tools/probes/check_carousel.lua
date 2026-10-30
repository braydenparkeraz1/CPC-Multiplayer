-- Check: this game's carousel (kinds in stock, count, numSold, moneyMade), stray cars at it, and the buyers' cars.
-- Set MP.checkNames = { "<buyer>", ... } first (the "buyer" names in the host's "shop" log lines).
local env = MP.env
local C = env.component
local S = MP.room
local item = env.currentRoom.items.VEHICLE
local out = { "[check] role=" .. tostring(MP.role) }
for e, key in pairs(S.known) do
    if key:find("^CarCarousel@") then
        local slots = C.GoodsStorage.getFor(e).entitiesPerItem[item]
        local kinds = {}
        for i = 1, #slots do
            local car = slots[i]
            if car and env.valid(car) then kinds[#kinds + 1] = C.Persistent.getFor(car).applyTemplateOnLoad end
        end
        table.sort(kinds)
        local pr = C.Pricing.getFor(e)
        out[#out + 1] = string.format("stock=%s count=%d sold=%s made=%.2f", table.concat(kinds, ","),
            env.system.Goods.getStorageCount(e, item), tostring(pr.numSold), pr.moneyMade)
    end
end
local stray = 0
for v in pairs(S.vehicles) do
    if env.valid(v) and C.Transform.has(v) and not C.InStorage.has(v) and not C.OwnedVehicle.has(v)
        and not C.GoodsTransporter.has(v) and not C.Driver.has(v) then stray = stray + 1 end
end
out[#out + 1] = "unowned cars on the map (not in stock): " .. stray
for _, name in ipairs(MP.checkNames or {}) do
    local p = env.getByName(name)
    local v = p and C.VehicleOwner.has(p) and C.VehicleOwner.getFor(p).vehicle
    out[#out + 1] = name .. ": " .. (v and env.valid(v) and (C.Persistent.getFor(v).applyTemplateOnLoad .. (C.Transform.has(v) and " on map" or " off map")) or "no car")
        .. (p and (" state=" .. tostring(C.PersonFSM.getFor(p).state) .. (C.Transform.has(p) and " in town" or " out of town")) or "")
end
return table.concat(out, " | ")
