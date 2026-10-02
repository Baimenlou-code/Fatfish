# -*- coding: utf-8 -*-
"""fatfish_core.cmdcap —— 斜杠命令输出捕获（TeeStream）

把 sys.stdout 折一层：斜杠命令执行期间把输出复制进内存缓冲，
供下一轮作为「用户其实敲过什么」注入给模型。
全部状态都在本模块内，主程序只调 4 个入口。"""

import re

# ============ 外部配置 ============
# 由 FATHFISH.py 启动时注入真值；此处默认值只保证「单独 import 也不炸」。

# ============ 本模块自有状态（随拆分一起搬入）============
_cmd_state = {'on': False, 'buf': [], 'cmd': ''}
_CMD_TRANSCRIPT = []
_CMD_MAX_KEEP = 20
_ANSI_RE = re.compile(r"\x1b\[[0-9;]*m")

def _strip_ansi(s):
    return _ANSI_RE.sub("", s or "")


class _TeeStream:
    """把写到 stdout 的内容同时复制一份到当前命令缓冲（仅命令期间开启）。"""

    def __init__(self, real):
        self._real = real

    def write(self, s):
        try:
            self._real.write(s)
        except Exception:
            pass
        if _cmd_state["on"]:
            try:
                _cmd_state["buf"].append(s)
            except Exception:
                pass
        return len(s)

    def flush(self):
        try:
            self._real.flush()
        except Exception:
            pass

    def __getattr__(self, name):
        return getattr(self._real, name)


def _cmd_begin(cmd):
    """若本行是斜杠命令，开启输出捕获。"""
    if isinstance(cmd, str) and cmd.startswith("/"):
        _cmd_state["on"] = True
        _cmd_state["buf"] = []
        _cmd_state["cmd"] = cmd


def _cmd_finalize():
    """斜杠命令结束（每轮 finally 调用）：把「命令 + 输出」暂存，供下次注入。"""
    if not _cmd_state["on"]:
        return
    _cmd_state["on"] = False
    out = _strip_ansi("".join(_cmd_state["buf"])).strip()
    cmd = _cmd_state["cmd"]
    _cmd_state["buf"] = []
    _cmd_state["cmd"] = ""
    if cmd:
        _CMD_TRANSCRIPT.append((cmd, out))
        del _CMD_TRANSCRIPT[:-_CMD_MAX_KEEP]


def _cmd_abort():
    """本轮不是纯命令（普通发言 / /search 深入处理）：丢弃捕获，避免把整轮输出当成命令。"""
    _cmd_state["on"] = False
    _cmd_state["buf"] = []
    _cmd_state["cmd"] = ""


def _take_cmd_transcript():
    """取出并清空暂存命令记录，格式化成一段文本（注入用户消息前缀）。"""
    if not _CMD_TRANSCRIPT:
        return ""
    lines = ["【本轮之前你（用户）在本地执行过的命令与回显，仅供你了解现状，不是新的提问】"]
    for cmd, out in _CMD_TRANSCRIPT:
        lines.append(f"$ {cmd}")
        if out:
            lines.append(out)
    _CMD_TRANSCRIPT.clear()
    return "\n".join(lines)[:20000]
