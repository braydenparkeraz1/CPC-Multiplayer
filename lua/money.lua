-- Car Park Capital Multiplayer - shared money.
-- Lua can change the city's money (transactMoney) but can't read it, so the helper reads and writes
-- the balance in the game's memory (src/MoneySync.cs). This module makes the marker transactions the
-- helper uses to find the balance, and on the host applies what friends spend.
local MP = ...

local S = MP.room

local MARK_TICKS = 5    -- a marker is taken back ~0.5 s after it was added

local function transact(amount, category)
    transactMoney(amount, TransactionCategory[category], vec3(0, 0, 0), false)
end

-- { mark, amount }: adds amount now and takes it back a moment later; the helper keeps the values in
-- memory that moved by exactly that.
MP.handlers.moneymark = function(msg)
    if type(msg.amount) ~= "number" then return end
    if S.moneyUndo then
        -- One marker at a time: take the previous one back first.
        transact(-S.moneyUndo.amount, "MISCELLANEOUS")
        S.moneyUndo = nil
    end
    transact(msg.amount, "MISCELLANEOUS")
    S.moneyUndo = { mark = msg.mark, amount = msg.amount, at = S.tick + MARK_TICKS }
    MP.emit("moneymark", { mark = msg.mark, step = 1 })
end

-- Host: { spent, refunded } from friends' builds and demolitions. Their own games charged only
-- themselves, and copies of builds are free here (bNoCost), so this is the only charge.
MP.handlers.spend = function(msg)
    if type(msg.spent) == "number" and msg.spent > 0 then transact(-msg.spent, "CONSTRUCTION") end
    if type(msg.refunded) == "number" and msg.refunded > 0 then transact(msg.refunded, "REFUND") end
    MP.trace("spend applied", { id = msg.id, spent = msg.spent, refunded = msg.refunded })
end

MP.moneyTick = function()
    local undo = S.moneyUndo
    if undo and S.tick >= undo.at then
        S.moneyUndo = nil
        transact(-undo.amount, "MISCELLANEOUS")
        MP.emit("moneymark", { mark = undo.mark, step = 2 })
    end
end
