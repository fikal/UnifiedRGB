using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using UnifiedRgb.App;
using UnifiedRgb.App.Services;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Net;
using static UnifiedRgb.Tests.TestHelpers;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| What the 10/15 fix-all pass left open, pinned.               |
|                                                              |
|  E9   a re-save rebuilds effect and zone entries, and their  |
|       unknown fields have to come across with them           |
|  F16  restoring your OWN backup brings the fan settings back;|
|       somebody else's still cannot set them                  |
|  dup  two devices of one name both match by that name        |
|  V2   no hold survives a rescan that it should not, and the  |
|       one that should is not released by the old server     |
|                                                              |
| Fakes and the harness's own config folder only. Every file   |
| it touches is put back as it was, for the suites after it.   |
\*-----------------------------------------------------------*/
static class RemainingStateSuite
{
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly string[] Files = { "profiles.json", "settings.json", "scenes.json", "canvas.json", "lcd.json", "calibration.json" };

    public static void Run(Harness t)
    {
        if (!AppPaths.ConfigDir.StartsWith(Isolation.Root, StringComparison.OrdinalIgnoreCase))
        {
            t.Check(false, "REFUSING to run: AppPaths is not redirected, and this suite rewrites real config files");
            return;
        }

        var originals = Files.ToDictionary(n => n, n => File.Exists(AppPaths.Config(n)) ? File.ReadAllBytes(AppPaths.Config(n)) : null);
        double brightness = Master.Brightness;
        string temp = TempDir();
        Exception? failure = null;
        // The view models need an STA, like every other suite that builds one.
        var thread = new Thread(() =>
        {
            try
            {
                EntriesKeepTheirExtra(t);
                ExtraFollowsAMovedRange(t);
                StoreCarriesByIdentity(t);
                PaletteKeepsItsExtra(t);
                HoldsAcrossARescan(t);
                StoppedServerHoldsNothing(t);
                ColorsOfParkedAcrossARescan(t);
                ExtraLeavesWithItsEffect(t);
                StaleReleaseKeepsANewerClaim(t);
                OwnBackupBringsFanSettings(t, temp);
                ForeignBundleLeavesThemOut(t, temp);
                SameNamedDevicesMatchByName(t, temp);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                foreach (var (name, bytes) in originals)
                {
                    string path = AppPaths.Config(name);
                    if (bytes == null) { if (File.Exists(path)) File.Delete(path); }
                    else File.WriteAllBytes(path, bytes);
                }
                CleanImportLeftovers();
                Master.Brightness = brightness;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        try { Directory.Delete(temp, recursive: true); } catch { }
        if (failure != null) throw new Exception("RemainingState regression failed", failure);
    }

    /*---------------- E9: the entries a save rebuilds ----------------*/

    static RgbZone Zone(string name, int offset, int count) => new() { Name = name, Offset = offset, Count = count };

    /// <summary>What a newer build wrote: a field on the profile, on each zone
    /// of the layout and on the effect entry.</summary>
    const string FutureProfiles = """
        [{"Name":"E9 entries","FutureProfile":1,
          "DeviceFrames":{"E9 board":["FF0000","FF0000","00FF00","00FF00"]},
          "DeviceZones":{"E9 board":[{"Name":"Fans","Offset":0,"Count":2,"FutureZone":"fans"},
                                     {"Name":"Strip","Offset":2,"Count":2,"FutureZone":"strip"}]},
          "Effects":[{"Device":"E9 board","Offset":2,"Count":2,"Effect":"Rainbow Wave","FutureEffect":{"Easing":"in"}}]}]
        """;

    /// <summary>The saved profile as JSON, read back off the disk.</summary>
    static JsonObject OnDisk(string name)
        => JsonNode.Parse(File.ReadAllText(AppPaths.Config("profiles.json")))!.AsArray()
               .Select(n => n!.AsObject()).Single(p => p["Name"]?.GetValue<string>() == name);

    static void ExpectFutureFields(Harness t, string how, int stripOffset)
    {
        var saved = OnDisk("E9 entries");
        t.Equal(1, saved["FutureProfile"]?.GetValue<int>() ?? 0, $"{how}: the profile's own field is kept (pinned by FixState already)");
        var effect = saved["Effects"]!.AsArray().Select(e => e!.AsObject()).Single(e => e["Effect"]?.GetValue<string>() == "Rainbow Wave");
        t.Equal(stripOffset, effect["Offset"]!.GetValue<int>(), $"{how}: the effect is saved where it runs now");
        t.Equal("in", effect["FutureEffect"]?["Easing"]?.GetValue<string>(),
            $"{how}: the effect entry keeps the field a newer build wrote on it (was: a new entry with an empty bag)");
        var zones = saved["DeviceZones"]!["E9 board"]!.AsArray().Select(z => z!.AsObject()).ToList();
        t.Equal("fans", zones.Single(z => z["Name"]!.GetValue<string>() == "Fans")["FutureZone"]?.GetValue<string>(),
            $"{how}: the 'Fans' zone keeps its field");
        t.Equal("strip", zones.Single(z => z["Name"]!.GetValue<string>() == "Strip")["FutureZone"]?.GetValue<string>(),
            $"{how}: the 'Strip' zone keeps its field (was: rebuilt from the device without it)");
    }

    static void EntriesKeepTheirExtra(Harness t)
    {
        t.Section("E9: a re-save keeps the unknown fields of the effect and zone entries it rebuilds");
        CleanConfig();
        File.WriteAllText(AppPaths.Config("profiles.json"), FutureProfiles);
        var vm = new MainViewModel(startServices: false);
        try
        {
            var board = new FakeDevice { Name = "E9 board", LedCount = 4, ZoneSpec = new[] { Zone("Fans", 0, 2), Zone("Strip", 2, 2) } };
            vm.Devices.Add(board);
            var p = vm.Profiles.Single(x => x.Name == "E9 entries");
            vm.ApplyProfile(p);
            t.Equal(1, vm.Lighting.Engine.ChannelsFor(board).Count, "precondition: the saved effect runs on the strip");

            // The button: picking a profile fills the name box, and a filled box
            // re-saves through SaveProfile.
            vm.SelectedProfile = p;
            vm.SaveProfileCommand.Execute(null);
            ExpectFutureFields(t, "the Save button", stripOffset: 2);

            // The close prompt's path, from what the button just saved.
            t.Check(vm.SaveActiveProfile(), "SaveActiveProfile writes");
            ExpectFutureFields(t, "SaveActiveProfile", stripOffset: 2);

            // A rescan and an automation round trip rebuild the entry from the
            // running channel (CaptureEffects -> RestoreEffects); this is the
            // same trip, and the bag has to ride it.
            vm.ReapplyEffects();
            t.Check(vm.SaveActiveProfile(), "SaveActiveProfile writes after the effects restarted");
            ExpectFutureFields(t, "after the effects were restarted from their own snapshot", stripOffset: 2);
        }
        finally { Stop(vm); }
    }

    static void ExtraFollowsAMovedRange(Harness t)
    {
        t.Section("E9: the effect entry's fields follow its range when the layout moved it");
        CleanConfig();
        File.WriteAllText(AppPaths.Config("profiles.json"), FutureProfiles);
        var vm = new MainViewModel(startServices: false);
        try
        {
            // A strip was added to the first header since the profile was saved:
            // every zone after it moved up by two, so the effect saved at 2..3
            // now runs at 4..5. The replaced entry no longer sits at the range
            // the new one is saved with, so only the running range knows.
            var board = new FakeDevice
            {
                Name = "E9 board", LedCount = 6,
                ZoneSpec = new[] { Zone("New strip", 0, 2), Zone("Fans", 2, 2), Zone("Strip", 4, 2) },
            };
            vm.Devices.Add(board);
            var p = vm.Profiles.Single(x => x.Name == "E9 entries");
            vm.ApplyProfile(p);
            var running = vm.Lighting.Engine.ChannelsFor(board);
            t.Check(running.Count == 1 && running[0].Offset == 4, "precondition: the effect was re-anchored to the strip's new place");
            vm.SelectedProfile = p;
            t.Check(vm.SaveActiveProfile(), "SaveActiveProfile writes");
            ExpectFutureFields(t, "a moved range", stripOffset: 4);
        }
        finally { Stop(vm); }
    }

    static void StoreCarriesByIdentity(Harness t)
    {
        t.Section("E9: the store carries a bag to the entry with the same identity");
        CleanConfig();
        File.WriteAllText(AppPaths.Config("profiles.json"), FutureProfiles);
        var store = new ProfileStore();
        var board = new FakeDevice { Name = "E9 board", LedCount = 4, ZoneSpec = new[] { Zone("Fans", 0, 2), Zone("Strip", 2, 2) } };
        // An entry built with no bag at all - the saved effect started again
        // by hand on its range - and one on a range nothing was saved on.
        var effects = new List<EffectAssignment>
        {
            new() { Device = "E9 board", Offset = 2, Count = 2, Effect = "Rainbow Wave" },
            new() { Device = "E9 board", Offset = 0, Count = 2, Effect = "Breathing" },
        };
        t.Check(store.TryCapture("E9 entries", new (IRgbDevice, Rgb[])[] { (board, new Rgb[4]) }, effects: effects) != null, "the capture writes");
        var saved = OnDisk("E9 entries");
        var byOffset = saved["Effects"]!.AsArray().Select(e => e!.AsObject()).ToDictionary(e => e["Offset"]!.GetValue<int>());
        t.Equal("in", byOffset[2]["FutureEffect"]?["Easing"]?.GetValue<string>(), "the entry with the saved device, range and effect takes its bag");
        t.Check(!byOffset[0].ContainsKey("FutureEffect"), "...and an entry on another range does not");
        var zones = saved["DeviceZones"]!["E9 board"]!.AsArray();
        t.Check(zones.All(z => z!["FutureZone"] != null), "zones are matched by name");

        // Another effect on the saved range is another entry: Rainbow Wave's
        // fields must not ride along onto it (verifier V2, P3-b).
        CleanConfig();
        File.WriteAllText(AppPaths.Config("profiles.json"), FutureProfiles);
        store = new ProfileStore();
        var other = new List<EffectAssignment> { new() { Device = "E9 board", Offset = 2, Count = 2, Effect = "Breathing" } };
        t.Check(store.TryCapture("E9 entries", new (IRgbDevice, Rgb[])[] { (board, new Rgb[4]) }, effects: other) != null, "the second capture writes");
        var swapped = OnDisk("E9 entries")["Effects"]!.AsArray().Select(e => e!.AsObject()).Single(e => e["Offset"]!.GetValue<int>() == 2);
        t.Check(!swapped.ContainsKey("FutureEffect"), "a different effect on the saved range does not take the old effect's bag");
    }

    static void PaletteKeepsItsExtra(Harness t)
    {
        t.Section("E9: overwriting a saved palette keeps the fields a newer build wrote on it");
        CleanConfig();
        var vm = new MainViewModel(startServices: false);
        try
        {
            vm.SettingsData.SavedPalettes = new()
            {
                JsonSerializer.Deserialize<SavedPalette>("""{"Name":"E9 palette","Colors":["FF0000"],"FuturePalette":3}""")!,
            };
            vm.SaveCurrentPaletteAs("E9 palette");
            var pal = JsonNode.Parse(File.ReadAllText(AppPaths.Config("settings.json")))!["SavedPalettes"]!.AsArray().Single()!;
            t.Check(pal["Colors"]!.AsArray().Count == 2, "precondition: the palette was overwritten with the current one");
            t.Equal(3, pal["FuturePalette"]?.GetValue<int>() ?? 0, "...and kept its unknown field (was: a new entry without it)");
        }
        finally { Stop(vm); }
    }

    /*---------------- V2: holds across a rescan ----------------*/

    static void HoldsAcrossARescan(Harness t)
    {
        t.Section("V2: a rescan leaves no hold it should not, and keeps the one it should");
        CleanConfig();
        var vm = new MainViewModel(startServices: false);
        var ui = Dispatcher.CurrentDispatcher;
        Thread? straggler = null, reconnect = null;
        OpenRgbHost? newHost = null;
        try
        {
            // Nothing in the rescan may open a port or start anything.
            vm.SettingsData.SdkServerEnabled = false;
            vm.SettingsData.UseOpenRgb = false;
            vm.SettingsData.Cs2Enabled = false;
            var aOld = new FakeDevice { Name = "V2 A", LedCount = 2 };
            var bOld = new FakeDevice { Name = "V2 B", LedCount = 2 };
            vm.Devices.Add(aOld); vm.Devices.Add(bOld);
            vm.Profiles.Clear();
            var red = new Profile { Name = "V2 red" };
            red.DeviceFrames[aOld.Name] = new[] { "FF0000", "FF0000" };
            red.DeviceFrames[bOld.Name] = new[] { "FF0000", "FF0000" };
            vm.Profiles.Add(red);
            vm.ApplyProfile(red); vm.Lighting.Applier.Drain(2000);
            var white = new[] { Rgb.White, Rgb.White };

            // The server that is up when the rescan starts, as RestartSdkServer
            // builds it. Not listening: what is under test is what happens to
            // the calls its client threads make, not the socket.
            var oldHost = new OpenRgbHost(vm, vm.Lighting, ui);
            oldHost.SetDevices(vm.Devices.ToList());
            var oldServer = new OpenRgbServer(oldHost);
            typeof(MainViewModel).GetField("_sdkHost", Instance)!.SetValue(vm, oldHost);
            typeof(MainViewModel).GetField("_sdkServer", Instance)!.SetValue(vm, oldServer);
            oldHost.BeginExternal(aOld);   // on the UI thread, so the hop runs inline
            t.Check(vm.Lighting.IsClientOwned(aOld), "precondition: a client of the old server holds A");

            // A client thread of the old server that took B as the rescan began:
            // parked in its hop to the UI thread, which is about to be busy with
            // the rescan. The stop gives up on it after half a second, and it
            // carries on - takeover, then its frame - once the rescan is over.
            straggler = Parked(t, ui, "the old server's straggler", () =>
            {
                oldHost.BeginExternal(bOld);
                oldHost.PushExternal(bOld, 0, white);
            });

            var aNew = new FakeDevice { Name = "V2 A", LedCount = 2 };
            var bNew = new FakeDevice { Name = "V2 B", LedCount = 2 };
            vm.Redetect = () =>
            {
                // The next server, up on the new list, and a client that
                // reconnects at once and takes A on it while the rescan is still
                // finishing: its hop is queued behind the rescan too.
                newHost = new OpenRgbHost(vm, vm.Lighting, ui);
                newHost.SetDevices(new IRgbDevice[] { aNew, bNew });
                var host = newHost;
                reconnect = Parked(t, ui, "a client of the next server", () => host.BeginExternal(aNew));
                return new IRgbDevice[] { aNew, bNew };
            };
            int bOldWrites = bOld.WriteCount;

            vm.RescanCommand.Execute(null);
            t.Check(vm.Devices.SequenceEqual(new IRgbDevice[] { aNew, bNew }), "precondition: the rescan swapped in the new instances");
            // What was queued behind the rescan runs now: the takeovers at Send,
            // ahead of the old server's reset, which was queued first at Normal.
            Pump(ui);
            t.Check(straggler!.Join(5000) && reconnect != null && reconnect.Join(5000), "both client threads finished");
            vm.Lighting.Applier.Drain(2000);

            t.Check(!vm.Lighting.IsClientOwned(aOld) && !vm.Lighting.IsClientOwned(bOld),
                "nothing of the replaced instances is held or claimed (was: the straggler's frame re-claimed B's old instance)");
            t.Equal(bOldWrites, bOld.WriteCount,
                "the straggler's frame never reached B's old instance - a disposed handle in the app (was: written)");
            t.Check(HeldOf(vm).All(d => vm.Devices.Contains(d)), "every hold left is on a current instance");
            t.Check(vm.Lighting.IsClientOwned(aNew),
                "the next server's client keeps A, taken during the rescan (was: released by the old server's reset when it finally ran)");
            t.Check(vm.HealthOf(aNew) == DeviceHealthState.ControlledElsewhere, "...and A shows as controlled by another app");
            t.Check(!vm.Lighting.IsClientOwned(bNew), "B is free");

            // The view model's own half, for a late call that does reach it (a
            // server kept up across a detect has no retired host to stop it):
            // a takeover or a release naming a replaced instance touches nothing.
            vm.StopEffectsOn(bOld);
            t.Check(vm.HealthOf(bOld) != DeviceHealthState.ControlledElsewhere,
                "a takeover of a replaced instance does not put it back into the health table the rescan reset");
            vm.RestoreDevice(bOld);
            vm.Lighting.Applier.Drain(2000);
            t.Equal(bOldWrites, bOld.WriteCount, "a release of a replaced instance writes nothing to it (was: a push to a disposed handle)");
            // ...and nothing keeps one alive: a late "describe this device" from
            // the old server's thread stores no frame for it either.
            oldHost.ColorsOf(bOld);
            t.Check(!FramesOf(vm).Any(d => ReferenceEquals(d, aOld) || ReferenceEquals(d, bOld)),
                "no frame is stored for a replaced instance (each one pinned it, handles and all, for the session)");

            // Later still, the straggler reads its next packet. Through one host
            // shared by every server it resolved against the NEW list, and took B
            // for a server that would never let it go.
            oldHost.BeginExternal(bNew);
            oldHost.PushExternal(bNew, 0, white);
            vm.Lighting.Applier.Drain(2000);
            t.Check(!vm.Lighting.IsClientOwned(bNew),
                "a takeover from the stopped server holds nothing (was: B held, and refused every write, for the rest of the session)");
            vm.SelectedDevice = bNew;
            vm.Hex = "00FF00";
            vm.Lighting.Applier.Drain(2000);
            t.Equal("00FF00", bNew.Last?[0].ToHex(), "...so the user's own colour still reaches B");

            newHost!.EndExternal(aNew);
            Pump(ui); vm.Lighting.Applier.Drain(2000);
            t.Check(!vm.Lighting.IsClientOwned(aNew), "the next server's client lets go of A in the ordinary way");
            t.Equal("FF0000", aNew.Last?[0].ToHex(), "...and A is back on the user's red");
        }
        finally
        {
            straggler?.Join(5000);
            reconnect?.Join(5000);
            newHost?.Shutdown();
            Stop(vm);
        }
    }

    static void StoppedServerHoldsNothing(Harness t)
    {
        t.Section("V2: switching the SDK server off leaves nothing held by its leftover threads");
        CleanConfig();
        var vm = new MainViewModel(startServices: false);
        var ui = Dispatcher.CurrentDispatcher;
        Thread? straggler = null;
        try
        {
            vm.SettingsData.UseOpenRgb = false;
            var kept = new FakeDevice { Name = "V2 kept", LedCount = 2 };
            var late = new FakeDevice { Name = "V2 late", LedCount = 2 };
            vm.Devices.Add(kept); vm.Devices.Add(late);
            vm.Profiles.Clear();
            var red = new Profile { Name = "V2 red" };
            red.DeviceFrames[kept.Name] = new[] { "FF0000", "FF0000" };
            red.DeviceFrames[late.Name] = new[] { "FF0000", "FF0000" };
            vm.ApplyProfile(red); vm.Lighting.Applier.Drain(2000);

            var host = new OpenRgbHost(vm, vm.Lighting, ui);
            host.SetDevices(vm.Devices.ToList());
            typeof(MainViewModel).GetField("_sdkHost", Instance)!.SetValue(vm, host);
            typeof(MainViewModel).GetField("_sdkServer", Instance)!.SetValue(vm, new OpenRgbServer(host));
            vm.SettingsData.SdkServerEnabled = true;   // as it was while the server ran; the field, so nothing starts
            host.BeginExternal(kept);
            t.Check(vm.Lighting.IsClientOwned(kept), "precondition: a client holds one device");

            // No rescan here: the same instances stay, so the view model's
            // "is it current" check cannot tell this takeover is a dead one.
            straggler = Parked(t, ui, "the stopped server's straggler", () =>
            {
                host.BeginExternal(late);
                host.PushExternal(late, 0, new[] { Rgb.White, Rgb.White });
            });
            int lateWrites = late.WriteCount;
            vm.SdkServerEnabled = false;   // the user switches the server off
            Pump(ui);
            t.Check(straggler.Join(5000), "the straggler finished");
            vm.Lighting.Applier.Drain(2000);
            t.Check(!vm.Lighting.IsClientOwned(late),
                "its takeover, queued while the server was stopping, holds nothing (was: held for a server that is gone)");
            t.Equal(lateWrites, late.WriteCount, "...and its frame was not painted");

            // A release is still a release: the stop issues them for the claims
            // it ends, and they are what gives the lighting back.
            host.EndExternal(kept);
            Pump(ui); vm.Lighting.Applier.Drain(2000);
            t.Check(!vm.Lighting.IsClientOwned(kept), "a release from the retired host still lets go");
            t.Equal("FF0000", kept.Last?[0].ToHex(), "...and puts the user's lighting back");
        }
        finally
        {
            straggler?.Join(5000);
            Stop(vm);
        }
    }

    /// <summary>Verifier V2 (P3-a): a client thread of the old server parked in
    /// ColorsOf's UI hop - the realistic straggler, since the rescan's
    /// device-list-changed packet makes clients re-read the controllers - ran
    /// after the rescan and stored a frame for the replaced instance.</summary>
    static void ColorsOfParkedAcrossARescan(Harness t)
    {
        t.Section("V2: a colour request parked across a rescan pins nothing");
        CleanConfig();
        var vm = new MainViewModel(startServices: false);
        var ui = Dispatcher.CurrentDispatcher;
        Thread? straggler = null;
        try
        {
            vm.SettingsData.SdkServerEnabled = false;
            vm.SettingsData.UseOpenRgb = false;
            vm.SettingsData.Cs2Enabled = false;
            var aOld = new FakeDevice { Name = "P9 A", LedCount = 2 };
            vm.Devices.Add(aOld);
            var oldHost = new OpenRgbHost(vm, vm.Lighting, ui);
            oldHost.SetDevices(vm.Devices.ToList());
            var oldServer = new OpenRgbServer(oldHost);
            typeof(MainViewModel).GetField("_sdkHost", Instance)!.SetValue(vm, oldHost);
            typeof(MainViewModel).GetField("_sdkServer", Instance)!.SetValue(vm, oldServer);

            straggler = Parked(t, ui, "ColorsOf straggler", () => oldHost.ColorsOf(aOld));
            var aNew = new FakeDevice { Name = "P9 A", LedCount = 2 };
            vm.Redetect = () => new IRgbDevice[] { aNew };
            vm.RescanCommand.Execute(null);
            Pump(ui);
            t.Check(straggler.Join(5000), "the parked request finished");
            t.Check(!FramesOf(vm).Any(d => ReferenceEquals(d, aOld)),
                "it stored no frame for the replaced instance (was: pinned until the next rescan)");
        }
        finally
        {
            straggler?.Join(5000);
            Stop(vm);
        }
    }

    const string ReplacedRangeProfiles = """
        [{"Name":"P6 entries",
          "DeviceFrames":{"P6 board":["FF0000","FF0000","00FF00","00FF00"]},
          "Effects":[{"Device":"P6 board","Offset":2,"Count":2,"Effect":"Rainbow Wave","FutureEffect":{"Easing":"in"}}]}]
        """;

    /// <summary>Verifier V2 (P3-b): the range's effect replaced by an
    /// overlapping one, then another effect started there again - the new
    /// effect inherited the replaced one's unknown fields into a brand-new
    /// profile.</summary>
    static void ExtraLeavesWithItsEffect(Harness t)
    {
        t.Section("E9: a range's unknown fields leave with the effect they were saved with");
        CleanConfig();
        File.WriteAllText(AppPaths.Config("profiles.json"), ReplacedRangeProfiles);
        var vm = new MainViewModel(startServices: false);
        try
        {
            var board = new FakeDevice { Name = "P6 board", LedCount = 4 };
            vm.Devices.Add(board);
            vm.ApplyProfile(vm.Profiles.Single(x => x.Name == "P6 entries"));
            var breathing = vm.Effects.First(e => e.Name == "Breathing");
            var fxFor = typeof(MainViewModel).GetMethod("FxFor", Instance)!;
            var apply = typeof(MainViewModel).GetMethod("ApplyFxRange", Instance)!;
            void Start(int offset, int count)
            {
                var fx = fxFor.Invoke(vm, new object[] { board, offset, count })!;
                fx.GetType().GetProperty("Choice")!.SetValue(fx, breathing);
                apply.Invoke(vm, new object[] { board, offset, count, fx, breathing });
            }
            Start(0, 4);   // a whole-device effect replaces the saved one on 2..3
            Start(2, 2);   // ...and later an effect is started on 2..3 again
            vm.ProfileName = "P6 fresh";
            vm.SaveProfileAsNew();
            var saved = JsonNode.Parse(File.ReadAllText(AppPaths.Config("profiles.json")))!.AsArray()
                .Select(n => n!.AsObject()).Single(o => o["Name"]?.GetValue<string>() == "P6 fresh");
            var entry = saved["Effects"]!.AsArray().Select(e => e!.AsObject()).FirstOrDefault(e => e["Offset"]?.GetValue<int>() == 2);
            t.Check(entry != null, "precondition: the new profile has an entry on 2..3");
            t.Check(entry != null && !entry.ContainsKey("FutureEffect"),
                $"the new {entry?["Effect"]} entry does not carry the replaced Rainbow Wave's unknown fields");
        }
        finally { Stop(vm); }
    }

    /// <summary>External review: a release queues on the UI thread at Normal
    /// priority while a takeover hops synchronously at Send, so a new client's
    /// takeover could run first and the old client's release then removed the
    /// NEW hold and put the user's lighting back under the new client.</summary>
    static void StaleReleaseKeepsANewerClaim(Harness t)
    {
        t.Section("SDK: an old client's late release does not end a newer client's claim");
        CleanConfig();
        var vm = new MainViewModel(startServices: false);
        var ui = Dispatcher.CurrentDispatcher;
        try
        {
            vm.SettingsData.SdkServerEnabled = false;
            vm.SettingsData.UseOpenRgb = false;
            var dev = new FakeDevice { Name = "Claimed", LedCount = 2 };
            vm.Devices.Add(dev);
            var host = new OpenRgbHost(vm, vm.Lighting, ui);
            host.SetDevices(vm.Devices.ToList());
            host.BeginExternal(dev, 1);
            t.Check(vm.Lighting.IsClientOwned(dev), "precondition: client 1 holds the device");
            host.EndExternal(dev, 1);   // client 1 lets go: its release is queued
            host.BeginExternal(dev, 2); // client 2 takes the device before that release runs
            Pump(ui);
            t.Check(vm.Lighting.IsClientOwned(dev), "client 2 still holds it after client 1's late release (was: released under it)");
            host.EndExternal(dev, 2);
            Pump(ui);
            t.Check(!vm.Lighting.IsClientOwned(dev), "...and client 2's own release ends it");
        }
        finally { Stop(vm); }
    }

    /// <summary>Run a client callback on a thread of its own, as a server's
    /// socket thread does, and return once its hop to the UI thread is queued:
    /// the test then knows the call is waiting behind whatever the UI thread
    /// does next, instead of sleeping and hoping.</summary>
    static Thread Parked(Harness t, Dispatcher ui, string who, Action call)
    {
        using var queued = new ManualResetEventSlim();
        Thread? worker = null;
        DispatcherHookEventHandler posted = (_, _) => { if (Thread.CurrentThread == worker) queued.Set(); };
        ui.Hooks.OperationPosted += posted;
        try
        {
            worker = new Thread(() => call()) { IsBackground = true, Name = who };
            worker.Start();
            t.Check(queued.Wait(5000), $"{who}: its hop to the UI thread is queued");
        }
        finally { ui.Hooks.OperationPosted -= posted; }
        return worker;
    }

    static ICollection<IRgbDevice> HeldOf(MainViewModel vm)
        => ((ConcurrentDictionary<IRgbDevice, byte>)typeof(LightingController).GetField("_held", Instance)!.GetValue(vm.Lighting)!).Keys;

    static ICollection<IRgbDevice> FramesOf(MainViewModel vm)
        => ((ConcurrentDictionary<IRgbDevice, Rgb[]>)typeof(LightingController).GetField("_frames", Instance)!.GetValue(vm.Lighting)!).Keys;

    static void Pump(Dispatcher ui)
    {
        var frame = new DispatcherFrame();
        ui.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /*---------------- F16: the fan settings and your own backup ----------------*/

    /// <summary>A machine's settings with every fan setting set, and the
    /// switches no bundle may carry.</summary>
    const string FanSettings = """
        {
          "MasterBrightness": 0.4, "LianHandoffOnExit": true, "LianSpeedScale": 2.5,
          "FanLabels": { "fan:Fan #1": "Pump" }, "LianUniChannel": 2, "LianUniFansByChannel": [1, 1, 3, 1],
          "SdkServerEnabled": true, "UseOpenRgb": true, "Cs2Enabled": true
        }
        """;

    /// <summary>The same machine later: the fan settings lost or changed.</summary>
    const string LaterSettings = """
        { "MasterBrightness": 1.0, "LianHandoffOnExit": false, "LianSpeedScale": 1.4, "LianUniChannel": 0 }
        """;

    static JsonObject Settings() => JsonNode.Parse(File.ReadAllText(AppPaths.Config("settings.json")))!.AsObject();

    static string FanBundle(string temp, string file)
    {
        CleanConfig();
        File.WriteAllText(AppPaths.Config("settings.json"), FanSettings);
        File.WriteAllText(AppPaths.Config("calibration.json"), "{\"Devices\":{},\"Zones\":{}}");
        string bundle = Path.Combine(temp, file);
        SetupBundle.Export(bundle);
        File.WriteAllText(AppPaths.Config("settings.json"), LaterSettings);
        return bundle;
    }

    static void OwnBackupBringsFanSettings(Harness t, string temp)
    {
        t.Section("F16: your own backup brings the fan settings back");
        string bundle = FanBundle(temp, "own.urgb");
        var preview = SetupBundle.Preview(bundle);
        t.Check(preview.Ok && preview.SameMachine, $"precondition: a backup of this machine ({preview.Problem})");
        t.Check(preview.Preferences is { Status: ImportStatus.Differs }, "the preferences differ");
        string detail = preview.Preferences?.Detail ?? "";
        t.Check(detail.Contains("fan hand-off", StringComparison.Ordinal) && detail.Contains("backup of this machine", StringComparison.Ordinal),
            $"the row says the fan settings come along, and why ({detail})");
        t.Check(detail.Contains("SDK server", StringComparison.Ordinal), "...and names what never does");

        var result = SetupBundle.Apply(preview, new ImportChoices { Preferences = true, MachineHardware = true });
        t.Check(result.Ok && result.SettingsChanged, $"the preferences import ({result.Problem})");
        var mine = Settings();
        t.Check(mine["LianHandoffOnExit"]?.GetValue<bool>() == true, "the fan hand-off is back on (was: left off by the allow-list, even from your own backup)");
        t.Equal("Pump", mine["FanLabels"]?["fan:Fan #1"]?.GetValue<string>(), "the fan names are back");
        t.Equal(2.5, mine["LianSpeedScale"]?.GetValue<double>() ?? 0, "the Lian Li speed calibration is back");
        t.Equal(2, mine["LianUniChannel"]?.GetValue<int>() ?? -1, "the wired hub's connector is back");
        t.Equal(3, mine["LianUniFansByChannel"]?[2]?.GetValue<int>() ?? 0, "...and its fan counts");
        t.Equal(0.4, mine["MasterBrightness"]?.GetValue<double>() ?? 0, "the portable preferences come too");
        foreach (string key in new[] { "SdkServerEnabled", "UseOpenRgb", "Cs2Enabled" })
            t.Check(!mine.ContainsKey(key), $"{key} is still never taken, even from this machine");
        t.Check(result.RestartRecommended,
            "the hub's connector and fan counts ask for a restart, as the hub layout file does (and calibration alone did not clear it)");

        var again = SetupBundle.Preview(bundle);
        t.Check(again.Preferences is { Status: ImportStatus.Same }, "once taken, the row reads as already the same");

        // Only a fan setting differs now: the row has to see it, or it reads as
        // "nothing to change" over a hand-off the import would switch back on.
        mine["LianHandoffOnExit"] = false;
        File.WriteAllText(AppPaths.Config("settings.json"), mine.ToJsonString());
        var handoffOnly = SetupBundle.Preview(bundle);
        t.Check(handoffOnly.Preferences is { Status: ImportStatus.Differs }, "a fan setting alone makes the row differ, on this machine");
    }

    static void ForeignBundleLeavesThemOut(Harness t, string temp)
    {
        t.Section("F16: somebody else's bundle still cannot set them, and says so");
        string bundle = FanBundle(temp, "theirs.urgb");
        MadeOn(bundle, "THEIR-PC");
        var preview = SetupBundle.Preview(bundle);
        t.Check(preview.Ok && !preview.SameMachine, $"precondition: made on another machine ({preview.Problem})");
        string detail = preview.Preferences?.Detail ?? "";
        t.Check(detail.Contains("Left out", StringComparison.Ordinal) && detail.Contains("THEIR-PC", StringComparison.Ordinal)
                && detail.Contains("fan hand-off", StringComparison.Ordinal),
            $"the row names the fan settings it leaves out, and why ({detail})");

        var result = SetupBundle.Apply(preview, new ImportChoices { Preferences = true });
        t.Check(result.Ok, $"the preferences import ({result.Problem})");
        var mine = Settings();
        t.Check(mine["LianHandoffOnExit"]?.GetValue<bool>() == false, "the fan hand-off is not taken from another machine");
        t.Equal(0, mine["LianUniChannel"]?.GetValue<int>() ?? -1, "...nor the hub's connector");
        t.Check(!mine.ContainsKey("FanLabels"), "...nor the fan names");
        t.Check(!result.RestartRecommended, "nothing was taken that needs a restart");

        var again = SetupBundle.Preview(bundle);
        t.Check(again.Preferences is { Status: ImportStatus.Same },
            "the fan settings it will not take do not make the row read as different");
        t.Check(again.Preferences?.Detail.Contains("Left out", StringComparison.Ordinal) == true, "...but it still says they are left out");
    }

    /*---------------- two devices of one name ----------------*/

    static void SameNamedDevicesMatchByName(Harness t, string temp)
    {
        t.Section("a bundle with two devices of one name matches both by that name");
        CleanConfig();
        File.WriteAllText(AppPaths.Config("profiles.json"), JsonSerializer.Serialize(new[]
        {
            new Profile { Name = "Twins", DeviceFrames = { ["Keeb"] = new[] { "FF0000", "FF0000" } } },
        }));
        string twins = Path.Combine(temp, "twins.urgb");
        t.Check(SetupBundle.Export(twins, new IRgbDevice[]
        {
            new FakeDevice { Name = "Keeb", LedCount = 2 }, new FakeDevice { Name = "Keeb", LedCount = 2 },
        }).Ok, "fixture exports");
        CleanConfig();

        var both = SetupBundle.Preview(twins, new IRgbDevice[]
        {
            new FakeDevice { Name = "Keeb", LedCount = 2 }, new FakeDevice { Name = "Keeb", LedCount = 2 },
        });
        t.Equal(2, both.Devices.Count(d => d.LocalName == "Keeb" && d.Detail.Contains("matched by name", StringComparison.Ordinal)),
            "both keyboards are present by name (was: the second 'not attached', its name spent on the first)");
        t.Check(!both.Summary.Any(s => s.Contains("not attached", StringComparison.Ordinal)), "...and the summary does not call one missing");

        t.Section("same-named devices pair by identity, so the one that is here is the one that matches");
        string mixed = Path.Combine(temp, "mixed.urgb");
        t.Check(SetupBundle.Export(mixed, new IRgbDevice[]
        {
            new FakeDevice { Name = "Keeb", LedCount = 2 }, new FakeDevice { Name = "Keeb", LedCount = 3 },
        }).Ok, "fixture exports");
        var one = SetupBundle.Preview(mixed, new IRgbDevice[] { new FakeDevice { Name = "Keeb", LedCount = 3 } });
        var three = one.Devices.Single(d => d.BundleDescription.Contains("3 LEDs", StringComparison.Ordinal));
        var two = one.Devices.Single(d => d.BundleDescription.Contains("2 LEDs", StringComparison.Ordinal));
        t.Check(three.Detail.Contains("matched by name", StringComparison.Ordinal),
            $"the 3-LED keyboard, which is the one here, is the match ({three.Detail})");
        t.Check(two.LocalName == "Keeb" && two.Detail.Contains("shares the name", StringComparison.Ordinal),
            $"the other says it shares the name, and where its lighting lands ({two.Detail})");

        t.Section("a renamed twin does not take the lighting off the device of that name");
        // Here the second keyboard is called something else. Its identity still
        // matches, and remapping "Keeb" onto it moved the bundle's one entry for
        // that name off the keyboard that IS called Keeb.
        var renamed = SetupBundle.Preview(twins, new IRgbDevice[]
        {
            new FakeDevice { Name = "Keeb", LedCount = 2 }, new FakeDevice { Name = "Zed", LedCount = 2 },
        });
        t.Check(renamed.Devices.All(d => d.LocalName == "Keeb"), "both bundle rows land on Keeb");
        t.Check(renamed.DefaultChoices().DeviceRemap.Count == 0 && renamed.EverythingChoices().DeviceRemap.Count == 0,
            "no remap is proposed for a name that is here (was: Keeb -> Zed)");
        var applied = SetupBundle.Apply(renamed, renamed.EverythingChoices());
        t.Check(applied.Ok, $"the import goes through ({applied.Problem})");
        var profile = JsonSerializer.Deserialize<List<Profile>>(File.ReadAllText(AppPaths.Config("profiles.json")))!.Single(p => p.Name == "Twins");
        t.Check(profile.DeviceFrames.ContainsKey("Keeb") && !profile.DeviceFrames.ContainsKey("Zed"),
            "the lighting stays on Keeb (was: moved to Zed, and Keeb lost it)");
        CleanConfig();
    }

    /*---------------- helpers ----------------*/

    static void Stop(MainViewModel vm)
    {
        vm.Shows.Dispose(); vm.Lcd.Dispose(); vm.Lighting.StopAndDrain();
        ((LianBakeService)typeof(MainViewModel).GetField("_bake", Instance)!.GetValue(vm)!).Stop();
    }

    static void CleanConfig()
    {
        foreach (string name in Files)
        {
            try { File.Delete(AppPaths.Config(name)); } catch { }
        }
        CleanImportLeftovers();
    }

    static void CleanImportLeftovers()
    {
        try { Directory.Delete(AppPaths.Config("lcd-assets"), recursive: true); } catch { }
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(AppPaths.ConfigDir, "import-backup-*"))
                Directory.Delete(dir, recursive: true);
        }
        catch { }
    }
}
