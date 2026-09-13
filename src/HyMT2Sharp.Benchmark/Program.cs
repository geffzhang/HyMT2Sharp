using System.Diagnostics;
using HyMT2Sharp.Gguf;
using HyMT2Sharp.Kernels;
using HyMT2Sharp.Model;

string modelPath = Args.Get(args, "--model")
    ?? @"D:\_\model\Hy-MT2-1.8B-Q4_K_M.gguf";
int threads = Args.GetInt(args, "--threads", CpuThreadPool.PreferPCoreCount());
int benchPrefill = Args.GetInt(args, "--bench-prefill", 0);
int benchDecode = Args.GetInt(args, "--bench-decode", 0);
int colTileKb = Args.GetInt(args, "--col-tile-kb", 0);
if (colTileKb > 0)
    GemmQ4K.ColTileBytes = colTileKb * 1024;
int q2ColTileKb = Args.GetInt(args, "--q2-col-tile-kb", 0);
if (q2ColTileKb > 0)
    MulMatQ2.ColTileBytes = q2ColTileKb * 1024;
bool bench = args.Contains("--bench") || benchPrefill > 0 || benchDecode > 0;
if (args.Contains("--dump-q4-gemm"))
{
    DumpQ4Gemm();
    return;
}

if (args.Contains("--micro-q4"))
{
    MicroQ4(Args.GetInt(args, "--micro-in", 2048), Args.GetInt(args, "--micro-out", 64), Args.GetInt(args, "--micro-tokens", 32), Args.GetInt(args, "--micro-reps", 500));
    return;
}

if (args.Contains("--dump-silu"))
{
    DumpSilu();
    return;
}

if (args.Contains("--dump"))
{
    using GgufFile dump = new(modelPath);
    Console.WriteLine($"arch={dump.GetString("general.architecture")} pre={dump.GetString("tokenizer.ggml.pre")} vocab={dump.GetStringArray("tokenizer.ggml.tokens").Length}");
    Dictionary<string, int> typeCounts = [];
    foreach ((string name, GgufTensorInfo info) in dump.Tensors)
    {
        string t = info.Type.ToString();
        typeCounts[t] = typeCounts.TryGetValue(t, out int c) ? c + 1 : 1;
        if (info.Type is not GgmlTensorType.Q4_K and not GgmlTensorType.F32)
            Console.WriteLine($"{info.Type,-8} {name} [{string.Join(",", info.Shape)}]");
    }

    Console.WriteLine("type counts:");
    foreach ((string t, int c) in typeCounts)
        Console.WriteLine($"  {t}={c}");
    return;
}

if (!bench && !args.Contains("--verify-prefill"))
{
    Console.WriteLine("HyMT2Sharp.Benchmark");
    Console.WriteLine("  --model PATH");
    Console.WriteLine("  --threads N");
    Console.WriteLine("  --bench");
    Console.WriteLine("  --bench-prefill N");
    Console.WriteLine("  --bench-decode N");
    Console.WriteLine("  --col-tile-kb N");
    Console.WriteLine("  --q2-col-tile-kb N");
    Console.WriteLine("  --profile");
    Console.WriteLine("  --verify-prefill N");
    Console.WriteLine("  --dump");
    Console.WriteLine("  --dump-q4-gemm");
    Console.WriteLine("  --dump-silu");
    Console.WriteLine("  --micro-q4 [--micro-in N --micro-out N --micro-tokens N --micro-reps N]");
    return;
}

Console.WriteLine($"HyMT2Sharp  model={modelPath}");
Console.WriteLine($"threads={threads}  avx2={System.Runtime.Intrinsics.X86.Avx2.IsSupported}  vnni={System.Runtime.Intrinsics.X86.AvxVnni.IsSupported}");

using HunyuanDenseModel model = new(modelPath, threads);
Console.WriteLine($"arch={model.Config.Architecture} layers={model.Config.NumLayers} hidden={model.Config.HiddenSize} heads={model.Config.NumHeads}/{model.Config.NumKvHeads} vocab={model.Config.VocabSize}");

if (args.Contains("--verify-prefill"))
{
    int count = Math.Max(1, Args.GetInt(args, "--verify-prefill", 16));
    int[] ids = new int[count];
    for (int i = 0; i < ids.Length; i++) ids[i] = 1 + i % Math.Max(1, model.Config.VocabSize - 1);
    model.ResetCache();
    float[] batched = model.Forward(ids);
    model.ResetCache();
    float[] serial = [];
    for (int i = 0; i < ids.Length; i++)
        serial = model.Forward([ids[i]]);
    float maxAbs = 0;
    double sumSq = 0;
    for (int i = 0; i < batched.Length; i++)
    {
        float d = batched[i] - serial[i];
        maxAbs = MathF.Max(maxAbs, MathF.Abs(d));
        sumSq += d * d;
    }
    Console.WriteLine($"verify-prefill tokens={count} max-abs={maxAbs:E3} rms={Math.Sqrt(sumSq / batched.Length):E3} batched-top={ArgMax(batched)} serial-top={ArgMax(serial)}");
    return;
}

