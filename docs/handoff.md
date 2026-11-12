# 蓝天游戏助手 · 交接文档

> **强制规则：本文件必须在每个任务结束前更新。**
> 任何 Agent 接手项目时，先读本文件，再读 [需求文档](requirements.md)、[开发文档](development.md) 与 [开源调研](research.md)。
> 只记录“当前事实 + 下一步”，不要把设计细节复制进来；设计以开发文档为准，需求以需求文档为准。

| 项目 | 内容 |
| --- | --- |
| 文档编号 | AGS-HANDOFF |
| 当前版本 | v0.1.0 |
| 最后更新 | 2026-11-12 |
| 最后更新人 | Qoder |
| 仓库状态 | M1 全部完成；M2-0/M2-1/M2-2 完成并实机验证（含明日方舟、三角洲行动真实帧采集）；M3-0 传感器边车与 GPU 温度已落地；M3-1 启动自动提权、权限自检、游戏类前台自动帧采集已完成并实机验证；AI 手动分析（OpenAI 兼容）与 MCP 只读服务已落地；折线图默认改为单张合并叠加图（多卡片为可选设置）；长时诊断记录默认关闭、本机已开启（5 秒）；PresentMon 残留 ETW 会话与停帧看门狗已修复；MCP 已登记到 Qoder App；日志常开与按天轮换、LLM 连通测试、CPU/GPU 硬件名称、掉帧标记与高负载进程归因已完成；修复帧率误读（原神 60 帧被读成 30）与部分游戏始终无法识别帧率；新增按月归档的软件问题日志 `logs\issues\issues-YYYY-MM.log`（只收 Warn/Error/崩溃，四进程共用）；FR-006 卡顿判定、FR-007 会话记录与回放、FR-008 本地归因报告、FR-009 全局热键、FR-011 托盘通知已完成；FR-010 游戏自动识别与动态采样已完成并实机验证；FR-015 报告导出（Markdown + 时间线 PNG）已完成；AI 报告已可与会话合并（会话窗口“AI 分析”写入 `<会话>.ai.json`，回放与导出共用） |

---

## 1. 当前快照

| 项 | 值 |
| --- | --- |
| 产品阶段 | M1（v0.1）完成；M2（帧级真相）完成核心闭环；M3-1 权限与自动采集闭环完成；M3 的 AI 手动分析与 MCP 只读服务已落地；界面与采集稳定性进入收尾 |
| 最新里程碑 | AI 报告合并进会话完成：会话窗口新增“AI 分析”，以选中会话（而非实时缓冲）重建事实层，`≥1000 ms` 采集断帧不参与；分析成功且非离线回退时写入 `<会话文件>.ai.json`，详情区显示摘要、主因/置信度、前 4 条证据与前 3 条建议，FR-015 导出自动附带。`SessionSchema.DiscontinuityFrameMs` 成为报告与 AI 共用的断帧阈值。报告同时记录模型与事实哈希，再次打开时命中即复用并提示“已复用”，按钮变“重新分析”，不再重复请求。FR-020 可选“严重事件自动分析”已落地：默认关闭，仅在完整会话落盘后、评分 / 预算 / 同意 / Key 全部满足时触发并写入同一 `.ai.json`。总数 213 项测试通过；Debug/Release 0 警告 0 错误。会话级点击验证待配置 API Key 后执行。 |
| 可运行产物 | `build/run-dev.ps1` 可启动；`build/package.ps1` 默认 AOT 流程可生成便携 ZIP（含 PresentMon）；`build/publish-mcp.ps1` 生成 `artifacts/mcp/AzureGameAssistant.Mcp.exe`；二号屏桌面快捷方式指向 Release |
| 代码分支 | `main`（`feat/m1-foundation` 已 squash 合入，分支保留） |
| 当前提交 | `42651af` |
| 未提交变更 | 无 |
| 阻塞事项 | 帧率准确性需在用户真实游戏（原神 60 帧、三角洲行动）长测确认；AI 窗口与“测试连接”按钮待点击验证；掉帧/高负载标记需在真实游戏复现后核对事件与报告；3 款游戏 PresentMon 实测与反作弊评估待用户游戏环境（已测明日方舟、三角洲行动）；深模式内存/CPU 与 60 分钟连续性待专项测量；本机默认热键 `Ctrl+Alt+M` 被其他程序占用，需用户在设置页改键（实测 `Ctrl+Alt+F9` 可用）；卡顿判定召回率与误报需真实游戏长测；会话级 AI 分析（3.16）待配置 API Key 后点击验证 |

## 2. 环境

| 项 | 值 |
| --- | --- |
| 操作系统 | Windows 10.0.26100（x64） |
| .NET SDK | 9.0.100（目标框架 `net8.0`） |
| Git | 2.54.0.windows.1 |
| 默认分支 | `main` |
| Git 身份 | `worldcopyist <2767263382@qq.com>` |
| 仓库路径 | `C:\Users\worldcopyist\Documents\Qoder\2026-11-07\275e2b44` |
| NativeAOT 工具链 | VS Build Tools 17.14（C++ x64）安装于 `F:\VS2022BuildTools`；Windows SDK 10.0.22621（C 盘既有） |
| 远端 | 无（暂不上传 GitHub） |
| 内核驱动 | PawnIO 2.2.0（用户确认后安装；`DEMAND_START`，签名者 `namazso.eu`，安装包 SHA256 `1f519a22…3032`）；CPU 温度驱动模式使用 |
| 备注 | 仓库根 `NuGet.Config` 清空 fallbackPackageFolders，规避本机无效共享源 `D:\VS2022\Shared\NuGetPackages` |

## 3. 已完成

- [x] 需求访谈与三份基础文档（需求 / 开发 / 调研）、提交规范、README、`.gitignore`、`.gitattributes`。
- [x] Git 仓库初始化与文档阶段提交（`Agent: Qoder`）。
- [x] 方向调整：PresentMon 帧级真相、LHM 厂商传感器边车、OpenAI 兼容 LLM 分析。
- [x] M1-1：解决方案、五个项目、`Directory.Build.props`、`.editorconfig`、警告即错误。
- [x] M1-2：数据模型、`RingBuffer`、`SettingsService`（schemaVersion=2）、`FileLogger`、xUnit 测试。
- [x] M1-3：PDH 系统指标、NT 进程采样、GPU Engine 解析、共享内存 v2（seqlock）、命名管道 v2。
  - 已验证：magic/PID/能力位/GPU/显存/抖动/QPC 单调/心跳；`hello`、`status`、`capabilities`、`pause`、`resume`、`quit` 全部往返正常；未知命令被正确拒绝。
