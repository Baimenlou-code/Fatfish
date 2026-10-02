# -*- coding: utf-8 -*-
"""fatfish_core.uicolors —— **界面配色的单一真源** / Single source of truth for colors

为什么会有这个文件
==================
配色原先散落在 6 张表里（THEME / TAG_FG / RAINBOW / A_FG / A_FG_B / A_BG / A_BG_B
+ 内联的 256 色基表），加上函数里零散的字面量，一共 54 种颜色、98 处出现。
结果是「改一处、漏一处」，注释之间还互相矛盾 —— 同一段代码上同时写着
「浅底（白底）可读的深色系」和「深底可读的亮色系」，那是白底皮肤与黑底皮肤
来回换肤留下的地层，没人收拾。

现在全部收敛到本文件：**改配色只改这里**。chat_window.py 不再出现任何颜色字面量。

取色依据（实测，不凭印象）
==========================
命令提示符（cmd）在本机实际显示的颜色，由两条独立证据交叉验证得到：

  ① Windows Terminal 的 settings.json 里 ``"schemes": []`` 且各 profile 未指定
     colorScheme → cmd 走内置默认方案 **Campbell**；
  ② ``HKCU\\Console`` 的 ``ColorTable00-15``（按控制台**原生 BGR 序**）解出的
     十六个值，与 Campbell 逐项一致。

所以下表的 16 色 = **你在命令提示符里真正看到的那 16 色**。

    控制台原生序 0-7 ：黑 暗蓝 暗绿 暗青 暗红 暗紫 暗黄 浅灰
    ANSI 码     30-37：黑  红   绿   黄   蓝   紫   青   白
    ↑ 两套顺序**不一样**（ANSI 把红放第 2 位，控制台把红放第 5 位）。
      下表已按 ANSI 码整理（因为代码里发的是 ANSI 码），直接取用即可。

怎么用
======
    from fatfish_core.uicolors import THEME, TAG_FG, RAINBOW, A_FG, A_FG_B, A_BG, A_BG_B

    · ``ANSI_FG / ANSI_FG_B / ANSI_BG / ANSI_BG_B`` 是正式名；
      ``A_FG / A_FG_B / A_BG / A_BG_B`` 是等价的短别名（窗口代码沿用旧称，
      避免为了换名字而大改 chat_window.py）。两者指向**同一个 dict 对象**。

窗口（chat_window.py）是唯一使用方；控制台那侧只发 ANSI 码，由终端按它自己的
调色板渲染 —— 因此窗口与控制台**天然一致**，不需要各调一套。
"""

# ============================================================
# 一、16 色基础调色板（命令提示符实测值 = Windows Terminal "Campbell"）
# ============================================================

#: 普通色：ANSI 30-37（前景）
ANSI_FG = {
    30: "#0C0C0C",   # 黑
    31: "#C50F1F",   # 红
    32: "#13A10E",   # 绿
    33: "#C19C00",   # 黄
    34: "#0037DA",   # 蓝
    35: "#881798",   # 紫
    36: "#3A96DD",   # 青
    37: "#CCCCCC",   # 白（浅灰）
}

#: 亮色：ANSI 90-97（前景）
ANSI_FG_B = {
    90: "#767676",   # 亮黑（暗灰）
    91: "#E74856",   # 亮红
    92: "#16C60C",   # 亮绿
    93: "#F9F1A5",   # 亮黄
    94: "#3B78FF",   # 亮蓝
    95: "#B4009E",   # 亮紫
    96: "#61D6D6",   # 亮青
    97: "#F2F2F2",   # 亮白
}

#: 背景码沿用同一批颜色：40-47 ↔ 30-37，100-107 ↔ 90-97
ANSI_BG = {40 + (k - 30): v for k, v in ANSI_FG.items()}
ANSI_BG_B = {100 + (k - 90): v for k, v in ANSI_FG_B.items()}

#: 短别名 —— 与上面的 ``ANSI_*`` 是**同一个对象**（窗口代码沿用旧称，不必改名）
A_FG = ANSI_FG
A_FG_B = ANSI_FG_B
A_BG = ANSI_BG
A_BG_B = ANSI_BG_B

#: 语义化的三个常用色（省得到处写 ANSI_FG[37]）
UI_FG = ANSI_FG[37]      # 正文色  #CCCCCC
UI_BG = ANSI_FG[30]      # 窗口底色 #0C0C0C
UI_DIM = ANSI_FG_B[90]   # 次要信息 #767676


# ============================================================
# 二、派生工具
# ============================================================

def mix(c1, c2, t):
    """把 c1 向 c2 混 t（0~1），返回 ``#RRGGBB``。

    用途：面板、边框、按钮这些「层次色」不该另定一套常量，
    而应该从底色与前景**算出来** —— 这样换底层配色时它们自动跟着走，
    不会再出现「改了 bg 忘了改 panel」的半吊子状态。
    """
    def _px(c):
        c = str(c).lstrip("#")
        return int(c[0:2], 16), int(c[2:4], 16), int(c[4:6], 16)

    try:
        a, b = _px(c1), _px(c2)
        t = max(0.0, min(1.0, float(t)))
        return "#%02X%02X%02X" % tuple(
            int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3)
        )
    except Exception:
        return c1


def xterm256(n):
    """xterm-256 色索引 → ``#RRGGBB``（与终端同一套算法，保证观感一致）。"""
    n = int(n)
    if n < 0:
        n = 0
    if n < 16:
        # xterm 序 0-7 = 普通色，8-15 = 亮色
        base = ([ANSI_FG[30 + i] for i in range(8)]
                + [ANSI_FG_B[90 + i] for i in range(8)])
        return base[n]
    if n < 232:
        n -= 16
        r, g, b = n // 36, (n % 36) // 6, n % 6
        conv = (lambda v: 0 if v == 0 else 55 + v * 40)
        return "#%02x%02x%02x" % (conv(r), conv(g), conv(b))
    v = 8 + (n - 232) * 10
    return "#%02x%02x%02x" % (v, v, v)


