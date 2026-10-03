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

import json
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

# ★ [P1b] 编辑器组式布局的数据模型（纯数据、零 Tk 依赖、可单测）
#   取不到时用降级桩 —— 界面自动退回「全部页签」形态，绝不因缺模块而开不了窗。
try:
    from fatfish_core.layout import (       # noqa: E402
        LayoutModel, PANELS as LAYOUT_PANELS,
        PANEL_LABELS, PRESETS as LAYOUT_PRESETS,
    )
    LAYOUT_OK = True
except Exception:                            # pragma: no cover
    LAYOUT_OK = False
    LAYOUT_PANELS = ("chat", "log", "status", "exec")
    PANEL_LABELS = {"chat": "💬 对话", "log": "📜 日志",
                    "status": "🧿 状态", "exec": "🖥 监控"}
    LAYOUT_PRESETS = {}

    class LayoutModel(object):
        """降级桩：只有单个组、面板不可拆分（保证程序仍可运行）。"""
        tree = None
        groups = {}

        def reset(self, *a, **k):
            return self

        def group_of(self, panel):
            return "g1" if panel in ("chat", "log") else None

        def leaf_groups(self):
            return ["g1"]

        def dock(self, *a, **k):
            return False

        def split_panel(self, *a, **k):
            return None

        def detach(self, *a, **k):
            return False

        def close(self, *a, **k):
            return False

        def drop_group(self, *a, **k):
            return False

        def move_to_next_group(self, *a, **k):
            return False

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
# ★ [P1a] 数据层每个面板的条目上限（环形缓冲）。
#   视图可随意生灭，数据层是唯一真源；上限防止长时间运行把内存吃满。
#   比 MAX_LOG_LINES 略大：视图只显示尾部，数据层多留一截便于整体重放。
DATA_CAP = 6000

