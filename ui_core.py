
import sys
import time
import io
import re
import threading

__all__ = [
    "RESET", "K", "R", "G", "Y", "BL", "M", "CY", "W",
    "BK", "BR", "BG", "BY", "BB", "BM", "BC", "BW",
    "BOLD", "DIM", "ITAL", "UND", "RV", "ST",
    "BGR", "BGG", "BGY", "BGB",
    "fg256", "bg256", "rgb", "paint", "rainbow", "gradient",
    "MOOD_COLOR", "MOOD_EMOJI", "MOOD_KEYWORDS", "MOOD_ORDER", "detect_mood",
    "TAG_COLOR", "TAG_RE", "render_markup", "strip_markup", "render_inline",
    "print_banner", "print_ai",
    "WAIT_FRAMES", "Wait",
]

RESET = "\033[0m"
K   = "\033[30m"; R   = "\033[31m"; G   = "\033[32m"; Y   = "\033[33m"
BL  = "\033[34m"; M   = "\033[35m"; CY  = "\033[36m"; W   = "\033[37m"
BK  = "\033[90m"; BR  = "\033[91m"; BG  = "\033[92m"; BY  = "\033[93m"
BB  = "\033[94m"; BM  = "\033[95m"; BC  = "\033[96m"; BW  = "\033[97m"
BOLD = "\033[1m"; DIM = "\033[2m"; ITAL = "\033[3m"
UND  = "\033[4m"; RV  = "\033[7m"; ST  = "\033[9m"
BGR  = "\033[41m"; BGG = "\033[42m"; BGY = "\033[43m"; BGB = "\033[44m"

def fg256(n):
    return f"\033[38;5;{n}m"

def bg256(n):
    return f"\033[48;5;{n}m"

def rgb(r, g, b, back=False):
    return f"\033[{'48' if back else '38'};2;{r};{g};{b}m"

def paint(text, *styles):
    return "".join(styles) + str(text) + RESET

def rainbow(text):
    pal = [196, 202, 208, 214, 220, 226, 190, 154, 118, 82,
           46, 47, 51, 45, 39, 33, 27, 57, 93, 129, 165, 201]
    return "".join(fg256(pal[i % len(pal)]) + ch
                   for i, ch in enumerate(text)) + RESET

def gradient(text, c1, c2):
    n = max(len(text) - 1, 1)
    out = []
    for i, ch in enumerate(text):
        t = i / n
        r = int(c1[0] + (c2[0] - c1[0]) * t)
        g = int(c1[1] + (c2[1] - c1[1]) * t)
        b = int(c1[2] + (c2[2] - c1[2]) * t)
        out.append(rgb(r, g, b) + ch)
    return "".join(out) + RESET

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
    "error":   ["error", "Error", "ERROR", "failed", "Failed", "FAILED",
                "exception", "Exception", "Traceback", "cannot", "unable"],
    "sad":     ["sorry", "unfortunately", "sadly", "regret"],
    "happy":   ["great", "success", "Success", "congrats", "congratulations",
                "done", "complete", "completed", "👍", "🎉", "haha", "nice"],
    "excited": ["wow", "amazing", "awesome", "incredible", "!!"],
    "love":    ["love", "like it", "❤", "😊"],
    "think":   ["let me think", "let's analyze", "consider", "first,", "reasoning"],
    "code":    ["```", "def ", "class ", "import ", "function "],
}
MOOD_ORDER = ["error", "code", "think", "sad", "happy", "love", "excited"]

def detect_mood(text):
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

TAG_COLOR = {
    "red": BR, "green": BG, "yellow": BY, "blue": BB,
    "cyan": BC, "magenta": BM, "white": BW, "gray": BK, "grey": BK,
    "bold": BOLD, "italic": ITAL, "underline": UND,
    "dim": DIM, "strike": ST,
}
TAG_RE = re.compile(r"\{\{(\w+)\}\}(.*?)\{\{/\1\}\}", re.DOTALL)

def _tag(tag, content):
    t = tag.lower()
    if t == "rainbow":
        return rainbow(content)
    return paint(content, TAG_COLOR[t]) if t in TAG_COLOR else content

def render_markup(text):
    return TAG_RE.sub(lambda m: _tag(m.group(1), m.group(2)), text)

def strip_markup(text):
    text = TAG_RE.sub(lambda m: m.group(2), text)
    text = re.sub(r"\*\*(.+?)\*\*", r"\1", text)
    text = re.sub(r"(?<!`)`([^`]+)`(?!`)", r"\1", text)
    return text

def render_inline(line):
    line = render_markup(line)
    line = re.sub(r"\*\*(.+?)\*\*",
                  lambda m: paint(m.group(1), BW, BOLD), line)
    line = re.sub(r"`([^`]+)`",
                  lambda m: paint(m.group(1), BM, bg256(236)), line)
    return line

def print_banner():
    title = "🐟 DeepSeek FatFish EN Edition v1.1.1 started"
    sub = "type /help for all commands"
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

WAIT_FRAMES = "-\\|/"

