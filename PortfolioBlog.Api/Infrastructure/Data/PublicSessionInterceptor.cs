using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>공개 조회 연결이 열릴 때마다 세션을 읽기 전용으로 두고, SELECT 실행 시간과 메타데이터 잠금 대기에 상한을 건다(스펙 D2).</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 불변 SQL 문자열만 갖고 컨텍스트 간에 공유된다. 콜백은 연결을 연 호출 스레드(요청 스레드)에서 실행된다.</description></item>
/// <item><description><b>Memory Allocation:</b> 연결 열기당 명령 객체 1개. SQL 문자열은 생성자에서 한 번 만든다.</description></item>
/// <item><description><b>Blocking:</b> 비동기 경로는 DB 왕복 1회를 await한다(연결을 열 때마다 1회, 스펙 R2). 동기 경로는 동기 왕복이다.</description></item>
/// </list>
/// MySqlConnector에는 PG의 시작 매개변수(<c>Options=-c …</c>)에 해당하는 것이 없어 연결마다 설정한다. 연결 문자열에 <c>ConnectionReset=true</c>가 강제되어 있어(풀에서 꺼낼 때 세션 리셋)
/// 이전 대여자가 바꾼 세션 값이 남지 않고, 이 인터셉터가 리셋 직후 다시 설정한다. <c>lock_wait_timeout</c>(초, 최소 1)은 메타데이터 잠금 대기
/// (예: 누군가 <c>LOCK TABLES</c>)를 끊는다(SELECT는 <c>max_execution_time</c>이 더 짧으면 그쪽이 먼저 3024로 끊는다 — 스파이크 S3c. <c>lock_wait_timeout</c>은 그 상한이 닿지 않는 경로의 안전망이다). InnoDB 일반 SELECT는 행 잠금을 기다리지 않으므로 이것이 공개 경로의 유일한 무한 대기 지점이다.
/// </remarks>
public sealed class PublicSessionInterceptor : DbConnectionInterceptor
{
    private readonly string _sql;

    /// <summary>세션 설정 SQL을 만든다.</summary>
    /// <param name="maxExecutionMs">SELECT 실행 상한(밀리초, 100~60000 — StartupValidation이 먼저 검증한다).</param>
    /// <exception cref="ArgumentOutOfRangeException">범위를 벗어났을 때. 정수만 SQL에 들어가므로 주입 여지는 없지만 범위 밖 값은 설정 오류다.</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 생성 후 불변. 컨텍스트 옵션을 만드는 스레드에서 한 번 호출된다.</description></item>
    /// <item><description><b>Memory Allocation:</b> SQL 문자열 1개(인스턴스 수명 동안 재사용).</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public PublicSessionInterceptor(int maxExecutionMs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxExecutionMs, 100);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxExecutionMs, 60_000);
        var lockWaitSeconds = Math.Max(1, (maxExecutionMs + 999) / 1000);
        _sql = string.Create(CultureInfo.InvariantCulture,
            $"SET SESSION transaction_read_only = ON, max_execution_time = {maxExecutionMs}, lock_wait_timeout = {lockWaitSeconds}");
    }

    /// <summary>동기로 연 공개 연결에 세션 설정을 건다.</summary>
    /// <param name="connection">방금 열린 연결(ConnectionReset=true로 세션이 리셋된 상태).</param>
    /// <param name="eventData">EF 이벤트 데이터(사용하지 않음).</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe(무상태). 연결을 연 호출 스레드에서 실행된다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 명령 객체 1개(즉시 해제).</description></item>
    /// <item><description><b>Blocking:</b> 동기 DB 왕복 1회로 호출 스레드를 블로킹한다(기동 시 <c>PublicRoleGrants.Apply</c>의 동기 경로만 해당).</description></item>
    /// </list>
    /// </remarks>
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = _sql;
        command.ExecuteNonQuery();
    }

    /// <summary>비동기로 연 공개 연결에 세션 설정을 건다.</summary>
    /// <param name="connection">방금 열린 연결(ConnectionReset=true로 세션이 리셋된 상태).</param>
    /// <param name="eventData">EF 이벤트 데이터(사용하지 않음).</param>
    /// <param name="cancellationToken">요청 취소 토큰.</param>
    /// <returns>세션 설정 명령의 완료.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe(무상태). 연결을 연 호출 스레드에서 시작해 await 후 스레드 풀에서 이어진다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 명령 객체 1개와 비동기 상태 머신(즉시 해제).</description></item>
    /// <item><description><b>Blocking:</b> Non-blocking. DB 왕복 1회를 await한다(연결을 열 때마다, 스펙 R2).</description></item>
    /// </list>
    /// </remarks>
    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = _sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
