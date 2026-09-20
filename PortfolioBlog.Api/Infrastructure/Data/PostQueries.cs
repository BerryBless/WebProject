using Microsoft.EntityFrameworkCore;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Domain;

namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>글 → DTO 프로젝션. 모든 응답이 같은 조회 경로를 거치므로 "저장 직후 응답"과 "재조회 응답"이 항상 같다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. 전달된 DbContext 스코프 안에서 단일 스레드로만 호출한다.</description></item>
/// <item><description><b>Memory Allocation:</b> 프로젝션 결과만 할당한다(엔티티 추적 없음). <c>GetDetailAsync</c>는 본문 최대 200KB 문자열을 1개 보유할 수 있다.</description></item>
/// <item><description><b>Blocking:</b> 비동기 Non-blocking. 모든 DB 접근을 <c>await</c>한다.</description></item>
/// </list>
/// 다대다 링크는 DB가 순서를 보장하지 않으므로 태그는 항상 정규화 이름 순으로 정렬한다(클라이언트·테스트가 의존하는 계약).
/// </remarks>
public static class PostQueries
{
    /// <summary>Id로 글 1건을 조회해 상세 DTO로 투영한다.</summary>
    /// <param name="db">조회에 쓸 DbContext.</param>
    /// <param name="id">조회할 글의 Id.</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>일치하는 글이 있으면 <see cref="PostDetailDto"/>, 없으면 <c>null</c>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not Thread-safe. 전달된 <paramref name="db"/> 스코프 안에서만 호출한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <c>AsNoTracking</c> 프로젝션 결과 1건(본문 문자열 포함) + 태그 이름 배열.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. SELECT 1회를 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    public static async Task<PostDetailDto?> GetDetailAsync(AppDbContext db, Guid id, CancellationToken ct)
    {
        var row = await db.Posts.AsNoTracking().Where(p => p.Id == id)
            .Select(p => new
            {
                p.Id, p.Slug, p.Title, p.Summary, p.ContentMarkdown, p.SeriesId, p.SeriesOrder, p.CreatedAt, p.UpdatedAt, p.Version,
                Tags = p.PostTags.OrderBy(pt => pt.Tag.NormalizedName).Select(pt => pt.Tag.Name).ToArray(),
            })
            .SingleOrDefaultAsync(ct);
        return row is null ? null : new PostDetailDto(row.Id, row.Slug, row.Title, row.Summary, row.ContentMarkdown, row.Tags,
            row.SeriesId, row.SeriesOrder, row.CreatedAt, row.UpdatedAt, row.Version);
    }

    /// <summary><paramref name="query"/>를 최신순으로 페이지네이션해 본문을 뺀 요약 DTO 배열로 투영한다.</summary>
    /// <param name="query">필터가 이미 적용된 <see cref="Post"/> 쿼리(이 메서드가 정렬·페이지네이션·프로젝션을 추가한다).</param>
    /// <param name="skip">건너뛸 건수.</param>
    /// <param name="take">가져올 건수.</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>최신순으로 정렬된 <see cref="PostSummaryDto"/> 배열(본문 미포함).</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not Thread-safe. <paramref name="query"/>가 속한 DbContext 스코프 안에서만 호출한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <paramref name="take"/> 건수만큼의 프로젝션 결과(본문 미포함이라 목록이 커져도 200KB × N을 피한다) + 결과 배열 1개.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. SELECT 1회를 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    public static async Task<PostSummaryDto[]> ListAsync(IQueryable<Post> query, int skip, int take, CancellationToken ct)
    {
        var rows = await query.AsNoTracking()
            .OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id)
            .Skip(skip).Take(take)
            .Select(p => new
            {
                p.Id, p.Slug, p.Title, p.Summary, p.SeriesId, p.SeriesOrder, p.CreatedAt, p.UpdatedAt, p.Version,
                Tags = p.PostTags.OrderBy(pt => pt.Tag.NormalizedName).Select(pt => pt.Tag.Name).ToArray(),
            })
            .ToListAsync(ct);
        return rows.Select(r => new PostSummaryDto(r.Id, r.Slug, r.Title, r.Summary, r.Tags, r.SeriesId, r.SeriesOrder, r.CreatedAt, r.UpdatedAt, r.Version)).ToArray();
    }
}
