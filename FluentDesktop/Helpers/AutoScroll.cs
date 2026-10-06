using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentDesktop.Helpers;

/// <summary>
/// Attached property: keeps a ListView pinned to its last item while new
/// lines stream in, unless the user scrolled up.
/// </summary>
public static class AutoScroll
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(AutoScroll), new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListView list || e.NewValue is not true) return;
        INotifyCollectionChanged? source = null;
        var scheduled = false;

        void Handler(object? s, NotifyCollectionChangedEventArgs args)
        {
            if (scheduled || list.Items.Count == 0) return;
            var viewer = FindScrollViewer(list);
            if (viewer != null && viewer.ScrollableHeight - viewer.VerticalOffset > 72) return;
            scheduled = true;
            // One jump per dispatcher turn instead of per line.
            list.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                scheduled = false;
                if (list.Items.Count > 0) list.ScrollIntoView(list.Items[list.Items.Count - 1]);
            });
        }

        void Attach()
        {
            if (source != null) source.CollectionChanged -= Handler;
            source = list.ItemsSource as INotifyCollectionChanged;
            if (source != null) source.CollectionChanged += Handler;
        }

        list.Loaded += (_, _) => Attach();
        list.Unloaded += (_, _) =>
        {
            if (source != null) source.CollectionChanged -= Handler;
            source = null;
        };
        list.RegisterPropertyChangedCallback(ItemsControl.ItemsSourceProperty, (_, _) => Attach());
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindScrollViewer(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        }
        return null;
    }
}
