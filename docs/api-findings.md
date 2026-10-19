# Phase 0 findings: prices, settings and money (for 0.4.0)

How these were found:
- **Static search** of the game's own Lua (`<game>/assets/scripts/`) and of the strings in `carparkcapital.exe`. Component
  field names come from the engine's reflection strings.
- **A decoded save** (zlib + CBOR, see "Save format" below).
- **Live probes** with the new `--dev` console. The probes ran in a fresh City game, with the player changing values in
  the UI between dumps. Money was located by scanning the game's memory from outside (a throwaway C# tool, not in the
  repo).

Probe results are quoted as they came back from `devout.txt`.

## 1. Where player-set prices and settings live

**Parking price: `ParkingEntry.ratePerMinute`, one per entrance.** The default is 0.06.

| Step | `ParkingEntrance@290,8,240/0/0` | `ParkingEntrance@297,8,242/1/0` |
|---|---|---|
| Before | 0.06 | 0.06 |
| Player sets 0.37 in the UI on the first one | 0.37 | **0.37** (and `BadPricing` appeared) |
| Script sets the first one to 0.15 | 0.15 | 0.37 |

- The UI's price box writes **every** entrance in the city, but the value is stored per entrance.
- After a script write, the UI showed 0.15 and 0.37: the game shows each stored value and does not re-link them.
- So a script must set each entrance itself. In practice all entrances hold the same price.
- `ParkingExit` has no price, only `totalMoneyTransacted`, which is a statistic.

**Shops, drive-thru stalls, restrooms, vending machines, the gas pump, the car carousel and the golf cart:**
component `Pricing`.
- Fields from the engine's reflection list: `price`, `minPrice`, `maxPrice`, `bCarbrainBased`, `numSold`, `moneyMade`,
  `priceText`, `syncId`.
- Templates set `syncId = getPricingId("<kind>")`, for example `"restroom"`, `"soda_vending"`, `"fries_stall"` or
  `"gasoline"`. The room keeps a `syncedPrices` map in `money.syncedPrices` in the save.
- So these prices are very likely shared per kind by the game itself, the same way the entrance price box writes every
  entrance.
- **Not probed in game yet:** no priced object was built in the test city. In Phase 1, build a vending machine or
  restroom and repeat the entrance diff on `Pricing`.

**Other player-set state found:**
- `ShutDownVehicleBarrier`: a component with no fields. Its presence means an entrance or exit was closed by the
  player. It is in the entrance's persistence list.
- `Paused`: presence means a pausable business was paused. `Upkeep.pausedMultiplier` is 0.5.
- **Not synced:**
  - `BadPricing` and `BadPricingAlert` are the game's own alerts, derived from the price.
  - `VehicleBarrier.bHoldVehicles` is simulation state.
  - `Upkeep.costPerDay` (400 for entrances and exits) is a template constant.

The Phase 1 whitelist therefore needs **marker components** (present or absent) as well as fields:

```lua
{ component = "ParkingEntry", fields = { "ratePerMinute" } },
{ component = "Pricing", fields = { "price" } },            -- verify in Phase 1
{ marker = "ShutDownVehicleBarrier" },                      -- verify toggling it from Lua works
{ marker = "Paused" },                                      -- verify
```

## 2. Can a script set them?

- **Yes for `ParkingEntry.ratePerMinute`.** A plain field write
  (`component.ParkingEntry.getFor(e).ratePerMinute = 0.15`) showed up in the game's UI. Nothing reverted it.
- **Not tested yet:**
  - `Pricing.price` writes, and whether the game copies them to the other objects with the same `syncId`.
  - Adding and removing the `ShutDownVehicleBarrier` and `Paused` components. These may need the game's own toggle
    logic, for example the barrier/nav updates behind the `OpenedChanged` entity event.

## 3. Money

- **Lua cannot read the balance.**
  - `system.Stats` exposes no functions, and nothing in `_G` or the room environment returns money.
  - `scenario` only has `startingFunds`, `maxLoan` and `loanInterest` (all functions).
  - `transactMoney` returns nothing.
- **Lua can change it.** `transactMoney(amount, TransactionCategory.MISCELLANEOUS, vec3(0, 0, 0), true)` added +1000
  and then +500, both visible in the HUD. The last argument shows the money popup.
  - Categories, from the engine strings: `SHUTTLE_SERVICE, OIL_CONSUMPTION, OIL_PRODUCTION, POLICE, VEHICLE_STOCK,
    VEHICLE_SALES, PARKING, DRIVE_THRU, PROPAGANDA, INTEREST, REFUND, CONSTRUCTION, CHEAT, MISCELLANEOUS`.
  - `pairs(TransactionCategory)` lists nothing, but indexing a name works.
- **The balance is a `double` in the game's memory.**
  - Scanning for doubles in 60290..60300 (the HUD showed 60,295) found 3 values:

    ```
    d1c94fd158 60295.53125
    255a1fc1840 60298.222219228745
    255e33a13f0 60295.55555254221
    ```
  - After `transactMoney(+500)` all three rose by about 496 (the 500 minus a few seconds of upkeep).
  - Writing 70000 to `255e33a13f0` made the HUD show 70,000 and keep counting from there. The `d1c94…` copy followed
    the write; the `255a1…` value did not.
  - So `255e33a13f0` is the balance, `d1c94…` is a display copy, and `255a1…` is something else (a statistic?).
