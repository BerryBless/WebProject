using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using PortfolioBlog.Api.Domain;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>공개 조회 세션 통제(스펙 D2): 읽기 전용, SELECT 실행 상한, 풀 재대여 시 리셋.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>픽스처 공유:</b> "mysql" 컬렉션 컨테이너, 테스트마다 팩토리.</description></item>
/// <item><description><b>병렬 실행:</b> 컬렉션 내 직렬.</description></item>
/// <item><description><b>외부 자원:</b> Docker MySQL.</description></item>
/// </list>
/// </remarks>
[Collection("mysql")]
public sealed class PublicDbContextTests(MySqlContainerFixture mysql)
{
    private static readonly Dictionary<string, string?> FastTimeout = new() { ["Public:StatementTimeoutMs"] = "200" };

    // 스파이크 S3b: SELECT SLEEP(2)는 max_execution_time에 걸려도 오류 없이 0을 반환한다(3024가 나지 않는다). 그래서 행을 실제로 훑는 교차 조인으로 실행 시간 초과를 만든다.
    private const string SlowSelect = "SELECT COUNT(*) AS `Value` FROM information_schema.COLUMNS a, information_schema.COLUMNS b, information_schema.COLUMNS c";

    /// <summary>공개 컨텍스트의 느린 SELECT는 max_execution_time으로 끊기고(3024) 분류기가 QueryTimeout으로 본다.</summary>
    [Fact]
    public async Task SlowSelect_IsCancelledByMaxExecutionTime()
    {
        using var factory = new ApiFactory(mysql, FastTimeout);
        using var _ = factory.CreateClient();
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PublicDbContext>();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.SqlQueryRaw<long>(SlowSelect).ToListAsync());
        Assert.Equal(DbErrorKind.QueryTimeout, DbErrorClassifier.Classify(ex));
        Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2));
    }

    /// <summary>관리 컨텍스트에는 실행 상한이 없다(관리 작업이 공개 설정에 끊기지 않는다).</summary>
    [Fact]
    public async Task AdminContext_IsNotAffected()
    {
        using var factory = new ApiFactory(mysql, FastTimeout);
        using var _ = factory.CreateClient();
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0L, await db.Database.SqlQueryRaw<long>("SELECT CAST(@@SESSION.max_execution_time AS SIGNED) AS `Value`").SingleAsync());
        Assert.Equal(0L, await db.Database.SqlQueryRaw<long>("SELECT CAST(@@SESSION.transaction_read_only AS SIGNED) AS `Value`").SingleAsync());
    }

    /// <summary>세션 겹 단독 증명: 관리 사용자 연결(권한은 충분)에 공개 인터셉터만 붙여도 쓰기가 1792로 막힌다. SaveChanges는 앱 겹이 먼저 막는다.</summary>
    [Fact]
    public async Task SessionLayer_Alone_BlocksWrites()
    {
        using var factory = new ApiFactory(mysql);
        using var _ = factory.CreateClient();
        var options = new DbContextOptionsBuilder<PublicDbContext>()
            .UseMySql(DataServiceCollectionExtensions.WithSessionReset(factory.ConnectionString), DataServiceCollectionExtensions.ServerVersion)
            .AddInterceptors(new PublicSessionInterceptor(3000)).Options;
        await using var db = new PublicDbContext(options);
        var raw = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM `Tags`"));
        Assert.Equal(DbErrorKind.ReadOnly, DbErrorClassifier.Classify(raw));
        var bulk = await Assert.ThrowsAnyAsync<Exception>(() => db.Tags.ExecuteDeleteAsync());
        Assert.Equal(DbErrorKind.ReadOnly, DbErrorClassifier.Classify(bulk));
        db.Tags.Add(new Tag { Name = "x", NormalizedName = "x" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    /// <summary>
    /// 공개 세션이 read_only를 스스로 꺼도 풀 재대여 때 리셋되고 인터셉터가 다시 켠다. 같은 물리 연결(CONNECTION_ID)을 재사용했는지도 확인해야
    /// "리셋"을 측정한 것이 된다. 또 같은 풀을 관리 연결이 빌리면 read_only가 꺼져 있다(Development에서 Default를 공유하는 경우).
    /// </summary>
    [Fact]
    public async Task EscapedReadOnly_IsResetOnReuse_AndNeverLeaksToAdmin()
    {
        using var factory = new ApiFactory(mysql);
        using var _ = factory.CreateClient();
        var single = new MySqlConnectionStringBuilder(DataServiceCollectionExtensions.WithSessionReset(factory.ConnectionString)) { MaximumPoolSize = 1 }.ConnectionString;
        var options = new DbContextOptionsBuilder<PublicDbContext>().UseMySql(single, DataServiceCollectionExtensions.ServerVersion).AddInterceptors(new PublicSessionInterceptor(3000)).Options;
        // Pomelo의 UseMySql은 넘겨받은 연결 문자열에 `Allow User Variables=True;Use Affected Rows=False`를 덧붙여 실제 풀 키로 쓴다.
        // 그래서 원시 MySqlConnection(single)은 이 문자열을 그대로 풀 키로 쓰는 EF 관리 연결과 다른 풀에 들어가(별개의 물리 연결이 되어)
        // "관리 연결이 같은 풀을 빌린다"는 시나리오를 증명하지 못한다(항상 새 연결 기본값 0/0을 보게 되어 리셋 여부와 무관하게 통과해 버린다).
        // 실제 운영에서 관리 연결(AppDbContext)도 같은 UseMySql 경로로 문자열이 변형되어 공개 연결과 같은 풀 키를 쓰므로, 검증도 인터셉터 없는
        // AppDbContext로 같은 UseMySql 경로를 거쳐야 진짜 풀 공유를 재현한다.
        var adminOptions = new DbContextOptionsBuilder<AppDbContext>().UseMySql(single, DataServiceCollectionExtensions.ServerVersion).Options;
        try
        {
            long firstId;
            await using (var db = new PublicDbContext(options))
            {
                await db.Database.OpenConnectionAsync();
                firstId = await db.Database.SqlQueryRaw<long>("SELECT CAST(CONNECTION_ID() AS SIGNED) AS `Value`").SingleAsync();
                await db.Database.ExecuteSqlRawAsync("SET SESSION transaction_read_only = OFF");
                Assert.Equal(0L, await db.Database.SqlQueryRaw<long>("SELECT CAST(@@SESSION.transaction_read_only AS SIGNED) AS `Value`").SingleAsync()); // 기준선: 이스케이프가 통한다
            }
            await using (var db = new PublicDbContext(options))
            {
                await db.Database.OpenConnectionAsync();
                Assert.Equal(firstId, await db.Database.SqlQueryRaw<long>("SELECT CAST(CONNECTION_ID() AS SIGNED) AS `Value`").SingleAsync());
                Assert.Equal(1L, await db.Database.SqlQueryRaw<long>("SELECT CAST(@@SESSION.transaction_read_only AS SIGNED) AS `Value`").SingleAsync());
            }
            await using (var admin = new AppDbContext(adminOptions)) // 인터셉터 없는 관리 측 대여, 같은 UseMySql 경로라 공개 연결과 같은 물리 풀을 공유한다
            {
                await admin.Database.OpenConnectionAsync();
                Assert.Equal(firstId, await admin.Database.SqlQueryRaw<long>("SELECT CAST(CONNECTION_ID() AS SIGNED) AS `Value`").SingleAsync()); // 같은 물리 연결을 재사용했는지까지 확인
                Assert.Equal(0L, await admin.Database.SqlQueryRaw<long>("SELECT CAST(@@SESSION.transaction_read_only AS SIGNED) AS `Value`").SingleAsync());
                Assert.Equal(0L, await admin.Database.SqlQueryRaw<long>("SELECT CAST(@@SESSION.max_execution_time AS SIGNED) AS `Value`").SingleAsync());
            }
        }
        finally
        {
            // 실제 풀 키(Pomelo가 변형한 문자열)로 비워야 유휴 연결이 즉시 닫힌다. 원시 "single" 문자열은 이 테스트에서 아무 풀도 채우지 않는다.
            using var clear = new AppDbContext(adminOptions);
            MySqlConnection.ClearPool((MySqlConnection)clear.Database.GetDbConnection());
        }
    }

    /// <summary>범위 밖 상한 값은 인터셉터 생성에서 거부된다(정수만 SQL에 들어가지만 설정 오류는 드러낸다).</summary>
    [Theory]
    [InlineData(99)]
    [InlineData(60_001)]
    public void Interceptor_RejectsOutOfRangeTimeouts(int ms) => Assert.Throws<ArgumentOutOfRangeException>(() => new PublicSessionInterceptor(ms));
}
