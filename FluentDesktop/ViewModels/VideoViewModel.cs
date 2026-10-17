using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentDesktop.Core.Infrastructure;
using FluentDesktop.Core.Settings;
using FluentDesktop.Core.Video;
using FluentDesktop.Core.YtDlp;
using FluentDesktop.Helpers;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FluentDesktop.ViewModels;

public sealed partial class LocalVideoItem(LocalVideo video) : ObservableObject
{
    public LocalVideo Video { get; } = video;
    public string Name => Video.Name;
    public string Path => Video.Path;
    public string Size => VideoChapters.HumanSize(Video.SizeBytes);

    [ObservableProperty]
    private ImageSource? _thumbnail;

    [ObservableProperty]
    private bool _isChecked;

    [ObservableProperty]
    private bool _isSelected;
}

public sealed record ChapterRow(string Name, string Range);

public sealed partial class R2ObjectItem(R2Object obj, bool localMatch) : ObservableObject
{
    public R2Object Object { get; } = obj;
    public string Key => Object.Key;
    public string Size => VideoChapters.HumanSize(Object.Size);
    public string Modified => Object.LastModified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "";
    public bool LocalMatch { get; } = localMatch;
}

public sealed partial class VideoViewModel : ViewModelBase, IDisposable
{
    private readonly YtLibraryService _library = new();
    private ManagedProcess? _job;
    private ManagedProcess? _upload;
    private bool _batchCancelled;
    private VideoInfo? _info;

    public LogBuffer Logs => AppLogs.For("Video");
    public ObservableCollection<LocalVideoItem> LocalVideos { get; } = [];
    public ObservableCollection<ChapterRow> Chapters { get; } = [];
    public ObservableCollection<R2ObjectItem> Objects { get; } = [];

    // ---- file + chapters --------------------------------------------------