- [x] M1-4：WPF 主窗口、五张指标卡、自绘三轨时间轴（1/5/15 分钟、悬停、缩放、平移）、Top-5 进程、状态卡、设置页、托盘、单实例、采集进程自动启动与退避重启。
- [x] i18n（en 中性资源 + zh-CN 卫星程序集，启动按设置切换）与明暗主题（跟随系统）。
- [x] M1-5：`build/run-dev.ps1`、`build/measure.ps1`（CSV + 中位数）、`build/package.ps1`（便携 ZIP）。
- [x] UI 冒烟：托盘隐藏/双击恢复、暂停/恢复、设置保存、托盘菜单退出（UI 与采集进程均正常退出）、语言启动切换、打包产物可运行。
- [x] 测试：21 个单元测试全部通过（RingBuffer、SettingsService、GpuInstanceParser）；新增图表/悬浮窗设置归一化用例。
- [x] M1-6：Release 连续运行 30 分钟 + 仅托盘 10 分钟；采样无缺口，内存稳定，性能红线全部达标。
- [x] 修正 Release 界面误用 Debug 采集进程的问题；修正 `run-dev.ps1` 空参数报错。
- [x] M1-7 图标：`build/make-icon.ps1` 生成 16–256px 多尺寸 ICO；主窗口、设置窗、EXE 与托盘统一使用该图标；托盘从 EXE 提取失败时回退系统图标。
- [x] M1-7 分指标图表：CPU / GPU / 内存 / 显存 / 磁盘各一张折线图，独立开关并持久化；单图可见时占满宽度，多图两列并可滚动；内存按提交占比、显存自动缩放。
- [x] M1-7 桌面悬浮数据：置顶、鼠标穿透、不抢焦点；四角 + 偏移定位，默认左上；FPS / 平均帧 / 1% Low / CPU / GPU / GPU 温度 / 内存 / 显存内容项开关；设置页与托盘菜单均可开关。帧率与温度在 M2/M3 数据接入前显示“—”。
- [x] M1-7 悬浮窗样式调整：去掉背景面板与边框，只显示数据；数据项从左到右横向排列，宽度不足时按数据项自动换行；文字加细阴影保证可读性；默认位置改为左上角并同步默认配置与测试。
- [x] M1-7 图表弹窗：每张折线图卡头部新增“↗”，把实时 `TimelineChart` 移入独立 `ChartWindow`；主窗口卡片显示占位与“收回图表”；关闭小窗自动归还并恢复按钮；主窗口隐藏但小窗可见时继续按 500ms 节拍刷新。
- [x] M1-7 悬浮窗配色：新增默认 / 浅色 / 天空蓝 / 自定义四套文字主题；自定义颜色支持 `#RRGGBB`，标签同色降透明度，阴影按亮度自动黑/白；设置页提供主题下拉、颜色输入与实时色块，非法值回退白色。
- [x] M2-0 PresentMon 随包：下载 2.6.0 x64 控制台程序到 `third_party/presentmon/`，核验 SHA256 与 Intel Authenticode 签名，附 MIT 许可证与 `SHA256SUMS`；根目录新增 `THIRD-PARTY-NOTICES.md`。
- [x] M2-0 CLI 核验：确认 `--process_id`、`--output_stdout`、`--v2_metrics`、`--session_name`、`--terminate_on_proc_exit`、`--stop_existing_session` 可用；默认输出 2.x PascalCase 指标（`CPUStartTime` 为毫秒）；非提权启动退出码 `6`，stderr 为 `failed to start trace session: access denied.`。
- [x] M2-1 帧解析基础：`FrameCsvParser` 按表头建映射（大小写不敏感，不硬编码列序），输出 `PresentMonFrame`；`FrameStats` 提供瞬时/平均 FPS、1% / 0.1% Low 与基于 120 帧中位数的尖峰判定；新增 10 项单元测试。
- [x] M2-1 帧共享内存：帧区使用独立 `FrameWriteSequence`，写者批量写帧、读者 `ReadFrames` / `TryReadLatestFrame` 整批校验；新增 4 项布局与结构尺寸测试，测试总数 36。
- [x] M2-1 PresentMon 监督器：按 `Frames.TargetPid` 启动子进程（`--process_id` + `--v2_metrics`），64 帧或 100ms 批量写帧；kill-on-close Job Object；1s / 5s / 15s 退避，连续 3 次失败降级；识别退出码 6 与 access denied；目标退出即停止；采集进程同步能力位与 `FrameValid`。
- [x] M2-2 悬浮窗帧数据：UI 增量读取帧区（`ReadFrames(startIndex)`），本地维护 `FrameStats`；FPS / 平均帧 / 1% Low 实时显示，2 秒无新帧回落为“—”。用 240 条合成帧实机验证：显示 `60 / 56 / 15`，停止注入后恢复“—”。
- [x] K-12 NativeAOT：VS Build Tools C++ 工作负载安装到 `F:\VS2022BuildTools`（不占 C 盘）；`vswhere` 可定位实例；`dotnet publish -p:PublishAot=true` 与 `build/package.ps1` 默认 AOT 流程均成功，采集进程原生可执行文件 3.80 MB。
- [x] K-12 AOT 实测：采集进程（浅模式）3 分钟专用工作集 10.1 MB、私有提交 12.5 MB、CPU 0.098%、句柄 304；30 秒退出码 0、CPU 0.72s。
- [x] 打包修正：PresentMon 改放 `collector\presentmon\`（采集进程按自身目录解析），打包脚本校验 SHA256 与 LICENSE；采集进程启动帧采集前校验 `PresentMonBinary` 哈希（失败降级为不可用），新增 2 项测试，测试总数 39。
- [x] M2-2 帧采集界面闭环：管道新增 `frames.start`（携带 `pid`）/ `frames.stop`，`status` 附带 `frames` 状态对象（`state` / `capturing` / `targetPid` / `error`）；采集进程将监督器改为可运行时创建与销毁，同一 PID 复用、切换 PID 先停后启；`permissionDenied` / `unavailable` 作为终态保留，不再被 `finally` 覆盖为 `stopped`。
- [x] M2-2 目标进程选择：状态卡新增下拉框（展开时枚举进程，带窗口优先，显示名称/标题与 PID，最多 200 项，排除助手自身）、开始 / 停止 / 刷新按钮与状态文本；开始成功后写入并保存 `settings.frames.targetPid`，停止时清空；状态文本随 1 秒轮询更新。
- [x] M2-2 主窗口帧轨道：新增 `FrameTimeline`（16384 点定长环，约 256KB）与“帧”图表卡；帧 QPC 时间戳用最近系统采样锚点 + 实测 QPC 频率换算为 UTC，与系统指标共用横轴；每列 min/max 聚合绘制帧时间曲线，尖峰帧画红点（上限 512）；纵轴 16.7 / 33.3 / 50 / 66.7 / 100ms 档位自动缩放；图例“帧时间 (ms)”+“尖峰”，悬停显示帧时间与 FPS；卡片头部显示瞬时 FPS 与“平均 / 1% Low”；支持“↗”脱离与 `charts.frames` 开关（默认开启）。
- [x] M2 修正：PresentMon 2.6 默认 `--v2_metrics` 输出 `FrameTime` / `CPUBusy` 等列名，`FrameCsvParser` 同时兼容 v2 名称与旧 `Ms*` 别名；新增回归测试，真实帧数据恢复。
- [x] M3-0 传感器边车：新建 `AzureGameAssistant.Sensors`（LHM 0.9.6，GPU-only），写 `Local\AzureGameAssistant.Sensors.v1`（128KB、900 槽、seqlock）；检测 WinRing0/inpoutx64/RTCore64 等内核驱动，检测到即以退出码 3 退出；无 GPU 退出码 4；采集进程用 Job Object 监督并按 1s/5s/15s 退避重启。
- [x] 遥测 v3：Magic `'AGS3'`、Protocol 3、Sample 槽位 256 字节，新增 `CpuSensorSlot C0` 与 `CpuSensorValid` 位；Event/Frame 区偏移按新布局计算，布局测试同步更新。
- [x] GPU 温度链路：边车读取 `GPU Core` / `GPU Hot Spot` / 核心频率 / 风扇 / 显存，采集进程并入 `V0`，悬浮窗“GPU 温度”显示实机 53°C（热点 61°C）。
- [x] CPU 温度链路：采集进程并入 `C0`，悬浮窗新增“CPU 温度”行并在设置页提供开关；边车每 5 秒尝试 ACPI `MSAcpi_ThermalZoneTemperature`，缺失时保持 `NaN`。
- [x] 提权 UX：非管理员时状态卡显示“以管理员身份重启”；UI 以 `runas` 重启并携带 `--elevated-relaunch`，等待旧实例释放互斥体最多 10 秒；用户取消 UAC 时恢复采集并提示失败，不静默重试。
- [x] 真实游戏验证：明日方舟（PID 18096）提权采集，共享内存 `caps=7`、`flags=15`、帧索引持续增长；悬浮窗显示“帧率 166 / 平均帧 143 / 1% Low 帧 63 / CPU 29% / GPU 5% / GPU 温度 53°C / CPU 温度 — / 内存 59%”；主窗口帧图显示 149.7 FPS、平均 136 / 1% Low 20。
- [x] 测试：49 项全部通过（新增 `SensorLayoutTests` 与旧 schema 自动开启传感器用例）。
- [x] M3-1 启动自动提权：`requestElevationOnStartup` 默认开启，非管理员实例在创建窗口前以 `runas` 重启；取消 UAC 继续浅模式且只请求一次；`--no-elevate` 可临时跳过；旧实例先释放单实例互斥体，新实例最多等待 10 秒接管。本机（UAC 策略 0）实测静默提权成功，仅保留一个界面进程。
- [x] M3-1 权限自检窗口：主界面“权限自检”按钮打开诊断窗口，逐项检查管理员权限、Performance Log Users、PresentMon 路径与 SHA256、ETW 探针会话、遥测心跳、帧采集状态、GPU 传感器、CPU 温度来源；支持重新检测、复制报告、未提权时提权；ETW 探针只创建并立即停止随机会话，不接触游戏进程。实机结果：7 项 OK、Performance Log Users 警告、CPU 温度缺失并给出驱动模式说明。
- [x] M3-1 前台自动帧采集：`frames.autoCapture` 默认开启；每 4 个状态节拍检查前台 PID，排除助手自身与 shell 进程，要求进程存活且有主窗口；`permissionDenied` 冷却 5 分钟、`unavailable` 冷却 1 分钟；手动停止后同一 PID 不再自动启动。实机启动 2 秒内自动开始采集明日方舟（PID 18096），悬浮窗显示“帧率 127 / 平均帧 136 / 1% Low 20”。
- [x] M3-1 CPU 温度来源：设置页新增“CPU 温度来源”（ACPI / 驱动 / 关闭），采集进程与边车通过 `--cpu-mode` 传递；默认 ACPI，驱动模式只在用户显式选择时启用 LHM CPU 组，程序不自动安装 PawnIO；本机 ACPI 热区不可用，边车日志明确记录，权限自检显示缺失原因。
- [x] 设置重启判定修正：改为与启动时应用的采样间隔、传感器开关/间隔、CPU 模式比较，避免设置保存后同一实例被提前归一化导致采集进程不重启。
- [x] 测试：53 项全部通过（新增 CPU 模式归一化与自动采集/提权设置用例）；Release 构建 0 警告 0 错误。
- [x] M3-1 CPU 温度驱动模式实测：经用户确认后安装 PawnIO 2.2.0（官方 GitHub 发布，SHA256 与 Authenticode 校验通过）；设置切到 `driver` 后边车读到 `Core (Tctl/Tdie) 47°C`、`Package 45W`，悬浮窗显示“CPU 温度 47°C”，权限自检“CPU 温度”变为 OK。驱动为 `DEMAND_START`，不随系统自动加载；用户知悉 ACE 可能将其视为环境异常，可执行 `sc stop PawnIO` 或卸载。
- [x] M3 AI 接口：新增 `AzureGameAssistant.Ai` 库（预设/地址校验、Credential Manager、事实层、脱敏、提示词、OpenAI 兼容 SSE/JSON 客户端、严格 JSON 校验、危险建议过滤、预算与用量、本地兜底、报告落盘）。主界面“AI 分析”打开 `AiAnalysisWindow`：请求摘要、JSON 预览、同意、进度、取消、结构化结果与本地兜底；设置页新增 AI 分组与 Key 保存/删除。默认关闭、手动触发、Key 只存凭据管理器。
- [x] M3 MCP：新增 `AzureGameAssistant.Mcp` 只读 stdio 服务（JSON-RPC 2.0，协议 `2024-11-05`），固定 6 个有界工具：快照、历史聚合、帧统计、Top 进程、链路诊断、采集状态；`build/publish-mcp.ps1` 生成单文件产物。实机以 stdio 对运行中的采集进程冒烟：`sampleCount` 1801、`frameCount` 78234，非管理员时状态正确标记 `degraded` 并说明控制管道需同权限。
- [x] 测试补齐：新增 `AiEndpointTests`、`AiRedactorTests`、`AiFactsBuilderTests`、`AiReportValidatorTests`、`OpenAiCompatibleClientTests`、`AiAnalysisServiceTests`、`McpServerTests`；测试总数 106 项，全部通过；Release 全解决方案 0 警告 0 错误（因运行实例锁定 `bin\Release`，使用 `BaseOutputPath` 输出到 `artifacts/build` 验证）。
- [x] 折线图默认统一单窗口：主窗口只保留一个“实时时间轴”区域放置全部指标图；新增“布局设计”勾选项，只有勾选后才显示每张卡的“↗”脱离按钮；取消勾选或关闭小窗时全部图表自动合并回主窗口。实机截图确认默认状态没有多余窗口，帧/CPU/GPU 等曲线同轴显示。
- [x] 自动采集仅针对游戏类前台进程：要求进程存活、有主窗口，并且全屏（无 `WS_CAPTION` 且覆盖整个显示器）或 GPU 占用 ≥ 5%；前台切到桌面应用时保持正在出帧的游戏会话；同一 PID 20 秒内不重复尝试；`permissionDenied` 冷却 5 分钟、`unavailable` 冷却 1 分钟。
- [x] PresentMon ETW 残留会话修复（三角洲行动帧率）：定位到 `--stop_existing_session` 在存在同名残留会话时会让新会话在约 1–16 秒后静默停帧；启动流程改为先 `--terminate_existing_session --no_csv` 清理，再以不带 `--stop_existing_session` 的参数启动正式会话。读循环改为 500ms 轮询：20 秒无首帧、或目标仍活跃（前台或 GPU ≥ 5%）但 10 秒无新帧时自动重启。实机重启后 75 秒连续采集 8,379 帧、最新帧延迟 1.0s，MCP 帧统计平均 119.3 FPS、1% low 73.4。
- [x] MCP 接入 Qoder App：用户级自定义连接器 `azure-game-assistant`（stdio，指向 `artifacts/mcp/AzureGameAssistant.Mcp.exe`），6 个只读工具可枚举；发布替换 exe 后曾出现一次 `MCP_PROTOCOL_CONNECTION_CLOSED`，执行 `retry` 后恢复。
- [x] 折线图默认合并叠加（本轮）：`ChartSettings.Separate` 默认 `false`，主窗口只显示一张合并图；`ChartSeries` 标志枚举按设置决定叠加哪些线条，共用 0–100% 纵轴（显存按总容量百分比归一化），帧时间使用右侧副轴（ms）；图例、悬停与线色按启用指标动态生成。设置页新增“分开显示图表卡片”，勾选后回到原有独立卡片布局，“布局设计/脱离小窗”只在该模式下出现。合并图卡片直接拉伸铺满“实时时间轴”卡片第 1 行（不再放进 `ScrollViewer`），随窗口高度自适应且不出现滚动条；实测一张图六条彩色曲线、图例与坐标轴完整。
- [x] 内存口径修正：此前卡片/图表使用“已提交 / 提交上限”（提交上限 = 物理内存 + 页面文件，16GB 机器显示为 31.9GB）。新增 `SystemMemory`（`GlobalMemoryStatusEx` 读取总物理内存并缓存），`TelemetryMath.MemoryUsagePct` 改为 `(总物理内存 − Available MBytes) / 总物理内存`，提交数据只保留给压力判定；卡片副标题改为“使用 x / y GB”，MCP 快照新增 `memory.totalMb` / `usedMb`。实机 16GB 机器显示 15.9GB 总容量。离线渲染探针验证合并图控件正常，并再次确认运行态无裁剪。
- [x] AI 模型列表查询：`AiEndpoint.TryNormalizeModels` 从 Base URL 推导 `GET /models`；`OpenAiCompatibleClient.ListModelsAsync` 解析 `data[].id`、去重排序并过滤非对话模型，401/超时/非 JSON 均有可读错误。设置页模型行新增“获取模型”按钮，结果出现在模型输入框旁的下拉列表，选择后写回模型名；未保存 Key 时提示先保存，只有点击时才联网。实测设置窗布局正常、`deepseek-chat` 保留、无 Key 时提示正确；新增 5 项客户端测试与端点测试，测试总数 118。
- [x] 长时诊断记录（FR-023）：新增 `LongRunRecorder`（CSV/事件/摘要/上下文）与 `DiagnosticsSnapshot`；UI 每个刷新节拍组装快照，按 5/10/30 秒节流写入 `%LOCALAPPDATA%\AzureGameAssistant\diagnostics\diag-*\`。`samples.csv` 覆盖性能、内存/提交、磁盘、抖动、帧率与 low 帧、温度/显存、链路状态与 UI 自身开销；`processes.csv` 每 30 秒 Top-5；`events.jsonl` 记录采集 PID 变化、帧状态、数据缺口与温度可用性；退出生成 `summary.txt`（覆盖率、缺口、帧采集中断、帧率分位、温度覆盖、UI 内存/CPU）。设置页新增“诊断记录”分组与“打开记录目录”，保存后立即生效。本地已开启（5 秒）；实机验证首行即含数据、30 秒内生成进程排行、事件与上下文正确；新增 `LongRunRecorderTests`，测试总数 121。
- [x] 日志复盘与三角洲帧率消失修复（项 1、8）：复盘 10.3 小时日志发现 294 次 PresentMon 被本程序 10 秒停帧看门狗杀进程、平均约 125 秒一次；界面在 2 秒无新帧后清空 FPS。自然恢复时记录“帧数据中断 N 秒后自行恢复”，停顿消息附带实际无数据秒数；快照新增帧年龄与帧写索引（`samples.csv` 新增 `frame_age_s` / `frame_write_index`），`events.jsonl` 新增帧率中断起止事件与摘要统计。
- [x] 帧率消失根因返工（项 8）：用户否定“保持 + 恢复中…”的权宜方案。复查运行日志确认 PresentMon 静默 2–5 秒后会自行恢复（`帧数据中断 2.0 秒后自行恢复`），而旧看门狗在 6/10 秒就杀掉仍存活的进程，再叠加 ETW 清理与退避把抖动放大成 12–16 秒空洞。改为：停帧兜底 25 秒且仅目标活跃时触发；同类停帧不累计“连续 3 次失败”；首帧 20 秒超时也要求目标活跃；仅在上次被强制结束或非零退出后才做 `--terminate_existing_session` 清理（正常退出直接重启）；会话清理超时 800ms、重启退避 0.25/1/3 秒。界面删除“恢复中…”文案与资源，短暂无帧时保持最后一次读数（主窗口 30 秒、悬浮窗 20 秒），目标退出或停止采集立即清空。
- [x] 硬件名称（项 2）：CPU 卡片标题下显示处理器型号（去除厂商前缀、限制长度），GPU 卡片显示主显卡型号（优先独显、跳过虚拟适配器）；两者均带完整名称悬浮提示。
- [x] LLM 连通测试（项 3）：`OpenAiCompatibleClient.TestConnectionAsync` 发送一次最小对话探测请求；设置页 API 地址行新增“测试连接”按钮，成功显示端点与耗时，401/非 JSON/网络错误给出可读原因；新增 3 项客户端测试。
- [x] 日志常开与按天轮换（项 4、5）：`LogSettings { Enabled = true, RetentionDays = 3 }`；`FileLogger.Enabled` 支持运行时停写，界面与采集/传感器/MCP 启动时都读取同一设置；`LogMaintenance.Prune` 在启动与保存设置后删除超过保留天数的 `*.log`（1–30 天，默认 3 天），保留既有 2MB × 5 分卷；设置页新增“日志”分组与保留天数输入；新增 `LogMaintenanceTests`。
- [x] 掉帧标记与高负载进程归因（项 6、7）：新增 `FrameDropTracker`（容量 64，1 秒合并、长停/CPU 等待/GPU 等待/混合分类、嫌疑进程）与 `HighLoadTracker`（容量 16，CPU/GPU/显存/内存/磁盘阈值、连续 3 采样、第一/第二进程）；事实层 schemaVersion 2 新增 `drops[]` / `highLoads[]`（进程名统一匿名化），提示词 schema 新增 `dropAnalysis[]` / `highLoadAttribution[]`，校验器强制逐条掉帧原因与每个高负载资源的进程归属，本地报告同样生成；分析窗新增“掉帧分析”“高负载归因”卡片；长时诊断写入 `frame_drop` / `high_load` 事件与摘要计数。新增 `RuntimeTrackerTests`、事实层/校验器/诊断记录用例，测试总数 135 项全部通过；Release 全解决方案 0 警告 0 错误。
- [x] 帧率误读修复（原神 60 帧显示 30）：`FrameStats` 不再用 `1000 / 最后一帧耗时` 当瞬时 FPS，改为“最近约 1000ms 帧时间窗口的帧数 / 窗口总耗时”；平均帧与 1%/0.1% Low 改为最近约 10 秒窗口（上限 4096 帧），旧采集、菜单与加载不再长期拖低；瞬时窗口上限 256 帧。单元测试新增“单次 33.3ms 漏帧时瞬时仍为 57–59”与“旧慢采集被 10 秒窗口淘汰”两项，测试总数 137 项全部通过。
- [x] 断帧空洞排除：PresentMon 在停顿后会以单帧上报整段空闲（实测 `frame_time_last_ms=19278.03`，此前还有 1529ms），这类帧只在帧图上画断点，不进入 `FrameStats`、掉帧追踪与尖峰计数；阈值 `DiscontinuityFrameMs = 1000`（1.5 秒的停顿帧同样排除，60 帧游戏不存在合法的 1 秒单帧）。
- [x] 部分游戏始终无法识别帧率：`CollectorHost.StartFrames` 对同一 PID 不再因 `Unavailable` 永久返回 false，失败/结束的会话可被替换重启；`PresentMonSupervisor` 在“目标存活但 20 秒无首帧”时按 2–15 秒退避重试且不累计失败次数（日志限流）；自动采集在目标处于 `unavailable` 但玩家重新回到游戏时立即重试，不再固定 20 秒冷却。
- [x] 无帧新鲜度窗口由 2 秒放宽到 5 秒（仅诊断与日志口径，界面保持读数不受影响），避免一次性停顿刷出大量“帧率数据中断”。
- [x] 软件问题日志（FR-026）：新增 `IssueLog` 把 Warn / Error / 未处理异常归档到 `logs\issues\issues-YYYY-MM.log`，月份按写入时刻计算（跨月自动换文件、无需重启）；每个文件首次写入补版本头；`FileLogger` 在 Warn 及以上转发到归档（Info 不进），界面 `AppDomain.UnhandledException` 补齐非 `Exception` 分支，采集 / 传感器 / MCP 新增 `AppDomain.UnhandledException` 处理器；`AppPaths.IssueLogsDirectory` 启动即创建；按天保留只扫顶层 `*.log` 故不影响归档；设置页“日志”新增“打开问题日志”按钮。新增 `IssueLogTests`（6 项），测试总数 143 项全部通过；Release 全解决方案 0 警告 0 错误。
- [x] 问题日志实机验证：以 `AzureGameAssistant.Collector.exe --interval 0` 触发参数错误（在互斥体检查之前记日志，不影响运行中的实例），`logs\issues\issues-2026-11.log` 生成成功，内容为版本头 + `2026-11-10 01:24:18.285 [ERR] [collector:41716] --interval 需要 100–10000 之间的毫秒数`，与运行中的 UI/采集/传感器进程互不干扰。

### 3.1 性能实测（Release，采样 1000ms，专用工作集口径）

**主窗口显示，30 分钟连续运行（169 个采样）**

| 进程 | 专用工作集（中位数） | 总工作集 | 私有提交 | CPU（中位数） | 预算 |
| --- | --- | --- | --- | --- | --- |
| Collector | 11.8 MB | 50.9 MB | 16.1 MB | 0.098% | ≤30 MB / ≤0.3% |
| UI | 45.5 MB | 136.0 MB | 71.2 MB | 0.288% | 主窗口 ≤80 MB |

**仅托盘，10 分钟静置（56 个采样）**

| 进程 | 专用工作集（中位数） | 私有提交 | CPU（中位数） | 预算 |
| --- | --- | --- | --- | --- |
| Collector | 12.8 MB | 16.6 MB | 0.098% | ≤30 MB / ≤0.3% |
| UI | 47.0 MB（末段稳定 43.5 MB） | 72.4 MB | 0.000% | 托盘 ≤60 MB |

- UI 保持软件渲染（ADR-021）；隐藏后刷新计时器停止，CPU 中位数为 0。
- 测量脚本已加入 `Process Working Set - Private` 列，与需求文档“以专用工作集为准”一致；同时保留总工作集与私有提交。

### 3.2 UI 增强性能抽测（Release，2026-11-07，采样 1000ms）

**主窗口可见 + 悬浮窗开启，1 分钟（10 个采样，磁盘图关闭）**

| 进程 | 专用工作集（中位数） | 总工作集 | 私有提交 | CPU（中位数） | 句柄 |
| --- | --- | --- | --- | --- | --- |
| Collector | 13.1 MB | 50.7 MB | 16.2 MB | 0.115% | 375 |
| UI | 47.4 MB | 137.2 MB | 71.1 MB | 0.300% | 609 |

**主窗口隐藏 + 悬浮窗开启，1 分钟（11 个采样）**

| 进程 | 专用工作集（中位数） | 总工作集 | 私有提交 | CPU（中位数） | 句柄 |
| --- | --- | --- | --- | --- | --- |
| Collector | 12.7 MB | 50.7 MB | 16.0 MB | 0.093% | 372 |
| UI | 47.1 MB | 137.6 MB | 70.3 MB | 0.128% | 594 |

- 与 3.1 托盘基线（UI 47.0MB）相比，悬浮窗内存增量约 0.1MB，满足 ≤8MB 预算；UI 与采集进程仍分别在 80MB / 30MB 红线内。
- 主窗口隐藏后因悬浮窗仍需 2Hz 刷新，CPU 不再为 0，但 0.128% 仍低于 0.3% 目标。
- 本次为 1 分钟抽测；30 分钟连续性与 8 小时稳定性仍按原计划复测。

### 3.3 连续性与正确性

- 采样连续性：30 分钟窗口内 `writeIndex` 203 → 2027，历时 1823.9s，实际 1824 个采样点，无缺口。
- 时间轴：1 分钟与 15 分钟视图均已实机截图核对，窗口跨度准确、三轨曲线连续。
- 稳定性：30 分钟无崩溃、无未处理异常；心跳 659–873ms（间隔 1000ms）。
- 句柄：UI 564 → 568 基本稳定；Collector 381 → 455 缓慢上升，托盘 10 分钟窗口内 468 → 466 回落，暂不判定为泄漏，列入 8 小时稳定性复核（K-15）。

### 3.4 无背景悬浮窗抽测（Release，2026-11-07，1 分钟，采样 1000ms）

| 条件 | UI 专用工作集 | UI CPU（中位数） | Collector 专用工作集 | Collector CPU |
| --- | --- | --- | --- | --- |
| 主窗口可见 + 悬浮窗 | 46.3 MB | 0.423% | 12.2 MB | 0.081% |
| 主窗口隐藏 + 悬浮窗 | 47.4 MB | 0.094% | 12.0 MB | 0.070% |

- 去掉背景与改用横向 `WrapPanel` 后，悬浮窗内存增量与 3.2 抽测基本一致；隐藏主窗口时 UI CPU ≤ 0.1% 目标仍满足。
- 主窗口可见时 UI CPU 为 0.423%，高于 3.2 的 0.300%；两次为同条件短测，差异原因未定位（可能为图表重绘与系统活动波动），暂不归因于悬浮窗样式，列入 8 小时稳定性复核。Collector 仍在 0.3% 红线内。

### 3.5 图表弹窗与悬浮窗配色抽测（Release，2026-11-07）

| 条件 | 采样方式 | UI CPU |
| --- | --- | --- |
| 主窗口隐藏 + CPU 图表小窗可见 | 5 秒 `TotalProcessorTime` | 0.391% |
| 关闭图表小窗、主窗口仍隐藏 | 5 秒 `TotalProcessorTime` | 0.052% |

- 小窗可见时刷新计时器保持运行，CPU 高于纯托盘基线（≈0.05%）但低于 0.5%；关闭后立即回落到空闲水平，验证计时器门控正确。
- 磁盘图默认关闭，因此主窗口仅有 4 个“↗”按钮；点击 CPU 图表后新窗口标题为“CPU · 蓝天游戏助手”，关闭后 4 个按钮全部恢复可用。
- 悬浮窗配色实测：选择“天空蓝”后数值变为 `#5FC0FF`、标签变浅蓝；选择自定义 `#00FF7F` 后全部数值变绿，标签同色但降低不透明度；设置文件写入 `"theme": "Custom", "color": "#00FF7F"`，重启后可保持。验收后已将本机设置恢复为默认白色。
- 以上为短时抽测，不替代 8 小时稳定性测试；图表小窗位置与尺寸未持久化（K-20）。
- 帧数据显示验证：向共享内存注入 240 条合成帧（16.7ms，每 50 帧一次 66.8ms 尖峰）后，悬浮窗显示 `60 / 56 / 15`；停止注入 2 秒后回落为“—”。真实 PresentMon 帧数据待提权与游戏环境验证。

