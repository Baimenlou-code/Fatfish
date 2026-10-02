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


# ============ search ============
def web_search(query, max_results=5, search_depth="basic", include_answer=True):
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


# ============ extract ============
def web_extract(urls, max_results=5, extract_depth="advanced"):
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
    """
    _win_net("🌐 正在联网…")
    try:
        text = _do_network_impl(user_input, query=query, mode=mode, urls=urls)
    except BaseException:
        _win_net("⚠️ 联网出错")
        raise
    body = text or ""
    bad = bool(re.search(r"出错|超时|未配置|无法联网|未搜到|未抓到", body[:80]))
    if bad:
        _win_net("⚠️ 联网未成功")
    else:
        _win_net("🌐 联网完成（%d 字）" % len(body))
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
    主程序 FATHFISH.py 有 _force_utf8_streams() 处理这件事；
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
    p.add_argument("cmd", choices=["search", "extract", "both", "ask", "check"])
    p.add_argument("args", nargs="*", help="search/both/ask = 检索词；extract = URL 列表")
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
    _force_utf8_stdio()
    args = build_cli_parser().parse_args(argv)

    # ---- 自举密钥（只读 .env 的目标键，绝不打印明文）----
    key, env_path = load_env_key(args.env)
    if key:
        set_api_key(key)

    # ---- check：不联网，只报状态 ----
    if args.cmd == "check":
        lines = ["【net_tools 自检】",
                 "  密钥状态 : %s" % ("已就绪" if key else "未配置（联网不可用）"),
                 "  密钥遮罩 : %s" % mask_key(key),
                 "  读取来源 : %s" % (env_path or "(未找到 .env)"),
                 "  当前模式 : NET_MODE=%s / TAVILY_MODE=%s" % (NET_MODE, TAVILY_MODE),
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
    return 0


if __name__ == "__main__":
    sys.exit(main())
