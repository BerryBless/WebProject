---
name: deadlock-review
description: "deadlock-analyzer 보고서를 독립 검증해 id별 verdict, FN 추가, 최종 finding·점수·재분석 필요 여부를 JSON으로 확정한다. deadlock-reviewer 전용."
---

# Deadlock Review Skill

## 입력 읽기
1. `{run_dir}/00_input/meta.json`
2. `{run_dir}/03_deadlock_analysis[_r2].json` — 프롬프트가 지정한 정확한 파일
3. `{run_dir}/00_input/source.txt` **전체** — FN 탐지는 보고서와 무관하게 소스를 읽는다

`target_type=pr`이면 diff 밖 파일은 `git show {head_sha}:<경로>`.

## Step 1: 원본 finding 대조
각 `DA-n`의 `file`을 소스에서 확인: 코드 존재 / 패턴 일치 / `context` 정확성 / `is_conditional` 조건 성립 / severity가 문맥 규칙에 맞는지.

## Step 2: verdict
| verdict | 조건 | 점수 |
|---|---|---|
| confirmed | 모두 타당 | 포함 |
| modified | 결함 유효하나 severity·pattern·context·fix 보정 | **포함**(final_severity) |
| rejected | 오탐 | 제외 |

**기각 사유(근거 파일:라인 필수):**
- `sync-blocking`의 context가 `entrypoint`/`test`이고 수신자가 Task임이 확인됨 → rejected(정보성으로만). `library`는 기각 불가(conditional 유지)
- `lock-order`가 한 방향만 존재, 또는 두 경로가 동시 실행 불가(Init/Runtime 분리) → rejected 또는 conditional로 modified
- `Monitor` 재진입이 같은 스레드에서만 발생
- `configure-await`가 `app`/`test`/`entrypoint` → rejected
- 직접 `lock { await }` → `compile-error`로 modified(점수 제외 정보성)
- `cancellation-policy`를 "무한 대기"로 올린 항목에 탈출 경로가 있음 → medium으로 modified
- 수신자가 Task가 아닌 `.Result`/`.Wait(` → rejected

**기각 사유가 아닌 것:** "다른 경로에 Release가 있다"(모든 종료 경로 필요), "ASP.NET Core라 SynchronizationContext가 있다"(없다 — `app` 문맥은 기아 high로 modified), "라이브러리인데 호출자가 현재 앱뿐이다"(라이브러리 계약은 호출자 미상).

## Step 3: FN 독립 탐지 (`DR-n`, 공통 스키마 전체)
```csharp
// FN-1: Task.Run(() => asyncMethod().Result) — Task.Run 안의 동기 블로킹
// FN-2: async void 람다 이벤트 구독 + try/catch 없음
// FN-3: Channel Writer.Complete 부재 + 소비자 취소 토큰 없음
// FN-4: WhenAll 대상 작업이 토큰을 무시하고 탈출 경로 없음 (토큰 전달 자체가 아니라 탈출 불가가 기준)
// FN-5: 재귀 async 에서 같은 SemaphoreSlim 재취득 (비재진입 → 자기 교착)
// FN-6: WaitAsync(timeout) false 반환 뒤 Release (카운트 초과)
// FN-7: SemaphoreSlim 취득이 try 안에 있어 취득 실패에도 finally Release
// FN-8: Monitor 와 System.Threading.Lock 혼용 (lock-api-mix)
// FN-9: using (_lock.EnterScope()) { await … } (System.Threading.Lock 도 스레드 친화)
// FN-10: ValueTask 를 .Result 로 동기 소비 (미완료 시 undefined)
```

## Step 4: 심각도 재조정
- 상향: hot path(요청/프레임당) 확인, library 문맥 확인
- 하향: context `unknown`→확인 결과 `app`/`test`, 동시 실행 가능성 낮음(코드 근거)

## Step 5: 최종 집합·재분석 판단
- `final_findings` = confirmed + modified(final_severity) + additional. 동일 위치·패턴은 하나만
- **재분석 요청**(`needs_reanalysis: true`, `reanalysis_targets: ["DA-3", "semaphore-release-path in Cache.cs:80-120"]`)은 FN 2건 이상이 방법론 문제로 보이거나 원본 50% 이상 rejected일 때. 최대 1회. 요청하더라도 이 라운드의 `final_findings`·`final_score`는 완성한다(오케스트레이터가 재호출을 수행)
- 합의 불가 항목은 `disputed[]`에 양측 근거 병기

## 점수·비율
```
final_score = max(0, 100 − 25c − 12h − 5m − 2l)   over final_findings (compile-error 정보성 제외)
fp_rate     = rejected 원본 ÷ 검증 원본            (병합·정보성 재분류는 rejected 아님; 원본 0건이면 null)
```

## 출력
1. `{run_dir}/03_deadlock_review[_r2].json`에 `verdicts[]`(id·verdict·final_severity·final_pattern·reason), `additional_findings[]`, `final_findings[]`, `disputed[]`, `needs_reanalysis`(bool), `reanalysis_targets[]`, `unverified[]`, `verified_original_count`, `rejected_count`, `fp_rate`, `counts`, `final_score`를 Write. Write는 이 파일에만
2. 최종 응답 첫 줄 `{"status":"done","output":"<경로>","round":N,"counts":{...},"final_score":N,"fp_rate":N,"rejected":N,"additional":N,"needs_reanalysis":false}`. SendMessage 사용 금지(analyzer에게 직접 요청하지 않는다)
