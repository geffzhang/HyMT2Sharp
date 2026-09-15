# HyMT2Sharp MCP Apps 支持方案

> 状态：M1–M3 全部已实施（stdio + `translate` 工具 + MCP Apps 翻译工作台 UI + streamable HTTP + 单文件发布，构建与协议冒烟通过）；M4 可选
> 目标仓库：geffzhang/HyMT2Sharp（上游 sdcb/HyMT2Sharp）
> 日期：2026-09-15

## 1. 背景与目标

HyMT2Sharp 目前对外提供三种形态：

| 形态 | 项目 | 说明 |
|------|------|------|
| NuGet 库 | `Sdcb.HyMT2Sharp.Model` | 进程内调用 `HunyuanDenseModel` |
| CLI | `HyMT2Sharp.Cli` | 命令行翻译/对话 |
| HTTP 服务 | `HyMT2Sharp.Server` | OpenAI 兼容 `/v1/chat/completions` |

**缺口**：没有一个形态能直接接入 MCP（Model Context Protocol）生态。Claude Desktop、VS Code Copilot、WorkBuddy 等宿主无法原生发现并调用本地 Hy-MT2 翻译能力；更无法获得**交互式翻译 UI**。

**MCP Apps** 是 MCP 的 UI 扩展（spec 草案 2025-06-18）：工具可以声明一个 HTML UI 资源，宿主将其渲染在 iframe 中，UI 通过 `postMessage` JSON-RPC 桥回调宿主的 `tools/call` / `resources/read`。C# SDK 已提供官方支持（`ModelContextProtocol.Extensions.Apps`，实验性）。

### 目标

1. **G1（基础）**：HyMT2Sharp 作为本地 MCP Server 暴露 `translate` 工具，任何 MCP 宿主可直接调用本地翻译，零外部依赖、离线可用。
2. **G2（亮点）**：通过 MCP Apps 提供一个"翻译工作台"交互 UI（双语对照、语言选择、耗时统计），在支持 MCP Apps 的宿主内嵌渲染。
3. **G3（工程）**：不侵入现有 Model/Server 代码，新增独立项目承载；stdio / HTTP 双传输；可回填上游。

### 非目标（本期不做）

- GPU/非 x86 推理路径
- 翻译记忆库、术语库持久化（Hy-MT2 的 glossary/style 指令翻译仅做 prompt 透传，不做管理界面）
- 多模型热切换、多实例并行推理

## 2. 现有资产盘点（可直接复用）

| 资产 | 位置 | 复用方式 |
|------|------|----------|
| 推理引擎 | `HunyuanDenseModel`（加载/分词/KV cache/`Forward`） | 直接引用 `Sdcb.HyMT2Sharp.Model` |
| Chat 模板 | `ChatTemplate.RenderHunyuanDense` | 翻译 prompt 渲染 |
| 采样循环 | `ChatCompletionService.Generate`（greedy + 流式 delta + timings） | 抽取为共享 `TranslationService` 的参考实现 |
| CLI 参数解析 | `Args.Get` / `GetInt` 模式 | 沿用同风格 |
| 环境变量约定 | `HYMT2_MODEL` | 沿用 |

关键约束：**`HunyuanDenseModel` 非线程安全，单实例必须串行调用**（`ChatCompletionService` 已用 `SemaphoreSlim(1,1)` 串行化，新服务沿用同一模式）。

## 3. 技术选型

MCP C# SDK（官方，与 Microsoft 协作维护）：

| 包 | 用途 | 备注 |
|----|------|------|
| `ModelContextProtocol` | stdio Server + DI + 特性化 tool/resource 发现 | 稳定 |
| `ModelContextProtocol.AspNetCore` | Streamable HTTP 传输（`WithHttpTransport` + `MapMcp`） | 稳定 |
| `ModelContextProtocol.Extensions.Apps` | `[McpAppUi]`、`WithMcpApps()`、`McpApps.HtmlMimeType` | **实验性，需 `NoWarn MCPEXP003`** |

