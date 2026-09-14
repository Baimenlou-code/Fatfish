# -*- coding: utf-8 -*-
"""
make_fatpack.py —— 重新生成「肥鱼一键安装器」（把当前最新文件内嵌进去）

用法 / Usage：
    python make_fatpack.py                 生成 FATPACKI.bat（默认，I 版）
    python make_fatpack.py FATPACK.bat     覆盖生成原版名
    python make_fatpack.py --manifest      只打印将内嵌的文件清单，不生成

原理 / How it works：
    把程序文件 base64 编码后逐行嵌进 .bat 文件末尾，用 ##PYBEGIN## 作分隔标记。
    运行时安装器用 PowerShell 在"自身文件"里 LastIndexOf 这个标记，
    把标记之后的纯文本（一段 Python）抠成一个临时 .py 跑掉，
    那段 Python 再 base64 解码、把内嵌文件"吐"到磁盘上。
    于是安装器本身就是「脚本 + 压缩包」二合一，单文件即可分发。

三个踩过坑的讲究 / Three hard-won rules：
    1. 【bat 外壳必须纯 ASCII】
       cmd.exe 解析批处理时会按"字节偏移"续读文件；文件里含多字节 UTF-8 字符时，
       偏移量会错位，导致它从句中开始读、把半截注释当成命令执行
       （实测症状：'显式指定' is not recognized / '?新获取一份完整的' is not recognized）。
       所以 bat 外壳一律英文 ASCII；中文只出现在【载荷脚本】里 —— 载荷由
       PowerShell 用 -Encoding UTF8 显式读写，完全不经 cmd 解析，因此安全。
    2. 【模板必须用原始字符串 r'''...'''】
       否则 Windows 路径里的 \\f（如 %TEMP%\\fatfish_...）会被 Python 当成换页符
       解释成 0x0C，PowerShell 会报 "Illegal characters in path"。
    3. 【覆盖前先备份】
       已存在的文件会先拷进 _backup/，不会静默吃掉你的旧版本。

历史 / History：
    本生成器曾顺带产出 3 个 .hex「十六进制转写副本」（fatfish1.0.4 / fatfish_runtime /
    fatfish_lang 三个 .bat 的逐字节转写）。实测确认它们【不参与任何流程】——
    全项目无一处读取、安装器也不内嵌它们、信息量 100% 冗余。
    2026-09-14 已连同相关代码一并移除（见 README 4.3 节）。
"""

import os
import sys
import base64
import hashlib

# ============ 配置 ============
# 会被内嵌进安装器的「运行必需文件」
EMBED = [
    "FATGFISH.py",            # 主程序
    "workspace.py",           # 工作台 / 工具
    "file_tools.py",          # 文件·图片读取
    "net_tools.py",           # 联网
    "exec_tools.py",          # 跑命令 / 跑 Python
    "fatfish_watcher.py",     # 监控器
    "launch.py",              # 启动枢纽
    "fatfish1.0.4.bat",       # 启动器（双击这个）
    "fatfish_runtime.bat",    # 运行窗口
    "fatfish_lang.bat",       # 语言探测
    "README.md",              # 说明书
    ".gitignore",             # 防误传名单
]

MARKER = "##PYBEGIN##"
VERSION = "1.0"
EDITION = "I"
DEFAULT_OUT = "FATPACKI.bat"


