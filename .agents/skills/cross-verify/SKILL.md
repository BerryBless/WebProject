---
name: cross-verify
description: >
  Codex와 실제 Codex CLI가 Plan·최종 코드리뷰를 교차 검증하는 개발 파이프라인 오케스트레이터.
  **필수 트리거 조건: 요청에 '코덱스' 또는 'Codex' 키워드가 명시된 경우에만 실행한다** (스킬명 직접 호출
  '/cross-verify' 제외). 트리거 예: '코덱스 교차 검증으로 구현', 'Codex랑 같이 구현', '코덱스와 교차 검증',
  'Codex 교차 리뷰 파이프라인'. 후속: '코덱스 교차 검증 이어서', '코덱스 교차검증 재실행', 'Codex 리뷰 단계만 다시'.
  트리거 금지: '교차 검증', '더블 체크', '크로스 체크' 등이 있어도 코덱스/Codex 언급이 없으면 이 스킬을 실행하지
  않는다(일반 리뷰·검증 요청으로 처리). 단발 Codex 질문·단독 리뷰는 codex 스킬이 담당하므로 역시 트리거하지 않는다.
---

# Cross-Verify — Codex ↔ Codex 교차 검증 개발 파이프라인

**실행 모드: 서브 에이전트(Agent 도구) + 파일 기반 전달.** 1차 산출 단계에서 상대 결론을 보기 전 독립 작성이 절대 요구라 에이전트 간 통신은 없고, 모든 데이터는 run 디렉토리 파일로만 오간다. 각 에이전트는 최종 응답 첫 줄 JSON으로 보고한다.

```
[오케스트레이터(메인 세션)]
  Phase 1: cross-planner ∥ codex-adapter(plan)   → 교환 검토 → 조정 → 통합 계획 → 양측 재검토
  Phase 2: cross-implementer                      → 테스트 실행 (범위 이탈 시 Phase 1 회귀)
  Phase 3: cross-reviewer ∥ codex-adapter(review) → 교환 검증 → 조정 → 수정 → 양측 재검토
```

핵심 불변식:
- **Codex는 실제 CLI 호출로만 참여한다.** 모든 Codex 산출물 옆에 `invoke-codex.ps1`이 남긴 `*.meta.json`이 있어야 하고, **오케스트레이터가 직접** `status=success`, `out_sha256 == sha256(파일)`, `thread_id` 존재, `log_bytes>0`를 재검증한다. 어댑터 보고만으로 성공을 인정하지 않는다.
- **Codex는 검증만 한다.** 코드 수정은 cross-implementer만(Codex는 read-only 샌드박스 고정).
- **독립성은 양방향이다.** Codex 측은 `*codex*`를, Codex 측은 `_workspace/cross/**`의 다른 파일을 1차 단계에서 읽지 않는다(프롬프트 명시 + `.log` 접근 흔적 검사).
- **두 모델의 동의는 필요 조건이지 통과 조건이 아니다.** 최종 판정은 코드와 실제 테스트 실행 결과.

## Stop 훅과의 관계

이 파이프라인은 사용자 확인(더러운 트리, 3라운드 미합의, 테스트 실패)으로 턴을 끝낼 수 있다. 그때 Stop 훅이 반쯤 구현된 코드를 폴백 메시지로 커밋·푸시하지 않도록:
1. **Phase 0 시작 시 `.git/harness_commit_in_progress`(내용: run_id)를 만든다.** 훅은 이 파일이 있으면(6시간 이내) 커밋을 건너뛴다.
2. 파이프라인이 끝나면(완료·중단 모두) 센티널을 삭제하고, 완료 시에는 WHY 메시지를 `.git/auto_commit_msg.txt`에 남겨 훅이 커밋하게 한다. 중단·실패 시에는 메시지 파일을 쓰지 않고 센티널만 지운 뒤 사용자에게 "미검증 변경이 작업 트리에 남아 있음"을 알린다(훅이 폴백 커밋하기 전에 사용자가 판단할 수 있도록 **센티널을 유지한 채** 질문하는 것도 허용 — 이 경우 답변 후 삭제).
3. `_workspace/cross/<run>/`은 git 추적 대상이다. 중간 커밋이 생겨도 리뷰 입력 diff에서 제외한다(Phase 3.1).

## 작업 디렉토리와 라운드 규칙

`_workspace/cross/<YYYYMMDD_HHmmss_기능약칭>/` — 번호 규칙 `0x`=컨텍스트, `1x`=Plan, `2x`=구현, `3x`=리뷰, `9x`=최종. **보관 이동을 하지 않는다**(추적 중인 감사 기록이므로 run 디렉토리를 누적). Codex 산출물은 항상 `.meta.json`·`.log`·`.err` 동반(`.log/.err`는 gitignore).

