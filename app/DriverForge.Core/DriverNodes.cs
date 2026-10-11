using System.Runtime.InteropServices;

namespace DriverForge.Core;

/// <summary>A driver Windows considers for a device, with Windows' own rank (SPEC §4.4, §5.1).</summary>
public sealed record DriverNode(
    string Description,
    string Manufacturer,
    string Provider,
    DriverVersion Version,
    DateOnly? Date,
    string Inf,
    string Section,
    string InfHardwareId,
    IReadOnlyList<string> InfCompatibleIds,
    uint Rank,
    bool Signed,
    bool IsInstalled);

/// <summary>
/// Asks SetupAPI which drivers match a device and how Windows ranks them:
/// SetupDiBuildDriverInfoList, then SP_DRVINSTALL_PARAMS.Rank per driver node.
/// Read-only.
/// </summary>
public static class DriverNodes
{
    /// <summary>Compatible drivers already in this PC's driver store.</summary>
    public static IReadOnlyList<DriverNode> Staged(string instanceId) => Build(instanceId, null);

    /// <summary>The driver nodes one INF has for the device (DI_ENUMSINGLEINF); empty when it doesn't match.</summary>
    public static IReadOnlyList<DriverNode> FromInf(string instanceId, string infPath) => Build(instanceId, infPath);

    static IReadOnlyList<DriverNode> Build(string instanceId, string? inf)
    {
        using var dev = DeviceHandle.Open(instanceId);
        if (dev is null) return [];
        var info = dev.Info;
        if (inf is not null && !dev.SetSingleInf(inf)) return [];
        if (!Native.SetupDiBuildDriverInfoList(dev.Set, ref info, Native.SPDIT_COMPATDRIVER)) return [];
        try
        {
            var nodes = new List<DriverNode>();
            var drv = new Native.SP_DRVINFO_DATA_V2 { cbSize = (uint)Marshal.SizeOf<Native.SP_DRVINFO_DATA_V2>() };
            for (uint i = 0; Native.SetupDiEnumDriverInfoW(dev.Set, ref info, Native.SPDIT_COMPATDRIVER, i, ref drv); i++)
            {
                var p = new Native.SP_DRVINSTALL_PARAMS { cbSize = (uint)Marshal.SizeOf<Native.SP_DRVINSTALL_PARAMS>() };
                if (!Native.SetupDiGetDriverInstallParamsW(dev.Set, ref info, ref drv, ref p)) continue;
                if ((p.Flags & Native.DNF_BAD_DRIVER) != 0) continue;
                var detail = Detail(dev.Set, ref info, ref drv);
                if (detail is null) continue;
                var (section, infFile, hwid, compat) = detail.Value;
                nodes.Add(new DriverNode(drv.Description, drv.MfgName, drv.ProviderName,
                    Version(drv.DriverVersion), PropertyData.FileTimeDate(drv.DriverDate),
                    infFile, section, hwid, compat, p.Rank,
                    Signed: (p.Flags & (Native.DNF_INF_IS_SIGNED | Native.DNF_AUTHENTICODE_SIGNED)) != 0,
                    IsInstalled: (p.Flags & Native.DNF_INSTALLEDDRIVER) != 0));
            }
            return nodes;
        }
        finally
        {
            Native.SetupDiDestroyDriverInfoList(dev.Set, ref info, Native.SPDIT_COMPATDRIVER);
        }
    }

    /// <summary>DriverVersion DWORDLONG: four 16-bit parts, most significant first.</summary>
    internal static DriverVersion Version(ulong v) =>
        new((ushort)(v >> 48), (ushort)(v >> 32), (ushort)(v >> 16), (ushort)v);

    static (string Section, string Inf, string HardwareId, IReadOnlyList<string> CompatibleIds)? Detail(
        nint set, ref Native.SP_DEVINFO_DATA info, ref Native.SP_DRVINFO_DATA_V2 drv)
    {
        Native.SetupDiGetDriverInfoDetailW(set, ref info, ref drv, null, 0, out uint needed);
        if (needed < Native.DetailFixedSize) return null;
        var buf = new byte[needed];
        BitConverter.TryWriteBytes(buf, (uint)Native.DetailFixedSize);
        if (!Native.SetupDiGetDriverInfoDetailW(set, ref info, ref drv, buf, needed, out _)) return null;
        return ParseDetail(buf);
    }

    /// <summary>
    /// SP_DRVINFO_DETAIL_DATA_W (64-bit layout): HardwareID is a multi-sz at the
    /// end; CompatIDsOffset/Length count characters from its start.
    /// </summary>
    internal static (string, string, string, IReadOnlyList<string>) ParseDetail(byte[] buf)
    {
        uint compatOffset = BitConverter.ToUInt32(buf, 12), compatLength = BitConverter.ToUInt32(buf, 16);
        string Fixed(int offset, int chars) => PropertyData.String(buf[offset..(offset + chars * 2)]);
        string section = Fixed(32, 256), inf = Fixed(544, 260);
        var ids = buf[Native.DetailIdsOffset..];
        string hwid = compatOffset > 1 || compatOffset == 0 ? PropertyData.String(ids) : "";
        IReadOnlyList<string> compat = [];
        if (compatOffset > 0 && compatLength > 0)
        {
            int start = (int)compatOffset * 2, end = Math.Min(ids.Length, start + (int)compatLength * 2);
            if (start < end) compat = PropertyData.StringList(ids[start..end]);
        }
        return (section, inf, hwid, compat);
    }
}

