# 하네스 전수조사·수정 리포트 (2026-09-11)

## 1. 배경 및 목적

`WebProject`는 `ClaudeCodeStudy`의 하네스(에이전트 25·스킬 26·Stop 훅·CI·Codex 미러)를 복사해 시작했다. 이식 직후 `settings.json`이 JSON 이스케이프 오류로 로드되지 않는 문제를 고친 뒤, 사용자 요청으로 **모든 에이전트와 스킬이 이 저장소에서 실제로 동작하는지 전수조사**하고 발견된 결함을 전부 수정했다. 함께 요청된 C# 웹 프로젝트용 `.gitignore` 정비도 포함한다.

조사 방법: Explore 에이전트 3개의 읽기 전용 정적 감사 → 결함 목록 확정 → 일괄 수정 → 재감사 스크립트 작성 → 오케스트레이터 5종 실제 실행으로 동적 검증.

## 2. 설계 결정

| 결정 | 채택 | 대안 | 이유 |
|------|------|------|------|
| 팀 도구 부재 대응 | Agent 팬아웃(단일 메시지 다중 호출) + 완료 알림 + SendMessage | TeamCreate 계열이 생길 때까지 대기 | 이 빌드에 TeamCreate/TaskCreate/TaskGet/TeamDelete 가 없음을 ToolSearch로 확인. 팬아웃 방식은 이번 세션에서 18개 에이전트 실행으로 검증됨 |
| git 에이전트 등록 | 프론트매터 추가 + `subagent_type` 실명 호출 | `skills/commitandpush/references/` 로 이동 | 다른 22개 에이전트와 호출 방식을 통일. 등록 후 세션에서 실제 에이전트 타입으로 노출됨 확인 |
| 감사 전용 에이전트 권한 | `tools:` 에서 `Edit` 만 제외 | 전면 read-only | 리뷰어는 `_workspace/*.json` 을 Write 해야 하므로 Write 는 유지. 소스 인플레이스 수정만 차단 |
| 커밋 주체 | `commitandpush` 스킬(능동) + Stop 훅(안전망) 공존 | 한쪽 폐기 | 스킬이 먼저 커밋하면 훅은 변경 없음으로 종료. 충돌하지 않으므로 둘 다 정본으로 문서화 |
| `.gitignore` | `dotnet new gitignore` 공식 템플릿 + 커스텀 블록 | 기존 Rider 전용 파일에 규칙 추가 | VS·Rider·NuGet·테스트 산출물을 한 번에 커버. 커스텀 블록(`_work*/`, `_workspace/cross/` 재포함)은 그대로 유지 |
| 서명 | `Claude Fable 5.1` 로 통일, 미러는 `Codex <noreply@openai.com>` | 모델명 제거 | 현재 세션 아이덴티티와 일치. 미러 잡종 서명 제거 |
| CI 테스트 게이트 | `WebProject.Api.Tests`(xUnit + Mvc.Testing) 추가 | 테스트 없이 유지 | `dotnet test` 가 실제로 검사할 대상이 생김 |

## 3. 감사 결과 (정적)

| # | 심각도 | 결함 | 수정 |
|---|---|---|---|
| F1 | 치명 | 오케스트레이터 5종이 미존재 팀 도구에 의존 | Phase 2~5를 Agent 팬아웃·완료 알림 방식으로 재작성, `pipeline-supervisor.md` TaskGet 참조 제거, 워커 20개에 "리더 ID 미상 시 최종 응답으로 보고" 추가 |
| F2 | 치명 | git 에이전트 3개 프론트매터 없음 → 미등록 | 프론트매터(name/description/tools) 추가, `commitandpush` 가 실명 `subagent_type` 으로 호출 |
| F3 | 높음 | `E:/project/dotnet_study/...` 절대경로 | `cd "$CLAUDE_PROJECT_DIR"` + 상대경로로 교체 |
| F4 | 높음 | Codex 미러에 `codex` 스킬 존재(재귀 위험) | 디렉터리 삭제, 미러 재동기화(24개) |
| F5 | 높음 | 미러 잡종 서명 `Codex Sonnet 4.6 <noreply@anthropic.com>` | `Codex <noreply@openai.com>` 으로 통일(10곳) |
| F6 | 높음 | `.gitignore` 에 VS 규칙 없음, `.vs/`·`*.user` 이미 커밋됨 | 공식 템플릿 재생성 + `git rm --cached` 8개 파일 |
| F7 | 중 | git 에이전트의 `references/*.md` 상대참조 깨짐 | 프로젝트 루트 기준 경로로 수정 |
| F8 | 중 | 커밋 주체 이중화 정본 미정 | CLAUDE.md 에 정책 명문화, push-controller 가 커밋 후 `.git/auto_commit_msg.txt` 삭제 |
| F9 | 중 | CI `dotnet test` 대상 없음 | `WebProject.Api.Tests` 추가, `/weatherforecast` 통합 테스트 1건 |
| F10 | 중 | AGENTS.md 축약 누락 | 루트 자동 인식·훅 설치 명령·구성 절 보강 |
| F11 | 낮 | 감사 에이전트 `tools:` 제한 없음 | 12개 에이전트에 Edit 제외 화이트리스트 |
| F12 | 낮 | 서명 `Claude Sonnet 4.6` 노후화 | `Claude Fable 5.1` 로 통일(5개 파일) |
| F13 | 낮 | superpowers 플러그인 이중 등록 | `superpowers@superpowers-marketplace` 제거 |
| F14 | 높음 (실행 중 발견) | TDD `TddSession.csproj` 템플릿이 SDK 기본 글로빙을 끄지 않아 명시 Compile 과 중복(NETSDK1022), 같은 폴더의 다른 산출 .cs 까지 컴파일 | `EnableDefaultCompileItems=false` 추가 |
| F15 | 높음 (실행 중 발견) | 같은 템플릿에서 builder 단계 `Compile Remove` 가 무조건이고 빈 `03_qa/Src` 에 `Exists()` 가 참 → 스텁 미컴파일(CS0103) | 실제 .cs 존재 여부(`@(_BuilderSrc)`, `@(_QaSrc)`) 조건으로 교체 |

