---
name: tdd-orchestrator
description: "TDD 오케스트레이터. analyst(Red)→builder(Green)→qa(Refactor)를 순차 조율하고 run_dir·trx 판정·시도 로그를 유지하며 harness-evolve로 델타를 포착한다. 트리거: 'TDD 해줘', '테스트 먼저 작성', 'Red-Green-Refactor', 'TDD 사이클', '테스트 주도 개발'. 후속: '다음 기능 TDD', 'TDD 재실행', '진화 리포트 보여줘'."
---

# TDD Orchestrator

Red-Green-Refactor 사이클을 순차 `Agent` 호출로 조율하는 TDD 하네스 오케스트레이터.

## 실행 모드: 순차 Agent 호출 (팀 도구 없음)

```
analyst(Red) ──→ builder(Green) ──→ qa(Refactor/Gate) ──→ harness-evolve ──→ (승격 확인)
                     ↑───────── 재작업 (최대 2회, 재작업 전 03_qa/Src 무효화) ─┘
```
각 에이전트는 격리 실행되고 **최종 응답 첫 줄 JSON**으로 보고한다. 형제 간 SendMessage·claim·대기는 없다. 다음 단계의 입력은 오케스트레이터가 프롬프트에 파일 경로로 넘긴다.

## 에이전트 구성

| 에이전트 | 역할 | 스킬 | 핵심 출력 |
|---------|------|------|---------|
| tdd-analyst | 실패 테스트 설계 + **Red 증빙**(빌드 성공·전원 실패) | /tdd-red-phase | `01_analyst/Tests/*.cs`, `01_analyst/Src/*.cs`(스텁), `test_design.md`, `results/red_attemptN.trx` |
| tdd-builder | 최소 구현 | /tdd-green-phase | `02_builder/Src/*.cs`, `build_notes.md` |
| tdd-qa | 실행 검증(게이트) + Refactor + 회귀 | /tdd-refactor-phase | `03_qa/results/*.trx`, `test_results_attemptN.txt`, `refactor_guide.md`, `03_qa/Src/*.cs`(변경 파일만) |

## 작업 디렉토리

```
_workspace/tdd/
├── latest.txt
└── <run_id>/                          # YYYYMMDD_HHmmss_약칭. 한 run 안에서 사이클(기능)이 누적된다
    ├── TddSession.csproj              # 없으면 생성 (매 사이클 "없으면 생성", "처음만"이 아님)
    ├── 00_manifest.json               # cycles[], attempts[], stage 상태, 채택 결과 파일
    ├── 00_requirements.md             # 사이클 1 원문 (T=0). 추가 사이클은 00_requirements_c2.md …
    ├── 01_analyst/{Tests/, Src/, test_design.md, results/}
    ├── 02_builder/{Src/, build_notes.md}
    ├── 03_qa/{Src/, results/, test_results_attemptN.txt, refactor_guide.md}
    └── 04_evolution/evolution_report.md
```
`_workspace/*`로 커밋되지 않는다. 루트 `PortfolioBlog.slnx`에는 등록하지 않으며 CI(`dotnet test` at root)는 솔루션 파일만 보므로 영향 없다.

