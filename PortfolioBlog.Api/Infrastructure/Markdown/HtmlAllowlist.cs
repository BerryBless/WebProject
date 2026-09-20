using AngleSharp.Dom;
using ColorCode;
using ColorCode.Styling;
using Ganss.Xss;

namespace PortfolioBlog.Api.Infrastructure.Markdown;

/// <summary>최종 HTML에 적용하는 허용 목록. 파서 설정(raw HTML 비활성)과 URL 정책이 1·2차 방어이고 이것이 3차다:
/// 하이라이터나 Markdig 확장에 버그가 있어도 허용 목록 밖의 태그·속성·클래스·스킴은 출력에 남지 못한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> <see cref="Create"/>가 돌려준 인스턴스는 구성을 바꾸지 않는 한 여러 스레드에서 동시에 <c>Sanitize</c>할 수 있다(호출마다 새 DOM을 만든다).</description></item>
/// <item><description><b>Memory Allocation:</b> <c>Sanitize</c>는 입력 HTML을 DOM으로 파싱한다 — 입력 크기의 수 배.</description></item>
/// <item><description><b>Blocking:</b> 동기 CPU 작업.</description></item>
/// </list>
/// </remarks>
public static class HtmlAllowlist
{
    public static IReadOnlySet<string> AllowedTags { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "p", "br", "hr", "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "ul", "ol", "li", "pre", "code", "span", "div",
        "em", "strong", "del", "sup", "a", "img", "table", "thead", "tbody", "tr", "th", "td", "input",
    };

    public static IReadOnlySet<string> AllowedAttributes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "href", "src", "alt", "title", "id", "class", "type", "checked", "disabled",
    };

    private static readonly string[] MarkdigClasses = ["contains-task-list", "task-list-item", "footnotes", "footnote-ref", "footnote-back-ref"];

    public static HtmlSanitizer Create()
    {
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedTags.Clear();
        foreach (var tag in AllowedTags) sanitizer.AllowedTags.Add(tag);
        sanitizer.AllowedAttributes.Clear();
        foreach (var attribute in AllowedAttributes) sanitizer.AllowedAttributes.Add(attribute);
        sanitizer.AllowedSchemes.Clear();
        foreach (var scheme in new[] { "http", "https", "mailto" }) sanitizer.AllowedSchemes.Add(scheme);
        sanitizer.AllowedCssProperties.Clear(); // style 속성 자체를 허용하지 않지만 이중으로 비운다
        sanitizer.AllowedAtRules.Clear();
        sanitizer.AllowDataAttributes = false;

        // 클래스 허용 목록: 하이라이터가 쓰는 스타일 이름 + 언어 컨테이너 이름 + Markdig 확장이 붙이는 것. 비어 있으면 "모든 클래스 허용"이 되므로 반드시 채운다.
        foreach (var style in StyleDictionary.DefaultLight)
        {
            if (!string.IsNullOrEmpty(style.ReferenceName)) sanitizer.AllowedClasses.Add(style.ReferenceName);
        }
        foreach (var language in Languages.All) sanitizer.AllowedClasses.Add(language.CssClassName);
        foreach (var name in MarkdigClasses) sanitizer.AllowedClasses.Add(name);

        // input은 작업 목록의 비활성 체크박스로만 남긴다.
        sanitizer.PostProcessNode += static (_, e) =>
        {
            if (e.Node is IElement { LocalName: "input" } input && input.GetAttribute("type") != "checkbox") input.Remove();
        };
        return sanitizer;
    }
}
