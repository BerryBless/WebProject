using Microsoft.Extensions.DependencyInjection;
using PortfolioBlog.Api.Domain;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>공개 표면 테스트용 시드. 관리 API를 거치지 않고 DbContext로 직접 넣는다(빠르고, 앱 검증이 막는 데이터도 넣을 수 있다).</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. 호출마다 독립된 스코프를 만들어 저장하므로 같은 <see cref="ApiFactory"/>를 가리키는 여러 호출을 병렬로 돌리지 않는다(공유 픽스처 없음, 상태는 각 호출 안에서만 산다).</description></item>
/// <item><description><b>Memory Allocation:</b> 호출마다 DI 스코프·<see cref="Post"/> 또는 <see cref="Series"/> 엔티티 1개를 힙에 할당한다.</description></item>
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
}
