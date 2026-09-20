@echo off
chcp 65001 >nul
title 🐟 FatFish Runtime v1.1.1
cd /d "%~dp0"

REM ---- 探测系统语言，设置 FISH_LANG（zh / en）----
REM      兼容性优先：多级探测逐级降级，全失败默认英文。
REM      探测只在 bat 层做，Python 侧不参与。
call "%~dp0fatfish_lang.bat"

REM ============================================================
REM  肥鱼主程序运行窗口（由 fatfish1.1.1.bat 启动）
REM
REM  本窗口负责：
REM    1) 通过 launch.py 启动主程序 FATHFISH.py（拿到真实 PID）；
REM    2) 由 launch.py 用该 PID 拉起监控器 fatfish_watcher.py
REM       （监控器独立黑窗口，实时滚动记录主程序跑了哪些程序）；
REM    3) 主程序结束后，监控器自动进入 30 秒倒计时退出。
REM
REM  本窗口独立运行：主程序结束后不自动关闭，需按任意键退出。
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
REM   (Kept pure ASCII on purpose: a UTF-8 Chinese comment in a .bat can make cmd's
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

REM ---- 换回好看的窗口标题 ----
title 🐟 FatFish Runtime v1.1.1

echo.
if "%FISH_LANG%"=="en" (
    echo  +------------------------------------------------+
    echo  ^|  FatFish Runtime Window                        ^|
    echo  +------------------------------------------------+
    echo   This window is standalone: it stays open after the program ends.
    echo   To exit: after the program ends, press any key to close it.
) else (
    echo  ╭────────────────────────────────────────────────╮
    echo  │  🐟 肥鱼主程序运行窗口 [FatFish Runtime Window]  │
    echo  ╰────────────────────────────────────────────────╯
    echo   本窗口独立运行：主程序结束后不会自动关闭
    echo   [standalone window: stays open after the program ends]
    echo   退出方式：主程序结束后，按任意键关闭本窗口
    echo   [to exit: after the program ends, press any key to close]
)
echo.

REM ---- 前台运行启动枢纽 launch.py（它再拉起主程序 + 监控器）----
python "%~dp0launch.py"
set "FISH_EXIT=%errorlevel%"

REM ---- 主程序结束，清掉 PID 文件 ----
if exist "%~dp0_fatfish_pid.txt" del "%~dp0_fatfish_pid.txt" >nul 2>nul

echo.
if "%FISH_LANG%"=="en" (
    echo [FatFish runtime ended] exit code: %FISH_EXIT%
    echo Press any key to close this window...
) else (
    echo [肥鱼主程序已结束] 退出码 [exit code]：%FISH_EXIT%
    echo [FatFish runtime ended] 按任意键关闭本窗口 [press any key to close this window]...
)
pause >nul
exit /b %FISH_EXIT%