    [ObservableProperty]
    private string _filePath = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(HasInfo))]
    private bool _isLoading;

    [ObservableProperty]
    private string _infoText = "";

    [ObservableProperty]
    private string _chaptersText = "";

    [ObservableProperty]
    private string _chapterSummary = "";

    [ObservableProperty]
    private bool _stripTags;

    [ObservableProperty]
    private bool _matchSource = true;

    [ObservableProperty]
    private string _outputPath = "";

    [ObservableProperty]
    private int _encoderIndex;

    [ObservableProperty]
    private double _quality = 30;

    [ObservableProperty]
    private double _gopSeconds = 4;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(IsJobRunning))]
    private string? _jobLabel;

    [ObservableProperty]
    private double _jobProgress;

    public bool IsJobRunning => JobLabel != null;
    public bool HasInfo => _info != null && !IsLoading;
    public bool CanStart => _info != null && JobLabel == null && !IsLoading;

    // ---- local library ----------------------------------------------------

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _libraryText = "";

    // ---- R2 ---------------------------------------------------------------

    [ObservableProperty]
    private string _prefix = "videos-training/";

    [ObservableProperty]
    private bool _isListing;

    [ObservableProperty]
    private string _bucketText = "Not listed yet";

    [ObservableProperty]
    private string _uploadFile = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUploading), nameof(NotUploading), nameof(CanBatch))]
    private string? _uploadLabel;

    [ObservableProperty]
    private double _uploadProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBatch))]
    private int _checkedCount;

    public bool CanBatch => UploadLabel == null && CheckedCount > 0;

    public bool IsUploading => UploadLabel != null;
    public bool NotUploading => UploadLabel == null;

    private void Log(string text, LogLevel level = LogLevel.Info) => OnUI(() => AppLogs.Push("Video", text, level));

    // ---- load -------------------------------------------------------------

    public async Task LoadAsync()
    {
        var path = FilePath.Trim().Trim('"');
        if (path.Length == 0 || IsLoading || IsJobRunning) return;
        IsLoading = true;
        try
        {
            var info = await VideoChapters.ProbeAsync(path);
            ApplyInfo(info);
            Log($"Read {path}");
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or System.Text.Json.JsonException)
        {
            _info = null;
            InfoText = "";
            Chapters.Clear();
            Log($"Couldn’t read {path}: {e.Message}", LogLevel.Error);
            Services.Notifier.Error($"Couldn’t read the video: {e.Message}");
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasInfo));
            OnPropertyChanged(nameof(CanStart));
        }
    }

    private void ApplyInfo(VideoInfo info)
    {
        _info = info;
        FilePath = info.Path;
        var duration = VideoChapters.Clock((long)Math.Round(info.DurationS * 1000));
        InfoText = $"{duration} · {VideoChapters.HumanSize(info.SizeBytes)}" + (info.Video is { } v ? $" · {v.Summary}" : "");
        Chapters.Clear();
        foreach (var c in info.Chapters) Chapters.Add(new ChapterRow(c.Name, $"{VideoChapters.Clock(c.StartMs)} – {VideoChapters.Clock(c.EndMs)}"));
        var source = info.ChapterSource switch
        {
            ChapterSource.Tag => "from the chapters tag",
            ChapterSource.Markers => "from chapter markers",
            _ => "none",
        };
        ChapterSummary = $"{System.IO.Path.GetFileName(info.Path)} · {info.Chapters.Count} chapters · {source}";
        ChaptersText = info.Chapters.Count == 0 ? "" : VideoChapters.ToPrettyJson(info.Chapters);
        OutputPath = VideoChapters.DefaultEncodeOutput(info.Path);
        if (!IsUploading) UploadFile = info.Path;
        foreach (var item in LocalVideos) item.IsSelected = string.Equals(item.Path, info.Path, StringComparison.OrdinalIgnoreCase);
    }

    public void ResetEditor() => ChaptersText = _info is { Chapters.Count: > 0 } i ? VideoChapters.ToPrettyJson(i.Chapters) : "";

    /// <summary>Empty text means "no chapters".</summary>
    private List<Chapter> EditedChapters()
    {
        var text = ChaptersText.Trim();
        if (text.Length == 0) return [];
        var chapters = VideoChapters.Parse(text);
        if (_info != null)
        {
            var durationMs = (long)Math.Round(_info.DurationS * 1000);
            if (chapters.FirstOrDefault(c => c.EndMs > durationMs + 500) is { } late)
                throw new FormatException($"“{late.Name}” ends at {VideoChapters.Clock(late.EndMs)}, after the video ({VideoChapters.Clock(durationMs)})");
        }
        return chapters;
    }

    public Task WriteMetadataAsync() => StartJobAsync(encode: false);

    public Task EncodeAsync() => StartJobAsync(encode: true);

    private async Task StartJobAsync(bool encode)
    {
        if (!CanStart || _info is not { } info) return;
        List<Chapter> chapters;
        try { chapters = EditedChapters(); }
        catch (FormatException e) { Log(e.Message, LogLevel.Error); Services.Notifier.Error(e.Message); return; }

        var source = info.Path;
        string output, finalPath;
        if (encode)
        {
            output = OutputPath.Trim().Length == 0 ? VideoChapters.DefaultEncodeOutput(source) : OutputPath.Trim();
            if (string.Equals(System.IO.Path.GetFullPath(output), System.IO.Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
            {
                Log("The output must differ from the source file.", LogLevel.Error);
                return;
            }
            finalPath = output;
        }
        else
        {
            // Written next to the source, moved over it once verified.
            output = VideoChapters.TempOutput(source);
            finalPath = source;
        }
        var metaFile = System.IO.Path.ChangeExtension(output, "ffmeta");
        try { await File.WriteAllTextAsync(metaFile, VideoChapters.FfMetadata(chapters)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log($"Couldn’t write {metaFile}: {e.Message}", LogLevel.Error);
            return;
        }

        List<string> args;
        if (encode)
        {
            var options = new EncodeOptions(EncoderIndex == 0 ? VideoEncoder.Nvenc : VideoEncoder.X265, (int)Quality, GopSeconds, MatchSource ? info.Video : null);
            if (options.MatchSource is { } m) Log($"Matching the original: {m.Summary}");
            var keyframes = VideoChapters.KeyframeTimes(chapters, info.DurationS, options.GopS);
            Log($"{chapters.Count} chapters, {keyframes.Count} forced keyframes");
            args = VideoChapters.EncodeArgs(source, output, chapters, StripTags, metaFile, keyframes, options);
        }
        else
        {
            args = VideoChapters.WriteMetadataArgs(source, output, chapters, StripTags, metaFile);
        }

        var duration = Math.Max(info.DurationS, 0.001);
        ManagedProcess process;
        try
        {
            process = ProcessRunner.StartStreaming("ffmpeg", args,
                line =>
                {
                    if (VideoChapters.ProgressSeconds(line) is { } s) OnUI(() => JobProgress = Math.Min(100, 100 * s / duration));
                },
                line => Log(line, LogLevel.Error));
        }
        catch (Exception e)
        {
            File.Delete(metaFile);
            Log($"Couldn’t start ffmpeg: {e.Message}", LogLevel.Error);
            return;
        }
        Log("ffmpeg " + WindowsCommandLine.Join(args), LogLevel.Command);
        _job = process;
        JobProgress = 0;
        JobLabel = encode ? "Re-encoding" : "Writing metadata";

        var exit = await process.WaitAsync();
        process.Dispose();
        _job = null;
        try { File.Delete(metaFile); } catch (IOException) { }
        try
        {
            if (exit != 0)
            {
                try { File.Delete(output); } catch (IOException) { }
                throw new InvalidOperationException(JobLabel == "Cancelling" ? "Cancelled" : $"ffmpeg exited with {exit}");
            }
            var verified = await Task.Run(async () =>
            {
                var result = await VideoChapters.VerifyAsync(output, chapters);
                if (output != finalPath)
                {
                    File.Move(output, finalPath, overwrite: true);
                    result = result with { Path = finalPath };
                }
                return result;
            });
            Log($"Done: {verified.Path} · {VideoChapters.HumanSize(verified.SizeBytes)} · {verified.Chapters.Count} chapters", LogLevel.Success);
            Services.Notifier.Success(encode ? "Re-encode finished" : "Metadata written");
            if (encode && info.Video is { } src && verified.Video is { } dst)
            {
                Log($"Original: {src.Summary}");
                Log($"Re-encode: {dst.Summary}");
                var diffs = VideoChapters.Compare(src, dst);
                if (diffs.Count == 0) Log("Matches the original (size, frame rate, bit depth, colours, bitrate ±25 %)", LogLevel.Success);
                else Log("Differs from the original: " + string.Join("; ", diffs), LogLevel.Warning);
            }
            if (!encode) ApplyInfo(verified);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Log(e.Message, LogLevel.Error);
            Services.Notifier.Error(e.Message);
        }
        finally
        {
            JobLabel = null;
        }
    }

    public void Cancel()
    {
        if (_job?.Kill() == true)
        {
            JobLabel = "Cancelling";
            Log("Cancelling…", LogLevel.Warning);
        }
    }

    public void OpenInPlayer()
    {
        if (_info != null) ProcessRunner.Open(_info.Path);
    }

    // ---- local library ----------------------------------------------------

    public async Task ScanLocalAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        var folder = FolderSettings.Load().VideoLibrary;
        LibraryText = folder;
        List<LocalVideo> videos;
        try
        {
            videos = await Task.Run(() => VideoChapters.ScanVideos(folder));
        }
        catch (DirectoryNotFoundException e)
        {
            LocalVideos.Clear();
            LibraryText = e.Message;
            IsScanning = false;
            return;
        }
        var checkedPaths = LocalVideos.Where(v => v.IsChecked).Select(v => v.Path).ToHashSet();
        LocalVideos.Clear();
        foreach (var v in videos)
        {
            var item = new LocalVideoItem(v) { IsChecked = checkedPaths.Contains(v.Path), IsSelected = string.Equals(v.Path, _info?.Path, StringComparison.OrdinalIgnoreCase) };
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LocalVideoItem.IsChecked)) CheckedCount = LocalVideos.Count(x => x.IsChecked);
            };
            LocalVideos.Add(item);
        }
        CheckedCount = LocalVideos.Count(x => x.IsChecked);
        LibraryText = $"{videos.Count} videos in {folder}";
        IsScanning = false;
        RefreshLocalMatches();
        foreach (var item in LocalVideos.ToList())
        {
            if (await VideoChapters.ThumbnailAsync(item.Path) is { } thumb)
            {
                item.Thumbnail = new BitmapImage(new Uri(thumb)) { DecodePixelWidth = 320 };
            }
        }
    }

    public async Task SelectLocalAsync(LocalVideoItem item)
    {
        if (IsJobRunning)
        {
            Log("Wait for the running job to finish first.", LogLevel.Warning);
            return;
        }
        FilePath = item.Path;
        await LoadAsync();
    }

    // ---- R2 ---------------------------------------------------------------

    public async Task ListR2Async()
    {
        if (IsListing) return;
        IsListing = true;
        try
        {
            var credentials = TrainingBucket.LoadCredentials();
            var objects = await TrainingBucket.ListAsync(_library, credentials, Prefix.Trim());
            Objects.Clear();
            foreach (var o in objects) Objects.Add(new R2ObjectItem(o, LocalMatches(o)));
            BucketText = $"{credentials.Bucket} · {objects.Count} files · {VideoChapters.HumanSize(objects.Sum(o => o.Size))}";
        }
        catch (Exception e) when (e is YtDlpException or HttpRequestException or IOException or TaskCanceledException)
        {
            BucketText = e.Message;
            Log(e.Message, LogLevel.Error);
        }
        finally
        {
            IsListing = false;
        }
    }

    private bool LocalMatches(R2Object o) => LocalVideos.Any(v => v.Name == o.Name && v.Video.SizeBytes == o.Size);

    private void RefreshLocalMatches()
    {
        var items = Objects.Select(o => new R2ObjectItem(o.Object, LocalMatches(o.Object))).ToList();
        Objects.Clear();
        foreach (var i in items) Objects.Add(i);
    }

    public bool KeyExists(string key) => Objects.Any(o => o.Key == key);

    public string UploadKey => TrainingBucket.KeyFor(Prefix, UploadFile.Trim().Trim('"'));

    public List<(string File, string Key)> BatchItems() =>
        LocalVideos.Where(v => v.IsChecked).Select(v => (v.Path, TrainingBucket.KeyFor(Prefix, v.Path))).ToList();

    public async Task DeleteObjectAsync(string key)
    {
        try
        {
            await _library.DeleteR2ObjectAsync(TrainingBucket.LoadCredentials(), key);
            Log($"Deleted {key}", LogLevel.Success);
            await ListR2Async();
        }
        catch (Exception e) when (e is YtDlpException or HttpRequestException or IOException)
        {
            Log($"Couldn’t delete {key}: {e.Message}", LogLevel.Error);
            Services.Notifier.Error(e.Message);
        }
    }

    /// <summary>Uploads one after another; returns (ok, failed).</summary>
    public async Task UploadAsync(List<(string File, string Key)> items)
    {
        if (IsUploading || items.Count == 0) return;
        _batchCancelled = false;
        int done = 0, failed = 0;
        try
        {
            var credentials = TrainingBucket.LoadCredentials();
            foreach (var (file, key) in items)
            {
                if (_batchCancelled) break;
                UploadLabel = items.Count == 1 ? $"Uploading {key}" : $"Uploading {done + 1} of {items.Count}: {key}";
                UploadProgress = 0;
                var size = new FileInfo(file) is { Exists: true } fi ? fi.Length : -1;
                if (size < 0)
                {
                    Log($"{file} isn’t a file", LogLevel.Error);
                    failed++;
                    done++;
                    continue;
                }
                Log($"Uploading {file} → {key} ({VideoChapters.HumanSize(size)})", LogLevel.Command);
                var errors = new List<string>();
                var started = DateTime.UtcNow;
                var process = await TrainingBucket.StartUploadAsync(credentials, key, file, line =>
                {
                    switch (TrainingBucket.ParseRcloneLine(line))
                    {
                        case RcloneLine.Progress p: OnUI(() => UploadProgress = p.Percent); break;
                        case RcloneLine.Error err: lock (errors) errors.Add(err.Message); break;
                    }
                });
                _upload = process;
                var code = await process.WaitAsync();
                process.Dispose();
                _upload = null;
                done++;
                if (code == 0)
                {
                    var seconds = Math.Max((DateTime.UtcNow - started).TotalSeconds, 0.001);
                    Log($"Uploaded {key} in {seconds:0} s ({VideoChapters.HumanSize((long)(size / seconds))}/s)", LogLevel.Success);
                }
                else
                {
                    failed++;
                    Log($"{key}: {(errors.Count > 0 ? string.Join(" ", errors) : $"rclone exit {code}")}", LogLevel.Error);
                }
            }
        }
        catch (YtDlpException e)
        {
            Log($"Couldn’t start the upload: {e.Message}", LogLevel.Error);
            Services.Notifier.Error(e.Message);
            return;
        }
        finally
        {
            UploadLabel = null;
            _upload = null;
        }
        if (items.Count > 1)
        {
            foreach (var v in LocalVideos) v.IsChecked = false;
            Log($"Uploads finished: {done - failed} ok, {failed} failed{(_batchCancelled ? " (cancelled)" : "")}", failed == 0 ? LogLevel.Success : LogLevel.Warning);
        }
        await ListR2Async();
    }

    public void CancelUpload()
    {
        _batchCancelled = true;
        if (_upload?.Kill() == true) Log("Cancelling upload…", LogLevel.Warning);
    }

    public void Dispose()
    {
        _job?.Kill();
        _upload?.Kill();
    }
}
