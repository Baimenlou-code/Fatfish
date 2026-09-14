# 🐟 肥鱼 FatFish —— 猪都能看懂的说明书（v1.0.4 · 文档第 2 版）
# 🐟 FatFish — A Manual Even a Pig Can Understand (v1.0.4 · Doc Rev.2)

> 一句话：**肥鱼是一个住在黑色命令行窗口里的 AI 助手。**
> 你打字问它，它回答你；它还能帮你读写文件、上网查资料、跑命令、跑代码、看图片。
>
> One line: **FatFish is an AI assistant that lives in a black command-line window.**
> You type, it answers; it can also read/write files, search the web, run commands, run code, and look at images.

**当前版本 Current version**：`v1.0.4`（横幅自称「G 版」/ banner calls itself "G Edition"）
**文档修订 Doc revision**：第 2.5 版，2026-09-14 / Rev.2.5, 2026-09-14
**运行环境 Runtime**：Windows + Python 3.8+（实测 3.8.10 通过 / verified on 3.8.10）

不用懂编程，照着下面做就行 👇
No programming knowledge needed — just follow along 👇

---

## 〇、这一版文档改了什么？ / What Changed in This Doc Revision?

| 项目 / Item | 旧文档 / Old Doc | 新文档 / New Doc |
|---|---|---|
| 启动器文件名 / Launcher name | `fatfish1.0.2.bat` ❌ | ✅ `fatfish1.0.4.bat` |
| 主程序文件名 / Main script | `FATFFISHI.py` ❌ | ✅ `FATGFISH.py` |
| 启动链路 / Launch chain | 没写 / not documented | ✅ 四层链路全图解 / full 4-layer diagram |
| 监控器窗口 / Watcher window | 没写 / not documented | ✅ 独立说明 / its own chapter |
| 跑命令 / 跑 Python | 只提一句 / one line | ✅ 独立章节 + 权限与落盘 / own chapter |
| 读图片 / Image input | 没写 / not documented | ✅ 独立说明 / documented |
| 数值上限 / All limits | 没写 / not documented | ✅ 完整参数表 / full table |
| 已知问题 / Known issues | 没写 / not documented | ✅ 实测发现 4 条 / 4 findings |
| 章节编号 / Chapter numbers | 「三步」下面却有第 3、4 步 ❌ | ✅ 编号已理顺 / renumbered |

### 📌 更新记录 / Changelog

| 时间 / When | 版本 / Ver | 内容 / What |
|---|---|---|
| 2026-09-14 | rev.2 | 全面重写文档：补启动链路、监控器、跑命令、参数表、已知问题等 |
| 2026-09-14 | rev.2.1 | **新增轮次计时器**（`/timer`）：统计「本轮耗时」与「你停留了多久」；文档同步（见 5.3 节）|
| 2026-09-14 | rev.2.2 | ① 计时显示**精简成单行**（`⏱️ 本轮 12.4s` / `⏱️ 停留 8.2s`）② **新增 `FATPACKI.bat`**：重做的安装器，内嵌 12 个最新文件，修掉原版两个致命缺陷 ③ 新增 `make_fatpack.py` 生成器（见 8.4）|
| 2026-09-14 | rev.2.3 | **新增「一键放行」**：报批时按 `a` / `1`，本批照批 + **本轮剩余读写全部免问**（发下一条命令即失效）；新增 `/auto` 命令（见 5.4）|
| 2026-09-14 | rev.2.4 | **重装 `FATPACK.bat`**：从 8 个过期文件换成 12 个最新文件（270 KB），两个安装器现在等价；载荷清单会印自己的文件名；旧版已留档进 `_backup/` |
| 2026-09-14 | rev.2.5 | **移除 3 个 `.hex` 转写文件**：实测核实它们不参与任何流程（100% 冗余），连同生成器里的相关代码、本文件与安装器里的描述一并清理（见 4.3）|

---

## 一、肥鱼能干啥？ / What Can FatFish Do?

| 能力 / Capability | 说人话 / In Plain Words |
|---|---|
| 💬 聊天 / Chat | 跟它说话，它回答你。回答还会**按情绪变色** / Talk to it, it replies — with mood-based colors |
| 📄 读文件 / Read files | 给路径就把它读进上下文，支持 `@路径` 和 `/read` / Give it a path; supports `@path` and `/read` |
| 📚 读整个目录 / Read directories | `/readr 目录` 递归读完整个目录树 / `/readr <dir>` walks the whole tree |
| 🖼️ 看图片 / See images | 图片按**文件头魔数**识别真伪，支持 PNG/JPEG/GIF/WebP / Magic-number sniffing, PNG/JPEG/GIF/WebP |
| ✍️ 写文件 / Write files | 新建、覆盖、追加、精确替换、删除、建目录、全文搜索 / create, overwrite, append, replace, delete, mkdir, search |
| 🛡️ 改文件要你点头 / Edits need approval | 读写文件前**批量问你一次**，按 `y` 才动 / asks once in a batch, needs `y` |
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

