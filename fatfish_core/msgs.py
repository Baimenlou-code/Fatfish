# -*- coding: utf-8 -*-
"""fatfish_core.msgs —— 消息流水线

从 FATHFISHI.py 拆出：消息分组 / 清洗 / token 估算 / 裁剪 / 代码块抽取。
★ save_code_files 与 ai_name_code 留在主程序：它们要调模型客户端（client）
  与 MODEL，属需要外部副作用的一次调用，故不搬入本模块。
外部配置由主程序启动时注入（见下方 CONFIG 段）。"""

from .envutil import _env_int
import re

# ============ 外部配置 ============
# 由 FATHFISHI.py 启动时注入真值；此处默认值只保证「单独 import 也不炸」。
MAX_HISTORY_TOKENS = _env_int("MAX_HISTORY_TOKENS", 800000)
TRIM_KEEP_FIRST_USER = True
TRIM_TOOL_CLIP_CHARS = _env_int("TRIM_TOOL_CLIP_CHARS", 40000)

# ============ 本模块自有状态（随拆分一起搬入）============
CODE_BLOCK_RE = re.compile(r"```[ \t]*(\w+)?[ \t]*\n(.*?)```", re.DOTALL)
UNCLOSED_BLOCK_RE = re.compile(r"```[ \t]*(\w+)?[ \t]*\n(.*)\Z", re.DOTALL)
LANG_EXT = {'python': 'py', 'py': 'py', 'javascript': 'js', 'js': 'js', 'typescript': 'ts', 'ts': 'ts', 'bash': 'sh', 'shell': 'sh', 'sh': 'sh', 'html': 'html', 'css': 'css', 'json': 'json', 'yaml': 'yaml', 'yml': 'yml', 'java': 'java', 'c': 'c', 'cpp': 'cpp', 'c++': 'cpp', 'go': 'go', 'rust': 'rs', 'sql': 'sql'}

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
