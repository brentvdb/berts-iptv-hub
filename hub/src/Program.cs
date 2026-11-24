using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BertsIptvHub;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory, // a Windows service starts in System32; read config next to the exe
});
#if WINDOWS_SERVICE
builder.Host.UseWindowsService(o => o.ServiceName = "BertsIptvHub");
#endif

var opts = builder.Configuration.GetSection("Hub").Get<HubOptions>() ?? new HubOptions();
opts.Resolve(AppContext.BaseDirectory);

builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(opts.DataDirFull, "logs")));
builder.WebHost.UseUrls(opts.ListenUrl);

builder.Services.AddSingleton(opts);
builder.Services.AddSingleton<HttpClients>();
builder.Services.AddSingleton<ProviderClient>();
builder.Services.AddSingleton<StreamHub>();
builder.Services.AddSingleton<FavoritesStore>();
builder.Services.AddSingleton<SyncService>();
builder.Services.AddSingleton<EpgIndex>();
builder.Services.AddHostedService<CatalogWarmup>();

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Hub");
var hub = app.Services.GetRequiredService<StreamHub>();
var provider = app.Services.GetRequiredService<ProviderClient>();
var favorites = app.Services.GetRequiredService<FavoritesStore>();
var sync = app.Services.GetRequiredService<SyncService>();
var clients = app.Services.GetRequiredService<HttpClients>();
var epg = app.Services.GetRequiredService<EpgIndex>();
var started = DateTime.UtcNow;

hub.StateChanged += s => sync.Broadcast(s);
favorites.Changed += f => sync.Broadcast(SyncService.FavoritesMessage(f));

if (!opts.ProviderConfigured)
    log.LogError("Provider is not configured. Edit appsettings.json (Hub:Provider) and restart the service.");
if (opts.LocalPassword == "changeme")
    log.LogWarning("LocalPassword is still 'changeme' - set your own in appsettings.json.");

bool Authed(string? u, string? p) => u == opts.LocalUsername && p == opts.LocalPassword;
bool AuthedQuery(HttpRequest r) => Authed(r.Query["username"], r.Query["password"]);
string Who(HttpContext ctx) => ctx.Request.Query["device"].FirstOrDefault() is { Length: > 0 } d
    ? $"{d} ({ctx.Connection.RemoteIpAddress})"
    : ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
IResult NotConfigured() => Results.Problem("Hub provider is not configured (appsettings.json)", statusCode: 503);

static IResult JsonFile(string path, string contentType) =>
    Results.Stream(ProviderClient.OpenShared(path), contentType);

static async Task<Dictionary<string, string>> ParamsAsync(HttpRequest r)
{
    var d = r.Query.ToDictionary(k => k.Key, k => k.Value.ToString(), StringComparer.OrdinalIgnoreCase);
    if (r.HasFormContentType)
    {
        var form = await r.ReadFormAsync();
        foreach (var kv in form) d[kv.Key] = kv.Value.ToString();
    }
    return d;
}

// ---- Xtream-compatible API -----------------------------------------------------------------

