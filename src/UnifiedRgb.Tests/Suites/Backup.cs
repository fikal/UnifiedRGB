using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using UnifiedRgb.App;
using UnifiedRgb.App.Services;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Effects;
using static UnifiedRgb.Tests.TestHelpers;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| Setup backup and restore: the whole configuration as one     |
| portable file.                                               |
|                                                              |
| Three things here are worth more than the rest.              |
|                                                              |
| The ROUND TRIP, because a backup that does not restore is    |
| worse than no backup: the user finds out at the moment they  |
| need it. That includes the LCD background, which is the      |
| only part of a setup that is not JSON and the part a         |
| naive implementation silently drops (designs reference an    |
| absolute path that means nothing on the other machine).      |
|                                                              |
| The REFUSALS, because the failure mode that matters is not   |
| "import did nothing", it is "import did half of it". Every   |
| broken bundle below has to be turned away with a reason,     |
| with the user's own files untouched afterwards.              |
|                                                              |
| And IDENTITY, because matching devices by name is what this  |
| feature exists to stop doing: the same keyboard renamed is   |
| still the same keyboard, and two identical fans are not one. |
|                                                              |
| Everything writes under the isolated config root or a temp   |
| directory of its own, and cleans up: see Isolation.          |
\*-----------------------------------------------------------*/

static class BackupSuite
{
    public static void Run(Harness t)
    {
        // The harness refuses to start without the redirect, but this suite
        // rewrites profiles.json and scenes.json wholesale, so it says so
        // again at its own door rather than trusting a distant gate.
        if (!AppPaths.ConfigDir.StartsWith(Isolation.Root, StringComparison.OrdinalIgnoreCase))
        {
            t.Check(false, "REFUSING to run: AppPaths is not redirected, and this suite rewrites real config files");
            return;
        }

        Identity(t);

        string temp = TempDir();
        try
        {
            RoundTrip(t, temp);
            Refusals(t, temp);
            Destinations(t);
            ImportRegressions(t, temp);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
            CleanConfig();
        }
    }

    /*--- where a bundle is allowed to write ---*/

    /// <summary>A bundle is a file from the internet, and every field in its
    /// manifest is something its author chose. These are the answers to "what
    /// can it make us overwrite" and "can it lie about what a file IS".</summary>
    static void Destinations(Harness t)
    {
        t.Section("a bundle cannot choose where it lands");
        static UnifiedRgb.App.Services.BundleEntry E(string root, string name, string group)
            => new() { Root = root, Name = name, Group = group, Path = "config/" + name };

        string legit = UnifiedRgb.App.Services.SetupBundle.TargetPathFor(
            E(UnifiedRgb.App.Services.SetupBundle.RootConfig, "hardware.json", "machine"))!;
        t.Check(legit != null, "a file we publish, in its own group, is accepted");
        t.Equal(AppPaths.Config("hardware.json"), legit, "...and lands in our config folder");

        // Escapes. Nothing here may resolve to a path at all.
        foreach (string name in new[] { @"..\..\Startup\evil.cmd", "sub/dir.json", "..", ".", "" })
            t.Check(UnifiedRgb.App.Services.SetupBundle.TargetPathFor(
                E(UnifiedRgb.App.Services.SetupBundle.RootConfig, name, "machine")) == null,
                $"a name that is not a bare published file name is refused: '{name}'");

        // A root of its own invention.
        t.Check(UnifiedRgb.App.Services.SetupBundle.TargetPathFor(
            E("startup", "hardware.json", "machine")) == null, "an unknown root is refused");

        // The mislabel. Confining the FOLDER is not enough: the group decides
        // which consent checkbox covers the entry and which sentence the
        // preview shows, so settings.json carried as "machine" would be
        // described to the user as fan curves and hub wiring on the way in.
        t.Check(UnifiedRgb.App.Services.SetupBundle.TargetPathFor(
            E(UnifiedRgb.App.Services.SetupBundle.RootConfig, "settings.json", "machine")) == null,
            "a real file of ours carried under the WRONG group is refused");
        t.Check(UnifiedRgb.App.Services.SetupBundle.TargetPathFor(
            E(UnifiedRgb.App.Services.SetupBundle.RootConfig, "settings.json", "settings")) != null,
            "...and is accepted under its own group");

        // A file we never publish, even sitting in the right folder.
        t.Check(UnifiedRgb.App.Services.SetupBundle.TargetPathFor(
            E(UnifiedRgb.App.Services.SetupBundle.RootConfig, "backend.json", "settings")) == null,
            "a config file we deliberately never export cannot be written by a bundle");
        t.Check(UnifiedRgb.App.Services.SetupBundle.TargetPathFor(
            E(UnifiedRgb.App.Services.SetupBundle.RootConfig, "anything.json", "settings")) == null,
            "a file name we do not publish at all is refused");
    }

