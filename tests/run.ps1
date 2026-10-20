# Runs the helper against the stand-in game (tests/FakeGame.cs) through the scenarios in tests/README.md.
# Build first with build.ps1. Every helper and fake game here runs under .testrun\t and is closed afterwards.
#
#   powershell -ExecutionPolicy Bypass -File tests\run.ps1 [-Only <scenario>,...]
param(
    [string[]]$Only
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$bin = Join-Path $root ".testrun\bin"
$work = Join-Path $root ".testrun\t"
$helperExe = Join-Path $bin "CPC-Multiplayer.exe"
$fakeExe = Join-Path $bin "fake\carparkcapital.exe"
if (-not (Test-Path $helperExe) -or -not (Test-Path $fakeExe)) { throw "Build first: build.ps1" }

$script:procs = @()
$script:port = 27800

# Retries $action for a few seconds: a just-killed process can still hold its files, and a control file
# can be mid-delete when we write it.
function Retry([scriptblock]$action) {
    for ($i = 0; ; $i++) {
        try { & $action; return } catch { if ($i -ge 20) { throw } Start-Sleep -Milliseconds 250 }
    }
}

function New-GameDir([string]$name) {
    $dir = Join-Path $work $name
    if (Test-Path $dir) { Retry { Remove-Item -Recurse -Force $dir } }
    New-Item -ItemType Directory -Force (Join-Path $dir "assets\scripts\entities\3d") | Out-Null
    New-Item -ItemType Directory -Force (Join-Path $dir "assets\scripts\entities\simulation") | Out-Null
    New-Item -ItemType Directory -Force (Join-Path $dir "assets\scripts\ui_screens") | Out-Null
    New-Item -ItemType Directory -Force (Join-Path $dir "saves") | Out-Null
    # The installer patches these, so they need its anchor lines.
    Set-Content (Join-Path $dir "assets\scripts\entities\3d\MainMenu.lua") "function create(menu)`r`n    setName(menu, `"Menu`")`r`nend"
    Set-Content (Join-Path $dir "assets\scripts\entities\simulation\GlobalStorage.lua") "function create(entity)`r`n    setName(entity, `"Global Storage Entity`")`r`nend"
    Set-Content (Join-Path $dir "assets\scripts\entities\simulation\_catalogue.lua") "-- catalogue"
    Copy-Item $fakeExe $dir
    Set-Content (Join-Path $dir "stdin.txt") ""
    return $dir
}

function Start-Helper([string]$dir, [string[]]$helperArgs, [hashtable]$envVars = @{}) {
    $old = @{}
    foreach ($k in $envVars.Keys) { $old[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $envVars[$k]) }
    try {
        $all = $helperArgs + @("--game", $dir, "--allow-second-instance", "--control", (Join-Path $dir "helper-control.txt"))
        # stdin is an empty file, so the helper never waits on a prompt or "Press Enter".
        $p = Start-Process -FilePath $helperExe -ArgumentList $all -WorkingDirectory $dir -NoNewWindow -PassThru `
            -RedirectStandardInput (Join-Path $dir "stdin.txt") `
            -RedirectStandardOutput (Join-Path $dir "helper.out") -RedirectStandardError (Join-Path $dir "helper.err")
    } finally {
        foreach ($k in $envVars.Keys) { [Environment]::SetEnvironmentVariable($k, $old[$k]) }
    }
    $script:procs += $p
    return $p
}

function Read-Shared([string]$path) {
    if (-not (Test-Path $path)) { return "" }
    $fs = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    try { return (New-Object IO.StreamReader($fs)).ReadToEnd() } finally { $fs.Dispose() }
}

function HelperLog([string]$dir) {
    $logs = Get-ChildItem (Join-Path $dir "multiplayer\runtime") -Filter "helper-*.log" -ErrorAction SilentlyContinue
    if (-not $logs) { return "" }
    return Read-Shared $logs[0].FullName
}

function GameLog([string]$dir) { return Read-Shared (Join-Path $dir "fakegame.log") }

