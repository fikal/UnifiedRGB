using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnifiedRgb.App;
using UnifiedRgb.App.Services;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Effects;
using static UnifiedRgb.Tests.TestHelpers;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| Regressions for the 2026-10-14 review of the setup bundle:   |
|                                                              |
|  F1  the preview (and an Apply that writes nothing) must not |
|      re-arm saves to a file the app could not read          |
|  F5  one device-remap policy; preview and Apply agree, and   |
|      only ticked profiles are remapped                       |
|  F9  size and entry-count bounds, manifest-first reads       |
|  F16 preferences are an allow-list                           |
|  and the minor ones: images are extracted whole (a stub left |
|  by a crash is repaired), five import backups are kept, and  |
|  an oversized image is never read on import.                 |
|                                                              |
| Everything runs under the isolated config root and cleans    |
| up after itself, including the session's "unreadable" set.  |
\*-----------------------------------------------------------*/

static class FixBundleSuite
{
    public static void Run(Harness t)
    {
        if (!AppPaths.ConfigDir.StartsWith(Isolation.Root, StringComparison.OrdinalIgnoreCase))
        {
            t.Check(false, "REFUSING to run: AppPaths is not redirected, and this suite rewrites real config files");
            return;
        }

        string temp = TempDir();
        try
        {
            PreviewKeepsUnreadableGuard(t, temp);
            ApplyRefusesUnreadableFile(t, temp);
            StaleEntriesAgree(t, temp);
            ContradictionsOnlyInTicked(t, temp);
            TwinsSeedFirstRow(t, temp);
            Bounds(t, temp);
            PreferencesAllowList(t, temp);
            AssetsExtractWhole(t, temp);
            KeepsFiveBackups(t, temp);
            FanConfigFlag(t, temp);
        }
        finally
        {
            ReleaseUnreadable();
            try { Directory.Delete(temp, recursive: true); } catch { }
            CleanConfig();
        }
    }

    /*--- F1 ---*/

    static void PreviewKeepsUnreadableGuard(Harness t, string temp)
    {
        t.Section("F1: a preview leaves 'could not be read at startup' alone");
        CleanConfig();
        string bg = Path.Combine(temp, "f1.png");
        File.WriteAllBytes(bg, Png(1));
        WriteScenes("Bundle screen", bg);
        WriteLive(bg, elements: 1);
        var hub = new FakeDevice { Name = "Hub A", LedCount = 8 };
        var hubB = new FakeDevice { Name = "Hub B", LedCount = 8 };
        WriteCanvas(("Hub A", 10), ("Hub B", 20));
        string bundle = Path.Combine(temp, "f1.urgb");
        t.Check(SetupBundle.Export(bundle, new IRgbDevice[] { hub, hubB }).Ok, "fixture exports");

        // This machine's own files, which the app then fails to read at
        // "startup" (a sync client holds them), so it runs on defaults with
        // saves to them switched off.
        WriteScenes("Mine", null);
        WriteLive(null, elements: 3);
        string scenes = AppPaths.Config("scenes.json"), lcd = AppPaths.Config("lcd.json");
        using (Lock(scenes)) SceneStore.Load();
        using (Lock(lcd)) LcdDesign.Load();
        t.Check(ProfileStore.IsUnreadable(scenes) && ProfileStore.IsUnreadable(lcd),
            "precondition: both files are marked unreadable for the session");

        // Readable again now, but nothing has reloaded them into memory: a
        // save from here on would write the defaults over the real files.
        var preview = SetupBundle.Preview(bundle, new IRgbDevice[] { hubB });
        t.Check(preview.Ok, $"the bundle previews ({preview.Problem})");
        t.Check(preview.Screens.Count == 1 && preview.CurrentScreen != null, "the preview did compare screens and the live screen");
        t.Check(ProfileStore.IsUnreadable(scenes),
            "previewing leaves scenes.json's saves off (SceneStore.Load used to switch them back on)");
        t.Check(ProfileStore.IsUnreadable(lcd),
            "previewing leaves lcd.json's saves off (LcdDesign.Load used to switch them back on)");

        // An Apply that builds scenes.json in memory and is then refused by a
        // later section writes nothing, and so must not re-arm saves either.
        var choices = preview.EverythingChoices();
        choices.DeviceRemap["Hub A"] = "Hub B";   // a contradiction in the desk layout
        string before = File.ReadAllText(scenes);
        var refused = SetupBundle.Apply(preview, choices);
        t.Check(!refused.Ok, "an Apply refused after the screens were built writes nothing");
        t.Equal(before, File.ReadAllText(scenes), "...scenes.json is untouched");
        t.Check(ProfileStore.IsUnreadable(scenes) && ProfileStore.IsUnreadable(lcd),
            "...and saves to scenes.json and lcd.json are still off");

        ReleaseUnreadable();
        t.Check(!ProfileStore.IsUnreadable(scenes) && !ProfileStore.IsUnreadable(lcd), "cleanup: the guard is cleared by a real reload");
        CleanConfig();
    }

