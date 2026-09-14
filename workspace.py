import os
import re
import logging
from datetime import datetime

import exec_tools

# ============ 工作台根目录（运行时可切换）============
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
    """返回当前工作台根目录。"""
    if _WORKSPACE_DIR is None:
        _init_default()
    return _WORKSPACE_DIR

# 待批准的切换请求：{"path": 目标路径}
_PENDING_CD = None


def default_workspace():
    """返回默认作业区（家目录）的绝对路径。"""
    return os.path.abspath(
        os.path.expanduser(os.path.expandvars(DEFAULT_WORKSPACE))
    )


def is_inside_default(path):
    """判断路径是否位于默认作业区之内（含自身）。"""
    home = default_workspace()
    p = os.path.abspath(path)
    root = home.rstrip(os.sep) + os.sep
    return p == home or p.startswith(root)


def set_workspace(path):
    """切换工作台根目录，返回 (ok, 说明)。

    规则：
    - 切回默认作业区（或默认作业区内的子目录）：直接放行。
    - 切到默认作业区之外：不执行，登记为「待批准」，返回需审批的提示。
    """
    global _WORKSPACE_DIR, _PENDING_CD
    if not path:
        return False, "路径为空"
    p = os.path.abspath(
        os.path.expanduser(os.path.expandvars(str(path).strip().strip("'\"`")))
    )
    if not os.path.exists(p):
        return False, f"路径不存在：{p}"
    if not os.path.isdir(p):
        return False, f"不是目录：{p}"

    # 移出默认作业区 → 需用户批准
    if not is_inside_default(p):
        _PENDING_CD = {"path": p}
        return False, (
            f"⚠️ 该操作会把办公场所移出默认作业区，需用户批准后才能执行。\n"
            f"目标：{p}\n"
            f"默认作业区：{default_workspace()}\n"
            f"请用户确认后，调用 ws_cd_approve 放行。"
        )

    _WORKSPACE_DIR = p
    _PENDING_CD = None
    clear_tickets()
    _log(f"[{_ts()}] 工作台切换到：{p}")
    return True, f"工作台已切换到：{p}"


def approve_pending_cd():
    """批准并执行挂起的切换请求，返回 (ok, 说明)。"""
    global _WORKSPACE_DIR, _PENDING_CD
    if not _PENDING_CD:
        return False, "当前没有待批准的切换请求"
    p = _PENDING_CD["path"]
    if not os.path.isdir(p):
        _PENDING_CD = None
        return False, f"目标已不存在或不是目录：{p}"
    _WORKSPACE_DIR = p
    _PENDING_CD = None
    clear_tickets()
    _log(f"[{_ts()}] 用户批准，工作台切换到：{p}")
    return True, f"已批准，工作台切换到：{p}"

def reset_workspace():
    """恢复默认工作台。"""
    _init_default()
    clear_tickets()
    return True, f"工作台已重置为：{_WORKSPACE_DIR}"

# PEP 562：让 workspace.WORKSPACE_DIR 动态取当前值
def __getattr__(name):
    if name == "WORKSPACE_DIR":
        return get_workspace()
    raise AttributeError(name)

# ============ 常量（已整体放宽）============
MAX_READ_BYTES = 5 * 1024 * 1024   # 单文件读取上限：5MB（原 200KB）
MAX_READ_CHARS = 1_000_000         # 单文件读取字符上限：100 万（原 5 万）

def _log(msg):
    logging.info(msg)

def _ts():
    return datetime.now().strftime("%Y-%m-%d %H:%M:%S")

# ============ 读过凭证（会话级）============
# path -> (mtime, size)  表示"本会话内已确认读过该文件"
_READ_TICKETS = {}

def _issue_ticket(path):
    """登记已读凭证。"""
    try:
        st = os.stat(path)
        _READ_TICKETS[os.path.abspath(path)] = (st.st_mtime, st.st_size)
    except OSError:
        pass

