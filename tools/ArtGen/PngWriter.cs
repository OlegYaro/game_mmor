using System.Buffers.Binary;

namespace ArtGen;

/// <summary>
/// Minimal PNG encoder (RGBA, 8 bit, no interlace). The zlib stream uses stored deflate blocks so the bytes cannot
/// depend on the platform's zlib implementation or version.
/// </summary>
public static class PngWriter
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Encodes the canvas as a PNG file.</summary>
    public static byte[] Encode(Canvas canvas)
    {
        int stride = canvas.Width * 4;
        byte[] raw = new byte[(stride + 1) * canvas.Height];
        for (int y = 0; y < canvas.Height; y++)
        {
            raw[y * (stride + 1)] = 0; // filter type: none
            Buffer.BlockCopy(canvas.Pixels, y * stride, raw, (y * (stride + 1)) + 1, stride);
        }

        using MemoryStream png = new();
        png.Write(Signature);

        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0), (uint)canvas.Width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)canvas.Height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // colour type: RGBA
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", ZlibStored(raw));
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>CRC-32 (IEEE, as used by PNG) of a chunk's type and data.</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        byte[] body = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++)
        {
            body[i] = (byte)type[i];
        }

        data.CopyTo(body, 4);
        s.Write(body);
        BinaryPrimitives.WriteUInt32BigEndian(len, Crc32(body));
        s.Write(len);
    }

    private static byte[] ZlibStored(byte[] raw)
    {
        using MemoryStream z = new();
        z.WriteByte(0x78); // CMF: deflate, 32K window
        z.WriteByte(0x01); // FLG: no dictionary, fastest; (0x7801 % 31 == 0)
        int pos = 0;
        do
        {
            int n = Math.Min(65535, raw.Length - pos);
            bool last = pos + n >= raw.Length;
            z.WriteByte(last ? (byte)1 : (byte)0);
            z.WriteByte((byte)n);
            z.WriteByte((byte)(n >> 8));
            z.WriteByte((byte)~n);
            z.WriteByte((byte)(~n >> 8));
            z.Write(raw, pos, n);
            pos += n;
        }
        while (pos < raw.Length);

        uint a = 1;
        uint b = 0;
        foreach (byte v in raw)
        {
            a = (a + v) % 65521;
            b = (b + a) % 65521;
        }

        Span<byte> adler = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(adler, (b << 16) | a);
        z.Write(adler);
        return z.ToArray();
    }

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
