---
name: thread-dispatch-design
description: "BoundedChannel<T> 기반 서버 범위 디스패처를 계약(불변)에 맞춰 설계하고 컴파일되는 C#을 {run_dir}/02_dispatcher/ThreadDispatcher.cs에 출력한다. thread-dispatcher-designer 전용."
---

# Thread Dispatch Design Skill

## 입력 읽기
1. `{run_dir}/00_design_brief.md` — 처리량(용량 산정), 워커 수, 디스패처 범위(서버 공유/연결별), 핸들러 예외 정책
2. `{run_dir}/02_interface_contract.cs` — **불변 입력.** `ParsedMessage`(IDisposable 소유 복사본), `IMessageDispatcher`. 없으면 `error` 보고(스텁 설계 금지)

## 설계 규칙 (감사 체크리스트와 1:1)
| 규칙 | 이유 |
|---|---|
| 워커가 메시지를 **직접 await 처리** | `readonly struct : IThreadPoolWorkItem`을 `UnsafeQueueUserWorkItem(IThreadPoolWorkItem, bool)`에 넘기면 인터페이스 매개변수라 **박싱**. 재큐잉은 BoundedChannel 백프레셔를 무제한 스레드풀 큐로 새게 하고 drain 보장도 깨진다 |
| `FullMode.Wait` + `AllowSynchronousContinuations=false` | 손실 없는 감속. 생산자 스레드에서 소비자 continuation이 inline 실행되어 IO 루프가 막히는 것을 방지 |
| 완료는 **서버 소유자만** `TryComplete` | 연결 종료가 공유 채널을 닫으면 다른 연결의 `WriteAsync`가 `ChannelClosedException`. `Complete()` 2회는 예외 |
| 워커 루프는 핸들러 예외로 줄어들지 않음 | 취소 요청 OCE만 탈출. 핸들러 내부 OCE·예외는 로깅 후 계속 |
| 메시지 `Dispose` 정확히 1회(finally) | 풀 버퍼 반환. 강제 중단 시 잔여 메시지도 Dispose |
| 폐기 모드(`DropNewest|DropOldest|DropWrite`)를 쓰면 폐기 메시지의 Dispose 책임 정의 | `Drop`이라는 멤버는 없다 |
| `SingleReader`는 워커 1개일 때만 true(힌트) | 순서를 강제하지 않는다. 순서 보장은 워커 1개 설계로 |
| `UnsafeQueueUserWorkItem`이 생략하는 것은 ExecutionContext | "보안 컨텍스트"가 아니다 |
| `Channel<T>`은 thread-safe이지 lock-free가 아님 | BoundedChannel 내부 lock. 보고에 `lock_free:true`를 쓰지 않는다 |
| CLAUDE.md: public `<remarks>` 3항목, `Channel/CTS/Interlocked 대상 필드` 선언부 근거 `//` | 프로젝트 규칙 |

## 채널 용량·워커 수
```
ChannelCapacity = 예상 처리량(msg/s) × 허용 지연(s)     예: 100,000 × 0.01 = 1,000
WorkerCount     = Environment.ProcessorCount (CPU 바운드) / 2~4× (핸들러가 I/O 바운드)
```
| 시나리오 | FullMode | 용량 | 비고 |
|---|---|---|---|
| 레이턴시 중요 | Wait | 100~500 | 빠른 백프레셔 |
| 처리량 중요 | Wait | 5,000~10,000 | 버스트 흡수 |
| 최신값만 유효(텔레메트리) | DropOldest | 중간 | 폐기 메시지 Dispose 필수 |
| 절대 비블로킹 | DropWrite | 큼 | 호출자가 반환값 false 처리 + Dispose |

## 참조 구현 (빌드 검증: net10.0, TreatWarningsAsErrors, 경고 0·오류 0 — 2026-09-13)