def _check_ticket(path):
    """检查是否持有有效凭证。返回 (ok, 提示)。"""
    ap = os.path.abspath(path)
    if not os.path.exists(ap):
        # 文件不存在 → 允许新建，无需凭证
        return True, ""
    tk = _READ_TICKETS.get(ap)
    if tk is None:
        return False, f"未读过该文件，请先 ws_read：{_rel(ap)}"
    try:
        st = os.stat(ap)
    except OSError as e:
        return False, f"无法读取文件状态：{e}"
    if st.st_mtime != tk[0] or st.st_size != tk[1]:
        _READ_TICKETS.pop(ap, None)   # 凭证作废
        return False, f"文件已被外部改动，凭证失效，请重新 ws_read：{_rel(ap)}"
    return True, ""

def clear_tickets():
    """清空所有读过凭证（例如切换工作台时）。"""
    _READ_TICKETS.clear()

# ============ 根一级文件变更备案 ============
BACKUP_DIRNAME = "_backup"   # 备份区（位于工作台根下，自身不受备案规则约束）

def _is_root_level_file(rel):
    """判断相对路径是否为'工作台根一级'的文件（不含子目录）。"""
    if not rel:
        return False
    r = str(rel).strip().strip("'\"`").replace("\\", "/").strip("/")
    if not r:
        return False
    # 根一级：只有一段，且不是备份区自身
    if "/" in r:
        return False
    if r == BACKUP_DIRNAME or r.startswith(BACKUP_DIRNAME + "/"):
        return False
    return True

def _backup_root_file(rel, p):
    """把根一级文件的原内容备份到 _backup/。返回 (ok, 说明)。

    仅当目标文件已存在时才备份；不存在（新建）则跳过。
    """
    if not _is_root_level_file(rel):
        return True, ""            # 非根一级，不备案
    if not os.path.isfile(p):
        return True, ""            # 新建，无原文件
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
        return False, f"备案失败（原文件未能备份）：{e}"
    _log(f"[{_ts()}] 备案原文件 {name} -> {_rel(dst)}")
    return True, f"已备案原文件 → {_rel(dst)}"

# ============ 路径安全 ============
def _safe_path(rel):
    """把相对路径解析到工作台内，越界则抛异常。"""
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
        raise PermissionError(f"路径越界：{rel}（工作台根：{ws}）")
    return target

def _rel(path):
    """转成相对工作台的展示路径。"""
    try:
        return os.path.relpath(path, get_workspace())
    except ValueError:
        return path

# ============ 工具实现 ============
def ws_list(path="", depth=3, max_entries=2000):
    """列出工作台内目录树。"""
    try:
        p = _safe_path(path)
    except PermissionError as e:
        return False, str(e)
    if not os.path.isdir(p):
        return False, f"不是目录：{_rel(p)}"

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
                lines.append(f"{indent}  ...（超过 {max_entries} 项，已截断）")
                return True, "\n".join(lines)
            size = os.path.getsize(os.path.join(root, name))
            lines.append(f"{indent}  {name}  ({size}B)")
    return True, "\n".join(lines) or "（空目录）"

def ws_read(path):
    """读取工作台内文件，成功后发放'读过凭证'。"""
    try:
        p = _safe_path(path)
    except PermissionError as e:
        return False, str(e)
    if not os.path.isfile(p):
        return False, f"文件不存在：{_rel(p)}"
    size = os.path.getsize(p)
    if size > MAX_READ_BYTES:
        return False, f"文件过大（{size}B > {MAX_READ_BYTES}B）"
    text = None
    for enc in ("utf-8-sig", "utf-8", "gbk", "big5"):
        try:
            with open(p, "r", encoding=enc) as f:
                text = f.read()
            break
        except UnicodeDecodeError:
            continue
        except OSError as e:
            return False, f"读取失败：{e}"
    if text is None:
        return False, "无法识别文件编码"
    if len(text) > MAX_READ_CHARS:
        text = text[:MAX_READ_CHARS] + f"\n...（已截断，原文 {len(text)} 字符）"
    _issue_ticket(p)
    return True, text

