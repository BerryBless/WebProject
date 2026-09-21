namespace PortfolioBlog.Api.Pages;

/// <summary>이전/다음 링크. <c>asp-route-page</c>는 Razor Pages의 예약 키와 충돌하므로 문자열로 만든다.</summary>
/// <param name="BasePath">쪽 번호를 뺀 경로(<c>/</c>, <c>/tags/c%23</c>, <c>/search</c>). 이미 인코딩된 값.</param>
/// <param name="Query">검색어(원문). 없으면 <c>null</c>.</param>
/// <param name="Page">현재 쪽 번호.</param>
/// <param name="LastPage">마지막 쪽 번호.</param>
public sealed record PagerModel(string BasePath, string? Query, int Page, int LastPage)
{
    /// <summary>주어진 쪽으로 가는 링크를 만든다.</summary>
    /// <param name="page">링크가 가리킬 쪽 번호.</param>
    /// <returns>1쪽이고 검색어가 없으면 <see cref="BasePath"/> 그대로, 그 밖은 <c>?[q=…&amp;]page=…</c>를 붙인 경로.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 불변 record의 값만 읽는다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 보간 문자열 1개(검색어가 있으면 <see cref="Uri.EscapeDataString"/> 결과 1개 추가).</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking).</description></item>
    /// </list>
    /// </remarks>
    public string Href(int page)
    {
        var q = Query is null ? string.Empty : "q=" + Uri.EscapeDataString(Query) + "&";
        return page == 1 && Query is null ? BasePath : $"{BasePath}?{q}page={page}";
    }
}
