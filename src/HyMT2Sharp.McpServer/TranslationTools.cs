using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Sdcb.HyMT2Sharp.McpServer;

[McpServerToolType]
public sealed class TranslationTools
{
    /// <summary>
    /// JSON options for <c>StructuredContent</c>: snake_case keys to match the
    /// wire style used by the OpenAI-compatible server in this repo.
    /// </summary>
    private static readonly JsonSerializerOptions StructuredJson = new(JsonSerializerOptions.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter<HyLanguage>() },
    };

    [McpServerTool(Name = "translate")]
    [Description(
        "Translate text with the local Hy-MT2 model — fully offline, no network or API key needed. " +
        "Supports 33+ languages (Chinese, English, Japanese, Korean, and more, incl. Traditional Chinese). " +
        "Note: the first call also loads the GGUF model into memory and can take several seconds; " +
        "concurrent calls are serialized on a single model instance.")]
    public static async Task<CallToolResult> Translate(
        TranslationService svc,
        [Description("Text to translate")] string text,
        [Description("Target language to translate into, e.g. zh, en, ja")] HyLanguage targetLanguage,
        [Description("Optional source language hint (metadata only; the model auto-dects the source)")]
        HyLanguage? sourceLanguage = null,
        [Description("Maximum tokens to generate (default 512)")] int? maxTokens = null,
        CancellationToken cancellationToken = default)
    {
        TranslationResult r;
        try
        {
            r = await svc.TranslateAsync(text, targetLanguage, sourceLanguage, maxTokens, cancellationToken);
        }
        // Surface actionable messages to the client; McpException text is
        // forwarded verbatim, other exceptions are masked by the SDK.
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or InvalidOperationException)
        {
            throw new McpException(ex.Message, ex);
        }

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = r.TranslatedText }],
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                text = r.SourceText,
                source_language = r.SourceLanguage,
                target_language = r.TargetLanguage,
                translated_text = r.TranslatedText,
                usage = new
                {
                    prompt_tokens = r.PromptTokens,
                    completion_tokens = r.CompletionTokens,
                },
                timings = new
                {
                    prompt_ms = Math.Round(r.PromptMs, 1),
                    decode_ms = Math.Round(r.DecodeMs, 1),
                    tokens_per_second = Math.Round(r.TokensPerSecond, 1),
                },
                model_loaded_this_call = r.ModelLoadedThisCall,
            }, StructuredJson),
        };
    }
}