**双击 `FATPACKI.bat`**（或 `FATPACK.bat`，两者内容等价），然后等它自己跑完。
/ **Double-click `FATPACKI.bat`** (or `FATPACK.bat` — the two are equivalent), and wait.

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
# 肥鱼配置文件 —— 请把占位符替换成你自己的 API Key
# DeepSeek 申请：https://platform.deepseek.com/
# Tavily  申请：https://tavily.com/
DEEPSEEK_API_KEY=sk-在这里填入你的deepseek密钥
TAVILY_API_KEY=tvly-在这里填入你的tavily密钥
```

把等号后面的**占位文字**换成你自己的密钥，保存关闭。/ Replace placeholders with your real keys.

| 密钥 / Key | 用途 / Purpose | 申请地址 / Apply At | 前缀 / Prefix |
|---|---|---|---|
| `DEEPSEEK_API_KEY` | **必填**，没它没法聊天 / **Required** | <https://platform.deepseek.com/> | `sk-` |
| `TAVILY_API_KEY` | 选填，没它不能联网 / Optional, web search only | <https://tavily.com/> | `tvly-` |

> 💡 只填 DeepSeek 也能用，只是不能联网。
> 💡 只填了 DeepSeek 却遇到问题？那正常 —— 联网功能会静默降级，不会崩。

### 第 4 步：启动！ / Step 4: Launch!

**双击 `fatfish1.0.4.bat`**。/ **Double-click `fatfish1.0.4.bat`.**

看到彩色的肥鱼横幅就成功了 🎉 / If you see the colorful FatFish banner, you're in 🎉

**⚠️ 你会看到两个窗口，这是正常的：**

| 窗口 / Window | 标题 / Title | 干什么 / What It Does |
|---|---|---|
| 主窗口 / Main | `🐟 FatFish Runtime v1.0.4` | 你打字聊天的地方 / where you type and chat |
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
你双击 fatfish1.0.4.bat
        │
        │  ① 检查 python 命令在不在 PATH
        │  ② 有 venv / .venv 就激活
        │  ③ 试 import 依赖，缺了就装
        │  ④ 没有 .env 就生成模板并要求填 key
        │  ⑤ start "" cmd /k "fatfish_runtime.bat"   ← 开新窗口
        ▼
fatfish_runtime.bat（新窗口，标题 🐟 FatFish Runtime v1.0.4）
        │
        │  ① chcp 65001（UTF-8 代码页）
        │  ② call fatfish_lang.bat   ← 探测系统语言 → FISH_LANG=zh / en
        │  ③ 把窗口标题临时改成随机串，再用 PowerShell 按标题反查自己的 PID
        │     写入 _fatfish_pid.txt
        │  ④ 前台运行 python launch.py
        ▼
launch.py
        │
        │  ① subprocess.Popen([python, FATGFISH.py])  ← 拿到【真实 PID】
        │  ② subprocess.Popen([python, fatfish_watcher.py, <真实PID>],
        │                     creationflags=CREATE_NEW_CONSOLE)  ← 独立黑窗
        │  ③ main_proc.wait()  ← 等主程序结束
        │  ④ 再等监控器最多 40 秒收尾，超时就 kill
        ▼
   ┌────────────────────────┐        ┌─────────────────────────────┐
   │  FATGFISH.py（前台）    │        │  fatfish_watcher.py（独立窗）│
   │  聊天主循环 / REPL      │        │  ① tail logs/**/exec_*.out   │
   │  工具调用 / 联网 / 存码 │        │  ② 每 1 秒查一次主程序还活着吗│
   └────────────────────────┘        │  ③ 主程序消失 → 再 drain 3 轮 │
                                     │  ④ 30 秒倒计时后自动退出      │
                                     │     （按任意键可提前退）      │
                                     └─────────────────────────────┘
```

### 为什么要搞得这么绕？ / Why So Convoluted?

| 设计 / Design | 原因 / Reason |
|---|---|
| 用 `launch.py` 而不是纯 bat 启动 | **纯 bat 拿不到子进程的真实 PID**（`%errorlevel%` 只是退出码）。Python 的 `subprocess.Popen` 能直接拿到 `pid`，还能避开 bat 里多层引号嵌套导致的「闪退」/ bat cannot get a child PID; Python can |
| 监控器用 `CREATE_NEW_CONSOLE` | 让它有**自己独立的黑窗口**，输出不跟主窗口打架 / gives it its own window |
| 主程序不用 `CREATE_NEW_CONSOLE` | 主程序要**继承当前控制台**，否则它的输入输出会跑到别的窗口去 / main inherits the console |
| `fatfish_runtime.bat` 反查自己 PID | 写进 `_fatfish_pid.txt`，方便**外部工具识别/关闭肥鱼窗口** / for external tools |

> ⚠️ **注意两个 PID 不是同一个东西：**
> - `_fatfish_pid.txt` 里存的是 **runtime 那个 cmd 窗口的 PID**（由 `fatfish_runtime.bat` 用窗口标题反查写入）
> - 监控器盯的是 **`FATGFISH.py` 进程的 PID**（由 `launch.py` 通过 `Popen` 拿到并传参）
>
> 这俩由不同环节产生、用途不同，别搞混。

---

## 四、文件都是干啥的？ / File Overview

### 4.1 一级文件（16 个）/ Top-level files (16)

| 文件 / File | 大小 / Size | 行数 / Lines | 干啥的 / Purpose |
|---|---|---|---|
| `FATPACKI.bat` | 约 270 KB | 276 | ✅ **推荐的一键安装器**（I 版，内嵌 12 个最新文件）/ the recommended installer |
| `FATPACK.bat` | 270.1 KB | 276 | ✅ **一键安装器**（与 `FATPACKI.bat` 内容等价）/ one-click installer |
| `make_fatpack.py` | 15.4 KB | 420 | **安装器生成器**（文件更新后重跑它即可重新打包）/ installer generator |
| `fatfish1.0.4.bat` | 6.5 KB | 106 | **启动器**（先查环境再开新窗口）/ launcher |
| `fatfish_runtime.bat` | 3.2 KB | 74 | **运行窗口**（探语言、写 PID、跑 launch.py）/ runtime window |
| `fatfish_lang.bat` | 3.9 KB | 90 | **语言探测器**（四级降级 → `FISH_LANG`）/ language probe |
| `launch.py` | 4.5 KB | 124 | **启动枢纽**（拿 PID、拉监控器、等收尾）/ launch hub |
| `FATGFISH.py` | 49.5 KB | **1125** | **主程序**（聊天 REPL + 工具循环 + 轮次计时 + 一键放行）/ main program |
| `fatfish_watcher.py` | 11.5 KB | 347 | **监控器**（tail 子程序输出 + 存活检测）/ watcher |
| `workspace.py` | 24.9 KB | 675 | **工作台**（路径安全、读过凭证、备份、14 个工具）/ workspace |
| `file_tools.py` | 13.4 KB | 388 | **读文件/目录/图片**、路径解析、多模态组装 / file & image IO |
| `net_tools.py` | 7.2 KB | 205 | **联网**（Tavily search / extract / auto）/ web tools |
| `exec_tools.py` | 10.6 KB | 340 | **跑命令 / 跑 Python**，输出落盘 / exec engine |
| `README.md` | 约 60 KB | 约 1113 | 就是本文件（会随文档更新变动）/ this file |
| `.env` | 127 B | 2 | 你的密钥配置 | 🔴 **绝不要分享/上传** / your keys |
| `.gitignore` | 114 B | 11 | 防误传名单（第一行 `.env`）/ ignore list |

