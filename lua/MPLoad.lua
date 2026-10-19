-- Car Park Capital Multiplayer - loads the shared city.
-- Installed as assets/scripts/ui_screens/MPLoad.lua. The mod opens this screen because UI screen
-- scripts run while the game renders, the one place where switching levels is allowed.
local path = rawget(_G, "MP_LOAD_PATH")
if path then
    _G.MP_LOAD_PATH = nil
    local ok, err = pcall(loadOrCreateLevel, path)
    if not ok then print("[MP] could not load " .. tostring(path) .. ": " .. tostring(err)) end
end
