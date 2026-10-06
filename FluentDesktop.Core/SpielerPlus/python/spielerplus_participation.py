#!/usr/bin/env python3
"""Export SpielerPlus participation page to CSV/JSON via agent-browser.

Opens https://www.spielerplus.de/en/participation (login via Chrome profile),
extracts tabular participation data with generic table parsing, writes
participation.json + participation.csv.

Usage:
  AGENT_BROWSER_PROFILE=Default python3 spielerplus_participation.py \
      --session spielerplus-ics --out participation.json --csv participation.csv \
      [--url https://www.spielerplus.de/en/participation] [--headed]

Output JSON: {"url": ..., "title": ..., "count": N, "events": [...], "tables": N}
CSV columns: superset of extracted fields (date, title, kind, status, ...).
"""
import argparse
import csv
import json
import os
import shutil
import subprocess
import sys

from browser_open import open_url as _verified_open
from browser_open import run_captured as _run_captured

EXPORT_JS = r"""
(() => {
  const out = { title: document.title, url: location.href, tables: [] };
  try {
    const tables = Array.from(document.querySelectorAll('table'));
    out.tables = tables.map((t, ti) => {
      // headers: th in thead or first row
      let headers = Array.from(t.querySelectorAll('thead th')).map(h => h.innerText.trim());
      const rows = Array.from(t.querySelectorAll('tbody tr')).length
        ? Array.from(t.querySelectorAll('tbody tr'))
        : Array.from(t.querySelectorAll('tr'));
      if (!headers.length && rows.length) {
        const firstTh = Array.from(rows[0].querySelectorAll('th,td')).map(c => c.innerText.trim());
        // heuristic: treat first row as header if it contains th or known labels
        const looksHeader = rows[0].querySelector('th') ||
          firstTh.some(x => /datum|date|event|spiel|training|teilnahme|status|zu-?sage|absage/i.test(x));
        if (looksHeader) headers = firstTh;
      }
      const dataRows = (headers.length && rows.length &&
        rows[0].querySelector('th')) ? rows.slice(0) : rows;
      // if first row was used as header from td, skip it
      let startIdx = 0;
      if (headers.length && dataRows.length) {
        const firstRowText = Array.from(dataRows[0].querySelectorAll('th,td'))
          .map(c => c.innerText.trim()).join('|');
        if (firstRowText === headers.join('|')) startIdx = 1;
      }
      const data = [];
      for (let i = startIdx; i < dataRows.length; i++) {
        const cells = Array.from(dataRows[i].querySelectorAll('th,td'));
        if (!cells.length) continue;
        const row = {};
        cells.forEach((c, ci) => {
          const key = headers[ci] || ('col' + ci);
          row[key] = c.innerText.trim().replace(/\s+/g, ' ');
          const link = c.querySelector('a[href]');
          if (link) row[key + '_href'] = link.getAttribute('href');
        });
        // row-level link fallback
        const rl = dataRows[i].querySelector('a[href*="/game/view"],a[href*="/training/view"],a[href*="/event/view"],a[href*="/tournament/view"]');
        if (rl && !row.url) row.url = rl.getAttribute('href');
        if (Object.values(row).some(v => v)) data.push(row);
      }
      // table caption / preceding heading as context
      let context = '';
      try {
        const cap = t.querySelector('caption');
        if (cap) context = cap.innerText.trim().slice(0, 200);
        else {
          let el = t.previousElementSibling;
          let hops = 0;
          while (el && hops < 3 && !context) {
            if (/H[1-4]|P|DIV/i.test(el.tagName) && el.innerText.trim())
              context = el.innerText.trim().slice(0, 200);
            el = el.previousElementSibling; hops++;
          }
        }
      } catch (e) {}
      return { index: ti, context, headers, rows: data };
    });
  } catch (e) { out.tablesErr = String(e).slice(0, 500); }
  // fallback: participation list items (cards/divs) if no tables
  try {
    const items = Array.from(document.querySelectorAll(
      '[class*="participation"], [class*="attendance"], .event-item, .list-group-item'
    )).slice(0, 500).map(el => ({
      text: el.innerText.trim().slice(0, 500).replace(/\s+/g, ' '),
      href: (el.querySelector('a[href]') || {}).href || ''
    })).filter(x => x.text);
    if (items.length) out.listItems = items;
  } catch (e) {}
  try {
    out.bodyText = document.body.innerText.slice(0, 3000);
  } catch (e) {}
  return JSON.stringify(out);
})()
"""


