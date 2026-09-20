using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Domain;
using PortfolioBlog.Api.Infrastructure.Access;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Features.Auth;

/// <summary><c>/api/auth</c> 하위 인증 관련 엔드포인트(로그인·로그아웃·상태 조회)를 등록한다. 로그인·상태 조회만 익명 접근을 허용하고, 그 외 <c>/api</c> 엔드포인트는 세션을 요구한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 정적 클래스이며 인스턴스 상태가 없다. 등록 메서드는 앱 시작 시 단일 스레드에서 호출된다.</description></item>
/// <item><description><b>Memory Allocation:</b> 라우트 등록 시 1회성 할당만 발생한다.</description></item>
/// <item><description><b>Blocking:</b> 동기 실행. I/O 없음.</description></item>
/// </list>
/// </remarks>
public static class AuthEndpoints
{
    /// <summary>로그인 요청 본문의 <c>password</c> 필드 최대 길이(문자). 이보다 길면 해싱 전에 400으로 거부한다.</summary>
    public const int PasswordMaxLength = 256;

    /// <summary><c>/auth</c> 그룹을 만들고 <c>GET /me</c>·<c>POST /login</c>·<c>POST /logout</c>을 등록한다.</summary>
    /// <param name="api"><c>/api</c> 루트 그룹.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 앱 시작 시 단일 스레드에서 1회 호출되어 라우트를 등록한다. 등록되는 엔드포인트 델리게이트는 요청마다 병렬로 안전하게 호출된다(공유 가변 상태 없음, 의존성은 요청 스코프로 주입).</description></item>
    /// <item><description><b>Memory Allocation:</b> 라우트 그룹·엔드포인트 등록에 따른 시작 시 1회성 할당만 발생한다.</description></item>
    /// <item><description><b>Blocking:</b> 동기 실행. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public static void MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth");
        auth.MapGet("/me", (HttpContext ctx) => TypedResults.Ok(new AuthStatusDto(ctx.User.Identity?.IsAuthenticated ?? false)))
            .AllowAnonymous().WithName("GetAuthStatus");
        // WithMetadata: 속도 제한기가 요청 경로 문자열이 아니라 실제로 선택된 엔드포인트로 로그인 요청을 식별하게 한다(LoginRateLimitMetadata 참고).
        auth.MapPost("/login", LoginAsync).AllowAnonymous().WithName("Login").WithMetadata(new LoginRateLimitMetadata());
        auth.MapPost("/logout", LogoutAsync).WithName("Logout");
    }

    /// <summary>비밀번호만으로 로그인한다(아이디 없음, 단일 작성자). 성공하면 비밀번호 지문과 현재 세션 epoch를 담은 <c>__Host-</c> 세션 쿠키를 내려준다.</summary>
    /// <param name="request">로그인 요청 본문.</param>
    /// <param name="http">현재 요청의 <see cref="HttpContext"/>(로그인 쿠키 발급에 필요).</param>
    /// <param name="credential">설정된 비밀번호 해시를 검증하는 자격 증명기.</param>
    /// <param name="db">현재 세션 epoch를 읽어올 DbContext.</param>
    /// <param name="clock">쿠키 티켓의 발급 시각(<c>IssuedUtc</c>)을 고정할 시계.</param>
    /// <param name="loggers">로그인 성공·실패를 원본 IP만 남기고 기록할 로거 팩토리.</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>성공 시 204, 검증 실패 시 401 <c>ProblemDetails</c>, 입력 형식 오류 시 400.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> ASP.NET Core 요청 파이프라인 스레드에서 호출된다. <see cref="AdminCredential.Verify"/>가 CPU 바운드 동기 블로킹을 수행하므로 상위 속도 제한기(<see cref="AuthServiceCollectionExtensions.AddAdminAuth"/>의 <c>login-concurrency</c> 파티션)가 동시 실행 수를 묶는다.</description></item>
    /// <item><description><b>Memory Policy:</b> 요청당 클레임·<see cref="ClaimsIdentity"/>·<see cref="ClaimsPrincipal"/> 각 1개를 할당한다(성공 시). 비밀번호 문자열은 요청 파이프라인 종료 후 GC 대상이며 별도로 보관하지 않는다.</description></item>
    /// <item><description><b>Concurrency:</b> Thread-safe. 요청 스코프 의존성만 사용해 다른 요청과 공유 가변 상태가 없다. Non-blocking: DB 조회는 <c>await</c>하지만 비밀번호 검증 자체는 동기 블로킹이다(위 참조).</description></item>
    /// </list>
    /// </remarks>
    private static async Task<IResult> LoginAsync(LoginRequest request, HttpContext http, AdminCredential credential,
        AppDbContext db, TimeProvider clock, ILoggerFactory loggers, CancellationToken ct)
    {
        if (request.Password is null || request.Password.Length > PasswordMaxLength)
        {
            // 길이 상한: 해시 입력을 제한해 거대한 본문으로 PBKDF2 시간을 늘리는 공격을 막는다.
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["password"] = [$"비밀번호는 필수이며 {PasswordMaxLength}자 이하여야 합니다."],
            });
        }
        var logger = loggers.CreateLogger("PortfolioBlog.Api.Auth");
        if (!credential.Verify(request.Password))
        {
            logger.LogWarning("관리자 로그인 실패. RemoteIp={RemoteIp}", http.Connection.RemoteIpAddress); // 비밀번호·본문은 기록하지 않는다
            return TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "로그인 실패");
        }

        var epoch = await db.AdminStates.AsNoTracking().Where(s => s.Id == AdminState.SingletonId).Select(s => s.SessionEpoch).SingleAsync(ct);
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "admin"),
            new Claim(SessionRules.FingerprintClaim, credential.Fingerprint),
            new Claim(SessionRules.EpochClaim, epoch.ToString(CultureInfo.InvariantCulture)),
        ], AuthServiceCollectionExtensions.Scheme);
        // IsPersistent=false: 브라우저를 닫으면 사라지는 세션 쿠키. IssuedUtc를 주입한 시계로 고정해 절대 만료 판정의 기준으로 쓴다.
        await http.SignInAsync(AuthServiceCollectionExtensions.Scheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = false, IssuedUtc = clock.GetUtcNow() });
        logger.LogInformation("관리자 로그인 성공. RemoteIp={RemoteIp}", http.Connection.RemoteIpAddress);
        return TypedResults.NoContent();
    }

    /// <summary>로그아웃한다. 단일 작성자 정책상 로그아웃은 "이 브라우저만"이 아니라 서버 측 세션 epoch를 올려 그 전에 발급된 모든 세션(복사된 쿠키 포함)을 함께 폐기한다.</summary>
    /// <param name="http">현재 요청의 <see cref="HttpContext"/>(쿠키 삭제에 필요).</param>
    /// <param name="db">세션 epoch를 원자적으로 증가시킬 DbContext.</param>
    /// <param name="loggers">로그아웃을 원본 IP만 남기고 기록할 로거 팩토리.</param>
    /// <param name="ct">요청 취소 토큰.</param>
    /// <returns>항상 204(엔드포인트 자체가 인가 정책으로 보호되어 세션 없는 요청은 여기 도달하지 못한다).</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> ASP.NET Core 요청 파이프라인 스레드에서 호출된다. 이 엔드포인트는 <c>/api</c> 그룹의 인가 정책으로 보호되어 유효한 세션이 있어야 도달한다.</description></item>
    /// <item><description><b>Memory Policy:</b> 추가 엔티티 그래프 로드 없이 <c>ExecuteUpdateAsync</c>로 UPDATE 1건만 보낸다(변경 추적기 미사용).</description></item>
    /// <item><description><b>Concurrency:</b> Thread-safe. <c>SessionEpoch = SessionEpoch + 1</c>은 DB가 실행하는 원자적 증가라 동시에 여러 요청이 로그아웃해도 갱신이 소실되지 않는다. Non-blocking: 모든 I/O를 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    private static async Task<IResult> LogoutAsync(HttpContext http, AppDbContext db, ILoggerFactory loggers, CancellationToken ct)
    {
        // 단일 작성자 정책: 로그아웃은 "모든 세션 폐기"다. 원자적 증가라 동시 로그아웃에도 안전하다.
        await db.AdminStates.Where(s => s.Id == AdminState.SingletonId)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.SessionEpoch, s => s.SessionEpoch + 1), ct);
        await http.SignOutAsync(AuthServiceCollectionExtensions.Scheme);
        loggers.CreateLogger("PortfolioBlog.Api.Auth").LogInformation("관리자 로그아웃(전 세션 폐기). RemoteIp={RemoteIp}", http.Connection.RemoteIpAddress);
        return TypedResults.NoContent();
    }
}
