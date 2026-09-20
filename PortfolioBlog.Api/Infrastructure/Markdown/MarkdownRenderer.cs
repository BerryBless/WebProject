using System.Text;
using Ganss.Xss;
using Markdig;
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
/// <item><description><b>Blocking:</b> 호출 스레드에서 동기 실행되는 CPU 작업이며 취소할 수 없다. 코드 강조 예산(<see cref="HighlightingCodeBlockRenderer"/>)과
/// 선형 제목 id(<see cref="HeadingIds"/>)를 적용한 뒤로는, 리뷰가 측정한 병적 코드·제목 입력(수정 전 최대 12분 25초)이 전부 1초 미만으로 끝난다.
/// 다만 Markdig 자체의 인라인 파서는 적대적 인라인 구문에서는 여전히 초선형이며 라이브러리 밖에서 고칠 수 없다
/// (수정 전 측정: <c>[a](</c> 40,000회=160KB 약 4.2초, <c>`a`</c> 40,000회 약 2.5초) — 그래서 호출부가 반드시 동시 실행 수를 제한해야 한다
/// (미리보기 엔드포인트는 이미 제한한다; Plan 2B의 공개 페이지는 렌더링한 HTML을 캐시하거나 렌더 동시성을 게이트해야 한다).
/// 첫 렌더링에는 ColorCode 등의 정적 초기화 비용(약 185ms)이 한 번 더 붙는다.</description></item>
/// </list>
/// HTML을 저장하지 않고 요청마다 렌더링하므로 이 클래스의 보안 수정은 과거 글 전체에 즉시 적용된다.
/// </remarks>
public sealed class MarkdownRenderer
{
    /// <summary>입력 상한(UTF-8 바이트). DB CHECK(<c>CK_Posts_Content_Size</c>)·글 검증과 같은 값. 이 상한을 넘으면 <see cref="Render"/>가
    /// <see cref="ArgumentException"/>을 던진다(호출부가 저장 전에 걸렀어야 하는 버그). 상한 이하라도 구조가 너무 깊이 중첩되면(대괄호·인용·강조 등 128단계 초과)
    /// 별도로 <see cref="MarkdownTooComplexException"/>이 던져진다 — 이쪽은 호출부 버그가 아니라 입력 자체의 문제다.</summary>
    public const int MaxInputBytes = 204_800;

    // MarkdownPipeline: Build() 이후 불변이라 스레드 간 공유가 안전하다. 확장은 허용 목록으로만 켠다 —
    // 임의 속성({#id .class}), 미디어 임베드, raw HTML은 끄거나 아예 등록하지 않는다.
    // UseAutoIdentifiers는 쓰지 않는다: 그 확장의 중복 제목 해소가 제목 개수에 이차로 느려진다(HeadingIds가 선형으로 대신한다).
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .UseTaskLists()
        .UseFootnotes()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseAutoLinks()
        .Build();

    // HtmlSanitizer: Sanitize 호출마다 독립된 AngleSharp DOM을 만들기 때문에 구성만 고정돼 있으면 공유 인스턴스를 동시에 쓸 수 있다.
    private readonly HtmlSanitizer _sanitizer = HtmlAllowlist.Create();

    /// <summary>마크다운을 안전한 HTML로 변환한다. 원본 마크다운은 저장하지 않고 매 요청 렌더링하므로 보안 수정이 과거 글 전체에 즉시 적용된다.</summary>
    /// <param name="markdown">렌더링할 마크다운 원문(UTF-8 <see cref="MaxInputBytes"/>바이트 이하).</param>
    /// <returns>허용 목록만 남은 정제된 HTML 문자열. 빈 입력은 빈 문자열.</returns>
    /// <exception cref="ArgumentException"><paramref name="markdown"/>이 UTF-8 <see cref="MaxInputBytes"/>바이트를 넘는다(호출부 검증 누락).</exception>
    /// <exception cref="MarkdownTooComplexException"><paramref name="markdown"/>의 구조(대괄호·인용·강조 등)가 Markdig의 중첩 한도(128단계)를 넘는다.</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 호출마다 새 <see cref="HighlightingCodeBlockRenderer"/>·문서·DOM을 만들어 공유 가변 상태를 쓰지 않는다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 입력 크기에 비례(AST + 중간 HTML + 정제용 DOM + 출력 문자열). 반환 문자열의 소유권은 호출자.</description></item>
    /// <item><description><b>Blocking:</b> 클래스 <see cref="MarkdownRenderer"/> 문서의 Blocking 항목 참조. 동기·취소 불가·호출부가 동시성을 제한해야 함은 동일하다.</description></item>
    /// </list>
    /// </remarks>
    public string Render(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        if (Encoding.UTF8.GetByteCount(markdown) > MaxInputBytes)
        {
            throw new ArgumentException($"마크다운 입력은 UTF-8 {MaxInputBytes}바이트 이하여야 합니다. 호출부가 먼저 검증해야 합니다.", nameof(markdown));
        }

        try
        {
            // 전체 한정: 이 파일의 네임스페이스가 'Markdown'이라 Markdig의 정적 클래스 Markdown과 이름이 겹친다(using만으로는 해석되지 않음).
            var document = Markdig.Markdown.Parse(markdown, _pipeline);
            ApplyUrlPolicy(document);
            HeadingIds.Assign(document);

            using var writer = new StringWriter();
            var renderer = new HtmlRenderer(writer);
            _pipeline.Setup(renderer);
            if (renderer.ObjectRenderers.FindExact<CodeBlockRenderer>() is { } builtIn) renderer.ObjectRenderers.Remove(builtIn);
            renderer.ObjectRenderers.Add(new HighlightingCodeBlockRenderer());
            renderer.Render(document);
            writer.Flush();
            return _sanitizer.Sanitize(writer.ToString());
        }
        catch (ArgumentException ex)
        {
            // Markdig 1.4.0은 중첩 한도(128단계) 초과를 파싱 또는 렌더링 중 아무 때나 평범한 ArgumentException으로만 알린다.
            // 위의 크기 상한 검사를 통과한 뒤 이 블록 안에서 나는 ArgumentException은 전부 이 경우뿐이므로 ParamName으로 가를 필요가 없다.
            throw new MarkdownTooComplexException("마크다운 구조가 너무 깊게 중첩됐습니다(중첩 한도 128).", ex);
        }
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
