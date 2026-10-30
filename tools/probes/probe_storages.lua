-- One-off probe: every entity with GoodsStorage that can hold cars, and what it holds.
local env = MP.env
local C = env.component
local item = env.currentRoom.items.VEHICLE
local out = { "[storages] t=" .. tostring(env.getSimulationTime()) }
for index = 0, 40000 do
    for version = 0, 255 do
        local id = index + version * 1048576
        if env.valid(id) then
            if C.GoodsStorage.has(id) then
                local st = C.GoodsStorage.getFor(id)
                local cap = st.itemCapacity[item]
                if cap and cap > 0 then
                    local tpl = C.Persistent.has(id) and C.Persistent.getFor(id).applyTemplateOnLoad or env.getName(id) or "?"
                    out[#out + 1] = tostring(tpl) .. "#" .. id .. " cars=" .. tostring(env.system.Goods.getStorageCount(id, item)) .. "/" .. cap
                        .. (C.Transform.has(id) and " onmap" or "") .. (C.GoodsTransporter.has(id) and " transporter" or "")
                        .. (C.GoodsGatherer.has(id) and " gatherer" or "") .. (C.GoodsProducer.has(id) and " producer" or "")
                end
            end
            break
        end
    end
end
return table.concat(out, " | ")
