# HyMT2Sharp Q2 性能优化手册

这篇文档写给之后接手 HyMT2Sharp 的工程师或 AI。

目标不是背下某一段 AVX2 代码。

目标是知道应该先测什么、应该先证明什么、哪些地方改错后会同时损坏性能和正确性。

本文以 Ryzen 7 5800X、Windows、8 线程、AVX2 的当前开发环境为准。

当前模型是 `Hy-MT2-1.8B-2Bit.gguf` 和 `Hy-MT2-1.8B-Q4_K_M.gguf`。

当前模型结构是 `hunyuan-dense`、32 层、hidden 2048、attention heads 16/4、head dim 128、FFN 6144。

Q2 权重使用 `Q2_0C`，Q4 权重使用 `Q4_K`，部分权重使用 Q6，norm 使用 F32。

这篇文档记录的是可复用的方法和已经验证过的陷阱。

它不是对任何其他 CPU、线程数或模型尺寸的性能承诺。

## 1. 先读结论

Q2 性能优化的最大收益来自数据布局，而不是某一条神奇指令。

逐行展开 Q2 权重会放大内存占用，也会让 GEMM 失去连续访问优势。

把 8 个输出行重排成一个紧凑 panel，才能让一个 activation 广播同时服务多个输出列。

Q2 的 bit 操作必须先用小型参考实现证明，再写 AVX2 热核。

Q2 的一个 512-value 权重块只有一个 scale，两个 256-value activation block 共享这个 scale。

`nIn / 512` 是 Q2 block 数，`nIn / 256` 是 Q8 block 数，这两个数字不能混用。

Q8_Kx4 的 `Bsums` 不是简单的四行连续数组，必须按实际布局读取。

short 累加器暂时不溢出，不等于最后的水平归约也不会溢出。

prefill 和 decode 是两种不同的问题，不能为了 prefill 改掉 decode 的调度和内存路径。

QKV、gate/up、SiLU→down 可以共享 Q8 activation，但共享必须用屏障保证所有写入完成。

线程池、scratch、panel 指针和尾部处理属于公共基础设施，改动它们要比改一个局部循环更谨慎。

每一次性能改动都必须同时回答三个问题：快了多少、结果是否仍然对、Q4/decode 是否回归。

## 2. 当前基线和测量纪律

正式 benchmark 使用 Release 配置。

正式 benchmark 使用固定线程数，当前机器建议显式写 `--threads 8`。

正式 benchmark 使用 512 prefill tokens 和 128 decode tokens。

正式 benchmark 命令如下：

```powershell
dotnet run --project src/HyMT2Sharp.Benchmark -c Release --no-build -- `
  --model "D:\_\model\Hy-MT2-1.8B-2Bit.gguf" `
  --bench-prefill 512 --bench-decode 128 --threads 8
```

Q4 对照命令只替换模型路径：

```powershell
dotnet run --project src/HyMT2Sharp.Benchmark -c Release --no-build -- `
  --model "D:\_\model\Hy-MT2-1.8B-Q4_K_M.gguf" `
  --bench-prefill 512 --bench-decode 128 --threads 8
```

CLI 会先 warmup 一次长 prefill。

CLI 还会 warmup 一次单 token forward。

之后才开始三次 prefill 计时。

decode 计时在重新跑过 512 token prompt 后连续执行 128 个 token。

这套顺序是为了接近 `llama-bench` 的 warmup 语义。

不要拿冷启动的第一次 Forward 和 llama.cpp 的热循环均值比较。

不要把带 `--profile` 的结果当作正式峰值。

profile 会增加计时读取和分支，适合定位瓶颈，不适合宣布性能。

当前工作区曾测到 Q2 prefill 约 442 tok/s、decode 约 44 tok/s。

同一台 5800X 的其他轮次也出现过约 470/41 和约 434/43。

最近一次连续复测记录如下：

| 模型 | prefill 512 | decode 128 |
| --- | ---: | ---: |
| HyMT2Sharp Q4_K_M | 332.05 tok/s | 23.91 tok/s |
| HyMT2Sharp Q2_0C | 441.98 tok/s | 44.25 tok/s |
| llama.cpp Q4_K_M | 254.93 ± 3.10 tok/s | 27.39 ± 0.37 tok/s |

这张表用于提供优化前后的量级，不应被当成每次运行的固定结果。

