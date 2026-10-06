#!/usr/bin/env python3
"""Reliable agent-browser invocation.

Two Windows pitfalls handled here:

1. The CLI ships as `agent-browser.CMD`/`.ps1` shims; a bare
   `"agent-browser"` is not directly executable (each script resolves it
   via `shutil.which`, which finds the `.CMD` shim).
2. The process that launches the daemon/browser never exits even though
   its work succeeds: the long-lived daemon/browser inherit its stdio
   pipes, so `subprocess.run(..., capture_output=True)` waits for EOF
   forever — even past `timeout` (the re-`communicate()` after killing
   the CLI blocks on pipes held open by the grandchildren).
   Redirecting to temp files avoids pipes entirely.

`open_url()` additionally tolerates the launch hang by verifying the
active tab URL.
"""

from __future__ import annotations

import subprocess
import tempfile
from typing import Mapping


def run_captured(
    command: list[str],
    *,
    env: Mapping[str, str],
    timeout: int | float | None = None,
) -> "subprocess.CompletedProcess[str]":
    """Run `command`, capturing output to temp files (never pipes)."""
    with tempfile.TemporaryFile(
        "w+", encoding="utf-8", errors="replace"
    ) as out, tempfile.TemporaryFile(
        "w+", encoding="utf-8", errors="replace"
    ) as err:
        proc = subprocess.run(
            list(command),
            stdout=out,
            stderr=err,
            env=dict(env),
            timeout=timeout,
        )
        out.seek(0)
        err.seek(0)
        return subprocess.CompletedProcess(
            proc.args, proc.returncode, out.read(), err.read()
        )


def open_url(
    command: list[str],
    *,
    session_base: list[str],
    env: Mapping[str, str],
    timeout: int | float = 90,
) -> "subprocess.CompletedProcess[str]":
    """Run a full `... open <url>` command list with hang tolerance."""
    proc: subprocess.CompletedProcess[str] | None = None
    try:
        proc = run_captured(command, env=env, timeout=timeout)
    except subprocess.TimeoutExpired:
        proc = None
    if proc is not None and proc.returncode == 0:
        return proc
    check = run_captured([*session_base, "get", "url"], env=env, timeout=30)
    if check.returncode == 0 and (check.stdout or "").strip():
        # Navigation landed; the launcher hung and was abandoned.
        return check
    detail = ""
    if proc is not None:
        detail = (proc.stderr or "")[-500:]
    raise RuntimeError(f"browser navigation failed. {detail}".strip())
