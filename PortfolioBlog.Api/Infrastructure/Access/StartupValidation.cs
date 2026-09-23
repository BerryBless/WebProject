using System.Net;
using Microsoft.Extensions.Options;
using Npgsql;
using PortfolioBlog.Api.Infrastructure.Data;
using PortfolioBlog.Api.Infrastructure.Markdown;
using PortfolioBlog.Api.Infrastructure.Storage;
using PortfolioBlog.Api.Infrastructure.Web;

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
    /// <summary><c>Site</c>·<c>Admin</c>·<c>Proxy</c>·<c>ConnectionStrings</c>·<c>DataProtection</c> 설정 섹션을 검증한다. 형식 오류는 환경에 상관없이,
    /// 운영에 필수인 값의 누락·두 origin의 동일 여부·origin의 https 스킴 여부·공개 조회 연결(<c>ConnectionStrings:Public</c>)의 누락·
    /// Data Protection 키 경로(<c>DataProtection:KeysPath</c>)의 누락·상대 경로 여부는 <c>Development</c>가 아닌 모든 환경에서 시작 실패로 처리한다
    /// (<c>Staging</c>이나 오타난 환경 이름이 <c>IsProduction()</c> 검사만으로는 걸러지지 않고 그대로 통과하는 것을 막는다).
    /// 공개 조회 연결의 <c>Options</c>·<c>Command Timeout</c> 규칙과 관리 연결과의 동일 사용자 여부는 환경과 무관하게 항상 검사한다.</summary>
    /// <param name="services">검증 대상 옵션을 조회할 <see cref="IServiceProvider"/>. <c>builder.Build()</c> 이후의 <c>app.Services</c>여야 한다.</param>
    /// <param name="environment">현재 호스팅 환경. <see cref="IHostEnvironment.IsDevelopment"/> 판정에 쓰인다.</param>
    /// <exception cref="InvalidOperationException">설정 값의 형식이 잘못되었거나, <c>Development</c>가 아닌 환경에서 필수 설정이 비어 있거나,
    /// 두 origin이 같거나, origin의 스킴이 <c>https</c>가 아니거나, <c>ConnectionStrings:Public</c>의 사용자 이름이 롤 이름 형식이 아니거나
    /// <c>ConnectionStrings:Default</c>와 같거나, 두 연결 문자열 중 하나에 <c>Options</c>가 있거나 <c>Command Timeout</c>이 <c>Public:StatementTimeoutMs</c>보다
    /// 작거나 같을 때. 메시지에 문제가 된 설정 키를 포함한다.</exception>
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
        var attachments = services.GetRequiredService<IOptions<AttachmentOptions>>().Value;

        Check("Site:PublicOrigin", () => SiteOptions.HostOf(site.PublicOrigin));
        Check("Site:AdminOrigin", () => SiteOptions.HostOf(site.AdminOrigin));
        if (string.IsNullOrWhiteSpace(site.Title)) throw new InvalidOperationException("설정 Site:Title 은(는) 비울 수 없습니다.");
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
        var pub = services.GetRequiredService<IOptions<PublicOptions>>().Value;
        if (admin.UploadPerMinute < 1 || admin.UploadConcurrency < 1)
        {
            throw new InvalidOperationException("Admin:UploadPerMinute·UploadConcurrency 는 1 이상이어야 합니다.");
        }
        if (pub.PagePerIpPerMinute < 1 || pub.AssetPerIpPerMinute < 1 || pub.SearchPerIpPerMinute < 1 || pub.SearchConcurrency < 1)
        {
            throw new InvalidOperationException("Public:PagePerIpPerMinute·AssetPerIpPerMinute·SearchPerIpPerMinute·SearchConcurrency 는 1 이상이어야 합니다.");
        }
        if (pub.StatementTimeoutMs is < 100 or > 60_000)
        {
            throw new InvalidOperationException("Public:StatementTimeoutMs 는 100~60000 이어야 합니다.");
        }
        // 연결 문자열이 비어 있으면 기존 가드(DataServiceCollectionExtensions.RequireConnectionString, 컨텍스트가 처음 해석될 때)가
        // 그대로 처리한다 — 여기서는 값이 있을 때만, 그 값이 공개 연결 조립과 실제로 합쳐지는지를 시작 시점에 미리 확인한다.
        var configuration = services.GetRequiredService<IConfiguration>();
        var connectionString = configuration.GetConnectionString("Default");
        var publicConnectionString = configuration.GetConnectionString("Public");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            CheckConnectionString("ConnectionStrings:Default", connectionString, pub.StatementTimeoutMs);
        }
        if (!string.IsNullOrWhiteSpace(publicConnectionString))
        {
            CheckConnectionString("ConnectionStrings:Public", publicConnectionString, pub.StatementTimeoutMs);
            // 롤 이름은 GRANT 문장에 직접 들어간다(PublicRoleGrants) — 형식을 DB 접속 전에 확인한다.
            var publicRole = PublicRoleGrants.RoleOf(publicConnectionString);
            if (!string.IsNullOrWhiteSpace(connectionString)
                && string.Equals(publicRole, new NpgsqlConnectionStringBuilder(connectionString).Username, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("ConnectionStrings:Public 의 Username 이 ConnectionStrings:Default 와 같습니다 — 공개 조회는 별도의 읽기 전용 롤이어야 합니다.");
            }
        }
        var rendering = services.GetRequiredService<IOptions<RenderingOptions>>().Value;
        if (rendering.Concurrency is < 1 or > 64 || rendering.QueueTimeoutMs is < 1 or > 60_000 || rendering.CacheMegabytes is < 1 or > 1024)
        {
            throw new InvalidOperationException("Rendering:Concurrency·QueueTimeoutMs·CacheMegabytes 범위 오류");
        }
        // 모든 환경에서 필수: 첨부 저장 경로가 없으면 업로드마다 예외가 나므로, 그 실패를 첫 업로드가 아니라 시작 시점에 드러낸다.
        if (string.IsNullOrWhiteSpace(attachments.RootPath))
        {
            throw new InvalidOperationException("설정 Attachments:RootPath 이(가) 필수입니다. 예: .data/attachments(개발) 또는 절대 경로(운영).");
        }
        // 저장소 생성자가 경로를 계산하며 하는 검증(루트 계산 오류 등)도 시작 시점에 드러낸다.
        services.GetRequiredService<FileSystemAttachmentStore>();

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
            // 상대 경로는 콘텐츠 루트(배포 시 작업 디렉터리)에 따라 달라져 운영에서는 의도치 않은 위치를 가리키기 쉽다.
            // appsettings.Development.json은 로컬 상대 경로(.data/attachments)를 그대로 쓰므로 Development만 예외로 허용한다.
            Require(Path.IsPathFullyQualified(attachments.RootPath), "Attachments:RootPath");
            // 공개 조회가 관리 롤(테이블 소유자)로 돌면 default_transaction_read_only만 남는다 — 그것은 세션이 스스로 끌 수 있다(스펙 3.7).
            Require(!string.IsNullOrWhiteSpace(publicConnectionString), "ConnectionStrings:Public");
            // 키가 컨테이너의 임시 위치에 생기면 재시작할 때마다 모든 세션이 조용히 끊긴다(읽기 전용 루트 FS에서는 메모리에만 남는다).
            Require(Path.IsPathFullyQualified(configuration[AuthServiceCollectionExtensions.DataProtectionKeysPathKey] ?? string.Empty), AuthServiceCollectionExtensions.DataProtectionKeysPathKey);
        }
    }

    /// <summary>연결 문자열 하나가 공개 연결 조립과 합쳐지는지, 클라이언트 시간 제한이 DB 시간 제한보다 긴지 확인한다.</summary>
    /// <param name="key">예외 메시지에 넣을 설정 키. 값(비밀번호 포함 가능)은 메시지에 넣지 않는다.</param>
    /// <param name="connectionString">검사할 연결 문자열.</param>
    /// <param name="statementTimeoutMs"><c>Public:StatementTimeoutMs</c>.</param>
    /// <exception cref="InvalidOperationException"><c>Options</c>가 들어 있거나 <c>Command Timeout</c>(초)×1000이 <paramref name="statementTimeoutMs"/> 이하일 때.</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 정적 메서드로 공유 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 연결 문자열 파서 1개.</description></item>
    /// <item><description><b>Blocking:</b> 동기 실행. 순수 파싱이라 I/O가 없다 — 이 클래스의 "I/O 없음" 계약을 지킨다.</description></item>
    /// </list>
    /// </remarks>
    private static void CheckConnectionString(string key, string connectionString, int statementTimeoutMs)
    {
        // Npgsql은 알 수 없는 키워드·형식 오류를 FormatException이 아니라 ArgumentException으로 던진다(실측) — Check가 둘 다 잡는다.
        var parsed = Check(key, () => new NpgsqlConnectionStringBuilder(connectionString));
        // Options가 이미 있으면 공개 연결의 시작 옵션(statement_timeout·default_transaction_read_only)과 합칠 수 없다.
        // 첫 공개 요청이 아니라 시작 시점에 드러낸다.
        if (!string.IsNullOrEmpty(parsed.Options))
        {
            throw new InvalidOperationException($"{key} 에 Options 를 넣을 수 없습니다 — 공개 조회 연결의 시작 옵션과 합칠 수 없습니다.");
        }
        // CommandTimeout(초, 0=무한)이 statement_timeout(밀리초)보다 먼저 끊기면 클라이언트가 DB보다 먼저 취소해버려서
        // OverloadExceptionHandler가 기대하는 57014(DB 시간제한) 대신 클라이언트 취소 예외가 난다 — 503 매핑 설계가 깨진다.
        if (parsed.CommandTimeout != 0 && parsed.CommandTimeout * 1000L <= statementTimeoutMs)
        {
            throw new InvalidOperationException(
                $"{key} 의 Command Timeout(초)이 Public:StatementTimeoutMs(밀리초)보다 커야 합니다 — " +
                "그렇지 않으면 클라이언트 취소가 DB의 statement_timeout보다 먼저 발생합니다.");
        }
    }

    /// <summary>설정 값을 파싱하고, 형식 오류(<see cref="FormatException"/>·<see cref="ArgumentException"/>)를 설정 키를 포함한 <see cref="InvalidOperationException"/>으로 바꾼다.</summary>
    /// <typeparam name="T">파싱 결과 타입.</typeparam>
    /// <param name="key"><see cref="InvalidOperationException"/> 메시지에 포함할 설정 키 이름.</param>
    /// <param name="parse">실제 파싱을 수행하는 델리게이트.</param>
    /// <returns>파싱에 성공한 값.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="parse"/>가 <see cref="FormatException"/> 또는 <see cref="ArgumentException"/>을 던졌을 때.</exception>
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
        catch (Exception ex) when (ex is FormatException or ArgumentException) { throw new InvalidOperationException($"설정 {key} 이(가) 잘못되었습니다: {ex.Message}", ex); }
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
