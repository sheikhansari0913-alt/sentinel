// Sentinel/Monitoring/PressureMonitor.cs

using System.Collections.Concurrent;
using System.Diagnostics;
using Sentinel.Engines;
using Sentinel.Models;
using Sentinel.Native;
using System.Runtime.InteropServices;

namespace Sentinel.Monitoring;

/// <summary>
/// The brain. Polls system state on a 1 Hz timer, computes a Pressure Score,
/// and triggers reclamation when thresholds are crossed.
///
/// Pressure Score (0..100):
///   + (MemoryLoadPercent - 50) * 1.0   weighting memory pressure
///   + (CommitPercent - 70) * 1.0       weighting commit pressure
///   + (top1ProcessCpu - 30) * 0.5      runaway CPU bonus
///
/// Threshold actions:
///   score >= 60   ->  FlushModifiedList   (free, no cache loss)
///   score >= 75   ->  PurgeLowPriorityStandby   (drop cold cache)
///   score >= 90   ->  TrimNonProtectedProcesses (per-process)
///
/// We never call PurgeFullStandbyList automatically — that's user-initiated.
/// </summary>
public sealed class PressureMonitor : IDisposable
{
    private readonly MemoryEngine _memory;
    private readonly CpuEngine _cpu;
    private readonly ProtectionEngine _protection;
    private readonly System.Threading.Timer _timer;

    private readonly ConcurrentDictionary<int, (DateTime At, TimeSpan Cpu)> _cpuHistory = new();
    private readonly ConcurrentDictionary<int, long> _wsHistory = new();

    public event Action<MemoryStatus, IReadOnlyList<ProcessSnapshot>, double>? Tick;

    public PressureMonitor(MemoryEngine memory, CpuEngine cpu, ProtectionEngine protection)
    {
        _memory = memory;
        _cpu = cpu;
        _protection = protection;
        _timer = new System.Threading.Timer(OnTick, null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public bool AutomaticActionsEnabled { get; set; } = true;

    private void OnTick(object? _)
    {
        try
        {
            var status = _memory.ReadStatus();
            var snapshots = BuildSnapshots();
            _protection.Score(snapshots);
            EnrichWithRates(snapshots);

            double score = ComputePressureScore(status, snapshots);

            if (AutomaticActionsEnabled)
                MaybeAct(score, status, snapshots);

            Tick?.Invoke(status, snapshots, score);
        }
        catch (Exception ex)
        {
            ActionLogger.Instance.LogError("PressureMonitor.Tick", "self", ex.Message);
        }
    }

    private static double ComputePressureScore(
        MemoryStatus status, IReadOnlyList<ProcessSnapshot> snapshots)
    {
        double s = 0;
        s += Math.Max(0, status.MemoryLoadPercent - 50) * 1.0;
        s += Math.Max(0, status.CommitPercent - 70) * 1.0;
        double topCpu = snapshots.DefaultIfEmpty().Max(p => p?.CpuPercent ?? 0);
        s += Math.Max(0, topCpu - 30) * 0.5;
        return Math.Clamp(s, 0, 100);
    }

    private void MaybeAct(double score, MemoryStatus status,
        IReadOnlyList<ProcessSnapshot> snapshots)
    {
        // Defer everything if user is actively doing heavy I/O — purging
        // the standby list under load makes the I/O slower.
        if (snapshots.Any(p => p.IsForeground && p.CpuPercent > 50))
            return;

        if (score >= 90)
        {
            // Per-process trim on the worst non-protected memory leeches.
            foreach (var leech in snapshots
                .Where(p => !p.IsProtected)
                .OrderByDescending(p => p.WorkingSetBytes)
                .Take(5))
            {
                _memory.TrimProcessWorkingSet(leech);
            }
        }

        if (score >= 75 && status.LowPriorityStandbyBytes > 512L * 1024 * 1024)
        {
            _memory.PurgeLowPriorityStandbyList();
        }
        else if (score >= 60 && status.ModifiedBytes > 256L * 1024 * 1024)
        {
            _memory.FlushModifiedList();
        }
    }

    /// <summary>
    /// Enumerate processes and pull the metrics our engines need.
    /// </summary>
    private List<ProcessSnapshot> BuildSnapshots()
    {
        var fg = _protection.GetForegroundProcessId();
        var currentUser = _protection.GetCurrentUser();
        var foregroundWindowOwners = CollectVisibleWindowOwners();

        var list = new List<ProcessSnapshot>(256);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                bool isCritical = SafetyKernel.IsKernelCritical(p.Id);
                bool ownedByUser = false;
                try { ownedByUser = string.Equals(GetProcessOwner(p), currentUser, StringComparison.OrdinalIgnoreCase); }
                catch { }

                list.Add(new ProcessSnapshot
                {
                    Pid = p.Id,
                    Name = p.ProcessName + ".exe",
                    ImagePath = TryGetImagePath(p),
                    WorkingSetBytes = p.WorkingSet64,
                    PrivateBytes = p.PrivateMemorySize64,
                    ThreadCount = p.Threads.Count,
                    HandleCount = p.HandleCount,
                    StartTime = TryGetStartTime(p),
                    TotalCpuTime = TryGetCpuTime(p),
                    HasVisibleWindow = foregroundWindowOwners.Contains(p.Id),
                    IsForeground = p.Id == fg,
                    IsCriticalSystem = isCritical,
                    IsOwnedByCurrentUser = ownedByUser,
                });
            }
            catch { /* process may have exited */ }
            finally { p.Dispose(); }
        }
        return list;
    }

