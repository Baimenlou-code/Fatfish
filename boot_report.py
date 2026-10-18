# -*- coding: utf-8 -*-
"""
boot_report.py —— 肥鱼开工自检模块 / FatFish Boot Report

作用 / What it does
--------------------
在肥鱼（FATHFISHII.py）每次启动时，采集一份「运行环境快照」，然后做两件事：

  ① 终端可视化：以带色彩、带分隔线的面板输出，人一眼能看清；
  ② 注入给模型：把同一份信息压成精简文本，拼进系统提示词，
     让执行者 AI 从第一句话起就知道「程序在哪、现在几点、工作台在哪、有无异常」。

采集内容 / Collected items（全部本机、纯只读、不联网）
----------------------------------------------------
  · 物理时间：本地时间 / UTC / 时区偏移 / 星期
  · 运行进程：真实 PID、进程启动时间、已运行时长、工作目录、解释器路径与版本
  · 程序文件：正在运行的脚本绝对路径、字节数、最后修改时间
  · 工作台根：当前 workspace 目录（由调用方传入，避免模块间循环依赖）
  · 同名副本：周边目录里同名程序文件清单（哪份最新、哪份正在跑）
  · 健康检查：`_fatfish_pid.txt` 记录的运行窗口 PID 是否仍然有效（存活 + 进程身份）

设计原则 / Design rules
----------------------
  · 绝不抛异常：任何一步失败都降级为 None / 空列表，`collect()` 统一兜底。
  · 无第三方依赖：只用标准库（ctypes 亦为可选，失败即降级）。
  · 纯只读：不写任何文件、不发任何网络请求。
  · 可关闭：主程序用 .env 的 BOOT_REPORT / BOOT_REPORT_PEERS 开关控制。

自测 / Self-test
---------------
    python boot_report.py            # 打印完整可视化面板
    python boot_report.py --brief    # 只打印「注入给模型」的精简文本
    python boot_report.py --no-color # 无颜色（便于重定向到文件比对）
"""

import os
import sys
import time
import datetime

__all__ = [
    "collect", "render_console", "render_brief", "system_message",
    "scan_peers", "paint", "RST", "C",
]

# ============================================================
# 颜色 / 宽度 工具（本模块自足，不依赖主程序，避免循环导入）
# ============================================================

RST = "\033[0m"
C = {
    "cyan":    "\033[96m",
    "blue":    "\033[94m",
    "green":   "\033[92m",
    "yellow":  "\033[93m",
    "red":     "\033[91m",
    "magenta": "\033[95m",
    "gray":    "\033[90m",
    "white":   "\033[97m",
    "bold":    "\033[1m",
    "dim":     "\033[2m",
}


def paint(text, *styles):
    """给文本套 ANSI 样式；styles 为空则原样返回。"""
    if not styles:
        return str(text)
    return "".join(styles) + str(text) + RST


def _dw(s):
    """估算字符串的显示宽度（东亚宽字符/emoji 记 2，组合符记 0）。

    仅用于把内容对齐到面板宽度；估偏一点不影响可读性，故不做严格处理。
    """
    try:
        import unicodedata
    except Exception:
        return len(s or "")
    w = 0
    for ch in str(s or ""):
        if unicodedata.combining(ch):
            continue
        w += 2 if unicodedata.east_asian_width(ch) in ("W", "F") else 1
    return w


def _pad(s, width):
    """按显示宽度右侧补空格（超宽则原样返回）。"""
    d = width - _dw(s)
    return s + (" " * d if d > 0 else "")


def _bar(pct, width=10):
    """纯 ASCII 额度条（避免 unicode 方块在不同终端下宽度不一）。"""
    try:
        p = max(0.0, min(1.0, float(pct or 0)))
    except (TypeError, ValueError):
        p = 0.0
    n = int(round(p * width))
    return "[" + "#" * n + "." * (width - n) + "]"


# ============================================================
# 小工具：时间 / 体积 / 时区 / 进程启动时间
# ============================================================

WEEK = ["周一", "周二", "周三", "周四", "周五", "周六", "周日"]


def _fmt_ts(ts, with_week=False):
    if not ts:
        return "-"
    s = time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(ts))
    if with_week:
        s += " " + WEEK[time.localtime(ts).tm_wday]
    return s


def _fmt_size(n):
    try:
        n = float(n)
    except (TypeError, ValueError):
        return "-"
    if n < 1024:
        return "%d B" % int(n)
    for unit in ("KB", "MB", "GB"):
        n /= 1024.0
        if n < 1024 or unit == "GB":
            return "%.1f %s" % (n, unit)
    return "%.1f GB" % n


def _fmt_dur(sec):
    """把秒数格式化为 1d2h3m4s 风格。"""
    if sec is None:
        return "-"
    sec = int(max(0, sec))
    d, sec = divmod(sec, 86400)
    h, sec = divmod(sec, 3600)
    m, s = divmod(sec, 60)
    parts = []
    if d:
        parts.append("%dd" % d)
    if h or d:
        parts.append("%dh" % h)
    if m or h or d:
        parts.append("%dm" % m)
    parts.append("%ds" % s)
    return "".join(parts)


def _tz_info(now=None):
    """返回 (时区标签, UTC 偏移小时数)。"""
    try:
        dt = now or datetime.datetime.now()
        off = dt.astimezone().utcoffset()
        hours = off.total_seconds() / 3600.0 if off is not None else None
        if hours is None:
            return "本地时区未知", None
        sign = "+" if hours >= 0 else "-"
        ah = abs(hours)
        hh = int(ah)
        mm = int(round((ah - hh) * 60))
        label = "UTC%s%d:%02d" % (sign, hh, mm)
        name = time.tzname[0] if time.tzname else ""
        return (label + (" " + name if name else "")), hours
    except Exception:
        return "本地时区未知", None