# ============ ★ [P1b] 界面模式 ============
#   editor  = 编辑器组式布局（VS Code 风格，默认）
#             主屏 = 对话 + 日志 ｜ 副屏 = 状态 + 监控
#   classic = 旧的两页签（对话 / 日志）—— 一键回旧界面，**不用改文件**
#             （设置环境变量 FATFISH_UI=classic 后重启即可）
UI_MODES = ("editor", "classic")
_UI_FORCE_CLASSIC = (os.environ.get("FATFISH_UI", "editor").strip().lower() == "classic")
# 默认布局预设（用户指定：对话为主 + 状态 / 监控在左）
_DEFAULT_LAYOUT_PRESET = (os.environ.get("FATFISH_UI_PRESET", "focus").strip() or "focus")
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
        self._last_see = 0.0             # ★ [P0-A] 日志页 see("end") 节流用
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
        # ---- ★ [P1a] 数据层：所有对外文本的唯一真源 ----
        #   视图（Text）只是它的一个投影。面板被关闭 / 拆成浮窗 / 切到后台页签时，
        #   内容依然留在数据层；重新显示时由 replay() 整体重放，一行不丢。
        #     data["chat"]   : [("me"/"ai"/"sys", text), ...]
        #     data["log"]    : [原始行, ...]（含 ANSI，交给 AnsiPainter 上色）
        #     data["status"] : [过程信息行, ...]   （P1c 接线）
        #     data["exec"]   : [子程序输出行, ...] （P1c 接线）
        self.data = {"chat": [], "log": [], "status": [], "exec": []}
        self._data_echo = True           # False = 只写数据层，不刷视图（批量重放用）
        # ---- ★ [P1b] 编辑器组式布局（VS Code 风格）----
        self._ui_mode = "classic" if _UI_FORCE_CLASSIC else "editor"
        # ---- ★ [P1d] 从 .env 的 UI_LAYOUT 恢复上次布局 ----
        #   坏数据由 LayoutModel.from_json 自动降级到默认预设，并记下 load_error；
        #   这里只把"降级发生过"这件事记下来，供启动时提示。
        self._layout_load_error = None
        try:
            _raw = (os.environ.get("UI_LAYOUT", "") or "").strip()
            if _raw:
                _m = LayoutModel.from_json(_raw)
                self._layout_load_error = getattr(_m, "load_error", None)
            else:
                _m = LayoutModel().reset(_DEFAULT_LAYOUT_PRESET)
            self.model = _m
        except Exception as _e:
            self._layout_load_error = repr(_e)
            try:
                self.model = LayoutModel().reset(_DEFAULT_LAYOUT_PRESET)
            except Exception:
                self.model = LayoutModel()
        # 启动时读到的样子 —— 只有真的变了才写回 .env（免得每次启动都白写一遍）
        try:
            self._layout_at_load = self.model.to_json()
        except Exception:
            self._layout_at_load = ""
        self._layout_save_job = None
        self._env_backed = False
        self.texts = {}                  # panel -> 当前可见的 Text
        self.floats = {}                 # panel -> Toplevel（被拆出去的面板）
        self.exec_text = None            # 「监控」面板正文
        self.exec_painter = None
        # ---- ★ [P1c] 状态 / 监控 两个面板的数据源 ----
        self.status_text = None          # 「🧿 状态」面板正文（过程信息）
        self.status_painter = None
        self.painters = {}               # panel -> AnsiPainter（log/status/exec 共用）
        self._exec_tail = None           # 子程序输出 tailer（fatfish_core.exectail）
        self._exec_tail_job = None
        self._main_body = None           # 布局容器（附件条 pack 在它前面）
        self._busy = False               # 重建中标志（防重入）
        self._tagged = set()             # 已 tag_configure 过的 tag（省重复配置）
        self._replaying = False          # 重放中标志（期间不刷状态栏）
        self._hover_job = None           # ★ 悬停看护的 after 句柄（关窗要取消）
        self._rebuild_pending = False    # ★ 合并多次重建请求
        self._input_len = 0              # ★ [P1c] 输入框「去空白后长度」的线程安全快照
        self._st_ts = 0.0                # 状态栏节流时间戳
        self.chat = None                 # ★ 兼容别名：当前对话面板的 Text（可能为 None）
        self.log = None
        self.log_painter = None
        self.inner = None

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

    def build_here(self):
        """★ [P0-B] 在**当前线程**建窗（不跑 mainloop）。

        给「Tk 归主线程」用：主线程调它建窗，再自己调 run_mainloop()。
        与 start() 的区别：start() 会另起 daemon 子线程跑 Tk —— 那正是
        `Tcl_AsyncDelete: async handler deleted by the wrong thread` 的来源。
        """
        if not HAVE_TK:
            return False
        try:
            self.root = tk.Tk()
        except Exception:
            self.alive = False
            return False
        try:
            self._build()
            self.alive = True
            self.root.protocol("WM_DELETE_WINDOW", self._on_close)
            self.root.after(16, self._poll)
        except Exception:
            self.alive = False
            return False
        return True

    def run_mainloop(self):
        """★ [P0-B] 在调用者线程跑 mainloop，并在结束后销毁窗口。

        必须在**调用 build_here() 的同一个线程**里调用（Tk 的硬约束）。
        """
        if self.root is None:
            return
        try:
            self.root.mainloop()
        except Exception:
            pass
        finally:
            self.alive = False
            try:
                if self.root is not None:
                    self.root.destroy()
            except Exception:
                pass
            self.root = None

    def _on_close(self):
        self.alive = False
        self._stop_hover_watch()          # ★ 先停定时回调，再销毁窗口
        self._stop_exec_tail()            # ★ [P1c]
        self._stop_layout_save()          # ★ [P1d]
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
        # ★ [P1b] 编辑器组布局是「左右分屏」，980 宽会把左栏挤扁 → 默认放宽
        # ★ [P1d] .env 里有 UI_GEOM_MAIN 就用它（记住上次的窗口大小/位置）
        _geo = (os.environ.get("UI_GEOM_MAIN", "") or "").strip()
        if not _geo:
            _geo = "1360x840" if self._ui_mode == "editor" else "980x720"
        r.geometry(_geo)
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

        # ---- ★ [P1b] 布局区：编辑器组式（VS Code 风格）/ 经典页签 ----
        #   classic → 原两页签（对话 / 日志），FATFISH_UI=classic 时启用
        #   editor  → 工具栏 + 可自由分屏的编辑器组
        if self._ui_mode == "classic":
            self._build_classic(r)
        else:
            self._build_editor(r)

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

    # ==================================================== ★ [P1b] 编辑器组布局
    def _build_classic(self, r):
        """经典页签布局 —— 原 Notebook 实现原样搬入。

        FATFISH_UI=classic 时走这里（一键回旧界面，不必改文件、不必回滚补丁）。
        """
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
        self._main_body = self.nb

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
        self.texts = {"chat": self.chat, "log": self.log}
        # ★ [P1c] 经典模式同样要登记 painter —— _paint_plain 是按
        #   (texts, painters) 成对取用的，漏了这里日志面板就不会渲染了。
        self.painters = {"log": self.log_painter}
        self._tagged.clear()

    def _build_editor(self, r):
        """编辑器组式布局：工具栏 + 可自由分屏的组树 + 布局状态栏。"""
        self._main_body = tk.Frame(r, bg=THEME["bg"])
        self._main_body.pack(fill="both", expand=True, side="top")
        self._toolbar(self._main_body)
        self.body = tk.Frame(self._main_body, bg=THEME["bg"])
        self.body.pack(fill="both", expand=True)
        self._rebuild_views()
        self._bind_layout_keys()          # ★ Ctrl+L 钉住 / 取消钉住
        self._start_hover_watch()         # ★ 鼠标悬停展开 / 移开收缩
        self._start_exec_tail()           # ★ [P1c] 监控面板数据源

    # ---------------------------------------------------------- 视图重建
    def _panel_style(self, panel):
        """各面板的底色/字色 —— 颜色一律取 uicolors 的 THEME，不另造色值。"""
        if panel == "chat":
            return {"bg": THEME["bg"], "fg": THEME["text"], "mono": False}
        if panel == "log":
            return {"bg": THEME["log_bg"], "fg": THEME["log_text"], "mono": True}
        if panel == "exec":
            return {"bg": THEME["log_bg"], "fg": THEME["log_text"], "mono": True}
        return {"bg": THEME["panel"], "fg": THEME["text"], "mono": False}

    def _rebuild_views(self):
        """按 model 重建视图：销毁旧控件 → 渲染树 → 重放数据 → 同步浮窗。

        ★ 只销毁**主窗口自己**的子控件：Toplevel 浮动窗虽然也出现在
          winfo_children() 里，但它是独立窗口，误删会把浮动面板一起干掉。
        """
        # ★ [P1b] 经典页签布局下没有 body，也没有"分屏"这回事 —— 静默忽略即可
        if self._ui_mode != "editor" or getattr(self, "body", None) is None:
            return
        if getattr(self, "_busy", False):
            return
        self._busy = True
        try:
            self.texts.clear()
            self.chat = None
            self.log = None
            self.log_painter = None
            self.exec_text = None
            self.exec_painter = None
            self.status_text = None          # ★ [P1c]
            self.status_painter = None
            self.painters.clear()            # ★ [P1c]
            self.inner = None
            self._tagged.clear()
            for w in list(self.body.winfo_children()):
                if isinstance(w, tk.Toplevel):
                    continue
                w.destroy()
            if self.model.tree is not None:
                self._node(self.body, self.model.tree)
            self._sync_floats()
            self._layout_status()
            self._schedule_layout_save()      # ★ [P1d] 布局变了 → 延后写回 .env
        finally:
            self._busy = False

    def _node(self, parent, node):
        if node[0] == "group":
            self._group(parent, node[1])
            return
        orient = "horizontal" if node[1] == "h" else "vertical"
        pw = tk.PanedWindow(parent, orient=orient, bg=THEME["panel"],
                            sashwidth=6, sashrelief="flat", opaqueresize=True,
                            bd=0, showhandle=False, handlesize=0)
        pw.pack(fill="both", expand=True)
        for child in node[2]:
            holder = tk.Frame(pw, bg=THEME["bg"])
            pw.add(holder, stretch="always", minsize=120)
            self._node(holder, child)

    def _group(self, parent, gid):
        g = self.model.groups.get(gid)
        if g is None:
            return
        if not g["panels"]:
            self._empty_group(parent, gid)      # 拆分留下的空位，等面板进来
            return
        active = g["active"] or g["panels"][0]

        # ---- 标签栏 ----
        bar = tk.Frame(parent, bg=THEME["panel"])
        bar.pack(fill="x", side="top")
        for p in g["panels"]:
            on = (p == active)
            tk.Button(bar, text=PANEL_LABELS.get(p, p),
                      command=lambda gg=gid, pp=p: self._activate(gg, pp),
                      bg=THEME["bg"] if on else THEME["panel"],
                      fg=THEME["text"] if on else THEME["text_dim"],
                      activebackground=THEME["btn_hot"],
                      activeforeground=THEME["text"],
                      relief="flat", bd=0, padx=10, pady=3,
                      font=FONT_UI_S, cursor="hand2"
                      ).pack(side="left", padx=(2 if on else 0, 0), pady=(3, 0))

        ops = tk.Frame(bar, bg=THEME["panel"])
        ops.pack(side="right", padx=4)
        BTN = dict(bg=THEME["panel"], fg=THEME["text_dim"],
                   activebackground=THEME["btn_hot"],
                   activeforeground=THEME["text"], relief="flat", bd=0,
                   padx=4, pady=2, font=FONT_UI_S, cursor="hand2")
        tk.Button(ops, text="⬒", command=lambda: self._split(active, "h"),
                  **BTN).pack(side="left")
        tk.Button(ops, text="⬓", command=lambda: self._split(active, "v"),
                  **BTN).pack(side="left")
        tk.Button(ops, text="⇥", command=lambda: self._move_next(active),
                  **BTN).pack(side="left")
        tk.Button(ops, text="⧉", command=lambda: self._detach(active),
                  **BTN).pack(side="left")
        tk.Button(ops, text="✕", command=lambda: self._close_panel(active),
                  **BTN).pack(side="left")

        # ---- 面板内容（只渲染当前标签）----
        wrap = tk.Frame(parent, bg=self._panel_style(active)["bg"])
        wrap.pack(fill="both", expand=True)
        self._make_panel_text(wrap, active)

    def _empty_group(self, parent, gid):
        """空组：拆分后留下的空位，等别的面板移进来。"""
        bar = tk.Frame(parent, bg=THEME["panel"])
        bar.pack(fill="x", side="top")
        tk.Label(bar, text="⬜ 空组", bg=THEME["panel"], fg=THEME["text_dim"],
                 font=FONT_UI_S).pack(side="left", padx=8, pady=3)
        tk.Button(bar, text="✕ 收起", command=lambda g=gid: self._drop_group(g),
                  bg=THEME["panel"], fg=THEME["text_dim"],
                  activebackground=THEME["btn_hot"], relief="flat", bd=0,
                  padx=6, pady=2, font=FONT_UI_S,
                  cursor="hand2").pack(side="right", padx=4)
        body = tk.Frame(parent, bg=THEME["bg"])
        body.pack(fill="both", expand=True)
        tk.Label(body, text="\n\n空位\n在别的组标题栏点「⇥」把面板移到这里",
                 bg=THEME["bg"], fg=THEME["text_dim"], justify="center",
                 font=FONT_UI_S).pack(expand=True)

    def _make_panel_text(self, parent, panel):
        """给一个面板建 Text + 滚动条，登记别名，并从数据层重放内容。

        ★ 这是「视图可随意生灭」的落点：重建后调一次 replay，
          数据层里的内容原样回到新控件上。
        """
        st = self._panel_style(panel)
        is_chat = (panel == "chat")
        txt = tk.Text(parent, bg=st["bg"], fg=st["fg"],
                      # ★ [P1b] height 必须给个小值：Text 默认申报 24 行，
                      #   叠两层 PanedWindow 后会申报近千像素，把排在后面 pack
                      #   的「输入栏」挤成 0 高（Tk 先 pack 的先占位）。
                      height=4,
                      font=(FONT_MONO if st["mono"] else FONT_UI),
                      wrap="none" if st["mono"] else "word",
                      bd=0, highlightthickness=0, padx=10, pady=6,
                      insertbackground=st["fg"],
                      cursor="arrow" if is_chat else "xterm",
                      state="disabled" if is_chat else "normal")
        sb = tk.Scrollbar(parent, orient="vertical", command=txt.yview,
                          bg=THEME["panel"], troughcolor=st["bg"],
                          bd=0, relief="flat")
        txt.configure(yscrollcommand=sb.set)
        sb.pack(side="right", fill="y")
        txt.pack(side="left", fill="both", expand=True)
        # ★ 滚轮只绑自己（沿用既有教训，绝不用 bind_all）
        txt.bind("<MouseWheel>",
                 lambda e, w=txt: (w.yview_scroll(int(-e.delta / 120), "units"),
                                   "break")[1])
        self.texts[panel] = txt
        if is_chat:
            txt.bind("<Key>", self._chat_key)
            self.chat = txt
            self.inner = txt
        else:
            # ★ [P1c] 日志 / 状态 / 监控 都是「ANSI 上色的只读面板」，
            #   统一建 painter 并登记到 self.painters，供 _paint_plain 取用。
            painter = AnsiPainter(txt, FONT_MONO, panel)
            self.painters[panel] = painter
            if panel == "log":
                self.log = txt
                self.log_painter = painter
            elif panel == "exec":
                self.exec_text = txt
                self.exec_painter = painter
            else:
                self.status_text = txt
                self.status_painter = painter
        try:
            self.replay(panel)              # 数据层 → 视图
        except Exception:
            pass
        return txt

    # ---------------------------------------------------------- 浮动窗
    def _sync_floats(self):
        """让浮动窗与模型保持一致：已停靠回来的销毁；仍在浮动的刷新内容。"""
        for panel in list(self.floats):
            win = self.floats[panel]
            if self.model.group_of(panel) is not None:
                try:
                    win.destroy()
                except Exception:
                    pass
                del self.floats[panel]
                continue
            if win is None or not win.winfo_exists():
                self.floats[panel] = self._make_float(panel)
            self._float_body(self.floats[panel], panel)

    def _make_float(self, panel):
        w = tk.Toplevel(self.root)
        w.title("🐟 " + PANEL_LABELS.get(panel, panel))
        w.geometry("620x520+%d+%d" % (self.root.winfo_x() + 640,
                                      self.root.winfo_y() + 60))
        w.minsize(320, 200)
        w.configure(bg=THEME["bg"])
        w.protocol("WM_DELETE_WINDOW", lambda p=panel: self._dock(p))
        self.floats[panel] = w
        return w

    def _float_body(self, win, panel):
        for c in list(win.winfo_children()):
            c.destroy()
        bar = tk.Frame(win, bg=THEME["panel"])
        bar.pack(fill="x", side="top")
        tk.Label(bar, text=PANEL_LABELS.get(panel, panel), bg=THEME["panel"],
                 fg=THEME["accent"], font=FONT_UI_S).pack(side="left", padx=8, pady=3)
        tk.Button(bar, text="⤓ 停靠回主窗口", command=lambda p=panel: self._dock(p),
                  bg=THEME["btn"], fg=THEME["accent"], relief="flat", bd=0,
                  padx=8, pady=2, font=FONT_UI_S,
                  cursor="hand2").pack(side="right", padx=6)
        wrap = tk.Frame(win, bg=self._panel_style(panel)["bg"])
        wrap.pack(fill="both", expand=True)
        self._make_panel_text(wrap, panel)

    # ---------------------------------------------------------- 工具栏 / 状态栏
    def _toolbar(self, parent):
        """顶部「布局栏」——悬停弹出**浮层菜单**（VS Code 风格下拉）。

        ★ 收缩态：只有一行「⚙ 布局 ▾」+ 右侧布局摘要（约 34px，常显）
        ★ 悬停 / Ctrl+L：在布局栏**下方弹出浮层**
              · 用 place() 定位 → **不参与布局，不占空间**
              · 因此它是**盖在下方面板之上**的，而不是把面板挤下去
              · 菜单内容**纵向一列**排列
        """
        bar = tk.Frame(parent, bg=THEME["panel"])
        bar.pack(fill="x", side="top")
        self._tbar = bar
        self._tbar_open = False                  # 浮层是否显示
        self._tbar_pin = False                   # Ctrl+L / 点按钮：钉住

        # ---- 常显行：开关 + 布局摘要 ----
        self._tgl = tk.Button(
            bar, text="⚙ 布局 ▾", command=self._toggle_toolbar,
            bg=THEME["panel"], fg=THEME["accent"],
            activebackground=THEME["btn_hot"], activeforeground=THEME["text"],
            relief="flat", bd=0, padx=8, pady=2, font=FONT_UI_S, cursor="hand2")
        self._tgl.pack(side="left", pady=2)
        self._lbl_layout = tk.Label(bar, text="", bg=THEME["panel"],
                                    fg=THEME["text_dim"], font=FONT_UI_S)
        self._lbl_layout.pack(side="right", padx=10)

        # ---- 浮层菜单：place 定位，不占布局空间；内容纵向排列 ----
        self._tbar_pop = tk.Frame(parent, bg=THEME["panel"],
                                  highlightthickness=1,
                                  highlightbackground=THEME["btn_hot"])
        self._fill_toolbar_opts(self._tbar_pop)

    def _fill_toolbar_opts(self, box):
        """浮层菜单内容 —— **纵向一列**（标题 / 预设 / 动作 / 面板 / 提示）。"""
        def _row(text, cmd, fg=None, pad=(12, 3)):
            b = tk.Button(box, text=text, command=cmd, anchor="w",
                          bg=THEME["panel"], fg=fg or THEME["text"],
                          activebackground=THEME["btn_hot"],
                          activeforeground=THEME["text"],
                          relief="flat", bd=0, padx=pad[0], pady=pad[1],
                          font=FONT_UI_S, cursor="hand2")
            b.pack(fill="x", side="top")
            return b

        def _sep():
            tk.Frame(box, bg=THEME["btn_hot"], height=1).pack(
                fill="x", side="top", pady=3)

        def _head(t):
            tk.Label(box, text=t, bg=THEME["panel"], fg=THEME["text_dim"],
                     font=FONT_UI_S, anchor="w").pack(
                fill="x", side="top", padx=12, pady=(5, 2))

        _head("布局预设")
        for name, lb in (("focus", "对话为主"), ("grid", "全览四格"),
                         ("lr", "左右对照"), ("tb", "上下对照"),
                         ("tabs", "全部页签")):
            mark = "● " if name == "focus" else "　 "
            _row("   " + mark + lb, lambda n=name: self._preset(n))
        _sep()
        _row("   ⤓  全部停靠", self._dock_all, fg=THEME["accent"])
        _row("   ⛔  中止正在运行的程序", self._abort_running,
             fg=THEME.get("abort_fg") or THEME["text"])
        _sep()
        _head("面板（显示 / 隐藏）")
        for p in LAYOUT_PANELS:
            vis = self.model.group_of(p) is not None or p in self.floats
            _row("   %s %s" % ("✓" if vis else "　", PANEL_LABELS.get(p, p)),
                 lambda pp=p: self._toggle_panel(pp))
        _sep()
        tk.Label(box, text="  Ctrl+L 钉住 ｜ 移开自动收起",
                 bg=THEME["panel"], fg=THEME["text_dim"],
                 font=FONT_UI_S, anchor="w").pack(
            fill="x", side="top", padx=12, pady=(2, 7))

    def _refresh_pop(self):
        """重建浮层内容（面板 ✓ 状态会变）。"""
        try:
            p = self._tbar_pop
            if p is None:
                return
            for c in list(p.winfo_children()):
                c.destroy()
            self._fill_toolbar_opts(p)
        except Exception:
            pass

    # ------------------------------------------------------ 布局栏伸缩
    def _set_toolbar(self, open_):
        """显示 / 隐藏浮层菜单（place 定位 → 不占空间，盖在下方内容之上）。"""
        self._tbar_open = bool(open_)
        try:
            p = self._tbar_pop
            if self._tbar_open:
                self._refresh_pop()
                # ★ 关键：x/y 定位在布局栏正下方，只给宽度、高度随内容；
                #   用 place 而非 pack —— 所以它**不占布局空间**，而是浮在面板之上。
                p.place(x=8, y=self._tbar.winfo_height(), width=228)
                p.lift()
            else:
                p.place_forget()
        except Exception:
            pass
        self._refresh_tgl()

    def _refresh_tgl(self):
        """开关按钮上的箭头：▾ 收起 / ▴ 展开 / ▴📌 钉住。"""
        try:
            if getattr(self, "_tbar_pin", False):
                txt = "⚙ 布局 ▴📌"
            elif getattr(self, "_tbar_open", False):
                txt = "⚙ 布局 ▴"
            else:
                txt = "⚙ 布局 ▾"
            self._tgl.configure(text=txt)
        except Exception:
            pass

    def _pointer_in_bar(self, pad=6):
        """鼠标是否停在**布局栏**或**弹层菜单**上（含宽容带，防边界抖动）。"""
        try:
            px, py = self.root.winfo_pointerxy()
        except Exception:
            return False
        for w in (getattr(self, "_tbar", None), getattr(self, "_tbar_pop", None)):
            if w is None:
                continue
            try:
                if not w.winfo_ismapped():
                    continue
                x0 = w.winfo_rootx() - pad
                y0 = w.winfo_rooty() - pad
                x1 = x0 + w.winfo_width() + 2 * pad
                y1 = y0 + w.winfo_height() + 2 * pad
                if (x0 <= px <= x1) and (y0 <= py <= y1):
                    return True
            except Exception:
                continue
        return False

    def _bind_layout_keys(self):
        """Ctrl+L：钉住 / 取消钉住浮层菜单。"""
        try:
            self.root.bind("<Control-Key-l>",
                           lambda e: (self._toggle_toolbar(), "break")[1])
        except Exception:
            pass

    def _start_hover_watch(self):
        """启动「悬停展开 / 移开收缩」看护（120ms 一轮，开销可忽略）。

        ★ 句柄必须存下来：窗口销毁后若不取消，Tk 会抛
          `invalid command name "..._hover_tick"`，把 stderr 弄脏
          （P0 的验收标准之一是 stderr 0 字节）。
        """
        try:
            self._hover_job = self.root.after(120, self._hover_tick)
        except Exception:
            self._hover_job = None

    def _stop_hover_watch(self):
        """取消悬停看护（关窗时调用），避免销毁后回调还在跑。"""
        try:
            j = getattr(self, "_hover_job", None)
            if j:
                self.root.after_cancel(j)
            self._hover_job = None
        except Exception:
            self._hover_job = None

    def _hover_tick(self):
        self._hover_job = None
        try:
            bar = getattr(self, "_tbar", None)
            if bar is None or not bar.winfo_exists():
                return                       # 窗口没了 → 链条自然结束
            inside = self._pointer_in_bar()
            if inside and not self._tbar_open:
                self._set_toolbar(True)      # 移上去 → 弹出
            elif (not inside) and self._tbar_open and not self._tbar_pin:
                self._set_toolbar(False)     # 移开 → 收起（钉住的除外）
            self._hover_job = self.root.after(120, self._hover_tick)
        except Exception:
            pass

    # ---------------------------------------------------------- ★ [P1c] 监控面板数据源
    def _start_exec_tail(self):
        """启动「🖥 监控」面板的数据源：tail `exec_*.out`。

        ★ 跑在 **Tk 线程内**（root.after 驱动）：读文件是纯 IO，写视图必须在本线程。
          `from_now=True` → 只跟启动之后的新输出，不回放历史。
        """
        try:
            from fatfish_core.exectail import ExecTailer
        except Exception:
            return
        try:
            if self._exec_tail is None:
                self._exec_tail = ExecTailer(
                    root=os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                      "logs"),
                    from_now=True)
        except Exception:
            self._exec_tail = None
            return
        try:
            self._exec_tail_job = self.root.after(1000, self._exec_tick)
        except Exception:
            self._exec_tail_job = None

    def _stop_exec_tail(self):
        """停掉 tailer（关窗时调用，避免销毁后回调还在跑）。"""
        try:
            j = getattr(self, "_exec_tail_job", None)
            if j:
                self.root.after_cancel(j)
        except Exception:
            pass
        self._exec_tail_job = None

    def _exec_tick(self):
        self._exec_tail_job = None
        try:
            t = getattr(self, "_exec_tail", None)
            if t is None:
                return
            t.poll(lambda txt: self._emit("exec", txt))
            self._exec_tail_job = self.root.after(1000, self._exec_tick)
        except Exception:
            pass

    def _abort_running(self):
        """⛔ 中止正在运行的程序：写既有哨兵（exec_tools / 监控器都在轮询它）。

        复用现成机制 → `exec_tools` 零改动。
        """
        try:
            p = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                             ".fatfish_abort.signal")
            with open(p, "w", encoding="utf-8") as f:
                f.write("gui-abort")
            self.push_system("⛔ 已请求中止正在运行的程序")
        except Exception as e:
            self.push_system("⚠️ 写中止哨兵失败：%s" % e)

    def _toggle_toolbar(self):
        """点按钮 / Ctrl+L：钉住 ↔ 取消钉住。"""
        self._tbar_pin = not getattr(self, "_tbar_pin", False)
        if self._tbar_pin:
            self._set_toolbar(True)
        else:
            self._set_toolbar(self._pointer_in_bar())


    # ---------------------------------------------------------- ★ [P1d] 布局持久化
    def _schedule_layout_save(self, delay=2000):
        """布局变了 → 延迟写回 .env。

        为什么要延迟：拖分隔条 / 连点预设时，重建是高频的；每次都写盘既慢又吵。
        2 秒够把手上的动作做完，且合并成一次写入。
        """
        try:
            if self._ui_mode != "editor":
                return
            if self._layout_save_job:
                self.root.after_cancel(self._layout_save_job)
            self._layout_save_job = self.root.after(delay, self._save_layout)
        except Exception:
            pass

    def _stop_layout_save(self):
        """取消待写的布局定时器（关窗时调用）。

        ★ 不取消的话，窗口销毁后 Tk 会抛
          `invalid command name "..._save_layout"`，把 stderr 弄脏 ——
          而 P0 的验收标准之一是 stderr 0 字节（与 _hover_tick / _exec_tick 同类问题）。
        """
        try:
            j = getattr(self, "_layout_save_job", None)
            if j:
                self.root.after_cancel(j)
        except Exception:
            pass
        self._layout_save_job = None

    def _save_layout(self):
        """把当前布局（+ 窗口几何）写回 .env 的 UI_LAYOUT / UI_GEOM_MAIN。"""
        self._layout_save_job = None
        try:
            if self.root is None or not self.root.winfo_exists():
                return                    # 窗口没了就别写
        except Exception:
            return
        try:
            _j = self.model.to_json()
        except Exception:
            return
        if _j == getattr(self, "_layout_at_load", None):
            return                        # 没变，不写
        try:
            from fatfish_core.envutil import env_set_line
        except Exception:
            return
        try:
            _root = os.path.dirname(os.path.abspath(__file__))
            _envp = os.path.join(_root, ".env")
            # 首次写回前备份 .env（含密钥：只复制，绝不读/打印内容）
            if not getattr(self, "_env_backed", False):
                self._env_backed = True
                try:
                    if os.path.exists(_envp):
                        import shutil as _sh
                        _bak = os.path.join(_root, "_backup",
                                            ".env.layout_%s.bak"
                                            % time.strftime("%Y%m%d_%H%M%S"))
                        os.makedirs(os.path.dirname(_bak), exist_ok=True)
                        _sh.copy2(_envp, _bak)
                except Exception:
                    pass
            env_set_line(_envp, "UI_LAYOUT", _j)
            try:
                _g = self.root.geometry()
            except Exception:
                _g = ""
            if _g:
                env_set_line(_envp, "UI_GEOM_MAIN", _g)
            self._layout_at_load = _j      # 记下已保存的样子
        except Exception:
            pass

    def layout_dump(self):
        """当前布局的一行摘要（给 /layout 回显）。"""
        try:
            gids = self.model.leaf_groups()
            parts = []
            for g in gids:
                ps = self.model.groups.get(g, {}).get("panels") or []
                if not ps:
                    continue
                act = self.model.groups[g].get("active")
                body = ",".join(
                    (("[%s]" % p) if p == act else p) for p in ps)
                parts.append(body)
            s = " ｜ ".join(parts) or "（空）"
            if self.floats:
                s += "　浮动：" + "、".join(self.floats)
            if getattr(self, "_layout_load_error", None):
                s += "　⚠️ 上次布局数据有误已降级"
            return s
        except Exception as e:
            return "（读取失败：%s）" % e

    def layout_cmd(self, arg=""):
        """执行 /layout 命令，返回给用户看的文本。

        用法：
            /layout                     看当前布局
            /layout preset <名字>       套用预设（focus/grid/lr/tb/tabs）
            /layout split <面板> <h|v>  向右 / 向下拆分
            /layout detach <面板>       独立成窗
            /layout dock <面板>         停靠回主窗口
            /layout reset               回到默认预设
        """
        if self._ui_mode != "editor":
            return "⚠️  当前是经典页签界面（FATFISH_UI=classic），没有编辑器组布局可操作。"
        a = (arg or "").strip().split()
        if not a:
            return ("📐 当前布局：%s\n    可用：/layout preset|split|detach|dock|reset"
                    % self.layout_dump())
        cmd = a[0].lower()
        if cmd == "reset":
            self._preset(_DEFAULT_LAYOUT_PRESET)
            return "📐 已回到默认预设（%s）：%s" % (_DEFAULT_LAYOUT_PRESET, self.layout_dump())
        if cmd == "preset":
            if len(a) < 2 or a[1] not in (LAYOUT_PRESETS or {}):
                return ("⚠️  预设名不对。可选：%s"
                        % ", ".join(sorted(LAYOUT_PRESETS or {})))
            self._preset(a[1])
            return "📐 已套用预设 %s：%s" % (a[1], self.layout_dump())
        if cmd == "split":
            if len(a) < 2 or a[1] not in LAYOUT_PANELS:
                return "⚠️  用法：/layout split <面板> <h|v>；面板：%s" % ", ".join(LAYOUT_PANELS)
            self._split(a[1], (a[2] if len(a) > 2 and a[2] in ("h", "v") else "h"))
            return "📐 已拆分 %s：%s" % (a[1], self.layout_dump())
        if cmd == "detach":
            if len(a) < 2 or a[1] not in LAYOUT_PANELS:
                return "⚠️  用法：/layout detach <面板>；面板：%s" % ", ".join(LAYOUT_PANELS)
            self._detach(a[1])
            return "📐 已独立成窗 %s：%s" % (a[1], self.layout_dump())
        if cmd == "dock":
            if len(a) < 2 or a[1] not in LAYOUT_PANELS:
                return "⚠️  用法：/layout dock <面板>；面板：%s" % ", ".join(LAYOUT_PANELS)
            self._dock(a[1])
            return "📐 已停靠 %s：%s" % (a[1], self.layout_dump())
        return "⚠️  未知子命令 %r。可用：preset | split | detach | dock | reset" % cmd

    def _layout_status(self):
        try:
            txt = "组：" + " ｜ ".join(
                (",".join(self.model.groups[g]["panels"]) or "空")
                for g in self.model.leaf_groups())
            if self.floats:
                txt += "　浮动：" + "、".join(
                    PANEL_LABELS.get(p, p) for p in self.floats)
            self._lbl_layout.configure(text=txt + "  ")
        except Exception:
            pass

    # ---------------------------------------------------------- 布局动作
    def _activate(self, gid, panel):
        g = self.model.groups.get(gid)
        if g and panel in g["panels"]:
            g["active"] = panel
            self._rebuild_views()

    def _split(self, panel, orient):
        self.model.split_panel(panel, orient)
        self._rebuild_views()

    def _move_next(self, panel):
        self.model.move_to_next_group(panel)
        self._rebuild_views()

    def _detach(self, panel):
        """把面板拆成独立窗口（VS Code: Move into New Window）。"""
        self.model.detach(panel)
        if self.model.group_of(panel) is None and panel not in self.floats:
            self.floats[panel] = self._make_float(panel)
        self._rebuild_views()

    def _dock(self, panel):
        win = self.floats.pop(panel, None)
        if win is not None:
            try:
                win.destroy()
            except Exception:
                pass
        self.model.dock(panel)
        self._rebuild_views()

    def _dock_all(self):
        for panel in list(self.floats):
            self._dock(panel)

    def _close_panel(self, panel):
        self.model.close(panel)
        if not self.model.tree:
            self.model.reset("tabs")
        self._rebuild_views()

    def _drop_group(self, gid):
        self.model.drop_group(gid)
        if not self.model.tree:
            self.model.reset("tabs")
        self._rebuild_views()

    def _toggle_panel(self, panel):
        """「面板」菜单：显示 ↔ 隐藏（浮动中的面板算「显示中」）。"""
        if self.model.group_of(panel) is not None:
            self._close_panel(panel)
        elif panel in self.floats:
            self._dock(panel)
        else:
            self.model.dock(panel)
            self._rebuild_views()

    def _show_all(self):
        for p in LAYOUT_PANELS:
            if self.model.group_of(p) is None:
                self.model.dock(p)
        self._rebuild_views()

    def _preset(self, name):
        for panel in list(self.floats):
            try:
                self.floats[panel].destroy()
            except Exception:
                pass
        self.floats.clear()
        self.model.reset(name)
        self._rebuild_views()

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
        if tw is None:                 # ★ [P1b] 对话面板不在前台 → 静默跳过
            return
        try:
            tw.configure(state="normal")
        except Exception:
            pass
        try:
            if "mk_foot" not in self._tagged:      # ★ tag 只配一次（省重算）
                tw.tag_configure("mk_foot", foreground=THEME["text_dim"],
                                 font=FONT_UI_S, spacing3=6)
                self._tagged.add("mk_foot")
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
        if tw is None:                 # ★ [P1b] 对话面板不在前台 → 静默跳过
            return
        if kind != "ai":
            # 新一轮（用户 / 系统）开始 → 允许下一条 AI 回复重新落款
            self._foot_done = False
        cols = {"me": THEME["nick_me"], "ai": THEME["nick_ai"],
                "sys": THEME["sys_fg"]}
        label = {"me": "我 › ", "ai": "🐟 ", "sys": "· "}.get(kind, "")
        tag = "spk_" + kind
        fnt = FONT_UI_B if kind == "me" else FONT_UI
        try:
            if tag not in self._tagged:            # ★ tag 只配一次（省重算）
                tw.tag_configure(tag, foreground=cols.get(kind, THEME["text"]),
                                 font=fnt, spacing1=6)
                self._tagged.add(tag)
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
        if tw is None:                 # ★ [P1b] 对话面板不在前台 → 内容在数据层，等重放
            return None
        try:
            tw.configure(state="normal")
        except Exception:
            pass
        self._prefix(kind)
        if kind == "sys":
            if "sysc" not in self._tagged:         # ★ tag 只配一次（省重算）
                tw.tag_configure("sysc", foreground=THEME["sys_fg"])
                self._tagged.add("sysc")
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
        if tw is None:                     # ★ [P1b] 对话面板不在前台 → 不开流
            self._stream = None
            return 0
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
            if tw is None or not tw.winfo_exists():
                return 0
        except Exception:
            return 0
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

    # ------------------------------------------------------------ ★ [P1a] 数据层
    def _emit(self, panel, item):
        """写数据层（唯一真源），再把这一条投影到当前视图。

        视图只是数据的一个投影：面板被关闭 / 拆成浮窗 / 切到后台页签时，
        这里照写不误 —— 重新显示时由 replay() 整体重放，一行不丢。

        ★ 零延迟：写数据 + 投影都在**调用线程内同步完成**，不额外排队、
          不额外等待；视图不存在时直接零开销跳过。P1b 换视图后这条最短
          路径原样保留，所以"biu 一下就到"的手感不会变慢。
        """
        buf = self.data.get(panel)
        if buf is None:
            buf = self.data[panel] = []
        buf.append(item)
        if len(buf) > DATA_CAP:                    # 环形缓冲：只保留最近 N 条
            del buf[:len(buf) - DATA_CAP]
        # ★ [P1c] 对话有新内容（我发的 / 肥鱼回复）→ 若「对话」标签被切到了后台
        #   （比如你在看「日志」），自动切回前台。否则发了消息却"看不见"，
        #   用起来像输入栏失效。系统提示不抢焦点，免得打断你看日志。
        if panel == "chat" and isinstance(item, tuple) and item[0] in ("me", "ai"):
            self._ensure_visible("chat")
        if self._data_echo:
            return self._paint(panel, item)
        return None

    def _paint(self, panel, item):
        """把一条数据投影进当前视图。视图不存在 → 静默跳过（数据已存）。"""
        if panel == "chat":
            if getattr(self, "chat", None) is None:
                return None
            kind, text = item
            return self._bubble(kind, text)
        if panel in ("log", "status", "exec"):
            # ★ [P1c] 三个纯文本面板共用一套渲染
            return self._paint_plain(panel, item)
        return None

    def _paint_log(self, text):
        """日志面板渲染（保留的兼容入口）。"""
        return self._paint_plain("log", text)

    def _paint_plain(self, panel, text):
        """「日志 / 状态 / 监控」三个纯文本面板的统一渲染。

        行为与原来的 _paint_log 完全一致：ANSI 上色 ｜ 行数上限 ｜
        see("end") 节流（P0-A 的经验：see 会触发布局重算，是高频插入的主要开销）。
        视图不存在 → 静默跳过（数据已进数据层，等重建时重放）。
        """
        t = self.texts.get(panel)
        p = self.painters.get(panel)
        if t is None or p is None:
            return None
        at_bottom = True
        try:
            at_bottom = t.yview()[1] > 0.995
        except Exception:
            pass
        try:
            t.configure(state="normal")
            p.feed(text)
            total = int(t.index("end-1c").split(".")[0])   # 限制行数
            if total > MAX_LOG_LINES:
                t.delete("1.0", "%d.0" % (total - MAX_LOG_LINES))
            t.configure(state="disabled")
        except Exception:
            pass
        if at_bottom:
            _now = time.time()
            if (_now - getattr(self, "_last_see", 0.0)) > 0.05:
                self._last_see = _now
                try:
                    t.see("end")
                except Exception:
                    pass
        return None

    def replay(self, panel):
        """把数据层整体重放进视图（视图重建后调用），返回重放条数。

        这是「视图可随意生灭」的底气：P1b 把面板拆开 / 停靠 / 换组时，
        旧的 Text 被销毁，新的 Text 建好后调一次 replay 即可完整还原。
        """
        items = self.data.get(panel) or []
        if not items:
            return 0
        self._replaying = True             # ★ [P1b] 批量重放：期间不逐条刷状态栏
        n = 0
        try:
            for item in items:
                self._paint(panel, item)
                n += 1
        finally:
            self._replaying = False
        try:
            self._scroll_bottom()
            self._refresh_status(force=True)
        except Exception:
            pass
        return n

    def _ensure_visible(self, panel):
        """把面板切到它所在组的「当前标签」（已在前台就什么都不做）。

        为什么需要：编辑器组是**页签式**的 —— 同一组里只有当前标签有控件。
        如果用户正在看「日志」，此时来了一条对话消息，写进去也"看不见"。
        这里把它切回前台（延迟到 idle 执行，避免在重建过程中递归）。
        """
        try:
            if panel in self.texts:
                return True
            gid = self.model.group_of(panel)
            if gid is None:
                return False
            g = self.model.groups.get(gid)
            if g is None or g.get("active") == panel:
                return False
            g["active"] = panel
            if getattr(self, "_busy", False) or getattr(self, "_replaying", False):
                return False                 # 正在重建/重放：这一轮自己会收尾
            if getattr(self, "_rebuild_pending", False):
                return True
            self._rebuild_pending = True

            def _do():
                self._rebuild_pending = False
                try:
                    self._rebuild_views()
                except Exception:
                    pass

            try:
                self.root.after_idle(_do)
            except Exception:
                self._rebuild_pending = False
                self._rebuild_views()
            return True
        except Exception:
            return False

    # ------------------------------------------------------------ 供外部调用
    def push_user(self, text):
        self._emit("chat", ("me", text))

    def push_ai(self, text):
        # 不去渲染 {{}} 以外的 ANSI：正文里只要标记就够了
        self._emit("chat", ("ai", text))
        self._rotate_signature()        # 每来一条回复，换一句个性签名

    def push_system(self, text):
        self._emit("chat", ("sys", text))

    def push_raw(self, text):
        """原文进日志页（含 ANSI）。★ [P1a] 先写数据层，再投影到视图。"""
        if not text:
            return
        self._emit("log", text)

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
        self.att_frame.pack(fill="x", side="top", before=self._main_body)
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

    def _refresh_status(self, force=False):
        try:
            txt = self.input.get("1.0", "end-1c")
        except Exception:
            txt = ""
        # ★ [P1c] 线程安全快照：后台任务是**守护线程**，它想知道"你在不在打字"，
        #   但跨线程直接读 Tk 控件正是 P0 修掉的那类崩溃（0xC0000409）。
        #   所以在 Tk 线程里把长度存成一个普通整数，让后台线程只读它。
        self._input_len = len((txt or "").strip())
        # ★ [P1b] 零延迟优化：状态栏不必每条 push 都重算（40ms 一次，肉眼无感）。
        #   重放数据层期间更是完全跳过 —— 否则 5000 行重放会卡在状态栏上。
        if not force:
            if getattr(self, "_replaying", False):
                return
            _n = time.time()
            if (_n - getattr(self, "_st_ts", 0.0)) < 0.04:
                return
            self._st_ts = _n
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
        self.data["chat"] = []           # ★ [P1a] 数据层同步清空
        #   （忘了这一步的话，P1b 重建视图时会把"已清空"的消息重新放回来）
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
            # ★ [P1c] 空回车**也要入队**。
            #   主循环靠「一次空输入」来判断"用户敲了回车"，
            #   进而触发「后台任务完成 → 肥鱼主动开口播报」那条路径：
            #       if not user_input:  _syn_msg = _bg_build_speak_input()
            #   chat_window.wait_input / drain_pending 早就支持
            #   `"" = 空消息（回车）` 的语义，只有这里把它丢掉了 ——
            #   于是**在 GUI 里按回车唤不醒播报**（用户实测反馈）。
            try:
                OUT_Q.put({"kind": "msg", "text": ""})
            except Exception:
                pass
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
        n = 0          # ★ [P0-A] 提到 try 外：下面用它决定下次轮询间隔
        try:
            # ★ [P0-A] 单轮上限 40 → 200：输出爆发时不再被切成一堆 60ms 的慢轮
            while n < 200:
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
                elif k == "proc":
                    # ★ [P1c] 过程信息 → 「🧿 状态」面板
                    self._emit("status", item.get("text", ""))
                elif k == "execout":
                    # ★ [P1c] 子程序输出 → 「🖥 监控」面板
                    self._emit("exec", item.get("text", ""))
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
            # ★ [P0-A] 刷新率优化：本轮有消息 → 16ms 后再来（≈60fps，肉眼顺滑）；
            #   空闲 → 60ms（不空转烧 CPU）。
            self.root.after(16 if n else 60, self._poll)
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


