-- Tests for lua/settings.lua (prices) against the stand-in engine (tests/lua/engine.lua).
-- Run through tests/run.ps1 (scenario LuaSync), or directly: .testrun\bin\luarun\luarun.exe tests\lua\settings_test.lua
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

local FIELD = "ParkingEntry.ratePerMinute"

-- A city with two parking entrances at 0.06, loaded and indexed.
local function city()
    local w = Engine.new(root)
    w:bootMod(true)
    for _, x in ipairs({ 1, 2 }) do
        local e = w:spawnPlaced("ParkingEntrance", x, 8, 1)
        w.comps.ParkingEntry[e] = { ratePerMinute = 0.06 }
    end
    w:step(25)
    w:clearEmitted()
    return w
end

local function entrance(w, x)
    return w.comps.ParkingEntry[w:objectAt(x, 8, 1)]
end

local function key(x)
    return "ParkingEntrance@" .. x .. ",8,1/0/0"
end

test("a price our player changes is reported", function()
    local w = city()
    entrance(w, 1).ratePerMinute = 0.3
    w:step(3)
    local settings = w:take("setting")
    eq(#settings, 1, "one report")
    eq(settings[1].key, key(1), "key")
    eq(settings[1].fields[FIELD], 0.3, "value")
end)

test("a friend's price is applied and not reported back", function()
    local w = city()
    w:send({ type = "settings", entries = { { key = key(1), fields = { [FIELD] = 0.5 } } }, seen = 0 })
    w:step(10)
    eq(entrance(w, 1).ratePerMinute, 0.5, "applied")
    eq(#w:take("setting"), 0, "not echoed")
    eq(#w:take("settingrevert"), 0, "nothing undone")
end)

test("with 'apply to all' ticked, the game undoing a friend's price isn't our player's edit", function()
    local w = city()
    w.syncedPrices.parking = 0.06
    w:send({ type = "settings", entries = { { key = key(1), fields = { [FIELD] = 0.5 } } }, seen = 0 })
    w:step(10)
    eq(entrance(w, 1).ratePerMinute, 0.06, "the game put its own price back")
    eq(#w:take("setting"), 0, "not reported as our player's edit")
    local reverts = w:take("settingrevert")
    eq(#reverts, 1, "reported as undone by the game")
    eq(reverts[1].key, key(1), "key")
    eq(reverts[1].wanted, 0.5, "the price we wanted")
    eq(reverts[1].value, 0.06, "the game's price")
    eq(reverts[1].kind, "parking", "the kind of price")
end)

test("the game's stored price changed to ours later is quiet; our player's edit after that is reported", function()
    local w = city()
    w.syncedPrices.parking = 0.06
    w:send({ type = "settings", entries = { { key = key(1), fields = { [FIELD] = 0.5 } } }, seen = 0 })
    w:step(2)
    -- The helper writes the game's stored price: every entrance follows.
    w.syncedPrices.parking = 0.5
    w:step(3)
    eq(entrance(w, 2).ratePerMinute, 0.5, "every entrance follows")
    w:take("settingrevert")
    w:step(20)
    w.syncedPrices.parking = nil
    w:clearEmitted()
    entrance(w, 2).ratePerMinute = 0.7
    w:step(3)
    local settings = w:take("setting")
    eq(#settings, 1, "the player's edit")
    eq(settings[1].fields[FIELD], 0.7, "value")
end)

test("a kind's values are listed for the helper", function()
    local w = city()
    entrance(w, 2).ratePerMinute = 0.2
    w:step(3)
    w:send({ type = "typevalue?", req = 7, kind = "parking", field = FIELD })
    local reply = w:take("typevalue")[1]
    eq(reply.req, 7, "req")
    eq(reply.count, 2, "both entrances")
    eq(reply.min, 0.06, "min")
    eq(reply.max, 0.2, "max")
end)

test("Pricing kinds are named once the helper sends the names", function()
    local w = city()
    local stall = w:spawnPlaced("Restroom", 5, 8, 5)
    w.comps.Pricing[stall] = { price = 4, syncId = w.room.getPricingId("restroom") }
    w:step(3)
    w:send({ type = "pricingids", names = { "restroom", "soda_vending" } })
    w:send({ type = "typevalue?", req = 1, kind = "restroom", field = "Pricing.price" })
    local reply = w:take("typevalue")[1]
    eq(reply.count, 1, "the restroom")
    eq(reply.min, 4, "its price")
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
print(string.format("%d of %d Lua settings tests passed", #tests - failures, #tests))
if failures > 0 then error(failures .. " Lua settings tests failed", 0) end
