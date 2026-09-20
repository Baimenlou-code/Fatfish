# 🐟 肥鱼 FatFish —— 猪都能看懂的说明书（v1.1.1 · 文档第 3.1 版）
# 🐟 FatFish — A Manual Even a Pig Can Understand (v1.1.1 · Doc Rev.3.1)

> 一句话：**肥鱼是一个住在黑色命令行窗口里的 AI 助手。**
> 你打字问它，它回答你；它还能帮你读写文件、上网查资料、跑命令、跑代码、看图片。
>
> One line: **FatFish is an AI assistant that lives in a black command-line window.**
> You type, it answers; it can also read/write files, search the web, run commands, run code, and look at images.

**当前版本 Current version**：`v1.1.1`（横幅自称「H 版」/ banner calls itself "H Edition"）
**文档修订 Doc revision**：第 3.1 版，2026-09-19 / Rev.3.1, 2026-09-19
**运行环境 Runtime**：Windows + Python 3.8+（实测 3.8.10 通过 / verified on 3.8.10）

不用懂编程，照着下面做就行 👇
No programming knowledge needed — just follow along 👇

---

## 〇、这一版文档改了什么？ / What Changed in This Doc Revision?

| 项目 / Item | 旧文档 / Old Doc | 新文档 / New Doc |
|---|---|---|
| 启动器文件名 / Launcher name | `fatfish1.0.2.bat` ❌ | ✅ `fatfish1.1.1.bat` |
| 主程序文件名 / Main script | `FATFFISHI.py` ❌ | ✅ `FATHFISH.py` |
| 启动链路 / Launch chain | 没写 / not documented | ✅ 四层链路全图解 / full 4-layer diagram |
| 监控器窗口 / Watcher window | 没写 / not documented | ✅ 独立说明 / its own chapter |
| 跑命令 / 跑 Python | 只提一句 / one line | ✅ 独立章节 + 权限与落盘 / own chapter |
| 读图片 / Image input | 没写 / not documented | ✅ 独立说明 / documented |
| 数值上限 / All limits | 没写 / not documented | ✅ 完整参数表 / full table |
| 已知问题 / Known issues | 没写 / not documented | ✅ 实测发现 4 条 / 4 findings |
| 章节编号 / Chapter numbers | 「三步」下面却有第 3、4 步 ❌ | ✅ 编号已理顺 / renumbered |

### 📌 更新记录 / Changelog

> 📦 **本节的更新记录已于 2026-09-20 剥离独立存放。**
> 完整编年史（含 README 从未记录的原型阶段与 `v1.0.x` 阶段）见：
> **`oldver/CHANGELOG.md`**
> The changelog has been moved out and consolidated into `oldver/CHANGELOG.md`.
> 本节原有 `rev.2` ~ `rev.3.1` 全部条目已完整并入该文件，无遗漏。

---

## 一、肥鱼能干啥？ / What Can FatFish Do?

| 能力 / Capability | 说人话 / In Plain Words |
|---|---|
| 💬 聊天 / Chat | 跟它说话，它回答你。回答还会**按情绪变色** / Talk to it, it replies — with mood-based colors |
| 📄 读文件 / Read files | 给路径就把它读进上下文，支持 `@路径` 和 `/read` / Give it a path; supports `@path` and `/read` |
| 📚 读整个目录 / Read directories | `/readr 目录` 递归读完整个目录树 / `/readr <dir>` walks the whole tree |
| 🖼️ 看图片 / See images | 图片按**文件头魔数**识别真伪，支持 PNG/JPEG/GIF/WebP / Magic-number sniffing, PNG/JPEG/GIF/WebP |
| ✍️ 写文件 / Write files | 新建、覆盖、追加、精确替换、删除、建目录、全文搜索 / create, overwrite, append, replace, delete, mkdir, search |
| 🛡️ 写文件要你点头 / Writes need approval | **写 / 追加 / 替换 / 删除 / 跑命令 / 跑代码**执行前会批量问你一次，按 `y` 才动；**只读**默认免报批（敏感文件除外）/ writes & execs ask once per batch; reads are approval-free by default |
| 🌐 联网搜索 / Web search | Tavily 搜索 + 网页正文抓取，三档模式 / Tavily search + page extract, 3 modes |
| 💻 跑命令 / Run commands | 让它在工作台里执行 CMD 命令 / runs CMD commands inside the workspace |
| 🐍 跑 Python / Run Python | 让它在工作台里跑 Python 代码，traceback 可见 / runs Python with real tracebacks |
| 💾 自动存代码 / Auto-save code | 它写的代码块自动命名存盘（名字由 AI 起）/ auto-names and saves code blocks |
| 🪟 实时输出窗口 / Live output window | 另开一个黑窗，实时滚动显示子程序输出 / a 2nd window tails sub-program output |
| 🌏 多语言 / Multi-language | 探测系统语言，中文系统给中文界面 / probes system language |

---

## 二、五步跑起来（小白版） / Get Started in 5 Steps (Beginner Edition)

### 第 1 步：装 Python / Step 1: Install Python

1. 打开 <https://www.python.org/downloads/> / Open <https://www.python.org/downloads/>
2. 下载并安装 **Python 3.8 或更高版本** / Download and install **Python 3.8 or newer**
3. ⚠️ **安装时一定要勾选 `Add Python to PATH`**（在安装界面最下面那个小方框）
   ⚠️ **Be sure to check `Add Python to PATH`** (the small checkbox at the bottom)
4. 装完**重启电脑**（或至少关掉所有命令行窗口）/ **Restart your PC** after installing

> 怎么确认装好了？按 `Win + R`，输入 `cmd` 回车，打 `python --version`，
> 能显示版本号（比如 `Python 3.11.5`）就成功了。
>
> How to verify? Press `Win + R`, type `cmd`, hit Enter, then type `python --version`.

### 第 2 步：一键安装肥鱼 / Step 2: One-Click Install

**双击 `FATPACKII.bat`**，然后等它自己跑完。
/ **Double-click `FATPACKII.bat`**, and wait.

它会自动做 7 件事 / It does 7 things automatically:

| 步骤 / Step | 干什么 / What It Does |
|---|---|
| `[1/7]` | 检查 Python，没装就给出下载指引 / checks Python, prints download guide if missing |
| `[2/7]` | **从自己文件末尾解压出 12 个程序文件**（内嵌 base64）；覆盖前先备份到 `_backup/` / self-extracts 12 files and backs up any old ones |
| `[3/7]` | 创建虚拟环境 `venv`（失败就退回全局 Python）/ creates `venv` (falls back to global) |
| `[4/7]` | 装依赖 `openai` / `python-dotenv` / `requests`，失败自动换**清华镜像**重试 / installs deps, retries via Tsinghua mirror |
| `[5/7]` | 生成 `.env` 模板并**自动用记事本打开**（已存在则原样保留）/ creates `.env`, opens Notepad, keeps an existing one |
| `[6/7]` | 自检：试导入所有核心模块 / self-check: imports all core modules |
| `[7/7]` | 询问是否**立刻启动肥鱼** / asks whether to launch FatFish right away |

> 💡 第 2 步的原理很酷：安装器本身是「**脚本 + 压缩包二合一**」。
> 它会用 PowerShell 在自己文件里找 `##PYBEGIN##` 标记，把标记之后的 base64 数据
> 抠出来变成一段临时 Python 脚本跑掉，于是程序文件就被「吐」出来了。详见第八章。
>
> 💡 Cool part: the installer is a **script + archive in one file**. See Chapter 8.
>
> 🧪 需要静默安装（自动化 / CI）？加两个环境变量：
> `set FATFISH_SKIP_VENV=1`（不建 venv）、`set FATFISH_NO_GUI=1`（不弹记事本、不问启动）。

### 第 3 步：填 API Key（关键！） / Step 3: Fill In Your API Key (Crucial!)

上一步会自动打开 `.env`，长这样 / The previous step opens `.env`, which looks like:

```
# ============================================================
#  FatFish .env - fill in your keys, then type /reload
#  Chinese reference: README.md section 18.6
#  Rule: never put a trailing comment on an EMPTY value line.
#  ASCII only. Avoid parentheses and percent signs on every line.
# ============================================================

# ---- main model, the executor ----
FATFISH_API_KEY=<your DeepSeek key>
FATFISH_BASE_URL=https://api.deepseek.com
FATFISH_MODEL=deepseek-flash

# ---- web search (optional) ----
TAVILY_API_KEY=<your Tavily key>

# ---- dual-AI verification, the reviewer ----
VERIFIER_API_KEY=
VERIFIER_BASE_URL=https://api.deepseek.com
VERIFIER_MODEL=deepseek-flash

# ... 其余 50+ 项（VERIFY_* / 送审上限 / 人工报批 / 联网）见「18.6 全量配置项清单」
```

把每个 `=` 后面填上你的值（必填项只有 `FATFISH_API_KEY`），保存关闭。/ Fill in the value after each `=`; save and close.

> 💡 **只想换模型 / 换服务商？** 改上面那三行 `FATFISH_*`，再在程序里敲 `/reload` 即可，
> 代码零改动 —— 完整说明见**第十八章**。
> 旧写法 `DEEPSEEK_API_KEY` / `DEEPSEEK_BASE_URL` / `DEEPSEEK_MODEL` 仍然兼容（作为回退）。

| 密钥 / Key | 用途 / Purpose | 申请地址 / Apply At | 前缀 / Prefix |
|---|---|---|---|
| `FATFISH_API_KEY` | **必填**，没它没法聊天 / **Required**（旧名 `DEEPSEEK_API_KEY` 仍兼容） | <https://platform.deepseek.com/> | `sk-` |
| `TAVILY_API_KEY` | 选填，没它不能联网 / Optional, web search only | <https://tavily.com/> | `tvly-` |

> 💡 只填 DeepSeek 也能用，只是不能联网。
> 💡 只填了 DeepSeek 却遇到问题？那正常 —— 联网功能会静默降级，不会崩。

### 第 4 步：启动！ / Step 4: Launch!

**双击 `fatfish1.1.1.bat`**。/ **Double-click `fatfish1.1.1.bat`.**

看到彩色的肥鱼横幅就成功了 🎉 / If you see the colorful FatFish banner, you're in 🎉

**⚠️ 你会看到两个窗口，这是正常的：**

| 窗口 / Window | 标题 / Title | 干什么 / What It Does |
|---|---|---|
| 主窗口 / Main | `🐟 FatFish Runtime v1.1.1` | 你打字聊天的地方 / where you type and chat |
| 监控窗口 / Watcher | （自成一体）/ standalone | 实时滚动显示肥鱼跑的子程序输出 / tails sub-program output |

**两个窗口都是「独立窗口」：程序结束后不会自动关闭，需要按任意键。**
Both windows are **standalone**: they stay open after the program ends; press any key.

> 启动器自己的窗口在跑到第 4 步时会提示「按任意键关闭本启动器窗口」，
> 按一下关掉它就行，主窗口不受影响。

### 第 5 步：改完 Key 不重启？ / Step 5: No Restart After Editing `.env`!

在肥鱼提示符里输入 `/reload`，看到「🔄 已重新加载 .env，API key 已更新」就成功了。
Type `/reload` at the FatFish prompt.

---

## 三、启动链路是怎么串起来的？ / How the Launch Chain Works

这是**旧文档完全没写**、但最容易踩坑的地方。/ This is what the old doc completely missed.

```
你双击 fatfish1.1.1.bat
        │
        │  ① 检查 python 命令在不在 PATH
        │  ② 有 venv / .venv 就激活
        │  ③ 试 import 依赖，缺了就装
        │  ④ 没有 .env 就生成模板并要求填 key
        │  ⑤ start "" cmd /k "fatfish_runtime.bat"   ← 开新窗口
        ▼
fatfish_runtime.bat（新窗口，标题 🐟 FatFish Runtime v1.1.1）
        │
        │  ① chcp 65001（UTF-8 代码页）
        │  ② call fatfish_lang.bat   ← 探测系统语言 → FISH_LANG=zh / en
        │  ③ 用「重定向法」取本窗口（runtime cmd）的父进程 PID，写入 _fatfish_pid.txt
        │     （PowerShell 由本窗口直接启动，父进程即本窗口；不再依赖窗口标题）
        │  ④ 前台运行 python launch.py
        ▼
launch.py
        │
        │  ① subprocess.Popen([python, FATHFISH.py])  ← 拿到【真实 PID】
        │  ② subprocess.Popen([python, fatfish_watcher.py, <真实PID>],
        │                     creationflags=CREATE_NEW_CONSOLE)  ← 独立黑窗
        │  ③ main_proc.wait()  ← 等主程序结束
        │  ④ 再等监控器最多 40 秒收尾，超时就 kill
        ▼
   ┌────────────────────────┐        ┌─────────────────────────────┐
   │  FATHFISH.py（前台）    │        │  fatfish_watcher.py（独立窗）│
   │  聊天主循环 / REPL      │        │  ① tail logs/**/exec_*.out   │
   │  工具调用 / 联网 / 存码 │        │  ② 每 1 秒查一次主程序还活着吗│
   └────────────────────────┘        │  ③ 主程序消失 → 再 drain 3 轮 │
                                     │  ④ 30 秒倒计时后自动退出      │
                                     │     （按任意键可提前退）      │
                                     └─────────────────────────────┘
```

> 📌 **监控器只看「新增」输出**：它启动时会把已存在的 `exec_*.out` 记为**基线**，
> 之后只滚动基线之后的新内容，不会再把当天 / 昨天的历史输出重刷一遍
> （2026-09-19 修订；此前每次冷启动都会整份回放，是 watcher 日志膨胀的主因）。

### 为什么要搞得这么绕？ / Why So Convoluted?

| 设计 / Design | 原因 / Reason |
|---|---|
| 用 `launch.py` 而不是纯 bat 启动 | **纯 bat 拿不到子进程的真实 PID**（`%errorlevel%` 只是退出码）。Python 的 `subprocess.Popen` 能直接拿到 `pid`，还能避开 bat 里多层引号嵌套导致的「闪退」/ bat cannot get a child PID; Python can |
| 监控器用 `CREATE_NEW_CONSOLE` | 让它有**自己独立的黑窗口**，输出不跟主窗口打架 / gives it its own window |
| 主程序不用 `CREATE_NEW_CONSOLE` | 主程序要**继承当前控制台**，否则它的输入输出会跑到别的窗口去 / main inherits the console |
| `fatfish_runtime.bat` 取自己的 PID | 用「重定向法」取真实 runtime cmd PID 写进 `_fatfish_pid.txt`，方便**外部工具识别/关闭肥鱼窗口** / for external tools |

> ⚠️ **注意两个 PID 不是同一个东西：**
> - `_fatfish_pid.txt` 里存的是 **runtime 那个 cmd 窗口的 PID**（由 `fatfish_runtime.bat` 用「重定向法」取父进程 PID 写入；踩过的坑与修法见第十二章「提醒 4」）
> - 监控器盯的是 **`FATHFISH.py` 进程的 PID**（由 `launch.py` 通过 `Popen` 拿到并传参）
>
> 这俩由不同环节产生、用途不同，别搞混。

---

## 四、文件都是干啥的？ / File Overview

### 4.1 一级文件（19 个 / 常驻）/ Top-level files (19)

> 口径：根目录**常驻**文件；`_fatfish_pid.txt` 属运行时产物（退出即删），不计入。

