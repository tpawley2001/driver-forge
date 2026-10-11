using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriverForge.Core;

namespace DriverForge.ViewModels;

public enum DeviceFilter { Attention, Missing, Updates, Skipped, UpToDate }

public sealed partial class FilterChip(DeviceFilter filter, DriversViewModel owner) : ObservableObject
{
    public DeviceFilter Filter { get; } = filter;
    [ObservableProperty] private string label = "";
    [ObservableProperty] private bool isActive;
    [RelayCommand] void Pick() => owner.Filter = Filter;
}

public sealed record DeviceGroup(string Title, IReadOnlyList<DeviceRowViewModel> Items);

public sealed partial class DriversViewModel : ObservableObject
{
    readonly MainViewModel main;
    List<DeviceRowViewModel> rows = [];
    bool building;

    public DriversViewModel(MainViewModel main)
    {
        this.main = main;
        Chips = new(Enum.GetValues<DeviceFilter>().Select(f => new FilterChip(f, this)));
        UpdateChips();
    }

    public ObservableCollection<FilterChip> Chips { get; }
    public ObservableCollection<DeviceGroup> Groups { get; } = [];

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string busyText = "";
    [ObservableProperty] private string? error;
    [ObservableProperty] private bool hasReport;
    [ObservableProperty] private string subtitle = "Not scanned yet";
    [ObservableProperty] private string warnings = "";
    [ObservableProperty] private int missingCount;
    [ObservableProperty] private int updateCount;
    [ObservableProperty] private int upToDateCount;
    [ObservableProperty] private int skippedCount;
    [ObservableProperty] private DeviceRowViewModel? selected;
    [ObservableProperty] private string selectionSummary = "";
    [ObservableProperty] private string installLabel = "Nothing selected";
    [ObservableProperty] private bool canInstall;
    [ObservableProperty] private DeviceFilter filter = DeviceFilter.Attention;

    public ScanResult? Report { get; private set; }
    public int AttentionCount => MissingCount + UpdateCount;
    public string UpToDateText => UpToDateCount == 1
        ? "1 device is already on the best available driver"
        : $"{UpToDateCount} devices are already on the best available driver";

    partial void OnUpToDateCountChanged(int value) => OnPropertyChanged(nameof(UpToDateText));

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

    public bool TestMode => main.Settings.TestMode;

    partial void OnFilterChanged(DeviceFilter value)
    {
        UpdateChips();
        Rebuild();
    }

