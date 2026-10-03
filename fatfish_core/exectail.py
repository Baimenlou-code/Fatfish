#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
exectail.py —— 子程序输出实时 tailer（从 fatfish_watcher.py 抽出，可复用）

背景
----
`exec_tools.py` 每跑一个子进程，都会把 stdout/stderr 实时写入
`logs/YYYY/MM/DD/exec_*.out`。目前有两处消费者：

    1. `fatfish_watcher.py` —— 独立控制台窗口（老形态）
    2. GUI「🖥 监控」面板    —— P1c 新增（编辑器组布局）

两边都需要"只跟新增字节"的 tail 语义，所以把这段逻辑抽到这里，
避免复制两份、日后改一处漏一处。

对外
----
    ExecTailer(root="logs", from_now=True)
        .poll(sink)      # sink(text) 会被逐条调用；返回本次新增的字节数
        .last_paths()    # 最近一轮看到的文件清单（排障用）

要点（沿用 fatfish_watcher 的既有经验）
--------------------------------------
· `from_now=True` 启动时先把已存在文件的当前大小记为基线 —— 否则会把历史
  输出整份回放一遍（表现为"每次启动先把上一轮滚一遍"）。
· 文件被截断 / 重写（size < offset）→ 从头再来。
· 跨零点：同时看「今天」和「昨天」两个目录，防止午夜前后漏读。
· 一切都是**只读**，任何 IO 异常静默跳过，绝不打扰主流程。

作者：肥鱼 / 2026-10-03（P1c）
"""

import os
from datetime import datetime, timedelta

__all__ = ["ExecTailer", "today_dirs", "iter_exec_files"]

DEFAULT_ROOT = "logs"

#: 文件名前缀 / 后缀（exec_tools 的落盘约定）
PREFIX = "exec_"
SUFFIX = ".out"

#: 同一个文件连续投递失败多少次后放弃（防止每轮重复读同一段）
MAX_DELIVER_FAILS = 5


def today_dirs(root=DEFAULT_ROOT, back_days=2):
    """返回最近几天的 exec 输出目录（今天的排前面）。"""
    now = datetime.now()
    out = []
    for delta in range(max(1, int(back_days))):
        d = now - timedelta(days=delta)
        out.append(os.path.join(root, "%04d" % d.year, "%02d" % d.month,
                                "%02d" % d.day))
    return out


def iter_exec_files(directory):
    """列出一个目录下的 exec_*.out（目录不存在则空）。"""
    try:
        names = os.listdir(directory)
    except OSError:
        return []
    out = []
    for name in names:
        if name.startswith(PREFIX) and name.endswith(SUFFIX):
            out.append(os.path.join(directory, name))
    return out


class ExecTailer(object):
    """实时 tail `exec_*.out`，只把**新增**内容交给 sink。

    sink 形如 `callable(text)`；异常会被吞掉，不影响主流程。
    """

    def __init__(self, root=DEFAULT_ROOT, from_now=True, back_days=2):
        self.root = root
        self.back_days = back_days
        self._offsets = {}          # path -> 已读字节
        self._seen = set()          # 已经"报过到"的文件（用于首行标注）
        self._announced = set()
        self._fails = {}            # path -> 连续投递失败次数（见 _tail_one）
        self.last = []              # 最近一轮扫到的文件
        if from_now:
            self._prime()

    # ---------------------------------------------------------- 基线
    def _prime(self):
        """启动基线：把已存在文件的当前大小记为起点（等价 tail -f）。"""
        for d in today_dirs(self.root, self.back_days):
            for path in iter_exec_files(d):
                try:
                    self._offsets[path] = os.path.getsize(path)
                except OSError:
                    pass
                self._seen.add(path)

    # ---------------------------------------------------------- 轮询
    def poll(self, sink):
        """扫一轮，把新内容交给 sink。返回本次新增的字节数。"""
        total = 0
        self.last = []
        for d in today_dirs(self.root, self.back_days):
            for path in iter_exec_files(d):
                self.last.append(path)
                total += self._tail_one(path, sink)
        return total

    # ---------------------------------------------------------- 单个文件
    def _tail_one(self, path, sink):
        try:
            size = os.path.getsize(path)
        except OSError:
            return 0
        offset = self._offsets.get(path, 0)
        if size < offset:                 # 被截断 / 重写 → 从头再来
            offset = 0
        if size == offset:
            return 0
        # 连续投递失败太多次 → 熔断：跳过这段积压、计数清零，继续跟后续输出。
        #   （既不每轮重复读同一段，也不因一次故障把该文件永久拉黑）
        if self._fails.get(path, 0) >= MAX_DELIVER_FAILS:
            self._offsets[path] = size
            self._fails[path] = 0
            return 0
        try:
            with open(path, "r", encoding="utf-8", errors="replace") as f:
                f.seek(offset)
                chunk = f.read()
                new_offset = f.tell()
        except OSError:
            return 0
        if not chunk:
            return 0

        head = ""
        if path not in self._announced:
            head = ("-" * 58 + "\n"
                    + "📤 子程序输出 → %s\n" % os.path.basename(path))

        # ★ 投递成功才推进偏移 —— 失败就留在原地，下一轮重投。
        #   本项目原则是「绝不丢信息」；若在这里无条件推进，
        #   sink 偶发异常就会静默吃掉一段子程序输出。
        okd = True
        if head:
            if self._safe(sink, head):
                self._announced.add(path)   # 头部成功了就不再重复发
            else:
                okd = False
        okd = self._safe(sink, chunk) and okd

        if okd:
            self._announced.add(path)
            self._offsets[path] = new_offset
            self._fails[path] = 0
        else:
            self._fails[path] = self._fails.get(path, 0) + 1
        return len(chunk)

    @staticmethod
    def _safe(sink, text):
        """投递一段文本；返回是否成功（失败会被上层用于"要不要推进偏移"）。"""
        try:
            sink(text)
            return True
        except Exception:
            return False

    # ---------------------------------------------------------- 复位
    def reset(self):
        """清空偏移（下次 poll 会重新按当前大小建基线）。"""
        self._offsets.clear()
        self._announced.clear()
        self._fails.clear()
        self._prime()
