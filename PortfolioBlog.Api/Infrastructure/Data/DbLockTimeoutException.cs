namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>DB 사용자 잠금(<c>GET_LOCK</c>) 대기가 상한을 넘었다. MySQL은 이 경우 오류가 아니라 0을 반환하므로 앱이 직접 던진다.</summary>
/// <param name="message">운영 로그용 설명(비밀값 없음).</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 불변 예외 객체. 생성 후 공유해도 안전하다.</description></item>
/// <item><description><b>Memory Allocation:</b> 예외 인스턴스 1개.</description></item>
/// <item><description><b>Blocking:</b> 생성자는 즉시 반환한다.</description></item>
/// </list>
/// </remarks>
public sealed class DbLockTimeoutException(string message) : Exception(message);
