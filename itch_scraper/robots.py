"""Robots.txt awareness for the itch.io crawl.

The README promises polite crawling; the cheapest way to keep that promise is
to actually read ``https://itch.io/robots.txt`` once per run, cache it next to
the page cache, and refuse to fetch anything it disallows.  Anything odd
(network error, malformed file) fails *open* with a warning - we would rather
skip a page than crash the whole pipeline over a missing robots file.
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from typing import Any, Optional
from urllib import robotparser
from urllib.parse import urljoin, urlparse


@dataclass
class RobotsPolicy:
    """Wraps :mod:`urllib.robotparser` with our caching/logging conventions.

    Attributes:
        allowed_paths: Disallowed prefixes discovered (for logging only).
        loaded: Whether a robots.txt body was successfully parsed.
    """

    parser: robotparser.RobotFileParser = field(default_factory=robotparser.RobotFileParser)
    user_agent: str = "*"
    loaded: bool = False
    disallow: list[str] = field(default_factory=list)

    # ------------------------------------------------------------------ #
    # construction                                                       #
    # ------------------------------------------------------------------ #
    @classmethod
    def from_text(cls, text: str, user_agent: str) -> "RobotsPolicy":
        """Build a policy from an already-downloaded robots.txt body.

        Args:
            text: Raw ``robots.txt`` contents.
            user_agent: The UA string used for the crawl.

        Returns:
            A ready-to-query :class:`RobotsPolicy`.
        """
        policy = cls(user_agent=user_agent)
        policy.parser.parse_lines(text.splitlines())
        policy.loaded = True
        policy.disallow = _extract_disallow(text)
        return policy

    @classmethod
    def allow_all(cls, reason: str) -> "RobotsPolicy":
        """Return a permissive policy when robots.txt is unavailable.

        Args:
            reason: Why we could not read robots.txt (logged by the caller).

        Returns:
            A policy whose :meth:`is_allowed` always returns ``True``.
        """
        policy = cls()
        policy.parser.parse_lines([])
        policy.loaded = False
        policy.disallow = []
        policy.reason = reason  # type: ignore[attr-defined]
        return policy

    # ------------------------------------------------------------------ #
    # queries                                                            #
    # ------------------------------------------------------------------ #
    def is_allowed(self, url: str) -> bool:
        """Return ``True`` when ``url`` may be fetched under the policy."""
        if not self.loaded:
            return True
        try:
            return bool(self.parser.can_fetch(self.user_agent, url))
        except Exception:  # pragma: no cover - defensive against weird URLs
            return True

    def filter_urls(self, urls: Any, stats: Any | None = None) -> list[str]:
        """Drop every URL the policy disallows, logging what got dropped.

        Args:
            urls: Iterable of absolute or relative URLs.
            stats: Optional :class:`~itch_scraper.logging_utils.RunStats`.

        Returns:
            The permitted URLs, in input order.
        """
        kept: list[str] = []
        for url in urls:
            absolute = _absolutize(url)
            if self.is_allowed(absolute):
                kept.append(url)
            elif stats is not None:
                stats.info("robots.txt disallows %s - skipping", absolute)
        return kept


def allow_all_policy() -> RobotsPolicy:
    """A permissive policy (used when robots.txt cannot be consulted)."""
    return RobotsPolicy.allow_all("not loaded")


_DISALLOW_RE = re.compile(r"^\s*disallow\s*:\s*(?P<path>\S+)", re.IGNORECASE | re.MULTILINE)


def _extract_disallow(text: str) -> list[str]:
    """Collect every ``Disallow`` path from a robots.txt body."""
    return [m.group("path") for m in _DISALLOW_RE.finditer(text) if m.group("path") != "/"]


def _absolutize(url: str) -> str:
    """Make ``url`` absolute against itch.io so the parser can match paths."""
    if urlparse(url).scheme:
        return url
    return urljoin("https://itch.io/", url)


def fetch_robots(fetcher: Any, base_url: str = "https://itch.io/robots.txt", stats: Any | None = None) -> RobotsPolicy:
    """Download (through the rate-limited fetcher) and build the policy.

    Args:
        fetcher: Object exposing ``get(url) -> str | None`` - typically
            :class:`~itch_scraper.fetch_layer.ItchFetcher`.
        base_url: Location of the robots file.
        stats: Optional run-stats object used for logging/failure records.

    Returns:
        A :class:`RobotsPolicy`; permissive when the file cannot be read.
    """
    text: Optional[str] = None
    try:
        text = fetcher.get(base_url)
    except Exception as exc:  # pragma: no cover - network edge cases
        if stats is not None:
            stats.record_failure(base_url, "robots_lookup_failed", error=exc)
        return RobotsPolicy.allow_all(f"lookup failed: {exc}")

    if not text:
        if stats is not None:
            stats.warning("Could not read %s; proceeding without robots rules", base_url)
        return RobotsPolicy.allow_all("empty response")

    try:
        policy = RobotsPolicy.from_text(text, user_agent=getattr(fetcher, "user_agent", "*"))
    except Exception as exc:  # pragma: no cover - malformed robots file
        if stats is not None:
            stats.record_failure(base_url, "robots_parse_failed", error=exc)
        return RobotsPolicy.allow_all(f"parse failed: {exc}")

    if stats is not None:
        stats.info(
            "robots.txt loaded (%d disallow rule(s)%s)",
            len(policy.disallow),
            ": " + ", ".join(policy.disallow) if policy.disallow else "",
        )
    return policy