    static void ApplyRefusesUnreadableFile(Harness t, string temp)
    {
        t.Section("F1: Apply will not merge into a file it cannot read");
        CleanConfig();
        SaveProfiles(new Profile { Name = "Bundled", DeviceFrames = { ["Keeb"] = new[] { "FF0000" } } });
        string bundle = Path.Combine(temp, "locked.urgb");
        t.Check(SetupBundle.Export(bundle).Ok, "fixture exports");
        SaveProfiles(new Profile { Name = "Mine", DeviceFrames = { ["Keeb"] = new[] { "00FF00" } } });
        string profiles = AppPaths.Config("profiles.json");
        string before = File.ReadAllText(profiles);

        var preview = SetupBundle.Preview(bundle);
        ImportResult result;
        using (Lock(profiles)) result = SetupBundle.Apply(preview, preview.EverythingChoices());
        t.Check(!result.Ok, "an import over a profiles.json that cannot be read is refused");
        t.Check(result.Problem?.Contains("could not be read", StringComparison.OrdinalIgnoreCase) == true,
            $"...and says why ({result.Problem})");
        t.Equal(before, File.ReadAllText(profiles), "...the file is untouched (it used to be merged as if empty)");
        t.Check(!ProfileStore.IsUnreadable(profiles), "...and the import did not switch its saves off either");
        CleanConfig();
    }

    /*--- F5 ---*/

    static void StaleEntriesAgree(Harness t, string temp)
    {
        t.Section("F5: a stale entry is dropped by the preview AND by Apply");
        CleanConfig();
        // The keyboard is "Keeb" on the old machine, and its profile still
        // carries a leftover under "K95 RGB" - which is what this machine calls
        // it, and is not a device the old machine had.
        var night = new Profile { Name = "Night" };
        night.DeviceFrames["Keeb"] = new[] { "FF0000" };
        night.DeviceFrames["K95 RGB"] = new[] { "123456" };
        night.Effects = new() { new EffectAssignment { Device = "K95 RGB", Effect = "Rainbow", Count = 1 } };
        SaveProfiles(night);
        WriteCanvas(("Keeb", 10), ("K95 RGB", 99));
        File.WriteAllText(AppPaths.Config("calibration.json"),
            "{\"Devices\":{\"Keeb\":{\"GainR\":0.5},\"K95 RGB\":{\"GainR\":0.9}},\"Zones\":{}}");
        string bundle = Path.Combine(temp, "stale.urgb");
        t.Check(SetupBundle.Export(bundle, new IRgbDevice[] { new FakeDevice { Name = "Keeb", LedCount = 26 } }).Ok, "fixture exports");
        CleanConfig();

        var local = new IRgbDevice[] { new FakeDevice { Name = "K95 RGB", LedCount = 26 } };
        var preview = SetupBundle.Preview(bundle, local);
        t.Check(preview.Ok, $"previews ({preview.Problem})");
        t.Check(preview.Warnings.Any(w => w.Contains("Night", StringComparison.Ordinal) && w.Contains("stale", StringComparison.Ordinal)),
            "the preview says the profile's stale entry is dropped");
        var choices = preview.EverythingChoices();
        t.Check(SetupBundle.RemapProblem(preview, choices) == null, "the window's check sees nothing to refuse");
        var result = SetupBundle.Apply(preview, choices);
        t.Check(result.Ok, $"...and Apply agrees: it imports (it used to refuse the same entry: {result.Problem})");

        var saved = ReadProfiles().Single(p => p.Name == "Night");
        t.Check(saved.DeviceFrames.Count == 1 && saved.DeviceFrames.TryGetValue("K95 RGB", out var f) && f[0] == "FF0000",
            "the mapped keyboard's frame wins, the leftover is gone");
        t.Check(saved.Effects is { Count: 0 }, "the leftover's effect went with it");
        var canvas = CanvasLayout.TryParse(File.ReadAllText(AppPaths.Config("canvas.json")))!;
        t.Check(canvas.Items.Count == 1 && canvas.Items[0].Device == "K95 RGB" && canvas.Items[0].X == 10,
            "the desk layout follows the same rule (it used to refuse a stale placement on Apply)");
        var calibration = JsonNode.Parse(File.ReadAllText(AppPaths.Config("calibration.json")))!["Devices"]!.AsObject();
        t.Check(calibration.Count == 1 && calibration["K95 RGB"]?["GainR"]?.GetValue<double>() == 0.5,
            "and so does calibration (it used to throw 'Conflicting calibration mapping')");
        CleanConfig();
    }

