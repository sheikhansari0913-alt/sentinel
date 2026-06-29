// Sentinel/Native/NativeStructs.cs
// Layouts of NT structures we marshal across the P/Invoke boundary.

using System.Runtime.InteropServices;

namespace Sentinel.Native;

/// <summary>
/// Returned by NtQuerySystemInformation(SystemMemoryListInformation).
/// Sizes are in *pages*, not bytes. Multiply by Environment.SystemPageSize.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SYSTEM_MEMORY_LIST_INFORMATION
{
    public nuint ZeroPageCount;
    public nuint FreePageCount;
    public nuint ModifiedPageCount;
    public nuint ModifiedNoWritePageCount;
    public nuint BadPageCount;
    /// <summary>Pages in each of the 8 priority sub-lists (0..7).</summary>
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
    public nuint[] PageCountByPriority;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
    public nuint[] RepurposedPagesByPriority;
    public nuint ModifiedPageCountPageFile;
}

/// <summary>
/// Returned by NtQuerySystemInformation(SystemPerformanceInformation).
/// Most fields are in pages or ticks; we only read a subset.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SYSTEM_PERFORMANCE_INFORMATION
{
    public long IdleProcessTime;
    public long IoReadTransferCount;
    public long IoWriteTransferCount;
    public long IoOtherTransferCount;
    public uint IoReadOperationCount;
    public uint IoWriteOperationCount;
    public uint IoOtherOperationCount;
    public uint AvailablePages;
    public uint CommittedPages;
    public uint CommitLimit;
    public uint PeakCommitment;
    public uint PageFaultCount;
    public uint CopyOnWriteCount;
    public uint TransitionCount;
    public uint CacheTransitionCount;
    public uint DemandZeroCount;
    public uint PageReadCount;
    public uint PageReadIoCount;
    public uint CacheReadCount;
    public uint CacheIoCount;
    public uint DirtyPagesWriteCount;
    public uint DirtyWriteIoCount;
    public uint MappedPagesWriteCount;
    public uint MappedWriteIoCount;
    public uint PagedPoolPages;
    public uint NonPagedPoolPages;
    public uint PagedPoolAllocs;
    public uint PagedPoolFrees;
    public uint NonPagedPoolAllocs;
    public uint NonPagedPoolFrees;
    public uint FreeSystemPtes;
    public uint ResidentSystemCodePage;
    public uint TotalSystemDriverPages;
    public uint TotalSystemCodePages;
    public uint NonPagedPoolLookasideHits;
    public uint PagedPoolLookasideHits;
    public uint AvailablePagedPoolPages;
    public uint ResidentSystemCachePage;
    public uint ResidentPagedPoolPage;
    public uint ResidentSystemDriverPage;
    public uint CcFastReadNoWait;
    public uint CcFastReadWait;
    public uint CcFastReadResourceMiss;
    public uint CcFastReadNotPossible;
    public uint CcFastMdlReadNoWait;
    public uint CcFastMdlReadWait;
    public uint CcFastMdlReadResourceMiss;
    public uint CcFastMdlReadNotPossible;
    public uint CcMapDataNoWait;
    public uint CcMapDataWait;
    public uint CcMapDataNoWaitMiss;
    public uint CcMapDataWaitMiss;
    public uint CcPinMappedDataCount;
    public uint CcPinReadNoWait;
    public uint CcPinReadWait;
    public uint CcPinReadNoWaitMiss;
    public uint CcPinReadWaitMiss;
    public uint CcCopyReadNoWait;
    public uint CcCopyReadWait;
    public uint CcCopyReadNoWaitMiss;
    public uint CcCopyReadWaitMiss;
    public uint CcMdlReadNoWait;
    public uint CcMdlReadWait;
    public uint CcMdlReadNoWaitMiss;
    public uint CcMdlReadWaitMiss;
    public uint CcReadAheadIos;
    public uint CcLazyWriteIos;
    public uint CcLazyWritePages;
    public uint CcDataFlushes;
    public uint CcDataPages;
    public uint ContextSwitches;
    public uint FirstLevelTbFills;
    public uint SecondLevelTbFills;
    public uint SystemCalls;
}

/// <summary>Result of GlobalMemoryStatusEx — overall memory snapshot.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
internal class MEMORYSTATUSEX
{
    public uint dwLength;
    public uint dwMemoryLoad;
    public ulong ullTotalPhys;
    public ulong ullAvailPhys;
    public ulong ullTotalPageFile;
    public ulong ullAvailPageFile;
    public ulong ullTotalVirtual;
    public ulong ullAvailVirtual;
    public ulong ullAvailExtendedVirtual;
    public MEMORYSTATUSEX() { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)); }
}

/// <summary>For LookupPrivilegeValue / AdjustTokenPrivileges.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct LUID
{
    public uint LowPart;
    public int HighPart;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LUID_AND_ATTRIBUTES
{
    public LUID Luid;
    public uint Attributes;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TOKEN_PRIVILEGES
{
    public uint PrivilegeCount;
    public LUID_AND_ATTRIBUTES Privileges; // First (and for us only) entry.
}

internal static class TokenPrivilegeFlags
{
    public const uint SE_PRIVILEGE_ENABLED            = 0x00000002;
    public const uint SE_PRIVILEGE_ENABLED_BY_DEFAULT = 0x00000001;
}

/// <summary>For SetInformationJobObject(JobObjectCpuRateControlInformation).</summary>
[StructLayout(LayoutKind.Explicit)]
internal struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
{
    [FieldOffset(0)] public uint ControlFlags;
    /// <summary>CPU cap, 1..10000 = 0.01% .. 100%.</summary>
    [FieldOffset(4)] public uint CpuRate;
    [FieldOffset(4)] public uint Weight;
    [FieldOffset(4)] public ushort MinRate;
    [FieldOffset(6)] public ushort MaxRate;
}

/// <summary>For GetLastInputInfo — tracks user input idle time.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct LASTINPUTINFO
{
    public uint cbSize;
    public uint dwTime;
}
