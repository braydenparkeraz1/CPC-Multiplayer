-- Car Park Capital Multiplayer - shared helpers (JSON out, inspection).
local MP = ...

local function isArray(t)
    local n = 0
    for k in pairs(t) do
        if type(k) ~= "number" or k < 1 or k % 1 ~= 0 then return false end
        n = n + 1
    end
    for i = 1, n do if t[i] == nil then return false end end
    return true
end

local escapes = { ['"'] = '\\"', ['\\'] = '\\\\', ['\b'] = '\\b', ['\f'] = '\\f', ['\n'] = '\\n', ['\r'] = '\\r', ['\t'] = '\\t' }

local function encodeString(s)
    return '"' .. s:gsub('[%c"\\]', function(c)
        return escapes[c] or string.format("\\u%04x", c:byte())
    end) .. '"'
end

local function encode(v, depth)
    depth = depth or 0
    if depth > 32 then return '"<too deep>"' end
    local tv = type(v)
    if tv == "nil" then return "null" end
    if tv == "boolean" then return v and "true" or "false" end
    if tv == "number" then
        if v ~= v or v == math.huge or v == -math.huge then return "null" end
        if math.type(v) == "integer" then return tostring(v) end
        return string.format("%.17g", v)
    end
    if tv == "string" then return encodeString(v) end
    if tv == "table" then
        local out = {}
        if next(v) ~= nil and isArray(v) then
            for i = 1, #v do out[i] = encode(v[i], depth + 1) end
            return "[" .. table.concat(out, ",") .. "]"
        end
        for k, val in pairs(v) do
            out[#out + 1] = encodeString(tostring(k)) .. ":" .. encode(val, depth + 1)
        end
        return "{" .. table.concat(out, ",") .. "}"
    end
    return encodeString(tostring(v))
end
MP.jsonEncode = encode

-- Every message to the helper is a single stdout line: MP>{json}
function MP.emit(msgType, data)
    local payload = { t = msgType, d = data }
    print("MP>" .. encode(payload))
end

-- Lists the keys of a table, or of a userdata's metatable (sol2 usertypes keep members there).
function MP.keysOf(v)
    local keys = {}
    local t = v
    if type(v) == "userdata" then t = getmetatable(v) end
    if type(t) ~= "table" then return keys end
    for k, val in pairs(t) do keys[#keys + 1] = tostring(k) .. ":" .. type(val) end
    table.sort(keys)
    return keys
end

function MP.inspect(v, depth, seen)
    depth = depth or 2
    seen = seen or {}
    local tv = type(v)
    if tv == "string" then return string.format("%q", v) end
    if tv ~= "table" then return tostring(v) end
    if seen[v] then return "<cycle>" end
    if depth <= 0 then return "{...}" end
    seen[v] = true
    local parts = {}
    for k, val in pairs(v) do
        parts[#parts + 1] = tostring(k) .. " = " .. MP.inspect(val, depth - 1, seen)
    end
    table.sort(parts)
    return "{ " .. table.concat(parts, ", ") .. " }"
end
