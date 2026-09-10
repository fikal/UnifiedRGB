using System.IO;
using UnifiedRgb.Core;
using static UnifiedRgb.Tests.TestHelpers;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| Persistence: the files the app cannot afford to lose.        |
|                                                              |
| SafeFile is the write path underneath every store, so it is  |
| tested first and hardest: a partial write or an orphaned     |
| .tmp here becomes a corrupt profile everywhere else. The     |
| profile, scene and LCD sections then cover the reading end,  |
| where the recurring failure is not a malformed file but a    |
| well-formed one holding an explicit null, which defeats a    |
| property initializer and hands startup a null list.          |
\*-----------------------------------------------------------*/

static class StorageSuite
{
    public static void Run(Harness t)
    {
        t.Section("SafeFile atomic writes");
        {
            string dir = Path.Combine(Path.GetTempPath(), "unifiedrgb-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string f = Path.Combine(dir, "state.json");
            SafeFile.WriteAllText(f, "first");
            t.Equal("first", File.ReadAllText(f), "SafeFile create");
            SafeFile.WriteAllText(f, "second");
            t.Equal("second", File.ReadAllText(f), "SafeFile replace");
            t.Check(!File.Exists(f + ".tmp"), "SafeFile leaves no temp file");
            Directory.Delete(dir, recursive: true);
        }

        t.Section("SafeFile: parent dirs, no BOM, failure cleanup (#16 #20 #159 #162)");
        {
            string root = TempDir();
            try
            {
                string nested = Path.Combine(root, "a", "b", "x.json");
                SafeFile.WriteAllText(nested, "{}");
                t.Check(File.Exists(nested) && File.ReadAllText(nested) == "{}", "SafeFile creates missing parent directories");
                t.Check(!File.Exists(nested + ".tmp"), "SafeFile nested write leaves no .tmp");

                string utf = Path.Combine(root, "utf.txt");
                SafeFile.WriteAllText(utf, "héllo ☃");
                var bytes = File.ReadAllBytes(utf);
                t.Check(!(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF), "SafeFile writes UTF-8 without a BOM");
                t.Equal("héllo ☃", File.ReadAllText(utf), "SafeFile non-ASCII text round-trips");
                SafeFile.WriteAllText(utf, "second");
                t.Equal("second", File.ReadAllText(utf), "SafeFile second write replaces the first");
                t.Check(!File.Exists(utf + ".tmp"), "SafeFile replace leaves no .tmp");

                // The target is an existing DIRECTORY: the rename must fail, and the
                // half-written .tmp must not be left behind for the next save.
                string asDir = Path.Combine(root, "i-am-a-dir");
                Directory.CreateDirectory(asDir);
                bool threw = false;
                try { SafeFile.WriteAllText(asDir, "x"); } catch (Exception) { threw = true; }
                t.Check(threw, "SafeFile throws when the target cannot be replaced");
                t.Check(!File.Exists(asDir + ".tmp"), "SafeFile failure deletes its .tmp");
            }
            finally { try { Directory.Delete(root, recursive: true); } catch { } }

            // The whole harness depends on this: if the redirect ever stops working,
            // every persistence test below is silently editing the user's real files.
            t.Check(AppPaths.ConfigDir.StartsWith(Isolation.Root, StringComparison.OrdinalIgnoreCase),
                "tests are isolated: AppPaths.ConfigDir is redirected under the temp root");
            t.Check(AppPaths.LocalDir.StartsWith(Isolation.Root, StringComparison.OrdinalIgnoreCase),
                "tests are isolated: AppPaths.LocalDir is redirected under the temp root");
            t.Check(!AppPaths.ConfigDir.Contains(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                               StringComparison.OrdinalIgnoreCase),
                "tests are isolated: the real roaming profile is not in play");

            _ = AppPaths.ConfigDir;   // forces the cctor
            t.Check(Directory.Exists(AppPaths.ConfigDir), "AppPaths cctor creates ConfigDir");
            t.Check(Directory.Exists(AppPaths.LocalDir), "AppPaths cctor creates LocalDir (fan-config.json home)");
            t.Equal(Path.Combine(AppPaths.LocalDir, "fan-config.json"), AppPaths.Local("fan-config.json"), "AppPaths.Local joins under LocalDir");
        }

        t.Section("Profile.Screen: a profile carries its pump screen");
        {
            var p = new UnifiedRgb.App.Profile { Name = "Night", Screen = "Clock" };
            string json = System.Text.Json.JsonSerializer.Serialize(p);
            var back = System.Text.Json.JsonSerializer.Deserialize<UnifiedRgb.App.Profile>(json)!;
            t.Equal("Clock", back.Screen, "the bound screen round-trips through profiles.json");
            var old = System.Text.Json.JsonSerializer.Deserialize<UnifiedRgb.App.Profile>("{\"Name\":\"Old\",\"DeviceFrames\":{}}")!;
            t.Check(old.Screen == null, "a profile saved before screens were bound reads as unbound, not as an error");
        }

        t.Section("SceneStore / LcdDesign survive explicit nulls (B9)");
        {
            // A property initializer only runs when the key is ABSENT; an explicit
            // null defeats it, and startup then walks a null list.
            string dir = UnifiedRgb.Core.AppPaths.ConfigDir;
            string scenes = System.IO.Path.Combine(dir, "scenes.json");
            string lcd = System.IO.Path.Combine(dir, "lcd.json");

            File.WriteAllText(scenes, "{\"Scenes\":null,\"Sequences\":null}");
            var s1 = UnifiedRgb.App.SceneStore.Load();
            t.Check(s1.Scenes != null && s1.Sequences != null && s1.Scenes.Count == 0,
                "scenes.json with explicit null collections loads empty rather than throwing");

            File.WriteAllText(scenes, "{\"Scenes\":[null],\"Sequences\":[{\"Name\":\"S\",\"Actions\":null}]}");
            var s2 = UnifiedRgb.App.SceneStore.Load();
            t.Equal(0, s2.Scenes.Count, "a null scene entry is dropped");
            t.Equal(0, s2.Sequences[0].Actions.Count, "a sequence with null Actions loads empty");

            File.WriteAllText(scenes, "{\"Scenes\":[{\"Name\":\"  \",\"Design\":null}]}");
            t.Equal(0, UnifiedRgb.App.SceneStore.Load().Scenes.Count, "a scene with no usable name is dropped");

            File.WriteAllText(lcd, "{\"Elements\":null}");
            var d1 = UnifiedRgb.App.LcdDesign.Load();
            t.Check(d1.Elements != null && d1.Elements.Count == 0, "lcd.json with a null Elements list loads empty");

            File.WriteAllText(lcd, "{\"Elements\":[null]}");
            t.Equal(0, UnifiedRgb.App.LcdDesign.Load().Elements.Count, "a null element is dropped");

            // A null on a non-nullable number is a different kind of broken: the file
            // will not deserialize at all, so it is corrupt and the store's existing
            // handling takes over (defaults, original kept aside). What matters here
            // is that it is graceful rather than an exception out of startup.
            File.WriteAllText(lcd, "{\"Elements\":[],\"BgW\":null}");
            var d2 = UnifiedRgb.App.LcdDesign.Load();
            t.Check(d2 != null && d2.Elements != null, "a number that cannot deserialize falls back to a usable design");

            File.WriteAllText(lcd, "{\"Elements\":[{\"Kind\":\"Text\",\"FontSize\":0}]}");
            var d3 = UnifiedRgb.App.LcdDesign.Load();
            t.Check(d3.Elements.Count == 1 && d3.Elements[0].FontSize > 0,
                "a zero font size is repaired rather than handed to the renderer");

            try { File.Delete(scenes); } catch { }
            try { File.Delete(lcd); } catch { }
        }
    }
}
