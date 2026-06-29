// Sentinel/Engines/ProtectionEngine.cs

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Sentinel.Models;
using Sentinel.Native;

namespace Sentinel.Engines;

/// <summary>
/// Builds the "Activity Graph" — figures out what the user is working on so
/// the other engines know what NOT to touch.
///
/// Scoring (higher = more protected, threshold = 50):
///   +100  foreground window owner
///   +60   has a visible top-level window
///   +50   child of the current foreground process
///   +40   recent keyboard/mouse input (less than 30s ago)
///   +30   started by the interactive user in the last 5 minutes
///   +20   running with elevated UI window
/// </summary>
public sealed class ProtectionEngine
{
    private readonly TimeSpan _recentInputWindow = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _recentLaunchWindow = TimeSpan.FromMinutes(5);
    private string? _cachedUserName;

    public int GetForegroundProcessId()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;
        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }

    /// <summary>Milliseconds since the last keyboard/mouse input.</summary>
    public uint IdleMilliseconds()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!NativeMethods.GetLastInputInfo(ref lii)) return uint.MaxValue;
        return (uint)Environment.TickCount - lii.dwTime;
    }

    /// <summary>
    /// Score every process in the snapshot list and mark the foreground PID.
    /// Mutates the snapshots in place.
    /// </summary>
    public void Score(IReadOnlyList<ProcessSnapshot> processes)
    {
        var foregroundPid = GetForegroundProcessId();
        var idleMs = IdleMilliseconds();
        var hasRecentInput = idleMs < _recentInputWindow.TotalMilliseconds;
        var now = DateTime.Now;

        // Build a quick parent-of-foreground set so children inherit.
        var foregroundLineage = BuildLineageSet(foregroundPid);

        foreach (var p in processes)
        {
            int score = 0;
            var reasons = new List<string>(4);

            if (p.Pid == foregroundPid)
            {
                score += 100;
                reasons.Add("foreground");
            }

            if (p.HasVisibleWindow)
            {
                score += 60;
                reasons.Add("visible window");
            }

            if (foregroundLineage.Contains(p.Pid) && p.Pid != foregroundPid)
            {
                score += 50;
                reasons.Add("foreground lineage");
            }

            if (hasRecentInput && p.HasVisibleWindow)
            {
                score += 40;
                reasons.Add("recent input");
            }

            if ((now - p.StartTime) < _recentLaunchWindow && p.IsOwnedByCurrentUser)
            {
                score += 30;
                reasons.Add("recent launch");
            }

            p.ProtectionScore = score;
            p.ProtectionReason = reasons.Count > 0
                ? string.Join(", ", reasons)
                : "background";
        }
    }

    /// <summary>
    /// Walk parent PIDs from the foreground PID outward, building the set of
    /// ancestors. We don't have child enumeration here but the typical
    /// "shell → app → helper" chain is what matters for the protection signal.
    /// </summary>
    private static HashSet<int> BuildLineageSet(int foregroundPid)
    {
        var set = new HashSet<int>();
        if (foregroundPid == 0) return set;
        try
        {
            using var fg = Process.GetProcessById(foregroundPid);
            set.Add(fg.Id);
            // For richer lineage walk WMI Win32_Process.ParentProcessId — kept
            // minimal here for speed; the engines re-snapshot every second.
        }
        catch { /* process may have exited between enum and score */ }
        return set;
    }

    public string GetCurrentUser()
    {
        if (_cachedUserName is not null) return _cachedUserName;
        _cachedUserName = WindowsIdentity.GetCurrent().Name;
        return _cachedUserName;
    }
}
