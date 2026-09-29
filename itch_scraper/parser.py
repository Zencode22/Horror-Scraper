"""PARSE & EXTRACT layer for itch.io pages.

Two entry points, matching the README box ``Itch Parser (title, price, dev,
upload date)``:

``parse_listing_page``  browse/tag/horror grid -> one raw record per game card
``parse_game_page``     a game page -> upload date, author, rating, tags ...

Both return plain ``dict`` objects with *raw* (un-normalised) values plus the
intermediate fields the cleaning stage needs (``price_raw``,
``release_date_raw``, ``updated_at_raw``...).  Keeping parsing dumb makes it
trivially unit-testable against saved HTML fixtures.
"""

from __future__ import annotations

import json
import re
from typing import Any, Optional

from bs4 import BeautifulSoup, Tag

from itch_scraper.utils import _clean_text, normalize_price, to_iso_date

#: itch.io's "X" / relative dates are not real release dates - never trust them.
_RELATIVE_DATE_RE = re.compile(
    r"^\s*(?:just now|now|\d+\s*(?:second|minute|hour|day|week|month|year)s?"
    r"\s*ago|yesterday|today)\s*$",
    re.IGNORECASE,
)


# ---------------------------------------------------------------------- #
# listing pages                                                          #
# ---------------------------------------------------------------------- #
def parse_listing_page(html: str, source_url: str, page: int = 1) -> list[dict[str, Any]]:
    """Extract every game card from an itch.io browse listing.

    Args:
        html: Body of ``https://itch.io/games/...tag-horror?page=N``.
        source_url: URL the body came from (stored for provenance/debugging).
        page: Listing page number (stored on each record).

    Returns:
        A list of raw records; cards without a title/link are skipped.
    """
    soup = BeautifulSoup(html or "", "lxml")
    records: list[dict[str, Any]] = []

    for cell in soup.select(".game_cell"):
        record = _parse_game_cell(cell, source_url, page)
        if record is not None:
            records.append(record)

    # Fallback: some layouts render a table instead of the card grid.
    if not records:
        for anchor in soup.select('a.game_link[href*=".itch.io"]'):
            record = _record_from_anchor(anchor, source_url, page)
            if record is not None and record["url"] not in {r["url"] for r in records}:
                records.append(record)

    return records


def _parse_game_cell(cell: Tag, source_url: str, page: int) -> Optional[dict[str, Any]]:
    """Turn a single ``div.game_cell`` into a raw record."""
    title_anchor = cell.select_one("a.title") or cell.select_one("a.game_link")
    if title_anchor is None:
        return None

    url = title_anchor.get("href", "")
    title = _clean_text(title_anchor.get_text(" ", strip=True))
    if not title or not url:
        return None

    price_tag = cell.select_one(".price_tag")
    price_raw = _price_text(price_tag)

    author_anchor = cell.select_one(".game_author a")
    developer = _clean_text(author_anchor.get_text(" ", strip=True)) if author_anchor else ""

    genre_el = cell.select_one(".game_genre")
    description_el = cell.select_one(".game_text")

    record: dict[str, Any] = {
        "title": title,
        "platform": "itch",
        "url": url,
        "game_id": cell.get("data-game_id"),
        "developer": developer,
        "price_raw": price_raw,
        "price_usd": normalize_price(price_raw),
        "genre": _clean_text(genre_el.get_text(" ", strip=True)) if genre_el else "",
        "short_description": (
            _clean_text(description_el.get("title") or description_el.get_text(" ", strip=True))
            if description_el
            else ""
        ),
        "platforms": _platform_labels(cell),
        "release_date": None,          # listings never carry a release date
        "release_date_raw": None,
        "source": "listing",
        "source_url": source_url,
        "page": page,
    }
    return record


def _record_from_anchor(anchor: Tag, source_url: str, page: int) -> Optional[dict[str, Any]]:
    """Build a minimal record when only a bare game link is available."""
    title = _clean_text(anchor.get_text(" ", strip=True))
    url = anchor.get("href", "")
    if not title or not url:
        return None
    return {
        "title": title,
        "platform": "itch",
        "url": url,
        "game_id": None,
        "developer": "",
        "price_raw": None,
        "price_usd": None,
        "genre": "",
        "short_description": "",
        "platforms": [],
        "release_date": None,
        "release_date_raw": None,
        "source": "listing-fallback",
        "source_url": source_url,
        "page": page,
    }


