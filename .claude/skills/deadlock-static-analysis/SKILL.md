---
name: deadlock-static-analysis
description: ".NET 10 async/await 코드에서 데드락·스레드풀 기아·해제 누락 가능성이 있는 패턴(동기 블로킹, Monitor/Lock 보유 중 await, SemaphoreSlim 해제 경로, 락 순서, ConfigureAwait, async void, 취소 정책, Channel 완료)을 문맥(library/app/test/entrypoint)별 조건부 위험으로 정적 분석한다. deadlock-analyzer 에이전트 전용 스킬."
---

# Deadlock Static Analysis Skill

## 입력 읽기
1. `{run_dir}/00_input/meta.json` → `run_id`, `target_type`, `head_sha`
2. `{run_dir}/00_input/source.txt` **전체** Read(분할 허용). `{run_dir}/02_lockfree_findings.json`이 있으면 락 위치 참고(없어도 진행)
3. 재분석 호출이면 프롬프트의 `reanalysis_targets`를 먼저 처리하고 `reanalysis_response[]`에 기록

## 문맥(context) 판정 — 심각도의 전제
| context | 판단 근거 | 의미 |
|---|---|---|
| `library` | 클래스 라이브러리 SDK, public/internal API, 호출자 미상 | 호출자가 SynchronizationContext·제한 스케줄러를 쓸 수 있음 → 동기 블로킹은 **critical(conditional)** |
| `app` | `Microsoft.NET.Sdk.Web`, 엔드포인트·미들웨어·호스티드 서비스 | **ASP.NET Core에는 SynchronizationContext가 없다.** 동기 블로킹은 데드락이 아니라 **스레드풀 기아**(부하 시 정지) → high |
| `entrypoint` | `static Main`, `app.Run()`, 콘솔 부트스트랩 | 컨텍스트 없음 → 제외(정보성) |
| `test` | `*Tests*` 프로젝트, xUnit/NUnit 어트리뷰트 | 제외 또는 low |
| `unknown` | 판단 불가 | 한 단계 하향 + `unverified` |

## 8대 탐지 패턴

### Pattern 1: `sync-blocking`
수신자가 `Task`/`ValueTask`인 `.Result`, `.Wait(…)`, `.GetAwaiter().GetResult()`만 대상이다. `IdentityResult.Result`, `Monitor.Wait`, `SemaphoreSlim.Wait`, `ManualResetEventSlim.Wait`는 해당 없음(수신자 타입을 선언·시그니처로 확인).
- `library` → critical, `is_conditional: true`, `condition: "호출자가 SynchronizationContext(UI/레거시 ASP.NET) 또는 제한 스케줄러에서 호출"`
- `app` → high (`scenario`에 "요청 급증 시 스레드풀 고갈로 전체 지연", 데드락이라고 쓰지 않음)
- `entrypoint`/`test` → 제외 또는 low
- **컨텍스트가 없어도** 락 순환·제한 스케줄러(`ConcurrentExclusiveSchedulerPair` 등)로 교착할 수 있으므로 그런 정황이 보이면 conditional 유지
```csharp
// library, critical(conditional)
public Data Get() => _http.GetStringAsync(url).Result;
// 수정: 진정한 async 또는 별도 동기 API. ConfigureAwait(false)는 라이브러리 내부 await 에 적용
public async Task<Data> GetAsync(CancellationToken ct) => Parse(await _http.GetStringAsync(url, ct).ConfigureAwait(false));
```

### Pattern 2: `monitor-await`
- 직접 `lock (x) { await … }`는 **컴파일 오류(CS1996)** 다. 데드락으로 보고하지 말고 `pattern: "compile-error"` 정보성으로 기록.
- 실제 대상: `Monitor.Enter` + `try { await } finally { Monitor.Exit }`, `using (_lock.EnterScope()) { await }`(System.Threading.Lock도 스레드 친화적). 문제는 "continuation이 **다른 스레드**에서 재개되어 `Exit`이 `SynchronizationLockException`을 던지거나 락을 쥔 채 대기하는 것"이다(continuation이 이동 못 한다는 설명은 오답).
- 수정: `SemaphoreSlim(1,1)` + `WaitAsync` + try/finally.