정상 확인 항목: 22개 에이전트 name=파일명, 참조 스킬 18종 실존, 스폰 에이전트 이름 전부 실존, `invoke-codex.ps1` 실존, commit-msg 훅 설치됨, 에이전트 파일에 절대경로 없음.

## 4. 동적 검증 — 오케스트레이터 5종 실행

대상: `WebProject.Api/Program.cs` + `WebProject.Api.Tests/WeatherForecastEndpointTests.cs` (템플릿 수준 코드라 결과 자체보다 파이프라인 완주 여부가 목적)

| 하네스 | 에이전트 | 결과 | 산출물 |
|---|---|---|---|
| code-review-orchestrator | 4 (병렬) | 종합 89점, APPROVE. Medium 3(핸들러 앰비언트 의존, 매직 넘버 중복, 커버리지 갭) / Low 17 | `_workspace/03_consolidated_report.md`, `02_*_findings.json` ×4 |
| concurrency-guard-orchestrator | 4 (2 병렬 + 2 순차) | 100점, APPROVE. 락·동기 블로킹 0건, 기각 항목 6건 검증자 재확인 | `_workspace/04_concurrency_guard_report.md` |
| gc-guard-orchestrator | 3 (2 병렬 + 1 순차) | 최종 91점, APPROVE. 확정 1(LINQ 이터레이터) / 하향 2 / 기각 1, FP 20% | `_workspace/04_gc_guard_report.md`, `03_peer_review.json` |
| tdd-orchestrator | 3 (순차) + harness-evolve | Red 16 케이스 실패 → Green 16/16 (재작업 0) → Refactor 6건 적용 후 회귀 16/16 PASS | `_workspace/01_analyst/`, `02_builder/`, `03_qa/`, `04_evolution/evolution_report.md` |
| pipeline-architect-orchestrator | 1 감독자 + 3 중첩 워커 | APPROVE. CRITICAL 0 / HIGH 2 / MEDIUM 4 / LOW 6, 워커 재작업 0회. 감독자 1회 재호출(API 한도) | `_workspace/02_interface_contract.cs`, `02_io_loop/IoLoop.cs`(915줄), `02_dispatcher/ThreadDispatcher.cs`(519줄), `03_load_test_audit.md`, `04_pipeline_architecture.md` |

실행 중 확인된 하네스 동작 특성:
- 워커가 "리더에게 SendMessage" 를 시도했지만 리더 ID를 모르는 경우가 반복됨 → 워커 20개에 최종 응답 보고 규칙 추가(F1 수정에 포함).
- 병렬 에이전트 간 SendMessage 공유(necessary_locks, buffer_allocations)는 상대가 이미 종료된 경우 도달하지 않음 → 리더가 통합 시 직접 대조하도록 스킬에 명시.
- 순차 생성-검증(analyzer→reviewer, builder→qa)은 완료 알림 후 다음 Agent 호출로 정상 동작.

## 5. 파이프라인 아키텍처 하네스 결과

브리프: TCP 에코 서버(길이 접두사 4B LE + 페이로드), 동시 연결 10,000, 200,000 msg/s, 최대 64 KB, p99 2 ms, 정상 경로 Zero-allocation.

**실행 경과**
- 감독자가 인터페이스 계약(`02_interface_contract.cs`)을 작성하고 io-loop-designer·thread-dispatcher-designer 를 단일 메시지로 동시 호출. 두 워커 모두 산출물 완성.
- 첫 감독자 실행이 품질 게이트 직전 세션 사용량 한도(HTTP 429)로 중단됨 → 스킬 에러 규칙대로 1회 재호출하되 기존 산출물부터 이어가도록 지시. 재호출은 워커를 다시 만들지 않고 게이트·감사·문서화만 수행.
- 품질 게이트: PipeWriter/Reader Complete 전 종료 경로, `AdvanceTo(consumed, examined)` 매 ReadAsync 1회, 계약 준수, Channel.Writer.Complete, 클로저-프리 Work Item, 백프레셔 전 항목 합격. 보조 근거로 세 파일을 저장소 밖 임시 프로젝트로 net10.0 빌드 시 경고 0·오류 0.
- load-test-auditor 판정 **APPROVE** (CRITICAL 0). 메모리 폭증·영구 대기·프로세스 행을 유발하는 결함 없음.

