# 蓝天游戏助手 · 交接文档

> **强制规则：本文件必须在每个任务结束前更新。**
> 任何 Agent 接手项目时，先读本文件，再读 [需求文档](requirements.md)、[开发文档](development.md) 与 [开源调研](research.md)。
> 只记录“当前事实 + 下一步”，不要把设计细节复制进来；设计以开发文档为准，需求以需求文档为准。

| 项目 | 内容 |
| --- | --- |
| 文档编号 | AGS-HANDOFF |
| 当前版本 | v0.0.2 |
| 最后更新 | 2026-11-07 |
| 最后更新人 | Qoder |
| 仓库状态 | 已初始化，文档完成，无代码 |

---

## 1. 当前快照

| 项 | 值 |
| --- | --- |
| 产品阶段 | 文档阶段（M0） |
| 最新里程碑 | 无（M1 未开始） |
| 可运行产物 | 无 |
| 代码分支 | `main` |
| 当前提交 | `docs: 初始化项目文档与仓库结构`（下一次提交为文档改版） |
| 未提交变更 | 本次文档改版（需求 v1.1、开发 v1.1、调研新增、README、交接、提交规范） |
| 阻塞事项 | 无 |

## 2. 环境

| 项 | 值 |
| --- | --- |
| 操作系统 | Windows 10.0.26100（x64） |
| .NET SDK | 9.0.100（目标框架 `net8.0`） |
| Git | 2.54.0.windows.1 |
| 默认分支 | `main` |
| Git 身份 | `worldcopyist <2767263382@qq.com>` |
| 仓库路径 | `C:\Users\worldcopyist\Documents\Qoder\2026-11-07\275e2b44` |
| 远端 | 无（暂不上传 GitHub） |

## 3. 已完成

- [x] 需求访谈：确认技术栈、采集方式、判定策略、性能预算、界面形态、分期、命名、提交标注方式。
- [x] 需求文档 `docs/requirements.md`（FR 21 项、NFR 7 类、验收与风险）。
- [x] 开发文档 `docs/development.md`（架构、IPC、采集、帧管线、传感器边车、AI 模块、数据模型、算法、UI、构建、测试、编码规范）。
- [x] 开源调研 `docs/research.md`（竞品矩阵、复用边界、许可证义务、明确不复用对象）。
- [x] 提交规范 `docs/commit-guidelines.md`。
- [x] README 索引、`.gitignore`、`.gitattributes`。
- [x] Git 仓库初始化与首次提交（`Agent: Qoder`）。
- [x] 方向调整：引入 PresentMon 帧级真相、LHM 厂商传感器边车、OpenAI 兼容 LLM 分析；同步修订隐私、权限、分期与验收。

## 4. 进行中

无。下一个任务从 M1 开始；M2-0 / M3-0 为技术验证节点，进入对应阶段前必须先做。

## 5. 下一步（按顺序）

1. **M1-1 解决方案骨架**：创建 `AzureGameAssistant.sln`、`src/` 五个项目（Core / Collector / Sensors / UI / Tests）、`Directory.Build.props`、`.editorconfig`；Core/Collector 开启警告即错误。
2. **M1-2 Core 基础设施**：数据模型、`RingBuffer`、`SettingsService`（schemaVersion=2）、`FileLogger`，并补单元测试。
3. **M1-3 采集进程**：PDH 系统指标、`NtQuerySystemInformation` 进程采样、GPU Engine 解析、共享内存 v2 写入、命名管道 v2 服务。
4. **M1-4 界面进程**：应用外壳、托盘（`Shell_NotifyIcon`）、主窗口、系统时间轴控件、设置页、i18n 基础设施。
5. **M1-5 打包与测量**：`build/measure.ps1`、便携目录结构（先不含 PresentMon/LHM）。
6. **M1-6 验收**：连续 30 分钟运行，对照需求文档 NFR 测量内存/CPU/磁盘。
7. **M2-0 PresentMon 验证**（进入 M2 前）：锁定版本与 SHA256、核对参数/列名、验证权限模型、3 款游戏实测、评估反作弊兼容性。
8. **M3-0 传感器验证**（进入 M3 前）：LHM GPU-only、确认不加载内核驱动、三厂商真机、内存/CPU 预算。
9. **M3-0 AI 验证**（进入 M3 前）：OpenAI 兼容接口、3 家云 + 1 个本地模型、schema 与危险建议过滤。

