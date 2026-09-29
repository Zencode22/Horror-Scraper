// End-to-end itch.io pipeline: fetch -> parse -> clean -> Excel.
//
// This is the module the README's mermaid diagram collapses into "Itch Fetcher
// -> Itch Parser -> Cleaning -> Excel Writer". Call ScrapeAsync from your own
// code, or run the CLI (Program.cs) for a command-line run.

namespace ItchScraper;

/// <summary>Artefacts produced by one scrape run.</summary>
public sealed record ScrapeResult(
    GameTable Table,
    List<Record> RawRecords,
    RunStats Stats,
    string? Workbook,
    Dictionary<string, object?> Summary);

public static class Pipeline
{
    /// <summary>Scrape itch.io horror listings and return the run's artefacts.</summary>
    /// <param name="config">Run configuration; defaults to a fresh instance.</param>
    /// <param name="fetcher">Pre-built fetcher (tests inject a stubbed one). When
    /// omitted a rate-limited <see cref="ItchFetcher"/> is used.</param>
    /// <param name="writeOutput">Also write the Excel workbook. Set false to only
    /// get the table back.</param>
    /// <param name="steamTable">Optional cleaned Steam table so the Combined sheet
    /// can be produced in one pass once that branch exists.</param>
    /// <param name="logger">Logger shared with the stats object.</param>
    public static async Task<ScrapeResult> ScrapeAsync(
        ItchScraperConfig? config = null,
        ItchFetcher? fetcher = null,
        bool writeOutput = true,
        GameTable? steamTable = null,
        ScraperLogger? logger = null)
    {
        config ??= new ItchScraperConfig();
        var ownedLogger = logger ?? new ScraperLogger();
        ownedLogger.Configure(config.LogDir);
        var stats = new RunStats(config, ownedLogger);
        stats.ResetFailedLog();
        stats.Info($"starting itch.io scrape | pages<= {config.MaxPages} | details={config.FetchDetails}");

        var ownsFetcher = fetcher is null;
        fetcher ??= new ItchFetcher(config, stats);

        var robots = await LoadRobotsAsync(fetcher, stats).ConfigureAwait(false);

        var listings = new List<Record>();
        foreach (var sourceUrl in robots.FilterUrls(config.SourceUrls, stats))
        {
            listings.AddRange(await CrawlListingAsync(fetcher, sourceUrl, robots, config, stats)
                .ConfigureAwait(false));
        }

        stats.Info($"collected {listings.Count} listing rows from {config.SourceUrls.Count} source(s)");

        var detailsByUrl = new Dictionary<string, Record>(StringComparer.Ordinal);
        if (config.FetchDetails)
        {
            detailsByUrl = await FetchDetailsAsync(fetcher, listings, robots, config, stats)
                .ConfigureAwait(false);
        }

        var merged = Cleaning.MergeDetails(listings, detailsByUrl);
        var table = Cleaning.CleanTable(merged, stats);

        string? workbookPath = null;
        if (writeOutput && table.Count > 0)
        {
            workbookPath = config.ResolvedWorkbookPath;
            ExcelWriter.WriteItchSheet(
                table, workbookPath,
                sheetItch: config.SheetItch,
                sheetSteam: config.SheetSteam,
                sheetCombined: config.SheetCombined,
                steamTable: steamTable,
                stats: stats);
        }
        else if (writeOutput)
        {
            stats.Warning("nothing to write: the cleaned table is empty");
        }

        var summary = stats.Summary();
        summary["cache"] = stats.CacheStats();
        stats.Logger.Info($"run summary: {JsonLine.Serialize(summary)}");
        AppendRunSummary(config, summary);
        stats.Report();

        if (ownsFetcher) fetcher.Dispose();

        return new ScrapeResult(table, merged, stats, workbookPath, summary);
    }

    // ------------------------------------------------------------------ //
    // crawl helpers                                                      //
    // ------------------------------------------------------------------ //
    /// <summary>Read itch.io's robots.txt through the same polite fetcher.</summary>
    private static async Task<RobotsPolicy> LoadRobotsAsync(ItchFetcher fetcher, RunStats stats)
    {
        try
        {
            return await RobotsFetcher.FetchRobotsAsync(fetcher, stats).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            stats.Warning($"robots lookup blew up ({e.Message}); crawling permissive policy");
            return RobotsPolicy.AllowAll("exception");
        }
    }

    /// <summary>Walk ?page=N browse pages until exhausted or MaxPages reached.</summary>
    private static async Task<List<Record>> CrawlListingAsync(
        ItchFetcher fetcher, string startUrl, RobotsPolicy robots,
        ItchScraperConfig config, RunStats stats)
    {
        var collected = new List<Record>();
        string? url = startUrl;
        var seenPages = new HashSet<string>(StringComparer.Ordinal);

        for (var pageNumber = 1; pageNumber <= config.MaxPages; pageNumber++)
        {
            if (url is null || !seenPages.Add(url)) break;
            if (!robots.IsAllowed(url))
            {
                stats.Info($"robots.txt blocks {url} - stopping this source");
                break;
            }

            var html = await fetcher.GetAsync(
                url,
                new Dictionary<string, object?> { ["source"] = startUrl, ["page"] = pageNumber })
                .ConfigureAwait(false);
            if (html is null) break;

            var rows = ItchParser.ParseListingPage(html, url, pageNumber);
            if (rows.Count == 0)
            {
                stats.Info($"no game cards on {url} (layout change?) - stopping");
                break;
            }

            stats.Listings += rows.Count;
            collected.AddRange(rows);
            stats.Info($"page {pageNumber} of {url} -> {rows.Count} games");

            url = fetcher.NextPageUrl(html, url);
        }

        return collected;
    }

    /// <summary>Fetch each unique game page to recover its upload/release date.</summary>
    private static async Task<Dictionary<string, Record>> FetchDetailsAsync(
        ItchFetcher fetcher, List<Record> listings, RobotsPolicy robots,
        ItchScraperConfig config, RunStats stats)
    {
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in listings)
        {
            var url = Utils.CleanText(row.Get("url"));
            if (url.Length > 0 && seen.Add(url)) urls.Add(url);
        }

        var allowed = robots.FilterUrls(urls, stats).Take(config.MaxDetailRequests).ToList();
        if (allowed.Count < seen.Count)
        {
            stats.Info($"detail requests capped at {allowed.Count} of {seen.Count} unique games");
        }

        var details = new Dictionary<string, Record>(StringComparer.Ordinal);
        for (var index = 0; index < allowed.Count; index++)
        {
            var url = allowed[index];
            var html = await fetcher.GetAsync(
                url, new Dictionary<string, object?> { ["detail_index"] = index + 1 })
                .ConfigureAwait(false);
            if (html is null) continue;

            details[url] = ItchParser.ParseGamePage(html, url);
            stats.Details++;
            if ((index + 1) % 20 == 0)
            {
                stats.Info($"details: {index + 1}/{allowed.Count} fetched");
            }
        }
        return details;
    }

    /// <summary>Append the JSON summary line so trends across runs stay inspectable.</summary>
    private static void AppendRunSummary(ItchScraperConfig config, IDictionary<string, object?> summary)
    {
        try
        {
            Directory.CreateDirectory(config.LogDir);
            File.AppendAllText(Path.Combine(config.LogDir, "run_summary.jsonl"),
                               JsonLine.Serialize(summary) + Environment.NewLine);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Logging must never take the pipeline down.
            Console.Error.WriteLine($"warning: could not persist run summary: {e.Message}");
        }
    }
}