> 📌 `_fatfish_pid.txt` 也是运行时产物（runtime 窗口 PID，退出时自动删除），
> 但它只在程序运行时短暂存在，故不计入上表。

### 4.2 一级目录（6 个）/ Top-level dirs (6)

| 目录 / Dir | 干啥的 / Purpose | 会被 git 忽略吗 |
|---|---|---|
| `workspace/` | **工作台**：肥鱼唯一能自由读写的区域 / the sandbox | ❌ 不忽略 |
| `logs/年/月/日/` | 聊天日志、执行输出、监控器日志 / logs | ✅ 忽略 |
| `generated_code/年/月/日/` | 肥鱼自动存下的代码 / saved code | ✅ 忽略 |
| `_backup/` | 根一级文件被改前的**自动备份** / auto backups | ❌ 不忽略 |
| `.fatfish_tmp/` | 跑 Python 时的临时脚本 / temp scripts | ❌ 不忽略 |
| `__pycache__/` | Python 字节码缓存 / bytecode cache | ✅ 忽略（`*.pyc`）|

### 4.3 关于 `.hex` 转写文件（2026-09-14 已移除）/ On the `.hex` Files (Removed)

**现状：工作台里已经没有 `.hex` 文件了。**

早先曾有 3 个 —— `fatfish1.0.4.hex` / `fatfish_runtime.hex` / `fatfish_lang.hex`，
分别是三个 `.bat` 的**逐字节十六进制转写**（每行 16 字节、第 8 字节后加一个空格分栏、
小写、CRLF 结尾；`40 65 63 68 6f` 就是 `@echo`）。

**为什么删掉** —— 实测核实它们**不参与任何流程**：

| 核查 / Check | 结果 / Result |
|---|---|
| 全项目搜 `.hex` | 39 处命中，**没有一处是「读取」**（唯一的代码动作是 `open(..., "wb")` 写出）|
| 安装器内嵌清单 | **不含** `.hex` → 完全不参与安装 |
| 信息量 | **100% 冗余** —— 每个字节在对应的 `.bat` 里都有 |

**为什么"当保真备份用"站不住脚** —— 没有任何程序会用 `.hex` 还原 `.bat`，
也没有文档写过还原方法，等于「一个没有配钥匙的备用锁」。
（还原逻辑本身只要一行 `binascii.unhexlify(去空白(内容))`，但既然没人会执行它，
留着就只是占地方。）

**2026-09-14 做的三件事**：

1. 删除 3 个 `.hex`（共 43 KB）
2. 从 `make_fatpack.py` 里移除生成它们的代码（`HEX_FROM` 常量、`hexdump()` 函数、
   载荷里的生成循环）—— 今后重打包**不会**再产出 `.hex`
3. 从本文件和两个安装器里清掉相关描述

> 💡 如果你哪天又想要这种「转写副本」：格式要点就在上文（16 字节/行、8+8 分栏、
> 小写、CRLF），还原只要 `binascii.unhexlify`。旧文件本身已随 2026-09-14 的
> 工作台归档移出，见 `E:\FATFISH\oldver\workspace_cleanup_*`。

---

## 五、怎么跟肥鱼说话？ / How to Talk to FatFish

启动后会出现提示符，直接打字回车就行 / Once started, just type and hit Enter:

```
🌐~🔍 你 [You] ▸ 你好，介绍一下你自己
```

提示符还会告诉你当前状态 / The prompt shows your current state:

```
🌐~🔍 你 ▸
│ │  └─ 联网模式图标：🔍 搜索 / 📄 抓正文 / 🎯 自动
│ └──── Tavily 模式图标
└────── 联网开关：🌐+ 强制开 / 🌐- 关 / 🌐~ 自动
```

### 5.1 命令速查 / Command Reference

| 你输入 / You Type | 肥鱼会干啥 / What FatFish Does |
|---|---|
| `@C:\某个文件.txt` | 读取这个文件 / read this file |
| `/read 路径...` | 读一个或多个文件（一层）/ read files/dirs, one level |
| `/file 路径...` | 同 `/read` / same as `/read` |
| `/open 路径...` | 同 `/read` / same as `/read` |
| `/readr 文件夹` | **递归**读整个目录树 / recursively read whole tree |
| `/net on` | 打开联网（每次都搜）/ always search |
| `/net off` | 关闭联网 / never search |
| `/net auto` | 智能判断（默认）/ smart auto (default) |
| `/net` | 查看当前模式 / show mode |
| `/tavily search` | 只做关键词搜索 / search only |
| `/tavily extract` | 只抓 URL 正文 / extract URLs only |
| `/tavily auto` | 有 URL 就抓正文，否则搜索 / auto-pick |
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
| `/clear` | 清空聊天记录 / clear chat history |
| `/reload` | 重新读 `.env` / reload `.env` |
| `/help` | 显示帮助 / show help |
| `exit` / `quit` / `退出` | 退出肥鱼 / quit |

> 路径里有空格，用双引号包起来：`/read "C:\我的 文件夹\a.py"`
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
- **会写进日志**：每轮耗时同时写入当天聊天日志（`本轮耗时 12.43s`）。
- **可关闭**：`/timer off` 之后完全静默（但内部时间戳照常更新，`/timer on` 立刻恢复）。
- **行内紧凑**：只报两个数字 —— `本轮 12.4s` 和 `停留 8.2s`，不占屏、不打断阅读。

时间格式规则 / Formatting rules:

| 时长 / Duration | 显示 / Shown |
|---|---|
| < 60 秒 | `12.4s` |
| < 1 小时 | `1m23.4s` |
| ≥ 1 小时 | `1h02m` |

> 💡 实现上它只用了标准库 `time.time()`（单次约几十纳秒），对性能没有可感知影响。
> 源码位置：`FATGFISH.py` 的 `# ---- 计时器（本轮耗时 / 你停留了多久）----` 区块。

### 5.4 一键放行：本轮别再一句句问我了 / One-Key Auto-Approve

