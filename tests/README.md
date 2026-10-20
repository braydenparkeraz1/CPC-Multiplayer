# Testing the helper without the game

`FakeGame.cs` builds a stand-in `carparkcapital.exe` that does what the mod's Lua side does: it reads the inbox
file, acks messages, prints `MP>` lines, "loads" a city on `loadworld` and exits on `leave`.
It can't test anything inside the real game (object/tile sync, pausing), only the helper and the network protocol.

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
`level` (load a city and autosave it), `menu`, `save`, `exit`.
Drive a helper the same way through the file given with `--control`: one command per line, as you'd type them
(`host`, `join <addr>`, `leave`, `say <text>`, `status`, `quit`, ...).
Set `FAKEGAME_IGNORE_LEAVE=1` for the client to check the helper force-closes a game that doesn't close.
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

Checked in the real game (two copies on one PC): a price set on either side shows on the other within 2 s, without
flicking back while the slider is dragged; a new entrance a friend builds and prices arrives with that price; a price
changed while paused goes out after unpausing. The price slider set both entrances of the test city at once, and both
were synced. `Pricing.price` (shops, restrooms) is not tried yet.
