# -*- coding: utf-8 -*-
"""
make_fatpack.py —— 重新生成「肥鱼一键安装器」（把当前最新文件内嵌进去）

用法 / Usage：
    python make_fatpack.py                 生成 FATPACKII.bat（默认，II 版）
    python make_fatpack.py FATPACKII.bat   同上（显式指定，徽标自动识别为 II）
    python make_fatpack.py --ii            同上（便捷写法）
    python make_fatpack.py --i             生成 FATPACKI.bat（旧 I 版）
    python make_fatpack.py FATPACK.bat     生成原版名
    python make_fatpack.py --edition XX    显式指定版本徽标（默认从输出名推断）
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
    本生成器曾顺带产出 3 个 .hex「十六进制转写副本」（fatfish1.2.1 / fatfish_runtime /
    fatfish_lang 三个 .bat 的逐字节转写）。实测确认它们【不参与任何流程】——
    全项目无一处读取、安装器也不内嵌它们、信息量 100% 冗余。
    2026-09-14 已连同相关代码一并移除（见 README 4.3 节）。

    2026-09-20：新增 FATPACKII 支持 —— 版本徽标改为「由输出文件名推断」
    （见 resolve_edition），于是 FATPACKII.bat 的标题/横幅会自动印 "II Edition"，
    不再依赖手改 EDITION 常量。

    2026-09-20（后续）：默认产物 DEFAULT_OUT 由 FATPACKI.bat 改为 FATPACKII.bat；
    旧的 FATPACK.bat / FATPACKI.bat 已归档进 oldpackmd\\，本目录只保留 FATPACKII.bat。
    想生成旧版名仍可显式指定：--i / --i 或直接传文件名。

    2026-10-02：补齐 EMBED 的**新架构** —— 新增 chat_window.py（对话窗口）、
    stream_core.py（流式内核）与整个 fatfish_core/ 子包（10 个模块，含 roundtime.py），
    并让载荷脚本支持**子目录**（原实现 open(name,"wb") 直写，遇上
    "fatfish_core/policy.py" 会因目录不存在而失败）。
    同时把 local_py_modules() 扩展到「含 __init__.py 的子目录」（包），
    否则审计看不到 fatfish_core 这个包，整个子包漏了也不报警。
    改动前：EMBED 18 项且缺 fatfish_core —— 装完启动必然 ModuleNotFoundError。

    2026-09-20（再后续）：补齐 EMBED —— 新增 ui_core.py / verify_tools.py /
    settings.py / boot_report.py / common.py 共 5 个模块（12 → 17 项）。
    它们是「模块拆分」之后遗留的漏网之鱼（展示层 / 双人核验 / 参数中心 / 开工自检 /
    公共件）：主程序 FATHFISHI.py 第 30、76、1532 行分别裸 import
    verify_tools / ui_core / settings，少任何一个都会在 import 阶段直接
    ModuleNotFoundError —— 旧安装器装完双击启动必然闪退。
    因此同时做了两件配套的事：
      1) 安装器第 6 步自检改为「把 EMBED 里全部 .py 都 import 一遍」。
         原来自检只列了清单内的 net_tools/file_tools/workspace/exec_tools，
         恰好全在清单里 —— 属于「自证清白」，漏包也照样打印 [OK]。
      2) 新增 audit_embed()：打包前自动核对「EMBED 是否已覆盖本地模块依赖」。
         加 --strict 可在审计不通过时中止打包（返回码 3），杜绝同类漏包重演。
"""

import os
import sys
import base64
import hashlib

