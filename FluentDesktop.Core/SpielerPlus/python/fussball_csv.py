#!/usr/bin/env python3
"""Fetch a FUSSBALL.DE team match plan and write a SpielerPlus CSV.

The public team page is backed by a small HTML endpoint.  The endpoint is
deliberately used instead of scraping the rendered browser page because it
returns the complete match-plan table, including venue and match IDs.

The CSV follows the column order and value formats in ``games_en.xlsx``:
semicolon separated, UTF-8 with BOM, ISO dates, and ``HH:MM:SS`` times.  An
optional template-preserving XLSX output is available for accounts whose CSV
validator rejects otherwise valid rows.
"""

from __future__ import annotations

import argparse
from copy import deepcopy
import csv
import io
import json
import os
import re
import sys
from dataclasses import asdict, dataclass
from datetime import date, datetime, time, timedelta
from html import unescape
from html.parser import HTMLParser
from pathlib import Path
from typing import BinaryIO, Iterable, TextIO
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen
from zipfile import ZIP_DEFLATED, ZipFile
from xml.etree import ElementTree as ET


FUSSBALL_BASE = "https://www.fussball.de"
TEAM_PAGE_URL = (
    "https://www.fussball.de/mannschaft/"
    "fc-hertha-03-iii-hertha-03-zehlendorf-berlin/"
    "-/saison/2627/team-id/011MIE2EJK000000VTVG0001VTR8C1K7#!/"
)
TEAM_ID = "011MIE2EJK000000VTVG0001VTR8C1K7"
TEAM_NAME = "FC Hertha 03 III"
DEFAULT_HOME_VENUE = "Ernst-Reuter-Sportf"

CSV_FIELDS = (
    "Game Type \n(Optional)",
    "Opponent",
    "Date",
    "End-Date",
    "Start",
    "Meet \n(Optional)",
    "End \n(Optional)",
    "Home Game",
    "Premises",
    "Adress (Optional)",
    "Infos about the game (Optional)",
    "Participation (Optional)",
    "Nominations (Optional)",
    "Participate until \n(x hours before event)",
    "Participation Reminder \n(x hours before event)",
)

XLSX_MAIN_NS = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"
XLSX_REL_NS = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
XML_NS = "http://www.w3.org/XML/1998/namespace"
_XLSX_Q = f"{{{XLSX_MAIN_NS}}}"
ET.register_namespace("", XLSX_MAIN_NS)
ET.register_namespace("r", XLSX_REL_NS)

DATE_RE = re.compile(r"(?P<date>\d{2}\.\d{2}\.(?:\d{2}|\d{4}))")
TIME_RE = re.compile(r"(?<!\d)(?P<time>(?:[01]\d|2[0-3]):[0-5]\d)(?!\d)")
MATCH_NUMBER_RE = re.compile(r"\b\d{8,10}\b")
TEAM_ID_RE = re.compile(r"/team-id/([A-Z0-9]+)", re.IGNORECASE)
TYPE_RE = re.compile(r"\b(?P<code>ME|PO|FS|TU|HM)\b", re.IGNORECASE)

GAME_TYPE_BY_CODE = {
    "ME": "Game",
    "PO": "Game",
    "FS": "Friendly Game",
    "TU": "Game",
    "HM": "Game",
}
MATCH_LABEL_BY_CODE = {
    "ME": "Meisterschaft",
    "PO": "Pokal",
    "FS": "Freundschaftsspiel",
    "TU": "Turnier",
    "HM": "Hallenturnier",
}


def clean_text(value: str) -> str:
    """Collapse markup whitespace while preserving meaningful punctuation."""
    return re.sub(r"\s+", " ", unescape(value or "")).strip()


def build_matchplan_endpoint(team_id: str, date_from: str, date_to: str) -> str:
    """Build the public FUSSBALL.DE match-plan endpoint."""
    return (
        f"{FUSSBALL_BASE}/ajax.team.matchplan/-/"
        f"datum-von/{date_from}/datum-bis/{date_to}/max/500/"
        "match-type/-1/wettkampftyp/-1/show-venues/true/"
        f"mime-type/HTML/mode/PAGE/team-id/{team_id}"
    )


def season_range(today: date | None = None) -> tuple[str, str]:
    """Return the current football season's July 1 through June 30 range."""
    today = today or date.today()
    start_year = today.year if today.month >= 7 else today.year - 1
    return f"{start_year}-07-01", f"{start_year + 1}-06-30"


