-- Car Park Capital Multiplayer - cars on a business visit: parking lots and drive-thrus.
-- Every PC simulates its own people, so their cars park, leave and pay at other times; a friend saw its
-- own cars pay at a lot's exit and the money vanish (the host's wallet wins). The host's visits are the
-- city's, so a friend's game shows them instead of its own. Cars just driving around stay each PC's own.
--
-- A car is on a visit while it has a bay (ParkingSpaceClaim), its owner holds a ParkingEntryTicket, or
-- it is going to or inside a drive-thru (GoingToDriveThru, InDriveThru); and for 3 s after, so it is
-- seen leaving.
-- Host: every 0.2 s reports the visiting cars that moved, and every 5 s all of them:
--   "visits" { cars = { { id, tpl, c1, c2, x, y, z, rx, ry, rz }, ... }, gone = { id, ... },
--              lots = { [key] = { n, m } }, pays = { { cat, amount, x, y, z }, ... }, full }
-- id is the host's car entity; c1/c2 its two colours; rx/ry/rz its rotation in degrees (Transform.rotation
-- reads as angles; X*Y*Z rebuilds it). lots: each parking entrance's and exit's numVehiclesPassed (n) and
-- an exit's totalMoneyTransacted (m), when they change. pays: what the visits paid, as they paid it:
-- parking (an exit's total grew; the engine charges it, Lua doesn't see it), drive-thrus and gas pumps
-- (their scripts call transactMoney).
-- Friend: shows each host car as a stand-in (the same kind of car and colours, no owner or driver) at
-- the host's position, moved smoothly frame by frame, and removes it when gone. Its own cars don't
-- visit: every tick their bay claims, tickets and drive-thru visits are called off (a car without a
-- ticket leaves free; a driver whose claim goes away drives on; one already parked is hidden until its
-- owner has driven it away from the lot). Its own drive-thru and gas income is
-- dropped. The host's lot counters are written onto ours, and each payment is repeated (the money
-- shows at the same spot; the helper puts the host's balance back straight after).
local MP = ...

local S = MP.room
local C = component

local REPORT_TICKS = 2     -- moving cars, every 0.2 s
local FULL_TICKS = 50      -- every car, every 5 s (a lost or late message heals)
local LINGER_TICKS = 30    -- a car is still shown 3 s after its visit, driving away from the exit
local STATUS_TICKS = 50    -- a friend's report to the helper, at most every 5 s
local MOVED = 0.02         -- a position change worth reporting
local TURNED = 0.5         -- degrees
local JUMP = 20            -- farther than this, a stand-in is put there instead of moved
local HIDDEN_UNTIL = 25    -- a hidden car of ours shows again this far from where it stood

-- Payments that belong to a car's visit. A friend's game drops its own; the host's are repeated.
local VISIT_CATEGORIES = { "PARKING", "DRIVE_THRU", "OIL_CONSUMPTION" }

S.visitSent = S.visitSent or {}     -- host: car -> what we last reported
S.visitUntil = S.visitUntil or {}   -- host: car -> tick it stops being shown
S.visitPays = S.visitPays or {}     -- host: payments not reported yet
S.lots = S.lots or {}               -- parking entrances and exits: key -> entity
S.lotSent = S.lotSent or {}         -- host: key -> { n, m } last reported
S.puppets = S.puppets or {}         -- friend: host car id -> stand-in
S.puppetOf = S.puppetOf or {}       -- friend: stand-in entity -> host car id
S.visitStats = S.visitStats or { cancelled = 0, replayed = 0, blocked = 0 }
S.hidden = S.hidden or {}           -- friend: our own parked car -> its sprite, shadow and where it stood

-- The room's shared environment (as boot.lua finds it): the game's own scripts look transactMoney up there.
local roomEnv = MP.env
for _ = 1, 5 do
    if type(roomEnv) ~= "table" or rawget(roomEnv, "setComponents") then break end
    local mt = getmetatable(roomEnv)
    roomEnv = mt and mt.__index
end

