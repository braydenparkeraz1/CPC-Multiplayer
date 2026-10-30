-- Car Park Capital Multiplayer - who owns which car.
-- People buy cars at a car carousel (system.VehicleShop.tryBuy, in the engine), and the city's car
-- ownership drives its Car Dependency level. Every PC simulates its own people, so they buy at other
-- times; the host's game decides. Both games have the same people (by name) from the host's save.
--
-- Host: every 2 s, reports every owner and the kind of car they own: "cars" { owners = { [name] = template } },
-- when it changed.
-- Friend: the helper passes the host's list on (inbox "cars"). Someone the host's game has with a car gets
-- the same kind of car here, as the game's own Lua makes one (_person_vehicle_utils.lua: applyTemplate,
-- VehicleOwner, OwnedVehicle); someone with a car here but not in the host's game loses it. Only people
-- out of town are changed, so no car vanishes from under its driver; the rest wait for their next trip out.
-- A friend's game sells no cars itself: its car carousels refuse every purchase (the sale would only flash
-- money the host's wallet doesn't get, and the car would be taken back).
--
-- Car carousels. A carousel's stock is up to 10 real cars in its GoodsStorage (entitiesPerItem: a slot per
-- carriage, empty ones nil), brought by car-carrier trucks. The game's tryBuy hands one of them, on the
-- map, to the buyer, who gets in, and adds to Pricing.numSold and moneyMade.
-- Host: reports each carousel's stock (the kinds of car), numSold and moneyMade when they change:
-- "shop" { key, stock = { template, ... }, sold, made }; at once for a sale, with sale = { buyer, tpl }.
-- Friend: its carousel holds the same kinds of car (cars its own trucks bring are destroyed, the host's are
-- made), with the host's numSold and moneyMade, and each sale is repeated: the buyer gets the same kind of
-- car at once, unless they are in a car or their own car is out on the map (then the owners list above
-- swaps it once they are out of town). It costs and earns nothing here: the money is the host's wallet.
local MP = ...

local S = MP.room
local C = component

local CHECK_TICKS = 20    -- every 2 s
local MAX_CHANGES = 20    -- per check, so a big difference doesn't stall one frame
local SHOP_TICKS = 5      -- carousels are compared every 0.5 s

S.vehicles = S.vehicles or {}
S.shops = S.shops or {}            -- carousel key -> entity
S.shopSent = S.shopSent or {}      -- host: carousel key -> what we last reported
S.shopWanted = S.shopWanted or {}  -- friend: carousel key -> the host's { stock, sold, made }

-- The car-shopping behavior (behavior/other/car_shopping.lua) asks system.VehicleShop.tryBuy, a plain Lua
-- table of the room, which hands a car from the carousel's stock over and charges for it. On a friend's game
-- it says no; the person goes home without a car, as when the carousel is empty.
local shopSystem = type(system) == "table" and system.VehicleShop
if type(shopSystem) == "table" and type(shopSystem.tryBuy) == "function" then
    if not S.tryBuyWrapped then
        S.tryBuyWrapped = true
        local original = shopSystem.tryBuy
        shopSystem.tryBuy = function(person, shop, ...)
            if MP.role == "client" then
                S.carsRefused = (S.carsRefused or 0) + 1
                return false
            end
            local bought = original(person, shop, ...)
            if bought and MP.role == "host" and MP.reportSale then
                local ok, err = pcall(MP.reportSale, person, shop)
                if not ok then MP.log("cars: sale not reported: " .. tostring(err)) end
            end
            return bought
        end
    end
else
    MP.log("cars: no system.VehicleShop.tryBuy, a friend's game can still sell cars")
end

local function templateOf(v)
    if not C.Persistent.has(v) then return nil end
    local tpl = C.Persistent.getFor(v).applyTemplateOnLoad
    return type(tpl) == "string" and tpl ~= "" and tpl or nil
end

-- Our game's owners: name -> template, and name -> { person, vehicle }.
local function ownersNow()
    local owners, ents = {}, {}
    for v in pairs(S.vehicles) do
        if not valid(v) then
            S.vehicles[v] = nil
        elseif C.OwnedVehicle.has(v) then
            local p = C.OwnedVehicle.getFor(v).owner
            local tpl = templateOf(v)
            local name = p and valid(p) and getName(p)
            if name and tpl then
                owners[name] = tpl
                ents[name] = { person = p, vehicle = v }
            end
        end
    end
    return owners, ents
