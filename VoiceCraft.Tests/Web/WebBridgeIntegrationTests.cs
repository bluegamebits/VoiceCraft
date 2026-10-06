using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VoiceCraft.Core.World;
using VoiceCraft.Network;
using VoiceCraft.Network.Servers;
using VoiceCraft.Network.Systems;
using VoiceCraft.Server.Runtime.Systems;
using VoiceCraft.Web;
using VoiceCraft.Web.Audio;

namespace VoiceCraft.Tests.Web;

/// <summary>
/// End to end: a real VoiceCraft server, the web bridge, and two WebSocket "browsers" using the
/// PCM fallback codec. Browser A sends a tone; browser B must hear it.
/// </summary>
public class WebBridgeIntegrationTests
{
    [Fact]
    public async Task TwoBrowsers_HearEachOther_ThroughTheBridge()
    {
        using var world = new VoiceCraftWorld();
        var udpPort = GetFreeUdpPort();
        using var server = new LiteNetVoiceCraftServer(world)
        {
            Config = new LiteNetVoiceCraftServer.LiteNetVoiceCraftConfig
            {
                Port = (uint)udpPort, MaxClients = 8, Motd = "Web test", PositioningType = PositioningType.Server
            }
        };
        using var audioEffectSystem = new AudioEffectSystem(); // no effects: everyone hears everyone
        using var eventHandlerSystem = new EventHandlerSystem([server], [], audioEffectSystem, world);
        var visibilitySystem = new VisibilitySystem(world, audioEffectSystem);
        server.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverLoop = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                server.Update();
                visibilitySystem.Update();
                eventHandlerSystem.Update();
                await Task.Delay(5);
            }
        });

        var httpPort = GetFreeTcpPort();
        var options = new WebBridgeOptions
        {
            Listen = $"http://localhost:{httpPort}/", ServerHost = "127.0.0.1", ServerPort = udpPort, MaxSessions = 4
        };
        using var bridge = new WebBridgeServer(options);
        var bridgeLoop = Task.Run(() => bridge.RunAsync(cts.Token));
        await Task.Delay(200);

        try
        {
            // Static client and health endpoint.
            using var http = new HttpClient();
            var index = await http.GetStringAsync($"http://localhost:{httpPort}/");
            Assert.Contains("voicecraft-web.js", index);
            var health = await http.GetStringAsync($"http://localhost:{httpPort}/health");
            Assert.Contains("\"sessions\":0", health);

            using var a = await ConnectBrowserAsync(httpPort, cts.Token);
            using var b = await ConnectBrowserAsync(httpPort, cts.Token);
            await WaitForStateAsync(a, "connected", cts.Token);
            await WaitForStateAsync(b, "connected", cts.Token);

            // A talks: a 440 Hz tone for up to 4 s, while B listens for a loud frame.
            var talking = Task.Run(async () =>
            {
                var frame = new byte[Pcm16WebAudioCodec.FrameBytes];
                var phase = 0.0;
                for (var n = 0; n < 200 && !cts.IsCancellationRequested; n++)
                {
                    for (var i = 0; i < Pcm16WebAudioCodec.FrameSamples; i++)
                    {
                        var s = (short)(Math.Sin(phase) * 12000);
                        phase += 2 * Math.PI * 440 / Pcm16WebAudioCodec.SampleRate;
                        BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(i * 2), s);
                    }

                    await a.SendAsync(frame, WebSocketMessageType.Binary, true, cts.Token);
                    await Task.Delay(20, cts.Token);
                }
            });

            var heard = await ListenForAudioAsync(b, minRms: 0.05, cts.Token);
            Assert.True(heard, "Browser B never received A's audio.");

            await a.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            await b.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            await talking.ContinueWith(_ => { });
        }
        finally
        {
            await cts.CancelAsync();
            await Task.WhenAll(serverLoop.ContinueWith(_ => { }), bridgeLoop.ContinueWith(_ => { }));
        }
    }

    private static async Task<ClientWebSocket> ConnectBrowserAsync(int port, CancellationToken token)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://localhost:{port}/ws"), token);
        var hello = JsonSerializer.SerializeToUtf8Bytes(new HelloMessage("hello", "pcm16", Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "en-US"));
        await socket.SendAsync(hello, WebSocketMessageType.Text, true, token);
        return socket;
    }

    private static async Task WaitForStateAsync(ClientWebSocket socket, string state, CancellationToken token)
    {
        var buffer = new byte[8192];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close) throw new Exception("Closed before " + state);
            if (result.MessageType != WebSocketMessageType.Text) continue;
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (doc.RootElement.GetProperty("t").GetString() != "state") continue;
            var current = doc.RootElement.GetProperty("state").GetString();
            if (current == state) return;
            if (current == "disconnected")
                throw new Exception("Disconnected: " + doc.RootElement.GetProperty("reason").GetString());
        }
    }

    private static async Task<bool> ListenForAudioAsync(ClientWebSocket socket, double minRms, CancellationToken token)
    {
        var buffer = new byte[8192];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close) return false;
                if (result.MessageType != WebSocketMessageType.Binary || result.Count != Pcm16WebAudioCodec.FrameBytes) continue;
                double sum = 0;
                for (var i = 0; i < Pcm16WebAudioCodec.FrameSamples; i++)
                {
                    var s = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(i * 2)) / 32768.0;
                    sum += s * s;
                }

                if (Math.Sqrt(sum / Pcm16WebAudioCodec.FrameSamples) >= minRms) return true;
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static int GetFreeUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed record HelloMessage(
        [property: System.Text.Json.Serialization.JsonPropertyName("t")] string T,
        [property: System.Text.Json.Serialization.JsonPropertyName("codec")] string Codec,
        [property: System.Text.Json.Serialization.JsonPropertyName("user")] string User,
        [property: System.Text.Json.Serialization.JsonPropertyName("server")] string Server,
        [property: System.Text.Json.Serialization.JsonPropertyName("locale")] string Locale);
}