    /*--- 3. stable device identity ---*/

    static void Identity(Harness t)
    {
        t.Section("device identity");

        // The whole point: the user renames a device (or the other machine's
        // driver spells the model differently) and it is still the same
        // device. Name matching gets this wrong; identity must not.
        var before = new FakeDevice { Name = "Corsair K95 RGB Platinum", LedCount = 26 };
        var after = new FakeDevice { Name = "Big Keyboard", LedCount = 26 };
        t.Equal(DeviceIdentity.For(before, 0), DeviceIdentity.For(after, 0),
            "identity survives a rename");
        t.Check(DeviceIdentity.Signature(before).Contains("26"),
            "the signature is derived from the device's shape, not its name");

        // Different hardware must not collide, or a restore would paint a
        // keyboard's frame onto a fan hub.
        var different = new FakeDevice { Name = "Corsair K95 RGB Platinum", LedCount = 8 };
        t.Check(DeviceIdentity.For(before, 0) != DeviceIdentity.For(different, 0),
            "a different LED count is a different identity, same name or not");

        // Two identical devices are the case a name can never resolve.
        var survey = DeviceIdentity.Survey(new IRgbDevice[]
        {
            new FakeDevice { Name = "Fan Hub", LedCount = 8 },
            new FakeDevice { Name = "Fan Hub", LedCount = 8 },
        });
        t.Equal(2, survey.Count, "every device is fingerprinted");
        t.Check(survey[0].Identity != survey[1].Identity,
            "two identical devices get two identities (the ordinal tells them apart)");

        // Reproducible across calls, because a bundle written today is read
        // months later by another process. A per-process hash would not be.
        t.Equal(DeviceIdentity.For(before, 0), DeviceIdentity.For(before, 0),
            "identity is reproducible, not a per-process hash");

        t.Check(DeviceIdentity.Survey(null).Count == 0, "no devices is an empty survey, not a throw");
    }

    /*--- 1. export, then import onto a "different machine" ---*/

