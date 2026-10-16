using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows.Threading;
using UnifiedRgb.App;
using UnifiedRgb.App.Services;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Devices;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Native;
using static UnifiedRgb.Tests.TestHelpers;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| The three items the 2026-10-14 fix pass left open:           |
|                                                              |
|   ENE      a busy SMBus counted as a refused block write     |
|   folder   ProtectedFolder adopting a re-planted junction    |
|   Lian     an unconfirmed upload re-uploaded on every rebake |
|                                                              |
| Stand-ins only, as in FixDrivers: the SMBus is a fake on a    |
| PRIVATE mutex (never Global\Access_SMBUS.HTP.Method, which    |
| the running app and HWiNFO contend on), folders live in temp, |
| and the wireless fans are the fake transmitter with no        |
| receiver.                                                     |
\*-----------------------------------------------------------*/
static class RemainingDriversSuite
{
    public static void Run(Harness t)
    {
        EneHappyPathBytes(t);
        SmbusBlockOutcomes(t);
        EneBusyBusIsNotARefusal(t);
        FolderNeverAdoptsALink(t);
        LianUnconfirmedBackoff(t);
        LightsOffCancelsABakeInFlight(t);
    }

    /*---------------- ENE: the stand-in bus ----------------*/

    /// <summary>An SMBus with ENE sticks on it, recording every transfer it is
    /// asked for (function and the exact input cells) - the bytes that would
    /// have reached the PawnIO module.</summary>
    sealed class RecordingEneBus : PawnSmbus
    {
        public sealed class Dev
        {
            public readonly Dictionary<int, byte> Regs = new();
            public int Sel;
        }
        public readonly Dictionary<byte, Dev> Devices = new();
        public readonly List<(string Fn, ulong[] Input)> Xfers = new();
        public int RefuseBlocksOver = int.MaxValue;   // the host refuses blocks longer than this
        public RecordingEneBus(string mutexName) : base(new PawnIO(), mutexName) { }
        public override string ChipsetName => "recording";

        /// <summary>Called after a transfer is recorded, before it is answered.</summary>
        public Action<ulong[]>? OnXfer;

        private protected override int Xfer(string function, ulong[] input, ulong[] output)
        {
            lock (Xfers) Xfers.Add((function, (ulong[])input.Clone()));
            OnXfer?.Invoke(input);
            if (function != "ioctl_smbus_xfer") return -1;
            byte addr = (byte)input[0];
            bool read = input[1] == 1;
            byte cmd = (byte)input[2];
            ulong proto = input[3];
            if (!Devices.TryGetValue(addr, out var d)) return -1;
            if (read)
            {
                int v;
                if (proto == 1) v = 0;                                          // probe
                else if (cmd is >= 0xA0 and < 0xB0) v = cmd - 0xA0;             // ENE identity window
                else if (cmd == 0x81) v = d.Regs.GetValueOrDefault(d.Sel++);    // data register
                else v = 0;
                output[0] = (ulong)v;
                return 1;
            }
            if (proto == 3 && cmd == 0x00)
            {
                ushort w = (ushort)input[4];
                d.Sel = ((w << 8) & 0xFF00) | (w >> 8);
                return 0;
            }
            if (proto == 2 && cmd == 0x01) { d.Regs[d.Sel++] = (byte)input[4]; return 0; }
            if (proto == 5 && cmd == 0x03)
            {
                var bytes = new byte[40];
                for (int c = 0; c < 5; c++) BitConverter.GetBytes(input[4 + c]).CopyTo(bytes, c * 8);
                if (bytes[0] > RefuseBlocksOver) return -1;
                for (int i = 0; i < bytes[0]; i++) d.Regs[d.Sel++] = bytes[1 + i];
                return 0;
            }
            return 0;
        }

        public void ClearLog() { lock (Xfers) Xfers.Clear(); }
        public List<(string Fn, ulong[] Input)> Log() { lock (Xfers) return Xfers.ToList(); }

        /// <summary>Block lengths of the 0x03 block writes asked for so far.</summary>
        public List<int> Blocks()
        {
            lock (Xfers)
                return Xfers.Where(x => x.Input.Length == 9 && x.Input[2] == 0x03 && x.Input[3] == 5)
                            .Select(x => (int)(x.Input[4] & 0xFF)).ToList();
        }
        public int Bytes() { lock (Xfers) return Xfers.Count(x => x.Input.Length == 5 && x.Input[2] == 0x01 && x.Input[3] == 2); }
    }

    /// <summary>Another tool on the bus: a thread that takes the (private)
    /// SMBus mutex and keeps it until disposed. A thread, because the mutex is
    /// re-entrant for its owner - the test thread holding it would not make
    /// the driver wait at all.</summary>
    sealed class BusHog : IDisposable
    {
        readonly Thread _thread;
        readonly ManualResetEventSlim _release = new(false);
        readonly ManualResetEventSlim _held = new(false);
        volatile bool _waiting;

