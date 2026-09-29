"""Itch.io horror-game scraper.

This package implements the **Itch.io branch** of the pipeline described in the
project README::

    DATA SOURCE (itch.io browse/tag/horror)
      -> FETCH LAYER (requests + rate limiter + retries + local cache)
      -> PARSE & EXTRACT (title, price, developer, upload date)
      -> CLEANING & VALIDATION (dedupe, normalize, drop broken rows)
      -> OUTPUT LAYER (Excel workbook via pandas + openpyxl)

Public helpers
--------------
``scrape_itch``        Run the whole itch branch and return a tidy DataFrame.
``write_itch_sheet``   Write the ``itch`` sheet of the output workbook.
``clean_dataframe``    Dedupe + normalize + validate a raw record DataFrame.
``normalize_price``    "Pay $7.99 or more" / "$0 -100%" -> float USD.
``to_iso_date``        "28 September 2026 @ 20:08 UTC" -> "2026-09-28".
"""

from itch_scraper.cleaning import (
    clean_dataframe,
    deduplicate,
    normalize_rows,
    validate_rows,
)
from itch_scraper.config import CONFIG, ItchScraperConfig
from itch_scraper.excel_writer import SCHEMA_COLUMNS, write_itch_sheet
from itch_scraper.pipeline import scrape_itch
from itch_scraper.utils import normalize_price, to_iso_date

__all__ = [
    "CONFIG",
    "SCHEMA_COLUMNS",
    "ItchScraperConfig",
    "clean_dataframe",
    "deduplicate",
    "normalize_price",
    "normalize_rows",
    "scrape_itch",
    "to_iso_date",
    "validate_rows",
    "write_itch_sheet",
]

__version__ = "0.1.0"