def _proc_start_time(pid):
    """取进程启动时间戳（Windows 用 ctypes GetProcessTimes；失败返回 None）。"""
    if os.name != "nt":
        return None
    try:
        import ctypes
        from ctypes import wintypes

        PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
        k32 = ctypes.windll.kernel32
        k32.OpenProcess.restype = wintypes.HANDLE
        k32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        h = k32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, int(pid))
        if not h:
            return None
        try:
            create, exit_t = wintypes.FILETIME(), wintypes.FILETIME()
            kern, user = wintypes.FILETIME(), wintypes.FILETIME()
            ok = k32.GetProcessTimes(h, ctypes.byref(create), ctypes.byref(exit_t),
                                     ctypes.byref(kern), ctypes.byref(user))
            if not ok:
                return None
            # FILETIME：自 1601-01-01 起的 100 纳秒数
            val = (create.dwHighDateTime << 32) | create.dwLowDateTime
            return val / 1e7 - 11644473600.0
        finally:
            try:
                k32.CloseHandle(h)
            except Exception:
                pass
    except Exception:
        return None


# ============================================================
# PID 标记文件（_fatfish_pid.txt）的存活 / 身份查询
# ============================================================
#
# 语义澄清（重要）：
#   `_fatfish_pid.txt` 记录的是 **runtime 窗口进程（cmd.exe）的 PID**，
#   由 fatfish_runtime.bat 在启动主程序前写入、主程序退出后删除，
#   供外部工具识别 / 关闭肥鱼运行窗口。
#   它与「本 Python 进程的 PID」本来就是两个不同的数字，**不能拿两者比较**。
#   所以校验只做两件事：① 该 PID 是否还活着；② 它还像不像肥鱼链路里的进程。
#
# 允许的映像名：runtime cmd 本体，以及两种终端宿主（传统 conhost / Windows Terminal）
PID_OWNER_NAMES = {
    "cmd.exe", "conhost.exe", "openconsole.exe",
    "windowsterminal.exe", "wt.exe",
}

_PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
_STILL_ACTIVE = 259
_ERROR_INVALID_PARAMETER = 87
_ERROR_ACCESS_DENIED = 5


def _pid_state(pid):
    """查询 PID 的存活状态与映像名（纯只读；任何失败都降级为 None，绝不抛异常）。

    返回 (alive, name)：
        alive = True  —— 进程存在
        alive = False —— 进程不存在
        alive = None  —— 查询失败（异常），调用方应放弃判断、不要报警
        name  —— 小写映像名（如 'cmd.exe'）；取不到则为 None（含无权限打开的情形）
    """
    if os.name != "nt":
        return None, None
    try:
        import ctypes
        from ctypes import wintypes

        k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        k32.OpenProcess.restype = wintypes.HANDLE
        k32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        k32.GetExitCodeProcess.argtypes = [wintypes.HANDLE,
                                           ctypes.POINTER(wintypes.DWORD)]
        k32.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD,
                                                   wintypes.LPWSTR,
                                                   ctypes.POINTER(wintypes.DWORD)]
        k32.CloseHandle.argtypes = [wintypes.HANDLE]

        h = k32.OpenProcess(_PROCESS_QUERY_LIMITED_INFORMATION, False, int(pid))
        if not h:
            err = ctypes.get_last_error()
            if err == _ERROR_INVALID_PARAMETER:
                return False, None          # 该 PID 不存在
            if err == _ERROR_ACCESS_DENIED:
                return True, None           # 存在，但无权限读它的映像名（上层按 unknown 处理）
            return None, None

        try:
            code = wintypes.DWORD()
            if not k32.GetExitCodeProcess(h, ctypes.byref(code)):
                return None, None
            if code.value != _STILL_ACTIVE:
                return False, None
            buf = ctypes.create_unicode_buffer(1024)
            size = wintypes.DWORD(len(buf))
            if k32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(size)):
                return True, os.path.basename(buf.value).lower()
            return True, None
        finally:
            try:
                k32.CloseHandle(h)
            except Exception:
                pass
    except Exception:
        return None, None


# ============================================================
# 同名副本扫描
# ============================================================

# 这些目录名一律跳过（既省时间，也避免把 venv / 缓存里的同名文件算进来）
SKIP_DIRS_LOWER = {
    "venv", ".venv", "env", "__pycache__", "site-packages", "dist-packages",
    "node_modules", ".git", ".hg", ".svn", ".idea", ".vscode",
    ".fatfish_tmp", "_backup", "oldver",
}


def _is_drive_root(path):
    try:
        drive, tail = os.path.splitdrive(os.path.abspath(path))
        return tail in ("\\", "/", "")
    except Exception:
        return False


def _peer_roots(script_path, up=2):
    """候选扫描根：脚本所在目录 + 向上最多 up 层（跳过盘根，以免全盘扫描）。"""
    roots = []
    try:
        p = os.path.dirname(os.path.abspath(script_path))
    except Exception:
        return roots
    for _ in range(up + 1):
        if p and p not in roots and not _is_drive_root(p):
            roots.append(p)
        nxt = os.path.dirname(p)
        if not nxt or nxt == p:
            break
        p = nxt
    return roots


