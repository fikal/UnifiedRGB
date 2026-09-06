namespace UnifiedRgb.Core;

/// <summary>The one line the LCD's now-playing element shows.
///
/// Separate from the media plumbing so the wording is testable without a
/// media session, and capped in length here rather than at the typesetter:
/// a podcast episode title can run to hundreds of characters, and building a
/// glyph run for all of it every second to then throw most of it away is
/// work the panel does not need to do.</summary>
public static class NowPlayingText
{
    /// <summary>Between artist and title. A middle dot, not a dash: track and
    /// band names contain dashes often enough that one more would read as part
    /// of the name rather than as a separator.</summary>
    public const string Separator = " · ";

    /// <summary>Well past what fits on a 320px panel at any usable size, so it
    /// only ever catches the pathological case.</summary>
    public const int MaxChars = 80;

    /// <summary>"Artist - Title", or whichever half exists. Empty when neither
    /// does, which is what makes the element vanish rather than show a stray
    /// separator when a stream reports no metadata.</summary>
    public static string Compose(string? artist, string? title)
    {
        string a = (artist ?? "").Trim();
        string t = (title ?? "").Trim();
        if (a.Length == 0 && t.Length == 0) return "";
        if (a.Length == 0) return Ellipsize(t, MaxChars);
        if (t.Length == 0) return Ellipsize(a, MaxChars);
        return Ellipsize(a + Separator + t, MaxChars);
    }

    /// <summary>Cut to at most maxChars including the ellipsis, without leaving
    /// a space stranded in front of it.</summary>
    public static string Ellipsize(string s, int maxChars)
    {
        if (maxChars <= 0) return "";
        if (s.Length <= maxChars) return s;
        if (maxChars == 1) return "…";
        return string.Concat(s.AsSpan(0, maxChars - 1).TrimEnd(), "…");
    }
}
