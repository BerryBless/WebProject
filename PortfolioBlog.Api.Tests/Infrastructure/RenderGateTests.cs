using PortfolioBlog.Api.Infrastructure.Markdown;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>렌더 게이트의 동시 실행 상한과 대기 상한을 가짜 렌더 함수로 결정적으로 검증한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 실행되며, 게이트 안의 "느린" 렌더는 <see cref="Task.Run(Action)"/>으로 별도 스레드 풀 스레드에 올린다.</description></item>
/// <item><description><b>Memory Policy:</b> 테스트마다 독립된 <see cref="RenderGate"/> 인스턴스를 새로 만들고 <c>using</c>으로 해제한다. 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
/// <item><description><b>Concurrency:</b> DB·Docker에 의존하지 않으므로 다른 컬렉션과 병렬로 실행할 수 있다.</description></item>
/// </list>
/// </remarks>
public sealed class RenderGateTests
{
    /// <summary>슬롯이 차 있으면 대기 시간 뒤 <see cref="RenderBusyException"/>이고, 슬롯이 풀리면 다시 받는다.</summary>
    [Fact]
    public async Task FullGate_RejectsAfterQueueTimeout_ThenRecovers()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var gate = new RenderGate(md =>
        {
            if (md == "slow") { entered.Set(); release.Wait(TimeSpan.FromSeconds(30)); }
            return new RenderedMarkdown(md, null, false);
        }, concurrency: 1, queueTimeout: TimeSpan.FromMilliseconds(50));

        var slow = Task.Run(() => gate.RenderAsync("slow", CancellationToken.None));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

        await Assert.ThrowsAsync<RenderBusyException>(() => gate.RenderAsync("second", CancellationToken.None));

        release.Set();
        await slow;
        Assert.Equal("third", (await gate.RenderAsync("third", CancellationToken.None)).Html);
        Assert.Equal(2, gate.RenderCount); // 거부된 요청은 렌더링하지 않았다
    }

    /// <summary>렌더가 예외를 던져도 슬롯이 반납된다(반납되지 않으면 두 번째 호출이 RenderBusyException이 된다).</summary>
    [Fact]
    public async Task ThrowingRender_ReleasesTheSlot()
    {
        using var gate = new RenderGate(_ => throw new MarkdownTooComplexException("x", new ArgumentException()), 1, TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<MarkdownTooComplexException>(() => gate.RenderAsync("a", CancellationToken.None));
        await Assert.ThrowsAsync<MarkdownTooComplexException>(() => gate.RenderAsync("b", CancellationToken.None));
    }
}
