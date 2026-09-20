# -*- coding: utf-8 -*-
"""
make_fatpack_en.py -- regenerate the FatFish English Edition one-click installer

Usage:
    python make_fatpack_en.py                 build FATPACKEN.bat (default)
    python make_fatpack_en.py FATPACKEN.bat   explicit output name
    python make_fatpack_en.py --manifest      list what would be embedded, build nothing
    python make_fatpack_en.py --strict        abort (exit 3) if the EMBED audit fails

How it works
------------
Program files are base64-encoded and written line by line into the tail of a
.bat file, separated by the marker ##PYBEGIN##. At run time the installer uses
PowerShell to take LastIndexOf of that marker inside its OWN file, extracts
everything after it (a chunk of Python) into a temp .py, and runs it. That
Python decodes the base64 and writes the embedded files to disk. So the
installer is a "script + archive" in one file and can be distributed as is.

Three hard-won rules
--------------------
1. The batch shell must be pure ASCII.
   cmd.exe continues reading a batch file by BYTE OFFSET. When the file contains
   multi-byte UTF-8, the offset drifts, cmd starts reading mid-token and tries
   to execute half a comment as a command. So the shell is English ASCII only.
   Non-ASCII text is safe inside the payload, which is written and read by
   PowerShell with an explicit UTF-8 encoding and never parsed by cmd.
2. Templates must be raw strings (r'''...''').
   Otherwise a Windows path such as %TEMP%\\fatfish_... has its \\f interpreted
   as a form feed (0x0C) and PowerShell fails with "Illegal characters in path".
3. Back up before overwriting.
   An existing file is copied into _backup/ first; nothing is silently lost.

Maintenance
-----------
The EMBED list must cover every local module on the startup path. The main
program imports ui_core / verify_tools / settings / boot_report at import time;
missing any one of them makes it fail before the REPL even starts. audit_embed()
below verifies this automatically, and the installer's step 6 imports every
embedded .py rather than a hand-written subset -- a hand-written subset would
only ever check the files already in the list, which is self-certification.
"""

import os
import sys
import base64
import hashlib

# ============ Configuration ============

# Files embedded into the installer. The order is also the extraction order.
EMBED = [
    "FATENFISH.py",           # main program
    "ui_core.py",             # view layer (colors / markup / banner / spinner)
    "verify_tools.py",        # dual-AI verify engine
    "settings.py",            # settings registry (/set)
    "boot_report.py",         # boot report (environment snapshot)
    "common.py",              # shared primitives (timestamps / dated dirs)
    "workspace.py",           # workspace sandbox + the 14 tools
    "file_tools.py",          # file / directory / image reading
    "net_tools.py",           # web search
    "exec_tools.py",          # command and Python execution
    "fatfish_watcher.py",     # watcher (tails sub-program output)
    "launch.py",              # launch hub
    "fatfish1.1.1.bat",       # launcher (double-click this)
    "fatfish_runtime.bat",    # runtime window
    "fatfish_lang.bat",       # language probe
    "README.md",              # manual
    ".gitignore",             # leak-prevention list
]

# Local modules the installer's self-check must import.
# Kept in sync with EMBED by audit_embed().
SELFCHECK_MODULES = [
    "common", "ui_core", "settings", "boot_report", "verify_tools",
    "net_tools", "file_tools", "workspace", "exec_tools",
]

MARKER = "##PYBEGIN##"
VERSION = "1.1.1"
EDITION = "EN"
DEFAULT_OUT = "FATPACKEN.bat"
MAIN_SCRIPT = "FATENFISH.py"
LAUNCHER = "fatfish1.1.1.bat"

# Edition badge is inferred from the output filename, so the banner always
# matches the file that was built.
EDITION_TAGS = ("I", "II", "III", "IV", "EN")

# Characters that would need escaping inside a batch `echo` line.
ECHO_UNSAFE = set("()%^<>|&!")


# ============ .env template ============
# The .env skeleton is generated from the real .env file at build time instead
# of being hand-copied into the shell. Hand-copying drifts: the shell would keep
# advertising settings that the program no longer reads. Generating it means the
# installer always ships the same skeleton as the source tree.