    static void RoundTrip(Harness t, string temp)
    {
        t.Section("export and import round trip");

        string background = Path.Combine(temp, "aurora.png");
        File.WriteAllBytes(background, FakePng());
        WriteSetup(t, background);

        // The exporting machine's keyboard, and a strip that the receiving
        // machine will not have.
        var keeb = new FakeDevice { Name = "Keeb", LedCount = 26 };
        var strip = new FakeDevice { Name = "Strip", LedCount = 8 };

        string bundlePath = Path.Combine(temp, "setup.urgb");
        var exported = SetupBundle.Export(bundlePath, new IRgbDevice[] { keeb, strip });
        t.Check(exported.Ok, $"export succeeds ({exported.Problem})");
        t.Check(File.Exists(bundlePath), "the bundle is one file the user can copy anywhere");
        t.Equal(1, exported.AssetCount, "the screen background travels inside the bundle");
        t.Check(exported.FileCount >= 5, "the config files travel too");
        t.Check(!Directory.EnumerateFiles(temp, "*.tmp").Any(), "export leaves no temporary file behind");

        // Now become another machine: nothing of the setup survives, and the
        // background image is gone from where the design said it was. Only
        // the bundle is left.
        CleanConfig();
        File.Delete(background);

        // ...whose keyboard the driver names differently. Same shape, so the
        // identity matches and the profile's device keys can be remapped.
        var localKeeb = new FakeDevice { Name = "K95 RGB", LedCount = 26 };
        var preview = SetupBundle.Preview(bundlePath, new IRgbDevice[] { localKeeb });
        t.Check(preview.Ok, $"a bundle we just wrote previews cleanly ({preview.Problem})");
        t.Check(preview.Manifest != null && preview.Manifest.MachineName.Length > 0,
            "the manifest records the machine the bundle came from");
        t.Check(preview.Summary.Count > 0, "the preview is a set of sentences the UI can show");

        var remapped = preview.Devices.FirstOrDefault(d => d.BundleName == "Keeb");
        t.Check(remapped != null && remapped.IsRemap && remapped.LocalName == "K95 RGB",
            "a renamed device is remapped by identity, not dropped");
        var missing = preview.Devices.FirstOrDefault(d => d.BundleName == "Strip");
        t.Check(missing != null && missing.LocalName == null,
            "a device that is not on this machine is reported as missing rather than guessed at");

        t.Equal(1, preview.Profiles.Count(i => i.Status == ImportStatus.New), "the profile reads as new here");
        t.Equal(1, preview.Screens.Count(i => i.Status == ImportStatus.New), "the screen reads as new here");
        t.Equal(1, preview.Palettes.Count(i => i.Status == ImportStatus.New), "the palette reads as new here");
        t.Check(preview.Canvas is { Status: ImportStatus.New }, "the desk layout reads as new here");

        var result = SetupBundle.Apply(preview, preview.DefaultChoices());
        t.Check(result.Ok, $"applying a preview of our own bundle succeeds ({result.Problem})");
        t.Check(result.ProfilesChanged && result.ScenesChanged && result.CanvasChanged && result.SettingsChanged,
            "the result tells the caller which stores it must re-read");

        // Profiles: back, and keyed by THIS machine's device name.
        var profiles = ReadProfiles();
        var night = profiles.FirstOrDefault(p => p.Name == "Night");
        t.Check(night != null, "the profile came back");
        t.Check(night != null && night.DeviceFrames.ContainsKey("K95 RGB"),
            "the profile's frames were remapped onto this machine's device name");
        t.Check(night != null && !night.DeviceFrames.ContainsKey("Keeb"),
            "the bundle's device name is gone, not left beside the remapped one");
        var effects = night?.Effects;
        t.Check(effects is { Count: 1 } && effects[0].Device == "K95 RGB",
            "the effect assignment was remapped too");
        string[]? frame = null;
        night?.DeviceFrames.TryGetValue("K95 RGB", out frame);
        t.Check(frame is { Length: 2 } && frame[0] == "FF0000",
            "the saved colors survived the trip");

        // Screens: back, with the background rewritten to the copy that
        // travelled inside the bundle.
        var scenes = SceneStore.Load();
        var aurora = scenes.Scenes.FirstOrDefault(s => s.Name == "Aurora");
        t.Check(aurora != null, "the saved screen came back");
        string? bg = aurora?.Design.BackgroundImagePath;
        t.Check(bg != null && bg != background, "the background path was rewritten off the old machine's path");
        t.Check(bg != null && File.Exists(bg), "the background image itself is on this machine and the screen renders");
        t.Check(bg != null && bg.StartsWith(AppPaths.ConfigDir, StringComparison.OrdinalIgnoreCase),
            "imported images live inside the config folder, so the next backup picks them up");
        t.Equal(1, scenes.Sequences.Count, "the show came back");
        t.Check(LcdDesign.Load().BackgroundImagePath is { } live && File.Exists(live),
            "the screen that was on the panel came back with a usable background too");

        // Desk layout: back, and remapped.
        var canvas = CanvasLayout.Load();
        t.Equal(1, canvas.Items.Count, "the desk layout came back");
        t.Equal("K95 RGB", canvas.Items[0].Device, "the desk layout was remapped onto this machine's device name");

        // Palettes: back. Preferences were NOT taken, because the default
        // choices only add what is missing.
        var settings = ReadSettings();
        t.Check(settings.SavedPalettes?.Any(p => p.Name == "Sunset") == true, "the saved palette came back");
        t.Check(settings.CustomColors?.Contains("ABCDEF") == true, "the color swatches came back");
        t.Check(Math.Abs(settings.MasterBrightness - 1.0) < 0.001,
            "preferences are not taken by default, only the things that were missing");

        // Re-importing the same bundle is now a no-op, which is what "Same"
        // means and what stops a double click doubling anything.
        var again = SetupBundle.Preview(bundlePath, new IRgbDevice[] { localKeeb });
        t.Check(again.Profiles.All(i => i.Status == ImportStatus.Same), "a second import of the same bundle finds nothing to do");
        t.Check(ImportPreview.IsEmpty(again.DefaultChoices()), "and ticks nothing");

        /*--- 2. the conflict preview ---*/

        t.Section("conflict preview does not apply anything");

        // A profile of the same name, but different: exactly the case a blind
        // import destroys.
        var mine = new Profile { Name = "Night" };
        mine.DeviceFrames["K95 RGB"] = new[] { "0000FF", "0000FF" };
        File.WriteAllText(AppPaths.Config("profiles.json"), JsonSerializer.Serialize(new[] { mine }));
        string beforeText = File.ReadAllText(AppPaths.Config("profiles.json"));

        var conflict = SetupBundle.Preview(bundlePath, new IRgbDevice[] { localKeeb });
        var row = conflict.Profiles.FirstOrDefault(i => i.Name == "Night");
        t.Check(row is { Status: ImportStatus.Differs }, "a colliding profile is reported as a conflict");
        t.Check(row != null && row.Detail.Length > 0, "and it says what would happen in words");
        t.Equal(beforeText, File.ReadAllText(AppPaths.Config("profiles.json")),
            "previewing writes nothing at all");
        t.Check(!conflict.DefaultChoices().Profiles.Contains("Night"),
            "a conflict is NOT ticked by default: importing must not lose a profile silently");
        t.Check(conflict.EverythingChoices().Profiles.Contains("Night"),
            "restoring a backup over the top is still one call away");

        // Applying the safe default leaves the user's own profile alone.
        SetupBundle.Apply(conflict, conflict.DefaultChoices());
        t.Equal("0000FF", ReadProfiles().First(p => p.Name == "Night").DeviceFrames["K95 RGB"][0],
            "the user's own profile is untouched when the conflict is left unticked");

        // Ticking it replaces the profile, and keeps a copy of what it replaced.
        var overwrite = SetupBundle.Preview(bundlePath, new IRgbDevice[] { localKeeb });
        var choices = overwrite.DefaultChoices();
        choices.Profiles.Add("Night");
        var overwritten = SetupBundle.Apply(overwrite, choices);
        t.Check(overwritten.Ok, $"an explicit overwrite applies ({overwritten.Problem})");
        t.Equal("FF0000", ReadProfiles().First(p => p.Name == "Night").DeviceFrames["K95 RGB"][0],
            "the bundle's profile replaced the local one when the user asked for it");
        t.Check(overwritten.BackupDirectory != null && Directory.Exists(overwritten.BackupDirectory),
            "what was replaced is kept: an import's only undo");

        // The remapping is the user's to change, not ours to assume.
        t.Section("remapping is explicit");
        var manual = SetupBundle.Preview(bundlePath, new IRgbDevice[] { localKeeb });
        var manualChoices = manual.EverythingChoices();
        manualChoices.DeviceRemap.Clear();      // the user said "no, leave the names alone"
        t.Check(SetupBundle.Apply(manual, manualChoices).Ok, "an import with the remapping cleared still applies");
        t.Check(ReadProfiles().First(p => p.Name == "Night").DeviceFrames.ContainsKey("Keeb"),
            "clearing the remapping keeps the bundle's own device names, as asked");
    }

