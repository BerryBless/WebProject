using System.Net;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PortfolioBlog.Api.Infrastructure.Web;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>공개 검색: 입력 경계, 와일드카드 리터럴 처리, 반사 인코딩, 자원 한도.</summary>
/// <param name="factory">컬렉션이 공유하는 컨테이너를 바탕으로 클래스 전용 DB를 갖는 <see cref="ApiFactory"/> 클래스 픽스처.</param>
/// <param name="mysql">컬렉션이 공유하는 MySQL 컨테이너 fixture. 속도 제한 테스트가 격리된 <see cref="ApiFactory"/>를 새로 만들 때 재사용한다.</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 실행된다. <paramref name="factory"/>가 호스팅하는 TestServer가 실제 MySQL 컨테이너에 TCP로 접속한다.</description></item>
/// <item><description><b>Memory Policy:</b> <paramref name="factory"/>는 <see cref="IClassFixture{TFixture}"/>로 클래스 단위 1회 생성·공유된다.
/// <see cref="Search_HasItsOwnRateLimit"/>만 속도 제한 한도를 격리하려고 새 <see cref="ApiFactory"/>를 만들어 <c>using</c>으로 해제한다 — 다른 테스트 메서드와 한도·DB를 공유하지 않는다.</description></item>
/// <item><description><b>Concurrency:</b> 공유 <paramref name="factory"/>를 쓰는 테스트 메서드들은 서로 다른 slug를 시드해 데이터가 섞이지 않는다.</description></item>
/// <item><description><b>Blocking:</b> 모든 HTTP·DB 접근은 <c>await</c>로 비동기 대기하며 동기 블로킹이 없다.</description></item>
/// </list>
/// </remarks>
[Collection("mysql")]
public sealed class SearchPageTests(ApiFactory factory, MySqlContainerFixture mysql) : IClassFixture<ApiFactory>
{
    /// <summary>검색어 없이 열면 빈 폼이다. GET 폼이며 숨은 필드(antiforgery 토큰)가 없다.</summary>
    [Fact]
    public async Task Empty_ShowsTheFormOnly()
    {
        using var client = factory.CreatePublicClient();
        var doc = await HtmlDoc.GetAsync(client, "/search");
        var form = doc.QuerySelector("form.search-form")!;
        Assert.Equal("get", form.GetAttribute("method"));
        Assert.Equal("/search", form.GetAttribute("action"));
        Assert.Empty(form.QuerySelectorAll("input[type=hidden]"));
        Assert.Null(doc.QuerySelector("ul.post-list"));
        Assert.Equal("noindex", doc.QuerySelector("meta[name=robots]")?.GetAttribute("content"));
    }

    /// <summary>제목·요약·본문에서 찾고, % 와 _ 는 글자 그대로다.</summary>
    [Fact]
    public async Task Finds_InTitleSummaryAndBody_WithLiteralWildcards()
    {
        await PublicSeed.PostAsync(factory, "s-title", "검색대상제목 100% 완료");
        await PublicSeed.PostAsync(factory, "s-summary", "다른 글", summary: "요약 속 검색대상요약");
        await PublicSeed.PostAsync(factory, "s-body", "또 다른 글 100 완료", markdown: "본문 속 검색대상본문 a_b");
        using var client = factory.CreatePublicClient();

        async Task<string[]> SlugsAsync(string q) =>
            (await HtmlDoc.GetAsync(client, "/search?q=" + Uri.EscapeDataString(q)))
                .QuerySelectorAll("ul.post-list a.post-title").Select(a => a.GetAttribute("href")!).Order().ToArray();

        Assert.Equal(new[] { "/posts/s-title" }, await SlugsAsync("검색대상제목"));
        Assert.Equal(new[] { "/posts/s-summary" }, await SlugsAsync("검색대상요약"));
        Assert.Equal(new[] { "/posts/s-body" }, await SlugsAsync("검색대상본문"));
        Assert.Equal(new[] { "/posts/s-title" }, await SlugsAsync("100%"));   // %가 와일드카드면 s-body도 나온다
        Assert.Equal(new[] { "/posts/s-body" }, await SlugsAsync("a_b"));
        Assert.Empty(await SlugsAsync("%%"));
    }

    /// <summary>검색어는 입력란에 인코딩되어 되돌아올 뿐, 요소가 되지 않는다.</summary>
    [Fact]
    public async Task Query_IsReflectedOnlyAsAnEncodedInputValue()
    {
        const string q = "<script>alert(1)</script>\"><img src=x onerror=alert(1)>";
        using var client = factory.CreatePublicClient();
        var doc = await HtmlDoc.GetAsync(client, "/search?q=" + Uri.EscapeDataString(q));
        Assert.Equal(q, doc.QuerySelector("form.search-form input[name=q]")?.GetAttribute("value"));
        Assert.Empty(doc.QuerySelectorAll("script, img, [onerror]"));
    }

