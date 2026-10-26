# Status and next steps

Where multiplayer stands, what was checked in the real game, and what's next. Last updated 2026-10-20.
The README has the user-facing description of each feature; `docs/probes-0.5.md` the in-game checklist.

## Where the code is

| Branch | State |
|---|---|
| `main` (pushed, `bbe06a1`) | Everything below: 0.5.0 phase 1, the main menu's Host/Join buttons and LAN discovery, and (merged from `feature/stats-sync`) per-player dev files, city statistics, car ownership, no car sales on a friend's game, pause, "apply to all" boxes and prices, `tools/` |

The game is installed at `C:\Games\Car Park Capital`. `CPC-Multiplayer-test4.exe` there is the build of `main` as of
the "apply to all" commit; `CPC-Multiplayer.exe` is an older build, and `-test`, `-test2`, `-test3` are older still (a
running helper locks its exe, so new builds were copied in under new names). Make the newest one `CPC-Multiplayer.exe`
and delete the others once no helper runs.

## Features, checked in the real game

Two copies of the game on one PC (host and friend), city `ix`.

| Feature | Status |
|---|---|
| Host / Join buttons on the main menu, LAN "Join <name>" button | Works |
| Builds, demolitions, roads, floors, land ownership (0.4.0) | Works (land overlay and eviction: see below) |
| Money follows the host | Works |
| Game speed | Works |
| Pause (0.5.0+) | Works. Space sets a flag (`Level + 0x8`), not the speed |
| Day and time of day | Works (clock nudging) |
| Per-object prices | Works |
| "Apply to all" boxes and stored prices (0.5.0+) | Works, both directions, ticks included |
| Suburban homes and the McCarMansion built by a friend | Works (the house paints its own plot) |
| City statistics on the HUD: carbrainness, civil unrest, need fulfilment, car dependency progress (0.5.0+) | Works: the friend's newest stats snapshot is overwritten with the host's |
| Car ownership (0.5.0+) | Works: the friend's helper log showed it following the host's 97 owners, none waiting or missing |
| Protest damage, the host's only (branch `fix/protest-damage`) | Works (2026-10-26, city copy `mpcheck`): a lamppost destroyed on the friend's game wasn't sent and came back in 7 s; the friend's demolish tool reached the host as `demolish` at once, refund included; the host's own protest damage still reached the friend; no protest banners sent |
| A friend's car carousel sells nothing (0.5.0+) | In place (`system.VehicleShop.tryBuy` refuses on a friend's game); never seen refusing, because both carousels were empty during the tests |

## Not shared yet, in the user's order of importance

The user's goals: the statistics first (done), then everything that changes the game's state, especially cars
entering and leaving lots and drive-thrus, car sales, and protests. 1:1 positions of every pedestrian don't matter.

1. **Car carousel stock and sales (next).** The user wants the friend's carousel to show the host's stock and a sale to
   happen on both at the same moment, not to show nothing on the friend's side (as now). Facts found:
   - A carousel is a `CarCarousel` placeable with `VehicleShop`, `Pricing` (`price`, `numSold`, `moneyMade`; these
     differed between the games: 68 vs 59 sold), `GoodsStorage`, and `VehicleShopEmpty` while it has no stock.
   - Buying happens in `assets/scripts/behavior/other/car_shopping.lua`, which calls `system.VehicleShop.tryBuy(person,
     shop)`. `system` is a plain Lua table per room, so the mod can wrap it (it already does, in `lua/cars.lua`).
   - Lua also gets `system.Goods.getStorageCount`, `popItemFromStorage` and `pushItemToStorage`; stock comes by
     car-carrier trucks (`CarCarrierGarage`).
   - Idea: the host's mod reports each purchase (who, which carousel) from its own `tryBuy` wrapper; the friend's mod lets
     that one purchase through (or makes the car and pops the stock itself), and stock counts are mirrored with the
     Goods functions.
2. **Cars entering and leaving lots and drive-thrus**, the cause of the friend's parking income appearing and vanishing.
   - Every car is a resident's own (`OwnedVehicle`/`VehicleOwner`, templates Cabriolet, BananaCar, BubbleCabin, ...),
     the same people by name on both sides. About 10-15 drive at once; people out of town have no `Transform`.
   - Parking is a `ParkingEntryTicket` on the person: `entryTime`, `ratePerMinute`, `parkingEntrance`, `bPaid`,
     `usedParkingSpace`. A car's `Transform.position` can be written from Lua and a parked car stays put; a moving
     car wasn't tried. Writing `PersonFSM.state` is accepted but doesn't stop the person.
   - Idea: the host streams its owned cars' positions, tickets and drive-thru state; the friend's game places its
     copies there instead of driving them, so they leave lots and pay when the host's do.
