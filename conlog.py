# -*- coding: utf-8 -*-
"""conlog.py —— 把「真实控制台窗口」嵌进 Tk 面板（方案1）。

原理（四步）：
  1. 用 CREATE_NEW_CONSOLE 起一个宿主进程（loghost.py）→ 它有自己的控制台窗口；
  2. 用窗口类名 ConsoleWindowClass + 唯一标题，FindWindowW 找到那个 HWND；
  3. SetParent 成 Tk frame 的子窗口，去掉标题栏/边框，MoveWindow 铺满；
  4. 日志文本写进宿主的 stdin → 宿主原样写进控制台。

为什么不直接用 Tk Text：Text 是富文本控件，自带排版引擎 —— wrap="char" 时
每个字符都要参与折行计算，see()/yview() 会强制整块重排，长行 + 高频追加时
单次插入就得十几毫秒，界面直接冻结。真控制台是固定字符网格，没有这一步。

安全边界：只认 ConsoleWindowClass；只动自己起的那个宿主窗口（唯一标题匹配）；
任何一步失败都返回 False，调用方照常可以退回原来的 Tk Text 渲染。
"""
import os
import sys
import time
import ctypes
import subprocess

IS_WIN = (sys.platform == "win32")

HWND = ctypes.c_void_p
LONG_PTR = ctypes.c_ssize_t

GWL_STYLE = -16

WS_CHILD = 0x40000000
WS_POPUP = 0x80000000
WS_CAPTION = 0x00C00000
WS_THICKFRAME = 0x00040000
WS_BORDER = 0x00800000
WS_DLGFRAME = 0x00400000
WS_SYSMENU = 0x00080000
WS_MINIMIZEBOX = 0x00020000
WS_MAXIMIZEBOX = 0x00010000

SW_HIDE = 0
SW_SHOW = 5

CREATE_NEW_CONSOLE = 0x00000010
STARTF_USESHOWWINDOW = 0x00000001

CONSOLE_CLASS = "ConsoleWindowClass"
MARK = b"\x1cDUMP\x1c"          # 控制通道前缀（与 loghost.py 保持一致）

user32 = kernel32 = None
_GetWindowLongPtrW = _SetWindowLongPtrW = None

if IS_WIN:
    from ctypes import wintypes
    user32 = ctypes.WinDLL("user32", use_last_error=True)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

    user32.FindWindowW.argtypes = [wintypes.LPCWSTR, wintypes.LPCWSTR]
    user32.FindWindowW.restype = HWND
    user32.SetParent.argtypes = [HWND, HWND]
    user32.SetParent.restype = HWND
    user32.MoveWindow.argtypes = [HWND, ctypes.c_int, ctypes.c_int,
                                  ctypes.c_int, ctypes.c_int, ctypes.c_int]
    user32.MoveWindow.restype = ctypes.c_int
    user32.ShowWindow.argtypes = [HWND, ctypes.c_int]
    user32.ShowWindow.restype = ctypes.c_int
    user32.IsWindow.argtypes = [HWND]
    user32.IsWindow.restype = ctypes.c_int
    user32.GetWindowRect.argtypes = [HWND, ctypes.POINTER(wintypes.RECT)]
    user32.GetWindowRect.restype = ctypes.c_int
    user32.GetClientRect.argtypes = [HWND, ctypes.POINTER(wintypes.RECT)]
    user32.GetClientRect.restype = ctypes.c_int

    try:
        _GetWindowLongPtrW = user32.GetWindowLongPtrW
        _SetWindowLongPtrW = user32.SetWindowLongPtrW
    except AttributeError:                       # pragma: no cover
        _GetWindowLongPtrW = user32.GetWindowLongW
        _SetWindowLongPtrW = user32.SetWindowLongW
    _GetWindowLongPtrW.argtypes = [HWND, ctypes.c_int]
    _GetWindowLongPtrW.restype = LONG_PTR
    _SetWindowLongPtrW.argtypes = [HWND, ctypes.c_int, LONG_PTR]
    _SetWindowLongPtrW.restype = LONG_PTR


def is_window(hwnd):
    """句柄是否还是个有效窗口。"""
    if not user32 or not hwnd:
        return False
    try:
        return bool(user32.IsWindow(hwnd))
    except Exception:
        return False


def win_rect(hwnd):
    """窗口矩形 (x, y, w, h)（屏幕像素）。"""
    from ctypes import wintypes
    r = wintypes.RECT()
    try:
        user32.GetWindowRect(hwnd, ctypes.byref(r))
        return (r.left, r.top, r.right - r.left, r.bottom - r.top)
    except Exception:
        return None


def client_rect(hwnd):
    """客户区尺寸 (w, h)（屏幕像素）。"""
    from ctypes import wintypes
    r = wintypes.RECT()
    try:
        user32.GetClientRect(hwnd, ctypes.byref(r))
        return (r.right - r.left, r.bottom - r.top)
    except Exception:
        return None


