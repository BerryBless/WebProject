# Documentation Harness(문서화 하네스) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `문서화` 한마디로 프로젝트 전체(INITIAL) 또는 마지막 baseline 이후 변경분(INCREMENTAL)을 다단계 Claude 파이프라인으로 분석해 `docs/generated/`의 두괄식 문서와 문서 내 Mermaid를 생성·검증·갱신하는 `doc-harness/`를 만든다.

**Architecture:** Node 24 + TypeScript(`tsx`) CLI. 각 분석 항목은 `claude -p --json-schema`로 띄운 **독립 읽기 전용 세션**이고, 파일은 하네스만 쓴다. 모든 실행은 `workspace/runs/run-NNNN/staging/`에서 만들고 Verification 통과 후에만 `workspace/current/`·`docs/generated/`·`baseline.json`으로 원자 교체한다. 변경 감지는 파일 해시 + git, 영향 분석은 evidence에서 유도한 depgraph, 문서 갱신은 HTML 주석 앵커로 감싼 섹션 단위다.

**Tech Stack:** TypeScript 5, Node 24, `tsx`, `vitest`, `ajv`(+`ajv-formats`), `yaml`, `commander`, `mermaid` 12 + `jsdom`(문법 검증), `claude` CLI 2.1.280.

**Spec:** `docs/superpowers/specs/2026-09-23-doc-harness-design.md` (절 번호를 §로 인용한다).

**범위 밖:** Mermaid 이미지 렌더, Agent SDK 전환, 기능 분석 병렬 기본 활성, 기존 `docs/` 9개 페이지 자동 갱신, 프로덕션 코드 수정 일체.

## Global Constraints

- **에이전트는 쓰기 도구를 갖지 않는다.** 모든 `claude` 호출은 `--tools Read,Glob,Grep --setting-sources user --no-session-persistence --permission-prompts none`을 고정한다(§5). 하네스가 파일을 쓴다.
- 하네스는 `doc-harness/**`와 `docs/generated/**`만 쓴다. 그 밖의 저장소 파일에 쓰는 코드 경로가 있으면 결함이다.
- 절대 경로 하드코딩 금지. 저장소 루트는 `config/harness.yaml`의 `project.root`(하네스 디렉터리 기준 상대)로 계산한다.
- 로그·프롬프트 파일에 비밀값을 남기지 않는다. 주입 컨텍스트에 `appsettings*.json`·`.env*`의 **내용**을 넣지 않는다(경로만).
- 정본(`workspace/current/`, `docs/generated/`, `baseline.json`)은 `commitRun()` 한 곳에서만 쓴다(§9).
- 출력 JSON은 CLI `--json-schema` + 하네스 `ajv` 이중 검증(§5). 재시도 최대 `retry.max_attempts`(3).
- 문서 본문 한국어, 파일명·ID·스키마 키 영어(D3). 문서 순서: 결론 → Mermaid → 상세 → 코드 근거(§6.5).
- Mermaid에 `style`·`classDef`·`linkStyle`·`%%{init`·HTML 라벨 금지, 노드 25·간선 40 초과 금지(D14). 별도 다이어그램 파일 금지.
- `.gitignore`: `doc-harness/node_modules/`, `doc-harness/workspace/runs/*/staging/`, `doc-harness/workspace/runs/*/logs/`, `doc-harness/workspace/inbox/`. **Task 1에서 코드보다 먼저** 추가한다(Stop 훅이 `git add -A`).
- 새 `.ts`·`.md`·`.json`은 LF, UTF-8(BOM 없음). 모든 `import`는 `.js` 확장자 포함(ESM, `"type": "module"`).
- 테스트는 `doc-harness/`에서 `npm test`(Vitest). 실제 `claude`를 부르는 테스트는 `test/integration/`에 두고 `DOC_HARNESS_LIVE=1`일 때만 돈다.
- 커밋 메시지: `{접두사}: {제목}`(접두사 추가/수정/버그수정/리팩토링/문서/테스트/의존성, 50자 이내, WHY 중심), 본문 `- ` 항목, 마지막 줄 `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`. 이 세션에서는 Stop 훅이 커밋하므로 각 Task 끝의 "Commit" 단계는 `.git/auto_commit_msg.txt` 작성으로 대체해도 된다.

## 스파이크 — 계획을 쓰기 전에 실제로 잰 것 (2026-09-23)

| # | 무엇을 쟀나 | 결과 → 반영 |
|---|---|---|
| S1 | `claude -p --output-format json --json-schema` | `structured_output`에 검증된 객체, `is_error`, `total_cost_usd`, `session_id`, `stop_reason`, `num_turns` 반환. `--tools ""`로도 구조화 출력 동작 |
| S2 | stdin 프롬프트 | `printf … \| claude -p …` 동작. Windows 명령줄 길이 문제 회피 |
| S3 | `--setting-sources user` | 프로젝트 Stop/PreToolUse 훅 미실행. 이 세션(Claude Code 안)에서 중첩 실행 성공 |
| S4 | `mermaid@12.0.0` + `jsdom` | `globalThis.window/document` 주입 후 `mermaid.parse()`가 flowchart·sequence·er·state 통과, 잘못된 타입·문법 3종 `Parse error` 검출 |
| S5 | `claude --help` | `--max-turns` 없음 → `--max-budget-usd`로 상한. `--effort <level>` 있음 |

## 설계 결정(승인됨, 스펙 §3 D1~D15)

TypeScript/`tsx` · `docs/generated/` · 한국어 본문 · 에이전트 무쓰기 · workspace 커밋 범위 · 모델 배분(02·03·04·I04·09 opus) · 트리거 정확 일치 · 세션 맥락 inbox · 백그라운드+센티널 · 삭제 기능 `REMOVED` 보존 · HTML 주석 앵커 · 수동 수정 보존 · 복잡도 25/40 · 파서 실패 시 `MERMAID_RENDER_NOT_VERIFIED`.

## 파일 구조

