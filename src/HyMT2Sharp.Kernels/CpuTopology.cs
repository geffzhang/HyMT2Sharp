using System.Runtime.InteropServices;

namespace Sdcb.HyMT2Sharp.Kernels;

/// <summary>
/// Discovers performance cores from the OS topology instead of guessing from
/// <see cref="Environment.ProcessorCount"/>. Efficiency class 0 is the P-core
/// class on Windows hybrid CPUs. The default worker count is the number of
/// physical P-cores, not SMT siblings.
/// </summary>
public static class CpuTopology
{
    public static IReadOnlyList<int> PCoreLogicalIds { get; }
    public static IReadOnlyList<int> PCoreLeaders { get; }
    public static int PreferPCoreCount { get; }

    static CpuTopology()
    {
        List<Core> cores = [];
        try
        {
            if (OperatingSystem.IsWindows())
                TryQueryWindows(cores);
            else if (OperatingSystem.IsLinux())
                TryQueryLinux(cores);
        }
        catch
        {
            cores.Clear();
        }

        if (cores.Count == 0)
        {
            int n = Math.Max(1, Environment.ProcessorCount);
            int[] all = new int[n];
            for (int i = 0; i < n; i++)
                all[i] = i;
            PCoreLogicalIds = all;
            PCoreLeaders = all;
            PreferPCoreCount = n;
            return;
        }

        byte best = cores[0].EfficiencyClass;
        for (int i = 1; i < cores.Count; i++)
        {
            if (cores[i].EfficiencyClass < best)
                best = cores[i].EfficiencyClass;
        }

        List<int> logical = [];
        List<int> leaders = [];
        foreach (Core core in cores)
        {
            if (core.EfficiencyClass != best || core.LogicalIds.Length == 0)
                continue;
            leaders.Add(core.LogicalIds[0]);
            logical.AddRange(core.LogicalIds);
        }

        if (logical.Count == 0)
        {
            int n = Math.Max(1, Environment.ProcessorCount);
            int[] all = new int[n];
            for (int i = 0; i < n; i++)
                all[i] = i;
            PCoreLogicalIds = all;
            PCoreLeaders = all;
            PreferPCoreCount = n;
            return;
        }

        PCoreLogicalIds = logical;
        PCoreLeaders = leaders;
        PreferPCoreCount = Math.Max(1, leaders.Count);
    }

    public static int[] ParseCpuList(string text)
    {
        List<int> ids = [];
        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int dash = part.IndexOf('-');
            if (dash < 0)
            {
                if (int.TryParse(part, out int one))
                    ids.Add(one);
                continue;
            }

            if (!int.TryParse(part[..dash], out int begin) || !int.TryParse(part[(dash + 1)..], out int end))
                continue;
            if (end < begin)
                (begin, end) = (end, begin);
            for (int i = begin; i <= end; i++)
                ids.Add(i);
        }

        return [.. ids];
    }

    private static void TryQueryWindows(List<Core> cores)
    {
        const int relationProcessorCore = 0;
        uint length = 0;
        GetLogicalProcessorInformationEx(relationProcessorCore, IntPtr.Zero, ref length);
        if (length == 0)
            return;

        IntPtr buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(relationProcessorCore, buffer, ref length))
                return;

            int offset = 0;
            while (offset + 8 <= (int)length)
            {
                int relationship = Marshal.ReadInt32(buffer, offset);
                int size = Marshal.ReadInt32(buffer, offset + 4);
                if (size < 32 || offset + size > (int)length)
                    break;
                if (relationship == relationProcessorCore)
                {
                    byte efficiency = Marshal.ReadByte(buffer, offset + 9);
                    ushort groupCount = (ushort)Marshal.ReadInt16(buffer, offset + 30);
                    List<int> ids = [];
                    int maskOffset = offset + 32;
                    for (int g = 0; g < groupCount && maskOffset + IntPtr.Size + 2 <= offset + size; g++)
                    {
                        nuint mask = unchecked((nuint)(nint)Marshal.ReadIntPtr(buffer, maskOffset));
                        ushort group = (ushort)Marshal.ReadInt16(buffer, maskOffset + IntPtr.Size);
                        if (group == 0)
                        {
                            for (int bit = 0; bit < 64; bit++)
                            {
                                if (((mask >> bit) & 1) != 0)
                                    ids.Add(bit);
                            }
                        }

                        maskOffset += IntPtr.Size == 8 ? 16 : 12;
                    }

                    if (ids.Count > 0)
                        cores.Add(new Core(efficiency, [.. ids]));
                }

                offset += size;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void TryQueryLinux(List<Core> cores)
    {
        string[] paths =
        [
            "/sys/devices/cpu_core/cpus",
            "/sys/devices/system/cpu/cpu_core/cpus",
        ];
        string? list = null;
        foreach (string path in paths)
        {
            if (!File.Exists(path))
                continue;
            try
            {
                list = File.ReadAllText(path);
                break;
            }
            catch
            {
                // Best-effort.
            }
        }

        if (string.IsNullOrWhiteSpace(list))
            return;

        Dictionary<int, List<int>> byCore = [];
        foreach (int id in ParseCpuList(list))
        {
            int coreId = id;
            string corePath = $"/sys/devices/system/cpu/cpu{id}/topology/core_id";
            try
            {
                if (File.Exists(corePath) && int.TryParse(File.ReadAllText(corePath).Trim(), out int parsed))
                    coreId = parsed;
            }
            catch
            {
                // Keep the logical id as the grouping key.
            }

            if (!byCore.TryGetValue(coreId, out List<int>? ids))
            {
                ids = [];
                byCore[coreId] = ids;
            }

            ids.Add(id);
        }

        foreach (List<int> ids in byCore.Values)
            cores.Add(new Core(0, [.. ids]));
    }

    private readonly record struct Core(byte EfficiencyClass, int[] LogicalIds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(
        int relationshipType,
        IntPtr buffer,
        ref uint returnedLength);
}
