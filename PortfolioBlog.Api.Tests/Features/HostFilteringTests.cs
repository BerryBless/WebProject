using System.Net;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Infrastructure.Web;
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
[Collection("mysql")]
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

    /// <summary>허용 목록 밖 Host의 400에는 본문이 없다: 프레임워크의 <c>HostFilteringMiddleware</c>는 앱 미들웨어보다 바깥이라
    /// <see cref="SecurityHeadersMiddleware"/>가 CSP·nosniff를 붙일 기회가 없으므로, 그 응답이 HTML 본문을 실어 보내면
    /// "본문 있는 응답에는 보안 헤더가 있다"는 계약이 깨진다. <c>IncludeFailureMessage = false</c>로 본문 자체를 없애 그 구멍을 닫는다.</summary>
    [Fact]
    public async Task RejectedHost_400_HasNoBody_AndNoSecurityHeaders()
    {
        using var client = factory.CreatePublicClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/health");
        req.Headers.Host = "evil.test";
        using var res = await client.SendAsync(req);

        Assert.Equal(400, (int)res.StatusCode);
        Assert.Empty(await res.Content.ReadAsByteArrayAsync());
        Assert.False(res.Headers.Contains("Content-Security-Policy"), "호스트 필터 400에 CSP가 실렸다 — 본문이 있는데 헤더가 없는 조합이 남아 있다.");
        Assert.False(res.Content.Headers.Contains("Content-Type"), "본문이 없는 응답에 Content-Type이 남아 있다.");
    }

    /// <summary>위 <see cref="RejectedHost_400_HasNoBody_AndNoSecurityHeaders"/>가 관측하는 성질의 출처를 옵션 값으로도 고정한다
    /// (프레임워크가 이 옵션의 의미를 바꾸면 두 단언 중 어느 쪽이 깨졌는지 구분할 수 있다).</summary>
    [Fact]
    public void HostFilteringOptions_DoNotIncludeTheFailureMessage()
    {
        using var _ = factory.CreateClient();
        Assert.False(factory.Services.GetRequiredService<IOptions<HostFilteringOptions>>().Value.IncludeFailureMessage);
    }
}
