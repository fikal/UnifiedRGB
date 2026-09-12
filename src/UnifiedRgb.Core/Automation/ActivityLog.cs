namespace UnifiedRgb.Core.Automation;

/*-----------------------------------------------------------*\
| "Why did my lights just change?"                             |
|                                                              |
| The app decides the lighting from several sources that       |
| overlap constantly (a schedule, a foreground app rule, a     |
| sensor threshold, the session lock, an SDK client). Any one  |
| of them is understandable on its own; the combination is     |
| not, because the only thing the user ever sees is the single |
| status line, and that line is overwritten by the next        |
| decision two seconds later. The reasoning is thrown away the |
| instant it stops being true.                                 |
|                                                              |
| This is the short memory that fixes that: the last handful   |
| of decisions, each one a plain sentence a person can read.   |
| It is deliberately NOT the log file. The log is for a        |
| developer reading a support bundle and keeps everything;     |
| this is a panel in the UI, so it keeps only what would       |
| answer "why did that happen" and it is written in the same   |
| plain voice as the automation status copy.                   |
|                                                              |
| Written from the automation timer (UI thread), the OpenRGB   |
| socket threads and the sensor hub's own thread; read from    |
| the UI thread. So: thread safe, no allocation on the paths   |
| that run every tick (callers only Add on a real transition,  |
| and Add itself allocates nothing), and no writer is ever     |
| parked behind UI work - the Changed handlers run OUTSIDE     |
| the lock, and the lock itself only ever covers a few field   |
| writes.                                                      |
\*-----------------------------------------------------------*/

/// <summary>The category shown beside an entry. This is the "Game rule
/// activated" half of the line; the entry's own sentence carries the detail,
/// so the two never repeat each other.</summary>
public enum ActivityKind
{
    /// <summary>A rule started driving the lighting.</summary>
    RuleActivated,
    /// <summary>Two sources both wanted the lights and one outranked the
    /// other. Precedence is completely invisible today, which is exactly why
    /// a complex setup is hard to reason about.</summary>
    Precedence,
    /// <summary>No rule matches any more, back to the user's own lighting.</summary>
    BackToBase,
    /// <summary>The lights were deliberately turned off.</summary>
    LightsOff,
    /// <summary>An SDK client took or released a device.</summary>
    SdkClient,
    /// <summary>The thermal failsafe handed the fans back to the board.</summary>
    Failsafe,
    /// <summary>The user changed the lighting while a rule was driving it.</summary>
    UserOverride,
    /// <summary>Automation was paused or resumed.</summary>
    Paused,
    /// <summary>Something the user asked for did not fully work - a save that
    /// could not be written, a rename only half applied. These used to reach
    /// nothing but the log file, so "Save" appeared to do nothing at all and the
    /// only clue was in a file the user has no reason to open.</summary>
    Problem,
}

/// <summary>One line of history: when, what kind of thing happened, and one
/// sentence saying it. Repeats is 1 for a normal entry and counts up when the
/// identical entry happens again, so a flapping rule collapses to one line
/// instead of flushing the whole ring.</summary>
public readonly record struct ActivityEntry(DateTime At, ActivityKind Kind, string Text, int Repeats)
{
    /// <summary>The short category label, for the left column of the panel.</summary>
    public string Category => Kind switch
    {
        ActivityKind.RuleActivated => "Rule activated",
        ActivityKind.Precedence => "Priority",
        ActivityKind.BackToBase => "Back to your lighting",
        ActivityKind.LightsOff => "Lights off",
        ActivityKind.SdkClient => "SDK client",
        ActivityKind.Failsafe => "Failsafe",
        ActivityKind.UserOverride => "You took over",
        ActivityKind.Paused => "Automation",
        ActivityKind.Problem => "Did not work",
        _ => "",
    };

    /// <summary>What the panel actually shows: the sentence, with the repeat
    /// count appended only when there is one worth showing.</summary>
    public string Display => Repeats > 1 ? $"{Text} (x{Repeats})" : Text;

    /// <summary>Wall-clock time, no date: the whole ring covers minutes to
    /// hours, and a date on every row is noise.</summary>
    public string TimeText => At.ToString("HH:mm:ss");
}

