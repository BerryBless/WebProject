using System.Buffers.Binary;
using System.Text;
using PortfolioBlog.Api.Infrastructure.Storage;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>메타데이터 제거기 단위 테스트. 서버는 이미지를 디코딩하지 않으므로 "메타데이터 바이트가 사라졌는가"와 "컨테이너 구조가 온전한가"를 본다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 테스트마다 자체 <see cref="MemoryStream"/>을 쓴다. 공유 상태 없음.</description></item>
/// <item><description><b>Memory Allocation:</b> 픽스처는 각 5KB 미만.</description></item>
/// <item><description><b>Blocking:</b> 동기 메모리 I/O만. Docker 불필요.</description></item>
/// </list>
/// 제거 후에도 <b>디코딩한 픽셀이 같고 프레임 수가 유지된다</b>는 사실은 계획 단계에서 PIL로 확인했다(<c>Fixtures/Images/make-fixtures.py</c>의 설명 참조).
/// 이 테스트 프로젝트에는 디코더가 없으므로 같은 사실을 구조 수준에서 고정한다.
/// </remarks>
public sealed class MetadataStripperTests
{
    private static readonly string[] Secrets = ["secret", "SpikeCam", "Exif", "xmpmeta", "Seoul", "Author", "Location"];

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

    /// <summary>픽스처에는 실제로 메타데이터가 들어 있고(테스트의 전제), 제거 후에는 하나도 남지 않으며 파일은 더 작아지고 같은 형식으로 판정된다.</summary>
    [Theory]
    [InlineData("exif-gps.jpg")]
    [InlineData("exif-text.png")]
    [InlineData("exif-xmp.webp")]
    [InlineData("comment-animated.gif")]
    [InlineData("progressive-trailing.jpg")]
    public void Strip_RemovesEveryMetadataMarker(string fixture)
    {
        var original = Fixture(fixture);
        Assert.Contains(Secrets, s => Contains(original, s)); // 전제: 원본에는 비밀이 있다

        var stripped = Strip(original);

        Assert.All(Secrets, s => Assert.False(Contains(stripped, s), $"{fixture}: '{s}'가 남아 있다"));
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

    /// <summary>PNG: 청크 열이 IHDR로 시작해 IEND로 끝나고, 남은 청크는 전부 허용 목록에 있다.</summary>
    [Fact]
    public void Strip_Png_KeepsOnlyAllowlistedChunks()
    {
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
        Assert.DoesNotContain(types, t => t is "eXIf" or "tEXt" or "iTXt" or "zTXt" or "tIME");
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

    /// <summary>GIF: 주석은 빠지고 애니메이션 반복 확장(NETSCAPE2.0)과 두 프레임(이미지 구분자 0x2C 두 번 이상)은 남는다. 트레일러로 끝난다.</summary>
    [Fact]
    public void Strip_Gif_KeepsAnimation()
    {
        var stripped = Strip(Fixture("comment-animated.gif"));
        Assert.True(Contains(stripped, "NETSCAPE2.0"));
        Assert.Equal(0x3B, stripped[^1]);
        Assert.True(stripped.Length > 4000); // 프레임 데이터가 통째로 남아 있다(원본 4,613바이트에서 주석만 빠진다)
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
}
