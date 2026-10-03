# -*- coding: utf-8 -*-
"""fatfish_core.envutil —— 环境变量读取 / 密钥体检

从 FATHFISH.py 拆出。纯函数，只依赖 os，可独立自测。"""

import os
import re

# ============ 外部配置 ============
# 由 FATHFISH.py 启动时注入真值；此处默认值只保证「单独 import 也不炸」。

# ============ 配置 ============
# ---- .env 读取容错辅助 ----
# 背景：python-dotenv 对「空值 + 行尾注释」（形如 KEY=   # 注释）不会剥离注释，
#      会把注释文本原样当值返回；若把它当 API key，会构造出含中文的 HTTP 头，
#      触发 httpx 的 'ascii' codec 编码错误。这里统一做防御性清理。
def _env_clean(name, default=""):
    v = os.getenv(name, "")
    if v is None:
        return default
    v = v.strip()
    if v.startswith("#"):
        return default
    for sep in ("  #", "\t#", " #"):
        if sep in v:
            v = v.split(sep, 1)[0].strip()
    return v or default


def _looks_like_key(s):
    """API key 基本体检：非空、无空白、纯 ASCII（HTTP 头只能放 ASCII）。"""
    if not s:
        return False
    if any(c.isspace() for c in s):
        return False
    return all(ord(c) < 128 for c in s)


def _env_int(name, default):
    try:
        return int(_env_clean(name, str(default)) or default)
    except (TypeError, ValueError):
        return default


def _env_bool(name, default=False):
    v = _env_clean(name, "1" if default else "0").lower()
    return v in ("1", "true", "yes", "on")


def _env_float(name, default=0.0):
    try:
        return float(_env_clean(name, str(default)) or default)
    except (TypeError, ValueError):
        return default


# ============ .env 写入（2026-10-03 · P1d）============
#   背景：GUI 要"记住布局"，就得把几行配置写回 .env。
#   ★ 安全红线（.env 里有 API Key）：
#     · 只改动**匹配的那一行**，其余字节原样保留 —— 绝不整文件重写；
#     · 不打印、不返回 .env 内容；
#     · 写失败只返回 (False, 原因)，由调用方决定提示与否。
#   与 settings.save_to_env 的区别：那个走"设置注册表"，这里只做**单键**写入，
#   不必把一个纯界面偏好塞进设置体系里。

def env_has_key(path, key):
    """`.env` 里是否存在该键（只回布尔，不回内容）。"""
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            text = f.read()
    except OSError:
        return False
    pat = re.compile(r"^[ \t]*" + re.escape(str(key)) + r"[ \t]*=", re.M)
    return bool(pat.search(text))


def env_set_line(path, key, value):
    """就地把 `.env` 的 `KEY=value` 写入 / 更新（不存在则追加到末尾）。

    ★ 只碰这一行，其余原样保留。换行风格按原文件（CRLF / LF）自适应。
    返回 (ok, msg)。
    """
    key = str(key or "").strip()
    if not key:
        return False, "空键名"
    try:
        raw = open(path, "rb").read() if os.path.exists(path) else b""
    except OSError as e:
        return False, "读 .env 失败：%s" % e

    # 换行风格：CRLF 占多数就按 CRLF 写
    _crlf = raw.count(b"\r\n")
    _lf = raw.count(b"\n")
    nl = b"\r\n" if (_crlf and _crlf >= (_lf - _crlf)) else b"\n"

    text = raw.decode("utf-8", "replace")
    lines = text.replace("\r\n", "\n").replace("\r", "\n").split("\n")
    if lines and lines[-1] == "":
        lines.pop()                      # 末尾空行先去掉，稍后统一补

    line_new = "%s=%s" % (key, value)
    pat = re.compile(r"^[ \t]*" + re.escape(key) + r"[ \t]*=")
    hit = False
    for i, ln in enumerate(lines):
        if pat.match(ln):
            lines[i] = line_new
            hit = True
            break
    if not hit:
        lines.append(line_new)

    out = nl.join(x.encode("utf-8") for x in lines) + nl
    try:
        with open(path, "wb") as f:
            f.write(out)
    except OSError as e:
        return False, "写 .env 失败：%s" % e
    return True, ("已更新 %s" % key) if hit else ("已追加 %s" % key)
