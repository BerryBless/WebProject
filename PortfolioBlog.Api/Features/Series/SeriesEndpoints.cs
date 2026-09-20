using Microsoft.EntityFrameworkCore;
using Npgsql;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Features.Series;

/// <summary>시리즈 관리 API. 그룹에 걸린 호스트·IP·CSRF·세션 검사를 통과한 요청만 도달한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 핸들러는 무상태 정적 메서드. 요청마다 스코프된 DbContext를 받는다.</description></item>
/// <item><description><b>Memory Allocation:</b> 목록·상세 모두 프로젝션(글 본문을 읽지 않는다).</description></item>
/// <item><description><b>Blocking:</b> 모든 DB I/O는 async. 삭제는 "소속 글의 두 필드 비우기 + 시리즈 삭제"를 한 트랜잭션으로 묶는다.</description></item>
/// </list>
/// </remarks>
public static class SeriesEndpoints
{
    /// <summary><c>/series</c> 그룹을 만들고 목록·상세·생성·수정·삭제 엔드포인트를 등록한다.</summary>
    /// <param name="api"><c>/api</c> 루트 그룹.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 앱 시작 시 단일 스레드에서 1회 호출되어 라우트를 등록한다. 등록되는 엔드포인트 델리게이트는 요청마다 병렬로 안전하게 호출된다(공유 가변 상태 없음, 의존성은 요청 스코프로 주입).</description></item>
    /// <item><description><b>Memory Allocation:</b> 라우트 그룹·엔드포인트 등록에 따른 시작 시 1회성 할당만 발생한다.</description></item>
    /// <item><description><b>Blocking:</b> 동기 실행. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public static void MapSeriesEndpoints(this RouteGroupBuilder api)
    {
        var series = api.MapGroup("/series");
        series.MapGet("", ListAsync).WithName("ListSeries");
        series.MapGet("/{id:guid}", GetAsync).WithName("GetSeries");
        series.MapPost("", CreateAsync).WithName("CreateSeries");
        series.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateSeries");
        series.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteSeries");
    }

    /// <summary><paramref name="query"/>를 소속 글 수를 포함한 <see cref="SeriesDto"/>로 투영한다.</summary>
    /// <param name="query">필터가 이미 적용된 <see cref="Domain.Series"/> 쿼리.</param>
    /// <returns>투영된 <see cref="SeriesDto"/> 쿼리(아직 실행되지 않음).</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> 호출한 핸들러와 같은 요청 파이프라인 스레드에서 실행된다.</description></item>
    /// <item><description><b>Memory Policy:</b> 쿼리 식 구성만 하며 실행 전까지 추가 할당이 없다. <c>Posts.Count</c>는 SQL 서브쿼리로 변환되어 글 엔티티를 로드하지 않는다.</description></item>
    /// <item><description><b>Concurrency:</b> Thread-safe. 지연 실행되는 <see cref="IQueryable{T}"/>만 구성하고 DB I/O를 수행하지 않는다.</description></item>
    /// </list>
    /// </remarks>
    private static IQueryable<SeriesDto> Project(IQueryable<Domain.Series> query) =>
        query.Select(s => new SeriesDto(s.Id, s.Slug, s.Title, s.Description, s.Posts.Count));

    /// <summary>모든 시리즈를 제목순으로 조회한다.</summary>
    /// <param name="db">조회에 쓸 DbContext.</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>소속 글 수를 포함한 <see cref="SeriesDto"/> 배열을 담은 200.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> ASP.NET Core 요청 파이프라인 스레드에서 호출된다.</description></item>
    /// <item><description><b>Memory Policy:</b> <see cref="SeriesDto"/> 배열 1개만 할당한다(글 엔티티는 로드하지 않는다).</description></item>
    /// <item><description><b>Concurrency:</b> Thread-safe. Non-blocking: SELECT 1회를 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    private static async Task<IResult> ListAsync(AppDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await Project(db.Series.AsNoTracking().OrderBy(s => s.Title).ThenBy(s => s.Id)).ToArrayAsync(ct));

