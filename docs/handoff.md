# 蓝天游戏助手 · 交接文档

> **强制规则：本文件必须在每个任务结束前更新。**
> 任何 Agent 接手项目时，先读本文件，再读 [需求文档](requirements.md)、[开发文档](development.md) 与 [开源调研](research.md)。
> 只记录“当前事实 + 下一步”，不要把设计细节复制进来；设计以开发文档为准，需求以需求文档为准。

| 项目 | 内容 |
| --- | --- |
| 文档编号 | AGS-HANDOFF |
| 当前版本 | v0.1.0 |
| 最后更新 | 2026-11-07 |
| 最后更新人 | Qoder |
| 仓库状态 | M1 全部完成；M2-0/M2-1/M2-2 完成并实机验证（含明日方舟、三角洲行动真实帧采集）；M3-0 传感器边车与 GPU 温度已落地；M3-1 启动自动提权、权限自检、游戏类前台自动帧采集已完成并实机验证；AI 手动分析（OpenAI 兼容）与 MCP 只读服务已落地；折线图默认改为单张合并叠加图（多卡片为可选设置）；PresentMon 残留 ETW 会话问题已修复；MCP 已登记到 Qoder App |

---

## 1. 当前快照

| 项 | 值 |
| --- | --- |
| 产品阶段 | M1（v0.1）完成；M2（帧级真相）完成核心闭环；M3-1 权限与自动采集闭环完成；M3 的 AI 手动分析与 MCP 只读服务已落地；界面与采集稳定性进入收尾 |
| 最新里程碑 | 折线图默认改为单张合并叠加图：CPU/GPU/内存/显存/磁盘/帧时间以不同颜色共用一个坐标系（帧时间右侧副轴），多卡片模式改为可选；内存卡片改为按物理内存显示（不再把提交上限当总量）；AI 设置支持按厂商 `/models` 拉取并选择模型；自动采集只对游戏类前台进程；PresentMon 残留 ETW 会话清理与停帧重启；三角洲行动帧率恢复；MCP 接入 Qoder App |
| 可运行产物 | `build/run-dev.ps1` 可启动；`build/package.ps1` 默认 AOT 流程可生成便携 ZIP（含 PresentMon）；`build/publish-mcp.ps1` 生成 `artifacts/mcp/AzureGameAssistant.Mcp.exe`；二号屏桌面快捷方式指向 Release |
| 代码分支 | `main`（`feat/m1-foundation` 已 squash 合入，分支保留） |
| 当前提交 | `f979968` |
| 未提交变更 | 无 |
| 阻塞事项 | 3 款游戏 PresentMon 实测与反作弊评估待用户游戏环境（已测明日方舟、三角洲行动）；AI 窗口待本轮重启 Release 后点击验证；MCP 已在 Qoder App 注册，注册前建立的会话需新开会话才会出现工具；深模式内存/CPU 与 60 分钟连续性待专项测量 |

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

## 4. 进行中

- **M3 AI**：库、UI 与自动化测试已完成；本轮已停止旧实例并重建 Release，`AiAnalysisWindow` 待本轮启动后做点击级验证（预览 JSON、同意、预算与错误提示）。
- **M3 MCP**：已在 Qoder App 注册为 `azure-game-assistant`，可枚举 6 个只读工具；正在进行的会话建立于注册之前，工具列表尚未刷新，新开一个会话即可直接调用 `diagnose` / `get_hardware_snapshot` / `get_frame_stats`。
- **帧采集连续性**：三角洲行动已连续采帧 75 秒验证通过；60 分钟后台渲染连续性与强杀 PresentMon 后 10 秒恢复仍待长测。
- **M3-1**：启动提权、权限自检、前台自动采集已实机闭环；本机已装 PawnIO 并切到驱动模式，CPU 温度可读。
- **M3-0**：GPU 传感器链路已通；边车进程内存/CPU 与其他厂商真机待补测。
- **M2-0**：明日方舟已实测；其余 2 款游戏与反作弊兼容性评估待用户提供环境。

## 5. 下一步（按顺序）