- **라운드 접미사:** 같은 단계의 재산출은 전부 `_r2`, `_r3`(조정·통합·재검토·수정·diff 모두). 오케스트레이터는 모든 에이전트 프롬프트에 **정확한 파일명**을 적고, 소비자는 항상 **가장 높은 접미사** 파일을 받는다(에이전트가 추측하지 않음).
- **`00_manifest.json`:** 단계별 입력 해시와 채택 파일을 기록한다.
  ```json
  { "run_id": "…", "base_sha": "…", "requirement_sha256": "…",
    "stages": { "plan": { "round": 1, "final_plan": "13_final_plan.md", "final_plan_sha256": "…", "verdicts": {"Codex":"APPROVE","codex":"APPROVE"} },
                "impl": { "attempts": 1, "results": "20_test_results.txt", "diff_sha256": "…" },
                "review": { "round": 2, "adjudication": "31_review_adjudication_r2.md", "diff": "33_diff_r2.patch", "diff_sha256": "…" } },
    "codex_calls": [ { "stage": "plan", "meta": "10_codex_plan.md.meta.json", "status": "success", "thread_id": "…", "duration_sec": 212 } ],
    "degraded": false }
  ```

## Phase 0: 컨텍스트 확인 및 사전 점검

1. **모드 판별:** 기존 run + 부분 재실행 요청("리뷰만 다시") → 해당 run 재사용. `00_manifest.json`의 해당 단계 입력 해시(요구사항·확정 계획·diff)를 현재와 비교해 **같을 때만** 부분 재실행, 다르면 상위 단계부터. 기존 run + 새 기능 → 새 run. 미존재 → 초기 실행.
2. **Codex 사전 점검(필수):** `codex login status`. 실패 시 중단하고 `codex login` 안내. Codex 없이 진행하면서 "교차 검증"이라 부르지 않는다.
3. **센티널 생성** `.git/harness_commit_in_progress`.
4. **작업 트리 점검:** `git status --porcelain`에 `_workspace/cross/` 밖 변경이 있으면 사용자에게 알리고 계속할지 묻는다(센티널 유지). 사용자 확인 **후** Phase 1 시작 시점에 `git rev-parse HEAD`를 기준 커밋으로 확정하고 `00_manifest.json`에 기록한다(확인 전 HEAD를 쓰지 않는다).
5. run 디렉토리 생성.

## Phase 1: Plan 교차 검증 (최대 3라운드)

1. **컨텍스트** — `00_context.md`: 요구사항 원문, 프로젝트 규칙 요약(AGENTS.md 주석 규칙 포함), 관련 코드 경로·현재 구조, 제약, 기준 커밋, 비범위. `requirement_sha256` 기록.
2. **독립 계획(병렬, 단일 메시지 Agent 2회)** — cross-planner mode=plan → `10_claude_plan.md`; codex-adapter stage=plan(`prompts/plan.md`) → `10_codex_plan.md`. 어댑터 프롬프트에 Codex 산출물 미포함.
3. **Codex 증빙 재검증(오케스트레이터):** meta `status`, `out_sha256`(직접 `sha256sum`), `thread_id`, `log_bytes`. `.log`에 `claude_plan|claude_review` 접근 흔적이 있으면 독립성 위반 → 그 산출물 무효, 재호출 1회.
4. **교환 검토(병렬)** — cross-planner mode=check → `11_claude_check_of_codex_plan.md`; codex-adapter stage=plan-check → `11_codex_check_of_claude_plan.md`.
5. **조정** — `12_plan_adjudication.md`: 양측 지적 전수, `채택/기각/미해결` + 근거, 7축(요구사항 누락·잘못된 가정·과도한 설계·구조 충돌·예외·테스트·프로젝트 규칙) 확인.
6. **통합 계획** — `13_final_plan.md`(채택 반영 내역 명시). manifest에 sha256.
7. **양측 재검토(병렬)** — cross-planner mode=final-check(입력에 `12_plan_adjudication.md` 포함) → `14_claude_final_check.md`; codex-adapter stage=final-check → `14_codex_final_check.md`. 양측 마지막 줄 `VERDICT: APPROVE`면 Phase 2.
8. **라운드:** REQUEST-CHANGES면 `12_…_r2` → `13_…_r2` → `14_…_r2`. 최대 3라운드. 이후 미합의면 중단·보고(쟁점·양측 근거·선택지).

## Phase 2: 구현과 테스트

1. `Agent(cross-implementer)` — 입력 `00_context.md`, 최고 접미사 `13_final_plan*.md`. 출력 코드 변경 + `20_impl_notes.md`, `20_test_results.txt`.
2. `status: scope_deviation` → `00_context.md`에 반영하고 Phase 1 경량 회귀(이탈 항목만 양측 검토, `_rN`).
3. 테스트 실패 시 구현자가 3회까지 시도. 그래도 실패면 사용자 보고(센티널 유지, 메시지 파일 미작성).

## Phase 3: 최종 코드 리뷰 교차 검증 (최대 3라운드)

