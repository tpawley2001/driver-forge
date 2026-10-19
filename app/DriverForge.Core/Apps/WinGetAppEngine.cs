using Microsoft.Management.Deployment;
using WinGetInstallOptions = Microsoft.Management.Deployment.InstallOptions;

namespace DriverForge.Core.Apps;

/// <summary>
/// The Apps engine on the in-process winget engine (APPS.md §3–§7). One
/// package at a time; a running installer is never cancelled half-way, so
/// <c>ct</c> is only checked before an install starts.
/// </summary>
public sealed class WinGetAppEngine(string dataDirectory) : IAppEngine, IDisposable
{
    readonly WinGetSession _session = new();
    readonly CuratedCatalog _curated = CuratedCatalog.Load();
    readonly IReadOnlyList<System.Text.RegularExpressions.Regex> _runtimes = ProgramsSnapshot.Runtimes();
    InstallHistory History => new(Path.Combine(dataDirectory, "history.jsonl"));

    /// <summary>How long to wait before the one retry when another installer is running (§3.5).</summary>
    public TimeSpan BusyRetryDelay { get; init; } = TimeSpan.FromSeconds(60);

    public async Task<AppStatus> GetStatusAsync(CancellationToken ct = default)
    {
        // Reg-free WinRT activation needs Windows 10 1903 (build 18362).
        if (Environment.OSVersion.Version.Build < 18362)
            return new AppStatus(false, "", "The Apps page needs Windows 10 version 1903 or later.");
        try
        {
            string version = await _session.RunAsync(pm => pm.Version);
            await _session.RunAsync(pm => WinGetSession.Connect(pm, CompositeSearchBehavior.RemotePackagesFromAllCatalogs));
            return new AppStatus(true, version, $"winget {version}");
        }
        catch (EngineException ex) { return new AppStatus(false, "", ex.Message + " Showing installed apps only."); }
        catch (Exception ex) { return new AppStatus(false, "", $"The winget engine didn't start ({ex.Message})."); }
    }

    public Task<IReadOnlyList<AppRow>> ListCuratedAsync(CancellationToken ct = default) =>
        _session.RunAsync<IReadOnlyList<AppRow>>(pm =>
        {
            var offered = _curated.Offered.ToList();
            if (offered.Count == 0) return [];
            var catalog = WinGetSession.Connect(pm, CompositeSearchBehavior.RemotePackagesFromAllCatalogs);
            var find = new FindPackagesOptions();
            foreach (var app in offered)
                find.Selectors.Add(new PackageMatchFilter { Field = PackageMatchField.Id, Option = PackageFieldMatchOption.EqualsCaseInsensitive, Value = app.Id });
            var byId = catalog.FindPackages(find).Matches
                .Select(m => m.CatalogPackage)
                .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            // Catalog order within a category is the curated file's order.
            return offered.Where(a => byId.ContainsKey(a.Id)).Select(a => ToRow(byId[a.Id])).ToList();
        });

    /// <summary>Curated IDs the catalog doesn't know (renamed or removed packages).</summary>
    public Task<IReadOnlyList<string>> MissingCuratedAsync() =>
        _session.RunAsync<IReadOnlyList<string>>(pm =>
        {
            var catalog = WinGetSession.Connect(pm, CompositeSearchBehavior.RemotePackagesFromRemoteCatalogs);
            return _curated.Apps.Where(a => WinGetSession.FindById(catalog, a.Id) is null).Select(a => a.Id).ToList();
        });

    public Task<IReadOnlyList<AppRow>> SearchAsync(string text, CancellationToken ct = default) =>
        _session.RunAsync<IReadOnlyList<AppRow>>(pm =>
        {
            text = text.Trim();
            if (text.Length < 2) return [];
            var catalog = WinGetSession.Connect(pm, CompositeSearchBehavior.RemotePackagesFromAllCatalogs);
            // CatalogDefault is what `winget search <text>` matches on.
            var find = new FindPackagesOptions { ResultLimit = 50 };
            find.Selectors.Add(new PackageMatchFilter { Field = PackageMatchField.CatalogDefault, Option = PackageFieldMatchOption.ContainsCaseInsensitive, Value = text });
            return catalog.FindPackages(find).Matches.Select(m => ToRow(m.CatalogPackage)).ToList();
        });

