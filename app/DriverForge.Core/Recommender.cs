namespace DriverForge.Core;

public enum DeviceState
{
    Missing,        // no working driver and an offer exists
    Update,         // a better, not-older driver exists
    Skipped,        // something exists but we deliberately don't recommend it
    NoDriverFound,  // no working driver and nothing matches
    UpToDate,
}

public sealed record Assessment(
    Device Device,
    DeviceState State,
    DriverOffer? Recommended,
    IReadOnlyList<DriverOffer> Offers,
    string? Reason)
{
    public bool IsActionable => Recommended is not null && State is DeviceState.Missing or DeviceState.Update;
}

/// <summary>Turns a scan into what the app offers (SPEC §5.3, §5.4).</summary>
public static class Recommender
{
    // Display adapters kept current by the vendor's own app; a driver from
    // elsewhere would only fight it.
    static readonly (string Prefix, string Owner)[] VendorManaged =
    [
        (@"PCI\VEN_10DE", "the NVIDIA App"),
        (@"PCI\VEN_1002", "AMD Software"),
    ];

    public static IReadOnlyList<Assessment> Assess(ScanResult scan, ISet<string>? ignoredInstanceIds = null) =>
        scan.Devices.Select(d => Assess(d, scan.OffersFor(d), ignoredInstanceIds)).ToList();

    public static Assessment Assess(Device device, IReadOnlyList<DriverOffer> offers, ISet<string>? ignoredInstanceIds = null)
    {
        var ranked = offers.DistinctBy(o => o.Id).Order(DriverComparer.BestFirst).ToList();
        var best = ranked.FirstOrDefault();

        if (ignoredInstanceIds?.Contains(device.InstanceId) == true)
            return new(device, DeviceState.Skipped, null, ranked, "You chose to ignore this device.");

        if (device.NeedsDriver)
            return best is null
                ? new(device, DeviceState.NoDriverFound, null, ranked, "No source has a driver for this device.")
                : new(device, DeviceState.Missing, best, ranked, null);

        var managed = VendorManaged.FirstOrDefault(v =>
            device.InstanceId.StartsWith(v.Prefix, StringComparison.OrdinalIgnoreCase));
        if (managed.Owner is not null)
            return new(device, DeviceState.Skipped, null, ranked, $"Managed by {managed.Owner}, so it's left alone.");

        if (best is null)
            return new(device, DeviceState.UpToDate, null, ranked, null);

        var installed = device.Installed!;
        // Without the installed rank, assume it matched as well as the offer.
        uint installedRank = installed.Rank ?? best.Rank;

        // Never offer a downgrade as an update, however well it matches. Only
        // an offer that ranks at least as well as the installed driver would
        // win; a worse-ranked older one (an inbox generic driver) is no news.
        if (best.Rank <= installedRank && best.Date is { } bd && installed.Date is { } id && bd < id)
            return new(device, DeviceState.Skipped, null, ranked,
                $"The best-matched driver ({best.Version}, {bd:yyyy-MM-dd}) is older than the installed one, so it isn't recommended.");

        bool better = DriverComparer.Compare(best.Rank, best.Date, best.Version,
            installedRank, installed.Date, installed.Version) < 0;
        bool same = best.Version.CompareTo(installed.Version) == 0 && best.Date == installed.Date;
        return better && !same
            ? new(device, DeviceState.Update, best, ranked, null)
            : new(device, DeviceState.UpToDate, null, ranked, null);
    }
}
