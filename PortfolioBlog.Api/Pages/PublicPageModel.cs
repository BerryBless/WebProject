using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Infrastructure.Access;

namespace PortfolioBlog.Api.Pages;

/// <summary>공개 페이지 모델의 공통 기반: 사이트 설정과 머리 정보 설정.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. <see cref="PageModel"/>과 마찬가지로 요청마다 새 인스턴스가 만들어지므로 인스턴스 간 공유가 없다.</description></item>
/// <item><description><b>Memory Allocation:</b> <see cref="SetHead"/> 호출마다 <see cref="PageHead"/> 인스턴스 1개를 할당한다.</description></item>
/// <item><description><b>Blocking:</b> 해당 없음. 이 클래스 자체는 I/O를 하지 않는다(파생 클래스의 <c>OnGetAsync</c>가 한다).</description></item>
/// </list>
/// </remarks>
public abstract class PublicPageModel(IOptions<SiteOptions> site) : PageModel
{
    /// <summary>레이아웃이 머리 정보를 읽는 ViewData 키.</summary>
    public const string HeadKey = "Head";

    /// <summary>현재 사이트 설정(제목·소개·두 origin).</summary>
    protected SiteOptions Site { get; } = site.Value;

    /// <summary>레이아웃이 그릴 머리 정보를 <see cref="PageModel.ViewData"/>에 채운다.</summary>
    /// <param name="title">쪽 제목. 없으면 사이트 제목만 쓴다.</param>
    /// <param name="description">meta description. 비어 있으면 생략.</param>
    /// <param name="path">이 쪽의 정식 경로(<c>/</c>로 시작). 절대 URL은 <c>Site:PublicOrigin</c>을 붙여 만든다.</param>
    /// <param name="ogType"><c>og:type</c> 값(기본 <c>website</c>).</param>
    /// <param name="imagePath"><c>/attachments/…</c> 경로. 렌더러의 URL 정책을 통과한 값만 넘긴다.</param>
    /// <param name="noIndex">검색 결과 쪽처럼 색인하지 말아야 하면 <see langword="true"/>.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not Thread-safe. 요청 하나에 속한 <see cref="PageModel.ViewData"/>만 건드린다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="PageHead"/> 인스턴스 1개 + 제목 조합용 보간 문자열(제목이 있을 때만).</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    protected void SetHead(string? title, string? description, string path, string ogType = "website", string? imagePath = null, bool noIndex = false) =>
        ViewData[HeadKey] = new PageHead(
            string.IsNullOrEmpty(title) ? Site.Title : $"{title} · {Site.Title}", Site.Title,
            string.IsNullOrWhiteSpace(description) ? null : description,
            Site.PublicOrigin + path, ogType, imagePath is null ? null : Site.PublicOrigin + imagePath, noIndex);
}
