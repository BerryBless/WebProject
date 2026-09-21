using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Infrastructure.Access;
using PortfolioBlog.Api.Infrastructure.Data;
using PortfolioBlog.Api.Infrastructure.Web;

namespace PortfolioBlog.Api.Pages;

/// <summary>태그별 글 목록. 경로 값은 라우팅이 URL 디코딩한 뒤의 문자열이며, 저장 때와 같은 규칙으로 정규화해 찾는다.</summary>
/// <param name="db">태그·목록 조회에 쓸 공개 전용 컨텍스트.</param>
/// <param name="site">사이트 설정(제목·소개·origin).</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. Razor Pages가 요청마다 새 인스턴스를 만들어 스레드 간 공유가 없다.</description></item>
/// <item><description><b>Memory Allocation:</b> <see cref="Tag"/> 1건 + <see cref="Posts"/>가 보유하는 최대 <see cref="PublicQueries.PageSize"/>건.</description></item>
/// <item><description><b>Blocking:</b> <see cref="OnGetAsync"/>가 비동기 Non-blocking으로 DB를 조회한다.</description></item>
/// </list>
/// </remarks>
public sealed class TagPageModel(PublicDbContext db, IOptions<SiteOptions> site) : PublicPageModel(site)
{
    /// <summary>이번 요청의 태그(정규화 이름과 표시 이름).</summary>
    public PublicTag Tag { get; private set; } = null!;

    /// <summary>이 태그가 붙은 글의 한 쪽.</summary>
    public PublicPage<PublicPostSummary> Posts { get; private set; } = null!;

    // OnGetAsync가 이미 계산한(그리고 null이 아님을 확인한) 태그 경로를 그대로 재사용한다. Pager에서 PublicUrls.Tag(...)를
    // 다시 호출하면 "."·".."이 아님을 보장하는 널 억제(!)가 멀리 떨어진 그 가드에 의존하게 되므로, 계산 시점의 값을 직접 들고 있는다.
    private string _path = null!;

    /// <summary>목록 하단 이전/다음 링크 모델.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not Thread-safe. 이 요청 인스턴스 전용 필드만 읽는다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 접근할 때마다 새 <see cref="PagerModel"/> 인스턴스 1개를 할당한다(캐시하지 않음). 뷰가 한 요청에서 한 번만 읽는다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public PagerModel Pager => new(_path, null, Posts.Page, Math.Min(Posts.LastPage, IndexModel.MaxPage));

    /// <summary>경로의 태그 이름을 정규화해 그 태그가 붙은 글 목록을 채운다.</summary>
    /// <param name="tag">요청 경로의 태그 이름(URL 디코딩된 원문).</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>정상이면 이 페이지, 입력이 형식 밖이거나 태그가 없거나 쪽 번호가 범위를 벗어나면 404.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not Thread-safe. 이 요청 인스턴스 안에서만 호출된다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="TagResolver.Normalize"/>의 정규화 문자열 1개 + <see cref="PublicQueries.ByTagAsync"/>의 결과.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. <see cref="PublicQueries.ByTagAsync"/>가 태그 조회 1회 + <c>COUNT</c> 1회 + 목록 SELECT 1회, 총 3회를 순차 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    public async Task<IActionResult> OnGetAsync(string tag, CancellationToken ct)
    {
        // 정규화 전에 길이를 먼저 자른다: 정규화(NFC)는 입력 길이에 비례하는 작업이다. 공백 축소 여지를 두고 상한의 4배까지만 받는다.
        if (tag.Length > AppDbContext.TagMax * 4 || TextRules.ContainsNul(tag)) return NotFound();
        var key = TagResolver.Normalize(tag);
        if (key.Length is 0 or > AppDbContext.TagMax || PublicUrls.Tag(key) is not { } path) return NotFound();
        if (!PageNumber.TryRead(Request.Query, IndexModel.MaxPage, out var page)) return NotFound();

        var found = await PublicQueries.ByTagAsync(db, key, page, ct);
        if (found is null || (page > 1 && found.Value.Posts.Items.Count == 0)) return NotFound();
        (Tag, Posts) = found.Value;
        _path = path;
        SetHead($"태그: {Tag.Name}", null, page == 1 ? path : $"{path}?page={page}");
        return Page();
    }
}
