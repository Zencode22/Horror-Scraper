// PARSE & EXTRACT layer for itch.io pages.
//
// Two entry points, matching the README box "Itch Parser (title, price, dev,
// upload date)":
//
//   ParseListingPage  browse/tag/horror grid -> one raw record per game card
//   ParseGamePage     a game page -> upload date, author, rating, tags ...
//
// Both return plain Record objects with *raw* (un-normalised) values plus the
// intermediate fields the cleaning stage needs (price_raw, release_date_raw,
// updated_at_raw...). Keeping parsing dumb makes it trivially unit-testable
// against saved HTML fixtures.

using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace ItchScraper;

public static class ItchParser
{
    /// <summary>itch.io's "X ago" / relative dates are not real release dates - never trust them.</summary>
    private static readonly Regex RelativeDateRegex = new(
        @"^\s*(?:just now|now|\d+\s*(?:second|minute|hour|day|week|month|year)s?\s*ago|yesterday|today)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RatingTooltipRegex = new(
        @"([\d.,]+)\s+average rating from\s+([\d,]+)\s+total ratings", RegexOptions.Compiled);

    private static readonly Regex PurchasePriceRegex = new(
        @"(?:US)?\$\s?\d+(?:\.\d{1,2})?", RegexOptions.Compiled);

