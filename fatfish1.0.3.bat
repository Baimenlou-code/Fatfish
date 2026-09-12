@echo off
chcp 65001 >nul
title 肥鱼启动器 v1.0.3
cd /d "%~dp0"

REM ============================================================
REM  1) 检查 Python
REM ============================================================
where python >nul 2>nul
if errorlevel 1 (
    echo [错误] 未检测到 python 命令，请先安装 Python 并加入 PATH
    pause
    exit /b 1
)

REM ============================================================
REM  2) 激活虚拟环境（如果存在）
REM ============================================================
if exist "venv\Scripts\activate.bat" (
    call "venv\Scripts\activate.bat"
    echo [信息] 已激活虚拟环境 venv
) else if exist ".venv\Scripts\activate.bat" (
    call ".venv\Scripts\activate.bat"
    echo [信息] 已激活虚拟环境 .venv
) else (
    echo [信息] 未发现虚拟环境，使用全局 Python
)

REM ============================================================
REM  3) 检查依赖，缺则自动安装
REM ============================================================
python -c "import openai, dotenv, requests" >nul 2>nul
if errorlevel 1 (
    echo [提示] 缺少依赖，正在安装...
    python -m pip install --upgrade pip
    python -m pip install openai python-dotenv requests
    if errorlevel 1 (
        echo [错误] 依赖安装失败，请手动执行：
        echo        python -m pip install openai python-dotenv requests
        pause
        exit /b 1
    )
    echo [信息] 依赖安装完成
) else (
    echo [信息] 依赖检查通过
)

REM ============================================================
REM  4) 检查 .env
REM ============================================================
if not exist ".env" (
    echo.
    echo [警告] 未找到 .env 文件
    echo        请在本目录创建 .env 并写入：
    echo            DEEPSEEK_API_KEY=你的key
    echo            TAVILY_API_KEY=你的key
    echo.
    pause
)

REM ============================================================
REM  5) 启动
REM ============================================================
echo.
echo 🐟 正在启动肥鱼...
echo.
python "FATFFISHI.py"

REM ============================================================
REM  6) 退出保留窗口
REM ============================================================
echo.
echo [肥鱼已退出] 按任意键关闭窗口...
pause >nul
