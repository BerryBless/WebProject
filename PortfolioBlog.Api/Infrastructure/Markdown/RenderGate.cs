using Microsoft.Extensions.Options;

namespace PortfolioBlog.Api.Infrastructure.Markdown;

/// <summary>렌더 슬롯을 <see cref="RenderingOptions.QueueTimeoutMs"/> 안에 얻지 못했다. 호출부(예외 처리기)가 503 + Retry-After로 바꾼다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 불변 예외 인스턴스이며 공유 가변 상태가 없다.</description></item>
/// <item><description><b>Memory Allocation:</b> 예외 인스턴스 1개(+ 스택 트레이스). 게이트가 슬롯을 거부하는 경로에서만 할당되고 정상 렌더링 경로의 비용은 0이다.</description></item>
/// <item><description><b>Blocking:</b> 해당 없음. 예외 타입 자체는 코드를 실행하지 않는다.</description></item>
/// </list>
/// </remarks>
public sealed class RenderBusyException() : Exception("렌더 슬롯을 제때 얻지 못했습니다.");

/// <summary>모든 마크다운 렌더링이 지나는 문. 렌더링은 동기·취소 불가 CPU 작업이라(최악 수 초) 동시에 도는 수를 프로세스 전체에서 묶는다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 공유 가변 상태는 <see cref="_slots"/>(내부적으로 Thread-safe)와 <see cref="_renderCount"/>(<see cref="Interlocked"/>로만 접근)뿐이다.</description></item>
/// <item><description><b>Memory Allocation:</b> 슬롯 획득 성공 경로는 추가 할당이 없다(<see cref="SemaphoreSlim.WaitAsync(TimeSpan, CancellationToken)"/> 자체의 대기 상태 객체 제외). 렌더 함수가 만드는 <see cref="RenderedMarkdown"/> 1개는 호출부 소관.</description></item>
/// <item><description><b>Blocking:</b> 슬롯 대기만 비동기·취소 가능하다. 슬롯을 얻은 뒤의 렌더 자체는 동기 CPU 작업이라 그 스레드를 점유한다(취소 불가) — <see cref="RenderAsync"/> 문서 참조.</description></item>
/// </list>
/// </remarks>
public sealed class RenderGate : IDisposable
{
    private readonly Func<string, RenderedMarkdown> _render;

    // SemaphoreSlim: WaitAsync가 대기자를 스레드 점유 없이 내부 큐(연결 리스트)에 세워 두는 관리형 세마포어다. 기다리는 요청은 스레드 풀 스레드를 쓰지 않는다.
    private readonly SemaphoreSlim _slots;
    private readonly TimeSpan _queueTimeout;

    // long + Interlocked: 테스트가 "렌더가 실제로 몇 번 돌았나"를 락 없이 관측한다.
    private long _renderCount;

    /// <summary>DI가 쓰는 생성자: 실제 <see cref="MarkdownRenderer.RenderDetailed"/>를 렌더 함수로, <see cref="RenderingOptions"/>의 값을 동시성·대기 상한으로 쓴다.</summary>
    /// <param name="renderer">실제 렌더링을 수행할 마크다운 렌더러.</param>
    /// <param name="options">동시 실행 수·대기 상한 설정.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 생성자 실행 중에는 공유되지 않는다(DI 컨테이너가 싱글턴 등록 시 1회만 호출).</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="SemaphoreSlim"/> 인스턴스 1개를 할당한다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public RenderGate(MarkdownRenderer renderer, IOptions<RenderingOptions> options)
        : this(renderer.RenderDetailed, options.Value.Concurrency, TimeSpan.FromMilliseconds(options.Value.QueueTimeoutMs)) { }

    /// <summary>테스트용: 렌더 함수를 바꿔 끼운다(DI는 public 생성자만 본다).</summary>
    /// <param name="render">실제 렌더링 대신 호출할 함수(테스트가 지연·예외를 주입한다).</param>
    /// <param name="concurrency">동시에 슬롯을 가질 수 있는 최대 렌더 수.</param>
    /// <param name="queueTimeout">슬롯을 기다리는 최대 시간.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 생성자 실행 중에는 공유되지 않는다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="SemaphoreSlim"/> 인스턴스 1개를 할당한다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    internal RenderGate(Func<string, RenderedMarkdown> render, int concurrency, TimeSpan queueTimeout)
    {
        _render = render;
        _slots = new SemaphoreSlim(concurrency, concurrency);
        _queueTimeout = queueTimeout;
    }

    /// <summary>지금까지 실제로 실행한 렌더 수(거부된 요청 제외).</summary>
    internal long RenderCount => Interlocked.Read(ref _renderCount);

    /// <summary>슬롯을 얻어 렌더링한다. 슬롯 대기만 비동기·취소 가능하고, 렌더 자체는 호출 스레드(대기했다면 스레드 풀 스레드)에서 동기로 돈다.</summary>
    /// <param name="markdown">렌더링할 마크다운 원문.</param>
    /// <param name="ct">슬롯 대기를 취소할 토큰(슬롯을 얻은 뒤에는 렌더 자체를 취소하지 못한다).</param>
    /// <returns>렌더 결과.</returns>
    /// <exception cref="RenderBusyException">대기 상한 안에 슬롯을 얻지 못했다.</exception>
    /// <exception cref="MarkdownTooComplexException">입력의 중첩이 너무 깊다(렌더러가 던진 것을 그대로 전파).</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 여러 호출자가 동시에 불러도 <see cref="SemaphoreSlim"/>이 슬롯 수를 정확히 지킨다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 렌더 함수가 만드는 <see cref="RenderedMarkdown"/> 1개 외 추가 할당이 없다. 슬롯 거부 시 <see cref="RenderBusyException"/> 1개.</description></item>
    /// <item><description><b>Blocking:</b> 슬롯 대기는 비동기(Non-blocking, 취소 가능). 슬롯을 얻은 뒤의 렌더 함수 호출은 동기 CPU 작업이라 그 스레드를 렌더가 끝날 때까지 점유한다(취소 불가) — 이것이 렌더 자체는 취소 토큰을 받지 않는 이유다. <c>finally</c>에서 슬롯을 반납하므로 렌더가 예외를 던져도(취소를 포함해 어떤 경로로 끝나도) 슬롯은 새는 곳 없이 돌아온다.</description></item>
    /// </list>
    /// </remarks>
    public async Task<RenderedMarkdown> RenderAsync(string markdown, CancellationToken ct)
    {
        if (!await _slots.WaitAsync(_queueTimeout, ct)) throw new RenderBusyException();
        try
        {
            Interlocked.Increment(ref _renderCount);
            return _render(markdown);
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>내부 세마포어를 해제한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 다른 스레드가 <see cref="RenderAsync"/>를 실행 중일 때 호출하면 <see cref="ObjectDisposedException"/>이 날 수 있다(싱글턴 수명 동안은 호출되지 않는다).</description></item>
    /// <item><description><b>Memory Allocation:</b> 추가 할당 없음.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking).</description></item>
    /// </list>
    /// </remarks>
    public void Dispose() => _slots.Dispose();
}
