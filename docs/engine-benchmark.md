# Hy-MT2 1.8B 跨引擎指标

文档版本：**2026-09-13.4**  
测量日期：2026-09-13（UTC+8 晚间，同一台机器连续复测）

主角是 **HyMT2Sharp**（纯 C# CPU，三种量化）。llama.cpp / TensorSharp 只作为对照。HyMT2Sharp **不支持 GPU**，所以 GPU 数字全部放在第 6 节，不和 CPU 主表混排。

本页统一 **prefill 512 / decode 128**。5800X 连续满载时频率和温度会漂，这些数字不是硬件上限。

**怎么读：** 给 HyMT2Sharp 打分请看第 4、5 节（同一条 CPU）。第 6 节的 llama.cpp GPU（约 1.3 万 / 330）是另一台加速器、另一套成熟 CUDA 图，不是 AVX2 写砸了。物理账见第 9 节。

## 1. 软件版本

| 组件 | 版本 |
| --- | --- |
| **HyMT2Sharp** | git `9567006ce31dc9a3f2041c78715f0dfbd667634c`（2026-09-13 19:39 +0800）；NuGet `Sdcb.HyMT2Sharp.{Gguf,Kernels,Model}` **1.0.0**；`net10.0` |
| **TensorSharp** | git `60704129caacb32211d4eab2fd91623b06123d6e`，标签 **v3.4.0.0**（2026-09-12）；仓库属性 `TensorSharpVersion=2.8.6`；原生 ggml **0.23.0** / `7840aab`；GGML CUDA 仅编 `86-real` |
| **llama.cpp** | **0.4.0-dev**，build **10941**，commit **4a8993735**；Clang 20.1.8 / Windows x86_64；`C:\_\3rd\bin\llama-bench.exe`。已加载 `ggml-cuda.dll`，能看到 `CUDA0: RTX 3080 Ti`。更早一轮 CPU-only 构建是 build **10894** / `d344123fe`（无 CUDA） |
| .NET SDK | 10.0.400 |
| CUDA Toolkit | **13.4.59** |
| NVIDIA 驱动 | **581.80**（`nvidia-smi` 报 CUDA **13.0**） |

TensorSharp Direct CUDA 不能直接用 `dotnet build` 的 PTX 9.4（驱动只 JIT 到 13.0）。本轮 kernel 编成 **`sm_86` cubin**。`ggml_cuda` 还要把 `...\CUDA\v13.4\bin\x64` 加进 `PATH`。

## 2. 硬件与模型

| 项 | 值 |
| --- | --- |
| CPU | AMD Ryzen 7 5800X，8 核 / 16 线程，Zen 3 |
| GPU | NVIDIA GeForce RTX 3080 Ti 12 GB，compute 8.6（仅对照引擎使用） |
| OS | Windows 10.0.26200 |
| 线程 | 8（`--threads 8` / `TS_*_THREADS=8` / `llama-bench -t 8`） |
| SIMD | AVX2=True，AVX-VNNI=False，无 AVX-512 |

| 量化 | 文件 | 大小 |
| --- | --- | ---: |
| STQ1_0（1.25-bit） | `D:\_\model\Hy-MT2-1.8B-1.25Bit.gguf` | 441 MiB |
| Q2_0C | `D:\_\model\Hy-MT2-1.8B-2Bit.gguf` | 573 MiB |
| Q4_K_M | `D:\_\model\Hy-MT2-1.8B-Q4_K_M.gguf` | 1.05 GiB |

三份都是 `hunyuan-dense`，32 层，hidden 2048，heads 16/4，词表 120818，约 1.79B。

## 3. 工作负载

| 引擎 | 计时口径 |
| --- | --- |
| HyMT2Sharp | warmup 一次 512 + 一次单 token；3 次 prefill 取平均；再 prefill 512 后连续 128 次 `Forward`（含 ArgMax） |
| TensorSharp | 内置 warmup；`--bench-runs 3` greedy-e2e；表内 **best**，括号为平均 |
| llama.cpp | warmup 后 pp512 / tg128 各 5 次（Q2/Q1.25 试跑用了 3 次，但模型加载失败） |

