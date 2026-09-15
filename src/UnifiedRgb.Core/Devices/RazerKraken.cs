using UnifiedRgb.Core.Native;

namespace UnifiedRgb.Core.Devices;

/// <summary>Razer Kraken V3 X headset (1532:0537), driven directly.
///
/// Not part of RazerHid: that driver speaks Razer's 90-byte vendor protocol and
/// gates on a collection carrying it. The Kraken's vendor collection carries a
/// 40-byte FEATURE report and refuses every report id but 0x23, which is not
/// lighting - so it fails that gate, and the headset used to reach us only
/// through OpenRGB.
///
/// That route does not work. OpenRGB detects the device, reports it in Direct
/// mode and accepts every colour, and the headset stays on its onboard effect:
/// verified from cold on two machines, and the only thing that ever lit it was
/// the write below. So we send it ourselves.
///
/// The wire format is OpenRGB's own (RazerKrakenV3Controller), and every byte
/// here was confirmed against real hardware rather than read off a capture:
///   mode   40 01 00 0F 08            -> direct mode
///   colour 40 03 00 RR GG BB         -> the one LED
/// Both go to the CONSUMER collection (usage page 0x000C) as OUTPUT reports,
/// padded to its report length. That is not where a Razer control interface
/// usually lives, which is exactly why it took a probe to find.
///
/// ONE product id on purpose. The V3 (0549) and V3 HyperSense (0533) are in the
/// same OpenRGB controller and probably answer the same commands, but "probably"
/// is how you take over a device you cannot drive and leave its owner worse off
/// than the fallback. Add one when somebody's bundle shows it working.</summary>
public sealed class RazerKraken : IRgbDevice
{
    const ushort VID = 0x1532, PID_KRAKEN_V3_X = 0x0537;
    const byte REPORT_ID = 0x40, CMD_MODE = 0x01, CMD_COLOR = 0x03;
    const byte MODE_ARG = 0x0F, MODE_DIRECT = 0x08;

    readonly IHidTransport _hid;
    readonly int _outLen;
    readonly object _writeLock = new();
    Rgb? _last;
    bool _needMode = true;
    volatile bool _disposed;

    public string Name { get; }
    public string Vendor => "Razer";
    public DeviceType Type => DeviceType.Other;   // no Headset in the enum; it is not a fan or a controller
    public int LedCount => 1;
    public IReadOnlyList<RgbZone> Zones { get; } =
        new[] { new RgbZone { Name = "Headset", Offset = 0, Count = 1 } };

    internal RazerKraken(IHidTransport hid, int outLen, string name)
    {
        _hid = hid; _outLen = outLen; Name = name;
    }

    /// <summary>Product ids this driver owns. RazerHid asks, so the bundle does not
    /// print "nothing here can drive this" directly above a line saying we just
    /// drove it - the Kraken speaks a different protocol, not no protocol.</summary>
    internal static bool Handles(ushort pid) => pid == PID_KRAKEN_V3_X;

    public static RazerKraken? TryOpen()
    {
        // The consumer collection, which is where the lighting commands land.
        // Its output reports are what carries them; the 0xFFA0 vendor collection
        // on the same interface has none at all.
        var r = HidNative.OpenFirst("RazerKraken", VID, PID_KRAKEN_V3_X,
            h => h.UsagePage == 0x000C && h.Usage == 0x0001 && h.OutputLength >= 6);
        if (r == null) return null;
        var info = r.Value.Info;
        string name = string.IsNullOrWhiteSpace(info.Product) ? "Razer Kraken V3 X" : info.Product;
        Log.Info("RazerKraken", $"opened {name} (pid {PID_KRAKEN_V3_X:X4}), {info.OutputLength} B output reports");
        return new RazerKraken(r.Value.Handle, info.OutputLength, name);
    }

    /// <summary>Drop the cached colour so the next frame is written even if it is
    /// identical, and re-assert the mode with it.
    ///
    /// The mode goes too because this is the call every caller that has decided a
    /// write MUST land already makes. Vendor software holding the headset at
    /// detection is the ordinary case - Synapse does it - and a mode asked for
    /// once, at the one moment it was most likely to fail, is a headset that never
    /// lights again until the app is restarted.</summary>
    public void InvalidateCache() { lock (_writeLock) { _last = null; _needMode = true; } }

    public bool SetColors(IReadOnlyList<Rgb> colors)
    {
        if (_disposed) return false;   // a write that outlives Dispose: refuse quietly, per the contract
        // Nothing to show is not a delivery: everything above passes LedCount.
        if (colors.Count == 0) return false;
        lock (_writeLock)
        {
            if (_disposed) return false;
            var c = colors[0];

            // Mode FIRST, and its verdict is the frame's. A colour accepted while
            // the headset is running its onboard effect changes nothing on the
            // user's head, so reporting success there would be a lie a must-land
            // caller cannot see through.
            if (_needMode)
            {
                _last = null;
                if (!Send(CMD_MODE, MODE_ARG, MODE_DIRECT, 0))
                    return WritePolicy.Refused("kraken-mode", "RazerKraken",
                        "direct-mode setup refused; the mode and the colour will be retried");
                _needMode = false;
            }

            if (_last == c) return true;   // already showing it: a skip is a success

            if (!Send(CMD_COLOR, c.R, c.G, c.B))
            {
                // Not cached, and the mode is asked for again: a refusal here is
                // most often the headset having been taken by something else, and
                // whatever took it will have put it back on its own lighting.
                _needMode = true;
                _last = null;
                return WritePolicy.Refused("kraken-write", "RazerKraken",
                    "colour write refused; it will be sent again");
            }
            _last = c;
            return true;
        }
    }

    /// <summary>One command, padded to the collection's report length. Windows
    /// wants exactly that many bytes; the device reads the first few.</summary>
    bool Send(byte cmd, byte a1, byte a2, byte a3)
    {
        var p = new byte[_outLen];
        p[0] = REPORT_ID; p[1] = cmd;
        // p[2] is the report's first argument and is always zero for both
        // commands; the values start at p[3].
        p[3] = a1; p[4] = a2; p[5] = a3;
        return _hid.Write(p);
    }

    // Set under the write lock so a write arriving after this returns false
    // instead of reaching a closed handle and logging a "stopped answering" the
    // headset never earned.
    public void Dispose()
    {
        lock (_writeLock)
        {
            if (_disposed) return;
            _disposed = true;
            _hid.Dispose();
        }
    }
}
