# -*- coding: utf-8 -*-
"""fatfish_core —— 肥鱼主程序的功能部件（从 FATHFISH.py 拆出）

拆分原则：
  · 函数体**逐字搬运**，不改一行逻辑；
  · 外部配置（常量/开关）用「模块级变量 + 主程序启动时注入」的方式传递，
    因此模块可以独立 import、独立自测；
  · 主程序里原有名字全部通过 ``from fatfish_core.xxx import *`` 或显式导入保留，
    外部（补丁脚本、文档、审查员）看到的调用方式完全不变。
"""
__all__ = ["envutil", "cmdcap", "msgs", "policy"]
