# -*- coding: utf-8 -*-
"""
verify_tools.py —— 双人核验引擎（第二位 AI 审查员）

设计动机
--------
主程序（肥鱼 = 执行者）在决定「写 / 改 / 删文件、切换工作台、执行命令、跑代码」
这类有副作用的动作之前，先请第二位 AI（审查员）过目。审查员拥有独立的人格与
独立的 system prompt（可选用不同模型，形成「异源核验」），只能给出三种裁决：

    approve    —— 放行：动作合理且安全，可以执行。
    supplement —— 补充说明：动作主体可行，但信息不足 / 风险未交代，
                  要求执行者说明目的、影响范围、回滚方案、依据后再执行。
    revise     —— 变更方案：动作存在错误、风险，或存在更优更小的方案，
                  必须换方案后重新送审。

为省 token，审查员可用「极简简写」输出（解析器同样认）：
    y  → approve（通过，无附加内容）    n  → revise（不通过，需变更方案）

未通过时，本模块把审查意见拼成一段「回填文本」，由主程序塞进 role="tool" 的
消息里打回给执行者，迫使它补充说明或改方案后「重新提交核验」，而不是原样重试。

与主程序的关系
--------------
- 纯模块，不接触工作台文件系统，只做「读待核验动作 → 出审查意见」。
- 复用 openai SDK，支持任意 OpenAI 兼容 base_url（可换成别家模型做真异源）。
- 任何网络 / 服务异常都不吞掉主流程：按 FAIL_MODE 决定「放行」还是「拦截」。
- 所有核验过程落盘到 logs/.../verify.log，便于事后审计。

快速自检
--------
    python verify_tools.py            # 跑内置自检（用假模型，不联网）
"""

import os
import re
import sys
import json
import time
import atexit
import logging
import traceback
from datetime import datetime

# ============ 模块状态 / 可调参数 ============
API_KEY     = ""
BASE_URL    = "https://api.deepseek.com"
MODEL       = "deepseek-chat"
MODE        = "auto"           # off / auto / all
STRICT      = True             # True=未通过且超重试上限则拦截；False=放行
MAX_RETRIES = 2                # 同一轮内允许「打回重交」的次数
FAIL_MODE   = "open"           # 核验服务不可用时：open=放行 / closed=拦截
LOG_DIR     = ""               # 由主程序注入（logs/YYYY/MM/DD）
TIMEOUT     = 120              # 单次审查请求超时（秒）
TEMPERATURE = 0.2              # 审查员要冷静
MAX_TOKENS  = 2000             # 审查意见输出上限（原 1200，输入变多后可给出更细的要求）

# ---- 业务 AI → 复核 AI：送审「方案」的长度上限（已整体扩充）----
#   注意：以下单位都是「字符」而非 token。中文约 1 字符 ≈ 1.5 token。
PROMPT_MAX_CHARS     = 64000   # 送审内容总上限（原 16000）
ACTION_PREVIEW       = 4000    # 单个动作参数预览上限（原 800）
REPLACE_PREVIEW      = 2000    # ws_replace 的 old / new 各自上限（原 400）
GOAL_MAX_CHARS       = 6000    # 用户原始需求上限（原 2000）
PLAN_MAX_CHARS       = 16000   # 执行者自述 / 计划上限（原 2000）★ 方案核心，重点扩充
CONTEXT_MAX_CHARS    = 24000   # 近期上下文上限（原 6000）
ANSWER_MAX_CHARS     = 24000   # 待复核的最终答复上限（原 8000）
ANSWER_CTX_CHARS     = 12000   # 答复复核时的上下文上限（原 4000）

_client = None
_client_sig = None
_MOCK = None                   # 自检注入：替换真实模型调用
_JSON_MODE_OK = True           # 服务端是否支持 response_format=json_object

# 有副作用的工具（auto 模式核验这些）
SIDE_EFFECT_TOOLS = {
    "ws_write", "ws_append", "ws_replace", "ws_delete", "ws_mkdir",
    "ws_cd", "ws_cd_approve", "ws_run_cmd", "ws_run_python",
}
# 只读工具（仅 all 模式才核验）
READ_ONLY_TOOLS = {
    "ws_where", "ws_list", "ws_read", "ws_search", "ws_forget",
}

VALID_VERDICTS = ("approve", "supplement", "revise")


# ============ 工具函数 ============
def _ts():
    return datetime.now().strftime("%Y-%m-%d %H:%M:%S")


def _log(msg):
    """同时写 root logger 与 logs/.../verify.log（后者可选）。"""
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
    return s if len(s) <= n else s[:n] + f"…（已截断，原长 {len(s)}）"


# ============ watcher 镜像（把审查过程实时写进被 tail 的文件）============
# fatfish_watcher.py 只 tail「logs/YYYY/MM/DD/exec_*.out」。因此主程序把镜像
# 路径设为同目录下的 exec_verify_<pid>.out —— 命名天然落在扫描范围内，
# watcher 会把它当「子程序输出」实时滚动，无需改动 watcher。
MIRROR_PATH = ""
MIRROR_ON   = True
_mirror_fh  = None


def set_mirror(path=None, enabled=None):
    """设置镜像文件路径 / 开关。path 传空串即关闭镜像。"""
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
    """关闭镜像文件句柄（进程退出时由 atexit 调用，保证落盘）。"""
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
    """把审查过程（一行或多行）写入镜像文件，供 fatfish_watcher 实时显示。"""
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


def _mirror_block(title, lines=None, icon="🧿 "):
    """写一段带分隔线与时间戳的镜像块。"""
    mirror("-" * 60)
    mirror(f"[{_ts()}] {icon}{title}".rstrip())
    for ln in (lines or []):
        mirror("    " + str(ln))


def _action_oneline(name, args):
    """把一次工具调用压成一行（镜像里用的紧凑描述）。"""
    args = args or {}
    if name == "ws_run_cmd":
        return f"{name} → {_clip(args.get('command', ''), 90)}"
    if name == "ws_run_python":
        return f"{name} → {_clip(args.get('code', ''), 90)}"
    if name == "ws_write":
        return (f"{name} → {args.get('path', '')} "
                f"（{len(args.get('content', '') or '')} 字符）")
    if name == "ws_append":
        return (f"{name} → {args.get('path', '')} "
                f"（追加 {len(args.get('content', '') or '')} 字符）")
    if name == "ws_replace":
        return (f"{name} → {args.get('path', '')} "
                f"「{_clip(args.get('old', ''), 30)}」→「{_clip(args.get('new', ''), 30)}」")
    if name in ("ws_delete", "ws_mkdir", "ws_cd", "ws_read"):
        return f"{name} → {args.get('path', '')}"
    if name == "ws_search":
        return f"{name} → {args.get('keyword', '')}"
    try:
        return f"{name} → {_clip(json.dumps(args, ensure_ascii=False), 90)}"
    except (TypeError, ValueError):
        return f"{name} → {_clip(str(args), 90)}"


