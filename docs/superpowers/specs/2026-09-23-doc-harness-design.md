# Documentation Harness 설계 (2026-09-23, 승인됨)

원 개발자가 없어도 신규 개발자가 이해·실행·디버깅·수정·확장할 수 있는 기술 문서를 **다단계 파이프라인**으로 생성하고, 이후 개발이 이어지는 동안 `문서화` 한마디로 **변경분만 증분 갱신**하는 시스템. 코드가 Source of Truth이며, 모든 다이어그램은 Mermaid로 관련 Markdown 안에 원본 그대로 존재한다.

관련: 요구 원문은 사용자 프롬프트 3회분(파이프라인·증분·Diagram 규칙). 구현 계획은 `docs/superpowers/plans/2026-09-23-doc-harness.md`.

## 1. 조사로 확인한 환경 사실

| 항목 | 사실 |
|---|---|
| Claude CLI | `claude.exe` 2.1.280. `claude -p --output-format json --json-schema …`가 `structured_output`에 스키마 검증 JSON을 돌려준다(실측). 프롬프트는 stdin으로 받는다. `--max-turns` 없음, 상한은 `--max-budget-usd`. Claude Code 세션 안에서 중첩 실행 가능(실측) |
| 훅 격리 | `--setting-sources user`로 프로젝트 `settings.json`의 Stop 훅(자동 커밋)·쓰기 범위 훅이 자식 세션에서 실행되지 않는다(실측). `--bare`는 API 키 전용이라 OAuth 환경에서 불가. 자식 세션은 저장소 루트 `CLAUDE.md`를 자동 로드한다(감수) |
| Mermaid 파서 | `mermaid` 12.0.0의 `parse()`가 `jsdom` 위에서 브라우저 없이 동작한다(정상 4종 통과, 타입·문법 오류 3종 검출, 실측) |
| 런타임 | Node 24.16, pwsh 7.6, Python 3.12, .NET 10, Codex 0.154 |
| 대상 규모 | 추적 파일 455, 커밋 49, 소스 약 21.5k줄(.cs 159, .ts/.tsx 50, .cshtml 11), 엔드포인트 파일 8, Razor 페이지 6, DbContext 2 |
| 기존 문서 | `docs/` 손으로 쓴 9개 페이지(README가 랜딩), `plan/*_report_*.md`에 단계별 결함·교훈 |
| 트리거 관습 | `CLAUDE.md`의 "하네스: …" 절이 키워드를 선언하고 `.claude/skills/<이름>/SKILL.md` `description`에 같은 키워드. `AGENTS.md`·`.agents/skills/` 미러 |

## 2. 원칙

1. 근거 우선순위: **실제 코드/설정 > Git 이력 > 테스트 > 기존 문서 > 대화 맥락 > 추론.**
2. 모든 주장은 `CONFIRMED | INFERRED | UNKNOWN | POSSIBLE_LEGACY | POTENTIAL_ISSUE` 중 하나의 상태와 `evidence[{file,symbol,lines,commit}]`를 가진다.
3. 에이전트는 **쓰기 도구를 갖지 않는다**(Read/Glob/Grep만). 파일은 하네스가 쓴다. 프로덕션 코드 수정 금지가 구조로 보장된다.
4. 문서가 Diagram의 원본이다. 이미지·별도 다이어그램 파일을 만들지 않는다.
5. Baseline은 Verification 성공 후에만 갱신한다. 실패한 Run은 정본을 건드리지 않는다.
6. 하네스는 단순하게: 재현 가능한 프롬프트 + 분리된 컨텍스트 + 구조화 중간 산출물 + 근거 + 검증.

## 3. 승인된 결정

