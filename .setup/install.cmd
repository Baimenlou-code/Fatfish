@echo off
rem ============================================================
rem  Install .NET 10 SDK (per-user, no admin rights needed)
rem
rem  Target dir : %LOCALAPPDATA%\Microsoft\dotnet
rem  Uninstall  : delete that directory. No system files touched.
rem
rem  NOTE: keep this file pure ASCII with CRLF line endings.
rem ============================================================

setlocal
set "SDK_VERSION=10.0.403"
set "INSTALL_DIR=%LOCALAPPDATA%\Microsoft\dotnet"
set "SCRIPT=%~dp0dotnet-install.ps1"

echo ============================================================
echo  Installing .NET SDK %SDK_VERSION%
echo  Target: %INSTALL_DIR%
echo ============================================================
echo.

if not exist "%SCRIPT%" (
    echo [ERROR] dotnet-install.ps1 not found at: %SCRIPT%
    exit /b 1
)

if exist "%INSTALL_DIR%\dotnet.exe" echo [INFO] existing dotnet.exe found, will overwrite same version

powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" -Version %SDK_VERSION% -Channel 10.0 -InstallDir "%INSTALL_DIR%" -NoPath

if errorlevel 1 (
    echo.
    echo [FAILED] installation did not complete
    exit /b 1
)

echo.
echo ============================================================
echo  Verify
echo ============================================================
"%INSTALL_DIR%\dotnet.exe" --version
echo.
echo --- SDKs ---
"%INSTALL_DIR%\dotnet.exe" --list-sdks
echo.
echo Next: run build.cmd
exit /b 0
