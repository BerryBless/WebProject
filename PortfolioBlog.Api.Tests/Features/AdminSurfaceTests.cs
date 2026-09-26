using System.Net;
using System.Net.Http.Json;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Infrastructure.Access;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>
/// <c>/api/*</c> 요청을 본문을 읽기 전에 거르는 <see cref="AdminSurfaceMiddleware"/>의 통합 테스트.
/// 호스트·IP·CSRF 헤더·Origin 4단계 검사와 공개 호스트에서의 404를 검증한다.
/// </summary>
/// <param name="factory">컬렉션이 공유하는 컨테이너를 바탕으로 클래스 전용 DB를 갖는 <see cref="ApiFactory"/> 클래스 픽스처.</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 실행된다. <see cref="ApiFactory"/>가 호스팅하는 인메모리 TestServer로
/// 요청을 보내므로 실제 소켓 바인딩·TCP 핸드셰이크는 발생하지 않는다.</description></item>
/// <item><description><b>Memory Policy:</b> 팩토리는 <see cref="IClassFixture{TFixture}"/>로 클래스 단위 1회 생성·공유된다.
/// 각 테스트는 <c>using</c>으로 자신의 <see cref="HttpClient"/>·응답을 스코프 종료 시 해제한다.</description></item>
/// <item><description><b>Concurrency:</b> 팩토리·HttpClient는 Thread-safe하나, 테스트 간에는 요청 헤더 조작으로만 상태를 바꾸므로 데이터 간섭이 없다.</description></item>
/// <item><description><b>Blocking:</b> 모든 HTTP 호출은 <c>await</c>로 비동기 대기하며 동기 블로킹이 없다.</description></item>
/// </list>
/// </remarks>
[Collection("mysql")]
public sealed class AdminSurfaceTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Me = "/api/auth/me";

    /// <summary>허용 IP·CSRF 헤더를 모두 갖춘 관리 호스트 요청이 200과 <c>Cache-Control: no-store</c>를 반환하는지 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 이 테스트 전용 <see cref="HttpClient"/>만 사용하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 클라이언트·응답·역직렬화된 DTO 각 1개.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 요청 완료를 <c>await</c>로 대기한다.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task AllowedIp_WithHeader_OnAdminHost_Returns200_NoStore()
    {
        using var client = factory.CreateAdminClient();
        using var res = await client.GetAsync(Me);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.False((await res.Content.ReadFromJsonAsync<AuthStatusDto>(TestJson.Options))!.Authenticated);
        Assert.True(res.Headers.CacheControl?.NoStore);
    }

    /// <summary>허용 CIDR 밖의 원본 IP가 403으로 거부되는지 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 이 테스트 전용 <see cref="HttpClient"/>만 사용하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 클라이언트·응답 각 1개.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 요청 완료를 <c>await</c>로 대기한다.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task OutsiderIp_Returns403()
    {
        using var client = factory.CreateAdminClient();
        client.DefaultRequestHeaders.Remove(RemoteIpStartupFilter.HeaderName);
        client.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, ApiFactory.OutsiderIp);
        using var res = await client.GetAsync(Me);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>원본 IP를 전혀 알 수 없을 때(TestServer 기본값 null) 403으로 거부되는지 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 이 테스트 전용 <see cref="HttpClient"/>만 사용하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 클라이언트·응답 각 1개.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 요청 완료를 <c>await</c>로 대기한다.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task NoRemoteIp_Returns403()
    {
        using var client = factory.CreateAdminClient();
        client.DefaultRequestHeaders.Remove(RemoteIpStartupFilter.HeaderName);
        using var res = await client.GetAsync(Me);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    /// <summary>CSRF 헤더가 없으면 GET처럼 안전한 메서드도 403으로 거부되는지 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 이 테스트 전용 <see cref="HttpClient"/>만 사용하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 클라이언트·응답 각 1개.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 요청 완료를 <c>await</c>로 대기한다.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task MissingCsrfHeader_Returns403_EvenOnGet()
    {
        using var client = factory.CreateAdminClient();
        client.DefaultRequestHeaders.Remove(AdminSurfaceMiddleware.CsrfHeaderName);
        using var res = await client.GetAsync(Me);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    /// <summary>관리 호스트에서 대소문자 변형 경로(<c>/API/AUTH/ME</c>·<c>/Api/Posts</c>)도 <see cref="AdminSurfaceMiddleware"/> 자체가 그대로 관여해
    /// CSRF 헤더 없이는 403으로 거부되는지 검증한다. 지금까지는 공개 호스트에서의 404 변형만 검증되어 있었는데, 그 404는 <c>RequireHost</c>만으로도
    /// 나올 수 있어 미들웨어가 관리 호스트에서도 대소문자를 무시하고 스스로 작동한다는 보장이 되지 않았다.</summary>
    /// <param name="path">CSRF 헤더 없이 요청할, 관리 호스트의 대소문자 변형 경로.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 케이스마다 독립된 <see cref="HttpClient"/>를 사용하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 케이스당 클라이언트·응답 각 1개.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 요청 완료를 <c>await</c>로 대기한다.</description></item>
    /// </list>
    /// </remarks>
    [Theory]
    [InlineData("/API/AUTH/ME")]
    [InlineData("/Api/Posts")]
    public async Task AdminHost_CaseVariantPaths_Returns403_WithoutCsrfHeader(string path)
    {
        using var client = factory.CreateAdminClient();
        client.DefaultRequestHeaders.Remove(AdminSurfaceMiddleware.CsrfHeaderName);
        using var res = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    /// <summary>공개 호스트에서는 허용 IP·CSRF 헤더가 있어도 <c>/api</c> 경로(대소문자 변형·그룹 루트 포함)가 전부 404인지 검증한다.</summary>
    /// <param name="path">공개 호스트로 요청할 <c>/api</c> 하위 경로(대소문자 변형·그룹 루트 포함).</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 케이스마다 독립된 <see cref="HttpClient"/>를 사용하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 케이스당 클라이언트·응답 각 1개.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 요청 완료를 <c>await</c>로 대기한다.</description></item>
    /// </list>
    /// </remarks>
    [Theory]
    [InlineData("/api/auth/me")]
    [InlineData("/API/auth/me")]   // 대소문자 변형
    [InlineData("/api")]           // 그룹 루트 자체
    [InlineData("/api/")]
    public async Task PublicHost_ApiPaths_Return404_EvenFromAllowedIp(string path)
    {
        using var client = factory.CreatePublicClient();
        client.DefaultRequestHeaders.Remove(RemoteIpStartupFilter.HeaderName);
        client.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, ApiFactory.AllowedIp);
        client.DefaultRequestHeaders.Add(AdminSurfaceMiddleware.CsrfHeaderName, AdminSurfaceMiddleware.CsrfHeaderValue);
        using var res = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    /// <summary>안전하지 않은 메서드(POST)에서 Origin이 없거나·다른 origin이거나·스킴만 달라도 403으로 거부되는지 검증한다.</summary>
    /// <param name="origin">요청에 실을 Origin 헤더 값. null이면 Origin 헤더 자체를 생략한다.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 케이스마다 독립된 <see cref="HttpClient"/>를 사용하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 케이스당 클라이언트·응답 각 1개.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 요청 완료를 <c>await</c>로 대기한다.</description></item>
    /// </list>
    /// </remarks>
    [Theory]
    [InlineData(null)]                       // Origin 없음
    [InlineData("https://evil.test")]        // 다른 origin
    [InlineData("https://blog.test")]        // 같은 사이트의 공개 origin도 거부
    [InlineData("http://admin.test")]        // 스킴이 다르면 다른 origin
    public async Task UnsafeMethod_WithWrongOrigin_Returns403(string? origin)
    {
        using var client = factory.CreateAdminClient();
        client.DefaultRequestHeaders.Remove("Origin");
        if (origin is not null) client.DefaultRequestHeaders.Add("Origin", origin);
        using var res = await client.PostAsJsonAsync("/api/does-not-exist", new { });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    /// <summary>관리 Origin을 실은 안전하지 않은 메서드는 미들웨어를 통과하고(엔드포인트 부재로 404), Origin 검사에서 막히지 않는지 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 이 테스트 전용 <see cref="HttpClient"/>만 사용하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 클라이언트·응답 각 1개.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 요청 완료를 <c>await</c>로 대기한다.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task UnsafeMethod_WithAdminOrigin_PassesMiddleware()
    {
        using var client = factory.CreateAdminClient();
        using var res = await client.PostAsJsonAsync("/api/does-not-exist", new { });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode); // 미들웨어는 통과했고 엔드포인트가 없을 뿐이다
    }

    /// <summary><c>/health</c>는 <c>/api</c> 그룹 밖이라 관리 표면 제약을 받지 않고 어느 호스트·IP에서도 200인지 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 이 테스트 전용 <see cref="HttpClient"/>만 사용하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 클라이언트·응답 각 1개.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 요청 완료를 <c>await</c>로 대기한다.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task Health_IsPublic_OnAnyHost_FromAnyIp()
    {
        using var client = factory.CreatePublicClient();
        using var res = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