async Task<IResult> PlayerApi(HttpContext ctx)
{
    var q = await ParamsAsync(ctx.Request);
    q.TryGetValue("username", out var u);
    q.TryGetValue("password", out var p);
    if (!Authed(u, p))
        return Results.Json(new { user_info = new { auth = 0 } }, statusCode: 401);
    if (!opts.ProviderConfigured) return NotConfigured();

    var extra = q.Where(kv => !kv.Key.Equals("username", StringComparison.OrdinalIgnoreCase)
                           && !kv.Key.Equals("password", StringComparison.OrdinalIgnoreCase))
                 .Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value)).ToList();
    q.TryGetValue("action", out var action);

    try
    {
        if (string.IsNullOrEmpty(action))
        {
            // Login / account info: rewrite so the TV builds stream URLs pointing at the hub.
            var login = await provider.GetCachedAsync("player_api.php", extra, TimeSpan.FromMinutes(5), ctx.RequestAborted);
            JsonNode? node;
            await using (var fs = ProviderClient.OpenShared(login.Path)) node = await JsonNode.ParseAsync(fs);
            if (node is JsonObject root)
            {
                if (root["user_info"] is JsonObject ui)
                {
                    ui["username"] = opts.LocalUsername;
                    ui["password"] = opts.LocalPassword;
                }
                if (root["server_info"] is JsonObject si)
                {
                    var port = (ctx.Request.Host.Port ?? 80).ToString();
                    si["url"] = ctx.Request.Host.Host;
                    si["port"] = port;
                    si["https_port"] = port;
                    si["server_protocol"] = "http";
                }
                root["hub"] = new JsonObject
                {
                    ["name"] = "Bert's IPTV Hub",
                    ["version"] = AppInfo.Version,
                    ["build"] = AppInfo.Build,
                    ["sync"] = "/sync",
                    ["favorites"] = "/api/favorites",
                    ["liveFormat"] = "ts",
                    ["epgNow"] = "/api/epg/now",
                };
            }
            return Results.Text(node?.ToJsonString() ?? "{}", "application/json", Encoding.UTF8);
        }

        var ttl = action is "get_short_epg" or "get_simple_data_table" ? provider.ShortEpgTtl : provider.CatalogTtl;
        var entry = await provider.GetCachedAsync("player_api.php", extra, ttl, ctx.RequestAborted);
        return JsonFile(entry.Path, "application/json; charset=utf-8");
    }
    catch (Exception ex) when (!ctx.RequestAborted.IsCancellationRequested)
    {
        log.LogWarning("player_api {Action} failed: {Msg}", action ?? "(login)", ex.Message);
        return Results.Problem("Provider unavailable: " + ex.Message, statusCode: 502);
    }
}

Func<HttpContext, Task<IResult>> playerApi = PlayerApi;
app.MapGet("/player_api.php", playerApi);
app.MapPost("/player_api.php", playerApi);

app.MapGet("/xmltv.php", async (HttpContext ctx) =>
{
    if (!AuthedQuery(ctx.Request)) return Results.Unauthorized();
    if (!opts.ProviderConfigured) return NotConfigured();
    try
    {
        var e = await provider.GetXmltvAsync(ctx.RequestAborted);
        return JsonFile(e.Path, "application/xml; charset=utf-8");
    }
    catch (Exception ex) when (!ctx.RequestAborted.IsCancellationRequested)
    {
        return Results.Problem("EPG unavailable: " + ex.Message, statusCode: 502);
    }
});

// M3U playlist with every URL pointing back at the hub.
app.MapGet("/get.php", async (HttpContext ctx) =>
{
    if (!AuthedQuery(ctx.Request)) return Results.Unauthorized();
    if (!opts.ProviderConfigured) return NotConfigured();
    var extra = ctx.Request.Query
        .Where(k => k.Key is not ("username" or "password"))
        .Select(k => new KeyValuePair<string, string>(k.Key, k.Value.ToString())).ToList();
    try
    {
        var e = await provider.GetCachedAsync("get.php", extra, provider.CatalogTtl, ctx.RequestAborted);
        var text = await File.ReadAllTextAsync(e.Path, ctx.RequestAborted);
        var hubBase = $"http://{ctx.Request.Host}";
        text = text.Replace(provider.BaseUrl, hubBase)
                   .Replace($"/{provider.User}/{provider.Pass}/", $"/{opts.LocalUsername}/{opts.LocalPassword}/")
                   .Replace($"username={provider.User}&password={provider.Pass}",
                            $"username={opts.LocalUsername}&password={opts.LocalPassword}");
        return Results.Text(text, "audio/x-mpegurl", Encoding.UTF8);
    }
    catch (Exception ex) when (!ctx.RequestAborted.IsCancellationRequested)
    {
        return Results.Problem("Playlist unavailable: " + ex.Message, statusCode: 502);
    }
});

// ---- Live relay -------------------------------------------------------------------------------