    /// <summary>경계 밖 검색어는 400(안내문), 쪽 번호 오류는 404. 길이 초과·NUL 값은 입력란에 되돌리지 않는다.</summary>
    /// <param name="url">요청할 경로(쿼리 문자열 포함).</param>
    /// <param name="status">기대하는 응답 상태 코드.</param>
    /// <param name="echoed">400일 때 입력란에 남아야 할 값. <c>null</c>이면 이 요청은 400이 아니라 404이므로 입력란 단언을 건너뛴다.</param>
    [Theory]
    [InlineData("/search?q=a", 400, "a")]
    [InlineData("/search?q=%20a%20", 400, "a")]
    [InlineData("/search?q=ab&q=cd", 400, "")]
    [InlineData("/search?q=ab%00cd", 400, "")]
    [InlineData("/search?q=zzqq&page=51", 404, null)] // 형식(상한 51 초과)만 확인한다 — zzqq는 결과 0건이라 상한 검사가 사라져도 빈 쪽 폴백이 같은 404를 낸다.
                                                        // 상한이 DB 접근 전에 강제됨은 이 케이스로 증명되지 않는다 — 증명은 Page51_IsRejectedByTheCap_NotByEmptyFallback.
    [InlineData("/search?q=zzqq&page=x", 404, null)]
    [InlineData("/search?q=zzqq&page=2", 404, null)] // 결과가 없는 쪽
    public async Task InvalidInput(string url, int status, string? echoed)
    {
        using var client = factory.CreatePublicClient();
        var doc = await HtmlDoc.GetAsync(client, url, (HttpStatusCode)status);
        if (echoed is null) return;
        Assert.NotNull(doc.QuerySelector("p.notice"));
        Assert.Equal(echoed, doc.QuerySelector("form.search-form input[name=q]")?.GetAttribute("value"));
    }

    /// <summary>100자는 되고 101자는 400이며 되돌리지 않는다.</summary>
    [Fact]
    public async Task LengthBoundary()
    {
        using var client = factory.CreatePublicClient();
        await HtmlDoc.GetAsync(client, "/search?q=" + new string('z', 100));
        var tooLong = await HtmlDoc.GetAsync(client, "/search?q=" + new string('z', 101), HttpStatusCode.BadRequest);
        Assert.Equal(string.Empty, tooLong.QuerySelector("form.search-form input[name=q]")?.GetAttribute("value"));
    }

    /// <summary>검색 페이지 엔드포인트에 검색 정책이 붙어 있고(파일 이름이 바뀌어 규약이 빗나가면 실패), 한도를 넘으면 429 HTML + Retry-After다. 다른 페이지는 영향이 없다.</summary>
    [Fact]
    public async Task Search_HasItsOwnRateLimit()
    {
        using var isolated = new ApiFactory(mysql, new Dictionary<string, string?> { ["Public:SearchPerIpPerMinute"] = "2" });
        using var client = isolated.CreatePublicClient();
        var endpoint = isolated.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().Single(e => e.RoutePattern.RawText == "search");
        Assert.Equal(RateLimitPolicy.Search, endpoint.Metadata.GetMetadata<RateLimitMetadata>()?.Policy);

        await HtmlDoc.GetAsync(client, "/search?q=ab");
        await HtmlDoc.GetAsync(client, "/search?q=ab");
        using var third = await client.GetAsync("/search?q=ab");
        Assert.Equal((HttpStatusCode)429, third.StatusCode);
        Assert.True(third.Headers.Contains("Retry-After"));
        Assert.Equal("text/html", third.Content.Headers.ContentType?.MediaType);
        await HtmlDoc.GetAsync(client, "/"); // 페이지 한도는 남아 있다
    }

    /// <summary>쪽 번호 상한 50이 DB 접근 전에 강제됨을, 51쪽에 실제로 항목이 존재하는 상태로 증명한다(50쪽=200/20건, 51쪽=404).
    /// 결과가 0건인 검색어로는 상한 검사가 사라져도 "결과 없는 쪽" 폴백이 같은 404를 내 구별하지 못하므로, 50쪽 × 20 + 1건 = 1,001건을 시드해
    /// 51쪽에 정확히 1건이 남도록 만든다 — 상한이 없다면 이 요청은 404가 아니라 200(1건)이 된다.</summary>
    [Fact]
    public async Task Page51_IsRejectedByTheCap_NotByEmptyFallback()
    {
        using var isolated = new ApiFactory(mysql, new Dictionary<string, string?>());
        const string term = "zzz-bulk-term";
        await PublicSeed.ManyPostsAsync(isolated, 1001, term);
        using var client = isolated.CreatePublicClient();

        var last = await HtmlDoc.GetAsync(client, "/search?q=" + term + "&page=50");
        Assert.Equal(20, last.QuerySelectorAll("ul.post-list a.post-title").Length);

        using var res = await client.GetAsync("/search?q=" + term + "&page=51");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    /// <summary>쪽 링크는 검색어를 퍼센트 인코딩해 내보낸다. 위험 문자(따옴표·꺾쇠·앰퍼샌드·공백·해시)가 든 검색어도 href를 깨거나
    /// script·img·이벤트 속성 같은 새 요소를 만들지 않는다.</summary>
    [Fact]
    public async Task PagerLinks_EncodeTheQueryString_AndReflectNoElements()
    {
        using var isolated = new ApiFactory(mysql, new Dictionary<string, string?>());
        const string term = "a\"b<c&d #e";
        await PublicSeed.ManyPostsAsync(isolated, 21, term);
        using var client = isolated.CreatePublicClient();
        var encoded = Uri.EscapeDataString(term);

        var first = await HtmlDoc.GetAsync(client, "/search?q=" + encoded);
        Assert.Equal(20, first.QuerySelectorAll("ul.post-list a.post-title").Length);
        Assert.Empty(first.QuerySelectorAll("script, img, [onerror]"));
        Assert.Equal($"/search?q={encoded}&page=2", first.QuerySelector("nav.pager a[rel=next]")?.GetAttribute("href"));

        var second = await HtmlDoc.GetAsync(client, $"/search?q={encoded}&page=2");
        Assert.Equal(1, second.QuerySelectorAll("ul.post-list a.post-title").Length);
        Assert.Empty(second.QuerySelectorAll("script, img, [onerror]"));
        Assert.Equal($"/search?q={encoded}&page=1", second.QuerySelector("nav.pager a[rel=prev]")?.GetAttribute("href"));
    }
}
