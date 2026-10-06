using Windows.Storage.Pickers;

namespace FluentDesktop.Helpers;

public static class Pickers
{
    /// <summary>Folder picker bound to the main window (unpackaged apps need the HWND).</summary>
    public static async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}
