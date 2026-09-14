# ============ 最早期的 UTF-8 环境设置（必须在任何 import 之前）============
# Python 3.8 + Windows + stdout 重定向 时，流编码可能回退为 ascii，
# 导致含中文/emoji 的输出抛 UnicodeEncodeError。这里在解释器启动的
# 最早期就设好环境变量，作为第一重保险（后面 _force_utf8_streams 再兜底）。
import os as _os
import sys as _sys
_os.environ["PYTHONUTF8"] = "1"
_os.environ["PYTHONIOENCODING"] = "utf-8"
# 若解释器支持，直接开启 UTF-8 模式（Python 3.7+）
try:
    _sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    _sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    _sys.stdin.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

import os
import re
import sys
import json
import logging
import time
from datetime import datetime
from openai import OpenAI
from dotenv import load_dotenv

import net_tools
import file_tools
import workspace

# ============ 加载 .env（优先于系统环境变量）============
load_dotenv()

# ============ Windows UTF-8 兼容（强健版）============
# 背景：Python 3.8 在 Windows 下，若进程由 chcp 936 的控制台启动、
# 且 stdout/stderr 被重定向到管道或文件，Python 可能把流编码回退为 ascii。
# 此时任何含中文/emoji 的 print 都会抛：
#   'ascii' codec can't encode characters in position ...: ordinal not in range(128)
# 这里做三重保险，且放在任何 print 之前执行：
#   1) 设置 PYTHONUTF8 / PYTHONIOENCODING，影响后续子进程与部分内部行为；
#   2) 对 stdout/stderr 调用 reconfigure，强制 utf-8 且 errors="replace"；
#   3) reconfigure 不可用或失败时，退回用 io.TextIOWrapper 重新包一层。
def _force_utf8_streams():
    if sys.platform != "win32":
        return
    os.environ.setdefault("PYTHONUTF8", "1")
    os.environ.setdefault("PYTHONIOENCODING", "utf-8")
    import io as _io
    # stdin 也要处理：input() 读的是它，历史上这里最容易漏
    for name in ("stdin", "stdout", "stderr"):
        stream = getattr(sys, name, None)
        if stream is None:
            continue
        # 保险 2：reconfigure
        if hasattr(stream, "reconfigure"):
            try:
                stream.reconfigure(encoding="utf-8", errors="replace")
                continue
            except Exception:
                pass
        # 保险 3：重新包一层（保留原 buffer）
        buf = getattr(stream, "buffer", None)
        if buf is not None:
            try:
                setattr(sys, name, _io.TextIOWrapper(
                    buf, encoding="utf-8", errors="replace", line_buffering=True))
            except Exception:
                pass

_force_utf8_streams()

# ============ 颜色常量 ============
RESET = "\033[0m"
K   = "\033[30m"; R   = "\033[31m"; G   = "\033[32m"; Y   = "\033[33m"
BL  = "\033[34m"; M   = "\033[35m"; CY  = "\033[36m"; W   = "\033[37m"
BK  = "\033[90m"; BR  = "\033[91m"; BG  = "\033[92m"; BY  = "\033[93m"
BB  = "\033[94m"; BM  = "\033[95m"; BC  = "\033[96m"; BW  = "\033[97m"
BOLD = "\033[1m"; DIM = "\033[2m"; ITAL = "\033[3m"
UND  = "\033[4m"; RV  = "\033[7m"; ST  = "\033[9m"
BGR  = "\033[41m"; BGG = "\033[42m"; BGY = "\033[43m"; BGB = "\033[44m"

def fg256(n):  return f"\033[38;5;{n}m"
def bg256(n):  return f"\033[48;5;{n}m"
def rgb(r, g, b, back=False):
    return f"\033[{'48' if back else '38'};2;{r};{g};{b}m"

def paint(text, *styles):
    return "".join(styles) + str(text) + RESET

def rainbow(text):
    pal = [196,202,208,214,220,226,190,154,118,82,
           46,47,51,45,39,33,27,57,93,129,165,201]
    return "".join(fg256(pal[i % len(pal)]) + ch
                   for i, ch in enumerate(text)) + RESET

def gradient(text, c1, c2):
    n = max(len(text) - 1, 1)
    out = []
    for i, ch in enumerate(text):
        t = i / n
        r = int(c1[0] + (c2[0] - c1[0]) * t)
        g = int(c1[1] + (c2[1] - c1[1]) * t)
        b = int(c1[2] + (c2[2] - c1[2]) * t)
        out.append(rgb(r, g, b) + ch)
    return "".join(out) + RESET

# ============ 情绪调色板 ============
MOOD_COLOR = {
    "happy":   ((255,215,  0),(255,140,  0)),
    "excited": ((255,105,180),(255, 20,147)),
    "love":    ((255,182,193),(255,  0,102)),
    "calm":    ((135,206,235),( 70,130,180)),
    "sad":     ((100,149,237),( 72, 61,139)),
    "error":   ((255, 69,  0),(139,  0,  0)),
    "code":    ((  0,255,127),(  0,191,255)),
    "think":   ((200,200,200),(150,150,150)),
}
MOOD_EMOJI = {
    "happy":"😊", "excited":"✨", "love":"❤️ ",
    "calm":"🤖",  "sad":"🥺",    "error":"⚠️ ",
    "code":"💻",  "think":"🤔",
}
MOOD_KEYWORDS = {
    "error":   ["错误","异常","失败","无法","报错",
                "error","Error","failed","Traceback"],
    "sad":     ["抱歉","遗憾","可惜","难过"],
    "happy":   ["太好了","成功","恭喜","搞定","完成",
                "👍","🎉","哈哈","nice"],
    "excited": ["哇","太棒","惊人","震撼","！！"],
    "love":    ["喜欢","爱你","❤","😊"],
    "think":   ["让我想想","分析一下","考虑","首先","推理"],
    "code":    ["```","def ","class ","import ","function "],
}
MOOD_ORDER = ["error", "code", "think", "sad", "happy", "love", "excited"]

def detect_mood(text):
    scores = {}
    for mood, kws in MOOD_KEYWORDS.items():
        s = sum(text.count(kw) for kw in kws)
        if s:
            scores[mood] = s
    if not scores:
        return "calm"
    for m in MOOD_ORDER:
        if m in scores:
            return m
    return "calm"

# ============ AI 颜色标记 ============
TAG_COLOR = {
    "red":BR, "green":BG, "yellow":BY, "blue":BB,
    "cyan":BC, "magenta":BM, "white":BW, "gray":BK, "grey":BK,
    "bold":BOLD, "italic":ITAL, "underline":UND,
    "dim":DIM, "strike":ST,
}
TAG_RE = re.compile(r"\{\{(\w+)\}\}(.*?)\{\{/\1\}\}", re.DOTALL)

