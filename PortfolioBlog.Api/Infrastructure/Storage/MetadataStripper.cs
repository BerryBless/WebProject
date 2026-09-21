using System.Buffers;
using System.Buffers.Binary;

namespace PortfolioBlog.Api.Infrastructure.Storage;

/// <summary>이미지를 <b>디코딩하지 않고</b> 컨테이너 구조만 따라가며 메타데이터(EXIF·GPS·XMP·IPTC·주석·텍스트)를 버린다. 네 형식 모두 허용 목록 기반(allow-by-default-DENY)이다 — 알아보지 못하는 세그먼트·청크·확장 블록은 남기지 않고 버리거나 거부한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 상태는 호출이 넘긴 스트림뿐이다(스트림 자체는 호출자가 독점해야 한다).</description></item>
/// <item><description><b>Memory Allocation:</b> 파일 크기·청크 개수와 무관하게 64KB 풀 버퍼 하나 + 스택 버퍼(헤더·FourCC·식별자 비교는 전부 <c>stackalloc</c> + <see cref="ReadOnlySpan{T}"/> 비교, 문자열 할당 없음). 선언된 길이만큼 미리 할당하지 않으므로 "길이를 속인 청크"로 메모리를 부풀릴 수 없다.</description></item>
/// <item><description><b>Blocking:</b> 동기 스트림 I/O. 자세한 조건은 <see cref="Strip"/>의 Blocking 항목 참조.</description></item>
/// </list>
/// 디코더를 쓰지 않는 이유: 이미지 디코더는 그 자체가 큰 공격 표면이고, 재인코딩은 화질을 바꾼다. 컨테이너 파싱은 "허용 목록에 있는 블록만 길이만큼 복사하고 나머지는 건너뛰거나 거부"가 기본이지만,
/// GIF의 그래픽 제어 확장과 반복 확장은 그 "복사"가 아니다 — 서브블록 모양을 먼저 검증한 뒤 스택 버퍼에 담아 둔 값으로 다시 조립해서 쓴다(<see cref="StripGif"/> 참조).
/// 출력이 멱등이라(깨끗한 파일을 다시 넣으면 같은 바이트) 그 SHA-256을 저장 경로로 쓸 수 있다.
/// 구조가 어긋나거나 허용 목록 밖의 블록을 만나면 <see cref="InvalidDataException"/> — 호출부는 이를 "지원하지 않는 이미지"(415)로 바꾼다.
/// <para><b>남는 표면(residual, fix round 2):</b> 이 컴포넌트는 디코딩하지 않으므로 다음은 검사하지 않는다 —
/// (1) ICC 프로파일 바이트: JPEG <c>ICC_PROFILE</c> APP2 세그먼트(세그먼트당 최대 65,521바이트, 개수 제한 없음) · PNG <c>iCCP</c> · WebP <c>ICCP</c>
/// (측정: 임의 바이트 5MB가 JPEG의 APP2 세그먼트 80개에 실려도 그 JPEG은 여전히 디코딩된다);
/// (2) WebP <c>ANMF</c> 프레임의 페이로드; (3) JPEG <c>DQT</c>/<c>DHT</c>/<c>SOF</c> 페이로드(여기에 임의 바이트를 넣으면 파일이 디코딩되지 않으므로
/// 공격에 쓸모 있는 통로가 아니다); (4) PNG CRC는 복사만 하고 검증하지 않는다;
/// (5) GIF 화상 데이터의 LZW 서브블록 체인(픽셀 데이터)은 길이만큼 그대로 복사한다 — LZW 종료 코드 뒤에 덧붙인 서브블록도
/// 같은 서브블록 열의 일부로 보여 구분할 수 없다(측정: 여전히 재생되는 GIF 안에서 5,242,880바이트가 이렇게 살아남았다).
/// LZW 디코더 없이는 "여기서부터 진짜 픽셀이 아니다"를 판정할 수 없다.
/// 실제로 이들을 막는 것은 이 컴포넌트가 아니라 업로드 크기 상한·시그니처로 정한 Content-Type·<c>X-Content-Type-Options: nosniff</c>(Task 5) —
/// 그래서 이 잔여 바이트들은 브라우저에서 실행될 수 없다.</para>
/// </remarks>
public static class MetadataStripper
{
    private const int CopyBufferSize = 64 * 1024; // 85,000바이트 미만: LOH에 올라가지 않는다

