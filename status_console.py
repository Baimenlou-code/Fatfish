# -*- coding: utf-8 -*-
"""
status_console.py —— 肥鱼「状态台」（独立窗口）/ FatFish Status Console

为什么需要 / Why
----------------
主窗口应该只剩「你和肥鱼的对话」。但有两类信息天然不属于对话：

  · 需要你**亲手裁决**的报批申请  → 留在主窗口（那是交互，不是噪音）
  · 只是**告知过程**的状态信息    → 送到本窗口

本窗口专门接收后者，典型内容：
    ✅ 双人核验 → approve：<审查员的理由>
    ✅ 工作台 [workspace] ws_run_python → （解释器：...）
    🔓 本轮自动放行 2 个内容写入操作（无需再确认）
    🌐 联网需求核验 → 需要联网（high）：...
    以及各类「进行中 / 已降级 / 已放行」的提示

通信协议 / Protocol
-------------------
目录：<程序目录>/.fatfish_tmp/console/
  console.online   本窗口心跳（周期 touch mtime；主程序据此判断我是否在线）
  status.log       主程序 append 的状态流，本窗口增量 tail

关键约定（**不丢信息**）：
  主程序在写状态前会检查 console.online。
  若本窗口没启动（例如直接跑 python FATHFISH.py），心跳不存在 →
  主程序自动把这些信息打回主窗口，绝不会静默吞掉。

按键 / Keys
-----------
  k / ESC   中止正在运行的程序（与监控器窗口同机制，写中止哨兵）
  c         清屏（只清显示，不回退文件位置）
  q         退出本窗口

启动 / Launch
-------------
  由 launch.py 用 CREATE_NEW_CONSOLE 拉起；
  也可单独运行：python status_console.py

  ★ 默认**只显示本窗口启动之后的新状态**，不回放历史
    （status.log 是追加写、跨会话保留；启动时直接跟到文件末尾）。
    想回看最近若干行：
        python status_console.py --tail 20
    也可以用环境变量 FATFISH_STATUS_TAIL=20 指定。
"""

import os
import sys
import time
import traceback
from datetime import datetime

# ============ UTF-8 兜底（与其它模块同款）============
if sys.platform == "win32":
    os.environ.setdefault("PYTHONIOENCODING", "utf-8")
    os.environ.setdefault("PYTHONUTF8", "1")
for _n in ("stdout", "stderr"):
    _s = getattr(sys, _n, None)
    if _s is not None and hasattr(_s, "reconfigure"):
        try:
            _s.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass

# ============ 路径 / 常量 ============
HERE = os.path.dirname(os.path.abspath(__file__))
CONSOLE_DIR = os.path.join(HERE, ".fatfish_tmp", "console")
STATUS_LOG = os.path.join(CONSOLE_DIR, "status.log")
ONLINE_PATH = os.path.join(CONSOLE_DIR, "console.online")
ABORT_SENTINEL = os.path.join(HERE, ".fatfish_abort.signal")   # 与 exec_tools 一致


# ---------- [QUIT-CLEAN v1] 主程序退出信号 ----------
_SHUTDOWN_SIGNAL = os.path.join(HERE, ".fatfish_tmp", "shutdown.signal")
_MAIN_PID_FILE = os.path.join(HERE, ".fatfish_tmp", "main.pid")
_T0 = time.time()
_MAIN_PID = None          # 我该服务哪个主程序（main() 里从 --main-pid 取）


def _main_pid_from_file(slack=180.0):
    """从 .fatfish_tmp/main.pid 读「主程序真身」PID；没有/太旧则返回 None。

    ★ 为什么需要它：本机 venv 的 python.exe 是**转发壳**，
      launch.py 记下的 main_proc.pid 是壳的 PID，而真正跑主循环、写退出
      信号的是壳的子进程（真身）。所以这里优先读主程序自己写的文件。
    """
    try:
        if os.path.getmtime(_MAIN_PID_FILE) < _T0 - float(slack):
            return None                      # 上次运行的残留，不当真
        with open(_MAIN_PID_FILE, "r", encoding="utf-8", errors="replace") as f:
            v = f.read(64).strip()
        return int(v) if v.isdigit() else None
    except Exception:
        return None


