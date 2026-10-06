using System;
using OpusSharp.Core;
using OpusSharp.Core.Extensions;
using VoiceCraft.Core.Interfaces;

namespace VoiceCraft.Web.Audio;

/// <summary>
/// Opus encoder at 48 kHz. Used both as the headless client's microphone encoder (mono, VoIP)
/// and to encode the mixed output that is streamed to the browser (stereo).
/// </summary>
public class OpusAudioEncoder : IAudioEncoder
{
    private readonly OpusEncoder _opusEncoder;
    private bool _disposed;

    public OpusAudioEncoder(int channels, int bitRate)
    {
        _opusEncoder = new OpusEncoder(VoiceCraft.Core.Constants.SampleRate, channels,
            OpusPredefinedValues.OPUS_APPLICATION_VOIP);
        try
        {
            _opusEncoder.SetPacketLostPercent(20);
        }
        catch (OpusException)
        {
            // Optimization hint only.
        }

        _opusEncoder.SetBitRate(bitRate);
    }

    ~OpusAudioEncoder()
    {
        Dispose(false);
    }

    /// <param name="samples">Samples per channel.</param>
    public int Encode(Span<float> data, Span<byte> output, int samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _opusEncoder.Encode(data, samples, output, output.Length);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing) _opusEncoder.Dispose();
        _disposed = true;
    }
}
