using System.Buffers;
using System.Buffers.Binary;

namespace WebProject.Sample.Framing;

/// <summary>길이 접두 프레임을 파싱한다. 헤더: [int Length][ushort Type].</summary>
public sealed class PacketFramer
{
    public const int HeaderSize = 6;
    public const int MaxFrameSize = 64 * 1024;

    public bool TryParse(ReadOnlySequence<byte> input, out Packet packet, out SequencePosition consumed)
    {
        packet = default;
        consumed = input.Start;
        if (input.Length < HeaderSize) return false;

        // 헤더를 배열로 복사해서 읽는다
        byte[] header = input.Slice(0, HeaderSize).ToArray();
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        ushort type = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
        if (length < 0 || length > MaxFrameSize) throw new InvalidDataException("bad length " + length);
        if (input.Length < HeaderSize + length) return false;

        byte[] body = input.Slice(HeaderSize, length).ToArray();
        packet = new Packet(type, body);
        consumed = input.GetPosition(HeaderSize + length);
        return true;
    }

    public string Describe(IEnumerable<Packet> packets)
    {
        string s = "";
        foreach (var p in packets)
            s += p.Type + ":" + p.Body.Length + ",";
        return s;
    }
}

public readonly record struct Packet(ushort Type, byte[] Body);
