namespace DriverForge.Core.Apps;

/// <summary>Fixed rows and simulated installs, for --demo and screenshots (APPS.md §2).</summary>
public sealed class DemoAppEngine : IAppEngine
{
    readonly List<AppRow> _rows;

    public DemoAppEngine()
    {
        var curated = CuratedCatalog.Load();
        var installed = new Dictionary<string, (string Installed, string? Update)>
        {
            ["7zip.7zip"] = ("25.01", "26.04"),
            ["VideoLAN.VLC"] = ("3.0.24", null),
            ["Notepad++.Notepad++"] = ("8.9.2", "8.9.8.1"),
            ["Git.Git"] = ("2.54.0", "2.55.0.5"),
        };
        _rows = curated.Apps.Select((a, i) =>
        {
            installed.TryGetValue(a.Id, out var v);
            string name = a.Id[(a.Id.IndexOf('.') + 1)..].Replace('.', ' ');
            return new AppRow
            {
                Id = a.Id, Name = name, Publisher = a.Id.Split('.')[0],
                InstalledVersion = v.Installed, AvailableVersion = v.Update ?? v.Installed ?? $"{1 + i % 9}.{i}.0",
                IsUpdateAvailable = v.Update is not null, Category = a.Category, Curated = true,
                SelfUpdating = a.SelfUpdating, MachineOnly = a.MachineOnly, Verified = a.Verified ?? "2026-10-20",
                UserScopeOnly = a.Id == "Discord.Discord",
                Agreements = a.Id == "Zoom.Zoom" ? [new AppAgreement("License", "", "https://example.invalid/terms")] : [],
            };
        }).ToList();
    }

    public Task<AppStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(new AppStatus(true, "demo", "Demo catalog"));
    public Task<IReadOnlyList<AppRow>> ListCuratedAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AppRow>>(_rows);
    public Task<IReadOnlyList<AppRow>> SearchAsync(string text, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AppRow>>(text.Length < 2 ? [] :
        [
            new AppRow { Id = "Demo." + text, Name = text + " (demo result)", Publisher = "Demo", AvailableVersion = "1.0" },
        ]);
    public Task<IReadOnlyList<AppRow>> ListUpdatesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AppRow>>(_rows.Where(r => r.IsUpdateAvailable).ToList());
    public Task<IReadOnlyList<AppRow>> ListInstalledAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AppRow>>(_rows.Where(r => r.IsInstalled).ToList());
    public Task<IReadOnlyList<AppRow>> DescribeAsync(IReadOnlyList<AppRow> rows) => Task.FromResult(rows);
    public Task<AppRow?> FindAsync(string id) => Task.FromResult(_rows.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));

    public Task<AppResult> InstallAsync(AppRow app, AppInstallOptions o, IProgress<AppStep>? p = null, CancellationToken ct = default) => Simulate(app, AppAction.Install, p);
    public Task<AppResult> UpgradeAsync(AppRow app, AppInstallOptions o, IProgress<AppStep>? p = null, CancellationToken ct = default) => Simulate(app, AppAction.Upgrade, p);
    public Task<AppResult> UninstallAsync(AppRow app, IProgress<AppStep>? p = null, CancellationToken ct = default) => Simulate(app, AppAction.Uninstall, p);

    static async Task<AppResult> Simulate(AppRow app, AppAction action, IProgress<AppStep>? progress)
    {
        foreach (var (state, pct) in new[] { ("Downloading", 40.0), ("Installing", 80.0), ("Finished", 100.0) })
        {
            progress?.Report(new AppStep(app.Id, state, pct));
            await Task.Delay(150);
        }
        return new AppResult
        {
            Id = app.Id, Name = app.Name, Action = action, Outcome = AppOutcome.Simulated,
            FromVersion = app.InstalledVersion, ToVersion = app.AvailableVersion, Message = "Simulated (demo).",
        };
    }
}
