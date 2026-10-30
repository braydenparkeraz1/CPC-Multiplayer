-- A stand-in for the parts of Car Park Capital's Lua API the mod uses, enough to run lua/hooks.lua
-- and lua/sync.lua outside the game. One Engine is one PC's open city.
--
-- What it models, as far as the mod can tell:
--   * entities with components (Placeable, Placed, Placing, Persistent, Demolished)
--   * a scripted placement (Placing + applyTemplate) finishing a couple of ticks later: the Placing
--     entity goes away and the placed object is created by the template, or it is blocked when its
--     tile is taken (objects here are one tile big)
--   * the build tool: a preview ghost (Placing without Persistent) next to the placed object
--   * a demolition (Demolished) removing the object a few ticks later; the demolish tool's, as measured in
--     the game, only lasts one frame (timeUntilDestroy 0) before the object is destroyed
--   * the frame callback (MP.onFrame) once per tick
--   * prices (ParkingEntry, Pricing) and "apply to all": with syncedPrices[kind] set, every update
--     copies it onto every object of that kind, as the game does with money.syncedPrices
--   * people and their cars (Person, Vehicle, VehicleOwner, OwnedVehicle; Transform while in town),
--     names (getName/setName/getByName), and vehicle templates, which only give a Vehicle component
--   * car carousels (VehicleShop, Pricing, GoodsStorage with 10 car slots) and system.Goods as measured in the
--     game: popItemFromStorage takes the car in the last full slot and leaves it standing, pushItemToStorage
--     puts a car in the first empty slot; tryBuy hands the buyer a car from the stock and counts the sale
-- Not modelled: paint jobs finish instantly as "ok" if the tile is free.
local Engine = {}
Engine.__index = Engine

local COMPONENTS = { "Placeable", "Placed", "Placing", "Persistent", "Demolished", "ParkingEntry", "Pricing",
    "Person", "Vehicle", "VehicleOwner", "OwnedVehicle", "Transform", "Driver", "VehiclePassenger",
    "VehicleShop", "GoodsStorage", "InStorage" }

local VEHICLE_ITEM = 2
local CAROUSEL_SLOTS = 10

-- Templates that make a car rather than a placeable.
local VEHICLES = { Cabriolet = true, BananaCar = true, BubbleCabin = true }

function Engine.new(root)
    local w = setmetatable({}, Engine)
    w.root = root
    w.tick = 0
    w.nextEntity = 1
    w.alive = {}
    w.comps = {}
    for _, name in ipairs(COMPONENTS) do w.comps[name] = {} end
    w.templates = {}
    w.nextTemplate = 1
    w.placingJobs = {}   -- entity -> { tpl, at }
    w.demolishing = {}   -- entity -> tick
    w.emitted = {}
    w.syncedPrices = {}  -- kind -> price, for kinds with "apply to all" ticked
    w.finishAfter = 2
    w.demolishAfter = 3
    w.names = {}         -- entity -> name
    w.logs = {}
    w.room = w:makeRoom()
    return w
end

function Engine:templateOf(tpl)
    if not self.templates[tpl] then
        self.templates[tpl] = self.nextTemplate
        self.nextTemplate = self.nextTemplate + 1
    end
    return self.templates[tpl]
end

function Engine:create()
    local e = self.nextEntity
    self.nextEntity = e + 1
    self.alive[e] = true
    return e
end

function Engine:destroy(e)
    self.alive[e] = nil
    self.names[e] = nil
    for _, name in ipairs(COMPONENTS) do self.comps[name][e] = nil end
    self.placingJobs[e] = nil
    self.demolishing[e] = nil
end

local function defaults(name)
    if name == "Placing" then
        return { at = { tilePosition = { x = 0, y = 0, z = 0 }, rotation = 0, variantId = 0 } }
    end
    return {}
end

function Engine:componentApi(name)
    local w = self
    local store = self.comps[name]
    if not store then error("unknown component " .. name) end
    return {
        has = function(e) return store[e] ~= nil end,
        getFor = function(e)
            local data = store[e]
            if not data then
                data = defaults(name)
                store[e] = data
                if name == "Demolished" then w.demolishing[e] = w.tick end
            end
            return data
        end,
        remove = function(e) store[e] = nil end,
    }
