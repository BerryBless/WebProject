using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace PortfolioBlog.Api.Infrastructure.Access;

/// <summary>쿠키 인증·인가 정책·로그인 속도 제한·Data Protection을 DI 컨테이너에 등록하는 확장 메서드 모음.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 정적 클래스이며 인스턴스 상태가 없다. 등록 메서드는 앱 시작 시 단일 스레드에서 호출된다.</description></item>
/// <item><description><b>Memory Allocation:</b> 시작 시 서비스 디스크립터·옵션 구성 델리게이트 등록에 따른 1회성 할당만 발생한다.</description></item>
/// <item><description><b>Blocking:</b> 동기 실행. I/O 없음.</description></item>
/// </list>
/// </remarks>
public static class AuthServiceCollectionExtensions
{
    /// <summary>관리자 쿠키 인증 스킴 이름.</summary>
    public const string Scheme = "AdminCookie";

    /// <summary>세션 쿠키 이름. <c>__Host-</c> 접두사가 브라우저에 Secure·Path=/·Domain 미지정을 강제한다.</summary>
    public const string CookieName = "__Host-AdminSession";

    /// <summary><c>/api</c> 그룹이 요구하는 인가 정책 이름.</summary>
    public const string PolicyName = "Admin";

    /// <summary>Data Protection 키를 파일로 영속화할 경로를 담는 설정 키. 비어 있으면(개발·테스트) 프레임워크 기본 위치를 쓴다.</summary>
    public const string DataProtectionKeysPathKey = "DataProtection:KeysPath";

    /// <summary>쿠키 인증·인가 정책·로그인 속도 제한·Data Protection을 등록한다. 설정 값은 전부 지연 바인딩이다.</summary>
    /// <param name="services">등록 대상 서비스 컬렉션.</param>
    /// <returns>체이닝을 위해 그대로 반환하는 <paramref name="services"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 앱 시작 시 단일 스레드에서 1회 호출된다. 등록되는 <see cref="AdminCredential"/>·쿠키 인증 핸들러·속도 제한기는 싱글턴이며 내부적으로 Thread-safe하게 구현되어 있다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 옵션 바인딩·서비스 디스크립터 등록에 따른 시작 시 1회성 할당만 발생한다. 실제 설정 값 해석(<see cref="AdminOptions"/> 바인딩)은 여기서 일어나지 않고 첫 사용 시점으로 지연된다.</description></item>
    /// <item><description><b>Blocking:</b> 동기 실행. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public static IServiceCollection AddAdminAuth(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<AdminCredential>();

        services.AddAuthentication(Scheme).AddCookie(Scheme);
        services.AddOptions<CookieAuthenticationOptions>(Scheme).Configure<TimeProvider, IOptions<AdminOptions>>((o, clock, admin) =>
        {
            o.TimeProvider = clock;
            // __Host- 접두사: 브라우저가 Secure + Path=/ + Domain 미지정을 강제한다 → 형제 서브도메인(공개 도메인)이 이 쿠키를 덮어쓰거나 받을 수 없다.
            o.Cookie.Name = CookieName;
            o.Cookie.Path = "/";
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.IsEssential = true;
            o.ExpireTimeSpan = TimeSpan.FromHours(admin.Value.SessionHours);
            o.SlidingExpiration = false;
            // API는 로그인 HTML로 리다이렉트하지 않는다.
            o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
            o.Events.OnValidatePrincipal = SessionValidator.ValidateAsync;
        });
        services.AddAuthorizationBuilder().AddPolicy(PolicyName, p => p.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser());

