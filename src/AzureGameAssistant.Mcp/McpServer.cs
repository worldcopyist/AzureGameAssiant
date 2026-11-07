using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AzureGameAssistant.Core.Logging;

namespace AzureGameAssistant.Mcp;

/// <summary>
/// Minimal JSON-RPC 2.0 server for the MCP stdio transport. Only the methods
/// needed by tool consumers are implemented; stdout carries protocol frames and
/// nothing else, diagnostics go to the log file and stderr.
/// </summary>
public sealed class McpServer
{
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private readonly HardwareInspector _inspector;
    private readonly FileLogger? _logger;
    private readonly string _version = typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public McpServer(HardwareInspector inspector, FileLogger? logger = null)
    {
        _inspector = inspector;
        _logger = logger;
    }

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonNode? response;
            try
            {
                response = await HandleAsync(line, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.Error("处理 MCP 请求失败", ex);
                response = Error(null, -32603, "服务器内部错误");
            }

            if (response is null)
            {
                continue;
            }

            await output.WriteLineAsync(response.ToJsonString(WireOptions)).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<JsonNode?> HandleAsync(string line, CancellationToken cancellationToken)
    {
        JsonNode? request;
        try
        {
            request = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            return Error(null, -32700, "JSON 解析失败");
        }

        if (request is not JsonObject message)
        {
            return Error(null, -32600, "无效请求");
        }

        JsonNode? id = message["id"];
        bool isNotification = id is null;
        string method = message["method"] is JsonValue value && value.TryGetValue(out string? text)
            ? text ?? string.Empty
            : string.Empty;

        if (method.Length == 0)
        {
            return isNotification ? null : Error(id, -32600, "缺少 method");
        }

        switch (method)
        {
            case "initialize":
                return isNotification ? null : Result(id, BuildInitialize(message));

            case "notifications/initialized":
            case "notifications/cancelled":
            case "notifications/roots/list_changed":
                return null;

            case "ping":
                return isNotification ? null : Result(id, new JsonObject());

            case "tools/list":
                return isNotification
                    ? null
                    : Result(id, new JsonObject { ["tools"] = ToolCatalog.Describe() });

            case "tools/call":
                return isNotification ? null : await CallToolAsync(id, message, cancellationToken).ConfigureAwait(false);

            default:
                return isNotification ? null : Error(id, -32601, "不支持的方法：" + method);
        }
    }

    private async Task<JsonNode> CallToolAsync(JsonNode? id, JsonObject message, CancellationToken cancellationToken)
    {
        JsonObject? parameters = message["params"] as JsonObject;
        string name = parameters?["name"] is JsonValue value && value.TryGetValue(out string? text)
            ? text ?? string.Empty
            : string.Empty;

        if (name.Length == 0)
        {
            return Error(id, -32602, "缺少工具名称");
        }

        JsonObject? arguments = parameters?["arguments"] as JsonObject;
        _logger?.Info("调用工具 " + name);

        try
        {
            JsonObject result = name switch
            {
                "get_hardware_snapshot" => _inspector.GetSnapshot(),
                "get_history" => _inspector.GetHistory(
                    ReadInt(arguments, "seconds", 60, 1, 3600),
                    ReadInt(arguments, "max_points", 60, 2, 900),
                    ReadStrings(arguments, "metrics")),
                "get_frame_stats" => _inspector.GetFrameStats(
                    ReadInt(arguments, "seconds", 10, 1, 60)),
                "list_processes" => _inspector.GetProcesses(
                    ReadInt(arguments, "limit", 5, 1, 5)),
                "diagnose" => await DiagnoseAsync(cancellationToken).ConfigureAwait(false),
                "get_collector_status" => await CollectorStatusAsync(cancellationToken).ConfigureAwait(false),
                _ => throw new JsonRpcException(-32602, "未知工具：" + name),
            };

            return Result(id, new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = result.ToJsonString(WireOptions),
                    },
                },
                ["isError"] = false,
            });
        }
        catch (JsonRpcException ex)
        {
            return Error(id, ex.Code, ex.Message);
        }
        catch (ToolException ex)
        {
            return Result(id, new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = ex.Message,
                    },
                },
                ["isError"] = true,
            });
        }
        catch (Exception ex)
        {
            _logger?.Error("工具执行失败：" + name, ex);
            return Result(id, new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = "工具执行失败：" + ex.Message,
                    },
                },
                ["isError"] = true,
            });
        }
    }

    private async Task<JsonObject> CollectorStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _inspector.GetCollectorStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ToolException ex)
        {
            JsonObject status;
            try
            {
                status = _inspector.GetHeaderStatus();
            }
            catch (ToolException)
            {
                throw new ToolException(ex.Message);
            }

            status["degraded"] = true;
            status["note"] = ex.Message + "；" + status["note"]?.GetValue<string>();
            return status;
        }
    }

    private async Task<JsonObject> DiagnoseAsync(CancellationToken cancellationToken)
    {
        JsonObject diagnostics = _inspector.GetDiagnostics();
        JsonObject? status = null;
        string? statusError = null;

        try
        {
            status = await _inspector.GetCollectorStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ToolException ex)
        {
            statusError = ex.Message;
            try
            {
                status = _inspector.GetHeaderStatus();
                status["degraded"] = true;
            }
            catch (ToolException)
            {
                status = null;
            }
        }

        IReadOnlyList<string> hints = BuildHints(diagnostics, status, statusError);
        var hintArray = new JsonArray();
        foreach (string hint in hints)
        {
            hintArray.Add(JsonValue.Create(hint));
        }

        return new JsonObject
        {
            ["diagnostics"] = diagnostics,
            ["collector"] = status,
            ["collectorError"] = statusError,
            ["hints"] = hintArray,
        };
    }

    private static IReadOnlyList<string> BuildHints(JsonObject diagnostics, JsonObject? status, string? statusError)
    {
        var hints = new List<string>();
        JsonObject telemetry = diagnostics["telemetry"] as JsonObject ?? new JsonObject();
        JsonObject sensors = diagnostics["sensors"] as JsonObject ?? new JsonObject();

        if (!ReadBool(telemetry, "open"))
        {
            hints.Add("遥测共享内存未打开：请先启动蓝天游戏助手采集进程（AzureGameAssistant.Collector）。");
        }
        else if (!ReadBool(telemetry, "headerValid"))
        {
            hints.Add("遥测头无效：采集进程可能正在启动或版本不匹配，请重启应用。");
        }
        else
        {
            double heartbeat = ReadDouble(telemetry, "heartbeatAgeMs");
            if (heartbeat > 3_000)
            {
                hints.Add($"采集进程心跳已停滞 {heartbeat:F0} ms，可能被挂起或无响应。");
            }
        }

        double frameIndex = ReadDouble(telemetry, "frameWriteIndex");
        if (frameIndex <= 0)
        {
            hints.Add("尚未采集到帧：请在设置中确认 PresentMon 路径，并在权限自检中检查 ETW/管理员权限。");
        }

        if (!ReadBool(sensors, "open"))
        {
            hints.Add("传感器共享内存未打开：温度类数据不可用，请以管理员身份运行并启用传感器边车。");
        }
        else
        {
            double sensorAge = ReadDouble(sensors, "heartbeatAgeMs");
            if (sensorAge > 10_000)
            {
                hints.Add($"传感器心跳已停滞 {sensorAge:F0} ms，温度数据可能过期。");
            }
        }

        if (status is not null && ReadBool(status, "paused"))
        {
            hints.Add("采集已暂停：调用控制命令或界面上的继续按钮恢复采集。");
        }

        if (statusError is not null)
        {
            hints.Add("控制管道不可用：" + statusError);
            if (statusError.Contains("拒绝访问", StringComparison.Ordinal))
            {
                hints.Add("以管理员身份启动 MCP 服务可获取运行时长和最近错误；快照、历史、帧统计等读取类工具不受影响。");
            }
        }

        if (hints.Count == 0)
        {
            hints.Add("采集链路正常。");
        }

        return hints;
    }

    private JsonObject BuildInitialize(JsonObject request)
    {
        string version = ToolCatalog.ProtocolVersion;
        if (request["params"] is JsonObject parameters &&
            parameters["protocolVersion"] is JsonValue requested &&
            requested.TryGetValue(out string? text) &&
            !string.IsNullOrWhiteSpace(text))
        {
            version = text;
        }

        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "azure-game-assistant",
                ["version"] = _version,
            },
            ["instructions"] =
                "蓝天游戏助手的只读硬件数据服务。所有工具只读取共享内存，不修改系统、不结束进程。" +
                "开始分析前建议先调用 diagnose 确认采集链路，再用 get_hardware_snapshot 和 get_history 获取数据。",
        };
    }

    private static int ReadInt(JsonObject? arguments, string name, int fallback, int minimum, int maximum)
    {
        if (arguments?[name] is JsonValue value && value.TryGetValue(out int parsed))
        {
            return Math.Clamp(parsed, minimum, maximum);
        }

        return fallback;
    }

    private static IReadOnlyList<string>? ReadStrings(JsonObject? arguments, string name)
    {
        if (arguments?[name] is not JsonArray array)
        {
            return null;
        }

        var values = new List<string>(array.Count);
        foreach (JsonNode? item in array)
        {
            if (item is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
            {
                values.Add(text);
            }
        }

        return values.Count > 0 ? values : null;
    }

    private static bool ReadBool(JsonObject source, string name) =>
        source[name]?.GetValueKind() == JsonValueKind.True;

    private static double ReadDouble(JsonObject source, string name)
    {
        if (source[name] is not JsonValue value)
        {
            return 0;
        }

        if (value.TryGetValue(out double parsed))
        {
            return parsed;
        }

        return value.TryGetValue(out long integral) ? integral : 0;
    }

    private static JsonObject Result(JsonNode? id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result,
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        },
    };

    private sealed class JsonRpcException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}
