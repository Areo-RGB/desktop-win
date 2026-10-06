#!/usr/bin/env python3
"""Bulk-create SpielerPlus events from events.json via agent-browser.

Strategy (robust against the krajee datecontrol widgets):
  - open the create page (/en/game/create or /en/event/create)
  - fill hidden save inputs directly via JS:
      Game[startdate]=Y-m-d, Game[starttime]=H:i:00, Game[enddate], Game[endtime]
      (same for Event[...] on the event page)
    and mirror the visible display inputs (m/d/Y + h:i A) so validation passes
  - fill opponent / name / info / premises via agent-browser fill
  - select home/away via agent-browser select
  - submit by clicking the submit button, wait for navigation, record URL

Dedupe: skips events whose (date + opponent/title) already exists in
existing.json (produced by spielerplus_probe.py).

Usage:
  AGENT_BROWSER_PROFILE=Default python3 spielerplus_create.py \
      --events events.json --existing existing.json --out results.json \
      --session spielerplus-ics [--dry-run] [--limit 3] [--kinds game,event] \
      [--headed] [--submit-delay 2.0]

Notes:
  - Requires login in the Chrome profile (Default). The script never asks for
    credentials; open SpielerPlus once in Chrome if the session is logged out.
  - Default is DRY-RUN-safe? No: default DOES create. Pass --dry-run to only
    fill forms without submitting.
"""
import argparse
import base64
import json
import os
import shutil
import subprocess
import sys
import time

from browser_open import open_url as _verified_open
from browser_open import run_captured as _run_captured

CREATE_URL = {
    "game": "https://www.spielerplus.de/en/game/create",
    "event": "https://www.spielerplus.de/en/event/create",
    # birthdays map to generic events
    "birthday": "https://www.spielerplus.de/en/event/create",
}

FORM_PREFIX = {"game": "Game", "event": "Event", "birthday": "Event"}


def run(cmd, env, timeout=None):
    return _run_captured(cmd, env=env, timeout=timeout)


def ab(base, env, *args):
    p = run(base + list(args), env)
    return p


def js_fill_and_submit(prefix: str, ev: dict, dry_run: bool) -> str:
    """Build JS that fills hidden+display date/time inputs. Returns JS string."""
    # JSON-encode values for safe embedding
    payload = json.dumps({
        "sd": ev["sp_startdate"], "ed": ev["sp_enddate"],
        "st": ev["sp_starttime"], "et": ev["sp_endtime"],
        "sdd": ev["sp_startdate_disp"], "edd": ev["sp_enddate_disp"],
        "std": ev["sp_starttime_disp"], "etd": ev["sp_endtime_disp"],
        "dry": dry_run,
    })
    return r"""
(() => {
  const P = __PAYLOAD__;
  const prefix = __PREFIX__;
  function setVal(el, v) {
    if (!el) return false;
    el.focus();
    // native setter so framework listeners fire
    const proto = el.tagName === 'TEXTAREA'
      ? window.HTMLTextAreaElement.prototype
      : window.HTMLInputElement.prototype;
    const setter = Object.getOwnPropertyDescriptor(proto, 'value').set;
    setter.call(el, v);
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
    try { window.jQuery && window.jQuery(el).trigger('change'); } catch (e) {}
    return true;
  }
  const res = {};
  res.hidden_startdate = setVal(document.getElementById('datetime-startdate'), P.sd);
  res.hidden_starttime = setVal(document.getElementById('datetime-starttime'), P.st);
  res.hidden_enddate = setVal(document.getElementById('datetime-enddate'), P.ed);
  res.hidden_endtime = setVal(document.getElementById('datetime-endtime'), P.et);
  res.disp_startdate = setVal(document.getElementById('datetime-startdate-disp'), P.sdd);
  res.disp_starttime = setVal(document.getElementById('datetime-starttime-disp'), P.std);
  res.disp_enddate = setVal(document.getElementById('datetime-enddate-disp'), P.edd);
  res.disp_endtime = setVal(document.getElementById('datetime-endtime-disp'), P.etd);
  // verify
  res.verify = {
    sd: document.getElementById('datetime-startdate')?.value,
    st: document.getElementById('datetime-starttime')?.value,
    ed: document.getElementById('datetime-enddate')?.value,
    et: document.getElementById('datetime-endtime')?.value,
  };
  return JSON.stringify(res);
})()
""".replace("__PAYLOAD__", payload).replace("__PREFIX__", json.dumps(prefix))


