using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using EveDeck.Services.Intel;
using EveDeck.Services.Intel.Wire;
using Xunit;

namespace EveDeck.Tests;

public class IntelTransportTests
{
    [Theory]
    [InlineData(15)]
    [InlineData(93)]
    public void Tailer_RetainsPartialLinesIncludingOddByteWrites(int split)
    {
        var path = Path.GetTempFileName();
        try
        {
            var bytes = Encoding.Unicode.GetBytes("[ 2026.09.26 12:00:00 ] Example Pilot > Jita hostile\r\n");
            using var tailer = new IntelLogTailer();
            var messages = new List<ChatLogFormat.RawMessage>();
            tailer.MessageRead += (_, _, m) => messages.Add(m);
            File.WriteAllBytes(path, bytes[..split]);
            tailer.ReadNewLines(path, false);
            Assert.Empty(messages);
            using (var file = new FileStream(path, FileMode.Append)) file.Write(bytes[split..]);
            tailer.ReadNewLines(path, false);
            tailer.ReadNewLines(path, false);
            Assert.Equal("Jita hostile", Assert.Single(messages).Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Connection_DeliversUpdatePublishedDuringSnapshot()
    {
        var port = FreePort();
        Task? broadcast = null;
        IntelHttpServer? server = null;
        server = new IntelHttpServer(port, Path.GetTempPath(), () =>
        {
            broadcast = server!.BroadcastAsync(new WireServerMessage.Heartbeat { ServerTimeMillis = 123 });
            return new WireServerMessage.Snapshot { Messages = [], Locations = [], ScopeRegionIds = [], ServerTimeMillis = 0 };
        }, false, () => new HashSet<long>());
        await using var ownedServer = server;
        Assert.True(server.TryStart(out var error), error);
        using var client = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/intel"), timeout.Token);
            using var snapshot = JsonDocument.Parse(await ReadMessage(client, timeout.Token));
            Assert.Equal("snapshot", snapshot.RootElement.GetProperty("type").GetString());
            using var update = JsonDocument.Parse(await ReadMessage(client, timeout.Token));
            Assert.Equal(123, update.RootElement.GetProperty("serverTimeMillis").GetInt64());
            await broadcast!;
        }
        finally { client.Abort(); }
    }

    [Fact]
    public async Task Receive_ReassemblesFragmentedUtf8Command()
    {
        var port = FreePort();
        var received = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new IntelHttpServer(port, Path.GetTempPath(),
            () => new WireServerMessage.Snapshot { Messages = [], Locations = [], ScopeRegionIds = [], ServerTimeMillis = 0 }, false, () => new HashSet<long>(),
            channels => received.TrySetResult(channels));
        Assert.True(server.TryStart(out var error), error);
        using var client = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/intel"), timeout.Token);
            await ReadMessage(client, timeout.Token);
            var bytes = Encoding.UTF8.GetBytes("{\"type\":\"setChannels\",\"channels\":[\"café\"]}");
            var split = Array.IndexOf(bytes, (byte)0xC3) + 1;
            await client.SendAsync(new ArraySegment<byte>(bytes, 0, split), WebSocketMessageType.Text, false, timeout.Token);
            await client.SendAsync(new ArraySegment<byte>(bytes, split, bytes.Length - split), WebSocketMessageType.Text, true, timeout.Token);
            Assert.Equal("café", Assert.Single(await received.Task.WaitAsync(timeout.Token)));
        }
        finally { client.Abort(); }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<string> ReadMessage(ClientWebSocket socket, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            output.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
