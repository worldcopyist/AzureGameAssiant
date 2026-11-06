# 蓝天游戏助手 · 开发文档

| 项目 | 内容 |
| --- | --- |
| 文档编号 | AGS-DEV-001 |
| 版本 | v1.0 |
| 状态 | 已确认 |
| 作者 | Qoder |
| 适用版本 | v0.1 – v1.0 |
| 最后更新 | 2026-11-07 |

配套文档：[需求文档](requirements.md) · [交接文档](handoff.md) · [提交规范](commit-guidelines.md)

---

## 1. 设计前提

来自需求访谈的硬约束：

1. 技术栈为 **C# / .NET 8 + WPF**。
2. **采集进程 ≤ 30MB、平均 CPU ≤ 0.3%**；界面进程仅托盘 ≤ 60MB、主窗口 ≤ 80MB。
3. 采集以 **PDH 性能计数器**为主，GPU 在 M3 接入 **NVML / ADL / IGCL** 三家厂商 SDK。
4. 不开 ETW；卡顿判定用 **定时器抖动 + 资源尖峰 + 内存/磁盘压力**，另有全局热键。
5. 数据留存为 **15 分钟内存环形缓冲 + 卡顿前后各 30 秒落盘**（gzip JSONL）。
6. 支持 **Windows 10 1909+ / Windows 11，x64**，默认免提权。
7. 纯本地、无网络、无驱动、无注入。
8. 界面形态：托盘 + 可选悬浮条 + 主窗口；中英双语；明暗主题跟随系统；极简风格。
9. 分发形式：便携版 ZIP；三阶段交付（v0.1 / v0.5 / v1.0）。

## 2. 技术决策记录

| 编号 | 决策 | 选择 | 理由 | 被否方案 |
| --- | --- | --- | --- | --- |
| ADR-001 | 进程模型 | 采集进程与界面进程分离 | WPF 无法 AOT，基线内存高；采集进程用 NativeAOT 才能压到 30MB 内 | 单进程 WPF（后台也背 UI 内存） |
| ADR-002 | 采集进程运行时 | .NET 8 + NativeAOT，无窗口 | 启动快、无 JIT、常驻低；P/Invoke 密集场景合适 | .NET 8 JIT 自包含（30–50MB） |
| ADR-003 | UI 框架 | WPF + MVVM（CommunityToolkit.Mvvm） | 用户已确认；成熟、可自绘图表、资源占用可控 | WinUI 3、Avalonia、Qt |
| ADR-004 | 遥测传输 | 内存映射文件 + 环形缓冲（seqlock） | 零拷贝、无序列化、UI 可高频读取 | 命名管道传每个采样点（分配与延迟高） |
| ADR-005 | 控制通道 | 命名管道 + 长度前缀 JSON | 低频命令，实现简单、易调试 | gRPC（重）、共享内存命令区（复杂） |
| ADR-006 | 系统指标来源 | PDH（英文计数器名）+ NT 进程信息 API | PDH 覆盖 CPU/内存/磁盘/GPU；NT API 一次拿到全部进程，避免 `\Process(*)` 通配符开销 | WMI（慢）、`\Process(*)`（实例多时开销大） |
| ADR-007 | 图表 | 自绘 `FrameworkElement` + `DrawingContext` | 避免 LiveCharts/ScottPlot 的内存与依赖；可控降采样 | 第三方图表库 |
| ADR-008 | 会话格式 | gzip JSONL | 可读、可 grep、易演进；压缩后体积小 | SQLite（依赖与写放大）、自定义二进制（调试难） |
| ADR-009 | 托盘 | Win32 `Shell_NotifyIcon` + 隐藏窗口 | 不引入 WinForms / 第三方托盘库 | Hardcodet.NotifyIcon、WinForms 互操作 |
| ADR-010 | 国际化 | `.resx` + 运行时 `ResourceManager` | 标准、无第三方依赖、支持运行时切换 | 自研 JSON 语言包 |
| ADR-011 | 配置 | `settings.json` + `schemaVersion` | 可读、可迁移；UI 为唯一写入方 | 注册表、二进制配置 |
| ADR-012 | 版本管理 | 主干开发 + 短分支 + Squash PR | 单人 + 多 Agent 协作，历史线性清晰 | Git Flow |

## 3. 总体架构

```
┌──────────────────────────────────┐          ┌───────────────────────────────────┐
│ AzureGameAssistant.exe (UI)      │          │ AzureGameAssistant.Collector.exe  │
│ WPF / .NET 8 / asInvoker         │          │ .NET 8 NativeAOT / 无窗口          │
│                                  │          │                                   │
│  Tray  FloatingBar  MainWindow   │          │  SamplingScheduler (1s / 200ms)    │
│  TimelineChart  EventList        │          │  PdhSampler                        │
│  ReportView  Settings  i18n      │          │  ProcessSampler (NT API)           │
│  ThemeService  SessionRepository │          │  JitterMonitor (16ms)              │
└───────────┬──────────────────────┘          │  GpuProviders (PDH + M3 厂商)      │
            │                                 │  DetectionEngine                   │
            │ 控制：命名管道 (JSON)             │  RingBuffer (MMF, seqlock)         │
            ├────────────────────────────────►│  SessionWriter (JSONL.gz)          │
            │ 遥测：共享内存 (只读快照)          │  HotkeyHost (隐藏窗口)              │
            └────────────────────────────────►│  RetentionWorker                   │
                                              └───────────────────────────────────┘
```

**数据流**

1. 采集进程按节拍采样，写入共享内存环形缓冲。
2. 判定引擎读取同一批采样，产生卡顿事件候选。
3. 事件确认后，从环形缓冲截取前后各 30 秒，交给会话写入线程落盘。
4. UI 每 500ms 读取共享内存增量，更新图表、事件列表；点击事件时直接读取会话文件回放。
5. UI 通过命名管道下发设置、手动标记、暂停/恢复、退出等命令。

## 4. 进程模型与生命周期

### 4.1 采集进程

**职责**

