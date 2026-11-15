# 第三方组件与开源参考声明

本项目（蓝天游戏助手 · AzureGameAssistant）以 **GNU General Public License v3.0** 发布，许可证全文见根目录 [`LICENSE`](LICENSE)。

Copyright (C) 2026 worldcopyist

本文件登记两类内容：

1. **随项目分发或动态链接的第三方组件** —— 必须保留许可证与版权声明；
2. **仅参考设计、接口或产品思路的开源项目** —— 未复制其代码，列明出处与许可证，便于审计与致谢。

升级任何第三方版本时必须同步更新版本、哈希与许可证路径。

---

## 1. 随项目分发或链接的组件

| 组件 | 版本 | 来源 | 许可证 | 分发方式 | 许可证位置 |
| --- | --- | --- | --- | --- | --- |
| Intel PresentMon（控制台版） | 2.6.0 | https://github.com/GameTechDev/PresentMon | MIT | 随包附带官方 exe，子进程调用 | `third_party/presentmon/LICENSE.txt`（包内 `collector/presentmon/LICENSE.txt`） |
| LibreHardwareMonitorLib | 0.9.6 | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor | MPL-2.0 | NuGet 动态链接，未修改 | `third_party/licenses/LibreHardwareMonitorLib-MPL-2.0.txt`（包内 `collector/sensors/LICENSES/`） |
| DiskInfoToolkit | 1.1.2（随 LHM 传递） | https://github.com/Blacktempel/DiskInfoToolkit | MPL-2.0 | NuGet 动态链接，未修改 | 同 MPL-2.0 全文 |
| RAMSPDToolkit-NDD | 1.4.2（随 LHM 传递） | https://github.com/Blacktempel/RAMSPDToolkit | MPL-2.0 | NuGet 动态链接，未修改 | 同 MPL-2.0 全文 |
| BlackSharp.Core | 1.0.7（随 LHM 传递） | https://github.com/Blacktempel/BlackSharp | MPL-2.0 | NuGet 动态链接，未修改 | 同 MPL-2.0 全文 |
| HidSharp | 2.6.4（随 LHM 传递） | https://software.seekye.com/hidsharp | Apache-2.0 | NuGet 动态链接，未修改 | https://www.apache.org/licenses/LICENSE-2.0 |
| Mono.Posix.NETStandard | 1.0.0（随 LHM 传递） | https://github.com/dotnet/runtime | MIT | NuGet 动态链接，未修改 | 随包内 NuGet 元数据 |
| System.Management | 10.0.2 | https://github.com/dotnet/runtime | MIT | NuGet 动态链接 | 随包内 NuGet 元数据 |
| .NET 8 运行时 / WPF / Windows Forms | 8.0 | https://github.com/dotnet/runtime / https://github.com/dotnet/wpf | MIT | 自包含发布，随包附带运行时文件 | https://github.com/dotnet/runtime/blob/main/LICENSE.TXT |

### Intel PresentMon 2.6.0

- 文件：`PresentMon-2.6.0-x64.exe`（980,320 字节）
- SHA256：`b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af`
- Authenticode：有效，签名者 `CN=Intel Corporation, O=Intel Corporation, S=California, C=US`
- 使用方式：作为独立子进程运行，仅读取官方 CSV 输出；不修改、不注入、不暗示 Intel 背书。
- 分发依据：MIT 许可证允许随附分发，保留版权声明与许可证全文。

### LibreHardwareMonitorLib 0.9.6

- 使用项目：`AzureGameAssistant.Sensors`（独立边车进程）。
- 使用方式：通过 NuGet 动态链接，未修改源文件；默认仅启用 GPU 硬件组。用户可选开启 `driver` CPU 模式，此时启用 CPU 硬件组，由 LHM 通过 PawnIO 读取 MSR/SMU。
- 分发依据：MPL-2.0 允许以二进制形式随附分发；保留许可证全文与源码地址。本项目未修改任何 LHM 文件，因此无文件级公开义务；如未来修改，必须公开修改后的文件。

