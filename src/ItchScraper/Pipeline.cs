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
    Dictionary<string, object?> Summary,
    GameTable? SteamTable = null);

public static class Pipeline
{
    // ------------------------------------------------------------------ //
    // combined entry point (the one Program.cs uses)                     //
    // ------------------------------------------------------------------ //
    /// <summary>Crawl every enabled source (Steam + itch.io) and write the
    /// single three-sheet workbook.</summary>
    /// <param name="config">Shared run configuration.</param>
    /// <param name="writeOutput">Also write the Excel workbook.</param>
    /// <param name="logger">Optional logger (a fresh one is created otherwise).</param>
    public static async Task<ScrapeResult> RunAsync(
        ItchScraperConfig? config = null,
        bool writeOutput = true,
        ScraperLogger? logger = null,
        PoliteFetcher? fetcher = null)
    {
        config ??= new ItchScraperConfig();
        var ownedLogger = logger ?? new ScraperLogger();
        ownedLogger.Configure(config.LogDir);
        var stats = new RunStats(config, ownedLogger);
        stats.ResetFailedLog();

        var sources = new List<string>();
        if (config.FetchSteam) sources.Add("steam");
        if (config.FetchItch) sources.Add("itch");
        if (sources.Count == 0) sources.Add("itch"); // never crawl nothing
        stats.Info($"starting combined scrape | sources=[{string.Join(", ")}] | pages<= {config.MaxPages}");

        var ownsFetcher = fetcher is null;
        fetcher ??= new PoliteFetcher(config, stats);
        try
        {
            return await RunCoreAsync(config, fetcher, stats, writeOutput).ConfigureAwait(false);
        }
        finally
        {
            if (ownsFetcher) fetcher.Dispose();
        }
    }

    private static async Task<ScrapeResult> RunCoreAsync(
        ItchScraperConfig config, PoliteFetcher fetcher, RunStats stats, bool writeOutput)
    {
        // Which sources this run crawls (mirrors the flag computed by ScrapeAsync).
        var sources = new List<string>();
        if (config.FetchSteam) sources.Add("steam");
        if (config.FetchItch) sources.Add("itch");
        if (sources.Count == 0) sources.Add("itch"); // never crawl nothing

        var robotsByHost = await LoadRobotsByHostAsync(fetcher, config, stats).ConfigureAwait(false);

        GameTable? itchTable = null;
        GameTable? steamTable = null;
        var rawRecords = new List<Record>();

        if (config.FetchSteam && sources.Contains("steam"))
        {
            steamTable = await SteamPipeline
                .ScrapeAsync(config, fetcher, robotsByHost, stats)
                .ConfigureAwait(false);
            rawRecords.AddRange(steamTable.Rows);
        }

        if (config.FetchItch && sources.Contains("itch"))
        {
            var itch = await ScrapeInternalAsync(config, fetcher, robotsByHost, stats, writeOutput: false)
                .ConfigureAwait(false);
            itchTable = itch.Table;
            rawRecords.AddRange(itch.RawRecords);
        }

        string? workbookPath = null;
        if (writeOutput && (itchTable?.Count > 0 || steamTable?.Count > 0))
        {
            workbookPath = config.ResolvedWorkbookPath;
            ExcelWriter.WriteWorkbook(
                itchTable, steamTable, workbookPath,
                sheetCombined: config.SheetCombined,
                sheetSteam: config.SheetSteam,
                sheetItch: config.SheetItch,
                stats: stats);
        }
        else if (writeOutput)
        {
            stats.Warning("nothing to write: both source tables are empty");
        }

        var summary = stats.Summary();
        summary["cache"] = stats.CacheStats();
        summary["steam_rows"] = steamTable?.Count ?? 0;
        summary["itch_rows"] = itchTable?.Count ?? 0;

        var combined = ExcelWriter.CombinedTable(itchTable, steamTable);
        return new ScrapeResult(combined, rawRecords, stats, workbookPath, summary, steamTable);
    }

