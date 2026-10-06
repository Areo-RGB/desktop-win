namespace FluentDesktop.Core.DeviceMirror;

public sealed class AdbDevice
{
    public required string Serial { get; init; }

    /// <summary>device, offline, unauthorized, ...</summary>
    public required string State { get; init; }

    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? AndroidVersion { get; set; }
    public string? SdkVersion { get; set; }
    public int? BatteryLevel { get; set; }
    public bool IsCharging { get; set; }
    public int? ScreenWidth { get; set; }
    public int? ScreenHeight { get; set; }
    public int? ScreenDensity { get; set; }
    public string? IpAddress { get; set; }

    public bool IsOnline => State == "device";

    public string DisplayName => Manufacturer != null && Model != null ? $"{Manufacturer} {Model}" : Model ?? Serial;
}

public sealed record BinaryStatus(string AdbPath, string ScrcpyPath, bool AdbOk, bool ScrcpyOk, string? AdbVersion, string? ScrcpyVersion);

public sealed class ScrcpyConfig
{
    public int MaxFps { get; set; } = 60;          // 0 = unlimited
    public int MaxSize { get; set; } = 1080;       // 0 = original
    public string BitRate { get; set; } = "8M";
    public string VideoCodec { get; set; } = "h264";
    public bool Borderless { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool Fullscreen { get; set; }
    public bool StayAwake { get; set; } = true;
    public bool TurnScreenOff { get; set; }
    public bool ShowTouches { get; set; }
    public bool NoAudio { get; set; }
    public bool RecordVideo { get; set; }
    public string? CustomTitle { get; set; }

    /// <summary>Deterministic scrcpy argument list (stable order for tests and diagnostics).</summary>
    public List<string> ToArgs(string? serial = null, string? recordPath = null)
    {
        var args = new List<string>();
        if (serial != null) args.AddRange(["-s", serial]);
        args.AddRange(["--window-title", CustomTitle ?? (serial != null ? $"SCRCPY — {serial}" : "SCRCPY")]);
        if (Borderless) args.Add("--window-borderless");
        if (AlwaysOnTop) args.Add("--always-on-top");
        if (Fullscreen) args.Add("--fullscreen");
        if (StayAwake) args.Add("--stay-awake");
        if (TurnScreenOff) args.Add("--turn-screen-off");
        if (ShowTouches) args.Add("--show-touches");
        if (NoAudio) args.Add("--no-audio");
        if (MaxFps > 0) args.Add($"--max-fps={MaxFps}");
        if (MaxSize > 0) args.AddRange(["-m", $"{MaxSize}"]);
        if (BitRate.Length > 0) args.AddRange(["-b", BitRate]);
        if (VideoCodec.Length > 0) args.Add($"--video-codec={VideoCodec}");
        if (RecordVideo && recordPath != null) args.AddRange(["--record", recordPath]);
        return args;
    }
}
