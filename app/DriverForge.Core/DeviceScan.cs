using System.Runtime.InteropServices;
using System.Text;

namespace DriverForge.Core;

/// <summary>
/// Reads the PC's devices and their installed drivers through SetupAPI's
/// unified device property model (SPEC §3). Read-only: changes nothing.
/// </summary>
public static class DeviceScanner
{
    /// <summary>Every device; <paramref name="presentOnly"/> false adds ghost devices (SPEC §8.2).</summary>
    public static IReadOnlyList<Device> Scan(bool presentOnly = true, CancellationToken ct = default)
    {
        uint flags = Native.DIGCF_ALLCLASSES | (presentOnly ? Native.DIGCF_PRESENT : 0);
        nint set = Native.SetupDiGetClassDevsW(0, null, 0, flags);
        if (set == -1)
            throw new EngineException($"Windows wouldn't list the devices (SetupDiGetClassDevs error {Marshal.GetLastWin32Error()}).");
        try
        {
            var devices = new List<Device>();
            var info = new Native.SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<Native.SP_DEVINFO_DATA>() };
            for (uint i = 0; Native.SetupDiEnumDeviceInfo(set, i, ref info); i++)
            {
                ct.ThrowIfCancellationRequested();
                var d = Read(set, info);
                if (d is not null) devices.Add(d);
            }
            int err = Marshal.GetLastWin32Error();
            if (err != Native.ERROR_NO_MORE_ITEMS)
                throw new EngineException($"Windows stopped listing devices part way (SetupDiEnumDeviceInfo error {err}).");
            return devices;
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(set);
        }
    }

    static Device? Read(nint set, Native.SP_DEVINFO_DATA info)
    {
        string? id = InstanceId(set, info);
        if (id is null) return null;

        string? Str(DevKey k) => Property(set, info, k) is { Type: Native.DEVPROP_TYPE_STRING } p ? PropertyData.String(p.Data) : null;
        IReadOnlyList<string> List(DevKey k) => Property(set, info, k) is { Type: Native.DEVPROP_TYPE_STRING_LIST } p ? PropertyData.StringList(p.Data) : [];

        string desc = Str(DevKey.DeviceDesc) ?? "";
        string cls = Str(DevKey.Class) ?? "";
        Guid classGuid = Property(set, info, DevKey.ClassGuid) is { Type: Native.DEVPROP_TYPE_GUID, Data.Length: 16 } g ? new Guid(g.Data) : info.ClassGuid;

        InstalledDriver? installed = null;
        string? inf = Str(DevKey.DriverInfPath);
        if (!string.IsNullOrEmpty(inf))
        {
            DateOnly? date = Property(set, info, DevKey.DriverDate) is { Type: Native.DEVPROP_TYPE_FILETIME, Data.Length: 8 } f
                ? PropertyData.FileTimeDate(BitConverter.ToInt64(f.Data)) : null;
            uint? rank = Property(set, info, DevKey.DriverRank) is { Type: Native.DEVPROP_TYPE_UINT32, Data.Length: 4 } r
                ? BitConverter.ToUInt32(r.Data) : null;
            installed = new(
                Str(DevKey.DriverDesc) ?? "",
                Str(DevKey.DriverProvider) ?? "",
                DriverVersion.Parse(Str(DevKey.DriverVersion)),
                date,
                inf,
                Str(DevKey.MatchingDeviceId) ?? "",
                rank,
                Str(DevKey.DriverInfSection) ?? "");
        }

        return new Device(
            id,
            Str(DevKey.Name) ?? desc,
            desc,
            Str(DevKey.Manufacturer) ?? "",
            cls,
            List(DevKey.HardwareIds),
            List(DevKey.CompatibleIds),
            ProblemCode(info.DevInst),
            installed,
            classGuid);
    }

    /// <summary>The CM_PROB_* code, or 0 when the device has no problem.</summary>
    static int ProblemCode(uint devInst)
    {
        if (Native.CM_Get_DevNode_Status(out uint status, out uint problem, devInst, 0) != Native.CR_SUCCESS) return 0;
        return (status & Native.DN_HAS_PROBLEM) != 0 ? (int)problem : 0;
    }

    static string? InstanceId(nint set, Native.SP_DEVINFO_DATA info)
    {
        var buf = new char[256];
        if (Native.SetupDiGetDeviceInstanceIdW(set, ref info, buf, buf.Length, out int needed))
            return PropertyData.String(buf);
        if (Marshal.GetLastWin32Error() != Native.ERROR_INSUFFICIENT_BUFFER || needed <= 0) return null;
        buf = new char[needed];
        return Native.SetupDiGetDeviceInstanceIdW(set, ref info, buf, buf.Length, out _) ? PropertyData.String(buf) : null;
    }

    readonly record struct Prop(uint Type, byte[] Data);

    static Prop? Property(nint set, Native.SP_DEVINFO_DATA info, DevKey key)
    {
        var k = key.ToNative();
        Native.SetupDiGetDevicePropertyW(set, ref info, ref k, out _, null, 0, out uint needed, 0);
        if (Marshal.GetLastWin32Error() != Native.ERROR_INSUFFICIENT_BUFFER || needed == 0) return null;
        var buf = new byte[needed];
        return Native.SetupDiGetDevicePropertyW(set, ref info, ref k, out uint type, buf, needed, out _, 0)
            ? new Prop(type, buf) : null;
    }

    /// <summary>The OS facts INF decorations are matched against (SPEC §3.2).</summary>
    public static OsInfo Os()
    {
        var v = new Native.OSVERSIONINFOEXW { dwOSVersionInfoSize = (uint)Marshal.SizeOf<Native.OSVERSIONINFOEXW>() };
        if (Native.RtlGetVersion(ref v) != 0)
            throw new EngineException("Windows wouldn't report its version (RtlGetVersion).");
        string arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            var a => a.ToString().ToLowerInvariant(),
        };
        return new(arch, (int)v.dwMajorVersion, (int)v.dwMinorVersion, (int)v.dwBuildNumber, v.wProductType, v.wSuiteMask);
    }
}

