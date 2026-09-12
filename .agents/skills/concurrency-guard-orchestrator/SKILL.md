---
name: concurrency-guard-orchestrator
description: ".NET 10 고성능 비동기 서버 라이브러리의 동시성·락·데드락을 종합 감사하는 오케스트레이터. Lock-Free 설계 강제, 락 정당화 주석 감사, 데드락 생성-검증 분석을 에이전트 팀으로 조율하고 단일 보안 리포트를 생성한다. 트리거: '동시성 검사', '락 감사', '데드락 분석', '동시성 리뷰', 'Lock-Free 검증', 'async 데드락', 'concurrency guard', '컨텐션 분석'. 후속 작업: '다시 분석', '데드락만 재검토', '락 정당화 재감사', '이전 결과 업데이트', '보완 분석'."
---

# Concurrency Guard Orchestrator

.NET 10 고성능 비동기 서버 라이브러리를 위한 동시성 전문 감사 팀을 조율하는 오케스트레이터.

## 실행 모드: 하이브리드 (Agent 팬아웃 + 순차 생성-검증)

| Phase | 모드 | 팀 구성 | 이유 |
|-------|------|---------|------|
| Phase A (병렬 락 감사) | 에이전트 팀 | lock-free-enforcer ↔ lock-justification-auditor | 두 에이전트가 락 목록 공유·조율 필요 |
| Phase B (생성-검증) | 에이전트 팀 (동일 팀) | deadlock-analyzer → deadlock-reviewer | 분석기-검증자 직접 SendMessage 교환 |

세션당 팀 1개 제약 → 4개 에이전트를 단일 팀으로 구성하고 `depends_on`으로 Phase A/B 순서를 모델링한다.

## 에이전트 구성

| 팀원 | 에이전트 타입 | 스킬 | 출력 |
|------|-------------|------|------|
| lock-free-enforcer | lock-free-enforcer | /lock-free-enforcement | `02_lockfree_findings.json` |
| lock-justification-auditor | lock-justification-auditor | /lock-justification-audit | `02_lockjustification_findings.json` |
| deadlock-analyzer | deadlock-analyzer | /deadlock-static-analysis | `03_deadlock_analysis.json` |
| deadlock-reviewer | deadlock-reviewer | /deadlock-review | `03_deadlock_review.json` |

---

## 워크플로우

### Phase 0: 컨텍스트 확인

1. `_workspace/concurrency-guard/` 존재 여부 확인
2. 분기:
   - **미존재** → 초기 실행. Phase 1로 진행
   - **존재 + 특정 에이전트 재실행 요청** ("데드락만 다시") → **부분 재실행**: 해당 에이전트만 재호출, Phase 5에서 전체 리포트 재통합
   - **존재 + 새 코드 제공** → **새 실행**: `_workspace/concurrency-guard/`를 `_workspace/concurrency-guard_{YYYYMMDD_HHMMSS}/`로 이동 후 Phase 1

### Phase 1: 분석 대상 수집

**케이스 A — 인수 없음 (현재 브랜치 diff):**
```bash
BASE=$(git merge-base HEAD main 2>/dev/null || git merge-base HEAD master 2>/dev/null)
git diff $BASE HEAD
```

**케이스 B — 경로 지정:**
```bash
# .cs 파일들을 읽어 단일 파일로 병합
find <path> -name "*.cs" -exec cat {} \; > combined_source.txt
```

**케이스 C — PR 번호:**
```bash
gh pr diff <PR번호>
```

수집 내용을 `_workspace/concurrency-guard/00_input/source.txt`에 저장한다.
소스가 비어있으면 사용자에게 알리고 중지한다.

### Phase 2: 실행 규칙

