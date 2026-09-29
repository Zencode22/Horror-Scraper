// Central configuration for the Itch.io scraper.
//
// Every knob that the README mentions (crawl delay, retries, cache location,
// output workbook name, ...) lives here so the rest of the codebase stays free
// of hard-coded magic numbers. Values can be overridden with environment
// variables so a run can be tuned without editing code:
//
//     ITCH_MAX_PAGES=3 ITCH_FETCH_DETAILS=0 dotnet run

using System.Globalization;

namespace ItchScraper;

/// <summary>Runtime configuration for the itch.io branch of the pipeline.</summary>
public sealed record ItchScraperConfig
{
    /// <summary>Project root = two levels above the assembly directory by convention.</summary>
    public static string ProjectRoot { get; } = FindProjectRoot();

    // ------------------------------------------------------------------ //
    // DATA SOURCE                                                        //
    // ------------------------------------------------------------------ //
    /// <summary>Genre/tag browse pages to crawl. itch.io throttles aggressively,
    /// so keep the list short and let <see cref="MaxPages"/> do the rest.</summary>
    public IReadOnlyList<string> SourceUrls { get; init; } = new[]
    {
        "https://itch.io/games/tag-horror",
        "https://itch.io/games/newest/tag-horror",
    };

    // ------------------------------------------------------------------ //
    // FETCH LAYER                                                        //
    // ------------------------------------------------------------------ //
    public string UserAgent { get; init; } =
        "HorrorGameDataScraper/0.1 (+collaborative research project; respectful of robots.txt)";

    public double RequestTimeoutSeconds { get; init; } = 20.0;

    /// <summary>Politeness delay applied *before every* request. The README asks
    /// for a 1-2 second crawl delay, so we draw uniformly from that range.</summary>
    public double MinDelay { get; init; } = EnvFloat("ITCH_MIN_DELAY", 1.0);

    public double MaxDelay { get; init; } = EnvFloat("ITCH_MAX_DELAY", 2.0);

    /// <summary>Retry policy (exponential backoff, per README "Scraping Etiquette").</summary>
    public int MaxRetries { get; init; } = EnvInt("ITCH_MAX_RETRIES", 4);

    public double BackoffBase { get; init; } = EnvFloat("ITCH_BACKOFF_BASE", 2.0);

    public double BackoffCap { get; init; } = EnvFloat("ITCH_BACKOFF_CAP", 60.0);

    /// <summary>HTTP statuses treated as transient and therefore retried.</summary>
    public IReadOnlySet<int> RetryStatuses { get; init; } =
        new HashSet<int> { 408, 425, 429, 500, 502, 503, 504 };

    // ------------------------------------------------------------------ //
    // CRAWL DEPTH                                                        //
    // ------------------------------------------------------------------ //
    /// <summary>Maximum browse-listing pages fetched per source URL.</summary>
    public int MaxPages { get; init; } = EnvInt("ITCH_MAX_PAGES", 3);

    /// <summary>Also visit each game page to recover the upload/release date.
    /// This multiplies the request count, so it is opt-out via ITCH_FETCH_DETAILS.</summary>
    public bool FetchDetails { get; init; } = EnvBool("ITCH_FETCH_DETAILS", true);

    /// <summary>Hard cap on detail requests per run (safety valve).</summary>
    public int MaxDetailRequests { get; init; } = EnvInt("ITCH_MAX_DETAIL_REQUESTS", 120);

    // ------------------------------------------------------------------ //
    // CACHE / LOGS / OUTPUT                                              //
    // ------------------------------------------------------------------ //
    public string CacheDir { get; init; } =
        Environment.GetEnvironmentVariable("ITCH_CACHE_DIR") ?? Path.Combine(ProjectRoot, "cache", "itch");

    public string LogDir { get; init; } =
        Environment.GetEnvironmentVariable("ITCH_LOG_DIR") ?? Path.Combine(ProjectRoot, "logs");

    public string OutputDir { get; init; } =
        Environment.GetEnvironmentVariable("ITCH_OUTPUT_DIR") ?? Path.Combine(ProjectRoot, "output");

    public string WorkbookName { get; init; } =
        Environment.GetEnvironmentVariable("ITCH_WORKBOOK") ?? "workbook.xlsx";

    /// <summary>Sheet names used inside the workbook (README: Combined / Steam / Itch).</summary>
    public string SheetCombined { get; init; } = "Combined";
    public string SheetSteam { get; init; } = "Steam";
    public string SheetItch { get; init; } = "Itch";

    /// <summary>How long cached responses stay valid (seconds). Default: 12 hours.</summary>
    public double CacheTtl { get; init; } = EnvFloat("ITCH_CACHE_TTL", 12 * 3600);

    /// <summary>Absolute path of the Excel workbook this config writes to.</summary>
    public string ResolvedWorkbookPath => Path.Combine(OutputDir, WorkbookName);

    // ------------------------------------------------------------------ //
    // env helpers                                                        //
    // ------------------------------------------------------------------ //
    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name)?.Trim(), NumberStyles.Integer,
                     CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static double EnvFloat(string name, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(name)?.Trim(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static bool EnvBool(string name, bool fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name)?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(raw)) return fallback;
        return raw is "1" or "true" or "yes" or "y" or "on";
    }

    private static string FindProjectRoot()
    {
        // Walk up from the assembly location looking for the README marker so
        // `dotnet run` from bin/Debug still lands artefacts in the repo root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "README.md")) &&
                Directory.Exists(Path.Combine(dir.FullName, "itch_scraper")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}
