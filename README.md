# CPC-Multiplayer

Co-op building mod for Car Park Capital: a console helper (`CPC-Multiplayer.exe`) that hosts or joins a game over TCP,
plus Lua scripts it installs into the game.

The 0.2.0 source was recovered from the original `CPC-Multiplayer.exe` (a .NET Framework 4.0 console app, not obfuscated)
by decompiling it with ILSpy; that is the first commit. The Lua files in `lua/` were embedded resources in the exe.

## Build

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

This builds `CPC-Multiplayer.exe`, the stand-in game and the test tools into `.testrun\bin` (git-ignored). With the .NET
SDK installed it runs `dotnet build -c Release`; without it, it downloads Roslyn (`Microsoft.Net.Compilers.Toolset`) once
into `.testrun\tools` and compiles against the .NET Framework 4 assemblies in the Windows folder, using any installed .NET
runtime. For the Lua tests it also downloads Lua 5.4 (`lua54.dll` from the KeraLua package) once.

The output exe goes next to `carparkcapital.exe` (or pass `--game "<folder>"`).
.NET Framework apps can't start from a path longer than 260 characters ("This application could not be started").

Run every test with `tests\run.ps1` (see `tests/README.md`).

## Lobby mode

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

## Changes in 0.4.0

- **Lobby mode** (above).
- **The host's city decides.** A client's build goes to the host and on to everyone else straight away, and the host's
  game replays it. If the host's game can't place it (the host, or another friend, built there first), the host sends
  `undo` and every game takes it back; the builder sees "Your X overlapped something in the host's city". When it does
  fit, the host confirms it (`ok`). A replayed build that's blocked keeps retrying only while something may still free
  its tile: a removal in progress or, on a client, its own new build that the host is about to take back. On the host a
  friend's build gets 2 tries (about 3 s) before it's taken back.
  Roads, floors and land work the same way: a client's changes carry a sequence number the host confirms (`tilesok`,
  `landok`), and until then the client skips the host's older values for those tiles, so both end up with the host's.
- **Drift repair.** Every 5 s the host sends a digest of its city: for objects, a count and hash per bucket of keys
  (64 buckets); for roads, floors and land, a hash per map area (8 x 8 areas). A client compares it with its own while
  nothing is in flight. A bucket or area that differs on two checks in a row is repaired from the host: for objects the
  host sends the bucket's keys and the client's mod adds what's missing and takes out what the host doesn't have
  (never the player's own builds from the last 10 s); for the map the host sends the area and the client writes in the
  differences. A part that still differs after 3 repairs is left alone for 5 minutes, with a hint to use `refresh`.
  Only shared objects count: the loaded city and players' builds. Objects the game makes by itself (spills and the
  like) are each PC's own, and their removal is no longer sent to others. When the host's game has such an object, a
  repair leaves the joiner's copy (loaded with the save) alone. Objects whose key the helper can't carry (a template
  name with characters other than letters, digits and `_`) stay local and don't count either.
- **Joining needs no save.** The host sends its newest save of the open city right away, plus every build, tile and
  land change since it was written (the host keeps them from the moment the city opened, also before `host` in the
  lobby). Only a city that was never saved since it opened waits for a save or autosave. Roads, floors and land changed
  in the lobby before `host` aren't in the log (tile sync only runs while hosting); drift repair brings joiners up to
  date on those a few seconds after they join.
- **Faster sync.** Roads and floors are polled 4 times a second (was once) and friends' changes are written in as soon
  as they arrive; a demolition is spotted within a second in cities of up to 20,000 objects (was 8,000); a vanished object
  is reported after 1.5 s (was 3 s).
- **Same version or no join.** The host refuses a helper with another mod version (`refused`), and a client leaves a
  host with another version, since the protocol changed.
- The mod lists the game's Lua functions with "save" in their name when the helper says hello (in the helper's log),
  as a first step towards a "save now" when a friend joins.
