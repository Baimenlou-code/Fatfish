@echo off
chcp 65001 >nul
REM ============================================================
REM  【转发壳】启动器已更名为 fatfish1.2.1.bat
REM  本文件只为兼容旧快捷方式而保留，双击它会自动转交给新文件。
REM  想彻底清理：确认不再需要旧快捷方式后，直接删掉本文件即可。
REM ============================================================
echo.
echo   [提示] 启动器已更名为 fatfish1.2.1.bat，正在转交...
echo   [Info] Launcher renamed to fatfish1.2.1.bat, forwarding...
echo.
call "%~dp0fatfish1.2.1.bat"