- 目标框架 `net10.0`：与仓库 `Directory.Build.props` 一致，SDK 同样以 net10.0 为主目标，无冲突。
- 许可证 Apache-2.0，与仓库兼容。
- 参考实现：SDK 官方示例 `samples/WeatherAppServer`（MCP Apps 的标准写法，本方案大量借鉴其结构）。

**不确定性标注**：SDK 当前处于 2.0.x 线（2026-08 仍在重构 experimental 面），`Extensions.Apps` 的 API 形态可能变动；方案将 MCP Apps 相关代码隔离在独立类文件中，升级成本可控。落地前需以当时 NuGet 实际版本做一次构建验证。

## 4. 总体架构

新增项目 `src/HyMT2Sharp.McpServer`（控制台宿主，双模式）：

```
┌─────────────────────────────────────────────────────────────────┐
│  MCP 宿主（Claude Desktop / VS Code Copilot / WorkBuddy ...）     │
│                                                                 │
│  ┌───────────────┐   ┌────────────────────────────────────┐     │
│  │ LLM 决策层     │   │ MCP Apps iframe：翻译工作台 UI       │     │
│  │ tools/call     │   │ ui://hymt2/translate (HTML)        │     │
│  └──────┬────────┘   │  postMessage JSON-RPC 桥 ↕ 宿主      │     │
│         │            └───────────────┬────────────────────┘     │
└─────────┼────────────────────────────┼──────────────────────────┘
          │ MCP (JSON-RPC)
──────────┼────────────────────────────┼────────────────────────────
          ▼                            ▼
┌─────────────────────────────────────────────────────────────────┐
│  HyMT2Sharp.McpServer                                            │
│                                                                 │
│  传输层：stdio（默认） 或 Streamable HTTP --http 模式 /mcp          │
│  ├─ Tools:  translate / translate_ui                             │
│  ├─ Resources: ui://hymt2/translate, data://hymt2/languages     │
│  └─ TranslationService（SemaphoreSlim 串行化，懒加载模型）           │
└──────────────────────────┬──────────────────────────────────────┘
                           ▼
              Sdcb.HyMT2Sharp.Model（HunyuanDenseModel, AVX2）
                           ▼
              Hy-MT2-1.8B GGUF（Q4_K_M / Q2_0C / STQ1_0）
```

**双传输设计理由**：

- **stdio（默认）**：本地 MCP 宿主的标准拉起方式（Claude Desktop / WorkBuddy mcp.json / VS Code），宿主管理进程生命周期，无需占用端口。
- **HTTP（`--http`）**：调试（MCP Inspector）、远程/容器化部署、多客户端共享一个模型实例。沿用 `HyMT2Sharp.Server` 的参数风格（`--urls`）。

**UI 通信设计的关键结论**：MCP Apps UI 的所有交互（调工具、读资源）都走 `postMessage` → 宿主 → MCP 会话，**不直连后端 HTTP**。因此：

1. UI 在 stdio 模式下同样可交互（前提是宿主实现了 Apps 桥）；
2. UI 页面 CSP 无需 `connectDomains`，完全离线可用——这对 HyMT2"本地、离线"的定位是天然契合的卖点。

## 5. MCP Server 详细设计

### 5.1 启动参数

```
dotnet run --project src/HyMT2Sharp.McpServer -c Release -- \
  --model "D:\_\model\Hy-MT2-1.8B-Q4_K_M.gguf" \
  [--threads 0] [--max-tokens 512] [--http] [--urls http://127.0.0.1:8081]
```

- `--model` 缺省读 `HYMT2_MODEL` 环境变量（沿用 Server 约定）。
- `--http` 切换 Streamable HTTP 模式（默认 stateless，`HttpServerSessionMode.Stateless`）。
- stdio 模式下**所有日志必须写 stderr**（stdout 被 JSON-RPC 占用）。

### 5.2 TranslationService（模型门面）

