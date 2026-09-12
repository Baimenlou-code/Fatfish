# 🐟 肥鱼 FatFish —— 猪都能看懂的说明书
# 🐟 FatFish — A Manual Even a Pig Can Understand

> 一句话：**肥鱼是一个住在黑色命令行窗口里的 AI 助手。**
> 你打字问它，它回答你；它还能帮你读写文件、上网查资料、跑代码。
>
> One line: **FatFish is an AI assistant that lives in a black command-line window.**
> You type, it answers; it can also read/write files, search the web, and run code.

不用懂编程，照着下面做就行 👇
No programming knowledge needed — just follow along 👇

---

## 一、肥鱼能干啥？ / What Can FatFish Do?

| 能力 / Capability | 说人话 / In Plain Words |
|---|---|
| 💬 聊天 / Chat | 跟它说话，它回答你 / Talk to it, it replies |
| 📄 读文件 / Read files | 你给它一个文件路径，它把内容读进去看 / Give it a path, it reads the content |
| ✍️ 写文件 / Write files | 让它帮你新建、修改、保存文件 / Let it create, edit, and save files |
| 🌐 联网搜索 / Web search | 让它上网查最新消息、新闻、价格 / Let it look up news, prices, latest info |
| 💻 跑命令 / Run commands | 让它在你电脑上执行命令、跑 Python 代码 / Let it run commands and Python code on your PC |
| 💾 自动存代码 / Auto-save code | 它写的代码会自动存成文件 / Code it writes is saved to files automatically |

---

## 二、三步跑起来（小白版） / Get Started in 3 Steps (Beginner Edition)

### 第 1 步：装 Python / Step 1: Install Python

1. 打开 <https://www.python.org/downloads/> / Open <https://www.python.org/downloads/>
2. 下载并安装 **Python 3.8 或更高版本** / Download and install **Python 3.8 or newer**
3. ⚠️ **安装时一定要勾选 `Add Python to PATH`**（在安装界面最下面那个小方框）
   ⚠️ **Be sure to check `Add Python to PATH`** (the small checkbox at the bottom of the installer)
4. 装完**重启电脑**（或至少关掉所有命令行窗口）
   **Restart your PC** after installing (or at least close all command-line windows)

> 怎么确认装好了？按 `Win + R`，输入 `cmd` 回车，在黑窗口里打 `python --version`，
> 能显示版本号（比如 `Python 3.11.5`）就成功了。
>
> How to verify? Press `Win + R`, type `cmd`, hit Enter, then type `python --version`.
> If it shows a version (e.g. `Python 3.11.5`), you're good.

### 第 2 步：一键安装肥鱼 / Step 2: One-Click Install

**双击 `FATPACK.bat`**，然后等它自己跑完。
**Double-click `FATPACK.bat`** and wait for it to finish.

它会自动帮你 / It will automatically:
- ✅ 检查 Python / Check Python
- ✅ 释放肥鱼的程序文件 / Extract FatFish program files
- ✅ 创建独立的运行环境（venv） / Create an isolated environment (venv)
- ✅ 安装需要的库（openai、python-dotenv、requests） / Install required libraries (openai, python-dotenv, requests)
- ✅ 生成一个 `.env` 配置文件，并**自动用记事本打开** / Generate a `.env` config file and **open it in Notepad**

### 第 3 步：填 API Key（关键！） / Step 3: Fill In Your API Key (Crucial!)

上一步会自动打开一个叫 `.env` 的记事本文件，长这样：
The previous step opens a Notepad file named `.env`, which looks like this:

```
DEEPSEEK_API_KEY=sk-在这里填入你的deepseek密钥
TAVILY_API_KEY=tvly-在这里填入你的tavily密钥
```

把等号后面的**占位文字**换成你自己的密钥，保存关闭。去哪申请？
Replace the **placeholder text** after `=` with your own key, then save and close. Where to apply?

| 密钥 / Key | 用途 / Purpose | 申请地址 / Apply At |
|---|---|---|
| `DEEPSEEK_API_KEY` | **必填**，没它没法聊天 / **Required**, no chat without it | <https://platform.deepseek.com/> |
| `TAVILY_API_KEY` | 选填，没它不能联网搜索 / Optional, no web search without it | <https://tavily.com/> |

