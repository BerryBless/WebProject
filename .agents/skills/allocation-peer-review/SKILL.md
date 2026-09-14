---
name: allocation-peer-review
description: "heap-allocation-scanner·pooling-enforcer 보고서 2개를 독립 교차 검증해 최종 finding과 점수를 {run_dir}/03_peer_review.json에 확정한다. allocation-peer-reviewer 전용."
---

# Allocation Peer Review Skill

## 입력 읽기

1. `{run_dir}/00_input/meta.json` → `run_id`, `target_type`, `head_sha`
2. `{run_dir}/02_allocation_findings.json`, `{run_dir}/02_pooling_findings.json` (프롬프트에 "수집 실패"로 명시된 파일은 건너뛰고 `inputs_reviewed`에 반영)
3. `{run_dir}/00_input/source.txt` **전체** — 보고서에 없는 위치도 FN 탐지 대상이다

`target_type=pr`이면 diff 밖 파일은 `git show {head_sha}:<경로>`.

## Step 1: 원본 finding 대조

각 `id`의 `file`(파일:라인)을 source.txt에서 찾아 확인한다: 코드가 존재하는가 / 실제로 그 패턴인가 / `hot_path` 등급과 근거가 타당한가 / severity가 등급에 맞게 하향됐는가.

## Step 2: verdict 판정

| verdict | 조건 | 점수 반영 |
|---------|------|----------|
| `confirmed` | 패턴·위치·severity·fix 모두 타당 | 포함 |
| `modified` | 결함은 유효하나 severity·hot_path 등급·fix_code 보정 필요 | **포함**(final_severity로) |
| `rejected` | 오탐 | 제외 |

**기각 사유 (근거 파일:라인 필수):**
- 캡처 없는 람다·메서드 그룹(컴파일러 delegate 캐싱) — 스캐너가 `closure-capture`로 올렸다면 기각
- 값 타입 `new`, `stackalloc`(단 stackalloc 자체의 오용은 별도 검사)
- `List<T>.Count()`·`Any()` 등 ICollection 경로(할당 없음)
- `string.Format`이 아닌 보간 문자열(.NET 6+ 핸들러) 또는 `CompositeFormat` 제네릭 오버로드
- 초기화·설정·1회 팩토리 경로임이 호출자로 확인됨
- `Task.FromResult`의 인수가 런타임 캐시 값(bool, 소형 정수, null)
- 제안된 교체가 **동작을 바꿈**(Split 구분자 부재, AsSpan 범위 예외, 0 초기화 의존, 소유권 필요) — 결함 자체는 유효하면 `modified` + `corrected_fix`, 결함도 없으면 `rejected`

**기각 사유가 아닌 것:**
- "다른 코드 경로에 Return이 있다" — 모든 종료 경로를 추적해야 한다
- "params 호출 인수가 고정이다" — `params T[]` 확장 호출은 여전히 배열 할당. 기각은 C# 13 `params ReadOnlySpan<T>` 오버로드 선택·기존 배열 전달·인수 0개일 때만
- "`AggressiveInlining`이 있다" — 할당과 무관
- "루프 안이니 hot path" 반대로 "루프 밖이니 아니다" — 호출 빈도가 기준

## Step 3: FN 독립 탐지

source.txt를 두 보고서와 무관하게 읽고 추가한다(id `PR-n`, 공통 스키마 전체 기입):

