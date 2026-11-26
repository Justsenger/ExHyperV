using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using ExHyperV.Server.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace ExHyperV.Server.Tests;

internal sealed class SocketHost(Uri url, WebApplication app, string directory) : IAsyncDisposable
{
    internal WebApplication App => app;
    internal HttpClient Http { get; } = TransportTests.Client(url);
    internal static async Task<SocketHost> Start(string[]? args = null, Action<IServiceCollection>? configure = null,
        string? root = null, TimeProvider? clock = null, Action<WebApplication>? map = null)
    {
        string directory = TestStorage.CreatePath();
        var url = TransportTests.Loopback();
        var options = LaunchOptions.Parse(["--state-dir", directory, "--url", url.ToString(), ..args ?? []]);
        var app = Program.Build(options, root, configure, clock);
        map?.Invoke(app);
        await app.StartAsync();
        return new(url, app, directory);
    }

    internal async Task<WireClient> Connect(string path, string? bearer = null, string? origin = null)
    {
        var client = new WireClient();
        client.Socket.Options.CollectHttpResponseDetails = true;
        client.Socket.Options.Proxy = null;
        if (bearer != null) client.Socket.Options.SetRequestHeader("Authorization", "Bearer " + bearer);
        if (origin != null) client.Socket.Options.SetRequestHeader("Origin", origin);
        try
        {
            await client.Socket.ConnectAsync(new UriBuilder(new Uri(url, path.StartsWith("v2/") ? "ws/" + path : "ws/v1/" + path)) { Scheme = "ws" }.Uri, client.Timeout);
            return client;
        }
        catch (WebSocketException error)
        {
            var status = client.Socket.HttpStatusCode;
            client.Dispose();
            throw new HttpRequestException(error.Message, error, status);
        }
    }

    internal async Task Reject(string path, HttpStatusCode expected, string? bearer = null, string? origin = null)
    {
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Connect(path, bearer, origin));
        Assert.AreEqual(expected, error.StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        try { await app.StopAsync(); }
        finally { await app.DisposeAsync(); TestStorage.Delete(directory); }
    }
}

internal sealed class WireClient : IDisposable
{
    internal ClientWebSocket Socket { get; } = new();
    private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
    internal CancellationToken Timeout => deadline.Token;
    internal Task Send(object command) => Socket.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(command)),
        WebSocketMessageType.Text, true, Timeout);
    internal Task Audio(byte[] data) => Socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, Timeout);
    internal async Task<(WebSocketMessageType Type, byte[] Bytes)> Read()
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();
        ValueWebSocketReceiveResult part;
        do
        {
            part = await Socket.ReceiveAsync(buffer.AsMemory(), Timeout);
            if (part.MessageType == WebSocketMessageType.Close) throw new EndOfStreamException();
            stream.Write(buffer, 0, part.Count);
        } while (!part.EndOfMessage);
        return (part.MessageType, stream.ToArray());
    }

    internal async Task<JsonElement> Json(string type)
    {
        while (true)
        {
            var message = await Read();
            Assert.AreEqual(WebSocketMessageType.Text, message.Type);
            var json = JsonSerializer.Deserialize<JsonElement>(message.Bytes);
            string? actual = json.GetProperty("type").GetString();
            if (actual == type) return json;
            Assert.AreNotEqual("error", actual, json.ToString());
        }
    }

    internal async Task<byte[]> Binary()
    {
        while (true)
        {
            var message = await Read();
            if (message.Type == WebSocketMessageType.Binary) return message.Bytes;
            var json = JsonSerializer.Deserialize<JsonElement>(message.Bytes);
            Assert.AreNotEqual("error", json.GetProperty("type").GetString(), json.ToString());
        }
    }

    internal Task Close() => Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", Timeout);
    public void Dispose() { Socket.Dispose(); deadline.Dispose(); }
}
