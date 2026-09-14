@echo off
chcp 65001 >nul
title 🐟 FatFish Runtime v1.0.4
cd /d "%~dp0"

REM ---- 探测系统语言，设置 FISH_LANG（zh / en）----
REM      兼容性优先：多级探测逐级降级，全失败默认英文。
REM      探测只在 bat 层做，Python 侧不参与。
call "%~dp0fatfish_lang.bat"

REM ============================================================
REM  肥鱼主程序运行窗口（由 fatfish1.0.4.bat 启动）
REM
REM  本窗口负责：
REM    1) 通过 launch.py 启动主程序 FATGFISH.py（拿到真实 PID）；
REM    2) 由 launch.py 用该 PID 拉起监控器 fatfish_watcher.py
REM       （监控器独立黑窗口，实时滚动记录主程序跑了哪些程序）；
REM    3) 主程序结束后，监控器自动进入 30 秒倒计时退出。
REM
REM  本窗口独立运行：主程序结束后不自动关闭，需按任意键退出。
REM ============================================================

REM ---- 给本窗口设一个唯一标题（便于外部识别）----
set "PIDSIG=FATFISH_RUNTIME_PID_%RANDOM%%RANDOM%"
title %PIDSIG%

REM ---- 反查本 cmd 窗口的 PID，写入 PID 文件（供外部工具识别启动器）----
set "SELF_PID="
for /f %%i in ('powershell -NoProfile -Command "(Get-Process | Where-Object { $_.MainWindowTitle -eq '%PIDSIG%' } | Select-Object -First 1).Id"') do set "SELF_PID=%%i"
if defined SELF_PID (
    > "%~dp0_fatfish_pid.txt" echo %SELF_PID%
) else (
    > "%~dp0_fatfish_pid.txt" echo 0
)

REM ---- 换回好看的窗口标题 ----
title 🐟 FatFish Runtime v1.0.4

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
