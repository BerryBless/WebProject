using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Infrastructure.Access;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Pages;

/// <summary>공개 첫 쪽: 최신 글 목록(20개씩, 상한 500쪽).</summary>
/// <param name="db">목록 조회에 쓸 공개 전용 컨텍스트.</param>
/// <param name="site">사이트 설정(제목·소개·origin).</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. Razor Pages가 요청마다 새 인스턴스를 만들어 스레드 간 공유가 없다.</description></item>
/// <item><description><b>Memory Allocation:</b> <see cref="Posts"/>가 보유하는 최대 <see cref="PublicQueries.PageSize"/>건의 프로젝션 결과 + 요청마다 새로 만드는 <see cref="Pager"/> 인스턴스.</description></item>
/// <item><description><b>Blocking:</b> <see cref="OnGetAsync"/>가 비동기 Non-blocking으로 DB를 조회한다.</description></item>
/// </list>
/// </remarks>
public sealed class IndexModel(PublicDbContext db, IOptions<SiteOptions> site) : PublicPageModel(site)
{
    /// <summary>쪽 번호 상한(스펙 3.4). OFFSET 비용의 상한이기도 하다.</summary>
    public const int MaxPage = 500;

    /// <summary>이번 요청의 목록 한 쪽.</summary>
    public PublicPage<PublicPostSummary> Posts { get; private set; } = null!;

    /// <summary>목록 하단 이전/다음 링크 모델.</summary>
    public PagerModel Pager => new("/", null, Posts.Page, Math.Min(Posts.LastPage, MaxPage));

    /// <summary>쪽 번호를 읽어 그 쪽의 최신 글 목록을 채운다.</summary>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>정상이면 이 페이지, 쪽 번호가 형식 밖이거나 범위를 벗어나면 404.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not Thread-safe. 이 요청 인스턴스 안에서만 호출된다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="PublicQueries.LatestAsync"/> 문서 참조(최대 <see cref="PublicQueries.PageSize"/>건).</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. DB 조회 1회(<c>COUNT</c>+목록 SELECT)를 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!PageNumber.TryRead(Request.Query, MaxPage, out var page)) return NotFound();
        Posts = await PublicQueries.LatestAsync(db, page, ct);
        if (page > 1 && Posts.Items.Count == 0) return NotFound();
        SetHead(page == 1 ? null : $"{page}쪽", Site.Description, page == 1 ? "/" : $"/?page={page}");
        return Page();
    }
}