end

function Engine:set(e, comp)
    self.comps[comp.name][e] = comp.data
    if comp.name == "Demolished" then self.demolishing[e] = self.tick end
end

function Engine:makeRoom()
    local w = self
    local room = {}
    room.component = setmetatable({}, { __index = function(t, name)
        local api = w:componentApi(name)
        rawset(t, name, api)
        return api
    end })
    room.valid = function(e) return w.alive[e] == true end
    room.createEntity = function() return w:create() end
    room.destroyEntity = function(e) w:destroy(e) end
    room.uvec3 = function(x, y, z) return { x = x, y = y, z = z } end
    room.applyTemplate = function(e, tpl, args, persistent) w:applyTemplate(e, tpl, persistent) end
    room.setComponents = function(e, list) for _, c in ipairs(list) do w:set(e, c) end end
    room.setComponent = function(e, c) w:set(e, c) end
    for _, name in ipairs(COMPONENTS) do
        room[name] = function(data) return { name = name, data = data or {} } end
    end
    room.onEvent = function() end
    room.setOnDestroyCallback = function() end
    room.tileMapSize = { x = 64, y = 8, z = 64 }
    room.getPricingId = function(name) return w:templateOf("pricing:" .. name) end
    -- tryBuy: a carousel (spawnCarousel) hands over a car from its stock and counts the sale; any other
    -- shop entity stands for one that always has stock, and the buyer gets a new car.
    w.sold = 0
    room.currentRoom = { items = { VEHICLE = VEHICLE_ITEM } }
    room.system = { VehicleShop = { tryBuy = function(person, shop)
        local v
        if w.comps.GoodsStorage[shop] then
            v = w:lastInStock(shop)
            if not v then return false end
            w:popStock(shop)
            local pricing = w.comps.Pricing[shop]
            pricing.numSold = pricing.numSold + 1
            pricing.moneyMade = pricing.moneyMade + pricing.price
        else
            v = w:create()
            w:applyTemplate(v, "Cabriolet", true)
        end
        w.comps.VehicleOwner[person] = { vehicle = v }
        w.comps.OwnedVehicle[v] = { owner = person }
        w.sold = w.sold + 1
        return true
    end } }
    room.system.Goods = {
        getStorageCount = function(e, item)
            local n = 0
            for _ in pairs(w:slots(e)) do n = n + 1 end
            return n
        end,
        popItemFromStorage = function(e, item) return w:popStock(e) end,
        pushItemToStorage = function(e, item, car) return w:pushStock(e, car) end,
    }
    room.getName = function(e) return w.names[e] end
    room.setName = function(e, name)
        for other, n in pairs(w.names) do
            if n == name and other ~= e then return false end
        end
        w.names[e] = name
        return true
    end
    room.getByName = function(name)
        for e, n in pairs(w.names) do
            if n == name then return e end
        end
        return nil
    end
    return setmetatable(room, { __index = _G })
end

