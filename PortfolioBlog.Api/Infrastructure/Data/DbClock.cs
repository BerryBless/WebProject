namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>DB에 저장되는 모든 시각의 단일 출처. UTC이며 마이크로초로 절삭한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 무상태.</description></item>
/// <item><description><b>Memory Allocation:</b> Zero-allocation(struct 반환).</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
/// </list>
/// MySQL <c>DATETIME(6)</c>은 마이크로초까지 저장하므로 100ns 틱을 미리 절삭해야 "저장 전 값 == 재조회 값"이 성립한다.
/// </remarks>
public static class DbClock
{
    private const long TicksPerMicrosecond = 10;

    /// <summary>마이크로초로 절삭된 현재 UTC 시각을 반환한다.</summary>
    /// <returns>오프셋 0, 100ns 미만 잔여 틱이 제거된 <see cref="DateTimeOffset"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 공유 가변 상태를 읽거나 쓰지 않는다.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation. <see cref="DateTimeOffset"/>은 스택에 위치하는 값 형식이다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O·대기 없음.</description></item>
    /// </list>
    /// </remarks>
    public static DateTimeOffset UtcNow()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Ticks - now.Ticks % TicksPerMicrosecond, TimeSpan.Zero);
    }
}