/// <summary>The bounded ring of recent decisions. One shared instance for the
/// running app; the type is instantiable so tests can drive an isolated one
/// rather than fighting over global state.</summary>
public sealed class ActivityLog
{
    /// <summary>Small on purpose. This answers "why did my lights change",
    /// which is always a question about the last few minutes. An audit trail
    /// is what the log file is for, and a panel nobody scrolls past row 20 has
    /// no business holding thousands of rows alive.</summary>
    public const int Capacity = 200;

    /// <summary>The app's history. Static because the writers (a dispatcher
    /// timer, socket threads, the sensor hub) have no shared owner to be
    /// handed an instance by, and threading one through all of them would buy
    /// nothing: there is exactly one lighting stack per process.</summary>
    public static ActivityLog Shared { get; } = new();

    readonly object _gate = new();
    readonly ActivityEntry[] _ring = new ActivityEntry[Capacity];
    // Where the next entry goes, and how many slots are live. Oldest first in
    // ring order, so the oldest is _next once the ring has wrapped.
    int _next;
    int _count;

    /// <summary>Raised after an entry is added or an existing one is repeated.
    /// Raised on whichever thread wrote, so a UI subscriber must marshal.
    /// Deliberately fired outside the lock: a handler that repaints a panel
    /// must never be able to hold up the socket thread that wrote.</summary>
    public event Action? Changed;

    /// <summary>How many entries are currently held.</summary>
    public int Count { get { lock (_gate) return _count; } }

    /// <summary>Add one entry, or fold it into the newest one if it is
    /// identical. Allocates nothing: the entry is a struct and the caller owns
    /// the sentence, which is why callers must only call this on a genuine
    /// transition and never once per tick.</summary>
    public void Add(ActivityKind kind, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        lock (_gate)
        {
            // Deduplicate against the newest entry only. A rule that flaps
            // (a sensor sitting exactly on its threshold, an app that keeps
            // stealing focus) would otherwise push every other explanation out
            // of the ring, which is the one failure that makes this panel
            // useless precisely when it is needed.
            int newest = (_next - 1 + Capacity) % Capacity;
            if (_count > 0 && _ring[newest].Kind == kind
                && string.Equals(_ring[newest].Text, text, StringComparison.Ordinal))
            {
                // Move the timestamp forward: the interesting fact about a
                // repeat is when it last happened, not when it started.
                _ring[newest] = new ActivityEntry(DateTime.Now, kind, text, _ring[newest].Repeats + 1);
            }
            else
            {
                _ring[_next] = new ActivityEntry(DateTime.Now, kind, text, 1);
                _next = (_next + 1) % Capacity;
                if (_count < Capacity) _count++;   // past the cap the write above already dropped the oldest
            }
        }

        Changed?.Invoke();
    }

    /// <summary>The history, NEWEST FIRST, as a copy. Newest first because the
    /// panel is read from the top and the answer to "why did that just happen"
    /// is always the most recent entry. A copy because the ring is written
    /// from other threads and an enumerating UI would tear.</summary>
    public ActivityEntry[] Snapshot()
    {
        lock (_gate)
        {
            var outp = new ActivityEntry[_count];
            // _next - 1 is the newest; walk backwards, wrapping.
            int i = _next - 1;
            for (int n = 0; n < _count; n++)
            {
                if (i < 0) i += Capacity;
                outp[n] = _ring[i];
                i--;
            }
            return outp;
        }
    }

    /// <summary>Forget everything. For the UI's "clear" button and for tests;
    /// the history is per-session and never persisted, so this loses nothing
    /// that was not already going to be lost at exit.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_ring);   // drop the string references, this can sit idle for weeks
            _next = 0;
            _count = 0;
        }
        Changed?.Invoke();
    }

    /// <summary>Shorthand for the writers, which are scattered across two
    /// projects and would otherwise all spell out ActivityLog.Shared.Add.</summary>
    public static void Note(ActivityKind kind, string text) => Shared.Add(kind, text);
}
