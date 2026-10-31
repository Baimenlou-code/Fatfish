@echo off
setlocal
title FatFish 2.0.1 - Setup
rem ------------------------------------------------------------
rem  FatFish installer launcher.
rem  Keep this file pure ASCII + CRLF: cmd.exe miscounts bytes
rem  when a .bat carries multi-byte UTF-8.
rem ------------------------------------------------------------
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
echo.
pause
endlocal
