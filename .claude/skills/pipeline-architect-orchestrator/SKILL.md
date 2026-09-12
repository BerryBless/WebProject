---
name: pipeline-architect-orchestrator
description: "System.IO.Pipelines 기반 고성능 서버 라이브러리의 IO 루프·스레드 디스패처를 감독자 패턴으로 설계하고, 독립 빌드 게이트와 부하 테스트 감사까지 수행하는 오케스트레이터. 트리거(설계 의도가 명시된 요청에만): 'Pipelines 설계', 'IO 루프 구현', '디스패처 설계', 'Zero-copy 서버', 'Kestrel 패턴', '고성능 IO', 'PipeReader 설계', 'Channel 디스패처'. 후속 작업: '다시 설계', 'IO 루프 재작업', '디스패처 수정', '감사 재실행', '이전 결과 개선'."
---

# Pipeline Architect Orchestrator

System.IO.Pipelines 기반 IO 루프와 Channel<T> 기반 디스패처를 감독자 패턴으로 설계·빌드·감사하는 오케스트레이터.

## 실행 모드: 감독자 패턴 (Agent 중첩 호출) + 오케스트레이터 독립 검증

**이 빌드에는 팀 도구가 없다.** 오케스트레이터는 `pipeline-supervisor` **1개**를 `Agent`로 호출하고, 감독자는 자신의 `Agent` 도구로 워커 2개(병렬)와 감사자 1개(순차)를 호출한다. 워커끼리는 통신하지 않는다 — **인터페이스 계약은 감독자가 먼저 확정해 파일로 넘기는 불변 입력**이며, 워커는 준수 불가 시 최종 응답에 `deviation`을 적어 반환한다(협상·대기 없음). 감독자의 한 줄 보고는 신뢰하지 않고 오케스트레이터가 산출물·빌드·감사 JSON을 **직접 재검증**한다.

```
[오케스트레이터] ─Agent→ [pipeline-supervisor]
                            ├─ 02_interface_contract.cs 확정
                            ├─Agent(병렬)→ io-loop-designer, thread-dispatcher-designer
                            ├─ build/Pipeline.csproj 생성 + dotnet build (경고 0·오류 0)
                            ├─Agent→ load-test-auditor
                            └─ 04_pipeline_architecture.md
[오케스트레이터] ─ 산출물 존재·빌드 재실행·감사 JSON verdict 대조 → 사용자 보고
```

## 에이전트 구성

| 에이전트 | 역할 | 스킬 | 출력 (run_dir 기준) |
|---------|------|------|------|
| pipeline-supervisor | 계약 확정·감독·빌드 게이트·통합 | 없음(에이전트 정의) | `02_interface_contract.cs`, `build/`, `04_pipeline_architecture.md`, `00_manifest.json` |
| io-loop-designer | IO 루프 구현 | /io-loop-design | `02_io_loop/IoLoop.cs` |
| thread-dispatcher-designer | 디스패처 구현 | /thread-dispatch-design | `02_dispatcher/ThreadDispatcher.cs` |
| load-test-auditor | 부하 관점 감사 | /load-test-audit | `03_load_test_audit[_rN].json` + `.md` |

## 작업 디렉토리

```
_workspace/pipeline/
├── latest.txt
└── <run_id>/                       # YYYYMMDD_HHmmss_약칭
    ├── 00_design_brief.md
    ├── 00_manifest.json            # brief_sha256, contract_sha256, artifacts{path: sha256}, build{ok, warnings, errors}, audit{round, verdict}, rework_count
    ├── 02_interface_contract.cs
    ├── 02_io_loop/IoLoop.cs
    ├── 02_dispatcher/ThreadDispatcher.cs
    ├── build/Pipeline.csproj       # net10.0 classlib, 위 3개 .cs 를 Compile Include
    ├── 03_load_test_audit[_rN].json / .md
    └── 04_pipeline_architecture.md
```
`_workspace/` 루트·다른 하네스 디렉토리는 건드리지 않는다. 보관 이동은 자기 디렉토리만.

**build/Pipeline.csproj 템플릿:**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <NoWarn>CS1591</NoWarn>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="../02_interface_contract.cs" />
    <Compile Include="../02_io_loop/*.cs" />
    <Compile Include="../02_dispatcher/*.cs" />
  </ItemGroup>