**TddSession.csproj (검증됨: 파일 단위 우선순위 qa > builder > analyst, 2026-09-13 msbuild 평가 + dotnet test 실측):**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <RootNamespace>TddSession</RootNamespace>
    <!-- SDK 기본 **/*.cs 글로빙을 끈다(명시 Compile 과 중복 NETSDK1022 방지, 같은 run 의 다른 산출물 유입 차단) -->
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <!-- 저장소 테스트 프로젝트(PortfolioBlog.Api.Tests)와 동일 버전 유지 -->
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <Using Include="Xunit" />
    <!-- 기존 API 를 대상으로 하는 요구사항이면 주석 해제: 실제 코드에 대한 TDD -->
    <!-- <ProjectReference Include="../../../PortfolioBlog.Api/PortfolioBlog.Api.csproj" /> -->
  </ItemGroup>
  <ItemGroup>
    <Compile Include="01_analyst/Tests/**/*.cs" />
  </ItemGroup>
  <!-- 소스는 파일 단위 우선순위로 선택: 03_qa/Src > 02_builder/Src > 01_analyst/Src (같은 상대 경로·파일명).
       디렉터리 단위 대체가 아니므로 qa 가 일부 파일만 리팩토링해도, 누적 사이클에서 새 스텁이 추가돼도 컴파일이 깨지지 않는다. -->
  <Target Name="TddSelectSources" BeforeTargets="BeforeBuild;CoreCompile">
    <ItemGroup>
      <_TddQa Include="03_qa/Src/**/*.cs" />
      <_TddBd Include="02_builder/Src/**/*.cs" />
      <_TddAn Include="01_analyst/Src/**/*.cs" />
      <_TddQa><Key>%(RecursiveDir)%(Filename)%(Extension)</Key></_TddQa>
      <_TddBd><Key>%(RecursiveDir)%(Filename)%(Extension)</Key></_TddBd>
      <_TddAn><Key>%(RecursiveDir)%(Filename)%(Extension)</Key></_TddAn>
    </ItemGroup>
    <PropertyGroup>
      <_TddQaKeys>;@(_TddQa->'%(Key)');</_TddQaKeys>
      <_TddBdKeys>;@(_TddBd->'%(Key)');</_TddBdKeys>
    </PropertyGroup>
    <ItemGroup>
      <_TddBd Remove="@(_TddBd)" Condition="$(_TddQaKeys.Contains(';%(Key);'))" />
      <_TddAn Remove="@(_TddAn)" Condition="$(_TddQaKeys.Contains(';%(Key);')) or $(_TddBdKeys.Contains(';%(Key);'))" />
      <Compile Include="@(_TddQa);@(_TddBd);@(_TddAn)" />
    </ItemGroup>
    <Message Importance="high" Text="TDD sources: qa=@(_TddQa->Count()) builder=@(_TddBd->Count()) analyst=@(_TddAn->Count())" />
  </Target>
