# -*- coding: utf-8 -*-
"""
chat_window.py —— 肥鱼「QQ 式」对话窗口 / FatFish Chat Window
=============================================================

一个独立可跑的 Tkinter 窗口：

    ┌──────────────────────────────────────────────────────┐
    │ 🐟 肥鱼 · 对话     [deepseek-flash]   📎 🖼 📌 🧹     │
    ├──────────────────────────────────────────────────────┤
    │ 对话 │ 日志                                          │   ← 两个页签
    │  ┌────────────┐                                      │
    │  │ 🐟 肥鱼     │            ┌────────────┐            │
    │  │ 气泡…       │            │ 我          │            │
    │  └────────────┘            │ 气泡…       │            │
    │                            └────────────┘            │
    ├──────────────────────────────────────────────────────┤
    │ 📎 a.png ✕   📄 notes.md ✕                            │   ← 附件条
    ├──────────────────────────────────────────────────────┤
    │ ┌──────────────────────────────┐  ┌────────┐          │
    │ │ 多行输入框（自动增高）        │  │  发送  │          │
    │ └──────────────────────────────┘  └────────┘          │
    ├──────────────────────────────────────────────────────┤
    │ Enter 发送 · Shift+Enter 换行 · 拖文件进来即附件        │
    └──────────────────────────────────────────────────────┘

三条设计约束 / Design constraints
---------------------------------
1) **纯加法，不动控制台。**
   窗口发出的消息只做两件事：① 塞进 `OUT_Q`；② 调用 `on_send` 回调（由主程序
   注入的「注入回车」函数，用 `console_is_idle + console_send_enter` 把卡在
   `input()` 上的主循环叫醒）。这与「后台任务播报」「QQ 消息唤醒」用的是同一套
   机制，所以**控制台照旧完全可用**，窗口关掉也不影响主流程。

2) **线程模型：Tk 只在自己的守护线程里被触碰。**
   主程序（主线程）单向 `put()` 文本进 `IN_Q`；Tk 线程用 `after()` 轮询出队并渲染。
   绝不从别的线程调 widget 方法。

3) **零硬依赖。**
   · 缺 PIL      → 缩略图退化为 📎 图标（其余功能全在）
   · 拖拽不可用  → 只留「📎 选择文件」按钮（不影响其它）
   · 缺 tkinter / 创建失败 → 整个模块自我禁用，主程序毫发无损

对外接口 / Public API
---------------------
    start(on_send=None, title=..., model=...)   起窗口（幂等）
    stop()                                       关窗口
    is_on()                                      窗口是否活着
    push_user(text) / push_ai(text)              气泡：我 / 肥鱼
    push_system(text)                            灰色系统提示气泡
    push_raw(text)                               原文进「日志」页（含 ANSI 上色）
    has_pending() / take_pending()               主程序取用户消息
    set_status(model=..., workspace=...)         改顶栏信息

自测 / Self-test
----------------
    python chat_window.py --demo      看效果（离线，自己跟自己对话）
    python chat_window.py --selftest  程序化断言（可无头运行，失败自动跳过）
"""

import os
import queue
import re
import sys
import threading
import time

# ---------------------------------------------------------------- 可选依赖
try:
    import tkinter as tk
    from tkinter import filedialog, font as tkfont
    from tkinter import ttk
    HAVE_TK = True
except Exception:                                   # pragma: no cover
    tk = None
    HAVE_TK = False

try:
    from PIL import Image, ImageGrab, ImageTk
    HAVE_PIL = True
except Exception:                                   # pragma: no cover
    HAVE_PIL = False


# ---------------------------------------------------------------- 主题
# ============ 古早 QQ（QQ2005）配色（2026-10-02 换肤）============
#   要点：消息区纯白、面板是经典 XP 灰、昵称用蓝色（老 QQ 的标志），
#         系统消息灰、消息之间一条淡灰细线，**不再有气泡底色**。
# ============ 配色：单一真源（2026-10-02 收敛）============
#   ★ 本文件**不再定义任何颜色**。所有颜色集中在：
#         fatfish_core/uicolors.py
#     那里取的是**命令提示符（cmd）实测调色板** —— Windows Terminal 内置默认方案
#     Campbell，与 HKCU\Console 的 ColorTable00-15 逐项一致（两条独立证据交叉验证）。
#     所以窗口里看到的颜色 = 终端里看到的颜色，两边同一个体系。
#   ★ 要换肤 → 只改 uicolors.py，别在这里加色值。
#   ★ 输入区与对话区之间那根线 = THEME["divider"]（固定纯白，用户指定，不参与派生）。
import os as _os
import sys as _sys

_HERE = _os.path.dirname(_os.path.abspath(__file__))
if _HERE not in _sys.path:
    _sys.path.insert(0, _HERE)

from fatfish_core.uicolors import (          # noqa: E402
    THEME, TAG_FG, RAINBOW,
    A_FG, A_FG_B, A_BG, A_BG_B,
    ANSI_FG, ANSI_FG_B, UI_FG, UI_BG, UI_DIM, WHITE_LINE,
    xterm256 as _xterm256,
)

# ============ 字体（可配置 · 2026-10-02）============
#   ★ 默认 12pt：与 Windows Terminal 默认字号对齐（实测终端字符格 ≈ 12pt），
#     所以对话窗口里的字和终端里的字**看着一样大**。
#   想调字号/字体，不用改代码 —— 在 .env 里加这几行，重启即可：
#       FATFISH_UI_FONT=微软雅黑        # 正文/中文
#       FATFISH_UI_SIZE=13              # 正文字号（pt）
#       FATFISH_UI_MONO=Consolas        # 等宽（代码块）
#       FATFISH_UI_MONO_SIZE=12
import os as _os


def _ui_font_str(name, default):
    """取环境变量里的字体名；空 / 不存在则用默认。"""
    try:
        v = str(_os.environ.get(name, "") or "").strip()
    except Exception:
        v = ""
    return v or default


def _ui_font_int(name, default):
    """取环境变量里的字号；非法值一律退回默认（绝不因配置写错而崩）。"""
    try:
        v = str(_os.environ.get(name, "") or "").strip()
        return int(v) if v else int(default)
    except Exception:
        return int(default)


# ★ 2026-10-02：默认字体与 cmd / Windows 终端对齐（Consolas）。
#   Consolas 不含中文字形 → Tk 按系统字体链接回退到中文字体，与 cmd 的行为一致。
#   想换字体：.env 里写 FATFISH_UI_FONT=Cascadia Mono（或 微软雅黑/等线 …）
_UI_FACE = _ui_font_str("FATFISH_UI_FONT", "Consolas")
_UI_SIZE = _ui_font_int("FATFISH_UI_SIZE", 12)
_MONO_FACE = _ui_font_str("FATFISH_UI_MONO", "Consolas")
_MONO_SIZE = _ui_font_int("FATFISH_UI_MONO_SIZE", 12)

FONT_UI = (_UI_FACE, _UI_SIZE)
FONT_UI_S = (_UI_FACE, max(8, _UI_SIZE - 1))     # 次级信息比正文小 1 号
FONT_UI_B = (_UI_FACE, _UI_SIZE, "bold")
FONT_MONO = (_MONO_FACE, _MONO_SIZE)


# ============ 个性签名（与 ui_core 共用一份；取不到就兜底一句）============
try:
    from ui_core import signature as signature
except Exception:
    def signature():
        return "签名是一种态度，我想我可以很酷。"

# 消息内 {{tag}} 标记 → Tk 颜色。
#   ★ 必须与 ui_core.TAG_COLOR 指同一批颜色：ui_core 把 {{red}} 映射成 ANSI **91**
#     （亮红），所以这里的 "red" 取的就是 ANSI 91 的实际颜色，两边严格一致。
#   ★ 2026-10-02 之前这里是一套自调的粉彩色（#ff6b6b / #69db7c …），与控制台发出的
#     ANSI 码不是一个体系 —— 表现为「窗口里的颜色跟终端里的对不上」。
#   ★ 定义已移至 fatfish_core/uicolors.py（见文件顶部导入）。
#   （旧注释「浅底（白底）可读的深色系」属于白底皮肤时代，已作废并删除。）

MAX_LOG_LINES = 4000          # 日志页最多保留多少行
MAX_BUBBLE_TEXT = 200000      # 单个气泡最多多少字符（防手滑喂超大文件）

# ============ 状态条（2026-10-02 新增）============
#   把原本只画在控制台里的两样东西搬进窗口：
#     · 联网搜索状态（🌐 / 🔍 / ⛔）—— 放在「消息栏前面」的左边
#     · 等待转圈 - \ | / 与计时        —— 放在同一行的右边（与控制台同一套节奏）
SPIN_FRAMES = "-\\|/"

# 每轮回复末尾的落款（跟在时间后面）：时间 + 本轮耗时（见 fatfish_core/roundtime.py）
try:
    from fatfish_core.roundtime import cost_tag
except Exception:                      # 独立运行（--demo）时退化为无耗时
    def cost_tag():
        return ""


# ================================================================ ANSI 渲染
ANSI_RE = re.compile(r"\x1b\[([0-9;]*)m")

# ANSI 基本色 / xterm-256 → hex。
#   ★ 一律取「命令提示符实测调色板」（= Windows Terminal Campbell），与终端同一套值，
#     因此窗口里渲染的 ANSI 文本与终端里看到的**完全一致**。
#   ★ 旧注释（「按深色底调过」「日志页现在是白底 → 基本色换成深色系」）是白底皮肤
#     与黑底皮肤来回换肤留下的矛盾地层，2026-10-02 一并删除。
#   ★ A_FG / A_FG_B / A_BG / A_BG_B / _xterm256 的定义已移至 fatfish_core/uicolors.py。