# ============ 配置 ============
# 会被内嵌进安装器的「运行必需文件」
#
# ⚠️ 维护约定：本清单必须覆盖「主程序启动路径上全部本地模块」。
#    FATHFISHI.py 顶部的裸 import（ui_core / verify_tools）与中段的 import settings
#    一旦缺文件，主程序在 import 阶段就崩，安装器第 6 步的自检也查不出来
#    （旧版自检只列了清单内的模块，属于「自证清白」）。
#    以后新增 / 拆分 .py 模块，请同步：① 本清单；② 下面的 SELFCHECK_MODULES。
#    核对办法：python make_fatpack.py --manifest（会顺带跑一次审计）。
EMBED = [
    # ---- 主程序与核心模块 ----
    "FATHFISHI.py",            # 主程序
    "ui_core.py",             # 展示层（颜色/标记/横幅/等待动画/签名）
    "verify_tools.py",        # 双人核验引擎
    "settings.py",            # 参数中心 /set
    "boot_report.py",         # 开工自检
    "common.py",              # 公共基础件（时间戳/分层目录）
    "workspace.py",           # 工作台 / 工具
    "file_tools.py",          # 文件·图片读取
    "net_tools.py",           # 联网
    "exec_tools.py",          # 跑命令 / 跑 Python
    "fatfish_watcher.py",     # 监控器（独立窗口）
    "status_console.py",      # 状态台（过程信息窗口）
    "chat_window.py",         # ★ 2026-10-02 新增：对话窗口 / 操作台
    "stream_core.py",         # ★ 2026-10-02 新增：流式输出内核
    "launch.py",              # 启动枢纽（拿 PID / 拉监控器与状态台）
    # ---- 功能包 fatfish_core/（10 个，2026-10-02 从主程序拆出）----
    "fatfish_core/__init__.py",
    "fatfish_core/envutil.py",    # 环境变量清洗 / 类型转换
    "fatfish_core/cmdcap.py",     # 命令输出捕获
    "fatfish_core/msgs.py",       # 消息分组 / 裁剪 / token 估算
    "fatfish_core/policy.py",     # 报批判定 + 只读 Python 静态判定
    "fatfish_core/qqmode.py",     # QQ 前置模式
    "fatfish_core/streamhk.py",   # 流式胶水层
    "fatfish_core/setappl.py",    # /set 各 applier
    "fatfish_core/uicolors.py",   # 颜色单一真源
    "fatfish_core/roundtime.py",  # ★ 轮次计时（三处落款共用）
    # ★ 2026-10-03 P1 新增（编辑器组布局）——
    #   这两个模块**必须在清单里**：chat_window.py 是 try/except 导入它们的
    #   （取不到就用降级桩），所以缺文件不会崩，但会**静默降级**成
    #   "不能分屏 / 不能持久化"的阉割界面 —— 比崩掉更难发现。
    "fatfish_core/layout.py",     # 编辑器组布局数据模型（纯数据）
    "fatfish_core/exectail.py",   # exec 子程序输出 tailer
    # ---- 启动脚本 ----
    "fatfish1.2.2.bat",       # 启动器（双击这个）
    "fatfish_runtime.bat",    # 运行窗口
    "fatfish_lang.bat",       # 语言探测
    # ---- 小唐话词库（signatures/，2026-10-03）----
    #   个性签名的句子**不写在代码里**，放这些 txt，一行一句。
    #   用户可以自由增删改；ui_core.load_signatures() 会读它们（带 mtime 热重载）。
    "signatures/01_classic.txt",    # 经典：咸鱼自嘲 / 文艺 / 打工人
    "signatures/02_fish.txt",       # 鱼味特调
    "signatures/03_tech.txt",       # 技术味特调
    "signatures/04_real_logs.txt",  # 唐事录（真实翻车）
    "signatures/05_rejected.txt",   # 打回记录（被审查员拦下的案例）
    "signatures/README.md",         # 词库用法说明
    # ---- 文档与配置 ----
    "README.md",              # 说明书
    ".gitignore",             # 防误传名单
]

# 安装器第 6 步「自检」要 import 的本地模块（必须与 EMBED 里的 .py 严格一致！）
# 旧版只列了 net_tools / file_tools / workspace / exec_tools —— 恰好全在清单内，
# 于是漏包 ui_core / verify_tools / settings 也照样打印 [OK]，属于「自证清白」。
# 现在改为动态生成，并纳入 audit_embed() 的硬性校验。
SELFCHECK_MODULES = [
    "common", "ui_core", "settings", "boot_report", "verify_tools",
    "net_tools", "file_tools", "workspace", "exec_tools",
    "status_console",         # 状态台：独立脚本，import 仅验证语法/依赖完整
    "chat_window",            # 对话窗口（2026-10-02 新增）
    "stream_core",            # 流式内核（2026-10-02 新增）
    "fatfish_core",           # 功能包（2026-10-02 拆出；包名，查 __init__.py）
    "fatfish_core.layout",    # ★ 2026-10-03：编辑器组布局（缺失 → 界面静默降级）
    "fatfish_core.exectail",  # ★ 2026-10-03：exec 输出 tailer
]

