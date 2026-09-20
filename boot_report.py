
import os
import sys
import time
import datetime

__all__ = [
    "collect", "render_console", "render_brief", "system_message",
    "scan_peers", "paint", "RST", "C",
]

RST = "\033[0m"
C = {
    "cyan":    "\033[96m",
    "blue":    "\033[94m",
    "green":   "\033[92m",
    "yellow":  "\033[93m",
    "red":     "\033[91m",
    "magenta": "\033[95m",
    "gray":    "\033[90m",
    "white":   "\033[97m",
    "bold":    "\033[1m",
    "dim":     "\033[2m",
}

def paint(text, *styles):
    if not styles:
        return str(text)
    return "".join(styles) + str(text) + RST

def _dw(s):
    try:
        import unicodedata
    except Exception:
        return len(s or "")
    w = 0
    for ch in str(s or ""):
        if unicodedata.combining(ch):
            continue
        w += 2 if unicodedata.east_asian_width(ch) in ("W", "F") else 1
    return w

def _pad(s, width):
    d = width - _dw(s)
    return s + (" " * d if d > 0 else "")

WEEK = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"]

def _fmt_ts(ts, with_week=False):
    if not ts:
        return "-"
    s = time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(ts))
    if with_week:
        s += " " + WEEK[time.localtime(ts).tm_wday]
    return s

def _fmt_size(n):
    try:
        n = float(n)
    except (TypeError, ValueError):
        return "-"
    if n < 1024:
        return "%d B" % int(n)
    for unit in ("KB", "MB", "GB"):
        n /= 1024.0
        if n < 1024 or unit == "GB":
            return "%.1f %s" % (n, unit)
    return "%.1f GB" % n

def _fmt_dur(sec):
    if sec is None:
        return "-"
    sec = int(max(0, sec))
    d, sec = divmod(sec, 86400)
    h, sec = divmod(sec, 3600)
    m, s = divmod(sec, 60)
    parts = []
    if d:
        parts.append("%dd" % d)
    if h or d:
        parts.append("%dh" % h)
    if m or h or d:
        parts.append("%dm" % m)
    parts.append("%ds" % s)
    return "".join(parts)

def _tz_info(now=None):
    try:
        dt = now or datetime.datetime.now()
        off = dt.astimezone().utcoffset()
        hours = off.total_seconds() / 3600.0 if off is not None else None
        if hours is None:
            return "local timezone unknown", None
        sign = "+" if hours >= 0 else "-"
        ah = abs(hours)
        hh = int(ah)
        mm = int(round((ah - hh) * 60))
        label = "UTC%s%d:%02d" % (sign, hh, mm)
        name = time.tzname[0] if time.tzname else ""
        return (label + (" " + name if name else "")), hours
    except Exception:
        return "local timezone unknown", None

def _proc_start_time(pid):
    if os.name != "nt":
        return None
    try:
        import ctypes
        from ctypes import wintypes

        PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
        k32 = ctypes.windll.kernel32
        k32.OpenProcess.restype = wintypes.HANDLE
        k32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        h = k32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, int(pid))
        if not h:
            return None
        try:
            create, exit_t = wintypes.FILETIME(), wintypes.FILETIME()
            kern, user = wintypes.FILETIME(), wintypes.FILETIME()
            ok = k32.GetProcessTimes(h, ctypes.byref(create), ctypes.byref(exit_t),
                                     ctypes.byref(kern), ctypes.byref(user))
            if not ok:
                return None
            val = (create.dwHighDateTime << 32) | create.dwLowDateTime
            return val / 1e7 - 11644473600.0
        finally:
            try:
                k32.CloseHandle(h)
            except Exception:
                pass
    except Exception:
        return None

PID_OWNER_NAMES = {
    "cmd.exe", "conhost.exe", "openconsole.exe",
    "windowsterminal.exe", "wt.exe",
}

_PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
_STILL_ACTIVE = 259
_ERROR_INVALID_PARAMETER = 87
_ERROR_ACCESS_DENIED = 5

