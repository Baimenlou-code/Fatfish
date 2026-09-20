import os
import re
import logging
from datetime import datetime

import exec_tools

DEFAULT_WORKSPACE = os.getenv(
    "WORKSPACE_DIR",
    os.path.join(os.path.dirname(os.path.abspath(__file__)), "workspace"),
)

_WORKSPACE_DIR = None

def _init_default():
    global _WORKSPACE_DIR
    p = os.path.abspath(os.path.expanduser(os.path.expandvars(DEFAULT_WORKSPACE)))
    os.makedirs(p, exist_ok=True)
    _WORKSPACE_DIR = p

def get_workspace():
    if _WORKSPACE_DIR is None:
        _init_default()
    return _WORKSPACE_DIR

_PENDING_CD = None

def default_workspace():
    return os.path.abspath(
        os.path.expanduser(os.path.expandvars(DEFAULT_WORKSPACE))
    )

def is_inside_default(path):
    home = default_workspace()
    p = os.path.abspath(path)
    root = home.rstrip(os.sep) + os.sep
    return p == home or p.startswith(root)

def set_workspace(path):
    global _WORKSPACE_DIR, _PENDING_CD
    if not path:
        return False, "empty path"
    p = os.path.abspath(
        os.path.expanduser(os.path.expandvars(str(path).strip().strip("'\"`")))
    )
    if not os.path.exists(p):
        return False, f"path does not exist: {p}"
    if not os.path.isdir(p):
        return False, f"not a directory: {p}"

    if not is_inside_default(p):
        _PENDING_CD = {"path": p}
        return False, (
            f"[!] This operation would move the office outside the default "
            f"sandbox and needs user approval.\n"
            f"target: {p}\n"
            f"default sandbox: {default_workspace()}\n"
            f"Ask the user to confirm, then call ws_cd_approve."
        )

    _WORKSPACE_DIR = p
    _PENDING_CD = None
    clear_tickets()
    _log(f"[{_ts()}] workspace switched to: {p}")
    return True, f"workspace switched to: {p}"

def approve_pending_cd():
    global _WORKSPACE_DIR, _PENDING_CD
    if not _PENDING_CD:
        return False, "there is no switch request awaiting approval"
    p = _PENDING_CD["path"]
    if not os.path.isdir(p):
        _PENDING_CD = None
        return False, f"the target no longer exists or is not a directory: {p}"
    _WORKSPACE_DIR = p
    _PENDING_CD = None
    clear_tickets()
    _log(f"[{_ts()}] user approved; workspace switched to: {p}")
    return True, f"approved; workspace switched to: {p}"

def reset_workspace():
    _init_default()
    clear_tickets()
    return True, f"workspace reset to: {_WORKSPACE_DIR}"

def __getattr__(name):
    if name == "WORKSPACE_DIR":
        return get_workspace()
    raise AttributeError(name)

MAX_READ_BYTES = 5 * 1024 * 1024
MAX_READ_CHARS = 1_000_000

def _log(msg):
    logging.info(msg)

try:
    from common import ts as _ts
except ImportError:
    def _ts():
        return datetime.now().strftime("%Y-%m-%d %H:%M:%S")

_READ_TICKETS = {}

def _issue_ticket(path):
    try:
        st = os.stat(path)
        _READ_TICKETS[os.path.abspath(path)] = (st.st_mtime, st.st_size)
    except OSError:
        pass

def _check_ticket(path):
    ap = os.path.abspath(path)
    if not os.path.exists(ap):
        return True, ""
    tk = _READ_TICKETS.get(ap)
    if tk is None:
        return False, f"this file has not been read yet; ws_read it first: {_rel(ap)}"
    try:
        st = os.stat(ap)
    except OSError as e:
        return False, f"cannot read file status: {e}"
    if st.st_mtime != tk[0] or st.st_size != tk[1]:
        _READ_TICKETS.pop(ap, None)
        return False, f"the file changed externally, ticket expired; ws_read it again: {_rel(ap)}"
    return True, ""

def clear_tickets():
    _READ_TICKETS.clear()

BACKUP_DIRNAME = "_backup"

def _is_root_level_file(rel):
    if not rel:
        return False
    r = str(rel).strip().strip("'\"`").replace("\\", "/").strip("/")
    if not r:
        return False
    if "/" in r:
        return False
    if r == BACKUP_DIRNAME or r.startswith(BACKUP_DIRNAME + "/"):
        return False
    return True

