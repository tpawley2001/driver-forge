using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DriverForge.Core;

/// <summary>
/// Folders of driver packages the user adds (SPEC §4.3): an OEM's driver
/// download, a USB stick of drivers. .zip, .cab and .7z archives in them are
/// unpacked into the cache. Each folder has an index, so a scan is a lookup;
/// Windows makes the final match (<see cref="InfMatcher"/>).
/// </summary>
public sealed class FolderSource(Func<IReadOnlyList<string>> folders, string cacheDirectory) : IDriverSource
{
    public const string SourceName = "Local folder";
    public string Name => SourceName;

    public Task<IReadOnlyList<SourcedOffer>> FindAsync(IReadOnlyList<Device> devices, OsInfo os, IProgress<string>? progress, CancellationToken ct) =>
        Task.Run<IReadOnlyList<SourcedOffer>>(() =>
        {
            var roots = folders().Where(Directory.Exists).ToList();
            if (roots.Count == 0) return [];
            var byId = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                progress?.Report($"Reading driver folder {root}…");
                foreach (var dir in new[] { root }.Concat(UnpackArchives(root, ct)))
                    foreach (var (inf, ids) in FolderIndex.Update(dir, IndexPath(dir), os, ct))
                        foreach (var id in ids)
                        {
                            if (!byId.TryGetValue(id, out var set)) byId[id] = set = new(StringComparer.OrdinalIgnoreCase);
                            set.Add(inf);
                        }
            }

            var cache = new MatchCache(Path.Combine(cacheDirectory, "matches.json"));
            var found = new List<SourcedOffer>();
            foreach (var d in devices)
            {
                ct.ThrowIfCancellationRequested();
                var candidates = d.HardwareIds.Concat(d.CompatibleIds)
                    .SelectMany(id => byId.TryGetValue(id, out var s) ? s : [])
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (candidates.Count == 0) continue;
                progress?.Report($"Checking folder drivers for {d.DisplayName}…");
                // Every candidate INF's best node is an offer: they're all on disk already.
                foreach (var node in candidates.Select(inf => InfMatcher.Best(d, [inf], cache)).OfType<DriverNode>())
                    found.Add(new(d.InstanceId, InfMatcher.ToOffer(d, node, $"dir|{node.Inf}|{node.Section}", SourceName, isLocal: true)));
            }
            cache.Save();
            return found;
        }, ct);

    public Task<string> AcquireAsync(DriverOffer offer, IProgress<InstallStep>? progress, CancellationToken ct) =>
        Task.FromResult(offer.Inf is { } inf && File.Exists(inf) ? inf : throw new EngineException("The driver's folder is no longer there."));

    string IndexPath(string dir) => Path.Combine(cacheDirectory, "index", Hash(dir) + ".json");

    static readonly string[] ArchiveExtensions = [".zip", ".cab", ".7z"];

    /// <summary>Unpacks each .zip/.cab/.7z once (again when it changes); returns the unpacked folders.</summary>
    IEnumerable<string> UnpackArchives(string root, CancellationToken ct)
    {
        var archives = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => ArchiveExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
        foreach (var archive in archives)
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(archive);
            string into = Path.Combine(cacheDirectory, "archives", Hash($"{archive}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"));
            string done = Path.Combine(into, ".unpacked");
            if (!File.Exists(done))
            {
                try
                {
                    if (Directory.Exists(into)) Directory.Delete(into, true);
                    Directory.CreateDirectory(into);
                    switch (Path.GetExtension(archive).ToLowerInvariant())
                    {
                        case ".zip": ZipFile.ExtractToDirectory(archive, into); break;
                        case ".7z": SevenZip.Extract(archive, into, ct); break;
                        default:
                            var (code, _) = DriverBackup.Run("expand.exe", $"-F:* \"{archive}\" \"{into}\"");
                            if (code != 0) continue;
                            break;
                    }
                    File.WriteAllText(done, archive);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                               or SharpCompress.Common.ArchiveException or NotSupportedException)
                {
                    continue;
                }
            }
            yield return into;
        }
    }

    static string Hash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s.ToUpperInvariant())))[..16];
}

/// <summary>
/// index.json per folder (SPEC §4.3.1): INF path → size, mtime and the IDs its
/// models sections list for this OS. A file is re-read only when it changes.
/// </summary>
public static class FolderIndex
{
    public sealed record Entry(long Size, long Ticks, string Os, List<string> Ids);

    /// <summary>Every INF under the folder with the uppercase IDs it can match.</summary>
    public static IReadOnlyList<(string Inf, IReadOnlyList<string> Ids)> Update(string folder, string indexPath, OsInfo os, CancellationToken ct)
    {
        Dictionary<string, Entry> old = [];
        try
        {
            if (File.Exists(indexPath))
                old = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(indexPath)) ?? [];
        }
        catch (JsonException) { }

        string osKey = os.ToString();
        var fresh = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var inf in Directory.EnumerateFiles(folder, "*.inf", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var fi = new FileInfo(inf);
            if (old.TryGetValue(inf, out var e) && e.Size == fi.Length && e.Ticks == fi.LastWriteTimeUtc.Ticks && e.Os == osKey)
            {
                fresh[inf] = e;
                continue;
            }
            List<string> ids;
            try
            {
                ids = InfFile.Load(inf).Models(os)
                    .SelectMany(m => m.CompatibleIds.Prepend(m.HardwareId))
                    .Where(i => i.Length > 0).Select(i => i.ToUpperInvariant()).Distinct().ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            fresh[inf] = new(fi.Length, fi.LastWriteTimeUtc.Ticks, osKey, ids);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
        File.WriteAllText(indexPath, JsonSerializer.Serialize(fresh));
        return fresh.Where(kv => kv.Value.Ids.Count > 0).Select(kv => (kv.Key, (IReadOnlyList<string>)kv.Value.Ids)).ToList();
    }
}

/// <summary>.7z through SharpCompress (MIT; SPEC §4.3).</summary>
internal static class SevenZip
{
    public static void Extract(string archive, string into, CancellationToken ct)
    {
        string root = Path.GetFullPath(into) + Path.DirectorySeparatorChar;
        using var sevenZip = SharpCompress.Archives.SevenZip.SevenZipArchive.OpenArchive(archive);
        // Check every name before writing anything, so a bad archive leaves nothing behind.
        foreach (var e in sevenZip.Entries)
            if (e.Key is not { } key || !Inside(root, key))
                throw new InvalidDataException($"{Path.GetFileName(archive)} has an entry outside its folder: {e.Key}");
        // One pass front to back: a solid archive decompresses each block once.
        using var reader = sevenZip.ExtractAllEntries();
        while (reader.MoveToNextEntry())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.Entry.IsDirectory) continue;
            string path = Path.GetFullPath(Path.Combine(root, reader.Entry.Key!));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var output = File.Create(path);
            reader.WriteEntryTo(output);
        }
    }

    /// <summary>The entry name resolves to a path under <paramref name="root"/> (no "..\", no absolute path).</summary>
    internal static bool Inside(string root, string entryName) =>
        !Path.IsPathRooted(entryName)
        && Path.GetFullPath(Path.Combine(root, entryName)).StartsWith(root, StringComparison.OrdinalIgnoreCase);
}
