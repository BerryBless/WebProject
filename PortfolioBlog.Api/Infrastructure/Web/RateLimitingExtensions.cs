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
/// <item><description><b>Memory Allocation:</b> 공개 정책의 IP 파티션은 방문자 IP 수만큼 생긴다. <c>PartitionedRateLimiter.Create</c>가 반환하는 내부 구현(<c>DefaultPartitionedRateLimiter&lt;TResource,TKey&gt;</c>, .NET 10 런타임)은
/// <c>static readonly TimeSpan s_idleTimeLimit = 00:00:10</c> 필드와 백그라운드 타이머(<c>Heartbeat</c>)로 10초 이상 쓰이지 않은 파티션을 스스로 걷어 낸다 —
/// 리플렉션 프로브로 50개 파티션을 만든 뒤 아무 것도 호출하지 않고 기다리기만 했을 때 10~15초 사이에 내부 딕셔너리가 0으로 줄어드는 것을 실측했다(저장소 밖 임시 콘솔 프로젝트, 남기지 않음).
/// IPv6는 /64로 묶어 파티션 수를 더 줄인다(<see cref="ClientIp"/>).</description></item>
/// <item><description><b>Blocking:</b> 대기열 0 — 한도를 넘으면 기다리지 않고 즉시 429.</description></item>
/// </list>
/// 미들웨어 위치는 <c>AdminSurfaceMiddleware</c> 뒤다: 허용 IP 밖의 요청이 한도를 소진하지 못한다.
/// <c>WebApplication</c>은 라우팅을 사용자 미들웨어보다 앞에 두므로 제한기가 돌 때 <c>GetEndpoint()</c>는 이미 채워져 있다.
/// </remarks>
public static class RateLimitingExtensions
{
    /// <summary>동시 실행 제한에 걸렸을 때의 <c>Retry-After</c>(초). 동시성 제한기는 재시도 시점을 주지 않는다(실측) — 분 단위 창과 달리 곧 풀리므로 짧게 준다.</summary>
    public const int ConcurrencyRetryAfterSeconds = 5;

    /// <summary>고정 창 거부의 <c>Retry-After</c> 상한(초) = 창 길이.</summary>
    private const int MaxRetryAfterSeconds = 60;

