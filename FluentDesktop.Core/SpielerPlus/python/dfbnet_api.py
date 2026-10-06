#!/usr/bin/env python3
"""Fetch DFBnet v2 fixtures and write a SpielerPlus import file.

DFBnet's v2 match-search API is an authenticated application API.  The
REST endpoints return grouped match reports, which this module flattens
into the existing :class:`fussball_csv.Fixture` model so the CSV/XLSX writers
stay identical for both sources.

Authentication is intentionally supplied out-of-band through
``DFBNET_ACCESS_TOKEN`` (Bearer token) or ``DFBNET_COOKIE`` (a complete Cookie
header).  With ``--agent-browser-auth`` the short-lived bearer is obtained
from the headed browser session in memory.  Tokens are never accepted as
command-line arguments, logged, or written to the output metadata.
"""

from __future__ import annotations

import argparse
from dataclasses import asdict
from datetime import date, datetime
import json
import os
from pathlib import Path
import sys
from typing import Any, Callable, Mapping
from urllib.error import HTTPError, URLError
from urllib.parse import parse_qs, quote, urlsplit
from urllib.request import Request, urlopen

from dfbnet_auth import (
    DEFAULT_SESSION,
    DEFAULT_TIMEOUT_SECONDS,
    DfbnetBrowserAuthError,
    fetch_access_token,
)
from fussball_csv import (
    DEFAULT_HOME_VENUE,
    Fixture,
    TEAM_NAME,
    clean_text,
    season_range,
    write_csv,
    write_xlsx,
)


DFBNET_WEB_BASE = "https://www.dfbnet.org/sbo-mobile/v2"
# The current v2 web app sends its OIDC bearer token to the REST namespace.
# Keep the base configurable because DFBnet has operated multiple API
# namespaces over time and the setting is useful for a tenant-specific host.
DFBNET_API_BASE = f"{DFBNET_WEB_BASE}/rest"


class DfbnetError(RuntimeError):
    """Base error for DFBnet API and response failures."""


class DfbnetAuthError(DfbnetError):
    """Raised when no usable DFBnet credential was supplied."""


JsonOpener = Callable[..., Any]


def _response_detail(text: str, *, token: str, cookie: str) -> str:
    """Keep server error bodies from echoing authenticated credentials.

    DFBnet currently echoes a truncated bearer token in some 401 responses,
    which cannot be reliably scrubbed after truncation.  Once a credential is
    present, omit the response body altogether; the HTTP status and endpoint
    remain enough to diagnose the request without leaking a secret.
    """
    if token or cookie:
        return ""
    return text[:300]


def _credential_values(token: str = "", cookie: str = "") -> tuple[str, str]:
    """Normalize credentials and require at least one authentication method."""
    token = (token or "").strip()
    cookie = (cookie or "").strip()
    if token.lower().startswith("bearer "):
        token = token[7:].strip()
    if not token and not cookie:
        raise DfbnetAuthError(
            "DFBNET_ACCESS_TOKEN is not set; provide a DFBnet bearer token "
            "or set DFBNET_COOKIE to a logged-in Cookie header, or use "
            "--agent-browser-auth"
        )
    return token, cookie


def request_json(
    url: str,
    *,
    method: str = "GET",
    payload: object | None = None,
    token: str = "",
    cookie: str = "",
    timeout: int = 30,
    opener: JsonOpener = urlopen,
) -> object:
    """Make an authenticated JSON request and decode its response.

    ``opener`` is injectable for deterministic unit tests; production callers
    use :func:`urllib.request.urlopen` by default.
    """
    headers = {
        "Accept": "application/json",
        "User-Agent": "spielerplus-ics-importer/1.0",
    }
    token, cookie = _credential_values(token, cookie)
    if token:
        headers["Authorization"] = f"Bearer {token}"
    if cookie:
        headers["Cookie"] = cookie

    body: bytes | None = None
    if payload is not None:
        headers["Content-Type"] = "application/json"
        body = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    request = Request(url, data=body, headers=headers, method=method.upper())

    try:
        with opener(request, timeout=timeout) as response:
            raw = response.read()
            charset = response.headers.get_content_charset() or "utf-8"
            status = int(getattr(response, "status", 200) or 200)
    except HTTPError as exc:
        detail = ""
        try:
            detail = exc.read().decode("utf-8", errors="replace")
        except Exception:
            pass
        detail = _response_detail(detail, token=token, cookie=cookie)
        suffix = f": {detail}" if detail else ""
        raise DfbnetError(f"DFBnet request failed ({exc.code}) {url}{suffix}") from exc
    except (URLError, TimeoutError, OSError) as exc:
        raise DfbnetError(f"DFBnet request failed: {url}: {exc}") from exc

    text = raw.decode(charset, errors="replace")
    if status >= 400:
        detail = _response_detail(text, token=token, cookie=cookie)
        suffix = f": {detail}" if detail else ""
        raise DfbnetError(f"DFBnet request failed ({status}) {url}{suffix}")
    try:
        return json.loads(text)
    except json.JSONDecodeError as exc:
        raise DfbnetError(f"DFBnet returned invalid JSON from {url}: {exc}") from exc


