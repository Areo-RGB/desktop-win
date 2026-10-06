using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace FluentDesktop.Helpers;

public static class ClipboardHelper
{
    public static void SetText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    public static void Copy(string text, string label)
    {
        SetText(text);
        Services.Notifier.Success($"{label} copied to clipboard");
    }

    /// <summary>Puts a PNG on the clipboard as a bitmap (plus its path as text).</summary>
    public static async Task<bool> SetImageAsync(byte[] png, string? path = null)
    {
        try
        {
            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(png.AsBuffer());
            stream.Seek(0);
            var package = new DataPackage();
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            if (path != null) package.SetText(path);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<string?> GetTextAsync()
    {
        var content = Clipboard.GetContent();
        return content.Contains(StandardDataFormats.Text) ? await content.GetTextAsync() : null;
    }
}
