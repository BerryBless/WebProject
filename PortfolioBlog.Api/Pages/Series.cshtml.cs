using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Infrastructure.Access;
using PortfolioBlog.Api.Infrastructure.Data;
using PortfolioBlog.Api.Infrastructure.Web;

namespace PortfolioBlog.Api.Pages;

/// <summary>시리즈 설명 + 순서대로 글 목록(상한 <see cref="PublicQueries.SeriesMax"/>).</summary>
/// <param name="db">시리즈 조회에 쓸 공개 전용 컨텍스트.</param>
/// <param name="site">사이트 설정(제목·소개·origin).</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. Razor Pages가 요청마다 새 인스턴스를 만들어 스레드 간 공유가 없다.</description></item>
/// <item><description><b>Memory Allocation:</b> <see cref="Series"/>가 보유하는 시리즈 1건 + 소속 글 최대 <see cref="PublicQueries.SeriesMax"/>건.</description></item>
/// <item><description><b>Blocking:</b> <see cref="OnGetAsync"/>가 비동기 Non-blocking으로 DB를 조회한다.</description></item>
/// </list>
/// </remarks>
public sealed class SeriesPageModel(PublicDbContext db, IOptions<SiteOptions> site) : PublicPageModel(site)
{
    /// <summary>이번 요청의 시리즈 상세(소속 글 목록 포함).</summary>
    public PublicSeries Series { get; private set; } = null!;

    /// <summary>slug로 시리즈를 찾아 상세와 소속 글 목록을 채운다.</summary>
    /// <param name="slug">요청 경로의 시리즈 slug. 모델 바인딩은 공백뿐인 값을 <see langword="null"/>로 바꾼다(실측: <c>/series/%20</c>) — 그래서 널 허용이다.</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>정상이면 이 페이지, slug가 없거나(공백뿐) 형식이 틀리거나 시리즈가 없으면 404.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not Thread-safe. 이 요청 인스턴스 안에서만 호출된다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="PublicQueries.GetSeriesAsync"/> 문서 참조.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 시리즈+소속 글 조회(총 2회)를 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    public async Task<IActionResult> OnGetAsync(string? slug, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(slug)) return NotFound();
        if (!SlugRules.IsValid(slug)) return NotFound();
        var series = await PublicQueries.GetSeriesAsync(db, slug, ct);
        if (series is null) return NotFound();
        Series = series;
        SetHead($"시리즈: {series.Title}", series.Description, PublicUrls.Series(series.Slug));
        return Page();
    }
}
