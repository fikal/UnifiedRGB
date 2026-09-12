using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Automation;

namespace UnifiedRgb.App;

/// <summary>A saved effect assignment: which effect runs on which LED range of
/// which device, with its speed / tint / custom-pattern settings.</summary>
public sealed class EffectAssignment
{
    public string Device { get; set; } = "";
    public int Offset { get; set; }
    public int Count { get; set; }
    public string Effect { get; set; } = "";        // EffectChoice name
    public double Speed { get; set; } = 1.0;
    public bool Reverse { get; set; }               // direction: run the effect's clock backwards
    public string? BaseColor { get; set; }          // tint for base-color effects (hex)
    public string? PatternColor { get; set; }       // custom pattern settings
    public string? PatternMotion { get; set; }
    public double PatternDensity { get; set; } = 1.0;
    public bool PatternReverse { get; set; }
    public string[]? PatternPalette { get; set; }

    /// <summary>Rendered across the desk rather than against the device's own
    /// shape. Additive: an older build reading this file ignores it and the
    /// effect simply runs per-device, which is what it did before.</summary>
    public bool Canvas { get; set; }
}

/// <summary>What a profile does to the pump panel: put up one screen, run one
/// show, or leave it alone.
///
/// One type rather than two loose strings because they are one decision with
/// three answers, and callers were already fumbling the third. A null PumpTarget
/// means "this machine has nothing to say" - no screens and no shows exist - and
/// a target with both fields null means the user chose to leave the panel alone.
/// Those are genuinely different: the first must not overwrite what a profile
/// already carries, and the second must be able to clear it.</summary>
public readonly record struct PumpTarget(string? Screen, string? Show)
{
    public static readonly PumpTarget None = new(null, null);
    public bool IsNone => string.IsNullOrWhiteSpace(Screen) && string.IsNullOrWhiteSpace(Show);
    public static PumpTarget OfScreen(string name) => new(name, null);
    public static PumpTarget OfShow(string name) => new(null, name);
}

/// <summary>A saved lighting setup: per-device LED frames, keyed by device
/// name (a name is stable for as long as one machine keeps one set of
/// drivers), plus the effect assignments and user swatches that were live when
/// it was captured - and, since a profile became the one thing that means the
/// whole desk, the pump panel and the wallpaper that went with it.
///
/// The keys stay names on purpose. Every profile ever saved uses them, as do
/// canvas.json and the exit behaviours, and re-keying them would invalidate
/// people's saved setups for a benefit they only see when they MOVE machines.
/// The move is where a name stops being enough, so that is where identity is
/// added: a setup bundle records Core's DeviceIdentity next to each name and
/// remaps the keys on import. See Services/SetupBundle.cs.</summary>
public sealed class Profile
{
    // Not `required`: System.Text.Json throws for a missing required member, so
    // ONE hand-edited/old entry without a Name used to fail the whole list.
    public string Name { get; set; } = "";
    /// <summary>deviceName -> hex per LED. Null-tolerant on set: "DeviceFrames": null
    /// in a hand-edited file used to NRE the startup profile apply in the
    /// view-model constructor, i.e. the app would not launch until the file was fixed.</summary>
    public Dictionary<string, string[]> DeviceFrames { get => _frames; set => _frames = value ?? new(); }
    Dictionary<string, string[]> _frames = new();
    public string[]? CustomColors { get; set; }                              // user swatches (hex)
    public List<EffectAssignment>? Effects { get; set; }                     // running effects per target
    /// <summary>The saved pump-LCD screen this profile was saved with, so one
    /// profile switch sets the lights and the pump together. Null = leave the
    /// pump as it is. Additive: an older build ignores it.</summary>
    public string? Screen { get; set; }
    /// <summary>A saved SHOW - a timeline of profiles - that this profile starts.
    /// INDEPENDENT of Screen, not an alternative to it: setting both is the
    /// ordinary thing to build, because a profile that starts a show is usually
    /// also a step of that show and still has to say what the panel displays
    /// while it is up. Ignored when this profile is applied BY a show, so a step
    /// cannot swap the show out from under itself. Additive, like Screen.</summary>
    public string? Show { get; set; }
    /// <summary>The Wallpaper Engine profile this profile was saved with, so one
    /// switch sets the lights, the pump LCD and the screen in the case. Null =
    /// leave the wallpaper alone, which is what every profile saved before this
    /// existed means. A NAME rather than wallpaper files per monitor: the name
    /// is the user's own arrangement, and it survives a display being unplugged
    /// or renumbered. Additive: an older build ignores it.</summary>
    public string? Wallpaper { get; set; }
    public override string ToString() => Name;
}