async Task ServeLive(HttpContext ctx, string user, string pass, string file)
{
    if (!Authed(user, pass)) { ctx.Response.StatusCode = 401; return; }
    if (!opts.ProviderConfigured) { ctx.Response.StatusCode = 503; return; }
    var idPart = file.Contains('.') ? file[..file.IndexOf('.')] : file;
    if (!int.TryParse(idPart, out var id)) { ctx.Response.StatusCode = 404; return; }

    var ct = ctx.RequestAborted;
    Subscriber sub;
    try { sub = await hub.SubscribeLiveAsync(id, Who(ctx), ct); }
    catch (OperationCanceledException) { return; }

    try
    {
        ctx.Response.ContentType = "video/mp2t";
        ctx.Response.Headers.CacheControl = "no-cache, no-store";
        ctx.Response.Headers.Connection = "close"; // when the hub stops/switches, the TV's player sees EOF at once
        ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await ctx.Response.StartAsync(ct);

        var reader = sub.Reader;
        while (await reader.WaitToReadAsync(ct))
        {
            while (reader.TryRead(out var chunk))
                await ctx.Response.Body.WriteAsync(chunk, ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException) { }
    catch (IOException) { }
    catch (Exception ex) { log.LogDebug("Live send ended: {Msg}", ex.Message); }
    finally
    {
        hub.Unsubscribe(sub);
    }
}

app.MapGet("/live/{user}/{pass}/{file}", (HttpContext ctx, string user, string pass, string file) => ServeLive(ctx, user, pass, file));
app.MapGet("/{user}/{pass}/{id:int}", (HttpContext ctx, string user, string pass, int id) => ServeLive(ctx, user, pass, id.ToString()));

// ---- VOD / series / catch-up passthrough (takes the single slot, supports seeking) -------------

async Task ServeVod(HttpContext ctx, string kind, string key, string name, string upstreamUrl)
{
    if (!opts.ProviderConfigured) { ctx.Response.StatusCode = 503; return; }
    var (lease, token) = hub.AcquireVod(kind, key, name);
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, ctx.RequestAborted);
    try
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, upstreamUrl);
        if (ctx.Request.Headers.Range.Count > 0)
            req.Headers.TryAddWithoutValidation("Range", ctx.Request.Headers.Range.ToString());
        using var resp = await clients.Streams.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token);

        ctx.Response.StatusCode = (int)resp.StatusCode;
        foreach (var h in new[] { "Content-Type", "Content-Length", "Content-Range", "Last-Modified" })
            if (resp.Content.Headers.TryGetValues(h, out var v)) ctx.Response.Headers[h] = v.ToArray();
        foreach (var h in new[] { "Accept-Ranges", "ETag" })
            if (resp.Headers.TryGetValues(h, out var v)) ctx.Response.Headers[h] = v.ToArray();

        await using var body = await resp.Content.ReadAsStreamAsync(linked.Token);
        await body.CopyToAsync(ctx.Response.Body, 128 * 1024, linked.Token);
    }
    catch (OperationCanceledException) { }
    catch (IOException) { }
    catch (HttpRequestException ex)
    {
        log.LogWarning("{Kind} {Name} failed: {Msg}", kind, name, ex.Message);
        if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 502;
    }
    finally
    {
        hub.ReleaseVod(lease);
    }
}

string P(string s) => provider.Esc(s);

app.MapGet("/movie/{user}/{pass}/{file}", (HttpContext ctx, string user, string pass, string file) =>
    Authed(user, pass)
        ? ServeVod(ctx, "movie", "movie:" + file, "Movie " + Path.GetFileNameWithoutExtension(file),
            $"{provider.BaseUrl}/movie/{P(provider.User)}/{P(provider.Pass)}/{file}")
        : Task.FromResult(ctx.Response.StatusCode = 401));

app.MapGet("/series/{user}/{pass}/{file}", (HttpContext ctx, string user, string pass, string file) =>
    Authed(user, pass)
        ? ServeVod(ctx, "series", "series:" + file, "Episode " + Path.GetFileNameWithoutExtension(file),
            $"{provider.BaseUrl}/series/{P(provider.User)}/{P(provider.Pass)}/{file}")
        : Task.FromResult(ctx.Response.StatusCode = 401));

app.MapGet("/timeshift/{user}/{pass}/{duration}/{start}/{file}",
    (HttpContext ctx, string user, string pass, string duration, string start, string file) =>
    Authed(user, pass)
        ? ServeVod(ctx, "timeshift", $"timeshift:{file}:{start}", $"Catch-up {Path.GetFileNameWithoutExtension(file)} {start}",
            $"{provider.BaseUrl}/timeshift/{P(provider.User)}/{P(provider.Pass)}/{duration}/{start}/{file}")
        : Task.FromResult(ctx.Response.StatusCode = 401));