def scan_peers(script_path, extra_roots=None, max_files=900, max_depth=2):
    """扫描周边同名程序文件。

    返回 (hits, scanned_files, truncated)：
      hits          —— [{"path","size","mtime","current"}, ...]，按修改时间倒序
      scanned_files —— 实际查看过的文件条目数（用于说明扫描规模）
      truncated     —— 是否因 max_files 上限提前收工
    """
    hits, scanned, truncated = [], 0, False
    try:
        target = os.path.basename(script_path)
        cur_abs = os.path.abspath(script_path)
    except Exception:
        return hits, scanned, truncated

    roots, visited = [], set()
    for r in (_peer_roots(script_path) + list(extra_roots or [])):
        try:
            r = os.path.abspath(r)
        except Exception:
            continue
        if r in visited or not os.path.isdir(r):
            continue
        visited.add(r)
        roots.append(r)

    for root in roots:
        base_sep = root.rstrip("\\/").count(os.sep)
        for dp, dn, fn in os.walk(root):
            try:
                depth = dp.rstrip("\\/").count(os.sep) - base_sep
            except Exception:
                depth = 0
            dn[:] = [d for d in dn if d.lower() not in SKIP_DIRS_LOWER]
            if depth >= max_depth:
                dn[:] = []
            for f in fn:
                scanned += 1
                if scanned > max_files:
                    return hits, scanned, True
                if f != target:
                    continue
                p = os.path.join(dp, f)
                try:
                    st = os.stat(p)
                except OSError:
                    continue
                # 扫描根可能互相重叠（如 workspace 与其父目录），同一文件只记一次
                if any(h["path"].lower() == p.lower() for h in hits):
                    continue
                hits.append({
                    "path": p,
                    "size": st.st_size,
                    "mtime": st.st_mtime,
                    "current": os.path.normcase(p) == os.path.normcase(cur_abs),
                })
    hits.sort(key=lambda d: (-(d["mtime"] or 0), d["path"]))
    return hits, scanned, truncated


# ============================================================
# 搜索后端 & 用量自检（2026-10-18 新增）
# ============================================================
#
# 为什么放进开机自检：
#   多后端启用后，「这次到底会用哪几个搜索引擎、这个月还剩多少额度、要花多少钱」
#   变成每次启动都该一眼看到的基本事实。等联网出错才去翻 .env，太晚。
#
# 设计约束（与 boot_report 其它部分一致）：
#   · 纯只读：只读环境变量 + 查本地 sqlite，不发任何网络请求；
#   · 绝不抛异常：包没装 / 缺 key / 库坏了 → 降级成一行说明，不影响启动；
#   · 零硬依赖：import 失败就当没这功能（老部署照常跑）。
#
# 数据来源：
#   · 配置来自 net_tools 模块属性（主程序已从 .env 注入过）
#   · 用量来自 deepseek_search.quality 的台账（SQLite）

_SEARCH_PKG = {"tried": False, "mod": None}


def _load_search_pkg(root_dir=None):
    """延迟加载 workspace/deepseek_search；失败返回 None（调用方自行降级）。"""
    if _SEARCH_PKG["tried"]:
        return _SEARCH_PKG["mod"]
    _SEARCH_PKG["tried"] = True
    try:
        base = root_dir or os.path.dirname(os.path.abspath(__file__))
        pkg_dir = os.path.join(base, "workspace")
        if os.path.isdir(pkg_dir) and pkg_dir not in sys.path:
            sys.path.insert(0, pkg_dir)
        import deepseek_search as _ds
        _SEARCH_PKG["mod"] = _ds
    except Exception:
        _SEARCH_PKG["mod"] = None
    return _SEARCH_PKG["mod"]


def _net_cfg(name, default):
    """读一项联网配置：.env/环境变量优先，其次 net_tools 模块属性。

    为什么环境变量优先：
      boot_report 在**启动那一刻**跑，此时 .env 才是"本次启动的真实配置"。
      主程序读完 .env 会把它推给 net_tools（于是两处一致）；
      而单独跑 `python boot_report.py` 时没人推，只有 env 是对的 ——
      若这里只信 net_tools 的出厂默认（如 router=rule），就会显示错误信息。
    """
    try:
        env_v = (os.environ.get(name) or "").strip()
    except Exception:
        env_v = ""
    if env_v:
        return env_v
    try:
        import net_tools as _nt
        v = getattr(_nt, name, None)
        if v is not None and str(v).strip():
            return str(v).strip()
    except Exception:
        pass
    return default


def _net_backends_spec():
    """读 NET_BACKENDS 的当前值（env 优先，兼容 net_tools 被 programmatic 设过的情况）。"""
    try:
        env_v = (os.environ.get("NET_BACKENDS") or "").strip()
    except Exception:
        env_v = ""
    if env_v:
        return env_v
    try:
        import net_tools as _nt
        return (getattr(_nt, "NET_BACKENDS", "") or "").strip()
    except Exception:
        return ""


