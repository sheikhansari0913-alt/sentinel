// Sentinel/Engines/SafetyKernel.cs

using System.Collections.Generic;
using System.IO;
using Sentinel.Models;
using Sentinel.Monitoring;
using Sentinel.Native;

namespace Sentinel.Engines;

/// <summary>
/// The final gate every destructive action must pass through. The motto:
/// "If unsure, refuse." A vetoed action is logged with reason — those refusal
/// lines in the audit log are what you point at when an examiner asks
/// "what stops your tool from killing critical processes?"
/// </summary>
public sealed class SafetyKernel
{
    // Processes we will never touch under any circumstances, regardless of
    // what the user clicks. These either run before the Windows session
    // manager or hold the integrity of the user session itself.
    private static readonly HashSet<string> HardBlocklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "registry", "memory compression",
        "smss.exe", "csrss.exe", "wininit.exe", "services.exe",
        "lsass.exe", "winlogon.exe", "fontdrvhost.exe",
        "dwm.exe", "sihost.exe", "ctfmon.exe", "explorer.exe",
        "logonui.exe", "userinit.exe", "taskhostw.exe",
        "Sentinel.exe", // never act on ourselves
    };

    /// <summary>
    /// Should the named action be allowed against this process?
    /// Returns (allowed, reason). Reason is logged either way.
    /// </summary>
    public (bool Allowed, string Reason) AuthorizeAction(
        ProcessSnapshot process, string action)
    {
        // 1. Hardcoded blocklist — strongest veto. Comes first because we
        //    don't even want to open a handle to these.
        if (HardBlocklist.Contains(process.Name) ||
            (process.ImagePath is not null &&
             HardBlocklist.Contains(Path.GetFileName(process.ImagePath))))
        {
            return (false, $"Hard blocklist: {process.Name}");
        }

        // 2. PID 0 (System Idle) and 4 (System) — never.
        if (process.Pid is 0 or 4)
            return (false, $"Reserved PID {process.Pid}");

        // 3. Self — Sentinel must not act on Sentinel.
        if (process.Pid == Environment.ProcessId)
            return (false, "Cannot act on Sentinel itself");

        // 4. Kernel-marked critical process. Killing one of these bluescreens.
        if (process.IsCriticalSystem)
            return (false, "IsProcessCritical=true");

        // 5. Foreground app — the user is staring at it right now.
        if (process.IsForeground)
            return (false, "Foreground process");

        // 6. High protection score from the Protection Engine
        //    (visible window, recent input, parent of foreground, etc.).
        if (process.ProtectionScore >= 50)
            return (false, $"Protected ({process.ProtectionReason})");

        // 7. Not owned by the interactive user — likely a service we have
        //    no business throttling.
        if (!process.IsOwnedByCurrentUser && action != "Inspect")
            return (false, "Not owned by current user");

        return (true, "Authorized");
    }

    /// <summary>
    /// Cheap check — is the kernel itself marking this PID as critical?
    /// We call IsProcessCritical (Win8+), which reports the
    /// ProcessBreakOnTermination bit.
    /// </summary>
    public static bool IsKernelCritical(int pid)
    {
        try
        {
            using var h = NativeMethods.OpenProcess(
                ProcessAccessFlags.QueryLimitedInformation, false, pid);
            if (h.IsInvalid) return false;
            if (!NativeMethods.IsProcessCritical(h, out var critical)) return false;
            return critical;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Wrap a destructive operation. Logs veto or action and returns whether
    /// to proceed. Engines call this; it's the chokepoint.
    /// </summary>
    public bool Gate(ProcessSnapshot process, string action)
    {
        var (allowed, reason) = AuthorizeAction(process, action);
        if (!allowed)
        {
            ActionLogger.Instance.LogVeto(action,
                $"{process.Name}({process.Pid})", reason);
        }
        return allowed;
    }
}