def build_env_echo_lines(env_path=".env"):
    """Return the batch `echo` lines that write the .env skeleton.

    Raises RuntimeError if a line cannot be emitted safely by cmd's echo.
    """
    if not os.path.isfile(env_path):
        raise RuntimeError("cannot build the .env template: %s not found" % env_path)
    with open(env_path, "r", encoding="utf-8") as f:
        src = f.read().replace("\r\n", "\n").split("\n")
    while src and src[-1] == "":
        src.pop()
    out = []
    for i, line in enumerate(src, 1):
        if not line.isascii():
            raise RuntimeError(".env line %d is not ASCII: %r" % (i, line))
        bad = ECHO_UNSAFE & set(line)
        if bad:
            raise RuntimeError(".env line %d contains characters cmd would mangle: "
                               "%r in %r" % (i, sorted(bad), line))
        out.append("        echo." if line == "" else "        echo " + line)
    return out


# ============ Payload script template (UTF-8, run by the extracted Python) ============
PAYLOAD_TMPL = r'''import sys, os, base64, shutil
from datetime import datetime

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

PACK_VER = "__VER__"

FILES = {
__ITEMS__
}


def backup(path):
    """Copy the old file into _backup/ before overwriting it."""
    try:
        os.makedirs("_backup", exist_ok=True)
        ts = datetime.now().strftime("%Y%m%d_%H%M%S_%f")
        dst = os.path.join("_backup", os.path.basename(path) + "." + ts + ".bak")
        shutil.copy2(path, dst)
        return True
    except Exception:
        return False


n_new = n_upd = n_bad = total = 0
print("      [pack] __NAME__ v%s  |  embedding %d file(s)" % (PACK_VER, len(FILES)))
for name, b64 in FILES.items():
    try:
        data = base64.b64decode(b64)
    except Exception as e:
        print("      [FAIL] %-24s %s" % (name, e))
        n_bad += 1
        continue
    total += len(data)
    if os.path.exists(name):
        ok = backup(name)
        n_upd += 1
        tag = "updated - old -> _backup/" if ok else "updated - BACKUP FAILED"
    else:
        n_new += 1
        tag = "new"
    with open(name, "wb") as f:
        f.write(data)
    print("      [OK]   %-24s %7d B  %s" % (name, len(data), tag))

print("      [pack] new=%d / updated=%d / failed=%d / %d bytes total"
      % (n_new, n_upd, n_bad, total))
if n_bad:
    sys.exit(2)
'''