# Waits until $pattern (a regex) occurs at least $count times in the text $source returns.
function Expect([string]$what, [scriptblock]$source, [string]$pattern, [int]$count = 1, [int]$timeout = 20) {
    $deadline = (Get-Date).AddSeconds($timeout)
    while ((Get-Date) -lt $deadline) {
        if ([regex]::Matches((& $source), $pattern).Count -ge $count) { Write-Host "    ok: $what"; return }
        Start-Sleep -Milliseconds 200
    }
    throw "timed out waiting for: $what (/$pattern/ x$count)"
}

function ExpectExit([string]$what, $proc, [int]$timeout = 20) {
    if (-not $proc.WaitForExit($timeout * 1000)) { throw "timed out waiting for: $what" }
    Write-Host "    ok: $what"
}

function Game([string]$dir, [string]$cmd) {
    $file = Join-Path $dir "control.txt"
    Retry { Set-Content $file $cmd }
    $deadline = (Get-Date).AddSeconds(5)
    while ([IO.File]::Exists($file) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 50 }
    if ([IO.File]::Exists($file)) { throw "the fake game in $dir didn't take '$cmd'" }
}

function Helper([string]$dir, [string]$cmd) {
    $file = Join-Path $dir "helper-control.txt"
    Retry { Set-Content $file $cmd }
    $deadline = (Get-Date).AddSeconds(5)
    while ([IO.File]::Exists($file) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 50 }
    if ([IO.File]::Exists($file)) { throw "the helper in $dir didn't take '$cmd'" }
}

