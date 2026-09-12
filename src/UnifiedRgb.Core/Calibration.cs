using System.Runtime.CompilerServices;
using System.Text.Json;

namespace UnifiedRgb.Core;

/*-----------------------------------------------------------*\
| Per-device and per-ZONE color calibration: making one picked |
| color look like one color across the whole desk.             |
|                                                              |
| The problem this exists for is physical, not aesthetic. The  |
| keyboard, the fan rings, the strips and the GPU logo were    |
| built by different vendors out of different LED bins behind  |
| different diffusers, and they are driven at wildly different |
| currents. Ask all of them for #FFFFFF and you get warm white |
| on the board, blue-white on the fans, and a GPU logo bright  |
| enough to read by. No effect fixes that, because the effect  |
| is not what is wrong: the transfer function from "the number |
| we sent" to "the light that came out" differs per device.    |
|                                                              |
| So each device gets three trims:                             |
|                                                              |
|   GAMMA          the shape of that transfer curve. A device  |
|                  whose mid greys read too dark wants gamma   |
|                  below 1; too washed out wants above 1.      |
|   GAIN (R,G,B)   white balance. The straight per-channel     |
|                  scale that pulls a warm white cold or a     |
|                  cold white warm.                            |
|   MAX BRIGHTNESS a hard ceiling, for the one device that is  |
|                  simply louder than everything around it     |
|                  (the GPU logo, every time).                 |
|                                                              |
| ORDER OF OPERATIONS - fixed, and the reason is not taste:    |
|                                                              |
|   1. gamma   out = (v/255) ^ gamma                           |
|   2. gain    out = out * gain[channel]                       |
|   3. cap     out = min(out, MaxBrightness)                   |
|   4. byte    out = round(out * 255), clamped to 0..255       |
|                                                              |
| Gamma is FIRST because it describes the device's own curve,  |
| so it has to act on the value the user actually picked. Gain |
| is SECOND because white balance is a linear property of the  |
| emitter: applied after gamma it is the same ratio at every   |
| level, whereas applied before gamma a gain of 0.9 would come |
| out as 0.9^gamma and the balance would drift as the gamma    |
| slider moved - two controls fighting over one result. The    |
| cap is LAST because a ceiling that gets multiplied afterward |
| is not a ceiling.                                            |
|                                                              |
| Master.Brightness runs AFTER all of this (see Master.Finish).|
| Same argument: calibration must see the picked color, not a  |
| dimmed one, or the white balance would shift every time the  |
| master slider moved. The cap survives that ordering intact,  |
| because master brightness only ever reduces.                 |
|                                                              |
| THE RULE THIS SHARES WITH Master.Scale: calibration is       |
| applied to frames on their way to hardware and NEVER to      |
| stored state. The color the user picked must round-trip out  |
| of a saved profile exactly as they picked it, or trimming a  |
| device would slowly eat their own colors.                    |
|                                                              |
| AND THE RULE THAT PROTECTS EVERY OTHER TEST IN THIS REPO: an |
| untouched device is SHORT-CIRCUITED, not merely mathematical |
| identity. Apply() returns before it touches a byte when      |
| nothing is calibrated at all, and again when this particular |
| device has no entry. A default install therefore produces    |
| byte-for-byte the frames it produced before this file        |
| existed.                                                     |
|                                                              |
| WHY A DEVICE IS NOT A FINE ENOUGH UNIT. A motherboard is one |
| device object and seven completely different lights: a       |
| 30-LED ribbon strip on one header is brighter than, and a    |
| different tint from, the single LED under the chipset, and   |
| both hang off the same controller with the same name. Trim   |
| the device and you trim all seven together, which cannot fix |
| a mismatch that lives BETWEEN them. So a trim may also be    |
| attached to one named ZONE of a device.                      |
|                                                              |
| THE OVERRIDE RULE, and it is override and not composition:   |
|                                                              |
|   * a DEVICE entry means "everything on this device".        |
|   * a ZONE entry REPLACES the device entry for that zone's   |
|     LEDs. It does not multiply with it.                      |
|   * a zone with no entry of its own follows the device.      |
|   * a device with neither is untouched, short circuit and    |
|     all.                                                     |
|                                                              |
| Composition was the other option and it is the wrong one:    |
| two gammas and two caps multiplying together is a result no  |
| user can predict from the two sets of sliders in front of    |
| them, and "the ribbon is at 40% of the board's 50%" is not   |
| a sentence anybody wants to reason about while squinting at  |
| a case. One trim wins, and the more specific one wins.       |
|                                                              |
| Dragging a zone's sliders back to default REMOVES its entry, |
| so the zone follows the device again. That means a zone      |
| cannot be pinned to "explicitly neutral" underneath a        |
| trimmed device, which is deliberate: a slider set that reads |
| identical to the default but means something different from  |
| the default is a worse trap than the case it would serve,    |
| and the same result is reachable by trimming the other zones |
| instead of the device.                                       |
\*-----------------------------------------------------------*/

/// <summary>One trim, for a whole device or for one zone of one. The same
/// type serves both levels because they are the same five numbers doing the
/// same job over a different set of LEDs, and a separate ZoneCalibration would
/// only mean two copies of Normalize, IsIdentity and BuildTable drifting apart.
/// A plain mutable DTO because it is both what calibration.json holds and what
/// a slider binds to; the live copy the write path uses is a lookup table
/// built from it, not this object.</summary>
public sealed class DeviceCalibration
{
    /// <summary>Per-channel white balance. 1.0 = untouched. Above 1 is allowed
    /// (a device may genuinely be short of blue) and simply clips at the top,
    /// which is why the cap exists as a separate control.</summary>
    public double GainR { get; set; } = 1.0;
    public double GainG { get; set; } = 1.0;
    public double GainB { get; set; } = 1.0;

    /// <summary>Transfer-curve shape. 1.0 = untouched.</summary>
    public double Gamma { get; set; } = 1.0;

    /// <summary>Hard ceiling on this device's output, 0.05..1.0. 1.0 = no cap.</summary>
    public double MaxBrightness { get; set; } = 1.0;

    // The slider ranges. They are constants rather than magic numbers in the
    // XAML because Normalize() has to agree with the UI about what is legal,
    // and a hand-edited calibration.json has to be pulled into the same range.
    public const double MinGain = 0.0, MaxGain = 2.0;
    public const double MinGamma = 0.25, MaxGamma = 4.0;
    // The floor matches Master.Brightness's: a device capped to zero looks
    // broken rather than calibrated, and users do not connect "it is dead" to
    // a slider they moved a week ago.
    public const double MinCap = 0.05, MaxCap = 1.0;

    public DeviceCalibration Clone() => new()
    {
        GainR = GainR, GainG = GainG, GainB = GainB, Gamma = Gamma, MaxBrightness = MaxBrightness,
    };

    /// <summary>Pull hand-edited or NaN values into range. calibration.json is
    /// user-editable and a NaN gamma would turn every LED on the device into
    /// whatever Math.Pow does with it, which is worse than ignoring the file.
    /// Returns true when something had to change, so the loader can say so.</summary>
    public bool Normalize()
    {
        bool changed = false;
        GainR = Fix(GainR, 1.0, MinGain, MaxGain, ref changed);
        GainG = Fix(GainG, 1.0, MinGain, MaxGain, ref changed);
        GainB = Fix(GainB, 1.0, MinGain, MaxGain, ref changed);
        Gamma = Fix(Gamma, 1.0, MinGamma, MaxGamma, ref changed);
        MaxBrightness = Fix(MaxBrightness, 1.0, MinCap, MaxCap, ref changed);
        return changed;
    }