| ID | 결정 | 값 |
|---|---|---|
| D1 | 하네스 언어 | TypeScript, Node 24, `tsx`로 빌드 없이 실행. 테스트 Vitest. `ajv`·`yaml`·`commander`·`mermaid`·`jsdom` |
| D2 | 문서 출력 위치 | `docs/generated/`. 기존 `docs/` 9개는 덮어쓰지 않고 최하위 근거로만 사용 |
| D3 | 문서 언어 | 본문 한국어, 파일명·ID·스키마 키 영어 |
| D4 | 에이전트 쓰기 권한 | 없음. 하네스가 모든 파일을 씀 |
| D5 | workspace 커밋 | `baseline.json`·`current/`·`depgraph.json`·`runs/*/run.json` 커밋. `runs/*/staging/`·`logs/`·`inbox/` 무시 |
| D6 | 모델 배분 | 02·03·04·I04·09 opus, 나머지 sonnet. `harness.yaml`에서 phase별 변경 |
| D7 | 트리거 매칭 | 메시지 전체가 `문서화`·`문서화 전체`·`문서화 상태`·`문서화 검증`일 때만 |
| D8 | 대화 맥락 | 스킬이 세션 요약을 `workspace/inbox/session_context.md`에 씀. 최하위 근거 |
| D9 | 장시간 실행 | 백그라운드 Bash + `.git/harness_commit_in_progress` 센티널. 끊기면 `resume` |
| D10 | 커밋 범위 | D5와 같음 |
| D11 | 삭제 기능 | `features.json`에 `status: REMOVED`로 보존, 현재 상태 문서에서만 제외 |
| D12 | 섹션 식별 | HTML 주석 앵커 `<!-- doc-harness:section id="…" hash="…" -->` … `<!-- /doc-harness:section -->` |
| D13 | 수동 수정 | 보존. 코드와 충돌이 검증으로 확인될 때만 교체하고 `run.json`에 기록 |
| D14 | Diagram 복잡도 | 노드 25·간선 40 초과 시 `DIAGRAM_TOO_COMPLEX`(수정 루프 대상) |
| D15 | 파서 실패 | 멈추지 않고 `MERMAID_RENDER_NOT_VERIFIED` 기록 |

## 4. 디렉터리

```text
doc-harness/
├── README.md · package.json · package-lock.json · tsconfig.json · vitest.config.ts
├── config/harness.yaml
├── prompts/
│   ├── 00_global_rules.md
│   ├── 01_inventory.md … 07_operations_{error,security,performance,debt}.md
│   ├── 08_doc_<이름>.md (서술 문서별) · 09_verification.md · 09_consistency.md
│   └── I02_classify.md · I04_feature_delta.md · I05_feature_reanalysis.md · I07_failure_delta.md · I08_diagram_update.md
├── schemas/  common.schema.json(+$defs) · inventory · architecture · features · feature · data · api
│             · failures · operations · verification · changes · classification · impact · feature_delta
│             · diagram_update · document(서술 문서 출력) · baseline · run · state
├── templates/ (템플릿 렌더 문서: feature.md, 07_DATA_MODEL.md, 08_API.md, 09_FEATURES.md,
│               11_FAILURE_HISTORY.md, 17_TECH_DEBT.md, 19_UNKNOWN_AND_TODO.md, 20_CHANGELOG.md, README.md)
├── src/
│   ├── cli.ts            run [--auto|--full|--phase <id>|--feature <id>] · status · verify · resume · clean · report
│   ├── mode.ts           INITIAL / INCREMENTAL / UP_TO_DATE 판정
│   ├── run.ts            Run 트랜잭션(staging → 검증 → 원자 교체 → baseline)
│   ├── state.ts          runs/run-NNNN/state.json, 항목 상태·inputHash·재시도
│   ├── baseline.ts       baseline.json 읽기/쓰기/fingerprint
│   ├── depgraph.ts       Source ↔ Feature ↔ API/Data ↔ Diagram ↔ Document
│   ├── claude.ts         CLI 어댑터(인터페이스 + 서브프로세스 구현), 로그, 재시도
│   ├── context.ts        Phase별 주입 컨텍스트 조립(경로 목록·발췌)
│   ├── git-extract.ts    이력·마커 추출(범위 지정 가능)
│   ├── change/detect.ts · classify.ts · impact.ts
│   ├── phases/           p01…p10, i01…i11 (scope 인자)
│   ├── render/           sections.ts(앵커·펜스 파서) · templates.ts · documents.ts(섹션 단위 교체)
│   ├── verify/           deterministic.ts · mermaid.ts · consistency.ts · llm.ts · loop.ts
│   └── report.ts
├── test/                 vitest(픽스처 저장소 포함)
├── workspace/
│   ├── baseline.json · depgraph.json
│   ├── current/  inventory.json · architecture.json · features.json · features/F###.json · data.json · api.json · failures.json · operations.json · verification.json
│   ├── inbox/session_context.md
│   └── runs/run-NNNN/  run.json · state.json · changes.json · classification.json · impact.json · feature_delta.json · report.txt · staging/{current,docs} · logs/
└── logs/ (없음 — Run별 logs/ 사용)
```

