# -*- coding: utf-8 -*-
"""fatfish_core.policy —— 报批判定 / 只读 Python 静态判定

从 FATHFISH.py 拆出，是「要不要人工报批」的唯一判定入口，
外加 ws_run_python 的 AST 只读判定（只省一次审查员调用，人工闸门一道不少）。
★ 本模块配置项在 /set 变更时由主程序重新注入（见 FATHFISH._sync_modcfg）。"""

from .envutil import _env_clean
import workspace

# ============ 外部配置 ============
# 由 FATHFISH.py 启动时注入真值；此处默认值只保证「单独 import 也不炸」。
APPROVAL_REQUIRED = {'ws_append', 'ws_replace', 'ws_delete', 'ws_write'}
APPROVE_SCOPE = (_env_clean("APPROVE_SCOPE", "writes") or "writes").lower()
AUTO_APPROVE_SCOPE = (_env_clean("AUTO_APPROVE_SCOPE", "all") or "all").lower()
HIGH_RISK_TOOLS = {'ws_run_python', 'ws_run_cmd'}
NEVER_AUTO_APPROVE = {'ws_bg_python', 'ws_run_python', 'ws_run_cmd', 'ws_delete', 'ws_bg_cmd', 'ws_bg_kill'}
_READONLY_ALWAYS_BLOCK_ATTRS = {'execvp', 'spawnv', 'putenv', 'spawnve', 'execve', 'setenv', 'execlp', 'spawnl', 'fork', 'execv', 'forkpty', 'system', 'environ', 'execl', 'popen', 'getenv'}
_READONLY_FILE_ALLOW = {'seek', 'readlines', 'fileno', 'readline', 'errors', 'newlines', 'writable', 'isatty', 'read', 'closed', 'encoding', 'flush', 'name', 'readable', 'tell', 'mode', 'close'}
_READONLY_MAX_CHARS = 40000
_READONLY_MUTATING_ATTRS = {'rmtree', 'send', 'copyfile', 'copytree', 'call', 'removedirs', 'copy2', 'unlink', 'kill', 'rmdir', 'truncate', 'sendall', 'save', 'write', 'chown', 'write_bytes', 'chmod', 'run', 'urlopen', 'write_text', 'mkdir', 'to_excel', 'chdir', 'Popen', 'connect', 'rename', 'abort', 'check_output', 'to_pickle', 'check_call', 'to_csv', 'writelines', 'makedirs', 'dump'}
_READONLY_ROOT_ALLOW = None   # 运行时由 FATHFISH.py 注入
_READONLY_SAFE_MODULES = {'functools', 'unicodedata', 'stat', 'uuid', 'fnmatch', 'math', 'hashlib', 'string', 'operator', 'textwrap', 'time', 're', 'enum', 'datetime', 'dataclasses', 'pathlib', 'typing', 'csv', 'zlib', 'copy', 'io', 'collections', 'pprint', 'base64', 'ast', 'difflib', 'sys', 'os', 'gzip', 'random', 'binascii', 'decimal', 'fractions', 'itertools', 'locale', 'platform', 'struct', 'glob', 'json'}
_READONLY_SECRET_HINTS = ('.env', 'id_rsa', '.pem', 'private_key', 'credential', 'api_key', 'apikey', 'secret', 'password', 'passwd', 'sk-')

def _root_name(node):
    """取调用链最左侧的名字：a.b.c() → 'a'；Path(x).write_text() → 'Path'。"""
    import ast as _ast
    while isinstance(node, _ast.Attribute):
        node = node.value
    if isinstance(node, _ast.Name):
        return node.id
    if isinstance(node, _ast.Call):
        return _root_name(node.func)
    return ""


