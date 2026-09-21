using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Infrastructure.Data;
using PortfolioBlog.Api.Infrastructure.Markdown;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>공개 Razor 페이지를 실제 파이프라인 + PostgreSQL로 검증한다. 목록 쪽수처럼 DB 전체에 의존하는 테스트는 격리된 팩토리를 만든다.</summary>
/// <param name="factory">컬렉션이 공유하는 컨테이너를 바탕으로 클래스 전용 DB를 갖는 <see cref="ApiFactory"/> 클래스 픽스처.</param>
/// <param name="pg">컬렉션이 공유하는 PostgreSQL 컨테이너 fixture. 쪽수 테스트가 격리된 <see cref="ApiFactory"/>를 새로 만들 때 재사용한다.</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 실행된다. <paramref name="factory"/>가 호스팅하는 TestServer가 실제 PostgreSQL 컨테이너에 TCP로 접속한다.</description></item>
/// <item><description><b>Memory Policy:</b> <paramref name="factory"/>는 <see cref="IClassFixture{TFixture}"/>로 클래스 단위 1회 생성·공유된다.
/// <see cref="Index_ListsNewestFirst_Paginates_AndRejectsBadPages"/>만 자신만의 데이터를 격리하려고 새 <see cref="ApiFactory"/>를 만들어 <c>using</c>으로 해제한다 — 다른 테스트 메서드와 DB를 공유하지 않는다.</description></item>
/// <item><description><b>Concurrency:</b> 공유 <paramref name="factory"/>를 쓰는 테스트 메서드들은 서로 다른 slug·태그·시리즈를 시드해 데이터가 섞이지 않는다.</description></item>
/// <item><description><b>Blocking:</b> 모든 HTTP·DB 접근은 <c>await</c>로 비동기 대기하며 동기 블로킹이 없다.</description></item>
/// </list>
/// </remarks>
[Collection("postgres")]
public sealed class PublicPagesTests(ApiFactory factory, PostgresContainerFixture pg) : IClassFixture<ApiFactory>
{
    private const string Attachment = "/attachments/01234567-89ab-cdef-0123-456789abcdef/diagram.png";

    /// <summary>목록은 최신순 20개씩이고, 잘못된 쪽 번호는 전부 404다(보정하지 않는다).</summary>
    [Fact]
    public async Task Index_ListsNewestFirst_Paginates_AndRejectsBadPages()
    {
        using var isolated = new ApiFactory(pg, new Dictionary<string, string?>());
        var t0 = DbClock.UtcNow().AddDays(-1);
        for (var i = 0; i < 21; i++) await PublicSeed.PostAsync(isolated, $"p-{i:00}", $"글 {i}", createdAt: t0.AddMinutes(i));
        using var client = isolated.CreatePublicClient();

        var first = await HtmlDoc.GetAsync(client, "/");
        var titles = first.QuerySelectorAll("ul.post-list > li a.post-title").Select(a => a.TextContent).ToArray();
        Assert.Equal(20, titles.Length);
        Assert.Equal("글 20", titles[0]);
        Assert.Equal("/?page=2", first.QuerySelector("nav.pager a[rel=next]")?.GetAttribute("href"));

        var second = await HtmlDoc.GetAsync(client, "/?page=2");
        Assert.Equal(new[] { "글 0" }, second.QuerySelectorAll("ul.post-list > li a.post-title").Select(a => a.TextContent));
        Assert.Equal("/", second.QuerySelector("nav.pager a[rel=prev]")?.GetAttribute("href"));

        foreach (var bad in new[] { "/?page=0", "/?page=abc", "/?page=501", "/?page=3", "/?page=1&page=2", "/?page=" })
        {
            using var res = await client.GetAsync(bad);
            Assert.True(HttpStatusCode.NotFound == res.StatusCode, $"{bad}: {(int)res.StatusCode}");
        }
    }