    static void ContradictionsOnlyInTicked(Harness t, string temp)
    {
        t.Section("F5: a real contradiction refuses, but only in what was ticked");
        CleanConfig();
        SaveProfiles(
            new Profile { Name = "Only A", DeviceFrames = { ["Hub A"] = new[] { "FF0000" } } },
            new Profile { Name = "Both", DeviceFrames = { ["Hub A"] = new[] { "FF0000" }, ["Hub B"] = new[] { "0000FF" } } });
        WriteCanvas(("Hub A", 10), ("Hub B", 20));
        string bundle = Path.Combine(temp, "twins.urgb");
        t.Check(SetupBundle.Export(bundle, new IRgbDevice[]
        {
            new FakeDevice { Name = "Hub A", LedCount = 8 }, new FakeDevice { Name = "Hub B", LedCount = 8 },
        }).Ok, "fixture exports");
        CleanConfig();
        var preview = SetupBundle.Preview(bundle, new IRgbDevice[] { new FakeDevice { Name = "Hub B", LedCount = 8 } });

        // The user points Hub A at Hub B. "Both" carries Hub B's own lighting,
        // so for it that is two devices on one: a contradiction. "Only A" has
        // no Hub B entry, so for it the mapping is fine.
        var onlyA = new ImportChoices { Profiles = { "Only A" }, DeviceRemap = { ["Hub A"] = "Hub B" } };
        t.Check(SetupBundle.RemapProblem(preview, onlyA) == null, "the window's check accepts the profile without a clash");
        var ok = SetupBundle.Apply(preview, onlyA);
        t.Check(ok.Ok, $"an unticked profile's contradiction no longer refuses the ticked one ({ok.Problem})");
        t.Equal("FF0000", ReadProfiles().Single(p => p.Name == "Only A").DeviceFrames["Hub B"][0], "...which lands on Hub B");

        CleanConfig();
        var both = new ImportChoices { Profiles = { "Both" }, DeviceRemap = { ["Hub A"] = "Hub B" } };
        string? said = SetupBundle.RemapProblem(preview, both);
        var refused = SetupBundle.Apply(preview, both);
        t.Check(said != null && !refused.Ok, "a ticked contradiction is refused, and the window's check said so first");
        t.Equal(said, refused.Problem, "...in the same sentence Apply refuses with");
        t.Check(!File.Exists(AppPaths.Config("profiles.json")), "...and nothing was written");

        var canvas = new ImportChoices { Canvas = true, DeviceRemap = { ["Hub A"] = "Hub B" } };
        t.Check(SetupBundle.RemapProblem(preview, canvas) != null && !SetupBundle.Apply(preview, canvas).Ok,
            "the desk layout goes through the same policy");
        CleanConfig();
    }

