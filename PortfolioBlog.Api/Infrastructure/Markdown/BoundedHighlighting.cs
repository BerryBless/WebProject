using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using ColorCode;
using ColorCode.Compilation;
using ColorCode.Parsing;

namespace PortfolioBlog.Api.Infrastructure.Markdown;

/// <summary>ColorCode의 문법 정규식이 병적 입력(예: 종료되지 않은 <c>/*</c> 블록 주석)에서 지수적으로 느려지는 것을 매치 1건 단위로 끊는 컴파일러.
/// ColorCode 기본 <see cref="LanguageCompiler"/>가 만든 언어별 정규식을 한 번 더 감싸, 같은 패턴에 <see cref="MatchTimeout"/>을 건 새 <see cref="Regex"/>로 바꿔서 캐시한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. <see cref="HighlightingCodeBlockRenderer"/>가 이 인스턴스를 프로세스 전체 싱글턴으로 공유한다(여러 렌더 요청이 동시에 <see cref="Compile"/>을 호출할 수 있다).
/// 내부 캐시가 <see cref="ConcurrentDictionary{TKey,TValue}"/>라 락 없이 안전하다.</description></item>
/// <item><description><b>Memory Allocation:</b> 언어당 최초 1회만 새 <see cref="Regex"/>를 컴파일해 캐시한다. 이후 호출은 캐시 히트라 추가 할당이 없다.</description></item>
/// <item><description><b>Blocking:</b> <see cref="Compile"/>은 캐시 히트 시 즉시 반환. 캐시 미스(언어당 딱 한 번)에는 내부 <see cref="LanguageCompiler"/>가 정규식을 빌드하는 동기 비용이 든다.</description></item>
/// </list>
/// </remarks>
internal sealed class TimeBoundedLanguageCompiler : ILanguageCompiler
{
    /// <summary>재구성한 정규식 하나에 거는 매치 제한 시간. .NET 백트래킹 엔진은 백트래킹 도중에도 이 시간을 주기적으로 검사해
    /// 넘기면 <see cref="RegexMatchTimeoutException"/>을 던진다 — 지수 폭발은 "한 번의 매치 시도" 안에서 일어나므로 정확히 이 지점에서 끊긴다.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    // LanguageCompiler: ColorCode 내부 컴파일러를 그대로 재사용하되, 우리 소유의 Dictionary+락을 새로 만들어 넘긴다.
    // ColorCode가 내부적으로 쓰는 정적(static) 캐시·락은 절대 건드리지 않는다 — 거길 공유하면 시간 제한 없는 원본 Regex와 뒤섞일 위험이 있다.
    private readonly LanguageCompiler _inner = new(new Dictionary<string, CompiledLanguage>(), new ReaderWriterLockSlim());

    // ConcurrentDictionary<string, CompiledLanguage>: 이 컴파일러 인스턴스는 프로세스 전체 싱글턴이라 여러 렌더(스레드)가
    // 동시에 같은 언어를 처음 컴파일할 수 있다. GetOrAdd(Func<TKey,TValue> 오버로드)는 같은 키의 동시 생성을 직렬화하지 않는다 —
    // 동시에 도착한 호출은 각자 팩토리를 실행할 수 있고, 그중 하나의 결과만 저장된다(나머지는 버려진다). 이미 채워진 키의 읽기는
    // 락 없이 진행되므로 요청마다 반복되는 "캐시 히트" 경로(거의 모든 호출)가 경합 없이 빠르다. 이 중복 실행은 여기서는 무해하다 —
    // 같은 언어를 재컴파일한 결과는 항상 동일한(등가인) 정규식이라, 어느 스레드의 결과가 저장되든 이후 조회 결과는 같다.
    private readonly ConcurrentDictionary<string, CompiledLanguage> _rebuilt = new(StringComparer.Ordinal);

    public CompiledLanguage Compile(ILanguage language) =>
        _rebuilt.GetOrAdd(language.Id, _ =>
        {
            var inner = _inner.Compile(language);
            var timed = new Regex(inner.Regex.ToString(), inner.Regex.Options, MatchTimeout);
            return new CompiledLanguage(inner.Id, inner.Name, timed, inner.Captures);
        });
}

/// <summary>강조 도중 토큰 콜백이 호출될 때마다 이번 렌더에 남은 시간 예산을 확인해, 다 썼으면 즉시 멈춘다.
/// 정규식 매치 타임아웃(<see cref="TimeBoundedLanguageCompiler.MatchTimeout"/>)은 매치 "한 건"이 오래 걸리는 경우만 잡는다 —
/// 빠른 매치가 아주 많이 반복되는 경우는 매치 1건 단위로는 절대 안 걸리므로, 이 데코레이터가 토큰 단위로 누적 시간을 검사해 대신 잡는다.</summary>
/// <param name="inner">실제 토큰화를 위임할 공유 파서.</param>
/// <param name="isBudgetExceeded">이번 렌더의 시간 예산을 다 썼는지 확인하는 델리게이트. 호출할 때마다 최신 경과 시간을 반영해야 한다.</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. 렌더 1회(<see cref="HighlightingCodeBlockRenderer"/> 인스턴스 1개가 처리하는 코드블록 1개)마다 새로 만들어
/// 그 시도 동안만 쓰고 버리는 값 객체다. 감싸는 <paramref name="inner"/>(공유 <see cref="LanguageParser"/>)는 상태가 없어 병렬 호출에 안전하지만,
/// 이 데코레이터 자체와 <paramref name="isBudgetExceeded"/> 클로저는 특정 렌더의 시계를 가리키므로 절대 두 렌더 사이에서 공유하면 안 된다.</description></item>
/// <item><description><b>Memory Allocation:</b> 인스턴스 1개 + 콜백을 감싸는 델리게이트 1개. 토큰마다 추가 할당은 없다.</description></item>
/// <item><description><b>Blocking:</b> 예산 확인 자체는 즉시 반환. 실제 파싱은 <paramref name="inner"/>가 하는 동기 CPU 작업 그대로다.</description></item>
/// </list>
/// </remarks>
internal sealed class DeadlineLanguageParser(ILanguageParser inner, Func<bool> isBudgetExceeded) : ILanguageParser
{
    public void Parse(string sourceCode, ILanguage language, Action<string, IList<Scope>> parseHandler)
    {
        inner.Parse(sourceCode, language, (parsedSourceCode, scopes) =>
        {
            if (isBudgetExceeded()) throw new HighlightBudgetExceededException();
            parseHandler(parsedSourceCode, scopes);
        });
    }
}

/// <summary>한 번의 <see cref="MarkdownRenderer.Render"/> 호출에서 강조에 쓸 수 있는 시간 예산(<see cref="HighlightingCodeBlockRenderer.MaxHighlightMilliseconds"/>)을
/// 다 썼을 때 <see cref="DeadlineLanguageParser"/>가 던진다. <see cref="HighlightingCodeBlockRenderer"/>가 이를 잡아 해당 블록만 이스케이프한 일반 코드블록으로 떨어뜨린다.</summary>
internal sealed class HighlightBudgetExceededException() : Exception("이 렌더의 강조 시간 예산을 다 썼습니다.");
