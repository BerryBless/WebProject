---
name: code-review-orchestrator
description: "종합 코드 리뷰 하네스를 실행하는 오케스트레이터. 아키텍처·보안·성능·코드 스타일을 4개 에이전트가 병렬 감사하고 단일 리포트로 통합한다. 트리거: '코드 리뷰', '리뷰 해줘', '코드 점검', 'PR 검토', '코드 감사', '종합 리뷰', '전체 리뷰', '이 코드 봐줘'. 후속 작업: '다시 리뷰', '리뷰 업데이트', '보안만 다시', '아키텍처 재검토', '이전 리뷰 개선', '리뷰 보완'. 파일 경로나 PR 번호가 언급되면 반드시 이 스킬을 사용할 것."
---

# Code Review Orchestrator

종합 코드 리뷰 팀을 조율하여 단일 리포트를 생성하는 오케스트레이터.

## 실행 모드: 에이전트 팀 (팬아웃/팬인)

## 에이전트 구성

| 팀원 | 에이전트 타입 | 역할 | 스킬 | 출력 |
|------|-------------|------|------|------|
| architecture-reviewer | architecture-reviewer | SOLID·레이어·결합도 감사 | /architecture-review | `_workspace/02_architecture_findings.json` |
| security-reviewer | security-reviewer | OWASP·CWE 기반 취약점 스캔 | /security-review | `_workspace/02_security_findings.json` |
| performance-reviewer | performance-reviewer | N+1·async·LINQ 병목 탐지 | /performance-review | `_workspace/02_performance_findings.json` |
| style-reviewer | style-reviewer | 네이밍·복잡도·문서화 감사 | /style-review | `_workspace/02_style_findings.json` |

---

## 워크플로우

### Phase 0: 컨텍스트 확인 (후속 작업 지원)

1. `_workspace/` 디렉토리 존재 여부 확인
2. 실행 모드 결정:
   - **`_workspace/` 미존재** → 초기 실행. Phase 1로 진행
   - **`_workspace/` 존재 + 특정 도메인 재검토 요청** (예: "보안만 다시") → **부분 재실행**:
     - 해당 에이전트만 재호출
     - 기존 다른 에이전트의 JSON은 그대로 유지
     - Phase 4(통합)만 다시 실행
   - **`_workspace/` 존재 + 새 코드/파일 제공** → **새 실행**:
     - 기존 `_workspace/`를 `_workspace_{YYYYMMDD_HHMMSS}/`로 이동
     - Phase 1부터 새로 시작

---

### Phase 1: 리뷰 대상 수집

사용자 입력에 따라 아래 중 하나를 실행하여 diff 내용을 수집한다.

**케이스 A — 인수 없음 (현재 브랜치 vs. main/master):**
```bash
# 베이스 브랜치 감지
BASE=$(git merge-base HEAD main 2>/dev/null || git merge-base HEAD master 2>/dev/null || git merge-base HEAD origin/main 2>/dev/null)
git diff $BASE HEAD
```

**케이스 B — 경로 지정 (예: `/comprehensive-review src/Api/`):**
```bash
# 특정 파일 또는 디렉토리의 현재 상태
cat <path>  # 단일 파일
# 또는 디렉토리 내 .cs 파일들을 순차 읽기
```

**케이스 C — PR 번호 (예: `/comprehensive-review 42`):**
```bash
gh pr diff 42
```

수집된 내용을 `_workspace/00_input/diff.txt`에 저장한다.
diff가 비어있으면 사용자에게 알리고 중지한다.

**diff 크기 관리:** diff가 800줄을 초과하면 파일별로 요약을 작성하여 컨텍스트 부담을 줄인다:
```
[파일 요약: src/Api/Controllers/UserController.cs — 150줄 추가, 주요 변경: 사용자 인증 엔드포인트 3개 추가]
```

---

### Phase 2: 병렬 감사 실행 (Agent 팬아웃)

**공통 실행 규칙 (이 빌드에는 TeamCreate/TaskCreate/TaskGet/TeamDelete 팀 도구가 없다):**
- 병렬 실행이 필요한 에이전트는 **한 메시지 안에서 `Agent` 도구를 여러 번 호출**해 동시에 띄운다.
- 각 프롬프트에 프로젝트 루트, 입력 파일, 출력 파일 경로, "완료 시 severity별 건수·점수를 한 줄로 보고"를 명시한다.
- 완료는 **task-notification(완료 알림)** 으로 수신한다. 후속 지시가 필요하면 `SendMessage(to=<agentId>)` 로 보낸다.
- 순차 의존 단계는 앞 단계의 완료 알림을 받은 뒤 다음 `Agent` 를 호출한다.
- 에이전트 1개 실패 시 동일 프롬프트로 1회 재호출, 재실패 시 해당 도메인을 "수집 실패"로 표기하고 계속한다.

아래 4개를 **단일 메시지에서 동시에** 호출한다 (서브에이전트 타입 = 에이전트 파일명):

```
Agent(subagent_type="architecture-reviewer", description="Architecture review",
      prompt="당신은 종합 코드 리뷰 팀의 아키텍처 리뷰어입니다. 프로젝트 루트는 {project_root} 입니다.
              architecture-review 스킬을 사용하여 _workspace/00_input/diff.txt 를 감사하고
              결과 JSON을 _workspace/02_architecture_findings.json 에 저장하세요.
              완료 후 severity별 건수와 점수를 한 줄로 보고하세요. 프로젝트 소스는 수정하지 마세요.")
Agent(subagent_type="security-reviewer",     ... _workspace/02_security_findings.json ...)
Agent(subagent_type="performance-reviewer",  ... _workspace/02_performance_findings.json ...)
Agent(subagent_type="style-reviewer",        ... _workspace/02_style_findings.json ...)
```

