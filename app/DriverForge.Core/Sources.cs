namespace DriverForge.Core;

/// <summary>How an INF entry's IDs match a device's IDs (SPEC §5.1 identifier score).</summary>
public static class IdMatch
{
    /// <summary>
    /// The best (lowest) identifier score over every pair of a device ID and
    /// an INF entry ID, and the device ID that gives it; null when nothing matches.
    /// </summary>
    public static (string DeviceId, MatchKind Kind, uint Score)? Best(Device device, string infHardwareId, IReadOnlyList<string> infCompatibleIds)
    {
        var infIds = new List<string>(1 + infCompatibleIds.Count) { infHardwareId };
        infIds.AddRange(infCompatibleIds);
        (string, MatchKind, uint)? best = null;
        void Try(IReadOnlyList<string> ids, bool hardware)
        {
            for (int i = 0; i < ids.Count; i++)
                for (int k = 0; k < infIds.Count; k++)
                {
                    if (infIds[k].Length == 0 || !string.Equals(ids[i], infIds[k], StringComparison.OrdinalIgnoreCase)) continue;
                    uint score = DriverRank.IdentifierScore(hardware, i, k);
                    if (best is null || score < best.Value.Item3)
                        best = (ids[i], hardware ? MatchKind.HardwareId : MatchKind.CompatibleId, score);
                }
        }
        Try(device.HardwareIds, true);
        Try(device.CompatibleIds, false);
        return best;
    }
}

/// <summary>
/// Drivers already in this PC's driver store (SPEC §4.4), ranked by Windows.
/// Nothing to download; acquiring is a no-op.
/// </summary>
public sealed class DriverStoreSource : IDriverSource
{
    public const string SourceName = "This PC";
    public string Name => SourceName;

    public Task<IReadOnlyList<SourcedOffer>> FindAsync(IReadOnlyList<Device> devices, OsInfo os, IProgress<string>? progress, CancellationToken ct) =>
        Task.Run<IReadOnlyList<SourcedOffer>>(() =>
        {
            progress?.Report("Checking the driver store…");
            var found = new System.Collections.Concurrent.ConcurrentBag<SourcedOffer>();
            Parallel.ForEach(devices.Where(d => d.HardwareIds.Count + d.CompatibleIds.Count > 0),
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                d =>
                {
                    foreach (var node in DriverNodes.Staged(d.InstanceId))
                        if (ToOffer(d, node) is { } o) found.Add(new(d.InstanceId, o));
                });
            return found.ToList();
        }, ct);

    public Task<string> AcquireAsync(DriverOffer offer, IProgress<InstallStep>? progress, CancellationToken ct) =>
        Task.FromResult(offer.Inf ?? throw new EngineException("This driver has no INF path."));

    /// <summary>The installed driver itself isn't an offer.</summary>
    internal static DriverOffer? ToOffer(Device d, DriverNode n)
    {
        if (n.IsInstalled) return null;
        if (d.Installed is { } i && string.Equals(Path.GetFileName(n.Inf), i.Inf, StringComparison.OrdinalIgnoreCase)
            && string.Equals(n.Section, i.InfSection, StringComparison.OrdinalIgnoreCase)) return null;
        var match = IdMatch.Best(d, n.InfHardwareId, n.InfCompatibleIds);
        return new DriverOffer(
            Id: $"store|{n.Inf}|{n.Section}|{n.InfHardwareId}",
            Source: SourceName,
            Description: n.Description,
            Provider: n.Provider,
            Version: n.Version,
            Date: n.Date,
            MatchedId: match?.DeviceId ?? n.InfHardwareId,
            Match: match?.Kind ?? MatchKind.HardwareId,
            Rank: n.Rank,
            Signed: n.Signed,
            IsLocal: true,
            Inf: n.Inf,
            InfSection: n.Section);
    }
}