### 3.6 AOT 采集进程实测（Release NativeAOT，浅模式，采样 1000ms）

| 项 | 值 |
| --- | --- |
| 可执行文件 | `AzureGameAssistant.Collector.exe`，3,803,648 字节 |
| 专用工作集（3 分钟中位数，16 个采样） | 10.1 MB |
| 私有提交 | 12.5 MB |
| CPU（中位数） | 0.098% |
| 句柄 | 304 |
| 30 秒冒烟 | 退出码 0，CPU 0.72s，启动与退出正常 |

- 命令：`dotnet publish src/AzureGameAssistant.Collector/AzureGameAssistant.Collector.csproj -c Release -r win-x64 -p:PublishAot=true -o artifacts/collector`。
- 本次仅为浅模式基线；开启帧采集（PresentMon 子进程 + ETW）后的深模式内存与 CPU 列入 M2 验收复测。
- 便携包实机验证：把采集进程临时指向 `explorer.exe` 运行 15 秒，日志显示从 `collector\presentmon\` 找到并校验 PresentMon，随后按预期报“权限不足”（非提权），证明包内路径与哈希校验生效；验证后已将 `frames.targetPid` 恢复为 `null`。

### 3.7 帧采集界面闭环实测（Release，2026-11-07）

- 独立采集进程 + 管道直测：`frames.start` 指定 `explorer.exe` PID 后状态依次为 `starting` → `permissionDenied` → `stopped`，`error` 为“需要管理员权限或 Performance Log Users”；`frames.stop` 返回 `stopped`。
- UI 闭环（UIA 驱动）：下拉框选中 `explorer (11840)`，点击“开始”后状态卡显示“帧采集需要管理员权限（PID 11840）”，详情显示“需要管理员权限或 Performance Log Users”；点击“停止”后显示“帧采集未运行”，`settings.json` 的 `frames.targetPid` 恢复为 `null`。
- 未提权环境下 `permissionDenied` 为预期结果，真实帧数据采集需 M2-0 提权与游戏环境验收；本轮未新增单元测试（构建 0 警告 0 错误，39 项测试通过）。
- 验证用的 UI 与采集进程已停止，未遗留后台进程。

### 3.8 主窗口帧轨道实测（Release，2026-11-07）

- 方法：向共享内存帧区注入合成帧（60 FPS、16.7ms，每 20 帧一次长帧），`FrameWriteSequence` / `FrameWriteIndex` 位于头部偏移 56 / 64；用 UIA 定位主窗口并以 `PrintWindow(PW_RENDERFULLCONTENT)` 截图（无边框游戏全屏遮挡时普通截图会截到游戏）。
- 结果：主窗口首张卡片显示“帧 / 平均 57 · 1% Low 16 / 60.0 FPS”，图例显示“帧时间 (ms) ● 尖峰”，纵轴上限 100ms，粉色帧时间曲线连续、尖峰处有红色圆点；帧时间戳与系统采样对齐（末帧相对最近采样 +0.70s），未出现横向错位。
- 构建 0 警告 0 错误；新增 5 项 `FrameTimeline` 测试（顺序、环绕、截断、QPC→UTC 换算、零频回退），测试总数 44 项全部通过。
- 验证用的 UI 与采集进程（PID 12812 / 14348）在验收后停止，未遗留后台进程。

### 3.9 传感器、GPU 温度与真实游戏验证（Release，2026-11-07）

**环境**：AMD Ryzen 5 5600 + NVIDIA RTX 4060 Laptop（8GB），Windows 10.0.26100；Release UI 以管理员身份运行。

| 项 | 观测值 |
| --- | --- |
| 传感器共享内存 | `gpuTemp=53`、`hotSpot=61.3`、`coreClock=480`、`vramUsed=2673`、`cpuTemp=NaN` |
| 遥测采样 | `caps=7`（帧 + 厂商传感器 + 完整进程访问）、`flags=15`（进程 + GPU + 厂商 + 帧） |
| 帧区 | `frameIndex=1979` 持续增长，解析恢复 |
| 悬浮窗 | 帧率 166 / 平均帧 143 / 1% Low 帧 63 / CPU 29% / GPU 5% / GPU 温度 53°C / CPU 温度 — / 内存 59% |
| 主窗口帧图 | 149.7 FPS；平均 136 / 1% Low 20 |

- CPU 温度：本机 ACPI 热区返回“不支持”，`Win32_TemperatureProbe` 与热性能计数器均为空；AMD Ryzen 5 5600 无可用的用户态温度接口。按 ADR-022 不加载内核驱动，界面保持“—”。这是平台限制，不是悬浮窗缺陷。
- GPU 功率：LHM 0.9.6 对该 RTX 4060 不暴露 `SensorType.Power`，`PowerW` 为 `NaN`；不影响温度、频率、风扇与显存。
- 反作弊边界：未注入、未 Hook、未读写游戏内存，只运行官方 PresentMon ETW 会话并读取厂商公开传感器；未加载任何内核驱动。
- 验证进程（UI / Collector / Sensors / PresentMon）在验收后停止；`settings.json` 的 `frames.targetPid` 已清空。
- 本轮新增 5 项测试（传感器布局、CPU 温度标志、旧 schema 迁移），测试总数 49 项。

### 3.10 帧率误读与无法识别复盘（Release，2026-11-10）

**现象与证据**

| 项 | 观测 |
| --- | --- |
| 误读（原神 60 帧显示约 30） | UI 日志出现过 486.6 / 5500 / 3.5 / 7.4 FPS 的单帧读数；`diag-20261109-234854` 中真实速率（帧数和 / 总帧时间）中位数 54.3，而 `fps_inst` 中位数 59.6、`fps_avg` 中位数仅 39.9，帧时间在 16.7ms 与 30–34ms 两簇间切换 |
| 断帧空洞 | 运行中 PresentMon 上报单帧 `frame_time_last_ms=19278.03`（另见 1529.28），停顿被当成一帧渲染，旧逻辑直接拉低平均与低帧 |
| 无法识别 | 同一 PID 的监督器进入 `Unavailable` 后 `frames.start` 永久返回 false（日志中明日方舟连续 36 分钟 `unavailable`）；后台时 20 秒无首帧即耗尽 3 次失败预算；自动采集在失败目标保持前台时每 20 秒才重试一次 |

**处理与验证**

- 瞬时 FPS = 最近约 1000ms 窗口内帧数 / 窗口总帧时间；平均与低帧 = 最近约 10 秒窗口。单元测试：60 帧序列插入一帧 33.3ms 后，瞬时仍为 57–59；10 秒窗口外的旧慢采集不影响平均与 1% Low。
- ≥1000ms 的单帧只进图表断点，不进统计；掉帧追踪与尖峰计数同样跳过（本机诊断里 1529ms 与 19278ms 两类停顿帧都已被排除）。
- 会话可复活、无帧慢速重试、自动采集立即重试均已落地；Release 0 警告 0 错误，137 项测试通过。
- **未完成项**：本机没有能持续 60 帧出帧的测试对象（明日方舟空闲时约 1–8 帧，OBS 空场景约 1–3 帧），无法做 60 帧实时读数对照；准确性验证依据为诊断数据回放与单元测试，需用户在原神 / 三角洲行动中长测确认。验证用的设置改动（临时目标 OBS）已还原，应用已从桌面快捷方式重新启动并保持运行。

### 3.11 卡顿判定、热键与托盘通知（Release，2026-11-10）

- `DetectionEngine` 已实现：120 帧中位数、30 采样基线（切换前台 PID 时清空）、帧尖峰/显示空洞/抖动/CPU/GPU/内存/磁盘信号、2 秒静默合并、5 秒冷却、连续评分、深浅模式；单帧 ≥100ms 或连续 2 次尖峰在深模式直接确认。
- 主界面新增“最近卡顿”列表（容量 50）：时间、评分、原因、命中信号、进程名、时长；设置页新增 14 项阈值并可即时生效。
- 全局热键：`RegisterHotKey` + `MOD_NOREPEAT`，格式解析支持 `Ctrl/Alt/Shift/Win + 字母/数字/F1–F24`；注册失败记录 Win32 错误码并弹出托盘气泡；设置页“界面”分组可改键并注明独占全屏限制。
- 托盘：悬停显示 CPU / GPU / 内存；事件气泡只含评分，点击气泡或双击图标打开主界面；通知可静音。
- 修复：`MarkStutter` 原先直接添加事件、`MarkManual` 又入队，导致一次按键出现两条事件；改为统一 `DrainStutterEvents` 出队。实测 3 次按键生成 3 个事件。
- 验证：Release 0 警告 0 错误；156 项测试全部通过。本机 `Ctrl+Alt+M` 被其他程序占用（错误码 1409），已还原用户原设置并以桌面快捷方式重启；测过的 `Ctrl+Alt+F9` 可正常注册。
- 未完成：显存压力与热降频信号仍是归因阶段规划项（需求 FR-006 不含）；FR-006 的“7 类场景检出率 ≥90% / 10 分钟 0 误报”需真实游戏长测；FR-009 的 ±30 秒切片要等 FR-007 会话记录落地。

### 3.12 会话记录与回放（Release，2026-11-10）

- 新增 `Core/Sessions`：`SessionModels`（schema v2 + 源生成序列化）、`SessionStore`（gzip JSONL 原子写 / 首行枚举 / 流式读取 / 留存 / 启动清理）、`SessionRecorder`（事件号去重、30 秒后滚、退出冲刷）、`LocalReportBuilder`（主因、可疑进程、帧证据、置信度、1–3 条建议，中英双语）。
- 落盘策略：事件确认即写 `state=recording` 的完整可读文件，列表 3 秒内可见；`end + 30s` 原位重写为 `state=complete`；所有写入走 `.tmp` + `Flush(true)` + `File.Move(overwrite)`；启动删除 `*.tmp` 与未完成会话。
- 文件：`%LOCALAPPDATA%\AzureGameAssistant\sessions\<yyyyMMdd-HHmmss>-<eventId>.agsession.jsonl.gz`；帧 64 帧/批，短字段数组；尾部 SHA256 覆盖前序行；头部重复事件摘要，列表只读首行。
- 界面：主界面卡顿卡新增“会话”按钮，托盘菜单同步入口；`SessionsWindow` 左侧事件列表 + 右侧 `TimelineChart`（Combined）回放 + 元信息/数据缺口/本地报告/尾部统计；支持刷新、打开目录、删除（同步删除同名 `.ai.json`）。
- 设置：新增“会话存储”卡片，最多会话数（1–500，默认 20）与总容量 MB（10–10000，默认 500）。
- 验证：Release 0 警告 0 错误；162 项测试通过，其中新增 6 项覆盖“先写半成品再补全并去重 / 启动清理 / 双上限淘汰 / 未知主因仍有下一步 / 帧等待证据写入时间轴 / 非有限数清洗”。实机（明日方舟前台）点击“标记卡顿”：0.14 秒出现 `recording` 文件，30 秒后原位变为 `complete`（38 采样、19 帧、SHA256、无 `.tmp` 残留）；会话窗口可列出、回放合并时间轴并显示本地报告与建议。实机首轮发现 NaN/Infinity 使样本序列化中断、只留下半截 `.tmp`，已在写入边界统一清洗并补回归测试。

### 3.13 本地归因报告 FR-008（Release，2026-11-10）

- 分类：显存 ≥92% → Vram；内存压力或可用 <512MB → Memory；磁盘压力或延迟 ≥50ms 且占用 ≥50% → Disk；GPU 温度 ≥85°C / 热点 ≥95°C 且占用 ≥40%，或 CPU 温度 ≥90°C 且占用 ≥40% → Thermal；CPU/GPU 尖峰按峰值取舍；帧尖峰/显示空洞 → FramePacing；其余 → Manual / Unknown。
- 帧证据：≥1000ms 的采集断帧单独计 `gapCount`，不再计入最大帧、尖峰与等待均值；新增 `consistent`，按主因检查等待方向或尖峰/断帧是否吻合。
- 报告内容：可疑进程前 3（`cpuPeak + 0.8×gpuPeak + wsDelta/64`）、窗口峰值（含可用温度）、帧证据（最大帧/尖峰/断帧/有效帧）、一致性和 1–3 条建议；无帧数据时提示开启采集后复现。
- 界面：`SessionFrameEvidenceLine` 增加断帧数，帧证据下新增“主因与帧证据：一致/不一致或证据不足”；`CauseThermal` 中英标签；修正主界面与设置页过时的“后续版本提供”提示。
- 验证：`LocalReportBuilderTests` 7 类场景 7/7 正确，另有断帧/尖峰分离、一致性正反例、无数据兜底、深模式帧证据、英文输出；总数 171 项，Release 0 警告 0 错误。实机标记后报告为 `frameCount 69 / gapCount 8 / spikeCount 0 / maxFrameTimeMs 896.5 / consistent true`，会话窗口按新格式显示。

### 3.14 游戏自动识别与动态采样 FR-010（Release，2026-11-10）

- 判定链：遥测 `ForegroundPid` → 排除名单（桌面外壳/浏览器/录制/启动器/播放器/工具 + `GameDetection.Exclude`）→ 进程存活且有主窗口 → `SHQueryUserNotificationState == QUNS_RUNNING_D3D_FULL_SCREEN`、窗口覆盖所在显示器 ≥95%（`GameDisplayRules.CoversMonitor`）或 GPU ≥5%。结果缓存 1 秒，前台 PID 不变时不打开进程。
- `GameModeTracker`：进入游戏立即生效、切换游戏更新 PID、离开后 10 秒宽限（加载/菜单/Alt+Tab 不抖动），到期返回一次 `ExitDue`。
- 动态采样：控制管道 `sampling` 命令 `{cmd, intervalMs, gameMode}`；采集循环每轮重读 volatile 间隔，共享内存头 `SamplingIntervalMs` 与 `status.gameMode` 同步，采集进程不重启。进入用 `Sampling.GameMs`（200ms），宽限到期用 `Sampling.NormalMs`（1000ms）。
- 停止路径修复：`frames.stop` 原先在管道线程里同步等 PresentMon 退出（最长 2 秒），与客户端 2 秒超时重叠，导致离开全屏后采集残留。现改为先摘除 `_presentMon` 并 `Cancel()`，`Dispose()` 放到后台任务，控制管道立即返回 `stopped` 并记录 `帧采集已停止：PID n`；客户端该命令超时放宽到 6 秒（其余 2 秒）；UI 把未确认请求记为待办逐节拍重试，并对该 PID 加抑制防止自动采集立刻重启。
- 验证：`GameModeTests` 14 项（95% 阈值、超出显示器、窗口化、96%/92.6%/93.75% 边界、进入/切换/10 秒宽限/中途返回/重置），总数 185 项通过。实机：03:25:15 全屏探针 → 200ms，03:25:25 自动采集；探针关闭后 03:25:43 回落 1000ms，03:25:45 采集端 `帧采集已停止`，UI 同步 `stopped`。此前星穹铁道全屏同样进入 200ms 并自动采集，切走后 10 秒回落。Release 0 警告 0 错误，应用已从桌面快捷方式重新启动并保持运行。
- 未完成：浏览器全屏视频不误触与 5 款游戏（DX11/DX12/无边框）识别率 ≥90% 仍需真机复验。

### 3.15 报告导出 FR-015（Release，2026-11-12）

- 实现位置：`Core/Sessions/SessionExport.cs` 新增 `SessionMarkdownReport` 与 `SessionAiSummary`，只依赖会话文件内容，不引用 UI 或 AI 项目；`BuildFileBaseName` 用本地时间 + 主因命名。
- Markdown 内容：事件摘要（时间、评分、主因、信号、前台进程与 PID、±30 秒数据窗口、文件状态、采样/帧数）、环境摘要表（版本/OS/CPU/GPU/模式与采样/权限/传感器/温度来源/PresentMon）、事件时间轴（`![...](<同名>-timeline.png)` + 事件开始、CPU/GPU/磁盘峰值、最长有效帧、采集断帧、事件结束）、指标峰值表（CPU 与频率、GPU、最低可用内存、提交峰值、磁盘延迟与占用、计时抖动、显存、GPU 与 CPU 温度）、帧统计、可疑进程表、结论与建议、数据缺口；表格转义 `|`，中英双语。
- 帧统计口径：优先使用会话内本地报告的 `FrameEvidence`；无报告时从帧数据重算整段平均、1% / 0.1% 低帧，`≥1000 ms` 的单帧按采集断帧计数，不进入最长帧与低帧，避免把 5 秒级断档当成掉帧。
- PNG：`UI/Services/SessionExportService` 在 UI 线程构造 `TimelineChart`（Combined）并离屏 `Measure/Arrange`，`RenderTargetBitmap(1200×400, 96dpi)` + `PngBitmapEncoder` 输出；容器背景取主题 `SurfaceBrush`（无资源回退白色），整段会话一屏；`TimelineChart.SetEventWindow` 绘制半透明色带 + 虚线 + “事件窗口”标签，零长度事件退化为单条虚线；回放窗口同步显示该标记。
- 可选 AI 报告：读取 `<会话文件>.ai.json`（`AiReport` 源生成反序列化），转成 `SessionAiSummary` 附在文末；文件缺失/损坏时静默跳过。导出不读 Key、不发网络请求。
- 验证：`SessionExportTests` 9 项（文件名、中文章节、英文标题、AI 附加、断帧排除与平均帧、报告帧证据优先、缺口计数、事件窗口取峰值、单元格转义），总数 194 项通过。真机会话导出：`20261111-212256-Cpu` → PNG 1200×400 / 60 KB、Markdown 2.8 KB，事件窗口标签在时间轴右段；`20261111-034355-FramePacing` → PNG 82 KB，帧轨与 4950 ms 断档示意可见，报告最长有效帧 911.1 ms 与断帧 15 次一致。

### 3.16 AI 报告合并进会话（Release，2026-11-12）

- 实现位置：`Ai/AiReportStore`（读写 `<会话文件名>.ai.json`，`.tmp` + 覆盖重命名；损坏/缺失返回 null）、`Ai/SessionFactsFactory`（从 `SessionDocument` 重建 `FactsInput`：采样、帧、进程名、深浅模式；`≥1000 ms` 采集断帧不进入事实层）、`SessionsWindow` 新增“AI 分析”按钮与详情区 AI 段、`AiAnalysisWindow` 增加可选的会话路径参数。
- 流程：会话窗口选中会话 → “AI 分析” → 数据取自该会话（不是实时环形缓冲）→ 分析成功且非离线回退时写入 `<会话文件>.ai.json` → 详情区追加摘要、主因/置信度（本地化）、前 4 条证据与前 3 条建议 → 关闭窗口后自动刷新。
- 离线回退（网络失败时由本地归因生成）不写入 `.ai.json`，避免把本地归因误标为模型报告；导出侧仍按原逻辑读取该文件。
- `SessionSchema.DiscontinuityFrameMs = 1000` 统一断帧阈值，Markdown 报告与 AI 事实层共用。
- 验证：新增 `AiReportStoreTests` 4 项（写入/读取、缺失、损坏、会话事实重建与断帧过滤），总数 198 项通过；Debug/Release 均 0 警告 0 错误。实机点击验证待配置 API Key 后执行。

### 3.17 文档一致性修正（2026-11-12）

- FR-006 实现现状：删去“显存压力与热降频留待 M3 接入”，改为“只作为归因证据，不进入信号表、不参与事件确认”，与实现一致（`LocalReportBuilder` 已输出 `Vram` / `Thermal` 主因）。
- 开发文档 §9：显存压力与热降频两节由“规划项”改为“已接入归因”，写明 `LocalReportBuilder` 的实际判定条件（GPU 峰值 ≥ 40 且温度 ≥ 85°C / 热点 ≥ 95°C；CPU 峰值 ≥ 40 且温度 ≥ 90°C；显存占比峰值归因，`HighLoadTracker` 92% 记录高负载）。
- 开发文档 §10.12：删去“UI 尚未绑定会话文件”，改为两个入口说明（主界面实时快照 / 会话窗口 `SessionFactsFactory` 重建），并标注按事件自动触发仍属 FR-020 未实现项。

### 3.18 采集重试风暴与残留进程（2026-11-12）

- 现象：`collector-*.log` 每 2 秒成对出现 `未找到 PresentMon` / `帧采集无法启动`；同时一个采集进程在界面异常结束后仍常驻，锁住 `artifacts\AzureGameAssistant-0.1.0-win-x64\collector\AzureGameAssistant.Collector.exe`，导致 `build/package.ps1` 无法覆盖。
- 根因一：`MainViewModel._autoCaptureBlockedUntil` 只写不读，`StartFramesAsync` 返回空（管道错误或采集端拒绝）时直接返回，下一拍（2 秒）继续请求；`unavailable` 的前台分支还会清空冷却。
- 根因二：`CollectorLauncher` 不监控子进程，采集端也没有父进程感知；界面被强杀或崩溃后采集端继续运行。
- 修复：冷却按 PID 记录并在尝试前生效（`permissionDenied` 5 分钟 / `unavailable` 1 分钟 / 无应答 15 秒）；采集端新增 `--parent <界面 PID>`，每 2 秒检查父进程，父进程消失即自行停止。界面启动采集时自动传入该参数。
- 打包防护：`build/package.ps1` 在清理 `artifacts` 前检查 `AzureGameAssistant` / `AzureGameAssistant.Collector` 进程，检测到运行中直接报错退出，避免像本轮一样在程序运行时清空目录、把正在使用的 PresentMon 删掉。
- 验证：Debug/Release 0 警告 0 错误，198 项测试通过。重启后的实机验证待执行（当前运行中的旧版来自不完整的 artifacts 目录，缺 `presentmon` 子目录）。

### 3.19 AI 单价设置与文档校正（2026-11-12）

- 缺口：设置页只有每日 / 每月预算，没有 Token 单价输入，`AiProviderSettings.InputPricePerMillion` / `OutputPricePerMillion` 无处填写，导致请求前永远显示“未配置价格”、预算永远为 0 而不会硬停止。
- 修复：设置页 AI 分组新增“输入价格 / 输出价格（美元 / 百万 Token）”，按供应商存入草稿并在保存时写入对应 `AiProviderSettings`；切换供应商各自独立，0 表示不估算。
- 文档校正：§10.9 明确单价入口；把“自动分析默认关闭、评分 ≥ 80 触发”改为 FR-020 规划项（当前未实现）；把缓存改为“会话报告已落盘，自动复用尚未实现”。

### 3.20 AI 报告复用（2026-11-12）

- 缺口：开发文档 §10.9 要求“同一会话 + 同一模型 + 同一事实哈希已有报告时默认复用”，此前每次打开会话分析窗都会重新请求。
- 实现：会话报告新增 `model` 与 `factsHash` 字段；`ComputeFactsHash` 对去掉 `locale` 的事实层序列化做 SHA-256，切换界面语言不会让报告失效。打开会话分析窗时，`AiReportStore.IsReusable` 匹配模型（忽略大小写）与哈希即直接展示上次结果，状态提示“已复用”，按钮变为“重新分析”；不匹配仍正常请求。旧报告没有指纹，一律视为不可复用。
- 验证：新增 3 项测试（指纹往返与匹配、无指纹不复用、哈希与语言无关），总数 201 项通过；Debug/Release 0 警告 0 错误。

### 3.21 严重事件自动分析（2026-11-12）

- 缺口：`AiSettings.AutoAnalyze` / `AutoAnalyzeMinScore` 早有字段但无任何使用方；需求 FR-020 的“可选严重事件自动分析”一直未实现。
- 实现：`AutoAnalysisPolicy` 统一判定（AI 启用、自动开关、评分 ≥ 最低分、日/月预算均 > 0、未分析过、已同意、配置有效、Key 可用、预算未耗尽）；`App.OnSessionWritten` 只在 `SessionInfo.Complete == true` 时触发，从已落盘会话重建事实层，成功后写入 `<会话>.ai.json`；忙时跳过，不回退不重试；成功且开启通知时弹托盘提示；退出时取消在途请求。
- 设置：AI 分组新增“自动分析严重事件”与“最低评分”，默认关闭 / 80，随设置持久化。
- 验证：新增 `AutoAnalysisPolicyTests` 6 项，总数 207 项通过；Debug/Release 0 警告 0 错误。真实联网触发需用户配置 Key 并开启开关后长测。

### 3.22 悬浮窗按显示器定位（2026-11-12）

- 缺口：FR-012 与 K-16 长期限定“仅主显示器”，主窗口放到副屏（含负坐标布局）时悬浮窗仍留在主屏。
- 实现：新增 `MonitorWorkArea.TryGet`（`MonitorFromWindow` + `GetMonitorInfo`，复用既有 P/Invoke）取主窗口所在显示器工作区，按窗口 DPI 缩放到 DIP 后交给 Core 的 `OverlayPlacement.Place` 计算四角与偏移；定位失败回退 `SystemParameters.WorkArea`；超出工作区时夹紧，避免副屏偏移或窄屏把数据推出屏幕。主窗口 `LocationChanged`、悬浮窗显示与 `SizeChanged` 都会重新定位。
- 验证：新增 `OverlayPlacementTests` 4 项（左上偏移、右下对齐、副屏负坐标、超大窗口夹紧），总数 213 项通过；Release 0 警告 0 错误。实机跨屏显示待重启后确认。

### 3.23 便携包内置传感器边车（2026-11-12）

- 缺口：K-27 记录便携包只带 PresentMon，没有 `AzureGameAssistant.Sensors.exe` 与 LHM 依赖，分发版本的 GPU / CPU 温度会显示“—”。
- 实现：`build/package.ps1` 新增自包含发布边车到 `artifacts\sensors`，再复制到 `collector\sensors\`（跳过 pdb，保留 `LICENSES\`）；包根新增 `LICENSES\`（来自 `third_party\licenses`）。`CollectorHost.ResolveSensorsPath` 依次查找 `collector\sensors\`、采集进程同目录、开发树 `Release/Debug` 输出，找不到时深模式自动降级为无温度数据。
- 验证：`dotnet publish` 边车成功，输出含 `AzureGameAssistant.Sensors.exe`、`LibreHardwareMonitorLib.dll`、`HidSharp.dll`、`LICENSES\`，共 81MB；采集进程 Release 构建 0 警告 0 错误；`package.ps1` 通过 pwsh 语法解析。整包 ZIP 与打包后边车启动待退出旧实例后复验。

### 3.24 包内说明文档与许可路径（2026-11-12）

- 缺口：开发文档 §14.4 的包结构要求带 `README.md` 与 `THIRD-PARTY-NOTICES.md`，但 `package.ps1` 只复制 PresentMon 与许可证，README 被标为“后续补充”，第三方声明也没进包；`THIRD-PARTY-NOTICES.md` 里的许可路径指向仓库目录 `third_party\...`，解压后不存在。
- 实现：`package.ps1` 强制复制 `README.md` 与 `THIRD-PARTY-NOTICES.md`（缺失即失败），项目自身 `LICENSE` 未定（K-05）时改为输出警告而非静默跳过；第三方声明中的许可路径改为包内相对路径 `LICENSES\...` 与 `collector\presentmon\LICENSE.txt`；README 的 AI 段落更新为“可选严重事件自动分析，默认关闭”。
- 验证：`package.ps1` 通过 pwsh 语法解析；README / 第三方声明路径与包结构逐条对照；整包 ZIP 仍需退出旧实例后复验。

### 3.25 界面提示双语补齐（2026-11-12）

- 缺口：FR-013 要求界面中英双语，但 `App` 里三处用户可见提示写死中文（或中英混排）：单实例重复启动、保存设置失败、未处理异常；英文界面下会直接显示中文。
- 实现：新增 `AlreadyRunning`、`SettingsSaveFailed`、`UiUnhandledError` 三个资源键（中英各一份），改用 `LocalizationService` 读取；保存失败与异常提示用 `Format` 把原始异常信息接在后面，便于用户反馈。
- 验证：脚本比对 UI 代码里全部 216 个资源键与两份 resx（各 442 条），无缺失键、中英集合完全一致；UI Release 构建 0 警告 0 错误。对话框文案需重启后肉眼看一次。

### 3.26 悬浮窗不透明度（2026-11-12）

- 缺口：FR-012 把“透明度”列为 M3 增强，K-16 一直只做到按显示器定位；极亮壁纸下白色数值不够稳，用户除改配色外没有可调手段。
- 实现：`OverlaySettings.Opacity`（30–100，默认 100）在 `Normalize()` 中夹紧；`OverlayWindow.ApplyAppearance()` 换算成窗口 `Opacity`，与主题/颜色同一条即时生效路径；设置页“外观”区新增滑杆与百分比显示，保存后立即套用。`OverlayOpacity` 资源键中英各一条。
- 验证：`SettingsServiceTests` 扩充默认值与夹紧断言（默认 100、5 夹到 30）；Release 全量 213 项通过、UI 0 警告 0 错误。滑杆实际观感需重启后由用户确认。


### 3.27 打包后布局自检（2026-11-12）

- 缺口：脚本只校验 PresentMon 的存在与哈希，其余文件依赖“复制语句写对”；本机 `artifacts\AzureGameAssistant-0.1.0-win-x64\collector\` 就是反例——只有采集 exe，没有 `presentmon\`，运行时因此拿不到帧数据。
- 实现：所有文件归位后，`package.ps1` 按开发文档 §14.4 的必需项逐条 `Test-Path`（主程序、README、第三方声明、`LICENSES\LibreHardwareMonitorLib-MPL-2.0.txt`、采集进程、PresentMon 与其许可证、传感器边车），缺失即失败并列出路径。
- 验证：脚本通过 pwsh 语法解析；用同一份清单跑现有旧包，准确报出 README、第三方声明、LICENSES、PresentMon 两项与传感器边车共 6 项缺失；对合成的完整目录返回 0 项缺失。整包重新生成仍待退出旧实例。

## 4. 进行中

- **会话 AI 分析（3.16）**：按钮与写入链路已完成并通过单测；待配置 API Key 后在会话窗口点一次“AI 分析”，确认报告写入 `<会话>.ai.json`、详情区出现 AI 段、导出 Markdown 末尾附带 AI 章节。
- **会话回放实测**：本机已完成首轮闭环（0.14 秒出现 `recording`、30 秒后 `complete`、回放与报告正常、无 `.tmp`）；待用户在自己的游戏场景复验，并核对设置页“会话存储”的双上限淘汰与删除按钮。
- **报告导出（FR-015）**：代码与单测完成，真机会话已离线验证 Markdown/PNG 内容；待用户在会话窗口点一次“导出报告”，确认保存对话框默认文件名、事件窗口标记的位置与 Markdown 在常用编辑器中的显示。
- **游戏自动识别（FR-010）**：判定、10 秒宽限、动态采样与自动采集停止均已实机闭环；待用户在浏览器全屏视频与常玩游戏中复验“不误判”和识别率，并观察 200ms 模式下的采集进程与界面开销。
- **本地归因报告（FR-008）**：7 类场景单测 7/7，断帧/尖峰分离与一致性已实机显示；待用户在常玩游戏制造真实卡顿后核对主因是否符合手感，并根据长测数据校准温度与等待阈值。
- **软件问题日志**：归档路径、月份切分与四进程写入已实机验证；目录当前只有一次验证用的采集参数错误记录。后续排障直接读取 `logs\issues\`，无需翻常规运行日志。
- **长时诊断记录**：本机已开启（5 秒），等待三角洲行动长测数据。退出软件后读取 `%LOCALAPPDATA%\AzureGameAssistant\diagnostics\diag-<时间戳>\summary.txt`，再用 `samples.csv` 与 `events.jsonl` 复盘；强制结束进程时无摘要，直接使用 CSV/事件文件。
- **M3 AI**：库、UI 与自动化测试已完成；本轮新增“测试连接”、单价输入、报告复用与“严重事件自动分析”开关；自动分析默认关闭，需配置 Key、设置双预算并先完成一次同意后才会在事件会话落盘后触发，待真实长测。会话解压、事实构建与门控判定已移到后台线程，避免打断界面刷新。点击级验证（预览 JSON、同意、预算、错误提示、测试连接结果）仍待重启后执行。
- **掉帧/高负载标记**：单元测试与诊断事件已覆盖；需在三角洲行动等真实游戏复现后，核对 `events.jsonl` 的 `frame_drop` / `high_load` 与 AI 报告中的 `dropAnalysis` / `highLoadAttribution` 是否逐条对应。
- **M3 MCP**：已在 Qoder App 注册为 `azure-game-assistant`，可枚举 6 个只读工具；正在进行的会话建立于注册之前，工具列表尚未刷新，新开一个会话即可直接调用 `diagnose` / `get_hardware_snapshot` / `get_frame_stats`。
- **帧采集连续性与准确性**：停帧兜底、会话可复活、无帧慢速重试与断帧排除已完成；明日方舟 4 分钟内 3 次 2–3 秒自然静默均记录为“帧数据中断 N 秒后自行恢复”，界面持续保持读数。仍需用户在原神（应稳定在 60 附近）与三角洲行动长测：核对瞬时 FPS 不再跳到 30/数百、平均与 1% Low 落在合理区间、日志无持续 `unavailable`。
- **M3-1**：启动提权、权限自检、前台自动采集已实机闭环；本机已装 PawnIO 并切到驱动模式，CPU 温度可读。
- **M3-0**：GPU 传感器链路已通；边车进程内存/CPU 与其他厂商真机待补测。
- **M2-0**：明日方舟已实测；其余 2 款游戏与反作弊兼容性评估待用户提供环境。

## 5. 下一步（按顺序）

1. **FR-010 已完成（见 3.14）**：判定、宽限、动态采样与停止路径均已实机验证；剩余是浏览器全屏不误判与 5 款游戏识别率的用户复验。
2. **FR-015 已完成（见 3.15）**：Markdown + 时间线 PNG 仅本地生成；待用户在会话窗口点一次“导出报告”复验保存路径、事件标记与编辑器显示。
3. **会话回放用户复验**：本机时序已通过；请用户在常玩游戏里再点一次“标记卡顿”，确认列表出现、30 秒后完成、回放时间轴与主界面一致，并试一次删除。
4. **长时数据复盘（优先）**：三角洲行动长测结束并正常退出软件后，读取 diagnostics 最新会话的 `summary.txt`；结合 `samples.csv` 的帧率/资源/UI 自身开销与 `events.jsonl` 的缺口、采集重启、帧中断、掉帧/高负载事件定位问题，再决定优化项。
5. **AI 窗口与“测试连接”人工验证**：双击二号屏快捷方式启动 Release → 设置页 AI 分组点击“测试连接”核对成功/401/超时提示 → 主界面“AI 分析”核对预览 JSON、同意、预算、错误提示，以及“掉帧分析”“高负载归因”卡片的显示。默认不联网，只有点击后才发请求。
6. **掉帧/高负载真实复现**：在三角洲行动中制造一次卡顿与一次 CPU 高占用，确认 `events.jsonl` 出现 `frame_drop` / `high_load`、AI 报告逐条给出原因，且高负载资源都带进程名；若模型漏答，校验器应回退本地报告并提示。
7. **MCP Agent 验证**：新开一个 Qoder 会话（本会话建立于注册前），调用 `diagnose`、`get_hardware_snapshot`、`get_history`、`get_frame_stats`；若工具缺失，对连接器执行 `retry`。非管理员会话的 `get_collector_status` 显示 `degraded` 属设计行为。
8. **帧率准确性长测（本轮改动验收）**：在原神中确认瞬时 FPS 稳定在 60 附近、不再闪 30，平均与 1% Low 不低于约 55；在三角洲行动 60 分钟连续采集中确认日志不再出现持续 `unavailable`、断帧空洞不进入统计；期间观察 `samples.csv` 的 `fps_inst` / `fps_avg` / `frame_time_last_ms` 是否与体感一致。
9. **问题日志复盘**：用户反馈任何异常时，先看 `%LOCALAPPDATA%\AzureGameAssistant\logs\issues\issues-<当月>.log`（设置页“打开问题日志”直达），其中只有警告/错误，比常规运行日志快得多；确认记录足够定位后再看按次日志与诊断会话。
10. **ACE 兼容性观察**：在装有 PawnIO 的当前环境启动明日方舟，确认 ACE 是否拦截；若出现异常，立即 `sc stop PawnIO` 并切回 ACPI 模式。任何情况下不注入、不绕过反作弊。
11. **M2-0 实测**：再测 2 款游戏 + 反作弊兼容性评估（K-07），并复测深模式（PresentMon + 传感器边车运行中）采集进程与边车的内存/CPU。
12. **M3-0 传感器补测**：边车专用工作集与 CPU 占用；AMD/Intel GPU 各验证一次；确认 GPU-only 永不触发驱动加载。
13. **自动分析（FR-020）已完成**：评分门控、双预算、同意与 Key 校验均已接入；准备阶段移到后台线程，避免阻塞界面节拍。剩余为用户配置 Key 后的真实长测。
14. **8 小时稳定性**：重点复核 Collector 句柄趋势（K-15）与睡眠唤醒。
15. **多显示器悬浮窗已完成（见 3.22）**：按主窗口所在显示器工作区定位并提供越界夹紧；新增不透明度滑杆（3.26）。跨屏与滑杆观感待重启后复验。
16. **整包复验（见 3.24）**：退出旧实例后运行 `build/package.ps1`，解压 ZIP 确认根目录含 `README.md`、`THIRD-PARTY-NOTICES.md`、`LICENSES\`，`collector\sensors\AzureGameAssistant.Sensors.exe` 能启动并读出温度；实机执行第 5 节步骤 5 的 AI 点击级验证。

每个步骤完成后：更新本文件 → 按提交规范提交 → 在交接日志追加一行。

## 6. 关键决策（不要再重复讨论）

| 决策 | 结论 | 依据 |
| --- | --- | --- |
| 技术栈 | C# / .NET 8 + WPF；采集进程 NativeAOT | 用户确认；内存预算 |
| 进程模型 | 采集与 UI 分离，共享内存 + 命名管道；传感器独立边车 | ADR-001 / 004 / 005 / 014 |
| 系统数据源 | PDH 英文计数器 + NT 进程 API | ADR-006 |
| 帧数据源 | 随包 Intel PresentMon 控制台 exe，子进程 + CSV 解析 | 用户要求帧级真相；ADR-013 |
| 厂商 GPU 传感器 | LibreHardwareMonitorLib 边车，仅 GPU 硬件组，不加载内核驱动 | 用户要求厂商原生；ADR-014 |
| 权限模式 | 启动时请求一次提权（默认开启、可关闭）；取消 UAC 保持浅模式；内置只读权限自检 | 用户要求长期权限而非一次性；ADR-019 / ADR-023 |
| 自动帧采集 | 默认开启；仅前台游戏类进程（全屏或 GPU ≥ 5%）自动开始；切到桌面应用保持现有游戏会话；手动停止后同一进程不再自动重启 | 用户要求“看不到帧数”问题闭环；避免桌面应用误触发 |
| PresentMon 会话 | 首次启动用 `--terminate_existing_session --no_csv` 清理同名残留会话；正式采集不带 `--stop_existing_session`；正常退出后跳过清理直接重启；500ms 轮询 + 20 秒首帧 / 25 秒停帧兜底，且都要求目标活跃 | 残留 ETW 会话会让新会话静默停帧；但 PresentMon 静默 2–5 秒可自行恢复，过早重启反而制造 12–16 秒空洞 |
| 卡顿判定 | 帧尖峰优先 + 抖动 + 资源尖峰 + 显存/内存/磁盘压力 + 热键 | 用户确认；FR-006 |
| 数据留存 | 15 分钟系统环形缓冲 + 3 分钟帧环形缓冲；事件前后各 30 秒落盘 JSONL.gz | 用户确认；ADR-018 |
| 会话落盘 | 事件确认先写 `state=recording` 的可读文件，`end+30s` 原位原子重写为 `state=complete`；`.tmp` + `Flush(true)` + `File.Move(overwrite)`；启动清理未完成文件；帧 64 帧/批；默认 20 个 / 500MB | 同时满足“3 秒可发现”与“强杀不留损坏文件”；源生成序列化满足裁剪/AOT；ADR-031 |
| 采样 | 常规 1s / 游戏模式 200ms（自动识别或手动指定触发） | 用户确认 |
| 性能红线 | 采集浅 ≤30MB / ≤0.3%；深 ≤45MB / 自身 ≤0.5%，合计 ≤1.0%；UI 托盘 ≤60MB、主窗口 ≤80MB | 用户确认 |
| 界面 | 托盘 + 可选悬浮条 + 主窗口；中英双语；明暗跟随系统；极简 | 用户确认 |
| 分发 | 便携 ZIP；三阶段 v0.1 / v0.5 / v1.0 | 用户确认 |
| 命名 | `AzureGameAssistant`，进程 `AzureGameAssistant.exe` / `.Collector.exe` / `.Sensors.exe` | 用户确认 |
| AI 接口 | OpenAI 兼容 `/v1/chat/completions` + 自定义 Base URL，BYOK | 用户要求通用 LLM 接口；ADR-015 |
| AI 数据边界 | 本地事实层 + 默认脱敏 + 发送前预览 + 手动触发 + 预算上限 | 隐私与成本；ADR-017 |
| API Key | Windows Credential Manager | ADR-016 |
| AI 触发 | 手动触发（主界面“AI 分析”按钮 + 预览/同意）；可选“严重事件自动分析”默认关闭，按评分 / 双预算 / 同意 / Key 门控，仅在完整会话落盘后请求一次 | 用户要求可选自动分析；门控与单请求限制避免后台轮询与无效开销 |
| MCP 服务 | 独立只读 stdio 进程，6 个有界工具，不写系统、不执行命令 | 用户要求 Agent 直读硬件数据；ADR-024 |
| 默认联网 | 禁止；仅用户显式开启 AI 后访问其确认的地址 | 用户此前“纯本地”要求，按新需求放宽为“本地优先” |
| 提交标注 | 人类作者不变，footer 加 `Agent: Qoder`；其他 Agent 用自己名称 | 用户确认 |
| 分支模型 | 主干 + 短分支 + PR Squash | 用户确认 |
| 第三方分发 | PresentMon MIT、LHM MPL-2.0；固定版本 + SHA256 + 许可证清单 | 调研结论；ADR-020 |
| UI 渲染 | WPF 软件渲染；界面低频刷新，换取更低常驻内存 | 实测专用工作集约 43MB；ADR-021 |
| 分指标图表 | 默认一张合并折线图：各指标不同颜色叠加、共用 0–100% 纵轴（显存按容量百分比），帧时间用右侧 ms 副轴；设置中勾选“分开显示图表卡片”才切换为每指标一张独立卡片 | 用户要求默认在一个折线图里用不同颜色表示；多卡片作为可选布局 |
| 内存口径 | 卡片、图表与 MCP 的“内存使用率”按物理内存计算：`(总物理内存 − Available MBytes) / 总物理内存`，总量由 `GlobalMemoryStatusEx` 读取并缓存；已提交 / 提交上限只用于提交压力判定 | 用户设备 16GB，提交上限（物理 + 页面文件）31.9GB 被误显示为“内存总量”；物理口径与任务管理器一致 |
| 模型选择 | 设置页“获取模型”按 Base URL 调用厂商 `/models`，结果供下拉选择并写回可编辑模型名；仅用户点击时联网，未保存 Key 时提示先保存 | 用户要求直接选择厂商模型而非手输；保持 BYOK 与“默认不联网”边界 |
| 长时诊断记录 | 可选开关（默认关闭，本机已开启 5 秒）；5/10/30 秒写 `samples.csv`、每 30 秒 `processes.csv`、状态变化 `events.jsonl`、退出生成 `summary.txt`；不记录标题/路径/Key | 用户只需在长时间游戏后提供目录即可复盘；有界、低开销、隐私一致 |
| 日志策略 | 默认常开，可从启动记录到退出；可关闭，关闭后界面与采集/传感器/MCP 全部停写；按最后写入时间保留 1–30 天（默认 3 天），启动与保存设置时清理 | 用户要求常态保存且可自行关闭；按天轮换比按启动次数更贴合长期运行，且实现简单可验证 |
| 问题日志归档 | 只收 Warn/Error/未处理异常的独立归档 `logs\issues\issues-YYYY-MM.log`；月份按写入时刻算，追加后即关文件，四进程共用；按天清理只扫顶层不碰该子目录 | 用户要求“只看软件自身问题”的日志，混在 Info 流里翻不动；按月既是轮换单位又是排障单元；不常驻句柄符合“内存极小” |
| 帧率显示策略 | 短暂无帧时保持最后一次读数：主窗口 30 秒、悬浮窗 20 秒；不显示“恢复中”等标注；目标退出或停止采集立即清空为“—”；5 秒新鲜度只用于诊断与日志 | 用户要求真正解决“帧率短暂消失”，而不是用占位文案遮住；兜底重启阈值 25 秒，保持窗口需覆盖一次完整兜底重启 |
| FPS 统计口径 | 全部按时间窗速率计算：瞬时 FPS 覆盖最近约 1 秒、平均帧与 1%/0.1% Low 覆盖最近约 10 秒；≥1000ms 的单帧视为采集断点，只画在图上不进入统计；无帧时以 2–15 秒退避重试且不计失败次数，失败会话可被重启替换 | 单帧倒数会把一次漏帧读成 30 FPS、把偶发卡顿放大成 5500 FPS；整环平均会混入菜单与加载；PresentMon 停顿后会把整段空闲报成一帧；旧逻辑的 `Unavailable` 锁死会让采集永久失效 |
| 掉帧/高负载归因 | UI 进程固定容量追踪器（64/16），标记进入事实层；模型必须逐条输出 `dropAnalysis` 与每个资源的 `highLoadAttribution` 进程，缺失则判不完整并回退本地报告 | 用户明确要求“AI 分析后必须返回是哪个进程造成高负载”；固定容量保证内存极小，校验器让要求可验证 |
| 图表弹窗 | 默认只有一个主窗口；勾选“布局设计”后才显示脱离按钮，可把单图弹出为独立小窗，关闭小窗或取消勾选自动归还；同一指标仅一个实例 | 用户要求默认统一单窗、仅设计时可多窗；避免复制缓冲与双份渲染 |
| 桌面悬浮数据窗 | 无背景无边框，数据项横向排列并自动换行，默认左上；四角 + 偏移定位，内容项独立开关，鼠标穿透、不抢焦点 | 用户要求只显示数据、横向换行、默认左上；不注入游戏 |
| 悬浮窗配色 | 默认 / 浅色 / 天空蓝 / 自定义 `#RRGGBB`；整体不透明度 30%–100% 可调；标签同色降透明度，阴影按亮度自动黑/白 | 用户要求可设置颜色与主题；保证浅色桌面可读，并能在极亮壁纸下调低存在感 |
| 应用图标 | 用户提供图 1 生成多尺寸 ICO，窗口 / EXE / 托盘统一 | 用户要求 |
| 遥测协议 v3 | Sample 槽位 256 字节、CPU 传感器槽 `C0`、`CpuSensorValid`；帧/事件区偏移随布局重算 | 新增 CPU 温度而不破坏既有偏移；布局测试锁定 |
| CPU 温度来源 | 默认 ACPI WMI；可选 `driver` 模式（用户自行安装 PawnIO），缺失显示“—”；程序不自动安装驱动。本机经用户确认已安装并启用驱动模式 | 用户要 CPU 温度；反作弊约束下不静默装驱动；ADR-022 |
| 传感器默认 | schemaVersion 3 起默认开启；旧设置升级时自动开启一次 | 用户明确需要 GPU/CPU 温度；边车不加载驱动、可随时关闭 |

