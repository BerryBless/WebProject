using PortfolioBlog.Api.Infrastructure.Access;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>
/// <see cref="SessionRules.IsValid"/>의 절대 만료·지문 일치·epoch 일치 판정을 전 경우로 고정하는 단위 테스트.
/// 시계·DB·설정에 의존하지 않는 순수 함수라 실제 시간 경과 없이 값만으로 검증한다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 실행된다. 호스트·DB·시계 의존이 없다.</description></item>
/// <item><description><b>Memory Policy:</b> 케이스마다 <see cref="DateTimeOffset"/> 값 타입 연산만 하며 추가 할당이 없다.</description></item>
/// <item><description><b>Concurrency:</b> 케이스 간 공유 가변 상태가 없으므로 병렬 실행에 안전하다.</description></item>
/// </list>
/// </remarks>
public sealed class SessionRulesTests
{
    private static readonly DateTimeOffset Issued = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    /// <summary>발급 시각·경과 시간·지문·epoch 중 필요한 것만 바꿔 <see cref="SessionRules.IsValid"/>를 호출하는 테스트 헬퍼.
    /// 기본값은 "신선하고 모두 일치"하는 유효한 세션을 나타낸다.</summary>
    /// <param name="issued">티켓의 발급 시각. 생략하면 <see cref="Issued"/>.</param>
    /// <param name="age">발급 시각으로부터 "지금"까지의 경과 시간. 생략하면 1시간(음수면 미래 발급을 흉내낸다).</param>
    /// <param name="fp">티켓에 담긴 비밀번호 지문. 생략하면 현재 지문과 같은 <c>"f1"</c>.</param>
    /// <param name="epoch">티켓에 담긴 세션 epoch 문자열. 생략하면 현재 epoch(3)과 같은 <c>"3"</c>.</param>
    /// <returns><see cref="SessionRules.IsValid"/>의 판정 결과.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 정적 상태를 읽기만 하고 쓰지 않으므로 병렬 호출에 안전하다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="TimeSpan"/>·<see cref="DateTimeOffset"/> 값 타입 연산만 하여 힙 할당이 없다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. <see cref="SessionRules.IsValid"/> 자체가 Zero-allocation·즉시 반환이다.</description></item>
    /// </list>
    /// </remarks>
    private static bool IsValid(DateTimeOffset? issued = null, TimeSpan? age = null, string? fp = "f1", string? epoch = "3") =>
        SessionRules.IsValid(issued ?? Issued, Issued + (age ?? TimeSpan.FromHours(1)), Lifetime, fp, "f1", epoch, 3);

    /// <summary>발급 1시간 후, 지문·epoch가 모두 일치하는 신선한 세션은 유효한지 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 공유 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 힙 할당 없음.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    [Fact] public void Fresh_Matching_IsValid() => Assert.True(IsValid());

    /// <summary>경과 시간이 절대 수명과 정확히 같으면(초과가 아니라 같아도) 무효인지 검증한다(경계는 배타적).</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 공유 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 힙 할당 없음.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    [Fact] public void ExactlyAtLifetime_IsInvalid() => Assert.False(IsValid(age: Lifetime));

    /// <summary>경과 시간이 절대 수명을 넘으면 무효인지 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 공유 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 힙 할당 없음.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    [Fact] public void PastLifetime_IsInvalid() => Assert.False(IsValid(age: TimeSpan.FromHours(13)));

    /// <summary>"지금"이 발급 시각보다 앞서면(시계 역행·위조) 무효인지 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 공유 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 힙 할당 없음.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    [Fact] public void IssuedInFuture_IsInvalid() => Assert.False(IsValid(age: TimeSpan.FromMinutes(-5)));

    /// <summary>발급 시각이 아예 없는(null) 티켓은 무효인지 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 공유 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 힙 할당 없음.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public void MissingIssuedUtc_IsInvalid() =>
        Assert.False(SessionRules.IsValid(null, Issued, Lifetime, "f1", "f1", "3", 3));

    /// <summary>티켓의 비밀번호 지문이 현재 지문과 다르면(비밀번호가 바뀜) 무효인지 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 공유 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 힙 할당 없음.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    [Fact] public void FingerprintMismatch_IsInvalid() => Assert.False(IsValid(fp: "f2"));   // 비밀번호가 바뀜

    /// <summary>티켓에 지문 클레임이 아예 없으면(null) 무효인지 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 공유 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 힙 할당 없음.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    [Fact] public void MissingFingerprint_IsInvalid() => Assert.False(IsValid(fp: null));

    /// <summary>티켓의 epoch가 현재보다 낮으면(로그아웃으로 이미 폐기됨) 무효인지 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 공유 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 힙 할당 없음.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    [Fact] public void OlderEpoch_IsInvalid() => Assert.False(IsValid(epoch: "2"));            // 로그아웃으로 폐기됨

    /// <summary>티켓의 epoch 문자열이 숫자가 아니면 무효인지 검증한다(파싱 실패를 유효로 오판하지 않음).</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 공유 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 힙 할당 없음.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    [Fact] public void NonNumericEpoch_IsInvalid() => Assert.False(IsValid(epoch: "x"));

    /// <summary>티켓에 epoch 클레임이 아예 없으면(null) 무효인지 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 공유 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 힙 할당 없음.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    [Fact] public void MissingEpoch_IsInvalid() => Assert.False(IsValid(epoch: null));
}
