using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using PortfolioBlog.Api.Domain;
using PortfolioBlog.Api.Infrastructure.Data;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>Atom·sitemap·robots: 항상 유효한 XML, 절대 URL은 PUBLIC_ORIGIN, 공개 호스트 전용.</summary>
/// <param name="mysql">컬렉션이 공유하는 MySQL 컨테이너 fixture. 테스트 메서드마다 설정(사이트 타이틀 등)이 달라야 하므로 클래스 픽스처 대신 메서드마다 <see cref="ApiFactory"/>를 직접 만든다.</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 실행된다. 각 테스트가 만드는 <see cref="ApiFactory"/>가 호스팅하는 TestServer는 <paramref name="mysql"/>가 가리키는 실제 MySQL 컨테이너에 TCP로 접속한다.</description></item>
/// <item><description><b>Memory Policy:</b> 테스트 메서드마다 <see cref="ApiFactory"/>를 새로 생성해 <c>using</c>으로 해제한다(클래스 픽스처를 공유하지 않음 — 메서드마다 <c>Site:*</c> 설정 오버라이드가 달라 각자 독립된 DB·호스트가 필요하다).</description></item>
/// <item><description><b>Concurrency:</b> <c>[Collection("mysql")]</c>라 같은 컬렉션의 다른 테스트 클래스와 컨테이너를 공유하지만 xUnit이 컬렉션 안에서는 직렬 실행한다. 이 클래스 자체는 테스트마다 독립된 DB를 쓰므로 데이터 간섭이 없다.</description></item>
/// <item><description><b>Blocking:</b> 모든 HTTP·DB 접근은 <c>await</c>로 비동기 대기하며 동기 블로킹이 없다.</description></item>
/// </list>
/// </remarks>
[Collection("mysql")]
public sealed class FeedAndSitemapTests(MySqlContainerFixture mysql)
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Sitemap = "http://www.sitemaps.org/schemas/sitemap/0.9";

    /// <summary>요청을 보내고 응답 본문을 바이트로 읽어(BOM 검사용) UTF-8 문자열로 디코딩한다. <c>HttpContent</c>는 한 번만 읽을 수 있으므로
    /// XML 파싱용 문자열과 BOM 검사용 바이트를 각각 따로 읽지 않고 바이트를 한 번만 읽어 재사용한다.</summary>
    private static async Task<(HttpResponseMessage Response, byte[] Bytes)> GetBytesAsync(HttpClient client, string path, string? hostHeader = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (hostHeader is not null) request.Headers.Host = hostHeader;
        var res = await client.SendAsync(request);
        return (res, await res.Content.ReadAsByteArrayAsync());
    }

    /// <summary><paramref name="bytes"/>가 UTF-8 BOM(<c>0xEF 0xBB 0xBF</c>)으로 시작하지 않는지 검증한다.</summary>
    private static void AssertNoBom(byte[] bytes) =>
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "응답 본문이 UTF-8 BOM으로 시작한다.");

    /// <summary>사이트 설정(제목·설명·작성자)과 글 제목·요약 전부에 특수 문자·제어 문자가 섞여도 피드는 유효한 XML이고, 항목 id는 글 Id 기반이며,
    /// 절대 URL은 포트가 다른 요청 Host가 와도 요청 헤더가 아니라 설정값(PUBLIC_ORIGIN)을 쓴다.</summary>
    [Fact]
    public async Task Feed_IsValidAtom_EvenWithHostileTitles_AndUsesPublicOrigin()
    {
        using var factory = new ApiFactory(mysql, new Dictionary<string, string?>
        {
            ["Site:Title"] = "블로그 <&>" + (char)1,
            ["Site:Description"] = "소개 <i>" + (char)2,
            ["Site:Author"] = "작성자" + (char)3,
        });
        var hostile = "제목 <&\"'> ]]> " + (char)1 + "끝";
        var post = await PublicSeed.PostAsync(factory, "hostile-title", hostile, summary: "요약 <b>" + (char)2 + "끝");
        using var client = factory.CreatePublicClient();
        // RequireHost는 포트가 붙은 Host도 매칭한다(실측: 이 요청도 200) — 절대 URL이 이 요청 Host가 아니라 설정값을 쓰는지는
        // 응답에 ":8443"이 없는지로 판별한다(BaseAddress == PublicOrigin이라 단순 StartsWith(PublicOrigin)만으로는 요청 Host를
        // 그대로 반사해도 우연히 같은 문자열이 나와 판별력이 없다).
        var (res, bytes) = await GetBytesAsync(client, "/feed.xml", "blog.test:8443");
        using var _ = res; // 단언이 중간에 던져도 해제되도록 튜플 분해 직후 using으로 묶는다.

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/atom+xml", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal("public, max-age=300", res.Headers.CacheControl?.ToString());
        AssertNoBom(bytes);
        var feedText = Encoding.UTF8.GetString(bytes);
        var feed = XDocument.Parse(feedText).Root!;

        Assert.Equal(Atom + "feed", feed.Name);
        Assert.Equal("블로그 <&>", feed.Element(Atom + "title")?.Value);
        Assert.Equal("소개 <i>", feed.Element(Atom + "subtitle")?.Value);
        Assert.Equal(ApiFactory.PublicOrigin + "/", feed.Element(Atom + "id")?.Value);
        Assert.Equal("작성자", feed.Element(Atom + "author")?.Element(Atom + "name")?.Value);
        var entry = feed.Elements(Atom + "entry").Single();
        Assert.Equal("제목 <&\"'> ]]> 끝", entry.Element(Atom + "title")?.Value);
        Assert.Equal($"urn:uuid:{post.Id}", entry.Element(Atom + "id")?.Value);
        Assert.Equal("요약 <b>끝", entry.Element(Atom + "summary")?.Value);
        Assert.Matches(@"\A\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ\z", entry.Element(Atom + "published")!.Value);
        Assert.All(feed.Descendants(Atom + "link"), l => Assert.StartsWith(ApiFactory.PublicOrigin + "/", l.Attribute("href")!.Value, StringComparison.Ordinal));
        Assert.DoesNotContain(":8443", feedText, StringComparison.Ordinal);
    }

    /// <summary>피드는 최신 20개뿐이고 글이 없어도 유효하다.</summary>
    [Fact]
    public async Task Feed_HasAtMostTwentyNewestEntries_AndIsValidWhenEmpty()
    {
        using var factory = new ApiFactory(mysql, new Dictionary<string, string?>());
        using var client = factory.CreatePublicClient();
        var empty = XDocument.Parse(await client.GetStringAsync("/feed.xml")).Root!;
        Assert.Empty(empty.Elements(Atom + "entry"));
        Assert.NotNull(empty.Element(Atom + "updated"));

        var t0 = DbClock.UtcNow().AddDays(-1);
        for (var i = 0; i < 22; i++) await PublicSeed.PostAsync(factory, $"f-{i:00}", $"피드 {i}", createdAt: t0.AddMinutes(i));
        var feed = XDocument.Parse(await client.GetStringAsync("/feed.xml")).Root!;
        var titles = feed.Elements(Atom + "entry").Select(e => e.Element(Atom + "title")!.Value).ToArray();
        Assert.Equal(20, titles.Length);
        Assert.Equal("피드 21", titles[0]);
    }

    /// <summary>sitemap은 첫 쪽·글·태그·시리즈를 PUBLIC_ORIGIN 절대 URL로 싣고, 링크를 만들 수 없는 태그(".")와 글이 하나도 없는 태그는 뺀다.
    /// 포트가 다른 요청 Host가 와도 절대 URL은 설정값을 쓴다.</summary>
    [Fact]
    public async Task Sitemap_ListsEverything_WithEncodedAbsoluteUrls()
    {
        using var factory = new ApiFactory(mysql, new Dictionary<string, string?>());
        var series = await PublicSeed.SeriesAsync(factory, "sm-series", "시리즈");
        await PublicSeed.PostAsync(factory, "sm-post", "글 <&>", tags: ["C#", "."], seriesId: series.Id, seriesOrder: 1);
        // 글이 하나도 연결되지 않은 태그를 관리 컨텍스트로 직접 저장한다: 아래 정확 목록 단언에 이 태그가 없어야 "글 없는 태그 제외"가
        // 실제로 고정된다(이 태그를 넣지 않으면 그 필터를 지워도 이 테스트는 여전히 통과한다 — 판별력이 없는 상태였다).
        await using (var scope = factory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Tags.Add(new Tag { Name = "orphan-tag", NormalizedName = "orphan-tag" });
            await db.SaveChangesAsync();
        }
        using var client = factory.CreatePublicClient();
        var (res, bytes) = await GetBytesAsync(client, "/sitemap.xml", "blog.test:8443");
        using var _ = res; // 단언이 중간에 던져도 해제되도록 튜플 분해 직후 using으로 묶는다.

        Assert.Equal("application/xml", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal("public, max-age=300", res.Headers.CacheControl?.ToString());
        AssertNoBom(bytes);
        var sitemapText = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain(":8443", sitemapText, StringComparison.Ordinal);
        var locs = XDocument.Parse(sitemapText).Root!.Elements(Sitemap + "url").Select(u => u.Element(Sitemap + "loc")!.Value).ToArray();

        Assert.Equal(new[]
        {
            ApiFactory.PublicOrigin + "/", ApiFactory.PublicOrigin + "/posts/sm-post",
            ApiFactory.PublicOrigin + "/tags/c%23", ApiFactory.PublicOrigin + "/series/sm-series",
        }, locs);
    }

    /// <summary>robots.txt는 sitemap 위치를 PUBLIC_ORIGIN으로 알린다. 세 경로 모두 관리 호스트에서는 404다.</summary>
    [Fact]
    public async Task Robots_PointsToTheSitemap_AndAllThreeArePublicHostOnly()
    {
        using var factory = new ApiFactory(mysql, new Dictionary<string, string?>());
        using var client = factory.CreatePublicClient();
        using var robots = await client.GetAsync("/robots.txt");
        Assert.Equal("text/plain", robots.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"User-agent: *\nAllow: /\nSitemap: {ApiFactory.PublicOrigin}/sitemap.xml\n", await robots.Content.ReadAsStringAsync());

        using var admin = factory.CreateAdminClient();
        foreach (var path in new[] { "/feed.xml", "/sitemap.xml", "/robots.txt" })
        {
            using var res = await admin.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }
    }
}
