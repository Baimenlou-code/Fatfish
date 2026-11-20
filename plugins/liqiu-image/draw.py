# -*- coding: utf-8 -*-
"""
秋流生图 —— gpt-image-2 插件脚本。

【为什么这个插件走 python 执行面，而不是 http】

  1. 密钥放不进 header。http 执行面只支持「静态 headers」，而
     Authorization: Bearer <key> 必须来自 .env。把 key 硬编码进 plugin.json
     等于备份、截图、发群里时都在泄露它。python 执行面能直接读环境变量。
  2. 响应会被截断。http 执行面只读响应前 24KB，而一张 4K 图的 b64_json
     动辄几十 MB —— 拿到的必然是一段被切断的 base64，解码只会失败。
  3. 要落盘。生图的产物是一张文件，http 执行面没有「写文件」这个动作。

  代价是风险档为「高」（python 执行面一律按最坏情况定档），每次调用都要点一次允许，
  且不能「本次会话放行」。这是刻意的：这个动作会花钱、会写文件。
  想少点几次，就一次画完；而不是把闸门关掉。

【参数从哪来】
  PluginRunner 会把模型给的参数注入成 FATFISH_ARG_<大写>，这里只读这些变量。
  绝不要从命令行参数读 —— 参数是模型填的，拼进命令行就是命令注入。

【配置从哪来】
  .env（由主程序读进环境变量，子进程继承）：
    LIQIU_API_KEY        必填
    LIQIU_BASE_URL       默认 https://onliqiu.com/v1
    LIQIU_IMAGE_MODEL    默认 gpt-image-2 default（注意名字里真的有空格）
    LIQIU_IMAGE_SIZE     默认 auto
    LIQIU_IMAGE_QUALITY  默认 auto
    LIQIU_IMAGE_DIR      默认 art（相对工作台根）
"""

import base64
import json
import os
import re
import struct
import sys
import time
import urllib.error
import urllib.request

DEFAULT_BASE = "https://onliqiu.com/v1"
DEFAULT_MODEL = "gpt-image-2 default"
API_TIMEOUT = 290          # 比清单里的 300 秒略短：留出解码/写盘的时间
DOWNLOAD_TIMEOUT = 120
MAX_DETAIL = 800           # 出错时回显的响应体上限，防止把上下文撑爆

# Windows 文件名非法字符 + 控制字符
_BAD_NAME = re.compile(r'[\\/:*?"<>|\x00-\x1f]')


# ----------------------------------------------------------------------
# 小工具
# ----------------------------------------------------------------------

def arg(name):
    """读模型给的参数（由 PluginRunner 注入成环境变量）。"""
    return (os.environ.get("FATFISH_ARG_" + name.upper()) or "").strip()


def cfg(name, default=""):
    """读 .env 里的配置。"""
    return (os.environ.get(name) or "").strip() or default


def fail(message, hint=None, **extra):
    """统一的失败输出：一行 JSON，退出码 1。"""
    out = {"ok": False, "error": message}
    if hint:
        out["hint"] = hint
    out.update(extra)
    print(json.dumps(out, ensure_ascii=False))
    return 1


def snippet(raw, limit=MAX_DETAIL):
    """把响应体截成人类还能读的一段，供排障用。"""
    if isinstance(raw, bytes):
        text = raw[: limit * 2].decode("utf-8", "replace")
    else:
        text = str(raw)[: limit * 2]
    return text[:limit]


def hint_for_status(code):
    if code == 401:
        return "key 无效。检查 LIQIU_API_KEY 是否粘全（有没有多一个空格、或粘成了别家的 key）。"
    if code == 403:
        return "没有权限：这个 key 可能没有开放生图接口。"
    if code == 404:
        return "地址不对。LIQIU_BASE_URL 一般写 https://onliqiu.com/v1（不要写到 /images/generations）。"
    if code == 429:
        return "被限速或额度用完了。等一会儿再试，或去后台确认额度。"
    if code == 400:
        return "参数被拒：最常见是尺寸不被支持，或 prompt 触发了内容审核。"
    if 500 <= code < 600:
        return "服务端错误。这不是你的配置问题，过一会儿重试。"
    return None