def _expr_root_kind(expr, aliases):
    """推断一个表达式的「能力根种类」：os / pathlib / Path / __file__ / None。

    用于把 `p = pathlib.Path(x)`、`d = os.path`、`fh = open(p)` 这类
    **局部别名**识别成对应能力根，再按该根的允许表严格判定 —— 这是纯增益：
    多认出一个别名，只会让判定更严，不会更松。
    """
    import ast as _ast
    if expr is None:
        return None
    if isinstance(expr, _ast.Call):
        if isinstance(expr.func, _ast.Name) and expr.func.id == "open":
            return "__file__"
        return _expr_root_kind(expr.func, aliases)
    if isinstance(expr, _ast.Attribute):
        base = _expr_root_kind(expr.value, aliases)
        if base == "pathlib" and expr.attr in ("Path", "PurePath"):
            return "Path"
        if base == "os" and expr.attr == "path":
            return "os"
        return base
    if isinstance(expr, _ast.Name):
        if expr.id in _READONLY_ROOT_ALLOW:
            return expr.id
        if expr.id == "open":
            return "__file__"
        return aliases.get(expr.id)
    return None


def _collect_aliases(tree):
    """收集「本地名 → 能力根种类」映射：import as / 赋值 / for / with。

    只做保守传播：拿不准就不建映射（该名随后按未知对象兜底处理，仍然安全）。
    """
    import ast as _ast
    aliases = {}
    for node in _ast.walk(tree):
        if isinstance(node, _ast.Import):
            for a in node.names:
                if a.asname:
                    aliases[a.asname] = a.name.split(".")[0]
        elif isinstance(node, _ast.Assign):
            kind = _expr_root_kind(node.value, aliases)
            if kind:
                for t in node.targets:
                    names = ([t] if isinstance(t, _ast.Name)
                             else [e for e in getattr(t, "elts", [])
                                   if isinstance(e, _ast.Name)])
                    for nm in names:
                        aliases[nm.id] = kind
        elif isinstance(node, _ast.AnnAssign):
            if isinstance(node.target, _ast.Name):
                kind = _expr_root_kind(node.value, aliases)
                if kind:
                    aliases[node.target.id] = kind
        elif isinstance(node, _ast.For):
            # 只有 Path 这类「迭代产出同类对象」的根才向下传播，避免误伤
            kind = _expr_root_kind(node.iter, aliases)
            if kind == "Path" and isinstance(node.target, _ast.Name):
                aliases[node.target.id] = "Path"
        elif isinstance(node, _ast.With):
            for item in node.items:
                if isinstance(item.optional_vars, _ast.Name):
                    kind = _expr_root_kind(item.context_expr, aliases)
                    if kind:
                        aliases[item.optional_vars.id] = kind
    return aliases


