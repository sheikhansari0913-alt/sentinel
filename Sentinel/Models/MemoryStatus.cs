// Sentinel/Models/MemoryStatus.cs

namespace Sentinel.Models;

/// <summary>
/// Aggregated memory state of the system. The PressureMonitor produces this
/// every second; the UI charts it; the engines decide actions from it.
/// </summary>
public sealed class MemoryStatus
{
    public DateTime Captured { get; init; } = DateTime.UtcNow;
    public ulong TotalPhysicalBytes { get; init; }
    public ulong AvailablePhysicalBytes { get; init; }
    public ulong CommittedBytes { get; init; }
    public ulong CommitLimitBytes { get; init; }
    public uint MemoryLoadPercent { get; init; }

    /// <summary>Bytes in the standby (cache) list, total across all priorities.</summary>
    public ulong StandbyBytes { get; init; }
    /// <summary>Bytes in priority sub-lists 0..1 — the "throwaway" cache.</summary>
    public ulong LowPriorityStandbyBytes { get; init; }
    /// <summary>Bytes in the modified-page list (dirty, not yet on disk).</summary>
    public ulong ModifiedBytes { get; init; }
    /// <summary>Bytes that are immediately reusable: free + zeroed.</summary>
    public ulong FreeBytes { get; init; }

    /// <summary>Per-priority breakdown (8 buckets) of standby list in bytes.</summary>
    public ulong[] StandbyByPriority { get; init; } = new ulong[8];

    public double UsedPercent =>
        TotalPhysicalBytes == 0 ? 0
        : 100.0 * (TotalPhysicalBytes - AvailablePhysicalBytes) / TotalPhysicalBytes;

    public double CommitPercent =>
        CommitLimitBytes == 0 ? 0
        : 100.0 * CommittedBytes / CommitLimitBytes;
}
