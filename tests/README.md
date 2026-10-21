# Testing without the game

`run.ps1` runs everything; build first with `build.ps1`. There are three kinds of tests:

- **Helper unit tests** (`HelperTests.cs`, scenario `HelperUnits`): the helper's sync logic on its own: build events,
  the host's change log, pending tile edits, drift decisions, area hashes and repairs of the tile map; invites; the LAN
  announcement's format and parsing, which hosts the lobby keeps (own, other versions, expiry, one per host), button
  labels and broadcast addresses.
- **Lua tests** (`lua/sync_test.lua` with the stand-in engine `lua/engine.lua`, scenario `LuaSync`): the mod's
  `lua/hooks.lua` and `lua/sync.lua` running on Lua 5.4 (`LuaRunner.cs`, a minimal `lua.exe`) against a fake
  entity/component world: reporting builds, replaying friends' builds, retries, undo, digests and repairs, and two
  "PCs" racing for one tile. `lua/cars_test.lua` covers `lua/cars.lua`: the host reporting who owns which car, and a
  friend's game giving the host's owners the same kind of car and taking back cars only it has, only from people out of
  town, a few at a time. Run them alone with `.testrun\bin\luarun\luarun.exe tests\lua\sync_test.lua`.
  `lua/menu_test.lua` (scenario `LuaMenu`) does the same for `lua/menu.lua`, the main menu's buttons, with its own small
  stand-in for the engine: letters and shadows, clicks and hovering by cursor position, a menu sent before the camera exists,
  and a row of four buttons too wide for a 720p screen drawn smaller, on the same baseline, with its buttons still clickable.
