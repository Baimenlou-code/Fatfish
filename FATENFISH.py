import os as _os
import sys as _sys
_os.environ["PYTHONUTF8"] = "1"
_os.environ["PYTHONIOENCODING"] = "utf-8"
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

load_dotenv()

def _force_utf8_streams():
    if sys.platform != "win32":
        return
    os.environ.setdefault("PYTHONUTF8", "1")
    os.environ.setdefault("PYTHONIOENCODING", "utf-8")
    import io as _io
    for name in ("stdin", "stdout", "stderr"):
        stream = getattr(sys, name, None)
        if stream is None:
            continue
        if hasattr(stream, "reconfigure"):
            try:
                stream.reconfigure(encoding="utf-8", errors="replace")
                continue
            except Exception:
                pass
        buf = getattr(stream, "buffer", None)
        if buf is not None:
            try:
                setattr(sys, name, _io.TextIOWrapper(
                    buf, encoding="utf-8", errors="replace", line_buffering=True))
            except Exception:
                pass

_force_utf8_streams()

from ui_core import (
    RESET, K, R, G, Y, BL, M, CY, W, BK, BR, BG, BY, BB, BM, BC, BW,
    BOLD, DIM, ITAL, UND, RV, ST, BGR, BGG, BGY, BGB,
    fg256, bg256, rgb, paint, rainbow, gradient,
    MOOD_COLOR, MOOD_EMOJI, MOOD_KEYWORDS, MOOD_ORDER, detect_mood,
    TAG_COLOR, TAG_RE, render_markup, strip_markup, render_inline,
    print_banner, print_ai,
    Wait, WAIT_FRAMES,
)

def print_startup_status():
    print(paint(f"  [main] main model: {MODEL} @ {BASE_URL}", BC))
    print(paint(f"  [net] net mode: {net_tools.NET_MODE.upper()}"
                f" | tavily: {net_tools.TAVILY_MODE.upper()}", BC))
    print(paint(f"  [ws] workspace: {workspace.get_workspace()}", BC))
    print(paint(f"  [verify] dual-AI verify: {verify_tools.mode_label()}", BC))
    print(paint(f"  [mirror] verify mirror: {'ON' if VERIFY_MIRROR else 'OFF'}"
                + (f" → {os.path.basename(VERIFY_MIRROR_PATH)}" if VERIFY_MIRROR else ""), BC))
    _aa_scope = {"none": "auto-approve off", "writes": "content writes only", "all": "incl. delete/exec"}.get(
        AUTO_APPROVE_SCOPE, AUTO_APPROVE_SCOPE)
    _aa_note = {"writes": "delete/exec/sensitive files are never auto-approved",
                "none": "never auto-approve anything (batch confirmation only)",
                "all": "only sensitive files are never auto-approved (delete/exec are released too; at your own risk)"}.get(
        AUTO_APPROVE_SCOPE, "")
    print(paint(f"  [auto] one-key auto-approve: {'ON' if AUTO_APPROVE_ENABLED else 'OFF'}"
                f" (scope: {_aa_scope}; {_aa_note})"
                f" | timer: {'ON' if SHOW_TIMER else 'OFF'}", BC))
    print(paint(f"  [approve] approval scope: {APPROVE_SCOPE.upper()}"
                f" ({'read-only also asks' if APPROVE_SCOPE == 'all' else 'read-only skips, sensitive files still ask'})"
                f" | readonly-python skips verify: {'ON' if VERIFY_READONLY_PYTHON else 'OFF'}",
                BC))
    print(paint(f"  [log] logs: {LOG_DIR}", BC))
    print(paint(f"  [code] code: {CODE_DIR}", BC))

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

API_KEY        = _env_clean("FATFISH_API_KEY") or _env_clean("DEEPSEEK_API_KEY")
BASE_URL       = (_env_clean("FATFISH_BASE_URL") or _env_clean("DEEPSEEK_BASE_URL")
                  or "https://api.deepseek.com")
MODEL          = (_env_clean("FATFISH_MODEL") or _env_clean("DEEPSEEK_MODEL")
                  or "deepseek-flash")
TAVILY_API_KEY = _env_clean("TAVILY_API_KEY")

BOOT_MODEL     = MODEL
BOOT_BASE_URL  = BASE_URL

MAX_HISTORY    = _env_int("MAX_HISTORY", 500)
MAX_HISTORY_TOKENS = _env_int("MAX_HISTORY_TOKENS", 800000)
TRIM_KEEP_FIRST_USER = True
TRIM_TOOL_CLIP_CHARS = _env_int("TRIM_TOOL_CLIP_CHARS", 40000)
MAX_REPLY_TOKENS = _env_int("MAX_REPLY_TOKENS", 131072)
MAX_TOOL_ROUNDS  = _env_int("MAX_TOOL_ROUNDS", 512)
LOG_ROOT       = "logs"
CODE_ROOT      = "generated_code"
API_TIMEOUT    = _env_int("API_TIMEOUT", 900)

_VERIFIER_KEY_RAW  = _env_clean("VERIFIER_API_KEY", "")
_VERIFIER_KEY_BAD  = bool(_VERIFIER_KEY_RAW) and not _looks_like_key(_VERIFIER_KEY_RAW)
VERIFIER_API_KEY    = API_KEY if _VERIFIER_KEY_BAD else (_VERIFIER_KEY_RAW or API_KEY)
VERIFIER_BASE_URL   = _env_clean("VERIFIER_BASE_URL", "") or BASE_URL
VERIFIER_MODEL      = _env_clean("VERIFIER_MODEL", "") or MODEL
VERIFY_MODE         = _env_clean("VERIFY_MODE", "auto").lower()
VERIFY_STRICT       = _env_clean("VERIFY_STRICT", "1").lower() not in ("0", "false", "no", "off", "")
VERIFY_MAX_RETRIES  = _env_int("VERIFY_MAX_RETRIES", 2)
VERIFY_MAX_SUPPLEMENTS = _env_int("VERIFY_MAX_SUPPLEMENTS", 4)
VERIFY_FINAL_ANSWER = _env_bool("VERIFY_FINAL_ANSWER", False)
VERIFY_FAIL_MODE    = _env_clean("VERIFY_FAIL_MODE", "open").lower()
VERIFY_MIRROR       = _env_bool("VERIFY_MIRROR", True)
VERIFIER_MAX_TOKENS      = _env_int("VERIFIER_MAX_TOKENS", 0)
VERIFIER_TIMEOUT         = _env_int("VERIFIER_TIMEOUT", 0)
VERIFIER_TEMPERATURE     = _env_float("VERIFIER_TEMPERATURE", 0.2)
VERIFIER_PROMPT_CHARS    = _env_int("VERIFIER_PROMPT_CHARS", 0)
VERIFIER_ACTION_PREVIEW  = _env_int("VERIFIER_ACTION_PREVIEW", 0)
VERIFIER_REPLACE_PREVIEW = _env_int("VERIFIER_REPLACE_PREVIEW", 0)
VERIFIER_GOAL_CHARS      = _env_int("VERIFIER_GOAL_CHARS", 0)
VERIFIER_PLAN_CHARS      = _env_int("VERIFIER_PLAN_CHARS", 0)
VERIFIER_CONTEXT_CHARS   = _env_int("VERIFIER_CONTEXT_CHARS", 0)
VERIFIER_ANSWER_CHARS    = _env_int("VERIFIER_ANSWER_CHARS", 0)
VERIFIER_ANSWER_CTX      = _env_int("VERIFIER_ANSWER_CTX", 0)

def _verify_limit_kwargs():
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

_verify_retry       = 0
_verify_suppl       = 0

SHOW_TIMER      = _env_bool("SHOW_TIMER", True)
SHOW_WAIT_ANIM     = _env_bool("SHOW_WAIT_ANIM", True)
WAIT_ANIM_INTERVAL = _env_float("WAIT_ANIM_INTERVAL", 0.08)
_TIMER_EXITING  = False
_LAST_ROUND_END = None
_ROUND_START    = None
_ROUND_ACTIVE   = False

def _fmt_secs(s):
    s = max(0.0, float(s))
    if s < 60:
        return f"{s:.1f}s"
    if s < 3600:
        return f"{int(s // 60)}m{s % 60:.1f}s"
    return f"{int(s // 3600)}h{int((s % 3600) // 60):02d}m"

def _mark_round_start():
    global _LAST_ROUND_END, _ROUND_START, _ROUND_ACTIVE
    now = time.time()
    if SHOW_TIMER and _LAST_ROUND_END is not None:
        print(paint(f"  [..] linger {_fmt_secs(now - _LAST_ROUND_END)}", BK, DIM))
    _ROUND_START = now
    _ROUND_ACTIVE = True

def _mark_round_end():
    global _LAST_ROUND_END, _ROUND_ACTIVE
    now = time.time()
    _LAST_ROUND_END = now
    if SHOW_TIMER and _ROUND_ACTIVE and _ROUND_START is not None and not _TIMER_EXITING:
        cost = now - _ROUND_START
        print(paint(f"  [..] round {_fmt_secs(cost)}", BK, DIM))
        log(f"[{_ts()}] round took {cost:.2f}s")
    _ROUND_ACTIVE = False

def _wait_start(label):
    if not SHOW_WAIT_ANIM:
        return None
    try:
        return Wait(label, interval=WAIT_ANIM_INTERVAL).start()
    except Exception:
        return None

