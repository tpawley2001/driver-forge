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
        }),
        r.Warnings,
    }, Options);
}
