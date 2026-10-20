# Testing without the game

`run.ps1` runs everything; build first with `build.ps1`. There are three kinds of tests:

- **Helper unit tests** (`HelperTests.cs`, scenario `HelperUnits`): the helper's sync logic on its own: build events,
  the host's change log, pending tile edits, drift decisions, area hashes and repairs of the tile map.
- **Lua tests** (`lua/sync_test.lua` with the stand-in engine `lua/engine.lua`, scenario `LuaSync`): the mod's
  `lua/hooks.lua` and `lua/sync.lua` running on Lua 5.4 (`LuaRunner.cs`, a minimal `lua.exe`) against a fake
  entity/component world: reporting builds, replaying friends' builds, retries, undo, digests and repairs, and two
  "PCs" racing for one tile. Run them alone with `.testrun\bin\luarun\luarun.exe tests\lua\sync_test.lua`.
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
`--allow-second-instance --control <dir>\helper-control.txt`; stdin is an empty file, so nothing waits on a prompt. It drives
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
`level` (load a city and autosave it), `menu`, `save`, `exit`, `build <key>` / `demolish <key>` (the player builds or
demolishes), `sneak <key>` / `drop <key>` (an object appears or vanishes without being reported, to make the cities drift).
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
| LobbyHostJoin | Games get `hello` role `lobby`. The host runs `host` then `level`; the client's `join` reaches the host's city. The address lands in `settings.json` and chat arrives |
| LobbyBadJoin | A dead address, a bad port and `join` with no history all fail and leave the client in the lobby; it can join afterwards |
| LobbyHostLeaves | Host `leave`: the host game goes back to role `lobby` with the city still open. The client game closes, restarts in the lobby, and `MP_*.cpcp` is deleted. Host `host` again shares the open city, and client `join` (last address) gets back in |
| LobbyClientLeaves | Client `leave`: the host sees it go, the client game restarts in the lobby, and the client can join again |
| LobbyHostQuits | Host `quit`: the client game restarts in the lobby and the client helper keeps running. The client game exiting then ends the helper |
| LobbyHostGoneBeforeCity | The host leaves before loading a city: the client goes back to the lobby without its game being closed |

HostJoin (which includes the host going to the menu), HostQuit, HostKilled and IgnoreLeave cover the 0.3.0 table.
The frozen-host case is not automated.

## Scenarios added for 0.4.0 (sync)

| Scenario (`run.ps1`) | Expected |
|---|---|
| ClientBuildAccepted | A client's build reaches the host's game as a proposal; the host confirms it (`ok`). A host build reaches the client |
| ClientBuildRejected | The host's game can't place a client's build: the host takes it back, the client is told and its game gets `undo` |
| ThreePlayers | A client's build reaches the other client straight away; when the host can't place it, both clients get `undo` |
| InstantJoinLobbyCity | A host opens a city in the lobby and builds; after `host`, a client joins without anyone saving, and gets the build made after the save |
| DriftRepair | The client has an object the host doesn't, and lacks one the host has: within a few digests the client's game reconciles both |
| VersionRefused | A helper with another mod version gets `refused` |
| OldHostRefused | A client that reaches a host with another mod version (which welcomes it) leaves and goes back to the lobby |