    /// <summary>Id로 시리즈 상세와 소속 글 목록을 조회한다.</summary>
    /// <param name="id">조회할 시리즈의 Id.</param>
    /// <param name="db">조회에 쓸 DbContext.</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>시리즈가 있으면 정렬된 소속 글 목록을 담은 <see cref="SeriesDetailDto"/> 200, 없으면 404.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> ASP.NET Core 요청 파이프라인 스레드에서 호출된다.</description></item>
    /// <item><description><b>Memory Policy:</b> <see cref="SeriesDto"/> 1개 + 소속 글 수만큼의 <see cref="SeriesPostDto"/> 배열을 할당한다(글 본문은 읽지 않는다).</description></item>
    /// <item><description><b>Concurrency:</b> Thread-safe. Non-blocking: 시리즈 조회·글 목록 조회를 순차 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    private static async Task<IResult> GetAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
        var series = await Project(db.Series.AsNoTracking().Where(s => s.Id == id)).SingleOrDefaultAsync(ct);
        if (series is null) return TypedResults.NotFound();
        // 순서값 중복을 허용하므로 (SeriesOrder, CreatedAt, Id)로 안정 정렬한다. IX_Posts_SeriesId_SeriesOrder_CreatedAt_Id 인덱스가 이 정렬을 받친다.
        var posts = await db.Posts.AsNoTracking().Where(p => p.SeriesId == id)
            .OrderBy(p => p.SeriesOrder).ThenBy(p => p.CreatedAt).ThenBy(p => p.Id)
            .Select(p => new SeriesPostDto(p.Id, p.Slug, p.Title, p.SeriesOrder!.Value))
            .ToArrayAsync(ct);
        return TypedResults.Ok(new SeriesDetailDto(series, posts));
    }

    /// <summary>새 시리즈를 만든다.</summary>
    /// <param name="req">생성 요청 본문.</param>
    /// <param name="db">저장에 쓸 DbContext.</param>
    /// <param name="loggers">생성을 id·slug만 남기고 기록할 로거 팩토리.</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>성공 시 <c>Location</c> 헤더와 <see cref="SeriesDto"/>를 담은 201, 검증 실패 시 400, slug 중복이면 409.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> ASP.NET Core 요청 파이프라인 스레드에서 호출된다.</description></item>
    /// <item><description><b>Memory Policy:</b> <see cref="Domain.Series"/> 엔티티 1개를 할당한다.</description></item>
    /// <item><description><b>Concurrency:</b> Thread-safe. 요청 스코프 의존성만 사용한다. Non-blocking: slug 중복 조회·저장을 모두 <c>await</c>한다.
    /// 같은 slug 동시 생성은 사전 검사를 통과해도 <c>SaveChangesAsync</c>의 유니크 위반으로 409를 돌려준다(경쟁 창을 DB가 최종 방어한다).</description></item>
    /// </list>
    /// </remarks>
    private static async Task<IResult> CreateAsync(UpsertSeriesRequest req, AppDbContext db, ILoggerFactory loggers, CancellationToken ct)
    {
        var errors = SeriesValidation.Validate(req);
        if (errors.Any) return TypedResults.ValidationProblem(errors.ToDictionary());
        if (await db.Series.AnyAsync(s => s.Slug == req.Slug, ct)) return DbConflict.Problem($"slug '{req.Slug}'는 이미 쓰이고 있습니다.");

        var series = new Domain.Series { Slug = req.Slug!, Title = req.Title!.Trim(), Description = req.Description?.Trim() ?? string.Empty };
        db.Series.Add(series);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbConflict.IsConstraintRace(ex))
        {
            return DbConflict.Problem("같은 slug의 시리즈가 방금 만들어졌습니다.");
        }
        loggers.CreateLogger("PortfolioBlog.Api.Audit").LogInformation("시리즈 생성. SeriesId={SeriesId} Slug={Slug}", series.Id, series.Slug);
        return TypedResults.Created($"/api/series/{series.Id}", new SeriesDto(series.Id, series.Slug, series.Title, series.Description, 0));
    }

    /// <summary>기존 시리즈의 제목·설명을 수정한다. slug는 불변이며 요청 값이 현재와 다르면 거부한다.</summary>
    /// <param name="id">수정할 시리즈의 Id.</param>
    /// <param name="req">수정 요청 본문.</param>
    /// <param name="db">저장에 쓸 DbContext.</param>
    /// <param name="loggers">수정을 id만 남기고 기록할 로거 팩토리.</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>성공 시 갱신된 <see cref="SeriesDto"/>를 담은 200, 시리즈가 없거나(조회 시점 또는 저장 직전 삭제 경쟁) 404, 검증 실패나 slug 변경 시도면 400.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> ASP.NET Core 요청 파이프라인 스레드에서 호출된다.</description></item>
    /// <item><description><b>Memory Policy:</b> 추적되는 <see cref="Domain.Series"/> 1개를 로드한다.</description></item>
    /// <item><description><b>Concurrency:</b> Thread-safe. 요청 스코프 의존성만 사용한다. Non-blocking: 조회·저장·재조회를 모두 <c>await</c>한다.
    /// slug는 요청에서 아예 제외하지 않고 값이 오면 현재 값과 비교해 다르면 400으로 거부한다(형식은 유효하되 변경은 금지).
    /// 조회 이후 저장 직전에 이 시리즈가 동시 삭제되면 <c>UPDATE</c>가 0행에 적용되어 <see cref="DbUpdateConcurrencyException"/>이 나므로 404로,
    /// 저장 이후 재조회 직전에 삭제되면 <c>SingleOrDefaultAsync</c>가 <c>null</c>을 돌려주므로 역시 404로 처리한다(둘 다 500이 아니다).</description></item>
    /// </list>
    /// </remarks>
    private static async Task<IResult> UpdateAsync(Guid id, UpsertSeriesRequest req, AppDbContext db, ILoggerFactory loggers, CancellationToken ct)
    {
        var series = await db.Series.SingleOrDefaultAsync(s => s.Id == id, ct);
        if (series is null) return TypedResults.NotFound();

        var errors = SeriesValidation.Validate(req);
        // slug 불변: 공개 URL의 안정성을 위해 생성 후에는 바꿀 수 없다.
        if (req.Slug is not null && req.Slug != series.Slug) errors.Add("slug", "slug는 생성 후 바꿀 수 없습니다.");
        if (errors.Any) return TypedResults.ValidationProblem(errors.ToDictionary());

        series.Title = req.Title!.Trim();
        series.Description = req.Description?.Trim() ?? string.Empty;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // 조회 이후 저장 직전에 다른 요청이 이 시리즈를 삭제했다: UPDATE가 0행에 적용되어 EF가 낙관적 동시성 예외로 본다.
            return TypedResults.NotFound();
        }
        loggers.CreateLogger("PortfolioBlog.Api.Audit").LogInformation("시리즈 수정. SeriesId={SeriesId}", series.Id);

        var updated = await Project(db.Series.AsNoTracking().Where(s => s.Id == id)).SingleOrDefaultAsync(ct);
        // 저장은 성공했지만 재조회 직전에 다른 요청이 삭제했다면 여기서 null이 된다: SingleAsync였다면 InvalidOperationException(500).
        return updated is null ? TypedResults.NotFound() : TypedResults.Ok(updated);
    }

    /// <summary>시리즈를 삭제한다. 소속 글은 지우지 않고 <c>SeriesId</c>·<c>SeriesOrder</c>를 함께 비운다.</summary>
    /// <param name="id">삭제할 시리즈의 Id.</param>
    /// <param name="db">삭제에 쓸 DbContext.</param>
    /// <param name="loggers">삭제를 id만 남기고 기록할 로거 팩토리.</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>성공 시 204, 시리즈가 없으면 404, 락 경쟁을 뚫고 참조가 방금 생겼으면 409.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> ASP.NET Core 요청 파이프라인 스레드에서 호출된다.</description></item>
    /// <item><description><b>Memory Policy:</b> 추적 엔티티를 로드하지 않는다. <c>ExecuteSqlAsync</c>·<c>ExecuteUpdateAsync</c>·<c>ExecuteDeleteAsync</c>는 변경 추적기를 거치지 않고 SQL을 직접 실행한다.</description></item>
    /// <item><description><b>Concurrency:</b> Thread-safe. 요청 스코프 의존성만 사용한다. Non-blocking: 모든 DB I/O를 <c>await</c>한다.
    /// <c>CK_Posts_Series_Pair</c>(<c>SeriesId</c>·<c>SeriesOrder</c>가 함께 null이거나 함께 non-null)를 지키려면 두 컬럼을 같은 UPDATE 문에서 함께 비워야 하므로
    /// "시리즈 행 잠금 + 글 UPDATE + 시리즈 DELETE"를 한 트랜잭션으로 묶는다. 시리즈가 없으면 <c>tx</c>를 커밋하지 않고 <c>await using</c> dispose에서 롤백해 방금 비운 글 UPDATE를 원복한다.
    /// <c>ExecuteDeleteAsync</c>가 던지는 FK 위반은 <see cref="DbUpdateException"/>이 아니라 <see cref="PostgresException"/>이 그대로 올라온다(변경 추적기를 거치지 않는 벌크 연산이라
    /// <c>SaveChangesAsync</c> 전용 래핑 경로를 타지 않는다 — <c>ExecuteDeleteAsync</c>로 실제 FK 위반을 재현해 관찰로 확인함).</description></item>
    /// </list>
    /// </remarks>
    private static async Task<IResult> DeleteAsync(Guid id, AppDbContext db, ILoggerFactory loggers, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // [LOCK-REQUIRED] FOR UPDATE로 시리즈 행을 트랜잭션의 첫 문장에서 잠근다: 자식(Posts) INSERT가 FK 참조 무결성을
        // 검사할 때 부모(Series) 행에 FOR KEY SHARE 락을 거는데, 이는 FOR UPDATE(배타 락)와 충돌한다.
        // 우리가 이 락을 먼저 쥐면 같은 시리즈를 참조하려는 동시 글 저장의 SaveChangesAsync는 우리가 커밋·롤백할 때까지
        // 블로킹된다 — 우리가 먼저 커밋하면 그 글 저장은 깨진 FK로 실패해 409가 되고(PostEndpoints.CreateAsync의
        // DbConflict.IsConstraintRace가 이미 처리), 우리가 먼저 롤백하면 그 글 저장은 정상 진행된다.
        // 반대로 저 삽입이 우리보다 먼저 시작해 커밋까지 끝냈다면, 우리는 그 뒤에야 락을 얻으므로 아래 ExecuteUpdateAsync가
        // READ COMMITTED 하에서 그 글까지 포함해 비운다. 어느 순서든 매달린(dangling) 참조가 생기지 않는다.
        await db.Database.ExecuteSqlAsync($"""SELECT 1 FROM "Series" WHERE "Id" = {id} FOR UPDATE""", ct);

        // CK_Posts_Series_Pair 때문에 두 필드를 같은 UPDATE에서 함께 비운다. UpdatedAt은 건드리지 않는다(글 내용이 바뀐 게 아니다).
        await db.Posts.Where(p => p.SeriesId == id)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.SeriesId, (Guid?)null).SetProperty(p => p.SeriesOrder, (int?)null), ct);

        int deleted;
        try
        {
            deleted = await db.Series.Where(s => s.Id == id).ExecuteDeleteAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == DbConflict.ForeignKeyViolation)
        {
            // 2차 방어선: 위의 FOR UPDATE 선점으로도 막지 못한 경쟁(예: 격리 수준·제약 지연 변경)이 있다면
            // 500 대신 409로 알린다. IsConstraintRace는 DbUpdateException 전용이라 여기서는 쓸 수 없다.
            return DbConflict.Problem("참조하는 글이 방금 추가되었습니다. 다시 조회한 뒤 시도하세요.");
        }
        if (deleted == 0) return TypedResults.NotFound(); // tx는 dispose에서 롤백된다(비운 글이 있었다면 원복)
        await tx.CommitAsync(ct);
        loggers.CreateLogger("PortfolioBlog.Api.Audit").LogInformation("시리즈 삭제. SeriesId={SeriesId}", id);
        return TypedResults.NoContent();
    }
}
