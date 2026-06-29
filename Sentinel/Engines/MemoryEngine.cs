// Sentinel/Engines/MemoryEngine.cs

using System.ComponentModel;
using System.Runtime.InteropServices;
using Sentinel.Models;
using Sentinel.Monitoring;
using Sentinel.Native;

namespace Sentinel.Engines;

/// <summary>
/// Owns all memory-reclamation operations. There are three weapons:
///
///  1. Per-process working-set trim (EmptyWorkingSet) — collapses a single
///     process's resident pages.
///  2. System-wide standby/modified list operations
///     (NtSetSystemInformation + SystemMemoryListInformation) — equivalent to
///     RAMMap's "Empty Standby List" and "Empty Modified Page List" buttons,
///     but automated and pressure-driven.
///  3. Low-priority standby purge — the SAFE default that drops only stale
///     cache (priority sub-lists 0-1) and leaves hot cache intact.
///
/// We deliberately do NOT call EmptyWorkingSet on every process by default.
/// That is the anti-pattern most "RAM cleaners" commit. It makes Task Manager
/// look pretty for 10 seconds and then makes the machine slower for minutes
/// as everything pages back in.
/// </summary>
public sealed class MemoryEngine
{
    private readonly SafetyKernel _safety;
    private bool _privilegesEnabled;

    public MemoryEngine(SafetyKernel safety)
    {
        _safety = safety;
    }

    /// <summary>
    /// One-time elevation step. SeProfileSingleProcessPrivilege is required
    /// for SystemMemoryListInformation set operations. Without this enabled,
    /// the standby-list operations silently no-op even on an admin token.
    /// </summary>
    public void EnsurePrivileges()
    {
        if (_privilegesEnabled) return;
        try
        {
            NativeMethods.EnablePrivilege(PrivilegeNames.SeProfileSingleProcess);
            NativeMethods.EnablePrivilege(PrivilegeNames.SeIncreaseQuota);
            NativeMethods.EnablePrivilege(PrivilegeNames.SeDebug);
            _privilegesEnabled = true;
        }
        catch (Win32Exception ex)
        {
            ActionLogger.Instance.LogError("EnablePrivilege", "self", ex.Message);
        }
    }

    /// <summary>
    /// Pull current system-wide memory status, including the per-priority
    /// standby breakdown. This is the data structure the PressureMonitor
    /// uses to decide whether to act.
    /// </summary>
    public MemoryStatus ReadStatus()
    {
        var mem = new MEMORYSTATUSEX();
        NativeMethods.GlobalMemoryStatusEx(mem);

        var pageSize = (ulong)Environment.SystemPageSize;
        var standbyList = ReadStandbyList(pageSize);

        return new MemoryStatus
        {
            TotalPhysicalBytes      = mem.ullTotalPhys,
            AvailablePhysicalBytes  = mem.ullAvailPhys,
            CommittedBytes          = mem.ullTotalPageFile - mem.ullAvailPageFile,
            CommitLimitBytes        = mem.ullTotalPageFile,
            MemoryLoadPercent       = mem.dwMemoryLoad,
            StandbyBytes            = standbyList.Total,
            LowPriorityStandbyBytes = standbyList.LowPriority,
            ModifiedBytes           = standbyList.Modified,
            FreeBytes               = standbyList.Free,
            StandbyByPriority       = standbyList.ByPriority,
        };
    }

