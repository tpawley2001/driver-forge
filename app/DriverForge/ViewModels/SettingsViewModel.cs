using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriverForge.Core;

namespace DriverForge.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    readonly MainViewModel main;

    public SettingsViewModel(MainViewModel main)
    {
        this.main = main;
        Ignored = new(main.Settings.IgnoredDevices.Order());
        IgnoredApps = new(IgnoredAppRows());
    }

    // ── Apps (docs/APPS.md) ──
    public ObservableCollection<KeyValuePair<string, string>> IgnoredApps { get; }

    IEnumerable<KeyValuePair<string, string>> IgnoredAppRows() =>
        main.Settings.IgnoredApps.OrderBy(kv => kv.Key)
            .Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value.Length == 0 ? "every version" : "version " + kv.Value));

    public void RefreshIgnoredApps()
    {
        IgnoredApps.Clear();
        foreach (var row in IgnoredAppRows()) IgnoredApps.Add(row);
    }

    [RelayCommand]
    async Task UnignoreApp(string id)
    {
        main.Settings.IgnoredApps.Remove(id);
        main.SaveSettings();
        RefreshIgnoredApps();
        await main.AppsPage.LoadAsync(force: true);
    }

    public bool AutoUpdateApps
    {
        get => main.Settings.AutoUpdateApps;
        set { main.Settings.AutoUpdateApps = value; main.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AppsRestorePoint
    {
        get => main.Settings.AppsRestorePoint;
        set { main.Settings.AppsRestorePoint = value; main.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AppsAnyScope
    {
        get => main.Settings.AppsAnyScope;
        set { main.Settings.AppsAnyScope = value; main.SaveSettings(); OnPropertyChanged(); }
    }

    public ObservableCollection<string> Ignored { get; }

    public bool TestMode
    {
        get => main.Settings.TestMode;
        set { main.Settings.TestMode = value; main.SaveSettings(); OnPropertyChanged(); main.Drivers.Refresh(); }
    }

    public bool CreateRestorePoint
    {
        get => main.Settings.CreateRestorePoint;
        set { main.Settings.CreateRestorePoint = value; main.SaveSettings(); OnPropertyChanged(); }
    }

    public bool RestartWhenNeeded
    {
        get => main.Settings.RestartWhenNeeded;
        set { main.Settings.RestartWhenNeeded = value; main.SaveSettings(); OnPropertyChanged(); }
    }

    public string DataFolder => main.DataDirectory;

    public IReadOnlyList<ScanSchedule> Schedules { get; } = Enum.GetValues<ScanSchedule>();

    [ObservableProperty] private string? scheduleError;

    /// <summary>Applied to Task Scheduler at once; the setting only changes when that worked.</summary>
    public ScanSchedule ScanSchedule
    {
        get => main.Settings.ScanSchedule;
        set
        {
            try
            {
                ScheduledScan.Apply(value, Environment.ProcessPath!);
                main.Settings.ScanSchedule = value;
                main.SaveSettings();
                ScheduleError = null;
            }
            catch (EngineException ex) { ScheduleError = ex.Message; }
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    void Unignore(string instanceId)
    {
        main.Settings.IgnoredDevices.Remove(instanceId);
        main.SaveSettings();
        Ignored.Remove(instanceId);
        main.Drivers.Refresh();
    }

    public void Add(string instanceId)
    {
        if (!Ignored.Contains(instanceId)) Ignored.Add(instanceId);
    }
}
