# -*- coding: utf-8 -*-
"""net_balance.py — 联网板块的「余额 · 对账 · 预警」层（2026-10-18 新增）

为什么需要它
------------
原来「联网」这块只管开关（要不要联网、走哪个后端、怎么合并），
账目却散在两处、两套口径：

    官方 API 口径   →  DeepSeek 余额 / Tavily credit（真实扣费，权威）
    本地账本口径    →  quality.db 里 quota.usage()（程序自己记的流水）

两边一旦对不上，用户只看本地账本就会以为「才用了 2 次」，
而实际 Tavily 已经扣了 35 个 credit。
本模块把两套口径摆在一起、算出差额，并按阈值分级提醒。

三条铁律
--------
① **绝不阻塞 Tk 线程**：默认只读磁盘缓存；只有显式 allow_net=True 才联网。
② **绝不抛异常**：对外函数一律 try/except 兜到底，只返回降级结果。
③ **绝不打印密钥**：只输出长度与掩码，掩码只露前 8 位 + 后 4 位。

对外接口
--------
    snapshot(allow_net=False, max_age=None) -> dict   官方余额（含缓存状态）
    fetch(force=False)                      -> dict   联网抓一次并落缓存
    ledger(month=None)                      -> dict   本地账本
    reconcile(allow_net=False)              -> dict   对账明细
    alarms(allow_net=False)                 -> list   分级提醒 [{'level','text'}]
    one_line(allow_net=False)               -> str    一行（状态条）
    panel_text(allow_net=False)             -> str    多行（浮层/终端）
    rows_for_gui(allow_net=False)           -> list   [(标签, 值, 档位)]

CLI
---
    python net_balance.py              面板（优先用新鲜缓存，过期才联网）
    python net_balance.py --json       机器可读
    python net_balance.py --fetch      强制联网刷新
    python net_balance.py --offline    只读缓存与账本，绝不联网
    python net_balance.py --watch 300  每 5 分钟刷新一次
"""

from __future__ import absolute_import

import json
import os
import ssl
import sys
import time
import urllib.request
import urllib.error

# --------------------------------------------------------------------------- #
# 路径与配置
# --------------------------------------------------------------------------- #
ROOT = os.path.dirname(os.path.abspath(__file__))
WS = os.path.join(ROOT, "workspace")
CACHE_PATH = os.path.join(WS, "net_balance_cache.json")
STATE_PATH = os.path.join(WS, "net_balance_state.json")
ENV_PATH = os.path.join(ROOT, ".env")

# 数值全部可用 .env 覆盖；括号里是出厂默认
DEFAULTS = {
    "NETBAL_SHOW":        "1",     # 0 = 整个余额层静默
    "NETBAL_CACHE_TTL":   "60",    # 缓存新鲜期（秒）★ v1.2.3：90→60（一分钟一刷）
    "NETBAL_DS_WARN":     "20",    # DeepSeek 余额低于此（元）→ 偏紧
    "NETBAL_DS_CRIT":     "5",     # 低于此（元）→ 吃紧
    "NETBAL_TAVILY_WARN": "20",    # Tavily 剩余低于此（%）→ 偏紧
    "NETBAL_TAVILY_CRIT": "5",     # 低于此（%）→ 吃紧
    "NETBAL_RECON_TOL":   "3",     # 对账容差：差额小于此不报（次 / credit）
    "NETBAL_TIMEOUT":     "12",    # 单次 HTTP 超时（秒）
}

_ENV_CACHE = {"mtime": None, "data": {}}


# --------------------------------------------------------------------------- #
# 小工具：读 .env / 环境变量（不依赖 dotenv，避免启动期循环导入）
# --------------------------------------------------------------------------- #
def _read_env_file(path=None):
    """解析 .env → dict。带 mtime 缓存，文件不变就不重复读。"""
    path = path or ENV_PATH
    try:
        mtime = os.path.getmtime(path)
    except Exception:
        return {}
    if _ENV_CACHE["mtime"] == mtime:
        return _ENV_CACHE["data"]
    data = {}
    try:
        with open(path, "r", encoding="utf-8-sig", errors="replace") as f:
            for line in f:
                s = line.strip()
                if not s or s.startswith("#") or "=" not in s:
                    continue
                k, v = s.split("=", 1)
                k = k.strip()
                v = v.strip().strip('"').strip("'")
                if k:
                    data[k] = v
    except Exception:
        return {}
    _ENV_CACHE["mtime"] = mtime
    _ENV_CACHE["data"] = data
    return data


def _cfg(name, default=None):
    """配置读取顺序：进程环境变量 → .env 文件 → DEFAULTS → default。"""
    try:
        v = os.environ.get(name)
        if v is not None and str(v).strip() != "":
            return str(v).strip()
    except Exception:
        pass
    v = _read_env_file().get(name)
    if v is not None and str(v).strip() != "":
        return str(v).strip()
    if name in DEFAULTS:
        return DEFAULTS[name]
    return default


