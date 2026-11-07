using System.Text.Json.Nodes;

namespace AzureGameAssistant.Mcp;

/// <summary>
/// Read-only tool descriptions exposed over MCP. The schemas are intentionally
/// small: every tool has bounded arguments so an agent cannot request an
/// unbounded copy of shared memory.
/// </summary>
public static class ToolCatalog
{
    public const string ProtocolVersion = "2024-11-05";

    public static IReadOnlyList<string> Names { get; } =
    [
        "get_hardware_snapshot",
        "get_history",
        "get_frame_stats",
        "list_processes",
        "diagnose",
        "get_collector_status",
    ];

    public static JsonArray Describe()
    {
        var tools = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "get_hardware_snapshot",
                ["description"] = "读取当前硬件快照：CPU/GPU 使用率、温度、功耗、显存、内存、磁盘延迟、" +
                                  "系统型号、前台进程、最近的帧信息。需要蓝天游戏助手采集进程正在运行。",
                ["inputSchema"] = Object(new JsonObject(), []),
            },
            new JsonObject
            {
                ["name"] = "get_history",
                ["description"] = "读取最近一段时间的采样历史并按桶聚合，用于观察趋势或定位卡顿区间。" +
                                  "返回每秒/每桶的 CPU、GPU、内存、显存、磁盘、计时器抖动统计。",
                ["inputSchema"] = Object(
                    new JsonObject
                    {
                        ["seconds"] = Int("回看窗口（秒）", 1, 3600, 60),
                        ["max_points"] = Int("返回的最大数据点数量", 2, 900, 60),
                        ["metrics"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["description"] = "要包含的指标，缺省为全部",
                            ["items"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["enum"] = new JsonArray("cpu", "gpu", "memory", "vram", "disk", "jitter"),
                            },
                            ["maxItems"] = 6,
                        },
                    },
                    []),
            },
            new JsonObject
            {
                ["name"] = "get_frame_stats",
                ["description"] = "读取最近一段时间的帧时间统计：平均帧率、1% low、0.1% low、最大帧时间、" +
                                  "CPU/GPU 等待、显示间隔和帧延迟。需要帧采集已启动。",
                ["inputSchema"] = Object(
                    new JsonObject
                    {
                        ["seconds"] = Int("回看窗口（秒）", 1, 60, 10),
                    },
                    []),
            },
            new JsonObject
            {
                ["name"] = "list_processes",
                ["description"] = "列出采集进程当前跟踪的占用最高的进程（最多 5 个），" +
                                  "包含 CPU、GPU、工作集和是否前台。",
                ["inputSchema"] = Object(
                    new JsonObject
                    {
                        ["limit"] = Int("返回数量", 1, 5, 5),
                    },
                    []),
            },
            new JsonObject
            {
                ["name"] = "diagnose",
                ["description"] = "诊断采集链路：共享内存是否打开、心跳是否新鲜、帧采集与传感器状态、" +
                                  "控制管道是否可用，并给出可操作的修复提示。",
                ["inputSchema"] = Object(new JsonObject(), []),
            },
            new JsonObject
            {
                ["name"] = "get_collector_status",
                ["description"] = "查询采集进程状态（暂停、采样间隔、运行时长、最近错误）。" +
                                  "非管理员进程会自动退回共享内存状态（缺少运行时长与最近错误）。",
                ["inputSchema"] = Object(new JsonObject(), []),
            },
        };

        return tools;
    }

    private static JsonObject Object(JsonObject properties, string[] required) => new()
    {
        ["type"] = "object",
        ["properties"] = properties,
        ["required"] = new JsonArray(required.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()),
        ["additionalProperties"] = false,
    };

    private static JsonObject Int(string description, int minimum, int maximum, int defaultValue) => new()
    {
        ["type"] = "integer",
        ["description"] = description,
        ["minimum"] = minimum,
        ["maximum"] = maximum,
        ["default"] = defaultValue,
    };
}
