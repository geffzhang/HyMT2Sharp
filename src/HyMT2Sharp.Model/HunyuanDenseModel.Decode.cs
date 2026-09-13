using System.Diagnostics;
using HyMT2Sharp.Gguf;
using HyMT2Sharp.Kernels;

namespace HyMT2Sharp.Model;

public sealed unsafe partial class HunyuanDenseModel
{
    /// <summary>
    /// Decode (1 token) only. GEMV, no pair GEMM, no SiLU→q8, no quantize Barriers.
    /// Pointwise ops stay serial so prefill scheduling experiments cannot tax tg128.
    /// Thread pool is used only for GEMV columns and attention heads.
    /// </summary>
    private void DecodeBlock(float* hidden, int layer, int start)
    {
        const int seq = 1;
        int hiddenSize = Config.HiddenSize;
        float* n1 = (float*)Bump((nuint)((long)seq * hiddenSize * sizeof(float)));
        float* attn = (float*)Bump((nuint)((long)seq * hiddenSize * sizeof(float)));
        RmsSerial($"blk.{layer}.attn_norm.weight", hidden, n1, seq, hiddenSize);
        DecodeAttention(n1, attn, layer, start);
        Ops.AddInPlace(hidden, attn, hiddenSize, pool: null);

        float* n2 = (float*)Bump((nuint)((long)seq * hiddenSize * sizeof(float)));
        RmsSerial($"blk.{layer}.ffn_norm.weight", hidden, n2, seq, hiddenSize);
        float* gate = (float*)Bump((nuint)((long)seq * Config.FfnSize * sizeof(float)));
        float* up = (float*)Bump((nuint)((long)seq * Config.FfnSize * sizeof(float)));
        float* down = (float*)Bump((nuint)((long)seq * hiddenSize * sizeof(float)));
        DecodeGateUp(n2, gate, up, layer);
        long tSilu = ProfileEnabled ? Stopwatch.GetTimestamp() : 0;
        Ops.SiLUMul(gate, up, Config.FfnSize, pool: null);
        if (ProfileEnabled)
            TicksSilu += Stopwatch.GetTimestamp() - tSilu;
        DecodeLinear(gate, $"blk.{layer}.ffn_down.weight", down, Config.FfnSize, hiddenSize);
        Ops.AddInPlace(hidden, down, hiddenSize, pool: null);
    }

    private void DecodeAttention(float* input, float* output, int layer, int start)
    {
        const int seq = 1;
        int heads = Config.NumHeads;
        int kvHeads = Config.NumKvHeads;
        int dim = Config.HeadDim;
        int qDim = heads * dim;
        int kDim = kvHeads * dim;
        float* q = (float*)Bump((nuint)((long)seq * qDim * sizeof(float)));
        float* k = _cacheK[layer] + start * kDim;
        float* v = _cacheV[layer] + start * kDim;
        DecodeQkv(input, q, k, v, layer);

        long tRope = ProfileEnabled ? Stopwatch.GetTimestamp() : 0;
        Ops.NeoXRoPE(q, seq, heads, dim, Config.RopeDim, start, Config.RopeBase, pool: null);
        Ops.NeoXRoPE(k, seq, kvHeads, dim, Config.RopeDim, start, Config.RopeBase, pool: null);
        if (ProfileEnabled)
            TicksRope += Stopwatch.GetTimestamp() - tRope;
        RmsSerial($"blk.{layer}.attn_q_norm.weight", q, q, heads, dim);
        RmsSerial($"blk.{layer}.attn_k_norm.weight", k, k, kvHeads, dim);

        int kvLen = start + seq;
        float scale = 1f / MathF.Sqrt(dim);
        float* sc = (float*)Bump((nuint)((long)heads * seq * kvLen * sizeof(float)));
        long t0 = ProfileEnabled ? Stopwatch.GetTimestamp() : 0;
        Ops.AttentionScores(q, _cacheK[layer], sc, heads, kvHeads, dim, seq, kvLen, qDim, kDim, scale, start, _pool);
        if (ProfileEnabled)
        {
            TicksAttnScore += Stopwatch.GetTimestamp() - t0;
            t0 = Stopwatch.GetTimestamp();
        }

        Ops.SoftmaxCausal(sc, heads, seq, kvLen, start, _pool);
        if (ProfileEnabled)
        {
            TicksSoftmax += Stopwatch.GetTimestamp() - t0;
            t0 = Stopwatch.GetTimestamp();
        }

        float* ao = (float*)Bump((nuint)((long)seq * qDim * sizeof(float)));
        Ops.AttentionCombine(_cacheV[layer], sc, ao, heads, kvHeads, dim, seq, kvLen, qDim, kDim, start, _pool);
        if (ProfileEnabled)
            TicksAttnCombine += Stopwatch.GetTimestamp() - t0;

        DecodeLinear(ao, $"blk.{layer}.attn_output.weight", output, qDim, Config.HiddenSize);
    }

