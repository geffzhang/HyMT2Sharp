using Sdcb.HyMT2Sharp.Kernels;

namespace Sdcb.HyMT2Sharp.Tests;

public sealed class CpuTopologyTests
{
    [Fact]
    public void PreferPCoreCount_IsBetweenOneAndProcessorCount()
    {
        int n = CpuThreadPool.PreferPCoreCount();
        Assert.InRange(n, 1, Environment.ProcessorCount);
        Assert.True(n <= Math.Max(1, CpuTopology.PCoreLeaders.Count));
        Assert.True(n <= CpuTopology.PCoreLogicalIds.Count || CpuTopology.PCoreLogicalIds.Count == 0);
        Assert.Equal(n, CpuTopology.PreferPCoreCount);
        Assert.True(CpuTopology.PCoreLeaders.Count <= CpuTopology.PhysicalLeaders.Count);
    }

    [Theory]
    [InlineData("0-3", new[] { 0, 1, 2, 3 })]
    [InlineData("0,2,4", new[] { 0, 2, 4 })]
    [InlineData("0-1,8-9", new[] { 0, 1, 8, 9 })]
    [InlineData("  4-5,10  ", new[] { 4, 5, 10 })]
    public void ParseCpuList_ExpandsRanges(string text, int[] expected)
        => Assert.Equal(expected, CpuTopology.ParseCpuList(text));

    [Theory]
    [InlineData(16, 8, 8, true, false, 8)]
    [InlineData(32, 24, 8, true, false, 8)]
    [InlineData(32, 24, 8, true, true, 8)]
    [InlineData(4, 4, 4, false, true, 2)]
    [InlineData(4, 2, 2, true, true, 2)]
    [InlineData(4, 4, 4, false, false, 4)]
    [InlineData(8, 8, 8, false, true, 4)]
    [InlineData(3, 3, 3, false, true, 3)]
    [InlineData(16, 0, 0, false, false, 8)]
    [InlineData(4, 0, 0, false, false, 4)]
    [InlineData(4, 0, 0, false, true, 2)]
    [InlineData(1, 1, 1, false, false, 1)]
    public void ChooseWorkerCount_UsesPCores(
        int logical, int physical, int pCores, bool sawSmt, bool virtualMachine, int expected)
        => Assert.Equal(expected, CpuTopology.ChooseWorkerCount(logical, physical, pCores, sawSmt, virtualMachine));

    [Fact]
    public void CountPCores_UsesSmtMixAsHybridPSet()
    {
        int[] hybrid = [2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1];
        Assert.Equal(8, CpuTopology.CountPCores(hybrid));
    }

    [Fact]
    public void CountPCores_HomogeneousSmtIsAllPhysical()
        => Assert.Equal(8, CpuTopology.CountPCores([2, 2, 2, 2, 2, 2, 2, 2]));

    [Fact]
    public void CountPCores_NoSmtUsesLowestEfficiencyClass()
        => Assert.Equal(4, CpuTopology.CountPCores([1, 1, 1, 1, 1, 1, 1, 1], [0, 0, 0, 0, 1, 1, 1, 1]));

    [Theory]
    [InlineData("Virtual Machine", "Microsoft Corporation", true)]
    [InlineData("VMware7,1", "VMware, Inc.", true)]
    [InlineData("Standard PC (Q35 + ICH9, 2009)", "QEMU", true)]
    [InlineData("System Product Name", "ASUS", false)]
    [InlineData("All Series", "ASUS", false)]
    [InlineData("12GJA00JCD", "LENOVO", false)]
    public void LooksLikeVirtualFirmware_DetectsGuests(string product, string manufacturer, bool expected)
        => Assert.Equal(expected, CpuTopology.LooksLikeVirtualFirmware(product, manufacturer));
}