@dataclass(slots=True)
class Cell:
    classes: set[str]
    text_parts: list[str]
    anchors: list[tuple[str, str]]

    @property
    def text(self) -> str:
        return clean_text(" ".join(self.text_parts))


@dataclass(slots=True)
class Row:
    classes: set[str]
    cells: list[Cell]


class _TableParser(HTMLParser):
    """Small dependency-free parser for the FUSSBALL.DE match-plan table."""

    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.rows: list[Row] = []
        self._row: Row | None = None
        self._cell: Cell | None = None
        self._anchor_href: str | None = None
        self._anchor_text: list[str] = []

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        attrs_map = {key: value or "" for key, value in attrs}
        if tag == "tr":
            # Malformed pages should not make an otherwise useful table vanish.
            self._finish_cell()
            self._finish_row()
            classes = set((attrs_map.get("class") or "").split())
            self._row = Row(classes=classes, cells=[])
        elif tag in {"td", "th"} and self._row is not None:
            self._finish_cell()
            classes = set((attrs_map.get("class") or "").split())
            self._cell = Cell(classes=classes, text_parts=[], anchors=[])
        elif tag == "a" and self._cell is not None:
            self._anchor_href = attrs_map.get("href") or ""
            self._anchor_text = []

    def handle_startendtag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        self.handle_starttag(tag, attrs)
        self.handle_endtag(tag)

    def handle_data(self, data: str) -> None:
        if self._cell is not None:
            self._cell.text_parts.append(data)
        if self._anchor_href is not None:
            self._anchor_text.append(data)

    def handle_endtag(self, tag: str) -> None:
        if tag == "a" and self._anchor_href is not None:
            if self._cell is not None:
                self._cell.anchors.append((self._anchor_href, clean_text(" ".join(self._anchor_text))))
            self._anchor_href = None
            self._anchor_text = []
        elif tag in {"td", "th"}:
            self._finish_cell()
        elif tag == "tr":
            self._finish_cell()
            self._finish_row()

    def close(self) -> None:
        super().close()
        self._finish_cell()
        self._finish_row()

    def _finish_cell(self) -> None:
        if self._cell is not None and self._row is not None:
            self._row.cells.append(self._cell)
        self._cell = None
        self._anchor_href = None
        self._anchor_text = []

    def _finish_row(self) -> None:
        if self._row is not None:
            self.rows.append(self._row)
        self._row = None


@dataclass(slots=True)
class Fixture:
    """One row from the FUSSBALL.DE match plan."""

    date_iso: str
    time_text: str
    home: str
    away: str
    home_team_id: str
    away_team_id: str
    competition: str
    match_type: str
    match_number: str
    venue: str
    match_url: str
    source_endpoint: str = ""

    @property
    def scheduled(self) -> bool:
        return bool(self.date_iso and self.time_text)


def _cell_with_class(row: Row, class_name: str) -> Cell | None:
    return next((cell for cell in row.cells if class_name in cell.classes), None)


def _parse_date(value: str) -> str:
    match = DATE_RE.search(value)
    if not match:
        return ""
    raw = match.group("date")
    fmt = "%d.%m.%Y" if len(raw.rsplit(".", 1)[1]) == 4 else "%d.%m.%y"
    try:
        return datetime.strptime(raw, fmt).date().isoformat()
    except ValueError:
        return ""


def _parse_time(value: str) -> str:
    match = TIME_RE.search(value)
    return match.group("time") if match else ""


def _team_name_and_id(cell: Cell) -> tuple[str, str]:
    anchor = next((item for item in cell.anchors if "/team-id/" in item[0]), None)
    if anchor is None:
        return cell.text, ""
    match = TEAM_ID_RE.search(anchor[0])
    return (anchor[1] or cell.text), (match.group(1).upper() if match else "")


def _dedupe(fixtures: Iterable[Fixture]) -> list[Fixture]:
    result: list[Fixture] = []
    seen: set[str] = set()
    for fixture in fixtures:
        key = fixture.match_number or fixture.match_url or (
            f"{fixture.date_iso}|{fixture.time_text}|{fixture.home}|{fixture.away}"
        )
        if key in seen:
            continue
        seen.add(key)
        result.append(fixture)
    return sorted(result, key=lambda item: (item.date_iso or "9999-99-99", item.time_text, item.home, item.away))


