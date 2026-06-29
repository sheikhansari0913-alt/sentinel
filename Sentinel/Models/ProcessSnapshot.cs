// Sentinel/Models/ProcessSnapshot.cs

namespace Sentinel.Models;

/// <summary>
/// Everything the engines and UI need to know about a process at a point in
/// time. Built from System.Diagnostics.Process + our own enrichment.
/// </summary>
public sealed class ProcessSnapshot
{
    public required int Pid { get; init; }
    public required string Name { get; init; }
    public required string? ImagePath { get; init; }
    public required long WorkingSetBytes { get; init; }
    public required long PrivateBytes { get; init; }
    public required int ThreadCount { get; init; }
    public required int HandleCount { get; init; }
    public required DateTime StartTime { get; init; }
    public required TimeSpan TotalCpuTime { get; init; }
    public required bool HasVisibleWindow { get; init; }
    public required bool IsForeground { get; init; }
    public required bool IsCriticalSystem { get; init; }
    public required bool IsOwnedByCurrentUser { get; init; }

    // Set by ProtectionEngine.
    public int ProtectionScore { get; set; }
    public string ProtectionReason { get; set; } = string.Empty;

    // Set by PressureMonitor — sliding-window CPU and memory growth.
    public double CpuPercent { get; set; }
    public double MemoryGrowthBytesPerSec { get; set; }

    public bool IsProtected => ProtectionScore >= 50;
}
