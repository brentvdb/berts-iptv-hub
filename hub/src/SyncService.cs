using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace BertsIptvHub;

/// <summary>
/// WebSocket channel at /sync. The hub pushes {"type":"state"} and {"type":"favorites"} to every TV;
/// TVs send {"cmd":"hello"|"play"|"stop"|"getState"|"ping"}.
/// </summary>
public sealed class SyncService
{
    private sealed class Conn
    {
        public required WebSocket Ws { get; init; }
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public string Device { get; set; } = "unknown";
        public string Ip { get; init; } = "";
        public DateTime SinceUtc { get; } = DateTime.UtcNow;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, Conn> _conns = new();
    private readonly StreamHub _hub;
    private readonly FavoritesStore _favorites;
    private readonly ILogger<SyncService> _log;

    public SyncService(StreamHub hub, FavoritesStore favorites, ILogger<SyncService> log)
    {
        _hub = hub;
        _favorites = favorites;
        _log = log;
    }

    public IEnumerable<object> Clients => _conns.Values.Select(c => new { device = c.Device, ip = c.Ip, since = c.SinceUtc });

    public static object FavoritesMessage(FavoritesStore.Data d) =>
        new { type = "favorites", live = d.Live, vod = d.Vod, series = d.Series };

    public async Task HandleAsync(HttpContext ctx)
    {
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var id = Guid.NewGuid();
        var conn = new Conn { Ws = ws, Ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?" };
        var device = ctx.Request.Query["device"].ToString();
        if (!string.IsNullOrWhiteSpace(device)) conn.Device = device;
        _conns[id] = conn;
        _log.LogInformation("TV connected to sync: {Device} {Ip} ({N} connected)", conn.Device, conn.Ip, _conns.Count);

        try
        {
            await SendAsync(conn, JsonSerializer.SerializeToUtf8Bytes(_hub.Snapshot(), Json));
            await SendAsync(conn, JsonSerializer.SerializeToUtf8Bytes(FavoritesMessage(_favorites.Snapshot()), Json));

            var buf = new byte[8192];
            using var msg = new MemoryStream();
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, ctx.RequestAborted);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    break;
                }
                msg.Write(buf, 0, r.Count);
                if (!r.EndOfMessage)
                {
                    if (msg.Length > 64 * 1024) break;
                    continue;
                }
                var text = Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length);
                msg.SetLength(0);
                await HandleCommandAsync(conn, text, ctx.RequestAborted);
            }
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
        finally
        {
            _conns.TryRemove(id, out _);
            _log.LogInformation("TV left sync: {Device} {Ip} ({N} connected)", conn.Device, conn.Ip, _conns.Count);
        }
    }

    private async Task HandleCommandAsync(Conn c, string text, CancellationToken ct)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(text);
            root = doc.RootElement.Clone();
        }
        catch
        {
            return;
        }
        if (root.ValueKind != JsonValueKind.Object) return;
        var cmd = root.TryGetProperty("cmd", out var cv) ? cv.GetString() : null;

        switch (cmd)
        {
            case "hello":
                if (root.TryGetProperty("device", out var d) && d.GetString() is { Length: > 0 } name) c.Device = name;
                break;
            case "play":
                var id = ProviderClient.ReadInt(root, "streamId");
                if (id is int sid) await _hub.StartLiveAsync(sid, c.Device, ct);
                break;
            case "stop":
                _hub.Stop($"stopped by {c.Device}");
                break;
            case "getState":
                await SendAsync(c, JsonSerializer.SerializeToUtf8Bytes(_hub.Snapshot(), Json));
                break;
            case "ping":
                await SendAsync(c, JsonSerializer.SerializeToUtf8Bytes(new { type = "pong" }, Json));
                break;
        }
    }

    public void Broadcast(object message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), Json);
        foreach (var c in _conns.Values) _ = SendAsync(c, bytes);
    }

    private static async Task SendAsync(Conn c, byte[] bytes)
    {
        await c.SendLock.WaitAsync();
        try
        {
            if (c.Ws.State != WebSocketState.Open) return;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await c.Ws.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token);
        }
        catch
        {
            // TV went away; its receive loop will clean up.
        }
        finally
        {
            c.SendLock.Release();
        }
    }
}
