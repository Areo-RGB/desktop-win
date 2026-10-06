using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FluentDesktop.Helpers;

/// <summary>Static functions for x:Bind function bindings.</summary>
public static class Bind
{
    public static bool Not(bool value) => !value;

    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfText(string? value) => string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfNotNull(object? value) => value is null ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfNull(object? value) => value is null ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisibleIfAny(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisibleIfNone(int count) => count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public static bool And(bool a, bool b) => a && b;

    public static Visibility AndNotVisible(bool a, bool b) => Visible(a && !b);

    public static Visibility NoneVisible(bool a, bool b) => Visible(!a && !b);

    public static Visibility VisibleIfEqual(int value, int expected) => Visible(value == expected);

    public static bool AndNot(bool a, bool b) => a && !b;

    public static double Percent(double value) => Math.Clamp(value, 0, 100);

    public static Brush Solid(byte r, byte g, byte b) => new SolidColorBrush(ColorHelper.FromArgb(255, r, g, b));

    public static Brush Success { get; } = Solid(0x0F, 0x9D, 0x58);
    public static Brush Warning { get; } = Solid(0xF5, 0x9E, 0x0B);
    public static Brush Danger { get; } = Solid(0xE5, 0x48, 0x4D);
    public static Brush Purple { get; } = Solid(0x8B, 0x5C, 0xF6);
    public static Brush Neutral { get; } = Solid(0x8A, 0x8A, 0x8A);
}