def _backup_root_file(rel, p):
    if not _is_root_level_file(rel):
        return True, ""
    if not os.path.isfile(p):
        return True, ""
    ws = get_workspace()
    bkdir = os.path.join(ws, BACKUP_DIRNAME)
    try:
        os.makedirs(bkdir, exist_ok=True)
        ts = datetime.now().strftime("%Y%m%d_%H%M%S_%f")
        name = os.path.basename(p)
        dst = os.path.join(bkdir, f"{name}.{ts}.bak")
        with open(p, "rb") as src, open(dst, "wb") as out:
            out.write(src.read())
    except OSError as e:
        return False, f"backup failed (original could not be copied): {e}"
    _log(f"[{_ts()}] backed up original {name} -> {_rel(dst)}")
    return True, f"original backed up -> {_rel(dst)}"

def _safe_path(rel):
    ws = get_workspace()
    if rel is None:
        rel = ""
    rel = str(rel).strip().strip("'\"`").replace("\\", os.sep).replace("/", os.sep)
    if os.path.isabs(rel):
        target = os.path.abspath(rel)
    else:
        target = os.path.abspath(os.path.join(ws, rel))
    root = ws.rstrip(os.sep) + os.sep
    if target != ws and not target.startswith(root):
        raise PermissionError(f"path escapes the workspace: {rel} (workspace root: {ws})")
    return target

def _rel(path):
    try:
        return os.path.relpath(path, get_workspace())
    except ValueError:
        return path

SENSITIVE_NAME_GLOBS = (
    ".env", ".env.*", "*.env",
    "*.key", "*.pem", "*.pfx", "*.p12", "*.keystore", "*.jks",
    "id_rsa", "id_rsa.*", "id_ed25519", "id_ed25519.*", "id_dsa*",
    "*.credential*", "*.secret*", "*passwd*", "*password*",
    ".netrc", ".npmrc", ".pypirc", ".git-credentials",
    "credentials.json", "secrets.json", "service_account*.json",
)

_SENSITIVE_DIR_PREFIXES = ("_backup/.env", ".git/")

def is_sensitive_path(rel):
    if not rel:
        return False
    r = str(rel).strip().strip("'\"`").replace("\\", "/").lstrip("/")
    while r.startswith("./"):
        r = r[2:]
    if not r:
        return False
    low = r.lower()
    for pre in _SENSITIVE_DIR_PREFIXES:
        if low.startswith(pre):
            return True
    import fnmatch
    name = low.rsplit("/", 1)[-1]
    for g in SENSITIVE_NAME_GLOBS:
        if fnmatch.fnmatch(name, g) or fnmatch.fnmatch(low, g):
            return True
    return False

def sensitive_reason(rel):
    if is_sensitive_path(rel):
        return f"sensitive-file guard: {rel} matches the key/credential list and needs manual confirmation"
    return ""

def ws_list(path="", depth=3, max_entries=2000):
    try:
        p = _safe_path(path)
    except PermissionError as e:
        return False, str(e)
    if not os.path.isdir(p):
        return False, f"not a directory: {_rel(p)}"

    lines, count = [], 0
    base_depth = p.rstrip(os.sep).count(os.sep)

    for root, dirs, files in os.walk(p):
        dirs.sort()
        files.sort()
        cur_depth = root.rstrip(os.sep).count(os.sep) - base_depth
        if cur_depth >= depth:
            dirs[:] = []
        indent = "  " * cur_depth
        lines.append(f"{indent}{os.path.basename(root) or _rel(root)}/")
        for name in files:
            count += 1
            if count > max_entries:
                lines.append(f"{indent}  ...(more than {max_entries} entries, truncated)")
                return True, "\n".join(lines)
            size = os.path.getsize(os.path.join(root, name))
            lines.append(f"{indent}  {name}  ({size}B)")
    return True, "\n".join(lines) or "(empty directory)"

