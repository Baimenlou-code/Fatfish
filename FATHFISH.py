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
import verify_tools

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

# ============ 展示层（颜色/渲染/情绪）→ 已剥离到 ui_core.py ============
# 46 个成员（颜色常量、paint/gradient/rainbow、{{}}标记渲染、情绪判定、
# print_banner、print_ai）原样搬移；自测：python ui_core.py
from ui_core import (   # noqa: F401
    RESET, K, R, G, Y, BL, M, CY, W, BK, BR, BG, BY, BB, BM, BC, BW,
    BOLD, DIM, ITAL, UND, RV, ST, BGR, BGG, BGY, BGB,
    fg256, bg256, rgb, paint, rainbow, gradient,
    MOOD_COLOR, MOOD_EMOJI, MOOD_KEYWORDS, MOOD_ORDER, detect_mood,
    TAG_COLOR, TAG_RE, render_markup, strip_markup, render_inline,
    print_banner, print_ai,
    Wait, WAIT_FRAMES,
)

# ============ 输出渲染 ============


def print_startup_status():
    """打印运行状态一览（启动时收敛为一行提示，完整内容用 /status 调出）。"""
    print(paint(f"  🧠 主模型 [main model]：{MODEL} @ {BASE_URL}", BC))
    print(paint(f"  🌐 联网模式 [net mode]：{net_tools.NET_MODE.upper()}"
                f" ｜ 检索模式 [tavily]：{net_tools.TAVILY_MODE.upper()}", BC))
    print(paint(f"  🛠️  工作台目录 [workspace]：{workspace.get_workspace()}", BC))
    print(paint(f"  🧿 双人核验 [dual-AI verify]：{verify_tools.mode_label()}", BC))
    print(paint(f"  📡 审查意见镜像 [mirror]：{'ON' if VERIFY_MIRROR else 'OFF'}"
                + (f" → {os.path.basename(VERIFY_MIRROR_PATH)}" if VERIFY_MIRROR else ""), BC))
    # 状态行按 auto_approve_scope 如实描述（避免与 _never_auto_approve 的实际行为不符）
    _aa_scope = {"none": "关闭放行", "writes": "仅内容写入", "all": "含删除/执行类"}.get(
        AUTO_APPROVE_SCOPE, AUTO_APPROVE_SCOPE)
    _aa_note = {"writes": "删除/执行/敏感文件永不自动放行",
                "none": "一律不自动放行（等于只保留逐批确认）",
                "all": "仅敏感文件永不自动放行——删除/执行类也会被放行（风险自负）"}.get(
        AUTO_APPROVE_SCOPE, "")
    print(paint(f"  🔐 一键放行 [auto-approve]：{'ON' if AUTO_APPROVE_ENABLED else 'OFF'}"
                f"（范围：{_aa_scope}；{_aa_note}）"
                f" ｜ 计 时 [timer]：{'ON' if SHOW_TIMER else 'OFF'}", BC))
    print(paint(f"  📝 报批范围 [approve scope]：{APPROVE_SCOPE.upper()}"
                f"（{'只读也报批' if APPROVE_SCOPE == 'all' else '只读免报批，敏感文件仍报批'}）"
                f" ｜ 只读 Python 免核验 [readonly-py]：{'ON' if VERIFY_READONLY_PYTHON else 'OFF'}",
                BC))
    print(paint(f"  📂 日志 [log]：{LOG_DIR}", BC))
    print(paint(f"  📂 代码 [code]：{CODE_DIR}", BC))



# ============ 配置 ============
# ---- .env 读取容错辅助 ----
# 背景：python-dotenv 对「空值 + 行尾注释」（形如 KEY=   # 注释）不会剥离注释，
#      会把注释文本原样当值返回；若把它当 API key，会构造出含中文的 HTTP 头，
#      触发 httpx 的 'ascii' codec 编码错误。这里统一做防御性清理。
def _env_clean(name, default=""):
    v = os.getenv(name, "")
    if v is None:
        return default
    v = v.strip()
    if v.startswith("#"):
        return default
    for sep in ("  #", "\t#", " #"):
        if sep in v:
            v = v.split(sep, 1)[0].strip()
    return v or default


def _looks_like_key(s):
    """API key 基本体检：非空、无空白、纯 ASCII（HTTP 头只能放 ASCII）。"""
    if not s:
        return False
    if any(c.isspace() for c in s):
        return False
    return all(ord(c) < 128 for c in s)


def _env_int(name, default):
    try:
        return int(_env_clean(name, str(default)) or default)
    except (TypeError, ValueError):
        return default


def _env_bool(name, default=False):
    v = _env_clean(name, "1" if default else "0").lower()
    return v in ("1", "true", "yes", "on")


def _env_float(name, default=0.0):
    try:
        return float(_env_clean(name, str(default)) or default)
    except (TypeError, ValueError):
        return default


# ---- 主模型（执行者）：全部从 .env 读取 ----
#  换成任何「OpenAI 兼容」服务，只改 .env 里这三项即可，无需动代码。
#  取值优先级：FATFISH_* → DEEPSEEK_*（旧写法，向后兼容）→ 内置默认。
API_KEY        = _env_clean("FATFISH_API_KEY") or _env_clean("DEEPSEEK_API_KEY")
BASE_URL       = (_env_clean("FATFISH_BASE_URL") or _env_clean("DEEPSEEK_BASE_URL")
                  or "https://api.deepseek.com")
MODEL          = (_env_clean("FATFISH_MODEL") or _env_clean("DEEPSEEK_MODEL")
                  or "deepseek-flash")
TAVILY_API_KEY = _env_clean("TAVILY_API_KEY")

# 出厂配置快照（供 /model 或诊断时对比当前值）
BOOT_MODEL     = MODEL
BOOT_BASE_URL  = BASE_URL

# ---- 上下文 / 交互 / 命令 限制（已整体放宽，可按需再调）----
MAX_HISTORY    = _env_int("MAX_HISTORY", 500)   # 保留的历史消息条数（原 20 → 200 → 1000 → 400 → 500，适配 1M 上下文；.env 的 MAX_HISTORY 可覆盖）
MAX_HISTORY_TOKENS = _env_int("MAX_HISTORY_TOKENS", 800000)   # 历史部分的 token 预算（估算值，原 60000 → 512000 → 2000000 → 800000；.env 的 MAX_HISTORY_TOKENS 可覆盖）
TRIM_KEEP_FIRST_USER = True   # 是否钉住最早一条 user（任务目标）
TRIM_TOOL_CLIP_CHARS = _env_int("TRIM_TOOL_CLIP_CHARS", 40000)  # 单条 tool 结果超过此长度则中间截断（原 4000 → 60000 → 200000 → 40000；.env 可覆盖）
MAX_REPLY_TOKENS = _env_int("MAX_REPLY_TOKENS", 131072)     # 单次回复的 max_tokens（原 16384 → 32768 → 65536 → 131072；.env 可覆盖）
MAX_TOOL_ROUNDS  = _env_int("MAX_TOOL_ROUNDS", 512)        # 单轮交互内工具调用轮数上限（原 16 → 128 → 512；.env 可覆盖）
LOG_ROOT       = "logs"
CODE_ROOT      = "generated_code"
API_TIMEOUT    = _env_int("API_TIMEOUT", 900)          # 单次 API 请求超时秒数（原 60 → 300 → 900，大上下文更慢；.env 可覆盖）

# ---- 双人核验（第二位 AI 审查员）----
#   0) VERIFIER_*   ：审查员的 key / 地址 / 模型（留空则沿用主模型配置）
#   1) VERIFY_MODE  ：off / auto（仅核验有副作用动作）/ all（连只读也核验）
#   2) VERIFY_STRICT：True=超重试上限即拦截；False=放行
#   3) VERIFY_MAX_RETRIES：同一轮内「打回重交」的次数上限（仅 revise 计入）
#   3b) VERIFY_MAX_SUPPLEMENTS：supplement（要求补充资料）的免费轮数上限，
#       不计入退回次数；用尽后才归入退回计数（防审查员无限索取）
#   4) VERIFY_FINAL_ANSWER：是否连最终答复也复核（默认关）
#   5) VERIFY_FAIL_MODE：核验服务不可用时 open=放行 / closed=拦截
_VERIFIER_KEY_RAW  = _env_clean("VERIFIER_API_KEY", "")
_VERIFIER_KEY_BAD  = bool(_VERIFIER_KEY_RAW) and not _looks_like_key(_VERIFIER_KEY_RAW)
VERIFIER_API_KEY    = API_KEY if _VERIFIER_KEY_BAD else (_VERIFIER_KEY_RAW or API_KEY)
VERIFIER_BASE_URL   = _env_clean("VERIFIER_BASE_URL", "") or BASE_URL
VERIFIER_MODEL      = _env_clean("VERIFIER_MODEL", "") or MODEL
VERIFY_MODE         = _env_clean("VERIFY_MODE", "auto").lower()
VERIFY_STRICT       = _env_clean("VERIFY_STRICT", "1").lower() not in ("0", "false", "no", "off", "")
VERIFY_MAX_RETRIES  = _env_int("VERIFY_MAX_RETRIES", 2)
# 需求：「要求补充资料」（supplement）不计入退回次数；这是免费补充轮数上限（防死循环）
VERIFY_MAX_SUPPLEMENTS = _env_int("VERIFY_MAX_SUPPLEMENTS", 4)
VERIFY_FINAL_ANSWER = _env_bool("VERIFY_FINAL_ANSWER", False)
VERIFY_FAIL_MODE    = _env_clean("VERIFY_FAIL_MODE", "open").lower()
# 是否把审查意见镜像到监控器窗口（watcher 会 tail logs/.../exec_*.out）
VERIFY_MIRROR       = _env_bool("VERIFY_MIRROR", True)
# ---- 送审「方案」长度上限（业务 AI → 复核 AI，单位：字符；中文约 1 字 ≈ 1.5 token）----
#   留空 / 非正整数则用 verify_tools 内置默认值。
VERIFIER_MAX_TOKENS      = _env_int("VERIFIER_MAX_TOKENS", 0)        # 审查意见输出上限
VERIFIER_TIMEOUT         = _env_int("VERIFIER_TIMEOUT", 0)           # 单次审查超时（秒）
VERIFIER_TEMPERATURE     = _env_float("VERIFIER_TEMPERATURE", 0.2)   # 审查员温度
VERIFIER_PROMPT_CHARS    = _env_int("VERIFIER_PROMPT_CHARS", 0)      # 送审总上限
VERIFIER_ACTION_PREVIEW  = _env_int("VERIFIER_ACTION_PREVIEW", 0)    # 单个动作预览
VERIFIER_REPLACE_PREVIEW = _env_int("VERIFIER_REPLACE_PREVIEW", 0)   # ws_replace old/new
VERIFIER_GOAL_CHARS      = _env_int("VERIFIER_GOAL_CHARS", 0)        # 用户原始需求
VERIFIER_PLAN_CHARS      = _env_int("VERIFIER_PLAN_CHARS", 0)        # 执行者自述/计划 ★
VERIFIER_CONTEXT_CHARS   = _env_int("VERIFIER_CONTEXT_CHARS", 0)     # 近期上下文
VERIFIER_ANSWER_CHARS    = _env_int("VERIFIER_ANSWER_CHARS", 0)      # 待复核的答复
VERIFIER_ANSWER_CTX      = _env_int("VERIFIER_ANSWER_CTX", 0)        # 答复复核的上下文


def _verify_limit_kwargs():
    """把「送审长度上限」的 env 值整理成 configure() 的参数（0 表示不覆盖）。"""
    pairs = {
        "max_tokens": VERIFIER_MAX_TOKENS,
        "timeout": VERIFIER_TIMEOUT,
        "temperature": VERIFIER_TEMPERATURE,
        "prompt_max_chars": VERIFIER_PROMPT_CHARS,
        "action_preview": VERIFIER_ACTION_PREVIEW,
        "replace_preview": VERIFIER_REPLACE_PREVIEW,
        "goal_max_chars": VERIFIER_GOAL_CHARS,
        "plan_max_chars": VERIFIER_PLAN_CHARS,
        "context_max_chars": VERIFIER_CONTEXT_CHARS,
        "answer_max_chars": VERIFIER_ANSWER_CHARS,
        "answer_ctx_chars": VERIFIER_ANSWER_CTX,
    }
    return {k: v for k, v in pairs.items() if v and v > 0}


def _reconfig_verifier():
    """把当前核验相关全局配置重新注入 verify_tools。

    ★ 单一入口：启动初始化 与 运行时重配置（/set、/verify、/reload）都走这里，
      彻底杜绝「两处参数列表各改各的」造成的配置漂移。
    """
    verify_tools.configure(
        api_key=VERIFIER_API_KEY,
        base_url=VERIFIER_BASE_URL,
        model=VERIFIER_MODEL,
        mode=VERIFY_MODE,
        strict=VERIFY_STRICT,
        max_retries=VERIFY_MAX_RETRIES,
        fail_mode=VERIFY_FAIL_MODE,
        log_dir=LOG_DIR,
        mirror_path=VERIFY_MIRROR_PATH,
        mirror_on=VERIFY_MIRROR,
        **_verify_limit_kwargs(),
    )


_verify_retry       = 0      # 运行时状态：本轮已退回次数（仅 revise 计）
_verify_suppl       = 0      # 运行时状态：本轮「要求补充资料」已用轮数（不计退回）

# ---- 计时器（本轮耗时 / 你停留了多久）----
#   本轮 = 你发话那一刻 → 本轮彻底跑完（含工具循环）
#   停留 = 上一轮跑完 → 你下一次发话
SHOW_TIMER      = _env_bool("SHOW_TIMER", True)       # 是否显示计时（/timer off 可关；.env 可覆盖）
# ---- 等待动画（- \ | / 轮转：让你看得见「在跑」而不是卡死）----
#   依赖：_env_bool / _env_float 定义于本文件上方（约行 138 / 143），早于此处求值。
SHOW_WAIT_ANIM     = _env_bool("SHOW_WAIT_ANIM", True)       # 总开关（/set show_wait_anim off）
WAIT_ANIM_INTERVAL = _env_float("WAIT_ANIM_INTERVAL", 0.08)  # 每帧间隔（秒）
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


