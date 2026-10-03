# -*- coding: utf-8 -*-
"""
stream_core.py —— 流式输出内核（肥鱼装备升级 · Stream Mode）
============================================================

背景 / Why
----------
升级前，主程序是「憋一口气说完」：

    resp = client.chat.completions.create(...)      # 阻塞到最后一个 token
    print_ai(resp.choices[0].message.content)       # 整段一次性刷出来

长回答动辄十几秒屏幕毫无动静（只有转圈动画）。本模块把这一跳改成
**边收边打**：SSE 增量一到就渲染上屏。

三条硬约束（决定了实现为什么长这样）
------------------------------------
1) **不能破坏工具调用**
   主循环只认三个字段：``msg.content`` / ``msg.tool_calls`` /
   ``choice.finish_reason``。流式拿到的是一堆碎片（arguments 被切成好几段），
   所以这里必须把它们**拼回一个结构完全兼容的对象**（见 ``_Shim*``），
   让主程序下游（工具执行 / 报批 / 双人核验 / 日志 / QQ 直通）一个字都不用改。

2) **不能漏出标记**
   肥鱼支持 ``{{green}}文字{{/green}}`` 上色（``ui_core.TAG_RE`` /
   ``render_markup``）。流式会把标记切成两半：直接裸打会把 ``{{green}}``
   原样吐给用户。所以这里实现了一个 **标签栈 + 有限回退窗口** 的增量渲染器：
   - ``{{name}}`` 一到就上色（不必等闭合标签），``{{/name}}`` 一到就复位；
   - 半截的 ``{{``、``**``、`` ` `` 一律**扣在手里**，等够了再吐；
   - 扣留超过阈值（标记疑似写错）就按字面吐出，**绝不吞字**。

3) **不能退化成更差**
   任何异常、模块缺失、需要人工复核（最终答复核验开启）等场景，一律
   **回退到原来的非流式路径**（由主程序的 ``_fc_call`` 负责兜底）。
   流式是「新增能力」，不是「替换实现」。

对外接口 / Public API
---------------------
    enabled()               流式是否开启（env FATFISH_STREAM，默认开）
    set_enabled(bool)       运行期开关（/stream 命令用）
    think_enabled()         是否显示思考链（env FATFISH_STREAM_THINK，默认开）
    set_think(bool)
    new_printer(out=None)   造一个增量渲染器（可单独测试）
    stream_chat(...)        跑一次流式请求，返回与非流式兼容的响应对象
    last_printed()          上一轮正文是否已实时打印（主程序据此跳过 print_ai）
    finish_answer()         打印收尾横线（与 print_ai 的页脚一致）
    selftest()              离线自测：不联网、不花钱、不动真文件

自测 / Self-test
----------------
    python stream_core.py            # 跑全部断言
    python stream_core.py --demo     # 用假流把渲染效果演示一遍（离线）

作者备注 / Design notes
-----------------------
· 只依赖标准库 + ``ui_core`` 的着色常量（ui_core 不可用时自动降级为无色，
  仍然能流式，只是不上色 —— 绝不因为「装饰」失效而丢掉「功能」）。
· 本模块**不 import openai**：client 由调用方传入。因此可以喂假流做离线测试。
"""

import io
import os
import re
import sys
import time

# ============ 着色常量：优先复用 ui_core，失败则无色兜底 ============
try:                                            # pragma: no cover
    from ui_core import (                       # noqa: F401
        RESET, CY, ITAL, BG, BY, BW, BM, BOLD, DIM, BK,
        bg256, paint, gradient, fg256, cost_tag, mark_footer,
        detect_mood, MOOD_COLOR, MOOD_EMOJI, TAG_COLOR,
        unicodedata as _ud,                     # ui_core 里 import 过，能取到就是同一份
    )
except Exception:                               # pragma: no cover
    RESET = CY = ITAL = BG = BY = BW = BM = BOLD = DIM = BK = ""
    def cost_tag():
        return ""

    def mark_footer():
        pass

    def bg256(n):
        return ""

    def fg256(n):
        return ""

    def paint(text, *styles):
        return str(text)

    def gradient(text, c1, c2):
        return str(text)

    def detect_mood(text):
        return "calm"

    MOOD_COLOR = {"calm": ((135, 206, 235), (70, 130, 180))}
    MOOD_EMOJI = {"calm": "🤖"}
    TAG_COLOR = {"red": "", "green": "", "yellow": "", "blue": "", "cyan": "",
                 "magenta": "", "white": "", "gray": "", "grey": "",
                 "bold": "", "italic": "", "underline": "", "dim": "", "strike": ""}

# 彩虹调色板（与 ui_core.rainbow 同一套 256 色轮转）
RAINBOW_PAL = [196, 202, 208, 214, 220, 226, 190, 154, 118, 82,
               46, 47, 51, 45, 39, 33, 27, 57, 93, 129, 165, 201]


# ============ 开关 ============
ENV_ENABLE = "FATFISH_STREAM"          # 1/0、on/off、true/false、yes/no
ENV_THINK = "FATFISH_STREAM_THINK"     # 是否显示 reasoning_content（思考链）

_FALSEY = ("0", "false", "no", "off", "n", "否", "关", "关闭")


def _env_bool(name, default=True):
    raw = os.environ.get(name)
    if raw is None or str(raw).strip() == "":
        return bool(default)
    return str(raw).strip().lower() not in _FALSEY


_STATE = {
    "enable": _env_bool(ENV_ENABLE, True),
    "think": _env_bool(ENV_THINK, True),
    "printed": False,          # 上一轮正文是否已经实时打印
    "streamed_rounds": 0,      # 计数器（诊断用）
    "fallback_rounds": 0,
}

# ---- 流式挂钩（操作台窗口靠它把增量实时灌进气泡）----
#   begin()        —— 一轮流式开始
#   delta(text)    —— 实际写到 out 的那一段（含 ANSI，窗口可原样喂给它的着色器）
#   end(aborted)   —— 结束；aborted=True 表示中途异常
_HOOKS = {"begin": None, "delta": None, "end": None}


def set_hooks(**kw):
    """注册流式挂钩：begin() / delta(text) / end(aborted)。

    只覆盖显式传进来的键；传 None 表示清除该挂钩。
    挂钩抛出的异常一律吞掉 —— 它绝不能影响渲染本身。
    """
    for k in ("begin", "delta", "end"):
        if k in kw:
            _HOOKS[k] = kw[k]


def _emit_hook(name, *args):
    fn = _HOOKS.get(name)
    if fn is None:
        return
    try:
        fn(*args)
    except Exception:
        pass


