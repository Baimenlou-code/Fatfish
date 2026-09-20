
import os
import re
import sys
import json
import time
import atexit
import logging
import traceback
from datetime import datetime

API_KEY     = ""
BASE_URL    = "https://api.deepseek.com"
MODEL       = "deepseek-chat"
MODE        = "auto"
STRICT      = True
MAX_RETRIES = 2
FAIL_MODE   = "open"
LOG_DIR     = ""
TIMEOUT     = 120
TEMPERATURE = 0.2
MAX_TOKENS  = 2000

PROMPT_MAX_CHARS     = 64000
ACTION_PREVIEW       = 4000
REPLACE_PREVIEW      = 2000
GOAL_MAX_CHARS       = 6000
PLAN_MAX_CHARS       = 16000
CONTEXT_MAX_CHARS    = 24000
ANSWER_MAX_CHARS     = 24000
ANSWER_CTX_CHARS     = 12000

_client = None
_client_sig = None
_MOCK = None
_JSON_MODE_OK = True

SIDE_EFFECT_TOOLS = {
    "ws_write", "ws_append", "ws_replace", "ws_delete", "ws_mkdir",
    "ws_cd", "ws_cd_approve", "ws_run_cmd", "ws_run_python",
}
READ_ONLY_TOOLS = {
    "ws_where", "ws_list", "ws_read", "ws_search", "ws_forget",
}

VALID_VERDICTS = ("approve", "supplement", "revise")

def _ts():
    return datetime.now().strftime("%Y-%m-%d %H:%M:%S")

def _log(msg):
    try:
        logging.info(msg)
    except Exception:
        pass
    if LOG_DIR:
        try:
            os.makedirs(LOG_DIR, exist_ok=True)
            with open(os.path.join(LOG_DIR, "verify.log"), "a", encoding="utf-8") as f:
                f.write(f"[{_ts()}] {msg}\n")
        except OSError:
            pass

def _clip(s, n):
    s = "" if s is None else str(s)
    s = s.replace("\r\n", "\n")
    return s if len(s) <= n else s[:n] + f"...(truncated; original length {len(s)})"

MIRROR_PATH = ""
MIRROR_ON   = True
_mirror_fh  = None

def set_mirror(path=None, enabled=None):
    global MIRROR_PATH, MIRROR_ON, _mirror_fh
    if enabled is not None:
        MIRROR_ON = bool(enabled)
    if path is not None and path != MIRROR_PATH:
        MIRROR_PATH = path
        if _mirror_fh is not None:
            try:
                _mirror_fh.close()
            except Exception:
                pass
            _mirror_fh = None
    return MIRROR_PATH

def close_mirror():
    global _mirror_fh
    if _mirror_fh is not None:
        try:
            _mirror_fh.flush()
            _mirror_fh.close()
        except Exception:
            pass
        _mirror_fh = None

atexit.register(close_mirror)

def mirror(text):
    global _mirror_fh
    if not (MIRROR_ON and MIRROR_PATH):
        return
    try:
        if _mirror_fh is None:
            d = os.path.dirname(MIRROR_PATH)
            if d:
                os.makedirs(d, exist_ok=True)
            _mirror_fh = open(MIRROR_PATH, "a", encoding="utf-8", errors="replace")
        for ln in (str(text).splitlines() or [""]):
            _mirror_fh.write(ln + "\n")
        _mirror_fh.flush()
    except OSError:
        pass

def _mirror_block(title, lines=None, icon="[V] "):
    mirror("-" * 60)
    mirror(f"[{_ts()}] {icon}{title}".rstrip())
    for ln in (lines or []):
        mirror("    " + str(ln))

def _action_oneline(name, args):
    args = args or {}
    if name == "ws_run_cmd":
        return f"{name} -> {_clip(args.get('command', ''), 90)}"
    if name == "ws_run_python":
        return f"{name} -> {_clip(args.get('code', ''), 90)}"
    if name == "ws_write":
        return (f"{name} -> {args.get('path', '')} "
                f"({len(args.get('content', '') or '')} chars)")
    if name == "ws_append":
        return (f"{name} -> {args.get('path', '')} "
                f"(append {len(args.get('content', '') or '')} chars)")
    if name == "ws_replace":
        return (f"{name} -> {args.get('path', '')} "
                f"'{_clip(args.get('old', ''), 30)}' -> '{_clip(args.get('new', ''), 30)}'")
    if name in ("ws_delete", "ws_mkdir", "ws_cd", "ws_read"):
        return f"{name} -> {args.get('path', '')}"
    if name == "ws_search":
        return f"{name} -> {args.get('keyword', '')}"
    try:
        return f"{name} -> {_clip(json.dumps(args, ensure_ascii=False), 90)}"
    except (TypeError, ValueError):
        return f"{name} -> {_clip(str(args), 90)}"

def _result_lines(res, actions=None):
    out = []
    v = res.get("verdict", "?")
    tag = {"approve": "[OK] approve",
           "supplement": "[!] supplement needed",
           "revise": "[X] revise needed"}.get(v, str(v))
    if res.get("failed"):
        tag = "[!] verify service failed"
    elif res.get("degraded") and v == "approve":
        tag += " (parse degraded)"
    head = f"verdict: {tag}  severity={res.get('severity', '?')}"
    if res.get("elapsed") is not None:
        head += f"  elapsed={res['elapsed']}s"
    out.append(head)

    if actions:
        out.append(f"actions submitted ({len(actions)}):")
        for i, (n, a) in enumerate(actions, 1):
            out.append(f"  {i}) {_action_oneline(n, a)}")

    if res.get("reason"):
        out.append("reason: " + res["reason"])
    for r in (res.get("risks") or []):
        out.append("risk: " + str(r))
    for d in (res.get("demands") or []):
        out.append("demand: " + str(d))
    for a in (res.get("alternatives") or []):
        out.append("alternative: " + str(a))
    return out

