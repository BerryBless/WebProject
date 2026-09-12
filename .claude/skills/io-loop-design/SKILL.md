---
name: io-loop-design
description: ".NET 10 고성능 서버를 위해 System.IO.Pipelines 기반 비동기 IO 루프를 설계하고 C# 코드를 작성한다. 계약(불변)의 PipeOptions·메시지 타입을 준수해 Fill/Read 루프, 소유권 분리 메시지, 상호 취소, 오류 전파를 갖춘 컴파일되는 구현을 {run_dir}/02_io_loop/IoLoop.cs에 출력한다. io-loop-designer 에이전트 전용 스킬."
---

# IO Loop Design Skill

## 입력 읽기
1. `{run_dir}/00_design_brief.md` — 프로토콜, 처리량, **메시지 최대 크기**, 종료·오류 정책
2. `{run_dir}/02_interface_contract.cs` — **불변 입력.** `ParsedMessage`(풀 버퍼 소유 복사본), `IMessageDispatcher`, `PipelineConstants`(백프레셔 상수). 없으면 작업하지 않고 `error` 보고

## 설계 규칙 (감사 체크리스트와 1:1)
| 규칙 | 이유 |
|---|---|
| `PauseWriterThreshold ≥ MaxFrameSize + HeaderSize` (계약 상수 사용) | 작으면 완전한 프레임이 도착하기 전에 writer가 pause되고 reader는 consumed를 못 옮겨 **교착** |
| 입력 부족 시 `AdvanceTo(consumed, buffer.End)` | `examined == consumed`면 파이프가 새 데이터 없음을 모르고 같은 버퍼를 즉시 재반환 → **CPU 100% 스핀** |
| `SequenceReader<byte>`는 동기 헬퍼 안에서만 | ref struct는 await를 가로질러 보존 불가(컴파일 오류) |
| 디스패치 전 페이로드를 풀 버퍼로 1회 복사 | `AdvanceTo`가 세그먼트를 풀에 반환하므로 파이프 슬라이스를 채널로 넘기면 **use-after-return**. 이 복사는 Zero-copy 위반이 아니라 소유권 이전이다 |
| 링크된 CTS로 상호 취소 | `Task.WhenAll`은 상대 루프를 끝내지 않는다. 리더가 죽어도 `ReceiveAsync`는 데이터가 올 때까지 대기 |
| `CompleteAsync(ex)`로 오류 전파, `IsCanceled` 처리 | 예외 없는 Complete는 잘린 스트림을 정상 EOF로 오인시킨다 |
| EOF 시 잔여 부분 프레임은 프로토콜 오류 | 조용히 버리면 데이터 손상이 숨는다 |
| `Socket.ReceiveAsync(Memory<byte>, …)` 사용 | 소켓당 `AwaitableSocketAsyncEventArgs`를 내부 캐시. 직접 SAEA 관리·`Pin` 불필요. `SetBuffer(byte*, int)` 오버로드는 **존재하지 않는다** |
| `useSynchronizationContext: false` | 서버에는 컨텍스트가 없고 캡처 비용만 든다 |
| CLAUDE.md: public `<remarks>` 3항목, `Pipe/PipeOptions/Memory/SequenceReader/IMemoryOwner/CTS` 선언부 근거 `//` | 프로젝트 규칙. 감사 대상 |

## 백프레셔 기준 (계약이 결정, 여기서는 산출 근거)
```
PauseWriterThreshold  = max(2 × (MaxFrame + Header), 프로파일 기본값)
ResumeWriterThreshold = PauseWriterThreshold / 2
MinimumSegmentSize    = 4KB(기본) / 8~16KB(고처리량·대형 프레임)
```
| 프로파일 | 기본값(프레임이 작을 때) |
|---|---|
| 고처리량(>100k msg/s) | 64KB / 32KB / 8KB |
| 균형 | 16KB / 8KB / 4KB |
| 저지연 | **min 은 여전히 2×(MaxFrame+Header)** — 4KB/2KB 같은 값은 프레임이 그보다 작을 때만 |

## 참조 구현 (빌드 검증: net10.0, TreatWarningsAsErrors, 경고 0·오류 0 — 2026-09-13)
계약 템플릿의 타입(`Pipeline.Contract`)과 함께 컴파일된다. 브리프에 맞게 프레임 파서(`TryParseMessage`)와 연결 식별자 전달만 조정한다.

