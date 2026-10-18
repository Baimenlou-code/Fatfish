import io
import json
import logging
import os
import re
import sys
from datetime import datetime

import requests

log = logging.getLogger(__name__)


# ============ 模块状态 ============
TAVILY_API_KEY = ""
TAVILY_MODE    = "auto"     # "search" / "extract" / "auto" / "both"
                            #   默认 auto：交给上层决定（AI 核验模式会自动选 search/extract）
                            #   显式设为 search/extract 时为「锁定」，AI 不再决定模式
NET_MODE       = "auto"     # "auto" / "on" / "off"
EXTRACT_MAX_LEN = 8000      # extract 单段最大字符数，超出则分段


def set_api_key(key):
    """注入 Tavily API key。会自动 strip 首尾空白。"""
    global TAVILY_API_KEY
    TAVILY_API_KEY = (key or "").strip()


# ============ 搜索后端：多选 / 审核 AI 决定 / 质量台账（2026-10-18 新增）============
# 为什么加这层：
#   以前写死 Tavily 一家。但不同后端各有盲区（官方强在中文官方文档、Tavily 强在英文技术文），
#   单吊一家必然有漏。这里把「用哪几个」变成可配置的多选，交给审核 AI 按问题类型和
#   历史质量数据决定；每次实际检索都记台账，后续能据此调偏好。
#
# 三个环境变量：
#   NET_BACKENDS        ""            → 不启用，完全走原来的 Tavily 路径（默认，零行为变化）
#                       "auto"        → 启用，候选 = 所有配了 key 的后端
#                       "deepseek,tavily" → 启用，候选 = 这两个（多选）
#   NET_BACKEND_ROUTER  "rule"（默认，按偏好+问题特征）/ "ai"（审核 AI 决定）/ "off"（固定不改）
#   NET_MERGE           "rrf"（默认，按名次融合，跨后端稳）/ "score" / "none"
#
# 与旧路径的关系：
#   NET_BACKENDS 为空时 web_search/web_extract 原样走 _web_search_tavily/_web_extract_tavily，
#   连日志措辞都不变；只有显式启用后才走多后端。想回滚只需清空这一个变量。
NET_BACKENDS = ""
NET_BACKEND_ROUTER = "rule"
NET_MERGE = "rrf"

_pending_backends = []      # 审核 AI 已经决定好的后端（由 verify_tools 写入，本模块消费后清空）
last_backends = []          # 上一次实际用到的后端
last_backend_log = []       # 上一次每个后端的执行情况

_DS = None                  # 延迟导入的 deepseek_search 包
_DS_FAILED = False


def _load_ds():
    """延迟导入 workspace/deepseek_search（失败则返回 None，调用方自动回退旧路径）。

    延迟导入的理由：主程序启动时不该因为一个可选依赖拖慢/拖挂；
    而且没装这个包的老部署也应该照常跑 Tavily。
    """
    global _DS, _DS_FAILED
    if _DS is not None or _DS_FAILED:
        return _DS
    try:
        here = os.path.dirname(os.path.abspath(__file__))
        pkg_dir = os.path.join(here, "workspace")
        if os.path.isdir(pkg_dir) and pkg_dir not in sys.path:
            sys.path.insert(0, pkg_dir)
        import deepseek_search as _pkg
        _DS = _pkg
    except Exception as exc:
        _DS_FAILED = True
        log.info("[%s] deepseek_search 不可用，回退 Tavily：%s" % (_ts(), exc))
    return _DS


def set_backends(spec):
    """设置后端多选。

    spec: "auto" / "deepseek,tavily" / "deepseek" / ["deepseek","tavily"] / "" / None
          ""/None = 关闭多后端（走旧 Tavily 路径）
    """
    global NET_BACKENDS
    if spec is None:
        NET_BACKENDS = ""
        return ""
    if isinstance(spec, (list, tuple)):
        spec = ",".join(str(x).strip() for x in spec if str(x).strip())
    NET_BACKENDS = str(spec).strip()
    return NET_BACKENDS


def backends_spec():
    """解析 NET_BACKENDS：返回 None=auto（全部可用），[]=未启用，[names]=指定多选。"""
    raw = (NET_BACKENDS or "").strip()
    if not raw:
        return []
    if raw.lower() in ("auto", "all", "*"):
        return None
    return [x.strip() for x in re.split(r"[,，、\s]+", raw) if x.strip()]


def set_pending_backends(names):
    """审核 AI 判定后写入「本次用哪几个后端」。do_network 消费后会清空。"""
    global _pending_backends
    if not names:
        _pending_backends = []
        return []
    if isinstance(names, str):
        names = re.split(r"[,，、\s]+", names)
    _pending_backends = [str(x).strip() for x in names if str(x).strip()]
    return _pending_backends


def take_pending_backends():
    """取出（并清空）审核 AI 的决定。一次联网只消费一次，不会污染下一次。"""
    global _pending_backends
    got, _pending_backends = _pending_backends, []
    return got


def _use_multi(want_extract=False):
    """是否走多后端路径。未配置时恒为 False → 行为与旧版完全一致。

    注意这里必须用 `== []` 而不是 `not spec`：
        backends_spec() 返回 None 表示 auto（全部可用），返回 [] 表示未启用；
        而 `not None` 也是 True —— 用 `not spec` 会把 auto 误判成未启用。
    """
    if backends_spec() == []:        # 只有空列表才算"未启用"
        return False
    if _load_ds() is None:
        return False
    return True


# ============ 联网流水钩子（2026-10-18 新增）============
#   用途：把「每次联网用了哪个后端、几条结果、耗时多少、成没成」推给对话窗口的
#        顶栏〔联网〕菜单（chat_window.py 的 _net_on_activity → 数据层）。
#
#   为什么做成"钩子"而不是直接 import chat_window：
#     本模块既被主程序调用，也被 CLI 单独拉起（那时根本没有 GUI）。
#     直接 import 会让 CLI 也去摸 tkinter；做成可空钩子，没有监听方就是零成本。
#
#   ★ 红线：钩子是**纯观察层**，它抛的任何异常都必须在 _fire_net_hook 里吞掉。
#     联网是主功能，流水只是好看的副产物 —— 副产物绝不许反噬主功能。
_NET_HOOK = None


