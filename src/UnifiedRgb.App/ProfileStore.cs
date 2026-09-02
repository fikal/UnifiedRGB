using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using UnifiedRgb.Core;

namespace UnifiedRgb.App;

/// <summary>A saved lighting setup: per-device LED frames, keyed by device
/// name (device identity is stable per machine).</summary>
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
}

public sealed class Profile
{
    // Not `required`: System.Text.Json throws for a missing required member, so
    // ONE hand-edited/old entry without a Name used to fail the whole list.
    public string Name { get; set; } = "";
    public Dictionary<string, string[]> DeviceFrames { get; set; } = new();  // deviceName -> hex per LED
    public string[]? CustomColors { get; set; }                              // user swatches (hex)
    public List<EffectAssignment>? Effects { get; set; }                     // running effects per target
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
    /// <summary>Custom Cooling-row names, keyed "{chipId:X4}:{fanIndex}" for
    /// board fans and "gpu:{fanIndex}" for GPU fans.</summary>
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
    /// <summary>Lights off on a nightly schedule.</summary>
    public bool NightMode { get; set; }
    public string NightStart { get; set; } = "23:00";
    public string NightEnd { get; set; } = "07:00";
    public bool NightIdleOnly { get; set; }   // night-off waits for 10 min idle instead of firing at the start time

    /// <summary>On app exit, switch the Lian Li wireless fans to follow their
    /// mainboard sync wire (e.g. SYS_FAN1) so a hardware curve keeps them
    /// temperature-aware while the app is away.</summary>
    public bool LianHandoffOnExit { get; set; }

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

/// <summary>Foreground-app rule: when a process whose name contains Process
/// is in the foreground, apply Profile.</summary>
public sealed class AutomationRule
{
    public string Process { get; set; } = "";
    public string Profile { get; set; } = "";
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

    public ProfileStore()
    {
        Directory.CreateDirectory(Dir);
        Profiles = LoadJson<List<Profile>>(ProfilesPath, "profiles.json") ?? new();
        Settings = LoadJson<SettingsData>(SettingsPath, "settings.json") ?? new();
    }

    /// <summary>Read a JSON store; null when absent or unreadable. An unreadable
    /// file is COPIED aside first (`*.corrupt-yyyyMMdd-HHmmss`) and logged: the
    /// old path silently substituted defaults, and the next routine save then
    /// overwrote the user's profiles/settings for good.</summary>
    internal static T? LoadJson<T>(string path, string what) where T : class
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path)); }
        catch (Exception ex)
        {
            string backup = path + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            try { File.Copy(path, backup, overwrite: true); } catch { backup = "(backup failed)"; }
            Log.Warn("store", $"{what} unreadable ({ex.Message}) - starting from defaults; original kept at {backup}");
            return null;
        }
    }

    public void SaveProfiles() => Save(ProfilesPath, Profiles, "profiles.json");
    public void SaveSettings() => Save(SettingsPath, Settings, "settings.json");

    /// <summary>Saves are called from property setters, timers and Dispose; a
    /// locked file (AV scan, sync client) must log, not surface as an error
    /// dialog or abort the shutdown sequence.</summary>
    static void Save<T>(string path, T data, string what)
    {
        try { SafeFile.WriteAllText(path, JsonSerializer.Serialize(data, JsonOpts)); }
        catch (Exception ex) { Log.Warn("store", $"{what} save failed: {ex.Message}"); }
    }

    /// <summary>Capture the given frames into a named profile (replacing any
    /// same-named profile) and persist. Devices absent right now (disabled or
    /// unplugged) keep their previously saved colors and effect assignments —
    /// disabling a device must never bleed its data out of profiles.</summary>
    public Profile Capture(string name, IEnumerable<(IRgbDevice Device, Rgb[] Frame)> frames,
                           string[]? customColors = null, List<EffectAssignment>? effects = null)
    {
        var old = Profiles.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var p = new Profile { Name = name, CustomColors = customColors, Effects = effects };
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dev, frame) in frames)
        {
            p.DeviceFrames[dev.Name] = frame.Select(c => c.ToString().TrimStart('#')).ToArray();
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

        Profiles.RemoveAll(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Profiles.Add(p);
        SaveProfiles();
        return p;
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

    public static void SetAutoStart(bool enable)
    {
        // Clear any legacy Run-key entry either way.
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            key.DeleteValue(RunValue, throwOnMissingValue: false);

        string exe = Environment.ProcessPath ?? "";
        string cmd = enable
            ? $"/Create /F /TN \"{TaskName}\" /SC ONLOGON /RL HIGHEST /TR \"\\\"{exe}\\\" --autostart\""
            : $"/Delete /F /TN \"{TaskName}\"";
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "schtasks", Arguments = cmd,
                UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(15000);
        }
        catch { /* user declined UAC — leave state as-is */ }
    }
}
