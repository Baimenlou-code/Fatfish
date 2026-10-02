# -*- coding: utf-8 -*-
"""fatfish_core.setappl —— /set 的设置 applier

从 FATHFISH.py 拆出。被 settings.register(apply=...) 引用，/set 改值时调用。

★ 原本用 globals()[X] = v 直接写主程序全局变量；搬进模块后那样会写错
  命名空间，故统一改为 _put(X, v)，setter 由主程序 bind() 注入。"""

import os
import net_tools

# ============ 注入接口（由 FATHFISH.py 启动时 bind）============
_reconfig_verifier = None   # 注入：核验配置重注入
_sync_modcfg = None         # 注入：部件配置同步

_setter = None

def _put(name, value):
    """写回主程序的全局变量（setter 由主程序注入）。"""
    _setter(name, value)

def bind(**kw):
    """注入外部依赖；只覆盖显式传入且非 None 的键（幂等）。"""
    g = globals()
    for _k, _v in kw.items():
        if _v is not None:
            g[_k] = _v
    return True



# ============ 外部配置 ============
LOG_DIR = ""          # 由主程序 bind() 注入


def _apply_verify_mirror(v):
    _put("VERIFY_MIRROR", v)
    _put("VERIFY_MIRROR_PATH",
        os.path.join(LOG_DIR, f"exec_verify_{os.getpid()}.out") if v else "")
    _reconfig_verifier()


def _apply_approve_run_tools(v):
    _put("APPROVE_RUN_TOOLS", v)
    # 与启动时的名单保持一致：只读类（含 ws_read）不在基础名单内，
    # 它们由 _approval_needed() 按「敏感文件 + approve_scope」动态决定。
    base = {"ws_write", "ws_append", "ws_replace", "ws_delete"}
    if v:
        base |= {"ws_run_cmd", "ws_run_python"}
    _put("APPROVAL_REQUIRED", base)


def _apply_approve_scope(v):
    """报批范围：all=只读也报批（旧行为）/ writes=只读免报批（新默认）。"""
    _put("APPROVE_SCOPE", v if v in ("all", "writes") else "writes")
    _sync_modcfg()      # [SPLIT v1] 同步给 fatfish_core.policy


def _apply_auto_approve_scope(v):
    """一键放行可覆盖的范围：none / writes / all（默认）。

    writes 档下，删除与执行类永不自动放行 —— 保证人工闸门不会整体消失。
    all 档下仅敏感文件仍拦，删除 / 执行类也会被放行；此时审查员 AI 成为该批
    唯一的外部复核 —— 送审备注会按档位如实告知它（见主循环的 extra_note）。
    """
    _put("AUTO_APPROVE_SCOPE", v if v in ("none", "writes", "all") else "all")
    _sync_modcfg()      # [SPLIT v1] 同步给 fatfish_core.policy


def _apply_net_mode(v):
    net_tools.NET_MODE = v


def _apply_tavily_mode(v):
    net_tools.TAVILY_MODE = v
