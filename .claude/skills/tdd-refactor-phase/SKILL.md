---
name: tdd-refactor-phase
description: "TDD Refactor 단계: dotnet test(trx)로 Green을 판정하고 PASS 시 동작 보존 리팩토링 적용·회귀 확인, 실패 시 롤백. PASS 없이 다음 단계 불가. tdd-qa 전용."
---

# TDD Refactor Phase Skill

## 입력 읽기
1. `{run_dir}/TddSession.csproj` — 없으면 `error`(오케스트레이터 책임)
2. `{run_dir}/01_analyst/Tests/*.cs`, `{run_dir}/02_builder/Src/*.cs`, 기존 `03_qa/Src/*.cs`(있으면 — 오케스트레이터가 재작업 전 비웠어야 함)
3. attempt 번호(프롬프트)

## Step 1: 테스트 실행 (테스트 실행 계약)
```bash
dotnet test "$run_dir/TddSession.csproj" --nologo --logger "trx;LogFileName=qa_attempt$N.trx" --results-directory "$run_dir/03_qa/results" > "$run_dir/03_qa/test_results_attempt$N.txt" 2>&1; echo "exit=$?" >> "$run_dir/03_qa/test_results_attempt$N.txt"
```
- 종료 코드를 파일에 남긴다(`| tee`로 가리지 않는다). 시도별 파일은 덮어쓰지 않는다
- 판정은 trx의 `<Counters total="" passed="" failed="" …>`(콘솔 `통과:/실패:/Passed:` 문자열에 의존하지 않는다)
- 복원 실패 → `dotnet restore` 1회 후 재시도. 파일 잠금 → `dotnet build-server shutdown` 후 재시도

## Step 2: 판정
| 조건 | verdict |
|---|---|
| 빌드 성공 · `total ≥ 1` · `failed == 0` · testhost 정상 종료 | **PASS** |
| 빌드 실패 | FAIL — 컴파일 오류 원문을 `failed_tests`에 |
| `failed > 0` | FAIL — 테스트명·메시지·Expected/Actual |
| `total == 0` 또는 결과 파일 없음 | FAIL — "테스트 발견 실패/실행 오류"(PASS로 취급 금지) |

FAIL 피드백 형식(최종 응답 `failed_tests`): `{"name":"CalculatorTests.Add_TwoPositives_ReturnsSum","message":"Assert.Equal() Failure: Expected 5, Actual -1","hint":"Add 가 a-b 를 반환"}`

## Step 3: Refactor 제안·적용 (PASS 후에만)
**동작 보존 변경만:** Fake Implementation 제거(테스트가 이미 강제할 때), 중복 추출, 이름 개선, 메서드 분리, 상수 추출, CLAUDE.md 주석 규칙 보완(public `<remarks>` 3항목, 선언부 근거 주석).
**금지:** 새 기능·정책 변경(`checked(a+b)` 같은 오버플로우 정책은 새 Red 사이클 후보로만 기록), 시그니처 변경, 성능 최적화.
적용할 파일만 `{run_dir}/03_qa/Src/<Feature>.cs`에 쓴다(파일 우선순위로 builder 파일을 덮음. 전체 스냅샷 불필요).

## Step 4: 회귀 테스트 + 롤백
```bash
dotnet test "$run_dir/TddSession.csproj" --nologo --logger "trx;LogFileName=qa_attempt${N}_regression.trx" --results-directory "$run_dir/03_qa/results" > "$run_dir/03_qa/test_results_attempt${N}_regression.txt" 2>&1; echo "exit=$?" >> …
```
- 전부 통과 → 적용 확정
- 실패 → **해당 `03_qa/Src` 파일을 삭제**하고 다시 실행해 builder 상태 PASS를 재확인. `refactor_guide.md`에 "미적용(회귀): 사유" 기록. 파일을 남겨두면 이후 모든 빌드가 깨진 코드를 보게 된다

## Step 5: 테스트 품질 감사 (제안만)
AAA 구조, 독립성, 이름, 단일 검증, 매직 어설션 → `refactor_guide.md`의 "테스트 품질" 절.

## refactor_guide.md (시도별 누적)
```markdown
# Refactor Phase 결과 — Attempt N
## 테스트 실행: 빌드 / total / passed / failed / trx 경로 / exit
## 판정: PASS | FAIL (+ failed_tests)
## Refactor 제안 | # | 위치 | 냄새 | 제안 | 적용 여부 |
## 회귀 테스트: regression trx / 결과 / 롤백 목록
## 다음 사이클 후보(동작 변경이라 미적용): …
## 테스트 품질
```

## 출력
1. `{run_dir}/03_qa/results/*.trx`, `test_results_attempt*.txt`, `refactor_guide.md`, `03_qa/Src/*.cs`(변경 파일만). Write는 `03_qa/`에만
2. 최종 응답 첫 줄 `{"status":"done|error","attempt":N,"verdict":"PASS|FAIL","build_ok":true,"total":N,"passed":N,"failed":N,"skipped":N,"failed_tests":[…],"trx":"…","refactor":{"proposed":N,"applied":[…],"reverted":[…],"regression_trx":"…","regression_passed":true}}`. SendMessage 사용 금지(FAIL은 오케스트레이터가 builder에게 전달)