def ws_read(path):
    try:
        p = _safe_path(path)
    except PermissionError as e:
        return False, str(e)
    if not os.path.isfile(p):
        return False, f"file does not exist: {_rel(p)}"
    size = os.path.getsize(p)
    if size > MAX_READ_BYTES:
        return False, f"file too large ({size}B > {MAX_READ_BYTES}B)"
    text = None
    for enc in ("utf-8-sig", "utf-8", "gbk", "big5"):
        try:
            with open(p, "r", encoding=enc) as f:
                text = f.read()
            break
        except UnicodeDecodeError:
            continue
        except OSError as e:
            return False, f"read failed: {e}"
    if text is None:
        return False, "unrecognized file encoding"
    if len(text) > MAX_READ_CHARS:
        text = text[:MAX_READ_CHARS] + f"\n...(truncated; original {len(text)} chars)"
    _issue_ticket(p)
    return True, text

def ws_write(path, content):
    try:
        p = _safe_path(path)
    except PermissionError as e:
        return False, str(e)
    if os.path.isdir(p):
        return False, f"target is a directory: {_rel(p)}"
    ok, bmsg = _backup_root_file(path, p)
    if not ok:
        return False, bmsg
    os.makedirs(os.path.dirname(p), exist_ok=True)
    try:
        with open(p, "w", encoding="utf-8") as f:
            f.write(content if content is not None else "")
    except OSError as e:
        return False, f"write failed: {e}"
    _issue_ticket(p)
    _log(f"[{_ts()}] ws_write {_rel(p)} ({len(content or '')} chars)")
    tail = f" | {bmsg}" if bmsg else ""
    return True, f"wrote {_rel(p)} ({len(content or '')} chars){tail}"

def ws_append(path, content):
    try:
        p = _safe_path(path)
    except PermissionError as e:
        return False, str(e)
    ok, msg = _check_ticket(p)
    if not ok:
        return False, msg
    ok, bmsg = _backup_root_file(path, p)
    if not ok:
        return False, bmsg
    os.makedirs(os.path.dirname(p), exist_ok=True)
    try:
        with open(p, "a", encoding="utf-8") as f:
            f.write(content if content is not None else "")
    except OSError as e:
        return False, f"append failed: {e}"
    _issue_ticket(p)
    _log(f"[{_ts()}] ws_append {_rel(p)}")
    tail = f" | {bmsg}" if bmsg else ""
    return True, f"appended to {_rel(p)}{tail}"

def ws_replace(path, old, new, count=1):
    try:
        p = _safe_path(path)
    except PermissionError as e:
        return False, str(e)
    if not os.path.isfile(p):
        return False, f"file does not exist: {_rel(p)}"
    ok, msg = _check_ticket(p)
    if not ok:
        return False, msg
    ok, bmsg = _backup_root_file(path, p)
    if not ok:
        return False, bmsg
    try:
        with open(p, "r", encoding="utf-8") as f:
            text = f.read()
    except OSError as e:
        return False, f"read failed: {e}"
    if old not in text:
        return False, "the text to replace was not found (exact match required)"
    n = text.count(old)
    text = text.replace(old, new, count if count and count > 0 else -1)
    try:
        with open(p, "w", encoding="utf-8") as f:
            f.write(text)
    except OSError as e:
        return False, f"write failed: {e}"
    _issue_ticket(p)
    _log(f"[{_ts()}] ws_replace {_rel(p)} ({n} occurrence(s) originally)")
    tail = f" | {bmsg}" if bmsg else ""
    return True, f"modified {_rel(p)} ({n} match(es) originally){tail}"

def ws_delete(path):
    try:
        p = _safe_path(path)
    except PermissionError as e:
        return False, str(e)
    if not os.path.exists(p):
        return False, f"does not exist: {_rel(p)}"
    ok, msg = _check_ticket(p)
    if not ok:
        return False, msg
    ok, bmsg = _backup_root_file(path, p)
    if not ok:
        return False, bmsg
    try:
        if os.path.isdir(p):
            os.rmdir(p)
        else:
            os.remove(p)
    except OSError as e:
        return False, f"delete failed: {e}"
    _READ_TICKETS.pop(os.path.abspath(p), None)
    _log(f"[{_ts()}] ws_delete {_rel(p)}")
    tail = f" | {bmsg}" if bmsg else ""
    return True, f"deleted {_rel(p)}{tail}"

def ws_mkdir(path):
    try:
        p = _safe_path(path)
    except PermissionError as e:
        return False, str(e)
    os.makedirs(p, exist_ok=True)
    return True, f"created directory {_rel(p)}"