    /// <summary>The row for exactly this winget ID, or null.</summary>
    public Task<AppRow?> FindAsync(string id) =>
        _session.RunAsync(pm =>
        {
            var pkg = WinGetSession.FindById(WinGetSession.Connect(pm, CompositeSearchBehavior.RemotePackagesFromAllCatalogs), id);
            return pkg is null ? null : ToRow(pkg);
        });

    public async Task<IReadOnlyList<AppRow>> ListUpdatesAsync(CancellationToken ct = default) =>
        (await ListInstalledAsync(ct)).Where(r => r.IsUpdateAvailable).ToList();

    /// <summary>Every installed program winget can see; AvailableVersion is
    /// null for ones it can't match to a catalog package ("Not managed").</summary>
    public Task<IReadOnlyList<AppRow>> ListInstalledAsync(CancellationToken ct = default) =>
        _session.RunAsync<IReadOnlyList<AppRow>>(pm =>
        {
            var catalog = WinGetSession.Connect(pm, CompositeSearchBehavior.LocalCatalogs);
            return catalog.FindPackages(new FindPackagesOptions()).Matches
                .Select(m => ToRow(m.CatalogPackage))
                .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        });

    /// <summary>
    /// Fill in what needs each package's manifest: licence terms (§5.2) and
    /// whether only a per-user installer exists (§5.3). Done for the apps the
    /// user picked, not for whole lists, because each one is a download.
    /// </summary>
    public Task<IReadOnlyList<AppRow>> DescribeAsync(IReadOnlyList<AppRow> rows) =>
        _session.RunAsync<IReadOnlyList<AppRow>>(pm =>
        {
            var catalog = WinGetSession.Connect(pm, CompositeSearchBehavior.RemotePackagesFromAllCatalogs);
            return rows.Select(row =>
            {
                var version = WinGetSession.FindById(catalog, row.Id)?.DefaultInstallVersion;
                if (version is null) return row;
                var agreements = version.GetCatalogPackageMetadata().Agreements
                    .Select(a => new AppAgreement(a.Label ?? "", a.Text ?? "", a.Url ?? "")).ToList();
                bool machine = version.GetApplicableInstaller(new WinGetInstallOptions { PackageInstallScope = PackageInstallScope.System }) is not null;
                bool any = version.GetApplicableInstaller(new WinGetInstallOptions { PackageInstallScope = PackageInstallScope.Any }) is not null;
                return row with { Agreements = agreements, UserScopeOnly = any && !machine };
            }).ToList();
        });

