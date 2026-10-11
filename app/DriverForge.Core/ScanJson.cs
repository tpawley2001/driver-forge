using System.Text.Json;

namespace DriverForge.Core;

/// <summary>A plain JSON dump of a scan, for checking it against other tools. Not a stable format.</summary>
public static class ScanJson
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Write(ScanResult r) => JsonSerializer.Serialize(new
    {
        at = r.At,
        os = r.Os,
        devices = r.Devices.Select(d => new
        {
            d.InstanceId,
            d.Name,
            d.Description,
            d.Manufacturer,
            d.Class,
            d.ClassGuid,
            d.HardwareIds,
            d.CompatibleIds,
            d.ProblemCode,
            d.NeedsDriver,
            installed = d.Installed is { } i ? new
            {
                i.Description,
                i.Provider,
                Version = i.Version.ToString(),
                Date = i.Date?.ToString("yyyy-MM-dd"),
                i.Inf,
                i.InfSection,
                i.MatchingId,
                Rank = i.Rank is { } rk ? $"0x{rk:X8}" : null,
            } : null,
            offers = r.OffersFor(d).Select(o => new
            {
                o.Source,
                o.Description,
                o.Provider,
                Version = o.Version.ToString(),
                Date = o.Date?.ToString("yyyy-MM-dd"),
                o.Inf,
                o.InfSection,
                o.MatchedId,
                Match = o.Match.ToString(),
                Rank = $"0x{o.Rank:X8}",
                o.Signed,
                o.Confirmed,
            }),
            assessment = Recommender.Assess(d, r.OffersFor(d)) is var a ? new { State = a.State.ToString(), a.Reason, Recommended = a.Recommended?.Id } : null,
        }),
        r.Warnings,
    }, Options);
}
