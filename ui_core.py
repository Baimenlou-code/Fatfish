# -*- coding: utf-8 -*-
"""
ui_core.py —— 肥鱼展示层 / FatFish UI Core

职责 / What it does
------------------
把「给人和给模型看的输出」这一层从主程序里剥离出来，单独成模块：

  · 颜色常量      ：RESET / K R G Y BL M CY W / BK BR BG BY BB BM BC BW /
                    BOLD DIM ITAL UND RV ST / BGR BGG BGY BGB
  · 颜色构造      ：fg256 / bg256 / rgb / paint / rainbow / gradient
  · 情绪判定      ：MOOD_COLOR / MOOD_EMOJI / MOOD_KEYWORDS / MOOD_ORDER / detect_mood
  · 行内标记渲染  ：TAG_COLOR / TAG_RE / _tag / render_markup / strip_markup / render_inline
  · 整块输出      ：print_banner（启动横幅）/ print_ai（AI 回复着色渲染）

不负责 / Not here
----------------
  · `print_startup_status` 留在主程序 —— 它要读 MODEL / BASE_URL / net_tools /
    workspace / verify_tools 等一堆运行时全局量，属「业务状态展示」而非纯展示。
  · 任何文件 IO、网络、日志 —— 本模块是纯展示层，无副作用。

数据一致性说明 / Invariants
--------------------------
  · MOOD_COLOR 有 8 种情绪；MOOD_ORDER 只列 7 种优先级，
    **"calm" 是兜底项，故意不进优先级表**（无关键词命中时直接返回 calm）。
  · MOOD_EMOJI 与 MOOD_COLOR 键集相同（8 种）。

剥离依据 / Why it can be split off
---------------------------------
AST 依赖分析：本模块 46 个成员，**外部全局依赖数 = 0**，唯一依赖是标准库 `re`。
搬移为逐字等价的代码移动（仅补 docstring 与排版规整），主程序只需改一处 import。

后续增补 / Later additions
------------------------
  · `Wait`（等待动画，- \ | / 轮转，2026-09-19 新增）：为「等待期间看得见在跑」
    而加；只多引入标准库 `sys` / `time` / `io` / `threading`，仍**无第三方依赖**。
    默认写 stderr，探测到非 TTY 时自动降级为「只报一行结果」。

自测 / Self-test
---------------
    python ui_core.py
"""

import sys
import time
import io
import re
import threading
import unicodedata

__all__ = [
    # 颜色常量
    "RESET", "K", "R", "G", "Y", "BL", "M", "CY", "W",
    "BK", "BR", "BG", "BY", "BB", "BM", "BC", "BW",
    "BOLD", "DIM", "ITAL", "UND", "RV", "ST",
    "BGR", "BGG", "BGY", "BGB",
    # 构造器
    "fg256", "bg256", "rgb", "paint", "rainbow", "gradient",
    # 情绪
    "MOOD_COLOR", "MOOD_EMOJI", "MOOD_KEYWORDS", "MOOD_ORDER", "detect_mood",
    # 行内渲染
    "TAG_COLOR", "TAG_RE", "render_markup", "strip_markup", "render_inline",
    # 整块输出
    "print_banner", "print_ai",
    # 等待动画
    "WAIT_FRAMES", "WAIT_FRAMES_REV", "Wait",
    # 轮次落款
    "cost_tag", "mark_footer",
    # 个性签名 / 左下角状态行
    "SIGNATURES", "signature", "WAIT_ANIM_DOCK", "console_status",
]

# ============ 颜色常量 ============
# 说明：这些是 ANSI 转义序列。Windows 10+ 终端默认支持；不支持时只会显示原始
# 转义码，不会报错，故无需在启动时探测终端能力。

RESET = "\033[0m"
K   = "\033[30m"; R   = "\033[31m"; G   = "\033[32m"; Y   = "\033[33m"
BL  = "\033[34m"; M   = "\033[35m"; CY  = "\033[36m"; W   = "\033[37m"
BK  = "\033[90m"; BR  = "\033[91m"; BG  = "\033[92m"; BY  = "\033[93m"
BB  = "\033[94m"; BM  = "\033[95m"; BC  = "\033[96m"; BW  = "\033[97m"
BOLD = "\033[1m"; DIM = "\033[2m"; ITAL = "\033[3m"
UND  = "\033[4m"; RV  = "\033[7m"; ST  = "\033[9m"
BGR  = "\033[41m"; BGG = "\033[42m"; BGY = "\033[43m"; BGB = "\033[44m"


# ============ 个性签名（2026-10-02）============
#   古早 QQ 的味道：细节处来一句没头没尾的话。
#   用在：启动横幅下、退出时、对话窗口底栏。想加就往下塞。
SIGNATURES = [
    "签名是一种态度，我想我可以很酷。",
    "别问，问就是在摸鱼。",
    "人生苦短，我用 Python。",
    "正在加载人生，请稍候…",
    "今天的 Bug，明天的经验。",
    "我是一条咸鱼，但我很快乐。",
    "代码三千，只取一瓢饮。",
    "不加班，是我最后的倔强。",
    "世界那么大，我先修个 Bug。",
    "吃 Token 长大的鱼。",
    "能跑就行，别问为什么。",
    "只要跑得够快，Bug 就追不上我。",
    "认真的鱼最帅。",
    "浅水喧哗，深水沉默 —— 我属于后者。",
    "与其感慨路难行，不如马上出发。",
    "保持热爱，奔赴山海。",
    "心有猛虎，细嗅蔷薇。",
    "路过人间，顺手写码。",
    "愿你出走半生，归来仍是少年。",
    "不问归期，只争朝夕。",
    "做一个安静的美鱼子。",
    "沉默是金，但沉默也扣钱。",
    "所有的不顺，都是为了更好的相遇。",
    "今天也要元气满满地吃 Token。",
]


