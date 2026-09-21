using Microsoft.EntityFrameworkCore;
using PortfolioBlog.Api.Domain;

namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>공개 페이지·피드가 쓰는 조회. 전부 <see cref="PublicDbContext"/>(시간 제한·읽기 전용)로만 돈다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. 전달된 <see cref="PublicDbContext"/> 스코프 안에서 단일 스레드로만 호출한다.</description></item>
/// <item><description><b>Memory Allocation:</b> 프로젝션 결과만 할당한다(<c>NoTracking</c>이라 변경 추적 그래프가 없다). 본문(최대 200KB)은 <see cref="GetContentAsync"/>가 캐시 미스일 때만 따로 읽는다 — 목록·메타 조회는 본문을 끌어오지 않는다.</description></item>
/// <item><description><b>Blocking:</b> 비동기 Non-blocking. 모든 DB 접근을 <c>await</c>하며, 연결의 <c>statement_timeout</c>이 문장 하나의 비용 상한이다.</description></item>
/// </list>
/// 컬렉션 하위 질의(태그 등)의 프로젝션은 익명 형식으로 받고 메모리에서 record로 바꾼다(<see cref="PostQueries"/>와 같은 방식 —
/// 중첩 컬렉션 안의 생성자 프로젝션에 기대지 않는다. EF Core가 그런 프로젝션을 지원하지 않거나 비효율적인 SQL을 낼 수 있다).
/// </remarks>
public static class PublicQueries
{
    /// <summary>목록 한 쪽의 글 수(스펙 3.4).</summary>
    public const int PageSize = 20;

    /// <summary>시리즈 한 개에서 읽는 글 수의 상한(시리즈 쪽·이전/다음 계산 공통).</summary>
    public const int SeriesMax = 500;

    /// <summary>Atom 피드의 항목 수.</summary>
    public const int FeedSize = 20;

    /// <summary>sitemap에 싣는 종류별 URL 수의 상한.</summary>
    public const int SitemapMax = 10_000;

    /// <summary>최신순으로 전체 글을 페이지네이션한다.</summary>
    /// <param name="db">조회에 쓸 공개 전용 컨텍스트.</param>
    /// <param name="page">1부터 시작하는 쪽 번호.</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>최신순(<c>CreatedAt DESC, Id</c>)으로 정렬된 한 쪽.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not Thread-safe. <paramref name="db"/> 스코프 안에서만 호출한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="PageAsync"/>와 동일(최대 <see cref="PageSize"/>건의 프로젝션 결과).</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. <c>COUNT</c> 1회 + 목록 SELECT 1회를 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    public static Task<PublicPage<PublicPostSummary>> LatestAsync(PublicDbContext db, int page, CancellationToken ct) =>
        PageAsync(db.Posts, page, ct);

    /// <summary>정규화 이름으로 태그를 찾아 그 태그가 붙은 글을 최신순으로 페이지네이션한다.</summary>
    /// <param name="db">조회에 쓸 공개 전용 컨텍스트.</param>
    /// <param name="normalizedName">태그의 정규화 이름(URL 키).</param>
    /// <param name="page">1부터 시작하는 쪽 번호.</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>일치하는 태그가 있으면 <c>(태그, 그 태그가 붙은 글의 한 쪽)</c>, 없으면 <see langword="null"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not Thread-safe. <paramref name="db"/> 스코프 안에서만 호출한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 태그 조회 결과 1건(익명 형식) + <see cref="PageAsync"/>의 페이지 결과.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 태그 조회 1회 + <see cref="PageAsync"/>의 2회를 순차 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    public static async Task<(PublicTag Tag, PublicPage<PublicPostSummary> Posts)?> ByTagAsync(PublicDbContext db, string normalizedName, int page, CancellationToken ct)
    {
        var tag = await db.Tags.Where(t => t.NormalizedName == normalizedName)
            .Select(t => new { t.Name, t.NormalizedName }).SingleOrDefaultAsync(ct);
        if (tag is null) return null;
        var posts = await PageAsync(db.Posts.Where(p => p.PostTags.Any(pt => pt.Tag.NormalizedName == normalizedName)), page, ct);
        return (new PublicTag(tag.Name, tag.NormalizedName), posts);
    }

