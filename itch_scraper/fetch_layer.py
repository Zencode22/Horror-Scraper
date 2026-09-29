"""FETCH LAYER for itch.io: rate limiting, retries and caching in one client.

Mirrors the README boxes ``Itch Fetcher`` + ``Rate Limiter (1-2 sec crawl
delay)``.  The design goal is that *every* outbound request goes through
:meth:`ItchFetcher.get`, so politeness can never be forgotten by accident.

Usage::

    fetcher = ItchFetcher(CONFIG, stats)
    html = fetcher.get("https://itch.io/games/tag-horror")   # str | None
"""

from __future__ import annotations

import random
import time
from typing import Any, Optional
from urllib.parse import urljoin

import requests
from requests.exceptions import RequestException

from itch_scraper.cache_store import CacheWriteError, ResponseCache


class RateLimiter:
    """Enforces a minimum, randomly-jittered gap between two requests.

    Args:
        min_delay: Lower bound of the sleep window, in seconds.
        max_delay: Upper bound of the sleep window, in seconds.
        sleeper: Injectable sleep function (patched out in tests).
        rng: Injectable uniform-random source (patched out in tests).
    """

    def __init__(
        self,
        min_delay: float = 1.0,
        max_delay: float = 2.0,
        sleeper=time.sleep,
        rng: Optional[Any] = None,
    ) -> None:
        if max_delay < min_delay:
            min_delay, max_delay = max_delay, min_delay
        self.min_delay = float(min_delay)
        self.max_delay = float(max_delay)
        self._sleep = sleeper
        self._rng = rng or random
        self._next_allowed_at = 0.0
        self.waited_seconds = 0.0
        self.waits = 0

    def delay_for_next_request(self) -> float:
        """Sleep until the polite window opens; returns seconds slept."""
        now = time.monotonic()
        already = self._next_allowed_at - now
        wait = max(0.0, already)
        if wait <= 0:
            # Fresh gap: draw the jitter for the *following* request.
            wait = self._rng.uniform(self.min_delay, self.max_delay)
        self._sleep(wait)
        self.waits += 1
        self.waited_seconds += wait
        self._next_allowed_at = time.monotonic() + self._rng.uniform(
            self.min_delay, self.max_delay
        )
        return wait

    def backoff_sleep(self, attempt: int, base: float, cap: float) -> float:
        """Sleep ``base ** attempt`` seconds (capped) after a failed try."""
        raw = min(cap, base ** max(1, attempt))
        # Full jitter avoids synchronised retry storms.
        delay = self._rng.uniform(0.0, raw) if raw > 0 else 0.0
        self._sleep(delay)
        self.waited_seconds += delay
        return delay