| 文件 / File | 大小 / Size | 行数 / Lines | 干啥的 / Purpose |
|---|---|---|---|
| `FATPACKII.bat` | 654.0 KB | 319 | ✅ **一键安装器**（II 版，内嵌 17 个最新文件）/ the one-click installer |
| `fatfish1.1.1.bat` | 6.5 KB | 106 | **启动器**（先查环境再开新窗口）/ launcher |
| `fatfish_runtime.bat` | 5.4 KB | 106 | **运行窗口**（探语言、取本窗口 PID、跑 launch.py）/ runtime window |
| `fatfish_lang.bat` | 3.9 KB | 90 | **语言探测器**（四级降级 → `FISH_LANG`）/ language probe |
| `launch.py` | 4.5 KB | 124 | **启动枢纽**（拿 PID、拉监控器、等收尾）/ launch hub |
| `FATHFISH.py` | 137.8 KB | 2695 | **主程序**（聊天 REPL + 工具循环 + 轮次计时 + 一键放行 + 等待动画）/ main program |
| `fatfish_watcher.py` | 13.0 KB | 380 | **监控器**（tail 子程序输出 + 存活检测）/ watcher |
| `workspace.py` | 27.3 KB | 732 | **工作台**（路径安全、读过凭证、备份、14 个工具）/ workspace |
| `file_tools.py` | 13.5 KB | 391 | **读文件 / 目录 / 图片**、路径解析、多模态组装 / file & image IO |
| `net_tools.py` | 9.2 KB | 239 | **联网**（Tavily search / extract / auto）/ web tools |
| `exec_tools.py` | 10.8 KB | 343 | **跑命令 / 跑 Python**，输出落盘 / exec engine |
| `common.py` | 4.8 KB | 148 | **公共基础件**（时间戳 / 日期分层目录，单一事实来源）/ shared utils |
| `settings.py` | 21.5 KB | 618 | **参数系统**（`/set` 注册、快照、预设方案）/ settings registry |
| `ui_core.py` | 18.3 KB | 478 | **展示层**（颜色 / 情绪调色 / `{{}}` 标记 / 横幅 / 等待动画）/ UI core |
| `verify_tools.py` | 63.7 KB | 1449 | **双人核验**（第二位 AI 审查员）/ dual-AI verifier |
| `boot_report.py` | 29.6 KB | 751 | **开工自检**（启动环境快照 + 注入提示词）/ boot report |
| `README.md` | 112.1 KB | 2028 | 就是本文件（第 3.1 版；会随文档更新变动）/ this file |
| `.env` | 2.2 KB | 66 | 你的密钥配置（🔴 **绝不要分享 / 上传**）/ your keys |
| `.gitignore` | 114 B | 11 | 防误传名单（第一行 `.env`）/ ignore list |

> 📌 `make_fatpack.py`（安装器生成器，25.9 KB / 629 行）现在位于 `workspace/`。
> 📌 四份专题文档（`切换API说明.md` / `双人核验模式.md` / `核验与报批规则修订_20260919.md` /
> `设置说明.md`）**已归档至 `oldpackmd/`**；核心内容已并入本 README 第十五~十八章。
> 📌 `_fatfish_pid.txt`（6 B）记 runtime 窗口 PID，只在运行时短暂存在。

### 4.2 一级目录（14 个）/ Top-level dirs (14)

| 目录 / Dir | 干啥的 / Purpose | 会被 git 忽略吗 |
|---|---|---|
| `workspace/` | **工作台**：肥鱼唯一能自由读写的区域；`make_fatpack.py` 也在此 / the sandbox | ❌ 不忽略 |
| `logs/`（年 → 月 → 日） | **主程序**的聊天日志与执行输出 / chat & exec logs | ✅ 忽略 |
| `generated_code/`（年 → 月 → 日） | 主程序自动存下的代码 / saved code | ✅ 忽略 |
| `_backup/` | 根一级文件被改前的**自动备份** / auto backups | ❌ 不忽略 |
| `.fatfish_tmp/` | 跑 Python 时的临时脚本 / temp scripts | ❌ 不忽略 |
| `__pycache__/` | Python 字节码缓存 / bytecode cache | ✅ 忽略（`*.pyc`） |
| `agents/` | Agent 模块与注册表（含 `agent_reviewer.py`）/ agent modules | ❌ 不忽略 |
| `venv/` | Python 虚拟环境（安装器创建）/ virtualenv | ❌ 不忽略 |
| `sub/` | 子目录测试残留 / dir-test leftovers | ❌ 不忽略 |
| `chat_logs/` | 旧版聊天日志目录（已由 `logs/` 接管）/ legacy chat logs | ✅ 忽略 |
| `fatfish/` | **git 仓库副本**（含 `.git/` 与独立 `venv/`）/ repo copy | ❌ 不忽略 |
| `oldpackmd/` | 归档：旧安装器 + 4 份专题文档原文 / archived packers & docs | ❌ 不忽略 |
| `oldver/` | 归档：历代版本 + **`CHANGELOG.md` 全量更新记录** / archive & changelog | ✅ 忽略 |
| `workspaceX/` | 归档：早期原型（`FAT-A FISH.py` 等）/ early prototypes | ✅ 忽略 |

> 📌 忽略规则以 `.gitignore` 为准（`.env` / `logs/` / `generated_code/` / `__pycache__/` /
> `*.pyc` / `chat_logs/` / `workspaceX/` / `oldver/`）。

## 五、怎么跟肥鱼说话？ / How to Talk to FatFish

启动后会出现提示符，直接打字回车就行 / Once started, just type and hit Enter:

```
🌐~🔍 你 [You] ▸ 你好，介绍一下你自己
```

提示符还会告诉你当前状态 / The prompt shows your current state:

```
🌐~🔍 你 ▸
│ │  └─ 联网模式图标：🔍 搜索 / 📄 抓正文 / 🎯 自动
│ └──── Tavily 模式图标：🔍 search / 📄 extract / 🎯 auto / 🧩 both
└────── 联网开关：🌐+ 强制开 / 🌐- 关 / 🌐~ 关键词自动 / 🌐🤖 AI 判断（ai 模式）
```

### 5.1 命令速查 / Command Reference

| 你输入 / You Type | 肥鱼会干啥 / What FatFish Does |
|---|---|
| `@某个文件.txt` | 读取这个文件 / read this file |
| `/read 路径...` | 读一个或多个文件（一层）/ read files/dirs, one level |
| `/file 路径...` | 同 `/read` / same as `/read` |
| `/open 路径...` | 同 `/read` / same as `/read` |
| `/readr 文件夹` | **递归**读整个目录树 / recursively read whole tree |
| `/net on` | 打开联网（每次都搜）/ always search |
| `/net off` | 关闭联网 / never search |
| `/net auto` | 关键词规则判断（出厂默认）/ keyword-rule auto |
| `/net ai` | **第二位 AI 判断**要不要搜，并改写检索词 / AI-judged search |
| `/net` | 查看当前模式 / show mode |
| `/tavily search` | 只做关键词搜索（锁定）/ search only |
| `/tavily extract` | 只抓 URL 正文（锁定）/ extract URLs only |
| `/tavily auto` | 有 URL 就抓正文，否则搜索 / auto-pick |
| `/tavily both` | 先搜再抓搜索结果前 2 条正文 / search then extract |
| `/tavily` | 查看当前 Tavily 模式 / show mode |
| `/search 今天天气` | **强制**联网搜一次（无视模式）/ force one search |
| `/ws` | 工作台命令总览 / workspace help |
| `/ws ls [路径]` | 列目录 / list dir |
| `/ws read <路径>` | 读文件 / read file |
| `/ws write <路径> <内容>` | 写/覆盖 / write or overwrite |
| `/ws append <路径> <内容>` | 追加 / append |
| `/ws rm <路径>` | 删除 / delete |
| `/ws mkdir <路径>` | 建目录 / make dir |
| `/ws search <关键词>` | 全文搜索 / full-text search |
| `/ws cd <路径>` | 切换工作台根目录 / switch workspace root |
| `/ws where` | 查看当前工作台 / show workspace |
| `/ws reset` | 恢复默认工作台 / reset workspace |
| `/ws forget [路径]` | 清除「读过凭证」（全部或单个）/ clear read tickets |
| `/timer on` / `/timer off` | 开关「本轮耗时 / 停留时长」计时 / toggle the round timer |
| `/timer` | 查看计时状态 / show timer status |
| `/auto on` / `/auto off` | 开关「一键放行」功能 / toggle one-key auto-approve |
| `/auto now` | **立刻放行本轮**剩余读写（不用等报批）/ approve the rest of this round now |
| `/auto` | 查看放行状态 / show auto-approve status |
| `/status` | 运行状态一览（模型/联网/工作台/核验/放行/计时/目录）/ runtime status |
| `/set` | 参数中心：查看 / 修改 / 还原 / 持久化（详见第十七章）/ settings hub |
| `/set <项> <值>` ｜ `/set save` | 改一项（本次运行生效）｜`save` 写入 `.env` 持久化 / change one；persist |
| `/set profile <名>` | 套用预设方案：`cheap` / `strict` / `fast` / `manual` / `offline` / `debug` / `default` |
| `/model` ｜ `/model <名>` | 查看 ｜ 临时切换主模型（仅本次会话）/ show or switch main model |
| `/verify ...` | 双人核验：`on`/`off`/`all`/`strict`/`model`/`answer`/`retries`/`supplements`/`fail`/`mirror`/`ping`（详见第十五章）|
| `/clear` | 清空聊天记录 / clear chat history |
| `/reload` | 重新读 `.env` / reload `.env` |
| `/help` | 显示帮助 / show help |
| `exit` / `quit` / `退出` | 退出肥鱼 / quit |

> 路径里有空格，用双引号包起来：`/read "我的 文件夹/某个文件.py"`
> Wrap paths with spaces in quotes.

### 5.2 为什么要「读过凭证」？ / Why "Read Tickets"?

一条铁律：**写、改、删一个已存在的文件之前，肥鱼必须先读过它。**

- 读取成功 → 系统发一张凭证，记下文件的 **修改时间 + 大小**
- 之后你改文件 → 凭证自动失效（时间/大小变了）→ 肥鱼必须重新读
- 这样能**防止它基于过期的印象乱改你的文件**

> Writes/edits/deletes require a prior read; the ticket records mtime+size and
> auto-invalidates if the file changed underneath.

### 5.3 计时器：本轮花了多久 / 你停留了多久 / The Round Timer

从 rev.2.1 起，肥鱼每轮都会自动打两个时间戳；rev.2.2 起显示**精简为单行**。
/ Since rev.2.1 FatFish stamps two times per round; rev.2.2 shows them on one short line.

| 指标 / Metric | 起 / From | 止 / To | 回答的问题 / The Question It Answers |
|---|---|---|---|
| **本轮耗时** / round cost | 你按下回车发话那一刻 | 本轮**彻底**跑完（含全部工具调用循环）| 肥鱼这次干活花了多久？ |
| **停留时长** / idle time | 上一轮跑完那一刻 | 你**下一次**发话那一刻 | 我盯着屏幕/去倒水花了多久？ |

实际长这样 / What it looks like:

```text
  ⏱️ 本轮 12.4s

🌐~🎯 你 [You] ▸ 帮我看看日志
  ⏱️ 停留 8.2s
```

设计要点 / Design notes:

- **无死角结算**：主循环用 `try / except / finally` 包住，`finally` 里统一调 `_mark_round_end()`。
  所以不管是正常回复、斜杠命令（走 `continue`）、还是抛异常，**每一轮都会被结算**。
- **退出时不打扰**：`exit` / `quit` / Ctrl+C 时设 `_TIMER_EXITING = True`，只记时刻、不打印耗时，
  告别语保持干净。
- **空输入不打断**：连按回车（空输入被 `continue` 跳过）既不结算也不重置，计时起点保持不变。
- **防时钟回拨**：`_fmt_secs(-5)` 会被夹成 `0.0s`，不会出现「-3.2s」这种怪值。
- **会写进日志**：每轮耗时由**主程序**同时写进当天的聊天日志（写成一行 `本轮耗时 12.43s`）。
- **可关闭**：`/timer off` 之后完全静默（但内部时间戳照常更新，`/timer on` 立刻恢复）。
- **行内紧凑**：只报两个数字 —— `本轮 12.4s` 和 `停留 8.2s`，不占屏、不打断阅读。

时间格式规则 / Formatting rules:

| 时长 / Duration | 显示 / Shown |
|---|---|
| < 60 秒 | `12.4s` |
| < 1 小时 | `1m23.4s` |
| ≥ 1 小时 | `1h02m` |

> 💡 实现上它只用了标准库 `time.time()`（单次约几十纳秒），对性能没有可感知影响。
> 源码位置：`FATHFISH.py` 的 `# ---- 计时器（本轮耗时 / 你停留了多久）----` 区块。

### 5.4 一键放行：本轮别再一句句问我了 / One-Key Auto-Approve

读写操作默认要你逐个批次点头（按 `y`）。当一个任务里肥鱼要连着写十几个文件时，
这个体验就很碎。**一键放行**解决的就是这个。

**怎么用**：报批弹窗时按 `a` 或 `1`。

| 按键 / Key | 效果 / Effect |
|---|---|
| `y` / `yes` / `是` / `批准` / `同意` | 只批准**这一批**（和以前一样）|
| **`a` / `1` / `all`** | 批准这一批，**并且本轮剩余的动作都不再询问**（默认档 `all` 含删除 / 执行；敏感文件永远仍需确认）|
| `n` / 其他 / EOF | 拒绝 |

**实际长这样 / What it looks like:**

```text
  🔐 即将执行以下敏感操作，需你批准：
     1. 写入/覆盖 [write]  README.md  （1234 字符）「# 🐟…」
     2. 删除 [delete]  tmp.txt
     y = 只批这一批 ｜ a 或 1 = 批准并放行本轮剩余全部 ｜ n = 拒绝
  👉 是否批准 [Approve?] [y/a/1/N] a
  🔓 已批准，并且本轮剩余敏感操作全部自动放行（直到你下一条命令）
  ✅ 工作台 ws_write → 已写入 README.md（1234 字符）
  ✅ 工作台 ws_delete → 已删除 tmp.txt

  🔓 本轮自动放行 1 个内容写入操作（无需再确认）   ← 同一轮内后面这些都不再问
  ✅ 工作台 ws_write → 已写入 helper.py（820 字符）
```

**作用域 / Scope**（这是重点）：

```text
你发话  ─────────────────────────────────────►  本轮结束
  │  🔓 放行态一直保持（最多 512 个工具轮次）        │
  └────────────────────────────────────────────────┘
                                                   │
下一条命令 ──► 🔒 立即恢复逐个报批 ◄────────────────┘
```

```
🌐~🔍 你 [You] ▸ 再帮我改一个文件
  🔒 自动放行已结束，恢复逐个报批 [auto-approve expired]   ← 复位提示
  🔐 即将执行以下敏感操作，需你批准：…                     ← 又弹窗了
```

所以它**只在"这一轮"有效**，你一发下一条命令就自动收回 —— 不会出现"忘了关"而长期裸奔的情况。

**四个开关 / Four Switches:**

| 常量 / Command | 默认 | 作用 |
|---|---|---|
| `AUTO_APPROVE_ENABLED` | **`True`** | 功能总开关。关掉后 `a` / `1` 失效（只能 `y` 逐个批）|
| `AUTO_APPROVE_DEFAULT` | `False` | 改成 `True` 则**每轮一开始就处于放行态** —— 等于完全不弹窗 |
| `AUTO_APPROVE_SCOPE` | **`all`** | 分档：`none` 全拦 / `writes` 只放写入 / **`all` 除敏感文件外全放行（含删除 / 执行）** |
| `/auto now` | — | 不想等报批出现，直接放行本轮剩余 |

