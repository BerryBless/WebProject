using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace PortfolioBlog.Api.Infrastructure.Markdown;

/// <summary>공개 글의 렌더 결과를 <c>(PostId, xmin)</c>으로 캐시하고, 같은 키의 동시 미스를 렌더 한 번으로 합친다(단일 비행).</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. <see cref="_cache"/>(내부적으로 Thread-safe)와 <see cref="_inflight"/>(<see cref="ConcurrentDictionary{TKey,TValue}"/>)만 공유하며 둘 다 동시 접근에 안전하다.</description></item>
/// <item><description><b>Memory Allocation:</b> 캐시 항목은 <see cref="RenderingOptions.CacheMegabytes"/>로 상한을 건다(<see cref="Store"/> 문서 참조). 단일 비행 항목은 렌더가 끝나면(성공·실패 모두) 즉시 제거된다.</description></item>
/// <item><description><b>Blocking:</b> 캐시 적중은 즉시 반환(Non-blocking). 미스는 <see cref="GetOrRenderAsync"/> 문서 참조.</description></item>
/// </list>
/// 프로세스 메모리에만 둔다 — 재배포하면 비워지므로 렌더러 보안 수정이 과거 글 전체에 즉시 적용된다(스펙 3.2). HTML을 DB에 저장하지 않는다.
/// </remarks>
public sealed class RenderedPostCache : IDisposable
{
    /// <summary>정상 렌더의 캐시 수명. 키에 xmin이 들어 있어 수정되면 어차피 미스다 — 이 값은 죽은 항목이 남는 시간의 상한이다.</summary>
    public static readonly TimeSpan NormalLifetime = TimeSpan.FromHours(24);

    /// <summary>시간 때문에 강조가 빠진 렌더의 수명. 서버가 한가해지면 곧 제대로 된 결과로 바뀐다.</summary>
    public static readonly TimeSpan DegradedLifetime = TimeSpan.FromMinutes(2);

    private readonly RenderGate _gate;

    // MemoryCache(SizeLimit): 항목마다 Size를 주면 합계 기준으로 예산을 강제한다. 공용 IMemoryCache가 아닌 전용 인스턴스라 다른 용도와 예산이 섞이지 않는다.
    // 실측(리뷰 프로브, SizeLimit=1,000,000·Size=5,000 항목 400개 연속 Set): 상한에 닿기 전까지는 Set이 전부 성공한다. 상한을 넘기는 순간부터는
    // 우선순위·LRU로 "비우고 넣는" 것이 아니라 그 Set 자체가 조용히 거부된다(즉시 TryGetValue해도 없음, Count 불변) — 이 상태가 195회 연속 Set 동안 유지됐다.
    // 압축은 스레드풀에 큐잉되는 비동기 작업이라 지연된다: 500ms 대기 후에야 Count가 200→190(정확히 5% = 기본 CompactionPercentage)으로 줄었고,
    // 그 뒤의 Set 1회가 다시 성공했다. 즉 상한 초과 상태에서 새 요청이 몰리면(쓰기가 압축보다 빠르면) 신규 항목이 한동안 전부 유실될 수 있다 —
    // 결론(Task 3 리뷰 라운드 1 실측 기반): 거부된 Set의 대가는 다음 요청에서 렌더 1회가 더 도는 것뿐이고 틀린 내용이 나가는 일은 없다
    // (TryGet 미스는 GetOrRenderAsync가 그 자리에서 다시 렌더링한다). 그 추가 렌더 비용의 상한은 RenderGate가 이미 건다(동시 렌더 수 제한).
    // 그래서 이 상태를 막는 별도 설계 변경(예: LRU 축출을 즉시 수행)은 필요 없다고 판단했다 — 상한을 넉넉히 잡는 것으로 충분하다.
    private readonly MemoryCache _cache;

    // ConcurrentDictionary<키, Lazy<Task>>: GetOrAdd는 값 팩토리를 여러 번 부를 수 있지만 저장되는 Lazy는 하나고, 그 하나의 Value만 실행된다 → 키당 렌더 1회.
    private readonly ConcurrentDictionary<(Guid, uint), Lazy<Task<RenderedMarkdown>>> _inflight = new();

