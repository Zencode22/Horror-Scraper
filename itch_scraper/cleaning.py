"""CLEANING & VALIDATION layer (README: Deduplicator / Normalizer / Validator).

Input:  list of raw records produced by :mod:`itch_scraper.parser`
Output: a ``pandas.DataFrame`` with exactly the README schema columns plus a
few itch-specific extras.

Pipeline order matters and is fixed here:

1. **merge** listing rows with their detail-page payload (by URL)
2. **normalize** prices/dates/strings
3. **deduplicate** on normalized title + developer (README's rule)
4. **validate** drop null/broken rows
"""

from __future__ import annotations

import math
from typing import Any, Iterable, Optional

import pandas as pd

from itch_scraper.utils import _clean_text, normalize_price, normalize_title, to_iso_date

#: The README "Planned Spreadsheet Schema", in order.
BASE_COLUMNS: tuple[str, ...] = (
    "title",
    "platform",
    "price_usd",
    "release_date",
    "developer",
    "reviews_positive",
)

#: Extra columns that are useful for itch rows (kept after the base schema).
EXTRA_COLUMNS: tuple[str, ...] = (
    "url",
    "game_id",
    "updated_at",
    "rating_count",
    "rating_value",
    "genre",
    "tags",
    "status",
    "platforms",
    "short_description",
    "source_url",
    "page",
)


# ---------------------------------------------------------------------- #
# step 1 - merge                                                         #
# ---------------------------------------------------------------------- #
def merge_details(
    listings: Iterable[dict[str, Any]],
    details_by_url: Optional[dict[str, dict[str, Any]]] = None,
) -> list[dict[str, Any]]:
    """Attach detail-page fields to their listing rows.

    Args:
        listings: Raw records from :func:`~itch_scraper.parser.parse_listing_page`.
        details_by_url: ``{url: parse_game_page(...)}``; missing entries simply
            leave the date fields empty (the validator decides later).

    Returns:
        New list of merged dicts (input objects are not mutated).
    """
    details_by_url = details_by_url or {}
    merged: list[dict[str, Any]] = []
    seen_urls: set[str] = set()

    for row in listings:
        record = dict(row)
        url = _clean_text(record.get("url"))
        # Same game can appear in several browse views; keep the first sighting.
        if url and url in seen_urls:
            continue
        seen_urls.add(url)

        detail = details_by_url.get(url) or {}
        for key in (
            "release_date",
            "release_date_raw",
            "updated_at",
            "updated_at_raw",
            "rating_count",
            "rating_value",
            "status",
            "tags",
            "genres",
        ):
            if detail.get(key) not in (None, "", []) and record.get(key) in (None, "", []):
                record[key] = detail[key]

        # Prefer the developer shown on the game page when the listing was blank.
        if not record.get("developer") and detail.get("developer"):
            record["developer"] = detail["developer"]

        # Price: listing tag wins (it reflects the sale actually running); fall
        # back to the detail page only when the listing had no price at all.
        if record.get("price_usd") is None and detail.get("price_usd") is not None:
            record["price_usd"] = detail["price_usd"]
            record["price_raw"] = detail.get("price_raw")

        if detail.get("title"):
            record.setdefault("detail_title", detail["title"])

        merged.append(record)

    return merged


# ---------------------------------------------------------------------- #
# step 2 - normalize                                                     #
# ---------------------------------------------------------------------- #
def normalize_rows(records: Iterable[dict[str, Any]]) -> list[dict[str, Any]]:
    """Normalize every record to the output schema (price -> USD, date -> ISO).

    * ``title``/``developer`` are whitespace-cleaned strings.
    * ``price_usd`` becomes a ``float`` (``NaN`` when unparseable).
    * ``release_date`` becomes ``YYYY-MM-DD`` (``NaT``-ish ``None`` otherwise).
    * ``reviews_positive`` stays ``None`` for itch rows - itch.io publishes star
      ratings, not positive/negative review counts (that column is Steam-only).
    """
    normalized: list[dict[str, Any]] = []
    for raw in records:
        row = dict(raw)
        release = row.get("release_date") or row.get("release_date_raw")
        updated = row.get("updated_at") or row.get("updated_at_raw")

        row["title"] = _clean_text(row.get("title") or row.get("detail_title"))
        row["developer"] = _clean_text(row.get("developer"))
        row["platform"] = "itch"
        row["price_usd"] = _price_or_nan(row.get("price_usd", row.get("price_raw")))
        row["release_date"] = to_iso_date(release)
        row["updated_at"] = to_iso_date(updated)
        row["reviews_positive"] = None
        row["genre"] = _clean_text(row.get("genre") or (row.get("genres") or [""])[0])
        row["tags"] = ", ".join(row.get("tags") or []) if isinstance(row.get("tags"), list) else _clean_text(row.get("tags"))
        row["platforms"] = ", ".join(row.get("platforms") or []) if isinstance(row.get("platforms"), list) else _clean_text(row.get("platforms"))
        row["status"] = _clean_text(row.get("status"))
        row["game_id"] = _clean_text(row.get("game_id")) or None
        normalized.append(row)
    return normalized