`/auto` 的状态输出长这样：

```text
  🔐 一键放行：ON ｜ 本轮：已放行 ｜ 每轮默认：需报批
     /auto on | /auto off 开关功能 ｜ /auto now 立刻放行本轮
```

**🛡️ 安全边界：放行只跳过"问你一句"**

这是设计上最要紧的一点 —— 一键放行**不放松任何其它防线**：

> ⚠️ **一处例外要说明白**：默认档 `all` 下，**「删除 / 执行也要逐批点头」这道人工闸门本身**
> 也会进入放行范围（仅敏感文件例外）。上表列的是「另外几道不受影响的防线」，**不等于**「人工确认还在」。
> 被自动放行的批次，程序会在送审材料里**如实告知审查员**「本批无人类兜底，请按原标准从严把关」。
> 想保留人工闸门：`/set auto_approve_scope writes`。

| 防线 / Guard | 放行时是否仍生效 |
|---|---|
| 🔒 路径沙箱（越界仍拒绝） | ✅ 照旧 |
| 🔒 读过凭证（没读过仍不能改） | ✅ 照旧 |
| 🔒 根一级文件自动备份到 `_backup/` | ✅ 照旧 |
| 🔒 每个操作的**逐条日志审计** | ✅ 照旧（`TOOL ws_write(...) -> ...` 全记） |
| 🚫 `/ws cd` 移出作业区的批准 | **不受影响**（那是独立机制）|

> 换句话说：按 `a` 相当于说「**这一轮的读写我放心，别一句句问**」，
> 而不是「**把保护全关掉**」。

---

### 5.5 等待动画：一眼看出「在跑」还是「卡死」 / Wait Spinner

调用模型、等审查员复核时，终端会安静几十秒 —— 从前分不清是**还在思考**还是**已经卡死**。
现在这段等待会**原地转圈并计时**：

```text
  -  等待模型响应  0.3s
  \  等待模型响应  0.4s
  |  等待模型响应  0.5s
  /  等待模型响应  0.6s
  ✅ 模型已响应（12.4s）      ← 停表后这一行被覆盖成结果，不留残影
```

**接了哪 4 个等待点**：

| 出现时机 | 动画文案 | 什么时候能看到 |
|---|---|---|
| 每轮调用模型时 | `等待模型响应` | 每次发言都会出现 |
| `/net ai` 模式下判定是否联网 | `判断是否需要联网` | 联网模式为 `ai` 时 |
| 复核最终答复 | `复核最终答复` | `/verify answer on` 时 |
| 执行有副作用动作前的审查 | `双人核验中` | 双人核验开启且涉及写文件 / 跑命令时 |

**四个设计要点**：

- **写 stderr，不写 stdout** —— stdout 被「命令可见化」的 tee 流捕获并注入给模型，
  动画若写进 stdout 会在上下文里塞满刷新帧。
- **非 TTY 自动降级** —— 输出被重定向到文件 / 管道时（日志、自动化脚本），
  动画自动关闭，只留一行 `✅ 模型已响应（12.4s）`，日志依然干净。
- **中断安全** —— 后台守护线程 + 停表清行；按 `Ctrl+C`、请求报错、审查员超时，
  都不会在屏幕上留下半行残影。
- **可关闭** —— `/set show_wait_anim off`，或在 `.env` 里写 `SHOW_WAIT_ANIM=0`。
  帧字符全为 ASCII 等宽（短横 / 反斜杠 / 竖线 / 斜杠），任何终端与编码下都不会乱码或抖动。

---

### 5.6 开工自检面板：开局即知情 / Boot Report

每次启动，肥鱼先采集一份**运行环境快照**（纯本机、只读、不联网），然后分两处用：

| 去处 | 内容 |
|---|---|
| 🖥 **终端面板** | 时间 / 进程 PID 与运行时长 / 程序文件与体积 / 工作台根 / 同名副本 / 健康提示（`_fatfish_pid.txt` 五态判定：ok / stale / suspect / unknown / invalid）|
| 🧠 **注入提示词** | 同一份信息的精简版，让执行者 AI 从第一句话起就知道「程序在哪、现在几点、工作台在哪、有无异常」|

开关（`.env`）：`BOOT_REPORT=1\|0` 控制面板，`BOOT_REPORT_PEERS=1\|0` 控制副本扫描；
**下次启动生效**。任何一步失败都**原样退回**，不会影响程序启动。

> 📌 完整运行状态随时可用 `/status` 调出，不必依赖启动那一刻的面板。

---

## 六、工作台（沙盒）机制 / The Workspace (Sandbox)

**工作台 = 肥鱼能自由读写的那个文件夹**，默认是程序目录下的 `workspace/`。
**Workspace = the folder FatFish can freely read/write**, defaults to `./workspace`.

### 6.1 三条安全设计 / Three Safety Designs

| 设计 / Design | 行为 / Behavior |
|---|---|
| 🔒 **路径越界拦截** | 所有路径都会被解析进工作台内，越界直接报错 `路径越界` / all paths are resolved inside the workspace |
| 🔒 **移出作业区要批准** | `/ws cd` 到默认作业区外面时**不执行**，先登记「待批准」；要调用 `ws_cd_approve` 你明确同意后才放行 / leaving the default area requires approval |
| 🔒 **写 / 执行批量报批** | `ws_write` / `ws_append` / `ws_replace` / `ws_delete` / `ws_run_cmd` / `ws_run_python` 汇总成一批问你一次，按 `y` 才动；拒绝后不重试。**只读类**（`ws_read` / `ws_list` / `ws_search` / `ws_where` / `ws_forget`）默认免报批，但**敏感文件**（`.env` / 密钥 / 凭据）即使只读也强制报批。想省事按 `a`/`1` 一键放行本轮（见 5.4）/ writes & execs ask once per batch; reads are free unless sensitive. Risk tiers: Ch.16 |

### 6.2 自动备份规则 / Auto-Backup Rule

「**根一级文件**」（直接躺在工作台根下、不在任何子目录里的文件）在被
**覆盖 / 追加 / 替换 / 删除**之前，会自动把原文件完整拷一份到 `_backup/`（备份区名固定，就叫这个）：

命名规则：`原文件名.年月日_时分秒_微秒.bak`
备份失败时整个操作会**中止**（宁可不改，也不丢原文件）。

> 子目录里的文件不受此规则约束 —— 所以根一级文件的「一改就留档」是刻意的重点保护。

> ⚠️ 这里**不写死数字**，因为**每改一次根一级文件就多一份全量副本**，数量秒级变化。
> 想看当前实况：在肥鱼里 `/ws ls _backup` 就行。

### 6.3 环境变量 / Environment Variable

| 变量 / Var | 作用 / Effect | 默认值 |
|---|---|---|
| `WORKSPACE_DIR` | 覆盖默认工作台的路径 / override default workspace | `<程序目录>/workspace` |

---

## 七、肥鱼长啥样？界面小知识 / UI Trivia

### 7.1 情绪调色板 / Mood Palette

肥鱼的回复会**自动嗅探情绪**并换配色（横线也会跟着渐变）：

| 情绪 | Emoji | 配色 / Colors | 触发关键词（示例）/ Trigger words |
|---|---|---|---|
| `happy` | 😊 | 金黄 → 橙 / gold→orange | 太好了、成功、恭喜、完成、👍、🎉 |
| `excited` | ✨ | 粉 → 深粉 / pink→deep pink | 哇、太棒、惊人、！！ |
| `love` | ❤️ | 浅粉 → 玫红 / pink→rose | 喜欢、爱你、❤ |
| `calm` | 🤖 | 天蓝 → 钢蓝 / sky→steel | （兜底默认）/ fallback default |
| `sad` | 🥺 | 矢车菊 → 暗板岩 / cornflower→darkslate | 抱歉、遗憾、可惜、难过 |
| `error` | ⚠️ | 橙红 → 深红 / orange-red→dark red | 错误、异常、失败、报错、Traceback |
| `code` | 💻 | 春绿 → 天蓝 / spring green→sky | ` ``` `、`def `、`class `、`import ` |
| `think` | 🤔 | 灰 → 深灰 / gray→dark gray | 让我想想、分析一下、首先、推理 |

判定优先级：`error > code > think > sad > happy > love > excited`（冲突时前面的赢）。

### 7.2 让肥鱼给文字上色 / Coloring Keywords

它自己也懂这套语法（系统提示词里教过它）/ FatFish is taught this syntax too:

```
{{red}}危险{{/red}}
{{green}}成功{{/green}}
{{bold}}重点{{/bold}}
{{rainbow}}炫彩{{/rainbow}}
```

可用标签 / Available tags：
- **颜色**：`red` `green` `yellow` `blue` `cyan` `magenta` `white` `gray`（`grey` 亦可）
- **样式**：`bold` `italic` `underline` `dim` `strike`
- **特效**：`rainbow`（22 色循环）

另外渲染时还认得 Markdown 的 `**加粗**`（白粗体）和 `` `行内代码` ``（紫底）。

### 7.3 输出归档 / Where Output Goes

本节只讲**主程序**自己的归档：它在程序目录内维护归档区，按「**年 → 月 → 日**」三层
子目录分层存放，**结构不依赖任何盘符或绝对路径** —— 换台机器、换个安装位置，结构都一样。

| 内容 / Content | 归档区 / Area | 文件名模式 / Name Pattern |
|---|---|---|
| 聊天日志 / chat log | `logs`，按 年 → 月 → 日 分层 | `chat_HHMMSS.log` |
| 命令 / 代码执行输出 / exec output | `logs`，同上分层 | `exec_HHMMSS_pid_run.out` |
| 自动存的代码 / saved code | `generated_code`，同上分层 | `<AI起的名字>.<ext>` |
| 跑 Python 的临时脚本 / temp script | `.fatfish_tmp`（跑完自动删）| `<你指定的名字>.py` |

> 📌 **范围说明**：上表只列**主程序自己产生 / 触发的归档**。
> 其它组件各自的日志（例如监控器自己的日志、审查员日志）不属于本节范围，故不在此列；
> 本节也不写死任何盘符与绝对路径。


---

## 八、安装器是怎么「自解压」的？ / How the Installers Self-Extract

这是最有趣的一块。安装器 = **脚本 + 内嵌压缩包**，同一个文件。

现在目录里只有一个安装器 `FATPACKII.bat`（II 版）；早期的 `FATPACK.bat` / `FATPACKI.bat` 已归档进 `oldpackmd/`。

| 安装器 / Installer | 内嵌内容 / Payload | 状态 / Status |
|---|---|---|
| `FATPACKII.bat` | **全套最新程序文件（17 项）** | ✅ 可用 / available（推荐）/ recommended |

> 早期版本曾内嵌一份过期清单（12 项，还漏了几个必需模块），现已重做并逐个校验通过（见 8.3）。
>
> 想重新打包？文件更新后跑一句 `python make_fatpack.py` 就行（见 8.4）。

### 8.1 结构 / Structure

一个安装器文件由**三段**拼成，看骨架就是：

```
<安装器>.bat
├── 开头一大段     可读的批处理安装逻辑（若干步骤，纯 ASCII 英文）
├── 中间一行       分隔标记（全文件只出现 1 次）
└── 标记之后       一段 Python 解包脚本（文件字典 + 循环写出）
        ↑ 其中若干"巨型行"，每行塞一个文件的 base64
          这几行占了整个文件的绝大部分体积
```

顺序是固定的：可读的安装逻辑在前，标记居中，载荷在后 —— 解包时从标记位置往后截取。

### 8.2 解包原理 / The Trick

解包靠的是**四步配合**：

1. **拼出标记** —— 分隔标记在运行时由两小段拼出来：这样这行代码本身不含完整标记，
   不会污染后面的搜索。
2. **截取载荷** —— 用 PowerShell 找标记**最后一次**出现的位置（防止正文里出现同名标记时找错），
   取其后全部内容，写成一段临时 Python 脚本。
3. **解包** —— 运行这段临时脚本：base64 解码，把内嵌的程序文件逐个写到磁盘。
4. **清理** —— 删掉临时脚本。

> 🥚 这里有两个很小但很妙的细节（标记到底几个字符、旧写法为什么「恰好无害」），
> 都写在文末**彩蛋**里。

### 8.3 内嵌清单与实测校验 / Embedded Files — Verified

校验方式：把安装器里的载荷抽出来解码，再与磁盘上的现场文件**逐字节比对**
（大小与内容都要对上）。

> 早期版本的载荷里确实混进过一批过期副本，而且漏掉了启动链必需的那几个文件 ——
> 这正是后来重做安装器的直接原因（详见第十二章「问题 1 / 问题 2」）。
> 🥚 那次「抓出过期文件」的过程有个小彩蛋，见文末。

### 8.4 安装器（`FATPACKII.bat`）改了哪些 / What the Installer Fixes

| 改动 / Change | 说明 / Notes |
|---|---|
| ✅ **内嵌全套最新文件** | 覆盖运行全部必需件：12 个 py + 3 个 bat + `README.md` + `.gitignore`（共 17 项）|
| ✅ **补上缺失的 4 个文件** | `fatfish_runtime.bat` / `fatfish_lang.bat` / `launch.py` / `fatfish_watcher.py` |
| ✅ **覆盖前自动备份** | 已存在的文件先拷进 `_backup/<名字>.<时间戳>.bak`，不静默吃掉旧版本 |
| ✅ **自检增强了** | 解包后先确认 `FATHFISH.py` 真的落地了，再往下走 |
| ✅ **可选立即启动** | 装完问一句「现在就启动吗」，输 `y` 直接开新窗口 |
| ✅ **静默开关** | `FATFISH_SKIP_VENV=1` / `FATFISH_NO_GUI=1`，方便自动化与沙箱测试 |
| ✅ **生成器自检** | bat 外壳若混进非 ASCII 字符会**直接报错停手**；标记出现次数必须为 1 |
| ⚠️ **外壳改为纯 ASCII 英文** | 见下面的「三个坑」—— 这是为了让 cmd 不在中文批处理上翻车 |

#### 🕳️ 开发时踩的三个坑 / Three Pitfalls (Worth Knowing)

关于安装器外壳，这里踩过三个很典型的坑：**一个转义字符被吃掉**、
**cmd 在多字节字符上读串行**、**分隔标记长度算错一个字符**。
三个坑的现场、报错与修法，都收在文末**彩蛋**里 ——
读完那三条，你就明白安装器为什么非要写成现在这样。

#### 🔁 重新打包 / Re-packaging

以后任何程序文件更新了（比如你又改了 `FATHFISH.py`），只要重跑一次生成器：

```bash
python make_fatpack.py                  # 生成 FATPACKII.bat（默认产物）
python make_fatpack.py FATPACKII.bat    # 显式指定产物名，效果同上
python make_fatpack.py --manifest       # 只看会内嵌哪些文件，不生成
```

生成器有三重保障：① bat 外壳必须纯 ASCII，否则直接报错停手；
② `##PYBEGIN##` 在文件里只能出现 1 次；③ 打印每个内嵌文件的 SHA1 供比对。

#### 🧪 I 版的实测结果 / Verified

| 测试 / Test | 结果 / Result |
|---|---|
| 载荷抽取（真调 PowerShell 按 bat 里的命令） | 退出码 0，抽出的 Python 可编译 ✅ |
| 沙箱第一遍：全新安装 | 12 个文件全部 `新增`，退出码 0 ✅ |
| 沙箱第二遍：覆盖更新 | 12 个文件全部 `更新` 并**各自留下备份**，`.env` 原样保留，退出码 0 ✅ |
| 还原质量 | **12/12 逐字节一致** ✅ |
| 沙箱产物可运行性 | 沙箱里的 `FATHFISH.py` 编译通过 ✅ |

