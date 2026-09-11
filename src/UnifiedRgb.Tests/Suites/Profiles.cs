using System.IO;
using UnifiedRgb.App;
using UnifiedRgb.Core;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| ProfileStore.Capture across a rename.                        |
|                                                              |
| A rename deletes the old profile before the new one is       |
| captured, so Capture's "keep what the profile already had    |
| for absent devices" rule found nothing under the new name    |
| and the remembered lighting of an unplugged or disabled      |
| device was lost on every rename. The old profile object is   |
| now handed over explicitly (carryFrom). Listed in Suites.cs  |
| as "Profiles" and run by name. Writes profiles.json in the   |
| harness's redirected config dir only.                        |
\*-----------------------------------------------------------*/
static class ProfilesSuite
{
    public static void Run(Harness t)
    {
        TheScreenInTheCase(t);
        ShowStepsBecomeProfiles(t);
        var store = new ProfileStore();
        var dev = new FakeDevice { Name = "Absent-Later", LedCount = 2 };
        var frame = new[] { Rgb.Red, Rgb.Blue };
        try
        {
            t.Section("capture while the device is present");
            // Saved while the device was present, with a pump screen.
            var old = store.Capture("Old", new[] { ((IRgbDevice)dev, frame) }, pump: PumpTarget.OfScreen("Screen B"));
            t.Check(old.DeviceFrames.Count == 1 && old.Screen == "Screen B", "fixture profile captured with one device and a screen");

            t.Section("rename carries the absent device's frame");
            // The rename, as SaveProfile does it: delete first, capture under
            // the new name with the device now ABSENT and no screen known.
            store.Delete("Old");
            var renamed = store.Capture("New", Array.Empty<(IRgbDevice, Rgb[])>(), carryFrom: old);
            t.Check(renamed.DeviceFrames.Count == 1, "rename keeps the absent device's saved frame (was 0)");
            t.Check(renamed.DeviceFrames.TryGetValue("Absent-Later", out var hex) && hex.Length == 2
                  && hex[0] == Rgb.Red.ToHex() && hex[1] == Rgb.Blue.ToHex(),
                "the carried frame is the one that was saved");
            t.Check(renamed.Screen == "Screen B", "rename keeps the profile's pump screen when none is known now");
            t.Check(store.Profiles.Any(p => p.Name == "New") && store.Profiles.All(p => p.Name != "Old"),
                "the store holds the new name only");

            t.Section("a present device overrides the carried frame");
            // A present device still wins over the carried data.
            var fresh = new[] { Rgb.Green, Rgb.Green };
            var again = store.Capture("New", new[] { ((IRgbDevice)dev, fresh) }, carryFrom: old);
            t.Check(again.DeviceFrames["Absent-Later"][0] == Rgb.Green.ToHex(), "a device that is present overrides the carried frame");

            t.Section("without carryFrom there is nothing to carry");
            // Without a carry-over the old behaviour is the documented loss:
            // pinned so the parameter cannot quietly become optional-and-unused.
            store.Delete("New");
            var bare = store.Capture("New", Array.Empty<(IRgbDevice, Rgb[])>());
            t.Check(bare.DeviceFrames.Count == 0, "without carryFrom a renamed profile has nothing to carry");
        }
        finally
        {
            store.Delete("Old");
            store.Delete("New");
        }
    }

    /*---------------- a show is a timeline of profiles ----------------*/

    /// <summary>A step used to name a pump scene of its own. It names a profile
    /// now, because a profile already carries the lights, the pump screen and
    /// the screen in the case - and a second way to set one of the three only
    /// created an argument about which won.
    ///
    /// The migration has to be honest rather than clever: an existing file must
    /// not quietly lose steps, and must not gain a profile nobody made.</summary>
    static void ShowStepsBecomeProfiles(Harness t)
    {
        t.Section("show steps become profiles");

        var profiles = new List<Profile>
        {
            new() { Name = "Night",  Screen = "Signal Path" },
            new() { Name = "Day",    Screen = "Clock" },
            new() { Name = "Also Day", Screen = "Clock" },      // two claim one screen
            new() { Name = "No screen" },
        };

        var store = new SceneStore
        {
            Sequences =
            {
                new SceneSequence
                {
                    Name = "Evening",
                    Actions =
                    {
                        new SceneAction { Scene = "Signal Path" },               // one owner -> adopted
                        new SceneAction { Scene = "Clock" },                     // two owners -> stranded
                        new SceneAction { Scene = "Gone" },                      // no owner  -> stranded
                        new SceneAction { Scene = "Signal Path", Profile = "Day" }, // already said what it wanted
                        new SceneAction { Profile = "Night" },                    // nothing to do
                    },
                },
            },
        };

        int stranded = store.MigrateSceneSteps(profiles);
        var steps = store.Sequences[0].Actions;

        t.Equal(2, stranded, "the two steps with no single answer are reported, not guessed at");
        t.Equal("Night", steps[0].Profile, "a screen exactly one profile carries becomes that profile");
        t.Check(steps[1].Profile == null, "a screen two profiles carry is left for the user to decide");
        t.Check(steps[2].Profile == null, "a screen no profile carries is left alone too");
        t.Equal("Day", steps[3].Profile, "a step that already named a profile keeps it");
        t.Equal("Night", steps[4].Profile, "a step with no screen is untouched");

        foreach (var a in steps)
            t.Check(a.Scene == null, "the retired scene field is cleared on every step, migrated or not");

        // Running it again must not undo anything or re-report.
        t.Equal(0, store.MigrateSceneSteps(profiles), "a second run has nothing left to do");
        t.Equal("Night", steps[0].Profile, "...and does not disturb what the first run decided");
    }