- 按节拍采集系统、GPU、进程数据。
- 维护 15 分钟环形缓冲。
- 运行卡顿判定与事件窗口管理。
- 写会话文件、执行留存清理。
- 注册全局热键，接收手动标记。
- 提供命名管道控制接口。

**线程**

| 线程 | 周期 | 职责 |
| --- | --- | --- |
| Sampling | 1s / 200ms | PDH 采集、进程采集（1s）、写环形缓冲、驱动判定 |
| Jitter | 16ms | 高精度等待定时器，测量实际唤醒间隔 |
| Vendor | 1s | NVML / ADL / IGCL 采集（M3），带 200ms 超时保护 |
| PipeServer | 事件驱动 | 处理 UI 命令 |
| SessionWriter | 队列驱动 | 快照环形缓冲、压缩、原子落盘 |
| Retention | 空闲 | 按数量/容量清理旧会话 |
| HiddenWindow | 消息驱动 | 热键、托盘/电源事件回调 |

**生命周期**

- 单实例互斥体：`Local\AzureGameAssistant.Collector`。
- 启动顺序：初始化日志 → 打开 PDH 查询 → 首次采样 → 创建共享内存 → 启动管道 → 注册热键 → 心跳。
- UI 崩溃不影响采集进程；采集进程继续缓冲与落盘，等待 UI 重连。
- 用户选择"退出"时：停止采样 → 刷新未完成会话 → 释放 PDH/厂商 SDK → 删除互斥体。
- 崩溃：写入 `logs\collector-crash-*.log`，退出码非 0；UI 按 1s / 5s / 15s 退避重启，最多 3 次。

### 4.2 界面进程

**职责**

- 托盘图标与菜单、通知。
- 主窗口：实时概览、时间轴、事件列表、归因详情、设置。
- 可选悬浮条。
- 读取共享内存、读取会话文件、导出报告。
- 作为 `settings.json` 的唯一写入方。

**生命周期**

- 单实例互斥体：`Local\AzureGameAssistant.UI`。
- 启动时尝试连接管道；失败则启动采集进程并等待最多 2 秒。
- 退出时通过管道通知采集进程退出；若采集进程无响应，仅退出 UI 并提示。

### 4.3 权限策略

