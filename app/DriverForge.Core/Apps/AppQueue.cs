namespace DriverForge.Core.Apps;

/// <summary>
/// Runs a batch one package at a time (APPS.md §5.1). A failure never stops
/// the queue; Cancel stops before the next package, never during one.
/// </summary>
public sealed class AppQueue(IAppEngine engine)
{
    public async Task<IReadOnlyList<AppResult>> RunAsync(IReadOnlyList<AppRow> apps, AppAction action, AppInstallOptions options,
                                                         IProgress<AppStep>? progress = null, CancellationToken ct = default)
    {
        var results = new List<AppResult>();
        foreach (var app in apps)
        {
            if (ct.IsCancellationRequested)
            {
                results.Add(new AppResult { Id = app.Id, Name = app.Name, Action = action, Outcome = AppOutcome.Cancelled, Message = "Cancelled." });
                continue;
            }
            results.Add(await RunOneAsync(app, action, options, progress, ct));
        }
        return results;
    }

    async Task<AppResult> RunOneAsync(AppRow app, AppAction action, AppInstallOptions options, IProgress<AppStep>? progress, CancellationToken ct)
    {
        if (action == AppAction.Uninstall) return await engine.UninstallAsync(app, progress, ct);

        // An installed app picked on Get apps is updated if it can be, else skipped (§3.5).
        if (action == AppAction.Install && app.IsInstalled)
            return app.IsUpdateAvailable
                ? await engine.UpgradeAsync(app, options, progress, ct)
                : new AppResult { Id = app.Id, Name = app.Name, Action = action, Outcome = AppOutcome.Skipped,
                                  Message = "Already installed.", FromVersion = app.InstalledVersion, ToVersion = app.InstalledVersion };

        // Licence terms nobody accepted: don't even start it (§5.2).
        if (app.Agreements.Count > 0 && !options.AcceptAgreements)
            return new AppResult { Id = app.Id, Name = app.Name, Action = action, Outcome = AppOutcome.NeedsConsent,
                                   Message = "Has licence terms that weren't accepted." };

        return action == AppAction.Upgrade
            ? await engine.UpgradeAsync(app, options, progress, ct)
            : await engine.InstallAsync(app, options, progress, ct);
    }

    /// <summary>The summary line for a finished batch.</summary>
    public static string Summary(IReadOnlyList<AppResult> results)
    {
        var parts = results.GroupBy(r => r.Outcome).OrderBy(g => g.Key)
            .Select(g => $"{g.Count()} {Label(g.Key)}");
        string text = string.Join(", ", parts) + ".";
        if (results.Any(r => r.RebootRequired)) text += " Restart Windows to finish.";
        if (results.Any(r => r.Extras.Count > 0)) text += " Some installs added other programs; see the results.";
        return text;
    }

    static string Label(AppOutcome o) => o switch
    {
        AppOutcome.Installed => "installed",
        AppOutcome.Updated => "updated",
        AppOutcome.Uninstalled => "uninstalled",
        AppOutcome.Skipped => "already installed",
        AppOutcome.UpToDate => "up to date",
        AppOutcome.Simulated => "simulated",
        AppOutcome.NeedsConsent => "need licence consent",
        AppOutcome.Blocked => "blocked by policy",
        AppOutcome.DownloadFailed => "download failed",
        AppOutcome.Cancelled => "cancelled",
        _ => "failed",
    };
}
