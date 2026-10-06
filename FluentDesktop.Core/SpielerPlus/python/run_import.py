#!/usr/bin/env python3
"""End-to-end pipeline: fetch ICS -> probe existing -> create missing.

Usage:
  AGENT_BROWSER_PROFILE=Default python3 run_import.py \
      --ics-url "https://calendar.google.com/calendar/ical/.../basic.ics" \
      --session spielerplus-ics [--dry-run] [--limit 3] [--kinds game,event,birthday] \
      [--from-date 2026-09-11] [--team-hint "FC Hertha 03"] [--headed] [--workdir .]

Writes events.json, existing.json, results.json into workdir.
Exit code 0 when no errors, 2 when some creates failed/uncertain.
"""
import argparse
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))


def sh(cmd, env):
    print("+ " + " ".join(cmd))
    p = subprocess.run(cmd, env=env)
    return p.returncode


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--ics-url", required=True)
    ap.add_argument("--session", default="spielerplus-ics")
    ap.add_argument("--profile", default=os.environ.get("AGENT_BROWSER_PROFILE", "Default"))
    ap.add_argument("--workdir", default=HERE)
    ap.add_argument("--from-date", default="")
    ap.add_argument("--kinds", default="game,event,birthday")
    ap.add_argument("--include", default="game,birthday,event")
    ap.add_argument("--team-hint", default="FC Hertha 03")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--headed", action="store_true")
    ap.add_argument("--skip-dedupe", action="store_true")
    args = ap.parse_args()

    env = dict(os.environ)
    env["AGENT_BROWSER_PROFILE"] = args.profile
    os.makedirs(args.workdir, exist_ok=True)
    events_json = os.path.join(args.workdir, "events.json")
    existing_json = os.path.join(args.workdir, "existing.json")
    results_json = os.path.join(args.workdir, "results.json")

    rc = sh([sys.executable, os.path.join(HERE, "ics_fetch.py"),
             "--url", args.ics_url, "--out", events_json]
            + (["--from-date", args.from_date] if args.from_date else [])
            + ["--include", args.include, "--team-hint", args.team_hint], env)
    if rc != 0:
        return rc
    rc = sh([sys.executable, os.path.join(HERE, "spielerplus_probe.py"),
             "--session", args.session, "--out", existing_json]
            + (["--headed"] if args.headed else []), env)
    if rc != 0:
        return rc
    cmd = [sys.executable, os.path.join(HERE, "spielerplus_create.py"),
           "--events", events_json, "--existing", existing_json,
           "--out", results_json, "--session", args.session,
           "--kinds", args.kinds]
    if args.dry_run:
        cmd.append("--dry-run")
    if args.limit:
        cmd += ["--limit", str(args.limit)]
    if args.headed:
        cmd.append("--headed")
    if args.skip_dedupe:
        cmd.append("--skip-dedupe")
    rc = sh(cmd, env)
    if rc != 0:
        return rc
    try:
        res = json.load(open(results_json, encoding="utf-8"))
        print(f"[pipeline] summary: {res.get('summary')}")
        bad = sum(v for k, v in (res.get("summary") or {}).items()
                  if k in ("error", "submit-uncertain"))
        return 2 if bad else 0
    except Exception:
        return 0


if __name__ == "__main__":
    raise SystemExit(main())
