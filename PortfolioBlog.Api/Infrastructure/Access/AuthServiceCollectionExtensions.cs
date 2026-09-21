using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace PortfolioBlog.Api.Infrastructure.Access;

/// <summary>쿠키 인증·인가 정책·Data Protection을 DI 컨테이너에 등록하는 확장 메서드 모음.</summary>
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

    /// <summary>쿠키 인증·인가 정책·Data Protection을 등록한다. 설정 값은 전부 지연 바인딩이다.</summary>
    /// <param name="services">등록 대상 서비스 컬렉션.</param>
    /// <returns>체이닝을 위해 그대로 반환하는 <paramref name="services"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 앱 시작 시 단일 스레드에서 1회 호출된다. 등록되는 <see cref="AdminCredential"/>·쿠키 인증 핸들러는 싱글턴이며 내부적으로 Thread-safe하게 구현되어 있다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 옵션 바인딩·서비스 디스크립터 등록에 따른 시작 시 1회성 할당만 발생한다. 실제 설정 값 해석(<see cref="AdminOptions"/> 바인딩)은 여기서 일어나지 않고 첫 사용 시점으로 지연된다.</description></item>
    /// <item><description><b>Blocking:</b> 동기 실행. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public static IServiceCollection AddAdminAuth(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<AdminCredential>();

        // 이 스킴을 명시적 기본값으로 등록한다 — 등록된 스킴이 하나뿐이면 ASP.NET Core가 어차피 그것을 기본값으로 자동 채택하지만,
        // 나중에 두 번째 스킴을 추가하면 "자동 기본값"은 조용히 사라진다. 명시하면 그 시점에도 동작이 그대로 유지된다.
        // WebApplication은 인증 미들웨어를 자동으로 추가하므로, 쿠키가 실린 요청은 인가 정책이 없는 경로(/health 등)에서도
        // 인증 미들웨어가 티켓을 검증하고 SessionValidator의 세션 조회(DB 1행)까지 수행한다 — "기본 스킴을 두지 않으면 비용이 0"이라는
        // 접근은 성립하지 않는다(직접 실측: /health도 쿠키 핸들러가 호출됨). 이 비용을 받아들이는 이유:
        // __Host- 세션 쿠키는 관리 호스트에 바인딩되어 공개 호스트로는 애초에 전송되지 않고(그래서 공개 페이지는 이 비용이 없다),
        // 관리 호스트에서의 단일 행 세션 조회는 단일 작성자 트래픽 규모에서 무시할 수 있는 비용이다.
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
        return services;
    }
}
