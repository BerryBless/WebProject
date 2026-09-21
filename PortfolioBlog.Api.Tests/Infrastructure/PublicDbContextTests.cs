using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PortfolioBlog.Api.Domain;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>공개 조회 연결이 DB 수준에서 시간 제한·읽기 전용인지 실제 PostgreSQL로 검증한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 실행된다. 컬렉션 픽스처 <see cref="PostgresContainerFixture"/>(컨테이너 자체)만 공유하고,
/// 각 테스트 케이스는 자신만의 <see cref="ApiFactory"/>(따라서 자신만의 DB·연결 문자열)를 직접 만들어 <c>using</c>으로 해제한다.</description></item>
/// <item><description><b>Memory Policy:</b> 케이스마다 <see cref="ApiFactory"/>·DI 스코프·<see cref="PublicDbContext"/> 또는 <see cref="AppDbContext"/> 각 1개.</description></item>
/// <item><description><b>Concurrency:</b> 케이스 간 공유 가변 상태가 없으므로(DB가 케이스마다 다름) 병렬 실행에 안전하다.</description></item>
/// <item><description><b>Blocking:</b> 비동기 Non-blocking. 모든 DB 왕복을 <c>await</c>한다.</description></item>
/// </list>
/// </remarks>
[Collection("postgres")]
public sealed class PublicDbContextTests(PostgresContainerFixture pg)
{
    private static readonly Dictionary<string, string?> FastTimeout = new() { ["Public:StatementTimeoutMs"] = "200" };

    /// <summary>느린 문장은 statement_timeout에서 57014로 끊긴다. 2초짜리 pg_sleep이 1.5초 안에 끝나야 한다(타임아웃이 없으면 2초를 다 기다린다).</summary>
    [Fact]
    public async Task SlowStatement_IsCancelledByStatementTimeout()
    {
        using var factory = new ApiFactory(pg, FastTimeout);
        using var _ = factory.CreateClient();
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PublicDbContext>();

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("SELECT pg_sleep(2)"));
        Assert.Equal("57014", ex.SqlState);
        Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(1.5));
    }

    /// <summary>같은 앱의 관리 컨텍스트는 제한을 받지 않는다(설정이 관리 연결로 새지 않았다).</summary>
    [Fact]
    public async Task AdminContext_IsNotAffected()
    {
        using var factory = new ApiFactory(pg, FastTimeout);
        using var _ = factory.CreateClient();
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // 200ms 제한을 넘는 0.5초 sleep이 예외 없이 끝나야 한다(관리 연결에는 statement_timeout이 없다).
        Assert.Null(await Record.ExceptionAsync(() => db.Database.ExecuteSqlRawAsync("SELECT pg_sleep(0.5)")));
    }

    /// <summary>공개 연결로는 어떤 쓰기도 할 수 없다: SQL은 25006, SaveChanges는 앱에서 막는다.</summary>
    [Fact]
    public async Task PublicContext_CannotWrite()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        using var _ = factory.CreateClient();
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PublicDbContext>();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM \"Tags\""));
        Assert.Equal("25006", ex.SqlState);
        var viaEf = await Assert.ThrowsAsync<PostgresException>(() => db.Tags.ExecuteDeleteAsync());
        Assert.Equal("25006", viaEf.SqlState);

        db.Tags.Add(new Tag { Name = "x", NormalizedName = "x" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    /// <summary>같은 물리 연결(세션)을 붙잡은 채 직접 <c>SET default_transaction_read_only = off</c>를 실행하면 PostgreSQL은 이를 허용하고
    /// 그 뒤의 쓰기가 성공한다(측정 결과를 고정하는 테스트 — 연결을 명시적으로 열어 두지 않으면 EF가 호출마다 커넥션 풀에서 다른 물리 연결을
    /// 빌려 오므로 이 이스케이프가 재현되지 않는다는 것도 같이 측정했다). 이 앱은 그런 SQL을 절대 만들지 않으므로 실제 위험 경로는 아니지만,
    /// <see cref="PublicDbContext"/>의 read-only가 "세션을 완전히 신뢰할 수 없는 제3자가 임의 SQL을 실행하는 상황"까지 막는 계층은
    /// 아니라는 한계를 코드로 남긴다. 진짜 방어는 쓰기 권한이 없는 DB 롤(스펙 §7 확장 지점)이다.</summary>
    [Fact]
    public async Task PublicContext_SessionCanOptOutOfReadOnly_DocumentedLimit()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        using var _ = factory.CreateClient();
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PublicDbContext>();

        // EF는 명시적으로 연결을 열어 두지 않는 한 ExecuteSqlRawAsync 호출마다 커넥션을 풀에서 빌리고 돌려준다 — 같은 물리 연결(세션)에서
        // SET → INSERT가 이어지는지 보장하려면 이 스코프가 끝날 때까지 연결을 직접 열어 붙잡아야 한다(그래야 세션 GUC 변경이 유지된다).
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await db.Database.ExecuteSqlRawAsync("SET default_transaction_read_only = off");
            Assert.Null(await Record.ExceptionAsync(() => db.Database.ExecuteSqlRawAsync("INSERT INTO \"Tags\" (\"Id\", \"Name\", \"NormalizedName\") VALUES (gen_random_uuid(), 'escape-probe', 'escape-probe')")));
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    /// <summary>연결 문자열에 원래 있던 Options는 버리지 않고 보존하지도 않는다 — 공개 연결의 Options는 항상 이 두 설정이다(조용한 덮어쓰기를 문서화하는 테스트).</summary>
    [Fact]
    public void BuildConnectionString_SetsExactlyTheTwoStartupOptions()
    {
        var built = new NpgsqlConnectionStringBuilder(PublicDbContext.BuildConnectionString("Host=h;Database=d;Username=u;Password=dummy;Options=-c work_mem=1MB", 3000));
        Assert.Equal("-c statement_timeout=3000 -c default_transaction_read_only=on", built.Options);
        Assert.Equal("d", built.Database);
    }
}
