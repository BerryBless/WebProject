using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using PortfolioBlog.Api.Domain;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>격리 수준(스펙 D7): 앱의 트랜잭션은 READ COMMITTED로 동작한다. 트랜잭션 안에서 다른 세션의 커밋이 보여야 한다.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>픽스처 공유:</b> 클래스 픽스처 ApiFactory 하나.</description></item>
/// <item><description><b>병렬 실행:</b> 컬렉션 내 직렬.</description></item>
/// <item><description><b>외부 자원:</b> Docker MySQL. 서버 기본값을 REPEATABLE READ로 되돌린 세션에서도 앱 인터셉터가 READ COMMITTED를 보장하는지 본다.</description></item>
/// </list>
/// </remarks>
[Collection("mysql")]
public sealed class IsolationLevelTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>
    /// 서버 설정에 기대지 않는다: 세션 기본값을 REPEATABLE READ로 바꾼 뒤 BeginTransactionAsync()(격리 미지정)를 연다.
    /// 첫 읽기 뒤 다른 세션이 값을 바꿔 커밋하면, READ COMMITTED라면 두 번째 읽기에 새 값이 보인다(REPEATABLE READ면 옛 값).
    /// </summary>
    [Fact]
    public async Task UnspecifiedTransaction_IsReadCommitted_EvenIfSessionDefaultIsRepeatableRead()
    {
        using var _ = factory.CreateClient();
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("SET SESSION transaction_isolation = 'REPEATABLE-READ'");
        await using var tx = await db.Database.BeginTransactionAsync();
        var before = await db.AdminStates.AsNoTracking().Select(a => a.SessionEpoch).SingleAsync();

        await using (var other = new MySqlConnection(factory.ConnectionString))
        {
            await other.OpenAsync();
            await using var bump = new MySqlCommand("UPDATE `AdminState` SET `SessionEpoch` = `SessionEpoch` + 1", other);
            await bump.ExecuteNonQueryAsync();
        }

        var after = await db.AdminStates.AsNoTracking().Select(a => a.SessionEpoch).SingleAsync();
        Assert.Equal(before + 1, after);
        await tx.RollbackAsync();
    }
}
