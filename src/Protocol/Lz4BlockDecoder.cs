namespace Aion2DPSPro.Protocol;

/// <summary>Small dependency-free LZ4 block decoder for AION batch frames.</summary>
public static class Lz4BlockDecoder
{
    public static int Decompress(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int s = 0, d = 0;
        while (s < src.Length)
        {
            byte token = src[s++];
            int literals = token >> 4;
            if (literals == 15) { int x; do { if (s >= src.Length) return -1; x = src[s++]; literals += x; } while (x == 255); }
            if (s + literals > src.Length || d + literals > dst.Length) return -1;
            src.Slice(s, literals).CopyTo(dst.Slice(d)); s += literals; d += literals;
            if (s >= src.Length) break;
            if (s + 2 > src.Length) return -1;
            int offset = src[s] | (src[s + 1] << 8); s += 2;
            if (offset <= 0 || offset > d) return -1;
            int match = token & 0x0F;
            if (match == 15) { int x; do { if (s >= src.Length) return -1; x = src[s++]; match += x; } while (x == 255); }
            match += 4;
            if (d + match > dst.Length) return -1;
            int from = d - offset;
            for (int i = 0; i < match; i++) dst[d++] = dst[from + i];
        }
        return d;
    }
}
