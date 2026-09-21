using ColorCode;
using ColorCode.Styling;

namespace PortfolioBlog.Api.Infrastructure.Markdown;

/// <summary>코드 강조용 CSS를 ColorCode의 스타일 사전에서 만든다. 파일로 두지 않는 이유: 클래스 이름이 라이브러리 버전과 항상 일치해야 한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. <see cref="Cached"/>는 <see cref="Lazy{T}"/> 기본 모드(<c>ExecutionAndPublication</c>)라 동시에
/// 여러 요청 스레드가 <see cref="Value"/>를 처음 읽어도 <see cref="Build"/>는 정확히 한 번만 실행되고 나머지는 그 결과를 기다렸다가 공유한다.</description></item>
/// <item><description><b>Memory Allocation:</b> 첫 접근에서만 <see cref="Build"/>가 최종 결과 문자열(실측: <see cref="HtmlClassFormatter"/> 두 번 호출 합계
/// 3,503자 = UTF-8 3,503바이트, 전부 ASCII라 문자 수와 바이트 수가 같다) 외에도 <c>GetCSSString()</c>이 반환하는 정제 전 중간 문자열 2개(밝은/어두운 테마),
/// 그 문자열을 <c>Split('}')</c>한 부분 문자열 배열 2개, <c>Where</c>·<c>Select</c> LINQ 반복자가 만드는 임시 객체를 <see cref="ClassRules"/> 호출마다 만든다 —
/// "문자열 1개만 할당"이 아니다. 이후 접근은 <see cref="Cached"/>가 들고 있는 최종 문자열 참조만 반환하고 추가 할당이 없다.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking)이 보통이다. 첫 접근에서 <see cref="Build"/>와 동시에 경합한 다른 스레드는 그 계산이 끝날 때까지 동기 대기한다(<see cref="Lazy{T}"/>의 기본 동작).</description></item>
/// </list>
/// </remarks>
public static class HighlightCss
{
    // Lazy<string>: 첫 요청에서 한 번만 만들고(실측 3,503바이트, Build() 자체는 중간 문자열·배열도 함께 할당한다 — 클래스 remarks 참조)
    // 이후는 같은 문자열을 돌려준다. 기본 모드(ExecutionAndPublication)라 동시 첫 요청도 한 번만 계산한다.
    private static readonly Lazy<string> Cached = new(Build);

    /// <summary>밝은 테마 규칙 + <c>prefers-color-scheme: dark</c> 안의 어두운 테마 규칙.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. <see cref="Lazy{T}"/>가 첫 계산을 직렬화한다(클래스 remarks 참조).</description></item>
    /// <item><description><b>Memory Allocation:</b> 첫 접근에서만 <see cref="Build"/>의 할당이 발생한다(클래스 remarks 참조). 이후는 캐시된 문자열 참조만 반환해 추가 할당이 없다.</description></item>
    /// <item><description><b>Blocking:</b> 첫 접근과 경합하지 않는 한 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    public static string Value => Cached.Value;

    private static string Build() =>
        ClassRules(StyleDictionary.DefaultLight) + "\n@media (prefers-color-scheme: dark){" + ClassRules(StyleDictionary.DefaultDark) + "}\n";

    // 기본 거부: 클래스 선택자로 시작하는 규칙만 남긴다. 라이브러리가 내는 body{background-color…}와
    // .plainText{color:…;color:…}(배경색을 color로 한 번 더 쓰는 버그 — 밝은 테마에서 글자가 흰색이 된다, 실측)는 버린다.
    private static string ClassRules(StyleDictionary styles) =>
        string.Concat(new HtmlClassFormatter(styles).GetCSSString()
            .Split('}', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static rule => rule.StartsWith('.') && !rule.StartsWith(".plainText{", StringComparison.Ordinal))
            .Select(static rule => rule + "}"));
}
