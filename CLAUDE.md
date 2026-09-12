# WebProject 프로젝트

## 프로젝트 개요

**목표:** (작성 예정) — 이 저장소는 `ClaudeCodeStudy`의 하네스 구성(에이전트·스킬·훅·CI·Codex 협업)을 그대로 이식해 시작한 새 솔루션이다. 솔루션 이름은 언제든 바뀔 수 있으므로 하네스 스크립트는 저장소 루트를 자동 인식한다(`CLAUDE_PROJECT_DIR` → 스크립트 위치 순).

**구성(2026-09-11):** `WebProject.sln`(.NET 10) 아래 두 프로젝트가 있다. 프로젝트를 추가하면 이 절과 CI(`.github/workflows/ci.yml`)를 함께 갱신할 것.
- `WebProject.Api` — ASP.NET Core 최소 API(`Microsoft.NET.Sdk.Web`). 통합 테스트 접근용으로 `Program`을 `public partial`로 노출한다.
- `WebProject.Api.Tests` — xUnit + `Microsoft.AspNetCore.Mvc.Testing`. CI의 `dotnet test` 게이트가 실제로 검사하는 대상이다.

**하네스 검증:** `pwsh scripts/harness-audit.ps1` 이 에이전트·스킬·미러 구조를 8개 항목으로 검사한다(프론트매터, 참조 실존, 절대경로, 팀 도구, 미러 동기화·Codex 에이전트 재귀, 서명, 쓰기 범위 훅). 하네스 파일을 고치면 실행해 PASS를 확인할 것. 감사 결과와 수정 이력은 `plan/harness_audit_0911.md` 참조.

**경로 규칙:** 절대 경로(`E:\project\...`)를 설정·스크립트에 하드코딩하지 않는다. Stop 훅은 `$env:CLAUDE_PROJECT_DIR`, PowerShell 스크립트는 `$PSScriptRoot` 기준으로 루트를 계산한다.

**작업 디렉토리 규칙:** 하네스 산출물은 `_workspace/<하네스명>/` 하위에만 쓴다(code-review·gc-guard·concurrency-guard·pipeline·tdd·git·cross). 새 실행 시 자기 하위 디렉토리만 `_workspace/<하네스명>_{타임스탬프}/`로 보관 이동하고, `_workspace/` 루트나 다른 하네스 디렉토리는 건드리지 않는다. 예외: `cross`는 git 추적 대상이라 **보관 이동하지 않고** run_id 하위 디렉토리를 누적한다(이동하면 추적 기록이 삭제로 커밋됨). `.gitignore`의 `_workspace/*` 규칙으로 `cross/` 외에는 커밋되지 않는다.

**쓰기 범위 훅:** 감사·리뷰 전용 서브에이전트 24종(구현 역할 `cross-implementer` 제외)은 `scripts/hooks/guard-write-scope.ps1` PreToolUse 훅으로 Write/Edit 대상이 자기 하네스 디렉터리(`_workspace/<하네스명>/`) 밖이면 거부된다. 1차 방어선은 각 에이전트 프론트매터의 `hooks:`(`-Allow <접두사>`), 2차 방어선은 `.claude/settings.json`의 프로젝트 훅(`-Mode map`, 훅 입력의 `agent_type`으로 판별. 메인 세션·구현 에이전트는 통과). **두 훅 모두 세션 시작 시 읽히므로 변경 후 세션을 재시작해야 적용된다.** 적용 확인은 감사·리뷰 에이전트에게 `_workspace/<하네스>/probe/` 와 `WebProject.Api/` 에 각각 Write 를 시도하게 해 후자만 거부되는지 본다.



**Git 훅:** `scripts/git-hooks/commit-msg`가 커밋 메시지 접두사 형식을 강제한다. 새로 클론하면 `Copy-Item scripts/git-hooks/commit-msg .git/hooks/`로 설치할 것.

**.gitignore:** `dotnet new gitignore` 공식 템플릿 + 프로젝트 커스텀 블록(Rider, `_work*/`, `_workspace/cross/` 재포함). Stop 훅이 `git add -A`로 전부 커밋하므로(민감 파일·50MB 초과·비밀값 내용은 차단) 새 생성물 폴더가 생기면 커밋 전에 규칙을 먼저 추가할 것.

