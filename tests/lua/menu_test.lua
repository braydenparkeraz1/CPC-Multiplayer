-- Tests for lua/menu.lua (the main menu's Host/Join buttons) against a stand-in for the few engine
-- calls it uses: entities with a camera-relative transform, the cursor, the screen size, clicks.
-- Run through tests/run.ps1 (scenario LuaMenu), or directly: .testrun\bin\luarun\luarun.exe tests\lua\menu_test.lua
local here = (arg and arg[0] or ""):gsub("\\", "/")
local root = here:match("^(.*)/tests/lua/[^/]*$") or "."

local tests, failures = {}, 0

local function test(name, fn) tests[#tests + 1] = { name = name, fn = fn } end

local function eq(actual, expected, what)
    if actual ~= expected then
        error(string.format("%s: expected %s, got %s", what or "value", tostring(expected), tostring(actual)), 2)
    end
end

-- A main menu: its entity and a 1920x1080 screen (newWorld adds the camera).
local function newMenu()
    local w = { entities = {}, nextId = 1, updates = {}, clicks = {}, emitted = {}, cursor = { x = 0, y = 0 },
        screen = { x = 1920, y = 1080 } }
    local function component(name)
        return { getFor = function(e) return w.entities[e] and w.entities[e].comps[name] end }
    end
    local env = setmetatable({}, { __index = _G })
    env.valid = function(e) return w.entities[e] ~= nil end
    env.createChild = function(parent, name)
        local id = w.nextId
        w.nextId = id + 1
        w.entities[id] = { name = name, parent = parent, comps = {} }
        return id
    end
    env.getChild = function(parent, name)
        for id, e in pairs(w.entities) do
            if e.parent == parent and e.name == name then return id end
        end
        return nil
    end
    env.destroyEntity = function(e) w.entities[e] = nil end
    local function struct(kind) return function(t) t = t or {}; t.kind = kind; return t end end
    env.Transform = struct("Transform")
    env.TransformChild = struct("TransformChild")
    env.RenderModel = struct("RenderModel")
    env.MaterialOverride = struct("MaterialOverride")
    env.CameraPerspective = struct("CameraPerspective")
    env.setComponents = function(e, list) for _, c in ipairs(list) do w.entities[e].comps[c.kind] = c end end
    env.setComponent = function(e, c) w.entities[e].comps[c.kind] = c end
    env.vec3 = function(x, y, z) return { x = x, y = y or x, z = z or x } end
    env.quat = { new = function() return {} end }
    env.loadModels = function() end
    env.getScreenSize = function() return w.screen end
    env.getCursorPos = function() return w.cursor end
    env.component = setmetatable({}, { __index = function(_, name) return component(name) end })
    env.setUpdateFunction = function(e, interval, fn) w.updates[#w.updates + 1] = fn end
    env.setTimeout = function(e, delay, fn) fn() end
    env.onEvent = function(name, fn) if name == "Click" then w.clicks[#w.clicks + 1] = fn end end
    w.env = env
    w.menu = env.createChild(nil, "Menu")
    w.MP = { generation = 1, hostEntity = w.menu, handlers = {}, env = env }
    w.MP.emit = function(t, d) w.emitted[#w.emitted + 1] = { t = t, d = d } end
    return w
end

local World = {}
World.__index = World
setmetatable(World, World)

function World:addCamera()
    local cam = self.env.createChild(self.menu, "cam")
    self.env.setComponents(cam, { self.env.CameraPerspective { fieldOfView = 8 } })
    self.cam = cam
end

function World:boot()
    local chunk = assert(loadfile(root .. "/lua/menu.lua", "t", self.env))
    chunk(self.MP)
end

function World:frame() for _, fn in ipairs(self.updates) do fn(0.016) end end

function World:click(x, y)
    self.cursor = { x = x, y = y }
    for _, fn in ipairs(self.clicks) do fn(true) end
end

function World:count(model)
    local n = 0
    for _, e in pairs(self.entities) do
        local rm = e.comps.RenderModel
        if rm and (model == nil or rm.modelName == model) then n = n + 1 end
    end
    return n
end

-- The menu's camera is a child named "cam", made a moment after the menu boots the mod.
local function newWorld(withCamera)
    local w = setmetatable(newMenu(), World)
    if withCamera ~= false then w:addCamera() end
    return w
end

-- Lines without an H or a J, so those letters are the buttons'.
local HOST_JOIN = { lines = { "Multiplayer - ready", "Copy, paste: play" },
    buttons = { { id = "host", label = "Host" }, { id = "join", label = "Join" } } }

-- With the menu camera's 8 degree view at 85 units, the 1080 px screen is 11.89 units tall:
-- 90.85 px per unit, centre (960, 540). The buttons' row is 4 + gap 1.6 + 4 letters of 0.75,
-- centred: Host spans x -3.8..-0.8, Join 0.8..3.8, both y -1.75..-1.02.
local PX = 540 / (85 * math.tan(math.rad(4)))
local function screen(x, y) return 960 + x * PX, 540 - y * PX end

test("the helper's menu becomes letters and their shadows", function()
    local w = newWorld()
    w:boot()
    w.MP.handlers.menu(HOST_JOIN)
    -- "HOST" and "JOIN": 8 letters, each with a shadow.
    eq(w:count("H_Text"), 2, "H and its shadow")
    eq(w:count("J_Text"), 2, "J and its shadow")
    -- ',' becomes a gap, ':' is two dots (one more than its character).
    local letters = #(("MULTIPLAYER - READY" .. "COPY, PASTE: PLAY"):gsub("[ ,]", "")) + 1 + 8
    eq(w:count(), letters * 2, "every letter has a shadow")
end)

test("a click on a button is sent to the helper, a click elsewhere isn't", function()
    local w = newWorld()
    w:boot()
    w.MP.handlers.menu(HOST_JOIN)
    w:click(screen(-2.3, -1.4))
    w:click(screen(2.3, -1.4))
    w:click(screen(0, -1.4))        -- the gap between them
    w:click(screen(-2.3, 2))        -- above
    eq(#w.emitted, 2, "clicks sent")
    eq(w.emitted[1].d.click, "host", "first")
    eq(w.emitted[2].d.click, "join", "second")
end)

test("hovering a button turns it white and bigger, and back", function()
    local w = newWorld()
    w:boot()
    w.MP.handlers.menu(HOST_JOIN)
    local function hostLetter()
        for _, e in pairs(w.entities) do
            local rm = e.comps.RenderModel
            if rm and rm.modelName == "H_Text" and e.comps.TransformChild.offset.position.z == -85 then return e end
        end
    end
    local function diffuse(e) return e.comps.RenderModel.materialOverrides.reserved_0.diffuse end
    eq(diffuse(hostLetter()).z, 0.15, "yellow at first")
    w.cursor = { x = select(1, screen(-2.3, -1.4)), y = select(2, screen(-2.3, -1.4)) }
    w:frame()
    eq(diffuse(hostLetter()).z, 1.0, "white when hovered")
    eq(hostLetter().comps.TransformChild.offset.scale.x > 1, true, "bigger when hovered")
    w.cursor = { x = 10, y = 10 }
    w:frame()
    eq(diffuse(hostLetter()).z, 0.15, "yellow again")
    eq(math.abs(hostLetter().comps.TransformChild.offset.scale.x - 1) < 1e-9, true, "normal size again")
end)

test("punctuation the letters lack gets a stand-in, other characters a gap", function()
    local w = newWorld()
    w:boot()
    w.MP.handlers.menu({ lines = { "1.2:3-4 ?!" }, buttons = {} })
    eq(w:count("1_Text") + w:count("2_Text") + w:count("3_Text") + w:count("4_Text"), 8, "digits")
    -- '.' is one small O, ':' two, '-' a flattened I; '?' and '!' are left out.
    eq(w:count("O_Text"), 6, "dots")
    eq(w:count("I_Text"), 2, "dash")
end)

test("a new menu replaces the old one, and only the newest load handles clicks", function()
    local w = newWorld()
    w:boot()
    w.MP.handlers.menu(HOST_JOIN)
    local before = w:count()
    w.MP.handlers.menu({ lines = {}, buttons = { { id = "leave", label = "Stop" } } })
    eq(w:count("H_Text"), 0, "old letters gone")
    eq(w:count("S_Text"), 2, "new button")
    -- The main menu opens again: menu.lua loads again in the same game.
    w:boot()
    w.MP.handlers.menu(HOST_JOIN)
    w:click(screen(-2.3, -1.4))
    eq(#w.emitted, 1, "one click sent, not one per load")
    eq(before > 0, true, "sanity")
end)

test("a menu that arrives before the camera exists shows once it does", function()
    local w = newWorld(false)
    w:boot()
    w.MP.handlers.menu(HOST_JOIN)
    eq(w:count(), 0, "nothing without a camera")
    w:addCamera()
    w:frame()
    eq(w:count("H_Text"), 2, "shown on the next frame")
end)

test("a row too wide for the screen is drawn smaller, and its buttons still work", function()
    -- A 720p window with Host, Join and two games found on the network (the longest labels the
    -- helper makes): 38 letters and three gaps would be 33.3 units, the screen is 21.1 wide.
    local w = newWorld()
    w.screen = { x = 1280, y = 720 }
    w:boot()
    w.MP.handlers.menu({ lines = {}, buttons = { { id = "host", label = "Host" }, { id = "join", label = "Join" },
        { id = "lan:192.168.1.20:27777", label = "Join ABCDEFGHIJ" }, { id = "lan:10.0.0.5:27777", label = "Join KLMNOPQRST" } } })
    local px = 360 / (85 * math.tan(math.rad(4)))
    local function at(x, y) return 640 + x * px, 360 - y * px end
    local first, last = w.MP.menu.buttons[1], w.MP.menu.buttons[4]
    local left = at(first.x0, 0)
    local right = at(last.x1, 0)
    eq(left > 0 and right < 1280, true, "row inside the screen")
    eq(math.abs((640 - left) - (right - 640)) < 1, true, "row centred")
    local top = select(2, at(0, first.y1))
    local bottom = select(2, at(0, first.y0))
    -- Same baseline as a full-size row, so it stays clear of the engine's panel below.
    eq(math.abs(bottom - select(2, at(0, -1.75))) < 1e-6, true, "baseline kept")
    eq(bottom - top < 0.73 * px and bottom - top > 0.4 * 0.73 * px, true, "smaller, not tiny")
    local lx, ly = at((last.x0 + last.x1) / 2, (last.y0 + last.y1) / 2)
    w:click(lx, ly)
    local hx, hy = at((first.x0 + first.x1) / 2, (first.y0 + first.y1) / 2)
    w:click(hx, hy)
    eq(#w.emitted, 2, "clicks sent")
    eq(w.emitted[1].d.click, "lan:10.0.0.5:27777", "found game")
    eq(w.emitted[2].d.click, "host", "host")
end)

test("a row that fits keeps its full size", function()
    local w = newWorld()
    w.screen = { x = 1280, y = 720 }
    w:boot()
    w.MP.handlers.menu({ lines = {}, buttons = { { id = "host", label = "Host" }, { id = "join", label = "Join" },
        { id = "lan:192.168.1.20:27777", label = "Join Hosty" } } })
    local b = w.MP.menu.buttons[1]
    eq(math.abs(b.y1 - b.y0 - 0.73) < 1e-9, true, "full height")
    eq(math.abs(w.MP.menu.buttons[2].x0 - b.x1 - 1.6) < 1e-9, true, "full gap")
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
