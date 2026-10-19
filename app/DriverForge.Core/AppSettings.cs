using System.Text.Json;

namespace DriverForge.Core;

/// <summary>Per-machine settings, stored next to the app (it's portable).</summary>
public sealed class AppSettings
{
    public bool CreateRestorePoint { get; set; } = true;
    public bool RestartWhenNeeded { get; set; }
    /// <summary>Simulate installs: everything runs except the driver install itself.</summary>
    public bool TestMode { get; set; }
    public HashSet<string> IgnoredDevices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Search the Microsoft Update Catalog during scans.</summary>
    public bool UseCatalog { get; set; } = true;
    /// <summary>Folders of driver packages to offer drivers from (SPEC §4.3).</summary>
    public List<string> DriverFolders { get; set; } = [];
    public ScanSchedule ScanSchedule { get; set; } = ScanSchedule.Off;

    // ── Apps page (docs/APPS.md) ──
    /// <summary>Package ID → ignored version ("" = every version). A version
    /// entry stops applying once a newer one is available (§5.4).</summary>
    public Dictionary<string, string> IgnoredApps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Install app updates in the scheduled run (§8). Off by default.</summary>
    public bool AutoUpdateApps { get; set; }
    /// <summary>Restore point before a batch of app installs (§5.1). Off by default.</summary>
    public bool AppsRestorePoint { get; set; }
    /// <summary>Allow per-user installers (PackageInstallScope.Any) instead of machine-wide only (§3.4).</summary>
    public bool AppsAnyScope { get; set; }

    /// <summary>True when this update should be hidden by the ignore list.</summary>
    public bool IsAppIgnored(string id, string? availableVersion) =>
        IgnoredApps.TryGetValue(id, out var v) && (v.Length == 0 || string.Equals(v, availableVersion, StringComparison.OrdinalIgnoreCase));

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options);
                if (s is not null)
                {
                    s.IgnoredDevices = new(s.IgnoredDevices, StringComparer.OrdinalIgnoreCase);
                    s.IgnoredApps = new(s.IgnoredApps, StringComparer.OrdinalIgnoreCase);
                    return s;
                }
            }
        }
        catch (JsonException) { }
        return new AppSettings();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
