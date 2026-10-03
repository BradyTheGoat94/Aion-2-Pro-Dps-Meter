using System.IO;
using System.Text.Json;

namespace Aion2DPSPro.Protocol;

public sealed class ReplayRecorder : IDisposable
{
    private readonly BinaryWriter data;
    private readonly StreamWriter index;
    private long offset;

    public ReplayRecorder(string directory)
    {
        Directory.CreateDirectory(directory);
        data = new BinaryWriter(File.Create(Path.Combine(directory, "stream.bin")));
        index = new StreamWriter(File.Create(Path.Combine(directory, "index.jsonl")));
    }

    public void Write(DateTime utc, string flow, uint sequence, ReadOnlySpan<byte> payload)
    {
        var b = payload.ToArray();
        data.Write(b);
        index.WriteLine(JsonSerializer.Serialize(new { utc, flow, sequence, offset, length = b.Length }));
        offset += b.Length;
        index.Flush();
        data.Flush();
    }

    public void Dispose() { index.Dispose(); data.Dispose(); }
}

