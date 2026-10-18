# -*- coding: utf-8 -*-
"""system_facts.py —— 机器环境事实层（2026-10-19 新增）

为什么需要它
------------
在此之前，"我跑在什么机器上"这件事对三方都是模糊的：

    执行者 AI   → 只能靠猜（经常把 PowerShell 语法写成 cmd 命令）
    审查员 AI   → 只被告知"检查命令是否跑得通"，却没被告知**目标 shell 是什么**
    用户        → 启动自检只显示 PID / 时间 / 工作台，看不到系统本身

结果就是：命令在 cmd.exe 里跑，人却在写 PowerShell；审核员也没法据此判错。

本模块把「机器环境」变成**一份可复用的结构化事实**，
三处共用同一个来源（单一事实来源），不各写一套：

    boot_report  → 渲染成自检面板 + 注入系统提示词
    verify_tools → 拼进审查员 prompt，让它按正确的方言核对命令
    exec_tools   → 命令执行前做方言预检，把低级错误提前拦下

设计约束
--------
· 零第三方依赖（只用标准库）
· 只读、不联网、不写文件
· 任何一步失败都降级，绝不抛异常（`collect()` 统一兜底）
· 结果按进程缓存（同一进程内环境不会变），可用 force=True 重采

用法
----
    import system_facts as sf
    f = sf.collect()                 # → dict
    print(sf.render_panel(f))        # 人看的
    print(sf.render_prompt(f))       # 给 AI 看的（精简、可直接注入）
    print(sf.shell_rules(f))         # 该 shell 的「能写 / 不能写」清单
    sf.check_command("Get-ChildItem")  # → [(级别, 说明), ...] 方言预检
"""

from __future__ import absolute_import

import os
import platform
import sys

__all__ = [
    "collect", "render_panel", "render_prompt", "shell_rules",
    "check_command", "shell_name", "os_family", "summary_line",
]

_CACHE = {"data": None}


# --------------------------------------------------------------------------- #
# 基础判定
# --------------------------------------------------------------------------- #
def os_family():
    """操作系统大类：windows / linux / darwin / other。"""
    try:
        if sys.platform.startswith("win"):
            return "windows"
        if sys.platform.startswith("linux"):
            return "linux"
        if sys.platform == "darwin":
            return "darwin"
        # 兜底：再看 platform.system()
        s = (platform.system() or "").lower()
        if "windows" in s:
            return "windows"
        if "linux" in s:
            return "linux"
        if "darwin" in s or "mac" in s:
            return "darwin"
    except Exception:
        pass
    return "other"


def shell_name():
    """**本程序执行命令时真正用的 shell**（不是用户终端里那个）。

    ★ 这一点极其重要，也是最容易搞错的地方：
      exec_tools.run_cmd 在 Windows 上走的是 `cmd /c`，不是 PowerShell。
      所以在这里，PowerShell 的写法（$env:X、Get-ChildItem、&&）一律不合法。
    """
    fam = os_family()
    if fam == "windows":
        return "cmd.exe"
    return "/bin/sh"


def _windows_version():
    """Windows 版本人话（10 / 11 / Server），失败返回 release 号。"""
    try:
        rel = platform.release() or ""
        ver = platform.version() or ""
        # Windows 11 的 build 号 >= 22000
        build = 0
        try:
            build = int(str(ver).split(".")[-1])
        except Exception:
            build = 0
        if "Server" in (platform.platform() or ""):
            return "Server %s" % rel
        if build >= 22000:
            return "11 (build %d)" % build
        if rel == "10":
            return "10 (build %d)" % build if build else "10"
        return rel or "?"
    except Exception:
        return "?"


def _linux_distro():
    """读 /etc/os-release 取发行版名。"""
    try:
        path = "/etc/os-release"
        if os.path.isfile(path):
            info = {}
            with open(path, "r", encoding="utf-8", errors="replace") as f:
                for line in f:
                    line = line.strip()
                    if "=" in line and not line.startswith("#"):
                        k, v = line.split("=", 1)
                        info[k.strip()] = v.strip().strip('"').strip("'")
            name = info.get("PRETTY_NAME") or info.get("NAME") or ""
            if name:
                return name
    except Exception:
        pass
    return platform.release() or "?"