MARKER = "##PYBEGIN##"
VERSION = "1.2.2"
EDITION = "I"                      # 兜底徽标：输出名无法识别时使用
DEFAULT_OUT = "FATPACKII.bat"      # 默认产物（2026-09-20 起由 I 版改为 II 版）

# 版本徽标由【输出文件名】推断（见 resolve_edition），于是：
#   FATPACKI.bat   → "I  Edition"
#   FATPACKII.bat  → "II Edition"
#   FATPACK.bat    → 兜底常量 EDITION（原版名，保持与 I 版一致的版式）
# 这样文件名叫什么、横幅就印什么，不会出现"名字 II、横幅写 I"的错位。
EDITION_ROMANS = ("I", "II", "III", "IV")   # 可识别的版本徽标（白名单）


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
    # ★ 2026-10-02：支持子目录（如 fatfish_core/policy.py）—— 先建目录再写。
    _d = os.path.dirname(name)
    if _d:
        try:
            os.makedirs(_d, exist_ok=True)
        except OSError as e:
            print("      [FAIL] %-24s 无法建立目录 %s：%s" % (name, _d, e))
            n_bad += 1
            continue
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
if not exist "FATHFISHI.py" (
    echo   [X] FATHFISHI.py was not extracted - aborting.
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
        echo # ============================================================
        echo #  FatFish .env - fill in your keys, then type /reload
        echo #  Chinese reference: README.md section 18.6
        echo #  Rule: never put a trailing comment on an EMPTY value line.
        echo #  ASCII only. Avoid parentheses and percent signs on every line.
        echo # ============================================================
        echo.
        echo # ---- main model, the executor ----
        echo FATFISH_API_KEY=
        echo FATFISH_BASE_URL=https://api.deepseek.com
        echo FATFISH_MODEL=deepseek-flash
        echo.
        echo # fallback when FATFISH_* is empty: DEEPSEEK_API_KEY / DEEPSEEK_MODEL
        echo # example for other providers:
        echo # FATFISH_API_KEY=sk-xxxxxxxxxxxxxxxx
        echo # FATFISH_BASE_URL=https://api.moonshot.cn/v1
        echo # FATFISH_MODEL=moonshot-v1-8k
        echo.
        echo TAVILY_API_KEY=
        echo.
        echo # ---- dual-AI verification, the reviewer ----
        echo VERIFIER_API_KEY=
        echo VERIFIER_BASE_URL=https://api.deepseek.com
        echo VERIFIER_MODEL=deepseek-flash
        echo.
        echo # off / auto / all  - default: auto
        echo VERIFY_MODE=auto
        echo # 1=block 0=allow when retries run out
        echo VERIFY_STRICT=1
        echo # max revise-returns per turn
        echo VERIFY_MAX_RETRIES=4
        echo # free supplement rounds, not counted as returns
        echo VERIFY_MAX_SUPPLEMENTS=4
        echo # also review the final answer: 1=yes 0=no
        echo VERIFY_FINAL_ANSWER=0
        echo # reviewer unreachable: open=allow+warn closed=block
        echo VERIFY_FAIL_MODE=open
        echo # mirror reviewer comments to watcher: 1=on 0=off
        echo VERIFY_MIRROR=1
        echo.
        echo # ---- prompt size limits, in chars ----
        echo # builtin defaults: total 64000 / plan 16000 / action 4000 / replace 2000
        echo # goal 6000 / context 24000 / answer 24000 / answer_ctx 12000
        echo # max_tokens 2000 / timeout 120s
        echo # VERIFIER_PROMPT_CHARS=64000
        echo # VERIFIER_PLAN_CHARS=16000
        echo # VERIFIER_ACTION_PREVIEW=4000
        echo # VERIFIER_REPLACE_PREVIEW=2000
        echo # VERIFIER_GOAL_CHARS=6000
        echo # VERIFIER_CONTEXT_CHARS=24000
        echo # VERIFIER_ANSWER_CHARS=24000
        echo # VERIFIER_ANSWER_CTX=12000
        echo # VERIFIER_MAX_TOKENS=2000
        echo # VERIFIER_TIMEOUT=120
        echo.
        echo # ---- manual approval ----
        echo # 1=ask before running commands / python too, 0=no
        echo APPROVE_RUN_TOOLS=1
        echo.
        echo # ---- networking ----
        echo # on / off / auto / ai  - recommended: ai
        echo NET_MODE=ai
        echo # auto / search / extract / both
        echo TAVILY_MODE=auto
        echo VERIFIER_MAX_TOKENS=10000
    ) > ".env"
    echo       [OK] .env created - full skeleton, no keys inside
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
python -c "import openai, dotenv, requests, __SELFCHECK__; print('      [OK] all modules imported')"
if errorlevel 1 echo   [!] Self-check failed - see the errors above

