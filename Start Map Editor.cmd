@echo off
rem Double-click to start the map editor. The first time it makes the game data from your
rem Diablo II: Resurrected install (needs Python 3, about a minute), then opens the editor.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\start.ps1"
if errorlevel 1 pause