    /// <summary>Fetch robots.txt once per source host so each site's rules are honoured.</summary>
    private static async Task<Dictionary<string, RobotsPolicy>> LoadRobotsByHostAsync(
        PoliteFetcher fetcher, ItchScraperConfig config, RunStats stats)
    {
        var hosts = new List<(string Host, string Url)> { ("itch.io", "https://itch.io/robots.txt") };
        if (config.FetchSteam) hosts.Add(("store.steampowered.com", "https://store.steampowered.com/robots.txt"));

        var policies = new Dictionary<string, RobotsPolicy>(StringComparer.OrdinalIgnoreCase);
        foreach (var (host, url) in hosts)
        {
            try
            {
                policies[host] = await RobotsFetcher
                    .FetchRobotsAsync(fetcher, stats, url)
                    .ConfigureAwait(false);
            }
            catch (Exception e)
            {
                stats.Warning($"robots lookup for {host} blew up ({e.Message}); crawling permissive policy");
                policies[host] = RobotsPolicy.AllowAll($"exception: {host}");
            }
        }
        return policies;
    }

    // ------------------------------------------------------------------ //
    // itch-only entry points                                             //
    // ------------------------------------------------------------------ //
    /// <summary>Scrape itch.io horror listings and return the run's artefacts.</summary>
    /// <param name="config">Run configuration; defaults to a fresh instance.</param>
    /// <param name="fetcher">Pre-built fetcher (tests inject a stubbed one). When
    /// omitted a rate-limited <see cref="PoliteFetcher"/> is used.</param>
    /// <param name="writeOutput">Also write the Excel workbook. Set false to only
    /// get the table back.</param>
    /// <param name="steamTable">Optional cleaned Steam table so the Combined sheet
    /// can be produced in one pass once that branch exists.</param>
    /// <param name="logger">Logger shared with the stats object.</param>
    public static async Task<ScrapeResult> ScrapeAsync(
        ItchScraperConfig? config = null,
        PoliteFetcher? fetcher = null,
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
        fetcher ??= new PoliteFetcher(config, stats);
        try
        {
            var robotsByHost = await LoadRobotsByHostAsync(fetcher, config, stats).ConfigureAwait(false);
            var itchOnly = new Dictionary<string, RobotsPolicy>(robotsByHost)
            {
                ["default"] = robotsByHost.TryGetValue("itch.io", out var p) ? p : RobotsPolicy.AllowAll("missing"),
            };
            var result = await ScrapeInternalAsync(config, fetcher, itchOnly, stats, writeOutput, steamTable)
                .ConfigureAwait(false);
            FinishRun(config, stats, result, writeOutput);
            return result;
        }
        finally
        {
            if (ownsFetcher) fetcher.Dispose();
        }
    }

    /// <summary>Core itch crawl: listing pages -> detail pages -> cleaning (+ optional write).</summary>
    private static async Task<ScrapeResult> ScrapeInternalAsync(
        ItchScraperConfig config,
        PoliteFetcher fetcher,
        IReadOnlyDictionary<string, RobotsPolicy> robotsByHost,
        RunStats stats,
        bool writeOutput,
        GameTable? steamTable = null)
    {
        var robots = SteamPipeline.PolicyFor("https://itch.io/", robotsByHost);

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
            ExcelWriter.WriteWorkbook(
                table, steamTable, workbookPath,
                sheetCombined: config.SheetCombined,
                sheetSteam: config.SheetSteam,
                sheetItch: config.SheetItch,
                stats: stats);
        }
        else if (writeOutput)
        {
            stats.Warning("nothing to write: the cleaned table is empty");
        }

        var summary = stats.Summary();
        summary["cache"] = stats.CacheStats();
        return new ScrapeResult(table, merged, stats, workbookPath, summary);
    }

    /// <summary>Persist the summary journal + final report line for a top-level run.</summary>
    private static void FinishRun(ItchScraperConfig config, RunStats stats, ScrapeResult result, bool writeOutput)
    {
        stats.Logger.Info($"run summary: {JsonLine.Serialize(result.Summary)}");
        AppendRunSummary(config, result.Summary);
        stats.Report();
    }

    // ------------------------------------------------------------------ //
    // crawl helpers                                                      //
    // ------------------------------------------------------------------ //
    /// <summary>Walk ?page=N browse pages until exhausted or MaxPages reached.</summary>
    private static async Task<List<Record>> CrawlListingAsync(
        PoliteFetcher fetcher, string startUrl, RobotsPolicy robots,
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
        PoliteFetcher fetcher, List<Record> listings, RobotsPolicy robots,
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
