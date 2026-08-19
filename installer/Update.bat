@echo off
REM Manual install/update. Same as PreLaunch.bat but talkative, and it waits so you can read it.
setlocal
set "PS1=%TEMP%\Update-SupermarketTweaks.ps1"
set "SRC=https://raw.githubusercontent.com/ZeldoKavira/SupermarketTweaks/main/installer/Update-SupermarketTweaks.ps1"

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "try { Invoke-WebRequest -Uri '%SRC%' -OutFile '%PS1%' -UseBasicParsing } catch { }"

if not exist "%PS1%" if exist "%~dp0Update-SupermarketTweaks.ps1" set "PS1=%~dp0Update-SupermarketTweaks.ps1"

powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%"
pause
