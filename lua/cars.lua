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
-- money the host's wallet doesn't get, and the car would be taken back), and the host's buyers get theirs above.
local MP = ...

local S = MP.room
local C = component

local CHECK_TICKS = 20    -- every 2 s
local MAX_CHANGES = 20    -- per check, so a big difference doesn't stall one frame

S.vehicles = S.vehicles or {}

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
            return original(person, shop, ...)
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