def set_net_hook(fn):
    """注册联网流水回调（fn(dict) 或 None 取消）。返回原先注册的函数。"""
    global _NET_HOOK
    old = _NET_HOOK
    _NET_HOOK = fn
    return old


def _fire_net_hook(info):
    """安全地打一次点：钩子抛任何异常都只记日志，绝不影响调用方。"""
    fn = _NET_HOOK
    if fn is None:
        return
    try:
        fn(info)
    except Exception as exc:
        try:
            log.info("[%s] 联网流水钩子异常（已忽略）：%s" % (_ts(), exc))
        except Exception:
            pass


# ---- 审核 AI 路由：一次调用同时决定"用哪几个"和"预期质量" ----
ROUTER_SYSTEM = (
    "你是搜索后端调度员。给你一个问题、若干可选搜索后端（含各自的擅长/弱项/成本/"
    "历史质量台账），你要决定这次用哪几个后端去搜。\n"
    "挑选原则：\n"
    "1. 中文官方文档、国内话题优先 deepseek；英文技术深度加一个 tavily 或 serper；\n"
    "2. 需要读原文细节时，只选支持抓正文的后端；\n"
    "3. 简单事实题只选 1 个；复杂或多源核验才选 2~3 个（每多一个多一次计费）；\n"
    "4. 台账里质量分明显偏低、或最近多次失败的后端，除非有特殊理由否则别选。\n"
    "输出格式（严格照这三行，不要多余内容）：\n"
    "后端：a,b\n"
    "理由：一句话说明为什么选这几个\n"
    "评分：a=4,b=3    ← 你对各后端在【这类问题】上的预期质量打分(1~5)，用于持续校准偏好\n"
)

_SCORE_RE = re.compile(r"([A-Za-z_]+)\s*[=＝]\s*([0-9.]+)")
_SCORE_LINE_RE = re.compile(r"^\s*(?:评分|质量分|scores?)\s*[：:]\s*(.+)$", re.M | re.I)


def _route_by_ai(question, want_extract=False):
    """让审核 AI（verify_tools 的那套 client）决定后端。

    返回 {"backends": [...], "why": "...", "scores": {...}} 或 None（失败/不可用）。
    失败一律返回 None，由调用方退回规则决策 —— 路由是增强，绝不能成为故障点。
    """
    ds = _load_ds()
    if ds is None:
        return None
    spec = backends_spec()
    try:
        avail = ds.router.available_backends(spec)
    except Exception:
        return None
    if not avail:
        return None

    try:
        import verify_tools
    except Exception as exc:
        log.info("[%s] 审核 AI 路由不可用（verify_tools 导入失败）：%s" % (_ts(), exc))
        return None

    # 独立 CLI 场景下 verify_tools 可能还没配 key，这里用 .env 兜一次
    try:
        if not getattr(verify_tools, "API_KEY", ""):
            k, _ = load_env_key(None, "DEEPSEEK_API_KEY")
            if k:
                verify_tools.configure(api_key=k)
    except Exception:
        pass

    try:
        block = ds.router.prompt_block(configured=spec, avail=avail)
    except Exception:
        block = ""

    user_prompt = "%s\n\n【本次问题】\n%s" % (block, (question or "")[:1500])
    if want_extract:
        user_prompt += "\n\n（注意：本次需要读取网页正文，请只选支持抓正文的后端）"

    try:
        text = verify_tools._chat([
            {"role": "system", "content": ROUTER_SYSTEM},
            {"role": "user", "content": user_prompt},
        ])
    except Exception as exc:
        log.info("[%s] 审核 AI 路由失败，回退规则：%s" % (_ts(), exc))
        return None

    res = ds.router.parse_route(text or "", avail)
    if not res or not res.get("backends"):
        log.info("[%s] 审核 AI 路由输出无法解析，回退规则：%s"
                 % (_ts(), (text or "")[:120].replace("\n", " ")))
        return None

    # ---- 把 AI 的预期评分记进台账（kind='judge'，不污染实测权重，但会进下次提示词）----
    scores = {}
    try:
        m = _SCORE_LINE_RE.search(text or "")
        if m:
            for name, val in _SCORE_RE.findall(m.group(1)):
                name = name.strip().lower()
                if name in res["backends"]:
                    try:
                        scores[name] = max(1.0, min(5.0, float(val)))
                    except Exception:
                        pass
        for name, val in scores.items():
            ds.quality.record_judgement(
                name, val,
                note="[AI路由] " + (res.get("why") or "")[:160],
                question=question or "")
    except Exception:
        pass

    res["scores"] = scores
    log.info("[%s] 审核 AI 路由：%s（%s）%s"
             % (_ts(), ",".join(res["backends"]), (res.get("why") or "")[:80],
                (" 评分 " + ",".join("%s=%.0f" % kv for kv in scores.items())) if scores else ""))
    return res


def _resolve_backends(question="", want_extract=False):
    """决定本次用哪些后端：审核 AI 已给 → 用它的；否则按 router 策略。"""
    global last_backends
    ds = _load_ds()
    if ds is None:
        return []
    pending = take_pending_backends()
    if pending:
        # 审核 AI 已经决定过（一次 AI 调用同时判"要不要搜"和"用哪个"，最省）
        try:
            avail = ds.router.available_backends(backends_spec())
        except Exception:
            avail = []
        picked = [n for n in pending if not avail or n in avail]
        if picked:
            last_backends = picked[:3]
            return last_backends
    # 由本模块现场问一次审核 AI（NET_BACKEND_ROUTER=ai 时）
    if (NET_BACKEND_ROUTER or "").strip().lower() == "ai":
        got = _route_by_ai(question, want_extract=want_extract)
        if got and got.get("backends"):
            last_backends = list(got["backends"])[:3]
            return last_backends
    try:
        res = ds.router.decide(
            question or "",
            configured=backends_spec(),
            router=NET_BACKEND_ROUTER,
            want_extract=want_extract,
        )
        last_backends = list(res.get("backends") or [])[:3]
        if last_backends:
            log.info("[%s] 联网后端决策：%s（by %s%s）| %s"
                     % (_ts(), ",".join(last_backends), res.get("by"),
                        "，缓存" if res.get("cached") else "", res.get("why", "")[:120]))
        return last_backends
    except Exception as exc:
        log.info("[%s] 后端决策失败，回退 Tavily：%s" % (_ts(), exc))
        return []