> 💡 只填 DeepSeek 也能用，只是不能联网。
> 💡 DeepSeek alone is enough to use it — you just won't have web search.

### 第 4 步：启动！ / Step 4: Launch!

**双击 `fatfish1.0.2.bat`**，看到彩色的肥鱼横幅就成功了 🎉
**Double-click `fatfish1.0.2.bat`**. If you see the colorful FatFish banner, you're in 🎉

---

## 三、怎么跟肥鱼说话？ / How to Talk to FatFish

启动后会出现提示符，直接打字回车就行，比如：
Once started, a prompt appears — just type and hit Enter, e.g.:

```
🌐~🔍 你 ▸ 你好，介绍一下你自己
```

### 常用命令（在提示符里输入） / Common Commands (type at the prompt)

| 你输入 / You Type | 肥鱼会干啥 / What FatFish Does |
|---|---|
| `@C:\某个文件.txt` | 读取这个文件的内容 / Read the content of this file |
| `/read 文件路径` | 读一个或多个文件 / Read one or more files |
| `/readr 文件夹路径` | 递归读整个文件夹里的所有文件 / Recursively read all files in a folder |
| `/net on` | 打开联网（每次都搜） / Turn on web search (search every time) |
| `/net off` | 关闭联网 / Turn off web search |
| `/net auto` | 智能判断要不要联网（默认） / Smart auto-decide (default) |
| `/search 今天天气` | 强制联网搜一次 / Force one web search |
| `/ws` | 查看工作台命令帮助 / Show workspace command help |
| `/clear` | 清空聊天记录，重新开始 / Clear chat history and start over |
| `/help` | 看所有命令 / Show all commands |
| `exit` 或 `quit` | 退出肥鱼 / Quit FatFish |

> 路径里有空格，用双引号包起来，例如：`/read "C:\我的 文件夹\a.py"`
> If a path contains spaces, wrap it in double quotes, e.g. `/read "C:\my folder\a.py"`

---

## 三·五、API Key 怎么申请？怎么用？（重点章节） / How to Get and Use API Keys (Key Chapter)

肥鱼靠两个 Key 才能干活：**DeepSeek Key（必填）** 和 **Tavily Key（选填）**。
这一节手把手教你申请，猪都能学会 🐷
FatFish needs two keys to work: **DeepSeek Key (required)** and **Tavily Key (optional)**.
This section walks you through it step by step — even a pig can learn 🐷

### 🅰️ DeepSeek API Key（必填，负责"聊天大脑"） / DeepSeek API Key (Required — the "Chat Brain")

**它是干嘛的**：肥鱼回答你、写代码、思考，全靠它。**没有它，肥鱼一句话都说不出来。**
**What it does**: It powers FatFish's replies, code writing, and thinking. **Without it, FatFish can't say a single word.**

#### 申请步骤 / Steps to Apply

1. **打开官网**：浏览器访问 <https://platform.deepseek.com/>
   **Open the site**: visit <https://platform.deepseek.com/>
2. **注册账号** / **Sign up**:
   - 用**手机号**或**邮箱**注册 / Register with a **phone number** or **email**
   - 收验证码 → 填进去 → 完成注册 / Receive the code → enter it → done
3. **实名认证**（部分功能需要） / **Identity verification** (needed for some features):
   - 登录后进「个人中心」，按提示完成实名认证 / Log in → "Profile" → follow the prompts
4. **充值**（重要 ⚠️） / **Top up** (important ⚠️):
   - DeepSeek 的 API 是**按用量付费**的，不是免费的 / The API is **pay-as-you-go**, not free
   - 进「充值」页面，充一点钱（比如 10 元就能用很久） / Top up a little (e.g. ¥10 lasts a long time)
   - 💡 新账号有时会送一点免费额度，够你试玩 / 💡 New accounts sometimes get free credits — enough to try
