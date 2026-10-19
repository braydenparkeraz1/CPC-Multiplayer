-- Car Park Capital Multiplayer: invisible one-tile repainter (material 13).
-- The mod places it on a tile whose roads/floors were written by the helper; repainting the floor
-- through the game makes it redraw that area and update where cars and people can go.
-- It removes itself right after it is placed and is never saved.
local info = PlaceableTemplateInfo {
    readableName = "MP Repaint 13",
    displayName = L10nKey.OBJ_ROAD_MARKINGS,
    buildPrice = 0,
    refundMultiplier = 0.0,
    visibleInBuildTool = false,
    dimensions = uvec3(1, 1, 1),
    bNeedsFloor = true,
    bNeedsSupport = false,
    allowedFloorMaterials = { 13 },
    typeMask = 0,
    compositeMask = 2047,
}
registerPlaceable(info)

function create(painter)
    setComponents(painter, {
        Placeable {
            templateIndex = info.templateIndex
        }
    })
    if component.Placed.has(painter) then
        setTimeout(painter, 0.0, function() destroyEntity(painter) end)
    end
end
