using System.Diagnostics;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using PortfolioBlog.Api.Infrastructure.Markdown;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>마크다운 → 안전한 HTML 파이프라인 테스트. 문자열 비교가 아니라 <b>출력을 다시 파싱한 DOM</b>으로 "실행 가능한 것이 0개"임을 검증한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 렌더러는 불변 싱글턴이라 테스트 간 공유한다(병렬 테스트 1개가 이를 직접 검증).</description></item>
/// <item><description><b>Memory Allocation:</b> 입력 크기에 비례. 가장 큰 입력은 약 190KB.</description></item>
/// <item><description><b>Blocking:</b> 동기 CPU 작업만. DB·Docker 불필요.</description></item>
/// </list>
/// </remarks>
public sealed class MarkdownRendererTests
{
    private static readonly MarkdownRenderer Renderer = new();

    private static IElement Parse(string html) => new HtmlParser().ParseDocument("<body>" + html + "</body>").Body!;

    /// <summary>출력 DOM 전체를 훑어 허용 목록 밖의 태그·속성, 이벤트 핸들러, 정책 밖 URL이 하나도 없음을 단언한다.
    /// href·src는 <see cref="UrlPolicy"/> 자체 판정과, 정책이 뚫려도 잡히도록 정책과 무관하게 고정한 접두사 검사를 함께 건다
    /// (정책이 너무 느슨해지는 회귀는 UrlPolicy만으로 검사하면 놓친다).</summary>
    private static void AssertInert(string html)
    {
        foreach (var element in Parse(html).QuerySelectorAll("*"))
        {
            Assert.True(HtmlAllowlist.AllowedTags.Contains(element.LocalName), $"허용되지 않은 태그: <{element.LocalName}> in {html}");
            foreach (var attribute in element.Attributes)
            {
                Assert.True(HtmlAllowlist.AllowedAttributes.Contains(attribute.Name), $"허용되지 않은 속성: {attribute.Name} in {html}");
                Assert.False(attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase), $"이벤트 핸들러 속성: {attribute.Name}");
            }
            if (element.GetAttribute("href") is { } href)
            {
                Assert.True(UrlPolicy.IsAllowedLink(href), $"정책 밖 href: {href}");
                Assert.True(href.StartsWith("http://", StringComparison.Ordinal) || href.StartsWith("https://", StringComparison.Ordinal)
                    || href.StartsWith("mailto:", StringComparison.Ordinal) || href.StartsWith('#')
                    || (href.StartsWith('/') && !href.StartsWith("//", StringComparison.Ordinal)),
                    $"정책과 무관하게 고정한 href 접두사 검사 실패: {href}");
            }
            if (element.GetAttribute("src") is { } src)
            {
                Assert.True(UrlPolicy.IsAllowedImage(src), $"정책 밖 src: {src}");
                Assert.True(src.StartsWith("/attachments/", StringComparison.Ordinal), $"정책과 무관하게 고정한 src 접두사 검사 실패: {src}");
            }
            if (element.LocalName == "input") Assert.Equal("checkbox", element.GetAttribute("type"));
        }
    }

    /// <summary>공격 입력은 전부 비활성 출력이 된다.</summary>
    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("hello <img src=x onerror=alert(1)>")]
    [InlineData("<iframe src=\"//evil.test\"></iframe>")]
    [InlineData("<svg onload=alert(1)>")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>")]
    [InlineData("<form action=\"/api/auth/logout\" method=post><button>x</button></form>")]
    [InlineData("<details open ontoggle=alert(1)>x</details>")]
    [InlineData("<style>body{display:none}</style>")]
    [InlineData("<base href=\"//evil.test/\">")]
    [InlineData("<meta http-equiv=refresh content=\"0;url=//evil.test\">")]
    [InlineData("[x](javascript:alert(1))")]
    [InlineData("[x](JaVaScRiPt:alert(1))")]
    [InlineData("[x](&#106;avascript:alert(1))")]
    [InlineData("[x](&#x6A;avascript&colon;alert(1))")]
    [InlineData("[x](vbscript:msgbox(1))")]
    [InlineData("[x](data:text/html;base64,PHNjcmlwdD4=)")]
    [InlineData("[x](//evil.test/x)")]
    [InlineData("[x](/\\evil.test)")]
    [InlineData("<javascript:alert(1)>")]
    [InlineData("![i](https://evil.test/pixel.png)")]
    [InlineData("![i](data:image/png;base64,AAAA)")]
    [InlineData("![i](/attachments/../api/posts)")]
    [InlineData("![i](/attachments/0192f0c4-7a3b-7c1d-9e2f-1a2b3c4d5e6f/x.png \"t\\\" onerror=\\\"alert(1)\")")]
    [InlineData("[x](https://ok.test \"t\\\" onmouseover=\\\"alert(1)\")")]
    [InlineData("# Title {#id .cls onclick=alert(1)}")]
    [InlineData("text{onmouseover=alert(1)}")]
    [InlineData("```html\n<script>alert(1)</script>\n```")]
    [InlineData("```\"><script>alert(1)</script>\ncode\n```")]
    [InlineData("    <script>alert(1)</script>")]
    [InlineData("`<script>alert(1)</script>`")]
    [InlineData("| a |\n|---|\n| <script>alert(1)</script> |")]
    [InlineData("- [x] <input type=text autofocus onfocus=alert(1)>")]
    [InlineData("```html\n<img src=\"https://evil.test/x.png\" onerror=\"alert(1)\">\n```")]
    public void Render_AttackInput_ProducesInertOutput(string markdown)
    {
        var html = Renderer.Render(markdown);
        AssertInert(html);
        var body = Parse(html);
        Assert.Empty(body.QuerySelectorAll("script, iframe, svg, form, button, style, base, meta, details, object, embed, link"));
    }

    /// <summary>거부된 링크는 사라지되 글자와 안쪽 서식은 남고, 텍스트가 중복되지 않는다.</summary>
    [Fact]
    public void Render_RejectedLink_KeepsTextOnce_AndInnerFormatting()
    {
        var body = Parse(Renderer.Render("**[굵은 *기울임* 링크](javascript:alert(1))** 끝"));
        Assert.Empty(body.QuerySelectorAll("a"));
        Assert.Equal("굵은 기울임 링크 끝", body.TextContent.Trim());
        Assert.NotNull(body.QuerySelector("strong em"));
    }

    /// <summary>거부된 이미지는 대체 텍스트만 남는다.</summary>
    [Fact]
    public void Render_RejectedImage_LeavesAltText()
    {
        var body = Parse(Renderer.Render("앞 ![대체 텍스트](https://evil.test/a.png) 뒤"));
        Assert.Empty(body.QuerySelectorAll("img"));
        Assert.Equal("앞 대체 텍스트 뒤", body.TextContent.Trim());
    }

    /// <summary>정상 문서의 기능은 유지된다: 링크·자체 첨부 이미지·표·작업 목록·취소선·각주·제목 앵커·자동 링크.</summary>
    [Fact]
    public void Render_LegitimateDocument_KeepsFeatures()
    {
        const string markdown = """
            ## 한글 제목

            [외부](https://example.test/a) [내부](/posts/x) [앵커](#한글-제목) <https://auto.test/b> https://bare.test/c

            ![도식](/attachments/0192f0c4-7a3b-7c1d-9e2f-1a2b3c4d5e6f/diagram.png "캡션")

            | 열1 | 열2 |
            |---|---|
            | a | b |

            - [x] 완료
            - [ ] 예정

            ~~취소~~ 각주[^1]

            [^1]: 각주 본문
            """;
        var html = Renderer.Render(markdown);
        AssertInert(html);
        var body = Parse(html);

        Assert.Equal("한글-제목", body.QuerySelector("h2")!.Id);
        Assert.Equal(5, body.QuerySelector("h2 + p")!.QuerySelectorAll("a[href]").Length); // 제목 바로 다음 문단의 링크 5개(각주 앵커는 다른 문단에 있다)
        var img = body.QuerySelector("img")!;
        Assert.Equal("/attachments/0192f0c4-7a3b-7c1d-9e2f-1a2b3c4d5e6f/diagram.png", img.GetAttribute("src"));
        Assert.Equal("도식", img.GetAttribute("alt"));
        Assert.Equal("캡션", img.GetAttribute("title"));
        Assert.NotNull(body.QuerySelector("table thead th"));
        Assert.Equal(2, body.QuerySelectorAll("li.task-list-item input[type=checkbox][disabled]").Length);
        Assert.NotNull(body.QuerySelector("del"));
        Assert.NotNull(body.QuerySelector("a.footnote-ref"));
    }

    /// <summary>지원 언어는 CSS 클래스로만 강조하고(인라인 style 금지 — CSP style-src 'self'), 코드 안의 HTML은 이스케이프된다. 미지원 언어는 일반 코드블록이다.</summary>
    [Fact]
    public void Render_CodeBlocks_HighlightWithClassesOnly_AndEscapeContent()
    {
        var html = Renderer.Render("```csharp\nvar s = \"<b>\"; // 주석\n```\n\n```bash\necho \"<x>\"\n```\n");
        AssertInert(html);
        var body = Parse(html);

        Assert.NotNull(body.QuerySelector("div.csharp pre span.keyword"));
        Assert.Empty(body.QuerySelectorAll("[style]"));
        Assert.Empty(body.QuerySelectorAll("b, x"));
        var plain = body.QuerySelectorAll("pre > code").Single();
        Assert.Contains("echo \"<x>\"", plain.TextContent, StringComparison.Ordinal);
    }

    /// <summary>주어진 언어로 <paramref name="totalLength"/>자짜리 한 줄짜리 코드 펜스를 만든다. <c>&lt;b&gt;</c>를 포함시켜 이스케이프 여부도 같이 확인할 수 있게 한다.</summary>
    private static string OneLineCodeFence(string language, int totalLength)
    {
        const string marker = "var s = \"<b>\"; ";
        var line = totalLength <= marker.Length ? marker[..totalLength] : marker + new string('x', totalLength - marker.Length);
        return $"```{language}\n{line}\n```\n";
    }

    /// <summary>짧은 줄(<see cref="HighlightingCodeBlockRenderer.MaxHighlightLineLength"/> 미만)을 반복해 대략 <paramref name="approxLength"/>자짜리 코드 펜스를 만든다.
    /// 줄 예산이 아니라 블록·문서 예산만 걸리게 하려는 목적이다.</summary>
    private static string ManyShortLinesCodeFence(string language, int approxLength)
    {
        const string line = "var a=1;\n"; // 9자, 줄 예산(400자)에 전혀 걸리지 않는다
        var repeats = approxLength / line.Length;
        return $"```{language}\n{string.Concat(Enumerable.Repeat(line, repeats))}```\n";
    }

    /// <summary>리뷰가 측정한 병적 입력(최대 12분 25초)이 예산 적용 후에는 전부 5초 안에 끝나는지 검증한다.
    /// 200KB 문자열을 <c>[InlineData]</c> 속성에 직접 박지 않고 이 메서드 안에서 조립한다.</summary>
    /// <param name="caseName">조립할 병적 입력의 종류.</param>
    [Theory]
    [InlineData("css-long-line")]
    [InlineData("html-attr-soup")]
    [InlineData("csharp-one-line")]
    [InlineData("identical-headings")]
    public void Render_PathologicalCodeAndHeadings_FinishQuickly(string caseName)
    {
        var markdown = caseName switch
        {
            "css-long-line" => "```css\n" + "a{b:\"" + new string('f', 200_000) + "\n```\n",
            "html-attr-soup" => "```html\n" + string.Concat(Enumerable.Repeat("<a a=\"a ", 25_000)) + "\n```\n",
            "csharp-one-line" => "```csharp\n" + string.Concat(Enumerable.Repeat("var a=1;", 25_000)) + "\n```\n",
            "identical-headings" => string.Concat(Enumerable.Repeat("# heading\n\n", 18_600)),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName)),
        };
        Assert.True(Encoding.UTF8.GetByteCount(markdown) <= MarkdownRenderer.MaxInputBytes,
            $"{caseName} 입력이 상한을 넘어 크기 검증에서 먼저 걸립니다: {Encoding.UTF8.GetByteCount(markdown)}바이트");

        var stopwatch = Stopwatch.StartNew();
        Renderer.Render(markdown);
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"{caseName}이(가) {stopwatch.Elapsed}만에 끝났습니다(5초 상한 초과).");
    }

    /// <summary>예산을 넘는 줄(401자)이 있는 블록은 강조를 포기하고 이스케이프한 일반 코드블록이 되며,
    /// 예산 이내(400자)인 블록은 그대로 강조되는지 검증한다.</summary>
    [Fact]
    public void Render_OverBudgetCodeBlock_FallsBackToPlainEscapedBlock()
    {
        var overHtml = Renderer.Render(OneLineCodeFence("csharp", HighlightingCodeBlockRenderer.MaxHighlightLineLength + 1));
        AssertInert(overHtml);
        var overBody = Parse(overHtml);
        Assert.Empty(overBody.QuerySelectorAll("div.csharp"));
        Assert.Empty(overBody.QuerySelectorAll("span.keyword"));
        Assert.Empty(overBody.QuerySelectorAll("b"));
        var overCode = overBody.QuerySelector("pre > code")!;
        Assert.Contains("<b>", overCode.TextContent, StringComparison.Ordinal);

        var atHtml = Renderer.Render(OneLineCodeFence("csharp", HighlightingCodeBlockRenderer.MaxHighlightLineLength));
        AssertInert(atHtml);
        Assert.NotNull(Parse(atHtml).QuerySelector("div.csharp span.keyword"));
    }

    /// <summary>강조 예산은 렌더러 인스턴스가 아니라 한 번의 <c>Render</c> 호출(문서)에 매인다: 예산을 다 쓴 문서 안 뒤쪽 블록은 밀리지만,
    /// 같은 인스턴스로 그 다음에 렌더링한 작은 문서는 다시 처음부터 강조된다.</summary>
    [Fact]
    public void Render_HighlightBudget_IsPerDocument_NotShared()
    {
        var fourBlocks = string.Concat(Enumerable.Repeat(ManyShortLinesCodeFence("csharp", 19_000), 4));
        var html = Renderer.Render(fourBlocks);
        AssertInert(html);
        var body = Parse(html);
        Assert.Equal(3, body.QuerySelectorAll("div.csharp").Length);
        Assert.Equal(1, body.QuerySelectorAll("pre > code").Length);

        var again = Renderer.Render("```csharp\nvar a = 1;\n```\n");
        Assert.NotNull(Parse(again).QuerySelector("div.csharp span.keyword"));
    }

    /// <summary>제목 id는 선형 슬러그화로 생성되고, 중복은 서로 다른 id로 풀리며, 길이는 상한(80자) 이내이고, 위험 문자가 섞이지 않는지 검증한다.</summary>
    [Fact]
    public void Render_HeadingIds_AreSlugged_Deduplicated_AndBounded()
    {
        var longHeading = new string('a', 300);
        var markdown = $"## Hello World\n\n## Hello World\n\n## Hello World-1\n\n## !!!\n\n## {longHeading}\n\n# <script>\n";
        var html = Renderer.Render(markdown);
        var ids = Parse(html).QuerySelectorAll("h1, h2").Select(h => h.Id!).ToArray(); // HeadingIds.Assign이 모든 제목에 id를 붙이므로 null이 아님을 단언한다

        Assert.Equal(6, ids.Length);
        Assert.Equal("hello-world", ids[0]);
        Assert.Equal(ids.Length, ids.Distinct().Count()); // 전부 서로 다르다
        Assert.Equal("section", ids[3]); // 구두점만 있는 제목
        Assert.True(ids[4].Length <= 80);
        Assert.DoesNotContain('<', ids[5]);
        Assert.DoesNotContain('>', ids[5]);
        Assert.DoesNotContain('"', ids[5]);
        Assert.DoesNotContain(ids[5].ToCharArray(), char.IsWhiteSpace);
    }

    /// <summary>Markdig 1.4.0의 중첩 한도(128단계)를 넘는 최소 입력들이 500이 아니라 <see cref="MarkdownTooComplexException"/>이 되는지 검증한다.</summary>
    /// <param name="caseName">조립할 과도 중첩 입력의 종류.</param>
    [Theory]
    [InlineData("brackets")]
    [InlineData("blockquotes")]
    [InlineData("emphasis")]
    public void Render_TooDeeplyNested_ThrowsMarkdownTooComplex(string caseName)
    {
        var markdown = caseName switch
        {
            "brackets" => new string('[', 128) + "x",           // 129바이트
            "blockquotes" => new string('>', 128) + " x",        // 130바이트
            "emphasis" => new string('*', 255) + "a" + new string('*', 255), // 511바이트
            _ => throw new ArgumentOutOfRangeException(nameof(caseName)),
        };
        Assert.Throws<MarkdownTooComplexException>(() => Renderer.Render(markdown));
    }

    /// <summary>중첩 한도 바로 아래(127단계)는 예외 없이 정상 렌더링되는지 검증한다.</summary>
    [Fact]
    public void Render_127LevelsNested_RendersWithoutThrowing()
    {
        var html = Renderer.Render(new string('[', 127) + "x");
        Assert.NotEmpty(html);
    }

    /// <summary>허용 목록에 없는 클래스는 남지 않는다(정제기가 임의 클래스를 걸러 낸다).</summary>
    [Fact]
    public void Render_OutputClasses_AreFromTheAllowlistOnly()
    {
        var html = Renderer.Render("```csharp\nclass C { }\n```\n\n- [ ] 할 일\n");
        var allowed = HtmlAllowlist.Create().AllowedClasses;
        foreach (var element in Parse(html).QuerySelectorAll("[class]"))
        {
            foreach (var name in element.ClassList) Assert.Contains(name, allowed);
        }
    }

    /// <summary>상한(UTF-8 204,800바이트)까지는 렌더링하고, 넘으면 호출부의 검증 누락으로 보고 예외를 던진다.</summary>
    [Fact]
    public void Render_InputSizeLimit()
    {
        var atLimit = new string('a', MarkdownRenderer.MaxInputBytes);
        Assert.NotEmpty(Renderer.Render(atLimit));
        Assert.Throws<ArgumentException>(() => Renderer.Render(atLimit + "a"));
        Assert.Throws<ArgumentException>(() => Renderer.Render(new string('가', MarkdownRenderer.MaxInputBytes / 3 + 1))); // 글자 수가 아니라 바이트
    }

    /// <summary>공유 인스턴스를 여러 스레드가 동시에 써도 결과가 같다(싱글턴 등록의 전제).</summary>
    [Fact]
    public void Render_IsThreadSafe()
    {
        var markdown = new StringBuilder();
        for (var i = 0; i < 200; i++) markdown.Append("## 제목 ").Append(i).Append("\n\n[링크](https://ok.test/").Append(i).Append(") `code`\n\n```csharp\nvar x = ").Append(i).Append(";\n```\n\n");
        var expected = Renderer.Render(markdown.ToString());
        Parallel.For(0, 16, _ => Assert.Equal(expected, Renderer.Render(markdown.ToString())));
    }

    /// <summary>빈 입력은 빈 출력이다.</summary>
    [Fact]
    public void Render_Empty_ReturnsEmpty() => Assert.Equal(string.Empty, Renderer.Render(string.Empty).Trim());
}