# ============ Batch shell template (must stay pure ASCII) ============
BAT_TMPL = r'''@echo off
chcp 65001 >nul
title FatFish Installer __ED__ v__VER__ ^| __OUT__
cd /d "%~dp0"
setlocal enabledelayedexpansion

set "PACKVER=__VER__"
set "PACKCOUNT=__COUNT__"

echo.
echo ============================================================
echo    FatFish One-Click Installer  -  __ED__ Edition  v%PACKVER%
echo    Embedded: __COUNT__ files / __BYTES__ bytes
echo    Shell is ASCII by design - see make_fatpack_en.py for why.
echo ============================================================
echo.
echo    Quiet switches - for automation, ignore when double-clicking:
echo      set FATFISH_SKIP_VENV=1   skip virtualenv creation
echo      set FATFISH_NO_GUI=1      do not open Notepad, do not ask to launch
echo.

REM ============================================================
REM  1) Check Python
REM ============================================================
echo [1/7] Checking Python...
where python >nul 2>nul
if errorlevel 1 (
    echo.
    echo   [X] Python not found!
    echo.
    echo   Please install Python 3.8 or newer first:
    echo     1. Open https://www.python.org/downloads/
    echo     2. Download and run the installer
    echo     3. IMPORTANT: tick "Add Python to PATH"
    echo     4. Then run this installer again
    echo.
    pause
    exit /b 1
)
for /f "tokens=2" %%v in ('python --version 2^>^&1') do set PYVER=%%v
echo       [OK] Python %PYVER% found

REM ============================================================
REM  2) Extract the embedded program files.
REM     Get-Content is given an explicit -Encoding UTF8 so the payload
REM     decodes correctly no matter what the console code page is.
REM ============================================================
echo.
echo [2/7] Extracting embedded files - %PACKCOUNT% of them...
set "EXTRACT_PY=%TEMP%\fatfish_extract_%RANDOM%%RANDOM%.py"
set "MK=##PY"
set "MK=!MK!BEGIN##"
powershell -NoProfile -Command "$c = Get-Content -LiteralPath '%~f0' -Raw -Encoding UTF8; $i = $c.LastIndexOf('%MK%'); if ($i -lt 0) { exit 1 }; $c.Substring($i + __MLEN__) | Set-Content -LiteralPath '%EXTRACT_PY%' -Encoding UTF8"
if errorlevel 1 (
    echo   [X] Failed to extract the embedded data.
    echo       The installer is probably damaged or was re-saved by an editor.
    echo       Please download a fresh copy of __OUT__.
    pause
    exit /b 1
)
python "%EXTRACT_PY%"
if errorlevel 1 (
    echo   [X] Failed to write the program files.
    del "%EXTRACT_PY%" >nul 2>nul
    pause
    exit /b 1
)
del "%EXTRACT_PY%" >nul 2>nul
if not exist "__MAIN__" (
    echo   [X] __MAIN__ was not extracted - aborting.
    pause
    exit /b 1
)
echo       [OK] Core files are in place

REM ============================================================
REM  3) Virtualenv
REM ============================================================
echo.
echo [3/7] Configuring virtualenv...
if /i "%FATFISH_SKIP_VENV%"=="1" (
    echo       [skip] FATFISH_SKIP_VENV=1
) else if exist "venv\Scripts\activate.bat" (
    echo       [OK] venv already exists - skipped
) else (
    echo       Creating venv - takes about 10-30 seconds the first time...
    python -m venv venv
    if errorlevel 1 (
        echo       [!] venv creation failed - will keep using the global Python
    ) else (
        echo       [OK] venv created
    )
)
if /i not "%FATFISH_SKIP_VENV%"=="1" if exist "venv\Scripts\activate.bat" call "venv\Scripts\activate.bat"

REM ============================================================
REM  4) Dependencies
REM ============================================================
echo.
echo [4/7] Installing dependencies: openai / python-dotenv / requests ...
python -c "import openai, dotenv, requests" >nul 2>nul
if errorlevel 1 (
    echo       Upgrading pip...
    python -m pip install --upgrade pip -q
    echo       Installing from the official index...
    python -m pip install openai python-dotenv requests -q
    if errorlevel 1 (
        echo.
        echo   [X] Dependency install failed. Check your network, or run manually:
        echo       python -m pip install openai python-dotenv requests
        echo.
        pause
        exit /b 1
    )
    echo       [OK] Dependencies installed
) else (
    echo       [OK] Dependencies already satisfied - skipped
)

REM ============================================================
REM  5) .env skeleton
REM     Generated at build time from the real .env file, so the
REM     installer can never advertise settings the program ignores.
REM     Pure ASCII, no parentheses or percent signs on any line.
REM ============================================================
echo.
echo [5/7] Checking config file...
if exist ".env" (
    echo       [OK] .env exists - kept as is
) else (
    (
__ENVLINES__
    ) > ".env"
    echo       [OK] .env created - full skeleton, no keys inside
    if /i "%FATFISH_NO_GUI%"=="1" (
        echo       [skip] FATFISH_NO_GUI=1 - not opening Notepad
    ) else (
        echo.
        echo   [!] IMPORTANT: fill in your API keys in .env before launching FatFish!
        echo       Get one at https://platform.deepseek.com/
        echo.
        start notepad ".env"
    )
)

REM ============================================================
REM  6) Self-check -- import every embedded .py module.
REM     Importing only a hand-picked subset would be self-certifying:
REM     a module missing from the list would still report [OK].
REM ============================================================
echo.
echo [6/7] Running self-check...
python -c "import openai, dotenv, requests, __SELFCHECK__; print('      [OK] all modules imported')"
if errorlevel 1 echo   [!] Self-check failed - see the errors above

REM ============================================================
REM  7) Done - optional launch
REM ============================================================
echo.
echo [7/7] Install finished!
echo ------------------------------------------------------------
echo    Next steps:
echo      1. make sure .env holds your API keys
echo      2. double-click "__LAUNCHER__" to start FatFish
echo ------------------------------------------------------------
echo.

if /i "%FATFISH_NO_GUI%"=="1" (
    echo       [i] Launch prompt skipped - start __LAUNCHER__ manually.
) else (
    set "ANS="
    set /p "ANS=     Launch FatFish now? type y to start, Enter to skip : "
    if /i "!ANS!"=="y" (
        echo       [OK] Launching in a separate window...
        start "" cmd /k "%~dp0__LAUNCHER__"
    ) else (
        echo       [i] Not launched. Double-click __LAUNCHER__ anytime.
    )
)

echo.
pause
exit /b 0
'''


