namespace BertsIptvHub;

public sealed class HubOptions
{
    /// <summary>Address the hub listens on. 0.0.0.0 = all network adapters.</summary>
    public string ListenUrl { get; set; } = "http://0.0.0.0:8090";

    public ProviderOptions Provider { get; set; } = new();

    /// <summary>Credentials the TV apps use to log into the hub (not the provider).</summary>
    public string LocalUsername { get; set; } = "home";
    public string LocalPassword { get; set; } = "changeme";

    /// <summary>How much recent stream data is kept so a TV that joins mid-stream starts instantly.</summary>
    public int JoinBufferKB { get; set; } = 4096;

    /// <summary>Close the provider connection when no TV has been watching for this long.</summary>
    public int IdleTimeoutSeconds { get; set; } = 20;

    /// <summary>Reconnect attempts if the provider drops the stream.</summary>
    public int UpstreamRetries { get; set; } = 5;

    /// <summary>Treat the provider stream as dead if no data arrives for this long.</summary>
    public int UpstreamStallSeconds { get; set; } = 15;

    /// <summary>Per-TV send queue (chunks of up to 64 KB). A TV that falls this far behind is dropped.</summary>
    public int SubscriberQueueChunks { get; set; } = 1024;

    /// <summary>How often the background updater checks whether the channel list / EPG are due for an update.</summary>
    public int UpdateCheckMinutes { get; set; } = 30;

    public double CatalogCacheHours { get; set; } = 6;
    public double EpgCacheHours { get; set; } = 12;
    public double ShortEpgCacheMinutes { get; set; } = 10;

    public string DataDir { get; set; } = "data";

    public string DataDirFull { get; private set; } = "";

    public bool ProviderConfigured =>
        !string.IsNullOrWhiteSpace(Provider.Url) &&
        !Provider.Url.Contains("your-provider-host", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(Provider.Username);

    public void Resolve(string baseDir)
    {
        DataDirFull = Path.IsPathRooted(DataDir) ? DataDir : Path.Combine(baseDir, DataDir);
        Directory.CreateDirectory(DataDirFull);
    }
}

public sealed class ProviderOptions
{
    public string Url { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string UserAgent { get; set; } = "okhttp/4.12.0";
    /// <summary>Container requested from the provider for live channels. Keep "ts" — the relay serves MPEG-TS.</summary>
    public string LiveExtension { get; set; } = "ts";
}

public static class AppInfo
{
    private static readonly string Informational =
        (System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion ?? "0.0.0";

    /// <summary>e.g. "1.0.0"</summary>
    public static string Version => Informational.Split('+')[0];

    /// <summary>CI run number, 0 for local builds.</summary>
    public static int Build =>
        Informational.Contains("+build.") && int.TryParse(Informational[(Informational.IndexOf("+build.") + 7)..], out var b) ? b : 0;

    /// <summary>e.g. "v1.0.0 (build 12)"</summary>
    public static string Display => Build > 0 ? $"v{Version} (build {Build})" : $"v{Version} (local build)";

    public static byte[]? Resource(string name)
    {
        using var s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (s is null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
