import os
import re
import base64
import logging
from datetime import datetime

MAX_BYTES     = 200 * 1024
MAX_CHARS     = 50_000
MAX_DIR_FILES = 20

MAX_IMAGE_BYTES       = 32 * 1024 * 1024
MAX_IMAGES_PER_MSG    = 5
MAX_TOTAL_IMAGE_BYTES = 60 * 1024 * 1024

TEXT_EXTS = {
    ".py", ".md", ".txt", ".json", ".yaml", ".yml", ".toml", ".ini", ".cfg",
    ".js", ".ts", ".jsx", ".tsx", ".html", ".css", ".scss", ".vue",
    ".sh", ".bat", ".ps1", ".cmd",
    ".c", ".cpp", ".h", ".hpp", ".java", ".go", ".rs", ".rb", ".php",
    ".sql", ".csv", ".log", ".xml", ".env",
}

IMAGE_EXTS = {".jpg", ".jpeg", ".png", ".gif", ".webp"}

def is_image_path(path):
    return os.path.splitext(str(path))[1].lower() in IMAGE_EXTS

def sniff_image_mime(head):
    if head.startswith(b"\x89PNG\r\n\x1a\n"):
        return "image/png"
    if head.startswith(b"\xff\xd8\xff"):
        return "image/jpeg"
    if head.startswith(b"GIF87a") or head.startswith(b"GIF89a"):
        return "image/gif"
    if len(head) >= 12 and head[0:4] == b"RIFF" and head[8:12] == b"WEBP":
        return "image/webp"
    return None

def load_image(path):
    p = normalize_path(path)
    if not p:
        return False, "empty path", 0
    if not os.path.exists(p):
        return False, "file does not exist", 0
    if os.path.isdir(p):
        return False, "this is a directory", 0

    try:
        size = os.path.getsize(p)
    except OSError as e:
        return False, f"cannot get file size: {e}", 0
    if size > MAX_IMAGE_BYTES:
        return False, (f"image too large ({size} bytes > {MAX_IMAGE_BYTES} bytes), "
                       f"skipped"), 0

    try:
        with open(p, "rb") as f:
            raw = f.read()
    except OSError as e:
        return False, f"read failed: {e}", 0

    mime = sniff_image_mime(raw[:16])
    if not mime:
        return False, "not a supported image format (JPEG/PNG/GIF/WebP)", 0

    b64 = base64.b64encode(raw).decode("ascii")
    return True, {
        "type": "image_url",
        "image_url": {"url": f"data:{mime};base64,{b64}"},
    }, size

def _log(msg):
    logging.info(msg)

try:
    from common import ts as _ts
except ImportError:
    def _ts():
        return datetime.now().strftime("%Y-%m-%d %H:%M:%S")

_FILE_CACHE = {}

def normalize_path(path):
    p = str(path).strip().strip("'\"`")
    if not p:
        return ""
    p = os.path.expanduser(p)
    p = os.path.expandvars(p)
    return p

_normalize_path = normalize_path