def _wait_stop(w, ok=True, label=None, note=""):
    if w is None:
        return
    try:
        w.stop(ok=ok, label=label, note=note)
    except Exception:
        pass

try:
    from common import dated_dir as _dated_dir, ts as _ts
except ImportError:
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

client = OpenAI(api_key=API_KEY, base_url=BASE_URL)

net_tools.set_api_key(TAVILY_API_KEY)

_net_mode_env = _env_clean("NET_MODE", "").lower()
if _net_mode_env in ("on", "off", "auto", "ai"):
    net_tools.NET_MODE = _net_mode_env
_tavily_mode_env = _env_clean("TAVILY_MODE", "").lower()
if _tavily_mode_env in ("search", "extract", "auto", "both"):
    net_tools.TAVILY_MODE = _tavily_mode_env
_extract_env = _env_int("EXTRACT_MAX_LEN", 0)
if _extract_env > 0:
    net_tools.EXTRACT_MAX_LEN = _extract_env

VERIFY_MIRROR_PATH = (os.path.join(LOG_DIR, f"exec_verify_{os.getpid()}.out")
                      if VERIFY_MIRROR else "")

_reconfig_verifier()

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
        "Read the code below and give it a short English or pinyin word/phrase name; "
        "output only the file name itself, no extension, no quotes, no explanation, "
        "use underscores, at most 30 characters.\n\n"
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
        log(f"[{_ts()}] AI naming failed: {e}")
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
            log(f"[{_ts()}] saved code file: {path}")
        except Exception as e:
            log(f"[{_ts()}] failed to save code file: {e}")
    return saved

def _group_messages(msgs):
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
    out = []
    i = 0
    n = len(msgs)
    while i < n:
        m = msgs[i]
        role = m.get("role")

        if role == "tool":
            i += 1
            continue

        if role == "assistant" and m.get("tool_calls"):
            need_ids = [tc["id"] if isinstance(tc, dict)
                        else getattr(tc, "id", None)
                        for tc in m["tool_calls"]]
            j = i + 1
            got_ids = set()
            tool_msgs = []
            while j < n and msgs[j].get("role") == "tool":
                tool_msgs.append(msgs[j])
                got_ids.add(msgs[j].get("tool_call_id"))
                j += 1
            valid_tools = [t for t in tool_msgs
                           if t.get("tool_call_id") in need_ids]
            if valid_tools:
                out.append(m)
                out.extend(valid_tools)
            i = j
            continue

        out.append(m)
        i += 1

    return out

def _msg_text(m):
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
    total = 0
    for m in msgs:
        text = _msg_text(m)
        cjk = sum(1 for ch in text if "\u4e00" <= ch <= "\u9fff")
        other = len(text) - cjk
        total += int(cjk * 1.5 + other * 0.25) + 4
    return total

