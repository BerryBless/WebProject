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

        // 기본 스킴을 두지 않는다 — 쿠키가 실린 요청이라도 Admin 정책(스킴을 직접 지정)이 걸린 엔드포인트에서만 티켓 검증과 세션 DB 조회가 일어난다.
        // 공개 경로(/health, 첨부 GET)는 인증 비용이 0이다.
        services.AddAuthentication().AddCookie(Scheme);
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
