// SHARED FETCH LAYER: rate limiting, retries and caching in one polite client.
//
// Mirrors the README boxes "Steam Fetcher" / "Itch Fetcher" + "Rate Limiter
// (1-2 sec crawl delay)". One PoliteFetcher instance is shared by BOTH source
// crawlers inside a run, so the politeness gap applies across every outbound
// request and can never be forgotten by accident.
//
//   var fetcher = new PoliteFetcher(config, stats);
//   var html = await fetcher.GetAsync("https://itch.io/games/tag-horror"); // null on failure

using System.Diagnostics;
using AngleSharp;
using AngleSharp.Dom;

namespace ItchScraper;

/// <summary>Enforces a minimum, randomly-jittered gap between two requests.</summary>
public sealed class RateLimiter
{
    public double MinDelay { get; }
    public double MaxDelay { get; }

    private readonly Func<double, Task> _sleep;
    private readonly Func<double, double, double> _rng;
    private double _nextAllowedAt;

    public double WaitedSeconds { get; private set; }
    public int Waits { get; private set; }

    /// <param name="minDelay">Lower bound of the sleep window, in seconds.</param>
    /// <param name="maxDelay">Upper bound of the sleep window, in seconds.</param>
    /// <param name="sleeper">Injectable async sleep function (patched out in tests).</param>
    /// <param name="rng">Injectable uniform-random source (patched out in tests).</param>
    public RateLimiter(
        double minDelay = 1.0,
        double maxDelay = 2.0,
        Func<double, Task>? sleeper = null,
        Func<double, double, double>? rng = null)
    {
        if (maxDelay < minDelay) (minDelay, maxDelay) = (maxDelay, minDelay);
        MinDelay = minDelay;
        MaxDelay = maxDelay;
        _sleep = sleeper ?? (seconds => Task.Delay(TimeSpan.FromSeconds(seconds)));
        _rng = rng ?? ((lo, hi) => lo + Random.Shared.NextDouble() * (hi - lo));
    }

    /// <summary>Sleep until the polite window opens; returns seconds slept.</summary>
    public async Task<double> DelayForNextRequestAsync()
    {
        var now = MonoNow();
        var wait = Math.Max(0.0, _nextAllowedAt - now);
        if (wait <= 0)
        {
            // Fresh gap: draw the jitter for the *following* request.
            wait = _rng(MinDelay, MaxDelay);
        }
        await _sleep(wait).ConfigureAwait(false);
        Waits++;
        WaitedSeconds += wait;
        _nextAllowedAt = MonoNow() + _rng(MinDelay, MaxDelay);
        return wait;
    }

    /// <summary>Sleep base^attempt seconds (capped, with full jitter) after a failed try.</summary>
    public async Task<double> BackoffSleepAsync(int attempt, double base_, double cap)
    {
        var raw = Math.Min(cap, Math.Pow(base_, Math.Max(1, attempt)));
        // Full jitter avoids synchronised retry storms.
        var delay = raw > 0 ? _rng(0.0, raw) : 0.0;
        await _sleep(delay).ConfigureAwait(false);
        WaitedSeconds += delay;
        return delay;
    }

    private static double MonoNow() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private sealed class Dummy;
}

/// <summary>Polite HTTP GET client for itch.io with cache + retry + logging.</summary>
public sealed class PoliteFetcher : IDisposable
{
    public ItchScraperConfig Config { get; }
    public RunStats Stats { get; }
    public string UserAgent { get; }
    public HttpClient Http { get; }
    public ResponseCache Cache { get; }
    public RateLimiter Limiter { get; }

    /// <param name="config">Run configuration.</param>
    /// <param name="stats">Run-stats object used for counters and failure records.</param>
    /// <param name="http">Optional pre-configured <see cref="HttpClient"/> (tests inject a stub here).</param>
    /// <param name="cache">Optional response cache; built from config when omitted.</param>
    /// <param name="limiter">Optional rate limiter; built from config otherwise.</param>
    public PoliteFetcher(
        ItchScraperConfig config,
        RunStats stats,
        HttpClient? http = null,
        ResponseCache? cache = null,
        RateLimiter? limiter = null)
    {
        Config = config;
        Stats = stats;
        UserAgent = config.UserAgent;
        Http = http ?? BuildClient(config);
        Cache = cache ?? new ResponseCache(config.CacheDir, ttl: config.CacheTtl);
        Limiter = limiter ?? new RateLimiter(config.MinDelay, config.MaxDelay);
        Stats.AttachCache(Cache);
    }