class AnsiPainter(object):
    """把带 ANSI 的文本画进 tk.Text，并把 SGR 转成 tag 上色。

    · tag 按 (fg,bg,bold,...) 缓存，不重复创建；
    · 跨调用保留「半截转义序列」（流式输出会把 \\x1b[3 和 2m 切开）。
    """

    def __init__(self, widget, base_font=None, tag_prefix="a"):
        self.w = widget
        self.prefix = tag_prefix
        self.base_font = base_font or FONT_MONO
        self._tags = {}
        self._carry = ""
        # ★ 颜色状态必须跨 feed 保持 —— ANSI 是「持续状态」，终端就是这么工作的。
        #   原实现每次 feed 都把 fg/bold 重置为 None/False，导致流式增量边界处掉色：
        #   一段彩色文字被切成两个增量后，后半段会变成默认色
        #   （现象：代码块里同一行「后半截没上色」）。
        self._fg = None
        self._bg = None
        self._bold = False
        self._italic = False
        self._under = False
        self._strike = False

    def tag(self, fg=None, bg=None, bold=False, italic=False,
            underline=False, strike=False, font=None):
        key = (fg, bg, bold, italic, underline, strike, font)
        t = self._tags.get(key)
        if t is not None:
            return t
        name = "%s_%d" % (self.prefix, len(self._tags))
        # tk 的 tag 名字不能含空格
        cfg = {}
        if fg:
            cfg["foreground"] = fg
        if bg:
            cfg["background"] = bg
        if font:
            cfg["font"] = font
        else:
            f = self.base_font
            if bold or italic:
                spec = list(f)
                style = []
                if bold:
                    style.append("bold")
                if italic:
                    style.append("italic")
                spec.append(" ".join(style))
                cfg["font"] = tuple(spec)
        if underline:
            cfg["underline"] = True
        if strike:
            cfg["overstrike"] = True
        self.w.tag_configure(name, **cfg)
        self._tags[key] = name
        return name

    def feed(self, text, end=None):
        """画一段文本（可含 ANSI）。返回本次插入的字符数。

        ★ 关键顺序：**先剥离结尾的半截转义序列**再解析。
          流式会把 `\\x1b[9` 和 `2m` 切开送来，若先解析就会把 `\\x1b[9`
          当普通正文吐出去（早期版本正是这个 bug）。扣住的那截留在
          `self._carry` 里，等下一段拼上；如果永远不再来，`flush()` 吐回。
        """
        text = self._carry + text
        self._carry = ""
        m2 = re.search(r"\x1b(\[[0-9;]*)?$", text)
        if m2:
            self._carry = text[m2.start():]
            text = text[:m2.start()]
        if not text:
            return 0

        # ★ 从实例状态「续上」，不要重置（见 __init__ 的说明）
        fg, bg = self._fg, self._bg
        bold, italic = self._bold, self._italic
        under, strike = self._under, self._strike
        pos = 0
        n = 0
        while True:
            m = ANSI_RE.search(text, pos)
            if not m:
                break
            chunk = text[pos:m.start()]
            if chunk:
                n += self._emit(chunk, fg, bg, bold, italic, under, strike, end)
            codes = m.group(1) or "0"
            fg, bg, bold, italic, under, strike = self._apply(
                codes, fg, bg, bold, italic, under, strike)
            pos = m.end()
        tail = text[pos:]
        if tail:
            n += self._emit(tail, fg, bg, bold, italic, under, strike, end)
        # ★ 存回实例状态，供下一次 feed 续用
        self._fg, self._bg = fg, bg
        self._bold, self._italic = bold, italic
        self._under, self._strike = under, strike
        return n

    def flush(self):
        """把扣着的半截转义按字面吐出（收尾用）。"""
        if self._carry:
            c = self._carry
            self._carry = ""
            return c
        return ""

    def _emit(self, s, fg, bg, bold, italic, under, strike, end):
        # 日志页不解释 bare \r（会串行），直接丢弃
        s = s.replace("\r", "")
        if not s:
            return 0
        t = self.tag(fg, bg, bold, italic, under, strike)
        self.w.insert("end" if end is None else end, s, (t,))
        return len(s)

    def _apply(self, codes, fg, bg, bold, italic, under, strike):
        parts = [p for p in codes.split(";")]
        i = 0
        while i < len(parts):
            c = parts[i]
            try:
                v = int(c) if c != "" else 0
            except ValueError:
                i += 1
                continue
            if v == 0:
                fg = bg = None
                bold = italic = under = strike = False
            elif v == 1:
                bold = True
            elif v == 2:
                fg = fg or THEME["dim_fg"]
            elif v == 3:
                italic = True
            elif v == 4:
                under = True
            elif v == 9:
                strike = True
            elif 30 <= v <= 37:
                fg = A_FG[v]
            elif 90 <= v <= 97:
                fg = A_FG_B[v]
            elif 40 <= v <= 47:
                bg = A_BG[v]
            elif 100 <= v <= 107:
                bg = A_BG_B[v]
            elif v == 38 and i + 1 < len(parts):
                if parts[i + 1] == "5" and i + 2 < len(parts):
                    fg = _xterm256(parts[i + 2]); i += 2
                elif parts[i + 1] == "2" and i + 4 < len(parts):
                    fg = "#%02x%02x%02x" % (int(parts[i + 2]), int(parts[i + 3]),
                                            int(parts[i + 4])); i += 4
            elif v == 48 and i + 1 < len(parts):
                if parts[i + 1] == "5" and i + 2 < len(parts):
                    bg = _xterm256(parts[i + 2]); i += 2
                elif parts[i + 1] == "2" and i + 4 < len(parts):
                    bg = "#%02x%02x%02x" % (int(parts[i + 2]), int(parts[i + 3]),
                                            int(parts[i + 4])); i += 4
            elif v == 39:
                fg = None
            elif v == 49:
                bg = None
            i += 1
        return fg, bg, bold, italic, under, strike


# ================================================================ 气泡正文渲染
INLINE_RE = re.compile(r"\{\{(\w+)\}\}|\{\{/(\w+)\}\}|\*\*(.+?)\*\*|`([^`\n]+)`")

MOOD_ICON = {"happy": "😊", "excited": "✨", "love": "❤️", "calm": "🐟",
             "sad": "🥺", "error": "⚠️", "code": "💻", "think": "🤔"}


# ============ 表情（古早 QQ 表情框 + 消息内嵌表情）============
#   数据源：qq_bridge/stickers/index.json（28 个现成表情，含 fish_* 系列）
#   用法  ：输入框里插 [表情:fish_ok]，肥鱼回复里也会带这种标记 → 渲染成图
EMOJI_RE = re.compile(r"\[表情[:：]\s*([A-Za-z0-9_\u4e00-\u9fff\-]+)\s*\]")
_STICKER_DIRS = ("qq_bridge/stickers", "stickers", "../qq_bridge/stickers",
                 "workspace/qq_bridge/stickers")
_STICKER = {"loaded": False, "items": [], "by_id": {}}
_STICKER_IMG = {}
_STICKER_KEEP = []          # ★ 必须留引用，否则 PhotoImage 被 GC → 图变空白


def sticker_dir():
    """找到表情目录；找不到返回空串。"""
    here = os.path.dirname(os.path.abspath(__file__))
    for rel in _STICKER_DIRS:
        p = os.path.normpath(os.path.join(here, rel))
        if os.path.isdir(p):
            return p
    return ""


def sticker_index():
    """读表情索引（失败返回空列表，界面不会崩）。"""
    if _STICKER["loaded"]:
        return _STICKER["items"]
    _STICKER["loaded"] = True
    try:
        import json as _json
        p = os.path.join(sticker_dir(), "index.json")
        if os.path.exists(p):
            with open(p, encoding="utf-8") as f:
                data = _json.load(f)
            items = data.get("items") if isinstance(data, dict) else data
            for it in (items or []):
                if isinstance(it, dict) and it.get("id") and it.get("file"):
                    _STICKER["items"].append(it)
                    _STICKER["by_id"][it["id"]] = it
    except Exception:
        pass
    return _STICKER["items"]


