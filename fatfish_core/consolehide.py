# -*- coding: utf-8 -*-
"""consolehide.py —— 控制台窗口的隐藏 / 恢复 / 查询（GUI-First 收尾件）

为什么需要它 / Why
------------------
GUI-First 路线的最后一步是「**只弹一个窗口**」：启动器一点，出现的应该
只有对话窗口（GUI），不该再有一堆黑乎乎的 runtime / 监控器 / 状态台窗口。

但**不能**干脆用 pythonw.exe 起（真·无控制台），因为：

    · ``sys.stdout`` 会变成 ``None`` —— 程序里满地的 ``print()`` 直接崩；
    · ``stdin`` 同时失效 —— 主循环里「stdin 关闭 → 退出」那条路会被误触发，
      表现为「刚启动就自己退了」。

所以这里采取的做法是：

    **进程照旧挂在控制台上，只把那个控制台窗口藏起来。**（ShowWindow）

这时 ``stdout`` / ``stderr`` / ``stdin`` / ``input()`` 全部照常工作 ——
对主程序**零副作用**，纯粹是「窗口看不见了」，随时还能请回来（排障用）。

安全边界 / Safety
-----------------
绝不误伤别人的窗口：

1. 只认**本进程自己的**控制台宿主窗口 —— 窗口类名必须是 ``ConsoleWindowClass``
   （传统 conhost 宿主，就是用户看到的那个黑窗）。
2. **ConPTY 环境一律不动手**（Windows Terminal / VS Code 终端等）：
   那种情况下 ``GetConsoleWindow()`` 给回的是 ``PseudoConsoleWindow`` ——
   它是 ConPTY 的**内部伪窗口**，不是用户看到的那个终端窗口；隐藏它
   什么也改变不了，却会假报成功。**宁可留个窗口，也绝不误伤。**
3. 非 Windows / 没有控制台 / 任何异常 → 全部返回 False，静默降级。

对外接口 / API
--------------
    hwnd()         → 本进程控制台窗口句柄（拿不到为 0）
    available()    → 能不能安全地操作这个窗口
    is_visible()   → 当前是否可见
    hide()         → 藏起来
    show()         → 请回来（并置前）
    describe()     → 一行中文状态，供 ``/console status`` 用
"""

import sys

IS_WIN = (sys.platform == "win32")

# ShowWindow 的 nCmdShow 常量
_SW_HIDE = 0
_SW_SHOW = 5

# ★ 只在类名**明确认得**时才动手（见模块文档的安全边界）
#   · ConsoleWindowClass —— 传统 conhost 宿主窗口，就是用户看到的那个黑窗。
#     隐藏它 = 真的把界面收干净。**这是唯一会被动手的类。**
#   · PseudoConsoleWindow —— ConPTY 的**伪控制台**窗口（Windows Terminal /
#     VS Code 终端等）。★ 它**不是**用户看到的那个终端窗口：用户看到的是
#     终端宿主进程（WindowsTerminal.exe 等）的窗口。隐藏伪窗口什么也改变不了，
#     却会让 hide() 假报"成功" —— 所以**不进白名单**，一律不动手。
_CONSOLE_CLASSES = ("ConsoleWindowClass",)
_PSEUDO_CLASSES = ("PseudoConsoleWindow",)

_ctypes = None
_k32 = None
_u32 = None
try:
    if IS_WIN:
        import ctypes as _ctypes
        _k32 = _ctypes.windll.kernel32
        _u32 = _ctypes.windll.user32
except Exception:                              # pragma: no cover
    _ctypes = None
    _k32 = None
    _u32 = None


def hwnd():
    """本进程控制台窗口句柄；无控制台 / 非 Windows / 出错 → 0。"""
    if _k32 is None:
        return 0
    try:
        return int(_k32.GetConsoleWindow() or 0)
    except Exception:
        return 0


def window_class(h=None):
    """窗口类名（判据用）。"""
    if _u32 is None:
        return ""
    h = h or hwnd()
    if not h:
        return ""
    try:
        buf = _ctypes.create_unicode_buffer(256)
        _u32.GetClassNameW(h, buf, 256)
        return buf.value or ""
    except Exception:
        return ""


def available():
    """能不能**安全地**操作本进程的控制台窗口。

    三个条件缺一不可：Windows + 拿得到句柄 + 类名认得出来。
    任何一条不满足 → False（调用方应当直接放弃隐藏）。
    """
    h = hwnd()
    if not h:
        return False
    return window_class(h) in _CONSOLE_CLASSES


def is_visible():
    """控制台窗口当前是否可见（无法判定时返回 False）。"""
    if _u32 is None:
        return False
    h = hwnd()
    if not h:
        return False
    try:
        return bool(_u32.IsWindowVisible(h))
    except Exception:
        return False


def hide():
    """把控制台窗口藏起来。成功返回 True。

    ★ 只是「窗口不见」—— 控制台对象、stdin/stdout 全都还在，
      主程序感觉不到任何差别。
    """
    if _u32 is None or not available():
        return False
    try:
        _u32.ShowWindow(hwnd(), _SW_HIDE)
        return True
    except Exception:
        return False


def show():
    """把控制台窗口请回来，并置前。成功返回 True。"""
    if _u32 is None or not available():
        return False
    try:
        h = hwnd()
        _u32.ShowWindow(h, _SW_SHOW)
        try:
            _u32.SetForegroundWindow(h)
        except Exception:
            pass
        return True
    except Exception:
        return False


def describe():
    """一行中文状态，给 ``/console status`` 用。"""
    if not IS_WIN:
        return "非 Windows 平台，无控制台窗口可操作"
    h = hwnd()
    if not h:
        return "拿不到控制台窗口句柄（可能本就没有控制台）"
    cls = window_class(h)
    if cls in _PSEUDO_CLASSES:
        return ("控制台是 ConPTY 伪窗口（类名 %s）—— 你看到的那个终端窗口属于"
                "终端宿主进程（Windows Terminal 等），不是这个窗口；"
                "隐藏它没有意义，所以不去动它" % cls)
    if cls not in _CONSOLE_CLASSES:
        return ("控制台窗口类名是 %r，认不出是本进程自己的控制台 → 不动手"
                % cls)
    return ("可操作 ｜ 句柄 0x%X ｜ 类名 %s ｜ 当前%s"
            % (h, cls, "可见" if is_visible() else "已隐藏"))


if __name__ == "__main__":                      # 手动排障：python consolehide.py
    print("IS_WIN       =", IS_WIN)
    print("hwnd         =", hex(hwnd()))
    print("window_class =", repr(window_class()))
    print("available    =", available())
    print("is_visible   =", is_visible())
    print("describe     =", describe())
