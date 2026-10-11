using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriverForge.Core;

namespace DriverForge.ViewModels;

public sealed class HistoryRowViewModel(HistoryEntry entry)
{
    public HistoryEntry Entry { get; } = entry;
    public string Device => Entry.DeviceName;
    public string When => Entry.At.ToString("g");
    public string From => Entry.From?.ToString() ?? "No driver";
    public string To => Entry.To.ToString();
    public string Result => Entry.Simulated ? "Simulated · nothing was changed"
        : Entry.Ok ? (Entry.NeedsRestart ? "Installed · restart to finish" : "Installed")
        : "Failed: " + Entry.Message;
    public string Source => Entry.Source + (Entry.BackupFolder is not null ? " · old driver backed up" : "");
    public bool CanRollBack => Entry.CanRollBack;
    public bool Failed => !Entry.Ok;
}

/// <summary>The install log with one-click rollback (SPEC §7).</summary>
public sealed partial class HistoryViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<HistoryRowViewModel> Rows { get; } = [];

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? status;

    public bool IsEmpty => Rows.Count == 0;

    public void Refresh()
    {
        Rows.Clear();
        foreach (var e in main.Engine.History()) Rows.Add(new(e));
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    Task RollBack(HistoryRowViewModel row) => RollBackAsync(row.Entry, askFirst: true);

    public async Task RollBackAsync(HistoryEntry entry, bool askFirst)
    {
        if (IsBusy || !entry.CanRollBack) return;
        bool test = main.Settings.TestMode;
        if (askFirst && !main.Confirm($"Put back {entry.From} on {entry.DeviceName}?"))
            return;
        IsBusy = true;
        try
        {
            if (main.Settings.CreateRestorePoint && !test)
            {
                Status = "Creating a restore point (this takes about a minute)…";
                var rp = await Task.Run(() => RestorePoints.Create("Before Driver Forge rollback"));
                if (!rp.Created && !main.Confirm(rp.Message + "\n\nRoll back anyway, without a restore point?"))
                {
                    Status = "Nothing was changed, because no restore point could be created.";
                    return;
                }
            }
            Status = $"Rolling back {entry.DeviceName}…";
            var r = await main.Engine.RollbackAsync(entry, new InstallOptions(test), null);
            Status = r.Simulated ? $"{entry.DeviceName}: {r.Message}"
                : r.Ok ? $"{entry.DeviceName} is back on {entry.From}." + (r.NeedsRestart ? " Restart to finish." : "")
                : $"{entry.DeviceName}: rollback failed. {r.Message}";
        }
        catch (EngineException ex) { Status = ex.Message; }
        finally
        {
            IsBusy = false;
            Refresh();
            _ = main.Drivers.ScanAsync();
        }
    }
}