```text
doc-harness/
├── package.json · package-lock.json · tsconfig.json · vitest.config.ts · README.md
├── config/harness.yaml
├── prompts/*.md
├── schemas/*.schema.json
├── templates/*.md
├── src/
│   ├── types.ts          공통 타입(Status·Evidence·Claim·Diagram·Feature…)
│   ├── config.ts         harness.yaml 로드·경로 계산(HarnessPaths)
│   ├── fsx.ts            atomicWrite·sha256·readJson·listFiles(제외 규칙)
│   ├── schema.ts         ajv 검증기 validate(name, data)
│   ├── claude.ts         ClaudeRunner 인터페이스·CliClaudeRunner·FakeClaudeRunner
│   ├── prompts.ts        프롬프트 로드·치환·global rules 결합
│   ├── context.ts        파일 트리·발췌·정답 목록 추출(엔드포인트·엔티티·설정 키)
│   ├── git.ts            git 명령 래퍼
│   ├── git-extract.ts    이력·마커 추출(범위)
│   ├── state.ts          RunState(항목 상태·inputHash·attempts·cost)
│   ├── run.ts            Run 트랜잭션(create·commit·abandon·resume·prev 롤포워드)
│   ├── baseline.ts       Baseline 타입·load·save·fingerprint
│   ├── depgraph.ts       유도·조회
│   ├── mode.ts           decideMode
│   ├── change/detect.ts · classify.ts · impact.ts · featureDelta.ts
│   ├── phases/inventory.ts · architecture.ts · discovery.ts · feature.ts · dataApi.ts · failures.ts · operations.ts
│   ├── render/sections.ts · templates.ts · documents.ts · diagrams.ts
│   ├── verify/references.ts · mermaid.ts · consistency.ts · llm.ts · loop.ts
│   ├── pipeline.ts       INITIAL/INCREMENTAL 오케스트레이션
│   ├── report.ts
│   └── cli.ts
└── test/  unit/*.test.ts · fixtures/ · integration/*.live.test.ts
```

---

### Task 1: 골격·설정·공통 유틸

**Files:**
- Modify: `.gitignore`(끝에 블록 추가)
- Create: `doc-harness/package.json`, `tsconfig.json`, `vitest.config.ts`, `config/harness.yaml`, `src/types.ts`, `src/config.ts`, `src/fsx.ts`, `test/unit/config.test.ts`, `test/unit/fsx.test.ts`

**Interfaces:**
- Produces: `loadConfig(harnessDir?: string): HarnessConfig`, `resolvePaths(cfg): HarnessPaths { harnessDir, projectRoot, workspace, current, runs, inbox, docsOut, prompts, schemas, templates }`, `atomicWriteJson(path, data)`, `atomicWriteText(path, text)`, `sha256(text|Buffer): string`, `readJson<T>(path): T`, `listSourceFiles(root, cfg): string[]`(정규화된 `/` 경로, 제외 규칙 적용, 정렬), `hashFile(path)`.

- [ ] **Step 1: `.gitignore` 블록 추가**

```gitignore

# doc-harness(문서화 하네스) — 스테이징·로그·세션 인박스는 Run마다 생기는 임시물
doc-harness/node_modules/
doc-harness/workspace/runs/*/staging/
doc-harness/workspace/runs/*/logs/
doc-harness/workspace/inbox/
```

- [ ] **Step 2: `package.json`·`tsconfig.json`·`vitest.config.ts`**

```json
{
  "name": "doc-harness", "private": true, "type": "module",
  "scripts": { "harness": "tsx src/cli.ts", "test": "vitest run", "typecheck": "tsc --noEmit" },
  "dependencies": { "ajv": "^8.17.1", "ajv-formats": "^3.0.1", "commander": "^13.1.0", "jsdom": "^26.1.0", "mermaid": "^12.0.0", "yaml": "^2.8.0" },
  "devDependencies": { "@types/jsdom": "^21.1.7", "@types/node": "^24.0.0", "tsx": "^4.20.3", "typescript": "^5.9.2", "vitest": "^3.2.4" }
}
```
`npm install`로 실제 최신 patch를 lock에 고정한다. tsconfig: `module: NodeNext`, `target: ES2022`, `strict: true`, `moduleResolution: NodeNext`, `resolveJsonModule: true`, `include: ["src","test"]`.

- [ ] **Step 3: `config/harness.yaml`**

```yaml
project:
  root: ".."
  exclude: ["doc-harness/", "_workspace/", "docs/generated/", ".git/", "bin/", "obj/", "node_modules/", "dist/", "playwright-report/", "test-results/"]
  source_extensions: [".cs", ".cshtml", ".ts", ".tsx", ".css", ".json", ".yml", ".yaml", ".props", ".csproj", ".slnx", ".sh", ".ps1", ".toml", "Dockerfile", "Caddyfile", ".sql", ".md"]
  tooling_dirs: [".claude/", ".agents/", ".codex/", "scripts/", "plan/", "docs/", ".github/"]   # inventory에 기록, 기능 후보 제외
analysis: { feature_parallelism: 1, max_features: 30 }
retry: { max_attempts: 3 }
verification: { enabled: true, max_iterations: 3 }
git: { analyze_history: true, max_diff_bytes_per_commit: 40000, failure_keywords: ["fix","revert","retry","workaround","temporary","hack","fallback","broken","restore","rollback","버그수정","수정","되돌","임시","우회"] }
diagrams: { format: mermaid, max_nodes: 25, max_edges: 40 }
output: { docs: "../docs/generated", workspace: "./workspace" }
claude:
  bin: "claude"
  default: { model: "sonnet", budget_usd: 2.0, effort: "medium", timeout_sec: 900 }
  phases:
    architecture: { model: "opus", budget_usd: 4.0 }
    discovery:    { model: "opus", budget_usd: 4.0 }
    feature:      { model: "opus", budget_usd: 3.0 }
    feature_delta: { model: "opus", budget_usd: 3.0 }
    verification: { model: "opus", budget_usd: 4.0 }
```

- [ ] **Step 4: `src/types.ts`** — 스펙 §2·§6.5·§7의 타입을 그대로 옮긴다.

