namespace UnifiedRgb.Core.Net;

/// <summary>Opt-in installer for the UnifiedRGB Chroma interop shim, so Chroma
/// SDK apps (Wallpaper Engine, Chroma games) drive UnifiedRGB devices. Chroma
/// hosts load RzChromaSDK64.dll from C:\Program Files\Razer Chroma SDK\bin\.
///
/// Gear-safe by construction:
///  - If a REAL Razer SDK is already there, it is BACKED UP (renamed to
///    RzChromaSDK64_real.dll) before our proxy shim takes its place. The proxy
///    forwards every call to the backup, so the person's Razer devices keep
///    lighting; UnifiedRGB just also taps the color stream.
///  - Disable restores the backup exactly.
///  - Only ever runs from the user's explicit toggle, never on launch.</summary>
public static class ChromaShimInstaller
{
    // Razer's Chroma SDK installs under Program Files (x86) (its 32-bit-style
    // installer convention) even for the 64-bit DLL - that's where hosts look.
    // We install to BOTH Program Files variants to be safe; the x86 one is the
    // canonical path.
    static string[] BinDirs => new[]
    {
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Razer Chroma SDK", "bin"),
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),    "Razer Chroma SDK", "bin"),
    };
    static string BinDir => BinDirs[0];   // canonical
    static string ActiveDll => System.IO.Path.Combine(BinDir, "RzChromaSDK64.dll");
    static string SourceDll => System.IO.Path.Combine(AppContext.BaseDirectory, "RzChromaSDK64.dll");

    // Both bitnesses live side by side in the SDK's bin folder: 64-bit hosts
    // (Wallpaper Engine, most games) load RzChromaSDK64.dll, 32-bit games load
    // RzChromaSDK.dll. The 32-bit shim is optional at build time.
    static readonly (string Dll, string Backup, string Source)[] Shims =
    {
        ("RzChromaSDK64.dll", "RzChromaSDK64_real.dll", "RzChromaSDK64.dll"),
        ("RzChromaSDK.dll",   "RzChromaSDK_real.dll",   "RzChromaSDK.dll"),
    };
    static string SourceOf(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, name);

    public static bool ShimAvailable => System.IO.File.Exists(SourceDll);

    /// <summary>Is OUR shim currently the active DLL?</summary>
    public static bool Installed => System.IO.File.Exists(ActiveDll) && IsOurs(ActiveDll);

    static bool IsOurs(string path)
    {
        try
        {
            var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            return (vi.ProductName ?? "").Contains("UnifiedRGB", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Install the shim. Backs up a real Razer DLL first so its gear
    /// keeps working via the proxy. Returns null on success, else why not.</summary>
    public static string? Install()
    {
        if (!ShimAvailable) return "The Chroma shim isn't bundled with this build yet.";
        string? firstErr = null;
        bool any = false;
        foreach (var dir in BinDirs)
            foreach (var (dll, backupName, sourceName) in Shims)
            {
                string source = SourceOf(sourceName);
                if (!System.IO.File.Exists(source)) continue;   // 32-bit shim not built into this release
                try
                {
                    string active = System.IO.Path.Combine(dir, dll);
                    string backup = System.IO.Path.Combine(dir, backupName);
                    System.IO.Directory.CreateDirectory(dir);
                    if (System.IO.File.Exists(active) && !IsOurs(active))
                    {
                        // A genuine Razer DLL: preserve it as the proxy's forward target.
                        if (System.IO.File.Exists(backup)) System.IO.File.Delete(backup);
                        System.IO.File.Move(active, backup);
                        Log.Info("chroma", $"backed up real Razer SDK {dll} in {dir}");
                    }
                    System.IO.File.Copy(source, active, overwrite: true);
                    if (dll == "RzChromaSDK64.dll") any = true;
                    Log.Info("chroma", $"shim installed -> {active}");
                }
                catch (Exception ex) { firstErr ??= ex.Message; }
            }
        return any ? null : firstErr;
    }

    /// <summary>Remove the shim and restore any backed-up real Razer DLL, in
    /// every location.</summary>
    public static string? Uninstall()
    {
        try
        {
            foreach (var dir in BinDirs)
                foreach (var (dll, backupName, _) in Shims)
                {
                    string active = System.IO.Path.Combine(dir, dll);
                    string backup = System.IO.Path.Combine(dir, backupName);
                    if (System.IO.File.Exists(active) && IsOurs(active)) System.IO.File.Delete(active);
                    if (System.IO.File.Exists(backup))
                    {
                        // Razer Synapse may have reinstalled its own DLL over our shim
                        // since we backed it up; then the backup is stale and Move
                        // would throw (aborting the other directory's cleanup).
                        if (System.IO.File.Exists(active)) System.IO.File.Delete(backup);
                        else System.IO.File.Move(backup, active);   // restore Razer's
                    }
                }
            Log.Info("chroma", "shim removed");
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }
}