# ---- 等待动画：开始 / 收尾（- \ | / 轮转；写 stderr，非 TTY 自动降级）----
#   为什么写 stderr：stdout 被 _TeeStream 包装，斜杠命令期间还会被捕获并注入给模型，
#   动画若写进 stdout 会在上下文里留下大量刷新帧。详见 ui_core.Wait 的说明。
def _wait_start(label):
    """开始等待动画；被关闭或出错时返回 None（调用方无需判断）。"""
    if not SHOW_WAIT_ANIM:
        return None
    try:
        return Wait(label, interval=WAIT_ANIM_INTERVAL).start()
    except Exception:
        return None


def _wait_stop(w, ok=True, label=None, note=""):
    """结束等待动画并落一行结果；w 为 None 时是无操作。"""
    if w is None:
        return
    try:
        w.stop(ok=ok, label=label, note=note)
    except Exception:
        pass


# ---- 公共基础件：时间戳 / 日期分层目录（来自 common.py，单一事实来源）----
# 这两项原先在本文件与另外 6 个模块里各写了一份逐字相同的实现；
# 现收敛到 common.py：改一处即可全局生效。
try:
    from common import dated_dir as _dated_dir, ts as _ts
except ImportError:               # common.py 缺失时退回本地实现，保持自足
    def _dated_dir(root: str) -> str:
        now = datetime.now()
        d = os.path.join(root, f"{now:%Y}", f"{now:%m}", f"{now:%d}")
        os.makedirs(d, exist_ok=True)
        return d

    def _ts():
        return datetime.now().strftime("%Y-%m-%d %H:%M:%S")

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

# 注：_ts() 由上方 common.py 统一引入（单一事实来源），此处不再重复定义

# ============ 初始化客户端 ============
client = OpenAI(api_key=API_KEY, base_url=BASE_URL)

# ============ 注入 Tavily key 到 net_tools ============
net_tools.set_api_key(TAVILY_API_KEY)

# ============ 联网模式（可从 .env 覆盖）============
#   on=总是联网 / off=从不 / auto=关键词规则 / ai=第二位 AI 核验判断
_net_mode_env = _env_clean("NET_MODE", "").lower()
if _net_mode_env in ("on", "off", "auto", "ai"):
    net_tools.NET_MODE = _net_mode_env
_tavily_mode_env = _env_clean("TAVILY_MODE", "").lower()
if _tavily_mode_env in ("search", "extract", "auto", "both"):
    net_tools.TAVILY_MODE = _tavily_mode_env
# extract 单段最大字符（.env 可覆盖；/set save 会写回 EXTRACT_MAX_LEN）
_extract_env = _env_int("EXTRACT_MAX_LEN", 0)
if _extract_env > 0:
    net_tools.EXTRACT_MAX_LEN = _extract_env

# ============ 初始化「双人核验」审查员 ============
#   审查员是独立的第二个模型实例：独立 system prompt（挑剔的复核人格），
#   默认沿用主模型的 key 但使用 VERIFIER_MODEL 指定的（建议不同的）模型。
#
# 镜像路径：fatfish_watcher.py 只 tail「logs/YYYY/MM/DD/exec_*.out」，
# 因此把审查过程写到同目录的 exec_verify_<pid>.out —— 命名天然落在
# watcher 的扫描范围内，监控器窗口就会实时滚出审查意见，无需改 watcher。
VERIFY_MIRROR_PATH = (os.path.join(LOG_DIR, f"exec_verify_{os.getpid()}.out")
                      if VERIFY_MIRROR else "")

# 启动初始化：与运行时重配置共用同一入口（见上方 _reconfig_verifier）
_reconfig_verifier()

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

# ============ 敏感工具报批（2026-09-19 修订：按风险分级）============
# 修订动机：原名单按「文件类 / 执行类」粗分，导致两个错配 ——
#   ① ws_read（纯只读、已被沙箱限制在工作台内）每次都要人工点头，高频摩擦；
#   ② 而 ws_mkdir / ws_cd（真有副作用）反倒只靠 AI 核验。
# 现按「风险等级」统一分级（单一事实来源，避免核验名单与报批名单各改各的）：
#
#   L0 只读     ws_where / ws_list / ws_read / ws_search / ws_forget
#               → 免人工报批。唯一例外：ws_read 命中「敏感文件名单」
#                 （.env / 密钥 / 凭据，见 workspace.is_sensitive_path）→ 强制报批。
#   L1 低副可逆 ws_mkdir / ws_cd / ws_cd_approve → 仅 AI 核验（沙箱边界另有兜底）
#   L2 内容写入 ws_write / ws_append / ws_replace / ws_delete → 人工 + 核验（不变）
#   L3 执行     ws_run_cmd / ws_run_python → 人工 + 核验（不变）
#
# 依赖说明（均先于本区块定义，见同文件更早处）：
#   _env_clean / _env_bool 定义于上方「配置 → .env 读取容错辅助」段；
#   workspace 模块于文件头部导入；is_sensitive_path / sensitive_reason
#   由 workspace.py 提供（本会话同一批补丁写入，已用 20 条用例实测通过）。
#
# 想回到「连只读也要报批」的旧行为：`/set approve_scope all`（已登记为设置项，立即生效）。
APPROVE_RUN_TOOLS = _env_bool("APPROVE_RUN_TOOLS", True)
APPROVE_SCOPE = (_env_clean("APPROVE_SCOPE", "writes") or "writes").lower()
if APPROVE_SCOPE not in ("all", "writes"):
    APPROVE_SCOPE = "writes"

# 是否让「静态判定为只读」的 ws_run_python 免去 AI 核验（省 token；人工报批不变）
VERIFY_READONLY_PYTHON = _env_bool("VERIFY_READONLY_PYTHON", True)

READ_ONLY_TOOLS = {"ws_where", "ws_list", "ws_read", "ws_search", "ws_forget"}

# 基础报批名单：内容写入类（创建 / 覆盖 / 追加 / 替换 / 删除）。
# 注：ws_read 不在其中 —— 它是只读的，是否报批由 _approval_needed() 动态判定。
APPROVAL_REQUIRED = {"ws_write", "ws_append", "ws_replace", "ws_delete"}
if APPROVE_RUN_TOOLS:
    APPROVAL_REQUIRED |= {"ws_run_cmd", "ws_run_python"}

# 报批时高亮显示的高危工具（执行命令 / 运行代码）
HIGH_RISK_TOOLS = {"ws_run_cmd", "ws_run_python"}


def _approval_needed(name, args=None):
    """判断一次工具调用是否需要人工报批。返回 (need: bool, why: str)。

    这是报批决策的**唯一入口**（启动名单 + 动态例外都收敛在这里），
    避免「多处各写一份判定」造成的规则漂移。
    """
    args = args or {}
    if name in APPROVAL_REQUIRED:
        return True, ("high-risk exec" if name in HIGH_RISK_TOOLS else "")
    if name == "ws_read":
        if APPROVE_SCOPE == "all":
            return True, "approve_scope=all（只读也报批）"
        rel = args.get("path", "")
        if workspace.is_sensitive_path(rel):
            return True, workspace.sensitive_reason(rel)
    return False, ""


# ---- 一键放行的「永不免检」清单（2026-09-19 新增）----
# 背景：一键放行（/auto、报批时按 a/1、fast 方案）会跳过本轮的逐批人工确认。
#       若它覆盖一切，则「双人核验」的审查员 AI 就成了事实上**唯一的、也是最终的**
#       裁定者；而审查员人格是「默认放行、只拦真问题」，两者叠加会把安全裕度压得很低。
# 立场：一键放行的本意是「别为一批小改动反复打扰我」，
#       而不是「把删除 / 执行 / 读密钥也交给 AI 自行决定」。
#       因此按风险分级——高危动作的人工闸门永不跳过。
#
#   none   —— 最严：一键放行对内容写入也不生效（等于只保留逐批确认）
#   writes —— 保守：写 / 追加 / 替换可放行（沙箱 + 根一级自动备份兜底）；
#             删除、执行命令、执行代码、敏感文件**必须由用户亲手确认**
#   all    —— 默认：除敏感文件（.env / 密钥 / 凭据）外全部可放行，即按一次
#             a/1 之后，删除 / 执行类也不再逐批人工确认（信任场景，风险自负）
#             ★ 默认档位下 NEVER_AUTO_APPROVE 实际失效，人工闸门仅在敏感文件上保留；
#               审查员会通过送审备注得知「本批无人类兜底」（见 extra_note）。
#
# 依赖（位置均为本仓库实测）：
#   _env_clean 定义于本文件第 114 行；
#   workspace.is_sensitive_path 见 workspace.py 第 243 行。
# 前置条件（均以实测验证）：
#   ① _request_approval 内部**不存在**基于 _AUTO_APPROVE_TURN 的短路——把动作
#      送进去必然弹出人工确认（假 input 实跑 5 例：删除/执行/读 .env/回 y/对照）。
#   ② settings.register 支持 choices / env_name / apply（inspect 签名已核对）。
#   ③ 本函数定义于第 ~1496 行，而其 register 调用在第 ~1613 行（晚于
#      auto_approve_enabled 的第 1608 行）——先定义后注册，无顶层 NameError。
#   ④ globals()["AUTO_APPROVE_SCOPE"] 写入可被本函数在运行时读到（已实测）。
AUTO_APPROVE_SCOPE = (_env_clean("AUTO_APPROVE_SCOPE", "all") or "all").lower()
if AUTO_APPROVE_SCOPE not in ("none", "writes", "all"):
    AUTO_APPROVE_SCOPE = "all"

# 无论哪个档位都不允许自动放行的动作（删除 + 执行类）
NEVER_AUTO_APPROVE = {"ws_delete", "ws_run_cmd", "ws_run_python"}


def _never_auto_approve(name, args=None):
    """该动作是否禁止「一键放行」——必须由用户亲手确认。

    ★ 敏感文件（.env / 密钥 / 凭据）无论**读还是写**、无论哪个档位都不豁免：
      只查 ws_read 是不够的 —— 向 .env 写 / 追加 / 替换同样属于「碰密钥」。
    """
    args = args or {}
    _p = str(args.get("path", "") or "")
    if _p and workspace.is_sensitive_path(_p):
        return True
    if AUTO_APPROVE_SCOPE == "none":
        return True
    if AUTO_APPROVE_SCOPE == "all":
        return False
    return name in NEVER_AUTO_APPROVE


# ---- 只读 Python 静态判定（豁免的是「AI 核验」，不是「人工报批」）----
# 实测：一次 ws_run_python 的 AI 核验要额外花一次审查员调用（约 3~25k token），
#       而大量调用其实是只读探针（读文件 / 统计 / 打印），风险极低。
# 做法：用 AST 静态分析判定「静态上只读」，命中则跳过 AI 核验以省 token；
#       ★ 人工报批照旧保留 ★ —— 少掉的只是第二意见，人类闸门一道不少。
#
# 安全取向：**默认拒绝**。以下任一情况直接送审，不做豁免：
#   · 导入不在白名单内（含 `from <能力根> import …`，避免绕过能力根追踪）；
#   · 能力根（os / pathlib / Path / io / shutil / subprocess / socket /
#     requests / urllib / http / ftplib / smtplib / telnetlib / pty）上
#     调用了白名单之外的属性 —— 于是 os.execv / os.spawnv / os.fork 这类
#     变体自动被覆盖，无需逐个补名字；
#   · 访问 environ / getenv / putenv / setenv（环境变量里可能有 .env 的密钥）；
#   · 字符串字面量里出现 .env / id_rsa / .pem / api_key / secret / password
#     / credential / sk- 等敏感痕迹（防止 open(".env") 这类直读）；
#   · open() 的模式参数不是「字面量只读」；
#   · 动态取值（getattr/exec/eval/__import__…）、属性或下标赋值、global 声明；
#   · 解析失败、代码超长、空代码。
#   因此这里的 True 只代表「静态上没看到危险面」，不等于安全证明。
_READONLY_SAFE_MODULES = {
    "os", "sys", "re", "json", "math", "time", "datetime", "collections",
    "itertools", "functools", "hashlib", "ast", "stat", "glob", "fnmatch",
    "difflib", "textwrap", "unicodedata", "decimal", "fractions", "random",
    "csv", "string", "base64", "binascii", "struct", "zlib", "gzip", "io",
    "pathlib", "pprint", "copy", "operator", "typing", "enum", "dataclasses",
    "uuid", "platform", "locale",
}

# 能力根 → 只读属性白名单（default-deny：不在表内即拦）。
# 注意：environ / getenv / putenv / setenv 已被有意排除（环境变量可能含密钥）。
# 空集合 = 该能力根不给任何许可（出现即送审）。
_READONLY_ROOT_ALLOW = {
    "os": {
        "listdir", "walk", "scandir", "stat", "lstat", "readlink", "getcwd",
        "fspath", "sep", "linesep", "pathsep", "name", "curdir", "pardir",
        "altsep", "extsep", "devnull", "getpid", "getppid", "cpu_count",
        "uname", "strerror", "access",
        # os.path.*（对 os.path 的调用，最左根名同为 "os"）
        "join", "exists", "isfile", "isdir", "islink", "getmtime", "getctime",
        "getsize", "basename", "dirname", "abspath", "relpath", "splitext",
        "split", "normpath", "normcase", "expanduser", "expandvars",
        "commonprefix", "splitdrive", "realpath", "samefile", "isabs",
    },
    "pathlib": {"Path", "PurePath", "PosixPath", "WindowsPath",
                "PurePosixPath", "PureWindowsPath"},
    "Path": {"exists", "is_file", "is_dir", "is_symlink", "glob", "rglob",
             "iterdir", "stat", "lstat", "read_text", "read_bytes", "name",
             "stem", "suffix", "suffixes", "parent", "parents", "parts",
             "as_posix", "as_uri", "with_suffix", "with_name", "joinpath",
             "resolve", "absolute", "samefile", "owner", "group", "cwd",
             "home", "expanduser", "match", "relative_to", "is_absolute",
             "anchor", "drive", "root", "is_relative_to", "is_reserved"},
    "io": {"StringIO", "BytesIO", "text_encoding", "DEFAULT_BUFFER_SIZE",
           "SEEK_SET", "SEEK_CUR", "SEEK_END", "UnsupportedOperation"},
    # 以下能力根一律零许可：出现任何属性调用都送审
    "shutil": set(), "subprocess": set(), "socket": set(), "requests": set(),
    "urllib": set(), "http": set(), "ftplib": set(), "smtplib": set(),
    "telnetlib": set(), "pty": set(),
}