- 两个进程默认均为 `asInvoker`，清单不声明 `requireAdministrator`。
- 托盘菜单"以管理员身份重启"：UI 以 `runas` 重启，并先通过管道让旧采集进程退出；新 UI 以管理员身份启动采集进程。
- **禁止混合权限**：采集进程与 UI 必须同权限运行，否则高完整性进程创建的 `Local\` 对象低权限 UI 无法读取。UI 检测到共享内存打开失败时，提示"请以相同权限重启"。

## 5. 进程间通信

### 5.1 共享内存（遥测）

- 名称：`Local\AzureGameAssistant.Telemetry.v1`
- 大小：2MB（固定），创建时 `PAGE_READWRITE`，UI 以只读映射打开。
- 结构：Header + Sample 环形数组 + Event 环形数组。
- 同步：单写者 seqlock。写者将 `Sequence` 加 1（变奇数）→ 写入 → 再加 1（变偶数）；读者读两次序号，奇数或前后不一致则重试。

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct SharedHeader
{
    public long Magic;              // 'AGS1' = 0x31475341
    public int ProtocolVersion;     // 1
    public int SampleStride;        // sizeof(SampleRecord)
    public int SampleCapacity;      // 4500
    public int EventCapacity;       // 256
    public long WriteSequence;      // seqlock
    public long WriteIndex;         // 下一个写入槽位
    public long EventWriteIndex;
    public long HeartbeatQpc;
    public int CollectorPid;
    public int SamplingIntervalMs;
    public int ModeFlags;           // bit0 游戏模式, bit1 暂停
    public int Flags;
}
```

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct SampleRecord
{
    public long QpcTicks;
    public long UtcTicks;
    public float CpuUtilityPct;
    public float CpuFrequencyPct;
    public uint MemAvailableMb;
    public uint MemCommitMb;
    public uint MemCommitLimitMb;
    public float PageFilePct;
    public float DiskBusyPct;
    public float DiskLatencyMs;
    public float GpuUtilPct;
    public uint GpuDedicatedMb;
    public uint GpuSharedMb;
    public float TimerJitterMs;
    public uint ForegroundPid;
    public ushort ProcessorQueueLength;
    public ushort Flags;            // bit0 进程数据有效, bit1 GPU 数据有效, bit2 厂商数据有效
    public ProcessSlot P0, P1, P2, P3, P4;
}
```

- `ProcessSlot`：`uint Pid; uint StartTimeLow; float CpuPct; float GpuPct; uint WorkingSetMb; uint PrivateMb; ushort NameHint; ushort Flags;`（24 字节）。
- 容量按最快采样率计算：15 分钟 × 5Hz = 4500 槽位；单槽约 200 字节，约 900KB。
- UI 不依赖共享内存中的进程名：名称由 UI 自己的进程缓存（1 秒刷新）解析；会话文件中保存名称快照，避免进程退出后无法显示。

### 5.2 命名管道（控制）

- 名称：`\\.\pipe\AzureGameAssistant.Control.v1`
- 安全：DACL 仅允许当前用户 SID；`PIPE_REJECT_REMOTE_CLIENTS`。
- 帧格式：4 字节小端长度 + UTF-8 JSON；单帧上限 1MB。
- 请求：`{ "id": 1, "cmd": "status", "payload": { } }`
- 响应：`{ "id": 1, "ok": true, "data": { }, "error": null }`

| 命令 | 方向 | 说明 |
| --- | --- | --- |
| `hello` | UI → Collector | 协议版本握手，返回采集进程版本与能力位 |
| `status` | UI → Collector | 采样间隔、模式、运行时长、最后错误、队列积压 |
| `mark` | UI → Collector | 手动标记卡顿，可附备注 |
| `pause` / `resume` | UI → Collector | 暂停/恢复采样与判定（保留环形缓冲） |
| `setSettings` | UI → Collector | 下发阈值、采样、热键、游戏名单等补丁 |
| `listSessions` | UI → Collector | 返回会话文件元信息列表 |
| `deleteSession` | UI → Collector | 删除指定会话 |
| `quit` | UI → Collector | 优雅退出 |

- UI 直接读取会话文件（同用户、同权限），不走管道传输大文件。

## 6. 采集层

### 6.1 采样节拍

| 数据 | 常规模式 | 游戏模式 | 说明 |
| --- | --- | --- | --- |
| CPU / 内存 / 抖动 / 前台 PID | 1s | 200ms | 轻量，每拍都采 |
| GPU 总量 / 显存 | 1s | 1s | 通配符实例多，保持 1s |
| 进程排行 Top-5 | 1s | 1s | CPU 差值需要至少 1s 窗口 |
| 磁盘 / 硬缺页 | 1s | 1s | 平滑指标，无需高频 |
| 厂商 GPU 温度/功耗/频率 | 1s | 1s | SDK 调用有开销，固定 1s |

200ms 拍中的 GPU / 进程字段复用最近一次 1s 采样值，并在 `Flags` 中标明，避免误读。

### 6.2 PDH

- 统一使用 `PdhAddEnglishCounterW` 注册英文计数器名，避免中文系统本地化名称差异。
- 通配符计数（GPU）使用 `PdhGetFormattedCounterArrayW`，缓冲区复用，避免每拍分配。
- `PdhCollectQueryData` 在采样线程调用；任何失败记一次降级，不抛出到主循环。
- 实例名解析使用 `ReadOnlySpan<char>` 与手写扫描，禁止正则与字符串分配。

| 指标 | 计数器路径 |
| --- | --- |
| CPU 总利用率 | `\Processor Information(_Total)\% Processor Utility`（Win10 1709+；失败回退 `\Processor(_Total)\% Processor Time`） |
| CPU 有效频率 | `\Processor Information(_Total)\% Processor Performance` |
| 处理器队列 | `\System\Processor Queue Length` |
| 上下文切换 | `\System\Context Switches/sec` |
| 可用内存 | `\Memory\Available MBytes` |
| 已提交 / 上限 | `\Memory\Committed Bytes`、`\Memory\Commit Limit` |
| 硬缺页 | `\Memory\Pages Input/sec` |
| 页面文件 | `\Paging File(_Total)\% Usage` |
| 磁盘占用 | `\PhysicalDisk(_Total)\% Disk Time` |
| 磁盘响应 | `\PhysicalDisk(_Total)\Avg. Disk sec/Transfer` |
| GPU 引擎占用 | `\GPU Engine(*)\Utilization Percentage` |
| GPU 适配器显存 | `\GPU Adapter Memory(*)\Dedicated Usage`、`\GPU Adapter Memory(*)\Shared Usage` |
| GPU 进程显存 | `\GPU Process Memory(*)\Dedicated Usage`、`\GPU Process Memory(*)\Shared Usage` |

**GPU 实例名解析**

实例形如：

```
pid_12345_luid_0x00000000_0x0000A1B2_phys_0_eng_0_engtype_3D
```

规则：

- 提取 `pid`、`luid`、`phys`、`engtype`。
- 只聚合 `engtype_3D` 与 `engtype_Copy`；忽略 `_Total` 实例，避免重复计数。
- 单进程 GPU 占用 = 该 pid 各引擎占用求和，按 100% 封顶。
- 适配器总占用 = 该适配器所有 3D 引擎占用的最大值（与任务管理器"GPU 3D"口径接近）。
- 显存按适配器汇总专用/共享用量；进程级显存用于归因排序。

### 6.3 进程采样

使用 `NtQuerySystemInformation(SystemProcessInformation)` 一次性获取全部进程，缓冲区初始 1MB，遇 `STATUS_INFO_LENGTH_MISMATCH` 翻倍重试，稳定后复用。

每个进程提取：

- `UniqueProcessId`、`CreateTime`（组成进程唯一键，识别 PID 复用）
- `KernelTime`、`UserTime`（差值计算 CPU）
- `WorkingSetPrivateSize`、`PrivatePageCount`
- `WorkingSetSize`、`HandleCount`、`ThreadCount`
- `ImageName`（`UNICODE_STRING`，截断到 64 字符）
- `ReadOperationCount` / `WriteOperationCount` 等 IO 计数用于速率

**CPU 百分比**

```
cpuPct = (ΔKernel + ΔUser) / (ΔWallSeconds × LogicalProcessorCount) × 100
```

**排行**

- 每 1 秒分别按 CPU、GPU、私有工作集排序，取并集 Top-5；前台进程强制加入。
- 基线用于尖峰检测：每个进程保留最近 30 个采样值（约 30 秒），取中位数。
- 受保护进程（`OpenProcess` 失败）保留 PID 与名称，指标置空并标记。

### 6.4 前台与游戏识别

- 前台进程：`GetForegroundWindow` → `GetWindowThreadProcessId`。
- 全屏判定：
  - `SHQueryUserNotificationState` 返回 `QUNS_RUNNING_D3D_FULL_SCREEN`，直接判定为游戏。
  - 或前台窗口矩形覆盖其所在显示器可用区域 ≥ 95%，且进程不在排除名单（桌面、任务栏、`explorer`、浏览器、视频播放器）。
- 进入游戏模式后延迟 5 秒再切 200ms，避免加载瞬间误判；退出全屏后 10 秒恢复 1 秒。
- 排除名单存于设置，默认包含常见浏览器与播放器；用户可增删。

### 6.5 厂商 GPU（M3）

```csharp
internal interface IGpuVendorProvider : IDisposable
{
    GpuVendorKind Kind { get; }
    bool IsAvailable { get; }
    bool TryInitialize(out string? error);
    bool TrySample(Span<GpuVendorSample> destination, out int count);
}
```

| 厂商 | 动态库 | 主要接口 | 备注 |
| --- | --- | --- | --- |
| NVIDIA | `nvml.dll`（驱动安装） | `nvmlInit_v2`、`nvmlDeviceGetUtilizationRates`、`nvmlDeviceGetMemoryInfo`、`nvmlDeviceGetTemperature`、`nvmlDeviceGetPowerUsage`、`nvmlDeviceGetClockInfo` | 函数指针通过 `GetProcAddress` 获取，避免链接依赖 |
| AMD | `atiadlxx.dll`（驱动安装） | `ADL_Main_Control_Create`、`ADL_Adapter_AdapterInfo_Get`、`ADL_Overdrive5_Temperature_Get`、`ADL_Overdrive5_CurrentActivity_Get` | 需要提供内存分配回调 |
| Intel | `ControlLib.dll`（驱动安装） | IGCL 遥测接口（温度、频率、显存、功耗） | 以 Intel IGCL 头文件为准，按能力位探测 |

**降级与隔离**

- 初始化失败、DLL 缺失、设备不支持：记 Info 日志，能力位置为不可用，UI 显示 "—"。
- 采集在独立线程执行；单次调用超过 200ms 视为挂起，标记降级并退避 10 秒。
- 厂商数据仅作增强，不参与基础曲线可用性判断。
- 多适配器按 LUID 与 PDH 适配器配对；无法配对时按索引顺序展示，并标记"未关联"。

### 6.6 时间基准

- 单调时间：`QueryPerformanceCounter` / `QueryPerformanceFrequency`，用于环形缓冲排序与抖动计算。
- 墙钟时间：每个采样点记录 `DateTime.UtcNow.Ticks`；启动时建立 QPC 与 UTC 的映射，之后每 60 秒校准一次偏移，防止休眠/时钟调整导致漂移。
- 会话文件同时写入 `qpc` 与 `utc`，回放时以 `utc` 展示、以 `qpc` 计算间隔。

## 7. 数据模型

### 7.1 事件与信号

```csharp
internal enum StutterCause
{
    Unknown = 0,
    CpuContention = 1,
    GpuBound = 2,
    VramPressure = 3,
    MemoryPressure = 4,
    DiskBound = 5,
    SystemScheduling = 6,
    ManualMark = 7,
}

