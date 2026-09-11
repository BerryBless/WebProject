---
name: tdd-orchestrator
description: "TDD(테스트 주도 개발) 하네스를 실행하는 오케스트레이터. 사용자 요구사항 입력 시 analyst(Red)→builder(Green)→qa(Refactor) 파이프라인을 에이전트 팀으로 조율하고, 최종에 harness-evolve로 진화 델타를 포착한다. 트리거: 'TDD 해줘', '테스트 먼저 작성', 'Red-Green-Refactor', '기능 구현해줘 (TDD)', 'TDD 사이클', '테스트 주도 개발'. 후속: '다음 기능 TDD', '테스트 추가해줘', 'TDD 재실행', '리팩토링 가이드', '진화 리포트 보여줘'."
---

# TDD Orchestrator

Red-Green-Refactor 사이클을 에이전트 팀으로 조율하는 TDD 하네스 오케스트레이터.

## 실행 모드: 순차 Agent 호출 (파이프라인 + 생성-검증 혼합)

```
analyst ──→ builder ──→ qa
(Red)       (Green)    (Refactor)
                ↑──────── 재작업 (최대 2회)
```

## 에이전트 구성

| 팀원 | 역할 | 스킬 | 핵심 출력 |
|------|------|------|---------|
| tdd-analyst | 실패 테스트 설계 (Red) | /tdd-red-phase | Tests/*.cs + Stub/*.cs |
| tdd-builder | 최소 구현 (Green) | /tdd-green-phase | Src/*.cs |
| tdd-qa | 런타임 검증 + Refactor | /tdd-refactor-phase | test_results.txt + refactor_guide.md |

---

## 워크플로우

### Phase 0: 컨텍스트 확인

1. `_workspace/` 존재 여부 확인
2. 분기:
   - **미존재** → 초기 실행. Phase 1 진행
   - **존재 + 새 기능 요청** → 새 TDD 사이클: 기존 `_workspace/`를 `_workspace_{YYYYMMDD_HHMMSS}/`로 이동
   - **존재 + "다음 기능"/"테스트 추가"** → 누적 실행: 기존 테스트 보존하며 신규 추가
   - **존재 + "리팩토링만"** → qa 단독 재실행

### Phase 1: 프로젝트 환경 설정

`_workspace/` 하위에 xUnit 테스트 프로젝트를 생성한다:

```bash
cd "$CLAUDE_PROJECT_DIR" && mkdir -p _workspace/{01_analyst/Tests,01_analyst/Src,02_builder/Src,03_qa/Src,03_qa,04_evolution}
```

`_workspace/TddSession.csproj` 생성 (처음 실행 시만):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <RootNamespace>TddSession</RootNamespace>
    <!-- SDK 기본 **/*.cs 글로빙을 끈다: 아래 명시 Compile 항목과 중복(NETSDK1022)되고,
         _workspace/ 에 공존하는 다른 하네스 산출물(.cs)까지 컴파일되는 것을 막는다 -->
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.0" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>
  <!-- 분석가 테스트 + 스텁 -->
  <ItemGroup>
    <Compile Include="01_analyst/Tests/**/*.cs" />
    <Compile Include="01_analyst/Src/**/*.cs" />
  </ItemGroup>
  <!-- 단계별 산출물 존재 여부 탐지: 빈 디렉터리만 있어도 Exists()가 참이 되므로
       실제 .cs 파일 유무를 아이템 글로빙으로 판정한다 -->
  <ItemGroup>
    <_BuilderSrc Include="02_builder/Src/**/*.cs" />
    <_QaSrc Include="03_qa/Src/**/*.cs" />
  </ItemGroup>
  <!-- builder 구현 (있으면 스텁 대체) -->
  <ItemGroup Condition="'@(_BuilderSrc)' != ''">
    <Compile Remove="01_analyst/Src/**/*.cs" />
    <Compile Include="02_builder/Src/**/*.cs" />
  </ItemGroup>
  <!-- qa 리팩토링 코드 (있으면 builder·스텁 대체) -->
  <ItemGroup Condition="'@(_QaSrc)' != ''">
    <Compile Remove="01_analyst/Src/**/*.cs" />
    <Compile Remove="02_builder/Src/**/*.cs" />
    <Compile Include="03_qa/Src/**/*.cs" />
  </ItemGroup>
