# -*- coding: utf-8 -*-
"""demo-clock 插件的脚本。

插件脚本的输入只有环境变量 —— 参数不会被拼进命令行，
所以这里可以放心把用户/模型给的文本当数据用，不需要转义。

约定：
  FATFISH_ARG_<参数名大写>   每个参数一个
  FATFISH_ARGS_JSON          全部参数的 JSON
  FATFISH_PLUGIN_ID          插件 id
  FATFISH_PLUGIN_DIR         插件目录
"""
import datetime
import json
import os
import sys

greeting = os.environ.get("FATFISH_ARG_GREETING") or "你好"

try:
    args = json.loads(os.environ.get("FATFISH_ARGS_JSON") or "{}")
except json.JSONDecodeError:
    args = {}

print(json.dumps({
    "greeting": greeting,
    "now": datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
    "cwd": os.getcwd(),
    "plugin": os.environ.get("FATFISH_PLUGIN_ID"),
    "args": args,
    "python": sys.version.split()[0],
}, ensure_ascii=False))