Q4 也会因温度、频率和后台负载出现明显变化。

README 中的数字应被理解为实测记录，而不是硅片的固定上限。

如果要比较一个改动，优先在同一轮次交替运行旧版本和新版本。

如果只能运行一次，至少记录每次三轮 prefill 的 reps。

如果三轮差异很大，先处理机器状态，再解释代码收益。

不要用一次异常高分决定默认 tile、线程数或算法方向。

## 3. Ryzen 7 5800X 的特殊注意点

5800X 是 Zen 3，不能套用其他 CPU 的核心分组或线程亲和性假设。

不能把面向另一种核心拓扑的线程亲和性假设直接搬到 5800X。

`CpuThreadPool.PreferPCoreCount()` 读取操作系统拓扑，只统计最高性能那一档的物理核（不含 SMT 兄弟线程和 E-core）。5800X 没有 E-core，因此自动值是 8。

因此在 5800X 上通常应该用显式 `--threads 8` 做可比测试。

Windows 调度、SMT、温度墙和后台任务都会改变短 benchmark 的分数。

性能分析必须分辨“代码变快”和“CPU 当时跑得更快”。

如果改动影响线程池，先比较同一线程数下的 Q2 和 Q4。

只让 Q2 使用新的 tile 或 panel，不要无意中改变 Q4 的默认参数。

## 4. 先理解整网的工作分解

prefill 的主要矩阵形状是多个 token 同时进入线性层。

这时 activation 可以量化成 Q8_Kx4，并被四个 token 的计算共同使用。

decode 每次只有一个 token。

decode 更像多个输出行的 GEMV，常常受权重读取和线程调度影响。

prefill 更像 GEMM，适合 output panel、activation panel 和 tile。

Q2 prefill 的关键路径包括 attention QKV。

关键路径还包括 FFN 的 gate/up。

关键路径还包括 SiLU 后的 down 投影。

QKV 的三个矩阵具有同一个输入 activation。

gate/up 的两个矩阵也具有同一个输入 activation。

因此输入 Q8 量化可以从三次减少为一次，或者从两次减少为一次。

down 的输入是 `silu(gate) * up`，不能复用 gate/up 的 Q8 数据。

但 SiLU、Q8 量化和 down GEMM 可以放在同一个并行区域。

Q2 权重主要通过 `MulMatQ2` 和 `Q2Panel` 进入这些路径。

模型级分发在 `HunyuanDenseModel.cs`。

prefill 专用调用在 `HunyuanDenseModel.Prefill.cs`。

decode 专用调用在 `HunyuanDenseModel.Decode.cs`。

先画出调用链，再决定修改哪一层。

## 5. Q2_0C 的真实格式

`BlockQ2_0C` 的大小是 130 bytes。

布局是一个 16-bit half scale 加上 128 bytes 的 packed codes。

一个 Q2 block 表示 512 个权重。

每个 packed byte 包含四个 2-bit code。

code 的数值范围是 0、1、2、3。

反量化值是：

```text
value = (code * 2 - 3) * D
```

所以四个 code 对应的整数值是 -3、-1、1、3。

前 64 bytes 的 Qs 表示前 256 个输入值。

后 64 bytes 的 Qs 表示后 256 个输入值。

不要把 Q2 的 128 个 packed bytes 当成 128 个权重。

不要把一个 byte 的四个 code 当成自然排列的四个输出列。

GGUF 中矩阵通常是行主序的。

单个输出行 `r` 的第 `b` 个 block 位于 `src + r * nb + b`。

这里的 `nb` 是 `nIn / Q2_0C.BlockLength`。

Q2 的 `BlockLength` 是 512，而不是 Q4/Q8 的 256。

加载时必须保留原始 Q2 行数据。

原始行数据用于尾部列、decode GEMV 和参考计算。

## 6. 曾经踩过的 Q2 正确性坑

旧实现曾经只对第一行调用 `Q2_0C.Expand()`。

后续行没有得到自己的展开数据，导致输出行读取错误。

这种 bug 可能在第一个输出行看起来完全正常。

不能只用一个输出列或一个固定 token 做验证。

旧实现还给每个 Q2 权重创建一个 byte-per-weight 的展开副本。

这会让一个约 600 MB 的 Q2 文件额外膨胀到非常大的 native buffer。

扩大内存并不能保证更快，因为工作集会离开 cache。

