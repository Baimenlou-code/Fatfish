# 🐟 FatFish — English Edition

> **An AI assistant that lives in a black command-line window.**
> You type, it answers. It can also read and write files, search the web, run
> commands, run code, and look at images.

**Version**: `v1.1.1` (EN Edition)
**Runtime**: Windows + Python 3.8 or newer (verified on 3.8.10)
**Edition note**: this is the English-only edition. All UI text, prompts, tool
descriptions, and documentation are English; the source carries no comments.

---

## Contents

1. [What it is](#1-what-it-is)
2. [Get started in 5 steps](#2-get-started-in-5-steps)
3. [Configuration (.env)](#3-configuration-env)
4. [Commands](#4-commands)
5. [The workspace and its tools](#5-the-workspace-and-its-tools)
6. [Dual-AI verify](#6-dual-ai-verify)
7. [The two gates and risk tiers](#7-the-two-gates-and-risk-tiers)
8. [Settings hub: /set](#8-settings-hub-set)
9. [Switching models and providers](#9-switching-models-and-providers)
10. [File overview](#10-file-overview)
11. [Troubleshooting](#11-troubleshooting)
12. [Design notes](#12-design-notes)

---

## 1. What it is

FatFish is a command-line AI assistant that can actually *do* things on your
machine, inside a sandbox it cannot escape by accident:

| Capability | In plain words |
|---|---|
| 💬 Chat | Talk to it; it replies with mood-based coloring |
| 📄 Read files | Give it a path, or use `@path` / `/read` |
| ✍️ Write files | Create, edit, and save files inside the workspace |
| 🌐 Web search | Look up current information when needed |
| 💻 Run commands | Execute CMD / Shell commands and Python code |
| 🖼 Images | Read image files and send them to a vision model |

Two safety mechanisms sit between the model and your disk:

- **Manual approval** — write / delete / execute actions are batched and shown
  to you for a yes/no before anything happens.
- **Dual-AI verify** — a second AI reviews the planned actions first and can
  send them back for clarification or revision.

---

## 2. Get started in 5 steps

### Step 1 — Install Python

Get Python 3.8+ from <https://www.python.org/downloads/>.
**Tick "Add Python to PATH"** during installation.

Verify: press `Win + R`, type `cmd`, hit Enter, then run:

```bat
python --version
```

### Step 2 — Unpack / clone

Put the program files in a directory of your choice, e.g. `C:\FatFish`.

### Step 3 — Create the config file

If `.env` does not exist yet, launch the program once — the launcher generates
a template automatically. Or copy the template section below into a new file
named `.env`.

**Minimum required**: `FATFISH_API_KEY`.

| Key | Purpose | Where to get it | Prefix |
|---|---|---|---|
| `FATFISH_API_KEY` | **Required** — the chat brain. Legacy `DEEPSEEK_API_KEY` still works as a fallback | <https://platform.deepseek.com/> | `sk-` |
| `TAVILY_API_KEY` | Optional — web search. Without it the web tools degrade quietly instead of crashing | <https://tavily.com/> | `tvly-` |

> 💡 **Rule that trips everyone up**: never put a trailing comment on an
> **empty** value line. `python-dotenv` does not strip it, so `KEY=  # note`
> becomes a value containing `#` and non-ASCII characters, which then breaks
> HTTP headers with `UnicodeEncodeError: 'ascii' codec`.

> 💡 **No spaces around `=`**. Write `KEY=value`, not `KEY = value`.

### Step 4 — Launch

Double-click **`fatfish1.1.1.bat`**.

You will see **two windows**, and that is normal:

1. **Runtime window** — the main chat REPL.
2. **Watcher window** — live-scrolls the output of any sub-program FatFish runs,
   so you can see what a command actually printed.

### Step 5 — After editing `.env`

Type `/reload` inside FatFish. You should see the reload confirmation. No
restart needed.

---

## 3. Configuration (.env)

The file is intentionally plain ASCII so it can be embedded into the installer
and so that nothing can garble it. Full reference:

### 3.1 Main model (the executor)

| Key | Default | Meaning |
|---|---|---|
| `FATFISH_API_KEY` | *(empty)* | **Required.** Empty falls back to `DEEPSEEK_API_KEY` |
| `FATFISH_BASE_URL` | `https://api.deepseek.com` | Any OpenAI-compatible endpoint |
| `FATFISH_MODEL` | `deepseek-flash` | Model name |

Legacy fallbacks: `DEEPSEEK_API_KEY` / `DEEPSEEK_BASE_URL` / `DEEPSEEK_MODEL`.

### 3.2 Web search (optional)

| Key | Default | Meaning |
|---|---|---|
| `TAVILY_API_KEY` | *(empty)* | Without it, web search is unavailable (quiet degradation) |

### 3.3 Dual-AI verify (the second AI)

| Key | Default | Meaning |
|---|---|---|
| `VERIFIER_API_KEY` | *(empty)* | Empty means "reuse the main key" |
| `VERIFIER_BASE_URL` | `https://api.deepseek.com` | Verifier endpoint |
| `VERIFIER_MODEL` | `deepseek-flash` | Ideally a **different** model from the main one |

### 3.4 Verification behaviour

| Key | Default | Meaning |
|---|---|---|
| `VERIFY_MODE` | `auto` | `off` / `auto` (side-effect actions only) / `all` (even read-only) |
| `VERIFY_STRICT` | `1` | `1` = block when the retry limit is exceeded / `0` = allow |
| `VERIFY_MAX_RETRIES` | `4` | Max send-backs per turn (revise only) |
| `VERIFY_MAX_SUPPLEMENTS` | `4` | Free "explain yourself" rounds, not counted as send-backs |
| `VERIFY_FINAL_ANSWER` | `0` | Also review the final answer |
| `VERIFY_FAIL_MODE` | `open` | `open` = allow and warn when the verifier is unreachable / `closed` = block |
| `VERIFY_MIRROR` | `1` | Mirror review comments into the watcher window |

### 3.5 Submission length caps (characters)

Leave empty or `0` to use the built-in defaults.

| Key | Default | Meaning |
|---|---|---|
| `VERIFIER_PROMPT_CHARS` | 64000 | Total submission cap |
| `VERIFIER_PLAN_CHARS` | 16000 | Executor statement / plan (the core of the submission) |
| `VERIFIER_ACTION_PREVIEW` | 4000 | Per-action argument preview |
| `VERIFIER_REPLACE_PREVIEW` | 2000 | Each of `old` / `new` for replacements |
| `VERIFIER_GOAL_CHARS` | 6000 | The user's request |
| `VERIFIER_CONTEXT_CHARS` | 24000 | Recent context |
| `VERIFIER_ANSWER_CHARS` | 24000 | Answer under review |
| `VERIFIER_ANSWER_CTX` | 12000 | Context during answer review |
| `VERIFIER_MAX_TOKENS` | 2000 | Review output cap |
| `VERIFIER_TIMEOUT` | 120 | Single review request timeout (seconds) |
| `VERIFIER_TEMPERATURE` | 0.2 | Verifier temperature |

### 3.6 Approval

| Key | Default | Meaning |
|---|---|---|
| `APPROVE_RUN_TOOLS` | `1` | `1` = running commands / code also needs manual approval |
| `APPROVE_SCOPE` | `writes` | `writes` = read-only skips approval (default; sensitive files still ask) / `all` = read-only also asks |
| `VERIFY_READONLY_PYTHON` | `1` | `1` = statically read-only Python skips *AI verify* (manual approval unchanged) |
| `AUTO_APPROVE_ENABLED` | `1` | Allow one-key auto-approve |
| `AUTO_APPROVE_SCOPE` | `all` | `none` / `writes` / `all` (see [§7](#7-the-two-gates-and-risk-tiers)) |

### 3.7 Networking

| Key | Default | Meaning |
|---|---|---|
| `NET_MODE` | `ai` | `on` / `off` / `auto` (keyword rules) / `ai` (a second AI decides) |
| `TAVILY_MODE` | `auto` | `auto` / `search` / `extract` / `both` |
| `EXTRACT_MAX_LEN` | 8000 | Max characters per extracted section |

### 3.8 Context and interaction

| Key | Default | Meaning |
|---|---|---|
| `MAX_HISTORY` | 500 | Retained history messages |
| `MAX_HISTORY_TOKENS` | 800000 | Token budget for history (an estimate) |
| `MAX_REPLY_TOKENS` | 131072 | `max_tokens` for a single reply |
| `MAX_TOOL_ROUNDS` | 512 | Tool-call rounds per turn |
| `TRIM_TOOL_CLIP_CHARS` | 40000 | Middle-truncate a tool result longer than this |
| `API_TIMEOUT` | 900 | Single API request timeout (seconds) |

### 3.9 Interface

| Key | Default | Meaning |
|---|---|---|
| `SHOW_TIMER` | `True` | Show round cost / linger time |
| `SHOW_WAIT_ANIM` | `True` | Show the spinner while waiting |
| `WAIT_ANIM_INTERVAL` | `0.08` | Spinner frame interval (seconds) |
| `BOOT_REPORT` | `1` | Print the boot-report panel at startup |
| `BOOT_REPORT_PEERS` | `1` | Boot report scans for same-name program copies |

---

## 4. Commands

### Read paths

| Command | Effect |
|---|---|
| `@path` | Read a file or one directory level |
| `/read path...` | Read several paths (one level) |
| `/file path...` | Same as `/read` |
| `/open path...` | Same as `/read` |
| `/readr path...` | Recursively read a whole directory tree |

### Workspace

| Command | Effect |
|---|---|
| `/ws` | Command overview |
| `/ws ls [path]` | List a directory |
| `/ws read <path>` | Read a file |
| `/ws write <path> <text>` | Write / overwrite |
| `/ws append <path> <text>` | Append |
| `/ws rm <path>` | Delete |
| `/ws mkdir <path>` | Create a directory |
| `/ws search <keyword>` | Full-text search |
| `/ws cd <path>` | Switch the workspace root |
| `/ws where` | Show the current workspace |
| `/ws reset` | Restore the default workspace |
| `/ws forget [path]` | Clear read tickets (all, or one file) |

### Session

| Command | Effect |
|---|---|
| `/status` | Runtime status overview |
| `/clear` | Clear the chat history |
| `/reload` | Reload `.env` (main model + verifier + Tavily together) |
| `/help` | Show help |
| `exit` / `quit` | Exit |

### Timer and approval

| Command | Effect |
|---|---|
| `/timer [on\|off]` | Toggle the round timer |
| `/auto on\|off\|now` | One-key auto-approve: toggle, or release this round immediately |

Auto-approve scope is controlled by `/set auto_approve_scope`:

- `none` — never auto-approve anything (strictest)
- `writes` — release content writes only (delete / exec still ask)
- `all` — release everything except sensitive files (delete / exec included, at your own risk)

### Model, verify, network

| Command | Effect |
|---|---|
| `/model` | Show the main model and base URL |
| `/model <name>` | Switch the main model for this session |
| `/verify` | Show dual-AI verify status |
| `/verify on\|off\|all` | Enable (side-effect only) / disable / verify everything |
| `/verify strict on\|off` | Block or allow when over the retry limit |
| `/verify model <name>` | Switch the verifier model |
| `/verify answer on\|off` | Review the final answer too |
| `/verify retries <n>` | Max send-backs (revise only) |
| `/verify supplements <n>` | Max free clarification rounds |
| `/verify fail open\|closed` | Behaviour when the verify service is down |
| `/verify mirror on\|off` | Mirror review comments to the watcher |
| `/verify ping` | Connectivity self-test (one real call) |
| `/net` / `/net on\|off\|auto\|ai` | Show / set the web mode |
| `/tavily` / `/tavily auto\|search\|extract\|both` | Show / set the retrieval mode |
| `/search <query>` | Force one web search regardless of mode |
| `/set` | Settings hub (see [§8](#8-settings-hub-set)) |

> Paths containing spaces must be wrapped in double quotes, e.g.
> `/ws read "my notes/readme.md"`.

**Answer markup** — the model can color its output:

```
{{green}}done{{/green}}  {{bold}}important{{/bold}}  {{red}}warning{{/red}}
{{rainbow}}colorful{{/rainbow}}
```

Available tags: `red green yellow blue cyan magenta white gray` plus
`bold italic underline dim rainbow`. Used sparingly, for emphasis only.

---

## 5. The workspace and its tools

FatFish can only touch files inside a **workspace root** (a sandbox). Anything
outside it is refused.

### The 14 tools

| Tool | Side effect | Notes |
|---|---|---|
| `ws_where` | none | Show the workspace root |
| `ws_list` | none | List a directory tree |
| `ws_read` | none | Read a file; issues a **read ticket** |
| `ws_search` | none | Full-text search |
| `ws_forget` | none | Clear read tickets |
| `ws_mkdir` | low | Create a directory |
| `ws_cd` | low | Switch the workspace root |
| `ws_cd_approve` | low | Approve a pending switch that leaves the default sandbox |
| `ws_write` | writes | Write / overwrite (full overwrite needs no prior read) |
| `ws_append` | writes | Append (needs a read ticket) |
| `ws_replace` | writes | Exact text replacement (needs a read ticket) |
| `ws_delete` | writes | Delete (needs a read ticket) |
| `ws_run_cmd` | exec | Run a CMD / Shell command |
| `ws_run_python` | exec | Run a Python snippet |

### Read tickets

`ws_append`, `ws_replace`, and `ws_delete` require that the file was read
**first** in the same session. The ticket records the file's mtime and size, so
it expires automatically if anything changes the file behind FatFish's back.
This prevents blind edits.

### Automatic backups

Before a **root-level** file is overwritten, appended to, replaced in, or
deleted, its previous content is copied to `_backup/<name>.<timestamp>.bak`.

### Sensitive-file guard

Reading—not just writing—a file that matches the credential list (`.env`,
`*.key`, `*.pem`, `id_rsa*`, `credentials.json`, ...) is treated as sensitive
and forced through manual approval. "Read-only" does not mean "risk-free": the
content travels to an external model, which is equivalent to sending the file.

### Switching the workspace root

Moving the root **outside the default sandbox** is blocked and registered as a
pending request. FatFish returns a notice; only after you explicitly agree does
`ws_cd_approve` carry it out.

---

## 6. Dual-AI verify

Before any action with side effects, a **second AI** reviews it.

### Why

A single model judging its own plan is a weak check. A verifier with an
independent persona—and ideally a different model—catches things the executor
rationalized away.

### The three verdicts

| Verdict | Meaning |
|---|---|
| `approve` | Sound and safe; proceed |
| `supplement` | Viable, but the purpose / blast radius / rollback are unclear; explain and resubmit |
| `revise` | Wrong or risky, or a clearly better option exists; change the plan and resubmit |

The verifier may answer in a terse shorthand to save tokens: `y` for approve,
`n` for revise.

### Counting rules

- A `supplement` costs a **free** round (up to `VERIFY_MAX_SUPPLEMENTS`) and does
  not count as a send-back.
- A `revise` counts against `VERIFY_MAX_RETRIES`.
- When the limit is exceeded: `VERIFY_STRICT=1` blocks the action;
  `VERIFY_STRICT=0` allows it to proceed to manual approval.

### What the verifier sees

The user's original request, the executor's stated plan, the exact list of
planned actions, recent context, and—importantly—the **user's real input log**,
collected straight from the local input channel. That log cannot be forged by
the model, so the verifier can tell whether *you* already authorized something.

### Consistency note

When re-submitting the same action, the verifier is told it is round 2+ and
instructed not to invent unrelated new demands. This prevents an endless loop of
shifting requirements.

### Mirroring

With `VERIFY_MIRROR=1`, review comments are written to
`logs/YYYY/MM/DD/exec_verify_<pid>.out`, which the watcher tails—so verdicts
scroll live in the second window.

### Test stubs

Environment variables make the whole system testable without network access:

| Variable | Effect |
|---|---|
| `FATFISH_VERIFY_MOCK=approve\|supplement\|revise` | Force a verdict |
| `FATFISH_JUDGE_MOCK=true\|false\|extract` | Force a web-need decision |
| `FATFISH_VERIFY_DEBUG=1` | Print stack traces to stderr |

---

## 7. The two gates and risk tiers

Every side-effecting action passes **two independent gates**:

```
   model plans an action
           │
           ▼
   ①  Dual-AI verify      ← second AI reviews the plan
           │ approve
           ▼
   ②  Manual approval     ← you press y/a/n in the terminal
           │ approved
           ▼
       executed
```

### Risk tiers

| Tier | Tools | Verify | Manual approval |
|---|---|---|---|
| **L0** read-only | `ws_where` `ws_list` `ws_read` `ws_search` `ws_forget` | only in `all` mode | **no** — except `ws_read` on a sensitive file |
| **L1** low, reversible | `ws_mkdir` `ws_cd` `ws_cd_approve` | yes | no |
| **L2** content writes | `ws_write` `ws_append` `ws_replace` `ws_delete` | yes | **yes** |
| **L3** execution | `ws_run_cmd` `ws_run_python` | yes | **yes** |

Tiers are the single source of truth, so the approval list and the verify list
cannot drift apart.

### One-key auto-approve

Pressing `a` or `1` during an approval prompt approves this batch **and**
releases the rest of the round. Its scope is capped by
`AUTO_APPROVE_SCOPE`:

| Scope | Effect |
|---|---|
| `none` | Never auto-approve (batch confirmation only) |
| `writes` | Release content writes; delete / exec still ask |
| `all` | Release everything except **sensitive files** |

> ⚠️ At the default `all`, the manual gate is effectively bypassed for
> delete / exec. That is a deliberate trust-mode trade-off. The verifier is
> informed in the submission note that "this batch has no human safety net" and
> is asked to hold its normal standard rather than relax it.

Sensitive files (`.env`, keys, credentials) are **never** auto-approved, for
reading or writing, in any scope.

---

## 8. Settings hub: /set

All tunables live in one registry with three concepts:

- **default** — the factory value captured at registration
- **current** — the value in force (possibly changed by `.env` or `/set`)
- **dirty** — whether current differs from default (shown as `*`)

| Command | Effect |
|---|---|
| `/set` | List settings (advanced items hidden) |
| `/set all` | List everything |
| `/set diff` | Show only what differs from the defaults |
| `/set <key>` | Show one setting's current value and default |
| `/set <key> <value>` | Change it for this run |
| `/set reset [key]` | Restore one, or all, to factory defaults |
| `/set save` | Persist the deviations to `.env` |
| `/set profile [name]` | List / apply a profile |
| `/set profile <name> --show` | Preview what a profile would change |
| `/set profile <name> --reset` | Restore everything first, then apply |

### Built-in profiles

| Profile | Idea |
|---|---|
| `cheap` | Compress submissions to cut token use |
| `strict` | Verify even read-only; stop on failure; no auto-approve |
| `fast` | Fewer interruptions, no approval prompts (trusted scenarios) |
| `manual` | Every operation needs human confirmation |
| `offline` | No web search; model knowledge only |
| `debug` | Full verification + mirror + timer, easy to observe |
| `default` | Restore the startup configuration |

Profiles are **atomic**: if any referenced key is unknown or any value is
illegal, the whole profile is rejected rather than applied halfway.

`/set save` writes only settings that deviate from their defaults, and it edits
existing lines in place instead of appending duplicates.

---

## 9. Switching models and providers

Which API and model FatFish uses is decided entirely by `.env`. Zero code
changes.

```ini
FATFISH_API_KEY=sk-xxxxxxxxxxxxxxxx
FATFISH_BASE_URL=https://api.moonshot.cn/v1
FATFISH_MODEL=moonshot-v1-8k
```

Then type `/reload` inside FatFish.

### Three things to check when switching

1. **The verifier too.** If `VERIFIER_*` is left empty it follows the main
   config; if you point the main model at another vendor, update or clear the
   verifier settings so it does not end up pointing at the old one.
2. **Two capabilities are mandatory.** The endpoint must support
   Function Calling (the `tools` parameter) and `max_tokens`. Without tool
   calling, every workspace tool stops working.
3. **Parameter differences.** FatFish sends `max_tokens`, `tools`, and
   `response_format` (for the verifier). If a provider returns HTTP 400, read
   the error and lower `MAX_REPLY_TOKENS` if needed.

### Temporarily without editing .env

Environment variables take precedence over `.env`:

```bat
set FATFISH_MODEL=moonshot-v1-8k
set FATFISH_BASE_URL=https://api.moonshot.cn/v1
python launch.py
```

---

## 10. File overview

### Top-level files (19)

| File | Purpose |
|---|---|
| `FATENFISH.py` | The main program (chat REPL + tool loop) |
| `ui_core.py` | View layer: colors, mood, markup, banner, spinner |
| `verify_tools.py` | Dual-AI verify engine (the second AI reviewer) |
| `settings.py` | Settings registry: register / snapshot / profiles / persist |
| `boot_report.py` | Boot report: environment snapshot injected into the prompt |
| `common.py` | Shared primitives: timestamps, date-layered directories |
| `workspace.py` | Workspace sandbox, the 14 tools, sensitive-file guard |
| `file_tools.py` | File / directory / image reading, multimodal assembly |
| `net_tools.py` | Tavily web search / extract |
| `exec_tools.py` | Command and Python execution engines |
| `fatfish_watcher.py` | Watcher: tails sub-program output |
| `launch.py` | Launch hub: captures the real PID, starts the watcher |
| `fatfish1.1.1.bat` | Launcher (double-click this) |
| `fatfish_runtime.bat` | Runtime window: writes the PID marker, runs launch.py |
| `fatfish_lang.bat` | Language probe (sets `FISH_LANG`) |
| `README.md` | This file |
| `.env` | Your keys — **never share or commit this** |
| `.gitignore` | Leak-prevention list |
| `_fatfish_pid.txt` | Runtime window PID (created at start, deleted on exit) |

### Runtime directories

| Directory | Purpose |
|---|---|
| `workspace/` | The sandbox: the only place FatFish may freely read and write |
| `logs/` | Chat logs and execution output (layered by year → month → day) |
| `generated_code/` | Code FatFish saved automatically |
| `_backup/` | Automatic backups of root-level files |
| `.fatfish_tmp/` | Temporary scripts (created at runtime, removed after) |
| `__pycache__/` | Bytecode cache |

> `logs/`, `generated_code/`, `__pycache__/`, and `*.pyc` are git-ignored, as is
> `.env`.

---

## 11. Troubleshooting

| Symptom | Likely cause and fix |
|---|---|
| `python` not found | Python is not on `PATH`. Reinstall and tick "Add Python to PATH" |
| Chat fails immediately | `FATFISH_API_KEY` is wrong, missing, or has stray spaces |
| `Model does not exist` | Change `FATFISH_MODEL` in `.env`, then `/reload`. Or try `/model <name>` |
| Web search says unavailable | `TAVILY_API_KEY` missing or expired (HTTP 401) |
| `UnicodeEncodeError: 'ascii' codec` | Classic: an **empty** value line has a trailing comment. Move the comment to its own line |
| Edited `.env` but nothing changed | Type `/reload` |
| Two windows confuse you | Normal: one is the REPL, the other is the watcher |
| Verifier always blocks | Check `/verify` status; try `/verify strict off`, or `/verify off` temporarily |
| Verifier always approves | It may be unreachable and `VERIFY_FAIL_MODE=open`. Check `/verify ping` |
| Tool call did nothing | Read the approval prompt; a rejected batch is not retried |

### The one mistake everyone makes

```ini
# WRONG - the comment becomes part of the value
FATFISH_API_KEY=   # put your key here

# RIGHT - comment on its own line
# put your key here
FATFISH_API_KEY=
```

---

## 12. Design notes

A few choices that look odd until you know why.

**Why the launcher / runtime / launch.py split?**
Batch files cannot reliably obtain a child process's real PID (`%errorlevel%`
is only an exit code). Python's `subprocess.Popen` gives the pid cleanly. So the
chain is: launcher → runtime window → `launch.py` (captures the PID, starts the
watcher) → main program. Each piece does one thing.

**Why is `_fatfish_pid.txt` the runtime window PID, not the Python PID?**
It exists so external tools can identify and close the FatFish *window*. The
window belongs to `cmd.exe`, so that is the PID recorded. The boot report
therefore never compares it against the Python PID—they are inherently
different numbers.

**Why a spinner on stderr?**
stdout is wrapped by a tee stream and, during slash commands, is captured and
injected into the model. Animation frames written to stdout would pollute the
context with hundreds of refresh lines.

**Why is `.env` plain ASCII?**
It gets embedded into the single-file installer, and batch-file parsing works on
byte offsets—non-ASCII text in the shell layer can desynchronize it. Keeping the
config ASCII makes the whole pipeline safe.

**Why does the watcher have a startup baseline?**
Without it, every cold start replays all of the day's earlier `exec_*.out` files
from the beginning, which floods the log. With the baseline it behaves like
`tail -f`: only bytes appended after startup.

**Why "degrade, never crash"?**
Every module is written so a missing dependency or a failed step falls back to a
sensible default rather than taking down the program. A broken boot report, for
instance, simply returns the original prompt unchanged.

---

## License and provenance

A personal project. The version history—including the early prototypes and the
reasoning behind major changes—lives in the repository's `CHANGELOG.md`, and
each tagged release carries its installer as a downloadable asset.

---

> 📌 The only four files you really need to run it:
> the installer (or the extracted sources), **`fatfish1.1.1.bat`** to launch,
> **`.env`** to configure, and **`FATENFISH.py`** as the program body.