    static void TwinsSeedFirstRow(Harness t, string temp)
    {
        t.Section("F5: the presets hand Apply the mapping the preview described");
        CleanConfig();
        SaveProfiles(new Profile { Name = "Fans", DeviceFrames = { ["Fan Hub"] = new[] { "FF0000" } } });
        string bundle = Path.Combine(temp, "fanhubs.urgb");
        t.Check(SetupBundle.Export(bundle, new IRgbDevice[]
        {
            new FakeDevice { Name = "Fan Hub", LedCount = 8 }, new FakeDevice { Name = "Fan Hub", LedCount = 8 },
        }).Ok, "fixture exports");
        var preview = SetupBundle.Preview(bundle, new IRgbDevice[]
        {
            new FakeDevice { Name = "Hub L", LedCount = 8 }, new FakeDevice { Name = "Hub R", LedCount = 8 },
        });
        t.Check(preview.Devices.Count(d => d.IsRemap) == 2, "precondition: both identical hubs match by identity");
        string? first = preview.Devices.First(d => d.IsRemap).LocalName;
        t.Equal(first, preview.DefaultChoices().DeviceRemap["Fan Hub"],
            "the first row wins in the presets, as in the preview's own diff (the presets used to take the last)");
        CleanConfig();
    }

    /*--- F9 and the asset cap ---*/

