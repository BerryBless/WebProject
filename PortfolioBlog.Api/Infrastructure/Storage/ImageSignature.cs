namespace PortfolioBlog.Api.Infrastructure.Storage;

/// <summary>파일의 첫 바이트들로 형식을 판정한다. 업로드된 파일 이름과 Content-Type은 공격자가 고를 수 있으므로 쓰지 않는다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 무상태.</description></item>
/// <item><description><b>Memory Allocation:</b> Zero-allocation(스팬 비교, UTF-8 리터럴은 정적 데이터).</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
/// </list>
/// 시그니처 일치는 "정상 이미지"를 보증하지 않는다. 구조 검증은 <see cref="MetadataStripper"/>가 컨테이너를 끝까지 읽으며 수행하고,
/// 응답에는 <c>X-Content-Type-Options: nosniff</c>와 여기서 정한 Content-Type만 쓴다.
/// </remarks>
public static class ImageSignature
{
    /// <summary>판정에 필요한 최소 바이트 수(WebP의 <c>RIFF….WEBP</c>).</summary>
    public const int HeaderLength = 12;

    /// <summary>주어진 헤더 바이트로 이미지 형식을 판정한다.</summary>
    /// <param name="header">파일 앞부분의 원시 바이트(최소 <see cref="HeaderLength"/>바이트면 모든 형식을 판정할 수 있다).</param>
    /// <returns>판정된 형식, 또는 네 형식 중 어디에도 맞지 않으면 <see langword="null"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 무상태.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation. 입력을 복사하지 않고 스팬으로만 비교한다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    public static ImageKind? Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 8 && header[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return ImageKind.Png;
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return ImageKind.Jpeg;
        if (header.Length >= 6 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8))) return ImageKind.Gif;
        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header.Slice(8, 4).SequenceEqual("WEBP"u8)) return ImageKind.WebP;
        return null;
    }

    /// <summary>형식에 대응하는 저장용 확장자(점 없음)를 반환한다.</summary>
    /// <param name="kind">판정된 이미지 형식</param>
    /// <returns>소문자 확장자 문자열(<c>"png"|"jpg"|"gif"|"webp"</c>)</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 무상태.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation(문자열 리터럴 반환, 새 할당 없음).</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    public static string Extension(ImageKind kind) => kind switch
    {
        ImageKind.Png => "png",
        ImageKind.Jpeg => "jpg",
        ImageKind.Gif => "gif",
        ImageKind.WebP => "webp",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>형식에 대응하는 MIME Content-Type을 반환한다.</summary>
    /// <param name="kind">판정된 이미지 형식</param>
    /// <returns>Content-Type 문자열(<c>"image/png"|"image/jpeg"|"image/gif"|"image/webp"</c>)</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 무상태.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation(문자열 리터럴 반환, 새 할당 없음).</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    public static string ContentType(ImageKind kind) => kind switch
    {
        ImageKind.Png => "image/png",
        ImageKind.Jpeg => "image/jpeg",
        ImageKind.Gif => "image/gif",
        ImageKind.WebP => "image/webp",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
