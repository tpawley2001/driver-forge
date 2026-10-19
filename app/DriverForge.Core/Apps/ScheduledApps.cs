namespace DriverForge.Core.Apps;

/// <summary>
/// The app pass of the scheduled scan (APPS.md §8): count the app updates
/// waiting, or install them when the user turned that on. Self-updating apps
/// and the ignore list are left alone either way.
/// </summary>
public static class ScheduledApps
{
    public static async Task<(int Pending, int Updated)> RunAsync(string data, AppSettings settings)
    {
        using var engine = new WinGetAppEngine(data);
        if (!(await engine.GetStatusAsync()).Available) return (0, 0);
        var updates = (await engine.ListUpdatesAsync())
            .Where(r => !r.SelfUpdating && !settings.IsAppIgnored(r.Id, r.AvailableVersion))
            .ToList();
        if (!settings.AutoUpdateApps || updates.Count == 0) return (updates.Count, 0);

        var options = new AppInstallOptions { MachineScopeOnly = !settings.AppsAnyScope, TestMode = settings.TestMode };
        // Terms need a person: those updates stay pending (AppQueue reports NeedsConsent).
        var results = await new AppQueue(engine).RunAsync(await engine.DescribeAsync(updates), AppAction.Upgrade, options);
        int updated = results.Count(r => r.Outcome is AppOutcome.Updated or AppOutcome.Simulated);
        return (updates.Count - updated, updated);
    }
}
