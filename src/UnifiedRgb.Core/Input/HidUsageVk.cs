namespace UnifiedRgb.Core.Input;

/// <summary>HID keyboard-page usage code → Windows virtual-key code. Used by
/// keyboards whose LED tables are keyed by HID usage (SteelSeries Apex) to
/// implement IKeyMappedDevice. Returns -1 for usages with no VK.</summary>
public static class HidUsageVk
{
    public static int ToVk(int usage) => usage switch
    {
        >= 0x04 and <= 0x1D => 'A' + (usage - 0x04),         // A..Z
        >= 0x1E and <= 0x26 => '1' + (usage - 0x1E),         // 1..9
        0x27 => '0',
        0x28 => 0x0D,   // Enter
        0x29 => 0x1B,   // Esc
        0x2A => 0x08,   // Backspace
        0x2B => 0x09,   // Tab
        0x2C => 0x20,   // Space
        0x2D => 0xBD,   // - _
        0x2E => 0xBB,   // = +
        0x2F => 0xDB,   // [
        0x30 => 0xDD,   // ]
        0x31 => 0xDC,   // backslash
        0x32 => 0xDC,   // Europe 1 (shares the backslash VK)
        0x33 => 0xBA,   // ; :
        0x34 => 0xDE,   // ' "
        0x35 => 0xC0,   // ` ~
        0x36 => 0xBC,   // , <
        0x37 => 0xBE,   // . >
        0x38 => 0xBF,   // / ?
        0x39 => 0x14,   // CapsLock
        >= 0x3A and <= 0x45 => 0x70 + (usage - 0x3A),        // F1..F12
        0x46 => 0x2C,   // PrintScreen
        0x47 => 0x91,   // ScrollLock
        0x48 => 0x13,   // Pause
        0x49 => 0x2D,   // Insert
        0x4A => 0x24,   // Home
        0x4B => 0x21,   // PageUp
        0x4C => 0x2E,   // Delete
        0x4D => 0x23,   // End
        0x4E => 0x22,   // PageDown
        0x4F => 0x27,   // Right
        0x50 => 0x25,   // Left
        0x51 => 0x28,   // Down
        0x52 => 0x26,   // Up
        0x53 => 0x90,   // NumLock
        0x54 => 0x6F,   // Num /
        0x55 => 0x6A,   // Num *
        0x56 => 0x6D,   // Num -
        0x57 => 0x6B,   // Num +
        0x58 => 0x0D,   // Num Enter (LL hook reports VK_RETURN)
        >= 0x59 and <= 0x61 => 0x61 + (usage - 0x59),        // Num1..Num9
        0x62 => 0x60,   // Num0
        0x63 => 0x6E,   // Num .
        0x64 => 0xE2,   // Europe 2 (ISO backslash)
        0x65 => 0x5D,   // Menu / App
        0xE0 => 0xA2,   // LCtrl
        0xE1 => 0xA0,   // LShift
        0xE2 => 0xA4,   // LAlt
        0xE3 => 0x5B,   // LWin
        0xE4 => 0xA3,   // RCtrl
        0xE5 => 0xA1,   // RShift
        0xE6 => 0xA5,   // RAlt
        0xE7 => 0x5C,   // RWin
        _ => -1,
    };
}