def _entry_name(item):
    """把一个融合结果压缩成一行"谁找到的"。"""
    found = item.get("found_by") or []
    return "+".join(found) if found else "?"


def _format_multi_search(items, backends):
    """把融合结果渲染成与旧 Tavily 版同风格的文本（主模型读起来无差别）。"""
    if not items:
        return "（未搜到相关结果）"
    lines = []
    if len(backends) > 1:
        lines.append("【多源融合】后端：%s（RRF 排序，[] 内为命中来源）\n"
                     % "、".join(backends))
    for i, r in enumerate(items, 1):
        title = r.get("title") or "（无标题）"
        body = (r.get("snippet") or r.get("content") or "").strip()
        if len(body) > 600:
            body = body[:600] + "…"
        url = r.get("url", "")
        tags = []
        if r.get("published"):
            tags.append(str(r["published"]))
        score = r.get("score")
        if isinstance(score, (int, float)):
            tags.append("相关度 %.1f" % score)
        auth = r.get("authority")
        if isinstance(auth, int) and not isinstance(auth, bool) and auth >= 0:
            tags.append("权威 %d/4" % auth)
        if len(backends) > 1:
            tags.append("来源 " + _entry_name(r))
        tail = ("　［" + "｜".join(tags) + "］") if tags else ""
        lines.append("%d. %s%s\n   %s\n   来源：%s" % (i, title, tail, body, url))
    return "\n".join(lines)


def _search_via_multi(query, max_results=5, want_extract=False):
    """走多后端检索。失败时返回带错误说明的文本（绝不抛给调用方）。"""
    global last_backend_log
    ds = _load_ds()
    if ds is None:
        return "（未配置 TAVILY_API_KEY，无法联网）"
    backends = _resolve_backends(question=query, want_extract=want_extract)
    if not backends:
        # 决策为空 → 退回旧路径，保证永远有结果
        return _web_search_tavily(query, max_results=max_results)
    try:
        mp = ds.MultiProvider(backends=backends, merge=NET_MERGE)
        # 融合需要每个后端多给几条才有排序空间，但最终输出仍尊重调用方的 max_results
        want = max(1, int(max_results or 5))
        raw = mp.search(query, top_k=max(want * 2, 8), ori_question=query)
        items = raw[:want]
        last_backend_log = list(mp.last_log)
        ok_n = sum(1 for l in last_backend_log if l["ok"])
        log.info("[%s] 多后端检索 %s：%d 条（成功 %d/%d 个后端）"
                 % (_ts(), ",".join(backends), len(items), ok_n, len(last_backend_log)))
        if not items:
            errs = [l["error"] for l in last_backend_log if not l["ok"]]
            return ("（未搜到相关结果）" + ("；后端报错：" + "; ".join(errs[:2]) if errs else ""))
        return _format_multi_search(items, backends)
    except Exception as exc:
        log.info("[%s] 多后端检索失败（%s）：%s" % (_ts(), ",".join(backends), exc))
        return "（搜索出错：%s）" % exc


def _extract_via_multi(urls, max_results=5):
    """走多后端抓正文：自动挑一个支持 fetch 的后端。"""
    ds = _load_ds()
    if ds is None:
        return "（未配置 TAVILY_API_KEY，无法联网）"
    if not urls:
        return "（没有可抓取的 URL）"
    backends = _resolve_backends(question=(urls or [""])[0], want_extract=True)
    if not backends:
        return _web_extract_tavily(urls, max_results=max_results)
    try:
        mp = ds.MultiProvider(backends=backends)
        docs = mp.extract(list(urls)[:max_results])
    except Exception as exc:
        log.info("[%s] 多后端抓取失败（%s）：%s" % (_ts(), ",".join(backends), exc))
        return "（抓取出错：%s）" % exc
    if not docs:
        return "（未抓到内容）"
    lines = []
    for i, d in enumerate(docs, 1):
        content = (d.get("content") or "").strip()
        url = d.get("url", "")
        if not content:
            lines.append("%d. %s\n（无正文）" % (i, url))
            continue
        if len(content) <= EXTRACT_MAX_LEN:
            lines.append("%d. %s\n%s" % (i, url, content))
        else:
            chunks = [content[j:j + EXTRACT_MAX_LEN]
                      for j in range(0, len(content), EXTRACT_MAX_LEN)]
            for k, ch in enumerate(chunks, 1):
                lines.append("%d.%d %s（第%d/%d段）\n%s"
                             % (i, k, url, k, len(chunks), ch))
    log.info("[%s] 多后端抓取成功：%d 个 URL（后端 %s）" % (_ts(), len(docs), ",".join(backends)))
    return "\n\n".join(lines)


def _balance_tail():
    """余额尾巴：官方余额 + 本月账本 + 对账（2026-10-18 新增）。

    ★ 只读 net_balance 的**磁盘缓存**（allow_net=False），绝不联网、绝不阻塞。
      缓存由 net_balance.fetch() 在开机 / 手动 --fetch / 后台刷新时写入。
    ★ 任何异常都吞掉：余额是"锦上添花"，绝不能让它拖垮 /net 本体。
    """
    if os.environ.get("NETBAL_SHOW", "1") in ("0", "false", "False", "off"):
        return ""
    try:
        import net_balance as _nb
        # ★ 开关走 _nb._cfg：进程环境 → .env 文件 → 出厂默认，三段都看。
        #   只看 os.environ 的话，.env 里写了 NETBAL_SHOW=0 却不生效（主程序没灌进环境时）。
        try:
            if str(_nb._cfg("NETBAL_SHOW", "1")).strip().lower() in ("0", "false", "off", ""):
                return ""
        except Exception:
            pass
        txt = _nb.panel_text(allow_net=False)
        return txt or ""
    except Exception:
        return ""


