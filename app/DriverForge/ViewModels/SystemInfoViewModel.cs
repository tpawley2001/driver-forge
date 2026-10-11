using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriverForge.Core;

namespace DriverForge.ViewModels;

public sealed class InstallDateRowViewModel(Device device, InstalledDriver driver)
{
    public DateTime At { get; } = driver.InstalledAt!.Value;
    public string When => At.ToString("g");
    public string Device => device.DisplayName;
    public string Driver => $"{driver.Provider} {driver.Version}".Trim() + (driver.Date is { } d ? $" ({d:d})" : "");
    public string Inf => driver.Inf;
    public bool CameWithWindows => !DriverBackup.IsThirdParty(driver.Inf);
}

/// <summary>What this PC is, and when each driver was installed (SPEC §9.1).</summary>
public sealed partial class SystemInfoViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<InfoSection> Sections { get; } = [];
    public ObservableCollection<InstallDateRowViewModel> Drivers { get; } = [];

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? status;
    [ObservableProperty] private string? copied;
    [ObservableProperty] private string driversNote = "";

    /// <summary>Most drivers that came with Windows were installed with Windows; they bury the interesting ones.</summary>
    [ObservableProperty] private bool includeWindowsDrivers;

    bool loaded;

    partial void OnIncludeWindowsDriversChanged(bool value) => RefreshDrivers();

    Task? loading;

    /// <summary>A call while a load is running waits for that load.</summary>
    public Task LoadAsync(bool force = false)
    {
        RefreshDrivers();
        if (loading is { IsCompleted: false }) return loading;
        if (loaded && !force) return Task.CompletedTask;
        return loading = ReadAsync();
    }

    async Task ReadAsync()
    {
        IsBusy = true;
        Status = "Reading this PC's hardware…";
        try
        {
            var sections = await main.Engine.SystemInfoAsync();
            Sections.Clear();
            foreach (var s in sections) Sections.Add(s);
            loaded = true;
            Status = null;
        }
        catch (Exception ex) when (ex is EngineException or System.Management.ManagementException)
        {
            Status = ex.Message;
        }
        finally { IsBusy = false; }
    }

    /// <summary>From the last scan, so it follows installs without reading every device again.</summary>
    public void RefreshDrivers()
    {
        Drivers.Clear();
        if (main.Drivers.Report is not { } report)
        {
            DriversNote = "The device scan hasn't finished yet.";
            return;
        }
        var all = report.Devices
            .Where(d => d.Installed?.InstalledAt is not null)
            .Select(d => new InstallDateRowViewModel(d, d.Installed!))
            .OrderByDescending(r => r.At)
            .ToList();
        foreach (var r in all.Where(r => IncludeWindowsDrivers || !r.CameWithWindows)) Drivers.Add(r);
        int hidden = all.Count - Drivers.Count;
        DriversNote = hidden > 0
            ? $"{Drivers.Count} drivers, newest first. {hidden} that came with Windows are hidden."
            : $"{Drivers.Count} drivers, newest first.";
    }

    [RelayCommand] Task Refresh() => LoadAsync(force: true);

    [RelayCommand]
    void Copy()
    {
        var text = SystemInfo.ToText(Sections);
        if (Drivers.Count > 0)
            text += Environment.NewLine + "Drivers by install date" + Environment.NewLine
                + string.Concat(Drivers.Select(d => $"  {d.When}  {d.Device} · {d.Driver} · {d.Inf}{Environment.NewLine}"));
        try
        {
            Clipboard.SetText(text);
            Copied = "Copied. Serial numbers aren't included, so it's safe to paste in public.";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Copied = "Another program is using the clipboard. Try again.";
        }
    }
}
