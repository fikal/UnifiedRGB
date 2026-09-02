namespace UnifiedRgb.App;

/// <summary>The ONE place the app reads its own version. It was computed three
/// ways (view model, SupportService, UpdateService) with two different shapes;
/// everything now agrees on the 3-part build number the release tags use.</summary>
public static class AppInfo
{
    public static Version Version { get; } = Normalize(typeof(AppInfo).Assembly.GetName().Version);

    /// <summary>"1.0.18"</summary>
    public static string VersionString => Version.ToString(3);

    /// <summary>"v1.0.18" (title bar).</summary>
    public static string VersionText => $"v{VersionString}";

    static Version Normalize(Version? v)
        => v == null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(0, v.Build));
}