    /// <summary>제목·요약·본문 <c>ILIKE</c>. <paramref name="term"/>의 메타문자는 글자 그대로 취급된다. 비용 상한은 연결의 statement_timeout과 검색 속도 제한이다.</summary>
    /// <param name="db">조회에 쓸 공개 전용 컨텍스트.</param>
    /// <param name="term">검색어(이스케이프 전 원문).</param>
    /// <param name="page">1부터 시작하는 쪽 번호.</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>일치하는 글을 최신순으로 페이지네이션한 결과.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not Thread-safe. <paramref name="db"/> 스코프 안에서만 호출한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="LikePattern.Contains"/>의 패턴 문자열 1개 + <see cref="PageAsync"/>의 페이지 결과.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. <c>ILIKE</c> 3열 전체 스캔이라 인덱스가 없으면 테이블 크기에 비례해 오래 걸릴 수 있다 —
    /// <c>statement_timeout</c>은 <b>문장 하나</b>의 상한이고 <see cref="PageAsync"/>가 이 조건으로 <c>COUNT</c> 1회 + 목록 SELECT 1회, 즉
    /// 문장 2개를 순차 실행하므로 호출 1회의 실질 상한은 <c>statement_timeout</c>의 최대 2배다. <c>PublicOptions.SearchConcurrency</c>가
    /// 동시 실행 수 상한이다(이 메서드 자체는 그 제한을 강제하지 않는다, 호출부의 계약).</description></item>
    /// </list>
    /// </remarks>
    public static Task<PublicPage<PublicPostSummary>> SearchAsync(PublicDbContext db, string term, int page, CancellationToken ct)
    {
        var pattern = LikePattern.Contains(term);
        return PageAsync(db.Posts.Where(p => EF.Functions.ILike(p.Title, pattern, LikePattern.Escape)
                                          || EF.Functions.ILike(p.Summary, pattern, LikePattern.Escape)
                                          || EF.Functions.ILike(p.ContentMarkdown, pattern, LikePattern.Escape)), page, ct);
    }

    /// <summary>본문을 뺀 글 메타데이터 + 시리즈 이웃. 본문은 캐시 미스일 때만 <see cref="GetContentAsync"/>로 따로 읽는다.</summary>
    /// <param name="db">조회에 쓸 공개 전용 컨텍스트.</param>
    /// <param name="slug">글의 공개 URL 식별자.</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>일치하는 글이 있으면 <see cref="PublicPostMeta"/>, 없으면 <see langword="null"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not Thread-safe. <paramref name="db"/> 스코프 안에서만 호출한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 글 1건(본문 제외) + 태그 배열 + 시리즈에 속하면 이웃 계산용 최대 <see cref="SeriesMax"/>건의 <c>(Id, Slug, Title)</c> 리스트.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 글 SELECT 1회, 시리즈에 속하면 형제 목록 SELECT 1회를 순차 <c>await</c>한다(총 최대 2회 — N+1 아님).</description></item>
    /// </list>
    /// </remarks>
    public static async Task<PublicPostMeta?> GetPostAsync(PublicDbContext db, string slug, CancellationToken ct)
    {
        var row = await db.Posts.Where(p => p.Slug == slug)
            .Select(p => new
            {
                p.Id, p.Version, p.Slug, p.Title, p.Summary, p.CreatedAt, p.UpdatedAt, p.SeriesId,
                SeriesSlug = p.Series != null ? p.Series.Slug : null,
                SeriesTitle = p.Series != null ? p.Series.Title : null,
                Tags = p.PostTags.OrderBy(pt => pt.Tag.NormalizedName).Select(pt => new { pt.Tag.Name, pt.Tag.NormalizedName }).ToArray(),
            })
            .SingleOrDefaultAsync(ct);
        if (row is null) return null;

        PublicLink? previous = null, next = null;
        if (row.SeriesId is { } seriesId)
        {
            var siblings = await db.Posts.Where(p => p.SeriesId == seriesId)
                .OrderBy(p => p.SeriesOrder).ThenBy(p => p.CreatedAt).ThenBy(p => p.Id)
                .Take(SeriesMax).Select(p => new { p.Id, p.Slug, p.Title }).ToListAsync(ct);
            var index = siblings.FindIndex(s => s.Id == row.Id);
            if (index > 0) previous = new PublicLink(siblings[index - 1].Slug, siblings[index - 1].Title);
            if (index >= 0 && index < siblings.Count - 1) next = new PublicLink(siblings[index + 1].Slug, siblings[index + 1].Title);
        }
        return new PublicPostMeta(row.Id, row.Version, row.Slug, row.Title, row.Summary, row.CreatedAt, row.UpdatedAt,
            row.Tags.Select(t => new PublicTag(t.Name, t.NormalizedName)).ToArray(),
            row.SeriesSlug is null ? null : new PublicLink(row.SeriesSlug, row.SeriesTitle!), previous, next);
    }

