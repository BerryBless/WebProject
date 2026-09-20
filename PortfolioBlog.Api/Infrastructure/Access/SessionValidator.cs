using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Domain;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Infrastructure.Access;

/// <summary>쿠키 티켓을 요청마다 서버 상태와 대조한다(절대 만료·비밀번호 지문·세션 epoch). 어긋나면 주체를 거부하고 쿠키를 지운다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> 인증 미들웨어가 요청 스레드에서 호출한다. 쿠키가 없는 요청에서는 호출되지 않는다.</description></item>
/// <item><description><b>Memory Policy:</b> 요청당 DbContext 스코프 조회 1회(단일 행 int 프로젝션).</description></item>
/// <item><description><b>Concurrency:</b> Thread-safe(무상태). Non-blocking: DB 조회를 await 한다. 관리 트래픽은 작성자 1명이라 캐시를 두지 않는다 — 캐시가 있으면 로그아웃 직후에도 폐기된 쿠키가 잠시 통한다.</description></item>
/// </list>
/// </remarks>
public static class SessionValidator
{
    /// <summary><see cref="CookieAuthenticationOptions.Events"/>의 <c>OnValidatePrincipal</c>로 등록되는 검증 델리게이트.
    /// 요청마다 티켓의 발급 시각·지문·epoch를 현재 서버 상태와 비교해 어긋나면 즉시 로그아웃시킨다.</summary>
    /// <param name="context">쿠키 인증 미들웨어가 넘겨주는, 복호화된 티켓과 <see cref="HttpContext"/>를 담은 컨텍스트.</param>
    /// <returns>검증(및 필요 시 거부·로그아웃 처리)이 완료되면 끝나는 작업.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> 쿠키 인증 미들웨어가 요청 파이프라인 스레드에서 호출한다. 유효한 <c>__Host-AdminSession</c> 쿠키가 붙은 요청마다 매번 실행된다.</description></item>
    /// <item><description><b>Memory Policy:</b> <see cref="AppDbContext"/>는 요청 DI 스코프에서 조회하며 소유권을 갖지 않는다(스코프 종료 시 프레임워크가 해제). 조회 결과는 <c>int</c> 값 1개뿐이다.</description></item>
    /// <item><description><b>Concurrency:</b> Thread-safe. 인스턴스 상태가 없는 정적 메서드다. Non-blocking: DB 조회를 <c>await</c>하며 요청 스레드를 점유하지 않는다.</description></item>
    /// </list>
    /// </remarks>
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var services = context.HttpContext.RequestServices;
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var lifetime = TimeSpan.FromHours(services.GetRequiredService<IOptions<AdminOptions>>().Value.SessionHours);
        var credential = services.GetRequiredService<AdminCredential>();
        var epoch = await services.GetRequiredService<AppDbContext>().AdminStates.AsNoTracking()
            .Where(s => s.Id == AdminState.SingletonId)
            .Select(s => s.SessionEpoch)
            .SingleAsync(context.HttpContext.RequestAborted);

        var valid = SessionRules.IsValid(
            context.Properties.IssuedUtc, now, lifetime,
            context.Principal?.FindFirst(SessionRules.FingerprintClaim)?.Value, credential.Fingerprint,
            context.Principal?.FindFirst(SessionRules.EpochClaim)?.Value, epoch);

        if (!valid)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(AuthServiceCollectionExtensions.Scheme);
        }
    }
}
