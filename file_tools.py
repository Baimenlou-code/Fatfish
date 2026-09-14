import os
import re
import base64
import logging
from datetime import datetime

# ============ 可调参数 ============
MAX_BYTES     = 200 * 1024      # 单文件最大字节数
MAX_CHARS     = 50_000          # 单文件最大字符数（超出截断）
MAX_DIR_FILES = 20              # 目录最多读取文件数

# ---- 图片相关阈值 ----
MAX_IMAGE_BYTES       = 32 * 1024 * 1024   # 单张图片最大 32 MiB（API 硬限制）
MAX_IMAGES_PER_MSG    = 5                  # 单条消息最多几张图
MAX_TOTAL_IMAGE_BYTES = 60 * 1024 * 1024   # 单条消息图片总字节上限（base64 前）

TEXT_EXTS = {
    ".py", ".md", ".txt", ".json", ".yaml", ".yml", ".toml", ".ini", ".cfg",
    ".js", ".ts", ".jsx", ".tsx", ".html", ".css", ".scss", ".vue",
    ".sh", ".bat", ".ps1", ".cmd",
    ".c", ".cpp", ".h", ".hpp", ".java", ".go", ".rs", ".rb", ".php",
    ".sql", ".csv", ".log", ".xml", ".env",
}

# ============ 图片支持 ============
# 支持的图片扩展名（仅用于快速初筛，最终以文件实际内容为准）
IMAGE_EXTS = {".jpg", ".jpeg", ".png", ".gif", ".webp"}

def is_image_path(path):
    """按扩展名快速判断是否可能是图片。"""
    return os.path.splitext(str(path))[1].lower() in IMAGE_EXTS

def sniff_image_mime(head):
    """按文件头魔数判断真实图片格式，返回 mime 或 None。

    文档要求：格式由文件实际内容判断，而非文件名或 MIME 声明。
    """
    if head.startswith(b"\x89PNG\r\n\x1a\n"):
        return "image/png"
    if head.startswith(b"\xff\xd8\xff"):
        return "image/jpeg"
    if head.startswith(b"GIF87a") or head.startswith(b"GIF89a"):
        return "image/gif"
    # WebP: RIFF....WEBP
    if len(head) >= 12 and head[0:4] == b"RIFF" and head[8:12] == b"WEBP":
        return "image/webp"
    return None

def load_image(path):
    """读取图片并编码为 OpenAI 兼容的 image_url 内容块。

    返回 (ok, block_or_err, raw_size)：
      ok=True  → block 为 {"type": "image_url", "image_url": {...}}，
                 raw_size 为原始字节数（供调用方累计总量）
      ok=False → block 为错误说明字符串，raw_size 为 0
    """
    p = normalize_path(path)
    if not p:
        return False, "路径为空", 0
    if not os.path.exists(p):
        return False, "文件不存在", 0
    if os.path.isdir(p):
        return False, "这是一个目录", 0

    try:
        size = os.path.getsize(p)
    except OSError as e:
        return False, f"无法获取文件大小：{e}", 0
    if size > MAX_IMAGE_BYTES:
        return False, (f"图片过大（{size} 字节 > {MAX_IMAGE_BYTES} 字节），"
                       f"已跳过"), 0

    try:
        with open(p, "rb") as f:
            raw = f.read()
    except OSError as e:
        return False, f"读取失败：{e}", 0

    mime = sniff_image_mime(raw[:16])
    if not mime:
        return False, "不是受支持的图片格式（JPEG/PNG/GIF/WebP）", 0

    b64 = base64.b64encode(raw).decode("ascii")
    return True, {
        "type": "image_url",
        "image_url": {"url": f"data:{mime};base64,{b64}"},
    }, size

# ============ 日志（复用主模块的 logging，若未配置则静默）============
def _log(msg):
    logging.info(msg)

def _ts():
    return datetime.now().strftime("%Y-%m-%d %H:%M:%S")

# ============ 文件缓存 ============
_FILE_CACHE = {}

# ============ 路径规范化 ============
def normalize_path(path):
    p = str(path).strip().strip("'\"`")
    if not p:
        return ""
    p = os.path.expanduser(p)
    p = os.path.expandvars(p)
    return p

# 兼容旧名（主文件里用的是 _normalize_path）
_normalize_path = normalize_path

