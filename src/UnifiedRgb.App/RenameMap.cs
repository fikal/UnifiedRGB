namespace UnifiedRgb.App;

/// <summary>The renames of one kind of named thing (profiles, screens, shows)
/// made this session, so a name captured BEFORE a rename can still be found
/// after it.
///
/// Live references are moved by the rename itself (rules, schedules, show
/// steps, profiles' screen and show). What this covers is the copies held
/// for later: an automation's return point, a paused show's playback, an
/// undo step, a request waiting for the pump panel to come back. Restored
/// after a rename, those named something that no longer existed: the profile
/// was deselected (and with it the close prompt for unsaved edits), the pump
/// or show pick pointed at nothing and the next Save wrote that, the show did
/// not resume, the canvas forgot which screen it was.</summary>
public sealed class RenameMap
{
    readonly Dictionary<string, string> _to = new(StringComparer.OrdinalIgnoreCase);

    public void Record(string from, string to)
    {
        // Whatever used to lead to the new name is moot: something is called
        // that now, and it is not a rename of anything.
        _to.Remove(to);
        if (!string.Equals(from, to, StringComparison.Ordinal)) _to[from] = to;
    }

    /// <summary>The current name for <paramref name="name"/>. A name that
    /// exists now is itself, whatever it was renamed from earlier: a new item
    /// can take an old name, and a live reference to it must not be sent to
    /// the item that used to carry it.</summary>
    public string? Follow(string? name, Func<string, bool> exists)
    {
        for (int hops = 0; name != null && hops < 32 && !exists(name); hops++)
        {
            if (!_to.TryGetValue(name, out var next) || string.Equals(next, name, StringComparison.Ordinal)) break;
            name = next;
        }
        return name;
    }
}
