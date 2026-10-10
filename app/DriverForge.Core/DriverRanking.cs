using System.Globalization;

namespace DriverForge.Core;

/// <summary>
/// A DriverVer version: four 16-bit parts compared numerically (SPEC §5.2).
/// Missing parts are 0; an unparsable version sorts below every real one.
/// </summary>
public readonly record struct DriverVersion(ushort A, ushort B, ushort C, ushort D) : IComparable<DriverVersion>
{
    public bool IsKnown { get; init; } = true;

    public static readonly DriverVersion Unknown = new(0, 0, 0, 0) { IsKnown = false };

    public static DriverVersion Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Unknown;
        var parts = s.Trim().Split('.');
        if (parts.Length > 4) return Unknown;
        var n = new ushort[4];
        for (int i = 0; i < parts.Length; i++)
            if (!ushort.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i]))
                return Unknown;
        return new(n[0], n[1], n[2], n[3]);
    }

    public int CompareTo(DriverVersion o)
    {
        if (IsKnown != o.IsKnown) return IsKnown ? 1 : -1;
        int c = A.CompareTo(o.A);
        if (c == 0) c = B.CompareTo(o.B);
        if (c == 0) c = C.CompareTo(o.C);
        if (c == 0) c = D.CompareTo(o.D);
        return c;
    }

    public override string ToString() => IsKnown ? $"{A}.{B}.{C}.{D}" : "";
}

public enum SignatureClass
{
    Trusted,            // WHQL, inbox, Windows SE, valid Authenticode
    UnsignedNtSection,  // no valid signature, DDInstall has a .nt extension
    Unsigned,           // no valid signature
    Unknown,
}

/// <summary>
/// Driver rank 0xSSGGTHHH, lower is better (SPEC §5.1, from Microsoft's
/// "How Windows Ranks Driver Packages"). Windows' own rank from SetupAPI is
/// the authority; this computes it for shortlisting and tests.
/// </summary>
public static class DriverRank
{
    public const byte DefaultFeatureScore = 0xFF;

    // Microsoft documents the order of the signature classes, not their
    // values; these keep that order and use 0xFF for unknown as documented.
    public static byte SignatureScore(SignatureClass s) => s switch
    {
        SignatureClass.Trusted => 0x00,
        SignatureClass.UnsignedNtSection => 0x80,
        SignatureClass.Unsigned => 0xC0,
        _ => 0xFF,
    };

    /// <summary>
    /// The identifier score. <paramref name="devicePos"/> is the matching ID's
    /// position in the device's hardware-ID or compatible-ID list;
    /// <paramref name="infPos"/> is its position in the INF Models entry,
    /// where 0 is the entry's hardware ID and 1+ are its compatible IDs.
    /// </summary>
    public static uint IdentifierScore(bool deviceHardwareId, int devicePos, int infPos)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(devicePos);
        ArgumentOutOfRangeException.ThrowIfNegative(infPos);
        bool infHardwareId = infPos == 0;
        (uint type, long position) = (deviceHardwareId, infHardwareId) switch
        {
            (true, true) => (0x0000u, devicePos),
            (true, false) => (0x1000u, devicePos),
            (false, true) => (0x2000u, devicePos),
            // j + k*0x100, k = position among the entry's compatible IDs
            (false, false) => (0x3000u, devicePos + (long)(infPos - 1) * 0x100),
        };
        return type | (uint)Math.Min(position, 0x0FFF);
    }

    public static uint Compute(SignatureClass signature, byte featureScore, uint identifierScore) =>
        ((uint)SignatureScore(signature) << 24) | ((uint)featureScore << 16) | (identifierScore & 0xFFFF);
}

/// <summary>
/// Windows' choice among matching drivers (SPEC §5.2): lowest rank, then
/// newest date, then highest version. Sorting with it puts the best first.
/// </summary>
public sealed class DriverComparer : IComparer<DriverOffer>
{
    public static readonly DriverComparer BestFirst = new();

    public int Compare(DriverOffer? x, DriverOffer? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return 1;
        if (y is null) return -1;
        return Compare(x.Rank, x.Date, x.Version, y.Rank, y.Date, y.Version);
    }

    /// <summary>Negative when the first driver is the better one.</summary>
    public static int Compare(uint rankX, DateOnly? dateX, DriverVersion verX, uint rankY, DateOnly? dateY, DriverVersion verY)
    {
        int c = rankX.CompareTo(rankY);
        if (c != 0) return c;
        c = Nullable.Compare(dateY, dateX);     // newer first
        if (c != 0) return c;
        return verY.CompareTo(verX);            // higher first
    }
}
