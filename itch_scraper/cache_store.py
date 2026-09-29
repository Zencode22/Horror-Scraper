"""On-disk response cache for the itch.io fetcher.

README, "Scraping Etiquette": *cache raw responses locally so re-runs don't
re-hit servers*.  This module implements exactly that with a tiny
content-addressed store::

    cache/itch/<sha1(url)>.json      {"url", "saved_at", "status", "text"}
    cache/itch/index.json            {sha1 -> {"url", "hits", "last_seen"}}

The cache is deliberately dumb (no SQLite, no compression): HTML pages are a
few hundred KB and a handful of files is easy to inspect by hand while
debugging the parser.
"""

from __future__ import annotations

import hashlib
import json
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Optional


def cache_key(url: str) -> str:
    """Return the stable content-address used as filename for ``url``."""
    return hashlib.sha1(url.encode("utf-8")).hexdigest()


@dataclass
class CacheEntry:
    """A cached HTTP response.

    Attributes:
        url: The request URL this body came from.
        text: Response body (decoded HTML / robots.txt).
        status: HTTP status code observed when it was fetched.
        saved_at: POSIX timestamp of the cache write.
    """

    url: str
    text: str
    status: int
    saved_at: float

    def age(self, now: Optional[float] = None) -> float:
        """Seconds elapsed since the entry was written."""
        return max(0.0, (now or time.time()) - self.saved_at)

    def is_fresh(self, ttl: float, now: Optional[float] = None) -> bool:
        """``True`` when the entry is younger than ``ttl`` seconds."""
        return self.age(now) <= ttl


class ResponseCache:
    """Filesystem-backed GET cache with TTL support.

    Args:
        cache_dir: Directory holding the entries (created on demand).
        ttl: Entries older than this many seconds are ignored on read but kept
            on disk until :meth:`purge` runs.
        enabled: Set to ``False`` to make every lookup a miss (useful in tests
            or with ``--no-cache``).
    """

    def __init__(self, cache_dir: Path | str, ttl: float = 12 * 3600, enabled: bool = True) -> None:
        self.cache_dir = Path(cache_dir)
        self.ttl = float(ttl)
        self.enabled = enabled
        self.hits = 0
        self.misses = 0
        self._index_path = self.cache_dir / "index.json"

    # ------------------------------------------------------------------ #
    # plumbing                                                           #
    # ------------------------------------------------------------------ #
    def _path_for(self, url: str) -> Path:
        return self.cache_dir / f"{cache_key(url)}.json"

    def _ensure_dir(self) -> None:
        self.cache_dir.mkdir(parents=True, exist_ok=True)

    def _load_index(self) -> dict[str, Any]:
        try:
            return json.loads(self._index_path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return {}

    def _update_index(self, url: str, key: str, status: int) -> None:
        index = self._load_index()
        record = index.get(key, {})
        record.update({"url": url, "status": status, "last_seen": time.time()})
        record["hits"] = int(record.get("hits", 0)) + 1
        index[key] = record
        try:
            self._ensure_dir()
            self._index_path.write_text(
                json.dumps(index, indent=2, sort_keys=True), encoding="utf-8"
            )
        except OSError:  # pragma: no cover - index is best-effort bookkeeping
            pass

    # ------------------------------------------------------------------ #
    # public API                                                         #
    # ------------------------------------------------------------------ #
    def get(self, url: str) -> Optional[CacheEntry]:
        """Return a fresh cached entry for ``url``, or ``None``.

        Corrupt or unreadable files count as a miss rather than raising.
        """
        if not self.enabled:
            self.misses += 1
            return None
        path = self._path_for(url)
        try:
            raw = json.loads(path.read_text(encoding="utf-8"))
            entry = CacheEntry(
                url=raw["url"], text=raw["text"], status=int(raw["status"]),
                saved_at=float(raw["saved_at"]),
            )
        except (OSError, ValueError, KeyError, TypeError):
            self.misses += 1
            return None
        if not entry.is_fresh(self.ttl):
            self.misses += 1
            return None
        self.hits += 1
        self._update_index(url, path.stem, entry.status)
        return entry

    def put(self, url: str, text: str, status: int) -> CacheEntry:
        """Store ``text`` for ``url`` and return the created entry."""
        entry = CacheEntry(url=url, text=text, status=status, saved_at=time.time())
        if not self.enabled:
            return entry
        path = self._path_for(url)
        try:
            self._ensure_dir()
            path.write_text(
                json.dumps(
                    {
                        "url": entry.url,
                        "status": entry.status,
                        "saved_at": entry.saved_at,
                        "text": entry.text,
                    },
                    ensure_ascii=False,
                ),
                encoding="utf-8",
            )
        except OSError as exc:  # pragma: no cover - never fail a crawl over cache
            # Keep going without caching; the caller only cares about the body.
            entry.saved_at -= 0  # no-op, documents intent
            raise CacheWriteError(str(exc)) from exc
        self._update_index(url, path.stem, status)
        return entry

    def purge(self, older_than: Optional[float] = None) -> int:
        """Delete stale entries; returns how many files were removed.

        Args:
            older_than: Age cutoff in seconds.  Defaults to ``self.ttl``.
        """
        cutoff = self.ttl if older_than is None else older_than
        now = time.time()
        removed = 0
        for path in sorted(self.cache_dir.glob("*.json")):
            if path == self._index_path:
                continue
            try:
                raw = json.loads(path.read_text(encoding="utf-8"))
                saved_at = float(raw.get("saved_at", 0))
            except (OSError, ValueError, TypeError):
                saved_at = 0.0
            if now - saved_at > cutoff:
                try:
                    path.unlink()
                    removed += 1
                except OSError:  # pragma: no cover
                    pass
        return removed

    def stats(self) -> dict[str, Any]:
        """Snapshot of cache utilisation for run summaries."""
        total = len(list(self.cache_dir.glob("*.json"))) if self.cache_dir.exists() else 0
        return {
            "dir": str(self.cache_dir),
            "entries": max(0, total - 1),  # minus index.json
            "hits": self.hits,
            "misses": self.misses,
            "enabled": self.enabled,
        }


class CacheWriteError(RuntimeError):
    """Raised when the cache directory cannot be written (crawl continues)."""
