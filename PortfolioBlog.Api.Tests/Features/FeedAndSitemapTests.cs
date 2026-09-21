using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PortfolioBlog.Api.Infrastructure.Data;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>Atom·sitemap·robots: 항상 유효한 XML, 절대 URL은 PUBLIC_ORIGIN, 공개 호스트 전용.</summary>
[Collection("postgres")]
public sealed class FeedAndSitemapTests(PostgresContainerFixture pg)
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Sitemap = "http://www.sitemaps.org/schemas/sitemap/0.9";

    /// <summary>특수 문자·제어 문자가 든 제목이 있어도 피드는 유효한 XML이고, 항목 id는 글 Id 기반이며, 링크는 요청 헤더가 아니라 설정값을 쓴다.</summary>
    [Fact]
    public async Task Feed_IsValidAtom_EvenWithHostileTitles_AndUsesPublicOrigin()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?> { ["Site:Title"] = "블로그 <&>", ["Site:Author"] = "작성자" });
        var hostile = "제목 <&\"'> ]]> " + (char)1 + "끝";
        var post = await PublicSeed.PostAsync(factory, "hostile-title", hostile, summary: "요약 <b>");
        using var client = factory.CreatePublicClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "evil.test");

        using var res = await client.GetAsync("/feed.xml");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/atom+xml", res.Content.Headers.ContentType?.MediaType);
        var feed = XDocument.Parse(await res.Content.ReadAsStringAsync()).Root!;

        Assert.Equal(Atom + "feed", feed.Name);
        Assert.Equal("블로그 <&>", feed.Element(Atom + "title")?.Value);
        Assert.Equal(ApiFactory.PublicOrigin + "/", feed.Element(Atom + "id")?.Value);
        Assert.Equal("작성자", feed.Element(Atom + "author")?.Element(Atom + "name")?.Value);
        var entry = feed.Elements(Atom + "entry").Single();
        Assert.Equal("제목 <&\"'> ]]> 끝", entry.Element(Atom + "title")?.Value);
        Assert.Equal($"urn:uuid:{post.Id}", entry.Element(Atom + "id")?.Value);
        Assert.Equal("요약 <b>", entry.Element(Atom + "summary")?.Value);
        Assert.Matches(@"\A\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ\z", entry.Element(Atom + "published")!.Value);
        Assert.All(feed.Descendants(Atom + "link"), l => Assert.StartsWith(ApiFactory.PublicOrigin + "/", l.Attribute("href")!.Value, StringComparison.Ordinal));
        Assert.DoesNotContain("evil.test", feed.ToString(), StringComparison.Ordinal);
    }

    /// <summary>피드는 최신 20개뿐이고 글이 없어도 유효하다.</summary>
    [Fact]
    public async Task Feed_HasAtMostTwentyNewestEntries_AndIsValidWhenEmpty()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
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

    /// <summary>sitemap은 첫 쪽·글·태그·시리즈를 PUBLIC_ORIGIN 절대 URL로 싣고, 링크를 만들 수 없는 태그(".")와 글 없는 태그는 뺀다.</summary>
    [Fact]
    public async Task Sitemap_ListsEverything_WithEncodedAbsoluteUrls()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        var series = await PublicSeed.SeriesAsync(factory, "sm-series", "시리즈");
        await PublicSeed.PostAsync(factory, "sm-post", "글 <&>", tags: ["C#", "."], seriesId: series.Id, seriesOrder: 1);
        using var client = factory.CreatePublicClient();

        using var res = await client.GetAsync("/sitemap.xml");
        Assert.Equal("application/xml", res.Content.Headers.ContentType?.MediaType);
        var locs = XDocument.Parse(await res.Content.ReadAsStringAsync()).Root!.Elements(Sitemap + "url").Select(u => u.Element(Sitemap + "loc")!.Value).ToArray();

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
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
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
