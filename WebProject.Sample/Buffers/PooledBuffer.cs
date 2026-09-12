using System.Buffers;

namespace WebProject.Sample.Buffers;

/// <summary>ArrayPool 기반 임시 버퍼.</summary>
public static class PooledBuffer
{
    public static byte[] Fill(Stream stream, int size)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(size);
        int read = stream.Read(buffer, 0, size);
        if (read < size) throw new EndOfStreamException();
        return buffer;
    }

    public static async Task<int> CopyAsync(Stream source, Stream target, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
        int total = 0;
        int n;
        while ((n = await source.ReadAsync(buffer, ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, n), ct);
            total += n;
        }
        ArrayPool<byte>.Shared.Return(buffer);
        return total;
    }

    public static string Hex(ReadOnlySpan<byte> data)
    {
        var chars = new char[data.Length * 2];
        for (int i = 0; i < data.Length; i++)
        {
            chars[i * 2] = "0123456789abcdef"[data[i] >> 4];
            chars[i * 2 + 1] = "0123456789abcdef"[data[i] & 0xF];
        }
        return new string(chars);
    }
}
