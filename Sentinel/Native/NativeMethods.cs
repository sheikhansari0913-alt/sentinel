// Sentinel/Native/NativeMethods.cs
// P/Invoke surface. Every Native API we touch is declared here so the
// Engines/ code stays clean and an examiner can audit our system calls
// in one file.

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sentinel.Native;

internal static class NativeMethods
{
    // -------------------------------------------------------------------
    // ntdll.dll — the Native API. We rely on these because the documented
    // Win32 wrappers either don't exist or hide the data we need.
    // -------------------------------------------------------------------

    /// <summary>
    /// Query system-wide information. We use it for SystemMemoryListInformation
    /// (standby/modified list sizes) and SystemPerformanceInformation
    /// (page faults, commits, cache reads).
    /// </summary>
    [DllImport("ntdll.dll", SetLastError = false)]
    internal static extern int NtQuerySystemInformation(
        SystemInformationClass SystemInformationClass,
        IntPtr SystemInformation,
        int SystemInformationLength,
        out int ReturnLength);

    /// <summary>
    /// Set system-wide information. We use it with SystemMemoryListInformation
    /// to issue the standby/modified-list purge commands. Requires
    /// SeProfileSingleProcessPrivilege.
    /// </summary>
    [DllImport("ntdll.dll", SetLastError = false)]
    internal static extern int NtSetSystemInformation(
        SystemInformationClass SystemInformationClass,
        ref int SystemInformation,
        int SystemInformationLength);

    /// <summary>Suspend every thread in the target process. Reversible via NtResumeProcess.</summary>
    [DllImport("ntdll.dll", SetLastError = false)]
    internal static extern int NtSuspendProcess(IntPtr ProcessHandle);

    [DllImport("ntdll.dll", SetLastError = false)]
    internal static extern int NtResumeProcess(IntPtr ProcessHandle);

    // NTSTATUS helper. STATUS_SUCCESS = 0.
    internal static bool NT_SUCCESS(int status) => status >= 0;

    // -------------------------------------------------------------------
    // kernel32.dll — process & handle management, job objects.
    // -------------------------------------------------------------------

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(
        ProcessAccessFlags processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetPriorityClass(SafeProcessHandle hProcess, uint dwPriorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsProcessCritical(SafeProcessHandle hProcess,
        [MarshalAs(UnmanagedType.Bool)] out bool Critical);

    // Job objects — the modern, safe way to constrain a process. Vastly
    // preferable to TerminateProcess for "this thing is hogging the CPU."
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AssignProcessToJobObject(IntPtr hJob, SafeProcessHandle hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetInformationJobObject(
        IntPtr hJob,
        JobObjectInfoType JobObjectInfoClass,
        ref JOBOBJECT_CPU_RATE_CONTROL_INFORMATION lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern int GetCurrentProcessId();

    // -------------------------------------------------------------------
    // psapi.dll / kernel32.dll — working set manipulation.
    // -------------------------------------------------------------------

    /// <summary>
    /// Strips a process down to its absolute minimum working set, paging the
    /// rest out. Used judiciously — we only call this on processes the
    /// SafetyKernel has cleared.
    /// </summary>
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EmptyWorkingSet(SafeProcessHandle hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProcessWorkingSetSizeEx(
        SafeProcessHandle hProcess, IntPtr dwMinimumWorkingSetSize,
        IntPtr dwMaximumWorkingSetSize, uint Flags);

    // -------------------------------------------------------------------
    // advapi32.dll — token privileges (SeProfileSingleProcessPrivilege etc.)
    // -------------------------------------------------------------------

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(SafeProcessHandle ProcessHandle,
        uint DesiredAccess, out SafeAccessTokenHandle TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool LookupPrivilegeValue(string? lpSystemName,
        string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle TokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool DisableAllPrivileges,
        ref TOKEN_PRIVILEGES NewState, uint BufferLength,
        IntPtr PreviousState, IntPtr ReturnLength);

    // -------------------------------------------------------------------
    // user32.dll — foreground/window tracking for the Protection Engine.
    // -------------------------------------------------------------------

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    // -------------------------------------------------------------------
    // High-level helpers built on top of the raw P/Invokes.
    // -------------------------------------------------------------------

    /// <summary>
    /// Enable a single named privilege on the current process's primary token.
    /// Returns true if AdjustTokenPrivileges reports the privilege was actually
    /// assigned. If you don't enable SeProfileSingleProcessPrivilege the standby
    /// list purges silently no-op even though they return success.
    /// </summary>
    internal static bool EnablePrivilege(string privilegeName)
    {
        using var currentProcess = GetCurrentProcess();
        if (!OpenProcessToken(currentProcess,
                (uint)(TokenAccess.AdjustPrivileges | TokenAccess.Query),
                out var tokenHandle))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "OpenProcessToken failed");

        using (tokenHandle)
        {
            if (!LookupPrivilegeValue(null, privilegeName, out var luid))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"LookupPrivilegeValue failed for {privilegeName}");

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new LUID_AND_ATTRIBUTES
                {
                    Luid = luid,
                    Attributes = TokenPrivilegeFlags.SE_PRIVILEGE_ENABLED,
                },
            };

            if (!AdjustTokenPrivileges(tokenHandle, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"AdjustTokenPrivileges failed for {privilegeName}");

            // ERROR_NOT_ALL_ASSIGNED = 1300; if returned, we don't have it.
            return Marshal.GetLastWin32Error() == 0;
        }
    }
}