## 7. 已知问题与限制

| 编号 | 问题 | 影响 | 处理 |
| --- | --- | --- | --- |
| K-01 | ~~30MB 采集内存预算尚未在 AOT 产物上验证~~ | 已解决 | AOT 浅模式实测专用工作集 10.1MB、CPU 0.098%；深模式待 M2 验收复测 |
| K-02 | PDH GPU 计数器在不同驱动上实例名/精度可能不同 | GPU 曲线缺失 | 已做实例解析与 `_Total` 过滤；能力探测与 M3 厂商边车补足 |
| K-03 | 全屏独占游戏可能拦截全局热键 | 手动标记失效 | 文档说明；提供托盘/悬浮条入口（M2） |
| K-04 | 本机仅安装 .NET SDK 9.0.100 | 构建 `net8.0` 需联网还原目标包 | 首次 `dotnet restore` 需可用网络 |
| K-05 | 许可证、代码签名未定 | 不影响开发 | M3 前决策；需兼容 MIT / MPL-2.0 |
| K-06 | PresentMon 需要管理员或 Performance Log Users | 普通用户无帧数据 | 启动时请求一次提权、取消则浅模式；自检窗口列出缺失项；`--no-elevate` 可跳过 |
| K-07 | 部分反作弊可能对 ETW 敏感 | 用户账号顾虑 | 只运行官方 PresentMon，不注入；文档不承诺全兼容 |
| K-08 | ~~LHM GPU-only 是否绝对不加载内核驱动待验证~~ | 已解决 | 边车启动前后检查 WinRing0/inpoutx64/RTCore64/MsIo 服务；本机 GPU-only 未加载驱动，检测到即以退出码 3 停止 |
| K-09 | 各 LLM 供应商兼容接口存在细节差异 | AI 调用失败 | 只依赖 `chat/completions` 最小子集；3 家云 + 本地联调 |
| K-10 | 含帧会话体积可能超过 12MB 预算 | 存储增长 | M2 实测；必要时降低帧批字段或提高压缩率 |
| K-11 | OCAT 许可证不明确 | 不可复用其代码 | 只参考思路；代码评审检查来源 |
| K-12 | ~~本机缺少 NativeAOT 链接器（VS C++ 工作负载）~~ | 已解决 | VS Build Tools 安装到 `F:\VS2022BuildTools`；`vswhere` 可定位，AOT 发布与默认打包流程均通过 |
| K-22 | PresentMon 帧数据需要管理员或 Performance Log Users；未提权时退出码 6 | 浅模式无帧数据 | 启动自动提权（可关闭）；权限自检“帧率采集”项实时显示状态；取消 UAC 时降级并提示 |
| K-24 | ~~帧采集目标只能通过 `settings.json` 的 `frames.targetPid` 指定~~ | 已解决 | 状态卡新增目标进程下拉框与开始/停止/刷新按钮；开始成功后自动保存 `frames.targetPid`，停止时清空；UIA 实测闭环通过 |
| K-13 | UI 软件渲染在超大窗口/高 DPI 下可能增加 CPU | 界面开销上升 | 仅 2Hz 刷新；M2 增加渲染模式设置项后再评估 |
| K-14 | 当前仅验证单显示器、单 GPU、本机驱动 | 兼容性未知 | M2-0 扩展手工验收矩阵；高 DPI/多显示器在 M2 验收 |
| K-15 | Collector 句柄在 30 分钟可见状态下 381 → 455 缓慢上升 | 长时运行可能累积 | 托盘 10 分钟窗口内回落且内存稳定；8 小时稳定性测试复核，必要时用句柄表定位 |
| K-16 | 悬浮窗跨显示器位置记忆与拖动未实现 | 不能记忆“副屏右下”这类位置，也不能手动拖动 | 已按主窗口所在显示器定位（3.22）、支持 30%–100% 不透明度（3.26）；位置记忆与拖动列入后续增强 |
| K-17 | ~~悬浮窗 GPU 温度仍显示“—”~~ | 已解决 | LHM 边车已接入，实机显示 GPU 温度 / 热点；FPS / 平均帧 / 1% Low 已在 M2-2 接通 |
| K-18 | ~~五张图在固定高度图区内需纵向滚动~~ | 已解决（默认合并图） | 默认改为单张合并叠加图并按可视区自适应高度，不再需要滚动；切到“分开显示图表卡片”后多卡片仍可纵向滚动 |
| K-19 | 悬浮数据无背景，极亮或高对比桌面背景下可读性依赖文字阴影 | 个别壁纸下数值不够清晰 | 已加细阴影；仍不足时由用户调整偏移或关闭壁纸；M3 再评估可选半透明底 |
| K-20 | 图表小窗每次打开都居中，不记忆位置、尺寸与置顶状态 | 常用的小窗需重新摆放 | 本轮按最小实现；后续可把窗口位置写入设置；窗口关闭后图表一定归还，不影响数据 |
| K-21 | 悬浮窗自定义颜色只接受 `#RRGGBB`，不支持透明度与渐变 | 无法单独调节文字透明度 | 标签已固定 78% 不透明度；M3 如需再扩展 `#AARRGGBB` |
| K-23 | 本机 ACPI 热区不可用（MAXSUN B550M + Ryzen 5 5600） | 默认 ACPI 模式下 CPU 温度显示“—” | 已由用户确认安装 PawnIO 2.2.0 并切到驱动模式，实测 CPU 温度 47°C；新环境仍需用户自行安装，程序不自动装 |
| K-28 | 本机存在已停止的 `WinRing0_1_2_0` 服务（其他工具遗留），PawnIO 已安装并运行 | 装 PawnIO 后内核驱动数量增加，ACE 可能视为环境异常 | 用户已被告知并确认；PawnIO 为 `DEMAND_START`，游戏前可 `sc stop PawnIO`，或 `PawnIO_setup.exe -uninstall -silent` 卸载；边车仍会在检测到运行中的已知 Ring0 驱动时拒绝启动 |
| K-25 | 传感器边车进程的专用工作集 / CPU 占用尚未单独测量 | 深模式整体预算未定论 | 下一轮专项测量；边车仅 1s 节拍、无 GUI，预计远低于 35MB 预算 |
| K-26 | LHM 0.9.6 对本机 RTX 4060 不提供功耗传感器 | GPU 功耗为“—” | 不写 0；改用驱动 SDK 或新版 LHM 时再补，保持缺失语义 |
| K-27 | ~~便携包尚未包含 `AzureGameAssistant.Sensors.exe` 与 LHM 依赖~~ | 已解决（待整包复验） | `package.ps1` 自包含发布边车到 `collector\sensors\`，并复制 `LICENSES\`；采集进程优先解析该子目录。边车发布与依赖清单已单独验证，整包 ZIP 需退出旧实例后复验 |
| K-29 | `AiAnalysisWindow`（含新增“测试连接”、掉帧分析、高负载归因卡片）尚未做本机点击级验证 | UI 交互问题可能未被发现 | 本轮已停止旧实例、重建并重启 Release；下一步按第 5 节步骤点击验证。服务层已有 135 项测试覆盖 |
| K-30 | ~~FR-020“严重事件自动分析”未实现~~ | 已解决（待真实长测） | 见 3.21：默认关闭，按评分、双预算、同意与 Key 门控，会话完整落盘后触发一次并写同一 `.ai.json`；已在设置页提供“自动分析严重事件”与“最低评分”。真实联网触发待用户配置 Key 后长测 |
| K-31 | MCP 以非管理员运行时 `get_collector_status` 为 `degraded` | 缺少运行时长与最近错误 | 属设计行为；快照/历史/帧统计/进程排行不受影响。需要完整状态时由用户以管理员身份运行 Agent |
| K-32 | MCP 默认框架依赖单文件，目标机需 .NET 8 运行时 | 无运行时环境无法启动 | `build/publish-mcp.ps1 -SelfContained` 生成自包含版本 |
| K-33 | ~~同名 PresentMon ETW 残留会话导致新会话静默停帧~~ | 已解决 | 启动前 `--terminate_existing_session --no_csv` 清理，正式会话不再带 `--stop_existing_session`；500ms 轮询看门狗；三角洲行动 75 秒连续 8,379 帧验证通过 |
| K-34 | Qoder App 在 MCP exe 被替换后可能保留 `MCP_PROTOCOL_CONNECTION_CLOSED` 失败状态 | 该连接器工具暂时不可见 | 对连接器执行 `retry`；在注册前建立的会话中不会出现新工具，需新开会话 |
| K-35 | 强杀界面进程时不会生成长时诊断 `summary.txt` | 缺少退出摘要 | 每行 CSV/事件均实时 flush，可直接用 `samples.csv` / `events.jsonl` 分析；不影响后续优化 |
| K-36 | 掉帧/高负载标记尚未在真实游戏中复现验证 | 阈值或分类可能与实际手感不一致 | 追踪器与校验器已有单元测试；按第 5 节第 3 步在三角洲行动复现后校准阈值 |
| K-37 | 掉帧标记最多保留最近 64 条、高负载 16 条，分析时只取最近 6 条 | 长时间会话中较早事件不会进入 AI 报告 | 诊断记录仍完整写入 `events.jsonl`；如需更长历史，用诊断文件复盘而不是扩大内存缓冲 |
| K-44 | 会话文件只在识别到卡顿事件时生成；纯手动标记也会生成，但当前没有“常驻录屏式”会话 | 用户只能回放有事件的时间段 | 符合 FR-007“事件前后各 30 秒”的范围；若后续需要连续会话，再单独评估磁盘与内存预算 |
| K-45 | 会话帧批次的 `cb/gl/gb/ae/pm` 为预留字段，当前写 0；`pid` 也写 0 | 帧级 PresentMon 原始列在会话中不完整 | `cw/gw/dl/ft/sp/u/q` 已覆盖回放与归因所需；等 AI 报告需要更多列时再填，保持读取兼容 |
| K-46 | ~~会话“3 秒内可见 / 30 秒后完成”尚未在真实游戏中人工点验~~ | 已解决 | 实测标记后 0.14 秒出现 `recording`，30 秒后转为 `complete`，无 `.tmp` 残留 |
| K-38 | 本机 `settings.json` 尚未包含 `logs` 分组 | 首次保存设置后才写入；当前按默认值常开、保留 3 天 | 默认值与需求一致；用户在设置页保存一次即生成该分组 |
| K-39 | 窗口化 FPS 尚未在真实 60 帧游戏（原神）中做读数对照 | 若仍有偏差，需依据用户长测数据校准窗口参数 | 本机没有持续 60 帧出帧的测试对象（明日方舟空闲 1–8 帧、OBS 空场景 1–3 帧）；准确性依据诊断回放与 137 项测试；按第 5 节第 5 步验收 |
| K-40 | 游戏初始化极慢或呈现路径 PresentMon 无法跟踪时，首帧前界面显示“—”并进入最长 15 秒的退避重试 | 前 1–2 分钟内可能没有帧率 | 不再判定失败、不锁死会话；玩家回到游戏立即重试；日志对连续无帧限流 |
| K-41 | FPS 统计只保留最近约 10 秒窗口，之前的低帧不会继续拉低平均值 | 长会话中的偶发卡顿不会长期反映在平均帧上 | 长时诊断 `samples.csv` 与 `events.jsonl` 仍保留全部历史，需要复盘时以诊断文件为准 |
| K-42 | 问题日志按月归档，不参与按天保留，也没有单独的清理策略 | 每个有问题的月份留下一个文件，长期累积体积不受控 | 只有 Warn/Error 才入档，正常使用每月约 KB 级；如多年累积可手动删除旧月份文件（设置页“打开问题日志”直达目录），后续再评估按年清理 |
| K-43 | 问题日志只在自然出现警告/错误时才会有内容，正常安静运行目录可能为空 | 用户打开目录可能看不到文件 | 属预期；本机已用一次真实参数错误验证写入格式，正常运行时按需出现 |
| K-47 | ~~会话报告把 ≥1000ms 的断帧空洞也计入“尖峰”次数（本机 19 帧中 7 次）~~ | 已解决 | 断帧单独计 `gapCount`，不再进入最大帧、尖峰与等待均值；实机报告为 `gapCount 8 / spikeCount 0 / max 896.5ms` |
| K-48 | 着色器编译、资产流送、系统调度暂无独立分类，分别归入 FramePacing、Disk、Unknown；断帧阈值 1000ms 为固定值 | 报告主因粒度有限 | 现有信号不足以可靠区分这三类；先保证不误判，等帧/IO 证据更细分时再拆分类别 |

## 8. 如何验证当前状态

```powershell
cd "C:/Users/worldcopyist/Documents/Qoder/2026-11-07/275e2b44"
git log --oneline --decorate -n 5
git status
dotnet build AzureGameAssistant.sln
dotnet test AzureGameAssistant.sln
powershell -File build/run-dev.ps1        # 启动界面与采集进程
powershell -File build/measure.ps1 -DurationMinutes 1
powershell -File build/measure.ps1 -DurationMinutes 30 -OutputPath artifacts/measurements/soak.csv
powershell -File build/package.ps1          # 默认 NativeAOT 便携包
powershell -File build/publish-mcp.ps1      # 生成 Agent 可接入的 MCP 单文件服务
```

MCP 冒烟（无需 Agent 客户端，直接走 stdio）：

```powershell
'{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}',
'{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_collector_status","arguments":{}}}' |
    .\artifacts\mcp\AzureGameAssistant.Mcp.exe
