using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>테스트 클래스마다 고유한 데이터베이스를 갖는 인메모리 호스트 팩토리.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe한 구성 단계(생성자)와 Thread-safe한 <see cref="WebApplicationFactory{TEntryPoint}"/> 기반 호스트가 혼재한다. 인스턴스를 여러 테스트 클래스가 공유하지 않는다(클래스 픽스처 1개당 1 인스턴스).</description></item>
/// <item><description><b>Memory Allocation:</b> 내부 TestServer·DI 컨테이너를 <see cref="Services"/> 최초 접근 시 구성한다. 클래스마다 새 데이터베이스명 문자열만 추가 할당한다.</description></item>
/// <item><description><b>Blocking:</b> 생성자는 즉시 반환(Non-blocking). 실제 호스트 기동·<c>Migrate()</c>는 <see cref="WebApplicationFactory{TEntryPoint}.CreateClient()"/> 최초 호출 시 지연 실행된다.</description></item>
/// </list>
/// 기본 생성자(xUnit 주입)는 설정 오버라이드 없이 만든다. 다른 설정이 필요한 테스트는
/// <c>new ApiFactory(pg, settings)</c>로 직접 만들고 <c>using</c>으로 해제한다.
/// </remarks>
public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly IReadOnlyDictionary<string, string?> _settings;

    /// <summary>xUnit이 클래스 픽스처로 주입하는 기본 생성자. 설정 오버라이드가 없다.</summary>
    /// <param name="pg">컬렉션이 공유하는 PostgreSQL 컨테이너 fixture.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> xUnit이 클래스 픽스처 생성 시 1회만 호출한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 연결 문자열 빌더가 문자열 1개를 힙에 할당한다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). 네트워크 I/O나 호스트 기동이 없다.</description></item>
    /// </list>
    /// </remarks>
    public ApiFactory(PostgresContainerFixture pg) : this(pg, new Dictionary<string, string?>()) { }

    // xUnit 2.x는 클래스 픽스처에 public 인스턴스 생성자가 정확히 하나여야 한다. 설정 오버라이드용은 internal로 둔다.
    internal ApiFactory(PostgresContainerFixture pg, IReadOnlyDictionary<string, string?> settings)
    {
        // 클래스마다 새 DB 이름을 써서 테스트 간 데이터 간섭을 없앤다. Migrate()가 DB를 생성한다.
        var csb = new NpgsqlConnectionStringBuilder(pg.ConnectionString)
        {
            Database = "blog_test_" + Guid.NewGuid().ToString("N"),
        };
        _connectionString = csb.ToString();
        _settings = settings;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", _connectionString);
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }
    }

    /// <summary>호출자가 소유하는 DI 스코프. <c>await using var scope = factory.CreateScope();</c></summary>
    /// <returns>호출자가 <c>await using</c>으로 해제해야 하는 비동기 서비스 스코프.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe하게 호출할 수 있으나, 반환된 스코프 자체와 그 안의 <c>AppDbContext</c>는 단일 스레드 전용이다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 스코프별 DI 컨테이너 자식 범위 1개를 할당한다. 스코프 종료(<c>DisposeAsync</c>) 시 해제된다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking).</description></item>
    /// </list>
    /// </remarks>
    public AsyncServiceScope CreateScope() => Services.CreateAsyncScope();
}
