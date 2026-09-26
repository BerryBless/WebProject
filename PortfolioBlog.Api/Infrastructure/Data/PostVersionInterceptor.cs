using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PortfolioBlog.Api.Domain;

namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>PG 시스템 컬럼을 대신하는 앱 관리 행 버전(스펙 D4): 추가되는 Post는 1, 수정되는 Post는 원래 값 + 1.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe(무상태). 컨텍스트 자체는 스레드 안전하지 않으므로 SaveChanges를 호출한 스레드에서만 그 컨텍스트를 만진다.</description></item>
/// <item><description><b>Memory Allocation:</b> 추적 중인 Post 엔트리 열거자 1개. DetectChanges 비용은 SaveChanges가 어차피 치르는 것을 앞당길 뿐이다.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
/// </list>
/// SaveChanges는 인터셉터를 부른 <b>뒤에</b> DetectChanges를 하므로, 여기서 먼저 DetectChanges를 불러야 속성 설정으로 바뀐 Post가 Modified로 보인다.
/// 호출부가 <c>OriginalValue</c>에 클라이언트 버전을 넣어 두면(PostEndpoints) WHERE는 그 값으로, SET은 +1로 나간다. 버전이 달라졌으면 0행이 갱신되어
/// <see cref="DbUpdateConcurrencyException"/>이 된다. 벌크 경로(<c>ExecuteUpdateAsync</c>)는 이 인터셉터를 거치지 않으므로 호출부가 직접 올린다.
/// 이 규칙은 <c>PostVersionTests.EveryPostsBulkUpdate_BumpsVersion</c>이 소스 스캔으로 강제한다.
/// </remarks>
public sealed class PostVersionInterceptor : SaveChangesInterceptor
{
    /// <summary>동기 SaveChanges 직전에 추가·수정되는 Post의 버전을 정한다.</summary>
    /// <param name="eventData">저장 중인 컨텍스트를 담은 EF 이벤트 데이터.</param>
    /// <param name="result">EF가 진행할 기본 결과(그대로 돌려준다).</param>
    /// <returns><paramref name="result"/> 그대로.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> SaveChanges를 호출한 스레드에서 실행된다. 컨텍스트를 다른 스레드와 공유하지 않는다는 EF 규칙을 따른다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 추적 중인 Post 엔트리 열거자 1개.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음(DetectChanges는 메모리 비교만 한다).</description></item>
    /// </list>
    /// </remarks>
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Bump(eventData.Context);
        return result;
    }

    /// <summary>비동기 SaveChanges 직전에 추가·수정되는 Post의 버전을 정한다.</summary>
    /// <param name="eventData">저장 중인 컨텍스트를 담은 EF 이벤트 데이터.</param>
    /// <param name="result">EF가 진행할 기본 결과(그대로 돌려준다).</param>
    /// <param name="cancellationToken">사용하지 않는다(I/O 없음).</param>
    /// <returns>동기 완료된 <paramref name="result"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> SaveChangesAsync를 호출한 스레드에서 실행된다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 추적 중인 Post 엔트리 열거자 1개. 결과는 동기 완료 <see cref="ValueTask{TResult}"/>라 Task 할당 없음.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(동기 완료). I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Bump(eventData.Context);
        // ValueTask.FromResult: 결과를 구조체에 직접 담아 동기 완료하므로 Task 객체를 힙에 만들지 않는다.
        return ValueTask.FromResult(result);
    }

    private static void Bump(DbContext? context)
    {
        if (context is null) return;
        context.ChangeTracker.DetectChanges();
        foreach (var entry in context.ChangeTracker.Entries<Post>())
        {
            if (entry.State == EntityState.Added) entry.Entity.Version = 1;
            else if (entry.State == EntityState.Modified)
            {
                var version = entry.Property(p => p.Version);
                version.CurrentValue = version.OriginalValue + 1;
            }
        }
    }
}
