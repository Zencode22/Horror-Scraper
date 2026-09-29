"""Log / error handler for the itch.io scraper (README: "Log / Error Handler").

Three sinks, all cheap to reason about:

* ``logs/itch_scraper.log`` - rotating text log (INFO level by default).
* ``logs/failed_requests.jsonl`` - one JSON object per request that never
  succeeded, so a human can re-check it later.
* console - only when the module is used directly (library imports keep the
  terminal quiet and just write to the file).
"""

from __future__ import annotations

import json
import logging
import logging.handlers
from datetime import datetime, timezone
from pathlib import Path
from types import TracebackType
from typing import Any, Optional

_LOGGER_NAME = "itch_scraper"
_FAILED_FILENAME = "failed_requests.jsonl"


def get_logger(config: Any | None = None) -> logging.Logger:
    """Return the shared scraper logger, attaching file handlers once.

    Args:
        config: An :class:`~itch_scraper.config.ItchScraperConfig` (or anything
            exposing ``log_dir``).  When omitted the logger still works but only
            writes to the console stream.

    Returns:
        The configured :class:`logging.Logger`.
    """
    logger = logging.getLogger(_LOGGER_NAME)
    if getattr(logger, "_itch_configured", False):
        return logger

    logger.setLevel(logging.INFO)
    logger.propagate = False

    formatter = logging.Formatter(
        "%(asctime)s | %(levelname)-7s | %(name)s | %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S",
    )

    if config is not None:
        log_dir = Path(config.log_dir)
        log_dir.mkdir(parents=True, exist_ok=True)
        handler: logging.Handler = logging.handlers.RotatingFileHandler(
            log_dir / "itch_scraper.log",
            maxBytes=2_000_000,
            backupCount=3,
            encoding="utf-8",
        )
    else:  # pragma: no cover - fallback path
        handler = logging.NullHandler()

    handler.setFormatter(formatter)
    logger.addHandler(handler)
    logger._itch_configured = True  # type: ignore[attr-defined]
    return logger


class RunStats:
    """Mutable counters + failure journal for a single scraping run.

    Attributes:
        requests: Number of HTTP responses consumed (cache hits included).
        cache_hits: Subset of ``requests`` served from the local cache.
        retries: Number of extra attempts triggered by transient failures.
        listings: Browse-listing rows parsed.
        details: Game detail pages parsed.
        failures: Recorded :func:`record_failure` payloads.
    """

    def __init__(self, config: Any, logger: Optional[logging.Logger] = None) -> None:
        self.config = config
        self.logger = logger or get_logger(config)
        self.started_at = datetime.now(timezone.utc)
        self.cache: Any | None = None
        self.requests = 0
        self.cache_hits = 0
        self.retries = 0
        self.listings = 0
        self.details = 0
        self.failures = 0
        self.failed_path = Path(config.log_dir) / _FAILED_FILENAME
        self._append_lock_needed = True

    # ------------------------------------------------------------------ #
    # logging helpers                                                    #
    # ------------------------------------------------------------------ #
    def info(self, message: str, *args: Any) -> None:
        """Log an informational message."""
        self.logger.info(message, *args)

    def warning(self, message: str, *args: Any) -> None:
        """Log a warning message."""
        self.logger.warning(message, *args)

    def record_failure(
        self,
        url: str,
        reason: str,
        status_code: Optional[int] = None,
        attempts: int = 0,
        error: Optional[BaseException] = None,
        context: Optional[dict[str, Any]] = None,
    ) -> None:
        """Append a failed-request entry to ``logs/failed_requests.jsonl``.

        Args:
            url: The request URL that could not be completed.
            reason: Short machine-friendly cause ("http_429", "timeout", ...).
            status_code: Final HTTP status, when a response was received.
            attempts: How many tries were burned before giving up.
            error: The exception that ended the retries, if any.
            context: Extra fields worth keeping (game title, page number...).
        """
        self.failures += 1
        payload: dict[str, Any] = {
            "timestamp": datetime.now(timezone.utc).isoformat(),
            "url": url,
            "reason": reason,
            "status_code": status_code,
            "attempts": attempts,
            "error": f"{type(error).__name__}: {error}" if error else None,
            "context": context or {},
        }
        try:
            self.failed_path.parent.mkdir(parents=True, exist_ok=True)
            with self.failed_path.open("a", encoding="utf-8") as handle:
                handle.write(json.dumps(payload, ensure_ascii=False) + "\n")
        except OSError as exc:  # pragma: no cover - disk problems shouldn't kill runs
            self.logger.error("Could not persist failure record: %s", exc)

    # ------------------------------------------------------------------ #
    # reporting                                                          #
    # ------------------------------------------------------------------ #
    def summary(self) -> dict[str, Any]:
        """Return the run counters as a plain dict (handy for logs/tests)."""
        duration = (datetime.now(timezone.utc) - self.started_at).total_seconds()
        return {
            "duration_seconds": round(duration, 2),
            "requests": self.requests,
            "cache_hits": self.cache_hits,
            "retries": self.retries,
            "listings_parsed": self.listings,
            "details_parsed": self.details,
            "failures": self.failures,
            "failed_requests_log": str(self.failed_path),
        }

    def report(self) -> None:
        """Log the run summary at INFO level."""
        data = self.summary()
        self.logger.info(
            "Run finished in %(duration_seconds)s s | %(requests)s requests "
            "(%(cache_hits)s cached, %(retries)s retries) | "
            "%(listings_parsed)s listing rows, %(details_parsed)s detail pages | "
            "%(failures)s failures -> %(failed_requests_log)s",
            **data,
        )

    def reset_failed_log(self) -> None:
        """Start a fresh failure journal for this run (keeps history out)."""
        if self._append_lock_needed and self.failed_path.exists():
            self.failed_path.unlink()

    def attach_cache(self, cache: Any) -> None:
        """Remember the response cache so :meth:`cache_stats` can report it."""
        self.cache = cache

    def cache_stats(self) -> dict[str, Any]:
        """Cache utilisation snapshot (empty when no cache was attached)."""
        cache = getattr(self, "cache", None)
        if cache is None:
            return {}
        try:
            return dict(cache.stats())
        except Exception:  # pragma: no cover - reporting must never raise
            return {}


# Context-manager sugar so callers can do `with error_boundary(stats): ...`
class error_boundary:  # noqa: N801 - reads like a keyword at call sites
    """Swallow-and-log guard: never let one bad row abort the whole crawl."""

    def __init__(self, stats: RunStats, label: str, url: str = "") -> None:
        self.stats = stats
        self.label = label
        self.url = url
        self.caught: Optional[BaseException] = None

    def __enter__(self) -> "error_boundary":
        return self

    def __exit__(
        self,
        exc_type: Optional[type[BaseException]],
        exc: Optional[BaseException],
        tb: Optional[TracebackType],
    ) -> bool:
        if exc_type is None:
            return False
        self.caught = exc
        self.stats.record_failure(
            url=self.url or self.label,
            reason=f"{self.label}:{exc_type.__name__}",
            error=exc,
        )
        self.stats.warning("Skipping %s after error: %s", self.label, exc)
        return True  # suppress - the crawl continues with the next item