---

## 九、所有「数值上限」一览 / All Tuning Constants

想调参数就改这些（都在源码顶部，注释里还标了历史变更）：

### 9.1 主程序 `FATHFISH.py`

| 常量 | 值 | 含义 / Meaning | 注释里的历史 / History |
|---|---|---|---|
| `MODEL` | `deepseek-flash` | 调用的模型名（`.env` 的 `FATFISH_MODEL` 可覆盖，见第十八章）/ model name | — |
| `BASE_URL` | `https://api.deepseek.com` | API 地址 | — |
| `MAX_HISTORY` | **500** | 保留的历史消息条数（`.env` 的 `MAX_HISTORY` 可覆盖）| 20 → 200 → 1000 → 400 → 500 |
| `MAX_HISTORY_TOKENS` | **800,000** | 历史部分的 token 预算 | 60k → 512k → 2M → 800k |
| `TRIM_KEEP_FIRST_USER` | `True` | 钉住最早一条 user（任务目标） | — |
| `TRIM_TOOL_CLIP_CHARS` | **40,000** | 单条 tool 结果超长则中间截断 | 4k → 60k → 200k → 40k |
| `MAX_REPLY_TOKENS` | **131,072** | 单次回复上限 | 16k → 32k → 64k → 131k |
| `MAX_TOOL_ROUNDS` | **512** | 单轮对话内工具循环上限 | 16 → 128 → 512 |
| `API_TIMEOUT` | **900 s** | 单次 API 请求超时 | 60 → 300 → 900 |
| `SHOW_TIMER` | `True` | 是否显示轮次计时（`/timer on/off`） | 新增于 rev.2.1 |
| `SHOW_WAIT_ANIM` | **`True`** | 等待时原地转圈 + 计时（四帧：短横 / 反斜杠 / 竖线 / 斜杠）| 新增于 2026-09-19 |
| `WAIT_ANIM_INTERVAL` | `0.08` | 动画每帧间隔（秒）| 新增于 2026-09-19 |
| `AUTO_APPROVE_ENABLED` | **`True`** | 「一键放行」总开关（报批时按 `a`/`1`）| 新增于 rev.2.3 |
| `AUTO_APPROVE_DEFAULT` | `False` | 改成 `True` = 每轮默认已放行（完全不弹窗）| 新增于 rev.2.3 |
| `AUTO_APPROVE_SCOPE` | **`all`** | 一键放行覆盖范围：`none` 全拦 / `writes` 仅写入 / **`all` 除敏感文件外全放行（含删除 / 执行）** | 2026-09-19 新增；同日默认由 `writes` 改为 `all` |
| `temperature` | 0.7 | 聊天采样温度 | — |
| AI 命名代码的 `temperature` | 0.2 / `max_tokens` 32 / `timeout` 30 | 给代码起名字时更"保守" | — |

### 9.2 工作台 `workspace.py`

| 常量 | 值 | 含义 |
|---|---|---|
| `MAX_READ_BYTES` | **5 MB** | 单文件读取字节上限（原 200 KB）|
| `MAX_READ_CHARS` | **1,000,000** | 单文件读取字符上限（原 5 万）|
| `ws_list(depth=)` | **3** | 列目录默认递归深度 |
| `ws_list(max_entries=)` | **2000** | 列目录最多显示条目 |
| `ws_search(max_hits=)` | **500** | 全文搜索最多命中数 |
| `BACKUP_DIRNAME` | `_backup` | 备份区目录名 |

### 9.3 文件与图片 `file_tools.py`

| 常量 | 值 | 含义 |
|---|---|---|
| `MAX_BYTES` | 200 KB | 用 `@路径` 读文件时的字节上限 |
| `MAX_CHARS` | 50,000 | 同上，字符上限（超出截断）|
| `MAX_DIR_FILES` | **20** | 读目录时最多读几个文件 |
| `MAX_IMAGE_BYTES` | **32 MiB** | 单张图片上限（API 硬限制）|
| `MAX_IMAGES_PER_MSG` | **5** | 单条消息最多几张图 |
| `MAX_TOTAL_IMAGE_BYTES` | **60 MiB** | 单条消息图片总量（base64 前）|

> 支持编码：`utf-8-sig` → `utf-8` → `gbk` → `big5` 逐个试；
> 读文件前先看前 4096 字节有没有 `\x00`，有就判定为二进制并跳过。

### 9.4 联网 `net_tools.py`

| 常量 | 值 | 含义 |
|---|---|---|
| `NET_MODE` | `auto` | 默认联网模式 |
| `TAVILY_MODE` | **`auto`** | 默认检索模式（有 URL 抓正文，否则搜索；`search`/`extract` 为强制锁定）|
| `max_results` | 5 | 搜索返回条数 / 抓取 URL 上限 |
| `EXTRACT_MAX_LEN` | 8,000 | 抓正文单段最大字符，超出分段 |
| 搜索超时 / 抓取超时 | 20 s / 30 s | — |
| 单条搜索结果正文截断 | 600 字符 | — |

**`auto` 模式的判断规则**（`need_search`）：

- 命中 **动作词** 必搜：搜索、查一下、帮我查、联网、新闻、股价、汇率、天气、多少钱、价格、发布、上线
- 命中 **时间词** 且句子里有 `?`/`？`/`吗` 才搜：最新、今天、现在、实时、当前、近期、最近、今年、本月、这周

### 9.5 执行引擎 `exec_tools.py`

| 常量 | 值 | 含义 |
|---|---|---|
| `DEFAULT_TIMEOUT` | **120 s** | 默认超时（原 30）|
| `MAX_TIMEOUT` | **1800 s** | 硬上限（原 300）|
| `MAX_OUTPUT_CHARS` | **200,000** | 返回给 AI 的输出字符上限（原 20,000）|
| `EXEC_OUTPUT_ROOT` | `logs` | 执行输出落盘的归档区**名**（相对程序目录，不含盘符）|

### 9.6 监控器 `fatfish_watcher.py`

| 常量 | 值 | 含义 |
|---|---|---|
| `DEFAULT_INTERVAL` | 1.0 s | 轮询间隔（下限 0.3 s）|
| `PS_TIMEOUT` | 30 s | PowerShell 存活检测超时 |
| `DRAIN_ROUNDS` / `DRAIN_INTERVAL` | 3 轮 / 1 s | 主程序关闭后再 tail 几轮 |
| `MISSING_TOLERANCE` | **3** | 连续 3 次查不到才算"彻底关闭"（防误判）|
| 收尾倒计时 | **30 s** | 按键可提前退出 |

> 📌 双人核验 / 设置中心 / 联网判断的**完整参数表**见**第十五~十八章**
> （对应 `verify_tools.py` / `settings.py` / `net_tools.py` / `.env`）。

---

## 十、跑命令 / 跑 Python 详解 / Running Commands & Code

### 10.1 它是怎么跑起来的 / How Execution Works

```
ws_run_cmd("dir")  /  ws_run_python(code)
        │
        ├─ 1) cwd 越界检查：只允许工作台内的子目录，越界直接拒绝
        ├─ 2) 分配落盘文件（归档区按 年 → 月 → 日 分层）exec_HHMMSS_<pid>_run.out
        ├─ 3) subprocess.Popen(cmd / python script)
        │       Windows 加 CREATE_NO_WINDOW 隐藏黑框
        │       环境变量里塞 PYTHONIOENCODING=utf-8 / PYTHONUTF8=1
        ├─ 4) 两条线程分别抽干 stdout / stderr
        │       边读边写落盘文件（顺手喂给监控器窗口）
        ├─ 5) 超时就 kill
        └─ 6) 解码（utf-8 → gbk → cp936 → big5 → latin-1 兜底）
                超过 20 万字符则头尾保留、中间截断
```

**跑 Python 的小心思**：代码会先写进工作台里的 `.fatfish_tmp/<名字>.py` 再执行，
所以出错时 traceback 里能看到**真实文件名**（而不是 `<string>` 或者一堆 `exec` 栈），
跑完自动删除。

### 10.2 输出落盘 = 监控器窗口的"信号源" / Output Files Feed the Watcher

`exec_*.out` 这个落盘文件是**两个功能共用的**：

1. 完整输出回传给 AI（AI 看得到）
2. 监控器窗口在**另一个黑窗里实时 tail 它**（你看得到）

所以你在主窗口看到「✅ 工作台 ws_run_python → …」时，
旁边那个监控窗口会**同步滚动出这个程序到底打印了什么**。

---

## 十一、多语言 / Multi-Language

肥鱼在 bat 层做了一次**四级降级探测**，只为决定界面说中文还是英文：

```
第一级：PowerShell  [CultureInfo]::CurrentCulture.Name          ← Win7+ 都有，最稳
第二级：PowerShell  [CultureInfo]::CurrentUICulture.Name        ← UI 语言
第三级：wmic os get locale                                       ← 老系统稳，新系统可能已弃用
第四级：系统区域设置注册表项（LocaleName）    ← 最后兜底
        │
        └─ 结果里含 "zh" → FISH_LANG=zh；其他/失败 → FISH_LANG=en
```

两个刻意为之的设计 / Two deliberate choices:

1. **`fatfish_lang.bat` 的行尾注释全是纯 ASCII** —— 因为子脚本不能假设调用方已经设好了
   `chcp 65001`，否则它自己的中文注释在 GBK 控制台下会被**当成命令解析**。
2. **非中文一律回退纯英文** —— 不搞半吊子翻译（原文注释：`No half-baked translations`）。

而 `fatfish1.1.1.bat` 的提示信息则统一用 `中文 | English | 日本語 | 한국어` 四语并列。

---

## 十二、⚠️ 已知问题与待办 / Known Issues & TODOs

以下都是我这次**实测发现的**，不是猜测：

### 🟢 问题 1（已解决）：安装器曾内嵌过期文件

**原状**：安装器载荷里有**若干个文件是旧版本**（大小对不上现场文件），
装出来是一套「半旧不新」的肥鱼。

✅ **现已修复**：重跑生成器做了一版新安装器，随后又把原版名也重装了一遍（旧版留档）。
现在两个安装器都逐字节校验通过（见 8.3）。

### 🟢 问题 2（已解决）：安装器曾漏掉启动链关键文件

**原状**：旧版内嵌清单只有 8 个文件，**缺了 4 个必需文件**：

| 缺失文件 | 后果 |
|---|---|
| `fatfish_runtime.bat` | `fatfish1.1.1.bat` 里 `start cmd /k "fatfish_runtime.bat"` **找不到文件** |
| `fatfish_lang.bat` | runtime 里 `call fatfish_lang.bat` 失败 |
| `launch.py` | runtime 里 `python launch.py` 失败 |
| `fatfish_watcher.py` | 没有监控器窗口 |

**即：用旧版全新安装后，双击 `fatfish1.1.1.bat` 会直接闪退或报错。**

✅ **现已修复**：两个安装器都内嵌 12 个文件，并在沙箱里各跑通了「全新安装 → 覆盖更新」两遍完整流程（见 8.4）。

### 🟡 问题 3：主程序文件名拼写不一致

主程序叫 `FATHFISH.py`（**多了一个 H**），而其他所有文件都是 `fatfish*`。
`__pycache__/` 里也忠实同步成了 `FATHFISH.cpython-38.pyc`。

**建议**：要么改名统一，要么保留现名但在 README/文档里明确标注（本版文档已标注）。
直接改名的风险：启动枢纽和安装器载荷里都**按字面写死了**这个文件名，改名要连带一起改。

### 🟡 问题 4：模型名可能不是可用的 API 名

默认主模型是 `deepseek-flash` —— 但它**不再写死在源码里**，而是由 `.env` 的
`FATFISH_MODEL` 决定（见第十八章）。DeepSeek 官方常见名有 `deepseek-chat` /
`deepseek-reasoner`，近期的 `/models` 也返回 `deepseek-flash` / `deepseek-v4-pro` /
`deepseek-v4-flash` / `deepseek-v3.2` / `deepseek-v3.1` / `deepseek-r1` 等。
**判断某个模型名是否存在，一律以 `/models` 接口或官方文档为准。**
如果聊天报「模型不存在」，改 `.env` 的 `FATFISH_MODEL`（或临时 `/model <名>`），再 `/reload`。

### 🟢 提醒 1：`.env` 是明文密钥

`FATFISH_API_KEY` / `TAVILY_API_KEY` 明文躺在磁盘上。
好在 `.gitignore` 第一行就是 `.env`，**不会**被 git 带走 —— 但这个防护仅对 git 有效。

### 🟢 提醒 2：备份会膨胀

`_backup/` 的规则是「根一级文件**每次被改前都全量留档**」，所以只要频繁改
`README.md` / `FATHFISH.py` 这类文件，备份就会快速堆积（迭代最猛的那一晚，备份数量冲到过上百份）。
**建议偶尔按「每文件保留最近几份」清一次** —— 清理方法很简单，就是在工作台里删掉过期的 `.bak`：
文件名里带时间戳（`原文件名.年月日_时分秒_微秒.bak`），按时间排序留新的即可。

### 🟢 提醒 3：当前没有 venv

`FATPACKII.bat` 会创建 `venv/`。当前目录下若 `venv/` 和 `.venv/` 都不存在，
（说明是全局 Python 环境在跑，或者 venv 被清理过）。
`fatfish1.1.1.bat` 对此是容错的：找不到 venv 就用全局 Python。

### 🟢 提醒 4：`_fatfish_pid.txt` 的取法（2026-09-19 已修）

它记的是 **runtime 窗口（cmd.exe）的 PID**，用途是让外部工具能识别 / 关闭肥鱼窗口。
该窗口退出时 `fatfish_runtime.bat` 会自动删除它；**非正常退出**（被强杀）时会留下来，
属于正常残留 —— 下次启动会先清旧值、再写新值。

⚠️ **旧取法有个坑（已修）**：原先用「窗口标题反查」—— 先把窗口标题改成随机串，
再用 PowerShell 按 `MainWindowTitle` 找自己。但在 **Windows Terminal（ConPTY）** 下，
`cmd.exe` 没有自己的顶层窗口，标题属于 `WindowsTerminal.exe`，于是反查抓到的
**可能是终端宿主的 PID**（历史运行日志里确实抓到过），外部工具据此操作甚至可能误伤终端窗口。

✅ **新取法：重定向法** —— `powershell ... > 临时文件`，让 PowerShell 报出
「自己的父进程 PID」。重定向由 cmd 自身处理文件句柄，PowerShell 由本窗口**直接**启动，
所以父进程就是 runtime cmd 本体（已用「真身 PID 对照」实测验证：探测值 == 真身 PID）。
降级链：`Get-CimInstance` → `Get-WmiObject` → 窗口标题反查（仅传统终端有效）→ 写 `0`。

> 另外，`boot_report.py` 的自检也同步改了判定语义：它**不再**拿这个 PID 去和
> Python 主程序的 PID 比（那本来就是两个不同的数字，比了必然误报），改为检查
> 「该 PID 是否还活着 + 映像名是否像肥鱼链路进程」，结果分五态：
> `ok`（有效）/ `stale`（残留，提示可删）/ `suspect`（疑似误抓）/ `unknown`（查不到身份，不报警）/ `invalid`（内容不是数字）。

---

## 十三、出问题了怎么办？（小白急救包）/ Troubleshooting