def _python_is_readonly(code):
    """AST 静态判定：这段代码在静态上是否「只读且无外联」。返回 (bool, 原因)。

    ⚠️ 作用域声明（避免误读）：本判定**只决定「要不要再请第二位 AI 复核」**，
       与「要不要人工报批」无关 —— ws_run_python 是否需要人工批准，由
       _approval_needed() 依 APPROVAL_REQUIRED 决定，与本函数无关。
       也就是说：判为只读只省一次审查员调用（token），人工闸门一道不少。

    只在能明确判定安全时返回 True；一切不确定都返回 False（由调用方照常送审）。
    绝不执行代码，只解析语法树，零副作用。
    """
    import ast as _ast
    if not code or not code.strip():
        return False, "空代码"
    if len(code) > _READONLY_MAX_CHARS:
        return False, "代码过长，未做静态判定"
    try:
        tree = _ast.parse(code)
    except SyntaxError as e:
        return False, f"语法解析失败：{e}"
    except Exception:
        return False, "AST 解析异常"

    aliases = _collect_aliases(tree)     # 先把局部别名还原成能力根
    bad = []
    for node in _ast.walk(tree):
        # ---- 导入 ----
        if isinstance(node, _ast.Import):
            for a in node.names:
                if a.name.split(".")[0] not in _READONLY_SAFE_MODULES:
                    bad.append("导入 " + a.name)
        elif isinstance(node, _ast.ImportFrom):
            mod = node.module or ""
            top = mod.split(".")[0]
            if top in _READONLY_ROOT_ALLOW:
                # from os import ... / from pathlib import ... → 能力根无法追踪，送审
                bad.append("from %s import …（能力根无法追踪）" % (mod or "?"))
            elif top not in _READONLY_SAFE_MODULES:
                bad.append("导入 " + (mod or "?"))
        # ---- 字符串字面量敏感痕迹 ----
        elif isinstance(node, _ast.Constant) and isinstance(node.value, str):
            low = node.value.lower()
            for h in _READONLY_SECRET_HINTS:
                if h in low:
                    bad.append("字面量含敏感痕迹 %r" % h)
                    break
        # ---- 调用 ----
        elif isinstance(node, _ast.Call):
            f = node.func
            if isinstance(f, _ast.Name):
                if f.id in ("exec", "eval", "compile", "__import__", "input",
                            "breakpoint", "getattr", "setattr", "delattr",
                            "globals", "locals", "vars"):
                    bad.append("调用 " + f.id)
                elif f.id == "open":
                    # ★ 默认拒绝：只有「字面量且为只读模式」才放行。
                    modes = [k.value for k in node.keywords if k.arg == "mode"]
                    if len(node.args) >= 2:
                        modes.append(node.args[1])
                    for mv in modes:
                        ok_mode = (isinstance(mv, _ast.Constant)
                                   and isinstance(mv.value, str)
                                   and not any(c in mv.value for c in "wax+"))
                        if not ok_mode:
                            bad.append("open(模式参数非字面量只读)")
                            break
            elif isinstance(f, _ast.Attribute):
                root = _root_name(f)
                root = aliases.get(root, root)      # 局部别名 → 还原能力根
                if f.attr in _READONLY_ALWAYS_BLOCK_ATTRS:
                    bad.append("调用 .%s()（环境变量/进程能力）" % f.attr)
                elif root in _READONLY_ROOT_ALLOW:
                    if f.attr not in _READONLY_ROOT_ALLOW[root]:
                        bad.append("调用 %s.%s()" % (root, f.attr))
                elif root == "__file__":
                    if f.attr not in _READONLY_FILE_ALLOW:
                        bad.append("文件对象调用 .%s()" % f.attr)
                elif f.attr in _READONLY_MUTATING_ATTRS:
                    bad.append("调用变异方法 .%s()" % f.attr)
            elif isinstance(f, _ast.Subscript):
                bad.append("下标调用（无法静态判定）")
        # ---- 属性访问 / 赋值 ----
        elif isinstance(node, _ast.Attribute):
            if node.attr in _READONLY_ALWAYS_BLOCK_ATTRS:
                bad.append("访问 .%s（环境变量/进程能力）" % node.attr)
            elif isinstance(node.ctx, _ast.Store):
                bad.append("对属性赋值 .%s" % node.attr)
        # ---- 下标赋值 / 作用域声明 ----
        elif isinstance(node, _ast.Subscript) and isinstance(node.ctx, _ast.Store):
            bad.append("对下标赋值（可能改环境变量/全局表）")
        elif isinstance(node, (_ast.Global, _ast.Nonlocal)):
            bad.append("global/nonlocal 声明")

    if bad:
        uniq = []
        for b in bad:
            if b not in uniq:
                uniq.append(b)
        return False, "、".join(uniq[:4])
    return True, ""


def _approval_needed(name, args=None):
    """判断一次工具调用是否需要人工报批。返回 (need: bool, why: str)。

    这是报批决策的**唯一入口**（启动名单 + 动态例外都收敛在这里），
    避免「多处各写一份判定」造成的规则漂移。
    """
    args = args or {}
    if name in APPROVAL_REQUIRED:
        return True, ("high-risk exec" if name in HIGH_RISK_TOOLS else "")
    if name == "ws_read":
        if APPROVE_SCOPE == "all":
            return True, "approve_scope=all（只读也报批）"
        rel = args.get("path", "")
        if workspace.is_sensitive_path(rel):
            return True, workspace.sensitive_reason(rel)
    return False, ""


def _never_auto_approve(name, args=None):
    """该动作是否禁止「一键放行」——必须由用户亲手确认。

    ★ 敏感文件（.env / 密钥 / 凭据）无论**读还是写**、无论哪个档位都不豁免：
      只查 ws_read 是不够的 —— 向 .env 写 / 追加 / 替换同样属于「碰密钥」。
    """
    args = args or {}
    _p = str(args.get("path", "") or "")
    if _p and workspace.is_sensitive_path(_p):
        return True
    if AUTO_APPROVE_SCOPE == "none":
        return True
    if AUTO_APPROVE_SCOPE == "all":
        return False
    return name in NEVER_AUTO_APPROVE