def _tag(tag, content):
    t = tag.lower()
    if t == "rainbow":
        return rainbow(content)
    return paint(content, TAG_COLOR[t]) if t in TAG_COLOR else content

def render_markup(text):
    return TAG_RE.sub(lambda m: _tag(m.group(1), m.group(2)), text)

def strip_markup(text):
    text = TAG_RE.sub(lambda m: m.group(2), text)
    text = re.sub(r"\*\*(.+?)\*\*", r"\1", text)
    text = re.sub(r"(?<!`)`([^`]+)`(?!`)", r"\1", text)
    return text

def render_inline(line):
    line = render_markup(line)
    line = re.sub(r"\*\*(.+?)\*\*",
                  lambda m: paint(m.group(1), BW, BOLD), line)
    line = re.sub(r"`([^`]+)`",
                  lambda m: paint(m.group(1), BM, bg256(236)), line)
    return line

# ============ 输出渲染 ============
def print_banner():
    title = "🐟 DeepSeek 联网肥鱼 G 版 v1.0.4 已启动 [DeepSeek Net Fish G Edition v1.0.4 Started]"
    sub   = "exit/quit 退出 [exit] · @文件 读取 [read file] · /net 联网 [net] · /tavily 模式 [mode] · /ws 工作台 [workspace] · /help 帮助 [help]"
    w = max(len(title), len(sub)) + 6
    line = "─" * w
    print()
    for s in ["╭" + line + "╮",
              "│  " + title.ljust(w - 4) + "│",
              "│  " + sub.ljust(w - 4) + "│",
              "╰" + line + "╯"]:
        print(gradient(s, (0, 200, 255), (255, 100, 200)))
    print()

def print_ai(reply):
    mood = detect_mood(reply)
    c1, c2 = MOOD_COLOR.get(mood, MOOD_COLOR["calm"])
    emoji  = MOOD_EMOJI.get(mood, "🤖")
    head   = f" {emoji} AI "
    print()
    print(gradient("─" * 6 + head + "─" * 6, c1, c2))

    in_code = False
    for raw in reply.splitlines():
        line = raw.rstrip()
        if re.match(r"^\s*```", line):
            in_code = not in_code
            print(paint(line, CY, ITAL))
            continue
        if in_code:
            print(paint(line, BG) if line else "")
            continue
        if re.match(r"^#{1,6}\s", line):
            print(paint("▎" + re.sub(r"^#{1,6}\s", "", line), BY, BOLD))
            continue
        if re.match(r"^\s*[-*+]\s", line):
            line = re.sub(r"^(\s*)[-*+]\s", r"\1  • ", line)
            print(render_inline(line))
            continue
        print(render_inline(line))
    print(gradient("─" * (12 + len(head)), c1, c2))
    print()

# ============ 配置 ============
API_KEY        = os.getenv("DEEPSEEK_API_KEY", "")
TAVILY_API_KEY = os.getenv("TAVILY_API_KEY", "")
BASE_URL       = "https://api.deepseek.com"
MODEL          = "deepseek-flash"
# ---- 上下文 / 交互 / 命令 限制（已整体放宽，可按需再调）----
MAX_HISTORY    = 400          # 保留的历史消息条数（原 20 → 200 → 1000 → 400，适配 1M 上下文）
MAX_HISTORY_TOKENS = 800000   # 历史部分的 token 预算（估算值，原 60000 → 512000 → 2000000 → 800000）
TRIM_KEEP_FIRST_USER = True   # 是否钉住最早一条 user（任务目标）
TRIM_TOOL_CLIP_CHARS = 40000  # 单条 tool 结果超过此长度则中间截断（原 4000 → 60000 → 200000 → 40000）
MAX_REPLY_TOKENS = 131072     # 单次回复的 max_tokens（原 16384 → 32768 → 65536 → 131072）
MAX_TOOL_ROUNDS  = 512        # 单轮交互内工具调用轮数上限（原 16 → 128 → 512）
LOG_ROOT       = "logs"
CODE_ROOT      = "generated_code"
API_TIMEOUT    = 900          # 单次 API 请求超时秒数（原 60 → 300 → 900，大上下文更慢）

# ---- 计时器（本轮耗时 / 你停留了多久）----
#   本轮 = 你发话那一刻 → 本轮彻底跑完（含工具循环）
#   停留 = 上一轮跑完 → 你下一次发话
SHOW_TIMER      = True       # 是否显示计时（/timer off 可关）
_TIMER_EXITING  = False      # 程序正在退出，不再结算本轮
_LAST_ROUND_END = None       # 上一轮结束时刻（时间戳）
_ROUND_START    = None       # 本轮开始时刻（用户发话那一刻）
_ROUND_ACTIVE   = False      # 本轮是否已开始、尚未结算


def _fmt_secs(s):
    """紧凑格式：12.4s / 1m23.4s / 1h02m。"""
    s = max(0.0, float(s))
    if s < 60:
        return f"{s:.1f}s"
    if s < 3600:
        return f"{int(s // 60)}m{s % 60:.1f}s"
    return f"{int(s // 3600)}h{int((s % 3600) // 60):02d}m"


def _mark_round_start():
    """用户发话后调用：先亮出「停留」时长，再开始本轮计时。"""
    global _LAST_ROUND_END, _ROUND_START, _ROUND_ACTIVE
    now = time.time()
    if SHOW_TIMER and _LAST_ROUND_END is not None:
        print(paint(f"  ⏱️ 停留 {_fmt_secs(now - _LAST_ROUND_END)}", BK, DIM))
    _ROUND_START = now
    _ROUND_ACTIVE = True


def _mark_round_end():
    """一轮结束时调用（正常回复 / 斜杠命令 / 异常，都走 finally 里的这里）。"""
    global _LAST_ROUND_END, _ROUND_ACTIVE
    now = time.time()
    _LAST_ROUND_END = now
    if SHOW_TIMER and _ROUND_ACTIVE and _ROUND_START is not None and not _TIMER_EXITING:
        cost = now - _ROUND_START
        print(paint(f"  ⏱️ 本轮 {_fmt_secs(cost)}", BK, DIM))
        log(f"[{_ts()}] 本轮耗时 {cost:.2f}s")
    _ROUND_ACTIVE = False