| 现象 / Symptom | 原因 & 解决 / Cause & Fix |
|---|---|
| **双击 bat 一闪就没了** | 没装 Python 或没加 PATH。重装 Python 并勾 `Add Python to PATH` / Python missing or not in PATH |
| **提示 `python 不是内部或外部命令`** | Python 没加进 PATH，重装时勾选那个选项 / reinstall and check PATH |
| **提示「提取内嵌数据失败，安装器可能已损坏」** | 安装器的 `##PYBEGIN##` 标记或载荷被破坏（比如用编辑器保存过、被截断）。重新拿一份完整的安装器，或本地重跑 `python make_fatpack.py` / installer payload corrupted |
| **`start cmd /k fatfish_runtime.bat` 报找不到文件** | 命中「问题 2」：安装器没内嵌 runtime。手动把 4 个缺失文件补上，或从完整目录复制过来 / see Known Issue 2 |
| **能启动但一聊天就报错** | `.env` 里 `FATFISH_API_KEY` 没填对（旧名 `DEEPSEEK_API_KEY` 同样可用），检查有没有多余空格 / key wrong or has spaces |
| **报「模型不存在」** | 改 `.env` 里的 `FATFISH_MODEL`（见问题 4 / 第十八章），改完 `/reload` / set `FATFISH_MODEL`, then `/reload` |
| **说不能联网** | 没填 `TAVILY_API_KEY`，或 key 过期（401）/ missing or expired Tower key |
| **提示「未读过该文件，请先 ws_read」** | 这是安全设计，不是 bug。让它先 `ws_read`（它会弹批准框，你按 `y`）|
| **提示「文件已被外部改动，凭证失效」** | 你（或别的程序）改了这个文件，凭证自动作废，重新读一次即可 |
| **提示「路径越界」** | 你让它读写工作台以外的路径。用 `/ws cd` 切到那个目录（移出默认作业区需批准）|
| **读写操作弹 `y/N` 提示** | 正常流程。输入 `y` 放行，`n` 拒绝（拒绝后它不会重试）/ press `y` or `n` |
| **监控器窗口一直没动静** | 只有当你让它跑命令/跑 Python 时才会有输出滚动（它是「子程序输出镜子」，不是日志窗）/ it only shows sub-program output |
| **关掉主窗口，监控器还在倒计时** | 设计如此：最多再 drain 3 秒 + 30 秒倒计时，按任意键可立刻退 / by design |
| **提示「工具调用轮次达到上限，已强制停止」** | 单轮超过 512 次工具调用，属于失控保护 / safety cap |
| **提示「回复被 max_tokens 截断」** | 回复太长被截。让它拆成几段继续，或调大 `MAX_REPLY_TOKENS` |
| **依赖安装失败** | 安装器会自动改清华镜像重试；还不行手跑：`python -m pip install openai python-dotenv requests` |
| **窗口中文乱码** | bat 里已设 `chcp 65001`；若仍乱码，换个新一点的 Windows 终端 |
| **改了 `.env` 不想重启** | 输入 `/reload` |

---

## 十四、API Key 怎么申请？（重点章节）/ How to Get API Keys

肥鱼靠两个 Key 才能干活：**DeepSeek Key（必填）** 和 **Tavily Key（选填）**。

### 🅰️ DeepSeek API Key（必填，负责"聊天大脑"）

**它是干嘛的**：肥鱼回答你、写代码、思考，全靠它。**没有它，肥鱼一句话都说不出来。**

1. **打开官网**：<https://platform.deepseek.com/>
2. **注册账号**：手机号或邮箱注册 → 收验证码 → 填进去
3. **实名认证**（部分功能需要）：登录后进「个人中心」，按提示完成
4. **充值**（重要 ⚠️）：API 是**按用量付费**的，不是免费的。进「充值」充一点
   （比如 10 元能用很久）。💡 新账号有时送免费额度，够试玩
5. **创建 API Key**：左侧菜单 **「API Keys」** → 点「创建 API Key」→ 起个名字（比如 `fatfish`）→
   确定后会弹出一长串以 `sk-` 开头的字符
6. **⚠️ 立刻复制保存！** **它只显示这一次！关掉页面就再也看不到了**

填进 `.env`（注意 **`=` 两边不要有空格**）：

```
FATFISH_API_KEY=<把复制到的密钥粘在这里>
```

### 🅱️ Tavily API Key（选填，负责"联网搜索"）

**不填也能用肥鱼**，只是不能联网，遇到"最新"类问题会答不上来。

1. **打开官网**：<https://tavily.com/>
2. **注册账号**：点右上角「Sign Up」，可用 Microsoft 账号一键登录，也可邮箱注册
3. **领取免费额度**：注册后送每月免费搜索次数（个人用基本够）
4. **获取 API Key**：登录后进「Dashboard」→ 找「API Keys」→ 复制 `tvly-` 开头那串
5. **同样立刻保存**

```
TAVILY_API_KEY=<把复制到的密钥粘在这里>
```

> 💡 肥鱼用的是 **Tavily** 做联网搜索（代码里写死的）。
> 腾讯云、火山引擎、阿里云等也提供搜索 API，但**肥鱼不支持那些**，认准 Tavily 官网。

### 🚨 Key 泄露了怎么办？

API Key 就像你家的**银行卡密码**。一旦被坏人拿到，他就能用**你的钱**刷 API。

**什么情况算"泄露"？**

| 场景 | 危险吗 |
|---|---|
| 把 `.env` 截图发到群里 / 发到网上 | 🔴 危险，等于把密码发出去 |
| 把 `.env` 上传到 GitHub / Gitee | 🔴 极度危险，机器人几秒就能扫到 |
| Key 不小心写进了要分享的代码 | 🔴 危险 |
| 电脑被人用过、或中了病毒 | 🟡 建议直接换 |
| 只是自己电脑上放着，没给别人看过 | 🟢 安全 |

**泄露了怎么补救？（三步）**

1. **立刻去平台删掉旧 Key**（最关键，越快越好）
   - DeepSeek：<https://platform.deepseek.com/> → API Keys → 找到那个 → 删除
   - Tavily：<https://tavily.com/> → Dashboard → API Keys → 删除
2. **创建一个新 Key**，立刻复制保存
3. **把新 Key 填回 `.env`**，如果肥鱼在跑就输入 `/reload`

**好习惯**：`.env` 只放自己电脑上 / 分享截图前打码 / 用 git 就确保 `.gitignore` 有 `.env` /
怀疑就换 / 别充太多钱。

> 🧠 一句话记住：**Key 一旦离开你的电脑，就当它已经泄露，立刻删掉重建。**

---

## 十五、双人核验：动手前的第二双眼睛 / Dual-AI Verify

> 一句话：**肥鱼（执行者）想做有副作用的动作之前，先把方案交给第二位 AI（审查员）复核；
> 审查员不放行，肥鱼就必须「补充说明」或「变更方案」后重新送审。**
> 核验**不替代**人工报批，而是「多一道」——两道闸门都要过。
> One line: **Before any side-effecting action, FatFish submits its plan to a second AI (the verifier).**

### 15.1 第二位 AI 的三个用途 / Three Uses of the Second AI

| 用途 | 何时触发 | 开关 | 行为 |
|---|---|---|---|
| 🧿 **动作核验** | 工具调用前（写 / 删 / 跑命令…） | `/verify on` / `off` / `all` | 放行 / 要求补充 / 要求改方案 |
| 📝 **答复核验** | 最终答复发出前 | `/verify answer on` / `off` | 答复被挑刺则打回重写 |
| 🌐 **联网需求核验** | 联网之前，判断「到底要不要搜」 | `/net ai` | 决定 不搜 / 搜索 / 抓正文 |

三者共用同一个审查员模型与配置（`VERIFIER_MODEL` 等），也共用镜像与日志。

### 15.2 它解决什么问题 / What It Prevents

单个 AI 自己拍板、自己执行，容易翻这几种车 / A single AI judging and acting alone tends to:

| 风险 | 例子 |
|---|---|
| 答非所问 / 擅自扩大范围 | 你只要「看看 README」，它却打算重写全部源码 |
| 破坏性、不可逆操作 | `del /f /q *.py`、全量覆盖核心文件、删完不留备份 |
| 信息不足就动手 | 没说清目的、影响范围、回滚方式就开干 |
| 方案不是最优 | 有更小、更安全的做法，却挑了最猛的那个 |

### 15.3 架构与卡点 / Where the Gate Sits

```
用户发言
   │
   ▼
主模型 = 执行者
   │  提出一批工具调用
   ▼
┌──────────────────────────────────────────────┐
│  🧿 双人核验（位于人工报批之前）                │
│  approve    → 放行，进入下一关                  │
│  supplement → 打回：要求补充说明（不计退回次数）│
│  revise     → 打回：要求变更方案（计入退回次数）│
└──────────────────────────────────────────────┘
   │  打回：审查意见回填给执行者 → 修正后重新提交
   │  超过上限：严格模式整批拦截 / 宽松模式放行并告警
   ▼
🔐 人工报批（文件类 + 执行类，原有防线不变）
   ▼
真正执行
   ▼
结果回填 → 模型继续
```

**关键设计**：核验**不替代**人工报批，而是「多一道」。核验通过 ≠ 直接执行，仍需你点头
（除非你开了 `/auto` 一键放行，见 5.4）。

### 15.4 三种裁决与计数规则 / Three Verdicts

| 裁决 | 含义 | 后续 | 计数影响 |
|---|---|---|---|
| `approve` | 合理且安全 | 放行，进入人工报批 | — |
| `supplement` | 主体可行，但信息不足 / 风险未交代 | 打回，要求补充：目的、影响范围、依据、回滚方式 | **不计入退回次数**，只消耗免费补充轮 `VERIFY_MAX_SUPPLEMENTS`（默认 4）；用尽后才归入退回计数 |
| `revise` | 有错误、有风险，或存在更优方案 | 打回，要求变更方案后重新送审 | 计入 `VERIFY_MAX_RETRIES`（默认 2） |

审查员**不输出 JSON**，用的是极简「文本协议」（从根上避开括号 / 引号 / 裸换行等 JSON 语法陷阱）：

```
通过                       补充                        变更
理由：一句话理由            理由：一句话理由             理由：一句话理由
                          要求：需要补充什么           要求：需要改什么
                                                     风险：识别到的风险
```

首行是裁决词（`通过` / `补充` / `变更`，也兼容 `approve` / `supplement` / `revise`），
其后每行以 `理由：` `要求：` `备选：` `风险：` 开头（可省略、可重复）。
解析顺序为「严格/宽容 JSON → JSON 关键词降级 → 简短文本协议 → 全都解析不出则保守判为 `supplement`」，
不再动辄「无法解析」。

### 15.5 谁能当审查员 / Who Reviews

默认策略：**同一把 key + 不同模型 + 独立人格**（异源审查，零额外成本）。

```dotenv
VERIFIER_API_KEY=        # 留空 → 沿用主 key
VERIFIER_BASE_URL=       # 留空 → 沿用主 base_url
VERIFIER_MODEL=deepseek-chat
```

想要**真·异源**（换别家模型当裁判），把上面三项填成任意 OpenAI 兼容服务即可。

### 15.6 运行时命令 / Runtime Commands

```
/verify                      查看状态（模式 / 审查员 / 严格 / 本轮已打回次数）
/verify on                   开启（auto：仅核验有副作用动作）
/verify all                  开启（连只读操作也核验）
/verify off                  关闭
/verify strict on|off        超重试上限：拦截 / 放行
/verify model <模型名>        热切换审查员模型
/verify answer on|off        是否复核最终答复
/verify retries <n>          打回重交上限（仅 revise 计入退回次数）
/verify supplements <n>      免费补充资料轮数上限（supplement 不计退回次数）
/verify fail open|closed     核验服务不可用时的策略
/verify mirror on|off        审查意见同步到监控器窗口
/verify ping                 连通性自检（真调一次审查员，无害探针）
```

**auto 模式核验哪些工具**（有副作用的）：

```
ws_write / ws_append / ws_replace / ws_delete / ws_mkdir
ws_cd / ws_cd_approve
ws_run_cmd / ws_run_python
```

只读工具（`ws_read` / `ws_list` / `ws_search` / `ws_where` / `ws_forget`）仅在 `all` 模式下核验。

### 15.7 控制台长什么样 / What It Looks Like

```text
  🧿 双人核验中 [verifying] 2 个动作（审查员 [verifier]：deepseek-chat）…
  🛑 双人核验 → revise：用户只要求评审 README，却要删除全部 Python 文件，严重偏离目标且不可逆
       · 要求：立即取消 del /f /q *.py 与重写核心文件
       · 要求：改为只读 README 并给出评审意见
       · 备选：先只读探查，不修改任何文件
  🔁 审查员未放行，已打回执行者补充/改方案（第 1/2 次）
  …
  🛑 已达双人核验重试上限，动作被拦截
```

日志：`logs/YYYY/MM/DD/verify.log`（含每次裁决与完整 traceback）。

### 15.8 审查意见镜像到监控器 / Mirror to the Watcher

审查的**全过程**会实时写进监控器窗口（🐟 那个独立黑窗），于是你能滚动看到每一次核验：
送审了哪些动作、裁决是什么、理由、风险、要求、备选、以及打回 / 拦截 / 放行。

**怎么做到的（零改动 watcher）**：监控器只 tail 一个约定 ——
`logs/YYYY/MM/DD/` 下**文件名以 `exec_` 开头、以 `.out` 结尾**的文件。于是核验引擎写到同目录的：

```
logs/YYYY/MM/DD/exec_verify_<主程序PID>.out
```

命名天然落在扫描范围内，`fatfish_watcher.py` 一行都不用改。

| 时机 | 镜像里的图标 |
|---|---|
| 送审 | `🧿 双人核验·送审 N 个动作` |
| 裁决 | `🧿 双人核验·审查意见`（含裁决 / 理由 / 风险 / 要求 / 备选） |
| 打回 | `🔁 双人核验·动作被打回（第 n/N 次）` |
| 超限拦截 | `🛑 双人核验超限 → 动作被拦截` |
| 超限放行（宽松） | `⚠️ 双人核验超限 → 宽松模式放行` |

开关：`/verify mirror on|off`，或 `.env` 的 `VERIFY_MIRROR=1|0`。
镜像文件**追加写、每次 flush**，多实例按 PID 分文件、互不覆盖。

### 15.9 送审材料包含什么 / What the Verifier Sees

复核 AI 判断得准不准，取决于它能**看到多少信息**。这些上限控制业务 AI 往审查员那里「递」多少内容
（单位：字符，中文约 1 字 ≈ 1.5 token）：

| 变量 | 默认 | 控制什么 |
|---|---|---|
| `VERIFIER_PROMPT_CHARS` | 64000 | 送审内容总上限 |
| `VERIFIER_PLAN_CHARS` | 16000 | ★ 执行者自述 / 计划（业务 AI 的方案，重点项） |
| `VERIFIER_ACTION_PREVIEW` | 4000 | 单个动作的参数预览（命令 / 代码 / 文件内容） |
| `VERIFIER_REPLACE_PREVIEW` | 2000 | `ws_replace` 的 old / new 各自 |
| `VERIFIER_GOAL_CHARS` | 6000 | 用户原始需求 |
| `VERIFIER_CONTEXT_CHARS` | 24000 | 近期对话上下文 |
| `VERIFIER_ANSWER_CHARS` | 24000 | 待复核的最终答复 |
| `VERIFIER_ANSWER_CTX` | 12000 | 答复复核时的上下文 |
| `VERIFIER_MAX_TOKENS` | 2000 | 审查意见的**输出**上限（token） |
| `VERIFIER_TIMEOUT` | 120 | 单次审查请求超时（秒） |