每个步骤完成后：更新本文件 → 按提交规范提交 → 在交接日志追加一行。

## 6. 关键决策（不要再重复讨论）

| 决策 | 结论 | 依据 |
| --- | --- | --- |
| 技术栈 | C# / .NET 8 + WPF；采集进程 NativeAOT | 用户确认；内存预算 |
| 进程模型 | 采集与 UI 分离，共享内存 + 命名管道；传感器独立边车 | ADR-001 / 004 / 005 / 014 |
| 系统数据源 | PDH 英文计数器 + NT 进程 API | ADR-006 |
| 帧数据源 | 随包 Intel PresentMon 控制台 exe，子进程 + CSV 解析 | 用户要求帧级真相；ADR-013 |
| 厂商 GPU 传感器 | LibreHardwareMonitorLib 边车，仅 GPU 硬件组，不加载内核驱动 | 用户要求厂商原生；ADR-014 |
| 权限模式 | 默认浅模式免提权；深模式显式提权后启用帧采集 | 用户确认低骚扰；ADR-019 |
| 卡顿判定 | 帧尖峰优先 + 抖动 + 资源尖峰 + 显存/内存/磁盘压力 + 热键 | 用户确认；FR-006 |
| 数据留存 | 15 分钟系统环形缓冲 + 3 分钟帧环形缓冲；事件前后各 30 秒落盘 JSONL.gz | 用户确认；ADR-018 |
| 采样 | 常规 1s / 游戏模式 200ms（自动识别或手动指定触发） | 用户确认 |
| 性能红线 | 采集浅 ≤30MB / ≤0.3%；深 ≤45MB / 自身 ≤0.5%，合计 ≤1.0%；UI 托盘 ≤60MB、主窗口 ≤80MB | 用户确认 |
| 界面 | 托盘 + 可选悬浮条 + 主窗口；中英双语；明暗跟随系统；极简 | 用户确认 |
| 分发 | 便携 ZIP；三阶段 v0.1 / v0.5 / v1.0 | 用户确认 |
| 命名 | `AzureGameAssistant`，进程 `AzureGameAssistant.exe` / `.Collector.exe` / `.Sensors.exe` | 用户确认 |
| AI 接口 | OpenAI 兼容 `/v1/chat/completions` + 自定义 Base URL，BYOK | 用户要求通用 LLM 接口；ADR-015 |
| AI 数据边界 | 本地事实层 + 默认脱敏 + 发送前预览 + 手动触发 + 预算上限 | 隐私与成本；ADR-017 |
| API Key | Windows Credential Manager | ADR-016 |
| 默认联网 | 禁止；仅用户显式开启 AI 后访问其确认的地址 | 用户此前“纯本地”要求，按新需求放宽为“本地优先” |
| 提交标注 | 人类作者不变，footer 加 `Agent: Qoder`；其他 Agent 用自己名称 | 用户确认 |
| 分支模型 | 主干 + 短分支 + PR Squash | 用户确认 |
| 第三方分发 | PresentMon MIT、LHM MPL-2.0；固定版本 + SHA256 + 许可证清单 | 调研结论；ADR-020 |

## 7. 已知问题与限制

