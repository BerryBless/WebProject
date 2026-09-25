using MySqlConnector;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>설정 오류는 조용히 넘어가지 않고 시작을 막는다.</summary>
/// <param name="mysql">컬렉션이 공유하는 MySQL 컨테이너 fixture. 케이스마다 설정을 다르게 주어야 하므로 클래스 픽스처 대신 케이스마다 <see cref="ApiFactory"/>를 직접 만든다.</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 실행된다. <see cref="ApiFactory.CreateClient()"/> 최초 호출이 호스트를 동기적으로 기동하며 그 자리에서 시작 검증 예외를 던진다.</description></item>
/// <item><description><b>Memory Policy:</b> 테스트 케이스마다 <see cref="ApiFactory"/>와 설정 딕셔너리를 직접 생성·<c>using</c>으로 해제하여 클래스 픽스처를 공유하지 않는다.</description></item>
/// <item><description><b>Concurrency:</b> 케이스 간 공유 가변 상태가 없으므로 병렬 실행에 안전하다.</description></item>
/// <item><description><b>Blocking:</b> <c>CreateClient()</c>는 동기 호출이며 시작 실패를 그 자리에서 예외로 전파한다(비동기 I/O 대기 없음).</description></item>
/// </list>
/// </remarks>
[Collection("mysql")]
public sealed class StartupValidationTests(MySqlContainerFixture mysql)
{
    /// <summary>Production 환경에서 시작에 필요한 최소 설정 세트를 만들고, <paramref name="mutate"/>로 한 항목만 깨뜨릴 수 있게 한다.</summary>
    /// <param name="mutate">기본 설정 딕셔너리를 수정하는 콜백. 특정 키를 비워 검증 실패를 유도한다.</param>
    /// <returns>Production 환경 전환 설정을 포함한, <see cref="ApiFactory"/> 생성자에 넘길 설정 딕셔너리.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 호출마다 새 딕셔너리를 만들어 반환하므로 다른 호출과 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 딕셔너리 1개와 더미 해시 바이트 배열 1개를 할당한다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    private static Dictionary<string, string?> Production(Action<Dictionary<string, string?>> mutate)
    {
        var s = new Dictionary<string, string?>
        {
            ["Test:Environment"] = "Production",
            ["Proxy:TrustedIp"] = "172.30.0.2",
            // 형식(base64)만 맞는 더미 값. 실제 해시·비밀번호를 테스트 코드에 넣지 않는다.
            ["Admin:PasswordHash"] = Convert.ToBase64String(new byte[61]),
        };
        mutate(s);
        return s;
    }