## 4. 主角：HyMT2Sharp CPU

本轮现场，8 线程，三种官方量化。

| 量化 | prefill 512 | decode 128 | prefill 三次 |
| --- | ---: | ---: | --- |
| **Q1.25 / STQ1_0** | **530.53** | 32.47 | 539.3 / 519.3 / 533.4 |
| **Q2_0C** | 529.18 | **33.99** | 534.0 / 529.7 / 524.0 |
| **Q4_K_M** | 375.84 | 17.31 | 363.3 / 369.2 / 396.7 |

相对本轮 Q4：Q1.25 prefill 快约 **41%**、decode 快约 **88%**；Q2 prefill 快约 **41%**、decode 快约 **96%**。Q1.25 与 Q2 彼此接近，decode 都明显快于 Q4。

这份 llama.cpp **加载不了** `Q2_0C` / `STQ1_0`（`failed to load model`），因此下面跨引擎对照只有 Q4_K_M。

## 5. CPU 跨引擎（仅 Q4_K_M）

HyMT2Sharp 没有 GPU，所以这一节仍是 CPU。TensorSharp 的 `hunyuan-dense` 是通用逐算子路径，没有整网融合图。

| 引擎 | 后端 | prefill 512 | decode 128 |
| --- | --- | ---: | ---: |
| **HyMT2Sharp 1.0.0** | 自研 AVX2 Q4 panel | **375.84** | **17.31** |
| llama.cpp 0.4.0-dev.10941 | `-dev none`（纯 CPU） | 243.96 ± 6.32 | 23.44 ± 0.37 |
| TensorSharp v3.4.0.0 | `--backend ggml_cpu` | 104.0（均 103.6） | 13.4（均 13.3） |
| TensorSharp v3.4.0.0 | `--backend cpu` | 25.3（均 25.2） | 12.4（均 12.3） |

HyMT2Sharp prefill 约为这份 llama.cpp CPU 的 **1.5×**，decode 约为 **0.74×**。注意：新构建只要加载了 `ggml-cuda.dll`，只写 `-ngl 0` 仍会把 backend 标成 CUDA（本轮 pp512 约 3022、tg128 约 29.8），那不是纯 CPU。纯 CPU 必须加 **`-dev none`**。

## 6. GPU 对照（HyMT2Sharp 无此项）

全部 GPU 数字集中在这里。模型仍是 Q4_K_M。HyMT2Sharp 不参与。

| 引擎 | 后端 | prefill 512 | decode 128 | 备注 |
| --- | --- | ---: | ---: | --- |
| llama.cpp 0.4.0-dev.10941 | CUDA `-ngl 99` | **14610 ± 1265** | **337.92 ± 1.96** | `ggml-cuda.dll`，整模上 GPU |
| TensorSharp v3.4.0.0 | `--backend cuda`（sm_86 cubin） | 2592（2592 / 2118 / 1768，均 2159） | 16.3（均 15.3） | attention 约占 decode 89% |
| TensorSharp v3.4.0.0 | `--backend ggml_cuda` | 347（均 318） | 13.8（均 13.6） | 同样是通用逐算子图 |

同一晚再跑 3 次，仍是 pp512 **12984 ± 760**、tg128 **325 ± 7**。数量级没变；prefill 单次只有约 35–40 ms，计时噪声大，所以标准差看起来吓人。

第 6 节只比较 **GPU 引擎彼此**：llama.cpp CUDA prefill 大约是 TensorSharp Direct CUDA 的 **5.6×**，decode 大约是 **21×**。不要拿 14610 / 337 去除 HyMT2Sharp 的 376 / 17——那是 3080 Ti 对 5800X，不是引擎对引擎。TensorSharp Direct CUDA 的 decode（16）甚至低于 HyMT2Sharp CPU（17）：每个 token 还要 launch 一整层小 kernel，「有 GPU」不等于「有可用的 decode 图」。

`--backend cuda` 最后一轮 129 次 forward、8375 ms：attention 7485 ms（89.4%），linear 377 ms（4.5%）。`--backend ggml_cuda` 合计 11007 ms：attention 50.3%，linear 28.5%。

