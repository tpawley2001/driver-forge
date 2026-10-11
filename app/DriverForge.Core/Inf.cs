using System.Globalization;
using System.Text;

namespace DriverForge.Core;

/// <summary>
/// An INF file read per Microsoft's "General Syntax Rules for INF Files"
/// (SPEC §4.3.2). Used to shortlist packages; Windows makes the real match.
/// </summary>
public sealed class InfFile
{
    readonly Dictionary<string, List<InfLine>> sections = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> strings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One line of a section: an optional key and comma-separated values, strings substituted.</summary>
    public sealed record InfLine(string? Key, IReadOnlyList<string> Values);

    public static InfFile Load(string path, int? lcid = null) => Parse(Decode(File.ReadAllBytes(path)), lcid);

    public static InfFile Parse(string text, int? lcid = null)
    {
        var inf = new InfFile();
        var raw = new Dictionary<string, List<(string? Key, List<string> Values)>>(StringComparer.OrdinalIgnoreCase);
        List<(string?, List<string>)>? current = null;
        foreach (var line in LogicalLines(text))
        {
            if (line.StartsWith('['))
            {
                int close = line.IndexOf(']');
                string name = (close > 0 ? line[1..close] : line[1..]).Trim();
                // A section may appear more than once: the contents merge.
                if (!raw.TryGetValue(name, out current)) raw[name] = current = [];
                continue;
            }
            current?.Add(SplitEntry(line));
        }

        // [Strings], then the localized [Strings.<LCID>] on top (LCID as 4 hex digits).
        int lang = lcid ?? CultureInfo.CurrentUICulture.LCID;
        foreach (var name in new[] { "Strings", $"Strings.{lang:x4}", $"Strings.{lang:X4}" }.Distinct(StringComparer.OrdinalIgnoreCase))
            if (raw.TryGetValue(name, out var s))
                foreach (var (k, v) in s)
                    if (k is not null) inf.strings[k] = v.Count > 0 ? v[0].Replace("%%", "%") : "";

        foreach (var (name, lines) in raw)
            inf.sections[name] = lines.Select(l => new InfLine(l.Key is null ? null : inf.Substitute(l.Key),
                l.Values.Select(inf.Substitute).ToList())).ToList();
        return inf;
    }

    public IReadOnlyList<InfLine> Section(string name) => sections.TryGetValue(name, out var l) ? l : [];
    public bool HasSection(string name) => sections.ContainsKey(name);

    /// <summary>A [Version] value ("Provider", "Class", "DriverVer", …).</summary>
    public string? Version(string key) => Section("Version").FirstOrDefault(l => string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase)) is { } l
        ? string.Join(",", l.Values) : null;

    /// <summary>DriverVer = mm/dd/yyyy[,w.x.y.z].</summary>
    public (DateOnly? Date, DriverVersion Version) DriverVer()
    {
        var line = Section("Version").FirstOrDefault(l => string.Equals(l.Key, "DriverVer", StringComparison.OrdinalIgnoreCase));
        if (line is null || line.Values.Count == 0) return (null, DriverVersion.Unknown);
        DateOnly? date = DateOnly.TryParseExact(line.Values[0].Trim(), ["MM/dd/yyyy", "M/d/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        return (date, line.Values.Count > 1 ? DriverVersion.Parse(line.Values[1]) : DriverVersion.Unknown);
    }

    /// <summary>
    /// The models entries that apply to this OS: per [Manufacturer] line, the
    /// models section with the best TargetOSVersion decoration (SPEC §4.3.3).
    /// </summary>
    public IReadOnlyList<InfModel> Models(OsInfo os)
    {
        var models = new List<InfModel>();
        foreach (var line in Section("Manufacturer"))
        {
            // "%Mfg% = Models, NTamd64, NTamd64.10.0...16299" or just "Models".
            var parts = line.Values;
            if (parts.Count == 0 || parts[0].Length == 0) continue;
            string baseName = parts[0];
            var decorations = parts.Skip(1).Where(p => p.Length > 0).ToList();
            string? chosen = TargetOs.Choose(decorations, os);
            string? sectionName = chosen is not null ? $"{baseName}.{chosen}"
                : decorations.Count == 0 || os.Architecture == "x86" ? baseName : null;
            if (sectionName is null) continue;
            foreach (var m in Section(sectionName))
            {
                if (m.Values.Count < 2) continue;     // description = install-section, hw-id [, compatible-id ...]
                var ids = m.Values.Skip(1).Select(v => v.Trim()).ToList();
                models.Add(new InfModel(m.Key ?? "", m.Values[0].Trim(), ids[0], ids.Skip(1).Where(i => i.Length > 0).ToList()));
            }
        }
        return models;
    }

    string Substitute(string s)
    {
        if (!s.Contains('%')) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '%') { sb.Append(s[i]); continue; }
            int end = s.IndexOf('%', i + 1);
            if (end < 0) { sb.Append(s, i, s.Length - i); break; }
            string token = s[(i + 1)..end];
            if (token.Length == 0) sb.Append('%');                      // %% is a literal %
            else sb.Append(strings.TryGetValue(token, out var v) ? v : s[i..(end + 1)]);
            i = end;
        }
        return sb.ToString();
    }

    /// <summary>UTF-16 LE (BOM or not), UTF-8 with BOM, else code page 1252.</summary>
    internal static string Decode(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
        if (b.Length >= 4 && b[1] == 0 && b[3] == 0 && b[0] != 0) return Encoding.Unicode.GetString(b);
        return Ansi.GetString(b);
    }

    static readonly Encoding Ansi = CodePage1252();

    static Encoding CodePage1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }

    /// <summary>Comments removed (';' outside quotes), '\' continuations joined, blank lines dropped.</summary>
    internal static IEnumerable<string> LogicalLines(string text)
    {
        var pending = new StringBuilder();
        foreach (var physical in text.Split('\n'))
        {
            string line = StripComment(physical.TrimEnd('\r')).TrimEnd();
            if (line.EndsWith('\\'))
            {
                pending.Append(line, 0, line.Length - 1);
                continue;
            }
            pending.Append(line);
            string full = pending.ToString().Trim();
            pending.Clear();
            if (full.Length > 0) yield return full;
        }
        if (pending.Length > 0 && pending.ToString().Trim() is { Length: > 0 } rest) yield return rest;
    }

    static string StripComment(string line)
    {
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ';' && !quoted) return line[..i];
        }
        return line;
    }

    /// <summary>key = v1, "v,2", v3 — quotes keep commas; "" inside quotes is a quote.</summary>
    internal static (string? Key, List<string> Values) SplitEntry(string line)
    {
        string? key = null;
        int eq = IndexOutsideQuotes(line, '=');
        string rest = line;
        if (eq >= 0)
        {
            key = Unquote(line[..eq].Trim());
            rest = line[(eq + 1)..];
        }
        var values = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < rest.Length; i++)
        {
            char c = rest[i];
            if (c == '"')
            {
                if (quoted && i + 1 < rest.Length && rest[i + 1] == '"') { sb.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == ',' && !quoted) { values.Add(sb.ToString().Trim()); sb.Clear(); }
            else sb.Append(c);
        }
        values.Add(sb.ToString().Trim());
        return (key, values);
    }

    static int IndexOutsideQuotes(string s, char c)
    {
        bool quoted = false;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '"') quoted = !quoted;
            else if (s[i] == c && !quoted) return i;
        }
        return -1;
    }

    static string Unquote(string s) => s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1].Replace("\"\"", "\"") : s;
}