문서 출력 `docs/generated/`: `README.md`, `00_EXECUTIVE_SUMMARY.md` … `20_CHANGELOG.md`, `features/F###_<NAME>.md`, `adr/`(ADR 후보는 architecture·failures의 결정 항목에서 템플릿 렌더). `diagrams/`는 없다.

## 5. Claude 호출 계약

```text
claude -p --output-format json --json-schema <schemas/x.schema.json 내용>
  --tools Read,Glob,Grep --setting-sources user --no-session-persistence
  --permission-prompts none --model <phase별> --max-budget-usd <phase별> --effort <phase별>
stdin = 00_global_rules.md + phase prompt + 주입 컨텍스트 (+ 이슈 목록, 재시도 시)
```

- 결과 `structured_output`을 하네스가 `ajv`로 재검증한다. 실패·타임아웃·예산 초과·`is_error`는 항목 단위로 최대 `retry.max_attempts`(3)회 재시도 후 FAILED.
- 호출마다 `runs/run-NNNN/logs/<항목>.log`에 시작/종료·cmd(프롬프트 해시)·`session_id`·비용·`stop_reason`·시도 횟수·오류 요약을 남긴다. 프롬프트 본문은 `logs/<항목>.prompt.md`에 별도 저장(비밀값 스캔 후).
- 컨텍스트는 파일 **경로 목록과 산출물 발췌**만 주입한다. 에이전트가 필요한 파일을 Read/Grep으로 읽는다.
- 분석 제외: `doc-harness/`, `_workspace/`, `docs/generated/`, `bin/`, `obj/`, `node_modules/`, `dist/`, `.git/`. `.claude/`·`scripts/`·`plan/`·`docs/`는 inventory에 "개발 도구·설계 문서"로 기록하고 기능 후보에서 제외.

## 6. 파이프라인

### 6.1 INITIAL (전체)

| Phase | 호출 | 입력 | 출력 | 모델 |
|---|---|---|---|---|
| 01 Inventory | 1 | 파일 트리(경로)·csproj/package.json·compose·CI 목록 | `inventory.json` | sonnet |
| 02 Architecture | 1 | inventory + Program.cs·DI·미들웨어 경로 | `architecture.json`(components·relations·runtime·storage·controlFlows·diagrams) | opus |
| 03 Feature Discovery | 1 | inventory + components + 엔드포인트·페이지·SPA 라우트 경로 | `features.json` | opus |
| 04 Feature Analysis | N | feature 항목 + relatedFiles + components 목록 | `features/F###.json` | opus |
| 05 Data / API | 2 | (a) DbContext·엔티티·마이그레이션 (b) 엔드포인트 파일 + features 요약 | `data.json`(erd 포함) · `api.json` | sonnet |
| 06 Failure History | 1 | `git-extract` 결과 + `plan/*_report_*.md` 경로(최하위 근거) | `failures.json` | sonnet |
| 07 Cross-Cutting | 4 | 영역별 관련 경로 + 04의 failurePoints 집계 | `operations.json` | sonnet |
| 08 Documentation | 서술 문서 수 | 문서별 선언된 `inputs` | `staging/docs/**` | sonnet |
| 09 Verification | 결정적 + 문서군별 1 (×≤3) | 문서 + workspace + 코드 정답 목록 | `verification.json` | opus |
| 10 Summary | 0 | 전체 | `report.txt` · `docs/generated/README.md` · baseline 생성 | 없음 |