    static double Fix(double v, double fallback, double lo, double hi, ref bool changed)
    {
        // Non-finite first: Math.Clamp(NaN, lo, hi) is NaN, so clamping alone
        // would let a NaN straight through into Math.Pow.
        if (!double.IsFinite(v)) { changed = true; return fallback; }
        double c = Math.Clamp(v, lo, hi);
        if (c != v) changed = true;
        return c;
    }

    /// <summary>True when this trim cannot change a single byte, so the write
    /// path can skip it entirely. The comparisons are exact-ish (1e-9) rather
    /// than generous on purpose: "close enough to identity" is a judgement
    /// call, and the one thing this property must never do is silently discard
    /// a trim the user can see.
    ///
    /// The cap's threshold is 0.999 and not 1.0 because a cap of 0.999 really
    /// is a no-op in bytes: it only clamps values above 0.999, and 0.999 * 255
    /// rounds to 255, which is what an uncapped 1.0 produces.</summary>
    public bool IsIdentity =>
        Near(GainR, 1.0) && Near(GainG, 1.0) && Near(GainB, 1.0) &&
        Near(Gamma, 1.0) && MaxBrightness >= 0.999;

    static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;

    /// <summary>The transform, computed directly, for ONE channel value. This
    /// is the reference implementation: the lookup table the write path uses
    /// is built out of exactly this call, and the test suite pins the two
    /// against each other. Nothing on the per-frame path may call it, because
    /// Math.Pow per LED per channel per frame at 60 fps is precisely the cost
    /// the table exists to avoid.</summary>
    public byte Map(byte v, double gain)
    {
        double x = v / 255.0;
        // Math.Pow(x, 1.0) returns x bit-for-bit, so the default gamma costs
        // nothing in accuracy even on the paths that do not short-circuit.
        double y = Gamma == 1.0 ? x : Math.Pow(x, Gamma);
        y *= gain;
        if (y > MaxBrightness) y = MaxBrightness;
        // Clamp BEFORE the cast: a gain above 1 can push y past 1.0, and an
        // unchecked (byte) of 256.4 is 0 - a blown-out white LED would go
        // black, which is the single most alarming way for this to fail.
        return (byte)Math.Clamp(y * 255.0 + 0.5, 0.0, 255.0);
    }

    public byte MapR(byte v) => Map(v, GainR);
    public byte MapG(byte v) => Map(v, GainG);
    public byte MapB(byte v) => Map(v, GainB);

    /// <summary>Apply the trim to one color. For the UI's preview swatch and
    /// for callers with a single pixel; frames go through Calibration.Apply.</summary>
    public Rgb Map(Rgb c) => new(MapR(c.R), MapG(c.G), MapB(c.B));

    /// <summary>The 3 x 256 lookup table the write path indexes. One array
    /// rather than three so a frame's three lookups stay in one allocation,
    /// and so publishing a rebuilt table is a single reference swap.</summary>
    internal byte[] BuildTable()
    {
        var t = new byte[768];
        for (int v = 0; v < 256; v++)
        {
            t[v] = Map((byte)v, GainR);
            t[256 + v] = Map((byte)v, GainG);
            t[512 + v] = Map((byte)v, GainB);
        }
        return t;
    }

    /// <summary>A short, stable text form of this trim. Used by the Lian Li
    /// bake signature, which has to notice a trim change the same way it
    /// notices a master-brightness change, or the fans would keep playing an
    /// animation baked from the OLD calibration while every streamed device
    /// showed the new one.</summary>
    public string Fingerprint() => IsIdentity ? "" : FormattableString.Invariant(
        $"{GainR:0.####},{GainG:0.####},{GainB:0.####},{Gamma:0.####},{MaxBrightness:0.####}");
}

