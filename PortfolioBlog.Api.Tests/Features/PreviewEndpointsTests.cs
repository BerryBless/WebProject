using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>미리보기 엔드포인트 통합 테스트. 공개 페이지와 같은 렌더러를 쓰는지, 입력 제한과 속도 제한이 걸리는지 본다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 클래스 픽스처 팩토리를 공유하되 속도 제한 테스트는 격리된 팩토리를 만든다(제한기 상태가 다른 테스트를 막지 않게).</description></item>
/// <item><description><b>Memory Allocation:</b> 가장 큰 요청 본문은 약 200KB.</description></item>
/// <item><description><b>Blocking:</b> 비동기. 실제 PostgreSQL 컨테이너(세션 검증)에 접속한다.</description></item>
/// </list>
/// </remarks>
[Collection("postgres")]
public sealed class PreviewEndpointsTests(ApiFactory factory, PostgresContainerFixture pg) : IClassFixture<ApiFactory>
{
    /// <summary>마크다운을 렌더링해 돌려주고, 위험한 입력은 공개 페이지와 똑같이 중화된다.</summary>
    [Fact]
    public async Task Preview_RendersSanitizedHtml()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        using var res = await client.PostAsJsonAsync("/api/preview", new PreviewRequest("# 제목\n\n[x](javascript:alert(1)) <script>alert(1)</script>"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = (await res.Content.ReadFromJsonAsync<PreviewResponse>(TestJson.Options))!.Html;
        Assert.Contains("<h1", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<a", html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>누락·NUL·크기 초과는 필드 키 <c>markdown</c>의 400이다(500이 아니다).</summary>
    [Fact]
    public async Task Preview_InvalidInput_Returns400()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        object[] bodies = [new { }, new PreviewRequest("본문\0"), new PreviewRequest(new string('가', 70_000))]; // '가' 3바이트 × 70,000 > 204,800
        foreach (var body in bodies)
        {
            using var res = await client.PostAsJsonAsync("/api/preview", body);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            var problem = await res.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(TestJson.Options);
            Assert.Contains("markdown", problem!.Errors.Keys);
        }
    }

    /// <summary>빈 본문은 허용한다(에디터를 막 열었을 때).</summary>
    [Fact]
    public async Task Preview_EmptyMarkdown_ReturnsEmptyHtml()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        using var res = await client.PostAsJsonAsync("/api/preview", new PreviewRequest(""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(string.Empty, (await res.Content.ReadFromJsonAsync<PreviewResponse>(TestJson.Options))!.Html.Trim());
    }

    /// <summary>세션이 없으면 401 — 렌더러는 CPU를 쓰므로 익명으로 열어 두지 않는다.</summary>
    [Fact]
    public async Task Preview_WithoutSession_Returns401()
    {
        using var client = factory.CreateAdminClient();
        using var res = await client.PostAsJsonAsync("/api/preview", new PreviewRequest("x"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    /// <summary>분당 한도를 넘으면 429 + Retry-After. 경로 변형으로 우회되지 않는다(엔드포인트 메타데이터로 판정).</summary>
    [Fact]
    public async Task Preview_IsRateLimited_AndPathVariantsShareTheBudget()
    {
        using var limited = new ApiFactory(pg, new Dictionary<string, string?> { ["Admin:PreviewPerMinute"] = "2" });
        using var client = await limited.CreateLoggedInClientAsync();
        using var first = await client.PostAsJsonAsync("/api/preview", new PreviewRequest("a"));
        using var second = await client.PostAsJsonAsync("/API/Preview/", new PreviewRequest("b"));
        using var third = await client.PostAsJsonAsync("/api/preview", new PreviewRequest("c"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal((HttpStatusCode)429, third.StatusCode);
        Assert.True(third.Headers.Contains("Retry-After"));
    }

    /// <summary>중첩이 너무 깊은 입력은 500이 아니라 필드 키 <c>markdown</c>의 400이다(<see cref="PortfolioBlog.Api.Infrastructure.Markdown.MarkdownTooComplexException"/>을 잡아서 변환).</summary>
    [Fact]
    public async Task Preview_TooDeeplyNested_Returns400()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        // Markdig 1.4.0 중첩 한도(128) 초과, 크기 상한(200KB)은 훨씬 밑돈다
        using var res = await client.PostAsJsonAsync("/api/preview", new PreviewRequest(new string('[', 200) + "x"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var problem = await res.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(TestJson.Options);
        Assert.Contains("markdown", problem!.Errors.Keys);
    }
}
