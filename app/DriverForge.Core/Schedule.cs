using System.Text.Json;

namespace DriverForge.Core;

public enum ScanSchedule { Off, Daily, Weekly }

/// <summary>
/// The scheduled scan (SPEC §9): a Task Scheduler task that runs
/// "DriverForge.exe --scan-only" elevated, only while the user is signed in
/// (so its notification has a desktop to show on).
/// </summary>
public static class ScheduledScan
{
    public const string TaskName = @"Driver Forge\Scan";

    public static void Apply(ScanSchedule schedule, string exePath)
    {
        if (schedule == ScanSchedule.Off)
        {
            var (_, _) = DriverBackup.Run("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F");   // fine when there's none
            return;
        }
        string when = schedule == ScanSchedule.Daily ? "/SC DAILY" : "/SC WEEKLY /D SUN";
        var (code, output) = DriverBackup.Run("schtasks.exe",
            $"/Create /TN \"{TaskName}\" /TR \"\\\"{exePath}\\\" --scan-only\" {when} /ST 12:00 /RL HIGHEST /IT /F");
        if (code != 0) throw new EngineException($"Windows couldn't schedule the scan: {output.Trim()}");
    }

    public static bool Exists() => DriverBackup.Run("schtasks.exe", $"/Query /TN \"{TaskName}\"").Code == 0;
}

/// <summary>What the last background scan found; the app shows it, tests read it.</summary>
public sealed record ScanSummary(DateTime At, int Missing, int Updates, IReadOnlyList<string> Devices,
                                 int AppUpdates = 0, int AppsUpdated = 0)
{
    /// <summary>What needs attention: drivers, plus app updates still waiting
    /// (ones the run installed itself are reported but don't count).</summary>
    public int Total => Missing + Updates + AppUpdates;

    public string Text => DriverText + AppText;

    string AppText =>
        (AppsUpdated > 0 ? $" {AppsUpdated} {(AppsUpdated == 1 ? "app was" : "apps were")} updated." : "")
        + (AppUpdates > 0 ? $" {AppUpdates} app {(AppUpdates == 1 ? "update is" : "updates are")} available." : "");

    string DriverText => (Missing, Updates) switch
    {
        (0, 0) => "All drivers are up to date.",
        (0, var u) => $"{u} driver {(u == 1 ? "update" : "updates")} available.",
        (var m, 0) => $"{m} {(m == 1 ? "device needs" : "devices need")} a driver.",
        var (m, u) => $"{m} {(m == 1 ? "device needs" : "devices need")} a driver, and {u} {(u == 1 ? "update is" : "updates are")} available.",
    };

    public static ScanSummary From(ScanResult scan, ISet<string> ignored)
    {
        var a = Recommender.Assess(scan, ignored);
        var missing = a.Where(x => x.State is DeviceState.Missing or DeviceState.NoDriverFound).ToList();
        var updates = a.Where(x => x.State == DeviceState.Update).ToList();
        return new(DateTime.Now, missing.Count, updates.Count, missing.Concat(updates).Select(x => x.Device.DisplayName).Distinct().ToList());
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}