# ============ 载荷脚本模板（原文，可含中文；走 PowerShell 显式 UTF-8）============
PAYLOAD_TMPL = r'''import sys, os, base64, shutil
from datetime import datetime

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

# 本载荷由安装器从自身文件末尾提取而来；中文输出靠上面的 reconfigure 保障。
PACK_VER = "__VER__"

FILES = {
__ITEMS__
}


def backup(path):
    """覆盖前把旧文件拷进 _backup/。"""
    try:
        os.makedirs("_backup", exist_ok=True)
        ts = datetime.now().strftime("%Y%m%d_%H%M%S_%f")
        dst = os.path.join("_backup", os.path.basename(path) + "." + ts + ".bak")
        shutil.copy2(path, dst)
        return True
    except Exception:
        return False


n_new = n_upd = n_bad = total = 0
print("      [pack] __NAME__ v%s  |  内嵌 %d 个文件" % (PACK_VER, len(FILES)))
for name, b64 in FILES.items():
    try:
        data = base64.b64decode(b64)
    except Exception as e:
        print("      [FAIL] %-24s %s" % (name, e))
        n_bad += 1
        continue
    total += len(data)
    if os.path.exists(name):
        ok = backup(name)
        n_upd += 1
        tag = "更新 updated - old -> _backup/" if ok else "更新 updated - 备份失败 BACKUP FAILED"
    else:
        n_new += 1
        tag = "新增 new"
    with open(name, "wb") as f:
        f.write(data)
    print("      [OK]   %-24s %7d B  %s" % (name, len(data), tag))

print("      [pack] 新增 new=%d / 更新 updated=%d / 失败 failed=%d / 共 %d 字节"
      % (n_new, n_upd, n_bad, total))
if n_bad:
    sys.exit(2)
'''


