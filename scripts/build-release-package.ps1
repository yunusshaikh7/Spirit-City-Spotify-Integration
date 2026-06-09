param(
    [string] $Version = "0.1.0"
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

function Reset-Directory {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $AllowedParent
    )

    Assert-ChildPath -Parent $AllowedParent -Child $Path
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Copy-Directory {
    param(
        [Parameter(Mandatory)] [string] $Source,
        [Parameter(Mandatory)] [string] $Destination,
        [Parameter(Mandatory)] [string] $AllowedParent
    )

    Assert-ChildPath -Parent $AllowedParent -Child $Destination
    Copy-Item -LiteralPath $Source -Destination $Destination -Recurse -Force
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactsRoot = Join-Path $repoRoot "artifacts"
$stageRoot = Join-Path $artifactsRoot "SpiritSync-$Version"
$binRoot = Join-Path $stageRoot "bin"
$zipPath = Join-Path $artifactsRoot "SpiritSync-$Version-windows.zip"
$publishRoot = Join-Path $artifactsRoot "publish"

Reset-Directory -Path $stageRoot -AllowedParent $artifactsRoot
Reset-Directory -Path $binRoot -AllowedParent $stageRoot
Reset-Directory -Path $publishRoot -AllowedParent $artifactsRoot

Push-Location $repoRoot
try {
    npm run build
}
finally {
    Pop-Location
}

$projects = @(
    @{
        Project = "tools\SpiritSyncLauncher\SpiritSyncLauncher.csproj"
        Output = "launcher"
        CopyTo = Join-Path $binRoot "SpiritCity.exe"
    }
    @{
        Project = "tools\SpiritCityRuntimePatch\SpiritCityRuntimePatch.csproj"
        Output = "runtime-patcher"
        CopyTo = Join-Path $binRoot "SpiritCityRuntimePatch.exe"
    }
    @{
        Project = "tools\SpiritSyncInstaller\SpiritSyncInstaller.csproj"
        Output = "installer"
        CopyTo = Join-Path $stageRoot "SpiritSyncInstaller.exe"
    }
    @{
        Project = "tools\SpiritSyncUninstaller\SpiritSyncUninstaller.csproj"
        Output = "uninstaller"
        CopyTo = Join-Path $stageRoot "SpiritSyncUninstaller.exe"
    }
)

foreach ($project in $projects) {
    $outputPath = Join-Path $publishRoot $project.Output
    Reset-Directory -Path $outputPath -AllowedParent $publishRoot

    dotnet publish (Join-Path $repoRoot $project.Project) `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -o $outputPath

    $exe = Get-ChildItem -LiteralPath $outputPath -Filter *.exe | Select-Object -First 1
    if (-not $exe) {
        throw "No executable produced for $($project.Project)"
    }

    Copy-Item -LiteralPath $exe.FullName -Destination $project.CopyTo -Force
}

Copy-Item -LiteralPath (Join-Path $repoRoot "README.md") -Destination $stageRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot ".env.example") -Destination $stageRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "spirit-sync.env.example") -Destination $stageRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "package.json") -Destination $stageRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "package-lock.json") -Destination $stageRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "tsconfig.json") -Destination $stageRoot -Force

Copy-Directory -Source (Join-Path $repoRoot "dist") -Destination (Join-Path $stageRoot "dist") -AllowedParent $stageRoot
Copy-Directory -Source (Join-Path $repoRoot "public") -Destination (Join-Path $stageRoot "public") -AllowedParent $stageRoot
Copy-Directory -Source (Join-Path $repoRoot "scripts") -Destination (Join-Path $stageRoot "scripts") -AllowedParent $stageRoot
Copy-Directory -Source (Join-Path $repoRoot "src") -Destination (Join-Path $stageRoot "src") -AllowedParent $stageRoot
$stageToolsRoot = Join-Path $stageRoot "tools"
New-Item -ItemType Directory -Path $stageToolsRoot -Force | Out-Null
$toolProjects = @(
    "SpiritCityAssetProbe",
    "SpiritCityRuntimePatch",
    "SpiritSyncInstaller",
    "SpiritSyncLauncher",
    "SpiritSyncUninstaller"
)

foreach ($toolProject in $toolProjects) {
    $sourceTool = Join-Path (Join-Path $repoRoot "tools") $toolProject
    $targetTool = Join-Path $stageToolsRoot $toolProject
    New-Item -ItemType Directory -Path $targetTool -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceTool "Program.cs") -Destination $targetTool -Force
    Copy-Item -LiteralPath (Join-Path $sourceTool "$toolProject.csproj") -Destination $targetTool -Force
}

Push-Location $stageRoot
try {
    npm install --omit=dev
}
finally {
    Pop-Location
}

if (Test-Path -LiteralPath $zipPath) {
    Assert-ChildPath -Parent $artifactsRoot -Child $zipPath
    Remove-Item -LiteralPath $zipPath -Force
}

Get-ChildItem -LiteralPath $stageRoot | Compress-Archive -DestinationPath $zipPath -Force
Write-Host "Built $zipPath"
