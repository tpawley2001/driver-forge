using System.Text.Json;
using System.Text.Json.Nodes;

namespace DriverForge.Core.Apps;

/// <summary>
/// App lists in Microsoft's `winget export` format (APPS.md §8, schema
/// https://aka.ms/winget-packages.schema.2.0.json), so a list moves between
/// Driver Forge and `winget import` both ways.
/// </summary>
public static class AppList
{
    public const string Schema = "https://aka.ms/winget-packages.schema.2.0.json";

    /// <summary>Package IDs in the file, in order, duplicates dropped. Packages
    /// from sources other than the winget community catalog are left out
    /// (the Store source isn't supported yet, §1).</summary>
    public static IReadOnlyList<string> Read(string json)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })
                   ?? throw new InvalidDataException("empty list");
        var ids = new List<string>();
        foreach (var source in root["Sources"]?.AsArray() ?? [])
        {
            string? name = source?["SourceDetails"]?["Name"]?.GetValue<string>();
            if (name is not null && !name.Equals("winget", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var pkg in source?["Packages"]?.AsArray() ?? [])
                if (pkg?["PackageIdentifier"]?.GetValue<string>() is { Length: > 0 } id
                    && !ids.Contains(id, StringComparer.OrdinalIgnoreCase))
                    ids.Add(id);
        }
        if (root["Sources"] is null) throw new InvalidDataException("not a winget package list (no Sources)");
        return ids;
    }

    public static string Write(IEnumerable<string> ids, string wingetVersion)
    {
        var packages = new JsonArray();
        foreach (string id in ids.Distinct(StringComparer.OrdinalIgnoreCase))
            packages.Add(new JsonObject { ["PackageIdentifier"] = id });
        var root = new JsonObject
        {
            ["$schema"] = Schema,
            ["CreationDate"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
            ["Sources"] = new JsonArray
            {
                new JsonObject
                {
                    ["Packages"] = packages,
                    ["SourceDetails"] = new JsonObject
                    {
                        ["Argument"] = "https://cdn.winget.microsoft.com/cache",
                        ["Identifier"] = "Microsoft.Winget.Source_8wekyb3d8bbwe",
                        ["Name"] = "winget",
                        ["Type"] = "Microsoft.PreIndexed.Package",
                    },
                },
            },
            ["WinGetVersion"] = wingetVersion,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