```csharp
// FN-1: params object[] 확장 호출 (배열 + 박싱). 수정: params ReadOnlySpan<T> 오버로드(C# 13), LoggerMessage 생성기
// FN-2: 캡처 람다 (외부 지역/this 참조) — 캡처 없는 람다는 제외
// FN-3: string.Format/Concat(object) 박싱 — 모든 .NET 버전. 수정: 보간 문자열 또는 CompositeFormat<T>
// FN-4: struct 열거자 박싱 — IEnumerable<T>/IEnumerator<T> 타입 변수로 foreach
// FN-5: hot path yield 생성기 — 소비 패턴이 즉시 물질화를 허용할 때만 List/배열 제안
// FN-6: hot path 예외 흐름 제어 — throw 가 정상 경로의 일부일 때. 수정: TryXxx/Result 패턴. 예외 인스턴스 재사용은 스택 추적·요청별 정보를 잃으므로 제안하지 않는다
// FN-7: ArrayPool 소유권 — Rent 배열의 모든 종료 경로, 풀 밖 배열 Return, 중복 Return, Return 후 사용, 반환값으로 노출
// FN-8: stackalloc 오용 — 루프 내, 상한 없음, async 내, 반환
// FN-9: ValueTask 다중 소비·미완료 .Result·WhenAll 직접 전달
// FN-10: Memory<T> 로 바꿨지만 풀 Return 시점이 사라진 코드
// FN-11: LINQ GroupBy/ToDictionary/OrderBy in hot path
```
Nullable<T>.Value 추출, 단순 값 복사, `Count` 프로퍼티 접근은 GC 결함이 아니므로 추가하지 않는다.

## Step 4: fix_code 검증

`pooling-enforcer`·스캐너의 각 `fix_code`에 대해 (문제 있으면 `fix_code_issues` + `modified`):

```
동작 보존
□ 구분자 부재·범위 초과·null 입력에서 원본과 같은 결과/예외인가
□ Rent 로 바꿨는데 원본이 0 초기화에 의존하지 않는가 (의존하면 Clear 포함)
□ Substring→AsSpan 결과가 필드·컬렉션·다른 스레드로 나가지 않는가
□ yield→List, 예외 재사용 등 의미 변경 제안이 소비 패턴상 허용되는가
ArrayPool
□ 모든 종료 경로에서 정확히 1회 Return (finally 또는 동등한 소유권 구조)
□ 실제 크기로 AsSpan(0, n) 슬라이스
□ 민감 데이터면 clearArray: true
□ 풀 밖 배열을 Return 하지 않는가, Return 후 접근이 없는가
Span / Memory
□ async 매개변수·필드·await 가로지르기 없음 (C# 13 지역 Span 은 허용)
□ Memory<T> 교체 시 풀 버퍼 소유권·Return 지점이 유지되는가
ValueTask
□ 단일 소비, 미완료 .Result 없음, WhenAll 은 AsTask()
□ 동기 완료율 근거가 있는가 (없으면 modified: 제안 철회)
stackalloc
□ 루프 밖, 상한 검증, 동기 메서드, 반환 없음
프로젝트 규칙 (CLAUDE.md)
□ ArrayPool/Span/Memory/ValueTask/IMemoryOwner/stackalloc 선언에 내부 동작 근거 // 주석
□ public 시그니처 제시 시 <remarks> Thread Safety·Memory Allocation·Blocking
```

## Step 5: 교차 조율 및 최종 집합

1. 두 보고서가 같은 위치를 보고하면(예: HA `raw-array-alloc` vs PE `raw-array-alloc`) 소스로 확인 후 **하나만** `final_findings`에 남기고 다른 하나는 `rejected` 대신 `verdict: "confirmed"` + `reason: "PE-3와 병합"`으로 표기하되 final_findings에는 넣지 않는다(FP 비율에 세지 않기 위해)
2. 같은 위치를 다른 패턴으로 봤으면(할당 vs 풀링 대안) 둘 다 유지
3. `final_findings` = confirmed + modified(final_severity 적용) + additional, `necessary` 표기 유지

## 점수·비율

```
final_score = max(0, 100 − 25×critical − 12×high − 5×medium − 2×low)   over final_findings, necessary 제외
fp_rate     = rejected 원본 수 ÷ 검증한 원본 수   (병합은 rejected 가 아님; 원본 0건이면 null)
```

## 출력

1. `{run_dir}/03_peer_review.json`(또는 지정된 `_rN` 이름)에 `verdicts[]`, `additional_findings[]`, `fix_code_issues[]`, `final_findings[]`, `unverified[]`, `fp_rate`, `counts`, `final_score` 를 Write. Write는 이 파일에만
2. 최종 응답 첫 줄 `{"status":"done","output":"<경로>","counts":{...},"final_score":N,"fp_rate":0.25,"rejected":N,"additional":N}`. SendMessage 사용 금지(팀 도구 없음)
