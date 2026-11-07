@echo off
rem Double-click after a Diablo II: Resurrected patch: makes the editor's game data again from
rem your install, then opens the editor.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\start.ps1" -Update
if errorlevel 1 pause