def signature():
    """随机取一句个性签名；取不到返回空串，绝不抛异常。"""
    try:
        import random as _rnd
        return _rnd.choice(SIGNATURES)
    except Exception:
        return ""


def _env_flag(name, default=True):
    """读环境变量当开关；空值 / 非法值一律退回默认。"""
    try:
        import os as _o
        v = str(_o.environ.get(name, "") or "").strip().lower()
        if not v:
            return bool(default)
        return v not in ("0", "off", "false", "no", "关", "关闭")
    except Exception:
        return bool(default)


def _env_str(name, default=""):
    """读环境变量当字符串；空值退回默认（绝不抛异常）。"""
    try:
        import os as _o
        v = str(_o.environ.get(name, "") or "").strip()
        return v or default
    except Exception:
        return default


# 转圈圈钉在【控制台左下角】；想退回原来的「同行原地刷新」就把环境变量设成 0
WAIT_ANIM_DOCK = _env_flag("WAIT_ANIM_DOCK", True)

# 每轮回复末尾的落款（挂在时间后面）：时间 + 本轮耗时（见 fatfish_core/roundtime.py）
from fatfish_core.roundtime import cost_tag, mark_footer     # noqa: E402


def fg256(n):
    """256 色前景。"""
    return f"\033[38;5;{n}m"


def bg256(n):
    """256 色背景。"""
    return f"\033[48;5;{n}m"


def rgb(r, g, b, back=False):
    """真彩色（默认前景；back=True 为背景）。"""
    return f"\033[{'48' if back else '38'};2;{r};{g};{b}m"


def paint(text, *styles):
    """给文本套上若干 ANSI 样式，结尾统一 RESET。"""
    return "".join(styles) + str(text) + RESET


def rainbow(text):
    """逐字符彩虹色（256 色轮转）。"""
    pal = [196, 202, 208, 214, 220, 226, 190, 154, 118, 82,
           46, 47, 51, 45, 39, 33, 27, 57, 93, 129, 165, 201]
    return "".join(fg256(pal[i % len(pal)]) + ch
                   for i, ch in enumerate(text)) + RESET


def gradient(text, c1, c2):
    """从颜色 c1 到 c2 的线性渐变（逐字符插值，真彩色）。"""
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
# 供 print_ai 使用：按回复内容判定情绪，换一套渐变色 + 表情，让回复有"脸色"。

MOOD_COLOR = {
    "happy":   ((255, 215,   0), (255, 140,   0)),
    "excited": ((255, 105, 180), (255,  20, 147)),
    "love":    ((255, 182, 193), (255,   0, 102)),
    "calm":    ((135, 206, 235), ( 70, 130, 180)),
    "sad":     ((100, 149, 237), ( 72,  61, 139)),
    "error":   ((255,  69,   0), (139,   0,   0)),
    "code":    ((  0, 255, 127), (  0, 191, 255)),
    "think":   ((200, 200, 200), (150, 150, 150)),
}
MOOD_EMOJI = {
    "happy": "😊", "excited": "✨", "love": "❤️ ",
    "calm": "🤖",  "sad": "🥺",    "error": "⚠️ ",
    "code": "💻",  "think": "🤔",
}
MOOD_KEYWORDS = {
    "error":   ["错误", "异常", "失败", "无法", "报错",
                "error", "Error", "failed", "Traceback"],
    "sad":     ["抱歉", "遗憾", "可惜", "难过"],
    "happy":   ["太好了", "成功", "恭喜", "搞定", "完成",
                "👍", "🎉", "哈哈", "nice"],
    "excited": ["哇", "太棒", "惊人", "震撼", "！！"],
    "love":    ["喜欢", "爱你", "❤", "😊"],
    "think":   ["让我想想", "分析一下", "考虑", "首先", "推理"],
    "code":    ["```", "def ", "class ", "import ", "function "],
}
# 注意：calm 是"无命中"时的兜底返回值，因此**不列入**优先级表。
MOOD_ORDER = ["error", "code", "think", "sad", "happy", "love", "excited"]


def detect_mood(text):
    """按关键词命中数判定情绪；无命中则 calm。

    优先级由 MOOD_ORDER 决定（错误 > 代码 > 思考 > ……），
    保证"既像报错又像成功"时不会误判成 happy。
    """
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
# 模型可以用 {{green}}成功{{/green}} 这类标记给输出上色。

TAG_COLOR = {
    "red": BR, "green": BG, "yellow": BY, "blue": BB,
    "cyan": BC, "magenta": BM, "white": BW, "gray": BK, "grey": BK,
    "bold": BOLD, "italic": ITAL, "underline": UND,
    "dim": DIM, "strike": ST,
}
TAG_RE = re.compile(r"\{\{(\w+)\}\}(.*?)\{\{/\1\}\}", re.DOTALL)


