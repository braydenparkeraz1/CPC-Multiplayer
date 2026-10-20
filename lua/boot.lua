-- Car Park Capital Multiplayer - bootstrap.
-- Loaded from game scripts with: loadfile("multiplayer/boot.lua", "t", _ENV)(context, entity)
-- The game sandbox has no io/os, so we talk to the helper app through:
--   game -> helper : print() lines on stdout, prefixed with "MP>"
--   helper -> game : multiplayer/runtime/inbox.lua, which we poll with loadfile()

local context, hostEntity = ...
local env = _ENV

-- Thumbnail/preview rooms also create a GlobalStorage entity; stay out of those.
if context == "level" then
    local preview = isPreviewRoom
    if type(preview) == "function" then preview = preview() end
    if preview or (getSimMode and SimMode and SimMode.PREVIEW ~= nil and getSimMode() == SimMode.PREVIEW) then return end
end

local MP = _G.MP or {}
_G.MP = MP

MP.ROOT = "multiplayer/"
MP.POLL_INTERVAL = 0.1
MP.VERSION = "0.4.0"

function MP.log(...)
    local parts = {}
    for i = 1, select("#", ...) do parts[#parts + 1] = tostring((select(i, ...))) end
    print("[MP] " .. table.concat(parts, " "))
end

local function loadModule(name, ...)
    local chunk, err = loadfile(MP.ROOT .. name .. ".lua", "t", env)
    if not chunk then
        MP.log("failed to load " .. name .. ": " .. tostring(err))
        return false
    end
    local ok, runErr = pcall(chunk, ...)
    if not ok then
        MP.log("error in " .. name .. ": " .. tostring(runErr))
        return false
    end
    return true
end

-- The helper launches the game with "-mpsession <id>" (the game reads arguments as key/value pairs),
-- so we only read an inbox it wrote for this run.
if MP.sessionArg == nil then
    MP.sessionArg = false
    local ok, args = pcall(getGameStartupArgs)
    if ok and args then
        pcall(function()
            for k, v in pairs(args) do
                local name = tostring(k):lower():gsub("^%-+", "")
                local value = tostring(v)
                if name == "mpsession" and value:match("^[%w_]+$") then MP.sessionArg = value end
            end
        end)
    end
end

-- Each room gets a fresh generation; older tick loops notice and stop.
MP.generation = (MP.generation or 0) + 1
local generation = MP.generation

MP.context = context
MP.env = env
MP.hostEntity = hostEntity
MP.onTick = nil

loadModule("util", MP)
loadModule("inbox", MP)

if context == "menu" then
    -- Host/Join buttons, so nobody has to type in the helper's window.
    loadModule("menu", MP)
end

if context == "level" then
    -- The level's shared environment is the first table up the __index chain that holds setComponents.
    local roomEnv = env
    for _ = 1, 5 do
        if type(roomEnv) ~= "table" or rawget(roomEnv, "setComponents") then break end
        local mt = getmetatable(roomEnv)
        roomEnv = mt and mt.__index
    end
    if not (rawget(_G, "MP_ROOMS") and MP_ROOMS[roomEnv]) then
        -- _catalogue.lua did not install the hook (should not happen); objects created so far are missed.
        loadModule("hooks", roomEnv, "boot")
    end
    MP.room = MP_ROOMS and MP_ROOMS[roomEnv]
    MP.onIndexed, MP.onUnindexed, MP.settingsTick, MP.moneyTick = nil, nil, nil, nil
    if MP.room then
        loadModule("sync", MP)
        loadModule("settings", MP)
        loadModule("money", MP)
    else
        MP.log("placeable hook missing, object sync disabled")
    end
end

local function step(paused)
    MP.paused = paused
    if MP.pollInbox then
        local ok, err = pcall(MP.pollInbox)
        if not ok then MP.log("inbox error: " .. tostring(err)) end
    end
    if MP.onTick then
        local ok, err = pcall(MP.onTick)
        if not ok then MP.log("tick error: " .. tostring(err)) end
    end
end

-- setTimeout counts simulation time, which stands still while the game is paused (speed 0).
-- An update function with interval 0 is still called every frame then, so once the timer has
-- been silent for STALL_FRAMES frames the frame callback drives the mod instead, every
-- FRAMES_PER_STEP frames. While the game runs, only the timer drives it.
local STALL_FRAMES = 60
local FRAMES_PER_STEP = 6
local framesSinceTimer, framesSinceStep = 0, 0

-- Engine errors (C++ exceptions) can skip straight past pcall and abort this call,
-- so the next tick is scheduled before doing any work.
local function tick()
    if MP.generation ~= generation then return end
    setTimeout(hostEntity, MP.POLL_INTERVAL, tick)
    framesSinceTimer = 0
    step(false)
end

local function frame()
    if MP.generation ~= generation then
        pcall(setUpdateFunction, hostEntity, 0.0, nil)
        return
    end
    framesSinceTimer = framesSinceTimer + 1
    if framesSinceTimer < STALL_FRAMES then return end
    framesSinceStep = framesSinceStep + 1
    if framesSinceStep < FRAMES_PER_STEP then return end
    framesSinceStep = 0
    step(true)
end

setTimeout(hostEntity, MP.POLL_INTERVAL, tick)
if context == "level" and type(setUpdateFunction) == "function" then
    local ok, err = pcall(setUpdateFunction, hostEntity, 0.0, frame)
    if not ok then MP.log("no frame callback, syncing stops while paused: " .. tostring(err)) end
end
MP.log("v" .. MP.VERSION .. " booted in '" .. tostring(context) .. "' session=" .. tostring(MP.sessionArg))
MP.emit("room", { context = context, version = MP.VERSION, session = MP.sessionArg })