def _locale_encoding():
    """当前进程的标准输出编码 / 首选编码。"""
    out = {}
    try:
        out["stdout"] = (getattr(sys.stdout, "encoding", "") or "").lower() or "?"
    except Exception:
        out["stdout"] = "?"
    try:
        out["preferred"] = (__import__("locale").getpreferredencoding(False) or "").lower()
    except Exception:
        out["preferred"] = "?"
    try:
        out["fs"] = (sys.getfilesystemencoding() or "").lower()
    except Exception:
        out["fs"] = "?"
    return out


def _is_admin_like():
    """是否管理员 / root（只报事实，不做任何提权）。"""
    try:
        if os_family() == "windows":
            import ctypes
            return bool(ctypes.windll.shell32.IsUserAnAdmin())
        return os.geteuid() == 0
    except Exception:
        return None


# --------------------------------------------------------------------------- #
# 采集
# --------------------------------------------------------------------------- #
def collect(force=False):
    """采集机器环境事实。带进程级缓存；force=True 重采。"""
    if _CACHE["data"] is not None and not force:
        return _CACHE["data"]

    fam = os_family()
    info = {
        "ok": True,
        "errors": [],
        "os_family": fam,
        "os_label": "?",
        "arch": "?",
        "machine": "?",
        "python": "?",
        "python_impl": "?",
        "shell": shell_name(),
        "venv": "",
        "encoding": {},
        "admin": None,
        "path_sep": os.sep,
        "drive": "",
        "cwd": "",
    }

    try:
        if fam == "windows":
            info["os_label"] = "Windows %s" % _windows_version()
        elif fam == "linux":
            info["os_label"] = "Linux · %s" % _linux_distro()
        elif fam == "darwin":
            info["os_label"] = "macOS %s" % (platform.mac_ver()[0] or platform.release())
        else:
            info["os_label"] = platform.system() or "?"
    except Exception as e:
        info["errors"].append("系统版本采集失败：%s" % e)

    try:
        info["arch"] = platform.machine() or "?"
        bits = platform.architecture()[0] if hasattr(platform, "architecture") else ""
        info["machine"] = "%s %s" % (info["arch"], bits) if bits else info["arch"]
    except Exception:
        pass

    try:
        info["python"] = "%s %s" % (platform.python_implementation(),
                                    platform.python_version())
        info["python_impl"] = platform.python_implementation()
    except Exception:
        pass

    try:
        info["encoding"] = _locale_encoding()
    except Exception:
        pass

    try:
        info["admin"] = _is_admin_like()
    except Exception:
        pass

    # 虚拟环境（看 sys.prefix 与 base_prefix 是否不同，最可靠）
    try:
        if getattr(sys, "base_prefix", sys.prefix) != sys.prefix:
            info["venv"] = sys.prefix
        elif os.environ.get("VIRTUAL_ENV"):
            info["venv"] = os.environ["VIRTUAL_ENV"]
    except Exception:
        pass

    try:
        info["cwd"] = os.getcwd()
        drive, _tail = os.path.splitdrive(info["cwd"])
        info["drive"] = drive or ""
    except Exception:
        pass

    _CACHE["data"] = info
    return info


