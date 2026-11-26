"""Shared plumbing for the metric capture scripts: tokens, HTTP, and the snapshot write.

Kept apart from the scripts themselves so the two tokens are read in exactly one place. Neither
token is ever printed, and nothing here writes one to a file.
"""

import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
SETTINGS = REPO_ROOT / "appsettings.Development.json"

DEFAULT_BASE = "http://localhost:5268"
SNAPSHOTS_PATH = "/admin/metric-snapshots"


def _token(env_name: str, section_key: str, what: str) -> str:
    """The token from the environment, falling back to the gitignored dev settings."""
    token = os.environ.get(env_name)
    if token:
        return token

    if not SETTINGS.exists():
        sys.exit(
            f"No {what}. Set {env_name}, or add Api:{section_key} to "
            "appsettings.Development.json (see docs/development.md)."
        )

    token = json.loads(SETTINGS.read_text()).get("Api", {}).get(section_key)
    if not token:
        sys.exit(f"appsettings.Development.json has no Api:{section_key}.")
    return token


def admin_token() -> str:
    """`Api:AdminToken` — the /admin surface."""
    return _token("MONITOR_ADMIN_TOKEN", "AdminToken", "admin token")


def access_token() -> str:
    """`Api:AccessToken` — the public analytics surface. Not interchangeable with the admin token."""
    return _token("MONITOR_ACCESS_TOKEN", "AccessToken", "access token")


def get(base: str, path: str, token: str, **params) -> dict:
    params["token"] = token
    url = f"{base}{path}?{urllib.parse.urlencode(params)}"
    with urllib.request.urlopen(url, timeout=600) as response:
        return json.load(response)


def get_all(base: str, path: str, token: str, page_size: int = 1000, **params) -> tuple[list, dict]:
    """Every page of a paged admin list, with the first page's `meta`."""
    rows: list = []
    page, meta = 1, {}
    while True:
        body = get(base, path, token, page=page, page_size=page_size, **params)
        meta = meta or body["meta"]
        rows.extend(body["data"])
        if len(rows) >= body["meta"]["total"] or not body["data"]:
            return rows, meta
        page += 1


def capture(base: str, token: str, rows: list) -> int:
    """POST a batch of measurements. Returns a process exit code and reports any refusal."""
    url = f"{base}{SNAPSHOTS_PATH}?{urllib.parse.urlencode({'token': token})}"
    request = urllib.request.Request(
        url,
        data=json.dumps(rows).encode(),
        headers={"Content-Type": "application/json"},
        method="POST",
    )

    try:
        with urllib.request.urlopen(request, timeout=600) as response:
            body = json.load(response)
            print(f"\nCaptured {body['meta']['total']} measurements.")
            return 0
    except urllib.error.HTTPError as error:
        body = json.loads(error.read() or b"{}")
        print(f"\nRefused ({error.code}): {body.get('message', body)}", file=sys.stderr)
        for measurement in body.get("measurements", []):
            print(f"  {measurement}", file=sys.stderr)
        return 1