def ws_write(path, content):
    """写入/覆盖文件，自动建父目录。全量覆盖豁免凭证校验，写完刷新凭证。"""
    try:
        p = _safe_path(path)
    except PermissionError as e:
        return False, str(e)
    if os.path.isdir(p):
        return False, f"目标是目录：{_rel(p)}"
    # 根一级文件：覆盖前先备案原文件
    ok, bmsg = _backup_root_file(path, p)
    if not ok:
        return False, bmsg
    os.makedirs(os.path.dirname(p), exist_ok=True)
    try:
        with open(p, "w", encoding="utf-8") as f:
            f.write(content if content is not None else "")
    except OSError as e:
        return False, f"写入失败：{e}"
    _issue_ticket(p)
    _log(f"[{_ts()}] ws_write {_rel(p)} ({len(content or '')} 字符)")
    tail = f"｜{bmsg}" if bmsg else ""
    return True, f"已写入 {_rel(p)}（{len(content or '')} 字符）{tail}"

def ws_append(path, content):
    """追加内容到文件末尾。需持有读过凭证。"""
    try:
        p = _safe_path(path)
    except PermissionError as e:
        return False, str(e)
    ok, msg = _check_ticket(p)
    if not ok:
        return False, msg
    # 根一级文件：追加前先备案原文件
    ok, bmsg = _backup_root_file(path, p)
    if not ok:
        return False, bmsg
    os.makedirs(os.path.dirname(p), exist_ok=True)
    try:
        with open(p, "a", encoding="utf-8") as f:
            f.write(content if content is not None else "")
    except OSError as e:
        return False, f"追加失败：{e}"
    _issue_ticket(p)
    _log(f"[{_ts()}] ws_append {_rel(p)}")
    tail = f"｜{bmsg}" if bmsg else ""
    return True, f"已追加到 {_rel(p)}{tail}"

def ws_replace(path, old, new, count=1):
    """精确替换文件内文本（默认只替换第一处；count=0 表示全部）。需持有读过凭证。"""
    try:
        p = _safe_path(path)
    except PermissionError as e:
        return False, str(e)
    if not os.path.isfile(p):
        return False, f"文件不存在：{_rel(p)}"
    ok, msg = _check_ticket(p)
    if not ok:
        return False, msg
    # 根一级文件：替换前先备案原文件
    ok, bmsg = _backup_root_file(path, p)
    if not ok:
        return False, bmsg
    try:
        with open(p, "r", encoding="utf-8") as f:
            text = f.read()
    except OSError as e:
        return False, f"读取失败：{e}"
    if old not in text:
        return False, "未找到要替换的内容（需精确匹配）"
    n = text.count(old)
    text = text.replace(old, new, count if count and count > 0 else -1)
    try:
        with open(p, "w", encoding="utf-8") as f:
            f.write(text)
    except OSError as e:
        return False, f"写入失败：{e}"
    _issue_ticket(p)
    _log(f"[{_ts()}] ws_replace {_rel(p)}（原有 {n} 处）")
    tail = f"｜{bmsg}" if bmsg else ""
    return True, f"已修改 {_rel(p)}（原有 {n} 处匹配）{tail}"

def ws_delete(path):
    """删除文件或空目录。需持有读过凭证。"""
    try:
        p = _safe_path(path)
    except PermissionError as e:
        return False, str(e)
    if not os.path.exists(p):
        return False, f"不存在：{_rel(p)}"
    ok, msg = _check_ticket(p)
    if not ok:
        return False, msg
    # 根一级文件：删除前先备案原文件
    ok, bmsg = _backup_root_file(path, p)
    if not ok:
        return False, bmsg
    try:
        if os.path.isdir(p):
            os.rmdir(p)
        else:
            os.remove(p)
    except OSError as e:
        return False, f"删除失败：{e}"
    _READ_TICKETS.pop(os.path.abspath(p), None)
    _log(f"[{_ts()}] ws_delete {_rel(p)}")
    tail = f"｜{bmsg}" if bmsg else ""
    return True, f"已删除 {_rel(p)}{tail}"