读写操作默认要你逐个批次点头（按 `y`）。当一个任务里肥鱼要连着写十几个文件时，
这个体验就很碎。**一键放行**解决的就是这个。

**怎么用**：报批弹窗时按 `a` 或 `1`。

| 按键 / Key | 效果 / Effect |
|---|---|
| `y` / `yes` / `是` / `批准` / `同意` | 只批准**这一批**（和以前一样）|
| **`a` / `1` / `all`** | 批准这一批，**并且本轮剩余的全部读写操作都不再询问** |
| `n` / 其他 / EOF | 拒绝 |

**实际长这样 / What it looks like:**

```text
  🔐 即将执行以下读写操作，需你批准：
     1. 写入/覆盖 [write]  README.md  （1234 字符）「# 🐟…」
     2. 删除 [delete]  tmp.txt
     y = 只批这一批 ｜ a 或 1 = 批准并放行本轮剩余全部 ｜ n = 拒绝
  👉 是否批准 [Approve?] [y/a/1/N] a
  🔓 已批准，并且本轮剩余读写操作全部自动放行（直到你下一条命令）
  ✅ 工作台 ws_write → 已写入 README.md（1234 字符）
  ✅ 工作台 ws_delete → 已删除 tmp.txt

  🔓 本轮自动放行 1 个操作（无需再确认）      ← 同一轮内后面这些都不再问
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
  🔐 即将执行以下读写操作，需你批准：…                     ← 又弹窗了
```

所以它**只在"这一轮"有效**，你一发下一条命令就自动收回 —— 不会出现"忘了关"而长期裸奔的情况。

**三个开关 / Three Switches:**

| 常量 / Command | 默认 | 作用 |
|---|---|---|
| `AUTO_APPROVE_ENABLED` | **`True`** | 功能总开关。关掉后 `a` / `1` 失效（只能 `y` 逐个批）|
| `AUTO_APPROVE_DEFAULT` | `False` | 改成 `True` 则**每轮一开始就处于放行态** —— 等于完全不弹窗 |
| `/auto now` | — | 不想等报批出现，直接放行本轮剩余 |

`/auto` 的状态输出长这样：

```text
  🔐 一键放行：ON ｜ 本轮：已放行 ｜ 每轮默认：需报批
     /auto on | /auto off 开关功能 ｜ /auto now 立刻放行本轮
```

**🛡️ 安全边界：放行只跳过"问你一句"**

这是设计上最要紧的一点 —— 一键放行**不放松任何其它防线**：

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

## 六、工作台（沙盒）机制 / The Workspace (Sandbox)

**工作台 = 肥鱼能自由读写的那个文件夹**，默认是程序目录下的 `workspace/`。
**Workspace = the folder FatFish can freely read/write**, defaults to `./workspace`.

### 6.1 三条安全设计 / Three Safety Designs

| 设计 / Design | 行为 / Behavior |
|---|---|
| 🔒 **路径越界拦截** | 所有路径都会被解析进工作台内，越界直接报错 `路径越界` / all paths are resolved inside the workspace |
| 🔒 **移出作业区要批准** | `/ws cd` 到默认作业区外面时**不执行**，先登记「待批准」；要调用 `ws_cd_approve` 你明确同意后才放行 / leaving the default area requires approval |
| 🔒 **读写操作批量报批** | `ws_read` / `ws_write` / `ws_append` / `ws_replace` / `ws_delete` 这 5 个工具在执行前会**汇总成一批问你一次**，按 `y` 才动；拒绝后肥鱼不会重试。想省事可按 `a`/`1`**一键放行本轮**（见 5.4）/ 5 tools ask once per batch; press `a`/`1` to auto-approve the rest of this round |

### 6.2 自动备份规则 / Auto-Backup Rule

「**根一级文件**」（直接躺在工作台根下、不在任何子目录里的文件）在被
**覆盖 / 追加 / 替换 / 删除**之前，会自动把原文件完整拷一份到 `_backup/`：

```
_backup/
  README.md.20260914_214700_123456.bak
  FATGFISH.py.20260914_210355_786288.bak
  ...
```

命名规则：`原文件名.年月日_时分秒_微秒.bak`
备份失败时整个操作会**中止**（宁可不改，也不丢原文件）。

> 子目录里的文件不受此规则约束 —— 所以根一级文件的「一改就留档」是刻意的重点保护。

当前 `_backup/` 的累积情况 / current accumulation:

> ⚠️ 这节**不写死数字**，因为**每改一次根一级文件就多一份全量副本**，数据秒级变化。
> 参考：2026-09-14 迭代最猛时曾涨到 **124 份 / 4.31 MB**，之后按「每文件保留最近 3 份」
> 整理过一次，并把全部备份连同工作台的非程序文件一起归档到了 `E:\FATFISH\oldver\`。
>
> 想看当前实况：直接 `dir _backup`，或在肥鱼里 `/ws ls _backup`。

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

| 内容 / Content | 位置 / Location | 文件名模式 |
|---|---|---|
| 聊天日志 / chat log | `logs/年/月/日/` | `chat_HHMMSS.log` |
| 命令/代码执行输出 / exec output | `logs/年/月/日/` | `exec_HHMMSS_pid_run.out` |
| 监控器日志 / watcher log | `logs/年/月/日/` | `watcher_HHMMSS.log` |
| 自动存的代码 / saved code | `generated_code/年/月/日/` | `<AI起的名字>.<ext>` |
| 跑 Python 的临时脚本 | `.fatfish_tmp/` | `<你指定的名字>.py`（跑完自动删） |

当前累积 / current: `logs/` 38 个文件、`generated_code/` 8 个文件。

---

## 八、安装器是怎么「自解压」的？ / How the Installers Self-Extract

这是最有趣的一块。安装器 = **脚本 + 内嵌压缩包**，同一个文件。

现在目录里有**两个**安装器，机制相同、内嵌内容完全一致：

| 安装器 / Installer | 内嵌内容 / Payload | 状态 / Status |
|---|---|---|
| `FATPACKI.bat` | **12 个文件，全是最新**（约 20 万字节）| ✅ 可用 / available |
| `FATPACK.bat` | **同上，完全等价**（只是解包清单会印自己的名字）| ✅ 可用（2026-09-14 重装）/ available, re-packed |
| ~~（旧版 `FATPACK.bat`）~~ | ~~8 个文件，其中 4 个已过期、且缺 4 个启动链必需文件~~ | 📦 已留档，见 `E:\FATFISH\oldver\workspace_cleanup_*\` 的压缩包内 `_backup/FATPACK.bat.legacy_*.bak` |

> 两个安装器**内嵌清单完全一致**，随便用哪个都行；名字里带 `I` 的那个是重做版的原名。
> 旧版（155.6 KB / 8 个过期文件）已随 2026-09-14 的工作台归档移出，仅作考古用。
>
> 想重新打包？文件更新后跑一句 `python make_fatpack.py` 就行（见 8.4）。

### 8.1 结构 / Structure

以**旧版** `FATPACK.bat`（155.6 KB / 165 行）为例，看清「脚本 + 压缩包二合一」的骨架：

```
FATPACK.bat（155.6 KB，165 行）
├── 第 1~145 行   可读的批处理安装逻辑（6 个步骤）
├── 第 148 行     ##PYBEGIN##          ← 分隔标记
└── 第 150~164 行 一段 Python 解包脚本（FILES 字典 + 循环写出）
       ↑ 其中 7 行是"巨型行"，每行塞了一个文件的 base64
         最长那行 48,130 字符（= 那时的 FATGFISH.py）
         这 7 行占了全文 97% 的体积
