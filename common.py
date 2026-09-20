# -*- coding: utf-8 -*-
"""
common.py —— 肥鱼公共基础件 / FatFish Common Utilities

为什么存在 / Why
----------------
时间戳这类小工具原先在 7 个模块里各写了一份完全相同的实现：

    def _ts():
        return datetime.now().strftime("%Y-%m-%d %H:%M:%S")

`_dated_dir()` 也有两份。重复本身不致命，但**改一处漏一处**会让各模块的
日志时间、目录命名规则悄悄分叉。本模块把它们收敛为单一事实来源（SSOT）。

收录内容 / Contents
------------------
  · ts()          —— 当前本地时间戳 "YYYY-MM-DD HH:MM:SS"
  · ts_of(t)      —— 任意时间戳的同一格式
  · dated_dir(root) —— 建并返回 root/YYYY/MM/DD（日志、生成代码的分层目录）

设计原则 / Design rules
----------------------
  · 零第三方依赖：只用标准库。
  · 绝不抛异常：任何异常都退回合理默认（见各函数 docstring）。
  · 可独立运行：`python common.py` 自带自测。

各模块的接入方式 / How modules use it
------------------------------------
    try:
        from common import ts as _ts
    except Exception:                # common.py 缺失时该模块仍保持自足
        def _ts():
            return datetime.now().strftime("%Y-%m-%d %H:%M:%S")

保留 `except` 分支是有意的：各模块因此仍可被单独复制/运行而不炸，
符合本项目「降级不崩溃」的既有风格。
"""

import os
import time
from datetime import datetime

__all__ = ["ts", "ts_of", "dated_dir", "TS_FMT"]

TS_FMT = "%Y-%m-%d %H:%M:%S"


def ts():
    """当前本地时间戳，格式 'YYYY-MM-DD HH:MM:SS'。

    失败时退回空字符串（调用方日志里出现空时间戳，比抛异常中断主流程好）。
    """
    try:
        return datetime.now().strftime(TS_FMT)
    except Exception:
        return ""


def ts_of(t=None):
    """把 Unix 时间戳 / datetime 转成同一格式；t 为空则等同 ts()。"""
    if t is None:
        return ts()
    try:
        if isinstance(t, datetime):
            return t.strftime(TS_FMT)
        return time.strftime(TS_FMT, time.localtime(float(t)))
    except Exception:
        return ""


def dated_dir(root):
    """建并返回 `root/YYYY/MM/DD`（幂等，已存在则直接返回）。

    日志与生成代码都按日期分层存放，本函数是那套目录规则的唯一出处。
    root 为空时退回当前目录，保证仍返回一个可用路径。
    """
    try:
        root = root or "."
        now = datetime.now()
        d = os.path.join(root, f"{now:%Y}", f"{now:%m}", f"{now:%d}")
        os.makedirs(d, exist_ok=True)
        return d
    except Exception:
        # 建目录失败（权限/磁盘满）也不抛：退回不存在的路径，由调用方自行处理
        try:
            now = datetime.now()
            return os.path.join(str(root), f"{now:%Y}", f"{now:%m}", f"{now:%d}")
        except Exception:
            return str(root or ".")


# ============================================================
# 自测 / Self-test
# ============================================================

def _selftest():
    ok = True

    t = ts()
    good = (len(t) == 19 and t[4] == "-" and t[13] == ":" and t[16] == ":")
    print("ts()        = %-24r %s" % (t, "OK" if good else "FAIL"))
    ok = ok and good

    t2 = ts_of(1789744091.0)
    good = (len(t2) == 19)
    print("ts_of(ts)   = %-24r %s" % (t2, "OK" if good else "FAIL"))
    ok = ok and good

    good = (ts_of(None) == ts()[:19] or len(ts_of(None)) == 19)
    print("ts_of(None) = %-24r %s" % (ts_of(None), "OK" if good else "FAIL"))
    ok = ok and good

    # 目录：用系统临时目录做自测，不污染工作台
    import tempfile
    base = tempfile.mkdtemp(prefix="fatfish_common_")
    d = dated_dir(base)
    good = os.path.isdir(d) and d.startswith(base)
    print("dated_dir() = %-24r %s" % (os.path.relpath(d, base), "OK" if good else "FAIL"))
    ok = ok and good

    # 幂等：再调一次应返回同一路径
    good = (dated_dir(base) == d)
    print("dated_dir() 幂等              %s" % ("OK" if good else "FAIL"))
    ok = ok and good

    # 边界：root 为空/None 不抛
    try:
        dated_dir(None)
        dated_dir("")
        print("边界(root=None/'')            OK")
    except Exception as e:
        ok = False
        print("边界(root=None/'')            FAIL:", e)

    try:
        import shutil
        shutil.rmtree(base, ignore_errors=True)
    except Exception:
        pass

    print("\n%s" % ("全部通过 ✅" if ok else "存在失败 ❌"))
    return 0 if ok else 1


if __name__ == "__main__":
    import sys
    sys.exit(_selftest())
