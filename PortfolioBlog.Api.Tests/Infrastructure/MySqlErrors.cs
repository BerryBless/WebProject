using System.Reflection;
using MySqlConnector;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>
/// 오류 파이프라인 테스트가 던질 <see cref="MySqlException"/>을 번호로 만든다. MySqlConnector의 <see cref="MySqlException"/>에는 public 생성자가 없어
/// non-public <c>(MySqlErrorCode errorCode, string sqlState, string message, Exception innerException)</c> 생성자를 리플렉션으로 호출한다(스파이크 S15a:
/// 이 생성자로 만든 예외의 <c>Number</c>=1062, <c>ErrorCode</c>=<c>DuplicateKeyEntry</c>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. <see cref="Ctor"/>는 정적 초기화 시 1회만 리플렉션 조회되고 이후 읽기만 하며, <see cref="Create"/>는 매 호출마다 새 예외 인스턴스를 만들어 반환하므로 공유 가변 상태가 없다.</description></item>
/// <item><description><b>Memory Allocation:</b> <see cref="Create"/> 호출마다 <see cref="MySqlException"/> 인스턴스 1개와 인자 배열·메시지 문자열을 힙에 할당한다. 실제 DB 왕복이 없어 그 외 할당은 없다.</description></item>
/// <item><description><b>Blocking:</b> 동기 실행. 리플렉션 호출(<see cref="ConstructorInfo.Invoke(object?[]?)"/>)만 하며 I/O·대기가 없다.</description></item>
/// </list>
/// </remarks>
internal static class MySqlErrors
{
    private static readonly ConstructorInfo Ctor = typeof(MySqlException).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic, [typeof(MySqlErrorCode), typeof(string), typeof(string), typeof(Exception)])
        ?? throw new InvalidOperationException("MySqlException(MySqlErrorCode, string, string, Exception) 생성자가 없다 — MySqlConnector 버전이 바뀌었는지 확인한다.");

    /// <summary>지정 번호의 예외를 만든다.</summary>
    /// <param name="number">MySQL 오류 번호. <see cref="MySqlException.Number"/>(int)로 그대로 읽힌다. 열거형에 이름이 없는 번호도 캐스트로 담긴다.</param>
    /// <returns>던질 예외. SQLSTATE는 일반값 <c>HY000</c>이다(분류기는 번호만 본다).</returns>
    public static MySqlException Create(int number) =>
        (MySqlException)Ctor.Invoke([(MySqlErrorCode)number, "HY000", $"simulated {number}", null]);
}