新类 `TranslationService`（放 McpServer 项目内，与 `ChatCompletionService` 同构）：

```csharp
public sealed class TranslationService : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HunyuanDenseModel? _model;          // 懒加载
    private readonly string _modelPath;
    private readonly int _threads;

    public async Task<TranslationResult> TranslateAsync(
        string text, HyLanguage target, HyLanguage? source,
        int maxTokens, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            HunyuanDenseModel model = GetOrCreateModel();   // 首次调用才加载 GGUF
            string prompt = TranslationPrompts.Render(text, target, source);
            // Encode → AlignPrompt → Forward → greedy decode 循环
            // （复刻 ChatCompletionService.Generate 的核心循环）
        }
        finally { _gate.Release(); }
    }
}
```

**懒加载理由**：宿主启动时会立刻 `initialize` + `list_tools`，若在 DI 单例构造函数里同步加载 GGUF（秒级），拖慢宿主启动且无收益。首次 `tools/call` 才加载，并在此期间返回进度语义（工具描述中注明"首次调用含模型加载耗时"）。

**失败恢复**：沿用 Server 的模式——生成中途异常/取消时 `ResetCache()`，防止 KV cache 处于脏状态。

### 5.3 语言枚举与 prompt 模板

`HyLanguage` 枚举（强类型参数，SDK 会把 enum 变成 inputSchema 的 `enum` 值，UI 端可直接从 `ui/initialize` 的 `hostContext.toolInfo.tool.inputSchema` 提取，这是 WeatherAppServer 的做法）：

```csharp
[JsonConverter(typeof(JsonStringEnumConverter<HyLanguage>))]
public enum HyLanguage
{
    zh, en, ja, ko, de, fr, es, pt, it, ru, ar, th, vi, id, ms, tl, hi,
    zh_Hant, pl, cs, nl, uk, he, fa, tr,
    // 民汉/方言（Hy-MT2 家族支持，1.8B 效果待验证，先全量暴露、文档标注）
    yue, bo, kk, mn, ug,
}
```

语言 → prompt 英文名的映射表（`zh → Chinese`、`zh_Hant → Traditional Chinese` …）。

**prompt 模板（对齐模型官方用法）**：

```
Translate the following segment into {TargetEnglishName}, without additional explanation：{text}
```

- 与 README / 文章中验证过的唯一 prompt 形态保持一致，不自行发明变体。
- `sourceLanguage` 仅写入返回元数据（Hy-MT2 官方 prompt 不含源语言槽位；是否加入 "from X" 变体需实测，默认不加）。
- `maxTokens` 默认 512（翻译输出通常短于对话）。

**不确定性标注**：Hy-MT2 家族宣称 33 语言 + 5 民汉，但 1.8B 对小语种的实际质量未逐语言验证。方案先全量枚举，`data://hymt2/languages` 资源中按「核心 / 扩展」分组标注，后续按实测裁剪。

### 5.4 Tools

```csharp
[McpServerToolType]
public sealed class TranslationTools
{
    [McpServerTool(Name = "translate")]
    [Description(
        "Translate text with the local Hy-MT2 model (offline, no network). " +
        "33+ languages incl. Chinese/English/Japanese/Korean... " +
        "First call includes model loading time.")]
    public static async Task<CallToolResult> Translate(
        TranslationService svc,
        [Description("Text to translate")] string text,
        [Description("Target language")] HyLanguage targetLanguage,
        [Description("Source language hint (metadata only, auto-detected by model)")]
        HyLanguage? sourceLanguage = null,
        CancellationToken ct = default)
    {
        TranslationResult r = await svc.TranslateAsync(...);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = r.TranslatedText }],
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                text = r.SourceText,
                sourceLanguage = r.SourceLanguage,
                targetLanguage = r.TargetLanguage,
                translatedText = r.TranslatedText,
                usage = new { promptTokens = r.PromptTokens, completionTokens = r.CompletionTokens },
                timings = new { promptMs = r.PromptMs, decodeMs = r.DecodeMs,
                                tokensPerSecond = r.TokensPerSecond },
            }),
        };
    }

    [McpServerTool(Name = "translate_ui")]
    [McpAppUi(ResourceUri = "ui://hymt2/translate")]
    [Description("Open the interactive translation workbench UI.")]
    public static string TranslateUi() => "Translation workbench opened.";
}
```

