using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ColorCode;
using ColorCode.Common;
using ColorCode.Parsing;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace PortfolioBlog.Api.Infrastructure.Markdown;

/// <summary>코드블록을 서버에서 강조한다. 출력은 CSS 클래스만 쓰며 인라인 <c>style</c>을 만들지 않는다(공개 페이지 CSP가 <c>style-src 'self'</c>·스크립트 전면 금지이기 때문).
/// ColorCode의 정규식 기반 토크나이저에는 비용이 다른 두 가지 병적 입력이 있다: (1) 줄 길이에 이차(quadratic)로 느려지는 경우(측정: 한 줄 2KB 93ms, 32KB 19,099ms) —
/// <see cref="MaxHighlightLineLength"/>로 막는다. (2) 종료되지 않은 <c>/*</c> 블록 주석처럼 문법이 지수(exponential)로 되짚는(backtrack) 경우
/// (측정: 1,320바이트 6,965ms, 약 1,600바이트에서 60초 초과) — 길이 예산만으로는 못 막는다(짧은 입력도 걸린다). 그래서 시간 자체에 상한을 건다:
/// 매치 1건이 오래 걸리면 <see cref="TimeBoundedLanguageCompiler.MatchTimeout"/>이 정규식 엔진 수준에서 끊고, 빠른 매치가 아주 많이 반복되면
/// <see cref="DeadlineLanguageParser"/>가 토큰 콜백마다 누적 시간을 확인해 끊는다. 렌더 1회의 강조 시간은 대략
/// <see cref="MaxHighlightMilliseconds"/> + <see cref="TimeBoundedLanguageCompiler.MatchTimeout"/>(약 2,250ms)를 넘지 않는다 —
/// 예산 소진 판정이 토큰 사이에서만 일어나므로, 판정 시점에 이미 진행 중이던 매치 1건이 최악의 경우 타임아웃 전체를 더 쓸 수 있기 때문이다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> <see cref="MarkdownRenderer.Render"/>가 호출마다 새 인스턴스를 만들어 붙이므로(<see cref="MarkdownRenderer"/> 참조)
/// 이 인스턴스는 절대 두 번의 <c>Render</c> 호출 사이에서 공유되지 않는다. <see cref="_highlightedLength"/>·<see cref="_highlightMilliseconds"/>는
/// 그 전제로 존재하는 "렌더 1회당" 상태다 — 인스턴스를 재사용하면 예산이 여러 문서에 걸쳐 누적되어 이 클래스의 계약이 깨진다.
/// 다만 정적 필드(<see cref="SharedCompiler"/>·<see cref="SharedRepository"/>·<see cref="SharedParser"/>)는 프로세스 전체에서 공유되는 상태 없는(stateless) 싱글턴이다.</description></item>
/// <item><description><b>Memory Allocation:</b> 코드블록마다 원문 StringBuilder와(예산 이내면) 포매터·시간 예산 델리게이트 1개씩. 블록 크기에 비례.</description></item>
/// <item><description><b>Blocking:</b> 동기 CPU 작업(정규식 기반 토큰화). 길이 예산 검사는 O(1)~O(줄 수)로 토큰화보다 훨씬 싸다. 시간 예산 초과·매치 타임아웃이 나면
/// 해당 블록만 이스케이프한 일반 코드블록으로 떨어지고, 남은 시간 예산이 있으면 이후 블록은 계속 강조를 시도한다(블록 단위 폴백 — 코드 한 조각이 글 전체의 강조를 죽이지 않는다).</description></item>
/// </list>
/// 언어 이름(info string)은 작성자 입력이다. <c>Languages.FindById</c>로 찾은 언어 객체만 쓰고 원문 문자열을 출력에 넣지 않는다.
/// 모르는 언어는 이스케이프한 일반 코드블록으로 떨어진다. 시간 예산은 부하에 따라 결과가 달라진다는 뜻이다 — 같은 글이 한가한 서버에서는 강조되고
/// 바쁜 서버에서는 일반 코드블록이 될 수 있다. 받아들이기로 한 성능 저하다(Plan 2B가 렌더링한 HTML을 캐시하면 이 변동성 자체가 사라진다).
/// </remarks>
public sealed class HighlightingCodeBlockRenderer : HtmlObjectRenderer<CodeBlock>
{
    /// <summary>강조를 시도할 한 줄의 최대 문자 수. 토크나이저가 줄 길이에 이차이므로, 평범한 소스 줄은 이 값보다 훨씬 짧고
    /// 이 값을 넘는 한 줄(예: 압축된 CSS·minified 코드)은 강조할 가치가 없다고 본다(2KB 한 줄에서 이미 93ms). 이 한 줄이 예산을 넘기면
    /// 그 줄이 속한 블록 전체가(다른 줄이 아무리 짧아도) 이스케이프한 일반 코드블록으로 떨어진다.</summary>
    public const int MaxHighlightLineLength = 400;

    /// <summary>한 번의 <see cref="MarkdownRenderer.Render"/> 호출(문서 1개)에서 강조에 쓸 수 있는 누적 문자 수 상한.
    /// 코드블록 자체는 예산 이내라도 같은 문서 안에 그런 블록이 아주 많으면 합계 비용이 커지므로, 다 쓰고 나면 이후 블록은 강조를 건너뛴다.</summary>
    public const int MaxHighlightDocumentLength = 60_000;