```ts
export type Status = 'CONFIRMED' | 'INFERRED' | 'UNKNOWN' | 'POSSIBLE_LEGACY' | 'POTENTIAL_ISSUE';
export interface Evidence { file: string; symbol?: string; lines?: string; commit?: string }
export interface Claim { text: string; status: Status; evidence: Evidence[] }
export type DiagramType = 'architecture' | 'sequence' | 'flowchart' | 'dataflow' | 'state' | 'er' | 'class';
export interface DiagramNode { node: string; code: string; symbol?: string }
export interface Diagram { id: string; type: DiagramType; title: string; summary: string; mermaid: string; details: string; nodes: DiagramNode[]; status: Status }
export type FeatureStatus = 'ACTIVE' | 'DEPRECATED' | 'REMOVED';
export interface FeatureSummary { id: string; name: string; slug: string; summary: string; entryPoints: string[]; relatedFiles: string[]; dependencies: string[]; importance: 'CORE' | 'SUPPORTING' | 'INFRA'; analysisStatus: 'PENDING' | 'SUCCESS' | 'FAILED' | 'STALE'; status: FeatureStatus }
export type Significance = 'MAJOR' | 'MINOR' | 'TRIVIAL';
export interface FeatureHistoryEntry { date: string; title: string; classification: string[]; before: string; after: string; reason: Claim; impact: string[]; significance: Significance; commits: string[] }
export interface FeatureAnalysis { feature: FeatureSummary; summary: string; entryPoints: Claim[]; executionFlow: { step: number; component: string; file: string; symbol?: string; description: string }[]; relatedCode: { file: string; symbol?: string; role: string }[]; dataFlow: Claim[]; stateTransitions: { from: string; to: string; trigger: string; evidence: Evidence[] }[]; databaseAccess: { entity: string; operation: string; file: string; symbol?: string }[]; externalDependencies: Claim[]; failurePoints: { where: string; condition: string; handling: string; status: Status; evidence: Evidence[] }[]; edgeCases: Claim[]; logging: Claim[]; diagrams: Diagram[]; unknowns: string[]; evidence: Evidence[]; history: FeatureHistoryEntry[] }
export type ItemStatus = 'PENDING' | 'RUNNING' | 'SUCCESS' | 'FAILED' | 'STALE' | 'SKIPPED';
export type RunMode = 'INITIAL' | 'INCREMENTAL';
```
(architecture·inventory·data·api·failures·operations·verification·changes·classification·impact·featureDelta·baseline·run 타입도 같은 파일에 스펙 §6·§7·§8 필드명 그대로 선언한다.)

- [ ] **Step 5: 실패 테스트 → 구현 → 통과**: `config.test.ts`는 `loadConfig()`가 `harness.yaml`을 읽고 `resolvePaths`가 `projectRoot`를 `doc-harness/..`의 절대 경로로, `docsOut`을 `<projectRoot>/docs/generated`로 돌려주는지. `fsx.test.ts`는 (a) `atomicWriteJson` 후 `.tmp` 파일이 남지 않는다 (b) `listSourceFiles`가 임시 디렉터리에서 `bin/`·`node_modules/`를 제외하고 `/`로 정규화된 정렬 목록을 준다 (c) `sha256('a')`가 고정값(`ca978112…`).

Run: `cd doc-harness && npm test` → PASS. `npm run typecheck` 오류 0.

- [ ] **Step 6: Commit** `추가: 문서화 하네스 골격과 설정·유틸`

---

### Task 2: 스키마와 검증기

**Files:**
- Create: `schemas/common.schema.json`, `inventory·architecture·features·feature·data·api·failures·operations·verification·changes·classification·impact·feature_delta·diagram_update·document·baseline·run·state.schema.json`, `src/schema.ts`, `test/unit/schema.test.ts`, `test/fixtures/schema/*.json`

**Interfaces:**
- Produces: `validate(name: SchemaName, data: unknown): { ok: true } | { ok: false; errors: string[] }`, `loadSchema(name): object`(CLI `--json-schema`에 넘길 **$ref 없이 인라인된** 객체 — `claude`는 외부 $ref를 해석하지 않으므로 `common` $defs를 각 스키마에 복사해 넣는 `inlineDefs()` 포함).

- [ ] **Step 1: `common.schema.json` $defs**: `status`(enum 5), `evidence`, `claim`, `diagram`(§6.5, `nodes` minItems 1), `featureSummary`, `historyEntry`.
- [ ] **Step 2: 각 스키마** — 스펙 §6·§7·§8의 필드를 `required`로 고정. `feature.schema.json`은 `FeatureAnalysis` 15+1 필드 전부 required. `document.schema.json`(서술 문서 LLM 출력)은 `{ title, sections: [{ id, heading, body, diagrams?: diagram[] }], relatedDocs: string[], unknowns: string[] }`. `diagram_update.schema.json`은 `{ decision: 'UNCHANGED'|'UPDATED', mermaid, changedEdges: string[], nodes: diagramNode[], reason }`. `verification.schema.json`은 §8 결과 + `diagramIssues[].type` enum(`INCORRECT_DIAGRAM_RELATION`·`DIAGRAM_NODE_NOT_IN_CODE`·`MERMAID_SYNTAX_ERROR`·`MERMAID_UNCLOSED_BLOCK`·`MERMAID_STYLE_FORBIDDEN`·`DIAGRAM_ABSTRACT_NODE`·`DIAGRAM_TOO_COMPLEX`·`SECTION_ANCHOR_MISMATCH`).
- [ ] **Step 3: 테스트**: 픽스처 `feature.ok.json` 통과, `feature.missing-evidence.json` 실패(`errors`에 `/evidence` 포함), `loadSchema('feature')`에 `$ref`가 남아 있지 않다(`JSON.stringify` 검색), `enum` status 오타 실패.
- [ ] **Step 4: Commit** `추가: 문서화 산출물 스키마와 이중 검증기`

---

### Task 3: Claude 어댑터

**Files:**
- Create: `src/claude.ts`, `test/unit/claude.test.ts`, `test/fixtures/fake-claude.mjs`, `test/integration/claude.live.test.ts`

**Interfaces:**
- Produces:
```ts
export interface ClaudeRequest { id: string; prompt: string; schemaName: SchemaName; phase: string; cwd: string; logDir: string }
export type ClaudeResult = { ok: true; output: unknown; costUsd: number; sessionId: string; durationMs: number; attempts: number } | { ok: false; error: string; attempts: number; costUsd: number }
export interface ClaudeRunner { run(req: ClaudeRequest): Promise<ClaudeResult> }
export class CliClaudeRunner implements ClaudeRunner { constructor(cfg: HarnessConfig, opts?: { bin?: string }) }
export class FakeClaudeRunner implements ClaudeRunner { constructor(handlers: Record<string, (req) => unknown | Error>) ; calls: ClaudeRequest[] }
```