设计要点：

- **`translate` 是唯一业务入口**，`StructuredContent` 是 UI 渲染的数据契约；`Content` 文本让不支持 Apps 的宿主也能拿到纯文本结果（渐进增强）。
- `translate_ui` 可带可选参数 `text` / `targetLanguage`：宿主 LLM 调用它时，UI 通过 `ui/notifications/tool-input` 收到参数并自动预填。
- 输入校验：文本 tokenize 后超过 `Config.ContextLength` 抛 `McpException`（带明确文案与建议），不静默截断。

### 5.5 Resources

```csharp
[McpServerResourceType]
public sealed class TranslationResources
{
    private static readonly string UiDir =
        Path.Combine(AppContext.BaseDirectory, "ui");

    [McpServerResource(UriTemplate = "ui://hymt2/translate",
        Name = "translate-workbench", MimeType = McpApps.HtmlMimeType)]
    [McpMeta("ui", """{"prefersBorder":true}""")]   // 无 connectDomains：全离线
    [Description("Interactive translation workbench UI")]
    public static string GetTranslateUi()
        => File.ReadAllText(Path.Combine(UiDir, "translate.html"));

    [McpServerResource(UriTemplate = "data://hymt2/languages",
        Name = "hymt2-languages", MimeType = "application/json")]
    [Description("Languages supported by Hy-MT2, grouped core/extended")]
    public static string GetLanguages() => /* JSON：核心/扩展两组 */;
}
```

### 5.6 Program.cs 骨架（双模式）

```csharp
string modelPath = GetArg(args, "--model", "-m")
    ?? Environment.GetEnvironmentVariable("HYMT2_MODEL") ?? "";
int threads = GetInt(args, 0, "--threads", "-t");
int maxTokens = GetInt(args, 512, "--max-tokens");
bool http = HasArg(args, "--http");

if (http)
{
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
    builder.WebHost.UseUrls(GetArg(args, "--urls") ?? "http://127.0.0.1:8081");
    builder.Services.AddSingleton(new TranslationService(modelPath, threads, maxTokens));
    builder.Services.AddMcpServer(o =>
    {
        o.ServerInfo = new Implementation { Name = "hymt2", Version = "1.0.0" };
        o.Capabilities = new ServerCapabilities
        {
            Tools = new ToolsCapability(),
            Resources = new ResourcesCapability(),
        };
    })
    .WithHttpTransport()          // stateless（默认）
    .WithTools<TranslationTools>()
    .WithResources<TranslationResources>()
    .WithMcpApps();               // 处理 [McpAppUi]

    WebApplication app = builder.Build();
    app.MapMcp("/mcp");
    app.Run();
}
else
{
    HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Services.AddSingleton(new TranslationService(modelPath, threads, maxTokens));
    builder.Services.AddMcpServer()
        .WithStdioServerTransport()
        .WithTools<TranslationTools>()
        .WithResources<TranslationResources>()
        .WithMcpApps();
    await builder.Build().RunAsync();
}
```

csproj 要点：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- Directory.Build.props 统一 net10.0 / Sdcb.* 命名 -->
    <NoWarn>$(NoWarn);MCPEXP003</NoWarn>   <!-- MCP Apps 实验性 API -->
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="ModelContextProtocol" Version="2.*" />
    <PackageReference Include="ModelContextProtocol.AspNetCore" Version="2.*" />
    <PackageReference Include="ModelContextProtocol.Extensions.Apps" Version="2.*" />
    <ProjectReference Include="..\HyMT2Sharp.Model\HyMT2Sharp.Model.csproj" />
  </ItemGroup>
  <ItemGroup>
    <Content Include="ui\*.html" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

