using UnifiedRgb.App.Services;

namespace UnifiedRgb.App;

/// <summary>The entry point, ahead of WPF's generated App.Main (the csproj's
/// StartupObject). A released build may hand over to its installed copy, or
/// restart itself so .NET unpacks its native DLLs into a folder only
/// administrators can write - and both have to happen before WPF loads the
/// first of those DLLs. See AppInstall.</summary>
static class Program
{
    [STAThread]
    static void Main()
    {
        if (AppInstall.HandOffBeforeStartup()) return;
        App.Main();
    }
}