```

**2026-09-14 重装后**的 `FATPACK.bat`（以及 `FATPACKI.bat`）长得一样，只是内容升级了：

```
FATPACK.bat / FATPACKI.bat（270.1 KB，276 行）
├── 第 1~181 行   批处理外壳（7 个步骤，纯 ASCII 英文）
├── 第 182 行     ##PYBEGIN##          ← 分隔标记（全文件只出现 1 次）
└── 第 184~269 行 解包脚本（FILES 字典 12 个条目 + 循环写出）
       ↑ 13 行"巨型行"，最长 65,356 字符（= README.md 的 base64）
```

### 8.2 解包原理 / The Trick

批处理里这三行的配合是精髓：

```bat
set "MK=##PY"           ← 分两段拼出标记，避免自己匹配到自己
set "MK=!MK!BEGIN##"
powershell ... $c.LastIndexOf('%MK%') ... $c.Substring($i + 12) | Set-Content %EXTRACT_PY%
python "%EXTRACT_PY%"   ← 跑解包脚本
```

1. 用 `LastIndexOf` 找**最后一个** `##PYBEGIN##`（防止正文里出现同名标记时找错）
2. 取标记之后的全部内容，写成临时 `.py`
3. 跑它 → 临时脚本 base64 解码，把 12 个文件写到磁盘（旧版是 8 个）
4. 删掉临时脚本

> ⚠️ 注意上面那行 `$i + 12` 是**旧版**写法。`##PYBEGIN##` 实际只有 11 个字符，
> `+12` 等于顺手吃掉了标记后的第一个换行符 —— **恰好无害**。
> 新生成的安装器用 `+11`（精确值，且由生成器算出来，不硬编码）。

标记被拆成 `##PY` + `BEGIN##` 两段拼接，是为了**让这行代码本身不包含完整标记**，
从而不会污染 `LastIndexOf` 的搜索结果。

### 8.3 内嵌清单与实测校验 / Embedded Files — Verified

我用 SHA1 逐个比对了解出的内容和磁盘现场：

| 内嵌文件 | 解出大小 | 磁盘大小 | 状态 |
|---|---|---|---|
| `.gitignore` | 114 B | 114 B | ✅ 一致 |
| `README.md` | 23,352 B | 23,352 B | ✅ 一致 |
| `file_tools.py` | 13,700 B | 13,700 B | ✅ 一致 |
| `net_tools.py` | 7,414 B | 7,414 B | ✅ 一致 |
| `FATGFISH.py` | 36,080 B | 42,313 B | ❌ **已过期**（少了 6 KB） |
| `exec_tools.py` | 6,812 B | 10,888 B | ❌ **已过期** |
| `workspace.py` | 25,384 B | 25,530 B | ❌ **已过期** |
| `fatfish1.0.4.bat` | 2,454 B | 6,704 B | ❌ **已过期** |

**一致 4 个 / 过期 4 个。**

> 这就是 `FATPACKI.bat` 存在的理由：原版装出来的是一套「半旧不新」的程序，
> 而且缺 `fatfish_runtime.bat` / `fatfish_lang.bat` / `launch.py` / `fatfish_watcher.py`，
> 全新机器上跑完连启动器都点不开（详见第十二章「问题 1 / 问题 2」）。

### 8.4 FATPACKI.bat（I 版）改了哪些 / What FATPACKI.bat Fixes

| 改动 / Change | 说明 / Notes |
|---|---|
| ✅ **内嵌 12 个最新文件** | 覆盖运行全部必需件：7 个 py + 3 个 bat + `README.md` + `.gitignore`（合计约 20 万字节）|
| ✅ **补上缺失的 4 个文件** | `fatfish_runtime.bat` / `fatfish_lang.bat` / `launch.py` / `fatfish_watcher.py` |
| ✅ **覆盖前自动备份** | 已存在的文件先拷进 `_backup/<名字>.<时间戳>.bak`，不静默吃掉旧版本 |
| ✅ **自检增强了** | 解包后先确认 `FATGFISH.py` 真的落地了，再往下走 |
| ✅ **可选立即启动** | 装完问一句「现在就启动吗」，输 `y` 直接开新窗口 |
| ✅ **静默开关** | `FATFISH_SKIP_VENV=1` / `FATFISH_NO_GUI=1`，方便自动化与沙箱测试 |
| ✅ **生成器自检** | bat 外壳若混进非 ASCII 字符会**直接报错停手**；标记出现次数必须为 1 |
| ⚠️ **外壳改为纯 ASCII 英文** | 见下面的「三个坑」—— 这是为了让 cmd 不在中文批处理上翻车 |

#### 🕳️ 开发 I 版时踩的三个坑 / Three Pitfalls (Worth Knowing)

