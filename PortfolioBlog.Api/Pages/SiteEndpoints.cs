using System.Text;
using System.Xml;
using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Infrastructure.Access;
using PortfolioBlog.Api.Infrastructure.Data;
using PortfolioBlog.Api.Infrastructure.Markdown;
using PortfolioBlog.Api.Infrastructure.Web;
// System.Xml도 XmlText라는 타입(텍스트 노드)을 선언해 이름이 겹친다 — using 별칭으로 우리 쪽 정리 함수를 명확히 가리킨다.
using XmlText = PortfolioBlog.Api.Infrastructure.Web.XmlText;

namespace PortfolioBlog.Api.Pages;

/// <summary>HTML이 아닌 공개 엔드포인트. 전부 GET/HEAD·공개 호스트 전용이며 <c>AccessMatrixTests.PublicAllowlist</c>에 올라 있다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 정적 클래스이며 등록되는 핸들러는 요청 컨텍스트만 다룬다.</description></item>
/// <item><description><b>Memory Allocation:</b> 등록 자체는 시작 시 1회. 핸들러 호출당 추가 할당은 <see cref="MapPublicSiteEndpoints"/> 안 각 핸들러 문서 참조.</description></item>
/// <item><description><b>Blocking:</b> 강조 CSS·robots 핸들러는 즉시 반환(Non-blocking, I/O 없음 — <see cref="HighlightCss.Value"/>는 지연 계산된 캐시 문자열을 반환할 뿐이다).
/// 피드·sitemap 핸들러는 그렇지 않다 — DB 조회를 <c>await</c>한다(<see cref="FeedAsync"/>는 SELECT 1문장, <see cref="SitemapAsync"/>는 3문장을 순차 실행, 각 핸들러 문서 참조).</description></item>
/// </list>
/// </remarks>
public static class SiteEndpoints
{
    /// <summary>코드 강조 CSS 경로.</summary>
    public const string HighlightCssPattern = "/css/highlight.css";

    /// <summary>Atom 1.0 피드 경로.</summary>
    public const string FeedPattern = "/feed.xml";

    /// <summary>sitemap 경로.</summary>
    public const string SitemapPattern = "/sitemap.xml";

    /// <summary>robots.txt 경로.</summary>
    public const string RobotsPattern = "/robots.txt";

    /// <summary>Atom 1.0 XML 네임스페이스 URI.</summary>
    private const string AtomNamespace = "http://www.w3.org/2005/Atom";

    /// <summary>sitemap.xml(sitemaps.org 프로토콜 0.9) XML 네임스페이스 URI.</summary>
    private const string SitemapNamespace = "http://www.sitemaps.org/schemas/sitemap/0.9";

    // string[]: 이 클래스의 네 엔드포인트가 전부 GET/HEAD만 받으므로, MapMethods 호출마다 배열 리터럴을 새로 할당하지 않고
    // 시작 시 1회 만든 배열 참조를 공유한다(등록은 앱 시작 시 1회뿐이라 절약 효과는 크지 않지만, 등록 지점이 늘어날수록 의미가 커진다).
    private static readonly string[] GetAndHead = ["GET", "HEAD"];