1. **리뷰 입력 고정(오케스트레이터):**
   ```bash
   git diff <base> -- . ':(exclude)_workspace/**' > 30_diff.patch
   git ls-files --others --exclude-standard -- . ':!_workspace' | while IFS= read -r f; do git diff --no-index -- /dev/null "$f" >> 30_diff.patch || true; done
   ```
   신규 파일 **본문**이 patch에 들어간다(`30_new_files.txt`는 목록·요지용 보조). patch가 0바이트면 "리뷰 대상 없음"으로 중단. `diff_sha256` manifest 기록.
2. **독립 리뷰(병렬)** — cross-reviewer mode=review → `30_claude_review.md`; codex-adapter stage=review(`prompts/review.md`) → `30_codex_review.md`. 증빙 재검증 + 독립성 검사.
3. **상호 검증(병렬)** — cross-reviewer mode=adjudicate → `31_claude_adjudication.md`; codex-adapter stage=adjudicate → `31_codex_adjudication.md`.
4. **조정** — `31_review_adjudication.md`: 지적별 유효/기각/미해결, 중복 통합, `[취향]` 분리. 미해결은 코드로 직접 판정, 불가면 최종 보고에 승계.
5. **수정** — 유효 지적이 있으면 `Agent(cross-implementer)` → `32_fix_notes.md`, `32_test_results.txt`. **유효 지적이 없으면** 수정 단계를 건너뛰고 6으로 가되, 프롬프트에 "수정 없음"을 명시한다.
6. **재검토** — 코드가 바뀌었으면 `git diff <base> -- . ':(exclude)_workspace/**' > 33_diff.patch`(+신규 파일)를 새로 만들고(수정 없음이면 `30_diff.patch` 재사용을 명시) cross-reviewer mode=reverify + codex-adapter stage=reverify(`prompts/reverify.md`, `{{DIFF_PATH}}`=`33_diff*.patch`) 병렬 → `33_*_reverify.md`. 양측 `VERDICT: APPROVE` + 테스트 통과면 종료.
7. **라운드:** 수정→재검토 `_r2`, `_r3`. 최대 3라운드.

## 종료 및 실패 처리

**완료 조건(전부):** High 미해결 0건 · 필수 테스트 실제 통과(`*_test_results*.txt`) · 양측 최종 `VERDICT: APPROVE` · 모든 Codex meta `success` + 해시·thread_id 재검증 통과.

**완료 시:** `90_final_report.md`(구현 요약, 채택/기각/미해결 통계, 테스트·미실행 테스트·검증 한계, Codex 호출 이력(manifest `codex_calls`), 중간 커밋 SHA 목록) 작성 → `.git/auto_commit_msg.txt`에 WHY 메시지 작성 → 센티널 삭제 → 사용자 요약.

**실패 구분:**
| 상황 | 보고 |
|------|------|
| Codex 호출 실패(인증/타임아웃/빈 출력/해시 불일치) 재시도도 실패 | "Codex 미실행 — 교차 검증 미완료". 진행 여부 확인(센티널 유지) |
| Codex `quota` 재시도도 실패 | Codex 단독으로 완료하되 첫머리에 "⚠ Codex 단독 수행 — 교차 검증 아님(Codex 토큰 부족)". `DEGRADED.md`(사유·시점·스킵 단계), manifest `degraded: true`. "교차 검증 완료" 문구 금지 |
| 독립성 위반 감지 | 해당 산출물 무효 + 1회 재호출. 재위반 시 그 단계는 "독립성 미보장"으로 보고 |
| 3라운드 후 미합의 | "미합의" — 쟁점·양측 근거·선택지. 센티널 유지한 채 사용자 결정 대기 |
| 테스트 실패 잔존 | "미완료" — 실패 원문. 메시지 파일 미작성, 센티널 삭제 후 "미검증 변경 잔존" 경고 |

## 에러 핸들링
- 에이전트 실패: 1회 재호출(`_rN`이 아닌 같은 파일명 덮어쓰기), 재실패 시 단계 실패 보고.
- codex-adapter 실패 유형은 어댑터 에러 표 참조. 인증 오류는 재시도 없이 보고. quota 재시도 결과가 인증·실행 오류면 quota 폴백이 아니라 그 유형으로 처리.
- 충돌 의견은 삭제하지 않고 출처(Codex/Codex) 병기.

## 테스트 시나리오
**정상:** "`/weatherforecast` 응답에 `GeneratedAt`(UTC) 필드를 코덱스 교차 검증으로 추가해줘" → Phase 0(센티널·base) → 양측 독립 계획 → 교환·조정·통합 → 구현+테스트 → 양측 독립 리뷰 → 조정·(수정)·재검토 → APPROVE×2 + 테스트 통과 → 90 보고 + 메시지 파일 → 센티널 삭제.
**에러 1(인증 만료):** Phase 0 `codex login status` 실패 → 중단, 센티널 삭제.
**에러 2(리뷰 3라운드 초과):** High 1건 미합의 → 쟁점·근거·선택지 제시, 센티널 유지.
**에러 3(quota):** review 단계 `quota` 2회 → Codex 단독 재검토, `DEGRADED.md`, 보고 첫머리 경고.

## 설치·실행 방법과 사용 예시
`references/usage.md` 참조.
