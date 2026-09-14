#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
fatfish_watcher.py —— 肥鱼「子程序输出监控器」

用途：
    与主程序（FATGFISH.py）并行运行，独立黑窗口，专门实时滚动显示
    exec_tools.py 跑起来的 CMD / Python 程序的输出。

    换句话说：这个窗口就是「exec_tools 子程序输出的实时镜子」。
    不再显示进程树、不再打印新进程发现记录——只看子程序到底跑出来些啥。

主程序关闭后：
    停记录 → 再 tail 一小段（把子程序最后的输出尾巴晾完）→ 30 秒倒计时退出，
    倒计时期间按任意键可立即退出。

用法：
    python fatfish_watcher.py <主程序PID> [--interval 秒]

监控原理：
    exec_tools.py 每次执行子进程，都会把 stdout/stderr 实时写入
    logs/YYYY/MM/DD/exec_*.out。本脚本周期扫描这些文件，把「新增的内容」
    原样打印到窗口。同时用一个极轻量的进程存活检测（不打印）判断主程序
    是否已关闭，用于决定何时收尾退出。

日志：
    logs/YYYY/MM/DD/watcher_HHMMSS.log
"""

import os
import sys
import time
import json
import subprocess
from datetime import datetime, timedelta

# ============ 编码兜底（关键修复）============
# 本脚本会 print 大量中文/emoji。当它被 subprocess 拉起、且 stdout 被重定向到
# 文件或管道时，Python 3.8 可能回退到 ascii/GBK 编码，导致：
#   'ascii' codec can't encode characters in position ...
# 这里在启动最早期就把 stdout/stderr 强制重配为 UTF-8，彻底根治。
if sys.platform == "win32":
    os.environ.setdefault("PYTHONIOENCODING", "utf-8")
    os.environ.setdefault("PYTHONUTF8", "1")
for _stream_name in ("stdout", "stderr"):
    _stream = getattr(sys, _stream_name, None)
    if _stream is not None and hasattr(_stream, "reconfigure"):
        try:
            _stream.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass

# ============ 可调参数 ============
DEFAULT_INTERVAL = 1.0     # 轮询间隔（秒）
PS_TIMEOUT = 30            # 单次 PowerShell 存活检测超时（秒）
LOG_ROOT = "logs"
DRAIN_ROUNDS = 3           # 主程序关闭后，再 tail 几轮把输出尾巴晾完
DRAIN_INTERVAL = 1.0       # 每轮 drain 的间隔（秒）

# Windows 下隐藏黑框
_CREATE_NO_WINDOW = 0x08000000 if sys.platform == "win32" else 0


def _ts():
    return datetime.now().strftime("%Y-%m-%d %H:%M:%S")


# ---------- 日志 ----------
def _dated_dir(root):
    now = datetime.now()
    d = os.path.join(root, f"{now:%Y}", f"{now:%m}", f"{now:%d}")
    os.makedirs(d, exist_ok=True)
    return d


class Logger:
    """同时写文件 + 打印到控制台。"""

    def __init__(self, path):
        self.path = path
        self._fh = open(path, "a", encoding="utf-8")

    def log(self, msg, echo=True):
        line = f"[{_ts()}] {msg}"
        self._fh.write(line + "\n")
        self._fh.flush()
        if echo:
            print(line)

    def raw(self, text, echo=True):
        """原样写一行（用于分隔线等）。"""
        self._fh.write(text + "\n")
        self._fh.flush()
        if echo:
            print(text)

    def close(self):
        try:
            self._fh.close()
        except Exception:
            pass


# ---------- 子程序输出实时跟踪 ----------
class ExecTailer:
    """实时 tail 工作台里 exec_tools 落盘的「子程序输出文件」。

    exec_tools.py 每次执行 CMD / Python 程序，都会把子进程的 stdout/stderr
    实时写入 logs/YYYY/MM/DD/exec_*.out。本类周期扫描这些文件，
    把「新增的内容」原样打印到监控器窗口，于是你就能看到
    「跑的程序到底跑出来些啥」，而不只是「跑了啥」。

    做法：
      - 记录每个文件已读到的字节偏移 self._offsets[path]
      - 每轮扫描当天目录下所有 exec_*.out
      - 对每个文件，从上次偏移继续读，读到多少打印多少
      - 文件被删除 / 目录不存在时静默跳过
    """

    def __init__(self, root="logs"):
        self.root = root
        self._offsets = {}          # path -> 已读字节数
        self._announced = set()     # 已打印过「开始输出」标题的文件

    def _today_dirs(self):
        """返回今天（以及昨天，防止跨零点）的 exec 输出目录列表。"""
        now = datetime.now()
        dirs = []
        for delta_day in (0, 1):
            d = now - timedelta(days=delta_day)
            dirs.append(os.path.join(self.root, f"{d:%Y}", f"{d:%m}", f"{d:%d}"))
        return dirs

    def poll(self, lg):
        """扫描一轮，把新内容打印出来。lg 是 Logger 实例。"""
        for d in self._today_dirs():
            if not os.path.isdir(d):
                continue
            try:
                names = os.listdir(d)
            except OSError:
                continue
            for name in names:
                if not (name.startswith("exec_") and name.endswith(".out")):
                    continue
                path = os.path.join(d, name)
                self._tail_one(path, lg)

    def _tail_one(self, path, lg):
        try:
            size = os.path.getsize(path)
        except OSError:
            return

        offset = self._offsets.get(path, 0)
        if size < offset:
            # 文件被截断/重写，从头再来
            offset = 0

        if size == offset:
            return  # 没有新内容

        # 首次看到这个文件，打印一条分隔标题
        if path not in self._announced:
            self._announced.add(path)
            lg.raw("-" * 68)
            lg.log(f"📤 子程序开始输出 [sub-program output] → {os.path.basename(path)}")

        try:
            with open(path, "r", encoding="utf-8", errors="replace") as f:
                f.seek(offset)
                chunk = f.read()
                self._offsets[path] = f.tell()
        except OSError:
            return

        if not chunk:
            return

        # 原样逐行打印，前面加个短前缀，便于和监控器自身日志区分
        for line in chunk.splitlines():
            lg.raw("    │ " + line)


# ---------- 主程序存活检测（轻量，仅用于判断何时收尾，不打印） ----------
_PS_ALIVE = r"""
$ErrorActionPreference = 'SilentlyContinue'
$p = Get-Process -Id {pid} -ErrorAction SilentlyContinue
if ($p) { 'ALIVE' } else { 'GONE' }
"""


def is_alive(pid):
    """检测目标 PID 是否仍存活。

    返回 True / False；检测失败（异常、超时）返回 None，
    调用方据此跳过本轮，避免误判「已退出」。
    """
    try:
        r = subprocess.run(
            ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
             "-Command", _PS_ALIVE.format(pid=pid)],
            capture_output=True, text=True, errors="replace",
            timeout=PS_TIMEOUT,
            creationflags=_CREATE_NO_WINDOW,
        )
    except Exception:
        return None

    out = (r.stdout or "").strip().upper()
    if out == "ALIVE":
        return True
    if out == "GONE":
        return False
    return None


def main():
    if len(sys.argv) < 2:
        print("用法：python fatfish_watcher.py <主程序PID> [--interval 秒]")
        return

    try:
        target_pid = int(sys.argv[1])
    except ValueError:
        print(f"无效的主程序 PID：{sys.argv[1]}")
        return

    # 解析 --interval
    interval = DEFAULT_INTERVAL
    if "--interval" in sys.argv:
        try:
            interval = float(sys.argv[sys.argv.index("--interval") + 1])
        except (IndexError, ValueError):
            pass
    interval = max(0.3, interval)

    log_dir = _dated_dir(LOG_ROOT)
    log_path = os.path.join(log_dir, f"watcher_{datetime.now():%H%M%S}.log")
    lg = Logger(log_path)

    lg.raw("=" * 68)
    lg.log("🐟 肥鱼监控器已启动 [FatFish Watcher started]")
    lg.log("本窗口只显示 exec_tools 子程序的实时输出 "
           "[showing exec_tools sub-program output only]")
    lg.log(f"监控目标主程序 PID [target main-program PID]：{target_pid}")
    lg.log(f"日志文件 [log file]：{log_path}")
    lg.raw("=" * 68)
    lg.raw("")

    missing_streak = 0        # 连续多少次检测不到主程序
    MISSING_TOLERANCE = 3     # 连续 3 次（约 3 秒）没见到才判定「彻底关闭」

    tailer = ExecTailer(LOG_ROOT)

    while True:
        time.sleep(interval)

        # 先 tail 子程序输出
        try:
            tailer.poll(lg)
        except Exception:
            pass

        alive = is_alive(target_pid)

        # 检测失败 → 跳过本轮，不误判
        if alive is None:
            continue

        if alive:
            missing_streak = 0
        else:
            missing_streak += 1
            if missing_streak >= MISSING_TOLERANCE:
                # 主程序彻底关闭
                lg.raw("")
                lg.raw("-" * 68)
                lg.log("🛑 主程序已彻底关闭 [main program fully closed]")
                break
            else:
                continue

    # ---- 主程序关闭后：再 tail 几轮，把子程序最后的输出尾巴晾完 ----
    lg.log(f"⌛ 子程序延时收尾中 [draining sub-program output]"
           f"（约 {DRAIN_ROUNDS * DRAIN_INTERVAL:.0f} 秒）...")
    for _ in range(DRAIN_ROUNDS):
        time.sleep(DRAIN_INTERVAL)
        try:
            tailer.poll(lg)
        except Exception:
            pass

    # ---- 收尾：停止记录，30 秒倒计时后自动退出（按键可提前退）----
    lg.raw("-" * 68)
    lg.log("✅ 监控已停止 [monitoring stopped]")
    lg.log(f"完整日志见 [full log]：{log_path}")
    lg.raw("=" * 68)
    lg.close()

    print()
    print("  🐟 监控器已停止记录 [watcher stopped recording]。")
    print(f"  🐟 完整日志 [full log]：{log_path}")
    print()
    _countdown_exit(30)
    print("  👋 监控器退出 [watcher exited]。")


def _countdown_exit(seconds):
    """倒计时 seconds 秒后返回；期间按任意键可立即返回。

    Windows 下用 msvcrt.kbhit() 做非阻塞检测；其他平台退回 sleep。
    """
    if sys.platform != "win32":
        try:
            time.sleep(seconds)
        except KeyboardInterrupt:
            pass
        return

    try:
        import msvcrt
    except Exception:
        try:
            time.sleep(seconds)
        except KeyboardInterrupt:
            pass
        return

    for remaining in range(seconds, 0, -1):
        # \r 回到行首覆盖刷新；end="" 不换行
        print(f"\r  ⏳ {remaining:2d} 秒后自动关闭 [auto-close in {remaining:2d}s]，"
              f"按任意键立即退出 [press any key to exit now] ... ", end="", flush=True)
        for _ in range(10):          # 把 1 秒切成 10 份，保证按键响应灵敏
            if msvcrt.kbhit():
                msvcrt.getch()       # 吃掉这个按键
                print("\r  ⌨️  检测到按键，提前退出 [key pressed, exiting early] ..."
                      + " " * 40)
                return
            time.sleep(0.1)
    print("\r  ⌛ 倒计时结束，自动关闭 [countdown finished, closing] ..."
          + " " * 40)


if __name__ == "__main__":
    main()