    static void Bounds(Harness t, string temp)
    {
        t.Section("F9: a bundle is bounded before it is opened");

        string huge = Path.Combine(temp, "huge.urgb");
        using (var fs = new FileStream(huge, FileMode.Create)) fs.SetLength(130L * 1024 * 1024);
        var big = SetupBundle.Preview(huge);
        t.Check(!big.Ok && big.Problem?.Contains("MB", StringComparison.Ordinal) == true,
            $"a file past the size cap is refused by its size ({big.Problem})");
        File.Delete(huge);

        string crowded = Path.Combine(temp, "crowded.urgb");
        var entries = new List<(string, byte[])> { ("manifest.json", Utf8(Manifest("[]", "[]"))) };
        for (int i = 0; i < SetupBundle.MaxEntries + 10; i++) entries.Add(($"junk/{i}.txt", new byte[] { 1 }));
        WriteZip(crowded, entries.ToArray());
        var many = SetupBundle.Preview(crowded);
        t.Check(!many.Ok && many.Problem?.Contains(SetupBundle.MaxEntries.ToString(), StringComparison.Ordinal) == true,
            $"a zip with more than {SetupBundle.MaxEntries} entries is refused ({many.Problem})");

        string listed = Path.Combine(temp, "listed.urgb");
        string oneEntry = "{\"Path\":\"config/profiles.json\",\"Root\":\"config\",\"Name\":\"profiles.json\",\"Group\":\"profiles\"}";
        WriteZip(listed, ("manifest.json", Utf8(Manifest("[" + string.Join(",", Enumerable.Repeat(oneEntry, SetupBundle.MaxEntries + 1)) + "]", "[]"))),
                         ("config/profiles.json", Utf8("[]")));
        t.Check(!SetupBundle.Preview(listed).Ok, "a manifest listing one small entry hundreds of times is refused");

        string twice = Path.Combine(temp, "twice.urgb");
        WriteZip(twice, ("manifest.json", Utf8(Manifest("[]", "[]"))), ("manifest.json", Utf8("{}")));
        var dup = SetupBundle.Preview(twice);
        t.Check(!dup.Ok && dup.Problem?.Contains("two files", StringComparison.Ordinal) == true,
            $"a zip naming one file twice is refused rather than resolved ({dup.Problem})");

        // An entry the manifest does not claim is never opened. This one
        // declares (and holds) more than the whole bundle budget; the reader
        // used to add it to the total and refuse the bundle for it.
        CleanConfig();
        string carrier = Path.Combine(temp, "carrier.urgb");
        using (var zip = new ZipArchive(new FileStream(carrier, FileMode.Create, FileAccess.Write, FileShare.None), ZipArchiveMode.Create))
        {
            void Text(string name, string text)
            {
                using var e = zip.CreateEntry(name).Open();
                e.Write(Utf8(text));
            }
            Text("manifest.json", Manifest(
                "[{\"Path\":\"config/profiles.json\",\"Root\":\"config\",\"Name\":\"profiles.json\",\"Group\":\"profiles\"}]", "[]"));
            Text("config/profiles.json", JsonSerializer.Serialize(new[] { new Profile { Name = "Plain" } }));
            using var s = zip.CreateEntry("extra/unclaimed.bin", CompressionLevel.Fastest).Open();
            var zeros = new byte[1024 * 1024];
            for (int i = 0; i < 130; i++) s.Write(zeros);
        }
        var carried = SetupBundle.Preview(carrier);
        t.Check(carried.Ok && carried.Profiles.Count == 1,
            $"an unclaimed entry is ignored, not read: the bundle previews ({carried.Problem})");

        t.Section("the image cap holds on import too");
        string fat = Path.Combine(temp, "fat.urgb");
        string scene = JsonSerializer.Serialize(new SceneStore { Scenes = { new LcdScene { Name = "Fat", Design = new LcdDesign { BackgroundImagePath = @"C:\old\fat.png" } } } });
        WriteZip(fat,
            ("manifest.json", Utf8(Manifest(
                "[{\"Path\":\"config/scenes.json\",\"Root\":\"config\",\"Name\":\"scenes.json\",\"Group\":\"scenes\"}]",
                "[{\"Path\":\"assets/0-fat.png\",\"OriginalPath\":\"C:\\\\old\\\\fat.png\",\"FileName\":\"fat.png\"}]"))),
            ("config/scenes.json", Utf8(scene)),
            ("assets/0-fat.png", new byte[25 * 1024 * 1024]));
        CleanConfig();
        var fatPreview = SetupBundle.Preview(fat);
        t.Check(fatPreview.Ok, $"a bundle with an oversized image still previews ({fatPreview.Problem})");
        t.Check(fatPreview.Warnings.Any(w => w.Contains("fat.png", StringComparison.Ordinal) && w.Contains("24 MB", StringComparison.Ordinal)),
            "...and says the image is too large and is left out");
        t.Check(!fatPreview.AssetBytes.ContainsKey("assets/0-fat.png"), "...without reading it (export's 24 MB cap now holds on import)");
        var fatApplied = SetupBundle.Apply(fatPreview, fatPreview.EverythingChoices());
        t.Check(fatApplied.Ok && SceneStore.TryParse(File.ReadAllText(AppPaths.Config("scenes.json")))!.Scenes.Single().Design.BackgroundImagePath == null,
            "the screen imports without the image rather than pointing at a path from another machine");
        CleanConfig();

        // What this pins is that the preview CAN run on a worker (it needs no UI
        // thread once the devices are surveyed); that the window actually uses
        // one is in ImportSetupWindow, which the harness does not drive.
        t.Section("the preview can run on a pool thread, from pre-surveyed devices");
        SaveProfiles(new Profile { Name = "Plain" });
        string plain = Path.Combine(temp, "plain.urgb");
        SetupBundle.Export(plain);
        var fingerprints = DeviceIdentity.Survey(new IRgbDevice[] { new FakeDevice { Name = "Keeb" } });
        var offThread = Task.Run(() => SetupBundle.PreviewAgainst(plain, fingerprints)).Result;
        t.Check(offThread.Ok && offThread.Profiles.Count == 1, "PreviewAgainst works from a pool thread with pre-surveyed devices");
        CleanConfig();
    }

    /*--- F16 ---*/