[Flags]
internal enum StutterSignal
{
    None = 0,
    TimerJitter = 1 << 0,
    CpuSpike = 1 << 1,
    GpuSpike = 1 << 2,
    MemoryPressure = 1 << 3,
    DiskPressure = 1 << 4,
    Manual = 1 << 5,
}

internal sealed record StutterEvent(
    long Id,
    long StartQpc,
    long EndQpc,
    float Score,
    StutterSignal Signals,
    StutterCause Cause,
    uint PrimaryPid,
    string? Note);
```

### 7.2 会话文件（schemaVersion = 1）

文件名：`20261107-214503-<eventId>.agsession.jsonl.gz`

每行一个 JSON 对象：

```json
{"type":"header","schemaVersion":1,"appVersion":"0.5.0","os":"10.0.22631","cpu":"...","gpu":["NVIDIA ..."],"samplingMs":200,"startUtc":"2026-11-07T13:45:03Z","windowMs":60000}
{"type":"sample","qpc":123456789,"utc":"2026-11-07T13:45:03.120Z","cpu":42.1,"freq":88.0,"memAvailMb":1200,"commitMb":15000,"commitLimitMb":16384,"pageFilePct":35.2,"diskBusy":12.0,"diskMs":3.1,"gpu":61.0,"gpuDedicatedMb":5200,"gpuSharedMb":300,"jitterMs":16.0,"fgPid":1234,"procs":[[1234,"game.exe","2026-11-07T13:00:00Z",35.2,61.0,4200,3800,1200000]]}
{"type":"event","id":17,"startQpc":123400000,"endQpc":123900000,"score":82.0,"signals":["TimerJitter","CpuSpike"],"cause":"CpuContention","primaryPid":4321,"note":null}
{"type":"report","cause":"CpuContention","confidence":"High","summary":"疑似后台进程抢占 CPU 导致卡顿","suspects":[{"pid":4321,"name":"render.exe","cpuPeak":92.0,"gpuPeak":0.0,"wsDeltaMb":120}],"suggestions":["..."]}
{"type":"footer","endUtc":"2026-11-07T13:46:03Z","samples":300,"dropped":0}
```

规则：

- `procs` 为可选字段，仅在 1 秒进程采样点出现；其余采样点省略以减小体积。
- 字段名短、单位写进字段名或文档；新增字段只能追加，不得改变既有含义。
- 头部必须包含 `schemaVersion`；读取方遇到更高版本时拒绝并提示升级。
- 尾部记录采样数与丢点，用于完整性校验。

## 8. 环形缓冲与会话存储

### 8.1 环形缓冲

- 固定容量 4500 槽位（15 分钟 × 5Hz），写满覆盖最旧。
- 时间窗口以 `QpcTicks` 计算，不依赖槽位数；模式切换不会导致时间轴断裂。
- 采集线程只写；UI 与判定引擎只读。写入采用 seqlock，读侧冲突重试 3 次后跳过本次刷新，不阻塞采集。
- 事件确认后，从环形缓冲复制 `[eventStart - 30s, eventEnd + 30s]` 的采样与对应进程数据，放入写入队列。
- 若事件跨越缓冲边界（例如工具刚启动不足 30 秒），保存实际可用部分并在文件头 `windowMs` 中如实记录。

### 8.2 会话写入

1. 会话线程取快照，写临时文件 `*.tmp`。
2. 逐行写入 header → samples → event → report → footer，使用 `Utf8JsonWriter` + `GZipStream`，缓冲区 64KB。
3. `FlushFileBuffers` 后 `MoveFileEx` 原子重命名。
4. 失败时保留 `.tmp` 并记 Error 日志，不阻塞后续事件。

### 8.3 留存

- 目录：`%LOCALAPPDATA%\AzureGameAssistant\sessions\`
- 上限：20 个文件或 500MB（以先到者为准）。
- 清理：按文件修改时间从旧到新删除；跳过正在写入的临时文件。
- 启动时与每次成功写入后各执行一次。

## 9. 卡顿检测与归因

### 9.1 信号计算

**定时器抖动**

- 使用 `CreateWaitableTimerExW` + `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`，周期 16ms。
- 每次唤醒记录 `actual = QPC 差值`，`jitter = actual - 16ms`。
- 不调用 `timeBeginPeriod`，避免全局提升定时器精度影响功耗。
- 单次 ≥ 50ms 记疑似；≥ 100ms 直接确认事件。

**CPU 尖峰**

- 基线 `base` = 最近 30 秒同一进程 CPU 百分比中位数；系统基线同理。
- 命中条件：`cpu >= 2.5 × base && cpu >= 30 / LogicalProcessorCount`（约 30% 单核），或系统 CPU ≥ 85%。
- 前台进程与 Top-5 进程都参与计算。

**GPU 尖峰**

- 命中条件：`gpuUtil >= 85 && gpuUtil - base >= 20`。
- 同时检查专用显存使用率 ≥ 95%，用于区分"GPU 算力瓶颈"与"显存不足"。

**内存压力**

- `availableMb < max(1024, totalPhysicalMb × 0.08)`，或 `commit / commitLimit > 0.90`。
- 结合 `Pages Input/sec` 相对基线的突增，避免把"缓存占用"误判为压力。

**磁盘压力**

- `diskLatencyMs >= 50` 或 `diskBusyPct >= 95` 持续 2 秒。
- 默认只参与评分，不单独确认事件，避免正常磁盘写入被记为卡顿。

### 9.2 事件状态机

```
Normal ──信号命中──► Suspected ──评分≥阈值 或 抖动≥100ms 或 手动──► Confirmed
   ▲                    │                                              │
   └──── 5s 冷却 ◄──────┴──────── 静默 2s 后结束 ◄────────────────────┘
