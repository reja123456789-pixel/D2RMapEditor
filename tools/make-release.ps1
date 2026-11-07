# Builds "D2R Map Editor.exe" and packs the release zip for Nexus / GitHub releases into dist\.
#   powershell -ExecutionPolicy Bypass -File tools\make-release.ps1 [-CascLib <path to CascLib.dll>]
# The zip holds the editor, the exe, CascLib.dll with its MIT license, and the README. Never the
# game data (gamedata.js, game\): each user makes their own from their own copy of the game.
param([string]$CascLib)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

[xml]$proj = Get-Content (Join-Path $root 'launcher\D2RMapEditorLauncher.csproj')
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version

if (-not $CascLib) {
    $CascLib = @((Join-Path $root 'CascLib.dll'), (Join-Path $root 'launcher\CascLib.dll')) + @(Get-ChildItem "${env:ProgramFiles(x86)}\Diablo II Resurrected\*\tools\CascLib.dll" -ErrorAction SilentlyContinue | ForEach-Object FullName) |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $CascLib -or -not (Test-Path $CascLib)) { throw 'CascLib.dll not found: pass -CascLib <path> (D2RMM has one in its tools folder).' }

Write-Host "Building D2R Map Editor.exe $version..."
dotnet publish (Join-Path $root 'launcher\D2RMapEditorLauncher.csproj') -c Release -o $root -nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$stage = Join-Path $root "dist\stage\D2R Map Editor"
if (Test-Path (Join-Path $root 'dist\stage')) { Remove-Item -Recurse -Force (Join-Path $root 'dist\stage') -Confirm:$false }
New-Item -ItemType Directory -Force $stage | Out-Null
foreach ($f in 'D2R Map Editor.exe', 'index.html', 'ds1.js', 'analysis.js', 'README.md', 'Update game data.cmd') { Copy-Item (Join-Path $root $f) $stage }
Copy-Item $CascLib (Join-Path $stage 'CascLib.dll')
Copy-Item (Join-Path $root 'launcher\CascLib-LICENSE.txt') $stage
New-Item -ItemType Directory -Force (Join-Path $stage 'tools') | Out-Null
foreach ($f in 'gamedata.js', 'game') { if (Test-Path (Join-Path $stage $f)) { throw "Game data in the release: $f" } }

$zip = Join-Path $root "dist\D2R-Map-Editor-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force -Confirm:$false }
Compress-Archive -Path $stage -DestinationPath $zip
Remove-Item -Recurse -Force (Join-Path $root 'dist\stage') -Confirm:$false
Write-Host "Release: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
