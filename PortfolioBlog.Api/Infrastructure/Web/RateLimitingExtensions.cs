using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Infrastructure.Access;

namespace PortfolioBlog.Api.Infrastructure.Web;

/// <summary>앱 전체의 속도 제한 체인을 등록한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 등록은 시작 시 1회. 제한기 자체는 프레임워크가 Thread-safe하게 관리한다.</description></item>
/// <item><description><b>Memory Allocation:</b> 파티션(키)마다 제한기 1개. 로그인 IP 파티션은 허용 IP 수로, 나머지는 상수로 한정된다.</description></item>
/// <item><description><b>Blocking:</b> 대기열 0 — 한도를 넘으면 기다리지 않고 즉시 429.</description></item>
/// </list>
/// 미들웨어 위치는 <c>AdminSurfaceMiddleware</c> 뒤다: 허용 IP 밖의 요청이 한도를 소진하지 못한다.
/// <c>WebApplication</c>은 라우팅을 사용자 미들웨어보다 앞에 두므로 제한기가 돌 때 <c>GetEndpoint()</c>는 이미 채워져 있다.
/// </remarks>
public static class RateLimitingExtensions
{
    /// <summary>제한기가 창 종료 시각(<see cref="MetadataName.RetryAfter"/>)을 주지 않을 때 <c>Retry-After</c> 헤더에 쓸 보수적 기본값(초).</summary>
    private const int FallbackRetryAfterSeconds = 60;