def _clip_tool_content(m, limit):
    c = m.get("content")
    if not isinstance(c, str) or len(c) <= limit:
        return m
    head = c[: limit // 2]
    tail = c[-(limit // 2):]
    omitted = len(c) - len(head) - len(tail)
    new = dict(m)
    new["content"] = f"{head}\n...({omitted} chars omitted)...\n{tail}"
    return new

def trim_history(msgs, limit):
    if not msgs:
        return msgs

    sys_msg = msgs[0] if msgs[0].get("role") == "system" else None
    rest = msgs[1:] if sys_msg else msgs

    pinned = None
    if TRIM_KEEP_FIRST_USER:
        for idx, m in enumerate(rest):
            if m.get("role") == "user":
                pinned = rest[idx]
                rest = rest[:idx] + rest[idx + 1:]
                break

    groups = _group_messages(rest)

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

    flat = [_clip_tool_content(m, TRIM_TOOL_CLIP_CHARS)
            if m.get("role") == "tool" else m
            for m in flat]

    if not pinned:
        while flat and flat[0].get("role") != "user":
            flat.pop(0)

    result = ([sys_msg] if sys_msg else []) + ([pinned] if pinned else []) + flat
    return _sanitize_messages(result)

APPROVE_RUN_TOOLS = _env_bool("APPROVE_RUN_TOOLS", True)
APPROVE_SCOPE = (_env_clean("APPROVE_SCOPE", "writes") or "writes").lower()
if APPROVE_SCOPE not in ("all", "writes"):
    APPROVE_SCOPE = "writes"

VERIFY_READONLY_PYTHON = _env_bool("VERIFY_READONLY_PYTHON", True)

READ_ONLY_TOOLS = {"ws_where", "ws_list", "ws_read", "ws_search", "ws_forget"}

APPROVAL_REQUIRED = {"ws_write", "ws_append", "ws_replace", "ws_delete"}
if APPROVE_RUN_TOOLS:
    APPROVAL_REQUIRED |= {"ws_run_cmd", "ws_run_python"}

HIGH_RISK_TOOLS = {"ws_run_cmd", "ws_run_python"}

def _approval_needed(name, args=None):
    args = args or {}
    if name in APPROVAL_REQUIRED:
        return True, ("high-risk exec" if name in HIGH_RISK_TOOLS else "")
    if name == "ws_read":
        if APPROVE_SCOPE == "all":
            return True, "approve_scope=all (read-only asks too)"
        rel = args.get("path", "")
        if workspace.is_sensitive_path(rel):
            return True, workspace.sensitive_reason(rel)
    return False, ""

AUTO_APPROVE_SCOPE = (_env_clean("AUTO_APPROVE_SCOPE", "all") or "all").lower()
if AUTO_APPROVE_SCOPE not in ("none", "writes", "all"):
    AUTO_APPROVE_SCOPE = "all"

NEVER_AUTO_APPROVE = {"ws_delete", "ws_run_cmd", "ws_run_python"}

def _never_auto_approve(name, args=None):
    args = args or {}
    _p = str(args.get("path", "") or "")
    if _p and workspace.is_sensitive_path(_p):
        return True
    if AUTO_APPROVE_SCOPE == "none":
        return True
    if AUTO_APPROVE_SCOPE == "all":
        return False
    return name in NEVER_AUTO_APPROVE

_READONLY_SAFE_MODULES = {
    "os", "sys", "re", "json", "math", "time", "datetime", "collections",
    "itertools", "functools", "hashlib", "ast", "stat", "glob", "fnmatch",
    "difflib", "textwrap", "unicodedata", "decimal", "fractions", "random",
    "csv", "string", "base64", "binascii", "struct", "zlib", "gzip", "io",
    "pathlib", "pprint", "copy", "operator", "typing", "enum", "dataclasses",
    "uuid", "platform", "locale",
}

_READONLY_ROOT_ALLOW = {
    "os": {
        "listdir", "walk", "scandir", "stat", "lstat", "readlink", "getcwd",
        "fspath", "sep", "linesep", "pathsep", "name", "curdir", "pardir",
        "altsep", "extsep", "devnull", "getpid", "getppid", "cpu_count",
        "uname", "strerror", "access",
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
    "shutil": set(), "subprocess": set(), "socket": set(), "requests": set(),
    "urllib": set(), "http": set(), "ftplib": set(), "smtplib": set(),
    "telnetlib": set(), "pty": set(),
}

_READONLY_ALWAYS_BLOCK_ATTRS = {
    "environ", "getenv", "putenv", "setenv",
    "execv", "execve", "execvp", "execl", "execlp", "spawnv", "spawnl",
    "spawnve", "fork", "forkpty", "system", "popen",
}

_READONLY_MUTATING_ATTRS = {
    "write", "writelines", "write_text", "write_bytes", "unlink",
    "rmdir", "removedirs", "rename", "truncate", "mkdir",
    "makedirs", "rmtree", "chmod", "chown", "kill",
    "copy2", "copyfile", "copytree", "save", "dump", "to_csv", "to_excel",
    "to_pickle", "urlopen", "connect", "send", "sendall", "Popen", "run",
    "call", "check_output", "check_call", "chdir", "abort",
}

_READONLY_FILE_ALLOW = {
    "read", "readline", "readlines", "close", "seek", "tell", "readable",
    "writable", "fileno", "closed", "name", "mode", "flush", "encoding",
    "errors", "newlines", "isatty",
}

_READONLY_SECRET_HINTS = (
    ".env", "id_rsa", ".pem", "private_key", "credential", "api_key",
    "apikey", "secret", "password", "passwd", "sk-",
)

_READONLY_MAX_CHARS = 40000

def _root_name(node):
    import ast as _ast
    while isinstance(node, _ast.Attribute):
        node = node.value
    if isinstance(node, _ast.Name):
        return node.id
    if isinstance(node, _ast.Call):
        return _root_name(node.func)
    return ""

def _expr_root_kind(expr, aliases):
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
    import ast as _ast
    if not code or not code.strip():
        return False, ""
    if len(code) > _READONLY_MAX_CHARS:
        return False, ""
    try:
        tree = _ast.parse(code)
    except SyntaxError as e:
        return False, f"{e}"
    except Exception:
        return False, "AST "

    aliases = _collect_aliases(tree)
    bad = []
    for node in _ast.walk(tree):
        if isinstance(node, _ast.Import):
            for a in node.names:
                if a.name.split(".")[0] not in _READONLY_SAFE_MODULES:
                    bad.append("import " + a.name)
        elif isinstance(node, _ast.ImportFrom):
            mod = node.module or ""
            top = mod.split(".")[0]
            if top in _READONLY_ROOT_ALLOW:
                bad.append("from %s import ... (capability root cannot be tracked)" % (mod or "?"))
            elif top not in _READONLY_SAFE_MODULES:
                bad.append("import " + (mod or "?"))
        elif isinstance(node, _ast.Constant) and isinstance(node.value, str):
            low = node.value.lower()
            for h in _READONLY_SECRET_HINTS:
                if h in low:
                    bad.append("literal contains sensitive trace %r" % h)
                    break
        elif isinstance(node, _ast.Call):
            f = node.func
            if isinstance(f, _ast.Name):
                if f.id in ("exec", "eval", "compile", "__import__", "input",
                            "breakpoint", "getattr", "setattr", "delattr",
                            "globals", "locals", "vars"):
                    bad.append("call " + f.id)
                elif f.id == "open":
                    modes = [k.value for k in node.keywords if k.arg == "mode"]
                    if len(node.args) >= 2:
                        modes.append(node.args[1])
                    for mv in modes:
                        ok_mode = (isinstance(mv, _ast.Constant)
                                   and isinstance(mv.value, str)
                                   and not any(c in mv.value for c in "wax+"))
                        if not ok_mode:
                            bad.append("open() mode argument is not a literal read-only mode")
                            break
            elif isinstance(f, _ast.Attribute):
                root = _root_name(f)
                root = aliases.get(root, root)
                if f.attr in _READONLY_ALWAYS_BLOCK_ATTRS:
                    bad.append("call .%s() (environment/process capability)" % f.attr)
                elif root in _READONLY_ROOT_ALLOW:
                    if f.attr not in _READONLY_ROOT_ALLOW[root]:
                        bad.append("call %s.%s()" % (root, f.attr))
                elif root == "__file__":
                    if f.attr not in _READONLY_FILE_ALLOW:
                        bad.append("file object calls .%s()" % f.attr)
                elif f.attr in _READONLY_MUTATING_ATTRS:
                    bad.append("calls mutating method .%s()" % f.attr)
            elif isinstance(f, _ast.Subscript):
                bad.append("subscript call (cannot be determined statically)")
        elif isinstance(node, _ast.Attribute):
            if node.attr in _READONLY_ALWAYS_BLOCK_ATTRS:
                bad.append("access .%s (environment/process capability)" % node.attr)
            elif isinstance(node.ctx, _ast.Store):
                bad.append("assignment to attribute .%s" % node.attr)
        elif isinstance(node, _ast.Subscript) and isinstance(node.ctx, _ast.Store):
            bad.append("subscript assignment (may modify env/global tables)")
        elif isinstance(node, (_ast.Global, _ast.Nonlocal)):
            bad.append("global/nonlocal declaration")

    if bad:
        uniq = []
        for b in bad:
            if b not in uniq:
                uniq.append(b)
        return False, "、".join(uniq[:4])
    return True, ""

AUTO_APPROVE_ENABLED = _env_bool("AUTO_APPROVE_ENABLED", True)
AUTO_APPROVE_DEFAULT = _env_bool("AUTO_APPROVE_DEFAULT", False)
_AUTO_APPROVE_TURN   = False

def _reset_auto_approve(quiet=False):
    global _AUTO_APPROVE_TURN
    was = _AUTO_APPROVE_TURN
    _AUTO_APPROVE_TURN = bool(AUTO_APPROVE_ENABLED and AUTO_APPROVE_DEFAULT)
    if was and not _AUTO_APPROVE_TURN and not quiet:
        print(paint(" 🔒 auto-approve expired; back to per-batch approval",
                    BK, DIM))

def _describe_tool_call(name, args):
    def _prev(s, n=80):
        s = "" if s is None else str(s)
        s = s.replace("\n", "⏎")
        return s if len(s) <= n else s[:n] + "…"

    if name == "ws_read":
        _rp = args.get("path", "")
        _tag = ("  [!] sensitive file (forced approval)"
                if workspace.is_sensitive_path(_rp) else "")
        return f"  read  {_rp}{_tag}"
    if name == "ws_write":
        return (f"  write/overwrite  {args.get('path', '')}"
                f"  ({len(args.get('content', '') or '')} chars) \"{_prev(args.get('content'))}\"")
    if name == "ws_append":
        return (f"  append  {args.get('path', '')}"
                f"  \"{_prev(args.get('content'))}\"")
    if name == "ws_replace":
        return (f"  replace  {args.get('path', '')}"
                f"  \"{_prev(args.get('old'), 40)}\" -> \"{_prev(args.get('new'), 40)}\"")
    if name == "ws_delete":
        return f"  delete  {args.get('path', '')}"
    if name == "ws_run_cmd":
        return (f"  >> run cmd  $ {_prev(args.get('command'), 160)}"
                f"   (cwd={args.get('cwd') or '.'}, timeout={args.get('timeout', 120)}s)")
    if name == "ws_run_python":
        code = args.get("code", "") or ""
        return (f"  >> run python  \"{_prev(code, 120)}\""
                f"   ({len(code)} chars, cwd={args.get('cwd') or '.'}, "
                f"timeout={args.get('timeout', 120)}s)")
    return f"{name}  {args}"

def _request_approval(pending):
    global _AUTO_APPROVE_TURN
    print()
    has_high = any(nm in HIGH_RISK_TOOLS for (nm, _a) in pending)
    print(paint("  [APPROVAL] the following sensitive operations need your approval:", BY, BOLD))
    for i, (name, args) in enumerate(pending, 1):
        color = BR if name in HIGH_RISK_TOOLS else BC
        print(paint(f"     {i}. {_describe_tool_call(name, args)}", color))
    if has_high:
        print(paint("  [!] this batch includes run-command / run-code (highlighted above);", BR, BOLD))
        print(paint("      confirm the consequences and blast radius before approving", BR, BOLD))
    if AUTO_APPROVE_ENABLED:
        if AUTO_APPROVE_SCOPE == "all":
            _sc_hint = ("a or 1 = approve this batch and release ALL remaining ops this round "
                        "(incl. delete/exec; sensitive files excepted) | ")
        elif AUTO_APPROVE_SCOPE == "none":
            _sc_hint = "(one-key auto-approve is off; /set auto_approve_scope writes enables it) | "
        else:
            _sc_hint = ("a or 1 = approve this batch and release remaining content writes this round "
                        "(delete/exec/sensitive files still ask) | ")
        print(paint(" y = this batch" + _sc_hint + "n = reject", BK))
    else:
        print(paint(" y approve / n reject other input = reject", BK))
    _keys = "[y/a/1/N]" if AUTO_APPROVE_ENABLED else "[y/N]"
    print(paint(f" 👉 Approve? {_keys} ", BY, BOLD), end="", flush=True)
    try:
        ans = input("").strip().lower()
    except (EOFError, KeyboardInterrupt):
        print()
        ans = "n"

    approve_all = AUTO_APPROVE_ENABLED and ans in ("a", "1", "all")
    approved = approve_all or ans in ("y", "yes")
    _record_user_event("user approval answer",
        f"{ans or '(empty)'} -> {'approved' if approved else 'rejected'}"
        f" (covering {len(pending)} operation(s))")

    if approved:
        if approve_all:
            _AUTO_APPROVE_TURN = True
            print(paint("  [OK] approved; all remaining sensitive ops this round "
                        "are auto-approved (until your next command)", BG, BOLD))
            log(f"[{_ts()}] user one-key release: {len(pending)} in this batch + all subsequent this round")
        else:
            print(paint(" ✅ approved, running…", BG, BOLD))
            log(f"[{_ts()}] user approved {len(pending)} sensitive operation(s)")
        return True, ""
    print(paint(" 🚫 rejected, all ops cancelled", BR, BOLD))
    log(f"[{_ts()}] user rejected {len(pending)} sensitive operation(s)")
    return False, (
        "The user refused to execute this operation (not approved). "
        "Do not retry the same operation; instead explain what you intend to do "
        "and wait for further instruction."
    )

def _handle_ws(cmd):
    body = cmd[3:].strip()
    if not body:
        print(paint("  [ws] workspace commands:", BC, BOLD))
        print(paint("     /ws ls [path]            list dir", BC))
        print(paint("     /ws read <path>          read file", BC))
        print(paint("     /ws write <path> <text>  write / overwrite", BC))
        print(paint("     /ws append <path> <text> append", BC))
        print(paint("     /ws rm <path>            delete", BC))
        print(paint("     /ws mkdir <path>         make dir", BC))
        print(paint("     /ws search <keyword>     full-text search", BC))
        print(paint("     /ws cd <path>            switch workspace root", BC))
        print(paint("     /ws where                show workspace", BC))
        print(paint("     /ws reset                reset workspace", BC))
        print(paint("     /ws forget [path]        clear read tickets (all or one)", BC))
        print(paint(f"     current workspace: {workspace.get_workspace()}", BY))
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
            print(paint("  [!] usage: /ws write <path> <text>", BR, BOLD))
            return
        ok, out = workspace.ws_write(sp[0], sp[1])
    elif sub == "append":
        sp = rest.split(None, 1)
        if len(sp) < 2:
            print(paint("  [!] usage: /ws append <path> <text>", BR, BOLD))
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
        ok, out = True, f" current workspace{workspace.get_workspace()}"
    elif sub == "reset":
        ok, out = workspace.reset_workspace()
    elif sub == "forget":
        ok, out = workspace.ws_forget(rest.strip())
    else:
        print(paint(f" ⚠️ unknown subcommand{sub} /ws try /ws for help", BR, BOLD))
        return

    color = BG if ok else BR
    print(paint(f"  {'✅' if ok else '⚠️'} {out}", color))

def _last_user_text(msgs):
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
    chunks = []
    for m in msgs[-max_msgs:]:
        if m.get("role") == "system":
            continue
        t = _msg_text(m)
        if t.strip():
            chunks.append(f"[{m.get('role')}] {t}")
    return "\n".join(chunks)[-limit:]

_USER_EVENTS = []
_USER_EVENTS_MAX = 12

def _record_user_event(kind, text):
    t = (text or "").strip()
    if not t:
        return
    _USER_EVENTS.append((kind, t))
    del _USER_EVENTS[:-_USER_EVENTS_MAX]

def _recent_user_events(limit=2000):
    if not _USER_EVENTS:
        return ""
    lines = [f"[{kind}] {text}" for kind, text in _USER_EVENTS]
    return "\n".join(lines)[-limit:]

def _print_search_judge_line(jv):
    if not jv or jv.get("skipped"):
        return
    if jv.get("failed"):
        print(paint("  [net] verify error -> lenient fallback: no web search", BY, BOLD))
        return
    if jv.get("need_search"):
        color, icon, tag = BG, "🔍", "web search needed"
    else:
        color, icon, tag = BK, "⛔", "no web search needed"
    print(paint(f"  {icon} web-need verify -> {tag}"
                f" ({jv.get('confidence', '?')}): {jv.get('reason', '')}", color, BOLD))
    if jv.get("query"):
        print(paint(f"       - rewritten query: {jv['query']}", color, DIM))

def _print_verify_line(res, tag="dual-AI verify"):
    if not res or res.get("skipped"):
        return
    v = res.get("verdict", "?")
    color = {"approve": BG, "supplement": BY, "revise": BR}.get(v, BC)
    icon = verify_tools.VERDICT_ICON.get(v, "❔")
    extra = ""
    if res.get("failed"):
        extra = " (verify service error; handled by the fallback policy)"
    elif res.get("degraded"):
        extra = " (output parsed in degraded mode)"
    print(paint(f"  {icon} {tag} -> {v}{extra}: {res.get('reason', '')}", color, BOLD))
    for d in (res.get("demands") or [])[:5]:
        print(paint(f"       - demand: {d}", color, DIM))
    for d in (res.get("alternatives") or [])[:3]:
        print(paint(f"       - alternative: {d}", color, DIM))

def make_prompt():
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
    print(paint(f"{icon}{tv} You ▸ ", color, BOLD), end="", flush=True)
    return ""

SYSTEM_PROMPT = (
    ""
    ""
    ""
    ""
    " [@] /read "
    ""
    " ``` \n\n"

    "workspace\n"
    " ws_where ws_cd ws_cd_approve "
    "ws_list ws_search ws_read \n"
    " ws_write /ws_append ws_replace "
    "ws_delete ws_mkdir \n"
    " ws_run_cmd ws_run_python Python\n"
    " ws_append / ws_replace / ws_delete ws_read "
    "ws_write "
    " _backup/\n"
    " "
    " ws_cd_approve \n\n"

    " / / \n"
    " 1) ws_write / ws_append / ws_replace / ws_delete "
    "ws_run_cmd / ws_run_python "
    "ws_where / ws_list / ws_search / ws_read / ws_forget ws_mkdir / ws_cd "
    ".env——\n"
    " 2) AI"
    " approve / supplement / revise "
    ""
    ""
    "\n"
    " ★ "
    ""
    " token\n"
    " auto Python "
    "——\n\n"

    " ws_list / ws_search ws_read"
    ""
    ""
    "\n\n"

    " {{}}{{/}} "
    " red/green/yellow/blue/cyan/magenta/white/gray"
    " bold/italic/underline/dim/rainbow"
    " {{green}}{{/green}}{{bold}}{{/bold}}"
    "{{red}}{{/red}}{{rainbow}}{{/rainbow}}"
    "/"
    "\n\n"

    ""
    ""
    ""
    "\n\n"
    "token\n\n"
    " token——"
    ""
    ""
    ""
    "\n\n"

    " DeepSeek /models "
    " /models deepseek-flashdeepseek-v4-prodeepseek-v4-flashdeepseek-v3.2"
    "deepseek-v3.1deepseek-r1 deepseek-chat / deepseek-coder"
    " deepseek-flash"
)

HELP_TEXT = """
 📖 Command Reference
 ─────────────────────────────────────────
 @ read file/dir, one level
 /read... read multiple paths, one level
 /file... /read same as /read
 /open... /read same as /read
 /readr... recursively read whole tree
 /net on|off|auto|ai autoai=AInet mode, ai=AI-judged
 /net show current net mode
 /tavily auto|search|extract|both search/extract tavily mode
 /tavily Tavily show current Tavily mode
 /search force one web search
 /ws workspace commands overview
 /ws ls [] list dir
 /ws read <> read file
 /ws write <> <> / write/overwrite
 /ws append <> <> append
 /ws rm <> delete
 /ws mkdir <> make dir
 /ws search <> full-text search
 /ws cd <> switch workspace root
 /ws where show current workspace
 /ws reset reset workspace
 /ws forget [] clear read tokens
 /clear clear chat history
 /status ///…runtime status
 /set / resetsettings, resettable
 /set <> 
 /set <> <> 
 /set all 
 /set diff 
 /set reset [] =
 /set profile cheap/strict/fast/manual/offline/debug... profile <>... profile <> --reset... profile <> --show 
 /set save.env
 /set approve_scope all|writes writes=
 /set verify_readonly_python on|off Python AI token on
 /help show this help
 /reload reload.env
 /timer on|off / toggle round timer
 /auto on|off|now a / 1 now = auto-approve
 /set auto_approve_scope 
 none = 
 writes = / 
 all = / 
 /model / show main model & base url
 /model <> switch main model this session
 /verify show dual-AI verify status
 /verify on|off|all ()// on/off/all scope
 /verify strict on|off / strict block or pass
 /verify model <> switch verifier model
 /verify answer on|off review final answer
 /verify retries <n> revise set retry limit
 /verify supplements <n> set supplement limit
 /verify fail open|closed / fail-open/closed
 /verify mirror on|off mirror verdict to watcher
 /verify ping connectivity self-test
 exit / quit exit program
 ─────────────────────────────────────────
 wrap paths with spaces in quotes, e.g.
 /read "C:\\my folder\\a.py"
 ─────────────────────────────────────────
 """

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
    print(paint(f" ⚠️ {_boot_err} "
                f"[boot report skipped]", BY, BOLD))

messages = [{"role": "system", "content": _SYS_CONTENT}]

print_banner()

if not API_KEY:
    print(paint(" ⚠️ API keyFATFISH_API_KEY / DEEPSEEK_API_KEY"
                " no main API key found, chat will fail", BR, BOLD))
if not TAVILY_API_KEY:
    print(paint(" ⚠️ TAVILY_API_KEY TAVILY_API_KEY not found, web search unavailable", BY, BOLD))

if _VERIFIER_KEY_BAD:
    print(paint(" ⚠️ VERIFIER_API_KEY.env "
                " key.env", BY, BOLD))

print(paint(f" 🐟 · /help /status "
            f" [ready · /help for commands, /status for runtime info]", BC, DIM))
import settings

def _rebuild_client():
    global client
    client = OpenAI(api_key=API_KEY, base_url=BASE_URL)

def _mk_applier(name, *, rebuild=False, reconfig=False):
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
    base = {"ws_write", "ws_append", "ws_replace", "ws_delete"}
    if v:
        base |= {"ws_run_cmd", "ws_run_python"}
    globals()["APPROVAL_REQUIRED"] = base

def _apply_approve_scope(v):
    globals()["APPROVE_SCOPE"] = v if v in ("all", "writes") else "writes"

def _apply_auto_approve_scope(v):
    globals()["AUTO_APPROVE_SCOPE"] = v if v in ("none", "writes", "all") else "all"

def _apply_net_mode(v):
    net_tools.NET_MODE = v

def _apply_tavily_mode(v):
    net_tools.TAVILY_MODE = v

settings.register("model", MODEL, desc="main model name (executor)", group="model",
                  env_name="FATFISH_MODEL", apply=_mk_applier("MODEL", rebuild=False))
settings.register("max_reply_tokens", MAX_REPLY_TOKENS,
                  desc="max_tokens for a single reply", group="model", type=int,
                  env_name="MAX_REPLY_TOKENS",
                  apply=_mk_applier("MAX_REPLY_TOKENS"))
settings.register("api_timeout", API_TIMEOUT,
                  desc="single API request timeout (seconds)", group="model", type=int,
                  env_name="API_TIMEOUT",
                  apply=_mk_applier("API_TIMEOUT"))

settings.register("verify_mode", VERIFY_MODE, desc="verification scope", group="verify",
                  choices=["off", "auto", "all"], env_name="VERIFY_MODE",
                  apply=_mk_applier("VERIFY_MODE", reconfig=True))
settings.register("verify_strict", VERIFY_STRICT, desc="block when the retry limit is exceeded", group="verify",
                  type=bool, env_name="VERIFY_STRICT",
                  apply=_mk_applier("VERIFY_STRICT", reconfig=True))
settings.register("verify_max_retries", VERIFY_MAX_RETRIES,
                  desc="max send-backs per turn", group="verify", type=int,
                  env_name="VERIFY_MAX_RETRIES",
                  apply=_mk_applier("VERIFY_MAX_RETRIES", reconfig=True))
settings.register("verify_max_supplements", VERIFY_MAX_SUPPLEMENTS,
                  desc="free supplement rounds (not counted as send-backs)", group="verify", type=int,
                  env_name="VERIFY_MAX_SUPPLEMENTS",
                  apply=_mk_applier("VERIFY_MAX_SUPPLEMENTS"))
settings.register("verify_final_answer", VERIFY_FINAL_ANSWER,
                  desc="also review the final answer", group="verify", type=bool,
                  env_name="VERIFY_FINAL_ANSWER", apply=_mk_applier("VERIFY_FINAL_ANSWER"))
settings.register("verify_fail_mode", VERIFY_FAIL_MODE,
                  desc="when the verify service is down", group="verify",
                  choices=["open", "closed"], env_name="VERIFY_FAIL_MODE",
                  apply=_mk_applier("VERIFY_FAIL_MODE", reconfig=True))
settings.register("verify_mirror", VERIFY_MIRROR,
                  desc="mirror review comments to the watcher", group="verify", type=bool,
                  env_name="VERIFY_MIRROR", apply=_apply_verify_mirror)
settings.register("verifier_model", VERIFIER_MODEL,
                  desc="verifier model (ideally different from the main model)", group="verify",
                  env_name="VERIFIER_MODEL",
                  apply=_mk_applier("VERIFIER_MODEL", reconfig=True))

_VL = [
    ("verifier_prompt_chars", "VERIFIER_PROMPT_CHARS", "total submission cap (chars)"),
    ("verifier_plan_chars", "VERIFIER_PLAN_CHARS", "executor statement / plan cap (chars)"),
    ("verifier_action_preview", "VERIFIER_ACTION_PREVIEW", "per-action preview cap (chars)"),
    ("verifier_replace_preview", "VERIFIER_REPLACE_PREVIEW", "replace old/new cap (chars)"),
    ("verifier_goal_chars", "VERIFIER_GOAL_CHARS", "user request cap (chars)"),
    ("verifier_context_chars", "VERIFIER_CONTEXT_CHARS", "recent context cap (chars)"),
    ("verifier_answer_chars", "VERIFIER_ANSWER_CHARS", "answer-under-review cap (chars)"),
    ("verifier_answer_ctx", "VERIFIER_ANSWER_CTX", "answer-review context cap (chars)"),
    ("verifier_max_tokens", "VERIFIER_MAX_TOKENS", "review output cap"),
    ("verifier_timeout", "VERIFIER_TIMEOUT", "single review timeout (seconds)"),
]
for _key, _var, _desc in _VL:
    _default = globals()[_var]
    if _default == 0:
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
    settings.register(_key, _default, desc=_desc, group="verify", type=int,
                      env_name=_var, advanced=True,
                      apply=_mk_applier(_var, reconfig=True))
settings.register("verifier_temperature", VERIFIER_TEMPERATURE,
                  desc="verifier temperature (0-2)", group="verify", type=float,
                  env_name="VERIFIER_TEMPERATURE",
                  advanced=True, apply=_mk_applier("VERIFIER_TEMPERATURE", reconfig=True))

settings.register("net_mode", net_tools.NET_MODE,
                  desc="net mode (ai = AI-judged)", group="network",
                  choices=["on", "off", "auto", "ai"], env_name="NET_MODE",
                  apply=_apply_net_mode)
settings.register("tavily_mode", net_tools.TAVILY_MODE,
                  desc="tavily mode (auto = AI decides)", group="network",
                  choices=["auto", "search", "extract", "both"], env_name="TAVILY_MODE",
                  apply=_apply_tavily_mode)
settings.register("extract_max_len", net_tools.EXTRACT_MAX_LEN,
                  desc="max chars per extracted section", group="network", type=int, advanced=True,
                  env_name="EXTRACT_MAX_LEN",
                  apply=lambda v: setattr(net_tools, "EXTRACT_MAX_LEN", v))

settings.register("approve_scope", APPROVE_SCOPE,
                  desc="approval scope: all = read-only also asks / writes = read-only skips (default; sensitive files still ask)",
                  group="approval", choices=["all", "writes"],
                  env_name="APPROVE_SCOPE", apply=_apply_approve_scope)
settings.register("verify_readonly_python", VERIFY_READONLY_PYTHON,
                  desc="statically read-only Python skips AI verify (manual approval unchanged; saves tokens)",
                  group="approval", type=bool, env_name="VERIFY_READONLY_PYTHON",
                  apply=_mk_applier("VERIFY_READONLY_PYTHON"))
settings.register("approve_run_tools", APPROVE_RUN_TOOLS,
                  desc="run command / run code requires manual approval", group="approval", type=bool,
                  env_name="APPROVE_RUN_TOOLS", apply=_apply_approve_run_tools)
settings.register("auto_approve_enabled", AUTO_APPROVE_ENABLED,
                  desc="allow one-key auto-approve (press a/1)", group="approval", type=bool,
                  env_name="AUTO_APPROVE_ENABLED",
                  apply=_mk_applier("AUTO_APPROVE_ENABLED"))
settings.register("auto_approve_default", AUTO_APPROVE_DEFAULT,
                  desc="released by default each round", group="approval", type=bool,
                  env_name="AUTO_APPROVE_DEFAULT",
                  apply=_mk_applier("AUTO_APPROVE_DEFAULT"))
settings.register("auto_approve_scope", AUTO_APPROVE_SCOPE,
                  desc="auto-approve scope: none / writes / all (default: all except sensitive files, incl. delete/exec)",
                  group="approval", choices=["none", "writes", "all"],
                  env_name="AUTO_APPROVE_SCOPE", apply=_apply_auto_approve_scope)

settings.register("show_timer", SHOW_TIMER, desc="show round cost / linger time", group="ui",
                  type=bool, env_name="SHOW_TIMER", apply=_mk_applier("SHOW_TIMER"))
settings.register("show_wait_anim", SHOW_WAIT_ANIM,
                  desc="show the spinner while waiting (4-frame rotation)", group="ui", type=bool,
                  env_name="SHOW_WAIT_ANIM", apply=_mk_applier("SHOW_WAIT_ANIM"))
settings.register("boot_report", _env_bool("BOOT_REPORT", True),
                  desc="print the boot-report panel at startup (next start)", group="ui",
                  type=bool, env_name="BOOT_REPORT")
settings.register("boot_report_peers", _env_bool("BOOT_REPORT_PEERS", True),
                  desc="boot report scans same-name program copies (next start)", group="ui",
                  type=bool, env_name="BOOT_REPORT_PEERS")

settings.register("max_history", MAX_HISTORY, desc="retained history messages", group="context",
                  type=int, env_name="MAX_HISTORY",
                  apply=_mk_applier("MAX_HISTORY"))
settings.register("max_history_tokens", MAX_HISTORY_TOKENS,
                  desc="token budget for history", group="context", type=int,
                  env_name="MAX_HISTORY_TOKENS",
                  apply=_mk_applier("MAX_HISTORY_TOKENS"))
settings.register("trim_tool_clip_chars", TRIM_TOOL_CLIP_CHARS,
                  desc="truncation length for a single tool result", group="context", type=int,
                  env_name="TRIM_TOOL_CLIP_CHARS",
                  apply=_mk_applier("TRIM_TOOL_CLIP_CHARS"))
settings.register("max_tool_rounds", MAX_TOOL_ROUNDS,
                  desc="tool-call rounds per turn", group="context", type=int,
                  env_name="MAX_TOOL_ROUNDS",
                  apply=_mk_applier("MAX_TOOL_ROUNDS"))

settings.register_profile(
    "cheap", "thrifty mode: compress submissions to cut token use", icon="[C]",
    aliases=["save", "eco"],
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

settings.register_profile(
    "strict", "fortress mode: verify even read-only, stop on failure, no auto-approve", icon="[S]",
    aliases=["safe"],
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

settings.register_profile(
    "fast", "turbo mode: fewer interruptions, no approval prompts, for trusted scenarios", icon="[F]",
    aliases=["speed", "turbo"],
    values={
        "verify_mode": "auto",
        "verify_max_retries": 1,
        "verify_final_answer": False,
        "verify_fail_mode": "open",
        "auto_approve_enabled": True,
        "auto_approve_default": True,
        "show_timer": False,
    })

settings.register_profile(
    "manual", "manual mode: every operation needs human confirmation", icon="[M]",
    aliases=["hand"],
    values={
        "verify_mode": "all",
        "verify_strict": True,
        "verify_fail_mode": "closed",
        "approve_run_tools": True,
        "auto_approve_enabled": False,
        "auto_approve_default": False,
    })

settings.register_profile(
    "offline", "offline mode: no web search, model knowledge only", icon="[O]",
    aliases=["nolink"],
    values={"net_mode": "off"})

settings.register_profile(
    "debug", "debug mode: full verification + mirror + timer, easy to observe", icon="[D]",
    aliases=["diag", "trace"],
    values={
        "verify_mode": "all",
        "verify_mirror": True,
        "show_timer": True,
        "net_mode": "auto",
    })

settings.register_profile(
    "default", "restore the startup factory configuration", icon="[R]",
    aliases=["reset", "base"],
    values={})

log(f"[{_ts()}] === session start === log: {LOG_DIR}")

_CMD_TRANSCRIPT = []
_CMD_MAX_KEEP   = 20
_ANSI_RE        = re.compile(r"\x1b\[[0-9;]*m")

_cmd_state = {"on": False, "buf": [], "cmd": ""}

def _strip_ansi(s):
    return _ANSI_RE.sub("", s or "")

class _TeeStream:

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
    if isinstance(cmd, str) and cmd.startswith("/"):
        _cmd_state["on"] = True
        _cmd_state["buf"] = []
        _cmd_state["cmd"] = cmd

def _cmd_finalize():
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
    _cmd_state["on"] = False
    _cmd_state["buf"] = []
    _cmd_state["cmd"] = ""

def _take_cmd_transcript():
    if not _CMD_TRANSCRIPT:
        return ""
    lines = [""]
    for cmd, out in _CMD_TRANSCRIPT:
        lines.append(f"$ {cmd}")
        if out:
            lines.append(out)
    _CMD_TRANSCRIPT.clear()
    return "\n".join(lines)[:20000]

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
        _record_user_event("", user_input)

        _mark_round_start()

        _reset_auto_approve(quiet=user_input.startswith("/"))

        _cmd_begin(user_input)

        if user_input.lower() in ("exit", "quit", ""):
            _TIMER_EXITING = True
            print(rainbow(" ✨ Bye! See you next time ✨ "))
            log(f"[{_ts()}] === ===")
            break

        if user_input == "/help":
            print(paint(HELP_TEXT, BY))
            continue

        if user_input in ("/status", "/st"):
            print(paint(" 📋 runtime status", BC, BOLD))
            print_startup_status()
            print(paint(" /set ", BC, DIM))
            continue

        if user_input == "/set" or user_input.startswith("/set ") or user_input == "/settings":
            body = "" if user_input == "/settings" else user_input[4:].strip()
            parts = body.split(None, 1)
            sub = parts[0].lower() if parts else ""
            arg = parts[1].strip() if len(parts) > 1 else ""

            if sub == "":
                print(paint(settings.render(show_all=False), BC))
            elif sub == "all":
                print(paint(settings.render(show_all=True), BC))
            elif sub in ("diff", ""):
                print(paint(settings.render_diff(), BY))
            elif sub in ("profile", "profiles", "", ""):
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
                            print(paint("     (effective this run only; use /set save to persist)", BC, DIM))
            elif sub == "save":
                ok, msg = settings.save_to_env()
                print(paint("  💾 " + msg, BG if ok else BR, BOLD))
            elif sub in ("reset", "default", ""):
                ok, msg, changed = settings.reset(arg or None)
                print(paint(("  ♻️  " if ok else "  ⚠️  ") + msg, BG if ok else BR, BOLD))
                if changed:
                    print(paint("     (effective this run only; use /set save to persist,", BC, DIM))
                    print(paint("      or delete the corresponding line in .env)", BC, DIM))
            else:
                key = sub
                if arg == "":
                    if settings.has(key):
                        print(paint(settings.render(key), BC))
                    else:
                        print(paint(f"  [!] unknown setting: {key} (use /set to list all)", BR, BOLD))
                else:
                    ok, msg = settings.set(key, arg)
                    color = BG if ok else BR
                    print(paint(("  ✅ " if ok else "  ⚠️  ") + msg, color, BOLD))
                    if ok:
                        print(paint("     (effective this run only; use /set save to persist)", BC, DIM))
            log(f"[{_ts()}] /set {body}")
            continue

        if user_input.startswith("/timer"):
            arg = user_input[6:].strip().lower()
            if arg in ("on", "off"):
                settings.set("show_timer", arg == "on")
                print(paint(f" ⏱️ {'ON' if SHOW_TIMER else 'OFF'}",
                            BG if SHOW_TIMER else BR, BOLD))
                log(f"[{_ts()}] timer display toggled to {SHOW_TIMER}")
            elif arg == "":
                extra = (f" | since last round {_fmt_secs(time.time() - _LAST_ROUND_END)}"
                         if _LAST_ROUND_END is not None else "")
                print(paint(f" ⏱️ {'ON' if SHOW_TIMER else 'OFF'}{extra}", BC, BOLD))
                print(paint("     /timer on | /timer off", BC))
            else:
                print(paint(f"  [!] unknown argument: {arg} (options: on / off)", BR, BOLD))
            continue

        if user_input.startswith("/auto"):
            arg = user_input[5:].strip().lower()
            if arg in ("on", "off"):
                settings.set("auto_approve_enabled", arg == "on")
                if not AUTO_APPROVE_ENABLED:
                    _AUTO_APPROVE_TURN = False
                print(paint(f" 🔐 {'ON' if AUTO_APPROVE_ENABLED else 'OFF'}",
                            BG if AUTO_APPROVE_ENABLED else BR, BOLD))
                log(f"[{_ts()}] {AUTO_APPROVE_ENABLED}")
            elif arg in ("now", "go"):
                _AUTO_APPROVE_TURN = True
                print(paint(" 🔓 "
                            "", BG, BOLD))
                log(f"[{_ts()}] ")
            elif arg == "":
                print(paint(
                    f" [auto] {'ON' if AUTO_APPROVE_ENABLED else 'OFF'}"
                    f" | this round: {'released' if _AUTO_APPROVE_TURN else 'not released'}"
                    f" | per-round default: {'released' if AUTO_APPROVE_DEFAULT else 'needs approval'}", BC, BOLD))
                print(paint(" /auto on | /auto off /auto now ", BC))
            else:
                print(paint(f"  [!] unknown argument: {arg} (options: on / off / now)", BR, BOLD))
            continue

        if user_input == "/model" or user_input.startswith("/model "):
            arg = user_input[6:].strip()
            if not arg:
                masked = ("" if not API_KEY else
                          (API_KEY[:6] + "…" + API_KEY[-4:]) if len(API_KEY) > 12 else "")
                print(paint(" 🧠 main model ", BC, BOLD))
                print(paint(f" · model {MODEL}", BC))
                print(paint(f" · base_url {BASE_URL}", BC))
                print(paint(f" · api_key {masked}", BC))
                print(paint(" /model <>.env /reload", BC))
            else:
                MODEL = arg
                print(paint(f" 🧠 main model switched{MODEL}"
                            f" {BASE_URL}", BG, BOLD))
                log(f"[{_ts()}] {MODEL}")
            continue

        if user_input.startswith("/verify") or user_input.startswith("/vfy"):
            _sp = user_input.split(None, 1)
            body = _sp[1].strip() if len(_sp) > 1 else ""
            _sp2 = body.split(None, 1)
            sub = _sp2[0].lower() if _sp2 and _sp2[0] else ""
            arg = _sp2[1].strip() if len(_sp2) > 1 else ""

            if sub in ("on", "auto"):
                settings.set("verify_mode", "auto")
                print(paint(" 🧿 auto", BG, BOLD))
            elif sub == "all":
                settings.set("verify_mode", "all")
                print(paint(" 🧿 all", BG, BOLD))
            elif sub == "off":
                settings.set("verify_mode", "off")
                print(paint(" 🧿 verification off", BR, BOLD))
            elif sub == "strict":
                _on = arg.lower() in ("on", "1", "true", "yes")
                settings.set("verify_strict", _on)
                print(paint(f"  [verify] strict mode: {'ON (block when over limit)' if _on else 'OFF (allow over limit)'}", BY, BOLD))
            elif sub == "model":
                if not arg:
                    print(paint(f" 🧿 verifier model{verify_tools.MODEL}", BC, BOLD))
                    print(paint(" /verify model <>", BC))
                else:
                    settings.set("verifier_model", arg)
                    print(paint(f" 🧿 {verify_tools.MODEL}", BG, BOLD))
            elif sub == "answer":
                _on = arg.lower() in ("on", "1", "true", "yes")
                settings.set("verify_final_answer", _on)
                print(paint(f" 🧿 review final answer{'ON' if _on else 'OFF'}", BY, BOLD))
            elif sub == "retries":
                try:
                    _n = max(0, int(arg))
                    settings.set("verify_max_retries", _n)
                    print(paint(f" 🧿 {_n}", BG, BOLD))
                except (TypeError, ValueError):
                    print(paint(f" 🧿 {verify_tools.MAX_RETRIES}/verify retries <n>", BC, BOLD))
            elif sub in ("supplements", "suppl"):
                try:
                    _n = max(0, int(arg))
                    settings.set("verify_max_supplements", _n)
                    print(paint(f" 🧿 {_n}", BG, BOLD))
                except (TypeError, ValueError):
                    print(paint(f" 🧿 {VERIFY_MAX_SUPPLEMENTS}"
                                f"/verify supplements <n>", BC, BOLD))
            elif sub == "fail":
                if arg.lower() in ("open", "closed"):
                    settings.set("verify_fail_mode", arg.lower())
                    print(paint(f" 🧿 [{arg.upper()}]", BY, BOLD))
                else:
                    print(paint(f" 🧿 {verify_tools.FAIL_MODE.upper()}"
                                f" open / closed", BC, BOLD))
            elif sub in ("mirror", "watch"):
                if arg.lower() in ("on", "off"):
                    _on = arg.lower() == "on"
                    settings.set("verify_mirror", _on)
                    print(paint(f" 📡 mirror to watcher"
                                f"{'ON' if _on else 'OFF'}"
                                f"{' → ' + os.path.basename(VERIFY_MIRROR_PATH) if _on else ''}",
                                BG if _on else BR, BOLD))
                    if _on:
                        verify_tools.mirror("📡 mirror enabled")
                else:
                    print(paint(f" 📡 mirror{'ON' if VERIFY_MIRROR else 'OFF'}"
                                f"{' → ' + VERIFY_MIRROR_PATH if VERIFY_MIRROR else ''}"
                                f"/verify mirror on|off", BC, BOLD))
            elif sub in ("ping", "test"):
                print(paint(" 🧿 …", BM, BOLD))
                _r = verify_tools.review(
                    [("ws_write", {"path": "__verify_ping__.txt", "content": "ping"})],
                    user_goal="",
                    ai_plan=" ping ",
                )
                _print_verify_line(_r, tag="")
            elif sub == "":
                print(paint(" 🧿 dual-AI verify status", BC, BOLD))
                print(paint(f"     {verify_tools.mode_label()}", BC))
                print(paint(f" · {'ON' if VERIFY_FINAL_ANSWER else 'OFF'}"
                            f" {_verify_retry}/{VERIFY_MAX_RETRIES}"
                            f" {_verify_suppl}/{VERIFY_MAX_SUPPLEMENTS}", BC))
                print(paint(f" · mirror to watcher"
                            f"{'ON' if VERIFY_MIRROR else 'OFF'}"
                            f"{' → ' + VERIFY_MIRROR_PATH if VERIFY_MIRROR else ''}", BC))
                print(paint(f" · [limits]{verify_tools.limits_label()}", BC))
                print(paint(" /verify on | off | all | strict on|off | model <> | "
                            "answer on|off | retries <n> | supplements <n> | fail open|closed | mirror on|off | ping", BC))
            else:
                print(paint(f" ⚠️ {sub} /verify ", BR, BOLD))
            log(f"[{_ts()}] /verify {body}")
            continue

        if user_input == "/clear":
            _CMD_TRANSCRIPT.clear()
            _USER_EVENTS.clear()
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
            print(paint(" 🧹 Chat history cleared", BY, BOLD))
            log(f"[{_ts()}] ")
            continue

        if user_input == "/reload":
            load_dotenv(override=True)
            new_key    = _env_clean("FATFISH_API_KEY") or _env_clean("DEEPSEEK_API_KEY")
            new_base   = (_env_clean("FATFISH_BASE_URL") or _env_clean("DEEPSEEK_BASE_URL")
                          or "https://api.deepseek.com")
            new_model  = (_env_clean("FATFISH_MODEL") or _env_clean("DEEPSEEK_MODEL")
                          or "deepseek-flash")
            new_tavily = _env_clean("TAVILY_API_KEY")
            if not new_key:
                print(paint(" ⚠️.env API keyFATFISH_API_KEY / DEEPSEEK_API_KEY"
                            " [no API key found in .env]", BR, BOLD))
                continue
            API_KEY, BASE_URL, MODEL = new_key, new_base, new_model
            TAVILY_API_KEY = new_tavily
            net_tools.set_api_key(new_tavily)
            client = OpenAI(api_key=API_KEY, base_url=BASE_URL)
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
            print(paint(" 🔄.env.env reloaded", BG, BOLD))
            print(paint(f" 🧠 main model{MODEL} @ {BASE_URL}", BC))
            print(paint(f" 🧿 verifier{verify_tools.MODEL} @ {verify_tools.BASE_URL}", BC))
            print(paint(f" 📡 mirror{'ON' if VERIFY_MIRROR else 'OFF'}"
                        f"{' → ' + os.path.basename(VERIFY_MIRROR_PATH) if VERIFY_MIRROR else ''}", BC))
            log(f"[{_ts()}].envmodel={MODEL} base_url={BASE_URL}")
            continue

        if user_input.startswith("/net"):
            arg = user_input[4:].strip().lower()
            if arg in ("on", "off", "auto", "ai"):
                settings.set("net_mode", arg)
                color = {"on": BG, "off": BR, "auto": BY, "ai": BC}[arg]
                desc = {"on": "", "off": "",
                        "auto": "", "ai": "AI "}[arg]
                print(paint(f" 🌐 net mode switched to{arg.upper()}"
                            f" —— {desc}", color, BOLD))
                log(f"[{_ts()}] {arg}")
            elif arg == "":
                cur = net_tools.NET_MODE.upper()
                print(paint(f" 🌐 current net mode{cur}", BC, BOLD))
                print(paint(" on = always search", BC))
                print(paint(" off = never search", BC))
                print(paint(" auto = keyword rules", BC))
                print(paint(" ai = 🤖 AI "
                            "AI-judged, recommended", BC))
                print(paint(" usage/net on | /net off | /net auto | /net ai", BC))
            else:
                print(paint(f" ⚠️ unknown arg{arg}"
                            f" options on / off / auto / ai", BR, BOLD))
            continue

        if user_input.startswith("/tavily"):
            arg = user_input[7:].strip().lower()
            if arg in ("search", "extract", "auto", "both"):
                settings.set("tavily_mode", arg)
                color = {"search": BC, "extract": BM, "auto": BY, "both": BG}[arg]
                desc = {"search": "", "extract": "",
                        "auto": " URL ", "both": ""}[arg]
                print(paint(f" 🔍 Tavily mode switched to{arg.upper()}"
                            f" —— {desc}"
                            + ("AI " if arg in ("search", "extract") else ""),
                            color, BOLD))
                log(f"[{_ts()}] Tavily {arg}")
            elif arg == "":
                print(paint(f" 🔍 Tavily current mode{net_tools.TAVILY_MODE.upper()}", BC, BOLD))
                print(paint(" auto = URL auto", BC))
                print(paint(" search = AI force search", BC))
                print(paint(" extract = URLforce extract", BC))
                print(paint(" both = 2 search + extract", BC))
                print(paint(" usage/tavily auto | search | extract | both", BC))
                if net_tools.NET_MODE == "ai":
                    print(paint(" 💡 AI auto AI search/extract", BY, DIM))
            else:
                print(paint(f" ⚠️ unknown arg{arg}"
                            f" options auto / search / extract / both", BR, BOLD))
            continue

        if user_input.startswith("/ws"):
            _handle_ws(user_input)
            continue

        force_search = False
        if user_input.startswith("/search "):
            force_search = True
            user_input = user_input[8:].strip()
            if not user_input:
                print(paint(" ⚠️ /search /search requires a query", BR, BOLD))
                continue

        _cmd_abort()
        _cmd_hist = _take_cmd_transcript()
        parts = [user_input]
        if _cmd_hist:
            parts.insert(0, _cmd_hist)
        log(f"[{_ts()}] {user_input}")

        image_blocks = []
        file_paths, recursive = file_tools.extract_file_refs(user_input)
        if file_paths:
            mode = "" if recursive else ""
            print(paint(f" 📂 detected {len(file_paths)} [{mode}] reading...",
                        BB, ITAL))
            block, ok_list, err_list, image_blocks = file_tools.load_files(
                file_paths, recursive=recursive)
            for p in ok_list:
                print(paint(f" 📄 read{p}", BG))
            for p, err in err_list:
                print(paint(f" ⚠️ read failed {p}{err}", BR, BOLD))
            if ok_list:
                parts.append(block)

        should_search = False
        search_query = None
        search_mode = None
        search_urls = None
        if force_search:
            should_search = True
        elif net_tools.NET_MODE == "on":
            should_search = True
        elif net_tools.NET_MODE == "ai":
            _wspin = _wait_start("")
            try:
                jv = verify_tools.judge_search(user_input, _recent_context(messages))
            except BaseException as _wexc:
                _wait_stop(_wspin, ok=False, label="",
                           note=type(_wexc).__name__)
                raise
            _wait_stop(_wspin, ok=True, label="")
            _print_search_judge_line(jv)
            if jv.get("failed"):
                should_search = net_tools.need_search(user_input)
                if should_search:
                    print(paint(" 🌐 → ", BY, DIM))
            else:
                should_search = bool(jv.get("need_search"))
                search_mode = jv.get("mode") or None
                search_query = jv.get("query") or None
                search_urls = jv.get("urls") or None
        elif net_tools.NET_MODE == "auto":
            should_search = net_tools.need_search(user_input)

        if should_search:
            print(paint(" 🌐 connecting...", BB, ITAL))
            _eff_mode = net_tools.TAVILY_MODE if net_tools.TAVILY_MODE in ("search", "extract") \
                else (search_mode or net_tools.TAVILY_MODE)
            if _eff_mode == "extract":
                _us = search_urls or net_tools.extract_urls(user_input)
                print(paint(f" 📄 =extract {len(_us)} URL", BB, DIM))
            else:
                if search_query and search_query != user_input:
                    print(paint(f" 🔎 =search {search_query}", BB, DIM))
                else:
                    print(paint(f" 🔎 =search", BB, DIM))
            parts.append("\n" + net_tools.do_network(
                user_input, query=search_query, mode=search_mode, urls=search_urls))

        content = file_tools.build_content("\n\n".join(parts), image_blocks)
        messages.append({"role": "user", "content": content})
        messages = trim_history(messages, MAX_HISTORY)

        _verify_retry = 0
        _verify_suppl = 0
        for _round in range(MAX_TOOL_ROUNDS):
            messages = _sanitize_messages(messages)
            _wspin = _wait_start("")
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
                _wait_stop(_wspin, ok=False, label="",
                           note=type(_wexc).__name__)
                raise
            _wait_stop(_wspin, ok=True, label="")
            choice = resp.choices[0]
            msg = choice.message

            if not getattr(msg, "tool_calls", None):
                if choice.finish_reason == "length":
                    print(paint(" ⚠️ max_tokens reply truncated, code may be incomplete", BR, BOLD))
                reply = msg.content or ""

                _vf_ans_free = _verify_suppl < VERIFY_MAX_SUPPLEMENTS
                if VERIFY_FINAL_ANSWER and verify_tools.is_enabled() \
                        and (_verify_retry < VERIFY_MAX_RETRIES or _vf_ans_free):
                    _wspin = _wait_start("")
                    try:
                        vres = verify_tools.review_answer(
                            reply,
                            user_goal=_last_user_text(messages),
                            context_text=_recent_context(messages),
                        )
                    except BaseException as _wexc:
                        _wait_stop(_wspin, ok=False, label="",
                                   note=type(_wexc).__name__)
                        raise
                    _wait_stop(_wspin, ok=True, label="")
                    _print_verify_line(vres, tag="")
                    if vres.get("blocked"):
                        _vf_suppl_mode = ((vres.get("verdict") or "").strip().lower()
                                          == "supplement" and _vf_ans_free)
                        if _vf_suppl_mode:
                            _verify_suppl += 1
                            _vf_ans_tag = (f"/"
                                           f" {_verify_suppl}/{VERIFY_MAX_SUPPLEMENTS}")
                        else:
                            _verify_retry += 1
                            _vf_ans_tag = f" {_verify_retry}/{VERIFY_MAX_RETRIES} "
                        print(paint(f" 🔁 {_vf_ans_tag}", BY, BOLD))
                        log(f"[{_ts()}] {vres.get('reason')}"
                            f"（suppl={_verify_suppl}/{VERIFY_MAX_SUPPLEMENTS}，"
                            f"retry={_verify_retry}/{VERIFY_MAX_RETRIES}）")
                        verify_tools.mirror("-" * 60)
                        verify_tools.mirror(f"[{_ts()}] 🔁 ·{_vf_ans_tag}")
                        verify_tools.mirror(f" {vres.get('reason', '')}")
                        messages.append({"role": "assistant", "content": reply})
                        messages.append({"role": "user",
                                         "content": verify_tools.feedback_text(vres, kind="answer")})
                        continue

                print_ai(reply)
                log(f"[{_ts()}] AI：{strip_markup(reply)}")
                messages.append({"role": "assistant", "content": reply})

                saved_files = save_code_files(reply)
                if saved_files:
                    print(paint(f" 💾 saved {len(saved_files)} code files to {CODE_DIR}/",
                                BG, BOLD))
                    for p in saved_files:
                        print(paint(f"     • {p}", BC))
                break

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

            parsed_calls = []
            for tc in msg.tool_calls:
                name = tc.function.name
                try:
                    args = json.loads(tc.function.arguments or "{}")
                except json.JSONDecodeError:
                    args = {}
                parsed_calls.append((tc, name, args))

            v_picks = verify_tools.pick_actions(parsed_calls)
            if v_picks and VERIFY_READONLY_PYTHON and verify_tools.MODE == "auto":
                _ro_kept = []
                for _pick in v_picks:
                    if _pick[1] == "ws_run_python":
                        _ro_code = (_pick[2] or {}).get("code", "") or ""
                        _is_ro, _ro_why = _python_is_readonly(_ro_code)
                        if _is_ro:
                            log(f"[{_ts()}] Python AI "
                                f"{len(_ro_code)} ")
                            continue
                    _ro_kept.append(_pick)
                v_picks = _ro_kept
            if v_picks:
                print(paint(f" 🧿 verifying {len(v_picks)} "
                            f" verifier{verify_tools.MODEL}…", BM, BOLD))
                _wspin = _wait_start("")
                try:
                    _v_attempt = _verify_retry + _verify_suppl + 1
                    vres = verify_tools.review(
                        [(n, a) for (_i, n, a) in v_picks],
                        user_goal=_last_user_text(messages),
                        ai_plan=msg.content or "",
                        context_text=_recent_context(messages),
                        user_events=_recent_user_events(),
                        extra_note=(
                            f"{workspace.get_workspace()}\n"
                            f" {_v_attempt} "
                            + (""
                               ""
                               if _v_attempt > 1 else "")
                            + ("\n⚠️ " + AUTO_APPROVE_SCOPE + "）："
                               + {"all": "** / / **"
                                         ""
                                         "",
                                  "writes": " / / "
                                            " / ",
                                  "none": "",
                                  }.get(AUTO_APPROVE_SCOPE, "")
                               + " —— "
                                 ""
                                 ""
                               if _AUTO_APPROVE_TURN else "")
                        ),
                    )
                except BaseException as _wexc:
                    _wait_stop(_wspin, ok=False, label="",
                               note=type(_wexc).__name__)
                    raise
                _wait_stop(_wspin, ok=True, label="")
                _print_verify_line(vres)
                if vres.get("blocked"):
                    _vf_vd = (vres.get("verdict") or "").strip().lower()
                    _vf_soft = (_vf_vd == "supplement"
                                and _verify_suppl < VERIFY_MAX_SUPPLEMENTS)
                    if _vf_soft:
                        _verify_suppl += 1
                        _vf_tag = (f""
                                   f" {_verify_suppl}/{VERIFY_MAX_SUPPLEMENTS}")
                    elif _verify_retry < VERIFY_MAX_RETRIES:
                        _verify_retry += 1
                        _vf_tag = (f"/"
                                   f" {_verify_retry}/{VERIFY_MAX_RETRIES} ")
                    else:
                        _vf_tag = ""
                    if _vf_tag:
                        blocked_idx = {i for (i, _n, _a) in v_picks}
                        fb = verify_tools.feedback_text(vres)
                        for i, (tc, name, args) in enumerate(parsed_calls):
                            content = fb if i in blocked_idx else (
                                ""
                                "")
                            messages.append({"role": "tool",
                                             "tool_call_id": tc.id,
                                             "content": content})
                        print(paint(f" 🔁 {_vf_tag}sent back for revision", BY, BOLD))
                        log(f"[{_ts()}] {vres.get('reason')}"
                            f"（suppl={_verify_suppl}/{VERIFY_MAX_SUPPLEMENTS}，"
                            f"retry={_verify_retry}/{VERIFY_MAX_RETRIES}）")
                        verify_tools.mirror("-" * 60)
                        verify_tools.mirror(f"[{_ts()}] 🔁 ·"
                                            f"（{_vf_tag}）")
                        verify_tools.mirror(f" {vres.get('reason', '')}")
                        verify_tools.mirror(" → ")
                        continue
                    if VERIFY_STRICT:
                        fb = verify_tools.feedback_text(vres, final=True)
                        for tc, name, args in parsed_calls:
                            messages.append({"role": "tool",
                                             "tool_call_id": tc.id,
                                             "content": fb})
                        print(paint(" 🛑 "
                                    "[verification retry limit reached — blocked]", BR, BOLD))
                        log(f"[{_ts()}] {vres.get('reason')}")
                        verify_tools.mirror("-" * 60)
                        verify_tools.mirror(f"[{_ts()}] 🛑 → "
                                            f"")
                        verify_tools.mirror(f" {vres.get('reason', '')}")
                        break
                    print(paint(" ⚠️ "
                                "[retry limit reached — proceeding leniently]", BY, BOLD))
                    log(f"[{_ts()}] {vres.get('reason')}")
                    verify_tools.mirror("-" * 60)
                    verify_tools.mirror(f"[{_ts()}] ⚠️ → "
                                        f"")
                    verify_tools.mirror(f" {vres.get('reason', '')}")

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
                    print(paint(f" 🔓 {len(_auto_i)} "
                                f"[auto-approved: {len(_auto_i)} write op(s)]", BB, DIM))
                    log(f"[{_ts()}] {len(_auto_i)} "
                        f"（scope={AUTO_APPROVE_SCOPE}）：{_auto_names}")
                if _must:
                    if _AUTO_APPROVE_TURN:
                        print(paint(f" 🛡 {len(_must)} "
                                    f" / / "
                                    f" [never auto-approved: {len(_must)}]", BR, BOLD))
                    _ok2, approval_reply = _request_approval(
                        [(n, a) for (_i, n, a) in _must])
                    if not _ok2:
                        _deny = {i for (i, _n, _a) in _must}

            for _idx, (tc, name, args) in enumerate(parsed_calls):
                if _idx in _deny:
                    ok, result = False, approval_reply
                else:
                    ok, result = workspace.call_tool(name, args)
                icon = "✅" if ok else "⚠️"
                color = BG if ok else BR
                first = result.splitlines()[0][:100] if result else ""
                print(paint(f" {icon} workspace {name} → {first}", color))
                log(f"[{_ts()}] TOOL {name}({args}) -> {result}")
                messages.append({
                    "role": "tool",
                    "tool_call_id": tc.id,
                    "content": result,
                })
        else:
            print(paint(" ⚠️ tool-call round limit reached, stopped", BY, BOLD))

    except KeyboardInterrupt:
        _TIMER_EXITING = True
        print("\n" + rainbow(" ✨ Exited, see you ✨ "))
        log(f"[{_ts()}] === ===")
        break
    except Exception as e:
        print(paint(f" ❌ error{e}", BR, BOLD))
    finally:
        _cmd_finalize()
        _mark_round_end()