def _tag(tag, content):
    """把单个 {{tag}}...{{/tag}} 转成 ANSI；未知标签原样返回。"""
    t = tag.lower()
    if t == "rainbow":
        return rainbow(content)
    return paint(content, TAG_COLOR[t]) if t in TAG_COLOR else content


def render_markup(text):
    """渲染 {{}} 颜色/样式标记。"""
    return TAG_RE.sub(lambda m: _tag(m.group(1), m.group(2)), text)


def strip_markup(text):
    """去掉标记与常见 markdown 装饰，得到纯文本（用于写日志）。"""
    text = TAG_RE.sub(lambda m: m.group(2), text)
    text = re.sub(r"\*\*(.+?)\*\*", r"\1", text)
    text = re.sub(r"(?<!`)`([^`]+)`(?!`)", r"\1", text)
    return text


def render_inline(line):
    """单行渲染：先处理 {{}} 标记，再处理 **粗体** 与 `代码`。"""
    line = render_markup(line)
    line = re.sub(r"\*\*(.+?)\*\*",
                  lambda m: paint(m.group(1), BW, BOLD), line)
    line = re.sub(r"`([^`]+)`",
                  lambda m: paint(m.group(1), BM, bg256(236)), line)
    return line


# ============ 整块输出 ============

def print_banner():
    """打印启动横幅（青→粉渐变边框）。"""
    title = "🐟 DeepSeek 联网肥鱼 H 版 v1.2.1 已启动 [DeepSeek Net Fish H Edition v1.2.1 Started]"
    sub = "输入 /help 查看全部命令 [type /help for all commands]"
    w = max(len(title), len(sub)) + 6
    line = "─" * w
    print()
    for s in ["╭" + line + "╮",
              "│  " + title.ljust(w - 4) + "│",
              "│  " + sub.ljust(w - 4) + "│",
              "╰" + line + "╯"]:
        print(gradient(s, (0, 200, 255), (255, 100, 200)))
    # ★ 个性签名：每次启动随机一句（古早 QQ 的味道）
    _sig = signature()
    if _sig:
        print(paint("  ✎ " + _sig, BK, ITAL))
    print()


def print_ai(reply):
    """按情绪着色渲染 AI 回复。

    规则（与原主程序一致）：
      - ``` 围栏行      → 青色斜体，并翻转代码块状态
      - 代码块内        → 整行绿色，空行原样输出
      - # 标题          → 去井号、黄色加粗、前置 ▎
      - - * + 列表项    → 换成 •（保留原缩进）后递归渲染行内标记
      - 其余            → render_inline
    """
    mood = detect_mood(reply)
    c1, c2 = MOOD_COLOR.get(mood, MOOD_COLOR["calm"])
    emoji = MOOD_EMOJI.get(mood, "🤖")
    head = f" {emoji} AI "
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
    # ★ 落款（2026-10-02）：收尾横线后面跟「时间 · 本轮耗时」
    mark_footer()
    print(gradient("─" * (12 + len(head)), c1, c2)
          + paint(" %s%s" % (time.strftime("%H:%M:%S"), cost_tag()), BK, DIM))
    print()


# ============ 等待动画（- \ | / 轮转）============
#   为什么需要：调用模型 / 双人核验 / 联网判断时，终端会「静止」几十秒，
#   分不清是在跑还是卡死。本类在终端原地转圈 + 计时，一眼可见。
#
#   四个设计要点：
#     1) 默认写 stderr —— stdout 被主程序的 _TeeStream 包装，且斜杠命令期间
#        会被捕获并注入给模型，动画若写进 stdout 会污染上下文；
#     2) 探测 isatty()，非 TTY（日志 / 管道 / 被捕获）时自动降级为「只报一行结果」，
#        不刷屏、不留回车符；
#     3) 后台 daemon 线程 + stop() 清行，任何异常 / 中断都不在屏幕上留残影；
#     4) 帧字符全为 ASCII 等宽（- \ | /），无编码与宽度抖动风险。
WAIT_FRAMES = "-\\|/"

# 反向帧（/ | \ -）：专门给「非只读工具执行」用 —— 与「模型思考」的正向转圈
# 方向相反，一眼就能分辨「现在是这台机器在跑」还是「模型在想」。
WAIT_FRAMES_REV = "/|\\-"


def _disp_width(s):
    """终端显示宽度：CJK 全角算 2 列，其余算 1 列。

    用于精确清行 —— 中文标签按 len() 算会少算一半，导致残影。
    """
    w = 0
    for ch in s:
        w += 2 if unicodedata.east_asian_width(ch) in ("W", "F") else 1
    return w


def _win_wait(kind, **kw):
    """把等待动画的阶段变化**同步给对话窗口**（窗口没开就静默跳过）。

    2026-10-02 新增：原来 - \\ | / 转圈只画在控制台上；现在同一条动画
    也出现在窗口「消息栏前面」的状态条里。全部包在 try 里 ——
    窗口出任何问题都不许影响控制台的转圈。
    """
    try:
        import chat_window as _cw
        if kind == "begin":
            _cw.spin_begin(kw.get("label"))
        elif kind == "label":
            _cw.spin_label(kw.get("label"))
        elif kind == "end":
            _cw.spin_end(ok=kw.get("ok", True), label=kw.get("label"),
                         elapsed=kw.get("elapsed", 0.0))
    except Exception:
        pass


