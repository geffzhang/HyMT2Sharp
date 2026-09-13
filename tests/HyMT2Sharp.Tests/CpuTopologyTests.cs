using Sdcb.HyMT2Sharp.Kernels;

namespace Sdcb.HyMT2Sharp.Tests;

public sealed class CpuTopologyTests
{
    [Fact]
    public void PreferPCoreCount_IsBetweenOneAndProcessorCount()
    {
        int n = CpuThreadPool.PreferPCoreCount();
        Assert.InRange(n, 1, Environment.ProcessorCount);
        Assert.Equal(n, CpuTopology.PCoreLeaders.Count);
        Assert.True(n <= CpuTopology.PCoreLogicalIds.Count);
    }

    [Theory]
    [InlineData("0-3", new[] { 0, 1, 2, 3 })]
    [InlineData("0,2,4", new[] { 0, 2, 4 })]
    [InlineData("0-1,8-9", new[] { 0, 1, 8, 9 })]
    [InlineData("  4-5,10  ", new[] { 4, 5, 10 })]
    public void ParseCpuList_ExpandsRanges(string text, int[] expected)
        => Assert.Equal(expected, CpuTopology.ParseCpuList(text));
}
