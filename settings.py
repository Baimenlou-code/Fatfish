
import json
import os
from datetime import datetime

_REGISTRY = {}

class Record:

    __slots__ = ("key", "default", "value", "desc", "group", "type",
                 "choices", "apply", "env_name", "advanced")

    def __init__(self, key, default, desc="", group="misc", type=str,
                 choices=None, apply=None, env_name=None, advanced=False):
        self.key = key
        self.default = default
        self.value = default
        self.desc = desc
        self.group = group
        self.type = type
        self.choices = choices
        self.apply = apply
        self.env_name = env_name
        self.advanced = advanced

    @property
    def dirty(self):
        return self.value != self.default

    def coerce(self, raw):
        if self.type is bool:
            if isinstance(raw, bool):
                return raw
            s = str(raw).strip().lower()
            if s in ("1", "true", "yes", "on", "y"):
                return True
            if s in ("0", "false", "no", "off", "n"):
                return False
            raise ValueError(f"a boolean accepts only on/off (got {raw!r})")
        if self.type is int:
            try:
                return int(str(raw).strip())
            except ValueError:
                raise ValueError(f"an integer is required (got {raw!r})")
        if self.type is float:
            try:
                return float(str(raw).strip())
            except ValueError:
                raise ValueError(f"a number is required (got {raw!r})")
        return str(raw).strip()

    def validate(self, value):
        if self.choices and value not in self.choices:
            return False, f"allowed values: {' / '.join(str(c) for c in self.choices)}"
        if self.type is int and not self.choices:
            if value < 0:
                return False, "must not be negative"
        if self.type is float:
            if value < 0:
                return False, "must not be negative"
        return True, ""

    def fmt(self, v=None):
        v = self.value if v is None else v
        if isinstance(v, bool):
            return "on" if v else "off"
        if v == "":
            return "(empty)"
        return str(v)

def register(key, default, desc="", group="misc", type=str,
             choices=None, apply=None, env_name=None, advanced=False):
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
    out = {}
    for rec in _REGISTRY.values():
        out.setdefault(rec.group, []).append(rec)
    return out

def set(key, raw, persist=False):
    rec = _REGISTRY.get(key)
    if not rec:
        near = [k for k in _REGISTRY if key.lower() in k.lower()]
        hint = f", did you mean: {' / '.join(near[:3])}?" if near else ""
        return False, f"no such setting [{key}]{hint}"

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
        except Exception as e:
            rec.value = old
            return False, f"apply failed (rolled back): {type(e).__name__}: {e}"

    rec.value = value
    note = "" if old == value else f" ({rec.fmt(old)} -> {rec.fmt(value)})"
    extra = ""
    if persist and rec.env_name:
        pok, pmsg = save_to_env([key])
        extra = "; " + pmsg if pok else f"; [!] persist failed: {pmsg}"
    return True, f"{key} = {rec.fmt(value)}{note}{extra}"

def reset(key=None, persist=False):
    if key:
        recs = [_REGISTRY[key]] if key in _REGISTRY else []
        if not recs:
            return False, f"no such setting [{key}]", []
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
                failed.append(f"{rec.key} ({type(e).__name__})")
                continue
        rec.value = rec.default
        if old != rec.default:
            changed.append(rec.key)

    scope = key or "all"
    if failed:
        msg = f"restored {scope}: {len(changed)} ok, {len(failed)} failed ({', '.join(failed)})"
    elif changed:
        msg = f"restored {scope} to factory defaults: {', '.join(changed)}"
    else:
        msg = f"{scope} was already at factory defaults, nothing to restore"
    if persist and changed:
        ok, pmsg = save_to_env(changed)
        msg += "; " + pmsg
    return True, msg, changed

def snapshot(only_dirty=False):
    return {k: r.value for k, r in _REGISTRY.items()
            if not only_dirty or r.dirty}

def diff():
    return [(r.key, r.default, r.value) for r in _REGISTRY.values() if r.dirty]

_PROFILES = {}
_PROFILE_ALIAS = {}

def register_profile(name, desc, values, icon="[P]", aliases=()):
    _PROFILES[name] = {"desc": desc, "icon": icon, "values": dict(values or {})}
    for a in aliases:
        _PROFILE_ALIAS[str(a).lower()] = name
    return name

def profiles():
    return _PROFILES

def resolve_profile(name):
    if not name:
        return None
    n = str(name).strip()
    if n in _PROFILES:
        return n
    low = n.lower()
    if low in _PROFILE_ALIAS:
        return _PROFILE_ALIAS[low]
    cand = [p for p in _PROFILES if p.lower().startswith(low)] if low else []
    return cand[0] if len(cand) == 1 else None

