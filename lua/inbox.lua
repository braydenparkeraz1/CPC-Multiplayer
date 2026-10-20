-- Car Park Capital Multiplayer - receives messages from the helper app.
-- The helper rewrites multiplayer/runtime/inbox_<session>.lua as:
--   return { session = "<id>", msgs = { { id = 1, type = "...", ... }, ... } }
-- Message ids only grow within a session; we run each id once and ack the newest one
-- so the helper can drop messages we have already seen.
local MP = ...

-- Without a session (game started from Steam instead of the helper) the mod stays passive,
-- so a leftover inbox from an earlier run can never replay old messages.
MP.INBOX_PATH = MP.sessionArg and (MP.ROOT .. "runtime/inbox_" .. MP.sessionArg .. ".lua") or nil
MP.handlers = {}
MP.lastMsgId = MP.lastMsgId or 0

local lastAckSent = -1
local ticksSinceAck = 0

function MP.pollInbox()
    if not MP.INBOX_PATH then return end
    local chunk = loadfile(MP.INBOX_PATH, "t", {})
    if not chunk then return end
    local ok, box = pcall(chunk)
    if not ok or type(box) ~= "table" or type(box.msgs) ~= "table" then return end
    if MP.sessionArg and box.session ~= MP.sessionArg then return end

    if box.session ~= MP.inboxSession then
        MP.inboxSession = box.session
        MP.lastMsgId = 0
    end

    for _, msg in ipairs(box.msgs) do
        if type(msg.id) == "number" and msg.id > MP.lastMsgId then
            MP.lastMsgId = msg.id
            local handler = MP.handlers[msg.type]
            if handler then
                local hOk, err = pcall(handler, msg)
                if not hOk then
                    MP.emit("error", { id = msg.id, type = msg.type, err = tostring(err) })
                end
            else
                MP.emit("error", { id = msg.id, type = msg.type, err = "no handler in " .. tostring(MP.context) })
            end
        end
    end

    ticksSinceAck = ticksSinceAck + 1
    if MP.lastMsgId ~= lastAckSent or ticksSinceAck >= 50 then
        lastAckSent = MP.lastMsgId
        ticksSinceAck = 0
        MP.emit("ack", { session = MP.inboxSession, id = MP.lastMsgId })
    end
end

-- Developer console: runs Lua sent by the helper and prints the result.
MP.handlers.eval = function(msg)
    local fn, err = load(msg.code, "=mp_eval", "t", MP.env)
    if not fn then
        MP.emit("eval", { id = msg.id, ok = false, out = tostring(err) })
        return
    end
    local results = table.pack(pcall(fn))
    local out = {}
    for i = 2, results.n do out[#out + 1] = MP.inspect(results[i], 3) end
    MP.emit("eval", { id = msg.id, ok = results[1], out = table.concat(out, "\t") })
end

-- Loads a save (the host's city). Level switches are refused during the game update we run in,
-- so this goes through a UI screen script, which runs during rendering.
MP.handlers.loadworld = function(msg)
    if type(msg.path) ~= "string" or not msg.path:match("^saves/[%w_%-]+%.cpcp$") then
        error("refusing to load " .. tostring(msg.path))
    end
    _G.MP_LOAD_PATH = msg.path
    openScreen("scripts/ui_screens/MPLoad")
end

-- The host left, so this copy of their city is no longer shared: close the game.
-- Like level switches, this goes through a UI screen script. The helper force-closes the game
-- if it is still open a few seconds later.
MP.handlers.leave = function(msg)
    _G.MP_LEAVE = true
    openScreen("scripts/ui_screens/MPLoad")
end

-- What the main menu's buttons show (lua/menu.lua replaces this on the main menu). One can arrive
-- just as the game leaves the menu; nothing to show then.
MP.handlers.menu = function(msg) end

MP.handlers.hello = function(msg)
    MP.role = msg.role
    MP.playerName = msg.name
    -- The host's autosaves are what joining players load, so keep them fresh.
    local autosaveErr
    if msg.role == "host" and type(msg.autosave) == "number" then
        local ok, err = pcall(function()
            if gameSettings.autoSaveInterval > msg.autosave then
                gameSettings.autoSaveInterval = msg.autosave
            end
        end)
        if not ok then autosaveErr = tostring(err) end
    end
    local okRead, interval = pcall(function() return gameSettings.autoSaveInterval end)
    local okFind, saveApi = pcall(MP.findFunctions, "[Ss]ave")
    MP.emit("hello", { role = MP.role, context = MP.context, version = MP.VERSION,
        autosave = okRead and interval or nil, autosaveErr = autosaveErr, saveApi = okFind and saveApi or nil })
end

-- Names of the game's Lua functions matching a pattern (logged by the helper; a way to find
-- API calls we might use, like saving the city).
function MP.findFunctions(pattern)
    local names, seen = {}, {}
    local t = MP.env
    for _ = 1, 6 do
        if type(t) ~= "table" or seen[t] then break end
        seen[t] = true
        for k, v in pairs(t) do
            if type(k) == "string" and type(v) == "function" and k:find(pattern) then names[#names + 1] = k end
        end
        local mt = getmetatable(t)
        t = mt and type(mt.__index) == "table" and mt.__index or nil
    end
    table.sort(names)
    return names
end