def parse_matchplan_html(html: str, *, team_id: str = TEAM_ID, source_endpoint: str = "") -> list[Fixture]:
    """Parse the endpoint's table into normalized fixtures.

    FUSSBALL.DE uses two-digit years in ``.column-date`` (for example
    ``13.09.26``), while the mobile headline uses four-digit years.  Both
    forms are accepted so the parser remains useful if markup changes.
    """
    parser = _TableParser()
    parser.feed(html)
    parser.close()
    fixtures: list[Fixture] = []

    for index, row in enumerate(parser.rows):
        if "row-competition" not in row.classes:
            continue
        date_cell = _cell_with_class(row, "column-date")
        date_iso = _parse_date(date_cell.text if date_cell else "")
        time_text = _parse_time(date_cell.text if date_cell else "")
        if not date_iso:
            continue

        team_cell = _cell_with_class(row, "column-team")
        competition = team_cell.text if team_cell else ""
        info_text = " ".join(cell.text for cell in row.cells)
        type_match = TYPE_RE.search(info_text)
        type_code = type_match.group("code").upper() if type_match else ""
        match_number_match = MATCH_NUMBER_RE.search(info_text)
        match_number = match_number_match.group(0) if match_number_match else ""
        match_type = MATCH_LABEL_BY_CODE.get(type_code, "")

        club_names: list[str] = []
        club_ids: list[str] = []
        venue = ""
        match_url = ""
        cursor = index + 1
        while cursor < len(parser.rows) and "row-competition" not in parser.rows[cursor].classes:
            next_row = parser.rows[cursor]
            for cell in next_row.cells:
                if "column-club" in cell.classes:
                    # A bye is rendered as a second ``column-club`` with
                    # ``spielfrei`` text but no team link.  It is not an
                    # importable game, so only linked clubs count here.
                    if not any("/team-id/" in href for href, _ in cell.anchors):
                        continue
                    name, found_id = _team_name_and_id(cell)
                    if name and name not in club_names:
                        club_names.append(name)
                        club_ids.append(found_id)
                if "column-score" in cell.classes or "column-detail" in cell.classes:
                    for href, _ in cell.anchors:
                        if re.search(r"/spiel/|/match/", href, re.IGNORECASE):
                            match_url = href
                            break
            if "row-venue" in next_row.classes and not venue:
                venue = clean_text(" ".join(cell.text for cell in next_row.cells))
            cursor += 1

        # Do not emit unrelated rows when the source page contains a headline
        # or other table after a match-plan row.
        if len(club_names) < 2:
            continue
        fixtures.append(Fixture(
            date_iso=date_iso,
            time_text=time_text,
            home=club_names[0],
            away=club_names[1],
            home_team_id=club_ids[0] if club_ids else "",
            away_team_id=club_ids[1] if len(club_ids) > 1 else "",
            competition=competition,
            match_type=match_type,
            match_number=match_number,
            venue=venue,
            match_url=match_url,
            source_endpoint=source_endpoint,
        ))
    return _dedupe(fixtures)


def _team_is_home(fixture: Fixture, team_id: str, team_name: str = TEAM_NAME) -> bool | None:
    if team_id and fixture.home_team_id and fixture.home_team_id.upper() == team_id.upper():
        return True
    if team_id and fixture.away_team_id and fixture.away_team_id.upper() == team_id.upper():
        return False
    normalized = clean_text(team_name).casefold()
    home_match = normalized and normalized in clean_text(fixture.home).casefold()
    away_match = normalized and normalized in clean_text(fixture.away).casefold()
    if home_match and not away_match:
        return True
    if away_match and not home_match:
        return False
    return None


def _venue_is_home(fixture: Fixture, home_venue: str) -> bool | None:
    """Return the configured venue-based home flag when venue data is present."""
    marker = clean_text(home_venue).casefold()
    venue = clean_text(fixture.venue).casefold()
    if not marker or not venue:
        return None
    return marker in venue


def _time_value(value: str) -> time:
    return datetime.strptime(value, "%H:%M").time()