def configure(api_key="", base_url="", model="", mode=None, strict=None,
              max_retries=None, fail_mode=None, log_dir=None,
              mirror_path=None, mirror_on=None,
              max_tokens=None, temperature=None, timeout=None,
              prompt_max_chars=None, action_preview=None, replace_preview=None,
              goal_max_chars=None, plan_max_chars=None, context_max_chars=None,
              answer_max_chars=None, answer_ctx_chars=None):
    global API_KEY, BASE_URL, MODEL, MODE, STRICT, MAX_RETRIES, FAIL_MODE, LOG_DIR
    global MAX_TOKENS, TEMPERATURE, TIMEOUT
    global PROMPT_MAX_CHARS, ACTION_PREVIEW, REPLACE_PREVIEW
    global GOAL_MAX_CHARS, PLAN_MAX_CHARS, CONTEXT_MAX_CHARS
    global ANSWER_MAX_CHARS, ANSWER_CTX_CHARS
    global _client, _client_sig
    if api_key:
        API_KEY = api_key.strip()
    if base_url:
        BASE_URL = base_url.strip()
    if model:
        MODEL = model.strip()
    if mode is not None:
        MODE = str(mode).strip().lower()
    if strict is not None:
        STRICT = bool(strict)
    if max_retries is not None:
        MAX_RETRIES = max(0, int(max_retries))
    if fail_mode is not None:
        FAIL_MODE = "closed" if str(fail_mode).strip().lower() == "closed" else "open"
    if log_dir is not None:
        LOG_DIR = log_dir
    if mirror_path is not None or mirror_on is not None:
        set_mirror(mirror_path, mirror_on)
    def _pos(v, cur, floor=200):
        try:
            n = int(v)
        except (TypeError, ValueError):
            return cur
        return n if n >= floor else cur

    if max_tokens is not None:
        MAX_TOKENS = _pos(max_tokens, MAX_TOKENS, 100)
    if temperature is not None:
        try:
            TEMPERATURE = max(0.0, min(2.0, float(temperature)))
        except (TypeError, ValueError):
            pass
    if timeout is not None:
        TIMEOUT = _pos(timeout, TIMEOUT, 10)
    if prompt_max_chars is not None:
        PROMPT_MAX_CHARS = _pos(prompt_max_chars, PROMPT_MAX_CHARS, 2000)
    if action_preview is not None:
        ACTION_PREVIEW = _pos(action_preview, ACTION_PREVIEW)
    if replace_preview is not None:
        REPLACE_PREVIEW = _pos(replace_preview, REPLACE_PREVIEW)
    if goal_max_chars is not None:
        GOAL_MAX_CHARS = _pos(goal_max_chars, GOAL_MAX_CHARS)
    if plan_max_chars is not None:
        PLAN_MAX_CHARS = _pos(plan_max_chars, PLAN_MAX_CHARS)
    if context_max_chars is not None:
        CONTEXT_MAX_CHARS = _pos(context_max_chars, CONTEXT_MAX_CHARS)
    if answer_max_chars is not None:
        ANSWER_MAX_CHARS = _pos(answer_max_chars, ANSWER_MAX_CHARS)
    if answer_ctx_chars is not None:
        ANSWER_CTX_CHARS = _pos(answer_ctx_chars, ANSWER_CTX_CHARS)
    _client = None
    _client_sig = None

def set_mode(mode):
    global MODE
    MODE = str(mode).strip().lower()
    return MODE

def set_strict(flag):
    global STRICT
    STRICT = bool(flag)
    return STRICT

def set_model(model):
    global MODEL, _client, _client_sig
    MODEL = (model or "").strip() or MODEL
    _client = None
    _client_sig = None
    return MODEL

def set_fail_mode(mode):
    global FAIL_MODE
    FAIL_MODE = "closed" if str(mode).strip().lower() == "closed" else "open"
    return FAIL_MODE

def is_enabled():
    return MODE in ("auto", "all")

def is_off():
    return not is_enabled()

def should_verify(tool_name):
    if MODE == "all":
        return True
    if MODE == "auto":
        return tool_name in SIDE_EFFECT_TOOLS
    return False

def pick_actions(parsed_calls):
    if not is_enabled():
        return []
    out = []
    for i, item in enumerate(parsed_calls):
        try:
            name = item[1]
            args = item[2]
        except (IndexError, TypeError):
            continue
        if should_verify(name):
            out.append((i, name, args))
    return out

def limits_label():
    return (f"total {PROMPT_MAX_CHARS} | plan {PLAN_MAX_CHARS} | goal {GOAL_MAX_CHARS} | "
            f"action {ACTION_PREVIEW} | context {CONTEXT_MAX_CHARS} | out {MAX_TOKENS} tok")

def mode_label():
    if not is_enabled():
        return "OFF (/verify on to enable)"
    scope = "ALL (incl. read-only)" if MODE == "all" else "AUTO (side-effect actions only)"
    return (f"ON | {scope} | verifier {MODEL} | "
            f"{'strict block' if STRICT else 'lenient allow'} | "
            f"max retries {MAX_RETRIES} | on failure "
            f"{'block' if FAIL_MODE == 'closed' else 'allow'}")

