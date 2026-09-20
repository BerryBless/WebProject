using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>요청 헤더 <c>X-Test-Remote-Ip</c> 값을 <c>Connection.RemoteIpAddress</c>에 넣는 테스트 전용 미들웨어를
/// 앱 파이프라인 <b>앞</b>에 등록한다(TestServer는 RemoteIpAddress가 null이다. <see cref="IStartupFilter"/>는 Program.cs의 미들웨어보다 먼저 실행된다).</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> ASP.NET Core 호스트 빌드 단계에서 1회 호출되어 파이프라인 델리게이트를 구성한다.
/// 이후 <see cref="Configure"/>가 반환한 델리게이트는 요청 파이프라인 스레드에서 매 요청 실행된다.</description></item>
/// <item><description><b>Memory Policy:</b> 요청마다 <see cref="IPAddress.TryParse(string?, out IPAddress?)"/> 성공 시 <see cref="IPAddress"/> 1개를 할당한다. 그 외 추가 할당 없음.</description></item>
/// <item><description><b>Concurrency:</b> Thread-safe. 공유 가변 상태 없이 매 요청 컨텍스트만 다룬다. Non-blocking.</description></item>
/// </list>
/// </remarks>
public sealed class RemoteIpStartupFilter : IStartupFilter
{
    /// <summary>원격 IP를 실어 보내는 테스트 전용 헤더 이름.</summary>
    public const string HeaderName = "X-Test-Remote-Ip";

    /// <summary>기존 파이프라인 구성 델리게이트 앞에 원격 IP 주입 미들웨어를 추가한다.</summary>
    /// <param name="next">Program.cs가 구성하는 원래 파이프라인 구성 델리게이트.</param>
    /// <returns>원격 IP 주입 미들웨어를 먼저 등록한 뒤 <paramref name="next"/>를 실행하는 구성 델리게이트.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 호스트 빌드 스레드에서 1회 호출된다. 반환하는 델리게이트는 상태를 캡처하지 않는다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 델리게이트 캡처 1회 및 내부 <c>app.Use</c> 등록 시 할당. 요청당 추가 할당은 <see cref="IPAddress"/> 파싱 성공 시에만 발생.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((ctx, nextMiddleware) =>
        {
            if (ctx.Request.Headers.TryGetValue(HeaderName, out var raw) && IPAddress.TryParse(raw.ToString(), out var ip))
            {
                ctx.Connection.RemoteIpAddress = ip;
            }
            return nextMiddleware(ctx);
        });
        next(app);
    };
}