```

- 合并窗口：3 秒内出现的信号归入同一事件。
- 结束条件：最后一个信号后静默 2 秒。
- 冷却：事件结束后 5 秒内不再新建事件；期间的手动标记仍生成独立事件。
- 评分：

```
score = min(100, max(
    jitterScore,
    cpuScore,
    gpuScore,
    memScore,
    diskScore
) + (命中信号数 >= 2 ? 10 : 0))

jitterScore = clamp((jitterMs - 50) / 150 * 100, 0, 100)
cpuScore    = clamp((cpuUtility - 80) / 20 * 100, 0, 100)
gpuScore    = clamp((gpuUtil - 85) / 15 * 100, 0, 100)
memScore    = clamp((commitRatio - 0.90) / 0.10 * 100, 0, 100)
diskScore   = clamp((diskLatencyMs - 50) / 100 * 100, 0, 100)
```

- 确认阈值默认 40；手动标记固定 100。

### 9.3 归因

1. 取事件窗口（开始前 30 秒至结束后 30 秒）内所有 1 秒进程采样。
2. 对每个进程计算：CPU 峰值与相对基线倍数、GPU 峰值、私有工作集增量、IO 峰值。
3. 进程可疑度：

```
suspicion = 0.35 × norm(cpuPeak)
          + 0.30 × norm(gpuPeak)
          + 0.25 × norm(wsDelta)
          + 0.10 × norm(ioPeak)