class Wait:
    r"""等待动画：- \ | / 四帧轮转 + 秒表。

    用法 / Usage:
        w = Wait("等待模型响应").start()
        ...
        w.stop(ok=True, label="模型已响应")        # → ✅ 模型已响应（12.4s）

    TTY 下的实际观感（同一行原地刷新）:
        -  等待模型响应  0.1s
        \  等待模型响应  0.2s
        |  等待模型响应  0.3s
        /  等待模型响应  0.4s
        停表后该行被覆盖为:  ✅ 模型已响应（12.4s）

    延迟启动 / start_delay:
        Wait("执行命令 ...", start_delay=0.4).start()
        若在 0.4s 内就 stop()（操作本来就很快），则**一帧都不刷、也不落结果行**，
        屏幕上完全无痕 —— 专治「毫秒级操作闪一下」的视觉噪音。
        超过 0.4s 才开始刷帧，行为与不设延迟时一致。0 = 立即开始（默认）。
    """

    FRAMES = WAIT_FRAMES

    def __init__(self, label="等待", stream=None, interval=0.08, enabled=None,
                 frames=None, start_delay=0.0, detail=None, dock=None):
        self.label = label
        self.stream = stream if stream is not None else sys.stderr
        self.interval = max(0.03, float(interval))
        # detail：每帧调用一次的可调用对象，返回一段「进度心跳」短文本
        #   （例如 "↑12.3MB" / "⚠ 8m 无输出"）。用于长任务一眼看出死活。
        self.detail = detail
        self._last_len = 0      # 上一帧显示宽度（用于按需清行，适应变长内容）
        # frames：自定义帧序列（例如反向的 WAIT_FRAMES_REV）。留空则用类默认 FRAMES。
        if frames:
            self.FRAMES = frames
        # start_delay：延迟这么久仍未被 stop，才开始刷帧。用于「秒级完成」的操作
        #   完全不打扰屏幕（避免闪一下）。0 = 立即开始（原行为，默认）。
        try:
            self.start_delay = max(0.0, float(start_delay))
        except (TypeError, ValueError):
            self.start_delay = 0.0
        self._shown = False      # 是否真的刷出过帧（决定 stop 时要不要清行/落结果行）
        self._win_on = False     # 是否已把「开始转圈」告诉对话窗口
        self._win_label = None   # 上一次同步给窗口的阶段文案
        self._ev = threading.Event()
        self._th = None
        self._t0 = None
        if enabled is None:
            try:
                enabled = bool(self.stream.isatty())
            except Exception:
                enabled = False
        self.enabled = bool(enabled)
        # dock：把转圈圈钉在【控制台左下角】—— 不占正文、不打扰输入行。
        #       None = 跟随全局开关 WAIT_ANIM_DOCK；做不到会自动退回同行刷新。
        self.dock = WAIT_ANIM_DOCK if dock is None else bool(dock)
        self._docked = False

    # ---- 内部 ----
    def _emit(self, text):
        try:
            self.stream.write(text)
            self.stream.flush()
        except Exception:
            pass

    def _detail_text(self):
        """取一次进度心跳文本；回调异常一律吞掉，绝不让动画把主程序搞崩。"""
        if not self.detail:
            return ""
        try:
            return str(self.detail() or "")
        except Exception:
            return ""

    def _loop(self):
        # 延迟启动：在 start_delay 内就被 stop() → 直接返回，一帧都不刷，
        # _shown 保持 False。这是「不闪一下」的关键。
        if self.start_delay > 0 and self._ev.wait(self.start_delay):
            return
        self._shown = True
        # ★ 同步给对话窗口：开始转圈（- \ | / 与控制台同一套节奏）
        self._win_on = True
        self._win_label = self.label
        _win_wait("begin", label=self.label)
        i = 0
        n = len(self.FRAMES)
        while True:
            extra = self._detail_text()
            line = "  %s  %s%s  %.1fs " % (
                self.FRAMES[i % n], self.label,
                ("  " + extra) if extra else "", time.time() - self._t0)
            if self.label != self._win_label:      # 阶段变了 → 通知窗口
                self._win_label = self.label
                _win_wait("label", label=self.label)
            # ★ 优先钉在【左下角】（不占正文、不打扰输入行）；
            #   做不到（非 Win32 控制台 / 输出被重定向）就退回「同行原地刷新」。
            if self.dock:
                if console_status(line):
                    self._docked = True
                else:
                    self.dock = False            # 能力不足 → 永久退回
            if not self._docked:
                self._last_len = _disp_width(line)
                self._emit("\r" + line)
            i += 1
            if self._ev.wait(self.interval):
                break

    # ---- 对外 ----
    def start(self):
        """开始动画；返回 self 便于链式调用。"""
        self._t0 = time.time()
        if self.enabled:
            try:
                self._th = threading.Thread(target=self._loop, daemon=True)
                self._th.start()
            except Exception:
                self.enabled = False
        return self

    def label_to(self, text):
        """切换阶段文案（下一帧即生效）。"""
        self.label = text
        return self

    def stop(self, ok=True, label=None, note="", on_line=None):
        """停止动画、清行并落一行结果；返回耗时秒数。可安全重复调用。

        on_line：可选回调。传入时**本函数不自己输出**，而是把「结束行」文本
                 交给它，由调用方决定这句话去哪（例如送到独立窗口）。
                 传 None 时行为与原来完全一致（写 self.stream）。
                 回调抛异常会被吞掉 —— 动画的收尾绝不能把主程序带崩。
        """
        el = time.time() - (self._t0 or time.time())
        self._ev.set()
        if self._th is not None:
            try:
                self._th.join(timeout=1.0)
            except Exception:
                pass
            self._th = None
        if label:
            self.label = label
        if self._win_on:                            # ★ 通知窗口：转圈收工
            self._win_on = False
            _win_wait("end", ok=ok, label=self.label, elapsed=el)
        _line = ("  %s %s（%.1fs）%s"
                 % ("✅" if ok else "⚠️ ", self.label, el,
                    ("  " + note) if note else ""))
        # 延迟期内就结束了（一帧都没刷过）→ 完全静默：不刷帧、也不落结果行，
        # 让「快操作」在屏幕上彻底无痕。（非 TTY 的 enabled=False 不走这里，
        # 仍保持"只报一行结果"的原语义。）
        if self._docked:
            console_status("", clear=True)          # 擦掉左下角那一行
            self._docked = False
        elif self.enabled and not self._shown:
            return el
        elif self.enabled:
            # 按实际显示宽度清行（内容里可能带进度心跳，固定 72 会清不干净）
            self._emit("\r" + " " * max(72, self._last_len + 4) + "\r")
        if on_line is not None:
            try:
                on_line(_line)
            except Exception:
                pass
        else:
            self._emit(_line + "\n")
        return el