    /*--- 4. every way a bundle can be broken ---*/

    static void Refusals(Harness t, string temp)
    {
        t.Section("a broken bundle is refused, never half-applied");

        // Rebuild a known-good setup so "nothing changed" is checkable.
        string background = Path.Combine(temp, "again.png");
        File.WriteAllBytes(background, FakePng());
        CleanConfig();
        WriteSetup(t, background);
        string intact = File.ReadAllText(AppPaths.Config("profiles.json"));

        void Refused(string path, string what)
        {
            var p = SetupBundle.Preview(path);
            t.Check(!p.Ok, $"{what} is refused");
            t.Check(!string.IsNullOrWhiteSpace(p.Problem), $"{what} is refused WITH A REASON");
            var applied = SetupBundle.Apply(p, new ImportChoices());
            t.Check(!applied.Ok, $"{what} cannot be applied even if a caller tries");
            t.Equal(intact, File.ReadAllText(AppPaths.Config("profiles.json")), $"{what} left the setup untouched");
        }

        Refused(Path.Combine(temp, "does-not-exist.urgb"), "a missing file");

        string junk = Path.Combine(temp, "junk.urgb");
        File.WriteAllText(junk, "this is not a zip, it is a sentence");
        Refused(junk, "a file that is not a zip");

        // A real bundle chopped in half: the shape a cancelled download or a
        // full disk leaves behind, and the one that used to be applied until
        // it ran out of file.
        string whole = Path.Combine(temp, "whole.urgb");
        var devices = new IRgbDevice[] { new FakeDevice { Name = "Keeb", LedCount = 26 } };
        t.Check(SetupBundle.Export(whole, devices).Ok, "a bundle to truncate");
        byte[] bytes = File.ReadAllBytes(whole);
        string truncated = Path.Combine(temp, "truncated.urgb");
        File.WriteAllBytes(truncated, bytes.Take(bytes.Length / 2).ToArray());
        Refused(truncated, "a truncated bundle");

        // A zip that is simply not ours.
        string alien = Path.Combine(temp, "alien.urgb");
        WriteZip(alien, ("hello.txt", "not a setup"));
        Refused(alien, "a zip with no manifest");

        // Made by a future build. Refusing beats guessing: a higher format
        // version exists because something changed that this build cannot
        // honour.
        string future = Path.Combine(temp, "future.urgb");
        WriteZip(future, ("manifest.json",
            $"{{\"Kind\":\"{SetupBundle.KindMarker}\",\"FormatVersion\":{SetupBundle.CurrentFormatVersion + 1},\"Entries\":[],\"Assets\":[],\"Devices\":[]}}"));
        var fromFuture = SetupBundle.Preview(future);
        t.Check(!fromFuture.Ok, "a bundle from a newer build is refused");
        t.Check(fromFuture.Problem?.Contains("newer", StringComparison.OrdinalIgnoreCase) == true,
            "and the reason says so, so the user knows to update rather than to re-export");

        // A manifest promising a file the zip does not hold.
        string incomplete = Path.Combine(temp, "incomplete.urgb");
        WriteZip(incomplete, ("manifest.json",
            $"{{\"Kind\":\"{SetupBundle.KindMarker}\",\"FormatVersion\":1,\"Entries\":"
            + "[{\"Path\":\"config/profiles.json\",\"Root\":\"config\",\"Name\":\"profiles.json\",\"Group\":\"profiles\"}],"
            + "\"Assets\":[],\"Devices\":[]}"));
        Refused(incomplete, "a bundle missing a file its manifest lists");

        // A hostile manifest trying to write outside the config folder. The
        // bundle is a file from the internet; nothing in it gets to choose a
        // path.
        string escape = Path.Combine(temp, "escape.urgb");
        WriteZip(escape,
            ("manifest.json",
             $"{{\"Kind\":\"{SetupBundle.KindMarker}\",\"FormatVersion\":1,\"Entries\":"
             + "[{\"Path\":\"config/evil\",\"Root\":\"config\",\"Name\":\"..\\\\..\\\\evil.cmd\",\"Group\":\"profiles\"}],"
             + "\"Assets\":[],\"Devices\":[]}"),
            ("config/evil", "whatever"));
        Refused(escape, "a bundle naming a file outside the config folder");

        t.Section("a bundle from a build that knows more");
        // The forward-compatibility promise: an entry this build does not
        // understand is skipped and SAID, and the rest still imports.
        string newer = Path.Combine(temp, "newer.urgb");
        WriteZip(newer,
            ("manifest.json",
             $"{{\"Kind\":\"{SetupBundle.KindMarker}\",\"FormatVersion\":1,\"AppVersion\":\"9.9.9\",\"MachineName\":\"OTHER\",\"Entries\":"
             + "[{\"Path\":\"config/holograms.json\",\"Root\":\"config\",\"Name\":\"holograms.json\",\"Group\":\"holograms\"}],"
             + "\"Assets\":[],\"Devices\":[]}"),
            ("config/holograms.json", "{}"));
        var tolerant = SetupBundle.Preview(newer);
        t.Check(tolerant.Ok, "a bundle carrying one unknown section still reads");
        t.Check(tolerant.Warnings.Any(w => w.Contains("holograms", StringComparison.OrdinalIgnoreCase)),
            "and the unknown section is reported rather than silently dropped");
        t.Check(!File.Exists(AppPaths.Config("holograms.json")),
            "a file this build does not understand is never written");
    }

