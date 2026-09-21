using Microsoft.Extensions.DependencyInjection;
using PortfolioBlog.Api.Domain;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>공개 표면 테스트용 시드. 관리 API를 거치지 않고 DbContext로 직접 넣는다(빠르고, 앱 검증이 막는 데이터도 넣을 수 있다).</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. 호출마다 독립된 스코프를 만들어 저장하므로 같은 <see cref="ApiFactory"/>를 가리키는 여러 호출을 병렬로 돌리지 않는다(공유 픽스처 없음, 상태는 각 호출 안에서만 산다).</description></item>
/// <item><description><b>Memory Allocation:</b> <see cref="PostAsync"/>·<see cref="SeriesAsync"/>는 호출마다 DI 스코프 + 엔티티 1개. <see cref="ManyPostsAsync"/>는 <c>count</c>개의 <see cref="Post"/>를 리스트로 모아 한 번에 저장한다(대량 시드 전용).</description></item>
/// <item><description><b>Blocking:</b> 비동기 Non-blocking. <c>SaveChangesAsync</c> 왕복을 <c>await</c>한다.</description></item>
/// </list>
/// </remarks>
internal static class PublicSeed
{
    /// <summary>글 1건을 관리 컨텍스트로 직접 저장한다.</summary>
    /// <param name="factory">글을 저장할 호스트의 <see cref="ApiFactory"/>.</param>
    /// <param name="slug">공개 URL 식별자.</param>
    /// <param name="title">글 제목.</param>
    /// <param name="markdown">본문 마크다운(기본값 "본문").</param>
    /// <param name="summary">요약(기본값 빈 문자열).</param>
    /// <param name="tags">연결할 태그 이름 목록(기본값 없음).</param>
    /// <param name="seriesId">소속 시리즈 Id(기본값 없음).</param>
    /// <param name="seriesOrder">시리즈 안 순서(기본값 없음).</param>
    /// <param name="createdAt">생성 시각(기본값 <see cref="DbClock.UtcNow"/>).</param>
    public static async Task<Post> PostAsync(ApiFactory factory, string slug, string title, string markdown = "본문", string summary = "",
        string[]? tags = null, Guid? seriesId = null, int? seriesOrder = null, DateTimeOffset? createdAt = null)
    {
        using var _ = factory.CreateClient(); // 호스트 기동 → Migrate()
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tagIds = await TagResolver.ResolveIdsAsync(db, tags, CancellationToken.None);
        var at = createdAt ?? DbClock.UtcNow();
        var post = new Post
        {
            Slug = slug, Title = title, Summary = summary, ContentMarkdown = markdown,
            SeriesId = seriesId, SeriesOrder = seriesOrder, CreatedAt = at, UpdatedAt = at,
        };
        foreach (var tagId in tagIds) post.PostTags.Add(new PostTag { TagId = tagId });
        db.Posts.Add(post);
        await db.SaveChangesAsync();
        return post;
    }

    /// <summary>시리즈 1건을 관리 컨텍스트로 직접 저장한다.</summary>
    /// <param name="factory">시리즈를 저장할 호스트의 <see cref="ApiFactory"/>.</param>
    /// <param name="slug">공개 URL 식별자.</param>
    /// <param name="title">시리즈 제목.</param>
    /// <param name="description">시리즈 소개(기본값 빈 문자열).</param>
    public static async Task<Series> SeriesAsync(ApiFactory factory, string slug, string title, string description = "")
    {
        using var _ = factory.CreateClient();
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var series = new Series { Slug = slug, Title = title, Description = description };
        db.Series.Add(series);
        await db.SaveChangesAsync();
        return series;
    }

    /// <summary>같은 검색어에 걸리는 글 <paramref name="count"/>건을 한 번의 <c>SaveChangesAsync</c>로 저장한다.
    /// 쪽 번호 상한처럼 "결과가 실제로 존재하는 먼 쪽"이 필요한 경계 테스트 전용 — <see cref="PostAsync"/>를 <paramref name="count"/>번 부르면
    /// 라운드트립도 그만큼 늘어 느리다.</summary>
    /// <param name="factory">글을 저장할 호스트의 <see cref="ApiFactory"/>.</param>
    /// <param name="count">저장할 글 수.</param>
    /// <param name="titlePrefix">모든 글 제목에 공통으로 넣을 검색어. 글마다 <c>" {순번}"</c>을 붙여 제목을 구분한다.</param>
    public static async Task ManyPostsAsync(ApiFactory factory, int count, string titlePrefix)
    {
        using var _ = factory.CreateClient(); // 호스트 기동 → Migrate()
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // 정렬 기준(CreatedAt DESC, Id)이 페이지 경계를 결정하므로 분 단위로 어긋나게 만들어 쪽 나눔이 결정적이게 한다.
        var t0 = DbClock.UtcNow().AddDays(-2);
        var posts = new List<Post>(count);
        for (var i = 0; i < count; i++)
        {
            var at = t0.AddMinutes(i);
            posts.Add(new Post
            {
                Slug = $"bulk-{i:0000}", Title = $"{titlePrefix} {i}", Summary = "", ContentMarkdown = "본문",
                CreatedAt = at, UpdatedAt = at,
            });
        }
        db.Posts.AddRange(posts);
        await db.SaveChangesAsync();
    }
}