        // 키 경로가 설정된 경우(운영)에만 파일에 영속화한다. 없으면 프레임워크 기본 위치를 쓴다(개발·테스트).
        services.AddDataProtection().SetApplicationName("PortfolioBlog.Api");
        services.AddOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>().Configure<IConfiguration>((o, cfg) =>
        {
            var path = cfg[DataProtectionKeysPathKey];
            if (!string.IsNullOrWhiteSpace(path))
            {
                o.XmlRepository = new Microsoft.AspNetCore.DataProtection.Repositories.FileSystemXmlRepository(
                    new DirectoryInfo(path), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
            }
        });

        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<AdminOptions>>((o, adminOptions) =>
        {
            var admin = adminOptions.Value;
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // 체인: 세 제한기를 모두 통과해야 한다. 로그인이 아닌 요청은 NoLimiter 파티션으로 빠진다. Plan 2가 공개 검색·미리보기 제한기를 이 체인에 추가한다.
            // CreateChained는 앞 제한기부터 순서대로 permit을 대여한다 — 뒤(global·concurrency)가 거부해도 앞(per-IP)이 이미 대여한 permit은 반환되지 않는다.
            // 즉 전역/동시성 한도로 튕긴 요청도 그 IP의 분당 카운터는 그대로 1 소모한다(고정 창 제한기라 명시적 반환 API가 없다). 의도된 보수적 동작이다: 한도 초과 시도를 "공짜"로 만들지 않는다.
            o.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                // FixedWindow: 창마다 카운터 하나만 두는 O(1) 제한기. 허용 IP 수가 적어 파티션 수도 작다.
                PartitionedRateLimiter.Create<HttpContext, string>(ctx => IsLogin(ctx)
                    ? RateLimitPartition.GetFixedWindowLimiter("login-ip:" + NormalizedIp(ctx.Connection.RemoteIpAddress), _ => Window(admin.LoginPerIpPerMinute))
                    : RateLimitPartition.GetNoLimiter("none")),
                PartitionedRateLimiter.Create<HttpContext, string>(ctx => IsLogin(ctx)
                    ? RateLimitPartition.GetFixedWindowLimiter("login-global", _ => Window(admin.LoginGlobalPerMinute))
                    : RateLimitPartition.GetNoLimiter("none")),
                // Concurrency: PBKDF2 검증은 CPU 바운드라 동시에 도는 수를 묶는다. 임대는 요청이 끝날 때 미들웨어가 반납한다. 대기열 0 = 초과분 즉시 429.
                PartitionedRateLimiter.Create<HttpContext, string>(ctx => IsLogin(ctx)
                    ? RateLimitPartition.GetConcurrencyLimiter("login-concurrency", _ => new ConcurrencyLimiterOptions { PermitLimit = admin.LoginConcurrency, QueueLimit = 0 })
                    : RateLimitPartition.GetNoLimiter("none")));
        });
        return services;
    }

    /// <summary>요청이 로그인 엔드포인트(<c>POST /api/auth/login</c>)로 라우팅되었는지 판정한다. 속도 제한 파티션 선택의 기준이다.</summary>
    /// <param name="ctx">현재 HTTP 요청 컨텍스트.</param>
    /// <returns>로그인 엔드포인트로 라우팅된 요청이면 <c>true</c>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 정적 메서드로 공유 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation. <see cref="Endpoint.Metadata"/> 조회는 기존 컬렉션을 순회할 뿐 새로 할당하지 않는다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
    /// </list>
    /// 요청 경로 문자열을 직접 비교하지 않는 이유: 라우팅은 끝 슬래시·대소문자를 정규화해 <c>/login</c>·<c>/login/</c>·<c>/LOGIN</c>을 전부 같은 엔드포인트로 매칭하지만
    /// 문자열 비교는 그중 정확한 표기만 통과시켜 나머지 변형이 속도 제한을 우회하게 만든다(끝 슬래시 변형 실증됨). <c>WebApplication</c>은 라우팅(엔드포인트 선택)이
    /// <see cref="RateLimiterApplicationBuilderExtensions.UseRateLimiter"/>보다 먼저 실행되므로 이 시점에 <see cref="Endpoint"/>가 이미 확정되어 있고,
    /// 실제로 어떤 핸들러가 실행될지와 정확히 일치하는 판정 기준이 된다.
    /// </remarks>
    private static bool IsLogin(HttpContext ctx) =>
        HttpMethods.IsPost(ctx.Request.Method) && ctx.GetEndpoint()?.Metadata.GetMetadata<LoginRateLimitMetadata>() is not null;

    /// <summary>IPv4-mapped IPv6 주소(<c>::ffff:a.b.c.d</c>)를 IPv4 표기로 접어 IP별 로그인 속도 제한 파티션 키를 만든다.</summary>
    /// <param name="ip">정규화할 원본 IP 주소. <c>null</c>이면 빈 문자열을 반환한다.</param>
    /// <returns>IPv4-mapped IPv6는 IPv4 표기 문자열로, 그 외에는 원본 <see cref="IPAddress.ToString"/> 값. <paramref name="ip"/>가 <c>null</c>이면 빈 문자열.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 정적 메서드로 공유 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> IPv4-mapped 입력은 <see cref="IPAddress.MapToIPv4"/> 호출로 <see cref="IPAddress"/> 1개를 추가 할당한 뒤 결과 문자열 1개를 만든다. 그 외에는 <see cref="IPAddress.ToString"/> 문자열 1개.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
    /// </list>
    /// 정규화 없이 같은 클라이언트가 듀얼스택 소켓 때문에 <c>a.b.c.d</c>와 <c>::ffff:a.b.c.d</c> 두 표기로 요청을 섞으면(<see cref="CidrList"/>·<see cref="IpAllowlistAdminAccessPolicy"/>는
    /// 이미 같은 IP로 정규화해 인식하지만) 이 파티션 키만 둘로 나뉘어 IP별 로그인 시도 한도가 사실상 두 배로 늘어난다.
    /// </remarks>
    private static string NormalizedIp(IPAddress? ip) =>
        ip is null ? string.Empty : (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();

    /// <summary>1분 창·대기열 없음(초과분 즉시 거부)의 <see cref="FixedWindowRateLimiterOptions"/>를 만든다.</summary>
    /// <param name="permitsPerMinute">1분 창 동안 허용할 최대 요청 수.</param>
    /// <returns>로그인 속도 제한 파티션에 쓸 옵션 인스턴스.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 정적 메서드로 공유 상태가 없다. 반환값은 호출자(내부 제한기)가 전용으로 소유한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="FixedWindowRateLimiterOptions"/> 인스턴스 1개.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    private static FixedWindowRateLimiterOptions Window(int permitsPerMinute) => new()
    {
        PermitLimit = permitsPerMinute,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
        AutoReplenishment = true,
    };
}
