-- Car Park Capital Multiplayer - Host/Join buttons on the main menu.
-- The game's own menu buttons are drawn by the engine (ImGui), which Lua can't add to. Ours are the
-- game's 3D letters (assets/models/3d_text.glb, A-Z and 0-9) placed in the menu's 3D scene, pinned in
-- front of its camera. Clicks on the engine's menu never reach Lua, so the two can't get mixed up.
--
-- The helper decides what the menu shows: inbox "menu" = { lines = { "...", ... }, buttons = { { id = "host",
-- label = "HOST" }, ... } }. A click on a button goes back as MP>{"t":"menu","d":{"click":"<id>"}}.
local MP = ...

-- Camera space: DEPTH units in front of the camera, so with the menu camera's narrow view a unit is
-- about 1/12 of the screen height. y is up from the screen's centre, in units.
local DEPTH = 85
local ADVANCE = 0.75          -- letter spacing (a letter is ~0.68 wide and 0.73 tall at scale 1)
local LETTER_HEIGHT = 0.73
local BUTTON_GAP = 1.6        -- between two buttons
-- Between the logo and the engine's menu panel, which has a fixed size in pixels, so it reaches up to
-- about y = -2.5 in a 720p window. Two status lines at most, then the buttons.
local LINE_Y = -0.45          -- baseline of the first status line
local LINE_STEP = 0.42        -- between status lines
local BUTTON_Y = -1.75        -- baseline of the button row
local LINE_SCALE = 0.4
local HOVER_SCALE = 1.1
-- Colours (the letters' own material is green): like the logo's yellow, white when hovered, and a dark
-- copy behind every letter as a drop shadow, so the text reads over the menu's busy background video.
local BUTTON_COLOR = vec3(1.0, 0.82, 0.15)
local HOVER_COLOR = vec3(1.0, 1.0, 1.0)
local TEXT_COLOR = vec3(1.0, 1.0, 1.0)
local SHADOW_COLOR = vec3(0.05, 0.05, 0.1)
local SHADOW_OFFSET = 0.05    -- down and to the right, per unit of letter scale

local menu = MP.menu or {}
MP.menu = menu
-- Handlers from an earlier load (an earlier visit of the main menu) see a different token and stop.
local token = {}
menu.token = token
menu.entities = {}
menu.buttons = {}
menu.hovered = nil
menu.nameSeq = menu.nameSeq or 0
-- What the helper last sent belongs to the previous visit of the main menu; it sends a new one.
menu.state = nil

local function camera()
    local cam = getChild(MP.hostEntity, "cam")
    if cam and valid(cam) then return cam end
    return nil
end

local function clear()
    for _, e in ipairs(menu.entities) do
        if valid(e) then destroyEntity(e) end
    end
    menu.entities = {}
    menu.buttons = {}
    menu.hovered = nil
end

local function colored(model, color)
    return RenderModel {
        modelName = model,
        materialOverrides = { reserved_0 = MaterialOverride { lerp = 1.0, bDiffuse = true, diffuse = color } }
    }
end

local function spawnOne(cam, model, x, y, z, scale, yScale, color)
    menu.nameSeq = menu.nameSeq + 1
    local e = createChild(MP.hostEntity, "mp_menu_" .. menu.nameSeq)
    local rot = quat:new()
    rot.y = 90 -- the letters face sideways in the model file
    setComponents(e, {
        Transform(),
        TransformChild {
            parentEntity = cam,
            offset = Transform {
                position = vec3(x, y, z),
                rotation = rot,
                scale = vec3(scale, scale * (yScale or 1), scale)
            }
        },
        colored(model, color)
    })
    menu.entities[#menu.entities + 1] = e
    return e
end

-- A letter and its shadow; returns the letter.
local function spawn(cam, model, x, y, scale, yScale, color)
    local d = SHADOW_OFFSET * math.max(scale, 0.4)
    spawnOne(cam, model, x + d, y - d, -DEPTH - 0.3, scale, yScale, SHADOW_COLOR)
    return spawnOne(cam, model, x, y, -DEPTH, scale, yScale, color)
end

-- Places one letter (or a stand-in for '.', ':' and '-', which the model doesn't have) centred on x.
local function spawnChar(cam, ch, x, y, scale, color)
    if ch:match("^[A-Z0-9]$") then
        return { spawn(cam, ch .. "_Text", x, y, scale, 1, color) }
    elseif ch == "." then
        return { spawn(cam, "O_Text", x, y, scale * 0.22, 1, color) }
    elseif ch == ":" then
        return { spawn(cam, "O_Text", x, y, scale * 0.22, 1, color),
            spawn(cam, "O_Text", x, y + LETTER_HEIGHT * scale * 0.5, scale * 0.22, 1, color) }
    elseif ch == "-" then
        return { spawn(cam, "I_Text", x, y + LETTER_HEIGHT * scale * 0.3, scale * 0.45, 0.35, color) }
    end
    return {}
end

-- Lays out text centred on x = 0 (or starting at x0), returning the letters and the text's width.
local function spawnText(cam, text, x0, y, scale, color)
    local width = #text * ADVANCE * scale
    local x = (x0 or -width / 2) + ADVANCE * scale / 2
    local letters = {}
    for i = 1, #text do
        for _, e in ipairs(spawnChar(cam, text:sub(i, i), x, y, scale, color)) do letters[#letters + 1] = e end
        x = x + ADVANCE * scale
    end
    return letters, width
end

local function cleanText(s)
    return tostring(s or ""):upper():gsub("[^A-Z0-9 .:%-]", " ")
end

function menu.render()
    clear()
    local state = menu.state
    local cam = camera()
    if not state or not cam then return end
    loadModels("assets/models/3d_text.glb", false, false)
    for i, line in ipairs(state.lines or {}) do
        if i > 2 then break end
        spawnText(cam, cleanText(line), nil, LINE_Y - (i - 1) * LINE_STEP, LINE_SCALE, TEXT_COLOR)
    end
    local buttons = state.buttons or {}
    local total = 0
    for i, b in ipairs(buttons) do
        total = total + #cleanText(b.label) * ADVANCE + (i > 1 and BUTTON_GAP or 0)
    end
    local x = -total / 2
    for _, b in ipairs(buttons) do
        local label = cleanText(b.label)
        local letters, width = spawnText(cam, label, x, BUTTON_Y, 1, BUTTON_COLOR)
        menu.buttons[#menu.buttons + 1] = { id = b.id, letters = letters, x0 = x, x1 = x + width,
            y0 = BUTTON_Y, y1 = BUTTON_Y + LETTER_HEIGHT }
        x = x + width + BUTTON_GAP
    end
end

-- Screen pixels of a camera-space point at DEPTH, for the cursor test.
local function toScreen(cam, x, y)
    local size = getScreenSize()
    local fov = component.CameraPerspective.getFor(cam).fieldOfView
    local unitPx = (size.y / 2) / (DEPTH * math.tan(math.rad(fov) / 2))
    return size.x / 2 + x * unitPx, size.y / 2 - y * unitPx
end

local function buttonAtCursor()
    local cam = camera()
    if not cam or #menu.buttons == 0 then return nil end
    local cursor = getCursorPos()
    for _, b in ipairs(menu.buttons) do
        -- A bit of slack around the letters, so the gaps between them count too.
        local left, top = toScreen(cam, b.x0 - 0.2, b.y1 + 0.2)
        local right, bottom = toScreen(cam, b.x1 + 0.2, b.y0 - 0.2)
        if cursor.x >= left and cursor.x <= right and cursor.y >= top and cursor.y <= bottom then return b end
    end
    return nil
end

local function setHover(b, on)
    for _, e in ipairs(b.letters) do
        if valid(e) then
            local offset = component.TransformChild.getFor(e).offset
            local s = offset.scale
            local factor = on and HOVER_SCALE or 1 / HOVER_SCALE
            offset.scale = vec3(s.x * factor, s.y * factor, s.z * factor)
            setComponent(e, colored(component.RenderModel.getFor(e).modelName, on and HOVER_COLOR or BUTTON_COLOR))
        end
    end
end

function menu.show(state)
    menu.state = state
    menu.render()
end

-- Called every frame on the main menu: hover highlight, and the menu once the camera exists.
local function frame()
    if menu.token ~= token then return end
    if menu.state and #menu.entities == 0 and camera() then menu.render() end
    local b = buttonAtCursor()
    if b ~= menu.hovered then
        if menu.hovered then setHover(menu.hovered, false) end
        if b then setHover(b, true) end
        menu.hovered = b
    end
end

-- In a timeout like the game's own MainMenu.lua, which builds its scene there "so doesn't get run when
-- generating spritesheet". The menu entity's update function belongs to boot's tick; ours runs on a
-- child of its own.
setTimeout(MP.hostEntity, 0.0, function()
    if menu.token ~= token then return end
    menu.nameSeq = menu.nameSeq + 1
    local anchor = createChild(MP.hostEntity, "mp_menu_anchor_" .. menu.nameSeq)
    setUpdateFunction(anchor, 0.0, frame)
    onEvent("Click", function()
        if menu.token ~= token then return end
        local b = buttonAtCursor()
        if b then MP.emit("menu", { click = b.id }) end
    end)
end)

MP.handlers.menu = function(msg)
    menu.show({ lines = msg.lines, buttons = msg.buttons })
end
