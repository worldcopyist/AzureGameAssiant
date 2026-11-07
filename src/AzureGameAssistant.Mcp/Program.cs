using System.Text;
using AzureGameAssistant.Core;
using AzureGameAssistant.Core.Logging;

namespace AzureGameAssistant.Mcp;

/// <summary>
/// Entry point for the MCP stdio server. The process never writes anything but
/// JSON-RPC frames to stdout; human-readable diagnostics go to the log file.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        FileLogger? logger = null;
        try
        {
            AppPaths.EnsureDirectories();
            logger = new FileLogger("mcp");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("无法初始化日志：" + ex.Message);
        }

        using (logger)
        {
            logger?.Info("MCP 硬件数据服务启动");

            using var inspector = new HardwareInspector();
            var server = new McpServer(inspector, logger);
            using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
            {
                AutoFlush = false,
                NewLine = "\n",
            };

            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cancellation.Cancel();
            };

            try
            {
                await server.RunAsync(input, output, cancellation.Token).ConfigureAwait(false);
                return 0;
            }
            catch (Exception ex)
            {
                logger?.Error("MCP 服务异常退出", ex);
                Console.Error.WriteLine("MCP 服务异常退出：" + ex.Message);
                return 1;
            }
        }
    }
}