def enabled():
    """流式总开关（运行期可改，见 set_enabled）。"""
    return bool(_STATE["enable"])


def set_enabled(v):
    _STATE["enable"] = bool(v)
    return _STATE["enable"]


def think_enabled():
    """是否把模型的思考链（reasoning_content）也实时显示出来。"""
    return bool(_STATE["think"])


def set_think(v):
    _STATE["think"] = bool(v)
    return _STATE["think"]


def last_printed():
    """上一轮 ``stream_chat`` 是否已经把正文实时打上屏了。

    主程序在 ``print_ai(reply)`` 处用它决定：已经打过 → 只补页脚；
    没打过（回退路径 / 被关掉） → 照旧整段渲染。
    """
    return bool(_STATE["printed"])


def take_printed():
    """读取并**清零**该标志（每轮一问一答只用一次）。"""
    v = bool(_STATE["printed"])
    _STATE["printed"] = False
    return v


def reset_turn():
    """新一轮请求开始前清零「已打印」标志。

    ★ 为什么必须有：主程序只在「最终答复」那一步 read-and-clear 本标志，
    **工具调用轮与异常中断轮都不会消费它**，于是它可能带着上一轮的 True
    跨轮残留。下一轮若改走非流式（/stream off、或本轮开启了最终答复核验），
    主程序就会误判成「正文已经打过了」而把回复**整段吞掉**。
    所以每个新请求进门前一律清零 —— 代价为零，换来的是「绝不吞回复」。
    """
    _STATE["printed"] = False


def stats():
    """诊断用计数快照（/stream status 展示）。"""
    return dict(_STATE)


# ============ 与 OpenAI 非流式响应结构兼容的「壳」 ============
# 主循环会用到的字段（逐字核对过 FATHFISHI.py:3917-4006）：
#   msg.content            —— 可为 None
#   msg.tool_calls         —— 无工具调用时必须是 None（下游用 getattr(...) 判空）
#   tc.id / tc.function.name / tc.function.arguments
#   choice.finish_reason   —— "stop" / "length" / "tool_calls" / None
# 只要这几个字段对得上，下游代码一行都不用改。

class _ShimFunction(object):
    __slots__ = ("name", "arguments")

    def __init__(self, name, arguments):
        self.name = name
        self.arguments = arguments


class _ShimToolCall(object):
    __slots__ = ("id", "type", "function")

    def __init__(self, id_, name, arguments):
        self.id = id_
        self.type = "function"
        self.function = _ShimFunction(name, arguments)


class _ShimMessage(object):
    __slots__ = ("role", "content", "tool_calls")

    def __init__(self, content, tool_calls=None):
        self.role = "assistant"
        self.content = content
        self.tool_calls = tool_calls or None       # 空列表 → None（关键）


class _ShimChoice(object):
    __slots__ = ("index", "message", "finish_reason")

    def __init__(self, message, finish_reason=None):
        self.index = 0
        self.message = message
        self.finish_reason = finish_reason


class _ShimResponse(object):
    __slots__ = ("choices", "id", "model", "stream_info")

    def __init__(self, choice, model="", stream_info=None):
        self.choices = [choice]
        self.id = ""
        self.model = model
        self.stream_info = stream_info or {}


# ============ 增量渲染器 ============
# 输出风格刻意对齐 ui_core.print_ai：
#   ``` 围栏行 → 青色斜体（并翻转代码块状态）
#   代码块内   → 整行绿色
#   # 标题     → 去井号、黄色加粗、前置 ▎
#   - * + 列表 → 换成 •（保留缩进）
#   其余       → render_inline（{{}} 标记 + **粗体** + `代码`）
#   首尾       → 情绪判定后的渐变横线

_TAG_OPEN_RE = re.compile(r"^\{\{(\w+)\}\}")
_TAG_CLOSE_RE = re.compile(r"^\{\{/(\w+)\}\}")
_TAG_NAME_RE = re.compile(r"^\/?(\w+)\}\}")
_FENCE_RE = re.compile(r"^[ \t]*`{3,}")
_HEAD_RE = re.compile(r"^#{1,6}[ \t]")
_LIST_RE = re.compile(r"^([ \t]*)[-*+][ \t]")

# 回退窗口上限：超过就按字面吐出，避免「标记写错 → 正文被吞」
MAX_HOLD_TAG = 24          # {{xxxxx  —— 标签名最长认这么多字符
MAX_HOLD_BOLD = 240        # **加粗** —— 内容最长认这么多
MAX_HOLD_CODE = 120        # `代码`   —— 内容最长认这么多
MAX_LINE_PREFIX = 24       # 行首缩进最多判这么多列


