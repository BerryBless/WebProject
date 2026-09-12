---
name: pipeline-architect-orchestrator
description: "System.IO.Pipelines 기반 고성능 서버 라이브러리의 IO 루프·스레드 디스패처를 감독자 패턴으로 설계하고 부하 테스트 감사까지 수행하는 오케스트레이터. 트리거: 'Pipelines 설계', 'IO 루프 구현', '디스패처 설계', 'Zero-copy 서버', 'Kestrel 패턴', '고성능 IO', 'PipeReader 설계', 'Channel 디스패처'. 후속 작업: '다시 설계', 'IO 루프 재작업', '디스패처 수정', '감사 재실행', '이전 결과 개선'."
---

# Pipeline Architect Orchestrator

System.IO.Pipelines 기반 IO 루프와 Channel<T> 기반 스레드 디스패처를 감독자 패턴으로 설계·검증하는 오케스트레이터.

## 실행 모드: 감독자 패턴 (Agent 중첩 호출)

```
[오케스트레이터] → Agent(pipeline-supervisor)
    └── [pipeline-supervisor] (감독자/리더) — 자신이 Agent 도구로 워커를 호출
            ├── 감독: [io-loop-designer] (워커 1)
            ├── 감독: [thread-dispatcher-designer] (워커 2)
            └── 위임: [load-test-auditor] (검증자)
```

감독자가 두 워커의 설계를 동적으로 조율하고, 품질 게이트 통과 후 감사자에게 위임한다.

## 에이전트 구성

| 팀원 | 에이전트 타입 | 역할 | 출력 |
|------|-------------|------|------|
| pipeline-supervisor | pipeline-supervisor | 감독, 조율, 통합 | `04_pipeline_architecture.md` |
| io-loop-designer | io-loop-designer | IO 루프 설계 | `02_io_loop/IoLoop.cs` |
| thread-dispatcher-designer | thread-dispatcher-designer | 디스패처 설계 | `02_dispatcher/ThreadDispatcher.cs` |
| load-test-auditor | load-test-auditor | 부하 테스트 감사 | `03_load_test_audit.md` |

---

## 워크플로우

### Phase 0: 컨텍스트 확인

1. `_workspace/pipeline/` 존재 여부 확인
2. 분기:
   - **미존재** → 초기 실행. Phase 1 진행
   - **존재 + 특정 재작업** ("IO 루프 다시") → 부분 재실행: 해당 워커만 재할당
   - **존재 + 새 요구사항** → 새 실행: `_workspace/pipeline/`를 `_workspace/pipeline_{YYYYMMDD_HHMMSS}/`로 이동

### Phase 1: 설계 브리프 수집

사용자 입력에서 다음을 파악하여 `_workspace/pipeline/00_design_brief.md`에 저장:

```markdown
# Pipeline 설계 브리프

## 서버 요구사항
- 프로토콜: [HTTP/WebSocket/Custom Binary/기타]
- 예상 동시 연결: [N개]
- 예상 처리량: [N rps / N msg/s]
- 메시지 최대 크기: [N KB]

## 성능 목표
- 레이턴시 목표: [N ms p99]
- 메모리 한계: [N MB per connection]
- GC 일시정지 허용: [있음/없음]

## 제약사항
- .NET 버전: [net10.0]
- 특이 사항: [...]
```

브리프가 불충분하면 사용자에게 핵심 항목(프로토콜, 처리량 목표)만 질문한다.

### Phase 2: 감독자 호출

**공통 실행 규칙 (이 빌드에는 TeamCreate/TaskCreate/TaskGet/TeamDelete 팀 도구가 없다):**
- 병렬 실행이 필요한 에이전트는 **한 메시지 안에서 `Agent` 도구를 여러 번 호출**해 동시에 띄운다.
- 각 프롬프트에 프로젝트 루트, 입력 파일, 출력 파일 경로, "완료 시 severity별 건수·점수를 한 줄로 보고"를 명시한다.
- 완료는 **task-notification(완료 알림)** 으로 수신한다. 후속 지시가 필요하면 `SendMessage(to=<agentId>)` 로 보낸다.
- 순차 의존 단계는 앞 단계의 완료 알림을 받은 뒤 다음 `Agent` 를 호출한다.
- 에이전트 1개 실패 시 동일 프롬프트로 1회 재호출, 재실패 시 해당 도메인을 "수집 실패"로 표기하고 계속한다.

오케스트레이터는 **감독자 1개만** 호출하고, 워커·감사자 호출은 감독자가 자신의 `Agent` 도구로 수행한다.

