using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DriverForge.Core;

/// <summary>One row of a Catalog search result.</summary>
public sealed record CatalogRow(string UpdateId, string Title, string Products, string Classification, DateOnly? LastUpdated, long Size);

/// <summary>
/// Reads the Catalog's HTML (SPEC §4.2). Not a documented API: everything
/// here fails soft (no rows) and is pinned by fixture tests.
/// </summary>
public static class CatalogParser
{
    static readonly Regex Row = new(@"<tr id=""(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})_R\d+"">(?<body>.*?)</tr>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Cell = new(@"_C(?<n>\d)_R\d+""[^>]*>(?<v>.*?)</td>", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    static readonly Regex OriginalSize = new(@"_originalSize"">\s*(?<n>\d+)\s*<", RegexOptions.CultureInvariant);
    static readonly Regex Tag = new("<[^>]+>", RegexOptions.CultureInvariant);
    static readonly Regex FileUrl = new(@"downloadInformation\[\d+\]\.files\[\d+\]\.url\s*=\s*'(?<v>[^']+)'", RegexOptions.CultureInvariant);
    static readonly Regex FileDigest = new(@"downloadInformation\[\d+\]\.files\[\d+\]\.digest\s*=\s*'(?<v>[^']*)'", RegexOptions.CultureInvariant);

    public static IReadOnlyList<CatalogRow> ParseSearch(string html)
    {
        var rows = new List<CatalogRow>();
        foreach (Match m in Row.Matches(html))
        {
            var cells = new Dictionary<int, string>();
            foreach (Match c in Cell.Matches(m.Groups["body"].Value))
                cells[c.Groups["n"].Value[0] - '0'] = Text(c.Groups["v"].Value);
            string title = cells.GetValueOrDefault(1, "");
            if (title.Length == 0) continue;
            DateOnly? updated = DateOnly.TryParseExact(cells.GetValueOrDefault(4, ""), "M/d/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
            var size = OriginalSize.Match(m.Groups["body"].Value);
            rows.Add(new CatalogRow(m.Groups["id"].Value.ToLowerInvariant(), title, cells.GetValueOrDefault(2, ""),
                cells.GetValueOrDefault(3, ""), updated, size.Success ? long.Parse(size.Groups["n"].Value, CultureInfo.InvariantCulture) : 0));
        }
        return rows;
    }

    /// <summary>
    /// A Catalog search page, with or without results. No results comes in two
    /// shapes: a "no results" message, or the results table with no rows.
    /// </summary>
    public static bool IsSearchPage(string html) =>
        Row.IsMatch(html)
        || html.Contains("ctl00_catalogBody_noResultText", StringComparison.Ordinal)
        || html.Contains("ctl00_catalogBody_ResultsHeaderTable", StringComparison.Ordinal);

    /// <summary>The download dialog's file URLs and their base64 SHA-1 digests.</summary>
    public static IReadOnlyList<(string Url, string Digest)> ParseDownload(string html)
    {
        var urls = FileUrl.Matches(html).Select(m => m.Groups["v"].Value).ToList();
        var digests = FileDigest.Matches(html).Select(m => m.Groups["v"].Value).ToList();
        return urls.Select((u, i) => (u, i < digests.Count ? digests[i] : "")).ToList();
    }

    /// <summary>The driver version in a title: its last dotted number ("… - 12.17.8.7", "… (6.0.9980.1)").</summary>
    public static DriverVersion TitleVersion(string title)
    {
        var all = Regex.Matches(title, @"(?<![\d.])\d+(?:\.\d+){1,3}(?![\d.])");
        return all.Count > 0 ? DriverVersion.Parse(all[^1].Value) : DriverVersion.Unknown;
    }

    /// <summary>The DriverVer date some titles carry ("Intel - Net - 9/29/2017 12:00:00 AM - 12.17.8.7").</summary>
    public static DateOnly? TitleDate(string title)
    {
        var m = Regex.Match(title, @"(?<!\d)(\d{1,2}/\d{1,2}/\d{4})(?!\d)");
        return m.Success && DateOnly.TryParseExact(m.Groups[1].Value, "M/d/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    /// <summary>
    /// A shortlist filter only: drop rows that plainly target another kind of
    /// Windows. Windows itself decides the rest when it reads the INF.
    /// </summary>
    public static bool MayFit(CatalogRow row, OsInfo os)
    {
        if (!row.Classification.StartsWith("Drivers", StringComparison.OrdinalIgnoreCase)) return false;
        string p = row.Products, t = row.Title;
        bool w10 = p.Contains("Windows 10", StringComparison.OrdinalIgnoreCase), w11 = p.Contains("Windows 11", StringComparison.OrdinalIgnoreCase);
        bool server = p.Contains("Server", StringComparison.OrdinalIgnoreCase);
        bool workstation = os.ProductType == 1;
        if (!w10 && !w11 && !server) return false;                       // Windows 7/8/Vista and older
        if (workstation && server && !w10 && !w11) return false;
        if (!workstation && !server) return false;
        if (os.Build < 22000 && w11 && !w10) return false;
        bool arm = (p + " " + t).Contains("ARM64", StringComparison.OrdinalIgnoreCase);
        return arm == (os.Architecture == "arm64") || !arm;
    }

    static string Text(string html) => string.Join(' ', WebUtility.HtmlDecode(Tag.Replace(html, " ")).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>
/// Microsoft Update Catalog (SPEC §4.2), for drivers Windows Update doesn't
/// offer this PC. Search results only shortlist: each candidate package is
/// downloaded, checked and expanded, and an offer exists only when Windows
/// matches one of its INFs to the device (<see cref="InfMatcher"/>).
/// </summary>
public sealed class CatalogSource(string cacheDirectory) : IDriverSource
{
    public const string SourceName = "Update Catalog";
    public string Name => SourceName;

    /// <summary>Packages up to this size are fetched during a scan to confirm the match; bigger ones at install time.</summary>
    public long ConfirmLimitBytes { get; init; } = 60L * 1024 * 1024;
    /// <summary>Candidate packages per device (newest versions first).</summary>
    public int CandidatesPerDevice { get; init; } = 2;

    static readonly TimeSpan SearchCacheAge = TimeSpan.FromHours(24);
    const string Base = "https://www.catalog.update.microsoft.com/";
    static readonly HttpClient Http = NewClient();
    static readonly SemaphoreSlim OneAtATime = new(1, 1);

    string SearchCache => Path.Combine(cacheDirectory, "search");
    string Packages => Path.Combine(cacheDirectory, "packages");

    int readable, unreadable;

    public async Task<IReadOnlyList<SourcedOffer>> FindAsync(IReadOnlyList<Device> devices, OsInfo os, IProgress<string>? progress, CancellationToken ct)
    {
        readable = unreadable = 0;
        var found = new List<SourcedOffer>();
        var withIds = devices.Where(d => d.HardwareIds.Count > 0).ToList();
        int n = 0;
        foreach (var d in withIds)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Searching the Microsoft Update Catalog ({++n} of {withIds.Count})…");
            // Most specific hardware ID first; stop at the first with results. Never compatible IDs.
            IReadOnlyList<CatalogRow> rows = [];
            int position = 0;
            for (; position < d.HardwareIds.Count && rows.Count == 0; position++)
                rows = (await SearchAsync(d.HardwareIds[position], ct)).Where(r => CatalogParser.MayFit(r, os)).ToList();
            if (rows.Count == 0) continue;
            position--;

            // One row per distinct package title, newest version first.
            var candidates = rows.GroupBy(r => r.Title, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .OrderByDescending(r => CatalogParser.TitleVersion(r.Title))
                .ThenByDescending(r => CatalogParser.TitleDate(r.Title) ?? r.LastUpdated)
                .Take(CandidatesPerDevice);
            foreach (var row in candidates)
            {
                var offer = row.Size is > 0 && row.Size <= ConfirmLimitBytes
                    ? await ConfirmedOfferAsync(d, row, ct)
                    : Unconfirmed(d, row, position);
                if (offer is not null) found.Add(new(d.InstanceId, offer));
            }
        }
        if (unreadable > 0 && readable == 0)
            throw new EngineException("The Catalog's search page has changed; Catalog results are unavailable.");
        return found;
    }

    async Task<DriverOffer?> ConfirmedOfferAsync(Device d, CatalogRow row, CancellationToken ct)
    {
        string folder;
        try { folder = await FetchAsync(row.UpdateId, null, ct); }
        catch (EngineException) { return null; }       // a bad package is no offer
        var node = InfMatcher.Best(d, InfMatcher.InfsIn(folder));
        return node is null ? null : InfMatcher.ToOffer(d, node, "cat|" + row.UpdateId, SourceName, isLocal: false, row.Size);
    }

    /// <summary>Too big to fetch during a scan: described from the row, checked by Windows before installing.</summary>
    static DriverOffer Unconfirmed(Device d, CatalogRow row, int hardwareIdPosition) => new(
        Id: "cat|" + row.UpdateId,
        Source: SourceName,
        Description: row.Title,
        Provider: row.Title.Split(" - ")[0].Trim(),
        Version: CatalogParser.TitleVersion(row.Title),
        Date: CatalogParser.TitleDate(row.Title),
        MatchedId: d.HardwareIds[hardwareIdPosition],
        Match: MatchKind.HardwareId,
        Rank: DriverRank.Compute(SignatureClass.Trusted, DriverRank.DefaultFeatureScore, DriverRank.IdentifierScore(true, hardwareIdPosition, 0)),
        Signed: true,
        IsLocal: false,
        DownloadBytes: row.Size > 0 ? row.Size : null,
        Confirmed: false);

    /// <summary>The package's expanded folder; the engine asks Windows for the node to install.</summary>
    public async Task<string> AcquireAsync(DriverOffer offer, IProgress<InstallStep>? progress, CancellationToken ct) =>
        await FetchAsync(offer.Id.StartsWith("cat|", StringComparison.Ordinal) ? offer.Id[4..] : throw new EngineException("Not a Catalog offer."), progress, ct);

    async Task<IReadOnlyList<CatalogRow>> SearchAsync(string hardwareId, CancellationToken ct)
    {
        string key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(hardwareId.ToUpperInvariant())));
        string cached = Path.Combine(SearchCache, key + ".json");
        try
        {
            if (File.Exists(cached) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cached) < SearchCacheAge)
                return JsonSerializer.Deserialize<List<CatalogRow>>(await File.ReadAllTextAsync(cached, ct)) ?? [];
        }
        catch (JsonException) { }

        string html = await PoliteAsync(() => Http.GetStringAsync(Base + "Search.aspx?q=" + Uri.EscapeDataString(hardwareId), ct), ct);
        var rows = CatalogParser.ParseSearch(html);
        Directory.CreateDirectory(SearchCache);
        if (!CatalogParser.IsSearchPage(html))
        {
            // Not a result page and not a "no results" page: keep it for diagnosis, don't cache.
            await File.WriteAllTextAsync(Path.Combine(cacheDirectory, "unreadable.html"), $"<!-- {hardwareId} -->\n{html}", ct);
            unreadable++;
            return [];
        }
        readable++;
        await File.WriteAllTextAsync(cached, JsonSerializer.Serialize(rows), ct);
        return rows;
    }

    /// <summary>Download, verify and expand a package once; later calls reuse the folder.</summary>
    async Task<string> FetchAsync(string updateId, IProgress<InstallStep>? progress, CancellationToken ct)
    {
        string dir = Path.Combine(Packages, updateId), expanded = Path.Combine(dir, "x"), done = Path.Combine(dir, "ok");
        if (File.Exists(done)) return expanded;

        string body = $"updateIDs=[{{\"size\":0,\"languages\":\"\",\"uidInfo\":\"{updateId}\",\"updateID\":\"{updateId}\"}}]";
        string dialog = await PoliteAsync(async () =>
        {
            using var resp = await Http.PostAsync(Base + "DownloadDialog.aspx",
                new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded"), ct);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct);
        }, ct);
        var files = CatalogParser.ParseDownload(dialog).Where(f => f.Url.EndsWith(".cab", StringComparison.OrdinalIgnoreCase)).ToList();
        if (files.Count == 0) throw new EngineException("The Catalog has no driver package to download for this update.");

        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(expanded);
        progress?.Report(InstallStep.Downloading);
        foreach (var (url, digest) in files)
        {
            string cab = Path.Combine(dir, Path.GetFileName(new Uri(url).LocalPath));
            await using (var src = await Http.GetStreamAsync(url, ct))
            await using (var dst = File.Create(cab))
                await src.CopyToAsync(dst, ct);
            if (digest.Length > 0)
            {
                await using var fs = File.OpenRead(cab);
                if (Convert.ToBase64String(await SHA1.HashDataAsync(fs, ct)) != digest)
                    throw new EngineException("The downloaded package doesn't match the Catalog's checksum.");
            }
            if (!Signature.IsMicrosoftSigned(cab))
                throw new EngineException("The downloaded package isn't signed by Microsoft.");
            progress?.Report(InstallStep.Extracting);
            await Task.Run(() => Expand(cab, expanded), ct);
        }
        if (InfMatcher.InfsIn(expanded).Count == 0) throw new EngineException("The package has no driver INF.");
        await File.WriteAllTextAsync(done, DateTime.UtcNow.ToString("o"), ct);
        return expanded;
    }

    /// <summary>expand.exe (in Windows) unpacks the cab; cabs inside it are unpacked once more.</summary>
    static void Expand(string cab, string into)
    {
        Cab(cab, into);
        foreach (var inner in Directory.EnumerateFiles(into, "*.cab", SearchOption.AllDirectories).ToList())
        {
            string sub = Path.Combine(Path.GetDirectoryName(inner)!, Path.GetFileNameWithoutExtension(inner));
            Directory.CreateDirectory(sub);
            Cab(inner, sub);
        }
    }

    static void Cab(string cab, string into)
    {
        var (code, output) = DriverBackup.Run("expand.exe", $"-F:* \"{cab}\" \"{into}\"");
        if (code != 0) throw new EngineException($"Couldn't unpack the driver package: {output.Trim()}");
    }

    /// <summary>One Catalog request at a time, with a short pause between them.</summary>
    static async Task<T> PoliteAsync<T>(Func<Task<T>> call, CancellationToken ct)
    {
        await OneAtATime.WaitAsync(ct);
        try
        {
            try { return await call(); }
            catch (HttpRequestException ex) { throw new EngineException($"The Catalog can't be reached ({ex.Message})."); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new EngineException("The Catalog didn't answer in time."); }
        }
        finally
        {
            await Task.Delay(250, CancellationToken.None);
            OneAtATime.Release();
        }
    }

    static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        string version = typeof(CatalogSource).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"DriverForge/{version}");
        return http;
    }
}

/// <summary>Authenticode checks through WinVerifyTrust (SPEC §4.2).</summary>
public static class Signature
{
    /// <summary>Valid Authenticode signature whose signer is Microsoft.</summary>
    public static bool IsMicrosoftSigned(string path)
    {
        if (!IsTrusted(path)) return false;
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return cert.GetNameInfo(X509NameType.SimpleName, false).StartsWith("Microsoft", StringComparison.Ordinal)
                || cert.Subject.Contains("O=Microsoft Corporation", StringComparison.Ordinal);
        }
        catch (CryptographicException) { return false; }
    }

    public static bool IsTrusted(string path)
    {
        var file = new WinTrust.FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WinTrust.FILE_INFO>(), pcwszFilePath = path };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrust.FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(file, filePtr, false);
            var data = new WinTrust.DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrust.DATA>(),
                dwUIChoice = 2,             // WTD_UI_NONE
                fdwRevocationChecks = 0,    // WTD_REVOKE_NONE
                dwUnionChoice = 1,          // WTD_CHOICE_FILE
                pFile = filePtr,
                dwStateAction = 1,          // WTD_STATEACTION_VERIFY
            };
            var action = WinTrust.GenericVerifyV2;
            int result = WinTrust.WinVerifyTrust(-1, ref action, ref data);
            data.dwStateAction = 2;         // WTD_STATEACTION_CLOSE
            WinTrust.WinVerifyTrust(-1, ref action, ref data);
            return result == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrust.FILE_INFO>(filePtr);
            Marshal.FreeHGlobal(filePtr);
        }
    }

    static class WinTrust
    {
        public static readonly Guid GenericVerifyV2Guid = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        public static Guid GenericVerifyV2 => GenericVerifyV2Guid;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct FILE_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public nint hFile;
            public nint pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DATA
        {
            public uint cbStruct;
            public nint pPolicyCallbackData;
            public nint pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public nint pFile;
            public uint dwStateAction;
            public nint hWVTStateData;
            public nint pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public nint pSignatureSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        public static extern int WinVerifyTrust(nint hwnd, ref Guid action, ref DATA data);
    }
}
