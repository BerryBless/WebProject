# 하네스 변경 이력

`CLAUDE.md`/`AGENTS.md` 각 하네스 절의 **변경 이력** 표를 이 파일로 옮겼다(2026-09-14, 매 세션 로드되는 토큰 절감). 하네스를 고치면 해당 절 표에 행을 추가하고, 설계 결정은 `plan/<기능명>_<MMDD>.md` 에 남긴다.

## 하네스 공통

| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-09-21 | XML 주석 규칙에 "적용 범위" 표 추가: 3항목 `<remarks>`는 인터페이스·public 클래스와 메서드·대리자·테스트 클래스에만 요구하고, 자동 속성·상수·DTO record·옵션 속성·테스트 메서드는 `<summary>`만, 도구 생성 코드는 면제. style-review 4-b가 같은 표를 따르고 상용구 remarks를 low로 보고 | CLAUDE.md·AGENTS.md·style-review 스킬 + `.agents` 미러 | 1단계 구현(PR #1) 최종 리뷰 권고: 자동 속성과 `[Fact]`마다 붙은 내용 없는 상용구가 파일 분량의 상당 부분을 차지하고 실제 제약을 가렸다. 리뷰어가 규칙 해석을 두고 Important 지적을 낸 사례도 있었다(Task 2) |
| 2026-09-20 | 솔루션·프로젝트 이름을 `WebProject` → `PortfolioBlog`로 변경하고 솔루션 파일을 `.slnx`로 전환. 스킬 본문의 경로 예시(`PortfolioBlog.Api`, `PortfolioBlog.slnx`)만 갱신, 동작 변경 없음 | tdd-orchestrator·code-review-orchestrator + `.agents` 미러·CLAUDE.md·AGENTS.md | 제품이 기술 블로그(`plan/tech_blog_0920.md`)로 확정되어 템플릿 이름을 정리. 하네스 스크립트는 루트 자동 인식이라 영향 없음 |
| 2026-09-14 | 토큰 절감: 변경 이력 표를 이 파일로 분리, 에이전트·스킬 description 축약(트리거 유지), 서브에이전트 `model:` 지정(기본 sonnet, 코드 생성·감독·cross 계열 opus), superpowers 플러그인 프로젝트 비활성화 | CLAUDE.md·AGENTS.md·에이전트 25종·스킬 26종+미러·.codex/agents·settings.json | 매 세션 고정 로드 약 47KB(CLAUDE.md 29KB + description 18KB) 중 절반 이상이 이력·트리거 나열이었고, 리뷰어 21종이 메인 모델을 상속해 팬아웃 비용이 큼 |

## 하네스: Git 자동 커밋 & 푸시 (Git Automator)

| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-06-03 | 초기 구성 | 전체 | Git 자동 커밋&푸시 파이프라인 구축 |
| 2026-06-03 | 파일 기반 메시지 전달로 재설계 | auto-commit.ps1 | nested claude -p 콜드스타트/stdin 취약성으로 폴백 빈발 |
| 2026-09-11 | git 에이전트 3개 프론트매터 등록·실명 호출, 커밋 주체 정책 명문화, 서명 Fable 5.1 통일 | git-*.md·commitandpush·auto-commit.ps1 | 하네스 전수조사: 프론트매터 없어 서브에이전트 미등록, 커밋 경로 이중화 정본 미정 |
| 2026-09-12 | 산출물 경로를 `_workspace/git/` 하위로 격리 | commitandpush·git-*.md | 모든 하네스가 `_workspace/` 루트를 공유해 산출물 파일명이 충돌하고 Phase 0 전체 이동이 타 하네스 산출물을 파괴(code-review 하네스 교차 검토에서 확인) |
| 2026-09-13 | Claude↔Codex 교차 점검 반영: Stop 훅 재작성(메시지 파일 선소비·실패 시 보존, 센티널로 파이프라인 중 커밋 차단, 잠금 디렉터리로 동시 실행 배제, 파일별 민감 필터+내용 스캔, 50MB 가드, 종료 코드 검사, push 실패 노출, 항상 exit 0), 스킬 run_dir·해시 resume·질문 최소화·push-only 모드, 에이전트 PASS/WARN/FAIL 계약·`-F` 커밋·실패 후 amend 금지·needs_confirmation 반환, security-patterns 정본화(JSON 키·연결 문자열·GUID 제거·주석/예시 실제값 FAIL), `.gitignore` `.env.*`, settings 훅 옵션, Codex toml 잡종 서명·경로 교정 | auto-commit.ps1·commitandpush·references 2종·git-*.md·settings.json·.gitignore·.codex/agents | plan/harness_cross_check_0913.md Git 절 19건 |
| 2026-09-10 | (AGENTS.md 전용) Codex 직접 커밋 규칙으로 분기 | AGENTS.md | Stop 훅은 Claude Code 전용이라 Codex 세션에서 메시지 파일이 잔류하는 문제 방지 |

## 하네스: Codex 협업 (Claude ↔ OpenAI Codex CLI)

| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-09-10 | 초기 구성 | codex 스킬·AGENTS.md | Codex CLI 병행 사용 + Claude→Codex 호출 워크플로 구축 |
| 2026-09-13 | argv 프롬프트 전달이 비TTY 툴에서 stdin 대기로 멈추는 문제 반영: 래퍼(invoke-codex.ps1) 사용을 1순위로, 직접 호출은 stdin(`-`) 파일 전달로 통일. `exec review` 대상 옵션(--uncommitted/--base/--commit) 명시, `resume --last` 대신 thread_id 재개, 사용량 한도 시 순차 실행·재시도 금지 | codex 스킬 | plan/harness_cross_check_0913.md cross 절 |

## 하네스: 교차 검증 개발 (Claude ↔ Codex Cross-Verify)

| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-09-10 | 초기 구성 | cross-verify 스킬 + cross-planner/cross-implementer/cross-reviewer/codex-adapter 에이전트 | Plan·리뷰 교차 검증 파이프라인 구축 |
| 2026-09-10 | 트리거 조건 강화 — 코덱스/Codex 키워드 필수 | cross-verify description·CLAUDE.md | 키워드 없는 일반 검증 요청에 고비용 파이프라인이 오발동하지 않도록 사용자 요청 |
| 2026-09-10 | 토큰 부족 시 Claude 단독 폴백 추가 | invoke-codex.ps1(status=quota)·codex-adapter·cross-verify | Codex 사용량 한도로 파이프라인이 멈추는 대신 작업을 완료하고 "교차 검증 아님"을 명시 피드백하도록 사용자 요청 |
| 2026-09-11 | 미러에서 codex 스킬 제거, 미러 서명 Codex로 통일, cross-planner/reviewer tools 제한 | .agents/skills·cross-*.md | 하네스 전수조사: 미러 정책 위반(재귀 위험)·잡종 서명 발견 |
| 2026-09-13 | Claude↔Codex 교차 점검 반영: invoke-codex.ps1 재작성(상태 1회 확정·Write-Error 제거·시도별 로그·quota 판별 오류줄 한정·경로 절대화·인자 인용·--json·meta에 out/prompt sha256·thread_id·cmd·codex_version), 오케스트레이터가 meta 독립 재검증·독립성 `.log` 검사, Stop 훅 센티널·기준 커밋 확정 시점·diff에서 `_workspace/**` 제외·신규 파일 본문 포함·`33_diff` 재검토·`00_manifest.json` 해시·최고 접미사 규칙·수정 없음 경로·VERDICT 토큰 통일, 에이전트 tools 명시(SendMessage/Skill 제거)·첫 줄 JSON, `.codex/agents`에서 Claude 전용 4종 제거 + 감사 항목 추가, 프롬프트 독립성·규칙 축, 예시를 WebProject.Api로 | cross-verify·invoke-codex.ps1·prompts·usage·codex-adapter/cross-*.md·harness-audit.ps1 | plan/harness_cross_check_0913.md cross 절 18건 |

## 하네스: 종합 코드 리뷰

| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-06-02 | 초기 구성 | 전체 | 종합 코드 리뷰 하네스 구축 |
| 2026-06-09 | 보안 가드 감사 | security-reviewer | 해킹·DDoS 공격 표면 점검 (리포트 plan/security_audit_0609.md) |
| 2026-09-11 | TeamCreate 의존 제거, Agent 팬아웃 방식으로 재작성, 리뷰어 tools 제한 | code-review-orchestrator·reviewer 4종 | 하네스 전수조사: 이 빌드에 팀 도구가 없어 실행 불가 |
| 2026-09-12 | 전용 run_dir(`_workspace/code-review/<run_id>/`) 격리, 팀 시대 프로토콜(SendMessage·claim) 제거, 기본 브랜치 빈 diff 폴백, 원본 diff 보존, 결정적 점수 산식·재정규화·판정 우선순위, JSON 구조 검증, 트리거 축소, 스킬 체크리스트 오류(레이어 그림·LINQ 예시·삭제 회귀·remarks 규칙) 교정 | code-review-orchestrator·reviewer 4종·review 스킬 4종·.codex/agents toml | Claude↔Codex 교차 검토(plan/code_review_harness_fix_0912.md): `_workspace/` 전체 이동이 타 하네스 산출물 파괴, 서브에이전트가 리더 ID 없이 SendMessage 시도, master에서 diff 0줄 등 16건 |
| 2026-09-13 | 쓰기 범위 훅 도입: `scripts/hooks/guard-write-scope.ps1`(PreToolUse, deny JSON), 감사·리뷰 에이전트 24종 프론트매터 `hooks:` + settings.json `-Mode map`(agent_type 판별), 감사 항목 8 추가 | guard-write-scope.ps1·에이전트 24종·settings.json·harness-audit.ps1 | plan/harness_cross_check_0913.md 미착수 항목 해소. 프론트매터 훅은 세션 시작 시 읽혀(CLI 문자열 확인) 이번 세션 프로브에서 미발동 → 재시작 후 프로브로 차단 동작 확인(소스·타 하네스 경로 거부, 자기 run_dir 허용) |

## 하네스: 동시성 가드 (.NET 10 고성능 서버)

| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-06-02 | 초기 구성 | 전체 | .NET 10 고성능 서버 동시성 하네스 구축 |
| 2026-09-11 | TeamCreate 의존 제거, Agent 팬아웃+순차 생성-검증으로 재작성 | concurrency-guard-orchestrator | 하네스 전수조사: 팀 도구 미존재 |
| 2026-09-12 | 산출물 경로를 `_workspace/concurrency-guard/` 하위로 격리, 새 실행 시 자기 디렉토리만 보관 이동 | concurrency-guard-orchestrator·전용 스킬 4종·에이전트 4종 | 모든 하네스가 `_workspace/` 루트를 공유해 산출물 파일명이 충돌하고 Phase 0 전체 이동이 타 하네스 산출물을 파괴(code-review 하네스 교차 검토에서 확인) |
| 2026-09-13 | Claude↔Codex 교차 점검 반영: 전용 run_dir·meta 해시(analyzer 재실행 시 reviewer 필수 재실행), 저장소 루트 `combined_source.txt` 제거(케이스 B 헤더·줄번호 보존), 형제 SendMessage·claim·필요 락 공유 제거(독립 병렬 + 오케스트레이터 대조), `needs_reanalysis:bool`+`reanalysis_targets[]` 계약 통일, 공통 finding 스키마(id·context·necessary), modified 포함 중앙 점수·재정규화·판정 우선순위, deadlock-analyzer tools 명시, .NET 오답 교정(ASP.NET Core SynchronizationContext 없음→기아 분류, lock{await}=컴파일 오류, ConfigureAwait 구조 판정·library 한정, Allman lock 정규식, System.Threading.Lock/EnterScope, SemaphoreSlim(1,1) 공인 프리미티브, Channel/CD는 thread-safe≠Lock-Free, bool CAS→int, ABA 재정의), [LOCK-REQUIRED]와 <remarks> 병행 계약·remarks 정합성 검사 | concurrency-guard-orchestrator·에이전트 4종·스킬 4종·.codex/agents toml | plan/harness_cross_check_0913.md 동시성 절 21건 |

## 하네스: GC 가드 (.NET 10 메모리 최적화)

| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-06-02 | 초기 구성 | 전체 | .NET 10 서버 GC 억제 메모리 최적화 하네스 구축 |
| 2026-09-11 | TeamCreate 의존 제거, Agent 팬아웃+순차 교차검증으로 재작성 | gc-guard-orchestrator | 하네스 전수조사: 팀 도구 미존재 |
| 2026-09-12 | 산출물 경로를 `_workspace/gc-guard/` 하위로 격리, 새 실행 시 자기 디렉토리만 보관 이동 | gc-guard-orchestrator·전용 스킬 3종·에이전트 3종 | 모든 하네스가 `_workspace/` 루트를 공유해 산출물 파일명이 충돌하고 Phase 0 전체 이동이 타 하네스 산출물을 파괴(code-review 하네스 교차 검토에서 확인) |
| 2026-09-12 | Codex 교차 점검 결과 반영: 전용 run_dir·해시 기반 부분 재실행(피어 리뷰 필수 재실행), 형제 간 SendMessage·claim 제거, pooling-enforcer tools 명시, 경로 모드 파일 헤더·줄번호 보존, 공통 finding 스키마(id·hot_path 3등급·necessary), modified 포함 중앙 점수 재계산·판정 우선순위·"분석 대상 없음" 상태, .NET 기술 오답 교정(캡처 없는 람다 캐싱, Count() 무할당, string.Format 박싱 전 버전, Split 동치, ArrayPool 풀 미스·소유권, stackalloc 조건, Task.FromResult 캐시, C# 13 Span·params span), fix_code에 CLAUDE.md 주석 규칙 | gc-guard-orchestrator·에이전트 3종·스킬 3종·.codex/agents toml | Claude↔Codex 교차 점검 20건(plan/gc_guard_harness_fix_0912.md) |

## 하네스: 파이프라인 아키텍처 (.NET 10 고성능 IO)

| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-06-02 | 초기 구성 | 전체 | .NET 10 고성능 IO 파이프라인 아키텍처 하네스 구축 |
| 2026-09-11 | TeamCreate 의존 제거, 감독자가 Agent로 워커를 중첩 호출하는 방식으로 재작성 | pipeline-architect-orchestrator·pipeline-supervisor | 하네스 전수조사: 팀 도구·TaskGet 미존재 |
| 2026-09-12 | 산출물 경로를 `_workspace/pipeline/` 하위로 격리, 새 실행 시 자기 디렉토리만 보관 이동 | pipeline-architect-orchestrator·전용 스킬 3종·에이전트 4종 | 모든 하네스가 `_workspace/` 루트를 공유해 산출물 파일명이 충돌하고 Phase 0 전체 이동이 타 하네스 산출물을 파괴(code-review 하네스 교차 검토에서 확인) |
| 2026-09-13 | Claude↔Codex 교차 점검 반영: run_dir·manifest(브리프·계약·산출물 해시), 계약을 불변 입력으로 확정(형제 협상·대기·SendMessage 제거, deviation 반환), 독립 빌드 게이트(build/Pipeline.csproj + dotnet build 경고 0·오류 0)와 오케스트레이터 재검증(빌드 재실행·감사 JSON 대조), 판정 3단계·점수 산식 단일 정본, 감사 입력 완전성 필수, 재작업 상한(워커별 1회·합계 2회), 에이전트 tools 명시, 템플릿 기술 오답 교정(examined 스핀, SequenceReader await 보존 컴파일 실패, 파이프 슬라이스 채널 전달 use-after-return→풀 버퍼 소유 복사본, struct IThreadPoolWorkItem 박싱, SetBuffer(byte*) 미존재, FullMode.Drop 미존재, WhenAll 상호 취소 없음, Complete(ex)·IsCanceled·EOF 잔여 프레임, 서버 범위 TryComplete, 워커 생존, PauseWriterThreshold≥MaxFrame+Header) — 템플릿 3종 net10.0 실제 빌드 검증 | pipeline-architect-orchestrator·pipeline-supervisor·io-loop-designer·thread-dispatcher-designer·load-test-auditor·스킬 3종·.codex/agents toml | plan/harness_cross_check_0913.md 파이프라인 절 22건 |

## 하네스: TDD (테스트 주도 개발)

| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-06-02 | 초기 구성 | 전체 | TDD Red-Green-Refactor 하네스 구축 (harness-evolve 포함) |
| 2026-09-11 | TeamCreate 의존 제거(순차 Agent 호출), dotnet_study 절대경로 제거, TddSession.csproj 템플릿 수정(EnableDefaultCompileItems=false, 단계별 Compile 조건을 실제 .cs 존재 여부로) | tdd-orchestrator·tdd-refactor-phase | 하네스 전수조사: 타 프로젝트 경로로 Refactor 단계 실패 확정. 실행 검증 중 NETSDK1022 중복·빈 03_qa/Src 로 스텁 미컴파일 발견 |
| 2026-09-12 | 산출물·TddSession.csproj 경로를 `_workspace/tdd/` 하위로 격리, 새 사이클 시 자기 디렉토리만 보관 이동 | tdd-orchestrator·tdd 단계 스킬 3종·harness-evolve·에이전트 3종 | 모든 하네스가 `_workspace/` 루트를 공유해 산출물 파일명이 충돌하고 Phase 0 전체 이동이 타 하네스 산출물을 파괴(code-review 하네스 교차 검토에서 확인) |
| 2026-09-13 | Claude↔Codex 교차 점검 반영: run_dir·manifest, TddSession.csproj를 **파일 단위 우선순위(qa > builder > analyst)** 로 재설계(msbuild 평가·dotnet test 실측 검증)해 부분 리팩토링·누적 사이클·재작업 루프에서 컴파일 깨짐 제거, csproj "없으면 생성", 네임스페이스 계약(TddSession/TddSession.Tests + global using Xunit), trx 기반 로케일 무관 판정·종료 코드 보존·시도별 결과 보존, Red 증빙(빌드 성공·전원 실패) 필수, 재작업 전 03_qa/Src 무효화·회귀 실패 롤백, 승격 절차·ProjectReference 옵션, 에이전트 tools 명시·SendMessage/claim 제거, 패키지 버전 정렬(17.14.1/2.9.3/3.1.4), 예시 오류 교정(Assert.Multiple·Dictionary.GetOrAdd·정수 나눗셈·checked 리팩토링) | tdd-orchestrator·tdd-analyst/builder/qa·tdd 단계 스킬 3종·harness-evolve·.codex/agents toml | plan/harness_cross_check_0913.md TDD 절 16건 |

## 하네스: 문서화 (Documentation Harness)

| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-09-23 | 초기 구성: `doc-harness/`(TypeScript, Node 24) — `claude -p --json-schema` 읽기 전용 세션 어댑터(`--setting-sources user`로 프로젝트 훅 차단), Run 트랜잭션(staging → 검증 → 원자 교체 → baseline), 해시+git 변경 감지 → LLM 분류 → 결정적 영향 분석(depgraph) → 기능 델타, Phase 01~07 프롬프트·스키마, 섹션 앵커 렌더러(템플릿 문서 + 서술 문서 + 다이어그램 UNCHANGED/UPDATED 판정·수동 수정 보존), 결정적 검증(참조·Mermaid 3층(jsdom 파서)·교차 일관성) + LLM 검증·일관성 + 수정 루프, CLI(run/status/verify/resume/report/clean), `doc-harness` 스킬·CLAUDE.md 절·미러 | doc-harness/ · .claude/skills/doc-harness · .agents/skills/doc-harness · CLAUDE.md · AGENTS.md · docs/harness.md · README.md · .gitignore | 원 개발자 없이도 이해·실행·디버깅·수정할 수 있는 문서를 코드 근거로 생성하고 `문서화` 한마디로 증분 유지하기 위해(설계 `docs/superpowers/specs/2026-09-23-doc-harness-design.md`). 구현 중 잡은 결함: 워커 풀 동시 쓰기의 tmp 파일명 충돌·Windows rename 경합(상태 저장 직렬화+재시도), 템플릿 문서 다이어그램이 판정 목록에서 빠짐, 관련 문서 크기 상한이 작은 문서를 버림 |