    /*---------------- the third panel ----------------*/

    /// <summary>A profile already carried the lights and the pump LCD; this is
    /// the screen in the case. Wallpaper Engine is driven by NAME, and the name
    /// has to survive the same journeys the rest of a profile does.</summary>
    static void TheScreenInTheCase(Harness t)
    {
        var store = new ProfileStore();
        var dev = new FakeDevice { Name = "Wallpaper-Test", LedCount = 2 };
        var frame = new[] { Rgb.Red, Rgb.Blue };
        var one = new[] { ((IRgbDevice)dev, frame) };

        try
        {
            t.Section("a wallpaper rides on a profile");

            var p = store.Capture("Wp", one, wallpaper: "Night");
            t.Equal("Night", p.Wallpaper, "a profile remembers the wallpaper it was saved with");

            // Null is "this machine has no opinion", which is what a save on a
            // box without Wallpaper Engine passes. It must not strip a name a
            // different machine set, or opening a bundle on a laptop would quietly
            // empty every profile.
            var kept = store.Capture("Wp", one);
            t.Equal("Night", kept.Wallpaper, "saving with nothing known keeps the name the profile already had");

            // Empty is the user choosing "leave the wallpaper alone" on purpose,
            // and that has to be able to clear a name set earlier - otherwise the
            // picker is a one-way door.
            var cleared = store.Capture("Wp", one, wallpaper: "");
            t.Check(cleared.Wallpaper == null, "choosing 'leave it alone' clears a name the profile had");

            var reset = store.Capture("Wp", one, wallpaper: "Day");
            t.Equal("Day", reset.Wallpaper, "...and a later choice sets it again");

            // A rename is the journey that has lost things before: the old
            // profile is deleted before the new one is captured, so the carry
            // has to be explicit.
            var renamed = store.Capture("Wp Renamed", one, carryFrom: reset);
            t.Equal("Day", renamed.Wallpaper, "a rename carries the wallpaper across");

            t.Section("reading Wallpaper Engine's profile list");
            // Its config is its own business and it is keyed by Windows user
            // name, so the section is found by NAME anywhere in the tree rather
            // than at a fixed path. Both shapes it could reasonably take are
            // accepted, and anything unreadable means "no profiles" rather than
            // an exception on the way to showing a picker.
            string dir = Path.Combine(Path.GetTempPath(), "urgb-we-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                // THE REAL SHAPE, copied from a live Wallpaper Engine config
                // after making a profile called Matrix: general/profiles is a
                // LIST OF OBJECTS, each carrying "name" alongside the layout and
                // the per-monitor wallpapers. This fixture is the reason the
                // reader is not a guess.
                //
                // Note what else each entry carries: a key called "profile",
                // whose value is an OBJECT. The reader only accepts a string, so
                // that key cannot be mistaken for the name however the candidate
                // keys are ordered - which is worth a fixture rather than worth
                // trusting, because the failure would be a picker full of blanks
                // rather than an error.
                string live = "{\"ryanb\":{\"general\":{\"profiles\":[{\"layout\":0,\"name\":\"Matrix\","
                            + "\"profile\":{},\"selectedwallpapers\":{\"Monitor0\":{\"file\":\"C:/x/scene.pkg\"},"
                            + "\"Monitor1\":{\"file\":\"C:/x/rain.mp4\"}}}]}}}";
                var fromLive = Read(dir, live);
                t.Equal(1, fromLive.Length, "the real config yields one profile");
                t.Equal("Matrix", fromLive[0], "...named from its \"name\", not from its \"profile\" key");

                t.Equal(2, Read(dir, "{\"ryanb\":{\"general\":{\"profiles\":{\"Night\":{},\"Day\":{}}}}}").Length,
                    "an object keyed by name");
                t.Equal(2, Read(dir, "{\"u\":{\"profiles\":[\"Night\",\"Day\"]}}").Length,
                    "a list of names");
                t.Equal(2, Read(dir, "{\"u\":{\"profiles\":[{\"name\":\"Night\"},{\"name\":\"Day\"}]}}").Length,
                    "a list of objects carrying a name");
                t.Equal("Day", Read(dir, "{\"u\":{\"profiles\":[\"Night\",\"Day\"]}}")[0],
                    "sorted, so the picker does not reshuffle itself between reads");
                t.Equal(1, Read(dir, "{\"u\":{\"profiles\":[\"Night\",\"night\",\"  \"]}}").Length,
                    "duplicates and blanks are dropped");
                t.Equal(0, Read(dir, "{\"u\":{\"general\":{}}}").Length,
                    "a config with no profiles section is no profiles");
                t.Equal(0, Read(dir, "{ not json at all").Length,
                    "a config we cannot parse is no profiles, not a crash");

                t.Equal(0, UnifiedRgb.App.Services.WallpaperEngine
                        .ReadProfiles(Path.Combine(dir, "does-not-exist.json")).Length,
                    "a missing config is no profiles, not a crash");

                t.Section("which wallpaper profile is already up");
                // Wallpaper Engine does not record this. Its config has a
                // "profile" slot beside the live wallpapers and it holds an
                // empty object whichever profile was applied, so the only honest
                // answer is which saved profile's per-monitor wallpapers equal
                // the ones currently showing. Shape copied from the live file.
                static string Cfg(string liveA, string liveB, string matrixA, string matrixB) =>
                    "{\"u\":{\"general\":{"
                    + "\"wallpaperconfig\":{\"profile\":{},\"selectedwallpapers\":{"
                    + "\"Monitor0\":{\"file\":\"" + liveA + "\"},\"Monitor1\":{\"file\":\"" + liveB + "\"}}},"
                    + "\"profiles\":["
                    + "{\"name\":\"Matrix\",\"selectedwallpapers\":{"
                    + "\"Monitor0\":{\"file\":\"" + matrixA + "\"},\"Monitor1\":{\"file\":\"" + matrixB + "\"}}},"
                    + "{\"name\":\"Diablo\",\"selectedwallpapers\":{"
                    + "\"Monitor0\":{\"file\":\"d0.mp4\"},\"Monitor1\":{\"file\":\"d1.mp4\"}}}]}}}";

                static string? Active(string dir, string json)
                {
                    string path = Path.Combine(dir, "active.json");
                    File.WriteAllText(path, json);
                    return UnifiedRgb.App.Services.WallpaperEngine.ReadActiveProfile(path);
                }

                t.Equal("Matrix", Active(dir, Cfg("m0.pkg", "m1.mp4", "m0.pkg", "m1.mp4")),
                    "the profile whose wallpapers are on screen is the active one");
                t.Check(Active(dir, Cfg("something-else.pkg", "m1.mp4", "m0.pkg", "m1.mp4")) == null,
                    "one monitor changed by hand matches nothing, so asking again restores it");
                t.Equal("Matrix", Active(dir, Cfg("M0.PKG", "m1.mp4", "m0.pkg", "m1.mp4")),
                    "case is not a difference worth reloading every monitor over");
                t.Check(Active(dir, "{\"u\":{\"general\":{\"profiles\":[{\"name\":\"X\"}]}}}") == null,
                    "nothing to compare answers unknown rather than naming the first profile");
                t.Check(Active(dir, "{ not json") == null, "an unreadable config answers unknown, not a crash");
            }
            finally { try { Directory.Delete(dir, recursive: true); } catch { } }

            // Nothing is asked of Wallpaper Engine for a name it does not have.
            // The control channel would take it, do nothing and say nothing.
            t.Check(!UnifiedRgb.App.Services.WallpaperEngine.Apply(null), "no wallpaper asked for is not a request");
            t.Check(!UnifiedRgb.App.Services.WallpaperEngine.Apply("   "), "nor is a blank one");
        }
        finally
        {
            store.Delete("Wp");
            store.Delete("Wp Renamed");
        }

        static string[] Read(string dir, string json)
        {
            string path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, json);
            return UnifiedRgb.App.Services.WallpaperEngine.ReadProfiles(path);
        }
    }
}
