using System.Net;
using System.Net.Http.Json;
using System.Text;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Infrastructure.Access;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>
/// 아이디 없는 비밀번호 로그인, <c>__Host-</c> 세션 쿠키 경화 속성, 서버 측 세션 폐기(epoch), 절대 만료, 로그인 속도 제한을
/// 실제 PostgreSQL 컨테이너와 인메모리 TestServer로 검증하는 통합 테스트.
/// </summary>
/// <param name="factory">컬렉션이 공유하는 컨테이너를 바탕으로 클래스 전용 DB를 갖는 <see cref="ApiFactory"/> 클래스 픽스처.</param>
/// <param name="pg">시계를 독립적으로 돌려야 하는 케이스가 별도 <see cref="ApiFactory"/>를 만들 때 재사용하는 컨테이너 fixture.</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 실행된다. 로그인 검증은 서버 측에서 PBKDF2를 동기 실행하므로 각 로그인 호출은 수십 ms가 걸린다.</description></item>
/// <item><description><b>Memory Policy:</b> 대부분의 케이스는 공유 <paramref name="factory"/>를 쓰지만, 시계 전진·속도 제한처럼 다른 테스트와 상태가 섞이면 안 되는 케이스는
/// <c>new ApiFactory(pg, ...)</c>로 격리된 인스턴스를 만들고 <c>using</c>으로 해제한다.</description></item>
/// <item><description><b>Concurrency:</b> 격리된 케이스는 자신만의 호스트·DB·시계를 가지므로 병렬 실행에 안전하다. 공유 <paramref name="factory"/>를 쓰는 케이스는
/// 요청 헤더 조작으로만 상태를 바꾸므로 데이터 간섭이 없다.</description></item>
/// <item><description><b>Blocking:</b> 모든 HTTP 호출은 <c>await</c>로 비동기 대기하며 동기 블로킹이 없다.</description></item>
/// </list>
/// </remarks>
[Collection("postgres")]
public sealed class AuthEndpointsTests(ApiFactory factory, PostgresContainerFixture pg) : IClassFixture<ApiFactory>
{
    private const string Login = "/api/auth/login";
    private const string Logout = "/api/auth/logout";
    private const string Me = "/api/auth/me";

