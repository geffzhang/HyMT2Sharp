using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Extensions.Apps;
using Sdcb.HyMT2Sharp.McpServer;

// MCP stdio: stdout carries JSON-RPC only; all diagnostics go to stderr.
Console.OutputEncoding = System.Text.Encoding.UTF8;

string modelPath = GetArg(args, "--model", "-m")
    ?? Environment.GetEnvironmentVariable("HYMT2_MODEL")
    ?? "";
int threads = GetInt(args, 0, "--threads", "-t");
int maxTokens = GetInt(args, 512, "--max-tokens");
bool httpMode = HasArg(args, "--http");
int port = GetInt(args, 17890, "--port", "-p");

if (!string.IsNullOrEmpty(modelPath))
    Console.Error.WriteLine($"HyMT2Sharp.McpServer  model={modelPath}");
else
    Console.Error.WriteLine("HyMT2Sharp.McpServer  model=<unset> (pass --model or set HYMT2_MODEL; will fail on first translate)");

if (httpMode)
{
    // Streamable HTTP transport: same tools/resources, reachable over the network.
    WebApplicationBuilder builder = WebApplication.CreateBuilder([]);
    builder.Logging.AddConsole(consoleLogOptions =>
    {
        consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
    });
    builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
    builder.Services.AddSingleton(new TranslationService(modelPath, threads, maxTokens));
    builder.Services
        .AddMcpServer(options =>
        {
            options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
            {
                Name = "hymt2",
                Version = "1.0.0",
            };
        })
        .WithHttpTransport()
        .WithTools<TranslationTools>()
        .WithResources<TranslationResources>()
        .WithMcpApps();

    WebApplication app = builder.Build();
    app.MapMcp("/mcp");

    Console.Error.WriteLine($"HyMT2Sharp.McpServer  listening on http://127.0.0.1:{port}/mcp (streamable HTTP)");
    await app.RunAsync();
}
else
{
    HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
    builder.Logging.AddConsole(consoleLogOptions =>
    {
        consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
    });
    builder.Services.AddSingleton(new TranslationService(modelPath, threads, maxTokens));
    builder.Services
        .AddMcpServer(options =>
        {
            options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
            {
                Name = "hymt2",
                Version = "1.0.0",
            };
        })
        .WithStdioServerTransport()
        .WithTools<TranslationTools>()
        .WithResources<TranslationResources>()
        .WithMcpApps();

    await builder.Build().RunAsync();
}

static bool HasArg(string[] args, params string[] names)
    => args.Any(a => names.Contains(a, StringComparer.OrdinalIgnoreCase));

static string? GetArg(string[] args, params string[] names)
{
    for (int i = 0; i < args.Length; i++)
    {
        if (names.Contains(args[i], StringComparer.OrdinalIgnoreCase) && i + 1 < args.Length)
            return args[i + 1];
    }

    return null;
}

static int GetInt(string[] args, int fallback, params string[] names)
    => int.TryParse(GetArg(args, names), out int value) ? value : fallback;
