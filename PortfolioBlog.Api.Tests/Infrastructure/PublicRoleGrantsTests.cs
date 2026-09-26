using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>공개 조회 사용자의 권한 경계(스펙 D3): 앱이 기동하며 5개 테이블 SELECT만 주고, SHOW GRANTS로 그 집합을 검증한다.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>픽스처 공유:</b> "mysql" 컬렉션 컨테이너. 테스트마다 팩토리(DB·공개 사용자)를 새로 만든다.</description></item>
/// <item><description><b>병렬 실행:</b> 컬렉션 내 직렬.</description></item>
/// <item><description><b>외부 자원:</b> Docker MySQL.</description></item>
/// </list>
/// </remarks>
[Collection("mysql")]
public sealed class PublicRoleGrantsTests(MySqlContainerFixture mysql)
{
    private static async Task<int> ErrorNumberAsync(string connectionString, string sql)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        try { await command.ExecuteNonQueryAsync(); return 0; }
        catch (MySqlException ex) { return ex.Number; }
    }

    /// <summary>공개 컨텍스트는 실제로 공개 사용자로 접속한다(관리 사용자로 새지 않는다).</summary>
    [Fact]
    public async Task PublicDbContext_ConnectsAsThePublicUser()
    {
        using var factory = new ApiFactory(mysql);
        using var _ = factory.CreateClient();
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PublicDbContext>();
        var current = await db.Database.SqlQueryRaw<string>("SELECT CURRENT_USER() AS `Value`").SingleAsync();
        Assert.Equal($"{factory.PublicUser}@%", current);
    }

    /// <summary>허용 5개 테이블은 읽히고, 세션 read_only를 스스로 꺼도 쓰기는 권한(1142)으로 막힌다. 두 겹 중 권한 겹의 증명이다.</summary>
    [Fact]
    public async Task PublicUser_ReadsAllowedTables_ButCannotWrite_EvenAfterDisablingReadOnly()
    {
        using var factory = new ApiFactory(mysql);
        using var _ = factory.CreateClient();
        foreach (var table in PublicRoleGrants.ReadableTables)
        {
            Assert.Equal(0, await ErrorNumberAsync(factory.PublicConnectionString, $"SELECT COUNT(*) FROM `{table}`"));
        }
        var number = await ErrorNumberAsync(factory.PublicConnectionString, "SET SESSION transaction_read_only = OFF; DELETE FROM `Tags`");
        Assert.Equal(DbErrorKind.PermissionDenied, DbErrorClassifier.KindOf(number));
        Assert.Equal(DbErrorKind.PermissionDenied, DbErrorClassifier.KindOf(await ErrorNumberAsync(factory.PublicConnectionString, "CREATE TABLE smoke_t (i int)")));
    }

    /// <summary>허용 목록 밖 테이블(세션 에포크, 마이그레이션 이력)은 읽을 수 없다.</summary>
    [Theory]
    [InlineData("AdminState")]
    [InlineData("__EFMigrationsHistory")]
    public async Task PublicUser_CannotReadTablesOutsideTheAllowlist(string table)
    {
        using var factory = new ApiFactory(mysql);
        using var _ = factory.CreateClient();
        Assert.Equal(DbErrorKind.PermissionDenied, DbErrorClassifier.KindOf(await ErrorNumberAsync(factory.PublicConnectionString, $"SELECT * FROM `{table}`")));
    }

    /// <summary>누군가 공개 사용자에게 초과 권한을 주면 다음 기동이 실패한다(자동 회수하지 않고 드러낸다, R6). 메시지에 문제 권한이 담긴다.</summary>
    [Fact]
    public void ExtraGrant_MakesStartupFail()
    {
        using var factory = new ApiFactory(mysql);
        using (factory.CreateClient()) { } // 1차 기동: DB·테이블 생성, 정상 권한
        mysql.Execute($"GRANT INSERT ON `{factory.DatabaseName}`.`Posts` TO '{factory.PublicUser}'@'%'");
        using var second = new ApiFactory(mysql, new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = factory.ConnectionString,
            ["ConnectionStrings:Public"] = factory.PublicConnectionString,
        });
        var ex = Assert.ThrowsAny<Exception>(() => second.CreateClient());
        Assert.Contains("GRANT SELECT, INSERT ON", ex.ToString(), StringComparison.Ordinal);
    }

    /// <summary>운영과 같은 권한(root 아님, blog.* 한정 GRANT OPTION)의 관리 사용자로도 적용이 된다. deploy init 스크립트와 같은 권한 문자열을 쓴다.</summary>
    [Fact]
    public void Apply_Works_WithTheDeployPrivileges_NotRoot()
    {
        using var probe = new ApiFactory(mysql);
        mysql.Execute($"CREATE DATABASE `{probe.DatabaseName}`");
        var (appUser, appSecret) = mysql.CreateUser($"GRANT {MySqlContainerFixture.AppPrivileges} ON `{probe.DatabaseName}`.* TO {{user}} WITH GRANT OPTION");
        try
        {
            using var factory = new ApiFactory(mysql, new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = new MySqlConnectionStringBuilder(probe.ConnectionString) { UserID = appUser, Password = appSecret }.ConnectionString,
                ["ConnectionStrings:Public"] = probe.PublicConnectionString,
            });
            using var client = factory.CreateClient(); // Migrate + GRANT + 검증이 예외 없이 끝나야 한다
        }
        finally
        {
            mysql.DropUser(appUser);
        }
    }
}
