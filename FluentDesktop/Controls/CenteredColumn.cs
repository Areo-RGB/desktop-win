using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace FluentDesktop.Controls;

/// <summary>
/// Centers its content in a column of at most <see cref="MaxContentWidth"/>.
/// Replaces <c>MaxWidth</c> + <c>HorizontalAlignment="Stretch"</c>: WinUI centers
/// such an element by its desired width, not its arranged width, so content that
/// "wants" less than the max drifts right (the gap grows with the window).
/// </summary>
public sealed partial class CenteredColumn : Panel
{
    public static readonly DependencyProperty MaxContentWidthProperty = DependencyProperty.Register(
        nameof(MaxContentWidth), typeof(double), typeof(CenteredColumn),
        new PropertyMetadata(double.PositiveInfinity, (d, _) => ((CenteredColumn)d).InvalidateMeasure()));

    public double MaxContentWidth
    {
        get => (double)GetValue(MaxContentWidthProperty);
        set => SetValue(MaxContentWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = Math.Min(availableSize.Width, MaxContentWidth);
        double desiredWidth = 0, desiredHeight = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(width, availableSize.Height));
            desiredWidth = Math.Max(desiredWidth, child.DesiredSize.Width);
            desiredHeight = Math.Max(desiredHeight, child.DesiredSize.Height);
        }
        // Claim the whole available width so the column can be centered in it.
        return new Size(double.IsInfinity(availableSize.Width) ? desiredWidth : availableSize.Width, desiredHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = Math.Min(finalSize.Width, MaxContentWidth);
        var x = Math.Max(0, (finalSize.Width - width) / 2);
        foreach (var child in Children)
        {
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
        }
        return finalSize;
    }
}
