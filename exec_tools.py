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
import subprocess
import tempfile
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


def _exec_output_path(workspace_root, tag):
    """为一次执行分配一个落盘文件：logs/YYYY/MM/DD/exec_HHMMSS_<pid>.out。

    监控器 fatfish_watcher.py 会实时 tail 这个目录下新出现的 exec_*.out，
    从而在独立窗口里滚动显示「程序到底跑出来些啥」。
    """
    now = datetime.now()
    d = os.path.join(workspace_root, EXEC_OUTPUT_ROOT,
                     f"{now:%Y}", f"{now:%m}", f"{now:%d}")
    try:
        os.makedirs(d, exist_ok=True)
    except OSError:
        return None
    name = f"exec_{now:%H%M%S}_{os.getpid()}_{tag}.out"
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
    out_path = _exec_output_path(output_root or work_dir, "run")
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
                fh.write("[stderr] " + text)
            else:
                fh.write(text)
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
        return None, "", f"找不到可执行程序：{e}", out_path, False
    except Exception as e:
        if fh:
            fh.write(f"[exec_tools] 启动异常：{e}\n")
            fh.close()
        return None, "", f"启动异常：{e}", out_path, False

    out_chunks = []
    err_chunks = []
    timed_out = False

    # 用线程分别读 stdout / stderr，边读边落盘，避免管道写满阻塞
    import threading

    def _pump(stream, is_err, sink):
        try:
            while True:
                chunk = stream.read(4096)
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

    try:
        proc.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        timed_out = True
        try:
            proc.kill()
        except Exception:
            pass
        try:
            proc.wait(timeout=5)
        except Exception:
            pass

    t_out.join(timeout=5)
    t_err.join(timeout=5)

    if fh:
        try:
            if timed_out:
                fh.write(f"\n[exec_tools] 超时（>{timeout}s）已被终止\n")
            fh.write(f"\n[exec_tools] 退出码：{proc.returncode}\n")
            fh.close()
        except Exception:
            pass

    out_text = _decode(b"".join(out_chunks))
    err_text = _decode(b"".join(err_chunks))
    return proc.returncode, out_text, err_text, out_path, timed_out


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

    rc, out, errout, out_path, timed_out = _run_with_tee(
        shell_args, work_dir, timeout, output_root=workspace_root)

    if rc is None:
        # 启动阶段就失败（找不到程序 / 异常）
        return False, errout

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

    rc, out, errout, out_path, timed_out = _run_with_tee(
        [sys.executable, script_path], work_dir, timeout, output_root=workspace_root)

    if rc is None:
        _cleanup(script_path)
        return False, errout

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
