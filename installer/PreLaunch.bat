@echo off
REM Runs from Steam's launch options, before the game starts. See README.
REM
REM Everything here is best-effort on purpose: if the network is down, GitHub is unreachable, or
REM git is missing, the game must still launch. Nothing in this file returns a failing exit code.
cd /d "%~dp0.."
git pull --ff-only --quiet 2>nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Update-SupermarketTweaks.ps1" -Quiet
exit /b 0