def backends_status_text():
    """给人看/给 AI 看的后端清单 + 台账（供 CLI 与 /net 命令）。"""
    ds = _load_ds()
    spec = backends_spec()
    if ds is None:
        _t = "（deepseek_search 包不可用，只能用 Tavily）"
        _b = _balance_tail()
        return _t + ("\n\n" + _b if _b else "")
    if spec == []:                   # 空列表 = 未启用；None 是 auto，不能混用
        _t = ("多后端：未启用（NET_BACKENDS 为空 → 走原 Tavily 路径）\n"
              "  想启用：set NET_BACKENDS=auto  或  set NET_BACKENDS=deepseek,tavily")
        _b = _balance_tail()
        return _t + ("\n\n" + _b if _b else "")
    lines = ["多后端：已启用（NET_BACKENDS=%s / router=%s / merge=%s）"
             % (NET_BACKENDS, NET_BACKEND_ROUTER, NET_MERGE),
             "", "可用后端："]
    try:
        for name, weight, st in ds.quality.ranked(candidates=ds.router.available_backends(spec)):
            extra = ""
            try:
                u = ds.quota.usage().get(name)
                if u:
                    if u.get("free_monthly"):
                        extra = "｜本月 %d/%d %s" % (u["units"], u["free_monthly"], u["unit"])
                    elif u.get("cost_yuan"):
                        extra = "｜本月 %.3f 元" % u["cost_yuan"]
            except Exception:
                pass
            lines.append("  %-9s 权重 %.2f  %s%s" % (
                name, weight,
                ("调用 %d 次，成功率 %.0f%%" % (st.get("calls", 0), (st.get("ok_rate") or 0) * 100))
                if st else "尚无记录", extra))
    except Exception as exc:
        lines.append("  （读取失败：%s）" % exc)
    if last_backends:
        lines.append("\n上次实际使用：%s" % "、".join(last_backends))
        for l in last_backend_log:
            lines.append("  %-9s %s %d 条 %dms %s" % (
                l["backend"], "OK  " if l["ok"] else "FAIL", l["n"], l["ms"], l["error"][:50]))
    # 2026-10-18：清单末尾挂上余额段（只读缓存，不联网）
    _b = _balance_tail()
    if _b:
        lines.append("")
        lines.append(_b)
    return "\n".join(lines)


# ============ 联网状态 → 对话窗口状态条（2026-10-02 新增）============
#   原本「正在联网 / 联网完成 / 需不需要联网」只画在控制台与状态台上；
#   现在同样一份同步进对话窗口「消息栏前面」的状态条。
#   窗口没开就静默跳过；任何异常都吞掉 —— 显示层绝不许影响联网本身。
def _win_net(text):
    try:
        import chat_window as _cw
        _cw.set_net_status(text)
    except Exception:
        pass


# ============ 独立运行支持：自己读 .env、自己遮罩密钥 ============
# 背景（2026-09-22）：主程序启动时用 net_tools.set_api_key(...) 注入密钥，
# 因此本模块脱离主程序**无法联网**。这里补一个最小的自举路径，
# 让本模块可以被当作独立命令行工具调用（见文件末尾的 CLI）。
# 安全约定：只读取 .env 中目标键的那一行；任何输出都只给遮罩，绝不打印明文；
#          也不接受「从命令行传密钥」（避免泄进 shell 历史与日志）。

def _clean_value(v):
    """清理 .env 值：去行尾注释、去首尾引号与空白。"""
    v = (v or "").strip()
    m = re.search(r"\s+#", v)
    if m:
        v = v[:m.start()]
    return v.strip().strip('"').strip("'")


def load_env_key(path=None, name="TAVILY_API_KEY"):
    """从 .env 读取指定键，返回 (value, path_used)。

    查找顺序：显式 path → 本文件所在目录的 .env → 其上级目录的 .env。
    找不到就返回 ("", 试图读的第一个路径)。
    """
    cands = []
    if path:
        cands.append(path)
    try:
        here = os.path.dirname(os.path.abspath(__file__))
    except Exception:
        here = os.getcwd()
    cands.append(os.path.join(here, ".env"))
    cands.append(os.path.join(os.path.dirname(here), ".env"))

    for p in cands:
        if not p or not os.path.isfile(p):
            continue
        try:
            with io.open(p, "r", encoding="utf-8", errors="replace") as f:
                for raw in f:
                    line = raw.strip()
                    if not line or line.startswith("#") or "=" not in line:
                        continue
                    k, v = line.split("=", 1)
                    if k.strip() == name:
                        return _clean_value(v), p
        except Exception:
            continue
    return "", (cands[0] if cands else "")


def mask_key(key):
    """密钥遮罩，仅用于人眼确认『配了没有』。"""
    key = key or ""
    if not key:
        return "(未配置)"
    if len(key) <= 12:
        return key[:2] + "***"
    return key[:7] + "…" + key[-4:]


try:
    from common import ts as _ts
except ImportError:               # common.py 缺失时退回本地实现，保持自足
    def _ts():
        return datetime.now().strftime("%Y-%m-%d %H:%M:%S")


# ============ 关键词判断（auto 模式） ============
TIME_WORDS   = ["最新", "今天", "现在", "实时", "当前", "近期", "最近",
                "今年", "本月", "这周"]
ACTION_WORDS = ["搜索", "查一下", "帮我查", "联网", "新闻", "股价",
                "汇率", "天气", "多少钱", "价格", "发布", "上线"]


def need_search(text):
    """auto 模式下的判断：命中动作词必搜；时间词需配问句。

    判定结果同时同步到对话窗口的状态条（🔍 需要联网 / ⛔ 无需联网）。
    """
    hit = any(w in text for w in ACTION_WORDS)
    if not hit:
        hit = any(w in text for w in TIME_WORDS) and bool(
            re.search(r"[?？吗]", text))
    _win_net("🔍 联网判定：需要联网" if hit else "⛔ 联网判定：无需联网")
    return hit


# ============ URL 抽取 ============
URL_RE = re.compile(r'https?://[^\s<>"\'）】]+')


def extract_urls(text):
    """从文本里抽出所有 URL，去重保序，并去掉末尾标点。"""
    seen, out = set(), []
    for u in URL_RE.findall(text):
        u = u.rstrip(".,;:!?）】")
        if u and u not in seen:
            seen.add(u)
            out.append(u)
    return out


