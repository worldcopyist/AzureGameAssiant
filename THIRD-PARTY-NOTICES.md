# 第三方组件声明

本文件登记随项目分发或链接的第三方组件。升级版本时必须同步更新哈希与许可证。

| 组件 | 版本 | 来源 | 许可证 | 本地路径 |
| --- | --- | --- | --- | --- |
| Intel PresentMon（控制台版） | 2.6.0 | https://github.com/GameTechDev/PresentMon | MIT | `third_party/presentmon/` |
| LibreHardwareMonitorLib | 0.9.6 | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor | MPL-2.0 | NuGet 引用；许可证全文 `LICENSES/LibreHardwareMonitorLib-MPL-2.0.txt` |
| System.Management | 10.0.2 | https://github.com/dotnet/runtime | MIT | NuGet 引用（传感器边车 WMI） |

## LibreHardwareMonitorLib 0.9.6

- 使用项目：`AzureGameAssistant.Sensors`（独立边车进程）。
- 许可证：Mozilla Public License 2.0；许可证全文：`LICENSES/LibreHardwareMonitorLib-MPL-2.0.txt`（与 `collector/sensors/LICENSES/` 内容一致）。
- 使用方式：通过 NuGet 动态链接，未修改源文件；默认仅启用 GPU 硬件组。用户可选开启 `driver` CPU 模式，此时启用 CPU 硬件组，由 LHM 通过 PawnIO 读取 MSR/SMU。
- 分发依据：MPL-2.0 允许以二进制形式随附分发；保留许可证全文与源码地址。若未来修改 LHM 文件，必须公开修改后的文件。

## PawnIO（可选，用户自行安装，不随项目分发）

- 版本：2.2.0（安装包 `PawnIO_setup.exe`，3,410,960 字节）
- 来源：https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0
- SHA256：`1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032`
- Authenticode：有效，签名者 `CN=namazso.eu, O=namazso, C=HU`
- 许可证：GPL-2.0（驱动本体）；本项目不分发该驱动，仅在用户自行安装后由 LHM 调用，不构成对驱动的再分发。
- 使用方式：仅当用户在设置中选择“驱动”CPU 温度来源时，LHM 才会打开 `\Device\PawnIO` 读取 CPU 温度；程序不自动安装、不自动更新驱动。
- 风险提示：内核驱动可能被游戏反作弊视为环境异常；是否安装由用户自行决定，可随时通过 `PawnIO_setup.exe -uninstall -silent` 卸载。

## System.Management 10.0.2

- 使用项目：`AzureGameAssistant.Sensors`，仅用于用户态 WMI ACPI 热区查询。
- 许可证：MIT（.NET Runtime 项目）；随 NuGet 包分发，保留版权与许可证声明。

## Intel PresentMon 2.6.0

- 文件：`PresentMon-2.6.0-x64.exe`（980,320 字节）
- SHA256：`b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af`
- Authenticode：有效，签名者 `CN=Intel Corporation, O=Intel Corporation, S=California, C=US`
- 许可证全文：`collector/presentmon/LICENSE.txt`（MIT，Copyright (C) 2017-2024 Intel Corporation）
- 使用方式：作为独立子进程运行，仅读取官方输出；不修改、不注入、不暗示 Intel 背书。
- 分发依据：MIT 许可证允许随附分发，需保留版权声明与许可证全文。