def _pid_state(pid):
    if os.name != "nt":
        return None, None
    try:
        import ctypes
        from ctypes import wintypes

        k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        k32.OpenProcess.restype = wintypes.HANDLE
        k32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        k32.GetExitCodeProcess.argtypes = [wintypes.HANDLE,
                                           ctypes.POINTER(wintypes.DWORD)]
        k32.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD,
                                                   wintypes.LPWSTR,
                                                   ctypes.POINTER(wintypes.DWORD)]
        k32.CloseHandle.argtypes = [wintypes.HANDLE]

        h = k32.OpenProcess(_PROCESS_QUERY_LIMITED_INFORMATION, False, int(pid))
        if not h:
            err = ctypes.get_last_error()
            if err == _ERROR_INVALID_PARAMETER:
                return False, None
            if err == _ERROR_ACCESS_DENIED:
                return True, None
            return None, None

        try:
            code = wintypes.DWORD()
            if not k32.GetExitCodeProcess(h, ctypes.byref(code)):
                return None, None
            if code.value != _STILL_ACTIVE:
                return False, None
            buf = ctypes.create_unicode_buffer(1024)
            size = wintypes.DWORD(len(buf))
            if k32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(size)):
                return True, os.path.basename(buf.value).lower()
            return True, None
        finally:
            try:
                k32.CloseHandle(h)
            except Exception:
                pass
    except Exception:
        return None, None

SKIP_DIRS_LOWER = {
    "venv", ".venv", "env", "__pycache__", "site-packages", "dist-packages",
    "node_modules", ".git", ".hg", ".svn", ".idea", ".vscode",
    ".fatfish_tmp", "_backup", "oldver",
}

def _is_drive_root(path):
    try:
        drive, tail = os.path.splitdrive(os.path.abspath(path))
        return tail in ("\\", "/", "")
    except Exception:
        return False

def _peer_roots(script_path, up=2):
    roots = []
    try:
        p = os.path.dirname(os.path.abspath(script_path))
    except Exception:
        return roots
    for _ in range(up + 1):
        if p and p not in roots and not _is_drive_root(p):
            roots.append(p)
        nxt = os.path.dirname(p)
        if not nxt or nxt == p:
            break
        p = nxt
    return roots

def scan_peers(script_path, extra_roots=None, max_files=900, max_depth=2):
    hits, scanned, truncated = [], 0, False
    try:
        target = os.path.basename(script_path)
        cur_abs = os.path.abspath(script_path)
    except Exception:
        return hits, scanned, truncated

    roots, visited = [], set()
    for r in (_peer_roots(script_path) + list(extra_roots or [])):
        try:
            r = os.path.abspath(r)
        except Exception:
            continue
        if r in visited or not os.path.isdir(r):
            continue
        visited.add(r)
        roots.append(r)

    for root in roots:
        base_sep = root.rstrip("\\/").count(os.sep)
        for dp, dn, fn in os.walk(root):
            try:
                depth = dp.rstrip("\\/").count(os.sep) - base_sep
            except Exception:
                depth = 0
            dn[:] = [d for d in dn if d.lower() not in SKIP_DIRS_LOWER]
            if depth >= max_depth:
                dn[:] = []
            for f in fn:
                scanned += 1
                if scanned > max_files:
                    return hits, scanned, True
                if f != target:
                    continue
                p = os.path.join(dp, f)
                try:
                    st = os.stat(p)
                except OSError:
                    continue
                if any(h["path"].lower() == p.lower() for h in hits):
                    continue
                hits.append({
                    "path": p,
                    "size": st.st_size,
                    "mtime": st.st_mtime,
                    "current": os.path.normcase(p) == os.path.normcase(cur_abs),
                })
    hits.sort(key=lambda d: (-(d["mtime"] or 0), d["path"]))
    return hits, scanned, truncated

