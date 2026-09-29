"""Central configuration for the Itch.io scraper.

Every knob that the README mentions (crawl delay, retries, cache location,
output workbook name, ...) lives here so the rest of the package stays free of
hard-coded magic numbers.  Values can be overridden with environment variables
so a run can be tuned without editing code::

    ITCH_MAX_PAGES=3 ITCH_FETCH_DETAILS=0 python -m itch_scraper.main
"""

from __future__ import annotations

import os
from dataclasses import dataclass, field
from pathlib import Path

#: Project root = the directory that contains this package.
PROJECT_ROOT = Path(__file__).resolve().parent.parent


def _env_int(name: str, default: int) -> int:
    """Read an ``int`` from the environment, falling back to ``default``."""
    raw = os.environ.get(name, "").strip()
    try:
        return int(raw) if raw else default
    except ValueError:
        return default


def _env_float(name: str, default: float) -> float:
    """Read a ``float`` from the environment, falling back to ``default``."""
    raw = os.environ.get(name, "").strip()
    try:
        return float(raw) if raw else default
    except ValueError:
        return default


def _env_bool(name: str, default: bool) -> bool:
    """Read a ``bool`` from the environment ("1/true/yes/on"), or ``default``."""
    raw = os.environ.get(name, "").strip().lower()
    if not raw:
        return default
    return raw in {"1", "true", "yes", "y", "on"}


@dataclass(frozen=True)
class ItchScraperConfig:
    """Runtime configuration for the itch.io branch of the pipeline."""

    # ------------------------------------------------------------------ #
    # DATA SOURCE                                                        #
    # ------------------------------------------------------------------ #
    #: Genre/tag browse pages to crawl.  ``itch.io`` throttles aggressively,
    #: so keep the list short and let ``max_pages`` do the rest.
    source_urls: tuple[str, ...] = (
        "https://itch.io/games/tag-horror",
        "https://itch.io/games/newest/tag-horror",
    )

    # ------------------------------------------------------------------ #
    # FETCH LAYER                                                        #
    # ------------------------------------------------------------------ #
    user_agent: str = (
        "HorrorGameDataScraper/0.1 (+collaborative research project; "
        "respectful of robots.txt)"
    )
    request_timeout: float = 20.0

    #: Politeness delay applied *before every* request.  The README asks for a
    #: 1-2 second crawl delay, so we draw uniformly from that range.
    min_delay: float = field(default_factory=lambda: _env_float("ITCH_MIN_DELAY", 1.0))
    max_delay: float = field(default_factory=lambda: _env_float("ITCH_MAX_DELAY", 2.0))

    #: Retry policy (exponential backoff, per README "Scraping Etiquette").
    max_retries: int = field(default_factory=lambda: _env_int("ITCH_MAX_RETRIES", 4))
    backoff_base: float = field(default_factory=lambda: _env_float("ITCH_BACKOFF_BASE", 2.0))
    backoff_cap: float = field(default_factory=lambda: _env_float("ITCH_BACKOFF_CAP", 60.0))

    #: HTTP statuses treated as transient and therefore retried.
    retry_statuses: frozenset[int] = frozenset({408, 425, 429, 500, 502, 503, 504})

    # ------------------------------------------------------------------ #
    # CRAWL DEPTH                                                        #
    # ------------------------------------------------------------------ #
    #: Maximum browse-listing pages fetched per source URL.
    max_pages: int = field(default_factory=lambda: _env_int("ITCH_MAX_PAGES", 3))
    #: Also visit each game page to recover the upload/release date.  This
    #: multiplies the request count, so it is opt-out via ``ITCH_FETCH_DETAILS``.
    fetch_details: bool = field(default_factory=lambda: _env_bool("ITCH_FETCH_DETAILS", True))
    #: Hard cap on detail requests per run (safety valve).
    max_detail_requests: int = field(
        default_factory=lambda: _env_int("ITCH_MAX_DETAIL_REQUESTS", 120)
    )

    # ------------------------------------------------------------------ #
    # CACHE / LOGS / OUTPUT                                              #
    # ------------------------------------------------------------------ #
    cache_dir: Path = field(default_factory=lambda: Path(os.environ.get("ITCH_CACHE_DIR", PROJECT_ROOT / "cache" / "itch")))
    log_dir: Path = field(default_factory=lambda: Path(os.environ.get("ITCH_LOG_DIR", PROJECT_ROOT / "logs")))
    output_dir: Path = field(default_factory=lambda: Path(os.environ.get("ITCH_OUTPUT_DIR", PROJECT_ROOT / "output")))
    workbook_name: str = os.environ.get("ITCH_WORKBOOK", "workbook.xlsx")

    #: Sheet names used inside the workbook (README: Combined / Steam / Itch).
    sheet_combined: str = "Combined"
    sheet_steam: str = "Steam"
    sheet_itch: str = "Itch"

    #: How long cached responses stay valid (seconds).  Default: 12 hours.
    cache_ttl: float = field(default_factory=lambda: _env_float("ITCH_CACHE_TTL", 12 * 3600))

    def resolved_workbook_path(self) -> Path:
        """Absolute path of the Excel workbook this config writes to."""
        return self.output_dir / self.workbook_name


#: Shared default configuration instance.
CONFIG = ItchScraperConfig()