# --------------------------------------------------------------------------- #
# 该 shell 的「能写 / 不能写」清单
# --------------------------------------------------------------------------- #
def shell_rules(facts=None):
    """返回当前 shell 的方言规则，供 AI 与审查员共用。

    结构：
        {"shell": str,
         "allowed": [(写法, 说明), ...],      # 这个 shell 支持的
         "forbidden": [(写法, 正确写法, 说明), ...]}  # 这个 shell 会报错的
    """
    f = facts or collect()
    sh = f.get("shell") or shell_name()

    if sh == "cmd.exe":
        return {
            "shell": "cmd.exe（Windows 命令提示符，非 PowerShell）",
            "allowed": [
                ("dir", "列目录"),
                ("type 文件", "看文件内容"),
                ("copy / del / move / ren", "文件操作"),
                ("set NAME=value", "设临时环境变量"),
                ("set NAME", "看环境变量"),
                ("%VAR%", "引用环境变量"),
                ("cmd & cmd", "顺序执行（& 不管前面成败）"),
                ("cmd && cmd", "顺序执行（&& 前面成功才继续）"),
                ("cmd || cmd", "前面失败才执行"),
                ("findstr", "文本过滤（不是 grep）"),
                ("where 程序名", "查程序位置（不是 which）"),
                ("tasklist / taskkill", "进程列表 / 结束进程"),
                ("2>&1", "重定向 stderr 到 stdout"),
            ],
            "forbidden": [
                ("$env:NAME", "set NAME=... 或 %NAME%", "PowerShell 变量语法，cmd 不认"),
                ("$NAME", "set NAME=... 或 %NAME%", "shell 变量语法，cmd 不认"),
                ("Get-ChildItem / ls 别名的 PowerShell 用法", "dir", "PowerShell cmdlet"),
                ("Get-Content", "type 文件", "PowerShell cmdlet"),
                ("Set-Location", "cd /d 路径", "PowerShell cmdlet"),
                ("Remove-Item", "del / rmdir", "PowerShell cmdlet"),
                ("Invoke-WebRequest / curl -o 的 PowerShell 习惯", "certutil -urlcache 或 python", "cmd 里没有 curl 别名"),
                ("ps1 脚本路径直接执行", "powershell -File x.ps1", "cmd 不能直接跑 .ps1"),
                ("export NAME=value", "set NAME=value", "bash 语法，cmd 不认"),
                ("echo $NAME", "echo %NAME%", "bash 变量语法"),
                ("grep / awk / sed", "findstr（或改用 Python）", "cmd 没有这些工具"),
                ("which 程序", "where 程序", "cmd 用 where"),
                ("chmod / ls -la", "dir / icacls", "Unix 命令"),
                ("单引号包裹参数 'x'", "双引号 \"x\"", "cmd 不把单引号当引号，会连同引号一起传进去"),
            ],
        }

    if sh == "/bin/sh":
        return {
            "shell": "/bin/sh（POSIX shell，非 bash 专属特性）",
            "allowed": [
                ("ls / cat / cp / rm / mv", "基本文件操作"),
                ("export NAME=value", "设环境变量"),
                ("$NAME", "引用变量"),
                ("cmd && cmd", "顺序执行"),
                ("grep / awk / sed", "文本处理"),
                ("which / command -v", "查程序位置"),
            ],
            "forbidden": [
                ("dir / copy / del / type", "ls / cp / rm / cat", "Windows cmd 命令"),
                ("%VAR%", "$VAR", "cmd 变量语法"),
                ("set NAME=value", "export NAME=value", "cmd 语法"),
                ("Get-ChildItem 等 cmdlet", "ls", "PowerShell 不算 POSIX"),
                ("[[ ... ]]", "[ ... ] 或 test", "bash 扩展，/bin/sh 可能不支持"),
                ("数组 a=(1 2 3)", "改用 Python 或 bash -c", "bash 专属"),
                ("source 之外的 . 用法需确认", "sh 支持 .", "注意 POSIX 差异"),
            ],
        }

    return {"shell": sh, "allowed": [], "forbidden": []}


