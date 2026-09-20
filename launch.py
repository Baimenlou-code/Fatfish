#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
launch.py —— 肥鱼「启动枢纽」

职责：
    1. 以子进程方式启动主程序 FATHFISH.py，拿到它【真实的 PID】；
    2. 用这个 PID 启动监控器 fatfish_watcher.py（独立黑窗口，实时滚动）；
    3. 等待主程序结束；
    4. 主程序一结束，监控器会自行检测到「目标 PID 消失」，
       停止记录并进入 30 秒倒计时后自动退出。

为什么需要它：
    纯 bat 很难拿到子进程的真实 PID（bat 里 %errorlevel% 只是退出码，
    拿不到 PID）。用 Python 的 subprocess.Popen 拿 pid 最干净，
    也避免了 bat 里多层引号嵌套导致的「闪退」。

用法：
    python launch.py
"""

import os
import sys
import time
import subprocess

# ============ UTF-8 兜底 ============
if sys.platform == "win32":
    os.environ.setdefault("PYTHONIOENCODING", "utf-8")
    os.environ.setdefault("PYTHONUTF8", "1")
for _name in ("stdout", "stderr"):
    _stream = getattr(sys, _name, None)
    if _stream is not None and hasattr(_stream, "reconfigure"):
        try:
            _stream.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass

# ============ 路径 ============
HERE = os.path.dirname(os.path.abspath(__file__))
MAIN_SCRIPT = os.path.join(HERE, "FATHFISH.py")
WATCHER_SCRIPT = os.path.join(HERE, "fatfish_watcher.py")
PY = sys.executable or "python"


def _say(msg):
    print(msg, flush=True)


def main():
    if not os.path.isfile(MAIN_SCRIPT):
        _say(f"  ❌ 找不到主程序 [main script not found]：{MAIN_SCRIPT}")
        return 1

    _say("  🐟 正在启动肥鱼主程序 [launching FatFish main program] ...")

    # ---- 1) 启动主程序，拿到真实 PID ----
    # 主程序继承当前控制台（前台运行），所以这里不能用 CREATE_NEW_CONSOLE，
    # 否则它的输出会跑到另一个窗口。当前窗口本身就是 runtime 窗口。
    try:
        main_proc = subprocess.Popen(
            [PY, MAIN_SCRIPT],
            cwd=HERE,
        )
    except Exception as e:
        _say(f"  ❌ 主程序启动失败 [failed to launch main program]：{e}")
        return 1

    main_pid = main_proc.pid
    _say(f"  ✅ 主程序已启动 [main program started] PID={main_pid}")

    # ---- 2) 启动监控器，独立黑窗口 ----
    watcher_proc = None
    if os.path.isfile(WATCHER_SCRIPT):
        try:
            # CREATE_NEW_CONSOLE：给监控器单独一个可见窗口
            watcher_proc = subprocess.Popen(
                [PY, WATCHER_SCRIPT, str(main_pid)],
                cwd=HERE,
                creationflags=0x00000010,   # CREATE_NEW_CONSOLE
            )
            _say(f"  ✅ 监控器已启动 [watcher started] PID={watcher_proc.pid}"
                 f"（独立窗口 [separate window]）")
        except Exception as e:
            _say(f"  ⚠️  监控器启动失败 [watcher failed to start]：{e}")
    else:
        _say(f"  ⚠️  未找到监控器脚本 [watcher script not found]：{WATCHER_SCRIPT}")

    # ---- 3) 等待主程序结束 ----
    _say("  ⏳ 主程序运行中……关闭主程序后，监控器将自动进入 30 秒倒计时退出。")
    _say("  ⏳ Main program running... after it closes, the watcher will "
         "count down 30s and exit automatically.")
    try:
        exit_code = main_proc.wait()
    except KeyboardInterrupt:
        # Ctrl+C：把主程序也一起收掉
        _say("  ⚠️  收到中断，正在结束主程序 [interrupted, terminating main] ...")
        try:
            main_proc.terminate()
        except Exception:
            pass
        exit_code = -1

    _say(f"  🛑 主程序已结束 [main program ended]，退出码 [exit code]：{exit_code}")

    # ---- 4) 等监控器自己收尾（最多等 40 秒，避免本进程僵住）----
    if watcher_proc is not None:
        _say("  ⌛ 等待监控器收尾 [waiting for watcher to finish] ...")
        try:
            watcher_proc.wait(timeout=40)
        except subprocess.TimeoutExpired:
            _say("  ⚠️  监控器超时未退，强制结束 [watcher timeout, killing] ...")
            try:
                watcher_proc.kill()
            except Exception:
                pass

    _say("  👋 启动枢纽退出 [launcher exited]。")
    return exit_code if isinstance(exit_code, int) else 0


if __name__ == "__main__":
    sys.exit(main())