def build_search_payload(
    *,
    club_id: str,
    date_from: str,
    date_to: str,
    team_type_id: str = "",
    competition_type_id: str = "",
    timing: str = "ALL",
) -> dict[str, object]:
    """Build the compact payload accepted by ``POST /match/search``."""
    club_id = str(club_id or "").strip()
    if not club_id:
        raise ValueError("club_id is required for a DFBnet match search")
    payload: dict[str, object] = {
        "searchType": "CLUB",
        "clubId": club_id,
        "kickoffFrom": date_from,
        "kickoffTo": date_to,
        "timing": (timing or "ALL").upper(),
    }
    if str(team_type_id or "").strip():
        payload["teamTypeId"] = str(team_type_id).strip()
    if str(competition_type_id or "").strip():
        payload["competitionTypeId"] = str(competition_type_id).strip()
    return payload


def _text(value: object) -> str:
    if value is None:
        return ""
    if isinstance(value, str):
        return " ".join(value.split())
    return str(value).strip()


def _first(mapping: Mapping[str, object], *keys: str) -> str:
    for key in keys:
        value = _text(mapping.get(key))
        if value:
            return value
    return ""


def _unique_join(values: list[str]) -> str:
    seen: set[str] = set()
    result: list[str] = []
    for value in values:
        value = _text(value)
        if value and value.casefold() not in seen:
            seen.add(value.casefold())
            result.append(value)
    return " | ".join(result)


def _kickoff_parts(value: object) -> tuple[str, str]:
    raw = _text(value)
    if not raw:
        return "", ""
    normalized = raw[:-1] + "+00:00" if raw.endswith("Z") else raw
    try:
        parsed = datetime.fromisoformat(normalized)
    except ValueError:
        # Keep the date if the server sends a future timestamp variant that
        # Python cannot parse, while still allowing the writer to skip an
        # unavailable kickoff time.
        return raw[:10] if len(raw) >= 10 else "", ""
    return parsed.date().isoformat(), parsed.strftime("%H:%M")


def _club_id_from_logo(value: object) -> str:
    raw = _text(value)
    if not raw:
        return ""
    try:
        return parse_qs(urlsplit(raw).query).get("id", [""])[0]
    except ValueError:
        return ""


def _venue_text(value: object) -> str:
    if isinstance(value, str):
        return _text(value)
    if not isinstance(value, Mapping):
        return ""
    name = _first(value, "name", "venueName", "shortName")
    type_name = _first(value, "typeName", "venueTypeName", "surface")
    street = _first(value, "street", "streetName", "address", "addressLine")
    postal_code = _first(value, "postalCode", "zipCode", "postcode")
    town = _first(value, "city", "town", "location")
    postal_town = " ".join(part for part in (postal_code, town) if part)
    # SpielerPlus compares imported addresses with the configured location
    # text.  A comma-separated, human-readable form matches the address shown
    # in DFBnet and keeps the venue name available for location matching.
    parts = [type_name, name, street, postal_town]
    result: list[str] = []
    seen: set[str] = set()
    for part in parts:
        normalized = part.casefold()
        if part and normalized not in seen:
            seen.add(normalized)
            result.append(part)
    return ", ".join(result)


