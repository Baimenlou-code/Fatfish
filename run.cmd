@echo off
rem ============================================================
rem  Double-click launcher for the FatFish WPF app.
rem  Keeps the console window open so startup errors stay visible.
rem
rem  Workspace root resolution (in order):
rem    1. FATFISH_WORKSPACE already set in the environment
rem    2. this script's own folder -- the repo root, which is where
rem       .env / src / tests actually live
rem    3. current directory (app default)
rem
rem  Config file: pinned to <repo root>\.env via FATFISH_ENV, so
rem  "which .env am I actually reading" no longer depends on cwd.
rem
rem  2026-11-20 fix: earlier revisions pointed one or two levels ABOVE
rem  the script ("..", and before that "..\.."). In a plain clone the
rem  script sits at the repo root, so both landed outside the repo --
rem  logs and the runtime workspace were written to the parent folder.
rem  The repo root is the script folder itself, so we use "%~dp0."
rem  (the trailing dot strips the final backslash).
rem
rem  Logs and the runtime workspace therefore land in <repo>\logs and
rem  <repo>\workspace, which is exactly what .gitignore ignores
rem  (/logs/ and /workspace/, both anchored to the repo root).
rem
rem  NOTE: pure ASCII + CRLF on purpose. See README.
rem ============================================================

setlocal
cd /d "%~dp0"

set "DOTNET_HOME=%LOCALAPPDATA%\Microsoft\dotnet"
set "DOTNET_EXE=%DOTNET_HOME%\dotnet.exe"
set "PATH=%DOTNET_HOME%;%PATH%"
set "DOTNET_ROOT=%DOTNET_HOME%"

if not exist "%DOTNET_EXE%" (
    echo [ERROR] .NET SDK not found. Run .setup\install.cmd first.
    pause
    exit /b 1
)

if not defined FATFISH_WORKSPACE (
    for %%I in ("%~dp0.") do set "FATFISH_WORKSPACE=%%~fI"
)

if not defined FATFISH_ENV (
    if exist "%~dp0.env" set "FATFISH_ENV=%~dp0.env"
)

echo Workspace root : %FATFISH_WORKSPACE%
if defined FATFISH_ENV echo Config file    : %FATFISH_ENV%
echo Starting FatFish...
echo.

"%DOTNET_EXE%" run --project src\FatFish.App -c Debug

if errorlevel 1 (
    echo.
    echo [ERROR] app exited with code %ERRORLEVEL%
    pause
)
