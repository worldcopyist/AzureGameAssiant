# 蓝天游戏助手 · 提交规范

| 项目 | 内容 |
| --- | --- |
| 文档编号 | AGS-GIT-001 |
| 版本 | v1.0 |
| 状态 | 生效 |
| 作者 | Qoder |
| 适用范围 | 本仓库全部提交、分支、Issue、Pull Request、版本标签 |
| 最后更新 | 2026-11-07 |

本规范以 Conventional Commits 1.0.0、Semantic Versioning 2.0.0、Keep a Changelog 1.1.0 为基础，结合本项目"人类 + 多 Agent 协作"的实际情况制定。

---

## 0. 强制规则摘要

1. **Agent 提交必须标注身份**：Qoder 产生的提交固定写 `Agent: Qoder`；其他 Agent 使用自己的正式名称。不得省略、不得冒用、不得写成笼统的 "AI"。
2. Git 作者保持人类账号不变，Agent 身份只写在提交信息尾部（trailer）。
3. `main` 受保护：所有变更走短分支 + Pull Request + Squash 合并。
4. 禁止 `--no-verify`、禁止强推 `main`、禁止提交密钥与大文件。
5. 每个任务结束必须更新 `docs/handoff.md`，并在 PR 自检清单中确认。

## 1. Agent 身份标注

### 1.1 规则

| 提交者 | Footer 必须包含 |
| --- | --- |
| Qoder | `Agent: Qoder` |
| 其他 Agent | `Agent: <该 Agent 的正式名称>`，例如 `Agent: Claude`、`Agent: Cursor` |
| 人类开发者 | 可选；若参与协作可写 `Agent: Human` 或省略 |

补充约束：

- **名称必须与实际执行提交的 Agent 一致**。Qoder 不得写成其他名称，其他 Agent 也不得写成 Qoder。
- 一个提交由多个 Agent 协作完成时，每个 Agent 各写一行，主要执行者在前：

```
Agent: Qoder
Agent: Claude
```

- `Agent:` 必须位于提交信息的最后一个段落（trailer 区），每行一个，不与正文混排。
- Git 的 `author` / `committer` 始终是仓库配置的人类账号（当前为 `worldcopyist`）。**禁止**为了标注 Agent 而临时修改 `user.name` / `user.email`。
- Squash 合并时，必须把分支内所有 Agent 标注去重后保留到 squash 提交中；不得只保留最后一个。
- PR 描述与 Issue 评论同样在末尾标注执行 Agent，格式相同。

### 1.2 正误示例

正确：

```
feat(detection): 增加内存压力信号

在提交率超过 90% 且可用内存低于 1GB 时触发疑似卡顿，
与抖动、CPU 尖峰信号共用事件合并窗口。

Closes #12
Agent: Qoder
```

错误：

```
feat: update

Agent: qoder          # 名称大小写不一致
```

```
fix: 修复采样崩溃
（缺少 Agent trailer）
```

```
feat: 新功能
Agent: AI             # 笼统名称，无法追溯
```

## 2. 提交信息格式

```
<type>(<scope>): <subject>

<body>

<footer>
```

- 第一行为标题，不超过 72 个字符，其中 `<subject>` 建议不超过 50 个字符。
- 标题与正文之间空一行；正文与 footer 之间空一行。
- 标题不以句号结尾，不使用 emoji。
- 正文使用中文，说明"为什么改、改了什么、有什么影响"，每行不超过 72 个字符。
- footer 用于关联 Issue、破坏性变更与 Agent 标注。

### 2.1 type

| type | 含义 | 示例 |
| --- | --- | --- |
| `feat` | 新增用户可见功能 | 时间轴、热键、归因报告 |
| `fix` | 修复缺陷 | 采样崩溃、内存泄漏、数值错误 |
| `docs` | 仅文档变更 | 需求、开发、交接、README |
| `style` | 不影响行为的格式调整 | 空白、缩进、分号 |
| `refactor` | 不改变外部行为的重构 | 拆分类、替换实现 |
| `perf` | 性能优化 | 降低采样分配、减少内存 |
| `test` | 测试相关 | 新增/修正测试 |
| `build` | 构建系统或依赖 | csproj、NuGet、AOT 配置 |
| `ci` | 持续集成配置 | 工作流、脚本 |
| `chore` | 杂项维护 | 版本号、忽略规则、目录整理 |
| `revert` | 回滚提交 | 必须引用被回滚的提交哈希 |

### 2.2 scope

