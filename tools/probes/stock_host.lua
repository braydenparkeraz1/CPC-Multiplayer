-- Test helper: puts cars into the host's carousel the way a car-carrier truck does.
local env = MP.env
local C = env.component
local S = MP.room
local item = env.currentRoom.items.VEHICLE
local kinds = { "BananaCar", "BubbleCabin", "ClassicCar", "Cabriolet", "BananaCar", "BubbleCabin", "ClassicCar", "Cabriolet" }
local out = {}
for e, key in pairs(S.known) do
    if key:find("^CarCarousel@") then
        for _, tpl in ipairs(kinds) do
            local car = env.createEntity()
            env.applyTemplate(car, tpl, {}, true)
            if not env.system.Goods.pushItemToStorage(e, item, car) then env.destroyEntity(car) break end
        end
        out[#out + 1] = key .. " now " .. env.system.Goods.getStorageCount(e, item)
    end
end
return "[stock] " .. table.concat(out, " ")
