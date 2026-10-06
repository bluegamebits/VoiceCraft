using System;

namespace VoiceCraft.Web.Audio;

/// <summary>
/// Streaming 3:1 resampler between 48 kHz and 16 kHz mono, used for the PCM fallback
/// (browsers without WebCodecs Opus). A windowed-sinc low-pass FIR (cutoff about 7.2 kHz)
/// runs before decimation and after zero-stuffing, so voice stays clean without aliasing.
/// Filter state carries across calls, so frames can be fed one at a time.
/// </summary>
public sealed class Resampler3x
{
    public const int Factor = 3;
    private const int Taps = 48;
    private static readonly float[] Kernel = BuildKernel();
    private readonly float[] _history = new float[Taps]; // circular buffer of 48 kHz samples
    private int _pos;

    /// <summary>48 kHz → 16 kHz. <paramref name="input"/> length must be a multiple of 3.</summary>
    public int Downsample(ReadOnlySpan<float> input, Span<float> output)
    {
        var outCount = input.Length / Factor;
        if (output.Length < outCount) throw new ArgumentException("Output too small.", nameof(output));
        for (var n = 0; n < outCount; n++)
        {
            for (var k = 0; k < Factor; k++) Push(input[n * Factor + k]);
            output[n] = Convolve();
        }

        return outCount;
    }

    /// <summary>16 kHz → 48 kHz.</summary>
    public int Upsample(ReadOnlySpan<float> input, Span<float> output)
    {
        var outCount = input.Length * Factor;
        if (output.Length < outCount) throw new ArgumentException("Output too small.", nameof(output));
        for (var n = 0; n < input.Length; n++)
        {
            for (var k = 0; k < Factor; k++)
            {
                Push(k == 0 ? input[n] * Factor : 0f); // zero-stuff, gain compensates for the inserted zeros
                output[n * Factor + k] = Convolve();
            }
        }

        return outCount;
    }

    private void Push(float sample)
    {
        _history[_pos] = sample;
        _pos = (_pos + 1) % Taps;
    }

    private float Convolve()
    {
        var acc = 0f;
        var idx = _pos;
        for (var k = 0; k < Taps; k++)
        {
            idx = idx == 0 ? Taps - 1 : idx - 1;
            acc += Kernel[k] * _history[idx];
        }

        return acc;
    }

    private static float[] BuildKernel()
    {
        const double cutoff = 7200.0 / 48000.0; // normalized to the 48 kHz rate
        var kernel = new float[Taps];
        var sum = 0.0;
        var mid = (Taps - 1) / 2.0;
        for (var i = 0; i < Taps; i++)
        {
            var x = i - mid;
            var sinc = Math.Abs(x) < 1e-9 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
            var window = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (Taps - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (Taps - 1));
            kernel[i] = (float)(sinc * window);
            sum += kernel[i];
        }

        for (var i = 0; i < Taps; i++) kernel[i] = (float)(kernel[i] / sum); // unity DC gain
        return kernel;
    }
}