    AppRow ToRow(CatalogPackage p)
    {
        var cur = _curated.Find(p.Id);
        var available = p.DefaultInstallVersion;
        var installed = p.InstalledVersion;
        string? installedVersion = installed?.Version;
        return new AppRow
        {
            Id = p.Id,
            Name = p.Name,
            Publisher = available?.Publisher ?? installed?.Publisher ?? "",
            InstalledVersion = installedVersion,
            AvailableVersion = available?.Version,
            // An installed version winget can't read ("Unknown") isn't upgraded (§3.4).
            IsUpdateAvailable = p.IsUpdateAvailable && installedVersion is not null
                                && !installedVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase),
            Source = available?.PackageCatalog?.Info?.Name ?? installed?.PackageCatalog?.Info?.Name ?? "winget",
            Category = cur?.Category,
            Curated = cur is not null,
            SelfUpdating = cur?.SelfUpdating ?? false,
            MachineOnly = cur?.MachineOnly ?? false,
            Verified = cur?.Verified,
        };
    }

    public Task<AppResult> InstallAsync(AppRow app, AppInstallOptions options, IProgress<AppStep>? progress = null, CancellationToken ct = default) =>
        ChangeAsync(app, AppAction.Install, options, progress, ct);

    public Task<AppResult> UpgradeAsync(AppRow app, AppInstallOptions options, IProgress<AppStep>? progress = null, CancellationToken ct = default) =>
        ChangeAsync(app, AppAction.Upgrade, options, progress, ct);

    async Task<AppResult> ChangeAsync(AppRow app, AppAction action, AppInstallOptions options, IProgress<AppStep>? progress, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Result(app, action, AppOutcome.Cancelled, "Cancelled before it started.");
        var result = await RunChangeAsync(app, action, options, progress);
        // Another installer was running (Windows Installer allows one): wait and try once more.
        if (result.Outcome == AppOutcome.Failed && result.Message.Contains("another installation", StringComparison.OrdinalIgnoreCase)
            && !ct.IsCancellationRequested)
        {
            progress?.Report(new AppStep(app.Id, "Waiting for another installer", 0));
            await Task.Delay(BusyRetryDelay, CancellationToken.None);
            result = await RunChangeAsync(app, action, options, progress);
        }
        Record(result, app);
        return result;
    }

    Task<AppResult> RunChangeAsync(AppRow app, AppAction action, AppInstallOptions options, IProgress<AppStep>? progress) =>
        _session.RunAsync(pm =>
        {
            var catalog = WinGetSession.Connect(pm, CompositeSearchBehavior.RemotePackagesFromAllCatalogs);
            var pkg = WinGetSession.FindById(catalog, app.Id);
            if (pkg is null) return Result(app, action, AppOutcome.Failed, "Not in the winget catalog any more.");
            string? from = pkg.InstalledVersion?.Version;
            if (action == AppAction.Install && from is not null)
                return Result(app, action, AppOutcome.Skipped, "Already installed.") with { FromVersion = from, ToVersion = from };
            if (action == AppAction.Upgrade && !pkg.IsUpdateAvailable)
                return Result(app, action, AppOutcome.UpToDate, "Up to date.") with { FromVersion = from, ToVersion = from };

            string? to = pkg.DefaultInstallVersion?.Version;
            // The machine-wide preference is for new installs. An upgrade keeps
            // the scope the app is installed in (a per-user app stays per-user),
            // which is what Any does for an installed package.
            var scope = options.MachineScopeOnly && action == AppAction.Install ? PackageInstallScope.SystemOrUnknown : PackageInstallScope.Any;

            if (options.TestMode)
            {
                // §5.5: download and hash-check, don't install.
                string dir = Path.Combine(Path.GetTempPath(), "DriverForge-apps-" + Guid.NewGuid().ToString("N")[..8]);
                try
                {
                    var download = pm.DownloadPackageAsync(pkg, new DownloadOptions
                    {
                        DownloadDirectory = dir, Scope = scope,
                        AcceptPackageAgreements = options.AcceptAgreements,
                        CorrelationData = options.CorrelationId ?? "",
                    });
                    download.Progress = (_, p) => progress?.Report(new AppStep(app.Id, "Downloading", p.DownloadProgress * 100));
                    var d = download.GetAwaiter().GetResult();
                    var (outcome, message) = d.Status == DownloadResultStatus.Ok
                        ? (AppOutcome.Simulated, "Simulated: downloaded and hash-checked, not installed.")
                        : MapStatus(d.Status.ToString(), d.ExtendedErrorCode?.HResult ?? 0, 0);
                    return Result(app, action, outcome, message) with { FromVersion = from, ToVersion = to };
                }
                finally { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            }

            var before = ProgramsSnapshot.Take();
            var installOptions = new WinGetInstallOptions
            {
                PackageInstallMode = PackageInstallMode.Silent,
                PackageInstallScope = scope,
                AcceptPackageAgreements = options.AcceptAgreements,
                AllowUpgradeToUnknownVersion = false,
                CorrelationData = options.CorrelationId ?? "",
            };
            var op = action == AppAction.Upgrade ? pm.UpgradePackageAsync(pkg, installOptions) : pm.InstallPackageAsync(pkg, installOptions);
            op.Progress = (_, p) => progress?.Report(new AppStep(app.Id, Describe(p.State),
                p.State == PackageInstallProgressState.Downloading ? p.DownloadProgress * 100 : p.InstallationProgress * 100));
            var r = op.GetAwaiter().GetResult();
            var (result, text) = MapInstall(r.Status, r.ExtendedErrorCode?.HResult ?? 0, r.InstallerErrorCode, action == AppAction.Upgrade);
            bool reboot = r.RebootRequired || (r.ExtendedErrorCode?.HResult ?? 0) == RebootToFinish;

            var extras = new List<InstalledProgram>();
            if (result is AppOutcome.Installed or AppOutcome.Updated)
            {
                var installed = WinGetSession.FindById(WinGetSession.Connect(pm, CompositeSearchBehavior.LocalCatalogs), app.Id)?.InstalledVersion;
                to = installed?.Version ?? to;
                var codes = installed?.ProductCodes.ToList() ?? [];
                extras = ProgramsSnapshot.Extras(ProgramsSnapshot.Added(before, ProgramsSnapshot.Take()),
                                                 pkg.Name, app.Publisher, codes, _runtimes);
                if (extras.Count > 0)
                    text += " Also installed: " + string.Join(", ", extras.Select(e => e.Publisher.Length > 0 ? $"{e.Name} ({e.Publisher})" : e.Name)) + ".";
            }
            if (reboot && !text.Contains("Restart", StringComparison.Ordinal)) text += " Restart to finish.";
            return Result(app, action, result, text) with { FromVersion = from, ToVersion = to, RebootRequired = reboot, Extras = extras };
        });

    public async Task<AppResult> UninstallAsync(AppRow app, IProgress<AppStep>? progress = null, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return Result(app, AppAction.Uninstall, AppOutcome.Cancelled, "Cancelled before it started.");
        var result = await _session.RunAsync(pm =>
        {
            var pkg = WinGetSession.FindById(WinGetSession.Connect(pm, CompositeSearchBehavior.LocalCatalogs), app.Id);
            if (pkg?.InstalledVersion is null) return Result(app, AppAction.Uninstall, AppOutcome.Skipped, "Not installed.");
            string from = pkg.InstalledVersion.Version;
            var op = pm.UninstallPackageAsync(pkg, new UninstallOptions { PackageUninstallMode = PackageUninstallMode.Silent });
            op.Progress = (_, p) => progress?.Report(new AppStep(app.Id, "Uninstalling", p.UninstallationProgress * 100));
            // ⚠️ Some uninstallers ignore the silent switch and wait on a prompt
            // nobody sees (IrfanView's, A7); winget would wait with them forever.
            var task = op.AsTask();
            if (!task.Wait(UninstallLimit))
            {
                op.Cancel();
                return Result(app, AppAction.Uninstall, AppOutcome.Failed,
                    $"Its uninstaller didn't finish in {UninstallLimit.TotalMinutes:0} minutes. It may be waiting for an answer in a window.") with { FromVersion = from };
            }
            var r = task.Result;
            var (outcome, text) = r.Status == UninstallResultStatus.Ok
                ? (AppOutcome.Uninstalled, "Uninstalled.")
                : MapStatus(r.Status.ToString(), r.ExtendedErrorCode?.HResult ?? 0, 0);
            // ⚠️ winget's Ok isn't proof (A7): NSIS uninstallers hand off to a
            // copy of themselves and exit, and some then wait on a prompt or do
            // nothing. Check the installed list until the app is gone.
            if (outcome == AppOutcome.Uninstalled && !GoneWithin(pm, app.Id, UninstallSettle))
                (outcome, text) = (AppOutcome.Failed, "Its uninstaller reported success, but the app is still installed. It may be waiting for an answer in a window.");
            if (r.RebootRequired) text += " Restart to finish.";
            return Result(app, AppAction.Uninstall, outcome, text) with { FromVersion = from, RebootRequired = r.RebootRequired };
        });
        Record(result, app);
        return result;
    }

    /// <summary>How long an uninstaller may run before it's given up on.</summary>
    public TimeSpan UninstallLimit { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long an uninstall may take to show in the installed list.</summary>
    public TimeSpan UninstallSettle { get; init; } = TimeSpan.FromSeconds(90);

    static bool GoneWithin(PackageManager pm, string id, TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (true)
        {
            if (WinGetSession.FindById(WinGetSession.Connect(pm, CompositeSearchBehavior.LocalCatalogs), id)?.InstalledVersion is null) return true;
            if (DateTime.UtcNow >= until) return false;
            Thread.Sleep(5000);
        }
    }

    static string Describe(PackageInstallProgressState s) => s switch
    {
        PackageInstallProgressState.Queued => "Queued",
        PackageInstallProgressState.Downloading => "Downloading",
        PackageInstallProgressState.Installing => "Installing",
        PackageInstallProgressState.PostInstall => "Finishing",
        _ => "Finished",
    };

    static AppResult Result(AppRow app, AppAction action, AppOutcome outcome, string message) =>
        new() { Id = app.Id, Name = app.Name, Action = action, Outcome = outcome, Message = message };

    void Record(AppResult r, AppRow app)
    {
        if (r.Outcome is AppOutcome.Cancelled) return;
        History.Append(new HistoryEntry(DateTime.Now, r.Id, r.Name,
            r.FromVersion is null ? null : new DriverRef("", "", r.FromVersion, null, r.Name, app.Publisher),
            new DriverRef("", "", r.ToVersion ?? "", null, r.Name, app.Publisher),
            app.Source, r.Succeeded, r.Outcome == AppOutcome.Simulated, r.RebootRequired, r.Message, null,
            new AppChange(r.Action.ToString(), r.FromVersion, r.ToVersion, r.Extras.Select(e => e.Name).ToList())));
    }

    // winget's documented return codes (returnCodes.md) that change what the user sees.
    public const int AlreadyInstalled = unchecked((int)0x8A150061);
    public const int UpdateNotApplicable = unchecked((int)0x8A15002B);
    public const int HashMismatch = unchecked((int)0x8A150011);
    public const int InstallInProgress = unchecked((int)0x8A150102);
    public const int DiskFull = unchecked((int)0x8A150105);
    public const int RebootToFinish = unchecked((int)0x8A150109);
    public const int RebootBeforeInstall = unchecked((int)0x8A15010A);
    public const int CancelledByUser = unchecked((int)0x8A15010C);
    public const int BlockedByPolicyCode = unchecked((int)0x8A15010F);
    public const int DependenciesFailed = unchecked((int)0x8A150110);

    /// <summary>§3.5: what an install or upgrade result means to the user.</summary>
    public static (AppOutcome, string) MapInstall(InstallResultStatus status, int hr, uint installerCode, bool upgrade)
    {
        if (status == InstallResultStatus.Ok) return upgrade ? (AppOutcome.Updated, "Updated.") : (AppOutcome.Installed, "Installed.");
        if (hr == RebootToFinish) return upgrade ? (AppOutcome.Updated, "Updated. Restart to finish.") : (AppOutcome.Installed, "Installed. Restart to finish.");
        if (hr == AlreadyInstalled) return (AppOutcome.Skipped, "Already installed.");
        if (status == InstallResultStatus.NoApplicableUpgrade || hr == UpdateNotApplicable) return (AppOutcome.UpToDate, "Up to date.");
        return MapStatus(status.ToString(), hr, installerCode);
    }

    static (AppOutcome, string) MapStatus(string status, int hr, uint installerCode)
    {
        if (status == "PackageAgreementsNotAccepted") return (AppOutcome.NeedsConsent, "Needs you to accept its licence terms.");
        if (status == "BlockedByPolicy" || hr == BlockedByPolicyCode) return (AppOutcome.Blocked, "Blocked by this PC's policy.");
        // Found in A7 (HandBrake): reported with a DownloadError status, but
        // it's a dependency (a runtime) that didn't install.
        if (hr == DependenciesFailed) return (AppOutcome.Failed, "A package it needs (usually a runtime) didn't install.");
        if (hr == HashMismatch) return (AppOutcome.DownloadFailed, "The download didn't match the catalog's checksum, so it wasn't installed.");
        if (status == "DownloadError") return (AppOutcome.DownloadFailed, $"Download failed (0x{hr:X8}).");
        if (hr == InstallInProgress) return (AppOutcome.Failed, "Another installation is in progress.");
        if (hr == DiskFull) return (AppOutcome.Failed, "Not enough disk space.");
        if (hr == RebootBeforeInstall) return (AppOutcome.Failed, "Restart Windows, then try again.");
        if (hr == CancelledByUser) return (AppOutcome.Failed, "The installer was cancelled.");
        if (status == "NoApplicableInstallers") return (AppOutcome.Failed, "No installer for this PC (architecture or scope).");
        string detail = installerCode != 0 ? $"installer exit code {installerCode}, " : "";
        return (AppOutcome.Failed, $"Failed ({status}, {detail}0x{hr:X8}).");
    }

    public void Dispose() => _session.Dispose();
}
