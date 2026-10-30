-- One-off probe (not part of the mod): can a friend's game show a stand-in car (no owner, no driver) that
-- stays where it's put and moves smoothly when the mod moves it? And can a lot's counters be written?
-- Run on a friend's game:  lua return loadfile("multiplayer/probe_puppet.lua", "t", MP.env)()
-- The first run copies a parked car (template, colours, Transform) into a free bay of the same direction,
-- then over 12 s: checks it stays, slides it 6 tiles towards the bay's entry in 0.05 s steps, and logs its
-- components; then destroys it (unless MP.room.keepPuppet). Run again to read the result.
local env = MP.env
local C = env.component
local S = MP.room

local function now() return env.getSimulationTime() end
local function fmt(p) return string.format("%.2f,%.2f,%.2f", p.x, p.y, p.z) end

if S.ppLog and not S.ppRunning then
    local out = "[puppet] " .. table.concat(S.ppLog, "\n")
    S.ppLog = nil
    return out
end
if S.ppRunning then return "[puppet] still running" end
S.ppRunning = true
S.ppLog = {}
local function log(s) S.ppLog[#S.ppLog + 1] = string.format("%.1f %s", now(), s) end

local function comps(e)
    local list = {}
    for _, c in ipairs({ "Transform", "Vehicle", "OwnedVehicle", "Persistent", "ModelSpriteView", "PathFinding",
        "VehiclePathSteering", "NavigationPath", "PathFindingGoal", "ParkingSpaceClaim", "Hoverable", "DespawnAfter",
        "CustomSpriteColors", "DropShadowVolume" }) do
        if C[c] and C[c].has(e) then list[#list + 1] = c end
    end
    return table.concat(list, ",")
end

local function templateOf(v)
    if not C.Persistent.has(v) then return nil end
    local tpl = C.Persistent.getFor(v).applyTemplateOnLoad
    return type(tpl) == "string" and tpl ~= "" and tpl or nil
end

-- A parked car to copy, and a free bay facing the same way (the key's rotation part).
local source, sourceBay
for v in pairs(S.vehicles or {}) do
    if env.valid(v) and C.Transform.has(v) and C.ParkingSpaceClaim.has(v) and templateOf(v) then
        local bay = C.ParkingSpaceClaim.getFor(v).parkingSpace
        if bay and env.valid(bay) and S.known[bay] and C.ParkingSpace.has(bay) then
            local space = C.ParkingSpace.getFor(bay)
            local at = env.localPositionToWorld(bay, space.vehiclePosLocal)
            local p = C.Transform.getFor(v).position
            if math.abs(p.x - at.x) + math.abs(p.z - at.z) < 0.1 then source, sourceBay = v, bay break end
        end
    end
end
if not source then
    S.ppRunning = false
    return "[puppet] no parked car to copy right now"
end
local rot = S.known[sourceBay]:match("/(%d+)/%d+$")
local freeBay
for e, key in pairs(S.known) do
    if env.valid(e) and C.ParkingSpace.has(e) and C.UnclaimedParkingSpace.has(e) and key:match("/(%d+)/%d+$") == rot
        and key:match("^ParkingBay@") then
        freeBay = e
        break
    end
end
if not freeBay then
    S.ppRunning = false
    return "[puppet] no free bay facing direction " .. tostring(rot)
end

local tpl = templateOf(source)
local colors = C.ModelSpriteView.getFor(source).reservedColorIndices
local space = C.ParkingSpace.getFor(freeBay)
local spot = env.localPositionToWorld(freeBay, space.vehiclePosLocal)
local entry = env.localPositionToWorld(freeBay, space.enterPosLocal)
log(string.format("copying %s (%s, colours %d/%d) from %s into free %s at %s",
    tostring(source), tpl, colors.x, colors.y, S.known[sourceBay], S.known[freeBay], fmt(spot)))

local car = env.createEntity()
local ok, err = pcall(env.applyTemplate, car, tpl, { mainColor = colors.x, secondaryColor = colors.y }, false)
if not ok then
    S.ppRunning = false
    return "[puppet] applyTemplate failed: " .. tostring(err)
end
env.setComponent(car, C.Transform.getFor(source))
local t = C.Transform.getFor(car)
t.position = env.vec3(spot.x, spot.y, spot.z)
local mine = C.ModelSpriteView.getFor(car).reservedColorIndices
log(string.format("made %s: colours %d/%d, rotation %s, components %s", tostring(car), mine.x, mine.y,
    tostring(t.rotation), comps(car)))
S.ppCar = car

local function check(label)
    if not env.valid(car) then log(label .. ": the car is gone") return false end
    if not C.Transform.has(car) then log(label .. ": no Transform") return false end
    local p = C.Transform.getFor(car).position
    log(string.format("%s: at %s (%.2f from the bay spot) components %s", label, fmt(p),
        math.sqrt((p.x - spot.x) ^ 2 + (p.z - spot.z) ^ 2), comps(car)))
    return true
end

-- The exit's counters: write a little more, read it back, put it back.
for e, key in pairs(S.known) do
    if env.valid(e) and C.ParkingExit.has(e) then
        local exit = C.ParkingExit.getFor(e)
        local before, n = exit.totalMoneyTransacted, exit.numVehiclesPassed
        local okw, errw = pcall(function()
            exit.totalMoneyTransacted = before + 1.25
            exit.numVehiclesPassed = n + 1
        end)
        env.setTimeout(MP.hostEntity, 0.5, function()
            local x = C.ParkingExit.getFor(e)
            log(string.format("exit %s counters: wrote %s (%s); 0.5 s later %.2f / %d (was %.2f / %d)", key,
                tostring(okw), tostring(errw), x.totalMoneyTransacted, x.numVehiclesPassed, before, n))
            x.totalMoneyTransacted = x.totalMoneyTransacted - 1.25
            x.numVehiclesPassed = x.numVehiclesPassed - 1
        end)
        break
    end
end

env.setTimeout(MP.hostEntity, 2.0, function()
    if not check("+2s, standing") then S.ppRunning = false return end
    -- Slide towards the bay's entry and 6 tiles on, in 40 steps of 0.05 s.
    local dx, dz = entry.x - spot.x, entry.z - spot.z
    local len = math.sqrt(dx * dx + dz * dz)
    dx, dz = dx / len * 6, dz / len * 6
    local step = 0
    local function slide()
        step = step + 1
        if not env.valid(car) or not C.Transform.has(car) then return end
        C.Transform.getFor(car).position = env.vec3(spot.x + dx * step / 40, spot.y, spot.z + dz * step / 40)
        if step < 40 then env.setTimeout(MP.hostEntity, 0.05, slide) end
    end
    slide()
    env.setTimeout(MP.hostEntity, 1.0, function() check("+3s, sliding") end)
    env.setTimeout(MP.hostEntity, 3.0, function()
        check("+5s, slid")
        local target = env.vec3(spot.x + dx, spot.y, spot.z + dz)
        local p = C.Transform.getFor(car).position
        log(string.format("slide ended %.2f from where it was sent", math.sqrt((p.x - target.x) ^ 2 + (p.z - target.z) ^ 2)))
    end)
    env.setTimeout(MP.hostEntity, 8.0, function()
        check("+10s, left alone")
        if not S.keepPuppet and env.valid(car) then
            env.destroyEntity(car)
            log("destroyed it")
        end
        S.ppRunning = false
    end)
end)
return "[puppet] started; run again in about 12 s"
