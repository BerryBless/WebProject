using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>응답 HTML을 브라우저와 같은 규칙(HTML5 파서)으로 읽는다. 문자열 검색으로 "태그가 없다"를 단언하지 않기 위한 도구.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 정적 클래스이며 호출마다 독립된 <see cref="AngleSharp.Html.Parser.HtmlParser"/>·문서를 만든다(공유 가변 상태 없음).</description></item>
/// <item><description><b>Memory Allocation:</b> 호출마다 새 <see cref="AngleSharp.Html.Parser.HtmlParser"/> 인스턴스와 파싱된 DOM 트리(응답 크기에 비례)를 힙에 할당한다.</description></item>
/// <item><description><b>Blocking:</b> <see cref="Parse"/>는 동기 CPU 작업(즉시 반환). <see cref="GetAsync"/>는 HTTP 왕복을 <c>await</c>하는 비동기 Non-blocking이다.</description></item>
/// </list>
/// </remarks>
internal static class HtmlDoc
{
    /// <summary>HTML 문자열을 파싱한다.</summary>
    /// <param name="html">파싱할 HTML 원문.</param>
    /// <returns>파싱된 DOM 문서.</returns>
    public static IHtmlDocument Parse(string html) => new HtmlParser().ParseDocument(html);

    /// <summary>GET 요청을 보내고 상태 코드·Content-Type을 확인한 뒤 응답 본문을 파싱한다.</summary>
    /// <param name="client">요청에 쓸 <see cref="HttpClient"/>.</param>
    /// <param name="url">요청할 경로.</param>
    /// <param name="expected">기대하는 응답 상태 코드(기본값 200).</param>
    /// <returns>파싱된 DOM 문서.</returns>
    public static async Task<IHtmlDocument> GetAsync(HttpClient client, string url, System.Net.HttpStatusCode expected = System.Net.HttpStatusCode.OK)
    {
        using var res = await client.GetAsync(url);
        Assert.Equal(expected, res.StatusCode);
        Assert.Equal("text/html", res.Content.Headers.ContentType?.MediaType);
        return Parse(await res.Content.ReadAsStringAsync());
    }
}