/// <summary>What calibration.json holds. Its own file rather than a corner of
/// settings.json or a profile, because this is per-MACHINE hardware trimming:
/// it describes the LEDs sitting on this desk, and it should survive every
/// profile switch, import and taste change the user has.</summary>
public sealed class CalibrationStore
{
    /// <summary>Keyed by device NAME, which is how profiles already key
    /// devices. Names are not unique in principle (two identical fan
    /// controllers) but they are what the user sees and what every other
    /// per-device setting in this app is keyed by, so being consistent beats
    /// being clever here.</summary>
    public Dictionary<string, DeviceCalibration> Devices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-zone trims, nested device name -> zone name -> trim.
    ///
    /// A SEPARATE object rather than extra members on the device entries, and
    /// nested rather than a flattened "Device/Zone" composite key, for two
    /// reasons. Every calibration.json in the field today holds only Devices,
    /// and an absent property leaves this initializer alone, so an old file
    /// loads with exactly the meaning it always had. And the other direction
    /// matters just as much: System.Text.Json ignores members it does not
    /// know, so a file written by this build still loads on the build before
    /// it, minus the zone trims it cannot honour. Zone names contain spaces,
    /// digits and brackets ("AIO Fans 1+2 (Header 2)"), so a composite key
    /// would need a separator no zone name may contain, and there is no such
    /// character.</summary>
    public Dictionary<string, Dictionary<string, DeviceCalibration>> Zones { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>The live calibration: the settings, the tables built from them,
/// and the one call the write boundary makes.</summary>
public static class Calibration
{
    static string PathName => AppPaths.Config("calibration.json");

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    /// <summary>Guards every WRITE to the settings and the table map. Readers
    /// never take it: they read one reference out of _tables and index a table
    /// that is never mutated after it is published.</summary>
    static readonly object _write = new();

    /// <summary>The device-level settings, keyed by name. Only ever replaced
    /// or mutated under _write.</summary>
    static Dictionary<string, DeviceCalibration> _settings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The zone-level settings, device name -> zone name -> trim.
    /// An inner map is REMOVED once its last zone goes, never left empty, so
    /// "_zoneSettings.Count == 0" really does mean no zone anywhere carries a
    /// trim and Rebuild can go back to the free path on it.</summary>
    static Dictionary<string, Dictionary<string, DeviceCalibration>> _zoneSettings
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The published hot-path tables: every device and zone that has
    /// a trim, each as its own 768-byte lookup. One object holding both maps
    /// rather than two fields, so a rebuild publishes device and zone tables
    /// in a single reference swap: two volatile fields could be read either
    /// side of a change and a plan built from a new device map and an old zone
    /// map would be a mixture that never existed.</summary>
    sealed class CalibrationTables
    {
        public required Dictionary<string, byte[]> Devices { get; init; }
        public required Dictionary<string, Dictionary<string, byte[]>> Zones { get; init; }
    }

    /// <summary>The hot-path tables. NULL when nothing on this machine is
    /// calibrated, which is the default state and the state the vast majority
    /// of installs stay in - and a null check is cheaper than even a failed
    /// dictionary lookup, so an uncalibrated desk pays almost nothing per
    /// frame.
    ///
    /// Never mutated after publication: a change builds a whole new object and
    /// swaps it in, so an effect worker reading it mid-swap sees either the
    /// old tables or the new ones and never a half-rehashed dictionary. That
    /// is why there is no lock on the read path.</summary>
    static volatile CalibrationTables? _tables;

    static int _version;

    /// <summary>Bumped on every change. The effect engine caches a pre-scaled
    /// static base across frames and has to know when that cache went stale;
    /// comparing this integer is how it finds out without re-transforming the
    /// whole device every frame.</summary>
    public static int Version => Volatile.Read(ref _version);

    /// <summary>A device's zone layout changed under us. Plans are cached per
    /// device instance and only revalidated on a Version bump, so a driver that
    /// rebuilds LedCount and Zones at runtime (the Lian Li hub hot-reloads its
    /// layout file) would otherwise keep trimming the OLD offsets forever - the
    /// user's zone trim quietly landing on the wrong LEDs until the next
    /// calibration edit or restart. Bumping here also republishes the driver's
    /// new arrays: the increment is the release the readers pair with.</summary>
    public static void NoteLayoutChanged() => Interlocked.Increment(ref _version);

    // Loading in the static constructor rather than from an explicit startup
    // call, because the write path (effect workers, the applier) can be the
    // first thing to touch this class and it must not be able to run against
    // an unloaded store. Everything inside Load is caught, so this can never
    // throw a TypeInitializationException at a device write.
    static Calibration() => Load();

    /*-----------------------------------------------------*\
    | The per-frame path. Nothing here allocates.            |
    \*-----------------------------------------------------*/

    /// <summary>Apply <paramref name="device"/>'s DEVICE-LEVEL trim to a
    /// buffer in place, with no zone awareness: a name is all this overload
    /// has, and a name cannot say which part of the device a buffer covers.
    /// It is for callers holding a name and nothing else (the aid's swatch
    /// maths, the tests). Everything writing a real frame goes through the
    /// IRgbDevice form below, via Master.Finish.
    ///
    /// Call ONLY on frames or clones about to be written to hardware, never on
    /// stored state - the same rule as Master.Scale, for the same reason.</summary>
    public static void Apply(string device, Rgb[] buf) => Apply(device, buf, 0, buf.Length);

    /// <summary>Apply to one range in place. The range form exists for the
    /// engine's compose path, which lays each channel's slice over an already
    /// transformed static base and must transform only the slices it just
    /// wrote - transforming the base twice would apply gamma twice.</summary>
    public static void Apply(string device, Rgb[] buf, int offset, int count)
    {
        // Two short circuits, in order of how often they fire. Neither is a
        // micro-optimisation: they are what makes a default install produce
        // byte-identical frames to the version of this app that had no
        // calibration at all.
        var tables = _tables;
        if (tables == null) return;                                   // nothing on this machine is calibrated
        if (!tables.Devices.TryGetValue(device, out var t)) return;    // this device is not calibrated

        int end = Math.Min(buf.Length, offset + count);
        MapRange(buf, Math.Max(0, offset), end, t);
    }

    /// <summary>THE frame path: apply this device's trims to a buffer, zones
    /// included, in place.
    ///
    /// <paramref name="deviceOffset"/> is the DEVICE LED index that
    /// buf[bufOffset] corresponds to, and it is the whole reason this overload
    /// exists. Two callers hand us a zone's own slice, whose index 0 is not
    /// the device's LED 0, and without being told where the buffer sits there
    /// is no way to know which zone's trim its pixels are under.
    ///
    /// Same rule as every other Apply: frames and clones on their way to
    /// hardware only, never stored state.</summary>
    public static void Apply(IRgbDevice device, Rgb[] buf, int bufOffset, int count, int deviceOffset)
    {
        // The free path first, before anything at all is touched - not even
        // the plan cache. This is the check that keeps a default install
        // byte-identical to the app that had no calibration in it.
        if (_tables == null || device == null || buf == null) return;
        var plan = PlanFor(device);
        if (plan == null) return;

        // Clamp in BUFFER space first, exactly as the range form has always
        // done (a range lying outside the buffer transforms nothing and does
        // not throw), then carry the clamp across into device space.
        int from = Math.Max(0, bufOffset);
        int to = Math.Min(buf.Length, bufOffset + count);
        if (to <= from) return;

        int dFrom = deviceOffset + (from - bufOffset);
        int dTo = dFrom + (to - from);
        // Device index + shift = buffer index, for the whole range. Held as
        // one addition so the walk below can be written in device space and
        // still index the buffer without a second mapping step.
        int shift = from - dFrom;

        // Walk the range in device space. The zone spans are sorted and
        // non-overlapping (BuildPlan guarantees both), so one pass covers
        // every LED exactly once: each span trims its own overlap with the
        // range, and the gaps between them fall to the device table if there
        // is one. Exactly once is the point - a pixel transformed twice would
        // have its gamma squared.
        int cursor = dFrom;
        var zones = plan.Zones;
        for (int i = 0; i < zones.Length && cursor < dTo; i++)
        {
            var z = zones[i];
            if (z.End <= cursor) continue;    // entirely before the range
            if (z.Start >= dTo) break;        // sorted, so nothing after this one can overlap either
            if (plan.Device != null && z.Start > cursor)
                MapRange(buf, cursor + shift, Math.Min(z.Start, dTo) + shift, plan.Device);
            int s = Math.Max(cursor, z.Start), e = Math.Min(dTo, z.End);
            MapRange(buf, s + shift, e + shift, z.Table);
            cursor = e;
        }
        if (plan.Device != null && cursor < dTo)
            MapRange(buf, cursor + shift, dTo + shift, plan.Device);
    }

    /// <summary>Whole-buffer convenience for a caller writing a complete
    /// device frame, where buf[0] really is the device's LED 0.</summary>
    public static void Apply(IRgbDevice device, Rgb[] buf, int deviceOffset = 0)
        => Apply(device, buf, 0, buf?.Length ?? 0, deviceOffset);

    /// <summary>One color through one device's trim (preview swatches, the
    /// "what will this actually look like" readout). Returns the color
    /// unchanged for an uncalibrated device.</summary>
    public static Rgb Apply(string device, Rgb c) => Apply(device, (string?)null, c);

    /// <summary>One color through the trim that governs one ZONE: its own if
    /// it has one, otherwise the device's, otherwise none. The override rule
    /// for a single pixel, for the editor's swatches.
    ///
    /// <paramref name="zone"/> null or empty asks for the device-level trim,
    /// which is what a caller looking at a whole device wants.</summary>
    public static Rgb Apply(string device, string? zone, Rgb c)
    {
        var tables = _tables;
        if (tables == null) return c;
        byte[]? t = null;
        if (!string.IsNullOrEmpty(zone) && tables.Zones.TryGetValue(device, out var zt))
            zt.TryGetValue(zone, out t);
        if (t == null && !tables.Devices.TryGetValue(device, out t)) return c;
        return new Rgb(t[c.R], t[256 + c.G], t[512 + c.B]);
    }

    /// <summary>The inner loop, shared by every Apply so there is one place
    /// where a table is indexed. Bounds are buffer indices, already clamped.</summary>
    static void MapRange(Rgb[] buf, int from, int to, byte[] t)
    {
        for (int i = from; i < to; i++)
        {
            var c = buf[i];
            buf[i] = new Rgb(t[c.R], t[256 + c.G], t[512 + c.B]);
        }
    }

    /*-----------------------------------------------------*\
    | The per-device plan, and its cache.                    |
    \*-----------------------------------------------------*/

    /// <summary>One zone's LED range in DEVICE space, paired with the table
    /// that trims it. Half-open: [Start, End).</summary>
    readonly record struct ZoneSpan(int Start, int End, byte[] Table);

    /// <summary>Everything the write path needs to know about one device,
    /// resolved once instead of per frame: its own table (null when only its
    /// zones are trimmed) and the trimmed zones, sorted by offset and
    /// guaranteed not to overlap.</summary>
    sealed class DevicePlan
    {
        public required int Version { get; init; }
        public required byte[]? Device { get; init; }
        public required ZoneSpan[] Zones { get; init; }

        /// <summary>Nothing on this device is trimmed. Cached anyway, so an
        /// untrimmed device in a calibrated rig does not rebuild a plan every
        /// frame just to discover it has none.</summary>
        public bool IsEmpty => Device == null && Zones.Length == 0;
    }

    /// <summary>Plans, keyed by device INSTANCE.
    ///
    /// Keying by instance is what makes this sound AND what keeps it free.
    /// ADDING_A_DEVICE.md makes LedCount and Zones immutable for a device
    /// object's lifetime, and Zones returns the same instance every time, so a
    /// plan built from one device object can never be invalidated by that
    /// object changing shape underneath it: the only thing that can stale a
    /// plan is a calibration change, and Version catches that. A weak table
    /// also means a device that is unplugged and disposed takes its plan with
    /// it rather than pinning a name-keyed entry forever.</summary>
    static readonly ConditionalWeakTable<IRgbDevice, DevicePlan> _plans = new();

    /// <summary>This device's plan, or null when nothing trims it. Per frame
    /// this is one version read, one weak-table lookup and an int compare, and
    /// it allocates nothing until a trim actually changes.</summary>
    static DevicePlan? PlanFor(IRgbDevice device)
    {
        // Version BEFORE tables, deliberately. A change between the two reads
        // then gives us tables NEWER than the stamp we cache, so the next
        // frame sees the mismatch and rebuilds; reading them the other way
        // round could stamp an old plan as current and leave it there.
        int ver = Version;
        var tables = _tables;
        if (tables == null) return null;

        if (!_plans.TryGetValue(device, out var plan) || plan.Version != ver)
        {
            plan = BuildPlan(device, tables, ver);
            // Two threads may build the same plan concurrently. Both produce
            // the same answer from the same published tables, so last write
            // wins is not merely tolerable, it is a no-op.
            _plans.AddOrUpdate(device, plan);
        }
        return plan.IsEmpty ? null : plan;
    }

    static DevicePlan BuildPlan(IRgbDevice device, CalibrationTables tables, int version)
    {
        tables.Devices.TryGetValue(device.Name, out byte[]? deviceTable);

        var spans = Array.Empty<ZoneSpan>();
        var declared = device.Zones;
        if (declared != null && declared.Count > 0
            && tables.Zones.TryGetValue(device.Name, out var zoneTables) && zoneTables.Count > 0)
        {
            var list = new List<ZoneSpan>(zoneTables.Count);
            // Driven by the DEVICE's zones, not by the file's: the file is
            // keyed by zone name and only the device knows where that name
            // sits. A stored trim for a zone this device does not have (a
            // renamed zone, a board swapped for a different model) simply
            // finds no range and is skipped rather than trimming something
            // arbitrary. ZoneKeys is what makes that lookup hit ONE zone: two
            // zones sharing a name would both match a single stored trim.
            var keys = ZoneKeys(declared);
            for (int i = 0; i < declared.Count; i++)
            {
                var z = declared[i];
                if (z == null || z.Count <= 0 || z.Offset < 0) continue;
                if (zoneTables.TryGetValue(keys[i], out var t))
                    list.Add(new ZoneSpan(z.Offset, z.Offset + z.Count, t));
            }
            // Whole-fan zones contain inner/outer-ring zones. Partition their
            // boundaries and choose the smallest covering zone for each span.
            // Equal ranges retain declaration order; no pixel is mapped twice.
            var boundaries = list.SelectMany(s => new[] { s.Start, s.End }).Distinct().Order().ToArray();
            var kept = new List<ZoneSpan>(list.Count);
            for (int i = 0; i + 1 < boundaries.Length; i++)
            {
                int start = boundaries[i], end = boundaries[i + 1];
                ZoneSpan? winner = null;
                foreach (var candidate in list)
                    if (candidate.Start <= start && candidate.End >= end
                        && (winner == null || candidate.End - candidate.Start < winner.Value.End - winner.Value.Start))
                        winner = candidate;
                if (winner is ZoneSpan selected) kept.Add(new ZoneSpan(start, end, selected.Table));
            }
            if (kept.Count > 0) spans = kept.ToArray();
        }

        return new DevicePlan { Version = version, Device = deviceTable, Zones = spans };
    }

    /// <summary>True when any device or zone at all has a trim. For the UI's
    /// "you have calibration active" hint and for cheap assertions.</summary>
    public static bool AnyCalibrated => _tables != null;

    /*-----------------------------------------------------*\
    | The settings API - what the view model calls.          |
    \*-----------------------------------------------------*/

    /// <summary>This device's trim, as a COPY. A copy because the caller is a
    /// slider: it will mutate what it is handed, and a live object would apply
    /// each half-finished drag state to the hardware through a table that had
    /// not been rebuilt for it. Unknown device = a fresh default, never null,
    /// so the UI never has to special-case "never calibrated".</summary>
    public static DeviceCalibration For(string device)
    {
        lock (_write)
            return _settings.TryGetValue(device, out var c) ? c.Clone() : new DeviceCalibration();
    }

    /// <summary>Install a trim for a device and rebuild its table. Normalizes
    /// first, so the UI cannot install a NaN and a caller does not have to
    /// remember to clamp. Does NOT write the file: a slider drag would
    /// otherwise be a few hundred disk writes. Call Save() when the drag
    /// ends.</summary>
    public static void Set(string device, DeviceCalibration? cal)
    {
        if (string.IsNullOrWhiteSpace(device) || cal == null) return;
        var copy = cal.Clone();
        copy.Normalize();
        lock (_write)
        {
            // An identity trim is REMOVED rather than stored, which is what
            // keeps the short circuit honest: a user who drags a slider and
            // drags it back is genuinely uncalibrated again, table lookup and
            // all, rather than paying for a table that does nothing.
            //
            // And a Set that changes nothing rebuilds nothing. Every rebuild
            // bumps Version, which stales every cached DevicePlan on the
            // machine, so each no-op call made every effect worker re-enter
            // BuildPlan and re-transform its whole device on the next frame.
            // SetZone already had this guard through RemoveZone's bool; this
            // side did not.
            if (copy.IsIdentity)
            {
                if (!_settings.Remove(device)) return;
            }
            else
            {
                if (_settings.TryGetValue(device, out var had) && had.Fingerprint() == copy.Fingerprint()) return;
                _settings[device] = copy;
            }
            Rebuild();
        }
    }

    /// <summary>This ZONE's own trim, as a copy, or NULL when it has none and
    /// is therefore following the device. Null is the answer the editor needs:
    /// "has its own" and "shows the same numbers as its device" are different
    /// states and only one of them survives a change to the device trim.</summary>
    public static DeviceCalibration? ForZone(string device, string zone)
    {
        if (string.IsNullOrWhiteSpace(device) || string.IsNullOrWhiteSpace(zone)) return null;
        lock (_write)
            return _zoneSettings.TryGetValue(device, out var zones) && zones.TryGetValue(zone, out var c)
                ? c.Clone() : null;
    }

    /// <summary>True when this zone carries its own trim rather than following
    /// its device.</summary>
    public static bool HasZoneTrim(string device, string zone) => ForZone(device, zone) != null;

    /// <summary>The trim that actually governs a zone, as a copy: its own if
    /// it has one, else its device's, else defaults. This is the override rule
    /// itself, and it is what the sliders load, so a zone that is following
    /// its device opens showing what it is really doing rather than a row of
    /// 1.0s that would be a lie.
    ///
    /// <paramref name="zone"/> null or empty asks about the device itself.</summary>
    public static DeviceCalibration Effective(string device, string? zone)
    {
        if (!string.IsNullOrWhiteSpace(zone))
        {
            var own = ForZone(device, zone!);
            if (own != null) return own;
        }
        return For(device);
    }

    /// <summary>The trim a zone inherits in the physical layout. Smaller
    /// containing zones win; equal ranges keep declaration order, matching
    /// BuildPlan. A parent editor still edits its own scope rather than one of
    /// its smaller, independently overridden children.</summary>
    public static DeviceCalibration Effective(IRgbDevice device, string? zone)
    {
        if (string.IsNullOrWhiteSpace(zone)) return For(device.Name);
        var declared = device.Zones;
        if (declared == null || declared.Count == 0) return Effective(device.Name, zone);
        var keys = ZoneKeys(declared);
        int targetIndex = Array.FindIndex(keys, key => string.Equals(key, zone, StringComparison.OrdinalIgnoreCase));
        if (targetIndex < 0) return Effective(device.Name, zone);
        var target = declared[targetIndex];
        if (target == null || target.Offset < 0 || target.Count <= 0) return For(device.Name);
        lock (_write)
        {
            DeviceCalibration? selected = null;
            int smallest = int.MaxValue;
            if (_zoneSettings.TryGetValue(device.Name, out var settings))
                for (int i = 0; i < declared.Count; i++)
                {
                    var candidate = declared[i];
                    if (candidate == null || candidate.Offset < 0 || candidate.Count <= 0
                        || candidate.Offset > target.Offset
                        || (long)candidate.Offset + candidate.Count < (long)target.Offset + target.Count
                        || candidate.Count >= smallest || !settings.TryGetValue(keys[i], out var trim)) continue;
                    selected = trim;
                    smallest = candidate.Count;
                }
            return selected?.Clone() ?? For(device.Name);
        }
    }

    /// <summary>UI reference swatch using the same inherited scope as its sliders.</summary>
    public static Rgb Apply(IRgbDevice device, string? zone, Rgb color) => Effective(device, zone).Map(color);

    /// <summary>Install a trim for one zone of a device, overriding whatever
    /// the device-level trim says for that zone's LEDs. Same contract as Set:
    /// normalizes, rebuilds the tables, does not touch the file.</summary>
    public static void SetZone(string device, string zone, DeviceCalibration? cal)
    {
        if (string.IsNullOrWhiteSpace(device) || string.IsNullOrWhiteSpace(zone) || cal == null) return;
        var copy = cal.Clone();
        copy.Normalize();
        lock (_write)
        {
            if (copy.IsIdentity)
            {
                // Default means FOLLOW THE DEVICE, so a zone dragged back to
                // default loses its entry entirely rather than being stored as
                // an explicit no-op. See the override rule at the top of this
                // file for why that is the behaviour and not a shortcut.
                if (!RemoveZone(device, zone)) return;
            }
            else
            {
                if (!_zoneSettings.TryGetValue(device, out var zones))
                    _zoneSettings[device] = zones = new Dictionary<string, DeviceCalibration>(StringComparer.OrdinalIgnoreCase);
                zones[zone] = copy;
            }
            Rebuild();
        }
    }

    /// <summary>Back to following the device for one zone.</summary>
    public static void ResetZone(string device, string zone)
    {
        lock (_write)
        {
            if (string.IsNullOrEmpty(device) || string.IsNullOrEmpty(zone)) return;
            if (!RemoveZone(device, zone)) return;
            Rebuild();
        }
    }

    /// <summary>Drop one zone entry and the device's inner map with it when
    /// that was the last one. The caller holds _write. Returns whether
    /// anything was actually removed, so callers can skip a rebuild.</summary>
    static bool RemoveZone(string device, string zone)
    {
        if (!_zoneSettings.TryGetValue(device, out var zones) || !zones.Remove(zone)) return false;
        if (zones.Count == 0) _zoneSettings.Remove(device);
        return true;
    }

    /// <summary>Back to defaults for one device, ZONES INCLUDED. "Reset this
    /// device" has to mean the device is sent exactly the color the user
    /// picked, and a surviving zone override would leave part of it still
    /// trimmed by a control the user just cleared.</summary>
    public static void Reset(string device)
    {
        lock (_write)
        {
            if (string.IsNullOrEmpty(device)) return;
            bool any = _settings.Remove(device);
            if (_zoneSettings.Remove(device)) any = true;
            if (!any) return;
            Rebuild();
        }
    }

    /// <summary>Back to defaults for everything. Also what the test suite uses
    /// to hand the rest of the harness a clean global.</summary>
    public static void ResetAll()
    {
        lock (_write)
        {
            if (_settings.Count == 0 && _zoneSettings.Count == 0 && _tables == null) return;
            _settings = new Dictionary<string, DeviceCalibration>(StringComparer.OrdinalIgnoreCase);
            _zoneSettings = new Dictionary<string, Dictionary<string, DeviceCalibration>>(StringComparer.OrdinalIgnoreCase);
            Rebuild();
        }
    }

    /// <summary>The device names that currently carry a trim of their own.</summary>
    public static IReadOnlyList<string> CalibratedNames
    {
        get { lock (_write) return _settings.Keys.ToList(); }
    }

    /// <summary>The store key for each of a device's zones, in declaration
    /// order and one per entry, so a caller can index straight into it.
    ///
    /// A trim is stored against a NAME, and a name is not something the app
    /// controls: GigabyteIt5711 takes it from a hand-edited hardware.json and
    /// OpenRgbDevice takes it from whatever the server says. Two headers called
    /// the same thing used to produce two rows in the editor that wrote one
    /// store entry, and one trim that was then applied to BOTH ranges - so
    /// trimming the header you could see also trimmed the one you could not.
    /// The second and later claimants of a name get a numbered suffix, which
    /// keeps the file readable and leaves every device whose names were already
    /// distinct (all of them, in every rig we have seen) storing exactly what
    /// it stored before. No migration, because nothing that was working
    /// changes.</summary>
    public static string[] ZoneKeys(IReadOnlyList<RgbZone>? zones)
    {
        if (zones == null || zones.Count == 0) return Array.Empty<string>();
        var keys = new string[zones.Count];
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < zones.Count; i++)
        {
            // A nameless zone still needs a key, and its position is the only
            // thing left to name it by.
            string name = zones[i]?.Name is string s && !string.IsNullOrWhiteSpace(s) ? s : $"Zone {i + 1}";
            // The loop rather than a count: a device declaring "Header",
            // "Header (2)" and "Header" would otherwise hand the third zone the
            // key the second one already holds.
            string key = name;
            for (int n = 2; !taken.Add(key); n++) key = $"{name} ({n})";
            keys[i] = key;
        }
        return keys;
    }

    /// <summary>The zones of one device that carry a trim of their own.</summary>
    public static IReadOnlyList<string> CalibratedZones(string device)
    {
        if (string.IsNullOrEmpty(device)) return Array.Empty<string>();
        lock (_write)
            return _zoneSettings.TryGetValue(device, out var zones) ? zones.Keys.ToList() : Array.Empty<string>();
    }

    /// <summary>A device's trims as text, ZONES INCLUDED, or "" when it has
    /// none. See DeviceCalibration.Fingerprint for why the bake path needs
    /// this: the zone trims are just as much a part of what the baked frames
    /// were computed from as the device trim is, so leaving them out would let
    /// the fans keep replaying an animation baked from a zone trim the user
    /// has already changed.</summary>
    public static string Fingerprint(string device)
    {
        if (string.IsNullOrEmpty(device)) return "";
        lock (_write)
        {
            string self = _settings.TryGetValue(device, out var c) ? c.Fingerprint() : "";
            if (!_zoneSettings.TryGetValue(device, out var zones) || zones.Count == 0) return self;
            var sb = new System.Text.StringBuilder(self);
            // Ordered, because a dictionary's order is not a promise and a
            // signature that reshuffles itself would re-bake the fans for no
            // reason at all.
            foreach (var pair in zones.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                sb.Append(';').Append(pair.Key).Append('=').Append(pair.Value.Fingerprint());
            return sb.ToString();
        }
    }

    /// <summary>The 768-byte table for one stored trim, built once per trim
    /// OBJECT. A rebuild reruns every entry, and moving one slider on one
    /// device rebuilt every table on the machine: 768 Math.Pow per table, per
    /// tick of the drag. Keyed on the stored instance, which Set and SetZone
    /// replace wholesale whenever the numbers change, so an entry that did not
    /// change keeps its reference and therefore keeps its table. Weak, so a
    /// removed trim is not held alive by its own cache.
    ///
    /// This is sound only because the stored objects are private clones that
    /// nothing mutates after storing - For() and ForZone() hand out copies
    /// precisely so no caller can reach them.</summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DeviceCalibration, byte[]> _tableCache = new();

    static byte[] TableFor(DeviceCalibration c) => _tableCache.GetValue(c, static k => k.BuildTable());

    /// <summary>Rebuild the published tables from the settings. The caller
    /// holds _write.</summary>
    static void Rebuild()
    {
        if (_settings.Count == 0 && _zoneSettings.Count == 0)
        {
            _tables = null;   // back to the free path
        }
        else
        {
            var devices = new Dictionary<string, byte[]>(_settings.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _settings) devices[pair.Key] = TableFor(pair.Value);

            var zones = new Dictionary<string, Dictionary<string, byte[]>>(_zoneSettings.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _zoneSettings)
            {
                var inner = new Dictionary<string, byte[]>(pair.Value.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var z in pair.Value) inner[z.Key] = TableFor(z.Value);
                zones[pair.Key] = inner;
            }
            _tables = new CalibrationTables { Devices = devices, Zones = zones };
        }
        // Every cached DevicePlan is stamped with the version it was built at,
        // so this one increment is what stales all of them at once. There is
        // no plan cache to walk and nothing to invalidate by hand.
        Interlocked.Increment(ref _version);
    }

    /*-----------------------------------------------------*\
    | Persistence.                                           |
    \*-----------------------------------------------------*/

    /// <summary>Read calibration.json, or start from nothing when there is
    /// none. Deliberately does NOT write a defaults file on first run the way
    /// hardware.json does: an empty trim file is not something anyone wants to
    /// hand-edit, and the file existing at all is a useful signal that this
    /// machine has actually been calibrated.
    ///
    /// A file that cannot be parsed is copied aside and the defaults take
    /// over. A corrupt trim file must NEVER stop the app or throw into a
    /// device write - the worst outcome allowed here is "the desk looks like
    /// it did before you calibrated it".</summary>
    public static void Load()
    {
        var loaded = ReadFile();
        lock (_write)
        {
            _settings = loaded.Devices;
            _zoneSettings = loaded.Zones;
            Rebuild();
        }
    }

    /// <summary>Load again from disk, discarding whatever is live. For the
    /// setup importer (which replaces the file underneath a running app) and
    /// for the test suite.</summary>
    public static void Reload() => Load();

    /// <summary>What one load produced: the device-level trims and the
    /// zone-level ones. A tuple rather than the store object itself because
    /// the store's dictionaries come out of the deserializer with the DEFAULT
    /// comparer no matter what the property initializer says, and every lookup
    /// in this class is case-insensitive.</summary>
    static (Dictionary<string, DeviceCalibration> Devices,
            Dictionary<string, Dictionary<string, DeviceCalibration>> Zones) ReadFile()
    {
        var empty = (new Dictionary<string, DeviceCalibration>(StringComparer.OrdinalIgnoreCase),
                     new Dictionary<string, Dictionary<string, DeviceCalibration>>(StringComparer.OrdinalIgnoreCase));
        string path;
        // Even resolving the path touches AppPaths, and this runs from a static
        // constructor: a throw here would surface as a TypeInitializationException
        // at whatever device write happened to touch the class first.
        try { path = PathName; } catch { return empty; }

        bool exists = false;
        try { exists = File.Exists(path); } catch { }
        if (!exists) return empty;

        // Read and parse are split, as everywhere else in this codebase: a
        // read failure is a locked or vanishing file, not a corrupt one, and
        // there is nothing to copy aside for it.
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex)
        {
            Log.Warn("calibration", $"calibration.json could not be read: {ex.Message} - running untrimmed");
            return empty;
        }

        CalibrationStore? store;
        try { store = JsonSerializer.Deserialize<CalibrationStore>(text, Opts); }
        catch (Exception ex)
        {
            KeepCorruptCopy(path, $"unreadable ({ex.Message})");
            return empty;
        }
        // The literal text `null` parses without throwing and is just as
        // useless as a syntax error.
        if (store == null) { KeepCorruptCopy(path, "the file is a JSON null"); return empty; }
        if (store.Devices == null) { KeepCorruptCopy(path, "no Devices object"); return empty; }

        var result = new Dictionary<string, DeviceCalibration>(StringComparer.OrdinalIgnoreCase);
        bool repaired = false;
        foreach (var pair in store.Devices)
        {
            // A hand-written file can contain a null entry or a blank key, and
            // both would otherwise NRE somewhere far away from here.
            if (string.IsNullOrWhiteSpace(pair.Key)) { repaired = true; continue; }
            if (pair.Value == null) { repaired = true; continue; }
            if (pair.Value.Normalize()) repaired = true;
            // An identity entry is dropped rather than kept, so a file full of
            // "1.0 everywhere" leaves the short circuit intact.
            if (pair.Value.IsIdentity) continue;
            result[pair.Key] = pair.Value;
        }

        // Zones are read the same forgiving way, and a file with no Zones
        // object at all is simply a file from before zones existed: it loads
        // with exactly the meaning it always had, every LED on the device
        // under the device entry. Missing is NOT corrupt here, which is why an
        // absent or null Zones does not go down the KeepCorruptCopy path the
        // way an absent Devices does.
        var zoneResult = new Dictionary<string, Dictionary<string, DeviceCalibration>>(StringComparer.OrdinalIgnoreCase);
        int zoneCount = 0;
        foreach (var pair in store.Zones ?? new Dictionary<string, Dictionary<string, DeviceCalibration>>())
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null) { repaired = true; continue; }
            var inner = new Dictionary<string, DeviceCalibration>(StringComparer.OrdinalIgnoreCase);
            foreach (var z in pair.Value)
            {
                if (string.IsNullOrWhiteSpace(z.Key)) { repaired = true; continue; }
                if (z.Value == null) { repaired = true; continue; }
                if (z.Value.Normalize()) repaired = true;
                // An identity zone entry means "follow the device", which is
                // what NO entry already means, so it is dropped here for the
                // same reason Set drops it.
                if (z.Value.IsIdentity) continue;
                inner[z.Key] = z.Value;
            }
            // Never store an empty inner map: Rebuild reads _zoneSettings.Count
            // to decide whether the free path is still available.
            if (inner.Count == 0) continue;
            zoneResult[pair.Key] = inner;
            zoneCount += inner.Count;
        }

