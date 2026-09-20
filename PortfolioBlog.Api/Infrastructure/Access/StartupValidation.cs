using System.Net;
using Microsoft.Extensions.Options;

namespace PortfolioBlog.Api.Infrastructure.Access;

/// <summary>보안 관련 설정을 시작 시점에 한 번 검증한다. 하나라도 틀리면 예외로 프로세스 시작을 막는다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 시작 스레드에서 1회 호출. 공유 상태 없음.</description></item>
/// <item><description><b>Memory Allocation:</b> 시작 시 1회성 할당만.</description></item>
/// <item><description><b>Blocking:</b> 동기, I/O 없음. DB 마이그레이션보다 <b>앞</b>에서 호출해 설정 오류가 DB 접속 오류에 가려지지 않게 한다.</description></item>
/// </list>
/// </remarks>
public static class StartupValidation
{
    /// <summary><c>Site</c>·<c>Admin</c>·<c>Proxy</c> 설정 섹션을 검증한다. 형식 오류는 환경에 상관없이,
    /// 운영에 필수인 값의 누락·두 origin의 동일 여부·origin의 https 스킴 여부는 <c>Development</c>가 아닌 모든 환경에서 시작 실패로 처리한다
    /// (<c>Staging</c>이나 오타난 환경 이름이 <c>IsProduction()</c> 검사만으로는 걸러지지 않고 그대로 통과하는 것을 막는다).</summary>
    /// <param name="services">검증 대상 옵션을 조회할 <see cref="IServiceProvider"/>. <c>builder.Build()</c> 이후의 <c>app.Services</c>여야 한다.</param>
    /// <param name="environment">현재 호스팅 환경. <see cref="IHostEnvironment.IsDevelopment"/> 판정에 쓰인다.</param>
    /// <exception cref="InvalidOperationException">설정 값의 형식이 잘못되었거나, <c>Development</c>가 아닌 환경에서 필수 설정이 비어 있거나,
    /// 두 origin이 같거나, origin의 스킴이 <c>https</c>가 아닐 때. 메시지에 문제가 된 설정 키를 포함한다.</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Program.cs의 시작 흐름에서 단일 스레드로 1회만 호출된다. 공유 가변 상태를 만들지 않는다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="IOptions{TOptions}.Value"/> 조회와 <see cref="CidrList.Parse"/>·<see cref="IPAddress.TryParse(string?, out IPAddress?)"/> 호출로 시작 시 1회성 할당만 발생한다.</description></item>
    /// <item><description><b>Blocking:</b> 동기 실행이며 I/O가 없다. <c>app.Services.CreateScope()</c>로 <c>AppDbContext</c>를 여는 마이그레이션 블록보다 반드시 먼저 호출해야, 연결 문자열이 잘못됐을 때 나는 Npgsql 오류가 아니라 이 메서드의 명확한 설정 오류가 먼저 보인다.</description></item>
    /// </list>
    /// </remarks>
    public static void Validate(IServiceProvider services, IHostEnvironment environment)
    {
        var site = services.GetRequiredService<IOptions<SiteOptions>>().Value;
        var admin = services.GetRequiredService<IOptions<AdminOptions>>().Value;
        var proxy = services.GetRequiredService<IOptions<ProxyOptions>>().Value;

        Check("Site:PublicOrigin", () => SiteOptions.HostOf(site.PublicOrigin));
        Check("Site:AdminOrigin", () => SiteOptions.HostOf(site.AdminOrigin));
        var cidrs = Check("Admin:AllowedCidrs", () => CidrList.Parse(admin.AllowedCidrs));
        if (proxy.TrustedIp.Length > 0)
        {
            Check("Proxy:TrustedIp", () => IPAddress.TryParse(proxy.TrustedIp, out var ip)
                ? ip
                : throw new FormatException($"단일 IP 주소여야 합니다(CIDR 아님): '{proxy.TrustedIp}'"));
        }
        if (admin.PasswordHash.Length > 0)
        {
            Check("Admin:PasswordHash", () => Convert.FromBase64String(admin.PasswordHash));
        }
        if (admin.LoginPerIpPerMinute < 1 || admin.LoginGlobalPerMinute < 1 || admin.LoginConcurrency < 1 || admin.SessionHours < 1)
        {
            throw new InvalidOperationException("Admin:LoginPerIpPerMinute·LoginGlobalPerMinute·LoginConcurrency·SessionHours 는 1 이상이어야 합니다.");
        }
        if (admin.PreviewPerMinute < 1 || admin.PreviewConcurrency < 1)
        {
            throw new InvalidOperationException("Admin:PreviewPerMinute·PreviewConcurrency 는 1 이상이어야 합니다.");
        }

        if (!environment.IsDevelopment())
        {
            // 운영은 반드시 프록시(Caddy) 뒤에서 돈다. 빠뜨리면 모든 요청의 원본 IP가 Caddy 주소가 되어 IP 검사가 무의미해진다.
            Require(proxy.TrustedIp.Length > 0, "Proxy:TrustedIp");
            Require(cidrs.Count > 0, "Admin:AllowedCidrs");
            Require(admin.PasswordHash.Length > 0, "Admin:PasswordHash");
            // 두 origin이 같으면 서브도메인 격리(세션 쿠키가 공개 호스트로 새지 않음)가 사라진다.
            // appsettings.Development.json은 로컬 https 포트 하나만 쓰려고 의도적으로 같은 값을 두므로 Development만 예외로 허용한다.
            Require(!string.Equals(site.PublicOrigin, site.AdminOrigin, StringComparison.OrdinalIgnoreCase), "Site:AdminOrigin");
            // HostOf가 이미 절대 URI 형식을 검증했으므로 여기서는 예외 없이 재구성할 수 있다. 세션 쿠키가 Secure라 http origin은 애초에 쿠키를 주고받지 못한다.
            Require(string.Equals(new Uri(site.PublicOrigin).Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal), "Site:PublicOrigin");
            Require(string.Equals(new Uri(site.AdminOrigin).Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal), "Site:AdminOrigin");
        }
    }

