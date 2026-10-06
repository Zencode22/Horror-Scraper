// CLEANING & VALIDATION layer (README: Deduplicator / Normalizer / Validator).
//
// Input:  list of raw records produced by ItchParser
// Output: a GameTable with exactly the README schema columns plus a few
//         itch-specific extras.
//
// Pipeline order matters and is fixed here:
//
//   1. merge   listing rows with their detail-page payload (by URL)
//   2. normalize prices/dates/strings
//   3. deduplicate on normalized title + developer (README's rule)
//   4. validate  drop null/broken rows

using System.Globalization;

namespace ItchScraper;

/// <summary>An ordered table of records - the C# stand-in for the pandas DataFrame.</summary>
public sealed class GameTable
{
    /// <summary>The README "Planned Spreadsheet Schema", in order.</summary>
    public static readonly string[] BaseColumns =
    {
        "title", "platform", "price_usd", "release_date", "developer", "reviews_positive",
    };

    /// <summary>Extra columns that are useful per source (kept after the base
    /// schema; only the ones actually present in a table get materialized).</summary>
    public static readonly string[] ExtraColumns =
    {
        // shared + itch-specific
        "url", "game_id", "updated_at", "rating_count", "rating_value", "genre", "tags",
        "status", "platforms", "short_description", "source_url", "page",
        // steam-specific
        "reviews_total", "review_percent", "review_sentiment", "price_original_raw",
    };

    public List<string> Columns { get; }
    public List<Record> Rows { get; }

    public GameTable(List<string> columns, List<Record> rows)
    {
        Columns = columns;
        Rows = rows;
    }

    public int Count => Rows.Count;

    public object? Value(Record row, string column) => row.Get(column);

    public bool HasColumn(string column) => Columns.Contains(column);
}

public static class Cleaning
{
    private const double Nan = double.NaN;

    // ------------------------------------------------------------------ //
    // step 1 - merge                                                     //
    // ------------------------------------------------------------------ //
    /// <summary>Attach detail-page fields to their listing rows.</summary>
    /// <param name="listings">Raw records from ItchParser.ParseListingPage.</param>
    /// <param name="detailsByUrl">{url: ParseGamePage(...)}; missing entries simply
    /// leave the date fields empty (the validator decides later).</param>
    /// <returns>New list of merged records (input objects are not mutated).</returns>
    public static List<Record> MergeDetails(IEnumerable<Record> listings,
                                             IDictionary<string, Record>? detailsByUrl)
    {
        detailsByUrl ??= new Dictionary<string, Record>();
        var merged = new List<Record>();
        var seenUrls = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in listings)
        {
            var record = new Record(row);
            var url = Utils.CleanText(record.Get("url"));
            // Same game can appear in several browse views; keep the first sighting.
            if (url.Length > 0 && !seenUrls.Add(url)) continue;

            var detail = detailsByUrl.TryGetValue(url, out var d) ? d : null;

            foreach (var key in new[]
                     {
                         "release_date", "release_date_raw", "updated_at", "updated_at_raw",
                         "rating_count", "rating_value", "status", "tags", "genres",
                     })
            {
                if (detail is not null && detail.Has(key) && !record.Has(key))
                {
                    record[key] = detail[key];
                }
            }

            // Prefer the developer shown on the game page when the listing was blank.
            if (!record.Has("developer") && detail?.Has("developer") == true)
            {
                record["developer"] = detail["developer"];
            }

            // Price: listing tag wins (it reflects the sale actually running); fall
            // back to the detail page only when the listing had no price at all.
            if (record.Get("price_usd") is null && detail?.Get("price_usd") is not null)
            {
                record["price_usd"] = detail["price_usd"];
                record["price_raw"] = detail.Get("price_raw");
            }

            if (detail?.Has("title") == true)
            {
                record.TryAdd("detail_title", detail["title"]);
            }

            merged.Add(record);
        }