## 6. 翻译工作台 UI（`ui/translate.html`）

交互模式完全对齐 WeatherAppServer 官方示例的桥协议：

| 方向 | 消息 | 用途 |
|------|------|------|
| UI → 宿主 | `ui/initialize`（request） | 握手；响应的 `hostContext.toolInfo.tool.inputSchema` 可直接拿到 `HyLanguage` 枚举，填充语言下拉框 |
| UI → 宿主 | `tools/call { name: "translate", arguments: {...} }` | 翻译按钮触发；结果取 `structuredContent` |
| UI → 宿主 | `resources/read { uri: "data://hymt2/languages" }` | inputSchema 缺失时的语言列表兜底 |
| 宿主 → UI | `ui/notifications/tool-input` | LLM 调用 `translate_ui(text, targetLanguage)` 时，UI 自动预填文本与目标语言并触发翻译 |
| 宿主 → UI | `ui/notifications/tool-result` | 展示 LLM 直接调 `translate` 的结果 |

界面（单文件 HTML，零外部依赖，跟随 `prefers-color-scheme` 明暗）：

```
┌─ Hy-MT2 翻译工作台 ───────────── tokens/s: 43.1 ─┐
│ [目标语言 ▼ zh 中文]  [源语言(自动) ▼]  [翻译]     │
│ ┌───────────────┐  ┌─────────────────────────┐   │
│ │ 原文输入       │→ │ 译文输出                 │   │
│ │ (textarea)    │  │ (structuredContent 渲染) │   │
│ └───────────────┘  └─────────────────────────┘   │
│ 状态栏：prompt 512 tok / decode 96 tok / 2.2 s     │
└───────────────────────────────────────────────────┘
```

- 语言选择优先从 `ui/initialize` 的 inputSchema enum 取，兜底走 `resources/read`；
- 翻译进行中禁用按钮 + spinner（单实例串行，若与 LLM 并发调用会排队，状态栏提示"排队中"）；
- 纯静态 HTML + 原生 JS，`escapeHtml` 处理所有动态文本（模型输出不可信，防注入）。

## 7. 宿主接入配置示例

**WorkBuddy（`~/.workbuddy/mcp.json`）**：

```json
{
  "mcpServers": {
    "hymt2": {
      "command": "dotnet",
      "args": [
        "run", "--project", "C:\\Users\\geffzhang\\WorkBuddy\\Hymt2Sharp\\src\\HyMT2Sharp.McpServer",
        "-c", "Release", "--",
        "--model", "D:\\_\\model\\Hy-MT2-1.8B-Q4_K_M.gguf"
      ]
    }
  }
}
```

（更优：`dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true` 产出单文件 exe，`command` 直接指 exe，避免每次 `dotnet run` 编译开销。）

**Claude Desktop / VS Code `mcp.json`**：同构的 `command` + `args` 配置。

**调试**：`npx @modelcontextprotocol/inspector dotnet run --project src/HyMT2Sharp.McpServer -- --model ...`（stdio 直连 Inspector）。

## 8. 实施计划

