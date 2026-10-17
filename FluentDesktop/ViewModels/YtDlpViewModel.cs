using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentDesktop.Core.Infrastructure;
using FluentDesktop.Core.YtDlp;
using FluentDesktop.Helpers;
using Microsoft.UI.Xaml.Media;

namespace FluentDesktop.ViewModels;

public sealed class HistoryEntry(YtHistoryItem item)
{
    public YtHistoryItem Item { get; } = item;
    public string Title => Item.Title;
    public ImageSource? Thumbnail => Images.FromUrl(Item.Thumbnail, 184);
    public string Subtitle =>
        $"{Item.Uploader} · {(Item.DownloadType == "audio" ? "AUDIO" : "VIDEO")} ({Item.Quality}) · {Format.Date(Item.When)}";
}

/// <summary>A folder row for the local or cloud library column.</summary>
public sealed class FolderEntry(string name, string path, int count)
{
    public string Name { get; } = name;
    public string Path { get; } = path;
    public string Count { get; } = count.ToString();
}

/// <summary>A thumbnail card in the library grid (local file or R2 object).</summary>
public sealed partial class MediaEntry : ObservableObject
{
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public bool IsCloud { get; init; }
    public YtLocalVideo? Local { get; init; }
    public YtCloudVideo? Cloud { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail), nameof(NoThumbnail))]
    private ImageSource? _thumbnail;

    [ObservableProperty]
    private string? _badge;

    public bool HasThumbnail => Thumbnail != null;
    public bool NoThumbnail => Thumbnail == null;

    /// <summary>Falls back to the Windows shell thumbnail of the video file.</summary>
    public async Task LoadShellThumbnailAsync()
    {
        if (Thumbnail != null || Local == null) return;
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(Local.Path);
            using var thumb = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.VideosView, 480);
            if (thumb == null) return;
            var image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            await image.SetSourceAsync(thumb);
            Thumbnail = image;
        }
        catch
        {
            // Keep the placeholder icon.
        }
    }
}

public sealed partial class YtDlpViewModel : ViewModelBase, IDisposable
{
    /// <summary>Paul's external drive library; falls back to Downloads when not mounted.</summary>
    public static readonly string DefaultLocalFilesRoot =
        Environment.GetEnvironmentVariable("YT_DLP_LIBRARY_ROOT") is { Length: > 0 } root ? root : @"D:\video_manager";

    private readonly YtDlpService _service = new();
    private readonly YtLibraryService _library = new();
    private readonly Dictionary<string, string> _uploaded = [];
    private YtLocalLibrary? _localLibrary;
    private YtCloudLibrary? _cloudLibrary;
    private bool _initialized;
    private bool _disposed;