def fixture_to_csv_row(
    fixture: Fixture,
    *,
    team_id: str = TEAM_ID,
    team_name: str = TEAM_NAME,
    home_venue: str = "",
    duration_minutes: int = 75,
    participate_until_hours: int = 24,
    reminder_hours: int = 48,
) -> dict[str, str]:
    """Convert one scheduled fixture to the official SpielerPlus columns."""
    if not fixture.scheduled:
        raise ValueError(f"fixture {fixture.date_iso} has no published kickoff time")
    if duration_minutes <= 0:
        raise ValueError("duration_minutes must be positive")
    if participate_until_hours < 0 or reminder_hours < 0:
        raise ValueError("reminder offsets cannot be negative")

    source_home = _team_is_home(fixture, team_id, team_name)
    if source_home is None:
        raise ValueError(f"cannot determine home/away for {fixture.home!r} vs {fixture.away!r}")
    home_game = _venue_is_home(fixture, home_venue)
    if home_game is None:
        home_game = source_home
    # The venue can override the SpielerPlus home flag for a relocated or
    # neutral fixture, but the opponent still comes from the source team side.
    opponent = fixture.away if source_home else fixture.home
    start_dt = datetime.combine(date.fromisoformat(fixture.date_iso), _time_value(fixture.time_text))
    end_dt = start_dt + timedelta(minutes=duration_minutes)
    game_type = GAME_TYPE_BY_CODE.get(
        next((code for code, label in MATCH_LABEL_BY_CODE.items() if label == fixture.match_type), ""),
        "Game",
    )
    info_parts = [part for part in (fixture.competition, fixture.match_type) if part]
    if fixture.venue:
        info_parts.append(f"Venue: {fixture.venue}")
    # Keep the import description compact and compatible with SpielerPlus's
    # source validator.  The complete match URL remains in
    # ``fussball_fixtures.json`` for audit/preview purposes.

    return {
        "Game Type \n(Optional)": game_type,
        "Opponent": opponent,
        "Date": start_dt.date().isoformat(),
        "End-Date": end_dt.date().isoformat(),
        "Start": start_dt.strftime("%H:%M:%S"),
        "Meet \n(Optional)": "",
        "End \n(Optional)": end_dt.strftime("%H:%M:%S"),
        "Home Game": "yes" if home_game else "no",
        "Premises": "",
        # SpielerPlus validates this field against the team's configured
        # locations.  Keep the complete source venue here so both the
        # FUSSBALL.DE and DFBnet pipelines can import a known address.
        "Adress (Optional)": fixture.venue,
        "Infos about the game (Optional)": " | ".join(info_parts),
        "Participation (Optional)": "Players must confirm",
        "Nominations (Optional)": "",
        "Participate until \n(x hours before event)": str(participate_until_hours),
        "Participation Reminder \n(x hours before event)": str(reminder_hours),
    }


def rows_for_fixtures(
    fixtures: Iterable[Fixture],
    *,
    team_id: str = TEAM_ID,
    team_name: str = TEAM_NAME,
    home_venue: str = "",
    duration_minutes: int = 75,
    participate_until_hours: int = 24,
    reminder_hours: int = 48,
) -> tuple[list[dict[str, str]], int]:
    """Return importable rows and the number of unscheduled fixtures skipped."""
    rows: list[dict[str, str]] = []
    skipped = 0
    for fixture in fixtures:
        if not fixture.scheduled:
            skipped += 1
            continue
        rows.append(fixture_to_csv_row(
            fixture,
            team_id=team_id,
            team_name=team_name,
            home_venue=home_venue,
            duration_minutes=duration_minutes,
            participate_until_hours=participate_until_hours,
            reminder_hours=reminder_hours,
        ))
    return rows, skipped


def write_csv(
    target: BinaryIO | TextIO,
    fixtures: Iterable[Fixture],
    *,
    team_id: str = TEAM_ID,
    team_name: str = TEAM_NAME,
    home_venue: str = "",
    delimiter: str = ";",
) -> tuple[int, int]:
    """Write fixtures to a UTF-8-BOM CSV and return ``(rows, skipped)``."""
    rows, skipped = rows_for_fixtures(
        fixtures,
        team_id=team_id,
        team_name=team_name,
        home_venue=home_venue,
    )
    if isinstance(target, io.BytesIO) or "b" in getattr(target, "mode", ""):
        wrapper = io.TextIOWrapper(target, encoding="utf-8-sig", newline="", write_through=True)
        writer = csv.DictWriter(wrapper, fieldnames=CSV_FIELDS, delimiter=delimiter, lineterminator="\r\n")
        writer.writeheader()
        writer.writerows(rows)
        wrapper.flush()
        wrapper.detach()
    else:
        # A caller opening a text file should use utf-8-sig itself; this branch
        # keeps the function convenient for StringIO-based tests/callers.
        writer = csv.DictWriter(target, fieldnames=CSV_FIELDS, delimiter=delimiter, lineterminator="\r\n")
        writer.writeheader()
        writer.writerows(rows)
    return len(rows), skipped


