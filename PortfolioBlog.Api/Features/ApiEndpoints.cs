using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Features.Auth;
using PortfolioBlog.Api.Features.Posts;
using PortfolioBlog.Api.Features.Series;
using PortfolioBlog.Api.Infrastructure.Access;

namespace PortfolioBlog.Api.Features;

/// <summary><c>/api</c> 그룹을 만들고 각 기능의 엔드포인트를 등록한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 정적 클래스이며 인스턴스 상태가 없다. 등록 메서드는 앱 시작 시 단일 스레드에서 호출된다.</description></item>
/// <item><description><b>Memory Allocation:</b> 라우트 그룹 등록에 따른 시작 시 1회성 할당만 발생한다.</description></item>
/// <item><description><b>Blocking:</b> 동기 실행. I/O 없음.</description></item>
/// </list>
/// </remarks>
public static class ApiEndpoints
{
    /// <summary><c>/api</c> 라우트 그룹을 관리 호스트로 제한해 만들고, 각 기능 모듈의 엔드포인트를 등록한다.</summary>
    /// <param name="app">엔드포인트를 등록할 <see cref="WebApplication"/>.</param>
    /// <returns>등록된 <c>/api</c> 라우트 그룹.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 앱 시작 시 단일 스레드에서 1회 호출되어 라우트를 등록한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="SiteOptions.HostOf"/> 호출 1회와 라우트 그룹 등록에 따른 시작 시 할당만 발생한다.</description></item>
    /// <item><description><b>Blocking:</b> 동기 실행. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public static RouteGroupBuilder MapApiEndpoints(this WebApplication app)
    {
        var adminHost = SiteOptions.HostOf(app.Services.GetRequiredService<IOptions<SiteOptions>>().Value.AdminOrigin);
        // RequireHost: 미들웨어의 호스트 검사와 같은 규칙을 라우팅에도 걸어 둔다(미들웨어 순서를 잘못 바꿔도 공개 호스트에서는 매칭되지 않는다).
        // RequireAuthorization: 이후 추가되는 모든 /api 엔드포인트는 기본이 세션 필수다. 익명 허용은 login·me뿐이며 해당 엔드포인트가 개별적으로 AllowAnonymous()를 선언한다.
        var api = app.MapGroup("/api").RequireHost(adminHost).RequireAuthorization(AuthServiceCollectionExtensions.PolicyName);
        api.MapAuthEndpoints();
        api.MapPostEndpoints();
        api.MapSeriesEndpoints();
        return api;
    }
}