## 하네스: Git 자동 커밋 & 푸시 (Git Automator)

**목표:** 보안 검증 → 한국어 커밋 메시지 자동 생성 → 안전한 커밋 & 푸시를 파이프라인으로 자동화한다.

**트리거:** `/commitandpush`, 커밋해줘, 푸시해줘, 변경사항 올려줘, 깃 커밋 요청 시 `commitandpush` 스킬을 사용하라.

**자동 커밋 메시지 전달 (필수 행동 규칙):**
코드·파일 변경을 완료하고 턴을 마치기 직전, WHY 중심 한국어 커밋 메시지를 **`.git/auto_commit_msg.txt`** 에 UTF-8로 작성한다.
- 형식: `{접두사}: {제목}` (접두사: 추가/수정/버그수정/리팩토링/문서/테스트/의존성)
- 제목: 50자 이내, 파일명 나열 금지, WHY 중심
- 본문(선택): `- ` 항목 나열
- 마지막 줄(필수): `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`

Stop 훅(`auto-commit.ps1`)이 이 파일을 **최상단에서 읽고 즉시 삭제**한 뒤 커밋한다(어떤 조기 종료 경로에서도 잔류하지 않음). 파일을 남기지 않으면 `{접두사}: 자동 커밋(메시지 미전달) — N개 파일 변경` 폴백으로 커밋된다(안전망). 커밋 실패 시 메시지는 `.git/auto_commit_msg.failed.txt`에 보존되어 다음 턴에 재사용된다.
- **이 턴에 `commitandpush` 스킬이 이미 커밋했으면 파일을 쓰지 않는다.**
- 훅은 `.git/harness_commit_in_progress` 센티널(6시간 이내)이 있으면 커밋을 건너뛴다. 사용자 확인으로 턴을 끝내야 하는 파이프라인(commitandpush·cross-verify)이 만들고 끝날 때 지운다.
- 훅은 파일별 민감 파일 필터(`.env*`, 키 파일, `secrets.json`, `appsettings.Production.json` 등)와 스테이지 diff 내용 스캔(개인키·클라우드 키·`"Password": "…"`·연결 문자열)을 통과한 변경만 커밋하며, 차단·실패·push 실패는 `systemMessage`로 알린다(항상 exit 0).

**커밋 주체 정책:** 커밋 경로는 두 가지이며 충돌하지 않는다.
- `commitandpush` 스킬 — 사용자가 명시 요청했을 때 보안 감사 → 메시지 작성 → 즉시 커밋·푸시하는 **능동 경로**. 시작 시 `.git/harness_commit_in_progress` 센티널을 만들어 훅을 잠그고, 끝날 때 센티널과 `.git/auto_commit_msg.txt`를 삭제한다. 사용자 확인은 보안 WARN 등 꼭 필요한 경우에만 묻는다.
- Stop 훅 — 턴 종료 시 남은 변경을 커밋하는 **수동 안전망**. 스킬이 먼저 커밋했으면 변경 없음으로 종료한다.

**변경 이력:**
| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-06-03 | 초기 구성 | 전체 | Git 자동 커밋&푸시 파이프라인 구축 |
| 2026-06-03 | 파일 기반 메시지 전달로 재설계 | auto-commit.ps1 | nested claude -p 콜드스타트/stdin 취약성으로 폴백 빈발 |
| 2026-09-11 | git 에이전트 3개 프론트매터 등록·실명 호출, 커밋 주체 정책 명문화, 서명 Fable 5.1 통일 | git-*.md·commitandpush·auto-commit.ps1 | 하네스 전수조사: 프론트매터 없어 서브에이전트 미등록, 커밋 경로 이중화 정본 미정 |
| 2026-09-12 | 산출물 경로를 `_workspace/git/` 하위로 격리 | commitandpush·git-*.md | 모든 하네스가 `_workspace/` 루트를 공유해 산출물 파일명이 충돌하고 Phase 0 전체 이동이 타 하네스 산출물을 파괴(code-review 하네스 교차 검토에서 확인) |
| 2026-09-13 | Claude↔Codex 교차 점검 반영: Stop 훅 재작성(메시지 파일 선소비·실패 시 보존, 센티널로 파이프라인 중 커밋 차단, 잠금 디렉터리로 동시 실행 배제, 파일별 민감 필터+내용 스캔, 50MB 가드, 종료 코드 검사, push 실패 노출, 항상 exit 0), 스킬 run_dir·해시 resume·질문 최소화·push-only 모드, 에이전트 PASS/WARN/FAIL 계약·`-F` 커밋·실패 후 amend 금지·needs_confirmation 반환, security-patterns 정본화(JSON 키·연결 문자열·GUID 제거·주석/예시 실제값 FAIL), `.gitignore` `.env.*`, settings 훅 옵션, Codex toml 잡종 서명·경로 교정 | auto-commit.ps1·commitandpush·references 2종·git-*.md·settings.json·.gitignore·.codex/agents | plan/harness_cross_check_0913.md Git 절 19건 |

