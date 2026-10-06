using System;

namespace VoiceCraft.Web;

public sealed class WebBridgeOptions
{
    /// <summary>HttpListener prefix the bridge listens on (HTTP and WebSocket). Put HTTPS in front of it.</summary>
    public string Listen { get; init; } = "http://127.0.0.1:9060/";

    /// <summary>VoiceCraft server the headless clients connect to over UDP.</summary>
    public string ServerHost { get; init; } = "127.0.0.1";

    public int ServerPort { get; init; } = 9050;

    /// <summary>Maximum simultaneous browser sessions.</summary>
    public int MaxSessions { get; init; } = 20;

    /// <summary>If non-empty, WebSocket connections are only accepted from these Origin header values.</summary>
    public string[] AllowedOrigins { get; init; } = [];

    /// <summary>Serve the bundled browser client (index.html, voicecraft-web.js, worklet).</summary>
    public bool ServeClient { get; init; } = true;

    public bool IsOriginAllowed(string? origin)
    {
        if (AllowedOrigins.Length == 0) return true;
        if (string.IsNullOrEmpty(origin)) return false;
        foreach (var allowed in AllowedOrigins)
            if (string.Equals(allowed.TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
