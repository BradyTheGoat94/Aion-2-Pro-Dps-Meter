namespace Aion2DPSPro.Protocol;

/// <summary>
/// AION 2 stream framing based on publicly documented current implementations:
/// varint length, optional F0-FE type marker, and FF FF + int32 size + LZ4 batch.
/// </summary>
public sealed class StreamFramer
{
    private readonly List<byte> buffer = new();
    private const int MaxMessageLen = 2_000_000;
    private const int MaxBufferBytes = 4_000_000;
    public event Action<DecoderDiagnostic>? Diagnostic;

    public IEnumerable<byte[]> Push(byte[] bytes, DateTime utc)
    {
        if (bytes.Length == 0) yield break;
        buffer.AddRange(bytes.ToArray());
        if (buffer.Count > MaxBufferBytes) { Diagnostic?.Invoke(new(utc,"framing","Buffer overflow; reset",buffer.Count)); buffer.Clear(); yield break; }

        int i = 0;
        while (i < buffer.Count)
        {
            while (i < buffer.Count && buffer[i] == 0) i++;
            if (i >= buffer.Count) break;
            int start = i, p = i;
            if (!TryReadVarint(buffer, ref p, out uint val, out int consumed)) break;
            long len64 = (long)val + consumed - 4;
            if (len64 <= 0 || len64 > MaxMessageLen) { i = start + 1; continue; }
            int len = (int)len64;
            if (start + len > buffer.Count) break;
            var frame = buffer.GetRange(start, len).ToArray();
            foreach (var msg in ExpandFrame(frame, consumed, utc)) yield return msg;
            i = start + len;
        }
        if (i > 0) buffer.RemoveRange(0, i);
    }

    private IEnumerable<byte[]> ExpandFrame(byte[] frame, int varintLen, DateTime utc)
    {
        int p = varintLen;
        if (p < frame.Length && (frame[p] & 0xF0) == 0xF0 && frame[p] != 0xFF) p++;
        if (p + 6 <= frame.Length && frame[p] == 0xFF && frame[p+1] == 0xFF)
        {
            int size = BitConverter.ToInt32(frame, p + 2);
            if (size <= 0 || size > MaxMessageLen) yield break;
            var outBuf = new byte[size];
            int written = Lz4BlockDecoder.Decompress(frame.AsSpan(p + 6), outBuf);
            if (written <= 0) { Diagnostic?.Invoke(new(utc,"lz4","Decompression failed",frame.Length)); yield break; }
            int q = 0;
            while (q < written)
            {
                while (q < written && outBuf[q] == 0) q++;
                if (q >= written) break;
                int st=q, rp=q;
                if (!ProtocolUtils.TryReadVarUInt(outBuf.AsSpan(0,written), ref rp, out uint v, out int c)) break;
                long ml=(long)v+c-4;
                if (ml<=0 || st+ml>written) { yield return outBuf.AsSpan(st,written-st).ToArray(); break; }
                yield return outBuf.AsSpan(st,(int)ml).ToArray(); q=st+(int)ml;
            }
            yield break;
        }
        yield return frame;
    }

    private static bool TryReadVarint(List<byte> b, ref int p, out uint value, out int consumed)
    {
        value=0; consumed=0; int shift=0;
        while (p<b.Count && consumed<5) { byte x=b[p++]; consumed++; value|=(uint)(x&0x7F)<<shift; if((x&0x80)==0)return true; shift+=7; }
        value=0; return false;
    }
}