# ============ search（调度层）============
def web_search(query, max_results=5, search_depth="basic", include_answer=True):
    """联网搜索的总入口。

    启用了多后端（NET_BACKENDS 非空）→ 走「多选 + 融合」；否则原样走 Tavily。
    对调用方完全透明：签名不变、返回的仍是给人/给模型读的格式化文本。

    ★ 2026-10-18：两条分支外面包了 try/finally 打点（见 _fire_net_hook）。
      用 finally 而不是"成功后再打点"，是为了让**失败/超时/异常**也留痕 ——
      排查时最需要的恰恰是失败那几次。
    """
    import time as _t
    _t0 = _t.time()
    _n, _ok, _bk, _out = 0, False, "", ""
    try:
        if _use_multi():
            _out = _search_via_multi(query, max_results=max_results)
            try:
                _bk = "、".join(last_backends) or "multi"
            except Exception:
                _bk = "multi"
        else:
            _out = _web_search_tavily(query, max_results=max_results,
                                      search_depth=search_depth,
                                      include_answer=include_answer)
            _bk = "tavily"
        _ok = bool(_out) and not re.match(
            r"^\s*（\s*(搜索出错|未搜到|未配置|搜索超时)", _out or "")
        try:
            # 结果条数：数「1. / 2. …」这种行首编号（对多后端与 Tavily 都成立）
            _n = len(re.findall(r"(?m)^\s*\d+\.\s", _out or ""))
        except Exception:
            _n = 0
        return _out
    finally:
        try:
            _fire_net_hook({"op": "search", "query": (query or "")[:120],
                            "backend": _bk or "?", "mode": "search",
                            "n": _n, "secs": _t.time() - _t0, "ok": _ok})
        except Exception:
            pass


# ============ search（Tavily 实现，原样保留）============
def _web_search_tavily(query, max_results=5, search_depth="basic", include_answer=True):
    """用 Tavily search API 联网搜索，返回格式化文本。

    search_depth   : "basic"（快）/ "advanced"（更全，稍贵）
    include_answer : 是否让 Tavily 额外给一段摘要
    后两个参数是 2026-09-22 新增的，均有默认值 —— 旧调用方行为不变。
    """
    if not TAVILY_API_KEY:
        return "（未配置 TAVILY_API_KEY，无法联网）"

    try:
        resp = requests.post(
            "https://api.tavily.com/search",
            json={
                "api_key": TAVILY_API_KEY,
                "query": query,
                "max_results": max_results,
                "search_depth": search_depth,
                "include_answer": include_answer,
            },
            timeout=20,
        )
        resp.raise_for_status()
        data = resp.json()

        lines = []
        if data.get("answer"):
            lines.append(f"【摘要】{data['answer']}\n")

        for i, r in enumerate(data.get("results", []), 1):
            title   = r.get("title", "（无标题）")
            content = (r.get("content") or "").strip()
            url     = r.get("url", "")
            if len(content) > 600:
                content = content[:600] + "…"
            lines.append(f"{i}. {title}\n   {content}\n   来源：{url}")

        if not lines:
            log.info(f"[{_ts()}] Tavily 无结果：{query}")
            return "（未搜到相关结果）"

        result = "\n".join(lines)
        log.info(f"[{_ts()}] Tavily 搜索成功：{query}（{len(data.get('results', []))} 条）")
        return result

    except requests.exceptions.Timeout:
        log.info(f"[{_ts()}] Tavily 搜索超时：{query}")
        return "（搜索超时，请稍后重试）"
    except requests.exceptions.HTTPError as e:
        code = e.response.status_code if e.response is not None else "?"
        log.info(f"[{_ts()}] Tavily 搜索 HTTP {code}：{query}")
        if code == 401:
            return "（搜索出错：401 未授权，TAVILY_API_KEY 可能无效或过期）"
        return f"（搜索出错：HTTP {code}）"
    except requests.exceptions.RequestException as e:
        log.info(f"[{_ts()}] Tavily 搜索请求失败：{e}")
        return f"（搜索出错：{e}）"
    except Exception as e:
        log.info(f"[{_ts()}] Tavily 搜索未知错误：{e}")
        return f"（搜索出错：{e}）"


# ============ extract（调度层）============
def web_extract(urls, max_results=5, extract_depth="advanced"):
    """抓网页正文的总入口。

    多后端启用时自动挑一个支持抓正文的后端（官方 Fetch / Tavily 都行）；
    否则原样走 Tavily extract。

    ★ 2026-10-18：同样用 try/finally 打点，失败路径也留痕。
    """
    import time as _t
    _t0 = _t.time()
    _urls = list(urls or [])
    _n, _ok, _bk, _out = 0, False, "", ""
    try:
        if _use_multi():
            _out = _extract_via_multi(_urls, max_results=max_results)
            try:
                _bk = "、".join(last_backends) or "multi"
            except Exception:
                _bk = "multi"
        else:
            _out = _web_extract_tavily(_urls, max_results=max_results,
                                       extract_depth=extract_depth)
            _bk = "tavily"
        _ok = bool(_out) and not re.match(
            r"^\s*（\s*(抓取出错|未抓到|未配置|抓取超时)", _out or "")
        _n = len(_urls)
        return _out
    finally:
        try:
            _fire_net_hook({"op": "extract",
                            "query": (_urls[0] if _urls else "")[:120],
                            "backend": _bk or "?", "mode": "extract",
                            "n": _n, "secs": _t.time() - _t0, "ok": _ok})
        except Exception:
            pass


