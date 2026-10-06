using System;
using System.CommandLine;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceCraft.Web;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var listenOption = new Option<string>("--listen", "-l")
        {
            Description = "HTTP prefix to listen on (WebSocket at /ws). Put an HTTPS reverse proxy in front of it.",
            DefaultValueFactory = _ => "http://127.0.0.1:9060/"
        };
        var serverHostOption = new Option<string>("--server-host", "-sh")
        {
            Description = "VoiceCraft server host the web clients connect to.",
            DefaultValueFactory = _ => "127.0.0.1"
        };
        var serverPortOption = new Option<int>("--server-port", "-sp")
        {
            Description = "VoiceCraft server UDP port.",
            DefaultValueFactory = _ => 9050
        };
        var maxSessionsOption = new Option<int>("--max-sessions", "-m")
        {
            Description = "Maximum simultaneous browser sessions.",
            DefaultValueFactory = _ => 20
        };
        var allowedOriginOption = new Option<string[]>("--allowed-origin", "-o")
        {
            Description = "Only accept WebSocket connections from this Origin (repeatable). Default: any.",
            DefaultValueFactory = _ => []
        };
        var noClientOption = new Option<bool>("--no-client")
        {
            Description = "Don't serve the bundled browser client; only /ws and /health.",
            DefaultValueFactory = _ => false
        };

        var root = new RootCommand("VoiceCraft web bridge: lets browsers join a VoiceCraft server without installing the app.")
        {
            listenOption, serverHostOption, serverPortOption, maxSessionsOption, allowedOriginOption, noClientOption
        };
        root.SetAction(async (parseResult, token) =>
        {
            var listen = parseResult.GetValue(listenOption)!;
            if (!listen.EndsWith('/')) listen += "/";
            var options = new WebBridgeOptions
            {
                Listen = listen,
                ServerHost = parseResult.GetValue(serverHostOption)!,
                ServerPort = parseResult.GetValue(serverPortOption),
                MaxSessions = Math.Max(1, parseResult.GetValue(maxSessionsOption)),
                AllowedOrigins = parseResult.GetValue(allowedOriginOption) ?? [],
                ServeClient = !parseResult.GetValue(noClientOption)
            };

            using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
            {
                ctx.Cancel = true;
                shutdown.Cancel();
            });
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                shutdown.Cancel();
            };

            using var server = new WebBridgeServer(options);
            try
            {
                await server.RunAsync(shutdown.Token);
            }
            catch (Exception ex)
            {
                Log.Error(ex.Message);
                return 1;
            }

            Log.Info("Stopped.");
            return 0;
        });

        return await root.Parse(args).InvokeAsync();
    }
}
