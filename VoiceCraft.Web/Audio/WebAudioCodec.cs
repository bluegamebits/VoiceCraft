using System;
using System.Buffers.Binary;
using VoiceCraft.Core;

namespace VoiceCraft.Web.Audio;

/// <summary>
/// How audio is carried over the browser WebSocket. Every frame is 20 ms.
/// Input (browser → bridge) becomes 960 mono samples at 48 kHz for the VoiceCraft client;
/// output (bridge → browser) starts as 960 stereo frames (1920 interleaved samples) at 48 kHz.
/// </summary>
public interface IWebAudioCodec : IDisposable
{
    string Name { get; }

    /// <summary>Decodes one browser microphone frame into <see cref="Constants.FrameSize"/> mono samples.</summary>
    bool DecodeInput(ReadOnlySpan<byte> payload, Span<float> output);

    /// <summary>Encodes one stereo output frame. Returns the number of bytes written, or 0 on failure.</summary>
    int EncodeOutput(Span<float> stereo, Span<byte> output);

    static IWebAudioCodec? Create(string? name) => name switch
    {
        OpusWebAudioCodec.CodecName => new OpusWebAudioCodec(),
        Pcm16WebAudioCodec.CodecName => new Pcm16WebAudioCodec(),
        _ => null
    };
}

/// <summary>Opus both ways: mono 48 kHz from the browser (WebCodecs), stereo 48 kHz back.</summary>
public sealed class OpusWebAudioCodec : IWebAudioCodec
{
    public const string CodecName = "opus";
    private const int MaxOpusPacket = 1275;
    private readonly OpusAudioDecoder _decoder = new(1);
    private readonly OpusAudioEncoder _encoder = new(Constants.PlaybackChannels, 64000);
    private readonly byte[] _packet = new byte[MaxOpusPacket];

    public string Name => CodecName;

    public bool DecodeInput(ReadOnlySpan<byte> payload, Span<float> output)
    {
        if (payload.IsEmpty || payload.Length > MaxOpusPacket) return false;
        payload.CopyTo(_packet);
        try
        {
            return _decoder.Decode(_packet.AsSpan(0, payload.Length), output, Constants.FrameSize) == Constants.FrameSize;
        }
        catch
        {
            return false; // malformed packet
        }
    }

    public int EncodeOutput(Span<float> stereo, Span<byte> output)
    {
        try
        {
            var written = _encoder.Encode(stereo, output, Constants.FrameSize);
            return written > 0 ? written : 0;
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        _decoder.Dispose();
        _encoder.Dispose();
    }
}

/// <summary>
/// Fallback for browsers without WebCodecs Opus: 16-bit little-endian PCM at 16 kHz, mono both ways
/// (320 samples, 640 bytes per frame). About 256 kbps each way.
/// </summary>
public sealed class Pcm16WebAudioCodec : IWebAudioCodec
{
    public const string CodecName = "pcm16";
    public const int SampleRate = 16000;
    public const int FrameSamples = SampleRate / 1000 * Constants.FrameSizeMs; // 320
    public const int FrameBytes = FrameSamples * 2;

    private readonly Resampler3x _up = new();
    private readonly Resampler3x _down = new();
    private readonly float[] _low = new float[FrameSamples];
    private readonly float[] _mono = new float[Constants.FrameSize];

    public string Name => CodecName;

    public bool DecodeInput(ReadOnlySpan<byte> payload, Span<float> output)
    {
        if (payload.Length != FrameBytes) return false;
        for (var i = 0; i < FrameSamples; i++)
            _low[i] = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(i * 2, 2)) / 32768f;
        _up.Upsample(_low, output);
        return true;
    }

    public int EncodeOutput(Span<float> stereo, Span<byte> output)
    {
        if (output.Length < FrameBytes) return 0;
        for (var i = 0; i < Constants.FrameSize; i++)
            _mono[i] = (stereo[i * 2] + stereo[i * 2 + 1]) * 0.5f;
        _down.Downsample(_mono, _low);
        for (var i = 0; i < FrameSamples; i++)
        {
            var s = Math.Clamp(_low[i], -1f, 1f);
            BinaryPrimitives.WriteInt16LittleEndian(output.Slice(i * 2, 2), (short)MathF.Round(s * 32767f));
        }

        return FrameBytes;
    }

    public void Dispose()
    {
    }
}