    /// <summary>Create a client with sane headers and no redirect ping-ponging.</summary>
    private static HttpClient BuildClient(ItchScraperConfig config)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = true, UseCookies = false };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(config.RequestTimeoutSeconds),
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", config.UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        return client;
    }

    // ------------------------------------------------------------------ //
    // main entry point                                                   //
    // ------------------------------------------------------------------ //
    /// <summary>Fetch <paramref name="url"/> politely and return its body, or null on failure.</summary>
    ///
    /// Order of operations: normalize URL -> consult cache -> rate-limit ->
    /// request (with exponential-backoff retries) -> cache the good response.
    ///
    /// <param name="url">Absolute URL, or relative to https://itch.io/.</param>
    /// <param name="context">Extra fields recorded alongside failures (page number, ...).</param>
    /// <returns>The decoded response text, or null when every attempt failed or
    /// the server answered with a permanent error (4xx other than 429).</returns>
    public async Task<string?> GetAsync(string url, IDictionary<string, object?>? context = null)
    {
        // Responses are cached per host so the two sources never collide.
        var target = Absolute(url);
        Cache.Scope = ScopeFor(target);

        var cached = Cache.Get(target);
        if (cached is not null)
        {
            Stats.CacheHits++;
            Stats.Requests++;
            Stats.Info($"cache hit  {target} ({cached.Age():F0} s old)");
            return cached.Text;
        }

        int? lastStatus = null;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= Config.MaxRetries; attempt++)
        {
            if (attempt > 1)
            {
                Stats.Retries++;
                var waited = await Limiter.BackoffSleepAsync(
                    attempt - 1, Config.BackoffBase, Config.BackoffCap).ConfigureAwait(false);
                Stats.Info($"retry {attempt}/{Config.MaxRetries} for {target} after {waited:F1}s backoff");
            }

            await Limiter.DelayForNextRequestAsync().ConfigureAwait(false);
            Stats.Requests++;

            HttpResponseMessage response;
            try
            {
                response = await Http.GetAsync(target).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                lastError = e;
                lastStatus = null;
                Stats.Warning($"request error for {target}: {e.Message}");
                continue;
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                lastStatus = status;
                lastError = null;

                if (response.IsSuccessStatusCode)
                {
                    var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    try
                    {
                        Cache.Put(target, text, status);
                    }
                    catch (CacheWriteException e)
                    {
                        Stats.Warning($"cache write failed for {target}: {e.Message}");
                    }
                    return text;
                }

                if (status == 404)
                {
                    // Removed / renamed game page: permanent, do not retry.
                    Stats.RecordFailure(target, "http_404", status, attempt, context: context);
                    Stats.Info($"404 (gone) {target}");
                    return null;
                }

                if (Config.RetryStatuses.Contains(status))
                {
                    Stats.Warning($"transient HTTP {status} for {target}");
                    continue;
                }

                // Anything else (401/403/410/5xx-not-listed...) is treated as final.
                Stats.RecordFailure(target, $"http_{status}", status, attempt, context: context);
                Stats.Warning($"giving up on {target} (HTTP {status})");
                return null;
            }
        }

        Stats.RecordFailure(
            target,
            lastError is null ? "exhausted_retries" : "error_after_retries",
            lastStatus, Config.MaxRetries, lastError, context);
        Stats.Warning($"exhausted {Config.MaxRetries} attempts for {target}");
        return null;
    }

    // ------------------------------------------------------------------ //
    // helpers                                                            //
    // ------------------------------------------------------------------ //
    /// <summary>Cache scope (sub-directory name) for a URL's host.</summary>
    public static string ScopeFor(string url)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "other";
        var safe = new string(host.Where(ch => char.IsLetterOrDigit(ch) || ch == '-').ToArray());
        return safe.Length > 0 ? safe : "other";
    }

    /// <summary>Resolve <paramref name="url"/> against <paramref name="baseUrl"/> so
    /// relative links (and cache keys) stay consistent regardless of the source site.</summary>
    public static string Absolute(string url, string baseUrl = "https://itch.io/")
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)) return url;
        if (Uri.TryCreate(baseUri, url, out var resolved)) return resolved.ToString();
        return url;
    }

    /// <summary>Return the ?page=N link itch.io puts at the bottom of a listing.</summary>
    /// <param name="html">The listing page body.</param>
    /// <param name="current">URL of the page we just parsed (used to avoid loops).</param>
    /// <returns>The absolute next-page URL, or null on the last page.</returns>
    public string? NextPageUrl(string html, string current)
    {
        var document = Parse(html);
        foreach (var anchor in document.QuerySelectorAll("a[href*='page=']"))
        {
            var label = (anchor.TextContent ?? string.Empty).Trim().ToLowerInvariant();
            var href = anchor.GetAttribute("href") ?? string.Empty;
            if (label.Contains("next") || label.Contains("›") || label.Contains("→"))
            {
                var candidate = Absolute(href);
                if (candidate != current) return candidate;
            }
        }
        return null;
    }

    /// <summary>Parse an HTML body into an AngleSharp document (shared helper).</summary>
    public static IDocument Parse(string html)
    {
        var context = BrowsingContext.New(Configuration.Default);
        return context.OpenAsync(req => req.Content(html ?? string.Empty)).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        // Only dispose the client we own; injected clients belong to the caller.
        if (!ReferenceEquals(Http, _injectedPlaceholder)) Http.Dispose();
    }

    private static readonly HttpClient _injectedPlaceholder = new();
}
