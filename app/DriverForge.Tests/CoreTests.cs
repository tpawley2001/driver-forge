using DriverForge.Core;
using Xunit;

namespace DriverForge.Tests;

public class DriverVersionTests
{
    [Theory]
    [InlineData("10.1.1.45", "10.1.1.9", 1)]     // numeric, not text
    [InlineData("6.0", "6.0.0.0", 0)]           // missing parts are 0
    [InlineData("1.2.3.4", "1.2.3.4", 0)]
    [InlineData("2.0", "", 1)]                  // unknown sorts lowest
    [InlineData("1.2.3.4.5", "0.0.0.1", -1)]    // too many parts is unknown
    [InlineData("1.70000", "0.1", -1)]          // part over 16 bits is unknown
    public void Compares_numerically(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(DriverVersion.Parse(a).CompareTo(DriverVersion.Parse(b))));

    [Fact]
    public void Prints_four_parts() => Assert.Equal("6.0.0.0", DriverVersion.Parse("6.0").ToString());
}

public class DriverRankTests
{
    // Values from Microsoft's "Identifier Score" page.
    [Theory]
    [InlineData(true, 0, 0, 0x0000u)]   // device HW ID #0 = INF HW ID
    [InlineData(true, 2, 0, 0x0002u)]
    [InlineData(true, 1, 3, 0x1001u)]   // device HW ID = INF compatible ID
    [InlineData(false, 4, 0, 0x2004u)]  // device compatible ID = INF HW ID
    [InlineData(false, 2, 1, 0x3002u)]  // compat j=2 = INF compat k=0
    [InlineData(false, 2, 3, 0x3202u)]  // j + k*0x100, k=2
    [InlineData(false, 0, 40, 0x3FFFu)] // capped within its type
    public void Identifier_score(bool hwid, int devicePos, int infPos, uint expected) =>
        Assert.Equal(expected, DriverRank.IdentifierScore(hwid, devicePos, infPos));

    [Fact]
    public void Composes_SSGGTHHH()
    {
        Assert.Equal(0x00FF0001u, DriverRank.Compute(SignatureClass.Trusted, DriverRank.DefaultFeatureScore, 0x0001));
        Assert.Equal(0xFF003000u, DriverRank.Compute(SignatureClass.Unknown, 0x00, 0x3000));
    }

    [Fact]
    public void Signature_classes_keep_their_order()
    {
        var order = new[] { SignatureClass.Trusted, SignatureClass.UnsignedNtSection, SignatureClass.Unsigned, SignatureClass.Unknown }
            .Select(DriverRank.SignatureScore).ToList();
        Assert.Equal(order.Order(), order);
        Assert.Equal(0xFF, order[^1]);
    }
}

public class DriverComparerTests
{
    static DriverOffer O(string id, uint rank, DateOnly? date, string ver) =>
        new(id, "test", id, "", DriverVersion.Parse(ver), date, "X", MatchKind.HardwareId, rank, true, true);

    [Fact]
    public void Rank_then_date_then_version()
    {
        var offers = new[]
        {
            O("newest-but-worse-rank", 0x00FF1000, new(2025, 1, 1), "9.0"),
            O("old", 0x00FF0000, new(2019, 1, 1), "9.0"),
            O("new-low-version", 0x00FF0000, new(2021, 1, 1), "1.0"),
            O("new-high-version", 0x00FF0000, new(2021, 1, 1), "2.0"),
        };
        Assert.Equal(["new-high-version", "new-low-version", "old", "newest-but-worse-rank"],
            offers.Order(DriverComparer.BestFirst).Select(o => o.Id));
    }
}

public class RecommenderTests
{
    static readonly DemoEngine Demo = new();

    static Assessment For(string name)
    {
        var scan = Demo.ScanAsync(null).GetAwaiter().GetResult();
        return Recommender.Assess(scan).Single(a => a.Device.DisplayName == name);
    }

    [Fact]
    public void Missing_device_gets_the_best_offer()
    {
        var a = For("SM Bus Controller");
        Assert.Equal(DeviceState.Missing, a.State);
        Assert.True(a.IsActionable);
    }

