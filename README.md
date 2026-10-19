# CPC-Multiplayer 0.2.0 (recovered source)

Source recovered from `CPC-Multiplayer.exe` (a .NET Framework 4.0 console app, not obfuscated) by decompiling it with ILSpy.
The Lua files in `lua/` were embedded resources in the exe and are identical to what it installs into the game folder.
Rebuilding this source gives the same program: decompiling the rebuild matches the original apart from compiler-level differences.

## Build

With the .NET SDK installed:

```
dotnet build -c Release
```

The output exe goes next to `carparkcapital.exe` (or pass `--game "<folder>"`).

## Layout

| File | Role |
|---|---|
| `src/Program.cs`, `Options.cs`, `Ui.cs` | Entry point, CLI args (`host`, `join <ip[:port]>`, `install`, `uninstall`, `--port`, `--name`, `--game`, `--autosave`, `--city`, `--allow-second-instance`), console menu |
| `src/GameInstall.cs` | Finds the game (Steam library or parent folders), copies the Lua files in, and patches 3 game scripts between `-- [cpc-multiplayer] begin/end` markers |
| `src/GameProcess.cs` | Starts `carparkcapital.exe -mpsession <id>` and reads its stdout |
| `src/Session.cs` | Core logic: host/join over TCP, routing events between game and network, sending the save file to joiners |
| `src/Peer.cs` | One TCP connection: newline-delimited JSON |
| `src/Inbox.cs`, `Lua.cs` | Writes `multiplayer/runtime/inbox_<id>.lua`, the helper→game message channel |
| `src/TileSync.cs`, `Native.cs` | Roads/floors/land sync by reading and writing the game's memory (`ReadProcessMemory`/`WriteProcessMemory`) |
| `lua/boot.lua` | In-game bootstrap: loaded from the main menu and from every level; runs a 0.1 s tick |
| `lua/inbox.lua` | Polls the inbox file, dispatches messages, acks |
| `lua/util.lua` | JSON encoder; `MP.emit` prints `MP>{json}` to stdout |
| `lua/hooks.lua` | Wraps `setComponents`/`setComponent` to see every new placeable entity |
| `lua/sync.lua` | Detects local place/demolish; replays remote ones; runs the repaint queue |
| `lua/MPLoad.lua` | UI-screen script used to switch level (load the host's city) |
| `lua/MPPaint0..13.lua` | Invisible one-tile placeables, one per floor material, used to make the game redraw tiles the helper wrote |
