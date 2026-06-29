// Sentinel/Engines/CpuEngine.cs

using System.ComponentModel;
using System.Runtime.InteropServices;
using Sentinel.Models;
using Sentinel.Monitoring;
using Sentinel.Native;

namespace Sentinel.Engines;

/// <summary>
/// CPU reclamation without killing. Three escalating actions:
///
///   1. SetPriorityClass(IDLE_PRIORITY_CLASS) — the OS scheduler gives the
///      process time only when nothing else wants it. Reversible instantly.
///   2. ProcessModeBackgroundBegin — additionally lowers I/O and memory
///      priority. Very effective for indexers, sync clients, telemetry.
///   3. Job Object with JobObjectCpuRateControlInformation HardCap — a hard
///      ceiling on CPU% the process can consume across all cores. The most
///      powerful and the most reversible of the three.
///
/// Every escalation goes through SafetyKernel.Gate first.
/// </summary>
public sealed class CpuEngine : IDisposable
{
    private readonly SafetyKernel _safety;
    private readonly Dictionary<int, IntPtr> _processJobs = new();
    private readonly object _gate = new();

    public CpuEngine(SafetyKernel safety) { _safety = safety; }

    /// <summary>Lowest cost throttle — just lower priority.</summary>
    public bool LowerPriority(ProcessSnapshot process)
    {
        if (!_safety.Gate(process, "LowerPriority")) return false;
        return SetPriorityInternal(process, PriorityClass.IdlePriorityClass);
    }

    public bool RestorePriority(ProcessSnapshot process)
    {
        return SetPriorityInternal(process, PriorityClass.NormalPriorityClass);
    }

    /// <summary>
    /// Move the process into "background mode" — combined CPU + I/O + memory
    /// priority drop. Use this for sync clients, backup agents, indexers.
    /// </summary>
    public bool EnterBackgroundMode(ProcessSnapshot process)
    {
        if (!_safety.Gate(process, "BackgroundMode")) return false;
        return SetPriorityInternal(process, PriorityClass.ProcessModeBackgroundBegin);
    }

    private bool SetPriorityInternal(ProcessSnapshot process, PriorityClass cls)
    {
        try
        {
            using var h = NativeMethods.OpenProcess(
                ProcessAccessFlags.SetInformation | ProcessAccessFlags.QueryInformation,
                false, process.Pid);
            if (h.IsInvalid)
            {
                ActionLogger.Instance.LogError("SetPriority",
                    $"{process.Name}({process.Pid})",
                    $"OpenProcess failed: {Marshal.GetLastWin32Error()}");
                return false;
            }

            if (!NativeMethods.SetPriorityClass(h, (uint)cls))
            {
                ActionLogger.Instance.LogError("SetPriority",
                    $"{process.Name}({process.Pid})",
                    $"SetPriorityClass failed: {Marshal.GetLastWin32Error()}");
                return false;
            }

            ActionLogger.Instance.LogAction("SetPriority",
                $"{process.Name}({process.Pid})", cls.ToString());
            return true;
        }
        catch (Exception ex)
        {
            ActionLogger.Instance.LogError("SetPriority",
                $"{process.Name}({process.Pid})", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Strongest throttle. Creates a Job Object with a hard CPU cap and
    /// assigns the process to it. cpuPercent is 1..100. The job handle is
    /// retained so we can release later.
    /// </summary>
    public bool ThrottleToCpuPercent(ProcessSnapshot process, int cpuPercent)
    {
        if (!_safety.Gate(process, "JobObjectThrottle")) return false;
        if (cpuPercent is < 1 or > 100) cpuPercent = 10;

        try
        {
            var hJob = NativeMethods.CreateJobObject(IntPtr.Zero, null);
            if (hJob == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "CreateJobObject failed");

            var info = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
            {
                ControlFlags = (uint)(JobObjectCpuRateControlFlags.Enable
                                    | JobObjectCpuRateControlFlags.HardCap),
                // The CPU rate is 1..10000 = 0.01% .. 100%.
                CpuRate = (uint)(cpuPercent * 100),
            };

            if (!NativeMethods.SetInformationJobObject(hJob,
                    JobObjectInfoType.CpuRateControlInformation,
                    ref info, (uint)Marshal.SizeOf<JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>()))
            {
                int err = Marshal.GetLastWin32Error();
                NativeMethods.CloseHandle(hJob);
                throw new Win32Exception(err, "SetInformationJobObject failed");
            }

            using var hProc = NativeMethods.OpenProcess(
                ProcessAccessFlags.SetQuota | ProcessAccessFlags.Terminate,
                false, process.Pid);
            if (hProc.IsInvalid)
            {
                NativeMethods.CloseHandle(hJob);
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "OpenProcess (for job assignment) failed");
            }

            if (!NativeMethods.AssignProcessToJobObject(hJob, hProc))
            {
                int err = Marshal.GetLastWin32Error();
                NativeMethods.CloseHandle(hJob);
                // ERROR_ACCESS_DENIED (5) typically means the process is
                // already in another job that can't be nested. We surface
                // that cleanly so the UI can suggest LowerPriority instead.
                throw new Win32Exception(err, "AssignProcessToJobObject failed");
            }

            lock (_gate)
            {
                // If we already had a job for this PID, replace it.
                if (_processJobs.TryGetValue(process.Pid, out var oldJob))
                    NativeMethods.CloseHandle(oldJob);
                _processJobs[process.Pid] = hJob;
            }

            ActionLogger.Instance.LogAction("JobObjectThrottle",
                $"{process.Name}({process.Pid})", $"cap={cpuPercent}%");
            return true;
        }
        catch (Win32Exception ex)
        {
            ActionLogger.Instance.LogError("JobObjectThrottle",
                $"{process.Name}({process.Pid})", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Release a process from its Job Object. We can't unassign — we close
    /// the job handle, and when the last handle closes the job dissolves.
    /// </summary>
    public bool Release(ProcessSnapshot process)
    {
        lock (_gate)
        {
            if (!_processJobs.TryGetValue(process.Pid, out var hJob))
                return false;

            NativeMethods.CloseHandle(hJob);
            _processJobs.Remove(process.Pid);

            ActionLogger.Instance.LogAction("JobObjectRelease",
                $"{process.Name}({process.Pid})", "released");
            return true;
        }
    }

    public bool IsThrottled(int pid)
    {
        lock (_gate) return _processJobs.ContainsKey(pid);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var h in _processJobs.Values)
                NativeMethods.CloseHandle(h);
            _processJobs.Clear();
        }
    }
}
