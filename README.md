# HyMT2Sharp

纯 C# 的腾讯 Hy-MT2（`hunyuan-dense`）CPU 推理实现，带 AVX2 内核，目前测过 Q4_K_M、Q2_0C 和 1.25-bit STQ1_0 这三种 GGUF。其它模型没有测过。

GGUF 下载：

- [Hy-MT2-1.8B-1.25Bit-GGUF](https://huggingface.co/tencent/Hy-MT2-1.8B-1.25Bit-GGUF)
- [Hy-MT2-1.8B-2Bit-GGUF](https://huggingface.co/tencent/Hy-MT2-1.8B-2Bit-GGUF)
- [Hy-MT2-1.8B-GGUF](https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF)（含 Q4_K_M）

## 快速开始

```powershell
dotnet test tests/HyMT2Sharp.Tests -c Release
dotnet run --project src/HyMT2Sharp.Cli -c Release -- --model "D:\_\model\Hy-MT2-1.8B-Q4_K_M.gguf"
```

不传 `--threads` 时按拓扑自动占用物理 P-core（5800X 上是 8，并绑到物理核，不占 SMT）。`--prompt` 会跑完一轮后退出；省略则进入控制台多轮对话。

网页端是 ASP.NET Core Minimal API，兼容 OpenAI 的 `POST /v1/chat/completions`（含 SSE 流式），并自带聊天页：

```powershell
dotnet run --project src/HyMT2Sharp.Server -c Release -- --model "D:\_\model\Hy-MT2-1.8B-Q4_K_M.gguf"
```

浏览器打开 `http://127.0.0.1:8080`。也可用标准客户端：

```powershell
curl http://127.0.0.1:8080/v1/chat/completions -H "Content-Type: application/json" -d "{\"messages\":[{\"role\":\"user\",\"content\":\"Translate into Chinese, without additional explanation：Hello\"}],\"max_tokens\":16}"
```

## 进程内调用

先自行下载 GGUF，工程只引用 `HyMT2Sharp.Model`。`HunyuanDenseModel` 负责加载、分词、KV 缓存和 `Forward`；采样和拼接由调用方完成。下面的 `Generate` 按可见文本增量 `yield`，需要完整结果时自行合并即可。

```csharp
using HyMT2Sharp.Model;

using HunyuanDenseModel model = new(@"D:\_\model\Hy-MT2-1.8B-Q4_K_M.gguf");

await foreach (string piece in Generate(model, "Translate into Chinese, without additional explanation：Hello"))
    Console.Write(piece);

// 若要完整字符串：
// string text = string.Concat(await Generate(...).ToArrayAsync());

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

`threads` 传 `0`（默认）时按物理 P-core 自动绑核。实例不是线程安全的；多请求请排队或各用各的实例。

## Ryzen 7 5800X 性能

测试环境为 Ryzen 7 5800X（Zen 3）、Windows、Release、8 线程，运行时显示 `avx2=True`、`vnni=False`。模型加载和预热不计时；prefill 为 512 个 token 的三次平均，decode 为 512 token 上下文后连续生成 128 个 token。

最近对 Q1.25（STQ1_0）、Q2 和 Q4 进行了两轮独立进程复测，三种模型依次运行。下表为第二轮的完整结果：

| 模型                         |         prefill 512 |         decode 128 | prefill 三次                            |
| ---------------------------- | ------------------: | -----------------: | --------------------------------------- |
| HyMT2Sharp Q1.25 / STQ1_0    |    **553.63 tok/s** |        43.10 tok/s | 570.5 / 531.1 / 560.8                   |
| HyMT2Sharp Q2_0C             |        541.00 tok/s |    **43.79 tok/s** | 560.4 / 506.6 / 559.7                   |
| HyMT2Sharp Q4_K_M            |        416.33 tok/s |        24.79 tok/s | 417.0 / 412.7 / 419.4                   |
| llama.cpp Q4_K_M（此前记录） | 254.93 ± 3.10 tok/s | 27.39 ± 0.37 tok/s | `llama-bench -p 512 -n 128 -t 8 -ngl 0` |

按这轮数据，Q1.25 prefill 比 Q2 快约 2.3%，decode 慢约 1.6%，两者 decode 基本处于同一水平；相比 Q4，Q1.25 prefill 快约 33%，decode 快约 74%。

两轮 Q1.25 分别测得 prefill 555.61 / 553.63 tok/s、decode 43.56 / 43.10 tok/s。5800X 在连续满载时会受频率、温度和后台负载影响，这些结果不代表固定硬件上限。llama.cpp 一行保留此前的测量记录，未随本轮复测。

复现命令：

```powershell
dotnet run --project src/HyMT2Sharp.Benchmark -c Release -- --model "D:\_\model\Hy-MT2-1.8B-1.25Bit.gguf" --bench-prefill 512 --bench-decode 128 --threads 8
dotnet run --project src/HyMT2Sharp.Benchmark -c Release -- --model "D:\_\model\Hy-MT2-1.8B-Q4_K_M.gguf" --bench-prefill 512 --bench-decode 128 --threads 8
dotnet run --project src/HyMT2Sharp.Benchmark -c Release -- --model "D:\_\model\Hy-MT2-1.8B-2Bit.gguf" --bench-prefill 512 --bench-decode 128 --threads 8
```

Q2 面板默认使用 64 KiB 分块，也可以显式指定 `--q2-col-tile-kb 64`。`--profile` 可查看 STQ / Q2 / Q4 矩阵和注意力等分项耗时。

## 实现说明

- Q4_K_M 使用 `q4_Kx8 × q8_Kx4` AVX2 面板 GEMM，decode 使用 Q8 行量化和 GEMV。
- Q2_0C 使用压缩的 8 列面板，保留 2-bit 权重，不创建逐权重的字节展开副本。
- 1.25-bit STQ1_0 使用 42 字节 / 256 权重的 stride-16 块，加载时重排为 8 行面板，并用 AVX2 GEMV / GEMM 覆盖 decode 和 prefill。
- Q2 prefill 在 QKV、gate/up 和 SiLU→down 路径复用 Q8 激活量化，并对 2-bit 点积使用 int32 归约，避免 int16 溢出。
- Q2 的尾部列、非整齐 token 数和非 AVX2 通用路径仍保留；Q4 计算路径未修改。

## 验证

```powershell
dotnet test tests/HyMT2Sharp.Tests -c Release --no-restore
dotnet run --project src/HyMT2Sharp.Cli -c Release -- --model "D:\_\model\Hy-MT2-1.8B-2Bit.gguf" --prompt "Translate the following segment into Chinese, without additional explanation：Hello" --max-tokens 16
dotnet run --project src/HyMT2Sharp.Cli -c Release -- --model "D:\_\model\Hy-MT2-1.8B-Q4_K_M.gguf" --prompt "Translate the following segment into Chinese, without additional explanation：Hello" --max-tokens 16
```

当前测试为 22/22，Q2 和 Q4 的已知翻译提示均输出「你好」。

## 许可证

本项目采用 [Apache License 2.0](LICENSE)。
