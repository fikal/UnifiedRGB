using System.Net.Http;

namespace UnifiedRgb.Core;

/// <summary>The single source of truth for where UnifiedRGB keeps its state.
/// Every path under %APPDATA%/%LOCALAPPDATA% goes through here — seven files
/// were hand-building these before.</summary>
public static class AppPaths
{
    /// <summary>%APPDATA%\UnifiedRgb — roaming state (settings, profiles, log).</summary>
    public static readonly string ConfigDir =
        Redirect("UNIFIEDRGB_CONFIG_DIR", Environment.SpecialFolder.ApplicationData);

    /// <summary>%LOCALAPPDATA%\UnifiedRgb — machine-local state (fan-config.json).</summary>
    public static readonly string LocalDir =
        Redirect("UNIFIEDRGB_LOCAL_DIR", Environment.SpecialFolder.LocalApplicationData);

    /// <summary>%ProgramData%\UnifiedRgb — machine-wide state that only an
    /// administrator may write: the bundled OpenRGB build lives here.
    ///
    /// Not LocalAppData, and the difference is a privilege boundary rather than
    /// tidiness. This app runs elevated and LAUNCHES that executable, and every
    /// process running as the user can write to LocalAppData without any
    /// elevation at all - so a bundle kept there let an unprivileged process
    /// swap in a binary of its own and have the administrator process run it,
    /// at every logon. Nothing here creates this folder: it exists only once
    /// <see cref="ProtectedFolder.Ensure"/> has made it with its ACL, and
    /// anything anybody else put under the name first - a folder of their own,
    /// a junction - is moved aside by the same call, never adopted.</summary>
    public static readonly string MachineDir =
        Redirect("UNIFIEDRGB_MACHINE_DIR", Environment.SpecialFolder.CommonApplicationData);

    /// <summary>The real per-user location, unless the TEST HARNESS is what is
    /// running. The harness must not read or write the running user's settings,
    /// profiles and layouts: it used to work against the live files, so a run
    /// killed between a write and its restore left fixture JSON for the app to
    /// load.
    ///
    /// The override is gated on the entry assembly being the test harness, and
    /// that gate is the whole point rather than a detail. An environment
    /// variable is writable by any process running as the user - a plain setx
    /// into HKCU, no elevation - and this app runs ELEVATED. LocalDir is where
    /// the bundled OpenRGB lives, and that executable gets LAUNCHED, so an
    /// ungated variable would let an unprivileged process choose a binary for
    /// the administrator process to run at every logon, and that is not worth a
    /// shortcut in test plumbing. An attacker cannot rename their way into being
    /// UnifiedRgb.Tests, because the check is on OUR entry assembly, not on
    /// anything they supply.</summary>
    /// A METHOD, not a static readonly field: field initializers run in
    /// declaration order, and ConfigDir is declared above this - so as a field
    /// it was still false while ConfigDir was being initialized, and the
    /// redirect silently did nothing. The isolation assertions in the harness
    /// caught that, which is the reason they exist.
    static bool IsTestHost()
        => System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "UnifiedRgb.Tests";

    static string Redirect(string variable, Environment.SpecialFolder folder)
    {
        string fallback = Path.Combine(Environment.GetFolderPath(folder), "UnifiedRgb");
        if (!IsTestHost()) return fallback;
        string? over = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(over)) return fallback;
        // Absolute, so a relative value cannot resolve against whatever the
        // working directory happens to be.
        try { return Path.GetFullPath(over); } catch { return fallback; }
    }

    public static string Config(string file) => Path.Combine(ConfigDir, file);
    public static string Local(string file) => Path.Combine(LocalDir, file);
    public static string Machine(string file) => Path.Combine(MachineDir, file);

    static AppPaths()
    {
        // Both per-user trees, so every store can assume its parent exists:
        // LocalDir used to be created only by the OpenRGB installer, and on a
        // machine that never enabled the bridge fan-config.json silently failed
        // to save. MachineDir is deliberately NOT created here: it has to come
        // into being with its ACL, through ProtectedFolder, or it is just
        // another folder anybody can write to.
        try { Directory.CreateDirectory(ConfigDir); } catch { }
        try { Directory.CreateDirectory(LocalDir); } catch { }
    }
}
