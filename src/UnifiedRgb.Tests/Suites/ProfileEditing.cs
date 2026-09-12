using System.IO;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using UnifiedRgb.App;
using UnifiedRgb.App.Services;
using UnifiedRgb.Core;

namespace UnifiedRgb.Tests;

static class ProfileEditingSuite
{
    public static void Run(Harness t)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { UnavailableWallpaper(t); ManualChanges(t); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure != null) throw new Exception("Profile editing regression", failure);
    }

    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Stop(MainViewModel vm)
    {
        vm.Shows.Dispose(); vm.Lcd.Dispose(); vm.Lighting.StopAndDrain();
        ((LianBakeService)typeof(MainViewModel).GetField("_bake", Instance)!.GetValue(vm)!).Stop();
    }

    static void UnavailableWallpaper(Harness t)
    {
        t.Section("unavailable wallpaper survives refresh and Save");
        var type = typeof(WallpaperEngine);
        var fields = new[] { "_exe", "_searched", "_configPath", "_configStamp", "_profiles", "_active" }
            .Select(n => type.GetField(n, BindingFlags.Static | BindingFlags.NonPublic)!).ToArray();
        var old = fields.Select(f => f.GetValue(null)).ToArray();
        string dir = Path.Combine(Isolation.Root, "fake-wallpaper");
        Directory.CreateDirectory(dir);
        string cfg = Path.Combine(dir, "config.json");
        File.WriteAllText(cfg, "{\"general\":{\"profiles\":[{\"name\":\"Available\"}]}}");
        fields[0].SetValue(null, Path.Combine(dir, "never-launched.exe"));
        fields[1].SetValue(null, true);
        fields[2].SetValue(null, cfg);
        fields[3].SetValue(null, DateTime.MinValue);
        MainViewModel? vm = null;
        try
        {
            vm = new MainViewModel(false);
            vm.Profiles.Clear();
            var p = new Profile { Name = "Imported wallpaper fixture", Wallpaper = "Remote only" };
            vm.Profiles.Add(p); vm.SelectedProfile = p;
            var picker = new ComboBox();
            picker.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.WallpaperProfiles)) { Source = vm });
            picker.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(vm.WallpaperChoice)) { Source = vm, Mode = BindingMode.TwoWay });
            vm.RefreshWallpaperProfiles();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            t.Equal("Remote only", vm.WallpaperChoice, "refresh preserves the unavailable selection");
            t.Equal("Remote only", picker.SelectedItem, "the real bound picker still displays the saved name");
            vm.SaveActiveProfile();
            t.Equal("Remote only", new ProfileStore().Profiles.Single(x => x.Name == p.Name).Wallpaper,
                "saving other changes preserves an unavailable wallpaper on disk");

            File.WriteAllText(cfg, "{\"general\":{\"profiles\":[]}}");
            fields[3].SetValue(null, DateTime.MinValue);
            vm.RefreshWallpaperProfiles();
            t.Check(vm.WallpaperAvailable && vm.WallpaperProfiles.Contains("Remote only"),
                "an existing binding stays editable when this installation has no profiles");
            picker.SelectedItem = MainViewModel.NoWallpaper;
            vm.SaveActiveProfile();
            t.Check(new ProfileStore().Profiles.Single(x => x.Name == p.Name).Wallpaper == null,
                "explicitly choosing leave-alone clears the saved binding");
            vm.RefreshWallpaperProfiles();
            t.Check(!vm.WallpaperProfiles.Contains("Remote only"), "a cleared missing entry goes away on refresh");
            new ProfileStore().Delete(p.Name);
        }
        finally
        {
            if (vm != null) Stop(vm);
            for (int i = 0; i < fields.Length; i++) fields[i].SetValue(null, old[i]);
        }
    }

    static void ManualChanges(Harness t)
    {
        t.Section("manual lighting edits invalidate show profile identity");
        var vm = new MainViewModel(false);
        try
        {
            var dev = new FakeDevice { Name = "Manual show fixture", LedCount = 1 };
            vm.Devices.Add(dev); vm.Profiles.Clear();
            var red = new Profile { Name = "Saved red", DeviceFrames = new() { [dev.Name] = new[] { "FF0000" } } };
            vm.Profiles.Add(red); vm.ApplyProfile(red); vm.SelectedDevice = dev;
            vm.Lcd.Scenes.Sequences.Clear();
            vm.Lcd.Scenes.Sequences.Add(new SceneSequence { Name = "Red loop", Actions = new()
                { new SceneAction { Profile = red.Name, DelaySeconds = 60 } } });
            vm.Shows.Init(); vm.Shows.Start("Red loop");
            var seq = typeof(ShowViewModel).GetField("_sequencer", Instance)!.GetValue(vm.Shows)!;
            void Step() => typeof(SceneSequencer).GetMethod("Step", Instance)!.Invoke(seq, null);
            t.Equal(red.Name, vm.CaptureState().AppliedProfileName, "loading a profile records its live identity");
            vm.WallpaperChoice = "Unsaved wallpaper";
            vm.ShowChoice = "Unsaved show";
            vm.PumpChoice = new MainViewModel.PumpRow("Unsaved screen", "Unsaved screen");
            t.Equal(red.Name, vm.CaptureState().AppliedProfileName, "picker edits do not pretend the output changed");
            vm.Hex = "0000FF";
            t.Check(vm.CaptureState().AppliedProfileName == null, "manual color editing invalidates the applied name");
            Step(); vm.Lighting.Applier.Drain(2000);
            t.Equal(Rgb.Red, dev.Last![0], "the actual show step restores saved red after a manual blue edit");
            t.Equal(red.Name, vm.CaptureState().AppliedProfileName, "a show apply records its new identity");
            int applies = 0; vm.ApplyWallpaperProfile = _ => { applies++; return false; };
            Step();
            t.Equal(0, applies, "an unchanged consecutive step is still deduplicated");
            vm.SelectedEffectChoice = vm.Effects.First(e => e.Name == "Rainbow Wave");
            t.Check(vm.CaptureState().AppliedProfileName == null, "a manual effect change invalidates the name too");
            Step();
            t.Equal(0, vm.CaptureState().Effects.Count, "the next step restores the saved static profile after an effect edit");
        }
        finally { Stop(vm); }
    }
}