---

## 플랜 문서화 규칙

기능 설계나 아키텍처 결정이 완료되면 `plan/` 디렉토리에 설계 문서를 작성한다.

### 파일 명명 규칙
```
plan/<기능명>_<MMDD>.md
예) plan/packet_serialization_0602.md
    plan/rudp_channel_0603.md
    plan/rpc_generator_0610.md
```

### 문서 필수 포함 항목
1. **배경 및 목적** — 왜 이 기능이 필요한가, 어떤 문제를 해결하는가
2. **설계 결정** — 채택한 방식과 후보 대안 비교 (표 형식 권장)
3. **컴포넌트 구조** — 디렉토리 트리, 의존 관계 다이어그램
4. **핵심 API** — 주요 사용 패턴 코드 예시
5. **변경 파일 목록** — 신규/수정 파일과 내용 요약
6. **빌드 검증** — 실행 명령어
7. **향후 확장 포인트** — 다음 사이클 추천 항목

### 현재 플랜 문서 목록

| 파일 | 날짜 | 내용 |
|------|------|------|
| plan/harness_audit_0911.md | 2026-09-11 | 하네스 전수조사 결과(F1~F13), 수정 내역, 오케스트레이터 5종 실행 검증, 재감사 스크립트 |
| plan/code_review_harness_fix_0912.md | 2026-09-12 | 종합 코드 리뷰 하네스 Claude↔Codex 교차 검토 결과 16건, 설계 결정(run_dir 격리·점수 산식·판정 순서), 변경 파일, 실전 검증(96점 APPROVE), 후속 과제 처리(전 하네스 _workspace 격리) |
| plan/gc_guard_harness_fix_0912.md | 2026-09-12 | GC 가드 하네스 Claude↔Codex 교차 점검 20건, 설계 결정(독립 병렬+피어 정본, 공통 finding 스키마, 점수·판정), .NET 기술 오답 교정 목록, 변경 파일, 검증 |
| plan/harness_cross_check_0913.md | 2026-09-13 | 나머지 하네스 5종(동시성·파이프라인·TDD·Git·cross-verify) Claude↔Codex 교차 점검 결함표(합집합 96건), 공통 결함 6종, 권장 순서대로 5종 전부 수정 적용·검증(6절) |

---

## 인터페이스 및 API 문서화(주석) 규칙