function Stop-All {
    foreach ($p in $script:procs) { if (-not $p.HasExited) { try { $p.Kill() } catch { } } }
    $script:procs = @()
    Get-Process carparkcapital -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($work, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { try { $_.Kill() } catch { } }
    Start-Sleep -Milliseconds 300
}

function Next-Port { $script:port++; return $script:port }

# ---- 0.3.0 scenarios: host/join picked on the command line ----

function Scenario-HostJoin {
    $h = New-GameDir "host"; $c = New-GameDir "client"; $port = Next-Port
    Start-Helper $h @("host", "--name", "Hosty", "--port", $port) | Out-Null
    Expect "host listens" { HelperLog $h } "Hosting on port $port"
    Expect "host game gets hello role=host" { GameLog $h } "hello role=host"
    Game $h "level"
    Expect "host city loaded" { HelperLog $h } "City loaded"
    Start-Helper $c @("join", "127.0.0.1:$port", "--name", "Clienty") | Out-Null
    Expect "client in host's city" { HelperLog $c } "You're in the host's city"
    Expect "client game loaded the copy" { GameLog $c } "loading saves/MP_Hosty\.cpcp \(exists\)"

    # Host goes back to the menu: the client's game closes, restarts, and rejoins when the host loads a city.
    Game $h "menu"
    Expect "client game closes" { GameLog $c } "leave: closing"
    Expect "client game restarts" { HelperLog $c } "Restarting your game\. You rejoin"
    Game $h "level"
    Expect "client rejoins" { HelperLog $c } "You're in the host's city" 2 30
}

function Scenario-HostQuit {
    $h = New-GameDir "host"; $c = New-GameDir "client"; $port = Next-Port
    $hp = Start-Helper $h @("host", "--name", "Hosty", "--port", $port)
    Expect "host listens" { HelperLog $h } "Hosting on port"
    Game $h "level"
    Expect "host city loaded" { HelperLog $h } "City loaded"
    $cp = Start-Helper $c @("join", "127.0.0.1:$port", "--name", "Clienty")
    Expect "client in host's city" { HelperLog $c } "You're in the host's city"
    Helper $h "quit"
    Expect "client game closes" { GameLog $c } "leave: closing"
    ExpectExit "client helper exits" $cp
    ExpectExit "host helper exits" $hp
    if (Get-ChildItem (Join-Path $c "saves") -Filter "MP_*.cpcp") { throw "MP_ copies were not deleted" }
    Write-Host "    ok: client MP_ copies deleted"
}

function Scenario-HostKilled {
    $h = New-GameDir "host"; $c = New-GameDir "client"; $port = Next-Port
    $hp = Start-Helper $h @("host", "--name", "Hosty", "--port", $port)
    Expect "host listens" { HelperLog $h } "Hosting on port"
    Game $h "level"
    Expect "host city loaded" { HelperLog $h } "City loaded"
    Start-Helper $c @("join", "127.0.0.1:$port", "--name", "Clienty") | Out-Null
    Expect "client in host's city" { HelperLog $c } "You're in the host's city"
    $hp.Kill()
    Expect "client notices" { HelperLog $c } "Lost connection to the host"
    Expect "client game closes" { GameLog $c } "leave: closing"
}

function Scenario-IgnoreLeave {
    $h = New-GameDir "host"; $c = New-GameDir "client"; $port = Next-Port
    Start-Helper $h @("host", "--name", "Hosty", "--port", $port) | Out-Null
    Expect "host listens" { HelperLog $h } "Hosting on port"
    Game $h "level"
    Expect "host city loaded" { HelperLog $h } "City loaded"
    Start-Helper $c @("join", "127.0.0.1:$port", "--name", "Clienty") @{ FAKEGAME_IGNORE_LEAVE = "1" } | Out-Null
    Expect "client in host's city" { HelperLog $c } "You're in the host's city"
    Helper $h "quit"
    Expect "client game ignores leave" { GameLog $c } "leave: ignored"
    Expect "helper force-closes it" { HelperLog $c } "didn't close by itself" 1 20
}

# ---- 0.4.0 lobby: the game starts first, host/join picked afterwards ----

function Start-LobbyPair([int]$port) {
    $h = New-GameDir "host"; $c = New-GameDir "client"
    $hp = Start-Helper $h @("lobby", "--name", "Hosty", "--port", $port)
    $cp = Start-Helper $c @("lobby", "--name", "Clienty", "--port", $port)
    Expect "host game in lobby" { GameLog $h } "hello role=lobby"
    Expect "client game in lobby" { GameLog $c } "hello role=lobby"
    Expect "lobby hint" { HelperLog $h } "The game is at the main menu"
    return @{ h = $h; c = $c; hp = $hp; cp = $cp }
}

function Join-Lobby($s, [int]$port, [int]$n = 1) {
    Helper $s.c "join 127.0.0.1:$port"
    Expect "client game gets hello role=client" { GameLog $s.c } "hello role=client" $n
    Expect "client in host's city" { HelperLog $s.c } "You're in the host's city" $n 30
}

function Scenario-LobbyHostJoin {
    $port = Next-Port
    $s = Start-LobbyPair $port
    Helper $s.h "host"
    Expect "host listens" { HelperLog $s.h } "Hosting on port $port"
    Expect "host game gets hello role=host" { GameLog $s.h } "hello role=host"
    Expect "host told to load a city" { HelperLog $s.h } "load the city you want to share"
    Game $s.h "level"
    Expect "host city loaded" { HelperLog $s.h } "City loaded"
    Join-Lobby $s $port
    Expect "address remembered" { Read-Shared (Join-Path $s.c "multiplayer\settings.json") } "127\.0\.0\.1:$port"
    Helper $s.c "say hello from the client"
    Expect "host gets chat" { HelperLog $s.h } "\[Clienty\] hello from the client"
}

function Scenario-LobbyBadJoin {
    $port = Next-Port
    $s = Start-LobbyPair $port
    Helper $s.c "join 127.0.0.1:1"
    Expect "dead address fails" { HelperLog $s.c } "Could not connect"
    Helper $s.c "join nonsense:abc"
    Expect "bad port refused" { HelperLog $s.c } "isn't a valid address"
    Helper $s.c "join"
    Expect "join without address or history" { HelperLog $s.c } "Usage: join"
    Helper $s.c "status"
    Expect "still in the lobby" { HelperLog $s.c } "Not in a session"
    if ($s.cp.HasExited) { throw "client helper exited" }
    # And it can still join afterwards.
    Helper $s.h "host"
    Game $s.h "level"
    Expect "host city loaded" { HelperLog $s.h } "City loaded"
    Join-Lobby $s $port
}

function Scenario-LobbyHostLeaves {
    $port = Next-Port
    $s = Start-LobbyPair $port
    Helper $s.h "host"
    Game $s.h "level"
    Expect "host city loaded" { HelperLog $s.h } "City loaded"
    Join-Lobby $s $port

    # Host stops hosting: its game stays in the city, the client's game restarts in the lobby.
    Helper $s.h "leave"
    Expect "host stopped" { HelperLog $s.h } "You stopped hosting"
    Expect "host game back to lobby role" { GameLog $s.h } "hello role=lobby" 2
    Expect "client game closes" { GameLog $s.c } "leave: closing"
    Expect "client back in the lobby" { HelperLog $s.c } "Back in the lobby"
    Expect "client game restarted in the lobby" { GameLog $s.c } "hello role=lobby" 2
    if (Get-ChildItem (Join-Path $s.c "saves") -Filter "MP_*.cpcp") { throw "MP_ copies were not deleted" }
    Write-Host "    ok: client MP_ copies deleted"

    # Host shares the city it still has open; the client joins again with the remembered address.
    Helper $s.h "host"
    Expect "host shares the open city" { HelperLog $s.h } "Sharing the city you have open"
    Game $s.h "save"
    Helper $s.c "join"
    Expect "client rejoins" { HelperLog $s.c } "You're in the host's city" 2 30
}

function Scenario-LobbyClientLeaves {
    $port = Next-Port
    $s = Start-LobbyPair $port
    Helper $s.h "host"
    Game $s.h "level"
    Expect "host city loaded" { HelperLog $s.h } "City loaded"
    Join-Lobby $s $port
    Helper $s.c "leave"
    Expect "host sees the client go" { HelperLog $s.h } "Clienty left"
    Expect "client game closes" { GameLog $s.c } "leave: closing"
    Expect "client back in the lobby" { HelperLog $s.c } "Back in the lobby"
    Expect "client game restarted in the lobby" { GameLog $s.c } "hello role=lobby" 2
    Game $s.h "save"
    Join-Lobby $s $port 2
}

function Scenario-LobbyHostQuits {
    $port = Next-Port
    $s = Start-LobbyPair $port
    Helper $s.h "host"
    Game $s.h "level"
    Expect "host city loaded" { HelperLog $s.h } "City loaded"
    Join-Lobby $s $port
    Helper $s.h "quit"
    ExpectExit "host helper exits" $s.hp
    Expect "client disconnected" { HelperLog $s.c } "The host closed the game"
    Expect "client back in the lobby" { HelperLog $s.c } "Back in the lobby"
    Expect "client game restarted in the lobby" { GameLog $s.c } "hello role=lobby" 2
    if ($s.cp.HasExited) { throw "client helper exited" }

    # Closing the game in the lobby ends the helper.
    Game $s.c "exit"
    ExpectExit "client helper exits with its game" $s.cp
}

function Scenario-LobbyHostGoneBeforeCity {
    # The client joined but the host never loaded a city; the host leaves. The client's game is
    # still at the main menu, so it goes back to the lobby without restarting.
    $port = Next-Port
    $s = Start-LobbyPair $port
    Helper $s.h "host"
    Expect "host listens" { HelperLog $s.h } "Hosting on port"
    Helper $s.c "join 127.0.0.1:$port"
    Expect "client waits for a city" { HelperLog $s.c } "Waiting for Hosty to load a city"
    Helper $s.h "leave"
    Expect "client back in the lobby" { HelperLog $s.c } "Back in the lobby"
    Expect "client game told it is in the lobby" { GameLog $s.c } "hello role=lobby" 2
    if ((GameLog $s.c) -match "leave: closing") { throw "client game was closed" }
    Write-Host "    ok: client game stayed open"
}

# ---- unit tests ----

function Scenario-HelperUnits {
    & (Join-Path $bin "tests\helpertests.exe") | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "helper unit tests failed" }
}