        public BusHog(string mutexName)
        {
            _thread = new Thread(() =>
            {
                using var m = new Mutex(false, mutexName);
                _waiting = true;
                try { m.WaitOne(); }
                catch (AbandonedMutexException) { }   // still ours: Windows hands an abandoned mutex to the next waiter
                _held.Set();
                _release.Wait(10_000);
                m.ReleaseMutex();
            }) { IsBackground = true, Name = "smbus-hog" };
            _thread.Start();
        }

        /// <summary>Parked in its wait for the bus, or already holding it: from
        /// here a release by the driver hands the mutex to this thread first.</summary>
        public bool Parked(int timeoutMs) => TestHelpers.WaitUntil(
            () => _held.IsSet || (_waiting && (_thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0), timeoutMs);

        public bool Held(int timeoutMs) => _held.Wait(timeoutMs);

        public void Dispose()
        {
            _release.Set();
            _thread.Join(5000);
            _release.Dispose();
            _held.Dispose();
        }
    }

    static string PrivateMutexName() => $"UnifiedRgbTests-smbus-{Guid.NewGuid():N}";

    static RecordingEneBus.Dev EneStick(int leds = 8)
    {
        var d = new RecordingEneBus.Dev();
        d.Regs[0x1C02] = (byte)leds;   // config table: LED count
        return d;
    }

    // The ENE wire contract, written out independently of the driver: what a
    // register select, a data byte and a 0x03 block look like as the module's
    // input cells (addr, WRITE=0, command, protocol, value...).
    static ulong[] Select(byte addr, ushort reg) => new ulong[] { addr, 0, 0x00, 3, (ulong)(((reg << 8) & 0xFF00) | (reg >> 8)) };
    static ulong[] DataByte(byte addr, byte value) => new ulong[] { addr, 0, 0x01, 2, value };
    static ulong[] Block(byte addr, byte[] data)
    {
        var bytes = new byte[40];
        bytes[0] = (byte)data.Length;
        data.CopyTo(bytes, 1);
        var cells = new ulong[9];
        cells[0] = addr; cells[1] = 0; cells[2] = 0x03; cells[3] = 5;
        for (int c = 0; c < 5; c++) cells[4 + c] = BitConverter.ToUInt64(bytes, c * 8);
        return cells;
    }

    /*---------------- ENE: the happy path's bytes, pinned ----------------*/

    /// <summary>Ryan's rig: two ENE sticks at 0x71 and 0x73. The exact transfer
    /// sequence of a first frame (direct-mode enable, then one whole-stick
    /// select + block) and of a changed frame (select + block only), compared
    /// cell for cell with the contract written out above. Recorded against
    /// the driver BEFORE the busy-bus change and unchanged after it.</summary>
    static void EneHappyPathBytes(Harness t)
    {
        t.Section("ENE: the happy path's transfers are byte-for-byte what they were");
        var bus = new RecordingEneBus(PrivateMutexName());
        bus.Devices[0x71] = EneStick();
        bus.Devices[0x73] = EneStick();
        var sticks = EneDram.DetectOn(bus);
        t.Equal(2, sticks.Count, "both sticks are found");
        try
        {
            foreach (var stick in sticks)
            {
                byte addr = stick.Name.Contains("0x71", StringComparison.Ordinal) ? (byte)0x71 : (byte)0x73;
                int n = stick.LedCount;
                var first = Enumerable.Range(0, n).Select(i => new Rgb((byte)(10 + i), (byte)(100 + i), (byte)(200 + i))).ToArray();
                var second = Enumerable.Range(0, n).Select(i => new Rgb((byte)(250 - i), (byte)(3 * i), (byte)(7 + i))).ToArray();
                static byte[] Rbg(Rgb[] f) => f.SelectMany(c => new[] { c.R, c.B, c.G }).ToArray();

                bus.ClearLog();
                t.Check(stick.SetColors(first), $"0x{addr:X2}: the first frame lands");
                var want = new List<ulong[]>
                {
                    Select(addr, 0x8020), DataByte(addr, 0x01),   // direct mode on
                    Select(addr, 0x80A0), DataByte(addr, 0x01),   // apply
                    Select(addr, 0x8100), Block(addr, Rbg(first)),   // the whole stick, R,B,G per LED
                };
                var got = bus.Log();
                t.Check(got.All(x => x.Fn == "ioctl_smbus_xfer"), $"0x{addr:X2}: every transfer is an SMBus transfer");
                t.Check(got.Count == want.Count && got.Zip(want).All(p => p.First.Input.SequenceEqual(p.Second)),
                    $"0x{addr:X2}: first frame = enable, apply, select 0x8100, one {n * 3}-byte block (got {Show(got)})");

                bus.ClearLog();
                t.Check(stick.SetColors(second), $"0x{addr:X2}: a changed frame lands");
                want = new List<ulong[]> { Select(addr, 0x8100), Block(addr, Rbg(second)) };
                got = bus.Log();
                t.Check(got.Count == want.Count && got.Zip(want).All(p => p.First.Input.SequenceEqual(p.Second)),
                    $"0x{addr:X2}: a changed frame = select 0x8100 + one block, nothing else (got {Show(got)})");

                bus.ClearLog();
                t.Check(stick.SetColors(second), $"0x{addr:X2}: the same frame again reports success");
                t.Equal(0, bus.Log().Count, $"0x{addr:X2}: ...and puts nothing on the bus");
            }
        }
        finally { foreach (var s in sticks) s.Dispose(); }
    }

