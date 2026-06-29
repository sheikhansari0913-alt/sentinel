# Sentinel — Architecture

## Design philosophy

Most consumer "PC optimizers" implement a misunderstanding of how a modern
operating system uses RAM. They call `EmptyWorkingSet` on every process and
declare victory when Task Manager shows more "free" memory. This is harmful:
Windows deliberately fills unused memory with the **standby list** — a copy of
recently read file pages that makes subsequent reads instant. Dumping that
cache and forcing every process to page out makes the system measurably
slower for minutes afterward.

Sentinel inverts this. It treats free RAM as wasted RAM by default. It only
reclaims when there is real evidence of pressure (high memory load, foreground
page-faulting, sustained background CPU use), and even then it prefers the
**least destructive** action that resolves the symptom:

1. Flush dirty pages to disk (no cache loss).
2. Drop only low-priority standby pages (stale cache).
3. Drop the full standby list (only if explicitly user-requested).
4. Trim individual non-protected processes (per-process, surgical).
5. Throttle CPU via Job Objects (reversible, no termination).

## Layered architecture

```
UI            ── MainWindow.xaml, MainViewModel
Monitoring    ── PressureMonitor, ActionLogger
Engines       ── ProtectionEngine, MemoryEngine, CpuEngine,
                  StorageEngine, SafetyKernel
Native        ── NativeMethods, NativeStructs, NativeEnums
                  (P/Invoke wrappers for ntdll, kernel32, psapi, advapi32, user32)
```

The Native layer is isolated from the Engines so we have a single auditable
surface listing every Windows API call the tool makes. An examiner can read
`Native/NativeMethods.cs` end to end and know what privileges this program
exercises.

## The SafetyKernel

Every destructive action passes through `SafetyKernel.Gate(process, action)`.
The kernel vetoes in this order:

1. **Hard blocklist** — `smss`, `csrss`, `wininit`, `services`, `lsass`,
   `winlogon`, `dwm`, `explorer`, and `Sentinel.exe` itself.
2. **Reserved PIDs** — 0 (Idle) and 4 (System).
3. **Self** — the running Sentinel process.
4. **Kernel-critical** — `IsProcessCritical` returns true.
5. **Foreground** — the user is interacting with this window right now.
6. **Protection score >= 50** — see scoring below.
7. **Not owned by the interactive user** — refuse most actions on services.

Every veto is logged with a reason. Those refusal lines are the proof you
point at when defending the safety claim.

## Protection scoring

```
+100  foreground window owner
 +60  has a visible top-level window
 +50  in the lineage of the foreground process
 +40  recent keyboard/mouse input
 +30  started by interactive user in last 5 min
```

Threshold = 50. Decay is implicit — scores are recomputed every second from
fresh observations, so as soon as a window loses focus and isn't touched its
protection naturally drops.

## Memory engine — what makes it different

The `SYSTEM_MEMORY_LIST_INFORMATION` structure (class 80 of
`NtQuerySystemInformation`) exposes the standby list broken into 8 priority
sub-lists. Low priority sub-lists (0..1) hold the cache pages the OS itself
considers least valuable. Sentinel's default reclamation purges only those
sub-lists via `MemoryPurgeLowPriorityStandbyList`. The full purge
(`MemoryPurgeStandbyList`) exists but is locked behind explicit
user confirmation in the UI because it nukes hot cache.

Few "RAM cleaners" on the market understand this distinction. RAMMap from
Sysinternals can perform the per-priority purge but only manually — it does
not run as a service or react to pressure. Sentinel does both.

## CPU engine — constrain, don't kill

Killing a process to "free up cores" can destabilise the system and lose user
work. Sentinel's escalation ladder:

1. `SetPriorityClass(IDLE_PRIORITY_CLASS)`
2. `SetPriorityClass(PROCESS_MODE_BACKGROUND_BEGIN)` — combined CPU/I/O/memory
   priority drop, ideal for sync clients and indexers.
3. Job Object with `JobObjectCpuRateControlInformation` and `HardCap` —
   absolute ceiling on the CPU% the process can consume. Reversible by
   closing the job handle.

Termination is available in the UI for unresponsive processes but is never
automatic.

## Pressure-driven governance

`PressureMonitor` ticks once per second. It computes a score from:

```
score  =  max(0, memoryLoadPct - 50) * 1.0
       +  max(0, commitPct - 70) * 1.0
       +  max(0, topProcessCpu - 30) * 0.5
```

Score >= 60  →  `FlushModifiedList`
Score >= 75  →  `PurgeLowPriorityStandbyList`
Score >= 90  →  per-process working-set trim on top non-protected leeches

Automatic actions are deferred if the foreground process is at >50% CPU
(the user is actively working) — purging the standby list while a build is
running makes the build slower.

## Reversibility

- **CPU throttling**: closing the Job Object handle dissolves the cap.
- **Priority changes**: tracked, reset on demand.
- **Storage deletion**: every deleted file is first moved into
  `%ProgramData%\Sentinel\Quarantine\<timestamp>_<label>\<original-path>`.
  A 7-day retention task purges old quarantine items.
- **Memory reclaim**: not reversible by nature (the OS will re-cache as files
  are touched again), but the action log records before/after available bytes
  so reclaim impact is auditable.

## Required privileges and why

| Privilege | Why we need it |
|---|---|
| `SeProfileSingleProcessPrivilege` | Required for `NtSetSystemInformation(SystemMemoryListInformation)`. Without it the standby list operations silently no-op. |
| `SeDebugPrivilege` | Required for `OpenProcess` against processes the calling token doesn't own — e.g. inspecting and trimming system-session processes. |
| `SeIncreaseQuotaPrivilege` | Required for `SetProcessWorkingSetSizeEx` to set working set bounds. |
| Administrator token | Required for `CreateJobObject` + `AssignProcessToJobObject` against arbitrary processes, and for DISM-based WinSxS cleanup. |

All three privileges are enabled once at startup in `MemoryEngine.EnsurePrivileges`.
