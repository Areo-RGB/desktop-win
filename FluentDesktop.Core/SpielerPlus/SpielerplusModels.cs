using System.Text.Json.Serialization;
using FluentDesktop.Core.Infrastructure;

namespace FluentDesktop.Core.SpielerPlus;

public enum FixtureSource { Fussball, Dfbnet }

/// <summary>Persisted importer settings (spielerplus_config.json).</summary>
public sealed class SpielerplusConfig
{
    public string FussballUrl { get; set; } =
        "https://www.fussball.de/mannschaft/fc-hertha-03-iii-hertha-03-zehlendorf-berlin/-/saison/2627/team-id/011MIE2EJK000000VTVG0001VTR8C1K7#!/";
    public string FussballDateFrom { get; set; } = "";
    public string FussballDateTo { get; set; } = "";
    public string HomeMarker { get; set; } = "Ernst-Reuter-Sportf";
    public string OutputDir { get; set; } = AppPaths.Desktop;
    public string DfbnetBaseUrl { get; set; } = "https://www.dfbnet.org/sbo-mobile/v2/rest";
    public string DfbnetTeamName { get; set; } = "FC Hertha 03 III";
    public string DfbnetClubId { get; set; } = "";
    public string DfbnetTeamTypeId { get; set; } = "";
    public string DfbnetCompetitionTypeId { get; set; } = "";
    public string DfbnetDateTo { get; set; } = "";
    public string IcsUrl { get; set; } = "";
    public string IcsFromDate { get; set; } = "";
    public string IcsInclude { get; set; } = "game,birthday,event";
    public string IcsTeamHint { get; set; } = "FC Hertha 03";
    public string BrowserSession { get; set; } = "spielerplus-ics";
    public string BrowserProfile { get; set; } = "Default";
    public string ParticipationUrl { get; set; } = "https://www.spielerplus.de/en/participation";

    public static string TodayIso(DateTime? now = null) => (now ?? DateTime.Now).ToString("yyyy-MM-dd");

    /// <summary>End of the current season (June 30); from June on it is next year.</summary>
    public static string DefaultSeasonEnd(DateTime? now = null)
    {
        var current = now ?? DateTime.Now;
        return $"{(current.Month >= 6 ? current.Year + 1 : current.Year)}-06-30";
    }

    public SpielerplusConfig ApplyDateDefaults()
    {
        if (string.IsNullOrWhiteSpace(FussballDateFrom)) FussballDateFrom = TodayIso();
        if (string.IsNullOrWhiteSpace(FussballDateTo)) FussballDateTo = DefaultSeasonEnd();
        if (string.IsNullOrWhiteSpace(DfbnetDateTo)) DfbnetDateTo = DefaultSeasonEnd();
        return this;
    }

    public SpielerplusConfig Clone() => (SpielerplusConfig)MemberwiseClone();
}

public sealed record SpielerplusFixture
{
    [JsonPropertyName("date_iso")] public string DateIso { get; init; } = "";
    [JsonPropertyName("time_text")] public string TimeText { get; init; } = "";
    [JsonPropertyName("home")] public string Home { get; init; } = "";
    [JsonPropertyName("away")] public string Away { get; init; } = "";
    [JsonPropertyName("competition")] public string Competition { get; init; } = "";
    [JsonPropertyName("match_type")] public string MatchType { get; init; } = "";
    [JsonPropertyName("venue")] public string Venue { get; init; } = "";
    [JsonPropertyName("match_url")] public string MatchUrl { get; init; } = "";

    [JsonIgnore] public bool Scheduled => DateIso.Length > 0 && TimeText.Length > 0;

    [JsonIgnore] public string MatchupLabel => $"{Home} vs {Away}";

    [JsonIgnore]
    public string CompetitionLabel =>
        Competition.Length == 0 ? MatchType : MatchType.Length == 0 ? Competition : $"{Competition} • {MatchType}";

    public bool IsHome(string homeMarker)
    {
        var marker = homeMarker.Trim();
        if (marker.Length > 0 && Venue.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        return Home.Contains("Hertha", StringComparison.Ordinal);
    }
}

public sealed record SpielerplusFetchResult(
    bool Ok,
    int ExitCode,
    string CsvPath = "",
    string XlsxPath = "",
    IReadOnlyList<SpielerplusFixture>? Fixtures = null,
    string Error = "")
{
    public IReadOnlyList<SpielerplusFixture> FixtureList => Fixtures ?? [];
}