</Project>
```

요구사항을 `_workspace/00_requirements.md`에 저장한다.

### Phase 2: 실행 규칙

**공통 실행 규칙 (이 빌드에는 TeamCreate/TaskCreate/TaskGet/TeamDelete 팀 도구가 없다):**
- 병렬 실행이 필요한 에이전트는 **한 메시지 안에서 `Agent` 도구를 여러 번 호출**해 동시에 띄운다.
- 각 프롬프트에 프로젝트 루트, 입력 파일, 출력 파일 경로, "완료 시 severity별 건수·점수를 한 줄로 보고"를 명시한다.
- 완료는 **task-notification(완료 알림)** 으로 수신한다. 후속 지시가 필요하면 `SendMessage(to=<agentId>)` 로 보낸다.
- 순차 의존 단계는 앞 단계의 완료 알림을 받은 뒤 다음 `Agent` 를 호출한다.
- 에이전트 1개 실패 시 동일 프롬프트로 1회 재호출, 재실패 시 해당 도메인을 "수집 실패"로 표기하고 계속한다.

TDD 파이프라인은 **순차**이므로 각 단계는 앞 단계의 완료 알림 수신 후 호출한다.

### Phase 3: 파이프라인 실행 (순차 Agent 호출 + 생성-검증 루프)

**Step 1 — Red:**
```
Agent(subagent_type="tdd-analyst", description="TDD red phase",
      prompt="당신은 TDD 분석가입니다. 프로젝트 루트는 {project_root} 입니다.
              tdd-red-phase 스킬을 사용하여 _workspace/00_requirements.md 를 읽고 실패하는 xUnit 테스트와 스텁을
              _workspace/01_analyst/ 에 작성하세요. 완료 후 설계한 테스트 수를 한 줄로 보고하세요.")
```

**Step 2 — Green** (analyst 완료 알림 후):
```
Agent(subagent_type="tdd-builder", description="TDD green phase",
      prompt="당신은 TDD 구현자입니다. tdd-green-phase 스킬을 사용하여 _workspace/01_analyst/ 의 테스트를 통과시키는
              최소 구현을 _workspace/02_builder/Src/ 에 작성하세요. 과잉 구현 금지.")
```

**Step 3 — Refactor/검증** (builder 완료 알림 후):
```
Agent(subagent_type="tdd-qa", description="TDD refactor phase",
      prompt="당신은 TDD QA입니다. tdd-refactor-phase 스킬을 사용하여 _workspace/TddSession.csproj 로 dotnet test 를
              실제 실행하고 결과를 _workspace/03_qa/ 에 기록하세요. PASS/FAIL 판정과 실패 테스트 목록을 한 줄로 보고하세요.")
```

**생성-검증 루프 규칙:**
- qa FAIL → 실패 테스트 목록과 함께 tdd-builder 를 재호출 (최대 2회), 이어서 tdd-qa 재호출
- 2회 초과 FAIL: 사용자에게 에스컬레이션 → analyst 테스트 재설계 또는 요구사항 재확인

**리더 모니터링:**
- 각 단계 완료 알림의 한 줄 요약을 기록 (테스트 수, 시도 횟수, PASS/FAIL)
- 완료 알림이 10분+ 없으면 SendMessage 로 진행 상태 확인

### Phase 4: harness-evolve 실행

qa PASS 판정 후 `/harness-evolve` 스킬을 직접 실행한다:
- `_workspace/` 전체를 읽어 진화 델타를 포착
- `_workspace/04_evolution/evolution_report.md` 생성

### Phase 5: 정리

1. 별도 팀 해제 절차 없음
2. `_workspace/` 보존 (다음 TDD 사이클의 회귀 테스트로 사용)
3. 결과 요약 보고:
   - Red 단계: N개 테스트 설계
   - Green 단계: N회 시도 (재작업 N회)
   - Refactor 단계: N개 개선 제안
   - 진화 델타: 핵심 발견 N개

---

## 산출물 구조

```
_workspace/
├── TddSession.csproj               ← xUnit 테스트 프로젝트
├── 00_requirements.md              ← 사용자 요구사항
├── 01_analyst/
│   ├── test_design.md              ← 테스트 설계 근거
│   ├── Tests/<Feature>Tests.cs     ← Red: 실패하는 테스트
│   └── Src/<Feature>.cs            ← 컴파일용 스텁
├── 02_builder/
│   ├── build_notes.md              ← 구현 결정 기록
│   └── Src/<Feature>.cs            ← Green: 최소 구현
├── 03_qa/
│   ├── test_results.txt            ← dotnet test 실행 결과
│   ├── refactor_guide.md           ← 리팩토링 가이드
│   └── Src/<Feature>.cs            ← Refactor: 개선된 코드 (선택)
└── 04_evolution/
    └── evolution_report.md         ← 진화 델타 리포트
```

---

## 에러 핸들링

| 상황 | 처리 |
|------|------|
| 요구사항 불명확 | analyst가 질문 목록 전달 → 오케스트레이터가 사용자에게 질문 |
| builder 2회 재작업 후 FAIL | 오케스트레이터가 analyst에게 테스트 재설계 지시 또는 사용자에게 에스컬레이션 |
| dotnet test 환경 오류 | 패키지 복원 실행 후 재시도: `dotnet restore _workspace/TddSession.csproj` |
| 빌드 오류 (네임스페이스 충돌 등) | .csproj의 Compile 항목 조정 |

---

## 테스트 시나리오

### 정상 흐름
1. 사용자: "정수 덧셈·나눗셈 Calculator 클래스를 TDD로 구현해줘"
2. Phase 1: TddSession.csproj 생성, 요구사항 저장
3. Red: analyst가 4개 테스트 + 스텁 작성 (Add 2개, Divide 2개)
4. Green: builder가 Add/Divide 최소 구현 (Divide는 0 체크 포함)
5. Refactor: qa가 `dotnet test` → 4/4 통과 → 매직 넘버 없음 확인 → PASS
6. harness-evolve: "나눗셈 0 처리"가 암묵적 요구사항으로 발견된 것 기록
7. 결과: 모든 산출물 + 진화 리포트

### 에러 흐름 (Green 실패)
1. builder가 Divide 구현에서 0 체크 누락
2. qa: `DivideByZeroTest` FAIL → builder에게 "b==0 처리 필요" 피드백
3. builder 재작업: `if (b == 0) throw new DivideByZeroException()` 추가
4. qa 재실행: 4/4 통과 → PASS
5. harness-evolve: "1회 재작업 후 Green" 기록