function Scenario-LuaSync {
    & (Join-Path $bin "luarun\luarun.exe") (Join-Path $root "tests\lua\sync_test.lua") | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "Lua tests failed" }
}

# ---- 0.4.0 sync: the host decides, instant join, drift repair ----

function Start-HostAndClient([hashtable]$hostEnv = @{}) {
    $h = New-GameDir "host"; $c = New-GameDir "client"; $port = Next-Port
    Start-Helper $h @("host", "--name", "Hosty", "--port", $port) $hostEnv | Out-Null
    Expect "host listens" { HelperLog $h } "Hosting on port $port"
    Game $h "level"
    Expect "host city loaded" { HelperLog $h } "City loaded"
    Start-Helper $c @("join", "127.0.0.1:$port", "--name", "Clienty") | Out-Null
    Expect "client in host's city" { HelperLog $c } "You're in the host's city"
    return @{ h = $h; c = $c; port = $port }
}

function Scenario-ClientBuildAccepted {
    $s = Start-HostAndClient
    Game $s.c "build Bench@4,1,4/0/0"
    Expect "host's game places it as a friend's build" { GameLog $s.h } "place Bench@4,1,4/0/0 \(proposal\)"
    Expect "host confirms it to the client" { HelperLog $s.c } "the host placed our Bench@4,1,4/0/0"
    Game $s.h "build Lamp@5,1,5/0/0"
    Expect "host's build reaches the client" { GameLog $s.c } "place Lamp@5,1,5/0/0\r?\n"
}