# ============ 单文件读取 ============
def read_text_file(path):
    p = normalize_path(path)
    if not p:
        return False, "路径为空"
    if not os.path.exists(p):
        return False, "文件不存在"
    if os.path.isdir(p):
        return False, "这是一个目录"
    if not os.path.isfile(p):
        return False, "不是普通文件"

    try:
        size = os.path.getsize(p)
    except OSError as e:
        return False, f"无法获取文件大小：{e}"
    if size > MAX_BYTES:
        return False, f"文件过大（{size} 字节 > {MAX_BYTES} 字节）"

    try:
        with open(p, "rb") as f:
            if b"\x00" in f.read(4096):
                return False, "看起来是二进制文件，已跳过"
    except OSError as e:
        return False, f"读取失败：{e}"

    text = None
    for enc in ("utf-8-sig", "utf-8", "gbk", "big5"):
        try:
            with open(p, "r", encoding=enc) as f:
                text = f.read()
            break
        except UnicodeDecodeError:
            continue
        except OSError as e:
            return False, f"读取失败：{e}"
    if text is None:
        return False, "无法识别文件编码"

    n = len(text)
    if n > MAX_CHARS:
        text = text[:MAX_CHARS] + f"\n...（已截断，原文共 {n} 字符）"
    return True, text

def read_text_file_cached(path):
    p = normalize_path(path)
    try:
        mtime = os.path.getmtime(p)
    except OSError:
        return read_text_file(p)
    cached = _FILE_CACHE.get(p)
    if cached and cached[0] == mtime:
        return True, cached[1]
    ok, data = read_text_file(p)
    if ok:
        _FILE_CACHE[p] = (mtime, data)
    return ok, data

# ============ 目录读取 ============
def _pick_files(files):
    picked = []
    for f in files:
        ext = os.path.splitext(f)[1].lower()
        if ext in TEXT_EXTS or ext == "":
            picked.append(f)
    return picked

def read_directory(path, recursive=False):
    p = normalize_path(path)
    if not os.path.isdir(p):
        return False, "不是目录", []

    picked, blocks = [], []

    if recursive:
        for root, dirs, files in os.walk(p):
            dirs.sort()
            for name in sorted(_pick_files(files)):
                if len(picked) >= MAX_DIR_FILES:
                    blocks.append(f"...（文件过多，仅读取前 {MAX_DIR_FILES} 个）")
                    break
                fp = os.path.join(root, name)
                ok, data = read_text_file_cached(fp)
                if ok:
                    picked.append(fp)
                    blocks.append(f"【文件：{fp}】\n{data}\n【文件结束：{fp}】")
            if len(picked) >= MAX_DIR_FILES:
                break
        head = f"目录：{p}（递归）\n共读取 {len(picked)} 个文件"
    else:
        try:
            entries = sorted(os.listdir(p))
        except OSError as e:
            return False, f"无法列出目录：{e}", []

        dirs  = [e for e in entries if os.path.isdir(os.path.join(p, e))]
        files = [e for e in entries if os.path.isfile(os.path.join(p, e))]
        files = _pick_files(files)

        head = (f"目录：{p}\n"
                f"子目录 {len(dirs)} 个：{', '.join(dirs[:20]) or '（无）'}\n"
                f"文件   {len(files)} 个：{', '.join(files[:20]) or '（无）'}")

        for name in files:
            if len(picked) >= MAX_DIR_FILES:
                blocks.append(f"...（目录内文件过多，仅读取前 {MAX_DIR_FILES} 个）")
                break
            fp = os.path.join(p, name)
            ok, data = read_text_file_cached(fp)
            if ok:
                picked.append(fp)
                blocks.append(f"【文件：{fp}】\n{data}\n【文件结束：{fp}】")

    body = "\n\n".join(blocks)
    text = head + ("\n\n" + body if body else "")
    return True, text, picked

# ============ 路径解析 ============
QUOTED_PATH_RE = re.compile(r'@?["\']([^"\']+)["\']')
AT_PATH_RE     = re.compile(r'@([^\s@]+)')

def _split_paths(s):
    out, buf, quote = [], "", None
    for ch in s:
        if quote:
            if ch == quote:
                out.append(buf); buf = ""; quote = None
            else:
                buf += ch
        elif ch in "\"'":
            quote = ch
        elif ch.isspace():
            if buf:
                out.append(buf); buf = ""
        else:
            buf += ch
    if buf:
        out.append(buf)
    return [x for x in out if x]

def _looks_like_path(s):
    if not s or "\n" in s or len(s) > 4096:
        return False
    p = normalize_path(s)
    if not p:
        return False
    if os.path.isfile(p):
        return True
    return os.path.isdir(p) and (os.sep in s or "/" in s)

