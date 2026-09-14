---
name: load-test-audit
description: "Pipelines·Channel<T> 서버 코드를 부하 테스트 관점(버퍼 수명, examined 스핀, 완료 전파, 백프레셔 교착, 워커 생존, 취소 누수)에서 감사해 점수와 3단계 판정을 JSON으로 낸다. load-test-auditor 전용."
---

# Load Test Audit Skill

## 입력 읽기 (4개 전부 필수)
1. `{run_dir}/02_interface_contract.cs`
2. `{run_dir}/02_io_loop/IoLoop.cs`
3. `{run_dir}/02_dispatcher/ThreadDispatcher.cs`
4. `{run_dir}/00_design_brief.md` (최대 프레임·디스패처 범위·예외 정책)
하나라도 없거나 비어 있으면 `inputs_complete:false`, `verdict:null`로 저장하고 종료한다. **부분 입력으로 APPROVE를 내지 않는다.** 재감사(`_rN`)면 이전 라운드 JSON도 읽어 id로 해소 여부를 추적한다.

## 감사 영역 (finding `area`)

### 1. `buffer-lifetime` (CRITICAL)
파이프 슬라이스(`ReadOnlySequence<byte>`/그 위의 `ReadOnlyMemory`)가 `AdvanceTo` 이후까지 생존하는 경로: 채널로 전달, 필드 저장, 비동기 핸들러 캡처. **풀 버퍼로 1회 복사해 소유권을 이전한 메시지는 정상**이다(Zero-copy 위반으로 잡지 않는다). 위반은 메시지당 `ToArray()`/`new byte[n]`/불필요한 2중 복사.

### 2. `examined-spin` (CRITICAL)
입력 부족으로 파싱 루프를 빠져나온 뒤 `AdvanceTo(consumed, examined)`에서 `examined == consumed`. 파이프가 새 데이터 없음을 모르고 같은 버퍼를 즉시 재반환 → CPU 100%. `examined`는 `buffer.End`여야 한다. 반대로 `AdvanceTo` 자체가 없는 경로도 CRITICAL(버퍼 영구 보유).

### 3. `completion` (CRITICAL~MEDIUM) — **경로 추적, 문법 위치 아님**
- 양단 Complete가 모든 종료 경로(정상 EOF·예외·취소·조기 return)에서 정확히 1회. `finally`, `await using` 래퍼, catch에서 `Complete(ex)` 후 정상 경로 `Complete()` 모두 유효
- 오류 시 `Complete(ex)`/`CompleteAsync(ex)`로 전파하는가(예외 없는 Complete → 잘린 스트림이 정상 EOF로 보임: HIGH)
- `ReadResult.IsCanceled`/`FlushResult.IsCanceled` 처리(MEDIUM)
- 리더 실패가 Fill 루프를 깨우는가(상호 취소·소켓 Shutdown). `Task.WhenAll`만 있으면 HIGH(유휴 소켓에서 영구 대기)
- EOF 시 잔여 부분 프레임을 표면화하는가(조용히 폐기 → MEDIUM)
- `Pipe`는 `IDisposable`이 아니다. Dispose 검사 금지. 재사용 시 `Reset()`만 확인

### 4. `backpressure-deadlock` (CRITICAL)
`PauseWriterThreshold < MaxFrameSize + HeaderSize`(브리프·계약 대조). 완전한 프레임 전에 writer가 pause → reader는 consumed를 못 옮김 → 교착. `useSynchronizationContext` 기본값 사용은 LOW.

### 5. `channel-completion` (CRITICAL~HIGH)
- 연결 종료 경로가 서버 공유 채널을 `Complete` → 다른 연결 `ChannelClosedException`(CRITICAL)
- `Complete()` 2회 가능 경로(예외) → `TryComplete` 필요(HIGH)
- 폐기 모드(`DropNewest/DropOldest/DropWrite`)에서 폐기 메시지 Dispose 누락(HIGH)
- 강제 종료 경로에서 잔여 메시지 Dispose 누락(MEDIUM)

### 6. `worker-survival` (HIGH)
핸들러 예외·핸들러 내부 OCE로 워커 루프가 탈출해 조용히 워커 수 감소. 메시지 `Dispose` 누락(풀 미스)·중복(풀 오염 → CRITICAL).

### 7. `allocation` (HIGH~LOW)
`readonly struct : IThreadPoolWorkItem`을 `UnsafeQueueUserWorkItem(IThreadPoolWorkItem,…)`에 전달(박싱, 백프레셔 누수 → HIGH). 메시지당 `ToArray()`·클로저 캡처 델리게이트(MEDIUM). "Channel은 lock-free"류의 잘못된 주장(LOW, 문서 정정).

### 8. `cancellation` (MEDIUM)
`FlushAsync()/ReadAsync()/WriteAsync()/ReceiveAsync()`에 토큰 누락. 정상 취소(OCE catch 후 Complete)는 결함이 아니다.

### 9. `blocking` (HIGH)
핫 패스(`FillPipeAsync/ReadPipeAsync/WorkerLoop/DispatchAsync`)의 `.Result`·`.Wait()`·동기 `SemaphoreSlim.Wait`·`lock` 안 I/O.

### 10. `project-rules` (MEDIUM)
public 멤버 `<remarks>`(Thread Safety·Memory Allocation·Blocking) 누락, `Pipe/PipeOptions/Channel/IMemoryOwner/SemaphoreSlim/CTS` 선언부 내부 동작 근거 주석 누락, 락에 `[LOCK-REQUIRED]` 없음.

## 점수·판정 (단일 정본)
```
score = max(0, 100 − 25×critical − 10×high − 4×medium − 1×low)
BLOCK : critical ≥ 1 또는 score < 60
REQUEST CHANGES : high ≥ 1 또는 score < 80
APPROVE : 그 외
inputs_complete == false → verdict null
```
감독자·오케스트레이터는 이 JSON의 verdict를 그대로 쓴다(재해석 금지).

## 출력
1. `{run_dir}/03_load_test_audit[_rN].json`: `domain, run_id, round, inputs_complete, findings[]{id:"LT-n", severity, area, owner:"io-loop|dispatcher|contract", file, scenario, detail, fix_code, resolved_from_round}, unverified[], counts, score, verdict`
2. 같은 이름 `.md`: 사람용 요약(판정·점수·영역별 발견·수정 코드)
3. Write는 이 두 파일에만. 최종 응답 첫 줄 `{"status":"done","output":"<json>","round":N,"inputs_complete":true,"counts":{...},"score":N,"verdict":"…"}`. SendMessage 사용 금지