### Phase 3: 완료 대기 및 조율

4개 완료 알림을 모두 수신할 때까지 기다린다. 알림에 담긴 한 줄 요약(건수·점수)을 기록한다.

**중복 발견 조율 규칙:**
- 동일한 코드 위치에 대한 발견이 두 에이전트에서 나오면, 각자 독립적으로 기록한다 (관점이 다름)
- 완전히 동일한 내용(같은 severity, 같은 제목)이면 Phase 4 통합 시 더 관련성 높은 도메인만 남긴다

모든 알림을 수신하면 Phase 4로 진행한다.

---

### Phase 4: 결과 통합 및 리포트 생성

1. 4개 JSON 파일을 Read로 수집한다:
   - `_workspace/02_architecture_findings.json`
   - `_workspace/02_security_findings.json`
   - `_workspace/02_performance_findings.json`
   - `_workspace/02_style_findings.json`

2. 파일이 없거나 파싱 실패 시: 해당 도메인을 "수집 실패"로 표시하고 나머지로 진행

3. **종합 점수 계산:**
   ```
   overall = security_score × 0.35
           + architecture_score × 0.25
           + performance_score × 0.25
           + style_score × 0.15
   ```

4. 다음 형식으로 리포트를 생성하여 `_workspace/03_consolidated_report.md`에 저장한다:

---

```markdown
# 종합 코드 리뷰 리포트
**생성:** {datetime}  |  **대상:** {target 설명}

---

## 종합 건강 점수

| 도메인 | 점수 | Critical | High | Medium | Low |
|--------|------|----------|------|--------|-----|
| 🏗️ 아키텍처 | XX / 100 | N | N | N | N |
| 🔒 보안 | XX / 100 | N | N | N | N |
| ⚡ 성능 | XX / 100 | N | N | N | N |
| 🎨 스타일 | XX / 100 | — | N | N | N |
| **종합** | **XX / 100** | **N** | **N** | **N** | **N** |

가중치: 보안 35% · 아키텍처 25% · 성능 25% · 스타일 15%

---

## Critical & High 발견사항 ← 머지 전 필수 수정

### [도메인] [SEVERITY] — 제목
**위치:** `파일명:라인`
**CWE:** CWE-XXX _(보안만)_
**문제:** 상세 설명
**수정:** 제안

_(없으면: "Critical/High 발견사항 없음 ✅")_

---

## Medium 발견사항 ← 권장 수정

_(없으면 생략)_

---

## Low / 정보성 ← 검토 권장

- [도메인] `파일명:라인` — 제목: 한줄 요약
- ...

_(없으면 생략)_

---

## 총평 및 판정

{3–5문장 종합 평가}

**판정: APPROVE / REQUEST CHANGES / BLOCK**

- **APPROVE**: Critical·High 없음, 전체 점수 80+
- **REQUEST CHANGES**: High 발견 또는 전체 점수 60–79
- **BLOCK**: Critical 발견 또는 전체 점수 60 미만
```

---

### Phase 5: 정리

1. 별도 팀 해제 절차 없음 (서브에이전트는 완료와 함께 종료됨)
2. `_workspace/` 보존 (중간 산출물 삭제 안 함 — 사후 확인용)
4. 사용자에게 리포트 내용 출력 + 파일 경로 안내:
   - 상세 리포트: `_workspace/03_consolidated_report.md`
   - 도메인별 원본: `_workspace/02_{domain}_findings.json`

---

## 데이터 흐름

```
사용자 요청
    │
    ▼
Phase 1: diff 수집 → _workspace/00_input/diff.txt
    │
    ▼
Phase 2: Agent 4개 동시 호출 (단일 메시지)
    │
    ▼
Phase 3: 완료 알림 4개 수신
    ├── architecture-reviewer → 02_architecture_findings.json
    ├── security-reviewer     → 02_security_findings.json
    ├── performance-reviewer  → 02_performance_findings.json
    └── style-reviewer        → 02_style_findings.json
    │
    ▼
Phase 4: 4개 JSON 통합 → 03_consolidated_report.md
    │
    ▼
Phase 5: 사용자 보고
```

---

## 에러 핸들링

| 상황 | 처리 |
|------|------|
| 에이전트 1개 실패 | 동일 프롬프트로 1회 재호출 → 재실패 시 해당 도메인 "수집 실패"로 표시하고 계속 |
| 에이전트 2개+ 실패 | 사용자에게 알리고 진행 여부 확인 |
| JSON 파싱 실패 | 해당 도메인 건너뜀, 리포트에 "파싱 실패" 명시 |
| diff 없음 | 즉시 중지, 사용자에게 대상 지정 요청 |
| 타임아웃 (완료 알림 없음 10분+) | 현재까지 수집된 결과로 Phase 4 진행, 미완료 도메인은 "수집 실패" 표기 |

---

## 테스트 시나리오

### 정상 흐름
1. 사용자: "이 PR 리뷰해줘 #15"
2. Phase 1: `gh pr diff 15` 실행 → diff 수집
3. Phase 2: Agent 4개 동시 호출
4. Phase 3: 완료 알림 4개 수신, 중복 발견 조율
5. Phase 4: JSON 4개 통합, 종합 점수 산출, 리포트 생성
6. Phase 5: 리포트 출력
7. 예상: `_workspace/03_consolidated_report.md` 생성, 판정 제시

### 에러 흐름 (팀원 1명 실패)
1. Phase 2 중 performance-reviewer가 에러로 중지
2. 리더가 실패 알림 수신
3. 동일 프롬프트로 1회 재호출
4. 재호출 실패 시 나머지 3개 도메인 결과로 Phase 4 진행
5. 리포트에 "⚠️ performance 도메인 수집 실패 — 수동 확인 필요" 명시