    /// <summary>본문과 그 본문의 버전을 **한 문장으로** 읽는다(메타데이터를 읽은 뒤 글이 수정됐어도 캐시 키와 내용이 어긋나지 않는다).</summary>
    /// <param name="db">조회에 쓸 공개 전용 컨텍스트.</param>
    /// <param name="id">글의 내부 식별자(<see cref="PublicPostMeta.Id"/>).</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>일치하는 글이 있으면 <see cref="PublicContent"/>, 없으면 <see langword="null"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not Thread-safe. <paramref name="db"/> 스코프 안에서만 호출한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 본문 문자열 1개(최대 200KB)를 보유한다 — 캐시 미스일 때만 호출되는 이유.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. SELECT 1회를 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    public static async Task<PublicContent?> GetContentAsync(PublicDbContext db, Guid id, CancellationToken ct)
    {
        var row = await db.Posts.Where(p => p.Id == id).Select(p => new { p.ContentMarkdown, p.Version }).SingleOrDefaultAsync(ct);
        return row is null ? null : new PublicContent(row.ContentMarkdown, row.Version);
    }

    /// <summary>시리즈 상세와 소속 글 전체 목록을 조회한다.</summary>
    /// <param name="db">조회에 쓸 공개 전용 컨텍스트.</param>
    /// <param name="slug">시리즈의 공개 URL 식별자.</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>일치하는 시리즈가 있으면 <see cref="PublicSeries"/>, 없으면 <see langword="null"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not Thread-safe. <paramref name="db"/> 스코프 안에서만 호출한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 시리즈 1건(익명 형식) + 소속 글 최대 <see cref="SeriesMax"/>건의 리스트.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 시리즈 SELECT 1회 + 소속 글 SELECT 1회를 순차 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    public static async Task<PublicSeries?> GetSeriesAsync(PublicDbContext db, string slug, CancellationToken ct)
    {
        var series = await db.Series.Where(s => s.Slug == slug).Select(s => new { s.Id, s.Slug, s.Title, s.Description }).SingleOrDefaultAsync(ct);
        if (series is null) return null;
        var posts = await db.Posts.Where(p => p.SeriesId == series.Id)
            .OrderBy(p => p.SeriesOrder).ThenBy(p => p.CreatedAt).ThenBy(p => p.Id)
            .Take(SeriesMax).Select(p => new { Order = p.SeriesOrder ?? 0, p.Slug, p.Title, p.CreatedAt }).ToListAsync(ct);
        return new PublicSeries(series.Slug, series.Title, series.Description,
            posts.Select(p => new PublicSeriesEntry(p.Order, p.Slug, p.Title, p.CreatedAt)).ToList());
    }

