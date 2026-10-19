using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriverForge.Core;
using DriverForge.Core.Apps;

namespace DriverForge.ViewModels;

public sealed partial class AppRowViewModel(AppRow row, Action changed) : ObservableObject
{
    public AppRow Row { get; private set; } = row;
    public string Name => Row.Name;
    public string Id => Row.Id;
    public string Publisher => Row.Publisher;
    public bool IsInstalled => Row.IsInstalled;
    public bool HasUpdate => Row.IsUpdateAvailable;
    public string Versions => Row.IsUpdateAvailable ? $"{Row.InstalledVersion} → {Row.AvailableVersion}"
                            : Row.InstalledVersion ?? Row.AvailableVersion ?? "";
    public string? Note => Row.UserScopeOnly ? "Installs for the signed-in account only" : Row.SelfUpdating ? "Updates itself" : null;
    public string Detail => Note is null ? Versions : $"{Versions}  ·  {Note}";
    public string IdDetail => Note is null ? Id : $"{Id}  ·  {Note}";
    [ObservableProperty] private bool isChecked;
    partial void OnIsCheckedChanged(bool value) => changed();
    public void Replace(AppRow r) { Row = r; OnPropertyChanged(string.Empty); }
}

public sealed class AppCategoryViewModel(string name, IEnumerable<AppRowViewModel> rows)
{
    public string Name { get; } = name;
    public ObservableCollection<AppRowViewModel> Rows { get; } = new(rows);
}

public sealed partial class AppResultViewModel(AppResult result) : ObservableObject
{
    public AppResult Result { get; } = result;
    public string Name => Result.Name;
    public string Outcome => Result.Outcome switch
    {
        AppOutcome.Installed => "Installed", AppOutcome.Updated => "Updated", AppOutcome.Uninstalled => "Uninstalled",
        AppOutcome.Skipped => "Already installed", AppOutcome.UpToDate => "Up to date", AppOutcome.Simulated => "Simulated",
        AppOutcome.NeedsConsent => "Needs consent", AppOutcome.Blocked => "Blocked", AppOutcome.DownloadFailed => "Download failed",
        AppOutcome.Cancelled => "Cancelled", _ => "Failed",
    };
    public bool Ok => Result.Succeeded;
    public string Message => Result.Message;
    public ObservableCollection<InstalledProgram> Extras { get; } = new(result.Extras);
}

/// <summary>The Apps page (APPS.md §9): Get apps and Updates.</summary>
public sealed partial class AppsViewModel(MainViewModel main) : ObservableObject
{
    IAppEngine Engine => main.Apps;

    public ObservableCollection<AppCategoryViewModel> Categories { get; } = [];
    public ObservableCollection<AppRowViewModel> SearchResults { get; } = [];
    public ObservableCollection<AppRowViewModel> Updates { get; } = [];
    public ObservableCollection<AppResultViewModel> Results { get; } = [];

    [ObservableProperty] private bool showUpdates;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private string? status;
    [ObservableProperty] private string engineStatus = "";
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string selectionText = "Nothing selected";
    [ObservableProperty] private string updatesText = "Updates";
    [ObservableProperty] private string? progressText;
    [ObservableProperty] private double progressValue;

    bool loaded;
    CancellationTokenSource? cancel;

    IEnumerable<AppRowViewModel> AllGetRows => Categories.SelectMany(c => c.Rows).Concat(SearchResults);

    Task? loading;

    /// <summary>Loads once; a caller arriving mid-load gets the load in flight.</summary>
    public Task LoadAsync(bool force = false)
    {
        if (loading is { IsCompleted: false }) return loading;
        if (loaded && !force) return Task.CompletedTask;
        return loading = LoadCoreAsync();
    }

    async Task LoadCoreAsync()
    {
        IsBusy = true;
        Status = "Opening the winget catalog…";
        try
        {
            var st = await Engine.GetStatusAsync();
            EngineStatus = st.Message;
            if (!st.Available) { Status = st.Message; return; }
            Status = "Loading apps…";
            var curated = await Engine.ListCuratedAsync();
            var catalog = CuratedCatalog.Load();
            Categories.Clear();
            foreach (string c in catalog.Categories)
            {
                var rows = curated.Where(r => r.Category == c).Select(r => new AppRowViewModel(r, UpdateSelection)).ToList();
                if (rows.Count > 0) Categories.Add(new(c, rows));
            }
            await LoadUpdatesAsync();
            loaded = true;
            Status = curated.Count == 0 ? "No curated apps are verified yet. Search the catalog above." : null;
            UpdateSelection();
        }
        catch (Exception ex) { Status = "The Apps page couldn't load: " + ex.Message; }
        finally { IsBusy = false; }
    }

