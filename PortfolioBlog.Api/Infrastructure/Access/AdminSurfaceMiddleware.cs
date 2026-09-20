using Microsoft.Extensions.Options;

namespace PortfolioBlog.Api.Infrastructure.Access;

/// <summary><c>/api</c> 요청을 본문을 읽기 전에 걸러 낸다: 관리 호스트가 아니면 404, 허용 IP가 아니면 403,
/// CSRF 헤더가 없으면 403, 안전하지 않은 메서드인데 Origin이 관리 origin이 아니면 403.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> 요청 파이프라인 스레드. 엔드포인트 실행(=인수 바인딩·본문 읽기)보다 앞이므로 거부된 요청의 본문은 읽히지 않는다.
/// 최소 API의 엔드포인트 필터는 바인딩 <b>이후</b>에 실행되기 때문에 필터가 아니라 미들웨어로 구현한다.</description></item>
/// <item><description><b>Memory Policy:</b> 통과 경로는 Zero-allocation(문자열 비교만). 거부 시 ProblemDetails 결과 1개 할당.</description></item>
/// <item><description><b>Concurrency:</b> Thread-safe. 생성 시 고정한 불변 필드만 읽는다. Non-blocking.</description></item>
/// </list>
/// IP 허용만으로는 CSRF를 막지 못한다(허용 네트워크 안의 브라우저가 악성 사이트를 열면 그 요청도 허용 IP에서 온다).
/// 브라우저는 교차 출처 요청에 커스텀 헤더를 붙이려면 CORS 프리플라이트를 통과해야 하고 이 앱은 CORS를 등록하지 않으므로
/// <c>X-Requested-With</c> 필수 검사로 교차 출처 폼 POST·multipart가 차단된다. Origin 검사는 같은 사이트의 다른 origin(공개 도메인)을 막는 2차 방어다.
/// </remarks>
public sealed class AdminSurfaceMiddleware
{
    /// <summary>브라우저가 교차 출처 요청에는 붙일 수 없는(=CORS 프리플라이트 필요) CSRF 방어용 헤더 이름.</summary>
    public const string CsrfHeaderName = "X-Requested-With";

    /// <summary><see cref="CsrfHeaderName"/> 헤더가 가져야 하는 값.</summary>
    public const string CsrfHeaderValue = "XMLHttpRequest";

    // PathString: 문자열 비교 시 세그먼트 경계를 인식하는 값 타입이라 "/apix" 같은 부분 문자열 오탐 없이 "/api"만 정확히 매칭한다.
    // 정적 readonly로 두어 요청마다 새로 만들지 않는다.
    private static readonly PathString ApiPrefix = new("/api");

    private readonly RequestDelegate _next;
    private readonly IAdminAccessPolicy _policy;
    private readonly string _adminHost;
    private readonly string _adminOrigin;

    /// <summary>파이프라인 구성 시 다음 미들웨어 델리게이트와 필요한 서비스를 주입받는다.</summary>
    /// <param name="next">이 미들웨어를 통과한 요청이 이어서 실행할 다음 파이프라인 단계.</param>
    /// <param name="policy">원본 IP가 관리 표면에 접근 가능한지 판정하는 정책.</param>
    /// <param name="site"><c>Site:AdminOrigin</c> 설정. 관리 호스트·Origin 비교 기준을 여기서 1회만 계산해 캐시한다.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> ASP.NET Core가 미들웨어 파이프라인 구성 시 1회 호출한다. 이후 <see cref="InvokeAsync"/>는 여러 요청 스레드에서 동시에 호출되지만 이 생성자에서 캡처한 필드는 전부 불변이라 안전하다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="SiteOptions.HostOf"/> 호출로 1회성 <see cref="Uri"/> 파싱 할당. 이후 요청마다 재사용된다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public AdminSurfaceMiddleware(RequestDelegate next, IAdminAccessPolicy policy, IOptions<SiteOptions> site)
    {
        _next = next;
        _policy = policy;
        _adminOrigin = site.Value.AdminOrigin;
        _adminHost = SiteOptions.HostOf(_adminOrigin);
    }

    /// <summary>요청이 <c>/api</c>로 시작하면 호스트·IP·CSRF 헤더·Origin을 순서대로 검사하고, 하나라도 실패하면 본문을 읽지 않고 즉시 거부한다.</summary>
    /// <param name="context">현재 HTTP 요청 컨텍스트.</param>
    /// <returns>다음 미들웨어(통과 시) 또는 거부 응답 작성(실패 시)이 완료되면 끝나는 작업.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 인스턴스 필드는 불변이고 <paramref name="context"/>는 요청마다 새로 전달된다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 통과 경로는 문자열 비교만 하여 Zero-allocation. 거부 경로는 <see cref="Results.Problem"/> 결과 1개를 할당한다.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 내부에서 동기 I/O·블로킹 호출을 하지 않는다.</description></item>
    /// </list>
    /// </remarks>
    public Task InvokeAsync(HttpContext context)
    {
        // StartsWithSegments는 기본이 OrdinalIgnoreCase이고 세그먼트 경계를 지킨다("/apix"는 불일치, "/API/x"는 일치). 라우팅의 대소문자 무시와 같은 기준이다.
        if (!context.Request.Path.StartsWithSegments(ApiPrefix))
        {
            return _next(context);
        }

        context.Response.Headers.CacheControl = "no-store";

        if (!string.Equals(context.Request.Host.Host, _adminHost, StringComparison.OrdinalIgnoreCase))
        {
            return Reject(context, StatusCodes.Status404NotFound, "찾을 수 없음", null);
        }
        if (!_policy.IsAllowed(context))
        {
            return Reject(context, StatusCodes.Status403Forbidden, "접근 거부", "이 네트워크에서는 관리 기능을 쓸 수 없습니다.");
        }
        if (!string.Equals(context.Request.Headers[CsrfHeaderName], CsrfHeaderValue, StringComparison.Ordinal))
        {
            return Reject(context, StatusCodes.Status403Forbidden, "교차 출처 요청 거부", $"{CsrfHeaderName}: {CsrfHeaderValue} 헤더가 필요합니다.");
        }
        var method = context.Request.Method;
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method)
            && !string.Equals(context.Request.Headers.Origin, _adminOrigin, StringComparison.Ordinal))
        {
            return Reject(context, StatusCodes.Status403Forbidden, "교차 출처 요청 거부", "Origin 헤더가 관리 origin과 일치해야 합니다.");
        }
        return _next(context);
    }

    /// <summary>ProblemDetails 형식으로 요청을 거부하는 응답을 작성한다.</summary>
    /// <param name="context">현재 HTTP 요청 컨텍스트.</param>
    /// <param name="status">응답할 HTTP 상태 코드.</param>
    /// <param name="title">ProblemDetails의 title.</param>
    /// <param name="detail">ProblemDetails의 detail. null이면 생략된다.</param>
    /// <returns>응답 작성이 완료되면 끝나는 작업.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 정적 메서드로 공유 상태가 없다. 매 호출이 독립적이다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="Results.Problem"/> 결과 객체 1개와 직렬화 버퍼를 할당한다.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 응답 스트림 쓰기를 <c>await</c> 가능한 <see cref="Task"/>로 반환한다.</description></item>
    /// </list>
    /// </remarks>
    private static Task Reject(HttpContext context, int status, string title, string? detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail).ExecuteAsync(context);
}