# --------------------------------------------------------------------------- #
# 命令方言预检（给 exec_tools 用的最后一道网）
# --------------------------------------------------------------------------- #
# 每条规则：(正则, 级别, 说明, 建议)
_CMD_TRAPS = [
    (r"\$env\s*:", "error", "cmd.exe 不认识 $env: 语法（那是 PowerShell）",
     "改用 set NAME=value 设置、%NAME% 引用"),
    (r"\b(Get|Set|Remove|New|Add|Clear|Copy|Move|Rename|Join|Split|Measure|Compare|"
     r"Group|Sort|Format|Convert|Import|Export|Update|Restart|Resume|Suspend|Wait|"
     r"Receive|Send|Register|Unregister|Enable|Disable|Show|Hide|Read|Enter|Exit|"
     r"Push|Pop|Redo|Undo|Save|Lock|Unlock|Protect|Unprotect|Use|Unblock|Debug|"
     r"Checkpoint|Invoke|Start|Stop|Select|Where|ForEach|Out|Write|Test"
     r")-[A-Z]\w+",
     "error", "这是 PowerShell cmdlet，cmd.exe 里没有",
     "改用对应的 cmd 命令，或显式写 powershell -Command \"...\""),
    (r"(^|[\s;&|])\$\{?\w+\}?(\s|=|$|\")", "warn",
     "cmd.exe 不展开 $NAME（那是 shell 变量）",
     "用 %NAME%"),
    (r"^\s*export\s+\w+=", "error", "export 是 bash/sh 语法，cmd 不支持",
     "改用 set NAME=value"),
    # 注：curl 不算 —— Windows 10 1803+ 起自带 curl.exe，写 curl 是**对的**。
    #     wget 仍算（cmd 里确实没有，除非用户自己装了）。
    (r"(^|[\s;&|])(grep|awk|sed|chmod|chown|which|wget|touch|ln)\b",
     "warn", "这些是 Unix 工具，cmd.exe 里通常不存在",
     "用 findstr / where，或直接写 Python 做"),
    (r"(^|[\s;&|])ls(\s|$)", "warn", "cmd 里 ls 不是内置命令（除非装了别名工具）",
     "用 dir"),
    (r"(^|[\s;&|])rm\s+-", "error", "rm -rf 是 Unix 写法，cmd 里没有",
     "用 rmdir /s /q 或 del /q"),
    (r"(^|[\s;&|])cp\s+", "warn", "cp 是 Unix 写法", "用 copy 或 xcopy/robocopy"),
    (r"(^|[\s;&|])cat\s+", "warn", "cat 是 Unix 写法", "用 type"),
    (r"\.ps1\b", "warn", "cmd 不能直接执行 .ps1 脚本",
     "写成 powershell -ExecutionPolicy Bypass -File x.ps1"),
    (r"(?<![=!<>])'(?:[^']*)'(?![=!<>])", "warn",
     "cmd.exe 不把单引号当引号，会连同引号一起传给程序",
     "改用双引号"),
    (r"(^|[|;&]\s*)head\b|(^|[|;&]\s*)tail\b", "warn",
     "cmd 没有 head / tail（那是 Unix 的）",
     "用 findstr，或改用 Python 读前/后 N 行"),
    (r"(^|[\s;&|])mkdir\s+-[pP]\b", "error",
     "cmd 的 mkdir 不支持 -p（那是 Unix 的递归建目录）",
     "改用 mkdir a\\b（cmd 默认就会建中间目录）"),
    (r"&&\s*$", "info", "以 && 结尾会让命令等待后文", "检查是否写完整"),
]

_SH_TRAPS = [
    (r"^\s*(dir|copy|del|type|set\s+\w+=|move|ren|cls|echo\s+%|tasklist|taskkill)\b",
     "error", "这是 Windows cmd 命令，/bin/sh 里不存在", "改用 ls / cp / rm / cat / export"),
    (r"%\w+%", "error", "%VAR% 是 cmd 语法，sh 里不展开", "改用 $VAR"),
]


def check_command(command, facts=None):
    """对一条命令做方言预检，返回 [(级别, 说明, 建议), ...]。

    级别：error（几乎必然跑不通）/ warn（多半跑不通）/ info（提示）
    绝不抛异常；空命令返回 []。
    """
    out = []
    try:
        import re
        cmd = str(command or "")
        if not cmd.strip():
            return out
        f = facts or collect()

        # ★★ 2026-10-19 关键修正：
        #   若这条命令**显式调用了另一个 shell**（powershell / pwsh / bash / wsl），
        #   那么里面的语法是给那个 shell 看的，不该拿当前 shell 的方言去判！
        #   踩过的坑：预检曾把 `powershell -NoProfile -Command "Get-ChildItem"`
        #   判为"必然失败" —— 可那正是本模块自己推荐的正确写法，自相矛盾。
        if re.search(r"(^|[\s;&|\"(])(powershell|pwsh|bash|wsl)(\.exe)?\b",
                     cmd, re.I):
            return []

        traps = _CMD_TRAPS if (f.get("shell") == "cmd.exe") else _SH_TRAPS
        for pat, level, msg, fix in traps:
            try:
                if re.search(pat, cmd, re.M):
                    out.append((level, msg, fix))
            except Exception:
                continue
        # 单引号那条容易误报（Python 代码里常有），对 python -c 放行
        if "python" in cmd.lower() and "-c" in cmd.lower():
            out = [x for x in out if "单引号" not in x[1]]
    except Exception:
        return []
    # 去重（同一说明只留一条）
    seen, uniq = set(), []
    for it in out:
        if it[1] in seen:
            continue
        seen.add(it[1])
        uniq.append(it)
    return uniq


# --------------------------------------------------------------------------- #
# 渲染
# --------------------------------------------------------------------------- #
def summary_line(facts=None):
    """一行摘要，适合塞进状态条 / 日志。"""
    f = facts or collect()
    bits = [f.get("os_label") or "?", "shell " + (f.get("shell") or "?"),
            f.get("python") or "?"]
    if f.get("venv"):
        bits.append("venv")
    if f.get("admin") is True:
        bits.append("管理员")
    return " · ".join(bits)


