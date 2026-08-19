@echo off
REM Double-clickable wrapper. -ExecutionPolicy Bypass so an unsigned script runs without the
REM user having to change machine-wide policy.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Update-SupermarketTweaks.ps1"
pause
