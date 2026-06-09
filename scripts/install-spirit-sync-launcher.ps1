param(
    [string] $GameRoot = "C:\Program Files (x86)\Steam\steamapps\common\Spirit City Lofi Sessions",
    [string] $BridgeSource = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [switch] $SkipNodeModules,
    [switch] $NoExeReplace
)

$ErrorActionPreference = "Stop"

function Assert-ChildPath {
    param(
        [Parameter(Mandatory)] [string] $Parent,
        [Parameter(Mandatory)] [string] $Child
    )

    $resolvedParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    $resolvedChild = [System.IO.Path]::GetFullPath($Child)

    if (-not $resolvedChild.StartsWith($resolvedParent, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify path outside ${resolvedParent}: ${resolvedChild}"
    }
}

function Copy-DirectoryFresh {
    param(
        [Parameter(Mandatory)] [string] $Source,
        [Parameter(Mandatory)] [string] $Destination,
        [Parameter(Mandatory)] [string] $AllowedParent
    )

    Assert-ChildPath -Parent $AllowedParent -Child $Destination

    if (Test-Path -LiteralPath $Destination) {
        Remove-Item -LiteralPath $Destination -Recurse -Force
    }

    Copy-Item -LiteralPath $Source -Destination $Destination -Recurse -Force
}

function Get-DotEnvValue {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string[]] $Names
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    foreach ($line in Get-Content -LiteralPath $Path) {
        $trimmed = $line.Trim()
        if ($trimmed.StartsWith("#") -or -not $trimmed.Contains("=")) {
            continue
        }

        $parts = $trimmed.Split("=", 2)
        $key = $parts[0].Trim()
        if ($Names -contains $key) {
            return $parts[1].Trim().Trim('"')
        }
    }

    return $null
}

function Get-NpmCommand {
    $npmCommand = Get-Command npm.cmd -ErrorAction SilentlyContinue
    if (-not $npmCommand) {
        $npmCommand = Get-Command npm -ErrorAction SilentlyContinue
    }

    if (-not $npmCommand) {
        throw "npm was not found on PATH. Install Node.js 20 or newer first."
    }

    return $npmCommand
}

$GameRoot = [System.IO.Path]::GetFullPath($GameRoot)
$BridgeSource = [System.IO.Path]::GetFullPath($BridgeSource)
$gameLauncher = Join-Path $GameRoot "SpiritCity.exe"
$gameBackup = Join-Path $GameRoot "SpiritCityBackup.exe"
$shippingExe = Join-Path $GameRoot "SpiritCity\Binaries\Win64\SpiritCity-Win64-Shipping.exe"
$installRoot = Join-Path $GameRoot "SpiritSync"
$envPath = Join-Path $GameRoot "spirit-sync.env"
$launcherProject = Join-Path $BridgeSource "tools\SpiritSyncLauncher\SpiritSyncLauncher.csproj"
$patcherProject = Join-Path $BridgeSource "tools\SpiritCityRuntimePatch\SpiritCityRuntimePatch.csproj"
$publishRoot = Join-Path $BridgeSource "artifacts\SpiritSyncLauncher"
$patcherPublishRoot = Join-Path $BridgeSource "artifacts\SpiritCityRuntimePatch"
$releaseBinRoot = Join-Path $BridgeSource "bin"
$prebuiltLauncher = Join-Path $releaseBinRoot "SpiritCity.exe"
$prebuiltPatcher = Join-Path $releaseBinRoot "SpiritCityRuntimePatch.exe"
$publishedLauncher = $prebuiltLauncher
$publishedPatcher = $prebuiltPatcher
$distServer = Join-Path $BridgeSource "dist\server.js"

if (-not (Test-Path -LiteralPath $GameRoot)) {
    throw "Spirit City install was not found: $GameRoot"
}

if (-not (Test-Path -LiteralPath $shippingExe)) {
    throw "Spirit City shipping executable was not found: $shippingExe"
}

if (-not (Test-Path -LiteralPath $distServer)) {
    $npm = Get-NpmCommand

    Write-Host "Building Spotify bridge..."
    Push-Location $BridgeSource
    try {
        & $npm.Source run build
    }
    finally {
        Pop-Location
    }
}
else {
    Write-Host "Using prebuilt Spotify bridge."
}

if (-not (Test-Path -LiteralPath $publishedLauncher)) {
    if (-not (Test-Path -LiteralPath $launcherProject)) {
        throw "Launcher project was not found: $launcherProject"
    }

    Write-Host "Publishing Spirit Sync launcher..."
    if (Test-Path -LiteralPath $publishRoot) {
        Assert-ChildPath -Parent $BridgeSource -Child $publishRoot
        Remove-Item -LiteralPath $publishRoot -Recurse -Force
    }

    & dotnet publish $launcherProject `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -o $publishRoot

    $publishedLauncher = Join-Path $publishRoot "SpiritCity.exe"
}
else {
    Write-Host "Using prebuilt Spirit Sync launcher."
}

if (-not (Test-Path -LiteralPath $publishedLauncher)) {
    throw "Published launcher was not produced: $publishedLauncher"
}

if (-not (Test-Path -LiteralPath $publishedPatcher)) {
    if (-not (Test-Path -LiteralPath $patcherProject)) {
        throw "Runtime patcher project was not found: $patcherProject"
    }

    Write-Host "Publishing Spirit Sync runtime patcher..."
    if (Test-Path -LiteralPath $patcherPublishRoot) {
        Assert-ChildPath -Parent $BridgeSource -Child $patcherPublishRoot
        Remove-Item -LiteralPath $patcherPublishRoot -Recurse -Force
    }

    & dotnet publish $patcherProject `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -o $patcherPublishRoot

    $publishedPatcher = Join-Path $patcherPublishRoot "SpiritCityRuntimePatch.exe"
}
else {
    Write-Host "Using prebuilt Spirit Sync runtime patcher."
}

