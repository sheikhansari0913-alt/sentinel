// Sentinel/ViewModels/MainViewModel.cs

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Sentinel.Engines;
using Sentinel.Models;
using Sentinel.Monitoring;

namespace Sentinel.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly SafetyKernel _safety = new();
    private readonly ProtectionEngine _protection = new();
    private readonly MemoryEngine _memory;
    private readonly CpuEngine _cpu;
    private readonly StorageEngine _storage = new();
    private readonly PressureMonitor _monitor;

    public ObservableCollection<ProcessSnapshot> Processes { get; } = new();
    public ObservableCollection<string> RecentActions { get; } = new();

    private string _memoryHeader = "Loading...";
    public string MemoryHeader { get => _memoryHeader; set => Set(ref _memoryHeader, value); }

    private string _standbyHeader = "";
    public string StandbyHeader { get => _standbyHeader; set => Set(ref _standbyHeader, value); }

    private double _pressureScore;
    public double PressureScore { get => _pressureScore; set => Set(ref _pressureScore, value); }

    private string _pressureLabel = "OK";
    public string PressureLabel { get => _pressureLabel; set => Set(ref _pressureLabel, value); }

    private bool _automatic = true;
    public bool AutomaticActionsEnabled
    {
        get => _automatic;
        set { Set(ref _automatic, value); _monitor.AutomaticActionsEnabled = value; }
    }

    public ICommand PurgeLowPriorityCommand { get; }
    public ICommand PurgeFullCommand { get; }
    public ICommand FlushModifiedCommand { get; }
    public ICommand TrimWorkingSetsCommand { get; }
    public ICommand CleanCachesCommand { get; }
    public ICommand WinSxSCleanupCommand { get; }
    public ICommand ThrottleSelectedCommand { get; }
    public ICommand ReleaseSelectedCommand { get; }
    public ICommand TrimSelectedCommand { get; }

    // Selection must survive the 1 Hz grid rebuild, otherwise the user can
    // never select a process and then click Throttle/Trim before the next
    // refresh wipes it. We remember the chosen PID and re-apply it.
    private ProcessSnapshot? _selectedProcess;
    private int? _selectedPid;
    private bool _refreshingGrid;
    public ProcessSnapshot? SelectedProcess
    {
        get => _selectedProcess;
        set
        {
            _selectedProcess = value;
            // Only capture intent when the USER changes the selection — not
            // during our own refresh, which momentarily clears it.
            if (!_refreshingGrid)
                _selectedPid = value?.Pid;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedProcess)));
        }
    }

    public MainViewModel()
    {
        _memory = new MemoryEngine(_safety);
        _cpu = new CpuEngine(_safety);
        _monitor = new PressureMonitor(_memory, _cpu, _protection);

        _memory.EnsurePrivileges();
        _monitor.Tick += OnMonitorTick;

        PurgeLowPriorityCommand = new RelayCommand(_ => Run(() =>
        {
            var r = _memory.PurgeLowPriorityStandbyList();
            AddAction($"Purge low-pri standby: reclaimed {r.DeltaMegabytes:F1} MB");
        }));

        PurgeFullCommand = new RelayCommand(_ =>
        {
            if (MessageBox.Show(
                "Full standby purge destroys the file system cache. " +
                "Subsequent reads will hit disk. Continue?",
                "Confirm aggressive purge",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            Run(() =>
            {
                var r = _memory.PurgeFullStandbyList();
                AddAction($"Full standby purge: reclaimed {r.DeltaMegabytes:F1} MB");
            });
        });

        FlushModifiedCommand = new RelayCommand(_ => Run(() =>
        {
            var r = _memory.FlushModifiedList();
            AddAction($"Flush modified: reclaimed {r.DeltaMegabytes:F1} MB");
        }));

        TrimWorkingSetsCommand = new RelayCommand(_ => Run(() =>
        {
            var r = _memory.TrimAllWorkingSets();
            AddAction($"Trim all working sets: reclaimed {r.DeltaMegabytes:F1} MB");
        }));

        CleanCachesCommand = new RelayCommand(_ => Run(() =>
        {
            var rpt = _storage.CleanCaches();
            AddAction($"Cache cleanup: {rpt.TotalBytes / (1024.0 * 1024.0):F1} MB " +
                      $"across {rpt.Entries.Count} targets");
        }));

        WinSxSCleanupCommand = new RelayCommand(_ =>
        {
            if (MessageBox.Show(
                "WinSxS cleanup may take 20+ minutes. Continue?",
                "Confirm component store cleanup",
                MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
                return;
            Run(() =>
            {
                var r = _storage.RunWinSxSCleanup();
                AddAction($"WinSxS cleanup exit code {r.ExitCode}");
            });
        });

        ThrottleSelectedCommand = new RelayCommand(_ =>
        {
            if (SelectedProcess is null) return;
            if (_cpu.ThrottleToCpuPercent(SelectedProcess, 5))
                AddAction($"Throttled {SelectedProcess.Name} to 5% CPU");
            else
                AddAction($"Throttle refused/failed for {SelectedProcess.Name}");
        });

        ReleaseSelectedCommand = new RelayCommand(_ =>
        {
            if (SelectedProcess is null) return;
            if (_cpu.Release(SelectedProcess))
                AddAction($"Released {SelectedProcess.Name}");
        });

        TrimSelectedCommand = new RelayCommand(_ =>
        {
            if (SelectedProcess is null) return;
            if (_memory.TrimProcessWorkingSet(SelectedProcess))
                AddAction($"Trimmed {SelectedProcess.Name}");
            else
                AddAction($"Trim refused/failed for {SelectedProcess.Name}");
        });
    }

    private void OnMonitorTick(MemoryStatus status, IReadOnlyList<ProcessSnapshot> snapshots, double score)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            PressureScore = score;
            PressureLabel = score switch
            {
                >= 90 => "CRITICAL",
                >= 75 => "HIGH",
                >= 60 => "ELEVATED",
                >= 40 => "MODERATE",
                _     => "OK",
            };

            MemoryHeader =
                $"Physical {Bytes(status.TotalPhysicalBytes - status.AvailablePhysicalBytes)} / " +
                $"{Bytes(status.TotalPhysicalBytes)}  ({status.MemoryLoadPercent}%)";
            StandbyHeader =
                $"Standby {Bytes(status.StandbyBytes)}  (low-pri {Bytes(status.LowPriorityStandbyBytes)})  " +
                $"Modified {Bytes(status.ModifiedBytes)}  Free {Bytes(status.FreeBytes)}";

            // Refresh the process grid — only the top consumers to keep UI snappy.
            _refreshingGrid = true;
            Processes.Clear();
            foreach (var s in snapshots
                .OrderByDescending(p => p.WorkingSetBytes)
                .Take(60))
                Processes.Add(s);

            // Re-apply the user's selection (by PID) so it survives the rebuild.
            if (_selectedPid is int pid)
                SelectedProcess = Processes.FirstOrDefault(p => p.Pid == pid);
            _refreshingGrid = false;
        });
    }

    private void AddAction(string line)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            RecentActions.Insert(0, $"{DateTime.Now:HH:mm:ss}  {line}");
            while (RecentActions.Count > 20) RecentActions.RemoveAt(RecentActions.Count - 1);
        });
    }

    private static void Run(Action a)
    {
        try { a(); }
        catch (Exception ex) { MessageBox.Show(ex.ToString(), "Action failed"); }
    }

    private static string Bytes(ulong b)
    {
        double v = b;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:F1} {units[i]}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _can;
    public RelayCommand(Action<object?> e, Predicate<object?>? c = null) { _execute = e; _can = c; }
    public event EventHandler? CanExecuteChanged
    {
        add    { CommandManager.RequerySuggested += value; }
        remove { CommandManager.RequerySuggested -= value; }
    }
    public bool CanExecute(object? parameter) => _can?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
}