# ============ 控制台安全插话（后台任务主动播报用）============
#   需求：后台任务跑完时肥鱼要能「主动说话」，
#         **但绝不能弄丢用户正在输入的那行字**。
#   策略（保守到物理上不可能出错）：
#     只在「用户什么都没输入」时才插话；一旦打了字就闭嘴，等回车再播报。
#     因为我们从不触碰有内容的输入行，就不存在「保存/恢复失败」的风险
#     （那种做法遇到中文输入法或折行就会翻车）。
#   三重保守检查，任一不可判定即视为「不空闲」：
#     ① Windows 控制台 API 可用；
#     ② 没有待处理的输入事件（用户没在敲键）；
#     ③ 提示符之后到光标之间没有任何已输入字符。

_STD_OUT = -11
_STD_IN = -10
_k32 = None
_CONSOLE_STATE = None
_COORD = None
_CSBI = None

if sys.platform == "win32":
    try:
        import ctypes as _ct

        class _COORD(_ct.Structure):
            _fields_ = [("X", _ct.c_short), ("Y", _ct.c_short)]

        class _SMALL_RECT(_ct.Structure):
            _fields_ = [("Left", _ct.c_short), ("Top", _ct.c_short),
                        ("Right", _ct.c_short), ("Bottom", _ct.c_short)]

        class _CSBI(_ct.Structure):
            _fields_ = [("dwSize", _COORD), ("dwCursorPosition", _COORD),
                        ("wAttributes", _ct.c_ushort), ("srWindow", _SMALL_RECT),
                        ("dwMaximumWindowSize", _COORD)]
    except Exception:
        _COORD = None
        _CSBI = None


def _console_ready():
    """惰性初始化控制台 API；不可用返回 False。"""
    global _k32, _CONSOLE_STATE
    if _CONSOLE_STATE is not None:
        return _CONSOLE_STATE
    _CONSOLE_STATE = False
    try:
        if sys.platform == "win32" and _COORD is not None:
            import ctypes as _ct
            _k32 = _ct.windll.kernel32
            _CONSOLE_STATE = True
    except Exception:
        _CONSOLE_STATE = False
    return _CONSOLE_STATE


def console_cursor():
    """当前光标位置 (x, y)；不可判定返回 None。"""
    if not _console_ready():
        return None
    try:
        import ctypes as _ct
        h = _k32.GetStdHandle(_STD_OUT)
        info = _CSBI()
        if not _k32.GetConsoleScreenBufferInfo(h, _ct.byref(info)):
            return None
        return int(info.dwCursorPosition.X), int(info.dwCursorPosition.Y)
    except Exception:
        return None


def console_pending_keys():
    """待处理的输入事件数；不可判定返回 None。"""
    if not _console_ready():
        return None
    try:
        import ctypes as _ct
        h = _k32.GetStdHandle(_STD_IN)
        n = _ct.c_ulong(0)
        if not _k32.GetNumberOfConsoleInputEvents(h, _ct.byref(n)):
            return None
        return int(n.value)
    except Exception:
        return None


def console_row_text(y, x0, x1):
    """读取屏幕第 y 行 [x0, x1) 区间的文字；失败返回 None。"""
    if not _console_ready():
        return None
    if x1 <= x0:
        return ""
    try:
        import ctypes as _ct
        h = _k32.GetStdHandle(_STD_OUT)
        n = int(x1 - x0)
        buf = _ct.create_unicode_buffer(n + 1)
        read = _ct.c_ulong(0)
        if not _k32.ReadConsoleOutputCharacterW(
                h, buf, n, _COORD(int(x0), int(y)), _ct.byref(read)):
            return None
        return buf[:read.value]
    except Exception:
        return None