- [ ] **Step 1: 실패 테스트** — `fake-claude.mjs`는 argv를 검사해 stdin 프롬프트를 읽고 `{"structured_output": …, "is_error": false, "total_cost_usd": 0.01, "session_id": "s1", "stop_reason": "end_turn"}`을 출력한다(env `FAKE_CLAUDE_MODE=badjson|error|invalid_schema`로 분기). 테스트: (a) 인자에 `--tools Read,Glob,Grep`·`--setting-sources user`·`--no-session-persistence`·`--permission-prompts none`·`--json-schema`가 있고 `Write`·`Edit`·`Bash`가 없다 (b) 프롬프트가 stdin으로 전달됐다(스텁이 echo) (c) `badjson`이면 3회 재시도 후 `ok:false` (d) `invalid_schema`(스키마 위반 출력)면 ajv가 잡아 재시도 (e) `logDir`에 `<id>.log`·`<id>.prompt.md`가 남고 log에 `attempts`·`cost` 필드가 있다.
- [ ] **Step 2: 구현** — `spawn(bin, args, { cwd, stdio: ['pipe','pipe','pipe'], windowsHide: true })`, `child.stdin.end(prompt)`, timeout은 `cfg.claude.default.timeout_sec`(초과 시 `kill`). 인자:
```ts
['-p','--output-format','json','--json-schema', JSON.stringify(loadSchema(req.schemaName)),
 '--tools','Read,Glob,Grep','--setting-sources','user','--no-session-persistence','--permission-prompts','none',
 '--model', phaseCfg.model, '--max-budget-usd', String(phaseCfg.budget_usd), '--effort', phaseCfg.effort]
```
Windows에서는 `bin`이 `claude`면 `claude.exe`를 `PATH`에서 찾는다(`shell: false`). 결과 파싱: stdout JSON → `is_error`면 실패, `structured_output` 없으면 실패, `validate(schemaName, structured_output)` 실패면 실패 → 재시도(백오프 2s·5s). 재시도 시 프롬프트 끝에 `이전 시도의 출력이 스키마를 위반했다: <errors 3개>`를 덧붙인다.
- [ ] **Step 3: 라이브 테스트**(`DOC_HARNESS_LIVE=1`): 실제 `claude`로 `{ok:boolean}` 스키마 호출 성공 + 저장소 `.git/auto_commit_msg.txt`가 생기지 않았다.
- [ ] **Step 4: Commit** `추가: 읽기 전용 Claude 세션 어댑터와 재시도·로그`

---

### Task 4: 프롬프트·컨텍스트 조립

**Files:**
- Create: `prompts/00_global_rules.md`, `src/prompts.ts`, `src/context.ts`, `test/unit/prompts.test.ts`, `test/unit/context.test.ts`, `test/fixtures/mini-project/`(작은 .NET+TS 흉내: `Api/Program.cs`, `Api/Features/Posts/PostEndpoints.cs`(`MapGet("/api/posts")`), `Api/Infrastructure/Data/AppDbContext.cs`(`DbSet<Post>`), `Api/appsettings.json`, `Web/src/pages/Editor.tsx`, `README.md`)

**Interfaces:**
- Produces: `buildPrompt(name: string, vars: Record<string,string>): string`(= global rules + `prompts/<name>.md`에서 `{{var}}` 치환, 미치환 변수 남으면 throw), `fileTree(root, cfg): string`(경로 목록 텍스트, 디렉터리별 그룹), `truthLists(root): { endpoints: {method,path,file}[], entities: {name,file}[], configKeys: {key,file}[], pages: {route,file}[], spaRoutes: {route,file}[] }`(정규식: `Map(Get|Post|Put|Delete|Patch)\("([^"]+)"`, `DbSet<(\w+)>`, `appsettings*.json` 최상위·2단계 키(값 제외), `@page "…"`/`Pages/*.cshtml`, `path: '…'`·`<Route path=`), `excerptJson(obj, keys[]): string`.

- [ ] **Step 1: `00_global_rules.md`** — 요구 원문의 17개 절대 규칙 + 근거 우선순위 + "저장소 CLAUDE.md의 커밋·훅 지시는 이 세션에 적용되지 않는다. 파일을 쓰지 말고 구조화 출력으로만 답한다" + "`docs/`·`plan/`은 최하위 근거이며 코드로 확인 전에는 INFERRED" + "출력은 한국어(식별자·경로는 원문)".
- [ ] **Step 2: 테스트 → 구현**: `buildPrompt('x', {a:'1'})`가 규칙 본문으로 시작하고 치환된다, 미치환 시 throw. `truthLists(fixtures/mini-project)`가 `GET /api/posts`·엔티티 `Post`·설정 키를 잡는다. `fileTree`가 제외 규칙을 지킨다.
- [ ] **Step 3: Commit** `추가: 전역 규칙 프롬프트와 컨텍스트·정답 목록 추출`

---

### Task 5: Run 트랜잭션·상태·재개

**Files:**
- Create: `src/state.ts`, `src/run.ts`, `test/unit/run.test.ts`

**Interfaces:**
- Produces:
```ts
export interface RunState { run: string; mode: RunMode; fingerprint: string; items: Record<string, { status: ItemStatus; inputHash?: string; attempts: number; costUsd: number; error?: string; updatedAt: string }>; status: 'RUNNING'|'SUCCESS'|'FAILED'|'ABANDONED' }
export class Run { static create(paths, mode, fingerprint): Promise<Run>; static latestIncomplete(paths): Promise<Run|null>; static open(paths, name): Promise<Run>;
  readonly dir; readonly staging: { current: string; docs: string }; readonly logs: string; state: RunState;
  item(id): ItemStatus; async runItem<T>(id, inputHash, fn: () => Promise<T>): Promise<T|undefined>  // SUCCESS+같은 해시면 skip(undefined 아님: 저장된 결과 로드는 호출자), RUNNING 기록→fn→SUCCESS/FAILED
  async commit(): Promise<void>  // §9 원자 교체: current→current.prev, staging/current→current, docs 동일, prev 삭제, state SUCCESS
  async abandon(reason): Promise<void>
  static async recover(paths): Promise<void> } // *.prev가 남아 있으면 롤포워드(정본이 완전하면 prev 삭제, 아니면 prev 복원)
```