    /// <summary>주어진 설정으로 호스트를 기동하면 예외가 나고, 그 예외 텍스트에 기대한 설정 키가 포함되는지 검증하는 테스트 헬퍼.</summary>
    /// <param name="settings"><see cref="ApiFactory"/>에 줄 설정 오버라이드.</param>
    /// <param name="expectedKey">예외 텍스트에 포함되어야 하는 설정 키 이름.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 호출마다 새 <see cref="ApiFactory"/>를 만들고 <c>using</c>으로 해제하므로 다른 호출과 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 팩토리 1개.</description></item>
    /// <item><description><b>Blocking:</b> <c>CreateClient()</c>는 동기 호출이며 시작 실패를 그 자리에서 예외로 전파한다.</description></item>
    /// </list>
    /// <c>WebApplicationFactory</c>는 시작 예외를 감쌀 수 있으므로 예외 타입이 아니라
    /// <c>Assert.ThrowsAny&lt;Exception&gt;</c> 후 <c>ex.ToString()</c>(내부 예외까지 포함)에 키 이름이 포함되는지로 검증한다.
    /// </remarks>
    private void AssertStartupFails(Dictionary<string, string?> settings, string expectedKey)
    {
        using var factory = new ApiFactory(mysql, settings);
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains(expectedKey, ex.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Production 환경에서 필수 설정(프록시·CIDR·비밀번호 해시)이 모두 있으면 정상 기동하는지 검증한다.</summary>
    [Fact]
    public void Production_ValidSettings_Starts()
    {
        using var factory = new ApiFactory(mysql, Production(_ => { }));
        using var client = factory.CreateClient();
    }

    /// <summary>Production 환경에서 <c>Proxy:TrustedIp</c>가 비면 시작이 실패하고 예외에 그 키가 포함되는지 검증한다.</summary>
    [Fact]
    public void Production_MissingTrustedProxy_Fails() =>
        AssertStartupFails(Production(s => s["Proxy:TrustedIp"] = ""), "Proxy:TrustedIp");

    /// <summary>Production 환경에서 <c>Admin:AllowedCidrs</c>가 비면 시작이 실패하고 예외에 그 키가 포함되는지 검증한다.</summary>
    [Fact]
    public void Production_EmptyAllowedCidrs_Fails() =>
        AssertStartupFails(Production(s => s["Admin:AllowedCidrs"] = ""), "Admin:AllowedCidrs");

    /// <summary>Production 환경에서 <c>Admin:PasswordHash</c>가 비면 시작이 실패하고 예외에 그 키가 포함되는지 검증한다.</summary>
    [Fact]
    public void Production_MissingPasswordHash_Fails() =>
        AssertStartupFails(Production(s => s["Admin:PasswordHash"] = ""), "Admin:PasswordHash");

    /// <summary>환경에 상관없이 <c>Admin:AllowedCidrs</c> 형식이 CIDR 문법(쉼표 구분)에 맞지 않으면 시작이 실패하는지 검증한다.</summary>
    [Fact]
    public void AnyEnvironment_InvalidCidr_Fails() =>
        AssertStartupFails(new Dictionary<string, string?> { ["Admin:AllowedCidrs"] = "203.0.113.0/24,198.51.100.0/24" }, "Admin:AllowedCidrs");

    /// <summary>환경에 상관없이 <c>Proxy:TrustedIp</c>가 단일 IP가 아니라 CIDR 형태면 시작이 실패하는지 검증한다.</summary>
    [Fact]
    public void AnyEnvironment_InvalidTrustedProxy_Fails() =>
        AssertStartupFails(new Dictionary<string, string?> { ["Proxy:TrustedIp"] = "172.30.0.0/16" }, "Proxy:TrustedIp");

    /// <summary>환경에 상관없이 <c>Site:AdminOrigin</c>에 끝 슬래시(경로)가 붙으면 시작이 실패하는지 검증한다.</summary>
    [Fact]
    public void AnyEnvironment_OriginWithPath_Fails() =>
        AssertStartupFails(new Dictionary<string, string?> { ["Site:AdminOrigin"] = "https://admin.test/" }, "Site:AdminOrigin");

    /// <summary>환경에 상관없이 <c>Site:Title</c>이 공백뿐이면 시작이 실패하고 예외에 그 키가 포함되는지 검증한다.
    /// 비어 있으면 <c>&lt;title&gt;</c>·머리글·Atom 피드 제목이 " · Blog" 꼴로 깨지므로 첫 요청이 아니라 시작 시점에 막는다.</summary>
    [Fact]
    public void AnyEnvironment_BlankSiteTitle_Fails() =>
        AssertStartupFails(new Dictionary<string, string?> { ["Site:Title"] = "   " }, "Site:Title");

    /// <summary><c>Staging</c>처럼 <c>Production</c>이 아닌(그러나 <c>Development</c>도 아닌) 환경에서도 필수 설정 누락으로 시작이 실패하는지 검증한다.
    /// <c>environment.IsProduction()</c> 판정만으로는 이런 환경 이름을 걸러내지 못해 필수 검사를 조용히 건너뛰던 결함의 회귀 테스트다.</summary>
    [Fact]
    public void Staging_MissingTrustedProxy_Fails()
    {
        var settings = Production(s => s["Proxy:TrustedIp"] = "");
        settings["Test:Environment"] = "Staging";
        AssertStartupFails(settings, "Proxy:TrustedIp");
    }

    /// <summary><c>Development</c>가 아닌 환경에서 <c>Site:PublicOrigin</c>과 <c>Site:AdminOrigin</c>이 같으면(대소문자 무시) 시작이 실패하는지 검증한다.
    /// 두 origin이 같으면 서브도메인 격리(세션 쿠키가 공개 호스트로 새지 않음)가 사라진다.
    /// <c>appsettings.Development.json</c>은 로컬 포트 하나만 쓰려고 의도적으로 같은 값을 두므로 Development는 이 검사에서 예외다.</summary>
    [Fact]
    public void Production_EqualOrigins_Fails() =>
        AssertStartupFails(Production(s => s["Site:AdminOrigin"] = ApiFactory.PublicOrigin), "Site:AdminOrigin");

    /// <summary><c>Development</c>가 아닌 환경에서 <c>Site:AdminOrigin</c>의 스킴이 <c>https</c>가 아니면 시작이 실패하는지 검증한다.
    /// 세션 쿠키가 Secure라 http origin에서는 애초에 쿠키를 주고받지 못하므로, 이 값은 배포 실수의 신호다.</summary>
    [Fact]
    public void Production_HttpAdminOrigin_Fails() =>
        AssertStartupFails(Production(s => s["Site:AdminOrigin"] = "http://admin.test"), "Site:AdminOrigin");

    /// <summary>환경에 상관없이 <c>Attachments:RootPath</c>가 비면 시작이 실패하고 예외에 그 키가 포함되는지 검증한다.
    /// 비어 있으면 첫 업로드에서야 예외가 나므로, 그 실패를 시작 시점으로 앞당기는 규칙의 회귀 테스트다.</summary>
    [Fact]
    public void AnyEnvironment_MissingAttachmentsRoot_Fails() =>
        AssertStartupFails(new Dictionary<string, string?> { ["Attachments:RootPath"] = "" }, "Attachments:RootPath");

    /// <summary>Production 환경에서 <c>Attachments:RootPath</c>가 상대 경로면 시작이 실패하고 예외에 그 키가 포함되는지 검증한다.
    /// 상대 경로는 콘텐츠 루트(배포 시 작업 디렉터리)에 따라 달라져 운영에서 의도치 않은 위치를 가리키기 쉽다.
    /// <c>appsettings.Development.json</c>은 상대 경로(<c>.data/attachments</c>)를 그대로 쓰므로 Development는 이 검사에서 예외다.</summary>
    [Fact]
    public void Production_RelativeAttachmentsRoot_Fails() =>
        AssertStartupFails(Production(s => s["Attachments:RootPath"] = ".data/attachments"), "Attachments:RootPath");

    /// <summary>환경에 상관없이 <c>Attachments:RootPath</c>가 이미 존재하는 파일(디렉터리가 아님)을 가리키면 시작 시점의 쓰기 가능 확인에서
    /// 실패하고 예외에 그 키가 포함되는지 검증한다 — 첫 업로드가 아니라 시작 시점에 드러나는지가 이 테스트의 핵심이다.</summary>
    [Fact]
    public void AnyEnvironment_AttachmentsRootIsAnExistingFile_Fails()
    {
        var tempFile = Path.GetTempFileName(); // 절대 경로의 0바이트 파일을 만들어 준다 — "디렉터리 자리에 파일이 있다"를 재현한다
        try
        {
            AssertStartupFails(new Dictionary<string, string?> { ["Attachments:RootPath"] = tempFile }, "Attachments:RootPath");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    /// <summary>경로로 쓸 수 없는 값(NUL 문자 포함)이면 <c>Path.GetFullPath</c>가 던지는 <see cref="ArgumentException"/>이
    /// (그 메시지 자체는 어느 설정 키가 문제인지 말해주지 않는데도) 설정 키를 명시한 시작 실패로 바뀌는지 검증한다.
    /// NUL은 소스에 원시 바이트로 쓰지 않고 C# 이스케이프(<c>'\0'</c>)로만 표기한다.</summary>
    [Fact]
    public void AnyEnvironment_AttachmentsRootHasNulCharacter_Fails() =>
        AssertStartupFails(new Dictionary<string, string?> { ["Attachments:RootPath"] = "bad" + '\0' + "path" }, "Attachments:RootPath");

    /// <summary>환경에 상관없이 <c>Admin:LoginPerIpPerMinute</c>가 0이면 시작이 실패하고 예외에 그 키가 포함되는지 검증한다.</summary>
    [Fact]
    public void AnyEnvironment_ZeroLoginPerIpPerMinute_Fails() =>
        AssertStartupFails(new Dictionary<string, string?> { ["Admin:LoginPerIpPerMinute"] = "0" }, "Admin:LoginPerIpPerMinute");

    /// <summary>환경에 상관없이 <c>Admin:LoginGlobalPerMinute</c>가 0이면 시작이 실패하고 예외에 그 키가 포함되는지 검증한다.</summary>
    [Fact]
    public void AnyEnvironment_ZeroLoginGlobalPerMinute_Fails() =>
        AssertStartupFails(new Dictionary<string, string?> { ["Admin:LoginGlobalPerMinute"] = "0" }, "LoginGlobalPerMinute");

    /// <summary>환경에 상관없이 <c>Admin:LoginConcurrency</c>가 0이면 시작이 실패하고 예외에 그 키가 포함되는지 검증한다.</summary>
    [Fact]
    public void AnyEnvironment_ZeroLoginConcurrency_Fails() =>
        AssertStartupFails(new Dictionary<string, string?> { ["Admin:LoginConcurrency"] = "0" }, "LoginConcurrency");

    /// <summary>환경에 상관없이 <c>Admin:SessionHours</c>가 0이면 시작이 실패하고 예외에 그 키가 포함되는지 검증한다.</summary>
    [Fact]
    public void AnyEnvironment_ZeroSessionHours_Fails() =>
        AssertStartupFails(new Dictionary<string, string?> { ["Admin:SessionHours"] = "0" }, "SessionHours");

    /// <summary>환경에 상관없이 <c>Admin:PreviewPerMinute</c>가 0이면 시작이 실패하고 예외에 그 키가 포함되는지 검증한다.</summary>
    [Fact]
    public void AnyEnvironment_ZeroPreviewPerMinute_Fails() =>
        AssertStartupFails(new Dictionary<string, string?> { ["Admin:PreviewPerMinute"] = "0" }, "Admin:PreviewPerMinute");

    /// <summary>환경에 상관없이 <c>Admin:PreviewConcurrency</c>가 0이면 시작이 실패하고 예외에 그 키가 포함되는지 검증한다.</summary>
    [Fact]
    public void AnyEnvironment_ZeroPreviewConcurrency_Fails() =>
        AssertStartupFails(new Dictionary<string, string?> { ["Admin:PreviewConcurrency"] = "0" }, "PreviewConcurrency");

    /// <summary>환경에 상관없이 <c>ConnectionStrings:Default</c>의 Command Timeout(초)이 <c>Public:StatementTimeoutMs</c>(밀리초)보다
    /// 먼저 끊기면 시작이 실패하고 예외에 그 키가 포함되는지 검증한다 — 그렇지 않으면 클라이언트 취소가 DB의 실행 시간 상한(max_execution_time, 3024)보다
    /// 먼저 발생해 <c>OverloadExceptionHandler</c>의 503 매핑 설계가 성립하지 않는다. 기본 <c>Public:StatementTimeoutMs</c>는
    /// 3000이므로 Command Timeout=1(=1000ms)이면 1000 &lt;= 3000 조건에 걸린다.</summary>
    [Fact]
    public void AnyEnvironment_CommandTimeoutNotLargerThanStatementTimeout_Fails()
    {
        var shortCommandTimeout = new MySqlConnectionStringBuilder(mysql.ConnectionString) { DefaultCommandTimeout = 1 }.ConnectionString;
        AssertStartupFails(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = shortCommandTimeout }, "ConnectionStrings:Default");
    }

    /// <summary><c>Development</c>가 아닌 환경에서 공개 조회 전용 연결(<c>ConnectionStrings:Public</c>)이 없으면 시작이 실패한다.
    /// 없으면 공개 페이지가 테이블 소유자 롤로 돌고, 남는 방어는 세션이 스스로 끌 수 있는 <c>default_transaction_read_only</c>뿐이다.</summary>
    [Fact]
    public void Production_MissingPublicConnectionString_Fails() =>
        AssertStartupFails(Production(s => s["ConnectionStrings:Public"] = ""), "ConnectionStrings:Public");

    /// <summary><c>Development</c>가 아닌 환경에서 Data Protection 키 경로가 없거나 상대 경로면 시작이 실패한다.
    /// 상대 경로("keys")를 따로 보는 이유: 컨테이너의 작업 디렉터리(읽기 전용 루트 FS) 아래를 가리키게 된다.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("keys")]
    public void Production_MissingOrRelativeDataProtectionKeysPath_Fails(string path) =>
        AssertStartupFails(Production(s => s["DataProtection:KeysPath"] = path), "DataProtection:KeysPath");

    /// <summary>공개 연결의 사용자가 관리 연결과 같으면 시작이 실패한다. "root"인 이유: 테스트 컨테이너의 관리 연결 사용자가 그 이름이다.</summary>
    [Fact]
    public void PublicConnectionString_SameUserAsDefault_Fails() =>
        AssertStartupFails(new Dictionary<string, string?> { ["ConnectionStrings:Public"] = "Server=db.example;Database=blog;User ID=root;SslMode=Required" }, "별도의 읽기 전용 사용자");

    /// <summary>공개 연결의 사용자 이름이 평범한 소문자 식별자가 아니면 DB에 닿기 전에 시작이 실패한다(GRANT 문장에 직접 들어가는 값이다).</summary>
    [Fact]
    public void PublicConnectionString_WithUnsafeRoleName_Fails() =>
        AssertStartupFails(new Dictionary<string, string?> { ["ConnectionStrings:Public"] = "Server=db.example;Database=blog;User ID='blog public';SslMode=Required" }, "ConnectionStrings:Public 의 User ID");

    /// <summary>환경에 상관없이 <c>ConnectionStrings:Public</c>이 MySqlConnector가 모르는 키워드를 담고 있으면(연결 문자열 자체를 파싱하지 못한다)
    /// 시작이 실패하고, 예외 텍스트에 설정 키(<c>ConnectionStrings:Public</c>)는 있지만 연결 문자열 값(호스트 <c>db.example</c>)은 없는지
    /// 검증한다. MySqlConnector는 이런 파싱 실패를 <see cref="FormatException"/>이 아니라 <see cref="ArgumentException"/>으로 던지므로(스파이크 S12:
    /// <c>"Option 'bogus keyword' not supported."</c>), <c>StartupValidation.CheckConnectionString</c>이 파서 호출 자체를 감싸는지가
    /// 이 테스트의 핵심이다.</summary>
    [Fact]
    public void PublicConnectionString_WithUnknownKeyword_FailsWithoutLeakingTheValue()
    {
        using var factory = new ApiFactory(mysql, new Dictionary<string, string?>
        {
            ["ConnectionStrings:Public"] = "Server=db.example;Database=blog;User ID=blog_public_test;Bogus Keyword=1;SslMode=Required",
        });
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        var text = ex.ToString();
        Assert.Contains("ConnectionStrings:Public", text, StringComparison.Ordinal);
        Assert.DoesNotContain("db.example", text, StringComparison.Ordinal);
    }

    /// <summary>같은 파싱 실패가 <c>ConnectionStrings:Default</c>에서도 그 키로 보고되고 값(호스트)은 새지 않는지 검증한다.
    /// 사용자 이름 <c>root</c>는 테스트 컨테이너의 실제 관리 사용자 이름을 흉내만 낸 값이며, 연결 문자열 파싱 자체가 이 지점에서
    /// 이미 실패하므로 실제 DB에 연결을 시도하지 않는다.</summary>
    [Fact]
    public void DefaultConnectionString_WithUnknownKeyword_FailsWithoutLeakingTheValue()
    {
        using var factory = new ApiFactory(mysql, new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = "Server=db.example;Database=blog;User ID=root;Bogus Keyword=1;SslMode=Required",
        });
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        var text = ex.ToString();
        Assert.Contains("ConnectionStrings:Default", text, StringComparison.Ordinal);
        Assert.DoesNotContain("db.example", text, StringComparison.Ordinal);
    }
}