正确的策略是保留紧凑原始 Q2，并创建 8 列 panel。

重排时必须验证所有输出行，而不是只验证 panel 的第一列。

测试数据必须让不同输出行拥有不同 D。

测试数据还必须让同一行的不同 512 block 拥有不同 D。

否则“错误地复用第一个 block 的 scale”会被测试掩盖。

当前测试 `Q2_PackedPanelMatchesRows` 特意覆盖了这个场景。

## 7. 为什么要做 BlockQ2x8 panel

`BlockQ2x8` 的大小是 1056 bytes。

它包含 8 个 float scale，共 32 bytes。

它还包含 1024 bytes 的 packed Q2 panel data。

一个 panel 对应连续 8 个输出行和一个 512-value 输入 block。

多个 panel 在内存中按输出 group、输入 block排列。

第 `g` 个输出 group、第 `b` 个输入 block 的地址是 `dst + g * nb + b`。

`RepackQ2.Rows()` 负责从行主序 Q2 转换为 panel。

重排的核心单位是 32 个 K 值。

32 个 K 值可以拆成四组各 8 个 K 值。

四组的 2-bit code 被合并到一个 byte 的四个 bit pair 中。

一个 64-byte panel chunk 保存 8 个列的 8-byte 小片段。

这样 AVX2 可以加载 32 个 bytes，并用一个 activation 广播参与多个列的计算。

panel 只改变存取顺序，不改变 Q2 的量化值。

不要在 panel 中把 code 预先改成 signed weight，除非重新证明内存和吞吐收益。

保留 2-bit 形式可以减少 panel 大小，也避免第二次展开。

## 8. RepackQ2 的验证方法

先写一个纯标量 `RepackQ2.Rows()`，不要一开始就写 SIMD repack。

用随机 Qs 填充原始行。

对每个输出行、每个 K 值，比较原始 `Value()` 和 panel 解码值。

比较时必须覆盖所有 512 个 K 值。

不要只检查每个 byte 的低 2 bits。

要检查四个 plane 的高位 pair。

要检查两个 256-value half。

要检查多个输入 block。

要检查 8 列边界。

然后再让 panel GEMV 和原始行 GEMV 对拍。

最后再让 panel GEMM 和逐 token 的原始行点积对拍。

测试输入应包含正数、负数、零和接近量化边界的值。

随机测试要固定 seed，失败时才能复现。

不要用全零权重作为唯一测试，因为 scale、code 和 correction 都可能被绕过。

## 9. Q2 热核的数学关系

Q2 code 的乘积可以拆成两部分：`2 * code * activation - 3 * activation`。

第一部分可以用 `vpmaddubsw` 计算 packed code 与 signed activation 的乘积。

第二部分只需要 activation 的总和。

Q8 activation block 已经保存了 `Bsums`，可以用于第二部分。

因此每个输出列的整数 dot 可以写成：

```text
dot = 2 * sum(code * q8) - 3 * sum(q8)
```

最后再乘权重 scale 和 activation scale。

`Q2Panel.Gemv()` 使用相同的数学拆分。

`Q2Panel.Gemm()` 对四个 token 行并行执行相同计算。

panel 的列顺序经过水平相加后是 `[0, 1, 4, 5, 2, 3, 6, 7]`。

`ColumnOrder` permutation 用于将它恢复为自然输出顺序。

如果修改 `RowSum()`，必须同步检查 store 前的 permutation。

不要只看某一列数值，因为 permutation 错误可能只是列错位。

## 10. int16 溢出是两层问题

在一个短累加器中，64 个 product 的最大绝对值大约是 `64 * 3 * 128 = 24576`。

这个数在 int16 范围内。

但一个短 lane 后续还会参与 pair reduction 和 horizontal add。

多个 24576 相加很快就超过 32767。

因此不能先用 int16 做完整水平归约，再转换为 int32。

正确顺序是先用 `vpmaddwd` 或等价操作扩成 int32。

扩成 int32 后再进行跨 lane 的水平加法。

`TotalSums()` 也必须在足够宽的类型中完成最终行总和。

`Q2_PackedPanelDoesNotOverflowInt16` 使用全 1 activation 和全 3 权重覆盖极端场景。

只用普通随机数据很难稳定触发溢出。

测试中应断言理论值，而不是只断言结果是有限数。

如果结果变成负数或突然缩小一半，优先检查 int16 wraparound。

