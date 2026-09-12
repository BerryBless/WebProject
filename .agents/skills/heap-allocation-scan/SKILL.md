---
name: heap-allocation-scan
description: ".NET 10 서버 라이브러리 hot path의 불필요한 힙 할당을 정밀 탐지한다. boxing/unboxing, 루프 내 new, hot path LINQ, 캡처 클로저, string 연산, 암묵적 배열을 분석하고 공통 finding 스키마 JSON을 {run_dir}/02_allocation_findings.json에 출력한다. heap-allocation-scanner 에이전트 전용 스킬."
---

# Heap Allocation Scan Skill

## 입력 읽기

1. `{run_dir}/00_input/meta.json`에서 `run_id`, `target_type`, `head_sha` 확인 (`run_dir`은 프롬프트로 전달, 없으면 `_workspace/gc-guard/latest.txt`)
2. `{run_dir}/00_input/source.txt`를 **처음부터 끝까지** Read (길면 offset/limit 분할, `index.md`는 탐색용)
3. 형식별: diff면 `+` 줄의 새 할당과 `-` 줄로 사라진 캐싱·풀링(static delegate 필드, ArrayPool 사용 제거)을 모두 본다. `=== FILE: ===` 전체 파일이면 hot path 후보 메서드부터 식별한다

## 저장소 문맥 조사 (읽기 전용)

| 판정 항목 | 필수 조회 | 조회 불가 시 |
|----------|----------|------------|
| hot path 확정 | 대상 메서드의 호출자 Grep(엔드포인트 매핑, 수신 루프, 백그라운드 서비스) | `hot_path: unknown` + severity 하향 |
| 컬렉션 실제 타입 | 필드·매개변수 선언(`List<T>` vs `IEnumerable<T>` vs 배열) | 보수적으로 판정, detail에 명시 |
| 값 타입 여부 | `struct`/`record struct` 선언 | `unverified` |
| 람다 캡처 여부 | 람다 본문이 참조하는 지역 변수·`this` 멤버 | 캡처 없음이 확인되면 보고하지 않음 |

`target_type=pr`이면 `git show {head_sha}:<경로>`로 읽는다.

## Hot Path 판정

`confirmed`(호출 경로를 코드로 확인) / `candidate`(이름·구조상 유력, 호출자 미확인) / `unknown`. 미확정은 severity 한 단계 하향. 루프 내부라는 사실만으로는 hot path가 아니다. `[Benchmark]`는 confirmed, `[MethodImpl(AggressiveInlining)]`은 근거가 아니다. 초기화·설정 로드·팩토리 1회 호출은 `necessary: true` 또는 low.

## 탐지 패턴 6종 (메커니즘 기준)

### Pattern 1: Boxing / Unboxing

```csharp
// HIGH: 값 타입 → object / 비제네릭 인터페이스
object o = intValue;                 // 박싱
IComparable c = structValue;         // 박싱 (IComparable<T> 제네릭 제약이면 박싱 없음)
ArrayList list; list.Add(42);        // 비제네릭 컬렉션
Hashtable ht; ht["k"] = structValue;

// HIGH: string.Format(object) 계열은 모든 .NET 버전에서 박싱한다
string.Format("{0} {1}", intA, intB);   // object 오버로드 → 박싱
// 무할당 대안: 보간 문자열 $"{intA} {intB}" (DefaultInterpolatedStringHandler, .NET 6+),
//              또는 .NET 8+ CompositeFormat + string.Format<TArg0,...>(provider, format, args)

// MEDIUM: struct 열거자의 인터페이스 박싱 (원소가 아니라 열거자가 박싱된다)
IEnumerable<int> seq = list;          // 캐스트 자체는 할당 없음
foreach (var x in seq) { }            // List<int>.Enumerator(struct) → IEnumerator<int>로 박싱 1회
// 수정: 구체 타입(List<int>)으로 foreach 하면 struct 열거자 그대로 사용

// 탐지 힌트: object/dynamic 매개변수에 값 타입 전달, 비제네릭 컬렉션, string.Format/Concat(object), 인터페이스 타입 변수에 struct 대입
```

### Pattern 2: 루프 내 `new`

```csharp
// HIGH (hot path confirmed면 CRITICAL 가능): 루프마다 참조 타입·배열 생성
for (int i = 0; i < count; i++)
{
    var buffer = new byte[1024];     // raw-array-alloc — pooling-enforcer도 볼 것이나 여기서도 기록
    var list = new List<int>();
    var dto = new Response();
}
// 값 타입 new(struct)는 힙 할당이 아니다 → 보고하지 않음

// 수정 방향: 루프 밖으로 호이스팅·재사용, 버퍼는 ArrayPool(pooling-enforcer 영역), 컬렉션은 Clear() 재사용
```

### Pattern 3: Hot Path LINQ