| scope | 范围 |
| --- | --- |
| `collector` | 采集进程、PDH、进程采样、共享内存 |
| `core` | 数据模型、环形缓冲、判定、会话、设置、日志 |
| `detection` | 卡顿信号、事件状态机、归因 |
| `gpu` | GPU Engine、NVML / ADL / IGCL |
| `ipc` | 命名管道、共享内存协议 |
| `storage` | 会话文件、留存、导出 |
| `ui` | WPF 窗口、控件、时间轴、托盘、悬浮条 |
| `i18n` | 中英文资源 |
| `theme` | 明暗主题与视觉 |
| `build` | 构建、发布、打包 |
| `docs` | 文档 |
| `repo` | 仓库级配置 |

scope 可省略（如 `docs: 修正错别字`），但涉及上述模块时必须写。

### 2.3 subject

- 使用祈使语气，动词开头：`增加`、`修复`、`移除`、`重构`、`降低`。
- 描述结果，不描述过程：写"降低采样分配"，不写"尝试优化一下"。
- 一个提交只做一件事；标题应能独立说明变更。

### 2.4 body

- 说明动机与影响，必要时给出测量数据。
- 涉及性能的变更必须写明前后数值与测量条件。
- 涉及界面变更建议附截图路径或说明。
- 不写"按需求修改"这类无信息量内容。

### 2.5 footer

| trailer | 用途 | 示例 |
| --- | --- | --- |
| `Closes` | 关闭 Issue | `Closes #12` |
| `Refs` | 关联但不关闭 | `Refs #8` |
| `BREAKING CHANGE` | 破坏性变更说明 | `BREAKING CHANGE: 会话格式升级到 v2` |
| `Agent` | Agent 身份（强制） | `Agent: Qoder` |

顺序：业务 trailer 在前，`Agent` 在最后一段。

## 3. 分支规范

| 分支 | 用途 | 命名 |
| --- | --- | --- |
| `main` | 始终可构建、可运行 | 固定 |
| 功能 | 新功能 | `feat/<issue>-<slug>`，如 `feat/12-gpu-vendor` |
| 修复 | 缺陷修复 | `fix/<issue>-<slug>`，如 `fix/31-pipe-reconnect` |
| 文档 | 文档变更 | `docs/<slug>` |
| 性能 | 性能优化 | `perf/<slug>` |
| 重构 | 重构 | `refactor/<slug>` |
| 测试 | 测试补充 | `test/<slug>` |
| 杂项 | 构建、依赖、脚本 | `chore/<slug>` |

规则：

- 全小写、kebab-case，不超过 40 个字符。
- 从最新 `main` 创建；存在 Issue 时带 Issue 编号。
- 生命周期不超过 3 天；过长说明任务拆分不够小。
- 合并后删除远端与本地分支。
- 定期 `git fetch` 并用 `rebase` 同步 `main`；不要 merge `main` 进功能分支。

## 4. Issue 规范

### 4.1 类型与标题

| 类型 | 标题前缀 | 用途 |
| --- | --- | --- |
| 功能 | `[Feature]` | 新能力 |
| 缺陷 | `[Bug]` | 可复现的错误行为 |
| 任务 | `[Task]` | 有明确交付物的工程任务 |
| 调研 | `[Research]` | 技术选型、可行性验证 |
| 技术债 | `[Debt]` | 重构、清理、补测试 |

标题格式：`[类型] 简述`，例如 `[Bug] 游戏模式下采样间隔漂移到 300ms`。

### 4.2 标签

| 维度 | 标签 |
| --- | --- |
| 类型 | `type/feature`、`type/bug`、`type/task`、`type/research`、`type/debt` |
| 优先级 | `priority/P0`（阻塞发布）、`P1`（高）、`P2`（中）、`P3`（低） |
| 模块 | `area/collector`、`area/core`、`area/detection`、`area/gpu`、`area/ipc`、`area/storage`、`area/ui`、`area/i18n`、`area/docs`、`area/build` |
| 状态 | `status/blocked`、`status/needs-info`、`status/ready` |
| 阶段 | `phase/M1`、`phase/M2`、`phase/M3` |

### 4.3 缺陷模板

```markdown
## 环境
- 版本：
- Windows 版本：
- CPU / GPU / 内存：
- 权限：普通 / 管理员

## 复现步骤
1.
2.

## 期望结果

## 实际结果

## 证据
- 截图：
- 会话文件：
- 日志路径：

## 影响
- 频率：必现 / 偶发（约 x 次中 y 次）
- 严重度：阻塞 / 严重 / 一般 / 轻微
```

### 4.4 功能模板

```markdown
## 背景与问题

## 目标

## 方案要点（可选）

## 验收标准
- [ ]

## 不在本次范围

## 关联需求
FR-xxx
```

### 4.5 任务模板

```markdown
## 目标

## 交付物

## 验收方式

## 依赖 / 阻塞
```