        if (repaired)
            Log.Warn("calibration", "calibration.json contained values outside the allowed range; they were clamped in memory");
        if (result.Count > 0)
            Log.Info("calibration", $"trimming {result.Count} device(s): {string.Join(", ", result.Keys)}");
        if (zoneCount > 0)
            Log.Info("calibration", $"trimming {zoneCount} zone(s) on {zoneResult.Count} device(s): {string.Join(", ", zoneResult.Keys)}");
        return (result, zoneResult);
    }

    static void KeepCorruptCopy(string path, string why)
    {
        string backup = path + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
        try { File.Copy(path, backup, overwrite: true); }
        catch (Exception ex) { backup = $"(copy failed: {ex.Message})"; }
        Log.Warn("calibration", $"calibration.json {why} - running untrimmed; original kept at {backup}");
    }

    /// <summary>Persist the current trims. Call when a slider drag ENDS, not
    /// while it moves.</summary>
    public static void Save()
    {
        var snapshot = new CalibrationStore
        {
            Devices = new Dictionary<string, DeviceCalibration>(StringComparer.OrdinalIgnoreCase),
            Zones = new Dictionary<string, Dictionary<string, DeviceCalibration>>(StringComparer.OrdinalIgnoreCase),
        };
        lock (_write)
        {
            foreach (var pair in _settings) snapshot.Devices[pair.Key] = pair.Value.Clone();
            foreach (var pair in _zoneSettings)
            {
                var inner = new Dictionary<string, DeviceCalibration>(StringComparer.OrdinalIgnoreCase);
                foreach (var z in pair.Value) inner[z.Key] = z.Value.Clone();
                snapshot.Zones[pair.Key] = inner;
            }
        }

        try { SafeFile.WriteAllText(PathName, JsonSerializer.Serialize(snapshot, Opts)); }
        catch (Exception ex) { Log.Warn("calibration", $"calibration.json could not be written: {ex.Message}"); }
    }
}

