using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PortfolioBlog.Api.Infrastructure.Access;

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
    /// <summary>관리 표면 테스트가 쓰는 관리 origin(테스트 전용, RFC 2606 <c>.test</c>).</summary>
    public const string AdminOrigin = "https://admin.test";

    /// <summary>공개 표면 테스트가 쓰는 공개 origin(테스트 전용, RFC 2606 <c>.test</c>).</summary>
    public const string PublicOrigin = "https://blog.test";

    /// <summary><c>Admin:AllowedCidrs</c> 기본값(<c>203.0.113.0/24</c>, RFC 5737 문서용 대역) 안에 속하는 IP.</summary>
    public const string AllowedIp = "203.0.113.9";

    /// <summary><c>Admin:AllowedCidrs</c> 기본값(RFC 5737 문서용 대역 <c>198.51.100.0/24</c>) 밖에 있는 IP.</summary>
    public const string OutsiderIp = "198.51.100.7";

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
        // 개별 테스트의 _settings가 아래에서 덮어쓸 수 있도록 기본값을 루프 앞에 먼저 넣는다.
        builder.UseSetting("Site:PublicOrigin", PublicOrigin);
        builder.UseSetting("Site:AdminOrigin", AdminOrigin);
        builder.UseSetting("Admin:AllowedCidrs", "203.0.113.0/24");
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }
        if (_settings.TryGetValue("Test:Environment", out var env) && env is not null)
        {
            builder.UseEnvironment(env);
        }
        // RemoteIpStartupFilter를 Program.cs 파이프라인보다 앞에 끼워 TestServer의 null RemoteIpAddress를 헤더 값으로 대체한다.
        builder.ConfigureServices(services => services.AddTransient<IStartupFilter, RemoteIpStartupFilter>());
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

    /// <summary>관리 호스트로 가는 클라이언트. 허용 IP·CSRF 헤더·Origin을 기본으로 붙인다. 부재를 검증하는 테스트는 직접 제거한다.
    /// https 주소를 쓰는 이유: 세션 쿠키가 Secure라서 http 주소에서는 CookieContainer가 쿠키를 돌려보내지 않는다.</summary>
    /// <param name="handleCookies">true이면 내부 <see cref="System.Net.Http.CookieContainer"/>가 <c>Set-Cookie</c>를 자동 저장·전송한다. 쿠키 부재/직접 제어를 검증하는 테스트는 false로 끈다.</param>
    /// <returns>호출자가 <c>using</c>으로 해제해야 하는, 관리 origin·허용 IP·CSRF 헤더·Origin이 기본 설정된 <see cref="HttpClient"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 호출마다 새 <see cref="HttpClient"/> 인스턴스를 반환하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="HttpClient"/>와 기본 헤더 문자열 몇 개를 할당한다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). 실제 네트워크 연결은 첫 요청 전송 시점에 지연 수행된다.</description></item>
    /// </list>
    /// </remarks>
    public HttpClient CreateAdminClient(bool handleCookies = true)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(AdminOrigin),
            HandleCookies = handleCookies,
            AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.Add(AdminSurfaceMiddleware.CsrfHeaderName, AdminSurfaceMiddleware.CsrfHeaderValue);
        client.DefaultRequestHeaders.Add("Origin", AdminOrigin);
        client.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, AllowedIp);
        return client;
    }

    /// <summary>공개 호스트로 가는 클라이언트(임의의 외부 방문자).</summary>
    /// <returns>호출자가 <c>using</c>으로 해제해야 하는, 공개 origin·허용되지 않은 IP가 기본 설정된 <see cref="HttpClient"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 호출마다 새 <see cref="HttpClient"/> 인스턴스를 반환하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="HttpClient"/>와 기본 헤더 문자열 1개를 할당한다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). 실제 네트워크 연결은 첫 요청 전송 시점에 지연 수행된다.</description></item>
    /// </list>
    /// </remarks>
    public HttpClient CreatePublicClient()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(PublicOrigin), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, OutsiderIp);
        return client;
    }
}