def _effective_main_pid():
    """当前该认定的主程序 PID：真身文件优先，其次 --main-pid。"""
    return _main_pid_from_file() or _MAIN_PID


def _signal_pid():
    """读退出信号里的主程序 PID；旧格式（无 PID 字段）返回 None。"""
    try:
        with open(_SHUTDOWN_SIGNAL, "r", encoding="utf-8", errors="replace") as f:
            raw = f.read(200).strip()
    except Exception:
        return None
    parts = raw.split()
    if not parts:
        return None
    try:
        return int(parts[-1])
    except (TypeError, ValueError):
        return None


def _shutdown_requested():
    """主程序是否「有意退出」——且**这一份信号是写给我服务的那个主程序的**。

    判据：① 信号比本进程启动新（防止上次残留误伤新窗口）；
          ② 信号里的 PID == 我该服务的主程序（真身文件优先，其次 --main-pid）。
    兼容：旧格式信号（无 PID）或不知道服务谁时，退回纯时间戳，行为同以前。

    ★ 2026-10-02 加 ②：此前只看时间戳，任何 fatfish 实例退出都会把别的
      窗口一起关掉（真实事故）。
    """
    try:
        if not os.path.exists(_SHUTDOWN_SIGNAL):
            return False
        if os.path.getmtime(_SHUTDOWN_SIGNAL) < _T0 - 1.0:
            return False                     # 旧信号：不是本次运行的
        pid = _signal_pid()
        if pid is None:
            return True                      # 旧格式：保持原行为
        mine = _effective_main_pid()
        if mine is None:
            return True                      # 不知道服务谁：也只能认时间戳
        return int(pid) == int(mine)
    except Exception:
        return False

HEARTBEAT_SEC = 1.5      # 心跳刷新间隔
POLL_SEC = 0.25          # 扫描新状态的间隔
PRIME_MAX = 64 * 1024    # --tail 回看时的最大字节数（防止把超大日志整段读进来）
STATUS_TAIL = 0          # 启动时回看多少行；0 = 不回看（默认，只看新状态）
WINDOW_TITLE = "🐟 肥鱼状态台 [FatFish Status Console]"
LINE = "═" * 62
DASH = "─" * 62


# ============ 基础工具 ============
def ensure_dirs():
    try:
        os.makedirs(CONSOLE_DIR, exist_ok=True)
        return True
    except OSError:
        return False


def touch(path):
    try:
        with open(path, "a", encoding="utf-8"):
            pass
        os.utime(path, None)
    except OSError:
        pass


def set_title(title):
    if sys.platform != "win32":
        return
    try:
        import ctypes
        ctypes.windll.kernel32.SetConsoleTitleW(title)
    except Exception:
        pass


def clear_screen():
    try:
        os.system("cls" if sys.platform == "win32" else "clear")
    except Exception:
        pass


def pid_alive(pid):
    """目标进程是否存活。返回 True / False / None（无法判定）。"""
    if not pid:
        return None
    try:
        pid = int(pid)
    except (TypeError, ValueError):
        return None
    if sys.platform == "win32":
        try:
            import ctypes
            k = ctypes.windll.kernel32
            h = k.OpenProcess(0x1000, False, pid)   # PROCESS_QUERY_LIMITED_INFORMATION
            if h:
                k.CloseHandle(h)
                return True
            return False if k.GetLastError() != 5 else True   # 5 = 拒绝访问，可能还活着
        except Exception:
            return None
    try:
        os.kill(pid, 0)
        return True
    except OSError:
        return False


def parse_arg(argv, name):
    for i, a in enumerate(argv):
        if a == name and i + 1 < len(argv):
            return argv[i + 1]
        if a.startswith(name + "="):
            return a.split("=", 1)[1]
    return None


# ============ 渲染（纯函数，便于自测）============
def render_header():
    return "\n".join([
        LINE,
        "  🐟 肥鱼状态台 [FatFish Status Console]",
        LINE,
        "",
        "  本窗口显示**过程状态**（不打断主窗口的对话）：",
        "    · 双人核验的结论与理由",
        "    · 工作台工具的执行结果",
        "    · 自动放行 / 联网判定 / 降级提示",
        "",
        "  ⚠️ 需要你点头的**报批申请仍在主窗口**，不在本窗口。",
        "",
        "  按键： k 或 ESC = 中止正在运行的程序 ｜ c = 清屏 ｜ q = 退出",
        "  （默认只显示**启动之后**的新状态，不回放历史；想回看用 --tail 20）",
        DASH,
        "",
    ])