def render_panel(facts=None, width=70):
    """多行面板（给人看）。"""
    f = facts or collect()
    L = []
    L.append("🖥  机器环境 · MACHINE")
    L.append("─" * 30)
    L.append("系统      %s" % (f.get("os_label") or "?"))
    L.append("架构      %s" % (f.get("machine") or "?"))
    L.append("Python    %s" % (f.get("python") or "?"))
    if f.get("venv"):
        L.append("虚拟环境  %s" % f["venv"])
    enc = f.get("encoding") or {}
    L.append("编码      stdout=%s  首选=%s" % (enc.get("stdout", "?"),
                                              enc.get("preferred", "?")))
    sh = f.get("shell")
    L.append("执行shell %s" % ("cmd.exe（不是 PowerShell）" if sh == "cmd.exe" else sh))
    adm = f.get("admin")
    L.append("权限      %s" % ("管理员 / root" if adm else ("普通用户" if adm is False else "未知")))
    if f.get("drive"):
        L.append("工作盘    %s  分隔符 %s" % (f["drive"], f.get("path_sep")))
    rules = shell_rules(f)
    if rules.get("forbidden"):
        L.append("─" * 30)
        L.append("该 shell 不可用（写了必错）：")
        for bad, good, why in rules["forbidden"][:6]:
            L.append("  ✗ %-28s → %s" % (bad[:28], good))
    return "\n".join(L)


def render_prompt(facts=None, max_forbidden=8):
    """精简文本（给 AI 注入系统提示词用）。

    ★ 刻意写得短：系统提示词每轮都要吃 token，这里只保留"会让我犯错的"事实。
    """
    f = facts or collect()
    L = []
    L.append("· 机器环境：%s ｜ %s ｜ Python %s%s"
             % (f.get("os_label") or "?", f.get("machine") or "?",
                f.get("python") or "?",
                "（venv）" if f.get("venv") else ""))
    sh = f.get("shell")
    if sh == "cmd.exe":
        L.append("· 执行 shell：**cmd.exe**（ws_run_cmd 走 `cmd /c`，不是 PowerShell）")
        L.append("  ✅ 可用：dir / type / copy / del / findstr / where / set X=1 / %X%")
        L.append("  ❌ 禁用：$env:X / Get-ChildItem / Remove-Item / export / grep / "
                 "sed / which / rm -rf / 单引号当引号 / .ps1 直跑")
        L.append("  ⚠️ 要 PowerShell 语义就显式写：powershell -NoProfile -Command \"...\"")
    else:
        L.append("· 执行 shell：**%s**（ws_run_cmd 走 `/bin/sh -c`）" % sh)
        L.append("  ✅ 可用：ls / cat / rm / export X=1 / $X / grep / which")
        L.append("  ❌ 禁用：dir / copy / type / set X=1 / %X% / Get-ChildItem")
    enc = (f.get("encoding") or {}).get("preferred") or "?"
    if "utf" not in enc:
        L.append("· 控制台编码：%s（中文输出可能乱码，脚本里显式指定 UTF-8）" % enc)
    return "\n".join(L)


def _selftest():
    """离线自检：打印采集结果 + 若干方言预检样例。"""
    import json
    f = collect(force=True)
    print("== system_facts 自检 ==")
    print(json.dumps({k: v for k, v in f.items() if k != "errors"},
                     ensure_ascii=False, indent=2))
    print()
    print(render_panel(f))
    print()
    print("【注入给 AI 的精简文本】")
    print(render_prompt(f))
    print()
    print("【方言预检样例】")
    samples = [
        "dir /b",
        "Get-ChildItem -Path .",
        "$env:PATH",
        "export FOO=1",
        "grep -r foo .",
        "rm -rf build",
        "python -c 'print(1)'",
        "dir *.py | findstr test",
        'echo "hello"',
    ]
    for s in samples:
        hits = check_command(s, f)
        tag = "OK " if not hits else ("ERR" if any(h[0] == "error" for h in hits) else "WARN")
        print("  [%s] %s" % (tag, s))
        for lvl, msg, fix in hits:
            print("        - (%s) %s → %s" % (lvl, msg, fix))
    return 0


if __name__ == "__main__":
    sys.exit(_selftest())