> 📌 **优先级压缩**：总长超限时各段按优先级**比例收缩**（而不是整段丢弃），权重为优先级平方：
> `动作清单(9) > 用户真实操作记录(8) > 执行者计划(7) > 用户需求(6) > 近期上下文(2)`。
> 越重要的信息留得越多，上下文最先让步，但**任何段落都不会凭空消失**；收尾指令句永不裁剪。

> 📌 **让审查员看到「用户本人的即时命令」**：主程序会把**你本人**刚敲下的命令 / 报批答复
> （`y` / `a` / `n`）原声附在送审材料里。采集点**只在 `input()` 通道**——模型没有任何写入通道，
> 故该记录**不可被模型伪造**。于是审查员不会再误判「执行者擅自代替用户放行」而把工作台切换驳回；
> 若记录显示用户已亲手命令 / 批准，审查员只复核操作本身是否安全。滚动保留最近 12 条，`/clear` 一并清空。

### 15.10 排错 / Troubleshooting

| 现象 | 排查 |
|---|---|
| 审查员总是 approve | `VERIFY_MODE` 是否被设成了 `off`？`/verify` 看状态；或核验服务异常在 fail-open（日志里会有 `failed=True`） |
| 审查员总是 revise | `VERIFIER_MODEL` 是否为有效模型；或提示词与动作本身确实有问题（这正是它的价值） |
| `UnicodeEncodeError: 'ascii' codec` | 经典坑：`.env` 里**留空的项不要写行尾注释**。`python-dotenv` 会把 `KEY=  # 注释` 的注释当成值，塞进 HTTP 头 → `httpx` 只允许 ASCII 头 → 崩。程序内置 `_env_clean()` + `_looks_like_key()` 兜底，但最好别依赖它 |
| 想临时关掉 | `/verify off`（当轮生效），或 `.env` 里 `VERIFY_MODE=off` |

```bash
# 1) 纯逻辑自检（不联网，用假模型）
python verify_tools.py

# 2) 连通性自检（真调一次审查员）——在肥鱼里输入：
/verify ping

# 3) 强制"打回"演练（不联网、不碰文件，用测试桩）
set FATFISH_VERIFY_MOCK=revise && set VERIFY_MODE=all && python FATHFISH.py
```

### 15.11 联网需求核验（`/net ai`）/ AI-Judged Web Search

原先的联网判断是**纯关键词规则**（`net_tools.need_search`）：

```
命中动作词（搜索 / 天气 / 股价…）              → 搜
命中时间词（最新 / 现在 / 今天…）且含 ? ？ 吗  → 搜
```

这套规则两个方向都会出错：

| 问题 | 例子 | 后果 |
|---|---|---|
| **误报**（不该搜却搜） | 「现在双人核验是可开关的模式吗」→ 命中「现在」+「吗」| 搜回一堆无关内容，污染上下文 |
| **漏报**（该搜却没搜） | 「最新的 DeepSeek 模型是什么」→ 句尾无 `？`/`吗` | 该查实时信息时没查，回答可能过时 |

实测误报率约 **42%** —— 因为「现在」「当前」在中文里多数是**问自身状态**，不是查外部世界。

`/net ai` 模式下，联网前先请审核 AI 独立判断：

```
用户消息 ──▶ 🧿 联网需求核验（第二位 AI）
                ├─ 不搜 ──▶ 直接用模型自身知识回答
                └─ 要搜
                     ├─ extract ──▶ 📄 抓取指定 URL 的正文
                     └─ search  ──▶ 🔍 用改写后的关键词搜索
```

**实测效果**（12 条人工标注测试集）：

| 方案 | 准确率 | 正确搜 | 正确跳过 |
|---|---|---|---|
| 关键词规则 `auto` | 66.7% | 5/6 | 3/6 |
| **AI 核验 `ai`** | **100%** | 6/6 | 6/6 |

**顺带解决「查询改写」**：不再把口语整句丢给搜索引擎，而由审核 AI 先改写成检索词：

| 原始口语 | 改写后检索词 |
|---|---|
| 现在比特币价格多少 | `比特币 最新价格` |
| 今天有什么 AI 新闻 | `今日 AI 新闻` |
| 最新的 DeepSeek 模型是什么 | `DeepSeek 最新模型 版本` |

**检索模式由 AI 决定**（有链接 ≠ 一定 extract）：

| 模式 | 何时选用 |
|---|---|
| 📄 `extract` | 需要读**特定网页正文**（消息里带 URL，意图是"看这篇文章/总结这个链接"） |
| 🔍 `search` | 需要**按主题检索全网**（描述的是一个话题，没指定具体页面） |
| 🧩 `both` | 先搜再抓（进阶，可手动指定） |

**优先级**：`/tavily search|extract`（用户显式锁定，最高） > AI 判断 > `TAVILY_MODE=auto` 内置规则。

**兜底与容错**：

| 情况 | 行为 |
|---|---|
| 审核 AI 调用失败 / 超时 | **自动回退**关键词规则，并在控制台提示 |
| 输出无法解析 | 保守判定为**不搜**（宁缺毋滥） |
| 未配置审核 key | 直接回退关键词规则 |
| `/search <问题>` | 强制搜索，**跳过**一切判断 |

```
/net ai      开启（AI 核验判断，推荐）
/net auto    回到关键词规则
/net on      总是联网
/net off     从不联网
/net         查看当前模式与各档说明
```

```bash
# 不联网演练：强制判定
set FATFISH_JUDGE_MOCK=true     # 强制"需要联网(搜索)"
set FATFISH_JUDGE_MOCK=extract  # 强制"需要联网(抓取)"
set FATFISH_JUDGE_MOCK=false    # 强制"无需联网"
```

### 15.12 安全边界 / Safety Boundary

| 项目 | 说明 |
|---|---|
| 核验 ≠ 免批准 | 核验通过后仍走人工报批（除非一键放行） |
| 打回不执行 | 被驳回的动作**完全不会**触碰文件 / 命令 |
| 重试有上限 | 最多 `VERIFY_MAX_RETRIES` 次，之后拦截或告警放行 |
| 补资料不计次 | `supplement` 另受 `VERIFY_MAX_SUPPLEMENTS`（默认 4）约束，防审查员无限索取 |
| 服务故障 | 默认 fail-open（放行 + 告警），可改 `closed` 变为「故障即停」 |
| 不在 auto 范围 | 只读操作默认不核验（想全查用 `/verify all`） |
| 执行类双重把关 | `ws_run_cmd` / `ws_run_python` **同时**受双人核验与人工报批约束 |

---

## 十六、两道闸门与风险分级 / Two Gates & Risk Tiers

> 一句话：**每个有副作用的动作要过两道关 —— 先由审查员 AI 复核，再由你点头。**
> 「只读免报批」缩的是**人工点击量**，「只读探针免送审」缩的是 **token**；
> 两条判定彼此独立，安全闸门的**数量**没有减少。

### 16.1 风险分级（单一事实来源）/ Risk Tiers

| 级别 | 工具 | 人工报批 | AI 核验 |
|---|---|---|---|
| **L0 只读** | `ws_where` `ws_list` `ws_read` `ws_search` `ws_forget` | ❌ **免**（`ws_read` 命中敏感文件时强制恢复） | 仅 `verify_mode=all` |
| **L1 低副可逆** | `ws_mkdir` `ws_cd` `ws_cd_approve` | ❌ 免 | ✅ auto |
| **L2 内容写入** | `ws_write` `ws_append` `ws_replace` `ws_delete` | ✅ 必须 | ✅ auto |
| **L3 执行** | `ws_run_cmd` `ws_run_python` | ✅ 必须 | ✅ auto（静态只读的 Python 除外） |

> 📌 两条判定**彼此独立**：人工报批 ← `_approval_needed()`；AI 核验 ← `pick_actions()` + 只读过滤。
> 所以「只读 Python 免核验」豁免的**只有 AI 第二意见**，人工报批**一分未减**。

### 16.2 敏感文件守护 / Sensitive Files

只读不等于无风险：读出来的内容会进入模型上下文。凡属「读取即泄露」的文件，
**一律恢复人工报批 + 强制核验**（命中时控制台显示 `⚠️ 敏感文件（强制人工）`）。

```
文件名 glob：.env / .env.* / *.env / *.key / *.pem / *.pfx / *.p12
             *.keystore / *.jks / id_rsa* / id_ed25519* / id_dsa*
             *.credential* / *.secret* / *passwd* / *password*
             .netrc / .npmrc / .pypirc / .git-credentials
             credentials.json / secrets.json / service_account*.json
路径前缀：  _backup/.env*   .git/
```

实现为**纯字符串判定、零 IO**（`workspace.is_sensitive_path()`）。

### 16.3 只读 Python 的静态判定 / Static Read-Only Check

`_python_is_readonly(code)`：只解析 AST、**绝不执行**。判定取向是**「默认拒绝」**——
以下任一情况直接照常送审：

| 拦截维度 | 说明 |
|---|---|
| 导入白名单 | 只认 `os / sys / re / json / math / time / datetime / hashlib / pathlib / base64 …` 等只读面模块；`subprocess / socket / shutil / requests / tempfile …` 一律拒 |
| **能力根白名单** | 对 `os / pathlib / io / shutil / subprocess / socket / requests / urllib / http / ftplib / smtplib / telnetlib / pty`：**只放行显式列出的只读属性**，其余一律拦（于是 `os.execv` / `os.fork` 这类变体自动被覆盖） |
| **别名追踪** | `p = pathlib.Path(x)`、`d = os.path`、`import os as _o`、`for p in Path('.').glob()`、`with open(...) as fh` → 还原成能力根后按对应白名单判（`p.rename()` 会拦，`p.read_text()` 放行） |
| 环境变量 | 访问 `environ / getenv / putenv / setenv` **无条件拦截**（子进程继承了 `.env` 的密钥！） |
| 字面量痕迹 | 字符串常量出现 `.env / id_rsa / .pem / api_key / secret / password / credential / sk-` → 送审（防 `open(".env")`） |
| `open()` 模式 | **只有字面量且为只读模式才放行**；模式是变量 / 含 `w a x +` → 送审 |
| 动态与赋值 | `exec / eval / compile / __import__ / getattr / setattr / delattr / globals / locals / vars`；属性赋值、下标赋值；`global / nonlocal` |
| 其它 | 解析失败、空代码、超过 40000 字符 |

> ⚠️ **诚实边界**：判为「只读」只代表**静态上没看到危险面**，**不等于安全证明**。
> 它换来的是「省一次审查员调用」，代价由**人工报批**照常兜底。

### 16.4 相关开关 / Switches

| 设置项 | 默认 | 作用 | 回滚到旧行为 |
|---|---|---|---|
| `approve_scope` | `writes` | `all` = 只读也报批（旧行为）/ `writes` = 只读免报批 | `/set approve_scope all` |
| `verify_readonly_python` | `on` | 静态只读的 Python 免 AI 核验 | `/set verify_readonly_python off` |
| `auto_approve_scope` | `all` | 一键放行覆盖范围（见 5.4） | `/set auto_approve_scope writes` |

`.env` 键名：`APPROVE_SCOPE` / `VERIFY_READONLY_PYTHON` / `AUTO_APPROVE_SCOPE`（持久化用 `/set save`）。

### 16.5 为什么「一键放行」时审查员反而更严 / Why the Verifier Gets Stricter

一键放行跳过的是**人工**那道闸门，于是审查员的隐含前提（「后面还有人工兜底」）不再成立。
补偿措施是：被自动放行的批次，送审材料里会**如实告知**审查员
「一键放行已开启（范围：X）……你是唯一的外部复核，请按原标准从严」，
并且**敏感文件永远不参与自动放行**。

> ⚠️ 默认档 `all` 下，删除 / 执行也会被一键放行覆盖，人工闸门**仅在敏感文件上保留**。
> 想回到保守档：`/set auto_approve_scope writes`。
> 对齐性：被自动放行的批次必带提示，带提示的批次必是被自动放行的 —— 不存在「被放行却无人知情」的批次。

---

## 十七、设置中心 `/set`：所有旋钮集中一处 / Settings Hub

> 一句话：**所有可调参数集中在一处查看、修改、还原、持久化。**
> One line: **All tunable parameters: view, change, reset, persist — in one place.**

### 17.1 三个概念（先搞清这个）/ Three Concepts

| 概念 | 含义 |
|---|---|
| **出厂默认** `default` | **程序启动那一刻**生效的值（已包含 `.env` 的覆盖）。不可变快照。 |
| **当前值** `current` | 现在正在用的值，可能被 `/set`、`/net`、`/verify` 等命令改过。 |
| **偏离** `dirty` | 当前值 ≠ 出厂默认 → 在列表里标 `*`。 |

> 📌 **reset 的语义**：还原到「**本次启动时**的状态」。
> `.env` 里写了 `VERIFY_MODE=off`，启动默认就是 `off`，`reset` 也回到 `off`
> （而不是回到代码里写死的 `auto`）。

### 17.2 命令速查 / Command Reference

```bash
/set                       # 列出全部常用设置（隐藏进阶项）
/set all                   # 列出全部（含进阶项，如送审长度上限）
/set diff                  # 只看「已偏离默认」的项
/set <项名>                # 查看单项：现值 / 默认 / 类型 / 说明 / 可选值
/set <项名> <值>           # 修改（本次运行生效）
/set reset                 # 全部还原为出厂默认
/set reset <项名>          # 单项还原
/set profile               # 列出预设方案（见 17.3）
/set profile <名>          # 套用预设方案
/set save                  # 把「偏离默认」的项写入 .env（持久化）
```

`/settings` 等价于 `/set`。

### 17.3 预设方案（Profile）/ Profiles

```bash
/set profile                    # 列出全部方案
/set profile strict --show      # 只看会改什么，不执行
/set profile strict             # 套用（只改方案涉及的项）
/set profile strict --reset     # 先全部还原，再套用（干净基线）
```

支持**中文别名**（`/set profile 铁壁` = `/set profile strict`）与**前缀匹配**（`str` → `strict`）。

| 方案 | 别名 | 一句话 | 改动项 |
|---|---|---|---|
| 💸 `cheap` | 省钱 / save / eco | 压缩送审量，省 token 省时间 | 11 |
| 🛡 `strict` | 严格 / 铁壁 / safe | 连只读也审、故障即停、禁一键放行 | 10 |
| ⚡ `fast` | 快速 / speed / turbo | 少打扰、免报批，适合信任场景 | 7 |
| 🖐 `manual` | 手动 / hand | 每一步都要你点头 | 6 |
| 📴 `offline` | 离线 / nolink | 完全不联网 | 1 |
| 🔬 `debug` | 调试 / diag / trace | 全量核验 + 镜像 + 计时 | 4 |
| 🏠 `default` | 默认 / 重置 / base | 还原为启动时的出厂配置 | — |

**两个典型方案的细节**：

| 💸 `cheap` | 值 | 说明 |
|---|---|---|
| `verifier_prompt_chars` | 20000 | 送审总上限（默认 64000） |
| `verifier_plan_chars` | 6000 | 方案上限（默认 16000） |
| `verifier_context_chars` | 6000 | 上下文（默认 24000） |
| `verifier_max_tokens` | 800 | 审查意见输出（默认 2000） |
| `max_history` | 150 | 历史条数（默认 400） |
| `max_history_tokens` | 300000 | 历史预算（默认 80 万） |
| `net_mode` | ai | AI 判断联网，避免无谓搜索 |
| `verify_final_answer` | off | 不复核答复 |

