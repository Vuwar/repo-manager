using System.Text;

namespace RepoManager.Core.Agents;

/// <summary>The town's two sounds as in-memory WAV files (22 050 Hz, 16-bit mono PCM), in the prototype's style.</summary>
public static class Beeps
{
    private const int Rate = 22050;

    private sealed record Note(double Hz, double Seconds, double Volume, bool Square);

    public static byte[] Wav(AttentionSound sound)
    {
        Note[] notes = sound == AttentionSound.NeedsYou
            ? [new(880, 0.09, 0.25, true), new(1320, 0.09, 0.25, true)]
            : [new(660, 0.16, 0.15, false)];
        var samples = new List<short>();
        foreach (var n in notes)
        {
            var count = (int)(Rate * n.Seconds);
            for (var i = 0; i < count; i++)
            {
                var phase = Math.Sin(2 * Math.PI * n.Hz * i / Rate);
                var wave = n.Square ? Math.Sign(phase) : phase;
                // A short fade in and out keeps each note from clicking.
                var edge = Math.Min(1.0, Math.Min(i, count - 1 - i) / (Rate * 0.005));
                samples.Add((short)(wave * n.Volume * edge * short.MaxValue));
            }
        }
        return Encode(samples);
    }

    private static byte[] Encode(List<short> samples)
    {
        var data = samples.Count * 2;
        using var ms = new MemoryStream(44 + data);
        using var w = new BinaryWriter(ms, Encoding.ASCII);
        w.Write("RIFF"u8);
        w.Write(36 + data);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(Rate);
        w.Write(Rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(data);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}

/// <summary>Who a notification click is for: the session, while it still exists.</summary>
public static class AttentionTarget
{
    public static string? For(string? sessionId, AgentRegistry agents) =>
        sessionId != null && agents.Get(sessionId) != null ? sessionId : null;
}