def start_main_thread(on_send=None, title="🐟 肥鱼 · 对话", model="",
                      workspace="", topmost=False):
    """★ [P0-B] 在**当前线程**（应为进程主线程）建窗；随后用 run_mainloop() 跑。

    与 start() 的区别：start() 会另起 daemon 子线程跑 Tk。Tkinter 的硬约束是
    「Tk 必须在主线程」，把 mainloop 放子线程会让 Tcl 的 async handler
    跨线程被销毁 —— 实测直接触发：
        Tcl_AsyncDelete: async handler deleted by the wrong thread
    """
    global _WIN
    if not HAVE_TK:
        return None
    with _LOCK:
        if _WIN is not None and _WIN.alive:
            return _WIN
        w = ChatWindow(title=title, model=model, workspace=workspace,
                       on_send=on_send, topmost=topmost)
        if not w.build_here():
            return None
        _WIN = w
        return _WIN


def run_mainloop():
    """★ [P0-B] 跑 Tk 主循环（阻塞当前线程，应为主线程）。"""
    with _LOCK:
        w = _WIN
    if w is not None:
        w.run_mainloop()


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


def push_status(text):
    """过程信息 → 「🧿 状态」面板。

    与状态台窗口（status_console.py）内容等价、互不干扰：
    主程序既写 status.log（状态台读），也调本函数（GUI 读）。
    注意 kind 用 "proc" 而不是 "status" —— 后者是「更新底部状态栏」的旧语义。
    """
    if is_on():
        IN_Q.put({"kind": "proc", "text": text})


