param(
    [string] $GameRoot = "C:\Program Files (x86)\Steam\steamapps\common\Spirit City Lofi Sessions",
    [string] $BridgeRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [int] $CefDebugPort = 0,
    [switch] $SkipGame
)

$ErrorActionPreference = "Stop"

$gameExe = Join-Path $GameRoot "SpiritCity\Binaries\Win64\SpiritCity-Win64-Shipping.exe"
$rootLauncher = Join-Path $GameRoot "SpiritCity.exe"
$legacyEnv = Join-Path $GameRoot "spirit-sync.env"

if (-not (Test-Path -LiteralPath $GameRoot)) {
    throw "Spirit City install was not found: $GameRoot"
}

if (-not (Test-Path -LiteralPath $gameExe)) {
    throw "Spirit City shipping executable was not found: $gameExe"
}

if (-not (Test-Path -LiteralPath $legacyEnv)) {
    Write-Host "No spirit-sync.env found at $legacyEnv"
    Write-Host "Create one from spirit-sync.env.example or use .env in the bridge folder."
}

$npm = Get-Command npm.cmd -ErrorAction SilentlyContinue
if (-not $npm) {
    $npm = Get-Command npm -ErrorAction SilentlyContinue
}

if (-not $npm) {
    throw "npm was not found on PATH. Install Node.js 20 or newer first."
}

$distServer = Join-Path $BridgeRoot "dist\server.js"
if (-not (Test-Path -LiteralPath $distServer)) {
    Write-Host "Building bridge..."
    Push-Location $BridgeRoot
    try {
        & $npm.Source run build
    }
    finally {
        Pop-Location
    }
}

$bridgeStartInfo = [System.Diagnostics.ProcessStartInfo]::new()
$bridgeStartInfo.FileName = $npm.Source
$bridgeStartInfo.Arguments = "run start"
$bridgeStartInfo.WorkingDirectory = $BridgeRoot
$bridgeStartInfo.UseShellExecute = $false
$bridgeStartInfo.CreateNoWindow = $true
$bridgeStartInfo.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
$bridgeStartInfo.EnvironmentVariables["SPIRIT_CITY_INSTALL_DIR"] = $GameRoot

$bridgeProcess = [System.Diagnostics.Process]::Start($bridgeStartInfo)

Write-Host "Started Spotify bridge process $($bridgeProcess.Id)."
Write-Host "Bridge URL: http://127.0.0.1:8012/spirit-sync"

if (-not $SkipGame) {
    if ($CefDebugPort -gt 0) {
        Start-Process `
            -FilePath $gameExe `
            -ArgumentList @("SpiritCity", "cefdebug=$CefDebugPort") `
            -WorkingDirectory $GameRoot
        Write-Host "Started Spirit City with CEF debugging on http://127.0.0.1:$CefDebugPort"
    }
    else {
        $gameToLaunch = if (Test-Path -LiteralPath $rootLauncher) { $rootLauncher } else { $gameExe }
        Start-Process -FilePath $gameToLaunch -WorkingDirectory $GameRoot
    }

    Write-Host "Started Spirit City."
}
