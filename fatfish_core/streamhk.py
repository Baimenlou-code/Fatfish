# -*- coding: utf-8 -*-
"""fatfish_core.streamhk —— 流式输出胶水层

从 FATHFISH.py 拆出（原 STREAM-PATCH v1 块）。真正的增量渲染在
stream_core.py；本模块负责「何时用流式 / 何时回退非流式」以及
转圈动画与正文的交接。

★ 外部依赖用 bind() 注入：log / _wait_stop。"""

import sys
from common import ts as _ts
from ui_core import BC, BG, BOLD, BR, BY, DIM, paint

try:
    import stream_core as _FC_SC
except Exception:
    _FC_SC = None

_FC_AVAILABLE = _FC_SC is not None

# ============ 注入接口（由 FATHFISH.py 启动时 bind）============
log = None
_wait_stop = None

def bind(**kw):
    """注入外部依赖；只覆盖显式传入且非 None 的键（幂等）。"""
    g = globals()
    for _k, _v in kw.items():
        if _v is not None:
            g[_k] = _v
    return True


# ============ 随拆搬入的状态 ============
_FC_STATE = {"spin_stopped": None}             # 已被提前收掉的「转圈」对象


def _fc_first(spin):
    """首个增量到达时：把「等待模型响应」的转圈提前收掉，把屏幕让给正文。"""
    def _cb():
        try:
            if spin is not None and not _fc_spin_done(spin):
                _wait_stop(spin, ok=True, label="模型开始输出")
                _FC_STATE["spin_stopped"] = spin
        except Exception:
            pass
    return _cb


def _fc_spin_done(spin):
    """该转圈是否已在首个增量时收掉（避免再落一行重复的结束语）。

    Wait.stop 本身可安全重复调用，但重复调用会**多落一行**结果文；
    这里显式判一下，动画窗口才干净。
    """
    return spin is not None and _FC_STATE.get("spin_stopped") is spin


def _fc_plain(client, model, messages, temperature, max_tokens, timeout, tools):
    """原来的非流式调用（回退路径，逐字等价于升级前的写法）。"""
    return client.chat.completions.create(
        model=model,
        messages=messages,
        temperature=temperature,
        max_tokens=max_tokens,
        timeout=timeout,
        tools=tools,
    )


def _fc_call(client, model, messages, temperature=0.7, max_tokens=None,
             timeout=None, tools=None, spin=None, verifier_guard=False):
    """主循环唯一的模型入口：能流就流，不能流就退回原样。

    返回：与非流式响应结构**完全兼容**的对象（.choices[0].message /
    .tool_calls / .finish_reason 都在），所以下游不用改。

    回退（不会造成重复输出）的场景：
      · stream_core 模块缺失 或 被 /stream off 关掉；
      · verifier_guard=True —— 最终答复要送去复核，必须拿全量文本再决定显不显示；
      · create() 阶段就炸了（一个字都还没吐出来）。
    不吞、也不重试的场景：
      · 流已经吐过字了才炸 —— 重试会重复输出，原样抛给主循环报错。
    """
    _FC_STATE["spin_stopped"] = None
    if _FC_SC is not None:
        # ★ 每轮进场先清零「已打印」：工具轮 / 异常轮不会消费这个标志，残留的
        #   True 会让本轮的**非流式回退**被误判成「正文已经打过了」而吞掉回复。
        _FC_SC.reset_turn()
    if _FC_SC is None or not _FC_SC.enabled() or verifier_guard:
        return _fc_plain(client, model, messages, temperature, max_tokens, timeout, tools)
    try:
        return _FC_SC.stream_chat(
            client, model, messages,
            temperature=temperature, max_tokens=max_tokens, timeout=timeout,
            tools=tools, out=sys.stdout, on_first=_fc_first(spin),
        )
    except Exception as _fc_e:
        if _FC_SC.last_printed():
            raise                    # 已经吐过字了：重试 = 重复输出，宁可报错
        log("[%s] 流式失败，自动回退非流式：%s: %s"
            % (_ts(), type(_fc_e).__name__, _fc_e))
        return _fc_plain(client, model, messages, temperature, max_tokens, timeout, tools)


def _fc_take_printed():
    """本轮正文是否已被实时打印（决定还要不要 print_ai 整段渲染）。"""
    try:
        return bool(_FC_SC.take_printed()) if _FC_SC is not None else False
    except Exception:
        return False


def _fc_finish_answer():
    """补上 print_ai 的那道收尾横线（已流式打印时才需要）。"""
    try:
        if _FC_SC is not None:
            _FC_SC.finish_answer()
    except Exception:
        pass


def _fc_handle_cmd(user_input):
    """处理 /stream 命令；返回 True 表示已消费该输入。"""
    if not (user_input == "/stream" or user_input.startswith("/stream ")):
        return False
    body = user_input[7:].strip().lower()

    if _FC_SC is None:
        print(paint("  ⚠️  找不到 stream_core.py，流式输出不可用（主程序已自动用非流式）。",
                    BR, BOLD))
        print(paint("     把它放到主程序同目录再重启即可。", BC, DIM))
        return True

    if body in ("", "status", "状态"):
        st = _FC_SC.stats()
        print(paint("  🌊 流式输出 [stream mode]：%s" % ("已开启 ON" if st["enable"] else "已关闭 OFF"),
                    BC, BOLD))
        print(paint("     · 思考链显示 [reasoning]：%s" % ("ON" if st["think"] else "OFF"),
                    BC, DIM))
        print(paint("     · 本会话已流式 %d 轮 ｜ 回退 %d 轮"
                    % (st["streamed_rounds"], st["fallback_rounds"]), BC, DIM))
        print(paint("     用法：/stream on | off | think on|off", BC, DIM))
        return True

    if body in ("on", "开", "开启", "1"):
        _FC_SC.set_enabled(True)
        print(paint("  🌊 流式输出已开启 [stream ON]：模型会边想边吐字", BG, BOLD))
        return True

    if body in ("off", "关", "关闭", "0"):
        _FC_SC.set_enabled(False)
        print(paint("  🐢 流式输出已关闭 [stream OFF]：回到整段输出（等全写完再显示）",
                    BY, BOLD))
        return True

    if body.startswith("think"):
        arg = body[5:].strip()
        if arg in ("on", "开", "开启", "1", ""):
            _FC_SC.set_think(True)
            print(paint("  💭 思考链显示：ON", BC, BOLD))
        elif arg in ("off", "关", "关闭", "0"):
            _FC_SC.set_think(False)
            print(paint("  💭 思考链显示：OFF", BC, BOLD))
        else:
            print(paint("     用法：/stream think on|off", BR, BOLD))
        return True

    print(paint("     用法：/stream [on|off|status|think on|off]", BR, BOLD))
    return True