    /// <summary>Atom 피드에 실을 최신 글 목록을 읽는다.</summary>
    /// <param name="db">조회에 쓸 공개 전용 컨텍스트.</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>최신순(<c>CreatedAt DESC, Id</c>) 최대 <see cref="FeedSize"/>건.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not Thread-safe. <paramref name="db"/> 스코프 안에서만 호출한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 최대 <see cref="FeedSize"/>건의 프로젝션 결과 + 결과 리스트 1개.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. SELECT 1회를 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    public static async Task<IReadOnlyList<PublicFeedEntry>> FeedAsync(PublicDbContext db, CancellationToken ct)
    {
        var rows = await db.Posts.OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id).Take(FeedSize)
            .Select(p => new { p.Id, p.Slug, p.Title, p.Summary, p.CreatedAt, p.UpdatedAt }).ToListAsync(ct);
        return rows.Select(p => new PublicFeedEntry(p.Id, p.Slug, p.Title, p.Summary, p.CreatedAt, p.UpdatedAt)).ToList();
    }

    /// <summary>sitemap.xml에 실을 글·태그·시리즈 URL 키를 조회한다.</summary>
    /// <param name="db">조회에 쓸 공개 전용 컨텍스트.</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>종류별로 상한 <see cref="SitemapMax"/>건까지 담은 <see cref="PublicSitemap"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not Thread-safe. <paramref name="db"/> 스코프 안에서만 호출한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 종류별 최대 <see cref="SitemapMax"/>건의 리스트 3개(글·태그·시리즈).</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. SELECT 3회(글·태그·시리즈)를 순차 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    public static async Task<PublicSitemap> SitemapAsync(PublicDbContext db, CancellationToken ct)
    {
        var posts = await db.Posts.OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id).Take(SitemapMax)
            .Select(p => new { p.Slug, p.UpdatedAt }).ToListAsync(ct);
        // 글이 하나도 없는 태그는 빈 목록 쪽이라 싣지 않는다.
        var tags = await db.Tags.Where(t => t.PostTags.Any()).OrderBy(t => t.NormalizedName).Take(SitemapMax).Select(t => t.NormalizedName).ToListAsync(ct);
        var series = await db.Series.OrderBy(s => s.Slug).Take(SitemapMax).Select(s => s.Slug).ToListAsync(ct);
        return new PublicSitemap(posts.Select(p => (p.Slug, p.UpdatedAt)).ToList(), tags, series);
    }

    /// <summary><paramref name="query"/>를 최신순으로 페이지네이션해 본문을 뺀 요약 DTO로 투영한다.</summary>
    /// <param name="query">필터가 이미 적용된 <see cref="Post"/> 쿼리(이 메서드가 정렬·페이지네이션·프로젝션을 추가한다).</param>
    /// <param name="page">1부터 시작하는 쪽 번호. 호출부(예: Task 5의 페이지 매개변수)가 보통 상한을 이미 강제하지만, 이 메서드도 심층 방어로
    /// 범위를 직접 검사한다 — 검사가 없으면 음수 <paramref name="page"/>가 음수 OFFSET으로 내려가 PostgreSQL이 이를 0으로 취급해 조용히
    /// 1쪽을 반환해 버린다(오류 없이 틀린 결과).</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>최신순으로 정렬된 한 쪽.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="page"/>가 1보다 작거나, <c>(page - 1) * <see cref="PageSize"/></c>가
    /// <see cref="int"/> 범위를 넘어설 만큼 클 때.</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not Thread-safe. <paramref name="query"/>가 속한 DbContext 스코프 안에서만 호출한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <c>COUNT</c> 결과(값 형식) + 최대 <see cref="PageSize"/>건의 프로젝션 결과(태그 배열 포함, 각 행이 자신의 태그를 서브쿼리로 내장하므로 N+1이 아니라 SELECT 1회다 — <c>ToQueryString()</c>으로 확인, 구현 보고서 참조).</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. <c>COUNT</c> 1회 + 목록 SELECT 1회를 순차 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    private static async Task<PublicPage<PublicPostSummary>> PageAsync(IQueryable<Post> query, int page, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        // (page - 1) * PageSize가 int를 오버플로하지 않는 가장 큰 page 값까지만 허용한다 — 오버플로하면 Skip에 음수가 들어가
        // 위와 같은 이유로 조용히 잘못된 결과를 낼 수 있다. 호출부가 이미 훨씬 작은 상한(스펙 3.4의 500쪽 등)을 강제하므로
        // 이 상한에 실제로 닿는 것은 방어선이 뚫렸을 때뿐이다.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(page, int.MaxValue / PageSize);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .Select(p => new
            {
                p.Slug, p.Title, p.Summary, p.CreatedAt,
                Tags = p.PostTags.OrderBy(pt => pt.Tag.NormalizedName).Select(pt => new { pt.Tag.Name, pt.Tag.NormalizedName }).ToArray(),
            })
            .ToListAsync(ct);
        return new PublicPage<PublicPostSummary>(
            rows.Select(r => new PublicPostSummary(r.Slug, r.Title, r.Summary, r.CreatedAt, r.Tags.Select(t => new PublicTag(t.Name, t.NormalizedName)).ToArray())).ToList(),
            page, total);
    }
}
