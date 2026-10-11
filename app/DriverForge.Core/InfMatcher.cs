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
    public static DriverNode? Best(Device device, IEnumerable<string> infPaths) =>
        infPaths.SelectMany(inf => DriverNodes.FromInf(device.InstanceId, inf))
            .Order(NodeComparer.BestFirst)
            .FirstOrDefault();

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
