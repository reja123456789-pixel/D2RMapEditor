@echo off
rem Builds "D2R Map Editor.exe" next to index.html (needs the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0)
cd /d "%~dp0.."
where dotnet >nul 2>&1 || ( echo The .NET 8 SDK is not installed: https://dotnet.microsoft.com/download/dotnet/8.0 & pause & exit /b 1 )
echo Building D2R Map Editor.exe...
dotnet publish "launcher\D2RMapEditorLauncher.csproj" -c Release -o "%~dp0.." -nologo -v quiet
if errorlevel 1 ( echo. & echo Build FAILED. & if not "%1"=="nopause" pause & exit /b 1 )
echo Done: "D2R Map Editor.exe" is next to index.html.
if not "%1"=="nopause" pause
