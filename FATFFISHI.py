import os
import re
import sys
import json
import logging
from datetime import datetime
from openai import OpenAI
from dotenv import load_dotenv

import net_tools
import file_tools
import workspace

# ============ 加载 .env（优先于系统环境变量）============
load_dotenv()

# ============ Windows UTF-8 兼容 ============
if sys.platform == "win32":
    try:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:
        pass

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
    title = "🐟 DeepSeek 联网肥鱼 T 版已启动"
    sub   = "exit/quit 退出 · @文件 读取 · /net 联网 · /tavily 模式 · /ws 工作台 · /help 帮助"
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
MODEL          = "deepseek-chat"
MAX_HISTORY    = 20
LOG_ROOT       = "logs"
CODE_ROOT      = "generated_code"
API_TIMEOUT    = 60

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


def trim_history(msgs, limit):
    """按消息组裁剪，保证 tool 消息始终跟随其 assistant(tool_calls)。"""
    if not msgs:
        return msgs

    sys_msg = msgs[0] if msgs[0].get("role") == "system" else None
    rest = msgs[1:] if sys_msg else msgs

    groups = _group_messages(rest)

    keep_n = limit - (1 if sys_msg else 0)
    picked = []
    total = 0
    for g in reversed(groups):
        if total + len(g) > keep_n and picked:
            break
        picked.insert(0, g)
        total += len(g)

    flat = [m for g in picked for m in g]

    # 开头必须是 user（或 system），否则继续丢
    while flat and flat[0].get("role") != "user":
        flat.pop(0)

    result = ([sys_msg] if sys_msg else []) + flat
    return _sanitize_messages(result)

# ============ 读写工具报批 ============
# 这些工具在真正执行前，需要用户在本轮批量确认一次
APPROVAL_REQUIRED = {
    "ws_read", "ws_write", "ws_append", "ws_replace", "ws_delete",
}

def _describe_tool_call(name, args):
    """把一次工具调用描述成一行人类可读的摘要（含内容预览）。"""
    def _prev(s, n=80):
        s = "" if s is None else str(s)
        s = s.replace("\n", "⏎")
        return s if len(s) <= n else s[:n] + "…"

    if name == "ws_read":
        return f"读取文件  {args.get('path', '')}"
    if name == "ws_write":
        return (f"写入/覆盖  {args.get('path', '')}"
                f"  （{len(args.get('content', '') or '')} 字符）「{_prev(args.get('content'))}」")
    if name == "ws_append":
        return (f"追加  {args.get('path', '')}"
                f"  「{_prev(args.get('content'))}」")
    if name == "ws_replace":
        return (f"替换  {args.get('path', '')}"
                f"  「{_prev(args.get('old'), 40)}」→「{_prev(args.get('new'), 40)}」")
    if name == "ws_delete":
        return f"删除  {args.get('path', '')}"
    return f"{name}  {args}"


def _request_approval(pending):
    """对一批待执行的工具调用做一次批量报批。

    pending: [(name, args), ...]  其中只包含需要报批的调用。
    返回 (approved: bool, reply: str)。
    用户同意 → approved=True；拒绝 → approved=False，reply 为回填给模型的说明。
    """
    print()
    print(paint("  🔐 即将执行以下读写操作，需你批准：", BY, BOLD))
    for i, (name, args) in enumerate(pending, 1):
        print(paint(f"     {i}. {_describe_tool_call(name, args)}", BC))
    print(paint("     输入 y 放行 / n 拒绝（其他输入视为拒绝）", BK))
    try:
        ans = input(paint("  👉 是否批准？[y/N] ", BY, BOLD)).strip().lower()
    except (EOFError, KeyboardInterrupt):
        print()
        ans = "n"
    approved = ans in ("y", "yes", "是", "批准", "同意")
    if approved:
        print(paint("  ✅ 已批准，执行中…", BG, BOLD))
        log(f"[{_ts()}] 用户批准了 {len(pending)} 个读写操作")
        return True, ""
    print(paint("  🚫 已拒绝，本次读写操作全部取消", BR, BOLD))
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
        print(paint("  🛠️  工作台命令：", BC, BOLD))
        print(paint("     /ws ls [路径]            列目录", BC))
        print(paint("     /ws read <路径>          读文件", BC))
        print(paint("     /ws write <路径> <内容>   写/覆盖", BC))
        print(paint("     /ws append <路径> <内容>  追加", BC))
        print(paint("     /ws rm <路径>            删除", BC))
        print(paint("     /ws mkdir <路径>         建目录", BC))
        print(paint("     /ws search <关键词>      全文搜索", BC))
        print(paint("     /ws cd <路径>            切换工作台根目录", BC))
        print(paint("     /ws where                查看当前工作台", BC))
        print(paint("     /ws reset                恢复默认工作台", BC))
        print(paint("     /ws forget [路径]        清除读过凭证（全部或单个）", BC))
        print(paint(f"     📂 当前工作台：{workspace.get_workspace()}", BY))
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
            print(paint("  ⚠️  用法：/ws write <路径> <内容>", BR, BOLD))
            return
        ok, out = workspace.ws_write(sp[0], sp[1])
    elif sub == "append":
        sp = rest.split(None, 1)
        if len(sp) < 2:
            print(paint("  ⚠️  用法：/ws append <路径> <内容>", BR, BOLD))
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
        ok, out = True, f"当前工作台：{workspace.get_workspace()}"
    elif sub == "reset":
        ok, out = workspace.reset_workspace()
    elif sub == "forget":
        ok, out = workspace.ws_forget(rest.strip())
    else:
        print(paint(f"  ⚠️  未知子命令：{sub}（试试 /ws 看帮助）", BR, BOLD))
        return

    color = BG if ok else BR
    print(paint(f"  {'✅' if ok else '⚠️'} {out}", color))