```
Agent(subagent_type="pipeline-supervisor", description="Pipeline design supervision",
      prompt="당신은 파이프라인 설계 팀의 감독자입니다. 프로젝트 루트는 {project_root} 입니다.
              _workspace/pipeline/00_design_brief.md 를 읽고 아래 절차를 수행하세요.
              1) 인터페이스 계약을 _workspace/pipeline/02_interface_contract.cs 에 작성
              2) Agent 도구로 io-loop-designer 와 thread-dispatcher-designer 를 단일 메시지에서 동시에 호출
                 (각각 io-loop-design / thread-dispatch-design 스킬 사용,
                  산출물 _workspace/pipeline/02_io_loop/IoLoop.cs, _workspace/pipeline/02_dispatcher/ThreadDispatcher.cs)
              3) 두 완료 알림 수신 후 품질 게이트 체크리스트로 검토, 불합격 시 해당 워커를 issues 목록과 함께 1회 재호출
              4) Agent 도구로 load-test-auditor 를 호출 (load-test-audit 스킬, 산출물 _workspace/pipeline/03_load_test_audit.md)
              5) BLOCK 판정이면 해당 워커 1회 재작업 후 재감사
              6) _workspace/pipeline/04_pipeline_architecture.md 에 최종 아키텍처 문서 작성
              완료 후 감사 판정(APPROVE/BLOCK)과 산출물 경로를 한 줄로 보고하세요.")
```

### Phase 3: 감독자 주도 설계 실행 (감독자 내부 절차)

**Step 1 — 인터페이스 계약 (병렬 시작 전)**
```
감독자가 브리프에서 ParsedMessage 타입·파이프 용량을 결정 → _workspace/pipeline/02_interface_contract.cs 작성
```

**Step 2 — 병렬 설계 (Agent 팬아웃, 단일 메시지)**
```
Agent(subagent_type="io-loop-designer",          prompt="... interface_contract.cs 를 준수하여 IoLoop.cs 작성 ...")
Agent(subagent_type="thread-dispatcher-designer", prompt="... interface_contract.cs 를 준수하여 ThreadDispatcher.cs 작성 ...")
```

**Step 3 — 품질 게이트 (각 완료 알림 수신 시)**
```
감독자가 산출물 파일 Read → 체크리스트 확인
합격 → 다음 단계
불합격 → 해당 워커를 {"action": "revision-required", "issues": [...]} 와 함께 재호출 (1회)
```

**Step 4 — 감사 위임**
```
두 워커 품질 게이트 통과 → Agent(subagent_type="load-test-auditor", prompt="... 두 파일 감사 → 03_load_test_audit.md")
```

**Step 5 — 통합**
```
감사 완료 → 감독자가 _workspace/pipeline/04_pipeline_architecture.md 작성
```

**감독자 개입 조건:**
- 워커 완료 알림이 10분+ 없음: 현재까지 산출물로 진행, 미완료 부분은 감독자가 직접 보완
- 재작업 요청 1회 후 미해결: 감독자가 해당 부분 직접 보완
- BLOCK 감사 결과: 해당 워커에게 재작업 지시 (1회 한도)

### Phase 4: 정리

1. 별도 팀 해제 절차 없음
2. `_workspace/pipeline/` 보존
3. 최종 아키텍처 문서 경로 안내

---

## 산출물 구조

```
_workspace/pipeline/
├── 00_design_brief.md              ← 설계 요구사항
├── 02_interface_contract.cs        ← IO 루프 ↔ 디스패처 인터페이스
├── 02_io_loop/
│   └── IoLoop.cs                   ← IO 루프 구현 (io-loop-designer)
├── 02_dispatcher/
│   └── ThreadDispatcher.cs         ← 디스패처 구현 (thread-dispatcher-designer)
├── 03_load_test_audit.md           ← 부하 테스트 감사 결과
└── 04_pipeline_architecture.md     ← 통합 아키텍처 문서 (감독자)
```

---

## 에러 핸들링

| 상황 | 처리 |
|------|------|
| 워커 응답 없음 | 감독자가 재지시 → 재응답 없으면 해당 파트 감독자가 직접 처리 |
| 인터페이스 합의 실패 | 감독자가 중재안 직접 제시 |
| BLOCK 감사 후 재작업 실패 | 감사 보고서에 미해결 BLOCK 명시하고 APPROVE 불가 판정 |
| 설계 브리프 불충분 | 핵심 항목만 사용자에게 질문 (프로토콜, 목표 처리량) |

---

## 테스트 시나리오

### 정상 흐름
1. 사용자: "TCP 이진 프로토콜 서버 파이프라인 설계해줘, 100k rps 목표"
2. Phase 1: 브리프 작성 (TCP/이진/100k rps/net10.0)
3. 인터페이스 협상: `ParsedMessage { ReadOnlySequence<byte> Payload; long ConnectionId; }`
4. IO 루프 설계: FillPipeAsync + ReadPipeAsync + 16KB 백프레셔
5. 디스패처 설계: BoundedChannel(capacity:1000) + 8 워커 + struct Work Item
6. 품질 게이트: 두 설계 모두 통과
7. 부하 감사: APPROVE (CRITICAL 0건)
8. 아키텍처 문서 생성

### 에러 흐름 (BLOCK 발견)
1. IO 루프 초안에서 `reader.AdvanceTo` 누락 → CRITICAL
2. 감독자가 io-loop-designer에게 재작업 지시
3. 재작업 후 AdvanceTo 추가 → 재감사 → APPROVE
4. 아키텍처 문서에 "초기 설계에서 AdvanceTo 누락 → 수정 완료" 기록
