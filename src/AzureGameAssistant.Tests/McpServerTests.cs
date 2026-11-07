using System.Text.Json.Nodes;
using AzureGameAssistant.Mcp;

namespace AzureGameAssistant.Tests;

public sealed class McpServerTests
{
    private static async Task<JsonNode?> HandleAsync(string request)
    {
        using var inspector = new HardwareInspector();
        var server = new McpServer(inspector);
        return await server.HandleAsync(request, CancellationToken.None);
    }

    [Fact]
    public async Task InitializeEchoesRequestedProtocolVersion()
    {
        JsonNode? response = await HandleAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""");

        Assert.Equal("2.0", response?["jsonrpc"]?.GetValue<string>());
        Assert.Equal(1, response?["id"]?.GetValue<int>());
        Assert.Equal("2025-06-18", response?["result"]?["protocolVersion"]?.GetValue<string>());
        Assert.Equal("azure-game-assistant", response?["result"]?["serverInfo"]?["name"]?.GetValue<string>());
        Assert.False(response?["result"]?["capabilities"]?["tools"]?["listChanged"]?.GetValue<bool>());
    }

    [Fact]
    public async Task InitializeWithoutParamsFallsBackToDefaultProtocol()
    {
        JsonNode? response = await HandleAsync("""{"jsonrpc":"2.0","id":2,"method":"initialize"}""");

        Assert.Equal(
            ToolCatalog.ProtocolVersion,
            response?["result"]?["protocolVersion"]?.GetValue<string>());
    }

    [Fact]
    public async Task ToolsListExposesCatalogNames()
    {
        JsonNode? response = await HandleAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/list"}""");

        JsonArray? tools = response?["result"]?["tools"] as JsonArray;
        Assert.NotNull(tools);
        Assert.Equal(ToolCatalog.Names.Count, tools!.Count);

        string[] names = tools
            .Select(tool => tool?["name"]?.GetValue<string>() ?? string.Empty)
            .ToArray();
        Assert.Equal(ToolCatalog.Names, names);

        foreach (JsonNode? tool in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool?["description"]?.GetValue<string>()));
        }
    }

    [Fact]
    public async Task NotificationsProduceNoResponse()
    {
        Assert.Null(await HandleAsync(
            """{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        Assert.Null(await HandleAsync(
            """{"jsonrpc":"2.0","method":"tools/list"}"""));
        Assert.Null(await HandleAsync(
            """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":1}}"""));
    }

    [Fact]
    public async Task PingReturnsEmptyResult()
    {
        JsonNode? response = await HandleAsync("""{"jsonrpc":"2.0","id":4,"method":"ping"}""");

        Assert.NotNull(response?["result"] as JsonObject);
        Assert.Empty((JsonObject)response!["result"]!);
    }

    [Fact]
    public async Task UnknownMethodReturnsMethodNotFound()
    {
        JsonNode? response = await HandleAsync("""{"jsonrpc":"2.0","id":5,"method":"resources/list"}""");

        Assert.Equal(-32601, response?["error"]?["code"]?.GetValue<int>());
        Assert.Equal(5, response?["id"]?.GetValue<int>());
    }

    [Fact]
    public async Task InvalidJsonReturnsParseError()
    {
        JsonNode? response = await HandleAsync("{ not json");

        Assert.Equal(-32700, response?["error"]?["code"]?.GetValue<int>());
        Assert.Null(response?["id"]);
    }

    [Fact]
    public async Task MissingMethodReturnsInvalidRequest()
    {
        JsonNode? response = await HandleAsync("""{"jsonrpc":"2.0","id":6}""");

        Assert.Equal(-32600, response?["error"]?["code"]?.GetValue<int>());
    }

    [Fact]
    public async Task UnknownToolReturnsInvalidParams()
    {
        JsonNode? response = await HandleAsync(
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"delete_everything"}}""");

        Assert.Equal(-32602, response?["error"]?["code"]?.GetValue<int>());
    }

    [Fact]
    public async Task MissingToolNameReturnsInvalidParams()
    {
        JsonNode? response = await HandleAsync(
            """{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"arguments":{}}}""");

        Assert.Equal(-32602, response?["error"]?["code"]?.GetValue<int>());
    }

    [Fact]
    public async Task ToolCallReturnsContentEnvelopeEvenWithoutCollector()
    {
        JsonNode? response = await HandleAsync(
            """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"get_hardware_snapshot"}}""");

        JsonArray? content = response?["result"]?["content"] as JsonArray;
        Assert.NotNull(content);
        Assert.Equal("text", content![0]?["type"]?.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(content[0]?["text"]?.GetValue<string>()));
        Assert.NotNull(response?["result"]?["isError"]);
    }

    [Fact]
    public void CatalogSchemasBoundEveryArgument()
    {
        JsonArray tools = ToolCatalog.Describe();
        Assert.Equal(ToolCatalog.Names.Count, tools.Count);

        foreach (JsonNode? tool in tools)
        {
            JsonObject schema = tool?["inputSchema"] as JsonObject ?? throw new InvalidOperationException();
            Assert.Equal("object", schema["type"]?.GetValue<string>());
            Assert.False(schema["additionalProperties"]?.GetValue<bool>());
            Assert.NotNull(schema["required"] as JsonArray);
        }

        JsonObject history = SchemaOf(tools, "get_history");
        AssertBounds(history["properties"]?["seconds"], 1, 3600);
        AssertBounds(history["properties"]?["max_points"], 2, 900);
        Assert.Equal(6, history["properties"]?["metrics"]?["maxItems"]?.GetValue<int>());

        AssertBounds(SchemaOf(tools, "get_frame_stats")["properties"]?["seconds"], 1, 60);
        AssertBounds(SchemaOf(tools, "list_processes")["properties"]?["limit"], 1, 5);

        JsonObject snapshot = SchemaOf(tools, "get_hardware_snapshot");
        Assert.Empty((JsonObject)snapshot["properties"]!);
    }

    private static JsonObject SchemaOf(JsonArray tools, string name) =>
        tools
            .First(tool => tool?["name"]?.GetValue<string>() == name)?["inputSchema"] as JsonObject
        ?? throw new InvalidOperationException($"缺少工具 {name}");

    private static void AssertBounds(JsonNode? property, int minimum, int maximum)
    {
        JsonObject node = property as JsonObject ?? throw new InvalidOperationException();
        Assert.Equal("integer", node["type"]?.GetValue<string>());
        Assert.Equal(minimum, node["minimum"]?.GetValue<int>());
        Assert.Equal(maximum, node["maximum"]?.GetValue<int>());
        int fallback = node["default"]?.GetValue<int>() ?? -1;
        Assert.InRange(fallback, minimum, maximum);
    }
}
