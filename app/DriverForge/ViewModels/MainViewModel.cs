using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriverForge.Core;
using Wpf.Ui.Controls;

namespace DriverForge.ViewModels;

public sealed partial class NavItem(string label, SymbolRegular icon, object page) : ObservableObject
{
    public string Label { get; } = label;
    public SymbolRegular Icon { get; } = icon;
    public object Page { get; } = page;
    [ObservableProperty] private int badge;
    [ObservableProperty] private bool isCurrent;
}

public sealed partial class MainViewModel : ObservableObject
{
    readonly string settingsPath;

    public MainViewModel(IDriverEngine engine, string dataDirectory)
    {
        Engine = engine;
        DataDirectory = dataDirectory;
        settingsPath = Path.Combine(dataDirectory, "settings.json");
        Settings = AppSettings.Load(settingsPath);
        Drivers = new DriversViewModel(this);
        SettingsPage = new SettingsViewModel(this);
        History = new HistoryViewModel(this);
        Sources = new SourcesViewModel(this);
        Cleanup = new CleanupViewModel(this);
        SystemInfo = new SystemInfoViewModel(this);

        driversNav = new("Drivers", SymbolRegular.DeveloperBoard24, Drivers);
        Nav =
        [
            driversNav,
            new("Sources", SymbolRegular.CloudArrowDown24, Sources),
            new("History & rollback", SymbolRegular.History24, History),
            new("Cleanup", SymbolRegular.Broom24, Cleanup),
            new("System info", SymbolRegular.Desktop24, SystemInfo),
        ];
        settingsNav = new("Settings", SymbolRegular.Settings24, SettingsPage);
        Navigate(driversNav);
    }

    public IDriverEngine Engine { get; }
    public string DataDirectory { get; }
    public AppSettings Settings { get; }
    public DriversViewModel Drivers { get; }
    public SettingsViewModel SettingsPage { get; }
    public HistoryViewModel History { get; }
    public SourcesViewModel Sources { get; }
    public CleanupViewModel Cleanup { get; }
    public SystemInfoViewModel SystemInfo { get; }
    public ObservableCollection<NavItem> Nav { get; }

    readonly NavItem driversNav;
    readonly NavItem settingsNav;
    public NavItem SettingsNav => settingsNav;

    [ObservableProperty] private object? currentPage;
    [ObservableProperty] private bool isInstalling;

    public string EngineLabel => Engine.Name;

    public Task StartAsync() => Drivers.ScanAsync();

    /// <summary>Navigates by sidebar label (used by --screenshot --page).</summary>
    public void NavigateTo(string label)
    {
        var item = Nav.Append(settingsNav).FirstOrDefault(n => n.Label.StartsWith(label, StringComparison.OrdinalIgnoreCase));
        if (item is not null) Navigate(item);
    }

    public void SaveSettings() => Settings.Save(settingsPath);

    /// <summary>Yes/no question; automation (--screenshot) answers no.</summary>
    public Func<string, bool> Confirm { get; set; } = message =>
        System.Windows.MessageBox.Show(message, "Driver Forge", System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes;

    public void SetBadge(int n) => driversNav.Badge = n;

    [RelayCommand]
    void Navigate(NavItem item)
    {
        if (IsInstalling && item.Page != CurrentPage) return;   // stay on the install until it ends
        foreach (var n in Nav) n.IsCurrent = n == item;
        settingsNav.IsCurrent = settingsNav == item;
        CurrentPage = item.Page;
        if (item.Page is HistoryViewModel h) h.Refresh();
        if (item.Page is CleanupViewModel c) _ = c.LoadAsync();
        if (item.Page is SystemInfoViewModel si) _ = si.LoadAsync();
    }

    public void ShowDrivers() => Navigate(driversNav);

    public async void StartInstall(IReadOnlyList<DeviceRowViewModel> rows) => await InstallAsync(rows, Settings.TestMode);

    public async Task InstallAsync(IReadOnlyList<DeviceRowViewModel> rows, bool testMode)
    {
        var install = new InstallViewModel(this, rows, testMode);
        CurrentPage = install;
        IsInstalling = true;
        try { await install.RunAsync(); }
        finally { IsInstalling = false; }
    }
}