if (bench && (benchPrefill > 0 || benchDecode > 0 || args.Contains("--bench")))
{
    int pp = benchPrefill > 0 ? benchPrefill : 512;
    int tg = benchDecode > 0 ? benchDecode : 128;
    int[] ids = new int[pp];
    int vocab = Math.Max(2, model.Config.VocabSize);
    for (int i = 0; i < pp; i++)
        ids[i] = 1 + (i % (vocab - 1));

    bool profile = args.Contains("--profile");
    // llama-bench warms the prompt and one generated token before the timed reps.
    model.ResetCache();
    model.Forward(ids);
    model.ResetCache();
    model.Forward([ids[0]]);
    model.ResetCache();

    HunyuanDenseModel.ProfileEnabled = profile;
    const int reps = 3;
    double[] prefillMs = new double[reps];
    float[] logits = [];
    Stopwatch sw = new();
    for (int r = 0; r < reps; r++)
    {
        model.ResetCache();
        HunyuanDenseModel.ResetProfile();
        sw.Restart();
        logits = model.Forward(ids);
        sw.Stop();
        prefillMs[r] = sw.Elapsed.TotalMilliseconds;
        if (profile)
            PrintProfile($"prefill[{r}]");
    }

    int next = ArgMax(logits);
    HunyuanDenseModel.ProfileEnabled = false;
    model.ResetCache();
    logits = model.Forward(ids);
    next = ArgMax(logits);
    HunyuanDenseModel.ProfileEnabled = profile;
    HunyuanDenseModel.ResetProfile();
    sw.Restart();
    for (int i = 0; i < tg; i++)
    {
        logits = model.Forward([next]);
        next = ArgMax(logits);
    }

    double decodeMs = sw.Elapsed.TotalMilliseconds;
    double ppMean = 0;
    for (int r = 0; r < reps; r++)
        ppMean += prefillMs[r];
    ppMean /= reps;
    Console.WriteLine($"prefill tokens={pp}  {ppMean:F1} ms  {pp / (ppMean / 1000.0):F2} tok/s  reps={pp / (prefillMs[0] / 1000.0):F1}/{pp / (prefillMs[1] / 1000.0):F1}/{pp / (prefillMs[2] / 1000.0):F1}");
    Console.WriteLine($"decode  tokens={tg}  {decodeMs:F1} ms  {tg / (decodeMs / 1000.0):F2} tok/s");
    if (profile)
        PrintProfile("decode");
    Console.WriteLine($"Compare llama-bench -m <same.gguf> -p {pp} -n {tg} -t {threads} -ngl 0");
    return;
}

static void PrintProfile(string phase)
{
    double freq = Stopwatch.Frequency;
    double q4 = HunyuanDenseModel.TicksQ4 / freq * 1000.0;
    double q2 = HunyuanDenseModel.TicksQ2 / freq * 1000.0;
    double stq = HunyuanDenseModel.TicksSTQ / freq * 1000.0;
    double q6 = HunyuanDenseModel.TicksQ6 / freq * 1000.0;
    double score = HunyuanDenseModel.TicksAttnScore / freq * 1000.0;
    double soft = HunyuanDenseModel.TicksSoftmax / freq * 1000.0;
    double comb = HunyuanDenseModel.TicksAttnCombine / freq * 1000.0;
    double rms = HunyuanDenseModel.TicksRms / freq * 1000.0;
    double rope = HunyuanDenseModel.TicksRope / freq * 1000.0;
    double silu = HunyuanDenseModel.TicksSilu / freq * 1000.0;
    double quant = HunyuanDenseModel.TicksQuant / freq * 1000.0;
    double embed = HunyuanDenseModel.TicksEmbed / freq * 1000.0;
    double accounted = q4 + q2 + stq + q6 + score + soft + comb + rms + rope + silu + quant + embed;
    Console.WriteLine($"profile {phase}: q4={q4:F0}ms q2={q2:F0}ms stq={stq:F0}ms q6={q6:F0}ms attnQK={score:F0}ms softmax={soft:F0}ms attnAV={comb:F0}ms rms={rms:F0}ms rope={rope:F0}ms silu={silu:F0}ms embed={embed:F0}ms accounted={accounted:F0}ms");
}

