# In-game checks for 0.5.0 and what comes after

0.5.0 changes things only the real game can confirm, and the next steps (land, cars and people, city statistics)
need facts about the game that can't be found by reading its scripts. Each check below says what to do, what to
send back, and what the answer decides.

Results so far are in `docs/status.md`: P1 (pause and speed), P2 ("apply to all") and P3 (suburban home) pass; P4 and
P11 led to the findings there (statistics, cars, protests).

## Setup

Start the helper with the developer console:

```
CPC-Multiplayer.exe lobby --dev --dev-write
```

Load a city. The console files are in `<game>\multiplayer\runtime\`:
- **`devcmd.txt`**: Lua, run in the game once it's saved. Results go to `devout.txt`. The game must be
  unpaused for this, because the mod's Lua stops while paused.
- **`devmem.txt`**: memory commands, one per line. Results go to `devmemout.txt`. These work while paused.
  Addresses are hex.

Send back `devout.txt`, `devmemout.txt`, and the playtest log (`multiplayer\logs\`, newest file). Use
`note <text>` in the helper window to mark each step in the log.

## A. Checks for what 0.5.0 already does

**P1. Pause and speed (two players).**
1. Host and join as usual.
2. Press Space on either PC: the other should pause within a moment, and its window should say who paused.
3. Do the same with each speed button, from both PCs.
4. Note whether the other PC's speed buttons and pause icon show the new state. The helper changes the speed
   value the HUD reads, but the buttons might keep their own state.
5. Note whether opening the Esc menu pauses everyone.

If speed doesn't follow, write `chain` in `devmem.txt` before and after pressing Space and send `devmemout.txt`.
- **This decides:** whether speed sharing stays on. If the game ignores or undoes the writes, the helper switches
  to read-only by itself and says so.

**P2. "Apply to all".**
1. On the host, tick "apply to all" for a restroom or vending machine and set a price.
2. On the friend's PC, change that kind's price.
3. Within a few seconds the host's log should say "found the game's stored price for <kind>", and both PCs
   should show the friend's price.

If it says "not found in the game's memory" instead, write these in `devmem.txt` and send the result:
```
find str restroom
find f32 <the price shown>
find f64 <the price shown>
```
- **This decides:** the layout to search for. The map node is assumed to be a short `std::string`, then a float
  or double.

**P3. Suburban home.**
1. A friend builds suburban homes and a McCarMansion, a few times, with the build tool's floor painting on and off.
2. None of them should be taken back.
3. If one is, its message now names the reason. Send it and the log.

**P5. Placing fields.** This is needed only if P3 shows reasons like "nil" or a number. With a build ghost on
screen (unpaused), put this in `devcmd.txt`:
```lua
local out = {}
for _, e in pairs(MP.room.ghostEntity) do
  if valid(e) and component.Placing.has(e) then
    local p = component.Placing.getFor(e)
    for _, f in ipairs({ "bEvaluated", "bBlocked", "blockedReason", "blockedReasonStr", "blockedBy", "bAutoPaintFloor" }) do
      local ok, v = pcall(function() return p[f] end)
      out[#out + 1] = f .. " = " .. tostring(ok and v or "(error)")
    end
  end
end
return table.concat(out, "\n")
```

## B. Checks for land (Phase 3)

**P9. Land registry.** The helper writes the per-cell ownership bytes. The map overlay and eviction read
something else.
1. In `devmem.txt`: `tiles`. Note the land address `L`.
2. `find ptr L`. A pointer to the land array leads to the object that holds it (the land registry). Note each
   address found as `R`.
3. For each `R`: `save before R-0x200 0x2000`. Also save any other pointers you see near `R` in a `read R-0x100 0x300`.
4. Buy one plot in the game's UI.
5. `diff before`, for each saved block.
6. Send `devmemout.txt`.

- **This decides:** whether the helper can set the plot ownership and an "upload again" flag, so a friend's
  purchase shows on the map and counts for eviction. If not, see P10.

**P10. A plot through the game's own code.** The bus stop and subway templates make a land plot
(`bCreateLandPlot`). On a cell the helper made "owned" (bought by a friend, dark on your map), unpaused, put
this in `devcmd.txt`, with `X Y Z` the cell:
```lua
local e = createEntity()
local p = component.Placing.getFor(e)
p.at.tilePosition = uvec3(X, Y, Z); p.bNoCost = true; p.bTryFinish = true; p.bIgnoreLandStatus = true
applyTemplate(e, "BusStop", {}, true)
return "placed"
```
- Check whether the overlay there turns light and eviction there works.
- Then demolish the bus stop and check again.
- **This decides:** whether an invisible plot-making placeable can refresh land ownership. Otherwise a friend's
  land purchase would need a reload.

## C. Checks for cars, people and statistics (Phase 4)

**P4. The Lua API.** Unpaused, in `devcmd.txt`:
```lua
local r = {}
r[#r + 1] = "system: " .. MP.inspect(system, 2)
r[#r + 1] = "scenario: " .. MP.inspect(scenario, 2)
for _, pat in ipairs({ "[Ss]pawn", "[Ff]rustrat", "[Ll]evel", "[Ss]tat", "[Pp]rotest", "[Cc]arbrain", "[Ss]peed", "[Pp]ause", "[Tt]ime" }) do
  r[#r + 1] = pat .. ": " .. table.concat(MP.findFunctions(pat), ", ")
end
return table.concat(r, "\n")
```
- **This decides:** whether spawning, unrest or the car-dependency level can be read or set from Lua.

**P11. City statistics in memory.** Note the HUD's civil unrest (%), carbrainness (%) and car-dependency level.
Then in `devmem.txt`, with the unrest as a fraction (24% is 0.24):
```
find f32 0.24 0.005
find f64 0.24 0.005
```
1. Let unrest change, then repeat the searches.
2. Keep the addresses whose value followed the HUD.
3. Do the same for carbrainness, and for the level (`find` an `i32` isn't offered: use `read` around the city
   room from `chain`).

- **This decides:** a backstop that copies these values from the host.

**P3b. The clock.** Unpaused, in `devcmd.txt`:
```lua
return getSimulationTime(), getDayTime(), DAY_LENGTH, DAYTIME_LENGTH
```
1. Pause.
2. In `devmem.txt`: `find f64 <the first number> 0.5` and `find f32 <the first number> 0.5`. Keep the
   addresses near the city room from `chain`.
- **This decides:** whether a joiner's clock can be set straight to the host's, instead of catching up at 2x.

**P6, P7, P8. Taking over cars and people.** These need a build with extra Lua and are better done together in
one session: stopping a friend's own spawning, showing the host's cars and people as stand-ins, and removing the
friend's own. They come after P4, whose answer says which way to try first.
