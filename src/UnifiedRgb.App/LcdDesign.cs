using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

using UnifiedRgb.Core;

namespace UnifiedRgb.App;

// Persisted by NAME (integers still accepted on read) so inserting or
// reordering a member can never silently remap every saved design and scene.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LcdElementKind { Time, Date, CpuTemp, Text, GpuTemp, FanRpm, NetSpeed, AnalogClock, Weather }

/// <summary>One positioned text element on the pump display. Coordinates are the
/// top-left corner in landscape space (0..320 x, 0..240 y), which is exactly how
/// the editor canvas lays it out, so the editor is WYSIWYG.</summary>
public sealed class LcdElement : INotifyPropertyChanged
{
    public LcdElementKind Kind { get; set; }

    string _text = "Text";
    public string Text { get => _text; set { _text = value; Changed(); } }

    double _x = 90, _y = 100;
    public double X { get => _x; set { _x = value; Changed(); } }
    public double Y { get => _y; set { _y = value; Changed(); } }

    double _fontSize = 40;
    public double FontSize
    {
        get => _fontSize;
        set { _fontSize = value; Changed(); PropertyChanged?.Invoke(this, new(nameof(ClockSize))); }
    }

    string _colorHex = "FFFFFF";
    public string ColorHex { get => _colorHex; set { _colorHex = value; Changed(); } }

    bool _bold = true;
    public bool Bold { get => _bold; set { _bold = value; Changed(); } }

    [JsonIgnore]
    public string Label => Kind switch
    {
        LcdElementKind.Time => "Time",
        LcdElementKind.Date => "Date",
        LcdElementKind.CpuTemp => "CPU Temp",
        LcdElementKind.GpuTemp => "GPU Temp",
        LcdElementKind.FanRpm => "Fan RPM",
        LcdElementKind.NetSpeed => "Network",
        LcdElementKind.AnalogClock => "Clock",
        LcdElementKind.Weather => "Weather",
        _ => string.IsNullOrWhiteSpace(Text) ? "Text" : Text,
    };

    /// <summary>The analog clock is drawn (a face + hands), not typeset, so the
    /// editor swaps its text block for an image and the panel takes a draw path.</summary>
    [JsonIgnore] public bool IsClock => Kind == LcdElementKind.AnalogClock;

    /// <summary>Size of the clock's editor box in landscape px (diameter). The
    /// FontSize slider drives the radius, so one control sizes every element.</summary>
    [JsonIgnore] public double ClockSize => FontSize * 2;

    // Live-rendered clock face for the WYSIWYG editor, refreshed each tick along
    // with Display. Not persisted, and does not trigger a device re-render.
    ImageSource? _clockImage;
    [JsonIgnore]
    public ImageSource? ClockImage
    {
        get => _clockImage;
        set { _clockImage = value; PropertyChanged?.Invoke(this, new(nameof(ClockImage))); }
    }

    // Live rendered text, refreshed each second for the WYSIWYG editor. Not
    // persisted, and (unlike the real properties) does not trigger a re-render.
    string? _display;
    [JsonIgnore]
    public string Display
    {
        get => _display ?? Label;
        set { _display = value; PropertyChanged?.Invoke(this, new(nameof(Display))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed([CallerMemberName] string? n = null)
    {
        PropertyChanged?.Invoke(this, new(n));
        PropertyChanged?.Invoke(this, new(nameof(Label)));
    }
}

/// <summary>The saved pump layout: an optional background image plus text elements.</summary>
public sealed class LcdDesign
{
    public string? BackgroundImagePath { get; set; }
    /// <summary>Background placement in landscape space. W==0 means "not
    /// set yet" — the app materializes a centered cover rect on load, and
    /// from then on the editor and the panel render from these SAME numbers
    /// (the old split-brain — editor top-aligned, panel centered — is why
    /// the preview and the physical screen disagreed).</summary>
    public double BgX { get; set; }
    public double BgY { get; set; }
    public double BgW { get; set; }
    public double BgH { get; set; }
    public bool BgAspectLock { get; set; } = true;
    public List<LcdElement> Elements { get; set; } = new();

    public static LcdDesign Default() => new()
    {
        Elements =
        {
            new LcdElement { Kind = LcdElementKind.Time, X = 70, Y = 70, FontSize = 64, Bold = true, ColorHex = "FFFFFF" },
            new LcdElement { Kind = LcdElementKind.CpuTemp, X = 95, Y = 150, FontSize = 34, Bold = false, ColorHex = "78C8FF" },
        },
    };

    static string Path => UnifiedRgb.Core.AppPaths.Config("lcd.json");

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static LcdDesign Load() => ProfileStore.LoadJson<LcdDesign>(Path, "lcd.json") ?? Default();

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            SafeFile.WriteAllText(Path, JsonSerializer.Serialize(this, Opts));
        }
        catch (Exception ex) { Log.Warn("lcd", $"lcd.json save failed: {ex.Message}"); }
    }
}

/// <summary>Supplies the current CPU temperature in Celsius, or null if no source
/// is available. A real Ring0-backed reader (PawnIO) plugs in here.</summary>
public interface ICpuTempProvider
{
    double? ReadCelsius();
}

/// <summary>The default when PawnIO isn't available: no CPU temperature.</summary>
public sealed class NullCpuTempProvider : ICpuTempProvider
{
    public double? ReadCelsius() => null;
}