```csharp
// HIGH: 요청 경로의 지연 연산자 체인 — 연산자마다 이터레이터 객체 + 람다 delegate(캡처 시)
var items = _cache.Values.Where(x => x.IsActive).Select(x => x.ToDto()).ToList();

// 할당이 없는 경우 (보고 금지)
list.Count()      // ICollection<T> 경로 → Count 프로퍼티, 할당 없음
list.Any()        // .NET 8+ TryGetNonEnumeratedCount → 컬렉션이면 할당 없음
array.Length, span.Length

// MEDIUM: GroupBy/ToDictionary/OrderBy — 내부 버퍼·Lookup 다중 할당
```
초기화·설정·1회 팩토리의 LINQ는 보고 제외.

### Pattern 4: 클로저 캡처 (캡처가 있을 때만)

```csharp
// HIGH: 외부 지역 변수 또는 this 를 캡처 → 디스플레이 클래스 1개 + delegate 객체
int threshold = ...;
items.Where(x => x.Value > threshold);          // threshold 캡처
tasks[i] = Task.Run(() => Process(i, _state));  // i 와 this 캡처

// 보고 금지: 캡처 없는 람다는 컴파일러가 static 필드에 delegate를 캐싱한다 (호출마다 할당 없음)
list.Sort((a, b) => a.CompareTo(b));            // 캡처 없음 → 캐싱됨
items.Where(static x => x.IsActive);            // static 람다 (캡처 시 컴파일 에러로 보장)

// 수정 방향: 캡처 값을 매개변수로 넘기는 오버로드 사용(예: Where<T,TState>는 없으므로 for 루프), 
//            static 람다 + 상태 객체 전달, 또는 캐싱된 delegate 필드
// 주의: [MethodImpl(AggressiveInlining)] 은 클로저 할당을 제거하지 않는다
```

### Pattern 5: String 할당

```csharp
// HIGH: 루프 내 + / 보간 → 반복마다 새 string
string result = "";
foreach (var item in items) result += item.Name + ", ";

// 수정: StringBuilder(초기 용량 추정) 또는 길이가 계산 가능하면 string.Create(len, state, static (span, s) => ...)
// 최종 결과가 string 이어야 하면 마지막 1회 할당은 necessary 다. "string 생성 0회" 같은 목표는 세우지 않는다
// ReadOnlySpan<char> 는 읽기 전용 슬라이스라 문자열을 조립할 수 없다

// MEDIUM: hot path 의 Substring/Split/ToUpper 복사 (pooling-enforcer 의 substring-copy 와 겹치면 피어가 정리)
```

### Pattern 6: 암묵적 배열·컬렉션 할당

```csharp
// HIGH: params T[] 확장 호출 — 인수 개수가 고정·상수여도 호출마다 배열 생성 (+ object[] 이면 값 타입 박싱)
void Log(string fmt, params object[] args);
Log("v={0}", intValue);              // object[1] + 박싱
// 할당 없는 경우: 이미 만든 배열을 그대로 전달, 인수 0개(Array.Empty), C# 13 `params ReadOnlySpan<T>` 오버로드가 선택될 때
// 수정: params ReadOnlySpan<T> 오버로드 제공(C# 13), 로깅은 LoggerMessage 소스 생성기

// MEDIUM: yield return 생성기 — 열거 시작 시 상태 머신 객체 1개. hot path 에서 매 요청 열거하면 보고.
//         단, List 로 바꾸면 물질화 할당과 즉시 실행이 생기므로 소비 패턴이 맞을 때만 제안

// LOW: 초기 용량 미지정 컬렉션 (new List<T>() 후 대량 Add → 배열 재할당 log2(n)회)
// MEDIUM: 컬렉션 식 [a, b, c] 가 배열/List 로 물질화되는 hot path (Span 대상이면 stackalloc/인라인 배열로 무할당)
```

## 심각도 기준 (hot path confirmed 기준, candidate/unknown은 한 단계 하향)

| 심각도 | 기준 |
|--------|------|
| **critical** | 요청·프레임당 수십 회 이상 또는 대형 배열(≥ 85KB LOH) 반복 할당 |
| **high** | 요청당 수 회 할당: 루프 내 new, 캡처 클로저, string 루프, params 박싱 |
| **medium** | 요청당 1회 수준 소형 할당, 열거자 박싱, GroupBy 류 |
| **low** | 초기 용량 미지정, 이론적 개선 |

## 점수

`score = max(0, 100 − 25×critical − 12×high − 5×medium − 2×low)`, `necessary: true` 제외. `counts`는 findings와 일치. 최종 점수는 피어 리뷰 후 오케스트레이터가 재계산한다.

## 출력

1. 공통 finding 스키마(id `HA-n`, `hot_path`, `hot_path_evidence`, `fix_code`에 선언부 근거 주석)로 `{run_dir}/02_allocation_findings.json`에 Write. Write는 이 파일에만
2. hot path가 없으면 `hot_path_found: false`, 비hot path 할당은 low/necessary, score 100
3. 최종 응답 첫 줄 `{"status":"done","output":"<경로>","counts":{...},"score":N,"hot_path_found":true|false}`. SendMessage 사용 금지(팀 도구 없음)