class Wait:

    FRAMES = WAIT_FRAMES

    def __init__(self, label="waiting", stream=None, interval=0.08, enabled=None):
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

    def start(self):
        self._t0 = time.time()
        if self.enabled:
            try:
                self._th = threading.Thread(target=self._loop, daemon=True)
                self._th.start()
            except Exception:
                self.enabled = False
        return self

    def label_to(self, text):
        self.label = text
        return self

    def stop(self, ok=True, label=None, note=""):
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
        self._emit("  %s %s (%.1fs)%s\n"
                   % ("OK" if ok else "!", self.label, el,
                      ("  " + note) if note else ""))
        return el

def _selftest():
    ok = True
    problems = []

    def check(name, cond, detail=""):
        nonlocal ok
        if not cond:
            ok = False
            problems.append("%s %s" % (name, detail))
        print("   %-44s %s %s" % (name, "OK" if cond else "FAIL", detail))

    print("-- color builders --")
    check("paint ends with RESET", paint("x", BG).endswith(RESET))
    check("paint carries style code", BG in paint("x", BG))
    check("fg256(196)", fg256(196) == "\033[38;5;196m")
    check("bg256(236)", bg256(236) == "\033[48;5;236m")
    check("rgb foreground", rgb(1, 2, 3) == "\033[38;2;1;2;3m")
    check("rgb background", rgb(1, 2, 3, back=True) == "\033[48;2;1;2;3m")
    check("rainbow has content and resets", len(rainbow("abc")) > 3 and rainbow("abc").endswith(RESET))
    check("gradient empty string is safe", isinstance(gradient("", (0, 0, 0), (255, 255, 255)), str))
    check("gradient single char is safe", gradient("a", (0, 0, 0), (255, 255, 255)).endswith(RESET))

    print("-- mood detection --")
    check("error has top priority", detect_mood("error but complete") == "error")
    check("code hits", detect_mood("```python\nimport os") == "code")
    check("no keywords -> calm", detect_mood("just saying something") == "calm")
    check("happy hits", detect_mood("great, done") == "happy")
    check("MOOD_ORDER subset of MOOD_COLOR, diff is calm only",
          set(MOOD_ORDER) <= set(MOOD_COLOR)
          and set(MOOD_COLOR) - set(MOOD_ORDER) == {"calm"})
    check("MOOD_EMOJI covers every mood", set(MOOD_EMOJI) == set(MOOD_COLOR))

    print("-- markup rendering --")
    check("render_markup colors", BG in render_markup("{{green}}OK{{/green}}"))
    check("unknown tag passes through", render_markup("{{nope}}x{{/nope}}") == "x")
    check("rainbow tag", render_markup("{{rainbow}}ab{{/rainbow}}").endswith(RESET))
    check("strip_markup removes markup", strip_markup("{{red}}warning{{/red}}") == "warning")
    check("strip_markup removes bold", strip_markup("**focus**") == "focus")
    check("strip_markup removes backticks", strip_markup("`code`") == "code")
    check("render_inline end to end", BW in render_inline("**bold**"))

    print("-- block output (stdout captured, terminal untouched) --")
    import io
    import contextlib
    buf = io.StringIO()
    try:
        with contextlib.redirect_stdout(buf):
            print_banner()
            print_ai("# heading\n- item\n```py\nx=1\n```\nplain line")
        out = buf.getvalue()
        plain = re.sub(r"\x1b\[[0-9;]*m", "", out)
        check("print_banner has output", len(out) > 100)
        check("print_ai contains the AI header", "AI" in plain)
        check("print_ai colors code blocks green", BG in out)
        check("print_ai heading carries the bar", "▎" in out)
    except Exception as e:
        check("print helpers never raise", False, str(e))

    print("-- Wait spinner --")

    class _TTY(io.StringIO):
        def isatty(self):
            return True

    _b1 = io.StringIO()
    _t0 = time.time()
    _w1 = Wait("test", stream=_b1, enabled=False).start()
    time.sleep(0.15)
    _el = _w1.stop()
    check("non-TTY emits a single line", len(_b1.getvalue().splitlines()) == 1,
          repr(_b1.getvalue()))
    check("non-TTY has no carriage return", "\r" not in _b1.getvalue())
    check("timing error <0.1s", abs(_el - 0.15) < 0.1, "%.3fs" % _el)

    _b2 = _TTY()
    _w2 = Wait("test", stream=_b2, enabled=True).start()
    time.sleep(0.3)
    _w2.stop(ok=True, label="done")
    _txt = _b2.getvalue()
    _fr = re.findall(r"\r  ([^\s])  ", _txt)
    check("TTY mode keeps refreshing", _txt.count("\r") >= 2, "%d times" % _txt.count("\r"))
    check("at least 3 frames, all from -\\|/",
          len(_fr) >= 3 and all(f in Wait.FRAMES for f in _fr),
          "".join(_fr))
    check("final line overwrites the spinner", "with" in _txt or "done (" in _txt)
    check("thread reclaimed after stop", _w2._th is None)
    _same = Wait("x", stream=io.StringIO(), enabled=False)
    check("repeated stop never raises", _same.stop() >= 0 and _same.stop() >= 0)

    print("\n%s" % ("ui_core self-test all passed" if ok else "FAILURES present: %s" % problems))
    return 0 if ok else 1

if __name__ == "__main__":
    import sys
    sys.exit(_selftest())
