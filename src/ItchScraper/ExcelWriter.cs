// OUTPUT LAYER: write the itch rows into the shared Excel workbook.
//
// README target:
//
//   workbook.xlsx
//     Sheet1: Combined
//     Sheet2: Steam only
//     Sheet3: Itch only
//
// The Steam branch is not implemented yet, so this module writes/updates *only*
// the sheets it owns ("Itch" and "Combined") and leaves a correctly-ordered
// empty "Steam" sheet in place for whoever builds that fetcher next. Existing
// workbooks are merged rather than clobbered: unrelated sheets keep their data.

using System.Globalization;
using ClosedXML.Excel;

namespace ItchScraper;

public static class ExcelWriter
{
    /// <summary>The README "Planned Spreadsheet Schema" - always the first six columns.</summary>
    public static readonly string[] SchemaColumns =
    {
        "title", "platform", "price_usd", "release_date", "developer", "reviews_positive",
    };

    /// <summary>Column order inside the workbook (schema first, itch extras after).</summary>
    public static readonly string[] SheetColumns = SchemaColumns.Concat(new[]
    {
        "url", "game_id", "updated_at", "rating_value", "rating_count", "genre", "tags",
        "status", "platforms", "short_description", "source_url", "page",
    }).ToArray();

    // ------------------------------------------------------------------ //
    // frame helpers                                                      //
    // ------------------------------------------------------------------ //
    /// <summary>Reindex a table to <see cref="SheetColumns"/>, dropping unknown columns.
    /// Missing schema columns are added as empty; rows sort by title then developer
    /// for stable diffs between runs.</summary>
    public static GameTable PrepareSheetTable(GameTable table)
    {
        var columns = new List<string>();
        foreach (var column in SchemaColumns)
        {
            if (!table.Columns.Contains(column))
            {
                foreach (var row in table.Rows) row[column] = null;
                table.Columns.Add(column);
            }
        }
        columns.AddRange(SheetColumns.Where(table.HasColumn));

        var rows = table.Rows
            .OrderBy(r => SortKey(r, "title"), StringComparer.Ordinal)
            .ThenBy(r => SortKey(r, "developer"), StringComparer.Ordinal)
            .ToList();
        return new GameTable(columns, rows);
    }

    private static string SortKey(Record row, string column) => Utils.CleanText(row.Get(column));

    /// <summary>Stack the itch table together with the (future) Steam table.</summary>
    public static GameTable CombinedTable(GameTable? itch, GameTable? steam)
    {
        var tables = new List<GameTable>();
        if (steam is { Count: > 0 }) tables.Add(PrepareSheetTable(steam));
        if (itch is { Count: > 0 }) tables.Add(PrepareSheetTable(itch));
        if (tables.Count == 0) return new GameTable(SheetColumns.ToList(), new List<Record>());

        var columns = tables.SelectMany(t => t.Columns).Distinct().ToList();
        var ordered = SheetColumns.Where(columns.Contains)
            .Concat(columns.Where(c => !SheetColumns.Contains(c))).ToList();
        var rows = tables.SelectMany(t => t.Rows).ToList();
        return new GameTable(ordered, rows);
    }

