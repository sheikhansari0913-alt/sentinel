// Sentinel/Native/NativeEnums.cs
// Constants and enumerations for the Native API surface we touch.
// Kept in one place so the rest of the code reads cleanly.

namespace Sentinel.Native;

/// <summary>
/// Commands accepted by NtSetSystemInformation when the InformationClass is
/// SystemMemoryListInformation (class 80). These are the operations RAMMap
/// exposes as buttons; we drive them programmatically.
/// </summary>
internal enum SystemMemoryListCommand : int
{
    MemoryCaptureAccessedBits         = 0,
    MemoryCaptureAndResetAccessedBits = 1,
    /// <summary>Trim every process's working set system-wide. Heavy hammer.</summary>
    MemoryEmptyWorkingSets            = 2,
    /// <summary>Flush dirty pages to disk, moving them to the standby list.</summary>
    MemoryFlushModifiedList           = 3,
    /// <summary>Destroy the entire standby (file cache) list. Aggressive.</summary>
    MemoryPurgeStandbyList            = 4,
    /// <summary>Destroy only the low-priority sub-lists 0..1. Safe default.</summary>
    MemoryPurgeLowPriorityStandbyList = 5,
    MemoryCommandMax                  = 6,
}

/// <summary>
/// InformationClass values we pass to NtQuerySystemInformation /
/// NtSetSystemInformation. Only the ones we actually use.
/// </summary>
internal enum SystemInformationClass : int
{
    SystemBasicInformation             = 0,
    SystemPerformanceInformation       = 2,
    SystemProcessInformation           = 5,
    SystemMemoryListInformation        = 80,
    SystemFileCacheInformationEx       = 81,
}

/// <summary>
/// Access rights for OpenProcess. We almost always want
/// PROCESS_QUERY_LIMITED_INFORMATION for read-only enumeration and
/// PROCESS_SET_QUOTA | PROCESS_TERMINATE for trim/job-object actions.
/// </summary>
[Flags]
internal enum ProcessAccessFlags : uint
{
    Terminate                  = 0x0001,
    CreateThread               = 0x0002,
    VmOperation                = 0x0008,
    VmRead                     = 0x0010,
    VmWrite                    = 0x0020,
    DupHandle                  = 0x0040,
    CreateProcess              = 0x0080,
    SetQuota                   = 0x0100,
    SetInformation             = 0x0200,
    QueryInformation           = 0x0400,
    SuspendResume              = 0x0800,
    QueryLimitedInformation    = 0x1000,
    Synchronize                = 0x00100000,
    AllAccess                  = 0x001F0FFF,
}

/// <summary>Priority classes for SetPriorityClass.</summary>
internal enum PriorityClass : uint
{
    IdlePriorityClass        = 0x00000040,
    BelowNormalPriorityClass = 0x00004000,
    NormalPriorityClass      = 0x00000020,
    AboveNormalPriorityClass = 0x00008000,
    HighPriorityClass        = 0x00000080,
    RealtimePriorityClass    = 0x00000100,
    /// <summary>Background mode begin: lowers I/O and memory priority too.</summary>
    ProcessModeBackgroundBegin = 0x00100000,
    ProcessModeBackgroundEnd   = 0x00200000,
}

/// <summary>Token privilege flags.</summary>
[Flags]
internal enum TokenAccess : uint
{
    AssignPrimary    = 0x0001,
    Duplicate        = 0x0002,
    Impersonate      = 0x0004,
    Query            = 0x0008,
    QuerySource      = 0x0010,
    AdjustPrivileges = 0x0020,
    AdjustGroups     = 0x0040,
    AdjustDefault    = 0x0080,
    AdjustSessionId  = 0x0100,
    Read             = 0x00020008,
    Write            = 0x000200E0,
    AllAccess        = 0x000F00FF,
}

internal static class PrivilegeNames
{
    public const string SeDebug                  = "SeDebugPrivilege";
    public const string SeProfileSingleProcess   = "SeProfileSingleProcessPrivilege";
    public const string SeIncreaseQuota          = "SeIncreaseQuotaPrivilege";
    public const string SeIncreaseWorkingSet     = "SeIncreaseWorkingSetPrivilege";
}

/// <summary>
/// Job Object information classes we use, specifically for CPU rate control.
/// </summary>
internal enum JobObjectInfoType : int
{
    BasicLimitInformation       = 2,
    ExtendedLimitInformation    = 9,
    CpuRateControlInformation   = 15,
}

[Flags]
internal enum JobObjectCpuRateControlFlags : uint
{
    Enable          = 0x1,
    WeightBased     = 0x2,
    HardCap         = 0x4,
    NotifyInterval  = 0x8,
    MinMaxRate      = 0x10,
}
