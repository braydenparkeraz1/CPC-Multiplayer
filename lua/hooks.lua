-- Car Park Capital Multiplayer - placeable creation hook.
-- Called from _catalogue.lua while a level's templates load (before entities exist), once per level.
-- Every template's create() calls setComponents/setComponent through the shared room environment,
-- so wrapping those two functions lets us see each entity that gets a Placeable component.
local roomEnv, installedBy = ...
if type(roomEnv) ~= "table" or type(rawget(roomEnv, "setComponents")) ~= "function" then return end

local rooms = rawget(_G, "MP_ROOMS")
if not rooms then
    rooms = setmetatable({}, { __mode = "k" })
    _G.MP_ROOMS = rooms
end
if rooms[roomEnv] then return end

-- playerPlaced: placed objects created while the build tool had a preview ghost of the same kind.
-- This is decided here, when the object is created, because the mod's timers stop while the game
-- is paused and players often build while paused.
local state = {
    candidates = {}, seen = 0, installedBy = installedBy or "catalogue",
    seq = 0, ghostSeq = {}, ghostEntity = {}, playerPlaced = {},
}
rooms[roomEnv] = state

local GHOST_SEQ_WINDOW = 200   -- placed objects may be created a little after their ghost is replaced

local candidates = state.candidates
local component = roomEnv.component
local valid = roomEnv.valid
local placeableHas, placeableGetFor = component.Placeable.has, component.Placeable.getFor
local placedHas, placingHas, placingGetFor = component.Placed.has, component.Placing.has, component.Placing.getFor
local persistentHas = component.Persistent.has
local origSetComponents = roomEnv.setComponents
local origSetComponent = roomEnv.setComponent

local function watch(entity)
    if candidates[entity] ~= nil or not placeableHas(entity) then return end
    candidates[entity] = 0
    state.seen = state.seen + 1
    state.seq = state.seq + 1
    local templateIndex = placeableGetFor(entity).templateIndex
    if placedHas(entity) then
        local ghost, ghostSeq = state.ghostEntity[templateIndex], state.ghostSeq[templateIndex]
        if (ghost and valid(ghost) and placingHas(ghost)) or (ghostSeq and state.seq - ghostSeq <= GHOST_SEQ_WINDOW) then
            state.playerPlaced[entity] = true
        end
    elseif placingHas(entity) and not persistentHas(entity) and not placingGetFor(entity).bTryFinish then
        -- The build tool's preview; scripted placements (spills, synced objects) set bTryFinish.
        state.ghostSeq[templateIndex] = state.seq
        state.ghostEntity[templateIndex] = entity
    end
end

roomEnv.setComponents = function(entity, list)
    local result = origSetComponents(entity, list)
    watch(entity)
    return result
end

roomEnv.setComponent = function(entity, comp)
    local result = origSetComponent(entity, comp)
    watch(entity)
    return result
end