| 🛡 `strict` | 值 | 说明 |
|---|---|---|
| `verify_mode` | **all** | 连只读操作也核验 |
| `verify_max_retries` | 3 | 多给一次改过机会 |
| `verify_final_answer` | **on** | 答复也要复核 |
| `verify_fail_mode` | **closed** | 核验服务挂了就停下等 |
| `auto_approve_enabled` | **off** | 不给「一键放行」留口子 |
| `verifier_prompt_chars` | 64000 | 给足信息，审得更准 |

> ⚡ `fast` 会开 `auto_approve_default`（即**默认免报批**），请按信任场景谨慎使用。

**设计要点**：只改涉及的项（未列出的保持不动，不归零）；`--reset` 可选；`--show` 可预览；
**整批原子性**（方案里若引用未注册项或非法值 → 整批拒绝，不会改一半）；套完满意可 `/set save` 写进 `.env`。

### 17.4 全部设置项 / All Settings

**🧠 主模型**：`model`（`.env` 键 `FATFISH_MODEL`）、`max_reply_tokens`、`api_timeout`

**🧿 双人核验**：

| 项名 | 类型 | 可选值 | 说明 |
|---|---|---|---|
| `verify_mode` | 枚举 | `off` / `auto` / `all` | 核验范围 |
| `verify_strict` | 开关 | | 超重试上限即拦截 |
| `verify_max_retries` | 整数 | | 打回重交次数上限（**仅 revise 计入**） |
| `verify_max_supplements` | 整数 | | 免费补充资料轮数上限（`supplement` **不计退回次数**） |
| `verify_final_answer` | 开关 | | 是否复核最终答复 |
| `verify_fail_mode` | 枚举 | `open` / `closed` | 核验服务故障时的策略 |
| `verify_mirror` | 开关 | | 审查意见镜像到监控器 |
| `verifier_model` | 文本 | | 审查员模型（建议与主模型不同） |

**进阶项**（`/set all` 可见）——送审长度上限，单位字符：
`verifier_prompt_chars`(64000) / `verifier_plan_chars`(16000) / `verifier_action_preview`(4000) /
`verifier_replace_preview`(2000) / `verifier_goal_chars`(6000) / `verifier_context_chars`(24000) /
`verifier_answer_chars`(24000) / `verifier_answer_ctx`(12000) / `verifier_max_tokens`(2000) /
`verifier_timeout`(120) / `verifier_temperature`(0.2)

**🌐 联网**：`net_mode`（`on` / `off` / `auto` / `ai`）、`tavily_mode`（`auto` / `search` / `extract` / `both`）、`extract_max_len`

**🔐 报批**：`approve_run_tools`、`auto_approve_enabled`、`auto_approve_default`、
`approve_scope`、`auto_approve_scope`（后两项见第十六章）

**🖥 界面**：`show_timer`、`show_wait_anim`

**📏 上下文**：`max_history`、`max_history_tokens`、`trim_tool_clip_chars`、`max_tool_rounds`

### 17.5 与其它命令的关系 / One Source of Truth

**所有开关类命令都走设置中心**，两边始终一致：

| 命令 | 等价于 |
|---|---|
| `/net ai` | `/set net_mode ai` |
| `/tavily both` | `/set tavily_mode both` |
| `/verify off` | `/set verify_mode off` |
| `/verify strict off` | `/set verify_strict off` |
| `/verify model X` | `/set verifier_model X` |
| `/verify answer on` | `/set verify_final_answer on` |
| `/verify retries 3` | `/set verify_max_retries 3` |
| `/verify supplements 5` | `/set verify_max_supplements 5` |
| `/verify fail closed` | `/set verify_fail_mode closed` |
| `/verify mirror off` | `/set verify_mirror off` |
| `/timer off` | `/set show_timer off` |
| `/auto off` | `/set auto_approve_enabled off` |

所以：**用哪个都行**，改完立刻双向同步，`/set diff` 都能看到。

### 17.6 生效范围与持久化 / Scope & Persistence

```
        /set <项> <值>
             │
             ▼
     立即生效（本次运行）          ← 关掉程序就没了
             │
             │  /set save
             ▼
        写入 .env                ← 重启后仍然生效
             │
             │  /set reset
             ▼
     还原为「本次启动时的值」       ← 本次运行内有效
```

| 操作 | 影响范围 | 持久？ |
|---|---|---|
| `/set <项> <值>` | 当前进程立即生效 | ❌ 关掉即失 |
| `/set save` | 写入 `.env` | ✅ 重启仍在 |
| `/set reset [项]` | 本次运行还原为启动值 | ❌ |
| 想永久还原 | 手动删掉 `.env` 里对应的行 | ✅ |

**`/set save` 只写「偏离默认」的项** —— 没改过的不动，所以 `.env` 不会被刷满。

### 17.7 安全设计 / Safety Design

| 机制 | 说明 |
|---|---|
| **类型校验** | 整数项收到 `abc` → `⚠️ 需要整数（收到 'abc'）`，不改动现值 |
| **枚举校验** | `verify_mode banana` → `⚠️ 可选值：off / auto / all` |
| **范围校验** | 整数 / 浮点项不接受负数 |
| **失败回滚** | 应用回调报错时，当前值**回滚**为原值（不会留下半改状态） |
| **未知项提示** | `/set nope` 会提示「未找到设置项」，并给出相近名字的建议 |
| **`.env` 就地替换** | 只改对应行，不追加重复键，不破坏注释结构 |

```bash
# 设置中心自身的单元测试（不依赖主程序）
python settings.py
```

---

## 十八、切换模型 / API：改 `.env` 三行 + `/reload` / Switching Models & APIs

> 一句话：**肥鱼「本体」用哪家 API、哪个模型，全部由 `.env` 决定，代码零改动。**
> 换服务商 = 改三行 `.env` + 程序里敲一句 `/reload`。
> One line: **Which API/model FatFish uses is decided entirely by `.env`. Zero code changes.**

### 18.1 三件套 / The Big Three

```dotenv
FATFISH_API_KEY=sk-xxxxxxxxxxxxxxxx
FATFISH_BASE_URL=https://api.deepseek.com
FATFISH_MODEL=deepseek-flash
```

| 变量 | 作用 | 留空时 |
|---|---|---|
| `FATFISH_API_KEY` | 访问密钥 | 回退 `DEEPSEEK_API_KEY`，再无则告警 |
| `FATFISH_BASE_URL` | 接口地址（OpenAI 兼容） | 回退 `DEEPSEEK_BASE_URL`，再无则 `https://api.deepseek.com` |
| `FATFISH_MODEL` | 模型名 | 回退 `DEEPSEEK_MODEL`，再无则 `deepseek-flash` |

**优先级**：`FATFISH_*` → `DEEPSEEK_*`（旧写法，向后兼容）→ 内置默认。

> ⚠️ 留空的项**不要写行尾注释**。`python-dotenv` 对 `KEY=   # 注释` 不会剥离注释，
> 会把注释文本当成值（曾导致 `Authorization` 头含中文、`httpx` 直接报 ascii 编码错）。
> 注释请独立成行；程序也内置了 `_env_clean()` 兜底，但最好别依赖它。

### 18.2 两种生效方式 / Two Ways

| 方式 | 操作 | 作用域 |
|---|---|---|
| **持久化** | 改 `.env` → 程序里输入 `/reload` | 永久生效 |
| **临时** | `/model <模型名>` | 仅本次会话（接口地址不变） |

```text
/model                    查看当前 模型 / 接口 / 密钥（掩码）
/model deepseek-chat      临时换模型（当次会话）
/reload                   重读 .env（主模型 + 审查员 + Tavily 一起重载）
```

启动横幅与 `/model` 都会显示当前实际使用的 `模型 @ 接口`，一眼可辨。

### 18.3 常见厂商配置示例 / Vendor Examples

> 下表 base_url 仅为示例，**以各厂商官方文档为准**；填入的模型名必须真实存在。

| 厂商 | `FATFISH_BASE_URL` | 模型名示例 |
|---|---|---|
| DeepSeek | `https://api.deepseek.com` | `deepseek-chat` / `deepseek-reasoner` |
| 月之暗面 Kimi | `https://api.moonshot.cn/v1` | `moonshot-v1-8k` / `kimi-k2-0905-preview` |
| OpenAI | `https://api.openai.com/v1` | `gpt-4o` / `gpt-4o-mini` |
| 智谱 GLM | `https://open.bigmodel.cn/api/paas/v4` | `glm-4-plus` |
| 阿里通义 | `https://dashscope.aliyuncs.com/compatible-mode/v1` | `qwen-plus` / `qwen-max` |
| 硅基流动 | `https://api.siliconflow.cn/v1` | `deepseek-ai/DeepSeek-V3` |
| OpenRouter | `https://openrouter.ai/api/v1` | `anthropic/claude-3.5-sonnet` |
| 本地 Ollama | `http://localhost:11434/v1` | `qwen2.5:7b`（key 随便填，如 `ollama`） |

```dotenv
FATFISH_API_KEY=sk-你的新key
FATFISH_BASE_URL=https://api.moonshot.cn/v1
FATFISH_MODEL=moonshot-v1-8k
```

改完在程序里敲 `/reload`，看到横幅刷新即成功。

### 18.4 换别家时必须一起注意的三件事 / Three Gotchas

**① 双人核验的审查员也要跟着换。** 换了主模型后，若 `VERIFIER_MODEL` 仍写着旧厂商的模型名，
审查环节会 401 / 404。最省事的做法是**把审查员留空，让它跟随主模型**：

```dotenv
VERIFIER_API_KEY=
VERIFIER_BASE_URL=
VERIFIER_MODEL=
```

或显式指定同一家的另一个模型，形成异源审查（`VERIFIER_MODEL=moonshot-v1-32k`）。

**② 目标服务必须支持两样东西**：OpenAI 兼容的 `/chat/completions`，
以及 **Function Calling（`tools` 参数）** —— 不支持的话工作台工具（读写文件等）全部失效。

**③ 参数差异**：主程序里使用了 `max_tokens`、`tools`、`response_format=json_object`（审查员用）。
个别服务对参数名或取值挑剔，若报 400，先看返回的错误信息，必要时下调 `MAX_REPLY_TOKENS`。

### 18.5 验证清单 / Verification Checklist

```bash
# 1) 语法与模块自检
python -m py_compile FATHFISH.py verify_tools.py
python verify_tools.py

# 2) 启动看横幅（期望一行：🧠 主模型 [main model]：<你的模型> @ <你的接口>）
python launch.py

# 3) 程序内确认
/model        # 查看三件套
/reload       # 重载后再看一次
/verify ping  # 顺带确认审查员也活着
```

**不改 `.env` 也能临时验证别家接口**（环境变量优先于 `.env`）：

```bat
set FATFISH_MODEL=moonshot-v1-8k
set FATFISH_BASE_URL=https://api.moonshot.cn/v1
python FATHFISH.py
```

### 18.6 `.env` 全量配置项清单（中文详解）/ All `.env` Keys Explained

> 为了能整份嵌进 `FATPACKII.bat`（bat 外壳必须纯 ASCII，否则 cmd 会按字节偏移错位），
> `.env` 本体只保留**纯 ASCII 骨架**（键名 + 极简英文注释）；完整中文说明统统住在本节。
> The file itself stays ASCII-only so the installer can embed it; all Chinese docs live here.

#### A. 主模型（执行者）—— 肥鱼「本体」调用哪家 API

| 键 | 默认 | 说明 |
|---|---|---|
| `FATFISH_API_KEY` | 空 | **必填**。留空则回退 `DEEPSEEK_API_KEY`，再无则告警 |
| `FATFISH_BASE_URL` | `https://api.deepseek.com` | 服务地址（任意 OpenAI 兼容服务） |
| `FATFISH_MODEL` | `deepseek-flash` | 模型名 |

- 改这三行 → 程序里敲 `/reload` 即生效：**无需改代码、无需重启**。
- 换别家示例：

```ini
FATFISH_API_KEY=sk-xxxxxxxxxxxxxxxx
FATFISH_BASE_URL=https://api.moonshot.cn/v1
FATFISH_MODEL=moonshot-v1-8k
```

- **向后兼容**：`FATFISH_*` 留空时，自动回退读 `DEEPSEEK_API_KEY` / `DEEPSEEK_BASE_URL` / `DEEPSEEK_MODEL`。

#### B. 联网搜索（选填）

| 键 | 默认 | 说明 |
|---|---|---|
| `TAVILY_API_KEY` | 空 | 选填。没它就**不能联网搜索**（功能静默降级，不会崩）；申请见第十四章 |

#### C. 双人核验（第二位 AI 审查员）

| 键 | 默认 | 说明 |
|---|---|---|
| `VERIFIER_API_KEY` | 空 | 审查员密钥；**留空 → 沿用主模型 key** |
| `VERIFIER_BASE_URL` | `https://api.deepseek.com` | 审查员接口 |
| `VERIFIER_MODEL` | `deepseek-flash` | 审查员模型（建议与主模型不同，形成异源核验） |

> 为什么要有第二位 AI、它拦在哪、三种裁决是什么 —— 见**第十五章**。

#### D. 核验行为开关

| 键 | 默认 | 说明 |
|---|---|---|
| `VERIFY_MODE` | `auto` | `off`=关闭 / `auto`=仅核验有副作用动作 / `all`=连只读操作也核验 |
| `VERIFY_STRICT` | `1` | 超重试上限后：`1`=拦截（严格） / `0`=放行（宽松） |
| `VERIFY_MAX_RETRIES` | `4` | 同一轮内允许「打回重交」的次数（仅 revise 计入） |
| `VERIFY_MAX_SUPPLEMENTS` | `4` | 「要求补充资料」的免费轮数上限，不计入退回次数 |
| `VERIFY_FINAL_ANSWER` | `0` | 是否也复核最终答复：`0`=否 / `1`=是 |
| `VERIFY_FAIL_MODE` | `open` | 核验服务不可用时：`open`=放行并告警 / `closed`=拦截等待 |
| `VERIFY_MIRROR` | `1` | 把审查意见镜像到监控器窗口：`1`=开 / `0`=关 |

#### E. 送审「方案」长度上限（业务 AI → 复核 AI）

单位：字符（中文约 1 字 ≈ 1.5 token）。留空或填 `0` 则用内置默认值。
调大 = 复核 AI 能看到更完整信息，但更费 token、更慢。

| 键 | 内置默认 | 说明 |
|---|---|---|
| `VERIFIER_PROMPT_CHARS` | 64000 | 送审内容总上限（超限按优先级压缩，上下文先让步） |
| `VERIFIER_PLAN_CHARS` | 16000 | ★ 执行者自述 / 计划（业务 AI 的「方案」，重点项） |
| `VERIFIER_ACTION_PREVIEW` | 4000 | 单个动作的参数预览 |
| `VERIFIER_REPLACE_PREVIEW` | 2000 | `ws_replace` 的 old / new 各自 |
| `VERIFIER_GOAL_CHARS` | 6000 | 用户原始需求 |
| `VERIFIER_CONTEXT_CHARS` | 24000 | 近期上下文 |
| `VERIFIER_ANSWER_CHARS` | 24000 | 待复核的最终答复 |
| `VERIFIER_ANSWER_CTX` | 12000 | 答复复核时的上下文 |
| `VERIFIER_MAX_TOKENS` | 2000 | 审查意见的输出上限 |
| `VERIFIER_TIMEOUT` | 120 | 单次审查请求超时（秒） |

#### F. 人工报批