def collect_search(root_dir=None):
    """采集搜索后端配置 + 本月用量。返回 dict，任何失败都降级为说明文本。"""
    out = {
        "ok": True,
        "pkg": False,
        "enabled": False,        # 是否启用了多后端
        "spec": "",              # NET_BACKENDS 原值
        "spec_desc": "",         # 人话描述
        "router": "rule",
        "merge": "rrf",
        "backends": [],          # [{name,label,unit,used,free,remaining,pct,cost,period}]
        "period": "",
        "total_cost": 0.0,
        "alerts": [],
        "keys": {},              # {deepseek: bool, tavily: bool, ...}
        "errors": [],
    }

    # ---- 1. 配置（不依赖 deepseek_search，永远能读出来）----
    try:
        spec = _net_backends_spec()
        out["spec"] = spec
        low = spec.lower()
        if not spec:
            out["enabled"] = False
            out["spec_desc"] = "未启用（走原单后端）"
        elif low in ("auto", "all", "*"):
            out["enabled"] = True
            out["spec_desc"] = "auto（所有配了 key 的后端）"
        else:
            out["enabled"] = True
            out["spec_desc"] = spec
        try:
            out["router"] = _net_cfg("NET_BACKEND_ROUTER", "rule") or "rule"
            out["merge"] = _net_cfg("NET_MERGE", "rrf") or "rrf"
        except Exception:
            out["router"], out["merge"] = "rule", "rrf"
    except Exception as e:
        out["errors"].append("读取搜索后端配置失败：%s" % e)

    # ---- 2. key 有无（只报有没有，绝不回显内容）----
    for _name, _env in (("deepseek", "DEEPSEEK_API_KEY"),
                        ("tavily", "TAVILY_API_KEY"),
                        ("bocha", "BOCHA_API_KEY"),
                        ("serper", "SERPER_API_KEY"),
                        ("bing", "BING_SEARCH_KEY")):
        try:
            out["keys"][_name] = bool((os.environ.get(_env) or "").strip())
        except Exception:
            out["keys"][_name] = False

    # ---- 3. 用量（需要 deepseek_search，失败就只报配置）----
    ds = _load_search_pkg(root_dir)
    if ds is None:
        out["ok"] = False
        out["errors"].append("deepseek_search 包不可用（只有配置信息，无用量统计）")
        return out
    out["pkg"] = True

    try:
        spec_norm = None if out["spec"].lower() in ("auto", "all", "*") else (
            [] if not out["spec"] else
            [x.strip() for x in out["spec"].replace("，", ",").replace("、", ",").split(",")
             if x.strip()])
        avail = ds.router.available_backends(spec_norm if spec_norm != [] else [])
        if spec_norm == [] and not out["enabled"]:
            # 未启用多后端：仍然列出"哪些后端本来可用"，方便你决定要不要开
            avail_all = ds.router.available_backends(None)
            out["available_all"] = avail_all
    except Exception as e:
        out["errors"].append("探测可用后端失败：%s" % e)
        avail = []

    try:
        usage = ds.quota.usage()
        period = ds.quota.month_bounds()[2]
        out["period"] = period
        out["total_cost"] = ds.quota.total_cost()
        out["alerts"] = ds.quota.alerts()
    except Exception as e:
        out["errors"].append("读取用量台账失败：%s" % e)
        usage, period = {}, ""

    # ---- 4. 组装后端明细 ----
    seen = set()
    for name in avail:
        u = usage.get(name) or {}
        row = {
            "name": name,
            "label": u.get("label") or name,
            "unit": u.get("unit") or "次",
            "used": float(u.get("units") or 0),
            "free": u.get("free_monthly"),
            "remaining": u.get("remaining"),
            "pct": u.get("used_pct"),
            "cost": float(u.get("cost_yuan") or 0),
            "calls": int(u.get("calls") or 0),
            "failed": int(u.get("failed") or 0),
        }
        out["backends"].append(row)
        seen.add(name)
    # 有台账但当前不可用（例如 key 被删了）—— 也列出来，别让数据凭空消失
    for name, u in (usage or {}).items():
        if name in seen:
            continue
        out["backends"].append({
            "name": name, "label": u.get("label") or name,
            "unit": u.get("unit") or "次",
            "used": float(u.get("units") or 0),
            "free": u.get("free_monthly"),
            "remaining": u.get("remaining"),
            "pct": u.get("used_pct"),
            "cost": float(u.get("cost_yuan") or 0),
            "calls": int(u.get("calls") or 0),
            "failed": int(u.get("failed") or 0),
            "unavailable": True,
        })
    out["backends"].sort(key=lambda r: (-(r["used"] or 0), r["name"]))
    return out


def _balance_brief():
    """官方余额的精简尾巴（2026-10-18 新增）。

    ★ 读 net_balance 的**磁盘缓存**，不联网、不阻塞 —— 开机自检绝不能因为
      查余额而卡住启动（查不到就只显示"未取到"，不影响任何其它逻辑）。
    """
    try:
        import net_balance as _nb
    except Exception:
        return []
    out = []
    try:
        l = _nb.one_line(allow_net=False)
        if l:
            out.append("· " + l)
    except Exception:
        pass
    try:
        for a in _nb.alarms(allow_net=False):
            if a.get("level") in ("warn", "crit", "dead"):
                out.append("· " + _nb.LEVEL_ICON.get(a["level"], "") + " " + a["text"])
    except Exception:
        pass
    return out


def format_search_brief(s, max_backends=3):
    """把搜索自检结果压成一行（或两行），供注入系统提示词。"""
    if not s:
        return ""
    if not s.get("enabled"):
        avail = s.get("available_all") or []
        tail = ("，可用后端：%s" % "、".join(avail[:4])) if avail else ""
        _lines = ["· 搜索后端：未启用多后端（走原单后端）%s" % tail]
        _lines.extend(_balance_brief())
        return "\n".join(_lines)

    parts = ["· 搜索后端：%s（router=%s，merge=%s）"
             % (s.get("spec_desc") or s.get("spec") or "?", s.get("router"), s.get("merge"))]

    items = []
    for r in (s.get("backends") or [])[:max_backends]:
        if r.get("free"):
            seg = "%s %.0f/%d %s" % (r["label"], r["used"], r["free"], r["unit"])
        elif r.get("cost"):
            seg = "%s %.3f 元" % (r["label"], r["cost"])
        else:
            seg = "%s 本月 0" % r["label"]
        if r.get("unavailable"):
            seg += "（当前不可用）"
        items.append(seg)
    if items:
        parts.append("（" + "，".join(items)
                     + ("，等 %d 个" % len(s["backends"]) if len(s["backends"]) > max_backends
                        else "") + "）")
    if s.get("period"):
        parts.append("｜周期 %s" % s["period"])
    line = "".join(parts)
    alert = (s.get("alerts") or [])
    if alert:
        line += "\n· 额度提醒：" + "；".join(alert[:2])
    # 2026-10-18：带上官方余额（读缓存，不联网）
    _bl = _balance_brief()
    if _bl:
        line += "\n" + "\n".join(_bl)
    return line


