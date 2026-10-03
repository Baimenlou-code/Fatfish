# fatfish_core —— 肥鱼功能部件

> 2026-10-02 分两批从 `FATHFISHI.py` 拆出。
> `FATHFISHI.py`：**4848 行 / 242.4 KB → 3262 行 / 171.7 KB（-32.7%）**

---

## 一、拆出了什么

| 批次 | 模块 | 行数 | 内容 | 依赖 |
|---|---|---|---|---|
| ① | `envutil.py` | 54 | `_env_clean / _looks_like_key / _env_int / _env_bool / _env_float` | `os` |
| ① | `cmdcap.py` | 90 | `_strip_ansi / _TeeStream / _cmd_begin / _cmd_finalize / _cmd_abort / _take_cmd_transcript` | `re` |
| ① | `msgs.py` | 213 | 消息分组 / 清洗 / token 估算 / 裁剪 / 代码块抽取 | `re` + `.envutil` |
| ① | `policy.py` | 242 | 报批判定（`_approval_needed`/`_never_auto_approve`）+ 只读 Python 静态判定 | `ast` + `workspace` |
| ② | `qqmode.py` | 973 | **QQ 前置模式**：消息收发 / 附件守卫 / 密码门禁 / 报批改道 / 直通 / 看门狗 | `ui_core` `workspace` `.policy` |
| ② | `streamhk.py` | 170 | **流式胶水层**：何时用流式、何时回退非流式、转圈与正文交接 | `stream_core` |
| ② | `setappl.py` | 75 | **/set 各 applier** | `net_tools` |

合计 **1828 行**搬出主文件。

---

## 二、三条铁律（改代码前必读）

### 1. 函数体逐字搬运
验证方式：对每个搬走的函数做 AST 结构比对（`ast.dump(..., include_attributes=False)`）
→ 第一批 **33/33**、第二批 **qqmode 35/35 + streamhk 7/7** 全部逐字一致。

### 2. 主程序保留同名导入
所以**所有调用点一个字都不用改**，补丁脚本 / 文档里提到的函数名照旧可见。

### 3. 外部依赖用 `bind(**kw)` 注入
模块自己**不认识**主程序的全局量，靠主程序在启动时推过去：

```python
# 模块侧
log = None
def bind(**kw):
    g = globals()
    for _k, _v in kw.items():
        if _v is not None:
            g[_k] = _v

# 主程序侧（FATHFISHI.py 的 _bind_modparts / _sync_modcfg）
_qqmode.bind(log=log, _qq_orig_request_approval=_request_approval,
             _BG_PROMPT_POS=_BG_PROMPT_POS)
```

**配置常量**（`APPROVE_SCOPE`、`AUTO_APPROVE_SCOPE`…）一律**留在主程序**，
由 `_sync_modcfg()` 在启动时与 `/set` 变更后推给模块 —— 留两个副本必然分叉。

---

## 三、踩过的坑（血泪）

| 坑 | 症状 | 处理 |
|---|---|---|
| `_READONLY_ROOT_ALLOW` 模块默认值是 `None` | 只读判定崩在 `TypeError: argument of type 'NoneType' is not iterable` | 改为「常量全部留主程序 + 注入」，一个都不删 |
| `globals()[X] = v` | 搬进模块后写进了**模块自己**的命名空间，主程序读不到 | `setappl` 全改成 `_put(X, v)`，setter 由主程序注入 |
| `_qq_orig_request_approval = globals().get("_request_approval")` | 同上，搬走后 `globals()` 里查不到 → 恒为 `None`，QQ 报批被静默跳过 | 改为 `bind()` 注入 |
| `_BG_PROMPT_POS` 是**运行时反复赋值**的 | 搬走快照 → 看门狗线程永远看到旧值，插话判断失效 | 主程序在 2 处赋值点追加 `_sync_qqmode_pos()` |
| 插入锚点后再按**原始行号**删除 | 行号整体偏移，删错位置导致语法错误 | 改用「一遍扫描：边插边删」 |
| 校验器拿**完整原树**比**截断新树** | 报出几十个假"缺失" | 两边都截断到 `while True:` 同口径再比 |

