using System.Text.RegularExpressions;

namespace UnifiedRgb.Core;

/// <summary>Taking the person out of a diagnostic bundle.
///
/// These get dragged into PUBLIC GitHub issues and pasted into forum threads,
/// so the bundle has to be safe to post by someone who will not read it first.
/// It lives in Core because there are two report paths, the app's Report a
/// problem and the standalone diagnostic exe, and only one of them used to
/// scrub anything.</summary>
public static class Redaction
{
    /// <summary>Windows account names are often a real first name, and a plain
    /// substring replace over the whole bundle is a trap: a user called Ian
    /// turns "Lian Li" into "L&lt;user&gt; Li", Cam rewrites "NZXT CAM", Ram eats
    /// "RAM RGB", Art eats "session start". Word boundaries only.</summary>
    static readonly RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>The tail of a PnP or HID instance id is the device's own serial
    /// (a Bluetooth MAC on some adapters). The VID and PID are what anyone
    /// diagnosing needs; the tail is not.</summary>
    static readonly Regex InstanceTail = new(
        @"(\b(?:USB|HID|BTHENUM|BTHLE)\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}(?:&\w+)*\\)\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The same tail in the device-PATH spelling
    /// (\?\hid#vid_1532&amp;pid_00cf&amp;mi_01#9&amp;2036339a&amp;0&amp;0001#{guid}) that
    /// HID paths and the bundled OpenRGB's log use; the backslash form above
    /// never matched it.</summary>
    static readonly Regex DevicePathTail = new(
        @"(\bhid#vid_[0-9a-f]{4}&pid_[0-9a-f]{4}(?:&\w+)*#)[^#{\s]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Instance-id and device-path serial tails, for the tests as much
    /// as for Scrub: the suite used to check a private copy of the pattern,
    /// which proved nothing about this one.</summary>
    internal static string RedactInstanceTails(string text)
        => DevicePathTail.Replace(InstanceTail.Replace(text, "$1<instance>"), "$1<instance>");

    /// <summary>Replace a person's or machine's name where it stands as its own
    /// word AND is not written as an acronym. Under three characters is left
    /// alone entirely: it would match far too much to be worth it, and the
    /// profile path replacement has already covered the paths.
    ///
    /// Word boundaries alone are not enough. They spare "Lian Li" from a user
    /// called Ian, but "CAM" and "RAM" are whole words, so a user called Cam
    /// still rewrote "NZXT CAM is running" and one called Ram ate the RAM
    /// section heading. An all-caps hit, when the name itself is not all-caps,
    /// is an acronym rather than them. Someone whose account really is "CAM"
    /// still gets redacted, because then the casing agrees.</summary>
    internal static bool ReplaceName(ref string text, string name, string replacement)
    {
        if (name.Length < 3)
        {
            // Too short for a bare-word match, but the exact quoted or
            // path-delimited forms are unambiguous - "account 'Ed'" is the
            // Wallpaper Engine line, "\Ed\" a path the profile pass missed.
            if (name.Length == 0) return false;
            var strict = new Regex($@"(?<=['""\\]){Regex.Escape(name)}(?=['""\\])", Opts);
            if (!strict.IsMatch(text)) return false;
            text = strict.Replace(text, replacement);
            return true;
        }
        bool nameIsUpper = name.ToUpperInvariant() == name;
        bool hit = false;

        text = new Regex($@"\b{Regex.Escape(name)}\b", Opts).Replace(text, m =>
        {
            if (!nameIsUpper && m.Value.ToUpperInvariant() == m.Value) return m.Value;   // an acronym
            hit = true;
            return replacement;
        });
        return hit;
    }

    /// <summary>Scrub a bundle. Returns the text with a note at the top saying
    /// what was taken out, so a maintainer reading it knows the gaps are
    /// deliberate rather than a device that failed to report.</summary>
    public static string Scrub(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var removed = new List<string>();

        // The profile path first: it contains the account name, and replacing
        // it whole is exact where a bare name match would not be.
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 0 && text.Contains(profile, StringComparison.OrdinalIgnoreCase))
        {
            text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            removed.Add("profile path");
        }

        if (ReplaceName(ref text, Environment.UserName, "<user>")) removed.Add("account name");

        string machine = Environment.MachineName;
        if (!machine.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase)
            && ReplaceName(ref text, machine, "<pc>")) removed.Add("computer name");

        if (InstanceTail.IsMatch(text) || DevicePathTail.IsMatch(text))
        {
            text = RedactInstanceTails(text);
            removed.Add("device serial numbers");
        }

        if (removed.Count == 0) return text;
        return $"[redacted before saving: {string.Join(", ", removed)}]"
             + Environment.NewLine + Environment.NewLine + text;
    }
}
