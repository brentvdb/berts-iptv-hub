using System.Net;

namespace BertsIptvHub;

/// <summary>Two HTTP clients to the provider: one for API/EPG calls, one for long-running streams.</summary>
public sealed class HttpClients : IDisposable
{
    public HttpClient Api { get; }
    public HttpClient Streams { get; }

    public HttpClients(HubOptions o)
    {
        Api = Make(o.Provider.UserAgent, streams: false);
        Streams = Make(o.Provider.UserAgent, streams: true);
    }

    private static HttpClient Make(string userAgent, bool streams)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = streams ? DecompressionMethods.None : DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            // Never try to "drain" an endless stream for connection reuse: close it at once,
            // so the provider sees the connection end immediately when we switch channels.
            MaxResponseDrainSize = streams ? 0 : 1024 * 1024,
        };
        var client = new HttpClient(handler)
        {
            Timeout = streams ? Timeout.InfiniteTimeSpan : TimeSpan.FromMinutes(5),
        };
        if (!string.IsNullOrWhiteSpace(userAgent))
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        return client;
    }

    public void Dispose()
    {
        Api.Dispose();
        Streams.Dispose();
    }
}
