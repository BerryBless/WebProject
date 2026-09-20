using PortfolioBlog.Api.Contracts;

namespace PortfolioBlog.Api.Features.Auth;

/// <summary><c>/api/auth</c> 하위 인증 관련 엔드포인트를 등록한다. 이 단계에서는 익명 상태 조회(<c>/me</c>)만 제공한다(로그인·세션은 Task 4).</summary>
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
    /// <summary><c>/auth</c> 그룹을 만들고 <c>GET /me</c>를 등록한다.</summary>
    /// <param name="api"><c>/api</c> 루트 그룹.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 앱 시작 시 단일 스레드에서 1회 호출되어 라우트를 등록한다. 등록되는 엔드포인트 델리게이트는 요청마다 병렬로 안전하게 호출된다(공유 가변 상태 없음).</description></item>
    /// <item><description><b>Memory Allocation:</b> 라우트 그룹·엔드포인트 등록에 따른 시작 시 1회성 할당만 발생한다. 요청당 <c>GET /me</c> 처리는 <see cref="AuthStatusDto"/> 1개만 할당한다.</description></item>
    /// <item><description><b>Blocking:</b> 동기 실행. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public static void MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth");
        auth.MapGet("/me", (HttpContext ctx) => TypedResults.Ok(new AuthStatusDto(ctx.User.Identity?.IsAuthenticated ?? false)))
            .WithName("GetAuthStatus");
    }
}
