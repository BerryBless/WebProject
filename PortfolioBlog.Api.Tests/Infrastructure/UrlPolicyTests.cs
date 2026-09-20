using PortfolioBlog.Api.Infrastructure.Markdown;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>마크다운 링크·이미지 URL 정책 단위 테스트. 허용 목록 방식이라 "허용되는 것"을 좁게, "거부되는 것"을 넓게 고정한다.</summary>
public sealed class UrlPolicyTests
{
    /// <summary>링크는 http·https·mailto 절대 URL, 같은 사이트의 루트 상대 경로, 문서 내 앵커만 허용한다.</summary>
    [Theory]
    [InlineData("https://example.test/a?b=1#c")]
    [InlineData("http://example.test")]
    [InlineData("HTTPS://EXAMPLE.TEST/UPPER")]
    [InlineData("mailto:someone@example.test")]
    [InlineData("/posts/my-post")]
    [InlineData("/tags/C%23")]
    [InlineData("#section-1")]
    public void IsAllowedLink_Accepts(string url) => Assert.True(UrlPolicy.IsAllowedLink(url));

    /// <summary>스크립트·데이터 스킴, 프로토콜 상대, 백슬래시 트릭, 공백·제어문자 난독화, 상대 경로는 전부 거부한다.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("java\tscript:alert(1)")]
    [InlineData("java\nscript:alert(1)")]
    [InlineData(" javascript:alert(1)")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.test/x")]
    [InlineData("//evil.test/x")]
    [InlineData("/\\evil.test/x")]
    [InlineData("\\\\evil.test\\x")]
    [InlineData("https:\\\\evil.test")]
    [InlineData("relative/path")]
    [InlineData("../up")]
    [InlineData("https://example.test/a b")]
    [InlineData("https://exa\0mple.test")]
    public void IsAllowedLink_Rejects(string? url) => Assert.False(UrlPolicy.IsAllowedLink(url));

    /// <summary>이미지는 이 사이트가 직접 서빙하는 첨부 경로(<c>/attachments/{guid}/{파일명}</c>)만 허용한다.</summary>
    [Theory]
    [InlineData("/attachments/0192f0c4-7a3b-7c1d-9e2f-1a2b3c4d5e6f/diagram.png")]
    [InlineData("/attachments/0192F0C4-7A3B-7C1D-9E2F-1A2B3C4D5E6F/%ED%95%9C%EA%B8%80.webp")]
    public void IsAllowedImage_Accepts(string url) => Assert.True(UrlPolicy.IsAllowedImage(url));

    /// <summary>외부 이미지(핫링크·추적 픽셀), data URI, 경로 탈출, 질의 문자열은 거부한다.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("https://evil.test/pixel.png")]
    [InlineData("//evil.test/pixel.png")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("/attachments/../api/posts")]
    [InlineData("/attachments/%2e%2e/api/posts")]
    [InlineData("/attachments/not-a-guid/x.png")]
    [InlineData("/attachments/0192f0c4-7a3b-7c1d-9e2f-1a2b3c4d5e6f/")]
    [InlineData("/attachments/0192f0c4-7a3b-7c1d-9e2f-1a2b3c4d5e6f/a/b.png")]
    [InlineData("/attachments/0192f0c4-7a3b-7c1d-9e2f-1a2b3c4d5e6f/x.png?download=1")]
    [InlineData("/attachments/0192f0c4-7a3b-7c1d-9e2f-1a2b3c4d5e6f/x.png\n")]
    [InlineData("/Attachments/0192f0c4-7a3b-7c1d-9e2f-1a2b3c4d5e6f/x.png")]
    [InlineData("/posts/my-post")]
    public void IsAllowedImage_Rejects(string? url) => Assert.False(UrlPolicy.IsAllowedImage(url));
}
