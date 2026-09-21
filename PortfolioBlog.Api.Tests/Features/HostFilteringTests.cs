using System.Net;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>설정된 두 호스트 밖의 Host 헤더는 앱 코드에 닿기 전에 400이다(2A가 남긴 AllowedHosts=*).</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 실행되며, 프레임워크가 등록하는 <c>HostFilteringMiddleware</c>가 TestServer 파이프라인 최상단에서 요청을 가로챈다.</description></item>
/// <item><description><b>Memory Policy:</b> <paramref name="factory"/>는 클래스 픽스처로 1회 생성·공유된다. 각 <see cref="Theory"/> 케이스가 독립된 <see cref="HttpClient"/>·<see cref="HttpRequestMessage"/>를 만들어 <c>using</c>으로 해제한다.</description></item>
/// <item><description><b>Concurrency:</b> <c>postgres</c> 컬렉션에 속해 같은 컬렉션의 다른 테스트 클래스와 순차 실행된다. 케이스 간 공유 가변 상태가 없어 병렬 실행에도 안전하다.</description></item>
/// </list>
/// </remarks>
[Collection("postgres")]
public sealed class HostFilteringTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>설정된 두 호스트(대소문자 무시)만 통과하고, 그 밖의 호스트·서브도메인 위장·localhost는 400이다.</summary>
    [Theory]
    [InlineData("blog.test", 200)]
    [InlineData("admin.test", 200)]
    [InlineData("BLOG.TEST", 200)]
    [InlineData("evil.test", 400)]
    [InlineData("blog.test.evil.test", 400)]
    [InlineData("localhost", 400)]
    public async Task Host_IsRestrictedToTheTwoConfiguredHosts(string host, int expected)
    {
        using var client = factory.CreatePublicClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/health");
        req.Headers.Host = host;
        using var res = await client.SendAsync(req);
        Assert.Equal(expected, (int)res.StatusCode);
    }
}