def format_line(text, ts=None):
    """给一条状态加时间前缀（主程序写入的是纯文本，这里补时间戳）。"""
    ts = ts or datetime.now().strftime("%H:%M:%S")
    body = (text or "").rstrip()
    # 主程序已经带缩进（'  ✅ ...'），保留但把时间戳插在最前
    if body.startswith("  "):
        body = body[2:]
    return "[%s] %s" % (ts, body)


def compute_start_offset(size, back=None):
    """启动时从哪个字节开始跟。

    ★ 现在的语义（修于 2026-10-02）：**默认直接跟到文件末尾** —— 只显示
      「启动之后」的新状态，不回放历史。

      旧实现是「小文件从 0 开始、大文件回看 PRIME_MAX 字节」，结果变成
      **日志越小、回放得越全**：每次开状态台都会把上一次会话的过程信息
      整段重放一遍（表现就是「状态栏每次都加载出上次的内容」）。
      想主动回看历史，请用 `--tail N`（见 tail_start_offset）。

    back：仅作向后兼容保留 —— 显式传了才按「回看 back 字节」算。
    """
    size = max(0, int(size or 0))
    if back:
        return max(0, size - int(back))
    return size


def tail_start_offset(path, n_lines, back=PRIME_MAX):
    """返回一个偏移量，使从该处读起恰好包含**最后 n_lines 行**。

    只在用户显式 `--tail N` 时用；最多回看 back 字节（超大日志不会被整段读入）。
    返回的偏移量一定落在行首（回看窗的第一行若被截断就跳过它），
    这样打印出来的第一行不会是残行。
    """
    try:
        size = os.path.getsize(path)
    except OSError:
        return 0
    if size <= 0:
        return 0
    try:
        n_lines = int(n_lines)
    except (TypeError, ValueError):
        return size
    if n_lines <= 0:
        return size
    start = max(0, size - int(back))
    try:
        with open(path, "rb") as f:
            f.seek(start)
            data = f.read()
    except OSError:
        return size

    end = len(data)
    while end > 0 and data[end - 1:end] in (b"\n", b"\r"):
        end -= 1                      # 忽略结尾的空行，免得「最后一行」算成空
    pos = end
    for _ in range(n_lines):
        if pos <= 0:
            break
        j = data.rfind(b"\n", 0, pos)
        if j < 0:
            pos = 0
            break
        pos = j
    off = start + (pos + 1 if pos > 0 else 0)
    if start > 0 and off <= start:
        k = data.find(b"\n")          # 回看窗首行可能是半截 → 跳到下一个行首
        off = start + (k + 1 if k >= 0 else 0)
    return max(0, min(off, size))


def count_lines(path, max_bytes=8 * 1024 * 1024):
    """数文件里有多少行（只为提示文案用；超大文件只在尾部窗口内统计）。"""
    try:
        size = os.path.getsize(path)
    except OSError:
        return 0
    if size <= 0:
        return 0
    truncated = size > max_bytes
    try:
        with open(path, "rb") as f:
            if truncated:
                f.seek(size - max_bytes)
            data = f.read()
    except OSError:
        return 0
    n = data.count(b"\n")
    if truncated:
        return n                      # 近似：尾部窗口内的行数
    return n + (0 if data.endswith(b"\n") else 1)


def _resolve_tail_arg(argv, env_name="FATFISH_STATUS_TAIL"):
    """解析 --tail N（也认环境变量）。非法值一律当作 0（= 不回看）。"""
    raw = parse_arg(argv, "--tail")
    if raw is None or str(raw).strip() == "":
        raw = os.environ.get(env_name)
    try:
        n = int(str(raw).strip()) if raw not in (None, "") else 0
    except (TypeError, ValueError):
        n = 0
    return max(0, n)


