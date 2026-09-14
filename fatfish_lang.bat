@echo off
REM ============================================================
REM  fatfish_lang.bat -- FatFish shared language probe
REM
REM  Purpose:
REM    Detect the Windows system language and set the environment
REM    variable FISH_LANG, so launchers can pick the right language
REM    for their messages. This script ONLY probes and decides.
REM    It does NOT handle encoding, and it does NOT touch chcp
REM    (each launcher keeps its own "chcp 65001").
REM
REM  Compatibility first:
REM    Multi-level probe with graceful fallback; the first level
REM    that returns a result wins. If everything fails, default
REM    to English. All probe errors are swallowed silently.
REM
REM  NOTE: All comments in this file are pure ASCII on purpose.
REM    A child script must NOT depend on the caller having set
REM    chcp 65001, otherwise its own Chinese comments would be
REM    mis-parsed as commands under a GBK console.
REM
REM  Usage:
REM    call "%~dp0fatfish_lang.bat"
REM    then use %FISH_LANG%  (value: zh or en)
REM
REM  Output variable:
REM    FISH_LANG=zh   system language is Chinese (Simplified/Traditional/HK)
REM    FISH_LANG=en   English, or non-Chinese-non-English, or probe failed
REM
REM  Policy:
REM    Anything that is not Chinese falls back to pure English.
REM    No half-baked translations -- this is the core trade-off.
REM ============================================================

REM ---- Default to English (safest, ASCII never garbles) ----
set "FISH_LANG=en"
set "_FISH_LOCALE="

REM ============================================================
REM  Level 1: PowerShell Get-Culture (built-in since Win7, standard)
REM    Widest availability, so it goes first.
REM ============================================================
for /f "usebackq delims=" %%i in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "[System.Globalization.CultureInfo]::CurrentCulture.Name" 2^>nul`) do (
    if not defined _FISH_LOCALE set "_FISH_LOCALE=%%i"
)

REM ============================================================
REM  Level 2: PowerShell Get-UICulture (UI language, fallback)
REM ============================================================
if not defined _FISH_LOCALE (
    for /f "usebackq delims=" %%i in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "[System.Globalization.CultureInfo]::CurrentUICulture.Name" 2^>nul`) do (
        if not defined _FISH_LOCALE set "_FISH_LOCALE=%%i"
    )
)

REM ============================================================
REM  Level 3: wmic os get locale (stable on old systems, may be
REM    deprecated on new ones). Returns LCID like 0804 / 0409.
REM ============================================================
if not defined _FISH_LOCALE (
    for /f "usebackq skip=1 tokens=1" %%i in (`wmic os get locale 2^>nul`) do (
        if not defined _FISH_LOCALE set "_FISH_LOCALE=%%i"
    )
)

REM ============================================================
REM  Level 4: registry NLS locale (last resort)
REM ============================================================
if not defined _FISH_LOCALE (
    for /f "usebackq tokens=*" %%i in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "(Get-ItemProperty 'HKCU:\Control Panel\International' -Name LocaleName -ErrorAction SilentlyContinue).LocaleName" 2^>nul`) do (
        if not defined _FISH_LOCALE set "_FISH_LOCALE=%%i"
    )
)

REM ============================================================
REM  Decide: only trust what we can identify for sure.
REM    contains "zh" -> zh
REM    contains "en" -> en
REM    anything else / empty -> en (default, no action needed)
REM ============================================================
if defined _FISH_LOCALE (
    echo %_FISH_LOCALE% | findstr /i /c:"zh" >nul 2>nul && set "FISH_LANG=zh"
)

REM ---- Clean up temp variable, do not pollute caller env ----
set "_FISH_LOCALE="

REM This script stays silent and ends quietly.
exit /b 0
