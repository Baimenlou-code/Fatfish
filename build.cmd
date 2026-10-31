@echo off
rem ============================================================
rem  FatFish C# build script
rem
rem  Why this wrapper exists:
rem    The .NET SDK was installed per-user at
rem        %LOCALAPPDATA%\Microsoft\dotnet
rem    while the system PATH already contains
rem        C:\Program Files\dotnet  (runtime only, NO SDK)
rem    Windows resolves PATH as "system first, user second",
rem    so a bare "dotnet" hits the SDK-less copy and fails with
rem    "No SDKs were found".
rem    This script puts the per-user install at the FRONT of PATH.
rem
rem  Usage:
rem    build.cmd            -> build
rem    build.cmd test       -> build + run unit tests
rem    build.cmd run        -> build + launch WPF app
rem    build.cmd publish    -> publish single-file exe
rem    build.cmd info       -> print environment info
rem
rem  NOTE: keep this file pure ASCII with CRLF line endings.
rem        See README.md "Why ASCII-only .cmd" for details.
rem ============================================================

setlocal

set "DOTNET_HOME=%LOCALAPPDATA%\Microsoft\dotnet"
set "DOTNET_EXE=%DOTNET_HOME%\dotnet.exe"

set "PATH=%DOTNET_HOME%;%PATH%"
set "DOTNET_ROOT=%DOTNET_HOME%"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"

if not exist "%DOTNET_EXE%" (
    echo [ERROR] dotnet.exe not found at: %DOTNET_EXE%
    echo         Run .setup\install.cmd first.
    exit /b 1
)

cd /d "%~dp0"

set "ACTION=%~1"
if "%ACTION%"=="" set "ACTION=build"

if /i "%ACTION%"=="info"    goto :do_info
if /i "%ACTION%"=="build"   goto :do_build
if /i "%ACTION%"=="test"    goto :do_test
if /i "%ACTION%"=="run"     goto :do_run
if /i "%ACTION%"=="publish" goto :do_publish

echo [ERROR] Unknown action: %ACTION%
echo         Usage: build.cmd [info^|build^|test^|run^|publish]
exit /b 1


:do_info
echo ============================================================
echo  Environment
echo ============================================================
echo  dotnet.exe  : %DOTNET_EXE%
echo  DOTNET_ROOT : %DOTNET_ROOT%
echo.
echo --- version ---
"%DOTNET_EXE%" --version
echo.
echo --- SDKs ---
"%DOTNET_EXE%" --list-sdks
echo.
echo --- Runtimes ---
"%DOTNET_EXE%" --list-runtimes
exit /b 0


:do_build
echo ============================================================
echo  Build
echo ============================================================
"%DOTNET_EXE%" build FatFish.slnx -c Debug
exit /b %ERRORLEVEL%


:do_test
echo ============================================================
echo  Build + Test
echo ============================================================
"%DOTNET_EXE%" build FatFish.slnx -c Debug
if errorlevel 1 exit /b 1
echo.
"%DOTNET_EXE%" test FatFish.slnx -c Debug --no-build --logger "console;verbosity=normal"
exit /b %ERRORLEVEL%


:do_run
echo ============================================================
echo  Launch WPF app
echo ============================================================
rem Use the repo-root .env when it exists.
rem Without this, EnvFile.LoadNearest walks upward and stops at
rem workspace\.env, which holds a DIFFERENT (older) API key than
rem the repo-root one. Nothing errors - you just quietly talk to
rem another account, which is exactly the kind of bug that only
rem shows up on the bill.
if exist "%~dp0..\..\.env" set "FATFISH_ENV=%~dp0..\..\.env"
"%DOTNET_EXE%" run --project src\FatFish.App -c Debug
exit /b %ERRORLEVEL%


:do_publish
echo ============================================================
echo  Publish single-file exe
echo ============================================================
"%DOTNET_EXE%" publish src\FatFish.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if errorlevel 1 exit /b 1
echo.
echo ------------------------------------------------------------
echo  Output:
echo    src\FatFish.App\bin\Release\net10.0-windows\win-x64\publish\
echo ------------------------------------------------------------
dir /b "src\FatFish.App\bin\Release\net10.0-windows\win-x64\publish\FatFish.exe" 2>nul
exit /b 0