def ws_search(keyword, path="", max_hits=500):
    try:
        p = _safe_path(path)
    except PermissionError as e:
        return False, str(e)
    if not os.path.isdir(p):
        return False, f"not a directory: {_rel(p)}"
    hits = []
    for root, dirs, files in os.walk(p):
        dirs.sort()
        for name in sorted(files):
            fp = os.path.join(root, name)
            try:
                with open(fp, "r", encoding="utf-8", errors="ignore") as f:
                    for i, line in enumerate(f, 1):
                        if keyword in line:
                            hits.append(f"{_rel(fp)}:{i}: {line.rstrip()[:200]}")
                            if len(hits) >= max_hits:
                                return True, "\n".join(hits) + f"\n...(more than {max_hits} hits, truncated)"
            except OSError:
                continue
    return True, "\n".join(hits) if hits else f"not found: {keyword}"

def ws_cd(path):
    return set_workspace(path)

def ws_cd_approve():
    return approve_pending_cd()

def ws_where():
    return True, f"current workspace: {get_workspace()}"

def ws_forget(path=""):
    if not path:
        clear_tickets()
        return True, "cleared all read tickets"
    try:
        p = _safe_path(path)
    except PermissionError as e:
        return False, str(e)
    _READ_TICKETS.pop(os.path.abspath(p), None)
    return True, f"cleared the ticket for: {_rel(p)}"

def ws_run_cmd(command, cwd="", timeout=30):
    return exec_tools.run_cmd(command, get_workspace(), cwd=cwd, timeout=timeout)

def ws_run_python(code, cwd="", timeout=30, filename=""):
    return exec_tools.run_python(
        code, get_workspace(), cwd=cwd, timeout=timeout, filename=filename or None
    )