# ============ 按键 ============
def read_key():
    """非阻塞读一个按键，返回小写字符；无按键返回 None。"""
    if sys.platform != "win32":
        return None
    try:
        import msvcrt
        if not msvcrt.kbhit():
            return None
        ch = msvcrt.getch()
        if ch in (b"\x00", b"\xe0"):
            msvcrt.getch()
            return None
        try:
            return ch.decode("utf-8", "ignore").lower()
        except Exception:
            return None
    except Exception:
        return None


def request_abort(reason="status-console"):
    """写中止哨兵：exec_tools 的等待循环轮询到即杀进程树。"""
    try:
        with open(ABORT_SENTINEL, "w", encoding="utf-8") as f:
            f.write(reason)
        return True
    except OSError:
        return False


def handle_key(key, out=print):
    """处理按键。返回 'quit' 或 None。"""
    if key is None:
        return None
    if key in ("k", "\x1b"):
        if request_abort():
            out("  ⛔ 已请求中止正在运行的程序 [abort requested]")
        else:
            out("  ⚠️ 写中止哨兵失败 [failed to write abort signal]")
    elif key == "c":
        clear_screen()
        out(render_header(), flush=True)
    elif key in ("q",):
        return "quit"
    return None


# ============ 主循环 ============
def main():
    ensure_dirs()
    set_title(WINDOW_TITLE)
    main_pid = parse_arg(sys.argv[1:], "--main-pid")
    global _MAIN_PID
    try:
        _MAIN_PID = int(main_pid) if main_pid else None
    except (TypeError, ValueError):
        _MAIN_PID = None
    dead_streak = 0
    last_heartbeat = 0.0

    print(render_header(), flush=True)

    # ---- 起始位置：默认只看「启动之后」的新状态，**不回放历史** ----
    #   旧实现在日志小于 PRIME_MAX 时从 0 开始读，于是每次开状态台都会把
    #   上一次会话的过程信息整段重放（用户反馈：「每次都加载出上次的内容」）。
    #   现在默认跟到文件末尾；确实想回看，用 --tail N 显式指定行数。
    try:
        size0 = os.path.getsize(STATUS_LOG)
    except OSError:
        size0 = 0
    tail_n = _resolve_tail_arg(sys.argv[1:])
    if tail_n > 0:
        offset = tail_start_offset(STATUS_LOG, tail_n)
        print("  （按 --tail %d 回看最近 %d 行，已跳过更早的 %d 字节）\n"
              % (tail_n, tail_n, offset), flush=True)
    else:
        offset = compute_start_offset(size0)
        if size0:
            print("  （已跳过历史 %d 行 / %d 字节，只显示新状态；"
                  "想回看用 --tail 20）\n" % (count_lines(STATUS_LOG), size0),
                  flush=True)

    while True:
        try:
            now = time.time()
            if now - last_heartbeat > HEARTBEAT_SEC:
                touch(ONLINE_PATH)
                last_heartbeat = now

            # ---- 增量 tail 状态流 ----
            try:
                size = os.path.getsize(STATUS_LOG)
            except OSError:
                size = None
            if size is not None:
                if size < offset:
                    offset = 0          # 文件被截断（主程序重启）→ 从头跟
                if size > offset:
                    try:
                        with open(STATUS_LOG, "r", encoding="utf-8",
                                  errors="replace") as f:
                            f.seek(offset)
                            chunk = f.read()
                            offset = f.tell()
                        for ln in chunk.splitlines():
                            if ln.strip():
                                print(format_line(ln), flush=True)
                    except OSError:
                        pass

            # ---- [QUIT-CLEAN v1] 主程序要求全体退出 ----
            if _shutdown_requested():
                print("\n  🛑 收到退出信号，状态台关闭 [shutdown signal]\n")
                return 0

            # ---- 按键 ----
            if handle_key(read_key()) == "quit":
                print("\n  👋 状态台退出 [console exit]\n")
                return 0

            # ---- 主程序退出检测 ----
            #   优先用主程序自己写的 main.pid（venv 转发壳会让 argv 的 PID 不是真身）
            mp = _effective_main_pid() or main_pid
            if mp:
                if pid_alive(mp) is False:
                    dead_streak += 1
                    if dead_streak >= 3:
                        print("\n  🛑 主程序已退出，状态台自动关闭 "
                              "[main program exited, closing]\n")
                        time.sleep(1.2)
                        return 0
                else:
                    dead_streak = 0

            time.sleep(POLL_SEC)

        except KeyboardInterrupt:
            print("\n  👋 状态台退出 [console exit]\n")
            return 0
        except Exception:
            try:
                print("\n  ⚠️ 状态台内部异常（已忽略，继续运行）：")
                traceback.print_exc()
                time.sleep(0.5)
            except Exception:
                pass
    return 0


