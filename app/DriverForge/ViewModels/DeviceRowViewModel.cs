using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DriverForge.Core;
using Wpf.Ui.Controls;

namespace DriverForge.ViewModels;

public sealed partial class CandidateViewModel(DriverOffer offer, bool best, DeviceRowViewModel owner) : ObservableObject
{
    public DriverOffer Offer { get; } = offer;
    public bool IsBest { get; } = best;
    public string Version => Offer.Version.IsKnown ? Offer.Version.ToString() : "Unknown version";
    public string Date => Format.Date(Offer.Date);
    public string Meta => string.Join(" · ", new[]
    {
        Offer.Match == MatchKind.HardwareId ? "Exact hardware ID" : "Compatible ID",
        Offer.Signed ? "Signed" : "Unsigned",
        Compare(owner.Device.Installed),
        Offer.Source,
        Offer.IsLocal ? null : Format.Size(Offer.DownloadBytes),
    }.Where(s => !string.IsNullOrEmpty(s)));

    string? Compare(InstalledDriver? installed) =>
        installed?.Date is not { } cur || Offer.Date is not { } d ? null
        : d > cur ? "Newer than installed" : d < cur ? "Older than installed" : "Same date as installed";

    [ObservableProperty] private bool isChosen;

    partial void OnIsChosenChanged(bool value)
    {
        if (value) owner.Choose(this);
    }
}

public sealed partial class DeviceRowViewModel : ObservableObject
{
    public Assessment Assessment { get; }
    public Device Device => Assessment.Device;
    public DeviceState State => Assessment.State;
    public ObservableCollection<CandidateViewModel> Candidates { get; }

    public DeviceRowViewModel(Assessment assessment, Action selectionChanged)
    {
        Assessment = assessment;
        this.selectionChanged = selectionChanged;
        isChecked = assessment.IsActionable;
        Candidates = new(assessment.Offers.Select(c => new CandidateViewModel(c, c == assessment.Recommended, this)));
        // Choosing the recommended one marks it; the row is already checked.
        Candidates.FirstOrDefault(c => c.IsBest)?.IsChosen = true;
    }

    readonly Action selectionChanged;

    [ObservableProperty] private bool isChecked;
    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private CandidateViewModel? chosen;

    partial void OnIsCheckedChanged(bool value) => selectionChanged();

    internal void Choose(CandidateViewModel c)
    {
        foreach (var other in Candidates)
            if (other != c && other.IsChosen) other.IsChosen = false;
        Chosen = c;
        OnPropertyChanged(nameof(NextVersion));
        OnPropertyChanged(nameof(Dates));
        OnPropertyChanged(nameof(CanInstall));
        // Picking a driver by hand is a request to install it.
        if (!IsChecked) IsChecked = true; else selectionChanged();
    }

    public bool CanInstall => Chosen is not null;

    public string Name => Device.DisplayName;
    public string HardwareId => Device.PrimaryHardwareId;
    public string InstanceId => Device.InstanceId;
    public string ClassName => string.IsNullOrEmpty(Device.Class) ? "Unknown class" : Device.Class;

    public string CurrentVersion => Device.Installed is { } d ? (d.Version.IsKnown ? d.Version.ToString() : "Unknown") : "No driver";
    public string NextVersion => Chosen?.Version ?? State switch
    {
        DeviceState.UpToDate => "Best available",
        DeviceState.NoDriverFound => "Nothing matches",
        _ => "—",
    };
    public bool HasNext => Chosen is not null;

    public string Dates
    {
        get
        {
            string cur = Format.Date(Device.Installed?.Date);
            if (Chosen is null) return cur.Length > 0 ? $"Installed {cur}" : "";
            return cur.Length > 0 ? $"{cur}  →  {Chosen.Date}" : $"Available {Chosen.Date}";
        }
    }

    public string Badge => State switch
    {
        DeviceState.Missing => "Missing",
        DeviceState.Update => "Update",
        DeviceState.NoDriverFound => "No driver found",
        DeviceState.UpToDate => "Up to date",
        _ => "Skipped",
    };

    public string? Reason => Assessment.Reason;

    public string InstalledLine => Device.Installed is { } d
        ? string.Join(" · ", new[] { d.Provider, d.Version.ToString(), Format.Date(d.Date), d.Inf }.Where(s => !string.IsNullOrEmpty(s)))
            + (Device.NeedsDriver ? " · not working" : "")
        : "No driver installed.";

    public SymbolRegular Icon => DeviceIcon.For(Device);

    public string SearchUrl => "https://www.catalog.update.microsoft.com/Search.aspx?q=" + Uri.EscapeDataString(HardwareId);
}

static class Format
{
    public static string Date(DateOnly? d) => d?.ToString("yyyy-MM-dd") ?? "";

    public static string? Size(long? bytes) => bytes switch
    {
        null => null,
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB download",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB download",
    };
}

static class DeviceIcon
{
    public static SymbolRegular For(Device d)
    {
        string id = d.InstanceId.ToUpperInvariant();
        string cls = d.Class.ToLowerInvariant();
        string ids = string.Join(" ", d.CompatibleIds).ToUpperInvariant();
        if (id.StartsWith("HDAUDIO") || cls.Contains("sound") || cls.Contains("audio") || ids.Contains("CC_04")) return SymbolRegular.Speaker224;
        if (id.StartsWith("BTH") || cls.Contains("bluetooth")) return SymbolRegular.Bluetooth24;
        if (cls.Contains("network") || ids.Contains("CC_02")) return SymbolRegular.Wifi124;
        if (cls.Contains("display") || cls.Contains("monitor") || ids.Contains("CC_03")) return SymbolRegular.Desktop24;
        if (cls.Contains("disk") || cls.Contains("storage") || cls.Contains("ide") || ids.Contains("CC_01")) return SymbolRegular.HardDrive24;
        if (cls.Contains("keyboard")) return SymbolRegular.Keyboard24;
        if (cls.Contains("print")) return SymbolRegular.Print24;
        if (cls.Contains("camera") || cls.Contains("imaging")) return SymbolRegular.Camera24;
        if (cls.Contains("batter")) return SymbolRegular.Battery024;
        if (id.StartsWith("USB") || cls.Contains("usb")) return SymbolRegular.PlugConnected24;
        return SymbolRegular.DeveloperBoard24;
    }
}
