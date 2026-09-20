namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>테스트가 앞으로 돌릴 수 있는 시계. 세션 절대 만료 검증용.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. <see cref="Advance"/>와 <see cref="GetUtcNow"/>는 서로 다른 스레드(테스트 스레드·요청 파이프라인 스레드)에서 동시에 호출될 수 있다.</description></item>
/// <item><description><b>Memory Allocation:</b> Zero-allocation. <see cref="long"/> 필드 하나만 갱신한다.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환. I/O·대기 없음.</description></item>
/// </list>
/// </remarks>
public sealed class MutableTimeProvider : TimeProvider
{
    // Interlocked로 읽고 쓰는 틱: 테스트 스레드가 바꾼 값을 서버 스레드가 찢어짐 없이 관측한다.
    private long _offsetTicks;

    /// <summary>실제 시스템 UTC 시각에 누적된 오프셋을 더해 반환한다.</summary>
    /// <returns>시스템 UTC + 현재까지 <see cref="Advance"/>로 누적된 오프셋.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. <see cref="Interlocked.Read(ref long)"/>로 다른 스레드가 <see cref="Advance"/>로 갱신 중인 값도 찢어짐 없이 읽는다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="DateTimeOffset"/> 값 타입 연산만 하여 힙 할당이 없다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

    /// <summary>시계를 앞으로(또는 뒤로) 돌린다. 이후 <see cref="GetUtcNow"/> 호출부터 즉시 반영된다.</summary>
    /// <param name="by">누적할 오프셋. 세션 만료를 흉내낼 때는 양수를 준다.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. <see cref="Interlocked.Add(ref long, long)"/>로 다른 스레드의 <see cref="GetUtcNow"/> 읽기와 경합 없이 원자적으로 누적한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);
}