def apply_profile(name, reset_first=False):
    key = resolve_profile(name)
    if not key:
        near = " / ".join(_PROFILES.keys())
        return False, f"no such profile [{name}] (available: {near})", [], []

    info = _PROFILES[key]
    values = info["values"]

    problems = []
    for k, v in values.items():
        rec = _REGISTRY.get(k)
        if not rec:
            problems.append(f"{k} (not registered)")
            continue
        try:
            cv = rec.coerce(v)
        except ValueError as e:
            problems.append(f"{k} ({e})")
            continue
        ok, err = rec.validate(cv)
        if not ok:
            problems.append(f"{k} ({err})")
    if problems:
        return False, "profile definition is invalid: " + "; ".join(problems), [], []

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

    icon = info.get("icon", "[P]")
    if failed:
        msg = f"{icon} applied [{key}]: {len(applied)} ok, {len(failed)} failed"
        msg += " (" + "; ".join(f"{k}: {e}" for k, e in failed) + ")"
    elif applied:
        msg = f"{icon} applied [{key}]: {info['desc']}, {len(applied)} item(s) changed"
        if rchanged:
            msg += f" (after restoring {len(rchanged)} item(s) first)"
    elif rchanged:
        msg = f"{icon} restored to factory defaults, {len(rchanged)} item(s)"
        if values:
            msg += f"; [{key}] targets were already satisfied"
    else:
        msg = f"{icon} [{key}] already matches the current settings, nothing to change"
    return True, msg, applied, failed

def render_profiles():
    if not _PROFILES:
        return "(no profiles registered)"
    lines = ["  available profiles:"]
    for name, info in _PROFILES.items():
        icon = info.get("icon", "[P]")
        n = len(info["values"])
        lines.append(f"    {icon} {name:<12} {info['desc']} ({n} items)")
    lines.append("")
    lines.append("  usage: /set profile <name>           apply (only the items it covers)")
    lines.append("         /set profile <name> --reset   restore everything first, then apply")
    lines.append("         /set profile <name> --show    preview what it would change")
    return "\n".join(lines)

def profile_preview(name):
    key = resolve_profile(name)
    if not key:
        return f"(no such profile {name})"
    info = _PROFILES[key]
    lines = [f"  {info.get('icon', '[P]')} {key} -- {info['desc']}",
             f"  would change these {len(info['values'])} item(s) (target / current ->):"]
    for k, v in info["values"].items():
        rec = _REGISTRY.get(k)
        if not rec:
            lines.append(f"    [!]  {k:<26} -> {v}   (not registered)")
            continue
        try:
            cv = rec.coerce(v)
            target = rec.fmt(cv)
        except ValueError:
            target = f"{v} (illegal)"
        cur = rec.fmt()
        mark = "=" if cur == target else "->"
        star = " " if cur == target else "*"
        lines.append(f"    {star} {k:<26} {cur:<14} {mark} {target}")
    lines.append("")
    lines.append("  (items marked * would change; run /set profile %s to apply)" % key)
    return "\n".join(lines)

def _env_path():
    return os.path.join(os.path.dirname(os.path.abspath(__file__)), ".env")

def save_to_env(keys=None):
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
        return True, "nothing to write to .env (no setting deviates from its default)"

    try:
        with open(path, "r", encoding="utf-8") as f:
            lines = f.read().splitlines()
    except OSError:
        lines = []

    written = []
    for key, env_name, rec in targets:
        val = rec.fmt()
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
        return False, f"failed to write .env: {e}"

    return True, f"written to .env: {', '.join(written)}"

GROUP_ICON = {
    "model": "🧠", "verify": "🧿", "network": "🌐", "approval": "🔐",
    "ui": "🖥", "context": "📏", "misc": "⚙️",
}

def render(key=None, show_all=False):
    if key:
        rec = _REGISTRY.get(key)
        if not rec:
            return f"(no such setting {key})"
        star = " *" if rec.dirty else ""
        lines = [f"  {rec.key}{star}",
                 f"    current [value]  : {rec.fmt()}",
                 f"    factory [default]: {rec.fmt(rec.default)}",
                 f"    type             : {rec.type.__name__}",
                 f"    description      : {rec.desc}"]
        if rec.choices:
            lines.append(f"    choices          : " + " / ".join(str(c) for c in rec.choices))
        if rec.env_name:
            lines.append(f"    .env key         : {rec.env_name}")
        return "\n".join(lines)

    out = []
    for group, recs in groups().items():
        shown = [r for r in recs if show_all or not r.advanced]
        if not shown:
            continue
        icon = GROUP_ICON.get(group, "*")
        out.append(f"  {icon} {group}")
        for r in shown:
            star = "*" if r.dirty else " "
            out.append(f"    {star} {r.key:<26} = {r.fmt():<14} {r.desc}")
    dirty = diff()
    out.append("")
    if dirty:
        out.append(f"  * marks a deviation from the factory default ({len(dirty)} item(s)); use /set reset to restore")
    else:
        out.append("  all at factory defaults")
    if not show_all:
        out.append("  use /set all to show everything (including advanced items)")
    return "\n".join(out)

