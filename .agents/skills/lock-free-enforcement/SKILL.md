---
name: lock-free-enforcement
description: ".NET 10 고성능 서버 코드에서 동기화 프리미티브(lock/System.Threading.Lock/Monitor/Mutex/SemaphoreSlim/ReaderWriterLockSlim/SpinLock)를 탐지하고, Interlocked·Channel·동시성 컬렉션으로 호출자 락을 제거할 수 있는지(동작 보존 조건 포함) 판정한다. lock-free-enforcer 에이전트 전용 스킬."
---

# Lock-Free Enforcement Skill

## 입력 읽기
1. `{run_dir}/00_input/meta.json` → `run_id`, `target_type`, `head_sha`
2. `{run_dir}/00_input/source.txt` **전체** Read(길면 분할, `index.md`는 탐색용). diff면 `+`의 새 락과 `-`로 사라진 락·Interlocked·풀링도 본다
3. 다른 에이전트의 결과를 기다리지 않는다

## 저장소 문맥 조사 (읽기 전용)
| 판정 항목 | 필수 조회 | 조회 불가 시 |
|---|---|---|
| `context` | `.csproj` SDK(Web → app, 클래스 라이브러리 → library), 네임스페이스·폴더(`*Tests*` → test), `Main`/`app.Run` → entrypoint | `unknown` + severity 하향 |
| 락이 보호하는 불변식 | 락 블록 안에서 읽고 쓰는 **모든 필드**와 그 필드의 다른 접근 경로 | necessary로 보수 판정 |
| 컬렉션 실제 타입·생산자/소비자 수 | 필드 선언, 호출자 | 대안 제시 보류(`unverified`) |

## 탐지 패턴 (정규식은 줄 단위, Allman 대응)
```
lock\s*\(                                   # C# lock 문 (다음 줄 { 포함)
\bLock\b\s+\w+\s*=|\.EnterScope\(|\.TryEnterScope\(   # System.Threading.Lock (.NET 9+)
Monitor\.(Enter|TryEnter|Exit|Wait|Pulse)
new\s+Mutex\b|\.WaitOne\(
new\s+SemaphoreSlim\(|\.WaitAsync\(|\.Wait\((?!\))    # SemaphoreSlim 사용처 (Task.Wait 와 구분: 수신자 타입 확인)
new\s+ReaderWriterLockSlim|\.Enter(Read|Write|UpgradeableRead)Lock\(
\bSpinLock\b|\bSpinWait\b
Interlocked\.|Volatile\.(Read|Write)          # 오용 여부 확인용
```

## 판정 기준

### replaceable — 호출자 락 제거 가능 (대체 코드 필수, 동작 보존 조건 명시)
| 기존 | 대안 | 보존 조건 |
|---|---|---|
| `lock` + 단일 `int/long` 증감·가산 | `Interlocked.Increment/Decrement/Add` | 같은 필드의 **모든** 쓰기가 Interlocked로 바뀌어야 함 |
| `lock` + 단일 참조 교체 | `Interlocked.Exchange/CompareExchange` | 교체 후 다른 필드와의 일관성 불필요 |
| `lock` + bool 플래그 | `int` 필드 + `Interlocked.CompareExchange(ref _flag, 1, 0) == 0` (bool 필드에는 int CAS 불가) | 플래그 표현을 int로 변경, 모든 접근 경로 수정 |
| `lock` + 단일 64-bit 읽기 | `Interlocked.Read` / `Volatile.Read` | 32-bit 프로세스 찢김 방지 목적일 때. Volatile은 가시성만, 원자성은 Interlocked |
| 생산자-소비자 큐 + `lock` | `Channel<T>` | 큐 외 다른 상태와 묶인 불변식이 없을 때. **BoundedChannel은 내부 lock을 쓴다** — "호출자 락 제거"이지 Lock-Free가 아님 |
| `ConcurrentQueue<T>` + `lock` | 큐 단독 | 락이 **큐 단일 연산만** 감쌀 때. `Count 확인 후 Dequeue` 같은 복합 연산이면 necessary |
| `Dictionary<K,V>` + `lock` | `ConcurrentDictionary<K,V>` | 단일 키 연산만일 때. `GetOrAdd` 팩토리는 중복 실행될 수 있음(부작용 없는 팩토리만). CD 쓰기 경로는 내부 lock |
| 불변 스냅샷 교체 | `ImmutableXxx` + CAS 루프 | 쓰기 빈도 낮음 |

