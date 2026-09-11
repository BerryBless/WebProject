---
name: gc-guard-orchestrator
description: ".NET 10 서버 라이브러리의 GC 압력 억제를 위한 메모리 최적화 팀을 조율하는 오케스트레이터. 힙 할당 스캐너와 풀링 강제자가 병렬 감사 후 상호 리뷰를 거쳐 단일 GC 가드 리포트를 생성한다. 트리거: 'GC 억제', '힙 할당 감사', '메모리 최적화', 'ArrayPool 검사', 'ValueTask 검증', 'Span 적용', 'boxing 탐지', 'GC 압력 분석', '메모리 최적화 리뷰'. 후속 작업: '다시 분석', 'GC 재검토', '할당 보완', '이전 결과 업데이트', 'ValueTask만 다시'."
---

# GC Guard Orchestrator

.NET 10 고성능 서버 라이브러리의 GC 억제를 위한 메모리 최적화 팀을 조율하는 오케스트레이터.

## 실행 모드: 하이브리드 (Agent 팬아웃 + 순차 교차 검증)

| Phase | 모드 | 팀 구성 | 이유 |
|-------|------|---------|------|
| Phase A (병렬 감사) | 에이전트 팀 | heap-allocation-scanner ↔ pooling-enforcer | 버퍼 할당 발견을 실시간 공유, 상호 보완 |
| Phase B (교차 검증) | 에이전트 팀 (동일) | allocation-peer-reviewer | 두 보고서를 독립 교차 검증 |

4개 에이전트 대신 **3개 에이전트**를 단일 팀으로 구성하고, `depends_on`으로 Phase A/B 순서를 모델링한다.

## 에이전트 구성

| 팀원 | 에이전트 타입 | 스킬 | 출력 |
|------|-------------|------|------|
| heap-allocation-scanner | heap-allocation-scanner | /heap-allocation-scan | `02_allocation_findings.json` |
| pooling-enforcer | pooling-enforcer | /pooling-enforcement | `02_pooling_findings.json` |
| allocation-peer-reviewer | allocation-peer-reviewer | /allocation-peer-review | `03_peer_review.json` |

---

## 워크플로우

### Phase 0: 컨텍스트 확인

1. `_workspace/` 존재 여부 확인
2. 분기:
   - **미존재** → 초기 실행. Phase 1 진행
   - **존재 + 특정 에이전트 재실행** ("ValueTask만 다시") → 부분 재실행: 해당 에이전트만 재호출 후 Phase 4(리포트) 재통합
   - **존재 + 새 코드** → 새 실행: 기존 `_workspace/`를 `_workspace_{YYYYMMDD_HHMMSS}/`로 이동 후 Phase 1

### Phase 1: 분석 대상 수집

**케이스 A — 현재 브랜치 diff:**
```bash
BASE=$(git merge-base HEAD main 2>/dev/null || git merge-base HEAD master 2>/dev/null)
git diff $BASE HEAD -- "*.cs"
```

**케이스 B — 경로 지정:**
```bash
find <path> -name "*.cs" | xargs cat
```

**케이스 C — PR 번호:**
```bash
gh pr diff <PR번호> -- "*.cs"
```

수집 내용을 `_workspace/00_input/source.txt`에 저장한다.
빈 경우 사용자에게 대상 지정 요청 후 중지한다.

### Phase 2: 실행 규칙

**공통 실행 규칙 (이 빌드에는 TeamCreate/TaskCreate/TaskGet/TeamDelete 팀 도구가 없다):**
- 병렬 실행이 필요한 에이전트는 **한 메시지 안에서 `Agent` 도구를 여러 번 호출**해 동시에 띄운다.
- 각 프롬프트에 프로젝트 루트, 입력 파일, 출력 파일 경로, "완료 시 severity별 건수·점수를 한 줄로 보고"를 명시한다.
- 완료는 **task-notification(완료 알림)** 으로 수신한다. 후속 지시가 필요하면 `SendMessage(to=<agentId>)` 로 보낸다.
- 순차 의존 단계는 앞 단계의 완료 알림을 받은 뒤 다음 `Agent` 를 호출한다.
- 에이전트 1개 실패 시 동일 프롬프트로 1회 재호출, 재실패 시 해당 도메인을 "수집 실패"로 표기하고 계속한다.

### Phase 3: Phase A — 병렬 감사 (Agent 팬아웃)

아래 2개를 **단일 메시지에서 동시에** 호출한다:

```
Agent(subagent_type="heap-allocation-scanner", description="Heap allocation scan",
      prompt="당신은 heap-allocation-scanner입니다. 프로젝트 루트는 {project_root} 입니다.
              heap-allocation-scan 스킬로 _workspace/00_input/source.txt 의 hot path 힙 할당을 탐지하고
              _workspace/02_allocation_findings.json 에 저장하세요. 버퍼/배열 할당은 buffer_allocations 배열로 별도 표기하세요.
              완료 후 건수·점수를 한 줄로 보고하세요.")
Agent(subagent_type="pooling-enforcer", description="Pooling enforcement",
      prompt="당신은 pooling-enforcer입니다. ... pooling-enforcement 스킬로 source.txt 의 ValueTask/Span/ArrayPool 패턴을 점검하고
              _workspace/02_pooling_findings.json 에 저장하세요. ...")
```

