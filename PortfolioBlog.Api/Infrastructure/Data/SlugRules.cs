using System.Text.RegularExpressions;

namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>slug 형식 규칙. DB CHECK 제약(<see cref="AppDbContext.SlugPattern"/>)과 같은 정규식을 쓴다. 글·시리즈가 공유하므로 Features가 아니라 여기에 둔다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 정적 클래스이며 소스 생성된 <see cref="Regex"/> 매처는 스레드 안전하게 재사용 가능하다.</description></item>
/// <item><description><b>Memory Allocation:</b> Zero-allocation. <see cref="Regex.IsMatch(string)"/>는 별도 매치 결과 객체를 만들지 않는다.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O 없음.</description></item>
/// </list>
/// </remarks>
public static partial class SlugRules
{
    // GeneratedRegex: 컴파일 타임에 소스 생성된 매처라 런타임 Regex 구성·JIT 비용이 없고, 이 패턴은 역추적 폭발이 없는 단순 반복이다.
    [GeneratedRegex(AppDbContext.SlugPattern, RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary><paramref name="slug"/>가 공개 URL slug 형식(길이·문자 집합)을 만족하는지 검사한다.</summary>
    /// <param name="slug">검사할 slug 문자열.</param>
    /// <returns>최대 길이 이하이고 정규식 패턴에 맞으면 <c>true</c>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 입력만 읽고 공유 상태를 변경하지 않는다.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking).</description></item>
    /// </list>
    /// </remarks>
    public static bool IsValid(string slug) => slug.Length <= AppDbContext.SlugMax && Pattern().IsMatch(slug);
}