public sealed class SettingsData
{
    public string? StartupProfile { get; set; }
    public bool StartMinimized { get; set; }        // open straight to the tray on launch
    public bool FirstRunDone { get; set; }           // the welcome wizard has been shown once
    /// <summary>Public builds: check GitHub Releases for a newer build at
    /// startup (the only outbound request such a build makes). Opt-out.</summary>
    public bool GithubUpdateCheck { get; set; } = true;
    public double[]? WindowBounds { get; set; }     // left, top, width, height
    public bool WindowMaximized { get; set; }
    public string[]? CustomColors { get; set; }     // user swatches (hex), global
    public List<DisabledDeviceEntry>? DisabledDevices { get; set; }
    public bool UseOpenRgb { get; set; }            // bridge extra devices via a managed OpenRGB
    /// <summary>Custom Cooling-row names, keyed "fan:{name}" where name is the
    /// row's raw sensor name: the LHM header name for board fans ("Fan #2"),
    /// "GPU" for the GPU row, "Lian Li {slot}" for wireless fans. By NAME, like
    /// fan-config.json, because LHM indices shift across re-enumeration; two
    /// identically named headers therefore share one label.</summary>
    public Dictionary<string, string>? FanLabels { get; set; }
    /// <summary>Global brightness scale (0.1-1.0) applied to every write.</summary>
    public double MasterBrightness { get; set; } = 1.0;
    public double LianSpeedScale { get; set; } = 1.4;   // fan animation speed calibration (see IntervalScale)
    public int LianUniFanCount { get; set; } = 4;       // legacy single count - migrated to [Channel]
    public int LianUniChannel { get; set; } = 0;        // active wired-hub connector 0..3
    public List<int> LianUniFansByChannel { get; set; } = new() { 1, 1, 1, 1 };   // fans per connector

    /*--- automation ---*/
    /// <summary>Foreground-app profile switching enabled.</summary>
    public bool AppSwitchEnabled { get; set; }
    public List<AutomationRule>? AutomationRules { get; set; }
    /// <summary>Lights off while the session is locked; restore on unlock.</summary>
    public bool LockLightsOff { get; set; } = true;
    /*--- Night mode became one row of the scheduler. These four are still
          written so an older build keeps working, but nothing reads them after
          the one-shot migration below. ---*/
    public bool NightMode { get; set; }
    public string NightStart { get; set; } = "23:00";
    public string NightEnd { get; set; } = "07:00";
    public bool NightIdleOnly { get; set; }

    /// <summary>Timed windows: lights off, or a profile, on chosen days.</summary>
    public List<ScheduleRule>? Schedules { get; set; }

    /// <summary>Threshold rules over the sensors (CPU hits 85, go red).</summary>
    public bool SensorRulesEnabled { get; set; }
    public List<SensorRule>? SensorRules { get; set; }

    /// <summary>On app exit, switch the Lian Li wireless fans to follow their
    /// mainboard sync wire (e.g. SYS_FAN1) so a hardware curve keeps them
    /// temperature-aware while the app is away.</summary>
    public bool LianHandoffOnExit { get; set; }

