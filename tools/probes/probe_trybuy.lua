-- One-off probe: wraps system.VehicleShop.tryBuy and records what each purchase does.
-- Run again to read the records: returns them (and keeps recording).
local env = MP.env
local C = env.component
local S = MP.room
local item = env.currentRoom.items.VEHICLE
S.buyLog = S.buyLog or {}
local function comps(e)
    if not env.valid(e) then return "invalid" end
    local parts = {}
    for _, c in ipairs({ "Transform", "TransformChild", "InStorage", "OwnedVehicle", "Driver", "Vehicle", "VehicleOwner", "VehiclePassenger", "EnterVehicle", "LeaveVehicle" }) do
        if C[c] and C[c].has(e) then parts[#parts + 1] = c end
    end
    return table.concat(parts, ",")
end
local function stockList(shop)
    local cars = C.GoodsStorage.getFor(shop).entitiesPerItem[item]
    local ids = {}
    if cars then for _, car in ipairs(cars) do ids[#ids + 1] = tostring(car) end end
    return table.concat(ids, ",")
end
if not S.buyProbe then
    S.buyProbe = true
    local prev = env.system.VehicleShop.tryBuy
    env.system.VehicleShop.tryBuy = function(person, shop, ...)
        local pr = C.Pricing.getFor(shop)
        local before = string.format("stock=%s sold=%s made=%.1f money=%s owner=%s pcomps=%s state=%s",
            stockList(shop), tostring(pr.numSold), pr.moneyMade, tostring(env.getMoney and env.getMoney() or "?"),
            tostring(C.VehicleOwner.has(person) and C.VehicleOwner.getFor(person).vehicle), comps(person),
            tostring(C.PersonFSM.getFor(person).state))
        local r = prev(person, shop, ...)
        local car = C.VehicleOwner.has(person) and C.VehicleOwner.getFor(person).vehicle
        local after = string.format("r=%s stock=%s sold=%s made=%.1f car=%s carcomps=%s pcomps=%s tpl=%s",
            tostring(r), stockList(shop), tostring(pr.numSold), pr.moneyMade, tostring(car), car and comps(car) or "-",
            comps(person), car and C.Persistent.has(car) and C.Persistent.getFor(car).applyTemplateOnLoad or "-")
        S.buyLog[#S.buyLog + 1] = env.getName(person) .. " @" .. tostring(env.getSimulationTime()) .. " BEFORE " .. before .. " AFTER " .. after
        -- And a moment later.
        local p = person
        env.setTimeout(MP.hostEntity, 1.0, function()
            local c2 = C.VehicleOwner.has(p) and C.VehicleOwner.getFor(p).vehicle
            S.buyLog[#S.buyLog + 1] = "  +1s " .. env.getName(p) .. " pcomps=" .. comps(p) .. " state=" .. tostring(C.PersonFSM.getFor(p).state)
                .. " car=" .. tostring(c2) .. " carcomps=" .. (c2 and comps(c2) or "-")
        end)
        return r
    end
end
local out = table.concat(S.buyLog, " || ")
S.buyLog = {}
return "[trybuy] " .. out
