# Sentinel — Intelligent Resource Reallocator

An intelligent Windows resource manager that selectively reclaims RAM, throttles
background CPU consumers via Job Objects, and performs deep storage cleanup —
while a Safety Kernel protects the foreground workflow, system-critical
processes, and processes with unsaved work.

This is **not** a "RAM cleaner." Empty RAM is wasted RAM. Sentinel uses
pressure-driven heuristics to reclaim only stale memory and constrain only
non-essential background processes.

---

## Architecture

```
┌──────────────────────────────────────────────────────────────┐
│  Sentinel.UI (WPF, .NET 8)                                   │
│     MainWindow ── MainViewModel                              │
└──────────────────────────────┬───────────────────────────────┘
                               │
┌──────────────────────────────▼───────────────────────────────┐
│  Monitoring                                                  │
│     PressureMonitor  ── ActionLogger                         │
└──────────────────────────────┬───────────────────────────────┘
                               │
┌──────────────────────────────▼───────────────────────────────┐
│  Engines                                                     │
│     ProtectionEngine ── decides what's untouchable           │
│     MemoryEngine     ── working set / standby list / mod list│
│     CpuEngine        ── Job Object throttling, priorities    │
│     StorageEngine    ── WinSxS, browser caches, logs         │
│     SafetyKernel     ── final veto layer before any action   │
└──────────────────────────────┬───────────────────────────────┘
                               │
┌──────────────────────────────▼───────────────────────────────┐
│  Native (P/Invoke)                                           │
│     NtQuerySystemInformation, NtSetSystemInformation,        │
│     NtSuspendProcess, EmptyWorkingSet, Job Objects,          │
│     SetWinEventHook, GetLastInputInfo, AdjustTokenPrivileges │
└──────────────────────────────────────────────────────────────┘
```

---

## Free toolchain (everything required)

| Need | Tool | Cost | Link |
|------|------|------|------|
| Compiler / IDE | Visual Studio 2022 Community | Free | https://visualstudio.microsoft.com/vs/community/ |
| .NET SDK | .NET 8 SDK | Free | bundled with VS or https://dotnet.microsoft.com/download |
| Installer | Inno Setup 6 | Free | https://jrsoftware.org/isdl.php |
| Source control | Git + GitHub | Free | https://git-scm.com/ |
| (Optional CI) | GitHub Actions | Free for public repos | github.com |

When installing Visual Studio, pick the workload **".NET desktop development"**.
That gives you `dotnet`, WPF, and the build tools in one shot.

---

## Build the .exe

From the project root (`Sentinel/`):

```powershell
cd Sentinel
dotnet restore
dotnet publish -c Release -r win-x64 --self-contained false -o ..\publish
```

Output: `publish\Sentinel.exe` plus its dependencies.

`--self-contained false` keeps it small (~5 MB). Use `--self-contained true`
if you want a single-folder copy that runs on machines without .NET 8 installed
(adds ~70 MB).

You can also just press **F5** in Visual Studio to build and run.

> **Run as Administrator.** Many of the Native APIs (`NtSetSystemInformation`
> for standby list, `OpenProcess` on system processes, Job Object creation
> for foreign processes) require elevation. The bundled `app.manifest` sets
> `requireAdministrator`, so Windows will prompt automatically.

---

## Make the installer (downloadable `Sentinel-Setup.exe`)

1. Install Inno Setup 6.
2. Open `installer\sentinel.iss` in the Inno Setup Compiler.
3. **Build → Compile.**
4. Output appears in `installer\Output\Sentinel-Setup.exe`.

That single `.exe` is your shippable installer. Double-click it on any Windows
10/11 machine and it installs to `Program Files`, creates Start Menu shortcuts,
and registers an uninstaller — exactly like commercial software.

If you want to host the download for free:

- **GitHub Releases** — upload the `.exe` to a Release tag in your repo. Direct
  download links, no hosting cost, version history built in.
