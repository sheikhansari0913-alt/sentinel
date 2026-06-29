# Sentinel — Threat Model

This document describes the attack surface of Sentinel and the mitigations
baked into its design. Including a threat model in your report demonstrates
that you've thought about Sentinel as a piece of privileged software, not
just a feature checklist.

## Assets

| Asset | Why it matters |
|---|---|
| **The elevated Sentinel process** | Runs as Administrator; can change other processes' priority, trim memory, throttle CPU. A compromise of Sentinel is effectively a compromise of the local machine. |
| **The audit log** | `%ProgramData%\Sentinel\actions.log`. Tampering would mask malicious actions. |
| **The quarantine store** | `%ProgramData%\Sentinel\Quarantine`. Stages files for 7 days before deletion. Could contain user data. |
| **Critical OS processes** | If Sentinel acts on `csrss.exe` / `lsass.exe` / `services.exe` etc., the system blue-screens or logs the user out. |

## Trust boundaries

```
                    +----------------------+
                    |  Sentinel.exe (admin) |   <-- TCB starts here
                    +-----------+----------+
                                |
            +-------------------+-------------------+
            |                                       |
        Native API                              Disk I/O
   (ntdll, kernel32, advapi32)        (quarantine, log, DISM)
            |                                       |
        Kernel                                File system
```

The only trust boundary inside the app is between the **UI thread**
(handles user clicks) and the **engines** (executes privileged calls). The
SafetyKernel sits on that boundary.

## Threats and mitigations

### T1. The user clicks "Throttle 5%" on a critical system process

**Risk:** System hang or BSOD.

**Mitigations:**
- SafetyKernel hard blocklist of known critical executables.
- `IsProcessCritical` check vetos kernel-marked critical processes.
- All UI actions route through `SafetyKernel.Gate` before any handle is
  even opened.

### T2. The user clicks "Purge full standby" while doing heavy I/O

**Risk:** Active workloads (file copies, builds) slow to a crawl as the cache
disappears.

**Mitigations:**
- The full purge requires explicit confirmation via `MessageBox`.
- The PressureMonitor's automatic actions defer if any foreground process is
  consuming >50% CPU.

### T3. Sentinel's privileges abused by another local process

**Risk:** A non-admin local attacker might try to drive Sentinel's UI to
perform actions on their behalf.

**Mitigations:**
- WPF runs in the elevated session; UI Automation cross-integrity messaging
  is blocked by UIPI (User Interface Privilege Isolation).
- No IPC surface (named pipe, socket, COM) exposed by the current version.
  If you add a service split later, ACL the named pipe to the installing
  user's SID and add a token-SID match check on connect.

### T4. Tampering with the audit log

**Risk:** A local attacker overwrites `actions.log` to hide an attack.

**Mitigations:** (Current version is best-effort.) Production hardening:
- Open the log with `FileShare.Read` only, so external writers can't truncate
  while we hold it.
- Optionally chain entries with a hash of the previous line (tamper-evident
  log).

### T5. Quarantine path traversal

**Risk:** A malicious filename like `..\..\Windows\System32\foo.dll` causes
quarantine moves to escape the staging folder.

**Mitigations:**
- Quarantine paths are built from `Path.Combine(quarantineRoot,
  GetRelativePath(sourceDir, file))` — `GetRelativePath` does not produce
  `..` traversals when both arguments are absolute and one is a descendant
  of the other. Sentinel only quarantines from well-known cache directories,
  not user-controlled paths.

### T6. DLL planting on dism.exe call

**Risk:** Calling `dism.exe` without a fully qualified path could load a
DLL from the current working directory.

**Mitigations:**
- Future hardening: pass `C:\Windows\System32\dism.exe` explicitly and set
  the working directory to `System32` before launch. The current code uses
  the unqualified name and trusts the elevated process's clean PATH.

### T7. Race between snapshot and action

**Risk:** Between enumerating a PID and acting on it, the original process
exits and its PID is reused by something we shouldn't touch.

**Mitigations:**
- `OpenProcess` with limited access fails fast on reused PIDs because the
  start time differs. For higher assurance, take a process handle once
  during enumeration and reuse it for the action.

## Out of scope (for this version)

- Kernel-mode component (driver). Adding one expands the TCB significantly
  and requires WHQL signing for production deployment.
- Inter-process communication with a separate service. The current monolith
  is simpler to audit and ship; a service split is a v2 hardening.
- Remote management. Sentinel is single-machine, single-user.

## Privileges Sentinel holds (least-privilege audit)

Sentinel requests the following at startup and uses them in exactly these
places:

| Privilege | Used in | Used for |
|---|---|---|
| `SeProfileSingleProcessPrivilege` | `MemoryEngine.IssueMemoryCommand` | `NtSetSystemInformation(SystemMemoryListInformation)` |
| `SeDebugPrivilege` | `MemoryEngine.TrimProcessWorkingSet`, `CpuEngine.*` | `OpenProcess` against foreign processes |
| `SeIncreaseQuotaPrivilege` | `MemoryEngine.TrimProcessWorkingSet` | `SetProcessWorkingSetSizeEx` |
| Administrator group | `StorageEngine.RunWinSxSCleanup` | Spawning `dism.exe` |

No other privileges are requested. Sentinel does not impersonate, does not
load kernel drivers, does not modify the Windows Registry outside its own
HKCU\Software\Sentinel key (none today).
