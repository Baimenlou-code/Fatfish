# -*- coding: utf-8 -*-
"""roundtime.py —— 轮次计时（本轮耗时 / 你停留了多久）

2026-10-02 自 FATHFISH.py 拆出。**函数体逐字搬运**，只把「在哪里打印」改成
「谁想知道谁来取」——于是三个落款出口（控制台 print_ai / 流式 finish_answer /
窗口 _foot）都调用本模块的 ``cost_tag()``，回答末尾那行三处口径完全一致，
而主程序**零传参**。

    · 本轮 = 你发话那一刻  → 本轮彻底跑完（含工具循环）
    · 停留 = 上一轮跑完    → 你下一次发话

外部依赖（paint / log / _ts / 配色）与开关（show_timer / exiting）都用
``bind()`` 注入；开关传**惰性 lambda**，因此永远读主程序最新值，
不会出现「模块里存了一份快照」那种分叉。
"""

import time

# ---- 注入槽：名字与 bind() 的关键字**逐字一致**（不注入也能独立自测）----
paint = None
log = None
_ts = None
BK = ""
DIM = ""
show_timer = None     # callable -> bool，对应主程序 SHOW_TIMER
exiting = None        # callable -> bool，对应主程序 _TIMER_EXITING

# ---- 运行时状态 ----
_LAST_ROUND_END = None
_ROUND_START = None
_ROUND_ACTIVE = False
_FOOTER_DONE = False     # 本轮落款是否已打过（避免与 _mark_round_end 的兜底行重复）


def bind(**kw):
    """主程序启动时注入绘图 / 日志 / 开关（值为 None 表示不覆盖默认）。"""
    g = globals()
    for k, v in kw.items():
        if v is not None:
            g[k] = v


def _show_on():
    try:
        return bool(show_timer()) if show_timer is not None else True
    except Exception:
        return True


def _is_exiting():
    try:
        return bool(exiting()) if exiting is not None else False
    except Exception:
        return False


def _pt(s):
    try:
        return paint(s, BK, DIM) if paint is not None else s
    except Exception:
        return s


def _fmt_secs(s):
    """紧凑格式：12.4s / 1m23.4s / 1h02m。"""
    s = max(0.0, float(s))
    if s < 60:
        return "%.1fs" % s
    if s < 3600:
        return "%dm%.1fs" % (int(s // 60), s % 60)
    return "%dh%02dm" % (int(s // 3600), int((s % 3600) // 60))


def cost_tag():
    """落款里的耗时段：「 · ⏱️ 本轮 44.8s」；未计时 / 已关闭时返回空串。"""
    if not _show_on() or _ROUND_START is None:
        return ""
    return " · ⏱️ 本轮 %s" % _fmt_secs(time.time() - _ROUND_START)


def mark_footer():
    """落款已经打过了（print_ai / 流式收尾调用）—— 让 _mark_round_end 不再补一行。"""
    global _FOOTER_DONE
    _FOOTER_DONE = True


def last_round_end():
    """上一轮结束时刻（时间戳）；从未跑过则为 None。"""
    return _LAST_ROUND_END


def _mark_round_start():
    """用户发话后调用：先亮出「停留」时长，再开始本轮计时。"""
    global _LAST_ROUND_END, _ROUND_START, _ROUND_ACTIVE, _FOOTER_DONE
    now = time.time()
    if _show_on() and _LAST_ROUND_END is not None:
        print(_pt("  ⏱️ 停留 %s" % _fmt_secs(now - _LAST_ROUND_END)))
    _ROUND_START = now
    _ROUND_ACTIVE = True
    _FOOTER_DONE = False


def _mark_round_end():
    """一轮结束时调用（正常回复 / 斜杠命令 / 异常，都走 finally 里的这里）。

    回复类的耗时已印在落款里（print_ai / finish_answer 调过 mark_footer），
    所以这里只在**没有落款**的轮次（斜杠命令等）补一行，避免重复打印。
    """
    global _LAST_ROUND_END, _ROUND_ACTIVE
    now = time.time()
    _LAST_ROUND_END = now
    if _show_on() and _ROUND_ACTIVE and _ROUND_START is not None and not _is_exiting():
        cost = now - _ROUND_START
        if not _FOOTER_DONE:
            print(_pt("  ⏱️ 本轮 %s" % _fmt_secs(cost)))
        if log is not None:
            try:
                log("[%s] 本轮耗时 %.2fs" % (_ts(), cost))
            except Exception:
                pass
    _ROUND_ACTIVE = False
