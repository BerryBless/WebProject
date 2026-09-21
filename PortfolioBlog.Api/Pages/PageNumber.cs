using System.Globalization;

namespace PortfolioBlog.Api.Pages;

/// <summary><c>?page=</c>를 직접 읽는다. Razor Pages에서 <c>page</c>는 예약 라우트 키라 모델 바인딩으로는 읽을 수 없다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 정적 클래스로 공유 가변 상태가 없다.</description></item>
/// <item><description><b>Memory Allocation:</b> Zero-allocation. <see cref="int.Parse(ReadOnlySpan{char}, NumberStyles, IFormatProvider?)"/>가 아니라
/// <see cref="int.Parse(string, NumberStyles, IFormatProvider?)"/>를 쓰지만 <paramref name="query"/>가 이미 갖고 있는 문자열을 그대로 넘기므로 추가 문자열 할당은 없다.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O 없음.</description></item>
/// </list>
/// </remarks>
public static class PageNumber
{
    /// <summary><c>?page=</c> 쿼리 값을 읽어 유효한 쪽 번호인지 검사한다.</summary>
    /// <param name="query">현재 요청의 쿼리 컬렉션.</param>
    /// <param name="max">허용하는 최대 쪽 번호.</param>
    /// <param name="page">읽은 쪽 번호(실패하면 의미 없는 값).</param>
    /// <returns>없으면 1쪽으로 <c>true</c>. 값이 하나이고 ASCII 숫자 1~4자리이며 1..<paramref name="max"/>이면 <c>true</c>. 그 밖은 전부 <c>false</c>(호출부가 404).</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 정적 메서드로 <paramref name="query"/>를 읽기만 한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking).</description></item>
    /// </list>
    /// </remarks>
    public static bool TryRead(IQueryCollection query, int max, out int page)
    {
        page = 1;
        if (!query.TryGetValue("page", out var values)) return true;
        if (values.Count != 1) return false;
        var raw = values[0];
        if (string.IsNullOrEmpty(raw) || raw.Length > 4) return false;
        foreach (var c in raw)
        {
            if (c is < '0' or > '9') return false; // char.IsDigit는 전각·다른 문자 체계의 숫자도 참이다
        }
        page = int.Parse(raw, NumberStyles.None, CultureInfo.InvariantCulture);
        return page >= 1 && page <= max;
    }
}