## 11. Q8_Kx4 的布局不能凭名字猜

`BlockQ8Kx4` 包含 4 个 float D。

它还包含 1024 个 interleaved signed Qs。

它还包含 64 个 short Bsums。

四个 token 的 Qs 是交错布局，不是四个 `BlockQ8K` 简单拼接。

`Quantize4x8()` 生成的布局必须和 Q2/Q4 panel kernel 的读取顺序一致。

Q2 的一个 512 block 要消费两个 Q8 256 block。

所以 Q2 GEMM 中的 activation 行地址通常是 `rows[b]`，其中 `b` 遍历 `nb * 2`。

Q2 prefill 的 q8 group 地址是 `q8 + g * q8Blocks`。

这里 `q8Blocks = nIn / Qk.SuperBlock`。

不要写成 `g * nb * 2`，除非你已经明确证明它等价且不会被维度改动破坏。

当前代码曾经出现过这个 scratch size/index 混淆，必须把它当作高危区域。

`Bsums` 的每一行四段位于 `row*4`、`row*4+16`、`row*4+32`、`row*4+48`。

读取总和时要按这个布局累加四段。

不能假设 `Bsums[row*16 ...]` 是自然排列。

修改 quantizer 时要同时更新 Q2、Q4、Q6 使用者的布局假设。

## 12. 权重 scale 的边界

一个 `BlockQ2x8` 的 8 个 D 对应 8 个输出列。

同一个 panel 的两个 256 输入半块共享这 8 个 D。

因此 GEMM 的 `b=0` 和 `b=1` 必须读取同一个 `w[0].D`。

`b=2` 和 `b=3` 必须读取 `w[1].D`。

错误地把 `w[0].D` 用于所有 b，会在单个 512 block 测试中看不出来。

跨 block scale 测试是必须的。

activation 的 `D[0..3]` 则分别属于四个 token。

权重 scale 和 activation scale 不能互换。

half scale 转 float 要使用可靠的 half conversion。

不要把 fixed buffer 的地址当成普通托管数组来处理。

特别不要把 `&x[b].D` 随意转成 float 数组指针。

fixed struct 字段的地址、元素 stride 和 JIT 生成的访问方式都要实际验证。

直接使用 `x[b].D[token]` 读取 fixed buffer 元素最安全。

## 13. Q2 GEMV 和 GEMM 要分开思考

decode 的 token 数是 1。

decode 应该走 `Q8K.QuantizeRow()` 加 Q2 GEMV。

decode 不应该为了复用 prefill panel 而创建四行 Q8 scratch。

decode 也不应该强制经过 prefill barrier。

`MulMatQ2.Gemv()` 会量化一行 activation，然后进入 `GemvPrequant()`。

当输出列数能被 8 整除且有 panel 时，使用 `Q2Panel.Gemv()`。

输出列尾部使用原始 Q2 行的 `Dot()`。

prefill 的 token 数大于 1 时，才进入 Q8Kx4 GEMM。

如果 token 数不能被 4 整除，可以 padding 到下一个 4 的倍数。

padding 输入必须清零。

padding 输出必须写入 scratch，不能直接越界写用户输出。

最后只能拷贝真实 token 的输出。

输出列不能被 8 整除时，panel 主路径处理完整 group，尾列走逐行参考路径。

尾部处理慢一些是可以接受的，错误的尾部结果不能接受。

## 14. 快路径条件要写完整

Q2 fused prefill 需要 AVX2。

Q2 fused prefill 需要 panel 指针非空。

Q2 fused prefill 需要输入维度是 512 的倍数。

Q2 fused prefill 需要 token 数是 4 的倍数。

Q2 panel 输出维度需要是 8 的倍数。

QKV 的 q、k、v 输出维度要分别满足 panel 条件。

gate/up 的 FFN 输出维度要满足 panel 条件。

down 的输出维度也要满足 panel 条件。

不满足条件时必须回到通用路径。

不能因为当前 1.8B 模型恰好满足条件，就删除 fallback。

不能让非 AVX2 CPU 进入 AVX2 intrinsic。

不要用 `Type == Q2_0C` 代替 `Packed2 != null` 检查。

原始 GGUF 可能有小矩阵或奇数输出列，它们没有完整 panel。

`ValidatePanel()` 应在进入共享 fused API 前拒绝非法 descriptor。

## 15. 共享量化和屏障

