---
name: pooling-enforcement
description: ".NET 10 서버 hot path에 ValueTask·ReadOnlySpan<T>·ArrayPool<T>.Shared·stackalloc 적용을 강제하고 소유권 위반을 탐지해 동작 보존 수정 코드를 제시한다. pooling-enforcer 전용."
---

# Pooling Enforcement Skill

## 입력 읽기

1. `{run_dir}/00_input/meta.json`에서 `run_id`, `target_type`, `head_sha` 확인
2. `{run_dir}/00_input/source.txt` **전체** Read. diff면 `+`의 새 패턴과 `-`로 제거된 풀링·Return·`using`도 본다
3. 스캐너 결과를 기다리지 않는다. 버퍼 할당(`new byte[]`, `new char[]`, `new T[n]`)은 직접 Grep한다

## 저장소 문맥 조사 (읽기 전용)

| 판정 항목 | 필수 조회 | 조회 불가 시 |
|----------|----------|------------|
| Rent 배열의 모든 종료 경로 | 해당 메서드 전체 + 배열을 넘겨받는 호출 대상(소유권 이전 여부) | `unverified` + 방향만 제시 |
| Task→ValueTask 동기 완료율 | 메서드 본문의 캐시 히트/조기 반환 경로, 호출자의 소비 방식(단일 await인지, WhenAll인지) | 제안하지 않음 |
| Substring 결과 소유권 | 반환값이 필드·컬렉션·다른 스레드로 전달되는지 | 제안하지 않음 |
| 0 초기화 의존 | `new T[n]` 이후 쓰기 전 읽기가 있는지 | Clear 포함 제안 |
| LangVersion | `.csproj`(C# 13 여부: await 사이 Span 지역, params span) | C# 12 기준 보수 판정 |

`target_type=pr`이면 `git show {head_sha}:<경로>`.

## 기법 1: ValueTask

```csharp
// 교체 대상: 동기 완료 경로가 실제로 자주 타는 hot path 메서드
public async Task<byte[]> ReadFromCacheAsync(string key)
{
    if (_cache.TryGetValue(key, out var cached)) return cached;   // 히트마다 Task<byte[]> 할당
    return await FetchAsync(key);
}
// 수정 (public 이므로 CLAUDE.md remarks 필수)
/// <summary>캐시 우선으로 바이트 배열을 읽는다.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. _cache 는 ConcurrentDictionary.</description></item>
/// <item><description><b>Memory Allocation:</b> 캐시 히트 시 힙 할당 0(ValueTask 가 값을 인라인 보관). 미스 시 상태 머신 1회.</description></item>
/// <item><description><b>Blocking:</b> 비동기(Non-blocking). 히트 경로는 동기 완료.</description></item>
/// </list>
/// </remarks>
// ValueTask<T>: 결과 또는 Task 를 구조체 하나에 담아 동기 완료 시 Task 객체 할당을 생략한다
public async ValueTask<byte[]> ReadFromCacheAsync(string key) { ... }

// Task.FromResult 교체는 캐시되지 않는 값일 때만: .NET 6+ 는 bool, -1~8 정수, null 등 공통 값을 캐시한다
return new ValueTask<T>(value);     // 참고: ValueTask 자체는 struct 지만 await 시 상태 머신은 힙에 박스될 수 있다
```

**오용 탐지 (`valuetask-misuse`):** 같은 인스턴스 2회 await · 미완료 상태에서 `.Result`/`.GetAwaiter().GetResult()` · `Task.WhenAll/WhenAny`에 `AsTask()` 없이 전달 · `IValueTaskSource` 기반 결과를 보관 후 재소비 · `.Preserve()` 없이 여러 소비자에게 노출.
로컬에 저장했다가 **정확히 1회** await하는 것은 정상이다.

**교체 금지:** 다중 소비자, 동기 완료가 드문 순수 I/O, 호출자가 `Task`를 요구하는 공개 계약.

## 기법 2: ReadOnlySpan<T> / Memory<T>

```csharp
// 교체 대상 (호출자가 복사본을 소유할 필요가 없을 때만)
string sub = input.Substring(offset, length);          → ReadOnlySpan<char> sub = input.AsSpan(offset, length);
byte[] slice = buffer.Skip(o).Take(n).ToArray();       → ReadOnlySpan<byte> slice = buffer.AsSpan(o, n);
//   주의: Skip/Take 는 범위 초과 시 잘라내지만 AsSpan 은 예외를 던진다 → 길이 검사 포함

// Split 첫 토큰: 구분자 부재 동작을 보존해야 한다
string part = line.Split(',')[0];                       // 쉼표 없으면 전체 문자열
int idx = line.IndexOf(',');
ReadOnlySpan<char> part = idx < 0 ? line.AsSpan() : line.AsSpan(0, idx);   // 동치
// 여러 토큰: MemoryExtensions.Split(span, ',') 열거자(.NET 8+) — 무할당
```

**수명 규칙:**

| 상황 | 판정 |
|------|------|
| async 메서드 매개변수 `Span<T>` | 불가(CS4012) → `Memory<T>` |
| `Span<T>` 인스턴스 필드 | 불가(CS8345) → `Memory<T>` 또는 배열+오프셋 |
| `await`를 가로질러 Span 보존 | 불가. C# 13부터 await 사이에서 지역 Span을 새로 만들어 쓰고 버리는 것은 허용 |
| Span 반환 | 배열·고정 버퍼 등 안전한 backing storage 를 가리키면 허용. stackalloc 반환은 불가 |
| Memory<T>로 교체 | 수명 문제는 풀리지만 **풀 버퍼의 소유권·Return 시점은 그대로 남는다**. `IMemoryOwner<T>`(MemoryPool)로 소유권을 명시하거나 Return 지점을 함께 제시 |

## 기법 3: ArrayPool<T>.Shared

```csharp
// 교체 대상: hot path 의 new T[n]
byte[] buffer = new byte[4096];
// 수정
// ArrayPool<byte>.Shared: 스레드별 TLS 슬롯 → 코어별 스택 순으로 조회해 동일 스레드 재사용 시 락 없이 O(1) 반환
byte[] rented = ArrayPool<byte>.Shared.Rent(4096);      // Length >= 4096, 이전 내용이 남아 있음
try
{
    // 기존 코드가 0 초기화에 의존했다면: rented.AsSpan(0, 4096).Clear();
    int written = Fill(rented.AsSpan(0, 4096));        // 항상 실제 크기로 슬라이스
    Process(rented.AsSpan(0, written));
}
finally
{
    ArrayPool<byte>.Shared.Return(rented);             // 민감 데이터면 clearArray: true
}
```

**소유권 추적 (`arraypool-return-missing` / `arraypool-misuse`):**
1. `.Rent(` 마다 배열 변수를 따라가며 **모든 종료 경로**(정상 return, 예외, 조기 return, 취소, 루프 break)에서 Return이 **정확히 1회** 실행되는지 확인
2. `finally`가 아니어도 정상인 형태: `IDisposable` 래퍼가 Dispose에서 Return, 배열을 넘겨받은 쪽이 Return 책임을 지는 명시적 소유권 이전
3. 위반: 예외 경로 누락(medium~high, 빈도에 따라), 풀 밖에서 만든 배열 Return(풀 오염, high), 중복 Return(high), Return 후 접근(high), Rent 배열을 반환값으로 노출해 소유권 불명(medium)
4. Return 누락은 **풀 미스**다. 미반환 배열은 도달 불가능해지면 GC가 수거하므로 "영구 누수"라고 쓰지 않는다. 대신 "반복 미반환 → 풀이 계속 새 배열을 할당 → 풀링 이득 소멸 + Gen2/LOH 압력"으로 설명

**민감 데이터:** `Return(buffer, clearArray: true)` (기본 false → 풀에 내용 잔류).

## 기법 4: stackalloc (`stackalloc-misuse`)

- 제안 조건: 크기가 상수이거나 상한이 검증된 런타임 값이고 대략 ≤ 256~512바이트, 수명이 메서드 내, **루프 밖**, 동기 메서드
- 결함: 루프 내 반복 stackalloc(스택 누적), 상한 없는 `stackalloc T[n]`, async 메서드 내 stackalloc, stackalloc Span 반환. `[SkipLocalsInit]`이 있으면 초기화되지 않은 데이터를 읽지 않는지 확인
- 기존 stackalloc 코드도 같은 기준으로 검사한다("이미 최적화됨"으로 건너뛰지 않음)
- 큰 임시 버퍼는 `n <= 512 ? stackalloc byte[512] : ArrayPool<byte>.Shared.Rent(n)` 이중 경로 제안

## 수정 코드 작성 원칙

모든 `fix_code`는:
1. **동작 보존** — 구분자 부재·범위 초과·null·0 초기화 의존·소유권을 원본과 동일하게. `behavior_preserved`에 근거 한 줄. 확인 못 하면 코드 생략
2. 메모리 타입 선언(`ArrayPool`, `Span`, `Memory`, `ValueTask`, `IMemoryOwner`, `stackalloc`)에 **내부 동작 근거 `//` 주석** (CLAUDE.md)
3. public 시그니처를 제시하면 `<remarks>`(Thread Safety·Memory Allocation·Blocking) 포함
4. ArrayPool 은 모든 종료 경로 Return + 실제 크기 슬라이스
5. Span 은 수명 규칙 표 준수

## 심각도 기준 (hot path confirmed 기준, 미확정 시 하향)

| 심각도 | 기준 |
|--------|------|
| **critical** | 풀 밖 배열 Return·중복 Return·Return 후 사용(풀 오염으로 데이터 손상 가능), 대형 버퍼 요청당 반복 할당 |
| **high** | 반복 경로의 Return 누락, hot path 대형 `new T[n]`, ValueTask 다중 소비 |
| **medium** | 예외 경로 1곳 Return 누락, Substring 복사, 캐시 히트 경로 Task 할당 |
| **low** | 소형 버퍼, 초기 용량, 이론적 개선 |

## 점수

`score = max(0, 100 − 25×critical − 12×high − 5×medium − 2×low)`, `necessary` 제외. 최종은 오케스트레이터 재계산.

## 출력

1. 공통 finding 스키마(id `PE-n`, `behavior_preserved` 포함)로 `{run_dir}/02_pooling_findings.json`에 Write. Write는 이 파일에만
2. hot path 없으면 `hot_path_found: false`, `findings: []`, score 100
3. 최종 응답 첫 줄 `{"status":"done","output":"<경로>","counts":{...},"score":N,"hot_path_found":true|false}`. SendMessage 사용 금지