    async Task LoadUpdatesAsync()
    {
        var updates = await Engine.ListUpdatesAsync();
        Updates.Clear();
        foreach (var r in updates.Where(r => !main.Settings.IsAppIgnored(r.Id, r.AvailableVersion)))
            Updates.Add(new AppRowViewModel(r, UpdateSelection));
        int hidden = updates.Count - Updates.Count;
        UpdatesText = $"{Updates.Count} {(Updates.Count == 1 ? "update" : "updates")} available" + (hidden > 0 ? $" · {hidden} ignored" : "");
        main.SetAppsBadge(Updates.Count);
    }

    void UpdateSelection()
    {
        int n = ShowUpdates ? Updates.Count(r => r.IsChecked) : AllGetRows.Count(r => r.IsChecked);
        SelectionText = n == 0 ? "Nothing selected" : $"{n} selected";
    }

    partial void OnShowUpdatesChanged(bool value) => UpdateSelection();

    [RelayCommand] void ShowGet() => ShowUpdates = false;
    [RelayCommand] void ShowUpdateList() => ShowUpdates = true;
    [RelayCommand] Task Refresh() => LoadAsync(force: true);

    [RelayCommand]
    async Task Search()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var found = await Engine.SearchAsync(SearchText);
            var keep = SearchResults.Where(r => r.IsChecked).ToList();
            SearchResults.Clear();
            foreach (var r in keep) SearchResults.Add(r);
            foreach (var r in found.Where(f => !keep.Any(k => k.Id == f.Id)))
                SearchResults.Add(new AppRowViewModel(r, UpdateSelection));
            Status = found.Count == 0 && SearchText.Trim().Length >= 2 ? $"Nothing in the catalog matches \"{SearchText.Trim()}\"." : null;
        }
        catch (Exception ex) { Status = "Search failed: " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand] void SelectAllUpdates() { foreach (var r in Updates) r.IsChecked = !r.Row.SelfUpdating; }

    [RelayCommand] Task Install() => RunAsync(AllGetRows.Where(r => r.IsChecked).ToList(), AppAction.Install);

    [RelayCommand] Task UpdateSelected() => RunAsync(Updates.Where(r => r.IsChecked).ToList(), AppAction.Upgrade);

    [RelayCommand] Task UpdateAll() => RunAsync(Updates.Where(r => !r.Row.SelfUpdating).ToList(), AppAction.Upgrade);

    [RelayCommand] void Cancel() { cancel?.Cancel(); ProgressText = "Stopping after the current app…"; }

    [RelayCommand]
    async Task Ignore(AppRowViewModel row)
    {
        main.Settings.IgnoredApps[row.Id] = row.Row.AvailableVersion ?? "";
        main.SaveSettings();
        await LoadUpdatesAsync();
    }

    public async Task RunAsync(IReadOnlyList<AppRowViewModel> picked, AppAction action)
    {
        if (IsRunning || picked.Count == 0) return;
        string? summary = null;
        IsRunning = true;
        main.IsInstalling = true;
        Results.Clear();
        cancel = new CancellationTokenSource();
        try
        {
            ProgressText = "Checking licence terms…";
            var rows = await Engine.DescribeAsync(picked.Select(p => p.Row).ToList());
            var terms = rows.Where(r => r.Agreements.Count > 0).ToList();
            bool accept = terms.Count == 0 || main.Confirm(
                "These apps have licence terms. Installing them silently means you accept them:\n\n"
                + string.Join("\n", terms.Select(r => $"• {r.Name}: " + string.Join(", ", r.Agreements.Select(a => a.Url.Length > 0 ? a.Url : a.Label))))
                + "\n\nAccept these terms and install?");
            var otherAccount = rows.Where(r => r.UserScopeOnly).ToList();
            if (otherAccount.Count > 0 && SessionUser.ElevatedAsSomeoneElse()
                && !main.Confirm("Driver Forge is running as a different account from the one signed in. These apps only install per account, so they'd go to that other account:\n\n"
                                 + string.Join("\n", otherAccount.Select(r => "• " + r.Name)) + "\n\nInstall them anyway?"))
                rows = rows.Except(otherAccount).ToList();

            if (main.Settings.AppsRestorePoint && !main.Settings.TestMode)
            {
                ProgressText = "Creating a restore point…";
                await Task.Run(() => RestorePoints.Create("Driver Forge: before installing apps"));
            }

            var options = new AppInstallOptions
            {
                AcceptAgreements = accept,
                MachineScopeOnly = !main.Settings.AppsAnyScope,
                TestMode = main.Settings.TestMode,
                CorrelationId = Guid.NewGuid().ToString("N"),
            };
            int done = 0;
            var progress = new Progress<AppStep>(s =>
            {
                var name = rows.FirstOrDefault(r => r.Id == s.Id)?.Name ?? s.Id;
                ProgressText = $"{name}: {s.State}… ({done + 1} of {rows.Count})";
                ProgressValue = (done + s.Percent / 100.0) / Math.Max(1, rows.Count) * 100;
            });
            var queue = new AppQueue(Engine);
            foreach (var row in rows)
            {
                var r = (await queue.RunAsync([row], action, options, progress, cancel.Token))[0];
                Results.Add(new AppResultViewModel(r));
                done++;
            }
            summary = (main.Settings.TestMode ? "Test mode. " : "") + AppQueue.Summary(Results.Select(r => r.Result).ToList());
            foreach (var p in picked) p.IsChecked = false;
        }
        catch (Exception ex) { summary = "Stopped: " + ex.Message; }
        finally
        {
            ProgressText = null;
            ProgressValue = 0;
            IsRunning = false;
            main.IsInstalling = false;
            loaded = false;
            await LoadAsync();                  // installed marks and updates change
            Status = summary;                   // the batch's outcome beats the reload's status
        }
    }

    [RelayCommand]
    void UninstallExtra(InstalledProgram extra)
    {
        string? command = extra.QuietUninstallCommand ?? extra.UninstallCommand;
        if (command is null) { Status = $"{extra.Name} has no uninstall command; remove it from Windows Settings."; return; }
        if (!main.Confirm($"Uninstall {extra.Name}?")) return;
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + command + "\"") { UseShellExecute = false, CreateNoWindow = true });
            Status = $"Started the uninstaller for {extra.Name}.";
        }
        catch (Exception ex) { Status = $"Couldn't start the uninstaller for {extra.Name}: {ex.Message}"; }
    }

    [RelayCommand]
    async Task ExportList()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "apps.json", Filter = "winget package list (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        var picked = AllGetRows.Where(r => r.IsChecked).Select(r => r.Id).ToList();
        if (picked.Count == 0) picked = (await Engine.ListInstalledAsync()).Where(r => r.AvailableVersion is not null).Select(r => r.Id).ToList();
        var st = await Engine.GetStatusAsync();
        await File.WriteAllTextAsync(dlg.FileName, AppList.Write(picked, st.EngineVersion));
        Status = $"Saved {picked.Count} apps to {dlg.FileName}. It also works with `winget import`.";
    }

    /// <summary>Tick these apps, adding catalog apps that aren't on the page yet.</summary>
    public async Task<IReadOnlyList<AppRowViewModel>> TickAsync(IEnumerable<string> ids)
    {
        var ticked = new List<AppRowViewModel>();
        foreach (string id in ids.Select(i => i.Trim()).Where(i => i.Length > 0))
        {
            var row = AllGetRows.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (row is null && await Engine.FindAsync(id) is { } found)
                SearchResults.Add(row = new AppRowViewModel(found, UpdateSelection));
            if (row is null) continue;
            row.IsChecked = true;
            ticked.Add(row);
        }
        return ticked;
    }

    [RelayCommand]
    async Task ImportList()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "winget package list (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var ids = AppList.Read(await File.ReadAllTextAsync(dlg.FileName));
            var ticked = await TickAsync(ids);
            var missing = ids.Where(id => !ticked.Any(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToList();
            ShowUpdates = false;
            Status = $"Ticked {ticked.Count} apps from the list." + (missing.Count > 0 ? " Not in the catalog: " + string.Join(", ", missing) : "");
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or IOException)
        {
            Status = "That file isn't a winget package list: " + ex.Message;
        }
    }
}