모든 인터페이스, public 클래스의 메서드, 대리자(Delegate), RPC 정의 코드를 생성하거나 수정할 때는 반드시 표준 XML 문서 주석(C# `///`)을 매우 상세히 작성해야 한다. 단순 기능 설명을 넘어 **고성능 시스템 프로그래밍 관점의 제약 조건**을 주석에 반드시 포함할 것.

### 주석 필수 포함 항목 (`<remarks>` 활용)

- **Thread Safety:** `Thread-safe` 또는 `Not Thread-safe` 명시. 콜백이면 어느 스레드 컨텍스트(I/O Thread, 호출 스레드 등)에서 실행되는지 명시.
- **Memory Allocation:** 힙 할당 발생 여부(`Zero-allocation guaranteed` 혹은 내부 할당량 명시). `ReadOnlySpan<byte>` / `ReadOnlyMemory<byte>` 버퍼의 **소유권(Ownership)과 생명주기** 명시.
- **Blocking 여부:** 즉시 반환인지, 동기 블로킹인지, 비동기(Non-blocking)인지 명시.

### 이상적인 주석 예시

```csharp
/// <summary>수신된 로우 패킷 버퍼를 역직렬화하여 내부 이벤트 파이프라인으로 라우팅합니다.</summary>
/// <param name="sessionId">패킷을 송신한 클라이언트 세션의 고유 식별자</param>
/// <param name="packetBuffer">수신된 원시 바이트 데이터 세그먼트</param>
/// <returns>패킷 라우팅 및 처리 성공 여부</returns>
/// <exception cref="InvalidPacketException">패킷 헤더가 손상되었거나 프로토콜 구조와 맞지 않을 때</exception>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> 고성능 네트워크 I/O 스레드 풀에서 직접 호출됩니다.
/// 내부에서 동기 블로킹(DB, File I/O)을 수행하면 전체 수신 루프가 정지됩니다.</description></item>
/// <item><description><b>Memory Policy:</b> <paramref name="packetBuffer"/> 소유권은 메서드 실행 동안만 유효합니다.
/// 반환 후에도 참조하려면 복사본을 생성해야 합니다.</description></item>
/// <item><description><b>Concurrency:</b> Thread-safe. 내부적으로 ConcurrentQueue 및 Interlocked로 락 경합을 최소화합니다.</description></item>
/// </list>
/// </remarks>
bool OnPacketReceived(long sessionId, ReadOnlySpan<byte> packetBuffer);
```

### 네트워크·메모리 관련 선언부 인라인 주석 규칙

네트워크 또는 메모리 관련 **함수·변수·필드를 선언할 때**는, 그것을 선택한 이유를 반드시 **해당 타입/API의 내부 동작**을 근거로 인라인 주석(`//`)으로 달아야 한다.

- 대상: `Socket`, `Pipe`, `PipeReader/Writer`, `Channel<T>`, `ArrayPool<T>`, `MemoryPool<T>`, `IMemoryOwner<T>`, `Memory<T>`, `Span<T>`, `NetworkStream`, `SocketAsyncEventArgs`, `ValueTask`, `SemaphoreSlim`, `ConcurrentQueue/Dictionary` 등 네트워크·메모리 관련 모든 타입의 선언
- 주석 내용: "왜 이 타입/API를 골랐는가" → 반드시 **내부 동작 메커니즘**을 이유로 삼을 것 (단순 기능 설명 금지)

**예시:**

```csharp
// Channel<T>: lock-free MPSC 큐로 구현되어 있어 다수 IO 스레드 → 단일 디스패처 경로에서 락 경합 없이 메시지를 전달
private readonly Channel<IPacket> _dispatchChannel = Channel.CreateUnbounded<IPacket>();

// ArrayPool<byte>.Shared: 고정 크기 버킷 풀로 TLS(Thread-Local Storage) 슬롯을 우선 확인하므로
// 동일 스레드에서 반환·재사용 시 힙 할당 없이 O(1) 반환
private readonly byte[] _recvBuffer = ArrayPool<byte>.Shared.Rent(4096);

// SemaphoreSlim: 커널 전환 없이 스핀-대기 후 관리형 대기로 전환하는 경량 세마포어.
// 짧은 임계 구간에서 Mutex보다 컨텍스트 스위치 비용이 낮아 고빈도 송신 제한에 적합
private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);
```

---

## 하네스: Codex 협업 (Claude ↔ OpenAI Codex CLI)

**목표:** Claude Code 세션 안에서 `codex exec`를 세컨드 오피니언·교차 검증·병렬 작업자로 호출한다. Codex는 `AGENTS.md`와 `.agents/skills/` 미러를 읽으므로 프로젝트 규칙이 자동 공유된다.

**트리거:** codex, 코덱스, codex에게 물어봐, 세컨드 오피니언, codex 리뷰, 교차 리뷰 요청 시 `codex` 스킬을 사용하라.

**동기화 규칙:** `CLAUDE.md`·`.claude/skills/`를 수정하면 `AGENTS.md`·`.agents/skills/` 미러도 함께 갱신할 것. 단, Git 하네스 섹션은 의도적으로 다르다 — Claude는 Stop 훅 파일 전달(`.git/auto_commit_msg.txt`), Codex는 직접 커밋(`Co-Authored-By: Codex <noreply@openai.com>`).

**변경 이력:**
| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-09-10 | 초기 구성 | codex 스킬·AGENTS.md | Codex CLI 병행 사용 + Claude→Codex 호출 워크플로 구축 |
| 2026-09-13 | argv 프롬프트 전달이 비TTY 툴에서 stdin 대기로 멈추는 문제 반영: 래퍼(invoke-codex.ps1) 사용을 1순위로, 직접 호출은 stdin(`-`) 파일 전달로 통일. `exec review` 대상 옵션(--uncommitted/--base/--commit) 명시, `resume --last` 대신 thread_id 재개, 사용량 한도 시 순차 실행·재시도 금지 | codex 스킬 | plan/harness_cross_check_0913.md cross 절 |

---

## 하네스: 교차 검증 개발 (Claude ↔ Codex Cross-Verify)

**목표:** Plan 단계와 최종 코드 리뷰 단계에서 Claude와 실제 Codex CLI가 독립 판단 → 상호 검증 → 근거 기반 조정을 거치는 개발 파이프라인. Codex는 검증 전담(read-only), 구현은 Claude 전담.

**트리거:** 요청에 '코덱스'/'Codex' 키워드가 명시된 교차 검증 요청(예: 코덱스 교차 검증으로 구현, Codex랑 같이 구현)에만 `cross-verify` 스킬을 사용하라. **코덱스/Codex 언급이 없는 '교차 검증'·'더블 체크' 요청에는 실행 금지.** 단발 Codex 질문·단독 리뷰는 `codex` 스킬.

**주의:** Codex 미실행 상태에서 "교차 검증 완료" 보고 금지. Codex 산출물은 `*.meta.json`(status=success) 증빙 필수. `cross-verify`·`codex` 스킬은 Claude 전용이므로 `.agents/skills/` 미러에서 제외한다(Codex 자기 호출 재귀 방지).

**변경 이력:**
| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-09-10 | 초기 구성 | cross-verify 스킬 + cross-planner/cross-implementer/cross-reviewer/codex-adapter 에이전트 | Plan·리뷰 교차 검증 파이프라인 구축 |
| 2026-09-10 | 트리거 조건 강화 — 코덱스/Codex 키워드 필수 | cross-verify description·CLAUDE.md | 키워드 없는 일반 검증 요청에 고비용 파이프라인이 오발동하지 않도록 사용자 요청 |
| 2026-09-10 | 토큰 부족 시 Claude 단독 폴백 추가 | invoke-codex.ps1(status=quota)·codex-adapter·cross-verify | Codex 사용량 한도로 파이프라인이 멈추는 대신 작업을 완료하고 "교차 검증 아님"을 명시 피드백하도록 사용자 요청 |
| 2026-09-11 | 미러에서 codex 스킬 제거, 미러 서명 Codex로 통일, cross-planner/reviewer tools 제한 | .agents/skills·cross-*.md | 하네스 전수조사: 미러 정책 위반(재귀 위험)·잡종 서명 발견 |
| 2026-09-13 | Claude↔Codex 교차 점검 반영: invoke-codex.ps1 재작성(상태 1회 확정·Write-Error 제거·시도별 로그·quota 판별 오류줄 한정·경로 절대화·인자 인용·--json·meta에 out/prompt sha256·thread_id·cmd·codex_version), 오케스트레이터가 meta 독립 재검증·독립성 `.log` 검사, Stop 훅 센티널·기준 커밋 확정 시점·diff에서 `_workspace/**` 제외·신규 파일 본문 포함·`33_diff` 재검토·`00_manifest.json` 해시·최고 접미사 규칙·수정 없음 경로·VERDICT 토큰 통일, 에이전트 tools 명시(SendMessage/Skill 제거)·첫 줄 JSON, `.codex/agents`에서 Claude 전용 4종 제거 + 감사 항목 추가, 프롬프트 독립성·규칙 축, 예시를 WebProject.Api로 | cross-verify·invoke-codex.ps1·prompts·usage·codex-adapter/cross-*.md·harness-audit.ps1 | plan/harness_cross_check_0913.md cross 절 18건 |

---

## 하네스: 종합 코드 리뷰

**목표:** 아키텍처·보안·성능·스타일 4개 에이전트가 병렬로 코드를 감사하고 단일 리포트로 통합한다.

**트리거:** 코드 리뷰, PR 검토, 코드 감사, 종합 리뷰 요청 시 `code-review-orchestrator` 스킬을 사용하라. 단순 질문(개념 설명 등)은 직접 응답 가능.

**변경 이력:**
| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-06-02 | 초기 구성 | 전체 | 종합 코드 리뷰 하네스 구축 |
| 2026-06-09 | 보안 가드 감사 | security-reviewer | 해킹·DDoS 공격 표면 점검 (리포트 plan/security_audit_0609.md) |
| 2026-09-11 | TeamCreate 의존 제거, Agent 팬아웃 방식으로 재작성, 리뷰어 tools 제한 | code-review-orchestrator·reviewer 4종 | 하네스 전수조사: 이 빌드에 팀 도구가 없어 실행 불가 |
| 2026-09-12 | 전용 run_dir(`_workspace/code-review/<run_id>/`) 격리, 팀 시대 프로토콜(SendMessage·claim) 제거, 기본 브랜치 빈 diff 폴백, 원본 diff 보존, 결정적 점수 산식·재정규화·판정 우선순위, JSON 구조 검증, 트리거 축소, 스킬 체크리스트 오류(레이어 그림·LINQ 예시·삭제 회귀·remarks 규칙) 교정 | code-review-orchestrator·reviewer 4종·review 스킬 4종·.codex/agents toml | Claude↔Codex 교차 검토(plan/code_review_harness_fix_0912.md): `_workspace/` 전체 이동이 타 하네스 산출물 파괴, 서브에이전트가 리더 ID 없이 SendMessage 시도, master에서 diff 0줄 등 16건 |
| 2026-09-13 | 쓰기 범위 훅 도입: `scripts/hooks/guard-write-scope.ps1`(PreToolUse, deny JSON), 감사·리뷰 에이전트 24종 프론트매터 `hooks:` + settings.json `-Mode map`(agent_type 판별), 감사 항목 8 추가 | guard-write-scope.ps1·에이전트 24종·settings.json·harness-audit.ps1 | plan/harness_cross_check_0913.md 미착수 항목 해소. 프론트매터 훅은 세션 시작 시 읽혀(CLI 문자열 확인) 이번 세션 프로브에서 미발동 — 재시작 후 검증 필요 |

---

## 하네스: 동시성 가드 (.NET 10 고성능 서버)

**목표:** Lock-Free 설계 강제·락 정당화 주석 감사·데드락 정적 분석(생성-검증)을 에이전트 팀으로 조율하고 단일 동시성 리포트를 생성한다.

**트리거:** 동시성 검사, 락 감사, 데드락 분석, Lock-Free 검증, async 데드락, 컨텐션 분석 요청 시 `concurrency-guard-orchestrator` 스킬을 사용하라.

**변경 이력:**
| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-06-02 | 초기 구성 | 전체 | .NET 10 고성능 서버 동시성 하네스 구축 |
| 2026-09-11 | TeamCreate 의존 제거, Agent 팬아웃+순차 생성-검증으로 재작성 | concurrency-guard-orchestrator | 하네스 전수조사: 팀 도구 미존재 |
| 2026-09-12 | 산출물 경로를 `_workspace/concurrency-guard/` 하위로 격리, 새 실행 시 자기 디렉토리만 보관 이동 | concurrency-guard-orchestrator·전용 스킬 4종·에이전트 4종 | 모든 하네스가 `_workspace/` 루트를 공유해 산출물 파일명이 충돌하고 Phase 0 전체 이동이 타 하네스 산출물을 파괴(code-review 하네스 교차 검토에서 확인) |
| 2026-09-13 | Claude↔Codex 교차 점검 반영: 전용 run_dir·meta 해시(analyzer 재실행 시 reviewer 필수 재실행), 저장소 루트 `combined_source.txt` 제거(케이스 B 헤더·줄번호 보존), 형제 SendMessage·claim·필요 락 공유 제거(독립 병렬 + 오케스트레이터 대조), `needs_reanalysis:bool`+`reanalysis_targets[]` 계약 통일, 공통 finding 스키마(id·context·necessary), modified 포함 중앙 점수·재정규화·판정 우선순위, deadlock-analyzer tools 명시, .NET 오답 교정(ASP.NET Core SynchronizationContext 없음→기아 분류, lock{await}=컴파일 오류, ConfigureAwait 구조 판정·library 한정, Allman lock 정규식, System.Threading.Lock/EnterScope, SemaphoreSlim(1,1) 공인 프리미티브, Channel/CD는 thread-safe≠Lock-Free, bool CAS→int, ABA 재정의), [LOCK-REQUIRED]와 <remarks> 병행 계약·remarks 정합성 검사 | concurrency-guard-orchestrator·에이전트 4종·스킬 4종·.codex/agents toml | plan/harness_cross_check_0913.md 동시성 절 21건 |

---

## 하네스: GC 가드 (.NET 10 메모리 최적화)

**목표:** 힙 할당 스캐너·풀링 강제자 병렬 감사 → 교차 검증으로 GC 압력 유발 패턴을 제거하고 ValueTask·Span·ArrayPool을 올바르게 적용한다.

**트리거:** GC 억제, 힙 할당 감사, 메모리 최적화, ArrayPool 검사, ValueTask 검증, boxing 탐지, GC 압력 분석 요청 시 `gc-guard-orchestrator` 스킬을 사용하라.

**변경 이력:**
| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-06-02 | 초기 구성 | 전체 | .NET 10 서버 GC 억제 메모리 최적화 하네스 구축 |
| 2026-09-11 | TeamCreate 의존 제거, Agent 팬아웃+순차 교차검증으로 재작성 | gc-guard-orchestrator | 하네스 전수조사: 팀 도구 미존재 |
| 2026-09-12 | 산출물 경로를 `_workspace/gc-guard/` 하위로 격리, 새 실행 시 자기 디렉토리만 보관 이동 | gc-guard-orchestrator·전용 스킬 3종·에이전트 3종 | 모든 하네스가 `_workspace/` 루트를 공유해 산출물 파일명이 충돌하고 Phase 0 전체 이동이 타 하네스 산출물을 파괴(code-review 하네스 교차 검토에서 확인) |
| 2026-09-12 | Codex 교차 점검 결과 반영: 전용 run_dir·해시 기반 부분 재실행(피어 리뷰 필수 재실행), 형제 간 SendMessage·claim 제거, pooling-enforcer tools 명시, 경로 모드 파일 헤더·줄번호 보존, 공통 finding 스키마(id·hot_path 3등급·necessary), modified 포함 중앙 점수 재계산·판정 우선순위·"분석 대상 없음" 상태, .NET 기술 오답 교정(캡처 없는 람다 캐싱, Count() 무할당, string.Format 박싱 전 버전, Split 동치, ArrayPool 풀 미스·소유권, stackalloc 조건, Task.FromResult 캐시, C# 13 Span·params span), fix_code에 CLAUDE.md 주석 규칙 | gc-guard-orchestrator·에이전트 3종·스킬 3종·.codex/agents toml | Claude↔Codex 교차 점검 20건(plan/gc_guard_harness_fix_0912.md) |

---

## 하네스: 파이프라인 아키텍처 (.NET 10 고성능 IO)

**목표:** System.IO.Pipelines 기반 Zero-copy IO 루프와 Channel<T> 락-프리 디스패처를 감독자 패턴으로 설계하고 부하 테스트 감사까지 수행한다.

**트리거:** Pipelines 설계, IO 루프 구현, 디스패처 설계, Zero-copy 서버, PipeReader 설계, Channel 디스패처 요청 시 `pipeline-architect-orchestrator` 스킬을 사용하라.

**변경 이력:**
| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-06-02 | 초기 구성 | 전체 | .NET 10 고성능 IO 파이프라인 아키텍처 하네스 구축 |
| 2026-09-11 | TeamCreate 의존 제거, 감독자가 Agent로 워커를 중첩 호출하는 방식으로 재작성 | pipeline-architect-orchestrator·pipeline-supervisor | 하네스 전수조사: 팀 도구·TaskGet 미존재 |
| 2026-09-12 | 산출물 경로를 `_workspace/pipeline/` 하위로 격리, 새 실행 시 자기 디렉토리만 보관 이동 | pipeline-architect-orchestrator·전용 스킬 3종·에이전트 4종 | 모든 하네스가 `_workspace/` 루트를 공유해 산출물 파일명이 충돌하고 Phase 0 전체 이동이 타 하네스 산출물을 파괴(code-review 하네스 교차 검토에서 확인) |
| 2026-09-13 | Claude↔Codex 교차 점검 반영: run_dir·manifest(브리프·계약·산출물 해시), 계약을 불변 입력으로 확정(형제 협상·대기·SendMessage 제거, deviation 반환), 독립 빌드 게이트(build/Pipeline.csproj + dotnet build 경고 0·오류 0)와 오케스트레이터 재검증(빌드 재실행·감사 JSON 대조), 판정 3단계·점수 산식 단일 정본, 감사 입력 완전성 필수, 재작업 상한(워커별 1회·합계 2회), 에이전트 tools 명시, 템플릿 기술 오답 교정(examined 스핀, SequenceReader await 보존 컴파일 실패, 파이프 슬라이스 채널 전달 use-after-return→풀 버퍼 소유 복사본, struct IThreadPoolWorkItem 박싱, SetBuffer(byte*) 미존재, FullMode.Drop 미존재, WhenAll 상호 취소 없음, Complete(ex)·IsCanceled·EOF 잔여 프레임, 서버 범위 TryComplete, 워커 생존, PauseWriterThreshold≥MaxFrame+Header) — 템플릿 3종 net10.0 실제 빌드 검증 | pipeline-architect-orchestrator·pipeline-supervisor·io-loop-designer·thread-dispatcher-designer·load-test-auditor·스킬 3종·.codex/agents toml | plan/harness_cross_check_0913.md 파이프라인 절 22건 |

---

## 하네스: TDD (테스트 주도 개발)

**목표:** 요구사항 입력 시 Red(실패 테스트)→Green(최소 구현)→Refactor(검증·리팩토링) 사이클을 에이전트 팀으로 완주하고, harness-evolve로 명세 대비 최종 코드의 진화 델타를 포착한다.

**트리거:** TDD, 테스트 먼저 작성, Red-Green-Refactor, TDD 사이클, 기능 구현(TDD) 요청 시 `tdd-orchestrator` 스킬을 사용하라. 진화 리포트는 `/harness-evolve`로 수동 실행 가능.

**변경 이력:**
| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-06-02 | 초기 구성 | 전체 | TDD Red-Green-Refactor 하네스 구축 (harness-evolve 포함) |
| 2026-09-11 | TeamCreate 의존 제거(순차 Agent 호출), dotnet_study 절대경로 제거, TddSession.csproj 템플릿 수정(EnableDefaultCompileItems=false, 단계별 Compile 조건을 실제 .cs 존재 여부로) | tdd-orchestrator·tdd-refactor-phase | 하네스 전수조사: 타 프로젝트 경로로 Refactor 단계 실패 확정. 실행 검증 중 NETSDK1022 중복·빈 03_qa/Src 로 스텁 미컴파일 발견 |
| 2026-09-12 | 산출물·TddSession.csproj 경로를 `_workspace/tdd/` 하위로 격리, 새 사이클 시 자기 디렉토리만 보관 이동 | tdd-orchestrator·tdd 단계 스킬 3종·harness-evolve·에이전트 3종 | 모든 하네스가 `_workspace/` 루트를 공유해 산출물 파일명이 충돌하고 Phase 0 전체 이동이 타 하네스 산출물을 파괴(code-review 하네스 교차 검토에서 확인) |
| 2026-09-13 | Claude↔Codex 교차 점검 반영: run_dir·manifest, TddSession.csproj를 **파일 단위 우선순위(qa > builder > analyst)** 로 재설계(msbuild 평가·dotnet test 실측 검증)해 부분 리팩토링·누적 사이클·재작업 루프에서 컴파일 깨짐 제거, csproj "없으면 생성", 네임스페이스 계약(TddSession/TddSession.Tests + global using Xunit), trx 기반 로케일 무관 판정·종료 코드 보존·시도별 결과 보존, Red 증빙(빌드 성공·전원 실패) 필수, 재작업 전 03_qa/Src 무효화·회귀 실패 롤백, 승격 절차·ProjectReference 옵션, 에이전트 tools 명시·SendMessage/claim 제거, 패키지 버전 정렬(17.14.1/2.9.3/3.1.4), 예시 오류 교정(Assert.Multiple·Dictionary.GetOrAdd·정수 나눗셈·checked 리팩토링) | tdd-orchestrator·tdd-analyst/builder/qa·tdd 단계 스킬 3종·harness-evolve·.codex/agents toml | plan/harness_cross_check_0913.md TDD 절 16건 |
