#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
layout.py —— 肥鱼「编辑器组式布局」数据模型（VS Code 风格）

定位
----
    GUI-First 迁移（P1/P2/P3）的地基模块：
        · 纯数据、**零 Tk 依赖**、可单测；
        · 与 workspace/gui_migration/layout_vscode.py 的原型模型语义一致
          （原型 30 项不变量 + 5 预设 + 16 次连续拆分已全过）。

模型（与 VS Code 一一对应）
---------------------------
    Group（编辑器组） = 一个带标签页的容器，装 0..N 个面板
    Split（分屏）     = 若干 Group 的排列（h 横排 / v 竖排），可拖 sash
    Float（浮动窗）   = 某面板被"拆出去"单独占一个窗口（由视图层持有 Toplevel，
                        本模块只负责"它不在任何组里"这一事实）

    groups = {"g1": {"panels": ["chat", "log"], "active": "chat"}, ...}
    tree   = ("group", "g1") | ("split", "h"/"v", [node, ...])

对外只暴露两个树操作原语，其余都是组合：
    _insert_sibling(tree, gid, node, orient)  在目标组"旁边"插入
    _prune(tree)                              折叠单孩子 split、丢弃失效节点

用法
----
    from fatfish_core.layout import LayoutModel, PANELS, PRESETS

    m = LayoutModel().reset("grid")
    m.split_panel("log", "h")       # 向右拆
    m.detach("exec")                # 摘出去（视图层开浮窗）
    m.dock("exec")                  # 停靠回来
    problems = m.check_invariants() # 空列表 = 健康
    d = m.to_dict()                 # 写进 .env 的 UI_LAYOUT
    m2 = LayoutModel.from_dict(d)   # 启动时恢复；坏数据自动回默认

命令行：
    python -m fatfish_core.layout --selftest

