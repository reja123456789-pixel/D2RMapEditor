@echo off
rem Double-click to start the map editor. With "D2R Map Editor.exe" it needs nothing else; without it
rem (the source version) the first start makes the game data with Python 3 (about a minute).
if exist "%~dp0D2R Map Editor.exe" ( start "" "%~dp0D2R Map Editor.exe" & exit /b 0 )
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\start.ps1"
if errorlevel 1 pause
