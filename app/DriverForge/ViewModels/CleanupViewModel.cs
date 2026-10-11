using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriverForge.Core;

namespace DriverForge.ViewModels;

public sealed partial class GhostRowViewModel(Device device) : ObservableObject
{
    public Device Device { get; } = device;
    public string Name => Device.DisplayName;
    public string Class => string.IsNullOrEmpty(Device.Class) ? "Unknown class" : Device.Class;
    public string InstanceId => Device.InstanceId;
    public bool Suggested => Ghosts.Suggested(Device);
    public string LastSeen => (Device.LastRemoval ?? Device.LastArrival) is { } t ? $"Last seen {t:d}" : "Last seen: unknown";
    [ObservableProperty] private bool isChecked;
}

/// <summary>Ghost devices (SPEC §8.2): list, suggest, remove.</summary>
public sealed partial class CleanupViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<GhostRowViewModel> Rows { get; } = [];

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? status;
    [ObservableProperty] private string summary = "";

    bool loaded;

    public async Task LoadAsync(bool force = false)
    {
        if (IsBusy || (loaded && !force)) return;
        IsBusy = true;
        Status = "Looking for devices that aren't connected…";
        try
        {
            var ghosts = await main.Engine.GhostsAsync();
            Rows.Clear();
            foreach (var g in ghosts.OrderByDescending(Ghosts.Suggested).ThenBy(g => g.Class).ThenBy(g => g.DisplayName))
                Rows.Add(new(g));
            loaded = true;
            Status = null;
            UpdateSummary();
        }
        catch (EngineException ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }

    void UpdateSummary() =>
        Summary = $"{Rows.Count} devices aren't connected · {Rows.Count(r => r.Suggested)} of them are hardware that was unplugged.";

    [RelayCommand] Task Refresh() => LoadAsync(force: true);

    [RelayCommand]
    void SelectSuggested()
    {
        foreach (var r in Rows) r.IsChecked = r.Suggested;
    }

    [RelayCommand]
    void SelectNone()
    {
        foreach (var r in Rows) r.IsChecked = false;
    }

    [RelayCommand]
    Task RemoveSelected() => RemoveAsync(Rows.Where(r => r.IsChecked).ToList(), askFirst: true);

    public async Task RemoveAsync(IReadOnlyList<GhostRowViewModel> picked, bool askFirst)
    {
        if (IsBusy || picked.Count == 0) return;
        if (askFirst && !main.Confirm($"Remove {picked.Count} device {(picked.Count == 1 ? "entry" : "entries")}? A device comes back by itself when it's plugged in again."))
            return;
        if (main.Settings.TestMode)
        {
            Status = $"Test mode: {picked.Count} would be removed; nothing was changed.";
            return;
        }
        IsBusy = true;
        int removed = 0;
        var failed = new List<string>();
        foreach (var r in picked)
        {
            Status = $"Removing {r.Name}…";
            try { await main.Engine.RemoveGhostAsync(r.Device); removed++; Rows.Remove(r); }
            catch (EngineException ex) { failed.Add($"{r.Name}: {ex.Message}"); }
        }
        IsBusy = false;
        UpdateSummary();
        Status = $"Removed {removed}." + (failed.Count > 0 ? " Not removed: " + string.Join("; ", failed) : "");
    }
}
