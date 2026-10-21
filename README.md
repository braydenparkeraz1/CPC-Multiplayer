# CPC-Multiplayer

Co-op building mod for Car Park Capital: a console helper (`CPC-Multiplayer.exe`) that hosts or joins a game over TCP,
plus Lua scripts it installs into the game.

The 0.2.0 source was recovered from the original `CPC-Multiplayer.exe` (a .NET Framework 4.0 console app, not obfuscated)
by decompiling it with ILSpy; that is the first commit. The Lua files in `lua/` were embedded resources in the exe.

## Quick start

Every player needs the game and `CPC-Multiplayer.exe` (see [Build](#build)). The helper finds the game in your Steam
library and installs the mod scripts by itself.

**Without a terminal:** double-click `CPC-Multiplayer.exe` (or start the game from Steam, see [Main menu buttons](#main-menu-buttons)).
The game starts with **Host** and **Join** on its main menu.

1. **Host:** press Host, then load or start the city you want to share. Your address is copied to the clipboard;
   paste it to your friends.
2. **Friends:** on the same network, press **Join** with the host's name (e.g. Join Hosty); it shows up a few seconds
   after the host presses Host. Otherwise copy the host's address and press Join. The game loads the host's city. Next
   time, Join with an empty clipboard joins the last host again.

**From a terminal:**

1. **Host:** run `CPC-Multiplayer.exe host`. The game starts; load or start the city you want to share. A city that
   has never been saved needs a save (Esc > Save) before a friend can join.
2. **Friends:** run `CPC-Multiplayer.exe join <host's IP>`. The game starts and loads the host's city.
3. Play. Builds, demolitions, roads, floors, land, prices and money are shared. Close the game or type `quit` to stop.

Or start `CPC-Multiplayer.exe` from a terminal without a mode: the game starts right away, and you type `host` or
`join <ip>` in the terminal, or use the buttons (lobby mode). Over the internet the host needs TCP port 27777 forwarded
(or `--port`), or everyone on one VPN. Every player must run the same version.

What is shared:

| Shared | How |
|---|---|
| Objects (buildings, entrances, decorations) | Placed and demolished on every PC |
| Roads, floors, land ownership | Copied through the game's memory |
| Parking and shop prices | The host's value wins; anyone can change them, also with "apply to all" ticked (0.4.0, 0.5.0) |
| Money | One wallet, the host's. Everyone's builds are paid from it (0.4.0) |
| Pause, game speed, day and time | Anyone pauses or changes the speed for everyone; clocks are kept together (0.5.0) |
| Cars, people, protests, and the statistics that come from them (carbrainness, civil unrest, car dependency) | **Not shared yet**: each PC simulates its own (see [Not shared yet](#not-shared-yet)) |

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

## Developer console

`--dev` makes the helper check `multiplayer/runtime/devcmd.txt` in the game folder once a second. The whole file is run
as one Lua chunk in the game (in the level's environment, like the `lua` command) and then deleted. Each result is
appended to `multiplayer/runtime/devout.txt`. It is off by default. `docs/api-findings.md` lists what was learned with it.

It also checks `multiplayer/runtime/devmem.txt` for commands that look at the game's memory from outside (one per
line; results in `devmemout.txt`), which work while the game is paused: `chain` (the city and its speed), `tiles`
(where the tile map and land are), `read <addr> <len>`, `find str|f32|f64|ptr <value>`, `save <name> <addr> <len>`
then `diff <name>`, and `write <addr> f32|f64|i32|i64|u8 <value>` only with `--dev-write`. Addresses are hex.
`docs/probes-0.5.md` lists the checks planned with it.

## Playtest log

Every run of the helper writes its own log to `multiplayer/logs/<date>_<time>_<mode>.log` in the game folder (the
newest 30 are kept; the window shows the path, and `log` prints it again). It has one timestamped line for:

- everything the window showed, and every command typed
- every message to and from other players (`net>`, `net<`), except pings; long strings (the city save, packed tiles)
  and lists longer than 24 items are shortened
- every message written for the game's mod (`inbox`), and every message the mod sent back (`mod`) except
  heartbeats and acks; the mod's trace of what it decided (`lua`): a replay placing, waiting, retrying or given up,
  what a repair added and took out, prices applied or kept, friends' spends applied
- the game starting, changing room and exiting, role changes, connections opening and closing, crashes
- a snapshot every 30 s and on `status` (`snap`): what the helper waits for, every connection's traffic, silence and
  clock difference, tile and money sync state, memory use

`note <text>` marks a moment ("the bench just vanished") in your log and every other player's, with a snapshot, so
the logs of one session can be lined up afterwards.

**Friends' logs go to the host.** A friend's helper sends its whole log to the host over the game connection (so over
the same LAN or VPN), from its first line and then as it grows, about once a second. The host keeps each one in a
folder named after its own log: `multiplayer/logs/<host's run>/<name> (<friend's run>).log`, and `log` prints where.
After a reconnect the friend sends from the start again and the host adds only the lines it doesn't have yet. A friend
who'd rather not share starts the helper with `--no-share-log`. These logs include the friend's PC name and Windows
version (in the first lines).

`helper-<mode>.log` in `multiplayer/runtime` is still written as before (overwritten each run).

## Main menu buttons

In lobby mode (below) the game's main menu shows two lines about multiplayer and buttons, above the game's own menu:

| Button | Shown | Effect |
|---|---|---|
| Host | in the lobby | Like `host`. Copies your address(es) for friends to the clipboard, e.g. `192.168.1.20:27777, 26.10.4.2:27777` (home network first, then VPNs; at most 3) |
| Join | in the lobby | Like `join`, with the addresses found on the clipboard (an invite, or a chat message with an IP in it), tried in turn; with none, the last host that worked |
| Join *name* | in the lobby, for up to 2 games hosted on your network | Joins that host. No copying: hosts announce themselves on the network (below) |
| Stop / Leave | hosting / joined | Like `leave` |

The lines say what is going on (hosting, how many friends joined, the invite, waiting for the host's city) and show the
latest error, e.g. a failed connection.

- **How it works.** The game draws its own menu in its engine, which Lua can't add to. So the buttons are the game's 3D
  letters (`assets/models/3d_text.glb`), placed in the main menu's 3D scene in front of its camera by `lua/menu.lua`. A
  click on one is sent to the helper, which runs it like a typed command and sends back what the menu should show.
  Clicks on the game's own menu never reach Lua.
- **No text box.** The game gives Lua no text input (only the keys it binds itself), hence the clipboard.
- **The letters have only A-Z and 0-9.** `.`, `:` and `-` are drawn with small letter shapes, and other characters
  are left out.
- **No console window.** Started on its own (a double-click, or Steam), the helper hides its console window, since
  the menu has everything. Started from a terminal, it keeps the terminal, and `--console` (or `--dev`) keeps the window
  in any case. With the window hidden, errors that used to wait for Enter show a message box (e.g. the game wasn't found).
  The playtest log has everything the window would have shown.
- **From Steam.** In Steam, right-click the game > Properties > Launch options:
  `"C:\path\to\CPC-Multiplayer.exe" %command%`. Play then starts the helper, which starts the game (the helper ignores the
  game's own command line that Steam adds).
- `--no-clipboard` keeps the buttons off the clipboard (the tests use it); Join then uses the last host only.
- **Games on your network.** While hosting, the helper broadcasts a short UDP message every 2 s on port 27778 (the
  host's name, mod version and TCP port) to every network it is on, including VPNs like ZeroTier or Radmin that pass
  broadcasts, and to this PC. A helper in the lobby listens there and offers each game heard in the last 6 s with the
  same mod version as a **Join *name*** button (button ids `lan:<ip>:<port>`), oldest first, at most 2; the window
  also prints `join <ip>:<port>` for it. It joins the address the message came from, preferring a home network over a
  VPN. The name is cut to 10 characters, and characters the letters lack become spaces (a name with none left shows
  the IP). Hosting stops the messages, and the button disappears a few seconds later.
  - `--lan-port <port>` uses another UDP port (both sides need the same), `--no-lan` turns it off. Several helpers on
    one PC can listen at once (the socket shares the port).
  - When the port is taken, Windows Firewall blocks it (it may ask to allow CPC-Multiplayer the first time; that
    also covers hosting), or the network has no broadcasts (some Wi-Fi with client isolation, most internet VPNs),
    there are just no such buttons; Host, Join and typed commands work as before. Problems are only in the helper's log.
- **The button row fits the screen.** Buttons are one centred row just above the game's own menu panel (which has a
  fixed pixel size, so in a 720p window it reaches up to just below the row). A row wider than 90% of the screen, as
  with two found games at 16:9, is drawn smaller (0.57 of full size with two 10-character names), keeping its
  baseline, so it never reaches down into the panel. Host, Join and one found game with a name of up to 8
  characters fit at full size.

## Lobby mode

`CPC-Multiplayer.exe` with no mode (or `CPC-Multiplayer.exe lobby`) starts the game first and lets you pick afterwards
(`CPC-Multiplayer.exe uninstall` removes the mod). Type these in the window, or write them to the file given with
`--control <file>`:

| Command | Effect |
|---|---|
| `host` | Share your city: the one open in the game, or the next one you load |
| `join [ip[:port]]` | Join a host; without an address, the last one that worked |
| `name <name>` | Change your name (lobby only) |
| `leave` | End the session and go back to the lobby. A host keeps its city open; a client's game restarts at the main menu |
| `note <text>` | Mark this moment in the playtest log, yours and every other player's (see Playtest log) |

When the host leaves or closes the game, clients go back to the lobby instead of the helper exiting. Closing the game
in the lobby ends the helper. `host` and `join` on the command line work as before.

The helper remembers your name and the last 5 host addresses that connected in `multiplayer/settings.json` in the game
folder. On first run the name defaults to your Steam profile name (from Steam's `config/loginusers.vdf`), then your
Windows user name.

## Changes in 0.5.0

Fixes from the first playtest. The protocol changed, so every player needs 0.5.0.

- **Host and Join on the main menu, and games on your network.** No terminal needed: the main menu has Host and
  Join buttons, and a Join button for each game hosted on the same network (see [Main menu buttons](#main-menu-buttons)).

- **Pause and game speed are shared.** Whoever pauses, unpauses or changes the speed, every game follows, and the
  others see who did it ("Ethan paused the game"). When two players change it at once, the change that reaches the
  host last wins everywhere.
  - Lua can't see or set the speed, and the mod's Lua doesn't run while paused. So the helper reads and writes it
    in the game's memory (`src/Sync/SpeedSync.cs`), every 50 ms, paused or not.
  - Where it lives was found by disassembling the game, so it only works with the game build it was found in
    (checked by the exe's SHA-256, `src/Sync/NativeLayout.cs`). With another build the helper says "Pause and game
    speed aren't shared" and everything else works as before.
  - If the game undoes the helper's writes, speed sharing goes read-only and says so.
  - **Day and time of day stay together.** The mod's heartbeat carries the game's clock (`getSimulationTime()`).
    A friend whose clock is more than 2 s behind the host's (a joiner starts from the host's last save) runs a bit
    faster than the host (up to 2x) until it has caught up, or a bit slower when ahead. The clock itself can't be set.
  - Pauses the game makes by itself (if the Esc menu pauses) are shared too.
- **Money follows the host within a moment.** The host sends its balance whenever it changes (at most 5 times a
  second; it was every 2 s), and a friend writes it straight away (every 50 ms; was 0.25 s). The 1.5 s hold for a
  friend's own purchase no longer delays the host's income (`src/Sync/MoneyFollow.cs`). A friend's own simulated income
  and upkeep no longer flash on screen.
- **"Apply to all" prices sync.** With "apply to all" ticked for a kind of price, the game keeps one stored price
  for the kind (`money.syncedPrices`) and copies it onto every object all the time, so a friend's price written
  into the objects was undone at once, and the host's mod reported that undo as its player's edit.
  - The mod now tells the game's undo apart from its player's edits (`settingrevert`).
  - The helper then finds the stored price in the game's memory (`src/Sync/PriceMapSync.cs`: the kind's name
    followed by the price, checked by writing the friend's price and seeing every object take it) and changes it.
  - If it can't be found, the host's price stands and the player whose game holds it is told to untick
    "apply to all".
  - Ticking or unticking the box itself is still each player's own.
- **A friend's suburban home no longer fails in the host's city.** The house turns its plot to Dirt while it's
  built, and those floor changes reached the host before the house did. The house needs Grass, so the host's game
  refused it, and the Dirt left behind made every retry fail too.
  - Floors a building paints under itself while it's built are no longer sent or charged
    (`src/Sync/ConstructionFloors.cs`, with sizes and materials from the game's templates in `src/Game/Templates.cs`).
    Every PC's own copy of the build paints them.
  - Builds carry the build tool's "paint the floor" setting (`paint`), and the copies use it.
  - When a build is taken back, the builder is told the game's reason ("the floor there is the wrong kind",
    "something else is already there") instead of always "overlapped something".
- **Developer memory console.** With `--dev`, `multiplayer/runtime/devmem.txt` takes memory commands (`chain`,
  `tiles`, `read`, `find`, `save`/`diff`, and `write` with `--dev-write`), with results in `devmemout.txt`. They
  run in the helper, so they work while the game is paused. See `src/Sync/DevMemory.cs` and `docs/probes-0.5.md`.

### Not shared yet

Each PC still runs its own simulation of cars, people and protests. A joiner starts with the host's (they're in the
save), but they drift apart from then on. Carbrainness (the citizens' average), price tolerance, car dependency and
civil unrest all come from that simulation, so they drift apart too.
- On a friend's PC this changes what citizens will pay at a booth, but not the money: the wallet is the host's.
- Land bought by a friend shows as owned when hovered and can be built on, but stays dark on the map, and eviction
  there says it isn't owned. The map overlay and the eviction check use state the game only updates through its own
  purchase code.

Both are next. `docs/probes-0.5.md` lists the in-game checks they need first.

## Changes in 0.4.0

- **Lobby mode** (above).
- **Host and Join buttons on the game's main menu**, and no console window when the helper is started on its own (see
  Main menu buttons).
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
- **Money is shared.** The host's balance is the city's wallet, and every player sees it.
  - Lua can change the money but not read it, so the helper finds the balance in the game's memory (`src/Sync/MoneySync.cs`):
    it scans for values near the balance in the city's save, the mod adds and takes back a $500 marker, and of the
    values that moved with it, the balance is the one whose HUD copy follows a small nudge. This takes about 20 s after a
    city loads, and shows as "Shared money: on".
  - The host's helper sends its balance every 2 s (at least every 10 s). A friend's helper writes it into its own game,
    so a friend's own simulation earns and pays nothing.
  - A friend's build is charged by their game and sent to the host as a numbered `spend`, which the host's game applies
    once (`transactMoney`). Prices come from the game's template scripts (`src/Game/Prices.cs`); demolitions give half back
    unless the template says otherwise. Roads and floors cost their material's price per tile. Land plots, and builds
    missing from the table, are charged what the friend's game spent (not tried in game yet: before this, a friend's
    land purchase was free).
  - Until the host has applied a friend's spend, the friend sees it already taken off, so the money doesn't bounce.
  - A friend's build the host takes back (`undo`) is refunded in full.
  - Money can go negative; nothing gets removed.
  - Known gaps: removing or reshaping tiles is free; the refund rules for the 14 drive-thru templates (full refund) and
    for old objects aren't measured; selling land isn't refunded.
- `--dev` developer console (see Developer console).

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
| `src/Program.cs`, `Options.cs`, `Ui.cs` | Entry point, CLI args (`host`, `join <ip[:port]>`, `lobby`, `install`, `uninstall`, `--port`, `--name`, `--game`, `--autosave`, `--city`, `--allow-second-instance`, `--control <file>`, `--dev`, `--dev-write`, `--no-share-log`, `--console`, `--no-clipboard`, `--lan-port <port>`, `--no-lan`, and a path to `carparkcapital.exe` from Steam's `%command%`), console menu; `Ui` writes status lines to a sink (the console today) |
| `src/Settings.cs`, `SteamName.cs`, `Util.cs` | `multiplayer/settings.json` (name, recent hosts); the Steam profile name as the default name; JSON and small helpers |
| `src/Session/Session.cs` | The helper while the game runs: main loop, starting and relaunching the game, the lobby, shutting down. Its header explains how the parts fit |
| `src/Session/Session.Game.cs` | The mod in our game: its `MP>` lines, and messages we send it |
| `src/Session/Session.Host.cs` | Hosting: connections, sending the city (save + change log), deciding friends' builds |
| `src/Session/Session.Client.cs` | Joining: the connection to the host, loading the host's city, builds waiting for the host |
| `src/Session/Session.Tiles.cs` | Roads, floors and land: polling, sending, writing in others' changes |
| `src/Session/Session.Drift.cs` | Digests, comparing with the host, repairing differences |
| `src/Session/Session.Prices.cs` | Prices: our game's edits, the host's table and its broadcasts |
| `src/Session/Session.Money.cs` | Money: finding, reading and writing the wallet, friends' spends |
| `src/Session/Session.Speed.cs` | Pause and game speed, and keeping the clocks together |
| `src/Session/Session.Commands.cs` | Console and control-file commands, the `--dev` console |
| `src/Session/Session.Menu.cs`, `src/Invite.cs`, `src/Clipboard.cs`, `src/ConsoleWindow.cs` | The main menu's buttons: what they show and do; the host's invite and the addresses in copied text; the clipboard; hiding the console window |
| `src/Session/Session.Lan.cs`, `src/Net/Lan.cs` | Games on the same network: announcing ours while hosting, the games heard lately for the menu's buttons; the message format and the UDP sockets |
| `src/Session/Session.Log.cs`, `src/PlayLog.cs` | The playtest log: its file and line format, snapshots, players' notes |
| `src/Protocol/Msg.cs`, `BuildEvent.cs`, `BlockReason.cs` | Building and reading messages; checking build events (`place`/`demolish`/`remove`); why the game refused a build, in words |
| `src/Sync/ChangeLog.cs` | Host: every change since the city opened, for joiners |
| `src/Sync/PendingEdits.cs` | Client: tile and land changes the host hasn't confirmed |
| `src/Sync/DriftTracker.cs` | Client: which buckets or areas to repair |
| `src/Sync/SharedSettings.cs` | The host's price table (whitelist, ranges, what changed) and the queue of prices for our game |
| `src/Sync/MoneySync.cs`, `MoneyFollow.cs` | Finds, reads and writes the city's money in the game's memory; when a friend writes the host's balance |
| `src/Sync/NativeLayout.cs`, `SpeedSync.cs`, `ClockNudge.cs` | The game's speed behind a known pointer chain (per game build); how much faster a friend runs to catch up |
| `src/Sync/PriceMapSync.cs` | Finds and changes the game's stored "apply to all" price of a kind |
| `src/Sync/ConstructionFloors.cs`, `src/Game/Templates.cs` | Floor changes a building makes while it's built, which aren't sent; template sizes, construction floors and price kinds from the game's scripts |
| `src/Sync/DevMemory.cs` | `--dev` memory console |
| `src/Sync/TileSync.cs`, `Native.cs`, `TileChange.cs`, `PaintAnchor.cs` | Roads/floors/land in the game's memory (`ReadProcessMemory`/`WriteProcessMemory`), and their area hashes |
| `src/Net/Peer.cs` | One TCP connection: newline-delimited JSON, queued sends, liveness |
| `src/Game/GameInstall.cs` | Finds the game (Steam library or parent folders), copies the Lua files in, and patches 3 game scripts between `-- [cpc-multiplayer] begin/end` markers |
| `src/Game/GameProcess.cs` | Starts `carparkcapital.exe -mpsession <id>`, reads its stdout, force-closes it |
| `src/Game/Prices.cs`, `SaveFile.cs` | Build and tile prices from the game's scripts; the balance in a save (zlib + CBOR) |
| `src/Game/Inbox.cs`, `Lua.cs` | Writes `multiplayer/runtime/inbox_<id>.lua`, the helper→game message channel |
| `lua/boot.lua` | In-game bootstrap: loaded from the main menu and from every level; runs a 0.1 s tick, or a frame-driven one while paused |
| `lua/inbox.lua` | Polls the inbox file, dispatches messages (`hello`, `loadworld`, `leave`, `eval`), acks |
| `lua/util.lua` | JSON encoder; `MP.emit` prints `MP>{json}` to stdout |
| `lua/hooks.lua` | Wraps `setComponents`/`setComponent` to see every new placeable entity |
| `lua/sync.lua` | Detects local place/demolish; replays remote ones; keeps the digest of shared objects and repairs buckets; runs the repaint queue; reports the map size |
| `lua/settings.lua` | Finds price changes by polling the objects that have one, applies the host's prices, tells the game's "apply to all" undoing a price from the player's edits |
| `lua/money.lua` | Marker transactions for finding the money; applies friends' spends on the host |
| `lua/menu.lua` | The main menu's Host/Join buttons and status lines, as 3D letters in the menu's scene; a row too wide for the screen is drawn smaller |
| `lua/MPLoad.lua` | UI-screen script used to switch level (load the host's city) or close the game |
| `lua/MPPaint0..13.lua` | Invisible one-tile placeables, one per floor material, used to make the game redraw tiles the helper wrote |
| `tests/` | Stand-in game, scenario runner, Lua tests with a stand-in engine, helper unit tests (see `tests/README.md`) |
| `build.ps1` | Builds the helper and the test tools into `.testrun\bin` |

## Network protocol

TCP, port 27777 by default, one JSON object per line, field `t` is the type.

Hosts on the same network also announce themselves over UDP (port 27778, every 2 s, to each network's broadcast
address, 255.255.255.255 and 127.255.255.255): one datagram of at most 512 bytes, `CPCMP-LAN 1 ` followed by
`{"id":"<random per run>","name":"<host's name, at most 32 chars>","version":"<mod version>","port":<TCP port>}`.
Anything else on the port is ignored.

| Type | Direction | Meaning |
|---|---|---|
| `hello` / `welcome` | client → host / host → client | Join, with name and mod version |
| `refused` | host → client | Not let in (`reason`), e.g. a different mod version (0.4.0) |
| `world?` / `world` | client → host / host → client | Ask for / send the city (base64 save, plus newer builds, tiles and land) |
| `ev` | both | Object placed, demolished or removed (`ev.type`, `ev.key`). From a client it's a proposal |
| `ok` / `undo` | host → client | The host's game placed a client's build / couldn't, so everyone takes it back (`key`, and `reason` in words when the game gave one) (0.4.0, 0.5.0) |
| `tiles`, `land` | both | Changed roads/floors (6 bytes each) and land ownership (5 bytes each), base64. From a client with `seq` |
| `tilesok`, `landok` | host → client | The host applied the client's changes up to `seq` (0.4.0) |
| `digest` | host → client | Every 5 s: `objects` (64 × "count:hash"), `tiles` and `land` (64 area hashes), `busy` (0.4.0) |
| `keys?` / `keys` | client → host / host → client | The host's object keys in one `bucket`, for a repair (0.4.0) |
| `area?` / `area` | client → host / host → client | The host's tiles and land in one map `area`, deflated and base64, for a repair (0.4.0) |
| `chat`, `players?` | both | Chat and player list |
| `ping` | both | Keep-alive, every 3 s (0.3.0). `at`: the sender's clock (ms since 1970, UTC), for the playtest log |
| `note` | both | A player's `note` (`name`, `text`), only written to the playtest log. The host passes it on |
| `log` | client → host | Lines of the client's playtest log: `run` (its file name), `from` (number of the first line, from 0), `lines`. At most 128 KB a second; not logged themselves |
| `hostleft` | host → client | The host left its city (0.3.0) |
| `bye` | host → client | The host is shutting down (0.3.0) |
| `setting` | client → host | The client's player changed a price: `key`, `fields` (`{"ParkingEntry.ratePerMinute": 0.37}`) and `seq`, which counts up (0.4.0) |
| `settings` | host → client | Prices of changed objects (`entries`, every 2 s) or of all objects (`full`, every 30 s and after `sync?`). `ack` is the newest `seq` from this client the host had applied; the client skips objects it changed after that (0.4.0) |
| `sync?` | client → host | Sent after loading the host's city: asks for every current price and the money (0.4.0) |
| `spend` | client → host | The client's build or demolition: `id` (counts up), `amount` (negative for a refund), `reason`, `key`. Applied once per `id` (0.4.0) |
| `money` | host → client | The host's balance (`value`) and the newest spend `id` from this client its game has applied (`ack`). When it changes (at most every 0.2 s), at least every 5 s, and after `sync?` (0.4.0, 0.5.0) |
| `speed` | both | Pause or game speed (`value`, 0 = paused). From a client with `seq`; from the host with `ack` (the newest `seq` from this client it applied) and `by` (who changed it, or null for a repeat). The host repeats it every 5 s and after `sync?` (0.5.0) |
| `clock` | host → client | The host's game clock in simulation seconds (`sim`), every 2 s while it runs (0.5.0) |

Between the helper and the mod: the mod prints `MP>{"t":...,"d":{...}}` lines (`room`, `ready`, `hb`, `ack`, `place`,
`demolish`, `remove`, `placed`, `blocked`, `digest`, `keys`, `reconciled`, `painted`, `error`, `eval`, `hello`, `menu`, and `trace`, which only goes in the playtest log), and the
helper writes messages to the inbox file (`hello`, `loadworld`, `leave`, `place`, `demolish`, `remove`, `undo`, `dump`,
`reconcile`, `paint`, `eval`, `menu`).
The main menu: inbox `menu` (`lines`: up to 2 strings, `buttons`: `{id, label}`; each replaces the last) is what it
shows, and the mod's `menu` (`click`: a button's `id`) is a click.
Prices add `setting` (game → helper, a local
change, with its own `seq`), inbox `settings` (apply these; `seen` is the newest `seq` the helper had, so the game skips
values for a field it changed later), inbox `settings?` (reply with all values) and its `settings` reply. For money:
inbox `moneymark` (`mark`, `amount`: add it, take it back ~0.5 s later, reporting each step as `moneymark`) and inbox
`spend` (host only: `spent` and `refunded` from friends).
In 0.5.0: `hb` carries `sim` (the game's clock); `place` may carry `paint` (the build tool's floor painting), in both
directions; `blocked` carries `reason` (the game's `Placing.blockedReasonStr`); the mod reports `settingrevert` (`key`,
`field`, `value` the game put back, `wanted`, `kind` of price) and answers inbox `typevalue?` (`req`, `kind`, `field`) with
`typevalue` (`count`, `min`, `max`); inbox `pricingids` (`names`) tells it the price kinds in the game's scripts.

