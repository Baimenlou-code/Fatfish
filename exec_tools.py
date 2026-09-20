
import os
import sys
import subprocess
import tempfile
from datetime import datetime

DEFAULT_TIMEOUT = 120
MAX_TIMEOUT = 1800
MAX_OUTPUT_CHARS = 200_000

EXEC_OUTPUT_ROOT = "logs"

_CREATE_NO_WINDOW = 0x08000000 if sys.platform == "win32" else 0

try:
    from common import ts as _ts
except ImportError:
    def _ts():
        return datetime.now().strftime("%Y-%m-%d %H:%M:%S")

def _truncate(text):
    if text is None:
        return ""
    if len(text) <= MAX_OUTPUT_CHARS:
        return text
    half = MAX_OUTPUT_CHARS // 2
    return (
        text[:half]
        + f"\n\n...(output too long, truncated; original length {len(text)} chars)...\n\n"
        + text[-half:]
    )

def _resolve_cwd(workspace_root, cwd):
    if not cwd:
        return workspace_root, ""
    target = os.path.abspath(os.path.join(workspace_root, cwd))
    root = workspace_root.rstrip(os.sep) + os.sep
    if target != workspace_root and not target.startswith(root):
        return None, f"cwd escapes the workspace: {cwd} (workspace root: {workspace_root})"
    if not os.path.isdir(target):
        return None, f"cwd is not a directory or does not exist: {cwd}"
    return target, ""

def _clean_env():
    env = os.environ.copy()
    env["PYTHONIOENCODING"] = "utf-8"
    env["PYTHONUTF8"] = "1"
    env.pop("PYTHONSTARTUP", None)
    return env

def _exec_output_path(workspace_root, tag):
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
    out_path = _exec_output_path(output_root or work_dir, "run")
    fh = None
    if out_path:
        try:
            fh = open(out_path, "w", encoding="utf-8", errors="replace")
        except OSError:
            fh = None
            out_path = None

    def _emit(chunk, is_err):
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
            fh.write(f"[exec_tools] executable not found: {e}\n")
            fh.close()
        return None, "", f"executable not found: {e}", out_path, False
    except Exception as e:
        if fh:
            fh.write(f"[exec_tools] launch error: {e}\n")
            fh.close()
        return None, "", f"launch error: {e}", out_path, False

    out_chunks = []
    err_chunks = []
    timed_out = False

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
                fh.write(f"\n[exec_tools] timed out (>{timeout}s) and was terminated\n")
            fh.write(f"\n[exec_tools] exit code: {proc.returncode}\n")
            fh.close()
        except Exception:
            pass

    out_text = _decode(b"".join(out_chunks))
    err_text = _decode(b"".join(err_chunks))
    return proc.returncode, out_text, err_text, out_path, timed_out

def run_cmd(command, workspace_root, cwd="", timeout=DEFAULT_TIMEOUT):
    if not command or not command.strip():
        return False, "empty command"

    timeout = max(1, min(int(timeout or DEFAULT_TIMEOUT), MAX_TIMEOUT))

    work_dir, err = _resolve_cwd(workspace_root, cwd)
    if work_dir is None:
        return False, err

    if sys.platform == "win32":
        shell_args = ["cmd", "/c", command]
    else:
        shell_args = ["/bin/sh", "-c", command]

    rc, out, errout, out_path, timed_out = _run_with_tee(
        shell_args, work_dir, timeout, output_root=workspace_root)

    if rc is None:
        return False, errout

    if timed_out:
        return False, f"command timed out (>{timeout}s) and was terminated: {command}"

    parts = [f"$ {command}", f"(working directory: {work_dir})", f"exit code: {rc}"]
    if out_path:
        parts.append(f"(live output file: {out_path})")
    if out:
        parts.append("--- stdout ---\n" + out)
    if errout:
        parts.append("--- stderr ---\n" + errout)
    if not out and not errout:
        parts.append("(no output)")

    text = "\n".join(parts)
    ok = rc == 0
    return ok, _truncate(text)

def run_python(code, workspace_root, cwd="", timeout=DEFAULT_TIMEOUT, filename=None):
    if not code or not code.strip():
        return False, "empty code"

    timeout = max(1, min(int(timeout or DEFAULT_TIMEOUT), MAX_TIMEOUT))

    work_dir, err = _resolve_cwd(workspace_root, cwd)
    if work_dir is None:
        return False, err

    tmp_dir = os.path.join(work_dir, ".fatfish_tmp")
    try:
        os.makedirs(tmp_dir, exist_ok=True)
    except OSError as e:
        return False, f"cannot create temp directory: {e}"

    if not filename:
        filename = f"snippet_{datetime.now():%H%M%S_%f}.py"
    if not filename.endswith(".py"):
        filename += ".py"
    script_path = os.path.join(tmp_dir, filename)

    try:
        with open(script_path, "w", encoding="utf-8") as f:
            f.write(code)
    except OSError as e:
        return False, f"failed to write temp script: {e}"

    rc, out, errout, out_path, timed_out = _run_with_tee(
        [sys.executable, script_path], work_dir, timeout, output_root=workspace_root)

    if rc is None:
        _cleanup(script_path)
        return False, errout

    if timed_out:
        _cleanup(script_path)
        return False, f"Python code timed out (>{timeout}s) and was terminated"

    parts = [f"(interpreter: {sys.executable})", f"exit code: {rc}"]
    if out_path:
        parts.append(f"(live output file: {out_path})")
    if out:
        parts.append("--- stdout ---\n" + out)
    if errout:
        parts.append("--- stderr ---\n" + errout)
    if not out and not errout:
        parts.append("(no output)")

    text = "\n".join(parts)
    ok = rc == 0
    _cleanup(script_path)
    return ok, _truncate(text)

def _decode(raw):
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
    try:
        if os.path.exists(path):
            os.remove(path)
    except OSError:
        pass
