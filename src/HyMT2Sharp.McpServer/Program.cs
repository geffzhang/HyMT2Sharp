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

if (!string.IsNullOrEmpty(modelPath))
    Console.Error.WriteLine($"HyMT2Sharp.McpServer  model={modelPath}");
else
    Console.Error.WriteLine("HyMT2Sharp.McpServer  model=<unset> (pass --model or set HYMT2_MODEL; will fail on first translate)");

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
