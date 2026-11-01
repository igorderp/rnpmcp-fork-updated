using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;
using RengaMcp.Adapters;
using RengaMcp.Infrastructure;
using RengaMcp.Tools;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    // stdout is reserved for the MCP protocol.
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.AddSingleton<IRengaAdapter>(services =>
{
    var loggerFactory = services.GetRequiredService<ILoggerFactory>();
    var mode = Environment.GetEnvironmentVariable("RENGA_MCP_MODE");

    return string.Equals(mode, "mock", StringComparison.OrdinalIgnoreCase)
        ? new MockRengaAdapter()
        : new DynamicComRengaAdapter(loggerFactory.CreateLogger<DynamicComRengaAdapter>());
});

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "renga-mcp", Version = "0.4.1" };
        options.ServerInstructions =
            "Access a running Renga BIM instance. Auto-connects to the sole running instance; when multiple instances are available choose processId using renga_connect. Explicit disconnect disables auto-connect until renga_connect. " +
            "Write tools preview and roll back by default; explicit preview=false is required to commit. " +
            "Read renga_get_geometry before architectural edits: baselines are object-local, creation placement is global. " +
            "renga_get_screenshot captures the model viewport through Renga, not desktop UI coordinates. " +
            "Native copy/paste, level elevation editing and native object moves use renga_ui_snapshot/key/click/type_text. UI preview only plans; it cannot roll back inputs. Use fresh UI snapshot IDs, inspect every result and verify completed edits by re-reading objects/levels. " +
            "Inspect engineering ports and compatible styles before creating MEP connections. " +
            "Never infer regulatory compliance or design quality from successful API calls alone. " +
            "Results may be paginated or truncated.";
    })
    .WithStdioServerTransport()
    .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
    {
        try { return await next(context, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (ComFailures.IsDisconnected(error) || ComFailures.Unwrap(error) is ArgumentException or InvalidOperationException
            or NotSupportedException or KeyNotFoundException or FormatException
            or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception)
        {
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = ComFailures.Describe(error) }] };
        }
    }))
    .WithTools<RengaTools>();

await builder.Build().RunAsync().ConfigureAwait(false);