def ws_mkdir(path):
    """创建目录。"""
    try:
        p = _safe_path(path)
    except PermissionError as e:
        return False, str(e)
    os.makedirs(p, exist_ok=True)
    return True, f"已创建目录 {_rel(p)}"

def ws_search(keyword, path="", max_hits=500):
    """在工作台内全文搜索关键词。"""
    try:
        p = _safe_path(path)
    except PermissionError as e:
        return False, str(e)
    if not os.path.isdir(p):
        return False, f"不是目录：{_rel(p)}"
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
                                return True, "\n".join(hits) + f"\n...（超过 {max_hits} 条，已截断）"
            except OSError:
                continue
    return True, "\n".join(hits) if hits else f"未找到：{keyword}"

def ws_cd(path):
    """切换工作台根目录。移出默认作业区时需用户批准。"""
    return set_workspace(path)

def ws_cd_approve():
    """批准并执行挂起的切换请求（移出默认作业区）。"""
    return approve_pending_cd()

def ws_where():
    """查看当前工作台根目录。"""
    return True, f"当前工作台：{get_workspace()}"

def ws_forget(path=""):
    """清空读过凭证；给 path 则只清该文件。"""
    if not path:
        clear_tickets()
        return True, "已清空所有读过凭证"
    try:
        p = _safe_path(path)
    except PermissionError as e:
        return False, str(e)
    _READ_TICKETS.pop(os.path.abspath(p), None)
    return True, f"已清除凭证：{_rel(p)}"

# ============ 命令 / 代码执行 ============
def ws_run_cmd(command, cwd="", timeout=30):
    """在工作台内执行 CMD/Shell 命令，返回输出。"""
    return exec_tools.run_cmd(command, get_workspace(), cwd=cwd, timeout=timeout)

def ws_run_python(code, cwd="", timeout=30, filename=""):
    """在工作台内运行一段 Python 代码，返回输出。"""
    return exec_tools.run_python(
        code, get_workspace(), cwd=cwd, timeout=timeout, filename=filename or None
    )

