using Microsoft.EntityFrameworkCore;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Domain;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Features.Posts;

/// <summary>글 관리 API. 그룹에 걸린 호스트·IP·CSRF·세션 검사를 통과한 요청만 도달한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 핸들러는 무상태 정적 메서드. 요청마다 스코프된 DbContext를 받는다.</description></item>
/// <item><description><b>Memory Allocation:</b> 목록은 본문 없이 프로젝션한다. 상세·저장은 본문(최대 200KB) 문자열을 1~2개 보유한다.</description></item>
/// <item><description><b>Blocking:</b> 모든 DB I/O는 async. 저장은 "태그 upsert + 글 + 링크"를 한 트랜잭션으로 묶어 부분 공개를 막는다(저장 즉시 공개이므로).</description></item>
/// </list>
/// </remarks>
public static class PostEndpoints
{
    /// <summary>목록 조회에서 <c>take</c> 쿼리 값이 없을 때 기본으로 가져올 건수.</summary>
    public const int DefaultTake = 50;

    /// <summary>목록 조회에서 <c>take</c> 쿼리 값이 가질 수 있는 최대값.</summary>
    public const int MaxTake = 200;

    /// <summary>목록 검색어(<c>q</c>) 쿼리 값의 최대 길이(문자).</summary>
    public const int MaxQueryLength = 100;

    /// <summary><c>/posts</c> 그룹을 만들고 목록·상세·생성·수정·삭제 엔드포인트를 등록한다.</summary>
    /// <param name="api"><c>/api</c> 루트 그룹.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 앱 시작 시 단일 스레드에서 1회 호출되어 라우트를 등록한다. 등록되는 엔드포인트 델리게이트는 요청마다 병렬로 안전하게 호출된다(공유 가변 상태 없음, 의존성은 요청 스코프로 주입).</description></item>
    /// <item><description><b>Memory Allocation:</b> 라우트 그룹·엔드포인트 등록에 따른 시작 시 1회성 할당만 발생한다.</description></item>
    /// <item><description><b>Blocking:</b> 동기 실행. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public static void MapPostEndpoints(this RouteGroupBuilder api)
    {
        var posts = api.MapGroup("/posts");
        posts.MapGet("", ListAsync).WithName("ListPosts");
        posts.MapGet("/{id:guid}", GetAsync).WithName("GetPost");
        posts.MapPost("", CreateAsync).WithName("CreatePost");
        posts.MapPut("/{id:guid}", UpdateAsync).WithName("UpdatePost");
        posts.MapDelete("/{id:guid}", DeleteAsync).WithName("DeletePost");
    }

    /// <summary>글 목록을 최신순으로 페이지네이션해 조회한다. <paramref name="q"/>가 있으면 제목·요약·본문을 <c>ILIKE</c>로 검색한다.</summary>
    /// <param name="db">조회에 쓸 DbContext.</param>
    /// <param name="q">검색어(선택, 최대 <see cref="MaxQueryLength"/>자). 메타문자는 리터럴로 취급된다.</param>
    /// <param name="skip">건너뛸 건수(0 이상, 기본 0).</param>
    /// <param name="take">가져올 건수(1~<see cref="MaxTake"/>, 기본 <see cref="DefaultTake"/>).</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>페이지네이션된 요약 목록과 전체 건수를 담은 200, 또는 잘못된 쿼리 값에 대한 400.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> ASP.NET Core 요청 파이프라인 스레드에서 호출된다.</description></item>
    /// <item><description><b>Memory Policy:</b> 본문을 뺀 요약 DTO만 <paramref name="take"/> 건수만큼 할당한다(목록이 200KB × N이 되지 않는다).</description></item>
    /// <item><description><b>Concurrency:</b> Thread-safe. 요청 스코프 의존성만 사용한다. Non-blocking: 개수 조회와 목록 조회를 순차 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    private static async Task<IResult> ListAsync(AppDbContext db, string? q, int? skip, int? take, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (skip is < 0) errors.Add("skip", "skip은 0 이상이어야 합니다.");
        if (take is < 1 or > MaxTake) errors.Add("take", $"take는 1~{MaxTake}여야 합니다.");
        var term = q?.Trim();
        // NUL(U+0000)은 ILIKE 매개변수로 PostgreSQL에 보내면 SqlState 22021로 실패한다(PostValidation과 같은 규칙).
        if (term?.Contains('\0') == true) errors.Add("q", "제어 문자(NUL)를 포함할 수 없습니다.");
        else if (term?.Length > MaxQueryLength) errors.Add("q", $"검색어는 {MaxQueryLength}자 이하여야 합니다.");
        if (errors.Any) return TypedResults.ValidationProblem(errors.ToDictionary());