def _price_or_nan(value: Any) -> float:
    """Return a float price, or ``NaN`` when nothing usable is available."""
    if value is None or (isinstance(value, float) and math.isnan(value)):
        parsed = None
    elif isinstance(value, (int, float)):
        parsed = float(value)
    else:
        parsed = normalize_price(value)
    return float("nan") if parsed is None else round(float(parsed), 2)


# ---------------------------------------------------------------------- #
# step 3 - deduplicate                                                   #
# ---------------------------------------------------------------------- #
def deduplicate(records: Iterable[dict[str, Any]]) -> list[dict[str, Any]]:
    """Drop duplicates matched on *normalized title + developer* (README rule).

    When two rows collide we keep the richer one (more non-empty fields), which
    makes re-running over overlapping browse views idempotent.
    """
    best: dict[tuple[str, str], dict[str, Any]] = {}
    for row in records:
        key = (normalize_title(row.get("title")), normalize_title(row.get("developer")))
        previous = best.get(key)
        if previous is None or _richness(row) > _richness(previous):
            best[key] = row
    return list(best.values())


def _richness(row: dict[str, Any]) -> int:
    """Count populated, meaningful fields - used to pick the keeper duplicate."""
    score = 0
    for field in ("title", "developer", "url", "release_date", "updated_at",
                  "genre", "tags", "status", "short_description"):
        value = row.get(field)
        if value not in (None, "", [], {}) and not (isinstance(value, float) and math.isnan(value)):
            score += 1
    if not isinstance(row.get("price_usd"), float) or not math.isnan(row.get("price_usd")):
        score += 1
    return score


# ---------------------------------------------------------------------- #
# step 4 - validate                                                      #
# ---------------------------------------------------------------------- #
def validate_rows(records: Iterable[dict[str, Any]]) -> list[dict[str, Any]]:
    """Drop broken rows: no title, no URL, or an unusable price.

    A missing ``release_date`` is *not* fatal (plenty of itch games never show
    one) but it is reported so the operator can judge coverage.
    """
    kept: list[dict[str, Any]] = []
    for row in records:
        title = _clean_text(row.get("title"))
        url = _clean_text(row.get("url"))
        price = row.get("price_usd")
        if not title or not url:
            continue
        if price is None or (isinstance(price, float) and math.isnan(price)):
            continue
        if price < 0 or price > 10_000:  # obvious scraping artifacts
            continue
        kept.append(row)
    return kept


# ---------------------------------------------------------------------- #
# dataframe helpers                                                      #
# ---------------------------------------------------------------------- #
def records_to_dataframe(records: Iterable[dict[str, Any]]) -> pd.DataFrame:
    """Build the final frame: schema columns first, extras after."""
    frame = pd.DataFrame(list(records))
    ordered = [c for c in BASE_COLUMNS + EXTRA_COLUMNS if c in frame.columns]
    rest = [c for c in frame.columns if c not in ordered]
    return frame.reindex(columns=ordered + rest)


def clean_dataframe(frame: pd.DataFrame, stats: Any | None = None) -> pd.DataFrame:
    """Run normalize -> dedupe -> validate on an already-built DataFrame.

    Provided so the Steam branch (and tests) can reuse the same cleaning rules
    on any source's raw records.

    Args:
        frame: Raw records frame.
        stats: Optional run-stats object for coverage logging.

    Returns:
        Cleaned frame with the schema columns guaranteed to exist.
    """
    records = frame.to_dict("records")
    cleaned = validate_rows(deduplicate(normalize_rows(records)))
    result = records_to_dataframe(cleaned)
    for column in BASE_COLUMNS:
        if column not in result.columns:
            result[column] = None
    if stats is not None:
        total = len(result)
        dated = int(result["release_date"].notna().sum()) if total else 0
        stats.info(
            "cleaning: %d raw -> %d rows (%d unique after dedupe pass), "
            "%d/%d carry a release date",
            len(records), total, total, dated, total or 1,
        )
    return result