```

`norm` 为窗口内该维度的 0–1 归一化；同分时优先前台进程与存活进程。

4. 主因判定规则（按优先级）：

| 条件 | 主因 |
| --- | --- |
| 手动标记 | ManualMark |
| `commitRatio > 0.90` 或可用内存低于阈值，且存在内存增量最大的进程 | MemoryPressure |
| 专用显存 ≥ 95% 且 GPU 占用高 | VramPressure |
| GPU 占用 ≥ 85% 且 GPU 信号命中 | GpuBound |
| 系统 CPU ≥ 85% 且 CPU 信号命中 | CpuContention |
| 磁盘响应 ≥ 50ms 且磁盘信号命中 | DiskBound |
| 抖动命中但资源均低于阈值 | SystemScheduling |
| 其余 | Unknown |

5. 置信度：
   - 高：评分 ≥ 70 且 ≥ 2 个信号互相印证。
   - 中：评分 ≥ 40 或主因有单一强信号。
   - 低：其余。
6. 报告建议模板（中/英各一套）：
   - CPU：给出可疑进程、峰值、基线倍数；建议关闭后台任务、限制进程优先级、检查单核占用。
   - 内存：给出可用量、提交率、增量最大的进程；建议关闭后台程序、检查泄漏、评估扩容。
   - GPU：给出占用、显存、温度（M3）；建议降低画质/分辨率、关闭光追、更新驱动。
   - 磁盘：给出响应时间与占用率；建议检查后台扫描/下载、迁移数据到 SSD。
   - 系统调度：资源正常但抖动高；建议更新驱动、检查电源计划、排查 DPC/中断（不自动执行）。

### 9.4 误报控制

- 默认阈值偏保守；所有阈值可在设置中调整。
- 事件必须附带命中信号；无信号的纯人工判断不生成自动事件。
- 启动后 10 秒、从睡眠恢复后 10 秒内不确认自动事件（避免初始化噪声）。
- 游戏加载、安装、编译等已知高负载场景仍会记录，但报告会注明"高负载场景"。

## 10. 界面层

### 10.1 结构

- `App.xaml`：资源、主题、全局异常处理。
- `MainWindow`：实时概览卡片、时间轴、事件列表、详情面板。
- `SettingsWindow`：分页设置（采集、判定、界面、存储、关于）。
- `FloatingBarWindow`：M3，置顶小条。
- `AboutWindow`：版本、隐私声明、日志目录入口。

### 10.2 MVVM

- 使用 `CommunityToolkit.Mvvm` 的 `ObservableObject`、`RelayCommand`。
- ViewModel 不直接引用 `System.Windows` 类型；服务通过构造函数注入（手写轻量容器，不引入 DI 框架）。
- 采样刷新由 `DispatcherTimer`（500ms）驱动；窗口不可见时停止。
- 所有 UI 更新合并到一次 `PropertyChanged` 批次，避免每字段刷新。

### 10.3 时间轴绘制

- `TimelineChart : FrameworkElement`，重写 `OnRender(DrawingContext)`。
- 每个像素列取该列时间范围内采样的 min/max，绘制为竖线段 + 折线；事件标记绘制在独立层。
- 几何缓存：仅在数据或视口变化时重建 `StreamGeometry`；画笔/画刷静态冻结（`Freeze()`）。
- 交互：滚轮缩放、左键拖动平移、悬停十字线与浮动提示、双击回到实时。
- 渲染节流：非交互状态最多 10fps；窗口最小化/隐藏时停止定时器。
- 不引入第三方图表库，避免额外内存与依赖。

### 10.4 托盘与通知

- `Shell_NotifyIconW` + 隐藏窗口接收 `WM_*` 回调。
- 右键菜单使用 WPF `ContextMenu`，在鼠标位置打开。
- 通知使用托盘气泡（`NIF_INFO`）；系统关闭通知时改为托盘图标闪烁。
- 菜单项：打开主界面、标记卡顿、暂停/恢复、静音通知、设置、退出。

### 10.5 悬浮条（M3）

- 无边框、透明、`Topmost`、`ShowInTaskbar=false`。
- 可拖动；位置按显示器保存；鼠标穿透通过 `WS_EX_TRANSPARENT` 切换。
- 只显示 CPU / GPU / 内存数值与迷你曲线，复用时间轴降采样逻辑。
- 不抢焦点：`ShowActivated=false`；避免在全屏独占游戏中强制置顶引发闪烁。

### 10.6 国际化与主题

- 资源：`Resources/Strings.resx`（英文默认）+ `Strings.zh-CN.resx`；启动时按系统语言选择。
- `LocalizationService` 暴露索引器与 `LanguageChanged` 事件；XAML 通过绑定服务实例取值，切换语言时刷新绑定。
- 主题：`Themes/Light.xaml`、`Themes/Dark.xaml`；读取 `HKCU\...\Themes\Personalize\AppsUseLightTheme`，监听 `SystemEvents.UserPreferenceChanged`。
- 设置支持 `Auto / Light / Dark`，默认 `Auto`。
- 视觉极简：无阴影、无渐变、单一强调色（蓝天蓝）、等宽数字（`Consolas` / `Cascadia Mono` 回退）。

### 10.7 设置

- 位置：`%LOCALAPPDATA%\AzureGameAssistant\settings.json`
- UI 是唯一写入方；采集进程启动时读取，之后只接收管道补丁。
- 写入采用临时文件 + 原子替换；读取失败时备份为 `settings.corrupt-<timestamp>.json` 并回退默认值。
- `schemaVersion` 与迁移函数集中管理。

```json
{
  "schemaVersion": 1,
  "sampling": { "normalMs": 1000, "gameMs": 200 },
  "detection": {
    "jitterSuspectMs": 50,
    "jitterConfirmMs": 100,
    "cpuSpikeRatio": 2.5,
    "gpuSpikePct": 85,
    "memAvailableFloorMb": 1024,
    "commitRatio": 0.90,
    "diskLatencyMs": 50,
    "confirmScore": 40
  },
  "hotkey": "Ctrl+Alt+M",
  "autoStart": true,
  "notifications": true,
  "theme": "Auto",
  "language": "zh-CN",
  "sessions": { "maxCount": 20, "maxTotalMb": 500 },
  "gameDetection": { "enabled": true, "exclude": ["chrome.exe", "msedge.exe", "firefox.exe", "vlc.exe"] },
  "gpuVendors": { "nvidia": true, "amd": true, "intel": true }
}
```

### 10.8 会话回放与导出

- `SessionRepository` 负责枚举、读取、删除会话；读取使用流式 `JsonSerializer` 逐行反序列化，避免整文件载入。
- 回放复用主窗口时间轴，切换到"历史会话"模式，事件详情从文件读取。
- 导出 Markdown：环境摘要、事件、指标峰值表、可疑进程表、建议。
- 导出 PNG：离屏渲染时间轴（`RenderTargetBitmap`），包含事件标记与时间范围；不依赖屏幕尺寸。

## 11. 性能预算与实现约束

### 11.1 预算

| 项 | 预算 | 执行手段 |
| --- | --- | --- |
| 采集进程工作集 | ≤ 30MB | NativeAOT；预分配缓冲；无 UI 程序集；无反射 |
| 采集 CPU | ≤ 0.3% / ≤ 1.0% | 1s 为主节拍；GPU 通配符 1s；进程一次系统调用；Span 解析 |
| 界面工作集 | ≤ 60 / 80MB | 自绘图表；无重型图表库；隐藏停渲染；复用几何与位图 |
| 会话写盘 | 仅事件时 | 内存缓冲；gzip；原子写 |
| 空闲磁盘 | ≤ 10KB/min | 仅启动与错误日志；无心跳写盘 |

### 11.2 热路径规则

- 采样循环内禁止 LINQ、闭包、装箱、字符串拼接、`foreach` 迭代器。
- 使用 `ArrayPool<T>` 与预分配数组；缓冲区只增不减，稳定后零分配。
- 进程名使用 `stackalloc char[64]` + `ReadOnlySpan<char>`；不在每拍创建 `string`。
- PDH 实例名解析手写扫描；禁止正则。
- 日志只在状态变化时写（启动、降级、错误、事件），不逐采样写。
- UI 侧图表使用冻结画刷与缓存几何；不在 `OnRender` 内分配大对象。
- 所有 P/Invoke 使用 `LibraryImport`（源生成），减少封送开销并兼容 AOT。

### 11.3 测量

- 脚本 `build/measure.ps1`（M1 提供）：
  - 读取 `WorkingSet64`、`PrivateMemorySize64`、`TotalProcessorTime`、`HandleCount`。
  - 10 分钟窗口，输出 CSV 与中位数。
- UI 内存用任务管理器"专用工作集"交叉验证。
- 每个 PR 涉及采集/UI 热路径时，必须在 PR 描述中附测量结果。

## 12. 目录结构

```
AzureGameAssistant/
├─ AzureGameAssistant.sln
├─ README.md
├─ .gitignore
├─ .gitattributes
├─ docs/
│  ├─ requirements.md
│  ├─ development.md
│  ├─ handoff.md
│  └─ commit-guidelines.md
├─ src/
│  ├─ AzureGameAssistant.Core/          # 数据模型、环形缓冲、判定、会话、设置、日志（无 UI 依赖）
│  ├─ AzureGameAssistant.Collector/     # NativeAOT 采集进程
│  ├─ AzureGameAssistant.UI/            # WPF 界面
│  └─ AzureGameAssistant.Tests/         # xUnit 单元与集成测试
├─ build/
│  ├─ measure.ps1
│  ├─ package.ps1
│  └─ version.props
└─ artifacts/                           # 构建产物（git 忽略）
```

命名空间：`AzureGameAssistant.Core.*`、`AzureGameAssistant.Collector.*`、`AzureGameAssistant.UI.*`。
进程名：`AzureGameAssistant.exe`（UI）、`AzureGameAssistant.Collector.exe`（采集）。

## 13. 构建与发布

### 13.1 环境

- Windows 10 1909+ / Windows 11 x64。
- .NET SDK 8.0.400+ 或 9.x（当前机器为 9.0.100，可构建 `net8.0`）。
- Visual Studio 2022 Build Tools，含"使用 C++ 的桌面开发"（NativeAOT 链接需要）。
- 可选：Visual Studio 2022 / Rider 用于调试。

### 13.2 目标框架

| 项目 | TFM | 发布方式 |
| --- | --- | --- |
| Core | `net8.0` | 类库 |
| Collector | `net8.0` | NativeAOT，win-x64 |
| UI | `net8.0-windows` | 自包含，win-x64，ReadyToRun |
| Tests | `net8.0` | 不发布 |

### 13.3 命令

```powershell
dotnet restore AzureGameAssistant.sln
dotnet build AzureGameAssistant.sln -c Release