# ----------------------------------------------------------------------
# 图片格式识别与尺寸解析（只用标准库）
# ----------------------------------------------------------------------

def sniff(content):
    """按魔数判断格式。不要相信扩展名 —— 服务端给的可能是 jpg 而我们存成 png。"""
    if content[:8] == b"\x89PNG\r\n\x1a\n":
        return "png", "png"
    if content[:3] == b"\xff\xd8\xff":
        return "jpg", "jpeg"
    if content[:4] == b"RIFF" and content[8:12] == b"WEBP":
        return "webp", "webp"
    if content[:6] in (b"GIF87a", b"GIF89a"):
        return "gif", "gif"
    return "bin", "unknown"


def dimensions(content, kind):
    """尽力解析宽高；解析不出来就返回 (None, None)，不影响主流程。"""
    try:
        if kind == "png":
            # IHDR 紧跟在 8 字节签名 + 4 字节长度 + 4 字节类型之后
            width, height = struct.unpack(">II", content[16:24])
            return width, height

        if kind == "jpeg":
            i = 2
            while i < len(content) - 9:
                if content[i] != 0xFF:
                    i += 1
                    continue
                marker = content[i + 1]
                # SOF0..SOF15（排除 C4/C8/CC 这几个非 SOF 标记）
                if marker in (0xC0, 0xC1, 0xC2, 0xC3, 0xC5, 0xC6, 0xC7,
                              0xC9, 0xCA, 0xCB, 0xCD, 0xCE, 0xCF):
                    height, width = struct.unpack(">HH", content[i + 5: i + 9])
                    return width, height
                if marker in (0xD8, 0xD9) or 0xD0 <= marker <= 0xD7:
                    i += 2
                    continue
                seg_len = struct.unpack(">H", content[i + 2: i + 4])[0]
                i += 2 + seg_len
    except (struct.error, IndexError):
        pass

    return None, None


def safe_name(raw_name, ext):
    """文件名清洗：只接受纯文件名，杜绝 ../ 与盘符。"""
    name = _BAD_NAME.sub("_", (raw_name or "").strip()).strip(" .")

    if not name:
        return "image_" + time.strftime("%Y%m%d_%H%M%S") + "." + ext

    # 后缀不对（或者被清洗成了奇怪的形状，例如 "../../evil:name" → "_.._evil_name"，
    # 最后那个点会被 splitext 当成扩展名）时补上真实后缀。
    # 否则会出现「一张 PNG 存成了没有扩展名的文件」，双击打不开。
    suffix = os.path.splitext(name)[1].lstrip(".").lower()

    if suffix not in ("png", "jpg", "jpeg", "webp", "gif"):
        name += "." + ext

    return name


def dedupe(path):
    """
    同名文件已存在时追加 _2 / _3。绝不覆盖 ——
    上一次生成的那张可能已经是用户唯一的一份。
    """
    if not os.path.exists(path):
        return path

    stem, ext = os.path.splitext(path)
    n = 2

    while n < 1000:
        candidate = "%s_%d%s" % (stem, n, ext)
        if not os.path.exists(candidate):
            return candidate
        n += 1

    return path


# ----------------------------------------------------------------------
# 主流程
# ----------------------------------------------------------------------