# ============ bat 外壳模板（必须纯 ASCII！）============
BAT_TMPL = r'''@echo off
chcp 65001 >nul
title FatFish Installer __ED__ v__VER__ ^| __OUT__
cd /d "%~dp0"
setlocal enabledelayedexpansion

set "PACKVER=__VER__"
set "PACKCOUNT=__COUNT__"

echo.
echo ============================================================
echo    FatFish One-Click Installer  -  __ED__ Edition  v%PACKVER%
echo    Embedded: __COUNT__ files / __BYTES__ bytes
echo    Shell is ASCII by design - see make_fatpack.py for why.
echo ============================================================
echo.
echo    Quiet switches - for automation, ignore when double-clicking:
echo      set FATFISH_SKIP_VENV=1   skip virtualenv creation
echo      set FATFISH_NO_GUI=1      do not open Notepad, do not ask to launch
echo.

REM ============================================================
REM  1) Check Python
REM ============================================================
echo [1/7] Checking Python...
where python >nul 2>nul
if errorlevel 1 (
    echo.
    echo   [X] Python not found!
    echo.
    echo   Please install Python 3.8 or newer first:
    echo     1. Open https://www.python.org/downloads/
    echo     2. Download and run the installer
    echo     3. IMPORTANT: tick "Add Python to PATH"
    echo     4. Then run this installer again
    echo.
    pause
    exit /b 1
)
for /f "tokens=2" %%v in ('python --version 2^>^&1') do set PYVER=%%v
echo       [OK] Python %PYVER% found

REM ============================================================
REM  2) Extract embedded program files.
REM     Get-Content gets an explicit -Encoding UTF8 so the payload
REM     is always decoded correctly, regardless of the console code page.
REM ============================================================
echo.
echo [2/7] Extracting embedded files - %PACKCOUNT% of them...
set "EXTRACT_PY=%TEMP%\fatfish_extract_%RANDOM%%RANDOM%.py"
set "MK=##PY"
set "MK=!MK!BEGIN##"
powershell -NoProfile -Command "$c = Get-Content -LiteralPath '%~f0' -Raw -Encoding UTF8; $i = $c.LastIndexOf('%MK%'); if ($i -lt 0) { exit 1 }; $c.Substring($i + __MLEN__) | Set-Content -LiteralPath '%EXTRACT_PY%' -Encoding UTF8"
if errorlevel 1 (
    echo   [X] Failed to extract embedded data.
    echo       The installer is probably damaged or was re-saved by an editor.
    echo       Please get a fresh copy of __OUT__.
    pause
    exit /b 1
)
python "%EXTRACT_PY%"
if errorlevel 1 (
    echo   [X] Failed to write program files.
    del "%EXTRACT_PY%" >nul 2>nul
    pause
    exit /b 1
)
del "%EXTRACT_PY%" >nul 2>nul
if not exist "FATGFISH.py" (
    echo   [X] FATGFISH.py was not extracted - aborting.
    pause
    exit /b 1
)
echo       [OK] Core files are in place

REM ============================================================
REM  3) Virtualenv
REM ============================================================
echo.
echo [3/7] Configuring virtualenv...
if /i "%FATFISH_SKIP_VENV%"=="1" (
    echo       [skip] FATFISH_SKIP_VENV=1
) else if exist "venv\Scripts\activate.bat" (
    echo       [OK] venv already exists - skipped
) else (
    echo       Creating venv - takes about 10-30 seconds the first time...
    python -m venv venv
    if errorlevel 1 (
        echo       [!] venv creation failed - will keep using the global Python
    ) else (
        echo       [OK] venv created
    )
)
if /i not "%FATFISH_SKIP_VENV%"=="1" if exist "venv\Scripts\activate.bat" call "venv\Scripts\activate.bat"

REM ============================================================
REM  4) Dependencies
REM ============================================================
echo.
echo [4/7] Installing dependencies: openai / python-dotenv / requests ...
python -c "import openai, dotenv, requests" >nul 2>nul
if errorlevel 1 (
    echo       Upgrading pip...
    python -m pip install --upgrade pip -q
    echo       Installing from the official index...
    python -m pip install openai python-dotenv requests -q
    if errorlevel 1 (
        echo       [!] Official index failed - retrying via the Tsinghua mirror...
        python -m pip install openai python-dotenv requests -i https://pypi.tuna.tsinghua.edu.cn/simple -q
        if errorlevel 1 (
            echo.
            echo   [X] Dependency install failed. Check your network, or run manually:
            echo       python -m pip install openai python-dotenv requests
            echo.
            pause
            exit /b 1
        )
    )
    echo       [OK] Dependencies installed
) else (
    echo       [OK] Dependencies already satisfied - skipped
)

REM ============================================================
REM  5) .env template
REM ============================================================
echo.
echo [5/7] Checking config file...
if exist ".env" (
    echo       [OK] .env exists - kept as is
) else (
    (
        echo # FatFish config - replace the placeholders with your real API keys
        echo # DeepSeek sign-up: https://platform.deepseek.com/
        echo # Tavily   sign-up: https://tavily.com/
        echo # Note: no spaces around "="; use /reload inside FatFish to hot-reload
        echo DEEPSEEK_API_KEY=sk-put-your-deepseek-key-here
        echo TAVILY_API_KEY=tvly-put-your-tavily-key-here
    ) > ".env"
    echo       [OK] .env template created
    if /i "%FATFISH_NO_GUI%"=="1" (
        echo       [skip] FATFISH_NO_GUI=1 - not opening Notepad
    ) else (
        echo.
        echo   [!] IMPORTANT: fill in your API keys in .env before launching FatFish!
        echo.
        start notepad ".env"
    )
)

REM ============================================================
REM  6) Self-check
REM ============================================================
echo.
echo [6/7] Running self-check...
python -c "import openai, dotenv, requests, net_tools, file_tools, workspace, exec_tools; print('      [OK] all modules imported')"
if errorlevel 1 echo   [!] Self-check failed - see the errors above

REM ============================================================
REM  7) Done - optional launch
REM ============================================================
echo.
echo [7/7] Install finished!
echo ------------------------------------------------------------
echo    Next steps:
echo      1. make sure .env holds your API keys
echo      2. double-click "fatfish1.0.4.bat" to start FatFish
echo ------------------------------------------------------------
echo.

if /i "%FATFISH_NO_GUI%"=="1" (
    echo       [i] Launch prompt skipped - start fatfish1.0.4.bat manually.
) else (
    set "ANS="
    set /p "ANS=     Launch FatFish now? type y to start, Enter to skip : "
    if /i "!ANS!"=="y" (
        echo       [OK] Launching in a separate window...
        start "" cmd /k "%~dp0fatfish1.0.4.bat"
    ) else (
        echo       [i] Not launched. Double-click fatfish1.0.4.bat anytime.
    )
)

echo.
pause
exit /b 0
'''