    /// <summary>
    /// CPU percent = delta(TotalProcessorTime) / delta(wallclock) / coreCount.
    /// Memory growth = delta(WorkingSet) / delta(seconds).
    /// </summary>
    private void EnrichWithRates(IReadOnlyList<ProcessSnapshot> snapshots)
    {
        var now = DateTime.UtcNow;
        int cores = Environment.ProcessorCount;

        foreach (var s in snapshots)
        {
            if (_cpuHistory.TryGetValue(s.Pid, out var prev))
            {
                double seconds = (now - prev.At).TotalSeconds;
                if (seconds > 0)
                {
                    double cpuSeconds = (s.TotalCpuTime - prev.Cpu).TotalSeconds;
                    s.CpuPercent = Math.Max(0,
                        Math.Min(100, 100.0 * cpuSeconds / (seconds * cores)));
                }
            }
            _cpuHistory[s.Pid] = (now, s.TotalCpuTime);

            if (_wsHistory.TryGetValue(s.Pid, out var prevWs))
                s.MemoryGrowthBytesPerSec = s.WorkingSetBytes - prevWs;
            _wsHistory[s.Pid] = s.WorkingSetBytes;
        }

        // Prune history of exited processes.
        var live = snapshots.Select(s => s.Pid).ToHashSet();
        foreach (var pid in _cpuHistory.Keys.Where(k => !live.Contains(k)).ToList())
        {
            _cpuHistory.TryRemove(pid, out _);
            _wsHistory.TryRemove(pid, out _);
        }
    }

    /// <summary>
    /// Best-effort owner lookup via System.Diagnostics. For a more robust
    /// implementation use OpenProcessToken + GetTokenInformation(TokenUser).
    /// </summary>
    private static string GetProcessOwner(Process process)
    {
        // Lightweight heuristic: query WMI is slow, doing it per tick at scale
        // is wasteful. Use SessionId as a cheap proxy: 0 = service, !=0 = user.
        return process.SessionId == 0 ? "NT AUTHORITY\\SYSTEM"
                                       : Environment.UserDomainName + "\\" + Environment.UserName;
    }

    private static string? TryGetImagePath(Process p)
    {
        try { return p.MainModule?.FileName; } catch { return null; }
    }
    private static DateTime TryGetStartTime(Process p)
    {
        try { return p.StartTime; } catch { return DateTime.MinValue; }
    }
    private static TimeSpan TryGetCpuTime(Process p)
    {
        try { return p.TotalProcessorTime; } catch { return TimeSpan.Zero; }
    }

    /// <summary>
    /// Visible-window heuristic: walk top-level windows once per tick and
    /// note which PIDs own at least one visible window.
    /// </summary>
    private static HashSet<int> CollectVisibleWindowOwners()
    {
        var pids = new HashSet<int>();
        EnumWindows((hwnd, _) =>
        {
            if (NativeMethods.IsWindowVisible(hwnd))
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                pids.Add((int)pid);
            }
            return true;
        }, IntPtr.Zero);
        return pids;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    public void Dispose() => _timer.Dispose();
}
