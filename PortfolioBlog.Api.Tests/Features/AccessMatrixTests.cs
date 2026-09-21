using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PortfolioBlog.Api.Infrastructure.Access;
using PortfolioBlog.Api.Infrastructure.Web;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>등록된 **모든** <c>/api</c> 엔드포인트를 라우트 테이블에서 열거해 접근 계약을 검사한다.
/// 새 엔드포인트를 추가하면서 보호를 빠뜨리면(그룹 밖에 매핑, AllowAnonymous 오용) 이 테스트가 잡는다.</summary>
/// <param name="factory">컬렉션이 공유하는 컨테이너를 바탕으로 클래스 전용 DB를 갖는 <see cref="ApiFactory"/> 클래스 픽스처.</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 실행된다. <see cref="ApiFactory"/>가 호스팅하는 인메모리 TestServer가
/// 실제 PostgreSQL 컨테이너에 TCP로 접속하므로 DB I/O는 실제 네트워크 왕복을 수반한다.</description></item>
/// <item><description><b>Memory Policy:</b> 팩토리는 <see cref="IClassFixture{TFixture}"/>로 클래스 단위 1회 생성·공유된다.
/// <see cref="Targets"/>는 호출마다 라우트 테이블을 순회해 <see cref="Target"/> 목록을 새로 할당한다(테스트 간 공유 가변 상태 없음).</description></item>
/// <item><description><b>Concurrency:</b> 팩토리·HttpClient는 Thread-safe하나, 테스트 간에는 클래스별 고유 DB로 격리되어 데이터 간섭이 없다.</description></item>
/// <item><description><b>Blocking:</b> 모든 HTTP 접근은 <c>await</c>로 비동기 대기하며 동기 블로킹이 없다.</description></item>
/// </list>
/// </remarks>
[Collection("postgres")]
public sealed partial class AccessMatrixTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>익명 접근이 허용된 유일한 두 경로. 나머지 <c>/api/*</c>는 전부 세션이 필요하다.</summary>
    private static readonly string[] AnonymousAllowed = ["/api/auth/login", "/api/auth/me"];

    /// <summary><paramref name="raw"/>가 <c>/api</c> 그룹(정확히 <c>/api</c> 자신이거나 <c>/api/</c>로 시작하는 경로)에 속하는지 세그먼트 경계로 판정한다.</summary>
    /// <param name="raw">라우트 패턴의 원본 텍스트(예: <see cref="RouteEndpoint.RoutePattern"/>의 <c>RawText</c>).</param>
    /// <returns><paramref name="raw"/>가 <c>/api</c> 세그먼트 경계로 시작하면 <c>true</c>.</returns>
    /// <remarks>
    /// 단순 <c>StartsWith("/api", …)</c>는 <c>/api-import</c>·<c>/apifeed.json</c>처럼 접두사만 같은 무관한 경로도 같이 잡아,
    /// 그런 경로가 보호 그룹의 검사도 받지 않고 공개 허용 목록 검사도 건너뛰는(즉 아무 검사도 받지 않는) 사각지대를 만든다.
    /// 세그먼트 경계(<c>/api</c> 정확히 일치 또는 <c>/api/</c> 접두사)로 판정해야 그 사각지대가 없다.
    /// </remarks>
    private static bool IsUnderApi(string raw) =>
        raw.Equals("/api", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);

    /// <summary>라우트 패턴의 <c>{id:guid}</c> 같은 매개변수 구간을 매칭하는 정규식.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. <c>GeneratedRegex</c>가 생성하는 인스턴스는 불변이며 상태를 공유하지 않는다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 소스 생성기가 컴파일 타임에 전용 매처를 만들어 런타임 정규식 파싱 할당이 없다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. 동기 CPU 연산만 수행한다.</description></item>
    /// </list>
    /// </remarks>
    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex RouteParameter();

    /// <summary>라우트 테이블에서 뽑아낸 요청 대상 1건: HTTP 메서드·경로(매개변수는 더미 GUID로 치환)·익명 허용 여부·multipart 전용 여부.</summary>
    /// <param name="Method">HTTP 메서드.</param>
    /// <param name="Path">매개변수를 더미 값으로 치환한 요청 경로.</param>
    /// <param name="AllowsAnonymous">엔드포인트 메타데이터에 <see cref="IAllowAnonymous"/>가 있는지 여부.</param>
    /// <param name="RequiresMultipart">엔드포인트가 <see cref="IAcceptsMetadata"/>로 <c>multipart/form-data</c>만 받는다고 선언했는지 여부(<c>IFormFile</c> 바인딩).
    /// 실측: 이런 엔드포인트에 세션 없이 <c>application/json</c> 본문을 보내면 401이 아니라 415(ASP.NET Core의 일반 RFC 9110 ProblemDetails 본문이며,
    /// 첨부 업로드 핸들러가 스스로 만드는 415의 <c>"지원하지 않는 이미지"</c> 제목과는 다르다)가 온다 — 핸들러가 호출되지 않았다는 뜻이다.
    /// 같은 요청에 Content-Type만 <c>multipart/form-data</c>로 맞추면(필드는 비워도) 정상적으로 401이 나온다(그 아래 별도로 확인함, 인가 우회 아님).
    /// <see cref="Build"/>가 매번 <c>application/json</c>을 보내면 이 엔드포인트만 401/403 대신 415가 나와 이 테스트들의 전제가 가려지므로 피한다.
    /// 추가 실측(fix round 1, A6): JSON 본문을 받는 <c>POST /api/posts</c>에 <c>text/plain</c>으로(세션 없이, 다른 게이트는 전부 통과시킨 채) 보내도
    /// 똑같이 415가 나왔다 — 이 415-vs-401 현상은 첨부 업로드만의 특이 동작이 아니라 <c>IAcceptsMetadata</c>를 선언하는 엔드포인트 일반에서
    /// 관찰된다는 뜻이다(관찰된 동작만 적는다: 정확한 메커니즘은 라우팅이 Content-Type 불일치 시 인가가 보기 전에 그 엔드포인트를
    /// 메타데이터 없는 415 전용 엔드포인트로 바꿔치기하는 것으로 추정되나, 프레임워크 내부까지 검증하지는 않았다).</param>
    private sealed record Target(string Method, string Path, bool AllowsAnonymous, bool RequiresMultipart);

    /// <summary>호스트의 <see cref="EndpointDataSource"/>를 순회해 <c>/api</c>로 시작하는 모든 라우트 엔드포인트를 <see cref="Target"/> 목록으로 뽑아낸다.</summary>
    /// <returns>메서드별로 펼쳐진 <see cref="Target"/> 목록(같은 경로가 여러 메서드를 지원하면 메서드 수만큼 항목이 생긴다).</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 호출된다. <c>factory.CreateClient()</c>가 호스트를 최초 기동시킨다(지연 초기화 트리거).</description></item>
    /// <item><description><b>Memory Policy:</b> 라우트 수만큼 <see cref="Target"/> 레코드를 새로 할당한다. 반환된 목록은 호출자 전용이며 캐시하지 않는다.</description></item>
    /// <item><description><b>Concurrency:</b> Thread-safe하게 호출할 수 있으나 반환 목록을 다른 스레드와 공유할 계획은 없다. Blocking: 동기 순회, I/O 없음(호스트가 이미 기동돼 있으면).</description></item>
    /// </list>
    /// </remarks>
    private List<Target> Targets()
    {
        using var _ = factory.CreateClient(); // 호스트 기동
        var targets = new List<Target>();
        foreach (var endpoint in factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var raw = endpoint.RoutePattern.RawText ?? string.Empty;
            if (!IsUnderApi(raw)) continue;
            var path = RouteParameter().Replace(raw, Guid.Empty.ToString());
            var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            var contentTypes = endpoint.Metadata.GetMetadata<IAcceptsMetadata>()?.ContentTypes ?? [];
            var multipart = contentTypes.Any(c => c.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase));
            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                targets.Add(new Target(method, path, anonymous, multipart));
            }
        }
        return targets;
    }

    /// <summary><paramref name="t"/>에 대응하는 <see cref="HttpRequestMessage"/>를 만든다. POST/PUT에는 일부러 불완전한 본문을 싣는다
    /// (JSON 엔드포인트에는 깨진 JSON, multipart 전용 엔드포인트에는 파일 파트가 없는 빈 multipart 폼).</summary>
    /// <param name="t">요청을 만들 대상.</param>
    /// <returns>호출자가 <c>using</c>으로 해제해야 하는 <see cref="HttpRequestMessage"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 호출된다.</description></item>
    /// <item><description><b>Memory Policy:</b> <see cref="HttpRequestMessage"/> 1개와(POST/PUT이면) 본문 콘텐츠 1개를 할당한다.</description></item>
    /// <item><description><b>Concurrency:</b> Thread-safe. Blocking: 즉시 반환, I/O 없음.
    /// 깨진 JSON(<c>{broken</c>)·빈 multipart 폼 둘 다 접근 검사가 본문 바인딩보다 먼저 실행됨을 증명하기 위한 의도적 선택이다: 접근 검사가 먼저면
    /// 바인딩 오류(400)가 아니라 401/403/404가 먼저 나와야 한다. <paramref name="t"/>가 <see cref="Target.RequiresMultipart"/>이면
    /// <c>application/json</c>을 보내지 않는다 — 실측상 Content-Type이 <c>IAcceptsMetadata</c>와 안 맞으면 이 엔드포인트는 세션이 없어도 401 대신
    /// 415(ASP.NET Core의 일반 ProblemDetails, 핸들러 자신이 만드는 415와 본문이 다르다 — 핸들러가 호출되지 않았다는 뜻)를 돌려줘 "접근 검사가 먼저"라는
    /// 이 테스트의 전제 자체가 다른 상태 코드로 가려진다. Content-Type을 <c>multipart/form-data</c>로 맞추면 같은 무세션 요청이 정상적으로 401이 되므로
    /// (별도 확인함) 이 엔드포인트의 인가 자체가 우회되는 것은 아니다 — 415는 Content-Type 불일치에서만 나온다.</description></item>
    /// </list>
    /// </remarks>
    private static HttpRequestMessage Build(Target t) => new(new HttpMethod(t.Method), t.Path)
    {
        Content = t.Method is not ("POST" or "PUT") ? null
            : t.RequiresMultipart ? new MultipartFormDataContent()
            : new StringContent("{broken", Encoding.UTF8, "application/json"),
    };

    /// <summary><paramref name="targets"/> 전부에 요청을 보내 상태 코드가 모두 <paramref name="expected"/>인지 검증한다.</summary>
    /// <param name="client">요청에 쓸 <see cref="HttpClient"/>.</param>
    /// <param name="targets">검증할 대상 목록.</param>
    /// <param name="expected">모든 대상에서 기대하는 상태 코드.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 호출된다.</description></item>
    /// <item><description><b>Memory Policy:</b> 대상마다 요청·응답 객체를 만들고 즉시 <c>using</c>으로 해제한다(누적 보유 없음).</description></item>
    /// <item><description><b>Concurrency:</b> Thread-safe. Non-blocking: 대상을 순차적으로 <c>await</c>한다(동시 호출로 라우트를 섞지 않는다).</description></item>
    /// </list>
    /// </remarks>
    private static async Task AssertAllAsync(HttpClient client, IEnumerable<Target> targets, HttpStatusCode expected)
    {
        foreach (var t in targets)
        {
            using var req = Build(t);
            using var res = await client.SendAsync(req);
            Assert.True(expected == res.StatusCode, $"{t.Method} {t.Path}: 기대 {(int)expected}, 실제 {(int)res.StatusCode}");
        }
    }

    /// <summary>라우트 테이블이 기대한 최소 엔드포인트 수 이상을 갖고, 익명 허용 경로가 정확히 <c>login</c>·<c>me</c>뿐인지 검증한다.</summary>
    [Fact]
    public void RouteTable_ContainsExpectedSurface_AndOnlyLoginAndMeAreAnonymous()
    {
        var targets = Targets();
        Assert.True(targets.Count >= 19, $"열거된 /api 엔드포인트가 너무 적다: {targets.Count}");
        Assert.Equal(AnonymousAllowed, targets.Where(t => t.AllowsAnonymous).Select(t => t.Path).Distinct().Order());
    }

    /// <summary>허용 CIDR 밖의 IP는 모든 <c>/api</c> 엔드포인트에서(익명 허용 포함) 바인딩 전 403을 받는지 검증한다.</summary>
    [Fact]
    public async Task OutsiderIp_Gets403_OnEveryEndpoint_BeforeBinding()
    {
        using var client = factory.CreateAdminClient();
        client.DefaultRequestHeaders.Remove(RemoteIpStartupFilter.HeaderName);
        client.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, ApiFactory.OutsiderIp);
        await AssertAllAsync(client, Targets(), HttpStatusCode.Forbidden);
    }

    /// <summary>CSRF 헤더(<c>X-Requested-With</c>)가 없으면 세션이 있어도 모든 엔드포인트에서 403을 받는지 검증한다.</summary>
    [Fact]
    public async Task MissingCsrfHeader_Gets403_OnEveryEndpoint_EvenWithSession()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        client.DefaultRequestHeaders.Remove(AdminSurfaceMiddleware.CsrfHeaderName);
        await AssertAllAsync(client, Targets(), HttpStatusCode.Forbidden);
    }

    /// <summary>GET/HEAD가 아닌 요청에 Origin 헤더가 없으면 세션이 있어도 403을 받는지 검증한다(GET/HEAD는 Origin 검사 대상이 아니므로 제외).</summary>
    [Fact]
    public async Task MissingOrigin_Gets403_OnEveryUnsafeEndpoint_EvenWithSession()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        client.DefaultRequestHeaders.Remove("Origin");
        await AssertAllAsync(client, Targets().Where(t => t.Method is not ("GET" or "HEAD")), HttpStatusCode.Forbidden);
    }

    /// <summary>공개 호스트로는 허용 IP·CSRF 헤더·Origin을 모두 갖춰도 모든 엔드포인트가 404인지 검증한다(라우팅 자체가 관리 호스트로 제한됨).</summary>
    [Fact]
    public async Task PublicHost_Gets404_OnEveryEndpoint_EvenFromAllowedIp()
    {
        using var client = factory.CreatePublicClient();
        client.DefaultRequestHeaders.Remove(RemoteIpStartupFilter.HeaderName);
        client.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, ApiFactory.AllowedIp);
        client.DefaultRequestHeaders.Add(AdminSurfaceMiddleware.CsrfHeaderName, AdminSurfaceMiddleware.CsrfHeaderValue);
        client.DefaultRequestHeaders.Add("Origin", ApiFactory.AdminOrigin);
        await AssertAllAsync(client, Targets(), HttpStatusCode.NotFound);
    }

    /// <summary>세션 쿠키 없이는 익명 허용이 아닌 모든 엔드포인트가 바인딩 전 401을 돌려주는지 검증한다.</summary>
    [Fact]
    public async Task NoSession_Gets401_OnEveryProtectedEndpoint_BeforeBinding()
    {
        using var client = factory.CreateAdminClient();
        await AssertAllAsync(client, Targets().Where(t => !t.AllowsAnonymous), HttpStatusCode.Unauthorized);
    }

    /// <summary><c>/api</c> 밖에 매핑해도 되는 공개 라우트의 명시적 허용 목록. 여기에 없는 라우트가 생기면 테스트가 실패한다.
    /// Task 5가 공개 첨부 GET·공개 Razor 페이지·강조 CSS를 추가했다.</summary>
    private static readonly string[] PublicAllowlist =
    [
        "", // 목록(Index) — Razor 페이지의 RawText에는 앞 슬래시가 없다
        "posts/{slug}",
        "tags/{tag}",
        "series/{slug}",
        "/health",
        "/openapi/{documentName}.json", // Development에서만 매핑된다
        "/attachments/{id:guid}/{fileName}",
        "/css/highlight.css",
    ];

    /// <summary><see cref="IsUnderApi"/>가 접두사가 아니라 세그먼트 경계로 판정하는지 검증한다: <c>/api-import</c>·<c>/apifeed.json</c>처럼
    /// 글자만 같고 세그먼트가 다른 경로는 그룹 밖으로 판정해야 한다.</summary>
    /// <param name="raw">판정할 라우트 패턴 원본 텍스트.</param>
    /// <param name="expected">기대하는 <see cref="IsUnderApi"/> 결과.</param>
    [Theory]
    [InlineData("/api", true)]
    [InlineData("/api/posts", true)]
    [InlineData("/API/Posts/{id:guid}", true)]
    [InlineData("/api-import", false)]
    [InlineData("/apifeed.json", false)]
    [InlineData("/health", false)]
    [InlineData("/attachments/{id:guid}/{fileName}", false)]
    public void IsUnderApi_MatchesSegmentBoundary(string raw, bool expected) => Assert.Equal(expected, IsUnderApi(raw));

    /// <summary>Content-Type이 안 맞아 415가 나는 경로도(<see cref="Target.RequiresMultipart"/> 참조) 접근 게이트를 우회하지 않는지
    /// <c>POST /api/attachments</c>에 JSON 본문을 보내 직접 확인한다: 허용 IP 밖·CSRF 헤더 없음·공개 호스트는 각각 평소대로 403/403/404이고,
    /// 게이트를 전부 통과했는데 세션만 없으면(실측: 401 또는 415) 핸들러 자신의 문제 제목이 응답에 없다 — 핸들러가 호출되지 않았다는 증거다.
    /// <see cref="AccessMatrixTests.Build"/>가 multipart 전용 엔드포인트에는 더 이상 JSON을 보내지 않게 된 뒤(fix round 1로) 이 특정 시나리오의
    /// 커버리지가 없어졌으므로 별도로 복원한다(fix round 1, A6).</summary>
    [Fact]
    public async Task WrongContentType_FromOutside_IsStillRejectedByTheGate_AndNeverReachesTheHandler()
    {
        const string path = "/api/attachments";
        static HttpRequestMessage JsonPost() => new(HttpMethod.Post, path) { Content = new StringContent("{broken", Encoding.UTF8, "application/json") };

        using (var outsider = factory.CreateAdminClient())
        {
            outsider.DefaultRequestHeaders.Remove(RemoteIpStartupFilter.HeaderName);
            outsider.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, ApiFactory.OutsiderIp);
            using var req = JsonPost();
            using var res = await outsider.SendAsync(req);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }

        using (var noCsrf = factory.CreateAdminClient())
        {
            noCsrf.DefaultRequestHeaders.Remove(AdminSurfaceMiddleware.CsrfHeaderName);
            using var req = JsonPost();
            using var res = await noCsrf.SendAsync(req);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }

        using (var pub = factory.CreatePublicClient())
        {
            // CreatePublicClient()는 기본으로 허용되지 않은 IP를 쓴다 — 여기서는 "호스트가 관리 호스트가 아니라서 404"를 증명하려는 것이므로
            // IP 게이트가 아니라 호스트 게이트가 404를 내는지 보려면 허용 IP로 바꿔야 한다(그러지 않으면 어느 게이트가 막았는지 알 수 없다).
            pub.DefaultRequestHeaders.Remove(RemoteIpStartupFilter.HeaderName);
            pub.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, ApiFactory.AllowedIp);
            pub.DefaultRequestHeaders.Add(AdminSurfaceMiddleware.CsrfHeaderName, AdminSurfaceMiddleware.CsrfHeaderValue);
            pub.DefaultRequestHeaders.Add("Origin", ApiFactory.AdminOrigin);
            using var req = JsonPost();
            using var res = await pub.SendAsync(req);
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }

        using (var noSession = factory.CreateAdminClient())
        {
            using var req = JsonPost();
            using var res = await noSession.SendAsync(req);
            // 실측(Task 5 report 참조): Content-Type 불일치는 401 대신 415(프레임워크 기본 ProblemDetails)를 낸다;
            // Content-Type을 맞추면 같은 무세션 요청은 401이 된다(인가는 정상 적용됨, 별도 확인함). 여기서는 두 상태 코드 중 하나이기만 하면
            // 되고, 핵심 증거는 응답 본문에 핸들러 자신의 문제 제목이 없다는 것 — 핸들러가 호출되지 않았다는 뜻이다.
            Assert.Contains(res.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.UnsupportedMediaType });
            var body = await res.Content.ReadAsStringAsync();
            // "지원하지 않는 이미지"는 UploadAsync가 파일을 store.SaveAsync에 성공적으로 바인딩한 뒤에만 만드는 제목이라(JSON 본문으로는
            // 절대 거기까지 못 간다) 이 본문에 없는 것이 당연해서 이 단언만으로는 "핸들러가 호출되지 않았다"를 증명하지 못한다(fix round 2, B2).
            // 실제로 핸들러가 세션 없이도 호출됐다면 나올 첫 반응은 file 필드 누락 400(AttachmentEndpoints.cs:110-113)의 상세 문구이므로
            // 그 문구가 없는지 확인해야 진짜 판별력이 있다. 두 문구를 UploadAsync의 두 관문(바인딩 성공 뒤 415, 바인딩 자체가 안 됐을 때 400)에
            // 각각 대응시켜 둘 다 없는지 본다.
            Assert.DoesNotContain("지원하지 않는 이미지", body, StringComparison.Ordinal);
            Assert.DoesNotContain("multipart 필드 'file'", body, StringComparison.Ordinal);
        }
    }

    /// <summary><c>/api</c> 밖의 모든 라우트는 허용 목록에 있어야 하고 GET/HEAD만 받아야 한다 —
    /// 관리 핸들러를 실수로 <c>/api</c> 그룹 밖에 매핑하면(그러면 어떤 접근 검사도 받지 않는다) 여기서 잡힌다.</summary>
    [Fact]
    public void EveryRouteOutsideApi_IsOnThePublicAllowlist_AndReadOnly()
    {
        using var _ = factory.CreateClient();
        var outside = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => !IsUnderApi(e.RoutePattern.RawText ?? string.Empty))
            .ToList();
        Assert.NotEmpty(outside); // 최소한 /health는 있어야 한다(열거가 비어 통과하는 일을 막는다)
        foreach (var endpoint in outside)
        {
            // null을 ""로 뭉개면 Index 페이지(RawText == "")의 허용 목록 항목과 구분할 수 없어져, RawText가 null인 엔드포인트가
            // 조용히 통과할 수 있다 — null은 그 자체로 실패 처리한다(빈 문자열과 다른 값으로 취급).
            var raw = endpoint.RoutePattern.RawText;
            Assert.True(raw is not null, "RawText가 null인 라우트가 있다(허용 목록의 빈 문자열과 구분되지 않아 오탐할 위험).");
            Assert.True(PublicAllowlist.Contains(raw, StringComparer.Ordinal), $"허용 목록에 없는 공개 라우트: {raw}");
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
            Assert.True(methods.Count > 0 && methods.All(m => m is "GET" or "HEAD"), $"{raw}: 공개 라우트는 GET/HEAD 전용이어야 한다(실제: {string.Join(",", methods)})");
        }
    }

    /// <summary>양쪽 호스트에서 열려도 되는 <c>/api</c> 밖 라우트. 여기에 없는 공개 라우트는 전부 공개 호스트에만 매칭되어야 한다.</summary>
    private static readonly string[] SharedBetweenHosts = ["/health", "/openapi/{documentName}.json", "/attachments/{id:guid}/{fileName}"];

    /// <summary>공개 페이지·피드 라우트에 공개 호스트 제한을 빠뜨리면(그러면 관리 origin에서도 렌더링된다) 여기서 잡힌다.
    /// 속도 제한 메타데이터(<see cref="RateLimitMetadata"/>)를 빠뜨리면(그러면 무제한 요청을 받는다) 이 테스트도 함께 잡는다 —
    /// <see cref="PortfolioBlog.Api.Pages.PublicPageConvention"/>이 두 메타데이터를 같은 곳에서 걸기 때문이다.</summary>
    [Fact]
    public void EveryPublicRoute_ExceptTheSharedOnes_IsBoundToThePublicHost()
    {
        using var _ = factory.CreateClient();
        var publicHost = new Uri(ApiFactory.PublicOrigin).Host;
        var bound = 0;
        foreach (var endpoint in factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var raw = endpoint.RoutePattern.RawText;
            Assert.True(raw is not null, "RawText가 null인 라우트가 있다(공유 목록의 빈 문자열과 구분되지 않아 오탐할 위험).");
            if (IsUnderApi(raw) || SharedBetweenHosts.Contains(raw, StringComparer.Ordinal)) continue;
            var hosts = endpoint.Metadata.GetMetadata<IHostMetadata>()?.Hosts ?? [];
            Assert.True(hosts.SequenceEqual([publicHost]), $"'{raw}': 공개 호스트 제한이 없다(실제: {string.Join(",", hosts)})");
            Assert.NotNull(endpoint.Metadata.GetMetadata<RateLimitMetadata>());
            bound++;
        }
        Assert.True(bound >= 5, $"검사된 공개 라우트가 너무 적다: {bound}");
    }
}