- The helper's code is split by concern (see Layout), with the decompiler's names replaced. The Lua sync logic has its
  own tests against a stand-in engine, and the helper's sync logic has unit tests.

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
| `src/Program.cs`, `Options.cs`, `Ui.cs` | Entry point, CLI args (`host`, `join <ip[:port]>`, `lobby`, `install`, `uninstall`, `--port`, `--name`, `--game`, `--autosave`, `--city`, `--allow-second-instance`, `--control <file>`), console menu; `Ui` writes status lines to a sink (the console today) |
| `src/Settings.cs`, `SteamName.cs`, `Util.cs` | `multiplayer/settings.json` (name, recent hosts); the Steam profile name as the default name; JSON and small helpers |
| `src/Session/Session.cs` | The helper while the game runs: main loop, starting and relaunching the game, the lobby, shutting down. Its header explains how the parts fit |
| `src/Session/Session.Game.cs` | The mod in our game: its `MP>` lines, and messages we send it |
| `src/Session/Session.Host.cs` | Hosting: connections, sending the city (save + change log), deciding friends' builds |
| `src/Session/Session.Client.cs` | Joining: the connection to the host, loading the host's city, builds waiting for the host |
| `src/Session/Session.Tiles.cs` | Roads, floors and land: polling, sending, writing in others' changes |
| `src/Session/Session.Drift.cs` | Digests, comparing with the host, repairing differences |
| `src/Session/Session.Commands.cs` | Console and control-file commands |
| `src/Protocol/Msg.cs`, `BuildEvent.cs` | Building and reading messages; checking build events (`place`/`demolish`/`remove`) |
| `src/Sync/ChangeLog.cs` | Host: every change since the city opened, for joiners |
| `src/Sync/PendingEdits.cs` | Client: tile and land changes the host hasn't confirmed |
| `src/Sync/DriftTracker.cs` | Client: which buckets or areas to repair |
| `src/Sync/TileSync.cs`, `Native.cs`, `TileChange.cs`, `PaintAnchor.cs` | Roads/floors/land in the game's memory (`ReadProcessMemory`/`WriteProcessMemory`), and their area hashes |
| `src/Net/Peer.cs` | One TCP connection: newline-delimited JSON, queued sends, liveness |
| `src/Game/GameInstall.cs` | Finds the game (Steam library or parent folders), copies the Lua files in, and patches 3 game scripts between `-- [cpc-multiplayer] begin/end` markers |
| `src/Game/GameProcess.cs` | Starts `carparkcapital.exe -mpsession <id>`, reads its stdout, force-closes it |
| `src/Game/Inbox.cs`, `Lua.cs` | Writes `multiplayer/runtime/inbox_<id>.lua`, the helper→game message channel |
| `lua/boot.lua` | In-game bootstrap: loaded from the main menu and from every level; runs a 0.1 s tick, or a frame-driven one while paused |
| `lua/inbox.lua` | Polls the inbox file, dispatches messages (`hello`, `loadworld`, `leave`, `eval`), acks |
| `lua/util.lua` | JSON encoder; `MP.emit` prints `MP>{json}` to stdout |
| `lua/hooks.lua` | Wraps `setComponents`/`setComponent` to see every new placeable entity |
| `lua/sync.lua` | Detects local place/demolish; replays remote ones; keeps the digest of shared objects and repairs buckets; runs the repaint queue; reports the map size |
| `lua/MPLoad.lua` | UI-screen script used to switch level (load the host's city) or close the game |
| `lua/MPPaint0..13.lua` | Invisible one-tile placeables, one per floor material, used to make the game redraw tiles the helper wrote |
| `tests/` | Stand-in game, scenario runner, Lua tests with a stand-in engine, helper unit tests (see `tests/README.md`) |
| `build.ps1` | Builds the helper and the test tools into `.testrun\bin` |

## Network protocol

TCP, port 27777 by default, one JSON object per line, field `t` is the type.

| Type | Direction | Meaning |
|---|---|---|
| `hello` / `welcome` | client → host / host → client | Join, with name and mod version |
| `refused` | host → client | Not let in (`reason`), e.g. a different mod version (0.4.0) |
| `world?` / `world` | client → host / host → client | Ask for / send the city (base64 save, plus newer builds, tiles and land) |
| `ev` | both | Object placed, demolished or removed (`ev.type`, `ev.key`). From a client it's a proposal |
| `ok` / `undo` | host → client | The host's game placed a client's build / couldn't, so everyone takes it back (`key`) (0.4.0) |
| `tiles`, `land` | both | Changed roads/floors (6 bytes each) and land ownership (5 bytes each), base64. From a client with `seq` |
| `tilesok`, `landok` | host → client | The host applied the client's changes up to `seq` (0.4.0) |
| `digest` | host → client | Every 5 s: `objects` (64 × "count:hash"), `tiles` and `land` (64 area hashes), `busy` (0.4.0) |
| `keys?` / `keys` | client → host / host → client | The host's object keys in one `bucket`, for a repair (0.4.0) |
| `area?` / `area` | client → host / host → client | The host's tiles and land in one map `area`, deflated and base64, for a repair (0.4.0) |
| `chat`, `players?` | both | Chat and player list |
| `ping` | both | Keep-alive, every 3 s (0.3.0) |
| `hostleft` | host → client | The host left its city (0.3.0) |
| `bye` | host → client | The host is shutting down (0.3.0) |

Between the helper and the mod: the mod prints `MP>{"t":...,"d":{...}}` lines (`room`, `ready`, `hb`, `ack`, `place`,
`demolish`, `remove`, `placed`, `blocked`, `digest`, `keys`, `reconciled`, `painted`, `error`, `eval`, `hello`), and the
helper writes messages to the inbox file (`hello`, `loadworld`, `leave`, `place`, `demolish`, `remove`, `undo`, `dump`,
`reconcile`, `paint`, `eval`).