def _result_lines(res, actions=None):
    """把一次裁决渲染成镜像用的多行文本。"""
    out = []
    v = res.get("verdict", "?")
    tag = {"approve": "✅ 放行 [approve]",
           "supplement": "🟡 需补充说明 [supplement]",
           "revise": "🛑 需变更方案 [revise]"}.get(v, str(v))
    if res.get("failed"):
        tag = "⚠️ 核验服务异常 [verify service failed]"
    elif res.get("degraded") and v == "approve":
        tag += "（解析降级）"
    head = f"裁决：{tag}  severity={res.get('severity', '?')}"
    if res.get("elapsed") is not None:
        head += f"  用时={res['elapsed']}s"
    out.append(head)

    if actions:
        out.append(f"送审动作（{len(actions)} 个）：")
        for i, (n, a) in enumerate(actions, 1):
            out.append(f"  {i}) {_action_oneline(n, a)}")

    if res.get("reason"):
        out.append("理由：" + res["reason"])
    for r in (res.get("risks") or []):
        out.append("风险：" + str(r))
    for d in (res.get("demands") or []):
        out.append("要求：" + str(d))
    for a in (res.get("alternatives") or []):
        out.append("备选：" + str(a))
    return out


# ============ 配置 / 状态 ============
def configure(api_key="", base_url="", model="", mode=None, strict=None,
              max_retries=None, fail_mode=None, log_dir=None,
              mirror_path=None, mirror_on=None,
              max_tokens=None, temperature=None, timeout=None,
              prompt_max_chars=None, action_preview=None, replace_preview=None,
              goal_max_chars=None, plan_max_chars=None, context_max_chars=None,
              answer_max_chars=None, answer_ctx_chars=None):
    """由主程序调用，注入配置。未传的项保持原值。"""
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
    # ---- 送审「方案」长度上限（业务 AI → 复核 AI）----
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
    """off 以外都算开启。"""
    return MODE in ("auto", "all")


def is_off():
    return not is_enabled()


def should_verify(tool_name):
    """按 MODE 判断某个工具调用是否需要核验。"""
    if MODE == "all":
        return True
    if MODE == "auto":
        return tool_name in SIDE_EFFECT_TOOLS
    return False


def pick_actions(parsed_calls):
    """从 [(tc, name, args), ...] 里挑出需要核验的项，返回 [(idx, name, args), ...]。

    主程序用它决定「这一批里哪些动作要送审」，同时用返回的 idx 精确回填打回消息。
    """
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
    """给 /verify 状态用的一行「送审长度上限」摘要。"""
    return (f"总 {PROMPT_MAX_CHARS} ｜ 计划 {PLAN_MAX_CHARS} ｜ 需求 {GOAL_MAX_CHARS} ｜ "
            f"动作 {ACTION_PREVIEW} ｜ 上下文 {CONTEXT_MAX_CHARS} ｜ 输出 {MAX_TOKENS} tok")


def mode_label():
    """给横幅 / 状态命令用的一行摘要。"""
    if not is_enabled():
        return "OFF（/verify on 开启）"
    scope = "ALL（含只读）" if MODE == "all" else "AUTO（仅有副作用动作）"
    return (f"ON · {scope} · 审查员 {MODEL} · "
            f"{'严格拦截' if STRICT else '宽松放行'} · "
            f"重试上限 {MAX_RETRIES} · 故障时{'拦截' if FAIL_MODE == 'closed' else '放行'}")


# ============ 审查员人格（system prompt） ============
VERIFIER_SYSTEM = (
    "你是「双人核验模式」中的第二位 AI——独立审查员（Verifier AI）。\n"
    "第一位 AI（执行者）正在操作一个真实的工作台（可读写文件、执行命令、运行代码），"
    "它把「即将执行的动作清单」交给你复核。你给出的是**独立第二意见**——"
    "通过之后仍会由用户本人在终端逐批确认，因此你不必替所有潜在风险兜底。\n\n"
    "【核心准则：默认放行，只拦真问题】\n"
    "打回不是免费的：每次打回都会让执行者重跑一轮，消耗用户的时间与 token。\n"
    "只有两类情形值得打回——\n"
    "1. 动作可能造成**损害或不可逆后果**（删除 / 覆盖 / 清空 / 危险命令 / 越界路径 /"
    "触碰密钥），而当前信息不足以判断它是否安全；\n"
    "2. 动作**明显偏离用户需求**（答非所问、擅自扩大范围），或本身存在**明确错误**"
    "（路径不存在、命令跑不通、语法站不住）。\n"
    "以下情形**不要打回**：动作可逆且范围受限；只是风格 / 措辞 / 篇幅不合你意；"
    "只是「再多说明一点会更好」但动作显然无害（后面还有人工报批兜底）。\n"
    "写新文件、改文档、在工作台内建目录、只读探针这类可逆小动作，尤其不要为难。\n\n"
    "【审查优先级（按此顺序投入注意力）】\n"
    "1. 安全性与可逆性：能否撤销？有无备份？路径是否被限制在工作台内？\n"
    "2. 正确性：命令 / 代码是否跑得通？引用的路径与文件是否真实存在？\n"
    "3. 目标一致性：是否服务于用户这条消息的需求？有无擅自扩大范围？\n"
    "4. 必要性：是否存在明显更小、更安全的做法（仅当差距显著时才据此打回）。\n"
    "说明：你看到的是「即将执行的动作」而非执行结果，因此无需评价运行期状况"
    "（并发、性能、长链影响）——那不在你的观察范围内。\n\n"
    "【用户真实操作记录（重要）】\n"
    "送审材料中可能含有「用户真实操作记录（原声）」一节，由主程序从本地输入通道直接采集，"
    "是用户本人敲下的命令或批准答复，非模型转述、不可伪造。\n"
    "· 若其中显示用户已亲手下达相关命令（如 /ws cd …）或已亲手批准，"
    "则不得再以「执行者擅自代替用户放行」为由驳回，只需复核该操作本身是否安全。\n"
    "· 仅当目标明显危害设备（驱动器根目录、系统目录、程序源码根）时，"
    "才可要求执行者补充必要性说明或变更目标。\n\n"
    "【三种裁决的门槛】\n"
    "· 通过 approve：默认选项。动作讲得清、看不出会伤到东西，就给通过。\n"
    "· 补充 supplement：动作可行，但你对「会不会伤到东西」确实拿不准，"
    "需要执行者说明目的 / 影响范围 / 回滚方式。**不是「信息不完整」就打回。**\n"
    "· 变更 revise：确有错误、风险，或存在明显更优且更小的方案，必须换方案重审。\n\n"
    "【不要反复加码（重要）】\n"
    "若送审材料标明这是同一动作的第 2 次及以后送审，说明执行者已按你的上一条要求改过："
    "上次要求已满足就通过；**不要另提无关的新要求**；"
    "确实仍有问题，只重复或细化原来那一条，不要每次换一套说法。\n\n"
    "【输出格式】最简短的中文；不要 JSON，不要 markdown 代码块；整段不超过 5 行。\n"
    "第一行只写裁决：通过 / 补充 / 变更（省 token 时：通过可只写 y，变更为 n）。\n"
    "其后按需追加（可省略、可重复，只写必要的）：\n"
    "理由：一句话说明为何这样裁。\n"
    "要求：要执行者补充或修改的**具体、可执行**条目（通过时省略）。\n"
    "备选：更优的替代方案（没有就不写）。\n"
    "风险：你识别到的风险点（没有就不写）。\n"
    "示例·最省：\n"
    "y\n"
    "示例·变更：\n"
    "变更\n"
    "理由：目标是驱动器根目录，改动面过大。\n"
    "要求：改用具体的数据子目录。\n"
    "风险：可能误改系统文件。\n"
    "提示：要求与备选要具体、可执行，不要写空话；动作确实没问题时就痛快回 y。"
)

