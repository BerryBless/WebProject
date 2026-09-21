using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Routing;
using PortfolioBlog.Api.Infrastructure.Web;

namespace PortfolioBlog.Api.Pages;

/// <summary>모든 Razor 페이지 엔드포인트에 공개 표면의 계약을 메타데이터로 건다: GET/HEAD 전용, 공개 호스트 전용, 속도 제한 정책.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe한 구성 단계에서만 쓰인다. <see cref="Apply"/>는 앱 시작 시 라우트 모델 구성 과정에서 단일 스레드로 호출되며,
/// 이후 요청 처리 중에는 이 클래스가 다시 실행되지 않는다(집행은 프레임워크 라우팅이 메타데이터를 읽어서 한다).</description></item>
/// <item><description><b>Memory Allocation:</b> 페이지·선택자 수만큼 <see cref="HttpMethodMetadata"/>·<see cref="HostAttribute"/>·<see cref="RateLimitMetadata"/> 인스턴스를 시작 시 1회씩 할당한다. 요청마다 할당하지 않는다.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O 없음.</description></item>
/// </list>
/// Razor 페이지는 기본적으로 모든 HTTP 메서드·모든 호스트에 매칭된다. 선택자의 엔드포인트 메타데이터에 넣으면 라우팅이 직접 집행한다
/// (실측: POST → 405 + Allow, 다른 호스트 → 404). 페이지마다 특성을 다는 방식은 새 페이지에서 빠뜨릴 수 있어 규약으로 한 번에 건다 —
/// 빠뜨린 경우는 <c>AccessMatrixTests</c>의 닫힌 세계 검사가 잡는다.
/// </remarks>
public sealed class PublicPageConvention(string publicHost) : IPageRouteModelConvention
{
    /// <summary>페이지 라우트 모델 하나에 GET/HEAD·공개 호스트·속도 제한 메타데이터를 추가한다.</summary>
    /// <param name="model">프레임워크가 구성 중인 페이지 라우트 모델.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not Thread-safe. 프레임워크가 앱 시작 시 단일 스레드에서만 호출한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 선택자 수만큼 메타데이터 인스턴스 3개씩 할당한다(시작 시 1회).</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking).</description></item>
    /// </list>
    /// </remarks>
    public void Apply(PageRouteModel model)
    {
        var policy = string.Equals(model.ViewEnginePath, "/Search", StringComparison.Ordinal) ? RateLimitPolicy.Search : RateLimitPolicy.PublicPage;
        foreach (var selector in model.Selectors)
        {
            selector.EndpointMetadata.Add(new HttpMethodMetadata(["GET", "HEAD"]));
            selector.EndpointMetadata.Add(new HostAttribute(publicHost));
            selector.EndpointMetadata.Add(new RateLimitMetadata(policy));
        }
    }
}