/// <summary>A device info set holding exactly one device, for driver-node calls.</summary>
internal sealed class DeviceHandle : IDisposable
{
    public nint Set { get; }
    public Native.SP_DEVINFO_DATA Info;

    DeviceHandle(nint set, Native.SP_DEVINFO_DATA info) { Set = set; Info = info; }

    public static DeviceHandle? Open(string instanceId)
    {
        nint set = Native.SetupDiCreateDeviceInfoList(0, 0);
        if (set == -1) return null;
        var info = new Native.SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<Native.SP_DEVINFO_DATA>() };
        if (!Native.SetupDiOpenDeviceInfoW(set, instanceId, 0, 0, ref info))
        {
            Native.SetupDiDestroyDeviceInfoList(set);
            return null;
        }
        return new(set, info);
    }

    /// <summary>Limit driver searches to one INF (DI_ENUMSINGLEINF + DriverPath).</summary>
    public bool SetSingleInf(string infPath)
    {
        var p = new Native.SP_DEVINSTALL_PARAMS_W { cbSize = (uint)Marshal.SizeOf<Native.SP_DEVINSTALL_PARAMS_W>() };
        if (!Native.SetupDiGetDeviceInstallParamsW(Set, ref Info, ref p)) return false;
        p.Flags |= Native.DI_ENUMSINGLEINF;
        p.DriverPath = infPath;
        return Native.SetupDiSetDeviceInstallParamsW(Set, ref Info, ref p);
    }

    public void Dispose() => Native.SetupDiDestroyDeviceInfoList(Set);
}

/// <summary>SetupAPI driver-node declarations (setupapi.h; 64-bit layouts).</summary>
internal static partial class Native
{
    public const uint SPDIT_COMPATDRIVER = 2;
    public const uint DI_ENUMSINGLEINF = 0x00010000;
    public const uint DNF_BAD_DRIVER = 0x00000800, DNF_INF_IS_SIGNED = 0x00002000,
        DNF_AUTHENTICODE_SIGNED = 0x00020000, DNF_INSTALLEDDRIVER = 0x00040000;

    // SP_DRVINFO_DETAIL_DATA_W: cbSize, InfDate, CompatIDsOffset, CompatIDsLength,
    // Reserved, SectionName[256], InfFileName[260], DrvDescription[256], HardwareID[1].
    public const int DetailIdsOffset = 1576, DetailFixedSize = 1584;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SP_DRVINFO_DATA_V2
    {
        public uint cbSize;
        public uint DriverType;
        public nint Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string MfgName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProviderName;
        public long DriverDate;
        public ulong DriverVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DRVINSTALL_PARAMS
    {
        public uint cbSize;
        public uint Rank;
        public uint Flags;
        public nint PrivateData;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SP_DEVINSTALL_PARAMS_W
    {
        public uint cbSize;
        public uint Flags;
        public uint FlagsEx;
        public nint hwndParent;
        public nint InstallMsgHandler;
        public nint InstallMsgHandlerContext;
        public nint FileQueue;
        public nint ClassInstallReserved;
        public uint Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DriverPath;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern nint SetupDiCreateDeviceInfoList(nint classGuid, nint hwndParent);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiOpenDeviceInfoW(nint set, string instanceId, nint hwndParent, uint flags, ref SP_DEVINFO_DATA info);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiGetDeviceInstallParamsW(nint set, ref SP_DEVINFO_DATA info, ref SP_DEVINSTALL_PARAMS_W p);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiSetDeviceInstallParamsW(nint set, ref SP_DEVINFO_DATA info, ref SP_DEVINSTALL_PARAMS_W p);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiBuildDriverInfoList(nint set, ref SP_DEVINFO_DATA info, uint driverType);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDriverInfoList(nint set, ref SP_DEVINFO_DATA info, uint driverType);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiEnumDriverInfoW(nint set, ref SP_DEVINFO_DATA info, uint driverType, uint index, ref SP_DRVINFO_DATA_V2 drv);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiGetDriverInstallParamsW(nint set, ref SP_DEVINFO_DATA info, ref SP_DRVINFO_DATA_V2 drv, ref SP_DRVINSTALL_PARAMS p);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiGetDriverInfoDetailW(nint set, ref SP_DEVINFO_DATA info, ref SP_DRVINFO_DATA_V2 drv,
        byte[]? detail, uint size, out uint required);
}