def collect(script_path=None, workspace_dir=None, extra_roots=None,
            do_scan_peers=True, max_files=900, max_depth=2):
    info = {
        "ok": True,
        "errors": [],
        "warnings": [],
        "notes": [],
        "collected_at": time.time(),
    }

    try:
        now = datetime.datetime.now()
        tz_label, tz_hours = _tz_info(now)
        info.update({
            "now_ts": time.time(),
            "now_str": now.strftime("%Y-%m-%d %H:%M:%S"),
            "weekday": WEEK[now.weekday()],
            "utc_str": datetime.datetime.utcnow().strftime("%Y-%m-%d %H:%M:%S"),
            "tz_label": tz_label,
            "tz_hours": tz_hours,
        })
    except Exception as e:
        info["errors"].append("time collection failed: %s" % e)

    try:
        pid = os.getpid()
        start_ts = _proc_start_time(pid)
        info.update({
            "pid": pid,
            "python_version": sys.version.split()[0],
            "python_exe": sys.executable,
            "cwd": os.getcwd(),
            "proc_start_ts": start_ts,
            "uptime_sec": (time.time() - start_ts) if start_ts else None,
            "pid_source": "os.getpid()",
        })
    except Exception as e:
        info["errors"].append("process info collection failed: %s" % e)

    try:
        sp = script_path or (sys.argv[0] if sys.argv and sys.argv[0] else "")
        sp = os.path.abspath(sp) if sp else ""
        info["script_path"] = sp
        if sp and os.path.isfile(sp):
            st = os.stat(sp)
            info["script_size"] = st.st_size
            info["script_mtime"] = st.st_mtime
        else:
            info["warnings"].append("could not locate the running program file: %s" % (sp or "(unknown)"))
    except Exception as e:
        info["errors"].append("program file collection failed: %s" % e)

    try:
        ws = workspace_dir
        if ws:
            ws = os.path.abspath(ws)
        info["workspace"] = ws
        if ws and not os.path.isdir(ws):
            info["warnings"].append("workspace directory does not exist: %s" % ws)
        elif ws and info.get("cwd"):
            same = os.path.normcase(os.path.abspath(info["cwd"])) == os.path.normcase(ws)
            info["cwd_is_workspace"] = same
    except Exception as e:
        info["errors"].append("workspace collection failed: %s" % e)

    info["peers"], info["peers_scanned"], info["peers_truncated"] = [], 0, False
    if do_scan_peers and info.get("script_path"):
        try:
            hits, scanned, trunc = scan_peers(
                info["script_path"], extra_roots=extra_roots,
                max_files=max_files, max_depth=max_depth)
            info["peers"], info["peers_scanned"], info["peers_truncated"] = hits, scanned, trunc
            others = [h for h in hits if not h["current"]]
            if others:
                info["notes"].append("found %d same-name program cop(y/ies) (not the running file)" % len(others))
            if trunc:
                info["notes"].append("peer scan hit the entry limit %d; results may be incomplete" % max_files)
        except Exception as e:
            info["errors"].append("peer scan failed: %s" % e)

    info["pid_file"] = None
    try:
        if info.get("script_path"):
            cand = os.path.join(os.path.dirname(info["script_path"]), "_fatfish_pid.txt")
            if os.path.isfile(cand):
                raw = ""
                try:
                    with open(cand, "r", encoding="utf-8", errors="replace") as f:
                        raw = f.read().strip()
                except OSError:
                    pass
                rec = {"path": cand, "value": raw, "status": None}
                info["pid_file"] = rec
                digits = "".join(ch for ch in raw if ch.isdigit())
                if not digits:
                    rec["status"] = "invalid"
                    info["warnings"].append(
                        "_fatfish_pid.txt does not contain a valid PID: %r" % raw[:40])
                else:
                    pid_val = int(digits)
                    alive, owner = _pid_state(pid_val)
                    rec.update({"pid": pid_val, "alive": alive, "owner_name": owner})
                    if alive is False:
                        rec["status"] = "stale"
                        info["warnings"].append(
                            "_fatfish_pid.txt is a leftover: it records %d but that process "
                            "no longer exists (previous run exited abnormally; safe to delete)"
                            % pid_val)
                    elif alive is True and owner is None:
                        rec["status"] = "unknown"
                        info["notes"].append(
                            "_fatfish_pid.txt records %d: the process exists but its image "
                            "name is unreadable, identity check skipped (no alarm)"
                            % pid_val)
                    elif alive is True and owner in PID_OWNER_NAMES:
                        rec["status"] = "ok"
                        info["notes"].append(
                            "_fatfish_pid.txt valid: runtime window PID %d (%s)"
                            % (pid_val, owner))
                    elif alive is True:
                        rec["status"] = "suspect"
                        info["warnings"].append(
                            "_fatfish_pid.txt records %d, but that PID is now %s, which does "
                            "not look like a FatFish runtime window (likely mis-captured)"
                            % (pid_val, owner))
                    else:
                        rec["status"] = "unknown"
                        info["notes"].append(
                            "_fatfish_pid.txt records %d; liveness could not be confirmed "
                            "(check skipped)" % pid_val)
    except Exception as e:
        info["errors"].append("PID marker check failed: %s" % e)

    try:
        if info.get("script_path") and info.get("workspace"):
            sd = os.path.normcase(os.path.dirname(info["script_path"]))
            wd = os.path.normcase(info["workspace"])
            if not (sd == wd or sd.startswith(wd + os.sep)):
                info["notes"].append("the program file is outside the workspace "
                                     "(workspace=%s, program=%s)"
                                     % (info["workspace"], info["script_path"]))
    except Exception:
        pass

    return info