# ============================================================
# 主采集
# ============================================================

def collect(script_path=None, workspace_dir=None, extra_roots=None,
            do_scan_peers=True, max_files=900, max_depth=2, do_search=True,
            root_dir=None):
    """采集开工自检信息，返回 dict。任何子步骤失败都不抛异常。"""
    info = {
        "ok": True,
        "errors": [],
        "warnings": [],
        "notes": [],
        "collected_at": time.time(),
    }

    # ---- 1. 物理时间 ----
    try:
        now = datetime.datetime.now()
        tz_label, tz_hours = _tz_info(now)
        info.update({
            "now_ts": time.time(),
            "now_str": now.strftime("%Y-%m-%d %H:%M:%S"),
            "weekday": WEEK[now.weekday()],
            "utc_str": datetime.datetime.utcnow().strftime("%Y-%m-%d %H:%M:%S"),
            "tz_label": tz_label,
            "tz_hours": tz_hours,
        })
    except Exception as e:
        info["errors"].append("时间采集失败：%s" % e)

    # ---- 2. 运行进程 ----
    try:
        pid = os.getpid()
        start_ts = _proc_start_time(pid)
        info.update({
            "pid": pid,
            "python_version": sys.version.split()[0],
            "python_exe": sys.executable,
            "cwd": os.getcwd(),
            "proc_start_ts": start_ts,
            "uptime_sec": (time.time() - start_ts) if start_ts else None,
            "pid_source": "os.getpid()",
        })
    except Exception as e:
        info["errors"].append("进程信息采集失败：%s" % e)

    # ---- 3. 程序文件 ----
    try:
        sp = script_path or (sys.argv[0] if sys.argv and sys.argv[0] else "")
        sp = os.path.abspath(sp) if sp else ""
        info["script_path"] = sp
        if sp and os.path.isfile(sp):
            st = os.stat(sp)
            info["script_size"] = st.st_size
            info["script_mtime"] = st.st_mtime
        else:
            info["warnings"].append("未能定位到正在运行的程序文件：%s" % (sp or "(未知)"))
    except Exception as e:
        info["errors"].append("程序文件采集失败：%s" % e)

    # ---- 4. 工作台根 ----
    try:
        ws = workspace_dir
        if ws:
            ws = os.path.abspath(ws)
        info["workspace"] = ws
        if ws and not os.path.isdir(ws):
            info["warnings"].append("工作台目录不存在：%s" % ws)
        elif ws and info.get("cwd"):
            same = os.path.normcase(os.path.abspath(info["cwd"])) == os.path.normcase(ws)
            info["cwd_is_workspace"] = same
    except Exception as e:
        info["errors"].append("工作台采集失败：%s" % e)

    # ---- 5. 同名副本 ----
    info["peers"], info["peers_scanned"], info["peers_truncated"] = [], 0, False
    if do_scan_peers and info.get("script_path"):
        try:
            hits, scanned, trunc = scan_peers(
                info["script_path"], extra_roots=extra_roots,
                max_files=max_files, max_depth=max_depth)
            info["peers"], info["peers_scanned"], info["peers_truncated"] = hits, scanned, trunc
            others = [h for h in hits if not h["current"]]
            if others:
                info["notes"].append("发现 %d 份同名程序副本（非当前运行文件）" % len(others))
            if trunc:
                info["notes"].append("副本扫描达到条目上限 %d，结果可能不完整" % max_files)
        except Exception as e:
            info["errors"].append("副本扫描失败：%s" % e)

    # ---- 6. 健康检查：_fatfish_pid.txt ----
    #  该文件记录 runtime 窗口（cmd.exe）的 PID，供外部工具识别 / 关闭肥鱼窗口；
    #  它与本进程（Python）的 PID 本就不同，因此**不做数值相等比较**，
    #  改为「存活 + 是否像肥鱼链路进程」的判定：
    #      ok       存活且映像名在白名单内   → 记 notes（不报警）
    #      stale    PID 已不存在（残留）     → warning，提示可安全删除
    #      suspect  存活但映像名不在白名单   → warning，提示疑似误抓
    #      unknown  查询失败 / 拿不到映像名  → 只记 notes，不误报
    #      invalid  内容不是数字             → warning
    info["pid_file"] = None
    try:
        if info.get("script_path"):
            cand = os.path.join(os.path.dirname(info["script_path"]), "_fatfish_pid.txt")
            if os.path.isfile(cand):
                raw = ""
                try:
                    with open(cand, "r", encoding="utf-8", errors="replace") as f:
                        raw = f.read().strip()
                except OSError:
                    pass
                rec = {"path": cand, "value": raw, "status": None}
                info["pid_file"] = rec
                digits = "".join(ch for ch in raw if ch.isdigit())
                if not digits:
                    rec["status"] = "invalid"
                    info["warnings"].append(
                        "_fatfish_pid.txt 内容不是有效 PID：%r" % raw[:40])
                else:
                    pid_val = int(digits)
                    alive, owner = _pid_state(pid_val)
                    rec.update({"pid": pid_val, "alive": alive, "owner_name": owner})
                    if alive is False:
                        rec["status"] = "stale"
                        info["warnings"].append(
                            "_fatfish_pid.txt 是残留：记为 %d，但该进程已不存在"
                            "（上次未正常退出，可安全删除）" % pid_val)
                    elif alive is True and owner is None:
                        rec["status"] = "unknown"
                        info["notes"].append(
                            "_fatfish_pid.txt 记为 %d：进程存在，但读不到映像名，"
                            "已跳过身份判断（不报警）" % pid_val)
                    elif alive is True and owner in PID_OWNER_NAMES:
                        rec["status"] = "ok"
                        info["notes"].append(
                            "_fatfish_pid.txt 有效：运行窗口 PID %d（%s）"
                            % (pid_val, owner))
                    elif alive is True:
                        rec["status"] = "suspect"
                        info["warnings"].append(
                            "_fatfish_pid.txt 记为 %d，但该 PID 现在是 %s，"
                            "不像是肥鱼运行窗口（疑似误抓）" % (pid_val, owner))
                    else:
                        rec["status"] = "unknown"
                        info["notes"].append(
                            "_fatfish_pid.txt 记为 %d，存活状态无法确认（已跳过判断）"
                            % pid_val)
    except Exception as e:
        info["errors"].append("PID 标记检查失败：%s" % e)

    # ---- 6b. 机器环境（2026-10-19 新增）----
    #   只读采集 OS / 架构 / shell / 编码 / 权限。
    #   ★ 关键点：这里能告诉 AI「执行命令用的是 cmd.exe 而不是 PowerShell」，
    #     这正是"写出跑不通的命令"这类低级错误的根源。
    info["system"] = None
    try:
        import system_facts as _sf
        info["system"] = _sf.collect()
        if not info["system"].get("ok"):
            info["notes"].append("机器环境采集不完整")
    except Exception as e:
        info["notes"].append("机器环境采集不可用：%s" % e)

    # ---- 7. 搜索后端 & 本月用量（2026-10-18 新增）----
    #   只读本地配置 + sqlite，不发网络请求；失败降级为一条 errors 说明。
    info["search"] = None
    if do_search:
        try:
            info["search"] = collect_search(
                root_dir=root_dir or (os.path.dirname(os.path.abspath(__file__))
                                      if not script_path else os.path.dirname(script_path)))
            for _e in (info["search"].get("errors") or []):
                info["notes"].append("搜索自检：%s" % _e)
        except Exception as e:
            info["notes"].append("搜索后端自检不可用：%s" % e)

    # ---- 8. 汇总提示 ----
    try:
        if info.get("script_path") and info.get("workspace"):
            sd = os.path.normcase(os.path.dirname(info["script_path"]))
            wd = os.path.normcase(info["workspace"])
            if not (sd == wd or sd.startswith(wd + os.sep)):
                info["notes"].append("程序文件位于工作台之外（工作台=%s，程序=%s）"
                                     % (info["workspace"], info["script_path"]))
    except Exception:
        pass

    return info