local categoryName = {}
for _, name in ipairs(VISIT_CATEGORIES) do
    local ok, value = pcall(function() return TransactionCategory[name] end)
    if ok and value ~= nil then categoryName[value] = name end
end

-- Host: drive-thru and gas payments, as their scripts make them. Friend: its own are dropped.
if type(roomEnv) == "table" and type(rawget(roomEnv, "transactMoney")) == "function" and not S.transactWrapped then
    S.transactWrapped = true
    local original = rawget(roomEnv, "transactMoney")
    roomEnv.transactMoney = function(amount, category, position, showText, ...)
        local name = categoryName[category]
        if name and name ~= "PARKING" and type(amount) == "number" and amount > 0 then
            if MP.role == "host" then
                local p = position
                S.visitPays[#S.visitPays + 1] = { cat = name, amount = amount,
                    x = p and p.x or 0, y = p and p.y or 0, z = p and p.z or 0 }
            elseif MP.role == "client" and not S.replaying then
                S.visitStats.blocked = S.visitStats.blocked + 1
                return
            end
        end
        return original(amount, category, position, showText, ...)
    end
elseif not S.transactWrapped then
    MP.log("visits: no transactMoney in the room, drive-thru payments aren't shared")
end

-- Lots are found as lua/sync.lua indexes the city's objects.
local function noteLot(e, key)
    if type(key) == "string" and valid(e) and (C.ParkingExit.has(e) or C.ParkingEntry.has(e)) then S.lots[key] = e end
end
local indexedBefore = MP.onIndexed
MP.onIndexed = function(e, key)
    if indexedBefore then indexedBefore(e, key) end
    noteLot(e, key)
end
for e, key in pairs(S.known or {}) do noteLot(e, key) end

local function lotNamed(key)
    local e = S.lots[key]
    if e and valid(e) and S.known and S.known[e] == key then return e end
    S.lots[key] = nil
    return nil
end

local function templateOf(v)
    if not C.Persistent.has(v) then return nil end
    local tpl = C.Persistent.getFor(v).applyTemplateOnLoad
    return type(tpl) == "string" and tpl ~= "" and tpl or nil
end

local function round(x, digits)
    local f = 10 ^ digits
    return math.floor(x * f + 0.5) / f
end

-- Host ---------------------------------------------------------------------------------------------------

local function onVisit(v)
    if C.ParkingSpaceClaim.has(v) or C.GoingToDriveThru.has(v) or C.InDriveThru.has(v) then return true end
    if C.OwnedVehicle.has(v) then
        local p = C.OwnedVehicle.getFor(v).owner
        if p and valid(p) and C.ParkingEntryTicket.has(p) then return true end
    end
    return false
end

local function carState(v)
    local t = C.Transform.getFor(v)
    local p, r = t.position, t.rotation
    local st = { id = v, tpl = templateOf(v), x = round(p.x, 2), y = round(p.y, 2), z = round(p.z, 2),
        rx = round(r.x, 1), ry = round(r.y, 1), rz = round(r.z, 1) }
    if C.ModelSpriteView.has(v) then
        local colors = C.ModelSpriteView.getFor(v).reservedColorIndices
        st.c1, st.c2 = colors.x, colors.y
    end
    return st
end

local function changed(old, new)
    return math.abs(old.x - new.x) > MOVED or math.abs(old.y - new.y) > MOVED or math.abs(old.z - new.z) > MOVED
        or math.abs(old.rx - new.rx) > TURNED or math.abs(old.ry - new.ry) > TURNED or math.abs(old.rz - new.rz) > TURNED
end

