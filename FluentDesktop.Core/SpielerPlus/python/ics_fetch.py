#!/usr/bin/env python3
"""Fetch + parse the public Google ICS feed (stdlib only).

Usage:
  python3 ics_fetch.py --url <ics-url> --out events.json [--from-date YYYY-MM-DD]
                       [--include game,training,birthday,event,holiday,all]
                       [--team-hint "FC Hertha 03"] [--stdout]

Output JSON: list of normalized events sorted by start, each with:
  uid, kind (game|training|birthday|event|holiday), summary, opponent,
  home_away (home|away|unknown), start/end in Europe/Berlin + UTC,
  spielerplus date/time save+display formats, location, description.
"""
import argparse
import json
import re
import sys
import urllib.request
from datetime import datetime, timezone
from zoneinfo import ZoneInfo

BERLIN = ZoneInfo("Europe/Berlin")
UTC = timezone.utc

FOOTBALL_HINTS = ("FC ", "SC ", "SV ", "SFC ", "Viktoria", "Hertha", "Kickers",
                  "Borussia", "Staaken", "Wannsee", "Spandau", "Siemensstadt",
                  "Stern", "Tennis", "Steglitzer", "Lichtenrader", "U13", "U11",
                  "D-Junioren", "Kreisklasse", "Spiel")


def unfold(text: str) -> str:
    # normalize CRLF -> LF first, then unfold continuation lines
    text = text.replace("\r\n", "\n").replace("\r", "\n")
    return re.sub(r"\n[ \t]", "", text)


def get_prop(block: str, name: str):
    m = re.search(r"^" + re.escape(name) + r"(?:;([^:]*))?:(.*)$", block,
                  re.M)
    if not m:
        return None, {}
    params_raw, value = m.group(1) or "", m.group(2).strip()
    params = {}
    for part in params_raw.split(";"):
        if "=" in part:
            k, v = part.split("=", 1)
            params[k] = v
    return value, params


def parse_dt(value: str, params: dict):
    """Return aware datetime. DATE values -> midnight Berlin."""
    if params.get("VALUE") == "DATE":
        d = datetime.strptime(value.strip(), "%Y%m%d").replace(tzinfo=BERLIN)
        return d, True
    v = value.strip()
    if v.endswith("Z"):
        d = datetime.strptime(v, "%Y%m%dT%H%M%SZ").replace(tzinfo=UTC)
        return d.astimezone(BERLIN), False
    tzid = params.get("TZID")
    tz = ZoneInfo(tzid) if tzid else BERLIN
    for fmt in ("%Y%m%dT%H%M%S", "%Y%m%dT%H%M", "%Y%m%d"):
        try:
            d = datetime.strptime(v, fmt).replace(tzinfo=tz)
            return d, (fmt == "%Y%m%d")
        except ValueError:
            continue
    raise ValueError(f"unparseable DTSTART value: {value!r} params={params}")


def unescape(s: str) -> str:
    return (s.replace("\\,", ",").replace("\\;", ";").replace("\\n", "\n")
             .replace("\\N", "\n").replace("\\\\", "\\")).strip()


def classify(summary: str, transp: str) -> str:
    s = summary.strip()
    if s.startswith("[Spiel]") or ((" - " in s) and any(h in s for h in FOOTBALL_HINTS)):
        return "game"
    if s.startswith("Training"):
        return "training"
    if s.startswith("Geburtstag"):
        return "birthday"
    if any(k in s for k in ("ferien", "Ferien", "Feiertag", "Weihnacht", "Ostern", "Pfingst")):
        return "holiday"
    if transp == "TRANSPARENT" and len(s) <= 4:
        return "holiday"
    return "event"


def parse_opponent(summary: str, team_hint: str):
    """Split 'A - B' into opponent + home/away relative to team_hint."""
    s = summary.strip()
    s = re.sub(r"^\[Spiel\]\s*", "", s)
    # strip trailing age-group parens for opponent cleanliness is done per-side
    if " - " not in s:
        return s, "unknown"
    left, right = [p.strip() for p in s.split(" - ", 1)]
    # remove trailing "(...)" only for comparison, keep full for display
    has_hint = lambda p: team_hint.lower() in p.lower()
    if has_hint(left) and not has_hint(right):
        return right, "home"
    if has_hint(right) and not has_hint(left):
        return left, "away"
    if has_hint(left) and has_hint(right):
        return s, "unknown"  # derby / internal
    return s, "unknown"


def fmt_disp_date(d: datetime) -> str:
    return d.strftime("%m/%d/%Y")


