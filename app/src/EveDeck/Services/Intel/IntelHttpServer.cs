using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using EveDeck.Services.Intel.Wire;

namespace EveDeck.Services.Intel;

/// <summary>
/// Serves the same endpoints as the standalone EveDeck Intel daemon, so the existing Android app and
/// browser UI can talk to EveDeck without knowing which one is running:
/// <c>/</c>, <c>/manifest.webmanifest</c>, <c>/icon-256.png</c>, <c>/universe.json</c>,
/// <c>/img/{category}/{id}/{variant}</c> and the <c>/intel</c> WebSocket.
///
/// Built on <see cref="TcpListener"/> rather than <see cref="HttpListener"/> deliberately.
/// HttpListener needs an admin-only URL ACL to bind anything but loopback on Windows, which would
/// leave the tablet unable to connect for any user not running EveDeck elevated. A raw socket binds
/// unprivileged, and the WebSocket upgrade is still handed to the framework's own framing via
/// <see cref="WebSocket.CreateFromStream"/> rather than being implemented by hand.
///
/// **No auth, no TLS** — deliberately identical to the daemon, for a LAN service carrying data
/// already public to everyone in the channel. Do not port-forward it. If this ever needs to leave the
/// LAN, auth has to be designed in rather than bolted on.
/// </summary>
public sealed class IntelHttpServer : IAsyncDisposable
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    // Web assets copied from EveDeck-Intel daemon/src/main/resources at commit
    // e80f593a95a4ff4519c7e945479452536a6d448e.
    private const string WebResourcePrefix = "EveDeck.Resources.IntelWeb.";
    private const string UniverseResource = "EveDeck.Resources.universe.json";

    private static readonly HashSet<int> AllowedImageSizes = [32, 64, 128, 256, 512];

    private static readonly Dictionary<string, string[]> AllowedImageCategories = new(StringComparer.Ordinal)
    {
        ["characters"] = ["portrait"],
        ["corporations"] = ["logo"],
        ["alliances"] = ["logo"],
        ["types"] = ["icon", "render", "bp", "bpc"],
        ["factions"] = ["logo"],
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly int _port;
    private readonly string _imageCacheFolder;
    private readonly Func<WireServerMessage.Snapshot> _snapshotFactory;
    private readonly Action<IReadOnlyList<string>>? _onSetChannels;
    private readonly Action<WireDisplaySettings>? _onSetDisplay;
    private readonly Action<string> _log;

    private readonly List<Session> _sessions = [];
    private readonly Lock _gate = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cancellation;

    public IntelHttpServer(
        int port,
        string imageCacheFolder,
        Func<WireServerMessage.Snapshot> snapshotFactory,
        Action<IReadOnlyList<string>>? onSetChannels = null,
        Action<WireDisplaySettings>? onSetDisplay = null,
        Action<string>? log = null)
    {
        _port = port;
        _imageCacheFolder = imageCacheFolder;
        _snapshotFactory = snapshotFactory;
        _onSetChannels = onSetChannels;
        _onSetDisplay = onSetDisplay;
        _log = log ?? (_ => { });
    }

    public bool IsRunning { get; private set; }

    public int ClientCount
    {
        get { lock (_gate) return _sessions.Count; }
    }

    /// <summary>
    /// Binds the port. Returns false with a reason rather than throwing when the port is taken, which
    /// is the normal case when the standalone daemon is already running — that is a condition to
    /// report, not a crash.
    /// </summary>
    public bool TryStart(out string? error)
    {
        if (IsRunning)
        {
            error = null;
            return true;
        }

        try
        {
            var listener = new TcpListener(IPAddress.Any, _port);
            listener.Start();
            _listener = listener;
        }
        catch (SocketException ex)
        {
            error = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? $"Port {_port} is already in use — EveDeck Intel (the standalone daemon) is probably already running. Stop it, or give EveDeck a different port."
                : $"Could not open port {_port}: {ex.Message}";
            _listener = null;
            return false;
        }

        _cancellation = new CancellationTokenSource();
        IsRunning = true;
        _ = Task.Run(() => AcceptLoopAsync(_cancellation.Token));
        _ = Task.Run(() => HeartbeatLoopAsync(_cancellation.Token));

        error = null;
        _log($"Intel server listening on port {_port}.");
        return true;
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;
        IsRunning = false;

        _cancellation?.Cancel();

        try { _listener?.Stop(); } catch { /* already closed */ }
        _listener = null;

        List<Session> sessions;
        lock (_gate)
        {
            sessions = [.._sessions];
            _sessions.Clear();
        }

        foreach (var session in sessions) await session.CloseAsync().ConfigureAwait(false);

        _cancellation?.Dispose();
        _cancellation = null;
        _log("Intel server stopped.");
    }

    /// <summary>Fans a message out to every connected client. Dead sessions are dropped.</summary>
    public async Task BroadcastAsync(WireServerMessage message)
    {
        List<Session> sessions;
        lock (_gate)
        {
            if (_sessions.Count == 0) return;
            sessions = [.._sessions];
        }

        var payload = Encoding.UTF8.GetBytes(WireProtocol.Serialize(message));
        var dead = new List<Session>();

        foreach (var session in sessions)
        {
            if (!await session.TrySendAsync(payload).ConfigureAwait(false)) dead.Add(session);
        }

        if (dead.Count == 0) return;
        lock (_gate)
        {
            foreach (var session in dead) _sessions.Remove(session);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await BroadcastAsync(new WireServerMessage.Heartbeat
            {
                ServerTimeMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }).ConfigureAwait(false);
        }
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        var listener = _listener;
        if (listener is null) return;

        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log($"Intel server accept failed: {ex.Message}");
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, token), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, token).ConfigureAwait(false);
            if (request is null)
            {
                client.Dispose();
                return;
            }

            if (request.IsWebSocketUpgrade && request.Path.Equals("/intel", StringComparison.Ordinal))
            {
                await UpgradeAndServeAsync(client, stream, request, token).ConfigureAwait(false);
                return;
            }

            await ServeHttpAsync(stream, request, token).ConfigureAwait(false);
            client.Dispose();
        }
        catch (Exception ex)
        {
            _log($"Intel server client error: {ex.Message}");
            try { client.Dispose(); } catch { /* already gone */ }
        }
    }

    private async Task UpgradeAndServeAsync(
        TcpClient client,
        NetworkStream stream,
        HttpRequest request,
        CancellationToken token)
    {
        if (request.WebSocketKey is null)
        {
            await WriteStatusAsync(stream, 400, "Bad Request", token).ConfigureAwait(false);
            client.Dispose();
            return;
        }

        var accept = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(request.WebSocketKey + WebSocketGuid)));

        var handshake =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(handshake), token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);

        var webSocket = WebSocket.CreateFromStream(
            stream,
            isServer: true,
            subProtocol: null,
            keepAliveInterval: HeartbeatInterval);

        var session = new Session(client, webSocket);
        lock (_gate) _sessions.Add(session);
        _log($"Intel client connected ({ClientCount} connected).");

        try
        {
            // Snapshot first and unconditionally: a client joining mid-fight must see recent history
            // before any incremental message reaches it.
            var snapshot = Encoding.UTF8.GetBytes(WireProtocol.Serialize(_snapshotFactory()));
            await session.TrySendAsync(snapshot).ConfigureAwait(false);

            await ReceiveLoopAsync(session, token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _sessions.Remove(session);
            await session.CloseAsync().ConfigureAwait(false);
            _log($"Intel client disconnected ({ClientCount} connected).");
        }
    }

    private async Task ReceiveLoopAsync(Session session, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];

        while (!token.IsCancellationRequested && session.Socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await session.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), token)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return; // client vanished, wifi asleep, PC sleeping -- all the same response
            }

            if (result.MessageType == WebSocketMessageType.Close) return;
            if (result.MessageType != WebSocketMessageType.Text) continue;

            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            HandleClientMessage(text);
        }
    }

    private void HandleClientMessage(string text)
    {
        var message = WireProtocol.Deserialize(text);
        switch (message)
        {
            case WireClientMessage.Hello hello:
                _log($"Intel client says hello: {hello.DeviceName} (protocol {hello.ProtocolVersion}).");
                break;

            case WireClientMessage.SetChannels setChannels:
                _onSetChannels?.Invoke(setChannels.Channels);
                break;

            case WireClientMessage.SetDisplay setDisplay:
                _onSetDisplay?.Invoke(setDisplay.Settings.Coerced());
                break;

            case WireClientMessage.Follow:
                // Accepted and ignored, exactly as the daemon does. EveDeck follows a set of
                // characters chosen on the PC and reports the nearest, so one client nominating a
                // single character must not narrow what everyone else sees.
                break;
        }
    }

    private async Task ServeHttpAsync(NetworkStream stream, HttpRequest request, CancellationToken token)
    {
        if (!request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            await WriteStatusAsync(stream, 405, "Method Not Allowed", token).ConfigureAwait(false);
            return;
        }

        switch (request.Path)
        {
            case "/":
            case "/index.html":
                await WriteResourceAsync(stream, "index.html", "text/html; charset=utf-8", token).ConfigureAwait(false);
                return;

            case "/manifest.webmanifest":
                await WriteResourceAsync(stream, "manifest.webmanifest", "application/json", token).ConfigureAwait(false);
                return;

            case "/icon-256.png":
                await WriteResourceAsync(stream, "icon-256.png", "image/png", token, cacheSeconds: 86400)
                    .ConfigureAwait(false);
                return;

            case "/universe.json":
                await WriteUniverseAsync(stream, token).ConfigureAwait(false);
                return;
        }

        if (request.Path.StartsWith("/img/", StringComparison.Ordinal))
        {
            await ServeImageAsync(stream, request, token).ConfigureAwait(false);
            return;
        }

        await WriteStatusAsync(stream, 404, "Not Found", token).ConfigureAwait(false);
    }

    private async Task ServeImageAsync(NetworkStream stream, HttpRequest request, CancellationToken token)
    {
        // /img/{category}/{id}/{variant}?size=n -- an allow-list, not an open proxy.
        var parts = request.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
        {
            await WriteStatusAsync(stream, 404, "Not Found", token).ConfigureAwait(false);
            return;
        }

        var category = parts[1];
        var variant = parts[3];
        var size = request.QueryValue("size") is { } raw && int.TryParse(raw, out var parsed) ? parsed : 64;

        if (!long.TryParse(parts[2], out var id) || id <= 0
            || !AllowedImageCategories.TryGetValue(category, out var variants)
            || !variants.Contains(variant, StringComparer.Ordinal)
            || !AllowedImageSizes.Contains(size))
        {
            await WriteStatusAsync(stream, 404, "Not Found", token).ConfigureAwait(false);
            return;
        }

        try
        {
            var bytes = await FetchImageAsync(category, id, variant, size, token).ConfigureAwait(false);
            if (bytes is null)
            {
                await WriteStatusAsync(stream, 404, "Not Found", token).ConfigureAwait(false);
                return;
            }

            await WriteBytesAsync(stream, bytes, "image/png", token, cacheSeconds: 86400).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log($"Intel image proxy failed: {ex.Message}");
            await WriteStatusAsync(stream, 502, "Bad Gateway", token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Fetched once and cached to disk forever — an entity's portrait or a hull's icon does not
    /// change, and this keeps the tablet from ever contacting an external host itself.
    /// </summary>
    private async Task<byte[]?> FetchImageAsync(
        string category,
        long id,
        string variant,
        int size,
        CancellationToken token)
    {
        var folder = Path.Combine(_imageCacheFolder, category);
        var file = Path.Combine(folder, $"{id}-{variant}-{size}.png");

        if (File.Exists(file)) return await File.ReadAllBytesAsync(file, token).ConfigureAwait(false);

        var url = $"https://images.evetech.net/{category}/{id}/{variant}?size={size}";
        using var response = await Http.GetAsync(url, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);

        try
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(file, bytes, token).ConfigureAwait(false);
        }
        catch
        {
            // A cache write failure must not fail the response.
        }

        return bytes;
    }

    private async Task WriteUniverseAsync(NetworkStream stream, CancellationToken token)
    {
        await using var resource = typeof(IntelHttpServer).Assembly.GetManifestResourceStream(UniverseResource);
        if (resource is null)
        {
            await WriteStatusAsync(stream, 404, "Not Found", token).ConfigureAwait(false);
            return;
        }

        using var memory = new MemoryStream();
        await resource.CopyToAsync(memory, token).ConfigureAwait(false);
        await WriteBytesAsync(stream, memory.ToArray(), "application/json", token, cacheSeconds: 86400)
            .ConfigureAwait(false);
    }

    private async Task WriteResourceAsync(
        NetworkStream stream,
        string name,
        string contentType,
        CancellationToken token,
        int cacheSeconds = 0)
    {
        await using var resource = typeof(IntelHttpServer).Assembly
            .GetManifestResourceStream(WebResourcePrefix + name);

        if (resource is null)
        {
            await WriteStatusAsync(stream, 404, "Not Found", token).ConfigureAwait(false);
            return;
        }

        using var memory = new MemoryStream();
        await resource.CopyToAsync(memory, token).ConfigureAwait(false);
        await WriteBytesAsync(stream, memory.ToArray(), contentType, token, cacheSeconds).ConfigureAwait(false);
    }

    private static async Task WriteBytesAsync(
        NetworkStream stream,
        byte[] body,
        string contentType,
        CancellationToken token,
        int cacheSeconds = 0)
    {
        var headers = new StringBuilder();
        headers.Append("HTTP/1.1 200 OK\r\n");
        headers.Append($"Content-Type: {contentType}\r\n");
        headers.Append($"Content-Length: {body.Length}\r\n");
        if (cacheSeconds > 0) headers.Append($"Cache-Control: public, max-age={cacheSeconds}\r\n");
        headers.Append("Connection: close\r\n\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), token).ConfigureAwait(false);
        await stream.WriteAsync(body, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task WriteStatusAsync(
        NetworkStream stream,
        int code,
        string reason,
        CancellationToken token)
    {
        var response =
            $"HTTP/1.1 {code} {reason}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    /// <summary>Reads just the request line and headers; this server has no request bodies.</summary>
    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[8 * 1024];
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), token)
                .ConfigureAwait(false);
            if (read == 0) break;
            total += read;

            var text = Encoding.ASCII.GetString(buffer, 0, total);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0) return HttpRequest.Parse(text[..end]);
        }

        return null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private sealed record HttpRequest(
        string Method,
        string Path,
        string? Query,
        bool IsWebSocketUpgrade,
        string? WebSocketKey)
    {
        public string? QueryValue(string key)
        {
            if (string.IsNullOrEmpty(Query)) return null;

            foreach (var pair in Query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var split = pair.Split('=', 2);
                if (split.Length == 2 && split[0].Equals(key, StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(split[1]);
            }

            return null;
        }

        public static HttpRequest? Parse(string head)
        {
            var lines = head.Split("\r\n");
            if (lines.Length == 0) return null;

            var requestLine = lines[0].Split(' ');
            if (requestLine.Length < 2) return null;

            var target = requestLine[1];
            var queryStart = target.IndexOf('?');
            var path = queryStart >= 0 ? target[..queryStart] : target;
            var query = queryStart >= 0 ? target[(queryStart + 1)..] : null;

            var upgrade = false;
            string? key = null;

            foreach (var line in lines.Skip(1))
            {
                var split = line.Split(':', 2);
                if (split.Length != 2) continue;
                var name = split[0].Trim();
                var value = split[1].Trim();

                if (name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)
                    && value.Contains("websocket", StringComparison.OrdinalIgnoreCase))
                {
                    upgrade = true;
                }
                else if (name.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
                {
                    key = value;
                }
            }

            return new HttpRequest(requestLine[0], Uri.UnescapeDataString(path), query, upgrade, key);
        }
    }

    /// <summary>
    /// One connected client. Sends are serialised: concurrent SendAsync calls on a single WebSocket
    /// are not allowed, and the broadcast path can race the snapshot on a freshly joined client.
    /// </summary>
    private sealed class Session(TcpClient client, WebSocket socket)
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public WebSocket Socket { get; } = socket;

        public async Task<bool> TrySendAsync(byte[] payload)
        {
            if (Socket.State != WebSocketState.Open) return false;

            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await Socket.SendAsync(
                        new ArraySegment<byte>(payload),
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async Task CloseAsync()
        {
            try
            {
                if (Socket.State == WebSocketState.Open)
                {
                    await Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                // Best effort; the socket may already be gone.
            }

            try { Socket.Dispose(); } catch { /* already disposed */ }
            try { client.Dispose(); } catch { /* already disposed */ }
        }
    }
}
