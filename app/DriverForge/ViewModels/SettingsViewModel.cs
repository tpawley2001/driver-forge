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
