#!/usr/bin/env python3
"""Obtain a DFBnet bearer token from an agent-browser session.

The helper opens DFBnet in a headed browser so an existing profile can be
reused and the user can complete an interactive login or 2FA challenge.  The
DFBnet single-page app currently stores its bearer token in session storage;
older deployments exposed an ``accesstoken`` cookie instead.  Both sources
are read in memory and the token is returned to the API client without ever
being printed or written to disk.
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import time
from typing import Any, Callable, Mapping

from browser_open import open_url as _verified_open
from browser_open import run_captured as _run_captured


DFBNET_LOGIN_URL = "https://www.dfbnet.org/sbo-mobile/v2/"
DEFAULT_SESSION = "spielerplus-dfbnet"
DEFAULT_TIMEOUT_SECONDS = 180


class DfbnetBrowserAuthError(RuntimeError):
    """Raised when agent-browser cannot provide a DFBnet access token."""


BrowserRunner = Callable[..., Any]


def _agent_browser_bin() -> str:
    """Resolve the agent-browser CLI (Windows .CMD shim aware)."""
    return shutil.which("agent-browser") or "agent-browser"


def _session_base(*, session: str) -> list[str]:
    return [_agent_browser_bin(), "--session", session]


def _command(
    *,
    session: str,
    profile: str,
    headed: bool,
    args: list[str],
) -> list[str]:
    command = [_agent_browser_bin(), "--session", session]
    if profile:
        command.extend(["--profile", profile])
    if headed:
        command.append("--headed")
    command.extend(args)
    return command


def _run_captured_shim(command, **kwargs):
    return _run_captured(command, env=kwargs.get("env", {}))


def _run_browser(
    command: list[str],
    *,
    env: Mapping[str, str],
    runner: BrowserRunner | None = None,
) -> Any:
    # run_captured uses temp files instead of pipes: the process that
    # launches the daemon/browser never exits (it inherits stdio to the
    # long-lived children), so capture_output would wait for EOF forever.
    run = runner if runner is not None else _run_captured_shim
    try:
        result = run(command, capture_output=True, text=True, env=dict(env))
    except FileNotFoundError as exc:
        raise DfbnetBrowserAuthError(
            "agent-browser is not installed or is not available on PATH"
        ) from exc
    if getattr(result, "returncode", 1) != 0:
        raise DfbnetBrowserAuthError("agent-browser could not complete the DFBnet browser action")
    return result


def _cookie_values(payload: object) -> list[Mapping[str, object]]:
    if isinstance(payload, str):
        try:
            payload = json.loads(payload)
        except json.JSONDecodeError:
            return []
    if not isinstance(payload, Mapping):
        return []
    data = payload.get("data")
    if not isinstance(data, Mapping):
        return []
    cookies = data.get("cookies")
    if not isinstance(cookies, list):
        return []
    return [cookie for cookie in cookies if isinstance(cookie, Mapping)]


def _storage_values(payload: object) -> Mapping[str, object]:
    """Return the web-storage key/value mapping from agent-browser JSON."""
    if isinstance(payload, str):
        try:
            payload = json.loads(payload)
        except json.JSONDecodeError:
            return {}
    if not isinstance(payload, Mapping):
        return {}
    data = payload.get("data")
    if not isinstance(data, Mapping):
        return {}
    values = data.get("data")
    if isinstance(values, Mapping):
        return values
    # Older agent-browser releases returned the storage map directly under
    # ``data`` rather than adding the nested ``data``/``lifecycle`` wrapper.
    return {
        str(key): value
        for key, value in data.items()
        if key not in {"lifecycle", "error"}
    }


def extract_access_token(payload: object) -> str:
    """Extract a DFBnet access token from cookie or storage JSON."""
    storage = _storage_values(payload)
    for key, value in storage.items():
        normalized = str(key).casefold().replace("_", "").replace("-", "")
        if normalized != "accesstoken":
            continue
        token = str(value or "").strip()
        if token:
            return token

    for cookie in _cookie_values(payload):
        name = str(cookie.get("name") or "").casefold().replace("_", "").replace("-", "")
        domain = str(cookie.get("domain") or "").casefold()
        if name != "accesstoken" or (domain and "dfbnet.org" not in domain):
            continue
        value = str(cookie.get("value") or "").strip()
        if value:
            return value
    return ""


def fetch_access_token(
    *,
    session: str = DEFAULT_SESSION,
    profile: str = "",
    timeout: int = DEFAULT_TIMEOUT_SECONDS,
    login_url: str = DFBNET_LOGIN_URL,
    runner: BrowserRunner | None = None,
    sleep_fn: Callable[[float], None] = time.sleep,
    now_fn: Callable[[], float] = time.monotonic,
) -> str:
    """Open DFBnet and wait for an authenticated access token."""
    if timeout < 0:
        raise ValueError("browser auth timeout must not be negative")
    session = (session or DEFAULT_SESSION).strip()
    profile = (profile or os.environ.get("AGENT_BROWSER_PROFILE", "Default")).strip()
    if not session:
        raise ValueError("agent-browser session must not be empty")

    env = dict(os.environ)
    if profile:
        env["AGENT_BROWSER_PROFILE"] = profile
    command_base = {
        "session": session,
        "profile": profile,
        "headed": True,
    }
    try:
        _verified_open(
            _command(**command_base, args=["open", login_url]),
            session_base=_session_base(session=session),
            env=env,
        )
    except RuntimeError as exc:
        raise DfbnetBrowserAuthError(str(exc)) from exc

    def read_token() -> str:
        # The current Angular app keeps the OIDC bearer token in session
        # storage.  Check it first, then retain cookie compatibility for older
        # deployments.
        for storage_name in ("session", "local"):
            result = _run_browser(
                _command(**command_base, args=["--json", "storage", storage_name]),
                env=env,
                runner=runner,
            )
            token = extract_access_token(getattr(result, "stdout", ""))
            if token:
                return token

        result = _run_browser(
            _command(**command_base, args=["--json", "cookies"]),
            env=env,
            runner=runner,
        )
        return extract_access_token(getattr(result, "stdout", ""))

    token = read_token()
    if token:
        return token

    print(
        "[dfbnet-auth] Complete DFBnet sign-in (including 2FA) in the headed browser window; waiting…",
        flush=True,
    )
    deadline = now_fn() + timeout
    while now_fn() < deadline:
        sleep_fn(min(1.0, max(0.0, deadline - now_fn())))
        token = read_token()
        if token:
            return token
    raise DfbnetBrowserAuthError(
        "no DFBnet access token appeared; finish sign-in in the agent-browser window"
    )
