using Microsoft.EntityFrameworkCore;

namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>인증 없는 공개 조회 전용 컨텍스트. 모델은 <see cref="AppDbContext"/>와 같고, 연결이 다르다: 읽기 전용 세션 + SELECT 실행 시간 상한.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. 요청 스코프당 1개 인스턴스이며 동시 사용 금지(<see cref="AppDbContext"/>와 같은 계약).</description></item>
/// <item><description><b>Memory Allocation:</b> <see cref="DataServiceCollectionExtensions.AddBlogData"/>가 <c>QueryTrackingBehavior.NoTracking</c>으로 등록하므로 변경 추적기 그래프를 보유하지 않는다. 프로젝션 결과만 힙에 남는다.</description></item>
/// <item><description><b>Blocking:</b> 모든 I/O는 async API로 Non-blocking. 연결을 열 때마다 <see cref="PublicSessionInterceptor"/>가 세션 설정 왕복 1회를 더한다. 이 컨텍스트로 <c>Migrate()</c>를 호출하지 않는다(마이그레이션은 <see cref="AppDbContext"/> 전용).</description></item>
/// </list>
/// 쓰기 금지는 세 겹이다 — ① 이 클래스의 <c>SaveChanges</c> 예외, ② <see cref="PublicSessionInterceptor"/>가 연결마다 거는 <c>transaction_read_only=ON</c>
/// (<c>ExecuteUpdate/Delete</c>·원시 SQL처럼 SaveChanges를 거치지 않는 쓰기를 MySQL이 오류 1792로 거부한다), ③ 공개 사용자에게 테이블 단위로만 준
/// SELECT 권한(그 밖의 문장은 오류 1142, <see cref="PublicRoleGrants"/>가 기동 시 검증한다).
/// 세션 값은 연결 문자열에 강제된 <c>ConnectionReset=true</c>로 풀 대여마다 리셋되고 인터셉터가 다시 설정한다. 그래서 같은 세션에서
/// <c>SET SESSION transaction_read_only = OFF</c>로 ②를 스스로 꺼도 그 연결이 풀로 돌아갔다가 다시 대여되면 원상 복구된다(측정: <c>PublicDbContextTests</c>).
/// 세션이 스스로 끌 수 있는 ②와 달리 ③은 세션이 바꿀 수 없으므로, 운영(Public 연결 문자열이 있는 환경)의 실질적 경계는 ③이다.
/// 이 앱은 그런 SQL을 만들지 않는다 — 모든 조회는 매개변수화된 LINQ(<see cref="PublicQueries"/>)뿐이고 사용자 입력이 SQL 문자열로 조립되는 경로가 없다.
/// </remarks>
public sealed class PublicDbContext(DbContextOptions<PublicDbContext> options) : AppDbContext(options)
{
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
    private static InvalidOperationException ReadOnly() => new("PublicDbContext는 읽기 전용입니다. 쓰기는 AppDbContext로 합니다.");
}