```csharp
// Before
private int _counter;                   lock (_sync) { _counter++; }
// After
// Interlocked.Increment: 단일 CPU 원자 명령(lock xadd)으로 갱신하므로 락 없이도 찢김·유실 없음
private int _counter;                   Interlocked.Increment(ref _counter);

// Before: bool 플래그
private bool _started;                  lock (_sync) { if (_started) return; _started = true; }
// After: bool 에는 CAS 오버로드가 없으므로 int 로 표현
// Interlocked.CompareExchange(ref int): 비교·교체가 한 원자 명령이라 "처음 진입한 스레드만 0→1 성공"이 보장됨
private int _started;                   if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;

// Before: 생산자-소비자
private readonly Queue<Work> _queue = new();   lock (_sync) { _queue.Enqueue(w); }
// After
// Channel<T>(Bounded): 내부적으로 lock 으로 보호되는 큐 + 대기자 목록. 호출자 락과 수동 신호를 제거하고
// 백프레셔(FullMode.Wait)를 제공한다. Lock-Free 자료구조는 아니며 락 경합을 라이브러리 내부로 옮긴 것이다.
private readonly Channel<Work> _channel = Channel.CreateBounded<Work>(new BoundedChannelOptions(1024) { SingleReader = true });
await _channel.Writer.WriteAsync(w, ct);
```

### necessary — 유지 (`necessary: true`, 감점 제외, `[LOCK-REQUIRED]` 대상)
1. **복합 불변식**: 2개 이상 필드/컬렉션을 한 번에 일관되게 갱신
2. **Read-Check-Write에 부작용**: CAS 재시도 루프로 바꾸면 부작용이 중복 실행되거나 기아 위험
3. **외부 리소스 직렬화**: 파일 핸들·소켓·비스레드세이프 라이브러리 객체
4. **전체 순서 보장**: A 완료 후 B 시작 같은 순서 계약(Channel의 FIFO만으로 부족)
5. **async 임계 구역**: `SemaphoreSlim(1,1)` — await를 포함하는 상호배제의 **공인 프리미티브**. 제거 대상이 아니다
6. 필요한 `lock(object)`는 **.NET 9+ `System.Threading.Lock`** 전환을 권고(`lock (_lock)`이 `EnterScope`로 컴파일되어 Monitor보다 빠르고 박싱 실수 방지). `Monitor`와 `Lock` API 혼용은 `lock-api-mix` high

### interlocked-misuse
- CAS 반환값을 쓰지 않는 루프(재시도 조건 오류) — ABA와 다른 문제로 기록
- **ABA**: 같은 참조가 제거→재삽입되는 자료구조(스택 pop/push 등)에서 CAS가 "변하지 않았다"고 오판. 관리 참조라도 재삽입 전이가 있으면 성립. 재삽입이 없는 단순 상태 교체는 ABA가 아니다
- `Volatile.Read/Write`를 원자성 보장으로 오해(가시성만 제공)
- `SpinWait` 바쁜 대기의 장시간 사용

## 용어 규율
"Lock-Free"는 락 없는 진행 보장을 뜻한다. `Channel<T>`·`ConcurrentDictionary`·`ReaderWriterLockSlim`은 **thread-safe**이지 Lock-Free가 아니다. 보고서에는 "호출자 락 제거"라고 쓴다. TPL Dataflow는 정식 API지만 팀 표준 밖이라 대안으로 제시하지 않는다. `ConcurrentBag<T>`은 같은 스레드가 넣고 빼는 경우에만 권한다.

## 심각도 (context가 unknown이면 한 단계 하향)
| 심각도 | 기준 |
|---|---|
| critical | `lock-api-mix`(같은 상태를 Monitor와 Lock으로 보호 — 상호배제 깨짐), 데이터 손상을 부르는 Interlocked 오용 |
| high | hot path의 replaceable 락(요청/프레임당 진입), 복합 연산을 잃는 잘못된 "큐 단독" 상태 |
| medium | 저빈도 replaceable 락, `Lock` 미전환 necessary 락(권고) |
| low | 스타일·권고 수준 |

## 점수
`score = max(0, 100 − 25c − 12h − 5m − 2l)`, `necessary: true` 제외. 최종은 오케스트레이터 재계산.

## 출력
1. 공통 finding 스키마(id `LF-n`, `context`, `necessary`, 대체 코드 선언부에 CLAUDE.md 근거 주석)로 `{run_dir}/02_lockfree_findings.json`에 Write. Write는 이 파일에만
2. 락 없음 → `locks_found:false`, `findings:[]`, score 100
3. 최종 응답 첫 줄 `{"status":"done","output":"<경로>","counts":{...},"necessary":N,"score":N,"locks_found":true|false}`. SendMessage 사용 금지
