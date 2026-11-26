#!/usr/bin/env python3
"""Capture one month's measurements into the metric snapshot store.

Run on the 1st, for the month just ended:

    python3 scripts/capture_month.py --dry-run
    python3 scripts/capture_month.py

A measurement may be captured only once, so a second run answers 409 and writes nothing. There is
no update or delete route: a period captured wrongly cannot be corrected through the API.

What it stores are **source fields, not finished measures.** No row here is "KPI4"; the report
computes that from the nine completeness rows when it runs. A measure whose definition is still
being argued over can then be recomputed for every period already captured, which is why both the
volume-weighted and the unweighted completeness figures are kept, and why the three monitor counts
behind CM13 are stored separately rather than summed.

The API must be running, and the capture needs both tokens -- the admin surface for most of it and
the public analytics surface for `number_of_facilities`.
"""

import argparse
import sys
from datetime import date
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import monitor_api as api  # noqa: E402

COMPLETENESS_FIELDS = (
    "location",
    "start_date",
    "end_date",
    "activities",
    "facilities",
    "age_range",
    "level",
    "accessibility_support",
    "gender_restriction",
)

# The three monitors the critical measure names. The other two are excluded on purpose.
CM13_MONITORS = ("single_feed_stall", "dataset_stall", "feed_ingestion_error")


def previous_month(today: date) -> date:
    """The first day of the month before the one `today` falls in."""
    first = today.replace(day=1)
    return (first.replace(year=first.year - 1, month=12) if first.month == 1
            else first.replace(month=first.month - 1))


def row(period, source, field, value, denominator=None, snapshot_date=None):
    return {
        "period": period.isoformat(),
        "source": source,
        "field": field,
        "value": value,
        "denominator": denominator,
        "snapshot_date": snapshot_date,
    }


def feed_quality_rows(base, token, period):
    """
    KPI1 and the nine completeness properties.

    The summary's completeness averages are **unweighted**, so the volume-weighted figure the
    measurement asks for ("adjusted by volume of feed") has to be computed here from the per-feed
    rows. Both are stored: which of the two a measure means is still unsettled, and keeping only one
    would decide it by accident.
    """
    feeds, meta = api.get_all(base, "/admin/feed-quality", token)
    snapshot = meta["snapshot_date"]

    body = api.get(base, "/admin/feed-quality", token, page_size=1)
    summary = body["summary"]

    rows = [
        row(period, "feed_quality", "total_future_opportunity_items",
            summary["total_future_opportunity_items"], snapshot_date=snapshot),
        row(period, "feed_quality", "total_feeds", summary["total_feeds"], snapshot_date=snapshot),
        row(period, "feed_quality", "feeds_scored", summary["feeds_scored"], snapshot_date=snapshot),
        row(period, "feed_quality", "average_score", summary["average_score"], snapshot_date=snapshot),
    ]

    for field in COMPLETENESS_FIELDS:
        # Volume-weighted, from the per-feed rows. A feed with no future items has weight zero and
        # so drops out of its own accord -- it is not evidence about a fleet-wide rate.
        numerator = weight_total = 0.0
        contributing = 0
        for feed in feeds:
            value = feed["completeness"].get(field)
            weight = feed.get("num_future_opportunity_items")
            if value is None or not weight or weight <= 0:
                continue
            numerator += value * weight
            weight_total += weight
            contributing += 1

        rows.append(row(
            period, "feed_quality", f"completeness_{field}_weighted",
            round(numerator / weight_total, 4) if weight_total else None,
            denominator=contributing, snapshot_date=snapshot))

        unweighted = summary["completeness"][field]
        rows.append(row(
            period, "feed_quality", f"completeness_{field}_unweighted",
            unweighted["average"], denominator=unweighted["feeds_reporting"],
            snapshot_date=snapshot))

    # The weight the volume-weighted figures were taken over, so they can be checked later.
    rows.append(row(period, "feed_quality", "completeness_weight_total",
                    weight_total, snapshot_date=snapshot))
    return rows


def activity_coverage_rows(base, token, period):
    """
    Both the labelled and the linked percentage.

    They answer different questions -- has an activity label at all, versus matches the activity
    list -- and move independently. Which one the activity-list measure means is not settled, so
    both are kept.
    """
    body = api.get(base, "/admin/activity-coverage/summary", token)
    data, snapshot = body["data"], body["meta"]["snapshot_date"]

    return [
        row(period, "activity_coverage", field, data[field], snapshot_date=snapshot)
        for field in (
            "activity_opportunities", "labelled_opportunities", "labelled_percentage",
            "linked_opportunities", "linked_percentage", "activities_offered", "vocabulary_size",
        )
    ]