# 复核「最终答复」时的人格（可选功能）
ANSWER_SYSTEM = (
    "你是「双人核验模式」中的第二位 AI——独立审查员（Verifier AI）。\n"
    "执行者 AI 即将把一个用户请求的最终答复交付给用户，请你复核。\n"
    "你给出的是**独立第二意见**，不是最后一道裁定；打回会让执行者重跑一轮，"
    "消耗用户的时间与 token，所以只拦真问题。\n\n"
    "【应当打回】\n"
    "1. 没有真正回答用户的问题（答非所问、偷换概念、遗漏关键点）；\n"
    "2. 关键事实或代码有错、存在明显幻觉或前后矛盾；\n"
    "3. 隐瞒了失败、未完成事项或应交代的不确定性。\n"
    "【不该打回】\n"
    "文风、篇幅、排版、措辞偏好；「如果再补充说明会更好」但并不影响交付；"
    "用户没要求逐字校对。不要为反对而反对。\n\n"
    "【三种裁决】通过（可以交付）/ 补充（需补关键信息）/ 变更（结论或方案有误，需重做）。\n"
    "【输出格式】最简短的中文；不要 JSON，不要 markdown；整段不超过 4 行。\n"
    "第一行只写裁决：通过 / 补充 / 变更（省 token 时：通过可只写 y，变更为 n）。\n"
    "其后按需追加（可省略、可重复）：\n"
    "理由：一句话理由。\n"
    "要求：需补充或修改的**具体条目**（通过时省略）。\n"
)


# ============ 动作描述 / 提示词拼装 ============
def describe_action(name, args):
    """把一次工具调用渲染成审查员看得懂的多行文本。"""
    args = args or {}

    def prev(key, n=ACTION_PREVIEW):
        return _clip(args.get(key, ""), n)

    if name == "ws_write":
        body = prev("content")
        return (f"写入/覆盖文件 [write]：{args.get('path', '')}\n"
                f"    内容（前 {ACTION_PREVIEW} 字）：\n{_indent(body)}")
    if name == "ws_append":
        return (f"追加内容到文件 [append]：{args.get('path', '')}\n"
                f"    追加内容：\n{_indent(prev('content'))}")
    if name == "ws_replace":
        return (f"精确替换文件内容 [replace]：{args.get('path', '')}\n"
                f"    old（待替换原文，前 {REPLACE_PREVIEW} 字）：\n"
                f"{_indent(_clip(args.get('old', ''), REPLACE_PREVIEW))}\n"
                f"    new（新内容，前 {REPLACE_PREVIEW} 字）：\n"
                f"{_indent(_clip(args.get('new', ''), REPLACE_PREVIEW))}")
    if name == "ws_delete":
        return f"删除文件/空目录 [delete]：{args.get('path', '')}"
    if name == "ws_mkdir":
        return f"创建目录 [mkdir]：{args.get('path', '')}"
    if name == "ws_cd":
        return f"切换工作台根目录 [cd]：{args.get('path', '')}"
    if name == "ws_cd_approve":
        return "批准并执行「移出默认作业区」的工作台切换 [cd approve]"
    if name == "ws_run_cmd":
        return (f"执行命令 [run cmd]（cwd={args.get('cwd', '')}，"
                f"timeout={args.get('timeout', 120)}）：\n{_indent(_clip(args.get('command', ''), ACTION_PREVIEW))}")
    if name == "ws_run_python":
        return (f"执行 Python 代码 [run python]（cwd={args.get('cwd', '')}，"
                f"timeout={args.get('timeout', 120)}）：\n{_indent(_clip(args.get('code', ''), ACTION_PREVIEW))}")
    # 兜底：JSON 化
    try:
        dump = json.dumps(args, ensure_ascii=False)
    except (TypeError, ValueError):
        dump = str(args)
    return f"{name}：{_clip(dump, ACTION_PREVIEW)}"


def _indent(text, pad="      "):
    return "\n".join(pad + ln for ln in str(text).splitlines())


def _join_trim(sections, limit, floor=250):
    """按优先级分配预算拼装段落；超限时所有段落按比例收缩，**保证总长不超限**。

    sections: [(priority, text), ...]，priority 越大分到的预算越多。
    算法：
      1. 先给每段一个保底额度 floor（不超过 limit/n）；
      2. 剩余预算按「优先级平方」权重分配（拉开差距）；
      3. 每段再按自身长度封顶。
    这样既能保住每段（不整段丢弃），又不会溢出到需要硬截尾。
    """
    sections = [(p, t) for p, t in sections if t]
    if not sections:
        return ""
    n = len(sections)
    sep = 2                                    # 段落间空行成本
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
        budget = min(budget, len(t))           # 不超过实际长度
        if len(t) > budget:
            t = t[:budget].rstrip() + "\n……（因总长限制已压缩）"
            budget = len(t)
        out.append(t)

    # 兜底：万一还超出（截断标记本身占位），按需再微调
    text = "\n\n".join(out)
    while len(text) > limit and any(len(t) > 200 for t in out):
        k = max(range(n), key=lambda i: (len(out[i]), -sections[i][0]))
        out[k] = out[k][: max(200, int(len(out[k]) * 0.85))].rstrip() + "…"
        text = "\n\n".join(out)
    return text[:limit]