class StreamPrinter(object):
    """把流式增量文本渲染成与 print_ai 同风格的终端输出。

    状态机分三层，逐层收窄：
      ① 行层：行首前缀（围栏 / 标题 / 列表 / 缩进）先「判定」再输出；
      ② 行内层：``{{}}`` 用标签栈实时上色；``**`` 与 ` `` ` 用有限回退窗口；
      ③ 代码块层：围栏之间整行绿色、**不解释任何标记**（与 print_ai 一致）。
    """

    def __init__(self, out=None, flush_interval=0.03, flush_chars=64,
                 mood_sample=64, show_think=None):
        self.out = out if out is not None else sys.stdout
        self.flush_interval = float(flush_interval)
        self.flush_chars = int(flush_chars)
        self.mood_sample_n = int(mood_sample)
        self.show_think = think_enabled() if show_think is None else bool(show_think)

        # ---- 输出缓冲 ----
        self._obuf = []
        self._olen = 0
        self._t_flush = time.time()

        # ---- 输入缓冲 / 行状态 ----
        self.buf = ""                 # 尚未处理的增量
        self.line_open = False        # 当前行是否已经输出过（行首前缀已判定）
        self.line_ansi = ""           # 当前行底色（代码绿 / 标题黄等）
        self.in_code = False          # 是否处于 ``` 围栏内
        self.line_buf = []            # 当前行的原始文本（判前缀用）

        # ---- 行内标记状态 ----
        self.tag_stack = []           # [(name, ansi)]
        self.rainbow_i = 0            # 彩虹标签内的字符计数

        # ---- 块级状态 ----
        self.header_done = False
        self.footer_done = False
        self.mood = "calm"
        self.c1, self.c2 = MOOD_COLOR.get("calm", ((135, 206, 235), (70, 130, 180)))
        self.emoji = MOOD_EMOJI.get("calm", "🤖")
        self.sample = []              # 情绪判定样本（取开头一小段）
        self.sample_n = 0
        self.n_chars = 0              # 正文净输出字符数
        self.n_think = 0
        self.think_open = False
        self._think_bol = True        # 思考链是否停在行首（缩进只在行首补一次）
        self.closed = False

    # ---------------------------------------------------------- 底层输出
    def _raw(self, s, force=True):
        """直接写（绕过缓冲），用于页眉页脚这种一次成型的东西。"""
        self._flush()
        try:
            self.out.write(s)
            self.out.flush()
        except Exception:
            pass

    def _w(self, s, force=False):
        """带缓冲的写：攒够字数或到时间就 flush（降低小碎片的 syscall 次数）。"""
        if not s:
            return
        self._obuf.append(s)
        self._olen += len(s)
        now = time.time()
        if force or self._olen >= self.flush_chars or (now - self._t_flush) >= self.flush_interval:
            self._flush()

    def _flush(self):
        if not self._obuf:
            return
        data = "".join(self._obuf)
        self._obuf = []
        self._olen = 0
        try:
            self.out.write(data)
            self.out.flush()
        except Exception:
            pass
        self._t_flush = time.time()
        # ★ 只有走 _flush 的才能真正写出去；页眉页脚走 _raw() 直接写、不经过这里。
        #   于是「挂钩收到的」恰好是**正文**，窗口气泡里不会混进那两条装饰横线。
        _emit_hook("delta", data)

    # ---------------------------------------------------------- 颜色状态
    def _ansi_now(self):
        return self.line_ansi + "".join(a for _n, a in self.tag_stack)

    def _set_color(self):
        """颜色状态变化后重新下发一次 ANSI（先 RESET 再叠加当前链）。"""
        self._w(RESET + self._ansi_now())

    # ---------------------------------------------------------- 页眉页脚
    def _ensure_header(self):
        """正文第一次上屏前，先打情绪横线（与 print_ai 同款）。"""
        if self.header_done:
            return
        self.header_done = True
        self.mood = detect_mood("".join(self.sample))
        self.c1, self.c2 = MOOD_COLOR.get(self.mood, self.c1)
        self.emoji = MOOD_EMOJI.get(self.mood, "🤖")
        head = " %s AI " % self.emoji
        self._raw("\n")
        self._raw(gradient("─" * 6 + head + "─" * 6, self.c1, self.c2) + "\n")

    def finish_answer(self):
        """打收尾横线（回答结束后调用一次，与 print_ai 的页脚一致）。"""
        if self.footer_done or not self.header_done:
            return
        self.footer_done = True
        head = " %s AI " % self.emoji
        # ★ 落款（2026-10-02）：收尾横线后面跟「时间 · 本轮耗时」
        mark_footer()
        self._raw(gradient("─" * (12 + len(head)), self.c1, self.c2)
                  + paint(" %s%s" % (time.strftime("%H:%M:%S"), cost_tag()),
                          BK, DIM) + "\n")
        self._raw("\n")

    # ---------------------------------------------------------- 思考链
    def feed_think(self, text):
        """reasoning_content 增量：灰色、**连续吐字**。

        ★ 修于 2026-10-02（bug：思考链「两三个字符就换行」）
          旧实现是「每个增量块各自成行」：
              for line in text.split("\\n"):
                  if line:
                      self._raw(paint("  " + line, BK) + "\\n")
          但推理链的传输粒度和正文一样是**一个 token 一个增量**
          （中文 1 字、英文 2~3 字母），而 split 在「不含换行」时永远返回
          长度为 1 的列表、`if line` 恒真 —— 末尾那个 "\\n" 是**硬加上去的**。
          于是每来一个 token 就换一行，屏幕上就是锯齿状的碎片。
          现在只在**源文本真的含 \\n** 时断行，行首缩进只在行首补一次。
        """
        if not self.show_think or not text:
            return
        if not self.think_open:
            self.think_open = True
            self._think_bol = True
            self._raw("\n")
            self._raw(paint("  💭 思考中…", BK, DIM) + "\n")
        self.n_think += len(text)

        out = []
        for i, seg in enumerate(text.replace("\r", "").split("\n")):
            if i:                                  # 段落边界：这才是该换行的地方
                out.append("\n")
                self._think_bol = True
            if not seg:
                continue
            if self._think_bol:                    # 行首只补一次缩进
                out.append(paint("  ", BK))
                self._think_bol = False
            out.append(paint(seg, BK))
        if out:
            self._raw("".join(out))

    def close_think(self):
        if self.think_open:
            if not self._think_bol:                # 停在半行 → 补一个换行收口
                self._raw("\n")
            self.think_open = False
            self._think_bol = True

    # ---------------------------------------------------------- 主入口
    def feed(self, text):
        """喂一段增量文本。"""
        if not text or self.closed:
            return
        text = text.replace("\r", "")          # 裸 CR 一律丢掉（模型不会输出它）
        if self.sample_n < self.mood_sample_n:
            self.sample.append(text)
            self.sample_n += len(text)
        self.buf += text
        self._pump()

    def close(self):
        """收尾：把手里扣着的半截标记按字面吐出，补齐行尾。绝不吞字。"""
        if self.closed:
            return
        self.closed = True
        if self.buf:
            # 剩下的一定是「疑似标记但没写完」的尾巴 —— 原样吐出，绝不吞字
            self._ensure_header()
            self.line_open = True
            self._emit_rich(self.buf)
            self.buf = ""
        if self.line_open:
            self._w(RESET, force=True)
            self._w("\n", force=True)
            self.line_open = False
            self.line_ansi = ""
        self._flush()

    def abort(self):
        """流中断 / 异常时收尾：把已渲染内容刷出去、闭合当前行。

        注意：**不打页脚** —— 回答是被截断的，页脚会造成「说完了」的错觉；
        也**不吞**手里扣着的半截标记（原样吐出更诚实）。
        """
        try:
            if self.buf:
                self.line_open = True
                self._emit_rich(self.buf)
                self.buf = ""
            if self.line_open:
                self._w(RESET, force=True)
                self._w("\n", force=True)
            self._flush()
        except Exception:
            pass
        self.line_open = False
        self.line_ansi = ""

    # ---------------------------------------------------------- 状态机
    def _pump(self):
        # ★ 顺序要点：``_decide_prefix`` 有可能**整行处理完并关行**（围栏 / 标题 /
        #   列表 / 空行）。那种情况下绝不能再跑 ``_emit_body`` —— 否则这行会被
        #   原样再吐一遍，且绕过行首变换（列表变不出 •）。所以每轮只做一件事，
        #   由循环重新判断当前该「定前缀」还是「吐正文」。
        while True:
            if self.line_open:
                if not self._emit_body():
                    return
            else:
                if not self._decide_prefix():
                    return

    def _decide_prefix(self):
        """行首判定：够信息了就输出前缀并开行；不够就先等着（返回 False）。"""
        buf = self.buf
        nl = buf.find("\n")
        if nl >= 0:
            head, tail, has_nl = buf[:nl], buf[nl + 1:], True
        else:
            head, tail, has_nl = buf, "", False

        kind = self._classify(head, has_nl)
        if kind is None:                     # 还需要更多字符才能判定
            return False

        line = head.rstrip("\r")
        self.line_open = True
        self._ensure_header()
        # ★ 这里**不能**把 head 整体摘走：没有换行时 head 只是「行的开头一段」。
        #   各分支只消费自己认领的那截前缀，剩下的留给 _emit_body 逐字吐出去。
        #   （早期版本整段摘走，导致 ## 标题 这类还没收完的行被当成完整行喷出去）

        if kind == "fence":
            # 只吃掉「缩进 + 反引号」，语言标签（python 之类）留给正文逐字吐，
            # 于是围栏行也能立刻上屏，不必等整行到齐。
            _lead = len(head) - len(head.lstrip(" \t"))
            _rest = head[_lead:]
            _take = _lead + (len(_rest) - len(_rest.lstrip("`")))
            self.line_ansi = CY + ITAL
            self._set_color()
            self._emit_raw_text(head[:_take])
            self.buf = buf[_take:]
            self.in_code = not self.in_code
            return True

        if kind == "code":
            # 代码块内：整行绿色、不解释任何标记；行尾换行交给 _emit_body 处理
            self.line_ansi = BG
            self._set_color()
            return True

        if kind == "heading":
            _stripped = re.sub(r"^#{1,6}[ \t]", "", head)
            _take = len(head) - len(_stripped)
            self.line_ansi = BY + BOLD
            self._set_color()
            self._emit_raw_text("▎")           # 与 print_ai 同款：去井号、加 ▎
            self.buf = buf[_take:]
            return True

        if kind == "list":
            _m = _LIST_RE.match(head)
            if _m:
                _take = _m.end()
                _prefix = _m.group(1) + "  • "   # 与 print_ai 同款：- → •
            else:                                # 理论上 _classify 不会放行到这里
                _take, _prefix = len(head), head
            self._emit_raw_text(_prefix)
            self.buf = buf[_take:]
            return True

        if kind == "blank":
            self._emit_raw_text(head)              # 空白行原样（与 print_ai 一致）
            self._w(RESET, force=True)
            self._w("\n", force=True)
            self.buf = buf[len(head) + 1:]         # 吃掉这一行 + 换行
            self._end_line()
            return True

        # 普通文本行：前缀无变换。★ 必须把行首那段**还回缓冲区**，
        # 交给行内渲染器逐字吐出 —— 早期版本漏了这一步，整行会被吃掉。
        self._ensure_header()
        self.buf = head + tail
        self.line_open = True
        return True

    def _classify(self, s, has_nl):
        """返回 'fence'|'code'|'heading'|'list'|'blank'|'text'，或 None=信息不足。"""
        # ① 围栏行优先级最高（进出代码块都靠它）
        lead = len(s) - len(s.lstrip(" \t"))
        rest = s[lead:]
        if rest.startswith("`"):
            nticks = len(rest) - len(rest.lstrip("`"))
            if nticks >= 3:
                return "fence"
            if not has_nl and rest.strip("`") == "" and len(s) <= MAX_LINE_PREFIX:
                return None                    # 可能是 `` 的开头，再等等
        elif not has_nl and rest == "" and len(s) <= MAX_LINE_PREFIX:
            return None                        # 全空白，可能是缩进的围栏，再等等

        if self.in_code:
            return "code"

        if s.strip() == "":
            return "blank" if has_nl else None if len(s) < 2 else "blank"

        # ② 标题：^#{1,6}\s
        if s.startswith("#"):
            k = len(s) - len(s.lstrip("#"))
            if k <= 6:
                if len(s) == k and not has_nl:
                    return None                # "###" 后面还可能有空格
                if len(s) == k and has_nl:
                    return "text"
                if s[k] in " \t":
                    return "heading"
            return "text"

        # ③ 列表：^\s*[-*+]\s
        m = _LIST_RE.match(s)
        if m:
            return "list"
        stripped = s.lstrip(" \t")
        if stripped and stripped[0] in "-*+":
            if len(stripped) == 1 and not has_nl:
                return None                    # "-" 后面还可能是空格
            return "text"
        if stripped == "" and not has_nl:
            return None if len(s) <= MAX_LINE_PREFIX else "text"
        return "text"

    def _end_line(self):
        """行结束：清行底色、复位标记栈（行内标记不跨行，与 render_inline 一致）。"""
        self.line_open = False
        self.line_ansi = ""
        self.tag_stack = []
        self.rainbow_i = 0

    # ---------------------------------------------------------- 行内输出
    def _emit_body(self):
        """把当前行能安全吐出的部分吐出去；需要更多字符时返回 False。

        「安全」的定义：
          · 不是半截标记（{{…、{{/…、**…、`…）—— 那些扣在手里；
          · 代码块内不做任何解释（整行原样，绿色）。
        """
        buf = self.buf
        if not buf:
            return False

        if self.in_code:
            nl = buf.find("\n")
            if nl < 0:
                self._emit_raw_text(buf)
                self.buf = ""
                return False
            self._emit_raw_text(buf[:nl])
            self.buf = buf[nl + 1:]
            self._w(RESET, force=True)
            self._w("\n", force=True)
            self._end_line()
            return True

        # ---- 找下一个「可疑起点」 ----
        idx = -1
        for pat in ("{{", "**", "`", "\n"):
            j = buf.find(pat)
            if j >= 0 and (idx < 0 or j < idx):
                idx = j
        if idx < 0:
            # ★ 没有「完整标记起点」，但**末尾那一个字符**可能正是标记的头一个
            #   字符：流式会把 "{{" 拆成 "{" + "{" 送过来，若此时把落单的 "{"
            #   当正文吐出去，后面的 {{tag}} 就会裸漏给用户。
            #   （这个 bug 就是 --demo 逐字喂演示抓出来的：分片自测看不见。）
            hold = 1 if (buf.endswith("{") or buf.endswith("*")) else 0
            if hold and len(buf) <= hold:
                return False                     # 整段就是那半个标记：再等等
            if hold:
                self._emit_rich(buf[:-hold])
                self.buf = buf[-hold:]
                return True
            self._emit_rich(buf)
            self.buf = ""
            return False
        if idx > 0:
            self._emit_rich(buf[:idx])
            self.buf = buf[idx:]
            return True

        # buf 以可疑起点开头
        if buf.startswith("{{"):
            return self._consume_tag()

        if buf.startswith("**"):
            return self._consume_inline("**", MAX_HOLD_BOLD,
                                        lambda t: paint(t, BW, BOLD))

        if buf.startswith("`"):
            if buf.startswith("``") and not buf.startswith("```"):
                self._emit_rich("`")            # 双反引号：当字面处理
                self.buf = buf[1:]
                return True
            return self._consume_inline("`", MAX_HOLD_CODE,
                                        lambda t: paint(t, BM, bg256(236)))

        # 换行
        self._w(RESET, force=True)
        self._w("\n", force=True)
        self.buf = buf[1:]
        self._end_line()
        return True

    def _consume_tag(self):
        """处理以 ``{{`` 开头的缓冲。返回 True=有进展（继续循环），False=等更多。"""
        buf = self.buf
        m = _TAG_OPEN_RE.match(buf)
        if m:
            name = m.group(1).lower()
            self.buf = buf[m.end():]
            self._push_tag(name)
            return True
        m = _TAG_CLOSE_RE.match(buf)
        if m:
            self.buf = buf[m.end():]
            self._pop_tag(m.group(1).lower())
            return True
        # 还没闭合：等等看（可能是 {{green}} / {{/green}} 被切开了）
        head = buf[:MAX_HOLD_TAG]
        if "}}" in head:
            # 有闭合符但不是合法标签名（如 {{a b}}）→ 当字面吐出，别吞
            end = buf.index("}}") + 2
            self._emit_rich(buf[:end])
            self.buf = buf[end:]
            return True
        if len(buf) < MAX_HOLD_TAG:
            return False
        self._emit_rich(buf[:2])                # 超长仍没闭合 → "{{" 当字面
        self.buf = buf[2:]
        return True

    def _consume_inline(self, mark, limit, color_fn):
        """``**加粗**`` / `` `代码` ``：找到闭合才上色，找不到就等（有上限）。"""
        buf = self.buf
        nl = buf.find("\n")
        end = buf.find(mark, len(mark))
        if end < 0 or (0 <= nl < end):
            # 本行内没有闭合（或先遇到换行）
            if nl >= 0 or len(buf) > limit:
                self._emit_rich(buf[:len(mark)])     # 当字面吐，绝不吞
                self.buf = buf[len(mark):]
                return True
            return False
        inner = buf[len(mark):end]
        if not inner:
            self._emit_rich(buf[:len(mark)])
            self.buf = buf[len(mark):]
            return True
        self._w(color_fn(inner) + RESET + self._ansi_now())
        self.buf = buf[end + len(mark):]
        return True

    # ---------------------------------------------------------- 标记栈
    def _push_tag(self, name):
        if name == "rainbow":
            self.tag_stack.append((name, ""))
            self.rainbow_i = 0
            self._set_color()
            return
        ansi = TAG_COLOR.get(name)
        if ansi is None:                       # 未知标签：吞掉开闭标签，内容原样
            self.tag_stack.append((name, ""))
            return
        self.tag_stack.append((name, ansi))
        self._set_color()

    def _pop_tag(self, name):
        # 从栈顶往下找最近的同名项（找不到就当字面输出，与 render_markup 一致）
        for i in range(len(self.tag_stack) - 1, -1, -1):
            if self.tag_stack[i][0] == name:
                del self.tag_stack[i:]
                if name == "rainbow":
                    self.rainbow_i = 0
                self._set_color()
                return
        self._emit_rich("{{/%s}}" % name)

    def _emit_rich(self, text):
        """行内正文输出：处理彩虹标签的逐字着色。"""
        if not text:
            return
        if self.tag_stack and self.tag_stack[-1][0] == "rainbow":
            out = []
            for ch in text:
                out.append(fg256(RAINBOW_PAL[self.rainbow_i % len(RAINBOW_PAL)]) + ch)
                self.rainbow_i += 1
            self._w("".join(out) + RESET + self._ansi_now())
            return
        self._emit_raw_text(text)

    def _emit_raw_text(self, text):
        if not text:
            return
        self.n_chars += len(text)
        self._w(text)


