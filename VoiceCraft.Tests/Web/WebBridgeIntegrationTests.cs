using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using VoiceCraft.Core.World;
using VoiceCraft.Network;
using VoiceCraft.Network.Audio.Effects;
using VoiceCraft.Network.Servers;
using VoiceCraft.Network.Systems;
using VoiceCraft.Network.World;
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

    /// <summary>
    /// The global channel through the bridge: proximity and visibility effects on, players linked the way the
    /// Minecraft add-on links them (world id + position). Browser A, 1000 blocks from B, switches to the global
    /// channel: B hears A, the unlinked browser C doesn't, and B stops hearing A after turning the channel off.
    /// </summary>
    [Fact]
    public async Task GlobalChannel_ReachesFarPlayers_ThatAreLinkedAndListening()
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
        using var audioEffectSystem = new AudioEffectSystem();
        audioEffectSystem.SetEffect(1, new VisibilityEffect());
        audioEffectSystem.SetEffect(2, new ProximityEffect { MaxRange = 30 });
        using var eventHandlerSystem = new EventHandlerSystem([server], [], audioEffectSystem, world);
        using var globalChannel = new GlobalChannelSystem(world, [server]) { Bitmask = 16 };
        var visibilitySystem = new VisibilitySystem(world, audioEffectSystem);
        var serverActions = new ConcurrentQueue<Action>();
        server.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var serverLoop = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                while (serverActions.TryDequeue(out var action)) action();
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

        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        using var a = await ConnectBrowserAsync(httpPort, cts.Token, userA);
        using var b = await ConnectBrowserAsync(httpPort, cts.Token, userB);
        using var c = await ConnectBrowserAsync(httpPort, cts.Token);
        using var sendA = new SemaphoreSlim(1, 1); // one send at a time on A: the tone and control messages
        using var stopTalking = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        Task? talking = null;
        try
        {
            await WaitForStateAsync(a, "connected", cts.Token);
            await WaitForStateAsync(b, "connected", cts.Token);
            await WaitForStateAsync(c, "connected", cts.Token);
            var readA = new BrowserReader(a);
            var readB = new BrowserReader(b);
            var readC = new BrowserReader(c);
            serverActions.Enqueue(() =>
            {
                Link(world, userA, Vector3.Zero);
                Link(world, userB, new Vector3(1000, 0, 0));
            });
            talking = Task.Run(() => SendToneAsync(a, sendA, stopTalking.Token));

            // Proximity: 1000 blocks is too far.
            await Task.Delay(300, cts.Token);
            Assert.False(await readB.HearsAsync(0.05, TimeSpan.FromSeconds(1.5)), "B heard A from 1000 blocks away.");

            // A talks on the global channel; the server confirms it to A's page.
            await SendJsonAsync(a, sendA, new { t = "global", talk = true }, cts.Token);
            await readA.WaitForAsync(m => m.GetProperty("t").GetString() == "global" && m.GetProperty("talk").GetBoolean());
            await readB.WaitForAsync(m => m.GetProperty("t").GetString() == "peers" &&
                m.GetProperty("list").EnumerateArray().Any(p => p.GetProperty("global").GetBoolean()));
            Assert.True(await readB.HearsAsync(0.05, TimeSpan.FromSeconds(10)), "B never heard A on the global channel.");
            Assert.False(await readC.HearsAsync(0.05, TimeSpan.FromSeconds(1.5)), "Unlinked C heard the global channel.");

            // B stops listening to the channel.
            await SendJsonAsync(b, null, new { t = "global", listen = false }, cts.Token);
            await readB.WaitForAsync(m => m.GetProperty("t").GetString() == "global" && !m.GetProperty("listen").GetBoolean());
            await readB.HearsAsync(2, TimeSpan.FromMilliseconds(500)); // drain audio already on its way
            Assert.False(await readB.HearsAsync(0.05, TimeSpan.FromSeconds(1.5)), "B still heard the global channel after leaving it.");
        }
        finally
        {
            await stopTalking.CancelAsync();
            if (talking != null) await talking.ContinueWith(_ => { });
            await cts.CancelAsync();
            await Task.WhenAll(serverLoop.ContinueWith(_ => { }), bridgeLoop.ContinueWith(_ => { }));
        }
    }

    private static void Link(VoiceCraftWorld world, Guid user, Vector3 position)
    {
        var entity = world.Entities.OfType<VoiceCraftNetworkEntity>().First(x => x.UserGuid == user);
        entity.Position = position;
        entity.WorldId = "minecraft:overworld";
    }

    private static async Task SendToneAsync(ClientWebSocket socket, SemaphoreSlim sendLock, CancellationToken token)
    {
        var frame = new byte[Pcm16WebAudioCodec.FrameBytes];
        var phase = 0.0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                for (var i = 0; i < Pcm16WebAudioCodec.FrameSamples; i++)
                {
                    var s = (short)(Math.Sin(phase) * 12000);
                    phase += 2 * Math.PI * 440 / Pcm16WebAudioCodec.SampleRate;
                    BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(i * 2), s);
                }

                await sendLock.WaitAsync(token);
                try
                {
                    await socket.SendAsync(frame, WebSocketMessageType.Binary, true, token);
                }
                finally
                {
                    sendLock.Release();
                }

                await Task.Delay(20, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Done talking.
        }
    }

    private static async Task SendJsonAsync(ClientWebSocket socket, SemaphoreSlim? sendLock, object message, CancellationToken token)
    {
        if (sendLock != null) await sendLock.WaitAsync(token);
        try
        {
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, token);
        }
        finally
        {
            sendLock?.Release();
        }
    }

    /// <summary>
    /// Reads a browser socket in the background, so a test can wait with a timeout: cancelling a pending
    /// WebSocket receive would abort the connection.
    /// </summary>
    private sealed class BrowserReader
    {
        private readonly Channel<(string? Text, double Rms)> _messages = Channel.CreateUnbounded<(string?, double)>();

        public BrowserReader(ClientWebSocket socket)
        {
            _ = Task.Run(() => ReadAsync(socket));
        }

        /// <summary>True if an audio frame at least this loud arrives in time.</summary>
        public async Task<bool> HearsAsync(double minRms, TimeSpan within)
        {
            using var timeout = new CancellationTokenSource(within);
            try
            {
                while (true)
                {
                    var (text, rms) = await _messages.Reader.ReadAsync(timeout.Token);
                    if (text == null && rms >= minRms) return true;
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        public async Task WaitForAsync(Func<JsonElement, bool> match)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var (text, _) = await _messages.Reader.ReadAsync(timeout.Token);
                if (text == null) continue;
                using var doc = JsonDocument.Parse(text);
                if (match(doc.RootElement)) return;
            }
        }

        private async Task ReadAsync(ClientWebSocket socket)
        {
            var buffer = new byte[8192];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.MessageType == WebSocketMessageType.Text)
                        _messages.Writer.TryWrite((Encoding.UTF8.GetString(buffer, 0, result.Count), 0));
                    else if (result.Count == Pcm16WebAudioCodec.FrameBytes)
                        _messages.Writer.TryWrite((null, Rms(buffer)));
                }
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
            {
                // Socket closed at the end of the test.
            }
            finally
            {
                _messages.Writer.TryComplete();
            }
        }

        private static double Rms(byte[] frame)
        {
            double sum = 0;
            for (var i = 0; i < Pcm16WebAudioCodec.FrameSamples; i++)
            {
                var s = BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(i * 2)) / 32768.0;
                sum += s * s;
            }

            return Math.Sqrt(sum / Pcm16WebAudioCodec.FrameSamples);
        }
    }

    private static async Task<ClientWebSocket> ConnectBrowserAsync(int port, CancellationToken token, Guid? user = null)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://localhost:{port}/ws"), token);
        var hello = JsonSerializer.SerializeToUtf8Bytes(new HelloMessage("hello", "pcm16", (user ?? Guid.NewGuid()).ToString(), Guid.NewGuid().ToString(), "en-US"));
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