/// <summary>The reference patches the calibration aid drives every device to
/// at once. Sliders on their own are useless for this job: nobody can trim a
/// keyboard to match fans they cannot see at the same time, so the aid puts
/// the SAME requested color on everything and lets the eye do the comparing.
///
/// White and mid grey are the two that matter and the two the review asked
/// for. White shows the balance error at the top of the range; mid grey shows
/// the gamma error, which white cannot show at all because every device is
/// pinned at maximum there. The three primaries are included because they are
/// how you find out WHICH channel is wrong once you can see that white is
/// off.</summary>
public enum CalibrationReference
{
    White,
    MidGrey,
    QuarterGrey,
    Red,
    Green,
    Blue,
}

/// <summary>The five things a user can drag on the calibration screen. Named
/// so the UI can ask whether one of them does anything for the patch that is
/// currently up (see CalibrationReferences.Affects).</summary>
public enum CalibrationControl
{
    GainR,
    GainG,
    GainB,
    Gamma,
    Cap,
}

public static class CalibrationReferences
{
    /// <summary>The order the aid cycles through. White first because it is
    /// where the mismatch is most obvious, then mid grey where gamma shows.</summary>
    public static readonly CalibrationReference[] Cycle =
    {
        CalibrationReference.White,
        CalibrationReference.MidGrey,
        CalibrationReference.QuarterGrey,
        CalibrationReference.Red,
        CalibrationReference.Green,
        CalibrationReference.Blue,
    };

