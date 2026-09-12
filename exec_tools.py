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

# ============ 可调参数 ============
DEFAULT_TIMEOUT = 30          # 秒
MAX_TIMEOUT = 300             # 秒，硬上限
MAX_OUTPUT_CHARS = 20_000     # 单次返回给 AI 的输出字符上限

# Windows 下隐藏黑框
_CREATE_NO_WINDOW = 0x08000000 if sys.platform == "win32" else 0


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

    try:
        proc = subprocess.run(
            shell_args,
            cwd=work_dir,
            env=_clean_env(),
            capture_output=True,
            timeout=timeout,
            creationflags=_CREATE_NO_WINDOW,
        )
    except subprocess.TimeoutExpired:
        return False, f"命令超时（>{timeout}s）已被终止：{command}"
    except FileNotFoundError as e:
        return False, f"找不到可执行程序：{e}"
    except Exception as e:
        return False, f"命令执行异常：{e}"

    out = _decode(proc.stdout)
    errout = _decode(proc.stderr)

    parts = [f"$ {command}", f"（工作目录：{work_dir}）", f"退出码：{proc.returncode}"]
    if out:
        parts.append("--- stdout ---\n" + out)
    if errout:
        parts.append("--- stderr ---\n" + errout)
    if not out and not errout:
        parts.append("（无输出）")

    text = "\n".join(parts)
    ok = proc.returncode == 0
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

    try:
        proc = subprocess.run(
            [sys.executable, script_path],
            cwd=work_dir,
            env=_clean_env(),
            capture_output=True,
            timeout=timeout,
            creationflags=_CREATE_NO_WINDOW,
        )
    except subprocess.TimeoutExpired:
        _cleanup(script_path)
        return False, f"Python 代码超时（>{timeout}s）已被终止"
    except Exception as e:
        _cleanup(script_path)
        return False, f"Python 执行异常：{e}"

    out = _decode(proc.stdout)
    errout = _decode(proc.stderr)

    parts = [f"（解释器：{sys.executable}）", f"退出码：{proc.returncode}"]
    if out:
        parts.append("--- stdout ---\n" + out)
    if errout:
        parts.append("--- stderr ---\n" + errout)
    if not out and not errout:
        parts.append("（无输出）")

    text = "\n".join(parts)
    ok = proc.returncode == 0
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