```csharp
// ===== 02_interface_contract.cs (계약 템플릿 — 감독자가 확정) =====
// 02_interface_contract.cs — IO 루프 ↔ 디스패처 계약 (감독자가 확정, 워커에게는 불변 입력)
using System.Buffers;
using System.IO.Pipelines;

namespace Pipeline.Contract;

/// <summary>IO 루프가 파싱해 디스패처로 넘기는 메시지. 파이프 버퍼와 수명이 분리된 <b>소유 복사본</b>이다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. 생산자(IO 루프)가 만들고 소비자(워커) 1개가 처리·해제한다.</description></item>
/// <item><description><b>Memory Allocation:</b> 메시지당 풀 버퍼 1개(<see cref="IMemoryOwner{T}"/>)를 빌린다. 파이프 세그먼트를 참조하지
/// 않으므로 <c>PipeReader.AdvanceTo</c> 이후에도 유효하다. <b>소유권은 소비자에게 이전</b>되며 소비자가 반드시 <see cref="Dispose"/>로 반환한다.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환(값 타입 컨테이너).</description></item>
/// </list>
/// </remarks>
public readonly struct ParsedMessage : IDisposable
{
    // IMemoryOwner<byte>: MemoryPool<byte>.Shared(ArrayPool 기반)에서 빌린 버퍼의 소유 핸들.
    // 파이프 세그먼트를 가리키는 ReadOnlySequence 대신 이것을 쓰는 이유는 AdvanceTo 가 세그먼트를 풀에 반환한 뒤에도
    // 워커가 안전하게 읽어야 하기 때문이다(use-after-return 방지). Dispose 시 풀로 되돌아간다.
    private readonly IMemoryOwner<byte> _owner;

    public ParsedMessage(long connectionId, ushort messageType, IMemoryOwner<byte> owner, int length)
    {
        ConnectionId = connectionId;
        MessageType = messageType;
        _owner = owner;
        Length = length;
    }

    public long ConnectionId { get; }
    public ushort MessageType { get; }
    public int Length { get; }

    // ReadOnlyMemory<byte>: 풀 버퍼 위의 (ref, offset, length) 뷰라 슬라이스에 할당이 없고, 실제 길이만 노출해
    // Rent 가 더 큰 버퍼를 돌려준 나머지 영역이 소비자에게 보이지 않게 한다.
    public ReadOnlyMemory<byte> Payload => _owner.Memory.Slice(0, Length);

    /// <summary>풀 버퍼를 반환한다. 소비자가 처리 완료 후 정확히 1회 호출한다.</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not Thread-safe. 소유자 1개만 호출.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation guaranteed. 중복 호출은 풀 오염이므로 금지.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    public void Dispose() => _owner.Dispose();
}

/// <summary>IO 루프가 파싱한 메시지를 워커에게 넘기는 단일 진입점.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 여러 연결(IO 루프)이 동시에 호출할 수 있다.</description></item>
/// <item><description><b>Memory Allocation:</b> 큐 공간이 있으면 할당 0(동기 완료 ValueTask). 꽉 찼을 때만 대기자 등록 1회.
/// 메시지 소유권은 호출 성공 시 디스패처로 이전된다(실패·취소 시 호출자가 Dispose).</description></item>
/// <item><description><b>Blocking:</b> 비동기(Non-blocking). 채널이 꽉 차면 공간이 날 때까지 await — 이것이 백프레셔다.</description></item>
/// </list>
/// </remarks>
public interface IMessageDispatcher
{
    ValueTask DispatchAsync(ParsedMessage message, CancellationToken ct);
}

/// <summary>브리프에서 확정한 파이프·프레이밍 상수. 두 워커가 같은 값을 쓴다.</summary>
public static class PipelineConstants
{
    public const int HeaderSize = 8;                 // [int Length][ushort Type][ushort Reserved]
    public const int MaxFrameSize = 64 * 1024;       // 브리프 "메시지 최대 크기"
    // 하드 제약: PauseWriterThreshold ≥ MaxFrameSize + HeaderSize. 작으면 완전한 프레임이 오기 전에 writer 가 pause 되어 교착한다.
    public const int PauseWriterThreshold = 2 * (MaxFrameSize + HeaderSize);
    public const int ResumeWriterThreshold = PauseWriterThreshold / 2;
    public const int MinimumSegmentSize = 4096;

    // PipeOptions: useSynchronizationContext=false 로 continuation 을 스레드풀에 직접 게시(서버에는 컨텍스트가 없고 캡처 비용만 든다).
    public static readonly PipeOptions ReceivePipeOptions = new(
        pauseWriterThreshold: PauseWriterThreshold,
        resumeWriterThreshold: ResumeWriterThreshold,
        minimumSegmentSize: MinimumSegmentSize,
        useSynchronizationContext: false);
}

// ===== 02_io_loop/IoLoop.cs =====
// 02_io_loop/IoLoop.cs — System.IO.Pipelines 기반 수신 루프 템플릿 (io-loop-design 스킬)
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net.Sockets;
using Pipeline.Contract;

namespace Pipeline.Io;

/// <summary>소켓 1개당 Fill(소켓→Pipe)·Read(Pipe→파싱→디스패치) 두 루프를 돌리는 수신 IO 루프.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 인스턴스는 연결 간 공유되지만 상태가 없어 Thread-safe. 연결별 상태는 메서드 지역에만 있다.</description></item>
/// <item><description><b>Memory Allocation:</b> 연결당 Pipe 1개 + 링크된 CTS 1개. 수신 경로는 파이프 세그먼트 풀을 쓰므로 정상 상태 할당 0.
/// 메시지당 풀 버퍼 1개를 빌려 디스패처로 소유권을 넘긴다.</description></item>
/// <item><description><b>Blocking:</b> 비동기(Non-blocking). 소켓 수신·FlushAsync·ReadAsync·DispatchAsync 전부 await.</description></item>
/// </list>
/// </remarks>
public sealed class IoLoop
{
    private readonly IMessageDispatcher _dispatcher;

    public IoLoop(IMessageDispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>연결 하나를 종료(EOF·오류·취소)까지 처리한다.</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 연결마다 1회 호출. 동일 소켓으로 동시 호출 금지.</description></item>
    /// <item><description><b>Memory Allocation:</b> Pipe·CTS·Task 2개(연결당 고정 비용).</description></item>
    /// <item><description><b>Blocking:</b> 비동기. 두 루프가 모두 끝나야 반환한다.</description></item>
    /// </list>
    /// </remarks>
    public async Task ProcessConnectionAsync(Socket socket, long connectionId, CancellationToken ct)
    {
        var pipe = new Pipe(PipelineConstants.ReceivePipeOptions);
        // CancellationTokenSource(linked): 한쪽 루프가 오류로 끝나면 다른 루프도 깨우기 위한 연결 수명 토큰.
        // Task.WhenAll 은 상대 루프를 종료시키지 않으므로(리더가 죽어도 ReceiveAsync 는 데이터가 올 때까지 대기) 명시적 취소가 필요하다.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Task fill = FillPipeAsync(socket, pipe.Writer, linked);
        Task read = ReadPipeAsync(pipe.Reader, connectionId, linked);
        await Task.WhenAll(fill, read).ConfigureAwait(false);
    }

    private static async Task FillPipeAsync(Socket socket, PipeWriter writer, CancellationTokenSource linked)
    {
        CancellationToken ct = linked.Token;
        Exception? error = null;
        try
        {
            while (true)
            {
                // GetMemory: 파이프 세그먼트 풀에서 직접 쓰기 영역을 받아 복사 없이 수신한다.
                Memory<byte> buffer = writer.GetMemory(PipelineConstants.MinimumSegmentSize);
                // Socket.ReceiveAsync(Memory): 소켓당 AwaitableSocketAsyncEventArgs 를 내부 캐시하므로 수신마다 SAEA 할당이 없다.
                int bytesRead = await socket.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
                if (bytesRead == 0) break;                                   // 정상 EOF
                writer.Advance(bytesRead);

                FlushResult flush = await writer.FlushAsync(ct).ConfigureAwait(false);
                if (flush.IsCompleted || flush.IsCanceled) break;           // 리더 종료 또는 CancelPendingFlush
            }
        }
        catch (OperationCanceledException) { /* 연결 수명 취소 — 정상 종료 경로 */ }
        catch (SocketException ex) when (IsConnectionReset(ex)) { /* 상대가 끊음 — 리더에게는 EOF 로 보인다 */ }
        catch (Exception ex) { error = ex; }
        finally
        {
            // 예외를 넘겨야 리더의 ReadAsync 가 잘린 스트림을 정상 EOF 로 오인하지 않는다.
            await writer.CompleteAsync(error).ConfigureAwait(false);
            if (error is not null) linked.Cancel();                          // 리더도 깨운다
        }
    }

    private async Task ReadPipeAsync(PipeReader reader, long connectionId, CancellationTokenSource linked)
    {
        CancellationToken ct = linked.Token;
        Exception? error = null;
        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync(ct).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;
                if (result.IsCanceled) break;                                // CancelPendingRead 로 깨어남

                SequencePosition consumed = buffer.Start;
                SequencePosition examined = buffer.End;
                bool needMore = false;
                try
                {
                    // 파싱은 동기 헬퍼에서만 한다: SequenceReader<byte>(ref struct)는 await 를 가로질러 보존할 수 없다.
                    while (TryParseMessage(buffer.Slice(consumed), connectionId, out ParsedMessage message, out SequencePosition next))
                    {
                        consumed = next;
                        try
                        {
                            // 소유권 이전: 성공하면 워커가 Dispose, 실패·취소면 여기서 Dispose
                            await _dispatcher.DispatchAsync(message, ct).ConfigureAwait(false);
                        }
                        catch
                        {
                            message.Dispose();
                            throw;
                        }
                    }
                    needMore = true;
                }
                finally
                {
                    // 완전 소비면 examined=consumed 로 두어도 되지만, 입력 부족으로 빠져나온 경우 examined 는 반드시 buffer.End 다.
                    // consumed 와 같은 위치를 examined 로 주면 파이프가 "새 데이터 없음"으로 보지 않아 같은 버퍼를 즉시 재반환 → CPU 100% 스핀.
                    reader.AdvanceTo(consumed, needMore ? buffer.End : consumed);
                }

                if (result.IsCompleted)
                {
                    // writer 가 끝났는데 미완성 프레임이 남아 있으면 프로토콜 오류다(조용히 버리지 않는다).
                    if (!buffer.Slice(consumed).IsEmpty)
                        throw new InvalidDataException($"connection {connectionId}: truncated frame ({buffer.Slice(consumed).Length} bytes)");
                    break;
                }
            }
        }
        catch (OperationCanceledException) { /* 정상 취소 */ }
        catch (Exception ex) { error = ex; }
        finally
        {
            await reader.CompleteAsync(error).ConfigureAwait(false);
            if (error is not null) linked.Cancel();                          // Fill 루프의 ReceiveAsync 를 깨운다
        }
    }

    /// <summary>프레임 하나를 파싱해 풀 버퍼로 복사한 메시지를 만든다. 입력 부족이면 false, 손상이면 예외.</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 순수 함수. Thread-safe.</description></item>
    /// <item><description><b>Memory Allocation:</b> 성공 시 풀 버퍼 1개 Rent(소유권은 반환 메시지로 이전). 실패 시 0.</description></item>
    /// <item><description><b>Blocking:</b> 동기 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    private static bool TryParseMessage(ReadOnlySequence<byte> input, long connectionId, out ParsedMessage message, out SequencePosition next)
    {
        message = default;
        next = input.Start;
        // SequenceReader<byte>: 세그먼트 경계를 넘는 정수 읽기를 복사 없이 처리하는 ref struct. 이 메서드 안에서만 살아야 한다.
        var reader = new SequenceReader<byte>(input);
        if (!reader.TryReadLittleEndian(out int length) || !reader.TryReadLittleEndian(out short type) || !reader.TryReadLittleEndian(out short _))
            return false;                                                    // 헤더 부족
        if (length < 0 || length > PipelineConstants.MaxFrameSize)
            throw new InvalidDataException($"connection {connectionId}: invalid frame length {length}");
        if (reader.Remaining < length)
            return false;                                                    // 본문 부족 → examined=End 로 더 받는다

        // MemoryPool<byte>.Shared: ArrayPool 기반 풀에서 length 이상 버퍼를 빌린다. 파이프 세그먼트 참조를 워커에게 넘기지 않기 위한 1회 복사.
        IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(length);
        input.Slice(reader.Position, length).CopyTo(owner.Memory.Span);
        reader.Advance(length);
        message = new ParsedMessage(connectionId, unchecked((ushort)type), owner, length);
        next = reader.Position;
        return true;
    }

    private static bool IsConnectionReset(SocketException ex) =>
        ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.OperationAborted;
}
```

## AdvanceTo 요약
```csharp
// 완전 소비: consumed == examined == 파싱 끝
reader.AdvanceTo(next, next);
// 입력 부족: consumed 는 마지막 완전 프레임 끝, examined 는 반드시 buffer.End
reader.AdvanceTo(consumed, buffer.End);
// 금지: 입력 부족인데 examined == consumed → 스핀
```

## 자체 점검 (저장 전)
- [ ] 계약 타입·시그니처를 바꾸지 않았다(바꿔야 하면 `deviation`)
- [ ] `examined` 규칙, 소유권 복사, 링크된 CTS, `Complete(ex)`, `IsCanceled`, EOF 잔여 프레임
- [ ] public 멤버 `<remarks>`, 선언부 근거 주석
- [ ] `{run_dir}/build/Pipeline.csproj`가 있으면 `dotnet build -nologo -v q`로 경고 0·오류 0 확인

## 출력
1. `{run_dir}/02_io_loop/IoLoop.cs`에 Write(이 디렉토리에만)
2. 최종 응답 첫 줄 `{"status":"done|failed","output":"<경로>","deviation":[…],"build_checked":true|false,"pause_threshold":N,"max_frame":N}`. SendMessage 사용 금지(형제와 협의하지 않는다)
