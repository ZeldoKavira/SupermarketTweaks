@echo off
REM Runs from Steam's launch options, before the game starts.
REM
REM Self-contained on purpose: this is the only file that needs to exist on the machine. It pulls
REM the current installer script straight from the repo each time, so the installer itself stays
REM up to date without git, a clone, or the GitHub CLI.
REM
REM Everything is best-effort. If the network is down or GitHub is unreachable the game must still
REM launch, so nothing here ever returns a failing exit code.
setlocal
set "PS1=%TEMP%\Update-SupermarketTweaks.ps1"
set "SRC=https://raw.githubusercontent.com/ZeldoKavira/SupermarketTweaks/main/installer/Update-SupermarketTweaks.ps1"

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "try { Invoke-WebRequest -Uri '%SRC%' -OutFile '%PS1%' -UseBasicParsing } catch { }"

REM Fall back to a copy sitting next to this file, if there is one.
if not exist "%PS1%" if exist "%~dp0Update-SupermarketTweaks.ps1" set "PS1=%~dp0Update-SupermarketTweaks.ps1"

if exist "%PS1%" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%" -Quiet
) else (
  echo Could not reach the updater; starting the game with whatever is installed.
)
exit /b 0