def _xlsx_serial(value: date) -> str:
    """Return an Excel 1900-date-system serial for an ISO date."""
    return str((value - date(1899, 12, 30)).days)


def _xlsx_time_fraction(value: time) -> str:
    """Return an Excel day fraction for a time-of-day value."""
    seconds = value.hour * 3600 + value.minute * 60 + value.second
    return f"{seconds / 86400:.17g}"


def _shared_string_values(root: ET.Element) -> tuple[list[str], dict[str, int]]:
    values: list[str] = []
    indexes: dict[str, int] = {}
    for si in root.findall(f"{_XLSX_Q}si"):
        value = "".join(t.text or "" for t in si.findall(f".//{_XLSX_Q}t"))
        indexes.setdefault(value, len(values))
        values.append(value)
    return values, indexes


def _shared_string_index(
    root: ET.Element,
    values: list[str],
    indexes: dict[str, int],
    value: str,
) -> int:
    existing = indexes.get(value)
    if existing is not None:
        return existing
    si = ET.SubElement(root, f"{_XLSX_Q}si")
    text = ET.SubElement(si, f"{_XLSX_Q}t")
    if value[:1].isspace() or value[-1:].isspace():
        text.set(f"{{{XML_NS}}}space", "preserve")
    text.text = value
    index = len(values)
    values.append(value)
    indexes[value] = index
    return index


def _set_xlsx_cell(
    cell: ET.Element,
    *,
    ref: str,
    style: str | None,
    value: str,
    kind: str,
    shared_root: ET.Element,
    shared_values: list[str],
    shared_indexes: dict[str, int],
) -> None:
    cell.clear()
    cell.set("r", ref)
    if style:
        cell.set("s", style)
    if value == "":
        return
    if kind == "string":
        cell.set("t", "s")
        scalar = str(_shared_string_index(shared_root, shared_values, shared_indexes, value))
    else:
        scalar = value
    ET.SubElement(cell, f"{_XLSX_Q}v").text = scalar


def _xlsx_sheet_with_rows(
    sheet_root: ET.Element,
    shared_root: ET.Element,
    rows: list[dict[str, str]],
) -> None:
    sheet_data = sheet_root.find(f"{_XLSX_Q}sheetData")
    if sheet_data is None:
        raise ValueError("template worksheet has no sheetData")
    template_rows = sheet_data.findall(f"{_XLSX_Q}row")
    if not template_rows:
        raise ValueError("template worksheet has no header row")
    header_row = deepcopy(template_rows[0])
    row_template = deepcopy(template_rows[1] if len(template_rows) > 1 else template_rows[0])
    template_cells = {
        cell.get("r", "")[:-1]: cell
        for cell in row_template.findall(f"{_XLSX_Q}c")
        if cell.get("r")
    }
    columns = tuple("ABCDEFGHIJKLMNO")
    field_kinds = {
        columns[index]: "string"
        for index in range(len(columns))
    }
    for column in ("C", "D"):
        field_kinds[column] = "date"
    for column in ("E", "F", "G"):
        field_kinds[column] = "time"
    for column in ("N", "O"):
        field_kinds[column] = "number"

    shared_values, shared_indexes = _shared_string_values(shared_root)
    sheet_data[:] = [header_row]
    for row_number, row_values in enumerate(rows, start=2):
        row_element = deepcopy(row_template)
        row_element.set("r", str(row_number))
        row_element.set("spans", "1:15")
        row_element[:] = []
        for column, field in zip(columns, CSV_FIELDS):
            template_cell = template_cells.get(column)
            cell = deepcopy(template_cell) if template_cell is not None else ET.Element(f"{_XLSX_Q}c")
            kind = field_kinds[column]
            value = row_values.get(field, "")
            scalar = value
            if kind == "date" and value:
                scalar = _xlsx_serial(date.fromisoformat(value))
            elif kind == "time" and value:
                scalar = _xlsx_time_fraction(datetime.strptime(value, "%H:%M:%S").time())
            _set_xlsx_cell(
                cell,
                ref=f"{column}{row_number}",
                style=template_cell.get("s") if template_cell is not None else None,
                value=scalar,
                kind="string" if kind == "string" else "number",
                shared_root=shared_root,
                shared_values=shared_values,
                shared_indexes=shared_indexes,
            )
            row_element.append(cell)
        sheet_data.append(row_element)
    dimension = sheet_root.find(f"{_XLSX_Q}dimension")
    if dimension is not None:
        dimension.set("ref", f"A1:O{max(1, len(rows) + 1)}")
    shared_items = shared_root.findall(f"{_XLSX_Q}si")
    shared_root.set("count", str(sum(
        1 for cell in sheet_root.findall(f".//{_XLSX_Q}c") if cell.get("t") == "s"
    )))
    shared_root.set("uniqueCount", str(len(shared_items)))


