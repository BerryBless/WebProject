using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>공개 조회 전용 롤의 권한: 앱이 시작하며 부여한 것이 "허용 테이블의 SELECT"뿐인지 실제 PostgreSQL에서 확인한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 테스트마다 자기 <see cref="ApiFactory"/>(= 자기 DB)를 만든다. 롤은 컨테이너 전역이지만 권한은 DB별이라 서로 간섭하지 않는다.</description></item>
/// <item><description><b>Memory Allocation:</b> 테스트당 호스트 1개와 Npgsql 연결 1~2개.</description></item>
/// <item><description><b>Blocking:</b> 공유 PostgreSQL 컨테이너(<c>postgres</c> 컬렉션)에 대한 실제 I/O.</description></item>
/// </list>
/// </remarks>
[Collection("postgres")]
public sealed class PublicRoleGrantsTests(PostgresContainerFixture pg)
{
    private static string AsPublicRole(ApiFactory factory) =>
        new NpgsqlConnectionStringBuilder(factory.ConnectionString)
        {
            Username = PostgresContainerFixture.PublicRole,
            Password = PostgresContainerFixture.PublicRoleSecret,
            Pooling = false, // 이 테스트의 연결이 팩토리의 풀 정리(ClearPool) 대상 밖에 남지 않게 한다
        }.ToString();