| 里程碑 | 内容 | 验收 | 预估 |
|--------|------|------|------|
| **M1 ✅ 已完成（2026-09-15）** | `HyMT2Sharp.McpServer` 项目 + stdio 传输 + `translate` 工具 + `TranslationService` | Release 构建 0 警告 0 错误；stdio 冒烟：`initialize` 握手、`tools/list`（38 语言字符串枚举 schema）、`tools/call` 错误路径返回结构化 `isError` 响应且错误信息透传；语言枚举经 `JsonStringEnumConverter` 以字符串暴露，可直接被后续 MCP Apps UI 的 inputSchema 消费 | 0.5–1 天 |
| **M2 ✅ 已完成（2026-09-15）** | MCP Apps：`translate_ui` + 两个 resources + `ui/translate.html` 工作台 | 协议冒烟全通：`initialize` 响应含 `extensions:{"io.modelcontextprotocol/ui":{}}`；`translate_ui`/`translate` 均带 `_meta.ui.resourceUri`；`resources/list` 返回 `text/html;profile=mcp-app` 资源（含 `prefersBorder`）；`resources/read` 正常返回 HTML 与分组语言 JSON。宿主内真实渲染需 MCP Apps 宿主，按渐进增强设计不阻塞交付 | 1 天 |
| **M3 ✅ 已完成（2026-09-15）** | `--http` 模式、README/文档、单文件发布脚本 | HTTP 冒烟全通：`initialize`（SSE 响应、声明 `io.modelcontextprotocol/ui` 扩展能力）、`tools/list`、`resources/read`、真实翻译（en→zh，57.6 tok/s）；发布脚本产出单文件 exe + `ui/translate.html`，发布版 stdio 冒烟通过 | 0.5 天 |
| M4（可选增强） | 进度通知（`IProgress<ProgressNotificationValue>` 上报 decode 进度）、长文分块翻译、glossary/style prompt 透传 | 按需 | 另议 |

M1 实施补充说明：

- SDK 实际锁定版本 `ModelContextProtocol 2.2.0` + `Microsoft.Extensions.Hosting 10.0.0`（方案中"2.*"已收敛到具体版本）。
- `McpServer` 项目未启用 `Extensions.Apps`（M2 再引入，届时加 `NoWarn MCPEXP003`），M1 保持全稳定 API。
- 端到端验收已通过（Hy-MT2-1.8B-1.25Bit，440MB）：en→zh 与 zh→en 双向翻译正常、正常停止（32 completion tokens）、暖路径 decode 约 63–75 tok/s、懒加载验证通过（首次调用 `model_loaded_this_call: true`，后续 `false`）。
- **实施中发现并修复**：翻译 prompt 必须经 `ChatTemplate.RenderHunyuanDense([new ChatMessage("user", prompt)])` 包装（BOS + user turn）。直接编码裸 prompt 会导致模型不输出 stop token、decode 撞满 maxTokens 且译文为空。
- 可预期异常（参数/模型文件/未配置路径）统一转 `McpException`，错误信息完整透传给客户端；未处理异常会被 SDK 屏蔽为泛化消息。
- 新增文件：`src/HyMT2Sharp.McpServer/{HyMT2Sharp.McpServer.csproj, Program.cs, TranslationService.cs, TranslationTools.cs, HyLanguage.cs}`，已加入 `HyMT2Sharp.slnx`。

M2 实施补充说明：

- `ModelContextProtocol.Extensions.Apps 2.2.0` 引入（`NoWarn MCPEXP003`）；实测 2.2.0 稳定版 API 与官方 `WeatherAppServer` 样例一致（`McpApps.HtmlMimeType`、`[McpAppUi]`、`[McpMeta]`、`WithMcpApps()`、`WithResources<T>()`）。
- `translate` 与 `translate_ui` 两个工具均挂 `[McpAppUi(ResourceUri = "ui://hymt2/translate")]`：前者让 LLM 直接翻译时宿主可把 `structuredContent` 推送到已打开的 UI；后者支持预填参数（`text` / `targetLanguage`，经 `ui/notifications/tool-input` 到达 UI）。
- UI 语言列表加载策略：优先 `tools/list` → `translate` 的 `inputSchema.properties.targetLanguage.enum`（枚举经 `JsonStringEnumConverter` 以字符串暴露），显示名兜底走 `resources/read data://hymt2/languages`（核心/扩展/方言三组 optgroup）。
- `ui/translate.html` 为零外部依赖单文件（原生 JS + `postMessage` JSON-RPC 桥），暗亮色跟随 `prefers-color-scheme`，`textContent` 渲染译文防注入。
- stdio 模式下 UI 同样可交互（桥经宿主转发，UI 不直连任何 HTTP）。