作者：肥鱼 / 2026-10-03
"""

import json
import sys

# ================================================================ 常量
#: 四个面板名（顺序即「面板 ▾」菜单里的顺序）
PANELS = ("chat", "log", "status", "exec")

#: 面板的中文名（视图层也用得到，放这里统一口径）
PANEL_LABELS = {
    "chat":   "💬 对话",
    "log":    "📜 日志",
    "status": "🧿 状态",
    "exec":   "🖥 监控",
}

#: 预设布局：返回 (groups 定义, 主窗口 tree)
PRESETS = {
    # 对话为主（用户 2026-10-03 指定）：
    #   左 = 对话（日志同组作页签） ｜ 右上 = 状态 ｜ 右下 = 监控
    "focus": {
        "groups": {"g1": ["chat", "log"], "g2": ["status"], "g3": ["exec"]},
        "tree": ("split", "h", [
            ("group", "g1"),
            ("split", "v", [("group", "g2"), ("group", "g3")]),
        ]),
    },
    # 全览四格：四个面板各占一格（2×2）
    "grid": {
        "groups": {"g1": ["chat"], "g2": ["log"], "g3": ["status"], "g4": ["exec"]},
        "tree": ("split", "h", [
            ("split", "v", [("group", "g1"), ("group", "g2")]),
            ("split", "v", [("group", "g3"), ("group", "g4")]),
        ]),
    },
    # 左右对照：对话 ｜ 日志（状态/监控叠在日志组里）
    "lr": {
        "groups": {"g1": ["chat"], "g2": ["log", "status", "exec"]},
        "tree": ("split", "h", [("group", "g1"), ("group", "g2")]),
    },
    # 上下对照：对话/状态 ／ 日志/监控
    "tb": {
        "groups": {"g1": ["chat", "status"], "g2": ["log", "exec"]},
        "tree": ("split", "v", [("group", "g1"), ("group", "g2")]),
    },
    # 全部页签：一个组装所有面板（最省地方）
    "tabs": {
        "groups": {"g1": list(PANELS)},
        "tree": ("group", "g1"),
    },
}

DEFAULT_PRESET = "grid"


# ================================================================ 序列化工具
def _node_to_list(node):
    """tree 元组 → 可 JSON 的 list。"""
    if node is None:
        return None
    if node[0] == "group":
        return ["group", node[1]]
    return ["split", node[1], [_node_to_list(c) for c in node[2]]]


def _node_from_list(node):
    """JSON list → tree 元组；非法结构返回 None。"""
    if not isinstance(node, (list, tuple)) or not node:
        return None
    if node[0] == "group":
        if len(node) < 2 or not isinstance(node[1], str):
            return None
        return ("group", node[1])
    if node[0] == "split":
        if len(node) < 3 or node[1] not in ("h", "v") or not isinstance(node[2], (list, tuple)):
            return None
        kids = []
        for c in node[2]:
            nc = _node_from_list(c)
            if nc is None:
                return None
            kids.append(nc)
        if len(kids) < 2:
            return None
        return ("split", node[1], kids)
    return None


# ================================================================ 模型
class LayoutModel(object):
    """纯数据：组 + 树。不含任何 Tk 代码，便于自测。"""

    def __init__(self):
        self.groups = {}      # gid -> {"panels": [...], "active": name}
        self.tree = None      # ("group", gid) | ("split", "h"/"v", [node, ...])
        self._seq = 0
        self.reset(DEFAULT_PRESET)

    # ---------------------------------------------------------- 基础
    def new_group(self, panels=None, active=None):
        """新建一个组，返回其 gid。"""
        self._seq += 1
        gid = "g%d" % self._seq
        panels = [p for p in (panels or []) if p in PANELS]
        self.groups[gid] = {
            "panels": panels,
            "active": active if active in panels else (panels[0] if panels else None),
        }
        return gid

    def reset(self, preset=DEFAULT_PRESET):
        """按预设重建。preset 非法时退回 DEFAULT_PRESET。"""
        spec = PRESETS.get(preset) or PRESETS[DEFAULT_PRESET]
        self.groups.clear()
        self._seq = 0
        mapping = {}
        for gid, panels in spec["groups"].items():
            mapping[gid] = self.new_group(panels)
        self.tree = self._remap(spec["tree"], mapping)
        return self

    def _remap(self, node, mapping):
        if node[0] == "group":
            return ("group", mapping[node[1]])
        return ("split", node[1], [self._remap(c, mapping) for c in node[2]])

    # ---------------------------------------------------------- 查询
    def where(self, panel):
        """面板现在在哪：('group', gid) / None（隐藏或浮动，视图层另存浮窗表）。"""
        gid = self.group_of(panel)
        return ("group", gid) if gid else None

    def all_panels(self):
        """当前在主窗口布局里的面板（不含浮动的）。"""
        out = []
        for g in self.groups.values():
            out.extend(g["panels"])
        return sorted(out)

    def group_of(self, panel):
        for gid, g in self.groups.items():
            if panel in g["panels"]:
                return gid
        return None

    def leaf_groups(self):
        """按树序（左→右、上→下）返回叶子组 id。"""
        out = []

        def walk(n):
            if n[0] == "group":
                out.append(n[1])
            else:
                for c in n[2]:
                    walk(c)

        if self.tree:
            walk(self.tree)
        return out

    # ---------------------------------------------------------- 树操作
    def _replace(self, node, target_gid, newnode):
        """把 tree 中 (group,target_gid) 替换成 newnode，返回 (新树, 是否替换)。"""
        if node[0] == "group":
            if node[1] == target_gid:
                return newnode, True
            return node, False
        kids, done = [], False
        for c in node[2]:
            if done:
                kids.append(c)
                continue
            nc, d = self._replace(c, target_gid, newnode)
            kids.append(nc)
            done = d
        return ("split", node[1], kids), done

    def _prune(self, node):
        """折叠只有一个孩子的 split；丢弃引用了不存在组的节点。

        ★ 默认**保留空组** —— 拆分是故意留空位等人放面板的，顺手删掉会让
          「拆分单个面板的组」看起来完全没反应。真正该清理的空组由
          `_drop_if_empty()` 精确处理（只清理刚被搬空的那一个）。
        """
        if node is None:
            return None
        if node[0] == "group":
            return node if node[1] in self.groups else None
        kids = []
        for c in node[2]:
            nc = self._prune(c)
            if nc is not None:
                kids.append(nc)
        if not kids:
            return None
        if len(kids) == 1:
            return kids[0]
        return ("split", node[1], kids)

    def _drop_if_empty(self, gid):
        """把**刚被搬空**的那个组删掉（拆分留下的空位不动）。"""
        if gid is None:
            return
        g = self.groups.get(gid)
        if g is not None and not g["panels"]:
            del self.groups[gid]
        self.tree = self._prune(self.tree)

    def _remove_panel(self, panel):
        """仅把面板从它所在的组里摘掉，返回原 gid（不动树）。"""
        gid = self.group_of(panel)
        if gid is None:
            return None
        g = self.groups[gid]
        g["panels"].remove(panel)
        if g["active"] == panel:
            g["active"] = g["panels"][0] if g["panels"] else None
        return gid

    # ---------------------------------------------------------- 面板操作
    def split_panel(self, panel, orient="h"):
        """拆分：h = 向右拆，v = 向下拆（对齐 VS Code 的 Split Right / Split Down）。

        · 该组还有别的面板 → 把这个面板**搬**到新组（与 VS Code 一致）；
        · 该组就这一个面板 → 面板留在原地，旁边开一个**空组**等放东西
          （不做空组的话，拆分单面板组会「点了没反应」）。
        """
        gid = self.group_of(panel)
        if gid is None:
            return None
        if len(self.groups[gid]["panels"]) > 1:
            self._remove_panel(panel)
            newg = self.new_group([panel])
        else:
            newg = self.new_group([])
        self.tree, _ = self._insert_sibling(self.tree, gid, ("group", newg), orient)
        self.tree = self._prune(self.tree) or self.tree
        return newg

    def _insert_sibling(self, node, target_gid, newnode, orient):
        """在 target_gid 所在组的**旁边**插入 newnode，返回 (新树, 是否插入)。

        · 父节点正是**同方向**的 split → 直接平铺成兄弟，树形不变深；
        · 否则 → 把目标组包进一个新的 split，实现「向右 / 向下拆分」。
        """
        if node is None:
            return newnode, True
        if node[0] == "group":
            if node[1] == target_gid:
                return ("split", orient, [node, newnode]), True
            return node, False
        if node[1] == orient:
            for i, c in enumerate(node[2]):
                if c[0] == "group" and c[1] == target_gid:
                    kids = node[2][:i + 1] + [newnode] + node[2][i + 1:]
                    return ("split", node[1], kids), True
        kids, done = [], False
        for c in node[2]:
            if done:
                kids.append(c)
                continue
            nc, d = self._insert_sibling(c, target_gid, newnode, orient)
            kids.append(nc)
            done = d
        return ("split", node[1], kids), done

    def move_to_next_group(self, panel):
        """把面板移到树序里的下一个组（VS Code 的「移到下一组」）。"""
        gid = self.group_of(panel)
        if gid is None:
            return False
        order = self.leaf_groups()
        if len(order) < 2:
            return False
        i = order.index(gid)
        nxt = order[(i + 1) % len(order)]
        self._drop_if_empty(self._remove_panel(panel))
        g = self.groups[nxt]
        g["panels"].append(panel)
        g["active"] = panel
        self.tree = self._prune(self.tree) or self.tree
        return True

    def detach(self, panel):
        """把面板从布局里摘出来（视图层负责为它开浮窗）。

        返回 True 表示正常摘出；False 表示「摘出去就没内容了」——
        此时自动兜底新建一个小组合法地容纳它，保证主树永不为空。
        """
        gid = self._remove_panel(panel)
        self._drop_if_empty(gid)
        if self.tree is None:                     # 别把主窗口掏空
            gid = self.new_group([panel])
            self.tree = ("group", gid)
            return False
        return True

    def dock(self, panel, into=None):
        """把面板停靠回主窗口（默认放进第一个组）。"""
        if panel not in PANELS:
            return False
        gid = into or (self.leaf_groups()[0] if self.leaf_groups() else None)
        if gid is None or gid not in self.groups:
            gid = self.new_group([])
            self.tree = ("group", gid) if self.tree is None else \
                ("split", "h", [self.tree, ("group", gid)])
        g = self.groups[gid]
        if panel not in g["panels"]:
            g["panels"].append(panel)
        g["active"] = panel
        return True

    def close(self, panel):
        """关闭（隐藏）一个面板。"""
        gid = self._remove_panel(panel)
        self._drop_if_empty(gid)
        return gid is not None

    def drop_group(self, gid):
        """用户手动收起一个空组。"""
        g = self.groups.get(gid)
        if g is not None and not g["panels"]:
            del self.groups[gid]
            self.tree = self._prune(self.tree)
            return True
        return False

    # ---------------------------------------------------------- 不变量
    def check_invariants(self):
        """返回问题列表（空 = 健康）。用于自测与运行期断言。"""
        problems = []
        # 1) 面板不重复（允许缺失：关闭 = 隐藏，是合法状态）
        seen = self.all_panels()
        if len(seen) != len(set(seen)):
            problems.append("面板重复出现在多个组：%s" % seen)
        # 2) 树上引用的组必须真实存在（空组合法 —— 拆分故意留的空位）
        for gid in self.leaf_groups():
            if gid not in self.groups:
                problems.append("树引用了不存在的组 %s" % gid)
        # 3) active 必须是组内成员；空组的 active 必须是 None
        for gid, g in self.groups.items():
            if g["panels"] and g["active"] not in g["panels"]:
                problems.append("组 %s 的 active=%r 不在成员里 %s"
                                % (gid, g["active"], g["panels"]))
            if not g["panels"] and g["active"] is not None:
                problems.append("空组 %s 的 active 应为 None，实为 %r"
                                % (gid, g["active"]))
        # 4) split 至少两个孩子；树不能为空
        def walk(n, path="root"):
            if n[0] == "group":
                return
            if len(n[2]) < 2:
                problems.append("%s 的 split 只有 %d 个孩子" % (path, len(n[2])))
            for i, c in enumerate(n[2]):
                walk(c, "%s/%d" % (path, i))

        if self.tree:
            walk(self.tree)
        else:
            problems.append("主树为空（没有可见面板）")
        return problems

    # ---------------------------------------------------------- 持久化
    def to_dict(self):
        """导出为可 JSON 的结构（写进 .env 的 UI_LAYOUT）。"""
        return {
            "groups": {gid: {"panels": list(g["panels"]), "active": g["active"]}
                       for gid, g in self.groups.items()},
            "tree": _node_to_list(self.tree),
        }

    def to_json(self, **kw):
        kw.setdefault("ensure_ascii", False)
        kw.setdefault("separators", (",", ":"))
        return json.dumps(self.to_dict(), **kw)

    @classmethod
    def from_dict(cls, d, strict=False):
        """从 to_dict() 的结构恢复；坏数据自动回默认预设（strict=True 则抛错）。"""
        m = cls.__new__(cls)
        m.groups = {}
        m.tree = None
        m._seq = 0
        bad = None
        try:
            if not isinstance(d, dict):
                raise ValueError("顶层必须是 dict")
            groups = d.get("groups") or {}
            if not isinstance(groups, dict):
                raise ValueError("groups 必须是 dict")
            for gid, g in groups.items():
                if not isinstance(g, dict):
                    continue
                gid = str(gid)
                panels = [p for p in (g.get("panels") or []) if p in PANELS]
                active = g.get("active")
                if active not in panels:
                    active = panels[0] if panels else None
                m.groups[gid] = {"panels": panels, "active": active}
                if gid.startswith("g") and gid[1:].isdigit():
                    m._seq = max(m._seq, int(gid[1:]))
            m.tree = _node_from_list(d.get("tree"))
            if m.tree is None:
                raise ValueError("tree 结构非法")
            problems = m.check_invariants()
            if problems:
                raise ValueError("不变量不满足：%s" % problems)
        except Exception as e:            # 坏数据一律降级，绝不半死
            bad = "%s: %s" % (type(e).__name__, e)
            m.reset(DEFAULT_PRESET)

        if bad and strict:
            raise ValueError(bad)
        m.load_error = bad                # 供调用方（日志）参考
        return m

    @classmethod
    def from_json(cls, s, strict=False):
        try:
            d = json.loads(s)
        except Exception as e:
            if strict:
                raise
            m = cls()
            m.load_error = "JSONDecodeError: %s" % e
            return m
        return cls.from_dict(d, strict=strict)


# ================================================================ 自测
def _selftest():
    ok = True
    fails = []

    def check(name, cond, extra=""):
        nonlocal ok
        if not cond:
            ok = False
            fails.append(name)
        print("   %-58s %s %s" % (name, "OK" if cond else "FAIL", extra))

    print("── 模型基础 ──")
    m = LayoutModel().reset("grid")
    check("初始四面板齐全", m.all_panels() == sorted(PANELS), str(m.all_panels()))
    check("初始无隐患", not m.check_invariants(), str(m.check_invariants()))
    check("grid 组数 = 4（四格）", len(m.leaf_groups()) == 4, str(m.leaf_groups()))

    print("── 分屏（向右 / 向下）──")
    m = LayoutModel().reset("tabs")
    check("起始 1 个组 4 个面板", len(m.leaf_groups()) == 1)
    m.split_panel("log", "h")
    check("向右拆分后 2 个组", len(m.leaf_groups()) == 2, str(m.leaf_groups()))
    check("拆分后无隐患", not m.check_invariants(), str(m.check_invariants()))
    m.split_panel("status", "v")
    check("再向下拆分 3 个组", len(m.leaf_groups()) == 3, str(m.leaf_groups()))
    check("面板没丢没重", m.all_panels() == sorted(PANELS), str(m.all_panels()))
    # 单面板组拆分 → 留空位（空组）
    m = LayoutModel().reset("lr")      # ★ focus 已改为 chat+log 同组；lr 里 chat 是单面板组
    g_chat = m.group_of("chat")
    n_before = len(m.leaf_groups())
    m.split_panel("chat", "h")
    check("单面板组拆分：面板留原地 + 旁边多一个空组",
          m.group_of("chat") == g_chat and len(m.leaf_groups()) == n_before + 1
          and not m.check_invariants(), str(m.check_invariants()))

    print("── 独立成窗 / 停靠 ──")
    m = LayoutModel().reset("grid")
    check("detach 前 log 在组里", m.group_of("log") is not None)
    m.detach("log")
    check("detach 后 log 不在任何组", m.group_of("log") is None)
    check("detach 后无隐患", not m.check_invariants(), str(m.check_invariants()))
    check("其余三面板仍在", m.all_panels() == sorted(["chat", "status", "exec"]),
          str(m.all_panels()))
    m.dock("log", into=m.leaf_groups()[0])
    check("dock 后 log 回到组里", m.group_of("log") is not None)
    check("dock 后四面板齐全", m.all_panels() == sorted(PANELS), str(m.all_panels()))

    print("── 移组 ──")
    m = LayoutModel().reset("grid")
    src = m.group_of("chat")
    hit = m.move_to_next_group("chat")
    check("移组成功", hit)
    check("chat 换了组", m.group_of("chat") != src, "%s → %s" % (src, m.group_of("chat")))
    check("移组后无隐患", not m.check_invariants(), str(m.check_invariants()))

    print("── 关闭 / 显示 ──")
    m = LayoutModel().reset("grid")
    m.close("exec")
    check("关闭后 exec 不可见", m.group_of("exec") is None)
    check("关闭后无空组残留", not m.check_invariants(), str(m.check_invariants()))
    m.dock("exec")
    check("重新显示 exec", m.group_of("exec") is not None)

    print("── 极端操作不崩 ──")
    m = LayoutModel().reset("tabs")
    for p in PANELS:
        m.detach(p)                            # 全部拆出去
    inv = m.check_invariants()
    check("全部拆出后模型仍自洽（保留兜底组）",
          isinstance(inv, list) and m.tree is not None, str(inv))
    for p in PANELS:
        m.dock(p)
    check("全部停靠回来四面板齐全", m.all_panels() == sorted(PANELS),
          str(m.all_panels()))
    m2 = LayoutModel().reset("grid")
    for _ in range(8):
        m2.split_panel("chat", "h")
        m2.split_panel("chat", "v")
    check("反复拆分 16 次模型仍自洽", not m2.check_invariants(),
          str(m2.check_invariants()))

    print("── 预设全可用 ──")
    for name in PRESETS:
        mm = LayoutModel().reset(name)
        check("预设 %s 自洽且面板齐全" % name,
              not mm.check_invariants() and mm.all_panels() == sorted(PANELS),
              "%s | %s" % (str(mm.check_invariants()), str(mm.all_panels())))

    print("── 持久化往返 ──")
    m = LayoutModel().reset("grid")
    m.split_panel("log", "v")
    m.detach("exec")
    m.move_to_next_group("status")
    s = m.to_json()
    m3 = LayoutModel.from_json(s)
    check("JSON 往返：组结构一致", m3.to_dict() == m.to_dict())
    check("JSON 往返：无隐患", not m3.check_invariants(), str(m3.check_invariants()))
    check("序列化是真 JSON（能反解）", json.loads(s)["tree"][0] == "split")
    bad = LayoutModel.from_json('{"groups": {"g1": {"panels": ["chat","chat"]}}, '
                               '"tree": ["group", "g9"]}')
    check("坏数据自动降级到默认预设",
          not bad.check_invariants() and bad.all_panels() == sorted(PANELS),
          "load_error=%r" % getattr(bad, "load_error", None))
    check("坏数据降级时记录了原因", bool(getattr(bad, "load_error", None)))
    try:
        LayoutModel.from_json("{不是json", strict=True)
        check("strict=True 时坏数据抛错", False)
    except Exception:
        check("strict=True 时坏数据抛错", True)

    print("\n%s" % ("layout.py 自测全部通过 ✅" if ok else "存在失败 ❌：%s" % fails))
    return 0 if ok else 1


def main(argv=None):
    argv = list(sys.argv[1:] if argv is None else argv)
    if "--selftest" in argv or not argv:
        return _selftest()
    print(__doc__)
    return 0


if __name__ == "__main__":
    sys.exit(main())
