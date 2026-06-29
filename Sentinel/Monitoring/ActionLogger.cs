// Sentinel/Monitoring/ActionLogger.cs

using System.IO;

namespace Sentinel.Monitoring;

/// <summary>
/// Append-only audit log of every action Sentinel takes. Lives in
/// %ProgramData%\Sentinel\actions.log. When something breaks during a demo
/// you can prove what we did (and didn't do) to the examiner.
/// </summary>
public sealed class ActionLogger
{
    private static readonly Lazy<ActionLogger> _instance = new(() => new ActionLogger());
    public static ActionLogger Instance => _instance.Value;

    private readonly string _logPath;
    private readonly object _gate = new();

    private ActionLogger()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Sentinel");
        Directory.CreateDirectory(dir);
        _logPath = Path.Combine(dir, "actions.log");
    }

    public string LogPath => _logPath;

    public void Log(string category, string action, string target, string reason)
    {
        var line = $"{DateTime.UtcNow:O}\t{category}\t{action}\t{target}\t{reason}";
        lock (_gate)
        {
            try { File.AppendAllText(_logPath, line + Environment.NewLine); }
            catch { /* logging must never throw into engine code */ }
        }
    }

    public void LogVeto(string action, string target, string reason)
        => Log("VETO", action, target, reason);

    public void LogAction(string action, string target, string detail)
        => Log("ACT", action, target, detail);

    public void LogError(string action, string target, string error)
        => Log("ERR", action, target, error);
}
