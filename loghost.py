# -*- coding: utf-8 -*-
"""loghost.py —— 日志宿主进程：一个拥有「真实控制台窗口」的小程序。

父进程（肥鱼 GUI）通过 stdin 管道把 UTF-8 字节喂进来，这里原样写进自己的
控制台。于是日志页 = 一个货真价实的控制台 —— 与 bat 共用同一套渲染引擎：
写入只更新字符格子、滚动只挪视口，不存在 Tk Text 那种「单次插入触发整块重排」。

三个关键设计：
  1. 用 CONOUT$ 直接打开控制台输出 —— 不依赖 std 句柄
     （父进程把 stdout 重定向了也不影响）。
     ★ 必须以 GENERIC_READ|GENERIC_WRITE 打开：读窗口尺寸 / 读屏幕字符
       都要求**读权限**，只写句柄会让这些 API 静默失败。
  2. WriteConsoleW + ENABLE_VIRTUAL_TERMINAL_PROCESSING → ANSI 色码照常上色。
  3. 后台线程「缓冲区宽度对齐窗口宽度」→ 折行发生在可见边缘，与 bat 一致
     （控制台默认在缓冲区宽度处折行，缓冲区比窗口宽就会变成横向滚动）。

另有一条极简控制通道（自证 / 自检用）：
    输入以 MARK 开头 → 表示这是命令，不是日志内容。
    目前只有一条：MARK + <文件路径> + \n → 把当前**可见窗口**的字幕 dump 到该文件；
    出错则把 traceback 写到 <路径>.err。

由父进程用 CREATE_NEW_CONSOLE 启动，标题由环境变量 FATFISH_LOGHOST_TITLE 指定。
"""
import os
import sys
import codecs
import traceback
import ctypes
import threading
from ctypes import byref, c_void_p, c_wchar_p, POINTER

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

GENERIC_READ = 0x80000000
GENERIC_WRITE = 0x40000000
FILE_SHARE_READ = 0x00000001
FILE_SHARE_WRITE = 0x00000002
OPEN_EXISTING = 3
ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004
STD_OUTPUT_HANDLE = -11

BUF_MAX_H = 9999          # 缓冲区高度上限（回看空间）
BUF_KEEP_H = 400          # 窗口上方至少保留多少行历史

MARK = b"\x1cDUMP\x1c"    # 控制通道前缀（文件分隔符，正常日志里几乎不可能出现）


class COORD(ctypes.Structure):
    _fields_ = [("X", ctypes.c_short), ("Y", ctypes.c_short)]


class SMALL_RECT(ctypes.Structure):
    _fields_ = [("Left", ctypes.c_short), ("Top", ctypes.c_short),
                ("Right", ctypes.c_short), ("Bottom", ctypes.c_short)]


class CONSOLE_SCREEN_BUFFER_INFO(ctypes.Structure):
    _fields_ = [("dwSize", COORD),
                ("dwCursorPosition", COORD),
                ("wAttributes", ctypes.c_ushort),
                ("srWindow", SMALL_RECT),
                ("dwMaximumWindowSize", COORD)]


kernel32.CreateFileW.restype = c_void_p
kernel32.CreateFileW.argtypes = [c_wchar_p, ctypes.c_uint32, ctypes.c_uint32,
                                 c_void_p, ctypes.c_uint32, ctypes.c_uint32, c_void_p]
kernel32.WriteConsoleW.restype = ctypes.c_int
kernel32.WriteConsoleW.argtypes = [c_void_p, c_wchar_p, ctypes.c_uint32,
                                   POINTER(ctypes.c_uint32), c_void_p]
kernel32.ReadConsoleOutputCharacterW.restype = ctypes.c_int
kernel32.ReadConsoleOutputCharacterW.argtypes = [c_void_p, c_wchar_p, ctypes.c_uint32,
                                                 COORD, POINTER(ctypes.c_uint32)]
kernel32.GetConsoleMode.argtypes = [c_void_p, POINTER(ctypes.c_uint32)]
kernel32.GetConsoleMode.restype = ctypes.c_int
kernel32.SetConsoleMode.argtypes = [c_void_p, ctypes.c_uint32]
kernel32.SetConsoleMode.restype = ctypes.c_int
kernel32.SetConsoleTitleW.argtypes = [c_wchar_p]
kernel32.SetConsoleTitleW.restype = ctypes.c_int
kernel32.SetConsoleOutputCP.argtypes = [ctypes.c_uint32]
kernel32.GetConsoleScreenBufferInfo.argtypes = [c_void_p,
                                                POINTER(CONSOLE_SCREEN_BUFFER_INFO)]