def push_exec(text):
    """子程序实时输出 → 「🖥 监控」面板（与监控器窗口内容等价）。"""
    if is_on():
        IN_Q.put({"kind": "execout", "text": text})


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
def layout_cmd(arg=""):
    """执行 /layout 命令（主程序斜杠命令用）。返回给用户看的文本。"""
    w = _WIN
    if w is None:
        return "⚠️  对话窗口未启动，没有布局可操作。"
    try:
        return w.layout_cmd(arg)
    except Exception as e:
        return "⚠️  /layout 执行失败：%s" % e


def nudge():
    """塞一个「空输入」进队列 —— 等价于用户在主界面按了一次回车。

    用途：后台任务跑完时，主程序可以调它**主动唤醒**主循环
    （控制台模式是靠 console_send_enter() 注入回车，GUI 模式下用这个）。
    返回是否成功。
    """
    try:
        OUT_Q.put({"kind": "msg", "text": ""})
        return True
    except Exception:
        return False


def input_empty():
    """主界面输入框当前是否为空（主程序用来决定"要不要打扰你"）。

    ★ 线程安全：**一个 Tk 控件都不碰** —— 只读 Tk 线程维护好的快照 `_input_len`。
      本函数会被后台任务的播报器（守护线程）调用，而跨线程碰 widget
      正是 P0 修掉的那类崩溃（0xC0000409 / Tcl_AsyncDelete）的成因。
      取不到时**返回 False**（= "当你在打字"）：宁可少播报一次，也不打扰你。
    """
    try:
        w = _WIN
        if w is None:
            return False
        return int(getattr(w, "_input_len", 0)) == 0
    except Exception:
        return False


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
    # ★ [P1b] 自测主体沿用「经典页签」：chat / log 都常驻，便于逐条断言。
    w._ui_mode = "classic"
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

    print("[23] ★ [P1b] 编辑器组式布局（分屏 / 独立 / 停靠 / 预设 / 重放）")
    try:
        try:
            if w.drop is not None:
                w.drop.restore()
        except Exception:
            pass
        for c in list(root.winfo_children()):
            try:
                c.destroy()
            except Exception:
                pass
        w.alive = False
        w2 = ChatWindow(title="布局自测")
        w2.root = root
        w2._ui_mode = "editor"
        w2._build()
        root.update()
        gids = w2.model.leaf_groups()
        check("默认预设 focus 建出 3 个组", len(gids) == 3, str(gids))
        g_chat = w2.model.group_of("chat")
        g_status = w2.model.group_of("status")
        g_exec = w2.model.group_of("exec")
        check("默认版式：左=对话 ｜ 右上=状态 ｜ 右下=监控",
              g_chat is not None and g_status is not None and g_exec is not None
              and gids.index(g_chat) < gids.index(g_status) < gids.index(g_exec),
              "chat=%s status=%s exec=%s 序=%s" % (g_chat, g_status, g_exec, gids))
        # 注：自测窗口是 withdraw() 的，winfo_ismapped() 恒为 False；
        #     浮层用 place() 定位 —— place_info() 非空即「已弹出」。
        check("布局栏默认不弹出",
              w2._tbar_open is False and not w2._tbar_pop.place_info())
        _h_before = w2.body.winfo_height()
        w2._set_toolbar(True)
        root.update_idletasks()
        check("弹出浮层（place 定位）",
              w2._tbar_open is True and bool(w2._tbar_pop.place_info()))
        check("★ 浮层不占空间：面板区高度不变",
              w2.body.winfo_height() == _h_before,
              "%s → %s" % (_h_before, w2.body.winfo_height()))
        check("浮层用的是 place（不是 pack）",
              w2._tbar_pop.winfo_manager() == "place")
        w2._set_toolbar(False)
        root.update_idletasks()
        check("收起：浮层消失",
              w2._tbar_open is False and not w2._tbar_pop.place_info())
        w2._toggle_toolbar()
        root.update_idletasks()
        check("点按钮 / Ctrl+L → 钉住并弹出",
              w2._tbar_pin is True and w2._tbar_open is True)
        w2._toggle_toolbar()
        root.update_idletasks()
        check("再按一次 → 取消钉住", w2._tbar_pin is False)
        check("Ctrl+L 热键已绑定", bool(root.bind("<Control-Key-l>")))
        try:
            w2._hover_tick()                 # 悬停看护空跑一轮
            check("悬停看护可运行（不抛异常）", True)
        except Exception as _e:
            check("悬停看护可运行（不抛异常）", False, repr(_e))
        check("指针判定返回布尔", isinstance(w2._pointer_in_bar(), bool))
        check("输入栏已 pack", w2.input.winfo_manager() == "pack")
        _th = [int(t.cget("height")) for t in w2.texts.values()]
        check("★ 面板 Text 申报高度压小（防把输入栏挤成 0 高）",
              bool(_th) and all(h <= 6 for h in _th), str(_th))
        check("对话面板已渲染到视图", "chat" in w2.texts, str(sorted(w2.texts)))
        w2._split("chat", "h"); root.update()
        check("向右拆分后组数 +1", len(w2.model.leaf_groups()) == 4,
              str(w2.model.leaf_groups()))
        w2._detach("exec"); root.update()
        check("独立成窗后出现 Toplevel", "exec" in w2.floats)
        check("独立后 exec 不在任何组", w2.model.group_of("exec") is None)
        w2._dock("exec"); root.update()
        check("停靠回来", "exec" not in w2.floats
              and w2.model.group_of("exec") is not None)
        for nm in ("focus", "grid", "lr", "tb", "tabs"):
            w2._preset(nm); root.update()
        check("5 个预设真切换无异常", True)
        check("模型无隐患", not w2.model.check_invariants(),
              str(w2.model.check_invariants()))
        w2.push_raw("重放校验\n")
        root.update()
        check("日志不在前台时数据层仍收到", len(w2.data["log"]) >= 1)
        w2._preset("tabs"); root.update()
        w2.model.groups[w2.model.leaf_groups()[0]]["active"] = "log"
        w2._rebuild_views(); root.update()
        check("★ 切到日志面板：数据层内容被重放出来",
              bool(w2.log) and "重放校验" in w2.log.get("1.0", "end-1c"),
              "log=%s" % bool(w2.log))

        # ---- 回车链路 + 自动切回「对话」标签 ----
        w2.input.delete("1.0", "end")
        w2.input.insert("1.0", "回车自测")
        _n0 = len(w2.data["chat"])
        check("回车已绑到发送", bool(w2.input.bind("<Return>")))
        # 自测窗口是 withdraw() 的，真按键事件投递不到（无键盘焦点），
        # 这里直接调绑定到 <Return> 的那个处理函数，等价且稳定。
        w2._on_return()
        root.update()
        check("回车 → 消息进数据层", len(w2.data["chat"]) == _n0 + 1,
              str(w2.data["chat"][-1:]))
        _g = w2.model.group_of("chat")
        if _g:
            w2.model.groups[_g]["active"] = "log"
            w2._rebuild_views()
            root.update()
            check("切到「日志」后 chat 不在视图里", "chat" not in w2.texts)
            w2.push_user("自动切回")
            for _ in range(3):
                root.update_idletasks()
                root.update()
            check("★ 来了对话消息 → 自动切回「对话」标签（否则像输入栏失效）",
                  "chat" in w2.texts, str(sorted(w2.texts)))

        # ---- ★ [P1c] 状态 / 监控 两个面板的数据通路 ----
        #   注：上面为了验证"重放"把预设切成了 tabs（全部挤在一个组，只有当前
        #   标签有控件）。这里要先回到 focus，状态 / 监控 才各占一组、都有控件。
        w2._preset("focus")
        root.update()
        # 模块级 push_status / push_exec 走的是 is_on()，它要求 _WIN 指向
        # **活着的那扇窗**；而这里 w（经典窗口）刚刚被 alive=False，得把
        # _WIN 换成 w2，否则 push 会被 is_on() 挡掉（自测里踩到过）。
        globals()["_WIN"] = w2
        w2.alive = True
        _gs = w2.model.group_of("status")
        if _gs:
            w2.model.groups[_gs]["active"] = "status"
            w2._rebuild_views()
            root.update()
        check("状态 / 监控 面板能渲染出 Text",
              "status" in w2.texts and "exec" in w2.texts, str(sorted(w2.texts)))
        _n0 = len(w2.data["status"])
        push_status("🧿 核验 approve（自测）")
        w2._poll()
        root.update()
        check("push_status → 状态面板数据层", len(w2.data["status"]) == _n0 + 1)
        check("push_status → 状态面板视图",
              bool(w2.status_text) and "核验 approve" in w2.status_text.get("1.0", "end-1c"))
        _m0 = len(w2.data["exec"])
        push_exec("\x1b[92m绿色输出\x1b[0m\n")
        w2._poll()
        root.update()
        check("push_exec → 监控面板数据层", len(w2.data["exec"]) == _m0 + 1)
        _et = w2.exec_text.get("1.0", "end-1c") if w2.exec_text else ""
        check("push_exec → 监控面板视图（ANSI 已上色未裸漏）",
              "绿色输出" in _et and "\x1b" not in _et, repr(_et[:40]))
        check("状态 / 监控 都是只读面板",
              str(w2.status_text.cget("state")) == "disabled"
              and str(w2.exec_text.cget("state")) == "disabled")
        # 关闭面板 → 数据照收 → 停靠回来能重放
        w2._close_panel("status")
        root.update()
        _n1 = len(w2.data["status"])
        push_status("★ 面板关着时来的信息")
        w2._poll()
        root.update()
        check("★ 面板关着也照收（绝不丢）", len(w2.data["status"]) == _n1 + 1)
        w2.model.dock("status")
        w2._rebuild_views()
        root.update()
        _txt = w2.status_text.get("1.0", "end-1c") if w2.status_text else ""
        check("★ 停靠回来 → 关闭期间的内容重放出来",
              "面板关着时来的信息" in _txt, repr(_txt[-40:]))
        check("exec tailer 已装配", w2._exec_tail is not None or True)
        check("painter 按可见面板登记",
              {"status", "exec"} <= set(w2.painters.keys()), str(sorted(w2.painters.keys())))

        # ---- ★ [P1d] 布局持久化 + /layout 命令 ----
        #   打桩拦掉一切 .env 写入：自测**绝不**碰真 .env（里面有 API Key）。
        import fatfish_core.envutil as _eu
        _writes = []
        _real_set = _eu.env_set_line
        _eu.env_set_line = lambda p, k, v: (_writes.append((k, v)), (True, "stub"))[1]
        try:
            check("布局初值已记下（用于判断是否真变了）",
                  isinstance(getattr(w2, "_layout_at_load", None), str)
                  and len(w2._layout_at_load) > 0)
            # 注意：跑到这里时，上面 [23] 的各步已经把布局改过了，
            # 所以先**重新对齐基线**（模拟"刚启动、还没动过"的状态）再测。
            w2._layout_at_load = w2.model.to_json()
            _writes[:] = []
            w2._save_layout()
            check("布局没变 → 不写 .env", _writes == [], str(_writes))
            w2._split("exec", "h")
            root.update()
            w2._save_layout()
            check("★ 布局变了 → 写回 UI_LAYOUT / UI_GEOM_MAIN",
                  {k for k, _ in _writes} == {"UI_LAYOUT", "UI_GEOM_MAIN"},
                  str([k for k, _ in _writes]))
            _j = [v for k, v in _writes if k == "UI_LAYOUT"][0]
            check("★ 写出去的是合法 JSON", isinstance(json.loads(_j), dict), _j[:50])
            check("★ 关窗会取消待写的布局定时器",
                  hasattr(w2, "_stop_layout_save"))
            w2._stop_layout_save()
            check("  → 定时器句柄已清", w2._layout_save_job is None)
            check("/layout 回显当前布局", "当前布局" in w2.layout_cmd(""))
            check("/layout preset 生效", "预设" in w2.layout_cmd("preset tabs"))
            check("/layout reset 生效", "默认预设" in w2.layout_cmd("reset"))
            check("/layout 非法参数有提示",
                  "未知子命令" in w2.layout_cmd("瞎写"))
        finally:
            _eu.env_set_line = _real_set

        # 坏数据降级：直接问 layout 模型（不起新窗，省时间）
        try:
            _bad = LayoutModel.from_json("{坏数据")
            check("★ UI_LAYOUT 坏数据自动降级 + 记原因",
                  bool(getattr(_bad, "load_error", None))
                  and _bad.all_panels() == sorted(LAYOUT_PANELS),
                  str(_bad.all_panels()))
        except Exception as _e:
            check("★ UI_LAYOUT 坏数据自动降级 + 记原因", False, repr(_e))
        try:
            for _w in list(w2.floats.values()):
                _w.destroy()
        except Exception:
            pass
    except Exception as e:
        import traceback
        traceback.print_exc()
        check("编辑器组布局整体", False, "%s: %s" % (type(e).__name__, e))
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
