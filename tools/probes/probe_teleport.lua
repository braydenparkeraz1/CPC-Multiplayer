-- One-off probe (not part of the mod): can a moving car be put somewhere else from Lua and keep driving?
-- Run on a friend's game:  lua return loadfile("multiplayer/probe_teleport.lua", "t", MP.env)()
-- The first run notes where every driven car is, and 2 s later moves the one that went furthest back to
-- where it was (a spot on its own route), then watches it for 8 s: position, distance from that spot,
-- whether its owner still drives it, and its path components. Run again to read the result.
local env = MP.env
local C = env.component
local S = MP.room

local function now() return env.getSimulationTime() end
local function vec(p) return env.vec3(p.x, p.y, p.z) end
local function fmt(p) return string.format("%.1f,%.1f,%.1f", p.x, p.y, p.z) end
local function dist(a, b) return math.sqrt((a.x - b.x) ^ 2 + (a.y - b.y) ^ 2 + (a.z - b.z) ^ 2) end

if S.tpLog and not S.tpRunning then
    local out = "[teleport] " .. table.concat(S.tpLog, "\n")
    S.tpLog = nil
    return out
end
if S.tpRunning then return "[teleport] still running" end

if MP.role == "host" and not S.parkForce then
    return "[teleport] this is the host's game; set MP.room.parkForce = true to run it here anyway"
end

S.tpRunning = true
S.tpLog = {}
local function log(s) S.tpLog[#S.tpLog + 1] = string.format("%.1f %s", now(), s) end

local function driven()
    local cars = {}
    for v in pairs(S.vehicles or {}) do
        if env.valid(v) and C.Transform.has(v) and C.OwnedVehicle.has(v) then
            local p = C.OwnedVehicle.getFor(v).owner
            if p and env.valid(p) and C.Driver.has(p) then cars[v] = p end
        end
    end
    return cars
end

local function describe(v, p, spot)
    local here = C.Transform.getFor(v).position
    local comps = {}
    for _, c in ipairs({ "NavigationPath", "PathFindingGoal", "PathFindingFailed", "VehiclePathSteering", "ParkingSpaceClaim", "GoingToDriveThru" }) do
        if C[c] and C[c].has(v) then comps[#comps + 1] = c end
    end
    local ok, st = pcall(function() return C.PersonFSM.getFor(p).state end)
    return string.format("at %s, %.1f from the spot; driver=%s state=%s %s", fmt(here), dist(here, spot),
        tostring(C.Driver.has(p)), ok and tostring(st) or "?", table.concat(comps, ","))
end

local before = {}
for v, p in pairs(driven()) do before[v] = vec(C.Transform.getFor(v).position) end
log("driven cars: " .. tostring((function() local n = 0 for _ in pairs(before) do n = n + 1 end return n end)()))

env.setTimeout(MP.hostEntity, 2.0, function()
    local ok, err = pcall(function()
        local best, bestD, owner
        for v, p in pairs(driven()) do
            if before[v] then
                local d = dist(C.Transform.getFor(v).position, before[v])
                if d > (bestD or 3) then best, bestD, owner = v, d, p end
            end
        end
        if not best then
            log("no car moved more than 3 in 2 s; run again when cars are driving")
            S.tpRunning = false
            return
        end
        local spot = before[best]
        local t = C.Transform.getFor(best)
        local members = {}
        local mt = getmetatable(t)
        if type(mt) == "table" then
            for k in pairs(mt) do if not tostring(k):match("^__") then members[#members + 1] = tostring(k) end end
        end
        table.sort(members)
        log("Transform members: " .. table.concat(members, " "))
        log(string.format("car %s of %s moved %.1f; before the move: %s", tostring(best), tostring(env.getName(owner)), bestD, describe(best, owner, spot)))
        t.position = vec(spot)
        log("moved back: " .. describe(best, owner, spot))
        for _, delay in ipairs({ 0.5, 1, 2, 4, 6, 8 }) do
            env.setTimeout(MP.hostEntity, delay, function()
                if env.valid(best) and C.Transform.has(best) then
                    log("+" .. delay .. "s " .. describe(best, owner, spot))
                else
                    log("+" .. delay .. "s the car is gone or out of town")
                end
                if delay == 8 then S.tpRunning = false end
            end)
        end
    end)
    if not ok then
        log("error: " .. tostring(err))
        S.tpRunning = false
    end
end)
return "[teleport] started; run again in about 12 s"