# ============ extract（Tavily 实现，原样保留）============
def _web_extract_tavily(urls, max_results=5, extract_depth="advanced"):
    """用 Tavily extract API 抓取 URL 正文，返回格式化文本。

    extract_depth : "basic" / "advanced"（默认 advanced，抓得更全）
    该参数是 2026-09-22 新增的，有默认值 —— 旧调用方行为不变。
    """
    if not TAVILY_API_KEY:
        return "（未配置 TAVILY_API_KEY，无法联网）"
    if not urls:
        return "（没有可抓取的 URL）"

    urls = urls[:max_results]

    try:
        resp = requests.post(
            "https://api.tavily.com/extract",
            json={
                "api_key": TAVILY_API_KEY,
                "urls": urls,
                "extract_depth": extract_depth,
            },
            timeout=30,
        )
        resp.raise_for_status()
        data = resp.json()
        lines = []
        for i, r in enumerate(data.get("results", []), 1):
            url     = r.get("url", "")
            content = (r.get("raw_content") or "").strip()
            if not content:
                lines.append(f"{i}. {url}\n（无正文）")
                continue
            if len(content) <= EXTRACT_MAX_LEN:
                lines.append(f"{i}. {url}\n{content}")
            else:
                chunks = [
                    content[j:j + EXTRACT_MAX_LEN]
                    for j in range(0, len(content), EXTRACT_MAX_LEN)
                    ]
                for k, ch in enumerate(chunks, 1):
                    lines.append(
                        f"{i}.{k} {url}（第{k}/{len(chunks)}段）\n{ch}"
                    )

        for f in data.get("failed_results", []):
            lines.append(
                f"⚠️ 抓取失败：{f.get('url', '?')} —— {f.get('error', '未知')}"
            )

        if not lines:
            log.info(f"[{_ts()}] Tavily extract 无结果：{urls}")
            return "（未抓到内容）"

        result = "\n\n".join(lines)
        log.info(f"[{_ts()}] Tavily extract 成功：{len(urls)} 个 URL")
        return result

    except requests.exceptions.Timeout:
        log.info(f"[{_ts()}] Tavily extract 超时：{urls}")
        return "（抓取超时，请稍后重试）"
    except requests.exceptions.HTTPError as e:
        code = e.response.status_code if e.response is not None else "?"
        log.info(f"[{_ts()}] Tavily extract HTTP {code}")
        if code == 401:
            return "（抓取出错：401 未授权，TAVILY_API_KEY 可能无效或过期）"
        return f"（抓取出错：HTTP {code}）"
    except requests.exceptions.RequestException as e:
        log.info(f"[{_ts()}] Tavily extract 请求失败：{e}")
        return f"（抓取出错：{e}）"
    except Exception as e:
        log.info(f"[{_ts()}] Tavily extract 未知错误：{e}")
        return f"（抓取出错：{e}）"


# ============ 统一调度 ============
def _do_network_impl(user_input, query=None, mode=None, urls=None):
    """按模式决定走 search 还是 extract，返回结果文本。

    参数：
        user_input : 用户原始输入（用于抽 URL、以及 query 缺省时兜底）
        query      : 检索词（通常由「联网需求核验」AI 改写，用于 search）
        mode       : 显式指定模式 "search" / "extract" / "auto" / "both"
                     None 时按 TAVILY_MODE
        urls       : extract 模式要抓取的 URL 列表；None 时自动从 user_input 抽取

    优先级（谁说了算）：
        1) `/tavily search|extract` 用户显式指定的模式（TAVILY_MODE 非 auto）→ 最高
        2) mode 参数（通常来自 AI 核验的决策）
        3) TAVILY_MODE == auto 的内置规则：有 URL 就 extract，否则 search
    """
    # 用户用 /tavily 显式锁定了模式 → 尊重它，不被 AI 覆盖
    forced = TAVILY_MODE if TAVILY_MODE in ("search", "extract") else None
    m = forced or (mode or TAVILY_MODE or "auto")
    m = str(m).strip().lower()
    if m not in ("search", "extract", "auto", "both"):
        m = "auto"

    url_list = [u for u in (urls or []) if u] or extract_urls(user_input)

    if m == "auto":
        m = "extract" if url_list else "search"

    if m == "extract":
        if not url_list:
            # 没有 URL → extract 无从下手，回退搜索并说明
            note = "（extract 模式但没有可用 URL，已回退为关键词搜索）\n"
            return note + web_search(query or user_input)
        head = ""
        if forced == "extract" and TAVILY_MODE == "extract":
            pass  # 用户显式指定，不加额外说明
        return head + web_extract(url_list)

    if m == "both":
        # 先搜，再抓取搜索结果里的头几条链接的正文（更全面的深挖）
        search_text = web_search(query or user_input)
        hit_urls = extract_urls(search_text)[:2]
        if not hit_urls:
            return search_text
        head = "【搜索摘要】\n" + search_text + "\n\n"
        head += "【抓取正文（搜索结果前 2 条）】\n"
        return head + web_extract(hit_urls)

    return web_search(query or user_input)


def do_network(user_input, query=None, mode=None, urls=None):
    """统一调度（包装层）：额外把「开始 / 完成 / 失败」同步到对话窗口状态条。

    真正的调度逻辑在 _do_network_impl 里（逐字未改）；本层只做三件事：
    联网前报「正在联网」、结束后报「完成/失败 + 字数」、异常时报「出错」。

    2026-10-18 追加：多后端模式下，把「本次用了哪几个后端」也画到状态条上，
    这样一眼能看出是不是真的按 AI 的决定在走。
    """
    _win_net("🌐 正在联网…")
    if not backends_spec():
        # 没启用多后端 → 清掉可能残留的「审核 AI 决定」，避免污染下一次
        set_pending_backends(None)
    try:
        text = _do_network_impl(user_input, query=query, mode=mode, urls=urls)
    except BaseException:
        _win_net("⚠️ 联网出错")
        raise
    body = text or ""
    bad = bool(re.search(r"出错|超时|未配置|无法联网|未搜到|未抓到", body[:120]))
    if bad:
        _win_net("⚠️ 联网未成功" + ("（%s）" % "、".join(last_backends) if last_backends else ""))
    else:
        via = ("（%s）" % "、".join(last_backends)) if last_backends else ""
        _win_net("🌐 联网完成%s（%d 字）" % (via, len(body)))
    # ★ 2026-10-18：联网一结束就**后台**刷一次余额（daemon 线程，立即返回、不阻塞）。
    #   这是「实时对账」的触发点：每次真正花掉额度之后，官方数字与本地账本立刻重算一遍，
    #   下一次打开「网络」浮层 / 跑 /net 看到的就是刚刚对过的新账。
    #   节流由 net_balance 自己管（默认 90 秒内不重复发起），所以这里随便调都不会打风暴。
    try:
        import net_balance as _nb
        _nb.refresh_async()
    except Exception:
        pass
    return text