```csharp
// 02_dispatcher/ThreadDispatcher.cs — Channel<T> 기반 서버 범위 디스패처 템플릿 (thread-dispatch-design 스킬)
using System.Threading.Channels;
using Pipeline.Contract;

namespace Pipeline.Dispatch;

/// <summary>워커가 메시지 하나를 처리한다.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 구현체는 Thread-safe 여야 한다(워커 N개가 동시에 호출).</description></item>
/// <item><description><b>Memory Allocation:</b> 메시지 소유권은 호출자(디스패처)에 남아 있다 — 구현체는 <see cref="ParsedMessage.Payload"/>를
/// await 너머로 보관하지 않는다(반환 후 버퍼가 풀로 돌아간다).</description></item>
/// <item><description><b>Blocking:</b> 비동기. 동기 블로킹(<c>.Result</c>)은 워커 스레드를 고갈시킨다.</description></item>
/// </list>
/// </remarks>
public interface IMessageHandler
{
    ValueTask HandleAsync(in ParsedMessage message, CancellationToken ct);
}

public sealed record DispatcherOptions
{
    // 채널 용량 = 예상 처리량(msg/s) × 허용 지연(s). 예: 100,000 × 0.01 = 1,000
    public int ChannelCapacity { get; init; } = 1_000;
    public int WorkerCount { get; init; } = Environment.ProcessorCount;
    // 생산자(IO 루프)가 여러 연결이면 false. 단일 리스너 스레드가 모든 연결을 돌리는 구조에서만 true.
    public bool SingleWriter { get; init; } = false;
}

/// <summary>서버 범위(모든 연결 공유) 디스패처. IO 루프 → BoundedChannel → 워커 N개.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. <see cref="DispatchAsync"/>는 연결 N개가 동시에 호출한다.
/// 완료(<see cref="DisposeAsync"/>)는 서버 종료 시 소유자 1개만 호출한다 — 연결 종료가 채널을 닫으면 안 된다.</description></item>
/// <item><description><b>Memory Allocation:</b> 큐 공간이 있을 때 <c>WriteAsync</c>는 동기 완료(할당 0). 워커는 메시지를 직접 await 처리하며
/// 별도 Work Item 을 만들지 않는다(struct IThreadPoolWorkItem 은 인터페이스 매개변수로 넘길 때 박싱된다).</description></item>
/// <item><description><b>Blocking:</b> 비동기. 채널이 꽉 차면 <c>WriteAsync</c>가 await 하여 IO 루프를 감속시킨다(백프레셔).</description></item>
/// </list>
/// </remarks>
public sealed class ThreadDispatcher : IMessageDispatcher, IAsyncDisposable
{
    // Channel<T>(Bounded): 내부 lock 으로 보호되는 큐 + 대기자 목록. "Lock-Free"가 아니라 호출자 락과 수동 신호를 제거해 주는 thread-safe 큐다.
    // FullMode.Wait 로 꽉 찼을 때 WriteAsync 가 대기하므로 백프레셔가 IO 루프(FlushAsync 정지)까지 자연스럽게 전파된다.
    private readonly Channel<ParsedMessage> _channel;
    private readonly IMessageHandler _handler;
    private readonly Task[] _workers;
    // CancellationTokenSource: 종료 시 진행 중인 핸들러에 협조적 취소를 전달한다. Task.Run 에 넘기는 토큰은 시작 전 취소에만 효과가 있으므로
    // 워커 루프 내부에서도 같은 토큰을 사용한다.
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;

    public ThreadDispatcher(DispatcherOptions options, IMessageHandler handler)
    {
        _handler = handler;
        _channel = Channel.CreateBounded<ParsedMessage>(new BoundedChannelOptions(options.ChannelCapacity)
        {
            SingleWriter = options.SingleWriter,
            SingleReader = options.WorkerCount == 1,     // 소비자 1개일 때만 true(최적화 힌트일 뿐 순서를 강제하지 않는다)
            FullMode = BoundedChannelFullMode.Wait,      // 손실 없이 감속. DropNewest/DropOldest/DropWrite 는 유효한 enum 값이지만 폐기 시 Dispose 책임이 생긴다
            AllowSynchronousContinuations = false        // 생산자 스레드에서 소비자 continuation 이 inline 실행되어 IO 루프가 막히는 것을 방지
        });
        _workers = new Task[options.WorkerCount];
        for (int i = 0; i < _workers.Length; i++)
            _workers[i] = Task.Run(() => WorkerLoopAsync(_channel.Reader, _handler, _cts.Token), CancellationToken.None);
    }

    /// <inheritdoc/>
    public ValueTask DispatchAsync(ParsedMessage message, CancellationToken ct)
    {
        // 종료 이후의 쓰기는 ChannelClosedException 대신 명확한 예외로. 호출자가 message 를 Dispose 한다(계약).
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(ThreadDispatcher));
        return _channel.Writer.WriteAsync(message, ct);
    }

    private static async Task WorkerLoopAsync(ChannelReader<ParsedMessage> reader, IMessageHandler handler, CancellationToken ct)
    {
        // ReadAllAsync(ct): Complete 후 남은 항목을 drain 하고 끝난다. ct 취소 시 OCE 로 탈출.
        await foreach (ParsedMessage message in reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await handler.HandleAsync(in message, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                message.Dispose();
                throw;                                                        // 종료 요청 — 루프 탈출
            }
            catch (Exception)
            {
                // 핸들러의 예외(핸들러 내부 OCE 포함)로 워커가 조용히 줄어들면 안 된다: 로깅 후 계속.
                // TODO: 로거 주입 후 기록
            }
            finally
            {
                message.Dispose();                                            // 소유권 종료: 풀 버퍼 반환(정확히 1회)
            }
        }
    }

    /// <summary>모든 생산자가 끝난 뒤 서버 소유자가 1회 호출한다. 남은 메시지를 drain 하고 워커를 종료한다.</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 중복 호출은 무시(Interlocked 가드). 연결 종료 경로에서 호출 금지.</description></item>
    /// <item><description><b>Memory Allocation:</b> 없음.</description></item>
    /// <item><description><b>Blocking:</b> 비동기. drain 이 끝날 때까지 대기. 강제 중단이 필요하면 <see cref="CancelAsync"/>.</description></item>
    /// </list>
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _channel.Writer.TryComplete();                                        // Complete() 2회 호출은 예외 — TryComplete 로 멱등
        try { await Task.WhenAll(_workers).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* CancelAsync 이후 정상 */ }
        _cts.Dispose();
    }

    /// <summary>drain 을 기다리지 않고 워커를 중단한다(서버 강제 종료 경로). 남은 메시지는 Dispose 된다.</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe(Interlocked 가드).</description></item>
    /// <item><description><b>Memory Allocation:</b> 없음.</description></item>
    /// <item><description><b>Blocking:</b> 비동기.</description></item>
    /// </list>
    /// </remarks>
    public async ValueTask CancelAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try { await Task.WhenAll(_workers).ConfigureAwait(false); } catch (OperationCanceledException) { }
        while (_channel.Reader.TryRead(out ParsedMessage leftover)) leftover.Dispose();   // 미처리 메시지의 풀 버퍼 반환
        _cts.Dispose();
    }
}
```