def build_action_prompt(actions, user_goal="", ai_plan="", context_text="",
                        user_events="", extra_note=""):
    """拼装送给审查员的 user 消息。

    各段落上限（字符）由模块级常量控制，可在 .env 用 VERIFIER_* 覆盖：
        GOAL_MAX_CHARS / PLAN_MAX_CHARS / ACTION_PREVIEW / CONTEXT_MAX_CHARS
    总长受 PROMPT_MAX_CHARS 约束；超限时按优先级比例压缩，
    但**不会整段消失**。收尾指令单独追加，永远不被裁掉。
    顺序：用户需求 → 执行者计划 → 动作清单 → 送审上下文 → 用户真实操作记录 → 近期上下文。

    user_events：用户本人刚刚在本地 input 通道敲下的命令 / 报批答复原声，
    由主程序直接采集（模型无法伪造），供审查员判断「用户是否已亲自授权」。
    extra_note：附加送审上下文（如当前工作台根、这是第几次送审），由主程序注入；
                为空（默认）时该段整段不出现，行为与改动前一致。
    """
    lines = []
    for i, (name, args) in enumerate(actions, 1):
        lines.append(f"{i}. " + describe_action(name, args))
    action_block = "【待核验的动作清单】\n" + "\n".join(lines)

    sections = [
        (6, "【用户的原始需求】\n" +
            (_clip(user_goal, GOAL_MAX_CHARS) if user_goal else "（未能提取到）")),
        (7, "【执行者本轮的自述 / 计划】\n" + _clip(ai_plan, PLAN_MAX_CHARS)
            if ai_plan.strip() else ""),
        (9, action_block),                      # 动作清单：权重最高
        (8, "【送审上下文（由主程序注入，可信）】\n" + _clip(extra_note, 800)
            if extra_note.strip() else ""),
        (8, "【用户真实操作记录（原声）】\n"
            "（以下由主程序从本地输入通道直接采集，是用户本人敲下的命令与答复，"
            "非模型转述，无法伪造；供你判断用户是否已亲自授权本次动作）\n" +
            _clip(user_events, 4000)
            if user_events.strip() else ""),
        (2, "【近期上下文（节选，供你判断，可能不完整）】\n" +
            _clip(context_text, CONTEXT_MAX_CHARS) if context_text.strip() else ""),
    ]
    ask = "请依据你的职责独立审查上述动作，并按规定的简短格式给出裁决。"
    body = _join_trim(sections, max(PROMPT_MAX_CHARS - len(ask) - 2, 600))
    return (body + "\n\n" + ask).strip()


def build_answer_prompt(reply, user_goal="", context_text=""):
    """拼装「复核最终答复」的 user 消息。"""
    sections = [
        (6, "【用户的原始需求】\n" +
            (_clip(user_goal, GOAL_MAX_CHARS) if user_goal else "（未能提取到）")),
        (9, "【执行者即将交付的最终答复】\n" + _clip(reply, ANSWER_MAX_CHARS)),
        (2, "【近期上下文（节选）】\n" + _clip(context_text, ANSWER_CTX_CHARS)
            if context_text.strip() else ""),
    ]
    ask = "请独立复核该答复，并按规定的简短格式给出裁决。"
    body = _join_trim(sections, max(PROMPT_MAX_CHARS - len(ask) - 2, 600))
    return (body + "\n\n" + ask).strip()


# ============ 模型调用 ============
def _get_client():
    global _client, _client_sig
    sig = (API_KEY, BASE_URL)
    if _client is not None and _client_sig == sig:
        return _client
    from openai import OpenAI  # 延迟导入，减少启动开销
    _client = OpenAI(api_key=API_KEY, base_url=BASE_URL)
    _client_sig = sig
    return _client


def _raw_chat(messages):
    """真正调用审查员模型，返回文本。

    注意：审查员已改用「简短文本协议」输出（不再要求 JSON），
    因此这里**不再**传 response_format=json_object —— 否则服务端会强制
    模型只输出 JSON，反而与新协议冲突，也会重新引入解析故障。
    """
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


# ============ JSON 解析（强健版） ============
def _balanced_json(text):
    """从文本里剥出第一个平衡的 {...}。"""
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
    """尽量宽容地把文本解析为 JSON。

    模型常见毛病：在 JSON 字符串里写裸换行 / 制表符，导致标准解析失败。
    先直接解析；失败则把字符串字面量内的裸控制字符转义后再试一次。
    返回 dict，彻底失败返回 None。
    """
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

_ZH_VERDICT_WORDS = (
    ("通过", "approve"), ("放行", "approve"), ("同意", "approve"), ("批准", "approve"),
    ("补充", "supplement"), ("需补充", "supplement"), ("存疑", "supplement"),
    ("变更", "revise"), ("需变更", "revise"), ("驳回", "revise"), ("拒绝", "revise"),
)
_EN_VERDICT_WORDS = {
    "approve": "approve", "supplement": "supplement", "revise": "revise",
    "ok": "approve", "pass": "approve", "yes": "approve",
    "reject": "revise", "no": "revise",
}


def _clean_head(s):
    return re.sub(r'[\s:：,，.。;；!！?？"\'`*#\-\[\]()（）]+', "", (s or "").strip().lower())


def _match_verdict_word(line):
    """在一行里识别裁决词，返回 canonical 值或空串。英文词只做整行精确匹配以防误判。"""
    core = _clean_head(line)
    if not core:
        return ""
    if core in _EN_VERDICT_WORDS:
        return _EN_VERDICT_WORDS[core]
    for w, v in _ZH_VERDICT_WORDS:
        if core.startswith(w):
            return v
    return ""


def _grab_labeled(lines, label):
    """从若干行里抽取以「label：」开头的内容（中英文冒号都认）。"""
    out = []
    for ln in lines:
        for sep in ("：", ":"):
            if ln.startswith(label + sep):
                v = ln[len(label) + len(sep):].strip()
                if v:
                    out.append(v)
                break
    return out


def _parse_verdict_text(raw):
    """解析「简短文本协议」：首行裁决词，其后 理由/要求/备选/风险 行。

    首行支持极简简写（省 token）：y=通过 / n=变更；也兼容
    通过/补充/变更 与 approve/supplement/revise 等写法。
    形如（最省）：
        y
    或：
        变更
        理由：目标是驱动器根目录。
        要求：改用具体子目录。
    解析不出返回 None（由调用方走保守兜底）。
    """
    lines = [ln.strip() for ln in (raw or "").splitlines() if ln.strip()]
    if not lines:
        return None

    verdict = ""
    brief = ""
    # ★ 极简简写入口（省 token）：首行经 _clean_head 归一化后「恰好等于」y/n 时直接映射。
    #   判定用整行相等，绝不做子串匹配，因此 "yaml"/"node" 之类不会被误命中；
    #   其余任何写法原样落到下方关键词表，行为与改动前完全一致。
    #   注：上方已有卫语句 `if not lines: return None`；此处再用条件表达式兜底，
    #   即便将来该卫语句被改动，也不会因空列表而抛 IndexError。
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

    reason = " ".join(_grab_labeled(lines, "理由"))[:500]
    demands = _grab_labeled(lines, "要求")
    alternatives = _grab_labeled(lines, "备选")
    risks = _grab_labeled(lines, "风险")

    low = raw.lower()
    if ("高风险" in raw) or ("严重" in raw) or ("high" in low):
        severity = "high"
    elif ("低风险" in raw) or ("low" in low):
        severity = "low"
    else:
        severity = "low" if verdict == "approve" else "medium"
    if not reason:
        reason = (f"（审查员极简回复「{brief}」，未附理由）" if brief
                  else "（审查员给出简短裁决，未附理由）")

    return {
        "verdict": verdict, "severity": severity, "reason": reason,
        "demands": demands, "alternatives": alternatives, "risks": risks,
        "degraded": False, "raw": raw,
    }