dotnet publish src/AzureGameAssistant.Collector/AzureGameAssistant.Collector.csproj `
  -c Release -r win-x64 -p:PublishAot=true -o artifacts/collector

dotnet publish src/AzureGameAssistant.UI/AzureGameAssistant.UI.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishReadyToRun=true -p:PublishSingleFile=false -o artifacts/ui
```

### 13.4 便携包

```
AzureGameAssistant-<version>-win-x64/
├─ AzureGameAssistant.exe
├─ AzureGameAssistant.Collector.exe
├─ *.dll / *.json
├─ README.md
└─ THIRD-PARTY-NOTICES.md   # M3
```

- ZIP 根目录为版本化文件夹，解压即用，不写注册表（自启项由用户手动开启）。
- 发布前运行 `build/measure.ps1`，结果附在交接文档。

## 14. 测试策略

### 14.1 单元测试（xUnit）

| 对象 | 用例 |
| --- | --- |
| `GpuInstanceParser` | 正常/异常实例名、`_Total` 过滤、多 GPU 聚合、数值封顶 |
| `RingBuffer` | 覆盖、回绕、时间窗口截取、并发读写一致性 |
| `DetectionEngine` | 单信号/多信号、合并窗口、冷却、手动标记、阈值边界 |
| `AttributionEngine` | 5 类场景合成数据、置信度、可疑进程排序 |
| `SessionSerializer` | 往返一致性、版本拒绝、损坏行处理、gzip 完整性 |
| `SettingsService` | 默认值、损坏恢复、版本迁移 |

### 14.2 集成测试

- 采集进程测试宿主：启动真实 PDH 查询，运行 60 秒，校验采样连续性、时间戳单调、内存稳定。
- 负载生成器：可控 CPU 忙循环、内存分配、磁盘写入，验证事件检出。
- IPC 测试：管道握手、命令往返、共享内存 seqlock 读写。
- UI 冒烟：启动 → 托盘 → 打开主窗口 → 切换语言/主题 → 退出（手工为主，M3 评估自动化）。

### 14.3 手工验收矩阵

| 维度 | 取值 |
| --- | --- |
| 系统 | Win10 22H2、Win11 22H2+ |
| 显卡 | NVIDIA、AMD、Intel 核显、混合显卡 |
| 显示器 | 单屏、双屏、不同 DPI |
| 游戏状态 | DX11、DX12、无边框窗口、全屏独占 |
| 权限 | 普通用户、管理员 |
| 稳定性 | 8 小时连续运行、睡眠唤醒、锁屏 |

## 15. 里程碑与任务拆解

### M1 / v0.1（核心可见）

1. 解决方案骨架、项目引用、`Directory.Build.props`、`.editorconfig`。
2. Core：数据模型、日志、设置、环形缓冲。
3. Collector：PDH 采样、NT 进程采样、GPU Engine 解析、共享内存写入、管道服务、隐藏窗口。
4. UI：应用外壳、托盘、主窗口、时间轴控件、设置页、i18n 基础设施。
5. 打包：便携目录结构、测量脚本。
6. 出口：连续 30 分钟记录，CPU/GPU/内存/磁盘曲线完整；性能红线达标。

### M2 / v0.5（能定位）

1. 抖动监测、信号计算、事件状态机。
2. 会话快照与 JSONL.gz 原子落盘、留存清理。
3. 归因引擎、报告模板、事件列表与详情。
4. 全局热键、手动标记。
5. 游戏自动识别与排除名单。
6. 双语完成、明暗主题、通知。
7. 出口：5 类测试场景检出率 ≥ 90%；10 分钟静置 0 误报；会话可回放。

### M3 / v1.0（能交付）

1. NVML / ADL / IGCL 接入与降级。
2. 悬浮条、Markdown + PNG 导出。
3. 性能调优与全量 NFR 测量。
4. 8 小时稳定性、睡眠唤醒、多显示器/DPI 验收。
5. 便携 ZIP、第三方声明、文档同步。
6. 出口：全部 NFR 通过；三家 GPU 至少各验证一次；文档与实现一致。

## 16. 编码规范

### 16.1 通用