| 键 | 默认 | 说明 |
|---|---|---|
| `APPROVE_RUN_TOOLS` | `1` | 「执行命令 / 运行代码」是否也纳入人工报批：`1`=是 / `0`=否 |

#### G. 联网模式

| 键 | 默认 | 说明 |
|---|---|---|
| `NET_MODE` | `ai` | `on`=总是联网 / `off`=从不 / `auto`=关键词规则 / `ai`=第二位 AI 判定（推荐）。运行时也可 `/net on\|off\|auto\|ai` |
| `TAVILY_MODE` | `auto` | `auto`=AI 决定 search/extract（推荐）/ `search`、`extract`=强制锁定 / `both`=先搜索再抓前 2 条正文 |
| `VERIFIER_MAX_TOKENS` | 10000 | 审查意见输出上限（**此处为有效值**，覆盖 E 组同名默认） |

#### ⚠️ H. 两条必读陷阱（`python-dotenv` 的坑）

1. **留空的项，不要写行尾注释。**
   `python-dotenv` 不剥离 `KEY=   # 注释` 里的注释，会把整串当值；
   于是 `KEY=  # 注释` 成了含 `#` 的字符串，塞进 HTTP 头时 `httpx` 只允许 ASCII
   → 直接 `UnicodeEncodeError: 'ascii' codec` 崩掉。**规则：注释独立成行，空值单独占行。**
2. **`=` 两边不要有空格。** 写 `KEY=value`，不要 `KEY = value`。

> 📌 想持久化 `/set` 的改动：`/set save` 会把「偏离默认」的项写进 `.env`，
> 只改对应行、不追加重复键、不破坏注释结构（见 17.6 / 17.7）。

---

## 十九、极简速查（懒人版）/ Quick Reference (Lazy Edition)

```
1. 装 Python（勾 Add to PATH）
2. 双击 FATPACKII.bat
3. 去 platform.deepseek.com 申请 DeepSeek Key（sk- 开头）
   （想联网就再去 tavily.com 申请 Tavily Key，tvly- 开头）
4. 把 Key 填进自动打开的 .env 文件（= 两边不留空格）
5. 双击 fatfish1.1.1.bat
6. 会开两个窗口：主窗口聊天，监控窗口看子程序输出
7. 开始打字聊天 🐟
```

**就这几步，猪都会了。祝你玩得开心！** 🎉
**That's it — even a pig can do it. Have fun!** 🎉

---

## 附录 A：一次典型会话长什么样 / Appendix A: A Typical Session

```text
🐟 DeepSeek 联网肥鱼 H 版 v1.1.1 已启动            ← 渐变彩色横幅
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
 🐟 肥鱼开工自检 · BOOT REPORT
 <日期> <时间> 周X ｜ UTC+8:00 中国标准时间
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
 ⏱  <本地时间>  ｜  UTC <对应时间>
 🧬  PID <进程号> ｜ Python 3.8.x ｜ 已运行 Ns     启动 <时间>
 🐍  <Python 解释器位置>
 📄  <程序目录>\FATHFISH.py
      · 体积与改动时间
 🛠  <工作台根>    （标注是否与 cwd 一致）
🗂  同名副本扫描结果
 🩺  需要留意：…
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  🐟 就绪 · 用 /help 看命令，/status 看运行状态

🌐~🎯 你 [You] ▸ 列出工作台里的文件

  ✅ 工作台 ws_list → workspace/          ← 只读操作默认免报批，直接执行
  🤖 AI（蓝色冷静配色）
  ...
  ⏱️ 本轮 12.4s

🌐~🎯 你 ▸ （你盯着屏幕想了 8 秒才继续打字）
  ⏱️ 停留 8.2s
```

> 📌 面板可用 `.env` 的 `BOOT_REPORT=0` 关闭；完整运行状态随时用 `/status` 调出。
> 启动横幅与面板都只是"开场"，真正的状态以 `/status` 为准。

## 附录 B：目录结构总览 / Appendix B: Directory Tree

```text
FatFish/
├── FATPACKII.bat            ← 一键安装器（内嵌 17 个文件；⚠️ 载荷是打包时刻的快照）
├── fatfish1.1.1.bat         ← 启动器（双击这个）
├── fatfish_runtime.bat      ← 运行窗口（探语言 / 写 PID / 跑 launch）
├── fatfish_lang.bat         ← 语言探测（四级降级）
├── launch.py                ← 启动枢纽（拿 PID / 拉监控器）
├── FATHFISH.py              ← 主程序（聊天 REPL + 工具循环）
├── fatfish_watcher.py       ← 监控器（只滚启动后的新增输出）
├── workspace.py             ← 工作台沙盒 / 14 个工具 / 敏感文件守护
├── file_tools.py            ← 文件·目录·图片读取（魔数嗅探真伪）
├── net_tools.py             ← Tavily 联网（search / extract）
├── exec_tools.py            ← 跑命令 / 跑 Python，输出落盘供监控器 tail
├── verify_tools.py          ← 双人核验引擎（审查员 + 联网判断 + 镜像）
├── boot_report.py           ← 开工自检（环境快照 + 注入提示词）
├── settings.py              ← 参数中心（注册 / 快照 / 预设 / 持久化）
├── ui_core.py               ← 展示层（颜色 / 情绪 / 标记 / 横幅 / 等待动画）
├── common.py                ← 公共基础件（时间戳 / 日期分层目录）
├── README.md                ← 本文件（第 3.1 版）
├── .env                     ← 你的密钥（🔴 别外传）
├── .gitignore               ← 防误传名单
├── _fatfish_pid.txt         ← runtime 窗口 PID（运行时生成；启动时重写、退出即删）
│
├── workspace/               ← 🏠 工作台沙盒（含 make_fatpack.py）
│   └── make_fatpack.py      ← 安装器生成器（文件更新后重跑它）
├── logs/                    ← 归档区（年 → 月 → 日 分层：聊天日志 / 执行输出）
├── generated_code/          ← 肥鱼自动存的代码（首次运行自动创建）
├── _backup/                 ← 根一级文件的自动备份
├── .fatfish_tmp/            ← 临时脚本（运行时创建，跑完删）
├── __pycache__/             ← 字节码缓存（运行时创建）
├── agents/                  ← Agent 模块（含 agent_reviewer.py）
├── venv/                    ← Python 虚拟环境（安装器创建）
├── sub/                     ← 子目录测试残留
├── chat_logs/               ← 旧版聊天日志（已由 logs/ 接管）
├── fatfish/                 ← git 仓库副本（含 .git/ 与独立 venv/）
├── oldpackmd/               ← 归档：旧安装器 + 4 份专题文档原文
├── oldver/                  ← 归档：历代版本 + CHANGELOG.md（全量更新记录）
└── workspaceX/              ← 归档：早期原型（FAT-A FISH.py 等）
```


---

## 附录 C：本版文档的核对结论 / Appendix C: Verification Log

本版 README 的所有架构描述都来自**逐文件通读 + 实测验证**，不是猜测：

| 结论 / Conclusion | 怎么得出的 / How Verified |
|---|---|
| 启动链路四层 | 通读 `fatfish1.1.1.bat` / `fatfish_runtime.bat` / `launch.py` / `fatfish_watcher.py` |
| `.hex` 是纯冗余副本（已删除） | 全项目搜索引用 + 核对安装器内嵌清单 + 解码后与 `.bat` 逐字节比对 |
| `FATPACK.bat` 重装前内嵌 8 个文件、4 个过期 | 抓出载荷字典 → 解码 → 与现场文件比对 |
| `FATPACK.bat` 重装后内嵌 12 个全一致 | 抽载荷 → 沙箱两遍实跑 → 逐字节比对全部通过 |
| 两个安装器内容等价 | 比对内嵌清单与主程序 base64 完全相同 |
| 所有常量上限数值 | 逐文件通读源码顶部常量区 |
| 编码/行尾/BOM 状态 | 二进制读入逐文件检测（全部 UTF-8 无 BOM，bat 为 CRLF，`FATPACK.bat` 为 LF）|
| 归档目录结构 | 遍历 `logs` 与 `generated_code`，按文件名模式归类统计 |
| `_fatfish_pid.txt` 语义 | 通读 `fatfish_runtime.bat`；并用「真身 PID 对照」实验验证重定向法（探测值 == 真身 cmd PID）|
| 计时器行为 | 7 个场景单元测试（首轮/停留/斜杠命令/退出/关闭开关/空输入/边界值）+ 真进程管道冒烟测试|
| `FATPACKI.bat` 可用性 | 真调 PowerShell 抽载荷 → 沙箱跑两遍（全新安装 / 覆盖更新）→ **逐字节比对通过** |
| 一键放行行为 | 按键矩阵单元测试 + 复位场景 + **真进程端到端**（做法见文末彩蛋）|
| 全模块通读（rev.2.8）| 全部模块编译通过；跨模块属性引用全部命中；根级文档逐字通读 |
| 报批 / 放行判定（rev.2.8）| `_never_auto_approve` 三档矩阵实测（`none` 全拦 / `writes` 放写入 / `all` 仅拦敏感文件）；`.env` 读写双路径均拦 |
| 监控器回放修复（rev.2.8）| `ExecTailer._prime` 基线实测：新逻辑冷启动回放 **0** 行；旧逻辑会把历史整份回放（见文末彩蛋） |
| `.env` 绑定补全（rev.2.8）| AST 清点全部 `register`，`env_name` 覆盖率 100%；相关常量改为 `_env_*` 读取并实测可覆盖 |
| 专题文档合并（rev.3.1）| 逐字通读 4 份 md，按「机制 / 参数 / 命令 / 坑」四类提炼为第十五~十八章；并全库检索校正 3 处过时描述（`.env` 三件套、模型名改法、参数索引）|

---

## 附录 D：开发彩蛋 / Easter Eggs

正文为了让文档干净，把不少「当时到底怎么翻车的」细节都抽走了。它们其实是这份文档里最值钱的部分 ——
想弄明白「代码为什么非要那么写」的人，直接看这里。

### 🥚 彩蛋 1：一个转义字符把安装流程搞崩了

安装器生成器的模板原本用普通三引号字符串写。模板里那句临时文件路径 `%TEMP%\fatfish_extract_...`
中的 `\f` 被 Python 当成**换页符**（0x0C）解释，路径实际变成了「盘符 + 换页符 + atfish_extract_...」，
PowerShell 当场报 `Illegal characters in path`，安装在第 2 步就断了。

**修法**：模板一律写成原始字符串。一行之差，找了半天。

### 🥚 彩蛋 2：cmd 把一行汉字「劈成两半」去执行

原以为 bat 里放中文无妨，实测报出这种错：

```text
'显式指定' is not recognized as an internal or external command
'<半个汉字>新获取一份完整的' is not recognized as an internal or external command
```

原因：cmd 解析批处理按**字节偏移**续读文件；文件里有多字节 UTF-8 字符时偏移会漂移，
于是它**从一行的中间开始读**，把半截 `REM` 注释当成命令执行（两个单引号之间就是被劈开的半个汉字）。

**修法**：bat 外壳一律纯 ASCII 英文，中文只放在**载荷脚本**里（载荷由 PowerShell 以
`-Encoding UTF8` 显式读写，完全不经 cmd 解析）。这就是为什么安装器提示全是英文，
而解包完打印出来的清单反而是中文。

### 🥚 彩蛋 3：分隔标记到底几个字符？

那个用作分界的标记（写在安装器正文中间的那一串）实际是 **11** 个字符，
可旧代码里按 `+12` 去截 —— 多算了 1 个。结果它顺手吃掉了标记后面的第一个换行符，
**恰好无害**，于是这个 bug 一直没人发现。新版改成按实际长度注入，
而且长度是**算出来的**、不是硬编码，将来改标记名也不会错位。

顺带一提：这个标记在代码里是拆成两小段拼出来的 —— 为了让那行代码本身不含完整标记，
否则「找最后一个标记」的逻辑会先搜到自己。

### 🥚 彩蛋 4：给肥鱼窗口「验明正身」

记录运行窗口 PID 的那个小文件，最早是靠「窗口标题反查」实现的 ——
先把窗口标题改成一个随机串，再用 PowerShell 按标题找自己。
在传统 cmd 窗口下没问题，但在 **Windows Terminal** 里，`cmd.exe` 没有自己的顶层窗口，
标题其实属于终端宿主程序，于是反查抓到的是**终端**的 PID，
外部工具照着它操作，甚至会误伤整个终端窗口（历史日志里确实抓到过）。

**修法（重定向法）**：换一句带重定向的命令跑 PowerShell，让它报出「自己的父进程 PID」——
重定向由 cmd 自己处理文件句柄，PowerShell 是本窗口直接启动的，所以父进程就是运行窗口本体。
降级链：`Get-CimInstance` → `Get-WmiObject` → 窗口标题反查（仅传统终端有效）→ 写 `0`。

### 🥚 彩蛋 5：监控器一口气回放了四万行

监控器是「子程序输出的镜子」，做法是 tail 输出文件。但它早期**不看时间**，
冷启动时会把当天、甚至更早的执行输出**从头整份刷一遍** ——
实测同一个目录回放量超过 4 万行，屏幕瞬间被历史输出淹没，也是监控器日志膨胀的主因。

**修法**：给它加一个**启动基线** —— 启动时先把已存在的输出文件记到当前长度，
之后只滚基线之后的新增内容。修好后，同样场景回放 **0** 行。

### 🥚 彩蛋 6：`.hex` 转写文件的兴衰

曾经有 3 个 `.hex` 文件，是三个 `.bat` 的**逐字节十六进制转写**
（每行 16 字节、第 8 字节后加一个空格分栏、小写、CRLF 结尾；`40 65 63 68 6f` 就是 `@echo`）。
它们被当成「保真备份」一直躺在目录里，直到被逐项核实：

- 全项目搜索，命中的地方**没有一处是「读取」**（唯一的代码动作是写出）；
- 安装器内嵌清单里**根本没有**它们；
- 每个字节在原 `.bat` 里都找得到 —— 信息量 100% 冗余。

换句话说，它是一把**没有配钥匙的备用锁**。还原其实只要一行
`binascii.unhexlify(去掉空白(内容))`，但既然没有任何程序会去执行它，留着就只是占地方。
于是连生成器里的相关代码（常量、`hexdump()` 函数、生成循环）一起清掉了。

### 🥚 彩蛋 7：怎么证明「一键放行」真的只放行一轮？

单元测试只能证明按键识别对，证明不了端到端行为。于是做了个「真进程实验」：
注入一个假的 `openai` 模块，让它一口气发两轮工具调用 ——
第 1 轮在弹窗上按 `a`，看第 2 轮是否真的免问；再发一条新命令，看放行态是否真的复位。
结论：第 2 轮免问 ✅、新命令后恢复逐个报批 ✅。

### 🥚 彩蛋 8：备份区是怎么「爆仓」的

备份规则是「根一级文件每次被改前都全量留档」，遇上疯狂改文档的夜晚就很壮观 ——
一晚能堆出上百份副本。后来按「每个文件只留最近几份」清了一轮，
并把历史副本连同工作台里的非程序文件一起挪进了单独的归档目录。
现在文档里索性**不写死备份数量** —— 因为它秒级变化，写什么都是错的。

---

> 📌 Key 怎么申请、怎么填、填错了咋办，看 **「十四、API Key 怎么申请？」**
> 📌 真正要跑的核心只有 4 个：`FATPACKII.bat`（装）、
> `fatfish1.1.1.bat`（启）、`.env`（配）、`FATHFISH.py`（本体）。
