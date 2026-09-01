using System.Text.Json;

namespace UnifiedRgb.Core;

/// <summary>One ARGB header hosting an addressable device (fan ring, strip).</summary>
public sealed class ArgbHeaderConfig
{
    public int Header { get; set; }              // 1-4
    public string Name { get; set; } = "";
    public int Leds { get; set; } = 8;
    public string ColorOrder { get; set; } = "GRB";   // wire order: RGB/GRB/BGR...
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

    static readonly string PathName = AppPaths.Config("hardware.json");

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    static HardwareConfig? _loaded;

    /// <summary>Load (once) from %APPDATA%, writing the defaults file on first
    /// run so users have something to edit. Any error falls back to defaults.</summary>
    public static HardwareConfig Load()
    {
        if (_loaded != null) return _loaded;
        try
        {
            if (File.Exists(PathName))
                return _loaded = JsonSerializer.Deserialize<HardwareConfig>(File.ReadAllText(PathName), Opts) ?? new();

            var def = new HardwareConfig();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathName)!);
            SafeFile.WriteAllText(PathName, JsonSerializer.Serialize(def, Opts));
            return _loaded = def;
        }
        catch { return _loaded = new HardwareConfig(); }
    }

    /// <summary>Persist and make this the active config (a device rescan
    /// rebuilds zones from it).</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathName)!);
            SafeFile.WriteAllText(PathName, JsonSerializer.Serialize(this, Opts));
        }
        catch { }
        _loaded = this;
    }
}