def resolve_edition(out_name, explicit=None):
    """Work out the edition badge: explicit argument, else the filename, else EDITION."""
    if explicit:
        return explicit
    stem = os.path.basename(out_name).upper()
    if stem.startswith("FATPACK") and stem.endswith(".BAT"):
        tail = stem[len("FATPACK"):-len(".BAT")]
        if tail in EDITION_TAGS:
            return tail
    return EDITION


def build_manifest():
    """Return [(name, bytes), ...] for the files that exist."""
    items = []
    for name in EMBED:
        if not os.path.isfile(name):
            print("  [!] skipping (not found): %s" % name)
            continue
        with open(name, "rb") as f:
            items.append((name, f.read()))
    return items


def local_py_modules():
    """Names of the local modules in this directory (*.py without the suffix)."""
    try:
        return {f[:-3] for f in os.listdir(".") if f.endswith(".py")}
    except Exception:
        return set()


def audit_embed():
    """Check that EMBED covers the local module dependencies.

    Two checks:
      1. SELFCHECK_MODULES must be a subset of EMBED, otherwise the installer's
         step 6 is self-certifying and a missing module still prints [OK].
      2. Every .py in EMBED is parsed; imports that resolve to a module present
         in this directory must also be in EMBED. That is exactly the shape of
         the earlier bug where ui_core / verify_tools / settings were left out.

    Never raises. Returns a list of problems (empty means it passed).
    """
    problems = []
    try:
        import ast
    except Exception as e:
        return ["cannot import ast, audit skipped: %s" % e]

    local = local_py_modules()
    emb = set(EMBED)

    for mod in SELFCHECK_MODULES:
        if ("%s.py" % mod) not in emb:
            problems.append("SELFCHECK_MODULES lists %s but %s.py is not in EMBED "
                            "(the self-check would pass falsely)" % (mod, mod))

    for name in EMBED:
        if not name.endswith(".py") or not os.path.isfile(name):
            continue
        try:
            with open(name, "r", encoding="utf-8", errors="replace") as fh:
                tree = ast.parse(fh.read())
        except Exception as e:
            problems.append("could not parse %s (skipped in this audit): %s" % (name, e))
            continue
        need = set()
        for node in ast.walk(tree):
            if isinstance(node, ast.Import):
                for a in node.names:
                    need.add(a.name.split(".")[0])
            elif isinstance(node, ast.ImportFrom):
                if node.module and node.level == 0:
                    need.add(node.module.split(".")[0])
        for mod in sorted(need & local):
            if ("%s.py" % mod) not in emb:
                problems.append("%s imports the local module %s.py, which is not in EMBED"
                                % (name, mod))

    return sorted(set(problems))


def _print_audit(problems):
    if problems:
        print("\n" + "!" * 78)
        print("[!] EMBED audit found problems (the install may not run):")
        for p in problems:
            print("    - " + p)
        print("!" * 78)
    else:
        print("\n[i] EMBED audit passed: every local module dependency is covered.")


