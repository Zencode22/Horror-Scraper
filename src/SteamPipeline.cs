// STEAM BRANCH of the single-program pipeline: fetch -> parse -> clean.
//
// Mirrors the README boxes "Steam Store -> Steam Fetcher -> Steam Parser".
// The output is a cleaned GameTable that the shared ExcelWriter drops into
// the "Steam" sheet and merges into "Combined"; itch.io keeps its own branch
// in Pipeline.cs. Both branches share ONE PoliteFetcher instance, so the
// 1-2 s crawl delay applies across every request of the run.

namespace ItchScraper;

public static class SteamPipeline
{
    /// <summary>Crawl Steam's horror-genre search pages and return cleaned rows.</summary>
    /// <param name="config">Shared run configuration.</param>
    /// <param name="fetcher">The run-wide polite fetcher (never disposed here).</param>
    /// <param name="robotsByHost">Per-host robots policies gathered by the caller.</param>
    /// <param name="stats">Run counters / logger.</param>
    public static async Task<GameTable> ScrapeAsync(
        ItchScraperConfig config,
        PoliteFetcher fetcher,
        IReadOnlyDictionary<string, RobotsPolicy> robotsByHost,
        RunStats stats)
    {
        var listings = new List<Record>();
        foreach (var sourceUrl in PermittedUrls(config.SteamSourceUrls, robotsByHost, stats))
        {
            listings.AddRange(await CrawlSearchAsync(fetcher, sourceUrl, robotsByHost, config, stats)
                .ConfigureAwait(false));
        }

        stats.Info($"steam: collected {listings.Count} listing rows from {config.SteamSourceUrls.Count} source(s)");

        var detailsByUrl = new Dictionary<string, Record>(StringComparer.Ordinal);
        if (config.FetchSteamDetails)
        {
            detailsByUrl = await FetchAppPagesAsync(fetcher, listings, robotsByHost, config, stats)
                .ConfigureAwait(false);
        }

        var merged = Cleaning.MergeDetails(listings, detailsByUrl);

        // "Free To Play" rows carry no numeric price span. Steam never shows an
        // explicit "$0.00", so we detect the free wording on *either* the raw
        // price text or the (un-normalised) price_usd and force it to 0.0.
        // Without this fix those rows survive parsing, then NormalizeRows turns
        // "Free To Play" into NaN, and ValidateRows silently drops them.
        foreach (var row in merged)
        {
            var raw = Utils.CleanText(row.Get("price_raw")).ToLowerInvariant();
            var usd = Utils.CleanText(row.Get("price_usd")).ToLowerInvariant();
            var looksFree = raw.Contains("free")
                         || usd.Contains("free")
                         || (raw.Length == 0 && usd.Length == 0);
            if (looksFree)
            {
                row["price_raw"] = "$0";
                row["price_usd"] = 0.0;
            }
        }

        return Cleaning.CleanTable(merged, stats);
    }

    // ------------------------------------------------------------------ //
    // crawl helpers                                                      //
    // ------------------------------------------------------------------ //
    private static IEnumerable<string> PermittedUrls(
        IEnumerable<string> urls, IReadOnlyDictionary<string, RobotsPolicy> robotsByHost, RunStats stats)
    {
        foreach (var url in urls)
        {
            var policy = PolicyFor(url, robotsByHost);
            if (policy.IsAllowed(url)) yield return url;
            else stats.Info($"steam: robots.txt blocks {url} - skipping source");
        }
    }

    /// <summary>Pick the robots policy whose host matches the URL (itch fallback for unknown hosts).</summary>
    internal static RobotsPolicy PolicyFor(string url, IReadOnlyDictionary<string, RobotsPolicy> robotsByHost)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            robotsByHost.TryGetValue(uri.Host, out var policy))
        {
            return policy;
        }
        return robotsByHost.TryGetValue("default", out var fallback) ? fallback : RobotsPolicy.AllowAll("no policy");
    }

    /// <summary>Walk Steam search pages via the &start=N pagination parameter.</summary>
    private static async Task<List<Record>> CrawlSearchAsync(
        PoliteFetcher fetcher, string startUrl,
        IReadOnlyDictionary<string, RobotsPolicy> robotsByHost,
        ItchScraperConfig config, RunStats stats)
    {
        var collected = new List<Record>();
        const int pageSize = 75; // search count= param we request

        for (var pageNumber = 1; pageNumber <= config.MaxPages; pageNumber++)
        {
            var url = pageNumber == 1
                ? startUrl
                : AppendStart(startUrl, (pageNumber - 1) * pageSize);
            if (!PolicyFor(url, robotsByHost).IsAllowed(url))
            {
                stats.Info($"steam: robots.txt blocks {url} - stopping this source");
                break;
            }

            var html = await fetcher.GetAsync(
                url,
                new Dictionary<string, object?> { ["source"] = "steam", ["page"] = pageNumber })
                .ConfigureAwait(false);
            if (html is null) break;

            var rows = SteamParser.ParseSearchPage(html, url, pageNumber);
            if (rows.Count == 0)
            {
                stats.Info($"steam: no result rows on {url} (end of list or layout change) - stopping");
                break;
            }

            stats.Listings += rows.Count;
            collected.AddRange(rows);
            stats.Info($"steam page {pageNumber} -> {rows.Count} games");

            if (rows.Count < pageSize) break; // last partial page
        }
        return collected;
    }

    private static string AppendStart(string url, int start) =>
        url.Contains("?") ? $"{url}&start={start}" : $"{url}?start={start}";

    /// <summary>Fetch each unique app page to recover date / developer / reviews.</summary>
    private static async Task<Dictionary<string, Record>> FetchAppPagesAsync(
        PoliteFetcher fetcher, List<Record> listings,
        IReadOnlyDictionary<string, RobotsPolicy> robotsByHost,
        ItchScraperConfig config, RunStats stats)
    {
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in listings)
        {
            var url = Utils.CleanText(row.Get("url"));
            if (url.Length > 0 && seen.Add(url)) urls.Add(url);
        }

        var allowed = urls.Where(u => PolicyFor(u, robotsByHost).IsAllowed(u))
                          .Take(config.MaxSteamDetailRequests).ToList();
        if (allowed.Count < seen.Count)
        {
            stats.Info($"steam: detail requests capped at {allowed.Count} of {seen.Count} unique apps");
        }

        var details = new Dictionary<string, Record>(StringComparer.Ordinal);
        for (var index = 0; index < allowed.Count; index++)
        {
            var url = allowed[index];
            var html = await fetcher.GetAsync(
                url, new Dictionary<string, object?> { ["detail_index"] = index + 1, ["source"] = "steam" })
                .ConfigureAwait(false);
            if (html is null) continue;

            details[url] = SteamParser.ParseAppPage(html, url);
            stats.Details++;
            if ((index + 1) % 20 == 0)
            {
                stats.Info($"steam details: {index + 1}/{allowed.Count} fetched");
            }
        }
        return details;
    }
}