# ============================================================
# 渲染①：终端可视化面板
# ============================================================

def _rule(width, color=True, ch="─"):
    s = ch * width
    return paint(s, C["gray"]) if color else s


def render_console(info, color=True, width=70):
    """把采集结果渲染成给人看的可视化面板（多行字符串）。"""
    w = max(48, int(width))
    out = []

    def add(s=""):
        out.append(s)

    L = (lambda s: paint(s, C["cyan"], C["bold"])) if color else (lambda s: s)
    K = (lambda s: paint(s, C["blue"])) if color else (lambda s: s)
    V = (lambda s: s)
    GOOD = (lambda s: paint(s, C["green"])) if color else (lambda s: s)
    WARN = (lambda s: paint(s, C["yellow"], C["bold"])) if color else (lambda s: s)
    DIM = (lambda s: paint(s, C["gray"], C["dim"])) if color else (lambda s: s)

    title = "🐟 肥鱼开工自检 · BOOT REPORT"
    sub = "%s %s ｜ %s" % (info.get("now_str", "-"), info.get("weekday", ""),
                           info.get("tz_label", "-"))

    add(_rule(w, color, "━"))
    add(" " + L(title))
    add(" " + DIM(sub))
    add(_rule(w, color, "━"))

    def row(icon_key, value, extra=""):
        """一行两列：左侧图标+键，右侧值。"""
        k = K(icon_key)
        line = " %s %s" % (k, V(value))
        if extra:
            line += "  " + extra
        add(line)

    # 1) 物理时间
    row("⏱ ", "%s（%s）" % (info.get("now_str", "-"), info.get("weekday", "-")),
        DIM("UTC %s" % info.get("utc_str", "-")))
    # 2) 进程
    up = _fmt_dur(info.get("uptime_sec"))
    row("🧬 ", "PID %s ｜ Python %s ｜ 已运行 %s"
        % (info.get("pid", "-"), info.get("python_version", "-"), up),
        DIM("启动 " + _fmt_ts(info.get("proc_start_ts"))))
    row("🐍 ", info.get("python_exe", "-"))
    # 3) 程序文件
    sp = info.get("script_path") or "(未知)"
    row("📄 ", sp)
    if info.get("script_size") is not None:
        add("     %s %s ｜ 改动于 %s"
            % (DIM("·"), _fmt_size(info.get("script_size")),
               _fmt_ts(info.get("script_mtime"))))
    # 4) 工作台
    ws = info.get("workspace") or "(未提供)"
    if info.get("cwd_is_workspace") is True:
        mark = GOOD("✔ 与 cwd 一致")
    elif info.get("cwd_is_workspace") is False:
        mark = WARN("⚠ 与 cwd 不一致（cwd=%s）" % info.get("cwd", "-"))
    else:
        mark = ""
    row("🛠 ", ws, mark)
    # 5) 同名副本
    peers = info.get("peers") or []
    if peers:
        head = "%d 份（扫描 %s 个条目%s）" % (
            len(peers), info.get("peers_scanned", 0),
            "，已达上限可能不全" if info.get("peers_truncated") else "")
        row("🗂 ", head)
        for p in peers[:6]:
            flag = GOOD("★ 当前") if p.get("current") else "     "
            # 注意：先按纯文本补宽、再上色；否则 ANSI 转义码会被算进宽度导致错位
            name_txt = _pad(os.path.basename(p["path"]), 22)
            add("     %s %s  %s  %s" % (
                flag, DIM(name_txt), _pad(_fmt_size(p.get("size")), 10),
                _fmt_ts(p.get("mtime"))))
            add("            %s" % DIM(p["path"]))
        if len(peers) > 6:
            add("     %s" % DIM("…… 另有 %d 份，已省略" % (len(peers) - 6)))
    else:
        row("🗂 ", DIM("未发现同名副本（或已关闭扫描）"))
    # 5b) 机器环境（2026-10-19 新增）
    _sys = info.get("system")
    if _sys:
        row("🖥 ", "%s ｜ %s ｜ %s"
            % (_sys.get("os_label") or "?", _sys.get("machine") or "?",
               _sys.get("python") or "?"))
        _adm = _sys.get("admin")
        _sh = _sys.get("shell") or "?"
        _enc = (_sys.get("encoding") or {}).get("preferred") or "?"
        row("🐚 ", "执行 shell：%s" % _sh,
            DIM("%s ｜ 编码 %s%s"
                % ("管理员" if _adm else ("普通用户" if _adm is False else "权限未知"),
                   _enc, " ｜ venv" if _sys.get("venv") else "")))
        # 只挑最容易写错的几条，别把面板撑爆
        try:
            import system_facts as _sf
            _bad = (_sf.shell_rules(_sys).get("forbidden") or [])[:4]
            if _bad:
                add("     %s" % DIM("该 shell 下这些写法必错："))
                for _b, _g, _w in _bad:
                    add("       %s %s" % (WARN("✗"), DIM("%s → %s" % (_b[:30], _g))))
        except Exception:
            pass

    # 6) 搜索后端 & 用量（2026-10-18 新增）
    s = info.get("search")
    if s:
        if not s.get("enabled"):
            _av = s.get("available_all") or []
            _tail = (GOOD("可启用：") + "、".join(_av[:4])) if _av else WARN("没有任何后端可用")
            row("🌐 ", DIM("搜索后端：未启用多后端（走原单后端）"), _tail)
        else:
            row("🌐 ", "搜索后端：%s" % (s.get("spec_desc") or s.get("spec")),
                DIM("router=%s ｜ merge=%s" % (s.get("router"), s.get("merge"))))
            # 每个后端一行：用量 / 额度条 / 花费
            for r in (s.get("backends") or []):
                label = r.get("label") or r.get("name")
                if r.get("free"):
                    pct = r.get("pct") or 0.0
                    use = WARN if pct >= 0.9 else (GOOD if pct < 0.6 else V)
                    line = ("     %s %s  %s %5.1f%%  已用 %d / %d %s"
                            % (_pad(label, 18), use("%s" % _bar(pct)),
                               V(""), pct * 100, r["used"], r["free"], r["unit"]))
                    add(line)
                else:
                    cost = ("%.3f 元" % r["cost"]) if r.get("cost") else "—"
                    add("     %s 本月 %d %s ｜ %s"
                        % (_pad(label, 18), r["used"], r["unit"], cost))
                if r.get("unavailable"):
                    add("       %s %s" % (WARN("⚠"), DIM("当前不可用（key 缺失或包未装）")))
            if s.get("period"):
                add("     %s %s ｜ 合计 %s"
                    % (DIM("·"), DIM("周期 " + s["period"]),
                       GOOD("%.3f 元" % s["total_cost"]) if s.get("total_cost")
                       else DIM("无花费")))
            for _a in (s.get("alerts") or []):
                add("     %s %s" % (WARN("⚠"), WARN(_a)))

    # 6b) 官方余额 · 本月对账（2026-10-18 新增）
    #     读 net_balance 缓存，不联网；缓存为空就只显示一行"尚未联网"。
    try:
        import net_balance as _nbc
        _btxt = _nbc.panel_text(allow_net=False)
    except Exception:
        _btxt = ""
    if _btxt:
        for _ln in _btxt.splitlines():
            if _ln.startswith("🌐"):
                add(" " + L(_ln))
            elif _ln.startswith("─"):
                add(" " + DIM(_ln))
            elif _ln.startswith("对账") or _ln.startswith("•") or _ln.startswith("⛔") \
                    or _ln.startswith("🔴") or _ln.startswith("🟡"):
                add("     " + WARN(_ln))
            else:
                add(" " + _ln)

    # 7) 健康检查
    warns = info.get("warnings") or []
    notes = info.get("notes") or []
    if warns:
        add(" %s %s" % (K("🩺 "), WARN("需要留意：")))
        for wmsg in warns:
            add("     %s %s" % (paint("⚠", C["yellow"]) if color else "!", wmsg))
    else:
        row("🩺 ", GOOD("健康检查通过，未发现异常"))
    for nmsg in notes:
        add("     %s %s" % (DIM("·"), DIM(nmsg)))
    errs = info.get("errors") or []
    for emsg in errs:
        add("     %s %s" % (paint("✖", C["red"]) if color else "x", DIM(emsg)))

    add(_rule(w, color, "━"))
    return "\n".join(out)


