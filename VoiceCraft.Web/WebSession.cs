using System;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using VoiceCraft.Core;
using VoiceCraft.Core.Audio;
using VoiceCraft.Core.World;
using VoiceCraft.Network;
using VoiceCraft.Network.Clients;
using VoiceCraft.Network.World;
using VoiceCraft.Web.Audio;

namespace VoiceCraft.Web;

/// <summary>
/// One browser connection. It owns a headless <see cref="LiteNetVoiceCraftClient"/> that joins the
/// VoiceCraft server like the native app does, so the server and the Minecraft add-on see a normal
/// client (binding, proximity, effects all unchanged). The browser only records the microphone and
/// plays back the mix that the client's own effect pipeline produces.
///
/// Protocol (WebSocket):
///   text frames are JSON control messages, {"t": "&lt;type&gt;", ...};
///   binary frames are one 20 ms audio frame in the codec chosen in "hello".
/// Browser → bridge: hello {codec, user, server, locale}, mute {value}, deafen {value},
///   volume {value 0..2}, inputVolume {value 0..2}, sensitivity {value 0..1}.
/// Bridge → browser: state {state, reason?}, title {text}, description {text}, speaking {value},
///   muted/deafened/serverMuted/serverDeafened {value}, peers {list: [{id, name, speaking}]}.
/// </summary>
public sealed class WebSession : IDisposable
{
    private const int MaxMessageBytes = 8192;
    private const int SilentFramesBeforePause = 10; // keep sending 200 ms after sound stops, then pause
    private const float SilencePeak = 1e-4f;
    private const int PeersEveryTicks = 25; // 500 ms
    private const float DefaultSensitivity = 0.04f; // same default as the native app