REM ============================================================
REM  7) Done - optional launch
REM ============================================================
echo.
echo [7/7] Install finished!
echo ------------------------------------------------------------
echo    Next steps:
echo      1. make sure .env holds your API keys
echo      2. double-click "fatfish1.2.2.bat" to start FatFish
echo ------------------------------------------------------------
echo.

if /i "%FATFISH_NO_GUI%"=="1" (
    echo       [i] Launch prompt skipped - start fatfish1.2.2.bat manually.
) else (
    set "ANS="
    set /p "ANS=     Launch FatFish now? type y to start, Enter to skip : "
    if /i "!ANS!"=="y" (
        echo       [OK] Launching in a separate window...
        start "" cmd /k "%~dp0fatfish1.2.2.bat"
    ) else (
        echo       [i] Not launched. Double-click fatfish1.2.2.bat anytime.
    )
)

echo.
pause
exit /b 0
'''


def resolve_edition(out_name, explicit=None):
    """决定安装器版本徽标（I / II / III / IV）。

    优先级：显式参数 explicit > 输出文件名里的罗马数字 > 常量 EDITION 兜底。
    于是 `python make_fatpack.py FATPACKII.bat` 直接得到 "II Edition"，
    无需另加开关，也不会出现"文件名 II、横幅却写 I"的错位。
    """
    if explicit:
        return explicit
    stem = os.path.basename(out_name).upper()
    if stem.startswith("FATPACK") and stem.endswith(".BAT"):
        tail = stem[len("FATPACK"):-len(".BAT")]
        if tail in EDITION_ROMANS:
            return tail
    return EDITION


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


def local_py_modules():
    """本地可 import 的模块/包名集合。

    2026-10-02 扩展：除根目录 *.py 之外，还纳入**含 __init__.py 的子目录**
    （即包，例如 fatfish_core）。主程序里写 `from fatfish_core.policy import ...`，
    按 a.name/module.split(".")[0] 解析出来的就是包名 `fatfish_core`，
    所以这里必须以包名入集合，否则审计会漏掉整个子包。
    """
    names = set()
    try:
        for f in os.listdir("."):
            if f.endswith(".py"):
                names.add(f[:-3])
            elif os.path.isdir(f) and os.path.isfile(os.path.join(f, "__init__.py")):
                names.add(f)
    except Exception:
        pass
    return names


def _in_embed_module(mod):
    """mod 是模块名 / 包名，**可含点**（如 fatfish_core.layout）；判断 EMBED 是否覆盖它。"""
    m = str(mod).replace(".", "/")
    return (("%s.py" % m) in EMBED) or (("%s/__init__.py" % m) in EMBED)


def audit_embed():
    """审计 EMBED 是否覆盖了本地模块依赖 —— 防止「装完跑不起来」。

    检查两条：
      1) SELFCHECK_MODULES ⊂ EMBED：否则安装器第 6 步的自检是「自证清白」，
         漏包也能打印 [OK]（2026-09-20 那次漏包的根因之一）。
      2) 逐个解析 EMBED 里的 .py，凡是 import 了「当前目录中确实存在的本地模块」
         却没进 EMBED 的，全部列出 —— 这正是 ui_core / verify_tools / settings
         被漏掉时的形态。

    审计自身全程不抛异常：任何解析失败都只记一条提示，绝不阻断打包。
    返回问题列表（空列表 = 通过）。
    """
    problems = []
    try:
        import ast
    except Exception as e:                    # 理论上不会发生
        return ["无法导入 ast，审计跳过：%s" % e]

    local = local_py_modules()
    emb = set(EMBED)

    # ① 自检模块必须都在清单里
    for mod in SELFCHECK_MODULES:
        if not _in_embed_module(mod):
            problems.append("SELFCHECK_MODULES 含 %s，但清单里没有 %s（自检会假绿）"
                            % (mod, mod))

    # ② 清单内 .py 的本地模块依赖必须都在清单里
    for name in EMBED:
        if not name.endswith(".py") or not os.path.isfile(name):
            continue
        try:
            with open(name, "r", encoding="utf-8", errors="replace") as fh:
                tree = ast.parse(fh.read())
        except Exception as e:
            problems.append("解析 %s 失败（该文件审计跳过）：%s" % (name, e))
            continue
        need = set()
        need_full = set()
        for node in ast.walk(tree):
            if isinstance(node, ast.Import):
                for a in node.names:
                    need.add(a.name.split(".")[0])
                    need_full.add(a.name)
            elif isinstance(node, ast.ImportFrom):
                if node.module and node.level == 0:
                    need.add(node.module.split(".")[0])
                    need_full.add(node.module)
        for mod in sorted(need & local):
            if not _in_embed_module(mod):
                problems.append("%s 依赖本地模块 %s，但它不在 EMBED 中" % (name, mod))

        # ③ ★ 2026-10-03 补：**包内子模块**单独核对。
        #   只查顶层名有个盲区：`from fatfish_core.layout import ...` 解析出来的
        #   顶层名是 `fatfish_core`，而 `fatfish_core/__init__.py` 在清单里 ——
        #   于是"整包已覆盖"，包内新增的模块就**永远查不出来**了。
        #   （真实案例：layout.py / exectail.py 加进包后，审计照样报"通过"。）
        for full in sorted(need_full):
            if "." not in full:
                continue
            rel = full.replace(".", "/") + ".py"
            if os.path.isfile(rel) and rel not in emb:
                problems.append("%s 依赖本地模块 %s，但它不在 EMBED 中" % (name, rel))

    return sorted(set(problems))


def _print_audit(problems):
    if problems:
        print("\n" + "!" * 78)
        print("[!] EMBED 审计发现问题（安装后可能直接跑不起来）：")
        for p in problems:
            print("    - " + p)
        print("!" * 78)
    else:
        print("\n[i] EMBED 审计通过：清单已覆盖全部本地模块依赖。")


def generate(out_name, edition=None, strict=False):
    ed = resolve_edition(out_name, edition)

    # ---- 先审计：漏包是最贵的 bug，宁可在打包时就吵起来 ----
    audit_problems = audit_embed()
    _print_audit(audit_problems)
    if audit_problems and strict:
        print("\n[FATAL] 审计未通过（--strict）：已中止，未写出 %s。" % out_name)
        return 3

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
           .replace("__ED__", ed)
           .replace("__OUT__", os.path.basename(out_name))
           .replace("__COUNT__", str(len(items)))
           .replace("__BYTES__", str(total))
           .replace("__SELFCHECK__", ", ".join(SELFCHECK_MODULES))
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
    print("\n已生成 %s（%s Edition）" % (out_name, ed))
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
    strict = "--strict" in args
    if "--manifest" in args:
        items = build_manifest()
        print("将内嵌 %d 个文件：" % len(items))
        for name, data in items:
            print("  %-24s %8d B" % (name, len(data)))
        _print_audit(audit_embed())
        return 0
    out = DEFAULT_OUT
    explicit_ed = None
    i = 0
    while i < len(args):
        a = args[i]
        if a == "--edition" and i + 1 < len(args):
            explicit_ed = args[i + 1]
            i += 2
            continue
        if a.startswith("--edition="):
            explicit_ed = a.split("=", 1)[1]
        elif a in ("--i", "--ii", "--iii"):        # 便捷预设
            out = "FATPACK%s.bat" % a[2:].upper()
        elif not a.startswith("--"):               # 输出名（--strict 等开关不算）
            out = a
        i += 1
    return generate(out, edition=explicit_ed, strict=strict)


if __name__ == "__main__":
    sys.exit(main())