def generate(out_name, edition=None, strict=False):
    ed = resolve_edition(out_name, edition)

    audit_problems = audit_embed()
    _print_audit(audit_problems)
    if audit_problems and strict:
        print("\n[FATAL] audit failed (--strict): nothing written to %s." % out_name)
        return 3

    items = build_manifest()
    if not items:
        print("Nothing to embed, aborting.")
        return 1

    total = sum(len(d) for _, d in items)
    print("=" * 78)
    print("Embedding %d files, %d bytes" % (len(items), total))
    print("=" * 78)
    print("  %-24s %8s  %s" % ("file", "bytes", "SHA1(first 12)"))
    print("  " + "-" * 60)
    for name, data in items:
        print("  %-24s %8d  %s" % (name, len(data), hashlib.sha1(data).hexdigest()[:12]))

    dict_lines = []
    for name, data in items:
        b64 = base64.b64encode(data).decode("ascii")
        dict_lines.append("    %s: %s," % (repr(name), '"%s"' % b64))
    payload = (PAYLOAD_TMPL
               .replace("__VER__", VERSION)
               .replace("__NAME__", os.path.basename(out_name))
               .replace("__ITEMS__", "\n".join(dict_lines)))

    env_lines = build_env_echo_lines(".env")
    print("\n  .env template: %d line(s) taken from .env" % len(env_lines))

    bat = (BAT_TMPL
           .replace("__VER__", VERSION)
           .replace("__ED__", ed)
           .replace("__OUT__", os.path.basename(out_name))
           .replace("__COUNT__", str(len(items)))
           .replace("__BYTES__", str(total))
           .replace("__SELFCHECK__", ", ".join(SELFCHECK_MODULES))
           .replace("__MLEN__", str(len(MARKER)))
           .replace("__MAIN__", MAIN_SCRIPT)
           .replace("__LAUNCHER__", LAUNCHER)
           .replace("__ENVLINES__", "\n".join(env_lines)))

    if not bat.isascii():
        bad = [(i + 1, l) for i, l in enumerate(bat.splitlines()) if not l.isascii()]
        print("\n[FATAL] the batch shell contains non-ASCII characters, which would "
              "desynchronize cmd's byte-offset parsing:")
        for i, l in bad[:10]:
            print("   line %d: %r" % (i, l))
        return 2

    text = bat + "\n\n\n" + MARKER + "\n" + payload
    text = text.replace("\r\n", "\n").replace("\n", "\r\n")
    raw = text.encode("utf-8")

    with open(out_name, "wb") as f:
        f.write(raw)

    print("  " + "-" * 60)
    print("\nBuilt %s (%s Edition)" % (out_name, ed))
    print("  total size     : %d bytes (%.1f KB)" % (len(raw), len(raw) / 1024))
    print("  shell (bat)    : %d bytes  %s" % (
        len((bat + "\n\n\n" + MARKER + "\n").encode("utf-8")),
        "pure ASCII" if bat.isascii() else "contains non-ASCII"))
    print("  payload (py)   : %d bytes (%.1f KB)" % (
        len(payload.encode("utf-8")), len(payload.encode("utf-8")) / 1024))
    print("  marker         : %s (length %d)" % (MARKER, len(MARKER)))
    print("  marker count   : %d (must be 1)" % raw.decode("utf-8").count(MARKER))
    print("  line endings   : CRLF")
    return 0


def main():
    args = list(sys.argv[1:])
    strict = "--strict" in args
    if "--manifest" in args:
        items = build_manifest()
        print("Would embed %d files:" % len(items))
        for name, data in items:
            print("  %-24s %8d B" % (name, len(data)))
        try:
            print("  .env template   %8d lines" % len(build_env_echo_lines(".env")))
        except Exception as e:
            print("  .env template   ERROR: %s" % e)
        _print_audit(audit_embed())
        return 0
    out = DEFAULT_OUT
    explicit_ed = None
    i = 0
    while i < len(args):
        a = args[i]
        if a == "--edition" and i + 1 < len(args):
            explicit_ed = args[i + 1]
            i += 2
            continue
        if a.startswith("--edition="):
            explicit_ed = a.split("=", 1)[1]
        elif not a.startswith("--"):
            out = a
        i += 1
    return generate(out, edition=explicit_ed, strict=strict)


if __name__ == "__main__":
    sys.exit(main())
