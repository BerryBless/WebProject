using PortfolioBlog.Api.Infrastructure.Markdown;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>강조 CSS는 라이브러리 출력에서 클래스 규칙만 남긴 것이다(기본 거부).</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 다른 테스트와 공유하는 가변 상태가 없다. <see cref="HighlightCss.Value"/>는 프로세스 전체가 공유하는 지연 계산 캐시를 읽기만 한다.</description></item>
/// <item><description><b>Memory Allocation:</b> <see cref="Css_CoversTheClassesTheRendererEmits"/>는 <see cref="MarkdownRenderer"/> 인스턴스 1개를 새로 만들어 렌더링한다(그 밖은 문자열 비교뿐).</description></item>
/// <item><description><b>Blocking:</b> 동기 실행. I/O 없음.</description></item>
/// </list>
/// </remarks>
public sealed class HighlightCssTests
{
    [Fact]
    public void Css_ContainsOnlyClassRules_InLightAndDark()
    {
        var css = HighlightCss.Value;
        Assert.Contains(".keyword{", css, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-color-scheme: dark){", css, StringComparison.Ordinal);
        Assert.DoesNotContain("body", css, StringComparison.Ordinal);        // 라이브러리의 body 배경 규칙은 버린다
        Assert.DoesNotContain(".plainText", css, StringComparison.Ordinal);  // color가 두 번 나오는 버그 규칙(밝은 테마에서 흰 글자)
        foreach (var banned in new[] { "<", "url(", "@import", "expression", "javascript:" })
        {
            Assert.DoesNotContain(banned, css, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(css.Count(c => c == '{'), css.Count(c => c == '}'));
    }

    /// <summary>렌더러가 실제로 내는 클래스가 CSS에 있다(라이브러리 버전이 바뀌어 이름이 달라지면 실패).</summary>
    [Fact]
    public void Css_CoversTheClassesTheRendererEmits()
    {
        var html = new MarkdownRenderer().Render("```csharp\n// c\nvar s = \"x\";\n```\n");
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(html, "<span class=\"([A-Za-z]+)\""))
        {
            Assert.Contains("." + m.Groups[1].Value + "{", HighlightCss.Value, StringComparison.Ordinal);
        }
    }
}