- [ ] **Step 1: 실패 테스트** — 임시 workspace에서 (a) `create`가 `run-0001`, 다음이 `run-0002` (b) `runItem`이 fn 예외를 FAILED로 기록하고 재던진다 (c) SUCCESS + 같은 inputHash면 fn을 호출하지 않는다 (d) `commit` 후 `current/`에 staging 내용이 있고 `.prev`·staging 파일이 정리됐다 (e) commit 중간 상태(`current.prev` 존재, `current` 없음)에서 `recover`가 prev를 복원한다 (f) `latestIncomplete`가 RUNNING인 최신 run만 돌려준다.
- [ ] **Step 2: 구현** (Node `fs.promises.rename`; 같은 볼륨 보장 — staging은 workspace 안). docs 정본 교체는 `docs/generated` 전체가 아니라 **staging/docs에 있는 파일만 덮어쓰고, `run.json.deletedDocuments`에 든 파일만 삭제**한다(앵커 밖 사람이 만든 파일 보존).
- [ ] **Step 3: Commit** `추가: 검증 통과 후에만 정본을 바꾸는 Run 트랜잭션`

---

### Task 6: Baseline·변경 감지·git

**Files:**
- Create: `src/git.ts`, `src/baseline.ts`, `src/change/detect.ts`, `test/unit/detect.test.ts`(임시 git 저장소를 `git init`해 픽스처로)

**Interfaces:**
- Produces: `git(root, args[]): Promise<string>`, `headCommit(root)`, `commitExists(root, sha)`, `untrackedFiles(root)`, `nameStatusSince(root, sha): {status:'A'|'M'|'D'|'R', path, oldPath?}[]`, `diffHunks(root, path, sinceSha|null): { added: string[]; removed: string[] }`; `Baseline` 타입(§7), `loadBaseline(paths): Baseline|null`, `saveBaseline(paths, b)`, `fingerprint(files: {path,hash}[]): string`; `detectChanges(paths, cfg, baseline|null): Promise<ChangeSet>` where `ChangeSet = { fingerprint, baselineCommit: string|null, headCommit, files: { file, changeType: 'ADDED'|'MODIFIED'|'DELETED'|'RENAMED'|'UNTRACKED', oldPath?, untracked: boolean, addedLines: string[], removedLines: string[], relatedSymbols: string[], possibleFeatures: string[], requiresAnalysis: boolean }[] }`.

- [ ] **Step 1: 실패 테스트** — 픽스처 저장소에 파일 3개 커밋 → baseline(files 해시) 생성 → (a) 수정 1·삭제 1·untracked 추가 1·rename 1을 만들고 `detectChanges`가 각각 MODIFIED/DELETED/UNTRACKED(ADDED)/RENAMED로 분류 (b) `.md`만 바뀐 파일은 `requiresAnalysis:false` (c) `relatedSymbols`가 `public class Foo`·`MapGet("/x")`를 잡는다 (d) baseline null이면 전부 ADDED.
- [ ] **Step 2: 구현** — 해시 대조가 1차, git `--name-status -M`은 rename 보강. hunk는 untracked면 파일 전체, 아니면 `git diff <baselineCommit> -- path`(baselineCommit 없으면 `git diff HEAD`)의 `+`/`-` 줄. 심볼 정규식: `(class|record|interface|enum)\s+(\w+)`, `Map(Get|Post|Put|Delete|Patch)\("([^"]+)"`, `export (function|const|class) (\w+)`, `function (\w+)`.
- [ ] **Step 3: Commit** `추가: 파일 해시와 git으로 baseline 이후 변경을 잡는 감지기`

---

### Task 7: Phase 01~03 (Inventory·Architecture·Discovery)

**Files:**
- Create: `prompts/01_inventory.md`, `02_architecture.md`, `03_feature_discovery.md`, `src/phases/inventory.ts`, `architecture.ts`, `discovery.ts`, `src/pipeline.ts`(PhaseContext만), `test/unit/phases-early.test.ts`

**Interfaces:**
- Produces: `interface PhaseContext { cfg; paths; run: Run; runner: ClaudeRunner; scope: Scope; log: (msg) => void }`, `type Scope = { kind: 'ALL' } | { kind: 'PARTIAL'; features: string[]; changedFiles: string[]; docs: string[]; diagrams: string[] }`; `runInventory(ctx): Promise<Inventory>`, `runArchitecture(ctx, inv, prev?: Architecture): Promise<Architecture>`, `runDiscovery(ctx, inv, arch): Promise<FeaturesFile>`. 각 함수는 `ctx.run.runItem('inventory', hash, …)`로 감싸고 결과를 `staging/current/<name>.json`에 쓴다.

- [ ] **Step 1: 프롬프트** — 01: "지도만 만든다, 기능 분석 금지", 조사 항목 20개, 출력 스키마 필드 설명, `{{fileTree}}`·`{{manifests}}`(csproj·package.json·compose·CI 경로). 02: `{{inventory}}` + `{{entryPoints}}` + "관계는 코드에서 확인, 다이어그램은 architecture(flowchart LR) 1개 + 필요하면 서브시스템 Level 분리, 실제 클래스명, style 금지". 03: "클래스≠기능, 사용자/시스템 관점, 상한 {{maxFeatures}}, importance, 도구 디렉터리 제외, slug는 대문자 스네이크".
- [ ] **Step 2: 테스트(Fake)** — 프롬프트에 mini-project의 `Program.cs` 경로가 포함되고, Fake 출력이 `staging/current/inventory.json`에 저장되며 state가 SUCCESS.
- [ ] **Step 3: Commit** `추가: 인벤토리·아키텍처·기능 발견 단계`

---

### Task 8: Phase 04~07 (기능 심층·Data/API·실패 이력·횡단)

**Files:**
- Create: `prompts/04_feature_analysis.md`, `05_data.md`, `05_api.md`, `06_failure_history.md`, `07_operations_error.md`, `07_operations_security.md`, `07_operations_performance.md`, `07_operations_debt.md`, `src/phases/feature.ts`, `dataApi.ts`, `failures.ts`, `operations.ts`, `src/git-extract.ts`, `test/unit/phases-late.test.ts`, `test/unit/git-extract.test.ts`

**Interfaces:**
- Produces: `runFeatures(ctx, features: FeatureSummary[], prev: Map<string, FeatureAnalysis>): Promise<Map<string, FeatureAnalysis>>`(워커 풀 `analysis.feature_parallelism`, 항목 id `feature:F001`), `runDataApi(ctx, features)`, `extractGitHistory(root, cfg, since: string|null, includeWorkingTree: boolean): GitExtract { commits: {sha,date,subject,body,files,keywordHits,diffExcerpt}[], markers: {file,line,text}[], workingTreeDiffExcerpt? }`, `runFailures(ctx, extract, prevFailures, sessionContext?: string)`, `runOperations(ctx, featureAnalyses)`.

