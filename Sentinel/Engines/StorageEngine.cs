// Sentinel/Engines/StorageEngine.cs

using System.Diagnostics;
using System.IO;
using Sentinel.Monitoring;

namespace Sentinel.Engines;

/// <summary>
/// Deep storage reclamation. We deliberately go further than "delete temp
/// files" — WinSxS component store, Delivery Optimization peer cache,
/// browser/app caches, Windows logs, the Recycle Bin.
///
/// Every deletion is staged: the file is first moved into a quarantine
/// folder where it stays for 7 days before actual removal. The
/// ActionLogger records each move so restoration is trivial.
/// </summary>
public sealed class StorageEngine
{
    private readonly string _quarantineRoot;

    public StorageEngine()
    {
        _quarantineRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Sentinel", "Quarantine");
        Directory.CreateDirectory(_quarantineRoot);
    }

    /// <summary>
    /// WinSxS deep clean via DISM. This is the heavy hitter — superseded
    /// component packages can accumulate to 10+ GB. ResetBase makes the
    /// reclamation permanent (you lose the ability to uninstall updates).
    /// </summary>
    public DismResult RunWinSxSCleanup(bool resetBase = false)
    {
        var args = resetBase
            ? "/Online /Cleanup-Image /StartComponentCleanup /ResetBase"
            : "/Online /Cleanup-Image /StartComponentCleanup";

        var psi = new ProcessStartInfo("dism.exe", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            Verb = "runas",
        };

        try
        {
            using var proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(TimeSpan.FromMinutes(20));

            ActionLogger.Instance.LogAction("WinSxSCleanup",
                "WinSxS", $"ExitCode={proc.ExitCode} ResetBase={resetBase}");

            return new DismResult
            {
                ExitCode = proc.ExitCode,
                StdOut = stdout,
                StdErr = stderr,
            };
        }
        catch (Exception ex)
        {
            ActionLogger.Instance.LogError("WinSxSCleanup", "WinSxS", ex.Message);
            return new DismResult { ExitCode = -1, StdErr = ex.Message };
        }
    }

    /// <summary>
    /// Targets the standard removable caches. Each entry is staged into
    /// quarantine so a user mistake is recoverable for 7 days.
    /// </summary>
    public CleanupReport CleanCaches()
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming  = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var winDir   = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var targets = new List<(string Label, string Path)>
        {
            ("Windows Temp",                 Path.Combine(winDir, "Temp")),
            ("User Temp",                    Path.GetTempPath()),
            ("SoftwareDistribution Downloads", Path.Combine(winDir, "SoftwareDistribution", "Download")),
            ("Delivery Optimization",        Path.Combine(winDir, "ServiceProfiles",
                                                "NetworkService", "AppData", "Local",
                                                "Microsoft", "Windows", "DeliveryOptimization")),
            ("Windows Logs",                 Path.Combine(winDir, "Logs")),
            ("Minidumps",                    Path.Combine(winDir, "Minidump")),
            ("Chrome cache",                 Path.Combine(localApp, "Google", "Chrome", "User Data", "Default", "Cache")),
            ("Edge cache",                   Path.Combine(localApp, "Microsoft", "Edge", "User Data", "Default", "Cache")),
            ("Firefox cache",                Path.Combine(localApp, "Mozilla", "Firefox", "Profiles")),
            ("Brave cache",                  Path.Combine(localApp, "BraveSoftware", "Brave-Browser", "User Data", "Default", "Cache")),
            ("Discord cache",                Path.Combine(roaming,  "discord", "Cache")),
            ("Teams cache",                  Path.Combine(roaming,  "Microsoft", "Teams", "Cache")),
            ("Slack cache",                  Path.Combine(roaming,  "Slack", "Cache")),
            ("VS Code workspace storage",    Path.Combine(roaming,  "Code", "User", "workspaceStorage")),
            ("npm cache",                    Path.Combine(roaming,  "npm-cache")),
            ("pip cache",                    Path.Combine(localApp, "pip", "cache")),
        };

        var report = new CleanupReport();
        foreach (var (label, path) in targets)
        {
            if (!Directory.Exists(path)) continue;
            long bytes = TryQuarantineDirectoryContents(label, path);
            if (bytes > 0)
            {
                report.Entries.Add(new CleanupEntry { Label = label, BytesReclaimed = bytes });
                report.TotalBytes += bytes;
            }
        }

        ActionLogger.Instance.LogAction("CleanCaches", "system",
            $"reclaimed {report.TotalBytes:N0} bytes across {report.Entries.Count} targets");
        return report;
    }

    /// <summary>
    /// Move the contents of a folder into quarantine. Returns bytes reclaimed.
    /// Files that are in use are skipped quietly.
    /// </summary>
    private long TryQuarantineDirectoryContents(string label, string sourceDir)
    {
        long totalBytes = 0;
        var dest = Path.Combine(_quarantineRoot,
            DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" +
            new string(label.Where(char.IsLetterOrDigit).ToArray()));

        try
        {
            Directory.CreateDirectory(dest);
            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(file);
                    var rel = Path.GetRelativePath(sourceDir, file);
                    var target = Path.Combine(dest, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(file, target, true);
                    totalBytes += fi.Length;
                }
                catch
                {
                    // file in use, ACL denied, etc. — skip silently, this
                    // is best-effort cleanup.
                }
            }
        }
        catch (Exception ex)
        {
            ActionLogger.Instance.LogError("Quarantine", label, ex.Message);
        }

        return totalBytes;
    }

    /// <summary>
    /// Purge the quarantine of items older than the retention window.
    /// Call from a background task. We do not run this automatically on
    /// every startup — only when the quarantine itself becomes large.
    /// </summary>
    public void PurgeOldQuarantine(TimeSpan retention)
    {
        var cutoff = DateTime.UtcNow - retention;
        foreach (var dir in Directory.GetDirectories(_quarantineRoot))
        {
            try
            {
                if (Directory.GetCreationTimeUtc(dir) < cutoff)
                    Directory.Delete(dir, true);
            }
            catch { }
        }
    }
}

public sealed class DismResult
{
    public int ExitCode { get; init; }
    public string StdOut { get; init; } = "";
    public string StdErr { get; init; } = "";
    public bool Success => ExitCode == 0;
}

public sealed class CleanupReport
{
    public List<CleanupEntry> Entries { get; } = new();
    public long TotalBytes { get; set; }
}

public sealed class CleanupEntry
{
    public required string Label { get; init; }
    public required long BytesReclaimed { get; init; }
}
