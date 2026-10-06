using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceCraft.Web;

/// <summary>
/// HTTP + WebSocket front end. Routes:
///   GET /ws      WebSocket for one browser session (see <see cref="WebSession"/>)
///   GET /health  JSON with session counts
///   GET /...     the bundled browser client (embedded wwwroot), when enabled
/// Meant to sit behind a TLS reverse proxy: browsers only allow microphone access on HTTPS.
/// </summary>
public sealed class WebBridgeServer(WebBridgeOptions options) : IDisposable
{
    private static readonly Assembly ResourceAssembly = typeof(WebBridgeServer).Assembly;
    private readonly HttpListener _listener = new();
    private readonly AudioClock _clock = new();
    private readonly ConcurrentDictionary<WebSession, byte> _sessions = new();

    public async Task RunAsync(CancellationToken token)
    {
        _listener.Prefixes.Add(options.Listen);
        _listener.Start();
        Log.Info($"VoiceCraft.Web listening on {options.Listen} -> VoiceCraft server {options.ServerHost}:{options.ServerPort}");
        await using var registration = token.Register(() => _listener.Stop());

        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (HttpListenerException ex)
            {
                Log.Error($"Listener error: {ex.Message}");
                continue;
            }

            _ = Task.Run(() => HandleAsync(context, token), token);
        }

        foreach (var session in _sessions.Keys) session.Dispose();
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken token)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (path == "/ws")
            {
                await HandleWebSocketAsync(context, token);
                return;
            }

            if (path == "/health")
            {
                var connected = 0;
                foreach (var session in _sessions.Keys)
                    if (session.State == Network.VcConnectionState.Connected) connected++;
                await WriteAsync(context.Response, 200, "application/json",
                    Encoding.UTF8.GetBytes($"{{\"sessions\":{_sessions.Count},\"connected\":{connected},\"maxSessions\":{options.MaxSessions}}}"));
                return;
            }

            if (options.ServeClient && context.Request.HttpMethod is "GET" or "HEAD" && TryGetAsset(path, out var asset, out var contentType))
            {
                await WriteAsync(context.Response, 200, contentType, asset);
                return;
            }

            await WriteAsync(context.Response, 404, "text/plain", "Not found"u8.ToArray());
        }
        catch (Exception ex)
        {
            Log.Error($"Request failed: {ex.Message}");
            try
            {
                context.Response.Abort();
            }
            catch
            {
                // ignored
            }
        }
    }

    private async Task HandleWebSocketAsync(HttpListenerContext context, CancellationToken token)
    {
        if (!context.Request.IsWebSocketRequest)
        {
            await WriteAsync(context.Response, 400, "text/plain", "WebSocket expected"u8.ToArray());
            return;
        }

        if (!options.IsOriginAllowed(context.Request.Headers["Origin"]))
        {
            await WriteAsync(context.Response, 403, "text/plain", "Origin not allowed"u8.ToArray());
            return;
        }

        var wsContext = await context.AcceptWebSocketAsync(null, TimeSpan.FromSeconds(15));
        var session = new WebSession(wsContext.WebSocket, options, _clock, token);
        if (_sessions.Count >= options.MaxSessions)
        {
            // Accept, explain, close: the browser can show a proper message instead of a failed connection.
            var full = "{\"t\":\"state\",\"state\":\"disconnected\",\"reason\":\"VoiceCraft.DisconnectReason.ServerFull\"}"u8.ToArray();
            await wsContext.WebSocket.SendAsync(full, System.Net.WebSockets.WebSocketMessageType.Text, true, token);
            await wsContext.WebSocket.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "full", token);
            session.Dispose();
            return;
        }

        _sessions.TryAdd(session, 0);
        Log.Info($"Session opened ({_sessions.Count} active)");
        try
        {
            await session.RunAsync();
        }
        finally
        {
            _sessions.TryRemove(session, out _);
            session.Dispose();
            Log.Info($"Session closed ({_sessions.Count} active)");
        }
    }

    private static bool TryGetAsset(string path, out byte[] data, out string contentType)
    {
        data = [];
        contentType = "application/octet-stream";
        if (path == "/") path = "/index.html";
        if (path.Contains("..")) return false;
        using var stream = ResourceAssembly.GetManifestResourceStream("wwwroot" + path);
        if (stream == null) return false;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        data = memory.ToArray();
        contentType = Path.GetExtension(path) switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".json" => "application/json",
            _ => "application/octet-stream"
        };
        return true;
    }

    private static async Task WriteAsync(HttpListenerResponse response, int status, string contentType, byte[] body)
    {
        response.StatusCode = status;
        response.ContentType = contentType;
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.ContentLength64 = body.Length;
        await response.OutputStream.WriteAsync(body);
        response.Close();
    }

    public void Dispose()
    {
        _clock.Dispose();
        _listener.Close();
    }
}
