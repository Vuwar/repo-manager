using System.Text;
using RepoManager.Core.Agents;

namespace RepoManager.Tests;

public class BeepsTests
{
    [Theory]
    // needs-you: two 90 ms notes; done: one 160 ms note; 22050 Hz, 16-bit mono
    [InlineData(AttentionSound.NeedsYou, 2 * 1984)]
    [InlineData(AttentionSound.Done, 3528)]
    public void Wav_HeaderAndLength(AttentionSound sound, int samples)
    {
        var wav = Beeps.Wav(sound);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(1, BitConverter.ToInt16(wav, 20)); // PCM
        Assert.Equal(1, BitConverter.ToInt16(wav, 22)); // mono
        Assert.Equal(22050, BitConverter.ToInt32(wav, 24));
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(samples * 2, BitConverter.ToInt32(wav, 40));
        Assert.Equal(44 + samples * 2, wav.Length);
        Assert.Equal(wav.Length - 8, BitConverter.ToInt32(wav, 4));
    }

    [Fact]
    public void Wav_DoneIsQuieterThanNeedsYou()
    {
        static int Peak(byte[] w) => Enumerable.Range(0, (w.Length - 44) / 2).Max(i => Math.Abs((int)BitConverter.ToInt16(w, 44 + i * 2)));
        Assert.True(Peak(Beeps.Wav(AttentionSound.Done)) < Peak(Beeps.Wav(AttentionSound.NeedsYou)));
    }
}
