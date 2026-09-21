using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Infrastructure.Web;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>모든 응답이 스펙 3.6의 헤더를 지니는지 실제 앱 파이프라인으로 검증한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 실행되며, <see cref="ApiFactory"/>가 구동하는 인메모리 TestServer로 실제 파이프라인(라우팅·미들웨어)을 검증한다.</description></item>
/// <item><description><b>Memory Policy:</b> <paramref name="factory"/>는 클래스 픽스처로 1회 생성·공유된다. <see cref="Hsts_OnlyOutsideDevelopment"/>만 별도의 <see cref="ApiFactory"/>를 직접 만들어 <c>using</c>으로 해제한다.</description></item>
/// <item><description><b>Concurrency:</b> <c>postgres</c> 컬렉션에 속해 같은 컬렉션의 다른 테스트 클래스와는 순차 실행된다. <paramref name="factory"/>는 <see cref="IClassFixture{TFixture}"/>로 클래스 내 모든 케이스가 공유하지만, 각 케이스가 <c>using</c>으로 자기 전용 <see cref="HttpClient"/>를 새로 만들어 요청 상태(쿠키·기본 헤더)는 공유하지 않는다.</description></item>
/// </list>
/// </remarks>
[Collection("postgres")]
public sealed class SecurityHeadersTests(ApiFactory factory, PostgresContainerFixture pg) : IClassFixture<ApiFactory>
{
    /// <summary>200·라우트 제약 실패 404·관리 게이트 거부 404가 전부 같은 헤더를 지닌다(2A가 남긴 "제약 실패 404에는 nosniff가 없다").</summary>
    [Theory]
    [InlineData("/health", 200)]
    [InlineData("/attachments/not-a-guid/x.png", 404)]
    [InlineData("/no-such-path", 404)]
    [InlineData("/api/posts", 404)] // 공개 호스트의 /api
    public async Task EveryResponse_CarriesBaselineHeaders(string path, int status)
    {
        using var client = factory.CreatePublicClient();
        using var res = await client.GetAsync(path);
        Assert.Equal(status, (int)res.StatusCode);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(SecurityHeadersMiddleware.PublicCsp, res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("strict-origin-when-cross-origin", res.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal(SecurityHeadersMiddleware.PermissionsPolicy, res.Headers.GetValues("Permissions-Policy").Single());
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.False(res.Headers.Contains("Set-Cookie"));
    }

    /// <summary>CSP 문자열이 스펙 3.6과 글자 그대로 같다(지시문이 하나라도 빠지면 실패).</summary>
    [Fact]
    public void PublicCsp_MatchesTheSpec() =>
        Assert.Equal("default-src 'none'; img-src 'self'; style-src 'self'; font-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'",
            SecurityHeadersMiddleware.PublicCsp);

    /// <summary>첨부 404는 자기 CSP(sandbox)를 유지한다 — 전역 미들웨어가 덮어쓰지 않는다.</summary>
    [Fact]
    public async Task AttachmentResponses_KeepTheirOwnSandboxCsp()
    {
        using var client = factory.CreatePublicClient();
        using var res = await client.GetAsync($"/attachments/{Guid.NewGuid()}/x.png");
        Assert.Equal("default-src 'none'; sandbox", res.Headers.GetValues("Content-Security-Policy").Single());
    }

    /// <summary>HSTS는 Development가 아닐 때만 붙는다(로컬 http 개발을 막지 않는다).</summary>
    [Fact]
    public async Task Hsts_OnlyOutsideDevelopment()
    {
        using (var dev = factory.CreatePublicClient())
        using (var res = await dev.GetAsync("/health"))
        {
            Assert.False(res.Headers.Contains("Strict-Transport-Security"));
        }

        using var production = new ApiFactory(pg, new Dictionary<string, string?>
        {
            ["Test:Environment"] = "Production", ["Proxy:TrustedIp"] = "172.30.0.2",
        });
        using var client = production.CreatePublicClient();
        using var prodRes = await client.GetAsync("/health");
        Assert.Equal("max-age=31536000; includeSubDomains", prodRes.Headers.GetValues("Strict-Transport-Security").Single());
    }

    /// <summary>Kestrel의 Server 헤더가 꺼져 있다(TestServer는 Kestrel이 아니므로 옵션 값으로 확인한다. 실제 응답은 최종 리뷰가 실제 호스트에서 본다).</summary>
    [Fact]
    public void KestrelServerHeader_IsDisabled()
    {
        using var _ = factory.CreateClient();
        Assert.False(factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.AddServerHeader);
    }
}