    [Fact]
    public void Better_rank_is_an_update()
    {
        var a = For("PCI standard ISA bridge");
        Assert.Equal(DeviceState.Update, a.State);
        Assert.Equal("10.1.1.45", a.Recommended!.Version.ToString());
    }

    [Fact]
    public void Newer_date_at_equal_rank_is_an_update()
    {
        var a = For("Realtek Audio");
        Assert.Equal(DeviceState.Update, a.State);
        Assert.Equal("6.0.9600.1", a.Recommended!.Version.ToString());
    }

    [Fact]
    public void Older_driver_is_never_an_update_even_with_a_better_rank()
    {
        var a = For("Ethernet Connection I219-LM");
        Assert.Equal(DeviceState.Skipped, a.State);
        Assert.Null(a.Recommended);
        Assert.Contains("older", a.Reason);
    }

    [Fact]
    public void Vendor_managed_display_is_skipped() =>
        Assert.Equal(DeviceState.Skipped, For("NVIDIA GeForce RTX 3060 Ti").State);

    [Fact]
    public void No_offer_for_a_broken_device_is_no_driver_found() =>
        Assert.Equal(DeviceState.NoDriverFound, For("Unknown device").State);

    [Fact]
    public void Working_device_without_offers_is_up_to_date() =>
        Assert.Equal(DeviceState.UpToDate, For("Logitech USB Receiver").State);

    [Fact]
    public void Ignored_device_is_skipped()
    {
        var d = For("SM Bus Controller").Device;
        var a = Recommender.Assess(d, [], new HashSet<string> { d.InstanceId });
        Assert.Equal(DeviceState.Skipped, a.State);
    }

    [Fact]
    public void Same_driver_as_installed_is_up_to_date()
    {
        var installed = new InstalledDriver("x", "v", DriverVersion.Parse("1.0"), new(2020, 1, 1), "oem1.inf", "X", 0x00FF2000);
        var d = new Device("D", "D", "D", "", "", ["X"], [], 0, installed);
        var offer = new DriverOffer("o", "test", "x", "v", DriverVersion.Parse("1.0"), new(2020, 1, 1), "X", MatchKind.HardwareId, 0x00FF0000, true, true);
        Assert.Equal(DeviceState.UpToDate, Recommender.Assess(d, [offer]).State);
    }

    [Fact]
    public void Older_driver_at_equal_rank_is_up_to_date()
    {
        var installed = new InstalledDriver("Wi-Fi", "Intel", DriverVersion.Parse("20.70.18.2"), new(2020, 6, 24), "oem40.inf", "PCI\\VEN_8086&DEV_24F3", 0x00FC0001);
        var d = new Device("D", "D", "D", "", "Net", ["PCI\\VEN_8086&DEV_24F3"], [], 0, installed);
        var older = new DriverOffer("s", "This PC", "Wi-Fi", "Intel", DriverVersion.Parse("20.70.3.3"), new(2018, 9, 3), "PCI\\VEN_8086&DEV_24F3", MatchKind.HardwareId, 0x00FC0001, true, true);
        var a = Recommender.Assess(d, [older]);
        Assert.Equal(DeviceState.UpToDate, a.State);
        Assert.Single(a.Offers);     // still listed for a hand-picked rollback
    }

    [Fact]
    public void Firmware_is_never_recommended()
    {
        var installed = new InstalledDriver("Firmware Resource", "Microsoft", DriverVersion.Parse("10.0.19041.1"), new(2006, 6, 21), "c_firmware.inf", "UEFI\\RES_X", 0x00FF3000);
        var d = new Device("UEFI\\RES_X\\0", "System Firmware", "System Firmware", "", "Firmware", ["UEFI\\RES_X"], [], 0, installed);
        var bios = new DriverOffer("wu|f", "Windows Update", "HP System Firmware", "HP", DriverVersion.Parse("2.58"), new(2022, 7, 29), "UEFI\\RES_X", MatchKind.HardwareId, 0x00FF0000, true, false);
        var a = Recommender.Assess(d, [bios]);
        Assert.Equal(DeviceState.Skipped, a.State);
        Assert.False(a.IsActionable);
        Assert.Single(a.Offers);
    }

