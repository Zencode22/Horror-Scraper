// CLI entry point - equivalent of `python -m itch_scraper.main`.
//
//   dotnet run --project src/ItchScraper                 # polite crawl -> output/workbook.xlsx
//   ITCH_MAX_PAGES=1 ITCH_FETCH_DETAILS=0 dotnet run     # quick smoke run
//   dotnet run -- --verbose                              # mirror log lines on stderr

using ItchScraper;

var verbose = args.Contains("--verbose") || args.Contains("-v");

Console.Error.WriteLine("itch.io horror scraper (C#) - respecting 1-2 s crawl delay + retries");

using var logger = new ScraperLogger { Verbose = verbose };
var config = new ItchScraperConfig();
logger.Configure(config.LogDir);

try
{
    var result = await Pipeline.ScrapeAsync(config, writeOutput: true, logger: logger);

    Console.Error.WriteLine($"rows written : {result.Table.Count}");
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
