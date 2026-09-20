using System.Text;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace PortfolioBlog.Api.Infrastructure.Markdown;

/// <summary>제목 블록에 <c>id</c>를 붙인다. Markdig의 <c>UseAutoIdentifiers</c> 확장은 중복 제목 처리가 제목 개수에 이차(quadratic)로 느려져서
/// (측정: 동일 제목 18,600개 5,873ms, 서로 다른 제목 12,000개는 301ms — 원인이 개수가 아니라 중복 해소 로직) 이 선형 대체로 바꾼다.</summary>
internal static class HeadingIds
{
    /// <summary>slug 최종 길이 상한(문자).</summary>
    public const int MaxLength = 80;

    /// <summary>문서의 모든 <see cref="HeadingBlock"/>에 고유한 <c>id</c>를 순서대로 부여한다.</summary>
    /// <param name="document">id를 부여할 문서. 제목 블록이 이 인스턴스 안에서 바로 수정된다.</param>
    public static void Assign(MarkdownDocument document)
    {
        // HashSet<string>: 지금까지 실제로 쓰인 id 전체(기본형 + 접미사 붙은 것 모두)를 O(1) 조회로 중복 검사하기 위함.
        var used = new HashSet<string>(StringComparer.Ordinal);
        // Dictionary<string,int>: 기본 slug별 "다음에 시도할 접미사" 커서. 한 번 늘어난 커서는 절대 줄지 않으므로
        // 같은 기본 slug에 대한 전체 재시도 횟수 합이 그 slug가 등장한 총 횟수를 넘지 않는다(선형성의 근거).
        var nextSuffix = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var heading in document.Descendants<HeadingBlock>())
        {
            var text = new StringBuilder();
            if (heading.Inline is not null)
            {
                // HeadingBlock은 LeafBlock이라 Descendants<T>()를 이 블록 자신에 직접 걸면 안으로 들어가지 못한다(document.Descendants가 ContainerBlock
                // 순회 중에만 LeafBlock.Inline으로 다리를 놓기 때문). Inline 트리의 시작점인 heading.Inline(ContainerInline)에서 바로 순회해야 한다.
                // 타입별로 따로 순회하면(LiteralInline 전부 → CodeInline 전부) 원래 글자 순서가 깨진다(라운드 1의 결함) —
                // 한 번의 순회에서 타입을 판별해야 문서 순서가 그대로 보존된다. 강조·링크 등 다른 인라인 노드 자체는 아무것도 보태지 않지만,
                // 그 자식(글자)은 Descendants()가 어차피 재귀적으로 방문하므로 별도 처리가 필요 없다.
                foreach (var descendant in heading.Inline.Descendants())
                {
                    switch (descendant)
                    {
                        case LiteralInline literal: text.Append(literal.Content.ToString()); break;
                        case CodeInline code: text.Append(code.Content); break;
                    }
                }
            }

            var baseSlug = Slug(text.ToString());
            string id;
            if (used.Add(baseSlug))
            {
                id = baseSlug;
            }
            else
            {
                var n = nextSuffix.TryGetValue(baseSlug, out var next) ? next : 1;
                string candidate;
                do
                {
                    candidate = $"{baseSlug}-{n}";
                    n++;
                }
                while (!used.Add(candidate));
                nextSuffix[baseSlug] = n;
                id = candidate;
            }

            heading.GetAttributes().Id = id;
        }
    }

    /// <summary>제목 텍스트를 URL 조각 식별자(fragment id)로 바꾼다. 문자·숫자는 소문자로, 공백은 하이픈으로, <c>-</c>·<c>_</c>는 그대로 남기고
    /// 그 외 문자는 버린다. 앞뒤 하이픈을 정리한 뒤 <see cref="MaxLength"/>로 자르며, 결과가 비면 <c>"section"</c>을 쓴다.</summary>
    /// <param name="text">제목의 평문(인라인 서식을 뺀 글자만).</param>
    /// <returns>생성된 slug(빈 값 없음).</returns>
    internal static string Slug(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (char.IsWhiteSpace(c)) sb.Append('-');
            else if (c is '-' or '_') sb.Append(c);
            // 그 외(구두점·기호·HTML 꺾쇠 등)는 버린다 — id에 <, >, ", 공백이 섞이지 않게 하는 유일한 방어선이므로 허용 목록 방식으로 짠다.
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length > MaxLength) slug = slug[..MaxLength];
        return slug.Length == 0 ? "section" : slug;
    }
}
