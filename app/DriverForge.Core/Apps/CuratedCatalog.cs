using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DriverForge.Core.Apps;

/// <summary>
/// The hand-picked list behind the Get apps tab (APPS.md §4), embedded from
/// Apps/catalog.json. It only says which winget IDs to offer and how to file
/// them; names, publishers and versions always come live from the catalog.
/// </summary>
public sealed partial class CuratedCatalog
{
    public int Version { get; init; }
    public List<string> Categories { get; init; } = [];
    public List<CuratedApp> Apps { get; init; } = [];

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static CuratedCatalog Load()
    {
        using var stream = typeof(CuratedCatalog).Assembly.GetManifestResourceStream("DriverForge.Core.Apps.catalog.json")
            ?? throw new InvalidOperationException("catalog.json is not embedded");
        return Parse(stream);
    }

    public static CuratedCatalog Parse(Stream json) =>
        JsonSerializer.Deserialize<CuratedCatalog>(json, Json) ?? throw new InvalidDataException("empty catalog");

    /// <summary>Apps the picker offers: only those verified on the test box (§4).</summary>
    public IEnumerable<CuratedApp> Offered => Apps.Where(a => a.Verified is not null);

    public CuratedApp? Find(string id) => Apps.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Everything wrong with the file; empty when it's fine.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (Version != 1) problems.Add($"unknown version {Version}");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in Apps)
        {
            if (!IdPattern().IsMatch(app.Id)) problems.Add($"{app.Id}: not a winget package ID");
            if (!seen.Add(app.Id)) problems.Add($"{app.Id}: listed twice");
            if (!Categories.Contains(app.Category)) problems.Add($"{app.Id}: unknown category {app.Category}");
            if (app.Verified is not null && !DateOnly.TryParseExact(app.Verified, "yyyy-MM-dd", out _))
                problems.Add($"{app.Id}: verified must be yyyy-MM-dd");
        }
        foreach (var c in Categories.Where(c => !Apps.Any(a => a.Category == c)))
            problems.Add($"category {c} has no apps");
        return problems;
    }

    // Publisher.Package[.More], as winget manifests name them.
    [GeneratedRegex(@"^[^.\s]+(\.[^.\s]+)+$")]
    private static partial Regex IdPattern();
}

public sealed record CuratedApp
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public bool SelfUpdating { get; init; }
    public bool MachineOnly { get; init; }
    public string? Verified { get; init; }
}