    public ObservableCollection<HistoryEntry> History { get; } = [];
    public ObservableCollection<FolderEntry> LocalFolders { get; } = [];
    public ObservableCollection<MediaEntry> LocalMedia { get; } = [];
    public ObservableCollection<FolderEntry> CloudFolders { get; } = [];
    public ObservableCollection<MediaEntry> CloudMedia { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private string _url = "";
    [ObservableProperty] private YtVideoInfo? _videoInfo;
    [ObservableProperty] private bool _isLoadingInfo;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private YtDownloadProgress? _progress;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _successMessage;
    [ObservableProperty] private int _downloadTypeIndex;
    [ObservableProperty] private int _qualityIndex = (int)YtVideoQuality.P1080;
    [ObservableProperty] private int _audioFormatIndex;
    [ObservableProperty] private bool _embedSubs;
    [ObservableProperty] private bool _embedThumbnail;
    [ObservableProperty] private bool _embedMetadata;
    [ObservableProperty] private bool _embedChapters;
    [ObservableProperty] private int _playlistIndex;
    [ObservableProperty] private string _outputDir = FluentDesktop.Core.Settings.FolderSettings.Load().Downloads;
    [ObservableProperty] private bool _isLoadingLocal;
    [ObservableProperty] private bool _isLoadingCloud;
    [ObservableProperty] private string _localSearch = "";
    [ObservableProperty] private string _cloudSearch = "";
    [ObservableProperty] private FolderEntry? _selectedLocalFolder;
    [ObservableProperty] private FolderEntry? _selectedCloudFolder;
    [ObservableProperty] private YtR2Config _r2Config = YtR2Config.FromEnvironment();
    [ObservableProperty] private string _localRoot = DefaultLocalFilesRoot;
    [ObservableProperty] private string? _uploadStatus;

    public IReadOnlyList<string> DownloadTypes { get; } = ["Video (MP4 / MKV)", "Audio only (MP3 / M4A)"];
    public IReadOnlyList<string> Qualities { get; } = Enum.GetValues<YtVideoQuality>().Select(q => q.Label()).ToList();
    public IReadOnlyList<string> AudioFormats { get; } = Enum.GetValues<YtAudioFormat>().Select(f => $"{f.Label()} audio").ToList();
    public IReadOnlyList<string> PlaylistModes { get; } = ["Single video", "Full playlist"];

    public bool IsVideoMode => DownloadTypeIndex == 0;
    public bool IsAudioMode => DownloadTypeIndex == 1;
    public string StatusLabel => IsLoading ? "Checking yt-dlp…" : IsAvailable ? "yt-dlp ready" : "yt-dlp not found";
    public bool ShowUnavailable => !IsAvailable && !IsLoading;
    public bool HasMessage => ErrorMessage != null || SuccessMessage != null;
    public string? Message => ErrorMessage ?? SuccessMessage;
    public bool IsError => ErrorMessage != null;
    public string HistoryLabel => $"History ({History.Count})";
    public bool ShowProgress => IsDownloading && Progress != null;
    public string ParseLabel => IsLoadingInfo ? "Parsing…" : "Parse";
    public bool CanParse => !IsLoadingInfo && !IsDownloading;
    public bool CanEdit => !IsDownloading;
    public string R2Label => $"{R2Config.Bucket} · {(R2Config.PublicBaseUrl.Length == 0 ? "private endpoint" : R2Config.PublicBaseUrl)}";

    public string VideoMeta => VideoInfo is not { } i
        ? ""
        : string.Join("\n", new[]
        {
            $"👤 {i.Uploader}",
            i.Duration > 0 ? $"⏱ {i.DurationFormatted}" : null,
            i.ViewCount is { } v ? $"👁 {Format.Count(v)} views" : null,
            i.IsPlaylist ? $"▶ Playlist · {i.PlaylistCount?.ToString() ?? "multiple"} videos" : null,
        }.Where(s => s != null));

    public ImageSource? VideoThumbnail => Images.FromUrl(VideoInfo?.Thumbnail, 520);

    public string SelectedLocalTitle => SelectedLocalFolder?.Name ?? "Select a folder";
    public string SelectedCloudTitle => SelectedCloudFolder?.Name ?? "Load cloud files and select a folder";
    public bool CanDeleteLocalFolder => SelectedLocalFolder != null && SelectedLocalFolder.Path != _localLibrary?.RootPath;

    partial void OnDownloadTypeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsVideoMode));
        OnPropertyChanged(nameof(IsAudioMode));
    }

    partial void OnIsLoadingChanged(bool value) => RaiseStatus();
    partial void OnIsAvailableChanged(bool value) => RaiseStatus();
    partial void OnErrorMessageChanged(string? value) => RaiseMessage();
    partial void OnSuccessMessageChanged(string? value) => RaiseMessage();
    partial void OnIsDownloadingChanged(bool value) => RaiseButtons();
    partial void OnIsLoadingInfoChanged(bool value) => RaiseButtons();
    partial void OnProgressChanged(YtDownloadProgress? value) => OnPropertyChanged(nameof(ShowProgress));
    partial void OnR2ConfigChanged(YtR2Config value) => OnPropertyChanged(nameof(R2Label));
    partial void OnLocalSearchChanged(string value) => RenderLocalFolders();
    partial void OnCloudSearchChanged(string value) => RenderCloudFolders();

    partial void OnVideoInfoChanged(YtVideoInfo? value)
    {
        OnPropertyChanged(nameof(VideoMeta));
        OnPropertyChanged(nameof(VideoThumbnail));
    }

    partial void OnSelectedLocalFolderChanged(FolderEntry? value)
    {
        OnPropertyChanged(nameof(SelectedLocalTitle));
        OnPropertyChanged(nameof(CanDeleteLocalFolder));
        RenderLocalMedia();
    }

    partial void OnSelectedCloudFolderChanged(FolderEntry? value)
    {
        OnPropertyChanged(nameof(SelectedCloudTitle));
        RenderCloudMedia();
    }

    partial void OnUrlChanged(string value)
    {
        ErrorMessage = null;
        SuccessMessage = null;
    }

    private void RaiseStatus()
    {
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(ShowUnavailable));
    }

    private void RaiseMessage()
    {
        OnPropertyChanged(nameof(HasMessage));
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(IsError));
    }

    private void RaiseButtons()
    {
        OnPropertyChanged(nameof(ShowProgress));
        OnPropertyChanged(nameof(ParseLabel));
        OnPropertyChanged(nameof(CanParse));
        OnPropertyChanged(nameof(CanEdit));
    }

    private void SetError(string message)
    {
        SuccessMessage = null;
        ErrorMessage = message;
    }

    private void SetSuccess(string message)
    {
        ErrorMessage = null;
        SuccessMessage = message;
    }

    public async Task InitAsync()
    {
        if (_initialized) return;
        _initialized = true;
        IsLoading = true;
        try
        {
            SetHistory(await _service.LoadHistoryAsync());
            IsAvailable = await _service.IsAvailableAsync();
            var root = Directory.Exists(DefaultLocalFilesRoot) ? DefaultLocalFilesRoot : OutputDir;
            await LoadLocalFilesAsync(root, silent: true);
        }
        catch (Exception e)
        {
            SetError($"Unable to initialize yt-dlp: {e.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void SetHistory(IEnumerable<YtHistoryItem> items)
    {
        History.Clear();
        foreach (var item in items) History.Add(new HistoryEntry(item));
        OnPropertyChanged(nameof(HistoryLabel));
    }

    public async Task PasteFromClipboardAsync()
    {
        try
        {
            var value = (await ClipboardHelper.GetTextAsync())?.Trim() ?? "";
            if (value.Length == 0 || !value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                SetError("Clipboard does not contain a valid URL.");
                return;
            }
            Url = value;
            await ParseAsync();
        }
        catch (Exception e)
        {
            SetError($"Clipboard error: {e.Message}");
        }
    }

    public async Task ParseAsync()
    {
        var url = Url.Trim();
        if (url.Length == 0)
        {
            SetError("Please enter a video link first.");
            return;
        }
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            SetError("Please enter a valid http(s) video URL.");
            return;
        }
        IsLoadingInfo = true;
        VideoInfo = null;
        ErrorMessage = null;
        SuccessMessage = null;
        try
        {
            VideoInfo = await Task.Run(() => _service.ParseVideoInfoAsync(url));
            if (VideoInfo.IsPlaylist) PlaylistIndex = 1;
        }
        catch (Exception e)
        {
            SetError(e.Message.Length == 0 ? "Failed to parse video details." : e.Message);
        }
        finally
        {
            IsLoadingInfo = false;
        }
    }

    public async Task SelectOutputFolderAsync()
    {
        if (await Pickers.PickFolderAsync() is { Length: > 0 } path) OutputDir = path;
    }

    public async Task StartDownloadAsync()
    {
        var url = Url.Trim();
        if (url.Length == 0 || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            SetError("Please enter a valid video link.");
            return;
        }
        if (IsDownloading) return;
        var options = new YtDownloadOptions(
            url,
            DownloadTypeIndex == 1 ? YtDownloadType.Audio : YtDownloadType.Video,
            (YtVideoQuality)Math.Max(0, QualityIndex),
            (YtAudioFormat)Math.Max(0, AudioFormatIndex),
            EmbedSubs, EmbedThumbnail, EmbedMetadata, EmbedChapters,
            PlaylistIndex == 1,
            OutputDir);

        IsDownloading = true;
        Progress = new YtDownloadProgress(0, "Starting…", "--", "--", "Initializing yt-dlp engine…");
        ErrorMessage = null;
        SuccessMessage = null;
        try
        {
            var output = await Task.Run(() => _service.DownloadAsync(options, p => OnUI(() =>
            {
                if (IsDownloading) Progress = p;
            })));
            if (_disposed) return;
            IsDownloading = false;
            SetSuccess($"Download completed! Saved to {OutputDir}");
            if (VideoInfo is { } info)
            {
                await _service.SaveHistoryAsync(new YtHistoryItem
                {
                    Id = $"{DateTimeOffset.Now.ToUnixTimeMilliseconds()}-{info.Id}",
                    Title = info.Title,
                    Thumbnail = info.Thumbnail,
                    Uploader = info.Uploader,
                    DownloadType = options.DownloadType == YtDownloadType.Audio ? "audio" : "video",
                    Quality = options.DownloadType == YtDownloadType.Audio ? options.AudioFormat.Value() : options.Quality.Value(),
                    FilePath = output,
                    Timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                });
                SetHistory(await _service.LoadHistoryAsync());
            }
        }
        catch (Exception e)
        {
            if (_disposed) return;
            IsDownloading = false;
            Progress = null;
            SetError(e.Message.Length == 0 ? "Download failed." : e.Message);
        }
    }

    public void CancelDownload()
    {
        if (!IsDownloading) return;
        _service.StopCurrentDownload();
        IsDownloading = false;
        Progress = null;
        SetError("Download canceled by user.");
    }

    public void OpenPath(string? path)
    {
        try { ProcessRunner.Open(path ?? OutputDir); }
        catch (Exception e) { SetError($"Failed to open folder: {e.Message}"); }
    }

    public async Task ClearHistoryAsync()
    {
        await _service.ClearHistoryAsync();
        SetHistory([]);
    }

    // ── Local library ──────────────────────────────────────────

    public async Task ChooseLocalRootAsync()
    {
        if (await Pickers.PickFolderAsync() is { Length: > 0 } path) await LoadLocalFilesAsync(path);
    }

    public async Task LoadLocalFilesAsync(string? path = null, bool silent = false)
    {
        var root = (path ?? _localLibrary?.RootPath ?? OutputDir).Trim();
        if (root.Length == 0) return;
        IsLoadingLocal = true;
        if (!silent)
        {
            ErrorMessage = null;
            SuccessMessage = null;
        }
        try
        {
            _localLibrary = await _library.ScanLocalFilesAsync(root);
            LocalRoot = _localLibrary.RootPath;
            var keep = SelectedLocalFolder?.Path;
            RenderLocalFolders();
            SelectedLocalFolder = LocalFolders.FirstOrDefault(f => f.Path == keep) ?? LocalFolders.FirstOrDefault();
            RenderLocalMedia();
        }
        catch (Exception e)
        {
            if (!silent) SetError(e.Message);
        }
        finally
        {
            IsLoadingLocal = false;
        }
    }

    private void RenderLocalFolders()
    {
        var selected = SelectedLocalFolder?.Path;
        LocalFolders.Clear();
        foreach (var f in (_localLibrary?.Folders ?? []).Where(f => Matches(f.Name, LocalSearch)))
        {
            LocalFolders.Add(new FolderEntry(f.Name, f.Path, f.Videos.Count));
        }
        if (selected != null && LocalFolders.FirstOrDefault(f => f.Path == selected) is { } again) SelectedLocalFolder = again;
    }

    private YtLocalFolder? CurrentLocalFolder => _localLibrary?.Folders.FirstOrDefault(f => f.Path == SelectedLocalFolder?.Path);

    private void RenderLocalMedia()
    {
        LocalMedia.Clear();
        if (CurrentLocalFolder is not { } folder) return;
        foreach (var v in folder.Videos)
        {
            var entry = new MediaEntry
            {
                Title = v.Name,
                Subtitle = $"{Format.Bytes(v.Size)} · {(v.Modified is { } m ? Format.Date(m) : "Unknown date")}",
                Thumbnail = Images.FromFile(v.Thumbnail, 480),
                Local = v,
                Badge = _uploaded.ContainsKey(v.Path) ? "R2" : null,
            };
            LocalMedia.Add(entry);
            _ = entry.LoadShellThumbnailAsync();
        }
    }

    public async Task DeleteLocalVideoAsync(YtLocalVideo video)
    {
        if (_localLibrary?.RootPath is not { } root) return;
        try
        {
            YtLibraryService.DeleteLocalFile(root, video.Path);
            _uploaded.Remove(video.Path);
            await LoadLocalFilesAsync(root, silent: true);
        }
        catch (Exception e)
        {
            SetError(e.Message);
        }
    }

    public async Task DeleteSelectedLocalFolderAsync()
    {
        if (_localLibrary?.RootPath is not { } root || CurrentLocalFolder is not { } folder) return;
        try
        {
            YtLibraryService.DeleteLocalFolder(root, folder.Path);
            foreach (var v in folder.Videos) _uploaded.Remove(v.Path);
            await LoadLocalFilesAsync(root, silent: true);
        }
        catch (Exception e)
        {
            SetError(e.Message);
        }
    }

    public async Task UploadLocalVideoAsync(YtLocalVideo video, string? folderName = null)
    {
        folderName ??= CurrentLocalFolder?.Name ?? "Unsorted";
        try
        {
            UploadStatus = $"Uploading {video.Name} to R2…";
            var url = await Task.Run(() => _library.UploadLocalVideoToR2Async(R2Config, video, folderName));
            _uploaded[video.Path] = url;
            foreach (var m in LocalMedia.Where(m => m.Local?.Path == video.Path)) m.Badge = "R2";
            ClipboardHelper.SetText(url);
            SetSuccess("Uploaded to R2 and copied URL.");
        }
        catch (Exception e)
        {
            SetError(e.Message);
        }
        finally
        {
            UploadStatus = null;
        }
    }

    public async Task UploadSelectedLocalFolderAsync()
    {
        if (CurrentLocalFolder is not { } folder) return;
        foreach (var v in folder.Videos) await UploadLocalVideoAsync(v, folder.Name);
    }

    public void CopyText(string text)
    {
        ClipboardHelper.SetText(text);
        SetSuccess("Copied to clipboard.");
    }

    // ── Cloud library ──────────────────────────────────────────

    public async Task LoadCloudFilesAsync()
    {
        IsLoadingCloud = true;
        ErrorMessage = null;
        SuccessMessage = null;
        try
        {
            _cloudLibrary = await Task.Run(() => _library.ListR2VideosAsync(R2Config));
            var keep = SelectedCloudFolder?.Path;
            RenderCloudFolders();
            SelectedCloudFolder = CloudFolders.FirstOrDefault(f => f.Path == keep) ?? CloudFolders.FirstOrDefault();
            RenderCloudMedia();
        }
        catch (Exception e)
        {
            SetError(e.Message);
        }
        finally
        {
            IsLoadingCloud = false;
        }
    }

    private void RenderCloudFolders()
    {
        var selected = SelectedCloudFolder?.Path;
        CloudFolders.Clear();
        foreach (var f in (_cloudLibrary?.Folders ?? []).Where(f => Matches(f.Name, CloudSearch)))
        {
            CloudFolders.Add(new FolderEntry(f.Name, f.Path, f.Videos.Count));
        }
        if (selected != null && CloudFolders.FirstOrDefault(f => f.Path == selected) is { } again) SelectedCloudFolder = again;
    }

    private YtCloudFolder? CurrentCloudFolder => _cloudLibrary?.Folders.FirstOrDefault(f => f.Path == SelectedCloudFolder?.Path);

    private void RenderCloudMedia()
    {
        CloudMedia.Clear();
        if (CurrentCloudFolder is not { } folder) return;
        foreach (var v in folder.Videos)
        {
            CloudMedia.Add(new MediaEntry
            {
                Title = v.Name,
                Subtitle = $"{Format.Bytes(v.Size)} · {(v.Modified is { } m ? Format.Date(m) : "Unknown date")}",
                IsCloud = true,
                Cloud = v,
                Badge = "R2",
            });
        }
    }

    public async Task DeleteCloudVideoAsync(YtCloudVideo video, bool reload = true)
    {
        try
        {
            await _library.DeleteR2ObjectAsync(R2Config, video.Key);
            if (reload) await LoadCloudFilesAsync();
        }
        catch (Exception e)
        {
            SetError(e.Message);
        }
    }

    public async Task DeleteSelectedCloudFolderAsync()
    {
        if (CurrentCloudFolder is not { } folder) return;
        foreach (var v in folder.Videos) await DeleteCloudVideoAsync(v, reload: false);
        await LoadCloudFilesAsync();
    }

    private static bool Matches(string name, string query) =>
        query.Trim().Length == 0 || name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        _disposed = true;
        _service.StopCurrentDownload();
    }
}