app.MapGet("/streaming/timeshift.php", (HttpContext ctx) =>
{
    if (!AuthedQuery(ctx.Request)) { ctx.Response.StatusCode = 401; return Task.CompletedTask; }
    var extra = ctx.Request.Query.Where(k => k.Key is not ("username" or "password"))
        .Select(k => new KeyValuePair<string, string>(k.Key, k.Value.ToString())).ToList();
    var stream = ctx.Request.Query["stream"].ToString();
    var start = ctx.Request.Query["start"].ToString();
    return ServeVod(ctx, "timeshift", $"timeshift:{stream}:{start}", $"Catch-up {stream} {start}",
        provider.BuildUrl("streaming/timeshift.php", extra));
});

// ---- Hub API: state, play/stop, favourites ----------------------------------------------------

app.Map("/sync", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    if (!AuthedQuery(ctx.Request)) { ctx.Response.StatusCode = 401; return; }
    await sync.HandleAsync(ctx);
});

app.MapGet("/api/state", (HttpContext ctx) =>
    AuthedQuery(ctx.Request) ? Results.Json(hub.Snapshot()) : Results.Unauthorized());

app.MapPost("/api/play/{id:int}", async (HttpContext ctx, int id) =>
{
    if (!AuthedQuery(ctx.Request)) return Results.Unauthorized();
    await hub.StartLiveAsync(id, Who(ctx), ctx.RequestAborted);
    return Results.Json(hub.Snapshot());
});

app.MapPost("/api/stop", (HttpContext ctx) =>
{
    if (!AuthedQuery(ctx.Request)) return Results.Unauthorized();
    hub.Stop($"stopped by {Who(ctx)}");
    return Results.Json(hub.Snapshot());
});

app.MapGet("/api/favorites", (HttpContext ctx) =>
    AuthedQuery(ctx.Request) ? Results.Json(SyncService.FavoritesMessage(favorites.Snapshot())) : Results.Unauthorized());

app.MapPut("/api/favorites/{kind}", async (HttpContext ctx, string kind) =>
{
    if (!AuthedQuery(ctx.Request)) return Results.Unauthorized();
    if (!FavoritesStore.IsKind(kind)) return Results.NotFound();
    List<int>? ids;
    try { ids = await JsonSerializer.DeserializeAsync<List<int>>(ctx.Request.Body, cancellationToken: ctx.RequestAborted); }
    catch (JsonException) { return Results.BadRequest("Body must be a JSON array of ids, e.g. [12,34]"); }
    favorites.Replace(kind, ids ?? new());
    return Results.Json(SyncService.FavoritesMessage(favorites.Snapshot()));
});

app.MapPost("/api/favorites/{kind}/{id:int}", (HttpContext ctx, string kind, int id) =>
    !AuthedQuery(ctx.Request) ? Results.Unauthorized()
    : favorites.Add(kind, id) ? Results.Json(SyncService.FavoritesMessage(favorites.Snapshot())) : Results.NotFound());

app.MapDelete("/api/favorites/{kind}/{id:int}", (HttpContext ctx, string kind, int id) =>
    !AuthedQuery(ctx.Request) ? Results.Unauthorized()
    : favorites.Remove(kind, id) ? Results.Json(SyncService.FavoritesMessage(favorites.Snapshot())) : Results.NotFound());

// ---- EPG, worked out on the PC so TV boxes don't have to parse the whole guide ------------------

static object? Prog(Programme? p) => p is null ? null : new { title = p.Title, desc = p.Desc, start = p.Start, end = p.End };

// Now/next for every channel (or ?ids=1,2,3). Keyed by stream id.
app.MapGet("/api/epg/now", async (HttpContext ctx) =>
{
    if (!AuthedQuery(ctx.Request)) return Results.Unauthorized();
    if (!opts.ProviderConfigured) return NotConfigured();
    try
    {
        var guide = await epg.GetAsync(ctx.RequestAborted);
        var live = await provider.GetLiveIndexAsync(ctx.RequestAborted);
        HashSet<int>? only = null;
        var ids = ctx.Request.Query["ids"].ToString();
        if (!string.IsNullOrWhiteSpace(ids))
            only = ids.Split(',').Select(x => int.TryParse(x, out var i) ? i : -1).Where(i => i >= 0).ToHashSet();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var result = new Dictionary<string, object>();
        foreach (var ch in live.Values)
        {
            if (only != null && !only.Contains(ch.Id)) continue;
            if (string.IsNullOrEmpty(ch.EpgChannelId) || !guide.TryGetValue(ch.EpgChannelId, out var list)) continue;
            var (n, nx) = EpgIndex.NowNext(list, now);
            if (n is null && nx is null) continue;
            result[ch.Id.ToString()] = new { now = Prog(n), next = Prog(nx) };
        }
        return Results.Json(new { generated = now, channels = result });
    }
    catch (Exception ex) when (!ctx.RequestAborted.IsCancellationRequested)
    {
        return Results.Problem("EPG unavailable: " + ex.Message, statusCode: 502);
    }
});