INITIAL은 "모든 파일이 ADDED인 INCREMENTAL"이다. phase 모듈은 `scope`(ALL 또는 영향 집합)를 받는다.

### 6.2 INCREMENTAL

| 단계 | 방식 | LLM | 산출물 |
|---|---|---|---|
| I01 Change Detection | 결정적(해시 + git) | 0 | `changes.json` |
| I02 Classification | 변경 파일·hunk(상한)·features 요약 → 다중 분류 + `significance` + `possibleFeatures` | 1 | `classification.json` |
| I03 Impact | depgraph 확장 + I02 병합, 불확실하면 확대(`widenReason`) | 0 | `impact.json` |
| I04 Feature Delta | 기존 features + 변경 파일 + I02 → 신규 ID 할당·삭제 처분·변경 목록 | 1 | `feature_delta.json` |
| I05 Targeted Re-analysis | Phase 04 모듈, 이전 `F###.json`을 "재검증 대상"으로 주입 | 영향 기능 수 | 새 `F###.json` + `history[]` |
| I06 Arch/Data/API | 분류·impact가 요구할 때만 Phase 02/05 모듈 | 0~3 | 갱신 JSON |
| I07 Failure/Decision | `git-extract`를 `baselineCommit..HEAD` + working tree + session_context로 한정, 기존 ID 목록 주고 append만 | 1 | failures 추가·troubleshooting 후보·changelog 항목 |
| I08 Update Docs | 입력 해시가 바뀐 문서·섹션만. 템플릿은 항상 재렌더(비용 0), 서술은 입력 변경분만 LLM. Diagram은 6.5 | 0~10 | `staging/docs` |
| I09 Consistency | 결정적 색인 대조 + LLM 1회(변경 문서 + 참조되는 미변경 문서) | 1 | 이슈 → I08 재실행 |
| I10 Verification | Phase 09(결정적은 전체, LLM은 변경 문서군), ≤3회 | 1~6 | `verification.json` |
| I11 Commit Run | 원자 교체, baseline·depgraph·run.json, 리포트 | 0 | `report.txt` |

변경이 없으면 I01에서 "문서화 확인 완료" 리포트만 내고 종료한다.

### 6.3 모드 판정 (`run --auto`)

- INITIAL: `baseline.json` 없음, `current/` 없음, `baselineCommit`이 저장소에 없음(`git cat-file -e`), 또는 `--full`.
- UP_TO_DATE: 변경 파일 0.
- 그 외 INCREMENTAL.

### 6.4 Change Detection

1. 분석 대상 파일 전체의 내용 해시를 `baseline.files`와 비교 → ADDED/MODIFIED/DELETED. 같은 해시가 다른 경로에 나타나면 RENAMED. `git ls-files --others --exclude-standard`에 있으면 UNTRACKED 플래그.
2. `git diff --name-status -M <baselineCommit>` + `git diff --cached` + `git status --porcelain`으로 교차 확인, 파일별 hunk 추출(untracked는 전체가 추가 줄).
3. 정규식으로 hunk의 C#/TS 심볼·엔드포인트 문자열을 `relatedSymbols`에 넣는다.
4. `requiresAnalysis`: 문서·테스트 전용 변경은 false. CONFIG_CHANGE는 `05_CONFIGURATION` 갱신 대상.

### 6.5 Diagram