    /// <summary><c>GET /api/auth/me</c>를 호출해 인증 여부를 읽는다. <paramref name="cookie"/>를 주면 쿠키 컨테이너 대신 그 값을 직접 <c>Cookie</c> 헤더로 보낸다.</summary>
    /// <param name="client">요청에 쓸 <see cref="HttpClient"/>.</param>
    /// <param name="cookie">직접 실을 <c>name=value</c> 쿠키 헤더 값. null이면 클라이언트의 쿠키 컨테이너에 맡긴다.</param>
    /// <returns><see cref="AuthStatusDto.Authenticated"/> 값.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 전달받은 <paramref name="client"/>만 사용하고 공유 가변 상태를 만들지 않는다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 요청 메시지·응답·역직렬화된 DTO 각 1개.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 요청 완료를 <c>await</c>로 대기한다.</description></item>
    /// </list>
    /// </remarks>
    private static async Task<bool> IsAuthenticatedAsync(HttpClient client, string? cookie = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Me);
        if (cookie is not null) req.Headers.Add("Cookie", cookie);
        using var res = await client.SendAsync(req);
        return (await res.Content.ReadFromJsonAsync<AuthStatusDto>(TestJson.Options))!.Authenticated;
    }

    /// <summary>올바른 비밀번호로 로그인하면 204를 반환하고, <c>__Host-</c> 접두사 요건(Secure·Path=/·Domain 미지정)과
    /// HttpOnly·SameSite=Strict를 만족하는 세션 쿠기 응답 헤더를 실어 보내는지 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 이 테스트 전용 <see cref="HttpClient"/>만 사용하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 클라이언트·응답·<c>Set-Cookie</c> 헤더 문자열 각 1개.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 로그인 요청 완료(서버 측 PBKDF2 검증 포함)를 <c>await</c>로 대기한다.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task Login_CorrectPassword_Returns204_AndHardenedCookie()
    {
        using var client = factory.CreateAdminClient(handleCookies: false);
        using var res = await client.PostAsJsonAsync(Login, new { password = ApiFactory.Password });

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        var cookie = res.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant();
        Assert.StartsWith("__host-adminsession=", cookie);
        Assert.Contains("; path=/", cookie);
        Assert.Contains("; secure", cookie);
        Assert.Contains("; httponly", cookie);
        Assert.Contains("; samesite=strict", cookie);
        Assert.DoesNotContain("domain=", cookie);   // __Host- 접두사 요건
        Assert.DoesNotContain("expires=", cookie);  // 세션 쿠키. 수명은 서버가 티켓 발급 시각으로 강제한다.
    }

    /// <summary>틀린 비밀번호(빈 문자열 포함)로 로그인하면 401을 반환하고 쿠키를 전혀 내려주지 않는지 검증한다.</summary>
    /// <param name="password">시도할 잘못된 비밀번호.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 케이스마다 독립된 <see cref="HttpClient"/>를 사용하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 케이스당 클라이언트·응답 각 1개.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 로그인 요청 완료를 <c>await</c>로 대기한다.</description></item>
    /// </list>
    /// </remarks>
    [Theory]
    [InlineData("wrong-password")]
    [InlineData("")]
    public async Task Login_WrongPassword_Returns401_NoCookie(string password)
    {
        using var client = factory.CreateAdminClient(handleCookies: false);
        using var res = await client.PostAsJsonAsync(Login, new { password });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.False(res.Headers.Contains("Set-Cookie"));
    }

    /// <summary>비밀번호 필드가 없거나 최대 길이를 넘으면 400을 반환하는지 검증한다(해시 입력 크기를 제한해 DoS를 막는 검사).</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 이 테스트 전용 <see cref="HttpClient"/>만 사용하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 클라이언트·요청 본문·응답 각 2세트(누락·초과 길이).</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 두 요청을 순차 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task Login_MissingOrOversizedPassword_Returns400()
    {
        using var client = factory.CreateAdminClient(handleCookies: false);
        using var missing = await client.PostAsJsonAsync(Login, new { });
        using var oversized = await client.PostAsJsonAsync(Login, new { password = new string('a', 257) });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
    }

    /// <summary>익명 요청은 <c>/me</c>가 <c>Authenticated=false</c>를 반환하고, 로그인한 클라이언트는 <c>true</c>를 반환하는지 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 케이스 전용 클라이언트 2개만 사용하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 클라이언트 2개(익명·로그인)와 응답 각 1개.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 로그인·조회 요청을 순차 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task Me_ReflectsSession()
    {
        using var anonymous = factory.CreateAdminClient();
        Assert.False(await IsAuthenticatedAsync(anonymous));
        using var loggedIn = await factory.CreateLoggedInClientAsync();
        Assert.True(await IsAuthenticatedAsync(loggedIn));
    }

    /// <summary>세션 없이 보호된 엔드포인트(<c>/logout</c>)를 호출하면 로그인 페이지로 리다이렉트하지 않고 401을 그대로 반환하는지 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 이 테스트 전용 <see cref="HttpClient"/>만 사용하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 클라이언트·응답 각 1개.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 요청 완료를 <c>await</c>로 대기한다.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task ProtectedEndpoint_WithoutSession_Returns401_NotRedirect()
    {
        using var client = factory.CreateAdminClient();
        using var res = await client.PostAsync(Logout, null);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Null(res.Headers.Location);
    }

    /// <summary>로그아웃하면 로그아웃을 호출한 클라이언트뿐 아니라 그 전에 복사해 둔 세션 쿠키까지(다른 곳에 저장된 사본 포함)
    /// 서버 측 epoch 증가로 함께 무효화되는지 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 별도 클라이언트 3개(탈취 시나리오용 <c>probe</c>, 소유자용 <c>owner</c>)만 사용하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 클라이언트 2개와 쿠키 문자열 1개, 응답 여러 개.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 로그인·로그아웃·조회 요청을 순차 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task Logout_RevokesEverySession_IncludingCopiedCookies()
    {
        var stolen = await factory.LoginAndGetCookieAsync();       // 다른 곳에 복사해 둔 쿠키
        using var probe = factory.CreateAdminClient(handleCookies: false);
        Assert.True(await IsAuthenticatedAsync(probe, stolen));

        using var owner = await factory.CreateLoggedInClientAsync();
        using var res = await owner.PostAsync(Logout, null);
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        Assert.False(await IsAuthenticatedAsync(probe, stolen));   // epoch가 올라 서버가 거부한다
        Assert.False(await IsAuthenticatedAsync(owner));
    }

    /// <summary>세션이 절대 수명(12시간) 안에서는 활동이 있어도 연장되지 않고(sliding 없음), 수명을 넘기면 무효가 되는지
    /// 격리된 팩토리의 시계를 실제로 전진시켜 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 이 테스트 전용 <see cref="ApiFactory"/>(<c>isolated</c>)만 시계를 전진시키므로 다른 테스트의 시계와 섞이지 않는다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 격리된 팩토리·클라이언트·응답 각 1개.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 로그인·조회 요청을 순차 <c>await</c>하고, 시계 전진 자체는 동기 즉시 반환이다.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task Session_ExpiresAfterAbsoluteLifetime_NoSliding()
    {
        using var isolated = new ApiFactory(pg, new Dictionary<string, string?>()); // 시계를 돌리므로 다른 테스트와 호스트를 나눈다
        var cookie = await isolated.LoginAndGetCookieAsync();
        using var probe = isolated.CreateAdminClient(handleCookies: false);

        isolated.Clock.Advance(TimeSpan.FromHours(11));
        Assert.True(await IsAuthenticatedAsync(probe, cookie));    // 활동이 있어도 수명은 늘지 않는다
        isolated.Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        Assert.False(await IsAuthenticatedAsync(probe, cookie));
    }

    /// <summary>IP별 로그인 시도 한도를 초과하면(비밀번호가 맞아도) 429를 반환하고, 다른 허용 IP는 자기 몫의 별도 한도를 갖는지 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 이 테스트 전용 격리된 <see cref="ApiFactory"/>(<c>limited</c>)만 사용하므로 다른 테스트의 속도 제한 상태와 섞이지 않는다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 격리된 팩토리·클라이언트 각 1개와 반복 요청·응답.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 로그인 요청들을 순차 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task Login_IsRateLimitedPerIp()
    {
        using var limited = new ApiFactory(pg, new Dictionary<string, string?> { ["Admin:LoginPerIpPerMinute"] = "3" });
        using var client = limited.CreateAdminClient(handleCookies: false);
        for (var i = 0; i < 3; i++)
        {
            using var res = await client.PostAsJsonAsync(Login, new { password = "wrong" });
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        }
        using var fourth = await client.PostAsJsonAsync(Login, new { password = ApiFactory.Password });
        Assert.Equal(HttpStatusCode.TooManyRequests, fourth.StatusCode); // 맞는 비밀번호라도 한도 초과면 거부

        // 다른 허용 IP는 자기 몫의 한도를 가진다.
        client.DefaultRequestHeaders.Remove(RemoteIpStartupFilter.HeaderName);
        client.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, "203.0.113.200");
        using var other = await client.PostAsJsonAsync(Login, new { password = ApiFactory.Password });
        Assert.Equal(HttpStatusCode.NoContent, other.StatusCode);
    }

    /// <summary>전역 로그인 한도는 IP가 달라도 합산되어 적용되는지 검증한다(분산된 시도로 IP별 한도를 우회할 수 없음).</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 이 테스트 전용 격리된 <see cref="ApiFactory"/>(<c>limited</c>)만 사용하므로 다른 테스트의 속도 제한 상태와 섞이지 않는다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 격리된 팩토리·클라이언트 각 1개와 반복 요청·응답.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 로그인 요청들을 순차 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task Login_GlobalLimit_AppliesAcrossIps()
    {
        using var limited = new ApiFactory(pg, new Dictionary<string, string?> { ["Admin:LoginGlobalPerMinute"] = "2" });
        using var client = limited.CreateAdminClient(handleCookies: false);
        for (var i = 0; i < 3; i++)
        {
            client.DefaultRequestHeaders.Remove(RemoteIpStartupFilter.HeaderName);
            client.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, $"203.0.113.{10 + i}");
            using var res = await client.PostAsJsonAsync(Login, new { password = "wrong" });
            Assert.Equal(i < 2 ? HttpStatusCode.Unauthorized : HttpStatusCode.TooManyRequests, res.StatusCode);
        }
    }

    /// <summary>허용 IP 밖의 외부 요청은 <see cref="AdminSurfaceMiddleware"/>가 로그인 속도 제한기보다 먼저 403으로 거부하므로,
    /// 외부 요청이 작성자의 로그인 한도를 전혀 소진시키지 못하는지 검증한다(IP 검사가 속도 제한보다 앞).</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 이 테스트 전용 격리된 <see cref="ApiFactory"/>(<c>limited</c>)만 사용하므로 다른 테스트의 속도 제한 상태와 섞이지 않는다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 격리된 팩토리·클라이언트 2개(외부인·작성자)와 반복 요청·응답.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 로그인 요청들을 순차 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task Login_FromOutsiderIp_Returns403_AndDoesNotConsumeRateLimit()
    {
        using var limited = new ApiFactory(pg, new Dictionary<string, string?> { ["Admin:LoginGlobalPerMinute"] = "1" });
        using var outsider = limited.CreateAdminClient(handleCookies: false);
        outsider.DefaultRequestHeaders.Remove(RemoteIpStartupFilter.HeaderName);
        outsider.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, ApiFactory.OutsiderIp);
        for (var i = 0; i < 5; i++)
        {
            using var res = await outsider.PostAsJsonAsync(Login, new { password = ApiFactory.Password });
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }
        using var author = limited.CreateAdminClient(handleCookies: false);
        using var ok = await author.PostAsJsonAsync(Login, new { password = ApiFactory.Password });
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode); // 외부 요청이 작성자의 로그인 한도를 소진하지 못한다
    }

    /// <summary>경로 문자열 변형(끝 슬래시·대소문자)으로 로그인 엔드포인트에 도달해도 정규 경로와 같은 속도 제한 예산을 공유하는지 검증한다.
    /// 라우팅은 끝 슬래시·대소문자를 무시하고 같은 엔드포인트로 매칭하므로, 속도 제한기가 원본 문자열을 다시 비교하면 우회된다.</summary>
    /// <param name="variantPath">정규 경로(<see cref="Login"/>)와 다른 문자열이지만 같은 엔드포인트로 라우팅되는 경로.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 이 테스트 전용 격리된 <see cref="ApiFactory"/>(<c>limited</c>)만 사용하므로 다른 테스트의 속도 제한 상태와 섞이지 않는다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 격리된 팩토리·클라이언트 각 1개와 반복 요청·응답.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 로그인 요청들을 순차 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    [Theory]
    [InlineData("/api/auth/login/")]
    [InlineData("/API/Auth/LOGIN")]
    public async Task Login_RateLimit_CannotBeBypassedWithPathVariants(string variantPath)
    {
        using var limited = new ApiFactory(pg, new Dictionary<string, string?> { ["Admin:LoginPerIpPerMinute"] = "2" });
        using var client = limited.CreateAdminClient(handleCookies: false);
        for (var i = 0; i < 2; i++)
        {
            using var res = await client.PostAsJsonAsync(variantPath, new { password = "wrong" });
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        }
        using var third = await client.PostAsJsonAsync(variantPath, new { password = ApiFactory.Password });
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode); // 경로 변형으로도 예산을 우회할 수 없다

        // 정규 경로도 같은 예산을 공유한다(변형 경로가 별도 예산을 만들지 않는다).
        using var canonical = await client.PostAsJsonAsync(Login, new { password = ApiFactory.Password });
        Assert.Equal(HttpStatusCode.TooManyRequests, canonical.StatusCode);
    }

    /// <summary>요청 본문이 JSON 리터럴 <c>null</c>이어도 바인딩 예외(500)가 아니라 비밀번호 누락과 같은 400으로 처리되는지 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 이 테스트 전용 <see cref="HttpClient"/>만 사용하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 클라이언트·요청 본문·응답 각 1개.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 요청 완료를 <c>await</c>로 대기한다.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task Login_NullJsonBody_Returns400()
    {
        using var client = factory.CreateAdminClient(handleCookies: false);
        using var content = new StringContent("null", Encoding.UTF8, "application/json");
        using var res = await client.PostAsync(Login, content);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