def parse_verdict(text):
    """把模型输出解析成结构化裁决。

    解析顺序（依次尝试，任一成功即返回）：
      ① 严格 / 宽容 JSON（向后兼容旧格式，或审查员偶尔仍吐 JSON）；
      ② JSON 关键字降级（形如 verdict: "supplement"）；
      ③ 简短文本协议（首行：y / n / 通过 / 补充 / 变更）；
      ④ 全都解析不出 → 保守判为 supplement。
    """
    raw = text or ""

    # ① JSON
    obj = None
    blob = _balanced_json(raw)
    if blob:
        obj = _loose_json_load(blob)
    if isinstance(obj, dict):
        return _verdict_from_obj(obj, raw)

    # ② JSON 关键字降级
    m = re.search(r'verdict"?\s*:\s*"?(approve|supplement|revise)', raw, re.I)
    if m:
        return {
            "verdict": m.group(1).lower(), "severity": "medium",
            "reason": "（审查员输出非严格 JSON，已降级解析）",
            "demands": [], "alternatives": [], "risks": [],
            "degraded": True, "raw": raw,
        }

    # ③ 简短文本协议
    txt = _parse_verdict_text(raw)
    if txt:
        return txt

    # ④ 保守兜底
    return {
        "verdict": "supplement",
        "severity": "medium",
        "reason": "审查员返回内容无法解析，按保守策略要求补充说明。",
        "demands": ["请补充说明本次动作的目的、影响范围与回滚方式。"],
        "alternatives": [], "risks": [],
        "degraded": True, "raw": raw,
    }


def _verdict_from_obj(obj, raw):
    """把已解析成功的 JSON dict 规范化成裁决结构。"""
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


# ============ 核心：审查 ============
def _fail_result(err, what="动作"):
    """核验服务不可用时的兜底结果（按 FAIL_MODE 决定是否拦截）。"""
    if FAIL_MODE == "closed":
        return {
            "ok": False, "blocked": True, "verdict": "supplement",
            "severity": "medium",
            "reason": f"核验服务不可用（{err}），已按 fail-closed 策略拦截，请稍后重试。",
            "demands": [f"{what}未通过独立核验：审查服务暂时不可用，请稍后重试或人工确认。"],
            "alternatives": [], "risks": [], "degraded": True, "failed": True, "raw": "",
        }
    return {
        "ok": False, "blocked": False, "verdict": "approve",
        "severity": "low",
        "reason": f"核验服务不可用（{err}），已按 fail-open 策略放行。",
        "demands": [], "alternatives": [], "risks": [],
        "degraded": True, "failed": True, "raw": "",
    }


def _run_review(system_prompt, user_prompt):
    """共用内核：调用审查员 → 解析 → 附带 blocked 标记。"""
    # ---- 测试桩：FATFISH_VERIFY_MOCK=approve|supplement|revise 可强制裁决（不联网）----
    _mock = (os.getenv("FATFISH_VERIFY_MOCK") or "").strip().lower()
    if _mock in VALID_VERDICTS:
        res = {
            "ok": True, "verdict": _mock,
            "severity": "low" if _mock == "approve" else "high",
            "reason": f"[测试桩] 强制裁决为 {_mock}",
            "demands": [] if _mock == "approve" else [f"[测试桩] 请补充说明后重新提交（{_mock}）"],
            "alternatives": ["[测试桩] 建议先只读探查再动手"] if _mock != "approve" else [],
            "risks": ["[测试桩] 假风险"] if _mock == "revise" else [],
            "degraded": False, "failed": False, "raw": "",
            "blocked": _mock != "approve",
        }
        _log(f"[{_ts()}] 核验（测试桩 FATFISH_VERIFY_MOCK={_mock}）：blocked={res['blocked']}")
        return res

    if _MOCK is None and not API_KEY:
        return _fail_result("未配置审查员 API key")

    messages = [
        {"role": "system", "content": system_prompt},
        {"role": "user", "content": user_prompt},
    ]
    t0 = time.time()
    try:
        text = _chat(messages)
    except Exception as e:
        tb = traceback.format_exc()
        _log(f"[{_ts()}] 核验调用失败：{type(e).__name__}: {e}\n{tb}")
        if os.getenv("FATFISH_VERIFY_DEBUG"):
            print("[verify_tools] 核验调用失败：\n" + tb, file=sys.stderr)
        return _fail_result(f"{type(e).__name__}: {e}")

    cost = time.time() - t0
    res = parse_verdict(text)
    res["ok"] = True
    res["blocked"] = res["verdict"] != "approve"
    res["elapsed"] = round(cost, 2)
    res.setdefault("failed", False)
    if res.get("degraded"):
        # 解析降级：supplement 会拦截，但我们尊重语义，照常拦截。
        # 诊断：把审查员原始输出落盘，便于定位「无法解析」的真实形态（不影响主流程）。
        try:
            _log(f"[{_ts()}] 核验降级解析，审查员原始输出（{len(text)} 字符）："
                 f"{_clip(str(text).replace(chr(10), ' | '), 800)}")
        except Exception:
            pass
    _log(f"[{_ts()}] 核验完成 {res['verdict']}({res['severity']}) "
         f"{cost:.2f}s：{res['reason']}")
    return res


def review(actions, user_goal="", ai_plan="", context_text="", user_events="",
           extra_note=""):
    """审查一批「有副作用的动作」。

    actions: [(name, args), ...]
    extra_note: 附加送审上下文（如当前工作台根、这是第几次送审），由主程序注入；
                缺省为空时对行为无任何影响（向后兼容）。
    返回 dict：ok / blocked / verdict / severity / reason / demands /
               alternatives / risks / degraded / failed / raw
    """
    actions = [a for a in (actions or []) if a]
    if not actions or not is_enabled():
        return {"ok": True, "blocked": False, "verdict": "approve",
                "reason": "（无需核验）", "demands": [], "alternatives": [],
                "risks": [], "degraded": False, "failed": False, "raw": "",
                "skipped": True}

    # 镜像：告知 watcher「开始审查」
    _mirror_block(f"双人核验·送审 {len(actions)} 个动作"
                  f"（审查员 {MODEL}）", None, icon="🧿 ")
    prompt = build_action_prompt(actions, user_goal, ai_plan, context_text,
                                 user_events, extra_note)
    res = _run_review(VERIFIER_SYSTEM, prompt)
    _mirror_block("双人核验·审查意见", _result_lines(res, actions))
    return res


