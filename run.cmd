@echo off
rem ============================================================
rem  Double-click launcher for the FatFish WPF app.
rem  Keeps the console window open so startup errors stay visible.
rem
rem  Workspace root resolution (in order):
rem    1. FATFISH_WORKSPACE already set in the environment
rem    2. two levels above this script  (..\.. = repo root)
rem    3. current directory (app default)
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
    for %%I in ("%~dp0..\..") do set "FATFISH_WORKSPACE=%%~fI"
)

echo Workspace root : %FATFISH_WORKSPACE%
echo Starting FatFish...
echo.

"%DOTNET_EXE%" run --project src\FatFish.App -c Debug

if errorlevel 1 (
    echo.
    echo [ERROR] app exited with code %ERRORLEVEL%
    pause
)