def _price_text(price_tag: Optional[Tag]) -> Optional[str]:
    """Pull the price string out of a listing ``.price_tag`` element.

    The visible text is usually ``"$7.99"`` or ``"$0 -100%"``; name-your-price
    games additionally expose ``title="Pay $1 or more for this game"``, which we
    prefer because it survives sale badges.
    """
    if price_tag is None:
        return None
    title_attr = price_tag.get("title")
    value_el = price_tag.select_one(".price_value")
    candidates = [title_attr, value_el.get_text(" ", strip=True) if value_el else None,
                  price_tag.get_text(" ", strip=True)]
    for candidate in candidates:
        text = _clean_text(candidate)
        if text:
            return text
    return None


def _platform_labels(cell: Tag) -> list[str]:
    """Windows/Linux/macOS icons on a game card, as human labels."""
    labels: list[str] = []
    for span in cell.select(".game_platform span[title]"):
        label = _clean_text(span.get("title")).lower().replace("download for ", "")
        if label and label not in labels:
            labels.append(label)
    return labels


# ---------------------------------------------------------------------- #
# detail (game) pages                                                    #
# ---------------------------------------------------------------------- #
def parse_game_page(html: str, url: str) -> dict[str, Any]:
    """Extract the fields a browse listing cannot provide.

    Args:
        html: Body of a game page (``<user>.itch.io/<slug>``).
        url: That page's URL (used for provenance and developer fallback).

    Returns:
        Raw detail fields: ``release_date``/``release_date_raw``,
        ``updated_at``, ``developer``, ``rating_count``, ``tags``, ``status``,
        ``classification``, ``price_raw`` and ``price_usd``.
    """
    soup = BeautifulSoup(html or "", "lxml")
    details: dict[str, Any] = {
        "url": url,
        "title": _detail_title(soup),
        "developer": _detail_developer(soup, url),
        "release_date": None,
        "release_date_raw": None,
        "updated_at": None,
        "updated_at_raw": None,
        "status": None,
        "classification": None,
        "rating_count": None,
        "rating_value": None,
        "tags": [],
        "genres": [],
        "price_raw": None,
        "price_usd": None,
        "source": "detail",
    }

    info_rows = _info_panel_rows(soup)

    # --- upload / release date ---------------------------------------- #
    released_raw = _row_value(info_rows, ("released", "release date", "published"))
    if released_raw:
        iso = to_iso_date(released_raw)
        if iso:
            details["release_date"], details["release_date_raw"] = iso, released_raw
    updated_raw = _row_value(info_rows, ("updated", "last updated"))
    if updated_raw:
        iso = to_iso_date(updated_raw)
        if iso:
            details["updated_at"], details["updated_at_raw"] = iso, updated_raw

    # --- status / classification -------------------------------------- #
    status = _row_value(info_rows, ("status",))
    if status:
        details["status"] = status
    classification = _row_value(info_rows, ("authors", "author"))
    if classification:
        details["developer"] = details["developer"] or classification

    # --- ratings ------------------------------------------------------- #
    ld = _ld_json_product(soup)
    if isinstance(ld, dict):
        rating = ld.get("aggregateRating") or {}
        details["rating_count"] = _as_int(rating.get("ratingCount"))
        details["rating_value"] = _as_float(rating.get("ratingValue"))
        offers = ld.get("offers") or {}
        if offers.get("price") is not None:
            details["price_raw"] = f"${offers['price']} {offers.get('priceCurrency', 'USD')}".strip()
            details["price_usd"] = normalize_price(details["price_raw"])

    # tooltip text such as "4.51 average rating from 49 total ratings".
    if details["rating_count"] is None:
        tooltip_el = soup.select_one("[data-tooltip*='total ratings']")
        if tooltip_el is not None:
            match = re.search(
                r"([\d.,]+)\s+average rating from\s+([\d,]+)\s+total ratings",
                _clean_text(tooltip_el.get("data-tooltip")),
            )
            if match:
                details["rating_value"] = details["rating_value"] or _as_float(match.group(1))
                details["rating_count"] = _as_int(match.group(2))

    # --- tags / genres ------------------------------------------------- #
    tag_row = _row_value(info_rows, ("tags",))
    if tag_row:
        details["tags"] = [t.strip() for t in re.split(r"[,]", tag_row) if t.strip()]
    genre_row = _row_value(info_rows, ("genre",))
    if genre_row:
        details["genres"] = [g.strip() for g in re.split(r"[,]", genre_row) if g.strip()]

    # --- purchase widget price (name-your-price detection) ------------ #
    if details["price_raw"] is None:
        details["price_raw"] = _purchase_price_text(soup)
        if details["price_raw"]:
            details["price_usd"] = normalize_price(details["price_raw"])

    return details