def _cfg_float(name, fallback):
    try:
        return float(_cfg(name, str(fallback)))
    except Exception:
        return float(fallback)


def _key(name, *fallbacks):
    """取密钥：进程环境变量 → .env → 其它候选键。返回 ('来源', 值)。"""
    names = (name,) + tuple(fallbacks)
    envf = _read_env_file()
    for n in names:
        try:
            v = os.environ.get(n)
            if v is not None and str(v).strip():
                return n, str(v).strip()
        except Exception:
            pass
        v = envf.get(n)
        if v:
            return n, v
    return None, ""


def mask_key(k):
    """掩码：只露前 8 位与后 4 位。绝不返回完整密钥。"""
    if not k:
        return "（空）"
    if len(k) <= 12:
        return k[:3] + "*" * max(0, len(k) - 3)
    return "%s...%s" % (k[:8], k[-4:])


def _now():
    return time.time()


def _ts_text(ts=None):
    try:
        return time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(ts or _now()))
    except Exception:
        return "?"


def _pid_alive(pid):
    """判断某个 PID 是否还活着。返回 True / False / None（判断不了）。"""
    try:
        pid = int(pid)
    except Exception:
        return None
    if pid <= 0:
        return False
    if os.name == "nt":
        try:
            import ctypes
            from ctypes import wintypes
            k32 = ctypes.WinDLL("kernel32", use_last_error=True)
            k32.OpenProcess.restype = wintypes.HANDLE
            k32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
            k32.CloseHandle.argtypes = [wintypes.HANDLE]
            # PROCESS_QUERY_LIMITED_INFORMATION = 0x1000（只要权限，不动进程）
            h = k32.OpenProcess(0x1000, False, pid)
            if not h:
                err = ctypes.get_last_error()
                if err == 5:          # ACCESS_DENIED → 进程存在但没权限
                    return True
                return False
            try:
                k32.CloseHandle(h)
            except Exception:
                pass
            return True
        except Exception:
            return None
    try:
        os.kill(pid, 0)
        return True
    except Exception:
        return False


