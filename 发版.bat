@echo off
title Vincel Release
echo ================================================
echo   Vincel One-Click Release
echo   Make sure VS build succeeded first
echo ================================================
echo.
set /p notes=Update notes (press Enter to skip):
echo.
echo Packaging, please wait...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-release.ps1" -Notes "%notes%"
echo.
echo ================================================
echo   Done!
echo   New files: VincelSetup.exe + update.json
echo   Upload them to GitHub repo root.
echo ================================================
pause
