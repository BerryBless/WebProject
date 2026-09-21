using PortfolioBlog.Api.Infrastructure.Web;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>공개 경로 생성. 태그는 경로 세그먼트 하나로 안전하게 인코딩되어야 한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 순수 함수 호출만 검증하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
/// <item><description><b>Memory Allocation:</b> 케이스당 <see cref="PortfolioBlog.Api.Infrastructure.Web.PublicUrls"/>가 반환하는 문자열 1개.</description></item>
/// <item><description><b>Blocking:</b> 동기 실행. I/O 없음.</description></item>
/// </list>
/// </remarks>
public sealed class PublicUrlsTests
{
    [Theory]
    [InlineData("c#", "/tags/c%23")]
    [InlineData(".net", "/tags/.net")]
    [InlineData("a b", "/tags/a%20b")]
    [InlineData("100%", "/tags/100%25")]
    [InlineData("a?b&c=d", "/tags/a%3Fb%26c%3Dd")]
    [InlineData("a\\b", "/tags/a%5Cb")]
    [InlineData("한글", "/tags/%ED%95%9C%EA%B8%80")]
    public void Tag_IsOnePathSegment(string normalized, string expected) => Assert.Equal(expected, PublicUrls.Tag(normalized));

    /// <summary>"."과 ".."은 URL 정규화가 다른 경로로 바꿔 버리므로 링크를 만들지 않는다.</summary>
    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void Tag_DotSegments_HaveNoLink(string normalized) => Assert.Null(PublicUrls.Tag(normalized));
}
