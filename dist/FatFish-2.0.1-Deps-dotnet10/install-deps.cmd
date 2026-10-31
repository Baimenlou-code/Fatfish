@echo off
setlocal
title FatFish - .NET 10 Desktop Runtime
rem Pure ASCII + CRLF on purpose (see FatFish installer).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-deps.ps1" %*
echo.
pause
endlocal