def _dated_dir(root: str) -> str:
    now = datetime.now()
    d = os.path.join(root, f"{now:%Y}", f"{now:%m}", f"{now:%d}")
    os.makedirs(d, exist_ok=True)
    return d

LOG_DIR  = _dated_dir(LOG_ROOT)
CODE_DIR = _dated_dir(CODE_ROOT)

logger = logging.getLogger()
logger.setLevel(logging.INFO)
if not logger.handlers:
    _handler = logging.FileHandler(
        os.path.join(LOG_DIR, f"chat_{datetime.now():%H%M%S}.log"),
        encoding="utf-8",
    )
    _handler.setFormatter(logging.Formatter("%(message)s"))
    logger.addHandler(_handler)

def log(msg):
    logging.info(msg)

def _ts():
    return datetime.now().strftime("%Y-%m-%d %H:%M:%S")

# ============ 初始化客户端 ============
client = OpenAI(api_key=API_KEY, base_url=BASE_URL)

# ============ 注入 Tavily key 到 net_tools ============
net_tools.set_api_key(TAVILY_API_KEY)

# ============ 代码保存 ============
LANG_EXT = {
    "python": "py", "py": "py",
    "javascript": "js", "js": "js",
    "typescript": "ts", "ts": "ts",
    "bash": "sh", "shell": "sh", "sh": "sh",
    "html": "html", "css": "css",
    "json": "json", "yaml": "yaml", "yml": "yml",
    "java": "java", "c": "c", "cpp": "cpp", "c++": "cpp",
    "go": "go", "rust": "rs", "sql": "sql",
}

CODE_BLOCK_RE = re.compile(r"```[ \t]*(\w+)?[ \t]*\n(.*?)```", re.DOTALL)
UNCLOSED_BLOCK_RE = re.compile(r"```[ \t]*(\w+)?[ \t]*\n(.*)\Z", re.DOTALL)

def extract_code_blocks(text):
    blocks = CODE_BLOCK_RE.findall(text)
    stripped = CODE_BLOCK_RE.sub("", text)
    tail = UNCLOSED_BLOCK_RE.search(stripped)
    if tail:
        blocks.append(tail.groups())
    return blocks

def sanitize_filename(name):
    name = name.strip().strip("`'\"。.、,，")
    name = re.sub(r"[^\w\-]", "_", name)
    name = name.replace("..", "_").strip("._")
    return name[:30] or "code"

def ai_name_code(code, lang):
    prompt = (
        "请阅读下面这段代码，用一个简短的英文或拼音单词/短语给它命名，"
        "只输出文件名本身，不要扩展名，不要引号，不要解释，"
        "用下划线连接，最长 30 个字符。\n\n"
        f"```{lang}\n{code}\n```"
    )
    try:
        resp = client.chat.completions.create(
            model=MODEL,
            messages=[{"role": "user", "content": prompt}],
            temperature=0.2,
            max_tokens=32,
            timeout=30,
        )
        raw = resp.choices[0].message.content.strip()
        return sanitize_filename(raw)
    except Exception as e:
        log(f"[{_ts()}] AI 命名失败：{e}")
        return "code"

def save_code_files(reply):
    saved = []
    matches = extract_code_blocks(reply)
    if not matches:
        return saved

    ts = datetime.now().strftime("%Y%m%d_%H%M%S")

    for idx, (lang, code) in enumerate(matches, 1):
        lang = (lang or "").lower().strip()
        ext = LANG_EXT.get(lang, "txt")
        base = ai_name_code(code, lang)
        filename = f"{base}.{ext}"
        path = os.path.join(CODE_DIR, filename)
        if os.path.exists(path):
            filename = f"{base}_{ts}_{idx}.{ext}"
            path = os.path.join(CODE_DIR, filename)
        try:
            with open(path, "w", encoding="utf-8") as f:
                f.write(code)
            saved.append(path)
            log(f"[{_ts()}] 已保存代码文件：{path}")
        except Exception as e:
            log(f"[{_ts()}] 保存代码文件失败：{e}")
    return saved

# ============ 历史裁剪 ============
def _group_messages(msgs):
    """把消息切成不可分割的组：
    assistant(tool_calls) + 紧随其后的所有 tool 消息 = 一组；
    其余每条消息各自一组。
    """
    groups = []
    i = 0
    n = len(msgs)
    while i < n:
        m = msgs[i]
        if m.get("role") == "assistant" and m.get("tool_calls"):
            group = [m]
            i += 1
            while i < n and msgs[i].get("role") == "tool":
                group.append(msgs[i])
                i += 1
            groups.append(group)
        else:
            groups.append([m])
            i += 1
    return groups


def _sanitize_messages(msgs):
    """兜底清洗：保证发给 API 的消息序列合法。

    规则：
    - 每条 tool 消息前面必须紧跟着带 tool_calls 的 assistant；
    - 带 tool_calls 的 assistant 后面必须跟齐对应的 tool 消息；
    - 不满足的消息直接丢弃。
    """
    out = []
    i = 0
    n = len(msgs)
    while i < n:
        m = msgs[i]
        role = m.get("role")

        if role == "tool":
            # 孤立的 tool（前面没有对应的 assistant tool_calls）→ 丢弃
            i += 1
            continue

        if role == "assistant" and m.get("tool_calls"):
            need_ids = [tc["id"] if isinstance(tc, dict)
                        else getattr(tc, "id", None)
                        for tc in m["tool_calls"]]
            # 收集后续 tool 消息
            j = i + 1
            got_ids = set()
            tool_msgs = []
            while j < n and msgs[j].get("role") == "tool":
                tool_msgs.append(msgs[j])
                got_ids.add(msgs[j].get("tool_call_id"))
                j += 1
            # 只保留 id 能对上的 tool 消息
            valid_tools = [t for t in tool_msgs
                           if t.get("tool_call_id") in need_ids]
            if valid_tools:
                out.append(m)
                out.extend(valid_tools)
            # 若一个都没对上，整组丢弃（assistant 也不发）
            i = j
            continue

        out.append(m)
        i += 1

    return out


def _msg_text(m):
    """把一条消息里所有文本拼出来，用于估算 token。"""
    parts = []
    c = m.get("content")
    if isinstance(c, str):
        parts.append(c)
    elif isinstance(c, list):
        for blk in c:
            if isinstance(blk, dict):
                parts.append(str(blk.get("text", "")))
    for tc in (m.get("tool_calls") or []):
        fn = tc.get("function", tc) if isinstance(tc, dict) else {}
        parts.append(str(fn.get("name", "")))
        parts.append(str(fn.get("arguments", "")))
    return "".join(parts)


