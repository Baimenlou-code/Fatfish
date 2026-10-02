# -*- coding: utf-8 -*-
"""fatfish_core.envutil —— 环境变量读取 / 密钥体检

从 FATHFISH.py 拆出。纯函数，只依赖 os，可独立自测。"""

import os

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