    /// <summary>이미지 컨테이너 구조를 처음부터 끝까지 읽으며, 허용 목록에 없는 세그먼트·청크·확장 블록은 전부 버리거나 거부하고(allow-by-default-DENY) 화상 데이터만 <paramref name="output"/>에 다시 쓴다.</summary>
    /// <param name="kind">이미 <see cref="ImageSignature.Detect"/>로 판정한 형식. 이 값이 실제 바이트와 다르면 결국 <see cref="InvalidDataException"/>이 나지만, 그 전에 이미 일부 kept 블록을 <paramref name="output"/>에 썼을 수 있다("즉시" 실패를 보장하지 않는다 — 아래 예외 항목 참조).</param>
    /// <param name="input">현재 위치부터 읽는 원본 스트림. 소유권은 호출자에게 있으며 이 메서드는 닫거나 되감지 않는다.</param>
    /// <param name="output">결과를 쓰는 스트림. 반드시 seek 가능해야 한다(WebP는 다 쓴 뒤 RIFF 크기 필드를 되돌아가 다시 쓴다). 이 검사는 <see cref="Strip"/> 진입점에서 형식과 무관하게 한 번만 하므로 네 형식 모두 같은 방식으로 실패한다. 소유권은 호출자에게 있으며 이 메서드는 닫지 않는다.</param>
    /// <exception cref="ArgumentException"><paramref name="output"/>이 seek 불가능할 때. <c>ParamName</c>은 항상 <c>"output"</c>이다.</exception>
    /// <exception cref="InvalidDataException">구조가 손상됐거나 파일이 잘렸거나 길이 필드가 남은 범위를 벗어나거나 허용 목록에 없는 마커/블록을 만났거나 고정 크기 블록의 길이가 규격과 다를 때. 이 시점에 <paramref name="output"/>에는 그때까지 남긴 블록이 이미 부분적으로 쓰여 있을 수 있다 — 호출부는 예외를 받으면 <paramref name="output"/>의 내용을 버려야 한다(부분 결과를 저장하면 안 된다).</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/>가 <see cref="ImageKind"/>의 정의된 값이 아닐 때.</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe(공유 가변 상태 없음). 단, 같은 <paramref name="input"/>/<paramref name="output"/> 인스턴스를 여러 스레드에서 동시에 넘기면 스트림 자체가 스레드 안전하지 않으므로 호출자가 직렬화해야 한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="ArrayPool{T}.Shared"/>에서 64KB 버퍼 하나를 빌려 재사용하고, 헤더·FourCC·식별자 비교는 전부 스택(<c>stackalloc</c>) 버퍼와 <see cref="ReadOnlySpan{T}"/> 비교라 청크·세그먼트 개수에 비례한 힙 할당이 없다(50,000개 청크로 측정: 256KB 미만 — 이전에는 청크마다 <c>string</c>을 할당해 5MB/437,000청크 PNG에서 13.3MB가 나갔다). <paramref name="input"/>과 <paramref name="output"/>의 소유권은 호출 끝까지 호출자에게 남고, 이 메서드는 둘 다 dispose하지 않는다. <paramref name="input"/>은 순방향으로만 읽으므로 seek 불가능해도 되지만 <paramref name="output"/>은 항상 seek 가능해야 한다.</description></item>
    /// <item><description><b>Blocking:</b> 동기 <see cref="Stream.Read(byte[],int,int)"/>/<see cref="Stream.Write(byte[],int,int)"/>·<see cref="Stream.ReadByte"/>/<see cref="Stream.WriteByte(byte)"/> 호출로 이뤄진다(비동기 오버로드 없음). 요청 본문 스트림에 직접 걸지 말고 크기를 이미 제한한 임시 파일 스트림이나 <see cref="MemoryStream"/>/<see cref="BufferedStream"/>에 대해서만 호출한다 — 별도 백그라운드 스레드가 꼭 필요하지는 않다(5MB 입력 기준 약 33ms 동기 호출). 단, <paramref name="input"/>/<paramref name="output"/>은 <see cref="Stream.ReadByte"/>/<see cref="Stream.WriteByte(byte)"/>를 효율적으로 오버라이드해야 한다 — JPEG 엔트로피 구간은 바이트 단위로 읽고 쓰므로, 기반 클래스 구현을 그대로 쓰는 스트림(호출마다 1바이트짜리 배열을 새로 할당)에 걸면 5MB JPEG 하나에서 344MB가 할당된다(측정값). <see cref="FileStream"/>·<see cref="MemoryStream"/>은 이 두 메서드를 오버라이드하므로 안전하다.</description></item>
    /// </list>
    /// </remarks>
    public static void Strip(ImageKind kind, Stream input, Stream output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanSeek) throw new ArgumentException("출력 스트림은 seek 가능해야 한다.", nameof(output));
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
    //
    // 세그먼트 수준은 허용 목록 기반이다(수정 전에는 "몇 개만 버리고 나머지는 통과"였는데, 그 "나머지"에 JFXX 썸네일·임의 APP2·
    // 예약 마커가 숨을 수 있었다 — fix round 1에서 지적됨):
    //   - 구조(그대로 유지): C0~CF(SOFn·DHT 0xC4·DAC 0xCC), DB(DQT), DC(DNL), DD(DRI), DA(SOS, 이후 엔트로피 구간은 그대로).
    //   - APPn(E0~EF)·COM(FE): 원칙적으로 전부 버린다. 예외 3가지만 페이로드 시작 바이트로 식별해 남긴다 —
    //       APP0이 "JFIF\0"로 시작하면 남긴다(밀도·화면비 정보가 없으면 표시 크기가 틀어진다). 식별자가 다른 APP0(JFXX 썸네일 등)은 버린다.
    //       APP2가 "ICC_PROFILE\0"으로 시작하면 남긴다(색 프로파일이 없으면 색이 달라진다). 식별자가 다른 APP2(MPF·FlashPix·임의 바이트 등)는 버린다.
    //       APP14(EE)가 "Adobe"로 시작하면 남긴다(CMYK/YCCK 색 변환 플래그가 없으면 색이 깨진다).
    //   - 그 밖의 모든 마커(00, 01, 02~BF, D0~D8, DE, DF, F0~FD)는 세그먼트 수준에 나타나면 거부한다 — 길이 없는 마커(RSTn·TEM)와
    //     채움용 FF00은 엔트로피 부호화 데이터 안에서만 유효하며 그 안에서는 CopyJpegEntropyData가 그대로 처리한다.
    private static void StripJpeg(Stream input, Stream output)
    {
        Span<byte> b = stackalloc byte[2]; // 마커·길이 필드를 힙 할당 없이 읽기 위한 2바이트 스택 버퍼(루프 밖에서 한 번만 할당해 재사용)
        Span<byte> identifierBuffer = stackalloc byte[12]; // 유지 대상 APP 식별자 중 가장 긴 것(ICC_PROFILE\0)까지 담는 고정 버퍼. 루프 안에서 반복 stackalloc하면 스택이 계속 자라므로 밖에서 한 번만 할당한다.
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

            var structural = marker is (>= 0xC0 and <= 0xCF) or 0xDB or 0xDC or 0xDD or 0xDA;
            var appOrComment = marker is (>= 0xE0 and <= 0xEF) or 0xFE;
            if (!structural && !appOrComment) throw new InvalidDataException("지원하지 않는 JPEG 마커다.");

            ReadExact(input, b);
            var length = BinaryPrimitives.ReadUInt16BigEndian(b);
            if (length < 2) throw new InvalidDataException("JPEG 세그먼트 길이가 잘못됐다.");
            var payload = length - 2;

            if (appOrComment)
            {
                var identifier = GetKeptAppIdentifier(marker);
                if (!identifier.IsEmpty && payload >= identifier.Length)
                {
                    var idSlice = identifierBuffer[..identifier.Length];
                    ReadExact(input, idSlice);
                    if (idSlice.SequenceEqual(identifier))
                    {
                        output.WriteByte(0xFF); output.WriteByte(marker); output.Write(b); output.Write(idSlice);
                        CopyExact(input, output, payload - identifier.Length);
                    }
                    else
                    {
                        Skip(input, payload - identifier.Length);
                    }
                }
                else
                {
                    Skip(input, payload);
                }
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

    // 남기는 APPn 식별자: JFIF(APP0), ICC_PROFILE(APP2), Adobe(APP14). 그 밖의 APPn·COM은 식별자를 확인하지 않고 버린다.
    private static ReadOnlySpan<byte> GetKeptAppIdentifier(byte marker) => marker switch
    {
        0xE0 => "JFIF\0"u8,
        0xE2 => "ICC_PROFILE\0"u8,
        0xEE => "Adobe"u8,
        _ => default,
    };

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
    // 남기는 청크: 화상·팔레트·투명도·색 공간·물리 해상도·APNG(IsPngKeptChunk 참조). 그 밖의 보조 청크(eXIf, tEXt, zTXt, iTXt, tIME, sPLT, 알 수 없는 것)는 버린다.
    // sPLT(제안 팔레트)는 자유 텍스트 팔레트 이름을 담고 어떤 디코더도 필수로 요구하지 않아 fix round 1에서 허용 목록에서 뺐다.
    // 첫 청크가 IHDR가 아니거나 IEND 전에 IDAT이 한 번도 없으면 거부한다 — 이전에는 "시그니처+IEND"만으로 된 빈 PNG도 통과했다.
    // fix round 2: 허용 목록에 있다고 선언한 길이 그대로 복사하면(예: 1MB gAMA, 중복 IHDR) 구조는 "허용된 청크"지만 규격 밖이다.
    // ValidatePngChunkSize로 고정/상한 크기를 검사하고, IHDR은 정확히 한 번만 나타나야 한다.
    // 잔여 위험(고치지 않음, 이번 라운드 범위 밖): 남기는 iCCP 청크 안의 최대 79바이트 Latin-1 프로파일 이름은 공격자가 채운 임의 텍스트다.
    private static void StripPng(Stream input, Stream output)
    {
        Span<byte> signature = stackalloc byte[8]; // PNG 매직 넘버 고정 크기 — 스택에 두면 힙 할당·GC 압력 없음
        ReadExact(input, signature);
        output.Write(signature);
        Span<byte> header = stackalloc byte[8]; // 청크 길이(4) + 종류(4) 고정 크기 헤더
        var sawIdat = false;
        var sawIhdr = false;
        var first = true;
        while (true)
        {
            ReadExact(input, header);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            if (length > int.MaxValue) throw new InvalidDataException("PNG 청크 길이가 잘못됐다.");
            var type = header.Slice(4, 4);
            if (first)
            {
                if (!type.SequenceEqual("IHDR"u8)) throw new InvalidDataException("PNG은 IHDR로 시작해야 한다.");
                first = false;
            }
            if (type.SequenceEqual("IHDR"u8))
            {
                if (sawIhdr) throw new InvalidDataException("PNG에 IHDR이 두 번 나타났다.");
                sawIhdr = true;
            }
            if (type.SequenceEqual("IDAT"u8)) sawIdat = true;
            var total = (long)length + 4; // 데이터 + CRC
            if (IsPngKeptChunk(type))
            {
                ValidatePngChunkSize(type, length);
                output.Write(header);
                CopyExact(input, output, total);
            }
            else Skip(input, total);
            if (type.SequenceEqual("IEND"u8))
            {
                if (!sawIdat) throw new InvalidDataException("PNG에 IDAT이 없다.");
                return; // IEND 뒤에 덧붙은 바이트는 버린다
            }
        }
    }

    // FourCC를 문자열로 바꾸지 않고 스팬끼리 직접 비교한다 — Encoding.GetString은 청크마다 새 string을 할당해
    // 청크 개수에 비례하는 힙 압력을 만든다(fix round 1에서 측정: 5MB PNG의 437,000개 청크 → 13.3MB 할당).
    private static bool IsPngKeptChunk(ReadOnlySpan<byte> type) =>
        type.SequenceEqual("IHDR"u8) || type.SequenceEqual("PLTE"u8) || type.SequenceEqual("IDAT"u8) ||
        type.SequenceEqual("IEND"u8) || type.SequenceEqual("tRNS"u8) || type.SequenceEqual("gAMA"u8) ||
        type.SequenceEqual("cHRM"u8) || type.SequenceEqual("sRGB"u8) || type.SequenceEqual("iCCP"u8) ||
        type.SequenceEqual("sBIT"u8) || type.SequenceEqual("bKGD"u8) || type.SequenceEqual("pHYs"u8) ||
        type.SequenceEqual("hIST"u8) || type.SequenceEqual("acTL"u8) || type.SequenceEqual("fcTL"u8) ||
        type.SequenceEqual("fdAT"u8) || type.SequenceEqual("cICP"u8) || type.SequenceEqual("mDCv"u8) ||
        type.SequenceEqual("cLLi"u8);

    // 고정/상한 크기 청크의 선언 길이를 검사한다(값 비교뿐이라 할당 없음). IDAT·fdAT·iCCP는 압축 화상 데이터·색 프로파일이라
    // 태생적으로 크기 제한이 없어 검사하지 않는다(허용 목록에 있다는 사실만으로는 "규격에 맞는 크기"를 보장하지 않는다 — fix round 2).
    private static void ValidatePngChunkSize(ReadOnlySpan<byte> type, uint length)
    {
        if (type.SequenceEqual("IHDR"u8) && length != 13) ThrowWrongSize();
        if (type.SequenceEqual("gAMA"u8) && length != 4) ThrowWrongSize();
        if (type.SequenceEqual("cHRM"u8) && length != 32) ThrowWrongSize();
        if (type.SequenceEqual("sRGB"u8) && length != 1) ThrowWrongSize();
        if (type.SequenceEqual("pHYs"u8) && length != 9) ThrowWrongSize();
        if (type.SequenceEqual("cICP"u8) && length != 4) ThrowWrongSize();
        if (type.SequenceEqual("mDCv"u8) && length != 24) ThrowWrongSize();
        if (type.SequenceEqual("cLLi"u8) && length != 8) ThrowWrongSize();
        if (type.SequenceEqual("acTL"u8) && length != 8) ThrowWrongSize();
        if (type.SequenceEqual("fcTL"u8) && length != 26) ThrowWrongSize();
        if (type.SequenceEqual("IEND"u8) && length != 0) ThrowWrongSize();
        if (type.SequenceEqual("PLTE"u8) && (length < 3 || length > 768 || length % 3 != 0)) ThrowWrongSize();
        if (type.SequenceEqual("tRNS"u8) && length > 256) ThrowWrongSize();
        if (type.SequenceEqual("hIST"u8) && (length > 512 || length % 2 != 0)) ThrowWrongSize();
        if (type.SequenceEqual("sBIT"u8) && length > 4) ThrowWrongSize();
        if (type.SequenceEqual("bKGD"u8) && length > 6) ThrowWrongSize();

        static void ThrowWrongSize() => throw new InvalidDataException("PNG 청크 크기가 규격과 다르다.");
    }

    // WebP: "RIFF" + 크기(4, 리틀엔디언) + "WEBP", 이어서 청크 = FourCC(4) + 크기(4) + 데이터(+홀수면 패딩 1). VP8X 플래그: 0x08 EXIF, 0x04 XMP.
    // 허용 목록: VP8X(정확히 10바이트일 때만 — 그 이상은 알려지지 않은 확장 필드를 실어 나를 수 있어 거부한다)·VP8 ·VP8L·ALPH·
    // ANIM(정확히 6바이트, fix round 2)·ANMF·ICCP. 그 밖(EXIF·XMP ·JUNK·미지 FourCC)은 전부 버린다.
    // 잔여 위험(고치지 않음, 이번 라운드 범위 밖): ANMF 프레임의 페이로드는 통째로 복사하므로 프레임 안에 숨긴 서브청크까지는 들여다보지 않는다.
    private static void StripWebP(Stream input, Stream output)
    {
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
            var fourCc = chunk[..4];
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk.Slice(4, 4));
            var padded = (long)size + (size & 1);
            if (consumed + padded > declaredEnd + 1) throw new InvalidDataException("WebP 청크가 RIFF 범위를 넘는다.");
            if (fourCc.SequenceEqual("VP8X"u8))
            {
                if (size != 10) throw new InvalidDataException("VP8X 길이가 잘못됐다.");
                output.Write(chunk);
                output.WriteByte((byte)(ReadByte(input) & ~0x0C)); // EXIF·XMP 플래그 해제
                CopyExact(input, output, padded - 1);
            }
            else if (IsWebPKeptOtherChunk(fourCc))
            {
                if (fourCc.SequenceEqual("ANIM"u8) && size != 6) throw new InvalidDataException("ANIM 길이가 잘못됐다."); // fix round 2: VP8X처럼 고정 6바이트다
                sawImage |= fourCc.SequenceEqual("VP8 "u8) || fourCc.SequenceEqual("VP8L"u8) || fourCc.SequenceEqual("ANMF"u8);
                output.Write(chunk);
                CopyExact(input, output, padded);
            }
            else
            {
                Skip(input, padded);
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

    private static bool IsWebPKeptOtherChunk(ReadOnlySpan<byte> fourCc) =>
        fourCc.SequenceEqual("VP8 "u8) || fourCc.SequenceEqual("VP8L"u8) || fourCc.SequenceEqual("ALPH"u8) ||
        fourCc.SequenceEqual("ANIM"u8) || fourCc.SequenceEqual("ANMF"u8) || fourCc.SequenceEqual("ICCP"u8);

    // GIF: 헤더(6) + 논리 화면 기술자(7) [+ 전역 색상표], 이어서 블록 = 0x21 확장 | 0x2C 이미지 | 0x3B 트레일러.
    // 확장 라벨 허용 목록: 그래픽 제어(0xF9)는 모양을 검증한 뒤(크기 바이트 4, 데이터 4바이트, 종료 바이트) 남긴다 — 아니면 거부한다.
    // 애플리케이션 확장(0xFF)은 식별자가 NETSCAPE2.0·ANIMEXTS1.0일 때만 반복 횟수 서브블록을 남긴다. 그 밖의 모든 라벨(주석 0xFE, 일반 텍스트 0x01, 예약·사설 라벨 0x00·0x02~0xF8 등)은 서브블록에
    // 임의 바이트를 담을 수 있으므로 통째로 버린다 — fix round 1 전에는 주석(0xFE)만 버려서, 같은 페이로드를 라벨만 0x01·0x42·0x00으로
    // 바꾸면 5MB까지도 그대로 살아남았다(재생 가능한 2프레임 GIF 안에서 확인됨).
    //
    // fix round 2: "허용 목록에 있다"(라벨이 F9 또는 id가 NETSCAPE2.0/ANIMEXTS1.0)는 것만으로는 부족했다 — kept로 판정한 블록도
    // 서브블록 열은 0x00 종료 바이트까지 몇 개든 이어 붙을 수 있어, 그 서브블록 "개수"(SHAPE)를 검사하지 않으면 그래픽 제어 뒤나
    // 반복 확장 뒤에 임의 크기(최대 5,242,880바이트까지 확인됨)의 추가 서브블록이 그대로 살아남았다. 그래서:
    //   - 그래픽 제어(0xF9)는 GIF89a 규격대로 서브블록이 정확히 하나, 크기 4바이트여야 한다. 그 외(크기≠4, 또는 4바이트 뒤에
    //     종료 바이트가 아닌 것)는 거부한다.
    //   - 애플리케이션 확장(0xFF)은 서브블록 열 전체를 훑어 "크기 3 + 첫 바이트 0x01"인 반복 횟수 서브블록(03 01 LL LL) 중
    //     맨 처음 찾은 하나만 남기고, 그 뒤에 나온 같은 모양의 중복과 NETSCAPE 버퍼링 서브블록(05 02 …)을 포함한 나머지는 전부 버린다.
    //     반복 횟수 서브블록이 하나도 없으면 id를 포함해 확장 전체를 버린다(먼저 스택 버퍼에 후보를 담아 두고, 찾았을 때만 21 FF 0B <id> 헤더를 쓴다).
    private static void StripGif(Stream input, Stream output)
    {
        Span<byte> header = stackalloc byte[13]; // GIF 헤더(6) + 논리 화면 기술자(7) 고정 크기
        ReadExact(input, header);
        output.Write(header);
        if ((header[10] & 0x80) != 0) CopyExact(input, output, 3L << ((header[10] & 0x07) + 1));
        Span<byte> application = stackalloc byte[11]; // 애플리케이션 식별자 + 인증 코드 고정 11바이트
        Span<byte> descriptor = stackalloc byte[9]; // 이미지 기술자 고정 9바이트
        Span<byte> graphicControl = stackalloc byte[4]; // 그래픽 제어 확장은 GIF89a 규격상 항상 4바이트 고정
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
            if (label == 0xFF)
            {
                if (ReadByte(input) != 11) throw new InvalidDataException("GIF 애플리케이션 확장 길이가 잘못됐다.");
                ReadExact(input, application);
                var isLoopCandidate = application.SequenceEqual("NETSCAPE2.0"u8) || application.SequenceEqual("ANIMEXTS1.0"u8);
                CopyGifLoopExtension(input, output, application, isLoopCandidate);
                continue;
            }
            if (label == 0xF9)
            {
                if (ReadByte(input) != 4) throw new InvalidDataException("GIF 그래픽 제어 확장 형식이 잘못됐다.");
                ReadExact(input, graphicControl);
                if (ReadByte(input) != 0x00) throw new InvalidDataException("GIF 그래픽 제어 확장 형식이 잘못됐다.");
                output.WriteByte(0x21); output.WriteByte(0xF9); output.WriteByte(0x04);
                output.Write(graphicControl);
                output.WriteByte(0x00);
                continue;
            }
            CopySubBlocks(input, output, keep: false); // 그 밖의 모든 라벨(주석 0xFE, 일반 텍스트 0x01, 예약·사설 라벨)은 통째로 버린다
        }
    }

    // 애플리케이션 확장(0xFF)의 서브블록 열을 전부 훑어 "크기 3 + 첫 바이트 0x01"인 첫 반복 횟수 서브블록만 후보 버퍼에 담아 두고,
    // 그 밖의 서브블록(큰 페이로드, NETSCAPE 버퍼링 서브블록 05 02 …, 중복된 반복 횟수 서브블록)은 전부 버린다.
    // 후보를 찾지 못했거나 id가 애초에 허용 목록 밖이면 21 FF 0B <id> 헤더 자체를 쓰지 않는다 — 그래서 스택 버퍼에 먼저
    // 담아 두고 입력을 끝까지 읽은 뒤에야 무엇을 쓸지 결정한다.
    private static void CopyGifLoopExtension(Stream input, Stream output, ReadOnlySpan<byte> applicationId, bool isLoopCandidate)
    {
        Span<byte> loopCount = stackalloc byte[3]; // 반복 횟수 서브블록 데이터(플래그 1 + 횟수 2) 후보 — 루프 밖에서 한 번만 할당해 재사용
        var found = false;
        while (true)
        {
            var size = ReadByte(input);
            if (size == 0) break;
            if (isLoopCandidate && !found && size == 3)
            {
                ReadExact(input, loopCount);
                found = loopCount[0] == 0x01;
            }
            else
            {
                Skip(input, size);
            }
        }
        if (found)
        {
            output.WriteByte(0x21); output.WriteByte(0xFF); output.WriteByte(11);
            output.Write(applicationId);
            output.WriteByte(0x03);
            output.Write(loopCount);
            output.WriteByte(0x00);
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
