using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Infrastructure.Access;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Pages;

/// <summary>공개 검색. 비용 상한은 세 겹이다: 검색 속도 제한(IP별 분당 + 전역 동시 실행), 쪽 번호 상한, 연결의 statement_timeout.</summary>
/// <param name="db">검색 조회에 쓸 공개 전용 컨텍스트.</param>
/// <param name="site">사이트 설정(제목·소개·origin).</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. Razor Pages가 요청마다 새 인스턴스를 만들어 스레드 간 공유가 없다.</description></item>
/// <item><description><b>Memory Allocation:</b> 유효성 검사를 통과하면 <see cref="Posts"/>가 보유하는 최대 <see cref="PublicQueries.PageSize"/>건의 프로젝션 결과. 실패하면 안내 메시지 문자열 1개(보간 문자열)뿐이고 DB 결과는 없다.</description></item>
/// <item><description><b>Blocking:</b> <see cref="OnGetAsync"/>가 비동기 Non-blocking으로 DB를 조회한다(유효성 검사 실패 시에는 DB에 닿지 않는다).</description></item>
/// </list>
/// </remarks>
public sealed class SearchModel(PublicDbContext db, IOptions<SiteOptions> site) : PublicPageModel(site)
{
    /// <summary>검색어 최소 길이(트림 후, UTF-16 단위). 한 글자 검색은 거의 모든 글에 맞아 전체 스캔 + 큰 결과만 만든다.</summary>
    public const int MinLength = 2;

    /// <summary>검색어 최대 길이(스펙 3.4).</summary>
    public const int MaxLength = 100;

    /// <summary>검색 결과 쪽 번호 상한(스펙 3.7).</summary>
    public const int MaxPage = 50;

    /// <summary>입력란에 되돌릴 값. 길이 초과·NUL·반복 매개변수면 빈 문자열.</summary>
    public string Query { get; private set; } = string.Empty;

    /// <summary>검색어 유효성 검사에 실패했을 때 보여줄 안내 문구. 검색어가 없거나(빈 폼) 유효한 검색이면 <see langword="null"/>.</summary>
    public string? Error { get; private set; }

    /// <summary>유효한 검색이 실행됐을 때의 결과 한 쪽. 빈 폼이거나 <see cref="Error"/>가 있으면 <see langword="null"/>.</summary>
    public PublicPage<PublicPostSummary>? Posts { get; private set; }

    /// <summary>목록 하단 이전/다음 링크 모델.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not Thread-safe. 이 요청 인스턴스 전용 <see cref="Posts"/>·<see cref="Query"/>만 읽는다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 접근할 때마다 새 <see cref="PagerModel"/> 인스턴스 1개를 할당한다(캐시하지 않음). 뷰는 <see cref="Posts"/>가 <see langword="null"/>이 아닐 때만 한 요청에서 한 번 읽는다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public PagerModel Pager => new("/search", Query, Posts!.Page, Math.Min(Posts.LastPage, MaxPage));

    /// <summary><c>q</c>를 읽어 검증하고, 유효하면 그 검색어로 <c>page</c> 쪽의 결과를 채운다.</summary>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns><c>q</c>가 없거나 공백뿐이면 빈 폼(200). 트림 후 <see cref="MinLength"/>~<see cref="MaxLength"/>자인 <c>q</c> 하나면 검색 결과(200).
    /// 그 밖의 <c>q</c>(반복·범위 밖 길이·NUL 포함)는 안내문과 함께 400. <c>page</c>가 형식 밖이거나 상한을 넘거나 결과가 없는 쪽이면 404.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not Thread-safe. 이 요청 인스턴스 안에서만 호출된다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 유효성 검사 실패 시 안내 메시지 문자열 1개뿐이다. 유효한 검색이면 <see cref="PublicQueries.SearchAsync"/> 문서 참조(최대 <see cref="PublicQueries.PageSize"/>건).</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. <c>q</c>·<c>page</c> 검증은 DB 접근 전에 끝나므로 잘못된 입력은 DB에 닿지 않는다 — 검증을 통과했을 때만
    /// <see cref="PublicQueries.SearchAsync"/>가 <c>COUNT</c> 1회 + 목록 SELECT 1회, 총 2회를 순차 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        SetHead("검색", null, "/search", noIndex: true);
        var values = Request.Query["q"];
        if (values.Count > 1) return Invalid("검색어는 하나만 보낼 수 있습니다.");
        var term = values.Count == 0 ? null : values[0]?.Trim();
        if (string.IsNullOrEmpty(term)) return Page();
        if (term.Length > MaxLength || TextRules.ContainsNul(term)) return Invalid($"검색어는 {MinLength}~{MaxLength}자여야 합니다.");
        Query = term;
        if (term.Length < MinLength) return Invalid($"검색어는 {MinLength}~{MaxLength}자여야 합니다.");
        if (!PageNumber.TryRead(Request.Query, MaxPage, out var page)) return NotFound();

        Posts = await PublicQueries.SearchAsync(db, term, page, ct);
        return page > 1 && Posts.Items.Count == 0 ? NotFound() : Page();
    }

    // 본문이 있는 400: 실측(InvalidInput 테스트) — Page()가 이미 Content-Type을 text/html로 설정하고 본문을 쓴 뒤이므로
    // StatusCodePages(ErrorResponses.HandleStatusCodeAsync)가 이 응답을 덮어쓰지 않는다.
    private PageResult Invalid(string message)
    {
        Error = message;
        Response.StatusCode = StatusCodes.Status400BadRequest;
        return Page();
    }
}
