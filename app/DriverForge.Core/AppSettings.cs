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
