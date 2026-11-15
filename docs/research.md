# 蓝天游戏助手 · 开源调研与复用决策

| 项目 | 内容 |
| --- | --- |
| 文档编号 | AGS-RESEARCH-001 |
| 版本 | v1.0 |
| 状态 | 已确认（2026-11-07） |
| 作者 | Qoder |
| 适用范围 | 技术选型、第三方依赖、许可证合规 |
| 最后更新 | 2026-11-16 |

配套文档：[需求文档](requirements.md) · [开发文档](development.md) · [交接文档](handoff.md)

---

## 1. 结论摘要

本项目**整体不是重复造轮子**，但其中三个子系统已经是成熟轮子，必须复用而不是自研：

| 子系统 | 结论 | 复用对象 |
| --- | --- | --- |
| 帧级呈现数据（frametime / GPU 延迟 / 显示延迟） | 直接复用 | Intel **PresentMon**（MIT） |
| 厂商 GPU 传感器（温度、功耗、频率、显存） | 复用封装层 | **LibreHardwareMonitorLib**（MPL-2.0），其内部调用 NVML / ADL / IGCL |
| 系统计数器与进程枚举 | 复用操作系统接口，不自研驱动 | Windows **PDH** + `NtQuerySystemInformation` |

仍然由本项目自研、且构成差异化的部分：

1. **常驻低开销环形缓冲 + 事件触发自动落盘**（PresentMon 只采帧，不做长期常驻与自动取证）。
2. **帧级数据与系统级数据的统一时间轴**（把 frametime 尖峰与 CPU / GPU / 内存 / 磁盘 / 进程变化对齐到同一时刻）。
3. **跨层归因**（帧证据 + 资源证据 + 进程证据 + 厂商传感器证据）。
4. **可选 LLM 深度分析**（本地规则先给出事实，模型只负责解释与建议，不接触未经脱敏的原始数据）。
5. **极简双语界面与严格内存预算**（现有工具要么功能过重，要么只做单一帧分析）。

## 2. 调研方法

- 通过 GitHub 搜索与仓库 API 检查：README、许可证、语言、最近提交、发行包内容、关键源码目录。
- 重点核对：是否支持 Windows ETW 帧采集、是否封装厂商传感器、是否要求管理员/内核驱动、许可证是否允许分发与商用。
- 许可证以仓库根目录 `LICENSE` / GitHub license API 为准；无许可证仓库一律视为**不可复用代码**，只参考产品思路。

## 3. 项目矩阵

星标数为 2026-11-07 的近似值，仅用于判断活跃度与成熟度。

