# Build & Ship — step by step

This walks through compiling Sentinel and packaging it into a downloadable
installer. Everything here is free.

## 1. Install the toolchain (one-time, ~30 min)

1. **Visual Studio 2022 Community**
   https://visualstudio.microsoft.com/vs/community/
   In the installer, check the **".NET desktop development"** workload.
   Everything else (Git, MSBuild, the .NET 8 SDK) comes with it.

2. **Inno Setup 6**
   https://jrsoftware.org/isdl.php
   Tiny installer (~3 MB), default options are fine.

## 2. Open the project

Either:

- Double-click `Sentinel.sln`, or
- From a terminal:
  ```powershell
  cd path\to\Sentinel
  start Sentinel.sln
  ```

## 3. First build (to confirm everything compiles)

Inside Visual Studio: **Build → Build Solution** (Ctrl+Shift+B). You should
see "Build succeeded" with 0 errors.

Or from the command line:

```powershell
cd Sentinel
dotnet build -c Release
```

## 4. Run it locally (F5)

Press **F5**. Windows will prompt for UAC elevation (because of the
`app.manifest` setting). Click Yes. Sentinel's dark dashboard opens.

If you skip elevation, the app still runs but standby-list operations
silently no-op — you'll see reclamation amounts of ~0 bytes.

## 5. Publish a release build

```powershell
cd Sentinel
dotnet publish -c Release -r win-x64 --self-contained false -o ..\publish
```

This produces `publish\Sentinel.exe` plus its dependent .dlls. The folder
should be ~5 MB. Sanity check: copy it to a temp location, run it, confirm
it still works.

If you want a single-folder build that runs on machines without .NET 8
installed, add `--self-contained true` — the folder swells to ~70 MB but
has no runtime dependency.

## 6. Build the installer

1. Open `installer\sentinel.iss` in the Inno Setup Compiler (right-click the
   file → "Open with Inno Setup Compiler").
2. Menu → **Build → Compile** (or F9).
3. Inno Setup runs LZMA2 compression and writes `installer\Output\Sentinel-Setup.exe`.

That `.exe` is your shippable artifact. ~3-4 MB. Double-click to install on
any Windows 10/11 x64 machine.

## 7. (Optional) Test the installer in a VM

If you have Hyper-V or VirtualBox handy, install a clean Windows 10 VM and
run `Sentinel-Setup.exe` inside it. This is what your examiner will see if
they install your binary on a fresh machine. Worth doing once before the demo.

## 8. (Optional) Publish to GitHub Releases for free

```powershell
git init
git add .
git commit -m "Sentinel v1.0.0"
gh repo create sentinel --public --push --source=.
gh release create v1.0.0 .\installer\Output\Sentinel-Setup.exe --notes "Initial release"
```

(Replace `gh` with the GitHub CLI; install from https://cli.github.com/.)

GitHub now hosts your `.exe` with a stable direct-download URL. You can
include this URL in your project report.

## Troubleshooting

**"NtSetSystemInformation returns 0xC0000061"**
→ The privilege isn't actually held. Run as Administrator and confirm the
app.manifest is being embedded (check `Sentinel.exe`'s Properties →
Digital Signatures / Manifest in Visual Studio Resource Viewer).

**"OpenProcess fails with error 5 on system processes"**
→ Expected for processes in a higher integrity level or session 0. Your
SafetyKernel will refuse these anyway; the error is logged and skipped.

**Memory reclaim shows 0 MB**
→ Either no pressure to reclaim from (a healthy system has little stale
standby), or privileges weren't enabled. Check `%ProgramData%\Sentinel\actions.log`
for EnablePrivilege errors.

**WPF designer errors but the app builds and runs**
→ Visual Studio's XAML designer is finicky. If `dotnet build` succeeds,
ignore the designer warnings.

**Build error CS0103 about `NativeMethods`**
→ Make sure `using Sentinel.Native;` is in the file complaining. All
the source files in this skeleton already include it.
