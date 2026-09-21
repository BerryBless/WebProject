namespace PortfolioBlog.Api.Contracts;

/// <summary>DB에 닿는 모든 문자열이 지키는 공통 규칙의 단일 출처.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 정적 함수.</description></item>
/// <item><description><b>Memory Allocation:</b> Zero-allocation(벡터화된 문자 검색).</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
/// </list>
/// PostgreSQL <c>text</c>는 NUL을 저장할 수 없고(SqlState 22021) JSON·쿼리 문자열은 NUL을 실어 나를 수 있다.
/// 본문 필드뿐 아니라 쿼리 문자열·경로 값·파일 이름까지 같은 규칙으로 막아 "검증 통과 → DB에서 500"을 없앤다.
/// </remarks>
public static class TextRules
{
    /// <summary>NUL 거부 시 필드 오류 메시지.</summary>
    public const string NulMessage = "제어 문자(NUL)를 포함할 수 없습니다.";

    /// <summary><paramref name="value"/>에 NUL(U+0000) 문자가 포함되어 있는지 검사한다.</summary>
    /// <param name="value">검사할 문자열. <c>null</c>이면 <c>false</c>.</param>
    /// <returns>NUL을 포함하면 <c>true</c>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 정적 함수로 공유 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation. <see cref="string.Contains(char)"/>는 내부적으로 벡터화된(SIMD) 문자 검색을 수행하며 새 문자열·버퍼를 만들지 않는다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public static bool ContainsNul(string? value) => value is not null && value.Contains('\0');
}
