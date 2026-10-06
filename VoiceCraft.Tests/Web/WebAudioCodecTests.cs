using System;
using VoiceCraft.Core;
using VoiceCraft.Web.Audio;

namespace VoiceCraft.Tests.Web;

public class WebAudioCodecTests
{
    [Fact]
    public void Resampler_DownThenUp_KeepsVoiceBandTone()
    {
        var down = new Resampler3x();
        var up = new Resampler3x();
        var input = new float[Constants.FrameSize];
        var low = new float[Constants.FrameSize / 3];
        var output = new float[Constants.FrameSize];
        double inPower = 0, outPower = 0;
        var phase = 0.0;
        for (var frame = 0; frame < 20; frame++)
        {
            for (var i = 0; i < input.Length; i++)
            {
                input[i] = (float)(0.5 * Math.Sin(phase));
                phase += 2 * Math.PI * 1000 / Constants.SampleRate; // 1 kHz: well inside the passband
            }

            down.Downsample(input, low);
            up.Upsample(low, output);
            if (frame < 5) continue; // let the filters settle
            foreach (var s in input) inPower += s * s;
            foreach (var s in output) outPower += s * s;
        }

        var gain = Math.Sqrt(outPower / inPower);
        Assert.InRange(gain, 0.9, 1.1);
    }

    [Fact]
    public void Resampler_Downsample_RejectsAboveNyquist()
    {
        var down = new Resampler3x();
        var input = new float[Constants.FrameSize];
        var low = new float[Constants.FrameSize / 3];
        double power = 0;
        var phase = 0.0;
        for (var frame = 0; frame < 20; frame++)
        {
            for (var i = 0; i < input.Length; i++)
            {
                input[i] = (float)(0.5 * Math.Sin(phase));
                phase += 2 * Math.PI * 12000 / Constants.SampleRate; // would alias at 16 kHz
            }

            down.Downsample(input, low);
            if (frame < 5) continue;
            foreach (var s in low) power += s * s;
        }

        var rms = Math.Sqrt(power / (15 * low.Length));
        Assert.True(rms < 0.02, $"12 kHz leaked through: rms {rms}");
    }

    [Fact]
    public void Pcm16Codec_RejectsWrongFrameSize()
    {
        using var codec = new Pcm16WebAudioCodec();
        var output = new float[Constants.FrameSize];
        Assert.False(codec.DecodeInput(new byte[10], output));
        Assert.True(codec.DecodeInput(new byte[Pcm16WebAudioCodec.FrameBytes], output));
    }

    [Fact]
    public void Pcm16Codec_EncodesStereoFrameTo640Bytes()
    {
        using var codec = new Pcm16WebAudioCodec();
        var stereo = new float[Constants.FrameSize * 2];
        var output = new byte[2048];
        Assert.Equal(Pcm16WebAudioCodec.FrameBytes, codec.EncodeOutput(stereo, output));
    }

    [Fact]
    public void Create_ReturnsNullForUnknownCodec()
    {
        Assert.Null(IWebAudioCodec.Create("mp3"));
        Assert.Null(IWebAudioCodec.Create(null));
        using var pcm = IWebAudioCodec.Create("pcm16");
        Assert.NotNull(pcm);
    }
}
