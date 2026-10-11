using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DriverForge.Core;

namespace DriverForge.Converters;

public sealed class InverseBoolToVisibility : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class InverseBool : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is not true;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is not true;
}

/// <summary>0 is visible (an "empty" message), anything else collapsed.</summary>
public sealed class ZeroToVisibility : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class NullToVisibility : IValueConverter
{
    /// <summary>False: null is collapsed. True: null is visible.</summary>
    public bool Invert { get; set; }
    public object Convert(object? value, Type t, object p, CultureInfo c)
    {
        bool present = value is not null && value is not string { Length: 0 };
        return present ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Badge colours per device state, from the mockup.</summary>
public sealed class StateBrush : IValueConverter
{
    public string Part { get; set; } = "Background";

    public object Convert(object? value, Type t, object p, CultureInfo c)
    {
        string key = value switch
        {
            DeviceState.Missing or DeviceState.NoDriverFound => "Missing",
            DeviceState.Update => "Update",
            DeviceState.UpToDate => "Ok",
            _ => "Skip",
        };
        return Application.Current.TryFindResource(key + (Part == "Background" ? "Bg" : "Fg")) as Brush ?? Brushes.Transparent;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
