namespace PortfolioBlog.Api.Infrastructure.Web;

/// <summary>모든 응답(라우트 제약 실패 404·예외 500 포함)에 기준 보안 헤더를 붙인다(스펙 3.6).</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 생성자에서 캡처하는 <c>_hsts</c>는 이후 변경되지 않는 불변 필드이고, <see cref="InvokeAsync"/>는 요청마다 새로 전달되는 <see cref="HttpContext"/>만 다룬다.</description></item>
/// <item><description><b>Memory Allocation:</b> 통과 경로는 <see cref="InvokeAsync"/>당 <c>OnStarting</c> 콜백에 넘길 state 튜플 1개만 할당한다(콜백 자체는 <c>static</c> 람다라 클로저를 만들지 않는다). 헤더 값은 전부 상수 참조라 추가 문자열 할당이 없다.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). <c>next(context)</c>를 그대로 반환해 나머지 파이프라인을 이어간다.</description></item>
/// </list>
/// </remarks>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, IHostEnvironment environment)
{
    /// <summary>공개 HTML의 CSP(스펙 3.6). 스크립트 출처가 아예 없다. JSON·XML·오류 응답에도 같은 값을 쓴다(더 엄격해서 나쁠 것이 없다).</summary>
    public const string PublicCsp =
        "default-src 'none'; img-src 'self'; style-src 'self'; font-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";

    /// <summary>브라우저 기능 전부 비활성.</summary>
    public const string PermissionsPolicy =
        "accelerometer=(), autoplay=(), camera=(), display-capture=(), encrypted-media=(), fullscreen=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), midi=(), payment=(), picture-in-picture=(), publickey-credentials-get=(), screen-wake-lock=(), usb=(), xr-spatial-tracking=()";

    // HSTS는 TLS 종단(Caddy) 뒤에서만 의미가 있다. Development는 http 프로필로도 뜨므로 붙이지 않는다.
    private readonly bool _hsts = !environment.IsDevelopment();

    /// <summary>응답 전송 직전(<c>OnStarting</c>)에 기준 보안 헤더를 붙이도록 예약하고 다음 미들웨어를 호출한다.</summary>
    /// <param name="context">현재 HTTP 요청 컨텍스트.</param>
    /// <returns>다음 미들웨어가 완료되면 끝나는 작업.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. <paramref name="context"/>는 요청마다 새로 전달되며 이 메서드가 건드리는 상태는 그 컨텍스트에 한정된다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <c>OnStarting</c>에 넘기는 state는 <c>(IHeaderDictionary, bool)</c> 값 튜플이며 <c>object</c> 매개변수로 전달되면서 박싱되어 힙에 1회 할당된다. 콜백 자체는 <c>static</c> 람다라 별도 클로저 할당은 없다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). 예외 처리 미들웨어가 <c>Response.Clear()</c>로 헤더를 비운 뒤에도 <c>OnStarting</c> 콜백은 전송 직전에 다시 실행되므로(실측: <c>UnhandledException_500_StillCarriesSecurityHeaders</c>), 헤더를 여기서 직접 설정하는 방식과 달리 500 응답에서도 헤더가 남는다.</description></item>
    /// </list>
    /// </remarks>
    public Task InvokeAsync(HttpContext context)
    {
        // OnStarting: 예외 처리 미들웨어가 Response.Clear()로 헤더를 비운 뒤에도 전송 직전에 다시 실행된다(직접 넣으면 500에서 사라진다).
        context.Response.OnStarting(static state =>
        {
            var (headers, hsts) = ((IHeaderDictionary, bool))state;
            headers.XContentTypeOptions = "nosniff";
            headers.TryAdd("Content-Security-Policy", PublicCsp); // 첨부 핸들러가 넣은 sandbox CSP는 덮어쓰지 않는다
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Permissions-Policy"] = PermissionsPolicy;
            headers.XFrameOptions = "DENY";
            if (hsts) headers.StrictTransportSecurity = "max-age=31536000; includeSubDomains";
            return Task.CompletedTask;
        }, (context.Response.Headers, _hsts));
        return next(context);
    }
}