QKV 使用一个 q8 buffer，然后连续运行三个 Q2 panel GEMM。

gate/up 使用一个 q8 buffer，然后连续运行两个 Q2 panel GEMM。

这两个场景不应分别提交三次或两次独立的 quantization job。

共享 API 当前由 `Q2PanelWeight` 描述 panel 指针、输出指针和列数。

`QuantizeAndGemm()` 共享普通输入的 Q8 quantization。

`SiluQuantizeAndGemm()` 共享 SiLU、Q8 quantization 和 down GEMM 的调度区域。

并行区域先按 token group 分配量化工作。

所有 worker 到达 `pool.Barrier()` 后，才允许开始 GEMM。

只有部分 worker 完成量化时，不能让任何 worker 读取未写完的 q8 block。

Barrier 使用的是当前 job 的 worker 数，不是整个线程池总数。

`pool.For(count, body)` 中每个参与 worker 都必须执行同一个 barrier 次数。

某个分支提前跳过 barrier，而另一个分支仍等待，会造成死锁。

在 barrier 前不能抛出未处理异常。

共享 API 必须让空的第二、第三矩阵安全返回。

矩阵 descriptor 的 `NOut=0` 是“没有矩阵”，不是一个空指针可以随便解引用。

## 16. 线程分工的经验

线程数不是越大越快。

线程池提交、唤醒、自旋和 barrier 都有固定成本。

token group 数太少时，按 token 分工可能无法填满线程。

output group 数太少时，按列分工也会产生空 worker。

当前 fused API 使用 token group 数和 output group 数的最大值决定 job 数。

这样量化和 GEMM 都能获得一定并行度。

但 job 数过大也会增加调度边界。

不要把每个 8 列 panel 都提交成一个独立线程任务。

使用连续的 `begin/end` 范围，让一个 worker 处理多个 panel。

不要让 decode 进入 prefill 专用 pair/triple API。

decode 的 GEMV 线程分工和 prefill 的 panel 分工应保持隔离。

修改 `CpuThreadPool` 时必须同时跑 Q2 prefill 和 decode。

不要只看 prefill 变快后就接受线程池改动。

如果 decode 掉到原有水平以下，优先撤销公共线程池改动。

## 17. Tile 选择方法

Q2 panel tile 控制一次处理多少输出 group。

tile 太小会增加循环和函数调用边界。

tile 太大会让权重工作集挤出 L2，并增加 cache miss。

当前 Q2 默认 `ColTileBytes` 是 64 KiB。

Q4 默认 tile 是独立的 128 KiB。

不要因为 Q4 的 tile 更大，就直接复制给 Q2。

Q2 一个 panel 是 1056 bytes 乘以输入 block 数。

实际 group 数应由 `ColTileBytes / (nb * sizeof(BlockQ2x8))` 计算。

必须使用 `Math.Max(1, ...)` 防止 tile 变成 0。

可以用 `--q2-col-tile-kb` 做 A/B：

```powershell
dotnet run --project src/HyMT2Sharp.Benchmark -c Release --no-build -- `
  --model "D:\_\model\Hy-MT2-1.8B-2Bit.gguf" `
  --bench-prefill 512 --bench-decode 128 --threads 8 `
  --q2-col-tile-kb 64