def _venue_from_details(data: object) -> str:
    """Extract a venue from the response returned by ``/match/{id}/details``."""
    if not isinstance(data, Mapping):
        return ""
    for key in ("venue", "matchVenue"):
        venue = _venue_text(data.get(key))
        if venue:
            return venue
    for key in ("matchInfo", "match", "details"):
        nested = data.get(key)
        venue = _venue_from_details(nested)
        if venue:
            return venue
    return ""


def _match_id_from_url(match_url: str) -> str:
    parsed = urlsplit(match_url)
    # DFBnet report links are hash routes (``/#/match-report/report/{id}``),
    # so the match ID lives in the fragment rather than the HTTP path.
    path = (parsed.fragment or parsed.path).rstrip("/")
    if not path:
        return ""
    return path.rsplit("/", 1)[-1]


def _fetch_match_venue(
    match_id: str,
    *,
    base_url: str,
    token: str,
    cookie: str,
    timeout: int,
    opener: JsonOpener,
) -> str:
    if not match_id:
        return ""
    response = request_json(
        f"{base_url.rstrip('/')}/match/{quote(match_id, safe='')}/details",
        method="GET",
        token=token,
        cookie=cookie,
        timeout=timeout,
        opener=opener,
    )
    return _venue_from_details(response)


def parse_search_results(
    data: object,
    *,
    source_endpoint: str,
    team_name: str = "",
) -> list[Fixture]:
    """Flatten DFBnet's grouped search response into normalized fixtures.

    When a club search contains several squads, ``team_name`` limits the
    result to matches where the configured team appears on either side.
    """
    if not isinstance(data, list):
        raise DfbnetError("DFBnet match search returned a non-list response")

    team_filter = clean_text(team_name).casefold()
    fixtures: list[Fixture] = []
    seen: set[str] = set()
    for group in data:
        if not isinstance(group, Mapping):
            continue
        competition = group.get("competition")
        competition_data = competition if isinstance(competition, Mapping) else {}
        competition_label = _unique_join([
            _first(competition_data, "leagueName", "leagueLevelName", "teamTypeName"),
            _first(competition_data, "playingAreaName"),
            _first(competition_data, "competitionGroupRoundOrTypeName"),
        ])
        match_type = _first(
            competition_data,
            "competitionGroupRoundOrCompetitionTypeName",
            "competitionTypeName",
            "competitionName",
        )
        matches = group.get("matchReports")
        if not isinstance(matches, list):
            continue
        for match in matches:
            if not isinstance(match, Mapping):
                continue
            match_id = _text(match.get("matchId"))
            home = _text(match.get("homeTeamName"))
            away = _text(match.get("awayTeamName"))
            if not home or not away:
                continue
            if (
                team_filter
                and team_filter not in clean_text(home).casefold()
                and team_filter not in clean_text(away).casefold()
            ):
                continue
            date_iso, time_text = _kickoff_parts(match.get("kickoff"))
            match_number = _text(match.get("matchNumber"))
            key = match_id or f"{date_iso}|{time_text}|{home}|{away}|{match_number}"
            if key in seen:
                continue
            seen.add(key)
            fixture = Fixture(
                date_iso=date_iso,
                time_text=time_text,
                home=home,
                away=away,
                home_team_id=_club_id_from_logo(match.get("homeTeamClubLogoUrl")),
                away_team_id=_club_id_from_logo(match.get("awayTeamClubLogoUrl")),
                competition=competition_label,
                match_type=match_type,
                match_number=match_number,
                venue=_venue_text(match.get("venue") or match.get("matchVenue")),
                match_url=(
                    f"{DFBNET_WEB_BASE}/#/match-report/report/{quote(match_id, safe='')}"
                    if match_id
                    else ""
                ),
                source_endpoint=source_endpoint,
            )
            fixtures.append(fixture)
    return fixtures