def summary_rows(base, token, period):
    """Estate counts, and the three monitor counts stored one per monitor rather than summed."""
    body = api.get(base, "/admin/summary", token)
    data, snapshot = body["data"], body["meta"]["snapshot_date"]

    rows = [
        row(period, "summary", field, data[field], snapshot_date=snapshot)
        for field in ("feeds", "datasets", "publishers_monitored", "publishers_with_issues")
    ]

    counts = {m["monitor_id"]: m for m in data.get("monitors", [])}
    for monitor_id in CM13_MONITORS:
        monitor = counts.get(monitor_id)
        rows.append(row(
            period, "summary", f"past_threshold_{monitor_id}",
            monitor["past_threshold_count"] if monitor else None,
            denominator=monitor["count"] if monitor else None, snapshot_date=snapshot))

    return rows


def analytics_rows(base, token, period):
    """The public analytics surface, which is where the facility count lives."""
    data = api.get(base, "/summary", token)
    snapshot = data["date"][:10] if data.get("date") else None

    return [
        row(period, "analytics_summary", field, data[field], snapshot_date=snapshot)
        for field in (
            "number_of_facilities", "number_of_opportunities", "number_of_publishers",
            "number_of_activities", "number_of_facility_types", "number_of_activity_providers",
            "percentage_of_local_authorities",
        )
    ]


def ecosystem_growth_rows(base, token, period):
    """
    The month's crawl figure, which is an aggregate over the month rather than a reading taken on
    one day. Skipped while the month is still in progress: the figure covers only the days so far,
    and a captured measurement cannot be corrected.
    """
    body = api.get(base, "/admin/ecosystem-growth-trend", token, months=60)
    snapshot = body["meta"]["snapshot_date"]

    for point in body["data"]:
        if point["period_start"] != period.isoformat():
            continue
        if point["in_progress"]:
            print(f"  {period}: skipping datasets_crawled, the month is still in progress.")
            return []
        return [row(period, "ecosystem_growth", "datasets_crawled", point["datasets_crawled"],
                    denominator=point["days_with_data"], snapshot_date=snapshot)]

    print(f"  {period}: no ecosystem growth point for this month.")
    return []


def show(value) -> str:
    """Console formatting only: counts read as counts, rates keep their decimals."""
    if value is None:
        return ""
    if isinstance(value, float) and not value.is_integer():
        return f"{value:,.3f}"
    return f"{int(value):,}"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--api-base", default=api.DEFAULT_BASE)
    parser.add_argument(
        "--period",
        help="First day of the month to capture, e.g. 2026-11-01. Defaults to the month just ended.",
    )
    parser.add_argument("--dry-run", action="store_true",
                        help="print the measurements and write nothing")
    args = parser.parse_args()

    period = date.fromisoformat(args.period) if args.period else previous_month(date.today())
    if period.day != 1:
        sys.exit(f"--period must be the first of a month, not {period}.")
    if period > date.today():
        sys.exit(f"--period {period} is in the future.")

    admin, public = api.admin_token(), api.access_token()
    base = args.api_base

    print(f"Capturing {period:%B %Y}\n")
    rows = (
        feed_quality_rows(base, admin, period)
        + activity_coverage_rows(base, admin, period)
        + summary_rows(base, admin, period)
        + analytics_rows(base, public, period)
        + ecosystem_growth_rows(base, admin, period)
    )

    print(f"\n{'source':<18} {'field':<42} {'value':>14} {'denominator':>12}")
    for measurement in rows:
        value = show(measurement["value"]) or "null"
        denominator = show(measurement["denominator"])
        print(f"{measurement['source']:<18} {measurement['field']:<42} {value:>14} {denominator:>12}")

    missing = [m for m in rows if m["value"] is None]
    if missing:
        print(f"\n{len(missing)} measurement(s) have no value and will be stored as null, "
              "which the report reads as 'not available' rather than zero.")

    if args.dry_run:
        print(f"\nDry run. {len(rows)} measurements would be captured.")
        return 0

    return api.capture(base, admin, rows)


if __name__ == "__main__":
    sys.exit(main())
