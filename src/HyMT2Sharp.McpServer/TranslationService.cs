using System.Diagnostics;
using System.Runtime.CompilerServices;
using Sdcb.HyMT2Sharp.Model;

namespace Sdcb.HyMT2Sharp.McpServer;

public sealed record TranslationResult(
    string SourceText,
    string TranslatedText,
    string TargetLanguage,
    string? SourceLanguage,
    int PromptTokens,
    int CompletionTokens,
    double PromptMs,
    double DecodeMs,
    double TokensPerSecond,
    bool ModelLoadedThisCall);

/// <summary>
/// Single-instance facade over <see cref="HunyuanDenseModel"/>. The model is
/// not thread-safe, so all calls are serialized through a gate. The GGUF is
/// loaded lazily on the first translation so that MCP hosts can list tools
/// without waiting for model load.
/// </summary>
public sealed class TranslationService : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _modelPath;
    private readonly int _threads;
    private readonly int _defaultMaxTokens;
    private HunyuanDenseModel? _model;
    private volatile bool _loadFailed;

    public TranslationService(string modelPath, int threads = 0, int defaultMaxTokens = 512)
    {
        _modelPath = modelPath;
        _threads = threads;
        _defaultMaxTokens = defaultMaxTokens > 0 ? defaultMaxTokens : 512;
    }

    public string ModelPath => _modelPath;
    public bool IsModelLoaded => _model is not null;

    public async Task<TranslationResult> TranslateAsync(
        string text,
        HyLanguage target,
        HyLanguage? source = null,
        int? maxTokens = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        await _gate.WaitAsync(cancellationToken);
        bool ok = false;
        try
        {
            (TranslationResult result, bool freshlyLoaded) = TranslateCore(
                text, target, source, maxTokens ?? _defaultMaxTokens, cancellationToken);
            ok = true;
            return result with { ModelLoadedThisCall = freshlyLoaded };
        }
        finally
        {
            if (!ok)
                _model?.ResetCache();
            _gate.Release();
        }
    }

    private (TranslationResult result, bool freshlyLoaded) TranslateCore(
        string text, HyLanguage target, HyLanguage? source, int maxTokens, CancellationToken ct)
    {
        bool freshlyLoaded = false;
        if (_model is null)
        {
            if (_loadFailed)
                throw new InvalidOperationException(
                    $"Model previously failed to load from '{_modelPath}'. Fix the path and restart the server.");

            if (string.IsNullOrWhiteSpace(_modelPath))
                throw new InvalidOperationException(
                    "No model path configured. Pass --model or set the HYMT2_MODEL environment variable.");

            if (!File.Exists(_modelPath))
                throw new FileNotFoundException($"Model file not found: {_modelPath}", _modelPath);

            Console.Error.WriteLine($"loading model {_modelPath}");
            try
            {
                _model = new HunyuanDenseModel(_modelPath, _threads);
            }
            catch
            {
                _loadFailed = true;
                throw;
            }

            freshlyLoaded = true;
            Console.Error.WriteLine(
                $"model ready  threads={_model.ThreadCount}{(_threads <= 0 ? " (physical P-cores)" : "")}  " +
                $"arch={_model.Config.Architecture} layers={_model.Config.NumLayers} vocab={_model.Config.VocabSize}");
        }

        HunyuanDenseModel model = _model;

        // Wrap the instruction in the official Hy-MT2 chat template (BOS +
        // user turn). Encoding the bare prompt without the template degrades
        // generation — the model never emits a stop token and DecodeVisible
        // yields empty output.
        string prompt = TranslationPrompts.Render(text, target);
        string rendered = ChatTemplate.RenderHunyuanDense([new ChatMessage("user", prompt)]);
        int[] promptIds = model.Tokenizer.Encode(rendered);
        if (promptIds.Length == 0)
            throw new ArgumentException("The text produced no tokens for the tokenizer.");
        if (promptIds.Length >= model.Config.ContextLength)
            throw new ArgumentException(
                $"The text is {promptIds.Length} tokens, exceeding the model context of {model.Config.ContextLength} tokens. " +
                "Split the text and translate it in parts.");

        PromptAlignment align = model.AlignPrompt(promptIds);
        Stopwatch timer = Stopwatch.StartNew();
        float[] logits = model.Forward(align.Suffix);
        double promptMs = timer.Elapsed.TotalMilliseconds;

        int token = ArgMax(logits);
        List<int> generated = [];
        timer.Restart();
        int decoded = 0;
        for (int i = 0; i < maxTokens; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (model.Tokenizer.IsStop(token))
                break;

            generated.Add(token);
            logits = model.Forward([token]);
            token = ArgMax(logits);
            decoded++;
        }

        double decodeMs = timer.Elapsed.TotalMilliseconds;
        string translated = model.Tokenizer.DecodeVisible(generated);

        return (new TranslationResult(
            SourceText: text,
            TranslatedText: translated,
            TargetLanguage: target.ToString(),
            SourceLanguage: source?.ToString(),
            PromptTokens: promptIds.Length,
            CompletionTokens: generated.Count,
            PromptMs: promptMs,
            DecodeMs: decodeMs,
            TokensPerSecond: decoded > 0 && decodeMs > 0 ? decoded / (decodeMs / 1000.0) : 0,
            ModelLoadedThisCall: false), freshlyLoaded);
    }

    private static int ArgMax(float[] logits)
    {
        int best = 0;
        float max = logits[0];
        for (int i = 1; i < logits.Length; i++)
        {
            if (logits[i] > max)
            {
                max = logits[i];
                best = i;
            }
        }

        return best;
    }

    public void Dispose()
    {
        _gate.Dispose();
        _model?.Dispose();
    }
}