def _rule(width, color=True, ch="─"):
    s = ch * width
    return paint(s, C["gray"]) if color else s

def render_console(info, color=True, width=70):
    w = max(48, int(width))
    out = []

    def add(s=""):
        out.append(s)

    L = (lambda s: paint(s, C["cyan"], C["bold"])) if color else (lambda s: s)
    K = (lambda s: paint(s, C["blue"])) if color else (lambda s: s)
    V = (lambda s: s)
    GOOD = (lambda s: paint(s, C["green"])) if color else (lambda s: s)
    WARN = (lambda s: paint(s, C["yellow"], C["bold"])) if color else (lambda s: s)
    DIM = (lambda s: paint(s, C["gray"], C["dim"])) if color else (lambda s: s)

    title = "FatFish BOOT REPORT"
    sub = "%s %s | %s" % (info.get("now_str", "-"), info.get("weekday", ""),
                          info.get("tz_label", "-"))

    add(_rule(w, color, "="))
    add(" " + L(title))
    add(" " + DIM(sub))
    add(_rule(w, color, "="))

    def row(icon_key, value, extra=""):
        k = K(icon_key)
        line = " %s %s" % (k, V(value))
        if extra:
            line += "  " + extra
        add(line)

    row("[time]", "%s (%s)" % (info.get("now_str", "-"), info.get("weekday", "-")),
        DIM("UTC %s" % info.get("utc_str", "-")))
    up = _fmt_dur(info.get("uptime_sec"))
    row("[proc]", "PID %s | Python %s | up %s"
        % (info.get("pid", "-"), info.get("python_version", "-"), up),
        DIM("started " + _fmt_ts(info.get("proc_start_ts"))))
    row("[py]", info.get("python_exe", "-"))
    sp = info.get("script_path") or "(unknown)"
    row("[file]", sp)
    if info.get("script_size") is not None:
        add("     %s %s | modified %s"
            % (DIM("."), _fmt_size(info.get("script_size")),
               _fmt_ts(info.get("script_mtime"))))
    ws = info.get("workspace") or "(not provided)"
    if info.get("cwd_is_workspace") is True:
        mark = GOOD("== cwd (matches)")
    elif info.get("cwd_is_workspace") is False:
        mark = WARN("!= cwd (differs: cwd=%s)" % info.get("cwd", "-"))
    else:
        mark = ""
    row("[ws]", ws, mark)
    peers = info.get("peers") or []
    if peers:
        head = "%d file(s) (scanned %s entries%s)" % (
            len(peers), info.get("peers_scanned", 0),
            ", limit reached so possibly incomplete" if info.get("peers_truncated") else "")
        row("[peers]", head)
        for p in peers[:6]:
            flag = GOOD("* running") if p.get("current") else "         "
            name_txt = _pad(os.path.basename(p["path"]), 22)
            add("     %s %s  %s  %s" % (
                flag, DIM(name_txt), _pad(_fmt_size(p.get("size")), 10),
                _fmt_ts(p.get("mtime"))))
            add("            %s" % DIM(p["path"]))
        if len(peers) > 6:
            add("     %s" % DIM("... %d more omitted" % (len(peers) - 6)))
    else:
        row("[peers]", DIM("no same-name copies found (or scanning disabled)"))
    warns = info.get("warnings") or []
    notes = info.get("notes") or []
    if warns:
        add(" %s %s" % (K("[health]"), WARN("worth a look:")))
        for wmsg in warns:
            add("     %s %s" % (paint("!", C["yellow"]) if color else "!", wmsg))
    else:
        row("[health]", GOOD("passed; no anomalies found"))
    for nmsg in notes:
        add("     %s %s" % (DIM("."), DIM(nmsg)))
    errs = info.get("errors") or []
    for emsg in errs:
        add("     %s %s" % (paint("x", C["red"]) if color else "x", DIM(emsg)))

    add(_rule(w, color, "="))
    return "\n".join(out)