    /*---------------- ENE: busy is not refused ----------------*/

    static void SmbusBlockOutcomes(Harness t)
    {
        t.Section("SMBus: a block write says whether the bus was busy or the host refused it");
        string name = PrivateMutexName();
        var bus = new RecordingEneBus(name) { MutexWaitMs = 100 };
        bus.Devices[0x71] = EneStick();
        try
        {
            t.Equal(BlockWrite.Ok, bus.WriteBlockData(0x71, 0x03, new byte[] { 1, 2, 3 }), "an uncontended block goes through");
            t.Equal(BlockWrite.Refused, bus.WriteBlockData(0x55, 0x03, new byte[] { 1, 2, 3 }), "a block nothing ACKs is refused");
            bus.ClearLog();
            t.Equal(BlockWrite.Refused, bus.WriteBlockData(0x71, 0x03, new byte[33]), "a block longer than SMBus carries is refused");
            t.Equal(0, bus.Log().Count, "...without being issued");
            using (var hog = new BusHog(name))
            {
                t.Check(hog.Held(2000), "another tool holds the bus");
                t.Equal(BlockWrite.BusBusy, bus.WriteBlockData(0x71, 0x03, new byte[] { 1, 2, 3 }),
                    "a block that could not get the bus is BusBusy, not Refused");
                t.Equal(0, bus.Log().Count, "...and nothing was put on the wire");
            }
            t.Equal(BlockWrite.Ok, bus.WriteBlockData(0x71, 0x03, new byte[] { 1, 2, 3 }), "the same block goes through once the bus is free");
        }
        finally { bus.Dispose(); }
    }

    /// <summary>The A6 remainder. The select lands; between it and the block
    /// another tool takes the bus and holds it past the driver's wait, so the
    /// block is never issued. That used to count as the host refusing a block,
    /// and three of them flipped the stick to 3-byte chunks for the session.</summary>
    static void EneBusyBusIsNotARefusal(Harness t)
    {
        t.Section("ENE: a bus taken between the select and the block does not count toward the chunk fallback");
        static Rgb[] Frame(int n, byte v) => Enumerable.Repeat(new Rgb(v, (byte)(v + 1), (byte)(v + 2)), n).ToArray();
        {
            string name = PrivateMutexName();
            var bus = new RecordingEneBus(name) { MutexWaitMs = 100 };
            bus.Devices[0x71] = EneStick();
            var stick = EneDram.DetectOn(bus).Single();
            int n = stick.LedCount;
            BusHog? hog = null;
            bool steal = false;
            // The register select of the direct-color block: the moment the
            // other tool gets in. It parks on the mutex while the select still
            // holds it, so the release hands the bus straight to it.
            bus.OnXfer = input =>
            {
                if (!steal || input.Length != 5 || input[2] != 0x00 || input[3] != 3 || input[4] != 0x0081) return;
                steal = false;
                hog = new BusHog(name);
                hog.Parked(2000);
            };
            try
            {
                t.Check(stick.SetColors(Frame(n, 10)), "a frame lands");
                bool allRefused = true, noneViaChunks = true;
                for (int i = 0; i < 4; i++)   // past BlockRefusalsBeforeFallback (3)
                {
                    bus.ClearLog();
                    steal = true;
                    bool landed = stick.SetColors(Frame(n, (byte)(20 + i * 10)));
                    hog?.Dispose(); hog = null;
                    allRefused &= !landed;
                    noneViaChunks &= !bus.Blocks().Any(len => len < n * 3) && bus.Bytes() == 0;
                }
                t.Check(allRefused, "a frame whose block met a busy bus is refused (not cached; re-sent next call)");
                t.Check(noneViaChunks, "...and is not pushed through 3-byte chunks or bytes onto the same busy bus");
                bus.ClearLog();
                var last = Frame(n, 90);
                t.Check(stick.SetColors(last), "the next frame lands");
                t.Check(bus.Blocks().SequenceEqual(new[] { n * 3 }),
                    $"...as ONE whole-stick block: four busy frames did not flip the stick to chunks (blocks: {string.Join(",", bus.Blocks())})");
                var regs = bus.Devices[0x71].Regs;
                t.Check(Enumerable.Range(0, n).All(i => regs.GetValueOrDefault(0x8100 + i * 3) == 90 && regs.GetValueOrDefault(0x8100 + i * 3 + 1) == 92),
                    "...and the stick holds that frame");
            }
            finally { hog?.Dispose(); stick.Dispose(); }
        }
        {
            // Real refusals with busy frames between them: a busy frame neither
            // counts nor resets the streak, and three REFUSALS still flip.
            string name = PrivateMutexName();
            var bus = new RecordingEneBus(name) { MutexWaitMs = 100 };
            bus.Devices[0x71] = EneStick();
            var stick = EneDram.DetectOn(bus).Single();
            int n = stick.LedCount;
            BusHog? hog = null;
            bool steal = false;
            bus.OnXfer = input =>
            {
                if (!steal || input.Length != 5 || input[2] != 0x00 || input[3] != 3 || input[4] != 0x0081) return;
                steal = false;
                hog = new BusHog(name);
                hog.Parked(2000);
            };
            int BigTries() => bus.Blocks().Count(len => len == n * 3);
            try
            {
                bus.RefuseBlocksOver = 3;   // a host that rejects large blocks
                t.Check(stick.SetColors(Frame(n, 10)), "refusal 1: the frame still lands, through chunks");
                steal = true;
                t.Check(!stick.SetColors(Frame(n, 20)), "busy: refused");
                hog?.Dispose(); hog = null;
                t.Check(stick.SetColors(Frame(n, 30)), "refusal 2: lands through chunks");
                bus.ClearLog();
                t.Check(stick.SetColors(Frame(n, 40)), "the next frame lands");
                t.Equal(1, BigTries(), "...having still tried the whole-stick block: the busy frame was not a third refusal");
                bus.ClearLog();
                t.Check(stick.SetColors(Frame(n, 50)), "after three real refusals frames still land");
                t.Equal(0, BigTries(), "...and no longer try the whole-stick block");
            }
            finally { hog?.Dispose(); stick.Dispose(); }
        }
    }