| 项目 | 许可证 | 约星标 | 技术栈 | 与本项目关系 | 采纳 | 不采纳 |
| --- | --- | --- | --- | --- | --- | --- |
| [Intel PresentMon](https://github.com/GameTechDev/PresentMon) | MIT | 2.7k | C++ / ETW | 帧级真相的权威数据源 | 官方 console 可执行文件、CSV 指标、按进程采集、会话生命周期 | C++ SDK、CEF 覆盖层、服务安装模式 |
| [CapFrameX](https://github.com/CXWorld/CapFrameX) | MIT | 1.4k | C# / .NET 10 | 最成熟的 PresentMon 集成参考 | 子进程托管与重启、CSV 聚合、帧环形缓冲、传感器记录、会话元数据 | 整套 WPF 应用、覆盖层、云端上传 |
| [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) | MPL-2.0 | 9.3k | C# | 厂商 GPU 传感器的成熟封装 | `LibreHardwareMonitorLib` NuGet，GPU-only 边车进程 | 内核驱动、CPU/主板传感器、整体 GUI |
| [StutterScope](https://github.com/AdenXie/StutterScope) | MIT | 新项目 | C# | 最接近的卡顿取证原型 | 离线报告结构、手动开始/停止、容量上限、Top 进程快照 | 不成熟的采集架构、无自动判定 |
| [game-stutter-recorder](https://github.com/jieke7k7k/game-stutter-recorder) | MIT | 新项目 | Python / PySide6 | 中文卡顿结论生成参考 | 游戏自动猜测、NVML 显存、结论写作口吻 | Python 运行时、PySide6 体积 |
| [stutter-test](https://github.com/jkoens3/stutter-test) | **无许可证** | 8 | C# | 卡顿分类维度参考 | 着色器编译 / 资产流送 / 帧节奏 / 热降频分类（仅概念，净室实现） | 任何代码 |
| [OCAT](https://github.com/GPUOpen-Tools/ocat) | 不明确 | 367 | C++ | AMD 生态的捕获/分析工具 | 仅产品思路 | 代码（许可证不明确，禁止复用） |
| [System Informer](https://github.com/winsiderss/systeminformer) | MIT | 16.4k | C++ | 系统与进程监控 | 进程树与句柄信息的交互思路 | 代码与架构（体量远超本项目） |
| [SidebarDiagnostics](https://github.com/ArcadeRenegade/SidebarDiagnostics) | GPL-3.0 | 2.6k | C# / WPF | 桌面监控侧栏 | 仅界面布局思路 | 代码（GPL 传染性，且 2024 后不活跃） |
| [fps_inspector_sdk](https://github.com/Andrey1994/fps_inspector_sdk) | 未核实 | 26 | C++ / ETW | 早期 ETW FPS 库 | 无 | 代码（过时、维护停滞） |

## 4. 逐项采纳决策

### 4.1 帧级真相：复用 PresentMon

**做法**

- 随包分发固定版本的 `PresentMon-<version>-x64.exe`，以子进程方式运行，输出 CSV 到标准输出。
- 采集进程只负责：选择目标进程、启动/监督子进程、流式解析 CSV、写入帧环形缓冲、会话落盘。
- 不自研 ETW consumer，不自研呈现队列分析，不注入游戏进程。

**理由**

- PresentMon 是 Intel 官方工具，直接读取 DXGI / D3D 呈现事件，指标口径与行业工具一致。
- MIT 许可证允许随包分发，只需保留版权与许可声明。
- CSV 列覆盖 `MsBetweenPresents`、`MsCPUBusy`、`MsCPUWait`、`MsGPULatency`、`MsGPUBusy`、`MsGPUWait`、`DisplayLatency`、`MsAnimationError` 等，足以支撑归因。

**边界**

- PresentMon 需要管理员权限或加入“Performance Log Users”组；普通权限下降级为无帧数据的浅模式。
- PresentMon 版本升级可能改变列名与参数，必须锁定版本、记录 SHA256，并在启动时校验。
- 只对目标游戏进程采集，不对全系统采集，避免无关开销。

**2.6.0 实测（2026-11-07）**

- 官方资产 `PresentMon-2.6.0-x64.exe` 已下载并校验：SHA256 `b2a706bc...18f1af`，Authenticode 有效、签名者为 Intel。
- `--help` 确认支持 `--process_id`、`--output_stdout`、`--v2_metrics`、`--terminate_on_proc_exit`、`--stop_existing_session`；默认输出 2.x 指标，时间列为 `CPUStartTime`（自录制开始的毫秒数），1.x 的 `TimeInSeconds` 不存在。
- 非提权运行返回退出码 `6`，stderr 明确要求管理员或 Performance Log Users；与浅/深模式设计一致。
- 3 款游戏实测与反作弊兼容性仍需在用户游戏环境完成，不在本次结论内。

### 4.2 厂商 GPU 传感器：复用 LibreHardwareMonitorLib

**做法**

- 新增可选边车进程 `AzureGameAssistant.Sensors.exe`，引用 `LibreHardwareMonitorLib` NuGet。
- 只启用 `IsGpuEnabled = true`，不启用 CPU、主板、控制器、存储等硬件组。
- 通过 NVML / ADL / IGCL 读取温度、功耗、频率、显存、风扇等厂商原生指标。
- 通过独立共享内存把传感器数据提供给采集进程与界面。

**理由**

- 用户要求“GPU 传感器尽量使用厂商自带的”；LHM 已经封装三家厂商接口，重复实现 NVML/ADL/IGCL 没有收益。
- 边车隔离可以把 MPL-2.0 依赖、潜在的驱动加载与异常崩溃挡在采集主进程之外。
- 仅 GPU 硬件组不需要 LHM 的 CPU/主板内核驱动；实施时以“未加载 `WinRing0` / `inpoutx64`”为验收条件。

**边界**

- MPL-2.0 是文件级弱传染：不修改 LHM 源文件、以 NuGet 动态链接、保留声明即可；修改过的 LHM 文件必须公开。
- 边车默认关闭；开启后单独计量内存与 CPU，不计入采集进程 30MB 红线。
- 若 LHM 版本或驱动导致初始化失败、加载内核驱动或采样卡死，边车整体降级，不影响基础采集。

### 4.3 系统计数器与进程枚举：复用操作系统接口

- PDH 英文计数器负责 CPU、内存、磁盘、GPU Engine、GPU 显存。
- `NtQuerySystemInformation(SystemProcessInformation)` 一次取回全部进程，避免 `\Process(*)` 通配符开销。
- 不引入 WMI、不引入 System Informer 的驱动、不自研内核组件。

### 4.4 环形缓冲与会话：参考 CapFrameX

- 帧环形缓冲参考 CapFrameX 的 `PresentMonCircularBuffer` 思路：固定容量、按时间窗口取快照、事件前后拼接。
- 系统指标仍使用自研 seqlock 环形缓冲，因为需要与共享内存、NativeAOT 和 15 分钟窗口配合。
- 会话元数据参考 CapFrameX：记录 PresentMon 版本、目标进程、采集起止、丢帧与权限状态。

### 4.5 卡顿分类：参考 stutter-test（仅概念）

- 在既有“CPU 抢占 / GPU 瓶颈 / 显存 / 内存 / 磁盘 / 系统调度”之外，增加：
  - 着色器编译（首次进入场景、CPU 单核尖峰、GPU 等待短、后续不再复现）。
  - 资产流送（磁盘/内存压力 + 显存增长 + 帧时间锯齿）。
  - 帧节奏/呈现（CPU 与 GPU 都不忙，但 frametime 与显示间隔异常）。
  - 热降频（温度/功耗触顶后频率下降，需厂商传感器）。
- 该仓库无许可证，**不复制任何代码、注释或文案**，只采用公开可验证的分类维度并独立实现。

### 4.6 报告：借鉴 StutterScope，输出保持自研

- 借鉴其“离线、可分享、包含 Top 进程与环境摘要”的结构。
- 本项目输出 Markdown + PNG；LLM 报告为可选增强，仍以本地规则报告为兜底。
- 不采用其 HTML 模板代码。

### 4.7 明确不复用

| 对象 | 原因 |
| --- | --- |
| 自研 ETW consumer | PresentMon 已覆盖，且需要大量呈现队列知识 |
| 自研 NVML / ADL / IGCL 绑定 | LHM 已封装；仅当 LHM 不可用时才考虑直接 P/Invoke |
| OCAT 代码 | 许可证不明确 |
| SidebarDiagnostics 代码 | GPL-3.0 与本项目许可证一致，但体量与定位不同，仅参考布局思路，未复制代码 |
| System Informer 架构 | 体量与驱动模型不符合“低开销、无驱动” |
| 第三方图表库 | 与 80MB UI 预算冲突，自绘更可控 |

## 5. 许可证义务

本项目自 2026-11-16 起以 **GPL-3.0** 发布（`LICENSE`，Copyright (C) 2026 worldcopyist）。下表逐项说明上游许可证与本项目的关系：

| 依赖 | 许可证 | 分发方式 | 与本项目 GPL-3.0 的关系 | 义务 |
| --- | --- | --- | --- | --- |
| PresentMon | MIT | 随包分发官方 exe | 兼容 | 保留版权声明与 MIT 许可证全文；不得暗示 Intel 背书 |
| LibreHardwareMonitorLib（及 DiskInfoToolkit、RAMSPDToolkit-NDD、BlackSharp.Core） | MPL-2.0 | NuGet 动态链接，未修改 | 兼容（MPL-2.0 §3.3 次级许可） | 保留许可证与源码获取方式；修改过的文件需公开 |
| HidSharp | Apache-2.0 | 随 LHM 传递，未修改 | 兼容 | 保留 Apache-2.0 许可证 |
| System.Management / .NET 8 运行时 | MIT | NuGet / 自包含发布 | 兼容 | 保留版权与许可证声明 |
| PawnIO | GPL-2.0 | 不随包分发，用户自行安装 | 独立程序，不构成组合作品 | 不承担再分发义务 |
| CapFrameX | MIT | 仅参考设计，不复制代码 | 兼容 | 如后续复制代码，保留版权与许可声明 |
| StutterScope | MIT | 仅参考设计 | 兼容 | 同上 |
| game-stutter-recorder | MIT | 仅参考设计 | 兼容 | 同上 |
| stutter-test | 无 | 仅参考概念 | 未使用代码 | 禁止复制代码、注释、文案 |
| OCAT | 不明确 | 不使用 | 无 | 无 |

`THIRD-PARTY-NOTICES.md` 随仓库与发布包分发，逐项登记版本、哈希、许可证路径；其中第 4 节给出兼容性结论。CI 许可证清单检查列入 M3 后续项。

## 6. “不重复造轮子”的边界

复用不等于拼装。以下能力必须由本项目自己完成，否则产品没有存在意义：

- **常驻与取证**：PresentMon 是一次性采集工具，不会在后台常驻 15 分钟并在卡顿时自动保存前后现场。
- **统一时间轴**：帧时间、系统指标、进程排行、厂商传感器必须对齐到同一 QPC/UTC 时间基准。
- **归因**：把“这一帧为什么慢”拆成 CPU 等待、GPU 等待、显存压力、资产流送、呈现节奏等可解释证据。
- **隐私与成本控制**：LLM 只接收脱敏后的事实摘要，默认手动触发，有预算上限，可完全离线。
- **低开销**：采集进程 30MB、0.3% CPU 的红线，现有工具没有一家以此为目标。

## 7. 风险与对策

| 风险 | 影响 | 对策 |
| --- | --- | --- |
| PresentMon 上游升级导致参数/列名变化 | 帧采集失败 | 锁定版本 + SHA256；启动时校验；解析器按表头映射列名；失败降级浅模式 |
| PresentMon 需要管理员权限 | 普通用户无帧数据 | 明确的浅/深模式；提供“以管理员重启”和“加入 Performance Log Users”说明 |
| 反作弊对 ETW 敏感 | 用户顾虑 | 不注入、不 Hook、不读写游戏内存；只调用官方 PresentMon；文档明确说明，不承诺兼容所有反作弊 |
| LHM 初始化触发内核驱动 | 违反“无驱动”承诺 | 仅 GPU 硬件组；启动后检查已加载驱动；检测到驱动立即停止边车并提示 |
| LHM 升级破坏 ABI | 传感器崩溃 | 锁定 NuGet 版本；边车崩溃不影响主采集；独立重启与退避 |
| 无许可证项目被误用 | 法律风险 | 调研文档记录；代码评审检查来源；只做净室实现 |
| 第三方二进制体积 | 便携包变大 | PresentMon 单 exe 约数 MB；可接受；不引入完整 LHM GUI |

## 8. 参考链接

- PresentMon：<https://github.com/GameTechDev/PresentMon>
- PresentMon 控制台参数：<https://github.com/GameTechDev/PresentMon/blob/main/README-ConsoleApplication.md>
- CapFrameX：<https://github.com/CXWorld/CapFrameX>
- LibreHardwareMonitor：<https://github.com/LibreHardwareMonitor/LibreHardwareMonitor>
- StutterScope：<https://github.com/AdenXie/StutterScope>
- game-stutter-recorder：<https://github.com/jieke7k7k/game-stutter-recorder>
- stutter-test：<https://github.com/jkoens3/stutter-test>

## 9. 变更记录

| 版本 | 日期 | 作者 | 变更 |
| --- | --- | --- | --- |
| v1.0 | 2026-11-07 | Qoder | 建立竞品矩阵，确认 PresentMon、LHM、PDH 的复用边界与许可证义务 |
| v1.1 | 2026-11-16 | Qoder | 项目许可证确定为 GPL-3.0；补充全部捆绑依赖（含 LHM 传递依赖、HidSharp、.NET 运行时）与参考项目的兼容性结论 |