def write_xlsx(
    target: BinaryIO,
    fixtures: Iterable[Fixture],
    *,
    template_path: str | Path,
    team_id: str = TEAM_ID,
    team_name: str = TEAM_NAME,
    home_venue: str = "",
) -> tuple[int, int]:
    """Populate the supplied SpielerPlus workbook template with fixtures.

    The site advertises CSV and Excel uploads, but the logged-in CSV validator
    currently rejects otherwise valid rows for this team.  Keeping this
    template-preserving fallback lets callers use the same generated data in
    the XLSX path without adding a third-party spreadsheet dependency.
    """
    rows, skipped = rows_for_fixtures(
        fixtures,
        team_id=team_id,
        team_name=team_name,
        home_venue=home_venue,
    )
    template = Path(template_path)
    if not template.is_file():
        raise FileNotFoundError(f"SpielerPlus template not found: {template}")
    with ZipFile(template, "r") as source:
        members = {info.filename: (info, source.read(info.filename)) for info in source.infolist()}
    sheet_name = "xl/worksheets/sheet1.xml"
    shared_name = "xl/sharedStrings.xml"
    if sheet_name not in members or shared_name not in members:
        raise ValueError("SpielerPlus template is missing the first worksheet/shared strings")
    sheet_root = ET.fromstring(members[sheet_name][1])
    shared_root = ET.fromstring(members[shared_name][1])
    _xlsx_sheet_with_rows(sheet_root, shared_root, rows)
    sheet_bytes = ET.tostring(sheet_root, encoding="utf-8", xml_declaration=True)
    shared_bytes = ET.tostring(shared_root, encoding="utf-8", xml_declaration=True)
    with ZipFile(target, "w", compression=ZIP_DEFLATED) as destination:
        for filename, (info, data) in members.items():
            if filename == sheet_name:
                data = sheet_bytes
            elif filename == shared_name:
                data = shared_bytes
            destination.writestr(info, data)
    return len(rows), skipped


def write_json(path: str | Path, fixtures: Iterable[Fixture], *, page_url: str, endpoint: str, date_from: str, date_to: str, skipped_unscheduled: int) -> None:
    fixtures_list = list(fixtures)
    payload = {
        "source_page": page_url,
        "source_endpoint": endpoint,
        "date_from": date_from,
        "date_to": date_to,
        "count": len(fixtures_list),
        "scheduled_count": sum(item.scheduled for item in fixtures_list),
        "skipped_unscheduled": skipped_unscheduled,
        "fixtures": [asdict(item) for item in fixtures_list],
    }
    Path(path).write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")


def fetch_html(url: str, *, referer: str = "", timeout: int = 30) -> str:
    request = Request(
        url,
        headers={
            "User-Agent": "spielerplus-ics-importer/1.0",
            "Accept-Language": "de-DE,de;q=0.9,en;q=0.6",
            "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
            **({"Referer": referer} if referer else {}),
        },
    )
    try:
        with urlopen(request, timeout=timeout) as response:
            raw = response.read()
            charset = response.headers.get_content_charset() or "utf-8"
    except (HTTPError, URLError, TimeoutError) as exc:
        raise RuntimeError(f"FUSSBALL.DE request failed: {url}: {exc}") from exc
    return raw.decode(charset, errors="replace")