/// <summary>Decoding of raw DEVPROPTYPE buffers. Separate so tests can feed it bytes.</summary>
internal static class PropertyData
{
    /// <summary>DEVPROP_TYPE_STRING: UTF-16, NUL-terminated.</summary>
    public static string String(byte[] data) => Cut(Encoding.Unicode.GetString(data, 0, data.Length & ~1));

    public static string String(char[] chars) => Cut(new string(chars));

    /// <summary>DEVPROP_TYPE_STRING_LIST: UTF-16 strings, each NUL-terminated, ending in an empty one. Order kept.</summary>
    public static IReadOnlyList<string> StringList(byte[] data)
    {
        var all = Encoding.Unicode.GetString(data, 0, data.Length & ~1);
        var list = new List<string>();
        foreach (var s in all.Split('\0'))
        {
            if (s.Length == 0) break;
            list.Add(s);
        }
        return list;
    }

    /// <summary>
    /// DEVPROP_TYPE_FILETIME driver dates. Windows stores the INF's DriverVer
    /// date as midnight UTC, so the UTC calendar date is the INF's date.
    /// </summary>
    public static DateOnly? FileTimeDate(long fileTime)
    {
        if (fileTime <= 0) return null;
        try { return DateOnly.FromDateTime(DateTime.FromFileTimeUtc(fileTime)); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    static string Cut(string s)
    {
        int nul = s.IndexOf('\0');
        return nul >= 0 ? s[..nul] : s;
    }
}

/// <summary>DEVPROPKEYs from devpkey.h (Microsoft's documented values).</summary>
internal readonly record struct DevKey(Guid Fmtid, uint Pid)
{
    static readonly Guid NameFmt = new("b725f130-47ef-101a-a5f1-02608c9eebac");
    static readonly Guid DeviceFmt = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    static readonly Guid DriverFmt = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6");

    public static readonly DevKey Name = new(NameFmt, 10);
    public static readonly DevKey DeviceDesc = new(DeviceFmt, 2);
    public static readonly DevKey HardwareIds = new(DeviceFmt, 3);
    public static readonly DevKey CompatibleIds = new(DeviceFmt, 4);
    public static readonly DevKey Class = new(DeviceFmt, 9);
    public static readonly DevKey ClassGuid = new(DeviceFmt, 10);
    public static readonly DevKey Manufacturer = new(DeviceFmt, 13);
    public static readonly DevKey DriverDate = new(DriverFmt, 2);
    public static readonly DevKey DriverVersion = new(DriverFmt, 3);
    public static readonly DevKey DriverDesc = new(DriverFmt, 4);
    public static readonly DevKey DriverInfPath = new(DriverFmt, 5);
    public static readonly DevKey DriverInfSection = new(DriverFmt, 6);
    public static readonly DevKey MatchingDeviceId = new(DriverFmt, 8);
    public static readonly DevKey DriverProvider = new(DriverFmt, 9);
    public static readonly DevKey DriverRank = new(DriverFmt, 14);

    public Native.DEVPROPKEY ToNative() => new() { fmtid = Fmtid, pid = Pid };
}

internal static partial class Native
{
    public const uint DIGCF_PRESENT = 0x2, DIGCF_ALLCLASSES = 0x4;
    public const int ERROR_INSUFFICIENT_BUFFER = 122, ERROR_NO_MORE_ITEMS = 259;
    public const uint DEVPROP_TYPE_UINT32 = 0x7, DEVPROP_TYPE_GUID = 0xD, DEVPROP_TYPE_FILETIME = 0x10,
        DEVPROP_TYPE_STRING = 0x12, DEVPROP_TYPE_STRING_LIST = 0x2012;
    public const uint CR_SUCCESS = 0, DN_HAS_PROBLEM = 0x400;

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct OSVERSIONINFOEXW
    {
        public uint dwOSVersionInfoSize, dwMajorVersion, dwMinorVersion, dwBuildNumber, dwPlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szCSDVersion;
        public ushort wServicePackMajor, wServicePackMinor, wSuiteMask;
        public byte wProductType, wReserved;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern nint SetupDiGetClassDevsW(nint classGuid, string? enumerator, nint hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInfo(nint set, uint index, ref SP_DEVINFO_DATA info);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiGetDeviceInstanceIdW(nint set, ref SP_DEVINFO_DATA info, char[] id, int size, out int required);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiGetDevicePropertyW(nint set, ref SP_DEVINFO_DATA info, ref DEVPROPKEY key,
        out uint type, byte[]? buffer, uint size, out uint required, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(nint set);

    [DllImport("cfgmgr32.dll")]
    public static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    [DllImport("ntdll.dll")]
    public static extern int RtlGetVersion(ref OSVERSIONINFOEXW info);
}