    static void PreferencesAllowList(Harness t, string temp)
    {
        t.Section("F16: preferences are an allow-list");
        CleanConfig();
        File.WriteAllText(AppPaths.Config("settings.json"), """
            {
              "MasterBrightness": 0.4, "StartMinimized": true, "FavoriteEffects": ["Rainbow"],
              "LianHandoffOnExit": true, "SdkServerEnabled": true, "SdkServerLan": true, "UseOpenRgb": true,
              "Cs2Enabled": true, "GsiToken": "theirs", "LianSpeedScale": 3.0, "GithubUpdateCheck": false,
              "FanLabels": { "fan:Fan #1": "Pump" }, "SomethingNewer": 5
            }
            """);
        string bundle = Path.Combine(temp, "prefs.urgb");
        t.Check(SetupBundle.Export(bundle).Ok, "fixture exports");
        // Somebody else's: a backup of THIS machine brings its fan settings back
        // (RemainingState pins that half).
        MadeOn(bundle, "OTHER");
        File.WriteAllText(AppPaths.Config("settings.json"),
            "{ \"MasterBrightness\": 1.0, \"LianHandoffOnExit\": false, \"MyOwnKey\": \"keep me\" }");

        var preview = SetupBundle.Preview(bundle);
        t.Check(preview.Preferences is { Status: ImportStatus.Differs }, "the preferences differ, and say so");
        var result = SetupBundle.Apply(preview, new ImportChoices { Preferences = true });
        t.Check(result.Ok && result.SettingsChanged, $"preferences import ({result.Problem})");

        var mine = JsonNode.Parse(File.ReadAllText(AppPaths.Config("settings.json")))!.AsObject();
        t.Check(mine["MasterBrightness"]?.GetValue<double>() == 0.4, "brightness is taken");
        t.Check(mine["StartMinimized"]?.GetValue<bool>() == true, "start minimized is taken");
        t.Check(mine["FavoriteEffects"]?.AsArray().Single()?.GetValue<string>() == "Rainbow", "starred effects are taken");
        t.Check(mine["LianHandoffOnExit"]?.GetValue<bool>() == false,
            "the fan hand-off switch is NOT taken from a bundle (the deny-list let it through)");
        foreach (string key in new[] { "SdkServerEnabled", "SdkServerLan", "UseOpenRgb", "Cs2Enabled", "GsiToken", "LianSpeedScale", "GithubUpdateCheck", "FanLabels" })
            t.Check(!mine.ContainsKey(key), $"{key} is not taken");
        t.Check(!mine.ContainsKey("SomethingNewer"), "a key this build does not know is not taken from a bundle either");
        t.Equal("keep me", mine["MyOwnKey"]?.GetValue<string>(), "a key of the user's own is left in their file");

        var again = SetupBundle.Preview(bundle);
        t.Check(again.Preferences is { Status: ImportStatus.Same },
            "once taken, the preferences read as already the same (the row used to read as an overwrite forever)");

        // A value of the wrong type would make the user's whole settings.json
        // unreadable at the next launch, and that resets every setting.
        File.WriteAllText(AppPaths.Config("settings.json"), "{ \"MasterBrightness\": \"loud\" }");
        string bad = Path.Combine(temp, "badprefs.urgb");
        t.Check(SetupBundle.Export(bad).Ok, "fixture exports");
        File.WriteAllText(AppPaths.Config("settings.json"), "{ \"MasterBrightness\": 0.7 }");
        var badPreview = SetupBundle.Preview(bad);
        SetupBundle.Apply(badPreview, new ImportChoices { Preferences = true, Rules = true });
        t.Equal("{ \"MasterBrightness\": 0.7 }", File.ReadAllText(AppPaths.Config("settings.json")),
            "settings this build cannot load are not merged into the user's file");
        CleanConfig();
    }

    /*--- the minor import items ---*/

