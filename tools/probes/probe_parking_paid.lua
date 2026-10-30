-- One-off probe (not part of the mod): does a car still pay at the exit when its ticket says paid, or has
-- no ticket? Run on a friend's game, after probe_parking.lua (which logs what happens next):
--   lua return loadfile("multiplayer/probe_parking_paid.lua", "t", MP.env)()
-- Everyone parked now (a ticket with a used parking space) goes in a group, by name in turn:
--   A: ticket.bPaid = true      B: ticket removed      C: left alone
-- probe_parking.lua's log then shows each one's group; an exit's money counter that moves as an A or B
-- person drives out means the engine charged them anyway. Changes this game only: run it on a friend's.
local env = MP.env
local C = env.component
local S = MP.room
S.parkGroups = S.parkGroups or {}
S.parkLog = S.parkLog or {}

if MP.role == "host" and not S.parkForce then
    return "[parking-paid] this is the host's game; set MP.room.parkForce = true to run it here anyway"
end

local parked = {}
for v in pairs(S.vehicles or {}) do
    if env.valid(v) and C.OwnedVehicle.has(v) then
        local p = C.OwnedVehicle.getFor(v).owner
        if p and env.valid(p) and C.ParkingEntryTicket.has(p) and not S.parkGroups[env.getName(p) or ""] then
            local ticket = C.ParkingEntryTicket.getFor(p)
            local space = ticket.usedParkingSpace
            if space and env.valid(space) then parked[#parked + 1] = { name = env.getName(p), person = p } end
        end
    end
end
table.sort(parked, function(a, b) return a.name < b.name end)

local groups = { "A", "B", "C" }
local done = {}
for i, e in ipairs(parked) do
    local g = groups[(i - 1) % 3 + 1]
    if g == "A" then
        C.ParkingEntryTicket.getFor(e.person).bPaid = true
    elseif g == "B" then
        C.ParkingEntryTicket.remove(e.person)
    end
    S.parkGroups[e.name] = g
    done[#done + 1] = g .. ":" .. e.name
end
local line = string.format("%.1f marked %d parked people: %s", env.getSimulationTime(), #done, table.concat(done, " "))
S.parkLog[#S.parkLog + 1] = line
return "[parking-paid] " .. line
