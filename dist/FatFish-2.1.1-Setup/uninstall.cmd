@echo off
setlocal
title FatFish - Uninstall
rem Pure ASCII + CRLF on purpose (see install.cmd).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1" %*
echo.
pause
endlocal