function Scenario-ClientBuildRejected {
    $s = Start-HostAndClient @{ FAKEGAME_BLOCK = "Bench@4,1,4/0/0" }
    Game $s.c "build Bench@4,1,4/0/0"
    Expect "host takes it back" { HelperLog $s.h } "Clienty's Bench didn't fit in your city"
    Expect "client is told" { HelperLog $s.c } "Your Bench overlapped something in the host's city"
    Expect "client's game takes it out" { GameLog $s.c } "undo Bench@4,1,4/0/0"
}

function Scenario-ThreePlayers {
    $s = Start-HostAndClient @{ FAKEGAME_BLOCK = "Tree@6,1,6/0/0" }
    $c2 = New-GameDir "client2"
    Start-Helper $c2 @("join", "127.0.0.1:$($s.port)", "--name", "Second") | Out-Null
    Expect "second client in host's city" { HelperLog $c2 } "You're in the host's city"
    Game $s.c "build Bench@4,1,4/0/0"
    Expect "a client's build reaches the other client" { GameLog $c2 } "place Bench@4,1,4/0/0\r?\n"
    Game $s.c "build Tree@6,1,6/0/0"
    Expect "the other client gets it too" { GameLog $c2 } "place Tree@6,1,6/0/0\r?\n"
    Expect "and takes it out when the host can't place it" { GameLog $c2 } "undo Tree@6,1,6/0/0"
    Expect "so does the builder" { GameLog $s.c } "undo Tree@6,1,6/0/0"
}

function Scenario-InstantJoinLobbyCity {
    # The host opens a city and builds before hosting; a friend joins without anyone saving by hand,
    # and still gets the build made after the city's last save.
    $port = Next-Port
    $s = Start-LobbyPair $port
    Game $s.h "level"
    Expect "city open in the lobby" { HelperLog $s.h } "city ready in the lobby"
    Expect "the game saved it" { GameLog $s.h } "saved"
    Game $s.h "build Sign@7,1,7/0/0"
    Expect "host sees its build" { HelperLog $s.h } '"t":"place".*Sign@7,1,7/0/0'
    Helper $s.h "host"
    Expect "host shares the open city" { HelperLog $s.h } "Sharing the city you have open"
    Join-Lobby $s $port
    Expect "city sent with the newer build" { HelperLog $s.h } "\+ 1 newer builds"
    Expect "client's game gets the build" { GameLog $s.c } "place Sign@7,1,7/0/0"
    if ((HelperLog $s.h) -match "needs your city") { throw "the host was asked to save" }
    Write-Host "    ok: nobody had to save"
}

function Scenario-DriftRepair {
    # Each side gets an object the other never heard of; the client's city is made to match the host's.
    $s = Start-HostAndClient
    Game $s.c "sneak Junk@9,9,9/0/0"
    Game $s.h "sneak Extra@8,8,8/0/0"
    Expect "client takes out what the host doesn't have" { GameLog $s.c } "reconcile \d+: placed \d+ removed 1" 1 45
    Expect "client adds what the host has" { GameLog $s.c } "reconcile \d+: placed 1" 1 45
    Expect "client is told" { HelperLog $s.c } "Fixed 1 object that didn't match the host's city" 1 10
}

