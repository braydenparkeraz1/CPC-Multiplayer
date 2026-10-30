-- One-off probe (not part of the mod): can a friend's game call off its own cars' visits?
-- Run on a friend's game:  lua return loadfile("multiplayer/probe_cancel.lua", "t", MP.env)()
-- Takes up to 2 cars driving to a bay (claim, not parked yet), 1 parked car whose owner is away, and up to
-- 2 cars on their way to a drive-thru; removes the claim and ticket (or GoingToDriveThru), then logs for
-- 20 s: the car's position and path components, its owner's state and driver flag, and whether the bay is
-- still claimed. Run again to read the result.
local env = MP.env
local C = env.component
local S = MP.room

local function now() return env.getSimulationTime() end
if S.cxLog and not S.cxRunning then
    local out = "[cancel] " .. table.concat(S.cxLog, "\n")
    S.cxLog = nil
    return out
end
if S.cxRunning then return "[cancel] still running" end
S.cxRunning = true
S.cxLog = {}
local function log(s) S.cxLog[#S.cxLog + 1] = string.format("%.1f %s", now(), s) end

local function state(p)
    local ok, st = pcall(function() return C.PersonFSM.getFor(p).state end)
    return ok and tostring(st) or "?"
end

local function inBay(v, bay)
    local space = C.ParkingSpace.getFor(bay)
    local at = env.localPositionToWorld(bay, space.vehiclePosLocal)
    local p = C.Transform.getFor(v).position
    return math.abs(p.x - at.x) + math.abs(p.z - at.z) < 0.1
end

local picked = {}
local driving, parked, thru = 0, 0, 0
for v in pairs(S.vehicles or {}) do
    if env.valid(v) and C.Transform.has(v) and C.OwnedVehicle.has(v) then
        local p = C.OwnedVehicle.getFor(v).owner
        if p and env.valid(p) then
            if C.ParkingSpaceClaim.has(v) then
                local bay = C.ParkingSpaceClaim.getFor(v).parkingSpace
                if bay and env.valid(bay) and C.ParkingSpace.has(bay) then
                    local isParked = inBay(v, bay)
                    if not isParked and C.Driver.has(p) and driving < 2 then
                        driving = driving + 1
                        picked[#picked + 1] = { kind = "driving to a bay", v = v, p = p, bay = bay }
                    elseif isParked and not C.Driver.has(p) and parked < 1 then
                        parked = parked + 1
                        picked[#picked + 1] = { kind = "parked, owner away", v = v, p = p, bay = bay }
                    end
                end
            elseif C.GoingToDriveThru.has(v) and thru < 2 then
                thru = thru + 1
                picked[#picked + 1] = { kind = "going to a drive-thru", v = v, p = p }
            end
        end
    end
end
if #picked == 0 then
    S.cxRunning = false
    S.cxLog = nil
    return "[cancel] nobody to try it on right now"
end

local function describe(e)
    local parts = {}
    for _, c in ipairs({ "NavigationPath", "PathFindingGoal", "PathFindingFailed", "ParkingSpaceClaim", "GoingToDriveThru", "InDriveThru" }) do
        if C[c].has(e.v) then parts[#parts + 1] = c end
    end
    local where = C.Transform.has(e.v) and (function(p) return string.format("%.1f,%.1f", p.x, p.z) end)(C.Transform.getFor(e.v).position) or "out of town"
    local bay = e.bay and env.valid(e.bay) and string.format(" bay claimed=%s unclaimed=%s",
        tostring(C.ClaimedParkingSpace.has(e.bay)), tostring(C.UnclaimedParkingSpace.has(e.bay))) or ""
    return string.format("%s [%s]: car at %s %s; state=%s driver=%s ticket=%s%s", env.getName(e.p), e.kind, where,
        table.concat(parts, ","), state(e.p), tostring(C.Driver.has(e.p)), tostring(C.ParkingEntryTicket.has(e.p)), bay)
end

for _, e in ipairs(picked) do
    log("before: " .. describe(e) .. (e.bay and (" " .. tostring(S.known[e.bay])) or ""))
    if e.bay then C.ParkingSpaceClaim.remove(e.v) else C.GoingToDriveThru.remove(e.v) end
    if C.ParkingEntryTicket.has(e.p) then C.ParkingEntryTicket.remove(e.p) end
    log("after:  " .. describe(e))
end
for _, delay in ipairs({ 1, 3, 6, 10, 15, 20 }) do
    env.setTimeout(MP.hostEntity, delay, function()
        for _, e in ipairs(picked) do
            if env.valid(e.v) and env.valid(e.p) then log("+" .. delay .. "s " .. describe(e)) end
        end
        if delay == 20 then S.cxRunning = false end
    end)
end
return "[cancel] called off " .. #picked .. " visits; run again in about 22 s"