**坑 1：`\f` 被 Python 吃成换页符**
生成器模板原本是普通三引号字符串，于是 `%TEMP%\fatfish_extract_...` 里的 `\f`
被解释成 **0x0C 换页字符**，路径实际变成 `C:\Temp␌atfish_extract_...`，
PowerShell 直接报 `Illegal characters in path`，安装在第 2 步就断了。
→ **修法**：模板一律写成原始字符串 `r'''...'''`。

**坑 2：cmd 在 UTF-8 中文批处理里会「字节偏移错位」**
原以为 bat 里放中文没问题，实测报错：

```text
'显式指定' is not recognized as an internal or external command
'�新获取一份完整的' is not recognized as an internal or external command
```

cmd 解析批处理时按**字节偏移**续读文件；文件含多字节 UTF-8 字符时偏移会漂移，
于是它**从一行的中间开始读**，把半截 `REM` 注释当成命令去执行
（那个 `�` 就是被劈开的半个汉字）。
→ **修法**：**bat 外壳一律纯 ASCII 英文**；中文只放在**载荷脚本**里 ——
载荷由 PowerShell 以 `-Encoding UTF8` 显式读写，完全不经 cmd 解析，因此安全。
这就是为什么 `FATPACKI.bat` 的提示全是英文，而解包后打印的清单仍是中文。

**坑 3：`##PYBEGIN##` 是 11 个字符，不是 12**
原版写的是 `$i + 12`，比标记实际长度多 1 —— 等于顺手吃掉了标记后的第一个换行符，
**恰好无害**（载荷照样能跑）。I 版改成按实际长度注入（11），是精确值；
而且这个长度是**算出来的**，不是硬编码，标记改名也不会错位。

#### 🔁 重新打包 / Re-packaging

以后任何程序文件更新了（比如你又改了 `FATGFISH.py`），只要重跑一次生成器：

```bash
python make_fatpack.py                  # 生成 FATPACKI.bat
python make_fatpack.py FATPACK.bat      # 想覆盖原版名也行
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
| 沙箱产物可运行性 | 沙箱里的 `FATGFISH.py` 编译通过 ✅ |

---

## 九、所有「数值上限」一览 / All Tuning Constants

想调参数就改这些（都在源码顶部，注释里还标了历史变更）：

### 9.1 主程序 `FATGFISH.py`

| 常量 | 值 | 含义 / Meaning | 注释里的历史 / History |
|---|---|---|---|
| `MODEL` | `deepseek-flash` | 调用的模型名 / model name | — |
| `BASE_URL` | `https://api.deepseek.com` | API 地址 | — |
| `MAX_HISTORY` | **400** | 保留的历史消息条数 | 20 → 200 → 1000 → 400 |
| `MAX_HISTORY_TOKENS` | **800,000** | 历史部分的 token 预算 | 60k → 512k → 2M → 800k |
| `TRIM_KEEP_FIRST_USER` | `True` | 钉住最早一条 user（任务目标） | — |
| `TRIM_TOOL_CLIP_CHARS` | **40,000** | 单条 tool 结果超长则中间截断 | 4k → 60k → 200k → 40k |
| `MAX_REPLY_TOKENS` | **131,072** | 单次回复上限 | 16k → 32k → 64k → 131k |
| `MAX_TOOL_ROUNDS` | **512** | 单轮对话内工具循环上限 | 16 → 128 → 512 |
| `API_TIMEOUT` | **900 s** | 单次 API 请求超时 | 60 → 300 → 900 |
| `SHOW_TIMER` | `True` | 是否显示轮次计时（`/timer on/off`） | 新增于 rev.2.1 |
| `AUTO_APPROVE_ENABLED` | **`True`** | 「一键放行」总开关（报批时按 `a`/`1`）| 新增于 rev.2.3 |
| `AUTO_APPROVE_DEFAULT` | `False` | 改成 `True` = 每轮默认已放行（完全不弹窗）| 新增于 rev.2.3 |
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
| `TAVILY_MODE` | `search` | 默认 Tavily 模式 |
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
| `EXEC_OUTPUT_ROOT` | `logs` | 子程序输出落盘根目录 |

### 9.6 监控器 `fatfish_watcher.py`

| 常量 | 值 | 含义 |
|---|---|---|
| `DEFAULT_INTERVAL` | 1.0 s | 轮询间隔（下限 0.3 s）|
| `PS_TIMEOUT` | 30 s | PowerShell 存活检测超时 |
| `DRAIN_ROUNDS` / `DRAIN_INTERVAL` | 3 轮 / 1 s | 主程序关闭后再 tail 几轮 |
| `MISSING_TOLERANCE` | **3** | 连续 3 次查不到才算"彻底关闭"（防误判）|
| 收尾倒计时 | **30 s** | 按键可提前退出 |

---

## 十、跑命令 / 跑 Python 详解 / Running Commands & Code

### 10.1 它是怎么跑起来的 / How Execution Works

```
ws_run_cmd("dir")  /  ws_run_python(code)
        │
        ├─ 1) cwd 越界检查：只允许工作台内的子目录，越界直接拒绝
        ├─ 2) 分配落盘文件 logs/年/月/日/exec_HHMMSS_<pid>_run.out
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
第四级：注册表 HKCU:\Control Panel\International → LocaleName    ← 最后兜底
        │
        └─ 结果里含 "zh" → FISH_LANG=zh；其他/失败 → FISH_LANG=en
```

两个刻意为之的设计 / Two deliberate choices:

1. **`fatfish_lang.bat` 的行尾注释全是纯 ASCII** —— 因为子脚本不能假设调用方已经设好了
   `chcp 65001`，否则它自己的中文注释在 GBK 控制台下会被**当成命令解析**。
2. **非中文一律回退纯英文** —— 不搞半吊子翻译（原文注释：`No half-baked translations`）。

而 `fatfish1.0.4.bat` 的提示信息则统一用 `中文 | English | 日本語 | 한국어` 四语并列。

---

## 十二、⚠️ 已知问题与待办 / Known Issues & TODOs

以下都是我这次**实测发现的**，不是猜测：

### 🟢 问题 1（已解决）：安装器曾内嵌过期文件

