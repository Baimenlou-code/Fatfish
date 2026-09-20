@echo off
chcp 65001 >nul
title FatFish Runtime v1.1.1
cd /d "%~dp0"

REM ---- Probe the system language and set FISH_LANG (zh / en) ----
REM      Compatibility first: a multi-level probe degrades gracefully and
REM      defaults to English when every level fails.
REM      The probe happens only in the batch layer; Python is not involved.
call "%~dp0fatfish_lang.bat"

REM ============================================================
REM  FatFish runtime window (started by fatfish1.1.1.bat)
REM
REM  This window is responsible for:
REM    1) starting the main program FATENFISH.py via launch.py (capturing its
REM       real PID);
REM    2) launch.py using that PID to bring up the watcher fatfish_watcher.py
REM       (its own console window, live-scrolling which programs were run);
REM    3) the watcher counting down 30 seconds and exiting after the main
REM       program ends.
REM
REM  This window is standalone: it does not close itself when the program ends;
REM  press any key to exit.
REM ============================================================

REM ---- Get this window's (runtime cmd) real PID, then write _fatfish_pid.txt ----
REM
REM   Purpose: let external tools identify / close the FatFish runtime window.
REM   Method : redirect trick -- ask PowerShell for its OWN parent process PID.
REM     * No window-title lookup: under Windows Terminal (ConPTY) cmd.exe has no
REM       top-level window; MainWindowTitle belongs to WindowsTerminal.exe, so the
REM       lookup may return the terminal host PID (seen in old run logs).
REM     * No for /f: for /f may execute the command via a temporary cmd /c, which
REM       yields that middle cmd's PID instead of this window's. The redirect form
REM       is launched directly by this cmd, so parent == this window (verified
REM       against the real PID in an isolated test).
REM
REM   Fallback chain: Get-CimInstance -> Get-WmiObject -> window title -> 0
REM   PID semantics: this file stores the RUNTIME WINDOW PID (not the python PID);
REM   rewritten on every start, deleted when the program exits.
REM   (Kept pure ASCII on purpose: a non-ASCII comment in a .bat can make cmd's
REM    byte-offset parsing drift and mis-execute comment text.)

if exist "%~dp0_fatfish_pid.txt" del "%~dp0_fatfish_pid.txt" >nul 2>nul

set "SELF_PID="
set "FISH_PIDTMP=%TEMP%\fatfish_selfpid_%RANDOM%%RANDOM%.txt"
if not defined TEMP set "FISH_PIDTMP=%~dp0_fatfish_selfpid_tmp.txt"

REM  (1) preferred: Get-CimInstance (Windows 8+ / PowerShell 3+)
powershell -NoProfile -ExecutionPolicy Bypass -Command "$p=Get-CimInstance Win32_Process -Filter ('ProcessId='+$PID); if($p){[int]$p.ParentProcessId}" > "%FISH_PIDTMP%" 2>nul
if exist "%FISH_PIDTMP%" for /f "usebackq tokens=* delims=" %%i in ("%FISH_PIDTMP%") do set "SELF_PID=%%i"

REM  (2) fallback : Get-WmiObject (older systems)
if not defined SELF_PID powershell -NoProfile -ExecutionPolicy Bypass -Command "$p=Get-WmiObject Win32_Process -Filter ('ProcessId='+$PID); if($p){[int]$p.ParentProcessId}" > "%FISH_PIDTMP%" 2>nul
if not defined SELF_PID if exist "%FISH_PIDTMP%" for /f "usebackq tokens=* delims=" %%i in ("%FISH_PIDTMP%") do set "SELF_PID=%%i"

REM  (3) last resort: window-title lookup (traditional conhost only)
if not defined SELF_PID set "PIDSIG=FATFISH_RUNTIME_PID_%RANDOM%%RANDOM%"
if not defined SELF_PID title %PIDSIG%
if not defined SELF_PID for /f %%i in ('powershell -NoProfile -Command "(Get-Process | Where-Object { $_.MainWindowTitle -eq '%PIDSIG%' } | Select-Object -First 1).Id"') do set "SELF_PID=%%i"

if exist "%FISH_PIDTMP%" del "%FISH_PIDTMP%" >nul 2>nul

REM  Accept digits only; otherwise write 0 (= no valid marker)
echo %SELF_PID%| findstr /r "^[0-9][0-9]*$" >nul 2>nul
if errorlevel 1 set "SELF_PID=0"

> "%~dp0_fatfish_pid.txt" echo %SELF_PID%

REM ---- restore the friendly window title ----
title FatFish Runtime v1.1.1

echo.
echo  +------------------------------------------------+
echo  ^|  FatFish Runtime Window                        ^|
echo  +------------------------------------------------+
echo   This window is standalone: it stays open after the program ends.
echo   To exit: after the program ends, press any key to close it.
echo.

REM ---- run the launch hub launch.py in the foreground (it starts main + watcher) ----
python "%~dp0launch.py"
set "FISH_EXIT=%errorlevel%"

REM ---- main program finished, remove the PID marker ----
if exist "%~dp0_fatfish_pid.txt" del "%~dp0_fatfish_pid.txt" >nul 2>nul

echo.
echo [FatFish runtime ended] exit code: %FISH_EXIT%
echo Press any key to close this window...
pause >nul
exit /b %FISH_EXIT%