## 7. 和 README 旧数字

README「性能」表是更早一轮，**不要和本文件第 4–6 节拼成一张表**。

| 来源 | Q1.25 pp/tg | Q2 pp/tg | Q4 pp/tg | llama.cpp Q4 pp/tg |
| --- | ---: | ---: | ---: | ---: |
| README 既有记录 | 553.63 / 43.10 | 541.00 / 43.79 | 416.33 / 24.79 | 254.93 ± 3.10 / 27.39 ± 0.37 |
| 本文 2026-09-13 现场 | 530.53 / 32.47 | 529.18 / 33.99 | 375.84 / 17.31 | 243.96 ± 6.32 / 23.44 ± 0.37（`-dev none`） |

排序没变：Q1.25 ≈ Q2 ≫ Q4（尤其 decode）；HyMT2Sharp Q4 prefill 仍快于这份 llama.cpp，decode 仍略慢。绝对值今晚偏低。

## 8. 复现命令

```powershell
# HyMT2Sharp 1.0.0 — 三种量化
dotnet run --project src/HyMT2Sharp.Benchmark -c Release -- `
  --model "D:\_\model\Hy-MT2-1.8B-1.25Bit.gguf" --bench-prefill 512 --bench-decode 128 --threads 8
dotnet run --project src/HyMT2Sharp.Benchmark -c Release -- `
  --model "D:\_\model\Hy-MT2-1.8B-2Bit.gguf" --bench-prefill 512 --bench-decode 128 --threads 8
dotnet run --project src/HyMT2Sharp.Benchmark -c Release -- `
  --model "D:\_\model\Hy-MT2-1.8B-Q4_K_M.gguf" --bench-prefill 512 --bench-decode 128 --threads 8

# llama.cpp 0.4.0-dev.10941（只能跑 Q4；Q2/Q1.25 加载失败）
llama-bench.exe -m "D:\_\model\Hy-MT2-1.8B-Q4_K_M.gguf" -p 512 -n 128 -t 8 -ngl 99 -r 5 -o md
llama-bench.exe -m "D:\_\model\Hy-MT2-1.8B-Q4_K_M.gguf" -p 512 -n 128 -t 8 -ngl 0 -dev none -r 5 -o md

# TensorSharp CPU / GPU 对照见仓库 TensorSharp.Cli，模型仍用 Q4_K_M
```

## 9. 读数时注意

- HyMT2Sharp 是 CPU 引擎。看主表请用第 4 节；看别人家的 CPU 用第 5 节；GPU 只看第 6 节。
- **14610 / 337 没测错。** 复测仍是约 1.3 万 / 325。1.8B Q4 整模进 12 GB 3080 Ti 之后，这个量级合理：
  - decode 近似带宽墙：337 tok/s × 1.05 GiB ≈ **350 GB/s**，3080 Ti 理论带宽约 912 GB/s，利用率约 38%。
  - 5800X 内存带宽大约 50 GB/s。HyMT2Sharp Q4 decode 17 × 1.05 GiB ≈ **18 GB/s**，Q2 decode 34 × 0.56 GiB ≈ **19 GB/s**，也是正常的 CPU 侧利用率。
  - 带宽差大约 15–18 倍，decode 差大约 20 倍，对得上硬件比，对不上「AVX2 很可笑」。
- 公平对照是第 5 节：HyMT2Sharp Q4 prefill 约为这份 llama.cpp CPU 的 **1.5×**，decode 约为 **0.74×**。Q2 / Q1.25 decode 到 33–34，而且这份 llama.cpp 加载不了这两种量化。
- TensorSharp `hunyuan-dense` 没有 fused 整网图。GPU prefill 可以很快，decode 不能按「1.8B + 3080 Ti」外推。
- 本机 llama.cpp 已能跑 GPU（build 10941 + `ggml-cuda.dll`），仍加载不了 Q2_0C / STQ1_0。纯 CPU 请用 `-dev none`，不要只写 `-ngl 0`。
- 不要把带 `--profile` 的结果写进这些表。
