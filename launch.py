#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
launch.py —— 肥鱼「启动枢纽」

职责：
    1. 以子进程方式启动主程序 FATHFISHI.py，拿到它【真实的 PID】；
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
MAIN_SCRIPT = os.path.join(HERE, "FATHFISHI.py")
WATCHER_SCRIPT = os.path.join(HERE, "fatfish_watcher.py")
STATUS_SCRIPT = os.path.join(HERE, "status_console.py")
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

    # ============ [GUI-FIRST v1] 默认只留一个窗口 ============
    #   监控器 / 状态台**默认不再单独开窗** —— 它们的职责已被 GUI 内建承揽：
    #     · 状态台 → FATHFISHI 的 push_status()   → 对话窗口「🧿 状态」面板
    #     · 监控器 → chat_window 的 exec tailer  → 对话窗口「🖥 监控」面板
    #   主程序起来之后还会把 runtime 控制台窗口藏掉（fatfish_core/consolehide.py），
    #   于是整条启动链最终**只剩一块对话窗口**。
    #
    #   ★ 后路：需要旧的多窗口形态（排障 / 想要独立的 watcher 日志窗口）时，
    #     启动前 set FATFISH_MULTIWIN=1 即可，行为与升级前完全一致。
    _MULTIWIN = os.environ.get("FATFISH_MULTIWIN", "0").strip().lower() in (
        "1", "on", "true", "yes", "开", "是")
    watcher_proc = None
    status_proc = None

    if _MULTIWIN:
        _say("  ⚠️  多窗口模式 [multi-window]"
             "（FATFISH_MULTIWIN=1）：额外启动监控器 / 状态台窗口")
        # ---- 2) 启动监控器，独立黑窗口 ----
        if os.path.isfile(WATCHER_SCRIPT):
            try:
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

        # ---- 2.5) 启动状态台窗口 ----
        if os.path.isfile(STATUS_SCRIPT):
            try:
                status_proc = subprocess.Popen(
                    [PY, STATUS_SCRIPT, "--main-pid", str(main_pid)],
                    cwd=HERE,
                    creationflags=0x00000010,   # CREATE_NEW_CONSOLE
                )
                _say(f"  ✅ 状态台已启动 [status console started] "
                     f"PID={status_proc.pid}（独立窗口 [separate window]）")
            except Exception as e:
                _say(f"  ⚠️  状态台启动失败 [status console failed]：{e}"
                     f"（过程信息将回落到主窗口）")
        else:
            _say(f"  ⚠️  未找到状态台脚本 [status script not found]：{STATUS_SCRIPT}"
                 f"（过程信息将回落到主窗口）")
    else:
        _say("  ℹ️  单窗口模式 [single-window]：")
        _say("       状态台 → 对话窗口「🧿 状态」面板 ｜ 监控器 → 「🖥 监控」面板")
        _say("       主程序起来后连本控制台窗口也会藏起来 → 最终只剩一块对话窗口")
        _say("       （要旧的多窗口形态：启动前 set FATFISH_MULTIWIN=1）")

    # ---- 3) 等待主程序结束 ----
    if _MULTIWIN:
        _say("  ⏳ 主程序运行中……关闭主程序后，监控器将自动进入 30 秒倒计时退出。")
        _say("  ⏳ Main program running... after it closes, the watcher will "
             "count down 30s and exit automatically.")
    else:
        _say("  ⏳ 主程序运行中……关掉对话窗口即结束本进程。")
        _say("  ⏳ Main program running... close the chat window to end it.")
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

    # ---- [CRASH-GUARD v1] 保证控制台窗口可见（硬崩溃也不让用户对着隐形窗口按任意键）----
    #   背景：GUI-First 下主程序起来后会把 runtime 控制台**藏起来**（fatfish_core.consolehide）。
    #         正常退出时主程序自己会 show() 回来；但**硬崩溃**（0xC0000409 / 被强杀等）
    #         走不到那一步 —— 控制台还藏着，而 fatfish_runtime.bat 却停在 `pause` 等按键，
    #         用户只看到"什么都没有"的桌面，窗口在后台无声地等一个永远不来的按键。
    #   为什么放这里：本进程与主程序**共用同一个控制台窗口**；无论主程序怎么结束（正常、
    #         报错、被强杀），这个位置都一定会执行到。所以无条件"把窗口请回来"即可 ——
    #         若本来就可见，is_visible() 短路，不做任何事（不抢焦点、零副作用）。
    #   回滚：删掉本段即可（或整文件还原 _backup/launch.py.*.bak）。
    try:
        from fatfish_core import consolehide as _ch
        if _ch.available() and not _ch.is_visible():
            _ch.show()
            _say("  🖥  控制台窗口已恢复显示 [console restored]"
                 "（主程序可能是异常结束，收尾信息见本窗口）")
    except Exception:
        pass

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

    # ---- 4.5) 等状态台收尾（它自己检测到主程序退出，一般会很快关掉）----
    if status_proc is not None:
        _say("  ⌛ 等待状态台收尾 [waiting for status console] ...")
        try:
            status_proc.wait(timeout=15)
        except subprocess.TimeoutExpired:
            _say("  ⚠️  状态台超时未退，强制结束 [status console timeout, killing] ...")
            try:
                status_proc.kill()
            except Exception:
                pass

    _say("  👋 启动枢纽退出 [launcher exited]。")
    return exit_code if isinstance(exit_code, int) else 0


if __name__ == "__main__":
    sys.exit(main())