1. **AI 窗口人工验证**：双击二号屏快捷方式启动 Release → 设置页填写供应商 / Base URL / 模型 / API Key（或选 Ollama）→ 主界面“AI 分析”核对预览 JSON、同意、预算与错误提示。默认不联网，只有点击“运行”后才发请求。
2. **MCP Agent 验证**：新开一个 Qoder 会话（本会话建立于注册前），调用 `diagnose`、`get_hardware_snapshot`、`get_history`、`get_frame_stats`；若工具缺失，对连接器执行 `retry`。非管理员会话的 `get_collector_status` 显示 `degraded` 属设计行为。
3. **帧采集长测**：三角洲行动或其他游戏 60 分钟连续采集，重点观察 ETW 会话是否再次停帧、PresentMon 是否重复重启、帧率与 1% low 是否连续；另做强杀 PresentMon 后 10 秒恢复验证。
4. **ACE 兼容性观察**：在装有 PawnIO 的当前环境启动明日方舟，确认 ACE 是否拦截；若出现异常，立即 `sc stop PawnIO` 并切回 ACPI 模式。任何情况下不注入、不绕过反作弊。
5. **M2-0 实测**：再测 2 款游戏 + 反作弊兼容性评估（K-07），并复测深模式（PresentMon + 传感器边车运行中）采集进程与边车的内存/CPU。
6. **M3-0 传感器补测**：边车专用工作集与 CPU 占用；AMD/Intel GPU 各验证一次；确认 GPU-only 永不触发驱动加载。
7. **自动分析（FR-020 剩余项）**：需先有事件评分写入源；在此之前不要添加“自动分析”开关。
8. **8 小时稳定性**：重点复核 Collector 句柄趋势（K-15）与睡眠唤醒。
9. **多显示器悬浮窗**：按显示器工作区定位（K-16）。

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
| PresentMon 会话 | 启动前用 `--terminate_existing_session --no_csv` 清理同名残留会话；正式采集不带 `--stop_existing_session`；500ms 轮询 + 20 秒首帧 / 10 秒停帧看门狗 | 残留 ETW 会话会让新会话静默停帧；三角洲行动实测恢复 |
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
| AI 触发 | 手动触发（主界面“AI 分析”按钮 + 预览/同意）；严重事件自动分析未实现，不提供开关 | 当前无事件评分写入源；避免后台请求与无效控件 |
| MCP 服务 | 独立只读 stdio 进程，6 个有界工具，不写系统、不执行命令 | 用户要求 Agent 直读硬件数据；ADR-024 |
| 默认联网 | 禁止；仅用户显式开启 AI 后访问其确认的地址 | 用户此前“纯本地”要求，按新需求放宽为“本地优先” |
| 提交标注 | 人类作者不变，footer 加 `Agent: Qoder`；其他 Agent 用自己名称 | 用户确认 |
| 分支模型 | 主干 + 短分支 + PR Squash | 用户确认 |
| 第三方分发 | PresentMon MIT、LHM MPL-2.0；固定版本 + SHA256 + 许可证清单 | 调研结论；ADR-020 |
| UI 渲染 | WPF 软件渲染；界面低频刷新，换取更低常驻内存 | 实测专用工作集约 43MB；ADR-021 |
| 分指标图表 | 默认一张合并折线图：各指标不同颜色叠加、共用 0–100% 纵轴（显存按容量百分比），帧时间用右侧 ms 副轴；设置中勾选“分开显示图表卡片”才切换为每指标一张独立卡片 | 用户要求默认在一个折线图里用不同颜色表示；多卡片作为可选布局 |
| 内存口径 | 卡片、图表与 MCP 的“内存使用率”按物理内存计算：`(总物理内存 − Available MBytes) / 总物理内存`，总量由 `GlobalMemoryStatusEx` 读取并缓存；已提交 / 提交上限只用于提交压力判定 | 用户设备 16GB，提交上限（物理 + 页面文件）31.9GB 被误显示为“内存总量”；物理口径与任务管理器一致 |
| 模型选择 | 设置页“获取模型”按 Base URL 调用厂商 `/models`，结果供下拉选择并写回可编辑模型名；仅用户点击时联网，未保存 Key 时提示先保存 | 用户要求直接选择厂商模型而非手输；保持 BYOK 与“默认不联网”边界 |
| 图表弹窗 | 默认只有一个主窗口；勾选“布局设计”后才显示脱离按钮，可把单图弹出为独立小窗，关闭小窗或取消勾选自动归还；同一指标仅一个实例 | 用户要求默认统一单窗、仅设计时可多窗；避免复制缓冲与双份渲染 |
| 桌面悬浮数据窗 | 无背景无边框，数据项横向排列并自动换行，默认左上；四角 + 偏移定位，内容项独立开关，鼠标穿透、不抢焦点 | 用户要求只显示数据、横向换行、默认左上；不注入游戏 |
| 悬浮窗配色 | 默认 / 浅色 / 天空蓝 / 自定义 `#RRGGBB`；标签同色降透明度，阴影按亮度自动黑/白 | 用户要求可设置颜色与主题；保证浅色桌面可读 |
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
| K-16 | 悬浮窗定位仅使用主显示器工作区 | 多显示器下无法指定副屏角落 | M2 手工验收矩阵覆盖；M3 增加按显示器记忆 |
| K-17 | ~~悬浮窗 GPU 温度仍显示“—”~~ | 已解决 | LHM 边车已接入，实机显示 GPU 温度 / 热点；FPS / 平均帧 / 1% Low 已在 M2-2 接通 |
| K-18 | ~~五张图在固定高度图区内需纵向滚动~~ | 已解决（默认合并图） | 默认改为单张合并叠加图并按可视区自适应高度，不再需要滚动；切到“分开显示图表卡片”后多卡片仍可纵向滚动 |
| K-19 | 悬浮数据无背景，极亮或高对比桌面背景下可读性依赖文字阴影 | 个别壁纸下数值不够清晰 | 已加细阴影；仍不足时由用户调整偏移或关闭壁纸；M3 再评估可选半透明底 |
| K-20 | 图表小窗每次打开都居中，不记忆位置、尺寸与置顶状态 | 常用的小窗需重新摆放 | 本轮按最小实现；后续可把窗口位置写入设置；窗口关闭后图表一定归还，不影响数据 |
| K-21 | 悬浮窗自定义颜色只接受 `#RRGGBB`，不支持透明度与渐变 | 无法单独调节文字透明度 | 标签已固定 78% 不透明度；M3 如需再扩展 `#AARRGGBB` |
| K-23 | 本机 ACPI 热区不可用（MAXSUN B550M + Ryzen 5 5600） | 默认 ACPI 模式下 CPU 温度显示“—” | 已由用户确认安装 PawnIO 2.2.0 并切到驱动模式，实测 CPU 温度 47°C；新环境仍需用户自行安装，程序不自动装 |
| K-28 | 本机存在已停止的 `WinRing0_1_2_0` 服务（其他工具遗留），PawnIO 已安装并运行 | 装 PawnIO 后内核驱动数量增加，ACE 可能视为环境异常 | 用户已被告知并确认；PawnIO 为 `DEMAND_START`，游戏前可 `sc stop PawnIO`，或 `PawnIO_setup.exe -uninstall -silent` 卸载；边车仍会在检测到运行中的已知 Ring0 驱动时拒绝启动 |
| K-25 | 传感器边车进程的专用工作集 / CPU 占用尚未单独测量 | 深模式整体预算未定论 | 下一轮专项测量；边车仅 1s 节拍、无 GUI，预计远低于 35MB 预算 |
| K-26 | LHM 0.9.6 对本机 RTX 4060 不提供功耗传感器 | GPU 功耗为“—” | 不写 0；改用驱动 SDK 或新版 LHM 时再补，保持缺失语义 |
| K-27 | 便携包尚未包含 `AzureGameAssistant.Sensors.exe` 与 LHM 依赖 | 分发版本的 GPU/CPU 温度不显示 | 开发态从同仓 Release 输出解析边车；M3 打包阶段把边车与 LICENSES 一并放入 `sensors\` 并校验 |
| K-29 | `AiAnalysisWindow` 尚未做本机点击级验证 | UI 交互问题可能未被发现 | 本轮已停止旧实例、重建并重启 Release；下一步按第 5 节步骤点击验证。服务层已有 106 项测试覆盖 |
| K-30 | FR-020“严重事件自动分析”未实现 | 与需求文档原描述存在差距 | 已在需求文档标注实现现状；不提供开关，避免后台请求。待事件评分源落地后实现 |
| K-31 | MCP 以非管理员运行时 `get_collector_status` 为 `degraded` | 缺少运行时长与最近错误 | 属设计行为；快照/历史/帧统计/进程排行不受影响。需要完整状态时由用户以管理员身份运行 Agent |
| K-32 | MCP 默认框架依赖单文件，目标机需 .NET 8 运行时 | 无运行时环境无法启动 | `build/publish-mcp.ps1 -SelfContained` 生成自包含版本 |
| K-33 | ~~同名 PresentMon ETW 残留会话导致新会话静默停帧~~ | 已解决 | 启动前 `--terminate_existing_session --no_csv` 清理，正式会话不再带 `--stop_existing_session`；500ms 轮询看门狗；三角洲行动 75 秒连续 8,379 帧验证通过 |
| K-34 | Qoder App 在 MCP exe 被替换后可能保留 `MCP_PROTOCOL_CONNECTION_CLOSED` 失败状态 | 该连接器工具暂时不可见 | 对连接器执行 `retry`；在注册前建立的会话中不会出现新工具，需新开会话 |

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
- 构建 0 警告、0 错误；测试 106 项全部通过（覆盖 AI 端点/脱敏/事实层/JSON 校验/HTTP 客户端/编排预算，以及 MCP 协议与 schema 边界）。
- 界面显示六张指标卡（CPU / GPU / 内存 / 显存 / 磁盘 / 抖动）、默认一张合并折线图（六条不同颜色的曲线 + 右侧帧时间副轴）、Top-5 进程与采集状态；标题栏显示猫耳头像图标。
- 内存卡按物理内存显示：16GB 机器显示“使用 x / 15.9 GB”与对应百分比；提交量 / 提交上限只用于 AI 与卡顿判定中的内存压力信号，不再出现在卡片副标题。
- 设置页取消“分开显示图表卡片”时只有合并图；勾选后变为每指标一张卡片，卡片头部只有勾选“布局设计”后才出现“↗”；点击后图表进入独立小窗，卡片显示“收回图表”，关闭小窗或取消“布局设计”后图表回到原位。默认启动只有主窗口一个界面窗口。
- 设置页可开关合并图中的每条曲线（分开模式下为每张图）、开关悬浮窗、选择四角位置与偏移、勾选悬浮内容；保存后立即生效。
- 状态卡“帧采集”区域可展开下拉框选择目标进程，开始后显示“帧采集启动中 / 运行中 / 需要管理员权限 / 不可用（PID …）”，停止后回到“帧采集未运行”；开启自动采集时，只有全屏或 GPU ≥ 5% 的前台游戏会自动开始采集，切换到桌面应用不会中断正在出帧的游戏，悬浮窗出现帧率数据。
- 三角洲行动或其他游戏采集 20 秒内应出现 FPS；若 PresentMon 进程存活但 10 秒无新帧且目标仍活跃，采集进程会自动重启会话，无需手动干预。
- “权限自检”按钮随时打开诊断窗口：管理员权限、Performance Log Users、PresentMon 哈希、ETW 会话、遥测心跳、帧采集、GPU 传感器、CPU 温度逐项显示 OK / 警告 / 缺失；可重新检测、复制报告；未提权时显示提权按钮。
- 设置页“权限与采集”可开关启动提权与自动帧采集，并选择 CPU 温度来源（ACPI / 驱动 / 关闭）；切换采样间隔、传感器开关/间隔或 CPU 模式保存后会重启采集进程。
- 设置页“AI 分析”可启用 AI、选择供应商、填写 Base URL / 模型、保存或删除 API Key（只显示“已保存 / 未保存”）、开关脱敏与进程名隐藏、设置每日/每月预算。
- 设置页 AI 分组点击“获取模型”后，按当前 Base URL 与 Key 拉取厂商模型列表；模型输入框保持可编辑，拉取成功后其右侧出现下拉列表，选择即填入模型名；未保存 Key 时提示“请先填写并保存 API Key”，401/超时/网络错误都有可读提示。
- 主界面“AI 分析”打开分析窗口：显示供应商、端点、预计 token/费用、预算、脱敏状态与 Key 状态；展示将要发送的 JSON 预览；首次启用需勾选同意；只有点击“运行”才会联网；401/429/超时/预算不足均有可读提示；失败时展示本地兜底报告。
- MCP：`artifacts\mcp\AzureGameAssistant.Mcp.exe` 按第 8 节 stdio 命令返回初始化与状态帧；已在 Qoder App 注册为 `azure-game-assistant`（6 个只读工具）；非管理员时状态含 `degraded: true` 与原因。
- “帧”图表卡在无帧数据时为空态、头部显示“—”；有数据后显示瞬时 FPS、“平均 / 1% Low”副标题与帧时间曲线，尖峰帧标红点。
- 设置页“文字外观”可选择配色主题；仅选“自定义颜色”时颜色输入框可用，输入 `#RRGGBB` 后色块实时预览，非法值按白色处理。
- 悬浮窗默认在左上角，无背景与边框，数据项从左到右排列，宽度不足时自动换行；有传感器数据时显示“GPU 温度 / GPU 热点 / CPU 温度”；本机驱动模式下 CPU 温度实测 47°C，ACPI 模式或未装 PawnIO 时为“—”；切换主题或自定义颜色后立即变色；鼠标可穿透到下方窗口；关闭主窗口后仍持续刷新；托盘菜单可快速开关。
- 启动自动提权后，主界面不再需要手动点“以管理员身份重启”；权限自检“帧率采集”应为 OK 并显示“正在采集所选游戏的帧率”。
- `measure.ps1` 输出的 `.summary.txt` 中 Collector 专用工作集约 10–13MB（AOT 浅模式约 10.1MB）、CPU 约 0.1%；UI 可见约 46–47MB，托盘 + 悬浮窗约 47MB / 0.09–0.13%。
- 语言与主题设置生效；30 分钟运行的采样缺口为 0。

关键文档自检：

- 需求文档包含 FR-019（帧级）、FR-020（LLM）、FR-021（AI 隐私/成本/安全）、FR-022（MCP 只读数据服务）。
- 开发文档包含 ADR-013（PresentMon）、ADR-014（LHM 边车）、ADR-015（AI 接口）、ADR-021（UI 软件渲染）、ADR-024（MCP 只读进程）与第 21 节 MCP 说明。
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