def fetch_fixtures(
    *,
    base_url: str = DFBNET_API_BASE,
    club_id: str = "",
    team_name: str = TEAM_NAME,
    team_type_id: str = "",
    competition_type_id: str = "",
    date_from: str,
    date_to: str,
    token: str = "",
    cookie: str = "",
    fetch_venue_details: bool = False,
    timeout: int = 30,
    opener: JsonOpener = urlopen,
) -> tuple[list[Fixture], str, str]:
    """Fetch DFBnet fixtures, resolving the first allowed club when needed."""
    token, cookie = _credential_values(token, cookie)
    base = base_url.rstrip("/")
    resolved_club_id = str(club_id or "").strip()
    if not resolved_club_id:
        values = request_json(
            f"{base}/match/search-values",
            method="POST",
            payload={"searchType": "CLUB"},
            token=token,
            cookie=cookie,
            timeout=timeout,
            opener=opener,
        )
        if not isinstance(values, Mapping):
            raise DfbnetError("DFBnet search-values returned an invalid response")
        clubs = values.get("clubs")
        if not isinstance(clubs, list) or not clubs:
            raise DfbnetError("DFBnet returned no clubs for the authenticated user")
        first_club = next((club for club in clubs if isinstance(club, Mapping)), None)
        resolved_club_id = _text(first_club.get("id")) if first_club else ""
        if not resolved_club_id:
            raise DfbnetError("DFBnet search-values returned a club without an ID")

    endpoint = f"{base}/match/search"
    payload = build_search_payload(
        club_id=resolved_club_id,
        date_from=date_from,
        date_to=date_to,
        team_type_id=team_type_id,
        competition_type_id=competition_type_id,
    )
    response = request_json(
        endpoint,
        method="POST",
        payload=payload,
        token=token,
        cookie=cookie,
        timeout=timeout,
        opener=opener,
    )
    fixtures = parse_search_results(
        response,
        source_endpoint=endpoint,
        team_name=team_name,
    )
    if fetch_venue_details:
        for fixture in fixtures:
            if fixture.venue:
                continue
            match_id = _match_id_from_url(fixture.match_url)
            if not match_id:
                continue
            try:
                fixture.venue = _fetch_match_venue(
                    match_id,
                    base_url=base,
                    token=token,
                    cookie=cookie,
                    timeout=timeout,
                    opener=opener,
                )
            except DfbnetError:
                # Venue/address is optional.  A permissions or deployment
                # difference in the detail endpoint must not drop the match.
                continue
    return fixtures, endpoint, resolved_club_id


def write_metadata(
    path: str | Path,
    fixtures: list[Fixture],
    *,
    endpoint: str,
    base_url: str,
    club_id: str,
    team_name: str,
    date_from: str,
    date_to: str,
    skipped_unscheduled: int,
) -> None:
    """Write a source/audit JSON file without persisting credentials."""
    payload = {
        "source": "DFBnet",
        "source_base": base_url,
        "source_endpoint": endpoint,
        "club_id": club_id,
        "team_name": team_name,
        "date_from": date_from,
        "date_to": date_to,
        "count": len(fixtures),
        "scheduled_count": sum(item.scheduled for item in fixtures),
        "skipped_unscheduled": skipped_unscheduled,
        "fixtures": [asdict(item) for item in fixtures],
    }
    Path(path).write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")


