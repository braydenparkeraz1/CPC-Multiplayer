-- Per-frame Demolished check on every street object and tree in the city, to see whether the game's own damage
-- (protests, fire) marks them Demolished before they go, like a player's demolition does (one frame, ttd 0).
-- Run: lua return loadfile("multiplayer/probe_demolished.lua", "t", MP.env)()   (results: "DEMOCITY" lua> lines)
-- Found with it and its predecessors (2026-10-26, city ix): the demolish tool's object had Demolished for exactly
-- one frame, timeUntilDestroy 0; trash cans wrecked in a protest and 33 protest banners went without it.
-- Also found: the level's Lua has no os, project or getCursorPos; Click events don't arrive while the demolish
-- tool is out; wrapping setComponents after the level loaded sees nothing (templates keep what they had at load);
-- a destroy callback (setOnDestroyCallback) runs after Demolished is already gone; checking 11,000 objects with
-- a tight loop of Demolished.has costs about 0.6 ms per frame.
local env = MP.env
local C = env.component
local valid = env.valid
local kinds = { Lamppost = true, LargeLamppost = true, ParkBench = true, TrashCan = true, FireHydrant = true,
    NationalFlag = true, ProtestBanner = true, Oak = true, SmallOak = true, LargePine = true, Pine = true,
    Birch = true, StonePine = true, Weeds = true }
local Q = MP.demoCity
if Q and Q.entity and valid(Q.entity) then env.destroyEntity(Q.entity) end
Q = { list = {}, seen = {}, frame = 0 }
MP.demoCity = Q
for key, e in pairs(MP.room.byKey) do
    if kinds[key:match("^([%w_]+)@")] then Q.list[#Q.list + 1] = { e = e, key = key } end
end
local function say(s) MP.emit("eval", { id = 0, ok = true, out = "DEMOCITY " .. s }) end
Q.entity = env.createEntity()
env.setUpdateFunction(Q.entity, 0.0, function()
    Q.frame = Q.frame + 1
    local list = Q.list
    local i = 1
    while i <= #list do
        local it = list[i]
        if valid(it.e) then
            if not Q.seen[it.key] and C.Demolished.has(it.e) then
                local ok, ttd = pcall(function() return C.Demolished.getFor(it.e).timeUntilDestroy end)
                Q.seen[it.key] = { f = Q.frame, ttd = ok and ttd or "?" }
            end
            i = i + 1
        else
            local s = Q.seen[it.key]
            say(it.key .. (s and string.format(" gone %d frames after Demolished (ttd %s)", Q.frame - s.f, tostring(s.ttd)) or " gone WITHOUT Demolished"))
            list[i] = list[#list]
            list[#list] = nil
        end
    end
    if Q.frame % 2000 == 0 then say("frame " .. Q.frame .. ", watching " .. #list) end
end)
return "watching " .. #Q.list .. " street objects and trees"