    private static async Task<string?> SqlStateOfAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        try { await command.ExecuteNonQueryAsync(); return null; }
        catch (PostgresException ex) { return ex.SqlState; }
    }

    /// <summary>DI가 주는 공개 컨텍스트는 실제로 공개 롤로 접속한다. 이 단언이 없으면 <c>ConnectionStrings:Public</c>을 읽는 배선이 끊겨도
    /// 다른 테스트가 전부 통과한다(관리 롤은 모든 것을 읽을 수 있으므로).</summary>
    [Fact]
    public async Task PublicDbContext_ConnectsAsThePublicRole()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        using var _ = factory.CreateClient();
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PublicDbContext>();
        var user = await db.Database.SqlQueryRaw<string>("SELECT current_user::text AS \"Value\"").SingleAsync();
        Assert.Equal(PostgresContainerFixture.PublicRole, user);
    }

    /// <summary>공개 롤은 허용 테이블을 읽을 수 있고, 읽기 전용 설정을 스스로 꺼도 쓰지 못한다.
    /// <c>SET default_transaction_read_only = off</c>를 먼저 실행하는 이유: 그 설정은 세션이 끌 수 있는 심층 방어일 뿐이고(스펙 3.7),
    /// 이 테스트가 증명하려는 것은 그것이 꺼진 뒤에도 남는 경계(권한)다.</summary>
    [Fact]
    public async Task PublicRole_CanReadAllowedTables_ButCannotWrite_EvenAfterDisablingReadOnly()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        using var _ = factory.CreateClient(); // 호스트 기동 → Migrate → 권한 부여
        await using var connection = new NpgsqlConnection(AsPublicRole(factory));
        await connection.OpenAsync();

        foreach (var table in PublicRoleGrants.ReadableTables)
        {
            Assert.Null(await SqlStateOfAsync(connection, $"SELECT count(*) FROM \"{table}\""));
        }
        Assert.Null(await SqlStateOfAsync(connection, "SET default_transaction_read_only = off"));
        const string insufficientPrivilege = "42501";
        Assert.Equal(insufficientPrivilege, await SqlStateOfAsync(connection, "DELETE FROM \"Posts\""));
        Assert.Equal(insufficientPrivilege, await SqlStateOfAsync(connection, "INSERT INTO \"Tags\" (\"Id\", \"Name\", \"NormalizedName\") VALUES (gen_random_uuid(), 'probe', 'probe')"));
        Assert.Equal(insufficientPrivilege, await SqlStateOfAsync(connection, "UPDATE \"Attachments\" SET \"FileName\" = 'x'"));
        Assert.Equal(insufficientPrivilege, await SqlStateOfAsync(connection, "TRUNCATE \"PostTags\""));
    }

    /// <summary>허용 목록 밖의 테이블(세션 폐기 카운터, 마이그레이션 이력)은 읽지도 못한다.
    /// 이 단언이 <c>ALTER DEFAULT PRIVILEGES</c>식 일괄 부여로 되돌아가는 것을 막는다.</summary>
    [Theory]
    [InlineData("AdminState")]
    [InlineData("__EFMigrationsHistory")]
    public async Task PublicRole_CannotReadTablesOutsideTheAllowlist(string table)
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        using var _ = factory.CreateClient();
        await using var connection = new NpgsqlConnection(AsPublicRole(factory));
        await connection.OpenAsync();
        Assert.Equal("42501", await SqlStateOfAsync(connection, $"SELECT * FROM \"{table}\""));
    }

    /// <summary>손으로 넓혀 둔 권한은 다음 시작에서 회수된다(소유 테이블 전부 회수 → 허용 목록만 부여). <c>PUBLIC</c> 의사 롤에
    /// 준 권한도 함께 회수되는지 검증한다 — <c>REVOKE … FROM {role}</c>만으로는 공개 롤이 <c>PUBLIC</c>의 일원으로서 그 권한을
    /// 그대로 물려받는다(F1 회귀 테스트).</summary>
    [Fact]
    public async Task Apply_RevokesGrantsOutsideTheAllowlist()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        using var _ = factory.CreateClient();
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using (var owner = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(factory.ConnectionString) { Pooling = false }.ToString()))
        {
            await owner.OpenAsync();
            Assert.Null(await SqlStateOfAsync(owner, $"GRANT SELECT, DELETE ON \"AdminState\" TO {PostgresContainerFixture.PublicRole}"));
            Assert.Null(await SqlStateOfAsync(owner, "GRANT SELECT, UPDATE ON \"AdminState\" TO PUBLIC"));
        }

        PublicRoleGrants.Apply(db, AsPublicRole(factory));

        await using var connection = new NpgsqlConnection(AsPublicRole(factory));
        await connection.OpenAsync();
        Assert.Equal("42501", await SqlStateOfAsync(connection, "SELECT * FROM \"AdminState\""));
        Assert.Equal("42501", await SqlStateOfAsync(connection, "UPDATE \"AdminState\" SET \"SessionEpoch\" = \"SessionEpoch\""));
        Assert.Null(await SqlStateOfAsync(connection, "SELECT count(*) FROM \"Posts\""));
    }

    /// <summary>관리 롤이 소유하지 않은 테이블(예: 점검용 계정이 만든 테이블)이 스키마에 있어도 <see cref="PublicRoleGrants.Apply"/>는
    /// 예외 없이 끝나고, 허용 테이블은 여전히 읽을 수 있다(F2 회귀 테스트). <c>REVOKE</c>를 관리 롤이 소유한 테이블로 좁히지 않으면
    /// (<c>ON ALL TABLES IN SCHEMA public</c>) 비 superuser 관리 롤에서는 이 테이블에 대한 권한 없음(42501)으로 트랜잭션 전체가
    /// 실패해 앱이 기동하지 못한다(운영의 <c>blog_app</c>은 superuser가 아니다).</summary>
    [Fact]
    public async Task Apply_SucceedsWithAllowedTableSelect_EvenWhenAForeignOwnedTableExists()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        using var _ = factory.CreateClient(); // 호스트 기동 → Migrate → 첫 Apply
        await using (var owner = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(factory.ConnectionString) { Pooling = false }.ToString()))
        {
            await owner.OpenAsync();
            Assert.Null(await SqlStateOfAsync(owner, "CREATE TABLE \"OtherOwned\" (i int)"));
            Assert.Null(await SqlStateOfAsync(owner, $"ALTER TABLE \"OtherOwned\" OWNER TO {PostgresContainerFixture.PublicRole}"));
        }

        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        PublicRoleGrants.Apply(db, AsPublicRole(factory)); // 예외 없이 끝나야 한다

        await using var connection = new NpgsqlConnection(AsPublicRole(factory));
        await connection.OpenAsync();
        Assert.Null(await SqlStateOfAsync(connection, "SELECT count(*) FROM \"Posts\""));
    }

    /// <summary>문장은 소유 테이블마다 <c>PUBLIC</c>·롤 권한을 먼저 회수한 뒤 스키마 사용을 주고, 소유 목록에 있는 허용 테이블에만
    /// <c>SELECT</c>를 준다. 소유 목록에만 있고 허용 목록엔 없는 테이블(<c>AdminState</c>)은 회수 대상일 뿐 부여 대상이 아니다.</summary>
    [Fact]
    public void BuildStatements_RevokesFirst_ThenGrantsSelectPerAllowedTable()
    {
        var owned = PublicRoleGrants.ReadableTables.Append("AdminState").ToArray();
        var statements = PublicRoleGrants.BuildStatements("blog_public", owned);

        var expectedRevokes = owned.SelectMany(t => new[]
        {
            $"REVOKE ALL ON \"{t}\" FROM PUBLIC",
            $"REVOKE ALL ON \"{t}\" FROM blog_public",
        });
        Assert.Equal(expectedRevokes, statements.Take(owned.Length * 2));
        Assert.Equal("GRANT USAGE ON SCHEMA public TO blog_public", statements[owned.Length * 2]);
        Assert.Equal(PublicRoleGrants.ReadableTables.Select(t => $"GRANT SELECT ON \"{t}\" TO blog_public"), statements.Skip(owned.Length * 2 + 1));
        Assert.DoesNotContain(statements, s => s.Contains("GRANT SELECT ON \"AdminState\"", StringComparison.Ordinal));
    }

    /// <summary>허용 테이블이 아직 소유 목록에 없으면(마이그레이션 전) 그 테이블에는 <c>SELECT</c>를 부여하지 않는다 — 존재하지 않는
    /// 테이블에 대한 <c>GRANT</c>는 42P01로 기동을 막기 때문이다.</summary>
    [Fact]
    public void BuildStatements_SkipsGrantForAllowedTableNotYetOwned()
    {
        var statements = PublicRoleGrants.BuildStatements("blog_public", ["Posts"]);
        Assert.DoesNotContain(statements, s => s.Contains("GRANT SELECT ON \"Series\"", StringComparison.Ordinal));
        Assert.Contains("GRANT SELECT ON \"Posts\" TO blog_public", statements);
    }

    /// <summary>롤 이름은 SQL 매개변수가 될 수 없어 문장에 직접 들어간다 — 따옴표·공백·대문자·세미콜론이 든 이름은 DB에 닿기 전에 거부한다.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("Blog_Public")]
    [InlineData("blog public")]
    [InlineData("blog_public; DROP TABLE \"Posts\"")]
    [InlineData("blog\"public")]
    [InlineData("1blog")]
    public void RoleOf_RejectsNamesThatAreNotPlainLowercaseIdentifiers(string username)
    {
        var connectionString = new NpgsqlConnectionStringBuilder { Host = "db.example", Database = "blog", Username = username }.ToString();
        Assert.Throws<InvalidOperationException>(() => PublicRoleGrants.RoleOf(connectionString));
        Assert.Throws<ArgumentException>(() => PublicRoleGrants.BuildStatements(username, []));
    }
}