    /// <summary>한 번의 <see cref="MarkdownRenderer.Render"/> 호출에서 강조(토큰화)에 실제로 쓸 수 있는 누적 시간 상한(밀리초).
    /// 길이 예산을 통과한 블록이라도 문법이 병적(예: 종료되지 않은 블록 주석)이면 토큰화 자체가 느려질 수 있어, 길이가 아니라 시간으로 최종 상한을 건다.</summary>
    public const int MaxHighlightMilliseconds = 2_000;

    // TimeBoundedLanguageCompiler: 언어별 정규식 재컴파일 비용이 크므로(문법마다 여러 패턴을 합성) 렌더마다 새로 만들지 않고
    // 프로세스 수명 동안 하나만 두고 모든 렌더가 공유한다. 인스턴스 자체가 Thread-safe(내부 ConcurrentDictionary)라 안전하다.
    private static readonly TimeBoundedLanguageCompiler SharedCompiler = new();

    // ILanguageRepository: Languages.All을 Id로 인덱싱한 사전. LanguageRepository.FindById는 이 사전 키의 대소문자·구성과
    // 무관하게 대소문자 무시 + 별칭(예: "csharp"→"c#") 해석을 자체적으로 수행한다(리플렉션으로 확인한 내부 동작).
    // 그래서 키를 Languages.All의 원본 Id 그대로 채우면 ColorCode 기본 인스턴스와 동일하게(중첩 언어 조회 포함) 동작한다.
    private static readonly ILanguageRepository SharedRepository = new LanguageRepository(Languages.All.ToDictionary(l => l.Id));

    // LanguageParser: 위 두 상태 없는 싱글턴으로 한 번만 구성해 재사용한다. 파서 자체는 컴파일된 문법을 들고 있을 뿐 가변 상태가 없어
    // 여러 렌더가 동시에 같은 인스턴스로 Parse를 호출해도 안전하다(각 호출은 자기 입력 문자열·콜백만 다룬다).
    internal static readonly LanguageParser SharedParser = new(SharedCompiler, SharedRepository);

    // int: 이 인스턴스가 처리한 코드블록들의 강조된 문자 수 누적. 클래스 <remarks>에 적었듯 인스턴스가 렌더 1회 전용이라
    // 필드로 둬도 스레드 간 공유·경합이 생기지 않는다(락·Interlocked 불필요).
    private int _highlightedLength;

    // double(밀리초): 이 인스턴스가 강조 시도에 실제로 쓴 시간의 누적치. Stopwatch.GetTimestamp()/GetElapsedTime으로 재서
    // finally에서 더한다 — try 블록이 타임아웃·예산 초과로 예외를 던져도 그동안 쓴 시간은 반드시 누적돼야 다음 블록의 예산 판정이 정확하다.
    private double _highlightMilliseconds;

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

        var withinLengthBudget = language is not null
            && longestLine <= MaxHighlightLineLength
            && _highlightedLength + code.Length <= MaxHighlightDocumentLength;

        if (!withinLengthBudget || _highlightMilliseconds >= MaxHighlightMilliseconds)
        {
            WritePlainEscaped(renderer, code);
            return;
        }

        // Stopwatch.GetTimestamp(): 고해상도 타이머의 원시 틱 값만 읽는 가장 싼 시각 취득(DateTime.UtcNow보다 오버헤드가 작다).
        // 이 블록의 시도 시작 시각을 잡아 두고, 남은 예산을 클로저가 매 토큰마다 재계산할 수 있게 한다.
        var startTicks = Stopwatch.GetTimestamp();
        var elapsedBeforeThisBlock = _highlightMilliseconds;
        try
        {
            var deadlineParser = new DeadlineLanguageParser(SharedParser,
                () => elapsedBeforeThisBlock + Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds >= MaxHighlightMilliseconds);
            // 오늘처럼 로컬 문자열로 먼저 완성한다: GetHtmlString이 실패(타임아웃·예산 초과)하면 Markdig 렌더러에는 아무것도 쓰이지 않은 채
            // 아래 catch로 넘어간다 — 부분적으로 강조된 출력이 새 나갈 수 없다.
            var html = new HtmlClassFormatter(languageParser: deadlineParser).GetHtmlString(code.ToString(), language!);
            _highlightedLength += code.Length;
            renderer.Write(html);
            renderer.Write("\n");
        }
        catch (RegexMatchTimeoutException)
        {
            WritePlainEscaped(renderer, code);
        }
        catch (HighlightBudgetExceededException)
        {
            WritePlainEscaped(renderer, code);
        }
        finally
        {
            _highlightMilliseconds += Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
        }
    }

    /// <summary>이스케이프한 일반 <c>&lt;pre&gt;&lt;code&gt;</c> 코드블록을 쓴다. 강조를 포기하는 모든 경로(모르는 언어, 길이 예산 초과,
    /// 시간 예산 소진, 매치 타임아웃, 강조 예산 초과)가 이 메서드 하나로 모인다 — 같은 출력 형태를 중복 작성하지 않는다.</summary>
    private static void WritePlainEscaped(HtmlRenderer renderer, StringBuilder code)
    {
        renderer.Write("<pre><code>");
        renderer.WriteEscape(code.ToString());
        renderer.Write("</code></pre>\n");
    }
}
