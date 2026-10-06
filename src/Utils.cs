// Small, dependency-free helpers shared by the itch.io scraper.
//
// The two functions that matter for the README's *Normalizer* box live here:
//
//   NormalizePrice -> USD double (null when no numeric price is present)
//   ToIsoDate      -> "YYYY-MM-DD" string (null when unparseable)

using System.Globalization;
using System.Text.RegularExpressions;

namespace ItchScraper;

/// <summary>A scraped row carried as loosely-typed key/value data.</summary>
public sealed class Record : Dictionary<string, object?>
{
    public Record() : base(StringComparer.Ordinal) { }

    public Record(IDictionary<string, object?> other) : base(other, StringComparer.Ordinal) { }

    /// <summary>Return the value as a cleaned string ("" for missing values).</summary>
    public string Str(string key) => Utils.CleanText(Get(key));

    /// <summary>Return the raw value or null.</summary>
    public object? Get(string key) => TryGetValue(key, out var value) ? value : null;

    public bool Has(string key) => TryGetValue(key, out var v) && !IsEmpty(v);

    public void Set(string key, object? value) => this[key] = value;

    private static bool IsEmpty(object? value) => value switch
    {
        null => true,
        string s => string.IsNullOrWhiteSpace(s),
        IReadOnlyList<string> list => list.Count == 0,
        _ => false,
    };
}

public static class Utils
{
    // "$0", "US$7.99", "€ 4,50" ... - we only ever keep the numeric part and
    // treat it as USD (itch.io browse listings are shown in USD).
    private static readonly Regex PriceRegex = new(@"(?<value>\d+(?:[.,]\d{1,2})?)", RegexOptions.Compiled);

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private static readonly Regex TitleKeyRegex = new(@"[^a-z0-9]+", RegexOptions.Compiled);

    /// <summary>Collapse whitespace and strip invisible/curly characters.</summary>
    public static string CleanText(object? value)
    {
        if (value is null) return string.Empty;
        var text = Normalizer.Normalize(value.ToString() ?? string.Empty);
        return WhitespaceRegex.Replace(text, " ").Trim();
    }

    /// <summary>
    /// Turn a raw itch.io price label into a US-dollar amount.
    ///
    /// Handled shapes (all observed on itch.io/games/tag-horror):
    ///   "$7.99"                        -> 7.99
    ///   "$0 -100%" (on sale)           -> 0.0
    ///   "Pay $1 or more for this game" -> 1.0 (name-your-price minimum)
    ///   "" / null / "Free"             -> 0.0 (no tag == free on itch.io)
    ///   "$--" / garbage                -> null (dropped by the validator)
    /// </summary>
    public static double? NormalizePrice(object? value)
    {
        var text = CleanText(value);
        if (text.Length == 0)
        {
            // itch.io omits the price tag entirely for free games.
            return 0.0;
        }

        var lowered = text.ToLowerInvariant();
        if (lowered.Contains("--", StringComparison.Ordinal)) return null;

        var match = PriceRegex.Match(lowered.Replace(',', '.'));
        if (!match.Success) return null;

        if (!double.TryParse(match.Groups["value"].Value, NumberStyles.Float,
                             CultureInfo.InvariantCulture, out var amount))
        {
            return null;
        }

        // A "-100%" sale badge means the game is currently free; the leading
        // "$0"/"$x" already reflects that, so nothing else to do but round.
        return Math.Round(amount, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Normalize a date-ish string to ISO YYYY-MM-DD. Accepts the formats
    /// itch.io emits ("28 September 2026 @ 20:08 UTC" from the info-panel
    /// abbr title, ISO timestamps from JSON-LD, plain "2026-09-28") plus a
    /// couple of common variants. Returns null when the input cannot be parsed.
    /// </summary>
    public static string? ToIsoDate(object? value)
    {
        var text = CleanText(value);
        if (text.Length == 0) return null;

        var candidates = new List<string> { text };
        // Drop a trailing timezone word so no style requires it.
        candidates.Add(Regex.Replace(text, @"\s+(UTC|GMT)$", "", RegexOptions.IgnoreCase));
        // Also allow "2026-09-28T20:08:00Z" style values.
        candidates.Add(text.Replace("Z", "+0000", StringComparison.Ordinal));

        foreach (var candidate in candidates)
        {
            var iso = TryParseIso(candidate);
            if (iso is not null) return iso;
        }
        return null;
    }

    private static readonly string[] DateFormats =
    {
        "yyyy-MM-dd",
        "yyyy-MM-ddTHH:mm:ssK",
        "yyyy-MM-ddTHH:mm:sszzz",
        "yyyy-MM-dd HH:mm:ss",
        "dd/MM/yyyy",
        "MM/dd/yyyy",
    };

    private static readonly string[] MonthNameFormats =
    {
        "d MMMM yyyy 'at' HH:mm zzz",
        "d MMM yyyy 'at' HH:mm zzz",
        "d MMMM yyyy",
        "d MMM yyyy",
        "MMMM d, yyyy",
        "MMM d, yyyy",
    };

    private static string? TryParseIso(string text)
    {
        // itch.io writes "28 September 2026 @ 20:08 UTC"; the '@' is not a
        // literal allowed in .NET format strings, so swap it for 'at'.
        var normalized = text.Replace("@ ", "at ", StringComparison.Ordinal);

        foreach (var format in DateFormats)
        {
            if (DateTimeOffset.TryParseExact(normalized, format, CultureInfo.InvariantCulture,
                                             DateTimeStyles.None, out var dto))
            {
                return dto.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            if (DateTime.TryParseExact(normalized, format, CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out var dt))
            {
                return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        }

        foreach (var format in MonthNameFormats)
        {
            if (DateTime.TryParseExact(normalized, format, CultureInfo.InvariantCulture,
                                       DateTimeStyles.AllowWhiteSpaces, out var dt))
            {
                return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        }
        return null;
    }

    /// <summary>
    /// Lower-case, punctuation-free key used for deduplication.
    /// '"Voices Of The Void" Alpha' and '"Voices of the Void" alpha' both
    /// collapse to voices_of_the_void_alpha.
    /// </summary>
    public static string NormalizeTitle(object? title)
    {
        var text = CleanText(title).ToLowerInvariant().Replace("&", " and ");
        text = TitleKeyRegex.Replace(text, "_");
        return text.Trim('_');
    }

    /// <summary>Return the first value that is not empty after cleaning, else null.</summary>
    public static string? FirstNonEmpty(params object?[] values)
    {
        foreach (var value in values)
        {
            var text = CleanText(value);
            if (text.Length > 0) return text;
        }
        return null;
    }

    /// <summary>NFKC normalisation + general cleanup of curly quotes etc.</summary>
    private static class Normalizer
    {
        public static string Normalize(string text)
        {
            // Equivalent of Python unicodedata.normalize("NFKC", ...) for the
            // characters itch.io actually emits (curly quotes, nbsp, ...).
            var sb = new System.Text.StringBuilder(text.Length);
            foreach (var ch in text)
            {
                sb.Append(ch switch
                {
                    '\u00A0' => ' ',
                    '\u2018' or '\u2019' => '\'',
                    '\u201C' or '\u201D' => '"',
                    '\u2013' => '-',
                    '\u2014' => '-',
                    '\u2026' => '.',
                    _ => ch,
                });
            }
            return sb.ToString();
        }
    }
}