def _fmt_elapsed(sec):
    """把秒数说成人话：45秒 / 12分 / 3小时12分。"""
    try:
        s = int(max(0, float(sec)))
    except Exception:
        return "?"
    if s < 60:
        return "%d秒" % s
    if s < 3600:
        return "%d分" % (s // 60)
    return "%d小时%d分" % (s // 3600, (s % 3600) // 60)


def _ago(ts):
    """把"多久之前"说成人话。"""
    if not ts:
        return "从未"
    d = max(0, _now() - ts)
    if d < 60:
        return "%d 秒前" % int(d)
    if d < 3600:
        return "%d 分钟前" % int(d / 60)
    if d < 86400:
        return "%d 小时前" % int(d / 3600)
    return "%d 天前" % int(d / 86400)


# --------------------------------------------------------------------------- #
# 磁盘缓存 / 基线状态（JSON，原子写；坏文件当没有）
# --------------------------------------------------------------------------- #
def _load_json(path):
    try:
        with open(path, "r", encoding="utf-8") as f:
            d = json.load(f)
        return d if isinstance(d, dict) else {}
    except Exception:
        return {}


def _save_json(path, data):
    """原子写：先写 .tmp 再 os.replace，避免半截文件。"""
    try:
        d = os.path.dirname(path)
        if d and not os.path.isdir(d):
            os.makedirs(d)
        tmp = path + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
        os.replace(tmp, path)
        return True
    except Exception:
        return False


# --------------------------------------------------------------------------- #
# HTTP（统一超时 + SSL，任何失败都变成 (None, 说明)）
# --------------------------------------------------------------------------- #
def _http_get_json(url, headers, timeout=12):
    """GET 一个 JSON。返回 (dict|None, 错误说明)。绝不抛。"""
    try:
        req = urllib.request.Request(url, headers=headers, method="GET")
        ctx = ssl.create_default_context()
        with urllib.request.urlopen(req, timeout=timeout, context=ctx) as r:
            raw = r.read().decode("utf-8", "replace")
        return json.loads(raw), ""
    except urllib.error.HTTPError as e:
        try:
            body = e.read().decode("utf-8", "replace")[:200]
        except Exception:
            body = ""
        return None, "HTTP %s %s %s" % (e.code, e.reason, body)
    except Exception as e:
        return None, "%s: %s" % (type(e).__name__, e)


def _base_url():
    return (_cfg("FATFISH_BASE_URL") or _cfg("DEEPSEEK_BASE_URL")
            or "https://api.deepseek.com").rstrip("/")


# --------------------------------------------------------------------------- #
# 官方余额：DeepSeek
# --------------------------------------------------------------------------- #
def fetch_deepseek(timeout=None):
    """GET /user/balance → 标准化 dict。"""
    timeout = int(timeout or _cfg_float("NETBAL_TIMEOUT", 12))
    src, key = _key("FATFISH_API_KEY", "DEEPSEEK_API_KEY")
    if not key:
        return {"ok": False, "error": "未找到 DeepSeek key", "source": None}
    url = _base_url() + "/user/balance"
    data, err = _http_get_json(url, {
        "Authorization": "Bearer " + key,
        "Accept": "application/json",
        "User-Agent": "FatFish-NetBalance/1.0",
    }, timeout=timeout)
    if data is None:
        return {"ok": False, "error": err, "source": src,
                "key_mask": mask_key(key)}
    infos = data.get("balance_infos") or []
    pick = None
    for b in infos:
        if str(b.get("currency", "")).upper() == "CNY":
            pick = b
            break
    if pick is None and infos:
        pick = infos[0]

    def _f(x):
        try:
            return float(x)
        except Exception:
            return 0.0

    out = {
        "ok": True,
        "error": "",
        "source": src,
        "key_mask": mask_key(key),
        "base_url": _base_url(),
        "available": bool(data.get("is_available")),
        "currency": (pick or {}).get("currency", "CNY"),
        "total": _f((pick or {}).get("total_balance")),
        "granted": _f((pick or {}).get("granted_balance")),
        "topped_up": _f((pick or {}).get("topped_up_balance")),
        "ts": _now(),
    }
    return out


# --------------------------------------------------------------------------- #
# 官方余额：Tavily
# --------------------------------------------------------------------------- #
def fetch_tavily(timeout=None):
    """GET /usage → 标准化 dict。

    两个上限必须同时看：
      account.plan_limit - plan_usage   计划池还剩多少
      key.limit          - key.usage    这把 key 还剩多少
    实际可用 = 两者取小。
    """
    timeout = int(timeout or _cfg_float("NETBAL_TIMEOUT", 12))
    src, key = _key("TAVILY_API_KEY")
    if not key:
        return {"ok": False, "error": "未找到 TAVILY_API_KEY", "source": None}
    data, err = _http_get_json("https://api.tavily.com/usage", {
        "Authorization": "Bearer " + key,
        "Accept": "application/json",
        "User-Agent": "FatFish-NetBalance/1.0",
    }, timeout=timeout)
    if data is None:
        return {"ok": False, "error": err, "source": src,
                "key_mask": mask_key(key)}

    def _i(d, k, dflt=0):
        try:
            v = (d or {}).get(k)
            return int(v) if v is not None else dflt
        except Exception:
            return dflt

    k = data.get("key") or {}
    a = data.get("account") or {}
    plan_limit = _i(a, "plan_limit")
    plan_used = _i(a, "plan_usage")
    key_limit = _i(k, "limit")
    key_used = _i(k, "usage")

    by_plan = max(0, plan_limit - plan_used) if plan_limit else None
    by_key = max(0, key_limit - key_used) if key_limit else None
    if by_plan is None and by_key is None:
        left = None
    elif by_plan is None:
        left = by_key
    elif by_key is None:
        left = by_plan
    else:
        left = min(by_plan, by_key)

    pct = None
    if plan_limit:
        pct = round(100.0 * max(0, plan_limit - plan_used) / float(plan_limit), 1)

    return {
        "ok": True,
        "error": "",
        "source": src,
        "key_mask": mask_key(key),
        "plan": a.get("current_plan") or "?",
        "plan_limit": plan_limit,
        "plan_used": plan_used,
        "key_limit": key_limit,
        "key_used": key_used,
        "left": left,
        "left_pct": pct,
        "search_usage": _i(a, "search_usage"),
        "extract_usage": _i(a, "extract_usage"),
        "crawl_usage": _i(a, "crawl_usage"),
        "map_usage": _i(a, "map_usage"),
        "research_usage": _i(a, "research_usage"),
        "paygo_usage": _i(a, "paygo_usage"),
        "paygo_limit": a.get("paygo_limit"),
        "ts": _now(),
    }


# --------------------------------------------------------------------------- #
# 缓存读写
# --------------------------------------------------------------------------- #
def _cache_age(blob=None):
    blob = blob if blob is not None else _load_json(CACHE_PATH)
    ts = blob.get("ts") or 0
    return (_now() - ts) if ts else None


def fetch(force=False, verbose=False):
    """联网抓两份余额并落缓存。force=False 且缓存新鲜时直接返回缓存。"""
    ttl = _cfg_float("NETBAL_CACHE_TTL", 60)
    old = _load_json(CACHE_PATH)
    age = _cache_age(old)
    if not force and age is not None and age <= ttl and old.get("deepseek") and old.get("tavily"):
        old["cached"] = True
        return old

    ds = fetch_deepseek()
    tv = fetch_tavily()
    blob = {
        "ts": _now(),
        "deepseek": ds,
        "tavily": tv,
        "cached": False,
    }
    _save_json(CACHE_PATH, blob)
    _update_baseline(blob)
    if verbose:
        for one in (ds, tv):
            if not one.get("ok"):
                sys.stderr.write("[net_balance] %s 抓取失败：%s\n"
                                 % (one.get("source") or "?", one.get("error")))
    return blob


_REFRESH_STATE = {"last": 0.0, "running": False, "lock": None}


def refresh_async(min_interval=None, verbose=False):
    """后台刷新一次余额（daemon 线程）。**立即返回，绝不阻塞调用方**。

    什么时候该调它：
      · 每次联网结束时（net_tools.do_network）—— "用完就报新余额"最自然；
      · 打开「网络」浮层时（chat_window._net_menu_open）—— 用户看到的是刚对过的账；
      · 跑 /net、--watch 时。

    节流：进程内 min_interval（默认 NETBAL_CACHE_TTL，60 秒）以内不重复发起，
          并且同一时刻只允许一个刷新线程在跑。所以随手多调几次也不会打出请求风暴。

    返回：True = 本次真的启动了线程；False = 被节流 / 已有线程在跑 / 线程起不来。
    """
    try:
        iv = (float(min_interval) if min_interval is not None
              else _cfg_float("NETBAL_CACHE_TTL", 60))
    except Exception:
        iv = 90.0
    now = _now()
    st = _REFRESH_STATE
    if st["running"]:
        return False
    if now - (st["last"] or 0.0) < max(5.0, iv):
        return False
    try:
        import threading
        if st["lock"] is None:
            st["lock"] = threading.Lock()
        lock = st["lock"]
    except Exception:
        return False
    if not lock.acquire(False):
        return False
    st["running"] = True
    st["last"] = now

    def _work():
        try:
            fetch(force=True, verbose=verbose)
        except Exception:
            pass
        finally:
            st["running"] = False
            try:
                lock.release()
            except Exception:
                pass

    try:
        th = threading.Thread(target=_work, name="netbal-refresh")
        th.daemon = True
        th.start()
        return True
    except Exception:
        st["running"] = False
        try:
            lock.release()
        except Exception:
            pass
        return False


def refresh_sync(min_interval=None, verbose=False):
    """同步刷新（会阻塞）。只给"启动时我确实想等它一下"的场合用，GUI 里严禁调用。"""
    try:
        iv = (float(min_interval) if min_interval is not None
              else _cfg_float("NETBAL_CACHE_TTL", 60))
    except Exception:
        iv = 90.0
    age = _cache_age()
    if age is not None and age <= iv:
        return snapshot(allow_net=False)
    return fetch(force=True, verbose=verbose)


def snapshot(allow_net=False, max_age=None):
    """读余额快照。

    allow_net=False（**GUI 默认**）→ 只读磁盘缓存，永不联网、永不阻塞。
    allow_net=True               → 缓存不新鲜就联网刷新。
    max_age                      → 覆盖新鲜期（秒）。
    """
    ttl = _cfg_float("NETBAL_CACHE_TTL", 60) if max_age is None else float(max_age)
    blob = _load_json(CACHE_PATH)
    age = _cache_age(blob)
    fresh = (age is not None and age <= ttl)
    if (not fresh) and allow_net:
        try:
            return fetch(force=True)
        except Exception:
            pass
    blob["fresh"] = fresh
    blob["age"] = age
    blob.setdefault("deepseek", {"ok": False, "error": "无缓存，尚未联网"})
    blob.setdefault("tavily", {"ok": False, "error": "无缓存，尚未联网"})
    return blob


# --------------------------------------------------------------------------- #
# 基线：为了把"官方余额变化"换算成"本期实际花费"
# --------------------------------------------------------------------------- #
def _update_baseline(blob):
    """每次联网成功后更新基线；保留上一次的值用于算差值。

    结构：
        {"first": {...本次基线...}, "prev": {...上次快照...}}
    """
    st = _load_json(STATE_PATH)
    ds = blob.get("deepseek") or {}
    tv = blob.get("tavily") or {}
    cur = {
        "ts": blob.get("ts"),
        "ds_total": ds.get("total") if ds.get("ok") else None,
        "tv_used": tv.get("plan_used") if tv.get("ok") else None,
        "tv_left": tv.get("left") if tv.get("ok") else None,
    }
    if not st.get("first"):
        st["first"] = cur
    st["prev"] = st.get("cur") or cur
    st["cur"] = cur
    _save_json(STATE_PATH, st)
    # ★ 2026-10-18：顺手把「本轮已用」的基线一起推进
    try:
        _sync_session(blob)
    except Exception:
        pass
    return st


# --------------------------------------------------------------------------- #
# 本轮已用（session）
# --------------------------------------------------------------------------- #
# 「本轮」= 本次主程序运行。口径只有一个，而且是最硬的那个：
#
#       本轮已用 = 启动那一刻的余额  -  现在的余额
#
# 为什么不用「token 数 × 单价」自己算：
#   · 主模型、审查员、搜索后端、AI 路由各自单价不同，漏一项就少算；
#   · 单价随时会调，硬编码迟早对不上；
#   · 而余额是官方结算后的真值 —— 差多少就是真花了多少，一分不差。
#
# 已知精度边界：官方 /user/balance 只给 2 位小数，
#   所以单次小额消耗（< 0.005 元）可能暂时不体现，累计到 0.01 就会显现。
#   这是接口本身的粒度，不是四舍五入的偷懒。
#
# 充值检测：若余额突然变多（> 基线 + 0.005），说明充值了，
#   直接把基线抬到新余额，本轮已用归零 —— 否则会显示成负数或荒谬的大数。
SESSION_PATH = os.path.join(WS, "net_balance_session.json")
_SESSION = {}


def start_session(force=False):
    """建立本轮基线。主程序启动时调一次（lazy 也可，见 session_info）。

    · 同一 PID 且已有有效基线 → 沿用（不重复重置）
    · 否则用**当前缓存余额**先立一个临时基线（provisional），
      再起后台线程抓真值；抓到后自动固化（见 _sync_session）。
      这样启动时不会为了查余额而卡住，显示也不会空着。
    """
    global _SESSION
    pid = os.getpid()
    try:
        snap = snapshot(allow_net=False)
    except Exception:
        snap = {}
    total = (snap.get("deepseek") or {}).get("total")

    st = _load_json(SESSION_PATH)
    if (not force) and st and st.get("pid") == pid \
            and st.get("base_total") is not None:
        _SESSION = st
        return st

    st = {
        "pid": pid,
        "started_at": _now(),
        "base_total": (float(total) if total is not None else None),
        "base_ts": (_now() if total is not None else None),
        # provisional=True 表示这条基线来自缓存快照，还没和官方对过
        "provisional": total is not None,
        "last_total": (float(total) if total is not None else None),
        "last_ts": _now(),
        "recharges": 0,
    }
    _SESSION = st
    _save_json(SESSION_PATH, st)
    try:
        refresh_async(min_interval=0)      # 立刻去抓真基线（后台）
    except Exception:
        pass
    return st


def _sync_session(blob):
    """余额刷新成功后调用：固化基线 / 记录最新值 / 检测充值。"""
    global _SESSION
    if not _SESSION:
        return
    ds = (blob or {}).get("deepseek") or {}
    if not ds.get("ok"):
        return
    total = ds.get("total")
    if total is None:
        return
    total = float(total)
    st = _SESSION
    base = st.get("base_total")
    ts = (blob or {}).get("ts") or _now()

    if base is None or st.get("provisional"):
        # 还没真基线（或只是缓存猜的）→ 用刚抓到的官方值固化
        st["base_total"] = total
        st["base_ts"] = ts
        st["provisional"] = False
    elif total > float(base) + 0.005:
        # 余额变多了 = 充值了 → 基线抬到新值，本轮从这一刻重新算
        st["recharges"] = int(st.get("recharges") or 0) + 1
        st["base_total"] = total
        st["base_ts"] = ts

    st["last_total"] = total
    st["last_ts"] = ts
    _save_json(SESSION_PATH, st)


def session_info(allow_foreign=False):
    """本轮的账。**纯内存读取**，可直接放进 GUI 定时器里每几百毫秒调一次。

    allow_foreign=False（默认，GUI 用）
        只认「本进程」的基线。PID 对不上就拒答 —— 绝不拿别人的账冒充我的。
    allow_foreign=True（CLI 用）
        允许读别的进程留下的基线，并标注 owner 是谁、是否还活着。
        目的是让终端里跑 `python net_balance.py` 也能看到正在运行的主程序那一轮。
    """
    global _SESSION
    if not _SESSION:
        _SESSION = _load_json(SESSION_PATH)
    st = _SESSION or {}
    out = {"ok": False, "spent": 0.0, "base": None, "current": None,
           "elapsed": 0.0, "provisional": False, "recharges": 0,
           "started_at": None, "reason": "", "foreign": False,
           "pid": None, "owner_alive": None}
    if not st:
        out["reason"] = "尚未建立基线"
        return out
    _spid = int(st.get("pid") or -1)
    if _spid != os.getpid():
        if not allow_foreign:
            # PID 变了 = 不是本轮，不该拿旧基线冒充
            out["reason"] = "基线属于旧进程（PID %s）" % st.get("pid")
            return out
        out["foreign"] = True
        out["pid"] = _spid
        out["owner_alive"] = _pid_alive(_spid)
    base = st.get("base_total")
    cur = st.get("last_total")
    if base is None or cur is None:
        out["reason"] = "等待首次余额（基线待定）"
        out["provisional"] = bool(st.get("provisional"))
        out["started_at"] = st.get("started_at")
        return out
    spent = float(base) - float(cur)
    out.update({
        "ok": True,
        "spent": round(max(0.0, spent), 2),
        "base": round(float(base), 2),
        "current": round(float(cur), 2),
        "elapsed": max(0.0, _now() - float(st.get("started_at") or _now())),
        "provisional": bool(st.get("provisional")),
        "recharges": int(st.get("recharges") or 0),
        "started_at": st.get("started_at"),
    })
    return out


def reset_session():
    """把本轮基线重设为「现在」。用于"我想从这一刻重新开始算"。"""
    return start_session(force=True)


def spent_line(short=False):
    """状态栏用的那一小行。拿不到就返回空串（调用方据此保持原样）。

    精度说明：余额只有 2 位小数，所以这里也显示 2 位。
    """
    i = session_info()
    if not i.get("ok"):
        return ""
    v = i.get("spent") or 0.0
    if short:
        return "%.2f元" % v
    return "本轮已用 %.2f元" % v


# --------------------------------------------------------------------------- #
# 本地账本
# --------------------------------------------------------------------------- #
def _ds_module():
    """拿到 deepseek_search 包（与主程序共用同一份，别重复 import）。"""
    try:
        import net_tools as _nt
        fn = getattr(_nt, "_load_ds", None)
        if fn is not None:
            m = fn()
            if m is not None:
                return m
    except Exception:
        pass
    try:
        if WS not in sys.path:
            sys.path.insert(0, WS)
        import deepseek_search as _ds
        return _ds
    except Exception:
        return None


def ledger(month=None):
    """本地账本（quality.db）。返回 quota.usage() 原样结构，失败返回 {}。"""
    ds = _ds_module()
    if ds is None:
        return {}
    try:
        return ds.quota.usage(month=month) or {}
    except Exception:
        return {}


def month_label(month=None):
    ds = _ds_module()
    if ds is None:
        return "?"
    try:
        return ds.quota.month_bounds(month)[2]
    except Exception:
        return "?"


# --------------------------------------------------------------------------- #
# 对账：官方口径 vs 本地账本口径
# --------------------------------------------------------------------------- #
def reconcile(allow_net=False, snap=None):
    """把两套口径摆在一起算差额。

    Tavily  → 官方"本月已用 credit" - 本地账本"本月记的 units" = 未记账消耗
    DeepSeek→ 官方余额差（上次快照 → 本次） - 账本同期花费 = 未记账花费
              （首次运行没有历史基线，只能给余额，不做差额判定）
    """
    snap = snap if snap is not None else snapshot(allow_net=allow_net)
    led = ledger()
    tol = _cfg_float("NETBAL_RECON_TOL", 3)
    out = {"month": month_label(), "tolerate": tol, "items": [], "notes": []}

    # ---------- Tavily ----------
    tv = snap.get("tavily") or {}
    lu = led.get("tavily") or {}
    if tv.get("ok"):
        official = int(tv.get("plan_used") or 0)
        local = int(round(float(lu.get("units") or 0)))
        delta = official - local
        out["items"].append({
            "backend": "tavily",
            "official": official,
            "local": local,
            "delta": delta,
            "unit": "credit",
            "verdict": ("一致" if abs(delta) <= tol
                        else ("官方多 %d（账本外消耗）" % delta if delta > 0
                              else "账本多 %d（可能跨月/重复记）" % (-delta))),
        })
    else:
        out["notes"].append("Tavily 官方用量未取到：%s" % (tv.get("error") or "?"))
        out["items"].append({"backend": "tavily", "official": None,
                             "local": int(round(float(lu.get("units") or 0))),
                             "delta": None, "unit": "credit",
                             "verdict": "官方不可用，仅本地"})

    # ---------- DeepSeek ----------
    ds = snap.get("deepseek") or {}
    lu2 = led.get("deepseek") or {}
    local_cost = float(lu2.get("cost_yuan") or 0.0)
    st = _load_json(STATE_PATH)
    prev = st.get("prev") or {}
    prev_total = prev.get("ds_total")
    cur_total = ds.get("total") if ds.get("ok") else None
    if ds.get("ok") and prev_total is not None and cur_total is not None:
        spent = round(float(prev_total) - float(cur_total), 4)
        # 账本同期花费无法精确切段，退化为"本月累计"参照，只提示不判错
        out["items"].append({
            "backend": "deepseek",
            "official": round(float(cur_total), 2),
            "local": round(local_cost, 4),
            "delta": spent,
            "unit": "元",
            "verdict": "两次快照间余额减少 %.4f 元（账本本月累计 %.4f 元）"
                       % (spent, local_cost),
        })
    elif ds.get("ok"):
        out["items"].append({
            "backend": "deepseek",
            "official": round(float(cur_total or 0), 2),
            "local": round(local_cost, 4),
            "delta": None, "unit": "元",
            "verdict": "首次快照，仅余额（差额从下次起可算）",
        })
    else:
        out["notes"].append("DeepSeek 余额未取到：%s" % (ds.get("error") or "?"))
        out["items"].append({"backend": "deepseek", "official": None,
                             "local": round(local_cost, 4), "delta": None,
                             "unit": "元", "verdict": "官方不可用，仅本地"})
    return out


# --------------------------------------------------------------------------- #
# 分级：充足 / 偏紧 / 吃紧 / 见底
# --------------------------------------------------------------------------- #
LEVEL_ICON = {"ok": "✅", "warn": "🟡", "crit": "🔴", "dead": "⛔", "unknown": "⚪"}
LEVEL_TEXT = {"ok": "充足", "warn": "偏紧", "crit": "吃紧", "dead": "见底",
              "unknown": "未取到"}


def _ds_level(ds):
    if not ds.get("ok"):
        return "unknown", ""
    total = float(ds.get("total") or 0)
    warn = _cfg_float("NETBAL_DS_WARN", 20)
    crit = _cfg_float("NETBAL_DS_CRIT", 5)
    if total <= 0:
        return "dead", "余额已耗尽，联网搜索会直接报错"
    if total < crit:
        return "crit", "余额仅剩 %.2f 元，随时可能扣光" % total
    if total < warn:
        return "warn", "余额 %.2f 元，低于警戒线 %.0f 元" % (total, warn)
    return "ok", ""


def _tv_level(tv):
    if not tv.get("ok"):
        return "unknown", ""
    left = tv.get("left")
    limit = tv.get("plan_limit") or 0
    if left is None:
        return "unknown", ""
    if limit:
        pct = 100.0 * float(left) / float(limit)
    else:
        pct = float(tv.get("left_pct") or 0)
    warn = _cfg_float("NETBAL_TAVILY_WARN", 20)
    crit = _cfg_float("NETBAL_TAVILY_CRIT", 5)
    if left <= 0:
        return "dead", "Tavily credit 已用尽，这个后端会直接 4xx"
    if pct < crit:
        return "crit", "Tavily 仅剩 %.0f%%，约 %d credit" % (pct, left)
    if pct < warn:
        return "warn", "Tavily 剩 %.0f%%，约 %d credit" % (pct, left)
    return "ok", ""


def grade(allow_net=False, snap=None):
    """两边各给一个档位：{'deepseek': (level, text), 'tavily': (...)}"""
    snap = snap if snap is not None else snapshot(allow_net=allow_net)
    return {
        "deepseek": _ds_level(snap.get("deepseek") or {}),
        "tavily": _tv_level(snap.get("tavily") or {}),
    }


def alarms(allow_net=False, snap=None):
    """只返回需要提醒的条目（warn / crit / dead）。足够安静，可以随便调用。"""
    if _cfg("NETBAL_SHOW", "1") in ("0", "false", "False", "off"):
        return []
    snap = snap if snap is not None else snapshot(allow_net=allow_net)
    g = grade(snap=snap)
    out = []
    for name, label in (("deepseek", "DeepSeek"), ("tavily", "Tavily")):
        lvl, msg = g.get(name) or ("unknown", "")
        if lvl in ("warn", "crit", "dead"):
            out.append({"level": lvl, "backend": name,
                        "text": "%s %s" % (label, msg)})
    rec = reconcile(allow_net=False, snap=snap)
    for it in rec.get("items") or []:
        d = it.get("delta")
        if d is not None and abs(d) > rec.get("tolerate", 3):
            if it["backend"] == "tavily" and d > 0:
                out.append({"level": "info", "backend": "tavily",
                            "text": "对账：Tavily 官方比账本多 %d credit（账本外消耗）" % d})
    return out


# --------------------------------------------------------------------------- #
# 渲染
# --------------------------------------------------------------------------- #
def _fmt_money(v):
    try:
        return "¥%.2f" % float(v)
    except Exception:
        return "?"


def one_line(allow_net=False, snap=None):
    """压到一行，塞进状态条/流水的收尾行。"""
    snap = snap if snap is not None else snapshot(allow_net=allow_net)
    g = grade(snap=snap)
    ds = snap.get("deepseek") or {}
    tv = snap.get("tavily") or {}
    bits = []
    if ds.get("ok"):
        bits.append("DS %s%s" % (_fmt_money(ds.get("total")), LEVEL_ICON[g["deepseek"][0]]))
    else:
        bits.append("DS ?")
    if tv.get("ok"):
        bits.append("TV %s/%s%s" % (tv.get("plan_used"), tv.get("plan_limit"),
                                    LEVEL_ICON[g["tavily"][0]]))
    else:
        bits.append("TV ?")
    bits.append("(%s)" % _ago(snap.get("ts")))
    return "余额：" + "  ".join(bits)


def panel_text(allow_net=False, snap=None, tty=False):
    """多行面板：终端与浮层共用一份逻辑，宽度控制在 40 字内。"""
    snap = snap if snap is not None else snapshot(allow_net=allow_net)
    ds = snap.get("deepseek") or {}
    tv = snap.get("tavily") or {}
    g = grade(snap=snap)
    led = ledger()
    rec = reconcile(allow_net=False, snap=snap)
    L = []
    L.append("🌐 网络与额度 · %s" % _ts_text())
    L.append("─" * 30)

    lvl, _ = g["deepseek"]
    if ds.get("ok"):
        L.append("DeepSeek  %s  %s" % (_fmt_money(ds.get("total")), LEVEL_ICON[lvl]))
        L.append("  赠送 %.2f · 充值 %.2f" % (ds.get("granted") or 0,
                                             ds.get("topped_up") or 0))
    else:
        L.append("DeepSeek  ⚪ %s" % (ds.get("error") or "未取到")[:24])

    lvl2, _ = g["tavily"]
    if tv.get("ok"):
        L.append("Tavily    %s/%s 剩 %s  %s"
                 % (tv.get("plan_used"), tv.get("plan_limit"),
                    tv.get("left"), LEVEL_ICON[lvl2]))
        L.append("  %s · 搜%d 抓%d"
                 % (tv.get("plan"), tv.get("search_usage"), tv.get("extract_usage")))
    else:
        L.append("Tavily    ⚪ %s" % (tv.get("error") or "未取到")[:24])

    L.append("─" * 30)
    parts = []
    for name in sorted(led):
        u = led[name] or {}
        if u.get("free_monthly"):
            parts.append("%s %d" % (str(u.get("label") or name).split(" ")[0],
                                    int(u.get("units") or 0)))
        elif u.get("cost_yuan"):
            parts.append("%s %.3f元" % (str(u.get("label") or name).split(" ")[0],
                                        float(u.get("cost_yuan") or 0)))
    L.append("本月账本  %s" % (" · ".join(parts) if parts else "无记录"))
    for it in rec.get("items") or []:
        _nm = {"tavily": "Tavily", "deepseek": "DeepSeek"}.get(
            it["backend"], it["backend"])
        L.append("对账 %s｜%s" % (_nm, it["verdict"]))
    al = alarms(snap=snap)
    for a in al:
        L.append("%s %s" % (LEVEL_ICON.get(a["level"], "•"), a["text"]))
    L.append("─" * 30)
    # ★ 2026-10-18：本轮已用（账户余额差，见 session_info 的说明）
    try:
        _si = session_info(allow_foreign=True)
        if _si.get("ok"):
            _who = ""
            if _si.get("foreign"):
                _alive = _si.get("owner_alive")
                _who = "（主程序 PID %s%s）" % (
                    _si.get("pid"), "" if _alive is not False else "，已结束")
            L.append("本轮已用 %.2f元（已运行 %s）%s"
                     % (_si["spent"], _fmt_elapsed(_si.get("elapsed")), _who))
        elif _si.get("reason"):
            L.append("本轮已用 —（%s）" % _si["reason"])
    except Exception:
        pass
    L.append("数据 %s" % _ago(snap.get("ts")))
    return "\n".join(L)


def rows_for_gui(allow_net=False, snap=None):
    """给 InlineMenu 之类用的紧凑结构：[(标签, 值, 档位), ...]。"""
    snap = snap if snap is not None else snapshot(allow_net=allow_net)
    ds = snap.get("deepseek") or {}
    tv = snap.get("tavily") or {}
    g = grade(snap=snap)
    rows = []
    if ds.get("ok"):
        rows.append(("DeepSeek", _fmt_money(ds.get("total")), g["deepseek"][0]))
    else:
        rows.append(("DeepSeek", "未取到", "unknown"))
    if tv.get("ok"):
        rows.append(("Tavily", "%s/%s" % (tv.get("plan_used"), tv.get("plan_limit")),
                     g["tavily"][0]))
        rows.append(("Tavily剩", str(tv.get("left")), g["tavily"][0]))
    else:
        rows.append(("Tavily", "未取到", "unknown"))
    rows.append(("更新", _ago(snap.get("ts")), "info"))
    return rows


# --------------------------------------------------------------------------- #
# CLI
# --------------------------------------------------------------------------- #
def main(argv=None):
    argv = list(sys.argv[1:] if argv is None else argv)
    as_json = "--json" in argv
    offline = "--offline" in argv
    force = "--fetch" in argv
    watch = 0
    if "--watch" in argv:
        try:
            watch = int(argv[argv.index("--watch") + 1])
        except Exception:
            watch = 300

    def once():
        if force and not offline:
            snap = fetch(force=True)
        else:
            snap = snapshot(allow_net=not offline)
        if as_json:
            out = {
                "snapshot": {k: v for k, v in snap.items() if k != "cached"},
                "ledger": ledger(),
                "reconcile": reconcile(snap=snap),
                "alarms": alarms(snap=snap),
                "grade": {k: v[0] for k, v in grade(snap=snap).items()},
                "session": session_info(),
            }
            sys.stdout.write(json.dumps(out, ensure_ascii=False, indent=2) + "\n")
        else:
            sys.stdout.write(panel_text(snap=snap) + "\n")
        try:
            sys.stdout.flush()
        except Exception:
            pass

    if watch <= 0:
        once()
        return 0
    try:
        while True:
            try:
                sys.stdout.write("\033[2J\033[H")   # 清屏（终端才有效）
            except Exception:
                pass
            once()
            time.sleep(max(10, watch))
    except KeyboardInterrupt:
        return 0
    return 0


if __name__ == "__main__":
    sys.exit(main())