    /// <summary>The color a reference asks every device for. Mid grey is 128
    /// and not a perceptual 187: this is a request in device units, and the
    /// whole point is to compare what different devices do with the SAME
    /// number.</summary>
    /// <summary>What "white" actually asks for. NOT 255, for two reasons.
    ///
    /// Every LED on every device with all three emitters at maximum is the
    /// heaviest thing this app can ask a rig for, and the calibration screen is
    /// the one place a user sits on that state for minutes while they fiddle. A
    /// board header, a DRAM stick and a GPU logo have very different opinions
    /// about how long they enjoy it, and none of them is worth finding out
    /// about here.
    ///
    /// The second reason is that full white is the wrong test signal anyway.
    /// Gain runs to 2.0, and on a channel already at 255 every gain above 1.0
    /// clips straight back to 255: the slider moves and nothing happens. Gamma
    /// is worse, because 1.0^gamma is 1.0 at every gamma, so on a full-scale
    /// patch that control does nothing whatsoever. At 60% both have room to
    /// work in both directions, which is what makes white useful for balance
    /// at all.</summary>
    public const double WhiteLevel = 0.6;

    static Rgb Grey(double level)
    {
        byte v = (byte)Math.Clamp(Math.Round(level * 255.0), 0, 255);
        return new Rgb(v, v, v);
    }

