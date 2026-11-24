namespace BertsIptvHub;

/// <summary>Periodically updates the channel list and EPG from the provider in the background.</summary>
public sealed class CatalogWarmup : BackgroundService
{
    private readonly HubOptions _o;
    private readonly ProviderClient _p;
    private readonly EpgIndex _epg;
    private readonly ILogger<CatalogWarmup> _log;

    public CatalogWarmup(HubOptions o, ProviderClient p, EpgIndex epg, ILogger<CatalogWarmup> log)
    {
        _o = o;
        _p = p;
        _epg = epg;
        _log = log;
    }

    public static readonly string[] LiveActions = { "get_live_categories", "get_live_streams" };

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(3), stop); } catch { return; }
        while (!stop.IsCancellationRequested)
        {
            if (_o.ProviderConfigured)
            {
                // Refreshes anything older than its cache time, so the TVs always get a recent copy instantly.
                foreach (var action in LiveActions)
                {
                    try
                    {
                        await _p.RefreshAsync("player_api.php", new[] { new KeyValuePair<string, string>("action", action) }, _p.CatalogTtl, stop);
                    }
                    catch (Exception ex) when (!stop.IsCancellationRequested) { _log.LogWarning("Channel list update ({A}) failed: {M}", action, ex.Message); }
                }
                try
                {
                    await _p.RefreshAsync("xmltv.php", Array.Empty<KeyValuePair<string, string>>(), _p.EpgTtl, stop);
                    await _epg.GetAsync(stop);
                }
                catch (Exception ex) when (!stop.IsCancellationRequested) { _log.LogWarning("EPG update failed: {M}", ex.Message); }
            }
            try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, _o.UpdateCheckMinutes)), stop); } catch { return; }
        }
    }
}

/// <summary>Minimal daily log file in data\logs, kept for 14 days.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly object _lock = new();

    public FileLoggerProvider(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.EnumerateFiles(dir, "hub-*.log"))
        {
            try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-14)) File.Delete(f); } catch { }
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(string line)
    {
        lock (_lock)
        {
            try { File.AppendAllText(Path.Combine(_dir, $"hub-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine); }
            catch { }
        }
    }

    public void Dispose() { }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _p;
        private readonly string _cat;

        public FileLogger(FileLoggerProvider p, string cat)
        {
            _p = p;
            _cat = cat.Contains('.') ? cat[(cat.LastIndexOf('.') + 1)..] : cat;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var lvl = logLevel switch
            {
                LogLevel.Information => "INFO",
                LogLevel.Warning => "WARN",
                LogLevel.Error => "ERROR",
                LogLevel.Critical => "CRIT",
                _ => logLevel.ToString().ToUpperInvariant(),
            };
            var msg = formatter(state, exception);
            if (exception != null) msg += " | " + exception.GetType().Name + ": " + exception.Message;
            _p.Write($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {lvl,-5} {_cat}: {msg}");
        }
    }
}
