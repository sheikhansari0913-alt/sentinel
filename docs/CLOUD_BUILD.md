# Building Sentinel without installing anything

If your local PC doesn't have room for Visual Studio (~10 GB) and Inno Setup,
build the installer in the cloud using **GitHub Actions**. It's free for
public repositories and takes ~4 minutes per build.

## What you need locally

- A web browser. That's it.
- A free GitHub account (sign up at https://github.com).

## What you DON'T need

- Visual Studio (not installed)
- .NET SDK (not installed)
- Inno Setup (not installed)
- Git client (you can use the browser's drag-and-drop upload)

## Step-by-step

### 1. Create the repository

1. Go to https://github.com/new
2. Repository name: `sentinel`
3. Visibility: **Public** (free Actions minutes are generous on public repos)
4. **Do not** initialize with README; leave it empty.
5. Click "Create repository".

### 2. Upload the project

On the empty repo page, click the link **"uploading an existing file"**.

- Drag the entire contents of your unzipped `Sentinel/` folder into the
  browser window. Include the hidden `.github/` folder — that's the
  workflow.
- Commit message: "Initial commit". Click "Commit changes".

> If the drag-and-drop misses the `.github/` folder (some browsers hide
> dotfiles), upload it separately: navigate into the repo, click
> "Add file → Create new file", type `.github/workflows/build.yml` in the
> filename field (the slashes create folders), paste the workflow YAML,
> commit.

### 3. Watch the build

1. Click the **Actions** tab on your repo.
2. You'll see a workflow run named "Build Sentinel" already in progress.
3. Click it. You'll see the steps: Setup .NET, Restore, Publish,
   Install Inno Setup, Compile installer, Upload artifact.
4. After ~4 minutes it shows a green check.

### 4. Download the installer

On the completed workflow run page, scroll to the **Artifacts** section
at the bottom. Click **Sentinel-Setup**. You get a zip; inside is
`Sentinel-Setup.exe`. That's your installer.

### 5. (Optional) Create a public release

Want a permanent public download URL to put in your report? On your local
machine in the browser:

1. Go to your repo → **Releases** (right sidebar) → "Create a new release".
2. Tag: `v1.0.0`. Title: "Sentinel v1.0.0".
3. Click "Publish release".

The workflow detects the tag and re-runs with the release step enabled.
After ~4 minutes the Release page shows `Sentinel-Setup.exe` attached
with a permanent download URL like:
`https://github.com/YOUR-USERNAME/sentinel/releases/download/v1.0.0/Sentinel-Setup.exe`

Paste that URL into your project report.

## Editing code without installing anything

GitHub has a full VS Code editor in the browser.

- On any GitHub repo page, press `.` (period key).
- The page transforms into a VS Code editor with your whole repository.
- Edit any file, then use the Source Control panel on the left to commit.
- Committing triggers the workflow automatically — your new installer is
  ready in ~4 minutes.

URL is `github.dev/YOUR-USERNAME/sentinel` if you want to bookmark it.

## What this DOESN'T solve

You still need a Windows machine to **run and demo** Sentinel. WPF is
Windows-only, and Sentinel manages Windows resources (working sets,
standby list, Job Objects), so the viva machine must run Windows 10/11.
But to run the built `Sentinel-Setup.exe`, you only need:

- Windows 10 or 11 x64
- ~70 MB free for installation
- Administrator access (for the UAC prompt)

You do not need any developer tools on the demo machine.

## Free quota you're using

Public repos on GitHub Free: unlimited Actions minutes on Linux runners,
and Windows runners cost 2x but are unlimited too on public repos as long
as you stay reasonable. You won't hit any limit doing this project.

Private repos get 2,000 free minutes/month, which is plenty for ~50 builds.
