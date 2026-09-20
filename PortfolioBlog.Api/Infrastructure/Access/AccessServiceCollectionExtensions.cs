using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace PortfolioBlog.Api.Infrastructure.Access;

/// <summary>관리 표면 접근 제어에 필요한 서비스(옵션 바인딩·IP 허용 정책·ForwardedHeaders 옵션)를 DI 컨테이너에 등록하는 확장 메서드 모음.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 정적 클래스이며 인스턴스 상태가 없다. 등록 메서드는 앱 시작 시 단일 스레드에서 호출된다.</description></item>
/// <item><description><b>Memory Allocation:</b> 시작 시 서비스 디스크립터·옵션 구성 델리게이트 등록에 따른 1회성 할당만 발생한다.</description></item>
/// <item><description><b>Blocking:</b> 동기 실행. I/O 없음.</description></item>
/// </list>
/// </remarks>
public static class AccessServiceCollectionExtensions
{
    /// <summary>Site·Admin·Proxy 옵션, IP 허용 정책, ForwardedHeaders 옵션을 등록한다. 설정 값은 전부 지연 바인딩이다.</summary>
    /// <param name="services">등록 대상 서비스 컬렉션.</param>
    /// <param name="configuration">설정 섹션을 읽어올 <see cref="IConfiguration"/>.</param>
    /// <returns>체이닝을 위해 그대로 반환하는 <paramref name="services"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 앱 시작 시 단일 스레드에서 1회 호출된다. 등록되는 <see cref="IAdminAccessPolicy"/>는 싱글턴이며 내부적으로 Thread-safe하게 구현되어 있다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 옵션 바인딩·서비스 디스크립터 등록에 따른 시작 시 1회성 할당만 발생한다. 실제 설정 값 파싱(예: <see cref="CidrList.Parse"/>)은 여기서 일어나지 않고 첫 해석 시점으로 지연된다.</description></item>
    /// <item><description><b>Blocking:</b> 동기 실행. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public static IServiceCollection AddAdminAccess(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SiteOptions>(configuration.GetSection(SiteOptions.SectionName));
        services.Configure<AdminOptions>(configuration.GetSection(AdminOptions.SectionName));
        services.Configure<ProxyOptions>(configuration.GetSection(ProxyOptions.SectionName));
        services.AddSingleton<IAdminAccessPolicy, IpAllowlistAdminAccessPolicy>();

        services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<ProxyOptions>>((o, proxy) =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // ForwardLimit=1: 직전 홉(Caddy)이 붙인 맨 오른쪽 항목 하나만 본다. 그 왼쪽은 클라이언트가 위조할 수 있다.
            o.ForwardLimit = 1;
            // 기본값(루프백)을 지우고 Caddy 고정 IP 하나만 신뢰한다. .NET 10에서 KnownNetworks는 obsolete라 KnownIPNetworks를 쓴다.
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
            // IPAddress.TryParse: 문자열을 즉시 파싱해 실패 시 예외 없이 false를 반환하므로 시작 경로에서 형식 오류로 죽지 않고
            // (형식 검증은 StartupValidation이 별도로 책임진다) 유효한 값만 신뢰 목록에 반영한다.
            if (IPAddress.TryParse(proxy.Value.TrustedIp, out var ip))
            {
                o.KnownProxies.Add(ip);
            }
        });
        return services;
    }

    /// <summary>신뢰 프록시가 설정된 경우에만 ForwardedHeaders 미들웨어를 등록한다.</summary>
    /// <param name="app">미들웨어를 등록할 <see cref="WebApplication"/>.</param>
    /// <returns>체이닝을 위해 그대로 반환하는 <paramref name="app"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 앱 시작 시 단일 스레드에서 1회 호출되어 파이프라인을 구성한다. 등록되는 <c>ForwardedHeadersMiddleware</c> 자체는 Thread-safe하다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 조건 판정을 위한 <see cref="IOptions{TOptions}"/> 조회 1회. 미들웨어를 등록하는 경우 파이프라인 델리게이트 1개가 추가된다.</description></item>
    /// <item><description><b>Blocking:</b> 동기 실행. I/O 없음.</description></item>
    /// </list>
    /// ForwardedHeadersMiddleware는 KnownIPNetworks와 KnownProxies가 <b>둘 다 비어 있으면 송신자 검사를 생략</b>하고 모든 X-Forwarded-For를 신뢰한다.
    /// 그래서 설정이 비었을 때 미들웨어를 등록하면 외부에서 헤더를 위조해 IP 검사를 우회할 수 있다. 비어 있으면 아예 넣지 않는다.
    /// </remarks>
    public static WebApplication UseTrustedForwardedHeaders(this WebApplication app)
    {
        if (app.Services.GetRequiredService<IOptions<ProxyOptions>>().Value.TrustedIp.Length > 0)
        {
            app.UseForwardedHeaders();
        }
        return app;
    }
}