def read_text_file(path):
    p = normalize_path(path)
    if not p:
        return False, "empty path"
    if not os.path.exists(p):
        return False, "file does not exist"
    if os.path.isdir(p):
        return False, "this is a directory"
    if not os.path.isfile(p):
        return False, "not a regular file"

    try:
        size = os.path.getsize(p)
    except OSError as e:
        return False, f"cannot get file size: {e}"
    if size > MAX_BYTES:
        return False, f"file too large ({size} bytes > {MAX_BYTES} bytes)"

    try:
        with open(p, "rb") as f:
            if b"\x00" in f.read(4096):
                return False, "looks like a binary file, skipped"
    except OSError as e:
        return False, f"read failed: {e}"

    text = None
    for enc in ("utf-8-sig", "utf-8", "gbk", "big5"):
        try:
            with open(p, "r", encoding=enc) as f:
                text = f.read()
            break
        except UnicodeDecodeError:
            continue
        except OSError as e:
            return False, f"read failed: {e}"
    if text is None:
        return False, "unrecognized file encoding"

    n = len(text)
    if n > MAX_CHARS:
        text = text[:MAX_CHARS] + f"\n...(truncated; original length {n} chars)"
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
        return False, "not a directory", []

    picked, blocks = [], []

    if recursive:
        for root, dirs, files in os.walk(p):
            dirs.sort()
            for name in sorted(_pick_files(files)):
                if len(picked) >= MAX_DIR_FILES:
                    blocks.append(f"...(too many files; only the first {MAX_DIR_FILES} were read)")
                    break
                fp = os.path.join(root, name)
                ok, data = read_text_file_cached(fp)
                if ok:
                    picked.append(fp)
                    blocks.append(f"[FILE: {fp}]\n{data}\n[END FILE: {fp}]")
            if len(picked) >= MAX_DIR_FILES:
                break
        head = f"directory: {p} (recursive)\nread {len(picked)} file(s)"
    else:
        try:
            entries = sorted(os.listdir(p))
        except OSError as e:
            return False, f"cannot list directory: {e}", []

        dirs  = [e for e in entries if os.path.isdir(os.path.join(p, e))]
        files = [e for e in entries if os.path.isfile(os.path.join(p, e))]
        files = _pick_files(files)

        head = (f"directory: {p}\n"
                f"subdirectories: {len(dirs)} -- {', '.join(dirs[:20]) or '(none)'}\n"
                f"files: {len(files)} -- {', '.join(files[:20]) or '(none)'}")

        for name in files:
            if len(picked) >= MAX_DIR_FILES:
                blocks.append(f"...(too many files in the directory; only the first {MAX_DIR_FILES} were read)")
                break
            fp = os.path.join(p, name)
            ok, data = read_text_file_cached(fp)
            if ok:
                picked.append(fp)
                blocks.append(f"[FILE: {fp}]\n{data}\n[END FILE: {fp}]")

    body = "\n\n".join(blocks)
    text = head + ("\n\n" + body if body else "")
    return True, text, picked

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
    paths = []
    recursive = False
    stripped = text.strip()

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

def load_files(paths, recursive=False):
    blocks, ok_list, err_list = [], [], []
    image_blocks = []
    total_img_bytes = 0

    for raw in paths:
        p = normalize_path(raw)
        if not p:
            err_list.append((raw, "empty path"))
            continue

        if os.path.isfile(p) and is_image_path(p):
            if len(image_blocks) >= MAX_IMAGES_PER_MSG:
                err_list.append(
                    (p, f"image count exceeds the per-message limit of {MAX_IMAGES_PER_MSG}, skipped"))
                _log(f"[{_ts()}] image count over limit, skipped: {p}")
                continue
            ok, res, size = load_image(p)
            if not ok:
                err_list.append((p, res))
                _log(f"[{_ts()}] image read failed {p}: {res}")
                continue
            if total_img_bytes + size > MAX_TOTAL_IMAGE_BYTES:
                err_list.append(
                    (p, f"total image size exceeds the per-message limit of "
                        f"{MAX_TOTAL_IMAGE_BYTES} bytes, skipped"))
                _log(f"[{_ts()}] total image size over limit, skipped: {p}")
                continue
            image_blocks.append(res)
            total_img_bytes += size
            ok_list.append(f"{p} (image)")
            _log(f"[{_ts()}] image loaded: {p} ({size} bytes)")
            continue

        if os.path.isdir(p):
            ok, data, picked = read_directory(p, recursive=recursive)
            if ok:
                blocks.append(f"[DIR: {p}]\n{data}\n[END DIR: {p}]")
                ok_list.append(f"{p} ({len(picked)} file(s))")
                _log(f"[{_ts()}] directory loaded: {p} ({len(picked)} file(s))")
            else:
                blocks.append(f"[DIR: {p}] read failed: {data}")
                err_list.append((p, data))
                _log(f"[{_ts()}] directory read failed {p}: {data}")
            continue

        ok, data = read_text_file_cached(p)
        if ok:
            blocks.append(f"[FILE: {p}]\n{data}\n[END FILE: {p}]")
            ok_list.append(p)
            _log(f"[{_ts()}] file loaded: {p}")
        else:
            blocks.append(f"[FILE: {p}] read failed: {data}")
            err_list.append((p, data))
            _log(f"[{_ts()}] file read failed {p}: {data}")

    return "\n\n".join(blocks), ok_list, err_list, image_blocks

def build_content(text, image_blocks=None):
    if not image_blocks:
        return text
    content = []
    if text:
        content.append({"type": "text", "text": text})
    content.extend(image_blocks)
    return content