    /// <summary>
    /// Call NtQuerySystemInformation(SystemMemoryListInformation) and convert
    /// page counts into byte counts. This is the data RAMMap displays.
    /// </summary>
    private static (ulong Total, ulong LowPriority, ulong Modified, ulong Free, ulong[] ByPriority)
        ReadStandbyList(ulong pageSize)
    {
        int size = Marshal.SizeOf<SYSTEM_MEMORY_LIST_INFORMATION>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            int status = NativeMethods.NtQuerySystemInformation(
                SystemInformationClass.SystemMemoryListInformation,
                buffer, size, out _);
            if (!NativeMethods.NT_SUCCESS(status))
                return (0, 0, 0, 0, new ulong[8]);

            var info = Marshal.PtrToStructure<SYSTEM_MEMORY_LIST_INFORMATION>(buffer);
            var byPriority = new ulong[8];
            ulong total = 0, low = 0;
            for (int i = 0; i < 8; i++)
            {
                ulong bytes = (ulong)info.PageCountByPriority[i] * pageSize;
                byPriority[i] = bytes;
                total += bytes;
                if (i <= 1) low += bytes;
            }

            return (
                total,
                low,
                (ulong)info.ModifiedPageCount * pageSize,
                ((ulong)info.FreePageCount + (ulong)info.ZeroPageCount) * pageSize,
                byPriority);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // -------------------------------------------------------------------
    // The three reclamation weapons.
    // -------------------------------------------------------------------

    /// <summary>
    /// SAFE default. Purges only standby sub-lists 0 and 1 — cold pages the
    /// OS itself considers throwaway. Call this from the PressureMonitor
    /// when memory load crosses ~80%.
    /// </summary>
    public ReclamationResult PurgeLowPriorityStandbyList()
    {
        EnsurePrivileges();
        return IssueMemoryCommand(
            SystemMemoryListCommand.MemoryPurgeLowPriorityStandbyList,
            "PurgeLowPriorityStandby");
    }

    /// <summary>
    /// AGGRESSIVE. Purges the entire standby list. Reclaims maximum bytes but
    /// the file cache is gone — subsequent reads hit disk. Only triggered if
    /// the foreground app is page-faulting AND the standby list is the biggest
    /// memory user. Behind a confirmation in the UI.
    /// </summary>
    public ReclamationResult PurgeFullStandbyList()
    {
        EnsurePrivileges();
        return IssueMemoryCommand(
            SystemMemoryListCommand.MemoryPurgeStandbyList,
            "PurgeStandby");
    }

    /// <summary>
    /// Flush the modified-page list to disk. Pages move from "modified" to
    /// "standby," making them reclaimable. Lower impact than a purge — runs
    /// before a heavy workload as a pre-warm.
    /// </summary>
    public ReclamationResult FlushModifiedList()
    {
        EnsurePrivileges();
        return IssueMemoryCommand(
            SystemMemoryListCommand.MemoryFlushModifiedList,
            "FlushModified");
    }

    /// <summary>
    /// System-wide working-set trim. The "Empty Working Sets" button. Every
    /// process gets paged. Use SPARINGLY — the Protection Engine cannot guard
    /// individual processes from this; it's a system-wide hammer.
    /// </summary>
    public ReclamationResult TrimAllWorkingSets()
    {
        EnsurePrivileges();
        return IssueMemoryCommand(
            SystemMemoryListCommand.MemoryEmptyWorkingSets,
            "TrimAllWorkingSets");
    }

    /// <summary>
    /// PER-PROCESS trim. Goes through SafetyKernel — protected processes are
    /// skipped. This is how the engine reclaims memory from a specific
    /// background leech without touching anything the user is using.
    /// </summary>
    public bool TrimProcessWorkingSet(ProcessSnapshot process)
    {
        if (!_safety.Gate(process, "TrimWorkingSet"))
            return false;

        try
        {
            using var handle = NativeMethods.OpenProcess(
                ProcessAccessFlags.SetQuota | ProcessAccessFlags.QueryInformation,
                false, process.Pid);
            if (handle.IsInvalid)
            {
                ActionLogger.Instance.LogError("TrimWorkingSet",
                    $"{process.Name}({process.Pid})",
                    $"OpenProcess failed: {Marshal.GetLastWin32Error()}");
                return false;
            }

            // EmptyWorkingSet is gentler than SetProcessWorkingSetSizeEx with
            // hard min disable; the OS swaps pages back as the process needs
            // them. We prefer it.
            if (!NativeMethods.EmptyWorkingSet(handle))
            {
                ActionLogger.Instance.LogError("TrimWorkingSet",
                    $"{process.Name}({process.Pid})",
                    $"EmptyWorkingSet failed: {Marshal.GetLastWin32Error()}");
                return false;
            }

            ActionLogger.Instance.LogAction("TrimWorkingSet",
                $"{process.Name}({process.Pid})",
                $"WS was {process.WorkingSetBytes:N0} bytes");
            return true;
        }
        catch (Exception ex)
        {
            ActionLogger.Instance.LogError("TrimWorkingSet",
                $"{process.Name}({process.Pid})", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Shared plumbing for the four system-wide memory commands. Returns
    /// before/after delta so the UI can show "reclaimed N MB."
    /// </summary>
    private ReclamationResult IssueMemoryCommand(
        SystemMemoryListCommand command, string label)
    {
        var before = ReadStatus();

        int cmd = (int)command;
        int status = NativeMethods.NtSetSystemInformation(
            SystemInformationClass.SystemMemoryListInformation,
            ref cmd, sizeof(int));

        if (!NativeMethods.NT_SUCCESS(status))
        {
            ActionLogger.Instance.LogError(label, "system",
                $"NtSetSystemInformation returned 0x{status:X8}");
            return new ReclamationResult
            {
                Operation = label,
                Success = false,
                BeforeAvailableBytes = before.AvailablePhysicalBytes,
                AfterAvailableBytes  = before.AvailablePhysicalBytes,
                NtStatus = status,
            };
        }

        // Let the kernel settle for a beat so the after-snapshot is real.
        Thread.Sleep(150);
        var after = ReadStatus();

        long delta = (long)after.AvailablePhysicalBytes
                   - (long)before.AvailablePhysicalBytes;

        ActionLogger.Instance.LogAction(label, "system",
            $"reclaimed {delta:N0} bytes (avail {before.AvailablePhysicalBytes:N0} -> " +
            $"{after.AvailablePhysicalBytes:N0})");

        return new ReclamationResult
        {
            Operation = label,
            Success = true,
            BeforeAvailableBytes = before.AvailablePhysicalBytes,
            AfterAvailableBytes  = after.AvailablePhysicalBytes,
            NtStatus = status,
        };
    }
}

public sealed class ReclamationResult
{
    public required string Operation { get; init; }
    public required bool Success { get; init; }
    public required ulong BeforeAvailableBytes { get; init; }
    public required ulong AfterAvailableBytes { get; init; }
    public required int NtStatus { get; init; }

    public long DeltaBytes => (long)AfterAvailableBytes - (long)BeforeAvailableBytes;
    public double DeltaMegabytes => DeltaBytes / (1024.0 * 1024.0);
}