```

本机已在二号显示器（1080×1920，位于物理坐标 2560,0）桌面放置“蓝天游戏助手”快捷方式，指向 `src/AzureGameAssistant.UI/bin/Release/net8.0-windows/AzureGameAssistant.exe`，双击即可启动界面并自动拉起 Release 采集进程；若清理过 Release 产物，需先执行 `dotnet build -c Release`。该快捷方式为开发机本地便利项，不随仓库分发。

预期：

- 首次启动若未提权且“启动时请求管理员权限”开启，会弹出一次 UAC；确认后界面与采集进程均以管理员运行，取消则继续浅模式且不再重复请求；启动参数加 `--no-elevate` 可跳过。
- 构建 0 警告、0 错误；测试 143 项全部通过（覆盖 AI 端点/脱敏/事实层/JSON 校验/HTTP 客户端/编排预算、掉帧与高负载追踪、日志保留与问题归档、FPS 时间窗与断帧排除，以及 MCP 协议与 schema 边界）。
- 界面显示六张指标卡（CPU / GPU / 内存 / 显存 / 磁盘 / 抖动）、默认一张合并折线图（六条不同颜色的曲线 + 右侧帧时间副轴）、Top-5 进程与采集状态；标题栏显示猫耳头像图标；CPU/GPU 卡片数值下方显示硬件型号。
- 日志默认常开：`%LOCALAPPDATA%\AzureGameAssistant\logs\` 每次运行都有记录；设置页“日志”关闭后界面与采集/传感器/MCP 均停止写入；保留天数默认 3，修改后保存即清理过期文件。
- 问题日志按月归档：`logs\issues\issues-YYYY-MM.log` 只收警告、错误与崩溃，文件头一行说明月份/版本/系统；设置页“日志”分组“打开问题日志”直达目录；制造一次警告或错误后应有对应记录，常规运行不影响该文件。
- 快速验证问题日志（不影响运行中的实例）：
  ```powershell
  & "src\AzureGameAssistant.Collector\bin\Release\net8.0\AzureGameAssistant.Collector.exe" --interval 0
  Get-Content "$env:LOCALAPPDATA\AzureGameAssistant\logs\issues\issues-$(Get-Date -Format yyyy-MM).log" -Tail 3
  ```
  预期退出码 1，归档末尾出现 `[ERR] [collector:<pid>] --interval 需要 100–10000 之间的毫秒数`。
- 内存卡按物理内存显示：16GB 机器显示“使用 x / 15.9 GB”与对应百分比；提交量 / 提交上限只用于 AI 与卡顿判定中的内存压力信号，不再出现在卡片副标题。
- 设置页取消“分开显示图表卡片”时只有合并图；勾选后变为每指标一张卡片，卡片头部只有勾选“布局设计”后才出现“↗”；点击后图表进入独立小窗，卡片显示“收回图表”，关闭小窗或取消“布局设计”后图表回到原位。默认启动只有主窗口一个界面窗口。
- 设置页可开关合并图中的每条曲线（分开模式下为每张图）、开关悬浮窗、选择四角位置与偏移、勾选悬浮内容；保存后立即生效。
- 状态卡“帧采集”区域可展开下拉框选择目标进程，开始后显示“帧采集启动中 / 运行中 / 需要管理员权限 / 不可用（PID …）”，停止后回到“帧采集未运行”；开启自动采集时，只有全屏或 GPU ≥ 5% 的前台游戏会自动开始采集，切换到桌面应用不会中断正在出帧的游戏，悬浮窗出现帧率数据。
- 三角洲行动或其他游戏采集 20 秒内应出现 FPS；瞬时帧率是最近约 1 秒的速率（60 帧游戏单次漏帧仍应显示约 57–59，不会跳到 30），平均帧与 1% Low 是最近约 10 秒，超长停顿的单帧不会拉低读数；游戏/ETW 短暂静默（几秒）时读取循环继续等待并记录“帧数据中断 N 秒后自行恢复”，界面保持最后读数且不出现“恢复中”；只有 PresentMon 存活但 25 秒无新帧且目标仍活跃时才重启会话（0.25/1/3 秒退避），目标始终无帧时按 2–15 秒慢速重试且不锁死；停止采集或目标退出时帧率立即显示“—”。
- “权限自检”按钮随时打开诊断窗口：管理员权限、Performance Log Users、PresentMon 哈希、ETW 会话、遥测心跳、帧采集、GPU 传感器、CPU 温度逐项显示 OK / 警告 / 缺失；可重新检测、复制报告；未提权时显示提权按钮。
- 设置页“权限与采集”可开关启动提权与自动帧采集，并选择 CPU 温度来源（ACPI / 驱动 / 关闭）；切换采样间隔、传感器开关/间隔或 CPU 模式保存后会重启采集进程。
- 设置页“AI 分析”可启用 AI、选择供应商、填写 Base URL / 模型、保存或删除 API Key（只显示“已保存 / 未保存”）、开关脱敏与进程名隐藏、设置每日/每月预算。
- 设置页 AI 分组点击“获取模型”后，按当前 Base URL 与 Key 拉取厂商模型列表；模型输入框保持可编辑，拉取成功后其右侧出现下拉列表，选择即填入模型名；未保存 Key 时提示“请先填写并保存 API Key”，401/超时/网络错误都有可读提示。
- 设置页 AI 分组点击“测试连接”后发送一次最小请求：成功显示端点与耗时；401 提示检查 API Key；非 JSON 响应提示接口不兼容；网络错误提示可读原因。只有点击时才联网。
- 设置页“诊断记录”可开启长时记录并选择 5/10/30 秒间隔；开启后立即在 `%LOCALAPPDATA%\AzureGameAssistant\diagnostics\diag-<时间戳>\` 生成 `context.json` / `samples.csv` / `processes.csv` / `events.jsonl`，每 5 秒约一行；正常退出时生成 `summary.txt`。“打开记录目录”可直接定位。
- 主界面“AI 分析”打开分析窗口：显示供应商、端点、预计 token/费用、预算、脱敏状态与 Key 状态；展示将要发送的 JSON 预览；首次启用需勾选同意；只有点击“运行”才会联网；401/429/超时/预算不足均有可读提示；失败时展示本地兜底报告。
- 分析窗口在帧分析下方新增“掉帧分析”和“高负载归因”：前者逐条显示发生时间、原因徽标、说明与嫌疑进程；后者按 CPU/GPU/显存/内存/磁盘显示造成高负载的进程与说明。事实层没有对应事件时不显示该区块。
- MCP：`artifacts\mcp\AzureGameAssistant.Mcp.exe` 按第 8 节 stdio 命令返回初始化与状态帧；已在 Qoder App 注册为 `azure-game-assistant`（6 个只读工具）；非管理员时状态含 `degraded: true` 与原因。
- “帧”图表卡在无帧数据时为空态、头部显示“—”；有数据后显示瞬时 FPS、“平均 / 1% Low”副标题与帧时间曲线，尖峰帧标红点。
- 设置页“文字外观”可选择配色主题；仅选“自定义颜色”时颜色输入框可用，输入 `#RRGGBB` 后色块实时预览，非法值按白色处理。
- 悬浮窗默认在左上角，无背景与边框，数据项从左到右排列，宽度不足时自动换行；有传感器数据时显示“GPU 温度 / GPU 热点 / CPU 温度”；本机驱动模式下 CPU 温度实测 47°C，ACPI 模式或未装 PawnIO 时为“—”；切换主题或自定义颜色后立即变色；鼠标可穿透到下方窗口；关闭主窗口后仍持续刷新；托盘菜单可快速开关。
- 启动自动提权后，主界面不再需要手动点“以管理员身份重启”；权限自检“帧率采集”应为 OK 并显示“正在采集所选游戏的帧率”。
- `measure.ps1` 输出的 `.summary.txt` 中 Collector 专用工作集约 10–13MB（AOT 浅模式约 10.1MB）、CPU 约 0.1%；UI 可见约 46–47MB，托盘 + 悬浮窗约 47MB / 0.09–0.13%。
- 语言与主题设置生效；30 分钟运行的采样缺口为 0。

