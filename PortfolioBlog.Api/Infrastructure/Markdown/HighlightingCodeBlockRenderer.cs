using System.Text;
using ColorCode;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace PortfolioBlog.Api.Infrastructure.Markdown;

/// <summary>코드블록을 서버에서 강조한다. 출력은 CSS 클래스만 쓰며 인라인 <c>style</c>을 만들지 않는다(공개 페이지 CSP가 <c>style-src 'self'</c>·스크립트 전면 금지이기 때문).
/// ColorCode의 정규식 기반 토크나이저는 줄 길이에 이차(quadratic)로 느려진다(측정: 한 줄 2KB 93ms, 4KB 392ms, 8KB 1,197ms, 16KB 4,765ms, 32KB 19,099ms, 200KB 약 12분).
/// 그래서 강조 여부를 결정하기 전에 줄·블록·문서 세 단계 예산을 먼저 확인하고, 예산을 넘으면 이스케이프한 일반 코드블록으로 떨어뜨린다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> <see cref="MarkdownRenderer.Render"/>가 호출마다 새 인스턴스를 만들어 붙이므로(<see cref="MarkdownRenderer"/> 참조)
/// 이 인스턴스는 절대 두 번의 <c>Render</c> 호출 사이에서 공유되지 않는다. <see cref="_highlightedLength"/>는 그 전제로 존재하는 "렌더 1회당" 상태다 — 인스턴스를 재사용하면
/// 예산이 여러 문서에 걸쳐 누적되어 이 클래스의 계약이 깨진다.</description></item>
/// <item><description><b>Memory Allocation:</b> 코드블록마다 원문 StringBuilder와(예산 이내면) 포매터 1개. 블록 크기에 비례.</description></item>
/// <item><description><b>Blocking:</b> 동기 CPU 작업(정규식 기반 토큰화). 예산 검사 자체는 문자열 길이 비교라 O(1)~O(줄 수)이며 토큰화보다 훨씬 싸다.</description></item>
/// </list>
/// 언어 이름(info string)은 작성자 입력이다. <c>Languages.FindById</c>로 찾은 언어 객체만 쓰고 원문 문자열을 출력에 넣지 않는다.
/// 모르는 언어는 이스케이프한 일반 코드블록으로 떨어진다.
/// </remarks>
public sealed class HighlightingCodeBlockRenderer : HtmlObjectRenderer<CodeBlock>
{
    /// <summary>강조를 시도할 한 줄의 최대 문자 수. 토크나이저가 줄 길이에 이차이므로, 평범한 소스 줄은 이 값보다 훨씬 짧고
    /// 이 값을 넘는 한 줄(예: 압축된 CSS·minified 코드)은 강조할 가치가 없다고 본다(2KB 한 줄에서 이미 93ms).</summary>
    public const int MaxHighlightLineLength = 400;

    /// <summary>강조를 시도할 한 코드블록의 최대 문자 수. 8KB 한 블록에서 이미 1,197ms가 측정되어, 이 값을 넘는 블록은
    /// 여러 줄에 걸쳐 있어도(즉 개별 줄은 짧아도) 토큰 수 자체가 많아 강조 비용이 무시할 수 없는 수준이 된다.</summary>
    public const int MaxHighlightBlockLength = 20_000;

    /// <summary>한 번의 <see cref="MarkdownRenderer.Render"/> 호출(문서 1개)에서 강조에 쓸 수 있는 누적 문자 수 상한.
    /// 코드블록 자체는 예산 이내라도 같은 문서 안에 그런 블록이 아주 많으면 합계 비용이 커지므로, 다 쓰고 나면 이후 블록은 강조를 건너뛴다.</summary>
    public const int MaxHighlightDocumentLength = 60_000;

    // int: 이 인스턴스가 처리한 코드블록들의 강조된 문자 수 누적. 클래스 <remarks>에 적었듯 인스턴스가 렌더 1회 전용이라
    // 필드로 둬도 스레드 간 공유·경합이 생기지 않는다(락·Interlocked 불필요).
    private int _highlightedLength;

    protected override void Write(HtmlRenderer renderer, CodeBlock block)
    {
        var code = new StringBuilder();
        var longestLine = 0;
        for (var i = 0; i < block.Lines.Count; i++)
        {
            var line = block.Lines.Lines[i].Slice.ToString();
            if (line.Length > longestLine) longestLine = line.Length;
            code.Append(line).Append('\n');
        }
        var info = (block as FencedCodeBlock)?.Info;
        var language = string.IsNullOrWhiteSpace(info) ? null : Languages.FindById(info.Trim().ToLowerInvariant());

        var withinBudget = language is not null
            && longestLine <= MaxHighlightLineLength
            && code.Length <= MaxHighlightBlockLength
            && _highlightedLength + code.Length <= MaxHighlightDocumentLength;

        if (!withinBudget)
        {
            renderer.Write("<pre><code>");
            renderer.WriteEscape(code.ToString());
            renderer.Write("</code></pre>\n");
            return;
        }

        _highlightedLength += code.Length;
        renderer.Write(new HtmlClassFormatter().GetHtmlString(code.ToString(), language));
        renderer.Write("\n");
    }
}
