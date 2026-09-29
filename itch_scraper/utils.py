"""Small, dependency-free helpers shared by the itch.io scraper.

The two functions that matter for the README's *Normalizer* box live here:

``normalize_price``  -> float USD (``None`` when no numeric price is present)
``to_iso_date``      -> ``YYYY-MM-DD`` string (``None`` when unparseable)
"""

from __future__ import annotations

import re
import unicodedata
from datetime import datetime
from typing import Iterable, Optional

#: ``$0``, ``US$7.99``, ``€ 4,50`` ... - we only ever keep the numeric part and
#: treat it as USD (itch.io browse listings are shown in USD).
_PRICE_RE = re.compile(r"(?P<value>\d+(?:[.,]\d{1,2})?)")

#: "28 September 2026 @ 20:08 UTC", "2026-09-28", "Sep 28, 2026", ...
_DATE_PATTERNS: tuple[str, ...] = (
    "%d %B %Y @ %H:%M %Z",
    "%d %b %Y @ %H:%M %Z",
    "%d %B %Y",
    "%d %b %Y",
    "%B %d, %Y",
    "%b %d, %Y",
    "%Y-%m-%dT%H:%M:%S%z",
    "%Y-%m-%dT%H:%M:%SZ",
    "%Y-%m-%d %H:%M:%S",
    "%Y-%m-%d",
    "%d/%m/%Y",
    "%m/%d/%Y",
)


def _clean_text(value: object) -> str:
    """Collapse whitespace and strip invisible/curly characters."""
    if value is None:
        return ""
    text = unicodedata.normalize("NFKC", str(value))
    return re.sub(r"\s+", " ", text).strip()


def normalize_price(value: object) -> Optional[float]:
    """Turn a raw itch.io price label into a float number of US dollars.

    Handled shapes (all observed on ``itch.io/games/tag-horror``):

    ================================ ==========
    listing text                     result
    ================================ ==========
    ``"$7.99"``                      ``7.99``
    ``"$0 -100%"`` (on sale)          ``0.0``
    ``"Pay $1 or more for this game"````1.0``   (name-your-price minimum)
    ``"$3.99  -50%"``                 ``3.99``
    ``""`` / ``None`` / ``"Free"``    ``0.0``   (no tag == free on itch.io)
    ``"$--"`` / garbage               ``None``  (dropped by the validator)
    ================================ ==========

    Args:
        value: The price text scraped from the listing (or detail page).

    Returns:
        The price as a ``float`` (two-decimal rounded), or ``None`` when the
        text carries no usable amount.
    """
    text = _clean_text(value)
    if not text:
        # itch.io omits the price tag entirely for free games.
        return 0.0

    lowered = text.lower()
    if "--" in lowered:
        return None

    match = _PRICE_RE.search(lowered.replace(",", "."))
    if not match:
        return None

    try:
        amount = float(match.group("value"))
    except ValueError:  # pragma: no cover - defensive
        return None

    # A "-100%" sale badge means the game is currently free; the leading
    # "$0"/"$x" already reflects that, so nothing else to do but round.
    return round(amount, 2)


def to_iso_date(value: object) -> Optional[str]:
    """Normalize a date-ish string to ISO ``YYYY-MM-DD``.

    Accepts the formats itch.io emits (``"28 September 2026 @ 20:08 UTC"`` from
    the info-panel ``<abbr title=...>``, ISO timestamps from JSON-LD, plain
    ``"2026-09-28"``) plus a couple of common variants.

    Args:
        value: Raw date string.

    Returns:
        ``"YYYY-MM-DD"`` or ``None`` when the input cannot be parsed.
    """
    text = _clean_text(value)
    if not text:
        return None

    # Drop a trailing timezone word so "%Z" is not required to match.
    candidates = [text, re.sub(r"\s+(UTC|GMT)$", "", text)]
    # Also allow "2026-09-28T20:08:00+00:00" style values.
    candidates.append(text.replace("Z", "+0000"))

    for candidate in candidates:
        for pattern in _DATE_PATTERNS:
            try:
                return datetime.strptime(candidate, pattern).date().isoformat()
            except ValueError:
                continue
    return None


def normalize_title(title: object) -> str:
    """Lower-case, punctuation-free key used for deduplication.

    ``'"Voices Of The Void" Alpha'`` and ``'“Voices of the Void” alpha'`` both
    collapse to ``voices_of_the_void_alpha``.
    """
    text = _clean_text(title).lower()
    text = text.replace("&", " and ")
    text = re.sub(r"[^a-z0-9]+", "_", text)
    return text.strip("_")


def first_non_empty(values: Iterable[object]) -> Optional[str]:
    """Return the first value that is not empty after cleaning, else ``None``."""
    for value in values:
        text = _clean_text(value)
        if text:
            return text
    return None


def write_json_safe(obj: object) -> str:  # pragma: no cover - tiny convenience
    """Render ``obj`` for logs without blowing up on odd types."""
    return repr(obj)