def sticker_image(sid, size=16):
    """取表情缩略图（tk.PhotoImage）；拿不到返回 None（调用方原样显示文字）。"""
    key = (str(sid), int(size))
    if key in _STICKER_IMG:
        return _STICKER_IMG[key]
    img = None
    try:
        import PIL.Image as _I
        import PIL.ImageTk as _IT
        it = _STICKER["by_id"].get(sid) if _STICKER["loaded"] else None
        if it is None:
            sticker_index()
            it = _STICKER["by_id"].get(sid)
        if it is not None:
            p = os.path.join(sticker_dir(), it["file"])
            if os.path.exists(p):
                im = _I.open(p)
                try:
                    im.seek(0)                  # GIF 取第一帧
                except Exception:
                    pass
                im = im.convert("RGBA")
                w, h = im.size
                s = min(w, h)                   # 居中裁成正方形
                im = im.crop(((w - s) // 2, (h - s) // 2,
                              (w + s) // 2, (h + s) // 2))
                im = im.resize((int(size), int(size)), _I.LANCZOS)
                img = _IT.PhotoImage(im)
    except Exception:
        img = None
    _STICKER_IMG[key] = img
    if img is not None:
        _STICKER_KEEP.append(img)
    return img


def _insert_rich(tw, s, tags=()):
    """把一段纯文本插进 Text：认出 [表情:id] → 内嵌小图；其余按 tags 插入。"""
    if not s:
        return
    pos = 0
    for m in EMOJI_RE.finditer(s):
        if m.start() > pos:
            tw.insert("end", s[pos:m.start()], tags)
        img = sticker_image(m.group(1), 16)
        if img is not None:
            tw.image_create("end", image=img, padx=1)
        else:
            tw.insert("end", m.group(0), tags)      # 认不出就原样显示，绝不吞字
        pos = m.end()
    if pos < len(s):
        tw.insert("end", s[pos:], tags)


def render_bubble(widget, text, base_fg=None, mono=FONT_MONO):
    """把肥鱼回复渲染进消息正文（tk.Text）。

    认得：``` 围栏（等宽块）、{{tag}} 上色、**加粗**、`行内代码`。
    不认得的标记原样保留（宁可多显示，不可吞字）。
    """
    tw = widget
    text = (text or "")[:MAX_BUBBLE_TEXT]
    lines = text.split("\n")
    in_code = False
    buf = []

    def flush_plain():
        if not buf:
            return
        s = "".join(buf)
        buf[:] = []
        pos = 0
        stack = []
        for m in INLINE_RE.finditer(s):
            if m.start() > pos:
                _insert_rich(tw, s[pos:m.start()], tuple(stack))
            g = m.groups()
            if g[0]:                       # {{tag}}
                name = g[0].lower()
                if name == "rainbow":
                    stack.append("mk_rainbow")
                else:
                    col = TAG_FG.get(name)
                    if col:
                        tag = "mk_" + name
                        tw.tag_configure(tag, foreground=col)
                        stack.append(tag)
            elif g[1]:                     # {{/tag}}
                name = g[1].lower()
                want = "mk_rainbow" if name == "rainbow" else "mk_" + name
                for i in range(len(stack) - 1, -1, -1):
                    if stack[i] == want:
                        del stack[i:]
                        break
            elif g[2] is not None:         # **bold**
                tag = "mk_bold"
                tw.tag_configure(tag, font=(FONT_UI_B[0], FONT_UI_B[1], "bold"))
                tw.insert("end", g[2], tuple(stack + [tag]))
            else:                          # `code`
                tag = "mk_code"
                tw.tag_configure(tag, font=mono, background=THEME["code_bg"],
                                 foreground=THEME["code_fg"])
                tw.insert("end", g[3], tuple(stack + [tag]))
            pos = m.end()
        if pos < len(s):
            _insert_rich(tw, s[pos:], tuple(stack))

    for ln in lines:
        if re.match(r"^\s*```", ln):
            flush_plain()
            in_code = not in_code
            continue
        if in_code:
            tag = "mk_block"
            # ★ 「框」：Text 的 tag 支持 relief/borderwidth/边距。
            #   黑底 + 1px 实线描边 + 左右留白 = 一个真正「框」住的代码块
            #   （边框颜色由 Tk 按系统色画，深底上自然呈现为浅色描边）。
            tw.tag_configure(tag, font=mono, background=THEME["code_bg"],
                             foreground=THEME["code_fg"], relief="solid",
                             borderwidth=1, lmargin1=10, lmargin2=10,
                             rmargin=10, spacing1=2, spacing3=2)
            tw.insert("end", ln + "\n", (tag,))
            continue
        # 标题 / 列表：与 print_ai 的观感对齐（标题用控制台亮黄加粗）
        if re.match(r"^#{1,6}\s", ln):
            flush_plain()
            tw.tag_configure("mk_head", foreground=THEME["head_fg"],
                             font=(FONT_UI_B[0], FONT_UI_B[1], "bold"))
            tw.insert("end", "▎" + re.sub(r"^#{1,6}\s", "", ln) + "\n",
                      ("mk_head",))
            continue
        if re.match(r"^\s*[-*+]\s", ln):
            buf.append(re.sub(r"^(\s*)[-*+]\s", r"\1  • ", ln) + "\n")
            continue
        buf.append(ln + "\n")
    flush_plain()


# ================================================================ 拖拽支持
class DropTarget(object):
    """Windows 的 WM_DROPFILES 拖拽接收（ctypes 子类化窗口过程）。

    纯附加能力：任何一步失败都静默降级（用户仍可用 📎 按钮）。
    """

    WM_DROPFILES = 0x0233
    GWLP_WNDPROC = -4

    def __init__(self, root, on_files):
        self.ok = False
        self.err = ""              # 失败原因（诊断用；成功时为空）
        self._old = None
        self._cb = None
        self.on_files = on_files
        if sys.platform != "win32":
            self.err = "非 Windows 平台，拖拽不可用（请用 📎 按钮）"
            return
        try:
            self._arm(root)
            self.ok = True
        except Exception as e:
            self.ok = False
            self.err = "%s: %s" % (type(e).__name__, e)

    def _arm(self, root):
        import ctypes
        from ctypes import wintypes
        self.ct = ctypes
        u32 = ctypes.windll.user32
        shell = ctypes.windll.shell32

        # ---- 找「最外层窗口」的 HWND ----
        #   Tk 在 Windows 上的层级是：最外层框架 → Tk 窗口(child) → 各种 widget。
        #   WM_DROPFILES 只发给**注册了拖拽的最外层窗口**，所以必须拿最外层。
        #   · winfo_id() 拿到的是 Tk 窗口（往往还有一层父框架）
        #   · wm_frame() 直接给出最外层框架（十六进制字符串）
        hwnd = 0
        try:
            hwnd = int(root.wm_frame(), 16)          # 最外层框架
        except Exception:
            hwnd = 0
        if not hwnd:
            hwnd = u32.GetParent(root.winfo_id()) or root.winfo_id()
        self.hwnd = hwnd

        LR = ctypes.c_ssize_t
        self._PROTO = ctypes.WINFUNCTYPE(LR, wintypes.HWND, ctypes.c_uint,
                                         wintypes.WPARAM, wintypes.LPARAM)

        def _proc(h, msg, wp, lp):
            if msg == self.WM_DROPFILES:
                # ★★ WM_DROPFILES 的 HDROP 在 **wParam**，lParam 不用。
                #    早期版本读了 lParam，于是永远拿到 0 个文件 —— 表现为
                #    「拖拽毫无反应但也不报错」，是最难查的那种静默失败。
                try:
                    paths = self._read_drop(wp)
                    if paths:
                        self.on_files(paths)
                except Exception as e:
                    self.err = "读取拖入文件失败：%s: %s" % (type(e).__name__, e)
                try:
                    # DragFinish 也在 shell32（不是 user32）
                    self.ct.windll.shell32.DragFinish(wintypes.HANDLE(wp))
                except Exception:
                    pass
                return 0
            try:
                return u32.CallWindowProcW(self._old, h, msg, wp, lp)
            except Exception:
                return 0

        self._cb = self._PROTO(_proc)            # 必须保引用，被 GC 回收会直接崩

        setter = getattr(u32, "SetWindowLongPtrW", None)
        if setter is None:
            setter = u32.SetWindowLongW
        setter.argtypes = [wintypes.HWND, ctypes.c_int, ctypes.c_void_p]
        setter.restype = ctypes.c_void_p
        u32.CallWindowProcW.argtypes = [ctypes.c_void_p, wintypes.HWND,
                                        ctypes.c_uint, wintypes.WPARAM,
                                        wintypes.LPARAM]
        u32.CallWindowProcW.restype = LR

        self._old = setter(wintypes.HWND(hwnd), self.GWLP_WNDPROC,
                           ctypes.cast(self._cb, ctypes.c_void_p))
        if not self._old:
            raise OSError("SetWindowLongPtrW 返回 0（窗口过程子类化失败）")
        self._wt = wintypes
        shell.DragAcceptFiles(wintypes.HWND(hwnd), True)

    def restore(self):
        """关窗前还原窗口过程 —— 否则 Tk 销毁窗口后仍留着指向 Python 回调的
        函数指针，极端情况下会在退出时踩到已释放的对象。"""
        if not self.ok or not self._old:
            return
        try:
            u32 = self.ct.windll.user32
            setter = getattr(u32, "SetWindowLongPtrW", None) or u32.SetWindowLongW
            setter.argtypes = [self._wt.HWND, self.ct.c_int, self.ct.c_void_p]
            setter.restype = self.ct.c_void_p
            setter(self._wt.HWND(self.hwnd), self.GWLP_WNDPROC, self._old)
            u32.DragAcceptFiles(self._wt.HWND(self.hwnd), False)
        except Exception:
            pass
        self.ok = False

    def _read_drop(self, hdrop):
        """从 HDROP 里读出拖入的文件全路径列表。

        ★ 这两个函数都在 **shell32**（DragQueryFileW / DragFinish），不是 user32。
          写成 user32 会抛 AttributeError —— 而早期版本把异常吞了，于是表现为
          「拖拽毫无反应」，非常难查。现在异常会记进 self.err。
        """
        from ctypes import wintypes
        ct = self.ct                        # ★ ctypes 是 _arm 里的局部导入，
        shell = ct.windll.shell32           #   这里必须走 self.ct，否则 NameError
        shell.DragQueryFileW.argtypes = [ct.c_void_p, ct.c_uint,
                                         ct.c_wchar_p, ct.c_uint]
        shell.DragQueryFileW.restype = ct.c_uint
        n = shell.DragQueryFileW(hdrop, 0xFFFFFFFF, None, 0)
        out = []
        buf = ct.create_unicode_buffer(2048)
        for i in range(int(n)):
            if shell.DragQueryFileW(hdrop, i, buf, 2048):
                out.append(buf.value)
        return out


# ================================================================ 窗口
class ChatWindow(object):
    def __init__(self, title="🐟 肥鱼 · 对话", model="", workspace="",
                 on_send=None, echo_console=True, topmost=False):
        self.title = title
        self.model = model
        self.workspace = workspace
        self.on_send = on_send
        self.echo_console = echo_console
        self.topmost = topmost

        self.root = None
        self.alive = False
        self.th = None
        self.attachments = []
        self.drop = None                 # 拖拽接收器（_build 里创建，可能为 None）
        self._thumb_refs = []            # 防止 PhotoImage 被 GC
        self._send_key_shift = False     # False = Enter 发送（QQ 默认）
        # ---- 操作台模式：报批确认条 / 流式气泡 ----
        self.ask_frame = None            # 确认条（_build 里创建）
        self._ask = None                 # 待答问题 {"id","options","default","title"}
        self._ask_seq = 0
        self._stream = None              # 进行中的流式气泡 {"body","painter","fit_pending"}
        self._stream_seq = 0
        self._echo_stream_to_log = True  # 流式正文是否也进「日志」页（默认进）
        # ---- 状态条：联网状态 + 等待转圈 ----
        self._lbl_net = None             # 左格：联网搜索状态
        self._lbl_spin = None            # 右格：- \ | / + 阶段 + 秒数
        self._spin = None                # {"t0","label","i"}
        self._spin_job = None            # 转圈的 after 句柄
        self._spin_clear_job = None      # 「收工结果」几秒后自动擦掉的句柄
        self._foot_done = False          # 本轮回复是否已落款（防流式收尾+整段推送落两次）

    # ------------------------------------------------------------ 生命周期
    def start(self):
        if not HAVE_TK:
            return False
        self.th = threading.Thread(target=self._run, name="fatfish-chat-ui",
                                   daemon=True)
        self.th.start()
        # 等窗口就绪（最多 6 秒）
        t0 = time.time()
        while time.time() - t0 < 6.0 and not self.alive:
            time.sleep(0.05)
        return self.alive

    def _run(self):
        try:
            self.root = tk.Tk()
        except Exception:
            self.alive = False
            return
        try:
            self._build()
            self.alive = True
            self.root.protocol("WM_DELETE_WINDOW", self._on_close)
            self.root.after(60, self._poll)
            self.root.mainloop()
        except Exception:
            self.alive = False
        finally:
            self.alive = False

    def _on_close(self):
        self.alive = False
        try:
            if self.drop is not None:
                self.drop.restore()
        except Exception:
            pass
        try:
            self.root.destroy()
        except Exception:
            pass

    def stop(self):
        self.alive = False
        try:
            if self.root:
                self.root.after(0, self._on_close)
        except Exception:
            pass

    # ------------------------------------------------------------ 界面
    def _build(self):
        r = self.root
        r.title(self.title)
        r.configure(bg=THEME["bg"])
        # 字大了以后照旧开 880x660 会显得挤，相应放宽（2026-10-02）
        r.geometry("980x720")
        r.minsize(640, 480)
        if self.topmost:
            try:
                r.attributes("-topmost", True)
            except Exception:
                pass

        # ---- 顶栏 ----
        top = tk.Frame(r, bg=THEME["panel"], height=44)
        top.pack(fill="x", side="top")
        top.pack_propagate(False)
        tk.Label(top, text=self.title, bg=THEME["panel"], fg=THEME["text"],
                 font=FONT_UI_B).pack(side="left", padx=(12, 6))
        self.lbl_model = tk.Label(top, text=self.model or "（未连接）",
                                  bg=THEME["panel"], fg=THEME["accent"],
                                  font=FONT_UI_S)
        self.lbl_model.pack(side="left", padx=6)
        for text, cmd, tip in (("🧹", self.clear_bubbles, "清空对话"),
                               ("📌", self.toggle_top, "置顶"),
                               ("🖼", self.add_clipboard_image, "粘贴剪贴板里的图片"),
                               ("📎", self.pick_files, "选择文件")):
            # 古早风：凸起按钮（relief=raised + 1px 边），不用扁平化
            b = tk.Button(top, text=text, command=cmd, bg=THEME["btn"],
                          fg=THEME["text"], font=FONT_UI, relief="raised",
                          activebackground=THEME["btn_hot"],
                          activeforeground=THEME["text"], bd=1,
                          padx=6, pady=0)
            b.pack(side="right", padx=3, pady=8)

        # ---- 页签：对话 / 日志 ----
        # ★ ttk 控件默认跟随系统主题（浅色）→ 在纯黑窗口里很扎眼，改成暗色
        try:
            _st = ttk.Style(r)
            try:
                _st.theme_use("clam")
            except Exception:
                pass
            _st.configure("TNotebook", background=THEME["panel"],
                          borderwidth=0, tabmargins=(2, 4, 2, 0))
            _st.configure("TNotebook.Tab", background=THEME["panel"],
                          foreground=THEME["text_dim"], padding=(12, 4),
                          borderwidth=0)
            _st.map("TNotebook.Tab",
                    background=[("selected", THEME["bg"])],
                    foreground=[("selected", THEME["text"])])
        except Exception:
            pass
        self.nb = ttk.Notebook(r)
        self.nb.pack(fill="both", expand=True, side="top")

        page1 = tk.Frame(self.nb, bg=THEME["bg"])
        page2 = tk.Frame(self.nb, bg=THEME["log_bg"])
        self.nb.add(page1, text="  对话  ")
        self.nb.add(page2, text="  日志  ")

        # 对话页：**一条连续的文本流**（成行一一排列，无气泡 / 无头像 / 无昵称行）
        self.chat = tk.Text(page1, bg=THEME["bg"], fg=THEME["text"],
                            font=FONT_UI, wrap="word", bd=0,
                            highlightthickness=0, padx=10, pady=6,
                            insertbackground=THEME["text"],
                            cursor="arrow", state="disabled")
        csb = tk.Scrollbar(page1, orient="vertical", command=self.chat.yview,
                           bg=THEME["panel"], troughcolor=THEME["bg"],
                           bd=0, relief="flat")
        self.chat.configure(yscrollcommand=csb.set)
        csb.pack(side="right", fill="y")
        self.chat.pack(side="left", fill="both", expand=True)
        # ★ 只读：放行滚动/复制键，拦掉一切编辑键
        self.chat.bind("<Key>", self._chat_key)
        # ★ 滚轮只绑自己身上 —— 不再用 bind_all（旧写法会连日志页/其它控件的
        #   滚轮一起抢走，表现为「一滚滚轮整个界面都在翻」）。
        self.chat.bind("<MouseWheel>", self._on_wheel)
        self.inner = self.chat        # 兼容旧的 self.inner 引用点

        # 日志页
        self.log = tk.Text(page2, bg=THEME["log_bg"], fg=THEME["log_text"],
                           font=FONT_MONO, wrap="word", bd=0,
                           insertbackground=THEME["text"], state="normal")
        lsb = tk.Scrollbar(page2, orient="vertical", command=self.log.yview,
                           bg=THEME["panel"], troughcolor=THEME["log_bg"],
                           bd=0, relief="flat")
        self.log.configure(yscrollcommand=lsb.set)
        lsb.pack(side="right", fill="y")
        self.log.pack(side="left", fill="both", expand=True)
        self.log_painter = AnsiPainter(self.log, FONT_MONO, "log")

        # ---- 报批确认条（有报批时才出现，就在输入区上方）----
        self.ask_frame = tk.Frame(r, bg=THEME["ask_bg"],
                                  highlightthickness=1,
                                  highlightbackground=THEME["ask_border"])
        self.ask_frame.pack(fill="x", side="top")
        self.ask_frame.pack_forget()

        # ---- 附件条 ----
        self.att_frame = tk.Frame(r, bg=THEME["panel"])
        self.att_frame.pack(fill="x", side="top")
        self.att_frame.pack_forget()          # 有附件时才显示

        # ---- 输入区 ----
        bottom = tk.Frame(r, bg=THEME["panel"])
        bottom.pack(fill="x", side="bottom")
        self._input_bottom = bottom          # 表情框挂这儿（不越过下面的白线）

        # ★★ 白线：把「对话区」与「输入区」分开（1px 纯白，横贯整宽）
        tk.Frame(bottom, bg=THEME["divider"], height=1).pack(fill="x", side="top")

        # ---- ★ 状态条（就在消息栏前面）：左 = 联网搜索状态，右 = 转圈 ----
        strip = tk.Frame(bottom, bg=THEME["panel"])
        strip.pack(fill="x", side="top", padx=10, pady=(4, 0))
        self._lbl_net = tk.Label(strip, text="🌐 联网：待命",
                                 bg=THEME["panel"], fg=THEME["accent"],
                                 font=FONT_UI_S, anchor="w")
        self._lbl_net.pack(side="left")
        self._lbl_spin = tk.Label(strip, text="", bg=THEME["panel"],
                                  fg=THEME["text_dim"], font=FONT_UI_S,
                                  anchor="e")
        self._lbl_spin.pack(side="right")

        # ---- 工具栏（古早 QQ 风：表情按钮）----
        bar = tk.Frame(bottom, bg=THEME["panel"])
        bar.pack(fill="x", side="top", padx=10, pady=(6, 0))
        tk.Button(bar, text="😊", command=self._toggle_emoji_panel,
                  bg=THEME["btn"], fg=THEME["text"], font=FONT_UI,
                  relief="raised", bd=1, padx=6, pady=1,
                  activebackground=THEME["btn_hot"]).pack(side="left")
        tk.Label(bar, text="表情", bg=THEME["panel"], fg=THEME["text_dim"],
                 font=FONT_UI_S).pack(side="left", padx=(4, 12))
        tk.Label(bar, text="（也可直接打 [表情:fish_ok]；肥鱼回复里的表情会显示成图）",
                 bg=THEME["panel"], fg=THEME["text_dim"],
                 font=FONT_UI_S).pack(side="left")
        wrap = tk.Frame(bottom, bg=THEME["panel"])
        wrap.pack(fill="x", padx=10, pady=(8, 4))
        self._input_wrap = wrap              # 表情框要插在这一行之前
        self.input = tk.Text(wrap, height=3, bg=THEME["input_bg"],
                             fg=THEME["text"], font=FONT_UI, wrap="word",
                             insertbackground=THEME["text"], relief="flat",
                             bd=0, padx=8, pady=6)
        self.input.pack(side="left", fill="both", expand=True)
        self.btn_send = tk.Button(wrap, text="发送\n(Enter)", command=self.send,
                                   bg=THEME["bubble_me"], fg=THEME["btn_fg"],
                                   font=FONT_UI, relief="flat", bd=0,
                                   width=8, activebackground=THEME["btn_go_hot"],
                                   activeforeground=THEME["btn_fg"])
        self.btn_send.pack(side="right", fill="y", padx=(8, 0))

        # 底栏：左边状态，右边「个性签名」（古早 QQ 的味儿）
        srow = tk.Frame(bottom, bg=THEME["panel"])
        srow.pack(fill="x", padx=12, pady=(0, 6))
        self.status = tk.Label(srow, text="", bg=THEME["panel"],
                               fg=THEME["text_dim"], font=FONT_UI_S,
                               anchor="w")
        self.status.pack(side="left")
        self.lbl_sign = tk.Label(srow, text="", bg=THEME["panel"],
                                 fg=THEME["text_dim"], font=FONT_UI_S,
                                 anchor="e")
        self.lbl_sign.pack(side="right")
        self.signature = signature()
        self._refresh_status()
        self._rotate_signature()

        # ---- 按键 ----
        self.input.bind("<Return>", self._on_return)
        self.input.bind("<Shift-Return>", self._on_shift_return)
        self.input.bind("<Control-Return>", self._on_shift_return)
        self.input.bind("<Alt-Return>", self._on_shift_return)
        self.input.bind("<Control-v>", self._on_paste)
        self.input.bind("<KeyRelease>", lambda e: self._refresh_status())
        self.input.bind("<Control-a>", self._sel_all)
        self.input.focus_set()

        # ---- 拖拽 ----
        self.drop = DropTarget(r, self._on_drop_files)
        if self.drop.ok:
            self.push_system("🖱 直接把文件拖进窗口即可作为附件（也可以用 📎 按钮）")

        self.push_system("👋 欢迎回来。Enter 发送 · Shift+Enter 换行 · "
                         "拖文件/图片进来即可附加。")

    # ---- 滚动 ----
    def _chat_key(self, e):
        """对话区只读：放行滚动键与 Ctrl 组合（复制/全选），其余编辑键一律吞掉。"""
        try:
            if e.keysym in ("Up", "Down", "Left", "Right", "Prior", "Next",
                            "Home", "End") or (e.state & 0x4):
                return None
        except Exception:
            pass
        return "break"

    def _on_wheel(self, e):
        """滚轮只滚对话区本身（绑定在自己身上，不再全局抢别人的滚轮）。"""
        try:
            self.chat.yview_scroll(int(-1 * (e.delta / 120)), "units")
        except Exception:
            pass
        return "break"

    def _scroll_bottom(self):
        try:
            self.chat.see("end")
        except Exception:
            pass

    # ------------------------------------------ 状态条：联网状态 + 转圈
    def set_net_status(self, text):
        """左格：联网搜索状态（🌐 正在联网搜索… / 🔍 需要联网 / ⛔ 无需联网）。"""
        try:
            self._lbl_net.configure(text=str(text or ""))
        except Exception:
            pass

    def _spin_paint(self):
        st = self._spin
        if not st:
            return
        try:
            ch = SPIN_FRAMES[st["i"] % len(SPIN_FRAMES)]
            self._lbl_spin.configure(
                text="%s  %s  %.1fs" % (ch, st["label"], time.time() - st["t0"]),
                fg=THEME["accent"])
        except Exception:
            pass

    def spin_begin(self, label=None):
        """右格：起一个 - \\ | / 转圈（与控制台同一套节奏）。"""
        self._cancel_spin_clear()
        self._spin = {"t0": time.time(), "label": str(label or "处理中"), "i": 0}
        self._spin_paint()
        self._spin_tick()

    def spin_label(self, label):
        """换阶段文案（下一帧生效）。"""
        if self._spin and label:
            self._spin["label"] = str(label)

    def spin_end(self, ok=True, label=None, elapsed=0.0):
        """收工：结果先留在状态条上，4 秒后自动擦掉。"""
        st = self._spin
        self._spin = None
        try:
            if self._spin_job:
                self.root.after_cancel(self._spin_job)
        except Exception:
            pass
        self._spin_job = None
        if st is None:
            return
        try:
            el = float(elapsed) or (time.time() - st["t0"])
            self._lbl_spin.configure(
                text="%s %s（%.1fs）" % ("✅" if ok else "⚠️ ", label or st["label"], el),
                fg=THEME["text_dim"])
        except Exception:
            pass
        self._cancel_spin_clear()
        try:
            self._spin_clear_job = self.root.after(4000, self._spin_clear)
        except Exception:
            pass

    def _spin_tick(self):
        st = self._spin
        if st is None:
            return
        st["i"] += 1
        self._spin_paint()
        try:
            self._spin_job = self.root.after(120, self._spin_tick)
        except Exception:
            self._spin_job = None

    def _spin_clear(self):
        self._spin_clear_job = None
        try:
            self._lbl_spin.configure(text="")
        except Exception:
            pass

    def _cancel_spin_clear(self):
        try:
            if self._spin_clear_job:
                self.root.after_cancel(self._spin_clear_job)
        except Exception:
            pass
        self._spin_clear_job = None

    # ------------------------------------------ 落款：时间 + 本轮标记
    def _foot(self):
        """回复末尾的落款「—— 16:52:31 · ⏱️ 本轮 44.8s」。

        ★ 去重按「轮」不按「时间」：同一条回复可能经过两条路径 ——
          · 流式：stream_end 落一次款；
          · 紧随其后的整段推送（push_ai，主程序在流式被跳过时才会走）。
          用 _foot_done 记住「这轮已经落过款」，只有新的用户/系统消息
          才会把它清掉。这样既不会重复落款，也不会因为两条回复挨得太近
          而吞掉落款（早先的「2 秒时间闸门」就有这个毛病）。
        """
        if self._foot_done:
            return
        self._foot_done = True
        tw = self.chat
        try:
            tw.configure(state="normal")
        except Exception:
            pass
        try:
            tw.tag_configure("mk_foot", foreground=THEME["text_dim"],
                             font=FONT_UI_S, spacing3=6)
            tw.insert("end", "        —— %s%s\n"
                      % (time.strftime("%H:%M:%S"), cost_tag()), ("mk_foot",))
        except Exception:
            pass
        try:
            tw.configure(state="disabled")
        except Exception:
            pass

    # ------------------------------------------ 对话流（成行排列 · 无气泡）
    def _prefix(self, kind):
        """写说话人前缀 —— 颜色一律取**控制台原色**：我=亮白，肥鱼=青，系统=暗灰。

        spacing1 给「上边距」当段落间距，于是不必插空行，
        消息自然一条接一条地往下排（成行一一排列）。
        """
        tw = self.chat
        if kind != "ai":
            # 新一轮（用户 / 系统）开始 → 允许下一条 AI 回复重新落款
            self._foot_done = False
        cols = {"me": THEME["nick_me"], "ai": THEME["nick_ai"],
                "sys": THEME["sys_fg"]}
        label = {"me": "我 › ", "ai": "🐟 ", "sys": "· "}.get(kind, "")
        tag = "spk_" + kind
        fnt = FONT_UI_B if kind == "me" else FONT_UI
        try:
            tw.tag_configure(tag, foreground=cols.get(kind, THEME["text"]),
                             font=fnt, spacing1=6)
        except Exception:
            pass
        try:
            tw.configure(state="normal")
        except Exception:
            pass
        tw.insert("end", label, (tag,))

    def _bubble(self, kind, text, who=None):
        """写一条消息 —— **成行排列**：没有气泡、头像、昵称行、时间与分隔线。

        kind: 'me' | 'ai' | 'sys'
        """
        tw = self.chat
        try:
            tw.configure(state="normal")
        except Exception:
            pass
        self._prefix(kind)
        if kind == "sys":
            tw.tag_configure("sysc", foreground=THEME["sys_fg"])
            tw.insert("end", str(text) + "\n", ("sysc",))
        else:
            render_bubble(tw, text)
            if kind == "ai":
                self._foot()               # ★ 回顾末尾落款：时间 + 本轮标记
        try:
            tw.configure(state="disabled")
        except Exception:
            pass
        self._scroll_bottom()
        self._refresh_status()
        return tw

    # ------------------------------------------ 表情框（古早 QQ 表情面板）
    def _toggle_emoji_panel(self):
        """开 / 关表情框。"""
        p = getattr(self, "emoji_panel", None)
        if p is not None:
            try:
                if p.winfo_ismapped():
                    p.pack_forget()
                else:
                    _b = getattr(self, "_input_wrap", None)
                    if _b is not None:
                        p.pack(fill="x", side="top", before=_b)
                    else:
                        p.pack(fill="x", side="bottom")
                return
            except Exception:
                self.emoji_panel = None
        try:
            self._build_emoji_panel()
        except Exception:
            pass

    def _build_emoji_panel(self):
        """7 列表情格子；点一下往输入框插 [表情:id]。"""
        items = sticker_index()
        _parent = getattr(self, "_input_bottom", None) or self.root
        p = tk.Frame(_parent, bg=THEME["panel"], highlightthickness=1,
                     highlightbackground=THEME["border"])
        cols = 7
        if not items:
            tk.Label(p, text="（没找到表情库：qq_bridge/stickers/index.json）",
                     bg=THEME["panel"], fg=THEME["text_dim"],
                     font=FONT_UI_S).pack(padx=8, pady=6)
        for i, it in enumerate(items):
            img = sticker_image(it["id"], 24)
            b = tk.Button(p, bg=THEME["panel"], relief="flat", bd=0,
                          padx=2, pady=2, activebackground=THEME["btn_hot"],
                          command=lambda s=it["id"]: self._insert_emoji(s),
                          text=("" if img is not None else it["id"][:3]))
            if img is not None:
                b.configure(image=img)
                b.image = img          # ★ 留引用，防 GC
            try:
                b.grid(row=i // cols, column=i % cols, sticky="nsew",
                       padx=1, pady=1)
            except Exception:
                pass
        self.emoji_panel = p
        _before = getattr(self, "_input_wrap", None)
        if _before is not None:
            p.pack(fill="x", side="top", before=_before)   # 工具栏与输入框之间
        else:
            p.pack(fill="x", side="bottom")

    def _insert_emoji(self, sid):
        """把 [表情:id] 插到输入框光标处。"""
        try:
            self.input.insert("insert", "[表情:%s]" % sid)
            self.input.focus_set()
            self._refresh_status()
        except Exception:
            pass

    # ------------------------------------------------------ 操作台：报批确认条
    def ask(self, text, options=None, default="n", title="需要你确认"):
        """弹一条确认条，等用户在窗口里点按钮（或敲 y/a/n）。

        选项默认是 y / a / n 三档，与主程序报批按键语义完全一致。
        返回 ask_id；答案由 ANSWER_Q 交给主程序。
        """
        options = options or [("y", "✅ 批准"), ("n", "🚫 拒绝")]
        self._ask_seq += 1
        aid = self._ask_seq
        self._ask = {"id": aid, "options": list(options), "default": default,
                     "title": title}
        self.push_system("❓ %s\n%s" % (title, text))   # 对话区留痕
        self._render_ask_bar(text, options, title)
        return aid

    def _render_ask_bar(self, text, options, title):
        if self.ask_frame is None:
            return
        for w in self.ask_frame.winfo_children():
            w.destroy()
        row = tk.Frame(self.ask_frame, bg=THEME["ask_bg"])
        row.pack(fill="x", padx=10, pady=(6, 0))
        tk.Label(row, text="🔐 " + title, bg=THEME["ask_bg"],
                 fg=THEME["ask_border"], font=FONT_UI_B).pack(side="left")
        tk.Label(self.ask_frame, text=text, bg=THEME["ask_bg"], fg=THEME["text"],
                 font=FONT_UI, justify="left", anchor="w",
                 wraplength=780).pack(fill="x", padx=12, pady=(2, 4))
        btns = tk.Frame(self.ask_frame, bg=THEME["ask_bg"])
        btns.pack(fill="x", padx=10, pady=(0, 8))
        for val, label in options:
            is_def = (self._ask or {}).get("default") == val
            tk.Button(btns, text="%s  [%s]" % (label, val),
                      command=lambda v=val: self._answer_ask(v),
                      bg=THEME["bubble_me"] if is_def else THEME["btn_hot"],
                      fg=THEME["btn_fg"], font=FONT_UI, relief="flat", bd=0,
                      padx=12, pady=4, activebackground=THEME["btn_ask_hot"],
                      activeforeground=THEME["btn_fg"]).pack(side="left", padx=(0, 8))
        tk.Label(btns, text="（也可直接在下面输入 y / a / n 回车）",
                 bg=THEME["ask_bg"], fg=THEME["text_dim"],
                 font=FONT_UI_S).pack(side="left", padx=6)
        self.ask_frame.pack(fill="x", side="top")
        try:
            self.input.focus_set()
        except Exception:
            pass

    def _answer_ask(self, value):
        """回答当前问题（按钮点击 / 键盘输入都走这里）。"""
        ask = self._ask
        if not ask:
            return False
        self._ask = None
        if self.ask_frame is not None:
            for w in self.ask_frame.winfo_children():
                w.destroy()
            self.ask_frame.pack_forget()
        self.push_system("👉 你的答复：%s" % value)
        ANSWER_Q.put({"kind": "answer", "id": ask["id"], "value": value})
        self._refresh_status()
        return True

    def cancel_ask(self, why="已取消"):
        """撤销当前问题（按默认值收场）—— 主程序那边会拿到默认答案，绝不悬空。"""
        ask = self._ask
        if not ask:
            return
        self._ask = None
        if self.ask_frame is not None:
            for w in self.ask_frame.winfo_children():
                w.destroy()
            self.ask_frame.pack_forget()
        self.push_system("⌛ %s（按默认 %s 处理）" % (why, ask.get("default", "n")))
        ANSWER_Q.put({"kind": "answer", "id": ask["id"],
                      "value": ask.get("default", "n")})

    def has_ask(self):
        return self._ask is not None

    # ------------------------------------------------------ 操作台：流式气泡
    def stream_begin(self, who=None):
        """开一条流式输出 —— 直接写进对话流（不再单开气泡）。"""
        self.stream_end()
        self._stream_seq += 1
        tw = self.chat
        self._prefix("ai")                 # 先落「🐟 」前缀，正文紧随其后
        painter = AnsiPainter(tw, FONT_MONO, "st%d" % self._stream_seq)
        self._stream = {"body": tw, "painter": painter,
                        "fit_pending": False, "n": 0}
        self._scroll_bottom()
        return self._stream_seq

    def stream_delta(self, text):
        """把一段增量喂进对话流（没有开流就自动开一条）。"""
        if not text:
            return
        if self._stream is None:
            self.stream_begin()
        st = self._stream
        try:
            st["painter"].feed(text)
        except Exception:
            pass
        st["n"] += len(text)
        # 重绘比较贵 → 节流合并：看得见在长，又不拖慢界面
        if not st.get("fit_pending"):
            st["fit_pending"] = True
            try:
                self.root.after(90, self._stream_fit)
            except Exception:
                st["fit_pending"] = False

    def _stream_fit(self):
        st = self._stream
        if not st:
            return
        st["fit_pending"] = False
        self._scroll_bottom()

    def stream_end(self, aborted=False):
        """收口当前流式输出。返回本轮灌进去的字符数。"""
        st = self._stream
        if not st:
            return 0
        self._stream = None
        tw = st["body"]
        try:
            tail = st["painter"].flush()
            if tail:
                st["painter"].feed(tail)
            if aborted:
                tw.insert("end", "\n⚠️ （输出中断）", ("st_abort",))
                tw.tag_configure("st_abort", foreground=THEME["abort_fg"])
            tw.insert("end", "\n")
            self._foot()                   # ★ 流式收尾也落款（2 秒闸门防重复）
            tw.configure(state="disabled")
        except Exception:
            pass
        self._scroll_bottom()
        self._refresh_status()
        return st.get("n", 0)

    def stream_active(self):
        return self._stream is not None

    def stream_chars(self):
        st = self._stream
        return int(st.get("n", 0)) if st else 0

    def _fit(self, body=None):
        """（成行排列后不再需要按内容调高度）保留接口：滚到底即可。"""
        self._scroll_bottom()

    # ------------------------------------------------------------ 供外部调用
    def push_user(self, text):
        self._bubble("me", text)

    def push_ai(self, text):
        # 不去渲染 {{}} 以外的 ANSI：正文里只要标记就够了
        self._bubble("ai", text)
        self._rotate_signature()        # 每来一条回复，换一句个性签名

    def push_system(self, text):
        self._bubble("sys", text)

    def push_raw(self, text):
        """原文进日志页（含 ANSI）。"""
        if not text:
            return
        at_bottom = True
        try:
            at_bottom = self.log.yview()[1] > 0.995
        except Exception:
            pass
        self.log.configure(state="normal")
        self.log_painter.feed(text)
        # 限制行数
        try:
            total = int(self.log.index("end-1c").split(".")[0])
            if total > MAX_LOG_LINES:
                self.log.delete("1.0", "%d.0" % (total - MAX_LOG_LINES))
        except Exception:
            pass
        self.log.configure(state="disabled")
        if at_bottom:
            try:
                self.log.see("end")
            except Exception:
                pass

    def set_status(self, model=None, workspace=None):
        if model is not None:
            self.model = model
            try:
                self.lbl_model.configure(text=model or "（未连接）")
            except Exception:
                pass
        if workspace is not None:
            self.workspace = workspace
        self._refresh_status()

    # ------------------------------------------------------------ 附件
    def _refresh_att(self):
        for w in self.att_frame.winfo_children():
            w.destroy()
        if not self.attachments:
            self.att_frame.pack_forget()
            self._refresh_status()
            return
        self.att_frame.pack(fill="x", side="top", before=self.nb)
        tk.Label(self.att_frame, text="📎 附件 %d 件：" % len(self.attachments),
                 bg=THEME["panel"], fg=THEME["accent"],
                 font=FONT_UI_S).pack(side="left", padx=(10, 4), pady=4)
        for i, p in enumerate(list(self.attachments)):
            chip = tk.Frame(self.att_frame, bg=THEME["btn"])
            chip.pack(side="left", padx=3, pady=4)
            icon = "🖼" if _is_image(p) else "📄"
            tk.Label(chip, text="%s %s" % (icon, os.path.basename(p)),
                     bg=THEME["btn"], fg=THEME["text"],
                     font=FONT_UI_S).pack(side="left", padx=(6, 2))
            tk.Button(chip, text="✕", command=lambda k=i: self.remove_att(k),
                      bg=THEME["btn"], fg=THEME["text_dim"], bd=0, relief="flat",
                      font=FONT_UI_S, activebackground=THEME["btn_hot"],
                      padx=4).pack(side="left")
        self._refresh_status()

    def add_attachments(self, paths):
        added = 0
        for p in paths:
            p = str(p)
            if not p or not os.path.exists(p):
                continue
            if p in self.attachments:
                continue
            self.attachments.append(p)
            added += 1
        if added:
            self._refresh_att()

    def remove_att(self, idx):
        try:
            self.attachments.pop(idx)
        except Exception:
            pass
        self._refresh_att()

    def pick_files(self):
        try:
            paths = filedialog.askopenfilenames(title="选择要附加的文件 / 图片")
        except Exception:
            paths = ()
        if paths:
            self.add_attachments(paths)

    def add_clipboard_image(self):
        """抓剪贴板：图片 → 存成临时 png 附件；文件列表 → 附件。"""
        if not HAVE_PIL:
            self.push_system("⚠️ 没装 Pillow，无法从剪贴板取图；请用 📎 或拖拽。")
            return
        self._clip_to_att()

    def _clip_to_att(self):
        try:
            data = ImageGrab.grabclipboard()
        except Exception:
            data = None
        if data is None:
            return False
        if isinstance(data, list):
            self.add_attachments(data)
            return True
        if HAVE_PIL and isinstance(data, Image.Image):
            d = os.path.join(_tmp_dir(), "clip_%s.png" % time.strftime("%H%M%S"))
            try:
                data.save(d)
                self.add_attachments([d])
                return True
            except Exception:
                return False
        return False

    # ------------------------------------------------------------ 事件
    def _sel_all(self, _e=None):
        self.input.tag_add("sel", "1.0", "end-1c")
        return "break"

    def _on_shift_return(self, _e=None):
        """插入换行（QQ 的 Shift+Enter 行为）。"""
        self.input.insert("insert", "\n")
        self._autogrow()
        return "break"

    def _on_return(self, _e=None):
        self.send()
        return "break"

    def _on_paste(self, _e=None):
        """Ctrl+V：剪贴板里是文件/图片 → 变附件；否则走默认文本粘贴。"""
        if self._clip_to_att():
            return "break"
        return None

    def _on_drop_files(self, paths):
        self.add_attachments(paths)

    def _autogrow(self):
        try:
            n = self.input.count("1.0", "end-1c", "displaylines")
            n = int(n[0]) if isinstance(n, (tuple, list)) else int(n)
        except Exception:
            n = 1
        self.input.configure(height=max(3, min(int(n or 1) + 0, 8)))

    def _refresh_status(self):
        try:
            txt = self.input.get("1.0", "end-1c")
        except Exception:
            txt = ""
        n = len(txt)
        parts = ["Enter 发送 · Shift+Enter 换行 · 拖文件进来即附件"]
        if self.attachments:
            parts.append("附件 %d 件" % len(self.attachments))
        parts.append("%d 字" % n)
        if self.model:
            parts.append(self.model)
        try:
            self.status.configure(text="  ｜  ".join(parts))
        except Exception:
            pass
        try:
            self.lbl_sign.configure(
                text="✎ " + (getattr(self, "signature", "") or ""))
        except Exception:
            pass

    def _rotate_signature(self):
        """换一句个性签名（古早 QQ 的味儿）。取不到就静默跳过。"""
        try:
            self.signature = signature()
            self.lbl_sign.configure(text="✎ " + (self.signature or ""))
        except Exception:
            pass

    def toggle_top(self):
        try:
            cur = bool(self.root.attributes("-topmost"))
            self.root.attributes("-topmost", not cur)
            self.push_system("📌 置顶：%s" % ("开" if not cur else "关"))
        except Exception:
            pass

    def clear_bubbles(self):
        try:
            self.chat.configure(state="normal")
            self.chat.delete("1.0", "end")
            self.chat.configure(state="disabled")
        except Exception:
            pass
        self.push_system("🧹 对话区已清空（日志页保留）")

    # ------------------------------------------------------------ 发送
    def send(self):
        try:
            text = self.input.get("1.0", "end-1c")
        except Exception:
            return
        # ★ 操作台：正在等报批答复时，输入框里的 y / a / n 就是**答案**，不是聊天消息。
        #   主程序此刻正阻塞在等这一个答复上，所以这条路径必须优先。
        if self._ask is not None:
            ans = text.strip().lower()
            vals = [str(v).lower() for v, _l in self._ask["options"]]
            if ans == "":
                self._answer_ask(self._ask.get("default", "n"))
                self.input.delete("1.0", "end")
                self.input.configure(height=3)
                return
            if ans in vals:
                self._answer_ask(ans)
                self.input.delete("1.0", "end")
                self.input.configure(height=3)
                return
            self.push_system("⚠️ 正在等你确认，请从 %s 里选一个（或点上面的按钮）"
                             % " / ".join(vals))
            return
        atts = list(self.attachments)
        if not text.strip() and not atts:
            return
        self.input.delete("1.0", "end")
        self.input.configure(height=3)
        self.attachments = []
        self._refresh_att()

        body = text.rstrip()
        if atts:
            refs = []
            for p in atts:
                refs.append('"%s"' % p if " " in p else "@" + p)
            body = (body + "\n\n" if body else "") + "\n".join(refs)
        self.push_user(body)
        OUT_Q.put({"kind": "msg", "text": body})
        if self.on_send:
            try:
                self.on_send()
            except Exception:
                pass
        self._refresh_status()

    # ------------------------------------------------------------ 轮询
    def _poll(self):
        if not self.alive:
            return
        try:
            n = 0
            while n < 40:
                try:
                    item = IN_Q.get_nowait()
                except queue.Empty:
                    break
                n += 1
                k = item.get("kind")
                if k == "user":
                    self.push_user(item.get("text", ""))
                elif k == "ai":
                    self.push_ai(item.get("text", ""))
                elif k == "sys":
                    self.push_system(item.get("text", ""))
                elif k == "raw":
                    self.push_raw(item.get("text", ""))
                elif k == "status":
                    self.set_status(model=item.get("model"),
                                    workspace=item.get("workspace"))
                elif k == "attach":
                    self.add_attachments(item.get("paths") or [])
                elif k == "ask":
                    self.ask(item.get("text", ""),
                             options=item.get("options"),
                             default=item.get("default", "n"),
                             title=item.get("title") or "需要你确认")
                elif k == "ask_cancel":
                    self.cancel_ask(item.get("why") or "已取消")
                elif k == "stream_begin":
                    self.stream_begin(item.get("who"))
                elif k == "stream_delta":
                    self.stream_delta(item.get("text", ""))
                elif k == "stream_end":
                    self.stream_end(aborted=bool(item.get("aborted")))
                elif k == "net":
                    self.set_net_status(item.get("text", ""))
                elif k == "spin_begin":
                    self.spin_begin(item.get("label"))
                elif k == "spin_label":
                    self.spin_label(item.get("label"))
                elif k == "spin_end":
                    self.spin_end(ok=bool(item.get("ok", True)),
                                  label=item.get("label"),
                                  elapsed=item.get("elapsed") or 0.0)
                elif k == "stop":
                    self._on_close()
                    return
        except Exception:
            pass
        try:
            self.root.after(60, self._poll)
        except Exception:
            pass


def _is_image(p):
    return os.path.splitext(str(p))[1].lower() in (".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp")


def _tmp_dir():
    d = os.path.join(os.getcwd(), ".fatfish_tmp", "chat_attach")
    try:
        os.makedirs(d, exist_ok=True)
    except Exception:
        pass
    return d


# ================================================================ 模块级 API
IN_Q = queue.Queue()       # 主程序 → 窗口
OUT_Q = queue.Queue()      # 窗口 → 主程序（用户聊的消息）
ANSWER_Q = queue.Queue()   # 窗口 → 主程序（报批答复；单独一条队列，
                           #   否则「报批时敲的 y」会和聊天消息抢同一个队列，
                           #   一旦被别的路径取走，主程序就会一直卡在等答复上）
_WIN = None
_LOCK = threading.Lock()


def start(on_send=None, title="🐟 肥鱼 · 对话", model="", workspace="",
          topmost=False):
    """起窗口（幂等）。返回 ChatWindow 或 None。"""
    global _WIN
    if not HAVE_TK:
        return None
    with _LOCK:
        if _WIN is not None and _WIN.alive:
            return _WIN
        w = ChatWindow(title=title, model=model, workspace=workspace,
                       on_send=on_send, topmost=topmost)
        if not w.start():
            return None
        _WIN = w
        return _WIN


def stop():
    global _WIN
    with _LOCK:
        w = _WIN
        _WIN = None
    if w is not None:
        try:
            w.stop()
        except Exception:
            pass


def is_on():
    return _WIN is not None and _WIN.alive


def _push(kind, text):
    if is_on():
        IN_Q.put({"kind": kind, "text": text})


def push_user(text):
    _push("user", text)


def push_ai(text):
    _push("ai", text)


def push_system(text):
    _push("sys", text)


def push_raw(text):
    _push("raw", text)


def push_attach(paths):
    if is_on():
        IN_Q.put({"kind": "attach", "paths": list(paths)})


def set_status(model=None, workspace=None):
    if is_on():
        IN_Q.put({"kind": "status", "model": model, "workspace": workspace})


def has_pending():
    return not OUT_Q.empty()


def take_pending():
    """取一条窗口来的用户消息。

    返回 str；**None 表示「没有消息」**，而 **"" 表示「有一条空消息」**（回车）。
    这个区分很关键：主循环靠「收到空行」触发「后台任务跑完主动开口」，
    所以空行必须原样送达，不能被当成「没消息」吞掉。
    """
    try:
        it = OUT_Q.get_nowait()
    except queue.Empty:
        return None
    if it.get("kind") != "msg":
        return None
    t = it.get("text")
    return t if t is not None else ""


def _push_kind(kind, text=None, **kw):
    if is_on():
        d = {"kind": kind}
        if text is not None:
            d["text"] = text
        d.update(kw)
        IN_Q.put(d)


# ---- 操作台：报批确认 ----
def ask(text, options=None, default="n", title="需要你确认"):
    """让窗口弹一条确认条。返回 True 表示窗口已受理（窗口不在则 False）。"""
    if not is_on():
        return False
    _push_kind("ask", text, options=options, default=default, title=title)
    return True


def cancel_ask(why="已取消"):
    _push_kind("ask_cancel", why)


def has_ask():
    return bool(_WIN is not None and _WIN.has_ask())


def take_answer(timeout=None):
    """取一条报批答复。timeout=None → 不阻塞；给秒数 → 最多等这么久。"""
    try:
        it = ANSWER_Q.get_nowait() if timeout is None else ANSWER_Q.get(timeout=timeout)
    except queue.Empty:
        return None
    return it.get("value")


def has_answer():
    return not ANSWER_Q.empty()


def clear_answers():
    """丢掉所有未读答复（新一轮开始前清理，防止上一轮的答复串到这一轮）。"""
    n = 0
    while True:
        try:
            ANSWER_Q.get_nowait()
            n += 1
        except queue.Empty:
            return n


# ---- 操作台：队列驱动的输入 ----
def wait_input(timeout=None):
    """阻塞等一条**用户消息**（操作台模式就是靠它驱动主循环的）。

    返回 str；超时或窗口已关返回 None。timeout=None 表示一直等。
    有超时的用法让调用方可以周期性检查「窗口是不是还活着 / 要不要退出」。
    """
    try:
        it = OUT_Q.get() if timeout is None else OUT_Q.get(timeout=timeout)
    except queue.Empty:
        return None
    if it.get("kind") == "msg":
        t = it.get("text")
        return t if t is not None else ""      # "" = 空消息（回车），与 None 区分
    return None


# ---- 操作台：流式气泡 ----
def push_stream_begin(who=None):
    _push_kind("stream_begin", None, who=who)


def push_stream_delta(text):
    if text:
        _push_kind("stream_delta", text)


def push_stream_end(aborted=False):
    _push_kind("stream_end", None, aborted=bool(aborted))


def stream_active():
    return bool(_WIN is not None and _WIN.stream_active())


# ---- 状态条：联网搜索状态 / 等待转圈（- \ | /）----
def set_net_status(text):
    """改「消息栏前面」左格：联网搜索状态。"""
    _push_kind("net", text or "")


def spin_begin(label=None):
    _push_kind("spin_begin", None, label=label)


def spin_label(label):
    _push_kind("spin_label", None, label=label)


def spin_end(ok=True, label=None, elapsed=0.0):
    _push_kind("spin_end", None, ok=bool(ok), label=label,
               elapsed=float(elapsed or 0.0))


def drain_pending():
    """取出所有待处理消息。

    有内容 → 合并成一条（空行分隔）；只有空消息 → 返回 ""（= 回车）；
    什么都没有 → None。
    """
    out = []
    got_empty = False
    while True:
        m = take_pending()
        if m is None:
            break
        if m.strip():
            out.append(m)
        else:
            got_empty = True
    if out:
        return "\n\n".join(out)
    return "" if got_empty else None


def _selftest_make_hdrop(paths):
    """给自测用：手工构造一个真的 HDROP（DROPFILES + 双空结尾 UTF-16 文件名表）。

    ★ GlobalAlloc 的 argtypes/restype 必须显式设：默认 restype 是 32 位 c_int，
      64 位下句柄会被截断成 0，表现为「GlobalAlloc 失败」的假象。
    """
    import struct
    ct = __import__("ctypes")
    from ctypes import wintypes  # noqa: F401
    k32 = ct.windll.kernel32
    k32.GlobalAlloc.argtypes = [ct.c_uint, ct.c_size_t]
    k32.GlobalAlloc.restype = ct.c_void_p
    k32.GlobalLock.argtypes = [ct.c_void_p]
    k32.GlobalLock.restype = ct.c_void_p
    k32.GlobalUnlock.argtypes = [ct.c_void_p]
    head = struct.pack("<Iiiii", 20, 0, 0, 0, 1)
    body = "".join(p + "\0" for p in paths) + "\0"
    data = head + body.encode("utf-16-le")
    h = k32.GlobalAlloc(0x0002, len(data))          # GMEM_MOVEABLE
    if not h:
        return None
    p = k32.GlobalLock(h)
    ct.memmove(p, data, len(data))
    k32.GlobalUnlock(h)
    return h


def _selftest_dnd(root, got):
    """端到端模拟一次 WM_DROPFILES 投递，返回 (是否通过, 说明)。"""
    import ctypes
    from ctypes import wintypes
    ct = ctypes
    u32 = ct.windll.user32
    u32.PostMessageW.argtypes = [wintypes.HWND, ct.c_uint, ct.c_void_p, ct.c_void_p]
    u32.PostMessageW.restype = wintypes.BOOL
    dt = DropTarget(root, lambda ps: got.extend(ps))
    if not dt.ok:
        return False, "未武装：%s" % (dt.err or "原因未知")
    cands = [os.path.abspath("chat_window.py"), os.path.abspath("README.md"),
             os.path.abspath("deploy_all.bat")]
    targets = [p for p in cands if os.path.exists(p)][:2]
    hdrop = _selftest_make_hdrop(targets)
    if not hdrop:
        return False, "构造 HDROP 失败"
    u32.PostMessageW(dt.hwnd, dt.WM_DROPFILES, ct.c_void_p(hdrop), None)
    for _ in range(60):
        root.update()
        time.sleep(0.02)
        if got:
            break
    dt.restore()
    if list(got) == list(targets):
        return True, "收到 %d 个文件" % len(got)
    return False, "期望 %r，实收 %r（err=%s）" % (targets, got, dt.err)


# ================================================================ 演示 / 自测
def _demo():
    if not HAVE_TK:
        print("❌ 本机没有 tkinter，无法演示。")
        return 2
    print("=" * 70)
    print("肥鱼对话窗口 · 演示模式（离线，自己跟自己对话）")
    print("=" * 70)
    print("  · Enter 发送 / Shift+Enter 换行")
    print("  · 直接把文件或图片拖进窗口试试")
    print("  · 右上角 📎 选文件、🖼 贴剪贴板图片")
    print("  · 关闭窗口即退出演示")
    print()

    def on_send():
        time.sleep(0.2)
        push_ai("收到啦 🐟 这是**演示回复**。\n\n"
                "下面是标记渲染测试：\n"
                "- {{green}}绿色成功{{/green}}\n"
                "- {{red}}红色警告{{/red}} 与 {{bold}}加粗{{/bold}}\n"
                "- 行内 `代码` 与代码块：\n"
                "```python\n"
                "def hello():\n"
                "    return '🐟'\n"
                "```\n"
                "附件会被写进消息里，交给现成的 file_tools 读取。")

    w = start(on_send=on_send, model="deepseek-flash（演示）",
              workspace=os.getcwd())
    if not w:
        print("❌ 窗口创建失败（可能没有可用的图形会话）。")
        return 2

    push_ai("你好呀，我是{{cyan}}肥鱼{{/cyan}}。\n\n"
            "这个窗口是「古早 QQ」式的：\n"
            "- 头像 + 昵称 + 时间 + 正文，**没有气泡**\n"
            "- 输入框可以**多行打字**（Shift+Enter 换行）\n"
            "- 📎 选文件、🖼 贴图、或者直接把文件**拖进来**\n"
            "- 点下边 😊 打开表情框\n\n"
            "切换上面的「日志」页，能看到完整的控制台输出。")
    push_raw("\x1b[36m[演示] 这是一段日志，带 ANSI 颜色：\x1b[0m\n"
             "  \x1b[92m✅ 成功\x1b[0m  \x1b[91m⚠️ 警告\x1b[0m  "
             "\x1b[93m★ 高亮\x1b[0m\n"
             "  \x1b[90m[dim] 我是暗淡的细节行\x1b[0m\n")

    # 让演示也能「收到」自己发的消息（在 Tk 线程里轮询 OUT_Q）
    def _echo():
        while True:
            try:
                it = OUT_Q.get_nowait()
            except queue.Empty:
                break
            msg = it.get("text")
            if msg:
                push_ai("（演示模式：若接到真程序，这里应该是我的回复）\n\n"
                        "你发了 %d 个字符：\n%s" % (len(msg), msg[:200]))
        try:
            w.root.after(300, _echo)
        except Exception:
            pass

    w.root.after(400, _echo)
    try:
        w.th.join()
    except KeyboardInterrupt:
        stop()
    return 0


def _selftest():
    """程序化断言。无图形会话时自动跳过（返回 0 并说明）。"""
    fails = []

    def check(name, cond, detail=""):
        if cond:
            print("   ✅ %s" % name)
        else:
            fails.append(name)
            print("   ❌ %s  %s" % (name, detail))

    print("== chat_window 离线自测 ==")
    print("[1] 依赖")
    check("tkinter 可用", HAVE_TK)
    print("       PIL（缩略图/剪贴板）: %s" % ("可用" if HAVE_PIL else "不可用（可降级）"))
    if not HAVE_TK:
        print("   ⏭ 无 tkinter，跳过界面测试")
        return 0

    print("[2] 建窗（无 mainloop，直接 update 驱动）")
    try:
        root = tk.Tk()
    except Exception as e:
        print("   ⏭ 无法创建窗口（%s），跳过" % e)
        return 0
    root.withdraw()

    w = ChatWindow(title="自测窗口")
    w.root = root
    try:
        w._build()
    except Exception as e:
        print("   ❌ _build 抛异常：%r" % (e,))
        return 1
    check("界面构建完成", w.inner is not None and w.log is not None)

    print("[3] 气泡渲染")
    w.push_ai("你好 {{green}}绿{{/green}} **粗** `码`\n```py\nx=1\n```\n")
    root.update()

    def _all_text(frame=None, acc=None):
        """成行排列后，整个对话区就是一条连续的文本流 —— 直接读它。"""
        try:
            return [w.chat.get("1.0", "end-1c")]
        except Exception:
            return [""]

    texts = "".join(_all_text())
    check("对话流已写入", bool(texts.strip()), repr(texts[:60]))
    check("气泡里有正文", "绿" in texts and "粗" in texts and "x=1" in texts, repr(texts[:120]))
    check("标记未裸漏", "{{" not in texts and "**" not in texts, repr(texts[:120]))

    print("[4] 用户消息 / 标记方向")
    before = len("".join(_all_text()))
    w.push_user("我是用户")
    root.update()
    check("用户消息已追加", len("".join(_all_text())) > before)
    texts2 = "".join(_all_text())
    check("用户内容在", "我是用户" in texts2)
    check("我 标签在", "我" in texts2)

    print("[5] 正文渲染器：彩虹 / 嵌套 / 引用")
    t = tk.Text(root)
    render_bubble(t, "{{rainbow}}彩虹{{/rainbow}} 与 **粗** 和 `码`")
    got = t.get("1.0", "end-1c")
    check("彩虹逐字不裸漏", "{{" not in got, repr(got))
    check("内容完整", all(c in got for c in "彩虹"), repr(got))

    print("[6] ANSI 日志渲染")
    w.push_raw("\x1b[92m绿色\x1b[0m 普通 \x1b[91m红\x1b[0m\n")
    root.update()
    logtxt = w.log.get("1.0", "end-1c")
    check("日志有内容", "绿色" in logtxt and "红" in logtxt, repr(logtxt))
    check("ANSI 未被原样残留", "\x1b" not in logtxt, repr(logtxt))
    check("确实上了色（有 tag）", bool(w.log.tag_names()), str(w.log.tag_names())[:80])

    print("[7] 半截转义序列跨调用")
    w.log.delete("1.0", "end")
    w.push_raw("\x1b[9")          # 被切断
    w.push_raw("2m续上了\x1b[0m")
    root.update()
    got = w.log.get("1.0", "end-1c")
    check("半截序列被拼回", "续上了" in got and "\x1b" not in got, repr(got))

    print("[8] 附件")
    tmp = os.path.join(_tmp_dir(), "selftest_att.txt")
    try:
        os.makedirs(os.path.dirname(tmp), exist_ok=True)
        open(tmp, "w", encoding="utf-8").write("x")
    except Exception:
        pass
    w.add_attachments([tmp, "不存在的路径"])
    root.update()
    check("附件入列（只收存在的）", w.attachments == [tmp], repr(w.attachments))
    check("附件条已显示", bool(w.att_frame.winfo_children()))
    w.remove_att(0)
    root.update()
    check("附件可移除", w.attachments == [])

    print("[9] 发送 → 队列 → 附件路径写进正文")
    w.input.delete("1.0", "end")
    w.input.insert("1.0", "帮我看看这个文件")
    w.add_attachments([tmp])
    w.send()
    root.update()
    check("输入框已清空", w.input.get("1.0", "end-1c") == "")
    check("附件已清空", w.attachments == [])
    msg = take_pending()
    check("消息进了队列", msg is not None and "帮我看看这个文件" in msg, repr(msg))
    check("附件路径写进了消息", msg and tmp.replace("\\", "\\") in msg.replace("\\", "\\"),
          repr(msg))
    check("取走后再取为 None", take_pending() is None)

    print("[10] 空格路径用引号、无空格用 @")
    tmp2 = os.path.join(_tmp_dir(), "带 空格 的.txt")
    try:
        with open(tmp2, "w", encoding="utf-8") as f:
            f.write("y")
    except Exception:
        tmp2 = None
    if tmp2:
        w.add_attachments([tmp2])
        w.input.insert("1.0", "hi")
        w.send()
        root.update()
        m2 = take_pending()
        check("带空格路径被引号包住", m2 and ('"%s"' % tmp2) in m2, repr(m2))
        os.remove(tmp2)

    print("[11] IN_Q 投递（主程序 → 窗口）")
    # ★ 用 globals() 而不是 `import chat_window`：自测是「以 __main__ 身份」跑的，
    #   再 import 自己会造出**第二个模块实例**，改它的 _WIN 对这里的函数毫无影响。
    globals()["_WIN"] = w     # 模拟「已通过 start() 启动」
    w.alive = True            # start() 里由 _run 置真；自测是手工建的
    push_ai("来自主程序的推送")
    for _ in range(5):
        w._poll()
        root.update()
    check("推送被渲染", "来自主程序的推送" in "".join(_all_text(w.inner)))
    check("is_on() 现在为 True", is_on() is True)
    globals()["_WIN"] = None
    w.alive = False

    print("[12] 模块级开关")
    check("未 start 时 is_on() 为 False", is_on() is False)
    check("未 start 时 push 不抛异常", (push_ai("x"), push_system("y"), push_raw("z")) and True)

    print("[13] 拖拽（WM_DROPFILES）端到端")
    if sys.platform != "win32":
        print("   ⏭ 非 Windows 平台，跳过（📎 按钮仍可用）")
    else:
        got = []
        try:
            ok, why = _selftest_dnd(root, got)
        except Exception as e:
            ok, why = False, "%s: %s" % (type(e).__name__, e)
        check("模拟拖入 2 个文件并收到", ok, why)

    # ================= 操作台能力（本轮新增） =================
    globals()["_WIN"] = w
    w.alive = True

    def _drain_ans():
        while True:
            try:
                ANSWER_Q.get_nowait()
            except queue.Empty:
                return

    print("[14] 报批确认条：弹条 → 点按钮 → 答复入队")
    _drain_ans()
    while take_pending():
        pass
    ok = ask("即将删除 a.txt，是否批准？",
             options=[("y", "✅ 批准"), ("a", "🔓 全放行"), ("n", "🚫 拒绝")],
             default="n", title="报批")
    for _ in range(5):
        w._poll()
        root.update()
    check("ask() 受理", ok is True)
    check("确认条已弹出", w.has_ask() is True)
    check("确认条可见（有子件）", bool(w.ask_frame.winfo_children()))
    check("ask_frame 已 pack", bool(w.ask_frame.winfo_ismapped()) or
          w.ask_frame.winfo_manager() != "")
    w._answer_ask("y")
    root.update()
    check("答复进了 ANSWER_Q", take_answer() == "y", repr(take_answer()))
    check("确认条已收起", w.has_ask() is False and not w.ask_frame.winfo_children())

    print("[15] 键盘答复：输入框里敲 y 回车 = 答复（不是聊天消息）")
    _drain_ans()
    while take_pending():
        pass
    ask("再确认一次", options=[("y", "批准"), ("n", "拒绝")], default="n")
    for _ in range(5):
        w._poll()
        root.update()
    w.input.delete("1.0", "end")
    w.input.insert("1.0", "y")
    w.send()
    root.update()
    check("y 被当作答复", take_answer() == "y")
    check("问题已关闭", w._ask is None)
    check("★ 没有被当成聊天消息发出去", take_pending() is None)

    print("[16] 不匹配的输入 → 只提示，不发送")
    _drain_ans()
    while take_pending():
        pass
    ask("再确认", options=[("y", "批准"), ("n", "拒绝")], default="n")
    for _ in range(5):
        w._poll()
        root.update()
    w.input.delete("1.0", "end")
    w.input.insert("1.0", "我先看看")
    w.send()
    root.update()
    check("问题仍挂着（不误答）", w.has_ask() is True)
    check("没有产生答复", take_answer() is None)
    check("没有当消息发出去", take_pending() is None)
    check("给了提示", "正在等你确认" in "".join(_all_text(w.inner)))
    w._answer_ask("n")                       # 收尾，别把问题挂在场上
    _drain_ans()
    root.update()

    print("[17] 空回车 = 采用默认答案（绝不悬空）")
    ask("确认下", options=[("y", "批准"), ("n", "拒绝")], default="n")
    for _ in range(5):
        w._poll()
        root.update()
    w.input.delete("1.0", "end")
    w.send()                                  # 空回车
    root.update()
    check("按默认 n 收场", take_answer() == "n")
    check("问题已关闭", w.has_ask() is False)

    print("[18] 流式气泡：begin / delta / end")
    w.stream_begin("🐟 肥鱼")
    w.stream_delta("你好，")
    w.stream_delta("这是\x1b[92m流式\x1b[0m输出")
    w.stream_delta("\n- 项目一\n# 标题\n")
    root.update()
    check("流式进行中", w.stream_active() is True)
    check("字符数在记账", w.stream_chars() >= 20, str(w.stream_chars()))
    n_chars = w.stream_end()
    root.update()
    check("流式已收口", w.stream_active() is False)
    check("end() 返回字符数", n_chars >= 20, str(n_chars))
    stxt = "".join(_all_text(w.inner))
    check("气泡里有流式内容", "流式" in stxt and "项目一" in stxt, repr(stxt[-160:]))
    check("ANSI 未裸漏进文本", "\x1b" not in stxt, repr(stxt[-160:]))

    print("[19] 流式中断要标出来（别让半句话装成完整答案）")
    w.stream_begin()
    w.stream_delta("半句话")
    w.stream_end(aborted=True)
    root.update()
    check("有中断标记", "输出中断" in "".join(_all_text(w.inner)))

    print("[20] 队列语义：答复与聊天消息绝不串台")
    _drain_ans()
    while take_pending():
        pass
    OUT_Q.put({"kind": "msg", "text": "聊天消息"})
    ANSWER_Q.put({"kind": "answer", "value": "y"})
    check("take_answer 只取答复", take_answer() == "y")
    check("take_pending 只取消息", take_pending() == "聊天消息")
    check("两份队列各自清空",
          take_answer() is None and take_pending() is None)

    print("[21] wait_input（操作台主循环靠它驱动）")
    check("空队列 + 超时 → None", wait_input(timeout=0.1) is None)
    OUT_Q.put({"kind": "msg", "text": "来自队列"})
    check("取到消息", wait_input(timeout=0.5) == "来自队列")

    print("[22] clear_answers（新一轮开始前清理陈答复）")
    ANSWER_Q.put({"kind": "answer", "value": "y"})
    ANSWER_Q.put({"kind": "answer", "value": "a"})
    n = clear_answers()
    check("清掉 2 条", n == 2, str(n))
    check("清完为空", has_answer() is False)

    globals()["_WIN"] = None
    w.alive = False

    try:
        root.destroy()
    except Exception:
        pass

    print()
    if fails:
        print("❌ 失败 %d 项：%s" % (len(fails), "、".join(fails)))
        return 1
    print("🎉 全部通过（离线，未联网，未动真文件）")
    return 0


def main(argv=None):
    argv = list(sys.argv[1:] if argv is None else argv)
    if "--demo" in argv:
        return _demo()
    return _selftest()


if __name__ == "__main__":
    sys.exit(main())
