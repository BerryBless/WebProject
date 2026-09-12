---
name: lock-justification-audit
description: ".NET 10 서버 코드의 모든 락(lock/System.Threading.Lock/Monitor/ReaderWriterLockSlim/Mutex/SemaphoreSlim(1,1)) 위치에 표준 정당화 주석([LOCK-REQUIRED])이 존재하고 내용이 충분한지, public API <remarks>의 Blocking 서술과 일치하는지 감사한다. lock-justification-auditor 에이전트 전용 스킬."
---

# Lock Justification Audit Skill

## 입력 읽기
1. `{run_dir}/00_input/meta.json` → `run_id`, `target_type`, `head_sha`
2. `{run_dir}/00_input/source.txt` **전체** Read. 다른 에이전트의 락 목록을 기다리지 않고 **직접 전수 탐지**한다

## 저장소 문맥 조사 (읽기 전용)
| 판정 항목 | 필수 조회 | 조회 불가 시 |
|---|---|---|
| 락 문 위 5줄 (diff에 잘렸을 때) | 파일 원본(`git show {head_sha}:<경로>` 또는 작업 트리) | `unverified`(주석 존재 미확인) — high로 확정하지 않음 |
| `<remarks>` 정합성 | 락을 포함한 public 메서드의 XML 주석 | `remarks_consistent: "n/a"` |
| `context` | csproj SDK·네임스페이스 | `unknown` |

## 탐지 대상 (줄 단위, Allman 스타일 대응 — `{` 위치에 의존하지 않는다)
```
lock\s*\(                                          # lock (x) — 같은 줄에 { 가 없어도 매치
\.EnterScope\(|\.TryEnterScope\(                   # System.Threading.Lock (.NET 9+)
Monitor\.(Enter|TryEnter)\s*\(
\.Enter(Read|Write|UpgradeableRead)Lock\s*\(       # 필드명에 의존하지 않음 (_rwLock 하드코딩 금지)
new\s+Mutex\b|\.WaitOne\s*\(
new\s+SemaphoreSlim\s*\(\s*1\s*,\s*1              # async 상호배제 — 정당화 대상
\bSpinLock\b
```
같은 프리미티브의 여러 사용처는 **사용처마다** 검사한다(선언 1회가 아니라 진입 지점).

## 필수 주석 표준
락 문(진입 지점) **위 0~5줄** 안에서 블록이 **끝나야** 한다(블록 시작이 아니라 마지막 줄 기준).
```csharp
// ===== [LOCK-REQUIRED] =====
// WHY-LOCK: {Interlocked/Channel/불변 자료구조로 해결 불가한 구체적 이유}
// CONTENTION-OPT: {락 범위·구간·프리미티브 선택 등 구체적 조치}
// ===========================
lock (_syncRoot)
{
```
**허용 변형:** `[LOCK-REQUIRED]`, `WHY-LOCK:`, `CONTENTION-OPT:` 세 키워드가 같은 주석 블록에 있으면 형식 충족(구분선 자유, `///` 블록 안이어도 됨).

## 내용 품질
**WHY-LOCK 충분:** "두 컬렉션을 원자적으로 갱신 — Interlocked는 단일 참조만", "소켓 핸들 OS 수준 직렬화", "스택 pop/push 재삽입으로 CAS ABA 성립", "await를 포함한 임계 구역이라 SemaphoreSlim(1,1)". **불충분:** "동시성 보호 위해", "thread-safe", 빈 값.
**CONTENTION-OPT 충분:** "락 내부 2줄, 검증 O(n)은 락 밖", "읽기 95%라 RWLS(읽기 병렬)", "락 없이 먼저 시도 후 충돌 시 진입", "System.Threading.Lock 채택". **불충분:** "없음", "N/A", "최적화 완료", 코드와 불일치(예: I/O가 락 안에 있는데 "락 밖으로 이동").

## 심각도 (에이전트 정의와 동일 — critical 없음)
| 심각도 | 패턴 | 조건 |
|---|---|---|
| high | `justification-missing` | 블록 자체 없음 |
| medium | `justification-insufficient` | 블록은 있으나 WHY-LOCK 또는 CONTENTION-OPT 불충분/코드 불일치 |
| medium | `remarks-inconsistent` | 락을 보유하는 public 메서드의 `<remarks>`가 "Non-blocking"/"Lock-free"라고 서술 |
| low | `justification-nonstandard` | 세 키워드 존재·내용 충분, 형식만 다름 |

## 예시
**high (없음):**
```csharp
lock (_syncRoot)
{
    _items.Add(item); _index[item.Id] = item;
}
```
**medium (CONTENTION-OPT 불충분):**
```csharp
// ===== [LOCK-REQUIRED] =====
// WHY-LOCK: 두 컬렉션을 원자적으로 업데이트해야 함
// CONTENTION-OPT: 없음
// ===========================
lock (_syncRoot) { ... }
```
**Accepted (블록 끝이 락 바로 위, 5줄 이내):**
```csharp
// ===== [LOCK-REQUIRED] =====
// WHY-LOCK: _items 와 _index 를 동시에 일관되게 유지해야 함. Interlocked 는 단일 참조 교체만 원자적.
// CONTENTION-OPT: 락 내부 2줄. 유효성 검사(O(n))는 진입 전 수행. 읽기 10배 우세지만 쓰기 시 두 컬렉션
//                 동시 접근이라 RWLS 대신 System.Threading.Lock(EnterScope) 채택.
// ===========================
// Lock: .NET 9+ 전용 뮤텍스. lock 문이 EnterScope 로 컴파일되어 Monitor 의 객체 헤더 경합·박싱 위험이 없다
private readonly Lock _syncRoot = new();
```
(선언부 `//` 근거 주석은 CLAUDE.md 규칙이며 `[LOCK-REQUIRED]`와 **병행**한다. 하나가 다른 하나를 대체하지 않는다.)

## 점수
`score = max(0, 100 − 12h − 5m − 2l)`. 최종은 오케스트레이터 재계산.

## 출력
1. 공통 finding 스키마(id `LJ-n`, `comment_present`, `why_lock_verdict`, `contention_opt_verdict`, `remarks_consistent`, `required_fix`에 그 락에 맞는 실제 주석 초안)로 `{run_dir}/02_lockjustification_findings.json`에 Write. Write는 이 파일에만
2. 락 없음 → `locks_found:false`, score 100
3. 최종 응답 첫 줄 `{"status":"done","output":"<경로>","counts":{...},"score":N,"locks_found":true|false}`. SendMessage 사용 금지