-- What the game does for applyTemplate on an entity with Placing: the template's create() gives it
-- its Placeable (through setComponents, which the mod's hook watches), and the placement finishes later.
function Engine:applyTemplate(e, tpl, persistent)
    if tpl == "Missing" then error("no template named Missing") end
    if persistent then
        self.comps.Persistent[e] = { applyTemplateOnLoad = tpl }
    end
    if VEHICLES[tpl] then
        -- Vehicle.lua: the Vehicle component first, then the rest through setComponents.
        self.comps.Vehicle[e] = {}
        self.room.setComponents(e, {})
        return
    end
    self.room.setComponents(e, { self.room.Placeable { templateIndex = self:templateOf(tpl) } })
    self.placingJobs[e] = { tpl = tpl, at = self.tick }
end

function Engine:objectAt(x, y, z)
    for e, placed in pairs(self.comps.Placed) do
        local p = placed.tilePosition
        if self.alive[e] and p.x == x and p.y == y and p.z == z then return e end
    end
    return nil
end

-- A placed object as the game creates it: by its template, through setComponents.
function Engine:spawnPlaced(tpl, x, y, z, rot, var)
    local e = self:create()
    self.room.setComponents(e, {
        self.room.Placeable { templateIndex = self:templateOf(tpl) },
        self.room.Persistent { applyTemplateOnLoad = tpl },
        self.room.Placed { tilePosition = { x = x, y = y, z = z }, rotation = rot or 0, variantId = var or 0 },
    })
    return e
end

-- A person (out of town: no Transform), as loaded from a save; with a car of the given template, if any.
function Engine:spawnPerson(name, carTemplate)
    local p = self:create()
    self.comps.Person[p] = {}
    self.comps.Persistent[p] = { applyTemplateOnLoad = "Person" }
    self.names[p] = name
    if carTemplate then
        local v = self:create()
        self.room.applyTemplate(v, carTemplate, {}, true)
        self.comps.VehicleOwner[p] = { vehicle = v }
        self.comps.OwnedVehicle[v] = { owner = p }
        self.names[v] = name .. "'s vehicle"
    end
    return p
end

-- The person's car (entity) and its template, or nil.
function Engine:carOf(name)
    local p = self.room.getByName(name)
    local owner = p and self.comps.VehicleOwner[p]
    if not owner or not self.alive[owner.vehicle] then return nil end
    return owner.vehicle, self.comps.Persistent[owner.vehicle].applyTemplateOnLoad
end

-- A car carousel holding cars of these kinds (in its first slots), as loaded from a save.
function Engine:spawnCarousel(x, y, z, stock)
    local e = self:spawnPlaced("CarCarousel", x, y, z, 0, 0)
    self.comps.VehicleShop[e] = {}
    self.comps.Pricing[e] = { price = 220, numSold = 0, moneyMade = 0 }
    local slots = setmetatable({}, { __len = function() return CAROUSEL_SLOTS end })
    self.comps.GoodsStorage[e] = { entitiesPerItem = { [VEHICLE_ITEM] = slots } }
    for _, tpl in ipairs(stock or {}) do self:deliver(e, tpl) end
    return e
end

function Engine:slots(shop)
    return self.comps.GoodsStorage[shop].entitiesPerItem[VEHICLE_ITEM]
end

-- A car-carrier truck brings a new car of this kind.
function Engine:deliver(shop, tpl)
    local car = self:create()
    self.room.applyTemplate(car, tpl, {}, true)
    self.comps.Transform[car] = {}
    return self:pushStock(shop, car)
end

function Engine:pushStock(shop, car)
    local slots = self:slots(shop)
    for i = 1, CAROUSEL_SLOTS do
        if slots[i] == nil then
            slots[i] = car
            self.comps.InStorage[car] = { storage = shop }
            return true
        end
    end
    return false
end

function Engine:lastInStock(shop)
    local slots = self:slots(shop)
    for i = CAROUSEL_SLOTS, 1, -1 do
        if slots[i] ~= nil then return slots[i], i end
    end
    return nil
end

function Engine:popStock(shop)
    local car, i = self:lastInStock(shop)
    if not car then return false end
    self:slots(shop)[i] = nil
    self.comps.InStorage[car] = nil
    return true
end

-- The kinds of car in the carousel's stock, sorted, as one string.
function Engine:stockOf(shop)
    local kinds = {}
    for _, car in pairs(self:slots(shop)) do kinds[#kinds + 1] = self.comps.Persistent[car].applyTemplateOnLoad end
    table.sort(kinds)
    return table.concat(kinds, ",")
end

-- Cars standing in the world that are in no carousel and nobody's (left over from a pop).
function Engine:strayCars()
    local n = 0
    for v in pairs(self.comps.Vehicle) do
        if self.alive[v] and not self.comps.InStorage[v] and not self.comps.OwnedVehicle[v] then n = n + 1 end
    end
    return n
end

-- Our player builds something with the build tool: its ghost is there when the object is made.
-- paint: the tool's "paint the floor" setting (bAutoPaintFloor), if given.
function Engine:playerBuild(tpl, x, y, z, rot, var, paint)
    local ghost = self:create()
    self.room.setComponents(ghost, {
        self.room.Placeable { templateIndex = self:templateOf(tpl) },
        self.room.Placing { at = { tilePosition = { x = x, y = y, z = z }, rotation = rot or 0, variantId = var or 0 }, bTryFinish = false,
            bAutoPaintFloor = paint },
    })
    local e = self:spawnPlaced(tpl, x, y, z, rot, var)
    self:destroy(ghost)
    return e
end

-- Our player demolishes something with the demolish tool: the game marks it Demolished for one frame,
-- then destroys it, between two of the mod's ticks.
function Engine:playerDemolish(e)
    self.comps.Demolished[e] = { timeUntilDestroy = 0 }
    if self.MP and self.MP.onFrame then self.MP.onFrame() end
    self:destroy(e)
end

-- The game removes an object without a demolition (protest damage, fire, sold, cleaned up, replaced).
function Engine:vanish(e)
    self:destroy(e)
end

function Engine:update()
    self.tick = self.tick + 1
    for e, entry in pairs(self.comps.ParkingEntry) do
        if self.syncedPrices.parking then entry.ratePerMinute = self.syncedPrices.parking end
    end
    for e, job in pairs(self.placingJobs) do
        if not self.alive[e] then
            self.placingJobs[e] = nil
        elseif self.tick - job.at >= self.finishAfter then
            self.placingJobs[e] = nil
            local placing = self.comps.Placing[e]
            local pos = placing.at.tilePosition
            if job.tpl:find("^MPPaint") then
                self:destroy(e)
            elseif self:objectAt(pos.x, pos.y, pos.z) then
                placing.bEvaluated = true
                placing.bBlocked = true
                placing.blockedReasonStr = "PLACE_BLOCK_OCCUPIED_BY_INCOMPATIBLE_PLACEABLE"
                if placing.bDestroyOnBlockedFinish then self:destroy(e) end
            else
                self:destroy(e)
                self:spawnPlaced(job.tpl, pos.x, pos.y, pos.z, placing.at.rotation, placing.at.variantId)
            end
        end
    end
    for e, at in pairs(self.demolishing) do
        if self.tick - at >= self.demolishAfter then self:destroy(e) end
    end
end

-- Loads the mod the way the game does in a level: the hook while templates load, then sync
-- (and settings, with withSettings).
function Engine:bootMod(withSettings, role)
    local function load(name, ...)
        local chunk = assert(loadfile(self.root .. "/lua/" .. name .. ".lua", "t", self.room))
        return chunk(...)
    end
    load("hooks", self.room, "test")
    local MP = { handlers = {}, lastMsgId = 0, hostEntity = self:create(), env = self.room, context = "level", role = role }
    MP.log = function(...) self.logs[#self.logs + 1] = table.concat({ ... }, " ") end
    MP.room = MP_ROOMS[self.room]
    load("util", MP)
    MP.emit = function(t, d) self.emitted[#self.emitted + 1] = { t = t, d = d } end
    load("sync", MP)
    if withSettings then load("settings", MP) end
    load("cars", MP)
    self.MP = MP
    return MP
end

-- One mod tick (0.1 s of game time).
function Engine:step(n)
    for _ = 1, n or 1 do
        self:update()
        if self.MP.onFrame then self.MP.onFrame() end
        self.MP.onTick()
    end
end

-- Delivers a message from the helper, as the inbox would.
function Engine:send(msg)
    self.MP.lastMsgId = self.MP.lastMsgId + 1
    msg.id = self.MP.lastMsgId
    local handler = assert(self.MP.handlers[msg.type], "no handler for " .. tostring(msg.type))
    handler(msg)
end

-- Messages the mod emitted since the last call (all, or of one type), as a list of data tables.
function Engine:take(t)
    local picked, rest = {}, {}
    for _, m in ipairs(self.emitted) do
        if t == nil or m.t == t then picked[#picked + 1] = m.d else rest[#rest + 1] = m end
    end
    self.emitted = rest
    return picked
end

function Engine:clearEmitted()
    self.emitted = {}
end

function Engine:keyAt(x, y, z)
    local e = self:objectAt(x, y, z)
    if not e then return nil end
    local placed = self.comps.Placed[e]
    return string.format("%s@%d,%d,%d/%d/%d", self.comps.Persistent[e].applyTemplateOnLoad, x, y, z, placed.rotation, placed.variantId)
end

-- The mod's latest digest as one comparable string.
function Engine:digest()
    local S = self.MP.room
    local parts = {}
    for i = 1, #S.counts do parts[i] = S.counts[i] .. ":" .. S.sums[i] end
    return table.concat(parts, " ")
end

return Engine
