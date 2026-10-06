using System.Text.RegularExpressions;

namespace FluentDesktop.Core.DesktopCommander;

public sealed record DesktopCommanderOutputEvent(
    string Line,
    string? AuthUrl = null,
    string? AuthCode = null,
    string? DeviceId = null,
    bool WaitingForAuthorization = false,
    bool RestoredSession = false,
    bool Online = false);

/// <summary>Extracts pairing URL/code/device id and lifecycle markers from CLI output.</summary>
public static partial class DesktopCommanderOutputParser
{
    public static DesktopCommanderOutputEvent? Parse(string rawLine)
    {
        var line = AnsiEscape().Replace(rawLine, "").Trim();
        if (line.Length == 0) return null;

        var rawUrl = AuthUrlPattern().Match(line) is { Success: true } m ? m.Value : null;
        var authUrl = rawUrl == null ? null : TrailingPunctuation().Replace(rawUrl, "");
        var code = AuthCodePattern().Match(line) is { Success: true } c ? c.Groups[1].Value : null;
        var deviceId = DeviceIdPattern().Match(line) is { Success: true } d ? d.Groups[1].Value : null;

        return new DesktopCommanderOutputEvent(
            line,
            AuthUrl: IsHttpUrl(authUrl) ? authUrl : null,
            AuthCode: code,
            DeviceId: deviceId,
            WaitingForAuthorization: line.Contains("Waiting for authorization") || line.Contains("Code expires in"),
            RestoredSession: line.Contains("Found persisted session") || line.Contains("Session restored"),
            Online: line.Contains("Device marked as online") || line.Contains("Presence tracked") || line.Contains("visible as online"));
    }

    private static bool IsHttpUrl(string? value) =>
        value != null &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        uri.Host.Length > 0;

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiEscape();

    [GeneratedRegex(@"https?://[^\s]*verify[^\s]*", RegexOptions.IgnoreCase)]
    private static partial Regex AuthUrlPattern();

    [GeneratedRegex(@"\b([A-Z0-9]{4}-[A-Z0-9]{4})\b")]
    private static partial Regex AuthCodePattern();

    [GeneratedRegex(@"Device ID assigned:\s*([^\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceIdPattern();

    [GeneratedRegex(@"[),.\]}'"";:]+$")]
    private static partial Regex TrailingPunctuation();
}
