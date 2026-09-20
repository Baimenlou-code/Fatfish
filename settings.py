# -*- coding: utf-8 -*-
"""
settings.py —— 肥鱼「统一设置中心」

设计目标
--------
1. **集中注册**：所有可调参数在一个地方登记，带默认值、类型、说明、分组。
2. **默认值快照**：注册时记下「出厂默认」，`/set reset` 可一键还原。
3. **即时生效**：改完立刻作用到运行中的程序（通过 apply 回调）。
4. **可选持久化**：`/set save` 把当前值写回 .env，重启后仍生效。
5. **纯本地**：不依赖主程序，可单独 import 做测试。

用法（在主程序里）
------------------
    import settings

    settings.register(
        "model", MODEL,
        desc="主模型名（执行者）", group="主模型", type=str,
        apply=lambda v: _apply_model(v),       # 传 None 表示「仅存值，由主程序自行读取」
    )
    ...
    settings.set("model", "deepseek-chat")     # 改值 + 应用 + 记录
    settings.reset("model")                    # 还原为出厂默认
    settings.reset()                           # 全部还原
    settings.snapshot()                        # 导出当前值（dict）

三个概念要分清
--------------
    default  —— 出厂默认（代码里写死的值，注册那一刻捕获）
    current  —— 当前生效值（可能被 .env 或 /set 改过）
    dirty    —— 是否偏离出厂默认（用于 /set 里标 *）
"""

import json
import os
from datetime import datetime

# ============ 注册表 ============
_REGISTRY = {}       # key -> Record


class Record:
    """单个设置项的元数据。"""

    __slots__ = ("key", "default", "value", "desc", "group", "type",
                 "choices", "apply", "env_name", "advanced")

    def __init__(self, key, default, desc="", group="其他", type=str,
                 choices=None, apply=None, env_name=None, advanced=False):
        self.key = key
        self.default = default          # 出厂默认（快照，不可变）
        self.value = default            # 当前值
        self.desc = desc
        self.group = group
        self.type = type                # str / int / float / bool
        self.choices = choices          # 可选：允许的取值集合
        self.apply = apply              # 回调：把值应用到程序（None=只存值）
        self.env_name = env_name        # 可选：对应的 .env 键名（供 save）
        self.advanced = advanced        # 是否「进阶」项（默认不在简表里显示）

    @property
    def dirty(self):
        """当前值是否偏离出厂默认。"""
        return self.value != self.default

    def coerce(self, raw):
        """把外部输入（字符串）转换成该设置项应有的类型。失败抛 ValueError。"""
        if self.type is bool:
            if isinstance(raw, bool):
                return raw
            s = str(raw).strip().lower()
            if s in ("1", "true", "yes", "on", "y", "开"):
                return True
            if s in ("0", "false", "no", "off", "n", "关"):
                return False
            raise ValueError(f"布尔值只接受 on/off（收到 {raw!r}）")
        if self.type is int:
            try:
                return int(str(raw).strip())
            except ValueError:
                raise ValueError(f"需要整数（收到 {raw!r}）")
        if self.type is float:
            try:
                return float(str(raw).strip())
            except ValueError:
                raise ValueError(f"需要数字（收到 {raw!r}）")
        return str(raw).strip()

    def validate(self, value):
        """范围 / 取值校验。返回 (ok, 错误说明)。"""
        if self.choices and value not in self.choices:
            return False, f"可选值：{' / '.join(str(c) for c in self.choices)}"
        if self.type is int and not self.choices:
            if value < 0:
                return False, "不能为负数"
        if self.type is float:
            if value < 0:
                return False, "不能为负数"
        return True, ""

    def fmt(self, v=None):
        """把值格式化为易读字符串。"""
        v = self.value if v is None else v
        if isinstance(v, bool):
            return "on" if v else "off"
        if v == "":
            return "（空）"
        return str(v)


def register(key, default, desc="", group="其他", type=str,
             choices=None, apply=None, env_name=None, advanced=False):
    """登记一个设置项。重复登记会覆盖（便于测试）。返回 Record。"""
    rec = Record(key, default, desc=desc, group=group, type=type,
                 choices=choices, apply=apply, env_name=env_name,
                 advanced=advanced)
    _REGISTRY[key] = rec
    return rec