    public static Rgb ColorOf(CalibrationReference r) => r switch
    {
        CalibrationReference.White => Grey(WhiteLevel),
        CalibrationReference.MidGrey => new Rgb(128, 128, 128),
        CalibrationReference.QuarterGrey => new Rgb(64, 64, 64),
        CalibrationReference.Red => new Rgb(255, 0, 0),
        CalibrationReference.Green => new Rgb(0, 255, 0),
        CalibrationReference.Blue => new Rgb(0, 0, 255),
        _ => Grey(WhiteLevel),
    };

    public static string LabelOf(CalibrationReference r) => r switch
    {
        CalibrationReference.White => $"White ({WhiteLevel * 100:0}%)",
        CalibrationReference.MidGrey => "Mid grey (50%)",
        CalibrationReference.QuarterGrey => "Dark grey (25%)",
        CalibrationReference.Red => "Red",
        CalibrationReference.Green => "Green",
        CalibrationReference.Blue => "Blue",
        _ => r.ToString(),
    };

    /// <summary>What a patch is FOR, in a few words. Six patches and five
    /// sliders make thirty combinations and most of them do nothing at all: a
    /// user who picks blue and drags the red slider is entitled to conclude the
    /// app is broken. Saying what each patch answers is half the fix, and
    /// Affects below is the other half.</summary>
    public static string PurposeOf(CalibrationReference r) => r switch
    {
        CalibrationReference.White => "for balance",
        CalibrationReference.MidGrey => "for gamma",
        CalibrationReference.QuarterGrey => "for gamma, low end",
        _ => "one channel on its own",
    };

    /// <summary>Can this control change what the device is showing for this
    /// patch? Pure arithmetic, from the transform in DeviceCalibration:
    ///
    ///   out = min((v/255)^gamma * gain, cap)
    ///
    /// A gain multiplies one channel, so it does nothing when that channel is
    /// already zero: red and green gain are inert on a blue patch. Gamma pins
    /// both ends of the curve (0^g is 0 and 1^g is 1), so it does nothing
    /// unless some channel sits strictly between them, which rules out the
    /// saturated primaries. The cap is a ceiling, so it can only bite on a
    /// channel that is lit at all.</summary>
    public static bool Affects(CalibrationReference r, CalibrationControl control)
    {
        var c = ColorOf(r);
        bool anyLit = c.R > 0 || c.G > 0 || c.B > 0;
        bool anyMidtone = Midtone(c.R) || Midtone(c.G) || Midtone(c.B);
        return control switch
        {
            CalibrationControl.GainR => c.R > 0,
            CalibrationControl.GainG => c.G > 0,
            CalibrationControl.GainB => c.B > 0,
            CalibrationControl.Gamma => anyMidtone,
            CalibrationControl.Cap => anyLit,
            _ => true,
        };

        static bool Midtone(byte v) => v > 0 && v < 255;
    }

    /// <summary>One tick of each control's slider - the smallest move a user
    /// can actually make, since the sliders snap. The window sets its tick
    /// frequencies from these, so "one tick" means the same thing in the
    /// arithmetic below as it does under the mouse.</summary>
    public static double StepOf(CalibrationControl control) => control switch
    {
        CalibrationControl.Cap => 0.05,
        _ => 0.1,
    };

    /// <summary>The same question, but asked of the trim the row ACTUALLY has
    /// rather than of the patch alone.
    ///
    /// The patch-only answer above is necessary and not sufficient. The
    /// transform clips at the ceiling, so a channel that is already pinned
    /// there absorbs every further move of its gain: with a gamma of 0.3 and a
    /// gain of 2.0, a 60% white patch leaves the top FORTY PER CENT of the blue
    /// slider doing nothing whatsoever, and the screen used to present it as a
    /// live control. "The slider moves and nothing happens" is the single most
    /// reliable way to convince someone an app is broken.
    ///
    /// Answered numerically rather than by rule: nudge the control one tick
    /// each way and see whether any output BYTE moves. That covers clipping,
    /// a dead channel and a pinned gamma with one test, and it cannot drift
    /// away from the transform because it calls it.</summary>
    public static bool Affects(CalibrationReference r, CalibrationControl control, DeviceCalibration? cal)
    {
        if (!Affects(r, control)) return false;
        if (cal == null) return true;

        var asked = ColorOf(r);
        var baseline = cal.Map(asked);
        double step = StepOf(control);
        for (int dir = -1; dir <= 1; dir += 2)
        {
            var probe = cal.Clone();
            Nudge(probe, control, dir * step);
            probe.Normalize();          // a nudge past the end is not a move
            var got = probe.Map(asked);
            if (got.R != baseline.R || got.G != baseline.G || got.B != baseline.B) return true;
        }
        return false;
    }