    /// <summary>Let other software drive our lighting over OpenRGB's network
    /// protocol. Off by default: opening a port is the user's call.</summary>
    public bool SdkServerEnabled { get; set; }

    /// <summary>When an app, sensor or scheduled rule stops matching, go back
    /// to the startup profile rather than to whatever was on screen when the
    /// rule started. On by default: the snapshot is invisible, and it is easy
    /// to end up with the rule's OWN profile as the thing it returns to.</summary>
    public bool ReturnToStartupProfile { get; set; } = true;

    /// <summary>Listen for Counter-Strike 2 game state.</summary>
    public bool Cs2Enabled { get; set; }

    /// <summary>The shared secret in the game's config file. Generated once and
    /// kept, so re-installing the config does not invalidate an existing one.</summary>
    public string? GsiToken { get; set; }

    /// <summary>Accept SDK clients from the network, not just this machine.
    /// The protocol has no authentication, so this is opt-in on top of opt-in.</summary>
    public bool SdkServerLan { get; set; }

    /// <summary>Starred effects shown as pills (null = built-in defaults).</summary>
    public List<string>? FavoriteEffects { get; set; }

    /// <summary>User-saved color palettes (name + hex list), shown in the
    /// Palette Library alongside the built-in presets.</summary>
    public List<SavedPalette>? SavedPalettes { get; set; }
}

/// <summary>A named color palette the user saved for reuse across effects.</summary>
public sealed class SavedPalette
{
    public string Name { get; set; } = "";
    public string[] Colors { get; set; } = System.Array.Empty<string>();   // hex, no '#'
}

/// <summary>A device the user disabled: the app never opens it (its whole
/// driver family is skipped at detection) so other RGB software can own it.
/// Name is what the UI shows; Family is the factory type that gets skipped.</summary>
public sealed class DisabledDeviceEntry
{
    public string Name { get; set; } = "";
    public string Family { get; set; } = "";
}

/// <summary>Loads/saves profiles + settings as JSON under %APPDATA%\UnifiedRgb,
/// and manages the launch-at-login registry entry.</summary>
public sealed class ProfileStore
{
    static readonly string Dir = AppPaths.ConfigDir;
    static readonly string ProfilesPath = AppPaths.Config("profiles.json");
    static readonly string SettingsPath = AppPaths.Config("settings.json");
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "UnifiedRgb";

    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public List<Profile> Profiles { get; } = new();
    public SettingsData Settings { get; private set; } = new();

    /// <summary>Night mode is now one schedule. Carry an existing setup over so
    /// upgrading changes nothing the user can see.
    ///
    /// Runs whenever Schedules is missing rather than once behind a flag: an
    /// older build strips fields it does not know, so a downgrade-then-upgrade
    /// arrives here with the legacy fields intact and Schedules gone, and the
    /// right answer then is to rebuild it. The legacy fields keep their values
    /// for exactly that reason.</summary>
    /// <returns>True when the file needs writing back.</returns>
    static bool MigrateNightMode(SettingsData s)
    {
        if (s.Schedules != null) return false;
        s.Schedules = new();
        if (!s.NightMode) return true;
        s.Schedules.Add(new ScheduleRule
        {
            Enabled = true,
            Days = 0x7F,
            Start = s.NightStart,
            End = s.NightEnd,
            Action = ScheduleAction.LightsOff,
            IdleOnly = s.NightIdleOnly,
        });
        Log.Info("settings", $"night mode migrated to a schedule ({s.NightStart} to {s.NightEnd})");
        return true;
    }

    public ProfileStore()
    {
        Directory.CreateDirectory(Dir);
        ReadFromDisk();
        // Write the migration back straight away. Settings are only saved when
        // something changes, so otherwise an upgraded night schedule would live
        // in memory until the user happened to touch an unrelated setting.
        if (MigrateNightMode(Settings)) SaveSettings();
    }