- 所有项目启用 `<Nullable>enable</Nullable>`、`<ImplicitUsings>enable</ImplicitUsings>`、`LangVersion 12`。
- Core 与 Collector 启用 `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`。
- 文件范围命名空间；4 空格缩进；UTF-8；LF/CRLF 按 `.gitattributes`。
- 类型/方法 `PascalCase`；局部变量/参数 `camelCase`；私有字段 `_camelCase`；常量 `PascalCase`。
- 异步方法以 `Async` 结尾，必须接受 `CancellationToken`；Core 中使用 `ConfigureAwait(false)`。
- 单位写入命名：`LatencyMs`、`AvailableMb`、`BytesPerSec`，禁止无单位数值。
- 禁止 `async void`（事件处理器除外）；禁止 `.Result` / `.Wait()`；禁止 `Thread.Abort`。

### 16.2 互操作

- P/Invoke 统一放 `Interop/`，使用 `[LibraryImport]` + `partial`，设置 `SetLastError`。
- 句柄使用 `SafeHandle` 或明确的 `try/finally` 释放。
- 厂商 DLL 通过 `NativeLibrary.Load` + `GetProcAddress` 动态绑定，任何缺失都走降级路径。
- 结构体显式 `StructLayout` 与 `Pack`，与原生布局一一对应，并加尺寸断言测试。

### 16.3 错误处理

- 只在系统边界（P/Invoke、文件、管道、外部输入）做校验。
- 采集失败记一次降级并继续下一拍；不得让单点失败终止采样线程。
- 用户可见错误提供可执行提示（打开日志、以管理员重启、重试）。
- 禁止吞掉异常不记录；禁止用异常控制正常流程。

### 16.4 注释

- 只解释"为什么"，不解释"是什么"。
- 非显然的阈值、Windows 版本差异、API 坑必须写明原因与来源。
- 每个 `Interop` 声明注明对应系统 API 与最低系统版本。

## 17. 日志与运维

- 目录：`%LOCALAPPDATA%\AzureGameAssistant\logs\`
- 文件：`collector-YYYYMMDD.log`、`ui-YYYYMMDD.log`、`crash-*.log`
- 滚动：单文件 2MB，保留 5 个；启动时清理超限文件。
- 级别：默认 Info；Debug 需在设置中开启，且 10 分钟后自动恢复 Info。
- 内容：时间、进程、线程、级别、事件 ID、消息；不记录窗口标题、命令行、用户名、IP。
- 日志写入失败不影响采集；连续失败 3 次后停止尝试并置标志。

## 18. 风险与技术债

| 风险/债务 | 说明 | 计划 |
| --- | --- | --- |
| PDH 计数器缺失 | 旧系统或精简系统可能没有 GPU 计数器 | 能力探测 + 降级；UI 明示不可用 |
| 厂商 SDK 稳定性 | 驱动升级可能改变 ABI | 动态加载、能力位、超时隔离；M3 建立真机回归清单 |
| WPF 内存超预算 | 自绘图表与资源缓存若不谨慎易超 80MB | 隐藏停渲染、几何缓存上限、每个 PR 测内存 |
| AOT 兼容性 | 反射/动态代码不可用 | Core/Collector 禁止反射；JSON 使用源生成 |
| 热键被游戏拦截 | 全屏独占可能收不到 | 文档说明；评估悬浮条入口；不引入 Hook |
| 判定阈值地域性 | 不同硬件/负载分布不同 | 阈值可调 + 真实数据校准 + 回归测试集 |
| 双语维护成本 | 新增文案易漏翻 | 资源键检查；缺失时回退英文并记 Warning |

## 19. 附录

### 19.1 关键 Win32 / NT API

| 领域 | API |
| --- | --- |
| PDH | `PdhOpenQueryW`、`PdhAddEnglishCounterW`、`PdhCollectQueryData`、`PdhGetFormattedCounterValue`、`PdhGetFormattedCounterArrayW`、`PdhCloseQuery` |
| 进程 | `NtQuerySystemInformation(SystemProcessInformation)` |
| 时间 | `QueryPerformanceCounter`、`QueryPerformanceFrequency`、`GetSystemTimes` |
| 定时器 | `CreateWaitableTimerExW`、`SetWaitableTimer`、`WaitForSingleObject` |
| 共享内存 | `CreateFileMappingW`、`MapViewOfFile`、`UnmapViewOfFile` |
| 管道 | `CreateNamedPipeW`、`ConnectNamedPipe`、`ReadFile`、`WriteFile`、`DisconnectNamedPipe` |
| 热键 | `RegisterHotKey`、`UnregisterHotKey` |
| 托盘 | `Shell_NotifyIconW` |
| 全屏/前台 | `SHQueryUserNotificationState`、`GetForegroundWindow`、`GetWindowThreadProcessId`、`GetWindowRect`、`MonitorFromWindow`、`GetMonitorInfoW` |
| 动态库 | `NativeLibrary.Load`、`NativeLibrary.GetExport`、`NativeLibrary.Free` |

### 19.2 通知状态枚举

| 值 | 含义 | 判定 |
| --- | --- | --- |
| `QUNS_NOT_PRESENT` | 屏幕锁定/屏保 | 不判定游戏 |
| `QUNS_BUSY` | 全屏应用（非 D3D 独占） | 结合窗口矩形判断 |
| `QUNS_RUNNING_D3D_FULL_SCREEN` | D3D 全屏独占 | 判定为游戏 |
| `QUNS_PRESENTATION_MODE` | 演示模式 | 不判定游戏 |
| `QUNS_ACCEPTS_NOTIFICATIONS` | 正常桌面 | 不判定游戏 |
| `QUNS_QUIET_TIME` | 免打扰 | 不判定游戏 |
| `QUNS_APP` | 沉浸式应用 | 结合窗口矩形判断 |

### 19.3 参考资料

- Microsoft Learn：Performance Counters（PDH）、GPU Engine 计数器、`NtQuerySystemInformation`、`SHQueryUserNotificationState`、NativeAOT 部署。
- NVIDIA NVML API Reference；AMD Display Library (ADL) SDK；Intel IGCL 文档。
- Conventional Commits 1.0.0；Semantic Versioning 2.0.0；Keep a Changelog 1.1.0。

### 19.4 待补充

- `build/measure.ps1`、`build/package.ps1` 在 M1 补充。
- 第三方依赖与许可证清单在 M3 补充。
- 归因测试集与基线数据在 M2 建立。