        IQueryable<Post> query = db.Posts;
        if (!string.IsNullOrEmpty(term))
        {
            var pattern = LikePattern.Contains(term);
            query = query.Where(p => EF.Functions.ILike(p.Title, pattern, LikePattern.Escape)
                                  || EF.Functions.ILike(p.Summary, pattern, LikePattern.Escape)
                                  || EF.Functions.ILike(p.ContentMarkdown, pattern, LikePattern.Escape));
        }
        var total = await query.CountAsync(ct);
        var items = await PostQueries.ListAsync(query, skip ?? 0, take ?? DefaultTake, ct);
        return TypedResults.Ok(new PagedPostsDto(items, total));
    }

    /// <summary>Id로 글 상세를 조회한다.</summary>
    /// <param name="id">조회할 글의 Id.</param>
    /// <param name="db">조회에 쓸 DbContext.</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>글이 있으면 상세 DTO를 담은 200, 없으면 404.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> ASP.NET Core 요청 파이프라인 스레드에서 호출된다.</description></item>
    /// <item><description><b>Memory Policy:</b> <see cref="PostQueries.GetDetailAsync"/>가 상세 DTO 1개(본문 최대 200KB)를 할당한다.</description></item>
    /// <item><description><b>Concurrency:</b> Thread-safe. Non-blocking: SELECT 1회를 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    private static async Task<IResult> GetAsync(Guid id, AppDbContext db, CancellationToken ct) =>
        await PostQueries.GetDetailAsync(db, id, ct) is { } dto ? TypedResults.Ok(dto) : TypedResults.NotFound();

    /// <summary>새 글을 만든다. 저장 즉시 공개되므로 태그 upsert·글·태그 링크를 한 트랜잭션으로 묶는다.</summary>
    /// <param name="req">생성 요청 본문.</param>
    /// <param name="db">저장에 쓸 DbContext.</param>
    /// <param name="loggers">생성을 id·slug만 남기고 기록할 로거 팩토리.</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>성공 시 <c>Location</c> 헤더와 상세 DTO를 담은 201, 검증 실패 시 400, slug 중복이거나 검증 뒤 참조가 사라졌으면 409.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> ASP.NET Core 요청 파이프라인 스레드에서 호출된다.</description></item>
    /// <item><description><b>Memory Policy:</b> <see cref="Post"/> 엔티티 1개 + 태그 연결 목록 + 본문(최대 200KB) 문자열 1개를 할당한다.</description></item>
    /// <item><description><b>Concurrency:</b> Thread-safe. 요청 스코프 의존성만 사용한다. Non-blocking: slug 중복 조회·태그 해석·트랜잭션·저장을 모두 <c>await</c>한다.
    /// 같은 slug 동시 생성은 사전 검사를 통과해도 <c>SaveChangesAsync</c>의 유니크 위반으로 409를 돌려준다(경쟁 창을 DB가 최종 방어한다).</description></item>
    /// </list>
    /// </remarks>
    private static async Task<IResult> CreateAsync(UpsertPostRequest req, AppDbContext db, ILoggerFactory loggers, CancellationToken ct)
    {
        var errors = PostValidation.Validate(req);
        await ValidateSeriesAsync(db, req, errors, ct);
        if (errors.Any) return TypedResults.ValidationProblem(errors.ToDictionary());

        if (await db.Posts.AnyAsync(p => p.Slug == req.Slug, ct)) return DbConflict.Problem($"slug '{req.Slug}'는 이미 쓰이고 있습니다.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var tagIds = await TagResolver.ResolveIdsAsync(db, req.TagNames, ct);
        var now = DbClock.UtcNow();
        var post = new Post
        {
            Slug = req.Slug!, Title = req.Title!.Trim(), Summary = req.Summary?.Trim() ?? string.Empty,
            ContentMarkdown = req.ContentMarkdown!, SeriesId = req.SeriesId, SeriesOrder = req.SeriesOrder,
            CreatedAt = now, UpdatedAt = now,
        };
        foreach (var tagId in tagIds) post.PostTags.Add(new PostTag { TagId = tagId });
        db.Posts.Add(post);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbConflict.IsConstraintRace(ex))
        {
            return DbConflict.Problem("같은 slug가 방금 만들어졌거나 참조한 시리즈·태그가 방금 삭제되었습니다. 다시 조회한 뒤 저장하세요.");
        }
        await tx.CommitAsync(ct);

        loggers.CreateLogger("PortfolioBlog.Api.Audit").LogInformation("글 생성. PostId={PostId} Slug={Slug}", post.Id, post.Slug); // 본문은 기록하지 않는다
        var dto = await PostQueries.GetDetailAsync(db, post.Id, ct);
        return TypedResults.Created($"/api/posts/{post.Id}", dto);
    }

    /// <summary>기존 글을 수정한다. slug는 불변이며, <paramref name="req"/>의 <c>Version</c>이 현재 값과 같아야 저장된다(낙관적 동시성).</summary>
    /// <param name="id">수정할 글의 Id.</param>
    /// <param name="req">수정 요청 본문(전체 교체 의미론).</param>
    /// <param name="db">저장에 쓸 DbContext.</param>
    /// <param name="loggers">수정을 id·slug만 남기고 기록할 로거 팩토리.</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>성공 시 갱신된 상세 DTO를 담은 200, 글이 없으면 404, 검증 실패 시 400, version이 오래됐거나 참조가 사라졌으면 409.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> ASP.NET Core 요청 파이프라인 스레드에서 호출된다.</description></item>
    /// <item><description><b>Memory Policy:</b> 추적되는 <see cref="Post"/>와 <see cref="PostTag"/> 컬렉션을 로드하고, 본문(최대 200KB) 문자열 1개를 교체 보유한다.</description></item>
    /// <item><description><b>Concurrency:</b> Thread-safe. 요청 스코프 의존성만 사용한다. Non-blocking: 모든 DB I/O를 <c>await</c>한다.
    /// <c>xmin</c>을 <c>OriginalValue</c>로 고정해 조회 이후 발생한 경쟁도 <c>UPDATE ... WHERE xmin = ...</c>로 잡는다(사전 검사만으로는 조회~저장 사이의 경쟁을 놓친다).</description></item>
    /// </list>
    /// </remarks>
    private static async Task<IResult> UpdateAsync(Guid id, UpsertPostRequest req, AppDbContext db, ILoggerFactory loggers, CancellationToken ct)
    {
        var post = await db.Posts.Include(p => p.PostTags).SingleOrDefaultAsync(p => p.Id == id, ct);
        if (post is null) return TypedResults.NotFound();

        var errors = PostValidation.Validate(req);
        // slug 불변: 공개 URL과 Atom 항목의 안정성을 위해 생성 후에는 바꿀 수 없다(스펙 3.2).
        if (req.Slug is not null && req.Slug != post.Slug) errors.Add("slug", "slug는 생성 후 바꿀 수 없습니다.");
        if (req.Version is null) errors.Add("version", "수정에는 조회 때 받은 version이 필요합니다.");
        await ValidateSeriesAsync(db, req, errors, ct);
        if (errors.Any) return TypedResults.ValidationProblem(errors.ToDictionary());

        if (post.Version != req.Version) return StaleVersion();
        // 읽은 뒤 저장 전까지의 경쟁도 잡도록 UPDATE의 WHERE xmin = ... 비교값을 클라이언트가 본 버전으로 고정한다.
        db.Entry(post).Property(p => p.Version).OriginalValue = req.Version!.Value;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var wanted = await TagResolver.ResolveIdsAsync(db, req.TagNames, ct);
        // 링크는 차집합만 지우고 더한다(전부 지웠다 다시 넣으면 같은 복합 키의 Deleted·Added 엔티티가 추적기에서 충돌한다).
        post.PostTags.RemoveAll(pt => !wanted.Contains(pt.TagId));
        foreach (var tagId in wanted.Where(tagId => post.PostTags.All(pt => pt.TagId != tagId)))
        {
            post.PostTags.Add(new PostTag { PostId = post.Id, TagId = tagId });
        }
        post.Title = req.Title!.Trim();
        post.Summary = req.Summary?.Trim() ?? string.Empty;
        post.ContentMarkdown = req.ContentMarkdown!;
        post.SeriesId = req.SeriesId;
        post.SeriesOrder = req.SeriesOrder;
        post.UpdatedAt = DbClock.UtcNow(); // 항상 바뀌므로 태그만 고쳐도 Posts 행이 갱신되어 xmin 비교가 실행된다
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return StaleVersion();
        }
        catch (DbUpdateException ex) when (DbConflict.IsConstraintRace(ex))
        {
            return DbConflict.Problem("참조한 시리즈·태그가 방금 삭제되었습니다. 다시 조회한 뒤 저장하세요.");
        }
        await tx.CommitAsync(ct);

        loggers.CreateLogger("PortfolioBlog.Api.Audit").LogInformation("글 수정. PostId={PostId} Slug={Slug}", post.Id, post.Slug);
        return TypedResults.Ok(await PostQueries.GetDetailAsync(db, post.Id, ct));
    }

    /// <summary>글을 삭제한다(태그 링크만 지우고 태그 자체는 남긴다). <paramref name="version"/>이 필수이며 현재 값과 같아야 삭제된다(낙관적 동시성).</summary>
    /// <param name="id">삭제할 글의 Id.</param>
    /// <param name="version">조회 때 받은 낙관적 동시성 토큰(필수 쿼리 값).</param>
    /// <param name="db">삭제에 쓸 DbContext.</param>
    /// <param name="loggers">삭제를 id·slug만 남기고 기록할 로거 팩토리.</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>성공 시 204, <paramref name="version"/>이 없으면 400, 글이 없으면 404, version이 오래됐으면 409.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> ASP.NET Core 요청 파이프라인 스레드에서 호출된다.</description></item>
    /// <item><description><b>Memory Policy:</b> 추적되는 <see cref="Post"/> 1개만 로드한다(태그·연결은 <c>Cascade</c> 삭제가 DB에서 처리하므로 미리 로드하지 않는다).</description></item>
    /// <item><description><b>Concurrency:</b> Thread-safe. 요청 스코프 의존성만 사용한다. Non-blocking: 모든 DB I/O를 <c>await</c>한다.
    /// <c>xmin</c>을 <c>OriginalValue</c>로 고정해 조회 이후 발생한 경쟁도 <c>DELETE ... WHERE xmin = ...</c>로 잡는다.</description></item>
    /// </list>
    /// </remarks>
    private static async Task<IResult> DeleteAsync(Guid id, uint? version, AppDbContext db, ILoggerFactory loggers, CancellationToken ct)
    {
        if (version is null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["version"] = ["삭제에는 조회 때 받은 version 쿼리 값이 필요합니다."] });
        }
        var post = await db.Posts.SingleOrDefaultAsync(p => p.Id == id, ct);
        if (post is null) return TypedResults.NotFound();
        if (post.Version != version) return StaleVersion();

        db.Entry(post).Property(p => p.Version).OriginalValue = version.Value;
        db.Posts.Remove(post);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return StaleVersion();
        }
        loggers.CreateLogger("PortfolioBlog.Api.Audit").LogInformation("글 삭제. PostId={PostId} Slug={Slug}", post.Id, post.Slug);
        return TypedResults.NoContent();
    }

    /// <summary><paramref name="req"/>가 참조하는 시리즈가 실제로 존재하는지 검사해 <paramref name="errors"/>에 누적한다.</summary>
    /// <param name="db">시리즈 존재를 조회할 DbContext.</param>
    /// <param name="req">검사할 요청.</param>
    /// <param name="errors">오류를 누적할 대상.</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> ASP.NET Core 요청 파이프라인 스레드에서 호출된다.</description></item>
    /// <item><description><b>Memory Policy:</b> 추가 엔티티 로드 없이 <c>AnyAsync</c> 존재 조회만 수행한다.</description></item>
    /// <item><description><b>Concurrency:</b> Thread-safe. Non-blocking: 존재 조회를 <c>await</c>한다. 이 검사 이후 시리즈가 삭제되는 경쟁은 저장 시 FK 위반(409)으로 잡는다.</description></item>
    /// </list>
    /// </remarks>
    private static async Task ValidateSeriesAsync(AppDbContext db, UpsertPostRequest req, ValidationErrors errors, CancellationToken ct)
    {
        if (req.SeriesId is { } seriesId && !await db.Series.AnyAsync(s => s.Id == seriesId, ct))
        {
            errors.Add("seriesId", "존재하지 않는 시리즈입니다.");
        }
    }

    /// <summary>낙관적 동시성 충돌(오래된 version)에 대한 409 응답을 만든다.</summary>
    /// <returns>409 Conflict <see cref="IResult"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 매개변수 없이 고정 메시지로 새 결과 객체를 만든다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult"/> 1개를 할당한다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking).</description></item>
    /// </list>
    /// </remarks>
    private static IResult StaleVersion() =>
        DbConflict.Problem("다른 곳에서 이 글이 먼저 수정되었습니다. 최신 내용을 다시 불러온 뒤 저장하세요.");
}