def has(key):
    return key in _REGISTRY


def get(key, default=None):
    rec = _REGISTRY.get(key)
    return rec.value if rec else default


def record(key):
    return _REGISTRY.get(key)


def keys():
    return list(_REGISTRY.keys())


def groups():
    """返回 {group: [Record, ...]}，保持注册顺序。"""
    out = {}
    for rec in _REGISTRY.values():
        out.setdefault(rec.group, []).append(rec)
    return out


def set(key, raw, persist=False):
    """设置一个值。

    返回 (ok: bool, msg: str)
    流程：查表 → 类型转换 → 校验 → 应用（apply 回调）→ 记录
    校验或应用失败时不改动当前值。
    """
    rec = _REGISTRY.get(key)
    if not rec:
        near = [k for k in _REGISTRY if key.lower() in k.lower()]
        hint = f"，是不是想输入：{' / '.join(near[:3])}？" if near else ""
        return False, f"未找到设置项 [{key}]{hint}"

    try:
        value = rec.coerce(raw)
    except ValueError as e:
        return False, str(e)

    ok, err = rec.validate(value)
    if not ok:
        return False, err

    old = rec.value
    if rec.apply:
        try:
            rec.apply(value)
        except Exception as e:                       # 应用失败 → 回滚
            rec.value = old
            return False, f"应用失败（已回滚）：{type(e).__name__}: {e}"

    rec.value = value
    note = "" if old == value else f"（{rec.fmt(old)} → {rec.fmt(value)}）"
    extra = ""
    if persist and rec.env_name:
        pok, pmsg = save_to_env([key])
        extra = "；" + pmsg if pok else f"；⚠️ 持久化失败：{pmsg}"
    return True, f"{key} = {rec.fmt(value)}{note}{extra}"


def reset(key=None, persist=False):
    """把设置项还原为出厂默认。key=None 表示全部还原。

    返回 (ok, msg, changed_keys)
    """
    if key:
        recs = [_REGISTRY[key]] if key in _REGISTRY else []
        if not recs:
            return False, f"未找到设置项 [{key}]", []
    else:
        recs = list(_REGISTRY.values())

    changed, failed = [], []
    for rec in recs:
        if rec.value == rec.default and rec.apply is None:
            continue
        old = rec.value
        if rec.apply:
            try:
                rec.apply(rec.default)
            except Exception as e:
                failed.append(f"{rec.key}（{type(e).__name__}）")
                continue
        rec.value = rec.default
        if old != rec.default:
            changed.append(rec.key)

    scope = key or "全部"
    if failed:
        msg = f"已还原 {scope}：成功 {len(changed)} 项，失败 {len(failed)} 项（{', '.join(failed)}）"
    elif changed:
        msg = f"已还原 {scope} 为出厂默认：{', '.join(changed)}"
    else:
        msg = f"{scope} 本就是出厂默认，无需还原"
    if persist and changed:
        ok, pmsg = save_to_env(changed)
        msg += "；" + pmsg
    return True, msg, changed


def snapshot(only_dirty=False):
    """导出当前值 {key: value}。"""
    return {k: r.value for k, r in _REGISTRY.items()
            if not only_dirty or r.dirty}


def diff():
    """返回偏离出厂默认的项：[(key, default, current), ...]"""
    return [(r.key, r.default, r.value) for r in _REGISTRY.values() if r.dirty]


# ============ 预设方案（Profile）============
_PROFILES = {}       # name -> {"desc": str, "icon": str, "values": {key: value}}
_PROFILE_ALIAS = {}  # 别名 -> 正式名


def register_profile(name, desc, values, icon="📦", aliases=()):
    """登记一套预设方案。

    values: {设置项名: 目标值}。未列出的项**保持不动**（不做归零）。
    想「先还原再套用」请用 apply_profile(name, reset_first=True)。
    """
    _PROFILES[name] = {"desc": desc, "icon": icon, "values": dict(values or {})}
    for a in aliases:
        _PROFILE_ALIAS[str(a).lower()] = name
    return name


def profiles():
    """返回 {name: info}。"""
    return _PROFILES


