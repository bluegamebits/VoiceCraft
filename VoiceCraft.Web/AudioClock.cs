using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using VoiceCraft.Core;

namespace VoiceCraft.Web;

/// <summary>
/// One thread that ticks every 20 ms (one audio frame) for all sessions: it pumps networking and
/// produces each session's mixed output frame. Stays on a Stopwatch schedule so it doesn't drift;
/// if it falls far behind (e.g. the machine was suspended) it skips ahead instead of bursting.
/// </summary>
public sealed class AudioClock : IDisposable
{
    private readonly ConcurrentDictionary<WebSession, byte> _sessions = new();
    private readonly Thread _thread;
    private volatile bool _running = true;

    public AudioClock()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "VoiceCraft.Web AudioClock", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public int Count => _sessions.Count;

    public void Add(WebSession session) => _sessions.TryAdd(session, 0);

    public void Remove(WebSession session) => _sessions.TryRemove(session, out _);

    private void Run()
    {
        var stopwatch = Stopwatch.StartNew();
        var next = 0.0;
        while (_running)
        {
            foreach (var session in _sessions.Keys)
            {
                try
                {
                    session.Tick();
                }
                catch (Exception ex)
                {
                    Log.Error($"Session tick failed: {ex.Message}");
                }
            }

            next += Constants.FrameSizeMs;
            var now = stopwatch.Elapsed.TotalMilliseconds;
            if (now - next > 200) next = now; // far behind: resync instead of catching up in a burst
            var wait = next - now;
            if (wait > 1) Thread.Sleep((int)wait);
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join(1000);
    }
}
