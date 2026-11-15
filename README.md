# 蓝天游戏助手 · AzureGameAssistant

> 一款本地优先、低开销的 Windows 性能监控与卡顿归因工具。
> 游戏或高负载工作突然掉帧时，它能告诉你：卡在哪一帧、当时谁在占资源、最可能的原因是什么。

**当前状态：v0.1.0-dev · 系统 / 帧 / 传感器采集、AI 手动分析（OpenAI 兼容）与 MCP 只读服务已落地**

[GitHub 仓库](https://github.com/worldcopyist/AzureGameAssiant) · [Releases 下载](https://github.com/worldcopyist/AzureGameAssiant/releases) · [第三方与开源声明](THIRD-PARTY-NOTICES.md) · 许可证 [GPL-3.0](LICENSE)

---

## 为什么做它

很多掉帧和卡顿并不是“电脑不行”，而是某一刻发生了可观测的资源事件：后台程序突然抢 CPU、显存吃满、内存耗尽触发换页、磁盘或驱动造成系统调度停顿。任务管理器只能看到“现在”，看不到“刚才那一秒”；只看资源占用，也解释不了“为什么这一帧慢了”。

本项目在后台以极低开销持续采样 CPU、内存、GPU、显存、磁盘与逐帧呈现数据，把它们放进内存环形缓冲；一旦检测到卡顿，就把卡顿前后各 30 秒的数据固化成会话文件，并在时间轴上还原现场、给出进程级与帧级归因。

## 核心能力

- **后台常驻采集**：CPU、内存、提交量、页面文件、GPU 占用、显存、磁盘、系统队列。
- **帧级真相**：复用 Intel PresentMon 采集 frametime、CPU/GPU 等待、显示延迟、1% low / 0.1% low；不自研 ETW、不注入游戏。
- **双采样频率**：常规 1 秒 / 次；识别到游戏全屏时切到 200 毫秒 / 次。
- **15 分钟系统环形缓冲 + 3 分钟帧环形缓冲**：平时不写盘，检测到卡顿才落盘，磁盘几乎无感。
- **卡顿判定**：帧尖峰优先，叠加定时器抖动、资源尖峰、显存/内存/磁盘压力，另有全局热键手动标记。
- **进程级 + 帧级归因**：CPU / GPU / 显存 / 内存 / 磁盘 / 帧节奏 / 着色器编译 / 资产流送 / 热降频，生成中英文报告。
- **厂商原生 GPU 传感器**：可选边车复用 LibreHardwareMonitor（内部调用 NVML / ADL / IGCL），读取温度、功耗、频率、风扇、显存；不加载内核驱动。
- **可选 LLM 深度分析**：支持 OpenAI 兼容接口与本地模型（Ollama / LM Studio），基于脱敏事实层生成结构化报告与优化建议；默认关闭、默认手动触发、有预算上限。
- **Agent 接入（MCP）**：提供只读 stdio 服务，Qoder、Claude Desktop 等 Agent 可直接读取硬件快照、历史、帧统计与诊断结果；固定 6 个有界工具，不修改系统。
- **时间轴回放**：多指标与帧轨共用一条时间轴，事件标记、缩放、悬停查看同一时刻的全部数值。
- **默认不联网**：无遥测、无崩溃上报、无自动更新；AI 分析仅在用户显式开启后使用。

## 性能红线

| 指标 | 目标 |
| --- | --- |
| 采集进程常驻内存 | ≤ 30 MB（浅模式）；≤ 45 MB（深模式，含帧环） |
| 采集进程平均 CPU | ≤ 0.3%（浅） / ≤ 0.5%（深，自身）；深模式合计 ≤ 1.0% |
| PresentMon 子进程 | ≤ 60 MB，额外 CPU ≤ 0.5% |
| 传感器边车 | ≤ 35 MB，CPU ≤ 0.2% |
| 界面进程内存 | 仅托盘 ≤ 60 MB；主窗口 ≤ 80 MB |
| 空闲磁盘写入 | 仅日志，≤ 10 KB / 分钟 |
| 卡顿事件可查看延迟 | ≤ 2 秒 |

> 具体测量与验收方法见 [需求文档 NFR 章节](docs/requirements.md#7-非功能需求)。

## 下载与运行

便携包发布在 [GitHub Releases](https://github.com/worldcopyist/AzureGameAssiant/releases)：

- `AzureGameAssistant-0.1.0-win-x64.zip`：解压即用，内含界面、NativeAOT 采集进程、传感器边车与 Intel PresentMon，无需另装 .NET。
- 系统要求 Windows 10 1909+ / Windows 11 x64；深度帧采集需管理员权限；反作弊敏感游戏可用 `--safe-mode` 启动。
- 源码构建与测试命令见下文「开发与参与」。

## 技术栈

- **采集进程**：C# / .NET 8 + NativeAOT，无窗口，PDH 性能计数器 + NT 进程信息 API。
- **帧数据**：随包分发 Intel PresentMon 控制台程序，子进程 + CSV 流式解析。
- **厂商传感器**：可选 `AzureGameAssistant.Sensors.exe` 边车，LibreHardwareMonitorLib，仅 GPU 硬件组。
- **界面进程**：C# / .NET 8 + WPF，MVVM，自绘时间轴（不引入重型图表库）。
- **进程通信**：共享内存（系统/帧/传感器遥测）+ 命名管道（控制命令）。
- **AI 分析**：OpenAI 兼容接口，BYOK，API Key 存 Windows Credential Manager。
- **Agent 接入**：MCP stdio JSON-RPC 2.0，独立只读进程，不向采集进程引入网络依赖。
- **存储**：gzip 压缩的 JSONL 会话文件，原子写入。
- **目标系统**：Windows 10 1909+ / Windows 11，x64。

## 文档

| 文档 | 内容 |
| --- | --- |
| [需求文档](docs/requirements.md) | 目标、功能需求 FR、非功能需求 NFR、验收标准、分期与风险 |
| [开发文档](docs/development.md) | 架构、IPC、采集、帧管线、传感器边车、AI 模块、数据模型、算法、UI、构建、测试 |
| [开源调研](docs/research.md) | 竞品矩阵、复用边界、许可证义务、明确不复用的对象 |
| [交接文档](docs/handoff.md) | 当前进度、下一步、关键决策、已知问题（每次任务后必须更新） |
| [提交规范](docs/commit-guidelines.md) | Commit / Branch / Issue / PR / 版本发布规范，含 Agent 标注强制规则 |

## 路线图

| 阶段 | 版本 | 范围 |
| --- | --- | --- |
| M1 | v0.1 | PDH + GPU Engine 采集、进程排行、系统环形缓冲、主窗口时间轴、托盘、自启、设置、日志 |
| M2 | v0.5 | PresentMon 帧级真相、帧信号判定、会话落盘、事件列表、帧级归因、热键、游戏自动识别、双语与明暗主题 |
| M3 | v1.0 | 厂商传感器边车、LLM 深度分析、MCP 只读数据服务、悬浮条、Markdown + PNG 导出、留存清理、性能验收、便携 ZIP |

## 隐私

所有采集数据默认仅保存在本机 `%LOCALAPPDATA%\AzureGameAssistant\`。程序默认不发起任何网络请求，不上传硬件信息、进程名或使用数据。会话文件仅由用户主动导出或分享。

AI 分析是可选功能，默认关闭。开启后：

- 首次使用需阅读并确认隐私说明；
- 每次请求前展示将发送的脱敏 JSON 与预计 token/费用；
- 默认只发送事实摘要，不发送原始会话；
- API Key 存入 Windows Credential Manager，不写入配置、日志或报告；
- 可改用 Ollama / LM Studio 等本地模型，完全离线。

AI 分析默认**手动触发**：主界面“AI 分析”按钮打开预览与同意窗口，确认后才发起请求。也可以选择开启“严重事件自动分析”（默认关闭）：仅在完整会话落盘、事件评分达到阈值、日/月预算未耗尽且已同意当前配置时才请求一次，不做后台轮询，也不会在未开启时上传任何数据。

## 安全模式与诊断

对反作弊敏感的网游可使用安全模式启动：

```powershell
AzureGameAssistant.exe --safe-mode
```

安全模式不提权、不加载 PresentMon / ETW 帧采集、不启动传感器边车，只保留 PDH / NT API 级别的系统与进程采样，界面顶部会显示“浅模式（无帧时间轴）”标记。

设置窗口的“导出脱敏诊断包”会生成一个 ZIP，内含最近一次长跑记录的 summary / samples / events / processes / resources、硬件快照与白名单设置摘要。导出前统一脱敏：用户名、绝对路径、UNC 路径、Bearer / `sk-*` 密钥、邮箱与 IPv4 地址都不会进入压缩包。

## Agent 接入（MCP）

先构建只读 MCP 服务：

```powershell
powershell -File build/publish-mcp.ps1
```

产物为 `artifacts/mcp/AzureGameAssistant.Mcp.exe`。在 Agent 的 MCP 配置中登记 stdio 命令：

```json
{
  "mcpServers": {
    "azure-game-assistant": {
      "command": "C:\\path\\to\\AzureGameAssistant.Mcp.exe"
    }
  }
}
```

可用工具：`get_hardware_snapshot`、`get_history`、`get_frame_stats`、`list_processes`、`diagnose`、`get_collector_status`。全部只读、参数有界；需先启动蓝天游戏助手采集进程。管理员身份可额外读取运行时长与最近错误，非管理员仍可读取共享内存数据（状态标记 `degraded`）。

## 开发与参与

当前 M1 已可运行：采集进程通过 PDH / NT API 采样并写入共享内存，界面进程显示实时卡片、时间轴、进程排行，支持托盘、暂停、设置与中英双语。

```powershell
dotnet build AzureGameAssistant.sln
powershell -File build/run-dev.ps1            # 构建并启动界面
powershell -File build/run-dev.ps1 -Tray      # 直接隐藏到托盘启动
powershell -File build/measure.ps1 -DurationMinutes 1
powershell -File build/publish-mcp.ps1         # 生成 Agent 可接入的 MCP 服务
powershell -File build/package.ps1 -CollectorMode SelfContained   # 无 C++ 工具链时的测试包
```

NativeAOT 采集进程打包需要 Visual Studio Build Tools 的“使用 C++ 的桌面开发”工作负载；缺失时 `package.ps1` 会明确报错。

开发环境、架构、构建命令与目录结构详见 [开发文档](docs/development.md)。

所有提交、分支、Issue 与 PR 必须遵守 [提交规范](docs/commit-guidelines.md)；Agent 提交必须在提交信息尾部标注 `Agent: <名称>`，Qoder 的提交固定为 `Agent: Qoder`。

## 许可

本项目以 [GNU General Public License v3.0](LICENSE) 发布，Copyright (C) 2026 worldcopyist。

在 GPL-3.0 条款下，你可以自由使用、修改与再分发；分发修改后的版本时，需同样以 GPL-3.0 开源并提供完整源码。

### 参考与使用的开源项目

| 项目 | 许可证 | 与本项目的关系 |
| --- | --- | --- |
| [Intel PresentMon](https://github.com/GameTechDev/PresentMon) | MIT | 随包分发官方控制台程序，提供逐帧呈现数据 |
| [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) | MPL-2.0 | 通过 `LibreHardwareMonitorLib` 读取厂商 GPU 传感器（未修改） |
| [CapFrameX](https://github.com/CXWorld/CapFrameX) | MIT | 参考 PresentMon 子进程托管、帧环形缓冲与会话元数据设计，未复制代码 |
| [StutterScope](https://github.com/AdenXie/StutterScope) | MIT | 参考离线报告结构，未复制代码 |
| [game-stutter-recorder](https://github.com/jieke7k7k/game-stutter-recorder) | MIT | 参考游戏识别与结论写作思路，未复制代码 |
| [stutter-test](https://github.com/jkoens3/stutter-test) | 无许可证 | 仅采纳卡顿分类概念并净室实现，未复制任何代码 |
| [System Informer](https://github.com/winsiderss/systeminformer) | MIT | 参考进程信息的交互思路，未复制代码 |
| [SidebarDiagnostics](https://github.com/ArcadeRenegade/SidebarDiagnostics) | GPL-3.0 | 参考桌面监控侧栏布局，未复制代码；与本项目许可证一致 |

- 可选的 PawnIO 内核驱动不随包分发，由用户自行安装，为独立程序。
- 完整的组件版本、哈希、许可证全文位置与 GPL-3.0 兼容性说明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)；调研与复用边界见 [开源调研](docs/research.md)。