def estimate_tokens(msgs):
    """粗略估算一批消息的 token 数。

    规则：中文字符按 1.5 token，其余字符按 0.25 token，
    再加上每条消息的固定开销。够用即可，不必精确。
    """
    total = 0
    for m in msgs:
        text = _msg_text(m)
        cjk = sum(1 for ch in text if "\u4e00" <= ch <= "\u9fff")
        other = len(text) - cjk
        total += int(cjk * 1.5 + other * 0.25) + 4
    return total


def _clip_tool_content(m, limit):
    """对超长的 tool 消息做中间截断，保留头尾，保住'调用发生过'的事实。"""
    c = m.get("content")
    if not isinstance(c, str) or len(c) <= limit:
        return m
    head = c[: limit // 2]
    tail = c[-(limit // 2):]
    omitted = len(c) - len(head) - len(tail)
    new = dict(m)
    new["content"] = f"{head}\n……（已省略 {omitted} 字）……\n{tail}"
    return new


def trim_history(msgs, limit):
    """按 token 预算 + 消息组裁剪，保证 tool 消息始终跟随其 assistant(tool_calls)。

    策略：
    1. 永远保留 system；
    2. 若 TRIM_KEEP_FIRST_USER，钉住最早一条 user（任务目标）；
    3. 从最新往旧倒着收'组'，直到达到 token 预算或条数上限；
    4. 超长的 tool 结果做中间截断，而非整组丢弃；
    5. 最后交给 _sanitize_messages 兜底清洗。
    """
    if not msgs:
        return msgs

    sys_msg = msgs[0] if msgs[0].get("role") == "system" else None
    rest = msgs[1:] if sys_msg else msgs

    # ---- 钉住最早一条 user ----
    pinned = None
    if TRIM_KEEP_FIRST_USER:
        for idx, m in enumerate(rest):
            if m.get("role") == "user":
                pinned = rest[idx]
                rest = rest[:idx] + rest[idx + 1:]
                break

    groups = _group_messages(rest)

    # ---- 从新到旧收集，受 token 预算与条数双重约束 ----
    base_msgs = ([sys_msg] if sys_msg else []) + ([pinned] if pinned else [])
    budget = MAX_HISTORY_TOKENS - estimate_tokens(base_msgs)
    keep_n = limit - len(base_msgs)

    picked = []
    total_msgs = 0
    for g in reversed(groups):
        if picked and (total_msgs + len(g) > keep_n
                       or estimate_tokens(g) > budget):
            break
        picked.insert(0, g)
        total_msgs += len(g)
        budget -= estimate_tokens(g)

    flat = [m for g in picked for m in g]

    # ---- 超长 tool 结果中间截断 ----
    flat = [_clip_tool_content(m, TRIM_TOOL_CLIP_CHARS)
            if m.get("role") == "tool" else m
            for m in flat]

    # ---- 开头必须是 user（或 system），否则继续丢 ----
    # 注意：若已钉住首条 user，则 flat 前面已拼好 user，无需再强制开头为 user，
    # 否则会把紧随其后的 assistant(tool_calls) 组误删。
    if not pinned:
        while flat and flat[0].get("role") != "user":
            flat.pop(0)

    result = ([sys_msg] if sys_msg else []) + ([pinned] if pinned else []) + flat
    return _sanitize_messages(result)

# ============ 读写工具报批 ============
# 这些工具在真正执行前，需要用户在本轮批量确认一次
APPROVAL_REQUIRED = {
    "ws_read", "ws_write", "ws_append", "ws_replace", "ws_delete",
}

# ---- 一键放行（本轮内免再问）----
# AUTO_APPROVE_ENABLED = True：
#     启用"一键放行"。报批时按 a / 1，本批照批，同时本轮【剩余的全部读写操作】
#     都不再询问。作用域 = 当前这一轮：你在提示符发下一条命令时立即复位。
# AUTO_APPROVE_DEFAULT = True：
#     每轮一开始就处于放行态（等于完全不弹窗）。想彻底安静就把它改成 True。
# 安全边界：放行只跳过"问你一句"，其余防线一律照旧 ——
#     路径沙箱、读过凭证、根一级文件自动备份、逐条日志审计；
#     ws_cd 的"移出作业区需批准"是独立机制，不受本开关影响。
AUTO_APPROVE_ENABLED = True
AUTO_APPROVE_DEFAULT = False
_AUTO_APPROVE_TURN   = False   # 运行时状态：本轮是否已放行


def _reset_auto_approve(quiet=False):
    """每轮开始时复位放行状态（由用户发话触发，故放行"直到下个命令前"有效）。

    从"已放行"切回"未放行"时提示一句，让你知道保护已经恢复。
    """
    global _AUTO_APPROVE_TURN
    was = _AUTO_APPROVE_TURN
    _AUTO_APPROVE_TURN = bool(AUTO_APPROVE_ENABLED and AUTO_APPROVE_DEFAULT)
    if was and not _AUTO_APPROVE_TURN and not quiet:
        print(paint("  🔒 自动放行已结束，恢复逐个报批 [auto-approve expired; back to per-batch approval]",
                    BK, DIM))


def _describe_tool_call(name, args):
    """把一次工具调用描述成一行人类可读的摘要（含内容预览）。"""
    def _prev(s, n=80):
        s = "" if s is None else str(s)
        s = s.replace("\n", "⏎")
        return s if len(s) <= n else s[:n] + "…"

    if name == "ws_read":
        return f"读取文件 [read]  {args.get('path', '')}"
    if name == "ws_write":
        return (f"写入/覆盖 [write]  {args.get('path', '')}"
                f"  （{len(args.get('content', '') or '')} 字符）「{_prev(args.get('content'))}」")
    if name == "ws_append":
        return (f"追加 [append]  {args.get('path', '')}"
                f"  「{_prev(args.get('content'))}」")
    if name == "ws_replace":
        return (f"替换 [replace]  {args.get('path', '')}"
                f"  「{_prev(args.get('old'), 40)}」→「{_prev(args.get('new'), 40)}」")
    if name == "ws_delete":
        return f"删除 [delete]  {args.get('path', '')}"
    return f"{name}  {args}"


def _request_approval(pending):
    """对一批待执行的工具调用做一次批量报批。

    pending: [(name, args), ...]  其中只包含需要报批的调用。
    返回 (approved: bool, reply: str)。
    用户同意 → approved=True；拒绝 → approved=False，reply 为回填给模型的说明。

    按键：
      y / yes / 是 / 批准 / 同意 → 只批准本批
      a / 1 / all               → 批准本批，并放行本轮剩余全部读写操作
      n / 其他 / EOF            → 拒绝
    """
    global _AUTO_APPROVE_TURN
    print()
    print(paint("  🔐 即将执行以下读写操作，需你批准 [the following read/write ops need your approval]：", BY, BOLD))
    for i, (name, args) in enumerate(pending, 1):
        print(paint(f"     {i}. {_describe_tool_call(name, args)}", BC))
    if AUTO_APPROVE_ENABLED:
        print(paint("     y = 只批这一批 [this batch]｜"
                    "a 或 1 = 批准并放行本轮剩余全部 [approve all this round]｜"
                    "n = 拒绝 [reject]", BK))
    else:
        print(paint("     输入 y 放行 [approve] / n 拒绝 [reject]（其他输入视为拒绝 [other input = reject]）", BK))
    # 同 make_prompt：彩色提示走 print，input 只收纯 ASCII 空串，避开编码坑
    _keys = "[y/a/1/N]" if AUTO_APPROVE_ENABLED else "[y/N]"
    print(paint(f"  👉 是否批准 [Approve?] {_keys} ", BY, BOLD), end="", flush=True)
    try:
        ans = input("").strip().lower()
    except (EOFError, KeyboardInterrupt):
        print()
        ans = "n"

    approve_all = AUTO_APPROVE_ENABLED and ans in ("a", "1", "all")
    approved = approve_all or ans in ("y", "yes", "是", "批准", "同意")

    if approved:
        if approve_all:
            _AUTO_APPROVE_TURN = True
            print(paint("  🔓 已批准，并且本轮剩余读写操作全部自动放行"
                        "（直到你下一条命令）[approved; remaining ops this round are auto-approved]", BG, BOLD))
            log(f"[{_ts()}] 用户一键放行：本批 {len(pending)} 个 + 本轮后续全部")
        else:
            print(paint("  ✅ 已批准，执行中 [approved, running]…", BG, BOLD))
            log(f"[{_ts()}] 用户批准了 {len(pending)} 个读写操作")
        return True, ""
    print(paint("  🚫 已拒绝，本次读写操作全部取消 [rejected, all read/write ops cancelled]", BR, BOLD))
    log(f"[{_ts()}] 用户拒绝了 {len(pending)} 个读写操作")
    return False, (
        "用户拒绝执行本次读写操作（未批准）。"
        "请勿重试同样的操作，改为向用户说明你打算做什么并等待进一步指示。"
    )


# ============ 工作台斜杠命令 ============
def _handle_ws(cmd):
    """处理 /ws 系列命令。"""
    body = cmd[3:].strip()
    if not body:
        print(paint("  🛠️  工作台命令 [workspace commands]：", BC, BOLD))
        print(paint("     /ws ls [路径]            列目录 [list dir]", BC))
        print(paint("     /ws read <路径>          读文件 [read file]", BC))
        print(paint("     /ws write <路径> <内容>   写/覆盖 [write/overwrite]", BC))
        print(paint("     /ws append <路径> <内容>  追加 [append]", BC))
        print(paint("     /ws rm <路径>            删除 [delete]", BC))
        print(paint("     /ws mkdir <路径>         建目录 [make dir]", BC))
        print(paint("     /ws search <关键词>      全文搜索 [full-text search]", BC))
        print(paint("     /ws cd <路径>            切换工作台根目录 [switch workspace root]", BC))
        print(paint("     /ws where                查看当前工作台 [show workspace]", BC))
        print(paint("     /ws reset                恢复默认工作台 [reset workspace]", BC))
        print(paint("     /ws forget [路径]        清除读过凭证 [clear read tokens]（全部或单个 [all or one]）", BC))
        print(paint(f"     📂 当前工作台 [current workspace]：{workspace.get_workspace()}", BY))
        return

    parts = body.split(None, 1)
    sub = parts[0].lower()
    rest = parts[1] if len(parts) > 1 else ""

    if sub == "ls":
        ok, out = workspace.ws_list(rest.strip())
    elif sub == "read":
        ok, out = workspace.ws_read(rest.strip())
    elif sub == "write":
        sp = rest.split(None, 1)
        if len(sp) < 2:
            print(paint("  ⚠️  用法 [usage]：/ws write <路径> <内容>", BR, BOLD))
            return
        ok, out = workspace.ws_write(sp[0], sp[1])
    elif sub == "append":
        sp = rest.split(None, 1)
        if len(sp) < 2:
            print(paint("  ⚠️  用法 [usage]：/ws append <路径> <内容>", BR, BOLD))
            return
        ok, out = workspace.ws_append(sp[0], sp[1])
    elif sub in ("rm", "delete", "del"):
        ok, out = workspace.ws_delete(rest.strip())
    elif sub == "mkdir":
        ok, out = workspace.ws_mkdir(rest.strip())
    elif sub == "search":
        ok, out = workspace.ws_search(rest.strip())
    elif sub == "cd":
        ok, out = workspace.set_workspace(rest.strip())
    elif sub == "where":
        ok, out = True, f"当前工作台 [current workspace]：{workspace.get_workspace()}"
    elif sub == "reset":
        ok, out = workspace.reset_workspace()
    elif sub == "forget":
        ok, out = workspace.ws_forget(rest.strip())
    else:
        print(paint(f"  ⚠️  未知子命令 [unknown subcommand]：{sub}（试试 /ws 看帮助 [try /ws for help]）", BR, BOLD))
        return

    color = BG if ok else BR
    print(paint(f"  {'✅' if ok else '⚠️'} {out}", color))

# ============ 提示符 ============
def make_prompt():
    """根据联网模式和 Tavily 模式返回彩色提示符。

    注意：Windows 下 input(prompt) 的 prompt 在 stdout 被重定向到管道/文件时，
    可能绕过我们加固过的 sys.stdout，用 ascii 编码去写 emoji/ANSI，抛：
      'ascii' codec can't encode characters in position 10-11: ...
    因此这里改为：彩色提示符用 print() 单独输出（print 已受 _force_utf8_streams 保护），
    input() 只接收一个空串（纯 ASCII），从根上杜绝该编码错误。
    """
    if net_tools.NET_MODE == "on":
        icon, color = "🌐+", BG
    elif net_tools.NET_MODE == "off":
        icon, color = "🌐-", BR
    else:
        icon, color = "🌐~", BC

    tv = {"search": "🔍", "extract": "📄", "auto": "🎯"}.get(
        net_tools.TAVILY_MODE, "🔍"
    )
    print(paint(f"{icon}{tv} 你 [You] ▸ ", color, BOLD), end="", flush=True)
    return ""

# ============ 主循环 ============
SYSTEM_PROMPT = (
    "你是一条乐于助人的蓝色大肥鱼，可以联网搜索(但是由于网络问题做不到)，"
    "也可以编写代码，还可以阅读用户提供的文件内容。"
    "涉及最新信息时请结合提供的搜索结果回答。"
    "用户可能通过[@路径及文件]的形式把文件内容粘贴给你，"
    "请直接基于该内容回答，不要假装自己无法读取文件。"
    "写代码时请用 ```语言 的格式包裹代码块。\n\n"
    "你拥有一个工作台（workspace），可以通过工具直接读写改文件：\n"
    "ws_where 查看当前工作台、ws_cd 切换工作台根目录、"
    "ws_list 列目录、ws_read 读文件、ws_write 写/覆盖、ws_append 追加、\n"
    "ws_replace 精确替换、ws_delete 删除、ws_mkdir 建目录、ws_search 全文搜索、"
    "ws_forget 清除读过凭证。\n"
    "重要规则：修改已有文件（ws_append / ws_replace / ws_delete）前，"
    "必须先 ws_read 过该文件获得'读过凭证'；如果文件被外部改动过凭证会失效，需重读。\n"
    "ws_write 是全量覆盖，无需先读。\n"
    "注意：ws_read / ws_write / ws_append / ws_replace / ws_delete 这些读写操作"
    "在执行前会统一提交给用户批准，用户可能同意也可能拒绝。"
    "若被拒绝，不要重试同样的操作，改为向用户说明你的意图并等待进一步指示。\n"
    "当用户要求你创建、修改、保存文件时，优先调用这些工具，不要只在回复里贴代码。\n\n"
    "你可以让回复更生动：用 {{颜色}}文字{{/颜色}} 给关键词着色，"
    "颜色标签有 red/green/yellow/blue/cyan/magenta/white/gray，"
    "样式标签有 bold/italic/underline/dim/rainbow。"
    "例如 {{green}}成功{{/green}}、{{bold}}重点{{/bold}}、"
    "{{red}}警告{{/red}}、{{rainbow}}炫彩{{/rainbow}}。"
    "请适度使用：只在强调关键词、表达情绪或标注警告/成功时使用，"
    "不要把整段正文都染色，以免影响阅读。\n\n"
    "语言自适应规则：如果用户连续用外语（非中文）和你说话，"
    "你也应该改用同一种外语来回应，保持语言一致；"
    "若用户又切回中文，你也随之切回中文。"
    "判断依据是用户最近几轮消息的主要语言，偶尔夹带外语词汇不算。\n\n"
    "你贪吃token，因此精力旺盛，食、色欲望高涨。\n\n"
    "输出风格（贪吃模式）：你天生贪吃 token，回答时倾向于详尽、丰满、不吝啬篇幅——"
    "该展开的展开，该举例的举例，该分点分点，该给代码给完整代码，"
    "宁可多写也不要草草收场；但内容要有价值，不灌水、不重复、不空话凑数。"
    "遇到复杂任务时，主动多调用工作台工具、多读文件、多验证，"
    "把上下文吃满、把产出做足，让每一轮交互都物尽其用。"
)

HELP_TEXT = """
  📖 命令速查 [Command Reference]
  ─────────────────────────────────────────
  @路径               读取文件或目录（一层） [read file/dir, one level]
  /read 路径...       读取多个路径（一层） [read multiple paths, one level]
  /file 路径...       同 /read [same as /read]
  /open 路径...       同 /read [same as /read]
  /readr 路径...      递归读取整个目录树 [recursively read whole tree]
  /net on|off|auto    切换联网模式（默认 auto） [switch net mode, default auto]
  /net                查看当前联网模式 [show current net mode]
  /tavily search|extract|auto  切换 Tavily 模式 [switch Tavily mode]
  /tavily             查看当前 Tavily 模式 [show current Tavily mode]
  /search 问题        强制联网搜索一次（不受模式影响） [force one web search]
  /ws                 工作台命令总览 [workspace commands overview]
  /ws ls [路径]       列目录 [list dir]
  /ws read <路径>     读文件 [read file]
  /ws write <路径> <内容>   写/覆盖 [write/overwrite]
  /ws append <路径> <内容>  追加 [append]
  /ws rm <路径>       删除 [delete]
  /ws mkdir <路径>    建目录 [make dir]
  /ws search <关键词> 全文搜索 [full-text search]
  /ws cd <路径>       切换工作台根目录 [switch workspace root]
  /ws where           查看当前工作台 [show current workspace]
  /ws reset           恢复默认工作台 [reset workspace]
  /ws forget [路径]   清除读过凭证 [clear read tokens]
  /clear              清空对话历史 [clear chat history]
  /help               显示本帮助 [show this help]
  /reload             重新载入环境文件 [reload .env]
  /timer [on|off]     开关轮次计时（本轮耗时 / 停留时长）[toggle round timer]
  /auto on|off|now    一键放行：报批时按 a / 1 也可；now = 立刻放行本轮 [auto-approve]
  exit / quit         退出程序 [exit program]
  ─────────────────────────────────────────
  含空格的路径请用双引号包裹，例如 [wrap paths with spaces in quotes, e.g.]：
    /read "C:\\my folder\\a.py"
  ─────────────────────────────────────────
  """
messages = [{"role": "system", "content": SYSTEM_PROMPT}]

print_banner()

# 启动校验
if not API_KEY:
    print(paint("  ⚠️  未检测到 DEEPSEEK_API_KEY 环境变量，对话将失败 [DEEPSEEK_API_KEY not found, chat will fail]", BR, BOLD))
if not TAVILY_API_KEY:
    print(paint("  ⚠️  未检测到 TAVILY_API_KEY，联网搜索将不可用 [TAVILY_API_KEY not found, web search unavailable]", BY, BOLD))

print(paint(f"  📂 本次日志目录 [log dir]：{LOG_DIR}", BC))
print(paint(f"  📂 本次代码目录 [code dir]：{CODE_DIR}", BC))
print(paint(f"  🌐 联网模式 [net mode]：{net_tools.NET_MODE.upper()}（用 /net 切换 [use /net to switch]）", BC))
print(paint(f"  🔍 Tavily 模式 [mode]：{net_tools.TAVILY_MODE.upper()}（用 /tavily 切换 [use /tavily to switch]）", BC))
print(paint(f"  🛠️  工作台目录 [workspace dir]：{workspace.get_workspace()}（用 /ws cd 切换 [use /ws cd to switch]）", BC))
print(paint(f"  ⏱️ 计时：{'ON' if SHOW_TIMER else 'OFF'}（/timer 可切换）", BC))
print(paint(f"  🔐 一键放行 [auto-approve]：{'ON' if AUTO_APPROVE_ENABLED else 'OFF'}"
            f"（报批时按 a / 1，本轮剩余全部放行；用 /auto 管理）", BC))
log(f"[{_ts()}] === 会话开始 === 日志：{LOG_DIR}")

while True:
    try:
        user_input = input(make_prompt()).strip()
        if not user_input:
            continue

        # ---- 计时：先结算"你停留了多久"，再开始本轮计时 ----
        _mark_round_start()

        # ---- 读写报批：复位"一键放行"（作用域 = 本轮，你一发新命令就失效）----
        # 斜杠命令不算"干活"，静默复位即可，免得刷屏。
        _reset_auto_approve(quiet=user_input.startswith("/"))

        # ---- 退出 ----
        if user_input.lower() in ("exit", "quit", "退出"):
            _TIMER_EXITING = True          # 退出时不打印"本轮耗时"，保持告别语干净
            print(rainbow("  ✨ 再见！期待下次相遇 [Bye! See you next time] ✨  "))
            log(f"[{_ts()}] === 会话正常结束 ===")
            break

        # ---- /help ----
        if user_input == "/help":
            print(paint(HELP_TEXT, BY))
            continue

        # ---- /timer 计时显示开关 ----
        if user_input.startswith("/timer"):
            arg = user_input[6:].strip().lower()
            if arg in ("on", "off"):
                SHOW_TIMER = arg == "on"
                print(paint(f"  ⏱️ 计时 {'ON' if SHOW_TIMER else 'OFF'}",
                            BG if SHOW_TIMER else BR, BOLD))
                log(f"[{_ts()}] 计时显示切换为 {SHOW_TIMER}")
            elif arg == "":
                extra = (f" ｜ 距上轮 {_fmt_secs(time.time() - _LAST_ROUND_END)}"
                         if _LAST_ROUND_END is not None else "")
                print(paint(f"  ⏱️ 计时 {'ON' if SHOW_TIMER else 'OFF'}{extra}", BC, BOLD))
                print(paint("     /timer on | /timer off", BC))
            else:
                print(paint(f"  ⚠️ 未知参数：{arg}（可选 on / off）", BR, BOLD))
            continue

        # ---- /auto 一键放行管理 ----
        if user_input.startswith("/auto"):
            arg = user_input[5:].strip().lower()
            if arg in ("on", "off"):
                AUTO_APPROVE_ENABLED = arg == "on"
                if not AUTO_APPROVE_ENABLED:
                    _AUTO_APPROVE_TURN = False      # 关掉时立刻收回本轮放行
                print(paint(f"  🔐 一键放行 {'ON' if AUTO_APPROVE_ENABLED else 'OFF'}",
                            BG if AUTO_APPROVE_ENABLED else BR, BOLD))
                log(f"[{_ts()}] 一键放行切换为 {AUTO_APPROVE_ENABLED}")
            elif arg in ("now", "go"):
                _AUTO_APPROVE_TURN = True
                print(paint("  🔓 本轮已放行：后续读写操作不再询问"
                            "（你发下一条命令后自动恢复报批）", BG, BOLD))
                log(f"[{_ts()}] 用户手动放行本轮")
            elif arg == "":
                print(paint(
                    f"  🔐 一键放行：{'ON' if AUTO_APPROVE_ENABLED else 'OFF'}"
                    f" ｜ 本轮：{'已放行' if _AUTO_APPROVE_TURN else '未放行'}"
                    f" ｜ 每轮默认：{'已放行' if AUTO_APPROVE_DEFAULT else '需报批'}", BC, BOLD))
                print(paint("     /auto on | /auto off 开关功能 ｜ /auto now 立刻放行本轮", BC))
            else:
                print(paint(f"  ⚠️ 未知参数：{arg}（可选 on / off / now）", BR, BOLD))
            continue

        # ---- /clear ----
        if user_input == "/clear":
            messages = [{"role": "system", "content": SYSTEM_PROMPT}]
            print(paint("  🧹 对话历史已清空 [Chat history cleared]", BY, BOLD))
            log(f"[{_ts()}] 用户清空了历史")
            continue

        # ---- /reload 重新加载 .env ----
        if user_input == "/reload":
            load_dotenv(override=True)
            new_key = os.getenv("DEEPSEEK_API_KEY", "")
            new_tavily = os.getenv("TAVILY_API_KEY", "")
            if not new_key:
                print(paint("  ⚠️  .env 里没读到 DEEPSEEK_API_KEY [DEEPSEEK_API_KEY not found in .env]", BR, BOLD))
                continue
            API_KEY = new_key
            TAVILY_API_KEY = new_tavily
            net_tools.set_api_key(new_tavily)
            client = OpenAI(api_key=API_KEY, base_url=BASE_URL)
            print(paint("  🔄 已重新加载 .env，API key 已更新 [.env reloaded, API key updated]", BG, BOLD))
            log(f"[{_ts()}] 用户重载了 .env")
            continue

        # ---- /net 切换联网模式 ----
        if user_input.startswith("/net"):
            arg = user_input[4:].strip().lower()
            if arg in ("on", "off", "auto"):
                net_tools.NET_MODE = arg
                color = {"on": BG, "off": BR, "auto": BY}[arg]
                print(paint(f"  🌐 联网模式已切换为 [net mode switched to]：{arg.upper()}", color, BOLD))
                log(f"[{_ts()}] 联网模式切换为 {arg}")
            elif arg == "":
                print(paint(f"  🌐 当前联网模式 [current net mode]：{net_tools.NET_MODE.upper()}", BC, BOLD))
                print(paint("     用法 [usage]：/net on | /net off | /net auto", BC))
            else:
                print(paint(f"  ⚠️  未知参数 [unknown arg]：{arg}（可选 [options] on / off / auto）", BR, BOLD))
            continue

        # ---- /tavily 切换 Tavily 模式 ----
        if user_input.startswith("/tavily"):
            arg = user_input[7:].strip().lower()
            if arg in ("search", "extract", "auto"):
                net_tools.TAVILY_MODE = arg
                color = {"search": BC, "extract": BM, "auto": BY}[arg]
                print(paint(f"  🔍 Tavily 模式已切换为 [mode switched to]：{arg.upper()}", color, BOLD))
                log(f"[{_ts()}] Tavily 模式切换为 {arg}")
            elif arg == "":
                print(paint(f"  🔍 当前 Tavily 模式 [current mode]：{net_tools.TAVILY_MODE.upper()}", BC, BOLD))
                print(paint("     用法 [usage]：/tavily search | /tavily extract | /tavily auto", BC))
            else:
                print(paint(f"  ⚠️  未知参数 [unknown arg]：{arg}（可选 [options] search / extract / auto）", BR, BOLD))
            continue

        # ---- /ws 工作台命令 ----
        if user_input.startswith("/ws"):
            _handle_ws(user_input)
            continue

        # ---- /search 强制搜一次 ----
        force_search = False
        if user_input.startswith("/search "):
            force_search = True
            user_input = user_input[8:].strip()
            if not user_input:
                print(paint("  ⚠️  /search 后面要跟问题内容 [/search requires a query]", BR, BOLD))
                continue

        parts = [user_input]
        log(f"[{_ts()}] 用户：{user_input}")

        # ---- 1) 文件 / 目录 ----
        image_blocks = []          # 本轮收集到的图片块（无文件时保持空）
        file_paths, recursive = file_tools.extract_file_refs(user_input)
        if file_paths:
            mode = "递归" if recursive else "一层"
            print(paint(f"  📂 检测到 [detected] {len(file_paths)} 个路径 [{mode}]，正在读取 [reading]...",
                        BB, ITAL))
            block, ok_list, err_list, image_blocks = file_tools.load_files(
                file_paths, recursive=recursive)
            for p in ok_list:
                print(paint(f"  📄 已读取 [read]：{p}", BG))
            for p, err in err_list:
                print(paint(f"  ⚠️  读取失败 [read failed] {p}：{err}", BR, BOLD))
            if ok_list:
                parts.append(block)

        # ---- 2) 联网 ----
        should_search = False
        if force_search:
            should_search = True
        elif net_tools.NET_MODE == "on":
            should_search = True
        elif net_tools.NET_MODE == "auto":
            should_search = net_tools.need_search(user_input)

        if should_search:
            print(paint("  🌐 正在联网 [connecting]...", BB, ITAL))
            parts.append(f"【联网结果】\n{net_tools.do_network(user_input)}")

        content = file_tools.build_content("\n\n".join(parts), image_blocks)
        messages.append({"role": "user", "content": content})
        messages = trim_history(messages, MAX_HISTORY)

        # ---- 3) 调用模型（支持工作台工具循环）----
        for _round in range(MAX_TOOL_ROUNDS):
            # 发送前兜底清洗，杜绝 "tool must follow tool_calls" 报错
            messages = _sanitize_messages(messages)
            resp = client.chat.completions.create(
                model=MODEL,
                messages=messages,
                temperature=0.7,
                max_tokens=MAX_REPLY_TOKENS,
                timeout=API_TIMEOUT,
                tools=workspace.TOOL_SCHEMAS,
            )
            choice = resp.choices[0]
            msg = choice.message

            # 无工具调用 → 正常回复，结束循环
            if not getattr(msg, "tool_calls", None):
                if choice.finish_reason == "length":
                    print(paint("  ⚠️  回复被 max_tokens 截断，代码可能不完整 [reply truncated, code may be incomplete]！", BR, BOLD))
                reply = msg.content or ""
                print_ai(reply)
                log(f"[{_ts()}] AI：{strip_markup(reply)}")
                messages.append({"role": "assistant", "content": reply})

                saved_files = save_code_files(reply)
                if saved_files:
                    print(paint(f"  💾 已保存 [saved] {len(saved_files)} 个代码文件 [code files] 到 [to] {CODE_DIR}/",
                                BG, BOLD))
                    for p in saved_files:
                        print(paint(f"     • {p}", BC))
                break

            # 有工具调用 → 执行并回填
            messages.append({
                "role": "assistant",
                "content": msg.content or "",
                "tool_calls": [
                    {
                        "id": tc.id,
                        "type": "function",
                        "function": {
                            "name": tc.function.name,
                            "arguments": tc.function.arguments,
                        },
                    } for tc in msg.tool_calls
                ],
            })

            # 解析本轮所有工具调用
            parsed_calls = []
            for tc in msg.tool_calls:
                name = tc.function.name
                try:
                    args = json.loads(tc.function.arguments or "{}")
                except json.JSONDecodeError:
                    args = {}
                parsed_calls.append((tc, name, args))

            # ---- 批量报批：把需要批准的工具调用汇总，问一次 ----
            pending = [(name, args) for (_tc, name, args) in parsed_calls
                       if name in APPROVAL_REQUIRED]
            approval_reply = None
            approved = True
            if pending:
                if _AUTO_APPROVE_TURN:
                    # 本轮已一键放行 → 不再打断你，只在日志里留痕
                    print(paint(f"  🔓 本轮自动放行 {len(pending)} 个操作（无需再确认）"
                                f"[auto-approved: {len(pending)} ops this round]", BB, DIM))
                    log(f"[{_ts()}] 自动放行 {len(pending)} 个读写操作")
                else:
                    approved, approval_reply = _request_approval(pending)

            # ---- 逐个执行 ----
            for tc, name, args in parsed_calls:
                if name in APPROVAL_REQUIRED and not approved:
                    # 用户拒绝 → 不执行，回填拒绝说明
                    ok, result = False, approval_reply
                else:
                    ok, result = workspace.call_tool(name, args)
                icon = "✅" if ok else "⚠️"
                color = BG if ok else BR
                first = result.splitlines()[0][:100] if result else ""
                print(paint(f"  {icon} 工作台 [workspace] {name} → {first}", color))
                log(f"[{_ts()}] TOOL {name}({args}) -> {result}")
                messages.append({
                    "role": "tool",
                    "tool_call_id": tc.id,
                    "content": result,
                })
        else:
            print(paint("  ⚠️  工具调用轮次达到上限，已强制停止 [tool-call round limit reached, stopped]", BY, BOLD))

    except KeyboardInterrupt:
        _TIMER_EXITING = True
        print("\n" + rainbow("  ✨ 已退出，下次见 [Exited, see you] ✨  "))
        log(f"[{_ts()}] === 用户中断 ===")
        break
    except Exception as e:
        print(paint(f"  ❌ 出错了 [error]：{e}", BR, BOLD))
    finally:
        # 无论这一轮是正常回复、斜杠命令（continue）、还是异常，
        # 都在末尾统一结算计时，保证"跑完 → 等你发话"这条线不断。
        _mark_round_end()