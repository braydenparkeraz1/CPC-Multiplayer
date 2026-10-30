-- Tests for lua/cars.lua (who owns which car) against the stand-in engine (tests/lua/engine.lua).
-- Run through tests/run.ps1 (scenario LuaSync), or directly: .testrun\bin\luarun\luarun.exe tests\lua\cars_test.lua
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

-- A city loaded from a save with these people (name -> car template, or false for none).
local function city(role, people)
    local w = Engine.new(root)
    local names = {}
    for name in pairs(people) do names[#names + 1] = name end
    table.sort(names)
    -- The hook is installed before the save's entities are made, as in the game.
    w:bootMod(false, role)
    for _, name in ipairs(names) do w:spawnPerson(name, people[name] or nil) end
    w:clearEmitted()
    return w
end

local function owners(msg)
    local parts = {}
    for name, tpl in pairs(msg.owners) do parts[#parts + 1] = name .. "=" .. tpl end
    table.sort(parts)
    return table.concat(parts, ",")
end

test("the host reports its owners, and again only when they change", function()
    local w = city("host", { Ann = "Cabriolet", Bob = false, Cy = "BananaCar" })
    w:step(25)
    local sent = w:take("cars")
    eq(#sent, 1, "one report")
    eq(owners(sent[1]), "Ann=Cabriolet,Cy=BananaCar", "owners")
    w:step(25)
    eq(#w:take("cars"), 0, "nothing new")
    -- Bob buys a car (the game hands one over: no setComponent for the owner link through Lua).
    local v = w:create()
    w.room.applyTemplate(v, "BubbleCabin", {}, true)
    w.comps.OwnedVehicle[v] = { owner = w.room.getByName("Bob") }
    w.comps.VehicleOwner[w.room.getByName("Bob")] = { vehicle = v }
    w:step(25)
    sent = w:take("cars")
    eq(#sent, 1, "reported again")
    eq(owners(sent[1]), "Ann=Cabriolet,Bob=BubbleCabin,Cy=BananaCar", "with Bob's")
end)

test("a friend's game gives the host's owners the same car, made like the game's own", function()
    local w = city("client", { Ann = false, Bob = false })
    w:send({ type = "cars", owners = { Ann = "BananaCar" } })
    w:step(3)
    local car, tpl = w:carOf("Ann")
    eq(tpl, "BananaCar", "Ann's car")
    eq(w.comps.OwnedVehicle[car].owner, w.room.getByName("Ann"), "car knows its owner")
    eq(w:carOf("Bob"), nil, "Bob has none")
    local status = w:take("carsync")
    eq(#status, 1, "status")
    eq(status[1].given, 1, "one given")
end)

test("a car only the friend's game has is taken back once its owner is out of town", function()
    local w = city("client", { Ann = "Cabriolet", Bob = "Cabriolet" })
    -- Bob is in town with his car.
    local bob = w.room.getByName("Bob")
    local bobCar = w:carOf("Bob")
    w.comps.Transform[bob] = {}
    w.comps.Driver[bob] = { vehicle = bobCar }
    w.comps.Transform[bobCar] = {}
    w:send({ type = "cars", owners = { Ann = "Cabriolet" } })
    w:step(3)
    eq(w:carOf("Bob"), bobCar, "Bob keeps it while driving")
    eq(w:take("carsync")[1].waiting, 1, "waiting for Bob")
    -- Bob leaves town.
    w.comps.Transform[bob] = nil
    w.comps.Driver[bob] = nil
    w.comps.Transform[bobCar] = nil
    w:step(21)
    eq(w:carOf("Bob"), nil, "taken")
    eq(w.alive[bobCar], nil, "the car is gone")
    eq(w.comps.VehicleOwner[bob], nil, "Bob owns nothing")
    eq(select(2, w:carOf("Ann")), "Cabriolet", "Ann keeps hers")
end)

test("another kind of car is swapped for the host's kind", function()
    local w = city("client", { Ann = "Cabriolet" })
    local old = w:carOf("Ann")
    w:send({ type = "cars", owners = { Ann = "BananaCar" } })
    w:step(3)
    w:step(21)
    local car, tpl = w:carOf("Ann")
    eq(tpl, "BananaCar", "swapped")
    eq(w.alive[old], nil, "the old one is gone")
end)

test("owners the friend's city doesn't have, and unknown kinds of car, are counted and skipped", function()
    local w = city("client", { Ann = false })
    w:send({ type = "cars", owners = { Newcomer = "Cabriolet", Ann = "Missing" } })
    w:step(3)
    eq(w:carOf("Ann"), nil, "no car of a kind the game doesn't know")
    local status = w:take("carsync")[1]
    eq(status.missing, 1, "the newcomer")
    eq(status.given, 0, "nothing given")
    eq(#w.logs > 0, true, "the unknown kind is logged")
end)

test("a friend's car carousels sell nothing; the host's do", function()
    local w = city("client", { Ann = false })
    local ann = w.room.getByName("Ann")
    eq(w.room.system.VehicleShop.tryBuy(ann, 99), false, "refused")
    eq(w:carOf("Ann"), nil, "no car")
    eq(w.sold, 0, "nothing sold")
    w:send({ type = "cars", owners = {} })
    w:step(3)
    eq(w:take("carsync")[1].refused, 1, "counted")
    local h = city("host", { Bob = false })
    eq(h.room.system.VehicleShop.tryBuy(h.room.getByName("Bob"), 99), true, "the host's sells")
    eq(select(2, h:carOf("Bob")), "Cabriolet", "Bob's car")
end)

test("a big difference is worked off a few at a time", function()
    local people, wanted = {}, {}
    for i = 1, 45 do
        people["P" .. i] = false
        wanted["P" .. i] = "Cabriolet"
    end
    local w = city("client", people)
    w:send({ type = "cars", owners = wanted })
    w:step(3)
    eq(w:take("carsync")[1].given, 20, "first check")
    w:step(20)
    w:step(20)
    local n = 0
    for i = 1, 45 do if w:carOf("P" .. i) then n = n + 1 end end
    eq(n, 45, "all after three checks")
end)

-- Car carousels -------------------------------------------------------------------------------------------

local SHOP_KEY = "CarCarousel@5,0,5/0/0"

-- A city with a carousel (loaded from the save, so it is indexed once the mod is ready).
local function cityWithShop(role, people, stock)
    local w = city(role, people)
    local shop = w:spawnCarousel(5, 0, 5, stock)
    w:step(25)
    w:clearEmitted()
    return w, shop
end

local function stockText(list)
    local t = {}
    for _, tpl in ipairs(list) do t[#t + 1] = tpl end
    table.sort(t)
    return table.concat(t, ",")
end

test("the host reports a carousel's stock when a truck brings cars, and each sale at once", function()
    local w = city("host", { Ann = false })
    local shop = w:spawnCarousel(5, 0, 5, { "Cabriolet" })
    w:step(25)
    local sent = w:take("shop")
    eq(#sent, 1, "first report")
    eq(sent[1].key, SHOP_KEY, "key")
    eq(stockText(sent[1].stock), "Cabriolet", "stock")
    eq(sent[1].sold, 0, "nothing sold yet")
    w:deliver(shop, "BananaCar")
    w:step(5)
    sent = w:take("shop")
    eq(#sent, 1, "the delivery")
    eq(stockText(sent[1].stock), "BananaCar,Cabriolet", "two cars")
    -- Ann buys the last car in: the game's own tryBuy, from the car-shopping behavior.
    eq(w.room.system.VehicleShop.tryBuy(w.room.getByName("Ann"), shop), true, "sold")
    sent = w:take("shop")
    eq(#sent, 1, "reported at once, before the next tick")
    eq(sent[1].sale.buyer, "Ann", "buyer")
    eq(sent[1].sale.tpl, "BananaCar", "her car")
    eq(stockText(sent[1].stock), "Cabriolet", "stock after the sale")
    eq(sent[1].sold, 1, "numSold")
    eq(sent[1].made, 220, "moneyMade")
    w:step(10)
    eq(#w:take("shop"), 0, "not again")
end)

test("a friend's carousel gets the host's kinds of car, numSold and moneyMade", function()
    local w, shop = cityWithShop("client", {}, { "Cabriolet", "Cabriolet", "BubbleCabin" })
    w:send({ type = "shop", key = SHOP_KEY, stock = { "BananaCar", "Cabriolet", "BananaCar" }, sold = 68, made = 16217.5 })
    eq(w:stockOf(shop), "BananaCar,BananaCar,Cabriolet", "stock")
    eq(w.comps.Pricing[shop].numSold, 68, "numSold")
    eq(w.comps.Pricing[shop].moneyMade, 16217.5, "moneyMade")
    eq(w:strayCars(), 0, "the cars taken out are gone, not left standing")
    local status = w:take("shopsync")[1]
    eq(status.removed, 2, "removed")
    eq(status.added, 2, "added")
    -- The friend's own truck brings a car: taken out again within half a second.
    w:deliver(shop, "BubbleCabin")
    w:step(5)
    eq(w:stockOf(shop), "BananaCar,BananaCar,Cabriolet", "own delivery undone")
    eq(w:strayCars(), 0, "and destroyed")
end)

test("a sale in the host's game happens in the friend's: the buyer gets the car, the stock drops", function()
    local w, shop = cityWithShop("client", { Ann = false, Bob = false }, { "BananaCar", "Cabriolet" })
    w:send({ type = "cars", owners = {} })
    w:step(3)
    w:send({ type = "shop", key = SHOP_KEY, stock = { "Cabriolet" }, sold = 1, made = 220,
        sale = { buyer = "Ann", tpl = "BananaCar" } })
    local car, tpl = w:carOf("Ann")
    eq(tpl, "BananaCar", "Ann's car, at once")
    eq(w.comps.OwnedVehicle[car].owner, w.room.getByName("Ann"), "owned")
    eq(w:stockOf(shop), "Cabriolet", "one fewer in stock")
    eq(w.comps.Pricing[shop].numSold, 1, "counted")
    eq(w:take("shopsync")[1].sale, "given", "status")
    -- The owners list sent before the sale doesn't take it back; the next one, with Ann, gives no second car.
    w:step(25)
    eq(w:carOf("Ann"), car, "kept")
    w:send({ type = "cars", owners = { Ann = "BananaCar" } })
    w:step(25)
    eq(w:carOf("Ann"), car, "the same car")
    local n = 0
    for _, o in pairs(w.comps.OwnedVehicle) do if o.owner == w.room.getByName("Ann") then n = n + 1 end end
    eq(n, 1, "one car")
end)

test("a buyer who already got the car from the owners list gets no second one", function()
    local w = cityWithShop("client", { Ann = false }, { "BananaCar" })
    w:send({ type = "cars", owners = { Ann = "BananaCar" } })
    w:step(3)
    local car = w:carOf("Ann")
    w:send({ type = "shop", key = SHOP_KEY, stock = {}, sold = 1, made = 220, sale = { buyer = "Ann", tpl = "BananaCar" } })
    eq(w:carOf("Ann"), car, "the same car")
    eq(w:take("shopsync")[1].sale, "had", "status")
end)

test("a buyer driving their own car here gets the host's kind once out of town", function()
    local w, shop = cityWithShop("client", { Bob = "Cabriolet" }, { "BananaCar" })
    w:send({ type = "cars", owners = { Bob = "Cabriolet" } })
    w:step(3)
    local bob, bobCar = w.room.getByName("Bob"), w:carOf("Bob")
    w.comps.Transform[bob] = {}
    w.comps.Driver[bob] = { vehicle = bobCar }
    w.comps.Transform[bobCar] = {}
    w:send({ type = "shop", key = SHOP_KEY, stock = {}, sold = 1, made = 220, sale = { buyer = "Bob", tpl = "BananaCar" } })
    eq(w:carOf("Bob"), bobCar, "keeps his car while driving")
    eq(w:take("shopsync")[1].sale, "waiting", "status")
    eq(w:stockOf(shop), "", "the stock drops anyway")
    w:step(25)
    eq(w:carOf("Bob"), bobCar, "still, though the old owners list says Cabriolet")
    w.comps.Transform[bob] = nil
    w.comps.Driver[bob] = nil
    w.comps.Transform[bobCar] = nil
    w:step(50)
    eq(select(2, w:carOf("Bob")), "BananaCar", "swapped once out of town")
end)

test("a buyer the friend's city doesn't have: only the stock follows", function()
    local w, shop = cityWithShop("client", {}, { "BananaCar" })
    w:send({ type = "shop", key = SHOP_KEY, stock = {}, sold = 1, made = 220, sale = { buyer = "Newcomer", tpl = "BananaCar" } })
    eq(w:stockOf(shop), "", "stock")
    eq(w:take("shopsync")[1].sale, "missing", "status")
end)

test("a friend's carousel sells nothing by itself and earns nothing", function()
    local w, shop = cityWithShop("client", { Ann = false }, { "BananaCar" })
    w:send({ type = "shop", key = SHOP_KEY, stock = { "BananaCar" }, sold = 5, made = 1100 })
    eq(w.room.system.VehicleShop.tryBuy(w.room.getByName("Ann"), shop), false, "refused")
    eq(w:carOf("Ann"), nil, "no car")
    eq(w:stockOf(shop), "BananaCar", "stock kept")
    eq(w.comps.Pricing[shop].numSold, 5, "numSold the host's")
    eq(w.comps.Pricing[shop].moneyMade, 1100, "moneyMade the host's")
    w:step(10)
    eq(#w:take("shop"), 0, "a friend reports no carousel")
end)

test("a carousel the friend's game doesn't have yet gets the host's stock once it is there", function()
    local w = city("client", {})
    w:step(25)
    w:send({ type = "shop", key = SHOP_KEY, stock = { "Cabriolet", "Cabriolet" }, sold = 2, made = 440 })
    eq(w:take("shopsync")[1].shop ~= nil, true, "noted as not there yet")
    local shop = w:spawnCarousel(5, 0, 5, {})
    w:step(10)
    eq(w:stockOf(shop), "Cabriolet,Cabriolet", "stock")
    eq(w.comps.Pricing[shop].numSold, 2, "numSold")
end)

for _, t in ipairs(tests) do
    local ok, err = pcall(t.fn)
    if ok then
        print("    ok: " .. t.name)
    else
        failures = failures + 1
        print("    FAIL: " .. t.name .. ": " .. tostring(err))
    end
end
if failures > 0 then
    print(failures .. " of " .. #tests .. " failed")
    os.exit(1)
end
print("all " .. #tests .. " passed")
