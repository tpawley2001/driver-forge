using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriverForge.Core;

namespace DriverForge.ViewModels;

public enum ItemStatus { Waiting, Downloading, Extracting, Installing, Installed, NeedsRestart, Failed, Simulated }

public sealed partial class InstallItemViewModel(DeviceRowViewModel row) : ObservableObject
{
    public DeviceRowViewModel Row { get; } = row;
    public DriverOffer Offer { get; } = row.Chosen!.Offer;
    public string Name => Row.Name;
    public string Target => $"{Offer.Version} · {Offer.Source}";

    [ObservableProperty] private ItemStatus status = ItemStatus.Waiting;
    [ObservableProperty] private string detail = "Waiting";

    public bool IsDone => Status is ItemStatus.Installed or ItemStatus.NeedsRestart or ItemStatus.Failed or ItemStatus.Simulated;
    public bool IsActive => Status is ItemStatus.Downloading or ItemStatus.Extracting or ItemStatus.Installing;
    public bool IsOk => Status is ItemStatus.Installed or ItemStatus.NeedsRestart or ItemStatus.Simulated;
    public bool IsFailed => Status == ItemStatus.Failed;
    public bool IsWaiting => Status == ItemStatus.Waiting;

    partial void OnStatusChanged(ItemStatus value)
    {
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsOk));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsWaiting));
    }
}

public sealed partial class InstallViewModel : ObservableObject
{
    readonly MainViewModel main;
    readonly CancellationTokenSource cts = new();

    public InstallViewModel(MainViewModel main, IReadOnlyList<DeviceRowViewModel> rows, bool testMode)
    {
        this.main = main;
        TestMode = testMode;
        Items = new(rows.Select(r => new InstallItemViewModel(r)));
        Title = $"{(testMode ? "Test-installing" : "Installing")} {Items.Count} {(Items.Count == 1 ? "driver" : "drivers")}";
    }

    public ObservableCollection<InstallItemViewModel> Items { get; }
    public ObservableCollection<string> Log { get; } = [];
    public bool TestMode { get; }

    [ObservableProperty] private string title;
    [ObservableProperty] private string stepText = "Starting…";
    [ObservableProperty] private double percent;
    [ObservableProperty] private bool isRunning = true;
    [ObservableProperty] private bool needsRestart;
    [ObservableProperty] private string? restorePointText;
    [ObservableProperty] private bool restorePointOk;
    [ObservableProperty] private string? resultText;
    [ObservableProperty] private bool restartScheduled;

    public async Task RunAsync()
    {
        try
        {
            if (main.Settings.CreateRestorePoint && !TestMode && !await CreateRestorePointAsync())
                return;
            foreach (var item in Items)
            {
                cts.Token.ThrowIfCancellationRequested();
                await InstallOneAsync(item);
                Percent = 100.0 * Items.Count(i => i.IsDone) / Items.Count;
            }
            Finish();
        }
        catch (OperationCanceledException)
        {
            foreach (var i in Items.Where(i => !i.IsDone)) { i.Status = ItemStatus.Failed; i.Detail = "Cancelled"; }
            ResultText = "Cancelled. Drivers that finished before the cancel stay installed.";
        }
        finally
        {
            IsRunning = false;
            Percent = 100;
        }
    }

    async Task InstallOneAsync(InstallItemViewModel item)
    {
        StepText = $"Installing {item.Name}";
        var progress = new Progress<InstallStep>(step =>
        {
            (item.Status, item.Detail) = step switch
            {
                InstallStep.Downloading => (ItemStatus.Downloading, "Downloading"),
                InstallStep.Extracting => (ItemStatus.Extracting, "Extracting"),
                InstallStep.BackingUp => (ItemStatus.Installing, "Backing up the current driver"),
                InstallStep.Verifying => (ItemStatus.Installing, "Checking the result"),
                _ => (ItemStatus.Installing, TestMode ? "Simulating install" : "Installing"),
            };
        });
        try
        {
            var r = await main.Engine.InstallAsync(item.Row.Device, item.Offer, new InstallOptions(TestMode), progress, cts.Token);
            if (r.Simulated) { item.Status = ItemStatus.Simulated; item.Detail = "Simulated · nothing was changed"; }
            else if (r.Ok && r.NeedsRestart) { item.Status = ItemStatus.NeedsRestart; item.Detail = "Installed · restart to finish"; }
            else if (r.Ok) { item.Status = ItemStatus.Installed; item.Detail = "Installed"; }
            else { item.Status = ItemStatus.Failed; item.Detail = $"Failed (error 0x{r.ErrorCode:X8})"; }
            if (!r.Simulated) NeedsRestart |= r.NeedsRestart;
            AddLog($"{item.Name}: {r.Message}");
        }
        catch (Exception ex) when (ex is EngineException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            item.Status = ItemStatus.Failed;
            item.Detail = "Failed";
            AddLog($"{item.Name}: {ex.Message}");
        }
    }

    /// <summary>False when there's no restore point and the user won't go on without one.</summary>
    async Task<bool> CreateRestorePointAsync()
    {
        StepText = "Creating a restore point (this takes about a minute)…";
        var r = await Task.Run(() => RestorePoints.Create("Before Driver Forge driver install"));
        RestorePointOk = r.Created;
        RestorePointText = r.Message;
        AddLog(r.Message);
        if (r.Created) return true;

        bool go = main.Confirm(r.Message + "\n\nInstall the drivers anyway, without a restore point?");
        if (!go)
        {
            foreach (var i in Items) { i.Status = ItemStatus.Failed; i.Detail = "Not installed"; }
            ResultText = "Nothing was installed, because no restore point could be created.";
        }
        return go;
    }

    void Finish()
    {
        int ok = Items.Count(i => i.IsOk), failed = Items.Count(i => i.IsFailed);
        bool simulated = Items.All(i => i.Status == ItemStatus.Simulated);
        ResultText = simulated
            ? $"Test run finished: {ok} would install. Nothing on this PC was changed."
            : failed == 0 ? $"Done: {ok} {(ok == 1 ? "driver" : "drivers")} installed."
            : $"{ok} installed, {failed} failed.";
        StepText = NeedsRestart ? "A restart is needed to finish." : "Finished.";

        if (NeedsRestart && !TestMode && main.Settings.RestartWhenNeeded)
            ScheduleRestart();
    }

    void AddLog(string line)
    {
        Log.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
        while (Log.Count > 300) Log.RemoveAt(0);
    }

    [RelayCommand]
    void Cancel() => cts.Cancel();

    [RelayCommand]
    void ScheduleRestart()
    {
        Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 60 /c \"Driver Forge: restarting to finish driver installs\"")
        { UseShellExecute = false, CreateNoWindow = true });
        RestartScheduled = true;
    }

    [RelayCommand]
    void CancelRestart()
    {
        Process.Start(new ProcessStartInfo("shutdown.exe", "/a") { UseShellExecute = false, CreateNoWindow = true });
        RestartScheduled = false;
    }

    [RelayCommand]
    async Task BackToDriversAsync()
    {
        main.ShowDrivers();
        await main.Drivers.ScanAsync();
    }
}