## 완료 흐름 (서버 범위 디스패처)
```
[연결 종료]  ReadPipeAsync 끝 → reader.Complete → (채널은 건드리지 않음)
[서버 종료]  모든 리스너 중단 → 모든 연결 drain 대기 → dispatcher.DisposeAsync(): TryComplete → 워커 drain → WhenAll
[강제 종료]  dispatcher.CancelAsync(): TryComplete + _cts.Cancel → 잔여 메시지 Dispose
```
연결별 디스패처가 브리프에 명시된 경우에만 연결 종료 경로에서 완료한다(계약에 소유자 기재).

## 자체 점검 (저장 전)
- [ ] 계약 시그니처 유지(변경 필요 시 `deviation`)
- [ ] 직접 await 처리, `TryComplete` 서버 소유, 워커 생존, Dispose 1회, `_cts` 취소 경로
- [ ] `<remarks>`·선언부 근거 주석
- [ ] `{run_dir}/build/Pipeline.csproj`가 있으면 `dotnet build` 경고 0·오류 0

## 출력
1. `{run_dir}/02_dispatcher/ThreadDispatcher.cs`에 Write(이 디렉토리에만)
2. 최종 응답 첫 줄 `{"status":"done|failed","output":"<경로>","deviation":[…],"build_checked":true|false,"channel_capacity":N,"worker_count":N,"full_mode":"Wait","completion_owner":"server|connection"}`. SendMessage 사용 금지
