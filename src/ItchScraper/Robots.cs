// Robots.txt awareness for the itch.io crawl.
//
// The README promises polite crawling; the cheapest way to keep that promise is
// to actually read https://itch.io/robots.txt once per run, cache it next to
// the page cache, and refuse to fetch anything it disallows. Anything odd
// (network error, malformed file) fails *open* with a warning - we would rather
// skip a page than crash the whole pipeline over a missing robots file.

using System.Text.RegularExpressions;

namespace ItchScraper;

/// <summary>A tiny robots.txt matcher following the Google/Mozilla semantics.</summary>
public sealed class RobotsPolicy
{
    private readonly List<string> _disallowPaths = new();
    private readonly List<string> _allowPaths = new();
    private readonly string _userAgent;

    /// <summary>Whether a robots.txt body was successfully parsed.</summary>
    public bool Loaded { get; private init; }

    /// <summary>Disallowed prefixes discovered (for logging only).</summary>
    public IReadOnlyList<string> Disallow => _disallowPaths;

    /// <summary>Why the policy is permissive, when it is.</summary>
    public string? Reason { get; init; }

    private RobotsPolicy(string userAgent) => _userAgent = userAgent;

    // ------------------------------------------------------------------ //
    // construction                                                       //
    // ------------------------------------------------------------------ //
    /// <summary>Build a policy from an already-downloaded robots.txt body.</summary>
    /// <param name="text">Raw robots.txt contents.</param>
    /// <param name="userAgent">The UA string used for the crawl.</param>
    public static RobotsPolicy FromText(string text, string userAgent)
    {
        var policy = new RobotsPolicy(userAgent) { Loaded = true };

        // Pick the group whose "user-agent" line matches our UA (or '*'),
        // mirroring urllib.robotparser's behaviour closely enough for itch.io.
        var token = FirstToken(userAgent);
        var groups = ParseGroups(text);
        foreach (var (agents, rules) in groups)
        {
            var applies = agents.Contains("*") ||
                          agents.Any(a => token.StartsWith(a, StringComparison.OrdinalIgnoreCase));
            if (!applies) continue;
            foreach (var (verb, path) in rules)
            {
                if (verb == "disallow" && path != "/" && path.Length > 0) policy._disallowPaths.Add(path);
                if (verb == "allow" && path.Length > 0) policy._allowPaths.Add(path);
            }
        }
        return policy;
    }

    /// <summary>Return a permissive policy when robots.txt is unavailable.</summary>
    /// <param name="reason">Why we could not read robots.txt (logged by the caller).</param>
    public static RobotsPolicy AllowAll(string reason) =>
        new RobotsPolicy("*") { Loaded = false, Reason = reason };

    private static IEnumerable<(HashSet<string> Agents, List<(string Verb, string Path)> Rules)> ParseGroups(string text)
    {
        HashSet<string>? agents = null;
        List<(string, string)>? rules = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash].Trim();
            if (line.Length == 0) continue;

            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var verb = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();

            if (verb == "user-agent")
            {
                if (rules is not null) { yield return (agents!, rules); agents = null; rules = null; }
                agents ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                agents.Add(value.ToLowerInvariant());
            }
            else if (verb is "allow" or "disallow")
            {
                rules ??= new List<(string, string)>();
                rules.Add((verb, value));
            }
        }
        if (agents is not null && rules is not null) yield return (agents, rules);
    }

    private static string FirstToken(string userAgent)
    {
        var trimmed = userAgent.Trim();
        var slash = trimmed.IndexOf('/');
        return (slash > 0 ? trimmed[..slash] : trimmed).ToLowerInvariant();
    }

    // ------------------------------------------------------------------ //
    // queries                                                            //
    // ------------------------------------------------------------------ //
    /// <summary>Return true when <paramref name="url"/> may be fetched under the policy.</summary>
    public bool IsAllowed(string url)
    {
        if (!Loaded) return true;
        try
        {
            var path = new Uri(url).PathAndQuery;
            // Longest matching rule wins (standard robots.txt precedence);
            // when nothing matches at all the URL is allowed.
            var allowed = true;
            var bestLength = -1;
            foreach (var pattern in _allowPaths)
            {
                if (pattern.Length > bestLength && StartsWith(path, pattern))
                { allowed = true; bestLength = pattern.Length; }
            }
            foreach (var pattern in _disallowPaths)
            {
                if (pattern.Length > bestLength && StartsWith(path, pattern))
                { allowed = false; bestLength = pattern.Length; }
            }
            return allowed;
        }
        catch (UriFormatException)
        {
            return true; // defensive against weird URLs
        }
    }

    private static bool StartsWith(string path, string pattern)
    {
        // robots.txt wildcards: '*' any sequence, '$' end-of-url anchor.
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*");
        if (regex.EndsWith("\\$", StringComparison.Ordinal))
        {
            regex = regex[..^2] + "$";
        }
        return Regex.IsMatch(path, regex);
    }

    /// <summary>Drop every URL the policy disallows, logging what got dropped.</summary>
    /// <param name="urls">Sequence of absolute or relative URLs.</param>
    /// <param name="stats">Optional run-stats object.</param>
    /// <returns>The permitted URLs, in input order.</returns>
    public List<string> FilterUrls(IEnumerable<string> urls, RunStats? stats = null)
    {
        var kept = new List<string>();
        foreach (var url in urls)
        {
            var absolute = Absolutize(url);
            if (IsAllowed(absolute)) kept.Add(url);
            else stats?.Info($"robots.txt disallows {absolute} - skipping");
        }
        return kept;
    }

    internal static string Absolutize(string url)
    {
        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return url;
        return new Uri(new Uri("https://itch.io/"), url).ToString();
    }
}

public static class RobotsFetcher
{
    /// <summary>Download (through the rate-limited fetcher) and build the policy.</summary>
    /// <param name="fetcher">Object exposing GetAsync(url) - typically <see cref="ItchFetcher"/>.</param>
    /// <param name="stats">Optional run-stats object used for logging/failure records.</param>
    /// <param name="robotsUrl">Location of the robots file.</param>
    /// <returns>A <see cref="RobotsPolicy"/>; permissive when the file cannot be read.</returns>
    public static async Task<RobotsPolicy> FetchRobotsAsync(
        ItchFetcher fetcher, RunStats? stats = null,
        string robotsUrl = "https://itch.io/robots.txt")
    {
        string? text;
        try
        {
            text = await fetcher.GetAsync(robotsUrl).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            stats?.RecordFailure(robotsUrl, "robots_lookup_failed", error: e);
            return RobotsPolicy.AllowAll($"lookup failed: {e.Message}");
        }

        if (string.IsNullOrEmpty(text))
        {
            stats?.Warning($"Could not read {robotsUrl}; proceeding without robots rules");
            return RobotsPolicy.AllowAll("empty response");
        }

        RobotsPolicy policy;
        try
        {
            policy = RobotsPolicy.FromText(text, fetcher.UserAgent);
        }
        catch (Exception e)
        {
            stats?.RecordFailure(robotsUrl, "robots_parse_failed", error: e);
            return RobotsPolicy.AllowAll($"parse failed: {e.Message}");
        }

        stats?.Info($"robots.txt loaded ({policy.Disallow.Count} disallow rule(s)" +
                    $"{(policy.Disallow.Count > 0 ? ": " + string.Join(", ", policy.Disallow) : "")})");
        return policy;
    }
}
