@echo off
REM Pull the newest scripts, then install the newest mod build.
REM
REM Two separate things go stale: this repo (the installer itself) and the mod release. Update.bat
REM only handles the second, so a change to the installer would never reach a machine that only
REM ever ran Update.bat. This does both, which makes it the one command worth remembering.
cd /d "%~dp0.."
echo Updating scripts...
git pull --ff-only
if errorlevel 1 (
  echo.
  echo Could not update the scripts - carrying on with the copy already here.
  echo.
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Update-SupermarketTweaks.ps1"
pause