def _agent_browser_bin() -> str:
    """Resolve the agent-browser CLI (Windows .CMD shim aware)."""
    return shutil.which("agent-browser") or "agent-browser"


def run(cmd, env, timeout=None):
    return _run_captured(cmd, env=env, timeout=timeout)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--session", default="spielerplus-ics")
    ap.add_argument("--profile", default=os.environ.get("AGENT_BROWSER_PROFILE", "Default"))
    ap.add_argument("--url", default="https://www.spielerplus.de/en/participation")
    ap.add_argument("--out", default="participation.json")
    ap.add_argument("--csv", default="participation.csv")
    ap.add_argument("--headed", action="store_true")
    args = ap.parse_args()

    env = dict(os.environ)
    env["AGENT_BROWSER_PROFILE"] = args.profile
    base = [_agent_browser_bin(), "--session", args.session]
    if args.headed:
        base.append("--headed")

    print(f"[participation] profile={args.profile} session={args.session}")
    print(f"[participation] open {args.url}")
    p = _verified_open(base + ["open", args.url], session_base=base, env=env)
    if p.returncode != 0:
        print((p.stdout or "")[-2000:], file=sys.stderr)
        print((p.stderr or "")[-2000:], file=sys.stderr)
        return 1

    run(base + ["wait", "--load", "networkidle"], env)
    p = run(base + ["eval", EXPORT_JS], env)
    raw = (p.stdout or "").strip()
    if p.returncode != 0 or not raw:
        print("[participation] eval failed", file=sys.stderr)
        print((p.stdout or "")[-2000:], file=sys.stderr)
        print((p.stderr or "")[-2000:], file=sys.stderr)
        return 1
    if raw.startswith('"') and raw.endswith('"'):
        try:
            raw = json.loads(raw)
        except Exception:
            pass
    try:
        data = json.loads(raw)
    except Exception as e:
        print(f"[participation] cannot parse eval output: {e}\n{raw[:3000]}", file=sys.stderr)
        return 1

    tables = data.get("tables") or []
    # pick largest table as primary; merge all tables with source context
    tables_sorted = sorted(tables, key=lambda t: len(t.get("rows") or []), reverse=True)
    merged = []
    for t in tables:
        ctx = t.get("context") or ""
        for r in t.get("rows") or []:
            row = dict(r)
            if ctx:
                row.setdefault("table", ctx)
            merged.append(row)
    if not merged and data.get("listItems"):
        for it in data["listItems"]:
            merged.append({"text": it.get("text", ""), "url": it.get("href", "")})

    payload = {
        "url": data.get("url", args.url),
        "title": data.get("title", ""),
        "table_count": len(tables),
        "count": len(merged),
        "events": merged,
    }
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(payload, f, ensure_ascii=False, indent=2)

    # CSV: union of keys, stable order with preferred columns first
    preferred = ["date", "Datum", "event", "Event", "Spiel", "title", "Titel",
                 "kind", "status", "Status", "Teilnahme", "Zusage", "table",
                 "text", "url"]
    keys = []
    seen = set()
    for k in preferred:
        # match case-insensitively against actual keys
        for mk in {kk for r in merged for kk in r.keys()}:
            if mk.lower() == k.lower() and mk not in seen:
                keys.append(mk)
                seen.add(mk)
    for r in merged:
        for k in r.keys():
            if k not in seen:
                seen.add(k)
                keys.append(k)
    if not keys:
        keys = ["text"]
    with open(args.csv, "w", encoding="utf-8", newline="") as f:
        w = csv.DictWriter(f, fieldnames=keys, extrasaction="ignore")
        w.writeheader()
        for r in merged:
            # absolutize relative SpielerPlus hrefs in url-ish columns
            row = dict(r)
            for k, v in list(row.items()):
                if isinstance(v, str) and v.startswith("/") and ("href" in k.lower() or k == "url"):
                    row[k] = "https://www.spielerplus.de" + v
            w.writerow(row)

    print(f"[participation] found {len(merged)} row(s) in {len(tables)} table(s) -> {args.out}, {args.csv}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
