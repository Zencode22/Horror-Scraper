// Central configuration for the whole scraper program (Steam + itch.io).
//
// Every knob that the README mentions (crawl delay, retries, cache location,
// output workbook name, ...) lives here so the rest of the codebase stays free
// of hard-coded magic numbers. Values can be overridden with environment
// variables so a run can be tuned without editing code:
//
//     MAX_PAGES=2 FETCH_STEAM=1 dotnet run
//
// Legacy SCRAPER_* aliases are still honoured, and the older ITCH_*/STEAM_*
// spellings map onto the same knobs, so nothing breaks mid-refactor.

using System.Globalization;

namespace ItchScraper;

/// <summary>Runtime configuration shared by both source branches of the pipeline.</summary>
public sealed record ItchScraperConfig
{
    /// <summary>Project root = the directory holding README.md + src/.</summary>
    public static string ProjectRoot { get; } = FindProjectRoot();

    // ------------------------------------------------------------------ //
    // DATA SOURCES                                                       //
    // ------------------------------------------------------------------ //
    /// <summary>Which sources a default run crawls (CLI flags override this).</summary>
    public bool FetchSteam { get; init; } = EnvBool("SCRAPER_FETCH_STEAM", true);

    public bool FetchItch { get; init; } = EnvBool("SCRAPER_FETCH_ITCH", true);

    /// <summary>Genre/tag browse pages to crawl on itch.io. itch throttles
    /// aggressively, so keep the list short and let <see cref="MaxPages"/> do the rest.</summary>
    public IReadOnlyList<string> SourceUrls { get; init; } = new[]
    {
        "https://itch.io/games/tag-horror",
        "https://itch.io/games/newest/tag-horror",
    };

    /// <summary>Steam Store search pages crawled for the horror genre.
    /// `genre=Horror` is Steam's reliable horror filter on the modern search
    /// endpoint; `sortBy=Released` returns newest-first so the crawl always
    /// sees fresh releases. `count=75` maximises results per page.</summary>
    public IReadOnlyList<string> SteamSourceUrls { get; init; } = new[]
    {
        "https://store.steampowered.com/search/?genre=Horror&sortBy=Released&count=75",
    };

    // ------------------------------------------------------------------ //
    // FETCH LAYER                                                        //
    // ------------------------------------------------------------------ //
    public string UserAgent { get; init; } =
        "HorrorGameDataScraper/0.1 (+collaborative research project; respectful of robots.txt)";

    public double RequestTimeoutSeconds { get; init; } = 20.0;

    /// <summary>Politeness delay applied *before every* request. The README asks
    /// for a 1-2 second crawl delay, so we draw uniformly from that range.</summary>
    public double MinDelay { get; init; } = EnvFloat("SCRAPER_MIN_DELAY", 1.0);

    public double MaxDelay { get; init; } = EnvFloat("SCRAPER_MAX_DELAY", 2.0);

    /// <summary>Retry policy (exponential backoff, per README "Scraping Etiquette").</summary>
    public int MaxRetries { get; init; } = EnvInt("SCRAPER_MAX_RETRIES", 4);

    public double BackoffBase { get; init; } = EnvFloat("SCRAPER_BACKOFF_BASE", 2.0);

    public double BackoffCap { get; init; } = EnvFloat("SCRAPER_BACKOFF_CAP", 60.0);

    /// <summary>HTTP statuses treated as transient and therefore retried.</summary>
    public IReadOnlySet<int> RetryStatuses { get; init; } =
        new HashSet<int> { 408, 425, 429, 500, 502, 503, 504 };

    // ------------------------------------------------------------------ //
    // CRAWL DEPTH                                                        //
    // ------------------------------------------------------------------ //
    /// <summary>Maximum browse-listing pages fetched per source URL.</summary>
    public int MaxPages { get; init; } = EnvInt("SCRAPER_MAX_PAGES", 3);

    /// <summary>Also visit each game page to recover the upload/release date.
    /// This multiplies the request count, so it is opt-out via ITCH_FETCH_DETAILS.</summary>
    public bool FetchDetails { get; init; } = EnvBool("SCRAPER_FETCH_DETAILS", true);

    /// <summary>Hard cap on detail requests per run (safety valve).</summary>
    public int MaxDetailRequests { get; init; } = EnvInt("SCRAPER_MAX_DETAIL_REQUESTS", 120);

    /// <summary>Also fetch each Steam store app page to recover release date,
    /// developer and review counts (the README's Steam parser fields).</summary>
    public bool FetchSteamDetails { get; init; } = EnvBool("STEAM_FETCH_DETAILS", true);

    /// <summary>Hard cap on Steam app-page requests per run.</summary>
    public int MaxSteamDetailRequests { get; init; } = EnvInt("STEAM_MAX_DETAIL_REQUESTS", 150);

    // ------------------------------------------------------------------ //
    // CACHE / LOGS / OUTPUT                                              //
    // ------------------------------------------------------------------ //
    public string CacheDir { get; init; } =
        Environment.GetEnvironmentVariable("SCRAPER_CACHE_DIR") ?? Path.Combine(ProjectRoot, "cache");

    public string LogDir { get; init; } =
        Environment.GetEnvironmentVariable("SCRAPER_LOG_DIR") ?? Path.Combine(ProjectRoot, "logs");

    public string OutputDir { get; init; } =
        Environment.GetEnvironmentVariable("SCRAPER_OUTPUT_DIR") ?? Path.Combine(ProjectRoot, "output");

    public string WorkbookName { get; init; } =
        Environment.GetEnvironmentVariable("SCRAPER_WORKBOOK") ?? "workbook.xlsx";

    /// <summary>The three sheets of the single output workbook (README: Combined / Steam / Itch).</summary>
    public string SheetCombined { get; init; } = "Combined";
    public string SheetSteam { get; init; } = "Steam";
    public string SheetItch { get; init; } = "Itch";

    /// <summary>How long cached responses stay valid (seconds). Default: 12 hours.</summary>
    public double CacheTtl { get; init; } = EnvFloat("SCRAPER_CACHE_TTL", 12 * 3600);

    /// <summary>Absolute path of the Excel workbook this config writes to.</summary>
    public string ResolvedWorkbookPath => Path.Combine(OutputDir, WorkbookName);

    // ------------------------------------------------------------------ //
    // env helpers                                                        //
    // ------------------------------------------------------------------ //
    private static int EnvInt(string name, int fallback) =>
        int.TryParse(EnvAny(name)?.Trim(), NumberStyles.Integer,
                     CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static double EnvFloat(string name, double fallback) =>
        double.TryParse(EnvAny(name)?.Trim(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static bool EnvBool(string name, bool fallback)
    {
        var raw = EnvAny(name)?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(raw)) return fallback;
        return raw is "1" or "true" or "yes" or "y" or "on";
    }

    /// <summary>Read SCRAPER_X first, then the legacy ITCH_X spelling of the same knob.</summary>
    private static string? EnvAny(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value)) return value;
        if (name.StartsWith("SCRAPER_", StringComparison.Ordinal))
        {
            value = Environment.GetEnvironmentVariable("ITCH_" + name["SCRAPER_".Length..]);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static string FindProjectRoot()
    {
        // Walk up from the assembly location looking for the README marker so
        // `dotnet run` from bin/Debug still lands artefacts in the repo root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "README.md")) &&
                Directory.Exists(Path.Combine(dir.FullName, "src")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}