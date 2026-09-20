using System.Text;
using ColorCode;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace PortfolioBlog.Api.Infrastructure.Markdown;

/// <summary>코드블록을 서버에서 강조한다. 출력은 CSS 클래스만 쓰며 인라인 <c>style</c>을 만들지 않는다(공개 페이지 CSP가 <c>style-src 'self'</c>·스크립트 전면 금지이기 때문).</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 인스턴스에 상태가 없다. 렌더링마다 새 <see cref="HtmlRenderer"/>에 붙여 쓴다.</description></item>
/// <item><description><b>Memory Allocation:</b> 코드블록마다 원문 StringBuilder와 포매터 1개. 블록 크기에 비례.</description></item>
/// <item><description><b>Blocking:</b> 동기 CPU 작업(정규식 기반 토큰화).</description></item>
/// </list>
/// 언어 이름(info string)은 작성자 입력이다. <c>Languages.FindById</c>로 찾은 언어 객체만 쓰고 원문 문자열을 출력에 넣지 않는다.
/// 모르는 언어는 이스케이프한 일반 코드블록으로 떨어진다.
/// </remarks>
public sealed class HighlightingCodeBlockRenderer : HtmlObjectRenderer<CodeBlock>
{
    protected override void Write(HtmlRenderer renderer, CodeBlock block)
    {
        var code = new StringBuilder();
        for (var i = 0; i < block.Lines.Count; i++)
        {
            code.Append(block.Lines.Lines[i].Slice.ToString()).Append('\n');
        }
        var info = (block as FencedCodeBlock)?.Info;
        var language = string.IsNullOrWhiteSpace(info) ? null : Languages.FindById(info.Trim().ToLowerInvariant());
        if (language is null)
        {
            renderer.Write("<pre><code>");
            renderer.WriteEscape(code.ToString());
            renderer.Write("</code></pre>\n");
            return;
        }
        renderer.Write(new HtmlClassFormatter().GetHtmlString(code.ToString(), language));
        renderer.Write("\n");
    }
}
