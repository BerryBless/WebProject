using System.Net;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>X-Forwarded-For는 설정된 단 하나의 프록시 IP가 보낸 경우에만 믿는다.</summary>
/// <param name="pg">컬렉션이 공유하는 PostgreSQL 컨테이너 fixture. 이 테스트 클래스는 <c>Proxy:TrustedIp</c> 설정을 케이스마다 다르게 주어야 하므로 클래스 픽스처 대신 케이스마다 <see cref="ApiFactory"/>를 직접 만든다.</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 실행된다. <see cref="ApiFactory"/>가 호스팅하는 인메모리 TestServer로 요청을 보낸다.</description></item>
/// <item><description><b>Memory Policy:</b> 테스트 케이스마다 <see cref="ApiFactory"/>를 직접 생성·<c>using</c>으로 해제하여 클래스 픽스처를 공유하지 않는다(설정 오버라이드가 케이스마다 다르므로).</description></item>
/// <item><description><b>Concurrency:</b> 케이스 간 공유 가변 상태가 없으므로 병렬 실행에 안전하다.</description></item>
/// <item><description><b>Blocking:</b> 모든 HTTP 호출은 <c>await</c>로 비동기 대기하며 동기 블로킹이 없다.</description></item>
/// </list>
/// </remarks>
[Collection("postgres")]
public sealed class ForwardedHeadersTests(PostgresContainerFixture pg)
{
    private const string Proxy = "172.30.0.2";        // Caddy 컨테이너 고정 IP
    private const string OtherContainer = "172.30.0.9"; // 같은 compose 네트워크의 다른 컨테이너

    /// <summary>지정한 원본 IP·<c>X-Forwarded-For</c> 값으로 <c>/api/auth/me</c>를 호출하고 상태 코드만 돌려주는 테스트 헬퍼.</summary>
    /// <param name="factory">이번 케이스 전용으로 구성된 <see cref="ApiFactory"/>.</param>
    /// <param name="remoteIp"><see cref="RemoteIpStartupFilter"/>가 <c>Connection.RemoteIpAddress</c>에 넣을 값(직접 연결한 소켓의 IP를 흉내낸다).</param>
    /// <param name="forwardedFor"><c>X-Forwarded-For</c> 헤더 값. null이면 헤더를 아예 보내지 않는다.</param>
    /// <returns>응답 상태 코드.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 호출마다 새 <see cref="HttpClient"/>를 만들고 <c>using</c>으로 해제하므로 다른 호출과 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 클라이언트·응답 각 1개.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 요청 완료를 <c>await</c>로 대기한다.</description></item>
    /// </list>
    /// </remarks>
    private static async Task<HttpStatusCode> GetMeAsync(ApiFactory factory, string remoteIp, string? forwardedFor)
    {
        using var client = factory.CreateAdminClient();
        client.DefaultRequestHeaders.Remove(RemoteIpStartupFilter.HeaderName);
        client.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, remoteIp);
        if (forwardedFor is not null) client.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);
        using var res = await client.GetAsync("/api/auth/me");
        return res.StatusCode;
    }

    /// <summary>신뢰 프록시가 직접 보낸 요청이면 <c>X-Forwarded-For</c>의 원본 IP를 그대로 IP 허용 판정에 쓰는지 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 이 테스트 전용 <see cref="ApiFactory"/>만 사용하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 팩토리 1개, 호출마다 클라이언트·응답 각 1개.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 두 호출을 순차 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task TrustedProxy_ForwardedFor_IsHonored()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?> { ["Proxy:TrustedIp"] = Proxy });
        Assert.Equal(HttpStatusCode.OK, await GetMeAsync(factory, Proxy, ApiFactory.AllowedIp));
        Assert.Equal(HttpStatusCode.Forbidden, await GetMeAsync(factory, Proxy, ApiFactory.OutsiderIp));
    }

    /// <summary>공격자가 <c>X-Forwarded-For</c>의 맨 왼쪽에 위조 IP를 끼워 넣어도 <c>ForwardLimit=1</c> 덕분에 무시되는지 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 이 테스트 전용 <see cref="ApiFactory"/>만 사용하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 팩토리·클라이언트·응답 각 1개.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 요청 완료를 <c>await</c>로 대기한다.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task TrustedProxy_SpoofedLeftmostEntry_IsIgnored()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?> { ["Proxy:TrustedIp"] = Proxy });
        // 공격자가 보낸 "허용 IP"가 맨 왼쪽, Caddy가 붙인 실제 IP가 맨 오른쪽. ForwardLimit=1이라 오른쪽 하나만 본다.
        Assert.Equal(HttpStatusCode.Forbidden,
            await GetMeAsync(factory, Proxy, $"{ApiFactory.AllowedIp}, {ApiFactory.OutsiderIp}"));
    }

    /// <summary>신뢰 목록에 없는 송신자가 보낸 <c>X-Forwarded-For</c>는 같은 컴포즈 네트워크 대역이어도 무시되는지 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 이 테스트 전용 <see cref="ApiFactory"/>만 사용하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 팩토리 1개, 호출마다 클라이언트·응답 각 1개.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 두 호출을 순차 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task UntrustedSender_EvenInSameNetwork_ForwardedForIsIgnored()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?> { ["Proxy:TrustedIp"] = Proxy });
        Assert.Equal(HttpStatusCode.Forbidden, await GetMeAsync(factory, OtherContainer, ApiFactory.AllowedIp));
        Assert.Equal(HttpStatusCode.Forbidden, await GetMeAsync(factory, ApiFactory.OutsiderIp, ApiFactory.AllowedIp));
    }

    /// <summary><c>Proxy:TrustedIp</c>가 비어 ForwardedHeaders 미들웨어 자체가 등록되지 않으면, 직접 연결 IP만 보고 <c>X-Forwarded-For</c>는 완전히 무시되는지 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 이 테스트 전용 <see cref="ApiFactory"/>만 사용하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 팩토리 1개, 호출마다 클라이언트·응답 각 1개.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 두 호출을 순차 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task NoTrustedProxy_MiddlewareNotRegistered_ForwardedForIgnored()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        // 신뢰 목록이 비면 ASP.NET 기본 동작은 "모든 헤더를 믿음"이다. 그래서 미들웨어를 아예 등록하지 않는다.
        Assert.Equal(HttpStatusCode.Forbidden, await GetMeAsync(factory, ApiFactory.OutsiderIp, ApiFactory.AllowedIp));
        Assert.Equal(HttpStatusCode.OK, await GetMeAsync(factory, ApiFactory.AllowedIp, null));
    }
}
