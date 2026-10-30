-- One-off probe (not part of the mod): do the game's own scripts' payments (drive-thrus, shops, restrooms)
-- pass through a transactMoney wrapper in the shared room environment? (MP.env is the mod's own
-- environment; the room's is its metatable's __index, as boot.lua finds it.)
-- Run with:  lua return loadfile("multiplayer/probe_transact.lua", "t", MP.env)()
-- The first run wraps it; every run returns the calls counted by category, and the last few.
local env = MP.env
local S = MP.room
local mt = getmetatable(env)
local roomEnv = mt and mt.__index
if type(roomEnv) ~= "table" or type(rawget(roomEnv, "transactMoney")) ~= "function" then
    return "[transact] no transactMoney in the room environment"
end

local names = {}
for _, name in ipairs({ "MISCELLANEOUS", "CONSTRUCTION", "REFUND", "PARKING", "DRIVE_THRU", "OIL_CONSUMPTION" }) do
    local ok, value = pcall(function() return roomEnv.TransactionCategory[name] end)
    if ok and value ~= nil then names[value] = name end
end

S.txCounts = S.txCounts or {}
S.txLast = S.txLast or {}
if not S.txWrapped then
    S.txWrapped = true
    local original = roomEnv.transactMoney
    roomEnv.transactMoney = function(amount, category, position, showText, ...)
        local name = names[category] or tostring(category)
        S.txCounts[name] = (S.txCounts[name] or 0) + 1
        if #S.txLast >= 12 then table.remove(S.txLast, 1) end
        S.txLast[#S.txLast + 1] = string.format("%.0f %s %.2f", env.getSimulationTime(), name, amount)
        if MP.role == "client" and S.txBlock and S.txBlock[name] then return end
        return original(amount, category, position, showText, ...)
    end
end

local counts = {}
for name, n in pairs(S.txCounts) do counts[#counts + 1] = name .. "=" .. n end
table.sort(counts)
return "[transact] counts: " .. table.concat(counts, " ") .. " | last: " .. table.concat(S.txLast, "; ")
