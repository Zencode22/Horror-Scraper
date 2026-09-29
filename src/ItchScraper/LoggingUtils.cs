// Log / error handler for the itch.io scraper (README: "Log / Error Handler").
//
// Three sinks, all cheap to reason about:
//
//   logs/itch_scraper.log        - rolling text log (INFO level by default)
//   logs/failed_requests.jsonl   - one JSON object per request that never
//                                  succeeded, so a human can re-check it later
//   console                      - only when the CLI asks for verbosity

using System.Globalization;

namespace ItchScraper;

/// <summary>Minimal leveled logger with size-based file rotation.</summary>
public sealed class ScraperLogger : IDisposable
{
    private static readonly object Sync = new();
    private StreamWriter? _file;
    private string _logPath = string.Empty;
    private long _bytes;
    private const long MaxBytes = 2_000_000;
    private const int BackupCount = 3;

    public bool Verbose { get; set; }

    /// <summary>Attach the rotating file sink (idempotent).</summary>
    public void Configure(string logDir)
    {
        lock (Sync)
        {
            if (_file is not null) return;
            Directory.CreateDirectory(logDir);
            _logPath = Path.Combine(logDir, "itch_scraper.log");
            _file = new StreamWriter(_logPath, append: true) { AutoFlush = true };
            try { _bytes = new FileInfo(_logPath).Length; } catch { _bytes = 0; }
        }
    }

    public void Info(string message) => Write("INFO   ", message);

    public void Warning(string message) => Write("WARNING", message);

    public void Error(string message) => Write("ERROR  ", message);

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} | {level} | itch_scraper | {message}";
        lock (Sync)
        {
            if (_file is not null)
            {
                _file.WriteLine(line);
                _bytes += System.Text.Encoding.UTF8.GetByteCount(line) + 1;
                if (_bytes > MaxBytes) Rotate();
            }
            if (Verbose) Console.Error.WriteLine(line);
        }
    }

    private void Rotate()
    {
        // itch_scraper.log -> .1 -> .2 -> .3 (oldest dropped), like RotatingFileHandler.
        _file?.Flush();
        _file?.Dispose();
        try
        {
            for (var i = BackupCount - 1; i >= 1; i--)
            {
                var src = $"{_logPath}.{i}";
                if (File.Exists(src)) File.Move(src, $"{_logPath}.{i + 1}", overwrite: i + 1 <= BackupCount);
            }
            if (File.Exists(_logPath)) File.Move(_logPath, $"{_logPath}.1", overwrite: true);
        }
        catch (IOException) { /* rotation must never take the crawl down */ }

        _file = new StreamWriter(_logPath, append: true) { AutoFlush = true };
        _bytes = 0;
    }

    public void Dispose()
    {
        lock (Sync)
        {
            _file?.Dispose();
            _file = null;
        }
    }
}

/// <summary>Mutable counters + failure journal for a single scraping run.</summary>
public sealed class RunStats
{
    private const string FailedFilename = "failed_requests.jsonl";

    public ItchScraperConfig Config { get; }
    public ScraperLogger Logger { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public ResponseCache? Cache { get; private set; }

    /// <summary>Number of HTTP responses consumed (cache hits included).</summary>
    public int Requests { get; set; }
    /// <summary>Subset of Requests served from the local cache.</summary>
    public int CacheHits { get; set; }
    /// <summary>Number of extra attempts triggered by transient failures.</summary>
    public int Retries { get; set; }
    /// <summary>Browse-listing rows parsed.</summary>
    public int Listings { get; set; }
    /// <summary>Game detail pages parsed.</summary>
    public int Details { get; set; }
    /// <summary>Recorded RecordFailure payloads.</summary>
    public int Failures { get; private set; }

    public string FailedPath => Path.Combine(Config.LogDir, FailedFilename);

    public RunStats(ItchScraperConfig config, ScraperLogger logger)
    {
        Config = config;
        Logger = logger;
    }

    // ------------------------------------------------------------------ //
    // logging helpers                                                    //
    // ------------------------------------------------------------------ //
    public void Info(string message) => Logger.Info(message);

    public void Warning(string message) => Logger.Warning(message);

    /// <summary>Append a failed-request entry to logs/failed_requests.jsonl.</summary>
    /// <param name="url">The request URL that could not be completed.</param>
    /// <param name="reason">Short machine-friendly cause ("http_429", "timeout", ...).</param>
    /// <param name="statusCode">Final HTTP status, when a response was received.</param>
    /// <param name="attempts">How many tries were burned before giving up.</param>
    /// <param name="error">The exception that ended the retries, if any.</param>
    /// <param name="context">Extra fields worth keeping (game title, page number...).</param>
    public void RecordFailure(string url, string reason, int? statusCode = null,
                              int attempts = 0, Exception? error = null,
                              IDictionary<string, object?>? context = null)
    {
        Failures++;
        var payload = new Dictionary<string, object?>
        {
            ["timestamp"] = DateTimeOffset.UtcNow.ToString(ObservationFormat),
            ["url"] = url,
            ["reason"] = reason,
            ["status_code"] = statusCode,
            ["attempts"] = attempts,
            ["error"] = error is null ? null : $"{error.GetType().Name}: {error.Message}",
            ["context"] = context ?? new Dictionary<string, object?>(),
        };
        try
        {
            Directory.CreateDirectory(Config.LogDir);
            File.AppendAllText(FailedPath, JsonLine.Serialize(payload) + Environment.NewLine);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Logger.Error($"Could not persist failure record: {e.Message}");
        }
    }

    private static readonly string ObservationFormat = "o";

    // ------------------------------------------------------------------ //
    // reporting                                                          //
    // ------------------------------------------------------------------ //
    /// <summary>Return the run counters as a plain dictionary (handy for logs/tests).</summary>
    public Dictionary<string, object?> Summary()
    {
        var duration = (DateTimeOffset.UtcNow - StartedAt).TotalSeconds;
        return new Dictionary<string, object?>
        {
            ["duration_seconds"] = Math.Round(duration, 2, MidpointRounding.AwayFromZero),
            ["requests"] = Requests,
            ["cache_hits"] = CacheHits,
            ["retries"] = Retries,
            ["listings_parsed"] = Listings,
            ["details_parsed"] = Details,
            ["failures"] = Failures,
            ["failed_requests_log"] = FailedPath,
        };
    }

    /// <summary>Log the run summary at INFO level.</summary>
    public void Report()
    {
        var data = Summary();
        Logger.Info(
            $"Run finished in {data["duration_seconds"]} s | {Requests} requests " +
            $"({CacheHits} cached, {Retries} retries) | " +
            $"{Listings} listing rows, {Details} detail pages | " +
            $"{Failures} failures -> {FailedPath}");
    }

    /// <summary>Start a fresh failure journal for this run (keeps history out).</summary>
    public void ResetFailedLog()
    {
        try { if (File.Exists(FailedPath)) File.Delete(FailedPath); }
        catch (IOException) { /* non-fatal */ }
    }

    /// <summary>Remember the response cache so CacheStats can report it.</summary>
    public void AttachCache(ResponseCache cache) => Cache = cache;

    /// <summary>Cache utilisation snapshot (empty when no cache was attached).</summary>
    public Dictionary<string, object?> CacheStats() =>
        Cache?.Stats() ?? new Dictionary<string, object?>();
}

internal static class JsonLine
{
    public static string Serialize(object value) =>
        System.Text.Json.JsonSerializer.Serialize(value, new System.Text.Json.JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
}