### 自包含运行时

- 界面进程与传感器边车以 `--self-contained` 方式发布，包内 `.dll` / `hostfxr.dll` / `coreclr.dll` 等来自 .NET 8 运行时与 WPF，均为 MIT 许可。
- 这些文件仅用于运行本项目，不单独对外提供。

---

## 2. 仅参考设计、未复制代码的开源项目

调研过程与复用边界详见 [`docs/research.md`](docs/research.md)。以下项目只提供产品思路、指标口径或交互参考，**没有把代码、注释或文案复制进本仓库**：

| 项目 | 许可证 | 参考内容 | 是否复制代码 |
| --- | --- | --- | --- |
| [CapFrameX](https://github.com/CXWorld/CapFrameX) | MIT | PresentMon 子进程托管与重启、CSV 聚合、帧环形缓冲容量策略、会话元数据字段 | 否 |
| [StutterScope](https://github.com/AdenXie/StutterScope) | MIT | 离线、可分享的报告结构；手动开始/停止与容量上限 | 否 |
| [game-stutter-recorder](https://github.com/jieke7k7k/game-stutter-recorder) | MIT | 游戏自动猜测、NVML 显存读取、中文结论写作口吻 | 否 |
| [stutter-test](https://github.com/jkoens3/stutter-test) | 无许可证 | 卡顿分类维度（着色器编译 / 资产流送 / 帧节奏 / 热降频），仅概念 | 否（净室实现） |
| [System Informer](https://github.com/winsiderss/systeminformer) | MIT | 进程树与句柄信息的交互思路 | 否 |
| [SidebarDiagnostics](https://github.com/ArcadeRenegade/SidebarDiagnostics) | GPL-3.0 | 桌面监控侧栏的布局思路 | 否 |
| [OCAT](https://github.com/GPUOpen-Tools/ocat) | 不明确 | 仅产品思路 | 否（许可证不明确，禁止复用） |

---

## 3. 可选驱动（不随项目分发）

### PawnIO

- 版本：2.2.0（安装包 `PawnIO_setup.exe`，3,410,960 字节）
- 来源：https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0
- SHA256：`1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032`
- Authenticode：有效，签名者 `CN=namazso.eu, O=namazso, C=HU`
- 许可证：GPL-2.0（驱动本体）
- 分发说明：本项目**不分发**该驱动。仅当用户自行安装并在设置中选择“驱动”CPU 温度来源时，LHM 才会打开 `\Device\PawnIO` 读取 CPU 温度；程序不自动安装、不自动更新驱动。
- 许可证关系：PawnIO 是独立程序，与本项目之间只存在运行时调用，不构成组合作品，因此其 GPL-2.0 不改变本项目的 GPL-3.0 授权。
- 风险提示：内核驱动可能被游戏反作弊视为环境异常；是否安装由用户自行决定，可随时通过 `PawnIO_setup.exe -uninstall -silent` 卸载。

---

## 4. 许可证兼容性说明（GPL-3.0）

| 上游许可证 | 与本项目 GPL-3.0 的关系 | 说明 |
| --- | --- | --- |
| MIT | 兼容 | 允许再分发与再许可，保留版权与许可声明即可 |
| Apache-2.0（HidSharp） | 兼容 | Apache-2.0 与 GPL-3.0 兼容，保留 NOTICE 与许可证文本 |
| MPL-2.0（LHM 及其传递依赖） | 兼容 | MPL-2.0 第 3.3 条提供次级许可，可并入 GPL-3.0 组合作品；本项目未修改这些文件 |
| GPL-2.0（PawnIO） | 不构成组合作品 | 驱动不随包分发，属独立程序 |
| 无许可证（stutter-test） | 未使用代码 | 仅采纳公开可验证的分类概念，净室实现 |

结论：现有依赖与参考对象均不阻碍本项目以 GPL-3.0 发布。若未来引入 GPL-2.0-only、SSPL、BUSL 等不兼容许可证的代码或二进制，必须先评估并更新本文件。