def console_is_idle(prompt_x=None, prompt_y=None):
    """用户此刻是否「空着提示符」——即可以安全插话而不破坏输入。

    传入提示符结束处的 (x, y) 会启用最严格的三重检查；
    不传则只做「无待处理按键」这一层检查。
    """
    if not _console_ready():
        return False
    pend = console_pending_keys()
    if pend is None or pend > 0:
        return False
    if prompt_x is None or prompt_y is None:
        return False
    cur = console_cursor()
    if cur is None:
        return False
    cx, cy = cur
    if cy != prompt_y or cx < prompt_x:
        return False            # 折行 / 位置异常 → 无法安全判定，保守拒绝
    txt = console_row_text(cy, prompt_x, cx)
    if txt is None:
        return False
    return txt.strip() == ""


def console_clear_line(width=110):
    """清空当前行并把光标移回行首（**仅在确认空闲后调用**）。"""
    try:
        sys.stdout.write("\r" + " " * max(20, int(width)) + "\r")
        sys.stdout.flush()
        return True
    except Exception:
        return False


def console_cursor_y():
    """当前光标所在行号；不可判定返回 None。"""
    cur = console_cursor()
    return None if cur is None else int(cur[1])


def console_clear_from(y_from, y_to=None):
    """抹掉屏幕上 [y_from, y_to] 这**整段**（含两端），并把光标放回 y_from 行首。

    用途：报批「采完即扫」—— 决定一旦做出，那一段就从眼前消失，
    主界面只留对话。凭证另行留档（见 FATHFISH._approval_receipt）。

    实现：Win32 直接填空格（同时复位字符属性，避免留下彩色底）；
    失败时退回 ANSI（先开 VT 处理）。判定不了就返回 False ——
    调用方应放弃抹除，而不是乱抹一通。
    """
    if not _console_ready() or y_from is None:
        return False
    try:
        import ctypes as _ct
        y_from = int(y_from)
        h = _k32.GetStdHandle(_STD_OUT)
        info = _CSBI()
        if not _k32.GetConsoleScreenBufferInfo(h, _ct.byref(info)):
            return False
        y_to = int(info.dwCursorPosition.Y) if y_to is None else int(y_to)
        if y_to < y_from:
            return False
        width = int(info.dwSize.X)
        if width <= 0:
            return False
        total = width * (y_to - y_from + 1)
        written = _ct.c_ulong(0)
        ok = _k32.FillConsoleOutputCharacterW(
            h, _ct.c_wchar(" "), total, _COORD(0, y_from), _ct.byref(written))
        if not ok:
            return False
        try:
            # 一并复位字符属性：否则清成了空格但仍带着原色底
            _k32.FillConsoleOutputAttribute(
                h, info.wAttributes, total, _COORD(0, y_from), _ct.byref(written))
        except Exception:
            pass
        return bool(_k32.SetConsoleCursorPosition(h, _COORD(0, y_from)))
    except Exception:
        return False


def console_status(text, clear=False):
    """把一行字写到【可见窗口左下角】（最后一行行首），**不移动当前光标**。

    为什么用 WriteConsoleOutputCharacterW：
      · 它直接写屏幕缓冲区，**不碰光标** → 不会打扰你正在输入的那一行；
      · 也**不触发滚动**（不像 print 带换行会把已有内容顶上去）→ 稳定钉在左下角。

    clear=True 用空格填满整行（擦掉）。
    返回 True/False；False = 这个终端做不到，调用方应回落原行为。
    """
    if not _console_ready():
        return False
    try:
        import ctypes as _ct
        h = _k32.GetStdHandle(_STD_OUT)
        info = _CSBI()
        if not _k32.GetConsoleScreenBufferInfo(h, _ct.byref(info)):
            return False
        # 可见窗口的最后一行（**不是**缓冲区最后一行 —— 缓冲区可能有好几千行）
        bottom = int(info.srWindow.Bottom)
        width = int(info.srWindow.Right) - int(info.srWindow.Left) + 1
        limit = width - 1                  # 留最后一格：写满有可能触发滚动
        if limit <= 0:
            return False
        s = "" if clear else str(text or "")
        # 按【显示宽度】裁（中文占 2 列），再补空格填满整行以覆盖旧内容
        out = ""
        for ch in s:
            if _disp_width(out + ch) > limit:
                break
            out += ch
        pad = limit - _disp_width(out)
        if pad > 0:
            out += " " * pad
        buf = _ct.create_unicode_buffer(out)
        written = _ct.c_ulong(0)
        return bool(_k32.WriteConsoleOutputCharacterW(
            h, buf, len(out), _COORD(0, bottom), _ct.byref(written)))
    except Exception:
        return False