def main():
    started = time.time()

    prompt = arg("prompt")

    if not prompt:
        return fail("缺少必填参数 prompt",
                    "prompt 用来描述画面，例如「海边落日下的旧灯塔，胶片质感」。")

    key = cfg("LIQIU_API_KEY") or cfg("LIQIU_IMAGE_KEY")

    if not key:
        return fail(
            "没有配置 LIQIU_API_KEY",
            "在主窗口菜单「文件 → 编辑配置文件 .env」里填上「生图 API Key」，"
            "保存后重启程序（或者点窗口里的「保存并重启」）。")

    base = cfg("LIQIU_BASE_URL", DEFAULT_BASE).rstrip("/")

    # 允许两种写法：写到 /v1，或者直接写完整的 /images/generations
    url = base if base.endswith("/images/generations") else base + "/images/generations"

    model = cfg("LIQIU_IMAGE_MODEL", DEFAULT_MODEL)
    size = arg("size") or cfg("LIQIU_IMAGE_SIZE", "auto")
    quality = arg("quality") or cfg("LIQIU_IMAGE_QUALITY", "auto")

    payload = {
        "model": model,
        "prompt": prompt,
        "size": size,
        "quality": quality,
        # 固定要 base64：url 通常会过期，而且还要再多一次下载失败的可能
        "response_format": "b64_json",
    }

    body = json.dumps(payload, ensure_ascii=False).encode("utf-8")

    request = urllib.request.Request(url, data=body, method="POST")
    request.add_header("Authorization", "Bearer " + key)
    request.add_header("Content-Type", "application/json")
    request.add_header("Accept", "application/json")

    try:
        with urllib.request.urlopen(request, timeout=API_TIMEOUT) as response:
            raw = response.read()
    except urllib.error.HTTPError as exc:
        return fail("接口返回 HTTP %d" % exc.code,
                    hint_for_status(exc.code),
                    detail=snippet(exc.read()))
    except urllib.error.URLError as exc:
        return fail("连不上 %s：%s" % (url, exc.reason),
                    "先确认网络通畅；如果配了代理，检查代理是否还活着。")
    except TimeoutError:
        return fail("请求超时（%d 秒）" % API_TIMEOUT,
                    "4K 出图本来就慢。可以先把尺寸降到 1024x1024 试一张。")

    try:
        data = json.loads(raw.decode("utf-8", "replace"))
    except json.JSONDecodeError:
        return fail("接口返回的不是 JSON", detail=snippet(raw))

    if isinstance(data, dict) and data.get("error"):
        err = data["error"]
        message = err.get("message") if isinstance(err, dict) else str(err)
        return fail("接口报错：" + str(message), detail=snippet(raw))

    items = data.get("data") if isinstance(data, dict) else None

    if not items:
        return fail("响应里没有 data 数组", detail=snippet(raw))

    first = items[0] or {}
    b64 = first.get("b64_json")
    image_url = first.get("url")

    if b64:
        try:
            content = base64.b64decode(b64)
        except ValueError:
            return fail("b64_json 解不开（可能被截断或包含非法字符）",
                        detail="base64 长度 %d" % len(b64))
    elif image_url:
        try:
            with urllib.request.urlopen(image_url, timeout=DOWNLOAD_TIMEOUT) as response:
                content = response.read()
        except (urllib.error.URLError, TimeoutError) as exc:
            return fail("图片下载失败：%s" % exc, detail=image_url)
    else:
        return fail("响应里既没有 b64_json 也没有 url", detail=snippet(raw))

    if not content:
        return fail("服务端返回了空图片", detail=snippet(raw))

    # ---------- 落盘 ----------
    root = os.path.abspath(os.getcwd())
    target_dir = os.path.abspath(os.path.join(root, cfg("LIQIU_IMAGE_DIR", "art")))

    # 目录必须落在工作台内：插件写文件同样受路径沙箱约束
    if target_dir != root and not target_dir.startswith(root + os.sep):
        return fail("保存目录跑出了工作台：%s" % target_dir,
                    "LIQIU_IMAGE_DIR 必须是工作台内的相对路径，例如 art 或 images/art。")

    os.makedirs(target_dir, exist_ok=True)

    ext, kind = sniff(content)
    width, height = dimensions(content, kind)

    path = dedupe(os.path.join(target_dir, safe_name(arg("filename"), ext)))

    try:
        with open(path, "wb") as handle:
            handle.write(content)
    except OSError as exc:
        return fail("写文件失败：%s" % exc, detail=path)

    rel = os.path.relpath(path, root).replace(os.sep, "/")

    result = {
        "ok": True,
        "path": rel,
        "bytes": len(content),
        "format": kind,
        "model": model,
        "size": size,
        "quality": quality,
        "prompt": prompt[:200],
        "elapsed_ms": int((time.time() - started) * 1000),
    }

    if width and height:
        result["width"] = width
        result["height"] = height

    if kind == "bin":
        result["warning"] = "响应不是常见图片格式，按二进制存了；先打开确认一下。"

    if first.get("revised_prompt"):
        result["revised_prompt"] = str(first["revised_prompt"])[:300]

    print(json.dumps(result, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