5. **创建 API Key** / **Create an API Key**:
   - 进左侧菜单 **「API Keys」**（有的版本叫「API 密钥」） / Open **"API Keys"** in the left menu
   - 点 **「创建 API Key」** 按钮 / Click **"Create API Key"**
   - 给它起个名字（随便，比如 `fatfish`） / Give it a name (anything, e.g. `fatfish`)
   - 点确定 → **会弹出一长串以 `sk-` 开头的字符** / Confirm → **a long string starting with `sk-` appears**
6. **⚠️ 立刻复制保存！** / **⚠️ Copy and save it immediately!**
   - 这串 `sk-xxxxxxxx...` 就是你的 Key / That `sk-xxxxxxxx...` string is your Key
   - **它只显示这一次！关掉页面就再也看不到了** / **It's shown only once! Close the page and it's gone forever**
   - 建议先粘到记事本存着，丢了只能删掉重新建 / Paste it into a notepad — if lost, you must delete and recreate

#### 填进肥鱼 / Fill It Into FatFish

打开肥鱼目录里的 `.env` 文件，把 Key 填进去（注意 **`=` 两边不要有空格**）：
Open the `.env` file in the FatFish folder and paste the Key in (note: **no spaces around `=`**):

```
DEEPSEEK_API_KEY=sk-你复制的那一长串字符
```

保存关闭即可。/ Save and close.

---

### 🅱️ Tavily API Key（选填，负责"联网搜索"） / Tavily API Key (Optional — for "Web Search")

**它是干嘛的**：让肥鱼能上网查最新消息、新闻、天气、股价。
**不填也能用肥鱼**，只是它不能联网，遇到"最新"类问题会答不上来。
**What it does**: Lets FatFish search the web for the latest news, weather, stock prices.
**FatFish still works without it** — it just can't go online, and can't answer "latest" questions.

#### 申请步骤 / Steps to Apply

1. **打开官网**：浏览器访问 <https://tavily.com/>
   **Open the site**: visit <https://tavily.com/>
2. **注册账号** / **Sign up**:
   - 点右上角 **「Sign Up」**（注册） / Click **"Sign Up"** at the top right
   - 可以用 **Microsoft 账号**一键登录，也可以用邮箱注册 / Use a **Microsoft account** for one-click login, or register with email
3. **领取免费额度** / **Claim free credits**:
   - Tavily 注册后会送**每月免费搜索次数**（个人用基本够） / Tavily gives **monthly free searches** (usually enough for personal use)
   - 在控制台能看到剩余额度 / See remaining credits in the dashboard
4. **获取 API Key** / **Get the API Key**:
   - 登录后进 **「Dashboard」**（控制台） / Log in → **"Dashboard"**
   - 找到 **「API Keys」** 区域 / Find the **"API Keys"** section
   - 复制那串以 **`tvly-`** 开头的字符 / Copy the string starting with **`tvly-`**
5. **同样立刻保存**，别弄丢 / **Save it immediately** too — don't lose it

#### 填进肥鱼 / Fill It Into FatFish

同样打开 `.env`，填进去：
Open `.env` the same way and paste it in:

```
TAVILY_API_KEY=tvly-你复制的那串字符
```

---

### 📋 填完长这样（示例） / What It Looks Like When Done (Example)

