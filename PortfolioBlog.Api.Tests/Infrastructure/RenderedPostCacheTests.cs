using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Infrastructure.Markdown;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>글 버전당 한 번만 렌더링되는지(캐시 + 단일 비행) 검증한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 실행되며, 공유 렌더를 흉내 내는 가짜 렌더 함수는 <see cref="Task.Run(Action)"/>으로 스레드 풀에서 돈다.</description></item>
/// <item><description><b>Memory Policy:</b> 테스트마다 독립된 <see cref="RenderGate"/>·<see cref="RenderedPostCache"/> 인스턴스를 새로 만들고 <c>using</c>으로 해제한다. <see cref="Options"/> 필드만 정적으로 공유하며 불변이라 안전하다.</description></item>
/// <item><description><b>Concurrency:</b> DB·Docker에 의존하지 않으므로 다른 컬렉션과 병렬로 실행할 수 있다.</description></item>
/// </list>
/// </remarks>
public sealed class RenderedPostCacheTests
{
    private static readonly IOptions<RenderingOptions> Options = Microsoft.Extensions.Options.Options.Create(new RenderingOptions());

    /// <summary>같은 글을 동시에 10번 요청해도 렌더는 한 번이다. 단일 비행이 없으면 렌더 수가 10(또는 게이트 거부)이 되어 실패한다.</summary>
    [Fact]
    public async Task ConcurrentRequests_ForTheSameVersion_RenderOnce()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var gate = new RenderGate(md => { entered.Set(); release.Wait(TimeSpan.FromSeconds(30)); return new RenderedMarkdown("<p>" + md + "</p>", null, false); },
            concurrency: 2, queueTimeout: TimeSpan.FromSeconds(30));
        using var cache = new RenderedPostCache(gate, Options);
        var id = Guid.NewGuid();

        var calls = Enumerable.Range(0, 10).Select(_ => cache.GetOrRenderAsync(id, 7, "본문", CancellationToken.None)).ToArray();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, gate.RenderCount); // 렌더가 아직 끝나지 않은 시점: 나머지 9개는 같은 작업에 매달려 있다
        release.Set();

        var results = await Task.WhenAll(calls);
        Assert.All(results, r => Assert.Same(results[0], r));
        Assert.Equal(1, gate.RenderCount);

        Assert.True(cache.TryGet(id, 7, out var hit));
        Assert.Same(results[0], hit);
        Assert.False(cache.TryGet(id, 8, out _)); // 버전이 바뀌면 미스
    }

    /// <summary>한 호출자의 취소는 그 호출자만 끝내고 공유 렌더는 계속된다.</summary>
    [Fact]
    public async Task CallerCancellation_DoesNotCancelTheSharedRender()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var gate = new RenderGate(md => { entered.Set(); release.Wait(TimeSpan.FromSeconds(30)); return new RenderedMarkdown(md, null, false); }, 1, TimeSpan.FromSeconds(30));
        using var cache = new RenderedPostCache(gate, Options);
        var id = Guid.NewGuid();
        using var cts = new CancellationTokenSource();

        var cancelled = cache.GetOrRenderAsync(id, 1, "a", cts.Token);
        var patient = cache.GetOrRenderAsync(id, 1, "a", CancellationToken.None);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        release.Set();
        Assert.Equal("a", (await patient).Html);
    }

    /// <summary>렌더 실패(과부하)는 캐시되지 않는다 — 다음 요청이 다시 시도한다.</summary>
    [Fact]
    public async Task FailedRender_IsNotCached()
    {
        var attempts = 0;
        using var gate = new RenderGate(md => Interlocked.Increment(ref attempts) == 1 ? throw new InvalidOperationException("첫 시도 실패") : new RenderedMarkdown(md, null, false), 1, TimeSpan.FromSeconds(5));
        using var cache = new RenderedPostCache(gate, Options);
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetOrRenderAsync(id, 1, "a", CancellationToken.None));
        Assert.Equal("a", (await cache.GetOrRenderAsync(id, 1, "a", CancellationToken.None)).Html);
    }
}
