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
    "WAIT_FRAMES", "Wait",
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
    title = "🐟 DeepSeek 联网肥鱼 H 版 v1.1.1 已启动 [DeepSeek Net Fish H Edition v1.1.1 Started]"
    sub = "输入 /help 查看全部命令 [type /help for all commands]"
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
    print(gradient("─" * (12 + len(head)), c1, c2))
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
    """

    FRAMES = WAIT_FRAMES

    def __init__(self, label="等待", stream=None, interval=0.08, enabled=None):
        self.label = label
        self.stream = stream if stream is not None else sys.stderr
        self.interval = max(0.03, float(interval))
        self._ev = threading.Event()
        self._th = None
        self._t0 = None
        if enabled is None:
            try:
                enabled = bool(self.stream.isatty())
            except Exception:
                enabled = False
        self.enabled = bool(enabled)

    # ---- 内部 ----
    def _emit(self, text):
        try:
            self.stream.write(text)
            self.stream.flush()
        except Exception:
            pass

    def _loop(self):
        i = 0
        n = len(self.FRAMES)
        while True:
            self._emit("\r  %s  %s  %.1fs "
                       % (self.FRAMES[i % n], self.label, time.time() - self._t0))
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

    def stop(self, ok=True, label=None, note=""):
        """停止动画、清行并落一行结果；返回耗时秒数。可安全重复调用。"""
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
        if self.enabled:
            self._emit("\r" + " " * 72 + "\r")
        self._emit("  %s %s（%.1fs）%s\n"
                   % ("✅" if ok else "⚠️ ", self.label, el,
                      ("  " + note) if note else ""))
        return el


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
    _same = Wait("x", stream=io.StringIO(), enabled=False)
    check("重复 stop 不抛异常", _same.stop() >= 0 and _same.stop() >= 0)

    print("\n%s" % ("ui_core 自测全部通过 ✅" if ok else "存在失败 ❌：%s" % problems))
    return 0 if ok else 1


if __name__ == "__main__":
    import sys
    sys.exit(_selftest())