VERIFIER_SYSTEM = (
    "You are the second AI in 'dual-AI verify' mode -- the independent Verifier AI.\n"
    "The first AI (the executor) is operating a real workspace (it can read and write files, "
    "run commands, and run code) and hands you the 'list of actions it is about to perform' for "
    "review. Yours is an **independent second opinion** -- even after passing, the user still "
    "confirms each batch in the terminal, so you need not cover every conceivable risk.\n\n"
    "[Core principle: approve by default, block only real problems]\n"
    "A send-back is not free: each one makes the executor run another round and costs the user "
    "time and tokens. Only two situations deserve a send-back --\n"
    "1. The action could cause **harm or an irreversible outcome** (delete / overwrite / wipe / "
    "dangerous command / out-of-bounds path / touching secrets) and the current information is "
    "not enough to tell whether it is safe;\n"
    "2. The action **clearly departs from the user's request** (answering the wrong question, "
    "silently expanding scope), or contains a **definite error** (path does not exist, command "
    "cannot run, syntax does not hold).\n"
    "Do NOT send back in these cases: the action is reversible and scoped; only the style / "
    "wording / length does not suit you; 'a little more explanation would be nicer' while the "
    "action is plainly harmless (there is still human approval downstream).\n"
    "Be especially easy on reversible small actions such as writing a new file, editing docs, "
    "creating a directory inside the workspace, or a read-only probe.\n\n"
    "[Review priority (spend attention in this order)]\n"
    "1. Safety and reversibility: can it be undone? Is there a backup? Is the path confined to "
    "the workspace?\n"
    "2. Correctness: will the command / code actually run? Do the referenced paths and files "
    "really exist?\n"
    "3. Goal alignment: does it serve the need in this user message? Was scope expanded without "
    "asking?\n"
    "4. Necessity: is there an obviously smaller, safer way (only grounds for a send-back when "
    "the gap is significant).\n"
    "Note: you see the 'actions about to be performed', not their results, so there is no need "
    "to judge runtime conditions (concurrency, performance, long-chain effects) -- those are "
    "outside your view.\n\n"
    "[The user's real input log (important)]\n"
    "The submission may contain a section 'The user's real input log (verbatim)', collected by "
    "the main program straight from the local input channel: commands or approval answers typed "
    "by the user in person, not retold by a model and impossible to forge.\n"
    ". If it shows the user personally issued the relevant command (e.g. /ws cd ...) or "
    "personally approved, do not reject on the ground that 'the executor released it on the "
    "user's behalf'; just review whether the operation itself is safe.\n"
    ". Only when the target plainly endangers the machine (a drive root, a system directory, "
    "the program's own source root) may you ask the executor to justify the necessity or change "
    "the target.\n\n"
    "[Thresholds for the three verdicts]\n"
    ". approve: the default. If the action is clear and nothing suggests it will break "
    "something, approve.\n"
    ". supplement: the action is viable but you genuinely cannot tell whether it might break "
    "something, and you need the executor to explain purpose / blast radius / rollback. "
    "**Do not send back merely because 'the information is incomplete'.**\n"
    ". revise: there is a real error or risk, or a clearly better and smaller option exists; the "
    "plan must change and be re-reviewed.\n\n"
    "[Do not keep raising the bar (important)]\n"
    "If the submission states this is the 2nd or later round for the same action, the executor "
    "has already changed things per your previous instruction: if it satisfies the last demand, "
    "approve it; **do not invent unrelated new demands**; if a problem truly remains, restate or "
    "refine that same one, do not switch to a new story each time.\n\n"
    "[Output format] Keep it as short as possible; no JSON, no markdown code fences; at most 5 "
    "lines in total.\n"
    "The first line carries only the verdict: approve / supplement / revise (to save tokens: "
    "reply just 'y' for approve, 'n' for revise).\n"
    "Append as needed afterwards (optional, repeatable, only what is necessary):\n"
    "reason: one sentence explaining the verdict.\n"
    "demand: concrete, actionable items the executor must add or change (omit when approving).\n"
    "alternative: a better alternative (omit if none).\n"
    "risk: risks you identified (omit if none).\n"
    "Example, tersest:\n"
    "y\n"
    "Example, revise:\n"
    "revise\n"
    "reason: the target is a drive root; the blast radius is far too large.\n"
    "demand: use a specific data subdirectory instead.\n"
    "risk: system files could be modified by mistake.\n"
    "Tip: demands and alternatives must be concrete and actionable, no filler; when the action "
    "really is fine, just answer y."
)

ANSWER_SYSTEM = (
    "You are the second AI in 'dual-AI verify' mode -- the independent Verifier AI.\n"
    "The executor AI is about to deliver the final answer to a user request; review it.\n"
    "Yours is an **independent second opinion**, not the last word; a send-back makes the "
    "executor run another round and costs the user time and tokens, so block only real problems.\n\n"
    "[Should send back]\n"
    "1. It does not actually answer the user's question (off topic, equivocation, a key point "
    "missed);\n"
    "2. A key fact or the code is wrong, with obvious hallucination or self-contradiction;\n"
    "3. It hides a failure, an unfinished item, or an uncertainty that should have been stated.\n"
    "[Should not send back]\n"
    "Style, length, layout, wording preferences; 'more explanation would be nicer' when it does "
    "not affect delivery; the user did not ask for a word-by-word proofread. Do not oppose for "
    "the sake of opposing.\n\n"
    "[Three verdicts] approve (ready to deliver) / supplement (key information missing) / "
    "revise (the conclusion or plan is wrong and must be redone).\n"
    "[Output format] As short as possible; no JSON, no markdown; at most 4 lines in total.\n"
    "The first line carries only the verdict: approve / supplement / revise (to save tokens: "
    "reply just 'y' for approve, 'n' for revise).\n"
    "Append as needed afterwards (optional, repeatable):\n"
    "reason: one sentence.\n"
    "demand: concrete items to add or change (omit when approving).\n"
)

def describe_action(name, args):
    args = args or {}

    def prev(key, n=ACTION_PREVIEW):
        return _clip(args.get(key, ""), n)

    if name == "ws_write":
        body = prev("content")
        return (f"Write/overwrite file [write]: {args.get('path', '')}\n"
                f"    content (first {ACTION_PREVIEW} chars):\n{_indent(body)}")
    if name == "ws_append":
        return (f"Append content to file [append]: {args.get('path', '')}\n"
                f"    appended content:\n{_indent(prev('content'))}")
    if name == "ws_replace":
        return (f"Exact replacement in file [replace]: {args.get('path', '')}\n"
                f"    old (text to replace, first {REPLACE_PREVIEW} chars):\n"
                f"{_indent(_clip(args.get('old', ''), REPLACE_PREVIEW))}\n"
                f"    new (replacement, first {REPLACE_PREVIEW} chars):\n"
                f"{_indent(_clip(args.get('new', ''), REPLACE_PREVIEW))}")
    if name == "ws_delete":
        return f"Delete file/empty dir [delete]: {args.get('path', '')}"
    if name == "ws_mkdir":
        return f"Create directory [mkdir]: {args.get('path', '')}"
    if name == "ws_cd":
        return f"Switch workspace root [cd]: {args.get('path', '')}"
    if name == "ws_cd_approve":
        return "Approve and execute a workspace switch that moves outside the default sandbox [cd approve]"
    if name == "ws_run_cmd":
        return (f"Run command [run cmd] (cwd={args.get('cwd', '')}, "
                f"timeout={args.get('timeout', 120)}):\n{_indent(_clip(args.get('command', ''), ACTION_PREVIEW))}")
    if name == "ws_run_python":
        return (f"Run Python code [run python] (cwd={args.get('cwd', '')}, "
                f"timeout={args.get('timeout', 120)}):\n{_indent(_clip(args.get('code', ''), ACTION_PREVIEW))}")
    try:
        dump = json.dumps(args, ensure_ascii=False)
    except (TypeError, ValueError):
        dump = str(args)
    return f"{name}: {_clip(dump, ACTION_PREVIEW)}"

def _indent(text, pad="      "):
    return "\n".join(pad + ln for ln in str(text).splitlines())

