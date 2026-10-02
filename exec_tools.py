# -*- coding: utf-8 -*-
"""
exec_tools.py —— 给肥鱼加装「运行 CMD 命令」和「跑 Python 代码」的引擎。

设计要点：
1. 所有命令默认在工作台根目录下执行（cwd），可用 cwd 参数指定子目录。
2. 一律带超时保护，防止卡死主程序。
3. 输出长度截断，避免撑爆上下文。
4. 返回 (ok, text)，与 workspace 其他工具保持一致。
"""

import os
import sys
import time
import json
import random
import subprocess
import tempfile
import threading
from datetime import datetime

# ============ 可调参数（已整体放宽）============
DEFAULT_TIMEOUT = 120         # 秒（原 30）
MAX_TIMEOUT = 1800            # 秒，硬上限（原 300）
MAX_OUTPUT_CHARS = 200_000    # 单次返回给 AI 的输出字符上限（原 20000）

# 子程序输出落盘目录（供监控器 fatfish_watcher.py 实时 tail）
# 结构：logs/YYYY/MM/DD/exec_HHMMSS_<pid>.out
EXEC_OUTPUT_ROOT = "logs"

# Windows 下隐藏黑框
_CREATE_NO_WINDOW = 0x08000000 if sys.platform == "win32" else 0


# ============ 用户中止（跑程序时手动打断）============
#   两条触发路径（2026-09-23 新增）：
#     A) 主窗口按 Ctrl+C —— 本模块的等待循环捕获 KeyboardInterrupt，
#        只杀这一个程序，**不打断整轮对话**（这是本次语义改造的重点）。
#     C) 监控器窗口（fatfish_watcher.py）按 K / ESC —— 写哨兵文件，
#        本模块轮询到即中止。
#   哨兵放在「本模块所在目录」，与 fatfish_watcher.py 的 __file__ 同目录，
#   两边无需传参即可对齐路径。
ABORT_SENTINEL = os.path.join(
    os.path.dirname(os.path.abspath(__file__)), ".fatfish_abort.signal")
ABORT_POLL = 0.2          # 等待循环的轮询间隔（秒）：越短响应越快，代价是空转
LAST_ABORTED = False      # 最近一次执行是否被用户中止（供主程序区分「中止」与「失败」）


def _kill_tree(pid, timeout=15):
    """杀掉以 pid 为根的**整棵进程树**（含孙进程）。返回是否成功。

    为什么必须杀树：Windows 下执行的是 cmd /c <command>，
    proc.kill() 只会杀掉 cmd.exe；它启动的 python.exe 会变成孤儿继续跑
    （实测抓到过一只独自狂奔 19 小时的 factor_turnover.py）。
    """
    if not pid:
        return False
    if sys.platform == "win32":
        try:
            r = subprocess.run(["taskkill", "/F", "/T", "/PID", str(pid)],
                               capture_output=True, timeout=timeout,
                               creationflags=_CREATE_NO_WINDOW)
            return r.returncode == 0
        except BaseException:
            # 连 KeyboardInterrupt 也吞掉：收拾现场必须完成，不能让中断再往上冒
            return False
    try:
        import signal
        os.killpg(os.getpgid(pid), signal.SIGKILL)
        return True
    except BaseException:
        return False


def request_abort(reason=""):
    """请求中止当前正在执行的子进程（写哨兵文件）。供 watcher / 主程序调用。"""
    try:
        d = os.path.dirname(ABORT_SENTINEL)
        if d:
            os.makedirs(d, exist_ok=True)
        with open(ABORT_SENTINEL, "w", encoding="utf-8") as f:
            f.write(reason or "abort")
        return True
    except OSError:
        return False


def abort_requested():
    """是否有人请求中止（哨兵文件存在）。"""
    try:
        return os.path.isfile(ABORT_SENTINEL)
    except OSError:
        return False


def clear_abort():
    """清除中止哨兵（避免上一轮残留的信号误伤下一次执行）。"""
    try:
        if os.path.isfile(ABORT_SENTINEL):
            os.remove(ABORT_SENTINEL)
    except OSError:
        pass