static unsafe void MicroQ4(int nIn, int nOut, int tokens, int reps)
{
    int nb = nIn / Qk.SuperBlock;
    using NativeBuffer q4 = new((nuint)((long)nOut * nb * Qk.Q4KSize));
    using NativeBuffer q4x8 = new((nuint)((long)(nOut / 8) * nb * Qk.Q4Kx8Size));
    using NativeBuffer q8x4 = new((nuint)((long)(tokens / 4) * nb * Qk.Q8Kx4Size));
    using NativeBuffer dst = new((nuint)((long)tokens * nOut * sizeof(float)));
    using NativeBuffer src = new((nuint)((long)tokens * nIn * sizeof(float)));
    using NativeBuffer scales = new((nuint)((long)(nOut / 8) * nb * Qk.Q4Kx8MetaSize));
    float* input = (float*)src.Pointer;
    for (int i = 0; i < tokens * nIn; i++)
        input[i] = MathF.Sin(i * 0.37f);
    BlockQ4K* rows = (BlockQ4K*)q4.Pointer;
    for (int r = 0; r < nOut; r++)
        Q4K.PackSimple(input + (r % tokens) * nIn, rows + r * nb, nIn);
    RepackQ4K.Rows(rows, (BlockQ4Kx8*)q4x8.Pointer, nIn, nOut);
    RepackQ4K.BuildMeta(rows, (BlockQ4Kx8Meta*)scales.Pointer, nIn, nOut);
    for (int g = 0; g < tokens / 4; g++)
        QuantizeQ8Kx4.Quantize4x8(input + g * 4 * nIn, (BlockQ8Kx4*)q8x4.Pointer + g * nb, nIn);
    for (int i = 0; i < 20; i++)
        GemmQ4K.GemmAvx2(nIn, (float*)dst.Pointer, nOut, (BlockQ4Kx8*)q4x8.Pointer, (BlockQ8Kx4*)q8x4.Pointer, tokens, nOut, (BlockQ4Kx8Meta*)scales.Pointer);
    Stopwatch sw = Stopwatch.StartNew();
    for (int i = 0; i < reps; i++)
        GemmQ4K.GemmAvx2(nIn, (float*)dst.Pointer, nOut, (BlockQ4Kx8*)q4x8.Pointer, (BlockQ8Kx4*)q8x4.Pointer, tokens, nOut, (BlockQ4Kx8Meta*)scales.Pointer);
    sw.Stop();
    double macs = (double)nIn * nOut * tokens * reps;
    double maddubs = macs / 32;
    Console.WriteLine($"micro-q4 in={nIn} out={nOut} tokens={tokens} weights={(nOut / 8) * nb * Qk.Q4Kx8Size / 1024}KB act={(tokens / 4) * nb * Qk.Q8Kx4Size / 1024}KB  {sw.Elapsed.TotalMilliseconds:F1} ms  {macs / sw.Elapsed.TotalSeconds / 1e9:F1} GMAC/s  {maddubs / sw.Elapsed.TotalSeconds / 1e9:F2} G-maddubs/s (peak ~9 at 4.5GHz)");
}

static unsafe void DumpQ4Gemm()
{
    const int nIn = 256;
    const int nOut = 16;
    const int tokens = 4;
    using NativeBuffer q4 = new((nuint)((long)nOut * (nIn / Qk.SuperBlock) * Qk.Q4KSize));
    using NativeBuffer q4x8 = new((nuint)((long)(nOut / 8) * (nIn / Qk.SuperBlock) * Qk.Q4Kx8Size));
    using NativeBuffer q8x4 = new((nuint)((nIn / Qk.SuperBlock) * Qk.Q8Kx4Size));
    using NativeBuffer meta = new((nuint)((long)(nOut / 8) * (nIn / Qk.SuperBlock) * Qk.Q4Kx8MetaSize));
    using NativeBuffer dst = new((nuint)((long)tokens * nOut * sizeof(float)));
    using NativeBuffer src = new((nuint)((long)tokens * nIn * sizeof(float)));
    float* input = (float*)src.Pointer;
    for (int i = 0; i < tokens * nIn; i++)
        input[i] = (i % 17) * 0.01f;
    BlockQ4K* rows = (BlockQ4K*)q4.Pointer;
    int nb = nIn / Qk.SuperBlock;
    for (int r = 0; r < nOut; r++)
        Q4K.PackSimple(input, rows + r * nb, nIn);
    RepackQ4K.Rows(rows, (BlockQ4Kx8*)q4x8.Pointer, nIn, nOut);
    RepackQ4K.BuildMeta(rows, (BlockQ4Kx8Meta*)meta.Pointer, nIn, nOut);
    QuantizeQ8Kx4.Quantize4x8(input, (BlockQ8Kx4*)q8x4.Pointer, nIn);
    GemmQ4K.GemmAvx2(nIn, (float*)dst.Pointer, nOut, (BlockQ4Kx8*)q4x8.Pointer, (BlockQ8Kx4*)q8x4.Pointer, tokens, nOut, (BlockQ4Kx8Meta*)meta.Pointer);
    Console.WriteLine($"dump-q4-gemm dst0={*((float*)dst.Pointer)}");
}

static unsafe void DumpSilu()
{
    const int n = 64;
    using NativeBuffer gate = new((nuint)(n * sizeof(float)));
    using NativeBuffer up = new((nuint)(n * sizeof(float)));
    float* g = (float*)gate.Pointer;
    float* u = (float*)up.Pointer;
    for (int i = 0; i < n; i++)
    {
        g[i] = (i - 32) * 0.1f;
        u[i] = 1.25f;
    }

    Ops.SiLUMulRange(g, u, 0, n);
    Console.WriteLine($"dump-silu g0={g[0]}");
}

static int ArgMax(float[] logits)
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

static class Args
{
    public static string? Get(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static int GetInt(string[] args, string name, int fallback)
        => int.TryParse(Get(args, name), out int value) ? value : fallback;
}