if (-not (Test-Path -LiteralPath $publishedPatcher)) {
    throw "Published runtime patcher was not produced: $publishedPatcher"
}

Write-Host "Installing bridge files to $installRoot..."
New-Item -ItemType Directory -Path $installRoot -Force | Out-Null

Copy-DirectoryFresh `
    -Source (Join-Path $BridgeSource "dist") `
    -Destination (Join-Path $installRoot "dist") `
    -AllowedParent $installRoot
Copy-DirectoryFresh `
    -Source (Join-Path $BridgeSource "public") `
    -Destination (Join-Path $installRoot "public") `
    -AllowedParent $installRoot

Copy-Item -LiteralPath (Join-Path $BridgeSource "package.json") -Destination $installRoot -Force
Copy-Item -LiteralPath (Join-Path $BridgeSource "package-lock.json") -Destination $installRoot -Force

if (-not $SkipNodeModules) {
    $sourceNodeModules = Join-Path $BridgeSource "node_modules"
    $targetNodeModules = Join-Path $installRoot "node_modules"

    if (Test-Path -LiteralPath $sourceNodeModules) {
        Write-Host "Copying local node_modules..."
        Copy-DirectoryFresh `
            -Source $sourceNodeModules `
            -Destination $targetNodeModules `
            -AllowedParent $installRoot
    }
    else {
        Write-Host "Installing production npm dependencies..."
        $npm = Get-NpmCommand
        Push-Location $installRoot
        try {
            & $npm.Source install --omit=dev
        }
        finally {
            Pop-Location
        }
    }
}

if (-not (Test-Path -LiteralPath $envPath)) {
    $sourceEnv = Join-Path $BridgeSource ".env"
    $sourceClientId = Get-DotEnvValue -Path $sourceEnv -Names @("SPOTIFY_CLIENT_ID", "ClientID")

    if ($sourceClientId) {
        $sourcePort = Get-DotEnvValue -Path $sourceEnv -Names @("PORT")
        if (-not $sourcePort) {
            $sourcePort = "8012"
        }

        $sourceClientSecret = Get-DotEnvValue -Path $sourceEnv -Names @("SPOTIFY_CLIENT_SECRET", "ClientSecret")
        $sourceSpotifyUser = Get-DotEnvValue -Path $sourceEnv -Names @("SPOTIFY_USER", "SpotifyUser")

        @(
            "PORT=$sourcePort"
            "ClientID=`"$sourceClientId`""
            "ClientSecret=`"$sourceClientSecret`""
            "SpotifyUser=`"$sourceSpotifyUser`""
        ) | Set-Content -LiteralPath $envPath -Encoding UTF8

        Write-Host "Created $envPath from local .env."
    }
    else {
        Copy-Item -LiteralPath (Join-Path $BridgeSource "spirit-sync.env.example") -Destination $envPath
        Write-Host "Created $envPath. Put your Spotify ClientID in this file."
    }
}
else {
    Write-Host "Keeping existing $envPath."
}

$sourceTokenStore = Join-Path $BridgeSource ".spotify-tokens.json"
$targetTokenStore = Join-Path $installRoot ".spotify-tokens.json"
if ((Test-Path -LiteralPath $sourceTokenStore) -and -not (Test-Path -LiteralPath $targetTokenStore)) {
    Copy-Item -LiteralPath $sourceTokenStore -Destination $targetTokenStore -Force
    Write-Host "Copied existing Spotify login token store."
}

Copy-Item -LiteralPath $publishedLauncher -Destination (Join-Path $installRoot "SpiritCity.exe") -Force
Copy-Item -LiteralPath $publishedPatcher -Destination (Join-Path $installRoot "SpiritCityRuntimePatch.exe") -Force

if (-not $NoExeReplace) {
    if (-not (Test-Path -LiteralPath $gameBackup)) {
        if (-not (Test-Path -LiteralPath $gameLauncher)) {
            throw "Could not find root launcher to back up: $gameLauncher"
        }

        Write-Host "Backing up original SpiritCity.exe to SpiritCityBackup.exe..."
        Move-Item -LiteralPath $gameLauncher -Destination $gameBackup
    }
    else {
        Write-Host "SpiritCityBackup.exe already exists; leaving it in place."
    }

    Write-Host "Installing replacement root SpiritCity.exe..."
    Copy-Item -LiteralPath $publishedLauncher -Destination $gameLauncher -Force
}

Write-Host "Spirit Sync launcher installed."
Write-Host "Launch Spirit City through Steam. If needed, log in at http://127.0.0.1:8012/login."