def console_send_enter():
    """向控制台注入一个回车键事件（让阻塞中的 input() 返回空行）。

    ⚠️ 仅在**确认用户没输入任何内容**时使用；否则会把用户的半截输入提交出去。
    """
    if not _console_ready():
        return False
    try:
        import ctypes as _ct

        class _KEY_EVENT(_ct.Structure):
            _fields_ = [("bKeyDown", _ct.c_int),
                        ("wRepeatCount", _ct.c_ushort),
                        ("wVirtualKeyCode", _ct.c_ushort),
                        ("wVirtualScanCode", _ct.c_ushort),
                        ("uChar", _ct.c_wchar),
                        ("dwControlKeyState", _ct.c_ulong)]

        class _INPUT_RECORD(_ct.Structure):
            _fields_ = [("EventType", _ct.c_ushort),
                        ("_pad", _ct.c_ushort),
                        ("Event", _KEY_EVENT)]

        recs = (_INPUT_RECORD * 2)()
        for i, down in enumerate((1, 0)):
            recs[i].EventType = 1                      # KEY_EVENT
            recs[i].Event.bKeyDown = down
            recs[i].Event.wRepeatCount = 1
            recs[i].Event.wVirtualKeyCode = 0x0D       # VK_RETURN
            recs[i].Event.wVirtualScanCode = 0x1C
            recs[i].Event.uChar = "\r"
            recs[i].Event.dwControlKeyState = 0
        h = _k32.GetStdHandle(_STD_IN)
        written = _ct.c_ulong(0)
        return bool(_k32.WriteConsoleInputW(
            h, _ct.byref(recs), 2, _ct.byref(written)))
    except Exception:
        return False


# ============================================================
# 自测 / Self-test
# ============================================================