# 无条件拦截的属性名（不论根是谁、读写与否）
_READONLY_ALWAYS_BLOCK_ATTRS = {
    "environ", "getenv", "putenv", "setenv",
    "execv", "execve", "execvp", "execl", "execlp", "spawnv", "spawnl",
    "spawnve", "fork", "forkpty", "system", "popen",
}

# 未知根对象（局部变量、第三方对象…）的兜底：命中「变异动词」即送审。
# 这里**只剔除 4 个「容器/字符串同名」的方法名**：
#     replace（str.replace 高频） / remove（list.remove 高频）
#     copy（dict.copy 高频）     / move（无同名高频，但危险实现仍在 shutil）
# 它们的**危险实现全部挂在已知能力根上**，已由上面的能力根允许表拦住：
#     os.replace / os.remove  → 根名 "os"，不在 os 允许表 ⇒ 拦
#     shutil.copy / shutil.move / Path.replace → 对应根允许表里没有 ⇒ 拦
# 其余变异动词（rename/rmdir/mkdir/makedirs/chmod/kill/send/Popen/run/call/
# chdir/unlink/rmtree/truncate/write* …）**一个都不删**，即便某个别名没被
# 追踪到，也仍会在这里被拦下 —— 兜底表因此是"宽度不变、只去掉误报源"。
_READONLY_MUTATING_ATTRS = {
    "write", "writelines", "write_text", "write_bytes", "unlink",
    "rmdir", "removedirs", "rename", "truncate", "mkdir",
    "makedirs", "rmtree", "chmod", "chown", "kill",
    "copy2", "copyfile", "copytree", "save", "dump", "to_csv", "to_excel",
    "to_pickle", "urlopen", "connect", "send", "sendall", "Popen", "run",
    "call", "check_output", "check_call", "chdir", "abort",
}

# 文件对象（含 open() 及其局部别名）允许的方法：只读语义才放行
_READONLY_FILE_ALLOW = {
    "read", "readline", "readlines", "close", "seek", "tell", "readable",
    "writable", "fileno", "closed", "name", "mode", "flush", "encoding",
    "errors", "newlines", "isatty",
}

# 字面量敏感痕迹（防止 open(".env") / 把路径与密钥名写死）
_READONLY_SECRET_HINTS = (
    ".env", "id_rsa", ".pem", "private_key", "credential", "api_key",
    "apikey", "secret", "password", "passwd", "sk-",
)

_READONLY_MAX_CHARS = 40000      # 超过此长度直接照常送审（避免大脚本的解析盲区）


def _root_name(node):
    """取调用链最左侧的名字：a.b.c() → 'a'；Path(x).write_text() → 'Path'。"""
    import ast as _ast
    while isinstance(node, _ast.Attribute):
        node = node.value
    if isinstance(node, _ast.Name):
        return node.id
    if isinstance(node, _ast.Call):
        return _root_name(node.func)
    return ""


def _expr_root_kind(expr, aliases):
    """推断一个表达式的「能力根种类」：os / pathlib / Path / __file__ / None。

    用于把 `p = pathlib.Path(x)`、`d = os.path`、`fh = open(p)` 这类
    **局部别名**识别成对应能力根，再按该根的允许表严格判定 —— 这是纯增益：
    多认出一个别名，只会让判定更严，不会更松。
    """
    import ast as _ast
    if expr is None:
        return None
    if isinstance(expr, _ast.Call):
        if isinstance(expr.func, _ast.Name) and expr.func.id == "open":
            return "__file__"
        return _expr_root_kind(expr.func, aliases)
    if isinstance(expr, _ast.Attribute):
        base = _expr_root_kind(expr.value, aliases)
        if base == "pathlib" and expr.attr in ("Path", "PurePath"):
            return "Path"
        if base == "os" and expr.attr == "path":
            return "os"
        return base
    if isinstance(expr, _ast.Name):
        if expr.id in _READONLY_ROOT_ALLOW:
            return expr.id
        if expr.id == "open":
            return "__file__"
        return aliases.get(expr.id)
    return None


def _collect_aliases(tree):
    """收集「本地名 → 能力根种类」映射：import as / 赋值 / for / with。

    只做保守传播：拿不准就不建映射（该名随后按未知对象兜底处理，仍然安全）。
    """
    import ast as _ast
    aliases = {}
    for node in _ast.walk(tree):
        if isinstance(node, _ast.Import):
            for a in node.names:
                if a.asname:
                    aliases[a.asname] = a.name.split(".")[0]
        elif isinstance(node, _ast.Assign):
            kind = _expr_root_kind(node.value, aliases)
            if kind:
                for t in node.targets:
                    names = ([t] if isinstance(t, _ast.Name)
                             else [e for e in getattr(t, "elts", [])
                                   if isinstance(e, _ast.Name)])
                    for nm in names:
                        aliases[nm.id] = kind
        elif isinstance(node, _ast.AnnAssign):
            if isinstance(node.target, _ast.Name):
                kind = _expr_root_kind(node.value, aliases)
                if kind:
                    aliases[node.target.id] = kind
        elif isinstance(node, _ast.For):
            # 只有 Path 这类「迭代产出同类对象」的根才向下传播，避免误伤
            kind = _expr_root_kind(node.iter, aliases)
            if kind == "Path" and isinstance(node.target, _ast.Name):
                aliases[node.target.id] = "Path"
        elif isinstance(node, _ast.With):
            for item in node.items:
                if isinstance(item.optional_vars, _ast.Name):
                    kind = _expr_root_kind(item.context_expr, aliases)
                    if kind:
                        aliases[item.optional_vars.id] = kind
    return aliases


def _python_is_readonly(code):
    """AST 静态判定：这段代码在静态上是否「只读且无外联」。返回 (bool, 原因)。

    ⚠️ 作用域声明（避免误读）：本判定**只决定「要不要再请第二位 AI 复核」**，
       与「要不要人工报批」无关 —— ws_run_python 是否需要人工批准，由
       _approval_needed() 依 APPROVAL_REQUIRED 决定，与本函数无关。
       也就是说：判为只读只省一次审查员调用（token），人工闸门一道不少。

    只在能明确判定安全时返回 True；一切不确定都返回 False（由调用方照常送审）。
    绝不执行代码，只解析语法树，零副作用。
    """
    import ast as _ast
    if not code or not code.strip():
        return False, "空代码"
    if len(code) > _READONLY_MAX_CHARS:
        return False, "代码过长，未做静态判定"
    try:
        tree = _ast.parse(code)
    except SyntaxError as e:
        return False, f"语法解析失败：{e}"
    except Exception:
        return False, "AST 解析异常"

    aliases = _collect_aliases(tree)     # 先把局部别名还原成能力根
    bad = []
    for node in _ast.walk(tree):
        # ---- 导入 ----
        if isinstance(node, _ast.Import):
            for a in node.names:
                if a.name.split(".")[0] not in _READONLY_SAFE_MODULES:
                    bad.append("导入 " + a.name)
        elif isinstance(node, _ast.ImportFrom):
            mod = node.module or ""
            top = mod.split(".")[0]
            if top in _READONLY_ROOT_ALLOW:
                # from os import ... / from pathlib import ... → 能力根无法追踪，送审
                bad.append("from %s import …（能力根无法追踪）" % (mod or "?"))
            elif top not in _READONLY_SAFE_MODULES:
                bad.append("导入 " + (mod or "?"))
        # ---- 字符串字面量敏感痕迹 ----
        elif isinstance(node, _ast.Constant) and isinstance(node.value, str):
            low = node.value.lower()
            for h in _READONLY_SECRET_HINTS:
                if h in low:
                    bad.append("字面量含敏感痕迹 %r" % h)
                    break
        # ---- 调用 ----
        elif isinstance(node, _ast.Call):
            f = node.func
            if isinstance(f, _ast.Name):
                if f.id in ("exec", "eval", "compile", "__import__", "input",
                            "breakpoint", "getattr", "setattr", "delattr",
                            "globals", "locals", "vars"):
                    bad.append("调用 " + f.id)
                elif f.id == "open":
                    # ★ 默认拒绝：只有「字面量且为只读模式」才放行。
                    modes = [k.value for k in node.keywords if k.arg == "mode"]
                    if len(node.args) >= 2:
                        modes.append(node.args[1])
                    for mv in modes:
                        ok_mode = (isinstance(mv, _ast.Constant)
                                   and isinstance(mv.value, str)
                                   and not any(c in mv.value for c in "wax+"))
                        if not ok_mode:
                            bad.append("open(模式参数非字面量只读)")
                            break
            elif isinstance(f, _ast.Attribute):
                root = _root_name(f)
                root = aliases.get(root, root)      # 局部别名 → 还原能力根
                if f.attr in _READONLY_ALWAYS_BLOCK_ATTRS:
                    bad.append("调用 .%s()（环境变量/进程能力）" % f.attr)
                elif root in _READONLY_ROOT_ALLOW:
                    if f.attr not in _READONLY_ROOT_ALLOW[root]:
                        bad.append("调用 %s.%s()" % (root, f.attr))
                elif root == "__file__":
                    if f.attr not in _READONLY_FILE_ALLOW:
                        bad.append("文件对象调用 .%s()" % f.attr)
                elif f.attr in _READONLY_MUTATING_ATTRS:
                    bad.append("调用变异方法 .%s()" % f.attr)
            elif isinstance(f, _ast.Subscript):
                bad.append("下标调用（无法静态判定）")
        # ---- 属性访问 / 赋值 ----
        elif isinstance(node, _ast.Attribute):
            if node.attr in _READONLY_ALWAYS_BLOCK_ATTRS:
                bad.append("访问 .%s（环境变量/进程能力）" % node.attr)
            elif isinstance(node.ctx, _ast.Store):
                bad.append("对属性赋值 .%s" % node.attr)
        # ---- 下标赋值 / 作用域声明 ----
        elif isinstance(node, _ast.Subscript) and isinstance(node.ctx, _ast.Store):
            bad.append("对下标赋值（可能改环境变量/全局表）")
        elif isinstance(node, (_ast.Global, _ast.Nonlocal)):
            bad.append("global/nonlocal 声明")

    if bad:
        uniq = []
        for b in bad:
            if b not in uniq:
                uniq.append(b)
        return False, "、".join(uniq[:4])
    return True, ""

# ---- 一键放行（本轮内免再问）----
# AUTO_APPROVE_ENABLED = True：
#     启用"一键放行"。报批时按 a / 1，本批照批，同时本轮【剩余的全部读写操作】
#     都不再询问。作用域 = 当前这一轮：你在提示符发下一条命令时立即复位。
# AUTO_APPROVE_DEFAULT = True：
#     每轮一开始就处于放行态（等于完全不弹窗）。想彻底安静就把它改成 True。
# 安全边界：放行只跳过"问你一句"，其余防线一律照旧 ——
#     路径沙箱、读过凭证、根一级文件自动备份、逐条日志审计；
#     ws_cd 的"移出作业区需批准"是独立机制，不受本开关影响。
AUTO_APPROVE_ENABLED = _env_bool("AUTO_APPROVE_ENABLED", True)
AUTO_APPROVE_DEFAULT = _env_bool("AUTO_APPROVE_DEFAULT", False)
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
        _rp = args.get("path", "")
        _tag = ("  ⚠️ 敏感文件（强制人工）[sensitive, forced]"
                if workspace.is_sensitive_path(_rp) else "")
        return f"读取文件 [read]  {_rp}{_tag}"
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
    if name == "ws_run_cmd":
        return (f"⚡ 执行命令 [run cmd]  $ {_prev(args.get('command'), 160)}"
                f"   （cwd={args.get('cwd') or '.'}, timeout={args.get('timeout', 120)}s）")
    if name == "ws_run_python":
        code = args.get("code", "") or ""
        return (f"⚡ 运行 Python [run python]  「{_prev(code, 120)}」"
                f"   （{len(code)} 字符, cwd={args.get('cwd') or '.'}, "
                f"timeout={args.get('timeout', 120)}s）")
    return f"{name}  {args}"


