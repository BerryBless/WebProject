# 개발 하네스

이 저장소는 `ClaudeCodeStudy`의 AI 협업 하네스를 이식해 시작했습니다. 설계와 구현은 Claude Code가 하고 OpenAI Codex CLI가 read-only로 교차 검증합니다. 제품 코드와 무관한 개발 도구이므로 배포물에는 들어가지 않습니다.

관련 문서: [진행 기록](history.md) · [프로젝트 규칙 `CLAUDE.md`](../CLAUDE.md) · [변경 이력](../plan/harness_changelog.md)

## 구성

| 구성 | 위치 | 역할 |
|---|---|---|
| Claude 에이전트 25종 | `.claude/agents/` | 코드 리뷰·동시성·GC·파이프라인·TDD·교차 검증 전문 에이전트 |
| Claude 스킬 26종 | `.claude/skills/` | `/commitandpush`, `code-review-orchestrator`, `cross-verify`, `codex` 등 |
| Codex 미러 | `.agents/skills/`, `.codex/` | Codex CLI가 같은 규칙을 읽도록 한 미러(`cross-verify`·`codex`는 자기 호출 재귀를 막으려고 의도적으로 제외) |
| Stop 훅 자동 커밋 | `.claude/settings.json`, `scripts/auto-commit.ps1` | 턴 종료 시 `.git/auto_commit_msg.txt`를 읽어 커밋·푸시 |
| 커밋 메시지 훅 | `scripts/git-hooks/commit-msg` | `{접두사}: {제목}` 형식 강제(클론 후 `.git/hooks/`에 복사) |
| 쓰기 범위 훅 | `scripts/hooks/guard-write-scope.ps1` | 감사·리뷰 전용 에이전트가 자기 작업 디렉터리 밖에 쓰지 못하게 차단 |
| 하네스 감사 | `scripts/harness-audit.ps1` | 에이전트·스킬·미러 구조 8개 항목 검사(프론트매터, 참조 실존, 절대 경로, 미러 동기화, 서명, 쓰기 범위 훅) |
| CI | `.github/workflows/ci.yml` | push·PR마다 `test`·`web`·`web-e2e` |
| 문서화 하네스 | `doc-harness/`, `.claude/skills/doc-harness` | `문서화` 한마디로 `docs/generated/`를 생성·증분 갱신하는 다단계 Claude 파이프라인(읽기 전용 자식 세션, Run 트랜잭션, Mermaid 검증). 상세는 [doc-harness/README.md](../doc-harness/README.md) |

하네스 파일을 고치면 `pwsh scripts/harness-audit.ps1`로 **PASS 8/8**을 확인합니다. `CLAUDE.md`·`.claude/skills/`를 고치면 `AGENTS.md`·`.agents/skills/` 미러도 함께 갱신합니다.

## 자동 커밋의 안전장치

Stop 훅은 턴이 끝날 때 남은 변경을 커밋하는 **수동 안전망**입니다. 그대로 두면 위험하므로 세 겹을 겁니다.

- **민감 파일 필터**: `.env*`, 키 파일, `secrets.json`, `appsettings.Production.json` 등은 커밋 대상에서 제외합니다.
- **스테이지 diff 내용 스캔**: 개인키·클라우드 키·비밀번호 리터럴·연결 문자열 패턴이 보이면 막습니다(자리표시자 단어가 있는 줄은 통과).
- **센티널**: 파이프라인이 실행 중일 때는 `.git/harness_commit_in_progress`를 두어 훅이 중간 상태를 커밋하지 못하게 합니다. 실행이 끝나거나 중단을 확정하면 지웁니다.

차단·실패·push 실패는 사용자에게 알리고 훅 자체는 항상 성공으로 끝납니다(작업을 막지 않기 위해).

## 오케스트레이터

각 하네스는 여러 전문 에이전트를 병렬로 돌리고 단일 리포트로 통합합니다. 산출물은 `_workspace/<하네스명>/` 아래에만 씁니다.

| 하네스 | 트리거 | 하는 일 |
|---|---|---|
| 종합 코드 리뷰 | 코드 리뷰, PR 검토 | 아키텍처·보안·성능·스타일 4개 에이전트 병렬 감사 → 통합 리포트 |
| 동시성 가드 | 락 감사, 데드락 분석 | Lock-Free 설계 강제, 락 정당화 주석 감사, 데드락 정적 분석 |
| GC 가드 | 힙 할당 감사, 메모리 최적화 | 할당 스캐너·풀링 강제자 병렬 감사 → 교차 검증 |
| 파이프라인 아키텍처 | Pipelines 설계, 디스패처 설계 | Zero-copy IO 루프와 `Channel<T>` 디스패처 설계 + 부하 테스트 감사 |
| TDD | TDD, Red-Green-Refactor | 실패 테스트 → 최소 구현 → 검증·리팩토링 완주 |
| Git 자동화 | `/commitandpush`, 커밋해줘 | 보안 감사 → 한국어 커밋 메시지 생성 → 커밋·푸시 |
| Codex 협업 | codex, 세컨드 오피니언 | `codex exec`를 세컨드 오피니언·병렬 작업자로 호출 |
| 교차 검증 개발 | "코덱스 교차 검증으로 구현" | Claude와 Codex가 독립 판단 → 상호 검증 → 근거 기반 조정. Codex는 검증 전담(read-only), 구현은 Claude 전담 |
| 문서화 | `문서화`, `문서화 전체`, `문서화 상태`, `문서화 검증` | Inventory → Architecture → 기능 발견·심층 분석 → Data/API → 실패 이력 → 횡단 분석 → 문서 생성 → Verification 루프. 최초는 전체, 이후는 baseline 대비 변경분만. 산출물은 `doc-harness/workspace/`와 `docs/generated/`에만 쓴다 |

Codex 산출물은 `*.meta.json`(status=success)으로 실행을 증빙합니다 — **Codex를 실행하지 않고 "교차 검증 완료"라고 보고하는 것은 금지**입니다.

## 기능 개발 흐름

기능 작업은 superpowers 플러그인의 흐름을 따릅니다.

1. **brainstorming** — 제품 방향을 사용자와 정합니다(여기서는 질문합니다).
2. **writing-plans** — 스파이크로 **먼저 측정하고** 계획을 씁니다. 계획에는 실제로 돌려 본 코드가 통째로 들어갑니다("나중에 구현"·"적절한 오류 처리" 같은 자리표시자 금지).
3. **subagent-driven-development** — 작업마다 새 구현자 에이전트를 띄우고, 매번 리뷰어가 **계획 자체를 의심하며** 감사합니다. 진행은 ledger 파일에 기록해 세션이 끊겨도 같은 지점에서 잇습니다.
4. 최종 전체 리뷰(실제 호스트·실제 스택 공격) → PR → CI → squash 병합 → 보고서.

실행 단계에서는 사용자에게 묻지 않고 추천안으로 끝까지 가되, 내린 결정은 전부 보고서의 **"내린 판정"** 표(결정 / 이유 / 틀렸을 때의 비용)에 남깁니다.
