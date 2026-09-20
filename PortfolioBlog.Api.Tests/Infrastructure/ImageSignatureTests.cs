using System.Text;
using PortfolioBlog.Api.Infrastructure.Storage;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>파일 시그니처 판정 단위 테스트. 업로드된 파일 이름·Content-Type은 믿지 않고 첫 바이트들만 본다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 공유 상태 없음.</description></item>
/// <item><description><b>Memory Allocation:</b> 픽스처 파일을 통째로 읽지만 각 5KB 미만이다.</description></item>
/// <item><description><b>Blocking:</b> 동기 파일 읽기만. Docker 불필요.</description></item>
/// </list>
/// </remarks>
public sealed class ImageSignatureTests
{
    private static byte[] Head(string fixture) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images", fixture))[..ImageSignature.HeaderLength];

    /// <summary>네 가지 허용 형식의 실제 파일을 알아보고, 확장자와 Content-Type을 시그니처에서 정한다.</summary>
    [Theory]
    [InlineData("exif-gps.jpg", ImageKind.Jpeg, "jpg", "image/jpeg")]
    [InlineData("exif-text.png", ImageKind.Png, "png", "image/png")]
    [InlineData("exif-xmp.webp", ImageKind.WebP, "webp", "image/webp")]
    [InlineData("comment-animated.gif", ImageKind.Gif, "gif", "image/gif")]
    public void Detect_KnownFormats(string fixture, ImageKind expected, string extension, string contentType)
    {
        var kind = ImageSignature.Detect(Head(fixture));
        Assert.Equal(expected, kind);
        Assert.Equal(extension, ImageSignature.Extension(kind!.Value));
        Assert.Equal(contentType, ImageSignature.ContentType(kind.Value));
    }

    /// <summary>SVG·HTML·빈 입력·너무 짧은 입력은 거부한다.</summary>
    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\">")]
    [InlineData("<!DOCTYPE html><html>")]
    [InlineData("")]
    [InlineData("GIF8")]
    public void Detect_RejectsText(string content) =>
        Assert.Null(ImageSignature.Detect(Encoding.ASCII.GetBytes(content)));

    /// <summary>실행 파일 헤더, "RIFF이지만 WEBP가 아닌" 컨테이너, 세 번째 바이트가 없는 JPEG SOI도 거부한다.
    /// 0x00이 든 입력은 문자열 리터럴이 아니라 바이트 배열로 만든다(계획 Global Constraints의 NUL 표기 규칙).</summary>
    [Fact]
    public void Detect_RejectsBinaryLookalikes()
    {
        Assert.Null(ImageSignature.Detect([0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00])); // MZ (PE 실행 파일)
        Assert.Null(ImageSignature.Detect([0x52, 0x49, 0x46, 0x46, 0x10, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45])); // RIFF....WAVE
        Assert.Null(ImageSignature.Detect([0xFF, 0xD8]));
    }
}