**原状**：`FATPACK.bat` 里塞的 `FATGFISH.py`（36,080 B vs 当时现行 42,313 B）、
`exec_tools.py`（6,812 vs 10,888）、`workspace.py`（25,384 vs 25,530）、
`fatfish1.0.4.bat`（2,454 vs 6,704）**4 个文件都是旧版本**，
装出来是一套「半旧不新」的肥鱼。

✅ **现已修复**：
1. `python make_fatpack.py FATPACKI.bat` 做出了内嵌 12 个最新文件的 I 版；
2. 2026-09-14 又用 `python make_fatpack.py FATPACK.bat` **把原版名也重装了一遍**（旧版留档）。
现在两个安装器都逐字节校验通过（见 8.4）。

### 🟢 问题 2（已解决）：安装器曾漏掉启动链关键文件

**原状**：旧版内嵌清单只有 8 个文件，**缺了 4 个必需文件**：

| 缺失文件 | 后果 |
|---|---|
| `fatfish_runtime.bat` | `fatfish1.0.4.bat` 里 `start cmd /k "fatfish_runtime.bat"` **找不到文件** |
| `fatfish_lang.bat` | runtime 里 `call fatfish_lang.bat` 失败 |
| `launch.py` | runtime 里 `python launch.py` 失败 |
| `fatfish_watcher.py` | 没有监控器窗口 |

**即：用旧版全新安装后，双击 `fatfish1.0.4.bat` 会直接闪退或报错。**

✅ **现已修复**：两个安装器都内嵌 12 个文件，并在沙箱里各跑通了「全新安装 → 覆盖更新」两遍完整流程（见 8.4）。

### 🟡 问题 3：主程序文件名拼写不一致

主程序叫 `FATGFISH.py`（**多了一个 G**），而其他所有文件都是 `fatfish*`。
`__pycache__/` 里也忠实同步成了 `FATGFISH.cpython-38.pyc`。

**建议**：要么改名统一，要么保留现名但在 README/文档里明确标注（本版文档已标注）。
直接改名的风险：`launch.py` 里写死了 `MAIN_SCRIPT = os.path.join(HERE, "FATGFISH.py")`，
`FATPACK.bat` 的载荷字典里也是 `'FATGFISH.py'`，要一起改。

### 🟡 问题 4：模型名可能不是可用的 API 名

`MODEL = "deepseek-flash"`。DeepSeek 官方 API 常见的是 `deepseek-chat` /
`deepseek-reasoner` 这类名字。如果聊天报「模型不存在」，先来这里改。

### 🟢 提醒 1：`.env` 是明文密钥

`DEEPSEEK_API_KEY` / `TAVILY_API_KEY` 明文躺在磁盘上。
好在 `.gitignore` 第一行就是 `.env`，**不会**被 git 带走 —— 但这个防护仅对 git 有效。

### 🟢 提醒 2：备份会膨胀

`_backup/` 的规则是「根一级文件**每次被改前都全量留档**」，所以只要频繁改
`README.md` / `FATGFISH.py` 这类文件，备份就会快速堆积（2026-09-14 一晚就涨到 124 份 / 4.31 MB）。
**建议偶尔按「每文件保留最近几份」清一次** —— 清理方法很简单，就是在工作台里删掉过期的 `.bak`：
文件名里带时间戳（`原文件名.年月日_时分秒_微秒.bak`），按时间排序留新的即可。

### 🟢 提醒 3：当前没有 venv

`FATPACK.bat` 会创建 `venv/`，但当前目录下 `venv/` 和 `.venv/` **都不存在**
（说明是全局 Python 环境在跑，或者 venv 被清理过）。
`fatfish1.0.4.bat` 对此是容错的：找不到 venv 就用全局 Python。

### 🟢 提醒 4：`_fatfish_pid.txt` 是残留

它记的是 runtime 窗口的 PID。该窗口退出时 `fatfish_runtime.bat` 会自动删除它；
如果是**非正常退出**（被强杀），它会留下来，属于正常残留。

---

## 十三、出问题了怎么办？（小白急救包）/ Troubleshooting

| 现象 / Symptom | 原因 & 解决 / Cause & Fix |
|---|---|
| **双击 bat 一闪就没了** | 没装 Python 或没加 PATH。重装 Python 并勾 `Add Python to PATH` / Python missing or not in PATH |
| **提示 `python 不是内部或外部命令`** | Python 没加进 PATH，重装时勾选那个选项 / reinstall and check PATH |
| **提示「提取内嵌数据失败，安装器可能已损坏」** | 安装器的 `##PYBEGIN##` 标记或载荷被破坏（比如用编辑器保存过、被截断）。重新拿一份完整的安装器，或本地重跑 `python make_fatpack.py` / installer payload corrupted |
| **`start cmd /k fatfish_runtime.bat` 报找不到文件** | 命中「问题 2」：安装器没内嵌 runtime。手动把 4 个缺失文件补上，或从完整目录复制过来 / see Known Issue 2 |
| **能启动但一聊天就报错** | `.env` 里 `DEEPSEEK_API_KEY` 没填对，检查有没有多余空格 / key wrong or has spaces |
| **报「模型不存在」** | 改 `FATGFISH.py` 里的 `MODEL`（见问题 4）/ change `MODEL` |
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
DEEPSEEK_API_KEY=sk-你复制的那一长串字符
```

### 🅱️ Tavily API Key（选填，负责"联网搜索"）

**不填也能用肥鱼**，只是不能联网，遇到"最新"类问题会答不上来。

1. **打开官网**：<https://tavily.com/>
2. **注册账号**：点右上角「Sign Up」，可用 Microsoft 账号一键登录，也可邮箱注册
3. **领取免费额度**：注册后送每月免费搜索次数（个人用基本够）
4. **获取 API Key**：登录后进「Dashboard」→ 找「API Keys」→ 复制 `tvly-` 开头那串
5. **同样立刻保存**

```
TAVILY_API_KEY=tvly-你复制的那串字符
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

## 十五、极简速查（懒人版）/ Quick Reference (Lazy Edition)

```
1. 装 Python（勾 Add to PATH）
2. 双击 FATPACK.bat
3. 去 platform.deepseek.com 申请 DeepSeek Key（sk- 开头）
   （想联网就再去 tavily.com 申请 Tavily Key，tvly- 开头）
4. 把 Key 填进自动打开的 .env 文件（= 两边不留空格）
5. 双击 fatfish1.0.4.bat
6. 会开两个窗口：主窗口聊天，监控窗口看子程序输出
7. 开始打字聊天 🐟
```