/// <summary>One models-section entry: description = install-section, hardware ID [, compatible IDs].</summary>
public sealed record InfModel(string Description, string InstallSection, string HardwareId, IReadOnlyList<string> CompatibleIds);

/// <summary>
/// TargetOSVersion decorations, NT[Architecture][.[OSMajorVersion][.[OSMinorVersion][.[ProductType][.[SuiteMask][.[BuildNumber]]]]]]
/// (per "INF Manufacturer Section"; SPEC §4.3.3).
/// </summary>
public static class TargetOs
{
    public sealed record Decoration(string Text, string? Arch, int? Major, int? Minor, int? ProductType, int? SuiteMask, int? Build);

    public static Decoration? Parse(string text)
    {
        string t = text.Trim();
        if (!t.StartsWith("NT", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = t[2..].Split('.');
        string arch = parts[0].ToLowerInvariant();
        if (arch is not ("" or "x86" or "amd64" or "arm" or "arm64" or "ia64")) return null;
        int? Num(int i, bool hex = false)
        {
            if (i >= parts.Length || parts[i].Length == 0) return null;
            string p = parts[i];
            if (p.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return int.TryParse(p[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h : null;
            return int.TryParse(p, hex ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
        }
        return new(t, arch.Length == 0 ? null : arch, Num(1), Num(2), Num(3), Num(4, hex: true), Num(5));
    }

    /// <summary>The decoration Windows would use on this OS, or null when none applies.</summary>
    public static string? Choose(IEnumerable<string> decorations, OsInfo os)
    {
        var fits = decorations.Select(Parse).OfType<Decoration>().Where(d => Fits(d, os)).ToList();
        return fits
            .OrderByDescending(d => d.Major ?? 0).ThenByDescending(d => d.Minor ?? 0)
            .ThenByDescending(d => d.Major == os.Major && d.Minor == os.Minor ? d.Build ?? 0 : 0)
            .ThenByDescending(d => (d.ProductType is null ? 0 : 1) + (d.SuiteMask is null ? 0 : 1))
            .ThenByDescending(d => d.Arch is null ? 0 : 1)
            .FirstOrDefault()?.Text;
    }

    internal static bool Fits(Decoration d, OsInfo os)
    {
        if (d.Arch is not null && d.Arch != os.Architecture) return false;
        int major = d.Major ?? 0, minor = d.Minor ?? 0;
        if (major > os.Major || (major == os.Major && minor > os.Minor)) return false;
        // A build is compared only when major.minor equals the OS exactly.
        if (d.Build is { } b && major == os.Major && minor == os.Minor && b > os.Build) return false;
        if (d.ProductType is { } pt && pt != os.ProductType) return false;
        if (d.SuiteMask is { } sm && (sm & os.SuiteMask) != sm) return false;
        return true;
    }
}
