#!/usr/bin/env python3
"""Backfill `datasets_crawled` into the metric snapshot store.

`/admin/ecosystem-growth-trend` derives a month-by-month crawl figure from `feed_ingestion`, which
holds history back to 2026-05-07. That is the only measure in the KPI framework with any history
behind it, so it is the one period series that can be recovered rather than captured going forward.

Run once. A measurement may be captured only once, so a second run answers 409 and writes nothing.

    python3 -I scripts/backfill_datasets_crawled.py --dry-run
    python3 -I scripts/backfill_datasets_crawled.py

The admin token is read from MONITOR_ADMIN_TOKEN, or from appsettings.Development.json, which is
gitignored. It is never printed.

Months still in progress are skipped. Their figure covers only the days so far and changes daily,
and a captured measurement cannot be corrected — so an in-progress month is left for the capture
that runs once it has ended.
"""

import argparse
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

SOURCE = "ecosystem_growth"
FIELD = "datasets_crawled"
REPO_ROOT = Path(__file__).resolve().parent.parent


def admin_token() -> str:
    token = os.environ.get("MONITOR_ADMIN_TOKEN")
    if token:
        return token

    settings = REPO_ROOT / "appsettings.Development.json"
    if not settings.exists():
        sys.exit(
            "No admin token. Set MONITOR_ADMIN_TOKEN, or add Api:AdminToken to "
            "appsettings.Development.json (see docs/development.md)."
        )

    token = json.loads(settings.read_text()).get("Api", {}).get("AdminToken")
    if not token:
        sys.exit("appsettings.Development.json has no Api:AdminToken.")
    return token


def get(base: str, path: str, token: str, **params) -> dict:
    params["token"] = token
    url = f"{base}{path}?{urllib.parse.urlencode(params)}"
    with urllib.request.urlopen(url, timeout=300) as response:
        return json.load(response)


def post(base: str, path: str, token: str, body: list) -> tuple[int, dict]:
    url = f"{base}{path}?{urllib.parse.urlencode({'token': token})}"
    request = urllib.request.Request(
        url,
        data=json.dumps(body).encode(),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=300) as response:
            return response.status, json.load(response)
    except urllib.error.HTTPError as error:
        return error.code, json.loads(error.read() or b"{}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--api-base", default="http://localhost:5299")
    parser.add_argument("--months", type=int, default=60)
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="print the rows that would be captured and write nothing",
    )
    args = parser.parse_args()

    token = admin_token()
    trend = get(args.api_base, "/admin/ecosystem-growth-trend", token, months=args.months)
    snapshot_date = trend["meta"]["snapshot_date"]

    rows, skipped = [], []
    for point in trend["data"]:
        if point["in_progress"]:
            skipped.append(point)
            continue
        rows.append(
            {
                "period": point["period_start"],
                "source": SOURCE,
                "field": FIELD,
                "value": point["datasets_crawled"],
                # Days the crawler actually ran that month. Carried so a month measured over a short
                # window -- May 2026 starts on the 7th -- is visible as such in the stored series.
                "denominator": point["days_with_data"],
                "snapshot_date": snapshot_date,
            }
        )

    if not rows:
        print("Nothing to capture.")
        return 0

    print(f"{'period':<12} {'value':>7} {'days':>6}")
    for row in rows:
        print(f"{row['period']:<12} {row['value']:>7} {row['denominator']:>6}")
    for point in skipped:
        print(f"{point['period_start']:<12} {'--':>7} {'--':>6}  skipped: month in progress")

    if args.dry_run:
        print(f"\nDry run. {len(rows)} measurements would be captured.")
        return 0

    status, body = post(args.api_base, "/admin/metric-snapshots", token, rows)
    if status == 201:
        print(f"\nCaptured {body['meta']['total']} measurements.")
        return 0

    print(f"\nRefused ({status}): {body.get('message', body)}", file=sys.stderr)
    for measurement in body.get("measurements", []):
        print(f"  {measurement}", file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main())