**就这几步，猪都会了。祝你玩得开心！** 🎉
**That's it — even a pig can do it. Have fun!** 🎉

---

## 附录 A：一次典型会话长什么样 / Appendix A: A Typical Session

```text
🐟 DeepSeek 联网肥鱼 G 版 v1.0.4 已启动            ← 渐变彩色横幅
  📂 本次日志目录：logs\2026\09\14
  📂 本次代码目录：generated_code\2026\09\14
  🌐 联网模式：AUTO（用 /net 切换）
  🔍 Tavily 模式：SEARCH（用 /tavily 切换）
  🛠️  工作台目录：E:\FATFISH\workspace（用 /ws cd 切换）
  ⏱️ 计时：ON（/timer 可切换）
  🔐 一键放行 [auto-approve]：ON（报批时按 a / 1，本轮剩余全部放行；用 /auto 管理）

🌐~🎯 你 ▸ 列出工作台里的文件

  🔐 即将执行以下读写操作，需你批准：
     1. 列出目录 [list]  ws_list  (depth=3)
     y = 只批这一批 ｜ a 或 1 = 批准并放行本轮剩余全部 ｜ n = 拒绝
  👉 是否批准 [Approve?] [y/a/1/N] a
  🔓 已批准，并且本轮剩余读写操作全部自动放行（直到你下一条命令）
  ✅ 工作台 ws_list → workspace/
  🤖 AI（蓝色冷静配色）
  ...
  ⏱️ 本轮 12.4s

🌐~🎯 你 ▸ （你盯着屏幕想了 8 秒才继续打字）
  ⏱️ 停留 8.2s
```

## 附录 B：目录结构总览 / Appendix B: Directory Tree

```text
FatFish/
├── FATPACKI.bat             ← ✅ 安装器（内嵌 12 个最新文件）
├── FATPACK.bat              ← ✅ 同上，内容等价
├── make_fatpack.py          ← 安装器生成器（文件更新后重跑它）
├── fatfish1.0.4.bat         ← 启动器（双击这个）
├── fatfish_runtime.bat      ← 运行窗口（探语言 / 写 PID / 跑 launch）
├── fatfish_lang.bat         ← 语言探测（四级降级）
├── launch.py                ← 启动枢纽（拿 PID / 拉监控器）
├── FATGFISH.py              ← 主程序（约 1125 行）
├── fatfish_watcher.py       ← 监控器（tail 子程序输出）
├── workspace.py             ← 工作台 / 14 个工具实现
├── file_tools.py            ← 文件·目录·图片读取
├── net_tools.py             ← Tavily 联网
├── exec_tools.py            ← 跑命令 / 跑 Python
├── README.md                ← 本文件
├── .env                     ← 你的密钥（🔴 别外传）
├── .gitignore               ← 防误传名单
├── _fatfish_pid.txt         ← runtime 窗口 PID（运行时生成，退出即删）
├── logs/YYYY/MM/DD/         ← chat_*.log / exec_*.out / watcher_*.log
├── _backup/                 ← 根一级文件的自动备份
├── workspace/               ← 🏠 工作台沙盒（**首次运行自动创建**）
├── generated_code/          ← 肥鱼自动存的代码（**首次运行自动创建**）
├── .fatfish_tmp/            ← 临时脚本（**运行时创建，跑完删**）
└── __pycache__/             ← 字节码缓存（**运行时创建**）
```

---

## 附录 C：本版文档的核对结论 / Appendix C: Verification Log

本版 README 的所有架构描述都来自**逐文件通读 + 实测验证**，不是猜测：

| 结论 / Conclusion | 怎么得出的 / How Verified |
|---|---|
| 启动链路四层 | 通读 `fatfish1.0.4.bat` / `fatfish_runtime.bat` / `launch.py` / `fatfish_watcher.py` |
| `.hex` 是纯冗余副本（已删除） | 全项目搜 `.hex`（39 处命中无一为「读」）+ 安装器内嵌清单不含 + `unhexlify` 解出与 `.bat` 逐字节一致 |
| `FATPACK.bat` 重装前内嵌 8 个文件、4 个过期 | 正则抓出 base64 字典 → 解码 → SHA1 比对现场文件 |
| `FATPACK.bat` 重装后内嵌 12 个全一致 | 抽载荷 → 沙箱两遍实跑 → 逐字节 15/15 |
| 两个安装器内容等价 | 比对内嵌清单与主程序 base64 完全相同 |
| 所有常量上限数值 | 逐文件通读源码顶部常量区 |
| 编码/行尾/BOM 状态 | 二进制读入逐文件检测（全部 UTF-8 无 BOM，bat 为 CRLF，`FATPACK.bat` 为 LF）|
| 归档目录结构 | 遍历 `logs/` 与 `generated_code/`，按文件名模式归类统计 |
| `_fatfish_pid.txt` 语义 | 通读 `fatfish_runtime.bat` 的标题反查逻辑 |
| 计时器行为 | 7 个场景单元测试（首轮/停留/斜杠命令/退出/关闭开关/空输入/边界值）+ 真进程管道冒烟测试（实测停留 3.0s / 2.0s，测出即 3.0s / 2.0s）|
| `FATPACKI.bat` 可用性 | 真调 PowerShell 抽载荷 → 沙箱跑两遍（全新安装 / 覆盖更新）→ **逐字节比对通过** |
| 一键放行行为 | 9 个按键单元测试（`y`/`yes`/`Y`/`a`/`1`/`all`/`n`/空/乱输）+ 复位函数 3 场景 + **真进程端到端**（注入假 openai 造两轮工具调用：第 1 轮弹窗按 `a` → 第 2 轮确实免问 → 发新命令确实复位）|

---

> 📌 Key 怎么申请、怎么填、填错了咋办，看 **「十四、API Key 怎么申请？」**
> 📌 真正要跑的核心只有 4 个：`FATPACKI.bat`（装）、
> `fatfish1.0.4.bat`（启）、`.env`（配）、`FATGFISH.py`（本体）。