def fmt_disp_time(d: datetime) -> str:
    return d.strftime("%I:%M %p")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", required=True)
    ap.add_argument("--out", default="events.json")
    ap.add_argument("--from-date", default="",
                    help="YYYY-MM-DD, drop events ending before this (Berlin)")
    ap.add_argument("--include", default="game,birthday,event",
                    help="comma list of kinds or 'all'")
    ap.add_argument("--team-hint", default="FC Hertha 03")
    ap.add_argument("--timeout", type=int, default=30)
    ap.add_argument("--stdout", action="store_true")
    args = ap.parse_args()

    req = urllib.request.Request(args.url, headers={"User-Agent": "spielerplus-ics-importer/1.0"})
    with urllib.request.urlopen(req, timeout=args.timeout) as r:
        raw = r.read().decode("utf-8", errors="replace")
    text = unfold(raw)
    blocks = re.findall(r"BEGIN:VEVENT(.*?)END:VEVENT", text, re.S)

    include = {p.strip().lower() for p in args.include.split(",") if p.strip()}
    want_all = "all" in include

    from_date = None
    if args.from_date:
        from_date = datetime.strptime(args.from_date, "%Y-%m-%d").replace(tzinfo=BERLIN)

    events = []
    skipped = {"past": 0, "cancelled": 0, "excluded_kind": 0, "recurrence_instance": 0}
    for b in blocks:
        summary, _ = get_prop(b, "SUMMARY")
        dtstart_v, dtstart_p = get_prop(b, "DTSTART")
        if not summary or not dtstart_v:
            continue
        summary = unescape(summary)
        uid, _ = get_prop(b, "UID")
        status, _ = get_prop(b, "STATUS")
        transp, _ = get_prop(b, "TRANSP")
        loc, _ = get_prop(b, "LOCATION")
        desc, _ = get_prop(b, "DESCRIPTION")
        dtend_v, dtend_p = get_prop(b, "DTEND")
        rrule, _ = get_prop(b, "RRULE")
        recid, _ = get_prop(b, "RECURRENCE-ID")
        transp = (transp or "").strip()
        status = (status or "").strip()
        if status == "CANCELLED":
            skipped["cancelled"] += 1
            continue
        try:
            start_berlin, all_day = parse_dt(dtstart_v, dtstart_p)
        except ValueError as e:
            print(f"warn: skip unparseable: {summary[:60]} ({e})", file=sys.stderr)
            continue
        if dtend_v:
            try:
                end_berlin, _ = parse_dt(dtend_v, dtend_p)
            except ValueError:
                end_berlin = start_berlin
        else:
            end_berlin = start_berlin
        if from_date and end_berlin < from_date:
            skipped["past"] += 1
            continue
        kind = classify(summary, transp)
        # declined single training instances are TRANSPARENT + past pattern; keep but mark
        if not want_all and kind not in include:
            skipped["excluded_kind"] += 1
            continue
        opponent, home_away = parse_opponent(summary, args.team_hint) if kind == "game" else ("", "unknown")
        # default durations: games 75min if missing handled by ICS already; keep as-is
        ev = {
            "uid": (uid or "").strip(),
            "kind": kind,
            "summary": summary,
            "opponent": opponent,
            "home_away": home_away,  # home|away|unknown
            "all_day": all_day,
            "start_berlin": start_berlin.isoformat(),
            "end_berlin": end_berlin.isoformat(),
            "start_utc": start_berlin.astimezone(UTC).isoformat(),
            "end_utc": end_berlin.astimezone(UTC).isoformat(),
            # SpielerPlus save formats (hidden inputs)
            "sp_startdate": start_berlin.strftime("%Y-%m-%d"),
            "sp_enddate": end_berlin.strftime("%Y-%m-%d"),
            "sp_starttime": start_berlin.strftime("%H:%M:00"),
            "sp_endtime": end_berlin.strftime("%H:%M:00"),
            # display formats (visible inputs)
            "sp_startdate_disp": fmt_disp_date(start_berlin),
            "sp_enddate_disp": fmt_disp_date(end_berlin),
            "sp_starttime_disp": fmt_disp_time(start_berlin),
            "sp_endtime_disp": fmt_disp_time(end_berlin),
            "location": unescape(loc) if loc else "",
            "description": unescape(desc) if desc else "",
            "status": status,
            "transp": transp,
            "recurrence": bool(rrule),
            "recurrence_instance": bool(recid),
        }
        events.append(ev)

    events.sort(key=lambda e: e["start_berlin"])
    payload = {"source": args.url, "count": len(events), "skipped": skipped,
               "events": events}
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(payload, f, ensure_ascii=False, indent=2)
    if args.stdout:
        json.dump(payload, sys.stdout, ensure_ascii=False, indent=2)
        sys.stdout.write("\n")
    print(f"fetched {len(blocks)} VEVENTs -> {len(events)} importable "
          f"(skipped={skipped}) -> {args.out}")
    by_kind: dict = {}
    for e in events:
        by_kind[e["kind"]] = by_kind.get(e["kind"], 0) + 1
    print(f"kinds: {by_kind}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
