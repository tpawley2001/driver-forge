using System.Collections.Concurrent;
using System.Text.Json;

namespace DriverForge.Core;

/// <summary>
/// The one way an INF-based driver is matched to a device, whatever source it
/// came from: ask Windows (DI_ENUMSINGLEINF) which of the package's drivers
/// it would use for this device and how it ranks them (SPEC §5.1). Nothing
/// is offered that Windows wouldn't install on the device.
/// </summary>
public static class InfMatcher
{
    /// <summary>Windows' best driver for the device among these INFs, or null when none applies.</summary>
    public static DriverNode? Best(Device device, IEnumerable<string> infPaths, MatchCache? cache = null) =>
        infPaths.Select(inf => cache is null ? BestIn(device, inf) : cache.Get(device, inf, () => BestIn(device, inf)))
            .OfType<DriverNode>()
            .Order(NodeComparer.BestFirst)
            .FirstOrDefault();

    static DriverNode? BestIn(Device device, string inf) =>
        DriverNodes.FromInf(device.InstanceId, inf).Order(NodeComparer.BestFirst).FirstOrDefault();

    /// <summary>An offer built from Windows' own view of the node.</summary>
    public static DriverOffer ToOffer(Device device, DriverNode node, string id, string source, bool isLocal, long? downloadBytes = null)
    {
        var match = IdMatch.Best(device, node.InfHardwareId, node.InfCompatibleIds);
        return new DriverOffer(
            Id: id,
            Source: source,
            Description: node.Description,
            Provider: node.Provider,
            Version: node.Version,
            Date: node.Date,
            MatchedId: match?.DeviceId ?? node.InfHardwareId,
            Match: match?.Kind ?? MatchKind.HardwareId,
            Rank: node.Rank,
            Signed: node.Signed,
            IsLocal: isLocal,
            DownloadBytes: downloadBytes,
            Inf: node.Inf,
            InfSection: node.Section);
    }

    /// <summary>The INFs in a folder tree (an expanded package).</summary>
    public static IReadOnlyList<string> InfsIn(string folder) =>
        Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.inf", SearchOption.AllDirectories).ToList() : [];
}

/// <summary>Windows' order among driver nodes (SPEC §5.2): rank, then newest date, then highest version.</summary>
public sealed class NodeComparer : IComparer<DriverNode>
{
    public static readonly NodeComparer BestFirst = new();

    public int Compare(DriverNode? x, DriverNode? y) =>
        x is null ? (y is null ? 0 : 1) : y is null ? -1
        : DriverComparer.Compare(x.Rank, x.Date, x.Version, y.Rank, y.Date, y.Version);
}

/// <summary>
/// Windows' answer per (device, INF), kept on disk between scans. It only
/// changes when the INF file or the device's IDs change, so both are in the
/// key; asking SetupAPI is the slow part of a folder or Catalog scan.
/// </summary>
public sealed class MatchCache(string path)
{
    public sealed record Item(DriverNode? Node);

    readonly ConcurrentDictionary<string, Item> items = Load(path);
    readonly ConcurrentDictionary<string, bool> used = new();

    public DriverNode? Get(Device device, string inf, Func<DriverNode?> ask)
    {
        var fi = new FileInfo(inf);
        string key = string.Join("|", device.InstanceId, string.Join(",", device.HardwareIds.Concat(device.CompatibleIds)),
            device.Installed?.Inf ?? "", inf, fi.Exists ? fi.Length : -1, fi.Exists ? fi.LastWriteTimeUtc.Ticks : 0);
        used[key] = true;
        return items.GetOrAdd(key, _ => new Item(ask())).Node;
    }

    /// <summary>Writes the entries this scan used; the rest are dropped.</summary>
    public void Save()
    {
        var keep = items.Where(kv => used.ContainsKey(kv.Key)).ToDictionary();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(keep));
    }

    static ConcurrentDictionary<string, Item> Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, Item>>(File.ReadAllText(path)) is { } d)
                return new(d);
        }
        catch (JsonException) { }
        return new();
    }
}