    /// <summary>로그인 IP별·전역 고정 창 + 동시 실행 제한을 체인으로 등록한다. 전 정책 공통 429 상태 코드와 <c>Retry-After</c> 헤더도 여기서 구성한다.</summary>
    /// <param name="services">등록 대상 서비스 컬렉션.</param>
    /// <returns>체이닝을 위해 그대로 반환하는 <paramref name="services"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 앱 시작 시 단일 스레드에서 1회 호출된다. 등록되는 <see cref="PartitionedRateLimiter{TResource}"/> 체인은 싱글턴이며 프레임워크가 Thread-safe하게 관리한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 옵션 바인딩·서비스 디스크립터 등록에 따른 시작 시 1회성 할당만 발생한다. 실제 파티션(키)별 제한기 인스턴스는 요청이 그 파티션을 처음 칠 때 지연 생성된다.</description></item>
    /// <item><description><b>Blocking:</b> 동기 실행. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public static IServiceCollection AddAppRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<AdminOptions>>((o, adminOptions) =>
        {
            var admin = adminOptions.Value;
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = static (context, _) =>
            {
                // 고정 창 제한기는 창이 끝나는 시점을 RetryAfter 메타데이터로 준다. 동시성 제한기는 주지 않으므로 보수적인 기본값을 쓴다.
                var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                    ? Math.Clamp((int)Math.Ceiling(retryAfter.TotalSeconds), 1, FallbackRetryAfterSeconds)
                    : FallbackRetryAfterSeconds;
                context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                return ValueTask.CompletedTask;
            };
            // 체인: 모든 제한기를 통과해야 한다. 해당 정책이 아닌 요청은 NoLimiter 파티션으로 빠진다.
            // CreateChained는 앞에서부터 permit을 빌린다 — 뒤 제한기가 거부해도 앞에서 빌린 permit은 돌아오지 않는다(고정 창에는 반환 API가 없다). 의도된 보수적 동작이다.
            o.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                // FixedWindow: 창마다 카운터 하나만 두는 O(1) 제한기.
                Window(RateLimitPolicy.Login, ctx => "login-ip:" + ClientIp.PartitionKey(ctx.Connection.RemoteIpAddress), admin.LoginPerIpPerMinute),
                Window(RateLimitPolicy.Login, _ => "login-global", admin.LoginGlobalPerMinute),
                // Concurrency: PBKDF2 검증은 CPU 바운드라 동시에 도는 수를 묶는다. 임대는 요청이 끝날 때 미들웨어가 반납한다.
                Concurrency(RateLimitPolicy.Login, "login-concurrency", admin.LoginConcurrency)
                ,
                Window(RateLimitPolicy.Preview, _ => "preview-global", admin.PreviewPerMinute),
                // 렌더링은 동기 CPU 작업이라 요청 취소로 멈추지 않는다. 동시에 도는 수를 직접 묶는다.
                Concurrency(RateLimitPolicy.Preview, "preview-concurrency", admin.PreviewConcurrency));
        });
        return services;
    }

    /// <summary>요청이 라우팅한 엔드포인트가 <paramref name="policy"/>로 표시되어 있는지 판정한다. 모든 파티션 선택기의 공통 기준이다.</summary>
    /// <param name="ctx">현재 HTTP 요청 컨텍스트.</param>
    /// <param name="policy">대조할 속도 제한 정책.</param>
    /// <returns>엔드포인트에 같은 정책의 <see cref="RateLimitMetadata"/>가 붙어 있으면 <c>true</c>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 정적 메서드로 공유 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation. <see cref="Endpoint.Metadata"/> 조회는 기존 컬렉션을 순회할 뿐 새로 할당하지 않는다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    internal static bool Matches(HttpContext ctx, RateLimitPolicy policy) =>
        ctx.GetEndpoint()?.Metadata.GetMetadata<RateLimitMetadata>()?.Policy == policy;

    /// <summary><paramref name="policy"/>가 걸린 요청만 <paramref name="key"/>로 파티션한 고정 창(1분) 제한기를 만들고, 그 외는 무제한 파티션으로 보낸다.</summary>
    /// <param name="policy">이 제한기를 적용할 정책.</param>
    /// <param name="key">파티션 키를 만드는 함수(예: IP별 로그인 예산).</param>
    /// <param name="permitsPerMinute">1분 창 동안 허용할 최대 요청 수.</param>
    /// <returns>체인에 넣을 <see cref="PartitionedRateLimiter{HttpContext}"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 정적 메서드로 공유 상태가 없다. 반환된 제한기는 여러 요청 스레드가 동시에 호출해도 안전하게 관리된다(프레임워크 보장).</description></item>
    /// <item><description><b>Memory Allocation:</b> 파티션 키 문자열은 해당 정책 요청마다 1개씩 만들어진다. 정책이 아닌 요청은 상수 파티션("none")으로 빠져 추가 할당이 없다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. 대기열(<c>QueueLimit</c>) 0 — 한도 초과분은 기다리지 않고 즉시 거부된다.</description></item>
    /// </list>
    /// </remarks>
    internal static PartitionedRateLimiter<HttpContext> Window(RateLimitPolicy policy, Func<HttpContext, string> key, int permitsPerMinute) =>
        PartitionedRateLimiter.Create<HttpContext, string>(ctx => Matches(ctx, policy)
            ? RateLimitPartition.GetFixedWindowLimiter(key(ctx), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true,
            })
            : RateLimitPartition.GetNoLimiter("none"));

    /// <summary><paramref name="policy"/>가 걸린 요청만 동시 실행 수를 <paramref name="permits"/>로 묶는 제한기를 만들고, 그 외는 무제한 파티션으로 보낸다.</summary>
    /// <param name="policy">이 제한기를 적용할 정책.</param>
    /// <param name="key">이 정책 전체가 공유하는 단일 파티션 키.</param>
    /// <param name="permits">동시에 허용할 최대 요청 수.</param>
    /// <returns>체인에 넣을 <see cref="PartitionedRateLimiter{HttpContext}"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 정적 메서드로 공유 상태가 없다. 반환된 제한기는 Thread-safe하다 — 임대 획득·반납마다
    /// 내부적으로 락을 하나씩 잡는다(<c>ConcurrencyLimiter</c> 내부의 private <c>Lock</c>과 <c>AttemptAcquireCore</c>의 try/finally를
    /// 리플렉션으로 확인). 경합 비용은 그 짧은 임계 구간뿐이다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 해당 정책 전체가 파티션 키 1개(상수 <paramref name="key"/>)를 공유하므로 요청량과 무관하게 제한기 인스턴스는 1개만 생성된다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. 대기열(<c>QueueLimit</c>) 0 — 초과분은 기다리지 않고 즉시 거부된다. 임대 반납은 요청 파이프라인 종료 시 미들웨어가 수행한다.</description></item>
    /// </list>
    /// </remarks>
    internal static PartitionedRateLimiter<HttpContext> Concurrency(RateLimitPolicy policy, string key, int permits) =>
        PartitionedRateLimiter.Create<HttpContext, string>(ctx => Matches(ctx, policy)
            ? RateLimitPartition.GetConcurrencyLimiter(key, _ => new ConcurrencyLimiterOptions { PermitLimit = permits, QueueLimit = 0 })
            : RateLimitPartition.GetNoLimiter("none"));
}