- **Scenarios** with the stand-in game: `FakeGame.cs` builds a `carparkcapital.exe` that does what the mod's Lua side
  does at the helper's end: it reads the inbox file, acks messages, prints `MP>` lines, "loads" a city on `loadworld`,
  exits on `leave`, keeps a set of object keys (confirming or blocking friends' builds, digest, dump, reconcile).
  These test the helper and the network protocol between real helper processes.

None of them can test what only the real game does: whether its placements, demolitions and memory layout behave as
the stand-ins assume. Roads, floors and land can't be scenario-tested at all (the fake game has no tile map in memory).

## Automated run

```
powershell -ExecutionPolicy Bypass -File build.ps1
powershell -ExecutionPolicy Bypass -File tests\run.ps1 [-Only LobbyHostLeaves,HostQuit]
```

`run.ps1` sets up `.testrun\t\host` and `.testrun\t\client` as below. It starts both helpers with
`--allow-second-instance --control <dir>\helper-control.txt --lan-port <port base + 1000>`, so games announced on the real
network (UDP 27778) don't show up in the tests, and a test run with another port base doesn't see this one's; stdin is an empty file, so nothing waits on a prompt. It drives
the helpers and fake games through their control files and checks the helper and fake-game logs. Everything it starts is
closed afterwards. A failed scenario's folders are copied to `.testrun\failed-<scenario>`.

## Manual setup

1. Build `FakeGame.cs` as `carparkcapital.exe` (`build.ps1` puts it in `.testrun\bin\fake`).
2. Make two game folders, `host` and `client`, each containing that exe plus these files
   (the installer patches them, so they need the anchor lines):
   - `assets/scripts/entities/3d/MainMenu.lua`: `function create(menu)` / `    setName(menu, "Menu")` / `end`
   - `assets/scripts/entities/simulation/GlobalStorage.lua`: `function create(entity)` / `    setName(entity, "Global Storage Entity")` / `end`
   - `assets/scripts/entities/simulation/_catalogue.lua`: anything
   - empty `assets/scripts/ui_screens/` and `saves/` folders
3. Keep every path under 260 characters: .NET Framework apps won't start from longer paths.

## Running

```
CPC-Multiplayer.exe host --game <host> --name Hosty --port 27877 --allow-second-instance
CPC-Multiplayer.exe join 127.0.0.1:27877 --game <client> --name Clienty --allow-second-instance
```

Drive a fake game by writing one command to `control.txt` in its folder:
`level` (load a city and autosave it), `menu`, `save`, `exit`, `build <key> [cost]` / `demolish <key> [refund]` (the player
builds or demolishes), `sneak <key>` / `drop <key>` (an object appears or vanishes without being reported, to make the cities
drift), `click <id>` (the player clicked a main menu button: `host`, `join`, `leave`, or a found game's `lan:<ip>:<port>`), and the price and money commands
below. The fake game logs what the main menu shows as `menu [<button ids>] <line> | <line>`.
Drive a helper the same way through the file given with `--control`: one command per line, as you'd type them
(`host`, `join <addr>`, `leave`, `say <text>`, `status`, `quit`, ...).
Set `FAKEGAME_IGNORE_LEAVE=1` for the client to check the helper force-closes a game that doesn't close, and
`FAKEGAME_BLOCK=<key>` for a game where a friend's build of that key doesn't fit.
Each fake game logs what it received to `fakegame.log`; the helpers log to `multiplayer/runtime/helper-*.log`.

## Scenarios checked for 0.3.0

| Scenario | Expected |
|---|---|
| Host `level`, client joins | Client gets `loadworld` and is "in the host's city" |
| Host `menu` | Client game gets `leave` and exits, the helper restarts it, client rejoins after host `level` |
| Host types `quit` | Client game gets `leave` and exits, the client helper exits, `saves/MP_*.cpcp` deleted |
| Host helper killed | Client sees the connection drop and closes its game |
| Client game ignores `leave` | Helper kills it after 8 s |
| Host helper frozen (suspended) | Client gives up after 20 s without data |

## Scenarios added for 0.4.0 (lobby mode)

Each starts both helpers with `lobby`, so the games start before anyone picks host or join.

| Scenario (`run.ps1`) | Expected |
|---|---|
| NoModeIsLobby | Started with no mode (double-clicked), the helper starts the game in the lobby without asking anything; `host` then works |
| LobbyHostJoin | Games get `hello` role `lobby`. The host runs `host` then `level`; the client's `join` reaches the host's city. The address lands in `settings.json` and chat arrives |
| LobbyBadJoin | A dead address, a bad port and `join` with no history all fail and leave the client in the lobby; it can join afterwards |
| LobbyHostLeaves | Host `leave`: the host game goes back to role `lobby` with the city still open. The client game closes, restarts in the lobby, and `MP_*.cpcp` is deleted. Host `host` again shares the open city, and client `join` (last address) gets back in |
| LobbyClientLeaves | Client `leave`: the host sees it go, the client game restarts in the lobby, and the client can join again |
| LobbyHostQuits | Host `quit`: the client game restarts in the lobby and the client helper keeps running. The client game exiting then ends the helper |
| LobbyHostGoneBeforeCity | The host leaves before loading a city: the client goes back to the lobby without its game being closed |
| MenuButtons | The main menu shows Host and Join and what Join will use. Join with no address explains; Host hosts and shows the invite and Stop; the client's Join reaches its last host and shows "Joined Hosty"; the host's menu counts the friend; Stop ends hosting. Run with `--no-clipboard` |
| LanJoin | No copying: the host presses Host, and within seconds the client's menu has a `lan:<ip>:<port>` button and its window names the game; clicking it joins Hosty and the address is remembered. Back in the lobby the button is there again; after the host's Stop it goes within 15 s. The host never offers its own game |
| LanBlocked | Another program holds the discovery port exclusively: the helpers log that they can't listen, and hosting and joining work as before |
| LuaMenu | `lua/menu.lua` against its stand-in engine (see Lua tests above) |

HostJoin (which includes the host going to the menu), HostQuit, HostKilled and IgnoreLeave cover the 0.3.0 table.
The frozen-host case is not automated.

## Scenarios added for 0.4.0 (sync)

| Scenario (`run.ps1`) | Expected |
|---|---|
| ClientBuildAccepted | A client's build reaches the host's game as a proposal; the host confirms it (`ok`). A host build reaches the client |
| PlaytestLog | Both playtest logs (`multiplayer/logs`) show a client's build going out, coming in, into the host's game and back from its mod; the city and a shortened digest arrive; a client's `note` lands in both logs with a snapshot; `status` writes one, with the client's clock difference. The client's log reaches the host from its first line, without the `log` messages in it; after `reconnect` new lines arrive and none are written twice |
| NoShareLog | A client started with `--no-share-log` sends the host no log |
| ClientBuildRejected | The host's game can't place a client's build: the host takes it back, the client is told and its game gets `undo` |
| ThreePlayers | A client's build reaches the other client straight away; when the host can't place it, both clients get `undo` |
| InstantJoinLobbyCity | A host opens a city in the lobby and builds; after `host`, a client joins without anyone saving, and gets the build made after the save |
| DriftRepair | The client has an object the host doesn't, and lacks one the host has: within a few digests the client's game reconciles both |
| VersionRefused | A helper with another mod version gets `refused` |
| OldHostRefused | A client that reaches a host with another mod version (which welcomes it) leaves and goes back to the lobby |

## Scenarios added for 0.4.0 (prices)

The fake game keeps a table of parking prices. `price <key> <value>` in its `control.txt` acts as the player changing one
(it prints a numbered `setting` line). It answers `settings?`, logs `applied <key> ParkingEntry.ratePerMinute=<value>` for
every value an inbox `settings` changes, and `pause` / `resume` stop and start its inbox reading. Like the mod, it skips a
value whose `seen` is older than its own edit of that price (logged as `stale <key>=<value>`). `dump` logs every price as
`price <key>=<value>`.

| Scenario (`run.ps1`) | Expected |
|---|---|
| PriceHostToClient | The host's game asks its game for prices when the city loads; a price the host sets reaches the client's game |
| PriceClientToHost | A price the client sets reaches the host's game, and the host's broadcast doesn't re-apply it on the client |
| PriceOnJoin | A price set before the client joined reaches its game after loading (`sync?`) |
| PriceCoalesce | Four price changes while the client's game is "paused" leave one pending `settings` message; after resuming only the newest is applied |
| PriceBothEdit | The host changes a price and the client changes it 0.4 s later: the host's game takes the client's value, and both end the same |
| PriceStaleAtHost | A client's price waits in the paused host game's inbox when the host changes the same one: the waiting value is never applied, even after another price arrives, and both end at the host's value |
| PriceStaleAtClient | The host's price waits in the paused client game's inbox when the client changes the same one: the client keeps its edit, and both end at it |

## Scenarios added for 0.4.0 (money)

The fake game keeps its money in native memory, with a copy refreshed every tick (like the HUD's) and a decoy that only
moves with transactions, so the helper has to find it the same way as in the real game. Its saves hold the balance like
the game's (zlib + CBOR), and a client takes it from the host's save. It runs inbox `moneymark` and `spend`, and has
the control commands `money <value>`, `build <key> <cost>` (or `place`) and `demolish <key> <refund>`. `dump` also logs `money=<value>`.
Each folder gets a `TestFlag` template costing 50.

| Scenario (`run.ps1`) | Expected |
|---|---|
| MoneyFound | Both helpers find their game's money; the markers leave it as it was |
| MoneyHostToClient | The host's money changes, and the client's game follows |
| MoneyClientIncomeIgnored | The client's own game earns something; it goes back to the host's balance |
| MoneyClientBuilds | A client build is charged to the host's game once, and both end the same |
| MoneyBothBuild | The host and client build at the same moment: both costs come off |
| MoneyRefund | A client demolition gives half the price back |
| MoneyUnpricedBuild | A build with no template price is charged what the client's game spent |
| MoneyHostPaused | The host's game is paused: the client shows the cost at once and keeps showing it until the host applies it |

Checked in the real game (two copies on one PC): a price set on either side shows on the other within 2 s, without
flicking back while the slider is dragged; a new entrance a friend builds and prices arrives with that price; a price
changed while paused goes out after unpausing. The price slider set both entrances of the test city at once, and both
were synced. `Pricing.price` (shops, restrooms) is not tried yet.

Money, checked in the real game: both helpers found their balance (the host's out of 16,418 candidates). A friend's
KeyMan cost the wallet 400, its demolition gave 200 back, and host and friend builds 4 s apart both came off. A friend's
15 road tiles cost 180, the same as the friend's own game charged. The host's income reached the friend every 2 s. Land
plots cost 7,177.50 and 4,300 (not per cell); a friend's land purchase was free in that build, which led to charging
what the friend's game spent (not retried in game yet).

## Scenarios added for 0.5.0

The fake game keeps a speed behind the same pointer chain as the game's (Level, rooms[0], room+0x48c). With
`CPC_TEST_LAYOUT=<file>` set it writes the addresses to that file in its folder, and the helper reads them from
there instead of using the game build's offsets. `speed <value>` in its `control.txt` acts as the player pausing (0)
or changing the speed. It logs `speed now <v>` whenever the helper changes it, and `dump` adds `speed=<v> sim=<s>`.
Its clock (`sim` in heartbeats) runs at that speed, from the wall clock; `FAKEGAME_SIM_BEHIND=<s>` starts a city that
far behind.

`lock <value>` ticks "apply to all" for parking: every tick the stored price (a map node in native memory, "parking"
then a float) is copied onto every entrance. A price the helper had it apply a moment before is reported as
`settingrevert`; any other change as `setting`, as `lua/settings.lua` does. With `FAKEGAME_LOCK_HIDDEN=1` the stored
price isn't in memory where the helper can find it. Inbox `typevalue?` is answered with every entrance's price.

| Scenario (`run.ps1`) | Expected |
|---|---|
| MoneyHostIncomeFast | The host's money rises; the client shows it within 2 s (it was up to 3.5 s) |
| StatsHostToClient | Host and client games compute other carbrainness (`carbrain 0.8` / `0.3`). The client helper finds the stats snapshots in its game's memory and writes the host's over its newest one, again for each new one its game adds; a new host value follows. The host never takes the client's. The stand-in keeps a Stats system at room+0x598 with a growing (and moving) vector of 0xe0-byte snapshots, like the game |
| CarsHostToClient | The host's game reports its car owners (`buycar`, `sellcar`); the client's game gets the list (inbox `cars`) after each change. A purchase in the client's game doesn't reach the host. A client joining later gets the list once its city has loaded |
| SpeedHostToClient | The host sets 3x, then pauses; the client's game follows, and the client is told who paused |
| SpeedClientToHost | The client pauses, then sets 2x; the host's game follows, and the client's own changes aren't undone |
| SpeedBothChange | Both change it at once; both end at the change that reached the host last |
| SpeedClockCatchUp | The client's clock starts 30 s behind: it runs faster until it has caught up, then at the host's speed |
| SpeedNoLayout | A game build the mod doesn't know: the helper says speed isn't shared, and builds still sync |
| PriceLockedAtHost | "Apply to all" is ticked on the host: the client's price is undone there at first, the host's helper finds the stored price and changes it, and every entrance on both PCs ends at the client's price; the next change goes straight in |
| PriceLockedHidden | The stored price can't be found: the host is told why friends can't change it, and the client goes back to the host's price |

ClientBuildRejected now also checks that the client is told the game's reason.
`lua/settings_test.lua` (run in LuaSync) checks the mod's side of prices: a player's edit is reported, a friend's
price isn't echoed, a price the game puts back under "apply to all" is a `settingrevert` and not an edit, and the
kinds of price are named and listed. `lua/sync_test.lua` adds the block reason and the build tool's floor painting.

`-Bin <folder>` runs the scenarios from another build folder (`build.ps1 -Out <folder>`), e.g. while a helper still
runs from `.testrun\bin`.