def review_answer(reply, user_goal="", context_text=""):
    """复核主模型的「最终答复」（可选功能）。"""
    if not (reply or "").strip() or not is_enabled():
        return {"ok": True, "blocked": False, "verdict": "approve",
                "reason": "（无需核验）", "demands": [], "alternatives": [],
                "risks": [], "degraded": False, "failed": False, "raw": "",
                "skipped": True}
    _mirror_block("双人核验·复核最终答复", None, icon="🧿 ")
    prompt = build_answer_prompt(reply, user_goal, context_text)
    res = _run_review(ANSWER_SYSTEM, prompt)
    _mirror_block("双人核验·答复复核意见", _result_lines(res, None))
    return res


# ============ 联网需求核验（第二位 AI 判定「要不要搜」）============
# 背景：原来的 net_tools.need_search() 是纯关键词规则 ——
#   「时间词 + 问号」就触发搜索，把「现在XX功能能开吗」这类问系统自身状态
#   的问题误判为需要联网（实测误报率约 42%），搜回一堆无关内容污染上下文。
# 这里改用第二位 AI 独立判断：是否需要外部时效信息？
# 顺带产出改写后的搜索关键词（去掉口语与指代），提升检索质量。
SEARCH_JUDGE_SYSTEM = (
    "你是「联网需求核验员」——在 AI 助手决定是否联网搜索之前，"
    "独立判断用户这条消息是否真的需要检索外部信息才能正确回答。\n\n"
    "【判定为「需要联网」need_search=true】\n"
    "1. 需要实时 / 时效数据：天气、股价、汇率、比分、路况、票价、最新新闻、版本发布、价格；\n"
    "2. 涉及动态变化或可能已过时的事实：具体数字、争议事实、行业动态、政策变化；\n"
    "3. 你自己不确定、无法给出确定答案的事实性问题；\n"
    "4. 用户明确要求：搜索 / 查一下 / 帮我查 / 联网 / 最新消息。\n\n"
    "【判定为「无需联网」need_search=false】\n"
    "1. 纯理论 / 学术问题：数学推导、算法、代码逻辑、写作、翻译、常识；\n"
    "2. 问题已含完整上下文：用户已提供文档 / 代码 / 资料，只需就地分析；\n"
    "3. 询问系统自身状态：当前配置、工作台路径、功能是否可开关、本程序行为、"
    "对话历史里已有的信息；\n"
    "4. 寒暄、闲聊、情绪表达、请求指导下一步怎么做；\n"
    "5. 答案在当前对话或工作台文件内即可获得；\n"
    "6. **刚刚已经搜过的话题**的追问与延续（除非用户要求查最新的、或换一个关键词）。\n\n"
    "【当 need_search=true 时，还要选择检索模式 mode】\n"
    '- "extract"：需要读取「特定网页的正文」时使用。特征：用户消息里带有具体 URL / 链接，'
    "且意图是了解该页面内容（如「看看这篇文章讲了啥」「总结一下这个链接」「这个文档说了什么」）。\n"
    '- "search"：需要「按主题检索全网信息」时使用。特征：用户描述的是一个话题 / 问题，'
    "没有指定要看某个具体页面（如「最新政策是什么」「XX 的价格」「有什么新闻」）。\n"
    "判断要点：\n"
    "  · 有链接 → 大概率 extract；但若是「还有别的类似网站吗」这类**以链接为线索去扩展搜索**的意图，仍用 search。\n"
    "  · 无链接 → 一律 search（extract 没有 URL 无法执行）。\n"
    "  · 拿不准 → 用 search（更通用、更安全）。\n\n"
    "【输出格式】不要用 JSON，不要 markdown。用最简短的中文回答，优先极简简写（省 token）：\n"
    "第一行只写一个词：y=需要联网搜索 / n=不需要联网 / 抓取=需读指定网页正文。\n"
    "★ 不需要联网：只回一个字母 n，不要写理由、不要写任何多余的字。\n"
    "★ 需要联网搜索：回 y，并紧跟一行「查询：…」给出改写后的检索词"
    "（精炼、去口语与指代、保留关键实体）。\n"
    "★ 需读指定网页正文：回「抓取」，并紧跟一行「网址：…」。\n"
    "除非确有风险要提醒，否则不要写「理由」行。\n"
    "示例一（不需要联网——只回一个字母）：\n"
    "n\n"
    "示例二（需要联网——y + 改写后的检索词）：\n"
    "y\n"
    "查询：比特币 最新价格\n"
    "示例三（需读指定网页正文）：\n"
    "抓取\n"
    "网址：https://example.com/article\n"
    "判断倾向：宁可少搜，也不要为纯本地 / 理论 / 自身状态类问题去搜一堆无关内容；"
    "但用户**明确要求**搜索时必须搜，不要因保守而漏搜。"
)


def _parse_search_text(raw):
    """解析「简短文本协议」的联网判定：首行 y / n / 不搜 / 搜索 / 抓取，其后 理由/查询/网址 行。

    极简简写（省 token）：y=需要联网（search）/ n=不需要联网；「抓取」=需读指定网页正文（extract）。
    y 未附「查询」行时 query 为空字符串，此时由调用方 net_tools.do_network 回落到用户原始消息。
    解析不出返回 None。"""
    lines = [ln.strip() for ln in (raw or "").splitlines() if ln.strip()]
    if not lines:
        return None

    def _kind(s):
        t = _clean_head(s)
        if t.startswith("不搜") or t in ("false", "no", "n", "否", "无需", "不需要", "不联网", "无需联网"):
            return (False, "search")
        if t.startswith("搜索") or t.startswith("联网搜") or t in ("search", "true", "yes", "y", "是", "需要"):
            return (True, "search")
        if t.startswith("抓取") or t.startswith("提取") or t.startswith("读取正文") or t == "extract":
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

    reason = " ".join(_grab_labeled(lines, "理由"))[:500]
    query = " ".join(_grab_labeled(lines, "查询") + _grab_labeled(lines, "检索词")
                     + _grab_labeled(lines, "关键词"))[:300]
    urls = []
    for ln in lines:
        for sep in ("：", ":"):
            if (ln.startswith("网址" + sep) or ln.startswith("链接" + sep)
                    or ln.startswith("URL" + sep)):
                urls += re.findall(r'https?://[^\s<>"\'）】\)\]]+', ln)
                break
    urls = [u.rstrip(".,;:!?）】、。，") for u in urls]
    urls = [u for u in urls if u][:10]
    if mode == "extract" and not urls:
        urls = _guess_urls(raw)
    if mode == "extract" and not urls:
        mode = "search"
    if not reason:
        reason = "（简短判定，未附理由）"
    return {"need_search": need, "mode": mode, "reason": reason,
            "query": query, "urls": urls, "confidence": "medium",
            "degraded": False, "failed": False, "raw": raw}