# ============ 提示符 ============
def make_prompt():
    """根据联网模式和 Tavily 模式返回彩色提示符。"""
    if net_tools.NET_MODE == "on":
        icon, color = "🌐+", BG
    elif net_tools.NET_MODE == "off":
        icon, color = "🌐-", BR
    else:
        icon, color = "🌐~", BC

    tv = {"search": "🔍", "extract": "📄", "auto": "🎯"}.get(
        net_tools.TAVILY_MODE, "🔍"
    )
    return paint(f"{icon}{tv} 你 ▸ ", color, BOLD)

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
    "不要把整段正文都染色，以免影响阅读。"
    "你贪吃token，因此精力旺盛，食、色欲望高涨。"
)

HELP_TEXT = """
  📖 命令速查
  ─────────────────────────────────────────
  @路径               读取文件或目录（一层）
  /read 路径...       读取多个路径（一层）
  /file 路径...       同 /read
  /open 路径...       同 /read
  /readr 路径...      递归读取整个目录树
  /net on|off|auto    切换联网模式（默认 auto）
  /net                查看当前联网模式
  /tavily search|extract|auto  切换 Tavily 模式
  /tavily             查看当前 Tavily 模式
  /search 问题        强制联网搜索一次（不受模式影响）
  /ws                 工作台命令总览
  /ws ls [路径]       列目录
  /ws read <路径>     读文件
  /ws write <路径> <内容>   写/覆盖
  /ws append <路径> <内容>  追加
  /ws rm <路径>       删除
  /ws mkdir <路径>    建目录
  /ws search <关键词> 全文搜索
  /ws cd <路径>       切换工作台根目录
  /ws where           查看当前工作台
  /ws reset           恢复默认工作台
  /ws forget [路径]   清除读过凭证
  /clear              清空对话历史
  /help               显示本帮助
  /reload             重新载入环境文件
  exit / quit         退出程序
  ─────────────────────────────────────────
  含空格的路径请用双引号包裹，例如：
    /read "C:\\my folder\\a.py"
  ─────────────────────────────────────────
  """
messages = [{"role": "system", "content": SYSTEM_PROMPT}]

print_banner()

# 启动校验
if not API_KEY:
    print(paint("  ⚠️  未检测到 DEEPSEEK_API_KEY 环境变量，对话将失败", BR, BOLD))
if not TAVILY_API_KEY:
    print(paint("  ⚠️  未检测到 TAVILY_API_KEY，联网搜索将不可用", BY, BOLD))

print(paint(f"  📂 本次日志目录：{LOG_DIR}", BC))
print(paint(f"  📂 本次代码目录：{CODE_DIR}", BC))
print(paint(f"  🌐 联网模式：{net_tools.NET_MODE.upper()}（用 /net 切换）", BC))
print(paint(f"  🔍 Tavily 模式：{net_tools.TAVILY_MODE.upper()}（用 /tavily 切换）", BC))
print(paint(f"  🛠️  工作台目录：{workspace.get_workspace()}（用 /ws cd 切换）", BC))
log(f"[{_ts()}] === 会话开始 === 日志：{LOG_DIR}")