```

至少比较 32、64、128、256 KiB。

每个设置至少跑三轮 prefill。

同时记录 decode，哪怕 tile 理论上只影响 prefill。

默认值只能根据重复测量决定。

某个 tile 在一轮中领先 2% 不足以说明它普遍更好。

## 18. RyuJIT 和 intrinsic 的经验

C# intrinsic 的源码形状不等于最终机器码形状。

RyuJIT 可能因为活跃变量太多而 spill 到栈。

也可能因为 helper、边界检查或 no-inline 改变而降低吞吐。

Q2 热核要保持累加器数量和生命周期可控。

第一组 product 可以直接初始化累加器，避免对零向量做多余 add。

后续组再执行加法。

不要凭汇编“看起来更短”判断更快。

必须用真实模型 benchmark 验证。

小型 microbenchmark 可以辅助定位，但不能替代整网结果。

`NoInlining` 可能减少某个函数的代码大小，也可能阻止有益的常量传播。

`AggressiveOptimization` 也不是性能保证。

不要把所有 helper 都标成 `NoInlining` 或 `AggressiveInlining`。

先对比默认 JIT，再对比一个局部属性改动。

如果使用 JIT dump，环境变量只对诊断命令生效。

诊断完成后清除 `DOTNET_JitDisasm` 等变量。

残留的 JIT dump 会污染 benchmark 输出和耗时。

5800X 不支持 AVX-512，不能为它设计依赖 AVX-512 的主路径。

当前运行时显示 `vnni=False`，不要假设 VNNI 可用。

未来若添加 VNNI，必须保留 AVX2 路径并做独立 dispatch。

## 19. Q4 保护线

用户要求 Q4 性能和正确性不能劣化。

因此 Q2 优化首先应避免修改 Q4 热核文件。

Q4 相关文件包括 `GemmQ4K.Avx2.cs`、`GemmQ4K.cs` 和 `MulMatQ4K.cs`。

Q4 的 panel、meta、Q4 scale、Q4 dmin 都有自己的布局。

不要为了复用 Q2 的 helper 把 Q4 数据结构强行统一。

可以复用概念，例如 Q8 quantization 和 thread pool。

不能复用未经证明相同的 pointer stride 或 correction 公式。

Q2 修改 `CpuThreadPool`、`ScratchArena` 或 `QuantizeQ8Kx4` 时必须跑 Q4 benchmark。

Q4 的最新实测受 5800X 状态影响很大，所以要比较同一机器状态和多轮 reps。

如果 Q4 明显回退，先检查共享基础设施，而不是继续优化 Q2 热核。

Q4 生成结果也要检查，不能只检查 tok/s。

## 20. ScratchArena 的注意事项

`ScratchArena` 提供 A 到 F 多个可复用 buffer。

同一 Forward 的不同阶段可能依赖不同 buffer 的内容仍然有效。

不要为了省一次分配，把两个仍在使用的数组映射到同一个 scratch slot。

Q2 prefill activation 通常使用 E 或 A，具体以调用路径为准。

padding input 和 padding output 必须使用独立空间。

输出 scratch 大小应按 `paddedTokens * nOut * sizeof(float)` 计算。

q8 scratch 大小应按 `(tokens / 4) * q8Blocks * Q8Kx4Size` 计算。

不要按 Q2 block 数计算 Q8 buffer 大小。

scratch 只保证 native memory 生命周期，不保证数据自动清零。

需要清零的 padding 区域必须显式 `NativeMemory.Clear()`。

不要把上一次矩阵的尾部数据当成当前 padding 的零。

如果使用 `Buffer.MemoryCopy`，同时检查 source bytes 和 destination capacity。

尾部拷贝只拷贝真实 token 数，不要把 padding 输出暴露给上层。

## 21. 测试必须覆盖的层次

第一层是结构大小测试。

当前应断言 `BlockQ2_0C` 是 130 bytes。

当前应断言 `BlockQ2x8` 是 1056 bytes。

当前应断言 `BlockQ8Kx4` 是 1168 bytes。

第二层是 Q2 scalar 与 AVX2 dot 对拍。

第三层是 Q2 panel 与原始行 dot 对拍。

第四层是跨多个输入 block 的 panel 对拍。

第五层是 int16 极值测试。

第六层是非 4 倍 token 的 padding/tail 测试。

第七层是非 8 倍输出列的 tail 测试。

第八层是 fused pair/triple API 的多矩阵测试。

`Q2_QuantizeAndGemmMatchesRows` 应覆盖真实 Q8 quantization。

测试矩阵要让两个 fused 输出拥有不同的 scale 和 code。

这样能发现 q8 指针复用和输出指针错位。

测试失败时先缩小到 kernel，再回到模型。

不要用端到端 logits 差异代替 kernel 对拍。

量化、RoPE、attention 浮点归约都可能使 batch 和 serial 的结果略有差异。

## 22. 端到端正确性检查

已知 prompt 是 `Translate the following segment into Chinese, without additional explanation：Hello`。

当前 Q2 和 Q4 都应输出「你好」。

这项检查能发现模型加载、行索引、cache 和最终输出层的严重错误。

它不能证明每个输出 logit 都逐位相同。

`--verify-prefill N` 可以比较 batch prefill 与 serial decode 的最后 logits。

当前 Q4 自身也可能存在可观的 batch/serial 数值差异。

因此 verify 结果要看 max abs、RMS 和 top token 一起判断。

如果 top token 变化，先做 kernel 级别对拍。

不要因为 max abs 非零就认定 Q2 kernel 一定错。

也不要因为 top token 没变就跳过尾部和 scale 测试。

## 23. profile 的正确用法

CLI 的 `--profile` 会输出 Q2、Q4、Q6、attention、RMS、RoPE 和 SiLU 分项。

Q2 prefill 重点看 `q2` 和总耗时的关系。

如果 q2 只占总耗时一小部分，继续抠 Q2 热核的收益会很有限。

如果 q2 占主要部分，才值得继续研究 panel、tile 或 JIT。

Q2 decode profile 可以确认 GEMV 是否占主要时间。

attention score、softmax 和 attention combine 不能被误算成 Q2 矩阵时间。

Profile counter 使用 `Stopwatch.GetTimestamp()`。

计时范围必须从调用前开始，到调用后立即结束。

不要把 warmup 或模型加载计入某个量化 kernel 的 counter。

共享量化 API 的计时应包括量化和 GEMM，或明确拆分，不能前后版本口径不同。

修改 profile 输出时同步修改 accounted 总和。

否则读者会误以为某个阶段没有耗时。

## 24. 推荐的优化工作流

第一步，确认当前分支和工作区状态。

第二步，先跑 22/22 测试和一次 Q2/Q4 生成。

第三步，记录不带 profile 的 Q2/Q4 benchmark。

第四步，打开 profile 找到最大耗时区段。

第五步，先写标量参考或小型单元测试。

第六步，只改一个数据布局或一个循环策略。

第七步，先跑 kernel 测试。

第八步，再跑 Release build。

第九步，跑同一机器上的 Q2 prefill/decode。

第十步，跑 Q4 prefill/decode 作为护栏。

第十一步，跑两个模型的已知翻译 prompt。

第十二步，检查 `git diff --check`。

第十三步，记录 reps、线程数、模型路径和是否带 profile。

如果一个改动同时改变了 panel、线程池和 quantizer，无法解释收益或回归来源。

应拆成多个小改动，或至少在本地做独立 A/B。

## 25. 不要做的事情

不要看到 Q2 更小就默认它一定更快。

不要把 Q2 2-bit code 展开成 sbyte 后再声称保留了紧凑路径。

不要只优化 GEMV，然后用它推断 GEMM 性能。

不要只优化 prefill，然后跳过 decode benchmark。

不要用其他 CPU 的线程模型解释 5800X 的结果。

不要用一次高分替换三次 reps。

不要为了看起来像 llama.cpp 而照抄不适用于 C# JIT 的循环嵌套。

不要默认 `NoInlining` 会减少 spill。

不要默认增加 worker 数会增加吞吐。

不要让 barrier 的任意一侧提前 return。

不要把 Q2 的 `nb` 和 Q8 的 `q8Blocks` 混在同一个变量里。

不要假设 Bsums 是自然行序。

不要把 block scale 当成 per-half scale。

不要在未加跨 block scale 测试前改 scale 加载。

不要在没有极值测试前改 int16/int32 归约。

不要在没有 tail 测试前删除 fallback。

不要在没有 Q4 对照前改公共线程池或 scratch。

不要把调试环境变量带进最终 benchmark。

## 26. 值得继续研究的方向

可以继续研究 Q2 panel 的更细 tile，但必须针对 5800X 重新测量。

可以研究 activation correction 的更深缓存复用，但要先确认寄存器压力没有增加。

可以研究按多个 output panel 批量预取，但必须比较真实模型而不是随机 microbenchmark。

可以研究 Q2 GEMV 的输出列分块和 cache 访问。

可以研究 Q8 quantization 与后续 attention 的内存生命周期。

可以研究在支持 VNNI 的其他 CPU 上增加独立 Q2 dispatch。

可以研究更精细的线程数选择，但不能用其他 CPU 的规则覆盖 Zen 3。

可以研究将 Q2 panel 在模型加载时持久化，减少启动时 repack 成本。

持久化 panel 时要记录格式版本和原始 GGUF 校验信息。

可以研究更准确的端到端 batch/serial 数值误差评估。

这些方向都不应先于当前 Q2/Q4 correctness 护栏。

## 27. 代码审查清单

修改 `BlockLayout.cs` 后，是否更新了结构大小测试？

修改 `RepackQ2.cs` 后，是否测试了所有 code plane？

修改 `Q2Panel.cs` 后，是否检查了 column permutation？

修改 scale 读取后，是否测试了多个输入 block？

修改 correction 后，是否跑了极值 int16 测试？

修改 q8 指针后，是否明确区分 Q2 block 数和 Q8 block 数？

修改 padding 后，是否测试了 3 token 和 10 output columns？

修改 fused API 后，是否测试了 pair 和 triple 两种矩阵数？

修改 `CpuThreadPool` 后，是否检查 barrier 对称性？

修改 `ScratchArena` 后，是否检查 A-F buffer 的生命周期？

修改模型分支后，是否确认非 AVX2/非整齐维度仍能 fallback？

修改公共 kernel 后，是否测 Q4 prefill/decode？

修改 decode 文件后，是否测 Q2 decode 和 Q4 decode？

任何 benchmark 变化是否有对应的命令和 reps 记录？

README 中是否只写当前硬件和当前版本的结论？

是否删除了临时 JIT dump、调试输出和环境变量？

## 28. 交接给下一个 AI 时应提供什么

提供当前分支名。

提供 `git status --short`。

提供模型文件名和模型大小。

提供 CPU、线程数、AVX2/VNNI 能力。

提供不带 profile 的 Q2/Q4 三轮 benchmark。

提供带 profile 的一轮分解数据。

提供测试总数和结果。

提供已知 prompt 的实际输出。

提供改动文件列表。

明确说明是否修改了 Q4 文件。

明确说明是否修改了线程池、quantizer、scratch 或 layout。

明确说明任何尚未验证的假设。

不要只交接“现在是 470 tok/s”这一句话。

没有硬件、命令、reps 和 correctness 上下文，这个数字不可复核。

## 29. 最小验证命令集

提交前至少运行：

```powershell
dotnet test tests/HyMT2Sharp.Tests -c Release --no-restore
dotnet build src/HyMT2Sharp.Cli -c Release --no-restore
dotnet build src/HyMT2Sharp.Benchmark -c Release --no-restore
dotnet run --project src/HyMT2Sharp.Benchmark -c Release --no-build -- `
  --model "D:\_\model\Hy-MT2-1.8B-2Bit.gguf" `
  --bench-prefill 512 --bench-decode 128 --threads 8