def _validated_dates(date_from: str, date_to: str) -> tuple[str, str]:
    try:
        start = date.fromisoformat(date_from)
        end = date.fromisoformat(date_to)
    except ValueError as exc:
        raise ValueError(f"dates must use YYYY-MM-DD: {exc}") from exc
    if end < start:
        raise ValueError("date-to must not be earlier than date-from")
    return start.isoformat(), end.isoformat()


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--base-url", default=os.environ.get("DFBNET_API_BASE_URL", DFBNET_API_BASE))
    ap.add_argument("--club-id", default=os.environ.get("DFBNET_CLUB_ID", ""))
    ap.add_argument("--team-name", default=os.environ.get("DFBNET_TEAM_NAME", TEAM_NAME))
    ap.add_argument("--team-type-id", default=os.environ.get("DFBNET_TEAM_TYPE_ID", ""))
    ap.add_argument("--competition-type-id", default=os.environ.get("DFBNET_COMPETITION_TYPE_ID", ""))
    ap.add_argument("--date-from", default="", help="YYYY-MM-DD (default: today)")
    ap.add_argument("--date-to", default="", help="YYYY-MM-DD (default: end of current season)")
    ap.add_argument(
        "--home-venue",
        default=os.environ.get("SPIELERPLUS_HOME_VENUE", DEFAULT_HOME_VENUE),
        help="venue text fragment treated as a home game (default: Ernst-Reuter-Sportf)",
    )
    ap.add_argument("--out", default="spielerplus_games.csv")
    ap.add_argument("--json-out", default="dfbnet_fixtures.json")
    ap.add_argument("--xlsx-out", default="", help="optional template-preserving XLSX output")
    ap.add_argument("--template", default="", help="SpielerPlus XLSX template")
    ap.add_argument(
        "--fetch-venue-details",
        action=argparse.BooleanOptionalAction,
        default=True,
        help="fetch match details to populate venue/address fields (default: enabled)",
    )
    ap.add_argument("--timeout", type=int, default=30)
    ap.add_argument(
        "--agent-browser-auth",
        action="store_true",
        help="open a headed agent-browser session and read DFBnet's bearer token when no token is configured",
    )
    ap.add_argument(
        "--session",
        default=os.environ.get("DFBNET_BROWSER_SESSION", DEFAULT_SESSION),
        help="agent-browser session used for interactive DFBnet login",
    )
    ap.add_argument(
        "--profile",
        default=os.environ.get("AGENT_BROWSER_PROFILE", "Default"),
        help="Chrome profile passed to agent-browser",
    )
    ap.add_argument(
        "--auth-timeout",
        type=int,
        default=DEFAULT_TIMEOUT_SECONDS,
        help="seconds to wait for interactive agent-browser login",
    )
    args = ap.parse_args(argv)

    try:
        _, default_to = season_range()
        date_from, date_to = _validated_dates(
            args.date_from or date.today().isoformat(),
            args.date_to or default_to,
        )
        token = os.environ.get("DFBNET_ACCESS_TOKEN", "")
        cookie = os.environ.get("DFBNET_COOKIE", "")
        if args.agent_browser_auth and not token.strip() and not cookie.strip():
            token = fetch_access_token(
                session=args.session,
                profile=args.profile,
                timeout=args.auth_timeout,
            )
        fixtures, endpoint, resolved_club_id = fetch_fixtures(
            base_url=args.base_url,
            club_id=args.club_id,
            team_name=args.team_name,
            team_type_id=args.team_type_id,
            competition_type_id=args.competition_type_id,
            date_from=date_from,
            date_to=date_to,
            token=token,
            cookie=cookie,
            fetch_venue_details=args.fetch_venue_details,
            timeout=args.timeout,
        )
        with open(args.out, "wb") as handle:
            rows, skipped = write_csv(
                handle,
                fixtures,
                team_id=resolved_club_id,
                team_name=args.team_name,
                home_venue=args.home_venue,
            )
        if args.xlsx_out:
            template_path = args.template or str(Path(__file__).resolve().parent.parent / "games_en.xlsx")
            with open(args.xlsx_out, "wb") as handle:
                xlsx_rows, xlsx_skipped = write_xlsx(
                    handle,
                    fixtures,
                    template_path=template_path,
                    team_id=resolved_club_id,
                    team_name=args.team_name,
                    home_venue=args.home_venue,
                )
            if (xlsx_rows, xlsx_skipped) != (rows, skipped):
                raise DfbnetError("CSV and XLSX outputs disagree on fixture rows")
        write_metadata(
            args.json_out,
            fixtures,
            endpoint=endpoint,
            base_url=args.base_url,
            club_id=resolved_club_id,
            team_name=args.team_name,
            date_from=date_from,
            date_to=date_to,
            skipped_unscheduled=skipped,
        )
    except (DfbnetError, DfbnetBrowserAuthError, OSError, ValueError) as exc:
        print(f"[dfbnet] error: {exc}", file=sys.stderr)
        return 1

    print(f"[dfbnet] endpoint: {endpoint}")
    print(f"[dfbnet] fetched {len(fixtures)} fixture(s), wrote {rows} scheduled row(s) -> {args.out}")
    if args.xlsx_out:
        print(f"[dfbnet] template-preserving XLSX -> {args.xlsx_out}")
    if skipped:
        print(f"[dfbnet] skipped {skipped} fixture(s) without a published kickoff time")
    print(f"[dfbnet] details -> {args.json_out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