    [Fact]
    public void Worse_ranked_older_generic_driver_is_up_to_date()
    {
        var installed = new InstalledDriver("Vendor", "Intel", DriverVersion.Parse("10.1.1.45"), new(2020, 9, 9), "oem3.inf", "PCI\\VEN_8086&DEV_A145", 0x00FF0001);
        var d = new Device("D", "D", "D", "", "", ["PCI\\VEN_8086&DEV_A145&REV_31", "PCI\\VEN_8086&DEV_A145"], ["PCI\\CC_0601"], 0, installed);
        var generic = new DriverOffer("g", "This PC", "PCI standard ISA bridge", "Microsoft", DriverVersion.Parse("10.0.19041.1"), new(2006, 6, 21), "PCI\\CC_0601", MatchKind.CompatibleId, 0x00FF2000, true, true);
        var a = Recommender.Assess(d, [generic]);
        Assert.Equal(DeviceState.UpToDate, a.State);
        Assert.Null(a.Reason);
    }
}

public class PropertyDataTests
{
    static byte[] Utf16(string s) => System.Text.Encoding.Unicode.GetBytes(s);

    [Fact]
    public void String_stops_at_nul() => Assert.Equal("Realtek", PropertyData.String(Utf16("Realtek\0junk")));

    [Fact]
    public void String_list_keeps_windows_order()
    {
        var ids = PropertyData.StringList(Utf16("PCI\\VEN_8086&DEV_15B7&SUBSYS_06DE1028&REV_31\0PCI\\VEN_8086&DEV_15B7&SUBSYS_06DE1028\0PCI\\VEN_8086&DEV_15B7\0\0"));
        Assert.Equal(["PCI\\VEN_8086&DEV_15B7&SUBSYS_06DE1028&REV_31", "PCI\\VEN_8086&DEV_15B7&SUBSYS_06DE1028", "PCI\\VEN_8086&DEV_15B7"], ids);
    }

    [Fact]
    public void Empty_string_list_is_empty() => Assert.Empty(PropertyData.StringList(Utf16("\0")));

    [Fact]
    public void Odd_byte_count_is_tolerated() => Assert.Equal("A", PropertyData.String([0x41, 0, 0]));