# ============ 独立命令行入口（供「AI 经审核后自行调用」）============
# 为什么要有它：
#   主程序里联网是「自动」的（判定 → do_network → 结果拼进用户消息）。
#   但独立场景下（例如：AI 在工作台里查资料、QQ 桥接进程要联网），
#   需要本模块能被**单独拉起**，且整条调用会经过既有的两道闸门：
#       ws_run_cmd（人工报批 + 审查员核验）→ 执行 → 结果回到 AI 上下文。
#
# 设计要点：
#   · 必须显式带 --allow-network 才真的联网（不给就拒绝，退出码 4）。
#     这样「要不要上网」在审批弹窗里一眼可见，不会偷偷发请求。
#   · --dry-run 只打印将要发送的请求体（密钥打码），不联网 —— 可离线验证参数。
#   · 输出默认截断到 6000 字符，避免把 AI 的上下文撑爆；--full 可关。
#   · 密钥只从 .env 读，永不打印明文，也不接受命令行传入。
#
# 用法示例：
#   python net_tools.py check
#   python net_tools.py search "NapCat HTTP 上报 配置" --allow-network
#   python net_tools.py search "QQ 机器人 发消息 接口" --max 8 --depth advanced --allow-network
#   python net_tools.py extract https://example.com/a https://example.com/b --allow-network
#   python net_tools.py both "OneBot 12 规范" --allow-network
#   python net_tools.py ask "帮我查一下 NapCat 最新版本" --allow-network
#   python net_tools.py search "..." --dry-run          # 离线看请求体

CLI_DEFAULT_LIMIT = 6000


def _cli_payload_preview(mode, args, query=None, urls=None):
    """给 --dry-run 用：把将发送的请求体打出来（密钥打码）。"""
    if mode == "extract":
        return {"url": "https://api.tavily.com/extract",
                "json": {"api_key": mask_key(TAVILY_API_KEY),
                         "urls": (urls or [])[:args.max],
                         "extract_depth": args.extract_depth}}
    if mode == "search":
        return {"url": "https://api.tavily.com/search",
                "json": {"api_key": mask_key(TAVILY_API_KEY), "query": query,
                         "max_results": args.max, "search_depth": args.depth,
                         "include_answer": True}}
    return {"mode": mode, "query": query, "urls": urls, "note": "both/ask 会组合多次请求"}


def _emit(text, args):
    """统一输出：支持 --json 与截断。"""
    if getattr(args, "json", False):
        print(json.dumps({"ok": True, "mode": args.cmd, "result": text},
                         ensure_ascii=False, indent=2))
        return
    limit = 0 if getattr(args, "full", False) else int(getattr(args, "limit", 0) or 0)
    if limit and len(text) > limit:
        sys.stdout.write(text[:limit])
        if not text[:limit].endswith("\n"):
            sys.stdout.write("\n")
        sys.stdout.write("…（输出已截断到 %d 字符；用 --full 看全文，或调 --limit）\n" % limit)
    else:
        sys.stdout.write(text if text.endswith("\n") else text + "\n")


def _unquote(s):
    """剥掉 shell 原样带进来的首尾引号（Windows cmd 常见）。"""
    s = (s or "").strip()
    if len(s) >= 2 and s[0] == s[-1] and s[0] in ('"', "'"):
        return s[1:-1].strip()
    return s


def _force_utf8_stdio():
    """把子进程的 stdout/stderr 钉成 UTF-8。

    背景：Windows + 中文 locale（cp936）下，被重定向到管道/文件时，
    Python 会按 locale 编码输出中文，下游按 UTF-8 读就会乱码。
    主程序 FATHFISHII.py 有 _force_utf8_streams() 处理这件事；
    本模块作为**独立子进程**被调用时不会经过那段代码，所以这里自带一份。
    """
    for name in ("stdout", "stderr"):
        stream = getattr(sys, name, None)
        try:
            if stream is not None and hasattr(stream, "reconfigure"):
                stream.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass


def build_cli_parser():
    import argparse
    p = argparse.ArgumentParser(
        prog="net_tools.py",
        description="FatFish 联网工具（独立调用版）：search / extract / both / ask / check",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=("示例：\n"
                "  python net_tools.py check\n"
                "  python net_tools.py search \"NapCat HTTP 上报 配置\" --allow-network\n"
                "  python net_tools.py extract https://example.com --allow-network\n"))
    p.add_argument("cmd", choices=["search", "extract", "both", "ask", "check", "backends", "quota"])
    p.add_argument("args", nargs="*", help="search/both/ask = 检索词；extract = URL 列表")
    p.add_argument("--backends", default=None,
                   help="临时指定搜索后端（多选，逗号分隔，如 deepseek,tavily；auto=全部可用）")
    p.add_argument("--router", default=None, choices=["ai", "rule", "off"],
                   help="后端路由策略：ai=审核 AI 决定 / rule=按偏好+规则 / off=不改")
    p.add_argument("--quality", action="store_true",
                   help="backends 子命令用：连质量台账一起打印")
    p.add_argument("--daily", action="store_true", help="quota 子命令用：按天明细")
    p.add_argument("--max", type=int, default=5, help="结果条数上限（默认 5）")
    p.add_argument("--depth", default="basic", choices=["basic", "advanced"],
                   help="search 深度（默认 basic）")
    p.add_argument("--extract-depth", dest="extract_depth", default="advanced",
                   choices=["basic", "advanced"], help="extract 深度（默认 advanced）")
    p.add_argument("--mode", default=None, choices=["search", "extract", "both", "auto"],
                   help="ask 用：显式指定模式")
    p.add_argument("--limit", type=int, default=CLI_DEFAULT_LIMIT, help="stdout 截断字符数")
    p.add_argument("--full", action="store_true", help="不截断输出")
    p.add_argument("--json", action="store_true", help="以 JSON 结构化输出")
    p.add_argument("--env", default=None, help="指定 .env 路径（默认自动寻找）")
    p.add_argument("--allow-network", dest="allow_network", action="store_true",
                   help="★ 显式联网许可：不带它就拒绝联网（退出码 4）")
    p.add_argument("--dry-run", dest="dry_run", action="store_true",
                   help="只打印将要发送的请求（密钥打码），不联网")
    return p