# ============ 自测（纯逻辑）============
def _e2e_probe(extra_args=(), seconds=1.8):
    """真起一个状态台子进程，抓它启动瞬间的输出。

    用于验证「默认不回放历史」这条行为 —— 纯逻辑测试看不出文件读写的真相。

    ★ 顺带把 console.online 的心跳复原：子进程会刷新它，而主程序是**靠心跳新鲜度**
      判断状态台在不在线的；留着新鲜心跳会导致主程序把状态写进日志却不在主窗口显示
      （等于短暂丢信息）。所以探针跑完要把它恢复原样。
    """
    import subprocess
    had = os.path.exists(ONLINE_PATH)
    old_mtime = None
    if had:
        try:
            old_mtime = os.path.getmtime(ONLINE_PATH)
        except OSError:
            old_mtime = None
    try:
        p = subprocess.run(
            [sys.executable, os.path.abspath(__file__), "--main-pid", str(os.getpid())]
            + list(extra_args),
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            timeout=seconds, cwd=HERE)
        out = p.stdout or b""
    except subprocess.TimeoutExpired as e:
        out = e.stdout or b""          # 超时是**预期**的（它本来就是个常驻窗口）
    except Exception:
        out = b""
    # ---- 复原心跳文件 ----
    try:
        if not had:
            if os.path.exists(ONLINE_PATH):
                os.remove(ONLINE_PATH)
        elif old_mtime is not None:
            os.utime(ONLINE_PATH, (old_mtime, old_mtime))
    except OSError:
        pass
    return (out or b"").decode("utf-8", "replace")


def _last_status_line():
    """status.log 里最后一条非空状态（用于校验 --tail 真的回看了它）。"""
    try:
        with open(STATUS_LOG, "r", encoding="utf-8", errors="replace") as f:
            lines = [ln for ln in f.read().splitlines() if ln.strip()]
        return lines[-1].strip() if lines else ""
    except OSError:
        return ""