    static void Nudge(DeviceCalibration cal, CalibrationControl control, double by)
    {
        switch (control)
        {
            case CalibrationControl.GainR: cal.GainR += by; break;
            case CalibrationControl.GainG: cal.GainG += by; break;
            case CalibrationControl.GainB: cal.GainB += by; break;
            case CalibrationControl.Gamma: cal.Gamma += by; break;
            case CalibrationControl.Cap:   cal.MaxBrightness += by; break;
        }
    }

    /// <summary>Why ONE control is doing nothing right now: a lowercase clause,
    /// empty when the control does apply. The caller supplies the sentence
    /// around it. For a group of sliders use InertNote instead.</summary>
    public static string InertBecause(CalibrationReference r, CalibrationControl control, DeviceCalibration? cal = null)
    {
        var kind = KindOf(r, control, cal);
        return kind == InertKind.None ? "" : Clause(kind, new List<string> { Channel(control) }, r, cal);
    }

    /// <summary>The whole dimmed-slider note for a group of controls, in one
    /// sentence, or empty when they all apply.
    ///
    /// Grouped by REASON, not emitted per control. Three gains pinned for the
    /// same reason at the same threshold used to print three clauses differing
    /// only in the word red, green or blue - four lines of near-identical text
    /// under three sliders, which reads as noise rather than as help.</summary>
    public static string InertNote(CalibrationReference r, DeviceCalibration? cal, params CalibrationControl[] controls)
    {
        // Keyed on the reason AND its number, so two channels that are both
        // pinned but recover at different gains stay in separate clauses rather
        // than merging under one figure that is wrong for one of them.
        var order = new List<(InertKind Kind, string Detail)>();
        var subjects = new Dictionary<(InertKind, string), List<string>>();

        foreach (var control in controls ?? Array.Empty<CalibrationControl>())
        {
            var kind = KindOf(r, control, cal);
            if (kind == InertKind.None) continue;
            var key = (kind, Detail(kind, r, control, cal));
            if (!subjects.TryGetValue(key, out var names))
            {
                subjects[key] = names = new List<string>();
                order.Add(key);
            }
            names.Add(Channel(control));
        }
        if (order.Count == 0) return "";

        return "Dimmed: " + string.Join("; ", order.Select(k => Clause(k.Kind, subjects[k], r, cal))) + ".";
    }

    enum InertKind { None, NoChannel, GammaNeedsMidtone, NothingLit, PinnedAtCeiling, ClippedCurve, BelowCeiling }

    static InertKind KindOf(CalibrationReference r, CalibrationControl control, DeviceCalibration? cal)
    {
        if (Affects(r, control, cal)) return InertKind.None;
        // The patch is the simpler cause, and the one the user can fix by
        // clicking a different patch, so it wins when both are true.
        if (!Affects(r, control))
            return control switch
            {
                CalibrationControl.Gamma => InertKind.GammaNeedsMidtone,
                CalibrationControl.Cap => InertKind.NothingLit,
                _ => InertKind.NoChannel,
            };
        return control switch
        {
            CalibrationControl.Cap => InertKind.BelowCeiling,
            CalibrationControl.Gamma => InertKind.ClippedCurve,
            _ => InertKind.PinnedAtCeiling,
        };
    }

    /// <summary>The number a clause quotes, where it has one. Also the second
    /// half of the grouping key.</summary>
    static string Detail(InertKind kind, CalibrationReference r, CalibrationControl control, DeviceCalibration? cal) => kind switch
    {
        InertKind.BelowCeiling => $"{Brightest(r, cal)}%",
        InertKind.PinnedAtCeiling => Recovery(r, control, cal),
        _ => "",
    };

    static string Clause(InertKind kind, List<string> names, CalibrationReference r, DeviceCalibration? cal)
    {
        string list = Join(names);
        bool many = names.Count > 1;
        return kind switch
        {
            // "no red or green", never "no red and green", which reads as a
            // complaint about the pair rather than about each of them.
            InertKind.NoChannel => $"this patch has no {Join(names, "or")} in it",
            InertKind.GammaNeedsMidtone => "gamma only bends the middle of the range, so try a grey patch",
            InertKind.NothingLit => "nothing is lit to cap",
            // The threshold, rather than a list of remedies. "Lower the gain,
            // the gamma or Max brightness" was true and useless: it never said
            // how far, which is the only part the reader does not already know.
            InertKind.PinnedAtCeiling =>
                $"{list} {(many ? "are" : "is")} pinned at the ceiling, so nothing changes until "
                + $"{(many ? "they come" : "it comes")} back under "
                + Recovery(r, names.Count == 0 ? CalibrationControl.GainR : ControlOf(names[0]), cal),
            InertKind.ClippedCurve => "gamma has nothing left to bend while this row is clipped at the ceiling",
            InertKind.BelowCeiling => $"nothing here reaches the ceiling, which starts to bite below {Brightest(r, cal)}%",
            _ => "",
        };
    }

    /// <summary>How far a pinned gain must come down before it moves the output
    /// again, walked in real slider ticks rather than solved, so the figure
    /// quoted is one the user can actually land on.</summary>
    static string Recovery(CalibrationReference r, CalibrationControl control, DeviceCalibration? cal)
    {
        // The multiplication sign the sliders themselves show, so the figure in
        // the sentence and the figure above the thumb are the same notation.
        if (cal == null) return "1×";
        double step = StepOf(control);
        var probe = cal.Clone();
        double found = DeviceCalibration.MinGain;
        for (double v = GainOf(cal, control); v >= DeviceCalibration.MinGain; v -= step)
        {
            SetGain(probe, control, v);
            probe.Normalize();
            if (Affects(r, control, probe)) { found = v; break; }
        }
        // One tick above the first value that moves: that is the boundary, and
        // "under 1.2x" is a truer instruction than "at 1.1x".
        return FormattableString.Invariant($"{found + step:0.#}×");

        static double GainOf(DeviceCalibration c, CalibrationControl control) => control switch
        {
            CalibrationControl.GainR => c.GainR,
            CalibrationControl.GainG => c.GainG,
            _ => c.GainB,
        };
        static void SetGain(DeviceCalibration c, CalibrationControl control, double v)
        {
            switch (control)
            {
                case CalibrationControl.GainR: c.GainR = v; break;
                case CalibrationControl.GainG: c.GainG = v; break;
                default: c.GainB = v; break;
            }
        }
    }

    static int Brightest(CalibrationReference r, DeviceCalibration? cal)
    {
        var sent = cal == null ? ColorOf(r) : cal.Map(ColorOf(r));
        int top = Math.Max(sent.R, Math.Max(sent.G, sent.B));
        return (int)Math.Round(top * 100.0 / 255.0);
    }

    /// <summary>"red", "red and green", "red, green and blue".</summary>
    static string Join(List<string> names, string conjunction = "and") => names.Count switch
    {
        0 => "",
        1 => names[0],
        2 => $"{names[0]} {conjunction} {names[1]}",
        _ => string.Join(", ", names.Take(names.Count - 1)) + $" {conjunction} " + names[^1],
    };

    static string Channel(CalibrationControl c) => c switch
    {
        CalibrationControl.GainR => "red",
        CalibrationControl.GainG => "green",
        CalibrationControl.GainB => "blue",
        CalibrationControl.Gamma => "gamma",
        _ => "Max brightness",
    };

    static CalibrationControl ControlOf(string channel) => channel switch
    {
        "red" => CalibrationControl.GainR,
        "green" => CalibrationControl.GainG,
        _ => CalibrationControl.GainB,
    };

    /// <summary>The next reference in the cycle, wrapping. The aid's "show me
    /// the next patch" button is one call.</summary>
    public static CalibrationReference Next(CalibrationReference r)
    {
        int i = Array.IndexOf(Cycle, r);
        return i < 0 ? Cycle[0] : Cycle[(i + 1) % Cycle.Length];
    }
}
