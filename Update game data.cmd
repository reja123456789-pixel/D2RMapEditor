@echo off
rem Double-click after a Diablo II: Resurrected patch: makes the editor's game data again from
rem your install, then opens the editor.
if exist "%~dp0D2R Map Editor.exe" ( start "" "%~dp0D2R Map Editor.exe" --update & exit /b 0 )
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\start.ps1" -Update
if errorlevel 1 pause