3. **Protests.** They already start in the same place on both games (shared unrest and buildings) with different
   people. A protester is a `Person` with `Protesting` (`leader`, cooldowns), `ProtestBlockingTarget` (`target`) and
   `PersonFSM.state` 700; the banner is a `ProtestBanner` entity. Only the host's protest damage reaches the city now
   (branch `fix/protest-damage`): a friend's game keeps what its own protesters wreck to itself, and gets the host's
   copy back.
4. **Land** (map overlay and eviction): `docs/probes-0.5.md` section B.
5. **People who exist only on the host** (newcomers are random on each PC): counted as `missing` by car ownership.
6. The Car Dependency **level** (`room + 0x240`) levels up from each game's own counts. The user wants that to follow
   from real cars, not a written number.

## How to work on it

- **Two copies on one PC**, started from `C:\Games\Car Park Capital` (the user starts them):
  ```
  CPC-Multiplayer.exe host --city ix --allow-second-instance --console --name HostPC --dev --dev-write --control multiplayer/runtime/control_HostPC.txt
  CPC-Multiplayer.exe join 127.0.0.1 --allow-second-instance --console --name FriendPC --dev --dev-write --control multiplayer/runtime/control_FriendPC.txt
  ```
- **Running Lua in a game** without the user: write `lua <code>` into that player's control file, or
  `lua return loadfile("multiplayer/<file>.lua", "t", MP.env)()` for a file in the game's `multiplayer` folder. The
  result shows as a `lua>` line in that helper's playtest log (`multiplayer/logs`, newest `_host`/`_join` file).
  Timers can report later with `MP.emit("eval", { id = 0, ok = true, out = ... })`. `tools/probes/` has the probes used
  so far. The game's Lua has no `io`.
- **Finding every entity**: ids are `index + version * 2^20`; loop index 0..40000 and versions 0..255, stopping at the
  first valid id per index (`tools/probes/probe_protest.lua`). Scanning versions 0 only misses reused slots.
- **Game memory**: `tools/MemTool.cs` (build and use: see its header). The game's pid is the child of the helper
  (`Get-CimInstance Win32_Process`). Pause, stats and "apply to all" were each found by snapshotting memory every
  second while the user toggled the thing a few times, then looking for bytes that switch in step.
- **Tests**: build with `build.ps1 -Out .testrun\bm`, and run a copy of `tests\run.ps1` with `$script:port = 29800`
  and `-Bin <full path to .testrun\bm>`. 53 scenarios pass on `main`.
- **Pitfalls**: Windows PowerShell 5.1 `Get-Content`/`Set-Content` mangle UTF-8 files (README arrows) and add a BOM;
  edit with an editor or a Python script file. Writing a game's memory changes its state for real (a test price of
  0.31 reached the host as a player's change).

## Native layout (exe SHA-256 `97DCE8EF...684A87`, in `src/Sync/NativeLayout.cs`)

| What | Where |
|---|---|
| Level | `[exe + 0x1bbaa10]` |
| Paused flag (byte, 1 = paused) | `Level + 0x8` |
| City room | `[[Level + 0x10]]`, vtable `exe + 0x17bf968` |
| Speed (float) | `room + 0x48c` |
| Car Dependency level (int) | `room + 0x240`; level thresholds vector at `room + 0x228` |
| "Apply to all" boxes / stored prices | `room + 0x400` (`std::vector<bool>`, bit count at `+0x18`) / `room + 0x420` (`std::vector<float>`), pricing-id order |
| Stats system | `[room + 0x598]`, vtable `exe + 0x17c0330`; snapshots vector at `+0x48`, 0xe0 bytes each, newest last |
| Stats snapshot | `+0x00` game time, `+0x08` balance, `+0x40` parking rate, `+0x50` ints (cars owned at `+0x64`, parking spaces in high demand at `+0x5c`), `+0x90` floats (civil unrest, happiness, carbrainness, buyACar, ...) |