def _selftest():
    ok = True
    problems = []

    def check(name, cond, extra=""):
        nonlocal ok
        if not cond:
            ok = False
            problems.append(name)
        print("   %-42s %s %s" % (name, "OK" if cond else "FAIL", extra))

    print("── 渲染 ──")
    h = render_header()
    check("标题正确", "肥鱼状态台" in h)
    check("说明报批不在本窗口", "报批申请仍在主窗口" in h)
    check("含按键说明", "k 或 ESC" in h and "c = 清屏" in h and "q = 退出" in h)
    check("说明默认不回放历史", "不回放历史" in h and "--tail" in h)

    print("── 行格式化 ──")
    l1 = format_line("  ✅ 双人核验 → approve：理由")
    check("加时间戳", l1.startswith("[") and "]" in l1)
    check("去主程序缩进", "  ✅" not in l1 and "✅" in l1)
    check("保留内容", "approve" in l1)

    print("── 起始偏移（默认不回放历史）──")
    check("空文件 → 0", compute_start_offset(0) == 0)
    check("小文件 → 直接到末尾（★ 旧实现在这里回放全部历史）",
          compute_start_offset(1000) == 1000, str(compute_start_offset(1000)))
    check("大文件 → 也到末尾", compute_start_offset(200 * 1024) == 200 * 1024)
    check("显式要求回看才往前退",
          compute_start_offset(200 * 1024, back=PRIME_MAX) == 200 * 1024 - PRIME_MAX)
    check("回看窗不越界", compute_start_offset(100, back=PRIME_MAX) == 0)

    print("── --tail 行数解析 ──")
    check("--tail 20", _resolve_tail_arg(["--tail", "20"]) == 20)
    check("--tail=20", _resolve_tail_arg(["--tail=20"]) == 20)
    check("--tail=abc → 0", _resolve_tail_arg(["--tail=abc"]) == 0)
    check("缺省 → 0（不回看）", _resolve_tail_arg([]) == 0)
    check("负数 → 0", _resolve_tail_arg(["--tail", "-5"]) == 0)

    print("── tail_start_offset / count_lines（真文件）──")
    import tempfile
    tf = os.path.join(tempfile.gettempdir(), "fatfish_tail_selftest.log")
    try:
        with open(tf, "w", encoding="utf-8") as f:
            for i in range(1, 11):
                f.write("line%d\n" % i)
        with open(tf, "r", encoding="utf-8") as f:
            f.seek(tail_start_offset(tf, 3))
            got3 = f.read()
        check("回看 3 行 = line8/9/10", got3 == "line8\nline9\nline10\n", repr(got3))
        with open(tf, "r", encoding="utf-8") as f:
            f.seek(tail_start_offset(tf, 1))
            got1 = f.read()
        check("回看 1 行 = line10", got1 == "line10\n", repr(got1))
        check("回看超过总行数 → 从头", tail_start_offset(tf, 99) == 0)
        check("回看 0 行 → 末尾", tail_start_offset(tf, 0) == os.path.getsize(tf))
        check("count_lines = 10", count_lines(tf) == 10, str(count_lines(tf)))
        check("不存在的文件不炸", tail_start_offset(tf + ".nope", 3) == 0
              and count_lines(tf + ".nope") == 0)
    finally:
        try:
            os.remove(tf)
        except OSError:
            pass

    print("── 参数解析 ──")
    check("--main-pid 123", parse_arg(["--main-pid", "123"], "--main-pid") == "123")
    check("--main-pid=456", parse_arg(["--main-pid=456"], "--main-pid") == "456")
    check("缺参数返回 None", parse_arg([], "--main-pid") is None)

    print("── pid_alive ──")
    check("本进程存活", pid_alive(os.getpid()) is True)
    check("不存在的 PID", pid_alive(999999) is False)
    check("空参数返回 None", pid_alive(None) is None)

    print("── 按键处理（不真的写哨兵）──")
    saved = globals()["request_abort"]
    hits = []
    globals()["request_abort"] = lambda reason="": (hits.append(reason), True)[1]
    out = []
    try:
        check("无关按键返回 None", handle_key("z", out.append) is None and not hits)
        handle_key("k", out.append)
        check("k 触发中止", len(hits) == 1)
        handle_key("\x1b", out.append)
        check("ESC 触发中止", len(hits) == 2)
        check("q 返回 quit", handle_key("q", out.append) == "quit")
    finally:
        globals()["request_abort"] = saved
    hits.clear()

    print("── 端到端：真起一次状态台 ──")
    if not (os.path.exists(STATUS_LOG) and os.path.getsize(STATUS_LOG) > 0):
        print("   ⏭ status.log 为空/不存在，跳过（无法验证回放行为）")
    else:
        out = _e2e_probe()
        check("启动横幅在", "肥鱼状态台" in out, out[:120])
        check("★ 默认不回放历史（明确提示已跳过）", "已跳过历史" in out, out[-160:])
        # `>` 是状态台给每条状态加的时间戳前缀，用它判断「没有任何旧状态被打印」
        hist_lines = [ln for ln in out.splitlines() if ln.startswith("[")]
        check("★ 输出里没有任何旧状态行", not hist_lines, repr(hist_lines[:3]))
        last = _last_status_line()
        if last:
            out2 = _e2e_probe(["--tail", "3"])
            core = last[:40]
            check("--tail 3 确实回看了最后一条状态", core in out2, repr(core))
            check("--tail 模式也给了提示", "回看" in out2, out2[-160:])
        else:
            print("   ⏭ status.log 无内容行，跳过 --tail 校验")

    print("\n%s" % ("status_console 自测全部通过 ✅" if ok
                    else "存在失败 ❌：%s" % problems))
    return 0 if ok else 1


if __name__ == "__main__":
    if "--selftest" in sys.argv:
        sys.exit(_selftest())
    try:
        sys.exit(main())
    except Exception:
        traceback.print_exc()
        try:
            input("  出错了，按回车关闭…")
        except Exception:
            pass
        sys.exit(1)
