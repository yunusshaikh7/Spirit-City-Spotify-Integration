param(
    [string] $SavedDir = (Join-Path $env:LOCALAPPDATA "SpiritCity\Saved"),
    [string] $OutputDir = (Join-Path $PSScriptRoot "..\.spirit-city-snapshots"),
    [string] $Label = (Get-Date -Format "yyyyMMdd-HHmmss"),
    [string] $CompareTo = ""
)

$ErrorActionPreference = "Stop"

function Get-RelativePath([string] $BasePath, [string] $Path) {
    $baseUri = [System.Uri]::new(($BasePath.TrimEnd("\") + "\"))
    $pathUri = [System.Uri]::new($Path)
    return [System.Uri]::UnescapeDataString($baseUri.MakeRelativeUri($pathUri).ToString()).Replace("/", "\")
}

function Get-InterestingStrings([string] $Path) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    $matches = [regex]::Matches($text, "(?i)(spirit-sync|spotify|custommusic|musicplayer|playlist|youtube|youtu|https?://[^\x00\s]+|127\.0\.0\.1|localhost)")

    return @($matches | ForEach-Object { $_.Value } | Select-Object -Unique)
}

if (-not (Test-Path -LiteralPath $SavedDir)) {
    throw "Spirit City saved directory was not found: $SavedDir"
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$roots = @(
    (Join-Path $SavedDir "SaveGames"),
    (Join-Path $SavedDir "Config\Windows"),
    (Join-Path $SavedDir "Logs")
) | Where-Object { Test-Path -LiteralPath $_ }

$files = foreach ($root in $roots) {
    Get-ChildItem -LiteralPath $root -File -Recurse
}

$entries = foreach ($file in $files) {
    try {
        $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName
    }
    catch {
        Write-Warning "Skipping locked file: $($file.FullName)"
        continue
    }

    [pscustomobject]@{
        path = Get-RelativePath $SavedDir $file.FullName
        length = $file.Length
        lastWriteTimeUtc = $file.LastWriteTimeUtc.ToString("O")
        sha256 = $hash.Hash
        strings = Get-InterestingStrings $file.FullName
    }
}

$snapshot = [pscustomobject]@{
    label = $Label
    savedDir = $SavedDir
    createdAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    files = @($entries | Sort-Object path)
}

$outputPath = Join-Path $OutputDir "$Label.json"
$snapshot | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 -LiteralPath $outputPath
Write-Host "Wrote snapshot: $outputPath"

if ($CompareTo) {
    $before = Get-Content -Raw -LiteralPath $CompareTo | ConvertFrom-Json
    $beforeByPath = @{}
    foreach ($entry in $before.files) {
        $beforeByPath[$entry.path] = $entry
    }

    $afterByPath = @{}
    foreach ($entry in $snapshot.files) {
        $afterByPath[$entry.path] = $entry
    }

    Write-Host ""
    Write-Host "Changed files:"
    foreach ($entry in $snapshot.files) {
        $previous = $beforeByPath[$entry.path]
        if (-not $previous) {
            Write-Host "  ADDED   $($entry.path) ($($entry.length) bytes)"
            continue
        }

        if ($previous.sha256 -ne $entry.sha256) {
            Write-Host "  CHANGED $($entry.path) ($($previous.length) -> $($entry.length) bytes)"
            $newStrings = @($entry.strings | Where-Object { $previous.strings -notcontains $_ })
            foreach ($value in $newStrings) {
                Write-Host "          + $value"
            }
        }
    }

    foreach ($entry in $before.files) {
        if (-not $afterByPath[$entry.path]) {
            Write-Host "  REMOVED $($entry.path)"
        }
    }
}