class ItchFetcher:
    """Polite HTTP GET client for itch.io with cache + retry + logging.

    Args:
        config: An :class:`~itch_scraper.config.ItchScraperConfig`.
        stats: A :class:`~itch_scraper.logging_utils.RunStats` used for counters
            and failure records.
        session: Optional pre-configured :class:`requests.Session` (tests inject
            a stub here).
        cache: Optional :class:`~itch_scraper.cache_store.ResponseCache`; built
            from ``config`` when omitted.
        limiter: Optional :class:`RateLimiter`; built from ``config`` otherwise.
        clock: Injectable monotonic clock (used by tests).
    """

    def __init__(
        self,
        config: Any,
        stats: Any,
        session: Optional[requests.Session] = None,
        cache: Optional[ResponseCache] = None,
        limiter: Optional[RateLimiter] = None,
    ) -> None:
        self.config = config
        self.stats = stats
        self.user_agent = config.user_agent
        self.session = session or self._build_session(config.user_agent)
        self.cache = cache if cache is not None else ResponseCache(
            config.cache_dir, ttl=config.cache_ttl
        )
        self.limiter = limiter or RateLimiter(config.min_delay, config.max_delay)
        # Let the run summary report cache utilisation.
        if hasattr(stats, "attach_cache"):
            stats.attach_cache(self.cache)

    # ------------------------------------------------------------------ #
    # setup                                                              #
    # ------------------------------------------------------------------ #
    @staticmethod
    def _build_session(user_agent: str) -> requests.Session:
        """Create a session with sane headers and no automatic redirects loop."""
        session = requests.Session()
        session.headers.update(
            {
                "User-Agent": user_agent,
                "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
                "Accept-Language": "en-US,en;q=0.9",
            }
        )
        return session

    # ------------------------------------------------------------------ #
    # main entry point                                                   #
    # ------------------------------------------------------------------ #
    def get(self, url: str, context: Optional[dict[str, Any]] = None) -> Optional[str]:
        """Fetch ``url`` politely and return its body, or ``None`` on failure.

        Order of operations: normalize URL -> consult cache -> rate-limit ->
        request (with exponential-backoff retries) -> cache the good response.

        Args:
            url: Absolute URL, or relative to ``https://itch.io/``.
            context: Extra fields recorded alongside failures (page number, ...).

        Returns:
            The decoded response text, or ``None`` when every attempt failed or
            the server answered with a permanent error (4xx other than 429).
        """
        target = self.absolute(url)

        cached = self.cache.get(target)
        if cached is not None:
            self.stats.cache_hits += 1
            self.stats.requests += 1
            self.stats.info("cache hit  %s (%.0f s old)", target, cached.age())
            return cached.text

        last_status: Optional[int] = None
        last_error: Optional[BaseException] = None

        for attempt in range(1, self.config.max_retries + 1):
            if attempt > 1:
                self.stats.retries += 1
                waited = self.limiter.backoff_sleep(
                    attempt - 1, self.config.backoff_base, self.config.backoff_cap
                )
                self.stats.info(
                    "retry %d/%d for %s after %.1fs backoff",
                    attempt, self.config.max_retries, target, waited,
                )

            self.limiter.delay_for_next_request()
            self.stats.requests += 1
            try:
                response = self.session.get(target, timeout=self.config.request_timeout)
            except RequestException as exc:
                last_error, last_status = exc, None
                self.stats.warning("request error for %s: %s", target, exc)
                continue

            status = int(response.status_code)
            last_status = status

            if status == 200:
                text = response.text
                try:
                    self.cache.put(target, text, status)
                except CacheWriteError as exc:  # pragma: no cover
                    self.stats.warning("cache write failed for %s: %s", target, exc)
                return text

            if status == 404:
                # Removed / renamed game page: permanent, do not retry.
                self.stats.record_failure(
                    target, "http_404", status_code=status, attempts=attempt,
                    context=context,
                )
                self.stats.info("404 (gone) %s", target)
                return None

            if status in self.config.retry_statuses:
                last_error = None
                self.stats.warning("transient HTTP %s for %s", status, target)
                continue

            # Anything else (401/403/410/5xx-not-listed...) is treated as final.
            self.stats.record_failure(
                target, f"http_{status}", status_code=status, attempts=attempt,
                context=context,
            )
            self.stats.warning("giving up on %s (HTTP %s)", target, status)
            return None

        self.stats.record_failure(
            target,
            "exhausted_retries" if last_error is None else "error_after_retries",
            status_code=last_status,
            attempts=self.config.max_retries,
            error=last_error,
            context=context,
        )
        self.stats.warning("exhausted %d attempts for %s", self.config.max_retries, target)
        return None

    # ------------------------------------------------------------------ #
    # helpers                                                            #
    # ------------------------------------------------------------------ #
    @staticmethod
    def absolute(url: str, base: str = "https://itch.io/") -> str:
        """Resolve ``url`` against itch.io so cache keys stay consistent."""
        return urljoin(base, url)

    def next_page_url(self, html: str, current: str) -> Optional[str]:
        """Return the ``?page=N`` link itch.io puts at the bottom of a listing.

        Args:
            html: The listing page body.
            current: URL of the page we just parsed (used to avoid loops).

        Returns:
            The absolute next-page URL, or ``None`` on the last page.
        """
        from bs4 import BeautifulSoup  # local import: only parser module needs bs4

        soup = BeautifulSoup(html, "lxml")
        for anchor in soup.select('a[href*="page="]'):
            label = (anchor.get_text(strip=True) or "").lower()
            href = anchor.get("href") or ""
            if "next" in label or "›" in label or "→" in label:
                candidate = self.absolute(href)
                if candidate != current:
                    return candidate
        return None