    /*---------------- ProtectedFolder: links, races, planted folders ----------------*/

    /// <summary>The same rules as the app's, owned by THIS user: the harness is
    /// not elevated, and only an elevated process may make Administrators the
    /// owner. Everything else - the no-follow inspection, the atomic create,
    /// the move-aside - is the production path.</summary>
    static ProtectedFolder.Policy Mine()
    {
        using var me = WindowsIdentity.GetCurrent();
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        return new ProtectedFolder.Policy(me.User!, system, admins, me.User!);
    }

    /// <summary>A directory junction, made the way any unprivileged process can
    /// (mklink /J needs no elevation and no developer mode).</summary>
    static bool Junction(string link, string target)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /c mklink /J \"{link}\" \"{target}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(10_000);
        return p.ExitCode == 0 && IsLink(link);
    }

    static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (IOException) { return false; }   // not there at all
    }

    static string Sddl(string dir) => new DirectoryInfo(dir).GetAccessControl()
        .GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);

    /// <summary>The ACL Ensure gives a folder, built afresh: a DirectorySecurity
    /// read from disk persists nothing when set elsewhere (only modified
    /// sections are written).</summary>
    static DirectorySecurity ProtectedAcl(ProtectedFolder.Policy policy)
    {
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags below = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var w in policy.Writers)
            acl.AddAccessRule(new FileSystemAccessRule(w, FileSystemRights.FullControl, below, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, below, PropagationFlags.None, AccessControlType.Allow));
        acl.SetOwner(policy.Owner);
        return acl;
    }

    static string[] Aside(string path) => Directory.GetFileSystemEntries(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".untrusted-*");

    static void FolderNeverAdoptsALink(Harness t)
    {
        t.Section("ProtectedFolder: a junction is never adopted, followed or written through");
        var policy = Mine();
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        string root = TempDir();
        try
        {
            // A folder that passes every check, with a binary in it: exactly
            // what a junction would want to lend its name to.
            string good = Path.Combine(root, "good");
            t.Check(ProtectedFolder.Ensure(good, policy, out string? why), $"a protected folder is made as this user ({why})");
            t.Check(ProtectedFolder.Verify(good, policy, out why), $"...and verifies ({why})");
            File.WriteAllText(Path.Combine(good, "OpenRGB.exe"), "theirs");
            string goodAcl = Sddl(good);
            var made = new DirectoryInfo(good).GetAccessControl();
            t.Check(made.AreAccessRulesProtected, "it was created with its own ACL, nothing inherited");
            t.Check(made.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                        .Where(r => r.IdentityReference.Equals(users))
                        .All(r => (r.FileSystemRights & (FileSystemRights.Write | FileSystemRights.Delete)) == 0),
                "...in which ordinary users can read but not write");

            string link = Path.Combine(root, "link");
            if (!Junction(link, good)) { t.Check(false, "a junction could be made for the test (mklink /J)"); return; }
            // A junction carries a security descriptor of its OWN, and a path
            // ACL read or write acts on that one, not the target's. The old
            // create-then-ACL, run on a junction re-planted under the name,
            // stamped the administrators-only ACL onto the junction itself,
            // and the old check (owner + DACL, by path) then passed it.
            new DirectoryInfo(link).SetAccessControl(ProtectedAcl(policy));
            t.Check(Sddl(good) == goodAcl && new DirectoryInfo(link).GetAccessControl().AreAccessRulesProtected,
                "the junction carries the protected ACL itself (its target is untouched)");
            t.Check(!ProtectedFolder.Verify(link, policy, out why) && why!.Contains("junction", StringComparison.Ordinal),
                $"a junction does not verify, whatever ACL it carries ({why})");

            t.Check(ProtectedFolder.Ensure(link, policy, out why), $"Ensure on the junction's name makes a folder of its own ({why})");
            t.Check(Directory.Exists(link) && !IsLink(link), "...a real directory now stands at the name");
            t.Check(!File.Exists(Path.Combine(link, "OpenRGB.exe")), "...without the target's binary in it");
            t.Check(File.Exists(Path.Combine(good, "OpenRGB.exe")) && Sddl(good) == goodAcl,
                "...and the target was not touched: its file and its ACL are as they were");
            var aside = Aside(link);
            t.Check(aside.Length == 1 && IsLink(aside[0]), $"...the junction itself was renamed aside, not followed ({string.Join(", ", aside)})");

            // The race the atomic create exists for: a junction re-planted
            // between clearing the name and creating the folder. The old code
            // created with .NET's create (which quietly accepts an existing
            // directory) and then set the ACL by path - on the target.
            string plain = Path.Combine(root, "plain");
            Directory.CreateDirectory(plain);
            File.WriteAllText(Path.Combine(plain, "payload.dll"), "theirs");
            string plainAcl = Sddl(plain);
            string raced = Path.Combine(root, "raced");
            bool planted = false;
            string? linkAcl = null;
            ProtectedFolder.BeforeCreate = p =>
            {
                if (p != raced) return;
                planted = Junction(raced, plain);
                if (planted) linkAcl = Sddl(raced);
            };
            bool ok;
            try { ok = ProtectedFolder.Ensure(raced, policy, out why); }
            finally { ProtectedFolder.BeforeCreate = null; }
            t.Check(planted, "a junction was planted mid-Ensure");
            t.Check(!ok && why != null && why.Contains("appeared", StringComparison.Ordinal),
                $"Ensure refuses what appeared under the name instead of adopting it ({why})");
            t.Check(planted && Sddl(raced) == linkAcl,
                "...without stamping our ACL on the planted junction - the step that made the old check pass it");
            t.Check(Sddl(plain) == plainAcl, "...and the folder it points at keeps its own ACL");
            t.Check(Directory.GetFileSystemEntries(plain).Length == 1 && File.Exists(Path.Combine(plain, "payload.dll")),
                "...or created or removed anything in that folder");
            t.Check(IsLink(raced), "...the planted link is left where it is, for the next call to move aside");

            // The folder it is made IN: the level a user can plant under
            // ProgramData before the app ever runs.
            string parentLink = Path.Combine(root, "parent-link");
            if (Junction(parentLink, plain))
            {
                t.Check(!ProtectedFolder.Ensure(Path.Combine(parentLink, "child"), policy, out why)
                        && why != null && why.Contains("junction", StringComparison.Ordinal),
                    $"a folder whose parent is a junction is refused ({why})");
                t.Check(!Directory.Exists(Path.Combine(plain, "child")), "...and nothing was created at the junction's target");
            }
            else t.Check(false, "a second junction could be made for the test");
        }
        finally
        {
            ProtectedFolder.BeforeCreate = null;
            // Junctions are removed, not walked into, by a recursive delete.
            try { Directory.Delete(root, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // a temp folder left behind costs nothing; the run's result stands
        }

        t.Section("ProtectedFolder: a folder that is not ours is moved aside, never walked into");
        root = TempDir();
        try
        {
            // Someone's own folder (owned by its creator, the temp folder's
            // inherited ACL) with a payload inside.
            string theirs = Path.Combine(root, "theirs");
            Directory.CreateDirectory(theirs);
            File.WriteAllText(Path.Combine(theirs, "OpenRGB.exe"), "an impostor");
            t.Check(!ProtectedFolder.Verify(theirs, policy, out string? why) && why!.Contains("inherits", StringComparison.Ordinal),
                $"a folder with an inherited ACL is not ours ({why})");
            t.Check(ProtectedFolder.Ensure(theirs, policy, out why), $"Ensure takes the name ({why})");
            t.Check(ProtectedFolder.Verify(theirs, policy, out why), $"...what stands there now verifies ({why})");
            t.Check(!File.Exists(Path.Combine(theirs, "OpenRGB.exe")), "...without the payload");
            var aside = Aside(theirs);
            t.Check(aside.Length == 1 && File.Exists(Path.Combine(aside[0], "OpenRGB.exe")),
                "...which was renamed aside whole, not deleted file by file");

            File.WriteAllText(Path.Combine(theirs, "ours.txt"), "kept");
            t.Check(ProtectedFolder.Ensure(theirs, policy, out why), $"a second call is idempotent ({why})");
            t.Check(File.Exists(Path.Combine(theirs, "ours.txt")) && Aside(theirs).Length == 1,
                "...our folder is used as it is: its contents kept, nothing moved");

            // An ACL loosened after the fact: not ours any more.
            var acl = new DirectoryInfo(theirs).GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.Modify, AccessControlType.Allow));
            new DirectoryInfo(theirs).SetAccessControl(acl);
            t.Check(!ProtectedFolder.Verify(theirs, policy, out why) && why!.Contains("can write", StringComparison.Ordinal),
                $"a folder ordinary users can write to does not verify ({why})");
            t.Check(ProtectedFolder.Ensure(theirs, policy, out why) && ProtectedFolder.Verify(theirs, policy, out _),
                $"...and is replaced rather than re-permissioned in place, with whatever was written while it was open ({why})");
            t.Check(!File.Exists(Path.Combine(theirs, "ours.txt")) && Aside(theirs).Length == 2, "...which went aside with it");

            string file = Path.Combine(root, "a-file");
            File.WriteAllText(file, "not a folder");
            t.Check(ProtectedFolder.Ensure(file, policy, out why) && Directory.Exists(file), $"a FILE at the name is moved aside too ({why})");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // a temp folder left behind costs nothing; the run's result stands
        }
    }

    /*---------------- Lian: an unconfirmed upload is not re-sent on every rebake ----------------*/

    /// <summary>The SLV3 transmitter: counts fragments and animation uploads.</summary>
    sealed class Transmitter : IUsbWriter
    {
        readonly List<byte[]> _writes = new();
        public byte BulkOutPipe => 1;
        public bool Write(byte pipe, byte[] buffer)
        {
            lock (_writes) _writes.Add((byte[])buffer.Clone());
            return true;
        }
        public int Writes { get { lock (_writes) return _writes.Count; } }
        /// <summary>Animation uploads: the distinct effect indices (RF bytes
        /// 14-17) on meta packets of multi-frame effects (RF 0x12 0x20, packet
        /// index 0, total frames above 1). Each upload sends its meta four
        /// times under one fresh index.</summary>
        public int AnimationMetas()
        {
            lock (_writes)
                return _writes.Where(p => p[1] == 0 && p[4] == 0x12 && p[5] == 0x20 && p[22] == 0 && (p[29] << 8 | p[30]) > 1)
                              .Select(p => BitConverter.ToUInt32(p, 18)).Distinct().Count();
        }
        public void Dispose() { }
    }

    static readonly PropertyInfo LianInstance = typeof(LianLiWireless).GetProperty("Instance",
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;

    /// <summary>Run what the baker marshalled to this (its UI) thread.</summary>
    static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    static bool PumpUntil(Func<bool> cond, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Pump();
            if (cond()) return true;
            Thread.Sleep(10);
        }
        Pump();
        return cond();
    }

    static void LianUnconfirmedBackoff(Harness t)
    {
        t.Section("Lian: a receiver that never confirms is not sent the whole upload on every rebake");
        t.Equal(60_000L, LianBakeService.BackoffFor(1), "the first unconfirmed round holds the settings back for a minute");
        t.Equal(120_000L, LianBakeService.BackoffFor(2), "...the second for two");
        t.Equal(30 * 60_000L, LianBakeService.BackoffFor(6), "...doubling up to the cap");
        t.Equal(30 * 60_000L, LianBakeService.BackoffFor(1000), "...and no further, however many rounds");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            object? prior = LianInstance.GetValue(null);
            int savedConfirm = LianLiWireless.AnimConfirmTimeoutMs;
            long savedBase = LianBakeService.UnconfirmedBackoffMs, savedCap = LianBakeService.UnconfirmedBackoffCapMs;
            LianLiWireless.AnimConfirmTimeoutMs = 300;
            LianBakeService.UnconfirmedBackoffMs = 1000;
            LianBakeService.UnconfirmedBackoffCapMs = 2000;
            var usb = new Transmitter();
            // No receiver: nothing ever reads the effect index back, so no
            // upload is ever confirmed - the receiver V2 is about.
            var fans = new LianLiWireless(usb, "NeverConfirms", new byte[] { 7, 2, 3, 4, 5, 6 }, new byte[] { 8, 2, 3, 4, 5, 6 }, 8, 1, 1)
            { ReceiverOpener = () => null };
            var lighting = new LightingController();
            try
            {
                var bake = new LianBakeService(lighting, () => new[] { fans });
                var rebakeMethod = typeof(LianBakeService).GetMethod("Rebake", BindingFlags.Instance | BindingFlags.NonPublic)!;
                void Rebake() => rebakeMethod.Invoke(bake, new object[] { fans, false });
                var misses = (System.Collections.IDictionary)typeof(LianBakeService)
                    .GetField("_misses", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bake)!;
                var black = Enumerable.Repeat(Rgb.Black, fans.LedCount).ToArray();
                fans.SuppressStreaming = true;   // the engine worker renders the preview only
                lighting.Engine.Start(fans, 0, fans.LedCount, lighting.FrameFor(fans), new Breathing(), 1.0, Rgb.Red);

                // Round 1: the upload, then (unconfirmed) its one retry, then streaming.
                Rebake();
                t.Check(PumpUntil(() => usb.AnimationMetas() == 1, 5000), "the bake is uploaded");
                Rebake();
                Pump();
                t.Check(fans.AnimationActive, "an upload awaiting confirmation counts as playing");
                t.Equal(1, usb.AnimationMetas(), "...so asking for the same bake again inside that window is not a second upload");
                t.Check(PumpUntil(() => usb.AnimationMetas() == 2, 5000), "unconfirmed: the bake is retried once");
                t.Check(PumpUntil(() => !fans.SuppressStreaming, 5000), "unconfirmed again: the fans are handed back to streaming");
                var roundEnded = Stopwatch.StartNew();
                int afterRound1 = usb.AnimationMetas();

                // The V2 case: rebake requests for the same settings.
                for (int i = 0; i < 3; i++) { Rebake(); Pump(); Thread.Sleep(60); }
                lighting.Applier.Drain(3000);
                Pump();
                t.Equal(afterRound1, usb.AnimationMetas(),
                    "the same settings asked for three more times are NOT uploaded again while the backoff runs (was: a whole round each)");
                t.Check(!fans.SuppressStreaming, "...they are streamed instead");
                int w0 = usb.Writes;
                t.Check(WaitUntil(() => usb.Writes > w0, 2000), "...and the engine is painting the fans");

                // A static frame (LightsOff, the unlock restore, identify's
                // put-back) inside the backoff: the fans are being streamed, so
                // the next engine frame paints the effect back. Still no upload.
                t.Check(fans.SetColors(black), "a static frame goes out");
                Rebake();
                Pump();
                lighting.Applier.Drain(3000);
                t.Equal(afterRound1, usb.AnimationMetas(), "...and does not bring the upload back early");
                t.Check(!fans.SuppressStreaming, "...the fans stay on streaming, so they are not left static or black");

                // Backoff over: one more round.
                Thread.Sleep((int)Math.Max(0, LianBakeService.UnconfirmedBackoffMs + 150 - roundEnded.ElapsedMilliseconds));
                Rebake();
                t.Check(PumpUntil(() => usb.AnimationMetas() == afterRound1 + 1, 5000), "once the backoff has run out the same settings are uploaded again");
                t.Check(fans.SuppressStreaming && fans.AnimationActive, "...and are no longer streamed");

                // D1 is untouched: a static frame cancels the animation in
                // hardware, and the same settings MUST go out again.
                t.Check(fans.SetColors(black) && !fans.AnimationActive, "a static frame cancels the animation");
                int before = usb.AnimationMetas();
                Rebake();
                t.Check(PumpUntil(() => usb.AnimationMetas() == before + 1, 5000), "D1: after a static frame the same settings ARE uploaded again");

                // That upload and its retry lapse too: the second round doubles the wait.
                t.Check(PumpUntil(() => usb.AnimationMetas() == before + 2 && !fans.SuppressStreaming, 8000),
                    "round 2 unconfirmed through its retry: streaming again");
                roundEnded.Restart();
                int afterRound2 = usb.AnimationMetas();
                Thread.Sleep((int)LianBakeService.UnconfirmedBackoffMs + 300);   // past the FIRST round's wait
                Rebake();
                Pump();
                lighting.Applier.Drain(3000);
                t.Equal(afterRound2, usb.AnimationMetas(), "a second unconfirmed round holds the settings back for longer (the wait doubled)");

                // A confirmation forgets the misses: the fans can take these settings.
                Thread.Sleep((int)Math.Max(0, LianBakeService.UnconfirmedBackoffCapMs + 150 - roundEnded.ElapsedMilliseconds));
                LianLiWireless.AnimConfirmTimeoutMs = 10_000;   // no lapse racing the confirmation below
                Rebake();
                t.Check(PumpUntil(() => usb.AnimationMetas() == afterRound2 + 1, 5000), "after the doubled wait the settings are uploaded again");
                lighting.Applier.Drain(3000);
                t.Check(misses.Contains(fans), "...with their misses still on record");
                var index = (byte[])typeof(LianLiWireless).GetField("_pendingIndex", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fans)!;
                var page = new byte[434];
                page[0] = 0x10;
                new byte[] { 7, 2, 3, 4, 5, 6 }.CopyTo(page, 4);   // our fan group's record
                Array.Copy(index, 0, page, 4 + 20, 4);              // ...playing the index we sent
                t.Check(fans.ApplyTelemetryPage(page, 46), "the fans report the uploaded effect index");
                t.Check(PumpUntil(() => !misses.Contains(fans), 3000), "...and the confirmation clears the record, so a later failure starts the count afresh");
                t.Check(fans.SuppressStreaming && fans.AnimationActive, "...the animation plays, streaming suppressed");
                bake.Stop();
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                lighting.Engine.StopAll();
                lighting.Applier.Drain(3000);
                fans.Dispose();
                LianInstance.SetValue(null, prior);
                LianLiWireless.AnimConfirmTimeoutMs = savedConfirm;
                LianBakeService.UnconfirmedBackoffMs = savedBase;
                LianBakeService.UnconfirmedBackoffCapMs = savedCap;
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        }) { IsBackground = true, Name = "remaining-drivers-lian" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(60_000)) { t.Check(false, "the Lian backoff test finished in time"); return; }
        if (failure != null) t.Check(false, $"the Lian backoff test threw: {failure}");
    }

    /// <summary>Keys waiting on one applier lane (the bake's upload is posted
    /// there as (device, "anim")).</summary>
    static bool Pending(CoalescingApplier applier, object lane, object key)
    {
        var lanes = (System.Collections.IDictionary)typeof(CoalescingApplier).GetField("_lanes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(applier)!;
        if (lanes[lane] is not object l) return false;
        var pending = (System.Collections.IDictionary)l.GetType().GetField("Pending")!.GetValue(l)!;
        lock (l.GetType().GetField("Lock")!.GetValue(l)!) return pending.Contains(key);
    }

    /// <summary>External review: LightsOff stopped the effect workers but left
    /// a wireless-fan bake that was still rendering (or its upload already
    /// queued behind a slow RF run) valid, so it uploaded its animation after
    /// the black frame and lit the fans on a locked desk.</summary>
    static void LightsOffCancelsABakeInFlight(Harness t)
    {
        t.Section("Lian: a bake still in flight when the lights go off never reaches the fans");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            object? prior = LianInstance.GetValue(null);
            var usb = new Transmitter();
            var fans = new LianLiWireless(usb, "LightsOff", new byte[] { 7, 2, 3, 4, 5, 6 }, new byte[] { 8, 2, 3, 4, 5, 6 }, 8, 1, 1)
            { ReceiverOpener = () => null };
            var lighting = new LightingController();
            var gate = new ManualResetEventSlim();
            try
            {
                bool dark = false;
                var bake = new LianBakeService(lighting, () => new[] { fans }) { Suppressed = () => dark };
                var rebake = typeof(LianBakeService).GetMethod("Rebake", BindingFlags.Instance | BindingFlags.NonPublic)!;
                fans.SuppressStreaming = true;
                lighting.Engine.Start(fans, 0, fans.LedCount, lighting.FrameFor(fans), new Breathing(), 1.0, Rgb.Red);
                object lane = LightingController.LaneOf(fans);

                // The upload queues behind a job holding the fans' lane, as it
                // does behind a slow run of RF packets.
                lighting.Applier.Post(lane, (fans, "test-hold"), () => gate.Wait(10_000));
                rebake.Invoke(bake, new object[] { fans, false });
                t.Check(PumpUntil(() => Pending(lighting.Applier, lane, (fans, "anim")), 5000), "the bake's upload is queued");
                bake.CancelInFlight();   // what LightsOff now does, before its black frames
                gate.Set();
                lighting.Applier.Drain(5000);
                t.Equal(0, usb.AnimationMetas(), "the cancelled bake uploads nothing after the lights went off (was: the fans animated on a dark desk)");

                // The second guard: an upload that comes due while the lights
                // are off is dropped even if nothing cancelled it.
                gate.Reset();
                lighting.Applier.Post(lane, (fans, "test-hold"), () => gate.Wait(10_000));
                bake.ForgetSignatures();
                rebake.Invoke(bake, new object[] { fans, false });
                t.Check(PumpUntil(() => Pending(lighting.Applier, lane, (fans, "anim")), 5000), "a second bake's upload is queued");
                dark = true;
                gate.Set();
                lighting.Applier.Drain(5000);
                t.Equal(0, usb.AnimationMetas(), "...and it is not uploaded while the lights are suppressed");
                dark = false;
                bake.Stop();

                // The wiring: LightsOff is what cancels, and what the uploads ask.
                var vm = new MainViewModel(startServices: false);
                var vmBake = (LianBakeService)typeof(MainViewModel).GetField("_bake", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
                try
                {
                    var gens = (System.Collections.Concurrent.ConcurrentDictionary<LianLiWireless, int>)typeof(LianBakeService)
                        .GetField("_gen", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vmBake)!;
                    gens[fans] = 5;
                    vm.LightsOff();
                    t.Equal(6, gens[fans], "LightsOff abandons every bake in flight");
                    t.Check(vmBake.Suppressed?.Invoke() == true, "...and an upload due during the dark window asks whether the lights are off");
                    vm.LightsSuppressed = false;
                }
                finally
                {
                    vm.Shows.Dispose(); vm.Lcd.Dispose(); vm.Lighting.StopAndDrain(); vmBake.Stop();
                }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                gate.Set();
                lighting.Engine.StopAll();
                lighting.Applier.Drain(3000);
                gate.Dispose();
                fans.Dispose();
                LianInstance.SetValue(null, prior);
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        }) { IsBackground = true, Name = "remaining-drivers-lightsoff" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(60_000)) { t.Check(false, "the lights-off bake test finished in time"); return; }
        if (failure != null) t.Check(false, $"the lights-off bake test threw: {failure}");
    }

    static string Show(List<(string Fn, ulong[] Input)> xfers)
        => string.Join(" | ", xfers.Select(x => string.Join(",", x.Input.Select(v => v.ToString("X")))));
}
