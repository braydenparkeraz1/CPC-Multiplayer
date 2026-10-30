-- Tests for lua/visits.lua (cars on a visit to a lot or drive-thru) against the stand-in engine
-- (tests/lua/engine.lua). Run through tests/run.ps1 (scenario LuaSync), or directly:
--   .testrun\bin\luarun\luarun.exe tests\lua\visits_test.lua
local here = (arg and arg[0] or ""):gsub("\\", "/")
local root = here:match("^(.*)/tests/lua/[^/]*$") or "."
local Engine = dofile(root .. "/tests/lua/engine.lua")

local tests, failures = {}, 0

local function test(name, fn) tests[#tests + 1] = { name = name, fn = fn } end

local function eq(actual, expected, what)
    if actual ~= expected then
        error(string.format("%s: expected %s, got %s", what or "value", tostring(expected), tostring(actual)), 2)
    end
end

local function near(actual, expected, what)
    if type(actual) ~= "number" or math.abs(actual - expected) > 0.01 then
        error(string.format("%s: expected %s, got %s", what or "value", tostring(expected), tostring(actual)), 2)
    end
end

local function city(role)
    local w = Engine.new(root)
    w:bootMod(false, role)
    w:step(1)
    w:clearEmitted()
    return w
end

-- Every car the mod reported since the last call, by id (the newest report of each), and the ids reported gone.
local function reports(w)
    local cars, gone, pays, lots, full = {}, {}, {}, {}, false
    for _, msg in ipairs(w:take("visits")) do
        for _, c in ipairs(msg.cars) do cars[c.id] = c end
        for _, id in ipairs(msg.gone) do gone[id] = true end
        for _, p in ipairs(msg.pays) do pays[#pays + 1] = p end
        for k, v in pairs(msg.lots) do lots[k] = v end
        full = full or msg.full == true
    end
    return cars, gone, pays, lots, full
end

local function count(t)
    local n = 0
    for _ in pairs(t) do n = n + 1 end
    return n
end

-- Host ---------------------------------------------------------------------------------------------------

test("the host reports a car with a bay, its kind, colours, place and angles; not one just driving", function()
    local w = city("host")
    local _, parker = w:spawnDriver("Ann", "Cabriolet", 10, 20)
    local _, wanderer = w:spawnDriver("Bob", "BananaCar", 30, 40)
    w.comps.ModelSpriteView[parker].reservedColorIndices = { x = 112, y = 113 }
    w.comps.ParkingSpaceClaim[parker] = { parkingSpace = 999 }
    w:step(2)
    local cars = reports(w)
    eq(count(cars), 1, "one car on a visit")
    local c = cars[parker]
    eq(c.tpl, "Cabriolet", "kind")
    eq(c.c1, 112, "main colour")
    eq(c.c2, 113, "second colour")
    near(c.x, 10, "x")
    near(c.z, 20, "z")
    near(c.ry, 90, "angle")
    eq(cars[wanderer], nil, "the other car isn't reported")
end)

test("a moving car is reported again, a standing one only with everything every 5 s", function()
    local w = city("host")
    local _, v = w:spawnDriver("Ann", "Cabriolet", 10, 20)
    w.comps.ParkingSpaceClaim[v] = { parkingSpace = 999 }
    w:step(2)
    reports(w)
    w.comps.Transform[v].position.x = 11
    w:step(2)
    local cars = reports(w)
    near(cars[v] and cars[v].x, 11, "moved")
    w:step(10)
    eq(count((reports(w))), 0, "standing: nothing")
    w:step(50)
    local again, _, _, _, full = reports(w)
    eq(full, true, "everything sent")
    eq(count(again), 1, "with the standing car")
end)

test("a car is still shown 3 s after its visit, then reported gone", function()
    local w = city("host")
    local p, v = w:spawnDriver("Ann", "Cabriolet", 10, 20)
    w.comps.ParkingEntryTicket[p] = { bPaid = false }
    w:step(2)
    eq(count((reports(w))), 1, "the ticket holder's car is on a visit")
    w.comps.ParkingEntryTicket[p] = nil
    w.comps.Transform[v].position.x = 12
    w:step(20)
    local cars, gone = reports(w)
    eq(cars[v] ~= nil, true, "still shown driving out")
    eq(gone[v], nil, "not gone yet")
    w:step(14)
    _, gone = reports(w)
    eq(gone[v], true, "gone after 3 s")
end)

test("an exit's money that grows is a parking payment there; the counters are reported", function()
    local w = city("host")
    local exit = w:spawnExit(5, 8, 6)
    w:step(4)
    local _, _, pays, lots = reports(w)
    eq(#pays, 0, "nothing paid when first seen")
    eq(lots["ParkingExit@5,8,6/3/0"].n, 0, "counter reported")
    w.comps.ParkingExit[exit].numVehiclesPassed = 1
    w.comps.ParkingExit[exit].totalMoneyTransacted = 129.6
    w:step(2)
    _, _, pays, lots = reports(w)
    eq(#pays, 1, "one payment")
    eq(pays[1].cat, "PARKING", "parking")
    near(pays[1].amount, 129.6, "amount")
    near(pays[1].x, 5, "at the exit")
    eq(lots["ParkingExit@5,8,6/3/0"].n, 1, "cars through")
    near(lots["ParkingExit@5,8,6/3/0"].m, 129.6, "money made")
end)

test("a drive-thru's payment (its script calls transactMoney) is reported; other money isn't", function()
    local w = city("host")
    w.room.transactMoney(4.5, w.room.TransactionCategory.DRIVE_THRU, w.room.vec3(7, 8, 9), true)
    w.room.transactMoney(500, w.room.TransactionCategory.MISCELLANEOUS, w.room.vec3(0, 0, 0), false)
    w:step(2)
    local _, _, pays = reports(w)
    eq(#pays, 1, "one visit payment")
    eq(pays[1].cat, "DRIVE_THRU", "drive-thru")
    near(pays[1].amount, 4.5, "amount")
    near(pays[1].z, 9, "where")
    eq(#w.transactions, 2, "both still went through on the host")
end)

-- Friend -------------------------------------------------------------------------------------------------

local function puppets(w)
    return w.MP.room.puppets
end

test("a friend's game shows the host's car: same kind and colours, nobody's, not saved, not clickable", function()
    local w = city("client")
    w:send({ type = "visits", cars = { { id = 501, tpl = "BananaCar", c1 = 112, c2 = 113, x = 10, y = 4, z = 20, rx = 0, ry = 90, rz = 0 } },
        gone = {}, lots = {}, pays = {} })
    local pp = puppets(w)[501]
    eq(pp ~= nil, true, "stand-in made")
    local car = pp.car
    eq(w.comps.Vehicle[car] ~= nil, true, "a vehicle")
    eq(w.comps.ModelSpriteView[car].reservedColorIndices.x, 112, "main colour")
    eq(w.comps.ModelSpriteView[car].reservedColorIndices.y, 113, "second colour")
    eq(w.comps.OwnedVehicle[car], nil, "nobody's")
    eq(w.comps.Persistent[car], nil, "not saved")
    eq(w.comps.Hoverable[car], nil, "not clickable")
    near(w.comps.Transform[car].position.z, 20, "where the host's is")
    near(w.comps.Transform[car].rotation.y, 90, "facing the same way")
end)

test("a stand-in moves smoothly to the host's next position, and goes when the host's car is done", function()
    local w = city("client")
    local function at(x) return { type = "visits", cars = { { id = 7, tpl = "Cabriolet", x = x, y = 4, z = 0, rx = 0, ry = 0, rz = 0 } }, gone = {}, lots = {}, pays = {} } end
    w:send(at(0))
    w:step(2)
    w:send(at(2))
    local car = puppets(w)[7].car
    w:step(1)
    local x = w.comps.Transform[car].position.x
    eq(x > 0 and x < 2, true, "part of the way after one frame (" .. tostring(x) .. ")")
    w:step(2)
    near(w.comps.Transform[car].position.x, 2, "there by the next report")
    w:send({ type = "visits", cars = {}, gone = { 7 }, lots = {}, pays = {} })
    eq(puppets(w)[7], nil, "gone")
    eq(w.alive[car], nil, "destroyed")
end)

test("everything from the host drops stand-ins it doesn't list", function()
    local w = city("client")
    w:send({ type = "visits", cars = { { id = 1, tpl = "Cabriolet", x = 0, y = 0, z = 0 }, { id = 2, tpl = "Cabriolet", x = 5, y = 0, z = 0 } },
        gone = {}, lots = {}, pays = {} })
    w:send({ type = "visits", cars = { { id = 2, tpl = "Cabriolet", x = 5, y = 0, z = 0 } }, gone = {}, lots = {}, pays = {}, full = true })
    eq(puppets(w)[1], nil, "unlisted one gone")
    eq(puppets(w)[2] ~= nil, true, "listed one stays")
end)

test("a friend's own cars don't visit: bay, ticket and drive-thru called off; stand-ins left alone", function()
    local w = city("client")
    local ann, annCar = w:spawnDriver("Ann", "Cabriolet", 0, 0)
    local _, bobCar = w:spawnDriver("Bob", "Cabriolet", 3, 0)
    w.comps.ParkingSpaceClaim[annCar] = { parkingSpace = 999 }
    w.comps.ParkingEntryTicket[ann] = { bPaid = false }
    w.comps.GoingToDriveThru[bobCar] = { driveThru = 998 }
    w:send({ type = "visits", cars = { { id = 3, tpl = "Cabriolet", x = 9, y = 0, z = 9 } }, gone = {}, lots = {}, pays = {} })
    local stand = puppets(w)[3].car
    w.comps.ParkingSpaceClaim[stand] = { parkingSpace = 997 }
    w:step(1)
    eq(w.comps.ParkingSpaceClaim[annCar], nil, "Ann's bay called off")
    eq(w.comps.ParkingEntryTicket[ann], nil, "and her ticket")
    eq(w.comps.GoingToDriveThru[bobCar], nil, "Bob's drive-thru called off")
    eq(w.comps.ParkingSpaceClaim[stand] ~= nil, true, "the stand-in isn't touched")
end)

test("a friend's own car parked from before is hidden, and shown again once driven away from the lot", function()
    local w = city("client")
    local ann, car = w:spawnDriver("Ann", "Cabriolet", 10, 10)
    w.comps.Driver[ann] = nil
    w.comps.ModelSpriteView[car].reservedColorIndices = { x = 112, y = 113, z = 0, w = 88 }
    w.comps.ParkingSpaceClaim[car] = { parkingSpace = 999 }
    local _, bobCar = w:spawnDriver("Bob", "Cabriolet", 0, 0)
    w.comps.ParkingSpaceClaim[bobCar] = { parkingSpace = 998 }
    w:step(1)
    eq(w.comps.ModelSpriteView[car], nil, "Ann's parked car has no sprite")
    eq(w.comps.DropShadowVolume[car], nil, "nor shadow")
    eq(w.comps.ModelSpriteView[bobCar] ~= nil, true, "Bob, driving to a bay, stays visible")
    w.comps.Driver[ann] = { vehicle = car }
    w.comps.Transform[car].position.x = 20
    w:step(1)
    eq(w.comps.ModelSpriteView[car], nil, "still hidden near the lot")
    w.comps.Transform[car].position.x = 40
    w:step(1)
    local view = w.comps.ModelSpriteView[car]
    eq(view ~= nil, true, "shown again")
    eq(view.name.model, "Cabriolet", "same model")
    eq(view.name.animation, "NoDoor", "same animation")
    eq(view.reservedColorIndices.x, 112, "same colour")
    eq(w.comps.DropShadowVolume[car].type, 1, "shadow back")
end)

test("a friend's own drive-thru income is dropped; the host's payments are shown where they happened", function()
    local w = city("client")
    w.room.transactMoney(4.5, w.room.TransactionCategory.DRIVE_THRU, w.room.vec3(1, 2, 3), true)
    w.room.transactMoney(-20, w.room.TransactionCategory.CONSTRUCTION, w.room.vec3(1, 2, 3), false)
    eq(#w.transactions, 1, "only the build went through")
    w:send({ type = "visits", cars = {}, gone = {}, lots = {}, pays = {
        { cat = "PARKING", amount = 129.6, x = 5, y = 8, z = 6 }, { cat = "DRIVE_THRU", amount = 3, x = 1, y = 1, z = 1 } } })
    eq(#w.transactions, 3, "both payments repeated")
    local t = w.transactions[2]
    near(t.amount, 129.6, "amount")
    eq(t.category, w.room.TransactionCategory.PARKING, "category")
    near(t.position.x, 5, "at the exit")
    eq(t.showText, true, "shown")
    eq(w.transactions[3].category, w.room.TransactionCategory.DRIVE_THRU, "the drive-thru's too")
end)

test("the host's lot counters are written onto ours", function()
    local w = city("client")
    local exit = w:spawnExit(5, 8, 6)
    w:step(2)
    w:send({ type = "visits", cars = {}, gone = {}, pays = {}, lots = { ["ParkingExit@5,8,6/3/0"] = { n = 287, m = 37858.05 } } })
    eq(w.comps.ParkingExit[exit].numVehiclesPassed, 287, "cars through")
    near(w.comps.ParkingExit[exit].totalMoneyTransacted, 37858.05, "money made")
end)

test("a stand-in isn't anyone's car to the owners list", function()
    local w = city("client")
    w:spawnPerson("Ann", false)
    w:send({ type = "cars", owners = {} })
    w:send({ type = "visits", cars = { { id = 3, tpl = "Cabriolet", x = 9, y = 0, z = 9 } }, gone = {}, lots = {}, pays = {} })
    w:step(25)
    eq(puppets(w)[3] ~= nil and w.alive[puppets(w)[3].car], true, "the stand-in stays")
    local status = w:take("carsync")
    eq(status[#status].taken, 0, "nothing taken back")
end)

for _, t in ipairs(tests) do
    local ok, err = pcall(t.fn)
    if ok then
        print("ok   " .. t.name)
    else
        failures = failures + 1
        print("FAIL " .. t.name .. ": " .. tostring(err))
    end
end
print(string.format("%d of %d visits tests passed", #tests - failures, #tests))
os.exit(failures == 0 and 0 or 1)