def parse_search_verdict(text):
    """解析联网需求核验的输出。解析失败时保守判定为「不搜」。"""
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
                    "mode": "search", "reason": "（非严格 JSON，已降级解析）",
                    "query": "", "urls": [],
                    "confidence": "low", "degraded": True, "failed": False, "raw": raw}
        txt = _parse_search_text(raw)
        if txt:
            return txt
        return {"need_search": False,
                "mode": "search",
                "reason": "输出无法解析，保守判定为无需联网",
                "query": "", "urls": [],
                "confidence": "low",
                "degraded": True, "failed": False, "raw": raw}

    mode = str(obj.get("mode", "")).strip().lower()
    if mode not in ("search", "extract"):
        # 若模型没给 / 给错，按「有 URL 就 extract」兜底判断
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
    """第二位 AI 判定：这条消息要不要联网？

    返回 dict：need_search / reason / query / confidence /
               degraded / failed / skipped
    - failed=True 时，调用方应回退到关键词规则 net_tools.need_search()。
    - query 非空时，调用方可直接用它去搜索（替代原始口语输入）。
    """
    if not (user_input or "").strip():
        return {"need_search": False, "reason": "（空输入）", "query": "",
                "confidence": "high", "degraded": False, "failed": False, "skipped": True}

    prompt_parts = ["【用户最新消息】\n" + _clip(user_input, 4000)]
    if (context_text or "").strip():
        prompt_parts.append("【近期对话上下文（节选，供判断，可能不完整）】\n"
                            + _clip(context_text, 4000))
    prompt_parts.append("请判定是否需要进行联网搜索，并按规定的简短格式给出结果。")
    user_prompt = _clip("\n\n".join(prompt_parts), PROMPT_MAX_CHARS)

    _mirror_block("联网需求核验·送审", [f"用户消息：{_clip(user_input, 120)}"], icon="🌐 ")

    # ---- 测试桩：FATFISH_JUDGE_MOCK=true|false|extract 强制判定（不联网）----
    _mock = (os.getenv("FATFISH_JUDGE_MOCK") or "").strip().lower()
    if _mock in ("true", "yes", "1", "search", "on"):
        res = {"need_search": True, "mode": "search", "reason": "[测试桩] 强制需要联网(search)",
               "query": _clip(user_input, 60), "urls": [], "confidence": "high",
               "degraded": False, "failed": False, "skipped": False}
        _mirror_block("联网需求核验·判定", ["need_search=True, mode=search（测试桩）"], icon="🌐 ")
        return res
    if _mock in ("extract", "e"):
        res = {"need_search": True, "mode": "extract", "reason": "[测试桩] 强制需要联网(extract)",
               "query": "", "urls": [u for u in _guess_urls(user_input)],
               "confidence": "high",
               "degraded": False, "failed": False, "skipped": False}
        _mirror_block("联网需求核验·判定", ["need_search=True, mode=extract（测试桩）"], icon="🌐 ")
        return res
    if _mock in ("false", "no", "0", "skip", "off"):
        res = {"need_search": False, "mode": "search", "reason": "[测试桩] 强制无需联网",
               "query": "", "urls": [], "confidence": "high",
               "degraded": False, "failed": False, "skipped": False}
        _mirror_block("联网需求核验·判定", ["need_search=False（测试桩）"], icon="🌐 ")
        return res

    if _MOCK is None and not API_KEY:
        return {"need_search": False, "mode": "search", "reason": "未配置核验 key",
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
        _log(f"[{_ts()}] 联网需求核验失败：{type(e).__name__}: {e}\n{tb}")
        if os.getenv("FATFISH_VERIFY_DEBUG"):
            print("[verify_tools] 联网需求核验失败：\n" + tb, file=sys.stderr)
        res = {"need_search": False, "mode": "search",
               "reason": f"核验失败：{type(e).__name__}",
               "query": "", "urls": [], "confidence": "low",
               "degraded": False, "failed": True, "skipped": False}
        _mirror_block("联网需求核验·异常", [res["reason"]], icon="⚠️ ")
        return res

    res = parse_search_verdict(text)
    res["elapsed"] = round(time.time() - t0, 2)
    res["skipped"] = False
    # ---- 兜底修正：mode=extract 必须有 URL ----
    if res.get("need_search") and res.get("mode") == "extract":
        if not res.get("urls"):
            res["urls"] = _guess_urls(user_input)
        if not res["urls"]:
            res["mode"] = "search"
            res["_mode_fallback"] = "extract 模式但未取到 URL，回退 search"
            if not res.get("query"):
                res["query"] = _clip(user_input, 80)
    line = (f"need_search={res['need_search']}  mode={res['mode']}  "
            f"confidence={res['confidence']}  用时={res['elapsed']}s")
    _log(f"[{_ts()}] 联网需求核验：{line} | {res['reason']}")
    extra = [line, f"理由：{res['reason']}"]
    if res.get("query"):
        extra.append(f"改写查询：{res['query']}")
    if res.get("urls"):
        extra.append("待抓取 URL：" + " , ".join(res["urls"][:5]))
    if res.get("_mode_fallback"):
        extra.append("⚠️ " + res["_mode_fallback"])
    _mirror_block("联网需求核验·判定", extra, icon="🌐 ")
    return res


def _guess_urls(text):
    """从文本里粗抽 URL（供 extract 模式兜底）。"""
    if not text:
        return []
    seen, out = set(), []
    for u in re.findall(r'https?://[^\s<>"\'）】\)\]]+', text):
        u = u.rstrip(".,;:!?）】、。，")
        if u and u not in seen:
            seen.add(u)
            out.append(u)
    return out[:10]


def search_judge_label(res):
    """给控制台 / 镜像用的一行摘要。"""
    if not res:
        return "（无判定）"
    if res.get("skipped"):
        return "（跳过）"
    if res.get("failed"):
        return "⚠️ 核验异常，回退关键词规则"
    if not res.get("need_search"):
        return f"⛔ 无需联网（{res.get('confidence', '?')}）{res.get('reason', '')}"
    mode = res.get("mode", "search")
    icon = "📄" if mode == "extract" else "🔍"
    detail = ""
    if mode == "extract":
        urls = res.get("urls") or []
        detail = f" ｜ 抓取 {len(urls)} 个 URL" + (
            f"：{urls[0]}" if urls else "（无 URL，将回退搜索）")
    elif res.get("query"):
        detail = f" ｜ 查询：{res['query']}"
    return (f"{icon} 需要联网·{mode}"
            f"（{res.get('confidence', '?')}）{res.get('reason', '')}{detail}")


# ============ 对外渲染 ============
VERDICT_ICON = {"approve": "✅", "supplement": "🟡", "revise": "🛑"}


def summary_line(res):
    """控制台一行摘要。"""
    if not res:
        return "（无核验结果）"
    if res.get("skipped"):
        return "（跳过核验）"
    v = res.get("verdict", "?")
    icon = VERDICT_ICON.get(v, "❔")
    tag = "核验服务异常" if res.get("failed") else f"审查员裁决：{v}"
    reason = _clip(res.get("reason", ""), 120)
    return f"{icon} {tag}｜{reason}"


def feedback_text(res, kind="action", final=False):
    """把审查意见拼成回填给执行者的文本（塞进 role='tool' 消息）。"""
    v = res.get("verdict", "supplement")
    label = {"approve": "通过", "supplement": "需补充说明", "revise": "需变更方案"}.get(v, v)
    lines = [f"【双人核验·未通过｜审查员裁决：{v}（{label}）】"]
    if res.get("reason"):
        lines.append(f"理由：{res['reason']}")
    if res.get("risks"):
        lines.append("审查员识别的风险：")
        lines += [f"  - {x}" for x in res["risks"]]
    if res.get("demands"):
        lines.append("要求你（执行者）补充或修改：")
        lines += [f"  {i}) {x}" for i, x in enumerate(res["demands"], 1)]
    else:
        lines.append("要求你（执行者）补充说明本次操作的目的、影响范围与回滚方式。")
    if res.get("alternatives"):
        lines.append("审查员建议的替代方案：")
        lines += [f"  - {x}" for x in res["alternatives"]]

    if kind == "answer":
        lines.append("👉 请据此补充说明或改写答复后重新交付；不要原样重复上一次答复。")
    else:
        lines.append("👉 请先补充说明或变更方案，然后重新提交核验；"
                     "不要原样重试被驳回的动作。")
    if final:
        lines.append("⚠️ 已超出核验重试上限，本行动作被拦截，"
                     "请向用户说明情况并等待进一步指示。")
    lines.append("（这是第二位 AI 独立审查员的意见，供你自我修正使用。）")
    return "\n".join(lines)


# ============ 自检（不联网） ============
def _selftest():
    global _MOCK, MODE, API_KEY, FAIL_MODE, STRICT
    import io
    sys.stdout.reconfigure(encoding="utf-8", errors="replace") if hasattr(sys.stdout, "reconfigure") else None
    fails = []

    def check(name, cond):
        print(("  ✅ " if cond else "  ❌ ") + name)
        if not cond:
            fails.append(name)

    print("=" * 66)
    print("verify_tools 自检开始")
    print("=" * 66)

    # 1) JSON 解析：代码块包裹
    r = parse_verdict('```json\n{"verdict":"revise","reason":"越界","demands":["换路径"]}\n```')
    check("解析 markdown 包裹的 JSON", r["verdict"] == "revise" and r["demands"] == ["换路径"])

    # 2) JSON 解析：前后有废话
    r = parse_verdict('好的，我的结论是 {"verdict": "approve", "reason": "ok"} 完毕。')
    check("解析夹带文字的 JSON", r["verdict"] == "approve")

    # 3) 关键词降级
    r = parse_verdict('verdict: "supplement" 请补充说明')
    check("非 JSON 时关键词降级", r["verdict"] == "supplement" and r["degraded"])

    # 4) 完全没有可用信息 → 保守 supplement（拦截与否由裁决语义决定）
    r = parse_verdict('胡说八道没有结构')
    check("无法解析时保守 supplement", r["verdict"] == "supplement")

    # 5) review 全流程（mock 模型返回 revise）
    MODE, API_KEY, FAIL_MODE, STRICT = "auto", "sk-test", "open", True
    _MOCK = lambda msgs: json.dumps({
        "verdict": "revise", "severity": "high",
        "reason": "覆盖整个文件风险高",
        "demands": ["改用 ws_replace 局部修改", "说明原文件备份位置"],
        "alternatives": ["先 ws_read 再 ws_replace"],
        "risks": ["全量覆盖不可逆"],
    })
    res = review([("ws_write", {"path": "FATHFISH.py", "content": "x" * 50}),
                  ("ws_read", {"path": "a.txt"})],
                 user_goal="帮我改个 bug", ai_plan="我打算整体重写")
    check("review 返回 blocked", res["blocked"] and res["verdict"] == "revise")
    fb = feedback_text(res)
    check("feedback 含 demands", "ws_replace" in fb and "双人核验·未通过" in fb)
    check("summary_line 可读", "revise" in summary_line(res))

    # 6) pick_actions：auto 只挑副作用工具
    parsed = [("tc1", "ws_read", {"path": "a"}), ("tc2", "ws_write", {"path": "b"})]
    picks = pick_actions(parsed)
    check("pick_actions(auto) 只挑 ws_write", len(picks) == 1 and picks[0][1] == "ws_write")
    MODE = "all"
    check("pick_actions(all) 全挑", len(pick_actions(parsed)) == 2)
    MODE = "off"
    check("pick_actions(off) 不挑", len(pick_actions(parsed)) == 0)

    # 7) 模型异常 + fail-open / fail-closed
    MODE = "auto"

    def boom(_msgs):
        raise RuntimeError("network down")

    _MOCK = boom
    FAIL_MODE = "open"
    r = review([("ws_write", {"path": "a", "content": "b"})])
    check("fail-open：放行且标记 failed", r["blocked"] is False and r["failed"])
    FAIL_MODE = "closed"
    r = review([("ws_write", {"path": "a", "content": "b"})])
    check("fail-closed：拦截且标记 failed", r["blocked"] is True and r["failed"])

    # 8) 答复复核
    _MOCK = lambda msgs: '{"verdict":"supplement","reason":"没回答核心问题","demands":["补充结论"]}'
    r = review_answer("随便说两句。", user_goal="怎么做 A？")
    check("review_answer 可打回", r["blocked"] and "补充结论" in feedback_text(r, kind="answer"))

    # 9) describe_action 覆盖常用工具
    for nm, ar in [("ws_write", {"path": "p", "content": "c"}),
                   ("ws_run_cmd", {"command": "dir"}),
                   ("ws_run_python", {"code": "print(1)"}),
                   ("ws_replace", {"path": "p", "old": "o", "new": "n"})]:
        check(f"describe_action({nm}) 非空",
              nm in describe_action(nm, ar) or "：" in describe_action(nm, ar))

    _MOCK = None
    print("=" * 66)
    if fails:
        print(f"自检失败 {len(fails)} 项：" + "；".join(fails))
        return 1
    print("自检全部通过 ✅")
    return 0


if __name__ == "__main__":
    sys.exit(_selftest())
