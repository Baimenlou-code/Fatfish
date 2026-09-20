import re
import logging
from datetime import datetime
import requests

log = logging.getLogger(__name__)

TAVILY_API_KEY = ""
TAVILY_MODE    = "auto"
NET_MODE       = "auto"
EXTRACT_MAX_LEN = 8000

def set_api_key(key):
    global TAVILY_API_KEY
    TAVILY_API_KEY = (key or "").strip()

try:
    from common import ts as _ts
except ImportError:
    def _ts():
        return datetime.now().strftime("%Y-%m-%d %H:%M:%S")

TIME_WORDS   = ["latest", "today", "now", "current", "currently", "recent",
                "recently", "this year", "this month", "this week"]
ACTION_WORDS = ["search", "look up", "lookup", "google", "browse", "news",
                "stock price", "exchange rate", "weather", "how much",
                "price", "release", "launched"]

def need_search(text):
    low = (text or "").lower()
    if any(w in low for w in ACTION_WORDS):
        return True
    if any(w in low for w in TIME_WORDS) and re.search(r"\?", text):
        return True
    return False

URL_RE = re.compile(r'https?://[^\s<>"\')\]]+')

def extract_urls(text):
    seen, out = set(), []
    for u in URL_RE.findall(text or ""):
        u = u.rstrip(".,;:!?)]")
        if u and u not in seen:
            seen.add(u)
            out.append(u)
    return out

def web_search(query, max_results=5):
    if not TAVILY_API_KEY:
        return "(TAVILY_API_KEY is not configured; cannot go online)"

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
            lines.append(f"[Summary] {data['answer']}\n")

        for i, r in enumerate(data.get("results", []), 1):
            title   = r.get("title", "(no title)")
            content = (r.get("content") or "").strip()
            url     = r.get("url", "")
            if len(content) > 600:
                content = content[:600] + "..."
            lines.append(f"{i}. {title}\n   {content}\n   source: {url}")

        if not lines:
            log.info(f"[{_ts()}] Tavily returned nothing: {query}")
            return "(no relevant results found)"

        result = "\n".join(lines)
        log.info(f"[{_ts()}] Tavily search ok: {query} ({len(data.get('results', []))} hits)")
        return result

    except requests.exceptions.Timeout:
        log.info(f"[{_ts()}] Tavily search timed out: {query}")
        return "(search timed out, please retry later)"
    except requests.exceptions.HTTPError as e:
        code = e.response.status_code if e.response is not None else "?"
        log.info(f"[{_ts()}] Tavily search HTTP {code}: {query}")
        if code == 401:
            return "(search error: 401 unauthorized -- TAVILY_API_KEY may be invalid or expired)"
        return f"(search error: HTTP {code})"
    except requests.exceptions.RequestException as e:
        log.info(f"[{_ts()}] Tavily search request failed: {e}")
        return f"(search error: {e})"
    except Exception as e:
        log.info(f"[{_ts()}] Tavily search unknown error: {e}")
        return f"(search error: {e})"

def web_extract(urls, max_results=5):
    if not TAVILY_API_KEY:
        return "(TAVILY_API_KEY is not configured; cannot go online)"
    if not urls:
        return "(no URL to fetch)"

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
                lines.append(f"{i}. {url}\n(no body)")
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
                        f"{i}.{k} {url} (part {k}/{len(chunks)})\n{ch}"
                    )

        for f in data.get("failed_results", []):
            lines.append(
                f"[!] fetch failed: {f.get('url', '?')} -- {f.get('error', 'unknown')}"
            )

        if not lines:
            log.info(f"[{_ts()}] Tavily extract returned nothing: {urls}")
            return "(nothing extracted)"

        result = "\n\n".join(lines)
        log.info(f"[{_ts()}] Tavily extract ok: {len(urls)} URL(s)")
        return result

    except requests.exceptions.Timeout:
        log.info(f"[{_ts()}] Tavily extract timed out: {urls}")
        return "(fetch timed out, please retry later)"
    except requests.exceptions.HTTPError as e:
        code = e.response.status_code if e.response is not None else "?"
        log.info(f"[{_ts()}] Tavily extract HTTP {code}")
        if code == 401:
            return "(fetch error: 401 unauthorized -- TAVILY_API_KEY may be invalid or expired)"
        return f"(fetch error: HTTP {code})"
    except requests.exceptions.RequestException as e:
        log.info(f"[{_ts()}] Tavily extract request failed: {e}")
        return f"(fetch error: {e})"
    except Exception as e:
        log.info(f"[{_ts()}] Tavily extract unknown error: {e}")
        return f"(fetch error: {e})"

def do_network(user_input, query=None, mode=None, urls=None):
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
            note = "(extract mode but no usable URL; fell back to keyword search)\n"
            return note + web_search(query or user_input)
        head = ""
        if forced == "extract" and TAVILY_MODE == "extract":
            pass
        return head + web_extract(url_list)

    if m == "both":
        search_text = web_search(query or user_input)
        hit_urls = extract_urls(search_text)[:2]
        if not hit_urls:
            return search_text
        head = "[Search summary]\n" + search_text + "\n\n"
        head += "[Fetched bodies (top 2 search results)]\n"
        return head + web_extract(hit_urls)

    return web_search(query or user_input)
