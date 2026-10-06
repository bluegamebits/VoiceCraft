using System;
using OpusSharp.Core;
using VoiceCraft.Core.Interfaces;

namespace VoiceCraft.Web.Audio;

/// <summary>Opus decoder at 48 kHz (mono by default, like the native client).</summary>
public class OpusAudioDecoder(int channels = VoiceCraft.Core.Constants.RecordingChannels) : IAudioDecoder
{
    private readonly OpusDecoder _opusDecoder = new(VoiceCraft.Core.Constants.SampleRate, channels);
    private bool _disposed;

    ~OpusAudioDecoder()
    {
        Dispose(false);
    }

    /// <param name="samples">Samples per channel.</param>
    public int Decode(Span<byte> buffer, Span<float> output, int samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _opusDecoder.Decode(buffer, buffer.Length, output, samples, false);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing) _opusDecoder.Dispose();
        _disposed = true;
    }
}
