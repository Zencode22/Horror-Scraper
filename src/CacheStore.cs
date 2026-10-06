// On-disk response cache shared by every fetcher in the program.
//
// README, "Scraping Etiquette": *cache raw responses locally so re-runs don't
// re-hit servers*. This module implements exactly that with a tiny
// content-addressed store, partitioned per host via <see cref="Scope"/> so
// Steam and itch.io entries live side by side without ever colliding:
//
//   cache/<scope>/<sha1(url)>.json   {"url", "saved_at", "status", "text"}
//   cache/<scope>/index.json         {sha1 -> {"url", "hits", "last_seen"}}
//
// The cache is deliberately dumb (no SQLite, no compression): HTML pages are a
// few hundred KB and a handful of files is easy to inspect by hand while
// debugging the parser.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ItchScraper;

/// <summary>A cached HTTP response.</summary>
public sealed record CacheEntry(string Url, string Text, int Status, double SavedAt)
{
    /// <summary>Seconds elapsed since the entry was written.</summary>
    public double Age(double? now = null) => Math.Max(0.0, (now ?? UnixNow()) - SavedAt);

    /// <summary>True when the entry is younger than <paramref name="ttl"/> seconds.</summary>
    public bool IsFresh(double ttl, double? now = null) => Age(now) <= ttl;

    internal static double UnixNow() =>
        (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 1000.0;
}

public sealed class CacheWriteException(string message, Exception inner) : Exception(message, inner);

/// <summary>Filesystem-backed GET cache with TTL support.</summary>
public sealed class ResponseCache
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Active partition (host name); set by the fetcher before each call.</summary>
    public string Scope { get; set; } = "default";

    public string CacheDir { get; }
    public double Ttl { get; set; }
    public bool Enabled { get; }
    public int Hits { get; private set; }
    public int Misses { get; private set; }

    /// <param name="cacheDir">Directory holding the entries (created on demand).</param>
    /// <param name="ttl">Entries older than this many seconds are ignored on read
    /// but kept on disk until <see cref="Purge"/> runs.</param>
    /// <param name="enabled">Set false to make every lookup a miss (useful in
    /// tests or with --no-cache).</param>
    public ResponseCache(string cacheDir, double ttl = 12 * 3600, bool enabled = true)
    {
        CacheDir = cacheDir;
        Ttl = ttl;
        Enabled = enabled;
    }

    private string RootFor(string scope) => Path.Combine(CacheDir, scope);

    private string IndexPathFor(string scope) => Path.Combine(RootFor(scope), "index.json");

    // ------------------------------------------------------------------ //
    // plumbing                                                           //
    // ------------------------------------------------------------------ //
    internal static string CacheKey(string url)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private string PathFor(string url) => Path.Combine(RootFor(Scope), $"{CacheKey(url)}.json");

    private void EnsureDir() => Directory.CreateDirectory(RootFor(Scope));

    private JsonObject LoadIndex()
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(IndexPathFor(Scope))) as JsonObject ?? new JsonObject();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new JsonObject();
        }
    }

    private void UpdateIndex(string url, string key, int status)
    {
        var index = LoadIndex();
        var record = index[key] as JsonObject ?? new JsonObject();
        record["url"] = url;
        record["status"] = status;
        record["last_seen"] = CacheEntry.UnixNow();
        record["hits"] = (record["hits"]?.GetValue<int>() ?? 0) + 1;
        index[key] = record;
        try
        {
            EnsureDir();
            File.WriteAllText(IndexPathFor(Scope), index.ToJsonString(Indented));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // index is best-effort bookkeeping - never fail over it.
        }
    }

    // ------------------------------------------------------------------ //
    // public API                                                         //
    // ------------------------------------------------------------------ //
    /// <summary>Return a fresh cached entry for <paramref name="url"/>, or null.
    /// Corrupt or unreadable files count as a miss rather than throwing.</summary>
    public CacheEntry? Get(string url)
    {
        if (!Enabled)
        {
            Misses++;
            return null;
        }

        CacheEntry? entry;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(PathFor(url)));
            var root = doc.RootElement;
            entry = new CacheEntry(
                Url: root.GetProperty("url").GetString() ?? url,
                Text: root.GetProperty("text").GetString() ?? string.Empty,
                Status: root.GetProperty("status").GetInt32(),
                SavedAt: root.GetProperty("saved_at").GetDouble());
        }
        catch (Exception e) when (e is IOException or JsonException or
                                 KeyNotFoundException or InvalidOperationException or
                                 UnauthorizedAccessException)
        {
            Misses++;
            return null;
        }

        if (!entry.IsFresh(Ttl))
        {
            Misses++;
            return null;
        }

        Hits++;
        UpdateIndex(url, Path.GetFileNameWithoutExtension(PathFor(url)), entry.Status);
        return entry;
    }

    /// <summary>Store <paramref name="text"/> for <paramref name="url"/>.</summary>
    public CacheEntry Put(string url, string text, int status)
    {
        var entry = new CacheEntry(url, text, status, CacheEntry.UnixNow());
        if (!Enabled) return entry;

        var path = PathFor(url);
        try
        {
            EnsureDir();
            var payload = new JsonObject
            {
                ["url"] = entry.Url,
                ["status"] = entry.Status,
                ["saved_at"] = entry.SavedAt,
                ["text"] = entry.Text,
            };
            File.WriteAllText(path, payload.ToJsonString(new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Keep going without caching; the caller only cares about the body.
            throw new CacheWriteException($"could not write cache entry for {url}", e);
        }

        UpdateIndex(url, Path.GetFileNameWithoutExtension(path), status);
        return entry;
    }

    /// <summary>Delete stale entries across every scope; returns files removed.</summary>
    public int Purge(double? olderThan = null)
    {
        var cutoff = olderThan ?? Ttl;
        var now = CacheEntry.UnixNow();
        var removed = 0;
        if (!Directory.Exists(CacheDir)) return removed;

        var paths = Directory
            .GetDirectories(CacheDir)                       // per-scope folders
            .SelectMany(d => Directory.GetFiles(d, "*.json"))
            .Concat(Directory.GetFiles(CacheDir, "*.json")) // legacy flat layout
            .OrderBy(p => p, StringComparer.Ordinal);

        foreach (var path in paths)
        {
            if (Path.GetFileName(path).Equals("index.json", StringComparison.OrdinalIgnoreCase)) continue;

            double savedAt;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                savedAt = doc.RootElement.TryGetProperty("saved_at", out var prop)
                    ? prop.GetDouble() : 0.0;
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidOperationException)
            {
                savedAt = 0.0;
            }

            if (now - savedAt > cutoff)
            {
                try
                {
                    File.Delete(path);
                    removed++;
                }
                catch (IOException) { /* best effort */ }
            }
        }
        return removed;
    }

    /// <summary>Snapshot of cache utilisation for run summaries.</summary>
    public Dictionary<string, object?> Stats()
    {
        var total = Directory.Exists(CacheDir)
            ? Directory.GetFiles(CacheDir, "*.json", SearchOption.AllDirectories).Length
            : 0;
        return new Dictionary<string, object?>
        {
            ["dir"] = CacheDir,
            ["entries"] = Math.Max(0, total - 1), // minus index.json
            ["hits"] = Hits,
            ["misses"] = Misses,
            ["enabled"] = Enabled,
        };
    }
}
