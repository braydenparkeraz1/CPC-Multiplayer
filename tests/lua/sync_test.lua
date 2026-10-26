-- Tests for lua/sync.lua against the stand-in engine (tests/lua/engine.lua).
-- Run through tests/run.ps1 (scenario LuaSync), or directly: .testrun\bin\luarun\luarun.exe tests\lua\sync_test.lua
local here = (arg and arg[0] or ""):gsub("\\", "/")
local root = here:match("^(.*)/tests/lua/[^/]*$") or "."
local Engine = dofile(root .. "/tests/lua/engine.lua")

local tests, failures = {}, 0

local function test(name, fn) tests[#tests + 1] = { name = name, fn = fn } end

local function check(cond, msg)
    if not cond then error(msg or "check failed", 2) end
end

local function eq(actual, expected, what)
    if actual ~= expected then
        error(string.format("%s: expected %s, got %s", what or "value", tostring(expected), tostring(actual)), 2)
    end
end

-- A city with a few objects, loaded and indexed (past the 2 s baseline). role: "host" or "client" (nil acts as the host).
local function city(objects, role)
    local w = Engine.new(root)
    w:bootMod(false, role)
    for _, o in ipairs(objects or {}) do w:spawnPlaced(o[1], o[2], o[3], o[4], o[5], o[6]) end
    w:step(25)
    return w
end

local function placeMsg(key)
    local tpl, x, y, z, rot, var = key:match("^([%w_]+)@(%d+),(%d+),(%d+)/(%d+)/(%d+)$")
    return { type = "place", key = key, tpl = tpl, x = tonumber(x), y = tonumber(y), z = tonumber(z), rot = tonumber(rot), var = tonumber(var) }
end

local function count(list, field, value)
    local n = 0
    for _, d in ipairs(list) do if d[field] == value then n = n + 1 end end
    return n
end

local function tracked(w, key) return w.MP.room.tracked[key] ~= nil end

-- ---- reporting our player's builds ----

test("loaded city is shared and nothing is reported", function()
    local w = city({ { "Bench", 1, 1, 1 }, { "Lamp", 2, 1, 1 } })
    eq(#w:take("place"), 0, "places")
    local ready = w:take("ready")
    eq(#ready, 1, "ready")
    eq(ready[1].objects, 2, "objects")
    check(tracked(w, "Bench@1,1,1/0/0") and tracked(w, "Lamp@2,1,1/0/0"), "loaded objects tracked")
end)

test("a player build is reported with its key", function()
    local w = city()
    w:playerBuild("Bench", 5, 1, 7, 2, 1)
    w:step()
    local places = w:take("place")
    eq(#places, 1, "places")
    eq(places[1].key, "Bench@5,1,7/2/1", "key")
    eq(places[1].tpl, "Bench", "tpl")
    eq(places[1].rot, 2, "rot")
    check(tracked(w, "Bench@5,1,7/2/1"), "tracked")
end)

test("a player demolition is reported once it is seen", function()
    local w = city({ { "Bench", 1, 1, 1 } })
    w:playerDemolish(w:objectAt(1, 1, 1))
    w:step()
    eq(count(w:take("demolish"), "key", "Bench@1,1,1/0/0"), 1, "demolish")
    check(not tracked(w, "Bench@1,1,1/0/0"), "untracked")
end)

test("an object that vanishes is reported as removed after a delay", function()
    local w = city({ { "Bench", 1, 1, 1 } })
    w:vanish(w:objectAt(1, 1, 1))
    w:step(5)
    eq(#w:take("remove"), 0, "not yet")
    w:step(15)
    eq(count(w:take("remove"), "key", "Bench@1,1,1/0/0"), 1, "remove")
end)

test("an object the game re-creates straight away is not a removal", function()
    local w = city({ { "Bench", 1, 1, 1 } })
    w:vanish(w:objectAt(1, 1, 1))
    w:step()
    w:spawnPlaced("Bench", 1, 1, 1)
    w:step(30)
    eq(#w:take("remove"), 0, "removes")
    check(tracked(w, "Bench@1,1,1/0/0"), "still tracked")
end)

test("objects the game makes by itself are not shared", function()
    local w = city()
    local spill = w:spawnPlaced("Spill", 3, 1, 3)
    w:step()
    eq(#w:take("place"), 0, "places")
    check(not tracked(w, "Spill@3,1,3/0/0"), "not tracked")
    w:vanish(spill)
    w:step(30)
    eq(#w:take("remove"), 0, "its removal isn't reported")
end)

-- ---- what the game takes by itself (protests, fire) ----

test("a friend's demolition with the tool is reported at once, though it lasts one frame", function()
    local w = city({ { "Bench", 1, 1, 1 } }, "client")
    w:playerDemolish(w:objectAt(1, 1, 1))
    w:step()
    eq(count(w:take("demolish"), "key", "Bench@1,1,1/0/0"), 1, "demolish")
    check(not tracked(w, "Bench@1,1,1/0/0"), "untracked")
end)

test("the host reports what its game takes", function()
    local w = city({ { "Lamppost", 1, 1, 1 } }, "host")
    w:vanish(w:objectAt(1, 1, 1))
    w:step(20)
    eq(count(w:take("remove"), "key", "Lamppost@1,1,1/0/0"), 1, "remove")
end)

test("a friend doesn't report what its game takes, and the host's repair puts it back", function()
    local host = city({ { "Lamppost", 1, 1, 1 }, { "Bench", 2, 1, 1 } }, "host")
    local client = city({ { "Lamppost", 1, 1, 1 }, { "Bench", 2, 1, 1 } }, "client")
    client:vanish(client:objectAt(1, 1, 1))
    client:step(30)
    eq(#client:take("remove") + #client:take("demolish"), 0, "nothing reported")
    check(not tracked(client, "Lamppost@1,1,1/0/0"), "untracked here")
    check(client:digest() ~= host:digest(), "the digests show the difference")
    local bucket = client.MP.bucketOfKey("Lamppost@1,1,1/0/0")
    host:send({ type = "dump", req = 1, bucket = bucket })
    local reply = host:take("keys")[1]
    client:send({ type = "reconcile", bucket = bucket, keys = reply.keys, here = reply.here })
    client:step(10)
    eq(client:keyAt(1, 1, 1), "Lamppost@1,1,1/0/0", "back on the friend's game")
    eq(client:digest(), host:digest(), "digests match")
end)

test("a friend reports what vanishes right after its own build (cleared by it)", function()
    local w = city({ { "Oak", 6, 1, 6 } }, "client")
    w:playerBuild("House", 5, 1, 5)
    w:vanish(w:objectAt(6, 1, 6))
    w:step(20)
    eq(count(w:take("place"), "key", "House@5,1,5/0/0"), 1, "the build")
    eq(count(w:take("remove"), "key", "Oak@6,1,6/0/0"), 1, "the tree it cleared")
end)

test("protest banners are never shared", function()
    local host = city({ { "Bench", 1, 1, 1 }, { "ProtestBanner", 3, 1, 3, 0, 4 } }, "host")
    local client = city({ { "Bench", 1, 1, 1 } }, "client")
    check(not tracked(host, "ProtestBanner@3,1,3/0/4"), "not tracked")
    eq(client:digest(), host:digest(), "a banner only one game has makes no difference")
    host:vanish(host:objectAt(3, 1, 3))
    host:step(20)
    eq(#host:take("remove"), 0, "its end isn't reported")
end)

-- ---- replaying other players' builds ----

test("a friend's build is placed and confirmed", function()
    local w = city()
    w:send(placeMsg("Bench@4,1,4/1/0"))
    w:step(5)
    check(w:objectAt(4, 1, 4), "placed")
    eq(count(w:take("placed"), "key", "Bench@4,1,4/1/0"), 1, "placed message")
    eq(#w:take("place"), 0, "not reported as our build")
    check(tracked(w, "Bench@4,1,4/1/0"), "tracked")
end)

test("a friend's build that is already here is confirmed straight away", function()
    local w = city({ { "Bench", 4, 1, 4 } })
    w:send(placeMsg("Bench@4,1,4/0/0"))
    eq(count(w:take("placed"), "key", "Bench@4,1,4/0/0"), 1, "placed message")
end)

test("a friend's demolition removes the object without echoing it", function()
    local w = city({ { "Bench", 1, 1, 1 } })
    w:send({ type = "demolish", key = "Bench@1,1,1/0/0" })
    w:step(10)
    check(not w:objectAt(1, 1, 1), "gone")
    eq(#w:take("demolish") + #w:take("remove"), 0, "no echo")
    check(not tracked(w, "Bench@1,1,1/0/0"), "untracked")
end)

test("a blocked build is retried, then reported blocked", function()
    local w = city()
    w:spawnPlaced("Rock", 4, 1, 4) -- something only we have, going nowhere
    w:step()
    w:send(placeMsg("Bench@4,1,4/0/0"))
    w:step(40)
    eq(count(w:take("blocked"), "key", "Bench@4,1,4/0/0"), 1, "blocked within 4 s")
    eq(#w:take("placed"), 0, "never placed")
end)

test("a blocked build says why", function()
    local w = city()
    w:spawnPlaced("Rock", 4, 1, 4)
    w:step()
    w:send(placeMsg("Bench@4,1,4/0/0"))
    w:step(40)
    local blocked = w:take("blocked")
    eq(#blocked, 1, "blocked once")
    eq(blocked[1].reason, "PLACE_BLOCK_OCCUPIED_BY_INCOMPATIBLE_PLACEABLE", "the game's reason")
    eq(w:objectAt(4, 1, 4) ~= nil, true, "the rock is still there")
end)

test("the build tool's floor painting goes with a build and is used by the replay", function()
    local a = city()
    a:playerBuild("House", 5, 1, 7, 0, 0, true)
    a:step()
    local place = a:take("place")[1]
    eq(place.paint, true, "paint reported")
    local b = city()
    local msg = placeMsg(place.key)
    msg.paint = place.paint
    b:send(msg)
    local e = b.MP.room.placing[place.key].entity
    eq(b.comps.Placing[e].bAutoPaintFloor, true, "the replay paints the floor too")
    b:step(10)
    eq(b:keyAt(5, 1, 7), place.key, "placed")
    a:playerBuild("Bench", 9, 1, 9)
    a:step()
    eq(a:take("place")[1].paint, nil, "no setting, nothing sent")
end)

test("a blocked build waits for a removal in progress", function()
    local w = city({ { "Rock", 4, 1, 4 } })
    w.demolishAfter = 60 -- a slow demolition animation
    w:send({ type = "demolish", key = "Rock@4,1,4/0/0" })
    w.MP.room.lastRemoteRemoval = -100 -- don't let the placement just wait for the demolition to start
    w:send(placeMsg("Bench@4,1,4/0/0"))
    w:step(100)
    eq(#w:take("blocked"), 0, "not given up")
    eq(w:keyAt(4, 1, 4), "Bench@4,1,4/0/0", "placed once the rock was gone")
end)

test("the host rejects a friend's build quickly, even next to its own new build", function()
    local host = city()
    host:playerBuild("Bench", 4, 1, 4)
    host:step()
    host:take()
    local msg = placeMsg("Lamp@4,1,4/0/0")
    msg.proposal = true
    host:send(msg)
    host:step(40)
    eq(count(host:take("blocked"), "key", "Lamp@4,1,4/0/0"), 1, "rejected within 4 s")
end)

test("a blocked build goes in once the host takes back ours", function()
    -- We built on a tile; the host placed something else there first. Its build reaches us
    -- (blocked by ours), then the host's undo of ours arrives: the retry gets it in.
    local w = city()
    w:playerBuild("Lamp", 4, 1, 4)
    w:step()
    w:take()
    w:send(placeMsg("Bench@4,1,4/0/0"))
    -- The host needs a few seconds to decide; we keep retrying, since our own new build may be in the way.
    w:step(60)
    eq(#w:take("blocked"), 0, "still trying")
    w:send({ type = "undo", key = "Lamp@4,1,4/0/0" })
    w:step(40)
    eq(w:keyAt(4, 1, 4), "Bench@4,1,4/0/0", "the host's build")
    eq(#w:take("demolish") + #w:take("remove"), 0, "the undo isn't echoed")
    eq(#w:take("blocked"), 0, "not blocked")
    check(not tracked(w, "Lamp@4,1,4/0/0"), "ours untracked")
end)

test("a demolition cancels a placement still waiting for a retry", function()
    local w = city()
    w:spawnPlaced("Rock", 4, 1, 4)
    w:step()
    w:send(placeMsg("Bench@4,1,4/0/0"))
    w:step(5)
    w:send({ type = "demolish", key = "Bench@4,1,4/0/0" })
    w:vanish(w:objectAt(4, 1, 4))
    w:step(100)
    check(not w:objectAt(4, 1, 4), "the cancelled build never went in")
end)

test("builds arriving while the city loads wait until it is indexed", function()
    local w = Engine.new(root)
    w:bootMod()
    w:send(placeMsg("Bench@4,1,4/0/0"))
    w:step(5)
    check(not w:objectAt(4, 1, 4), "not yet")
    w:step(30)
    check(w:objectAt(4, 1, 4), "placed after ready")
end)

-- ---- digest and repair ----

test("the digest counts shared objects and says when it is busy", function()
    local w = city({ { "Bench", 1, 1, 1 } })
    w:take()
    w:step(20)
    local digests = w:take("digest")
    check(#digests >= 1, "digest sent")
    local total = 0
    for _, c in ipairs(digests[#digests].counts) do total = total + c end
    eq(total, 1, "objects in digest")
    eq(digests[#digests].busy, false, "idle")
    w.finishAfter = 50 -- a slow placement, still running at the next digest
    w:send(placeMsg("Bench@4,1,4/0/0"))
    w:step(20)
    local busy = w:take("digest")
    eq(busy[#busy].busy, true, "busy while placing")
    eq(busy[#busy].msg, w.MP.lastMsgId, "reports the last message it ran")
end)

test("the digest is the same for the same objects, whatever the order", function()
    local a = city({ { "Bench", 1, 1, 1 }, { "Lamp", 2, 1, 1 }, { "Tree", 9, 1, 3, 1, 2 } })
    local b = city({ { "Tree", 9, 1, 3, 1, 2 }, { "Bench", 1, 1, 1 }, { "Lamp", 2, 1, 1 } })
    eq(a:digest(), b:digest(), "digest")
    a:playerBuild("Sign", 7, 1, 7)
    a:step()
    check(a:digest() ~= b:digest(), "differs after a build")
    b:send(placeMsg("Sign@7,1,7/0/0"))
    b:step(5)
    eq(a:digest(), b:digest(), "same again")
end)

test("the digest encodes as JSON arrays", function()
    local w = city()
    local json = w.MP.jsonEncode({ counts = w.MP.room.counts })
    check(json:find('^{"counts":%[0,0,'), json:sub(1, 40))
end)

test("dump lists the keys of one bucket", function()
    local w = city({ { "Bench", 1, 1, 1 }, { "Lamp", 2, 1, 1 } })
    local bucket = w.MP.bucketOfKey("Bench@1,1,1/0/0")
    w:send({ type = "dump", req = 7, bucket = bucket })
    local keys = w:take("keys")
    eq(#keys, 1, "answers")
    eq(keys[1].req, 7, "req")
    local found = false
    for _, k in ipairs(keys[1].keys) do
        if k == "Bench@1,1,1/0/0" then found = true end
        eq(w.MP.bucketOfKey(k), bucket, "bucket of " .. k)
    end
    check(found, "bench listed")
end)

test("reconcile adds what's missing and takes out what the host doesn't have", function()
    local host = city({ { "Bench", 1, 1, 1 } })
    local client = city({ { "Bench", 1, 1, 1 } })
    -- The client got a build the host never had (its undo got lost), and missed one of the host's.
    client:send(placeMsg("Lamp@3,1,3/0/0"))
    client:step(5)
    host:send(placeMsg("Sign@6,1,6/0/0"))
    host:step(5)
    -- Repair every bucket, as the helper would for the ones that differ.
    local fixed = 0
    for bucket = 0, 63 do
        host:send({ type = "dump", req = bucket, bucket = bucket })
        local keys = host:take("keys")[1].keys
        client:send({ type = "reconcile", bucket = bucket, keys = keys })
        local r = client:take("reconciled")[1]
        fixed = fixed + r.placed + r.removed
    end
    client:step(20)
    eq(fixed, 2, "fixes")
    check(not client:objectAt(3, 1, 3), "extra taken out")
    check(client:objectAt(6, 1, 6), "missing added")
    eq(client:digest(), host:digest(), "digests match")
end)

test("reconcile keeps our newest build", function()
    local host = city()
    local client = city()
    client:playerBuild("Bench", 2, 1, 2)
    client:step()
    local bucket = client.MP.bucketOfKey("Bench@2,1,2/0/0")
    client:send({ type = "reconcile", bucket = bucket, keys = {} })
    client:step(10)
    check(client:objectAt(2, 1, 2), "kept")
    client:step(100)
    client:send({ type = "reconcile", bucket = bucket, keys = {} })
    client:step(10)
    check(not client:objectAt(2, 1, 2), "taken out once it's old and the host still doesn't have it")
end)

-- ---- edge cases found in review ----

test("an object re-created in the same tick is not a removal", function()
    local w = city({ { "Bench", 1, 1, 1 } })
    w:vanish(w:objectAt(1, 1, 1))
    w:spawnPlaced("Bench", 1, 1, 1)
    w:step(30)
    eq(#w:take("remove"), 0, "removes")
    check(tracked(w, "Bench@1,1,1/0/0"), "still tracked")
end)

test("an undo while the build waits for a retry leaves nothing behind", function()
    local w = city()
    w:spawnPlaced("Rock", 4, 1, 4)
    w:step()
    w:send(placeMsg("Bench@4,1,4/0/0"))
    w:step(5) -- blocked once, retry waiting
    w:send({ type = "undo", key = "Bench@4,1,4/0/0" })
    w:step(25)
    local digest = w:take("digest")
    eq(digest[#digest].busy, false, "not busy afterwards")
    w:vanish(w:objectAt(4, 1, 4))
    w:step(20)
    w:send(placeMsg("Bench@4,1,4/0/0"))
    for i = 1, 30 do
        w:step()
        if i > 5 then check(w:objectAt(4, 1, 4), "stays in once placed (tick " .. i .. ")") end
    end
end)

test("a build the game can't make is reported blocked", function()
    local w = city()
    local ok = pcall(w.MP.handlers.place, placeMsg("Missing@4,1,4/0/0"))
    check(not ok, "the error goes on to the inbox")
    eq(count(w:take("blocked"), "key", "Missing@4,1,4/0/0"), 1, "blocked")
end)

test("a placement the game never finishes is given up", function()
    local w = city()
    w.finishAfter = 100000
    w:send(placeMsg("Bench@4,1,4/0/0"))
    w:step(150)
    eq(count(w:take("blocked"), "key", "Bench@4,1,4/0/0"), 1, "blocked")
    w:step(20)
    local digest = w:take("digest")
    eq(digest[#digest].busy, false, "not busy any more")
end)

test("objects with keys the helper can't carry stay local", function()
    local w = city({ { "Odd-Name", 1, 1, 1 } })
    check(not tracked(w, "Odd-Name@1,1,1/0/0"), "not tracked")
end)

test("objects the host has but doesn't share stay on a joiner", function()
    -- The host's game made a spill after loading; the joiner loaded it with the save.
    local host = city({ { "Bench", 1, 1, 1 } })
    host:spawnPlaced("Spill", 3, 1, 3)
    host:step()
    local client = city({ { "Bench", 1, 1, 1 }, { "Spill", 3, 1, 3 } })
    check(tracked(client, "Spill@3,1,3/0/0") and not tracked(host, "Spill@3,1,3/0/0"), "they disagree at first")
    local bucket = client.MP.bucketOfKey("Spill@3,1,3/0/0")
    host:send({ type = "dump", req = 1, bucket = bucket })
    local reply = host:take("keys")[1]
    client:send({ type = "reconcile", bucket = bucket, keys = reply.keys, here = reply.here })
    client:step(10)
    check(client:objectAt(3, 1, 3), "the spill stays")
    eq(client:take("reconciled")[1].removed, 0, "nothing taken out")
    eq(client:digest(), host:digest(), "digests match")
end)

-- ---- two players racing for one tile ----

test("host and client building on the same tile end up with the host's", function()
    local host = city()
    local client = city()
    host:playerBuild("Bench", 4, 1, 4)
    client:playerBuild("Lamp", 4, 1, 4)
    host:step()
    client:step()
    local hostBuild = host:take("place")[1]
    local clientBuild = client:take("place")[1]
    -- The client's build reaches the host: blocked there, so the host takes it back everywhere.
    local proposal = placeMsg(clientBuild.key)
    proposal.proposal = true
    host:send(proposal)
    -- Meanwhile the host's build reaches the client: blocked by the client's own, for now.
    client:send(placeMsg(hostBuild.key))
    local rejected = false
    for _ = 1, 60 do
        host:step()
        client:step()
        if count(host:take("blocked"), "key", clientBuild.key) > 0 then rejected = true break end
    end
    check(rejected, "host rejects the client's build")
    client:send({ type = "undo", key = clientBuild.key })
    client:step(40)
    eq(client:keyAt(4, 1, 4), hostBuild.key, "client has the host's build")
    eq(host:keyAt(4, 1, 4), hostBuild.key, "host kept its own")
    client:step(20)
    eq(client:digest(), host:digest(), "digests match")
end)

-- ---- run ----

for _, t in ipairs(tests) do
    local ok, err = pcall(t.fn)
    if ok then
        print("    ok: " .. t.name)
    else
        failures = failures + 1
        print("    FAIL: " .. t.name .. ": " .. tostring(err))
    end
end
print(string.format("%d of %d Lua tests passed", #tests - failures, #tests))
if failures > 0 then error(failures .. " Lua tests failed", 0) end