- [ ] **Step 1: 04 프롬프트** — 분석 항목 30개, 실패 경로 강조, Diagram 선택표와 이름·스타일 규칙, `{{feature}}`·`{{components}}`·`{{previousAnalysis}}`(없으면 "없음")·`{{changeHints}}`. 재분석일 때 "이전 분석은 검증 대상이지 사실이 아니다. 동작·설계에 의미 있는 변경이면 history 항목 1개를 MAJOR/MINOR로 추가, 아니면 history는 이전 것을 그대로".
- [ ] **Step 2: `git-extract`** — `git log --format=… --date=iso-strict --name-only [since..HEAD]`, 키워드(`cfg.git.failure_keywords`) 히트 커밋만 `git show --stat -p --format= <sha>`를 `max_diff_bytes_per_commit`로 자름. 마커 grep(`TODO|FIXME|HACK|XXX|workaround|fallback|retry|deprecated|legacy`, 소스 확장자만). 테스트: 픽스처 저장소에서 "버그수정:" 커밋이 keywordHits에 잡히고 diff가 상한 이하.
- [ ] **Step 3: 06·07 프롬프트** — 06: "커밋 메시지만으로 원인 단정 금지, 없으면 만들지 않는다, 기존 ID `{{existingIds}}`는 유지·보강만, session_context는 최하위 근거", 07 4종: 분류 `CONFIRMED_ISSUE|POTENTIAL_RISK|IMPROVEMENT` 필수, 분석과 개선 제안 분리.
- [ ] **Step 4: 테스트(Fake)** — 기능 2개에 Fake가 순서대로 응답, 하나가 2회 실패 후 성공 → `attempts` 기록; `feature_parallelism: 2`에서도 결과가 id별로 정확.
- [ ] **Step 5: Commit** `추가: 기능 심층 분석·데이터/API·실패 이력·횡단 분석 단계`

---

### Task 9: depgraph·영향 분석·분류·기능 델타

**Files:**
- Create: `src/depgraph.ts`, `src/change/classify.ts`, `src/change/impact.ts`, `src/change/featureDelta.ts`, `prompts/I02_classify.md`, `prompts/I04_feature_delta.md`, `test/unit/depgraph.test.ts`, `test/unit/impact.test.ts`

**Interfaces:**
- Produces: `buildDepgraph(current: CurrentWorkspace, docIndex: DocIndex): Depgraph`(`{ files: Record<path,{features,apis,entities,diagrams,documents}>, features: Record<id,{files,apis,entities,diagrams,documents,dependencies}> }`), `classifyChanges(ctx, changes, features): Promise<Classification>`(`{ items: [{ file, classifications: string[], significance, possibleFeatures, rationale }], summary }`), `analyzeImpact(changes, classification, depgraph, arch): Impact`(`{ affectedFeatures, candidateNewFeatureFiles, affectedDocs, affectedDiagrams, needsArchitecture, needsData, needsApi, widenReason: string[] }`), `runFeatureDelta(ctx, features, changes, classification, impact): Promise<FeatureDelta>`(`{ newFeatures: FeatureSummary[], changedFeatureIds, removedFeatures: [{id, disposition: 'REMOVED'|'DEPRECATED'|'MIGRATED'|'PARTIAL', replacedBy?, reason: Claim}] }`, 신규 ID는 하네스가 `F%03d` 다음 번호로 부여하고 프롬프트에 알려준다).

- [ ] **Step 1: depgraph 테스트** — 기능 F001의 evidence 파일 `a.cs`가 `files['a.cs'].features`에 F001, `api.json` featureIds로 apis 연결, 문서 inputs 선언(`templates.ts`의 `DOC_INPUTS` 상수, Task 10에서 정의하므로 여기서는 인터페이스만 두고 인자로 받는다).
- [ ] **Step 2: impact 규칙** — 직접(file→features) ∪ dependencies 1홉 ∪ (분류에 ARCHITECTURE_CHANGE|API_CHANGE|DATA_MODEL_CHANGE가 있으면 같은 component의 기능; `widenReason` 기록) ; 매핑 없는 requiresAnalysis 파일 → `candidateNewFeatureFiles`; 삭제 후보: relatedFiles 전부 DELETED. affectedDocs = features→documents ∪ 분류별 고정 문서(`CONFIG_CHANGE→05_CONFIGURATION`, `DEPENDENCY_CHANGE→06_DEPENDENCIES`, `DEPLOYMENT_CHANGE→16_DEPLOYMENT`, `TEST_CHANGE→15_TESTING`, `SECURITY_CHANGE→13_SECURITY`, `PERFORMANCE_CHANGE→14_PERFORMANCE`, `ERROR_HANDLING_CHANGE→10_ERROR_HANDLING`) ∪ 항상 `00_EXECUTIVE_SUMMARY`·`20_CHANGELOG`·`README`.
- [ ] **Step 3: Commit** `추가: 근거에서 유도한 의존 그래프와 변경 분류·영향 분석`

---

### Task 10: 렌더러 — 섹션 앵커·템플릿·서술 문서·Diagram

**Files:**
- Create: `src/render/sections.ts`, `templates.ts`, `documents.ts`, `diagrams.ts`, `templates/*.md`, `prompts/08_doc_*.md`(00_EXECUTIVE_SUMMARY·01_PROJECT_OVERVIEW·02_ARCHITECTURE·03_DIRECTORY_STRUCTURE·04_SETUP_AND_RUN·05_CONFIGURATION·06_DEPENDENCIES·10_ERROR_HANDLING·12_TROUBLESHOOTING·13_SECURITY·14_PERFORMANCE·15_TESTING·16_DEPLOYMENT·18_GLOSSARY), `prompts/I08_diagram_update.md`, `test/unit/sections.test.ts`, `test/unit/render.test.ts`