你的 `.env` 文件最终应该是这个样子（**下面是假的 Key，别照抄**）：
Your `.env` should end up like this (**the keys below are fake — don't copy them**):

```
DEEPSEEK_API_KEY=sk-1a2b3c4d5e6f7g8h9i0j
TAVILY_API_KEY=tvly-abcdefghijklmnop
```

> ⚠️ 常见错误 / Common mistakes:
> - `=` 前后加了空格 → 报错 / Spaces around `=` → error
> - Key 复制时**少了尾巴**或**多了换行** → 报错 / **Missing the tail** or **extra newline** when copying → error
> - 把 `sk-` 或 `tvly-` 前缀删掉了 → 报错 / Deleting the `sk-` or `tvly-` prefix → error

---

### 🔄 改完 Key 不用重启！ / No Restart Needed After Changing the Key!

如果你肥鱼已经在运行，改完 `.env` 后，在肥鱼提示符里输入：
If FatFish is already running, after editing `.env`, type this at the prompt:

```
/reload
```

它会重新读取配置，看到「🔄 已重新加载 .env，API key 已更新」就成功了。
It reloads the config. If you see "🔄 已重新加载 .env，API key 已更新", you're done.

---

### 💡 关于"联网搜索"的一个说明 / A Note on "Web Search"

肥鱼用的是 **Tavily** 做联网搜索（代码里写死的）。
网上还有腾讯云、火山引擎、阿里云等提供的"联网搜索 API"，但**肥鱼不支持那些**，
所以申请 Key 认准 **Tavily 官网**就行，别去别的平台申请。
FatFish uses **Tavily** for web search (hard-coded).
Other providers (Tencent Cloud, Volcano Engine, Alibaba Cloud, etc.) offer search APIs, but **FatFish doesn't support them**.
So apply for your key at the **official Tavily site** — nowhere else.

---

### 🚨 Key 泄露了怎么办？（重要！必看） / What If Your Key Leaks? (Important! Must Read)

API Key 就像你家的**银行卡密码**。一旦被坏人拿到，他就能用**你的钱**去调用 API，
把你的余额刷光，而账单全算你头上 💸
An API Key is like your **bank card PIN**. If a bad actor gets it, they can spend **your money** on API calls,
drain your balance, and the bill lands on you 💸

#### 什么情况算"泄露"了？ / What Counts as a "Leak"?

只要你的 Key **被你不认识的人看到了**，就算泄露，比如：
If your key is **seen by someone you don't know**, it's a leak. For example:

| 场景 / Scenario | 危险吗 / Risky? |
|---|---|
| 把 `.env` 文件截图发到群里 / 发到网上 / Screenshotting `.env` to a group/online | 🔴 危险，等于把密码发出去 / Dangerous — like sending your password |
| 把 `.env` 上传到 GitHub / Gitee 等公开仓库 / Uploading `.env` to GitHub/Gitee | 🔴 极度危险，机器人几秒就能扫到 / Extremely dangerous — bots scan it in seconds |
| Key 不小心写进了要分享的代码里 / Key accidentally written into shared code | 🔴 危险 / Dangerous |
| 电脑被人用过、或中了病毒 / PC used by others, or infected | 🟡 建议直接换 / Better to replace it |
| 只是自己电脑上放着，没给别人看过 / Just on your own PC, seen by no one | 🟢 安全，不用管 / Safe, ignore |

> ⚠️ 特别提醒：**GitHub 上有专门的机器人**，24 小时扫描新上传的 Key。
> 你刚推上去几秒钟，就可能已经被扫走并开始盗刷了。所以**永远不要把 `.env` 传上去**！
>
> ⚠️ Special warning: **GitHub has dedicated bots** scanning new uploads 24/7.
> Within seconds of pushing, your key may be scraped and abused. So **never upload `.env`**!

#### 泄露了怎么补救？（三步） / How to Fix a Leak (3 Steps)

**第 1 步：立刻去平台删掉旧 Key**（这是最关键的，越快越好）
**Step 1: Delete the old key on the platform immediately** (most critical — the sooner the better)

- **DeepSeek**：登录 <https://platform.deepseek.com/> → 进「API Keys」→
  找到泄露的那个 Key → 点**「删除」**（Delete / 删除）
  **DeepSeek**: log in to <https://platform.deepseek.com/> → "API Keys" →
  find the leaked key → click **"Delete"**
- **Tavily**：登录 <https://tavily.com/> → 进「Dashboard」→
  在「API Keys」里把旧 Key **删除**
  **Tavily**: log in to <https://tavily.com/> → "Dashboard" →
  **delete** the old key under "API Keys"

> 💡 删掉后，旧 Key **立刻失效**，坏人再用就会报错，刷不动了。
> 💡 Once deleted, the old key **becomes invalid instantly** — abusers just get errors.

**第 2 步：创建一个新 Key** / **Step 2: Create a new key**
- 按前面「申请步骤」里的方法，**重新创建一个新 Key** / Follow the "Steps to Apply" above to **create a new key**
- 同样记得**立刻复制保存** / Again, **copy and save it immediately**

**第 3 步：把新 Key 填回 `.env`** / **Step 3: Put the new key back into `.env`**
- 打开 `.env`，把旧 Key 换成新 Key / Open `.env` and replace the old key with the new one
- 如果肥鱼正在运行，输入 `/reload` 重新加载 / If FatFish is running, type `/reload`

#### 防患于未然（好习惯） / Prevention (Good Habits)

| 好习惯 / Good Habit | 说明 / Notes |
|---|---|
| 🔒 `.env` 只放自己电脑上 / Keep `.env` on your PC only | 别发群、别上传、别截图 / Don't share, upload, or screenshot |
| 🙈 分享截图前先打码 / Blur before sharing screenshots | 把 `sk-` / `tvly-` 后面的字符涂掉 / Mask the characters after `sk-` / `tvly-` |
| 📁 用 git 的话加 `.gitignore` / Add `.gitignore` if using git | 在项目里加一行 `.env`，防止误传 / Add a `.env` line to prevent accidental commits |
| 🔄 怀疑就换 / Replace when in doubt | 宁可多换一次，也别赌 / Better safe than sorry |
| 💰 别充太多钱 / Don't top up too much | 余额少，就算泄露损失也小 / A small balance means small loss if leaked |

> 🧠 一句话记住：**Key 一旦离开你的电脑，就当它已经泄露，立刻删掉重建。**
> 🧠 Remember: **Once a key leaves your PC, treat it as leaked — delete and recreate it at once.**

#### 检查一下你的余额（心里有数） / Check Your Balance (Stay Aware)

- **DeepSeek**：控制台首页能看到「余额」和「用量」，发现异常消费要警惕
  **DeepSeek**: the dashboard shows "Balance" and "Usage" — watch for odd spending
- **Tavily**：Dashboard 里能看到本月搜索次数，突然暴涨说明可能被盗用
  **Tavily**: the dashboard shows this month's search count — a sudden spike may mean abuse

---

## 四、什么是"工作台"？ / What Is the "Workspace"?

**工作台 = 肥鱼能自由读写的那个文件夹**（默认是程序目录下的 `workspace`）。
**Workspace = the folder FatFish can freely read and write** (defaults to `workspace` under the program folder).

肥鱼只能在这个文件夹里读写文件，出了这个范围它会先问你同不同意 —— 这是**安全设计**，防止它乱改你电脑上的东西。
FatFish can only read/write inside this folder. Outside it, it asks your permission first — a **safety design** to stop it from messing with your PC.

| 命令 / Command | 作用 / What It Does |
|---|---|
| `/ws where` | 看看现在工作台在哪 / Show where the workspace is |
| `/ws ls` | 列出工作台里的文件 / List files in the workspace |
| `/ws cd 路径` | 换一个工作台文件夹 / Switch to another workspace folder |
| `/ws reset` | 恢复默认工作台 / Restore the default workspace |

> 还有一条安全规则：肥鱼**改文件前必须先读过那个文件**，改的时候还会弹出确认让你按 `y` 同意。它绝不会偷偷改你的东西。
> One more safety rule: FatFish **must read a file before editing it**, and asks you to press `y` to confirm. It never silently changes your stuff.

---

## 五、肥鱼长啥样？（界面小知识） / What Does FatFish Look Like? (UI Trivia)

- 回答会**根据情绪自动变色**：开心是金色、出错是红色、代码是绿色……
  Replies **auto-color by mood**: gold for happy, red for errors, green for code…
- 你甚至可以让它用 `{{红色}}文字{{/红色}}` 这种语法给关键词上色
  You can even have it color keywords with syntax like `{{红色}}text{{/红色}}`
- 每次对话都会**自动记日志**，存在 `logs/年/月/日/` 里
  Every conversation is **auto-logged** under `logs/YYYY/MM/DD/`
- 它写的代码会存在 `generated_code/年/月/日/` 里
  Code it writes is saved under `generated_code/YYYY/MM/DD/`

---

## 六、出问题了怎么办？（小白急救包） / Troubleshooting (Beginner First-Aid Kit)

| 现象 / Symptom | 原因 & 解决 / Cause & Fix |
|---|---|
| **双击 bat 一闪就没了** / **The .bat flashes and closes** | 大概率没装 Python 或没加 PATH。重装 Python 并勾选 `Add Python to PATH` / Likely Python missing or not in PATH. Reinstall Python and check `Add Python to PATH` |
| **提示 `python 不是内部或外部命令`** / **"python is not recognized"** | Python 没加进 PATH。重装时勾选那个选项 / Python isn't in PATH. Check that option when reinstalling |
| **能启动但一聊天就报错** / **Starts but errors on chat** | `.env` 里的 `DEEPSEEK_API_KEY` 没填对。检查有没有多余空格 / `DEEPSEEK_API_KEY` in `.env` is wrong. Check for extra spaces |
| **说不能联网** / **Says it can't go online** | 没填 `TAVILY_API_KEY`，或者 key 过期了 / `TAVILY_API_KEY` missing or expired |
| **依赖安装失败** / **Dependency install fails** | 安装器会自动改用清华镜像重试；还不行就手动跑：`python -m pip install openai python-dotenv requests` / The installer retries via the Tsinghua mirror; if it still fails, run manually: `python -m pip install openai python-dotenv requests` |
| **改完 .env 不想重启** / **Edited .env, don't want to restart** | 在肥鱼里输入 `/reload` 重新加载配置 / Type `/reload` in FatFish to reload config |
| **窗口中文乱码** / **Garbled Chinese in the window** | 一般不会；bat 里已经设了 UTF-8。若出现，换个新一点的 Windows 终端 / Rare; the .bat already sets UTF-8. If it happens, use a newer Windows terminal |

---

## 七、文件都是干啥的？（进阶，看不懂可跳过） / File Overview (Advanced — Skip If Confusing)

```
fatfish/
├── FATPACK.bat        ← 一键安装器（双击它！） / One-click installer (double-click it!)
├── fatfish1.0.2.bat   ← 启动器（装好后双击它！） / Launcher (double-click after install!)
├── FATFFISHI.py       ← 肥鱼主程序 / Main program
├── workspace.py       ← 工作台/文件读写工具 / Workspace & file I/O tools
├── file_tools.py      ← 读取文件、目录的工具 / File & directory reading tools
├── net_tools.py       ← 联网搜索工具 / Web search tools
├── exec_tools.py      ← 执行命令、跑 Python 的工具 / Command & Python execution tools
├── .env               ← 你的密钥配置（安装时自动生成） / Your key config (auto-generated)
├── logs/              ← 聊天日志（自动生成） / Chat logs (auto-generated)
└── generated_code/    ← 肥鱼写的代码（自动生成） / Code written by FatFish (auto-generated)
```

---

## 八、极简速查（懒人版） / Quick Reference (Lazy Edition)

```
1. 装 Python（勾 Add to PATH） / Install Python (check Add to PATH)
2. 双击 FATPACK.bat / Double-click FATPACK.bat
3. 去 platform.deepseek.com 申请 DeepSeek Key（sk- 开头）
   （想联网就再去 tavily.com 申请 Tavily Key，tvly- 开头）
   / Get a DeepSeek Key at platform.deepseek.com (starts with sk-)
   / (For web search, also get a Tavily Key at tavily.com, starts with tvly-)
4. 把 Key 填进打开的 .env 文件 / Put the keys into the opened .env file
5. 双击 fatfish1.0.2.bat / Double-click fatfish1.0.2.bat
6. 开始打字聊天 🐟 / Start typing and chatting 🐟
```

**就这几步，猪都会了。祝你玩得开心！** 🎉
**That's it — even a pig can do it. Have fun!** 🎉

> 📌 Key 怎么申请、怎么填、填错了咋办，看 **「三·五、API Key 怎么申请？」** 那一章。
> 📌 For how to get/fill keys and fix mistakes, see the **"How to Get and Use API Keys"** chapter.
