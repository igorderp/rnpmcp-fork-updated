using System.Buffers.Binary;
using System.IO.Compression;

namespace RengaMcp.Infrastructure;

// Minimal lossless PNG encoder for top-down 32-bit Windows DIB pixels.
// No desktop runtime, System.Drawing or third-party image library required.
public static class PngEncoder
{
    public static byte[] EncodeBgra(int width, int height, ReadOnlySpan<byte> pixels)
    {
        if (width < 1 || height < 1 || (long)width * height > 16_000_000 || pixels.Length != (long)width * height * 4)
            throw new ArgumentException("Invalid bitmap dimensions or pixel data.");
        using var result = new MemoryStream();
        result.Write(new byte[] {137,80,78,71,13,10,26,10});
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8; header[9] = 2; // 8-bit RGB, not the undefined DIB alpha channel.
        header[10] = 0; header[11] = 0; header[12] = 0;
        WriteChunk(result, "IHDR"u8, header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true))
        {
            var row = new byte[checked(width * 3 + 1)]; // PNG filter 0
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var p = (y * width + x) * 4; var q = 1 + x * 3;
                    row[q] = pixels[p + 2]; row[q+1] = pixels[p+1]; row[q+2] = pixels[p];
                }
                zlib.Write(row);
            }
        }
        WriteChunk(result, "IDAT"u8, compressed.ToArray());
        WriteChunk(result, "IEND"u8, []);
        return result.ToArray();
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length); stream.Write(number);
        stream.Write(type); stream.Write(data);
        uint crc = 0xffffffff;
        foreach (var b in type) crc = UpdateCrc(crc, b);
        foreach (var b in data) crc = UpdateCrc(crc, b);
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc); stream.Write(number);
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320);
        return crc;
    }
}
