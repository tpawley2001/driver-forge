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