def render_brief(info, max_peers=3, for_prompt=True):
    lines = []
    if for_prompt:
        lines.append("[Boot report - collected automatically at startup by the program, "
                     "not user input; trust it directly]")
    lines.append("· Physical time: %s %s (%s)" % (
        info.get("now_str", "-"), info.get("weekday", ""), info.get("tz_label", "-")))

    proc = "· Running process: PID %s | Python %s | up %s" % (
        info.get("pid", "-"), info.get("python_version", "-"),
        _fmt_dur(info.get("uptime_sec")))
    lines.append(proc)

    if info.get("script_path"):
        seg = "· Program file: %s" % info["script_path"]
        if info.get("script_size") is not None:
            seg += " (%s, modified %s)" % (_fmt_size(info.get("script_size")),
                                           _fmt_ts(info.get("script_mtime")))
        lines.append(seg)

    if info.get("workspace"):
        seg = "· Workspace root: %s" % info["workspace"]
        if info.get("cwd_is_workspace") is True:
            seg += " (matches the process cwd)"
        elif info.get("cwd_is_workspace") is False:
            seg += " (note: differs from the process cwd %s)" % info.get("cwd")
        lines.append(seg)

    peers = info.get("peers") or []
    if peers:
        others = [p for p in peers if not p.get("current")]
        if others:
            lst = "; ".join(
                "%s (%s, %s)" % (p["path"], _fmt_size(p.get("size")), _fmt_ts(p.get("mtime")))
                for p in others[:max_peers])
            more = "" if len(others) <= max_peers else ", plus %d more" % (len(others) - max_peers)
            lines.append("· Same-name copies: %d non-running file(s) -- %s%s"
                         % (len(others), lst, more))

    for wmsg in (info.get("warnings") or []):
        lines.append("· Health note: %s" % wmsg)
    for nmsg in (info.get("notes") or []):
        lines.append("· Remark: %s" % nmsg)

    if for_prompt:
        lines.append("(This information refreshes on every start / context clear; "
                     "to double-check the workspace, call ws_where.)")
    return "\n".join(lines)

def system_message(base_prompt, script_path=None, workspace_dir=None,
                   do_scan_peers=True, verbose=False, extra_roots=None):
    try:
        info = collect(script_path=script_path, workspace_dir=workspace_dir,
                       extra_roots=extra_roots, do_scan_peers=do_scan_peers)
        if verbose:
            try:
                print(render_console(info, color=True))
            except Exception:
                pass
        brief = render_brief(info)
        return base_prompt + ("\n\n" + brief if brief else "")
    except Exception:
        return base_prompt

def _selftest(argv=None):
    argv = argv if argv is not None else sys.argv[1:]
    color = "--no-color" not in argv
    here = os.path.abspath(__file__)
    ws = os.environ.get("WORKSPACE_DIR") or os.path.dirname(here)
    info = collect(script_path=here, workspace_dir=ws)

    if "--brief" in argv:
        print(render_brief(info))
        return 0

    print(render_console(info, color=color))
    print()
    print(paint("[Brief text injected into the model - preview]", C["magenta"], C["bold"]) if color
          else "[Brief text injected into the model - preview]")
    print(render_brief(info))
    return 0

if __name__ == "__main__":
    try:
        sys.exit(_selftest())
    except KeyboardInterrupt:
        sys.exit(130)
