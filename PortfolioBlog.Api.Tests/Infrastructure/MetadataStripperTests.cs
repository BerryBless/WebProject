using System.Buffers.Binary;
using System.Text;
using PortfolioBlog.Api.Infrastructure.Storage;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>메타데이터 제거기 단위 테스트. 서버는 이미지를 디코딩하지 않으므로 "메타데이터 바이트가 사라졌는가"와 "컨테이너 구조가 온전한가"를 본다.
/// fix round 1부터는 허용 목록(allow-by-default-DENY) 위반 — 알려지지 않은 블록이 살아남는지 — 도 실제 파일을 바이트 스플라이싱해 검사한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 테스트마다 자체 <see cref="MemoryStream"/>을 쓴다. 공유 상태 없음.</description></item>
/// <item><description><b>Memory Allocation:</b> 픽스처는 각 5KB 미만. 할당 회귀 테스트(<see cref="Strip_AllocationDoesNotGrowWithChunkCount"/>)만 테스트 안에서 청크 5만 개짜리 PNG(약 600KB)를 만든다.</description></item>
/// <item><description><b>Blocking:</b> 동기 메모리 I/O만. Docker 불필요.</description></item>
/// </list>
/// 제거 후에도 <b>디코딩한 픽셀이 같고 프레임 수가 유지된다</b>는 사실은 계획 단계에서 PIL로 확인했다(<c>Fixtures/Images/make-fixtures.py</c>의 설명 참조).
/// 이 테스트 프로젝트에는 디코더가 없으므로 같은 사실을 구조 수준에서 고정한다.
/// </remarks>
public sealed class MetadataStripperTests
{
    // 픽스처별로 실제로 들어 있는 것으로 확인된 비밀만 나열한다(막연히 "일곱 개 중 하나"가 아니라 픽스처마다 전부 확인).
    private static readonly Dictionary<string, string[]> SecretsByFixture = new(StringComparer.Ordinal)
    {
        ["exif-gps.jpg"] = ["secret comment", "SpikeCam", "Exif"],
        ["exif-text.png"] = ["secret author", "Seoul", "SpikeCam"],
        ["exif-xmp.webp"] = ["SpikeCam", "xmpmeta"],
        ["comment-animated.gif"] = ["secret gif comment"],
        ["progressive-trailing.jpg"] = ["secret comment", "SpikeCam", "Exif", "secret trailing payload"],
    };

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images", name));

    private static byte[] Strip(byte[] data)
    {
        var kind = ImageSignature.Detect(data.AsSpan(0, Math.Min(ImageSignature.HeaderLength, data.Length)))!.Value;
        using var input = new MemoryStream(data, writable: false);
        using var output = new MemoryStream();
        MetadataStripper.Strip(kind, input, output);
        return output.ToArray();
    }

