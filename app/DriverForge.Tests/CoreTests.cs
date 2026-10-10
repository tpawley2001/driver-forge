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
}