    static void AssetsExtractWhole(Harness t, string temp)
    {
        t.Section("an image cut short by a crash is repaired, not reused");
        CleanConfig();
        string bg = Path.Combine(temp, "whole.png");
        byte[] png = Png(7);
        File.WriteAllBytes(bg, png);
        WriteScenes("Pic", bg);
        string bundle = Path.Combine(temp, "pic.urgb");
        t.Check(SetupBundle.Export(bundle).Ok, "fixture exports");
        CleanConfig();

        var first = SetupBundle.Preview(bundle);
        t.Check(SetupBundle.Apply(first, first.EverythingChoices()).Ok, "first import");
        string? landed = SceneStore.TryParse(File.ReadAllText(AppPaths.Config("scenes.json")))!.Scenes.Single().Design.BackgroundImagePath;
        t.Check(landed != null && File.ReadAllBytes(landed).AsSpan().SequenceEqual(png), "the image landed whole");

        // What a crash halfway through File.WriteAllBytes left: the right name,
        // the wrong bytes. Every later import saw the name and skipped it.
        File.WriteAllBytes(landed!, png.Take(10).ToArray());
        var second = SetupBundle.Preview(bundle);
        t.Check(SetupBundle.Apply(second, second.EverythingChoices()).Ok, "re-import");
        t.Check(File.ReadAllBytes(landed!).AsSpan().SequenceEqual(png), "the truncated image was replaced with the real one");
        t.Check(!Directory.EnumerateFiles(AppPaths.Config("lcd-assets"), "*.tmp").Any(), "no temporary file is left beside it");
        CleanConfig();
    }

    static void KeepsFiveBackups(Harness t, string temp)
    {
        t.Section("five import backups are kept, the new one included");
        CleanConfig();
        SaveProfiles(new Profile { Name = "Bundled", DeviceFrames = { ["Keeb"] = new[] { "FF0000" } } });
        string bundle = Path.Combine(temp, "backups.urgb");
        t.Check(SetupBundle.Export(bundle).Ok, "fixture exports");
        SaveProfiles(new Profile { Name = "Bundled", DeviceFrames = { ["Keeb"] = new[] { "00FF00" } } });
        for (int i = 1; i <= 7; i++) Directory.CreateDirectory(AppPaths.Config($"import-backup-2000010{i}-000000"));

        var preview = SetupBundle.Preview(bundle);
        var result = SetupBundle.Apply(preview, preview.EverythingChoices());
        t.Check(result.Ok && result.BackupDirectory != null, "an import that replaces a file leaves a backup");
        var kept = Directory.GetDirectories(AppPaths.ConfigDir, "import-backup-*");
        t.Equal(5, kept.Length, "five backups are kept, not six");
        t.Check(kept.Contains(result.BackupDirectory, StringComparer.OrdinalIgnoreCase), "...and the newest is one of them");
        CleanConfig();
    }

    static void FanConfigFlag(Harness t, string temp)
    {
        t.Section("an import that writes fan-config.json says so");
        CleanConfig();
        // fan-config.json lives in the local root, which other suites share:
        // whatever was there is put back afterwards.
        string fans = AppPaths.Local("fan-config.json");
        string? hadFans = File.Exists(fans) ? File.ReadAllText(fans) : null;
        try
        {
            if (hadFans != null) File.Delete(fans);
            File.WriteAllText(AppPaths.Config("calibration.json"), "{\"Devices\":{},\"Zones\":{}}");
            string calibrationOnly = Path.Combine(temp, "calibration-only.urgb");
            t.Check(SetupBundle.Export(calibrationOnly).Ok, "fixture exports");
            Directory.CreateDirectory(AppPaths.LocalDir);
            File.WriteAllText(fans, "{\"Curves\":{}}");
            string withFans = Path.Combine(temp, "with-fans.urgb");
            t.Check(SetupBundle.Export(withFans).Ok, "fixture exports");
            CleanConfig();
            File.Delete(fans);

            var without = SetupBundle.Apply(SetupBundle.Preview(calibrationOnly), new ImportChoices { MachineHardware = true });
            t.Check(without.Ok && without.CalibrationChanged && !without.FanConfigChanged,
                $"machine wiring without fan-config.json leaves the flag off ({without.Problem})");
            var with = SetupBundle.Apply(SetupBundle.Preview(withFans), new ImportChoices { MachineHardware = true });
            t.Check(with.Ok && with.FanConfigChanged, $"machine wiring that writes fan-config.json sets it ({with.Problem})");
            t.Check(File.Exists(fans), "...and the file is there for the hub to re-read");
        }
        finally
        {
            if (hadFans != null) File.WriteAllText(fans, hadFans);
            else { try { File.Delete(fans); } catch { } }
            CleanConfig();
        }
    }