function Scenario-VersionRefused {
    $h = New-GameDir "host"; $port = Next-Port
    Start-Helper $h @("host", "--name", "Hosty", "--port", $port) | Out-Null
    Expect "host listens" { HelperLog $h } "Hosting on port $port"
    $tcp = New-Object Net.Sockets.TcpClient("127.0.0.1", $port)
    try {
        $stream = $tcp.GetStream()
        $stream.ReadTimeout = 10000
        $writer = New-Object IO.StreamWriter($stream)
        $writer.AutoFlush = $true
        $writer.WriteLine('{"t":"hello","name":"Oldie","version":"0.3.0"}')
        $line = (New-Object IO.StreamReader($stream)).ReadLine()
    } finally {
        $tcp.Close()
    }
    if ($line -notmatch '"t":"refused"') { throw "expected a refusal, got: $line" }
    Write-Host "    ok: an older helper is refused"
    Expect "host says why" { HelperLog $h } "Oldie tried to join with mod version 0\.3\.0"
}

function Scenario-OldHostRefused {
    # A stand-in 0.3.0 host: it welcomes anyone. The client must refuse it itself.
    $c = New-GameDir "client"; $port = Next-Port
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, $port)
    $listener.Start()
    try {
        $cp = Start-Helper $c @("lobby", "--name", "Clienty")
        Expect "client game in lobby" { GameLog $c } "hello role=lobby"
        Helper $c "join 127.0.0.1:$port"
        $tcp = $listener.AcceptTcpClient()
        $stream = $tcp.GetStream()
        $stream.ReadTimeout = 10000
        $hello = (New-Object IO.StreamReader($stream)).ReadLine()
        if ($hello -notmatch '"t":"hello"') { throw "expected hello, got: $hello" }
        $writer = New-Object IO.StreamWriter($stream)
        $writer.AutoFlush = $true
        $writer.WriteLine('{"t":"welcome","host":"Oldhost","version":"0.3.0"}')
        Expect "client refuses the old host" { HelperLog $c } "The host has mod version 0\.3\.0, you have"
        Expect "client back in the lobby" { HelperLog $c } "Back in the lobby"
        $tcp.Close()
    } finally {
        $listener.Stop()
    }
}

$scenarios = @("HelperUnits", "LuaSync",
    "HostJoin", "HostQuit", "HostKilled", "IgnoreLeave",
    "LobbyHostJoin", "LobbyBadJoin", "LobbyHostLeaves", "LobbyClientLeaves", "LobbyHostQuits", "LobbyHostGoneBeforeCity",
    "ClientBuildAccepted", "ClientBuildRejected", "ThreePlayers", "InstantJoinLobbyCity", "DriftRepair", "VersionRefused", "OldHostRefused")
if ($Only) {
    # "-File" passes "A,B" as one string.
    $Only = $Only | ForEach-Object { $_ -split "," } | ForEach-Object { $_.Trim() }
    $scenarios = $scenarios | Where-Object { $Only -contains $_ }
}

$failed = @()
foreach ($name in $scenarios) {
    Write-Host "== $name"
    try {
        & "Scenario-$name"
        Write-Host "   PASS"
    } catch {
        Write-Host "   FAIL: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host ($_.InvocationInfo.PositionMessage)
        $failed += $name
        # Keep the logs of the failed run for a look.
        $keep = Join-Path $root ".testrun\failed-$name"
        Stop-All
        if (Test-Path $keep) { Retry { Remove-Item -Recurse -Force $keep } }
        Retry { Copy-Item -Recurse $work $keep }
    } finally {
        Stop-All
    }
}
Write-Host ""
if ($failed.Count -gt 0) {
    Write-Host "$($failed.Count) of $($scenarios.Count) failed: $($failed -join ', ') (logs in .testrun\failed-*)" -ForegroundColor Red
    exit 1
}
Write-Host "All $($scenarios.Count) scenarios passed." -ForegroundColor Green