-- Each lot's counters; an exit's money that grew is a car paying there.
local function lotsNow(pays)
    local lots = {}
    for key in pairs(S.lots) do
        local e = lotNamed(key)
        if e then
            local now
            if C.ParkingExit.has(e) then
                local exit = C.ParkingExit.getFor(e)
                now = { n = exit.numVehiclesPassed, m = round(exit.totalMoneyTransacted, 2) }
            elseif C.ParkingEntry.has(e) then
                now = { n = C.ParkingEntry.getFor(e).numVehiclesPassed }
            end
            local old = S.lotSent[key]
            if now and (not old or old.n ~= now.n or old.m ~= now.m) then
                if old and now.m and old.m and now.m > old.m + 0.004 and C.Transform.has(e) then
                    local p = C.Transform.getFor(e).position
                    pays[#pays + 1] = { cat = "PARKING", amount = round(now.m - old.m, 2), x = p.x, y = p.y, z = p.z }
                end
                S.lotSent[key] = now
                lots[key] = now
            end
        end
    end
    return lots
end

local function hostTick()
    local full = S.tick >= (S.visitFullAt or 0)
    if full then S.visitFullAt = S.tick + FULL_TICKS end
    local cars, shown = {}, {}
    for v in pairs(S.vehicles or {}) do
        if valid(v) and C.Transform.has(v) then
            if onVisit(v) then S.visitUntil[v] = S.tick + LINGER_TICKS end
            if (S.visitUntil[v] or -1) >= S.tick then
                local st = carState(v)
                if st.tpl then
                    shown[v] = true
                    local old = S.visitSent[v]
                    if full or not old or changed(old, st) then
                        cars[#cars + 1] = st
                        S.visitSent[v] = st
                    end
                end
            end
        end
    end
    local gone = {}
    for v in pairs(S.visitSent) do
        if not shown[v] then
            gone[#gone + 1] = v
            S.visitSent[v] = nil
        end
    end
    for v, untilTick in pairs(S.visitUntil) do
        if untilTick < S.tick or not valid(v) then S.visitUntil[v] = nil end
    end
    local pays = S.visitPays
    S.visitPays = {}
    local lots = lotsNow(pays)
    if full or #cars > 0 or #gone > 0 or #pays > 0 or next(lots) then
        MP.emit("visits", { cars = cars, gone = gone, lots = lots, pays = pays, full = full or nil })
    end
end

-- Friend -------------------------------------------------------------------------------------------------

local function q(angle, x, y, z)
    return quat:new():setFromAngleAndAxis(angle, vec3(x, y, z))
end

local function rotationOf(st)
    return q(st.rx, 1, 0, 0) * q(st.ry, 0, 1, 0) * q(st.rz, 0, 0, 1)
end

local function removePuppet(id)
    local pp = S.puppets[id]
    if not pp then return end
    S.puppets[id] = nil
    S.puppetOf[pp.car] = nil
    if valid(pp.car) then destroyEntity(pp.car) end
    if S.vehicles then S.vehicles[pp.car] = nil end
end

-- A stand-in: the host car's kind and colours, not anyone's and nobody driving it, not saved.
local function makePuppet(st)
    local car = createEntity()
    local args = {}
    if st.c1 then args.mainColor, args.secondaryColor = st.c1, st.c2 or st.c1 end
    local ok, err = pcall(applyTemplate, car, st.tpl, args, false)
    if not ok then
        if valid(car) then destroyEntity(car) end
        MP.log("visits: no template " .. tostring(st.tpl) .. ": " .. tostring(err))
        return nil
    end
    setComponent(car, Transform { position = vec3(st.x, st.y, st.z), rotation = rotationOf(st) })
    -- Its info panel would show nobody's car.
    if C.Hoverable.has(car) then C.Hoverable.remove(car) end
    return car
end

local function placePuppet(id, st)
    local pp = S.puppets[id]
    if pp and (not valid(pp.car) or not C.Transform.has(pp.car) or pp.tpl ~= st.tpl or pp.c1 ~= st.c1) then
        removePuppet(id)
        pp = nil
    end
    local target = vec3(st.x, st.y, st.z)
    if not pp then
        local car = makePuppet(st)
        if not car then return end
        pp = { car = car, tpl = st.tpl, c1 = st.c1 }
        S.puppets[id] = pp
        S.puppetOf[car] = id
        pp.from, pp.to, pp.elapsed = target, target, 0
        return
    end
    local t = C.Transform.getFor(pp.car)
    local here = t.position
    t.rotation = rotationOf(st)
    local d = math.sqrt((here.x - st.x) ^ 2 + (here.y - st.y) ^ 2 + (here.z - st.z) ^ 2)
    if d > JUMP then
        t.position = target
        pp.from = target
    else
        pp.from = vec3(here.x, here.y, here.z)
    end
    pp.to, pp.elapsed = target, 0
end

local function writeLot(key, want)
    local e = lotNamed(key)
    if not e or type(want) ~= "table" then return end
    if C.ParkingExit.has(e) then
        local exit = C.ParkingExit.getFor(e)
        if type(want.n) == "number" and exit.numVehiclesPassed ~= want.n then exit.numVehiclesPassed = want.n end
        if type(want.m) == "number" and math.abs(exit.totalMoneyTransacted - want.m) > 0.004 then exit.totalMoneyTransacted = want.m end
    elseif C.ParkingEntry.has(e) and type(want.n) == "number" then
        local entry = C.ParkingEntry.getFor(e)
        if entry.numVehiclesPassed ~= want.n then entry.numVehiclesPassed = want.n end
    end
end

-- The host's payment, so the money shows at the same spot. The helper writes the host's balance (which
-- already has it) straight after.
local function replay(pay)
    local cat = TransactionCategory[pay.cat]
    if cat == nil or type(pay.amount) ~= "number" then return end
    S.replaying = true
    local ok, err = pcall(transactMoney, pay.amount, cat, vec3(pay.x or 0, pay.y or 0, pay.z or 0), true)
    S.replaying = false
    if ok then
        S.visitStats.replayed = S.visitStats.replayed + 1
    else
        MP.log("visits: payment not shown: " .. tostring(err))
    end
end

-- From the helper (friend only): the host's visiting cars, lots and payments.
MP.handlers.visits = function(msg)
    if MP.role == "host" then return end
    -- How long between the host's reports, in frame time, so stand-ins arrive as the next report comes.
    local clock = S.visitClock or 0
    if S.visitLastAt then
        local gap = clock - S.visitLastAt
        if gap > 0 then S.visitGap = math.min(1.0, math.max(0.05, (S.visitGap or 0.2) * 0.7 + gap * 0.3)) end
    end
    S.visitLastAt = clock
    local listed = {}
    for _, st in ipairs(type(msg.cars) == "table" and msg.cars or {}) do
        if type(st) == "table" and type(st.id) == "number" and type(st.tpl) == "string"
            and type(st.x) == "number" and type(st.y) == "number" and type(st.z) == "number" then
            st.rx, st.ry, st.rz = tonumber(st.rx) or 0, tonumber(st.ry) or 0, tonumber(st.rz) or 0
            listed[st.id] = true
            placePuppet(st.id, st)
        end
    end
    for _, id in ipairs(type(msg.gone) == "table" and msg.gone or {}) do removePuppet(id) end
    if msg.full then
        for id in pairs(S.puppets) do
            if not listed[id] then removePuppet(id) end
        end
    end
    for key, want in pairs(type(msg.lots) == "table" and msg.lots or {}) do writeLot(key, want) end
    for _, pay in ipairs(type(msg.pays) == "table" and msg.pays or {}) do
        if type(pay) == "table" then replay(pay) end
    end
end

-- Our own car standing in a bay when its visit is called off (parked before the host's visits were shown)
-- would stand there under or beside the host's: it is hidden (no sprite or shadow; the game's sprites
-- don't scale) until its owner has driven it away from the lot or it is out of town.
local function hideParked(v)
    if S.hidden[v] or not C.Transform.has(v) or not C.ModelSpriteView.has(v) then return end
    local owner = C.OwnedVehicle.has(v) and C.OwnedVehicle.getFor(v).owner
    if owner and valid(owner) and C.Driver.has(owner) then return end
    local view = C.ModelSpriteView.getFor(v)
    local colors = view.reservedColorIndices
    local p = C.Transform.getFor(v).position
    S.hidden[v] = { model = view.name.model, anim = view.name.animation, colors = { colors.x, colors.y, colors.z, colors.w },
        shadow = C.DropShadowVolume.has(v) and C.DropShadowVolume.getFor(v).type or nil, at = vec3(p.x, p.y, p.z) }
    C.ModelSpriteView.remove(v)
    if C.DropShadowVolume.has(v) then C.DropShadowVolume.remove(v) end
end

local function showHidden()
    for v, h in pairs(S.hidden) do
        if not valid(v) then
            S.hidden[v] = nil
        else
            local away = not C.Transform.has(v)
            if not away then
                local p = C.Transform.getFor(v).position
                away = math.sqrt((p.x - h.at.x) ^ 2 + (p.z - h.at.z) ^ 2) > HIDDEN_UNTIL
            end
            if away then
                S.hidden[v] = nil
                setComponent(v, ModelSpriteView { name = ModelSpriteName(h.model, h.anim),
                    reservedColorIndices = u8vec4(h.colors[1], h.colors[2], h.colors[3], h.colors[4]) })
                if h.shadow then setComponent(v, DropShadowVolume { type = h.shadow }) end
            end
        end
    end
end

-- Our own cars don't visit: their bay, ticket and drive-thru visit are called off.
local function cancelOwnVisits()
    local cancelled = 0
    for v in pairs(S.vehicles or {}) do
        if not S.puppetOf[v] and valid(v) then
            local any = false
            if C.ParkingSpaceClaim.has(v) then
                C.ParkingSpaceClaim.remove(v)
                hideParked(v)
                any = true
            end
            if C.GoingToDriveThru.has(v) then C.GoingToDriveThru.remove(v); any = true end
            if C.InDriveThru.has(v) then C.InDriveThru.remove(v); any = true end
            if C.OwnedVehicle.has(v) then
                local p = C.OwnedVehicle.getFor(v).owner
                if p and valid(p) and C.ParkingEntryTicket.has(p) then C.ParkingEntryTicket.remove(p); any = true end
            end
            if any then cancelled = cancelled + 1 end
        end
    end
    S.visitStats.cancelled = S.visitStats.cancelled + cancelled
end

local function friendTick()
    cancelOwnVisits()
    showHidden()
    for id, pp in pairs(S.puppets) do
        if not valid(pp.car) then
            S.puppets[id] = nil
            S.puppetOf[pp.car] = nil
        end
    end
    if S.tick >= (S.visitStatusAt or 0) then
        S.visitStatusAt = S.tick + STATUS_TICKS
        local shown, hidden = 0, 0
        for _ in pairs(S.puppets) do shown = shown + 1 end
        for _ in pairs(S.hidden) do hidden = hidden + 1 end
        local st = S.visitStats
        local sig = shown .. "/" .. hidden .. "/" .. st.cancelled .. "/" .. st.replayed .. "/" .. st.blocked
        if sig ~= S.visitStatusSent then
            S.visitStatusSent = sig
            MP.emit("visitsync", { shown = shown, hidden = hidden, cancelled = st.cancelled, replayed = st.replayed, blocked = st.blocked })
        end
    end
end

-- Every frame: stand-ins move from where they were towards the host's newest position.
local frameBefore = MP.onFrame
MP.onFrame = function(dt)
    if frameBefore then frameBefore(dt) end
    if MP.role ~= "client" then return end
    dt = tonumber(dt) or (1 / 60)
    S.visitClock = (S.visitClock or 0) + dt
    local gap = S.visitGap or 0.2
    for _, pp in pairs(S.puppets) do
        if pp.elapsed and pp.elapsed < gap and valid(pp.car) and C.Transform.has(pp.car) then
            pp.elapsed = pp.elapsed + dt
            local a = math.min(1, pp.elapsed / gap)
            local f, t = pp.from, pp.to
            C.Transform.getFor(pp.car).position = vec3(f.x + (t.x - f.x) * a, f.y + (t.y - f.y) * a, f.z + (t.z - f.z) * a)
        end
    end
end

MP.visitsTick = function()
    if MP.role == "host" then
        if S.tick % REPORT_TICKS == 0 then hostTick() end
    elseif MP.role == "client" then
        friendTick()
    end
end