// Listings for one channel from (now - 1h) for ?hours= (default 24).
app.MapGet("/api/epg/{id:int}", async (HttpContext ctx, int id) =>
{
    if (!AuthedQuery(ctx.Request)) return Results.Unauthorized();
    if (!opts.ProviderConfigured) return NotConfigured();
    try
    {
        var live = await provider.GetLiveIndexAsync(ctx.RequestAborted);
        if (!live.TryGetValue(id, out var ch) || string.IsNullOrEmpty(ch.EpgChannelId))
            return Results.Json(new { streamId = id, programmes = Array.Empty<object>() });
        var guide = await epg.GetAsync(ctx.RequestAborted);
        var hours = int.TryParse(ctx.Request.Query["hours"], out var h) ? Math.Clamp(h, 1, 36) : 24;
        var from = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();
        var to = DateTimeOffset.UtcNow.AddHours(hours).ToUnixTimeMilliseconds();
        var items = guide.TryGetValue(ch.EpgChannelId, out var list)
            ? list.Where(p => p.End > from && p.Start < to).Select(Prog).ToArray()
            : Array.Empty<object?>();
        return Results.Json(new { streamId = id, programmes = items });
    }
    catch (Exception ex) when (!ctx.RequestAborted.IsCancellationRequested)
    {
        return Results.Problem("EPG unavailable: " + ex.Message, statusCode: 502);
    }
});

// ---- Status (read-only, no credentials shown) -------------------------------------------------

app.MapGet("/api/status", () =>
{
    var live = hub.CurrentLive;
    var proc = Process.GetCurrentProcess();
    return Results.Json(new
    {
        version = AppInfo.Version,
        build = AppInfo.Build,
        versionDisplay = AppInfo.Display,
        state = hub.Snapshot(),
        providerConfigured = opts.ProviderConfigured,
        providerHost = Uri.TryCreate(opts.Provider.Url, UriKind.Absolute, out var pu) ? pu.Host : null,
        uptimeMinutes = Math.Round((DateTime.UtcNow - started).TotalMinutes, 1),
        memoryMB = Math.Round(proc.WorkingSet64 / 1048576.0, 1),
        upstream = live is null ? null : new
        {
            channel = live.Info.Name,
            connectedAt = live.ConnectedUtc,
            megabytesIn = Math.Round(live.BytesIn / 1048576.0, 1),
            reconnects = live.Reconnects,
            viewers = live.SubscribersSnapshot().Select(s => new { who = s.Who, since = s.SinceUtc }),
        },
        syncClients = sync.Clients,
        cache = provider.CacheSummary(),
        updates = new
        {
            channels = provider.LastUpdated("player_api.php", ("action", "get_live_streams")),
            epg = provider.LastUpdated("xmltv.php"),
            channelsEveryHours = opts.CatalogCacheHours,
            epgEveryHours = opts.EpgCacheHours,
            epgChannels = epg.ChannelCount,
        },
    });
});

var logoPng = AppInfo.Resource("logo.png");
var faviconIco = AppInfo.Resource("favicon.ico");
var statusHtml = StatusPage.Html.Replace("{{VERSION}}", AppInfo.Display);
app.MapGet("/", () => Results.Content(statusHtml, "text/html; charset=utf-8"));
app.MapGet("/logo.png", () => logoPng is null ? Results.NotFound() : Results.Bytes(logoPng, "image/png"));
app.MapGet("/favicon.ico", () => faviconIco is null ? Results.NotFound() : Results.Bytes(faviconIco, "image/x-icon"));
app.MapGet("/api/version", () => Results.Json(new { name = "Bert's IPTV Hub", version = AppInfo.Version, build = AppInfo.Build }));

log.LogInformation("Bert's IPTV Hub {Version} listening on {Url}", AppInfo.Display, opts.ListenUrl);
app.Run();
