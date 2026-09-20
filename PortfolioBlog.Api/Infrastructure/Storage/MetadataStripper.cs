using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace PortfolioBlog.Api.Infrastructure.Storage;

/// <summary>이미지를 <b>디코딩하지 않고</b> 컨테이너 구조만 따라가며 메타데이터(EXIF·GPS·XMP·IPTC·주석·텍스트)를 버린다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 상태는 호출이 넘긴 스트림뿐이다(스트림 자체는 호출자가 독점해야 한다).</description></item>
/// <item><description><b>Memory Allocation:</b> 파일 크기와 무관하게 64KB 풀 버퍼 하나 + 스택 버퍼. 선언된 길이만큼 미리 할당하지 않으므로 "길이를 속인 청크"로 메모리를 부풀릴 수 없다.</description></item>
/// <item><description><b>Blocking:</b> 동기 스트림 I/O. 호출부는 임시 파일 스트림을 넘기며 10MB 이하임을 먼저 보장한다.</description></item>
/// </list>
/// 디코더를 쓰지 않는 이유: 이미지 디코더는 그 자체가 큰 공격 표면이고, 재인코딩은 화질을 바꾼다. 컨테이너 파싱은 "길이 필드를 읽고 건너뛰거나 복사"뿐이다.
/// 출력이 멱등이라(깨끗한 파일을 다시 넣으면 같은 바이트) 그 SHA-256을 저장 경로로 쓸 수 있다.
/// 구조가 어긋나면 <see cref="InvalidDataException"/> — 호출부는 이를 "지원하지 않는 이미지"(415)로 바꾼다.
/// </remarks>
public static class MetadataStripper
{
    private const int CopyBufferSize = 64 * 1024; // 85,000바이트 미만: LOH에 올라가지 않는다

    // PNG에서 남기는 청크: 화상·팔레트·투명도·색 공간·물리 해상도·APNG. 그 밖의 보조 청크(eXIf, tEXt, zTXt, iTXt, tIME, 알 수 없는 것)는 버린다.
    private static readonly HashSet<string> PngKeep = new(StringComparer.Ordinal)
    {
        "IHDR", "PLTE", "IDAT", "IEND", "tRNS", "gAMA", "cHRM", "sRGB", "iCCP", "sBIT", "bKGD", "pHYs", "hIST", "sPLT",
        "acTL", "fcTL", "fdAT", "cICP", "mDCv", "cLLi",
    };

