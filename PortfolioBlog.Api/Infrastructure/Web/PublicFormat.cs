using System.Globalization;

namespace PortfolioBlog.Api.Infrastructure.Web;

/// <summary>공개 출력의 날짜 형식. 문화권과 서버 시간대에 의존하지 않는다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 정적 클래스로 공유 가변 상태가 없다.</description></item>
/// <item><description><b>Memory Allocation:</b> 각 메서드가 서식화된 결과 문자열 1개를 할당한다.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O·시스템 시간대 조회 없음(<see cref="DateTimeOffset.ToUniversalTime"/>은 오프셋 산술뿐이다).</description></item>
/// </list>
/// </remarks>
public static class PublicFormat
{
    /// <summary>RFC 3339 UTC 문자열을 만든다.</summary>
    /// <param name="value">서식화할 시각.</param>
    /// <returns><c>2026-09-21T03:04:05Z</c> 형태. HTML <c>time[datetime]</c>·Atom·sitemap 공용.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 정적 메서드.</description></item>
    /// <item><description><b>Memory Allocation:</b> 서식화 결과 문자열 1개.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    public static string Rfc3339(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>사람이 읽는 날짜 문자열을 만든다.</summary>
    /// <param name="value">서식화할 시각.</param>
    /// <returns><c>2026-09-21</c> 형태(UTC 기준).</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 정적 메서드.</description></item>
    /// <item><description><b>Memory Allocation:</b> 서식화 결과 문자열 1개.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    public static string DisplayDate(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