def extract_file_refs(text):
    """从用户输入中提取路径列表和是否递归标志。
    返回 (paths, recursive)。"""
    paths = []
    recursive = False
    stripped = text.strip()

    # 修复：/readr 前缀不能被 /read 吞掉
    for cmd in ("/readr", "/read", "/file", "/open"):
        low = stripped.lower()
        if low == cmd or low.startswith(cmd + " "):
            if cmd == "/readr":
                recursive = True
            paths.extend(_split_paths(stripped[len(cmd):].strip()))
            break

    spans = []
    for m in QUOTED_PATH_RE.finditer(text):
        cand = m.group(1)
        if text[m.start()] == "@" or os.path.exists(normalize_path(cand)):
            paths.append(cand)
            spans.append((m.start(), m.end()))

    if spans:
        masked = list(text)
        for s, e in spans:
            for i in range(s, e):
                masked[i] = " "
        text_for_at = "".join(masked)
    else:
        text_for_at = text
    for m in AT_PATH_RE.finditer(text_for_at):
        paths.append(m.group(1))

    if not paths and _looks_like_path(stripped):
        paths.append(stripped)

    seen, out = set(), []
    for p in paths:
        key = normalize_path(p)
        if key and key not in seen:
            seen.add(key)
            out.append(key)
    return out, recursive

# ============ 批量加载 ============
def load_files(paths, recursive=False):
    """读取一批路径（文件或目录）。

    返回 (合并文本, 成功列表, 失败列表, 图片块列表)。
    图片块列表元素为 OpenAI 兼容的 image_url 内容块。
    图片软闸：单图 >MAX_IMAGE_BYTES、张数 >MAX_IMAGES_PER_MSG、
              总量 >MAX_TOTAL_IMAGE_BYTES 时跳过并记入失败列表。
    """
    blocks, ok_list, err_list = [], [], []
    image_blocks = []          # 收集图片块
    total_img_bytes = 0        # 累计图片原始字节数（base64 前）

    for raw in paths:
        p = normalize_path(raw)
        if not p:
            err_list.append((raw, "路径为空"))
            continue

        # ---- 图片走独立分支（不当作文本读）----
        if os.path.isfile(p) and is_image_path(p):
            # 软闸 1：张数上限
            if len(image_blocks) >= MAX_IMAGES_PER_MSG:
                err_list.append(
                    (p, f"图片数量超过单条上限 {MAX_IMAGES_PER_MSG} 张，已跳过"))
                _log(f"[{_ts()}] 图片张数超限，跳过：{p}")
                continue
            ok, res, size = load_image(p)
            if not ok:
                err_list.append((p, res))
                _log(f"[{_ts()}] 图片读取失败 {p}：{res}")
                continue
            # 软闸 2：总量上限
            if total_img_bytes + size > MAX_TOTAL_IMAGE_BYTES:
                err_list.append(
                    (p, f"图片总量超过单条上限 "
                        f"{MAX_TOTAL_IMAGE_BYTES} 字节，已跳过"))
                _log(f"[{_ts()}] 图片总量超限，跳过：{p}")
                continue
            image_blocks.append(res)
            total_img_bytes += size
            ok_list.append(f"{p}（图片）")
            _log(f"[{_ts()}] 已读取图片：{p}（{size} 字节）")
            continue

        if os.path.isdir(p):
            ok, data, picked = read_directory(p, recursive=recursive)
            if ok:
                blocks.append(f"【目录：{p}】\n{data}\n【目录结束：{p}】")
                ok_list.append(f"{p}（{len(picked)} 个文件）")
                _log(f"[{_ts()}] 已读取目录：{p}（{len(picked)} 个文件）")
            else:
                blocks.append(f"【目录：{p}】读取失败：{data}")
                err_list.append((p, data))
                _log(f"[{_ts()}] 目录读取失败 {p}：{data}")
            continue

        ok, data = read_text_file_cached(p)
        if ok:
            blocks.append(f"【文件：{p}】\n{data}\n【文件结束：{p}】")
            ok_list.append(p)
            _log(f"[{_ts()}] 已读取文件：{p}")
        else:
            blocks.append(f"【文件：{p}】读取失败：{data}")
            err_list.append((p, data))
            _log(f"[{_ts()}] 文件读取失败 {p}：{data}")

    return "\n\n".join(blocks), ok_list, err_list, image_blocks


def build_content(text, image_blocks=None):
    """把文本与图片块组装成模型可用的 content。

    - 无图片 → 返回纯字符串（与旧行为完全一致）
    - 有图片 → 返回块数组 [{"type":"text",...}, {"type":"image_url",...}, ...]
    """
    if not image_blocks:
        return text
    content = []
    if text:
        content.append({"type": "text", "text": text})
    content.extend(image_blocks)
    return content