using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using PortfolioBlog.Api.Infrastructure.Access;
using PortfolioBlog.Api.Infrastructure.Data;
using PortfolioBlog.Api.Infrastructure.Web;

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

    /// <summary>테스트 전용 더미 관리자 비밀번호(실제 비밀번호 아님).</summary>
    public const string Password = "dummy-test-password-0920"; // 테스트 전용 더미 값(실제 비밀번호 아님)

    // PBKDF2 10만 회라 해시 생성이 수십 ms 걸린다. 프로세스당 한 번만 만든다.
    private static readonly string PasswordHash = AdminCredential.Hash(Password);

    private readonly string _connectionString;
    private readonly IReadOnlyDictionary<string, string?> _settings;
    private readonly string? _attachmentsRootPathOverride;

    /// <summary>이 팩토리가 만든 테스트 전용 DB를 가리키는 관리 연결 문자열(Task 8의 잠금 테스트가 쓴다).</summary>
    internal string ConnectionString => _connectionString;

    /// <summary>테스트가 앞으로 돌릴 수 있는 시계. <c>TimeProvider</c> 싱글턴으로 등록되어 앱이 이 인스턴스를 통해 "지금"을 읽는다.</summary>
    public MutableTimeProvider Clock { get; } = new();

    /// <summary>이 팩토리 인스턴스 전용 첨부 저장 루트(임시 디렉터리 밑, 인스턴스마다 고유). 팩토리가 해제되면 재귀적으로 지워진다.</summary>
    public string AttachmentsRoot { get; } = Path.Combine(Path.GetTempPath(), "portfolioblog-tests", Guid.NewGuid().ToString("N"));

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
    // attachmentsRootTrailingSeparator: 지정하면 AttachmentsRoot 뒤에 이 구분자 하나를 붙인 값을 Attachments:RootPath로
    // 앱에 넘긴다(트레일링 구분자가 있는 운영 설정값 재현용, F1 회귀 테스트 전용 훅). AttachmentsRoot 프로퍼티 자체(정리용 경로)는
    // 구분자 없이 그대로 둔다 — 같은 파일 시스템 위치를 가리키므로 정리에는 영향이 없다.
    internal ApiFactory(PostgresContainerFixture pg, IReadOnlyDictionary<string, string?> settings, char? attachmentsRootTrailingSeparator = null)
    {
        // 클래스마다 새 DB 이름을 써서 테스트 간 데이터 간섭을 없앤다. Migrate()가 DB를 생성한다.
        var csb = new NpgsqlConnectionStringBuilder(pg.ConnectionString)
        {
            Database = "blog_test_" + Guid.NewGuid().ToString("N"),
        };
        _connectionString = csb.ToString();
        _settings = settings;
        _attachmentsRootPathOverride = attachmentsRootTrailingSeparator is { } separator ? AttachmentsRoot + separator : null;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", _connectionString);
        // 개별 테스트의 _settings가 아래에서 덮어쓸 수 있도록 기본값을 루프 앞에 먼저 넣는다.
        builder.UseSetting("Site:PublicOrigin", PublicOrigin);
        builder.UseSetting("Site:AdminOrigin", AdminOrigin);
        builder.UseSetting("Admin:AllowedCidrs", "203.0.113.0/24");
        builder.UseSetting("Admin:PasswordHash", PasswordHash);
        // 로그인 테스트가 서로의 한도를 소진하지 않도록 기본 한도를 크게 둔다. 속도 제한 테스트만 작은 값으로 덮어쓴다.
        builder.UseSetting("Admin:LoginPerIpPerMinute", "1000");
        builder.UseSetting("Admin:LoginGlobalPerMinute", "1000");
        builder.UseSetting("Admin:LoginConcurrency", "64");
        builder.UseSetting("Admin:PreviewPerMinute", "1000");
        builder.UseSetting("Admin:PreviewConcurrency", "64");
        builder.UseSetting("Admin:UploadPerMinute", "100000");
        builder.UseSetting("Admin:UploadConcurrency", "64");
        builder.UseSetting("Public:PagePerIpPerMinute", "100000");
        builder.UseSetting("Public:AssetPerIpPerMinute", "100000");
        builder.UseSetting("Public:SearchPerIpPerMinute", "100000");
        builder.UseSetting("Public:SearchConcurrency", "64");
        builder.UseSetting("Attachments:RootPath", _attachmentsRootPathOverride ?? AttachmentsRoot);
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }
        if (_settings.TryGetValue("Test:Environment", out var env) && env is not null)
        {
            builder.UseEnvironment(env);
        }
        // RemoteIpStartupFilter를 Program.cs 파이프라인보다 앞에 끼워 TestServer의 null RemoteIpAddress를 헤더 값으로 대체한다.
        builder.ConfigureServices(services =>
        {
            services.AddTransient<IStartupFilter, RemoteIpStartupFilter>();
            // TimeProvider.System 기본 등록을 걷어내고 테스트가 진행시킬 수 있는 시계로 바꾼다. 쿠키 인증의 IssuedUtc·만료 판정이 이 시계를 따른다.
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
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

    /// <summary>로그인한 관리 클라이언트(쿠키 컨테이너가 세션 쿠키를 들고 있다).</summary>
    /// <returns>호출자가 <c>using</c>으로 해제해야 하는, 로그인 세션 쿠키를 쿠키 컨테이너에 담은 <see cref="HttpClient"/>.</returns>
    /// <exception cref="InvalidOperationException">로그인 요청이 204가 아닌 상태 코드를 반환했을 때.</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 호출마다 새 <see cref="HttpClient"/>를 만들어 로그인하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="HttpClient"/> 1개와 로그인 요청·응답 버퍼.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 로그인 HTTP 왕복을 <c>await</c>로 대기한다(서버 측 PBKDF2 검증 포함 수십 ms).</description></item>
    /// </list>
    /// </remarks>
    public async Task<HttpClient> CreateLoggedInClientAsync()
    {
        var client = CreateAdminClient();
        using var res = await client.PostAsJsonAsync("/api/auth/login", new { password = Password });
        if (res.StatusCode != HttpStatusCode.NoContent)
        {
            throw new InvalidOperationException($"테스트 로그인 실패: {(int)res.StatusCode}");
        }
        return client;
    }

    /// <summary>로그인하고 <c>name=value</c> 형태의 쿠키 헤더 값을 돌려준다. "복사해 둔 쿠키 재사용" 시나리오용.</summary>
    /// <returns><c>Cookie</c> 요청 헤더에 그대로 실을 수 있는 <c>name=value</c> 문자열.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 호출마다 쿠키 자동 처리를 끈 새 <see cref="HttpClient"/>를 만들어 로그인하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="HttpClient"/> 1개와 <c>Set-Cookie</c> 헤더 파싱 결과 문자열.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 로그인 HTTP 왕복을 <c>await</c>로 대기한다.</description></item>
    /// </list>
    /// </remarks>
    public async Task<string> LoginAndGetCookieAsync()
    {
        using var client = CreateAdminClient(handleCookies: false);
        using var res = await client.PostAsJsonAsync("/api/auth/login", new { password = Password });
        var setCookie = res.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(AuthServiceCollectionExtensions.CookieName + "=", StringComparison.Ordinal));
        return setCookie.Split(';', 2)[0];
    }

    /// <summary>기반 <see cref="WebApplicationFactory{TEntryPoint}"/>가 호스트를 해제한 뒤, 관리·공개 두 연결 풀을 닫고 이 인스턴스 전용 첨부 임시 폴더를 재귀적으로 지운다.</summary>
    /// <param name="disposing"><see langword="true"/>면 관리 리소스(호스트·연결 풀·임시 폴더)까지 해제한다.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> xUnit이 픽스처 해제 시 1회만 호출한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 공개 연결 문자열을 다시 조립하는 문자열 1개(<see cref="PublicDbContext.BuildConnectionString"/>) + 반복용 배열 1개.</description></item>
    /// <item><description><b>Blocking:</b> 동기 파일 시스템 I/O(디렉터리 재귀 삭제). <c>base.Dispose</c>가 먼저 호스트를 내려 파일 핸들을 놓아야 삭제가 실패하지 않으므로 반드시 그 다음에 호출한다.</description></item>
    /// </list>
    /// </remarks>
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        // NpgsqlConnection.ClearPool: 풀은 연결 문자열별 프로세스 전역 상태라 호스트를 내려도 유휴 연결이 Connection Idle Lifetime(기본 300초) 동안
        // 서버에 남는다. 이 팩토리가 연 두 풀(관리·공개)을 즉시 닫아 공유 컨테이너의 max_connections를 다른 테스트에 돌려준다.
        // 공개 조회 풀도 닫는다(연결 문자열이 달라 풀이 따로다). 시간 제한 값이 연결 문자열의 일부라 앱과 같은 값으로 조립해야 같은 풀을 가리킨다.
        // int.TryParse: Dispose 안에서 예외를 던지면 바로 아래 첨부 임시 폴더 정리가 건너뛰어지므로, 파싱 실패를 예외 대신
        // PublicOptions 기본값으로 흡수한다(정리 자체는 최선 노력이고, 여기서 죽을 이유가 없다).
        var timeout = _settings.TryGetValue("Public:StatementTimeoutMs", out var raw) && raw is not null
            && int.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : new PublicOptions().StatementTimeoutMs;
        foreach (var cs in new[] { _connectionString, PublicDbContext.BuildConnectionString(_connectionString, timeout) })
        {
            using var connection = new NpgsqlConnection(cs);
            NpgsqlConnection.ClearPool(connection);
        }
        if (Directory.Exists(AttachmentsRoot)) Directory.Delete(AttachmentsRoot, recursive: true);
    }
}