关键文档自检：

- 需求文档包含 FR-019（帧级）、FR-020（LLM）、FR-021（AI 隐私/成本/安全）、FR-022（MCP 只读数据服务）、FR-023（长时诊断）、FR-024（日志常开与轮换）、FR-025（掉帧与高负载归因）、FR-026（软件问题日志按月归档）。
- 开发文档包含 ADR-013（PresentMon）、ADR-014（LHM 边车）、ADR-015（AI 接口）、ADR-021（UI 软件渲染）、ADR-024（MCP 只读进程）、ADR-025（日志策略）、ADR-026（掉帧/高负载标记）、ADR-027（FPS 统计与断帧）、ADR-028（软件问题按月归档）与第 10.13、11.9、18、21、22 节说明。
- 调研文档包含 PresentMon / CapFrameX / LHM 的许可证与复用边界。
- 提交规范包含 `Agent: Qoder` 强制规则与第三方/密钥禁令。

## 9. 交接日志

| 日期 | Agent | 内容 | 提交 |
| --- | --- | --- | --- |
| 2026-11-07 | Qoder | 完成需求访谈、四份文档、README 与 Git 初始化 | `docs: 初始化项目文档与仓库结构` |
| 2026-11-07 | Qoder | 完成开源调研；引入 PresentMon 帧级真相、LHM 传感器边车、OpenAI 兼容 LLM 分析；修订需求/开发/README/提交规范 | `a56e465 docs: 引入帧级真相与 AI 分析架构` |
| 2026-11-07 | Qoder | 完成 M1 基础闭环：采集进程、共享内存/管道、WPF 界面、托盘、i18n/主题、构建与测量脚本、20 项测试；完成性能初测与打包验证 | `feat(m1): 完成系统采集与界面基础闭环` |
| 2026-11-07 | Qoder | 修正 Release 误用 Debug 采集进程；完成 30 分钟可见 + 10 分钟托盘验收，采样无缺口、性能红线达标；测量脚本改用专用工作集口径 | `ed76f9d`、`427ba90` |
| 2026-11-07 | Qoder | 接入应用图标；拆分五张可开关折线图；实现桌面悬浮数据窗（四角定位、内容开关、鼠标穿透）；Release 抽测内存与 CPU 达标 | `feat(ui): 应用图标、分指标图表与桌面悬浮数据` |
| 2026-11-07 | Qoder | 悬浮数据窗改为无背景、横向排列自动换行、默认左上；文字加细阴影；抽测隐藏主窗口时 UI CPU 0.094% | `feat(ui): 悬浮数据改为无背景横向换行并默认左上` |
| 2026-11-07 | Qoder | 折线图支持脱离为独立小窗并自动归还；悬浮窗新增配色主题与自定义颜色；22 项测试通过，Release 实测小窗可见 UI CPU 0.391%、关闭后 0.052% | `d6247f3 feat(ui): 图表弹窗与悬浮窗配色主题` |
| 2026-11-07 | Qoder | 锁定 PresentMon 2.6.0 并核验 SHA256/签名；登记第三方许可证与 CLI/CSV 列名；新增帧 CSV 解析器与帧统计，测试增至 32 项 | `chore(frames): 锁定 PresentMon 2.6.0 并落地帧解析与统计` |
| 2026-11-07 | Qoder | 帧共享内存改为独立 seqlock，新增批量写帧与整批读取；补充布局测试，测试增至 36 项 | `feat(frames): 帧区独立 seqlock 与批量读写` |
| 2026-11-07 | Qoder | 接入 PresentMon 监督器：job object、批写、退避与权限降级；采集进程按目标 PID 启动帧采集并发布能力位 | `feat(collector): 接入 PresentMon 监督器` |
| 2026-11-07 | Qoder | 悬浮窗接入帧统计：增量读取帧区、FPS/平均帧/1% Low 显示与 2 秒失效；合成帧实机验证通过 | `feat(ui): 悬浮窗接入帧率与低帧显示` |
| 2026-11-07 | Qoder | 完成 K-12：安装 VS C++ 工具链到 F 盘，打通 NativeAOT 发布与默认打包；修正 PresentMon 包内路径，增加打包与运行时哈希校验；AOT 浅模式专用工作集 10.1MB；测试增至 39 项 | `f451778` |
| 2026-11-07 | Qoder | 完成 M2-2 帧采集界面闭环：新增 `frames.start` / `frames.stop` 与状态帧对象；状态卡支持选择目标进程并启停；UIA 实测权限不足提示与设置持久化，K-24 关闭 | `feat(ui): 帧采集目标选择与启停闭环` |
| 2026-11-07 | Qoder | 完成 M2-2 主窗口帧轨道：`FrameTimeline` 定长环 + QPC→UTC 对齐 + 每列 min/max 聚合与尖峰标记；新增“帧”图表卡、`charts.frames` 开关与脱离支持；合成帧实机截图验证，测试增至 44 项 | `feat(ui): 主窗口帧时间曲线与尖峰标记` |
| 2026-11-07 | Qoder | 完成传感器边车与 GPU 温度：LHM 0.9.6 GPU-only、传感器共享内存、驱动检测与监督退避；遥测升级 v3 并新增 CPU 温度槽；悬浮窗加入 CPU 温度与提权入口；修正 PresentMon 2.6 v2 CSV 列名；明日方舟真实帧率/温度实机验证通过；测试增至 49 项 | `feat(sensors): 接入 GPU 温度与 CPU 温度槽并完善提权入口` |
| 2026-11-07 | Qoder | 完成 M3-1：启动自动提权与单实例接管、内置权限自检窗口、前台游戏自动帧采集；CPU 温度新增 ACPI/驱动/关闭三档来源；设置保存后正确重启采集进程；Release 构建 0 警告、53 项测试通过；实机验证自动采集明日方舟帧率与自检结果 | `feat(ui): 启动自动提权、权限自检与前台自动帧采集` |
| 2026-11-07 | Qoder | 经用户确认安装 PawnIO 2.2.0（SHA256 + Authenticode 校验）并切到驱动模式；CPU 温度实测 47°C、Package 45W，权限自检转为 OK；登记第三方声明与 ACE 风险/回退方式 | `docs(handoff): 记录 PawnIO 安装与 CPU 温度验证` |
| 2026-11-07 | Qoder | 接入 AI 手动分析（OpenAI 兼容）：供应商预设与地址校验、Credential Manager、事实层/脱敏/预览/同意、SSE 客户端、严格 JSON 与危险建议过滤、预算与本地兜底、报告落盘；新增 MCP 只读 stdio 服务与发布脚本；测试增至 106 项 | `817c051` |
| 2026-11-07 | Qoder | 折线图默认统一单窗口（仅“布局设计”下可脱离）；自动采集仅针对全屏或 GPU≥5% 的游戏类前台进程；修复三角洲行动帧率：清理残留 ETW 会话并移除 `--stop_existing_session`，加入 20 秒首帧/10 秒停帧看门狗；MCP 登记到 Qoder App；实机 75 秒连续 8,379 帧，重启后约 60 FPS / 1% low 51.6 | `b5f9579` |
| 2026-11-07 | Qoder | 折线图默认改为单张合并叠加图：不同颜色共用 0–100% 纵轴、帧时间右侧 ms 副轴，多卡片改为可选设置；合并图按可视区自适应高度，坐标轴与时间标签完整可见；Release 构建 0 警告、106 项测试通过 | `54b9270` |
| 2026-11-07 | Qoder | 修正内存显示口径：改为物理内存使用率与总量（16GB 机器显示 15.9GB），提交上限不再当作内存总量；合并图移出滚动区直接铺满，修复高度异常与曲线不显示；MCP 快照补充 `totalMb` / `usedMb`；构建 0 警告、107 项测试通过 | `db3ba72` |
| 2026-11-07 | Qoder | AI 设置新增模型列表查询：按 Base URL 调用 `/models`，解析并过滤模型名供下拉选择，仍支持手动输入；无 Key 提示、401/超时/非 JSON 可读错误；修复设置窗缺少 `BoolToVisibility` 资源；构建 0 警告、118 项测试通过 | `f979968` |
| 2026-11-07 | Qoder | 新增长时诊断记录：5/10/30 秒 samples.csv、30 秒进程排行、状态事件 JSONL、退出摘要；设置页开关与目录入口；本机已开启 5 秒并实机验证首行数据与进程排行；构建 0 警告、121 项测试通过 | `ba8ab61` |
| 2026-11-09 | Qoder | 按用户 8 项要求：复盘日志并处理三角洲帧率短暂消失；CPU/GPU 卡片显示硬件型号；AI 设置新增“测试连接”；日志默认常开且可按 1–30 天保留（默认 3 天，子进程同步停写）；掉帧与高负载自动标记进入事实层，校验器强制模型逐条给出原因与高负载进程，分析窗新增结果卡片，诊断记录新增 `frame_drop` / `high_load` 事件；Release 0 警告 0 错误、135 项测试通过 | `33be2f6` |
| 2026-11-10 | Qoder | 帧率消失根因返工：日志证实 PresentMon 静默 2–5 秒可自愈，旧 10 秒看门狗反而把抖动放大成 12–16 秒空洞；改为 25 秒兜底且仅目标活跃时重启、正常退出跳过 ETW 清理、退避 0.25/1/3 秒、停帧不计失败次数；界面移除“恢复中…”并在短暂无帧时保持读数（主 30 秒 / 悬浮 20 秒）；Release 0 警告 0 错误、135 项测试通过 | `5ea6d12` |
| 2026-11-10 | Qoder | 帧率误读与无法识别：瞬时 FPS 改为约 1 秒窗口速率、平均与低帧改为约 10 秒窗口；≥1000ms 的断帧空洞移出统计；同 PID 失败会话可复活，无帧按 2–15 秒退避重试且不计失败，玩家回到游戏立即重试；新鲜度窗口 2→5 秒；诊断回放确认真实速率中位数 54.3 与旧平均 39.9 的差距源于单帧倒数与整环平均；Release 0 警告 0 错误、137 项测试通过；断帧阈值由 2 秒收紧到 1 秒以覆盖 1.5 秒级停顿帧 | `80d7d57`、`039f6b2` |
| 2026-11-10 | Qoder | 软件问题日志（FR-026）：新增 `IssueLog` 按月归档 Warn/Error/未处理异常到 `logs\issues\issues-YYYY-MM.log`，月份按写入时刻计算、跨月无需重启；`FileLogger` 转发归档（Info 不写入），四个进程补齐未处理异常处理器；按天清理不触及 `issues` 子目录；设置页新增“打开问题日志”；新增 6 项测试，总数 143 项通过，Release 0 警告 0 错误；以 `--interval 0` 触发真实参数错误实机验证归档格式 | `c9b68f6` |
| 2026-11-10 | Qoder | FR-006/009/011：`DetectionEngine`（帧/显示/抖动/CPU/GPU/内存/磁盘信号、连续评分、2s 合并、5s 冷却、深浅模式）、主界面 50 条卡顿列表、14 项阈值设置；全局热键（`MOD_NOREPEAT`、冲突错误码 1409 + 托盘气泡、设置页改键）、托盘悬停补内存、事件气泡点击回主界面；修复一次按键两条事件的重复入队；实测 3 次按键 = 3 个事件，156 项测试通过，Release 0 警告 0 错误 | `feat(detection): 接入卡顿判定、全局热键与托盘通知` |
| 2026-11-10 | Qoder | FR-007 会话记录与回放：gzip JSONL v2（头部重复事件摘要、64 帧/批、尾部 SHA256）、事件确认即写 `recording` 并在 `end+30s` 原位原子重写为 `complete`、`.tmp` + `Flush(true)` + `File.Move(overwrite)`、启动清理未完成文件、留存 20 个 / 500MB 可调；`SessionsWindow` 支持列表、时间轴回放、本地报告与删除；主界面与托盘新增入口；新增 5 项测试，总数 161 项通过，Release 0 警告 0 错误 | `feat(sessions): 会话记录、原子落盘与回放窗口` |
| 2026-11-10 | Qoder | 会话实机闭环与修复：点击“标记卡顿”后 0.14 秒出现 `recording`、30 秒后 `complete`；发现 NaN/Infinity 使 JSON 写入中断并留下半截 `.tmp`，在写入边界统一清洗并在失败时返回异常类型；新增回归测试，总数 162 项通过，Release 0 警告 0 错误 | `fix(sessions): 清洗非有限数并诊断写入失败` |
| 2026-11-10 | Qoder | FR-008 本地归因报告：7 类主因规则、温度阈值、断帧与尖峰分离、主因/帧证据一致性、可疑进程前 3、无数据兜底与中英报告；会话窗新增断帧数与一致性行，修正热降频标签与过时提示；新增 9 项报告测试，总数 171 项通过；实机会话报告 `gapCount 8 / spikeCount 0 / max 896.5ms / consistent true`；Release 0 警告 0 错误 | `4097d13` |
| 2026-11-10 | Qoder | FR-010 游戏自动识别与动态采样：`SHQueryUserNotificationState` / 95% 覆盖 / GPU≥5% 三路判定 + 排除名单，`GameModeTracker` 10 秒宽限；控制管道新增 `sampling` 命令，200ms / 1000ms 切换不重启采集进程，共享内存头与状态同步；修复 `frames.stop` 阻塞管道导致离开全屏后采集残留（后台释放 PresentMon + 客户端 6 秒超时 + UI 待办重试与目标抑制）；新增 `GameModeTests` 14 项，总数 185 项通过；实机全屏探针与星穹铁道均验证 200ms→自动采集→回落 1000ms→帧采集已停止；Release 0 警告 0 错误 | `308d7c6` |
| 2026-11-12 | Qoder | AI 报告合并进会话：`AiReportStore` 读写 `<会话>.ai.json`（原子写入、损坏静默忽略），`SessionFactsFactory` 从会话重建事实层并排除 ≥1000 ms 断帧；会话窗口新增“AI 分析”，成功后详情区显示摘要/主因/证据/建议，导出自动附带；离线回退不写入；`SessionSchema.DiscontinuityFrameMs` 统一阈值；新增 4 项测试，总数 198；Debug/Release 0 警告 0 错误 | `14b685b` |
| 2026-11-12 | Qoder | 文档一致性修正：FR-006 与开发文档 §9 统一为“显存/热降频只作归因证据”；§10.12 更新为实时快照与会话事实层两个入口；补充 handoff 3.17 | `56baadb` |
| 2026-11-12 | Qoder | 修复自动采集重试风暴与残留采集进程：冷却按 PID 真正生效（5 分钟 / 1 分钟 / 15 秒），采集端 `--parent` 父进程监控；`package.ps1` 运行中拒绝打包；补充 handoff 3.18 | `e45e270` |
| 2026-11-12 | Qoder | AI 成本控制补全：设置页新增输入 / 输出单价（美元 / 百万 Token），按供应商保存；校正 §10.9 自动分析与缓存说明；补充 handoff 3.19 | `7cfda7a` |
| 2026-11-12 | Qoder | AI 报告复用：会话报告记录模型与事实哈希（忽略语言），命中即展示并提示“已复用”、按钮变“重新分析”，旧报告不复用；新增 3 项测试，总数 201；Debug/Release 0 警告 0 错误；补充 handoff 3.20 | `d4976b1` |
| 2026-11-12 | Qoder | FR-020 严重事件自动分析：`AutoAnalysisPolicy` 统一判定评分 / 双预算 / 同意 / Key / 配置 / 预算；完整会话落盘后触发并写 `<会话>.ai.json`，忙时跳过不重试；设置页新增开关与最低评分，默认关闭 / 80；新增 6 项测试，总数 207；Debug/Release 0 警告 0 错误；补充 handoff 3.21 | `69abfa6` |
| 2026-11-12 | Qoder | 自动分析性能优化：会话解压、事实构建、哈希与门控判定移入 `Task.Run` 后台线程，界面线程只做日志 / 托盘提示 / 报告收尾；并发策略在开发文档中改为实际行为（忙时手动提示、自动跳过）；Release 0 警告 0 错误 | `381ce25` |
| 2026-11-12 | Qoder | 会话收尾补测：退出时 `FlushPending` 将未完成采集写成 `complete`，删除会话同时删除同名 `.ai.json`；新增 2 项测试，总数 209；Release 0 警告 0 错误 | `1b5685b` |
| 2026-11-12 | Qoder | 悬浮窗按显示器定位（K-16 部分）：主窗口所在显示器工作区 + DPI 换算 + 越界夹紧，定位失败回退主屏；新增 4 项定位单测，总数 213；Release 0 警告 0 错误；补充 handoff 3.22 | `0d4c4a3` |
| 2026-11-12 | Qoder | 便携包内置传感器边车（K-27）：`package.ps1` 自包含发布边车到 `collector\sensors\` 并复制 `LICENSES\`；采集进程按子目录 → 同目录 → 开发树解析；边车单独发布 81MB、依赖齐全，整包待复验；补充 handoff 3.23 | `e0b8f1d` |
| 2026-11-12 | Qoder | 包内说明文档：`package.ps1` 强制复制 `README.md` 与 `THIRD-PARTY-NOTICES.md`（缺失即失败），`LICENSE` 未定时改为警告；第三方声明许可路径改为包内相对路径；README 更新自动分析现状；新增 handoff 3.24 并把 K-30 标为已解决 | `cfd899c` |
| 2026-11-12 | Qoder | 界面提示双语补齐（FR-013）：单实例提示、保存设置失败、未处理异常三处改用资源键 `AlreadyRunning` / `SettingsSaveFailed` / `UiUnhandledError`；脚本核对 216 个资源键与中英各 442 条完全对齐；UI Release 0 警告 0 错误；补充 handoff 3.25 | `65c84cc` |
| 2026-11-12 | Qoder | 悬浮窗不透明度（FR-012 增强）：新增 `overlay.opacity`（30–100，默认 100）、`ApplyAppearance` 即时生效、设置页外观区滑杆与百分比；`SettingsServiceTests` 扩充默认值与夹紧断言，总数仍 213；Release 0 警告 0 错误；补充 handoff 3.26 | `42651af` |
| 2026-11-12 | Qoder | 打包布局自检：`package.ps1` 按 §14.4 必需项逐条校验，缺项即失败并列出路径；旧包实测报出 6 项缺失、合成完整目录 0 缺失；补充 handoff 3.27 | `待回填` |

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
