using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PortfolioBlog.Api.Domain;
using PortfolioBlog.Api.Infrastructure.Data;
using PortfolioBlog.Api.Infrastructure.Web;

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

    /// <summary>느린 문장은 statement_timeout에서 57014로 끊긴다. 2초짜리 pg_sleep이 1.5초 안에 끝나야 한다(타임아웃이 없으면 2초를 다 기다린다).
    /// 측정 전에 연결을 미리 열어 둔다: 이 팩토리의 공개 연결 풀은 이 테스트가 처음 쓰는 것이라 첫 물리 연결 수립(TCP + 인증 + 시작 매개변수)이
    /// 측정 구간에 섞이면 느린 러너에서 1.5초 상한을 statement_timeout과 무관한 이유로 넘길 수 있다.</summary>
    [Fact]
    public async Task SlowStatement_IsCancelledByStatementTimeout()
    {
        using var factory = new ApiFactory(pg, FastTimeout);
        using var _ = factory.CreateClient();
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PublicDbContext>();
        await db.Database.OpenConnectionAsync();

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
        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
    }

    /// <summary>같은 물리 연결(세션)을 붙잡은 채 직접 <c>SET default_transaction_read_only = off</c>를 실행하면 그 세션 안에서는 쓰기가
    /// 성공하지만, 연결을 닫아 Npgsql 풀에 반납했다가 다시 열면 <c>transaction_read_only</c>가 <c>on</c>으로 되돌아가고 같은 쓰기가 다시
    /// 25006으로 거부된다(측정 결과를 고정하는 테스트). <c>Maximum Pool Size=1</c>로 물리 연결을 하나로 묶어, 재획득한 연결이 반드시
    /// 같은 물리 연결(같은 <c>pg_backend_pid()</c>)임을 같이 확인한다 — 다르면 "리셋됐다"는 단언 자체가 무의미하기 때문이다.
    /// 이 앱은 이런 SQL을 절대 만들지 않으므로 실제 위험 경로는 아니지만(모든 조회는 매개변수화된 LINQ뿐),
    /// <see cref="PublicDbContext"/>의 read-only가 "세션을 완전히 신뢰할 수 없는 제3자가 임의 SQL을 실행하는 상황"까지 막는 계층은
    /// 아니라는 한계와, 그 한계가 "연결이 살아있는 그 세션 안에서만" 유효하다는 억제 범위를 함께 코드로 남긴다.
    /// <b>쓰기 권한이 없는 DB 롤(스펙 §7)을 도입하면</b>: 이 테스트의 "이스케이프 성공" 단언(reset 전)은 실패로 뒤집어야 하지만,
    /// "reset 후 read-only 복귀 + 25006" 단언은 그대로 유효하다(그 롤이면 애초에 reset 전에도 25006이 나므로).</summary>
    [Fact]
    public async Task PublicContext_SessionCanOptOutOfReadOnly_ButResetsWhenConnectionReturnsToPool()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        using var _ = factory.CreateClient(); // 호스트 기동 → Migrate()
        // Maximum Pool Size=1: 이 테스트 전용 풀에 물리 연결이 하나만 존재하도록 강제한다. 그래야 닫았다 다시 여는
        // 두 번째 연결이 반드시 첫 번째와 같은 물리 연결(따라서 같은 pg_backend_pid)을 재사용하고, 아래 pid 비교가 결정적이다.
        var connectionString = new NpgsqlConnectionStringBuilder(PublicDbContext.BuildConnectionString(factory.ConnectionString, new PublicOptions().StatementTimeoutMs)) { MaxPoolSize = 1 }.ConnectionString;
        try
        {
            int pidDuringEscape;
            await using (var conn = new NpgsqlConnection(connectionString))
            {
                await conn.OpenAsync();
                pidDuringEscape = (int)(await ScalarAsync(conn, "SELECT pg_backend_pid()"))!;
                await ExecAsync(conn, "SET default_transaction_read_only = off");
                // 같은 세션 안에서는 이스케이프가 실제로 통한다 — 아래 reset 후 단언과 대비하기 위한 baseline.
                Assert.Null(await Record.ExceptionAsync(() => ExecAsync(conn, "INSERT INTO \"Tags\" (\"Id\", \"Name\", \"NormalizedName\") VALUES (gen_random_uuid(), 'escape-probe-1', 'escape-probe-1')")));
            } // using 종료 → Close() → Npgsql이 기본값(No Reset On Close=false)으로 연결을 풀에 반납하며 세션을 리셋한다.

            int pidAfterReset;
            await using (var conn = new NpgsqlConnection(connectionString))
            {
                await conn.OpenAsync();
                pidAfterReset = (int)(await ScalarAsync(conn, "SELECT pg_backend_pid()"))!;
                Assert.Equal("on", (string)(await ScalarAsync(conn, "SHOW transaction_read_only"))!);
                var ex = await Record.ExceptionAsync(() => ExecAsync(conn, "INSERT INTO \"Tags\" (\"Id\", \"Name\", \"NormalizedName\") VALUES (gen_random_uuid(), 'escape-probe-2', 'escape-probe-2')"));
                var pgEx = Assert.IsType<PostgresException>(ex);
                Assert.Equal("25006", pgEx.SqlState);
            }
            // Maximum Pool Size=1이 실제로 같은 물리 연결을 재사용하게 만들었는지 확인한다 — 이게 성립해야 위 단언이 "리셋"을 측정한 것이지,
            // 우연히 처음부터 read-only였던 별개의 연결을 잡은 게 아님을 보장한다.
            Assert.Equal(pidDuringEscape, pidAfterReset);
        }
        finally
        {
            using var clear = new NpgsqlConnection(connectionString);
            NpgsqlConnection.ClearPool(clear);
        }
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Options가 없는 입력에는 공개 연결 전용 Options(statement_timeout·default_transaction_read_only) 정확히 두 개가 붙고, Database는 그대로 보존된다.</summary>
    [Fact]
    public void BuildConnectionString_NoOptions_SetsExactlyTheTwoStartupOptions()
    {
        var built = new NpgsqlConnectionStringBuilder(PublicDbContext.BuildConnectionString("Host=h;Database=d;Username=u;Password=dummy", 3000));
        Assert.Equal("-c statement_timeout=3000 -c default_transaction_read_only=on", built.Options);
        Assert.Equal("d", built.Database);
    }

    /// <summary>입력에 이미 Options가 있으면 조용히 덮어쓰지 않고 시작 실패로 거부한다(Fix round 1 — 운영자가 넣은 시작 옵션이
    /// 공개 연결에서만 경고 없이 사라지는 것을 막는다). 예외 메시지에 비밀번호 등 연결 문자열 값이 그대로 노출되지 않는지도 확인한다.</summary>
    [Fact]
    public void BuildConnectionString_ExistingOptions_Throws()
    {
        const string dummyPassword = "dummy-super-secret-marker";
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PublicDbContext.BuildConnectionString($"Host=h;Database=d;Username=u;Password={dummyPassword};Options=-c work_mem=1MB", 3000));
        Assert.Contains("ConnectionStrings:Default", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(dummyPassword, ex.Message, StringComparison.Ordinal);
    }
}
