using System.Text;
using Ganss.Xss;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace PortfolioBlog.Api.Infrastructure.Markdown;

/// <summary>마크다운을 URL 정책·허용 목록 정제를 거친 안전한 HTML로 변환한다. 공개 페이지와 미리보기가 이 하나를 공유한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 파이프라인과 정제기는 생성 후 바꾸지 않으며 호출마다 새 문서·렌더러·DOM을 만든다. 싱글턴으로 등록한다.</description></item>
/// <item><description><b>Memory Allocation:</b> 입력 크기에 비례(AST + 중간 HTML + 정제용 DOM + 출력 문자열). 반환 문자열의 소유권은 호출자.</description></item>
/// <item><description><b>Blocking:</b> 호출 스레드에서 동기 실행되는 CPU 작업이며 취소할 수 없다(160KB·코드블록 1,500개에 약 200ms). 호출부가 입력 크기와 동시 실행 수를 제한한다.</description></item>
/// </list>
/// HTML을 저장하지 않고 요청마다 렌더링하므로 이 클래스의 보안 수정은 과거 글 전체에 즉시 적용된다.
/// </remarks>
public sealed class MarkdownRenderer
{
    /// <summary>입력 상한(UTF-8 바이트). DB CHECK(<c>CK_Posts_Content_Size</c>)·글 검증과 같은 값.</summary>
    public const int MaxInputBytes = 204_800;

    // MarkdownPipeline: Build() 이후 불변이라 스레드 간 공유가 안전하다. 확장은 허용 목록으로만 켠다 —
    // 임의 속성({#id .class}), 미디어 임베드, raw HTML은 끄거나 아예 등록하지 않는다.
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .UseTaskLists()
        .UseFootnotes()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseAutoLinks()
        .Build();

    // HtmlSanitizer: Sanitize 호출마다 독립된 AngleSharp DOM을 만들기 때문에 구성만 고정돼 있으면 공유 인스턴스를 동시에 쓸 수 있다.
    private readonly HtmlSanitizer _sanitizer = HtmlAllowlist.Create();

    public string Render(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        if (Encoding.UTF8.GetByteCount(markdown) > MaxInputBytes)
        {
            throw new ArgumentException($"마크다운 입력은 UTF-8 {MaxInputBytes}바이트 이하여야 합니다. 호출부가 먼저 검증해야 합니다.", nameof(markdown));
        }

        // 전체 한정: 이 파일의 네임스페이스가 'Markdown'이라 Markdig의 정적 클래스 Markdown과 이름이 겹친다(using만으로는 해석되지 않음).
        var document = Markdig.Markdown.Parse(markdown, _pipeline);
        ApplyUrlPolicy(document);

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        _pipeline.Setup(renderer);
        if (renderer.ObjectRenderers.FindExact<CodeBlockRenderer>() is { } builtIn) renderer.ObjectRenderers.Remove(builtIn);
        renderer.ObjectRenderers.Add(new HighlightingCodeBlockRenderer());
        renderer.Render(document);
        writer.Flush();
        return _sanitizer.Sanitize(writer.ToString());
    }

    /// <summary>정책 밖 링크·이미지를 AST에서 푼다. 자식(링크 글자·이미지 대체 텍스트)은 그 자리에 남기고 링크 노드만 없앤다.</summary>
    private static void ApplyUrlPolicy(MarkdownDocument document)
    {
        // ToList(): 순회 중 트리를 고치므로 먼저 목록을 고정한다.
        foreach (var link in document.Descendants<LinkInline>().ToList())
        {
            var allowed = link.IsImage ? UrlPolicy.IsAllowedImage(link.Url) : UrlPolicy.IsAllowedLink(link.Url);
            if (allowed) continue;
            // ReplaceBy(new LiteralInline(...))로 텍스트를 다시 만들면 글자가 두 번 나온다(스파이크에서 확인). 자식을 옮기고 노드만 지운다.
            link.MoveChildrenAfter(link);
            link.Remove();
        }
        foreach (var autolink in document.Descendants<AutolinkInline>().ToList())
        {
            var url = autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url;
            if (!UrlPolicy.IsAllowedLink(url)) autolink.ReplaceBy(new LiteralInline(autolink.Url));
        }
    }
}