    /*--- fixtures and helpers ---*/

    /// <summary>A complete little setup on disk: one profile, one screen with
    /// a background, one show, a desk layout and a palette.</summary>
    static void WriteSetup(Harness t, string backgroundPath)
    {
        var profile = new Profile { Name = "Night", Screen = "Aurora" };
        profile.DeviceFrames["Keeb"] = new[] { "FF0000", "00FF00" };
        profile.Effects = new List<EffectAssignment>
        {
            new() { Device = "Keeb", Offset = 0, Count = 2, Effect = "Rainbow" },
        };
        File.WriteAllText(AppPaths.Config("profiles.json"), JsonSerializer.Serialize(new[] { profile }));

        var design = new LcdDesign { SceneName = "Aurora", BackgroundImagePath = backgroundPath, BgW = 320, BgH = 240 };
        design.Elements.Add(new LcdElement { Kind = LcdElementKind.Time, X = 10, Y = 10, FontSize = 40 });
        var scenes = new SceneStore();
        scenes.Scenes.Add(new LcdScene { Name = "Aurora", Design = design });
        scenes.Sequences.Add(new SceneSequence { Name = "Show", Actions = { new SceneAction { DelaySeconds = 2, Scene = "Aurora" } } });
        File.WriteAllText(AppPaths.Config("scenes.json"), JsonSerializer.Serialize(scenes));

        var live = new LcdDesign { SceneName = "Aurora", BackgroundImagePath = backgroundPath, BgW = 320, BgH = 240 };
        live.Elements.Add(new LcdElement { Kind = LcdElementKind.CpuTemp, X = 20, Y = 90, FontSize = 30 });
        File.WriteAllText(AppPaths.Config("lcd.json"), JsonSerializer.Serialize(live));

        var canvas = new CanvasLayout { Enabled = true };
        canvas.Items.Add(new CanvasItem { Device = "Keeb", X = 10, Y = 20, W = 100, H = 50 });
        File.WriteAllText(AppPaths.Config("canvas.json"), JsonSerializer.Serialize(canvas));

        var settings = new SettingsData
        {
            MasterBrightness = 0.5,
            CustomColors = new[] { "ABCDEF" },
            SavedPalettes = new List<SavedPalette> { new() { Name = "Sunset", Colors = new[] { "FF8800", "220044" } } },
        };
        File.WriteAllText(AppPaths.Config("settings.json"), JsonSerializer.Serialize(settings));

        t.Check(File.Exists(AppPaths.Config("profiles.json")), "the fixture setup is on disk");
    }

