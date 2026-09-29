"""OUTPUT LAYER: write the itch rows into the shared Excel workbook.

README target::

    workbook.xlsx
      Sheet1: Combined
      Sheet2: Steam only
      Sheet3: Itch only

The Steam branch is not implemented yet, so this module writes/updates *only*
the sheets it owns (``Itch`` and ``Combined``) and leaves a correctly-ordered
empty ``Steam`` sheet in place for whoever builds that fetcher next.  Existing
workbooks are merged rather than clobbered: unrelated sheets keep their data.
"""

from __future__ import annotations

from pathlib import Path
from typing import Any, Iterable, Optional

import pandas as pd

#: The README "Planned Spreadsheet Schema" - always the first six columns.
SCHEMA_COLUMNS: tuple[str, ...] = (
    "title",
    "platform",
    "price_usd",
    "release_date",
    "developer",
    "reviews_positive",
)

#: Column order inside the workbook (schema first, itch extras after).
SHEET_COLUMNS: tuple[str, ...] = SCHEMA_COLUMNS + (
    "url",
    "game_id",
    "updated_at",
    "rating_value",
    "rating_count",
    "genre",
    "tags",
    "status",
    "platforms",
    "short_description",
    "source_url",
    "page",
)


def prepare_sheet_frame(frame: pd.DataFrame) -> pd.DataFrame:
    """Reindex ``frame`` to :data:`SHEET_COLUMNS`, dropping unknown columns.

    Args:
        frame: Cleaned scraper output.

    Returns:
        A copy containing every schema column (missing ones filled with ``None``)
        sorted by title then developer for stable diffs between runs.
    """
    out = frame.copy()
    for column in SCHEMA_COLUMNS:
        if column not in out.columns:
            out[column] = None
    keep = [c for c in SHEET_COLUMNS if c in out.columns]
    out = out.reindex(columns=keep)
    sort_keys = [c for c in ("title", "developer") if c in out.columns]
    if sort_keys and len(out):
        out = out.sort_values(sort_keys, kind="stable", na_position="last")
    return out.reset_index(drop=True)


def combined_frame(itch: pd.DataFrame, steam: Optional[pd.DataFrame] = None) -> pd.DataFrame:
    """Stack the itch frame together with the (future) Steam frame."""
    frames: list[pd.DataFrame] = []
    if steam is not None and len(steam):
        frames.append(prepare_sheet_frame(steam))
    if len(itch):
        frames.append(prepare_sheet_frame(itch))
    if not frames:
        return pd.DataFrame(columns=list(SHEET_COLUMNS))
    return pd.concat(frames, ignore_index=True)


def _read_existing_sheet(path: Path, sheet: str) -> Optional[pd.DataFrame]:
    """Return one sheet of an existing workbook, or ``None`` when absent."""
    if not path.exists():
        return None
    try:
        book = pd.read_excel(path, sheet_name=None, engine="openpyxl")
    except Exception:  # pragma: no cover - corrupt/unreadable workbook
        return None
    return book.get(sheet)


def write_itch_sheet(
    frame: pd.DataFrame,
    workbook_path: Path | str,
    *,
    sheet_itch: str = "Itch",
    sheet_steam: str = "Steam",
    sheet_combined: str = "Combined",
    steam_frame: Optional[pd.DataFrame] = None,
    stats: Any | None = None,
) -> dict[str, int]:
    """Write the itch-only sheet plus the combined sheet into the workbook.

    Args:
        frame: Cleaned itch records.
        workbook_path: Target ``.xlsx`` file (parents are created).
        sheet_itch / sheet_steam / sheet_combined: Sheet names.
        steam_frame: Optional already-cleaned Steam records; when omitted, any
            Steam sheet found in an existing workbook is reused for the
            combined view.
        stats: Optional run-stats object for logging.

    Returns:
        ``{sheet_name: row_count}`` for the sheets this call touched.
    """
    path = Path(workbook_path)
    path.parent.mkdir(parents=True, exist_ok=True)

    itch_clean = prepare_sheet_frame(frame)
    steam_existing = None if steam_frame is not None else _read_existing_sheet(path, sheet_steam)
    steam_clean = (
        prepare_sheet_frame(steam_frame)
        if steam_frame is not None and len(steam_frame)
        else (steam_existing if steam_existing is not None else pd.DataFrame(columns=list(SCHEMA_COLUMNS)))
    )

    sheets: dict[str, pd.DataFrame] = {
        sheet_combined: combined_frame(itch_clean, steam_clean),
        sheet_steam: steam_clean if len(steam_clean) else pd.DataFrame(columns=list(SCHEMA_COLUMNS)),
        sheet_itch: itch_clean,
    }

    # Preserve unrelated sheets someone may have added by hand.
    if path.exists():
        try:
            existing = pd.read_excel(path, sheet_name=None, engine="openpyxl")
            for name, df in existing.items():
                sheets.setdefault(name, df)
        except Exception as exc:  # pragma: no cover
            if stats is not None:
                stats.warning("could not merge existing workbook %s: %s", path, exc)

    with pd.ExcelWriter(path, engine="openpyxl") as writer:
        for name in (sheet_combined, sheet_steam, sheet_itch):
            _write_one(writer, sheets[name], name, stats)
        for name, df in sheets.items():
            if name not in (sheet_combined, sheet_steam, sheet_itch):
                _write_one(writer, df, name, stats)

    counts = {name: int(len(sheets[name])) for name in (sheet_combined, sheet_steam, sheet_itch)}
    if stats is not None:
        stats.info("wrote %s -> %s", ", ".join(f"{k}={v} rows" for k, v in counts.items()), path)
    return counts


def _write_one(writer: "pd.ExcelWriter", df: pd.DataFrame, sheet: str, stats: Any | None) -> None:
    """Write ``df`` and apply header formatting / column widths."""
    safe_sheet = _safe_sheet_name(sheet)
    df.to_excel(writer, sheet_name=safe_sheet, index=False)
    try:
        worksheet = writer.sheets[safe_sheet]
        worksheet.freeze_panes = "A2"
        for idx, column in enumerate(df.columns, start=1):
            longest = max(
                [len(str(column))] + [len(str(v)) for v in df[column].head(200).tolist()]
            )
            worksheet.column_dimensions[worksheet.cell(row=1, column=idx).column_letter].width = min(
                48, max(10, longest + 2)
            )
    except Exception as exc:  # pragma: no cover - cosmetics must never fail a run
        if stats is not None:
            stats.warning("column sizing skipped for %s: %s", sheet, exc)


def _safe_sheet_name(name: str) -> str:
    """Excel forbids some characters and caps names at 31 chars."""
    for char in "[]:*?/\\":
        name = name.replace(char, "-")
    return name[:31] or "Sheet"


def frame_from_records(records: Iterable[dict[str, Any]]) -> pd.DataFrame:
    """Convenience wrapper used by scripts that skip the cleaning stage."""
    return pd.DataFrame(list(records))