class ConsoleLog(object):
    """一个「真控制台」日志面板后端。

    典型用法：
        cl = ConsoleLog(host_script)
        cl.start()
        cl.attach(frame_widget)          # 嵌进 Tk frame
        cl.write("...\\n")
        cl.fit(frame_widget)             # 容器尺寸变了
        cl.detach()                      # 控件即将被销毁（必须先 detach）
        cl.close()                       # 收尾，终止宿主
    """

    def __init__(self, host_script, title=None, python=None, timeout=8.0):
        self.host_script = host_script
        self.title = title or ("FATFISH_LOGHOST_%d" % os.getpid())
        self.python = python or sys.executable
        self.timeout = timeout
        self.proc = None
        self.hwnd = None
        self.parent_hwnd = None
        self.attached = False

    # ---------------------------------------------------------------- 启停

    def start(self):
        """起宿主进程并找到它的控制台窗口。成功返回 True。"""
        if not IS_WIN:
            return False
        env = dict(os.environ)
        env["FATFISH_LOGHOST_TITLE"] = self.title
        si = subprocess.STARTUPINFO()
        si.dwFlags |= STARTF_USESHOWWINDOW
        si.wShowWindow = SW_HIDE                   # 先别闪出来
        try:
            self.proc = subprocess.Popen(
                [self.python, self.host_script],
                stdin=subprocess.PIPE,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                creationflags=CREATE_NEW_CONSOLE,
                startupinfo=si,
                env=env,
                close_fds=False)
        except Exception:
            self.proc = None
            return False
        deadline = time.time() + self.timeout
        while time.time() < deadline:
            h = user32.FindWindowW(CONSOLE_CLASS, self.title)
            if h:
                self.hwnd = h
                return True
            if self.proc.poll() is not None:
                return False
            time.sleep(0.04)
        return False

    def alive(self):
        return bool(self.proc is not None and self.proc.poll() is None
                    and is_window(self.hwnd))

    # ---------------------------------------------------------------- 嵌入

    def attach(self, frame):
        """把控制台窗口 SetParent 到 Tk frame 下并铺满。"""
        if not IS_WIN or not is_window(self.hwnd):
            return False
        try:
            frame.update_idletasks()
            ph = frame.winfo_id()
            self.parent_hwnd = ph
            style = _GetWindowLongPtrW(self.hwnd, GWL_STYLE)
            style = (style & ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_BORDER |
                               WS_DLGFRAME | WS_SYSMENU | WS_MINIMIZEBOX |
                               WS_MAXIMIZEBOX)) | WS_CHILD
            _SetWindowLongPtrW(self.hwnd, GWL_STYLE, style)
            user32.SetParent(self.hwnd, ph)
            self.fit(frame)
            user32.ShowWindow(self.hwnd, SW_SHOW)
            self.attached = True
            return True
        except Exception:
            return False

    def fit(self, frame):
        """让控制台填满 frame 的当前像素尺寸。"""
        if not is_window(self.hwnd):
            return False
        try:
            w = max(1, frame.winfo_width())
            h = max(1, frame.winfo_height())
            user32.MoveWindow(self.hwnd, 0, 0, w, h, 1)
            return True
        except Exception:
            return False

    def detach(self):
        """脱离父窗口并隐藏 —— 必须在 frame 被 destroy **之前**调用，
        否则父窗口销毁会连带销毁子窗口（控制台整个没了）。"""
        if not is_window(self.hwnd):
            self.attached = False
            return False
        try:
            user32.ShowWindow(self.hwnd, SW_HIDE)
            user32.SetParent(self.hwnd, None)
            self.attached = False
            return True
        except Exception:
            return False

    # ---------------------------------------------------------------- 数据

    def write(self, text):
        """写一段文本（UTF-8 交给宿主）。"""
        if not text or self.proc is None:
            return False
        if self.proc.poll() is not None:
            return False
        try:
            self.proc.stdin.write(text.encode("utf-8"))
            self.proc.stdin.flush()
            return True
        except Exception:
            return False

    def dump(self, path, timeout=3.0):
        """让宿主把控制台**可见窗口**内容写进 path（自证 / 排障用）。"""
        if self.proc is None or self.proc.poll() is not None:
            return False
        try:
            if os.path.exists(path):
                os.remove(path)
        except Exception:
            pass
        if not self.write(MARK.decode("latin-1") + path + "\n"):
            return False
        deadline = time.time() + timeout
        while time.time() < deadline:
            if os.path.exists(path):
                time.sleep(0.05)
                return True
            time.sleep(0.05)
        return False

    def close(self):
        """收尾：脱离 + 终止宿主。"""
        try:
            self.detach()
        except Exception:
            pass
        if self.proc is not None:
            try:
                if self.proc.stdin:
                    self.proc.stdin.close()
            except Exception:
                pass
            try:
                if self.proc.poll() is None:
                    self.proc.terminate()
            except Exception:
                pass