    static List<Profile> ReadProfiles()
        => JsonSerializer.Deserialize<List<Profile>>(File.ReadAllText(AppPaths.Config("profiles.json"))) ?? new();

    static SettingsData ReadSettings()
        => JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(AppPaths.Config("settings.json"))) ?? new();

    /// <summary>Enough of a PNG header to be a plausible image file. Nothing
    /// decodes it here; what matters is that the BYTES travel and land.</summary>
    static byte[] FakePng()
    {
        var bytes = new List<byte> { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };
        for (int i = 0; i < 256; i++) bytes.Add((byte)i);
        return bytes.ToArray();
    }

    static void WriteZip(string path, params (string Name, string Text)[] entries)
    {
        using var zip = new ZipArchive(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None), ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            using var s = zip.CreateEntry(name).Open();
            byte[] bytes = new UTF8Encoding(false).GetBytes(text);
            s.Write(bytes, 0, bytes.Length);
        }
    }

    static void ImportRegressions(Harness t, string temp)
    {
        t.Section("import regressions");
        CleanConfig();
        var a = new FakeDevice { Name = "Hub A", LedCount = 8 };
        var b = new FakeDevice { Name = "Hub B", LedCount = 8 };
        ProfileStore.Save(AppPaths.Config("profiles.json"), new[] { new Profile
        {
            Name = "Twin hubs", DeviceFrames = new() { ["Hub A"] = new[] { "FF0000" }, ["Hub B"] = new[] { "0000FF" } }
        } }, "profiles.json");
        File.WriteAllText(AppPaths.Config("calibration.json"), "{\"Devices\":{\"Hub B\":{\"Red\":0.5}},\"Zones\":{}}");
        string bundle = Path.Combine(temp, "twins.urgb");
        t.Check(SetupBundle.Export(bundle, new IRgbDevice[] { a, b }).Ok, "twin setup exports");
        var preview = SetupBundle.Preview(bundle, new IRgbDevice[] { b });
        t.Check(preview.Devices.Single(x => x.BundleName == "Hub A").LocalName == null, "absent twin stays unmapped");
        var result = SetupBundle.Apply(preview, preview.EverythingChoices());
        t.Check(result.Ok && result.CalibrationChanged, "calibration accompanies a complete restore");
        var profile = ReadProfiles().Single();
        t.Equal("FF0000", profile.DeviceFrames["Hub A"][0], "absent twin's frame survives");
        t.Equal("0000FF", profile.DeviceFrames["Hub B"][0], "surviving twin keeps its own frame");
        var conflicting = preview.EverythingChoices();
        conflicting.DeviceRemap["Hub A"] = "Hub B";
        string before = File.ReadAllText(AppPaths.Config("profiles.json"));
        t.Check(!SetupBundle.Apply(preview, conflicting).Ok, "overlapping manual mapping is refused");
        t.Equal(before, File.ReadAllText(AppPaths.Config("profiles.json")), "refused mapping changes no profile");

        string first = Path.Combine(temp, "first-image.png"), second = Path.Combine(temp, "second-image.png");
        File.WriteAllBytes(first, new byte[] { 1 }); File.WriteAllBytes(second, new byte[] { 2 });
        new SceneStore { Scenes = new() { new LcdScene { Name = "Image", Design = new LcdDesign { BackgroundImagePath = first } } } }.Save();
        bundle = Path.Combine(temp, "images.urgb");
        SetupBundle.Export(bundle);
        new SceneStore { Scenes = new() { new LcdScene { Name = "Image", Design = new LcdDesign { BackgroundImagePath = second } } } }.Save();
        t.Equal(ImportStatus.Differs, SetupBundle.Preview(bundle).Screens.Single().Status, "different background bytes are a conflict");
        File.WriteAllBytes(second, new byte[] { 1 });
        t.Equal(ImportStatus.Same, SetupBundle.Preview(bundle).Screens.Single().Status, "same background at another path remains identical");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var vm = new LcdDesignerViewModel(() => false, () => false, _ => false, () => Array.Empty<string>(), () => null);
                vm.InitScenes();
                vm.SelectedSceneName = "Image";
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var sequencerField = typeof(LcdDesignerViewModel).GetField("_sequencer", flags)!;
                var oldSequencer = (SceneSequencer)sequencerField.GetValue(vm)!;
                oldSequencer.Start(new SceneSequence { Name = "Previous", Actions = new() { new SceneAction { DelaySeconds = 100 } } });
                new SceneStore { Scenes = new() { new LcdScene { Name = "Imported" } } }.Save();
                vm.ReloadScenes(false);
                t.Check(vm.SelectedSceneName == null, "scene reload clears a stale selected scene name");
                t.Check(!oldSequencer.Running, "old sequence stops before replacing imported store");
                vm.ReloadScenes(false);
                t.Equal("Imported", string.Join(",", vm.SceneNames), "scene reload replaces old collection without duplicates");
                var lcd = (LcdController)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LcdController));
                var lcdField = typeof(LcdDesignerViewModel).GetField("_lcd", flags)!;
                lcdField.SetValue(vm, lcd);
                try
                {
                    var importedDesign = LcdDesign.Default();
                    importedDesign.Elements.Clear();
                    importedDesign.Save();
                    vm.ReloadScenes(true);
                    t.Equal(0, lcd.Design.Elements.Count, "current screen import replaces the live controller design");
                }
                finally { lcdField.SetValue(vm, null); }
                var sceneField = typeof(LcdDesignerViewModel).GetField("_scenes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                ((SceneStore)sceneField.GetValue(vm)!).Save();
                t.Equal("Imported", SceneStore.Load().Scenes.Single().Name, "later save preserves imported scenes");

                // Initialize only the in-memory import surface; the production
                // constructor opens devices, sensors and servers and is not safe here.
                var main = (MainViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
                void Field(string name, object value) => typeof(MainViewModel).GetField(name, flags)!.SetValue(main, value);
                var store = new ProfileStore();
                Field("_store", store);
                foreach (string property in new[] { "Profiles", "AutoRules", "Schedules", "SensorRules", "CustomColors", "Devices" })
                {
                    var field = typeof(MainViewModel).GetField($"<{property}>k__BackingField", flags)!;
                    field.SetValue(main, Activator.CreateInstance(field.FieldType));
                }
                Field("<Lcd>k__BackingField", vm);
                var bake = new LianBakeService(null!, () => Array.Empty<UnifiedRgb.Core.Devices.LianLiWireless>());
                Field("_bake", bake);
                main.AutoRules.Add(new UnifiedRgb.Core.Automation.AutomationRule { Process = "old", Profile = "old" });
                main.CustomColors.Add(new Rgb(255, 0, 0));
                var importedSettings = new SettingsData
                {
                    MasterBrightness = .35,
                    CustomColors = new[] { "0000FF" },
                    FavoriteEffects = new() { "Rainbow" },
                    AutomationRules = new() { new() { Process = "new", Profile = "new" } },
                    Schedules = new() { new() { Profile = "schedule" } },
                    SensorRules = new() { new() { Profile = "sensor" } }
                };
                File.WriteAllText(AppPaths.Config("settings.json"), JsonSerializer.Serialize(importedSettings));
                double brightness = Master.Brightness;
                try
                {
                    main.ReloadAfterImport(new ImportResult { Ok = true, SettingsChanged = true });
                    t.Equal("new", main.AutoRules.Single().Process, "reload publishes imported app rules");
                    t.Equal("schedule", main.Schedules.Single().Profile!, "reload publishes imported schedules");
                    t.Equal("sensor", main.SensorRules.Single().Profile, "reload publishes imported sensor rules");
                    t.Equal("0000FF", main.CustomColors.Single().ToHex(), "reload replaces stale swatches");
                    t.Check(Math.Abs(Master.Brightness - .35) < .0001, "reload changes live master brightness");
                    t.Check(main.IsFavoriteEffect("Rainbow"), "reload changes cached favorites");
                    main.AutoRules.Single().Process = "edited";
                    main.PersistAutomation();
                    var saved = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(AppPaths.Config("settings.json")))!;
                    t.Equal("edited", saved.AutomationRules!.Single().Process, "editor objects update the imported settings store");
                }
                finally { bake.Stop(); Master.Brightness = brightness; }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw failure;
    }

    /// <summary>Leave the isolated config root as this suite found it. Other
    /// suites load these same files, so a fixture left behind here becomes a
    /// mystery failure over there.</summary>
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