def _detail_title(soup: BeautifulSoup) -> Optional[str]:
    """Game title from the page heading (falls back to ``<title>``)."""
    for selector in ("h1.game_title", "h1", "title"):
        el = soup.select_one(selector)
        if el is not None:
            text = _clean_text(el.get_text(" ", strip=True))
            if text:
                # "<title>" looks like "Name by Author".
                if selector == "title" and " by " in text:
                    text = text.rsplit(" by ", 1)[0].strip()
                return text
    return None


def _detail_developer(soup: BeautifulSoup, url: str) -> Optional[str]:
    """Developer/user name, taken from the subdomain or the page header."""
    match = re.match(r"https?://([^/.]+)\.itch\.io", url or "")
    author_row = None
    el = soup.select_one(".game_author_name a, .creator_link, .game_header_info a[href*='itch.io']")
    if el is not None:
        author_row = _clean_text(el.get_text(" ", strip=True))
    if author_row:
        return author_row
    return match.group(1) if match else None


def _info_panel_rows(soup: BeautifulSoup) -> dict[str, tuple[str, Optional[str]]]:
    """Map the game info panel table to ``{label: (plain_text, abbr_title)}``.

    itch.io renders relative dates ("21 hours ago") as visible text while the
    absolute timestamp hides in ``<abbr title="28 September 2026 @ 20:08 UTC">``
    - so we always capture both and let the caller prefer the precise one.
    """
    rows: dict[str, tuple[str, Optional[str]]] = {}
    for table in soup.select(".game_info_panel_widget table, table.info_panel"):
        for tr in table.find_all("tr"):
            cells = tr.find_all("td")
            if len(cells) < 2:
                continue
            label = _clean_text(cells[0].get_text(" ", strip=True)).lower()
            if not label:
                continue
            value_cell = cells[1]
            abbr = value_cell.find("abbr")
            absolute = _clean_text(abbr.get("title")) if abbr else None
            text = _clean_text(value_cell.get_text(" ", strip=True))
            # Relative strings are useless as dates; keep only absolute ones.
            if absolute is None and _RELATIVE_DATE_RE.match(text):
                absolute = None
            rows[label] = (absolute or text, absolute)
    return rows


def _row_value(rows: dict[str, tuple[str, Optional[str]]], keys: tuple[str, ...]) -> Optional[str]:
    """First non-empty value whose label starts with any of ``keys``."""
    for label, (text, _absolute) in rows.items():
        if any(label.startswith(key) for key in keys):
            return text or None
    return None


def _ld_json_product(soup: BeautifulSoup) -> Optional[dict[str, Any]]:
    """The schema.org ``Product`` JSON-LD block, if present and valid."""
    for script in soup.find_all("script", type="application/ld+json"):
        raw = script.string or script.get_text()
        if not raw:
            continue
        try:
            data = json.loads(raw)
        except ValueError:
            continue
        blocks = data if isinstance(data, list) else [data]
        for block in blocks:
            if isinstance(block, dict) and block.get("@type") == "Product":
                return block
    return None


def _purchase_price_text(soup: BeautifulSoup) -> Optional[str]:
    """Price wording from the buy/download widget ("$5 or more", "Free")."""
    buy_row = soup.select_one(".buy_row, .game_purchase_form, .purchases_table")
    if buy_row is None:
        return None
    text = _clean_text(buy_row.get_text(" ", strip=True))
    if not text:
        return None
    if re.search(r"name your own price|free", text, re.IGNORECASE):
        return "$0"
    match = re.search(r"(?:US)?\$\s?\d+(?:\.\d{1,2})?", text)
    return match.group(0) if match else None


def _as_int(value: Any) -> Optional[int]:
    """Best-effort int conversion ('11,074' -> 11074)."""
    if value is None:
        return None
    digits = re.sub(r"[^\d]", "", str(value))
    return int(digits) if digits else None


def _as_float(value: Any) -> Optional[float]:
    """Best-effort float conversion."""
    if value is None:
        return None
    try:
        return float(str(value).replace(",", "."))
    except ValueError:
        return None