# ============================================================
# 渲染②：注入给模型的精简文本
# ============================================================

def render_brief(info, max_peers=3, for_prompt=True):
    """把采集结果压成精简文本。

    for_prompt=True  —— 带「自动采集、可直接采信」的声明，供拼进系统提示词；
    for_prompt=False —— 纯事实罗列，供日志/调试。
    """
    lines = []
    if for_prompt:
        lines.append("【开工自检 · 由程序在启动时自动采集，非用户输入，可直接采信】")
    lines.append("· 物理时间：%s %s（%s）" % (
        info.get("now_str", "-"), info.get("weekday", ""), info.get("tz_label", "-")))

    proc = "· 运行进程：PID %s ｜ Python %s ｜ 已运行 %s" % (
        info.get("pid", "-"), info.get("python_version", "-"),
        _fmt_dur(info.get("uptime_sec")))
    lines.append(proc)

    if info.get("script_path"):
        seg = "· 程序文件：%s" % info["script_path"]
        if info.get("script_size") is not None:
            seg += "（%s，改动于 %s）" % (_fmt_size(info.get("script_size")),
                                          _fmt_ts(info.get("script_mtime")))
        lines.append(seg)

    if info.get("workspace"):
        seg = "· 工作台根：%s" % info["workspace"]
        if info.get("cwd_is_workspace") is True:
            seg += "（与进程 cwd 一致）"
        elif info.get("cwd_is_workspace") is False:
            seg += "（注意：与进程 cwd %s 不一致）" % info.get("cwd")
        lines.append(seg)

    peers = info.get("peers") or []
    if peers:
        others = [p for p in peers if not p.get("current")]
        if others:
            lst = "；".join(
                "%s（%s，%s）" % (p["path"], _fmt_size(p.get("size")), _fmt_ts(p.get("mtime")))
                for p in others[:max_peers])
            more = "" if len(others) <= max_peers else "，另有 %d 份" % (len(others) - max_peers)
            lines.append("· 同名副本：%d 份非当前运行文件 —— %s%s"
                         % (len(others), lst, more))

    for wmsg in (info.get("warnings") or []):
        lines.append("· 健康提示：%s" % wmsg)
    for nmsg in (info.get("notes") or []):
        lines.append("· 备注：%s" % nmsg)

    # 机器环境（2026-10-19 新增）—— 优先、单独成块，因为它决定命令怎么写
    _sys2 = info.get("system")
    if _sys2:
        try:
            import system_facts as _sf2
            for _ln in _sf2.render_prompt(_sys2).splitlines():
                lines.append(_ln)
        except Exception:
            lines.append("· 机器环境：%s ｜ shell %s"
                         % (_sys2.get("os_label") or "?", _sys2.get("shell") or "?"))

    if for_prompt:
        _sb = format_search_brief(info.get("search"))
        if _sb:
            lines.append(_sb)
        lines.append("（以上信息随每次启动 / 清空上下文自动刷新；"
                     "如需复核工作台，可调用 ws_where。）")
    return "\n".join(lines)


