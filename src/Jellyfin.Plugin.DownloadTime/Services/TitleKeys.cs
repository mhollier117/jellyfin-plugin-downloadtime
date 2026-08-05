namespace Jellyfin.Plugin.DownloadTime.Services;

/// <summary>
/// Title normalization shared by content matching (DiffEngine) and runtime
/// enrichment (RuntimeEnricher). Leading bracketed marker groups are stripped
/// ("[C] ", "[C/F] ", "[AC] [F] " — Anime Filler Marker prefixes, audit S-2);
/// interior brackets are part of the title. Everything else collapses to
/// lowercase alphanumerics so punctuation/spacing differences between sources
/// do not defeat a match.
/// </summary>
public static class TitleKeys
{
    public static string? Normalize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }
        var s = title.AsSpan().Trim();
        while (s.Length > 0 && s[0] == '[')
        {
            var close = s.IndexOf(']');
            if (close < 0)
            {
                break;
            }
            s = s[(close + 1)..].TrimStart();
        }
        var key = new string(s.ToArray().Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return key.Length == 0 ? null : key;
    }
}