---

## 四、怎么再加一个模块

1. AST 把目标函数整段取出（含前置注释），照抄进新模块；
2. 为它引用的每个模块级常量写默认值（**能用字面量就用字面量**，别写 `_env_int(...)`）；
3. 在 `FATHFISHI.py` 的 `[SPLIT v1] / [SPLIT v2]` 导入块里加一行；
4. 在 `_sync_modcfg()` 里加同步行（该常量别处也被引用时）；
5. 删掉原定义，然后跑四件事：
   - `ast.parse(new_src)` 语法校验；
   - 同口径顶层名字比对（**预期搬走的进白名单**）；
   - 反查残留引用（主程序是否还有非导入的引用）；
   - 剥掉主循环后 `exec` 一次 + 真启动一次。

---

## 五、回滚

> 📦 下面这些回滚点已于 2026-10-02 从根目录**归档**（移动，未删除）到
> `oldver\_root_bak_cleanup_20261002\`。

```bat
:: 回到拆分前（4848 行）
copy /Y "E:\FATFISH\oldver\_root_bak_cleanup_20261002\FATHFISHI.py.pre_split_20261002_172636.bak" "E:\FATFISH\FATHFISHI.py"
:: 仅回滚第二批（三项拆分）
copy /Y "E:\FATFISH\oldver\_root_bak_cleanup_20261002\FATHFISHI.py.pre_split2.bak" "E:\FATFISH\FATHFISHI.py"
:: 仅回滚「默认工作台改根目录」
copy /Y "E:\FATFISH\oldver\_root_bak_cleanup_20261002\FATHFISHI.py.pre_wsroot.bak" "E:\FATFISH\FATHFISHI.py"
copy /Y "E:\FATFISH\oldver\_root_bak_cleanup_20261002\workspace.py.pre_wsroot.bak" "E:\FATFISH\workspace.py"
```

回滚后 `fatfish_core/` 留着无害（不 import 就没影响）。

> ⚠️ 这些回滚点**早于** 2026-10-02 晚间的两项改动（`roundtime` 落款、退出信号 PID 校验），
> 拿它们回滚会一并丢掉那两项；只想退那两项，用各自的补丁脚本 `--revert`。

---

## 六、已知副作用

- **补丁脚本**：`patch_stream.py --revert` 现在只会删掉 `# [STREAM-PATCH v1]` 那段**说明注释**，
  函数定义在导入块里，**不受影响**（脚本本身也因此失去「撤销流式改造」的能力 ——
  要撤销请用整体备份）。`patch_window.py` 未受影响（`_win_*` 仍在主程序）。
- **`_BG_PROMPT_POS` 必须同步**：主程序里 2 处赋值后面都跟着 `_sync_qqmode_pos()`；
  以后新增赋值点也要跟着加，否则 QQ 看门狗会看到过期位置。
- **`setappl` 的 `_setter`**：写入的是 `__main__` 的全局变量，与拆分前语义一致
  （已实测：`_apply_approve_scope("all")` → 主程序 `APPROVE_SCOPE='all'` 且 policy 同步）。

---

## 七、还剩什么可拆

| 候选 | 行数 | 风险 |
|---|---|---|
| 主循环（`while True:` 内联 ~900 行） | ~900 | 高：直接读写 39 个全局量，要改造成 `def main()` |
| 窗口补丁（`_win_*`） | ~330 | 中：会动到 `patch_window.py` 的锚点 |
| 后台播报 / 状态台路由（`_bg_*` / `_status_mirror` / `_proc_line`） | ~180 | 低 |
| QQ 命令处理 / 工作台命令（`_qq_handle_cmd` / `_handle_ws`） | ~200 | 低 |
