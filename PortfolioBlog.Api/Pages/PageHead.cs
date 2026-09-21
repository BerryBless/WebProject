namespace PortfolioBlog.Api.Pages;

/// <summary>레이아웃의 <c>&lt;head&gt;</c>가 쓰는 값. 전부 Razor가 인코딩해 출력한다.</summary>
/// <param name="Title"><c>&lt;title&gt;</c>과 og:title.</param>
/// <param name="SiteTitle">머리글·og:site_name·피드 링크 제목.</param>
/// <param name="Description">meta description·og:description. 비어 있으면 생략.</param>
/// <param name="CanonicalUrl"><c>Site:PublicOrigin</c>으로 만든 절대 URL(요청 Host가 아니다).</param>
/// <param name="OgType"><c>website</c> 또는 <c>article</c>.</param>
/// <param name="OgImageUrl">절대 URL. 없으면 og:image를 내지 않는다.</param>
/// <param name="NoIndex">검색 결과 쪽처럼 색인하지 말아야 하는 쪽.</param>
public sealed record PageHead(string Title, string SiteTitle, string? Description, string CanonicalUrl, string OgType, string? OgImageUrl, bool NoIndex);
