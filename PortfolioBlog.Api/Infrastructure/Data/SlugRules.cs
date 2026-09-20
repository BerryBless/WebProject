using System.Text.RegularExpressions;

namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>slug 형식 규칙. DB CHECK 제약(<see cref="AppDbContext.SlugPattern"/>)과 같은 문자 집합을 검사하지만, .NET 매처는 <see cref="AppDbContext.SlugPattern"/> 문자열을 그대로 쓰지 않고 앵커만 <c>\A</c>·<c>\z</c>로 바꾼 별도 패턴을 쓴다. 글·시리즈가 공유하므로 Features가 아니라 여기에 둔다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 정적 클래스이며 소스 생성된 <see cref="Regex"/> 매처는 스레드 안전하게 재사용 가능하다.</description></item>
/// <item><description><b>Memory Allocation:</b> Zero-allocation. <see cref="Regex.IsMatch(string)"/>는 별도 매치 결과 객체를 만들지 않는다.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O 없음.</description></item>
/// </list>
/// <b>[<c>^</c>/<c>$</c> 대신 <c>\A</c>/<c>\z</c>를 쓰는 이유]</b> .NET <see cref="Regex"/>의 <c>$</c>는 문자열 끝뿐 아니라 "끝에 오는 단 하나의 <c>\n</c> 바로 앞"에서도 매칭된다(<see cref="RegexOptions.Multiline"/> 여부와 무관).
/// PostgreSQL의 <c>~</c> 연산자는 이런 예외가 없어 <c>"abc\n"</c>을 <see cref="AppDbContext.SlugPattern"/>(<c>^...$</c>)으로 거부한다.
/// 두 매처가 이 문자열 하나로 엇갈리면 형식 검증을 통과한 뒤 DB CHECK(<c>CK_Posts_Slug_Format</c>)에서만 막혀 400 대신 500이 된다.
/// <c>\A</c>(문자열의 절대 시작)·<c>\z</c>(문자열의 절대 끝, 개행 예외 없음)로 앵커링하면 이 차이가 사라진다.
/// DB CHECK 제약의 문자열(<see cref="AppDbContext.SlugPattern"/>)은 PostgreSQL 쪽 계약이라 그대로 두고, .NET 쪽만 독립적으로 앵커를 바꾼다 — 두 패턴은 문자 집합 규칙이 항상 같도록 함께 수정해야 한다.
/// </remarks>
public static partial class SlugRules
{
    // GeneratedRegex: 컴파일 타임에 소스 생성된 매처라 런타임 Regex 구성·JIT 비용이 없고, 이 패턴은 역추적 폭발이 없는 단순 반복이다.
    // \A...\z: DB CHECK 제약의 ^...$ 문자열과 문자 집합은 같지만 앵커만 바꾼 것이다. .NET의 $는 끝의 단일 \n 앞에서도 매칭되어
    // PostgreSQL ~ 연산자와 판정이 갈릴 수 있다(예: "abc\n") — \A·\z는 그런 예외가 없는 절대 시작/끝 앵커다.
    [GeneratedRegex(@"\A[a-z0-9]+(-[a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
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