</Project>
```

---

## 워크플로우

### Phase 0: 실행 모드 결정
1. `latest.txt` 없음 → 초기 실행.
2. 있으면 `00_manifest.json`을 읽고:
   - **특정 워커 재작업**("IO 루프 다시") → 같은 run_dir. 브리프·계약 해시가 같을 때만 해당 워커 재호출(감독자 경유). **재작업 후에는 빌드 게이트·감사(`_rN`)·04 문서를 반드시 다시 만든다**(옛 APPROVE가 새 코드에 붙지 않도록). 해시 상이 → 새 실행.
   - 새 요구사항 → 새 run_id.

### Phase 1: 설계 브리프
사용자 입력으로 `00_design_brief.md` 작성. **메시지 최대 크기는 필수**(백프레셔 하드 제약의 입력). 부족하면 프로토콜·처리량·최대 크기만 질문한다.
```markdown
# Pipeline 설계 브리프
## 서버 요구사항: 프로토콜 / 동시 연결 / 처리량(msg/s) / 메시지 최대 크기(KB, 필수) / 헤더 형식
## 성능 목표: p99 레이턴시 / 연결당 메모리 / GC 일시정지 허용
## 종료·오류 정책: 연결 끊김 시 잔여 프레임 처리, 서버 종료 시 drain 여부, 핸들러 예외 정책
## 제약: net10.0, 디스패처 범위(서버 공유 / 연결별), 특이 사항
```
`brief_sha256`을 manifest에 기록.

### Phase 2: 감독자 호출

```
Agent(subagent_type="pipeline-supervisor", description="Pipeline design supervision",
      prompt="당신은 파이프라인 설계 팀의 감독자입니다. 프로젝트 루트는 현재 작업 디렉토리입니다. run_dir={run_dir}.
              {run_dir}/00_design_brief.md 를 읽고 에이전트 정의의 절차대로:
              1) 계약 템플릿(에이전트 정의)에 따라 {run_dir}/02_interface_contract.cs 를 확정(브리프의 최대 프레임으로 PauseWriterThreshold ≥ MaxFrame+Header 를 만족시킬 것).
              2) 단일 메시지에서 Agent 로 io-loop-designer 와 thread-dispatcher-designer 를 동시에 호출(계약은 불변 입력, deviation 은 최종 응답으로만 수집).
              3) 두 최종 응답 JSON 을 검증하고 deviation 이 있으면 계약 v2 로 갱신 후 두 워커를 1회 재호출(최대 1회).
              4) {run_dir}/build/Pipeline.csproj 를 만들고 dotnet build 를 실행해 경고 0·오류 0 을 확인(실패 시 해당 워커에게 컴파일 오류 원문으로 1회 재호출).
              5) 품질 게이트 체크리스트(CLAUDE.md 주석 규칙 포함) 통과 후 Agent 로 load-test-auditor 호출.
              6) 감사 verdict 가 BLOCK 또는 REQUEST CHANGES 면 해당 워커 1회 재작업 → 빌드 → 재감사(_r2). 총 재작업 상한: 워커별 1회, 합계 2회.
              7) {run_dir}/04_pipeline_architecture.md(ADR 포함) 와 00_manifest.json 작성.
              프로젝트 소스는 수정하지 마세요. SendMessage 사용 금지. 워커에게 대기·협상을 요구하지 마세요.
              최종 응답 첫 줄: {\"status\":\"done|failed\",\"verdict\":\"APPROVE|REQUEST CHANGES|BLOCK|null\",\"audit_file\":\"…\",\"build\":{\"ok\":true,\"warnings\":0,\"errors\":0},\"rework_count\":N,\"artifacts\":[…],\"manifest\":\"…\"}")
```

### Phase 3: 오케스트레이터 독립 검증
1. `artifacts`의 파일이 모두 존재하고 비어 있지 않은지 확인. `00_manifest.json`의 sha256과 실제 파일 해시 대조.
2. **빌드 재실행:** `dotnet build {run_dir}/build/Pipeline.csproj -nologo -v q` → 경고 0·오류 0이 아니면 감독자 보고를 기각하고 `failed`.
3. `03_load_test_audit[_rN].json`을 Read해 `verdict`·`counts`·`score`를 감독자 보고와 대조. 불일치 → 감사 JSON을 정본으로 채택하고 리포트에 명시.
4. 감사 JSON 구조 검증: `verdict ∈ {APPROVE, REQUEST CHANGES, BLOCK}`, `findings[]`(id·severity·area·file·detail·fix), `inputs_complete: true`. `inputs_complete:false`면 판정 무효 → 감독자 1회 재호출.
5. 최종 판정은 감사 JSON verdict. 미해결 BLOCK/RC가 남은 채 재작업 상한에 도달했으면 그대로 보고(APPROVE로 바꾸지 않는다).

### Phase 4: 보고
run_dir 보존. `04_pipeline_architecture.md` 요약 + 감사 판정 + 빌드 결과 + 재작업 횟수 + 경로 안내.

---

## 에러 핸들링

| 상황 | 처리 |
|------|------|
| 브리프에 최대 프레임 크기 없음 | 질문 후 진행(추정 금지) |
| 감독자 실패/JSON 없음 | 1회 재호출(프롬프트에 "기존 산출물 검사 후 미완 단계부터 재개") → 재실패 시 실패 보고 |
| 워커 실패 | 감독자가 1회 재호출 → 재실패 시 감독자가 직접 구현하지 **않고** `failed`로 보고(미완 부분 명시) |
| 빌드 실패 | 컴파일 오류 원문과 함께 해당 워커 1회 재호출 → 재실패 시 `failed` |
| 감사 BLOCK/RC | 워커 1회 재작업 → 빌드 → 재감사 `_r2`. 상한 도달 시 미해결 판정 그대로 보고 |
| 감사 입력 불완전 | 판정 무효, 감독자 1회 재호출 |
| 오케스트레이터 재검증 불일치 | 감사 JSON·실제 빌드 결과를 정본으로. 감독자 보고는 리포트에 "불일치" 표기 |
| 최종 응답 미수신 | 능동 타임아웃 없음. 사용자에게 알리고 지시 시 실패 확정. 미완 산출물을 감독자가 덮어쓰게 하지 않는다 |

## 테스트 시나리오
**정상:** "TCP 이진 프로토콜, 100k msg/s, 최대 프레임 64KB, 서버 공유 디스패처" → 계약(`ParsedMessage` = 풀 버퍼 소유 복사본, `IMessageDispatcher.DispatchAsync`, `PauseWriterThreshold = 2×(64KB+8)`) → 두 워커 병렬 → 빌드 0/0 → 감사 APPROVE(score 100) → 04 문서.
**BLOCK 흐름:** IO 루프 초안이 입력 부족 시 `examined = consumed`로 스핀 → 감사 CRITICAL(`examined-spin`) → io-loop-designer 재작업 → 빌드 → `_r2` APPROVE → 04 문서에 "초안 스핀 결함 → 수정" 기록.
**실패 흐름:** 재작업 후에도 빌드 오류 → `failed`, 컴파일 오류 원문 보고.
