# Builds CPC-Multiplayer.exe, the stand-in game (tests/FakeGame.cs) and the Lua test runner
# (tests/LuaRunner.cs, with lua54.dll) into .testrun\bin.
#
# Uses the .NET SDK (dotnet build) when one is installed. Otherwise it compiles with Roslyn's csc.dll
# from the Microsoft.Net.Compilers.Toolset NuGet package (downloaded once into .testrun\tools), run on
# any installed .NET runtime, against the .NET Framework 4 assemblies in the Windows folder.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1 [-Out <folder>]
#
# Keep -Out short: .NET Framework apps won't start from a path longer than 260 characters.
param(
    [string]$Out = (Join-Path $PSScriptRoot ".testrun\bin")
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$toolsDir = Join-Path $root ".testrun\tools"
$roslynVersion = "4.11.0"
$keraLuaVersion = "1.4.4"
$fw = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319"

function Get-Csc {
    # An installed SDK ships its own csc.dll.
    $sdks = @()
    try { $sdks = @(& dotnet --list-sdks 2>$null) } catch { }
    foreach ($line in $sdks) {
        if ($line -match '^(\S+) \[(.+)\]$') {
            $sdkCsc = Join-Path $Matches[2] "$($Matches[1])\Roslyn\bincore\csc.dll"
            if (Test-Path $sdkCsc) { return $sdkCsc }
        }
    }
    $csc = Join-Path $toolsDir "roslyn-$roslynVersion\tasks\netcore\bincore\csc.dll"
    if (Test-Path $csc) { return $csc }
    $pkgDir = Join-Path $toolsDir "roslyn-$roslynVersion"
    $zip = Join-Path $toolsDir "roslyn-$roslynVersion.zip"
    New-Item -ItemType Directory -Force $toolsDir | Out-Null
    Write-Host "Downloading Microsoft.Net.Compilers.Toolset $roslynVersion from nuget.org..."
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest "https://www.nuget.org/api/v2/package/Microsoft.Net.Compilers.Toolset/$roslynVersion" -OutFile $zip -UseBasicParsing
    Expand-Archive $zip $pkgDir -Force
    Remove-Item $zip
    if (-not (Test-Path $csc)) { throw "csc.dll not found in the package" }
    return $csc
}

function Q([string]$path) { return '"' + $path + '"' }

function Invoke-Csc([string[]]$cscArgs) {
    $csc = Get-Csc
    $rsp = Join-Path $toolsDir "csc.rsp"
    # One argument per line; a response file avoids command-line length trouble.
    Set-Content -Path $rsp -Value $cscArgs -Encoding ascii
    & dotnet $csc -noconfig "@$rsp"
    if ($LASTEXITCODE -ne 0) { throw "csc failed" }
}

New-Item -ItemType Directory -Force $Out | Out-Null
$fakeOut = Join-Path $Out "fake"
New-Item -ItemType Directory -Force $fakeOut | Out-Null

$hasSdk = $false
try { $hasSdk = [bool](& dotnet --list-sdks 2>$null) } catch { }

if ($hasSdk) {
    Write-Host "Building with the .NET SDK..."
    & dotnet build (Join-Path $root "CPC-Multiplayer.csproj") -c Release -o $Out --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }
} else {
    Write-Host "Building with Roslyn csc.dll (no .NET SDK found)..."
    # Mirrors CPC-Multiplayer.csproj: src\**\*.cs, lua\*.lua embedded as "lua.<name>".
    $a = @("-nologo", "-nostdlib+", "-target:exe", "-platform:x64", "-langversion:12",
        "-unsafe+", "-optimize+", ("-out:" + (Q (Join-Path $Out "CPC-Multiplayer.exe"))))
    foreach ($ref in "mscorlib.dll", "System.dll", "System.Core.dll", "System.Web.Extensions.dll") {
        $a += "-reference:" + (Q (Join-Path $fw $ref))
    }
    foreach ($lua in Get-ChildItem (Join-Path $root "lua") -Filter *.lua) {
        $a += "-resource:" + (Q $lua.FullName) + ",lua." + $lua.BaseName
    }
    $a += Get-ChildItem (Join-Path $root "src") -Recurse -Filter *.cs | ForEach-Object { Q $_.FullName }
    Invoke-Csc $a
}

# Lua 5.4 for the mod's Lua tests (tests/lua): lua54.dll from the KeraLua package, downloaded once.
function Get-Lua54 {
    $dll = Join-Path $toolsDir "keralua-$keraLuaVersion\runtimes\win-x64\native\lua54.dll"
    if (Test-Path $dll) { return $dll }
    $pkgDir = Join-Path $toolsDir "keralua-$keraLuaVersion"
    $zip = Join-Path $toolsDir "keralua-$keraLuaVersion.zip"
    New-Item -ItemType Directory -Force $toolsDir | Out-Null
    Write-Host "Downloading KeraLua $keraLuaVersion (Lua 5.4) from nuget.org..."
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest "https://www.nuget.org/api/v2/package/KeraLua/$keraLuaVersion" -OutFile $zip -UseBasicParsing
    Expand-Archive $zip $pkgDir -Force
    Remove-Item $zip
    if (-not (Test-Path $dll)) { throw "lua54.dll not found in the package" }
    return $dll
}

Write-Host "Building the stand-in game..."
# Commas bind tighter than + in PowerShell, so concatenations in these lists are parenthesised.
$a = @("-nologo", "-nostdlib+", "-target:exe", "-platform:x64", "-langversion:12", "-optimize+",
    ("-out:" + (Q (Join-Path $fakeOut "carparkcapital.exe"))),
    ("-reference:" + (Q (Join-Path $fw "mscorlib.dll"))), ("-reference:" + (Q (Join-Path $fw "System.dll"))),
    ("-reference:" + (Q (Join-Path $fw "System.Core.dll"))),
    (Q (Join-Path $root "tests\FakeGame.cs")))
Invoke-Csc $a

Write-Host "Building the Lua test runner..."
$luaOut = Join-Path $Out "luarun"
New-Item -ItemType Directory -Force $luaOut | Out-Null
Copy-Item (Get-Lua54) $luaOut -Force
$a = @("-nologo", "-nostdlib+", "-target:exe", "-platform:x64", "-langversion:12", "-optimize+",
    ("-out:" + (Q (Join-Path $luaOut "luarun.exe"))),
    ("-reference:" + (Q (Join-Path $fw "mscorlib.dll"))),
    (Q (Join-Path $root "tests\LuaRunner.cs")))
Invoke-Csc $a

Write-Host "Building the helper unit tests..."
$testOut = Join-Path $Out "tests"
New-Item -ItemType Directory -Force $testOut | Out-Null
$a = @("-nologo", "-nostdlib+", "-target:exe", "-platform:x64", "-langversion:12", "-unsafe+", "-optimize+",
    "-main:HelperTests", ("-out:" + (Q (Join-Path $testOut "helpertests.exe"))))
foreach ($ref in "mscorlib.dll", "System.dll", "System.Core.dll", "System.Web.Extensions.dll") {
    $a += "-reference:" + (Q (Join-Path $fw $ref))
}
$a += Get-ChildItem (Join-Path $root "src") -Recurse -Filter *.cs | ForEach-Object { Q $_.FullName }
$a += Q (Join-Path $root "tests\HelperTests.cs")
Invoke-Csc $a

Write-Host "Built into $Out"