def resolve_profile(name):
    """把别名解析成正式方案名。找不到返回 None。"""
    if not name:
        return None
    n = str(name).strip()
    if n in _PROFILES:
        return n
    low = n.lower()
    if low in _PROFILE_ALIAS:
        return _PROFILE_ALIAS[low]
    # 前缀匹配
    cand = [p for p in _PROFILES if p.lower().startswith(low)] if low else []
    return cand[0] if len(cand) == 1 else None


def apply_profile(name, reset_first=False):
    """套用一套预设方案。

    返回 (ok, msg, applied: [(key, old, new)], failed: [(key, err)])
    """
    key = resolve_profile(name)
    if not key:
        near = " / ".join(_PROFILES.keys())
        return False, f"未找到预设方案 [{name}]（可选：{near}）", [], []

    info = _PROFILES[key]
    values = info["values"]

    # 校验所有项都存在且值合法（先全查，避免改一半）
    problems = []
    for k, v in values.items():
        rec = _REGISTRY.get(k)
        if not rec:
            problems.append(f"{k}（未注册）")
            continue
        try:
            cv = rec.coerce(v)
        except ValueError as e:
            problems.append(f"{k}（{e}）")
            continue
        ok, err = rec.validate(cv)
        if not ok:
            problems.append(f"{k}（{err}）")
    if problems:
        return False, "方案定义有误：" + "；".join(problems), [], []

    if reset_first:
        _, _, rchanged = reset()
    else:
        rchanged = []

    applied, failed = [], []
    for k, v in values.items():
        rec = _REGISTRY[k]
        old = rec.value
        ok, msg = set(k, v)
        if ok:
            if old != rec.value:
                applied.append((k, old, rec.value))
        else:
            failed.append((k, msg))

    icon = info.get("icon", "📦")
    if failed:
        msg = f"{icon} 已套用 [{key}]：成功 {len(applied)} 项，失败 {len(failed)} 项"
        msg += "（" + "；".join(f"{k}: {e}" for k, e in failed) + "）"
    elif applied:
        msg = f"{icon} 已套用 [{key}]：{info['desc']}，共改动 {len(applied)} 项"
        if rchanged:
            msg += f"（另已先还原 {len(rchanged)} 项）"
    elif rchanged:
        msg = f"{icon} 已还原为出厂默认，共 {len(rchanged)} 项"
        if values:
            msg += f"；[{key}] 的目标值已全部满足"
    else:
        msg = f"{icon} [{key}] 的取值与当前设置一致，无需改动"
    return True, msg, applied, failed


def render_profiles():
    """渲染预设方案列表。"""
    if not _PROFILES:
        return "（未登记任何预设方案）"
    lines = ["  可用预设方案 [profiles]："]
    for name, info in _PROFILES.items():
        icon = info.get("icon", "📦")
        n = len(info["values"])
        lines.append(f"    {icon} {name:<12} {info['desc']}（{n} 项）")
    lines.append("")
    lines.append("  用法：/set profile <名>          套用（只改方案涉及的项）")
    lines.append("        /set profile <名> --reset  先全部还原，再套用（干净基线）")
    lines.append("        /set profile <名> --show   只看该方案会改什么，不执行")
    return "\n".join(lines)


def profile_preview(name):
    """预览一套方案会做什么改动。"""
    key = resolve_profile(name)
    if not key:
        return f"（未找到预设方案 {name}）"
    info = _PROFILES[key]
    lines = [f"  {info.get('icon', '📦')} {key} —— {info['desc']}",
             f"  将改动以下 {len(info['values'])} 项（→ 目标值 / 现值）："]
    for k, v in info["values"].items():
        rec = _REGISTRY.get(k)
        if not rec:
            lines.append(f"    ⚠️  {k:<26} → {v}   （未注册）")
            continue
        try:
            cv = rec.coerce(v)
            target = rec.fmt(cv)
        except ValueError:
            target = f"{v}（非法）"
        cur = rec.fmt()
        mark = "＝" if cur == target else "→"
        star = " " if cur == target else "*"
        lines.append(f"    {star} {k:<26} {cur:<14} {mark} {target}")
    lines.append("")
    lines.append("  （标 * 的项会被改动；用 /set profile %s 执行）" % key)
    return "\n".join(lines)


# ============ .env 持久化 ============
def _env_path():
    return os.path.join(os.path.dirname(os.path.abspath(__file__)), ".env")