    /// <summary>이미지 컨테이너 구조를 처음부터 끝까지 읽으며 메타데이터 세그먼트·청크를 버리고 화상 데이터만 <paramref name="output"/>에 다시 쓴다.</summary>
    /// <param name="kind">이미 <see cref="ImageSignature.Detect"/>로 판정한 형식. 이 값이 실제 바이트와 다르면 즉시 <see cref="InvalidDataException"/>이 난다.</param>
    /// <param name="input">현재 위치부터 읽는 원본 스트림. 소유권은 호출자에게 있으며 이 메서드는 닫거나 되감지 않는다.</param>
    /// <param name="output">결과를 쓰는 스트림. WebP는 다 쓴 뒤 RIFF 크기 필드를 되돌아가 다시 쓰므로 <b>seek 가능</b>해야 한다. 소유권은 호출자에게 있으며 이 메서드는 닫지 않는다.</param>
    /// <exception cref="InvalidDataException">구조가 손상됐거나 파일이 잘렸거나 길이 필드가 남은 범위를 벗어날 때.</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe(공유 가변 상태 없음). 단, 같은 <paramref name="input"/>/<paramref name="output"/> 인스턴스를 여러 스레드에서 동시에 넘기면 스트림 자체가 스레드 안전하지 않으므로 호출자가 직렬화해야 한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="ArrayPool{T}.Shared"/>에서 64KB 버퍼 하나를 빌려 청크/세그먼트 데이터 복사에 재사용하고, 헤더는 스택(<c>stackalloc</c>) 버퍼로 읽는다. 청크가 선언한 길이만큼 미리 할당하지 않으므로 거짓 길이 필드로 힙을 부풀릴 수 없다. <paramref name="input"/>과 <paramref name="output"/>의 소유권은 호출 끝까지 호출자에게 남고, 이 메서드는 둘 다 dispose하지 않는다. <paramref name="input"/>은 순방향으로만 읽으므로 seek 불가능해도 되지만, <paramref name="output"/>은 WebP 크기 재기록 때문에 반드시 seek 가능해야 한다.</description></item>
    /// <item><description><b>Blocking:</b> 동기 <see cref="Stream.Read(byte[],int,int)"/>/<see cref="Stream.Write(byte[],int,int)"/> 호출로 이뤄진다(비동기 오버로드 없음). 호출부는 이 메서드를 요청 스레드가 아니라 파일 크기를 이미 제한한 백그라운드/워커 경로에서, 임시 파일 스트림을 대상으로 호출해야 한다.</description></item>
    /// </list>
    /// </remarks>
    public static void Strip(ImageKind kind, Stream input, Stream output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        switch (kind)
        {
            case ImageKind.Jpeg: StripJpeg(input, output); break;
            case ImageKind.Png: StripPng(input, output); break;
            case ImageKind.WebP: StripWebP(input, output); break;
            case ImageKind.Gif: StripGif(input, output); break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    // JPEG: SOI, 이어서 세그먼트 = FFxx + 길이(2바이트 빅엔디언, 자기 자신 포함) + 페이로드. SOS(FFDA) 뒤에는 엔트로피 부호화 데이터가 오고,
    // 그 구간은 "다음 진짜 마커"까지 따라간다 — SOS 이후를 끝까지 그대로 복사하면 (1) EOI 뒤에 덧붙인 데이터(폴리글랏·숨긴 메타데이터)와
    // (2) 프로그레시브 JPEG의 스캔 사이 세그먼트가 걸러지지 않는다(스파이크에서 10개 스캔짜리 파일과 ZIP을 덧붙인 파일로 확인).
    private static void StripJpeg(Stream input, Stream output)
    {
        Span<byte> b = stackalloc byte[2]; // 마커·길이 필드를 힙 할당 없이 읽기 위한 2바이트 스택 버퍼
        ReadExact(input, b);
        if (b[0] != 0xFF || b[1] != 0xD8) throw new InvalidDataException("JPEG SOI가 없다.");
        output.Write(b);
        var sawScan = false;
        var marker = ReadJpegMarker(input);
        while (true)
        {
            if (marker == 0xD9)
            {
                if (!sawScan) throw new InvalidDataException("JPEG에 화상 데이터(SOS)가 없다.");
                output.WriteByte(0xFF); output.WriteByte(0xD9);
                return; // EOI 뒤의 바이트는 버린다
            }
            if (marker is (>= 0xD0 and <= 0xD7) or 0x01) // 길이 없는 마커
            {
                output.WriteByte(0xFF); output.WriteByte(marker);
                marker = ReadJpegMarker(input);
                continue;
            }
            ReadExact(input, b);
            var length = BinaryPrimitives.ReadUInt16BigEndian(b);
            if (length < 2) throw new InvalidDataException("JPEG 세그먼트 길이가 잘못됐다.");
            var payload = length - 2;
            // 버림: APP1(Exif·XMP), APP3~APP13·APP15(APP13 = IPTC/Photoshop), COM.
            // 남김: APP0(JFIF), APP2(ICC 프로파일 — 없으면 색이 달라진다), APP14(Adobe 색 변환 — 없으면 CMYK/YCCK가 깨진다), 그 밖의 모든 화상 세그먼트.
            var drop = marker == 0xFE || marker == 0xE1 || (marker is >= 0xE3 and <= 0xEF && marker != 0xEE);
            if (drop)
            {
                Skip(input, payload);
                marker = ReadJpegMarker(input);
                continue;
            }
            output.WriteByte(0xFF); output.WriteByte(marker); output.Write(b);
            CopyExact(input, output, payload);
            if (marker == 0xDA)
            {
                sawScan = true;
                marker = CopyJpegEntropyData(input, output);
            }
            else
            {
                marker = ReadJpegMarker(input);
            }
        }
    }

    private static byte ReadJpegMarker(Stream input)
    {
        if (ReadByte(input) != 0xFF) throw new InvalidDataException("JPEG 마커가 아니다.");
        var marker = ReadByte(input);
        while (marker == 0xFF) marker = ReadByte(input); // 채움 바이트
        return marker;
    }

    // 엔트로피 부호화 데이터 안에서 FF는 항상 이스케이프된다: FF00(바이트 채움)과 FFD0~FFD7(재시작 마커)은 데이터의 일부이고,
    // 그 밖의 FFxx는 스캔을 끝내는 진짜 마커다. 그 마커를 돌려준다. 파일이 EOI 없이 끝나면 ReadByte가 InvalidDataException을 던진다.
    // 바이트 단위 읽기지만 호출부가 넘기는 FileStream은 64KB 버퍼를 가지므로 시스템 호출은 버퍼 단위로만 일어난다.
    private static byte CopyJpegEntropyData(Stream input, Stream output)
    {
        while (true)
        {
            var value = ReadByte(input);
            if (value != 0xFF)
            {
                output.WriteByte(value);
                continue;
            }
            var next = ReadByte(input);
            while (next == 0xFF) next = ReadByte(input);
            if (next == 0x00 || (next >= 0xD0 && next <= 0xD7))
            {
                output.WriteByte(0xFF); output.WriteByte(next);
                continue;
            }
            return next;
        }
    }

    // PNG: 시그니처 8바이트, 이어서 청크 = 길이(4, 빅엔디언) + 종류(4) + 데이터 + CRC(4).
    private static void StripPng(Stream input, Stream output)
    {
        Span<byte> signature = stackalloc byte[8]; // PNG 매직 넘버 고정 크기 — 스택에 두면 힙 할당·GC 압력 없음
        ReadExact(input, signature);
        output.Write(signature);
        Span<byte> header = stackalloc byte[8]; // 청크 길이(4) + 종류(4) 고정 크기 헤더
        while (true)
        {
            ReadExact(input, header);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            if (length > int.MaxValue) throw new InvalidDataException("PNG 청크 길이가 잘못됐다.");
            var type = Encoding.ASCII.GetString(header.Slice(4, 4));
            var total = (long)length + 4; // 데이터 + CRC
            if (PngKeep.Contains(type)) { output.Write(header); CopyExact(input, output, total); }
            else Skip(input, total);
            if (type == "IEND") return; // IEND 뒤에 덧붙은 바이트는 버린다
        }
    }

    // WebP: "RIFF" + 크기(4, 리틀엔디언) + "WEBP", 이어서 청크 = FourCC(4) + 크기(4) + 데이터(+홀수면 패딩 1). VP8X 플래그: 0x08 EXIF, 0x04 XMP.
    private static void StripWebP(Stream input, Stream output)
    {
        if (!output.CanSeek) throw new ArgumentException("WebP 출력 스트림은 seek 가능해야 한다(RIFF 크기를 다시 쓴다).", nameof(output));
        Span<byte> riff = stackalloc byte[12]; // RIFF 헤더 고정 크기(FourCC 4 + 크기 4 + WEBP 4)
        ReadExact(input, riff);
        var start = output.Position;
        output.Write(riff);
        var declaredEnd = 8 + (long)BinaryPrimitives.ReadUInt32LittleEndian(riff.Slice(4, 4));
        Span<byte> chunk = stackalloc byte[8]; // 청크 헤더 고정 크기(FourCC 4 + 크기 4)
        long consumed = 12;
        var sawImage = false;
        while (consumed + 8 <= declaredEnd)
        {
            ReadExact(input, chunk);
            consumed += 8;
            var fourCc = Encoding.ASCII.GetString(chunk[..4]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk.Slice(4, 4));
            var padded = (long)size + (size & 1);
            if (consumed + padded > declaredEnd + 1) throw new InvalidDataException("WebP 청크가 RIFF 범위를 넘는다.");
            if (fourCc is "EXIF" or "XMP ")
            {
                Skip(input, padded);
            }
            else if (fourCc == "VP8X")
            {
                if (size < 10) throw new InvalidDataException("VP8X 길이가 잘못됐다.");
                output.Write(chunk);
                output.WriteByte((byte)(ReadByte(input) & ~0x0C)); // EXIF·XMP 플래그 해제
                CopyExact(input, output, padded - 1);
            }
            else
            {
                sawImage |= fourCc is "VP8 " or "VP8L" or "ANMF";
                output.Write(chunk);
                CopyExact(input, output, padded);
            }
            consumed += padded;
        }
        if (!sawImage) throw new InvalidDataException("WebP에 화상 청크가 없다.");
        var end = output.Position;
        Span<byte> sizeBytes = stackalloc byte[4]; // RIFF 크기 필드 재기록용 4바이트 스택 버퍼
        BinaryPrimitives.WriteUInt32LittleEndian(sizeBytes, checked((uint)(end - start - 8)));
        output.Position = start + 4;
        output.Write(sizeBytes);
        output.Position = end;
    }

    // GIF: 헤더(6) + 논리 화면 기술자(7) [+ 전역 색상표], 이어서 블록 = 0x21 확장 | 0x2C 이미지 | 0x3B 트레일러.
    private static void StripGif(Stream input, Stream output)
    {
        Span<byte> header = stackalloc byte[13]; // GIF 헤더(6) + 논리 화면 기술자(7) 고정 크기
        ReadExact(input, header);
        output.Write(header);
        if ((header[10] & 0x80) != 0) CopyExact(input, output, 3L << ((header[10] & 0x07) + 1));
        Span<byte> application = stackalloc byte[11]; // 애플리케이션 식별자 + 인증 코드 고정 11바이트
        Span<byte> descriptor = stackalloc byte[9]; // 이미지 기술자 고정 9바이트
        var sawImage = false;
        while (true)
        {
            var introducer = ReadByte(input);
            if (introducer == 0x3B)
            {
                if (!sawImage) throw new InvalidDataException("GIF에 이미지가 없다.");
                output.WriteByte(0x3B);
                return; // 트레일러 뒤에 덧붙은 바이트는 버린다
            }
            if (introducer == 0x2C)
            {
                sawImage = true;
                output.WriteByte(0x2C);
                ReadExact(input, descriptor);
                output.Write(descriptor);
                if ((descriptor[8] & 0x80) != 0) CopyExact(input, output, 3L << ((descriptor[8] & 0x07) + 1));
                output.WriteByte(ReadByte(input)); // LZW 최소 코드 크기
                CopySubBlocks(input, output, keep: true);
                continue;
            }
            if (introducer != 0x21) throw new InvalidDataException("GIF 블록 구분자가 잘못됐다.");
            var label = ReadByte(input);
            if (label == 0xFE) { CopySubBlocks(input, output, keep: false); continue; } // 주석 확장
            if (label == 0xFF)
            {
                if (ReadByte(input) != 11) throw new InvalidDataException("GIF 애플리케이션 확장 길이가 잘못됐다.");
                ReadExact(input, application);
                var id = Encoding.ASCII.GetString(application);
                var keep = id is "NETSCAPE2.0" or "ANIMEXTS1.0"; // 반복 횟수만 남긴다. XMP 등 다른 애플리케이션 데이터는 버린다
                if (keep) { output.WriteByte(0x21); output.WriteByte(0xFF); output.WriteByte(11); output.Write(application); }
                CopySubBlocks(input, output, keep);
                continue;
            }
            output.WriteByte(0x21); output.WriteByte(label); // 그래픽 제어(0xF9)·일반 텍스트(0x01)
            CopySubBlocks(input, output, keep: true);
        }
    }

    private static void CopySubBlocks(Stream input, Stream output, bool keep)
    {
        while (true)
        {
            var size = ReadByte(input);
            if (keep) output.WriteByte(size);
            if (size == 0) return;
            if (keep) CopyExact(input, output, size); else Skip(input, size);
        }
    }

    private static byte ReadByte(Stream stream)
    {
        var value = stream.ReadByte();
        return value < 0 ? throw new InvalidDataException("파일이 예기치 않게 끝났다.") : (byte)value;
    }

    private static void ReadExact(Stream stream, Span<byte> buffer)
    {
        if (stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) != buffer.Length) throw new InvalidDataException("파일이 예기치 않게 끝났다.");
    }

    private static void Skip(Stream stream, long count)
    {
        if (stream.CanSeek)
        {
            if (stream.Position + count > stream.Length) throw new InvalidDataException("파일이 예기치 않게 끝났다.");
            stream.Seek(count, SeekOrigin.Current);
            return;
        }
        CopyExact(stream, Stream.Null, count);
    }

    private static void CopyExact(Stream input, Stream output, long count)
    {
        // ArrayPool<byte>.Shared: 스레드별 캐시(TLS 슬롯)를 먼저 확인하는 버킷 풀이라 같은 스레드에서 빌리고 돌려주면 힙 할당이 없다.
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            while (count > 0)
            {
                var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                if (read <= 0) throw new InvalidDataException("파일이 예기치 않게 끝났다.");
                output.Write(buffer, 0, read);
                count -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