    /// <summary>속도 제한 체인을 만들어 등록한다. 전 정책 공통 429 상태 코드와 <c>Retry-After</c> 헤더도 여기서 구성한다.</summary>
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
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<AdminOptions>, IOptions<PublicOptions>>((o, admin, pub) =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = static (context, _) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = RetryAfterSeconds(context.Lease).ToString(CultureInfo.InvariantCulture);
                return ValueTask.CompletedTask;
            };
            o.GlobalLimiter = BuildChain(admin.Value, pub.Value);
        });
        return services;
    }

    /// <summary>거부된 임대에서 <c>Retry-After</c> 초를 고른다: 고정 창은 창 종료까지(1~60), 동시 실행 제한은 <see cref="ConcurrencyRetryAfterSeconds"/>.</summary>
    /// <param name="lease">거부된(또는 획득된) 속도 제한 임대.</param>
    /// <returns>클라이언트에게 알려줄 재시도 대기 시간(초).</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 매개변수만 읽는 정적 메서드.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation. 메타데이터 조회와 산술 연산뿐이다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    internal static int RetryAfterSeconds(RateLimitLease lease) =>
        lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? Math.Clamp((int)Math.Ceiling(retryAfter.TotalSeconds), 1, MaxRetryAfterSeconds)
            : ConcurrencyRetryAfterSeconds;

    /// <summary>등록·테스트 양쪽이 쓰는 속도 제한 체인을 조립한다.</summary>
    /// <param name="admin">로그인·미리보기·업로드 한도가 담긴 관리 설정.</param>
    /// <param name="pub">공개 페이지·자산·검색 한도가 담긴 공개 설정.</param>
    /// <returns>체인 전체를 대표하는 단일 <see cref="PartitionedRateLimiter{HttpContext}"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 정적 메서드로 공유 상태가 없다. 반환된 체인은 여러 요청 스레드가 동시에 호출해도 안전하다(프레임워크 보장).</description></item>
    /// <item><description><b>Memory Allocation:</b> 정책 개수만큼 제한기 인스턴스를 시작 시 1회 할당한다. 파티션(키)별 내부 상태는 그 파티션이 처음 쓰일 때 지연 생성된다.</description></item>
    /// <item><description><b>Blocking:</b> 동기 실행. I/O 없음.</description></item>
    /// </list>
    /// <b>동시 실행 제한기를 고정 창보다 앞에</b> 둔다: CreateChained는 앞에서부터 임대를 빌리고, 뒤가 거부하면 앞의 임대를
    /// Dispose한다(실측). 동시성 임대는 Dispose로 반납되지만 고정 창에는 반환이 없으므로, 창이 앞이면 동시 실행 거부마다 분당 허용량이 1씩 사라진다.
    /// </remarks>
    internal static PartitionedRateLimiter<HttpContext> BuildChain(AdminOptions admin, PublicOptions pub) =>
        PartitionedRateLimiter.CreateChained(
            Concurrency(RateLimitPolicy.Login, "login-concurrency", admin.LoginConcurrency),
            Concurrency(RateLimitPolicy.Preview, "preview-concurrency", admin.PreviewConcurrency),
            Concurrency(RateLimitPolicy.Upload, "upload-concurrency", admin.UploadConcurrency),
            Concurrency(RateLimitPolicy.Search, "search-concurrency", pub.SearchConcurrency),
            Window(ctx => Matches(ctx, RateLimitPolicy.Login), ctx => "login-ip:" + Ip(ctx), admin.LoginPerIpPerMinute),
            Window(ctx => Matches(ctx, RateLimitPolicy.Login), _ => "login-global", admin.LoginGlobalPerMinute),
            Window(ctx => Matches(ctx, RateLimitPolicy.Preview), _ => "preview-global", admin.PreviewPerMinute),
            Window(ctx => Matches(ctx, RateLimitPolicy.Upload), _ => "upload-global", admin.UploadPerMinute),
            // 검색 창이 페이지 창보다 앞: 검색 한도에 걸린 요청이 페이지 허용량까지 깎지 않는다.
            Window(ctx => Matches(ctx, RateLimitPolicy.Search), ctx => "search-ip:" + Ip(ctx), pub.SearchPerIpPerMinute),
            Window(ctx => Matches(ctx, RateLimitPolicy.PublicPage) || Matches(ctx, RateLimitPolicy.Search), ctx => "page-ip:" + Ip(ctx), pub.PagePerIpPerMinute),
            Window(ctx => Matches(ctx, RateLimitPolicy.PublicAsset), ctx => "asset-ip:" + Ip(ctx), pub.AssetPerIpPerMinute));

    /// <summary>요청의 원격 IP를 속도 제한 파티션 키로 정규화한다.</summary>
    /// <param name="ctx">현재 HTTP 요청 컨텍스트.</param>
    /// <returns><see cref="ClientIp.PartitionKey"/>가 만든 정규화된 키 문자열.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 정적 메서드로 공유 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="ClientIp.PartitionKey"/>와 동일(키 문자열 1개).</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    private static string Ip(HttpContext ctx) => ClientIp.PartitionKey(ctx.Connection.RemoteIpAddress);

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

    /// <summary><paramref name="applies"/>가 참인 요청만 <paramref name="key"/>로 파티션한 고정 창(1분) 제한기를 만들고, 그 외는 무제한 파티션으로 보낸다.</summary>
    /// <param name="applies">이 제한기를 적용할지 판정하는 함수(<see cref="Matches"/> 호출 조합 — <see cref="RateLimitPolicy.Search"/>처럼 정책 하나가 여러 창에 걸릴 수 있다).</param>
    /// <param name="key">파티션 키를 만드는 함수(예: IP별 로그인 예산).</param>
    /// <param name="permitsPerMinute">1분 창 동안 허용할 최대 요청 수.</param>
    /// <returns>체인에 넣을 <see cref="PartitionedRateLimiter{HttpContext}"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 정적 메서드로 공유 상태가 없다. 반환된 제한기는 여러 요청 스레드가 동시에 호출해도 안전하게 관리된다(프레임워크 보장).</description></item>
    /// <item><description><b>Memory Allocation:</b> 파티션 키 문자열은 해당 창에 걸리는 요청마다 1개씩 만들어진다. 걸리지 않는 요청은 상수 파티션("none")으로 빠져 추가 할당이 없다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. 대기열(<c>QueueLimit</c>) 0 — 한도 초과분은 기다리지 않고 즉시 거부된다.</description></item>
    /// </list>
    /// </remarks>
    internal static PartitionedRateLimiter<HttpContext> Window(Func<HttpContext, bool> applies, Func<HttpContext, string> key, int permitsPerMinute) =>
        PartitionedRateLimiter.Create<HttpContext, string>(ctx => applies(ctx)
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