    /*--- fixtures and helpers ---*/

    /// <summary>Hold a file the way a sync client or scanner does, so a read
    /// fails with a sharing violation.</summary>
    static FileStream Lock(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.None);

    /// <summary>Clear the session's "could not be read" marks this suite set, the
    /// only way the app clears them: a real load of a readable file.</summary>
    static void ReleaseUnreadable()
    {
        foreach (string name in new[] { "scenes.json", "lcd.json", "profiles.json" })
        {
            string path = AppPaths.Config(name);
            if (!ProfileStore.IsUnreadable(path)) continue;
            if (!File.Exists(path)) File.WriteAllText(path, name == "profiles.json" ? "[]" : "{}");
            ProfileStore.LoadJson<object>(path, name);
        }
    }

    static void SaveProfiles(params Profile[] profiles)
        => File.WriteAllText(AppPaths.Config("profiles.json"), JsonSerializer.Serialize(profiles));

    static List<Profile> ReadProfiles()
        => JsonSerializer.Deserialize<List<Profile>>(File.ReadAllText(AppPaths.Config("profiles.json"))) ?? new();

    static void WriteScenes(string name, string? background)
    {
        var design = new LcdDesign { SceneName = name, BackgroundImagePath = background, BgW = 320, BgH = 240 };
        design.Elements.Add(new LcdElement { Kind = LcdElementKind.Time, X = 10, Y = 10, FontSize = 40 });
        var store = new SceneStore();
        store.Scenes.Add(new LcdScene { Name = name, Design = design });
        File.WriteAllText(AppPaths.Config("scenes.json"), JsonSerializer.Serialize(store));
    }

    static void WriteLive(string? background, int elements)
    {
        var live = new LcdDesign { SceneName = "Live", BackgroundImagePath = background, BgW = 320, BgH = 240 };
        for (int i = 0; i < elements; i++) live.Elements.Add(new LcdElement { Kind = LcdElementKind.CpuTemp, X = 20 + i, Y = 90, FontSize = 30 });
        File.WriteAllText(AppPaths.Config("lcd.json"), JsonSerializer.Serialize(live));
    }

    static void WriteCanvas(params (string Device, double X)[] items)
    {
        var canvas = new CanvasLayout { Enabled = true };
        foreach (var (device, x) in items) canvas.Items.Add(new CanvasItem { Device = device, X = x, Y = 20, W = 100, H = 50 });
        File.WriteAllText(AppPaths.Config("canvas.json"), JsonSerializer.Serialize(canvas));
    }

    static string Manifest(string entries, string assets)
        => $"{{\"Kind\":\"{SetupBundle.KindMarker}\",\"FormatVersion\":1,\"MachineName\":\"OTHER\",\"Entries\":{entries},\"Assets\":{assets},\"Devices\":[]}}";

    static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

    static byte[] Png(byte seed)
    {
        var bytes = new List<byte> { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };
        for (int i = 0; i < 256; i++) bytes.Add((byte)(i ^ seed));
        return bytes.ToArray();
    }

    static void WriteZip(string path, params (string Name, byte[] Bytes)[] entries)
    {
        using var zip = new ZipArchive(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None), ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries)
        {
            using var s = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
            s.Write(bytes, 0, bytes.Length);
        }
    }

    static void CleanConfig()
    {
        foreach (string name in new[] { "profiles.json", "settings.json", "scenes.json", "lcd.json", "canvas.json", "calibration.json" })
        {
            try { File.Delete(AppPaths.Config(name)); } catch { }
        }
        try { Directory.Delete(AppPaths.Config("lcd-assets"), recursive: true); } catch { }
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(AppPaths.ConfigDir, "import-backup-*"))
                Directory.Delete(dir, recursive: true);
        }
        catch { }
    }
}
