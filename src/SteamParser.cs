// PARSE & EXTRACT layer for Steam Store pages.
//
// Mirrors the README box "Steam Parser (title, price, release date, developer,
// reviews)". Two entry points:
//
//   ParseSearchPage  store.steampowered.com/search/?genre=Horror (horror)
//                    -> one raw record per result row
//   ParseAppPage     a /app/<id>/<slug>/ store page
//                    -> release date, developers, review summary
//
// Both return plain Record objects with *raw* values plus the intermediate
// fields the cleaning stage needs (price_raw, release_date_raw ...). Parsing
// stays dumb so it is trivially testable against saved HTML fixtures.

using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace ItchScraper;

public static class SteamParser
{
    /// <summary>"Oct 6, 2026" - Steam's search-row date format.</summary>
    private static readonly Regex SearchDateRegex = new(
        @"([A-Z][a-z]{2})\s+(\d{1,2}),\s+(\d{4})", RegexOptions.Compiled);

    /// <summary>Release-date value cell on an app page ("Mar 25, 2027", "Coming soon"...).</summary>
    private static readonly Regex ReleaseCellRegex = new(
        @"Release Date:\s*</div>\s*<div[^>]*>(?<date>[^<]+)</div>", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>JSON-LD block on app pages carries the authoritative ISO date.</summary>
    private static readonly Regex JsonLdDateRegex = new(
        @"""datePublished""\s*:\s*""(?<date>\d{4}-\d{2}-\d{2})", RegexOptions.Compiled);

    private static readonly Regex AppIdRegex = new(@"/app/(?<id>\d+)", RegexOptions.Compiled);

    /// <summary>"Overwhelmingly Positive (12,345)" style review summaries.</summary>
    private static readonly Regex ReviewSummaryRegex = new(
        @"(?<sentiment>Very Negative|Mostly Negative|Mixed|Mostly Positive|" +
        @"Overwhelmingly Positive|Very Positive|Positive|Negative)\s*\((?<count>[\d,.]+)\)",
        RegexOptions.Compiled);

    private static readonly Regex PercentRegex = new(@"(?<pct>\d{1,3})%", RegexOptions.Compiled);

    // ------------------------------------------------------------------ //
    // search results page                                                //
    // ------------------------------------------------------------------ //
    /// <summary>Parse one Steam search-results page into raw records.</summary>
    /// <param name="html">Body of a store.steampowered.com/search/ page.</param>
    /// <param name="sourceUrl">The search URL that produced this page.</param>
    /// <param name="page">1-based page counter used only for provenance logging.</param>
    /// <remarks>
    /// Steam has shipped two search-result layouts over the years:
    ///   * modern:  &lt;a class="search_result_row" href=".../app/ID/..."&gt;
    ///   * legacy:  &lt;div class="search_results_row"&gt;...&lt;a class="search_result_title"&gt;
    /// This parser accepts both so a markup change on Steam's side does not
    /// silently zero out the Steam sheet.
    /// </remarks>
    public static List<Record> ParseSearchPage(string html, string sourceUrl, int page = 1)
    {
        var document = PoliteFetcher.Parse(html);

        var rows = document.QuerySelectorAll(
            "a.search_result_row, div.search_results_row, div.search_result_row");

        var results = new List<Record>();
        foreach (var row in rows)
        {
            if (!IsGameRow(row)) continue;

            // The row may itself be the <a>, or contain one.
            IElement? anchor = row.LocalName == "a"
                ? row
                : row.QuerySelector("a.search_result_row, a.search_result_title, a[href*='/app/']");
            var href = anchor?.GetAttribute("href");
            if (anchor is null || href is null) continue;
            if (!href.Contains("/app/", StringComparison.Ordinal)) continue;

            // Title: modern markup wraps it in <span class="title">; legacy
            // uses the anchor text directly.
            var titleEl = row.QuerySelector("span.title") ?? anchor;
            var title = Utils.CleanText(titleEl.TextContent);
            if (title.Length == 0) continue;

            var url = PoliteFetcher.Absolute(href, "https://store.steampowered.com/");
            var record = new Record
            {
                ["platform"] = "steam",
                ["title"] = title,
                ["url"] = url,
                ["source_url"] = sourceUrl,
                ["page"] = page,
            };
            if (AppIdRegex.Match(url).Groups["id"].Value is { Length: > 0 } id)
            {
                record["game_id"] = id;
            }

            // Price: discounted rows show original + final spans; normal rows a
            // single price span; "Free To Play" rows have none (normalized to 0
            // by the pipeline after this method returns).
            var finalPrice = row.QuerySelector(".discount_final_price")?.TextContent;
            var originalPrice = row.QuerySelector(".discount_original_price")?.TextContent;
            var plainPrice = row.QuerySelector(".search_price, .search_result_price")?.TextContent;
            var priceText = Utils.FirstNonEmpty(finalPrice, originalPrice, plainPrice);
            if (priceText is not null)
            {
                record["price_raw"] = priceText;
                record["price_usd"] = priceText;
            }
            if (originalPrice is not null && finalPrice is not null)
            {
                record["price_original_raw"] = Utils.CleanText(originalPrice);
            }

            // Release date column: "Oct 6, 2026" or "Coming soon".
            // Modern markup uses `.search_released`; legacy used `.search_release`.
            var dateText = Utils.CleanText(
                row.QuerySelector(".search_released, .search_release")?.TextContent);
            if (dateText.Length > 0)
            {
                record["release_date_raw"] = dateText;
                record["release_date"] = ParseSearchDate(dateText);
            }

            results.Add(record);
        }
        return results;
    }

    /// <summary>True for rows that are games (Steam also returns bundles, DLC and
    /// special-event rows from the same query).</summary>
    private static bool IsGameRow(IElement row)
    {
        if (row.ClassList.Contains("search_result_special_event")) return false;
        var kind = Utils.CleanText(row.QuerySelector(".search_result_type")?.TextContent)
            .ToLowerInvariant();
        // Empty kind = ordinary game row; skip the known non-game kinds.
        return kind is not ("dlc" or "bundle" or "special event" or "season" or "software");
    }

    /// <summary>Turn a Steam search-row date label into ISO, or null ("Coming soon").</summary>
    internal static string? ParseSearchDate(string text)
    {
        var match = SearchDateRegex.Match(text);
        if (!match.Success) return Utils.ToIsoDate(text);
        var iso = string.Format(CultureInfo.InvariantCulture,
            "{0}-{1}-{2:D2}", match.Groups[3].Value, Month(match.Groups[1].Value),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
        return Utils.ToIsoDate(iso);
    }

    private static string Month(string abbrev) =>
        DateTime.TryParseExact(abbrev, "MMM", CultureInfo.InvariantCulture,
                               DateTimeStyles.None, out var parsed)
            ? parsed.Month.ToString(CultureInfo.InvariantCulture).PadLeft(2, '0')
            : "01";

    // ------------------------------------------------------------------ //
    // app (detail) page                                                  //
    // ------------------------------------------------------------------ //
    /// <summary>Extract release date, developers and review counts from an app page.</summary>
    /// <param name="html">Body of https://store.steampowered.com/app/&lt;id&gt;/... .</param>
    /// <param name="url">The app page URL (kept on the record for merging).</param>
    public static Record ParseAppPage(string html, string url)
    {
        var record = new Record { ["url"] = url };
        var doc = PoliteFetcher.Parse(html);

        var title = Utils.CleanText(doc.QuerySelector("div.app_name span, h1.appname")?.TextContent);
        if (title.Length > 0) record["title"] = title;

        // --- release date -------------------------------------------------
        // Prefer the machine-readable JSON-LD datePublished; fall back to the
        // human-readable "Release Date:" breakdown row.
        var jsonLd = JsonLdDateRegex.Match(html);
        if (jsonLd.Success)
        {
            record["release_date"] = Utils.ToIsoDate(jsonLd.Groups["date"].Value);
            record["release_date_raw"] = jsonLd.Groups["date"].Value;
        }
        else
        {
            var heading = ReleaseCellRegex.Match(html);
            var raw = Utils.CleanText(heading.Success
                ? heading.Groups["date"].Value
                : doc.QuerySelector("#release_date div.date")?.TextContent);
            if (raw.Length > 0)
            {
                record["release_date_raw"] = raw;
                record["release_date"] = Utils.ToIsoDate(raw);
            }
        }

        // --- developer / publisher ----------------------------------------
        var devs = doc.QuerySelectorAll("#developers_list a, #credit_link")
                       .Select(a => Utils.CleanText(a.TextContent))
                       .Where(t => t.Length > 0).Distinct().ToList();
        if (devs.Count > 0)
        {
            record["developer"] = devs[0];
            record["developers"] = devs;
        }

        // --- reviews (README: reviews_positive) ----------------------------
        // The all-reviews query summary reads e.g.
        //   "Overwhelmingly Positive (25,908) - 95% of the 25,908 user reviews... are positive."
        var reviewText = doc.QuerySelector("#default_reviews_all .query_summary")?.TextContent
                      ?? doc.QuerySelector(".game_review_summary.total")?.TextContent;
        var summaryMatch = ReviewSummaryRegex.Match(reviewText ?? string.Empty);
        var pctMatch = PercentRegex.Match(reviewText ?? string.Empty);
        if (summaryMatch.Success)
        {
            var sentiment = summaryMatch.Groups["sentiment"].Value;
            var digits = new string(summaryMatch.Groups["count"].Value.Where(char.IsDigit).ToArray());
            if (long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var total))
            {
                var fraction = pctMatch.Success
                    ? int.Parse(pctMatch.Groups["pct"].Value, CultureInfo.InvariantCulture) / 100.0
                    : DefaultSentimentFraction(sentiment);
                record["reviews_total"] = total;
                record["review_percent"] = pctMatch.Success ? pctMatch.Groups["pct"].Value + "%" : null;
                record["review_sentiment"] = sentiment;
                record["reviews_positive"] = (long)Math.Round(total * fraction);
            }
        }

        // --- genre tags ------------------------------------------------------
        var genres = doc.QuerySelectorAll("#genresAndManufacturer a, .glance_tags a")
                        .Select(a => Utils.CleanText(a.TextContent))
                        .Where(t => t.Length > 0 && t != "View all")
                        .Distinct().Take(8).ToList();
        if (genres.Count > 0) record["genres"] = genres;

        return record;
    }

    /// <summary>Rough prior when only a word like "Mostly Positive" is visible.</summary>
    private static double DefaultSentimentFraction(string? sentiment) => sentiment switch
    {
        "Overwhelmingly Positive" => 0.95,
        "Very Positive" => 0.90,
        "Mostly Positive" => 0.80,
        "Positive" => 0.75,
        "Mixed" => 0.60,
        "Mostly Negative" => 0.40,
        "Very Negative" => 0.20,
        _ => 0.70,
    };
}