    // ------------------------------------------------------------------ //
    // reading existing sheets (for merge-preserving writes)              //
    // ------------------------------------------------------------------ //
    /// <summary>Return one sheet of an existing workbook as a table, or null when absent.</summary>
    public static GameTable? ReadExistingSheet(string path, string sheetName)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var book = new XLWorkbook(path);
            var ws = book.Worksheets.FirstOrDefault(
                w => string.Equals(w.Name, sheetName, StringComparison.OrdinalIgnoreCase));
            if (ws is null) return null;
            return SheetToTable(ws);
        }
        catch (Exception)
        {
            return null; // corrupt/unreadable workbook
        }
    }

    private static GameTable SheetToTable(IXLWorksheet ws)
    {
        var used = ws.RangeUsed();
        if (used is null) return new GameTable(new List<string>(), new List<Record>());
        var lastRow = used.LastRow().RowNumber();
        var lastCol = used.LastColumn().ColumnNumber();

        var header = new List<string>();
        for (var c = 1; c <= lastCol; c++)
        {
            header.Add(ws.Cell(1, c).GetString().Trim());
        }

        var rows = new List<Record>();
        for (var r = 2; r <= lastRow; r++)
        {
            var record = new Record();
            for (var c = 1; c <= lastCol; c++)
            {
                var cell = ws.Cell(r, c);
                object? value = cell.IsEmpty() ? null : CellValue(cell);
                record[header[c - 1]] = value;
            }
            rows.Add(record);
        }
        return new GameTable(header, rows);
    }

    private static object? CellValue(IXLCell cell)
    {
        var type = cell.DataType;
        return type switch
        {
            XLDataType.Number => cell.GetDouble(),
            XLDataType.DateTime => cell.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            XLDataType.TimeSpan => cell.GetString(),
            XLDataType.Boolean => cell.GetBoolean(),
            XLDataType.Text => cell.GetString(),
            _ => cell.GetString(),
        };
    }

    // ------------------------------------------------------------------ //
    // writing                                                            //
    // ------------------------------------------------------------------ //
    /// <summary>Write the single three-sheet workbook: Combined / Steam / Itch.</summary>
    /// <param name="table">Cleaned itch records.</param>
    /// <param name="workbookPath">Target .xlsx file (parents are created).</param>
    /// <param name="sheetItch">/</param>
    /// <param name="sheetSteam">/</param>
    /// <param name="sheetCombined">Sheet names.</param>
    /// <param name="steamTable">Optional already-cleaned Steam records; when omitted, any
    /// Steam sheet found in an existing workbook is reused for the combined view.</param>
    /// <param name="stats">Optional run-stats object for logging.</param>
    /// <returns>{sheet_name: row_count} for the sheets this call touched.</returns>
    public static Dictionary<string, int> WriteWorkbook(
        GameTable? itchTable,
        GameTable? steamTable,
        string workbookPath,
        string sheetCombined = "Combined",
        string sheetSteam = "Steam",
        string sheetItch = "Itch",
        RunStats? stats = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(workbookPath)) ?? ".");

        // A source skipped this run keeps whatever an earlier run wrote, so the
        // three sheets always reflect the freshest data from both stores.
        var itchClean = itchTable is { Count: > 0 }
            ? PrepareSheetTable(itchTable)
            : FallbackOrEmpty(workbookPath, sheetItch, ranThisRun: itchTable is not null);
        var steamClean = steamTable is { Count: > 0 }
            ? PrepareSheetTable(steamTable)
            : FallbackOrEmpty(workbookPath, sheetSteam, ranThisRun: steamTable is not null);

        var sheets = new Dictionary<string, GameTable>(StringComparer.Ordinal)
        {
            [sheetCombined] = CombinedTable(itchClean, steamClean),
            [sheetSteam] = steamClean,
            [sheetItch] = itchClean,
        };

        // Preserve unrelated sheets someone may have added by hand.
        var extraSheets = new List<(string Name, GameTable Table)>();
        if (File.Exists(workbookPath))
        {
            try
            {
                using var existingBook = new XLWorkbook(workbookPath);
                foreach (var ws in existingBook.Worksheets)
                {
                    if (sheets.ContainsKey(ws.Name)) continue;
                    extraSheets.Add((ws.Name, SheetToTable(ws)));
                }
            }
            catch (Exception e)
            {
                stats?.Warning($"could not merge existing workbook {workbookPath}: {e.Message}");
            }
        }

        using (var book = new XLWorkbook())
        {
            foreach (var name in new[] { sheetCombined, sheetSteam, sheetItch })
            {
                WriteOne(book, sheets[name], name, stats);
            }
            foreach (var (name, sheetTable) in extraSheets)
            {
                if (!sheets.ContainsKey(name)) WriteOne(book, sheetTable, name, stats);
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(workbookPath))!;
            Directory.CreateDirectory(directory);
            book.SaveAs(workbookPath);
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in new[] { sheetCombined, sheetSteam, sheetItch })
        {
            counts[name] = sheets[name].Count;
        }
        stats?.Info($"wrote {string.Join(", ", counts.Select(kv => $"{kv.Key}={kv.Value} rows"))} -> {workbookPath}");
        return counts;
    }

    private static GameTable EmptySchemaTable() =>
        new GameTable(SchemaColumns.ToList(), new List<Record>());

    /// <summary>Reuse the previous sheet content when a source produced no rows
    /// this run (empty result -> truly empty sheet; skipped source -> keep old).</summary>
    private static GameTable FallbackOrEmpty(string workbookPath, string sheetName, bool ranButEmpty)
    {
        if (ranButEmpty) return EmptySchemaTable();
        var existing = ReadExistingSheet(workbookPath, sheetName);
        return existing is { Count: > 0 } ? PrepareSheetTable(existing) : EmptySchemaTable();
    }

    /// <summary>Write one sheet and apply header formatting / column widths.</summary>
    private static void WriteOne(XLWorkbook book, GameTable table, string sheetName, RunStats? stats)
    {
        var safeSheet = SafeSheetName(sheetName);
        var ws = book.AddWorksheet(safeSheet);
        try
        {
            for (var c = 0; c < table.Columns.Count; c++)
            {
                var cell = ws.Cell(1, c + 1);
                cell.Value = table.Columns[c];
                cell.Style.Font.Bold = true;
            }

            for (var r = 0; r < table.Rows.Count; r++)
            {
                for (var c = 0; c < table.Columns.Count; c++)
                {
                    var value = table.Rows[r].Get(table.Columns[c]);
                    var cell = ws.Cell(r + 2, c + 1);
                    switch (value)
                    {
                        case null: break;
                        case double d when !double.IsNaN(d): cell.Value = d; break;
                        case long l: cell.Value = l; break;
                        case int i: cell.Value = i; break;
                        case bool b: cell.Value = b; break;
                        default: cell.Value = Convert.ToString(value, CultureInfo.InvariantCulture); break;
                    }
                }
            }

            ws.SheetView.FreezeRows(1);
            for (var c = 0; c < table.Columns.Count; c++)
            {
                var longest = table.Columns[c].Length;
                foreach (var row in table.Rows.Take(200))
                {
                    var text = Convert.ToString(row.Get(table.Columns[c]), CultureInfo.InvariantCulture) ?? "";
                    longest = Math.Max(longest, text.Length);
                }
                ws.Column(c + 1).Width = Math.Min(48, Math.Max(10, longest + 2));
            }
        }
        catch (Exception e)
        {
            // cosmetics must never fail a run
            stats?.Warning($"column sizing skipped for {sheetName}: {e.Message}");
        }
    }

    /// <summary>Excel forbids some characters and caps names at 31 chars.</summary>
    internal static string SafeSheetName(string name)
    {
        foreach (var charIn in "[]:*?/\\")
        {
            name = name.Replace(charIn, '-');
        }
        name = name.Length > 31 ? name[..31] : name;
        return name.Length == 0 ? "Sheet" : name;
    }
}