    private static readonly Regex FreeOrNypRegex = new(
        @"name your own price|free", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ------------------------------------------------------------------ //
    // listing pages                                                      //
    // ------------------------------------------------------------------ //
    /// <summary>Extract every game card from an itch.io browse listing.</summary>
    /// <param name="html">Body of https://itch.io/games/...tag-horror?page=N.</param>
    /// <param name="sourceUrl">URL the body came from (stored for provenance/debugging).</param>
    /// <param name="page">Listing page number (stored on each record).</param>
    /// <returns>A list of raw records; cards without a title/link are skipped.</returns>
    public static List<Record> ParseListingPage(string html, string sourceUrl, int page = 1)
    {
        var document = PoliteFetcher.Parse(html ?? string.Empty);
        var records = new List<Record>();

        foreach (var cell in document.QuerySelectorAll(".game_cell"))
        {
            var record = ParseGameCell(cell, sourceUrl, page);
            if (record is not null) records.Add(record);
        }

        // Fallback: some layouts render a table instead of the card grid.
        if (records.Count == 0)
        {
            foreach (var anchor in document.QuerySelectorAll("a.game_link[href*='.itch.io']"))
            {
                var record = RecordFromAnchor(anchor, sourceUrl, page);
                if (record is not null &&
                    !records.Any(r => string.Equals((string?)r["url"], (string?)record["url"], StringComparison.Ordinal)))
                {
                    records.Add(record);
                }
            }
        }

        return records;
    }

    /// <summary>Turn a single div.game_cell into a raw record.</summary>
    private static Record? ParseGameCell(IElement cell, string sourceUrl, int page)
    {
        var titleAnchor = cell.QuerySelector("a.title") ?? cell.QuerySelector("a.game_link");
        if (titleAnchor is null) return null;

        var url = titleAnchor.GetAttribute("href") ?? string.Empty;
        var title = Utils.CleanText(titleAnchor.TextContent);
        if (title.Length == 0 || url.Length == 0) return null;

        var priceTag = cell.QuerySelector(".price_tag");
        var priceRaw = PriceText(priceTag);

        var authorAnchor = cell.QuerySelector(".game_author a");
        var developer = authorAnchor is not null ? Utils.CleanText(authorAnchor.TextContent) : string.Empty;

        var genreEl = cell.QuerySelector(".game_genre");
        var descriptionEl = cell.QuerySelector(".game_text");

        return new Record
        {
            ["title"] = title,
            ["platform"] = "itch",
            ["url"] = url,
            ["game_id"] = cell.GetAttribute("data-game_id"),
            ["developer"] = developer,
            ["price_raw"] = priceRaw,
            ["price_usd"] = Utils.NormalizePrice(priceRaw),
            ["genre"] = genreEl is not null ? Utils.CleanText(genreEl.TextContent) : string.Empty,
            ["short_description"] = descriptionEl is not null
                ? Utils.CleanText(descriptionEl.GetAttribute("title") ?? descriptionEl.TextContent)
                : string.Empty,
            ["platforms"] = PlatformLabels(cell),
            ["release_date"] = null,   // listings never carry a release date
            ["release_date_raw"] = null,
            ["source"] = "listing",
            ["source_url"] = sourceUrl,
            ["page"] = page,
        };
    }

    /// <summary>Build a minimal record when only a bare game link is available.</summary>
    private static Record? RecordFromAnchor(IElement anchor, string sourceUrl, int page)
    {
        var title = Utils.CleanText(anchor.TextContent);
        var url = anchor.GetAttribute("href") ?? string.Empty;
        if (title.Length == 0 || url.Length == 0) return null;

        return new Record
        {
            ["title"] = title,
            ["platform"] = "itch",
            ["url"] = url,
            ["game_id"] = null,
            ["developer"] = string.Empty,
            ["price_raw"] = null,
            ["price_usd"] = null,
            ["genre"] = string.Empty,
            ["short_description"] = string.Empty,
            ["platforms"] = new List<string>(),
            ["release_date"] = null,
            ["release_date_raw"] = null,
            ["source"] = "listing-fallback",
            ["source_url"] = sourceUrl,
            ["page"] = page,
        };
    }

    /// <summary>
    /// Pull the price string out of a listing .price_tag element. The visible
    /// text is usually "$7.99" or "$0 -100%"; name-your-price games additionally
    /// expose title="Pay $1 or more for this game", which we prefer because it
    /// survives sale badges.
    /// </summary>
    private static string? PriceText(IElement? priceTag)
    {
        if (priceTag is null) return null;

        var valueEl = priceTag.QuerySelector(".price_value");
        var candidates = new[]
        {
            priceTag.GetAttribute("title"),
            valueEl?.TextContent,
            priceTag.TextContent,
        };
        foreach (var candidate in candidates)
        {
            var text = Utils.CleanText(candidate);
            if (text.Length > 0) return text;
        }
        return null;
    }

    /// <summary>Windows/Linux/macOS icons on a game card, as human labels.</summary>
    private static List<string> PlatformLabels(IElement cell)
    {
        var labels = new List<string>();
        foreach (var span in cell.QuerySelectorAll(".game_platform span[title]"))
        {
            var label = Utils.CleanText(span.GetAttribute("title")).ToLowerInvariant()
                .Replace("download for ", "");
            if (label.Length > 0 && !labels.Contains(label)) labels.Add(label);
        }
        return labels;
    }

    // ------------------------------------------------------------------ //
    // detail (game) pages                                                //
    // ------------------------------------------------------------------ //
    /// <summary>Extract the fields a browse listing cannot provide.</summary>
    /// <param name="html">Body of a game page (&lt;user&gt;.itch.io/&lt;slug&gt;).</param>
    /// <param name="url">That page's URL (used for provenance and developer fallback).</param>
    /// <returns>Raw detail fields: release_date/release_date_raw, updated_at,
    /// developer, rating_count, tags, status, classification, price_raw and price_usd.</returns>
    public static Record ParseGamePage(string html, string url)
    {
        var document = PoliteFetcher.Parse(html ?? string.Empty);
        var details = new Record
        {
            ["url"] = url,
            ["title"] = DetailTitle(document),
            ["developer"] = DetailDeveloper(document, url),
            ["release_date"] = null,
            ["release_date_raw"] = null,
            ["updated_at"] = null,
            ["updated_at_raw"] = null,
            ["status"] = null,
            ["classification"] = null,
            ["rating_count"] = null,
            ["rating_value"] = null,
            ["tags"] = new List<string>(),
            ["genres"] = new List<string>(),
            ["price_raw"] = null,
            ["price_usd"] = null,
            ["source"] = "detail",
        };

        var infoRows = InfoPanelRows(document);

        // --- upload / release date ------------------------------------- //
        var ld = LdJsonProduct(document);
        var releasedRaw = RowValue(infoRows, "released", "release date", "published");
        if (!string.IsNullOrEmpty(releasedRaw))
        {
            var iso = Utils.ToIsoDate(releasedRaw);
            if (iso is not null)
            {
                details["release_date"] = iso;
                details["release_date_raw"] = releasedRaw;
            }
        }
        var updatedRaw = RowValue(infoRows, "updated", "last updated");
        if (!string.IsNullOrEmpty(updatedRaw))
        {
            var iso = Utils.ToIsoDate(updatedRaw);
            if (iso is not null)
            {
                details["updated_at"] = iso;
                details["updated_at_raw"] = updatedRaw;
            }
        }

        // --- status / classification ----------------------------------- //
        var status = RowValue(infoRows, "status");
        if (!string.IsNullOrEmpty(status)) details["status"] = status;
        var classification = RowValue(infoRows, "authors", "author");
        if (!string.IsNullOrEmpty(classification) && string.IsNullOrEmpty(details.Str("developer")))
        {
            details["developer"] = classification;
        }

        // --- ratings ---------------------------------------------------- //
        if (ld is not null)
        {
            var product = ld.Value;
            if (product.TryGetProperty("aggregateRating", out var rating) && rating.ValueKind == JsonValueKind.Object)
            {
                details["rating_count"] = AsInt(GetOpt(rating, "ratingCount"));
                details["rating_value"] = AsFloat(GetOpt(rating, "ratingValue"));
            }
            if (product.TryGetProperty("offers", out var offers) && offers.ValueKind == JsonValueKind.Object &&
                GetOpt(offers, "price") is { } priceElement)
            {
                var priceText = priceElement.ValueKind == JsonValueKind.Number
                    ? priceElement.GetRawText()
                    : priceElement.GetString();
                var currency = GetOpt(offers, "priceCurrency")?.GetString() ?? "USD";
                if (!string.IsNullOrEmpty(priceText))
                {
                    details["price_raw"] = $"${priceText}".Trim();
                    if (currency != "USD") details["price_raw"] = $"{details["price_raw"]} {currency}";
                    details["price_usd"] = Utils.NormalizePrice(details["price_raw"]);
                }
            }
        }

        // tooltip text such as "4.51 average rating from 49 total ratings".
        if (details["rating_count"] is null)
        {
            var tooltipEl = document.QuerySelector("[data-tooltip*='total ratings']");
            var tooltip = tooltipEl?.GetAttribute("data-tooltip");
            if (!string.IsNullOrEmpty(tooltip))
            {
                var match = RatingTooltipRegex.Match(Utils.CleanText(tooltip));
                if (match.Success)
                {
                    details["rating_value"] ??= AsFloat(match.Groups[1].Value.Replace(",", "."));
                    details["rating_count"] = AsInt(match.Groups[2].Value);
                }
            }
        }

        // --- tags / genres ---------------------------------------------- //
        var tagRow = RowValue(infoRows, "tags");
        if (!string.IsNullOrEmpty(tagRow))
        {
            details["tags"] = tagRow.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        }
        var genreRow = RowValue(infoRows, "genre");
        if (!string.IsNullOrEmpty(genreRow))
        {
            details["genres"] = genreRow.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(g => g.Trim()).Where(g => g.Length > 0).ToList();
        }

        // --- purchase widget price (name-your-price detection) ---------- //
        if (details["price_raw"] is null)
        {
            details["price_raw"] = PurchasePriceText(document);
            if (details["price_raw"] is not null)
            {
                details["price_usd"] = Utils.NormalizePrice(details["price_raw"]);
            }
        }

        return details;
    }

    /// <summary>Game title from the page heading (falls back to &lt;title&gt;).</summary>
    private static string? DetailTitle(IDocument document)
    {
        foreach (var selector in new[] { "h1.game_title", "h1", "title" })
        {
            var el = document.QuerySelector(selector);
            if (el is null) continue;
            var text = Utils.CleanText(el.TextContent);
            if (text.Length == 0) continue;
            // "<title>" looks like "Name by Author".
            if (selector == "title" && text.Contains(" by ", StringComparison.Ordinal))
            {
                text = text[..text.LastIndexOf(" by ", StringComparison.Ordinal)].Trim();
            }
            return text;
        }
        return null;
    }

    /// <summary>Developer/user name, taken from the subdomain or the page header.</summary>
    private static string? DetailDeveloper(IDocument document, string url)
    {
        var match = Regex.Match(url ?? string.Empty, @"^https?://([^/.]+)\.itch\.io");
        var el = document.QuerySelector(".game_author_name a, .creator_link, .game_header_info a[href*='itch.io']");
        var authorRow = el is not null ? Utils.CleanText(el.TextContent) : null;
        if (!string.IsNullOrEmpty(authorRow)) return authorRow;
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Map the game info panel table to {label: (plain_text, abbr_title)}.
    /// itch.io renders relative dates ("21 hours ago") as visible text while the
    /// absolute timestamp hides in &lt;abbr title="28 September 2026 @ 20:08 UTC"&gt;
    /// - so we always capture both and let the caller prefer the precise one.
    /// </summary>
    private static Dictionary<string, (string Text, string? Absolute)> InfoPanelRows(IDocument document)
    {
        var rows = new Dictionary<string, (string, string?)>(StringComparer.Ordinal);
        foreach (var table in document.QuerySelectorAll(".game_info_panel_widget table, table.info_panel"))
        {
            foreach (var tr in table.QuerySelectorAll("tr"))
            {
                var cells = tr.QuerySelectorAll("td");
                if (cells.Length < 2) continue;
                var label = Utils.CleanText(cells[0].TextContent).ToLowerInvariant();
                if (label.Length == 0) continue;

                var valueCell = cells[1];
                var abbr = valueCell.QuerySelector("abbr");
                var absolute = abbr is not null ? Utils.CleanText(abbr.GetAttribute("title")) : null;
                if (string.IsNullOrEmpty(absolute)) absolute = null;
                var text = Utils.CleanText(valueCell.TextContent);

                // Relative strings are useless as dates; keep only absolute ones.
                var effective = absolute ?? (RelativeDateRegex.IsMatch(text) ? null : text);
                rows[label] = (effective ?? text, absolute);
            }
        }
        return rows;
    }

    /// <summary>First non-empty value whose label starts with any of <paramref name="keys"/>.</summary>
    private static string? RowValue(Dictionary<string, (string Text, string? Absolute)> rows, params string[] keys)
    {
        foreach (var (label, (text, _)) in rows)
        {
            if (keys.Any(key => label.StartsWith(key, StringComparison.Ordinal)))
            {
                return string.IsNullOrEmpty(text) ? null : text;
            }
        }
        return null;
    }

    /// <summary>The schema.org Product JSON-LD block, if present and valid.</summary>
    private static JsonElement? LdJsonProduct(IDocument document)
    {
        foreach (var script in document.QuerySelectorAll("script[type='application/ld+json']"))
        {
            var raw = script.TextContent;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            JsonElement root;
            try
            {
                root = JsonDocument.Parse(raw).RootElement;
            }
            catch (JsonException)
            {
                continue;
            }

            var blocks = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray().ToList()
                : new List<JsonElement> { root };
            foreach (var block in blocks)
            {
                if (block.ValueKind == JsonValueKind.Object &&
                    GetOpt(block, "@type")?.GetString() == "Product")
                {
                    return block;
                }
            }
        }
        return null;
    }

    private static JsonElement? GetOpt(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) ? value : null;

    /// <summary>Price wording from the buy/download widget ("$5 or more", "Free").</summary>
    private static string? PurchasePriceText(IDocument document)
    {
        var buyRow = document.QuerySelector(".buy_row, .game_purchase_form, .purchases_table");
        if (buyRow is null) return null;
        var text = Utils.CleanText(buyRow.TextContent);
        if (text.Length == 0) return null;
        if (FreeOrNypRegex.IsMatch(text)) return "$0";
        var match = PurchasePriceRegex.Match(text);
        return match.Success ? match.Value : null;
    }

    /// <summary>Best-effort int conversion ('11,074' -> 11074).</summary>
    private static long? AsInt(object? value)
    {
        if (value is null) return null;
        var digits = Regex.Replace(value.ToString() ?? string.Empty, @"[^\d]", "");
        return digits.Length > 0 && long.TryParse(digits, out var parsed) ? parsed : null;
    }

    /// <summary>Best-effort float conversion.</summary>
    private static double? AsFloat(object? value)
    {
        if (value is null) return null;
        if (value is JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.Number) return json.GetDouble();
            if (json.ValueKind != JsonValueKind.String) return null;
            value = json.GetString();
        }
        var text = (value.ToString() ?? string.Empty).Replace(",", ".");
        return double.TryParse(text, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed : null;
    }
}
