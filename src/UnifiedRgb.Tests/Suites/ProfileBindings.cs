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

static class ProfileBindingSuite
{
    public static void Run(Harness t)
    {
        if (!AppPaths.ConfigDir.StartsWith(Isolation.Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Profile binding tests require isolated configuration.");
        var original = new[] { "profiles.json", "settings.json", "scenes.json" }.ToDictionary(
            name => AppPaths.Config(name), name => File.Exists(AppPaths.Config(name)) ? File.ReadAllBytes(AppPaths.Config(name)) : null);
        Exception? error = null;
        var thread = new Thread(() => { try { Check(t); } catch (Exception ex) { error = ex; } });
        try
        {
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
            if (error != null) throw error;
        }
        finally
        {
            foreach (var item in original)
                if (item.Value == null) File.Delete(item.Key); else File.WriteAllBytes(item.Key, item.Value);
        }
    }

    static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    static void Check(Harness t)
    {
        if (!AppPaths.ConfigDir.StartsWith(Isolation.Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Profile binding tests require isolated configuration.");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var vm = new MainViewModel(startServices: false);
        var store = (ProfileStore)typeof(MainViewModel).GetField("_store", flags)!.GetValue(vm)!;
        store.Profiles.Clear(); vm.Profiles.Clear();
        var profile = new Profile { Name = "Binding Before", Screen = "Unavailable screen", Show = "Unavailable show" };
        store.Profiles.Add(profile); vm.Profiles.Add(profile); store.SaveProfiles();
        store.Settings.AutomationRules = new() { new() { Process = "binding-game", Profile = profile.Name } };
        store.SaveSettings();
        vm.SelectedProfile = profile;
        vm.Lcd.Scenes.Sequences.Clear();
        var step = new SceneAction { Profile = profile.Name };
        vm.Lcd.Scenes.Sequences.Add(new SceneSequence { Name = "Binding show", Actions = new() { step } });
        vm.Shows.Init();
        // The normal constructor subscribes after detection, omitted in the safe seam.
        vm.Profiles.CollectionChanged += (_, _) => { vm.Shows.NotifyProfilesChanged(); };
        var stepPicker = new ComboBox();
        stepPicker.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("ProfileChoices") { Source = vm.Shows });
        stepPicker.SetBinding(Selector.SelectedItemProperty, new Binding("Profile")
            { Source = step, Mode = BindingMode.TwoWay, TargetNullValue = "(no change)" });
        var profilePicker = new ComboBox();
        profilePicker.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("Profiles") { Source = vm });
        profilePicker.SetBinding(Selector.SelectedItemProperty, new Binding("SelectedProfile") { Source = vm, Mode = BindingMode.TwoWay });
        Drain();

        t.Section("rename with live WPF selectors");
        vm.SaveProfileAs("Binding After"); Drain();
        t.Equal("Binding After", step.Profile, "show step survives profile-list churn");
        t.Equal("Binding After", stepPicker.SelectedItem, "bound step picker follows renamed profile");
        t.Equal("Binding After", store.Settings.AutomationRules.Single().Profile, "rule follows successful rename");
        t.Check(!store.Profiles.Any(p => p.Name == "Binding Before"), "old profile removed after references persist");

        t.Section("failed rename");
        string profilesBefore = File.ReadAllText(AppPaths.Config("profiles.json"));
        string settingsBefore = File.ReadAllText(AppPaths.Config("settings.json"));
        using (var locked = new FileStream(AppPaths.Config("profiles.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            vm.SaveProfileAs("Must not appear");
        Drain();
        t.Equal(profilesBefore, File.ReadAllText(AppPaths.Config("profiles.json")), "failed write keeps old profile file");
        t.Equal(settingsBefore, File.ReadAllText(AppPaths.Config("settings.json")), "failed write keeps saved references");
        t.Equal("Binding After", step.Profile, "failed write keeps live show reference");
        t.Equal("Binding After", vm.SelectedProfile!.Name, "failed write keeps selected profile");
        t.Check(!vm.Profiles.Any(p => p.Name == "Must not appear"), "failed write never publishes missing profile");

        t.Section("import reload with live profile selector");
        store.Profiles.Single().Screen = "Imported screen";
        store.Profiles.Single().Show = "Imported show";
        store.Profiles.Single().Wallpaper = "Imported wallpaper";
        store.SaveProfiles();
        vm.ReloadAfterImport(new ImportResult { Ok = true, ProfilesChanged = true }); Drain();
        t.Equal("Binding After", vm.SelectedProfile?.Name, "reload retains selection across selector reset");
        t.Equal("Imported screen", vm.PumpChoice.Screen, "reload refreshes pump target from imported profile");
        t.Equal("Imported show", vm.ShowChoice, "reload refreshes show target from imported profile");
        t.Equal("Imported wallpaper", vm.WallpaperChoice, "reload refreshes wallpaper target from imported profile");
        BindingOperations.ClearAllBindings(stepPicker);
        BindingOperations.ClearAllBindings(profilePicker);
    }
}