- **Locating it without the player's help (proposal for Phase 2):**
  1. Take the expected value: the `money.balance` in the save the city was loaded from. On the client that is always
     the host's save.
  2. Scan for 8-aligned doubles within a margin of it. This took 19 s over the whole process; limiting it to heap
     regions should be faster.
  3. Have the mod make a marker transaction, `transactMoney(+0.25)` then `-0.25`.
  4. Keep the candidate that moved by exactly 0.25. If more than one moves, write a test value to each and keep the
     one the others follow.

  For a host whose city was never saved, locate it after the first autosave.
- **Setting a client's money to the host's:** write the double directly, which updates the HUD and adds nothing to the
  finance history. Or apply the difference with `transactMoney`. The write is cleaner. Spends applied on the host
  should go through `transactMoney` (`CONSTRUCTION`, or `REFUND` for refunds), so they show up in the host's finances.

## 4. Build price and refund

- **No runtime lookup.**
  - Catalogue entries only expose `placeableTemplateIndex`, `chainIndex` and `driveThruTemplateName`.
  - `PlaceableTemplateInfo` goes into `registerPlaceable` at template load and isn't readable afterwards.
- **Fallback that works:** the helper parses `buildPrice` (and optional `refundMultiplier`) from
  `<game>/assets/scripts/entities/simulation/**/*.lua`, keyed by file name. That name is the template name in
  `Persistent.applyTemplateOnLoad` and in our event keys.
  - There are 165 `buildPrice` lines, mostly literal numbers.
  - Generated templates (building pieces, pillars, drive-thru pieces) compute theirs and need special handling or a
    Lua-side capture.
- **Measured with `BankNoteFlag` (`buildPrice = 50`, no `refundMultiplier`):**
  - Placing it took 66.00. Placing is impossible while paused, so this was done unpaused; the extra 16 is upkeep
    during the catalogue browsing.
  - Demolishing it gave **+25**, shown by the game. The money moved +22.33 over a few seconds of upkeep.
  - So the charge is `buildPrice` and the default refund is `0.5 × buildPrice`.
- **Unknown:**
  - whether `bApplyGlobalRefundMultiplier` (14 templates) or the object's age change the refund;
  - whether variants change the price;
  - drag placements produce one `place` event per object, so cost = count × price, presumably.

## 5. Roads, floors and land

- **Tile materials have a per-tile price**, readable from Lua (`getMaterialByName(name).price`): Grass 3, Asphalt 4,
  Sidewalk 6, Concrete 6, Steel 12. The rest are in `assets/tile_materials.json`: Dirt 2, Patchy Grass 2, Sand 2,
  Water 2, Slabs 8, Cobble Road 8, Rock 8, Scaffolding 6, Sand Road 4.
- **Unknown:** whether shape (ramps), height or removing tiles changes the cost; what land plots cost
  (`IsoLandRegistry` isn't exposed to Lua). To measure in Phase 2: money before and after building a known number of
  tiles, and buying a plot whose price the UI shows.

## 6. Change events

- No "setting changed" event was found.
- The only related entity events in the game's scripts are `OpenedChanged` (barrier) and `EntityTemplateChanged`.
- Settings changes have to be found by **polling**, as the plan assumes.

## Problem found: the mod does not run while the game is paused

The 0.3.0 README says a frame callback keeps the mod running while paused. In this test it didn't:
- `setUpdateFunction(hostEntity, 0.0, frame)` registered without an error.
- A dev command written at 16:21:09 while paused ran at 16:23:27, right after the player unpaused.
- The helper meanwhile warned "Your game isn't taking in friends' changes right now (paused?)".

The same happened in the 0.2.0 tests: update functions stop with the simulation.

Consequences:
- Pause doesn't lose anything, because inbox messages wait and building is impossible while paused anyway.
- Changes made by others only appear after unpausing, and the docs and the plan's assumption need correcting.
- For settings: a price changed in the UI while paused (if the UI allows it) is picked up by the next scan after
  unpausing.

## Save format (for the helper's money lookup)

`zlib( u64 cborLength | CBOR document | u64 blobLength | tile blob )`

The CBOR document is nlohmann's JSON-as-CBOR:
- `rooms[0].money` holds `{ balance, earned, loan, spent, syncedPrices }`. One example: `balance = 59723.33333134651`,
  `loan = 0.0`, `syncedPrices = {}` in a city without priced shops.
- Entities sit in `rooms[0].entities[]`, each with `template` and `components`. A parking entrance's
  `ParkingEntry.ratePerMinute` is saved there.

## Still unknown (to settle during Phases 1 and 2)

1. `Pricing.price`: per object or per `syncId`, settable from Lua, and what `syncedPrices` holds.
2. Toggling `ShutDownVehicleBarrier` / `Paused` from Lua.
3. Refund rules beyond the default 0.5 (global multiplier, age).
4. Tile cost by shape and height; land plot prices.
5. A reliable automatic way to find the money address (method proposed above, not built yet).