    /// <summary>렌더 게이트와 캐시 메모리 상한 설정으로 만든다.</summary>
    /// <param name="gate">실제 렌더링에 쓸 전역 렌더 게이트.</param>
    /// <param name="options">캐시 메모리 상한(<see cref="RenderingOptions.CacheMegabytes"/>) 설정.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 생성자 실행 중에는 공유되지 않는다(DI 컨테이너가 싱글턴 등록 시 1회만 호출).</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="MemoryCache"/> 인스턴스 1개를 할당한다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public RenderedPostCache(RenderGate gate, IOptions<RenderingOptions> options)
    {
        _gate = gate;
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = (long)options.Value.CacheMegabytes * 1024 * 1024 });
    }

    /// <summary>캐시에서 글 버전의 렌더 결과를 찾는다(렌더를 트리거하지 않는다).</summary>
    /// <param name="postId">글 고유 식별자.</param>
    /// <param name="version">조회할 버전(DB xmin).</param>
    /// <param name="rendered">있으면 캐시된 렌더 결과, 없으면 <see langword="null"/>.</param>
    /// <returns>캐시에 있었으면 <see langword="true"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. <see cref="MemoryCache.TryGetValue"/>는 내부적으로 동시 접근에 안전하다.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation이 <b>아니다</b>: <see cref="IMemoryCache"/>의 키 타입이 <see cref="object"/>라 값 형식인 <c>(Guid, uint)</c> 튜플이 호출마다 박싱된다. 적중해도 반환값 자체는 기존 <see cref="RenderedMarkdown"/> 인스턴스 참조라 그 이상 할당되지 않는다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking).</description></item>
    /// </list>
    /// </remarks>
    public bool TryGet(Guid postId, uint version, out RenderedMarkdown rendered)
    {
        if (_cache.TryGetValue((postId, version), out RenderedMarkdown? hit) && hit is not null)
        {
            rendered = hit;
            return true;
        }
        rendered = null!;
        return false;
    }

    /// <summary>캐시에 있으면 그것을, 없으면 (같은 키의 동시 호출과 합쳐) 한 번 렌더링해 돌려준다.</summary>
    /// <param name="postId">글 고유 식별자.</param>
    /// <param name="version">렌더링할 버전(DB xmin).</param>
    /// <param name="markdown">캐시 미스일 때 렌더링할 마크다운 원문.</param>
    /// <param name="ct">이 호출자의 대기만 취소한다. 공유 렌더는 다른 호출자를 위해 계속된다.</param>
    /// <returns>캐시된 또는 새로 렌더링한 결과.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 같은 키로 동시에 호출해도 <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd"/>가 <see cref="Lazy{T}"/> 인스턴스 하나만 저장하고, 그 <c>Value</c>(렌더 자체)는 정확히 한 번만 평가된다.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation이 아니다. 캐시 적중도 <see cref="TryGet"/> 문서대로 키 박싱 1회 + 완료된 <see cref="Task{TResult}"/> 1개(<see cref="Task.FromResult{TResult}(TResult)"/>)를 할당한다. 미스는 그 위에 <see cref="Lazy{T}"/> 1개(첫 호출자만, <see cref="ConcurrentDictionary{TKey,TValue}"/> 항목 포함) + 렌더 결과 1개를 더 할당한다.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. <paramref name="ct"/>는 <see cref="Task.WaitAsync(CancellationToken)"/>로 이 호출자의 대기에만 걸리므로, 공유 렌더 자체(<see cref="RenderGate.RenderAsync"/>가 동기로 CPU를 쓰는 구간)는 취소되지 않고 다른 호출자를 위해 계속된다.</description></item>
    /// </list>
    /// </remarks>
    public Task<RenderedMarkdown> GetOrRenderAsync(Guid postId, uint version, string markdown, CancellationToken ct)
    {
        if (TryGet(postId, version, out var hit)) return Task.FromResult(hit);
        var key = (postId, version);
        var lazy = _inflight.GetOrAdd(key, k => new Lazy<Task<RenderedMarkdown>>(() => RenderAndStoreAsync(k, markdown)));
        return lazy.Value.WaitAsync(ct);
    }

    /// <summary>이미 렌더링한 결과를 넣는다(글 저장 경로가 저장 전 확인용으로 한 렌더를 버리지 않고 선채움한다).</summary>
    /// <param name="postId">글 고유 식별자.</param>
    /// <param name="version">저장한 결과의 버전(DB xmin).</param>
    /// <param name="rendered">캐시에 넣을 렌더 결과.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. <see cref="MemoryCache.Set"/>는 내부적으로 동시 접근에 안전하다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="MemoryCacheEntryOptions"/> 1개 + 캐시 항목 자체(<paramref name="rendered"/>의 <c>Html</c>·<c>FirstImageUrl</c> 길이 합만큼 크기로 계산됨).</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking).</description></item>
    /// </list>
    /// </remarks>
    public void Store(Guid postId, uint version, RenderedMarkdown rendered) =>
        _cache.Set((postId, version), rendered, new MemoryCacheEntryOptions
        {
            // 리뷰 프로브 실측(Task 3 리뷰 라운드 1): 문자열 페이로드(Html·FirstImageUrl)를 제외한 고정 오버헤드가 항목당 약 290바이트였다.
            // 512로 넉넉히 잡아 과소평가를 피한다. FirstImageUrl도 문자열이라 Html과 같은 단위(UTF-16 2바이트/문자)로 더한다.
            Size = (long)rendered.Html.Length * sizeof(char) + (rendered.FirstImageUrl?.Length ?? 0) * sizeof(char) + 512,
            AbsoluteExpirationRelativeToNow = rendered.HighlightTimedOut ? DegradedLifetime : NormalLifetime,
        });

    /// <summary>실제 렌더링을 게이트 뒤에서 수행하고 성공하면 캐시에 넣는다. 성공·실패 어느 쪽이든 단일 비행 항목에서 자신을 제거해 다음 요청이 다시 시도하게 한다.</summary>
    /// <param name="key">렌더링할 (글 Id, 버전) 키.</param>
    /// <param name="markdown">렌더링할 마크다운 원문.</param>
    /// <returns>렌더 결과.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> <see cref="Lazy{T}"/>의 값 팩토리로만 호출되므로 같은 키에 대해 동시에 두 번 실행되지 않는다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="Store"/> 문서 참조. 실패 시 추가 할당 없음(예외를 그대로 전파).</description></item>
    /// <item><description><b>Blocking:</b> <see cref="Task.Yield"/>로 즉시 스레드를 양보한 뒤 <see cref="RenderGate.RenderAsync"/>를 <see cref="CancellationToken.None"/>으로 호출한다 — 어느 호출자가 취소해도 공유 렌더는 끝까지 간다.</description></item>
    /// </list>
    /// </remarks>
    private async Task<RenderedMarkdown> RenderAndStoreAsync((Guid PostId, uint Version) key, string markdown)
    {
        // 즉시 양보한다: 렌더는 동기 작업이라, 양보하지 않으면 Lazy 팩토리가 렌더가 끝날 때까지 반환하지 않고
        // 같은 키의 다른 호출자가 Lazy.Value에서 (스레드를 점유한 채) 동기로 막힌다.
        await Task.Yield();
        try
        {
            var rendered = await _gate.RenderAsync(markdown, CancellationToken.None);
            Store(key.PostId, key.Version, rendered);
            return rendered;
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    /// <summary>내부 <see cref="MemoryCache"/>를 해제한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 다른 스레드가 캐시를 조회 중일 때 호출하면 <see cref="ObjectDisposedException"/>이 날 수 있다(싱글턴 수명 동안은 호출되지 않는다).</description></item>
    /// <item><description><b>Memory Allocation:</b> 추가 할당 없음.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking).</description></item>
    /// </list>
    /// </remarks>
    public void Dispose() => _cache.Dispose();
}
