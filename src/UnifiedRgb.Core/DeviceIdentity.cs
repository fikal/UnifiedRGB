using System.Text;

namespace UnifiedRgb.Core;

/*-----------------------------------------------------------*\
| A device's identity, derived rather than reported.           |
|                                                              |
| Everything the app saves today is keyed by device NAME:      |
| profiles, canvas placement, exit behaviours, fan labels.     |
| That works because a name is stable for as long as one       |
| machine keeps one set of drivers, and it is why nothing here |
| changes those keys. It stops working the moment a setup      |
| moves: the same keyboard is "Corsair K95 RGB Platinum" on    |
| one machine and "K95 Platinum" on the other, and a restore   |
| that matches on the string silently drops its lighting.      |
|                                                              |
| So identity lives BESIDE the name, for the paths that cross  |
| machines (the setup bundle). It is computed from what every  |
| driver already exposes, and deliberately excludes the name   |
| so that renaming a device does not change who it is.         |
|                                                              |
| WHY NOT A REAL SERIAL: because IRgbDevice does not carry one |
| and most of these buses will not tell us. A per-driver       |
| serial (HID serial string, USB device path, SMBus address)   |
| is strictly better and is the follow-up: it would let two    |
| identical fans be told apart across a re-plug, which the     |
| ordinal below can only do while their detection order holds. |
| When that arrives, it slots in as a preferred source here    |
| and the derived form stays as the fallback.                  |
\*-----------------------------------------------------------*/

/// <summary>One device as a bundle records it: what it calls itself, and who
/// it is. Both, because the identity is for matching and the name is what a
/// human recognises in a conflict preview.</summary>
public sealed class DeviceFingerprint
{
    public string Name { get; set; } = "";

    /// <summary>The derived identity (see DeviceIdentity.For). Opaque on
    /// purpose: nothing should parse it, only compare it.</summary>
    public string Identity { get; set; } = "";

    public string Vendor { get; set; } = "";

    /// <summary>DeviceType by NAME, not by its integer value: an enum member
    /// inserted in the middle would otherwise re-label every fingerprint ever
    /// written into a bundle.</summary>
    public string Type { get; set; } = "";

    public int LedCount { get; set; }

    /// <summary>One line a human can match against their own hardware.</summary>
    public string Describe()
        => $"{Name} ({Vendor} {Type}, {LedCount} LED{(LedCount == 1 ? "" : "s")})";

    public override string ToString() => Describe();
}

/// <summary>Derives a stable identity for a device from its shape.</summary>
public static class DeviceIdentity
{
    /// <summary>The shape string an identity hashes. Vendor, type, LED count
    /// and the exact zone layout: enough that a keyboard is never confused
    /// with a fan hub, and that a 4-fan hub is distinguished from a 3-fan one.
    ///
    /// The NAME is not in here, and that is the point: a user who renames a
    /// device, or a driver that spells a model differently on the next
    /// machine, must still resolve to the same identity. Everything that is in
    /// here is a property of the hardware, so it reproduces on any machine
    /// that sees the same device through the same driver.</summary>
    // internal: Survey is the only production caller, and the suite reaches
    // it through InternalsVisibleTo. A public surface nothing outside the
    // assembly uses is a promise this app never made.
    internal static string Signature(IRgbDevice device)
    {
        if (device == null) return "v=|t=|n=0|z=";
        var sb = new StringBuilder();
        // Case-folded and trimmed: vendor strings arrive from firmware and
        // registry alike, and "Corsair" vs "CORSAIR " is not a different
        // device.
        sb.Append("v=").Append((device.Vendor ?? "").Trim().ToLowerInvariant());
        sb.Append("|t=").Append(device.Type);
        sb.Append("|n=").Append(device.LedCount);
        sb.Append("|z=");
        // Zones is an interface property a driver computes; a null or a null
        // entry must degrade to "no zones", never to an exception on a code
        // path whose whole job is to survive unfamiliar hardware.
        var zones = device.Zones;
        if (zones != null)
        {
            // By offset, so a driver that reorders its zone list without
            // changing the layout keeps its identity.
            foreach (var z in zones.Where(z => z != null).OrderBy(z => z.Offset).ThenBy(z => z.Count))
                sb.Append(z.Name?.Trim().ToLowerInvariant()).Append(':')
                  .Append(z.Offset).Append(':').Append(z.Count).Append(';');
        }
        return sb.ToString();
    }

    /// <summary>The identity of the ordinal-th device sharing a signature.
    /// Readable prefix so a log line or a preview row means something to a
    /// human, hash so the whole shape is compared in one string comparison,
    /// ordinal so two identical devices are two identities rather than one.</summary>
    internal static string For(IRgbDevice device, int ordinal)
    {
        string type = device?.Type.ToString() ?? "Other";
        int leds = device?.LedCount ?? 0;
        ulong h = Fnv1a64(Signature(device!)) & 0xFFFF_FFFF_FFFFUL;
        return $"{type}-{leds}-{h:x12}#{ordinal}";
    }

    /// <summary>Fingerprint a whole detected device list, assigning the
    /// ordinals that tell identical devices apart. Returned in the caller's
    /// order so it can be zipped back onto its own list.
    ///
    /// Ordinals are handed out over a NAME-sorted pass, with detection order
    /// only as the tiebreak. That keeps the numbering stable across a run
    /// where enumeration order shifts (USB re-enumeration reorders freely),
    /// as long as the names differ. Two devices that are identical AND
    /// identically named can still swap ordinals between runs, which is
    /// exactly the case a real serial would fix.</summary>
    public static List<DeviceFingerprint> Survey(IEnumerable<IRgbDevice>? devices)
    {
        var list = devices?.Where(d => d != null).ToList() ?? new List<IRgbDevice>();
        var result = new DeviceFingerprint[list.Count];

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (device, index) in list
                     .Select((d, i) => (d, i))
                     .OrderBy(x => x.d.Name ?? "", StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.i))
        {
            string sig = Signature(device);
            int ordinal = counts.TryGetValue(sig, out int seen) ? seen : 0;
            counts[sig] = ordinal + 1;
            result[index] = new DeviceFingerprint
            {
                Name = device.Name ?? "",
                Identity = For(device, ordinal),
                Vendor = device.Vendor ?? "",
                Type = device.Type.ToString(),
                LedCount = device.LedCount,
            };
        }
        return result.ToList();
    }

    /// <summary>FNV-1a, 64-bit. NOT string.GetHashCode: that is randomized per
    /// process on .NET, so an identity written into a bundle by one run would
    /// not match the same device on the next launch, let alone another
    /// machine. This has to be reproducible for years, so the algorithm is
    /// spelled out here rather than borrowed from the runtime.</summary>
    static ulong Fnv1a64(string s)
    {
        const ulong offset = 14695981039346656037UL, prime = 1099511628211UL;
        ulong h = offset;
        foreach (byte b in Encoding.UTF8.GetBytes(s))
        {
            h ^= b;
            h *= prime;
        }
        return h;
    }
}