def _selftest():
    ok = True
    problems = []

    def check(name, cond, detail=""):
        nonlocal ok
        if not cond:
            ok = False
            problems.append("%s %s" % (name, detail))
        print("   %-44s %s %s" % (name, "OK" if cond else "FAIL", detail))

    print("── 颜色构造 ──")
    check("paint 结尾复位", paint("x", BG).endswith(RESET))
    check("paint 含样式码", BG in paint("x", BG))
    check("fg256(196)", fg256(196) == "\033[38;5;196m")
    check("bg256(236)", bg256(236) == "\033[48;5;236m")
    check("rgb 前景", rgb(1, 2, 3) == "\033[38;2;1;2;3m")
    check("rgb 背景", rgb(1, 2, 3, back=True) == "\033[48;2;1;2;3m")
    check("rainbow 有内容且复位", len(rainbow("abc")) > 3 and rainbow("abc").endswith(RESET))
    check("gradient 空串不炸", isinstance(gradient("", (0, 0, 0), (255, 255, 255)), str))
    check("gradient 单字不炸", gradient("a", (0, 0, 0), (255, 255, 255)).endswith(RESET))

    print("── 情绪判定 ──")
    check("error 优先级最高", detect_mood("报错 但 成功 了") == "error")
    check("code 命中", detect_mood("```python\nimport os") == "code")
    check("无关键词→calm", detect_mood("随便说点什么") == "calm")
    check("happy 命中", detect_mood("太好了，搞定") == "happy")
    # calm 为兜底项，故意不在 MOOD_ORDER 中；此处校验"优先级表是颜色表的子集，
    # 且差集恰好只有 calm"这一不变量。
    check("MOOD_ORDER ⊂ MOOD_COLOR 且差集仅 calm",
          set(MOOD_ORDER) <= set(MOOD_COLOR)
          and set(MOOD_COLOR) - set(MOOD_ORDER) == {"calm"})
    check("MOOD_EMOJI 覆盖全部情绪", set(MOOD_EMOJI) == set(MOOD_COLOR))

    print("── 标记渲染 ──")
    check("render_markup 上色", BG in render_markup("{{green}}OK{{/green}}"))
    check("未知标签原样", render_markup("{{nope}}x{{/nope}}") == "x")
    check("rainbow 标签", render_markup("{{rainbow}}ab{{/rainbow}}").endswith(RESET))
    check("strip_markup 去标记", strip_markup("{{red}}警告{{/red}}") == "警告")
    check("strip_markup 去粗体", strip_markup("**重点**") == "重点")
    check("strip_markup 去反引号", strip_markup("`code`") == "code")
    check("render_inline 端到端", BW in render_inline("**粗**"))

    print("── 整块输出（捕获 stdout，不污染终端）──")
    import io
    import contextlib
    buf = io.StringIO()
    try:
        with contextlib.redirect_stdout(buf):
            print_banner()
            print_ai("# 标题\n- 列表\n```py\nx=1\n```\n普通行")
        out = buf.getvalue()
        # gradient() 会在每两个字符之间插入 ANSI 码，故匹配文本前必须先剥离转义序列
        plain = re.sub(r"\x1b\[[0-9;]*m", "", out)
        check("print_banner 有输出", len(out) > 100)
        check("print_ai 含 AI 头", "AI" in plain)
        check("print_ai 代码块着绿", BG in out)
        check("print_ai 标题带竖线", "▎" in out)
    except Exception as e:
        check("print 系列不抛异常", False, str(e))

    print("── 等待动画 Wait ──")

    class _TTY(io.StringIO):
        def isatty(self):
            return True

    _b1 = io.StringIO()
    _t0 = time.time()
    _w1 = Wait("测试", stream=_b1, enabled=False).start()
    time.sleep(0.15)
    _el = _w1.stop()
    check("非 TTY 只输出一行", len(_b1.getvalue().splitlines()) == 1,
          repr(_b1.getvalue()))
    check("非 TTY 无回车符", "\r" not in _b1.getvalue())
    check("计时误差 <0.1s", abs(_el - 0.15) < 0.1, "%.3fs" % _el)

    _b2 = _TTY()
    _w2 = Wait("测试", stream=_b2, enabled=True).start()
    time.sleep(0.3)
    _w2.stop(ok=True, label="完成")
    _txt = _b2.getvalue()
    _fr = re.findall(r"\r  ([^\s])  ", _txt)
    check("TTY 模式持续刷新", _txt.count("\r") >= 2, "%d 次" % _txt.count("\r"))
    check("动画帧 ≥3 且均取自 -\\|/",
          len(_fr) >= 3 and all(f in Wait.FRAMES for f in _fr),
          "".join(_fr))
    check("结束行覆盖动画", "✅ 完成（" in _txt)
    check("停止后线程已回收", _w2._th is None)

    # 反向帧（非只读工具执行专用）
    _b3 = _TTY()
    _w3 = Wait("反向", stream=_b3, enabled=True, frames=WAIT_FRAMES_REV).start()
    time.sleep(0.3)
    _w3.stop()
    _fr3 = re.findall(r"\r  ([^\s])  ", _b3.getvalue())
    check("自定义反向帧生效",
          len(_fr3) >= 3 and all(f in WAIT_FRAMES_REV for f in _fr3), "".join(_fr3))
    check("反向帧 = 默认帧逆序", WAIT_FRAMES_REV == WAIT_FRAMES[::-1])

    # 延迟启动（start_delay）：秒级完成的操作完全静默，不"闪一下"
    _b4 = _TTY()
    _w4 = Wait("快操作", stream=_b4, enabled=True, start_delay=0.5).start()
    time.sleep(0.1)
    _w4.stop()
    check("延迟期内 stop 完全静默", _b4.getvalue() == "", repr(_b4.getvalue()))

    _b5 = _TTY()
    _w5 = Wait("慢操作", stream=_b5, enabled=True, start_delay=0.1,
               interval=0.05).start()
    time.sleep(0.45)
    _w5.stop(ok=True, label="完成")
    check("超过延迟后照常刷帧",
          _b5.getvalue().count("\r") >= 2 and "✅ 完成（" in _b5.getvalue(),
          "%d 次" % _b5.getvalue().count("\r"))

    _b6 = _TTY()
    _w6 = Wait("快操作", stream=_b6, enabled=True, start_delay=0.5)
    check("未 start 直接 stop 也不炸", _w6.stop() >= 0 and _b6.getvalue() == "")

    # on_line：结束行改道（送独立窗口）—— 本屏幕不应再出现该行
    _b8 = _TTY()
    _got = []
    _w8 = Wait("等待模型响应", stream=_b8, enabled=True).start()
    time.sleep(0.25)
    _w8.stop(ok=True, label="模型已响应", on_line=_got.append)
    check("on_line 收到结束行",
          len(_got) == 1 and "模型已响应" in _got[0], repr(_got[:1]))
    check("on_line 时本屏幕不再输出该行",
          "模型已响应" not in _b8.getvalue(), repr(_b8.getvalue()[:50]))

    def _boom(_line):                     # 回调抛异常不得影响收尾
        raise RuntimeError("故意")
    _b9 = _TTY()
    _w9 = Wait("x", stream=_b9, enabled=True).start()
    time.sleep(0.15)
    _ok9 = True
    try:
        _w9.stop(on_line=_boom)
    except Exception:
        _ok9 = False
    check("on_line 回调异常被吞掉", _ok9)

    # 进度心跳（detail 回调）
    _calls = {"n": 0}

    def _detail():
        _calls["n"] += 1
        if _calls["n"] == 2:
            raise RuntimeError("回调故意报错")     # 回调异常必须被吞掉
        return "↑1.%dMB" % _calls["n"]

    _b7 = _TTY()
    _w7 = Wait("长任务", stream=_b7, enabled=True, interval=0.05,
               detail=_detail).start()
    time.sleep(0.4)
    _w7.stop(ok=True, label="完成")
    _o7 = _b7.getvalue()
    check("心跳文本出现在动画里", "↑1." in _o7, _o7[:60])
    check("心跳回调异常被吞掉", "✅ 完成（" in _o7)

    # 显示宽度：CJK 按 2 列算
    check("_disp_width 中文按2列", _disp_width("中文") == 4 and _disp_width("ab") == 2)

    # 控制台工具：不抛异常、返回类型正确
    check("console_cursor 不抛", console_cursor() is None or isinstance(console_cursor(), tuple))
    check("console_pending_keys 不抛",
          console_pending_keys() is None or isinstance(console_pending_keys(), int))
    check("console_is_idle 无位置参数返回 False", console_is_idle() is False)
    check("console_is_idle 脏位置返回 False", console_is_idle(-5, -5) is False)
    check("console_clear_line 不抛", console_clear_line() is True)
    check("console_cursor_y 类型正确",
          console_cursor_y() is None or isinstance(console_cursor_y(), int))
    # 抹除：判定不了（无控制台 / 区间非法）必须返回 False，绝不乱抹
    check("抹除 y_from=None 返回 False", console_clear_from(None) is False)
    check("抹除倒序区间返回 False", console_clear_from(10, 5) is False)
    check("抹除函数不抛异常", console_clear_from(0, 0) in (True, False))
    _same = Wait("x", stream=io.StringIO(), enabled=False)
    check("重复 stop 不抛异常", _same.stop() >= 0 and _same.stop() >= 0)

    print("\n%s" % ("ui_core 自测全部通过 ✅" if ok else "存在失败 ❌：%s" % problems))
    return 0 if ok else 1


if __name__ == "__main__":
    import sys
    sys.exit(_selftest())