    [Fact]
    public void Driver_date_is_the_utc_calendar_date() =>
        Assert.Equal(new DateOnly(2006, 6, 21), PropertyData.FileTimeDate(new DateTime(2006, 6, 21, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc()));

    [Fact]
    public void Zero_driver_date_is_unknown() => Assert.Null(PropertyData.FileTimeDate(0));
}

/// <summary>Reads the real PC the tests run on. Read-only.</summary>
public class LiveScanTests
{
    [Fact]
    public void Scan_finds_devices_with_ids()
    {
        var devices = DeviceScanner.Scan();
        Assert.NotEmpty(devices);
        Assert.All(devices, d => Assert.False(string.IsNullOrEmpty(d.InstanceId)));
        Assert.Equal(devices.Count, devices.Select(d => d.InstanceId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        // Every PC has PCI devices with hardware IDs and inbox drivers.
        Assert.Contains(devices, d => d.InstanceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) && d.HardwareIds.Count > 0);
        Assert.Contains(devices, d => d.Installed is { Rank: not null, Date: not null } i && i.Version.IsKnown && i.Inf.EndsWith(".inf", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Os_is_windows_10_or_later()
    {
        var os = DeviceScanner.Os();
        Assert.Equal(10, os.Major);
        Assert.True(os.Build >= 10240);
        Assert.Contains(os.Architecture, new[] { "amd64", "arm64" });
    }
}

public class NeedsDriverTests
{
    static readonly InstalledDriver Drv = new("x", "v", DriverVersion.Parse("1.0"), new(2020, 1, 1), "oem1.inf", "X", 0x00FF2000);
    static Device Dev(int problem, InstalledDriver? d) => new("D", "D", "D", "", "", ["X"], [], problem, d);

    [Fact] public void Failed_install_needs_a_driver() => Assert.True(Dev(28, null).NeedsDriver);
    [Fact] public void Reinstall_needs_a_driver_even_with_one() => Assert.True(Dev(18, Drv).NeedsDriver);
    [Fact] public void Driverless_device_with_a_problem_needs_one() => Assert.True(Dev(10, null).NeedsDriver);
    [Fact] public void Driverless_device_without_a_problem_is_fine() => Assert.False(Dev(0, null).NeedsDriver);
    [Fact] public void Working_driver_is_fine() => Assert.False(Dev(0, Drv).NeedsDriver);
}

/// <summary>Windows' real ranks on the PC the tests run on. Read-only.</summary>
public class LiveRankTests(ITestOutputHelper output)
{
    [Fact]
    public void Computed_identifier_score_matches_windows()
    {
        int nodes = 0;
        var wrong = new List<string>();
        foreach (var d in DeviceScanner.Scan().Where(d => d.HardwareIds.Count > 0))
            foreach (var n in DriverNodes.Staged(d.InstanceId))
            {
                nodes++;
                var m = IdMatch.Best(d, n.InfHardwareId, n.InfCompatibleIds);
                uint windows = n.Rank & 0xFFFF;
                if (m is null || m.Value.Score != windows)
                    wrong.Add($"{d.InstanceId} {Path.GetFileName(n.Inf)} {n.InfHardwareId}: windows 0x{windows:X4} computed {(m is null ? "none" : $"0x{m.Value.Score:X4}")}");
            }
        output.WriteLine($"{nodes} staged driver nodes, {wrong.Count} identifier mismatches");
        foreach (var w in wrong.Take(20)) output.WriteLine(w);
        Assert.True(nodes > 0);
        Assert.Empty(wrong);
    }

    [Fact]
    public void Installed_driver_is_among_the_staged_nodes_with_its_rank()
    {
        var d = DeviceScanner.Scan().First(d => d.InstanceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) && d.Installed?.Rank is not null);
        var installed = DriverNodes.Staged(d.InstanceId).Where(n => n.IsInstalled).ToList();
        Assert.Single(installed);
        Assert.Equal(d.Installed!.Rank, installed[0].Rank);
        Assert.Equal(d.Installed.Version, installed[0].Version);
    }

    [Fact]
    public void Driver_version_dwordlong_unpacks_most_significant_first() =>
        Assert.Equal("10.0.19041.1", DriverNodes.Version(0x000A_0000_4A61_0001).ToString());
}

public class WindowsUpdateTests
{
    [Theory]
    [InlineData("Intel - System - 10.1.1.45", "10.1.1.45")]
    [InlineData("Realtek Semiconductor Corp. - Extension - 10.0.19041.1 ", "10.0.19041.1")]
    [InlineData("INTEL - Bluetooth - 22.150.0.6", "22.150.0.6")]
    [InlineData("Some driver without a version", "")]
    public void Version_comes_from_the_title(string title, string version) =>
        Assert.Equal(version, WindowsUpdateSource.VersionFromTitle(title).ToString());

    static readonly Device Dev = new("PCI\\VEN_8086&DEV_A146&SUBSYS_8055103C&REV_31\\3&1", "LPC", "LPC", "", "System",
        ["PCI\\VEN_8086&DEV_A146&SUBSYS_8055103C&REV_31", "PCI\\VEN_8086&DEV_A146&SUBSYS_8055103C", "PCI\\VEN_8086&DEV_A146&REV_31", "PCI\\VEN_8086&DEV_A146"],
        ["PCI\\CC_060100", "PCI\\CC_0601"], 0, null);

    [Fact]
    public void Offer_matches_by_hardware_id_with_a_trusted_rank()
    {
        var u = new WindowsUpdateSource.WuDriver("abc", "Intel - System - 10.1.1.45", "pci\\ven_8086&dev_a146", new(2020, 9, 9), "System", "Intel", "INTEL", "Intel LPC - A146", 1234);
        var o = WindowsUpdateSource.ToOffer(Dev, u)!;
        Assert.Equal("wu|abc", o.Id);
        Assert.Equal(0x00FF0003u, o.Rank);
        Assert.Equal(MatchKind.HardwareId, o.Match);
        Assert.Equal("10.1.1.45", o.Version.ToString());
    }

    [Fact]
    public void Update_for_another_device_is_no_offer() =>
        Assert.Null(WindowsUpdateSource.ToOffer(Dev, new("x", "t", "PCI\\VEN_10EC&DEV_8168", null, "", "", "", "", 0)));
}

public class HistoryTests
{
    static DriverRef Ref(string inf) => new(inf, "SEC", "1.0.0.0", "2020-01-01", "d", "p");
    static HistoryEntry Entry(DriverRef? from, string? backup, bool ok = true, bool simulated = false) =>
        new(new DateTime(2026, 10, 10, 12, 0, 0), "PCI\\X\\1", "Dev", from, Ref("oem9.inf"), "This PC", ok, simulated, false, "m", backup);

    [Fact] public void Inbox_driver_can_be_put_back_without_a_backup() => Assert.True(Entry(Ref("machine.inf"), null).CanRollBack);
    [Fact] public void Third_party_driver_needs_its_backup() => Assert.False(Entry(Ref("oem3.inf"), null).CanRollBack);
    [Fact] public void Third_party_driver_with_backup_can_be_put_back() => Assert.True(Entry(Ref("oem3.inf"), "C:\\b").CanRollBack);
    [Fact] public void No_previous_driver_is_no_rollback() => Assert.False(Entry(null, null).CanRollBack);
    [Fact] public void Failed_or_simulated_change_is_no_rollback()
    {
        Assert.False(Entry(Ref("machine.inf"), null, ok: false).CanRollBack);
        Assert.False(Entry(Ref("machine.inf"), null, simulated: true).CanRollBack);
    }

    [Fact]
    public void Log_round_trips_newest_first_and_skips_damaged_lines()
    {
        string path = Path.Combine(Path.GetTempPath(), $"df-history-{Guid.NewGuid():N}.jsonl");
        try
        {
            var log = new InstallHistory(path);
            log.Append(Entry(Ref("machine.inf"), null));
            File.AppendAllText(path, "{not json\n");
            log.Append(Entry(Ref("oem3.inf"), "C:\\b") with { DeviceName = "Second" });
            var all = log.Load();
            Assert.Equal(2, all.Count);
            Assert.Equal("Second", all[0].DeviceName);
            Assert.Equal("oem3.inf", all[0].From!.Inf);
        }
        finally { File.Delete(path); }
    }
}

public class InstallMatchTests
{
    static DriverNode Node(string section, string version, string desc = "D") =>
        new(desc, "M", "P", DriverVersion.Parse(version), null, "C:\\Windows\\INF\\oem1.inf", section, "X", [], 0, true, false);

    [Fact] public void Picks_by_section_and_version() =>
        Assert.True(WindowsEngine.Matches(Node("Needs_ISAPNP_DRV", "10.1.1.45"), "needs_isapnp_drv", DriverVersion.Parse("10.1.1.45"), "other"));
    [Fact] public void Wrong_version_is_not_the_node() =>
        Assert.False(WindowsEngine.Matches(Node("S", "10.1.1.44"), "S", DriverVersion.Parse("10.1.1.45"), "D"));
    [Fact] public void Without_a_section_the_description_decides() =>
        Assert.True(WindowsEngine.Matches(Node("S", "1.0", "Intel LPC"), null, DriverVersion.Unknown, "intel lpc"));
    [Theory]
    [InlineData("2015-09-21", "2015-09-20", true)]
    [InlineData("2015-09-21", "2015-09-23", false)]
    public void Wu_dates_allow_a_day(string a, string b, bool close) =>
        Assert.Equal(close, WindowsEngine.DatesClose(DateOnly.Parse(a), DateOnly.Parse(b)));
}

public class CatalogParserTests
{
    static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    static readonly OsInfo Win10 = new("amd64", 10, 0, 19045, 1, 0x100);
    static readonly OsInfo Win11 = new("amd64", 10, 0, 26100, 1, 0x100);

    [Fact]
    public void Search_rows_are_parsed()
    {
        var rows = CatalogParser.ParseSearch(Fixture("catalog-search-i219.html"));
        Assert.Equal(3, rows.Count);
        var r = rows[1];
        Assert.Equal("7f6ca73a-b560-4eaa-a9b2-3662dbf4c1ab", r.UpdateId);
        Assert.Equal("Intel - Net - 9/29/2017 12:00:00 AM - 12.17.8.7", r.Title);
        Assert.Equal("Windows 10 Creators Update and Later Servicing Drivers", r.Products);
        Assert.Equal("Drivers (Networking)", r.Classification);
        Assert.Equal(new DateOnly(2017, 9, 28), r.LastUpdated);
        Assert.Equal(837954, r.Size);
    }

    [Fact]
    public void Another_page_parses_too() => Assert.Equal(6, CatalogParser.ParseSearch(Fixture("catalog-search-wdt.html")).Count);

    [Fact]
    public void No_results_page_has_no_rows() => Assert.Empty(CatalogParser.ParseSearch(Fixture("catalog-search-none.html")));

    [Theory]
    [InlineData("catalog-search-i219.html", true)]
    [InlineData("catalog-search-none.html", true)]
    [InlineData("catalog-search-empty-table.html", true)]   // the other "no results" shape
    [InlineData("catalog-download.html", false)]
    public void Recognises_search_pages(string fixture, bool isSearch) => Assert.Equal(isSearch, CatalogParser.IsSearchPage(Fixture(fixture)));

    [Fact]
    public void Download_dialog_gives_the_cab_and_digest()
    {
        var files = CatalogParser.ParseDownload(Fixture("catalog-download.html"));
        var f = Assert.Single(files);
        Assert.EndsWith("_fe33f3690ad8892059623f6656d5ea58f3fdb570.cab", f.Url);
        Assert.Equal("/jPzaQrYiSBZYj9mVtXqWPP9tXA=", f.Digest);
    }

    [Theory]
    [InlineData("Intel - Net - 9/29/2017 12:00:00 AM - 12.17.8.7", "12.17.8.7", "2017-09-29")]
    [InlineData("Realtek Semiconductor Corp. MEDIA Driver Update (6.0.9980.1)", "6.0.9980.1", null)]
    [InlineData("Intel driver update for Intel(R) Ethernet Connection (2) I219-LM", "", null)]
    [InlineData("Intel Corporation - Display - 30.0.101.1339", "30.0.101.1339", null)]
    public void Title_version_and_date(string title, string version, string? date)
    {
        Assert.Equal(version, CatalogParser.TitleVersion(title).ToString());
        Assert.Equal(date is null ? null : DateOnly.Parse(date), CatalogParser.TitleDate(title));
    }

    static CatalogRow Row(string products, string cls = "Drivers (Other Hardware)", string title = "X - System - 1.0") =>
        new("id", title, products, cls, null, 1);

    [Theory]
    [InlineData("Windows 10 and later drivers", true, true)]
    [InlineData("Windows 11 Client, version 21H2 and later, Servicing Drivers", false, true)]
    [InlineData("Windows Server 2016 and Later Servicing Drivers", false, false)]
    [InlineData("Windows 7, Windows 8", false, false)]
    public void Shortlist_by_product(string products, bool win10, bool win11)
    {
        Assert.Equal(win10, CatalogParser.MayFit(Row(products), Win10));
        Assert.Equal(win11, CatalogParser.MayFit(Row(products), Win11));
    }

    [Fact]
    public void Non_driver_rows_and_arm64_on_x64_are_dropped()
    {
        Assert.False(CatalogParser.MayFit(Row("Windows 10 and later drivers", cls: "Updates"), Win10));
        Assert.False(CatalogParser.MayFit(Row("Windows 10 and later drivers", title: "Qualcomm - ARM64 - 1.0"), Win10));
    }
}

public class DedupeTests
{
    [Fact]
    public void Same_driver_from_two_sources_keeps_the_higher_priority_one()
    {
        var engine = new WindowsEngine(Path.GetTempPath());
        DriverOffer O(string id, string source) => new(id, source, "d", "Intel", DriverVersion.Parse("1.2.3.4"), new(2020, 1, 1), "X", MatchKind.HardwareId, 0, true, false);
        var kept = engine.Dedupe([O("c", CatalogSource.SourceName), O("w", WindowsUpdateSource.SourceName), O("s", DriverStoreSource.SourceName)]);
        Assert.Equal("w", Assert.Single(kept).Id);
    }
}

/// <summary>Real Catalog matches seen on the test box (SPEC §5.6).</summary>
public class MatchRuleTests
{
    static Device Dev(string provider, string inf, string[] hw, string[]? compat = null) =>
        new("X\\1", "D", "D", "", "System", hw, compat ?? [], 0,
            new InstalledDriver("cur", provider, DriverVersion.Parse("10.0.19041.1"), new(2006, 6, 21), inf, hw[0], 0x00FF1003));

    static DriverOffer Cat(string provider, string matched, uint rank = 0x00FF0003) =>
        new("cat|" + provider, CatalogSource.SourceName, "x", provider, DriverVersion.Parse("19.5.32.69"), new(2019, 8, 26), matched, MatchKind.HardwareId, rank, true, false);

    [Fact]
    public void Touchpad_vendor_smbus_driver_is_not_recommended_over_intels()
    {
        var d = Dev("INTEL", "oem31.inf", ["PCI\\VEN_8086&DEV_A123&SUBSYS_8055103C&REV_31", "PCI\\VEN_8086&DEV_A123&SUBSYS_8055103C", "PCI\\VEN_8086&DEV_A123&REV_31", "PCI\\VEN_8086&DEV_A123"]);
        var a = Recommender.Assess(d, [Cat("Synaptics", "PCI\\VEN_8086&DEV_A123", 0x00800003)]);
        Assert.Equal(DeviceState.UpToDate, a.State);
        Assert.NotNull(a.Reason);
        Assert.Single(a.Offers);
    }

    [Fact]
    public void Oem_filter_on_a_generic_hid_id_is_not_recommended() =>
        Assert.Equal(DeviceState.UpToDate, Recommender.Assess(
            Dev("Microsoft", "msmouse.inf", ["HID\\VID_046D&PID_C077&REV_7200", "HID\\VID_046D&PID_C077", "HID_DEVICE_SYSTEM_MOUSE", "HID_DEVICE_UP:0001_U:0002"]),
            [Cat("HotKey", "HID_DEVICE_UP:0001_U:0002")]).State);

    [Fact]
    public void Driver_for_a_standard_acpi_id_is_not_recommended() =>
        Assert.Equal(DeviceState.UpToDate, Recommender.Assess(Dev("Microsoft", "pci.inf", ["ACPI\\VEN_PNP&DEV_0A08", "ACPI\\PNP0A08"]), [Cat("Pci", "ACPI\\PNP0A08", 0x00FF0001)]).State);

    [Fact]
    public void Same_maker_update_is_recommended() =>
        Assert.Equal(DeviceState.Update, Recommender.Assess(Dev("Realtek Semiconductor Corp.", "oem20.inf", ["HDAUDIO\\FUNC_01&VEN_10EC&DEV_0221"]), [Cat("Realtek", "HDAUDIO\\FUNC_01&VEN_10EC&DEV_0221", 0x00FF0000)]).State);

    [Fact]
    public void Vendor_driver_replacing_inbox_through_a_device_specific_id_is_recommended() =>
        Assert.Equal(DeviceState.Update, Recommender.Assess(Dev("Microsoft", "machine.inf", ["PCI\\VEN_8086&DEV_A115&REV_31", "PCI\\VEN_8086&DEV_A115"], ["PCI\\CC_060400"]), [Cat("Intel", "PCI\\VEN_8086&DEV_A115", 0x00FF0001)]).State);

    [Fact]
    public void Windows_update_is_trusted_as_targeted() =>
        Assert.Equal(DeviceState.Update, Recommender.Assess(Dev("INTEL", "oem31.inf", ["PCI\\VEN_8086&DEV_A123"]),
            [Cat("Synaptics", "PCI\\VEN_8086&DEV_A123", 0x00800000) with { Source = WindowsUpdateSource.SourceName, Id = "wu|1" }]).State);

    [Theory]
    [InlineData("Intel(R) Corporation", "intel")]
    [InlineData("INTEL", "intel")]
    [InlineData("Realtek Semiconductor Corp.", "realtek")]
    public void Maker_is_normalised(string provider, string maker) => Assert.Equal(maker, MatchRules.Maker(provider));
}