    /// <summary>Re-read both files from disk into THIS store.
    ///
    /// For the setup importer, which rewrites profiles.json and settings.json
    /// underneath the running app. Without it the app would keep the list it
    /// loaded at startup and the next routine save would put the pre-import
    /// profiles straight back over the imported ones. The list instance is
    /// kept (cleared and refilled) because the view model binds to it.</summary>
    public void Reload()
    {
        ReadFromDisk();
        if (MigrateNightMode(Settings)) SaveSettings();
        Log.Info("store", $"profiles and settings re-read from disk ({Profiles.Count} profile(s))");
    }

    void ReadFromDisk()
    {
        Profiles.Clear();
        // A null list entry ("[null]") is dropped rather than left to NRE the
        // first name lookup at startup.
        Profiles.AddRange(LoadJson<List<Profile?>>(ProfilesPath, "profiles.json")?.OfType<Profile>()
                          ?? Enumerable.Empty<Profile>());
        // Same for a null EFFECT entry ("Effects": [null]) and a null Device:
        // scrubbed once here so Capture's carry-over of absent devices and the
        // view-model's restore never meet one (an NRE on the first re-save).
        foreach (var p in Profiles)
        {
            p.Effects?.RemoveAll(e => e == null);
            foreach (var e in p.Effects ?? new()) e.Device ??= "";
        }
        Settings = LoadJson<SettingsData>(SettingsPath, "settings.json") ?? new();
    }