def build_manifest():
    """返回 [(名字, 字节内容), ...]，跳过不存在的文件。"""
    items = []
    for name in EMBED:
        if not os.path.isfile(name):
            print("  [!] 跳过（不存在）：%s" % name)
            continue
        with open(name, "rb") as f:
            items.append((name, f.read()))
    return items


def generate(out_name):
    items = build_manifest()
    if not items:
        print("没有可内嵌的文件，中止。")
        return 1

    total = sum(len(d) for _, d in items)
    print("=" * 78)
    print("将内嵌 %d 个文件，共 %d 字节" % (len(items), total))
    print("=" * 78)
    print("  %-24s %8s  %s" % ("文件", "字节", "SHA1(前12)"))
    print("  " + "-" * 60)
    for name, data in items:
        print("  %-24s %8d  %s" % (name, len(data), hashlib.sha1(data).hexdigest()[:12]))

    # ---- 拼 FILES 字典（每行一个文件，base64 单行）----
    dict_lines = []
    for name, data in items:
        b64 = base64.b64encode(data).decode("ascii")
        dict_lines.append("    %s: %s," % (repr(name), '"%s"' % b64))
    payload = (PAYLOAD_TMPL
               .replace("__VER__", VERSION)
               .replace("__NAME__", os.path.basename(out_name))
               .replace("__ITEMS__", "\n".join(dict_lines)))

    bat = (BAT_TMPL
           .replace("__VER__", VERSION)
           .replace("__ED__", EDITION)
           .replace("__OUT__", os.path.basename(out_name))
           .replace("__COUNT__", str(len(items)))
           .replace("__BYTES__", str(total))
           .replace("__MLEN__", str(len(MARKER))))

    # ---- 自查：bat 外壳必须纯 ASCII ----
    if not bat.isascii():
        bad = [(i + 1, l) for i, l in enumerate(bat.splitlines()) if not l.isascii()]
        print("\n[FATAL] bat 外壳含非 ASCII 字符，会在 cmd 里引发字节偏移错位：")
        for i, l in bad[:10]:
            print("   第 %d 行：%r" % (i, l))
        return 2

    # ---- 组装：bat 外壳 + 标记 + 载荷 ----
    text = bat + "\n\n\n" + MARKER + "\n" + payload
    text = text.replace("\r\n", "\n").replace("\n", "\r\n")
    raw = text.encode("utf-8")

    with open(out_name, "wb") as f:
        f.write(raw)

    print("  " + "-" * 60)
    print("\n已生成 %s" % out_name)
    print("  总大小        : %d 字节 (%.1f KB)" % (len(raw), len(raw) / 1024))
    print("  外壳(bat)     : %d 字节  %s" % (
        len((bat + "\n\n\n" + MARKER + "\n").encode("utf-8")),
        "纯 ASCII ✅" if bat.isascii() else "含非 ASCII ❌"))
    print("  载荷(py)      : %d 字节 (%.1f KB)" % (
        len(payload.encode("utf-8")), len(payload.encode("utf-8")) / 1024))
    print("  标记          : %s (长度 %d)" % (MARKER, len(MARKER)))
    print("  标记出现次数  : %d（应为 1）" % raw.decode("utf-8").count(MARKER))
    print("  行尾          : CRLF")
    return 0


def main():
    args = list(sys.argv[1:])
    if "--manifest" in args:
        items = build_manifest()
        print("将内嵌 %d 个文件：" % len(items))
        for name, data in items:
            print("  %-24s %8d B" % (name, len(data)))
        return 0
    out = DEFAULT_OUT
    for a in args:
        if not a.startswith("--"):
            out = a
    return generate(out)


if __name__ == "__main__":
    sys.exit(main())
