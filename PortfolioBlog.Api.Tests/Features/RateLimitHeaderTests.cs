using System.Net;
using System.Net.Http.Json;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>429 응답이 재시도 시점을 알려 주는지 검증한다(관리 SPA가 무작정 재시도하지 않게).</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 테스트마다 격리된 <see cref="ApiFactory"/>(자체 DB·자체 제한기 상태)를 만든다.</description></item>
/// <item><description><b>Memory Allocation:</b> 팩토리·HttpClient는 <c>using</c>으로 해제.</description></item>
/// <item><description><b>Blocking:</b> 비동기. 실제 PostgreSQL 컨테이너에 접속한다.</description></item>
/// </list>
/// </remarks>
[Collection("postgres")]
public sealed class RateLimitHeaderTests(PostgresContainerFixture pg)
{
    /// <summary>고정 창(1분) 한도를 넘긴 로그인은 429와 함께 1~60초의 Retry-After를 받는다.</summary>
    [Fact]
    public async Task LoginOverLimit_Returns429_WithRetryAfterSeconds()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?> { ["Admin:LoginPerIpPerMinute"] = "1" });
        using var client = factory.CreateAdminClient(handleCookies: false);
        using var first = await client.PostAsJsonAsync("/api/auth/login", new { password = "wrong-dummy-value" });
        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);

        using var second = await client.PostAsJsonAsync("/api/auth/login", new { password = "wrong-dummy-value" });
        Assert.Equal((HttpStatusCode)429, second.StatusCode);
        Assert.True(second.Headers.TryGetValues("Retry-After", out var values), "Retry-After 헤더가 없다.");
        var seconds = int.Parse(values!.Single(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(seconds, 1, 60);
    }
}