    partial void OnSelectedChanged(DeviceRowViewModel? oldValue, DeviceRowViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    [RelayCommand]
    void Select(DeviceRowViewModel row) => Selected = row;

    [RelayCommand]
    public async Task ScanAsync()
    {
        if (IsBusy) return;
        Error = null;
        IsBusy = true;
        BusyText = "Scanning devices…";
        var progress = new Progress<string>(t => BusyText = t);
        try
        {
            Load(await main.Engine.ScanAsync(progress));
        }
        catch (Exception ex) when (ex is EngineException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Load(ScanResult report)
    {
        Report = report;
        building = true;
        var assessments = Recommender.Assess(report, main.Settings.IgnoredDevices);
        rows = assessments
            .Select(a => new DeviceRowViewModel(a, OnRowSelectionChanged))
            .OrderBy(r => r.State)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        building = false;

        MissingCount = rows.Count(r => IsMissing(r.State));
        UpdateCount = rows.Count(r => r.State == DeviceState.Update);
        SkippedCount = rows.Count(r => r.State == DeviceState.Skipped);
        UpToDateCount = rows.Count(r => r.State == DeviceState.UpToDate);
        OnPropertyChanged(nameof(AttentionCount));
        Subtitle = $"{rows.Count} devices scanned · {report.At:t} · {report.Os}";
        Warnings = string.Join("\n", report.Warnings);
        HasReport = true;
        main.SetBadge(AttentionCount);

        UpdateChips();
        Rebuild();
        Selected = rows.FirstOrDefault(r => r.State is DeviceState.Missing or DeviceState.Update)
            ?? rows.FirstOrDefault(r => r.State == DeviceState.NoDriverFound) ?? rows.FirstOrDefault();
        OnRowSelectionChanged();
    }

    /// <summary>Not working, whether or not a source has a driver for it.</summary>
    static bool IsMissing(DeviceState s) => s is DeviceState.Missing or DeviceState.NoDriverFound;

    void UpdateChips()
    {
        foreach (var c in Chips)
        {
            c.IsActive = c.Filter == Filter;
            c.Label = c.Filter switch
            {
                DeviceFilter.Attention => $"Needs attention ({AttentionCount})",
                DeviceFilter.Missing => $"Missing ({MissingCount})",
                DeviceFilter.Updates => $"Updates ({UpdateCount})",
                DeviceFilter.Skipped => $"Skipped ({SkippedCount})",
                _ => $"Up to date ({UpToDateCount})",
            };
        }
    }

    void Rebuild()
    {
        Groups.Clear();
        void Add(string title, Func<DeviceRowViewModel, bool> pick)
        {
            var items = rows.Where(pick).ToList();
            if (items.Count > 0) Groups.Add(new(title, items));
        }
        bool all = Filter == DeviceFilter.Attention;
        if (all || Filter == DeviceFilter.Missing) Add("MISSING DRIVERS", r => IsMissing(r.State));
        if (all || Filter == DeviceFilter.Updates) Add("UPDATES AVAILABLE", r => r.State == DeviceState.Update);
        if (Filter == DeviceFilter.Skipped) Add("SKIPPED", r => r.State == DeviceState.Skipped);
        if (Filter == DeviceFilter.UpToDate) Add("UP TO DATE", r => r.State == DeviceState.UpToDate);
        // Hand-picked drivers stay visible wherever they live.
        if (all) Add("CHOSEN BY YOU", r => r.IsChecked && r.State is not (DeviceState.Missing or DeviceState.NoDriverFound or DeviceState.Update));
    }

    void OnRowSelectionChanged()
    {
        if (building) return;
        var picked = Picked();
        long download = picked.Where(r => !r.Chosen!.Offer.IsLocal).Sum(r => r.Chosen!.Offer.DownloadBytes ?? 0);
        SelectionSummary = picked.Count == 0 ? ""
            : $"{picked.Count} selected" + (download > 0 ? $" · {download / (1024.0 * 1024):0.#} MB to download" : "");
        InstallLabel = picked.Count switch
        {
            0 => "Nothing selected",
            1 => TestMode ? "Test-install 1 driver" : "Install 1 driver",
            var n => TestMode ? $"Test-install {n} drivers" : $"Install {n} drivers",
        };
        CanInstall = picked.Count > 0 && !IsBusy;
    }

    public List<DeviceRowViewModel> Picked() => rows.Where(r => r.IsChecked && r.Chosen is not null).ToList();

    public void SelectByName(string name)
    {
        var row = rows.FirstOrDefault(r => r.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (row is null) return;
        Filter = row.State switch
        {
            DeviceState.Missing or DeviceState.NoDriverFound or DeviceState.Update => DeviceFilter.Attention,
            DeviceState.UpToDate => DeviceFilter.UpToDate,
            _ => DeviceFilter.Skipped,
        };
        Selected = row;
    }

    [RelayCommand]
    void SelectAllRecommended()
    {
        foreach (var r in rows) r.IsChecked = r.Assessment.IsActionable;
    }

    [RelayCommand]
    void SelectNone()
    {
        foreach (var r in rows) r.IsChecked = false;
    }

    [RelayCommand]
    void Install()
    {
        var picked = Picked();
        if (picked.Count == 0) return;
        main.StartInstall(picked);
    }

    [RelayCommand]
    void Ignore(DeviceRowViewModel row)
    {
        main.Settings.IgnoredDevices.Add(row.InstanceId);
        main.SaveSettings();
        main.SettingsPage.Add(row.InstanceId);
        Refresh();
    }

    /// <summary>Re-applies settings (ignore list, test mode) to the last scan.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(TestMode));
        if (Report is not null) Load(Report);
    }

    [RelayCommand]
    static void CopyHardwareId(DeviceRowViewModel row) => Clipboard.SetText(row.HardwareId);

    [RelayCommand]
    static void SearchOnline(DeviceRowViewModel row) =>
        Process.Start(new ProcessStartInfo(row.SearchUrl) { UseShellExecute = true });

    partial void OnIsBusyChanged(bool value) => OnRowSelectionChanged();
}