### 4.6 就绪与完成

**就绪（Definition of Ready）**

- 有明确的背景、目标与验收标准。
- 标注优先级、模块与阶段。
- 依赖已列出且无未解决的阻塞。
- 工作量可在 3 天内完成；否则拆分。

**完成（Definition of Done）**

- 代码已合并到 `main`，构建通过。
- 新增/修改逻辑有对应测试，或说明无法自动测试的原因。
- 涉及性能红线的变更附测量数据。
- 文档与实现一致；`docs/handoff.md` 已更新。
- 无新增编译警告；无密钥、无生成物、无无关变更。

## 5. Pull Request 规范

### 5.1 基本要求

- 每个 PR 只解决一个问题，关联至少一个 Issue。
- 标题遵循提交信息格式，Squash 后即为提交标题。
- 代码变更（不含测试、文档、锁文件）建议 ≤ 400 行；超过则拆分。
- 必须为可评审状态再发起；草稿用 Draft PR。
- 禁止在 PR 中夹带无关格式化或重构。

### 5.2 模板

```markdown
## 变更摘要
- 

## 关联 Issue
Closes #

## 验证方式
- [ ] `dotnet build` 通过
- [ ] `dotnet test` 通过
- [ ] 手工验证步骤：
- [ ] 性能测量（如涉及）：采集进程 __ MB / __ % CPU；界面 __ MB

## 截图 / 录屏（界面变更）

## 风险与回滚
- 风险：
- 回滚方式：

## 自检清单
- [ ] 只做了一件事，无无关变更
- [ ] 无密钥、日志、会话文件、bin/obj 等生成物
- [ ] 新增逻辑有测试或说明原因
- [ ] 更新了 `docs/handoff.md`
- [ ] 文档与实现一致
- [ ] 提交信息含 `Agent: <名称>`

Agent: Qoder
```

### 5.3 评审

- 至少 1 名评审者批准；单人开发时至少完成一次自审清单，并鼓励引入其他 Agent 做交叉评审。
- 评审关注：正确性、性能红线、安全性（注入/权限/文件输入）、可维护性、文档同步。
- 评审意见分 `must`（必须改）与 `nit`（可选）；`must` 全部解决后才能合并。
- 作者不得自行批准自己的 PR（单人项目例外：需在 PR 中贴出自审结论）。

### 5.4 合并

- 使用 **Squash and merge**，保持 `main` 线性历史。
- Squash 提交标题使用 PR 标题；正文保留关键说明与所有 `Agent:` 标注（去重）。
- 合并后删除分支。
- 禁止 merge commit、禁止 rebase 后强推已评审分支。
- 合并前必须通过构建与测试；性能相关 PR 必须附测量结果。

## 6. 版本与发布

- 遵循 Semantic Versioning：`MAJOR.MINOR.PATCH`，开发期为 `0.x`。
- 里程碑版本：`v0.1.0`（M1）、`v0.5.0`（M2）、`v1.0.0`（M3）。
- 标签使用附注标签：`git tag -a v0.1.0 -m "v0.1.0: 核心采集与时间轴"`。
- 每个版本更新 `CHANGELOG.md`（Keep a Changelog 格式：Added / Changed / Fixed / Removed）。
- 发布前检查：NFR 测量、交接文档、README 状态、便携 ZIP 可解压运行。
- 发布提交示例：

```
chore(release): 发布 v0.1.0

- 核心采集、时间轴、托盘与设置可用
- 连续运行 30 分钟通过，内存与 CPU 达标

Agent: Qoder
```

## 7. 禁止事项

- 使用 `--no-verify` 跳过钩子；使用 `--no-gpg-sign` 绕过签名（除非用户明确要求）。
- 对 `main` 强推、改写已发布提交、删除他人分支。
- 提交密钥、令牌、证书、个人路径、会话文件、日志、`bin/`、`obj/`、`artifacts/`。
- 提交超过 5MB 的二进制文件（确需版本化时先讨论并记录原因）。
- 一个提交混合多个无关变更。
- 提交信息缺失 `Agent:`、冒用他人身份、使用笼统的 "AI"。
- 在 `main` 上直接开发（初始化提交除外）。
- 提交无法构建的代码。

## 8. 提交前检查清单

- [ ] 变更范围单一，标题能独立说明目的
- [ ] 构建与测试通过
- [ ] 无调试代码、临时日志、注释掉的代码
- [ ] 无密钥、无本地路径、无会话/日志文件
- [ ] 性能相关变更附前后数据
- [ ] `docs/handoff.md` 已更新
- [ ] 提交信息格式正确，footer 含 `Agent: <名称>`
- [ ] 分支名符合规范，已同步最新 `main`