# ============ 流式请求 ============
def _merge_name(old, frag):
    """工具名分片合并：正常只发一次；异常情况下按「不重复追加」兜底。"""
    if not frag:
        return old
    if not old:
        return frag
    if frag == old or old.endswith(frag):
        return old
    return old + frag


def _merge_id(old, frag):
    if not frag:
        return old
    return frag if not old else old


def _guard_iter(it, printer):
    """包一层迭代器：流中途炸了，先把**已经渲染出来的内容**刷上屏、记录「已打印」，
    再把异常原样抛出。主程序据此报错，并知道「不要重试，否则会重复输出」。"""
    try:
        for item in it:
            yield item
    except BaseException:
        _STATE["printed"] = printer.n_chars > 0
        printer.abort()
        raise


def consume_stream(chunk_iter, printer, on_first=None):
    """把 SSE 增量喂给渲染器，并拼出与非流式等价的响应对象。

    返回 (response_shim, info)。info 里带 streamed / printed / chunks 等诊断位。
    """
    content_parts = []
    think_seen = False
    calls = {}                 # index -> {"id","name","args"}
    finish = None
    n_chunks = 0
    first_fired = False

    for chunk in _guard_iter(chunk_iter, printer):
        n_chunks += 1
        choices = getattr(chunk, "choices", None) or []
        if not choices:
            continue                       # usage-only 之类的空包
        ch = choices[0]
        fr = getattr(ch, "finish_reason", None)
        if fr:
            finish = fr
        delta = getattr(ch, "delta", None)
        if delta is None:
            continue

        # ---- 思考链 ----
        rc = getattr(delta, "reasoning_content", None)
        if rc:
            think_seen = True
            # ★ 修于 2026-10-02：思考链一开始显示，就把「等待模型响应」的转圈收掉。
            #   否则转圈钉在控制台左下角（console_status 直写屏幕），思考文字同时
            #   在滚行，两者抢同一块屏幕 → 「等待模型响应 14.6s」被嵌进思考文字中间。
            #   （旧实现只在收到正文/工具调用时才触发 on_first，整个思考阶段转圈不停。）
            if not first_fired and printer.show_think:
                first_fired = True
                if on_first:
                    try:
                        on_first()
                    except Exception:
                        pass
            printer.feed_think(rc)

        # ---- 疑问①：正文 ----
        txt = getattr(delta, "content", None)
        if txt:
            if not first_fired:
                first_fired = True
                if on_first:
                    try:
                        on_first()
                    except Exception:
                        pass
            printer.close_think()      # ★ 无条件收口（思考链可能先于正文出现）
            content_parts.append(txt)
            printer.feed(txt)
            continue

        # ---- 工具调用碎片 ----
        tcs = getattr(delta, "tool_calls", None)
        if tcs:
            if not first_fired:
                first_fired = True
                if on_first:
                    try:
                        on_first()
                    except Exception:
                        pass
            printer.close_think()      # ★ 无条件收口
            for t in tcs:
                idx = getattr(t, "index", 0)
                if idx is None:
                    idx = 0
                slot = calls.get(idx)
                if slot is None:
                    slot = {"id": "", "name": "", "args": ""}
                    calls[idx] = slot
                tid = getattr(t, "id", None)
                if tid:
                    slot["id"] = _merge_id(slot["id"], tid)
                fn = getattr(t, "function", None)
                if fn is not None:
                    slot["name"] = _merge_name(slot["name"], getattr(fn, "name", None))
                    frag = getattr(fn, "arguments", None)
                    if frag:
                        slot["args"] += frag

    # 收尾渲染
    printer.close_think()
    printer.close()

    content = "".join(content_parts) or None
    tc_list = None
    if calls:
        tc_list = []
        for idx in sorted(calls.keys()):
            slot = calls[idx]
            tc_list.append(_ShimToolCall(
                slot["id"] or ("call_%s" % idx), slot["name"], slot["args"] or "{}"))
    msg = _ShimMessage(content, tc_list)
    resp = _ShimResponse(_ShimChoice(msg, finish), stream_info={
        "streamed": True,
        "printed": printer.n_chars > 0,
        "chunks": n_chunks,
        "chars": printer.n_chars,
        "think_chars": printer.n_think,
        "think_seen": think_seen,
        "tool_calls": len(tc_list or []),
        "finish_reason": finish,
    })
    return resp, resp.stream_info