def fetch_fixtures(
    page_url: str = TEAM_PAGE_URL,
    *,
    team_id: str = TEAM_ID,
    date_from: str,
    date_to: str,
    timeout: int = 30,
) -> tuple[list[Fixture], str]:
    """Fetch the public team page's match-plan endpoint."""
    # Fetch the requested page first so the source remains auditable and the
    # endpoint can be checked against the team URL's ID.
    page_html = fetch_html(page_url, timeout=timeout)
    page_team_id = TEAM_ID_RE.search(page_url)
    resolved_team_id = (page_team_id.group(1).upper() if page_team_id else team_id.upper())
    if resolved_team_id != team_id.upper():
        raise RuntimeError(f"team ID mismatch: page={resolved_team_id}, requested={team_id.upper()}")
    endpoint = build_matchplan_endpoint(resolved_team_id, date_from, date_to)
    matchplan_html = fetch_html(endpoint, referer=page_url, timeout=timeout)
    fixtures = parse_matchplan_html(matchplan_html, team_id=resolved_team_id, source_endpoint=endpoint)
    # Keep a useful fallback if the AJAX response is unavailable but the full
    # page contains the same table (some CDN responses inline it).
    if not fixtures:
        fixtures = parse_matchplan_html(page_html, team_id=resolved_team_id, source_endpoint=endpoint)
    if not fixtures:
        raise RuntimeError("FUSSBALL.DE returned no recognizable fixtures")
    return fixtures, endpoint


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--url", default=TEAM_PAGE_URL, help="FUSSBALL.DE team page")
    ap.add_argument("--team-id", default=TEAM_ID)
    ap.add_argument("--date-from", default="", help="YYYY-MM-DD (default: today)")
    ap.add_argument("--date-to", default="", help="YYYY-MM-DD (default: end of current season)")
    ap.add_argument(
        "--home-venue",
        default=os.environ.get("SPIELERPLUS_HOME_VENUE", DEFAULT_HOME_VENUE),
        help="venue text fragment treated as a home game (default: Ernst-Reuter-Sportf)",
    )
    ap.add_argument("--out", default="spielerplus_games.csv")
    ap.add_argument("--json-out", default="fussball_fixtures.json")
    ap.add_argument(
        "--xlsx-out",
        default="",
        help="optional template-preserving XLSX output (requires games_en.xlsx)",
    )
    ap.add_argument(
        "--template",
        default="",
        help="SpielerPlus XLSX template (default: ../games_en.xlsx next to this script)",
    )
    ap.add_argument("--timeout", type=int, default=30)
    args = ap.parse_args(argv)

    default_from, default_to = season_range()
    date_from = args.date_from or date.today().isoformat()
    date_to = args.date_to or default_to
    try:
        date.fromisoformat(date_from)
        date.fromisoformat(date_to)
    except ValueError as exc:
        ap.error(f"dates must use YYYY-MM-DD: {exc}")

    try:
        fixtures, endpoint = fetch_fixtures(
            args.url,
            team_id=args.team_id,
            date_from=date_from,
            date_to=date_to,
            timeout=args.timeout,
        )
        with open(args.out, "wb") as handle:
            rows, skipped = write_csv(
                handle,
                fixtures,
                team_id=args.team_id,
                home_venue=args.home_venue,
            )
        if args.xlsx_out:
            template_path = args.template or str(Path(__file__).resolve().parent.parent / "games_en.xlsx")
            with open(args.xlsx_out, "wb") as handle:
                xlsx_rows, xlsx_skipped = write_xlsx(
                    handle,
                    fixtures,
                    template_path=template_path,
                    team_id=args.team_id,
                    home_venue=args.home_venue,
                )
            if (xlsx_rows, xlsx_skipped) != (rows, skipped):
                raise RuntimeError("CSV and XLSX outputs disagree on fixture rows")
        write_json(
            args.json_out,
            fixtures,
            page_url=args.url,
            endpoint=endpoint,
            date_from=date_from,
            date_to=date_to,
            skipped_unscheduled=skipped,
        )
    except (OSError, RuntimeError, ValueError) as exc:
        print(f"[fussball] error: {exc}", file=sys.stderr)
        return 1

    print(f"[fussball] endpoint: {endpoint}")
    print(f"[fussball] fetched {len(fixtures)} fixture(s), wrote {rows} scheduled row(s) -> {args.out}")
    if args.xlsx_out:
        print(f"[fussball] template-preserving XLSX -> {args.xlsx_out}")
    if skipped:
        print(f"[fussball] skipped {skipped} fixture(s) without a published kickoff time")
    print(f"[fussball] details -> {args.json_out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