M3 实施补充说明：

- `ModelContextProtocol.AspNetCore 2.2.0` 引入（`FrameworkReference Microsoft.AspNetCore.App`）；`Microsoft.Extensions.Hosting` 显式引用移除（经 AspNetCore 包传递引入，显式引用触发 NU1510）。
- `MapMcp()` **必须显式传路由 pattern**：`app.MapMcp("/mcp")`。无参调用编译通过但端点未注册（POST /mcp 返回 404）——这是本里程碑发现的 SDK 行为坑。
- HTTP 模式仅绑定 `127.0.0.1`（本地使用定位；对外暴露需自行加反向代理与鉴权）。stdio 与 http 双模式共用同一套 tools/resources/Apps 注册，`Program.cs` 按 `--http` 分支。
- 响应为 SSE 流（`event: message` + `data:` 行），符合 streamable HTTP 规范；本实现无显式会话（未返回 `Mcp-Session-Id`），SDK 按无状态处理可用。
- `scripts/publish-mcpserver.ps1`：`PublishSingleFile` + `IncludeNativeLibrariesForSelfExtract`，`ui/translate.html` 以 Content 跟随产物（资源按 `AppContext.BaseDirectory` 解析，单文件下指向 exe 旁目录）；框架依赖与 `-SelfContained`（需 `-RID`）两种模式。
- `.gitignore` 增加 `publish/`。

依赖顺序：M1 → M2 → M3 严格串行；M4 独立。

## 9. 测试与验收

1. **单元**：`TranslationService` 对空文本、超长文本（> ContextLength）、取消令牌的行为；语言枚举与 prompt 渲染快照。
2. **协议**：Inspector 连 stdio 与 HTTP 两种模式，验证 `initialize` / `list_tools` / `call_tool` / `list_resources` / `read_resource`。
3. **Apps 端到端**：在支持 MCP Apps 的宿主中触发 `@translate_ui`，验证 iframe 渲染、`tools/call` 回桥、`tool-input` 预填。
4. **并发**：UI 与 LLM 同时发起翻译，验证串行排队无异常、无 KV cache 污染。
5. **回归**：现有 `dotnet test tests/HyMT2Sharp.Tests` 不受影响（零侵入）。

## 10. 风险与不确定性

| # | 风险 | 等级 | 缓解 |
|---|------|------|------|
| R1 | MCP Apps spec 尚为草案，宿主支持面有限（目前以 VS Code 系为主） | 中 | 渐进增强设计：无 Apps 的宿主仍可用 `translate` 纯文本工具；UI 只是加分项 |
| R2 | `Extensions.Apps` 实验性 API 变动（MCPEXP003） | 中 | Apps 代码集中在 2 个文件 + 1 个 HTML；`NoWarn` 显式声明；锁 SDK 版本号 |
| R3 | 1.8B 小语种翻译质量未验证 | 低 | 语言列表分「核心/扩展」标注；实测后裁剪枚举 |
| R4 | 单实例串行 → 并发翻译排队延迟（每次数秒） | 低 | 状态提示 + 后续可演进多实例池（每实例约 1–2 GB 内存，Q4 下更小） |
| R5 | 模型懒加载首次延迟被误认为"卡死" | 低 | 工具 Description 明示；日志输出加载进度 |
| R6 | SDK 2.0.x 与 net10.0 组合在本仓库的构建兼容性 | 低 | 两者目标框架一致；M1 第一步即构建冒烟 |

## 11. 后续演进（不在本期）

- 发布 `dotnet tool`（`dotnet tool install HyMT2Sharp.Mcp`）或框架依赖单文件，降低接入门槛；
- `translate_batch` 工具 + 文件级翻译（MCP Tasks 扩展管理长任务）；
- 接入 Hy-MT2-7B/30B-A3B 后的多规格选择；
- 回填上游 sdcb/HyMT2Sharp（作为独立 PR，MCP Apps 部分可配置开关）。