**공통 실행 규칙 (이 빌드에는 TeamCreate/TaskCreate/TaskGet/TeamDelete 팀 도구가 없다):**
- 병렬 실행이 필요한 에이전트는 **한 메시지 안에서 `Agent` 도구를 여러 번 호출**해 동시에 띄운다.
- 각 프롬프트에 프로젝트 루트, 입력 파일, 출력 파일 경로, "완료 시 severity별 건수·점수를 한 줄로 보고"를 명시한다.
- 완료는 **task-notification(완료 알림)** 으로 수신한다. 후속 지시가 필요하면 `SendMessage(to=<agentId>)` 로 보낸다.
- 순차 의존 단계는 앞 단계의 완료 알림을 받은 뒤 다음 `Agent` 를 호출한다.
- 에이전트 1개 실패 시 동일 프롬프트로 1회 재호출, 재실패 시 해당 도메인을 "수집 실패"로 표기하고 계속한다.

### Phase 3: Phase A — 병렬 락 감사 (Agent 팬아웃)

아래 2개를 **단일 메시지에서 동시에** 호출한다:

```
Agent(subagent_type="lock-free-enforcer", description="Lock-free audit",
      prompt="당신은 lock-free-enforcer입니다. 프로젝트 루트는 {project_root} 입니다.
              lock-free-enforcement 스킬을 사용하여 _workspace/concurrency-guard/00_input/source.txt 를 감사하고
              결과를 _workspace/concurrency-guard/02_lockfree_findings.json 에 저장하세요.
              necessary_locks 목록은 JSON 안에 포함하세요. 완료 후 건수·점수를 한 줄로 보고하세요.")
Agent(subagent_type="lock-justification-auditor", description="Lock justification audit",
      prompt="당신은 lock-justification-auditor입니다. ... lock-justification-audit 스킬로 source.txt 를 감사하고
              결과를 _workspace/concurrency-guard/02_lockjustification_findings.json 에 저장하세요. ...")
```

**필요 락 목록 공유:** lock-free-enforcer 완료 알림을 먼저 받으면 `02_lockfree_findings.json` 의
necessary_locks 를 `SendMessage` 로 lock-justification-auditor 에게 전달한다 (아직 실행 중일 때만).
이미 둘 다 끝났으면 Phase 5 통합 시 리더가 직접 대조한다.

```
lock-free-enforcer  →  [necessary_locks 목록]  →  lock-justification-auditor
      ↓                                                      ↓
02_lockfree_findings.json                    02_lockjustification_findings.json
```

두 완료 알림을 모두 수신하면 Phase 4로 진행한다.

### Phase 4: Phase B — 생성-검증 데드락 분석 (순차)

**Step 1 — 분석기 호출** (lock-free-enforcer 완료 후):
```
Agent(subagent_type="deadlock-analyzer", description="Deadlock static analysis",
      prompt="deadlock-static-analysis 스킬로 _workspace/concurrency-guard/00_input/source.txt 와
              _workspace/concurrency-guard/02_lockfree_findings.json 을 분석하고 _workspace/concurrency-guard/03_deadlock_analysis.json 에 저장하세요.")
```

**Step 2 — 검증자 호출** (분석기 완료 알림 후):
```
Agent(subagent_type="deadlock-reviewer", description="Deadlock review",
      prompt="deadlock-review 스킬로 _workspace/concurrency-guard/03_deadlock_analysis.json 을 독립 검증하고
              _workspace/concurrency-guard/03_deadlock_review.json 에 저장하세요. 재분석이 필요한 항목은 needs_reanalysis 배열로 보고하세요.")
```

**Step 3 — 재분석 (최대 1회):** 검증 결과에 needs_reanalysis 가 있으면 deadlock-analyzer 를
해당 항목 목록과 함께 1회 재호출하고, 이어서 deadlock-reviewer 를 1회 재호출한다.

```
deadlock-analyzer → [분석 완료] → deadlock-reviewer
                                        ↓
                         [기각/수정/추가 발견]
                                        ↓
                    재분석 필요? → deadlock-analyzer (최대 1회)
                                        ↓
                         [최종 확정] → 리더 완료 알림
```

최대 2라운드(초기 분석 + 1회 재분석) 보장.

### Phase 5: 결과 통합 및 리포트 생성

4개 파일을 Read로 수집:
- `_workspace/concurrency-guard/02_lockfree_findings.json`
- `_workspace/concurrency-guard/02_lockjustification_findings.json`
- `_workspace/concurrency-guard/03_deadlock_analysis.json`
- `_workspace/concurrency-guard/03_deadlock_review.json`

