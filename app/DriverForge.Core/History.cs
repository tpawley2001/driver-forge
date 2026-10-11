using System.Text.Json;

namespace DriverForge.Core;

/// <summary>A driver as recorded in the history: enough to find it again for a rollback.</summary>
public sealed record DriverRef(string Inf, string Section, string Version, string? Date, string Description, string Provider)
{
    public static DriverRef From(InstalledDriver d) =>
        new(d.Inf, d.InfSection, d.Version.ToString(), d.Date?.ToString("yyyy-MM-dd"), d.Description, d.Provider);

    public static DriverRef From(DriverOffer o) =>
        new(o.Inf is null ? "" : Path.GetFileName(o.Inf), o.InfSection ?? "", o.Version.ToString(), o.Date?.ToString("yyyy-MM-dd"), o.Description, o.Provider);

    public override string ToString() =>
        string.Join(" · ", new[] { Description, Version, Date, Inf }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>One install or rollback (SPEC §7). Stored one JSON object per line.</summary>
public sealed record HistoryEntry(
    DateTime At,
    string InstanceId,
    string DeviceName,
    DriverRef? From,
    DriverRef To,
    string Source,
    bool Ok,
    bool Simulated,
    bool NeedsRestart,
    string Message,
    string? BackupFolder)
{
    /// <summary>
    /// A real, successful change away from a driver we can get back: an inbox
    /// driver (always in Windows) or a third-party one we backed up.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanRollBack => Ok && !Simulated && From is { Inf.Length: > 0 } f
        && (!DriverBackup.IsThirdParty(f.Inf) || BackupFolder is not null);
}

/// <summary>history.jsonl: append-only, one entry per line, newest last.</summary>
public sealed class InstallHistory(string path)
{
    static readonly JsonSerializerOptions Options = new();
    readonly object gate = new();

    public string Path { get; } = path;

    public void Append(HistoryEntry e)
    {
        lock (gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.AppendAllText(Path, JsonSerializer.Serialize(e, Options) + "\n");
        }
    }

    /// <summary>Newest first. A damaged line is skipped, not fatal.</summary>
    public IReadOnlyList<HistoryEntry> Load()
    {
        lock (gate)
        {
            if (!File.Exists(Path)) return [];
            var list = new List<HistoryEntry>();
            foreach (var line in File.ReadLines(Path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { if (JsonSerializer.Deserialize<HistoryEntry>(line, Options) is { } e) list.Add(e); }
                catch (JsonException) { }
            }
            list.Reverse();
            return list;
        }
    }
}