def save_to_env(keys=None):
    """把设置值写回 .env（就地替换已有键，不存在则追加到文件末尾）。

    只写「有 env_name 且当前值 != 出厂默认」的项，避免把一大堆等同默认的
    配置刷进 .env。返回 (ok, msg)。
    """
    path = _env_path()
    targets = []
    for rec in _REGISTRY.values():
        if not rec.env_name:
            continue
        if keys is not None and rec.key not in keys:
            continue
        if not rec.dirty:
            continue
        targets.append((rec.key, rec.env_name, rec))

    if not targets:
        return True, "无需写入 .env（没有偏离默认的设置）"

    try:
        with open(path, "r", encoding="utf-8") as f:
            lines = f.read().splitlines()
    except OSError:
        lines = []

    written = []
    for key, env_name, rec in targets:
        val = rec.fmt()
        # 优先用 env_name 精确匹配
        hit = None
        for i, ln in enumerate(lines):
            s = ln.strip()
            if s.startswith("#") or "=" not in s:
                continue
            if s.split("=", 1)[0].strip() == env_name:
                hit = i
                break
        newline = f"{env_name}={val}"
        if hit is not None:
            lines[hit] = newline
        else:
            lines.append(newline)
        written.append(key)

    try:
        with open(path, "w", encoding="utf-8") as f:
            f.write("\n".join(lines).rstrip() + "\n")
    except OSError as e:
        return False, f"写入 .env 失败：{e}"

    return True, f"已写入 .env：{', '.join(written)}"


# ============ 展示 ============
GROUP_ICON = {
    "主模型": "🧠", "核验": "🧿", "联网": "🌐", "报批": "🔐",
    "界面": "🖥", "上下文": "📏", "其他": "⚙️",
}


def render(key=None, show_all=False):
    """渲染设置表。key=None 显示全部；show_all=False 时隐藏进阶项。"""
    if key:
        rec = _REGISTRY.get(key)
        if not rec:
            return f"（未找到设置项 {key}）"
        star = " *" if rec.dirty else ""
        lines = [f"  {rec.key}{star}",
                 f"    当前值 [value]  ：{rec.fmt()}",
                 f"    出厂默认 [default]：{rec.fmt(rec.default)}",
                 f"    类型 [type]     ：{rec.type.__name__}",
                 f"    说明 [desc]     ：{rec.desc}"]
        if rec.choices:
            lines.append(f"    可选值 [choices]：" + " / ".join(str(c) for c in rec.choices))
        if rec.env_name:
            lines.append(f"    .env 键 [env]   ：{rec.env_name}")
        return "\n".join(lines)

    out = []
    for group, recs in groups().items():
        shown = [r for r in recs if show_all or not r.advanced]
        if not shown:
            continue
        icon = GROUP_ICON.get(group, "•")
        out.append(f"  {icon} {group}")
        for r in shown:
            star = "*" if r.dirty else " "
            out.append(f"    {star} {r.key:<26} = {r.fmt():<14} {r.desc}")
    dirty = diff()
    out.append("")
    if dirty:
        out.append(f"  * 表示已偏离出厂默认（共 {len(dirty)} 项），用 /set reset 还原")
    else:
        out.append("  全部为出厂默认")
    if not show_all:
        out.append("  用 /set all 显示全部（含进阶项）")
    return "\n".join(out)


def render_diff():
    """只显示偏离默认的项。"""
    d = diff()
    if not d:
        return "  全部为出厂默认，没有需要还原的项。"
    lines = ["  以下项已偏离出厂默认："]
    for key, default, cur in d:
        rec = _REGISTRY[key]
        lines.append(f"    {key:<26} {rec.fmt(default):<14} → {rec.fmt(cur)}")
    return "\n".join(lines)