def stream_chat(client, model, messages, temperature=0.7, max_tokens=None,
                timeout=None, tools=None, out=None, on_first=None,
                printer=None, show_think=None, **extra):
    """跑一次流式 chat.completions，返回与非流式结构兼容的响应对象。

    参数与 ``client.chat.completions.create(...)`` 基本一一对应，多出来的是：
        on_first —— 收到**第一个**有效增量时的回调（主程序用它停掉转圈动画）
        out      —— 输出目标（默认 stdout）
        printer  —— 自备渲染器（测试用）
    """
    kwargs = {
        "model": model,
        "messages": messages,
        "temperature": temperature,
        "stream": True,
    }
    if max_tokens:
        kwargs["max_tokens"] = max_tokens
    if timeout:
        kwargs["timeout"] = timeout
    if tools:
        kwargs["tools"] = tools
    kwargs.update(extra)

    # ★ 每轮开始先清零「已打印」：万一 create() 阶段就抛异常（还没吐一个字），
    #   残留的 True 会骗过主程序的回退判断，导致这轮回复被整个跳过。
    _STATE["printed"] = False

    pr = printer or StreamPrinter(out=out, show_think=show_think)
    _emit_hook("begin")
    aborted = False
    try:
        stream = client.chat.completions.create(**kwargs)
        resp, info = consume_stream(stream, pr, on_first=on_first)
    except BaseException:
        aborted = True
        raise
    finally:
        _emit_hook("end", aborted)
    resp.model = model
    _STATE["printed"] = bool(info.get("printed"))
    _STATE["streamed_rounds"] += 1
    return resp