end

local function signature(owners)
    local parts = {}
    for name, tpl in pairs(owners) do parts[#parts + 1] = name .. "=" .. tpl end
    table.sort(parts)
    return table.concat(parts, "\n")
end

-- In town people have a position; drivers and passengers are in a car.
local function outOfTown(p)
    return not C.Transform.has(p) and not C.Driver.has(p) and not C.VehiclePassenger.has(p)
end

-- A car out of town has no position either.
local function idle(v)
    return not valid(v) or not C.Transform.has(v)
end

local function takeCar(person, vehicle)
    if C.VehicleOwner.has(person) then C.VehicleOwner.remove(person) end
    if valid(vehicle) then destroyEntity(vehicle) end
    S.vehicles[vehicle] = nil
end

local function giveCar(person, tpl)
    local vehicle = createEntity()
    local ok, err = pcall(applyTemplate, vehicle, tpl, {}, true)
    if not ok then
        if valid(vehicle) then destroyEntity(vehicle) end
        MP.log("cars: no template " .. tostring(tpl) .. ": " .. tostring(err))
        return false
    end
    setComponent(person, VehicleOwner { vehicle = vehicle })
    setComponent(vehicle, OwnedVehicle { owner = person })
    S.vehicles[vehicle] = true
    return true
end

-- Friend: brings our owners in line with the host's list. Returns what it did and what waits.
local function reconcile(wanted)
    local have, ents = ownersNow()
    local status = { given = 0, taken = 0, waiting = 0, missing = 0, refused = S.carsRefused or 0 }
    local changes = 0
    for name, tpl in pairs(have) do
        if wanted[name] ~= tpl then
            local e = ents[name]
            if changes < MAX_CHANGES and outOfTown(e.person) and idle(e.vehicle) then
                takeCar(e.person, e.vehicle)
                have[name] = nil
                changes = changes + 1
                status.taken = status.taken + 1
            else
                status.waiting = status.waiting + 1
            end
        end
    end
    for name, tpl in pairs(wanted) do
        if have[name] == nil then
            local p = getByName(name)
            if not p or not valid(p) or not C.Person.has(p) then
                -- Someone who moved to town in the host's game only.
                status.missing = status.missing + 1
            elseif C.VehicleOwner.has(p) and valid(C.VehicleOwner.getFor(p).vehicle) then
                -- A car we don't know (made before the hook): count it from now on, sort it out next time.
                S.vehicles[C.VehicleOwner.getFor(p).vehicle] = true
                status.waiting = status.waiting + 1
            elseif changes < MAX_CHANGES and outOfTown(p) then
                if C.VehicleOwner.has(p) then C.VehicleOwner.remove(p) end
                if giveCar(p, tpl) then
                    changes = changes + 1
                    status.given = status.given + 1
                end
            else
                status.waiting = status.waiting + 1
            end
        end
    end
    return status
end

-- Car carousels ------------------------------------------------------------------------------------------

local goods = type(system) == "table" and system.Goods
local VEHICLE_ITEM = currentRoom and currentRoom.items and currentRoom.items.VEHICLE

-- Carousels are found as lua/sync.lua indexes the city's objects (they all have VehicleShop).
local function noteShop(e, key)
    if type(key) == "string" and valid(e) and C.VehicleShop.has(e) then S.shops[key] = e end
end
local indexedBefore = MP.onIndexed
MP.onIndexed = function(e, key)
    if indexedBefore then indexedBefore(e, key) end
    noteShop(e, key)
end
for e, key in pairs(S.known or {}) do noteShop(e, key) end

local function shopNamed(key)
    local e = S.shops[key]
    if e and valid(e) and C.VehicleShop.has(e) and S.known and S.known[e] == key then return e end
    S.shops[key] = nil
    return nil
end

-- The cars in the carousel's slots, and their kinds sorted (a carousel holds no other goods).
local function stockOf(shop)
    local cars, kinds = {}, {}
    if not C.GoodsStorage.has(shop) then return cars, kinds end
    local slots = C.GoodsStorage.getFor(shop).entitiesPerItem[VEHICLE_ITEM]
    if slots then
        for i = 1, #slots do
            local car = slots[i]
            local tpl = car and valid(car) and templateOf(car)
            if tpl then
                cars[#cars + 1] = { car = car, tpl = tpl }
                kinds[#kinds + 1] = tpl
            end
        end
    end
    table.sort(kinds)
    return cars, kinds
end

local function money(x)
    return math.floor((tonumber(x) or 0) * 100 + 0.5) / 100
end

local function shopState(shop)
    local _, kinds = stockOf(shop)
    local pricing = C.Pricing.getFor(shop)
    return { stock = kinds, sold = pricing.numSold, made = money(pricing.moneyMade) }
end

local function stateSignature(st)
    return table.concat(st.stock, ",") .. "|" .. tostring(st.sold) .. "|" .. string.format("%.2f", st.made)
end

-- Host: a person bought a car at a carousel (from the tryBuy wrapper above, after the game's own).
MP.reportSale = function(person, shop)
    local key = S.known and S.known[shop]
    if not key then return end
    S.shops[key] = shop
    local car = C.VehicleOwner.has(person) and C.VehicleOwner.getFor(person).vehicle
    local st = shopState(shop)
    st.key = key
    st.sale = { buyer = getName(person), tpl = car and valid(car) and templateOf(car) }
    S.shopSent[key] = stateSignature(st)
    MP.emit("shop", st)
end

-- Takes cars out of the carousel. Goods.popItemFromStorage only takes the car in the last full slot (and
-- leaves it standing where it was), so pop until every unwanted car is out, and put the others back.
local function removeFromStock(shop, unwanted, count)
    local kept, removed = {}, 0
    for _ = 1, 12 do
        if removed >= count then break end
        local before = {}
        for _, s in ipairs((stockOf(shop))) do before[s.car] = true end
        if not goods.popItemFromStorage(shop, VEHICLE_ITEM) then break end
        local popped
        for _, s in ipairs((stockOf(shop))) do before[s.car] = nil end
        popped = next(before)
        if not popped then break end
        if unwanted[popped] then
            destroyEntity(popped)
            S.vehicles[popped] = nil
            removed = removed + 1
        else
            kept[#kept + 1] = popped
        end
    end
    for _, car in ipairs(kept) do goods.pushItemToStorage(shop, VEHICLE_ITEM, car) end
    return removed
end

-- Friend: the carousel's stock becomes the host's kinds of car. Returns how many went and came.
local function mirrorStock(shop, wanted)
    local need = {}
    for _, tpl in ipairs(wanted) do need[tpl] = (need[tpl] or 0) + 1 end
    local unwanted, count = {}, 0
    for _, s in ipairs((stockOf(shop))) do
        if (need[s.tpl] or 0) > 0 then
            need[s.tpl] = need[s.tpl] - 1
        else
            unwanted[s.car] = true
            count = count + 1
        end
    end
    local removed = count > 0 and removeFromStock(shop, unwanted, count) or 0
    local added = 0
    for tpl, n in pairs(need) do
        for _ = 1, n do
            local car = createEntity()
            local ok, err = pcall(applyTemplate, car, tpl, {}, true)
            if ok and goods.pushItemToStorage(shop, VEHICLE_ITEM, car) then
                added = added + 1
            else
                if valid(car) then destroyEntity(car) end
                S.vehicles[car] = nil
                if not ok then MP.log("cars: no template " .. tostring(tpl) .. ": " .. tostring(err)) end
            end
        end
    end
    return removed, added
end

-- Friend: the host's numSold, moneyMade and stock on our carousel. Returns what changed in the stock.
local function applyShop(shop, want)
    local pricing = C.Pricing.getFor(shop)
    if want.sold and pricing.numSold ~= want.sold then pricing.numSold = want.sold end
    if want.made and money(pricing.moneyMade) ~= want.made then pricing.moneyMade = want.made end
    local _, kinds = stockOf(shop)
    if table.concat(kinds, ",") == table.concat(want.stock, ",") then return 0, 0 end
    return mirrorStock(shop, want.stock)
end

-- Friend: the host sold a car to this person. Returns what happened to them here.
local function sellHere(name, tpl)
    if type(name) ~= "string" or type(tpl) ~= "string" then return "unknown" end
    local p = getByName(name)
    if not p or not valid(p) or not C.Person.has(p) then return "missing" end
    -- The host's next owners list has them; until then the last one mustn't take this car back.
    if S.carsWanted then S.carsWanted[name] = tpl end
    local own = C.VehicleOwner.has(p) and C.VehicleOwner.getFor(p).vehicle
    if own and valid(own) then
        if templateOf(own) == tpl then return "had" end
        if not idle(own) then return "waiting" end
    end
    if C.Driver.has(p) or C.VehiclePassenger.has(p) then return "waiting" end
    if own then takeCar(p, own) elseif C.VehicleOwner.has(p) then C.VehicleOwner.remove(p) end
    return giveCar(p, tpl) and "given" or "failed"
end

local function cleanStock(list)
    local stock = {}
    if type(list) == "table" then
        for _, tpl in ipairs(list) do
            if type(tpl) == "string" then stock[#stock + 1] = tpl end
        end
    end
    table.sort(stock)
    return stock
end

-- From the helper (friend only): a carousel's state in the host's game, with a sale if there was one.
MP.handlers.shop = function(msg)
    if MP.role == "host" or type(msg.key) ~= "string" then return end
    local want = { stock = cleanStock(msg.stock), sold = tonumber(msg.sold), made = tonumber(msg.made) and money(msg.made) }
    S.shopWanted[msg.key] = want
    local status = { key = msg.key }
    if type(msg.sale) == "table" then
        status.buyer = msg.sale.buyer
        status.sale = sellHere(msg.sale.buyer, msg.sale.tpl)
    end
    local shop = goods and VEHICLE_ITEM and shopNamed(msg.key)
    if shop then
        status.removed, status.added = applyShop(shop, want)
        status.stock = #(select(2, stockOf(shop)))
    else
        status.shop = "not in our city yet"
    end
    MP.emit("shopsync", status)
end

local function shopsTick()
    if not goods or not VEHICLE_ITEM then return end
    if MP.role == "host" then
        for key in pairs(S.shops) do
            local shop = shopNamed(key)
            if shop then
                local st = shopState(shop)
                local sig = stateSignature(st)
                if sig ~= S.shopSent[key] then
                    S.shopSent[key] = sig
                    st.key = key
                    MP.emit("shop", st)
                end
            end
        end
    elseif MP.role == "client" then
        -- Our own trucks still bring cars, and a carousel may be built after the host's state came.
        for key, want in pairs(S.shopWanted) do
            local shop = shopNamed(key)
            if shop then
                local removed, added = applyShop(shop, want)
                if removed > 0 or added > 0 then
                    MP.emit("shopsync", { key = key, removed = removed, added = added, stock = #want.stock })
                end
            end
        end
    end
end

-- From the helper (friend only): the host's owners.
MP.handlers.cars = function(msg)
    if type(msg.owners) ~= "table" then return end
    local wanted = {}
    for name, tpl in pairs(msg.owners) do
        if type(name) == "string" and type(tpl) == "string" then wanted[name] = tpl end
    end
    S.carsWanted = wanted
    S.carsNextCheck = S.tick
end

MP.carsTick = function()
    if S.tick % SHOP_TICKS == 0 then shopsTick() end
    if S.tick < (S.carsNextCheck or 0) then return end
    S.carsNextCheck = S.tick + CHECK_TICKS
    if MP.role == "host" then
        local owners = ownersNow()
        local sig = signature(owners)
        if sig ~= S.carsSent then
            S.carsSent = sig
            MP.emit("cars", { owners = owners })
        end
    elseif MP.role == "client" and S.carsWanted then
        local status = reconcile(S.carsWanted)
        local sig = status.given .. "/" .. status.taken .. "/" .. status.waiting .. "/" .. status.missing .. "/" .. status.refused
        if status.given > 0 or status.taken > 0 or sig ~= S.carsStatus then
            S.carsStatus = sig
            MP.emit("carsync", status)
        end
    end
end