**버퍼 할당 공유:** heap-allocation-scanner 완료 알림을 먼저 받으면 buffer_allocations 를
`SendMessage` 로 pooling-enforcer 에게 전달한다 (아직 실행 중일 때만).

```
heap-allocation-scanner  →  [버퍼 할당 목록]  →  pooling-enforcer
         ↓                                               ↓
02_allocation_findings.json                02_pooling_findings.json
```

두 완료 알림을 모두 수신하면 Phase B로 진행한다.

### Phase 4: Phase B — 교차 검증 (순차)

```
Agent(subagent_type="allocation-peer-reviewer", description="Allocation peer review",
      prompt="allocation-peer-review 스킬로 _workspace/02_allocation_findings.json 과 _workspace/02_pooling_findings.json 을
              _workspace/00_input/source.txt 기준으로 독립 교차 검증하고 _workspace/03_peer_review.json 에 저장하세요.")
```

```
02_allocation_findings.json ─┐
                              ├─▶ allocation-peer-reviewer ─▶ 03_peer_review.json
02_pooling_findings.json ────┘           +
source.txt (독립 FN 탐지)
```

### Phase 5: 결과 통합 및 리포트 생성

3개 파일을 Read로 수집:
- `_workspace/02_allocation_findings.json`
- `_workspace/02_pooling_findings.json`
- `_workspace/03_peer_review.json`

**종합 점수:**
```
final_score = peer_review.final_score  (교차 검증 후 확정 점수)
allocation_raw = allocation_findings.score  (스캐너 원점수, 참고용)
pooling_raw = pooling_findings.score    (강제자 원점수, 참고용)
```

**리포트 형식** (`_workspace/04_gc_guard_report.md`):

```markdown
# GC 가드 리포트
생성: {datetime}  |  대상: {target}

## 메모리 건강 점수
| 에이전트 | 원점수 | 확정 점수 |
|---------|--------|----------|
| 힙 할당 스캐너 | XX | (교차 검증 후) |
| 풀링 강제자 | XX | (교차 검증 후) |
| **최종 (교차 검증)** | — | **XX / 100** |

## CRITICAL — 즉시 수정 (GC 압력 심각)
[Confirmed critical 발견]
위치 / 패턴 / 문제 / 수정 코드

## HIGH — 머지 전 수정
[Confirmed high 발견 + FN에서 추가된 발견]

## ArrayPool.Return 누락 목록
[모든 Rent-without-Return 발견, CRITICAL 수준]

## 교차 검증 결과
- 기각된 False Positive: N건
- 추가 발견(FN): N건
- 수정된 fix_code: N건

## Medium / Low
[요약 목록]

## 판정
APPROVE / REQUEST CHANGES / BLOCK
```

### Phase 6: 정리

1. 별도 팀 해제 절차 없음
2. `_workspace/` 보존
3. 리포트 출력 + 경로 안내

---

## 에러 핸들링

| 상황 | 처리 |
|------|------|
| 에이전트 1명 실패 | 1회 재시작 → 재실패 시 해당 도메인 누락 표시 |
| 두 에이전트 모두 실패 | 사용자에게 알리고 진행 여부 확인 |
| 교차 검증 FP 비율 60%+ | 리포트에 "스캐너 방법론 재검토 권고" 명시 |
| peer_review.json 없음 | 두 원본 보고서로 리포트 생성, 검증 미완료 표시 |

---

## 테스트 시나리오

### 정상 흐름
1. 사용자: "Server.cs 메모리 최적화 검사해줘"
2. Phase 1: Server.cs 읽어 source.txt 저장
3. Phase 3: scanner가 `new byte[1024]` in loop(HIGH), boxer가 `Task<T>` → `ValueTask<T>` 교체 제안
4. scanner가 `new byte[1024]` 발견을 pooling-enforcer에게 SendMessage
5. pooling-enforcer가 ArrayPool 대안 코드 스니펫 작성
6. Phase 4: peer-reviewer가 scanner의 클로저 발견 1건 FP 기각(static lambda), FN 1건 추가(params boxing)
7. Phase 5: 최종 점수 72, REQUEST CHANGES

### 에러 흐름 (pooling-enforcer 실패)
1. Phase 3에서 pooling-enforcer 응답 없음 (10분 초과)
2. allocation-peer-reviewer가 allocation_findings.json만으로 검증 진행
3. 리포트에 "⚠️ 풀링 강제 에이전트 미완료 — ValueTask/ArrayPool 검증 수동 필요" 명시