    /// <summary>Read a JSON store; null when absent or unreadable. A CORRUPT
    /// file is copied aside first (`*.corrupt-yyyyMMdd-HHmmss`) and logged: the
    /// old path silently substituted defaults, and the next routine save then
    /// overwrote the user's profiles/settings for good. A file that merely
    /// could not be READ (sharing violation from an AV/sync client at an
    /// autostart launch, permissions) is not corrupt: the read is retried
    /// briefly, and if it still fails the store runs on defaults with saves
    /// to that path disabled for this session - so the intact file on disk is
    /// never replaced by an empty one.</summary>
    internal static T? LoadJson<T>(string path, string what) where T : class
    {
        if (!File.Exists(path)) return null;
        string text;
        try { text = ReadWithRetry(path); }
        // Vanished between Exists and the read (sync client relocating the
        // folder, AV quarantine): nothing on disk to protect, so it is "no
        // file" - defaults, saves allowed - not "unreadable".
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception ex)
        {
            lock (Unreadable) Unreadable.Add(path);
            Log.Warn("store", $"{what} could not be read ({ex.Message}) - running on defaults; saves to it are off until the next launch");
            return null;
        }
        try { return JsonSerializer.Deserialize<T>(text); }
        catch (Exception ex)
        {
            string backup = path + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            try { File.Copy(path, backup, overwrite: true); } catch { backup = "(backup failed)"; }
            Log.Warn("store", $"{what} unreadable ({ex.Message}) - starting from defaults; original kept at {backup}");
            return null;
        }
    }

    // Sharing violations from a scanner/sync client last milliseconds; a few
    // short retries cover them without turning startup into a wait.
    static string ReadWithRetry(string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return File.ReadAllText(path); }
            catch (IOException ex) when (attempt < 4 && ex is not (FileNotFoundException or DirectoryNotFoundException)) { Thread.Sleep(200); }
        }
    }

    /// <summary>Paths whose load failed for a non-corruption reason this
    /// session; Save skips them (see LoadJson).</summary>
    static readonly HashSet<string> Unreadable = new(StringComparer.OrdinalIgnoreCase);

    public void SaveProfiles() => TrySaveProfiles();
    internal bool TrySaveProfiles() => TrySave(ProfilesPath, Profiles, "profiles.json");
    public void SaveSettings() => Save(SettingsPath, Settings, "settings.json");

    /// <summary>A migration must not replace a legacy step with a reference
    /// to a profile that failed to reach disk.</summary>
    internal bool AddMigratedProfile(Profile profile)
    {
        Profiles.Add(profile);
        if (TrySaveProfiles()) return true;
        Profiles.Remove(profile);
        return false;
    }

    /// <summary>Saves are called from property setters, timers and Dispose; a
    /// locked file (AV scan, sync client) must log, not surface as an error
    /// dialog or abort the shutdown sequence. Shared by the other JSON stores
    /// (scenes.json) so the unreadable-file guard covers them too.</summary>
    internal static void Save<T>(string path, T data, string what)
        => TrySave(path, data, what);

    internal static bool TrySave<T>(string path, T data, string what)
    {
        bool unreadable;
        lock (Unreadable) unreadable = Unreadable.Contains(path);
        if (unreadable)
        {
            Log.Occasional($"store-skip:{what}", "store", $"{what} save skipped: the file could not be read at startup (see above)");
            return false;
        }
        try { SafeFile.WriteAllText(path, JsonSerializer.Serialize(data, JsonOpts)); return true; }
        catch (Exception ex) { Log.Warn("store", $"{what} save failed: {ex.Message}"); return false; }
    }

    /// <summary>Capture the given frames into a named profile (replacing any
    /// same-named profile) and persist. Devices absent right now (disabled or
    /// unplugged) keep their previously saved colors and effect assignments —
    /// disabling a device must never bleed its data out of profiles.</summary>
    /// <param name="carryFrom">The profile this one replaces under a DIFFERENT
    /// name (a rename). It has already been deleted, so it cannot be found by
    /// name; its absent-device data and screen carry over from here.</param>
    /// <summary>Empty and whitespace are stored as null, so "nothing bound" has
    /// exactly one representation in the file.</summary>
    static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    Profile BuildCapture(string name, IEnumerable<(IRgbDevice Device, Rgb[] Frame)> frames,
                           string[]? customColors = null, List<EffectAssignment>? effects = null,
                           PumpTarget? pump = null, Profile? carryFrom = null, string? wallpaper = null)
    {
        // A rename carries the RENAMED profile's data, even onto a name that
        // already exists: "rename A to B" means A's remembered devices, not the
        // old B's.
        var old = carryFrom ?? Profiles.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        // The screen follows the same rule as an absent device: nothing known
        // right now (no panel, or a canvas that is not a saved screen) keeps
        // what the profile already had rather than silently dropping it.
        // The wallpaper needs one distinction the screen does not, so it takes
        // three states rather than two. NULL means "nothing known here" - most
        // often Wallpaper Engine is not installed on THIS machine - and keeps
        // whatever the profile already had, so opening a bundle from the desktop
        // on a laptop does not quietly strip the wallpaper out of every profile.
        // EMPTY means the user chose "leave the wallpaper alone" on purpose, and
        // that has to be able to clear a name they set earlier.
        var p = new Profile
        {
            Name = name, CustomColors = customColors, Effects = effects,
            // No pump target known on this machine keeps whatever the profile
            // already carried; a target that says "leave it alone" clears both,
            // which is how a binding gets removed.
            Screen = pump == null ? old?.Screen : Blank(pump.Value.Screen),
            Show   = pump == null ? old?.Show   : Blank(pump.Value.Show),
            Wallpaper = wallpaper == null ? old?.Wallpaper
                      : wallpaper.Length == 0 ? null
                      : wallpaper,
        };
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dev, frame) in frames)
        {
            p.DeviceFrames[dev.Name] = frame.Select(c => c.ToHex()).ToArray();
            present.Add(dev.Name);
        }

        if (old != null)
        {
            foreach (var kv in old.DeviceFrames)
                if (!present.Contains(kv.Key))
                    p.DeviceFrames[kv.Key] = kv.Value;
            var absent = old.Effects?.Where(e => !present.Contains(e.Device)).ToList();
            if (absent is { Count: > 0 })
            {
                p.Effects ??= new();
                p.Effects.AddRange(absent);
            }
        }

        return p;
    }

    public Profile Capture(string name, IEnumerable<(IRgbDevice Device, Rgb[] Frame)> frames,
                           string[]? customColors = null, List<EffectAssignment>? effects = null,
                           PumpTarget? pump = null, Profile? carryFrom = null, string? wallpaper = null)
    {
        var p = BuildCapture(name, frames, customColors, effects, pump, carryFrom, wallpaper);
        Profiles.RemoveAll(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Profiles.Add(p);
        SaveProfiles();
        return p;
    }

    internal Profile? TryCapture(string name, IEnumerable<(IRgbDevice Device, Rgb[] Frame)> frames,
                           string[]? customColors = null, List<EffectAssignment>? effects = null,
                           PumpTarget? pump = null, Profile? carryFrom = null, string? wallpaper = null)
    {
        var p = BuildCapture(name, frames, customColors, effects, pump, carryFrom, wallpaper);
        var next = Profiles.Where(x => !x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Append(p).ToList();
        if (!TrySave(ProfilesPath, next, "profiles.json")) return null;
        Profiles.Clear(); Profiles.AddRange(next);
        return p;
    }

    internal bool TryRemoveRenamedProfile(string name)
    {
        var next = Profiles.Where(x => !x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!TrySave(ProfilesPath, next, "profiles.json")) return false;
        Profiles.Clear(); Profiles.AddRange(next);
        return true;
    }

    public void Delete(string name)
    {
        Profiles.RemoveAll(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (Settings.StartupProfile?.Equals(name, StringComparison.OrdinalIgnoreCase) == true)
        {
            Settings.StartupProfile = null;
            SaveSettings();
        }
        SaveProfiles();
    }

    /*-----------------------------------------------------*\
    | Launch at login — an ELEVATED scheduled task (RL       |
    | HIGHEST), so the PawnIO CPU-temp sensor works at boot  |
    | with no UAC prompt. Creating/removing the task needs   |
    | one elevation; the logon runs themselves never prompt. |
    \*-----------------------------------------------------*/
    const string TaskName = "UnifiedRGB";

    public static bool IsAutoStartEnabled()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "schtasks", Arguments = $"/Query /TN \"{TaskName}\"",
                UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p!.WaitForExit(4000);
            if (p.ExitCode == 0) return true;
        }
        catch { }
        // Legacy: the old HKCU Run entry.
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) != null;
    }

    /// <summary>Create/remove the logon task. False when schtasks failed, timed
    /// out or the UAC prompt was declined - logged with the exit code, so the
    /// "why didn't it start after reboot" case has a trace instead of a ticked
    /// box over a task that does not exist.</summary>
    public static bool SetAutoStart(bool enable)
    {
        string verb = enable ? "/Create" : "/Delete";
        try
        {
            // Clear any legacy Run-key entry either way.
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                key.DeleteValue(RunValue, throwOnMissingValue: false);

            string exe = Environment.ProcessPath ?? "";
            string cmd = enable
                ? $"/Create /F /TN \"{TaskName}\" /SC ONLOGON /RL HIGHEST /TR \"\\\"{exe}\\\" --autostart\""
                : $"/Delete /F /TN \"{TaskName}\"";
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "schtasks", Arguments = cmd,
                UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) { Log.Warn("autostart", $"schtasks {verb}: process did not start"); return false; }
            if (!p.WaitForExit(15000)) { Log.Warn("autostart", $"schtasks {verb}: timed out"); return false; }
            if (p.ExitCode != 0) { Log.Warn("autostart", $"schtasks {verb} exited {p.ExitCode}"); return false; }
            return true;
        }
        catch (Exception ex)
        {
            // Includes the user declining UAC (Win32Exception 1223).
            Log.Warn("autostart", $"schtasks {verb} failed: {ex.Message}");
            return false;
        }
    }
}
