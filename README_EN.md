# HyMT2Sharp [![NuGet](https://img.shields.io/nuget/v/Sdcb.HyMT2Sharp.Model.svg)](https://www.nuget.org/packages/Sdcb.HyMT2Sharp.Model) [![License: Apache-2.0](https://img.shields.io/badge/License-Apache--2.0-blue.svg)](LICENSE) [![QQ](https://img.shields.io/badge/QQ_Group-495782587-52B6EF?style=social&logo=tencent-qq&logoColor=000&logoWidth=20)](https://qm.qq.com/cgi-bin/qm/qr?_wv=1027&k=&authKey=&noverify=0&group_code=495782587)

[中文](README.md) | **English**

Unofficial pure C# CPU inference for [Hy-MT2](https://huggingface.co/tencent/Hy-MT2-1.8B) (`hunyuan-dense`). No llama.cpp or ONNX Runtime; AVX2 kernels are built in. Intended for in-process use.

Validated GGUF quantizations: **Q4_K_M**, **Q2_0C**, and **1.25-bit STQ1_0**. Other formats and model sizes have not been tested.

## Models

Weights are not shipped in the NuGet packages. Download a GGUF yourself:

| Quant | Hugging Face |
| --- | --- |
| 1.25-bit STQ1_0 | [Hy-MT2-1.8B-1.25Bit-GGUF](https://huggingface.co/tencent/Hy-MT2-1.8B-1.25Bit-GGUF) |
| Q2_0C | [Hy-MT2-1.8B-2Bit-GGUF](https://huggingface.co/tencent/Hy-MT2-1.8B-2Bit-GGUF) |
| Q4_K_M | [Hy-MT2-1.8B-GGUF](https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF) |

## Quick start

Run the CLI from this repo (point `--model` at your GGUF):

```powershell
dotnet run --project src/HyMT2Sharp.Cli -c Release -- --model "D:\_\model\Hy-MT2-1.8B-Q4_K_M.gguf"
```

Omit `--threads` to bind physical P-cores from the CPU topology (8 threads on a 5800X; physical cores only, no SMT). Pass `--prompt` for a single turn; omit it for a multi-turn console chat.

### HTTP server

`HyMT2Sharp.Server` exposes OpenAI-compatible `POST /v1/chat/completions` (including SSE) and a built-in chat page:

```powershell
dotnet run --project src/HyMT2Sharp.Server -c Release -- --model "D:\_\model\Hy-MT2-1.8B-Q4_K_M.gguf"
```

Open `http://127.0.0.1:8080`, or call it with curl:

```powershell
curl http://127.0.0.1:8080/v1/chat/completions -H "Content-Type: application/json" -d "{\"messages\":[{\"role\":\"user\",\"content\":\"Translate the following segment into Chinese, without additional explanation：SimdPaddleOCR is officially released today (NuGet: Sdcb.SimdPaddleOCR). It is a complete OCR inference engine written entirely in C#. It does not depend on Paddle Inference or ONNX Runtime, and it does not require shipping OpenCV native libraries.\"}],\"max_tokens\":128}"
```

## Use as a library

Install the inference entry package (`Sdcb.HyMT2Sharp.Gguf` and `Sdcb.HyMT2Sharp.Kernels` come in transitively):

```powershell
dotnet add package Sdcb.HyMT2Sharp.Model
```

`HunyuanDenseModel` loads the GGUF, tokenizes, owns the KV cache, and runs `Forward`. Sampling and string assembly are left to the caller — there is no built-in `Generate` / `ArgMax`. Minimal greedy streaming example:

```csharp
using Sdcb.HyMT2Sharp.Model;

using HunyuanDenseModel model = new(@"D:\_\model\Hy-MT2-1.8B-1.25Bit.gguf");

await foreach (string piece in Generate(model, "Translate the following segment into Chinese, without additional explanation：SimdPaddleOCR is officially released today (NuGet: Sdcb.SimdPaddleOCR). It is a complete OCR inference engine written entirely in C#. It does not depend on Paddle Inference or ONNX Runtime, and it does not require shipping OpenCV native libraries."))
    Console.Write(piece);

// Full string: string text = string.Concat(await Generate(...).ToArrayAsync());

static async IAsyncEnumerable<string> Generate(
    HunyuanDenseModel model,
    string user,
    int maxTokens = 128,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    int[] prompt = model.Tokenizer.Encode(ChatTemplate.RenderHunyuanDense([new ChatMessage("user", user)]));
    float[] logits = model.Forward(model.AlignPrompt(prompt).Suffix);
    await Task.Yield();

    List<int> generated = [];
    string visible = "";
    for (int i = 0; i < maxTokens; i++)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int token = ArgMax(logits);
        if (model.Tokenizer.IsStop(token))
            break;

        generated.Add(token);
        string next = model.Tokenizer.DecodeVisible(generated);
        if (next.Length > visible.Length && next.StartsWith(visible, StringComparison.Ordinal))
            yield return next[visible.Length..];
        visible = next;

        logits = model.Forward([token]);
        await Task.Yield();
    }
}

static int ArgMax(float[] logits)
{
    int best = 0;
    for (int i = 1; i < logits.Length; i++)
        if (logits[i] > logits[best])
            best = i;
    return best;
}
```

`threads = 0` (the default) binds physical P-cores. `HunyuanDenseModel` is not thread-safe; serialize requests or use one instance per caller.

## NuGet packages

| Package | Version | Notes |
| --- | --- | --- |
| `Sdcb.HyMT2Sharp.Model` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.HyMT2Sharp.Model.svg)](https://www.nuget.org/packages/Sdcb.HyMT2Sharp.Model) | Inference entry: load, tokenize, KV cache, `Forward` |
| `Sdcb.HyMT2Sharp.Gguf` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.HyMT2Sharp.Gguf.svg)](https://www.nuget.org/packages/Sdcb.HyMT2Sharp.Gguf) | GGUF v2/v3 reader (usually referenced transitively) |
| `Sdcb.HyMT2Sharp.Kernels` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.HyMT2Sharp.Kernels.svg)](https://www.nuget.org/packages/Sdcb.HyMT2Sharp.Kernels) | AVX2 / AVX-VNNI quantized kernels (usually referenced transitively) |

`HyMT2Sharp.Cli`, `HyMT2Sharp.Server`, and `HyMT2Sharp.Benchmark` live in this repo as samples and tooling. They are not published to NuGet.

## Performance

Environment: Ryzen 7 5800X (Zen 3), Windows, Release, 8 threads, `avx2=True`, `vnni=False`. Model load and warmup are excluded. Prefill is the mean of three 512-token runs. Decode is 128 tokens after a 512-token context.

| Model | prefill 512 | decode 128 | prefill reps |
| --- | ---: | ---: | --- |
| HyMT2Sharp Q1.25 / STQ1_0 | **553.63 tok/s** | 43.10 tok/s | 570.5 / 531.1 / 560.8 |
| HyMT2Sharp Q2_0C | 541.00 tok/s | **43.79 tok/s** | 560.4 / 506.6 / 559.7 |
| HyMT2Sharp Q4_K_M | 416.33 tok/s | 24.79 tok/s | 417.0 / 412.7 / 419.4 |
| llama.cpp Q4_K_M (earlier) | 254.93 ± 3.10 tok/s | 27.39 ± 0.37 tok/s | `llama-bench -p 512 -n 128 -t 8 -ngl 0` |

Q1.25 and Q2 decode at about the same rate. Versus Q4, Q1.25 prefill is ~33% faster and decode ~74% faster. A 5800X under sustained load will wander with clocks and temperature; these numbers are not a hardware ceiling. The llama.cpp row is a previous measurement and was not re-run with this pass.

Reproduce:

```powershell
dotnet run --project src/HyMT2Sharp.Benchmark -c Release -- --model "D:\_\model\Hy-MT2-1.8B-1.25Bit.gguf" --bench-prefill 512 --bench-decode 128 --threads 8
dotnet run --project src/HyMT2Sharp.Benchmark -c Release -- --model "D:\_\model\Hy-MT2-1.8B-Q4_K_M.gguf" --bench-prefill 512 --bench-decode 128 --threads 8
dotnet run --project src/HyMT2Sharp.Benchmark -c Release -- --model "D:\_\model\Hy-MT2-1.8B-2Bit.gguf" --bench-prefill 512 --bench-decode 128 --threads 8
```

Q2 panels default to a 64 KiB tile (`--q2-col-tile-kb 64`). `--profile` prints STQ / Q2 / Q4 matmul and attention breakdowns.

## Implementation notes

- **Q4_K_M**: `q4_Kx8 × q8_Kx4` AVX2 panel GEMM; decode uses Q8 row quant + GEMV.
- **Q2_0C**: compressed 8-column panels; 2-bit weights stay packed (no per-weight byte expansion).
- **STQ1_0**: 42 B / 256-weight stride-16 blocks, repacked to 8-row panels; prefill and decode both use AVX2 GEMV / GEMM.
- **Q2 prefill**: QKV, gate/up, and SiLU→down reuse Q8 activation quant; 2-bit dots reduce in int32 to avoid int16 overflow.
- Q2 tail columns, non-aligned token counts, and the non-AVX2 path remain; the Q4 compute path is unchanged.

## Development

```powershell
dotnet test tests/HyMT2Sharp.Tests -c Release
dotnet run --project src/HyMT2Sharp.Cli -c Release -- --model "D:\_\model\Hy-MT2-1.8B-2Bit.gguf" --prompt "Translate the following segment into Chinese, without additional explanation：SimdPaddleOCR is officially released today (NuGet: Sdcb.SimdPaddleOCR). It is a complete OCR inference engine written entirely in C#. It does not depend on Paddle Inference or ONNX Runtime, and it does not require shipping OpenCV native libraries." --max-tokens 128
dotnet run --project src/HyMT2Sharp.Cli -c Release -- --model "D:\_\model\Hy-MT2-1.8B-Q4_K_M.gguf" --prompt "Translate the following segment into Chinese, without additional explanation：SimdPaddleOCR is officially released today (NuGet: Sdcb.SimdPaddleOCR). It is a complete OCR inference engine written entirely in C#. It does not depend on Paddle Inference or ONNX Runtime, and it does not require shipping OpenCV native libraries." --max-tokens 128
```

## WeChat group

![](https://io.starworks.cc:88/cv-public/2026/ocr-wxg-qr.png)

If the WeChat QR code has expired, join the QQ group [.NET骚操作 495782587](https://qm.qq.com/cgi-bin/qm/qr?_wv=1027&k=&authKey=&noverify=0&group_code=495782587).

## License

[Apache License 2.0](LICENSE)
