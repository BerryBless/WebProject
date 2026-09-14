---
name: performance-review
description: ".NET/C# 성능 병목(N+1, async 오용, 힙 할당, LINQ, 캐싱 누락)을 탐지해 JSON을 {run_dir}/02_performance_findings.json에 출력한다. performance-reviewer 전용."
---

# Performance Review Skill

## 입력 읽기

1. `{run_dir}/00_input/meta.json`을 읽어 `run_id`, `target_type`, `head_sha`를 확인한다 (`run_dir`은 프롬프트로 전달됨. 없으면 `_workspace/code-review/latest.txt` 참조).
2. `{run_dir}/00_input/diff.txt`를 **처음부터 끝까지** Read로 읽는다. 길면 `offset`/`limit`으로 나눠 읽고, `index.md`가 있으면 파일 위치 탐색에만 쓴다. 요약만으로 판단하지 않는다.
3. diff면 `+` 줄(추가된 코드)에 집중하되, 캐시·풀링·`using`이 **삭제**된 곳도 확인한다.
4. 루프 내부, 데이터 접근 레이어, async 메서드를 우선 확인한다.

## 저장소 문맥 조사 (읽기 전용)

| 판정 항목 | 필수 보충 조회 | 조회 불가 시 |
|----------|--------------|------------|
| 호출 빈도(hot path 여부) | 호출자 Grep (엔드포인트 매핑, 루프, 백그라운드 서비스) | severity를 한 단계 낮추고 `detail`에 "빈도 미확인" |
| 캐싱 누락 | `IMemoryCache`/`IDistributedCache`/`HybridCache` 등록·주입 여부 (`Program.cs`, 생성자) | `unverified: 캐시 인프라` |
| N+1(lazy loading) | `DbContext` 옵션의 `UseLazyLoadingProxies`, 엔티티 `virtual` 내비게이션 | 추정임을 명시 |

`target_type=pr`이면 작업 트리 대신 `git show {head_sha}:<경로>`로 읽는다.

## 감사 체크리스트

### 1. N+1 쿼리 패턴

```csharp
// 위험: 루프 안에서 DB 쿼리
foreach (var order in orders)
{
    var customer = _db.Customers.Find(order.CustomerId); // N+1 발생
}

// 위험: EF Core lazy loading + 루프
foreach (var order in orders)
{
    Console.WriteLine(order.Customer.Name); // 내비게이션 프로퍼티 lazy load
}

// 안전: eager loading
var orders = _db.Orders.Include(o => o.Customer).ToList();
```

**탐지 포인트:** 루프 내 LINQ 쿼리, 루프 내 `Find()` / `FirstOrDefault()`, 내비게이션 프로퍼티를 루프 안에서 처음 접근

### 2. 비동기 안티패턴

```csharp
// 위험: 스레드 풀 블로킹
var result = asyncMethod().Result;          // 데드락 위험
var result = asyncMethod().GetAwaiter().GetResult();
asyncMethod().Wait();

// 위험: async void (예외 캐치 불가)
public async void HandleEvent(...)

// 위험: Task.Run 과용 (I/O 작업에 사용)
var data = await Task.Run(() => _db.Users.ToList());

// 안전
var result = await asyncMethod();
public async Task HandleEventAsync(...)
```

### 3. LINQ 비효율

```csharp
// 위험: 필터 전 ToList()
var users = _db.Users.ToList().Where(u => u.IsActive);  // 전체 로드 후 필터

// 위험: Count() > 0
if (_db.Users.Count() > 0)  // Any()가 더 효율적

// 위험: 중첩 루프에 IEnumerable 반복
foreach (var x in list)
    if (otherList.Contains(x))  // O(n²) - HashSet 사용 권장

// 안전
var users = _db.Users.Where(u => u.IsActive).ToList();
if (_db.Users.Any())
```

**비싼 변환 뒤 필터 (`Select(expensive).Where(pred)`):**
필터를 앞으로 옮기라고 권장하려면 두 조건을 모두 확인해야 한다. 하나라도 확인되지 않으면 "변환 비용 절감 가능성"으로 low에 기록하고 코드 예시는 제시하지 않는다.
1. `pred`가 변환 **입력** 타입에서 동등하게 표현 가능한가 (변환 결과의 속성을 검사하면 불가)
2. `expensive`가 `pred`의 결과에 영향을 주는 부작용·상태 변경이 없는가

```csharp
// 이동 가능: 조건이 원본 속성으로 표현됨
users.Select(u => Enrich(u)).Where(e => e.SourceId > 0)   →   users.Where(u => u.Id > 0).Select(u => Enrich(u))
// 이동 불가: 조건이 변환 결과에만 존재
users.Select(u => Enrich(u)).Where(e => e.EnrichedScore > 10)   // 그대로 두거나 Enrich 비용 자체를 줄인다
```

### 4. 메모리 관리

**IDisposable 미해제:**
```csharp
// 위험
var stream = new FileStream(...); // using 없음
var conn = new SqlConnection(...); // using 없음

// 안전
using var stream = new FileStream(...);
```

**string 연결 루프:**
```csharp
// 위험: O(n²) 복잡도
string result = "";
foreach (var item in items) result += item.ToString();

// 안전
var sb = new StringBuilder();
foreach (var item in items) sb.Append(item.ToString());
```

**이벤트 핸들러 누수:**
```csharp
// 위험: 구독 후 해제 없음 (장수 객체에 단수 객체 등록)
longLivedObject.Event += shortLivedObject.Handler;
// Dispose에서 -= 미실행
```

### 5. 캐싱 누락

다음 패턴이 반복 호출 경로에 있으면 캐싱을 고려한다:
- 설정 조회 (변경 빈도 낮음)
- 참조 데이터 조회 (코드/카테고리/열거형)
- 외부 API 호출
- 복잡한 집계 쿼리

캐시 인프라(`IMemoryCache`/`IDistributedCache`/`HybridCache`)가 등록·주입되어 있지 않은 서비스에서 위 패턴이 발견되면 보고한다. 등록 여부는 저장소 문맥 조사로 확인한다.

## 심각도 기준

| 심각도 | 기준 |
|--------|------|
| **critical** | 프로덕션 장애 수준: 무제한 쿼리, 데드락 가능, 메모리 무한 증가 |
| **high** | 응답 시간 수 초 증가, N+1 (수백~수천 쿼리), 스레드 풀 기아 |
| **medium** | 눈에 띄는 지연, 불필요한 CPU/메모리 소비 |
| **low** | 미미한 비효율, 캐싱 추가로 개선 가능한 부분 |

## 점수

`score = max(0, 100 − 25×critical − 10×high − 4×medium − 1×low)`. `counts`는 findings에서 센 값과 일치해야 한다.

## 출력

1. 결과를 `{run_dir}/02_performance_findings.json`(또는 오케스트레이터가 지정한 파일명)에 Write 도구로 저장한다. Write는 이 파일에만 사용한다.
2. 발견사항이 없으면 빈 배열, counts 0, score=100으로 저장한다.
3. 저장 후 **최종 응답 첫 줄**에 `{"status":"done","output":"<경로>","counts":{...},"score":N}`을 적고 종료한다. SendMessage는 사용하지 않는다(팀 도구 없음, 리더 ID 미상).
