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
