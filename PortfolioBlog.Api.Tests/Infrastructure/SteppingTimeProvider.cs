namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary><see cref="GetTimestamp"/>를 부를 때마다 고정 간격만큼 전진하는 가짜 시계. 기계 속도와 무관하게 "시간 예산 소진"을 재현한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. <see cref="GetTimestamp"/>가 <see cref="Interlocked.Add(ref long, long)"/>로 <see cref="_now"/>를 갱신하므로 여러 스레드가 동시에 불러도 값이 찢어지거나 갱신이 유실되지 않는다.</description></item>
/// <item><description><b>Memory Allocation:</b> Zero-allocation. <c>long</c> 필드 하나만 갱신한다.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O·대기 없음.</description></item>
/// </list>
/// </remarks>
internal sealed class SteppingTimeProvider(long stepMilliseconds) : TimeProvider
{
    // Interlocked: 렌더러가 다른 스레드에서 읽어도 찢어지지 않게 한다(테스트는 단일 스레드지만 시계 계약을 지킨다).
    private long _now;

    /// <summary>1틱 = 1ms.</summary>
    public override long TimestampFrequency => 1000;

    /// <summary>호출마다 내부 시각을 <c>stepMilliseconds</c>만큼 전진시킨 뒤 그 값을 반환한다.</summary>
    /// <returns>이번 호출까지 누적된 가짜 타임스탬프(밀리초 단위 틱).</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. <see cref="Interlocked.Add(ref long, long)"/>로 갱신·반환을 원자적으로 수행한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking).</description></item>
    /// </list>
    /// </remarks>
    public override long GetTimestamp() => Interlocked.Add(ref _now, stepMilliseconds);
}