- **GitHub Pages** — static site advertising the project, linking to the
  Release. Free.

---

## Demo plan (for your viva)

This is what to actually do in front of your examiners. Have a script.

1. **Show the Protection Engine working.**
   Open Notepad, type something. In Sentinel, point at the protected-list
   panel — `notepad.exe` should be flagged "Foreground / Protected" with a
   reason chain (foreground window, recent input, owns visible top-level
   window). Click "Trim All" — Notepad's working set is *not* touched.

2. **Show the Memory Engine reclaim.**
   Launch the included `MemoryHog.exe` test stub (allocates 1 GB). Show RAMMap
   in the corner. Click "Reclaim Memory" in Sentinel. Standby list drops,
   `MemoryHog`'s working set is collapsed. Open Notepad again — it opens
   instantly because hot working sets weren't touched.

3. **Show CPU throttling without killing.**
   Run a CPU-burner test stub. Sentinel detects sustained >80% CPU on a
   non-foreground process. It puts it in a Job Object capped at 5% CPU.
   Process stays alive, state preserved, CPU usage collapses. Then click
   "Release" — full speed returns. **No `TerminateProcess` was called.**

4. **Show the Safety Kernel veto.**
   Manually try to throttle `csrss.exe` from the UI. The Safety Kernel
   refuses and logs the refusal with a reason. Show the action log file.

5. **Show storage reclamation.**
   Run "Deep Clean" — show WinSxS reclaimed bytes, browser cache cleared,
   Event Logs archived. Numbers in the report.

If you can do those five things live, you pass.

---

## Project layout

```
Sentinel/
├── Sentinel.sln                       Solution file
├── README.md                          This file
├── Sentinel/                          Main app project
│   ├── Sentinel.csproj
│   ├── app.manifest                   UAC requireAdministrator
│   ├── App.xaml / App.xaml.cs
│   ├── Native/
│   │   ├── NativeMethods.cs           All P/Invoke signatures
│   │   ├── NativeStructs.cs           SYSTEM_PROCESS_INFORMATION, etc.
│   │   └── NativeEnums.cs             SystemMemoryListInformation enums
│   ├── Models/
│   │   ├── ProcessSnapshot.cs
│   │   └── MemoryStatus.cs
│   ├── Engines/
│   │   ├── ProtectionEngine.cs        Foreground & protection scoring
│   │   ├── MemoryEngine.cs            Working set + standby list ops
│   │   ├── CpuEngine.cs               Job Objects + priorities
│   │   ├── StorageEngine.cs           WinSxS, caches, logs
│   │   └── SafetyKernel.cs            Final veto layer
│   ├── Monitoring/
│   │   ├── PressureMonitor.cs         Heuristic governor
│   │   └── ActionLogger.cs            Audit log
│   ├── ViewModels/
│   │   └── MainViewModel.cs
│   └── Views/
│       └── MainWindow.xaml / .xaml.cs
├── installer/
│   └── sentinel.iss                   Inno Setup script
└── docs/
    ├── ARCHITECTURE.md
    └── THREAT_MODEL.md                For your report appendix
```

---

## What's still on you to finish

I built the foundation completely — Native API layer, Protection Engine,
Memory Engine, CPU Engine, Safety Kernel, Pressure Monitor, and a working
WPF UI. To turn this into your final submission you still need to:

- **Polish the UI.** Add charts (LiveCharts2 is free) for memory pressure over
  time. The current UI is functional but bare.
- **Flesh out StorageEngine.** I included the WinSxS path via DISM and browser
  cache stubs — expand the cache list (Discord, Slack, Teams, JetBrains, etc.)
  per the architecture doc.
- **Write the report.** Use `docs/ARCHITECTURE.md` and `docs/THREAT_MODEL.md`
  as starting points. Examiners love a threat model.
- **Write tests.** At minimum a `MemoryHog.exe` and `CpuBurner.exe` test stub
  so your demo is reproducible.

All of this is straightforward extension work, not new architecture.