def main(argv=None):
    global NET_BACKEND_ROUTER
    _force_utf8_stdio()

    # ---- 先把 .env 灌进进程环境（2026-10-18 补）----
    #   为什么必须做：CLI 是从命令行单独跑的，没人替它加载 .env。
    #   不加这一步时，`python net_tools.py backends` 会读到全空的进程环境，
    #   于是显示「多后端：未启用」—— 而主程序里其实早已启用，纯属误导。
    #   （同一个坑今天已经在 boot_report._selftest 和 verify 脚本上各咬过一次。）
    #   · 已存在的环境变量优先（load_dotenv 默认不覆盖），所以命令行 set 的仍然生效
    #   · 失败不报错：没装 python-dotenv 时照旧走「只读目标键」的老路径
    try:
        from dotenv import load_dotenv
        load_dotenv(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".env"))
    except Exception:
        pass

    args = build_cli_parser().parse_args(argv)

    # ---- 后端选择（命令行优先于 .env）----
    if getattr(args, "backends", None) is not None:
        set_backends(args.backends)
    else:
        # .env 里的 NET_BACKENDS 也要认（命令行没给时才回落到它）
        try:
            _nb = (os.environ.get("NET_BACKENDS") or "").strip()
            if _nb:
                set_backends(_nb)
        except Exception:
            pass
    if getattr(args, "router", None):
        NET_BACKEND_ROUTER = args.router
    else:
        try:
            _rb = (os.environ.get("NET_BACKEND_ROUTER") or "").strip().lower()
            if _rb in ("ai", "rule", "off"):
                NET_BACKEND_ROUTER = _rb
            _mg = (os.environ.get("NET_MERGE") or "").strip().lower()
            if _mg in ("rrf", "score", "none"):
                globals()["NET_MERGE"] = _mg
        except Exception:
            pass

    # ---- 自举密钥（只读 .env 的目标键，绝不打印明文）----
    key, env_path = load_env_key(args.env)
    if key:
        set_api_key(key)
    if not key:
        ds_key, _ = load_env_key(args.env, "DEEPSEEK_API_KEY")
        if ds_key:
            os.environ.setdefault("DEEPSEEK_API_KEY", ds_key)

    # ---- backends：只看后端清单与台账，不联网 ----
    if args.cmd == "backends":
        _emit(backends_status_text(), args)
        if getattr(args, "quality", False):
            ds = _load_ds()
            if ds is not None:
                _emit("\n" + ds.quality.summary_text(), args)
        return 0

    # ---- quota：额度与花费（不联网）----
    if args.cmd == "quota":
        ds = _load_ds()
        if ds is None:
            print("（deepseek_search 包不可用，无法统计）")
            return 2
        _emit(ds.quota.summary_text(), args)
        if getattr(args, "daily", False):
            _emit("\n" + ds.quota.daily_text(), args)
        return 0

    # ---- check：不联网，只报状态 ----
    if args.cmd == "check":
        lines = ["【net_tools 自检】",
                 "  密钥状态 : %s" % ("已就绪" if key else "未配置（联网不可用）"),
                 "  密钥遮罩 : %s" % mask_key(key),
                 "  读取来源 : %s" % (env_path or "(未找到 .env)"),
                 "  当前模式 : NET_MODE=%s / TAVILY_MODE=%s" % (NET_MODE, TAVILY_MODE),
                 "  后端选择 : NET_BACKENDS=%s / router=%s / merge=%s"
                 % (NET_BACKENDS or "(未启用，走 Tavily)", NET_BACKEND_ROUTER, NET_MERGE),
                 "  提示     : 真联网需显式加 --allow-network"]
        _emit("\n".join(lines), args)
        return 0 if key else 2

    argv_texts = [_unquote(a) for a in args.args]
    query = " ".join(t for t in argv_texts if t).strip()
    urls = [t for t in argv_texts if t.lower().startswith(("http://", "https://"))]

    if args.cmd in ("search", "both", "ask") and not query:
        sys.stderr.write("（缺少检索词：python net_tools.py %s \"关键词\" ...）\n" % args.cmd)
        return 1
    if args.cmd == "extract" and not urls:
        sys.stderr.write("（缺少 URL：python net_tools.py extract https://...）\n")
        return 1

    # ---- 干跑：不联网，只回显请求 ----
    if args.dry_run:
        if args.cmd == "extract":
            preview = _cli_payload_preview("extract", args, urls=urls)
        elif args.cmd == "both":
            preview = _cli_payload_preview("both", args, query=query, urls=urls)
        else:
            preview = _cli_payload_preview("search", args, query=query)
        _emit("【dry-run】以下请求将不会被发送：\n" + json.dumps(preview, ensure_ascii=False, indent=2),
              args)
        return 0

    # ---- 联网许可闸门 ----
    if not args.allow_network:
        sys.stderr.write("（已拒绝联网：请显式加上 --allow-network。"
                         "这个标志就是『本步已获人工/审查许可』的凭证。）\n")
        return 4
    if not key:
        sys.stderr.write("（未配置 TAVILY_API_KEY，无法联网。先跑：python net_tools.py check）\n")
        return 2

    # ---- 真正执行 ----
    try:
        if args.cmd == "search":
            out = web_search(query, max_results=args.max,
                             search_depth=args.depth, include_answer=True)
        elif args.cmd == "extract":
            out = web_extract(urls, max_results=args.max, extract_depth=args.extract_depth)
        elif args.cmd == "both":
            out = do_network(query, query=query, mode="both")
        else:  # ask
            out = do_network(query, query=None, mode=args.mode)
    except Exception as e:
        sys.stderr.write("（执行出错：%s: %s）\n" % (type(e).__name__, e))
        return 3

    if not args.json:
        head = "【net_tools %s】%s\n" % (args.cmd, query or " ".join(urls))
        sys.stdout.write(head)
    _emit(out, args)

    # ---- 联网后顺手报一句本月用量（可用 NET_QUOTA_SHOW=0 关掉）----
    #   ★ 2026-10-18：在本月用量之后补一行「实时余额」，并在吃紧时直接喊出来。
    #     余额读的是 net_balance 的磁盘缓存（不联网），所以这一行本身零成本。
    if os.environ.get("NET_QUOTA_SHOW", "1") not in ("0", "false", "False", "off"):
        try:
            ds = _load_ds()
            if ds is not None:
                line = ds.quota.one_line()
                if line:
                    sys.stderr.write("\n[" + line + "]\n")
        except Exception:
            pass
        try:
            import net_balance as _nb
            _bl = _nb.one_line(allow_net=False)
            if _bl:
                sys.stderr.write("[" + _bl + "]\n")
            for _a in _nb.alarms(allow_net=False):
                if _a.get("level") in ("crit", "dead"):
                    sys.stderr.write("[" + _nb.LEVEL_ICON[_a["level"]] + " "
                                     + _a["text"] + "]\n")
        except Exception:
            pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
