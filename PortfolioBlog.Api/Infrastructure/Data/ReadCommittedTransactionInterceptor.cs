using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>격리 수준을 지정하지 않은 모든 트랜잭션(SaveChanges 암묵 트랜잭션, <c>BeginTransactionAsync()</c>)을 READ COMMITTED로 시작한다(스펙 D7).</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 무상태. 콜백은 트랜잭션을 시작한 호출 스레드에서 실행된다.</description></item>
/// <item><description><b>Memory Allocation:</b> 트랜잭션 객체 1개(원래도 만들어질 것을 대신 만든다). 추가 할당 없음.</description></item>
/// <item><description><b>Blocking:</b> 비동기 경로는 <c>BeginTransactionAsync</c>를 await한다. 추가 왕복은 없다(원래 시작 문장을 대체).</description></item>
/// </list>
/// 기존 동시성 설계(SeriesEndpoints의 FOR UPDATE, TagResolver의 서수 삽입)는 전부 READ COMMITTED를 전제로 한다. 이 인터셉터는 <b>필수</b>다:
/// MySqlConnector의 인자 없는 <c>BeginTransaction()</c>은 서버·세션 기본값과 무관하게 매번
/// <c>SET SESSION TRANSACTION ISOLATION LEVEL REPEATABLE READ</c>를 보낸다(스파이크 S14, general_log 실측). 서버 인자
/// <c>--transaction-isolation=READ-COMMITTED</c>만으로는 EF 트랜잭션이 전부 REPEATABLE READ로 돈다.
/// 호출부가 격리 수준을 명시하면 그대로 둔다. EF를 거치지 않고 <c>MySqlConnection.BeginTransaction()</c>을 직접 부르는 코드는 이 인터셉터가 닿지 않으므로
/// 반드시 <c>IsolationLevel.ReadCommitted</c>를 명시한다.
/// </remarks>
public sealed class ReadCommittedTransactionInterceptor : DbTransactionInterceptor
{
    /// <summary>격리 수준이 지정되지 않은 동기 트랜잭션 시작을 READ COMMITTED 트랜잭션으로 대체한다.</summary>
    /// <param name="connection">트랜잭션을 시작할 열린 연결.</param>
    /// <param name="eventData">요청된 격리 수준을 담은 EF 이벤트 데이터.</param>
    /// <param name="result">EF가 진행할 기본 결과.</param>
    /// <returns>격리 수준 미지정이면 직접 만든 READ COMMITTED 트랜잭션으로 억제한 결과, 아니면 <paramref name="result"/> 그대로.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe(무상태). 트랜잭션을 시작한 호출 스레드에서 실행된다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 트랜잭션 객체 1개(원래 경로가 만들 것을 대신 만든다).</description></item>
    /// <item><description><b>Blocking:</b> 동기 DB 왕복(격리 수준 설정 + START TRANSACTION)으로 호출 스레드를 블로킹한다. 원래 경로의 왕복을 대체할 뿐 늘리지 않는다.</description></item>
    /// </list>
    /// </remarks>
    public override InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result) =>
        eventData.IsolationLevel == IsolationLevel.Unspecified
            ? InterceptionResult<DbTransaction>.SuppressWithResult(connection.BeginTransaction(IsolationLevel.ReadCommitted))
            : result;

    /// <summary>격리 수준이 지정되지 않은 비동기 트랜잭션 시작을 READ COMMITTED 트랜잭션으로 대체한다.</summary>
    /// <param name="connection">트랜잭션을 시작할 열린 연결.</param>
    /// <param name="eventData">요청된 격리 수준을 담은 EF 이벤트 데이터.</param>
    /// <param name="result">EF가 진행할 기본 결과.</param>
    /// <param name="cancellationToken">요청 취소 토큰.</param>
    /// <returns>격리 수준 미지정이면 직접 만든 READ COMMITTED 트랜잭션으로 억제한 결과, 아니면 <paramref name="result"/> 그대로.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe(무상태). 호출 스레드에서 시작해 await 후 스레드 풀에서 이어진다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 트랜잭션 객체 1개와 비동기 상태 머신. 격리 수준을 지정한 경로는 동기 완료 <see cref="ValueTask{TResult}"/>라 추가 할당 없음.</description></item>
    /// <item><description><b>Blocking:</b> Non-blocking. <c>BeginTransactionAsync</c>를 await한다(원래 시작 문장을 대체, 추가 왕복 없음).</description></item>
    /// </list>
    /// </remarks>
    // ValueTask: 격리 수준을 명시한 호출은 await 없이 결과를 바로 돌려주므로 Task 할당 없이 동기 완료된다.
    public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection, TransactionStartingEventData eventData,
        InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default) =>
        eventData.IsolationLevel == IsolationLevel.Unspecified
            ? InterceptionResult<DbTransaction>.SuppressWithResult(await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
            : result;
}