# ============ 离线自测（不联网 / 不花钱 / 不动真文件） ============
class _NS(object):
    """极简对象壳：给假流用。"""

    def __init__(self, **kw):
        self.__dict__.update(kw)


def _mk_chunk(content=None, reasoning=None, tool_calls=None, finish=None):
    delta = _NS(content=content, reasoning_content=reasoning, tool_calls=tool_calls)
    return _NS(choices=[_NS(delta=delta, finish_reason=finish)])


def _mk_tc(index, id_=None, name=None, args=None):
    return _NS(index=index, id=id_, function=_NS(name=name, arguments=args))


class _FakeCompletions(object):
    def __init__(self, chunks):
        self._chunks = chunks
        self.calls = []

    def create(self, **kw):
        self.calls.append(kw)
        chunks = self._chunks
        if isinstance(chunks, (list, tuple)):
            chunks = list(chunks)
        # ★ 不能写成 iter(list(chunks))：那会把生成器**提前求值**，
        #   于是「流中断」类异常跑到 create() 里去了，测不到真实的中断路径。
        return iter(chunks)


class _FakeClient(object):
    def __init__(self, chunks):
        self.chat = _NS(completions=_FakeCompletions(chunks))


def _selftest():
    fails = []

    def check(name, cond, detail=""):
        if cond:
            print("   ✅ %s" % name)
        else:
            fails.append(name)
            print("   ❌ %s  %s" % (name, detail))

    print("== stream_core 离线自测 ==")

    # ---------- ① 开关 ----------
    print("[1] 开关")
    old = _STATE["enable"]
    set_enabled(False)
    check("set_enabled(False) 生效", enabled() is False)
    set_enabled(True)
    check("set_enabled(True) 生效", enabled() is True)
    set_enabled(old)

    # ---------- ② 纯文本 ----------
    print("[2] 纯文本流式")
    buf = io.StringIO()
    pr = StreamPrinter(out=buf)
    pr.feed("你好")
    pr.feed("，世界")
    pr.close()
    pr.finish_answer()
    out = buf.getvalue()
    check("正文完整", "你好，世界" in out, repr(out))
    check("有页眉横线", "🤖" in out and out.count("─") >= 8, repr(out))

    # ---------- ③ 标记被切碎 ----------
    print("[3] {{}} 标记跨分片")
    buf = io.StringIO()
    pr = StreamPrinter(out=buf)
    for piece in ["{{gr", "een}}成", "功{{/gr", "een}}！"]:
        pr.feed(piece)
    pr.close()
    out = buf.getvalue()
    check("标记不裸漏", "{{" not in out and "}}" not in out, repr(out))
    check("内容保留", "成功" in out and "！" in out, repr(out))
    check("确实上了色", "\033[92m" in out, repr(out))

    # ---------- ④ 嵌套 + 彩虹 ----------
    print("[4] 嵌套与彩虹")
    buf = io.StringIO()
    pr = StreamPrinter(out=buf)
    pr.feed("{{bold}}{{red}}警告{{/red}}{{/bold}}")
    pr.feed(" {{rainbow}}彩虹{{/rainbow}}")
    pr.close()
    out = buf.getvalue()
    check("嵌套不裸漏", "{{" not in out, repr(out))
    check("内容在", all(c in out for c in "警告彩虹"), repr(out))

    # ---------- ⑤ 代码围栏 ----------
    print("[5] 代码围栏 / 标题 / 列表")
    buf = io.StringIO()
    pr = StreamPrinter(out=buf)
    pr.feed("# 标题\n")
    pr.feed("- 项目一\n- 项目二\n")
    pr.feed("```python\nprint(1)\n```\n")
    pr.feed("**粗体** 与 `代码`\n")
    pr.close()
    out = buf.getvalue()
    check("标题去了井号且带 ▎", "▎标题" in out, repr(out))
    check("列表变点号", "• 项目一" in out, repr(out))
    check("围栏行保留", "```python" in out, repr(out))
    check("代码行原样", "print(1)" in out, repr(out))
    check("行内粗体不裸漏", "**" not in out, repr(out))
    check("行内代码不裸漏反引号", "`代码`" not in out and "代码" in out, repr(out))

    # ---------- ⑥ 换行被切碎 ----------
    print("[6] 换行/前缀跨分片")
    buf = io.StringIO()
    pr = StreamPrinter(out=buf)
    for piece in ["##", " 小标", "题\n-", " 甲\n", "```", "py\n", "x=1\n", "`", "``\n"]:
        pr.feed(piece)
    pr.close()
    out = buf.getvalue()
    check("标题正确", "▎小标题" in out, repr(out))
    check("列表正确", "• 甲" in out, repr(out))
    check("围栏开合正确", "```py" in out and out.count("```") == 2, repr(out))

    # ---------- ⑦ 工具调用碎片拼装 ----------
    print("[7] 工具调用碎片")
    chunks = [
        _mk_chunk(tool_calls=[_mk_tc(0, id_="call_a", name="ws_read", args='{"pa')]),
        _mk_chunk(tool_calls=[_mk_tc(0, args='th": "a.py"}')]),
        _mk_chunk(tool_calls=[_mk_tc(1, id_="call_b", name="ws_list", args="{}")]),
        _mk_chunk(finish="tool_calls"),
    ]
    cli = _FakeClient(chunks)
    buf = io.StringIO()
    resp = stream_chat(cli, "fake-model", [{"role": "user", "content": "x"}],
                       tools=[{"type": "function"}], out=buf)
    msg = resp.choices[0].message
    check("无正文 → content is None", msg.content is None, repr(msg.content))
    check("拿到 2 个工具调用", len(msg.tool_calls) == 2, repr(msg.tool_calls))
    check("id 正确", msg.tool_calls[0].id == "call_a")
    check("name 正确", msg.tool_calls[0].function.name == "ws_read")
    check("arguments 拼接正确",
          msg.tool_calls[0].function.arguments == '{"path": "a.py"}',
          repr(msg.tool_calls[0].function.arguments))
    check("finish_reason 透传", resp.choices[0].finish_reason == "tool_calls")
    check("未被标记为已打印", last_printed() is False)
    check("请求里带了 stream=True", cli.chat.completions.calls[0].get("stream") is True)

    # ---------- ⑧ 正文 + 工具调用混合 ----------
    print("[8] 正文与工具调用混流")
    chunks = [
        _mk_chunk(content="我先看看"),
        _mk_chunk(content="文件。"),
        _mk_chunk(tool_calls=[_mk_tc(0, id_="c1", name="ws_read", args="{}")]),
        _mk_chunk(finish="tool_calls"),
    ]
    buf = io.StringIO()
    cli = _FakeClient(chunks)
    resp = stream_chat(cli, "m", [{"role": "user", "content": "x"}], out=buf)
    check("正文拼装", resp.choices[0].message.content == "我先看看文件。",
          repr(resp.choices[0].message.content))
    check("同时带工具调用", len(resp.choices[0].message.tool_calls) == 1)
    check("已实时打印", last_printed() is True)
    check("take_printed 清零", take_printed() is True and last_printed() is False)

    # ---------- ⑨ 思考链 ----------
    print("[9] 思考链")
    chunks = [_mk_chunk(reasoning="先想一下…"), _mk_chunk(content="答案"),
              _mk_chunk(finish="stop")]
    buf = io.StringIO()
    cli = _FakeClient(chunks)
    set_think(True)
    resp = stream_chat(cli, "m", [{"role": "user", "content": "x"}], out=buf)
    out = buf.getvalue()
    check("思考链已显示", "思考中" in out, repr(out))
    check("info 记录思考字符", resp.stream_info["think_chars"] > 0)
    check("答案正常", "答案" in out)

    # ★ 回归（2026-10-02）：推理链是按 token 增量送来的，绝不能「一片一行」
    _b2 = io.StringIO()
    _p2 = StreamPrinter(out=_b2, show_think=True)
    for _piece in ["用户", "问的", "是流", "式换", "行问", "题"]:
        _p2.feed_think(_piece)                 # 每片都不含 \n
    _p2.close_think()
    _plain2 = re.sub(r"\x1b\[[0-9;]*m", "", _b2.getvalue())
    _lines2 = [l for l in _plain2.split("\n") if l.strip()]
    check("★ 思考链连续吐字（不是一片一行）", len(_lines2) <= 2,
          "%d 行: %r" % (len(_lines2), _lines2[:6]))

    # ★ 回归：思考链一开始就要把转圈停掉（on_first 只触发一次，别重复）
    _hit = {"n": 0}
    _b3 = io.StringIO()
    _cli3 = _FakeClient([_mk_chunk(reasoning="先想一下…"),
                         _mk_chunk(content="答"), _mk_chunk(finish="stop")])
    stream_chat(_cli3, "m", [{"role": "user", "content": "x"}], out=_b3,
                on_first=lambda: _hit.__setitem__("n", _hit["n"] + 1))
    check("★ 思考链一开始就停转圈（on_first 触发一次）", _hit["n"] == 1,
          str(_hit["n"]))

    set_think(False)
    buf2 = io.StringIO()
    cli2 = _FakeClient([_mk_chunk(reasoning="不该出现"), _mk_chunk(content="答案2")])
    stream_chat(cli2, "m", [{"role": "user", "content": "x"}], out=buf2)
    check("关掉后不显示", "不该出现" not in buf2.getvalue())
    set_think(True)

    # ---------- ⑩ 异常传播 ----------
    print("[10] 流中断异常")
    def _boom():
        yield _mk_chunk(content="半句")
        raise RuntimeError("连接断了")
    cli = _FakeClient(_boom())
    buf = io.StringIO()
    try:
        stream_chat(cli, "m", [{"role": "user", "content": "x"}], out=buf)
        check("异常应向上抛", False, "没抛")
    except RuntimeError as e:
        check("异常向上抛（主程序据此报错）", "连接断了" in str(e))
        check("已吐出的半句保留", "半句" in buf.getvalue(), repr(buf.getvalue()))

    # ---------- ⑪ 空回复 ----------
    print("[11] 空回复")
    buf = io.StringIO()
    cli = _FakeClient([_mk_chunk(finish="stop")])
    resp = stream_chat(cli, "m", [{"role": "user", "content": "x"}], out=buf)
    check("content 为 None", resp.choices[0].message.content is None)
    check("未标记已打印（留给 print_ai）", last_printed() is False)

    # ---------- ⑫ 半截标记不吞字 ----------
    print("[12] 半截标记安全吐字")
    buf = io.StringIO()
    pr = StreamPrinter(out=buf)
    pr.feed("价格是 {{不是标签")
    pr.close()
    out = buf.getvalue()
    check("疑似标记按字面吐出", "{{不是标签" in out, repr(out))

    # ---------- ⑬ 逐字喂（最碎粒度） ----------
    print("[13] 逐字喂：单字符粒度下标记也不许裸漏")
    demo_text = (
        "你好呀，我是{{cyan}}肥鱼{{/cyan}}。\n\n"
        "先看一个{{bold}}重点{{/bold}}：\n"
        "- 支持 {{green}}实时上色{{/green}}\n"
        "- 支持 {{rainbow}}彩虹{{/rainbow}}与{{italic}}嵌套{{/italic}}\n\n"
        "# 代码也能流式\n"
        "```python\ndef hello():\n    return 'fish'\n```\n"
        "还有行内 **加粗** 与 `代码块` 以及 100*3 这种算式。\n"
    )
    buf = io.StringIO()
    pr = StreamPrinter(out=buf)
    for ch in demo_text:                 # ★ 一次只喂一个字符
        pr.feed(ch)
    pr.close()
    pr.finish_answer()
    out = buf.getvalue()
    check("无 {{ }} 残留", "{{" not in out and "}}" not in out, repr(out[:400]))
    check("无 ** 残留", "**" not in out, repr(out))
    check("反引号只剩围栏那 6 个", out.count("`") == 6, "%d 个" % out.count("`"))
    check("正文完整（彩虹为逐字着色，须逐字判定）",
          all(s in out for s in ("肥鱼", "实时上色", "与"))
          and all(c in out for c in "彩虹嵌套"), repr(out[:400]))
    check("算式里的单星号没被吃掉", "100*3" in out, repr(out))
    check("行的加粗确实上了色", "\033[97m" in out)
    check("标题/列表/围栏规则仍生效",
          "▎代码也能流式" in out and "• 支持" in out and "```python" in out)

    # ---------- ⑭ 流式挂钩（操作台窗口气泡靠它） ----------
    print("[14] 流式挂钩 begin / delta / end")
    got = {"b": 0, "d": [], "e": []}
    set_hooks(begin=lambda: got.__setitem__("b", got["b"] + 1),
              delta=lambda t: got["d"].append(t),
              end=lambda ab: got["e"].append(ab))
    cli = _FakeClient([_mk_chunk(content="甲"), _mk_chunk(content="乙"),
                       _mk_chunk(finish="stop")])
    buf = io.StringIO()
    stream_chat(cli, "m", [{"role": "user", "content": "x"}], out=buf)
    check("begin 触发 1 次", got["b"] == 1, str(got["b"]))
    check("delta 收到了正文", ("甲" in "".join(got["d"])) and ("乙" in "".join(got["d"])),
          repr(got["d"]))
    check("end 触发且未标记中断", got["e"] == [False], repr(got["e"]))
    check("挂钩内容与实际输出一致（正文都到了）",
          ("甲" in buf.getvalue()) and ("甲" in "".join(got["d"]))
          and ("乙" in buf.getvalue()) and ("乙" in "".join(got["d"])))
    check("页眉页脚没进 delta（只有正文）",
          "─" not in "".join(got["d"]), repr("".join(got["d"])[:80]))

    print("[15] 挂钩：中断要标记、抛异常不许影响渲染")
    got2 = {"e": []}
    set_hooks(end=lambda ab: got2["e"].append(ab))

    def _boom3():
        yield _mk_chunk(content="半句")
        raise RuntimeError("断流")
    try:
        stream_chat(_FakeClient(_boom3()), "m",
                    [{"role": "user", "content": "x"}], out=io.StringIO())
    except RuntimeError:
        pass
    check("中断时 end(True)", got2["e"] == [True], repr(got2["e"]))

    def _bad(_t):
        raise RuntimeError("挂钩自己炸了")
    set_hooks(delta=_bad)
    buf3 = io.StringIO()
    cli3 = _FakeClient([_mk_chunk(content="正文照常"), _mk_chunk(finish="stop")])
    stream_chat(cli3, "m", [{"role": "user", "content": "x"}], out=buf3)
    check("挂钩抛异常不影响正文", "正文照常" in buf3.getvalue(), repr(buf3.getvalue()))
    set_hooks(begin=None, delta=None, end=None)      # 收尾：清掉挂钩

    print()
    if fails:
        print("❌ 失败 %d 项：%s" % (len(fails), "、".join(fails)))
        return 1
    print("🎉 全部通过（离线、未联网、未花钱）")
    return 0