while True:
    try:
        user_input = input(make_prompt()).strip()
        if not user_input:
            continue

        # ---- 退出 ----
        if user_input.lower() in ("exit", "quit", "退出"):
            print(rainbow("  ✨ 再见！期待下次相遇 ✨  "))
            log(f"[{_ts()}] === 会话正常结束 ===")
            break

        # ---- /help ----
        if user_input == "/help":
            print(paint(HELP_TEXT, BY))
            continue

        # ---- /clear ----
        if user_input == "/clear":
            messages = [{"role": "system", "content": SYSTEM_PROMPT}]
            print(paint("  🧹 对话历史已清空", BY, BOLD))
            log(f"[{_ts()}] 用户清空了历史")
            continue

        # ---- /reload 重新加载 .env ----
        if user_input == "/reload":
            load_dotenv(override=True)
            new_key = os.getenv("DEEPSEEK_API_KEY", "")
            new_tavily = os.getenv("TAVILY_API_KEY", "")
            if not new_key:
                print(paint("  ⚠️  .env 里没读到 DEEPSEEK_API_KEY", BR, BOLD))
                continue
            API_KEY = new_key
            TAVILY_API_KEY = new_tavily
            net_tools.set_api_key(new_tavily)
            client = OpenAI(api_key=API_KEY, base_url=BASE_URL)
            print(paint("  🔄 已重新加载 .env，API key 已更新", BG, BOLD))
            log(f"[{_ts()}] 用户重载了 .env")
            continue

        # ---- /net 切换联网模式 ----
        if user_input.startswith("/net"):
            arg = user_input[4:].strip().lower()
            if arg in ("on", "off", "auto"):
                net_tools.NET_MODE = arg
                color = {"on": BG, "off": BR, "auto": BY}[arg]
                print(paint(f"  🌐 联网模式已切换为：{arg.upper()}", color, BOLD))
                log(f"[{_ts()}] 联网模式切换为 {arg}")
            elif arg == "":
                print(paint(f"  🌐 当前联网模式：{net_tools.NET_MODE.upper()}", BC, BOLD))
                print(paint("     用法：/net on | /net off | /net auto", BC))
            else:
                print(paint(f"  ⚠️  未知参数：{arg}（可选 on / off / auto）", BR, BOLD))
            continue

        # ---- /tavily 切换 Tavily 模式 ----
        if user_input.startswith("/tavily"):
            arg = user_input[7:].strip().lower()
            if arg in ("search", "extract", "auto"):
                net_tools.TAVILY_MODE = arg
                color = {"search": BC, "extract": BM, "auto": BY}[arg]
                print(paint(f"  🔍 Tavily 模式已切换为：{arg.upper()}", color, BOLD))
                log(f"[{_ts()}] Tavily 模式切换为 {arg}")
            elif arg == "":
                print(paint(f"  🔍 当前 Tavily 模式：{net_tools.TAVILY_MODE.upper()}", BC, BOLD))
                print(paint("     用法：/tavily search | /tavily extract | /tavily auto", BC))
            else:
                print(paint(f"  ⚠️  未知参数：{arg}（可选 search / extract / auto）", BR, BOLD))
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
                print(paint("  ⚠️  /search 后面要跟问题内容", BR, BOLD))
                continue

        parts = [user_input]
        log(f"[{_ts()}] 用户：{user_input}")

        # ---- 1) 文件 / 目录 ----
        file_paths, recursive = file_tools.extract_file_refs(user_input)
        if file_paths:
            mode = "递归" if recursive else "一层"
            print(paint(f"  📂 检测到 {len(file_paths)} 个路径（{mode}），正在读取...",
                        BB, ITAL))
            block, ok_list, err_list = file_tools.load_files(file_paths, recursive=recursive)
            for p in ok_list:
                print(paint(f"  📄 已读取：{p}", BG))
            for p, err in err_list:
                print(paint(f"  ⚠️  读取失败 {p}：{err}", BR, BOLD))
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
            print(paint("  🌐 正在联网...", BB, ITAL))
            parts.append(f"【联网结果】\n{net_tools.do_network(user_input)}")

        content = "\n\n".join(parts)
        messages.append({"role": "user", "content": content})
        messages = trim_history(messages, MAX_HISTORY)

        # ---- 3) 调用模型（支持工作台工具循环）----
        MAX_TOOL_ROUNDS = 16
        for _round in range(MAX_TOOL_ROUNDS):
            # 发送前兜底清洗，杜绝 "tool must follow tool_calls" 报错
            messages = _sanitize_messages(messages)
            resp = client.chat.completions.create(
                model=MODEL,
                messages=messages,
                temperature=0.7,
                max_tokens=16384,
                timeout=API_TIMEOUT,
                tools=workspace.TOOL_SCHEMAS,
            )
            choice = resp.choices[0]
            msg = choice.message

            # 无工具调用 → 正常回复，结束循环
            if not getattr(msg, "tool_calls", None):
                if choice.finish_reason == "length":
                    print(paint("  ⚠️  回复被 max_tokens 截断，代码可能不完整！", BR, BOLD))
                reply = msg.content or ""
                print_ai(reply)
                log(f"[{_ts()}] AI：{strip_markup(reply)}")
                messages.append({"role": "assistant", "content": reply})

                saved_files = save_code_files(reply)
                if saved_files:
                    print(paint(f"  💾 已保存 {len(saved_files)} 个代码文件到 {CODE_DIR}/",
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
                print(paint(f"  {icon} 工作台 {name} → {first}", color))
                log(f"[{_ts()}] TOOL {name}({args}) -> {result}")
                messages.append({
                    "role": "tool",
                    "tool_call_id": tc.id,
                    "content": result,
                })
        else:
            print(paint("  ⚠️  工具调用轮次达到上限，已强制停止", BY, BOLD))

    except KeyboardInterrupt:
        print("\n" + rainbow("  ✨ 已退出，下次见 ✨  "))
        log(f"[{_ts()}] === 用户中断 ===")
        break
    except Exception as e:
        print(paint(f"  ❌ 出错了：{e}", BR, BOLD))