### Pattern 3: `semaphore-release-path`
줄 간격이 아니라 **경로**를 추적한다: 취득(`await WaitAsync(…)` 또는 `Wait(…)`) 뒤 정상 return·예외·취소·조기 return·루프 break 각각에서 Release가 정확히 1회인가.
- `WaitAsync(timeout)`/`Wait(timeout)`이 `false`를 반환했는데 Release하는 것도 결함(카운트 초과 → `SemaphoreFullException`)
- try/finally의 **존재**가 안전의 증거가 아니다(취득이 try 안에 있으면 취득 실패에도 Release). 소유권 이전(다른 메서드가 Release 책임)이 명시되면 정상

### Pattern 4: `lock-order`
같은 실행 경로에서 프리미티브를 **중첩 보유**한 순서만 본다(순차 취득·해제는 해당 없음). A→B와 B→A가 동시에 실행 가능한 경로에 존재하면 high. Init 단계 전용 경로와 Runtime 전용 경로처럼 동시 실행 불가면 conditional.

### Pattern 5: `configure-await` (library만, medium)
정규식이 아니라 **구조**로 판정한다: `await <식>` 의 식 끝에 `.ConfigureAwait(` 가 없는가. `await using`/`await foreach`는 `ConfigureAwait`가 다른 위치에 붙는다(누락 판정 시 그 위치 확인). `Task.WhenAll/WhenAny`도 `.ConfigureAwait(false)` 적용 가능하므로 예외가 아니다.
- `app`/`test`/`entrypoint` 문맥은 **보고하지 않는다**(SynchronizationContext 없음). 접근성(public/internal)만으로 library를 판단하지 않는다.
- 누락은 교착의 증거가 아니라 라이브러리 관례 위반이다(medium, 데드락 시나리오 요구 없음).

### Pattern 6: `async-void` (medium)
`async\s+void\s+\w+\s*\(` — 이벤트 핸들러 시그니처(`object? sender, …EventArgs`)와 `async void` 람다 이벤트 구독은 제외하되, 그 안에 try/catch가 없으면 low로 "미처리 예외 시 프로세스 종료" 기록.

### Pattern 7: `cancellation-policy` (medium, 데드락 아님)
public async 메서드가 `CancellationToken`을 받지 않거나 내부 호출에 전달하지 않음. **무한 대기의 증명이 아니다.** 탈출 경로(타임아웃·완료 보장)가 전혀 없을 때만 high.

### Pattern 8: `channel-complete` (low)
`Channel.Create*` 후 `Writer.Complete()/TryComplete()` 호출이 종료 경로에 없음. 소비자가 `ReadAllAsync(ct)`로 취소 가능하면 low, 취소 토큰도 없으면 medium. 소유자(연결별/서버별)를 명시.

### 추가: `lock-api-mix` (critical)
같은 상태를 어떤 경로는 `Monitor`/`lock(object)`, 다른 경로는 `System.Threading.Lock`으로 보호 → 상호배제가 성립하지 않음.

## 심각도 요약 (context 반영 후)
| 심각도 | 패턴 |
|---|---|
| critical | library `sync-blocking`(conditional), `monitor-await`, `lock-api-mix` |
| high | app `sync-blocking`(기아), `semaphore-release-path`(반복 경로), `lock-order`(동시 실행 가능) |
| medium | library `configure-await`, `async-void`, `cancellation-policy`, 예외 경로 1곳 Release 누락 |
| low | `channel-complete`(취소 가능), 정보성 |

## 재현 시나리오 (finding마다)
1. 트리거 조건 2. 대기 지점과 호출 스택 3. 탈출 불가(또는 기아) 이유. conditional이면 조건이 성립하는 구체적 호출자 예.

## 점수
`score = max(0, 100 − 25c − 12h − 5m − 2l)`(참고용). 확정은 reviewer `final_findings`로 오케스트레이터가 계산.

## 출력
1. 공통 finding 스키마(id `DA-n`, `context`, `is_conditional/condition`, `scenario`, `fix_code`에 CLAUDE.md 근거 주석)로 `{run_dir}/03_deadlock_analysis[_r2].json`에 Write. Write는 이 파일에만
2. 패턴 없음 → `async_found:false`, `findings:[]`, score 100
3. 최종 응답 첫 줄 `{"status":"done","output":"<경로>","iteration":N,"counts":{...},"score":N,"async_found":true|false}`. SendMessage 사용 금지(reviewer에게 직접 요청하지 않는다)