**종합 점수 계산:**
```
lockfree_score         (lock-free-enforcer)
lockjustification_score (lock-justification-auditor)
deadlock_final_score   (deadlock-reviewer의 final_score)

overall = lockfree_score * 0.35
        + lockjustification_score * 0.30
        + deadlock_final_score * 0.35
```

**리포트 형식** (`_workspace/concurrency-guard/04_concurrency_guard_report.md`):

```markdown
# 동시성 가드 리포트
생성: {datetime} | 대상: {target}

## 종합 건강 점수
| 도메인 | 점수 | Critical | High | Medium | Low |
|--------|------|----------|------|--------|-----|
| 🔓 Lock-Free | XX | N | N | N | N |
| 📝 락 정당화 | XX | — | N | N | N |
| ⚡ 데드락 위험 | XX | N | N | N | N |
| **종합** | **XX** | **N** | **N** | **N** | **N** |

## CRITICAL — 즉시 수정 필수
[CRITICAL 발견사항 — 데드락 즉각 재현 가능]

## HIGH — 머지 전 수정
[HIGH 발견사항]

## 락 정당화 주석 미비 목록
[주석 없거나 불충분한 락 위치 목록]
[각 항목에 필수 주석 템플릿 제공]

## Medium / Low
[요약]

## 총평 및 판정
APPROVE / REQUEST CHANGES / BLOCK
```

### Phase 6: 정리

1. 별도 팀 해제 절차 없음
2. `_workspace/concurrency-guard/` 보존
3. 리포트 내용 출력 + 경로 안내

---

## 데이터 흐름

```
Phase 1: source.txt 수집
    ↓
Phase 2: 실행 규칙 확인
    ↓
Phase 3: Agent 2개 동시 호출 [lock-free-enforcer] ↔ [lock-justification-auditor]
         락 목록 공유 via SendMessage
    ↓
Phase 4: [deadlock-analyzer] → [deadlock-reviewer] → (재분석 가능, 1회)
    ↓
Phase 5: 4개 JSON 통합 → concurrency_guard_report.md
    ↓
Phase 6: 보고
```

---

## 에러 핸들링

| 상황 | 처리 |
|------|------|
| 에이전트 1개 실패 | 동일 프롬프트로 1회 재호출 → 재실패 시 해당 도메인 "수집 실패"로 표시 |
| lock-justification-auditor가 necessary_locks 미수신 | 소스 전체에서 직접 락 탐지로 전환 |
| deadlock-reviewer 재분석 요청 타임아웃 | 기존 분석 결과로 검증 진행 |
| 의견 불일치 3개+ | `disputed_findings`로 기록, 리더가 사용자에게 중재 요청 |

---

## 테스트 시나리오

### 정상 흐름
1. 사용자: "이 PR의 동시성 검사해줘 #23"
2. Phase 1: `gh pr diff 23` → source.txt
3. Phase 3: lock-free-enforcer가 `lock(_sync)` 3개 발견, 1개는 replaceable, 2개는 necessary. lock-justification-auditor에게 necessary_locks 전달.
4. lock-justification-auditor: 2개 necessary 락 중 1개는 [LOCK-REQUIRED] 없음 → HIGH 발견
5. Phase 4: deadlock-analyzer가 `.Result` 1개(CRITICAL), `ConfigureAwait` 누락 2개(MEDIUM) 발견
6. deadlock-reviewer: `.Result` 발견 확인(Confirmed), ConfigureAwait 중 1개는 Main에서만 호출 → 기각(Rejected)
7. Phase 5: 종합 리포트 생성, CRITICAL 1건 → BLOCK 판정

### 에러 흐름 (deadlock-reviewer 응답 없음)
1. Phase 4에서 deadlock-reviewer 타임아웃 (10분 초과)
2. 리더가 기존 `03_deadlock_analysis.json`으로 직접 Phase 5 진행
3. 리포트에 "⚠️ 데드락 검증 미완료 — 분석 결과 미검증" 명시