# ============================================================
# 渲染③：一站式「系统提示词注入」接口（供主程序调用）
# ============================================================

def system_message(base_prompt, script_path=None, workspace_dir=None,
                   do_scan_peers=True, verbose=False, extra_roots=None,
                   do_search=True):
    """采集 →（可选）打印可视化面板 → 返回「原提示词 + 自检摘要」。

    这是给主程序用的一站式接口：
      · 任何环节出错都原样返回 base_prompt，保证「自检坏了，程序照常能用」；
      · verbose=True 时顺带把面板打到终端（仅启动那一刻需要）。
    """
    try:
        info = collect(script_path=script_path, workspace_dir=workspace_dir,
                       extra_roots=extra_roots, do_scan_peers=do_scan_peers,
                       do_search=do_search)
        if verbose:
            try:
                print(render_console(info, color=True))
            except Exception:
                pass
        brief = render_brief(info)
        return base_prompt + ("\n\n" + brief if brief else "")
    except Exception:
        return base_prompt


# ============================================================
# 自测入口
# ============================================================

def _selftest(argv=None):
    argv = argv if argv is not None else sys.argv[1:]
    color = "--no-color" not in argv
    here = os.path.abspath(__file__)

    # ★ 单独运行本文件时必须自己加载 .env，否则读到的是「什么都没有」的进程环境：
    #   面板会误报「未启用多后端 / 无 key」，而主程序启动时其实一切正常 ——
    #   这种「自测与实际不符」最容易让人误判，所以这里跟主程序保持同一顺序。
    #   显式给路径：workspace/ 下还有一份 .env，从本文件向上找会先命中它。
    try:
        from dotenv import load_dotenv
        load_dotenv(os.path.join(os.path.dirname(here), ".env"))
    except Exception:
        pass

    # 自测时把「工作台」近似取为本文件所在目录的上一级（真实主程序会传入准确值）
    ws = os.environ.get("WORKSPACE_DIR") or os.path.dirname(here)
    info = collect(script_path=here, workspace_dir=ws)

    if "--brief" in argv:
        print(render_brief(info))
        return 0

    print(render_console(info, color=color))
    print()
    print(paint("【注入给模型的精简文本 · 预览】", C["magenta"], C["bold"]) if color
          else "【注入给模型的精简文本 · 预览】")
    print(render_brief(info))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(_selftest())
    except KeyboardInterrupt:
        sys.exit(130)