- 구조: `{id, type, title, summary, mermaid, details, nodes[{node, code, symbol}], status}`. 템플릿이 **결론 → Mermaid → 상세 → 코드 근거** 순서를 강제하고 `nodes`가 코드 근거 표가 된다.
- 선택 기준(Phase 04 프롬프트): 여러 컴포넌트 호출→Sequence, 분기→Flowchart, 데이터 이동→Data Flow, 상태→State, DB 관계→ER, 객체 관계→Class. 필요한 것만.
- 이름 규칙: 실제 클래스·파일명. `A1`·`B23`형 ID, `style`·`classDef`·`linkStyle`·`%%{init`·HTML 라벨 금지. 프롬프트와 린트 양쪽에서 강제.
- 증분: `impact.affectedDiagrams`만 대상. 문서에서 현재 Mermaid를 읽어 [기존 Mermaid + 코드 경로 + hunk + 새 흐름]을 주고 `{decision: UNCHANGED|UPDATED, mermaid, changedEdges}`를 받는다. UNCHANGED는 바이트 그대로 유지.
- 수동 수정: 앵커 `hash` ≠ 현재 섹션 해시면 `MANUAL_EDIT`. 검증 통과 시 보존하고 해시만 갱신. `INCORRECT_DIAGRAM_RELATION`·`DIAGRAM_NODE_NOT_IN_CODE`면 교체하고 `run.json.manualEditsOverridden[]`에 기록.

### 6.6 문서 갱신 규칙

- 렌더러는 관리 섹션을 앵커로 감싼다. 앵커 밖 텍스트는 건드리지 않는다. 사람이 앵커를 지운 섹션은 "관리 밖"으로 보고 덮어쓰지 않는다.
- `11_FAILURE_HISTORY`·`20_CHANGELOG`는 append-only 누적. `12_TROUBLESHOOTING`은 failures의 `recurrenceProcedure`가 있는 항목과 I07의 troubleshooting 후보로 갱신(증상→원인→확인→해결→관련 코드).
- 기능 문서 `## 변경 이력`은 `history[]`의 MAJOR/MINOR만 렌더(TRIVIAL 제외).
- 삭제 기능은 `09_FEATURES`·현재 문서에서 빠지고 `20_CHANGELOG`와 기능 문서 이력에 남는다.

## 7. Baseline · Depgraph · Run

```json
{
  "documentationVersion": 3, "lastSuccessfulRun": "run-0003", "lastSuccessfulAt": "…",
  "baselineCommit": "abc123", "workingTreeFingerprint": "sha256(sorted [path,hash])",
  "files":     { "<path>": { "hash": "…", "features": ["F004"], "documents": ["08_API.md"], "diagrams": ["F004_SEQUENCE"] } },
  "features":  { "F004": { "analysisHash": "…", "sourceHash": "…", "analyzedAt": "…", "status": "ACTIVE|DEPRECATED|REMOVED" } },
  "documents": { "02_ARCHITECTURE.md": { "hash": "…", "inputsHash": "…", "sections": { "<id>": "<hash>" } } },
  "diagrams":  { "F004_SEQUENCE": { "document": "features/F004_….md", "hash": "…" } }
}
```

`depgraph.json`은 LLM이 아니라 하네스가 workspace의 evidence에서 유도한다(file→feature: `F###.json` evidence·relatedCode; feature→api/data: `api.json.featureIds`·`databaseAccess`; feature→diagram: diagram id; →document: 문서별 `inputs` 선언 + 본문 참조). 매 Run의 I11에서 재유도.

`runs/run-NNNN/run.json`: 스펙의 필드(run·mode·startedAt·completedAt·baselineBefore/After·changedFiles·affectedFeatures·newFeatures·removedFeatures·updatedDocuments·updatedDiagrams·failuresDiscovered·verification) + `manualEditsOverridden` + `cost` + `status`.

## 8. Verification

1. **결정적(전체 문서, 비용 0)**: 파일 경로·심볼·엔드포인트·엔티티·`F###`·내부 링크 실재. Mermaid 3층(구조·`mermaid.parse`·규칙 린트) + `nodes[].code` 실재 + sequence participant ⊆ `executionFlow` 구성요소.
2. **교차 일관성 색인(결정적)**: 08_API 엔드포인트 집합 = api.json = 기능 문서 합집합; 07_DATA_MODEL 엔티티 = data.json; 02_ARCHITECTURE 컴포넌트 = architecture.json.
3. **LLM 일관성(I09)**: 변경 문서 + 참조되는 미변경 문서. 구조·응답·DTO·순서 모순만 묻는다.
4. **LLM 검증자(09/I10)**: 새 컨텍스트, "공격적 리뷰어". 문서군 + 코드에서 추출한 정답 목록(엔드포인트·엔티티·설정 키) + 소스. Hallucination·Missing·관계 오류·Diagram 관계·근거 없는 주장·실행 절차 실재.