    private readonly WebSocket _socket;
    private readonly WebBridgeOptions _options;
    private readonly AudioClock _clock;
    private readonly CancellationTokenSource _cts;
    private readonly Channel<string> _control = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<byte[]> _audio = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(6)
    {
        FullMode = BoundedChannelFullMode.DropOldest, // a slow connection loses old audio instead of building delay
        SingleReader = true
    });

    private readonly float[] _mic = new float[Constants.FrameSize];
    private readonly float[] _out = new float[Constants.FrameSize * Constants.PlaybackChannels];
    private readonly byte[] _encoded = new byte[Constants.MaximumEncodedBytes * 4];
    private readonly Lock _clientLock = new();
    private LiteNetVoiceCraftClient? _client;
    private IWebAudioCodec? _codec;
    private int _silentFrames = SilentFramesBeforePause;
    private int _tick;
    private string _lastPeers = string.Empty;
    private int _closing;
    private WebSocketCloseStatus _closeStatus = WebSocketCloseStatus.NormalClosure;
    private string _closeDescription = "bye";
    private int _disposed;

    public WebSession(WebSocket socket, WebBridgeOptions options, AudioClock clock, CancellationToken shutdown)
    {
        _socket = socket;
        _options = options;
        _clock = clock;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
    }

    public VcConnectionState State => _client?.ConnectionState ?? VcConnectionState.Disconnected;

    /// <summary>Runs until the browser or the VoiceCraft server ends the session.</summary>
    /// <remarks>
    /// Socket operations never get a cancellation token: cancelling a pending WebSocket call aborts the
    /// connection instead of closing it. Shutdown goes through <see cref="RequestClose"/>, and the send
    /// loop (the only sender) finishes with a proper close frame.
    /// </remarks>
    public async Task RunAsync()
    {
        var sender = Task.Run(SendLoopAsync);
        // If the browser doesn't answer our close frame, give up on it after a few seconds.
        await using var abort = _cts.Token.Register(() => _ = AbortLaterAsync());
        try
        {
            await ReceiveLoopAsync();
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            // Browser went away.
        }
        finally
        {
            _clock.Remove(this);
            RequestClose(WebSocketCloseStatus.NormalClosure, "bye");
            await StopClientAsync();
            await Task.WhenAny(sender, Task.Delay(3000));
        }
    }

    private async Task AbortLaterAsync()
    {
        await Task.Delay(5000);
        if (_socket.State != WebSocketState.Closed) _socket.Abort();
    }

    /// <summary>Called by <see cref="AudioClock"/> every 20 ms.</summary>
    public void Tick()
    {
        var client = _client;
        if (client == null) return;
        lock (_clientLock)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            client.Update();
            if (client.ConnectionState != VcConnectionState.Connected || _cts.IsCancellationRequested) return;
            ProduceOutput(client);
            if (++_tick % PeersEveryTicks == 0) SendPeers(client);
        }
    }

    #region Receive

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[MaxMessageBytes];
        while (_socket.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            var count = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                if (count >= buffer.Length)
                {
                    RequestClose(WebSocketCloseStatus.MessageTooBig, "message too big");
                    return;
                }

                result = await _socket.ReceiveAsync(buffer.AsMemory(count), CancellationToken.None);
                count += result.Count;
            } while (!result.EndOfMessage);

            if (_cts.IsCancellationRequested && result.MessageType != WebSocketMessageType.Close) continue; // closing: drain

            switch (result.MessageType)
            {
                case WebSocketMessageType.Close:
                    return;
                case WebSocketMessageType.Text:
                    HandleControl(buffer.AsSpan(0, count));
                    break;
                case WebSocketMessageType.Binary:
                    HandleAudio(buffer.AsSpan(0, count));
                    break;
            }
        }
    }

    private void HandleControl(ReadOnlySpan<byte> json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json.ToArray());
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("t", out var typeElement)) return;
            var client = _client;
            switch (typeElement.GetString())
            {
                case "hello":
                    Start(root);
                    break;
                case "mute" when client != null && TryGetBool(root, out var mute):
                    client.Muted = mute;
                    break;
                case "deafen" when client != null && TryGetBool(root, out var deafen):
                    client.Deafened = deafen;
                    break;
                case "volume" when client != null && TryGetFloat(root, out var volume):
                    client.OutputVolume = volume;
                    break;
                case "inputVolume" when client != null && TryGetFloat(root, out var inputVolume):
                    client.InputVolume = inputVolume;
                    break;
                case "sensitivity" when client != null && TryGetFloat(root, out var sensitivity):
                    client.MicrophoneSensitivity = sensitivity;
                    break;
            }
        }
    }

    private void HandleAudio(ReadOnlySpan<byte> payload)
    {
        var client = _client;
        var codec = _codec;
        if (client == null || codec == null) return;
        lock (_clientLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || !codec.DecodeInput(payload, _mic)) return;
            SampleVolume.Read(_mic, client.InputVolume);
            client.Write(_mic);
        }
    }

    #endregion

    #region VoiceCraft client

    private void Start(JsonElement hello)
    {
        if (_client != null) return; // only one hello per session

        var codec = IWebAudioCodec.Create(GetString(hello, "codec"));
        if (codec == null)
        {
            SendState("disconnected", "VoiceCraft.Web.UnsupportedCodec");
            RequestClose(WebSocketCloseStatus.PolicyViolation, "unsupported codec");
            return;
        }

        var userGuid = Guid.TryParse(GetString(hello, "user"), out var u) ? u : Guid.NewGuid();
        var serverUserGuid = Guid.TryParse(GetString(hello, "server"), out var s) ? s : Guid.NewGuid();
        var locale = GetString(hello, "locale");
        if (string.IsNullOrWhiteSpace(locale) || locale.Length > 16) locale = Constants.DefaultLanguage;

        var client = new LiteNetVoiceCraftClient(
            new OpusAudioEncoder(Constants.RecordingChannels, 32000),
            () => new OpusAudioDecoder())
        {
            InputVolume = 1f,
            OutputVolume = 1f,
            MicrophoneSensitivity = DefaultSensitivity
        };
        client.OnConnected += () => SendState("connected");
        client.OnDisconnected += reason =>
        {
            SendState("disconnected", reason);
            RequestClose(WebSocketCloseStatus.NormalClosure, "voicecraft disconnected");
        };
        client.OnSetTitle += text => SendText("title", text);
        client.OnSetDescription += text => SendText("description", text);
        client.OnSpeakingUpdated += value => SendBool("speaking", value);
        client.OnServerMuteUpdated += value => SendBool("serverMuted", value);
        client.OnServerDeafenUpdated += value => SendBool("serverDeafened", value);
        client.OnMuteUpdated += (value, _) => SendBool("muted", value);
        client.OnDeafenUpdated += (value, _) => SendBool("deafened", value);

        _codec = codec;
        _client = client;
        _clock.Add(this); // the clock pumps client.Update(), which the connect handshake needs
        SendState("connecting");
        _ = ConnectAsync(client, userGuid, serverUserGuid, locale);
    }

    private async Task ConnectAsync(LiteNetVoiceCraftClient client, Guid userGuid, Guid serverUserGuid, string locale)
    {
        try
        {
            var info = await client.PingAsync(_options.ServerHost, _options.ServerPort, _cts.Token);
            await client.ConnectAsync(_options.ServerHost, _options.ServerPort, userGuid, serverUserGuid, locale,
                info.PositioningType);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SendState("disconnected", ex is TimeoutException ? "VoiceCraft.Web.ServerUnreachable" : ex.Message);
            RequestClose(WebSocketCloseStatus.NormalClosure, "connect failed");
        }
        catch (OperationCanceledException)
        {
            // Session ended while connecting.
        }
    }

    private async Task StopClientAsync()
    {
        var client = _client;
        if (client == null) return;
        try
        {
            var disconnect = client.DisconnectAsync("VoiceCraft.DisconnectReason.Manual");
            await Task.WhenAny(disconnect, Task.Delay(3000));
        }
        catch
        {
            // Best effort.
        }
    }

    private void ProduceOutput(VoiceCraftClient client)
    {
        var codec = _codec;
        if (codec == null) return;
        var frame = _out.AsSpan();
        frame.Clear();
        var read = client.Read(frame);
        if (read > 0) SampleVolume.Read(frame[..read], client.OutputVolume);

        var peak = 0f;
        for (var i = 0; i < frame.Length; i++)
        {
            var sample = frame[i];
            if (!float.IsFinite(sample)) sample = 0f;
            sample = Math.Clamp(sample, -1f, 1f);
            frame[i] = sample;
            peak = Math.Max(peak, Math.Abs(sample));
        }

        if (peak < SilencePeak)
        {
            if (_silentFrames >= SilentFramesBeforePause) return; // nothing to hear: save bandwidth
            _silentFrames++;
        }
        else
        {
            _silentFrames = 0;
        }

        var written = codec.EncodeOutput(frame, _encoded);
        if (written <= 0) return;
        _audio.Writer.TryWrite(_encoded.AsSpan(0, written).ToArray());
    }

    private void SendPeers(VoiceCraftClient client)
    {
        var peers = client.World.Entities
            .OfType<VoiceCraftClientEntity>()
            .Where(x => x.IsVisible)
            .OrderBy(x => x.Id)
            .ToArray();
        var json = BuildJson(writer =>
        {
            writer.WriteString("t", "peers");
            writer.WriteStartArray("list");
            foreach (var peer in peers)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", peer.Id);
                writer.WriteString("name", peer.Name);
                writer.WriteBoolean("speaking", peer.Speaking);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
        if (json == _lastPeers) return;
        _lastPeers = json;
        _control.Writer.TryWrite(json);
    }

    #endregion

    #region Send

    private async Task SendLoopAsync()
    {
        var token = _cts.Token;
        try
        {
            while (!token.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                if (_control.Reader.TryRead(out var text))
                {
                    await _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
                    continue;
                }

                if (_audio.Reader.TryRead(out var audio))
                {
                    await _socket.SendAsync(audio, WebSocketMessageType.Binary, true, CancellationToken.None);
                    continue;
                }

                await Task.WhenAny(
                    _control.Reader.WaitToReadAsync(token).AsTask(),
                    _audio.Reader.WaitToReadAsync(token).AsTask());
            }

            // Closing: flush control messages (e.g. the disconnect reason), then the close frame.
            while (_socket.State == WebSocketState.Open && _control.Reader.TryRead(out var last))
                await _socket.SendAsync(Encoding.UTF8.GetBytes(last), WebSocketMessageType.Text, true, CancellationToken.None);
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await _socket.CloseOutputAsync(_closeStatus, _closeDescription, CancellationToken.None);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            // Connection already gone.
        }
    }

    private void SendState(string state, string? reason = null) =>
        _control.Writer.TryWrite(BuildJson(writer =>
        {
            writer.WriteString("t", "state");
            writer.WriteString("state", state);
            if (reason != null) writer.WriteString("reason", reason);
        }));

    private void SendText(string type, string text) =>
        _control.Writer.TryWrite(BuildJson(writer =>
        {
            writer.WriteString("t", type);
            writer.WriteString("text", text);
        }));

    private void SendBool(string type, bool value) =>
        _control.Writer.TryWrite(BuildJson(writer =>
        {
            writer.WriteString("t", type);
            writer.WriteBoolean("value", value);
        }));

    /// <summary>Ends the session: the send loop flushes, sends the close frame and stops. First caller's status wins.</summary>
    private void RequestClose(WebSocketCloseStatus status, string description)
    {
        if (Interlocked.Exchange(ref _closing, 1) == 0)
        {
            _closeStatus = status;
            _closeDescription = description;
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Session already disposed.
        }
    }

    #endregion

    #region Helpers

    private static string BuildJson(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryGetBool(JsonElement element, out bool value)
    {
        value = false;
        if (!element.TryGetProperty("value", out var v) || v.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        value = v.GetBoolean();
        return true;
    }

    private static bool TryGetFloat(JsonElement element, out float value)
    {
        value = 0;
        return element.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number &&
               v.TryGetSingle(out value) && float.IsFinite(value);
    }

    #endregion

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _clock.Remove(this);
        lock (_clientLock)
        {
            _client?.Dispose();
            _codec?.Dispose();
        }

        _cts.Dispose();
    }
}