def _demo():
    """离线演示：跑一段假流，展示渲染观感（不联网）。"""
    print("=" * 70)
    print("stream_core 离线演示（假流，不联网）")
    print("=" * 70)
    pieces = [
        "你好呀，我是{{cyan}}肥鱼{{/cyan}}。\n\n",
        "先看一个{{bold}}重点{{/bold}}：\n",
        "- 支持 {{green}}实时上色{{/green}}\n",
        "- 支持 {{rainbow}}彩虹{{/rainbow}}与嵌套\n\n",
        "# 代码也能流式\n",
        "```python\n",
        "def hello():\n",
        "    return '🐟'\n",
        "```\n",
        "还有行内 **加粗** 与 `代码块`。\n",
    ]
    buf = sys.stdout
    pr = StreamPrinter(out=buf)
    for p in pieces:
        for ch in p:                 # 逐字喂，模拟最碎的情况
            pr.feed(ch)
            time.sleep(0.004)
    pr.close()
    pr.finish_answer()
    print("（演示结束：以上每一段都是逐字上屏的）")
    return 0


def main(argv=None):
    argv = list(sys.argv[1:] if argv is None else argv)
    if "--demo" in argv:
        return _demo()
    return _selftest()


if __name__ == "__main__":
    sys.exit(main())