| 编号 | 问题 | 影响 | 处理 |
| --- | --- | --- | --- |
| K-01 | 尚未验证 30MB 内存预算能否达成 | 核心卖点风险 | M1 第一版采集进程完成后立即测量；超预算优先裁剪 |
| K-02 | PDH GPU 计数器在不同驱动上实例名/精度可能不同 | GPU 曲线缺失 | M1 做能力探测与降级；M3 厂商边车补足 |
| K-03 | 全屏独占游戏可能拦截全局热键 | 手动标记失效 | 文档说明；提供托盘/悬浮条入口 |
| K-04 | 本机仅安装 .NET SDK 9.0.100 | 构建 `net8.0` 需联网还原目标包 | 首次 `dotnet restore` 需可用网络 |
| K-05 | 许可证、代码签名未定 | 不影响开发 | M3 前决策；需兼容 MIT / MPL-2.0 |
| K-06 | PresentMon 需要管理员或 Performance Log Users | 普通用户无帧数据 | 浅/深模式；显式提权；不自动弹 UAC |
| K-07 | 部分反作弊可能对 ETW 敏感 | 用户账号顾虑 | 只运行官方 PresentMon，不注入；文档不承诺全兼容 |
| K-08 | LHM GPU-only 是否绝对不加载内核驱动待验证 | 违反“无驱动”承诺 | M3-0 验证；检测到驱动即停边车并降级 PDH |
| K-09 | 各 LLM 供应商兼容接口存在细节差异 | AI 调用失败 | 只依赖 `chat/completions` 最小子集；3 家云 + 本地联调 |
| K-10 | 含帧会话体积可能超过 12MB 预算 | 存储增长 | M2 实测；必要时降低帧批字段或提高压缩率 |
| K-11 | OCAT 许可证不明确 | 不可复用其代码 | 只参考思路；代码评审检查来源 |

## 8. 如何验证当前状态

```bash
cd "C:/Users/worldcopyist/Documents/Qoder/2026-11-07/275e2b44"
git log --oneline --decorate -n 5
git status
ls docs
```

预期：`main` 上至少有初始化提交与本次文档改版提交；工作区干净；`docs/` 下有 `requirements.md`、`development.md`、`research.md`、`handoff.md`、`commit-guidelines.md`。

关键文档自检：

- 需求文档包含 FR-019（帧级）、FR-020（LLM）、FR-021（AI 隐私/成本/安全）。
- 开发文档包含 ADR-013（PresentMon）、ADR-014（LHM 边车）、ADR-015（AI 接口）。
- 调研文档包含 PresentMon / CapFrameX / LHM 的许可证与复用边界。
- 提交规范包含 `Agent: Qoder` 强制规则与第三方/密钥禁令。

## 9. 交接日志

| 日期 | Agent | 内容 | 提交 |
| --- | --- | --- | --- |
| 2026-11-07 | Qoder | 完成需求访谈、四份文档、README 与 Git 初始化 | `docs: 初始化项目文档与仓库结构` |
| 2026-11-07 | Qoder | 完成开源调研；引入 PresentMon 帧级真相、LHM 传感器边车、OpenAI 兼容 LLM 分析；修订需求/开发/README/提交规范 | `docs: 引入帧级真相与 AI 分析架构`（待提交） |

## 10. 下次交接模板

复制以下内容到第 9 节上方，并覆盖第 1–7 节：

```markdown
## 1. 当前快照
- 阶段 / 里程碑：
- 可运行产物：
- 分支 / 提交：
- 未提交变更：
- 阻塞事项：

## 3. 本次完成
- [ ]

## 4. 进行中
- [ ] 任务： / 进度： / 卡点：

## 5. 下一步
1.

## 7. 新增问题
| 编号 | 问题 | 影响 | 处理 |
```

**更新检查清单**

- [ ] 第 1 节快照与 `git log`、`git status` 一致
- [ ] 已完成项可复现（给出命令或产物路径）
- [ ] 下一步按优先级排序，标注对应需求编号
- [ ] 新增决策写入第 6 节，并同步开发文档 ADR
- [ ] 新增限制写入第 7 节
- [ ] 交接日志追加一行
- [ ] 按提交规范提交，footer 含 `Agent: <名称>`