TOOL_SCHEMAS = [
    {
        "type": "function",
        "function": {
            "name": "ws_where",
            "description": "Show the current workspace root.",
            "parameters": {"type": "object", "properties": {}},
        },
    },
    {
        "type": "function",
        "function": {
            "name": "ws_cd",
            "description": "Switch the workspace root to the given path (must be an existing directory). Note: moving outside the default sandbox (~\\workspace) needs user approval and returns a pending-approval notice.",
            "parameters": {
                "type": "object",
                "properties": {"path": {"type": "string", "description": "the new workspace root"}},
                "required": ["path"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "ws_cd_approve",
            "description": "Approve and execute the previous workspace switch that was blocked (moving outside the default sandbox). Only call this after the user has explicitly agreed.",
            "parameters": {"type": "object", "properties": {}},
        },
    },
    {
        "type": "function",
        "function": {
            "name": "ws_list",
            "description": "List the directory tree inside the workspace. path is relative to the workspace; an empty string means the root.",
            "parameters": {
                "type": "object",
                "properties": {
                    "path": {"type": "string", "description": "relative path, defaults to the root"},
                    "depth": {"type": "integer", "description": "recursion depth, default 3"},
                },
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "ws_read",
            "description": "Read the contents of a text file inside the workspace. On success it issues a 'read ticket' and the file can then be modified directly within this session.",
            "parameters": {
                "type": "object",
                "properties": {"path": {"type": "string", "description": "relative file path"}},
                "required": ["path"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "ws_write",
            "description": "Write or overwrite a file inside the workspace, creating parent directories automatically. A full overwrite needs no prior read. Note: a root-level file is automatically backed up to _backup/ before being overwritten.",
            "parameters": {
                "type": "object",
                "properties": {
                    "path": {"type": "string", "description": "relative file path"},
                    "content": {"type": "string", "description": "the complete file content"},
                },
                "required": ["path", "content"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "ws_append",
            "description": "Append content to the end of a file inside the workspace. The file must be ws_read first. A root-level file is automatically backed up before appending.",
            "parameters": {
                "type": "object",
                "properties": {
                    "path": {"type": "string"},
                    "content": {"type": "string"},
                },
                "required": ["path", "content"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "ws_replace",
            "description": "Exact text replacement inside a file in the workspace (old must match verbatim, including indentation). The file must be ws_read first. A root-level file is automatically backed up before replacing.",
            "parameters": {
                "type": "object",
                "properties": {
                    "path": {"type": "string"},
                    "old": {"type": "string", "description": "the original text to replace"},
                    "new": {"type": "string", "description": "the new replacement text"},
                    "count": {"type": "integer", "description": "number of replacements; 0 or omitted means all"},
                },
                "required": ["path", "old", "new"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "ws_delete",
            "description": "Delete a file or empty directory inside the workspace. The file must be ws_read first. A root-level file is automatically backed up before deletion.",
            "parameters": {
                "type": "object",
                "properties": {"path": {"type": "string"}},
                "required": ["path"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "ws_mkdir",
            "description": "Create a directory inside the workspace.",
            "parameters": {
                "type": "object",
                "properties": {"path": {"type": "string"}},
                "required": ["path"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "ws_search",
            "description": "Full-text search for a keyword inside the workspace; returns file:line: content.",
            "parameters": {
                "type": "object",
                "properties": {
                    "keyword": {"type": "string"},
                    "path": {"type": "string", "description": "search scope, defaults to the root"},
                },
                "required": ["keyword"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "ws_forget",
            "description": "Clear 'read tickets'. With no path, clears all; with a path, clears only that file. After clearing, the file must be ws_read again before it can be modified.",
            "parameters": {
                "type": "object",
                "properties": {"path": {"type": "string", "description": "optional; a specific file"}},
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "ws_run_cmd",
            "description": (
                "Run one CMD (Windows) / Shell (other platforms) command inside the workspace "
                "directory and return stdout, stderr, and the exit code. Useful for inspecting "
                "system info, running programs, compiling, installing dependencies, git "
                "operations, and so on. The command runs in the workspace root by default; use "
                "cwd to target a subdirectory. Timeout-protected."
            ),
            "parameters": {
                "type": "object",
                "properties": {
                    "command": {"type": "string", "description": "the command to run, e.g. 'dir' or 'python --version'"},
                    "cwd": {"type": "string", "description": "subdirectory relative to the workspace; defaults to the workspace root"},
                    "timeout": {"type": "integer", "description": "timeout in seconds; default 120, max 1800"},
                },
                "required": ["command"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "ws_run_python",
            "description": (
                "Run a snippet of Python inside the workspace and return the result "
                "(stdout/stderr/exit code). The code is written to a temporary file and run with "
                "the current interpreter; multi-line code, imports, and file IO are all supported. "
                "Good for verifying algorithms, running scripts, and processing data. "
                "Timeout-protected."
            ),
            "parameters": {
                "type": "object",
                "properties": {
                    "code": {"type": "string", "description": "the complete Python code"},
                    "cwd": {"type": "string", "description": "subdirectory relative to the workspace; defaults to the workspace root"},
                    "timeout": {"type": "integer", "description": "timeout in seconds; default 120, max 1800"},
                    "filename": {"type": "string", "description": "optional temp script filename (helps tracebacks identify it)"},
                },
                "required": ["code"],
            },
        },
    },
]

_DISPATCH = {
    "ws_where":   lambda a: ws_where(),
    "ws_cd":      lambda a: ws_cd(a["path"]),
    "ws_cd_approve": lambda a: ws_cd_approve(),
    "ws_list":    lambda a: ws_list(a.get("path", ""), a.get("depth", 3)),
    "ws_read":    lambda a: ws_read(a["path"]),
    "ws_write":   lambda a: ws_write(a["path"], a.get("content", "")),
    "ws_append":  lambda a: ws_append(a["path"], a.get("content", "")),
    "ws_replace": lambda a: ws_replace(a["path"], a["old"], a["new"], a.get("count", 1)),
    "ws_delete":  lambda a: ws_delete(a["path"]),
    "ws_mkdir":   lambda a: ws_mkdir(a["path"]),
    "ws_search":  lambda a: ws_search(a["keyword"], a.get("path", "")),
    "ws_forget":  lambda a: ws_forget(a.get("path", "")),
    "ws_run_cmd":    lambda a: ws_run_cmd(a["command"], a.get("cwd", ""), a.get("timeout", 120)),
    "ws_run_python": lambda a: ws_run_python(a["code"], a.get("cwd", ""), a.get("timeout", 120), a.get("filename", "")),
}

def call_tool(name, args):
    fn = _DISPATCH.get(name)
    if not fn:
        return False, f"unknown tool: {name}"
    try:
        return fn(args or {})
    except KeyError as e:
        return False, f"missing parameter: {e}"
    except Exception as e:
        return False, f"tool raised an exception: {e}"