    // XmlWriterSettings: Encoding만 지정하고 Async는 켜지 않았다 — 이 설정으로 만든 XmlWriter는 동기 Write/Flush 전용이다.
    // UTF8Encoding(false): BOM 없이 직렬화한다(Atom·sitemap 스펙은 BOM을 요구하지 않고, 일부 파서는 BOM을 데이터의 일부로 오인한다).
    // CheckCharacters는 기본값(true)을 그대로 둔다 — XmlText.Clean이 무효 문자를 놓쳐도 깨진 XML이 나가는 대신 여기서 예외가 난다.
    private static readonly XmlWriterSettings XmlSettings = new() { Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false) };

    /// <summary>강조 CSS·Atom 피드·sitemap·robots GET/HEAD 엔드포인트를 등록한다.</summary>
    /// <param name="app">엔드포인트를 등록할 <see cref="WebApplication"/>.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 앱 시작 시 단일 스레드에서 1회 호출된다. 등록된 핸들러는 여러 요청 스레드가 동시에 호출해도 안전하다(무상태, 각 핸들러가 요청 스코프 의존성만 쓴다).</description></item>
    /// <item><description><b>Memory Allocation:</b> 등록 자체는 시작 시 1회성 할당뿐이다. 요청별 할당은 각 핸들러 문서 참조.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public static void MapPublicSiteEndpoints(this WebApplication app)
    {
        var publicHost = SiteOptions.HostOf(app.Services.GetRequiredService<IOptions<SiteOptions>>().Value.PublicOrigin);

        app.MapMethods(HighlightCssPattern, GetAndHead, static (HttpContext http) =>
            {
                http.Response.Headers.CacheControl = "public, max-age=86400";
                return TypedResults.Text(HighlightCss.Value, "text/css", Encoding.UTF8);
            })
            .RequireHost(publicHost).AllowAnonymous()
            .WithMetadata(new RateLimitMetadata(RateLimitPolicy.PublicAsset)).WithName("GetHighlightCss");

        app.MapMethods(FeedPattern, GetAndHead, FeedAsync).RequireHost(publicHost).AllowAnonymous()
            .WithMetadata(new RateLimitMetadata(RateLimitPolicy.PublicPage)).WithName("GetFeed");
        app.MapMethods(SitemapPattern, GetAndHead, SitemapAsync).RequireHost(publicHost).AllowAnonymous()
            .WithMetadata(new RateLimitMetadata(RateLimitPolicy.PublicPage)).WithName("GetSitemap");
        app.MapMethods(RobotsPattern, GetAndHead, static (IOptions<SiteOptions> site) =>
                TypedResults.Text($"User-agent: *\nAllow: /\nSitemap: {site.Value.PublicOrigin}{SitemapPattern}\n", "text/plain", Encoding.UTF8))
            .RequireHost(publicHost).AllowAnonymous()
            .WithMetadata(new RateLimitMetadata(RateLimitPolicy.PublicAsset)).WithName("GetRobots");
    }

    /// <summary>Atom 1.0 최신 <see cref="PublicQueries.FeedSize"/>개를 직렬화한다. 항목 <c>id</c>는 글의 내부 Id 기반 URN이라
    /// slug·도메인이 바뀌어도 구독기가 항목을 같은 것으로 계속 인식할 수 있다(설계 의도, 구독기별 실제 동작은 검증하지 않았다).</summary>
    /// <param name="http">응답 헤더(캐시 제어)를 설정할 현재 요청 컨텍스트.</param>
    /// <param name="db">글 목록 조회에 쓸 공개 전용 컨텍스트.</param>
    /// <param name="siteOptions">피드 제목·설명·작성자·절대 URL 기준(<see cref="SiteOptions.PublicOrigin"/>).</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>Atom XML을 담은 200.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> ASP.NET Core 요청 파이프라인 스레드에서 호출된다. <paramref name="db"/>는 요청 스코프 전용이다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 최대 <see cref="PublicQueries.FeedSize"/>건의 <see cref="PublicFeedEntry"/> 리스트 + 직렬화용 <see cref="MemoryStream"/> 버퍼(수십 KB) + 최종 <c>byte[]</c> 1개(<c>ToArray()</c>가 복사본을 만든다).</description></item>
    /// <item><description><b>Blocking:</b> DB 조회는 <c>await</c>로 비동기 대기한다. XML 직렬화 자체는 동기 CPU 작업이다(<see cref="XmlSettings"/>가 <c>Async</c>를 켜지 않았으므로 이 <see cref="XmlWriter"/>는 애초에 비동기 Write를 지원하지 않는다) — 다만 메모리 버퍼에만 쓰고 항목 수가 최대 20개로 작아 즉시 끝난다(I/O 대기 없음).</description></item>
    /// </list>
    /// </remarks>
    private static async Task<IResult> FeedAsync(HttpContext http, PublicDbContext db, IOptions<SiteOptions> siteOptions, CancellationToken ct)
    {
        var site = siteOptions.Value;
        var entries = await PublicQueries.FeedAsync(db, ct);

        // MemoryStream: XmlSettings가 Async를 켜지 않아 이 XmlWriter는 동기 Write/Flush 전용이다. 응답 스트림에 직접 동기로 쓰는 대신
        // 메모리에 전부 완성한 뒤 TypedResults.Bytes로 한 번에 돌려준다(피드는 최대 20개 항목이라 버퍼가 수십 KB를 넘지 않는다).
        using var buffer = new MemoryStream();
        using (var xml = XmlWriter.Create(buffer, XmlSettings))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("feed", AtomNamespace);
            xml.WriteElementString("title", AtomNamespace, XmlText.Clean(site.Title));
            if (!string.IsNullOrWhiteSpace(site.Description)) xml.WriteElementString("subtitle", AtomNamespace, XmlText.Clean(site.Description));
            xml.WriteElementString("id", AtomNamespace, site.PublicOrigin + "/");
            WriteLink(xml, "self", "application/atom+xml", site.PublicOrigin + FeedPattern);
            WriteLink(xml, "alternate", "text/html", site.PublicOrigin + "/");
            // 글이 없을 때도 필수 요소인 updated가 있어야 한다: 고정값(유닉스 기원)을 쓴다 — "지금"을 쓰면 글이 하나도 안 바뀌어도
            // 요청마다 값이 달라져 피드가 매번 "새로 바뀐 것"처럼 보인다(응답을 두 번 부르면 값이 다르다는 것 자체는 자명하다).
            xml.WriteElementString("updated", AtomNamespace, PublicFormat.Rfc3339(entries.Count == 0 ? DateTimeOffset.UnixEpoch : entries.Max(e => e.UpdatedAt)));
            xml.WriteStartElement("author", AtomNamespace);
            xml.WriteElementString("name", AtomNamespace, XmlText.Clean(string.IsNullOrWhiteSpace(site.Author) ? site.Title : site.Author));
            xml.WriteEndElement();
            foreach (var entry in entries)
            {
                xml.WriteStartElement("entry", AtomNamespace);
                xml.WriteElementString("id", AtomNamespace, $"urn:uuid:{entry.Id:D}");
                xml.WriteElementString("title", AtomNamespace, XmlText.Clean(entry.Title));
                WriteLink(xml, "alternate", "text/html", site.PublicOrigin + PublicUrls.Post(entry.Slug));
                xml.WriteElementString("published", AtomNamespace, PublicFormat.Rfc3339(entry.CreatedAt));
                xml.WriteElementString("updated", AtomNamespace, PublicFormat.Rfc3339(entry.UpdatedAt));
                if (entry.Summary.Length > 0)
                {
                    xml.WriteStartElement("summary", AtomNamespace);
                    xml.WriteAttributeString("type", "text");
                    xml.WriteString(XmlText.Clean(entry.Summary));
                    xml.WriteEndElement();
                }
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
        }
        http.Response.Headers.CacheControl = "public, max-age=300";
        return TypedResults.Bytes(buffer.ToArray(), "application/atom+xml; charset=utf-8");
    }

    /// <summary>글·태그·시리즈 URL을 sitemap 프로토콜 0.9 XML로 직렬화한다. 경로는 slug(<c>[a-z0-9-]</c> 문자 집합, <see cref="SlugRules"/> 참조)와
    /// <see cref="Uri.EscapeDataString"/>로 퍼센트 인코딩된 태그뿐이라(<see cref="PublicUrls.Tag"/>) <see cref="XmlText.Clean"/>이 필요 없다.</summary>
    /// <param name="http">응답 헤더(캐시 제어)를 설정할 현재 요청 컨텍스트.</param>
    /// <param name="db">글·태그·시리즈 키 조회에 쓸 공개 전용 컨텍스트.</param>
    /// <param name="siteOptions">절대 URL 기준(<see cref="SiteOptions.PublicOrigin"/>).</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>sitemap XML을 담은 200.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> ASP.NET Core 요청 파이프라인 스레드에서 호출된다. <paramref name="db"/>는 요청 스코프 전용이다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 종류별 최대 <see cref="PublicQueries.SitemapMax"/>(10,000)건의 리스트 3개(글·태그·시리즈, <see cref="PublicQueries.SitemapAsync"/> 참조) + 첫 쪽 URL 1개 = 문서에 실리는 URL은 최대 30,001개(3 × 10,000 + 1). 직렬화 중에는 이 목록들과 별개로 <see cref="MemoryStream"/> 버퍼 + <c>ToArray()</c>가 만드는 최종 <c>byte[]</c> 복사본이 동시에 메모리에 있어 문서 자체가 두 벌(스트림·배열) 존재하는 구간이 생긴다 — 이 상한 규모에서 문서 크기는 대략 수 MB로 추정된다(직접 측정하지 않음, 추정).</description></item>
    /// <item><description><b>Blocking:</b> DB 조회 3회(글·태그·시리즈)를 순차 <c>await</c>한다(<see cref="PublicQueries.SitemapAsync"/> 문서 참조). XML 직렬화는 <see cref="FeedAsync"/>와 같은 이유로 동기 CPU 작업이지만 메모리 버퍼에만 쓴다. <see cref="GetAndHead"/>가 GET과 HEAD에 같은 델리게이트를 등록하고 이 핸들러 코드에는 GET/HEAD 분기가 없으므로(코드 확인) HEAD 요청도 이 조회 3회와 직렬화 전체를 GET과 똑같이 수행한다 — HEAD가 GET보다 싸지 않다(프레임워크가 응답 전송 단계에서 본문만 생략하는 것으로 알려져 있으나, 그 생략 자체를 이 세션에서 재현 측정하지는 않았다).</description></item>
    /// </list>
    /// </remarks>
    private static async Task<IResult> SitemapAsync(HttpContext http, PublicDbContext db, IOptions<SiteOptions> siteOptions, CancellationToken ct)
    {
        var origin = siteOptions.Value.PublicOrigin;
        var map = await PublicQueries.SitemapAsync(db, ct);

        using var buffer = new MemoryStream();
        using (var xml = XmlWriter.Create(buffer, XmlSettings))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("urlset", SitemapNamespace);
            WriteUrl(xml, origin + "/", null);
            foreach (var (slug, updatedAt) in map.Posts) WriteUrl(xml, origin + PublicUrls.Post(slug), updatedAt);
            foreach (var key in map.TagKeys)
            {
                if (PublicUrls.Tag(key) is { } path) WriteUrl(xml, origin + path, null);
            }
            foreach (var slug in map.SeriesSlugs) WriteUrl(xml, origin + PublicUrls.Series(slug), null);
            xml.WriteEndElement();
        }
        http.Response.Headers.CacheControl = "public, max-age=300";
        return TypedResults.Bytes(buffer.ToArray(), "application/xml; charset=utf-8");
    }

    /// <summary>Atom <c>&lt;link&gt;</c> 요소 하나를 쓴다.</summary>
    private static void WriteLink(XmlWriter xml, string rel, string type, string href)
    {
        xml.WriteStartElement("link", AtomNamespace);
        xml.WriteAttributeString("rel", rel);
        xml.WriteAttributeString("type", type);
        xml.WriteAttributeString("href", href);
        xml.WriteEndElement();
    }

    /// <summary>sitemap <c>&lt;url&gt;</c> 요소 하나를 쓴다. <paramref name="lastModified"/>가 없으면 <c>lastmod</c>를 생략한다.</summary>
    private static void WriteUrl(XmlWriter xml, string loc, DateTimeOffset? lastModified)
    {
        xml.WriteStartElement("url", SitemapNamespace);
        xml.WriteElementString("loc", SitemapNamespace, loc);
        if (lastModified is { } at) xml.WriteElementString("lastmod", SitemapNamespace, PublicFormat.Rfc3339(at));
        xml.WriteEndElement();
    }
}
