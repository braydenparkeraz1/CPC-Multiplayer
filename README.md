# CPC-Multiplayer

Co-op building mod for Car Park Capital: a console helper (`CPC-Multiplayer.exe`) that hosts or joins a game over TCP,
plus Lua scripts it installs into the game.

The 0.2.0 source was recovered from the original `CPC-Multiplayer.exe` (a .NET Framework 4.0 console app, not obfuscated)
by decompiling it with ILSpy; that is the first commit. The Lua files in `lua/` were embedded resources in the exe.

## Build

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

This builds `CPC-Multiplayer.exe` and the stand-in game into `.testrun\bin` (git-ignored). With the .NET SDK installed it
runs `dotnet build -c Release`; without it, it downloads Roslyn (`Microsoft.Net.Compilers.Toolset`) once into
`.testrun\tools` and compiles against the .NET Framework 4 assemblies in the Windows folder, using any installed .NET runtime.

The output exe goes next to `carparkcapital.exe` (or pass `--game "<folder>"`).
.NET Framework apps can't start from a path longer than 260 characters ("This application could not be started").

Test the helper against the stand-in game with `tests\run.ps1` (see `tests/README.md`).

## Developer console

`--dev` makes the helper check `multiplayer/runtime/devcmd.txt` in the game folder once a second. The whole file is run
as one Lua chunk in the game (in the level's environment, like the `lua` command) and then deleted. Each result is
appended to `multiplayer/runtime/devout.txt`. It is off by default. `docs/api-findings.md` lists what was learned with it.

## Lobby mode (in progress for 0.4.0)

`CPC-Multiplayer.exe lobby` starts the game first and lets you pick afterwards. Type these in the window, or write them
to the file given with `--control <file>`:

| Command | Effect |
|---|---|
| `host` | Share your city: the one open in the game, or the next one you load |
| `join [ip[:port]]` | Join a host; without an address, the last one that worked |
| `name <name>` | Change your name (lobby only) |
| `leave` | End the session and go back to the lobby. A host keeps its city open; a client's game restarts at the main menu |

When the host leaves or closes the game, clients go back to the lobby instead of the helper exiting. Closing the game
in the lobby ends the helper. `host` and `join` on the command line work as before.

The helper remembers your name and the last 5 host addresses that connected in `multiplayer/settings.json` in the game
folder. On first run the name defaults to your Steam profile name (from Steam's `config/loginusers.vdf`), then your
Windows user name.

## Changes in 0.4.0 (in progress)

- **Prices are shared.** Parking entrance prices (`ParkingEntry.ratePerMinute`), and shop, drive-thru and restroom prices
  (`Pricing.price`, not yet tried in game), are the same on every player's PC.
  - The host's game holds the true values. Clients send only what they changed. The host applies changes in the order
    they arrive, broadcasts what changed every 2 s, and sends a full copy every 30 s and to anyone who finishes loading
    its city.
  - The mod (`lua/settings.lua`) applies only values that differ, and holds a friend's price until that friend's object
    exists here.
  - Edits are numbered, so a player's own edit isn't undone by an older value that was already on its way (no flicker
    while dragging the slider). Every newer value still applies: when two players change the same price at once, the
    one that reaches the host last wins on every PC.
  - Inbox messages of this kind replace each other, so a paused or loading game doesn't collect stale copies.
  - Only exchanged with 0.4.0 peers.
- `--dev` developer console (see above).

## Changes in 0.3.0

- **Syncing while paused.** Pausing sets the game's simulation speed to 0, which stops `setTimeout`, so 0.2.0 stopped
  syncing until you unpaused. The mod now also registers an update function (interval 0, called every frame) that takes
  over when the timer has been silent for 60 frames. Timeouts that wait for the game to finish a placement or repaint only
  count unpaused time.
- **Tile map search survives game updates.** The helper no longer needs memory regions of one exact size. The mod
  reports the map size (`tileMapSize`), and the helper looks for arrays of that size in any big-enough region, at any
  16-byte offset in the first 4 KB. A failed search is retried every 10 s (6 times), and again if the map is lost.
- **Host leaving ends the session for everyone.**
  - When the host quits or closes the game, it sends `bye`.
  - When the host leaves its city (main menu or another city), it sends `hostleft`.
  - Both sides ping every 3 s. The client gives up on a silent host after 20 s; the host drops a silent client after 30 s.
  - A client in the host's city closes its game: the mod calls `tryCloseGame`, and the helper force-closes the game
    after 8 s if needed. The client then deletes its `MP_*.cpcp` copies of host cities, so they can't be played on.
  - After `hostleft` the client's game restarts and rejoins when the host loads a city; after `bye` the helper exits.
- Sends run on a writer thread per connection, so one slow joiner can't stall everyone else.
- Clients no longer share builds made in a city other than the host's (for example after loading their own save).

## Layout

| File | Role |
|---|---|
| `src/Program.cs`, `Options.cs`, `Ui.cs` | Entry point, CLI args (`host`, `join <ip[:port]>`, `lobby`, `install`, `uninstall`, `--port`, `--name`, `--game`, `--autosave`, `--city`, `--allow-second-instance`, `--control <file>`, `--dev`), console menu; `Ui` writes status lines to a sink (the console today) |
| `src/Settings.cs`, `SteamName.cs` | `multiplayer/settings.json` (name, recent hosts); the Steam profile name as the default name |
| `src/GameInstall.cs` | Finds the game (Steam library or parent folders), copies the Lua files in, and patches 3 game scripts between `-- [cpc-multiplayer] begin/end` markers |
| `src/GameProcess.cs` | Starts `carparkcapital.exe -mpsession <id>`, reads its stdout, force-closes it |
| `src/Session.cs` | Core logic: lobby, host/join over TCP, routing events between game and network, sending the save file to joiners, ending the session when the host leaves |
| `src/Peer.cs` | One TCP connection: newline-delimited JSON, queued sends, liveness |
| `src/Inbox.cs`, `Lua.cs` | Writes `multiplayer/runtime/inbox_<id>.lua`, the helper→game message channel |
| `src/TileSync.cs`, `Native.cs` | Roads/floors/land sync by reading and writing the game's memory (`ReadProcessMemory`/`WriteProcessMemory`) |
| `lua/boot.lua` | In-game bootstrap: loaded from the main menu and from every level; runs a 0.1 s tick, or a frame-driven one while paused |
| `lua/inbox.lua` | Polls the inbox file, dispatches messages (`hello`, `loadworld`, `leave`, `eval`), acks |
| `lua/util.lua` | JSON encoder; `MP.emit` prints `MP>{json}` to stdout |
| `lua/hooks.lua` | Wraps `setComponents`/`setComponent` to see every new placeable entity |
| `lua/sync.lua` | Detects local place/demolish; replays remote ones; runs the repaint queue; reports the map size |
| `lua/settings.lua` | Finds price changes by polling the objects that have one, applies the host's prices (0.4.0) |
| `src/SharedSettings.cs` | The host's price table (whitelist, ranges, what changed) and the queue of prices for our game (0.4.0) |
| `lua/MPLoad.lua` | UI-screen script used to switch level (load the host's city) or close the game |
| `lua/MPPaint0..13.lua` | Invisible one-tile placeables, one per floor material, used to make the game redraw tiles the helper wrote |
| `tests/FakeGame.cs`, `run.ps1` | Stand-in for the game and the scenario runner, for testing the helper's networking without it (see `tests/README.md`) |
| `build.ps1` | Builds the helper and the stand-in game into `.testrun\bin` |

## Network protocol

TCP, port 27777 by default, one JSON object per line, field `t` is the type.

| Type | Direction | Meaning |
|---|---|---|
| `hello` / `welcome` | client → host / host → client | Join, with name and mod version |
| `world?` / `world` | client → host / host → client | Ask for / send the city (base64 save, plus newer builds, tiles and land) |
| `ev` | both | Object placed, demolished or removed (`ev.type`, `ev.key`) |
| `tiles`, `land` | both | Changed roads/floors (6 bytes each) and land ownership (5 bytes each), base64 |
| `chat`, `players?` | both | Chat and player list |
| `ping` | both | Keep-alive, every 3 s (0.3.0) |
| `hostleft` | host → client | The host left its city (0.3.0) |
| `bye` | host → client | The host is shutting down (0.3.0) |
| `setting` | client → host | The client's player changed a price: `key`, `fields` (`{"ParkingEntry.ratePerMinute": 0.37}`) and `seq`, which counts up (0.4.0) |
| `settings` | host → client | Prices of changed objects (`entries`, every 2 s) or of all objects (`full`, every 30 s and after `sync?`). `ack` is the newest `seq` from this client the host had applied; the client skips objects it changed after that (0.4.0) |
| `sync?` | client → host | Sent after loading the host's city: asks for every current price (0.4.0) |

Between the helper and the game (inbox → game, `MP>` lines → helper), 0.4.0 adds `setting` (game → helper, a local
change, with its own `seq`), inbox `settings` (apply these; `seen` is the newest `seq` the helper had, so the game skips
values for a field it changed later), inbox `settings?` (reply with all values) and its `settings` reply.
