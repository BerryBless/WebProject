using MySqlConnector;
using Testcontainers.MySql;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>테스트 전체가 공유하는 MySQL 8.4 컨테이너와 사용자 관리 도우미.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>픽스처 공유:</b> "mysql" 컬렉션의 모든 클래스가 한 컨테이너를 공유한다. DB는 팩토리마다 새로 만든다(ApiFactory).</description></item>
/// <item><description><b>병렬 실행:</b> 같은 컬렉션이라 클래스 간 직렬. 사용자 이름은 무작위라 충돌하지 않는다.</description></item>
/// <item><description><b>외부 자원:</b> Docker. 서버 인자는 운영(compose)과 같다(Global Constraints). 단 require_secure_transport는 켜지 않는다(부정 테스트가 필요할 때 개별로 확인).</description></item>
/// </list>
/// </remarks>
public sealed class MySqlContainerFixture : IAsyncLifetime
{
    /// <summary>테스트 전용 더미 비밀번호(실제 비밀번호 아님).</summary>
    public const string RootSecret = "dummy-root-0926";

    /// <summary>테스트 전용 더미 비밀번호(실제 비밀번호 아님).</summary>
    public const string UserSecret = "dummy-user-0926";

    /// <summary>관리 사용자 권한. <c>deploy/mysql-init/10-users.sh</c>와 글자 그대로 같아야 한다(<c>DeployInitScriptTests</c>가 대조).</summary>
    public const string AppPrivileges = "SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, DROP, INDEX, REFERENCES";

    // MySqlContainer: Docker API로 컨테이너를 띄우고 컨테이너 안에서 실제 쿼리가 성공할 때까지 기다리는 대기 전략이라 sleep 폴링이 필요 없다.
    // max-connections=500: 팩토리마다 DB가 달라 연결 문자열(=풀)이 팩토리 수만큼 생긴다. 팩토리 Dispose가 풀을 비우지만 병렬 시점 여유를 둔다.
    private readonly MySqlContainer _container = new MySqlBuilder("mysql:8.4")
        .WithUsername("root")
        .WithPassword(RootSecret)
        .WithCommand("--transaction-isolation=READ-COMMITTED", "--character-set-server=utf8mb4", "--collation-server=utf8mb4_0900_ai_ci",
            "--local-infile=0", "--innodb-lock-wait-timeout=10", "--max-connections=500")
        .Build();

    /// <summary>root 연결 문자열(TLS 필수). DB 이름은 비어 있지 않다(Testcontainers 기본 DB). 팩토리가 Database를 바꿔 쓴다.</summary>
    public string ConnectionString => new MySqlConnectionStringBuilder(_container.GetConnectionString()) { SslMode = MySqlSslMode.Required }.ConnectionString;

    /// <inheritdoc />
    public Task InitializeAsync() => _container.StartAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>root로 SQL을 실행한다(동기 — 팩토리 생성자·Dispose에서 쓴다).</summary>
    /// <param name="sql">실행할 SQL. 테스트 코드가 만든 상수·무작위 식별자만 넣는다.</param>
    public void Execute(string sql)
    {
        using var connection = new MySqlConnection(ConnectionString);
        connection.Open();
        using var command = new MySqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    /// <summary>무작위 이름의 TLS 필수 사용자를 만든다. 공개 사용자는 팩토리마다 따로 만든다: 한 사용자가 여러 DB 권한을 가지면 SHOW GRANTS 엄격 검증이 깨진다.</summary>
    /// <param name="grantSql">만든 뒤 실행할 GRANT 문(사용자 자리는 <c>{user}</c>). null이면 권한 없음(USAGE).</param>
    /// <returns>사용자 이름과 비밀번호.</returns>
    public (string User, string Password) CreateUser(string? grantSql = null)
    {
        var user = "u_" + Guid.NewGuid().ToString("N")[..20]; // 22자 ≤ 32
        Execute($"CREATE USER '{user}'@'%' IDENTIFIED BY '{UserSecret}' REQUIRE SSL");
        if (grantSql is not null) Execute(grantSql.Replace("{user}", $"'{user}'@'%'", StringComparison.Ordinal));
        return (user, UserSecret);
    }

    /// <summary>사용자를 지운다(없으면 무시).</summary>
    /// <param name="user">사용자 이름.</param>
    public void DropUser(string user) => Execute($"DROP USER IF EXISTS '{user}'@'%'");
}

/// <summary>MySQL 컨테이너를 공유하는 테스트 컬렉션.</summary>
[CollectionDefinition("mysql")]
public sealed class MySqlCollection : ICollectionFixture<MySqlContainerFixture>;