결과 `verification.json`: `score{coverage,accuracy}`(내부 지표), `hallucinations`, `missingItems`, `incorrectRelations`, `diagramIssues[{document,diagram,type,description,evidence}]`, `unsupportedClaims`, `fixRequired[{doc,section?,issue,evidence}]`, `mermaidParser: VERIFIED|MERMAID_RENDER_NOT_VERIFIED`.

수정 루프: `fixRequired`를 문서·섹션별로 묶어 해당 섹션만 이슈 목록을 주입해 재생성 → 결정적 검사 → LLM 재검증. `verification.max_iterations`(3) 초과 시 Run FAILED.

## 9. 실패 · 복구 · 원자성

| 상황 | 처리 |
|---|---|
| LLM 호출 실패 | 항목 재시도 3회 → FAILED, state 기록, Run 중단 |
| 검증 3회 초과 | Run FAILED. staging 보존, `report.txt`에 남은 이슈. 정본·baseline 무변경 |
| 크래시·세션 종료 | RUNNING 항목은 `resume`에서 재실행. 결과 파일은 tmp 후 rename |
| `resume` | 최근 미완료 Run 재개. 현재 fingerprint ≠ Run의 fingerprint면 이전 Run을 ABANDONED로 표시하고 새 Run |
| 정본 교체 | `current/`·`docs/generated/` → `*.prev` rename → staging rename → prev 삭제. 중간에 죽으면 `resume`이 prev 유무로 롤포워드/롤백 |
| Stop 훅 | 스킬이 센티널로 실행 중 자동 커밋을 막고 완료 후 한 번에 커밋 |

## 10. 트리거

- `CLAUDE.md`·`AGENTS.md` 새 절 "하네스: 문서화". 트리거 D7. 되묻지 않는다.
- `.claude/skills/doc-harness/SKILL.md`(+ `.agents/skills/` 미러): (1) `node_modules` 없으면 `npm ci` (2) 세션 요약을 `inbox/session_context.md`에 (3) 센티널 생성 (4) `npx tsx src/cli.ts run --auto` 백그라운드 (5) 완료 시 `report.txt` 그대로 출력, 센티널 삭제, 커밋 메시지 파일.
- `문서화 전체`→`run --full`, `문서화 상태`→`status`(LLM 없음), `문서화 검증`→`verify`.
- `harness-audit.ps1` PASS 유지.

## 11. 예상 문제와 대응

| 문제 | 대응 |
|---|---|
| Windows 명령줄 길이 | 프롬프트 stdin, 스키마는 파일에서 읽어 인자로 |
| 출력 64k 토큰 상한 | 문서·기능·섹션 단위 분할 호출 |
| JSON 불량 | CLI 스키마 + ajv 이중 검증, 재시도 |
| 자식 세션 훅 | `--setting-sources user` 고정, 자동 테스트로 `.git/auto_commit_msg.txt` 미생성 확인 |
| 비용·시간 | INITIAL 60~80회·1~2시간, INCREMENTAL 6~10회. phase별 예산 상한, state에 누적 비용 |
| Git 이력 | 커밋당 diff 상한 40KB, 유형 필터 |
| 기능 수 폭증 | "클래스≠기능" 규칙, 상한 30, `importance` |
| CLAUDE.md 자동 로드 | 감수. 프롬프트에 "CLAUDE.md의 커밋 지시 무시" 명시 |

## 12. 범위 밖

Mermaid 이미지 렌더, Claude Agent SDK 전환(어댑터로 자리만), 기능 분석 병렬화 기본 활성(구조만 준비, `feature_parallelism: 1`), 기존 `docs/` 9개 페이지의 자동 갱신.
