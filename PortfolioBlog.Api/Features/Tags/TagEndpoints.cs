using Microsoft.EntityFrameworkCore;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Features.Tags;

/// <summary>태그 조회·삭제. 생성은 글 저장 시 이름으로 자동이라 별도 엔드포인트가 없다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 무상태 정적 핸들러. 요청마다 스코프된 <see cref="AppDbContext"/>를 받아 공유 가변 상태가 없다.</description></item>
/// <item><description><b>Memory Allocation:</b> 목록은 <see cref="TagDto"/> 프로젝션만 수행하며 태그·글 엔티티를 추적 로드하지 않는다.</description></item>
/// <item><description><b>Blocking:</b> 모든 DB I/O는 async. 삭제는 <c>Tag → PostTag</c> FK가 cascade라 <c>PostTags</c> 링크만 함께 지운다.
/// 글 행은 바뀌지 않으므로 글의 <c>Version</c>(xmin)도 그대로다.</description></item>
/// </list>
/// </remarks>
public static class TagEndpoints
{
    /// <summary><c>/tags</c> 그룹을 만들고 목록·삭제 엔드포인트를 등록한다.</summary>
    /// <param name="api"><c>/api</c> 루트 그룹.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 앱 시작 시 단일 스레드에서 1회 호출되어 라우트를 등록한다. 등록되는 엔드포인트 델리게이트는 요청마다 병렬로 안전하게 호출된다(공유 가변 상태 없음, 의존성은 요청 스코프로 주입).</description></item>
    /// <item><description><b>Memory Allocation:</b> 라우트 그룹·엔드포인트 등록에 따른 시작 시 1회성 할당만 발생한다.</description></item>
    /// <item><description><b>Blocking:</b> 동기 실행. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public static void MapTagEndpoints(this RouteGroupBuilder api)
    {
        var tags = api.MapGroup("/tags");
        tags.MapGet("", async (AppDbContext db, CancellationToken ct) => TypedResults.Ok(
            await db.Tags.AsNoTracking().OrderBy(t => t.NormalizedName)
                .Select(t => new TagDto(t.Id, t.Name, t.NormalizedName, t.PostTags.Count)).ToArrayAsync(ct)))
            .WithName("ListTags");
        tags.MapDelete("/{id:guid}", async (Guid id, AppDbContext db, ILoggerFactory loggers, CancellationToken ct) =>
        {
            if (await db.Tags.Where(t => t.Id == id).ExecuteDeleteAsync(ct) == 0) return (IResult)TypedResults.NotFound();
            loggers.CreateLogger("PortfolioBlog.Api.Audit").LogInformation("태그 삭제. TagId={TagId}", id);
            return TypedResults.NoContent();
        }).WithName("DeleteTag");
    }
}