# ============ Function Calling 工具定义 ============
TOOL_SCHEMAS = [
    {
        "type": "function",
        "function": {
            "name": "ws_where",
            "description": "查看当前工作台根目录。",
            "parameters": {"type": "object", "properties": {}},
        },
    },
    {
        "type": "function",
        "function": {
            "name": "ws_cd",
            "description": "切换工作台根目录到指定路径（必须是已存在的目录）。注意：移出默认作业区（~\\workspace）的操作需用户批准，会返回待批准提示。",
            "parameters": {
                "type": "object",
                "properties": {"path": {"type": "string", "description": "新的工作台根目录"}},
                "required": ["path"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "ws_cd_approve",
            "description": "批准并执行上一次被拦下的工作台切换请求（移出默认作业区的操作）。仅在用户明确同意后调用。",
            "parameters": {"type": "object", "properties": {}},
        },
    },
    {
        "type": "function",
        "function": {
            "name": "ws_list",
            "description": "列出工作台内的目录树。path 为相对工作台的路径，空字符串表示根目录。",
            "parameters": {
                "type": "object",
                "properties": {
                    "path": {"type": "string", "description": "相对路径，默认根目录"},
                    "depth": {"type": "integer", "description": "递归深度，默认 3"},
                },
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "ws_read",
            "description": "读取工作台内的文本文件内容。读取成功后会获得'读过凭证'，之后可在本会话内直接修改该文件。",
            "parameters": {
                "type": "object",
                "properties": {"path": {"type": "string", "description": "文件相对路径"}},
                "required": ["path"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "ws_write",
            "description": "写入或覆盖工作台内的文件，会自动创建父目录。全量覆盖无需先读。注意：根一级文件被覆盖前会自动备份原文件到 _backup/。",
            "parameters": {
                "type": "object",
                "properties": {
                    "path": {"type": "string", "description": "文件相对路径"},
                    "content": {"type": "string", "description": "完整文件内容"},
                },
                "required": ["path", "content"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "ws_append",
            "description": "把内容追加到工作台内文件末尾。需先 ws_read 过该文件。根一级文件追加前会自动备份原文件。",
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
            "description": "在工作台内文件中精确替换文本（old 必须原样匹配，含缩进）。需先 ws_read 过该文件。根一级文件替换前会自动备份原文件。",
            "parameters": {
                "type": "object",
                "properties": {
                    "path": {"type": "string"},
                    "old": {"type": "string", "description": "要被替换的原文"},
                    "new": {"type": "string", "description": "替换后的新内容"},
                    "count": {"type": "integer", "description": "替换次数，0 或省略表示全部"},
                },
                "required": ["path", "old", "new"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "ws_delete",
            "description": "删除工作台内的文件或空目录。需先 ws_read 过该文件。根一级文件删除前会自动备份原文件。",
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
            "description": "在工作台内创建目录。",
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
            "description": "在工作台内全文搜索关键词，返回 文件:行号: 内容。",
            "parameters": {
                "type": "object",
                "properties": {
                    "keyword": {"type": "string"},
                    "path": {"type": "string", "description": "搜索范围，默认根目录"},
                },
                "required": ["keyword"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "ws_forget",
            "description": "清除'读过凭证'。不传 path 清全部；传 path 只清该文件。清掉后需重新 ws_read 才能改。",
            "parameters": {
                "type": "object",
                "properties": {"path": {"type": "string", "description": "可选，指定文件"}},
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "ws_run_cmd",
            "description": (
                "在工作台目录内执行一条 CMD（Windows）/Shell（其他平台）命令，"
                "返回 stdout、stderr 和退出码。可用于查看系统信息、运行程序、"
                "编译、安装依赖、git 操作等。命令默认在工作台根目录执行，"
                "可用 cwd 指定工作台内的子目录。有超时保护。"
            ),
            "parameters": {
                "type": "object",
                "properties": {
                    "command": {"type": "string", "description": "要执行的命令，例如 'dir' 或 'python --version'"},
                    "cwd": {"type": "string", "description": "相对工作台的子目录，默认工作台根目录"},
                    "timeout": {"type": "integer", "description": "超时秒数，默认 120，最大 1800"},
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
                "在工作台内运行一段 Python 代码并返回执行结果（stdout/stderr/退出码）。"
                "代码会写入临时文件后用当前解释器执行，支持多行、import、文件读写等。"
                "适合验证算法、跑脚本、处理数据。有超时保护。"
            ),
            "parameters": {
                "type": "object",
                "properties": {
                    "code": {"type": "string", "description": "完整的 Python 代码"},
                    "cwd": {"type": "string", "description": "相对工作台的子目录，默认工作台根目录"},
                    "timeout": {"type": "integer", "description": "超时秒数，默认 120，最大 1800"},
                    "filename": {"type": "string", "description": "可选，临时脚本文件名（便于 traceback 识别）"},
                },
                "required": ["code"],
            },
        },
    },
]

# ============ 工具分发 ============
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
    """执行工具，返回 (ok, result_text)。"""
    fn = _DISPATCH.get(name)
    if not fn:
        return False, f"未知工具：{name}"
    try:
        return fn(args or {})
    except KeyError as e:
        return False, f"缺少参数：{e}"
    except Exception as e:
        return False, f"工具执行异常：{e}"