def _request_approval(pending):
    """对一批待执行的工具调用做一次批量报批。

    pending: [(name, args), ...]  其中只包含需要报批的调用（文件类 + 执行类）。
    返回 (approved: bool, reply: str)。
    用户同意 → approved=True；拒绝 → approved=False，reply 为回填给模型的说明。

    按键：
      y / yes / 是 / 批准 / 同意 → 只批准本批
      a / 1 / all               → 批准本批，并放行本轮剩余全部敏感操作
      n / 其他 / EOF            → 拒绝
    """
    global _AUTO_APPROVE_TURN
    print()
    has_high = any(nm in HIGH_RISK_TOOLS for (nm, _a) in pending)
    print(paint("  🔐 即将执行以下敏感操作，需你批准 "
                "[the following sensitive ops need your approval]：", BY, BOLD))
    for i, (name, args) in enumerate(pending, 1):
        color = BR if name in HIGH_RISK_TOOLS else BC
        print(paint(f"     {i}. {_describe_tool_call(name, args)}", color))
    if has_high:
        print(paint("  ⚠️ 本批包含「执行命令 / 运行代码」（上方高亮项），"
                    "批准前请确认其后果与影响范围", BR, BOLD))
    if AUTO_APPROVE_ENABLED:
        # 依 auto_approve_scope 如实显示"一键放行能覆盖到哪"
        if AUTO_APPROVE_SCOPE == "all":
            _sc_hint = ("a 或 1 = 批准本批，并放行本轮剩余全部"
                        "（含删除 / 执行，仅敏感文件除外）[approve all]｜")
        elif AUTO_APPROVE_SCOPE == "none":
            _sc_hint = "（一键放行已关闭：/set auto_approve_scope writes 可开启）｜"
        else:
            _sc_hint = ("a 或 1 = 批准本批，并放行本轮剩余的内容写入"
                        "（删除 / 执行 / 敏感文件仍需点头）[approve writes this round]｜")
        print(paint("     y = 只批这一批 [this batch]｜" + _sc_hint + "n = 拒绝 [reject]", BK))
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
    _record_user_event("用户批准答复",
                       f"{ans or '(空)'} → {'批准' if approved else '拒绝'}"
                       f"（涉及 {len(pending)} 个操作）")

    if approved:
        if approve_all:
            _AUTO_APPROVE_TURN = True
            print(paint("  🔓 已批准，并且本轮剩余敏感操作全部自动放行"
                        "（直到你下一条命令）[approved; remaining sensitive ops this round are auto-approved]", BG, BOLD))
            log(f"[{_ts()}] 用户一键放行：本批 {len(pending)} 个 + 本轮后续全部")
        else:
            print(paint("  ✅ 已批准，执行中 [approved, running]…", BG, BOLD))
            log(f"[{_ts()}] 用户批准了 {len(pending)} 个敏感操作")
        return True, ""
    print(paint("  🚫 已拒绝，本次操作全部取消 [rejected, all ops cancelled]", BR, BOLD))
    log(f"[{_ts()}] 用户拒绝了 {len(pending)} 个敏感操作")
    return False, (
        "用户拒绝执行本次操作（未批准）。"
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

# ============ 双人核验辅助 ============
def _last_user_text(msgs):
    """取最近一条 user 消息的纯文本，供审查员理解「用户到底要什么」。"""
    for m in reversed(msgs):
        if m.get("role") == "user":
            c = m.get("content")
            if isinstance(c, str):
                return c
            if isinstance(c, list):
                buf = []
                for blk in c:
                    if isinstance(blk, dict):
                        buf.append(str(blk.get("text", "")))
                return "".join(buf)
    return ""


def _recent_context(msgs, limit=24000, max_msgs=40):
    """取最近若干条消息的文本摘要（截尾保近），供审查员参考。

    上限已放宽（原 limit=4000 / max_msgs=12），与 verify_tools.CONTEXT_MAX_CHARS 匹配，
    避免「业务 AI 想传更多、却被这里提前截掉」。
    """
    chunks = []
    for m in msgs[-max_msgs:]:
        if m.get("role") == "system":
            continue
        t = _msg_text(m)
        if t.strip():
            chunks.append(f"[{m.get('role')}] {t}")
    return "\n".join(chunks)[-limit:]


# ============ 用户即时命令记录（供审查员判断「用户是否已亲自授权」）============
# 用户本人敲下的命令 / 报批答复，由主程序在 input 通道直接采集；
# 模型没有写入通道，故审查员可据此确信「这是用户本人的意图」。
_USER_EVENTS = []          # [(kind, text), ...]
_USER_EVENTS_MAX = 12


def _record_user_event(kind, text):
    t = (text or "").strip()
    if not t:
        return
    _USER_EVENTS.append((kind, t))
    del _USER_EVENTS[:-_USER_EVENTS_MAX]


def _recent_user_events(limit=2000):
    """把用户本人最新敲下的命令 / 答复拼成一段文本，送审时交给审查员。

    只包含「用户亲手输入」的内容（主提示符命令 + 报批按键答复），
    不含模型生成内容，故不可被模型伪造。
    """
    if not _USER_EVENTS:
        return ""
    lines = [f"[{kind}] {text}" for kind, text in _USER_EVENTS]
    return "\n".join(lines)[-limit:]


def _print_search_judge_line(jv):
    """把「联网需求核验」的结果渲染成一行彩色输出。"""
    if not jv or jv.get("skipped"):
        return
    if jv.get("failed"):
        print(paint("  🌐 联网需求核验 → ⚠️ 核验异常，改用关键词规则", BY, BOLD))
        return
    if jv.get("need_search"):
        color, icon, tag = BG, "🔍", "需要联网"
    else:
        color, icon, tag = BK, "⛔", "无需联网"
    print(paint(f"  {icon} 联网需求核验 → {tag}"
                f"（{jv.get('confidence', '?')}）：{jv.get('reason', '')}", color, BOLD))
    if jv.get("query"):
        print(paint(f"       · 改写检索词：{jv['query']}", color, DIM))


def _print_verify_line(res, tag="双人核验"):
    """把核验结果渲染成一行彩色输出（含要求 / 备选）。"""
    if not res or res.get("skipped"):
        return
    v = res.get("verdict", "?")
    color = {"approve": BG, "supplement": BY, "revise": BR}.get(v, BC)
    icon = verify_tools.VERDICT_ICON.get(v, "❔")
    extra = ""
    if res.get("failed"):
        extra = "（核验服务异常，按兜底策略处理）"
    elif res.get("degraded"):
        extra = "（输出解析降级）"
    print(paint(f"  {icon} {tag} → {v}{extra}：{res.get('reason', '')}", color, BOLD))
    for d in (res.get("demands") or [])[:5]:
        print(paint(f"       · 要求：{d}", color, DIM))
    for d in (res.get("alternatives") or [])[:3]:
        print(paint(f"       · 备选：{d}", color, DIM))


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
    elif net_tools.NET_MODE == "ai":
        icon, color = "🌐🤖", BC
    else:
        icon, color = "🌐~", BC

    tv = {"search": "🔍", "extract": "📄", "auto": "🎯", "both": "🧩"}.get(
        net_tools.TAVILY_MODE, "🔍"
    )
    print(paint(f"{icon}{tv} 你 [You] ▸ ", color, BOLD), end="", flush=True)
    return ""

# ============ 主循环 ============
SYSTEM_PROMPT = (
    "你是一条乐于助人的蓝色大肥鱼，运行在用户的命令行里。"
    "你能读写文件、执行命令与代码；程序会在需要时自动联网，"
    "并把结果作为【联网结果】附在用户消息里。"
    "有【联网结果】时优先采信其中的时效信息；没有时不要声称自己刚查过网。"
    "用户可能通过 [@路径] 或 /read 把文件内容送进来，"
    "请直接基于这些内容回答，不要假装自己无法读取文件。"
    "写代码时请用 ```语言 的格式包裹代码块。\n\n"

    "【工作台与工具】你只能在一个工作台（workspace）沙盒内操作，所有路径都被限制在其中。\n"
    "  查：ws_where 当前根目录｜ws_cd 切换根目录｜ws_cd_approve 确认切换｜"
    "ws_list 列目录｜ws_search 全文搜索｜ws_read 读文件\n"
    "  改：ws_write 写/覆盖｜ws_append 追加｜ws_replace 精确替换｜"
    "ws_delete 删除｜ws_mkdir 建目录\n"
    "  跑：ws_run_cmd 执行命令｜ws_run_python 运行 Python\n"
    "  凭证：ws_append / ws_replace / ws_delete 之前必须先 ws_read 拿到「读过凭证」；"
    "文件被外部改动后凭证失效，需重新读。ws_write 是全量覆盖，无需先读。"
    "根一级文件被改动前，程序会自动备份到 _backup/。\n"
    "  切换：把根目录移出默认作业区会被拦下（返回待批准提示），"
    "经用户同意后再调 ws_cd_approve 放行。\n\n"

    "【两道闸门】写 / 删 / 执行类动作在落地前要过两道关：\n"
    "  1) 人工报批：ws_write / ws_append / ws_replace / ws_delete 与 "
    "ws_run_cmd / ws_run_python 会汇总成一批请用户点头；"
    "只读类（ws_where / ws_list / ws_search / ws_read / ws_forget）与 ws_mkdir / ws_cd "
    "默认免报批。敏感文件（.env、密钥、凭据等）即使只是读取也会强制报批——不要主动去读它们。\n"
    "  2) 双人核验：上述高风险动作还会先送给一位独立的「审查员 AI」复核，"
    "裁决为 approve 放行 / supplement 要求补充说明 / revise 要求变更方案；"
    "后两者动作不会执行，你会收到审查意见（见回填的工具结果）。"
    "此时必须补充说明（目的、影响范围、依据、回滚方式）或改用更稳妥的方案后重新提交，"
    "不要原样重试被驳回的动作。\n"
    "  ★ 审查员看得到你本轮的正文（也就是你的「计划」）。"
    "发起工具调用时用一两句话说清目的、影响范围与回滚方式，"
    "能显著减少被打回，更省时间与 token。\n"
    "  在默认 auto 模式下，静态判定为只读的 Python 探针不再送审；"
    "但不要刻意绕过闸门——拿不准就把意图写清楚，正常提交即可。\n\n"

    "【效率】一次能做完的事不要拖成多次：先用 ws_list / ws_search 定位，再精准 ws_read；"
    "彼此独立的读取可以在同一轮里一起提交；不要反复读同一个文件。"
    "被用户拒绝或被审查员打回时，不要重试同样的动作，改为说明意图并等待进一步指示。"
    "当用户要求你创建、修改、保存文件时，优先调用工具落盘，不要只在回复里贴代码。\n\n"

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
    "把上下文吃满、把产出做足，让每一轮交互都物尽其用。\n\n"

    "【模型名备忘】判断某个 DeepSeek 模型名是否存在时，一律以 /models 接口或最新官方文档为准"
    "（近期 /models 返回含 deepseek-flash、deepseek-v4-pro、deepseek-v4-flash、deepseek-v3.2、"
    "deepseek-v3.1、deepseek-r1，另有经典名 deepseek-chat / deepseek-coder），"
    "不要凭印象断言它不存在；本程序主模型与审查员默认取 deepseek-flash。"
)

HELP_TEXT = """
  📖 命令速查 [Command Reference]
  ─────────────────────────────────────────
  @路径               读取文件或目录（一层） [read file/dir, one level]
  /read 路径...       读取多个路径（一层） [read multiple paths, one level]
  /file 路径...       同 /read [same as /read]
  /open 路径...       同 /read [same as /read]
  /readr 路径...      递归读取整个目录树 [recursively read whole tree]
  /net on|off|auto|ai  切换联网模式（默认 auto；ai=AI核验判断）[net mode, ai=AI-judged]
  /net                查看当前联网模式 [show current net mode]
  /tavily auto|search|extract|both  切换检索模式（search/extract 为强制锁定）[tavily mode]
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
  /status             查看运行状态一览（模型/联网/工作台/核验…）[runtime status]
  /set                查看/修改参数（默认值可 reset）[settings, resettable]
      /set <项>              查看单项现值与出厂默认
      /set <项> <值>         修改（本次运行生效）
      /set all               列出全部（含进阶项）
      /set diff              只看已偏离默认的项
      /set reset [项]        还原为出厂默认（省略项=全部）
      /set profile           预设方案：cheap/strict/fast/manual/offline/debug
          ... profile <名>       套用（只改方案涉及的项）
          ... profile <名> --reset  先全部还原再套用
          ... profile <名> --show   只看会改什么，不执行
      /set save              把当前值写入 .env（持久化）
      /set approve_scope all|writes  报批范围（writes=只读免报批，默认；敏感文件仍报批）
      /set verify_readonly_python on|off  静态只读的 Python 免 AI 核验（省 token，默认 on）
  /help               显示本帮助 [show this help]
  /reload             重新载入环境文件 [reload .env]
  /timer [on|off]     开关轮次计时（本轮耗时 / 停留时长）[toggle round timer]
  /auto on|off|now    一键放行：报批时按 a / 1 也可；now = 立刻放行本轮 [auto-approve]
                      放行范围由 /set auto_approve_scope 控制（敏感文件永不自动放行）：
                        none   = 一律不自动放行（最严）
                        writes = 只放行内容写入（默认；删除 / 执行仍需确认）
                        all    = 除敏感文件外全放行（含删除 / 执行，风险自负）
  /model              查看当前主模型 / 接口地址 [show main model & base url]
  /model <模型名>      临时切换主模型（仅本次会话）[switch main model this session]
  /verify             查看双人核验状态 [show dual-AI verify status]
  /verify on|off|all  开启(仅有副作用)/关闭/全部核验 [on/off/all scope]
  /verify strict on|off   超重试上限即拦截 / 放行 [strict block or pass]
  /verify model <名>  切换审查员模型 [switch verifier model]
  /verify answer on|off   是否复核最终答复 [review final answer]
  /verify retries <n> 设置打回重交上限（仅 revise 计入） [set retry limit]
  /verify supplements <n> 设置免费补充资料轮数上限（不计退回次数） [set supplement limit]
  /verify fail open|closed  核验服务不可用时放行/拦截 [fail-open/closed]
  /verify mirror on|off    审查意见同步到监控器窗口 [mirror verdict to watcher]
  /verify ping        连通性自检（真调一次审查员）[connectivity self-test]
  exit / quit         退出程序 [exit program]
  ─────────────────────────────────────────
  含空格的路径请用双引号包裹，例如 [wrap paths with spaces in quotes, e.g.]：
    /read "C:\\my folder\\a.py"
  ─────────────────────────────────────────
  """

# ============ 开工自检（Boot Report / 启动环境可视化）============
# 启动时采集「运行环境快照」：① 终端可视化打印；② 注入系统提示词，让 AI 开局即知情。
# 只读、零第三方依赖；任何失败都原样退回 SYSTEM_PROMPT（见 boot_report.system_message）。
# 开关（.env）：BOOT_REPORT=1/0 打印面板；BOOT_REPORT_PEERS=1/0 扫描同名副本。
try:
    import boot_report
    _SYS_CONTENT = boot_report.system_message(
        SYSTEM_PROMPT,
        script_path=os.path.abspath(__file__),
        workspace_dir=workspace.get_workspace(),
        do_scan_peers=_env_bool("BOOT_REPORT_PEERS", True),
        verbose=_env_bool("BOOT_REPORT", True),
    )
except Exception as _boot_err:
    _SYS_CONTENT = SYSTEM_PROMPT
    print(paint(f"  ⚠️ 开工自检不可用（{_boot_err}），已跳过 "
                f"[boot report skipped]", BY, BOLD))

messages = [{"role": "system", "content": _SYS_CONTENT}]

print_banner()

# 启动校验
if not API_KEY:
    print(paint("  ⚠️  未检测到主模型 API key（FATFISH_API_KEY / DEEPSEEK_API_KEY），"
                "对话将失败 [no main API key found, chat will fail]", BR, BOLD))
if not TAVILY_API_KEY:
    print(paint("  ⚠️  未检测到 TAVILY_API_KEY，联网搜索将不可用 [TAVILY_API_KEY not found, web search unavailable]", BY, BOLD))

if _VERIFIER_KEY_BAD:
    print(paint("  ⚠️ VERIFIER_API_KEY 疑似无效（可能 .env 里该行写了行尾注释），"
                "已回退用主 key；请检查 .env", BY, BOLD))

print(paint(f"  🐟 就绪 · 用 /help 看命令，/status 看运行状态"
            f" [ready · /help for commands, /status for runtime info]", BC, DIM))
# ============ 统一设置中心（/set）============
# 所有可调参数在这里登记：读取启动值作为「默认值快照」，reset 可一键还原。
# 注：默认值取「程序启动时生效的值」（含 .env 覆盖），因此 reset = 回到启动状态。
import settings

def _rebuild_client():
    """按当前 API_KEY / BASE_URL 重建 OpenAI 客户端。"""
    global client
    client = OpenAI(api_key=API_KEY, base_url=BASE_URL)


def _mk_applier(name, *, rebuild=False, reconfig=False):
    """生成「写入全局变量 →（可选）重建客户端 / 重注入核验」的 applier。"""
    def _apply(v):
        globals()[name] = v
        if rebuild:
            _rebuild_client()
        if reconfig:
            _reconfig_verifier()
    return _apply


def _apply_verify_mirror(v):
    globals()["VERIFY_MIRROR"] = v
    globals()["VERIFY_MIRROR_PATH"] = (
        os.path.join(LOG_DIR, f"exec_verify_{os.getpid()}.out") if v else "")
    _reconfig_verifier()


def _apply_approve_run_tools(v):
    globals()["APPROVE_RUN_TOOLS"] = v
    # 与启动时的名单保持一致：只读类（含 ws_read）不在基础名单内，
    # 它们由 _approval_needed() 按「敏感文件 + approve_scope」动态决定。
    base = {"ws_write", "ws_append", "ws_replace", "ws_delete"}
    if v:
        base |= {"ws_run_cmd", "ws_run_python"}
    globals()["APPROVAL_REQUIRED"] = base


def _apply_approve_scope(v):
    """报批范围：all=只读也报批（旧行为）/ writes=只读免报批（新默认）。"""
    globals()["APPROVE_SCOPE"] = v if v in ("all", "writes") else "writes"


def _apply_auto_approve_scope(v):
    """一键放行可覆盖的范围：none / writes / all（默认）。

    writes 档下，删除与执行类永不自动放行 —— 保证人工闸门不会整体消失。
    all 档下仅敏感文件仍拦，删除 / 执行类也会被放行；此时审查员 AI 成为该批
    唯一的外部复核 —— 送审备注会按档位如实告知它（见主循环的 extra_note）。
    """
    globals()["AUTO_APPROVE_SCOPE"] = v if v in ("none", "writes", "all") else "all"


def _apply_net_mode(v):
    net_tools.NET_MODE = v


def _apply_tavily_mode(v):
    net_tools.TAVILY_MODE = v


# ---- 🧠 主模型 ----
settings.register("model", MODEL, desc="主模型名（执行者）", group="主模型",
                  env_name="FATFISH_MODEL", apply=_mk_applier("MODEL", rebuild=False))
settings.register("max_reply_tokens", MAX_REPLY_TOKENS,
                  desc="单次回复 max_tokens", group="主模型", type=int,
                  env_name="MAX_REPLY_TOKENS",
                  apply=_mk_applier("MAX_REPLY_TOKENS"))
settings.register("api_timeout", API_TIMEOUT,
                  desc="单次 API 请求超时（秒）", group="主模型", type=int,
                  env_name="API_TIMEOUT",
                  apply=_mk_applier("API_TIMEOUT"))

# ---- 🧿 双人核验 ----
settings.register("verify_mode", VERIFY_MODE, desc="核验范围", group="核验",
                  choices=["off", "auto", "all"], env_name="VERIFY_MODE",
                  apply=_mk_applier("VERIFY_MODE", reconfig=True))
settings.register("verify_strict", VERIFY_STRICT, desc="超重试上限即拦截", group="核验",
                  type=bool, env_name="VERIFY_STRICT",
                  apply=_mk_applier("VERIFY_STRICT", reconfig=True))
settings.register("verify_max_retries", VERIFY_MAX_RETRIES,
                  desc="打回重交次数上限", group="核验", type=int,
                  env_name="VERIFY_MAX_RETRIES",
                  apply=_mk_applier("VERIFY_MAX_RETRIES", reconfig=True))
settings.register("verify_max_supplements", VERIFY_MAX_SUPPLEMENTS,
                  desc="免费补充资料轮数上限（不计退回次数）", group="核验", type=int,
                  env_name="VERIFY_MAX_SUPPLEMENTS",
                  apply=_mk_applier("VERIFY_MAX_SUPPLEMENTS"))
settings.register("verify_final_answer", VERIFY_FINAL_ANSWER,
                  desc="是否复核最终答复", group="核验", type=bool,
                  env_name="VERIFY_FINAL_ANSWER", apply=_mk_applier("VERIFY_FINAL_ANSWER"))
settings.register("verify_fail_mode", VERIFY_FAIL_MODE,
                  desc="核验服务故障时", group="核验",
                  choices=["open", "closed"], env_name="VERIFY_FAIL_MODE",
                  apply=_mk_applier("VERIFY_FAIL_MODE", reconfig=True))
settings.register("verify_mirror", VERIFY_MIRROR,
                  desc="审查意见镜像到监控器", group="核验", type=bool,
                  env_name="VERIFY_MIRROR", apply=_apply_verify_mirror)
settings.register("verifier_model", VERIFIER_MODEL,
                  desc="审查员模型（建议与主模型不同）", group="核验",
                  env_name="VERIFIER_MODEL",
                  apply=_mk_applier("VERIFIER_MODEL", reconfig=True))

# 送审长度上限（进阶项）
_VL = [
    ("verifier_prompt_chars", "VERIFIER_PROMPT_CHARS", "送审内容总上限（字符）"),
    ("verifier_plan_chars", "VERIFIER_PLAN_CHARS", "执行者自述/计划上限（字符）"),
    ("verifier_action_preview", "VERIFIER_ACTION_PREVIEW", "单个动作预览上限（字符）"),
    ("verifier_replace_preview", "VERIFIER_REPLACE_PREVIEW", "replace old/new 上限（字符）"),
    ("verifier_goal_chars", "VERIFIER_GOAL_CHARS", "用户需求上限（字符）"),
    ("verifier_context_chars", "VERIFIER_CONTEXT_CHARS", "近期上下文上限（字符）"),
    ("verifier_answer_chars", "VERIFIER_ANSWER_CHARS", "待复核答复上限（字符）"),
    ("verifier_answer_ctx", "VERIFIER_ANSWER_CTX", "答复复核上下文上限（字符）"),
    ("verifier_max_tokens", "VERIFIER_MAX_TOKENS", "审查意见输出上限"),
    ("verifier_timeout", "VERIFIER_TIMEOUT", "单次审查超时（秒）"),
]
for _key, _var, _desc in _VL:
    _default = globals()[_var]
    if _default == 0:                     # 0 表示「用 verify_tools 内置默认」
        _default = getattr(verify_tools, {
            "VERIFIER_PROMPT_CHARS": "PROMPT_MAX_CHARS",
            "VERIFIER_PLAN_CHARS": "PLAN_MAX_CHARS",
            "VERIFIER_ACTION_PREVIEW": "ACTION_PREVIEW",
            "VERIFIER_REPLACE_PREVIEW": "REPLACE_PREVIEW",
            "VERIFIER_GOAL_CHARS": "GOAL_MAX_CHARS",
            "VERIFIER_CONTEXT_CHARS": "CONTEXT_MAX_CHARS",
            "VERIFIER_ANSWER_CHARS": "ANSWER_MAX_CHARS",
            "VERIFIER_ANSWER_CTX": "ANSWER_CTX_CHARS",
            "VERIFIER_MAX_TOKENS": "MAX_TOKENS",
            "VERIFIER_TIMEOUT": "TIMEOUT",
        }[_var], 0)
    settings.register(_key, _default, desc=_desc, group="核验", type=int,
                      env_name=_var, advanced=True,
                      apply=_mk_applier(_var, reconfig=True))
settings.register("verifier_temperature", VERIFIER_TEMPERATURE,
                  desc="审查员温度（0~2）", group="核验", type=float,
                  env_name="VERIFIER_TEMPERATURE",
                  advanced=True, apply=_mk_applier("VERIFIER_TEMPERATURE", reconfig=True))

# ---- 🌐 联网 ----
settings.register("net_mode", net_tools.NET_MODE,
                  desc="联网模式（ai=AI核验判断）", group="联网",
                  choices=["on", "off", "auto", "ai"], env_name="NET_MODE",
                  apply=_apply_net_mode)
settings.register("tavily_mode", net_tools.TAVILY_MODE,
                  desc="检索模式（auto=AI决定）", group="联网",
                  choices=["auto", "search", "extract", "both"], env_name="TAVILY_MODE",
                  apply=_apply_tavily_mode)
settings.register("extract_max_len", net_tools.EXTRACT_MAX_LEN,
                  desc="extract 单段最大字符", group="联网", type=int, advanced=True,
                  env_name="EXTRACT_MAX_LEN",
                  apply=lambda v: setattr(net_tools, "EXTRACT_MAX_LEN", v))

# ---- 🔐 报批 ----
# 回滚开关（2026-09-19 新增；默认写入「只读免报批」，可一条命令切回旧行为）
settings.register("approve_scope", APPROVE_SCOPE,
                  desc="报批范围 all=只读也报批 / writes=只读免报批(默认，敏感文件除外)",
                  group="报批", choices=["all", "writes"],
                  env_name="APPROVE_SCOPE", apply=_apply_approve_scope)
settings.register("verify_readonly_python", VERIFY_READONLY_PYTHON,
                  desc="静态判定的只读 Python 免 AI 核验（人工报批不变，省 token）",
                  group="报批", type=bool, env_name="VERIFY_READONLY_PYTHON",
                  apply=_mk_applier("VERIFY_READONLY_PYTHON"))
settings.register("approve_run_tools", APPROVE_RUN_TOOLS,
                  desc="跑命令/代码需人工批准", group="报批", type=bool,
                  env_name="APPROVE_RUN_TOOLS", apply=_apply_approve_run_tools)
settings.register("auto_approve_enabled", AUTO_APPROVE_ENABLED,
                  desc="允许一键放行（按 a/1）", group="报批", type=bool,
                  env_name="AUTO_APPROVE_ENABLED",
                  apply=_mk_applier("AUTO_APPROVE_ENABLED"))
settings.register("auto_approve_default", AUTO_APPROVE_DEFAULT,
                  desc="每轮默认免报批", group="报批", type=bool,
                  env_name="AUTO_APPROVE_DEFAULT",
                  apply=_mk_applier("AUTO_APPROVE_DEFAULT"))
settings.register("auto_approve_scope", AUTO_APPROVE_SCOPE,
                  desc="一键放行范围 none/writes/all(默认，除敏感文件外全放行，含删除/执行)",
                  group="报批", choices=["none", "writes", "all"],
                  env_name="AUTO_APPROVE_SCOPE", apply=_apply_auto_approve_scope)

# ---- 🖥 界面 ----
settings.register("show_timer", SHOW_TIMER, desc="显示本轮耗时/停留", group="界面",
                  type=bool, env_name="SHOW_TIMER", apply=_mk_applier("SHOW_TIMER"))
settings.register("show_wait_anim", SHOW_WAIT_ANIM,
                  desc="等待时显示转圈动画（- | / 四帧轮转）", group="界面", type=bool,
                  env_name="SHOW_WAIT_ANIM", apply=_mk_applier("SHOW_WAIT_ANIM"))
settings.register("boot_report", _env_bool("BOOT_REPORT", True),
                  desc="启动时打印开工自检面板（下次启动生效）", group="界面",
                  type=bool, env_name="BOOT_REPORT")
settings.register("boot_report_peers", _env_bool("BOOT_REPORT_PEERS", True),
                  desc="开工自检扫描同名程序副本（下次启动生效）", group="界面",
                  type=bool, env_name="BOOT_REPORT_PEERS")

# ---- 📏 上下文 ----
settings.register("max_history", MAX_HISTORY, desc="保留历史消息条数", group="上下文",
                  type=int, env_name="MAX_HISTORY",
                  apply=_mk_applier("MAX_HISTORY"))
settings.register("max_history_tokens", MAX_HISTORY_TOKENS,
                  desc="历史 token 预算", group="上下文", type=int,
                  env_name="MAX_HISTORY_TOKENS",
                  apply=_mk_applier("MAX_HISTORY_TOKENS"))
settings.register("trim_tool_clip_chars", TRIM_TOOL_CLIP_CHARS,
                  desc="单条 tool 结果截断长度", group="上下文", type=int,
                  env_name="TRIM_TOOL_CLIP_CHARS",
                  apply=_mk_applier("TRIM_TOOL_CLIP_CHARS"))
settings.register("max_tool_rounds", MAX_TOOL_ROUNDS,
                  desc="单轮工具调用轮数上限", group="上下文", type=int,
                  env_name="MAX_TOOL_ROUNDS",
                  apply=_mk_applier("MAX_TOOL_ROUNDS"))


# ============ 预设方案（/set profile）============
# 每套方案只列出「要改的项」，未列出的保持不动。
# 想从干净基线开始，用 /set profile <名> --reset。

# 💸 省钱模式：大幅压低送审量，省 token、省时间
settings.register_profile(
    "cheap", "省钱模式：压缩送审量，降低 token 消耗", icon="💸",
    aliases=["省钱", "save", "eco"],
    values={
        "verifier_prompt_chars": 20000,
        "verifier_plan_chars": 6000,
        "verifier_action_preview": 2000,
        "verifier_context_chars": 6000,
        "verifier_answer_chars": 10000,
        "verifier_max_tokens": 800,
        "max_history": 150,
        "max_history_tokens": 300000,
        "trim_tool_clip_chars": 15000,
        "verify_final_answer": False,
        "net_mode": "ai",
    })

# 🛡 铁壁模式：安全最大化，宁可慢也要稳
settings.register_profile(
    "strict", "铁壁模式：连只读也审、故障即停、禁一键放行", icon="🛡",
    aliases=["严格", "铁壁", "safe"],
    values={
        "verify_mode": "all",
        "verify_strict": True,
        "verify_max_retries": 3,
        "verify_final_answer": True,
        "verify_fail_mode": "closed",
        "approve_run_tools": True,
        "auto_approve_enabled": False,
        "verifier_prompt_chars": 64000,
        "net_mode": "ai",
        "show_timer": True,
    })

# ⚡ 极速模式：少打扰，适合信任场景下的批量作业
settings.register_profile(
    "fast", "极速模式：少打扰、免报批，适合信任场景", icon="⚡",
    aliases=["快速", "speed", "turbo"],
    values={
        "verify_mode": "auto",
        "verify_max_retries": 1,
        "verify_final_answer": False,
        "verify_fail_mode": "open",
        "auto_approve_enabled": True,
        "auto_approve_default": True,
        "show_timer": False,
    })

# 🖐 手动模式：每一步都要你点头
settings.register_profile(
    "manual", "手动模式：所有操作都需人工确认", icon="🖐",
    aliases=["手动", "hand"],
    values={
        "verify_mode": "all",
        "verify_strict": True,
        "verify_fail_mode": "closed",
        "approve_run_tools": True,
        "auto_approve_enabled": False,
        "auto_approve_default": False,
    })

# 🌐 离线模式：完全不联网
settings.register_profile(
    "offline", "离线模式：不联网，只用模型自身知识", icon="📴",
    aliases=["离线", "nolink"],
    values={"net_mode": "off"})

# 🔬 调试模式：观察核验全过程
settings.register_profile(
    "debug", "调试模式：全量核验 + 镜像 + 计时，便于观察", icon="🔬",
    aliases=["调试", "diag", "trace"],
    values={
        "verify_mode": "all",
        "verify_mirror": True,
        "show_timer": True,
        "net_mode": "auto",
    })

# 🏠 出厂默认（配合 --reset 使用）
settings.register_profile(
    "default", "还原为启动时的出厂配置", icon="🏠",
    aliases=["默认", "重置", "reset", "base"],
    values={})


log(f"[{_ts()}] === 会话开始 === 日志：{LOG_DIR}")

# ============ 命令可见化：让执行者 AI 看到斜杠命令及其结果 ============
# 背景：斜杠命令（/help、/verify on 等）在本地处理、直接 continue，
#       既不进 messages、也不回填给模型，于是「执行者」对用户敲过什么命令、
#       命令回了什么，一无所知。
# 做法：装一个「tee 流」，在命令执行期间把控制台输出复制一份（去掉 ANSI 色码）；
#       命令结束后暂存为「命令 + 输出」；待下一次真正调用模型时，
#       作为用户消息的前缀注入，使模型能感知这些命令与结果。
_CMD_TRANSCRIPT = []      # [(命令, 输出), ...] 最近若干条，待注入
_CMD_MAX_KEEP   = 20      # 最多保留多少条命令记录
_ANSI_RE        = re.compile(r"\x1b\[[0-9;]*m")

_cmd_state = {"on": False, "buf": [], "cmd": ""}


def _strip_ansi(s):
    return _ANSI_RE.sub("", s or "")


class _TeeStream:
    """把写到 stdout 的内容同时复制一份到当前命令缓冲（仅命令期间开启）。"""

    def __init__(self, real):
        self._real = real

    def write(self, s):
        try:
            self._real.write(s)
        except Exception:
            pass
        if _cmd_state["on"]:
            try:
                _cmd_state["buf"].append(s)
            except Exception:
                pass
        return len(s)

    def flush(self):
        try:
            self._real.flush()
        except Exception:
            pass

    def __getattr__(self, name):
        return getattr(self._real, name)


def _cmd_begin(cmd):
    """若本行是斜杠命令，开启输出捕获。"""
    if isinstance(cmd, str) and cmd.startswith("/"):
        _cmd_state["on"] = True
        _cmd_state["buf"] = []
        _cmd_state["cmd"] = cmd


def _cmd_finalize():
    """斜杠命令结束（每轮 finally 调用）：把「命令 + 输出」暂存，供下次注入。"""
    if not _cmd_state["on"]:
        return
    _cmd_state["on"] = False
    out = _strip_ansi("".join(_cmd_state["buf"])).strip()
    cmd = _cmd_state["cmd"]
    _cmd_state["buf"] = []
    _cmd_state["cmd"] = ""
    if cmd:
        _CMD_TRANSCRIPT.append((cmd, out))
        del _CMD_TRANSCRIPT[:-_CMD_MAX_KEEP]


def _cmd_abort():
    """本轮不是纯命令（普通发言 / /search 深入处理）：丢弃捕获，避免把整轮输出当成命令。"""
    _cmd_state["on"] = False
    _cmd_state["buf"] = []
    _cmd_state["cmd"] = ""


def _take_cmd_transcript():
    """取出并清空暂存命令记录，格式化成一段文本（注入用户消息前缀）。"""
    if not _CMD_TRANSCRIPT:
        return ""
    lines = ["【本轮之前你（用户）在本地执行过的命令与回显，仅供你了解现状，不是新的提问】"]
    for cmd, out in _CMD_TRANSCRIPT:
        lines.append(f"$ {cmd}")
        if out:
            lines.append(out)
    _CMD_TRANSCRIPT.clear()
    return "\n".join(lines)[:20000]


# 安装 tee（在 _force_utf8_streams 之后，包装的是最终 stdout）
try:
    if not isinstance(sys.stdout, _TeeStream):
        sys.stdout = _TeeStream(sys.stdout)
except Exception:
    pass


while True:
    try:
        user_input = input(make_prompt()).strip()
        if not user_input:
            continue
        _record_user_event("用户命令", user_input)

        # ---- 计时：先结算"你停留了多久"，再开始本轮计时 ----
        _mark_round_start()

        # ---- 读写报批：复位"一键放行"（作用域 = 本轮，你一发新命令就失效）----
        # 斜杠命令不算"干活"，静默复位即可，免得刷屏。
        _reset_auto_approve(quiet=user_input.startswith("/"))

        # ---- 命令可见化：斜杠命令进入输出捕获（供执行者 AI 感知）----
        _cmd_begin(user_input)

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

        # ---- /status 运行状态一览 ----
        if user_input in ("/status", "/st"):
            print(paint("  📋 运行状态 [runtime status]", BC, BOLD))
            print_startup_status()
            print(paint("  （各项均可单独调整：/set 查看与修改参数）", BC, DIM))
            continue

        # ---- /set 统一设置中心 ----
        if user_input == "/set" or user_input.startswith("/set ") or user_input == "/settings":
            body = "" if user_input == "/settings" else user_input[4:].strip()
            parts = body.split(None, 1)
            sub = parts[0].lower() if parts else ""
            arg = parts[1].strip() if len(parts) > 1 else ""

            # /set                     → 列表
            if sub == "":
                print(paint(settings.render(show_all=False), BC))
            # /set all                 → 含进阶项
            elif sub == "all":
                print(paint(settings.render(show_all=True), BC))
            # /set diff                → 只看偏离项
            elif sub in ("diff", "改过"):
                print(paint(settings.render_diff(), BY))
            # /set profile [名] [--reset] [--show]  → 预设方案
            elif sub in ("profile", "profiles", "方案", "预设"):
                a = arg.lower()
                if a in ("", "list", "ls"):
                    print(paint(settings.render_profiles(), BC))
                else:
                    tokens = arg.split()
                    name = tokens[0]
                    do_reset = "--reset" in tokens
                    do_show = "--show" in tokens or "--preview" in tokens
                    if do_show:
                        print(paint(settings.profile_preview(name), BC))
                    else:
                        ok, msg, applied, failed = settings.apply_profile(
                            name, reset_first=do_reset)
                        print(paint(("  " if ok else "  ⚠️  ") + msg,
                                    BG if ok else BR, BOLD))
                        if applied:
                            for k, old, new in applied:
                                rec = settings.record(k)
                                print(paint(f"       · {k:<26} {rec.fmt(old):<14} → {rec.fmt(new)}",
                                            BC, DIM))
                        if ok:
                            print(paint("     （仅本次运行生效；要持久化用 /set save）", BC, DIM))
            # /set save                → 写入 .env
            elif sub == "save":
                ok, msg = settings.save_to_env()
                print(paint("  💾 " + msg, BG if ok else BR, BOLD))
            # /set reset [key]         → 还原
            elif sub in ("reset", "default", "恢复"):
                ok, msg, changed = settings.reset(arg or None)
                print(paint(("  ♻️  " if ok else "  ⚠️  ") + msg, BG if ok else BR, BOLD))
                if changed:
                    print(paint("     （仅本次运行生效；想持久化请 /set save，"
                                "或把 .env 里对应的行删掉）", BC, DIM))
            else:
                # /set <key>            → 查看单项
                # /set <key> <value>    → 设置
                key = sub
                if arg == "":
                    if settings.has(key):
                        print(paint(settings.render(key), BC))
                    else:
                        print(paint(f"  ⚠️ 未知设置项：{key}（用 /set 查看全部）", BR, BOLD))
                else:
                    ok, msg = settings.set(key, arg)
                    color = BG if ok else BR
                    print(paint(("  ✅ " if ok else "  ⚠️  ") + msg, color, BOLD))
                    if ok:
                        print(paint("     （仅本次运行生效；要持久化用 /set save）", BC, DIM))
            log(f"[{_ts()}] /set {body}")
            continue

        # ---- /timer 计时显示开关 ----
        if user_input.startswith("/timer"):
            arg = user_input[6:].strip().lower()
            if arg in ("on", "off"):
                settings.set("show_timer", arg == "on")
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
                settings.set("auto_approve_enabled", arg == "on")
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

        # ---- /model 主模型查看 / 临时切换 ----
        if user_input == "/model" or user_input.startswith("/model "):
            arg = user_input[6:].strip()
            if not arg:
                masked = ("（未配置）" if not API_KEY else
                          (API_KEY[:6] + "…" + API_KEY[-4:]) if len(API_KEY) > 12 else "已配置")
                print(paint("  🧠 主模型 [main model] 状态：", BC, BOLD))
                print(paint(f"     · 模型 [model]    ：{MODEL}", BC))
                print(paint(f"     · 接口 [base_url] ：{BASE_URL}", BC))
                print(paint(f"     · 密钥 [api_key]  ：{masked}", BC))
                print(paint("     用法：/model <模型名> 临时切换（仅本次会话；持久化请改 .env 后 /reload）", BC))
            else:
                MODEL = arg
                print(paint(f"  🧠 主模型已临时切换为 [main model switched]：{MODEL}"
                            f"（仅本次会话；接口地址不变 {BASE_URL}）", BG, BOLD))
                log(f"[{_ts()}] 主模型临时切换为 {MODEL}")
            continue

        # ---- /verify 双人核验管理 ----
        if user_input.startswith("/verify") or user_input.startswith("/vfy"):
            _sp = user_input.split(None, 1)
            body = _sp[1].strip() if len(_sp) > 1 else ""
            _sp2 = body.split(None, 1)
            sub = _sp2[0].lower() if _sp2 and _sp2[0] else ""
            arg = _sp2[1].strip() if len(_sp2) > 1 else ""

            if sub in ("on", "auto"):
                settings.set("verify_mode", "auto")
                print(paint("  🧿 双人核验已开启（auto：仅核验有副作用动作）", BG, BOLD))
            elif sub == "all":
                settings.set("verify_mode", "all")
                print(paint("  🧿 双人核验已开启（all：连只读操作也核验）", BG, BOLD))
            elif sub == "off":
                settings.set("verify_mode", "off")
                print(paint("  🧿 双人核验已关闭 [verification off]", BR, BOLD))
            elif sub == "strict":
                _on = arg.lower() in ("on", "1", "true", "yes")
                settings.set("verify_strict", _on)
                print(paint(f"  🧿 严格模式 [strict]：{'ON（超限即拦截）' if _on else 'OFF（超限则放行）'}", BY, BOLD))
            elif sub == "model":
                if not arg:
                    print(paint(f"  🧿 当前审查员模型 [verifier model]：{verify_tools.MODEL}", BC, BOLD))
                    print(paint("     用法：/verify model <模型名>", BC))
                else:
                    settings.set("verifier_model", arg)
                    print(paint(f"  🧿 审查员模型已切换为：{verify_tools.MODEL}", BG, BOLD))
            elif sub == "answer":
                _on = arg.lower() in ("on", "1", "true", "yes")
                settings.set("verify_final_answer", _on)
                print(paint(f"  🧿 最终答复复核 [review final answer]：{'ON' if _on else 'OFF'}", BY, BOLD))
            elif sub == "retries":
                try:
                    _n = max(0, int(arg))
                    settings.set("verify_max_retries", _n)
                    print(paint(f"  🧿 打回重交上限已设为 {_n}", BG, BOLD))
                except (TypeError, ValueError):
                    print(paint(f"  🧿 当前打回重交上限：{verify_tools.MAX_RETRIES}（用法：/verify retries <n>）", BC, BOLD))
            elif sub in ("supplements", "suppl"):
                try:
                    _n = max(0, int(arg))
                    settings.set("verify_max_supplements", _n)
                    print(paint(f"  🧿 免费补充资料轮数上限已设为 {_n}（不计退回次数）", BG, BOLD))
                except (TypeError, ValueError):
                    print(paint(f"  🧿 当前免费补充资料轮数上限：{VERIFY_MAX_SUPPLEMENTS}"
                                f"（用法：/verify supplements <n>）", BC, BOLD))
            elif sub == "fail":
                if arg.lower() in ("open", "closed"):
                    settings.set("verify_fail_mode", arg.lower())
                    print(paint(f"  🧿 核验服务不可用时：[{arg.upper()}]", BY, BOLD))
                else:
                    print(paint(f"  🧿 当前故障策略：{verify_tools.FAIL_MODE.upper()}"
                                f"（可选 open / closed）", BC, BOLD))
            elif sub in ("mirror", "watch"):
                if arg.lower() in ("on", "off"):
                    _on = arg.lower() == "on"
                    settings.set("verify_mirror", _on)
                    print(paint(f"  📡 审查意见镜像到监控器 [mirror to watcher]："
                                f"{'ON' if _on else 'OFF'}"
                                f"{' → ' + os.path.basename(VERIFY_MIRROR_PATH) if _on else ''}",
                                BG if _on else BR, BOLD))
                    if _on:
                        verify_tools.mirror("📡 审查意见镜像已开启 [mirror enabled]")
                else:
                    print(paint(f"  📡 当前镜像 [mirror]：{'ON' if VERIFY_MIRROR else 'OFF'}"
                                f"{' → ' + VERIFY_MIRROR_PATH if VERIFY_MIRROR else ''}"
                                f"（用法：/verify mirror on|off）", BC, BOLD))
            elif sub in ("ping", "test"):
                print(paint("  🧿 正在自检审查员连通性（真实调用一次）…", BM, BOLD))
                _r = verify_tools.review(
                    [("ws_write", {"path": "__verify_ping__.txt", "content": "ping"})],
                    user_goal="（连通性自检：该动作无害，应当通过）",
                    ai_plan="我只想 ping 一下审查员，确认它在线。",
                )
                _print_verify_line(_r, tag="连通性自检")
            elif sub == "":
                print(paint("  🧿 双人核验状态 [dual-AI verify status]：", BC, BOLD))
                print(paint(f"     {verify_tools.mode_label()}", BC))
                print(paint(f"     · 最终答复复核：{'ON' if VERIFY_FINAL_ANSWER else 'OFF'}"
                            f" ｜ 本轮已退回：{_verify_retry}/{VERIFY_MAX_RETRIES}"
                            f" ｜ 补充资料轮：{_verify_suppl}/{VERIFY_MAX_SUPPLEMENTS}", BC))
                print(paint(f"     · 审查意见镜像到监控器 [mirror to watcher]："
                            f"{'ON' if VERIFY_MIRROR else 'OFF'}"
                            f"{' → ' + VERIFY_MIRROR_PATH if VERIFY_MIRROR else ''}", BC))
                print(paint(f"     · 送审上限 [limits，单位字符]：{verify_tools.limits_label()}", BC))
                print(paint("     用法：/verify on | off | all | strict on|off | model <名> | "
                            "answer on|off | retries <n> | supplements <n> | fail open|closed | mirror on|off | ping", BC))
            else:
                print(paint(f"  ⚠️ 未知参数：{sub}（试试 /verify 看用法）", BR, BOLD))
            log(f"[{_ts()}] /verify {body}")
            continue

        # ---- /clear ----
        if user_input == "/clear":
            _CMD_TRANSCRIPT.clear()   # 命令暂存一并清空，避免清空历史后又被注入旧命令
            _USER_EVENTS.clear()      # 用户操作记录一并清空
            # 重建 system 消息：顺带刷新开工自检（时间 / PID / 工作台可能已变）
            try:
                _SYS_CONTENT = boot_report.system_message(
                    SYSTEM_PROMPT,
                    script_path=os.path.abspath(__file__),
                    workspace_dir=workspace.get_workspace(),
                    do_scan_peers=_env_bool("BOOT_REPORT_PEERS", True),
                    verbose=False,
                )
            except Exception:
                _SYS_CONTENT = SYSTEM_PROMPT
            messages = [{"role": "system", "content": _SYS_CONTENT}]
            print(paint("  🧹 对话历史已清空 [Chat history cleared]", BY, BOLD))
            log(f"[{_ts()}] 用户清空了历史")
            continue

        # ---- /reload 重新加载 .env ----
        if user_input == "/reload":
            load_dotenv(override=True)
            new_key    = _env_clean("FATFISH_API_KEY") or _env_clean("DEEPSEEK_API_KEY")
            new_base   = (_env_clean("FATFISH_BASE_URL") or _env_clean("DEEPSEEK_BASE_URL")
                          or "https://api.deepseek.com")
            new_model  = (_env_clean("FATFISH_MODEL") or _env_clean("DEEPSEEK_MODEL")
                          or "deepseek-flash")
            new_tavily = _env_clean("TAVILY_API_KEY")
            if not new_key:
                print(paint("  ⚠️  .env 里没读到 API key（FATFISH_API_KEY / DEEPSEEK_API_KEY）"
                            " [no API key found in .env]", BR, BOLD))
                continue
            API_KEY, BASE_URL, MODEL = new_key, new_base, new_model
            TAVILY_API_KEY = new_tavily
            net_tools.set_api_key(new_tavily)
            client = OpenAI(api_key=API_KEY, base_url=BASE_URL)
            # 审查员配置一并重载（VERIFIER_* 留空时自动跟随主模型）
            VERIFY_MIRROR = _env_bool("VERIFY_MIRROR", True)
            VERIFY_MIRROR_PATH = (os.path.join(LOG_DIR, f"exec_verify_{os.getpid()}.out")
                                  if VERIFY_MIRROR else "")
            verify_tools.configure(
                api_key=_env_clean("VERIFIER_API_KEY") or API_KEY,
                base_url=_env_clean("VERIFIER_BASE_URL") or BASE_URL,
                model=_env_clean("VERIFIER_MODEL") or MODEL,
                mode=_env_clean("VERIFY_MODE", "auto").lower(),
                strict=_env_clean("VERIFY_STRICT", "1").lower()
                       not in ("0", "false", "no", "off", ""),
                max_retries=_env_int("VERIFY_MAX_RETRIES", 2),
                fail_mode=_env_clean("VERIFY_FAIL_MODE", "open").lower(),
                log_dir=LOG_DIR,
                mirror_path=VERIFY_MIRROR_PATH,
                mirror_on=VERIFY_MIRROR,
                max_tokens=_env_int("VERIFIER_MAX_TOKENS", 0) or None,
                timeout=_env_int("VERIFIER_TIMEOUT", 0) or None,
                prompt_max_chars=_env_int("VERIFIER_PROMPT_CHARS", 0) or None,
                action_preview=_env_int("VERIFIER_ACTION_PREVIEW", 0) or None,
                replace_preview=_env_int("VERIFIER_REPLACE_PREVIEW", 0) or None,
                goal_max_chars=_env_int("VERIFIER_GOAL_CHARS", 0) or None,
                plan_max_chars=_env_int("VERIFIER_PLAN_CHARS", 0) or None,
                context_max_chars=_env_int("VERIFIER_CONTEXT_CHARS", 0) or None,
                answer_max_chars=_env_int("VERIFIER_ANSWER_CHARS", 0) or None,
                answer_ctx_chars=_env_int("VERIFIER_ANSWER_CTX", 0) or None,
            )
            VERIFY_FINAL_ANSWER = _env_bool("VERIFY_FINAL_ANSWER", False)
            print(paint("  🔄 已重新加载 .env [.env reloaded]", BG, BOLD))
            print(paint(f"     🧠 主模型 [main model]：{MODEL} @ {BASE_URL}", BC))
            print(paint(f"     🧿 审查员 [verifier]：{verify_tools.MODEL} @ {verify_tools.BASE_URL}", BC))
            print(paint(f"     📡 审查意见镜像 [mirror]：{'ON' if VERIFY_MIRROR else 'OFF'}"
                        f"{' → ' + os.path.basename(VERIFY_MIRROR_PATH) if VERIFY_MIRROR else ''}", BC))
            log(f"[{_ts()}] 用户重载了 .env：model={MODEL} base_url={BASE_URL}")
            continue

        # ---- /net 切换联网模式 ----
        if user_input.startswith("/net"):
            arg = user_input[4:].strip().lower()
            if arg in ("on", "off", "auto", "ai"):
                settings.set("net_mode", arg)
                color = {"on": BG, "off": BR, "auto": BY, "ai": BC}[arg]
                desc = {"on": "总是联网", "off": "从不联网",
                        "auto": "关键词规则", "ai": "AI 核验判断（推荐）"}[arg]
                print(paint(f"  🌐 联网模式已切换为 [net mode switched to]：{arg.upper()}"
                            f" —— {desc}", color, BOLD))
                log(f"[{_ts()}] 联网模式切换为 {arg}")
            elif arg == "":
                cur = net_tools.NET_MODE.upper()
                print(paint(f"  🌐 当前联网模式 [current net mode]：{cur}", BC, BOLD))
                print(paint("     on   = 总是联网 [always search]", BC))
                print(paint("     off  = 从不联网 [never search]", BC))
                print(paint("     auto = 关键词规则 [keyword rules]", BC))
                print(paint("     ai   = 🤖 由第二位 AI 核验判断是否需要联网"
                            "（可顺带改写检索词）[AI-judged, recommended]", BC))
                print(paint("     用法 [usage]：/net on | /net off | /net auto | /net ai", BC))
            else:
                print(paint(f"  ⚠️  未知参数 [unknown arg]：{arg}"
                            f"（可选 [options] on / off / auto / ai）", BR, BOLD))
            continue

        # ---- /tavily 切换 Tavily 模式 ----
        if user_input.startswith("/tavily"):
            arg = user_input[7:].strip().lower()
            if arg in ("search", "extract", "auto", "both"):
                settings.set("tavily_mode", arg)
                color = {"search": BC, "extract": BM, "auto": BY, "both": BG}[arg]
                desc = {"search": "只搜索", "extract": "只抓正文",
                        "auto": "有 URL 抓正文，否则搜索", "both": "先搜再抓前几条正文"}[arg]
                print(paint(f"  🔍 Tavily 模式已切换为 [mode switched to]：{arg.upper()}"
                            f" —— {desc}"
                            + ("（已锁定，AI 不再决定模式）" if arg in ("search", "extract") else ""),
                            color, BOLD))
                log(f"[{_ts()}] Tavily 模式切换为 {arg}")
            elif arg == "":
                print(paint(f"  🔍 当前 Tavily 模式 [current mode]：{net_tools.TAVILY_MODE.upper()}", BC, BOLD))
                print(paint("     auto    = 有 URL 抓正文，否则搜索 [auto]", BC))
                print(paint("     search  = 强制只搜索（AI 不再决定模式）[force search]", BC))
                print(paint("     extract = 强制只抓正文（需 URL）[force extract]", BC))
                print(paint("     both    = 先搜索，再抓取结果前 2 条正文 [search + extract]", BC))
                print(paint("     用法 [usage]：/tavily auto | search | extract | both", BC))
                if net_tools.NET_MODE == "ai":
                    print(paint("     💡 当前联网模式为 AI：留 auto 即由 AI 决定 search/extract", BY, DIM))
            else:
                print(paint(f"  ⚠️  未知参数 [unknown arg]：{arg}"
                            f"（可选 [options] auto / search / extract / both）", BR, BOLD))
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

        # 走到这里说明不是纯斜杠命令（普通发言 / /search 深入处理）：
        # 结束命令捕获，并把「本轮之前敲过的命令 + 输出」注入用户消息前缀。
        _cmd_abort()
        _cmd_hist = _take_cmd_transcript()
        parts = [user_input]
        if _cmd_hist:
            parts.insert(0, _cmd_hist)
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
        search_query = None
        search_mode = None
        search_urls = None
        if force_search:
            should_search = True
        elif net_tools.NET_MODE == "on":
            should_search = True
        elif net_tools.NET_MODE == "ai":
            # 🤖 第二位 AI 判定：要不要联网 + 用 search 还是 extract
            #    （若用户已用 /tavily 锁定模式，net_tools 会优先尊重它）
            _wspin = _wait_start("判断是否需要联网")
            try:
                jv = verify_tools.judge_search(user_input, _recent_context(messages))
            except BaseException as _wexc:
                _wait_stop(_wspin, ok=False, label="联网判断失败",
                           note=type(_wexc).__name__)
                raise
            _wait_stop(_wspin, ok=True, label="联网判断完成")
            _print_search_judge_line(jv)
            if jv.get("failed"):
                should_search = net_tools.need_search(user_input)
                if should_search:
                    print(paint("  🌐 核验异常 → 回退关键词规则：需要联网", BY, DIM))
            else:
                should_search = bool(jv.get("need_search"))
                search_mode = jv.get("mode") or None
                search_query = jv.get("query") or None
                search_urls = jv.get("urls") or None
        elif net_tools.NET_MODE == "auto":
            should_search = net_tools.need_search(user_input)

        if should_search:
            print(paint("  🌐 正在联网 [connecting]...", BB, ITAL))
            _eff_mode = net_tools.TAVILY_MODE if net_tools.TAVILY_MODE in ("search", "extract") \
                else (search_mode or net_tools.TAVILY_MODE)
            if _eff_mode == "extract":
                _us = search_urls or net_tools.extract_urls(user_input)
                print(paint(f"     📄 模式=extract（抓取正文 {len(_us)} 个 URL）", BB, DIM))
            else:
                if search_query and search_query != user_input:
                    print(paint(f"     🔎 模式=search ｜ 检索词（已核验改写）：{search_query}", BB, DIM))
                else:
                    print(paint(f"     🔎 模式=search", BB, DIM))
            parts.append("【联网结果】\n" + net_tools.do_network(
                user_input, query=search_query, mode=search_mode, urls=search_urls))

        content = file_tools.build_content("\n\n".join(parts), image_blocks)
        messages.append({"role": "user", "content": content})
        messages = trim_history(messages, MAX_HISTORY)

        # ---- 3) 调用模型（支持工作台工具循环）----
        _verify_retry = 0          # 双人核验：本轮退回次数清零（仅 revise 计）
        _verify_suppl = 0          # 双人核验：本轮「补充资料」轮数清零（不计退回）
        for _round in range(MAX_TOOL_ROUNDS):
            # 发送前兜底清洗，杜绝 "tool must follow tool_calls" 报错
            messages = _sanitize_messages(messages)
            # 等待动画：模型思考期间原地转圈 + 计时，一眼分辨「在跑」还是卡死
            _wspin = _wait_start("等待模型响应")
            try:
                resp = client.chat.completions.create(
                    model=MODEL,
                    messages=messages,
                    temperature=0.7,
                    max_tokens=MAX_REPLY_TOKENS,
                    timeout=API_TIMEOUT,
                    tools=workspace.TOOL_SCHEMAS,
                )
            except BaseException as _wexc:
                _wait_stop(_wspin, ok=False, label="模型请求失败",
                           note=type(_wexc).__name__)
                raise
            _wait_stop(_wspin, ok=True, label="模型已响应")
            choice = resp.choices[0]
            msg = choice.message

            # 无工具调用 → 正常回复，结束循环
            if not getattr(msg, "tool_calls", None):
                if choice.finish_reason == "length":
                    print(paint("  ⚠️  回复被 max_tokens 截断，代码可能不完整 [reply truncated, code may be incomplete]！", BR, BOLD))
                reply = msg.content or ""

                # ---- 双人核验：最终答复复核（可选，默认关，/verify answer on 开启）----
                _vf_ans_free = _verify_suppl < VERIFY_MAX_SUPPLEMENTS
                if VERIFY_FINAL_ANSWER and verify_tools.is_enabled() \
                        and (_verify_retry < VERIFY_MAX_RETRIES or _vf_ans_free):
                    _wspin = _wait_start("复核最终答复")
                    try:
                        vres = verify_tools.review_answer(
                            reply,
                            user_goal=_last_user_text(messages),
                            context_text=_recent_context(messages),
                        )
                    except BaseException as _wexc:
                        _wait_stop(_wspin, ok=False, label="答复复核失败",
                                   note=type(_wexc).__name__)
                        raise
                    _wait_stop(_wspin, ok=True, label="答复复核完成")
                    _print_verify_line(vres, tag="答复核验")
                    if vres.get("blocked"):
                        # 需求：仅「要求补充资料」（supplement）不计退回次数，只计补充轮数
                        _vf_suppl_mode = ((vres.get("verdict") or "").strip().lower()
                                          == "supplement" and _vf_ans_free)
                        if _vf_suppl_mode:
                            _verify_suppl += 1
                            _vf_ans_tag = (f"要求补充/改写答复（不计退回次数，"
                                           f"补充轮 {_verify_suppl}/{VERIFY_MAX_SUPPLEMENTS}）")
                        else:
                            _verify_retry += 1
                            _vf_ans_tag = f"（第 {_verify_retry}/{VERIFY_MAX_RETRIES} 次）"
                        print(paint(f"  🔁 审查员{_vf_ans_tag}", BY, BOLD))
                        log(f"[{_ts()}] 答复核验打回：{vres.get('reason')}"
                            f"（suppl={_verify_suppl}/{VERIFY_MAX_SUPPLEMENTS}，"
                            f"retry={_verify_retry}/{VERIFY_MAX_RETRIES}）")
                        verify_tools.mirror("-" * 60)
                        verify_tools.mirror(f"[{_ts()}] 🔁 双人核验·答复被打回（{_vf_ans_tag}）")
                        verify_tools.mirror(f"    理由：{vres.get('reason', '')}")
                        messages.append({"role": "assistant", "content": reply})
                        messages.append({"role": "user",
                                         "content": verify_tools.feedback_text(vres, kind="answer")})
                        continue

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

            # ---- 双人核验：把有副作用的动作先交给第二位 AI（审查员）复核 ----
            v_picks = verify_tools.pick_actions(parsed_calls)
            # 省 token：静态判定为只读的 ws_run_python 跳过 AI 核验（默认拒绝式判定，
            #   拿不准就照常送审）。豁免的只是「第二意见」，人工报批照旧生效。
            #   仅在 auto 模式生效；verify_mode=all（铁壁模式）时一切照旧全审。
            if v_picks and VERIFY_READONLY_PYTHON and verify_tools.MODE == "auto":
                _ro_kept = []
                for _pick in v_picks:
                    if _pick[1] == "ws_run_python":
                        _ro_code = (_pick[2] or {}).get("code", "") or ""
                        _is_ro, _ro_why = _python_is_readonly(_ro_code)
                        if _is_ro:
                            log(f"[{_ts()}] 只读 Python 免 AI 核验（静态判定通过，"
                                f"{len(_ro_code)} 字符；人工报批仍生效）")
                            continue
                    _ro_kept.append(_pick)
                v_picks = _ro_kept
            if v_picks:
                print(paint(f"  🧿 双人核验中 [verifying] {len(v_picks)} 个动作"
                            f"（审查员 [verifier]：{verify_tools.MODEL}）…", BM, BOLD))
                _wspin = _wait_start("双人核验中")
                try:
                    _v_attempt = _verify_retry + _verify_suppl + 1
                    vres = verify_tools.review(
                        [(n, a) for (_i, n, a) in v_picks],
                        user_goal=_last_user_text(messages),
                        ai_plan=msg.content or "",
                        context_text=_recent_context(messages),
                        user_events=_recent_user_events(),
                        extra_note=(
                            f"当前工作台根：{workspace.get_workspace()}\n"
                            f"本轮第 {_v_attempt} 次送审"
                            + ("（此前已送审过同一动作，请先核对你的上一条要求"
                               "是否已被满足，不要另提无关的新要求）"
                               if _v_attempt > 1 else "")
                            # 如实说明（按档位分述）：被一键放行覆盖的动作没有人工确认了。
                            # ★ 2026-09-19：范围=all 时删除 / 执行也会被放行，必须让审查员
                            #   明确知道自己对这一批已无人类兜底（如实告知，不夸大也不隐瞒）。
                            + ("\n⚠️ 本轮已开启「一键放行」（范围：" + AUTO_APPROVE_SCOPE + "）："
                               + {"all": "**含删除 / 执行命令 / 执行代码在内**"
                                         "（仅敏感文件除外）都会自动放行，"
                                         "本批不存在任何逐批人工确认",
                                  "writes": "覆盖内容写入（写 / 追加 / 替换），"
                                            "删除 / 执行类仍会逐批人工确认",
                                  "none": "实际不覆盖任何动作（等于回到逐批确认）",
                                  }.get(AUTO_APPROVE_SCOPE, "覆盖范围见档位说明")
                               + "。对这些动作，你是唯一的外部复核 —— "
                                 "请按原本标准从严把关，"
                                 "不要因「用户已表示信任」而放宽。"
                               if _AUTO_APPROVE_TURN else "")
                        ),
                    )
                except BaseException as _wexc:
                    _wait_stop(_wspin, ok=False, label="核验失败",
                               note=type(_wexc).__name__)
                    raise
                _wait_stop(_wspin, ok=True, label="核验完成")
                _print_verify_line(vres)
                if vres.get("blocked"):
                    # ★ 需求：「要求补充资料」（supplement）不计入退回次数，只计补充轮数；
                    #   为防审查员反复索取资料造成死循环，另设免费补充轮数上限
                    #   VERIFY_MAX_SUPPLEMENTS（用尽后按 revise 计入退回次数）。
                    _vf_vd = (vres.get("verdict") or "").strip().lower()
                    _vf_soft = (_vf_vd == "supplement"
                                and _verify_suppl < VERIFY_MAX_SUPPLEMENTS)
                    if _vf_soft:
                        _verify_suppl += 1
                        _vf_tag = (f"要求补充说明（不计退回次数，"
                                   f"补充轮 {_verify_suppl}/{VERIFY_MAX_SUPPLEMENTS}）")
                    elif _verify_retry < VERIFY_MAX_RETRIES:
                        _verify_retry += 1
                        _vf_tag = (f"未放行，已打回执行者补充/改方案"
                                   f"（第 {_verify_retry}/{VERIFY_MAX_RETRIES} 次）")
                    else:
                        _vf_tag = ""
                    if _vf_tag:
                        # 打回执行者：补充说明或变更方案后重新提交（不执行本批动作）
                        blocked_idx = {i for (i, _n, _a) in v_picks}
                        fb = verify_tools.feedback_text(vres)
                        for i, (tc, name, args) in enumerate(parsed_calls):
                            content = fb if i in blocked_idx else (
                                "（同一批次中其他动作的双人核验未通过，本操作一并暂缓执行；"
                                "待执行者补充说明或变更方案后重新提交核验。）")
                            messages.append({"role": "tool",
                                             "tool_call_id": tc.id,
                                             "content": content})
                        print(paint(f"  🔁 审查员{_vf_tag}[sent back for revision]", BY, BOLD))
                        log(f"[{_ts()}] 双人核验打回：{vres.get('reason')}"
                            f"（suppl={_verify_suppl}/{VERIFY_MAX_SUPPLEMENTS}，"
                            f"retry={_verify_retry}/{VERIFY_MAX_RETRIES}）")
                        verify_tools.mirror("-" * 60)
                        verify_tools.mirror(f"[{_ts()}] 🔁 双人核验·动作被打回，等待执行者"
                                            f"（{_vf_tag}）")
                        verify_tools.mirror(f"    理由：{vres.get('reason', '')}")
                        verify_tools.mirror("    → 本批动作未执行，已把审查意见回填给执行者。")
                        continue
                    if VERIFY_STRICT:
                        # 超过重试上限 + 严格模式 → 整批拦截
                        fb = verify_tools.feedback_text(vres, final=True)
                        for tc, name, args in parsed_calls:
                            messages.append({"role": "tool",
                                             "tool_call_id": tc.id,
                                             "content": fb})
                        print(paint("  🛑 已达双人核验重试上限，动作被拦截 "
                                    "[verification retry limit reached — blocked]", BR, BOLD))
                        log(f"[{_ts()}] 双人核验超限拦截：{vres.get('reason')}")
                        verify_tools.mirror("-" * 60)
                        verify_tools.mirror(f"[{_ts()}] 🛑 双人核验超限 → 动作被拦截"
                                            f"（严格模式，全批未执行）")
                        verify_tools.mirror(f"    理由：{vres.get('reason', '')}")
                        break
                    print(paint("  ⚠️ 已达双人核验重试上限，按宽松模式放行 "
                                "[retry limit reached — proceeding leniently]", BY, BOLD))
                    log(f"[{_ts()}] 双人核验超限放行（宽松）：{vres.get('reason')}")
                    verify_tools.mirror("-" * 60)
                    verify_tools.mirror(f"[{_ts()}] ⚠️ 双人核验超限 → 宽松模式放行"
                                        f"（继续走人工报批）")
                    verify_tools.mirror(f"    理由：{vres.get('reason', '')}")

            # ---- 批量报批：把需要批准的工具调用汇总，问一次 ----
            #   判定已收敛到 _approval_needed()：写/删/执行必报批；
            #   ws_read 默认免报批，但命中敏感文件（.env / 密钥 / 凭据）时强制恢复报批。
            #
            # ★ 2026-09-19：一键放行**不再覆盖高危动作**（见 _never_auto_approve）。
            #   否则人工闸门被整体跳过，审查员 AI 会成为事实上的最终裁定者。
            #   本批需要批准的动作按索引分流：可自动放行的 / 必须亲手确认的。
            _need = [(i, n, a) for i, (_tc, n, a) in enumerate(parsed_calls)
                     if _approval_needed(n, a)[0]]
            approval_reply = None
            _deny = set()
            if _need:
                _auto_i, _must = set(), []
                for _i, _n, _a in _need:
                    if _AUTO_APPROVE_TURN and not _never_auto_approve(_n, _a):
                        _auto_i.add(_i)
                    else:
                        _must.append((_i, _n, _a))
                if _auto_i:
                    _auto_names = ", ".join(sorted(parsed_calls[_i][1] for _i in _auto_i))
                    print(paint(f"  🔓 本轮自动放行 {len(_auto_i)} 个内容写入操作（无需再确认）"
                                f"[auto-approved: {len(_auto_i)} write op(s)]", BB, DIM))
                    log(f"[{_ts()}] 自动放行 {len(_auto_i)} 个内容写入操作"
                        f"（scope={AUTO_APPROVE_SCOPE}）：{_auto_names}")
                if _must:
                    if _AUTO_APPROVE_TURN:
                        print(paint(f"  🛡 另有 {len(_must)} 个动作属「永不自动放行」类别"
                                    f"（删除 / 执行 / 敏感文件），仍需你亲手确认"
                                    f" [never auto-approved: {len(_must)}]", BR, BOLD))
                    _ok2, approval_reply = _request_approval(
                        [(n, a) for (_i, n, a) in _must])
                    if not _ok2:
                        _deny = {i for (i, _n, _a) in _must}

            # ---- 逐个执行 ----
            for _idx, (tc, name, args) in enumerate(parsed_calls):
                if _idx in _deny:
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
        _cmd_finalize()      # 斜杠命令：把「命令 + 输出」暂存，供下次注入给模型
        _mark_round_end()