**Interfaces:**
- Produces:
```ts
// sections.ts
export interface Section { id: string; hash: string; start: number; end: number; body: string }  // body = 앵커 사이 텍스트
export function parseSections(md: string): { sections: Section[]; issues: {type:'SECTION_ANCHOR_MISMATCH'|'MERMAID_UNCLOSED_BLOCK', line:number}[] }
export function wrapSection(id: string, body: string): string   // hash = sha256(body)
export function replaceSection(md: string, id: string, body: string): string  // 없으면 문서 끝에 추가
export function sectionHash(body: string): string
export function extractMermaidBlocks(md: string): { sectionId?: string; code: string; line: number }[]
// templates.ts
export const DOC_INPUTS: Record<string, string[]>  // '08_API.md': ['api.json','features.json'] …
export function renderTemplateDoc(name, ws: CurrentWorkspace): string  // feature·07·08·09·11·17·19·20·README
export function renderFeatureDoc(fa: FeatureAnalysis): string  // 파일명 features/F001_<SLUG>.md
// diagrams.ts
export function renderDiagramSection(d: Diagram): string  // 결론→mermaid→상세→코드 근거 표, 섹션 id = d.id
// documents.ts
export function renderNarrativeDoc(ctx, name, ws, prevMd: string|null, issues?: string[]): Promise<string>  // LLM(document.schema) → 섹션 조립
export function updateDocs(ctx, ws, scope, prevDocs: Map<name, md>): Promise<{ written: Map<name, md>; deleted: string[]; diagramDecisions: DiagramDecision[]; manualEdits: {doc, section, kept: boolean}[] }>
```

- [ ] **Step 1: sections 테스트** — 앵커 2개 파싱, 열린 펜스 감지, 짝 안 맞는 앵커 감지, `replaceSection`이 앵커 밖 텍스트를 보존, `wrapSection` 해시가 본문 변경 시 달라진다.
- [ ] **Step 2: 템플릿** — 두괄식 골격(제목·한 줄 요약·핵심·상세·Diagram·코드 근거·주의사항·관련 문서). `11_FAILURE_HISTORY`·`20_CHANGELOG`는 **prevMd의 항목을 유지하고 새 ID/날짜만 append**(ID 집합 비교). 기능 문서 `## 변경 이력`은 MAJOR/MINOR만.
- [ ] **Step 3: updateDocs 규칙** — scope ALL: 전부 렌더. PARTIAL: `DOC_INPUTS` 입력 해시가 바뀐 문서 + `affectedDocs`만. 서술 문서는 prevMd를 주고 "바뀐 섹션만" 요구, 응답 섹션으로 `replaceSection`. Diagram 섹션: affectedDiagrams만 `I08_diagram_update` 호출(기존 mermaid + 새 흐름 + hunk), UNCHANGED면 바이트 유지. MANUAL_EDIT(앵커 해시 ≠ 본문 해시)는 이번 Run에서 다시 쓰지 않고 `manualEdits`에 기록(검증 결과에 따라 Task 12 루프가 교체).
- [ ] **Step 4: Commit** `추가: 섹션 앵커 렌더러와 템플릿·서술·다이어그램 갱신`

---

### Task 11: 결정적 검증 — 참조·Mermaid·교차 일관성

**Files:**
- Create: `src/verify/references.ts`, `mermaid.ts`, `consistency.ts`, `test/unit/verify-mermaid.test.ts`, `test/unit/verify-refs.test.ts`

**Interfaces:**
- Produces: `checkReferences(docs: Map<name,md>, projectRoot, ws): Issue[]`(경로·심볼 grep·`F###`·내부 링크), `checkMermaid(docs, ws, cfg): Promise<{ issues: DiagramIssue[]; parser: 'VERIFIED'|'MERMAID_RENDER_NOT_VERIFIED' }>`(구조·`mermaid.parse`·린트·nodes↔code·participant ⊆ executionFlow), `checkConsistency(docs, ws): Issue[]`(엔드포인트·엔티티·컴포넌트 집합 대조). `Issue = { document, section?, type, description, evidence: Evidence[] }`.

- [ ] **Step 1: mermaid 테스트** — jsdom 로더(`globalThis.window/document` 주입, 실패 시 `NOT_VERIFIED`), 정상 4종 통과, `flowchartt` 오류, `classDef` 금지, `A1 --> B2` 난수 ID, `Service --> Repository` 추상 이름, 노드 26개 TOO_COMPLEX, `nodes[].code` 부재 파일.
- [ ] **Step 2: refs·consistency 테스트** — 문서의 `` `Api/Nope.cs` `` → 이슈, `F999` → 이슈, 08_API에 없는 api.json 엔드포인트 → MISSING.
- [ ] **Step 3: Commit** `추가: 참조·Mermaid·교차 일관성 결정적 검증기`

---

### Task 12: LLM 검증·일관성 리뷰·수정 루프

**Files:**
- Create: `prompts/09_verification.md`, `prompts/09_consistency.md`, `src/verify/llm.ts`, `src/verify/loop.ts`, `test/unit/verify-loop.test.ts`

**Interfaces:**
- Produces: `verifyLlm(ctx, docGroup: {name, docs}, ws, truth): Promise<VerificationPartial>`, `consistencyLlm(ctx, changedDocs, referencedDocs): Promise<Issue[]>`, `verificationLoop(ctx, ws, docs, scope, opts: { maxIterations, fixer: (issues) => Promise<Map<name,md>> }): Promise<{ verification: Verification; docs; iterations; passed: boolean; overridden: ManualOverride[] }>`.

- [ ] **Step 1: 루프 테스트(Fake)** — 1회차 이슈 2개 → fixer 호출 → 2회차 0개 → passed; 3회 내내 이슈면 `passed:false`; MANUAL_EDIT 섹션에 `INCORRECT_DIAGRAM_RELATION`이 나오면 fixer 대상에 포함되고 `overridden`에 기록.
- [ ] **Step 2: 프롬프트** — 09: "공격적 리뷰어, 문서 작성자 아님", 정답 목록 `{{truthLists}}` 주입, Hallucination/Missing/Relation/Diagram/Failure/Setup 6개 검사, `fixRequired`에 문서·섹션 id. 09_consistency: 모순만.
- [ ] **Step 3: Commit** `추가: LLM 검증자와 문서만 고치는 수정 루프`

---

### Task 13: 모드 판정·파이프라인·CLI·리포트

**Files:**
- Create: `src/mode.ts`, `src/pipeline.ts`(완성), `src/report.ts`, `src/cli.ts`, `test/unit/mode.test.ts`, `test/unit/pipeline.test.ts`(Fake로 INITIAL→INCREMENTAL 한 바퀴), `doc-harness/README.md`