    private static bool Contains(byte[] haystack, string needle) => haystack.AsSpan().IndexOf(Encoding.ASCII.GetBytes(needle)) >= 0;

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts) { part.CopyTo(result, offset); offset += part.Length; }
        return result;
    }

    private static byte[] Splice(byte[] original, int offset, byte[] insert)
    {
        var result = new byte[original.Length + insert.Length];
        Array.Copy(original, 0, result, 0, offset);
        Array.Copy(insert, 0, result, offset, insert.Length);
        Array.Copy(original, offset, result, offset + insert.Length, original.Length - offset);
        return result;
    }

    /// <summary>픽스처마다 실제로 들어 있는 비밀이 원본에는 전부 있고, 제거 후에는 하나도 남지 않으며 파일은 더 작아지고 같은 형식으로 판정된다.</summary>
    [Theory]
    [InlineData("exif-gps.jpg")]
    [InlineData("exif-text.png")]
    [InlineData("exif-xmp.webp")]
    [InlineData("comment-animated.gif")]
    [InlineData("progressive-trailing.jpg")]
    public void Strip_RemovesEveryMetadataMarker(string fixture)
    {
        var secrets = SecretsByFixture[fixture];
        var original = Fixture(fixture);
        Assert.All(secrets, s => Assert.True(Contains(original, s), $"{fixture}: 전제 위반 — '{s}'가 원본에 없다")); // 전제: 원본에는 이 비밀들이 전부 있다

        var stripped = Strip(original);

        Assert.All(secrets, s => Assert.False(Contains(stripped, s), $"{fixture}: '{s}'가 남아 있다"));
        Assert.True(stripped.Length < original.Length);
        Assert.Equal(ImageSignature.Detect(original.AsSpan(0, 12)), ImageSignature.Detect(stripped.AsSpan(0, 12)));
    }

    /// <summary>이미 깨끗한 파일에 다시 적용해도 바이트가 같다(멱등) — 같은 이미지는 항상 같은 SHA-256, 같은 저장 경로가 된다.</summary>
    [Theory]
    [InlineData("exif-gps.jpg")]
    [InlineData("exif-text.png")]
    [InlineData("exif-xmp.webp")]
    [InlineData("comment-animated.gif")]
    [InlineData("progressive-trailing.jpg")]
    public void Strip_IsIdempotent(string fixture)
    {
        var once = Strip(Fixture(fixture));
        Assert.Equal(once, Strip(once));
    }

    /// <summary>JPEG: JFIF(APP0)와 화상 데이터는 그대로, EXIF(APP1)와 주석(COM)만 빠진다. 파일은 SOI로 시작해 EOI로 끝난다.</summary>
    [Fact]
    public void Strip_Jpeg_KeepsStructure()
    {
        var stripped = Strip(Fixture("exif-gps.jpg"));
        Assert.Equal([0xFF, 0xD8, 0xFF, 0xE0], stripped[..4]);
        Assert.True(Contains(stripped, "JFIF"));
        Assert.Equal([0xFF, 0xD9], stripped[^2..]);
        Assert.False(Contains(stripped, "Exif"));
    }

    /// <summary>프로그레시브 JPEG(스캔 여러 개)의 모든 스캔이 남고, EOI 뒤에 덧붙인 페이로드(ZIP 시그니처 + 문자열)는 버려진다 —
    /// SOS 이후를 "끝까지 그대로 복사"하면 통과해 버리는 폴리글랏·숨긴 데이터를 막는다.</summary>
    [Fact]
    public void Strip_Jpeg_Progressive_KeepsAllScans_AndDropsBytesAfterEoi()
    {
        var original = Fixture("progressive-trailing.jpg");
        var stripped = Strip(original);

        static int Count(byte[] data, byte marker)
        {
            var count = 0;
            for (var i = 0; i + 1 < data.Length; i++) if (data[i] == 0xFF && data[i + 1] == marker) count++;
            return count;
        }
        Assert.True(Count(original, 0xDA) > 1, "전제: 픽스처는 스캔이 여러 개인 프로그레시브 JPEG다");
        Assert.Equal(Count(original, 0xDA), Count(stripped, 0xDA));
        Assert.Equal([0xFF, 0xD9], stripped[^2..]);
        Assert.True(stripped.AsSpan().IndexOf<byte>([0x50, 0x4B, 0x03, 0x04]) < 0, "EOI 뒤의 ZIP 시그니처가 남아 있다");
    }

    // JPEG SOI(2) + 첫 세그먼트(마커 2 + 길이(2, 자기 자신 포함) + 페이로드)의 끝 위치. 모든 JPEG 픽스처가 SOI 다음에
    // 표준 APP0(JFIF) 세그먼트로 시작한다는 전제로 스플라이싱 지점을 계산한다(하드코딩 오프셋 대신 실제 바이트에서 읽는다).
    private static int JpegFirstSegmentEnd(byte[] jpeg)
    {
        var length = (jpeg[4] << 8) | jpeg[5];
        return 4 + length;
    }

    private static byte[] JpegSegment(byte marker, byte[] payload)
    {
        var segment = new byte[4 + payload.Length];
        segment[0] = 0xFF;
        segment[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2, 2), (ushort)(payload.Length + 2));
        payload.CopyTo(segment, 4);
        return segment;
    }

    /// <summary>APP0/APP2/APP14는 식별자가 정확히 맞을 때만 남는다 — 식별자가 다른 APP0(JFXX 썸네일), 임의 바이트의 APP2,
    /// 가짜 식별자의 APP14는 버려지고, 진짜 ICC_PROFILE APP2와 원래의 JFIF는 남는다.</summary>
    [Fact]
    public void Strip_Jpeg_KeepsOnlyIdentifiedAppSegments()
    {
        var original = Fixture("exif-gps.jpg");
        var jfxxThumbnail = JpegSegment(0xE0, Concat(Ascii("JFXX"), [0x00], Ascii("fake-thumbnail-secret-thumb")));
        var arbitraryApp2 = JpegSegment(0xE2, Ascii("<html>secret-app2</html>"));
        var fakeApp14 = JpegSegment(0xEE, Concat(Ascii("NotAd"), Ascii("secret-app14")));
        var genuineIcc = JpegSegment(0xE2, Concat(Ascii("ICC_PROFILE"), [0x00], [0x01, 0x01], Ascii("keep-icc")));
        var insertOffset = JpegFirstSegmentEnd(original);
        var spliced = Splice(original, insertOffset, Concat(jfxxThumbnail, arbitraryApp2, fakeApp14, genuineIcc));

        var stripped = Strip(spliced);

        Assert.False(Contains(stripped, "secret-thumb"));
        Assert.False(Contains(stripped, "secret-app2"));
        Assert.False(Contains(stripped, "secret-app14"));
        Assert.True(Contains(stripped, "keep-icc"));
        Assert.True(Contains(stripped, "JFIF"));
        Assert.Equal(stripped, Strip(stripped)); // 멱등
    }

    /// <summary>세그먼트 수준 허용 목록 밖의 마커(구조도 APPn/COM도 아닌 값)는 조용히 통과시키지 않고 거부한다.</summary>
    [Theory]
    [InlineData((byte)0x00)]
    [InlineData((byte)0x02)]
    [InlineData((byte)0xBF)]
    [InlineData((byte)0xD8)]
    [InlineData((byte)0xF0)]
    public void Strip_Jpeg_UnknownMarker_Throws(byte marker)
    {
        var original = Fixture("exif-gps.jpg");
        var insertOffset = JpegFirstSegmentEnd(original);
        var spliced = Splice(original, insertOffset, [0xFF, marker, 0x00, 0x04, 0x41, 0x41]);
        Assert.Throws<InvalidDataException>(() => Strip(spliced));
    }

    // PNG 청크 파싱(테스트 전용) — (형식, 파일 오프셋, 데이터 길이)의 목록을 돌려준다.
    private static List<(string Type, int Offset, int Length)> ParsePngChunks(byte[] png)
    {
        var chunks = new List<(string, int, int)>();
        var offset = 8;
        while (offset < png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
            var type = Encoding.ASCII.GetString(png, offset + 4, 4);
            chunks.Add((type, offset, length));
            offset += 12 + length;
            if (type == "IEND") break;
        }
        return chunks;
    }

    private static byte[] PngChunkBytes(byte[] png, (string Type, int Offset, int Length) chunk) => png[chunk.Offset..(chunk.Offset + 12 + chunk.Length)];

    private static byte[] PngChunk(string type, byte[] data)
    {
        var chunk = new byte[12 + data.Length]; // CRC(마지막 4바이트)는 파서가 검증하지 않으므로 0으로 둔다
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(0, 4), (uint)data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        return chunk;
    }

    /// <summary>PNG: 청크 열이 IHDR로 시작해 IEND로 끝나고, 남은 청크는 전부 허용 목록(스펠아웃, 프로덕션 상수를 참조하지 않음)에 있다.</summary>
    [Fact]
    public void Strip_Png_KeepsOnlyAllowlistedChunks()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "IHDR", "PLTE", "IDAT", "IEND", "tRNS", "gAMA", "cHRM", "sRGB", "iCCP", "sBIT", "bKGD", "pHYs", "hIST",
            "acTL", "fcTL", "fdAT", "cICP", "mDCv", "cLLi",
        };
        var stripped = Strip(Fixture("exif-text.png"));
        var types = new List<string>();
        for (var offset = 8; offset < stripped.Length;)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(stripped.AsSpan(offset, 4));
            types.Add(Encoding.ASCII.GetString(stripped, offset + 4, 4));
            offset += 12 + (int)length;
        }
        Assert.Equal("IHDR", types[0]);
        Assert.Equal("IEND", types[^1]);
        Assert.Contains("IDAT", types);
        Assert.All(types, t => Assert.Contains(t, allowed));
    }

    /// <summary>서명+IEND만 있는 "빈 PNG"(IHDR도 IDAT도 없음), IHDR가 첫 청크가 아닌 파일, IDAT이 하나도 없는 파일은 전부 거부된다.
    /// sPLT는 CRC를 검증하지 않으므로 아무 내용으로나 끼워 넣어도 허용 목록에서 빠져 버려진다.</summary>
    [Fact]
    public void Strip_Png_RequiresIhdrFirstAndIdat()
    {
        var original = Fixture("exif-text.png");
        var signature = original[..8];
        var chunks = ParsePngChunks(original);
        var ihdr = chunks.First(c => c.Type == "IHDR");
        var second = chunks[1];
        var iend = chunks.First(c => c.Type == "IEND");

        var signatureAndIendOnly = Concat(signature, PngChunkBytes(original, iend));
        Assert.Throws<InvalidDataException>(() => Strip(signatureAndIendOnly));

        var ihdrMovedAfterSecond = Concat(
            signature,
            PngChunkBytes(original, second),
            PngChunkBytes(original, ihdr),
            original[(second.Offset + 12 + second.Length)..]);
        Assert.Throws<InvalidDataException>(() => Strip(ihdrMovedAfterSecond));

        var withoutIdat = new List<byte>(signature);
        foreach (var c in chunks) if (c.Type != "IDAT") withoutIdat.AddRange(PngChunkBytes(original, c));
        Assert.Throws<InvalidDataException>(() => Strip(withoutIdat.ToArray()));

        var withSplt = Splice(original, ihdr.Offset + 12 + ihdr.Length, PngChunk("sPLT", Ascii("junk-palette-name")));
        var stripped = Strip(withSplt);
        var strippedTypes = ParsePngChunks(stripped).Select(c => c.Type).ToList();
        Assert.DoesNotContain("sPLT", strippedTypes);
    }

    /// <summary>WebP: RIFF 크기 필드가 실제 길이와 맞고, VP8X의 EXIF·XMP 플래그가 꺼져 있다(플래그만 남으면 디코더가 없는 청크를 찾는다).</summary>
    [Fact]
    public void Strip_WebP_RewritesRiffSize_AndClearsFlags()
    {
        var stripped = Strip(Fixture("exif-xmp.webp"));
        Assert.Equal((uint)(stripped.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(stripped.AsSpan(4, 4)));
        Assert.Equal("VP8X", Encoding.ASCII.GetString(stripped, 12, 4));
        Assert.Equal(0, stripped[20] & 0x0C);
        Assert.False(Contains(stripped, "EXIF"));
        Assert.False(Contains(stripped, "XMP "));
    }

    // (FourCC 4바이트 오프셋, 크기 필드 오프셋, 데이터 오프셋, 선언 크기) — offset 12(첫 청크)부터 순서대로 찾는다.
    private static (int HeaderOffset, int SizeFieldOffset, int DataOffset, int DeclaredSize) FindWebPChunk(byte[] webp, string fourCc)
    {
        var offset = 12;
        while (offset + 8 <= webp.Length)
        {
            var cc = Encoding.ASCII.GetString(webp, offset, 4);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(offset + 4, 4));
            if (cc == fourCc) return (offset, offset + 4, offset + 8, size);
            offset += 8 + size + (size % 2);
        }
        throw new InvalidOperationException($"{fourCc} 청크를 찾지 못했다.");
    }

    private static byte[] AppendWebPChunk(byte[] webp, string fourCc, byte[] payload)
    {
        var pad = payload.Length % 2 == 1 ? 1 : 0;
        var chunkBytes = new byte[8 + payload.Length + pad];
        Encoding.ASCII.GetBytes(fourCc).CopyTo(chunkBytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(chunkBytes.AsSpan(4, 4), (uint)payload.Length);
        payload.CopyTo(chunkBytes, 8);
        var result = Concat(webp, chunkBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), (uint)(result.Length - 8)); // RIFF 크기 필드를 새 전체 길이로 맞춘다
        return result;
    }

    /// <summary>허용 목록 밖의 FourCC(JUNK)는 버려지고, 남은 파일의 RIFF 크기 필드는 실제 길이와 맞는다.</summary>
    [Fact]
    public void Strip_WebP_DropsUnknownChunks()
    {
        var withJunk = AppendWebPChunk(Fixture("exif-xmp.webp"), "JUNK", Ascii("secret-junk"));

        var stripped = Strip(withJunk);

        Assert.False(Contains(stripped, "secret-junk"));
        Assert.Equal((uint)(stripped.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(stripped.AsSpan(4, 4)));
    }

    /// <summary>VP8X 청크는 정확히 10바이트여야 한다 — 12바이트(여분 2바이트 포함)로 부풀리면 알려지지 않은 확장 필드를 실어 나를 수 있으므로 거부한다.</summary>
    [Fact]
    public void Strip_WebP_OversizedVp8x_Throws()
    {
        var original = Fixture("exif-xmp.webp");
        var (_, sizeFieldOffset, dataOffset, declaredSize) = FindWebPChunk(original, "VP8X");
        Assert.Equal(10, declaredSize); // 전제: 픽스처의 VP8X는 표준 10바이트다

        var mutated = new List<byte>(original);
        mutated.InsertRange(dataOffset + declaredSize, new byte[] { 0x00, 0x00 }); // 페이로드 끝에 여분 2바이트 삽입
        var result = mutated.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(sizeFieldOffset, 4), 12); // 크기 필드를 12로 속인다
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), (uint)(result.Length - 8)); // RIFF 크기도 자기 일관되게 맞춘다

        Assert.Throws<InvalidDataException>(() => Strip(result));
    }

    // GIF 서브블록 열([크기][크기만큼 데이터])* [0]을 건너뛰고 그 다음 오프셋을 돌려준다. 그래픽 제어·주석·일반 텍스트·
    // 애플리케이션 확장이 전부 이 형태를 공유한다(라벨별로 다른 구조가 아니다).
    private static int SkipSubBlocks(byte[] data, int offset)
    {
        while (true)
        {
            var size = data[offset]; offset++;
            if (size == 0) return offset;
            offset += size;
        }
    }

    private static int GifBodyStart(byte[] gif)
    {
        var offset = 13;
        if ((gif[10] & 0x80) != 0) offset += 3 << ((gif[10] & 0x07) + 1);
        return offset;
    }

    // 첫 이미지 구분자(0x2C) 바로 앞의 오프셋 — 스플라이싱 지점. 그 앞의 확장 블록들(0x21 + 라벨 + 서브블록열)은 건너뛴다.
    private static int FirstImageDescriptorOffset(byte[] gif)
    {
        var offset = GifBodyStart(gif);
        while (gif[offset] != 0x2C)
        {
            offset += 2; // 0x21 + 라벨
            offset = SkipSubBlocks(gif, offset);
        }
        return offset;
    }

    // 원시 0x2C 바이트 개수가 아니라 실제 이미지 구분자 개수를 센다(픽셀 데이터에도 0x2C가 나타날 수 있으므로 구조를 따라가야 한다).
    private static int CountGifFrames(byte[] gif)
    {
        var offset = GifBodyStart(gif);
        var frames = 0;
        while (gif[offset] != 0x3B)
        {
            if (gif[offset] == 0x2C)
            {
                frames++;
                var packedOffset = offset + 9; // 0x2C(1) + 좌(2)+상(2)+너비(2)+높이(2) 다음이 packed
                var hasLocalTable = (gif[packedOffset] & 0x80) != 0;
                var localTableSizeField = gif[packedOffset] & 0x07;
                offset += 10; // 0x2C + 이미지 기술자(9)
                if (hasLocalTable) offset += 3 << (localTableSizeField + 1);
                offset++; // LZW 최소 코드 크기
                offset = SkipSubBlocks(gif, offset);
            }
            else
            {
                offset += 2; // 0x21 + 라벨
                offset = SkipSubBlocks(gif, offset);
            }
        }
        return frames;
    }

    private static byte[] GifExtension(byte label, byte[] payload)
    {
        var blocks = new List<byte> { 0x21, label };
        if (label == 0x01)
        {
            blocks.Add(12);
            blocks.AddRange(new byte[12]); // 일반 텍스트 헤더(그리드 위치 등) — 파서는 값을 해석하지 않는다
        }
        var offset = 0;
        while (offset < payload.Length)
        {
            var chunkSize = Math.Min(255, payload.Length - offset);
            blocks.Add((byte)chunkSize);
            blocks.AddRange(payload.Skip(offset).Take(chunkSize));
            offset += chunkSize;
        }
        blocks.Add(0x00);
        return blocks.ToArray();
    }

    /// <summary>GIF: 주석은 빠지고 애니메이션 반복 확장(NETSCAPE2.0)과 두 프레임은 남는다(원시 0x2C 바이트 수가 아니라
    /// 구조를 따라간 실제 이미지 구분자 개수로 확인). 트레일러로 끝난다.</summary>
    [Fact]
    public void Strip_Gif_KeepsAnimation()
    {
        var original = Fixture("comment-animated.gif");
        var stripped = Strip(original);
        Assert.True(Contains(stripped, "NETSCAPE2.0"));
        Assert.Equal(0x3B, stripped[^1]);
        Assert.Equal(CountGifFrames(original), CountGifFrames(stripped));
    }

    /// <summary>그래픽 제어(0xF9)를 뺀 나머지 확장 라벨(일반 텍스트 0x01, 예약 0x42·0x00)은 서브블록 페이로드를 담고 있어도 전부 버려진다.
    /// 프레임 수는 그대로고, 다시 적용해도 바이트가 같다.</summary>
    [Theory]
    [InlineData((byte)0x01)]
    [InlineData((byte)0x42)]
    [InlineData((byte)0x00)]
    public void Strip_Gif_DropsEveryExtensionExceptGraphicControl(byte label)
    {
        var original = Fixture("comment-animated.gif");
        var payload = Ascii("<script>alert(1)</script> secret-gif-payload");
        var insertOffset = FirstImageDescriptorOffset(original);
        var hostile = Splice(original, insertOffset, GifExtension(label, payload));

        var stripped = Strip(hostile);

        Assert.False(Contains(stripped, "secret-gif-payload"));
        Assert.Equal(CountGifFrames(original), CountGifFrames(stripped));
        Assert.Equal(stripped, Strip(stripped)); // 멱등
    }

    /// <summary>잘린 파일은 <see cref="InvalidDataException"/>으로 끝난다 — 무한 루프·범위 밖 읽기·다른 예외 없이.</summary>
    [Theory]
    [InlineData("exif-gps.jpg")]
    [InlineData("exif-text.png")]
    [InlineData("exif-xmp.webp")]
    [InlineData("comment-animated.gif")]
    public void Strip_Truncated_ThrowsInvalidData(string fixture)
    {
        var data = Fixture(fixture);
        foreach (var cut in new[] { 13, data.Length / 3, data.Length / 2 })
        {
            Assert.Throws<InvalidDataException>(() => Strip(data[..cut]));
        }
    }

    /// <summary>선언 길이가 터무니없는 세그먼트·청크는 메모리를 할당하지 않고 거부한다.</summary>
    [Fact]
    public void Strip_LyingLengthField_ThrowsInvalidData()
    {
        var png = Fixture("exif-text.png");
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8, 4), 0x7FFFFFF0); // IHDR 길이를 2GB로
        Assert.Throws<InvalidDataException>(() => Strip(png));
    }

    // Write만 지원하는 seek 불가능한 스트림 — 호출자가 요청 응답 스트림 등 seek 불가능한 대상을 실수로 넘기는 상황을 흉내 낸다.
    private sealed class NonSeekableWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
    }

    /// <summary>네 형식 모두 출력 스트림이 seek 불가능하면 같은 방식(ArgumentException, ParamName == "output")으로 실패한다.</summary>
    [Theory]
    [InlineData(ImageKind.Jpeg, "exif-gps.jpg")]
    [InlineData(ImageKind.Png, "exif-text.png")]
    [InlineData(ImageKind.WebP, "exif-xmp.webp")]
    [InlineData(ImageKind.Gif, "comment-animated.gif")]
    public void Strip_NonSeekableOutput_ThrowsArgumentException_ForEveryKind(ImageKind kind, string fixture)
    {
        using var input = new MemoryStream(Fixture(fixture), writable: false);
        using var output = new NonSeekableWriteStream();

        var ex = Assert.Throws<ArgumentException>(() => MetadataStripper.Strip(kind, input, output));

        Assert.Equal("output", ex.ParamName);
    }

    /// <summary>선언한 길이만큼 미리 할당하지 않는다는 Memory 주석이 사실인지, 청크 "개수"에 비례한 할당이 없는지를 직접 측정해 고정한다
    /// (수정 전에는 청크마다 <c>Encoding.GetString</c>이 새 문자열을 할당해 청크 수에 비례하는 힙 압력을 만들었다).</summary>
    [Fact]
    public void Strip_AllocationDoesNotGrowWithChunkCount()
    {
        var fixture = Fixture("exif-text.png");
        var chunks = ParsePngChunks(fixture);
        var ihdr = PngChunkBytes(fixture, chunks.First(c => c.Type == "IHDR"));
        var idat = PngChunkBytes(fixture, chunks.First(c => c.Type == "IDAT"));
        var iend = PngChunkBytes(fixture, chunks.First(c => c.Type == "IEND"));
        var unknownChunk = PngChunk("abCd", []); // 허용 목록 밖의 0바이트 보조 청크 — 5만 번 반복해도 내용은 없다

        var png = new List<byte>(fixture[..8]);
        png.AddRange(ihdr);
        png.AddRange(idat);
        for (var i = 0; i < 50_000; i++) png.AddRange(unknownChunk);
        png.AddRange(iend);
        var data = png.ToArray();

        using var input = new MemoryStream(data, writable: false);
        using var output = new MemoryStream(data.Length); // 미리 크기를 잡아 둬 측정 구간에서 배열 재할당이 없게 한다

        void RunOnce()
        {
            input.Position = 0;
            output.SetLength(0);
            output.Position = 0;
            MetadataStripper.Strip(ImageKind.Png, input, output);
        }

        RunOnce(); // 워밍업: ArrayPool 스레드-로컬 슬롯을 채워 둔다(이후 Rent가 새 배열을 할당하지 않도록)
        var before = GC.GetAllocatedBytesForCurrentThread();
        RunOnce();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 256 * 1024, $"청크 5만 개 처리에 {allocated}바이트가 할당됐다(청크 수에 비례하면 안 된다).");
    }
}
