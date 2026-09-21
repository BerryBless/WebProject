using System.Diagnostics;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ColorCode;
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
    public void Render_PathologicalCodeAndHeadings_FinishQuickly(string caseName)
    {
        var markdown = caseName switch
        {
            "css-long-line" => "```css\n" + "a{b:\"" + new string('f', 200_000) + "\n```\n",
            "html-attr-soup" => "```html\n" + string.Concat(Enumerable.Repeat("<a a=\"a ", 25_000)) + "\n```\n",
            "csharp-one-line" => "```csharp\n" + string.Concat(Enumerable.Repeat("var a=1;", 25_000)) + "\n```\n",
            _ => throw new ArgumentOutOfRangeException(nameof(caseName)),
        };
        Assert.True(Encoding.UTF8.GetByteCount(markdown) <= MarkdownRenderer.MaxInputBytes,
            $"{caseName} 입력이 상한을 넘어 크기 검증에서 먼저 걸립니다: {Encoding.UTF8.GetByteCount(markdown)}바이트");

        var stopwatch = Stopwatch.StartNew();
        Renderer.Render(markdown);
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"{caseName}이(가) {stopwatch.Elapsed}만에 끝났습니다(5초 상한 초과).");
    }

    /// <summary>같은 제목이 수만 번 반복돼도 제목 id 중복 해소 비용이 제목 수에 선형인지 검증한다. 절대 시간이 아니라 <b>제목 하나당 비용의 비율</b>
    /// (동일 제목 18,600개 대 서로 다른 제목 12,000개, 둘 다 약 200KB)로 단언한다 — 이 입력의 렌더링은 선형이어도 수백 ms가 걸리는 실제 작업이라,
    /// 느린 CI 러너에서 다른 테스트와 병렬로 돌면 절대 상한(5초)을 넘는다(실측: 로컬 0.8초, GitHub 러너 7.2초). 비율은 기계 속도와 무관하다:
    /// Markdig의 <c>UseAutoIdentifiers</c>를 쓰던 때는 이 비율이 약 13배(그 확장만 재면 82배)였고 선형 <c>HeadingIds</c>로는 약 1.6배다(둘 다 실측).</summary>
    [Fact]
    public void Render_IdenticalHeadings_CostPerHeadingStaysLinear()
    {
        const int identicalCount = 18_600, distinctCount = 12_000;
        var identical = string.Concat(Enumerable.Repeat("# heading\n\n", identicalCount));
        var distinct = string.Concat(Enumerable.Range(0, distinctCount).Select(i => $"# heading {i:D5}\n\n"));
        Assert.True(Encoding.UTF8.GetByteCount(identical) <= MarkdownRenderer.MaxInputBytes);
        Assert.True(Encoding.UTF8.GetByteCount(distinct) <= MarkdownRenderer.MaxInputBytes);

        // 워밍업 1회 뒤 2회 중 빠른 쪽: JIT·GC·이웃 테스트의 일시적 간섭을 줄인다. 두 입력을 번갈아 재서 같은 부하 조건에 놓는다.
        static TimeSpan Measure(string markdown)
        {
            var stopwatch = Stopwatch.StartNew();
            Renderer.Render(markdown);
            return stopwatch.Elapsed;
        }
        Measure(identical);
        Measure(distinct);
        var identicalBest = TimeSpan.MaxValue;
        var distinctBest = TimeSpan.MaxValue;
        for (var i = 0; i < 2; i++)
        {
            var a = Measure(identical);
            if (a < identicalBest) identicalBest = a;
            var b = Measure(distinct);
            if (b < distinctBest) distinctBest = b;
        }

        var perIdentical = identicalBest.TotalMilliseconds / identicalCount;
        var perDistinct = distinctBest.TotalMilliseconds / distinctCount;
        Assert.True(perIdentical <= perDistinct * 4,
            $"동일 제목의 제목당 비용이 서로 다른 제목의 {perIdentical / perDistinct:F1}배다(상한 4배) — 중복 해소가 다시 초선형이 됐다. " +
            $"identical {identicalBest.TotalMilliseconds:F0}ms/{identicalCount}, distinct {distinctBest.TotalMilliseconds:F0}ms/{distinctCount}");
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

    /// <summary>종료되지 않은 <c>/*</c> 블록 주석 뒤에 <paramref name="lines"/>줄의 그럴듯한 함수 정의를 붙인다. ColorCode의 C 계열 문법이
    /// 이 패턴에서 블록 주석 종료를 찾으려고 지수적으로 되짚는다(백트래킹). 함수 이름은 일부러 <c>computeI</c>로 고정해(치환하지 않음)
    /// 테스트가 내용을 확인할 때 리터럴로 찾을 수 있게 하고, 뒤에 붙는 숫자만 줄 번호로 바꿔 줄마다 살짝 다르게 만든다.</summary>
    private static string UnterminatedCommentBody(int lines)
    {
        var sb = new StringBuilder("/* TODO: finish this\n");
        for (var i = 1; i <= lines; i++) sb.Append($"function computeI(source, index) {{ return source[index] + {i}; }}\n");
        return sb.ToString();
    }

    /// <summary>종료되지 않은 블록 주석 뒤에 붙인 코드가 C 계열 문법에서 예전에는(시간 예산 도입 전) 최소 6~7초(20줄)부터 60초 초과(25줄)까지
    /// 걸렸던 것이, 시간 예산(<see cref="HighlightingCodeBlockRenderer.MaxHighlightMilliseconds"/> + 정규식 매치 타임아웃) 도입 후에는
    /// 5초 안에 끝나는지 6개 언어에서 검증한다. 예산을 넘겨 일반 코드블록으로 떨어지더라도 내용(<c>computeI</c>)은 이스케이프된 채로 남아야 한다.</summary>
    /// <param name="language">ColorCode가 아는 C 계열 언어 id.</param>
    [Theory]
    [InlineData("javascript")]
    [InlineData("c#")]
    [InlineData("cpp")]
    [InlineData("java")]
    [InlineData("php")]
    [InlineData("typescript")]
    public void Render_UnterminatedBlockComment_IsBoundedByTime(string language)
    {
        var markdown = $"```{language}\n{UnterminatedCommentBody(40)}```\n";
        var stopwatch = Stopwatch.StartNew();
        var html = Renderer.Render(markdown);
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"{language}이(가) {stopwatch.Elapsed}만에 끝났습니다(5초 상한 초과).");
        AssertInert(html);
        var code = Parse(html).QuerySelector("pre > code");
        Assert.NotNull(code);
        Assert.Contains("computeI", code!.TextContent, StringComparison.Ordinal);
    }

    /// <summary>줄 길이 예산(400자) 이내인 줄이라도 <c>/*a</c>를 반복해 "블록 주석 열기 후보"를 잔뜩 만들면 여전히 느릴 수 있다는 가설을
    /// 시간 예산으로 막는지 검증한다(길이 예산만으로는 이 패턴을 못 거른다 — 모든 줄이 예산 이내이기 때문이다).</summary>
    [Fact]
    public void Render_CommentOpenFlood_IsBoundedByTime()
    {
        var line = string.Concat(Enumerable.Repeat("/*a", 134))[..400]; // 정확히 400자(줄 예산 경계) 한 줄
        var body = string.Concat(Enumerable.Repeat(line + "\n", 49));
        var markdown = $"```javascript\n{body}```\n";
        var stopwatch = Stopwatch.StartNew();
        Renderer.Render(markdown);
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"{stopwatch.Elapsed}만에 끝났습니다(5초 상한 초과).");
    }

    /// <summary>지수 폭발 블록 3개(각자 250ms 매치 타임아웃으로 끊긴다)를 한 문서에 넣어도 8초 안에 끝나고,
    /// 같은 렌더러 인스턴스로 그 다음에 렌더링한 작은 문서는 다시 강조되는지(예산이 렌더러가 아니라 렌더 1회에 매인다) 검증한다.
    /// 이 테스트 자체는 세 블록 각각이 매치 타임아웃(250ms)에서 끊기므로 8초 상한을 넉넉히 통과한다 — "블록마다 독립 예산이어도"
    /// 이 값들로는 통과하므로, 이 테스트는 시간 예산이 렌더당 누적임을 증명하지 못한다(그 증명은 각 블록이 개별로는 타임아웃에
    /// 걸리지 않는 <see cref="Render_TimeBudget_IsCumulativePerRender_Deterministically"/>가 한다). 이 테스트가 실제로 증명하는 것은
    /// (1) 한 문서 안의 타임아웃 블록 여러 개가 합쳐져도 유계로 끝난다는 것과 (2) 같은 렌더러 인스턴스의 다음 렌더가
    /// 새 예산으로 시작한다는 것 두 가지다.</summary>
    [Fact]
    public void Render_ThreeExponentialBlocks_StayBounded_AndNextRenderGetsFreshBudget()
    {
        var block = $"```javascript\n{UnterminatedCommentBody(40)}```\n\n";
        var markdown = string.Concat(Enumerable.Repeat(block, 3));
        var stopwatch = Stopwatch.StartNew();
        Renderer.Render(markdown);
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(8), $"{stopwatch.Elapsed}만에 끝났습니다(8초 상한 초과).");

        var again = Renderer.Render("```csharp\nvar a = 1;\n```\n");
        Assert.NotNull(Parse(again).QuerySelector("div.csharp span.keyword"));
    }

    /// <summary>시간 예산을 다 쓴 블록 하나가 같은 문서의 다른(정상) 블록까지 강조를 못 받게 막지 않는지 검증한다 — 폴백은 블록 단위다.</summary>
    [Fact]
    public void Render_TimedOutBlock_DoesNotDisableLaterBlocks()
    {
        var markdown = $"```javascript\n{UnterminatedCommentBody(40)}```\n\n```csharp\nvar a = 1;\n```\n";
        var html = Renderer.Render(markdown);
        AssertInert(html);
        var body = Parse(html);
        Assert.Empty(body.QuerySelectorAll("div.javascript")); // 첫 블록은 시간 예산 소진으로 일반 코드블록
        Assert.NotNull(body.QuerySelector("div.csharp span.keyword")); // 둘째 블록은 정상 강조
    }

    /// <summary>강조 시간 예산이 "렌더 1회에 걸친 누적"임을 기계 속도와 무관하게 증명한다. 시계가 호출마다 50ms 전진하므로 블록 하나가
    /// 최소 100ms를 쓴다 — 시작 호출(<c>startTicks</c>) 자체는 기준점이라 경과 시간에 0을 기여하고, <see cref="DeadlineLanguageParser"/>가
    /// 블록마다 예산 델리게이트를 최소 1회 호출해 시계가 한 번 전진(+50ms)하고, <c>finally</c>의 종료 시각 측정이 시계를 한 번 더 전진(+50ms)시켜 도합 100ms가 된다.
    /// 60블록이면 2,000ms 예산을 반드시 넘는다. 블록마다 예산이 따로면 마지막 블록도 강조되어 실패한다.</summary>
    [Fact]
    public void Render_TimeBudget_IsCumulativePerRender_Deterministically()
    {
        var renderer = new MarkdownRenderer(new SteppingTimeProvider(50));
        var markdown = string.Concat(Enumerable.Repeat("```csharp\nvar a = 1;\n```\n\n", 60));

        var result = renderer.RenderDetailed(markdown);

        var pres = Parse(result.Html).QuerySelectorAll("pre");
        Assert.Equal(60, pres.Length);
        Assert.NotNull(pres[0].QuerySelector("span.keyword"));  // 첫 블록은 강조됨
        Assert.Equal("code", pres[^1].FirstElementChild?.LocalName); // 마지막 블록은 평문 경로
        Assert.Null(pres[^1].QuerySelector("span"));
        Assert.True(result.HighlightTimedOut);

        // 같은 렌더러의 다음 렌더는 새 예산으로 시작한다.
        var again = renderer.RenderDetailed("```csharp\nvar a = 1;\n```\n");
        Assert.False(again.HighlightTimedOut);
        Assert.NotNull(Parse(again.Html).QuerySelector("span.keyword"));
    }

    /// <summary>실제 시계에서는 같은 문서가 전부 강조되고 시간 초과 표시가 없다(가짜 시계 테스트가 입력 때문에 통과한 것이 아님을 보인다).</summary>
    [Fact]
    public void Render_SameDocument_OnSystemClock_IsFullyHighlighted()
    {
        var result = new MarkdownRenderer().RenderDetailed(string.Concat(Enumerable.Repeat("```csharp\nvar a = 1;\n```\n\n", 60)));
        Assert.False(result.HighlightTimedOut);
        Assert.Equal(60, Parse(result.Html).QuerySelectorAll("span.keyword").Length);
    }

    /// <summary>첫 이미지는 URL 정책을 통과한 것만 돌려준다(외부 이미지는 링크가 풀리므로 후보가 아니다).</summary>
    [Fact]
    public void RenderDetailed_FirstImageUrl_IsTheFirstAllowedAttachment()
    {
        const string attachment = "/attachments/01234567-89ab-cdef-0123-456789abcdef/a.png";
        var result = new MarkdownRenderer().RenderDetailed($"![x](https://evil.test/p.png)\n\n![y]({attachment})\n");
        Assert.Equal(attachment, result.FirstImageUrl);
        Assert.Null(new MarkdownRenderer().RenderDetailed("글만 있다").FirstImageUrl);
    }

    /// <summary>블록 길이 상한(라운드 1의 <c>MaxHighlightBlockLength</c>)을 없앤 회귀 테스트: 평범한 소스 파일이 20,000자를 넘는 정도로 길어도
    /// (문서 예산 60,000자 이내) 강조가 끊기지 않아야 한다. 예전 20,000자 블록 상한은 "8KB 1줄=1,197ms"라는 잘못된 근거로 만들어졌었다
    /// (실제로는 400자 이하 줄이면 선형이라 20,000자 블록도 수십~수백 ms에 불과하다).</summary>
    [Fact]
    public void Render_OrdinaryLongSourceFile_IsHighlighted()
    {
        const string line = "var value = ComputeSomethingWithLongerName(inputArgument, indexNumber);\n"; // 약 74자, 줄 예산(400자)에 한참 못 미친다
        var code = string.Concat(Enumerable.Repeat(line, 300));
        Assert.True(code.Length > 20_000, $"샘플 총 길이가 20,000자를 못 넘음: {code.Length}");
        Assert.True(code.Length <= 60_000, $"샘플 총 길이가 문서 예산(60,000자)을 넘음: {code.Length}");
        var markdown = $"```csharp\n{code}```\n";
        var html = Renderer.Render(markdown);
        Assert.NotNull(Parse(html).QuerySelector("div.csharp span.keyword"));
    }

    /// <summary>시간 제한 컴파일러·전용 언어 저장소로 교체한 것이 토큰화 결과 자체를 바꾸지 않았는지(중첩 언어 강조 포함) 언어별로 핀 고정한다.
    /// 기본 포매터를 직접 호출하는 것은 이 테스트에서만 허용된다 — 운영 코드(<see cref="HighlightingCodeBlockRenderer"/>)는 항상 시간 제한 경로만 쓴다.</summary>
    /// <param name="languageId">ColorCode 언어 id. <see cref="Languages.FindById"/>가 <c>null</c>을 돌려주면(이 버전이 모르는 언어) 건너뛴다.</param>
    [Theory]
    [InlineData("csharp")]
    [InlineData("javascript")]
    [InlineData("html")]
    [InlineData("css")]
    [InlineData("sql")]
    [InlineData("python")]
    [InlineData("powershell")]
    [InlineData("xml")]
    [InlineData("json")]
    public void BoundedFormatter_MatchesDefaultFormatterOutput(string languageId)
    {
        var language = Languages.FindById(languageId);
        if (language is null) return; // ColorCode.Core 2.0.15가 모르는 언어 id — 강조 없이 넘어가는 것이 정상이라 이 케이스는 건너뛴다

        var code = languageId switch
        {
            "csharp" => "public class C { public int Add(int a, int b) => a + b; }",
            "javascript" => "function add(a, b) { return a + b; }",
            "html" => "<html><head><style>body { color: red; }</style></head><body><script>var a = 1;</script></body></html>",
            "css" => "body { color: red; margin: 0; }",
            "sql" => "SELECT * FROM Posts WHERE Id = 1;",
            "python" => "def add(a, b):\n    return a + b\n",
            "powershell" => "Get-ChildItem -Path C:\\temp | Where-Object { $_.Length -gt 100 }",
            "xml" => "<root><child attr=\"1\">text</child></root>",
            "json" => "{\"a\": 1, \"b\": [1, 2, 3]}",
            _ => throw new ArgumentOutOfRangeException(nameof(languageId)),
        };

        var expected = new HtmlClassFormatter().GetHtmlString(code, language);
        var deadlineParser = new DeadlineLanguageParser(HighlightingCodeBlockRenderer.SharedParser, isBudgetExceeded: () => false);
        var actual = new HtmlClassFormatter(languageParser: deadlineParser).GetHtmlString(code, language);

        Assert.Equal(expected, actual);
    }

    /// <summary>제목이 평문과 인라인 코드(백틱)를 섞을 때 id가 문서 순서를 지키는지 검증한다(라운드 1은 리터럴을 전부 모은 뒤 코드를 전부 붙여 순서가 깨졌었다).
    /// 강조(<c>**bold**</c>)·링크처럼 그 자체는 글자가 아닌 인라인도 자식 글자가 정확히 한 번, 제자리에서 뽑히는지 함께 확인한다.</summary>
    /// <param name="markdown">제목 한 줄짜리 마크다운.</param>
    /// <param name="expectedId">문서 순서가 지켜졌을 때 나와야 하는 id.</param>
    [Theory]
    [InlineData("## Install `npm` first", "install-npm-first")]
    [InlineData("## `code` tail", "code-tail")]
    [InlineData("## a `b` c `d` e", "a-b-c-d-e")]
    [InlineData("## **bold** and `code` mix", "bold-and-code-mix")]
    [InlineData("## `ArrayPool<T>` 사용법", "arraypoolt-사용법")]
    [InlineData("## [link text](https://example.test) after", "link-text-after")]
    public void Render_HeadingIds_KeepDocumentOrder_WithInlineCode(string markdown, string expectedId)
    {
        var html = Renderer.Render(markdown);
        var heading = Parse(html).QuerySelector("h2");
        Assert.NotNull(heading);
        Assert.Equal(expectedId, heading!.Id);
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