**Interfaces:**
- Produces: `decideMode(paths, baseline, changes, opts:{full}): 'INITIAL'|'INCREMENTAL'|'UP_TO_DATE'`; `runPipeline(opts: { mode?: 'auto'|'full'; phase?: string; feature?: string; sessionContextPath?: string; verifyOnly?: boolean }): Promise<RunSummary>`; `formatReport(summary): string`(요구 원문의 "문서화 완료"/"문서화 확인 완료"/실패 형식); CLI: `run [--auto|--full|--phase <id>|--feature <id>]`, `status`, `verify`, `resume`, `clean [--runs]`, `report [run]`. `status`는 LLM 없이 baseline·변경 파일 수·예상 영향 기능·STALE·마지막 Run을 출력.

- [ ] **Step 1: pipeline 순서** — INITIAL: 01→02→03→04→05→06→07→08→(09 루프)→depgraph→baseline→commit→run.json→report. INCREMENTAL: I01→(UP_TO_DATE면 리포트 종료)→I02→I03→I04→I05(변경+신규)→I06(조건)→I07→I08→I09→I10 루프→I11. `verifyOnly`는 `docs/generated`를 읽어 검증만 하고 정본을 건드리지 않는다. 실패 시 `run.status=FAILED`, baseline 유지, report에 남은 이슈.
- [ ] **Step 2: 테스트** — Fake 응답 세트로 mini-project INITIAL 완주 → baseline 생성 → 파일 1개 수정 → INCREMENTAL이 해당 기능만 재분석(Fake `calls`에서 `feature:F002` 없음) → UP_TO_DATE 케이스 → `verifyOnly`가 baseline을 바꾸지 않는다.
- [ ] **Step 3: Commit** `추가: 문서화 모드 판정·파이프라인·CLI·리포트`

---

### Task 14: 트리거 설치와 하네스 문서

**Files:**
- Create: `.claude/skills/doc-harness/SKILL.md`, `.agents/skills/doc-harness/SKILL.md`(동일)
- Modify: `CLAUDE.md`(구성 절에 `doc-harness/` 한 줄 + 새 절 "하네스: 문서화" + 플랜 표), `AGENTS.md`(미러), `docs/harness.md`, `README.md`(디렉터리 트리·문서 표에 `docs/generated/`), `plan/harness_changelog.md`

- [ ] **Step 1: SKILL.md** — frontmatter `name: doc-harness`, `description: "프로젝트 문서를 다단계 Claude 파이프라인으로 생성·증분 갱신한다. 트리거(메시지 전체가 정확히): '문서화', '문서화 전체', '문서화 상태', '문서화 검증'. 되묻지 않고 모드를 자동 판정한다."`. 본문 절차(스펙 §10): 사전 점검(`node`, `doc-harness/node_modules` 없으면 `npm ci`) → 세션 맥락 작성 규칙(직전 구현·실패·거부·workaround가 있을 때만, `workspace/inbox/session_context.md`, 비밀값 금지) → 센티널 `.git/harness_commit_in_progress` 생성 → `npm run harness -- run --auto`(전체: `--full`) 백그라운드 Bash(`timeout` 600000) → 완료 알림 후 `workspace/runs/<최신>/report.txt`를 그대로 출력 → 센티널 삭제 → `.git/auto_commit_msg.txt` 작성(`문서: 문서화 <모드> …`). `문서화 상태`·`문서화 검증`은 동기 실행(`status`·`verify`). 실패 시 report의 이슈 표를 보여 주고 baseline이 유지됐음을 알린다.
- [ ] **Step 2: CLAUDE.md 절** — 목표·트리거(D7)·"되묻지 않는다"·서브명령 표·변경 이력 포인터. AGENTS.md 동일.
- [ ] **Step 3: `pwsh scripts/harness-audit.ps1` PASS** 확인.
- [ ] **Step 4: Commit** `추가: '문서화' 트리거 스킬과 프로젝트 지침 등록`

---

### Task 15: 실전 실행·검증·보고서

- [ ] **Step 1: 소범위**: `npm run harness -- run --phase inventory` → `architecture` → `discovery`. 산출물을 읽어 기능 목록이 "사용자 관점"인지, 도구 디렉터리가 빠졌는지 확인. 어긋나면 프롬프트 수정 후 재실행.
- [ ] **Step 2: 기능 2개**: `run --feature F001`, `run --feature F002`. Diagram 규칙(실제 이름·스타일 없음·순서) 확인.
- [ ] **Step 3: 전체 INITIAL**: `run --full`(백그라운드). 비용·시간을 `run.json`에서 기록. verification 결과 검토.
- [ ] **Step 4: INCREMENTAL**: 소스 파일 하나에 의미 있는 변경 없이 주석 한 줄(문서화 검증용, 커밋하지 않음) → `run --auto` → 해당 기능만 재분석되고 UNCHANGED 다이어그램이 바이트 그대로인지 → 되돌린 뒤 `run --auto`가 UP_TO_DATE가 아닌 이유(해시가 baseline과 같으므로 UP_TO_DATE여야 함) 확인.
- [ ] **Step 5: `status`·`verify`** 동작 확인.
- [ ] **Step 6: 보고서** `plan/doc_harness_0923.md`(배경·설계 결정 표·컴포넌트·핵심 사용법·변경 파일·검증 명령·실측치·향후 확장), CLAUDE.md 플랜 표에 추가. 메모리 갱신.
- [ ] **Step 7: Commit** `문서: 문서화 하네스 실전 실행 결과와 보고서`

---

## Self-Review

- 스펙 §5 호출 계약 → Task 3. §6.1 → Task 7·8·10·12·13. §6.2 I01~I11 → Task 6·9·8·10·12·13. §6.3 → Task 13. §6.4 → Task 6. §6.5·§6.6 → Task 10·11·12. §7 → Task 5·6·9. §8 → Task 11·12. §9 → Task 5·13. §10 → Task 14. §11 → Task 3(stdin·재시도)·8(diff 상한)·7(기능 상한).
- 타입 일관성: `Run.runItem`·`PhaseContext`·`Scope`·`DOC_INPUTS`·`Issue`·`Diagram`이 Task 5·7·10·11·12에서 같은 이름·시그니처로 쓰인다.
- 플레이스홀더 없음. 코드 블록이 없는 단계는 인터페이스 블록의 시그니처로 정의된다.