        return merged;
    }

    // ------------------------------------------------------------------ //
    // step 2 - normalize                                                 //
    // ------------------------------------------------------------------ //
    /// <summary>Normalize every record to the output schema (price -> USD, date -> ISO).</summary>
    /// <remarks>
    /// * title/developer are whitespace-cleaned strings.
    /// * price_usd becomes a double (NaN when unparseable).
    /// * release_date becomes YYYY-MM-DD (null otherwise).
    /// * reviews_positive stays null for itch rows - itch.io publishes star
    ///   ratings, not positive/negative review counts (that column is Steam-only).
    /// </remarks>
    public static List<Record> NormalizeRows(IEnumerable<Record> records)
    {
        var normalized = new List<Record>();
        foreach (var raw in records)
        {
            var row = new Record(raw);
            var release = row.Get("release_date") ?? row.Get("release_date_raw");
            var updated = row.Get("updated_at") ?? row.Get("updated_at_raw");

            var platform = Utils.CleanText(row.Get("platform")).ToLowerInvariant();
            if (platform.Length == 0) platform = "itch";

            row["title"] = Utils.CleanText(row.Get("title") ?? row.Get("detail_title"));
            row["developer"] = Utils.CleanText(row.Get("developer"));
            row["platform"] = platform;
            row["price_usd"] = PriceOrNan(row.Get("price_usd") ?? row.Get("price_raw"), platform);
            row["release_date"] = Utils.ToIsoDate(release);
            row["updated_at"] = Utils.ToIsoDate(updated);
            row["reviews_positive"] = ReviewsPositive(row);
            row["genre"] = Utils.CleanText(row.Get("genre") ?? FirstOfList(row.Get("genres")));
            if (platform == "steam")
            {
                // Keep the full tag list too - Steam app pages expose several genres.
                row["tags"] = JoinList(row.Get("genres"));
            }
            row["tags"] = JoinList(row.Get("tags"));
            row["platforms"] = JoinList(row.Get("platforms"));
            row["status"] = Utils.CleanText(row.Get("status"));
            row["game_id"] = Utils.CleanText(row.Get("game_id")) is { Length: > 0 } gid ? gid : null;
            normalized.Add(row);
        }
        return normalized;
    }

    /// <summary>Coerce the review field: itch rows have none, Steam rows carry a
    /// computed positive-review count from the app page.</summary>
    private static long? ReviewsPositive(Record row) => row.Get("reviews_positive") switch
    {
        long l => l,
        int i => i,
        double d when !double.IsNaN(d) => (long)Math.Round(d),
        string text when long.TryParse(new string(text.Where(char.IsDigit).ToArray()),
                                       System.Globalization.NumberStyles.Integer,
                                       CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    private static string? FirstOfList(object? value) =>
        value is IReadOnlyList<string> list && list.Count > 0 ? list[0] : null;

    private static string JoinList(object? value) => value switch
    {
        IReadOnlyList<string> list => string.Join(", ", list),
        _ => Utils.CleanText(value),
    };

    /// <summary>Return a double price, or NaN when nothing usable is available.
    /// A *missing* price means "Free" on itch.io but "unknown" on Steam (where
    /// the validator decides whether to keep the row).</summary>
    private static double PriceOrNan(object? value, string platform = "itch")
    {
        double? parsed = value switch
        {
            null => platform == "itch" ? 0.0 : null,
            string s when s.Length == 0 => platform == "itch" ? 0.0 : null,
            double d => double.IsNaN(d) ? null : d,
            long l => (double)l,
            int i => i,
            _ => Utils.NormalizePrice(value),
        };
        return parsed is null || double.IsNaN(parsed.Value)
            ? Nan
            : Math.Round(parsed.Value, 2, MidpointRounding.AwayFromZero);
    }

    // ------------------------------------------------------------------ //
    // step 3 - deduplicate                                               //
    // ------------------------------------------------------------------ //
    /// <summary>Drop duplicates matched on *normalized title + developer* (README rule).</summary>
    /// <remarks>When two rows collide we keep the richer one (more non-empty
    /// fields), which makes re-running over overlapping browse views idempotent.</remarks>
    public static List<Record> Deduplicate(IEnumerable<Record> records)
    {
        var best = new Dictionary<(string, string), Record>();
        var order = new List<(string, string)>();
        foreach (var row in records)
        {
            var key = (Utils.NormalizeTitle(row.Get("title")), Utils.NormalizeTitle(row.Get("developer")));
            if (!best.ContainsKey(key)) order.Add(key);
            if (!best.TryGetValue(key, out var previous) || Richness(row) > Richness(previous))
            {
                best[key] = row;
            }
        }
        return order.Select(k => best[k]).ToList();
    }

    /// <summary>Count populated, meaningful fields - used to pick the keeper duplicate.</summary>
    private static int Richness(Record row)
    {
        var score = 0;
        foreach (var field in new[]
                 {
                     "title", "developer", "url", "release_date", "updated_at",
                     "genre", "tags", "status", "short_description",
                 })
        {
            if (row.Has(field)) score++;
        }
        if (row.Get("price_usd") is double price && !double.IsNaN(price)) score++;
        return score;
    }

    // ------------------------------------------------------------------ //
    // step 4 - validate                                                  //
    // ------------------------------------------------------------------ //
    /// <summary>Drop broken rows: no title, no URL, or an unusable price.</summary>
    /// <remarks>A missing release_date is *not* fatal (plenty of itch games never
    /// show one) but it is reported so the operator can judge coverage.</remarks>
    public static List<Record> ValidateRows(IEnumerable<Record> records)
    {
        var kept = new List<Record>();
        foreach (var row in records)
        {
            var title = Utils.CleanText(row.Get("title"));
            var url = Utils.CleanText(row.Get("url"));
            var priceObj = row.Get("price_usd");
            if (title.Length == 0 || url.Length == 0) continue;
            if (priceObj is not double price || double.IsNaN(price)) continue;
            if (price < 0 || price > 10_000) continue; // obvious scraping artifacts
            kept.Add(row);
        }
        return kept;
    }

    // ------------------------------------------------------------------ //
    // table helpers                                                      //
    // ------------------------------------------------------------------ //
    /// <summary>Build the final table: schema columns first, extras after.</summary>
    public static GameTable RecordsToTable(IEnumerable<Record> records)
    {
        var rows = records.ToList();
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            foreach (var key in row.Keys) present.Add(key);
        }

        var ordered = GameTable.BaseColumns.Concat(GameTable.ExtraColumns)
            .Where(present.Contains).ToList();
        var rest = present.Where(c => !ordered.Contains(c)).OrderBy(c => c, StringComparer.Ordinal).ToList();
        ordered.AddRange(rest);
        return new GameTable(ordered, rows);
    }

    /// <summary>Run normalize -> dedupe -> validate on already-parsed records.</summary>
    /// <remarks>Provided so the Steam branch (and tests) can reuse the same
    /// cleaning rules on any source's raw records.</remarks>
    /// <param name="records">Raw records.</param>
    /// <param name="stats">Optional run-stats object for coverage logging.</param>
    /// <returns>Cleaned table with the schema columns guaranteed to exist.</returns>
    public static GameTable CleanTable(IEnumerable<Record> records, RunStats? stats = null)
    {
        var raw = records.ToList();
        var cleaned = CrossPlatformDedupe(ValidateRows(Deduplicate(NormalizeRows(raw))));
        var result = RecordsToTable(cleaned);

        foreach (var column in GameTable.BaseColumns)
        {
            if (!result.Columns.Contains(column)) result.Columns.Add(column);
        }

        if (stats is not null)
        {
            var total = result.Count;
            var dated = result.Rows.Count(r => r.Get("release_date") is string s && s.Length > 0);
            stats.Info($"cleaning: {raw.Count} raw -> {total} rows " +
                       $"({total} unique after dedupe pass), {dated}/{Math.Max(total, 1)} carry a release date");
        }
        return result;
    }

    /// <summary>Drop cross-source duplicates: the same title+developer scraped
    /// from both stores. The Steam row wins because it carries richer review and
    /// release-date data; ties keep whichever was seen first.</summary>
    public static List<Record> CrossPlatformDedupe(IEnumerable<Record> records)
    {
        var byKey = new Dictionary<(string, string), Record>();
        var order = new List<(string, string)>();
        foreach (var row in records)
        {
            var key = (Utils.NormalizeTitle(row.Get("title")), Utils.NormalizeTitle(row.Get("developer")));
            if (!byKey.TryGetValue(key, out var existing))
            {
                order.Add(key);
                byKey[key] = row;
                continue;
            }
            var existingPlatform = Utils.CleanText(existing.Get("platform")).ToLowerInvariant();
            var candidatePlatform = Utils.CleanText(row.Get("platform")).ToLowerInvariant();
            if (existingPlatform != candidatePlatform && candidatePlatform == "steam")
            {
                byKey[key] = row; // steam beats itch for the combined view
            }
        }
        return order.Select(k => byKey[k]).ToList();
    }

    internal static string FormatDouble(double value) =>
        value.ToString("0.0################", CultureInfo.InvariantCulture);
}
