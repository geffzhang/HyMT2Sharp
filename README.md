# HyMT2Sharp [![NuGet](https://img.shields.io/nuget/v/Sdcb.HyMT2Sharp.Model.svg)](https://www.nuget.org/packages/Sdcb.HyMT2Sharp.Model) [![License: Apache-2.0](https://img.shields.io/badge/License-Apache--2.0-blue.svg)](LICENSE)

纯 C# 的 [Hy-MT2](https://huggingface.co/tencent/Hy-MT2-1.8B)（`hunyuan-dense`）**非官方** CPU 推理实现。不依赖 llama.cpp 或 ONNX Runtime，自带 AVX2 内核，面向进程内调用。

当前验证过的 GGUF 量化：**Q4_K_M**、**Q2_0C**、**1.25-bit STQ1_0**。其它格式与模型规模尚未测试。

## 模型

权重需自行下载（NuGet 包不含 GGUF）：

| 量化 | Hugging Face |
| --- | --- |
| 1.25-bit STQ1_0 | [Hy-MT2-1.8B-1.25Bit-GGUF](https://huggingface.co/tencent/Hy-MT2-1.8B-1.25Bit-GGUF) |
| Q2_0C | [Hy-MT2-1.8B-2Bit-GGUF](https://huggingface.co/tencent/Hy-MT2-1.8B-2Bit-GGUF) |
| Q4_K_M | [Hy-MT2-1.8B-GGUF](https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF) |

## 快速开始

从仓库直接运行 CLI（把 `--model` 换成你的 GGUF 路径）：

```powershell
dotnet run --project src/HyMT2Sharp.Cli -c Release -- --model "D:\_\model\Hy-MT2-1.8B-Q4_K_M.gguf"
```

不传 `--threads` 时按 CPU 拓扑自动绑定物理 P-core（5800X 上为 8 线程，绑物理核、不占 SMT）。加 `--prompt` 跑单轮后退出；省略则进入多轮对话。

### HTTP 服务

`HyMT2Sharp.Server` 提供 OpenAI 兼容的 `POST /v1/chat/completions`（含 SSE 流式）和内置聊天页：

```powershell
dotnet run --project src/HyMT2Sharp.Server -c Release -- --model "D:\_\model\Hy-MT2-1.8B-Q4_K_M.gguf"
```

浏览器访问 `http://127.0.0.1:8080`，或用 curl：

```powershell
curl http://127.0.0.1:8080/v1/chat/completions -H "Content-Type: application/json" -d "{\"messages\":[{\"role\":\"user\",\"content\":\"Translate into Chinese, without additional explanation：Hello\"}],\"max_tokens\":16}"
```

## 作为库使用

安装推理入口包（会传递引用 `Sdcb.HyMT2Sharp.Gguf` 与 `Sdcb.HyMT2Sharp.Kernels`）：

```powershell
dotnet add package Sdcb.HyMT2Sharp.Model
```

`HunyuanDenseModel` 负责加载 GGUF、分词、KV cache 和 `Forward`。采样策略与文本拼接留给调用方——库内目前没有内置 `Generate` / `ArgMax`。下面是一个最小 greedy 流式示例：

```csharp
using Sdcb.HyMT2Sharp.Model;

using HunyuanDenseModel model = new(@"D:\_\model\Hy-MT2-1.8B-Q4_K_M.gguf");

await foreach (string piece in Generate(model, "Translate into Chinese, without additional explanation：Hello"))
    Console.Write(piece);

// 完整字符串：string text = string.Concat(await Generate(...).ToArrayAsync());

static async IAsyncEnumerable<string> Generate(
    HunyuanDenseModel model,
    string user,
    int maxTokens = 64,
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

`threads = 0`（默认）自动绑物理 P-core。`HunyuanDenseModel` 不是线程安全的，并发请求请串行化或各用独立实例。

## NuGet 包

| 包 | 版本 | 说明 |
| --- | --- | --- |
| `Sdcb.HyMT2Sharp.Model` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.HyMT2Sharp.Model.svg)](https://www.nuget.org/packages/Sdcb.HyMT2Sharp.Model) | 推理入口：加载、分词、KV cache、`Forward` |
| `Sdcb.HyMT2Sharp.Gguf` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.HyMT2Sharp.Gguf.svg)](https://www.nuget.org/packages/Sdcb.HyMT2Sharp.Gguf) | GGUF v2/v3 读取（通常被 Model 传递引用） |
| `Sdcb.HyMT2Sharp.Kernels` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.HyMT2Sharp.Kernels.svg)](https://www.nuget.org/packages/Sdcb.HyMT2Sharp.Kernels) | AVX2 / AVX-VNNI 量化 kernel（通常被 Model 传递引用） |

`HyMT2Sharp.Cli`、`HyMT2Sharp.Server`、`HyMT2Sharp.Benchmark` 是仓库内的示例与基准工具，不发布 NuGet。

## 性能

测试环境：Ryzen 7 5800X（Zen 3）、Windows、Release、8 线程，`avx2=True`、`vnni=False`。不计模型加载与 warmup；prefill 为 512 token 三次平均，decode 为 512 token 上下文后连续生成 128 token。

| 模型 | prefill 512 | decode 128 | prefill 三次 |
| --- | ---: | ---: | --- |
| HyMT2Sharp Q1.25 / STQ1_0 | **553.63 tok/s** | 43.10 tok/s | 570.5 / 531.1 / 560.8 |
| HyMT2Sharp Q2_0C | 541.00 tok/s | **43.79 tok/s** | 560.4 / 506.6 / 559.7 |
| HyMT2Sharp Q4_K_M | 416.33 tok/s | 24.79 tok/s | 417.0 / 412.7 / 419.4 |
| llama.cpp Q4_K_M（此前记录） | 254.93 ± 3.10 tok/s | 27.39 ± 0.37 tok/s | `llama-bench -p 512 -n 128 -t 8 -ngl 0` |

Q1.25 与 Q2 的 decode 基本持平；相比 Q4，Q1.25 prefill 快约 33%、decode 快约 74%。5800X 连续满载时频率与温度波动较大，上述数字不代表硬件上限。llama.cpp 一行为历史记录，未随本轮复测。

复现：

```powershell
dotnet run --project src/HyMT2Sharp.Benchmark -c Release -- --model "D:\_\model\Hy-MT2-1.8B-1.25Bit.gguf" --bench-prefill 512 --bench-decode 128 --threads 8
dotnet run --project src/HyMT2Sharp.Benchmark -c Release -- --model "D:\_\model\Hy-MT2-1.8B-Q4_K_M.gguf" --bench-prefill 512 --bench-decode 128 --threads 8
dotnet run --project src/HyMT2Sharp.Benchmark -c Release -- --model "D:\_\model\Hy-MT2-1.8B-2Bit.gguf" --bench-prefill 512 --bench-decode 128 --threads 8
```

Q2 panel 默认 64 KiB tile，可用 `--q2-col-tile-kb 64` 显式指定。`--profile` 输出 STQ / Q2 / Q4 矩阵与注意力分项耗时。

## 实现要点

- **Q4_K_M**：`q4_Kx8 × q8_Kx4` AVX2 panel GEMM；decode 走 Q8 行量化 + GEMV。
- **Q2_0C**：8 列压缩 panel，保留 2-bit 权重，不展开为逐字节副本。
- **STQ1_0**：42 B / 256 权重的 stride-16 block，加载时重排为 8 行 panel，prefill / decode 均走 AVX2 GEMV / GEMM。
- **Q2 prefill**：QKV、gate/up、SiLU→down 复用 Q8 激活量化；2-bit 点积用 int32 归约避免 int16 溢出。
- Q2 尾部列、非对齐 token 与非 AVX2 路径仍保留；Q4 计算路径未改动。

## 开发与测试

```powershell
dotnet test tests/HyMT2Sharp.Tests -c Release
dotnet run --project src/HyMT2Sharp.Cli -c Release -- --model "D:\_\model\Hy-MT2-1.8B-2Bit.gguf" --prompt "Translate the following segment into Chinese, without additional explanation：Hello" --max-tokens 16
dotnet run --project src/HyMT2Sharp.Cli -c Release -- --model "D:\_\model\Hy-MT2-1.8B-Q4_K_M.gguf" --prompt "Translate the following segment into Chinese, without additional explanation：Hello" --max-tokens 16
```

当前 22/22 测试通过；Q2 与 Q4 在上述翻译 prompt 下均输出「你好」。

## 许可证

[Apache License 2.0](LICENSE)
