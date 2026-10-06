// CLI entry point for the combined Steam + itch.io horror scraper.
//
//   dotnet run --project src/ItchScraper                 # polite crawl -> output/workbook.xlsx
//   SCRAPER_MAX_PAGES=1 SCRAPER_FETCH_DETAILS=0 dotnet run   # quick smoke run
//   SCRAPER_FETCH_STEAM=0 dotnet run                     # itch only
//   dotnet run -- --verbose                              # mirror log lines on stderr

using ItchScraper;

var verbose = args.Contains("--verbose") || args.Contains("-v");

Console.Error.WriteLine("horror game scraper (C#) - Steam + itch.io, 1-2 s crawl delay + retries");

using var logger = new ScraperLogger { Verbose = verbose };
var config = new ItchScraperConfig();
logger.Configure(config.LogDir);

try
{
    // Combined pipeline: crawls every enabled source and writes the single
    // three-sheet workbook (Combined / Steam / Itch).
    var result = await Pipeline.RunAsync(config, writeOutput: true, logger: logger);

    Console.Error.WriteLine($"rows written : {result.Table.Count} " +
                            $"(steam={result.SteamTable?.Count ?? 0})");
    Console.Error.WriteLine($"workbook     : {result.Workbook ?? "(not written)"}");
    Console.Error.WriteLine($"requests     : {result.Stats.Requests} ({result.Stats.CacheHits} from cache)");
    Console.Error.WriteLine($"failures     : {result.Stats.Failures} -> {result.Stats.FailedPath}");

    foreach (var row in result.Table.Rows.Take(5))
    {
        Console.Error.WriteLine(
            $"  - {row.Str("title")} | {row.Str("platform")} | " +
            $"{row.Get("price_usd")} USD | {row.Str("release_date")} | {row.Str("developer")}");
    }
    if (result.Table.Count > 5)
    {
        Console.Error.WriteLine($"  ... {result.Table.Count - 5} more rows in the workbook");
    }

    return result.Table.Count > 0 ? 0 : 2;
}
catch (Exception e)
{
    logger.Error($"fatal: {e}");
    Console.Error.WriteLine($"fatal: {e.Message}");
    return 1;
}
