using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using PortfolioBlog.Api.Domain;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>
/// <c>SeriesEndpoints.DeleteAsync</c>의 첫 문장(<c>SELECT 1 FROM `Series` WHERE `Id` = {id} FOR UPDATE</c>)이 Guid 매개변수로도
/// char(36) 행과 같다고 비교되어 실제로 행 잠금을 잡는지 증명한다. 매개변수가 같지 않다고 비교되면 0행에 맞아 잠금 없이 조용히 지나간다.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>픽스처 공유:</b> 클래스 픽스처 ApiFactory 하나(DB 1개). 테스트마다 새 시리즈 slug를 쓴다.</description></item>
/// <item><description><b>병렬 실행:</b> 컬렉션 내 직렬. 테스트 안에서 EF 트랜잭션(잠금 보유)과 원시 연결(다른 세션)을 순차로 교차시킨다. 동시 태스크가 없어 결정적이다.</description></item>
/// <item><description><b>외부 자원:</b> Docker MySQL. 다른 세션은 <c>innodb_lock_wait_timeout=1</c>이라 잠금 대기 1초 뒤 1205로 끝난다.</description></item>
/// </list>
/// </remarks>
[Collection("mysql")]
public sealed class SeriesRowLockTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>
    /// SeriesEndpoints.cs의 문장을 그대로 흉내 낸 FOR UPDATE를 EF 트랜잭션에서 실행하면, 다른 세션의 같은 행 공유 잠금
    /// (글 INSERT의 FK 검사가 부모 행에 거는 것과 같은 S 잠금)이 1205로 막히고, 롤백 뒤에는 같은 문장이 그 행 1개를 돌려준다.
    /// 문장의 id를 다른 Guid로 바꾸면 잠금이 없어 이 테스트가 실패해야 한다(변이 확인).
    /// </summary>
    [Fact]
    public async Task ForUpdate_WithGuidParameter_LocksTheSeriesRow()
    {
        using var _ = factory.CreateClient();
        var series = new Series { Slug = "lock-" + Guid.NewGuid().ToString("N")[..8], Title = "s", Description = "" };
        await using (var seed = factory.CreateScope())
        {
            var seedDb = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            seedDb.Series.Add(series);
            await seedDb.SaveChangesAsync();
        }
        var id = series.Id;

        await using var other = new MySqlConnection(factory.ConnectionString);
        await other.OpenAsync();
        await using (var timeout = new MySqlCommand("SET SESSION innodb_lock_wait_timeout = 1", other))
            await timeout.ExecuteNonQueryAsync();
        // 다른 세션의 조회는 문자열 리터럴 비교로 같은 행을 확실히 가리킨다(검증 대상은 EF 쪽 Guid 매개변수다).
        await using var probe = new MySqlCommand("SELECT COUNT(*) FROM `Series` WHERE `Id` = @id FOR SHARE", other);
        probe.Parameters.AddWithValue("@id", id.ToString());

        await using (var scope = factory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync();
            // SeriesEndpoints.DeleteAsync와 같은 문장 모양(보간 Guid → 매개변수).
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM `Series` WHERE `Id` = {id} FOR UPDATE");

            var blocked = await Assert.ThrowsAsync<MySqlException>(() => probe.ExecuteScalarAsync());
            Assert.Equal(MySqlErrorCode.LockWaitTimeout, blocked.ErrorCode);
            await tx.RollbackAsync();
        }

        Assert.Equal(1L, Convert.ToInt64(await probe.ExecuteScalarAsync()));
    }
}