kernel32.GetConsoleScreenBufferInfo.restype = ctypes.c_int
kernel32.SetConsoleScreenBufferSize.argtypes = [c_void_p, COORD]
kernel32.SetConsoleScreenBufferSize.restype = ctypes.c_int
kernel32.GetStdHandle.argtypes = [ctypes.c_uint32]
kernel32.GetStdHandle.restype = c_void_p


def _open_conout():
    """打开本进程的控制台屏幕缓冲区（★ 读写权限都要）。"""
    h = kernel32.CreateFileW("CONOUT$", GENERIC_READ | GENERIC_WRITE,
                             FILE_SHARE_READ | FILE_SHARE_WRITE,
                             None, OPEN_EXISTING, 0, None)
    if not h or h == ctypes.c_void_p(-1).value:
        h = kernel32.GetStdHandle(STD_OUTPUT_HANDLE & 0xFFFFFFFF)
    return h


def _write_console(h, text):
    """写一段文本到控制台（UTF-16 长度按 code unit 算，emoji 才不会截断）。"""
    if not text:
        return
    n = len(text.encode("utf-16-le")) // 2
    written = ctypes.c_uint32(0)
    kernel32.WriteConsoleW(h, text, n, byref(written), None)


def _dump_visible(h, path):
    """把控制台**可见窗口**的字符内容写到 path（自证渲染用）。"""
    info = CONSOLE_SCREEN_BUFFER_INFO()
    if not kernel32.GetConsoleScreenBufferInfo(h, byref(info)):
        raise OSError("GetConsoleScreenBufferInfo 失败 err=%d"
                      % ctypes.get_last_error())
    win = info.srWindow
    ncols = win.Right - win.Left + 1
    lines = ["### buffer=%dx%d  window=(%d,%d)-(%d,%d)  %dx%d 格"
             % (info.dwSize.X, info.dwSize.Y,
                win.Left, win.Top, win.Right, win.Bottom, ncols,
                win.Bottom - win.Top + 1)]
    row = win.Top
    while row <= win.Bottom:
        buf = ctypes.create_unicode_buffer(ncols + 1)
        got = ctypes.c_uint32(0)
        if kernel32.ReadConsoleOutputCharacterW(h, buf, ncols,
                                                COORD(win.Left, row), byref(got)):
            lines.append(buf[:got.value])
        else:
            lines.append("")
        row += 1
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))
    return True


def _reflow_loop(h, stop):
    """把缓冲区宽度对齐窗口宽度 —— 折行发生在可见边缘（bat 手感）。"""
    while not stop.is_set():
        try:
            info = CONSOLE_SCREEN_BUFFER_INFO()
            if kernel32.GetConsoleScreenBufferInfo(h, byref(info)):
                win_w = info.srWindow.Right - info.srWindow.Left + 1
                win_h = info.srWindow.Bottom - info.srWindow.Top + 1
                buf_w, buf_h = info.dwSize.X, info.dwSize.Y
                if win_w >= 1 and win_h >= 1:
                    want_h = buf_h
                    if want_h < win_h + BUF_KEEP_H:
                        want_h = min(win_h + BUF_KEEP_H, BUF_MAX_H)
                    if buf_w != win_w or buf_h != want_h:
                        kernel32.SetConsoleScreenBufferSize(h, COORD(win_w, want_h))
        except Exception:
            pass
        stop.wait(0.25)


def main():
    title = os.environ.get("FATFISH_LOGHOST_TITLE") or ("FATFISH_LOGHOST_%d" % os.getpid())
    try:
        kernel32.SetConsoleOutputCP(65001)      # 输出走 UTF-8
    except Exception:
        pass
    try:
        kernel32.SetConsoleTitleW(title)
    except Exception:
        pass

    h = _open_conout()

    # 打开 VT 处理：ANSI 色码由控制台自己解析（与 bat 里看到的一模一样）
    mode = ctypes.c_uint32(0)
    if kernel32.GetConsoleMode(h, byref(mode)):
        kernel32.SetConsoleMode(h, mode.value | ENABLE_VIRTUAL_TERMINAL_PROCESSING)

    stop = threading.Event()
    threading.Thread(target=_reflow_loop, args=(h, stop), daemon=True).start()

    dec = codecs.getincrementaldecoder("utf-8")("replace")
    while True:
        try:
            data = os.read(0, 65536)
        except Exception:
            break
        if not data:
            break
        if data.startswith(MARK):                # 控制通道
            path = ""
            try:
                path = data[len(MARK):].split(b"\n", 1)[0].decode("utf-8", "replace")
                if path:
                    _dump_visible(h, path)
            except Exception:
                try:
                    if path:
                        with open(path + ".err", "w", encoding="utf-8") as f:
                            f.write(traceback.format_exc())
                except Exception:
                    pass
            continue
        text = dec.decode(data)
        if text:
            try:
                _write_console(h, text)
            except Exception:
                pass
    stop.set()


if __name__ == "__main__":
    main()