# ============ 自检 ============
def _selftest():
    import io
    try:
        import sys
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    fails = []

    def check(name, cond):
        print(("  ✅ " if cond else "  ❌ ") + name)
        if not cond:
            fails.append(name)

    print("=" * 66)
    print("settings 自检开始")
    print("=" * 66)

    applied = {}
    def _mk_applier(k):
        return lambda v: applied.__setitem__(k, v)

    register("t_int", 10, desc="整数", group="测试", type=int, apply=_mk_applier("t_int"))
    register("t_str", "abc", desc="字符串", group="测试", type=str, apply=_mk_applier("t_str"))
    register("t_bool", True, desc="布尔", group="测试", type=bool, apply=_mk_applier("t_bool"))
    register("t_choice", "auto", desc="枚举", group="测试", type=str,
             choices=["auto", "on", "off"], apply=_mk_applier("t_choice"))

    ok, msg = set("t_int", "42")
    check("整数设置", ok and get("t_int") == 42 and applied.get("t_int") == 42)

    ok, msg = set("t_int", "abc")
    check("类型错误被拒", (not ok) and get("t_int") == 42)

    ok, msg = set("t_int", "-5")
    check("负数被拒", not ok)

    ok, msg = set("t_bool", "off")
    check("布尔 off", ok and get("t_bool") is False)

    ok, msg = set("t_choice", "banana")
    check("枚举非法值被拒", (not ok) and "可选值" in msg)

    check("dirty 标记", record("t_int").dirty is True)
    check("未改项 not dirty", record("t_str").dirty is False)

    ok, msg, changed = reset("t_int")
    check("单项 reset", ok and get("t_int") == 10 and "t_int" in changed)

    set("t_int", 99)
    set("t_str", "xyz")
    ok, msg, changed = reset()
    check("全部 reset", ok and get("t_int") == 10 and get("t_str") == "abc")
    check("reset 后不再 dirty", diff() == [])

    # apply 抛异常 → 回滚
    register("t_boom", 1, group="测试", type=int,
             apply=lambda v: (_ for _ in ()).throw(RuntimeError("boom")))
    ok, msg = set("t_boom", "2")
    check("apply 异常时回滚", (not ok) and get("t_boom") == 1 and "已回滚" in msg)

    # 未找到
    ok, msg = set("nope", "1")
    check("未找到设置项", (not ok) and "未找到" in msg)

    # 快照 / diff
    set("t_str", "changed")
    check("snapshot(only_dirty)", snapshot(only_dirty=True) == {"t_str": "changed"})
    check("diff 内容", diff() == [("t_str", "abc", "changed")])

    # ---- 预设方案 ----
    reset()
    register_profile("test_prof", "测试方案", {"t_int": 77, "t_choice": "off"},
                     icon="🧪", aliases=["tp", "test"])
    check("方案已登记", "test_prof" in profiles())
    check("别名解析", resolve_profile("tp") == "test_prof"
          and resolve_profile("test") == "test_prof")
    check("前缀解析", resolve_profile("test_p") == "test_prof")

    ok, msg, applied, failed = apply_profile("tp")
    check("套用方案", ok and get("t_int") == 77 and get("t_choice") == "off")
    check("改动记录", len(applied) == 2 and not failed)

    ok, msg, applied, failed = apply_profile("tp")
    check("重复套用无改动", ok and applied == [])

    set("t_str", "dirty_value")
    ok, msg, applied, failed = apply_profile("tp", reset_first=True)
    check("--reset 先干净再套用", ok and get("t_str") == "abc" and get("t_int") == 77)

    check("预览含目标值", "77" in profile_preview("test_prof"))
    check("预览列表", "test_prof" in render_profiles())

    ok, msg, applied, failed = apply_profile("不存在")
    check("未知方案被拒", (not ok) and "未找到预设方案" in msg)

    # 方案里引用未注册项 → 整批拒绝
    register_profile("bad_prof", "坏方案", {"t_int": 1, "no_such_key": 2})
    ok, msg, applied, failed = apply_profile("bad_prof")
    check("方案含未注册项则整批拒绝",
          (not ok) and "未注册" in msg and get("t_int") == 77)

    # 方案里值非法 → 整批拒绝
    register_profile("bad_prof2", "坏方案2", {"t_choice": "banana"})
    ok, msg, applied, failed = apply_profile("bad_prof2")
    check("方案含非法值则整批拒绝", (not ok) and get("t_choice") == "off")

    print("=" * 66)
    if fails:
        print(f"自检失败 {len(fails)} 项：" + "；".join(fails))
        return 1
    print("自检全部通过 ✅")
    return 0


if __name__ == "__main__":
    import sys
    sys.exit(_selftest())