def render_diff():
    d = diff()
    if not d:
        return "  all at factory defaults; nothing to restore."
    lines = ["  the following items deviate from factory defaults:"]
    for key, default, cur in d:
        rec = _REGISTRY[key]
        lines.append(f"    {key:<26} {rec.fmt(default):<14} -> {rec.fmt(cur)}")
    return "\n".join(lines)

def _selftest():
    import io
    try:
        import sys
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    fails = []

    def check(name, cond):
        print(("  [OK] " if cond else "  [XX] ") + name)
        if not cond:
            fails.append(name)

    print("=" * 66)
    print("settings self-test start")
    print("=" * 66)

    applied = {}
    def _mk_applier(k):
        return lambda v: applied.__setitem__(k, v)

    register("t_int", 10, desc="int", group="test", type=int, apply=_mk_applier("t_int"))
    register("t_str", "abc", desc="string", group="test", type=str, apply=_mk_applier("t_str"))
    register("t_bool", True, desc="bool", group="test", type=bool, apply=_mk_applier("t_bool"))
    register("t_choice", "auto", desc="enum", group="test", type=str,
             choices=["auto", "on", "off"], apply=_mk_applier("t_choice"))

    ok, msg = set("t_int", "42")
    check("integer setting", ok and get("t_int") == 42 and applied.get("t_int") == 42)

    ok, msg = set("t_int", "abc")
    check("type error rejected", (not ok) and get("t_int") == 42)

    ok, msg = set("t_int", "-5")
    check("negative rejected", not ok)

    ok, msg = set("t_bool", "off")
    check("boolean off", ok and get("t_bool") is False)

    ok, msg = set("t_choice", "banana")
    check("illegal enum rejected", (not ok) and "allowed values" in msg)

    check("dirty flag", record("t_int").dirty is True)
    check("unchanged item not dirty", record("t_str").dirty is False)

    ok, msg, changed = reset("t_int")
    check("single reset", ok and get("t_int") == 10 and "t_int" in changed)

    set("t_int", 99)
    set("t_str", "xyz")
    ok, msg, changed = reset()
    check("reset all", ok and get("t_int") == 10 and get("t_str") == "abc")
    check("nothing dirty after reset", diff() == [])

    register("t_boom", 1, group="test", type=int,
             apply=lambda v: (_ for _ in ()).throw(RuntimeError("boom")))
    ok, msg = set("t_boom", "2")
    check("rollback when apply raises", (not ok) and get("t_boom") == 1 and "rolled back" in msg)

    ok, msg = set("nope", "1")
    check("unknown setting", (not ok) and "no such setting" in msg)

    set("t_str", "changed")
    check("snapshot(only_dirty)", snapshot(only_dirty=True) == {"t_str": "changed"})
    check("diff contents", diff() == [("t_str", "abc", "changed")])

    reset()
    register_profile("test_prof", "test profile", {"t_int": 77, "t_choice": "off"},
                     icon="[T]", aliases=["tp", "test"])
    check("profile registered", "test_prof" in profiles())
    check("alias resolution", resolve_profile("tp") == "test_prof"
          and resolve_profile("test") == "test_prof")
    check("prefix resolution", resolve_profile("test_p") == "test_prof")

    ok, msg, applied, failed = apply_profile("tp")
    check("apply profile", ok and get("t_int") == 77 and get("t_choice") == "off")
    check("change log", len(applied) == 2 and not failed)

    ok, msg, applied, failed = apply_profile("tp")
    check("re-applying changes nothing", ok and applied == [])

    set("t_str", "dirty_value")
    ok, msg, applied, failed = apply_profile("tp", reset_first=True)
    check("--reset cleans first then applies", ok and get("t_str") == "abc" and get("t_int") == 77)

    check("preview contains targets", "77" in profile_preview("test_prof"))
    check("profile listing", "test_prof" in render_profiles())

    ok, msg, applied, failed = apply_profile("nonexistent")
    check("unknown profile rejected", (not ok) and "no such profile" in msg)

    register_profile("bad_prof", "bad profile", {"t_int": 1, "no_such_key": 2})
    ok, msg, applied, failed = apply_profile("bad_prof")
    check("unregistered item rejects the whole profile",
          (not ok) and "not registered" in msg and get("t_int") == 77)

    register_profile("bad_prof2", "bad profile 2", {"t_choice": "banana"})
    ok, msg, applied, failed = apply_profile("bad_prof2")
    check("illegal value rejects the whole profile", (not ok) and get("t_choice") == "off")

    print("=" * 66)
    if fails:
        print(f"self-test failed on {len(fails)} item(s): " + "; ".join(fails))
        return 1
    print("self-test all passed")
    return 0

if __name__ == "__main__":
    import sys
    sys.exit(_selftest())
