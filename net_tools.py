import re
import logging
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
    """auto 模式下的判断：命中动作词必搜；时间词需配问句。"""
    if any(w in text for w in ACTION_WORDS):
        return True
    if any(w in text for w in TIME_WORDS) and re.search(r"[?？吗]", text):
        return True
    return False


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
def web_search(query, max_results=5):
    """用 Tavily search API 联网搜索，返回格式化文本。"""
    if not TAVILY_API_KEY:
        return "（未配置 TAVILY_API_KEY，无法联网）"

    try:
        resp = requests.post(
            "https://api.tavily.com/search",
            json={
                "api_key": TAVILY_API_KEY,
                "query": query,
                "max_results": max_results,
                "search_depth": "basic",
                "include_answer": True,
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
def web_extract(urls, max_results=5):
    """用 Tavily extract API 抓取 URL 正文，返回格式化文本。"""
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
                "extract_depth": "advanced",
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
def do_network(user_input, query=None, mode=None, urls=None):
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