using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentDesktop.Helpers;

public static class Dialogs
{
    public static XamlRoot Root => App.MainWindow.Content.XamlRoot;

    public static ContentDialog Create(string title, object content, string primary, string close = "Cancel", bool danger = false)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root,
            Title = title,
            Content = content,
            PrimaryButtonText = primary,
            CloseButtonText = close,
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = (App.MainWindow.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
        };
        if (danger)
        {
            dialog.PrimaryButtonStyle = new Style(typeof(Button))
            {
                Setters =
                {
                    new Setter(Control.BackgroundProperty, Bind.Danger),
                    new Setter(Control.ForegroundProperty, new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White)),
                },
            };
        }
        return dialog;
    }

    public static async Task<bool> ConfirmAsync(string title, string message, string primary, bool danger = false)
    {
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        return await Create(title, text, primary, danger: danger).ShowAsync() == ContentDialogResult.Primary;
    }
}
