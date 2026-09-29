"""Command-line entry point for the itch.io scraper.

Examples::

    # polite default run (3 listing pages per source, detail pages included)
    python -m itch_scraper.main

    # quick smoke test: one page, no detail requests, fresh cache
    python -m itch_scraper.main --max-pages 1 --no-details --purge-cache

    # dry run: scrape and clean but do not touch the workbook
    python -m itch_scraper.main --dry-run

Exit codes: ``0`` success, ``1`` nothing scraped, ``2`` bad arguments.
"""

from __future__ import annotations

import argparse
import sys
from dataclasses import replace
from typing import Optional, Sequence

from itch_scraper.config import CONFIG, ItchScraperConfig
from itch_scraper.pipeline import scrape_itch


def build_parser() -> argparse.ArgumentParser:
    """Create the CLI parser (kept separate so tests can introspect it)."""
    parser = argparse.ArgumentParser(
        prog="python -m itch_scraper.main",
        description="Scrape horror games from itch.io into the project workbook.",
    )
    parser.add_argument("--max-pages", type=int, default=CONFIG.max_pages,
                        help=f"listing pages per source URL (default: {CONFIG.max_pages})")
    parser.add_argument("--min-delay", type=float, default=CONFIG.min_delay,
                        help="minimum polite delay between requests, seconds")
    parser.add_argument("--max-delay", type=float, default=CONFIG.max_delay,
                        help="maximum polite delay between requests, seconds")
    parser.add_argument("--retries", type=int, default=CONFIG.max_retries,
                        help="retry attempts per request with exponential backoff")
    parser.add_argument("--max-detail-requests", type=int, default=CONFIG.max_detail_requests,
                        help="cap on game-page requests used to recover dates")
    parser.add_argument("--no-details", action="store_true",
                        help="skip game pages entirely (release_date will be empty)")
    parser.add_argument("--cache-ttl", type=float, default=CONFIG.cache_ttl,
                        help="seconds a cached response stays valid")
    parser.add_argument("--no-cache", action="store_true",
                        help="ignore any cached responses for this run")
    parser.add_argument("--purge-cache", action="store_true",
                        help="delete stale cache entries before crawling")
    parser.add_argument("--output", type=str, default=None,
                        help="workbook path override (default: output/workbook.xlsx)")
    parser.add_argument("--dry-run", action="store_true",
                        help="scrape + clean, but do not write the workbook")
    parser.add_argument("--quiet", action="store_true",
                        help="only print the final summary line")
    return parser


def config_from_args(args: argparse.Namespace) -> ItchScraperConfig:
    """Derive a run configuration from parsed CLI arguments."""
    config = replace(
        CONFIG,
        max_pages=max(1, args.max_pages),
        min_delay=max(0.0, args.min_delay),
        max_delay=max(args.min_delay, args.max_delay),
        max_retries=max(1, args.retries),
        max_detail_requests=max(0, args.max_detail_requests),
        fetch_details=not args.no_details,
        cache_ttl=args.cache_ttl,
    )
    if args.output:
        from pathlib import Path

        path = Path(args.output)
        config = replace(config, output_dir=path.parent or Path("."),
                         workbook_name=path.name)
    if args.no_cache:
        # A TTL of 0 makes every lookup a miss without touching files on disk.
        config = replace(config, cache_ttl=0.0)
    return config


def main(argv: Optional[Sequence[str]] = None) -> int:
    """Parse arguments, run the pipeline and print a human-friendly summary."""
    args = build_parser().parse_args(argv)

    if args.min_delay > args.max_delay:
        print("error: --min-delay cannot exceed --max-delay", file=sys.stderr)
        return 2

    config = config_from_args(args)
    result = scrape_itch(config, write_output=not args.dry_run)
    stats = result["stats"]

    if args.purge_cache:
        removed = getattr(stats, "cache", None) and stats.cache.purge()
        print(f"purged {removed or 0} stale cache entries")

    frame = result["frame"]
    summary = result["summary"]
    print("-" * 68)
    print(f"itch rows kept         : {len(frame)}")
    print(f"http requests          : {summary['requests']} "
          f"(cache hits {summary['cache_hits']}, retries {summary['retries']})")
    print(f"listings / details     : {summary['listings_parsed']} / {summary['details_parsed']}")
    print(f"failures               : {summary['failures']} ({summary['failed_requests_log']})")
    if frame is not None and len(frame):
        dated = int(frame["release_date"].notna().sum())
        free = int((frame["price_usd"] == 0).sum())
        print(f"rows with release date : {dated}/{len(frame)}")
        print(f"free / paid            : {free} / {len(frame) - free}")
        if not args.quiet:
            with _suppress_pandas_truncation():
                print(frame[list(frame.columns[:6])].head(10).to_string(index=False))
    workbook = result["workbook"]
    print(f"workbook               : {workbook if workbook else '(not written)'}")
    return 0 if len(frame) else 1


class _suppress_pandas_truncation:
    """Widen pandas display just for the console preview."""

    def __enter__(self):
        import pandas as pd

        self._old = (pd.get_option("display.width"), pd.get_option("display.max_colwidth"))
        pd.set_option("display.width", 200)
        pd.set_option("display.max_colwidth", 40)
        return self

    def __exit__(self, *_exc):
        import pandas as pd

        pd.set_option("display.width", self._old[0])
        pd.set_option("display.max_colwidth", self._old[1])
        return False


if __name__ == "__main__":  # pragma: no cover - CLI wrapper
    raise SystemExit(main())
