using System.Runtime.InteropServices;

namespace UnifiedRgb.Core.Native;

/// <summary>Whether this session is locked, ASKED of Windows rather than
/// remembered from a notification.
///
/// SystemEvents.SessionSwitch is a one-shot: miss it and the flag it set is
/// wrong until the process restarts. It gets missed across a suspend, which is
/// not a rare corner - the lock is what happens on the way into sleep, and the
/// unlock is what does not arrive on the way out. The app then sits in "locked,
/// lights off" forever with every device showing its own firmware default, and
/// a two-second re-evaluation timer cannot help because it re-reads the same
/// stale field. Seen on this desk: locked 23:11:46, slept 23:11:49, resumed
/// 23:11:57, unlocked 23:12:26, and the app never noticed - Windows said
/// UNLOCKED while the lights had been off for three minutes.
///
/// So the event stays as the FAST path and this is the truth the periodic
/// re-evaluation reconciles against.</summary>
public static class SessionState
{
    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSQuerySessionInformationW(IntPtr server, int session, int infoClass,
                                                   out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")]
    static extern void WTSFreeMemory(IntPtr memory);

    const int WTSSessionInfoEx = 25;
    const int CurrentSession = -1;
    // WTSINFOEX { DWORD Level; <4 pad>; WTSINFOEX_LEVEL1 Data; } and LEVEL1 is
    // { ULONG SessionId; WTS_CONNECTSTATE_CLASS SessionState; LONG SessionFlags; ... },
    // so the flags sit 8 into a union that starts 8 in - the union is 8-aligned
    // because the rest of LEVEL1 is LARGE_INTEGERs.
    const int LevelOffset = 0, FlagsOffset = 16, MinBytes = FlagsOffset + 4;
    const int Locked = 0, Unlocked = 1;

    /// <summary>True or false when Windows answers, NULL when it will not -
    /// which the caller must treat as "no new information" and keep whatever it
    /// had. Never guess here: guessing locked blacks out a desk that is in use,
    /// and guessing unlocked lights one that its owner walked away from.</summary>
    public static bool? IsLocked()
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!WTSQuerySessionInformationW(IntPtr.Zero, CurrentSession, WTSSessionInfoEx,
                                             out buffer, out int bytes) || buffer == IntPtr.Zero)
                return null;
            if (bytes < MinBytes) return null;
            // Level 1 is the only layout this offset describes. A future level
            // would put something else at +16, and reading it as flags would be
            // worse than not knowing.
            if (Marshal.ReadInt32(buffer, LevelOffset) != 1) return null;
            return Marshal.ReadInt32(buffer, FlagsOffset) switch
            {
                Locked => true,
                Unlocked => false,
                // Documented to be REVERSED on Windows 7 / Server 2008 R2, and
                // anything else is undocumented. Either way: no information.
                _ => null,
            };
        }
        catch { return null; }
        finally { if (buffer != IntPtr.Zero) WTSFreeMemory(buffer); }
    }
}
