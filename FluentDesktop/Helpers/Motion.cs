using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace FluentDesktop.Helpers;

/// <summary>Attached motion for ItemsRepeater, which has no built-in item transitions.</summary>
public static class Motion
{
    private const int MaxStaggered = 12;
    private static readonly UISettings Settings = new();

    public static readonly DependencyProperty StaggerInProperty =
        DependencyProperty.RegisterAttached("StaggerIn", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnStaggerInChanged));

    public static bool GetStaggerIn(DependencyObject o) => (bool)o.GetValue(StaggerInProperty);
    public static void SetStaggerIn(DependencyObject o, bool value) => o.SetValue(StaggerInProperty, value);

    private static void OnStaggerInChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsRepeater repeater) return;
        repeater.ElementPrepared -= OnElementPrepared;
        if ((bool)e.NewValue) repeater.ElementPrepared += OnElementPrepared;
    }

    private static void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (!Settings.AnimationsEnabled) return;
        var visual = ElementCompositionPreview.GetElementVisual(args.Element);
        var compositor = visual.Compositor;
        var delay = TimeSpan.FromMilliseconds(Math.Min(args.Index, MaxStaggered) * 30);
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0f, 0f), new Vector2(0f, 1f));

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, easing);
        fade.Duration = TimeSpan.FromMilliseconds(250);
        fade.DelayTime = delay;
        fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

        var slide = compositor.CreateScalarKeyFrameAnimation();
        slide.InsertKeyFrame(0, 12);
        slide.InsertKeyFrame(1, 0, easing);
        slide.Duration = TimeSpan.FromMilliseconds(300);
        slide.DelayTime = delay;
        slide.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

        visual.StartAnimation("Opacity", fade);
        visual.StartAnimation("Offset.Y", slide);
    }
}