def norm(s: str) -> str:
    return " ".join((s or "").lower().split())


def dedupe_key(ev: dict) -> str:
    if ev["kind"] == "game":
        return ev["sp_startdate"] + "|" + norm(ev.get("opponent") or ev["summary"])
    return ev["sp_startdate"] + "|" + norm(ev["summary"])


def existing_keys(existing: dict) -> set:
    keys = set()
    for e in existing.get("events", []):
        title = e.get("title", "")
        date = (e.get("date") or (e.get("start") or "")[:10])
        keys.add(date + "|" + norm(title))
        # game titles on SpielerPlus are short ("[Spiel] X"); also index them
    return keys


def _agent_browser_bin() -> str:
    """Resolve the agent-browser CLI (Windows .CMD shim aware)."""
    return shutil.which("agent-browser") or "agent-browser"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--events", default="events.json")
    ap.add_argument("--existing", default="existing.json")
    ap.add_argument("--out", default="results.json")
    ap.add_argument("--session", default="spielerplus-ics")
    ap.add_argument("--profile", default=os.environ.get("AGENT_BROWSER_PROFILE", "Default"))
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--kinds", default="game,event,birthday",
                    help="which ICS kinds to create (game maps to game form, event+birthday to event form)")
    ap.add_argument("--headed", action="store_true")
    ap.add_argument("--submit-delay", type=float, default=2.0)
    ap.add_argument("--skip-dedupe", action="store_true")
    args = ap.parse_args()

    env = dict(os.environ)
    env["AGENT_BROWSER_PROFILE"] = args.profile
    base = [_agent_browser_bin(), "--session", args.session]
    if args.headed:
        base.append("--headed")

    events = json.load(open(args.events, encoding="utf-8"))["events"]
    kinds = {k.strip().lower() for k in args.kinds.split(",") if k.strip()}
    events = [e for e in events if e["kind"] in kinds]
    if args.limit:
        events = events[:args.limit]

    exist = {"events": []}
    if os.path.exists(args.existing) and not args.skip_dedupe:
        exist = json.load(open(args.existing, encoding="utf-8"))
    ekeys = existing_keys(exist)
    print(f"[create] {len(events)} candidate(s), {len(ekeys)} existing key(s), "
          f"dry_run={args.dry_run}")

    results = []
    for i, ev in enumerate(events, 1):
        form_kind = "game" if ev["kind"] == "game" else "event"
        prefix = FORM_PREFIX[form_kind]
        url = CREATE_URL[form_kind]
        key = dedupe_key(ev)
        # fuzzy: exact key OR opponent substring match on same date
        dup = (key in ekeys)
        if not dup and not args.skip_dedupe and ev["kind"] == "game":
            opp = norm(ev.get("opponent", ""))
            for e in exist.get("events", []):
                if (e.get("date") == ev["sp_startdate"]
                        and opp and opp in norm(e.get("title", ""))):
                    dup = True
                    break
        label = f"[{i}/{len(events)}] {ev['kind']} {ev['sp_startdate']} " \
                f"{ev['sp_starttime_disp']} '{ev['summary'][:70]}'"
        if dup and not args.skip_dedupe:
            print(f"SKIP (exists) {label}")
            results.append({"status": "skipped-exists", "event": ev})
            continue
        print(f"{'DRY ' if args.dry_run else ''}CREATE {label}")
        try:
            p = _verified_open(base + ["open", url], session_base=base, env=env)
            if p.returncode != 0:
                raise RuntimeError(f"open failed: {(p.stderr or '')[-500:]}")
            ab(base, env, "wait", "--load", "networkidle")

            if form_kind == "game":
                opp = ev.get("opponent") or ev["summary"]
                p = ab(base, env, "fill", "#game-opponentname", opp)
                if p.returncode != 0:
                    raise RuntimeError(f"fill opponent failed: {(p.stderr or '')[-300:]}")
                hv = {"home": "1", "away": "0"}.get(ev.get("home_away", ""), "")
                if hv:
                    p = ab(base, env, "select", "#game-homegame", hv)
                    if p.returncode != 0:
                        print(f"  warn: home/away select failed ({(p.stderr or '')[-200:]})")
                info_bits = [ev["summary"]]
                if ev.get("location"):
                    info_bits.append(ev["location"])
                if ev.get("description"):
                    info_bits.append(ev["description"][:500])
                info = "\n".join(info_bits)
                p = ab(base, env, "fill", "#game-info", info[:2000])
                if p.returncode != 0:
                    print(f"  warn: info fill failed ({(p.stderr or '')[-200:]})")
                if ev.get("location"):
                    p = ab(base, env, "fill", "#game-premises", ev["location"][:240])
            else:
                name = ev["summary"] if ev["kind"] == "birthday" else ev["summary"]
                p = ab(base, env, "fill", "#event-name", name[:100])
                if p.returncode != 0:
                    raise RuntimeError(f"fill event name failed: {(p.stderr or '')[-300:]}")
                info_bits = []
                if ev.get("location"):
                    info_bits.append(ev["location"])
                if ev.get("description"):
                    info_bits.append(ev["description"][:500])
                if info_bits:
                    ab(base, env, "fill", "#event-info", "\n".join(info_bits)[:2000])
                if ev.get("location"):
                    ab(base, env, "fill", "#event-premises", ev["location"][:240])

            # hidden + display date/time via JS (base64 to avoid quoting issues)
            js = js_fill_and_submit(prefix, ev, args.dry_run)
            b64 = base64.b64encode(js.encode("utf-8")).decode()
            p = ab(base, env, "eval", "-b", b64)
            out = (p.stdout or "").strip()
            if out.startswith('"') and out.endswith('"'):
                try:
                    out = json.loads(out)
                except Exception:
                    pass
            print(f"  datefill: {out[:600]}")

            if args.dry_run:
                shot = ab(base, env, "screenshot")
                print(f"  dry-run screenshot: {(shot.stdout or '').strip()[-200:]}")
                results.append({"status": "dry-run", "event": ev, "datefill": out})
                continue

            time.sleep(args.submit_delay)
            # click the submit button (form submit). Selector: button[type=submit]
            p = ab(base, env, "click", "button[type=submit]")
            if p.returncode != 0:
                # fallback: submit form via JS
                print("  warn: click submit failed, trying JS submit")
                js2 = base64.b64encode(
                    b"document.querySelector('form').submit(); 'submitted'").decode()
                ab(base, env, "eval", "-b", js2)
            # wait for navigation away from /create
            p = ab(base, env, "wait", "--url", "**/view?id=*")
            new_url = ""
            if p.returncode == 0:
                q = ab(base, env, "get", "url")
                new_url = (q.stdout or "").strip()
            else:
                # still capture current url + errors for diagnosis
                q = ab(base, env, "get", "url")
                new_url = (q.stdout or "").strip()
                err = ab(base, env, "errors")
                print(f"  warn: no view-url after submit, url={new_url} "
                      f"errors={(err.stdout or '')[:500]}")
            print(f"  -> {new_url}")
            ok = "/view?id=" in new_url and "/create" not in new_url
            results.append({"status": "created" if ok else "submit-uncertain",
                            "event": ev, "url": new_url, "datefill": out})
            # add to dedupe set so reruns in same batch don't duplicate
            ekeys.add(key)
        except Exception as e:
            print(f"  ERROR: {e}")
            results.append({"status": "error", "event": ev, "error": str(e)[:500]})

    summary = {}
    for r in results:
        summary[r["status"]] = summary.get(r["status"], 0) + 1
    json.dump({"results": results, "summary": summary},
              open(args.out, "w", encoding="utf-8"),
              ensure_ascii=False, indent=2)
    print(f"[create] done: {summary} -> {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