dotnet run --project src/HyMT2Sharp.Benchmark -c Release --no-build -- `
  --model "D:\_\model\Hy-MT2-1.8B-Q4_K_M.gguf" `
  --bench-prefill 512 --bench-decode 128 --threads 8
dotnet run --project src/HyMT2Sharp.Cli -c Release --no-build -- `
  --model "D:\_\model\Hy-MT2-1.8B-2Bit.gguf" --prompt "Translate the following segment into Chinese, without additional explanation：Hello" --max-tokens 16
dotnet run --project src/HyMT2Sharp.Cli -c Release --no-build -- `
  --model "D:\_\model\Hy-MT2-1.8B-Q4_K_M.gguf" --prompt "Translate the following segment into Chinese, without additional explanation：Hello" --max-tokens 16
git diff --check
```

如果修改了 batch 语义，再增加：

```powershell
dotnet run --project src/HyMT2Sharp.Benchmark -c Release --no-build -- `
  --model "D:\_\model\Hy-MT2-1.8B-2Bit.gguf" --verify-prefill 16 --threads 8
```

`verify-prefill` 的差异需要结合 Q4 对照和 kernel tests 解读。

## 30. 最后的判断标准

性能优化不是让某次 benchmark 输出一个更大的数字。

性能优化是让数字在明确的命令和硬件上可复现。

性能优化是让 Q2 panel 的每一个 byte 都有可解释的来源。

性能优化是让每一个 SIMD reduction 都有足够宽的数值类型。

性能优化是让每一个 barrier 都有对称的到达路径。

性能优化是让 prefill 的收益不以 decode 或 Q4 回归为代价。

性能优化是让尾部、fallback、模型输出和单元测试都仍然正确。

如果无法解释一个改动为什么快，先不要把它设为默认。

如果无法证明一个改动为什么对，先不要把它合并。

如果 Q2 只是偶尔比 Q4 快，先查测量纪律和 CPU 状态。

如果 Q2 稳定比 Q4 快，也仍然要保留 Q4 对照。

最可靠的下一步通常是一个小的、可回滚、带测试的实验。
