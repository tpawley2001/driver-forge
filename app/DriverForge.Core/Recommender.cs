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

        // A firmware "driver" flashes the BIOS/UEFI or a device's firmware:
        // never recommended, only installed when picked by hand.
        if (string.Equals(device.Class, "Firmware", StringComparison.OrdinalIgnoreCase))
            return new(device, DeviceState.Skipped, null, ranked, "Firmware updates flash the PC's firmware, so they're only installed if you pick one by hand.");

        var installed = device.Installed!;
        // Of Windows' matches, only the ones that suit this PC are recommended (MatchRules).
        var fit = ranked.FirstOrDefault(o => MatchRules.Suits(device, installed, o));
        if (fit is null)
            return new(device, DeviceState.UpToDate, null, ranked,
                ranked.Any(o => DriverComparer.Compare(o.Rank, o.Date, o.Version, installed.Rank ?? o.Rank, installed.Date, installed.Version) < 0)
                    ? "Drivers from other makers match this device too. They aren't recommended (they're usually for other PCs), but you can pick one by hand."
                    : null);
        best = fit;

        // Without the installed rank, assume it matched as well as the offer.
        uint installedRank = installed.Rank ?? best.Rank;

        // Never offer a downgrade as an update, however well it matches. Only an
        // older offer that ranks strictly better would win in Windows' eyes, so
        // that's the one worth a warning; at equal or worse rank Windows keeps
        // the newer installed driver too, and the device is simply up to date.
        if (best.Rank < installedRank && best.Date is { } bd && installed.Date is { } id && bd < id)
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

/// <summary>
/// Which of Windows' matches suit this PC (SPEC §5.6). Windows Update targets
/// drivers to computer models as well as devices; the Catalog and local
/// folders don't, so a match there can be a driver made for another PC (a
/// laptop's touchpad driver hooking a desktop's SMBus controller). For a
/// working device such a match is only recommended when it comes from the
/// current driver's maker, or replaces Microsoft's inbox driver through an ID
/// specific to this device rather than a generic class ID.
/// </summary>
public static class MatchRules
{
    public static bool Suits(Device device, InstalledDriver installed, DriverOffer offer)
    {
        if (offer.Source == WindowsUpdateSource.SourceName) return true;
        if (SameMaker(offer.Provider, installed.Provider)) return true;
        return IsInbox(installed) && !IsGenericId(offer.MatchedId);
    }

    public static bool IsInbox(InstalledDriver d) =>
        Maker(d.Provider) == "microsoft" && !DriverBackup.IsThirdParty(d.Inf);

    public static bool SameMaker(string a, string b)
    {
        string x = Maker(a), y = Maker(b);
        return x.Length > 0 && x == y;
    }

    /// <summary>"Intel(R) Corporation", "INTEL", "Intel Corp." → "intel".</summary>
    public static string Maker(string provider)
    {
        var words = System.Text.RegularExpressions.Regex.Replace(provider.ToLowerInvariant(), @"\(r\)|\(tm\)|[®™.,]", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 ? words[0] : "";
    }

    /// <summary>
    /// IDs that name a class or standard of device rather than one maker's
    /// device: class codes, HID usages, USB classes, standard PNP/ACPI IDs.
    /// </summary>
    public static bool IsGenericId(string id)
    {
        string u = id.ToUpperInvariant();
        return u.StartsWith("HID_DEVICE") || u.Contains("\\CC_") || u.StartsWith("USB\\CLASS_") || u.StartsWith("USB\\COMPOSITE")
            || u.StartsWith("ACPI\\PNP") || u.StartsWith("ACPI\\VEN_PNP") || u.StartsWith("*PNP") || u.StartsWith("ACPI\\ACPI")
            || u.StartsWith("ACPI\\VEN_ACPI") || u.StartsWith("ROOT\\") || u.StartsWith("SWD\\GENERIC")
            || u.StartsWith("GENERIC") || u.StartsWith("DETECTED");
    }
}
