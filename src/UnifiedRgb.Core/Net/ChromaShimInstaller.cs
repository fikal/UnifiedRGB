namespace UnifiedRgb.Core.Net;

/// <summary>Opt-in installer for the UnifiedRGB Chroma interop shim, so Chroma
/// SDK apps (Wallpaper Engine, Chroma games) drive UnifiedRGB devices. Chroma
/// hosts load RzChromaSDK64.dll from C:\Program Files\Razer Chroma SDK\bin\.
///
/// Gear-safe by construction:
///  - If a REAL Razer SDK is already there, it is BACKED UP (renamed to
///    RzChromaSDK64_real.dll, and RzChromaSDK_real.dll for the 32-bit DLL)
///    before our proxy shim takes its place. The proxy forwards every call to
///    the backup, so the person's Razer devices keep lighting; UnifiedRGB
///    just also taps the color stream.
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

    // Both bitnesses live side by side in the SDK's bin folder: 64-bit hosts
    // (Wallpaper Engine, most games) load RzChromaSDK64.dll, 32-bit games load
    // RzChromaSDK.dll. The 32-bit shim is optional at build time. Row 0 is the
    // one that decides "available" and "installed"; the bundled shim carries
    // the same file name as the DLL it replaces.
    static readonly (string Dll, string Backup)[] Shims =
    {
        ("RzChromaSDK64.dll", "RzChromaSDK64_real.dll"),
        ("RzChromaSDK.dll",   "RzChromaSDK_real.dll"),
    };
    static string ActiveDll => System.IO.Path.Combine(BinDir, Shims[0].Dll);
    static string SourceOf(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, name);

    public static bool ShimAvailable => System.IO.File.Exists(SourceOf(Shims[0].Dll));

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
        // Success = the 64-bit shim landed in the canonical dir, the one hosts
        // load from and Installed checks. A copy into the other Program Files
        // used to count too, so a sharing violation on the real path (a host
        // had the DLL mapped) returned "ok" while the toggle snapped back off.
        bool canonicalOk = false;
        foreach (var dir in BinDirs)
            foreach (var (dll, backupName) in Shims)
            {
                string source = SourceOf(dll);
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
                    if (dir == BinDir && dll == Shims[0].Dll) canonicalOk = true;
                    Log.Info("chroma", $"shim installed -> {active}");
                }
                catch (Exception ex) { firstErr ??= Describe(ex, dll); }
            }
        return canonicalOk ? null : firstErr;
    }

    /// <summary>Remove the shim and restore any backed-up real Razer DLL, in
    /// every location. Per entry, like Install: one locked file (a host still
    /// has the shim mapped) must not skip the other bitness or directory and
    /// leave Razer's DLL parked as _real.dll there.</summary>
    public static string? Uninstall()
    {
        string? firstErr = null;
        foreach (var dir in BinDirs)
            foreach (var (dll, backupName) in Shims)
            {
                try
                {
                    string active = System.IO.Path.Combine(dir, dll);
                    string backup = System.IO.Path.Combine(dir, backupName);
                    if (System.IO.File.Exists(active) && IsOurs(active)) System.IO.File.Delete(active);
                    if (System.IO.File.Exists(backup))
                    {
                        // Razer Synapse may have reinstalled its own DLL over our shim
                        // since we backed it up; then the backup is stale and Move
                        // would throw.
                        if (System.IO.File.Exists(active)) System.IO.File.Delete(backup);
                        else System.IO.File.Move(backup, active);   // restore Razer's
                    }
                }
                catch (Exception ex) { firstErr ??= Describe(ex, dll); }
            }
        if (firstErr == null) Log.Info("chroma", "shim removed");
        return firstErr;
    }

    // A host that has the DLL mapped (Wallpaper Engine, a Chroma game) makes
    // the copy/delete fail with a sharing violation - say what to do about it.
    static string Describe(Exception ex, string dll) =>
        ex is IOException && (ex.HResult & 0xFFFF) == 32 /* ERROR_SHARING_VIOLATION */
            ? $"{dll} is in use - close Wallpaper Engine and any Chroma game, then try again"
            : ex.Message;
}