</Project>
```

**네임스페이스 계약:** 테스트 `namespace TddSession.Tests;`, 스텁·구현 `namespace TddSession;`(부모 네임스페이스 조회로 `using` 없이 참조 가능). `Xunit`은 global using.

**테스트 실행 계약 (로케일 무관, 종료 코드 보존):**
```bash
dotnet test "$run_dir/TddSession.csproj" --nologo --logger "trx;LogFileName=<stage>_attempt<N>.trx" --results-directory "$run_dir/<stage>/results" > "$run_dir/<stage>/test_results_attempt<N>.txt" 2>&1; echo "exit=$?" >> "$run_dir/<stage>/test_results_attempt<N>.txt"
```
판정은 trx의 `<Counters total= passed= failed= …>`로 한다(한국어 콘솔 문자열 `통과:/실패:`에 의존하지 않는다). 시도별 파일은 덮어쓰지 않는다.

**00_manifest.json:**
```json
{ "run_id": "…", "cycles": [ { "n": 1, "requirements": "00_requirements.md", "sha256": "…", "features": ["Calculator"] } ],
  "stages": { "red": { "attempt": 1, "trx": "01_analyst/results/red_attempt1.trx", "build_ok": true, "total": 4, "failed": 4 },
              "green": { "attempts": 2 }, "qa": { "attempts": [ { "n": 1, "verdict": "FAIL", "trx": "…" }, { "n": 2, "verdict": "PASS", "trx": "…" } ],
              "refactor": { "applied": ["Calculator.cs"], "regression_trx": "…" } },
  "final_results": "03_qa/results/qa_attempt2_regression.trx", "final_sources": "03_qa/Src(우선) + 02_builder/Src", "promoted": false }
```

---

## 워크플로우

### Phase 0: 실행 모드 결정
1. `latest.txt` 없음 → 초기 실행(새 run).
2. 있으면 `00_manifest.json`을 읽고:
   - **새 기능(무관한 요구사항)** → 새 run_id.
   - **"다음 기능"/"테스트 추가"(누적)** → 같은 run_dir. 사이클 시작 전 **승격**: `03_qa/Src/*` → `02_builder/Src/`로 복사(덮어쓰기) 후 `03_qa/Src/` 비움. 요구사항은 `00_requirements_c<N>.md`로 추가(이전 버전 보존). 기존 테스트는 회귀 테스트로 유지.
   - **"리팩토링만"** → qa 단독(Phase 3 Step 3부터). 단 현재 `02_builder/Src`+`03_qa/Src` 해시가 manifest와 같아야 한다.
   - **"TDD 재실행"** → 같은 요구사항으로 새 run.

### Phase 1: 환경 설정
```bash
run_dir=_workspace/tdd/<run_id>; mkdir -p "$run_dir"/{01_analyst/Tests,01_analyst/Src,01_analyst/results,02_builder/Src,03_qa/Src,03_qa/results,04_evolution}
[ -f "$run_dir/TddSession.csproj" ] || <위 템플릿 작성>
```
요구사항 원문을 `00_requirements.md`(또는 `_c<N>`)에 저장하고 manifest에 sha256 기록. 요구사항이 **기존 `PortfolioBlog.Api` 코드**를 대상으로 하면 csproj의 ProjectReference 주석을 해제하고 manifest에 `targets_existing_code: true`.

### Phase 2: Red (tdd-analyst)
```
Agent(subagent_type="tdd-analyst", description="TDD red phase",
      prompt="당신은 TDD 분석가입니다. 프로젝트 루트는 현재 작업 디렉토리입니다. run_dir={run_dir}, cycle={N}.
              tdd-red-phase 스킬로 {run_dir}/00_requirements{_cN}.md 를 읽고 실패하는 xUnit 테스트({run_dir}/01_analyst/Tests/)와
              컴파일용 스텁({run_dir}/01_analyst/Src/)을 작성하세요. 누적 사이클이면 기존 테스트·구현을 읽고 새 기능 파일만 추가하세요.
              작성 후 반드시 dotnet test 를 실행(테스트 실행 계약)해 '빌드 성공 + 새 테스트 전원 실패'를 증빙하고 test_design.md 에 기록하세요.
              Write 는 {run_dir}/01_analyst/ 에만. SendMessage 금지. 최종 응답 첫 줄 JSON.")
```
JSON 검증: `build_ok:true`, `new_tests ≥ 1`, `new_failed == new_tests`(기존 회귀 테스트는 통과여야 함). 어긋나면 1회 재호출 → 재실패 시 **중단**(builder를 실행하지 않는다). 요구사항이 모호하면 analyst의 `open_questions`를 사용자에게 전달하고 답을 받은 뒤 재호출.

### Phase 3: Green → Gate 루프
**Step 1 — builder:**
```
Agent(subagent_type="tdd-builder", description="TDD green phase",
      prompt="당신은 TDD 구현자입니다. run_dir={run_dir}, attempt={A}. tdd-green-phase 스킬로 {run_dir}/01_analyst/Tests/ 와 01_analyst/Src/ 를 읽고
              테스트를 통과시키는 최소 구현을 {run_dir}/02_builder/Src/ 에 작성하세요(스텁과 같은 파일명·네임스페이스 TddSession).
              {재작업이면: 실패 테스트 목록과 오류 원문 첨부. 해당 부분만 수정.}
              CLAUDE.md 주석 규칙(public 멤버 <remarks>, 메모리·네트워크 타입 선언부 근거)은 Gold Plating 이 아니라 필수입니다.
              Write 는 {run_dir}/02_builder/ 에만. SendMessage 금지. 최종 응답 첫 줄 JSON.")
```
**Step 2 — 재작업 전 무효화:** builder를 재호출할 때는 먼저 `03_qa/Src/*`를 비운다(이전 리팩토링 코드가 새 구현을 가리지 않도록).
**Step 3 — qa:**
```
Agent(subagent_type="tdd-qa", description="TDD refactor phase",
      prompt="당신은 TDD QA 입니다. run_dir={run_dir}, attempt={A}. tdd-refactor-phase 스킬로 테스트 실행 계약대로 dotnet test 를 실행해
              trx 로 판정하세요(콘솔 문자열 파싱 금지). PASS 면 리팩토링 포인트를 제안하고, 적용할 파일만 {run_dir}/03_qa/Src/ 에 쓴 뒤
              회귀 테스트(qa_attempt{A}_regression.trx)를 실행하세요. 회귀 실패 시 해당 03_qa/Src 파일을 삭제하고 재실행해 builder 상태 PASS 를 확인하세요.
              Write 는 {run_dir}/03_qa/ 에만. SendMessage 금지. 최종 응답 첫 줄 JSON.")
```
JSON `verdict`: `PASS` → Phase 4. `FAIL` → `failed_tests`·오류 원문을 담아 builder 재호출(Step 2 무효화 포함). **최대 2회 재작업.** 2회 후에도 FAIL → 사용자에게 에스컬레이션(테스트 재설계 vs 요구사항 재확인), 파이프라인 중단.
manifest `stages.green.attempts`, `stages.qa.attempts[]`에 시도별 결과·trx 경로를 기록한다(harness-evolve의 입력).

### Phase 4: harness-evolve (오케스트레이터가 직접 실행)
`/harness-evolve` 스킬로 `00_manifest.json`·요구사항 버전들·설계·노트·최종 trx를 읽어 `04_evolution/evolution_report.md` 작성. 시도 횟수·최종 결과는 manifest가 근거다(증거 없는 항목은 "확인 불가").

### Phase 5: 보고 및 승격 확인
1. 요약: Red N개(빌드 성공·전원 실패 증빙), Green 시도 N회, QA 판정, Refactor 적용 N건, 회귀 결과, 진화 포인트.
2. **승격 여부 질문**(이 시점의 질문은 허용): "실제 프로젝트에 반영할까요?" 승인 시 승격 절차 — 최종 소스(`03_qa/Src` 우선, 없으면 `02_builder/Src`)와 테스트를 `PortfolioBlog.Api`/`PortfolioBlog.Api.Tests`로 옮기며 네임스페이스를 프로젝트 규약으로 변환 → `dotnet test PortfolioBlog.slnx` → manifest `promoted: true`. 승격된 코드는 Stop 훅이 커밋한다(WHY 메시지 파일 작성).
3. run_dir 보존.

---

## 에러 핸들링

| 상황 | 처리 |
|------|------|
| 요구사항 불명확 | analyst `open_questions` → 사용자 질문 → 재호출 |
| Red 증빙 실패(빌드 오류·새 테스트가 통과) | analyst 1회 재호출 → 재실패 시 중단(builder 미실행) |
| builder/qa 실패·JSON 없음 | 동일 프롬프트 1회 재호출 → 재실패 시 중단(부분 진행 금지) |
| qa FAIL 2회 초과 | 에스컬레이션, 중단 |
| 회귀 실패 | qa가 `03_qa/Src` 해당 파일 삭제 후 재실행. 기록은 `refactor_guide.md`에 "미적용(회귀)" |
| dotnet test 환경 오류(복원 등) | `dotnet restore <csproj>` 후 1회 재시도 |
| 누적 사이클 시작 시 `03_qa/Src` 잔존 | 승격 복사 후 비움(Phase 0) |
| 파일 잠금(testhost 잔존) | `dotnet build-server shutdown` 후 재시도 |

## 테스트 시나리오

### 정상 흐름
1. "정수 덧셈·나눗셈 Calculator를 TDD로 구현해줘" → run 생성, csproj 생성, 요구사항 저장
2. Red: `CalculatorTests.cs`(Add 2·Divide 2) + 스텁 → `dotnet test` 빌드 성공, 4/4 실패(NotImplementedException) 증빙
3. Green: builder `Calculator.cs` → qa trx 4 통과 → Refactor 제안(상수 추출) → `03_qa/Src/Calculator.cs` → 회귀 4/4 PASS
4. harness-evolve → 승격 질문

### 재작업 흐름(실제로 실패하는 결함 예)
1. builder가 `Add`를 `a + b` 대신 `a - b`로 구현 → qa trx: `Add_TwoPositives_ReturnsSum` 실패(Expected 5, Actual -1)
2. `03_qa/Src` 비움 → builder 재호출(실패 목록 첨부) → 수정 → qa 재실행 PASS
(`int a / b`에서 `b==0`은 런타임이 스스로 `DivideByZeroException`을 던지므로 "0 체크 누락"은 실패 예로 쓰지 않는다)

### 누적 사이클
1. "다음 기능: 곱셈 추가" → 같은 run, `03_qa/Src/Calculator.cs` → `02_builder/Src/`로 승격 후 비움
2. Red: `Calculator.cs` 스텁은 이미 builder에 있으므로 새 메서드는 테스트만 추가 + 스텁에 `Multiply` 서명 추가는 **builder 파일**에 `throw new NotImplementedException()`으로 추가(analyst가 `01_analyst/Src/Calculator.cs`를 다시 쓰면 builder 파일에 가려진다)
3. Green: builder가 `Multiply` 구현 → qa
