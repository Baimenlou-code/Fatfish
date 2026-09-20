#!/usr/bin/env python

import os
import sys
import time
import subprocess

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

HERE = os.path.dirname(os.path.abspath(__file__))
MAIN_SCRIPT = os.path.join(HERE, "FATENFISH.py")
WATCHER_SCRIPT = os.path.join(HERE, "fatfish_watcher.py")
PY = sys.executable or "python"

def _say(msg):
    print(msg, flush=True)

def main():
    if not os.path.isfile(MAIN_SCRIPT):
        _say(f"  [X] Main script not found: {MAIN_SCRIPT}")
        return 1

    _say("  [*] Launching FatFish main program ...")

    try:
        main_proc = subprocess.Popen(
            [PY, MAIN_SCRIPT],
            cwd=HERE,
        )
    except Exception as e:
        _say(f"  [X] Failed to launch main program: {e}")
        return 1

    main_pid = main_proc.pid
    _say(f"  [OK] Main program started, PID={main_pid}")

    watcher_proc = None
    if os.path.isfile(WATCHER_SCRIPT):
        try:
            watcher_proc = subprocess.Popen(
                [PY, WATCHER_SCRIPT, str(main_pid)],
                cwd=HERE,
                creationflags=0x00000010,
            )
            _say(f"  [OK] Watcher started, PID={watcher_proc.pid} (separate window)")
        except Exception as e:
            _say(f"  [!] Watcher failed to start: {e}")
    else:
        _say(f"  [!] Watcher script not found: {WATCHER_SCRIPT}")

    _say("  [..] Main program running... after it closes, the watcher will "
         "count down 30s and exit automatically.")
    try:
        exit_code = main_proc.wait()
    except KeyboardInterrupt:
        _say("  [!] Interrupted, terminating main program ...")
        try:
            main_proc.terminate()
        except Exception:
            pass
        exit_code = -1

    _say(f"  [--] Main program ended, exit code: {exit_code}")

    if watcher_proc is not None:
        _say("  [..] Waiting for watcher to finish ...")
        try:
            watcher_proc.wait(timeout=40)
        except subprocess.TimeoutExpired:
            _say("  [!] Watcher timed out, killing it ...")
            try:
                watcher_proc.kill()
            except Exception:
                pass

    _say("  [bye] Launcher exited.")
    return exit_code if isinstance(exit_code, int) else 0

if __name__ == "__main__":
    sys.exit(main())
