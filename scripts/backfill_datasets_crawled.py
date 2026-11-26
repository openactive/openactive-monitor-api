#!/usr/bin/env python3
"""Backfill `datasets_crawled` into the metric snapshot store.

`/admin/ecosystem-growth-trend` derives a month-by-month crawl figure from `feed_ingestion`, which
holds history back to 2026-05-07. That is the only measure in the KPI framework with any history
behind it, so it is the one period series that can be recovered rather than captured going forward.

Run once. A measurement may be captured only once, so a second run answers 409 and writes nothing.

    python3 scripts/backfill_datasets_crawled.py --dry-run
    python3 scripts/backfill_datasets_crawled.py

Months still in progress are skipped. Their figure covers only the days so far and changes daily,
and a captured measurement cannot be corrected -- so an in-progress month is left to
`capture_month.py`, which runs once it has ended.
"""

import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import monitor_api as api  # noqa: E402

SOURCE = "ecosystem_growth"
FIELD = "datasets_crawled"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--api-base", default=api.DEFAULT_BASE)
    parser.add_argument("--months", type=int, default=60)
    parser.add_argument("--dry-run", action="store_true",
                        help="print the rows that would be captured and write nothing")
    args = parser.parse_args()

    token = api.admin_token()
    trend = api.get(args.api_base, "/admin/ecosystem-growth-trend", token, months=args.months)
    snapshot_date = trend["meta"]["snapshot_date"]

    rows, skipped = [], []
    for point in trend["data"]:
        if point["in_progress"]:
            skipped.append(point)
            continue
        rows.append({
            "period": point["period_start"],
            "source": SOURCE,
            "field": FIELD,
            "value": point["datasets_crawled"],
            # Days the crawler actually ran that month. Carried so a month measured over a short
            # window -- May 2026 starts on the 7th -- is visible as such in the stored series.
            "denominator": point["days_with_data"],
            "snapshot_date": snapshot_date,
        })

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

    return api.capture(args.api_base, token, rows)


if __name__ == "__main__":
    sys.exit(main())
