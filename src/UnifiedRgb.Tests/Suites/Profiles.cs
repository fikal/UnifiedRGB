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
        var store = new ProfileStore();
        var dev = new FakeDevice { Name = "Absent-Later", LedCount = 2 };
        var frame = new[] { Rgb.Red, Rgb.Blue };
        try
        {
            t.Section("capture while the device is present");
            // Saved while the device was present, with a pump screen.
            var old = store.Capture("Old", new[] { ((IRgbDevice)dev, frame) }, screen: "Screen B");
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
}