# ============================================================
# 三、界面主题（窗口各处都从这里取色）
# ============================================================
#
#   约定：
#     · 所有值都来自上面的调色板，或是用 mix() 派生出来的；
#     · 想整体换肤 → 只改「一、」的调色板 或 本表引用关系，不必碰窗口代码。

#: 输入区与对话区之间那根分割线。用户 2026-10-02 明确要求「一根**白线**」，
#: 后来又说过界面颜色难看 —— 那次的教训是「别拿自己的审美去盖用户的明确要求」。
#: 所以它**固定为纯白**，不参与任何自动派生。
WHITE_LINE = "#FFFFFF"

THEME = {
    "bg":          UI_BG,                  # 消息区底色 = cmd 的底色
    "panel":       mix(UI_BG, UI_FG, 0.06),  # 顶栏 / 底栏（比底色略亮，分出层次）
    "border":      mix(UI_BG, UI_FG, 0.22),
    "bubble_ai":   mix(UI_BG, UI_FG, 0.08),  # 键名沿用旧称（按钮等复用）
    "bubble_me":   mix(UI_BG, ANSI_FG[32], 0.45),   # 发送按钮（绿）
    "bubble_sys":  mix(UI_BG, UI_FG, 0.04),
    "text":        UI_FG,                  # 正文 = cmd 正文色
    "text_dim":    UI_DIM,                 # 时间 / 次要说明
    "accent":      ANSI_FG_B[96],          # 链接 / 强调 = 亮青
    "log_bg":      "#000000",              # 日志页底色（比消息区再沉一点）
    "log_text":    UI_FG,
    "input_bg":    mix(UI_BG, UI_FG, 0.08),  # 输入框（看得出边界）
    "btn":         mix(UI_BG, UI_FG, 0.15),
    "btn_hot":     mix(UI_BG, UI_FG, 0.24),
    "ask_bg":      mix(UI_BG, ANSI_FG_B[93], 0.16),  # 报批确认条：暖底（醒目不刺眼）
    "ask_border":  ANSI_FG_B[93],
    "streaming":   ANSI_FG_B[94],          # 流式进行中
    # ---- 说话人配色：一律用 cmd 原色，不再自己调橙/蓝 ----
    "nick_me":     ANSI_FG_B[97],          # 我：亮白
    "nick_ai":     ANSI_FG_B[96],          # 肥鱼：亮青
    "sep":         mix(UI_BG, UI_FG, 0.12),  # 消息之间的细线（低调）
    "sys_fg":      UI_DIM,                 # 系统消息 = 暗灰
    "code_bg":     mix(UI_BG, UI_FG, 0.07),  # 代码「框」底色
    "code_fg":     ANSI_FG_B[92],          # 代码字色 = 亮绿（与终端里一致）
    "code_border": mix(UI_BG, UI_FG, 0.28),  # 代码「框」边框
    "head_fg":     ANSI_FG_B[93],          # 标题 = 亮黄
    # ---- 控件（按钮等）：同样走派生，这里不留裸字面量 ----
    "btn_fg":      ANSI_FG_B[97],                  # 按钮文字（亮白）
    "btn_go_hot":  mix(UI_BG, ANSI_FG[32], 0.60),  # 发送按钮·悬停（更亮的绿）
    "btn_ask_hot": mix(UI_BG, UI_FG, 0.30),        # 确认条按钮·悬停
    "dim_fg":      UI_DIM,                         # ANSI SGR 2（暗色）的等效色
    "abort_fg":    ANSI_FG_B[91],                  # 「输出中断」标记（亮红）
    "divider":     WHITE_LINE,             # ★ 输入区 ↔ 对话区 的那根白线
}


# ============================================================
# 四、{{tag}} 标记 → 颜色
# ============================================================
#
#   必须与 ui_core.TAG_COLOR 指同一批颜色：
#   ui_core 把 {{red}} 映射成 ANSI **91**（亮红），所以这里取的也是亮红。
#   （这是 2026-10-02 那次「上色不对」的另一半原因：老表取的是自调的粉彩，
#     与控制台输出的 ANSI 码根本不是一个体系。）

TAG_FG = {
    "red":     ANSI_FG_B[91],
    "green":   ANSI_FG_B[92],
    "yellow":  ANSI_FG_B[93],
    "blue":    ANSI_FG_B[94],
    "magenta": ANSI_FG_B[95],
    "cyan":    ANSI_FG_B[96],
    "white":   ANSI_FG_B[97],
    "gray":    ANSI_FG_B[90],
    "grey":    ANSI_FG_B[90],
}

#: {{rainbow}} 用的色序（取调色板里的亮色，明度接近、过渡自然）
RAINBOW = [
    ANSI_FG_B[91], ANSI_FG_B[93], ANSI_FG_B[92], ANSI_FG_B[96],
    ANSI_FG_B[94], ANSI_FG_B[95], ANSI_FG_B[97],
]


__all__ = [
    # 调色板（正式名 + 短别名）
    "ANSI_FG", "ANSI_FG_B", "ANSI_BG", "ANSI_BG_B",
    "A_FG", "A_FG_B", "A_BG", "A_BG_B",
    # 语义色
    "UI_FG", "UI_BG", "UI_DIM", "WHITE_LINE",
    # 工具
    "mix", "xterm256",
    # 主题
    "THEME", "TAG_FG", "RAINBOW",
]