    private void DecodeQkv(float* input, float* q, float* k, float* v, int layer)
    {
        Weight wq = _weights[$"blk.{layer}.attn_q.weight"];
        Weight wk = _weights[$"blk.{layer}.attn_k.weight"];
        Weight wv = _weights[$"blk.{layer}.attn_v.weight"];
        int hidden = Config.HiddenSize;
        int qDim = Config.NumHeads * Config.HeadDim;
        int kDim = Config.NumKvHeads * Config.HeadDim;
        nuint q8Bytes = (nuint)Q8K.RowBytes(hidden);
        BlockQ8K* y = (BlockQ8K*)_gemmScratch.D(q8Bytes);
        Q8K.QuantizeRow(input, y, hidden);
        long t0 = ProfileEnabled ? Stopwatch.GetTimestamp() : 0;
        GemvPrequant(wq, y, q, hidden, qDim);
        GemvPrequant(wk, y, k, hidden, kDim);
        if (ProfileEnabled)
        {
            if (wq.Type == GgmlTensorType.Q2_0C) TicksQ2 += Stopwatch.GetTimestamp() - t0;
            else if (wq.Type == GgmlTensorType.STQ1_0) TicksSTQ += Stopwatch.GetTimestamp() - t0;
            else TicksQ4 += Stopwatch.GetTimestamp() - t0;
        }
        t0 = ProfileEnabled ? Stopwatch.GetTimestamp() : 0;
        if (wv.Type == GgmlTensorType.Q6_K)
        {
            Q6K.GemvPrequant(wv.Q6, y, v, hidden, kDim, _pool);
            if (ProfileEnabled)
                TicksQ6 += Stopwatch.GetTimestamp() - t0;
        }
        else
        {
            GemvPrequant(wv, y, v, hidden, kDim);
                if (ProfileEnabled)
                {
                    if (wv.Type == GgmlTensorType.Q2_0C) TicksQ2 += Stopwatch.GetTimestamp() - t0;
                    else if (wv.Type == GgmlTensorType.STQ1_0) TicksSTQ += Stopwatch.GetTimestamp() - t0;
                    else TicksQ4 += Stopwatch.GetTimestamp() - t0;
                }
        }
    }

    private void DecodeGateUp(float* input, float* gate, float* up, int layer)
    {
        Weight wg = _weights[$"blk.{layer}.ffn_gate.weight"];
        Weight wu = _weights[$"blk.{layer}.ffn_up.weight"];
        int hidden = Config.HiddenSize;
        int ffn = Config.FfnSize;
        nuint q8Bytes = (nuint)Q8K.RowBytes(hidden);
        BlockQ8K* y = (BlockQ8K*)_gemmScratch.D(q8Bytes);
        Q8K.QuantizeRow(input, y, hidden);
        long t0 = ProfileEnabled ? Stopwatch.GetTimestamp() : 0;
        GemvPrequant(wg, y, gate, hidden, ffn);
        GemvPrequant(wu, y, up, hidden, ffn);
        if (ProfileEnabled)
        {
            if (wg.Type == GgmlTensorType.Q2_0C) TicksQ2 += Stopwatch.GetTimestamp() - t0;
            else if (wg.Type == GgmlTensorType.STQ1_0) TicksSTQ += Stopwatch.GetTimestamp() - t0;
            else TicksQ4 += Stopwatch.GetTimestamp() - t0;
        }
    }

    private void DecodeLinear(float* input, string name, float* output, int nIn, int nOut)
    {
        Weight w = _weights[name];
        long t0 = ProfileEnabled ? Stopwatch.GetTimestamp() : 0;
        switch (w.Type)
        {
            case GgmlTensorType.Q4_K:
                MulMatQ4K.Gemv(w.Q4, input, output, nIn, nOut, _pool, _gemmScratch);
                if (ProfileEnabled)
                    TicksQ4 += Stopwatch.GetTimestamp() - t0;
                break;
            case GgmlTensorType.Q2_0C:
            MulMatQ2.Gemv(w.Packed2, w.Q2, input, output, nIn, nOut, _pool, _gemmScratch);
                if (ProfileEnabled)
                    TicksQ2 += Stopwatch.GetTimestamp() - t0;
                break;
            case GgmlTensorType.STQ1_0:
                MulMatSTQ.Gemv(w.PackedSTQ, w.STQ, input, output, nIn, nOut, _pool, _gemmScratch);
                if (ProfileEnabled)
                    TicksSTQ += Stopwatch.GetTimestamp() - t0;
                break;
            case GgmlTensorType.Q6_K:
                Q6K.Gemv(w.Q6, input, output, nIn, nOut, _pool, _gemmScratch);
                if (ProfileEnabled)
                    TicksQ6 += Stopwatch.GetTimestamp() - t0;
                break;
            default:
                throw new NotSupportedException($"{name} type {w.Type} is not a decode GEMV weight.");
        }
    }

    private void GemvPrequant(Weight w, BlockQ8K* x, float* y, int nIn, int nOut)
    {
        if (w.Type == GgmlTensorType.Q2_0C)
            MulMatQ2.GemvPrequant(w.Packed2, w.Q2, x, y, nIn, nOut, _pool);
        else if (w.Type == GgmlTensorType.STQ1_0)
            MulMatSTQ.GemvPrequant(w.PackedSTQ, w.STQ, x, y, nIn, nOut, _pool);
        else
            MulMatQ4K.GemvPrequant(w.Q4, x, y, nIn, nOut, _pool);
    }

    private void RmsSerial(string name, float* x, float* y, int rows, int dim)
    {
        Weight w = _weights[name];
        long t0 = ProfileEnabled ? Stopwatch.GetTimestamp() : 0;
        Ops.RmsNorm(x, w.F32, y, rows, dim, Config.Eps, pool: null);
        if (ProfileEnabled)
            TicksRms += Stopwatch.GetTimestamp() - t0;
    }
}
