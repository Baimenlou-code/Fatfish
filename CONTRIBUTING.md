# 参与 FatFish

感谢愿意花时间。这个仓库很小，规则也少，但下面几条是**踩过坑之后定的**，不是形式主义。

---

## 一、分支：`main` 是唯一长期分支

| 类型 | 命名 | 寿命 |
|---|---|---|
| 主干 | `main` | 永久 |
| 功能 | `feat/预设热重载`、`feat/sites` | 几天，合并即删 |
| 修复 | `fix/net-433`、`fix/approval-strict` | 同上 |
| 文档 | `docs/manual-v2` | 同上 |
| 杂务 | `chore/icon` | 同上 |

**不要开长期并行大分支。** 这个仓库历史上开过三条：

- `python` —— 完全合并进 main 却一直留着
- `manual` —— 只放《技术手册》的分支，基于已被淘汰的 Python 代码
- `ENforEnglishi` —— 英文版 Python 代码

后两条最终都变成了「谁也不敢删、谁也不敢合」的僵尸分支，落后主线 20~30 个提交。
它们已在 2026-11-20 归档为 tag 并删除分支：

```
archive/manual-v1.2.3      → 《技术手册》Python 版最后状态
archive/en-python-1.1.1    → 英文版 Python 代码最后状态
```

> **存档用 tag，不用分支。** tag 不可变、不占分支列表、随时 `git checkout` 取回。

仓库已开启 **合并后自动删除源分支**，所以正常流程下你不需要手动清理。

## 二、发布：用 tag，不用 `release/*` 分支

```
v2.1.1  ← 打 tag，然后建 GitHub Release 附安装包
```

不要开 `release/2.1` 这种分支 —— 只有当**同时维护多个版本**时才需要，本项目没有这个需求。

## 三、提交信息

```
type(scope): 一句话摘要（30 字内，说清"做了什么"）

正文：为什么改。写根因、写踩到的坑、写怎么验证的。
```

`type` 取 `feat` / `fix` / `docs` / `chore` / `ci` / `test` / `refactor`。

反面例子（这个仓库里真实出现过）：

```
修复问题
```

正面例子：

```
ci: restore 带上 Configuration=Release，修复 NETSDK1047

FatFish.App.csproj 的 RuntimeIdentifier 只在 Release 下生效，
按默认 Debug 还原出来的 project.assets.json 缺 net10.0-windows/win-x64
目标，Release 构建因此报 NETSDK1047。删 obj 后本地完整复现并验证修复。
```

**正文里请写"怎么验证的"** —— 这个仓库有过「发布日志说成功、实际跑的是旧 exe」的先例，
所以"我跑过了"比"应该没问题"值钱得多。

## 四、合并前必须满足

1. `dotnet build FatFish.slnx -c Release` **0 错误**
2. `dotnet test FatFish.slnx -c Release` **全绿**
3. CI（`build & test (windows)`）通过 —— `main` 已设为必需检查

本地跑测试时注意：有两条用例只在**中文代码页**下有意义（`CjkFact`），
英文 Windows 上会显示 Skip —— 这是设计如此，不是漏测。

## 五、不要提交的东西

| 路径 | 原因 |
|---|---|
| `.env` | 含明文 key，已列入 `.gitignore` |
| `config/presets/` | 预设文件里是明文 key |
| `workspace/` `logs/` `_backup/` | 运行时产物 |

> 仓库已开 **secret scanning + push protection**：真把 key 推上去会被直接拒绝。
> 但别指望它 —— 它只认得已知格式的凭据。

## 六、许可

本项目以 **Apache-2.0** 发布（见 `LICENSE`），第三方组件归属见 `NOTICE`。

按 Apache-2.0 §5，**你提交的代码默认也按同一许可授权**，不需要额外签 CLA。
提交即表示你同意这一点。

---

有问题直接开 Issue，不用先问能不能提。
