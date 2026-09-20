#!/usr/bin/env python

import os
import sys
import time
import json
import subprocess
from datetime import datetime, timedelta

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

DEFAULT_INTERVAL = 1.0
PS_TIMEOUT = 30
LOG_ROOT = "logs"
DRAIN_ROUNDS = 3
DRAIN_INTERVAL = 1.0

_CREATE_NO_WINDOW = 0x08000000 if sys.platform == "win32" else 0

try:
    from common import ts as _ts, dated_dir as _dated_dir
except ImportError:
    def _ts():
        return datetime.now().strftime("%Y-%m-%d %H:%M:%S")

    def _dated_dir(root):
        now = datetime.now()
        d = os.path.join(root, f"{now:%Y}", f"{now:%m}", f"{now:%d}")
        os.makedirs(d, exist_ok=True)
        return d

class Logger:

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
        self._fh.write(text + "\n")
        self._fh.flush()
        if echo:
            print(text)

    def close(self):
        try:
            self._fh.close()
        except Exception:
            pass

class ExecTailer:

    def __init__(self, root="logs", from_now=True):
        self.root = root
        self._offsets = {}
        self._announced = set()
        if from_now:
            self._prime()

    def _prime(self):
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
                try:
                    self._offsets[path] = os.path.getsize(path)
                except OSError:
                    pass

    def _today_dirs(self):
        now = datetime.now()
        dirs = []
        for delta_day in (0, 1):
            d = now - timedelta(days=delta_day)
            dirs.append(os.path.join(self.root, f"{d:%Y}", f"{d:%m}", f"{d:%d}"))
        return dirs

    def poll(self, lg):
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
            offset = 0

        if size == offset:
            return

        if path not in self._announced:
            self._announced.add(path)
            lg.raw("-" * 68)
            lg.log(f"[OUT] sub-program output starts -> {os.path.basename(path)}")

        try:
            with open(path, "r", encoding="utf-8", errors="replace") as f:
                f.seek(offset)
                chunk = f.read()
                self._offsets[path] = f.tell()
        except OSError:
            return

        if not chunk:
            return

        for line in chunk.splitlines():
            lg.raw("    | " + line)

_PS_ALIVE = r"""
$ErrorActionPreference = 'SilentlyContinue'
$p = Get-Process -Id {pid} -ErrorAction SilentlyContinue
if ($p) { 'ALIVE' } else { 'GONE' }
"""

def is_alive(pid):
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
        print("usage: python fatfish_watcher.py <main-program-PID> [--interval seconds]")
        return

    try:
        target_pid = int(sys.argv[1])
    except ValueError:
        print(f"invalid main-program PID: {sys.argv[1]}")
        return

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
    lg.log("FatFish Watcher started")
    lg.log("this window shows exec_tools sub-program output only")
    lg.log(f"target main-program PID: {target_pid}")
    lg.log(f"log file: {log_path}")
    lg.raw("=" * 68)
    lg.raw("")

    missing_streak = 0
    MISSING_TOLERANCE = 3

    tailer = ExecTailer(LOG_ROOT)

    while True:
        time.sleep(interval)

        try:
            tailer.poll(lg)
        except Exception:
            pass

        alive = is_alive(target_pid)

        if alive is None:
            continue

        if alive:
            missing_streak = 0
        else:
            missing_streak += 1
            if missing_streak >= MISSING_TOLERANCE:
                lg.raw("")
                lg.raw("-" * 68)
                lg.log("[STOP] main program fully closed")
                break
            else:
                continue

    lg.log(f"[..] draining sub-program output "
           f"(about {DRAIN_ROUNDS * DRAIN_INTERVAL:.0f}s) ...")
    for _ in range(DRAIN_ROUNDS):
        time.sleep(DRAIN_INTERVAL)
        try:
            tailer.poll(lg)
        except Exception:
            pass

    lg.raw("-" * 68)
    lg.log("[OK] monitoring stopped")
    lg.log(f"full log: {log_path}")
    lg.raw("=" * 68)
    lg.close()

    print()
    print("  FatFish watcher stopped recording.")
    print(f"  full log: {log_path}")
    print()
    _countdown_exit(30)
    print("  Watcher exited.")

def _countdown_exit(seconds):
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
        print(f"\r  [..] auto-close in {remaining:2d}s, "
              f"press any key to exit now ... ", end="", flush=True)
        for _ in range(10):
            if msvcrt.kbhit():
                msvcrt.getch()
                print("\r  [key] pressed, exiting early ..."
                      + " " * 40)
                return
            time.sleep(0.1)
    print("\r  [..] countdown finished, closing ..."
          + " " * 40)

if __name__ == "__main__":
    main()