    /// <summary>글 쪽: 본문은 정제된 HTML, 제목·요약은 속성 컨텍스트(따옴표)·RCDATA 컨텍스트(<c>&lt;/title&gt;</c>) 양쪽에서 실제로 인코딩되는지,
    /// 머리 정보의 절대 URL은 PUBLIC_ORIGIN인지, 실행 가능한 것은 0개인지 검증한다.</summary>
    [Fact]
    public async Task Post_RendersSafeHtml_AndHeadUsesPublicOrigin()
    {
        // title: "</title><script>…"을 포함한다 — <title>은 RCDATA라 인코딩 없이 그 시퀀스가 그대로 나가면 태그가 조기 종료되고
        // 진짜 <script> 요소가 생긴다(문자열만 비교하거나 "<" 유무만 보면 이 실패를 못 잡는다).
        // summary: 큰따옴표를 포함한다 — content="…" 속성값 안에서 인코딩 없이 그대로 나가면 그 자리에서 속성이 끊어진다.
        // 둘 다 인코딩되면(정상 경로) HTML 텍스트로만 남아 doc.Title·meta content 비교가 원본 문자열과 정확히 일치한다.
        const string title = "<b>굵게</b> & \"따옴표\"</title><script>alert(1)</script>";
        const string summary = "요약 <i>x</i> & \"인용\"";
        await PublicSeed.PostAsync(factory, "safe-post", title, summary: summary,
            markdown: $"<script>alert(1)</script>\n\n![그림]({Attachment})\n\n```csharp\nvar a = 1;\n```\n\n[나쁜 링크](javascript:alert(1))", tags: ["C#"]);
        using var client = factory.CreatePublicClient();

        var doc = await HtmlDoc.GetAsync(client, "/posts/safe-post");

        Assert.Equal(title, doc.QuerySelector("article h1")?.TextContent);
        Assert.Null(doc.QuerySelector("article h1 b"));
        Assert.Empty(doc.QuerySelectorAll("script, style, iframe, object, embed, [style], [onerror], [onclick]"));
        Assert.DoesNotContain(doc.QuerySelectorAll("a[href]"), a => a.GetAttribute("href")!.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(doc.QuerySelector("div.article-body div.csharp span.keyword"));
        Assert.Equal(Attachment, doc.QuerySelector("div.article-body img")?.GetAttribute("src"));
        Assert.Equal("/tags/c%23", doc.QuerySelector("ul.tag-list a")?.GetAttribute("href"));

        Assert.Equal($"{title} · Blog", doc.Title);
        Assert.Equal($"{title} · Blog", doc.QuerySelector("meta[property='og:title']")?.GetAttribute("content"));
        Assert.Equal(summary, doc.QuerySelector("meta[name=description]")?.GetAttribute("content"));
        Assert.Equal(summary, doc.QuerySelector("meta[property='og:description']")?.GetAttribute("content"));
        Assert.Equal(ApiFactory.PublicOrigin + "/posts/safe-post", doc.QuerySelector("link[rel=canonical]")?.GetAttribute("href"));
        Assert.Equal(ApiFactory.PublicOrigin + "/posts/safe-post", doc.QuerySelector("meta[property='og:url']")?.GetAttribute("content"));
        Assert.Equal("article", doc.QuerySelector("meta[property='og:type']")?.GetAttribute("content"));
        Assert.Equal(ApiFactory.PublicOrigin + Attachment, doc.QuerySelector("meta[property='og:image']")?.GetAttribute("content"));
        Assert.Equal(new[] { "/css/site.css", "/css/highlight.css" }, doc.QuerySelectorAll("link[rel=stylesheet]").Select(l => l.GetAttribute("href")));
    }

    /// <summary>이미지 없는 글은 og:image를 아예 내지 않는다(스펙 3.4).</summary>
    [Fact]
    public async Task Post_WithoutImage_HasNoOgImage()
    {
        await PublicSeed.PostAsync(factory, "no-image", "그림 없음", markdown: "글만");
        using var client = factory.CreatePublicClient();
        Assert.Null((await HtmlDoc.GetAsync(client, "/posts/no-image")).QuerySelector("meta[property='og:image']"));
    }

    /// <summary>없는 글·형식 밖 slug는 고정 HTML 404이고 요청 값을 반사하지 않는다.
    /// 공백뿐인 경로 값(스페이스·탭·개행·NBSP·전각 공백)도 포함한다: MVC 모델 바인딩이 그런 값을 <c>null</c>로 바꿔 주기 때문에
    /// 핸들러가 널을 방어하지 않으면 404가 아니라 500이 된다.</summary>
    [Theory]
    [InlineData("/posts/no-such-post")]
    [InlineData("/posts/Bad_Slug")]
    [InlineData("/posts/%3Cscript%3Ealert(1)%3C%2Fscript%3E")]
    [InlineData("/tags/no-such-tag")]
    [InlineData("/series/no-such-series")]
    [InlineData("/posts/%20")]
    [InlineData("/series/%20")]
    [InlineData("/series/%0A")]
    [InlineData("/tags/%20")]
    [InlineData("/tags/%09")]
    [InlineData("/tags/%C2%A0")]
    [InlineData("/tags/%E3%80%80")]
    public async Task Missing_Is404Html_WithoutReflection(string path)
    {
        using var client = factory.CreatePublicClient();
        var doc = await HtmlDoc.GetAsync(client, path, HttpStatusCode.NotFound);
        Assert.Equal("/", doc.QuerySelector("main a")?.GetAttribute("href"));
        Assert.Empty(doc.QuerySelectorAll("script"));
        Assert.DoesNotContain("alert", doc.DocumentElement.OuterHtml, StringComparison.Ordinal);
    }

    /// <summary>공개 페이지는 GET/HEAD 전용이고 공개 호스트에서만 열린다.</summary>
    [Fact]
    public async Task Pages_AreReadOnly_AndBoundToThePublicHost()
    {
        using var client = factory.CreatePublicClient();
        using var post = await client.PostAsync("/", new StringContent("x"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);

        using var admin = factory.CreateAdminClient();
        using var onAdminHost = await admin.GetAsync("/");
        Assert.Equal(HttpStatusCode.NotFound, onAdminHost.StatusCode);
    }

    /// <summary>같은 버전의 글은 한 번만 렌더링되고, 관리 API로 수정하면 저장 때 한 렌더가 캐시에 들어가 공개 쪽은 다시 렌더링하지 않는다.</summary>
    [Fact]
    public async Task Post_IsRenderedOncePerVersion_AndSavePrimesTheCache()
    {
        var seeded = await PublicSeed.PostAsync(factory, "cached-post", "캐시", markdown: "처음 본문");
        var gate = factory.Services.GetRequiredService<RenderGate>();
        using var client = factory.CreatePublicClient();

        var before = gate.RenderCount;
        await HtmlDoc.GetAsync(client, "/posts/cached-post");
        await HtmlDoc.GetAsync(client, "/posts/cached-post");
        Assert.Equal(before + 1, gate.RenderCount);

        using var admin = await factory.CreateLoggedInClientAsync();
        var current = await admin.GetFromJsonAsync<PostDetailDto>($"/api/posts/{seeded.Id}", TestJson.Options);
        using var put = await admin.PutAsJsonAsync($"/api/posts/{seeded.Id}", new
        {
            slug = "cached-post", title = "캐시", summary = "", contentMarkdown = "바뀐 본문", tagNames = Array.Empty<string>(), version = current!.Version,
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(before + 2, gate.RenderCount); // 저장 전 확인 렌더 1회

        var updated = await HtmlDoc.GetAsync(client, "/posts/cached-post");
        Assert.Contains("바뀐 본문", updated.QuerySelector("div.article-body")!.TextContent, StringComparison.Ordinal);
        Assert.Equal(before + 2, gate.RenderCount); // 공개 쪽은 선채움된 캐시를 썼다
    }

    /// <summary>태그 쪽은 URL 인코딩된 이름·대소문자 변형 모두 같은 정규화명으로 찾고, 시리즈 쪽은 순서대로, 글 쪽은 이전/다음을 보인다.</summary>
    [Fact]
    public async Task Tag_And_Series_Pages()
    {
        var series = await PublicSeed.SeriesAsync(factory, "gc-series", "GC 연재", "설명 <u>x</u>");
        var t0 = DbClock.UtcNow().AddDays(-2);
        await PublicSeed.PostAsync(factory, "gc-1", "GC 1편", tags: ["Tag Page C#"], seriesId: series.Id, seriesOrder: 1, createdAt: t0);
        await PublicSeed.PostAsync(factory, "gc-2", "GC 2편", seriesId: series.Id, seriesOrder: 2, createdAt: t0.AddMinutes(1));
        using var client = factory.CreatePublicClient();

        foreach (var url in new[] { "/tags/Tag%20Page%20C%23", "/tags/tag%20page%20c%23", "/tags/TAG%20%20PAGE%20C%23" })
        {
            var tagDoc = await HtmlDoc.GetAsync(client, url);
            Assert.Equal(new[] { "GC 1편" }, tagDoc.QuerySelectorAll("ul.post-list a.post-title").Select(a => a.TextContent));
            Assert.Equal(ApiFactory.PublicOrigin + "/tags/tag%20page%20c%23", tagDoc.QuerySelector("link[rel=canonical]")?.GetAttribute("href"));
        }

        var seriesDoc = await HtmlDoc.GetAsync(client, "/series/gc-series");
        Assert.Equal(new[] { "GC 1편", "GC 2편" }, seriesDoc.QuerySelectorAll("ol.series-posts a").Select(a => a.TextContent));
        Assert.Null(seriesDoc.QuerySelector("u")); // 설명은 인코딩된다

        var firstPost = await HtmlDoc.GetAsync(client, "/posts/gc-1");
        Assert.Equal("/series/gc-series", firstPost.QuerySelector("p.series-box a")?.GetAttribute("href"));
        Assert.Null(firstPost.QuerySelector("nav.post-nav a[rel=prev]"));
        Assert.Equal("/posts/gc-2", firstPost.QuerySelector("nav.post-nav a[rel=next]")?.GetAttribute("href"));
    }

    /// <summary>정적 CSS와 생성 CSS가 서빙되고, 사이트 CSS에는 id 선택자가 없으며(제목 id는 작성자 텍스트에서 나온다), wwwroot에는 그 파일 하나뿐이다.</summary>
    [Fact]
    public async Task Stylesheets_AreServed_SiteCssHasNoIdSelectors_AndWwwrootIsClosed()
    {
        using var client = factory.CreatePublicClient();
        using var site = await client.GetAsync("/css/site.css");
        Assert.Equal(HttpStatusCode.OK, site.StatusCode);
        Assert.Equal("text/css", site.Content.Headers.ContentType?.MediaType);
        using var highlight = await client.GetAsync("/css/highlight.css");
        Assert.Equal("text/css", highlight.Content.Headers.ContentType?.MediaType);
        Assert.Contains(".keyword{", await highlight.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var webRoot = factory.Services.GetRequiredService<IWebHostEnvironment>().WebRootPath;
        var files = Directory.EnumerateFiles(webRoot, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(webRoot, f).Replace('\\', '/')).Order().ToArray();
        Assert.Equal(new[] { "css/site.css" }, files);

        // 주석을 지운 뒤, '{' 바로 앞의 텍스트를 전부 뽑는다 — 그 텍스트는 선택자이거나 @규칙 머리(@media 등)다.
        // 중첩 깊이와 무관하게 모든 '{'를 훑으므로 @media 블록 안에 중첩된 규칙의 선택자도 놓치지 않는다.
        // (안쪽부터 {…} 블록을 반복 제거하는 이전 방식은 @media 자신의 {…}까지 다음 반복에서 지워버려, 그 안에 있던
        //  선택자 텍스트까지 함께 사라지는 사각지대가 있었다 — @media 안에 id 선택자를 넣어도 통과했다.)
        var css = Regex.Replace(await site.Content.ReadAsStringAsync(), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        foreach (Match m in Regex.Matches(css, @"([^{}]*)\{"))
        {
            var header = m.Groups[1].Value.Trim();
            if (header.StartsWith('@')) continue; // @media 등 at-규칙 머리는 선택자가 아니다
            Assert.DoesNotContain('#', header);
        }
    }
}
