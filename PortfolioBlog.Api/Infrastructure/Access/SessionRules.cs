using System.Globalization;

namespace PortfolioBlog.Api.Infrastructure.Access;

/// <summary>세션 티켓이 아직 유효한지 판정하는 순수 함수. 시계·DB·설정에 의존하지 않아 단위 테스트로 전 경우를 고정한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 무상태.</description></item>
/// <item><description><b>Memory Allocation:</b> Zero-allocation.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
/// </list>
/// </remarks>
public static class SessionRules
{
    /// <summary>세션 쿠키 티켓의 클레임 타입: 로그인 시점의 <see cref="AdminCredential.Fingerprint"/> 값을 담는다.</summary>
    public const string FingerprintClaim = "pwd";

    /// <summary>세션 쿠키 티켓의 클레임 타입: 로그인 시점의 <see cref="Domain.AdminState.SessionEpoch"/> 값을 담는다.</summary>
    public const string EpochClaim = "epoch";

    /// <summary>발급 시각·비밀번호 지문·세션 epoch가 모두 현재 서버 상태와 일치하는지 판정한다.</summary>
    /// <param name="issuedUtc">티켓 발급 시각(UTC). 없으면 무효.</param>
    /// <param name="now">판정 시점의 현재 시각(UTC).</param>
    /// <param name="lifetime">절대 세션 수명(sliding 연장 없음).</param>
    /// <param name="ticketFingerprint">티켓에 담긴 비밀번호 지문 클레임 값.</param>
    /// <param name="currentFingerprint">현재 <see cref="AdminCredential.Fingerprint"/> 값.</param>
    /// <param name="ticketEpoch">티켓에 담긴 세션 epoch 클레임 값(문자열, 숫자여야 함).</param>
    /// <param name="currentEpoch">DB에 저장된 현재 <see cref="Domain.AdminState.SessionEpoch"/> 값.</param>
    /// <returns>모든 조건이 일치하면 <c>true</c>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 정적 메서드이며 입력 매개변수만 읽는다.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation. <see cref="int.TryParse(string?, NumberStyles, IFormatProvider?, out int)"/>는 문자열을 파싱만 하고 새로 할당하지 않는다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public static bool IsValid(DateTimeOffset? issuedUtc, DateTimeOffset now, TimeSpan lifetime,
        string? ticketFingerprint, string currentFingerprint, string? ticketEpoch, int currentEpoch)
    {
        if (issuedUtc is not { } issued || issued > now || now - issued >= lifetime)
        {
            return false; // 절대 수명. sliding 연장이 없으므로 발급 시각만 본다.
        }
        if (!string.Equals(ticketFingerprint, currentFingerprint, StringComparison.Ordinal))
        {
            return false; // 비밀번호(해시)가 바뀌었다
        }
        return int.TryParse(ticketEpoch, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) && epoch == currentEpoch;
    }
}
