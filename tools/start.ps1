# Starts the map editor: makes the game data from your D2R install when it's missing or outdated
# (tools\extract-game-files.py), then opens index.html in Chrome or Edge.
# Run through "Start Map Editor.cmd" (or "Update game data.cmd", which passes -Update).
param([switch]$Update, [switch]$NoBrowser)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$savedPath = Join-Path $PSScriptRoot 'd2r-path.txt'

function Test-D2R($p) { $p -and (Test-Path (Join-Path $p 'D2R.exe')) }

function Find-D2R {
    if (Test-Path $savedPath) { $p = (Get-Content $savedPath -Raw).Trim(); if (Test-D2R $p) { return $p } }
    $keys = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Diablo II Resurrected',
            'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Diablo II Resurrected'
    foreach ($k in $keys) {
        if (Test-Path $k) { $p = (Get-ItemProperty $k).InstallLocation; if (Test-D2R $p) { return $p } }
    }
    foreach ($p in "${env:ProgramFiles(x86)}\Diablo II Resurrected", "$env:ProgramFiles\Diablo II Resurrected",
                   'C:\Games\Diablo II Resurrected', 'D:\Diablo II Resurrected', 'D:\Games\Diablo II Resurrected') {
        if (Test-D2R $p) { return $p }
    }
    Write-Host 'Diablo II: Resurrected was not found. Pick its folder (the one with D2R.exe).'
    Add-Type -AssemblyName System.Windows.Forms
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = 'Pick the Diablo II: Resurrected folder (the one with D2R.exe)'
    while ($dlg.ShowDialog() -eq 'OK') {
        if (Test-D2R $dlg.SelectedPath) { return $dlg.SelectedPath }
        Write-Host "No D2R.exe in $($dlg.SelectedPath). Try again."
    }
    throw 'No Diablo II: Resurrected folder picked.'
}

function Find-Python {
    # the py launcher first; the WindowsApps "python" is only a shortcut to the Microsoft Store
    $py = Get-Command py -ErrorAction SilentlyContinue
    if ($py) { & $py.Source -3 --version *> $null; if ($LASTEXITCODE -eq 0) { return @($py.Source, '-3') } }
    foreach ($name in 'python', 'python3') {
        foreach ($c in @(Get-Command $name -All -ErrorAction SilentlyContinue)) {
            if ($c.Source -match '\\WindowsApps\\') { continue }
            & $c.Source --version *> $null
            if ($LASTEXITCODE -eq 0) { return @($c.Source) }
        }
    }
    return $null
}

function Test-GameData {
    $gd = Join-Path $root 'gamedata.js'
    foreach ($f in $gd, (Join-Path $root 'game\maps.js'), (Join-Path $root 'game\tiles.js')) { if (-not (Test-Path $f)) { return 'missing' } }
    # made by an older extractor: lacks the lvlprest flags the export lists
    if (-not (Select-String -Path $gd -Pattern '"flags":' -SimpleMatch -Quiet)) { return 'outdated' }
    return 'ok'
}

$state = Test-GameData
if ($Update -or $state -ne 'ok') {
    $why = if ($Update) { 'Updating' } elseif ($state -eq 'missing') { 'First start: making' } else { 'Updating the outdated' }
    Write-Host "$why game data from your Diablo II: Resurrected install (about a minute)..."
    $d2r = Find-D2R
    Set-Content -Path $savedPath -Value $d2r -Encoding ASCII
    Write-Host "  D2R folder: $d2r"
    $python = Find-Python
    if (-not $python) {
        Write-Host ''
        Write-Host 'Python 3 is needed once, to read the game files. Get it from https://www.python.org/downloads/'
        Write-Host '(tick "Add python.exe to PATH" in the installer), then start the editor again.'
        Start-Process 'https://www.python.org/downloads/'
        exit 1
    }
    $exe = $python[0]; $pyArgs = @($python | Select-Object -Skip 1) + @((Join-Path $PSScriptRoot 'extract-game-files.py'), $d2r)
    & $exe @pyArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host ''
        Write-Host 'Making the game data failed (see above). If it says CascLib.dll was not found: D2RMM has one in its'
        Write-Host 'tools folder; copy it next to tools\extract-game-files.py and start again.'
        exit 1
    }
    if ((Test-GameData) -ne 'ok') { Write-Host 'The game data is still incomplete (see above).'; exit 1 }
    Write-Host 'Game data ready.'
}

# open the editor: Chrome, else Edge, else the default browser
if ($NoBrowser) { Write-Host 'Game data ok.'; exit 0 }
$url = ([System.Uri](Join-Path $root 'index.html')).AbsoluteUri
foreach ($b in 'chrome.exe', 'msedge.exe') {
    $reg = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\$b"
    $regU = "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\$b"
    if ((Test-Path $reg) -or (Test-Path $regU)) { Start-Process $b -ArgumentList "`"$url`""; exit 0 }
}
Start-Process (Join-Path $root 'index.html')