**미해결 항목 (다음 사이클, `04_pipeline_architecture.md` 7절에 담당·지침 기록)**
| 등급 | 항목 | 위치 | 담당 |
|---|---|---|---|
| HIGH | H-1 송신 타임아웃 부재 — 응답을 읽지 않는 클라이언트가 워커 1개를 HOL 정지 | `IoLoop.cs:568-661` | io-loop-designer |
| HIGH | H-2 유휴 연결이 4 KB 수신 세그먼트를 상시 보유·핀 (zero-byte read 미적용) | `IoLoop.cs:291-297` | io-loop-designer |
| MEDIUM | M-1 DisposeAsync 진입 경합, M-2/M-3 연결당 메모리·ArrayPool 버킷 한도(계약 개정 필요), M-4 MaxDrainPerTurn 기본값 | 계약·IoLoop·Dispatcher | 감독자 중재 |

**하네스 관점 결론**: 감독자 패턴의 Agent 중첩 호출(오케스트레이터 → 감독자 → 워커 3개)이 이 빌드에서 동작함을 확인. 단, 감독자 1회 실행이 약 13만 토큰·16분으로 5종 중 가장 무거우며, 세션 한도에 걸리면 산출물 재사용 재호출이 필요하다. 재호출 프롬프트 패턴("기존 산출물 이어가기")을 `pipeline-architect-orchestrator` 에러 핸들링에 추가할 것을 권고.

## 6. 변경 파일 목록

| 구분 | 파일 |
|---|---|
| 신규 | `.gitignore`(재생성), `scripts/harness-audit.ps1`, `WebProject.Api.Tests/`(csproj + `WeatherForecastEndpointTests.cs`), `plan/harness_audit_0911.md` |
| 수정 | 오케스트레이터 SKILL.md 5개, `tdd-refactor-phase/SKILL.md`, `commitandpush/SKILL.md` + `references/commit-message-guide.md`, `.claude/agents/*.md` 24개, `scripts/auto-commit.ps1`, `CLAUDE.md`, `AGENTS.md`, `.claude/settings.json`, `WebProject.Api/Program.cs`(`public partial class Program`), `WebProject.sln` |
| 삭제 | `.agents/skills/codex/`, 인덱스에서 `.vs/` 7개·`WebProject.Api.csproj.user` |
| 미러 | `.agents/skills/` 24개 재동기화(commitandpush 는 Codex 서명) |

## 7. 빌드 검증

```powershell
pwsh scripts/harness-audit.ps1        # 7/7 PASS, exit 0
dotnet build WebProject.sln -c Release # 경고 0, 오류 0
dotnet test WebProject.sln -c Release  # 통과 1 / 실패 0
git status --ignored                   # .vs/, *.user, _workspace/ 가 !! (무시)
```

`scripts/harness-audit.ps1` 검사 항목: ① 에이전트 프론트매터·name=파일명 ② 참조 스킬 실존 ③ 스폰 에이전트 실존(`subagent_type=` / `agent_type:` / `Agent(name,` 세 표기) ④ 절대경로 없음 ⑤ 미존재 팀 도구 참조 없음 ⑥ 미러 = `.claude/skills` − {cross-verify, codex}, 내용 동일 ⑦ 서명 일관성. 하네스 파일을 고치면 실행해 PASS 를 확인한다.

## 8. 향후 확장 포인트

1. **코드 리뷰 지적 반영**: `TemperatureF` 공식 부정확(`/ 0.5556` → `* 9 / 5`), 난수 상한 경계 불일치, 핸들러의 `TimeProvider` 주입 분리. 다음 TDD 사이클 후보.
2. **harness-audit.ps1 을 CI 에 추가**: `ci.yml` 에 `pwsh scripts/harness-audit.ps1` 스텝을 넣어 하네스 드리프트를 PR 단계에서 차단.
3. **TDD 산출물 승격 경로**: `_workspace/03_qa/Src` 의 최종 코드를 실제 프로젝트로 옮기는 절차가 스킬에 없음. `tdd-orchestrator` Phase 5 에 "승격 여부 사용자 확인" 추가 검토.
4. **cross-verify 하네스 미실행**: Codex CLI 0.154.0 이 설치되어 있으나 이번 조사에서는 실행하지 않았다. 사용자 요청 시 `코덱스 교차 검증` 키워드로 별도 검증.
5. **Stop 훅 안전장치**: 훅이 `git add -A` 로 전부 커밋하므로 새 생성물 폴더가 생기면 `.gitignore` 를 먼저 갱신해야 한다(이번 `.vs/` 사고의 원인). 훅에 `git status --porcelain | grep -E '\.vs/|bin/|obj/'` 차단 규칙 추가 검토.
