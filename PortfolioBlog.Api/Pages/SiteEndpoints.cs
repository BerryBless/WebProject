using System.Text;
using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Infrastructure.Access;
using PortfolioBlog.Api.Infrastructure.Markdown;
using PortfolioBlog.Api.Infrastructure.Web;

namespace PortfolioBlog.Api.Pages;

/// <summary>HTML이 아닌 공개 엔드포인트. 전부 GET/HEAD·공개 호스트 전용이며 <c>AccessMatrixTests.PublicAllowlist</c>에 올라 있다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 정적 클래스이며 등록되는 핸들러는 요청 컨텍스트만 다룬다.</description></item>
/// <item><description><b>Memory Allocation:</b> 등록 자체는 시작 시 1회. 핸들러 호출당 추가 할당은 <see cref="MapPublicSiteEndpoints"/> 안 람다 문서 참조.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O 없음(<see cref="HighlightCss.Value"/>는 지연 계산된 캐시 문자열을 반환할 뿐이다).</description></item>
/// </list>
/// </remarks>
public static class SiteEndpoints
{
    /// <summary>코드 강조 CSS 경로.</summary>
    public const string HighlightCssPattern = "/css/highlight.css";

    /// <summary>강조 CSS GET/HEAD 엔드포인트를 등록한다.</summary>
    /// <param name="app">엔드포인트를 등록할 <see cref="WebApplication"/>.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 앱 시작 시 단일 스레드에서 1회 호출된다. 등록된 핸들러는 여러 요청 스레드가 동시에 호출해도 안전하다(무상태, <see cref="HighlightCss.Value"/> 참조).</description></item>
    /// <item><description><b>Memory Allocation:</b> 요청마다 <see cref="HighlightCss.Value"/>(캐시된 문자열 참조, 추가 할당 없음)를 <see cref="Results.Text(string, string?, Encoding?)"/>가 감싸는 <c>ContentHttpResult</c> 1개로 반환한다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public static void MapPublicSiteEndpoints(this WebApplication app)
    {
        var publicHost = SiteOptions.HostOf(app.Services.GetRequiredService<IOptions<SiteOptions>>().Value.PublicOrigin);

        app.MapMethods(HighlightCssPattern, ["GET", "HEAD"], static (HttpContext http) =>
            {
                http.Response.Headers.CacheControl = "public, max-age=86400";
                return TypedResults.Text(HighlightCss.Value, "text/css", Encoding.UTF8);
            })
            .RequireHost(publicHost).AllowAnonymous()
            .WithMetadata(new RateLimitMetadata(RateLimitPolicy.PublicAsset)).WithName("GetHighlightCss");
    }
}
