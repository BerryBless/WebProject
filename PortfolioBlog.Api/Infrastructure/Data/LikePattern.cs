namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>사용자 입력을 <c>ILIKE</c>의 "포함" 패턴으로 바꾼다. 메타문자(<c>\ % _</c>)를 이스케이프해 입력이 와일드카드로 해석되지 않게 한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 정적 클래스이며 공유 가변 상태가 없어 여러 스레드가 동시에 호출해도 안전하다.</description></item>
/// <item><description><b>Memory Allocation:</b> 호출마다 <see cref="string.Replace(string, string, StringComparison)"/> 체이닝이 중간 문자열을 거쳐 최종 결과 문자열 1개를 힙에 할당한다.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O·대기 없음.</description></item>
/// </list>
/// 값은 항상 매개변수로 전달되므로 SQL 주입과는 별개의 문제다 — 여기서 막는 것은 "<c>%</c> 한 글자로 전체 테이블 스캔"과 의도와 다른 매칭이다.
/// </remarks>
public static class LikePattern
{
    /// <summary><c>ILIKE</c> 호출의 <c>ESCAPE</c> 절에 넘길 이스케이프 문자.</summary>
    public const string Escape = "\\";

    /// <summary><paramref name="term"/>의 메타문자를 이스케이프하고 앞뒤에 <c>%</c>를 붙여 "포함" 패턴을 만든다.</summary>
    /// <param name="term">이스케이프 전 원본 검색어.</param>
    /// <returns><c>EF.Functions.ILike(column, pattern, LikePattern.Escape)</c>에 바로 쓸 수 있는 패턴 문자열.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 입력 문자열만 읽고 공유 상태를 건드리지 않는다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 결과 문자열 1개(치환 체이닝의 중간 산물 포함, 힙 크기는 입력 길이에 비례).</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking).</description></item>
    /// </list>
    /// 치환 순서가 중요하다: <c>\</c>를 먼저 <c>\\</c>로 바꾸지 않으면 이후 <c>%</c>·<c>_</c> 치환이 만든 <c>\</c>를 다시 이스케이프해버린다.
    /// </remarks>
    public static string Contains(string term) =>
        "%" + term.Replace("\\", "\\\\", StringComparison.Ordinal)
                  .Replace("%", "\\%", StringComparison.Ordinal)
                  .Replace("_", "\\_", StringComparison.Ordinal) + "%";
}