# ============ 执行心跳（等待动画用：看得出「在跑」还是「卡死」）============
#   原理：读当前输出落盘文件的大小与修改时间 ——
#   输出还在长 = 活着；长时间不涨 = 可能卡住。
CURRENT_OUT = None        # 当前正在写的输出文件（同步执行期间由 _run_with_tee 设置）
_HB = {"path": None, "size": 0, "last_change": 0.0}


def _fmt_bytes(n):
    """人性化字节数：1234B / 12.3KB / 4.5MB / 1.2GB。"""
    n = float(n or 0)
    for unit in ("B", "KB", "MB"):
        if n < 1024:
            return ("%d%s" % (n, unit)) if unit == "B" else ("%.1f%s" % (n, unit))
        n /= 1024.0
    return "%.2fGB" % n


def _fmt_dur(sec):
    """紧凑时长：45s / 12m / 3h05m。"""
    sec = max(0, int(sec or 0))
    if sec < 60:
        return "%ds" % sec
    if sec < 3600:
        return "%dm" % (sec // 60)
    return "%dh%02dm" % (sec // 3600, (sec % 3600) // 60)


def progress_hint(path=None):
    """返回一行「进度心跳」短文本，供等待动画显示；无信息则返回空串。

    形如 "↑12.3MB"（健康）或 "⚠ 8m 无输出"（可能卡住）。
    """
    p = path or CURRENT_OUT
    if not p:
        return ""
    try:
        st = os.stat(p)
    except OSError:
        return ""
    now = time.time()
    if _HB["path"] != p:
        _HB.update({"path": p, "size": st.st_size, "last_change": now})
    if st.st_size != _HB["size"]:
        _HB["size"] = st.st_size
        _HB["last_change"] = now
    quiet = now - _HB["last_change"]
    if quiet >= 60:
        return "⚠ %s 无输出" % _fmt_dur(quiet)
    return "↑%s" % _fmt_bytes(st.st_size)


try:
    from common import ts as _ts
except ImportError:               # common.py 缺失时退回本地实现，保持自足
    def _ts():
        return datetime.now().strftime("%Y-%m-%d %H:%M:%S")


def _truncate(text):
    """输出过长则截断，保留头尾。"""
    if text is None:
        return ""
    if len(text) <= MAX_OUTPUT_CHARS:
        return text
    half = MAX_OUTPUT_CHARS // 2
    return (
        text[:half]
        + f"\n\n...（输出过长，已截断，原长 {len(text)} 字符）...\n\n"
        + text[-half:]
    )


def _resolve_cwd(workspace_root, cwd):
    """把 cwd 解析到工作台内，越界则拒绝。"""
    if not cwd:
        return workspace_root, ""
    target = os.path.abspath(os.path.join(workspace_root, cwd))
    root = workspace_root.rstrip(os.sep) + os.sep
    if target != workspace_root and not target.startswith(root):
        return None, f"cwd 越界：{cwd}（工作台根：{workspace_root}）"
    if not os.path.isdir(target):
        return None, f"cwd 不是目录或不存在：{cwd}"
    return target, ""


def _clean_env():
    """构造一个干净的子进程环境，强制 UTF-8 输出。"""
    env = os.environ.copy()
    env["PYTHONIOENCODING"] = "utf-8"
    env["PYTHONUTF8"] = "1"
    # 避免子进程继承某些影响输出的变量
    env.pop("PYTHONSTARTUP", None)
    return env


_OUT_SEQ = 0


def _read_chunk(stream, n=4096):
    """从管道读一段「有多少拿多少」的字节。

    ★ 必须用 read1() 而不是 read(n)：read(n) 会**阻塞到读满 n 字节或 EOF**，
      小输出程序因此要等进程结束才落盘，「实时输出文件」就名不副实了。
      read1() 只要有数据就返回；退化时用 os.read(fileno)。
    """
    try:
        r1 = getattr(stream, "read1", None)
        if r1 is not None:
            return r1(n)
    except Exception:
        pass
    try:
        return os.read(stream.fileno(), n)
    except Exception:
        return stream.read(n)


def _normalize_nl(text):
    """把 Windows 的 \\r\\n / 裸 \\r 统一成 \\n，避免文件里出现双换行。"""
    return text.replace("\r\n", "\n").replace("\r", "\n")


def _exec_output_path(workspace_root, tag, uniq=""):
    """为一次执行分配一个落盘文件：logs/YYYY/MM/DD/exec_HHMMSS_<pid>.out。

    监控器 fatfish_watcher.py 会实时 tail 这个目录下新出现的 exec_*.out，
    从而在独立窗口里滚动显示「程序到底跑出来些啥」。
    """
    global _OUT_SEQ
    now = datetime.now()
    d = os.path.join(workspace_root, EXEC_OUTPUT_ROOT,
                     f"{now:%Y}", f"{now:%m}", f"{now:%d}")
    try:
        os.makedirs(d, exist_ok=True)
    except OSError:
        return None
    # 加自增序号：后台任务可能同秒并发提交，只用 HHMMSS 会互相覆盖。
    _OUT_SEQ += 1
    name = f"exec_{now:%H%M%S}_{os.getpid()}_{tag}{uniq}_{_OUT_SEQ}.out"
    return os.path.join(d, name)


def _run_with_tee(cmd_args, work_dir, timeout, output_root=None):
    """执行子进程，边读边把 stdout/stderr 实时写入落盘文件。

    返回 (returncode, out_text, err_text, out_path, timed_out)。
    - out_text / err_text：解码后的完整输出（返回给 AI，行为与原来一致）
    - out_path：落盘文件路径（供监控器 tail），失败则为 None
    - timed_out：是否超时

    output_root：输出文件落盘的根目录（应为工作台根，保证监控器能扫到）。
                 为空时退回 work_dir。
    """
    global LAST_ABORTED, CURRENT_OUT
    LAST_ABORTED = False
    clear_abort()                 # 清掉可能残留的中止哨兵
    out_path = _exec_output_path(output_root or work_dir, "run")
    CURRENT_OUT = out_path        # 供 progress_hint() 读心跳
    fh = None
    if out_path:
        try:
            fh = open(out_path, "w", encoding="utf-8", errors="replace")
        except OSError:
            fh = None
            out_path = None

    def _emit(chunk, is_err):
        """把一段字节同时喂给落盘文件。"""
        if fh is None or not chunk:
            return
        try:
            text = _decode(chunk)
            if is_err:
                text = "[stderr] " + text
            fh.write(_normalize_nl(text))
            fh.flush()
        except Exception:
            pass

    try:
        proc = subprocess.Popen(
            cmd_args,
            cwd=work_dir,
            env=_clean_env(),
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            creationflags=_CREATE_NO_WINDOW,
        )
    except FileNotFoundError as e:
        if fh:
            fh.write(f"[exec_tools] 找不到可执行程序：{e}\n")
            fh.close()
        return None, "", f"找不到可执行程序：{e}", out_path, False, False
    except Exception as e:
        if fh:
            fh.write(f"[exec_tools] 启动异常：{e}\n")
            fh.close()
        return None, "", f"启动异常：{e}", out_path, False, False

    out_chunks = []
    err_chunks = []
    timed_out = False

    # 用线程分别读 stdout / stderr，边读边落盘，避免管道写满阻塞
    import threading

    def _pump(stream, is_err, sink):
        try:
            while True:
                chunk = _read_chunk(stream)      # read1：有多少拿多少，实时落盘
                if not chunk:
                    break
                _emit(chunk, is_err)
                sink.append(chunk)
        except Exception:
            pass
        finally:
            try:
                stream.close()
            except Exception:
                pass

    t_out = threading.Thread(target=_pump,
                             args=(proc.stdout, False, out_chunks), daemon=True)
    t_err = threading.Thread(target=_pump,
                             args=(proc.stderr, True, err_chunks), daemon=True)
    t_out.start()
    t_err.start()

    # ---- 等待子进程结束：分段轮询，便于「超时 / Ctrl+C / 外部中止」三种打断 ----
    #   用 0.2s 小片轮询而非一次性 proc.wait(timeout=timeout)，
    #   这样每种中断都能在 ~0.2s 内被响应。
    aborted = False
    _wait_t0 = time.time()
    while True:
        try:
            proc.wait(timeout=ABORT_POLL)
            break
        except subprocess.TimeoutExpired:
            pass
        except KeyboardInterrupt:
            # 路径 A：主窗口 Ctrl+C → 只中止这个程序，不打断整轮对话
            aborted = True
            _kill_tree(proc.pid)
            break
        if abort_requested():
            # 路径 C：监控器窗口按 K / ESC
            aborted = True
            _kill_tree(proc.pid)
            break
        if time.time() - _wait_t0 > timeout:
            timed_out = True
            _kill_tree(proc.pid)
            break

    try:
        proc.wait(timeout=5)      # 收尸（若已杀干净则立即返回）
    except Exception:
        pass

    t_out.join(timeout=5)
    t_err.join(timeout=5)
    if t_out.is_alive() or t_err.is_alive():
        # 泵线程仍卡在 read()（孤儿孙进程握着管道写端）→ 硬关管道逼它退出，
        # 否则线程与管道句柄会永久泄漏。
        for _s in (proc.stdout, proc.stderr):
            try:
                if _s:
                    _s.close()
            except Exception:
                pass
        t_out.join(timeout=2)
        t_err.join(timeout=2)

    if aborted:
        LAST_ABORTED = True
        clear_abort()
        log_abort = True
    else:
        log_abort = False

    if fh:
        try:
            if log_abort:
                fh.write("\n[exec_tools] ⛔ 已被用户中止（Ctrl+C / 监控器按 K），"
                         "进程树已回收\n")
            if timed_out:
                fh.write(f"\n[exec_tools] 超时（>{timeout}s）已被终止\n")
            fh.write(f"\n[exec_tools] 退出码：{proc.returncode}\n")
            fh.close()
        except Exception:
            pass

    out_text = _decode(b"".join(out_chunks))
    err_text = _decode(b"".join(err_chunks))
    CURRENT_OUT = None            # 心跳停止
    return proc.returncode, out_text, err_text, out_path, timed_out, aborted


def run_cmd(command, workspace_root, cwd="", timeout=DEFAULT_TIMEOUT):
    """
    执行一条 CMD / Shell 命令。

    参数：
        command        : 要执行的命令字符串
        workspace_root : 工作台根目录（作为默认工作目录）
        cwd            : 相对工作台的子目录，可选
        timeout        : 超时秒数

    返回 (ok, text)
    """
    if not command or not command.strip():
        return False, "命令为空"

    timeout = max(1, min(int(timeout or DEFAULT_TIMEOUT), MAX_TIMEOUT))

    work_dir, err = _resolve_cwd(workspace_root, cwd)
    if work_dir is None:
        return False, err

    # Windows 用 cmd /c，其他平台用 sh -c
    if sys.platform == "win32":
        shell_args = ["cmd", "/c", command]
    else:
        shell_args = ["/bin/sh", "-c", command]

    rc, out, errout, out_path, timed_out, aborted = _run_with_tee(
        shell_args, work_dir, timeout, output_root=workspace_root)

    if rc is None:
        # 启动阶段就失败（找不到程序 / 异常）
        return False, errout

    if aborted:
        return False, (f"⛔ 已被用户中止（Ctrl+C 或监控器按 K），"
                       f"进程树已回收：{command}")

    if timed_out:
        return False, f"命令超时（>{timeout}s）已被终止：{command}"

    parts = [f"$ {command}", f"（工作目录：{work_dir}）", f"退出码：{rc}"]
    if out_path:
        parts.append(f"（实时输出文件：{out_path}）")
    if out:
        parts.append("--- stdout ---\n" + out)
    if errout:
        parts.append("--- stderr ---\n" + errout)
    if not out and not errout:
        parts.append("（无输出）")

    text = "\n".join(parts)
    ok = rc == 0
    return ok, _truncate(text)


def run_python(code, workspace_root, cwd="", timeout=DEFAULT_TIMEOUT, filename=None):
    """
    运行一段 Python 代码。

    做法：把代码写进工作台下的临时 .py 文件，再用当前解释器执行，
    这样 traceback 里能看到真实文件名，方便调试。

    返回 (ok, text)
    """
    if not code or not code.strip():
        return False, "代码为空"

    timeout = max(1, min(int(timeout or DEFAULT_TIMEOUT), MAX_TIMEOUT))

    work_dir, err = _resolve_cwd(workspace_root, cwd)
    if work_dir is None:
        return False, err

    # 临时脚本放在工作台内的 .fatfish_tmp 目录，跑完删除
    tmp_dir = os.path.join(work_dir, ".fatfish_tmp")
    try:
        os.makedirs(tmp_dir, exist_ok=True)
    except OSError as e:
        return False, f"无法创建临时目录：{e}"

    if not filename:
        filename = f"snippet_{datetime.now():%H%M%S_%f}.py"
    if not filename.endswith(".py"):
        filename += ".py"
    script_path = os.path.join(tmp_dir, filename)

    try:
        with open(script_path, "w", encoding="utf-8") as f:
            f.write(code)
    except OSError as e:
        return False, f"写入临时脚本失败：{e}"

    rc, out, errout, out_path, timed_out, aborted = _run_with_tee(
        [sys.executable, script_path], work_dir, timeout, output_root=workspace_root)

    if rc is None:
        _cleanup(script_path)
        return False, errout

    if aborted:
        _cleanup(script_path)
        return False, "⛔ 已被用户中止（Ctrl+C 或监控器按 K），进程树已回收"

    if timed_out:
        _cleanup(script_path)
        return False, f"Python 代码超时（>{timeout}s）已被终止"

    parts = [f"（解释器：{sys.executable}）", f"退出码：{rc}"]
    if out_path:
        parts.append(f"（实时输出文件：{out_path}）")
    if out:
        parts.append("--- stdout ---\n" + out)
    if errout:
        parts.append("--- stderr ---\n" + errout)
    if not out and not errout:
        parts.append("（无输出）")

    text = "\n".join(parts)
    ok = rc == 0
    _cleanup(script_path)
    return ok, _truncate(text)


def _decode(raw):
    """把子进程字节输出解码为字符串，尽量兼容各种编码。"""
    if raw is None:
        return ""
    if isinstance(raw, str):
        return raw
    for enc in ("utf-8", "gbk", "cp936", "big5", "latin-1"):
        try:
            return raw.decode(enc)
        except UnicodeDecodeError:
            continue
    return raw.decode("utf-8", errors="replace")


def _cleanup(path):
    """删除临时脚本，静默失败。"""
    try:
        if os.path.exists(path):
            os.remove(path)
    except OSError:
        pass


# ============ 后台任务（长任务：提交即返回，跑完主动播报）============
#   动机：同步执行默认 120s、硬上限 1800s ——
#         ① 跑不了「几小时」的活；② 同步期间主窗口完全阻塞（既不能对话也不能查进度）。
#   做法：提交 → 立即返回 job_id → ws_bg_list / ws_bg_tail 查进度 →
#         ws_bg_kill 中止；跑完由主程序主动向用户播报（不用你一直干等）。
#   输出仍写 logs/YYYY/MM/DD/exec_*.out，因此监控器窗口照旧实时滚动。

BG_POLL = 0.5                  # 后台等待轮询（秒）
BG_DEFAULT_TIMEOUT = 0         # 0 = 不限时（长任务默认让它一直跑）
BG_TAIL_DEFAULT = 8000         # bg_tail 单次返回字符上限
_BG_DIRNAME = ".fatfish_tmp"
_BG_KEEP = 100                 # 登记表最多保留多少个任务

_BG_JOBS = {}                  # jid -> dict
_BG_LOCK = threading.Lock()
_BG_FINISHED = []              # 已结束、尚未被主程序取走通知的 jid


def _bg_id():
    return "job_%s_%03d" % (datetime.now().strftime("%m%d_%H%M%S"),
                            random.randint(0, 999))


def _bg_snapshot(j):
    return {k: j.get(k) for k in ("id", "kind", "desc", "status", "pid",
                                  "started", "ended", "returncode",
                                  "out_path", "timeout")}


def _bg_persist(workspace_root):
    """把任务登记表写盘（尽力而为；失败不影响任务本身，供跨会话查看）。"""
    try:
        d = os.path.join(workspace_root or ".", _BG_DIRNAME)
        os.makedirs(d, exist_ok=True)
        with _BG_LOCK:
            snap = [_bg_snapshot(j)
                    for j in list(_BG_JOBS.values())[-_BG_KEEP:]]
        with open(os.path.join(d, "jobs.json"), "w", encoding="utf-8") as f:
            json.dump(snap, f, ensure_ascii=False, indent=1)
    except Exception:
        pass


def _bg_finish(jid, status, returncode):
    """任务收尾：记状态、入通知队列、写盘。"""
    with _BG_LOCK:
        j = _BG_JOBS.get(jid)
        if not j:
            return
        j["status"] = status
        j["returncode"] = returncode
        j["ended"] = time.time()
        ws_root = j.get("workspace_root")
        _BG_FINISHED.append(jid)
    _bg_persist(ws_root)


def _bg_status_text(jid):
    """提交成功后回给 AI 的一段说明。"""
    with _BG_LOCK:
        j = dict(_BG_JOBS.get(jid) or {})
    return ("🚀 后台任务已提交 [job submitted]：%s\n"
            "  PID      ：%s\n"
            "  内容     ：%s\n"
            "  输出文件 ：%s\n"
            "  时限     ：%s\n"
            "  查进度   ：ws_bg_list() ／ ws_bg_tail(\"%s\")\n"
            "  中止     ：ws_bg_kill(\"%s\")\n"
            "  ★ 主窗口现在可以正常对话，不必干等；任务跑完会自动播报。"
            % (jid, j.get("pid"), (j.get("desc") or "")[:80],
               j.get("out_path") or "(未落盘)",
               ("不限时" if not j.get("timeout") else "%ds" % j["timeout"]),
               jid, jid))


def _bg_launch(cmd_args, kind, desc, workspace_root, work_dir, timeout,
               cleanup_path=None):
    """启动一个后台子进程并登记。返回 (jid, err)。"""
    out_path = _exec_output_path(workspace_root or work_dir, "bg",
                                 uniq="_%03d" % random.randint(0, 999))
    fh = None
    if out_path:
        try:
            fh = open(out_path, "w", encoding="utf-8", errors="replace")
        except OSError:
            out_path, fh = None, None

    try:
        proc = subprocess.Popen(
            cmd_args, cwd=work_dir, env=_clean_env(),
            stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            creationflags=_CREATE_NO_WINDOW)
    except Exception as e:
        if fh:
            try:
                fh.close()
            except Exception:
                pass
        return None, f"后台任务启动失败：{e}"

    jid = _bg_id()
    job = {"id": jid, "kind": kind, "desc": desc, "proc": proc, "pid": proc.pid,
           "out_path": out_path, "timeout": int(timeout or 0),
           "started": time.time(), "ended": None, "returncode": None,
           "status": "running", "kill_requested": False, "fh": fh,
           "workspace_root": workspace_root, "cleanup_path": cleanup_path}
    with _BG_LOCK:
        _BG_JOBS[jid] = job
    _bg_persist(workspace_root)

    def _pump(stream, is_err):
        """把子进程输出实时写进落盘文件（不留在内存，避免长任务吃爆内存）。"""
        try:
            while True:
                chunk = _read_chunk(stream)      # read1：实时可见，长任务关键
                if not chunk:
                    break
                _fh = job.get("fh")
                if _fh is not None:
                    try:
                        t = _decode(chunk)
                        if is_err:
                            t = "[stderr] " + t
                        _fh.write(_normalize_nl(t))
                        _fh.flush()
                    except Exception:
                        pass
        except Exception:
            pass
        finally:
            try:
                stream.close()
            except Exception:
                pass

    for _st, _is_err in ((proc.stdout, False), (proc.stderr, True)):
        threading.Thread(target=_pump, args=(_st, _is_err),
                         daemon=True).start()

    def _reaper():
        """守护线程：等进程结束 / 处理中止与超时，然后收尾通知。"""
        t0 = time.time()
        status = "done"
        while True:
            try:
                proc.wait(timeout=BG_POLL)
                status = "done" if proc.returncode == 0 else "failed"
                break
            except subprocess.TimeoutExpired:
                pass
            except Exception:
                status = "failed"
                break
            if job.get("kill_requested"):
                _kill_tree(proc.pid)
                try:
                    proc.wait(timeout=5)
                except Exception:
                    pass
                status = "killed"
                break
            _to = job.get("timeout") or 0
            if _to > 0 and time.time() - t0 > _to:
                _kill_tree(proc.pid)
                try:
                    proc.wait(timeout=5)
                except Exception:
                    pass
                status = "timeout"
                break

        _fh = job.get("fh")
        if _fh is not None:
            try:
                _fh.write("\n[exec_tools] 后台任务结束：%s（退出码 %s）\n"
                          % (status, proc.returncode))
                _fh.close()
            except Exception:
                pass
            job["fh"] = None
        if job.get("cleanup_path"):
            _cleanup(job["cleanup_path"])
        _bg_finish(jid, status, proc.returncode)

    threading.Thread(target=_reaper, daemon=True).start()
    return jid, ""


def bg_run_cmd(command, workspace_root, cwd="", timeout=BG_DEFAULT_TIMEOUT,
               desc=""):
    """提交一条后台 CMD/Shell 命令。返回 (ok, text)。"""
    if not command or not command.strip():
        return False, "命令为空"
    work_dir, err = _resolve_cwd(workspace_root, cwd)
    if work_dir is None:
        return False, err
    if sys.platform == "win32":
        shell_args = ["cmd", "/c", command]
    else:
        shell_args = ["/bin/sh", "-c", command]
    jid, err = _bg_launch(shell_args, "cmd", desc or command.strip()[:80],
                          workspace_root, work_dir, timeout)
    if not jid:
        return False, err
    return True, _bg_status_text(jid)


def bg_run_python(code, workspace_root, cwd="", timeout=BG_DEFAULT_TIMEOUT,
                  filename=None, desc=""):
    """提交一段后台 Python 代码。返回 (ok, text)。"""
    if not code or not code.strip():
        return False, "代码为空"
    work_dir, err = _resolve_cwd(workspace_root, cwd)
    if work_dir is None:
        return False, err
    tmp_dir = os.path.join(work_dir, _BG_DIRNAME)
    try:
        os.makedirs(tmp_dir, exist_ok=True)
    except OSError as e:
        return False, f"无法创建临时目录：{e}"
    if not filename:
        filename = "bg_%s.py" % datetime.now().strftime("%H%M%S_%f")
    if not filename.endswith(".py"):
        filename += ".py"
    script_path = os.path.join(tmp_dir, filename)
    try:
        with open(script_path, "w", encoding="utf-8") as f:
            f.write(code)
    except OSError as e:
        return False, f"写入临时脚本失败：{e}"
    jid, err = _bg_launch([sys.executable, script_path], "python",
                          desc or ("python %s" % filename),
                          workspace_root, work_dir, timeout,
                          cleanup_path=script_path)
    if not jid:
        return False, err
    return True, _bg_status_text(jid)


def bg_list(workspace_root=None):
    """列出所有后台任务。返回 (ok, text)。"""
    with _BG_LOCK:
        jobs = [_bg_snapshot(j) for j in _BG_JOBS.values()]
    if not jobs:
        return True, ("【后台任务】当前没有任务。\n"
                      "（用 ws_bg_cmd / ws_bg_python 提交长任务）")
    jobs.sort(key=lambda j: j.get("started") or 0)
    now = time.time()
    lines = ["【后台任务】共 %d 个：" % len(jobs)]
    for j in jobs:
        el = (j.get("ended") or now) - (j.get("started") or now)
        size = 0
        try:
            if j.get("out_path"):
                size = os.path.getsize(j["out_path"])
        except OSError:
            pass
        lines.append("  %-20s %-8s 已跑 %-7s 输出 %-9s %s"
                     % (j["id"], j.get("status"), _fmt_dur(el),
                        _fmt_bytes(size), (j.get("desc") or "")[:46]))
    run = [j for j in jobs if j.get("status") == "running"]
    if run:
        lines.append("  ↑ 仍在运行 %d 个；查输出用 ws_bg_tail(\"%s\")"
                     % (len(run), run[-1]["id"]))
    return True, "\n".join(lines)


def bg_tail(jid, offset=0, max_chars=BG_TAIL_DEFAULT):
    """增量读取后台任务输出。返回 (ok, text)，末尾会给出续读 offset。"""
    with _BG_LOCK:
        j = dict(_BG_JOBS.get(jid) or {})
    if not j:
        return False, "未知任务：%s（用 ws_bg_list 查看现有任务）" % jid
    p = j.get("out_path")
    if not p or not os.path.isfile(p):
        return True, "（%s 暂无输出文件）" % jid
    try:
        size = os.path.getsize(p)
    except OSError as e:
        return False, f"读取输出失败：{e}"
    off = max(0, int(offset or 0))
    if off > size:
        off = size
    try:
        with open(p, "r", encoding="utf-8", errors="replace") as f:
            f.seek(off)
            chunk = f.read(max(200, int(max_chars or BG_TAIL_DEFAULT)))
            new_off = f.tell()
    except OSError as e:
        return False, f"读取输出失败：{e}"
    head = ("【%s】状态=%s｜累计输出 %s｜本次 %d→%d 字节\n"
            % (jid, j.get("status"), _fmt_bytes(size), off, new_off))
    if not chunk:
        # 无新输出也给出 offset：让「续读」这件事在任何一次调用后都能继续，
        # AI 不必记住上一次的位置。
        return True, head + ("\n（无新输出；下次续读用 offset=%d）" % new_off)
    return True, head + chunk + ("\n（如未结束，可用 offset=%d 续读）" % new_off)


def bg_kill(jid, reason=""):
    """中止一个后台任务（杀整棵进程树）。返回 (ok, text)。"""
    with _BG_LOCK:
        j = _BG_JOBS.get(jid)
    if not j:
        return False, "未知任务：%s" % jid
    if j.get("status") != "running":
        return False, "任务 %s 已结束（状态：%s），无需中止" % (jid, j.get("status"))
    j["kill_requested"] = True
    _kill_tree(j.get("pid"))
    return True, ("⛔ 已中止后台任务 %s（进程树已回收）%s"
                  % (jid, ("｜原因：" + reason) if reason else ""))


def bg_output_tail(jid, max_chars=4000):
    """读任务输出的最后一段（供「跑完播报」用）。"""
    with _BG_LOCK:
        j = dict(_BG_JOBS.get(jid) or {})
    p = j.get("out_path")
    if not p or not os.path.isfile(p):
        return ""
    try:
        size = os.path.getsize(p)
        with open(p, "r", encoding="utf-8", errors="replace") as f:
            if size > max_chars:
                f.seek(size - max_chars)
            return f.read(max_chars)
    except OSError:
        return ""


def drain_finished():
    """取走「已结束但尚未通知」的任务快照（主程序据此决定何时开口播报）。"""
    with _BG_LOCK:
        ids = list(_BG_FINISHED)
        _BG_FINISHED.clear()
        out = [_bg_snapshot(_BG_JOBS[i]) for i in ids if i in _BG_JOBS]
    return out


def bg_running_ids():
    """仍在运行的后台任务 id 列表。"""
    with _BG_LOCK:
        return [j["id"] for j in _BG_JOBS.values()
                if j.get("status") == "running"]


def bg_progress_hint():
    """最新仍在运行的后台任务的心跳（供状态行/提示符显示）。"""
    with _BG_LOCK:
        run = [dict(j) for j in _BG_JOBS.values() if j.get("status") == "running"]
    if not run:
        return ""
    j = max(run, key=lambda x: x.get("started") or 0)
    return "%s %s" % (j["id"], progress_hint(j.get("out_path")) or "")
