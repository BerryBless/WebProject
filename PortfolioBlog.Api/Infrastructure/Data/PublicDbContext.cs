using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>인증 없는 공개 조회 전용 컨텍스트. 모델은 <see cref="AppDbContext"/>와 같고, 연결이 다르다: 문장 시간 제한 + 읽기 전용 트랜잭션.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. 요청 스코프당 1개 인스턴스이며 동시 사용 금지(<see cref="AppDbContext"/>와 같은 계약).</description></item>
/// <item><description><b>Memory Allocation:</b> <see cref="DataServiceCollectionExtensions.AddBlogData"/>가 <c>QueryTrackingBehavior.NoTracking</c>으로 등록하므로 변경 추적기 그래프를 보유하지 않는다. 프로젝션 결과만 힙에 남는다.</description></item>
/// <item><description><b>Blocking:</b> 모든 I/O는 async API로 Non-blocking. 이 컨텍스트로 <c>Migrate()</c>를 호출하지 않는다(마이그레이션은 <see cref="AppDbContext"/> 전용).</description></item>
/// </list>
/// 쓰기 금지는 두 겹이다: 앱에서 <c>SaveChanges</c>가 예외를 던지고, <c>ExecuteUpdate/Delete</c>·원시 SQL처럼 그 길을 지나지 않는 쓰기는
/// PostgreSQL이 <c>default_transaction_read_only=on</c>으로 거부한다(SqlState 25006, 실측: <c>PublicDbContextTests.PublicContext_CannotWrite</c>).
/// <b>알려진 한계(측정됨, 2계층 방어의 문서화된 경계):</b> 같은 물리 연결(세션)을 붙잡은 채 <c>SET default_transaction_read_only = off</c>
/// (또는 <c>SET TRANSACTION READ WRITE</c>)를 직접 실행하면 PostgreSQL은 이를 허용하고 그 뒤의 쓰기가 성공한다
/// (실측: <c>PublicDbContextTests.PublicContext_SessionCanOptOutOfReadOnly_DocumentedLimit</c> — 연결을 명시적으로 열어 두지 않으면
/// EF가 호출마다 풀에서 다른 물리 연결을 빌려 오므로 이 이스케이프는 저절로 재현되지 않는다는 것도 같은 테스트로 측정했다).
/// 이 앱은 그런 SQL을 절대 만들지 않는다 — 모든 조회는 매개변수화된 LINQ(<see cref="PublicQueries"/>)뿐이고 사용자 입력이 SQL 문자열로
/// 조립되는 경로가 없다. 그래도 이 컨텍스트만으로는 "제3자가 이 연결로 임의 SQL을 실행할 수 있다면" 완전한 방어가 아니다 —
/// 진짜 해결책은 쓰기 권한이 아예 없는 DB 롤(스펙 §7 확장 지점)이며, <c>default_transaction_read_only</c>는 그때까지의 심층 방어 계층이다.
/// </remarks>
public sealed class PublicDbContext(DbContextOptions<PublicDbContext> options) : AppDbContext(options)
{
    /// <summary>관리 연결 문자열에서 공개 조회용 연결 문자열을 만든다. 연결 문자열이 다르므로 Npgsql이 풀을 따로 만든다 — 설정이 관리 연결로 새지 않는다.</summary>
    /// <param name="baseConnectionString"><c>ConnectionStrings:Default</c>. 여기에 <c>Options</c>가 있었다면 **대체된다**.</param>
    /// <param name="statementTimeoutMs">문장 하나의 최대 실행 시간(밀리초).</param>
    /// <returns>공개 조회 전용 <c>Options</c>(<c>statement_timeout</c>·<c>default_transaction_read_only</c>)가 설정된 연결 문자열.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 순수 함수(입력 문자열을 읽어 새 문자열을 만들 뿐).</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="NpgsqlConnectionStringBuilder"/> 인스턴스 1개 + 결과 문자열 1개.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public static string BuildConnectionString(string baseConnectionString, int statementTimeoutMs) =>
        new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            // 시작 매개변수로 주면 세션 기본값이 된다: 풀에서 재사용될 때의 리셋도 이 값으로 돌아온다(실측: S6 스파이크 —
            // SET statement_timeout = 0으로 바꾼 뒤 커넥션을 풀에 반납·재획득해도 SHOW statement_timeout이 이 값으로 돌아왔다).
            Options = $"-c statement_timeout={statementTimeoutMs} -c default_transaction_read_only=on",
            ApplicationName = "PortfolioBlog.Public",
        }.ConnectionString;

    /// <summary>공개 컨텍스트는 저장을 허용하지 않는다.</summary>
    /// <param name="acceptAllChangesOnSuccess">사용하지 않음(항상 예외).</param>
    /// <returns>반환하지 않음 — 항상 예외를 던진다.</returns>
    /// <exception cref="InvalidOperationException">항상 던진다. 공개 컨텍스트로는 쓸 수 없다.</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 공유 상태를 건드리기 전에 즉시 예외를 던진다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 예외 인스턴스 1개 외 추가 할당 없음. DB 왕복이 없다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking, 예외로 반환).</description></item>
    /// </list>
    /// </remarks>
    public override int SaveChanges(bool acceptAllChangesOnSuccess) => throw ReadOnly();

    /// <summary>공개 컨텍스트는 저장을 허용하지 않는다.</summary>
    /// <param name="acceptAllChangesOnSuccess">사용하지 않음(항상 예외).</param>
    /// <param name="cancellationToken">사용하지 않음(항상 예외).</param>
    /// <returns>반환하지 않음 — 항상 예외를 던진다.</returns>
    /// <exception cref="InvalidOperationException">항상 던진다. 공개 컨텍스트로는 쓸 수 없다.</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 공유 상태를 건드리기 전에 즉시 예외를 던진다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 예외 인스턴스 1개 외 추가 할당 없음. DB 왕복이 없다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking, 예외로 반환). <c>Task</c>를 기다리지 않고 동기적으로 던진다.</description></item>
    /// </list>
    /// </remarks>
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) => throw ReadOnly();

    /// <summary>쓰기 금지 예외를 만든다.</summary>
    /// <returns>공개 컨텍스트가 쓰기를 거부할 때 던질 예외.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 순수 함수.</description></item>
    /// <item><description><b>Memory Allocation:</b> 예외 인스턴스 1개.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking).</description></item>
    /// </list>
    /// </remarks>
    private static InvalidOperationException ReadOnly() => new("PublicDbContext는 읽기 전용입니다. 쓰기는 AppDbContext로 합니다.");
}
