param(
    [string] $GameRoot = "C:\Program Files (x86)\Steam\steamapps\common\Spirit City Lofi Sessions",
    [switch] $RemoveSpiritSyncFiles
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

$GameRoot = [System.IO.Path]::GetFullPath($GameRoot)
$gameLauncher = Join-Path $GameRoot "SpiritCity.exe"
$gameBackup = Join-Path $GameRoot "SpiritCityBackup.exe"
$installRoot = Join-Path $GameRoot "SpiritSync"
$envPath = Join-Path $GameRoot "spirit-sync.env"

if (-not (Test-Path -LiteralPath $GameRoot)) {
    throw "Spirit City install was not found: $GameRoot"
}

if (Test-Path -LiteralPath $gameBackup) {
    if (Test-Path -LiteralPath $gameLauncher) {
        Assert-ChildPath -Parent $GameRoot -Child $gameLauncher
        Remove-Item -LiteralPath $gameLauncher -Force
    }

    Move-Item -LiteralPath $gameBackup -Destination $gameLauncher
    Write-Host "Restored original SpiritCity.exe."
}
else {
    Write-Host "SpiritCityBackup.exe was not found. Nothing to restore."
}

if ($RemoveSpiritSyncFiles) {
    if (Test-Path -LiteralPath $installRoot) {
        Assert-ChildPath -Parent $GameRoot -Child $installRoot
        Remove-Item -LiteralPath $installRoot -Recurse -Force
        Write-Host "Removed $installRoot."
    }

    if (Test-Path -LiteralPath $envPath) {
        Assert-ChildPath -Parent $GameRoot -Child $envPath
        Remove-Item -LiteralPath $envPath -Force
        Write-Host "Removed $envPath."
    }
}

Write-Host "Spirit Sync launcher uninstall complete."
