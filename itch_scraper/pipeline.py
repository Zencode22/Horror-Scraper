"""End-to-end itch.io pipeline: fetch -> parse -> clean -> Excel.

This is the module the README's mermaid diagram collapses into "Itch Fetcher ->
Itch Parser -> Cleaning -> Excel Writer".  Call :func:`scrape_itch` from your
own script, or use ``python -m itch_scraper.main`` for a CLI run.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any, Optional

import pandas as pd

from itch_scraper.cleaning import (
    clean_dataframe,
    merge_details,
    records_to_dataframe,
)
from itch_scraper.config import CONFIG, ItchScraperConfig
from itch_scraper.excel_writer import write_itch_sheet
from itch_scraper.fetch_layer import ItchFetcher
from itch_scraper.logging_utils import RunStats, get_logger
from itch_scraper.parser import parse_game_page, parse_listing_page
from itch_scraper.robots import RobotsPolicy, allow_all_policy, fetch_robots


def scrape_itch(
    config: Optional[ItchScraperConfig] = None,
    *,
    fetcher: Optional[ItchFetcher] = None,
    write_output: bool = True,
    steam_frame: Optional[pd.DataFrame] = None,
) -> dict[str, Any]:
    """Scrape itch.io horror listings and return the run's artefacts.

    Args:
        config: Run configuration; defaults to :data:`CONFIG`.
        fetcher: Pre-built fetcher (tests inject a stubbed one).  When omitted a
            rate-limited :class:`~itch_scraper.fetch_layer.ItchFetcher` is used.
        write_output: Also write the Excel workbook.  Set ``False`` to only get
            the DataFrame back.
        steam_frame: Optional cleaned Steam frame so the ``Combined`` sheet can
            be produced in one pass once that branch exists.

    Returns:
        A dict with keys:

        ``frame``
            Cleaned itch ``DataFrame`` (README schema columns first).
        ``raw_records``
            Listing+detail records before cleaning - handy for debugging.
        ``stats``
            The :class:`~itch_scraper.logging_utils.RunStats` instance.
        ``workbook``
            Path of the written workbook, or ``None`` when not written.
        ``summary``
            Plain-dict run summary (also appended to ``logs/run_summary.json``).
    """
    config = config or CONFIG
    logger = get_logger(config)
    stats = RunStats(config, logger)
    stats.reset_failed_log()
    stats.info("starting itch.io scrape | pages<= %d | details=%s",
               config.max_pages, config.fetch_details)

    fetcher = fetcher or ItchFetcher(config, stats)
    robots = _load_robots(fetcher, config, stats)

    listings: list[dict[str, Any]] = []
    for source_url in _allowed_sources(config.source_urls, robots, stats):
        listings.extend(_crawl_listing(fetcher, source_url, robots, config, stats))

    stats.info("collected %d listing rows from %d source(s)",
               len(listings), len(config.source_urls))

    details_by_url: dict[str, dict[str, Any]] = {}
    if config.fetch_details:
        details_by_url = _fetch_details(fetcher, listings, robots, config, stats)

    merged = merge_details(listings, details_by_url)
    frame = clean_dataframe(records_to_dataframe(merged), stats=stats)

    workbook_path: Optional[Path] = None
    if write_output and len(frame):
        workbook_path = config.resolved_workbook_path()
        write_itch_sheet(
            frame,
            workbook_path,
            sheet_itch=config.sheet_itch,
            sheet_steam=config.sheet_steam,
            sheet_combined=config.sheet_combined,
            steam_frame=steam_frame,
            stats=stats,
        )
    elif write_output:
        stats.warning("nothing to write: the cleaned frame is empty")

    summary = {**stats.summary(), "cache": stats.cache_stats()}
    stats.logger.info("run summary: %s", json.dumps(summary, ensure_ascii=False))
    _append_run_summary(config, summary)
    stats.report()

    return {
        "frame": frame,
        "raw_records": merged,
        "stats": stats,
        "workbook": workbook_path,
        "summary": summary,
    }


# ---------------------------------------------------------------------- #
# crawl helpers                                                          #
# ---------------------------------------------------------------------- #
def _load_robots(fetcher: ItchFetcher, config: ItchScraperConfig, stats: RunStats) -> RobotsPolicy:
    """Read itch.io's robots.txt through the same polite fetcher."""
    try:
        return fetch_robots(fetcher, stats=stats)
    except Exception as exc:  # pragma: no cover - defensive
        stats.warning("robots lookup blew up (%s); crawling permissive policy", exc)
        return allow_all_policy()


def _allowed_sources(urls, robots: RobotsPolicy, stats: RunStats) -> list[str]:
    """Filter configured sources through the robots policy."""
    return robots.filter_urls(list(urls), stats=stats)


def _crawl_listing(
    fetcher: ItchFetcher,
    start_url: str,
    robots: RobotsPolicy,
    config: ItchScraperConfig,
    stats: RunStats,
) -> list[dict[str, Any]]:
    """Walk ``?page=N`` browse pages until exhausted or ``max_pages`` reached."""
    collected: list[dict[str, Any]] = []
    url: Optional[str] = start_url
    seen_pages: set[str] = set()

    for page_number in range(1, config.max_pages + 1):
        if url is None or url in seen_pages:
            break
        if not robots.is_allowed(url):
            stats.info("robots.txt blocks %s - stopping this source", url)
            break
        seen_pages.add(url)

        html = fetcher.get(url, context={"source": start_url, "page": page_number})
        if html is None:
            break

        rows = parse_listing_page(html, source_url=url, page=page_number)
        if not rows:
            stats.info("no game cards on %s (layout change?) - stopping", url)
            break

        stats.listings += len(rows)
        collected.extend(rows)
        stats.info("page %d of %s -> %d games", page_number, url, len(rows))

        url = fetcher.next_page_url(html, current=url)

    return collected


def _fetch_details(
    fetcher: ItchFetcher,
    listings: list[dict[str, Any]],
    robots: RobotsPolicy,
    config: ItchScraperConfig,
    stats: RunStats,
) -> dict[str, dict[str, Any]]:
    """Fetch each unique game page to recover its upload/release date."""
    urls: list[str] = []
    seen: set[str] = set()
    for row in listings:
        url = row.get("url")
        if url and url not in seen:
            seen.add(url)
            urls.append(url)

    urls = robots.filter_urls(urls, stats=stats)[: config.max_detail_requests]
    if len(urls) < len(seen):
        stats.info(
            "detail requests capped at %d of %d unique games",
            len(urls), len(seen),
        )

    details: dict[str, dict[str, Any]] = {}
    for index, url in enumerate(urls, start=1):
        html = fetcher.get(url, context={"detail_index": index})
        if html is None:
            continue
        payload = parse_game_page(html, url)
        details[url] = payload
        stats.details += 1
        if index % 20 == 0:
            stats.info("details: %d/%d fetched", index, len(urls))
    return details


def _append_run_summary(config: ItchScraperConfig, summary: dict[str, Any]) -> None:
    """Append the JSON summary line so trends across runs stay inspectable."""
    try:
        log_dir = Path(config.log_dir)
        log_dir.mkdir(parents=True, exist_ok=True)
        with (log_dir / "run_summary.jsonl").open("a", encoding="utf-8") as handle:
            handle.write(json.dumps(summary, ensure_ascii=False) + "\n")
    except OSError as exc:  # pragma: no cover
        # Logging must never take the pipeline down.
        print(f"warning: could not persist run summary: {exc}")