    /// <summary>설정 값을 파싱하고, 형식 오류(<see cref="FormatException"/>)를 설정 키를 포함한 <see cref="InvalidOperationException"/>으로 바꾼다.</summary>
    /// <typeparam name="T">파싱 결과 타입.</typeparam>
    /// <param name="key"><see cref="InvalidOperationException"/> 메시지에 포함할 설정 키 이름.</param>
    /// <param name="parse">실제 파싱을 수행하는 델리게이트.</param>
    /// <returns>파싱에 성공한 값.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="parse"/>가 <see cref="FormatException"/>을 던졌을 때.</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 정적 메서드로 공유 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <paramref name="parse"/> 델리게이트의 클로저 캡처와 결과값 할당은 호출부 소관.</description></item>
    /// <item><description><b>Blocking:</b> 동기 실행. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    private static T Check<T>(string key, Func<T> parse)
    {
        try { return parse(); }
        catch (FormatException ex) { throw new InvalidOperationException($"설정 {key} 이(가) 잘못되었습니다: {ex.Message}", ex); }
    }

    /// <summary><c>Development</c>가 아닌 환경에서 요구되는 조건이 충족되지 않으면 설정 키를 포함한 <see cref="InvalidOperationException"/>을 던진다.</summary>
    /// <param name="ok">조건 충족 여부.</param>
    /// <param name="key">예외 메시지에 포함할 설정 키 이름.</param>
    /// <exception cref="InvalidOperationException"><paramref name="ok"/>가 <c>false</c>일 때.</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 정적 메서드로 공유 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 실패 시에만 예외·메시지 문자열 1개 할당.</description></item>
    /// <item><description><b>Blocking:</b> 동기 실행. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    private static void Require(bool ok, string key)
    {
        if (!ok) throw new InvalidOperationException($"Development가 아닌 환경에서는 설정 {key} 이(가) 올바르지 않습니다(누락되었거나 조건을 위반).");
    }
}