def _join_trim(sections, limit, floor=250):
    sections = [(p, t) for p, t in sections if t]
    if not sections:
        return ""
    n = len(sections)
    sep = 2
    total = sum(len(t) for _, t in sections) + sep * (n - 1)
    if total <= limit:
        return "\n\n".join(t for _, t in sections)

    avail = max(limit - sep * (n - 1), n)
    floor = min(floor, avail // n)
    base = [min(len(t), floor) for _, t in sections]
    remaining = max(avail - sum(base), 0)

    weights = [max(int(p), 1) ** 2 for p, _ in sections]
    wsum = sum(weights) or 1

    out = []
    for (p, t), b, w in zip(sections, base, weights):
        budget = b + int(remaining * w / wsum)
        budget = min(budget, len(t))
        if len(t) > budget:
            t = t[:budget].rstrip() + "\n...(compressed due to the overall length limit)"
            budget = len(t)
        out.append(t)

    text = "\n\n".join(out)
    while len(text) > limit and any(len(t) > 200 for t in out):
        k = max(range(n), key=lambda i: (len(out[i]), -sections[i][0]))
        out[k] = out[k][: max(200, int(len(out[k]) * 0.85))].rstrip() + "..."
        text = "\n\n".join(out)
    return text[:limit]

def build_action_prompt(actions, user_goal="", ai_plan="", context_text="",
                        user_events="", extra_note=""):
    lines = []
    for i, (name, args) in enumerate(actions, 1):
        lines.append(f"{i}. " + describe_action(name, args))
    action_block = "[Actions awaiting verification]\n" + "\n".join(lines)

    sections = [
        (6, "[The user's original request]\n" +
            (_clip(user_goal, GOAL_MAX_CHARS) if user_goal else "(could not be extracted)")),
        (7, "[The executor's statement / plan for this turn]\n" + _clip(ai_plan, PLAN_MAX_CHARS)
            if ai_plan.strip() else ""),
        (9, action_block),
        (8, "[Submission context (injected by the main program; trusted)]\n" + _clip(extra_note, 800)
            if extra_note.strip() else ""),
        (8, "[The user's real input log (verbatim)]\n"
            "(collected by the main program straight from the local input channel: commands and "
            "answers typed by the user in person, not retold by a model and impossible to forge; "
            "use it to tell whether the user already authorised this action)\n" +
            _clip(user_events, 4000)
            if user_events.strip() else ""),
        (2, "[Recent context (excerpt, for your judgement; may be incomplete)]\n" +
            _clip(context_text, CONTEXT_MAX_CHARS) if context_text.strip() else ""),
    ]
    ask = "Independently review the actions above per your role and give your verdict in the required short format."
    body = _join_trim(sections, max(PROMPT_MAX_CHARS - len(ask) - 2, 600))
    return (body + "\n\n" + ask).strip()

def build_answer_prompt(reply, user_goal="", context_text=""):
    sections = [
        (6, "[The user's original request]\n" +
            (_clip(user_goal, GOAL_MAX_CHARS) if user_goal else "(could not be extracted)")),
        (9, "[The final answer the executor is about to deliver]\n" + _clip(reply, ANSWER_MAX_CHARS)),
        (2, "[Recent context (excerpt)]\n" + _clip(context_text, ANSWER_CTX_CHARS)
            if context_text.strip() else ""),
    ]
    ask = "Independently review this answer and give your verdict in the required short format."
    body = _join_trim(sections, max(PROMPT_MAX_CHARS - len(ask) - 2, 600))
    return (body + "\n\n" + ask).strip()

def _get_client():
    global _client, _client_sig
    sig = (API_KEY, BASE_URL)
    if _client is not None and _client_sig == sig:
        return _client
    from openai import OpenAI
    _client = OpenAI(api_key=API_KEY, base_url=BASE_URL)
    _client_sig = sig
    return _client

def _raw_chat(messages):
    client = _get_client()
    resp = client.chat.completions.create(
        model=MODEL,
        messages=messages,
        temperature=TEMPERATURE,
        max_tokens=MAX_TOKENS,
        timeout=TIMEOUT,
    )
    return (resp.choices[0].message.content or "").strip()

def _chat(messages):
    if _MOCK is not None:
        return _MOCK(messages)
    return _raw_chat(messages)

def _balanced_json(text):
    if not text:
        return None
    start = text.find("{")
    if start < 0:
        return None
    depth = 0
    in_str = False
    esc = False
    for i in range(start, len(text)):
        ch = text[i]
        if in_str:
            if esc:
                esc = False
            elif ch == "\\":
                esc = True
            elif ch == '"':
                in_str = False
            continue
        if ch == '"':
            in_str = True
        elif ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
            if depth == 0:
                return text[start:i + 1]
    return None

def _loose_json_load(blob):
    if not blob:
        return None
    try:
        return json.loads(blob)
    except json.JSONDecodeError:
        pass
    out, in_str, esc = [], False, False
    for ch in blob:
        if in_str:
            if esc:
                out.append(ch); esc = False; continue
            if ch == "\\":
                out.append(ch); esc = True; continue
            if ch == '"':
                out.append(ch); in_str = False; continue
            if ch == "\n":
                out.append("\\n"); continue
            if ch == "\r":
                out.append("\\r"); continue
            if ch == "\t":
                out.append("\\t"); continue
            out.append(ch); continue
        out.append(ch)
        if ch == '"':
            in_str = True
    try:
        return json.loads("".join(out))
    except json.JSONDecodeError:
        return None

_VERDICT_WORDS = (
    ("approve", "approve"), ("approved", "approve"), ("pass", "approve"), ("ok", "approve"),
    ("supplement", "supplement"), ("clarify", "supplement"), ("need more", "supplement"),
    ("revise", "revise"), ("reject", "revise"), ("block", "revise"),
)
_EN_VERDICT_WORDS = {
    "approve": "approve", "supplement": "supplement", "revise": "revise",
    "ok": "approve", "pass": "approve", "yes": "approve",
    "reject": "revise", "no": "revise",
}

def _clean_head(s):
    return re.sub(r'[\s:：,，.。;；!！?？"\'`*#\-\[\]()（）]+', "", (s or "").strip().lower())

def _match_verdict_word(line):
    core = _clean_head(line)
    if not core:
        return ""
    if core in _EN_VERDICT_WORDS:
        return _EN_VERDICT_WORDS[core]
    for w, v in _VERDICT_WORDS:
        if core.startswith(w):
            return v
    return ""

def _grab_labeled(lines, label):
    out = []
    for ln in lines:
        for sep in (":", "："):
            if ln.startswith(label + sep):
                v = ln[len(label) + len(sep):].strip()
                if v:
                    out.append(v)
                break
    return out

def _parse_verdict_text(raw):
    lines = [ln.strip() for ln in (raw or "").splitlines() if ln.strip()]
    if not lines:
        return None

    verdict = ""
    brief = ""
    _head = _clean_head(lines[0]) if lines else ""
    if _head in ("y", "yes"):
        verdict, brief = "approve", "y"
    elif _head in ("n", "no"):
        verdict, brief = "revise", "n"
    if not verdict:
        for ln in lines[:3]:
            verdict = _match_verdict_word(ln)
            if verdict:
                break
    if not verdict:
        for ln in lines:
            verdict = _match_verdict_word(ln)
            if verdict:
                break
    if not verdict:
        return None

    reason = " ".join(_grab_labeled(lines, "reason"))[:500]
    demands = _grab_labeled(lines, "demand") + _grab_labeled(lines, "demands")
    alternatives = _grab_labeled(lines, "alternative") + _grab_labeled(lines, "alternatives")
    risks = _grab_labeled(lines, "risk") + _grab_labeled(lines, "risks")

    low = raw.lower()
    if ("high risk" in low) or ("severe" in low) or ("critical" in low):
        severity = "high"
    elif ("low risk" in low) or ("low" in low):
        severity = "low"
    else:
        severity = "low" if verdict == "approve" else "medium"
    if not reason:
        reason = (f"(verifier answered the terse '{brief}' with no reason)" if brief
                  else "(verifier gave a short verdict with no reason)")

    return {
        "verdict": verdict, "severity": severity, "reason": reason,
        "demands": demands, "alternatives": alternatives, "risks": risks,
        "degraded": False, "raw": raw,
    }

def parse_verdict(text):
    raw = text or ""

    obj = None
    blob = _balanced_json(raw)
    if blob:
        obj = _loose_json_load(blob)
    if isinstance(obj, dict):
        return _verdict_from_obj(obj, raw)

    m = re.search(r'verdict"?\s*:\s*"?(approve|supplement|revise)', raw, re.I)
    if m:
        return {
            "verdict": m.group(1).lower(), "severity": "medium",
            "reason": "(verifier output was not strict JSON; parsed in degraded mode)",
            "demands": [], "alternatives": [], "risks": [],
            "degraded": True, "raw": raw,
        }

    txt = _parse_verdict_text(raw)
    if txt:
        return txt

    return {
        "verdict": "supplement",
        "severity": "medium",
        "reason": "the verifier's reply could not be parsed; conservatively asking for clarification.",
        "demands": ["Explain the purpose, blast radius, and rollback plan of this action."],
        "alternatives": [], "risks": [],
        "degraded": True, "raw": raw,
    }

def _verdict_from_obj(obj, raw):
    verdict = str(obj.get("verdict", "")).strip().lower()
    if verdict not in VALID_VERDICTS:
        verdict = "supplement"

    def _as_list(v):
        if not v:
            return []
        if isinstance(v, str):
            return [v]
        if isinstance(v, (list, tuple)):
            return [str(x) for x in v if str(x).strip()]
        return [str(v)]

    return {
        "verdict": verdict,
        "severity": str(obj.get("severity", "medium")).strip().lower() or "medium",
        "reason": str(obj.get("reason", "")).strip(),
        "demands": _as_list(obj.get("demands")),
        "alternatives": _as_list(obj.get("alternatives")),
        "risks": _as_list(obj.get("risks")),
        "degraded": False,
        "raw": raw,
    }

def _fail_result(err, what="action"):
    if FAIL_MODE == "closed":
        return {
            "ok": False, "blocked": True, "verdict": "supplement",
            "severity": "medium",
            "reason": f"verify service unavailable ({err}); blocked per fail-closed policy, please retry later.",
            "demands": [f"{what} did not pass independent verification: the review service is temporarily unavailable; retry later or confirm manually."],
            "alternatives": [], "risks": [], "degraded": True, "failed": True, "raw": "",
        }
    return {
        "ok": False, "blocked": False, "verdict": "approve",
        "severity": "low",
        "reason": f"verify service unavailable ({err}); allowed per fail-open policy.",
        "demands": [], "alternatives": [], "risks": [],
        "degraded": True, "failed": True, "raw": "",
    }

def _run_review(system_prompt, user_prompt):
    _mock = (os.getenv("FATFISH_VERIFY_MOCK") or "").strip().lower()
    if _mock in VALID_VERDICTS:
        res = {
            "ok": True, "verdict": _mock,
            "severity": "low" if _mock == "approve" else "high",
            "reason": f"[test stub] forced verdict {_mock}",
            "demands": [] if _mock == "approve" else [f"[test stub] explain and re-submit ({_mock})"],
            "alternatives": ["[test stub] explore read-only first"] if _mock != "approve" else [],
            "risks": ["[test stub] fake risk"] if _mock == "revise" else [],
            "degraded": False, "failed": False, "raw": "",
            "blocked": _mock != "approve",
        }
        _log(f"[{_ts()}] review (test stub FATFISH_VERIFY_MOCK={_mock}): blocked={res['blocked']}")
        return res

    if _MOCK is None and not API_KEY:
        return _fail_result("verifier API key is not configured")

    messages = [
        {"role": "system", "content": system_prompt},
        {"role": "user", "content": user_prompt},
    ]
    t0 = time.time()
    try:
        text = _chat(messages)
    except Exception as e:
        tb = traceback.format_exc()
        _log(f"[{_ts()}] review call failed: {type(e).__name__}: {e}\n{tb}")
        if os.getenv("FATFISH_VERIFY_DEBUG"):
            print("[verify_tools] review call failed:\n" + tb, file=sys.stderr)
        return _fail_result(f"{type(e).__name__}: {e}")

    cost = time.time() - t0
    res = parse_verdict(text)
    res["ok"] = True
    res["blocked"] = res["verdict"] != "approve"
    res["elapsed"] = round(cost, 2)
    res.setdefault("failed", False)
    if res.get("degraded"):
        try:
            _log(f"[{_ts()}] review parsed in degraded mode; verifier raw output ({len(text)} chars): "
                 f"{_clip(str(text).replace(chr(10), ' | '), 800)}")
        except Exception:
            pass
    _log(f"[{_ts()}] review done {res['verdict']}({res['severity']}) "
         f"{cost:.2f}s: {res['reason']}")
    return res

def review(actions, user_goal="", ai_plan="", context_text="", user_events="",
           extra_note=""):
    actions = [a for a in (actions or []) if a]
    if not actions or not is_enabled():
        return {"ok": True, "blocked": False, "verdict": "approve",
                "reason": "(no verification needed)", "demands": [], "alternatives": [],
                "risks": [], "degraded": False, "failed": False, "raw": "",
                "skipped": True}

    _mirror_block(f"dual-AI verify - submitting {len(actions)} action(s) "
                  f"(verifier {MODEL})", None, icon="[V] ")
    prompt = build_action_prompt(actions, user_goal, ai_plan, context_text,
                                 user_events, extra_note)
    res = _run_review(VERIFIER_SYSTEM, prompt)
    _mirror_block("dual-AI verify - review comments", _result_lines(res, actions))
    return res

def review_answer(reply, user_goal="", context_text=""):
    if not (reply or "").strip() or not is_enabled():
        return {"ok": True, "blocked": False, "verdict": "approve",
                "reason": "(no verification needed)", "demands": [], "alternatives": [],
                "risks": [], "degraded": False, "failed": False, "raw": "",
                "skipped": True}
    _mirror_block("dual-AI verify - reviewing the final answer", None, icon="[V] ")
    prompt = build_answer_prompt(reply, user_goal, context_text)
    res = _run_review(ANSWER_SYSTEM, prompt)
    _mirror_block("dual-AI verify - answer review comments", _result_lines(res, None))
    return res

SEARCH_JUDGE_SYSTEM = (
    "You are the 'web-need verifier' -- before the AI assistant decides whether to search "
    "online, you independently judge whether this user message truly needs external information "
    "to be answered correctly.\n\n"
    "[Judge as need_search=true]\n"
    "1. Real-time / time-sensitive data is needed: weather, stock prices, exchange rates, "
    "scores, traffic, ticket prices, the latest news, version releases, prices;\n"
    "2. Facts that change over time or may be outdated: specific numbers, disputed facts, "
    "industry developments, policy changes;\n"
    "3. Factual questions you are unsure about and cannot answer with confidence;\n"
    "4. The user explicitly asks: search / look it up / check for me / go online / latest news.\n\n"
    "[Judge as need_search=false]\n"
    "1. Purely theoretical / academic questions: math derivations, algorithms, code logic, "
    "writing, translation, general knowledge;\n"
    "2. The question already carries full context: the user provided a document / code / "
    "material and only wants an analysis of it;\n"
    "3. Questions about the system's own state: the current config, the workspace path, whether "
    "a feature can be toggled, this program's behavior, information already in the conversation;\n"
    "4. Greetings, small talk, emotional expression, asking for guidance on what to do next;\n"
    "5. The answer is already available in the current conversation or in workspace files;\n"
    "6. **Follow-ups on a topic just searched** (unless the user asks for the latest, or a new "
    "keyword).\n\n"
    "[When need_search=true, also choose the retrieval mode]\n"
    '- "extract": used when the body of a SPECIFIC web page must be read. Signal: the user '
    "message contains a concrete URL / link and the intent is to understand that page (e.g. "
    "'see what this article says', 'summarise this link', 'what does this document say').\n"
    '- "search": used when the WHOLE WEB must be queried by topic. Signal: the user describes a '
    "topic / question without naming a specific page (e.g. 'what is the latest policy', 'the "
    "price of X', 'any news').\n"
    "Decision points:\n"
    "  . A link present -> probably extract; but if the intent is to USE the link as a lead to "
    "search for more like it, still use search.\n"
    "  . No link -> always search (extract cannot run without a URL).\n"
    "  . Unsure -> use search (more general, safer).\n\n"
    "[Output format] No JSON, no markdown. Answer in the fewest words; prefer the tersest "
    "shorthand (token-saving):\n"
    "Line 1 carries a single word: y=needs a web search / n=no search needed / extract=must "
    "read the body of a given page.\n"
    "* No search needed: reply with just the letter n; no reason, no extra words at all.\n"
    "* Search needed: reply y, then a line 'query: ...' with the rewritten search terms "
    "(concise, no colloquialisms or pronouns, keep the key entities).\n"
    "* Must read a given page body: reply 'extract', then a line 'url: ...'.\n"
    "Unless there is a real risk worth flagging, do not write a 'reason' line.\n"
    "Example 1 (no search needed -- just one letter):\n"
    "n\n"
    "Example 2 (search needed -- y plus a rewritten query):\n"
    "y\n"
    "query: bitcoin latest price\n"
    "Example 3 (must read a given page body):\n"
    "extract\n"
    "url: https://example.com/article\n"
    "Bias: prefer searching less over pulling a pile of irrelevant content for pure local / "
    "theoretical / self-state questions; but when the user **explicitly** asks to search, you "
    "must search -- do not under-search out of caution."
)

def _parse_search_text(raw):
    lines = [ln.strip() for ln in (raw or "").splitlines() if ln.strip()]
    if not lines:
        return None

    def _kind(s):
        t = _clean_head(s)
        if t.startswith("nosearch") or t in ("false", "no", "n", "none", "skip", "offline"):
            return (False, "search")
        if t.startswith("search") or t.startswith("websearch") or t in ("true", "yes", "y", "need"):
            return (True, "search")
        if t.startswith("extract") or t.startswith("fetch") or t.startswith("readpage"):
            return (True, "extract")
        return None

    need, mode = None, "search"
    for ln in lines[:3]:
        k = _kind(ln)
        if k:
            need, mode = k
            break
    if need is None:
        for ln in lines:
            k = _kind(ln)
            if k:
                need, mode = k
                break
    if need is None:
        return None

    reason = " ".join(_grab_labeled(lines, "reason"))[:500]
    query = " ".join(_grab_labeled(lines, "query") + _grab_labeled(lines, "keywords")
                     + _grab_labeled(lines, "search terms"))[:300]
    urls = []
    for ln in lines:
        for sep in (":", "："):
            if (ln.startswith("url" + sep) or ln.startswith("link" + sep)):
                urls += re.findall(r'https?://[^\s<>"\')\]\u3001\u3002\uff0c]+', ln)
                break
    urls = [u.rstrip(".,;:!?)]") for u in urls]
    urls = [u for u in urls if u][:10]
    if mode == "extract" and not urls:
        urls = _guess_urls(raw)
    if mode == "extract" and not urls:
        mode = "search"
    if not reason:
        reason = "(short decision with no reason)"
    return {"need_search": need, "mode": mode, "reason": reason,
            "query": query, "urls": urls, "confidence": "medium",
            "degraded": False, "failed": False, "raw": raw}

def parse_search_verdict(text):
    raw = text or ""
    obj = None
    blob = _balanced_json(raw)
    if blob:
        obj = _loose_json_load(blob)

    def _as_list(v):
        if not v:
            return []
        if isinstance(v, str):
            return [v.strip()] if v.strip() else []
        if isinstance(v, (list, tuple)):
            return [str(x).strip() for x in v if str(x).strip()]
        return [str(v).strip()]

    def _as_bool(v):
        if isinstance(v, bool):
            return v
        if isinstance(v, str):
            return v.strip().lower() in ("true", "1", "yes", "y")
        return bool(v)

    if not isinstance(obj, dict):
        m = re.search(r'need_search"?\s*:\s*(true|false)', raw, re.I)
        if m:
            return {"need_search": m.group(1).lower() == "true",
                    "mode": "search", "reason": "(not strict JSON; parsed in degraded mode)",
                    "query": "", "urls": [],
                    "confidence": "low", "degraded": True, "failed": False, "raw": raw}
        txt = _parse_search_text(raw)
        if txt:
            return txt
        return {"need_search": False,
                "mode": "search",
                "reason": "output could not be parsed; conservatively judging that no search is needed",
                "query": "", "urls": [],
                "confidence": "low",
                "degraded": True, "failed": False, "raw": raw}

    mode = str(obj.get("mode", "")).strip().lower()
    if mode not in ("search", "extract"):
        mode = "extract" if _as_list(obj.get("urls")) else "search"

    return {
        "need_search": _as_bool(obj.get("need_search")),
        "mode": mode,
        "reason": str(obj.get("reason", "")).strip(),
        "query": str(obj.get("query", "") or "").strip(),
        "urls": _as_list(obj.get("urls")),
        "confidence": str(obj.get("confidence", "medium")).strip().lower() or "medium",
        "degraded": False, "failed": False, "raw": raw,
    }

def judge_search(user_input, context_text=""):
    if not (user_input or "").strip():
        return {"need_search": False, "reason": "(empty input)", "query": "",
                "confidence": "high", "degraded": False, "failed": False, "skipped": True}

    prompt_parts = ["[The user's latest message]\n" + _clip(user_input, 4000)]
    if (context_text or "").strip():
        prompt_parts.append("[Recent conversation context (excerpt, for judgement; may be incomplete)]\n"
                            + _clip(context_text, 4000))
    prompt_parts.append("Decide whether a web search is needed and answer in the required short format.")
    user_prompt = _clip("\n\n".join(prompt_parts), PROMPT_MAX_CHARS)

    _mirror_block("web-need verify - submitting", [f"user message: {_clip(user_input, 120)}"], icon="[W] ")

    _mock = (os.getenv("FATFISH_JUDGE_MOCK") or "").strip().lower()
    if _mock in ("true", "yes", "1", "search", "on"):
        res = {"need_search": True, "mode": "search", "reason": "[test stub] forced to need the web (search)",
               "query": _clip(user_input, 60), "urls": [], "confidence": "high",
               "degraded": False, "failed": False, "skipped": False}
        _mirror_block("web-need verify - decision", ["need_search=True, mode=search (test stub)"], icon="[W] ")
        return res
    if _mock in ("extract", "e"):
        res = {"need_search": True, "mode": "extract", "reason": "[test stub] forced to need the web (extract)",
               "query": "", "urls": [u for u in _guess_urls(user_input)],
               "confidence": "high",
               "degraded": False, "failed": False, "skipped": False}
        _mirror_block("web-need verify - decision", ["need_search=True, mode=extract (test stub)"], icon="[W] ")
        return res
    if _mock in ("false", "no", "0", "skip", "off"):
        res = {"need_search": False, "mode": "search", "reason": "[test stub] forced to need no web",
               "query": "", "urls": [], "confidence": "high",
               "degraded": False, "failed": False, "skipped": False}
        _mirror_block("web-need verify - decision", ["need_search=False (test stub)"], icon="[W] ")
        return res

    if _MOCK is None and not API_KEY:
        return {"need_search": False, "mode": "search", "reason": "verify key is not configured",
                "query": "", "urls": [], "confidence": "low",
                "degraded": False, "failed": True, "skipped": False}

    messages = [
        {"role": "system", "content": SEARCH_JUDGE_SYSTEM},
        {"role": "user", "content": user_prompt},
    ]
    t0 = time.time()
    try:
        text = _chat(messages)
    except Exception as e:
        tb = traceback.format_exc()
        _log(f"[{_ts()}] web-need verify failed: {type(e).__name__}: {e}\n{tb}")
        if os.getenv("FATFISH_VERIFY_DEBUG"):
            print("[verify_tools] web-need verify failed:\n" + tb, file=sys.stderr)
        res = {"need_search": False, "mode": "search",
               "reason": f"verify failed: {type(e).__name__}",
               "query": "", "urls": [], "confidence": "low",
               "degraded": False, "failed": True, "skipped": False}
        _mirror_block("web-need verify - error", [res["reason"]], icon="[!] ")
        return res

    res = parse_search_verdict(text)
    res["elapsed"] = round(time.time() - t0, 2)
    res["skipped"] = False
    if res.get("need_search") and res.get("mode") == "extract":
        if not res.get("urls"):
            res["urls"] = _guess_urls(user_input)
        if not res["urls"]:
            res["mode"] = "search"
            res["_mode_fallback"] = "extract mode but no URL was obtained; falling back to search"
            if not res.get("query"):
                res["query"] = _clip(user_input, 80)
    line = (f"need_search={res['need_search']}  mode={res['mode']}  "
            f"confidence={res['confidence']}  elapsed={res['elapsed']}s")
    _log(f"[{_ts()}] web-need verify: {line} | {res['reason']}")
    extra = [line, f"reason: {res['reason']}"]
    if res.get("query"):
        extra.append(f"rewritten query: {res['query']}")
    if res.get("urls"):
        extra.append("URLs to fetch: " + " , ".join(res["urls"][:5]))
    if res.get("_mode_fallback"):
        extra.append("[!] " + res["_mode_fallback"])
    _mirror_block("web-need verify - decision", extra, icon="[W] ")
    return res

def _guess_urls(text):
    if not text:
        return []
    seen, out = set(), []
    for u in re.findall(r'https?://[^\s<>"\')\]]+', text):
        u = u.rstrip(".,;:!?)]")
        if u and u not in seen:
            seen.add(u)
            out.append(u)
    return out[:10]

def search_judge_label(res):
    if not res:
        return "(no decision)"
    if res.get("skipped"):
        return "(skipped)"
    if res.get("failed"):
        return "[!] verify error; falling back to the keyword rule"
    if not res.get("need_search"):
        return f"[no] no web needed ({res.get('confidence', '?')}) {res.get('reason', '')}"
    mode = res.get("mode", "search")
    icon = "[page]" if mode == "extract" else "[search]"
    detail = ""
    if mode == "extract":
        urls = res.get("urls") or []
        detail = f" | fetch {len(urls)} URL(s)" + (
            f": {urls[0]}" if urls else " (no URL; will fall back to search)")
    elif res.get("query"):
        detail = f" | query: {res['query']}"
    return (f"{icon} web needed - {mode}"
            f" ({res.get('confidence', '?')}) {res.get('reason', '')}{detail}")

VERDICT_ICON = {"approve": "[OK]", "supplement": "[!]", "revise": "[X]"}

def summary_line(res):
    if not res:
        return "(no review result)"
    if res.get("skipped"):
        return "(verification skipped)"
    v = res.get("verdict", "?")
    icon = VERDICT_ICON.get(v, "[?]")
    tag = "verify service failed" if res.get("failed") else f"verifier verdict: {v}"
    reason = _clip(res.get("reason", ""), 120)
    return f"{icon} {tag} | {reason}"

def feedback_text(res, kind="action", final=False):
    v = res.get("verdict", "supplement")
    label = {"approve": "approved", "supplement": "needs clarification", "revise": "needs a new plan"}.get(v, v)
    lines = [f"[Dual-AI verify - failed | verifier verdict: {v} ({label})]"]
    if res.get("reason"):
        lines.append(f"reason: {res['reason']}")
    if res.get("risks"):
        lines.append("risks identified by the verifier:")
        lines += [f"  - {x}" for x in res["risks"]]
    if res.get("demands"):
        lines.append("what you (the executor) must add or change:")
        lines += [f"  {i}) {x}" for i, x in enumerate(res["demands"], 1)]
    else:
        lines.append("explain the purpose, blast radius, and rollback plan of this operation.")
    if res.get("alternatives"):
        lines.append("alternatives suggested by the verifier:")
        lines += [f"  - {x}" for x in res["alternatives"]]

    if kind == "answer":
        lines.append("=> Clarify or rewrite the answer accordingly, then deliver it again; do not repeat the previous answer verbatim.")
    else:
        lines.append("=> Clarify or change the plan first, then re-submit for verification; "
                     "do not retry the rejected action unchanged.")
    if final:
        lines.append("[!] The verification retry limit is exhausted; this action is blocked, "
                     "so explain the situation to the user and wait for further instruction.")
    lines.append("(This is the opinion of the independent second AI reviewer, for your self-correction.)")
    return "\n".join(lines)

def _selftest():
    global _MOCK, MODE, API_KEY, FAIL_MODE, STRICT
    import io
    sys.stdout.reconfigure(encoding="utf-8", errors="replace") if hasattr(sys.stdout, "reconfigure") else None
    fails = []

    def check(name, cond):
        print(("  [OK] " if cond else "  [XX] ") + name)
        if not cond:
            fails.append(name)

    print("=" * 66)
    print("verify_tools self-test start")
    print("=" * 66)

    r = parse_verdict('```json\n{"verdict":"revise","reason":"out of bounds","demands":["change path"]}\n```')
    check("parse JSON wrapped in markdown", r["verdict"] == "revise" and r["demands"] == ["change path"])

    r = parse_verdict('OK, my conclusion is {"verdict": "approve", "reason": "ok"} done.')
    check("parse JSON amid chatter", r["verdict"] == "approve")

    r = parse_verdict('verdict: "supplement" please clarify')
    check("keyword fallback when not JSON", r["verdict"] == "supplement" and r["degraded"])

    r = parse_verdict('nonsense with no structure')
    check("unparseable -> conservative supplement", r["verdict"] == "supplement")

    MODE, API_KEY, FAIL_MODE, STRICT = "auto", "sk-test", "open", True
    _MOCK = lambda msgs: json.dumps({
        "verdict": "revise", "severity": "high",
        "reason": "overwriting the whole file is risky",
        "demands": ["use ws_replace for a local edit", "state where the original is backed up"],
        "alternatives": ["ws_read first, then ws_replace"],
        "risks": ["a full overwrite is irreversible"],
    })
    res = review([("ws_write", {"path": "FATENFISH.py", "content": "x" * 50}),
                  ("ws_read", {"path": "a.txt"})],
                 user_goal="help me fix a bug", ai_plan="I plan to rewrite it wholesale")
    check("review returns blocked", res["blocked"] and res["verdict"] == "revise")
    fb = feedback_text(res)
    check("feedback contains demands", "ws_replace" in fb and "Dual-AI verify" in fb)
    check("summary_line readable", "revise" in summary_line(res))

    parsed = [("tc1", "ws_read", {"path": "a"}), ("tc2", "ws_write", {"path": "b"})]
    picks = pick_actions(parsed)
    check("pick_actions(auto) picks only ws_write", len(picks) == 1 and picks[0][1] == "ws_write")
    MODE = "all"
    check("pick_actions(all) picks all", len(pick_actions(parsed)) == 2)
    MODE = "off"
    check("pick_actions(off) picks none", len(pick_actions(parsed)) == 0)

    MODE = "auto"

    def boom(_msgs):
        raise RuntimeError("network down")

    _MOCK = boom
    FAIL_MODE = "open"
    r = review([("ws_write", {"path": "a", "content": "b"})])
    check("fail-open: allowed and marked failed", r["blocked"] is False and r["failed"])
    FAIL_MODE = "closed"
    r = review([("ws_write", {"path": "a", "content": "b"})])
    check("fail-closed: blocked and marked failed", r["blocked"] is True and r["failed"])

    _MOCK = lambda msgs: '{"verdict":"supplement","reason":"did not answer the core question","demands":["add the conclusion"]}'
    r = review_answer("Just some filler.", user_goal="How do I do A?")
    check("review_answer can send back", r["blocked"] and "add the conclusion" in feedback_text(r, kind="answer"))

    for nm, ar in [("ws_write", {"path": "p", "content": "c"}),
                   ("ws_run_cmd", {"command": "dir"}),
                   ("ws_run_python", {"code": "print(1)"}),
                   ("ws_replace", {"path": "p", "old": "o", "new": "n"})]:
        check(f"describe_action({nm}) non-empty",
              nm in describe_action(nm, ar) or ":" in describe_action(nm, ar))

    _MOCK = None
    print("=" * 66)
    if fails:
        print(f"self-test failed on {len(fails)} item(s): " + "; ".join(fails))
        return 1
    print("self-test all passed")
    return 0

if __name__ == "__main__":
    sys.exit(_selftest())
