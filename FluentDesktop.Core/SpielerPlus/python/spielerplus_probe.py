#!/usr/bin/env python3
"""Dump existing SpielerPlus calendar events for dedupe (via agent-browser).

Uses the Default Chrome profile (logged-in) + a named agent-browser session.
Fetches the calendar page HTML and extracts its server-injected FullCalendar
events array.

Usage:
  AGENT_BROWSER_PROFILE=Default python3 spielerplus_probe.py \
      --session spielerplus-ics --out existing.json [--calendar-url URL] [--headed]

Output: {"count": N, "events": [{title, start, url, id, kind}]}
"""
import argparse
from datetime import date
import json
import os
import re
import shutil
import subprocess
import sys

from browser_open import open_url as _verified_open
from browser_open import run_captured as _run_captured

_FULLCALENDAR_ASSIGNMENT = re.compile(
    r"\bvar\s+fullcalendar_[A-Za-z0-9_$]+\s*=\s*", re.IGNORECASE
)
_EVENTS_PROPERTY = re.compile(r'"events"\s*:\s*', re.IGNORECASE)


def extract_calendar_events(html: str) -> list[dict]:
    """Extract the server-injected FullCalendar events array from page HTML.

    SpielerPlus renders the calendar configuration as JavaScript rather than
    requesting event objects from a separate calendar API.  The value after
    the ``"events"`` property is valid JSON even though the surrounding
    configuration also contains JavaScript callback functions, so decode only
    that array instead of evaluating page code.
    """
    if not isinstance(html, str):
        raise TypeError("calendar HTML must be a string")

    decoder = json.JSONDecoder()
    for assignment in _FULLCALENDAR_ASSIGNMENT.finditer(html):
        prop = _EVENTS_PROPERTY.search(html, assignment.end())
        if not prop:
            continue
        array_text = html[prop.end():].lstrip()
        try:
            events, _ = decoder.raw_decode(array_text)
        except json.JSONDecodeError:
            continue
        if isinstance(events, list):
            return events

    raise ValueError("FullCalendar events array not found in calendar HTML")


def _html_from_agent_browser_output(raw: str) -> str:
    """Return HTML from either raw or JSON-stringified CLI output."""
    text = (raw or "").strip()
    if text.startswith('"') and text.endswith('"'):
        try:
            decoded = json.loads(text)
        except json.JSONDecodeError:
            pass
        else:
            if isinstance(decoded, str):
                return decoded
    return text


def _agent_browser_bin() -> str:
    """Resolve the agent-browser CLI (Windows .CMD shim aware)."""
    return shutil.which("agent-browser") or "agent-browser"


def run(cmd, env, timeout=None):
    return _run_captured(cmd, env=env, timeout=timeout)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--session", default="spielerplus-ics")
    ap.add_argument("--profile", default=os.environ.get("AGENT_BROWSER_PROFILE", "Default"))
    ap.add_argument("--calendar-url",
                    default=f"https://www.spielerplus.de/events/calendar?date={date.today().isoformat()}")
    ap.add_argument("--out", default="existing.json")
    ap.add_argument("--headed", action="store_true",
                    help="show browser window (default headless)")
    ap.add_argument("--timeout", type=int, default=90)
    args = ap.parse_args()

    env = dict(os.environ)
    env["AGENT_BROWSER_PROFILE"] = args.profile
    base = [_agent_browser_bin(), "--session", args.session]
    if args.headed:
        base.append("--headed")

    print(f"[probe] profile={args.profile} session={args.session}")
    print(f"[probe] open {args.calendar_url}")
    p = _verified_open(
        base + ["open", args.calendar_url],
        session_base=base,
        env=env,
    )
    if p.returncode != 0:
        print(p.stdout[-2000:] if p.stdout else "", file=sys.stderr)
        print(p.stderr[-2000:] if p.stderr else "", file=sys.stderr)
        return 1

    p = run(base + ["wait", "--load", "networkidle"], env)
    # best-effort; ignore failure
    p = run(base + ["get", "html", "html"], env)
    html = _html_from_agent_browser_output(p.stdout)
    if p.returncode != 0 or not html:
        print("[probe] calendar HTML fetch failed", file=sys.stderr)
        print((p.stdout or "")[-2000:], file=sys.stderr)
        print((p.stderr or "")[-2000:], file=sys.stderr)
        return 1
    try:
        events = extract_calendar_events(html)
    except Exception as e:
        print(f"[probe] cannot parse calendar HTML: {e}", file=sys.stderr)
        return 1

    # enrich with id + kind from url
    for e in events:
        url = e.get("url", "")
        e["id"] = url.split("id=")[-1] if "id=" in url else ""
        if "/game/view" in url:
            e["kind"] = "game"
        elif "/training/view" in url:
            e["kind"] = "training"
        elif "/event/view" in url:
            e["kind"] = "event"
        elif "/tournament/view" in url:
            e["kind"] = "tournament"
        else:
            e["kind"] = "unknown"
        # normalize date part for dedupe
        e["date"] = (e.get("start") or "")[:10]

    payload = {"calendar_url": args.calendar_url, "count": len(events),
               "events": events,
               "link_count": sum(1 for e in events if e.get("url"))}
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(payload, f, ensure_ascii=False, indent=2)
    print(f"[probe] found {len(events)} events -> {args.out}")
    by_kind: dict = {}
    for e in events:
        by_kind[e["kind"]] = by_kind.get(e["kind"], 0) + 1
    print(f"[probe] kinds: {by_kind}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
