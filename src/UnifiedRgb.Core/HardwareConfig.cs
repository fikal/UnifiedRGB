using System.Text.Json;

namespace UnifiedRgb.Core;

/// <summary>One ARGB header hosting an addressable device (fan ring, strip).</summary>
public sealed class ArgbHeaderConfig
{
    public int Header { get; set; }              // 1-4
    public string Name { get; set; } = "";
    public int Leds { get; set; } = 8;
    public string ColorOrder { get; set; } = "GRB";   // wire order: RGB/GRB/BGR...
    /// <summary>The header drives a straight run (GPU ribbon, light bar) rather
    /// than fan rings. Effects place a ring's LEDs on a circle, which mirrors
    /// every lengthwise animation around a strip's midpoint; a strip is laid
    /// out as one line so it animates end to end. Default false = ring, so
    /// existing configs keep the layout they were tuned with.</summary>
    public bool Strip { get; set; }
}

/// <summary>User-editable per-machine hardware settings. Defaults describe this
/// machine; other machines edit %APPDATA%\UnifiedRgb\hardware.json (written on
/// first run) instead of recompiling.</summary>
public sealed class HardwareConfig
{
    public List<ArgbHeaderConfig> GigabyteArgbHeaders { get; set; } = new()
    {
        new ArgbHeaderConfig { Header = 2, Name = "AIO Fans 1+2 (Header 2)", Leds = 8, ColorOrder = "GRB" },
        new ArgbHeaderConfig { Header = 4, Name = "AIO Fan 3 (Header 4)",    Leds = 8, ColorOrder = "GRB" },
    };

    /// <summary>LED counts for Razer devices whose shape the firmware won't
    /// reveal (the HyperFlux V2 pad's strip), keyed by product id in hex
    /// ("00CF"). Set from the Lighting pane's Razer… dialog after a Test chase;
    /// a configured value beats the frame-width probe and the guess.</summary>
    public Dictionary<string, int> RazerLedCounts { get; set; } = new();

    static readonly string PathName = AppPaths.Config("hardware.json");

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    static HardwareConfig? _loaded;

    /// <summary>The user's file exists but could not be read (a sync client
    /// or AV holding it at startup) or could not be backed up before the
    /// defaults took over: Save() must not write this machine's defaults over
    /// it, so saves are off until the next launch.</summary>
    static bool _saveBlocked;

    /// <summary>Load (once) from %APPDATA%, writing the defaults file on first
    /// run so users have something to edit. A corrupt file is copied aside
    /// (`hardware.json.corrupt-<stamp>`) and logged before the defaults take
    /// over; an unreadable one (locked) blocks Save() for the session. Either
    /// way the next Save() cannot silently cement this machine's header layout
    /// over the user's.</summary>
    public static HardwareConfig Load()
    {
        if (_loaded != null) return _loaded;

        if (!File.Exists(PathName))
        {
            var def = new HardwareConfig();
            try { SafeFile.WriteAllText(PathName, JsonSerializer.Serialize(def, Opts)); }
            catch (Exception ex) { Log.Warn("hardware", $"hardware.json defaults could not be written: {ex.Message}"); }
            return _loaded = def;
        }

        // Read and parse are split: a read failure is a locked/vanishing file,
        // not a corrupt one - nothing to copy aside, but the intact original
        // must be protected from Save().
        string text;
        try { text = ReadWithRetry(); }
        catch (Exception ex)
        {
            _saveBlocked = true;
            Log.Warn("hardware", $"hardware.json could not be read ({ex.Message}) - running on defaults; saves are off until the next launch");
            return _loaded = new HardwareConfig();
        }

        try
        {
            var cfg = JsonSerializer.Deserialize<HardwareConfig>(text, Opts) ?? new();
            // The file is hand-edited: an explicit `null` for the list (or a
            // `[null]` entry) would NRE in the device's zone builder and in
            // the header dialog that exists to fix it.
            cfg.GigabyteArgbHeaders ??= new();
            cfg.GigabyteArgbHeaders.RemoveAll(h => h is null);
            cfg.RazerLedCounts ??= new();
            // Normalise the wire order once, at the load boundary, so the
            // device (GigabyteIt5711.NormalizeOrder, which still warns and
            // falls back on an unknown value) and the header dialog's combo
            // agree on a hand-typed "rgb" - the dialog used to pre-select GRB
            // for it and write that back on Save.
            foreach (var h in cfg.GigabyteArgbHeaders)
                h.ColorOrder = (h.ColorOrder ?? "GRB").Trim().ToUpperInvariant();
            return _loaded = cfg;
        }
        catch (Exception ex)
        {
            string backup = PathName + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            try { File.Copy(PathName, backup, overwrite: true); }
            catch { backup = "(backup failed - saves are off until the next launch)"; _saveBlocked = true; }
            Log.Warn("hardware", $"hardware.json unreadable ({ex.Message}) - using defaults; original kept at {backup}");
            return _loaded = new HardwareConfig();
        }
    }

    /// <summary>A sharing violation at startup is usually a sync client or AV
    /// pass that clears within a moment; retry before giving up on the file
    /// (and with it, saves) for the session.</summary>
    static string ReadWithRetry()
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return File.ReadAllText(PathName); }
            catch (IOException ex) when (attempt < 4 && ex is not (FileNotFoundException or DirectoryNotFoundException)) { Thread.Sleep(200); }
        }
    }

    /// <summary>Persist and make this the active config (a device rescan
    /// rebuilds zones from it).</summary>
    public void Save()
    {
        if (_saveBlocked)
        {
            Log.Warn("hardware", "hardware.json save skipped: the file could not be read at startup (applied for this session only)");
            _loaded = this;
            return;
        }
        try { SafeFile.WriteAllText(PathName, JsonSerializer.Serialize(this, Opts)); }
        catch (Exception ex) { Log.Warn("hardware", $"hardware.json save failed: {ex.Message}"); }
        _loaded = this;
    }
}
