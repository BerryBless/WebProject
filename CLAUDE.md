# PortfolioBlog 프로젝트

## 프로젝트 개요

**목표:** (작성 예정) — 이 저장소는 `ClaudeCodeStudy`의 하네스 구성(에이전트·스킬·훅·CI·Codex 협업)을 그대로 이식해 시작한 새 솔루션이다. 솔루션 이름은 언제든 바뀔 수 있으므로 하네스 스크립트는 저장소 루트를 자동 인식한다(`CLAUDE_PROJECT_DIR` → 스크립트 위치 순).

**구성(2026-09-20):** `PortfolioBlog.slnx`(.NET 10) 아래 두 .NET 프로젝트와 SPA 디렉터리가 있다. 프로젝트를 추가하면 이 절과 CI(`.github/workflows/ci.yml`)를 함께 갱신할 것. 제품 설계는 `plan/tech_blog_0920.md`(기술 블로그, **보안 최우선**) 참조. 사람이 읽는 문서는 `README.md`(랜딩)와 `docs/` 서브페이지 9개(architecture·security·development·testing·configuration·deployment·history·harness·worklog)로 나뉜다 — 코드·구성이 바뀌면 해당 서브페이지를 함께 갱신한다. 이전 PARA 노트앱 설계(`plan/para_notes_0917.md`)는 폐기됐다. 패키지 버전은 `Directory.Packages.props`(중앙 패키지 관리, 전이 의존성까지 고정)가 일괄 관리한다 — 새 패키지는 거기에 추가한다.
- `PortfolioBlog.Api` — ASP.NET Core 최소 API(관리 `/api`) + Razor Pages(공개 페이지 서버 렌더링)(`Microsoft.NET.Sdk.Web`). 통합 테스트 접근용으로 `Program`을 `public partial`로 노출한다. 공개 페이지는 `Pages/`(GET/HEAD·공개 호스트 전용 규약), 정적 파일은 `wwwroot/css/site.css` 하나.
- `PortfolioBlog.Api.Tests` — xUnit + `Microsoft.AspNetCore.Mvc.Testing`. CI의 `dotnet test` 게이트가 실제로 검사하는 대상이다.
- `PortfolioBlog.Web` — 관리 에디터 전용 React 19 + TypeScript + Vite SPA(3단계 구현 완료). `admin.<도메인>`에서만 서빙되며 `npm run build` 산출물 `dist/`는 Caddy 이미지에 복사된다. 보안 헤더(CSP 포함)의 정본은 `admin-headers.ts` — `vite preview`가 이미 쓰고 Plan 4의 Caddyfile이 CSP·`X-Content-Type-Options`·`X-Frame-Options`·`Referrer-Policy`·`Permissions-Policy`를 그대로 옮긴다. **HSTS는 Caddy가 따로 더한다**(이 파일에는 의도적으로 없다 — 루프백 미리보기 서버에서도 쓰이기 때문). `npm run certs`(개발 인증서 내보내기)·`npm run dev`(HTTPS 개발 서버)·`npm test`(Vitest)·`npm run e2e:prepare && npm run e2e`(Playwright, 실제 백엔드 + PostgreSQL + production 빌드, Chromium·Firefox, Docker Desktop 필요)로 검증한다. CI는 `web` 잡(lint·typecheck·test·build)과 `web-e2e` 잡(서비스 컨테이너 PostgreSQL + Playwright)을 돈다.
- `deploy/` — compose(caddy·api·postgres + 백업/복원 전용 `tools`, 네트워크 셋 `public`/`edge`/`db`)·Caddyfile(공개·관리 사이트 2개)·`.env.example`·`postgres-init`(DB 롤 셋 `blog_app`/`blog_public`)·`backup.sh`/`restore.sh`(무중단 백업과 복원)·`smoke/`(운영과 같은 이미지로 띄워 찌르는 스모크, CI `deploy-smoke` 잡)·`OPERATIONS.md`. 새 이미지 태그·Caddyfile·compose를 고치면 `bash deploy/smoke/run.sh`를 통과시킬 것.

**하네스 검증:** `pwsh scripts/harness-audit.ps1` 이 에이전트·스킬·미러 구조를 8개 항목으로 검사한다(프론트매터, 참조 실존, 절대경로, 팀 도구, 미러 동기화·Codex 에이전트 재귀, 서명, 쓰기 범위 훅). 하네스 파일을 고치면 실행해 PASS를 확인할 것. 감사 결과와 수정 이력은 `plan/harness_audit_0911.md` 참조.

**토큰 절감 규칙(2026-09-14):** 하네스별 변경 이력은 `plan/harness_changelog.md` 에만 기록한다(CLAUDE.md 에는 포인터만). 에이전트·스킬 `description` 은 트리거 키워드 + 한 문장 역할로 짧게 유지한다(본문은 호출 시에만 로드). 서브에이전트는 프론트매터 `model:` 로 기본 `sonnet`, 코드 생성·감독·cross 계열만 `opus` 를 쓴다. superpowers 플러그인은 `superpowers-marketplace` 버전만 활성(`.claude/settings.json`, 2026-09-17), 기능 작업은 brainstorming → writing-plans → executing-plans 흐름을 따른다.

**경로 규칙:** 절대 경로(`E:\project\...`)를 설정·스크립트에 하드코딩하지 않는다. Stop 훅은 `$env:CLAUDE_PROJECT_DIR`, PowerShell 스크립트는 `$PSScriptRoot` 기준으로 루트를 계산한다.

**작업 디렉토리 규칙:** 하네스 산출물은 `_workspace/<하네스명>/` 하위에만 쓴다(code-review·gc-guard·concurrency-guard·pipeline·tdd·git·cross). 새 실행 시 자기 하위 디렉토리만 `_workspace/<하네스명>_{타임스탬프}/`로 보관 이동하고, `_workspace/` 루트나 다른 하네스 디렉토리는 건드리지 않는다. 예외: `cross`는 git 추적 대상이라 **보관 이동하지 않고** run_id 하위 디렉토리를 누적한다(이동하면 추적 기록이 삭제로 커밋됨). `.gitignore`의 `_workspace/*` 규칙으로 `cross/` 외에는 커밋되지 않는다.

**쓰기 범위 훅:** 감사·리뷰 전용 서브에이전트 24종(구현 역할 `cross-implementer` 제외)은 `scripts/hooks/guard-write-scope.ps1` PreToolUse 훅으로 Write/Edit 대상이 자기 하네스 디렉터리(`_workspace/<하네스명>/`) 밖이면 거부된다. 1차 방어선은 각 에이전트 프론트매터의 `hooks:`(`-Allow <접두사>`), 2차 방어선은 `.claude/settings.json`의 프로젝트 훅(`-Mode map`, 훅 입력의 `agent_type`으로 판별. 메인 세션·구현 에이전트는 통과). **두 훅 모두 세션 시작 시 읽히므로 변경 후 세션을 재시작해야 적용된다.** 적용 확인은 감사·리뷰 에이전트에게 `_workspace/<하네스>/probe/` 와 `PortfolioBlog.Api/` 에 각각 Write 를 시도하게 해 후자만 거부되는지 본다.



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

**변경 이력:** `plan/harness_changelog.md` 의 "하네스: Git 자동 커밋 & 푸시 (Git Automator)" 절 참조.

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
| plan/harness_changelog.md | 2026-09-14 | 하네스별 변경 이력 표(CLAUDE.md에서 분리), 토큰 절감 조치 기록 |
| plan/para_notes_0917.md | 2026-09-17 | **(폐기됨 → tech_blog_0920.md)** PARA 노트앱 홈페이지 설계. 결정 이력 보존용 |
| plan/tech_blog_0920.md | 2026-09-20 | 기술 블로그 설계(PARA 대체, 보안 최우선): 공개 페이지 서버 렌더링(Razor+Markdig)·관리 SPA 서브도메인 분리, IP AND 비밀번호 세션, 마크다운 정제 파이프라인·CSP, 시리즈·검색·Atom·SEO, Codex 교차 검토 반영표, Mermaid 흐름도·시퀀스, 4단계 구현 계획 |
| plan/tech_blog_2a_report_0921.md | 2026-09-21 | 기술 블로그 2A단계 실행 보고서: 만든 것(마크다운 파이프라인·미리보기·이미지 첨부), 공격·측정 기반 검증 결과, 계획 결함 16건과 교훈, 질문 없이 내린 판정 12건, 수용한 잔여 위험, 알려진 문제(로컬 간헐 테스트), Plan 2B·3·4 인계 |
| plan/tech_blog_2b_report_0921.md | 2026-09-21 | 기술 블로그 2B단계 실행 보고서: 만든 것(공개 Razor 페이지·검색·피드·보안 헤더·속도 제한·읽기 전용 DB 연결·렌더 게이트/캐시·첨부 정합성), 실제 Production 호스트 HTTPS 공격 결과와 거기서 찾은 결함 3건, 계획 결함 23건과 교훈, 질문 없이 내린 판정 28건, 수용한 잔여 위험, 알려진 문제, Plan 3·4 인계 |
| plan/tech_blog_3_report_0922.md | 2026-09-22 | 기술 블로그 3단계(관리 에디터 SPA) 실행 보고서: 만든 것, 실제 호스트 공격 결과와 거기서 찾은 결함 5건, 검증하고 쓴 계획에서도 나온 계획 코드의 결함 약 20건과 교훈(보안 통제 자체의 결함·틀린 판정 R5), 질문 없이 내린 판정 16건, 수용한 잔여 위험, Plan 4 인계 |
| plan/resume_guide_0921.md | 2026-09-21 | 작업 재개 가이드(갱신형): 단계별 진행 상태와 기준 커밋, 재시작 5분 점검, **Plan 4 실행 재개 지점**(브랜치·커밋 표·재개하면 바로 할 일·실행하며 확인된 사실), SDD 실행이 끊겼을 때 복구(ledger·센티널), 자주 밟는 함정, 문서·코드 지도, 사용자가 정해 둔 결정 |

---

## 인터페이스 및 API 문서화(주석) 규칙

모든 인터페이스, public 클래스의 메서드, 대리자(Delegate), RPC 정의 코드를 생성하거나 수정할 때는 반드시 표준 XML 문서 주석(C# `///`)을 매우 상세히 작성해야 한다. 단순 기능 설명을 넘어 **고성능 시스템 프로그래밍 관점의 제약 조건**을 주석에 반드시 포함할 것.

### 적용 범위 (2026-09-21)

`<remarks>`의 3항목 목록은 **동작이 있는 곳**에만 요구한다. 내용 없는 상용구("Thread-safe / 할당 없음 / 즉시 반환")는 실제 제약을 가린다.

| 대상 | 요구 |
|---|---|
| 인터페이스, public 클래스·구조체와 그 **메서드**(생성자 포함), 대리자, 확장 메서드, 미들웨어·엔드포인트 핸들러 클래스 | `<summary>` + 3항목 `<remarks>` |
| 자동 속성, 상수, enum 멤버, DTO `record`(위치 매개변수는 `<param>`만), 옵션 클래스의 설정 속성 | `<summary>`만. 값의 의미·단위·제약을 적는다 |
| 테스트 클래스 | `<summary>` + 3항목 `<remarks>`(픽스처 공유·병렬 실행·외부 자원을 적는다) |
| 테스트 메서드(`[Fact]`·`[Theory]`) | `<summary>`만. 무엇을 증명하는지, 왜 그 입력인지 적는다 |
| EF 마이그레이션·Designer·ModelSnapshot 등 도구 생성 코드 | 면제 |

속성이라도 **접근할 때 계산·할당·I/O·잠금이 일어나면** 메서드로 보고 3항목을 적는다. 기존 코드의 상용구 `<remarks>`는 그 파일을 고칠 때 함께 정리한다(일괄 정리 커밋은 만들지 않는다).

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

**변경 이력:** `plan/harness_changelog.md` 의 "하네스: Codex 협업 (Claude ↔ OpenAI Codex CLI)" 절 참조.

---

## 하네스: 교차 검증 개발 (Claude ↔ Codex Cross-Verify)

**목표:** Plan 단계와 최종 코드 리뷰 단계에서 Claude와 실제 Codex CLI가 독립 판단 → 상호 검증 → 근거 기반 조정을 거치는 개발 파이프라인. Codex는 검증 전담(read-only), 구현은 Claude 전담.

**트리거:** 요청에 '코덱스'/'Codex' 키워드가 명시된 교차 검증 요청(예: 코덱스 교차 검증으로 구현, Codex랑 같이 구현)에만 `cross-verify` 스킬을 사용하라. **코덱스/Codex 언급이 없는 '교차 검증'·'더블 체크' 요청에는 실행 금지.** 단발 Codex 질문·단독 리뷰는 `codex` 스킬.

**주의:** Codex 미실행 상태에서 "교차 검증 완료" 보고 금지. Codex 산출물은 `*.meta.json`(status=success) 증빙 필수. `cross-verify`·`codex` 스킬은 Claude 전용이므로 `.agents/skills/` 미러에서 제외한다(Codex 자기 호출 재귀 방지).

**변경 이력:** `plan/harness_changelog.md` 의 "하네스: 교차 검증 개발 (Claude ↔ Codex Cross-Verify)" 절 참조.

---

## 하네스: 종합 코드 리뷰

**목표:** 아키텍처·보안·성능·스타일 4개 에이전트가 병렬로 코드를 감사하고 단일 리포트로 통합한다.

**트리거:** 코드 리뷰, PR 검토, 코드 감사, 종합 리뷰 요청 시 `code-review-orchestrator` 스킬을 사용하라. 단순 질문(개념 설명 등)은 직접 응답 가능.

**변경 이력:** `plan/harness_changelog.md` 의 "하네스: 종합 코드 리뷰" 절 참조.

---

## 하네스: 동시성 가드 (.NET 10 고성능 서버)

**목표:** Lock-Free 설계 강제·락 정당화 주석 감사·데드락 정적 분석(생성-검증)을 에이전트 팀으로 조율하고 단일 동시성 리포트를 생성한다.

**트리거:** 동시성 검사, 락 감사, 데드락 분석, Lock-Free 검증, async 데드락, 컨텐션 분석 요청 시 `concurrency-guard-orchestrator` 스킬을 사용하라.

**변경 이력:** `plan/harness_changelog.md` 의 "하네스: 동시성 가드 (.NET 10 고성능 서버)" 절 참조.

---

## 하네스: GC 가드 (.NET 10 메모리 최적화)

**목표:** 힙 할당 스캐너·풀링 강제자 병렬 감사 → 교차 검증으로 GC 압력 유발 패턴을 제거하고 ValueTask·Span·ArrayPool을 올바르게 적용한다.

**트리거:** GC 억제, 힙 할당 감사, 메모리 최적화, ArrayPool 검사, ValueTask 검증, boxing 탐지, GC 압력 분석 요청 시 `gc-guard-orchestrator` 스킬을 사용하라.

**변경 이력:** `plan/harness_changelog.md` 의 "하네스: GC 가드 (.NET 10 메모리 최적화)" 절 참조.

---

## 하네스: 파이프라인 아키텍처 (.NET 10 고성능 IO)

**목표:** System.IO.Pipelines 기반 Zero-copy IO 루프와 Channel<T> 락-프리 디스패처를 감독자 패턴으로 설계하고 부하 테스트 감사까지 수행한다.

**트리거:** Pipelines 설계, IO 루프 구현, 디스패처 설계, Zero-copy 서버, PipeReader 설계, Channel 디스패처 요청 시 `pipeline-architect-orchestrator` 스킬을 사용하라.

**변경 이력:** `plan/harness_changelog.md` 의 "하네스: 파이프라인 아키텍처 (.NET 10 고성능 IO)" 절 참조.

---

## 하네스: TDD (테스트 주도 개발)

**목표:** 요구사항 입력 시 Red(실패 테스트)→Green(최소 구현)→Refactor(검증·리팩토링) 사이클을 에이전트 팀으로 완주하고, harness-evolve로 명세 대비 최종 코드의 진화 델타를 포착한다.

**트리거:** TDD, 테스트 먼저 작성, Red-Green-Refactor, TDD 사이클, 기능 구현(TDD) 요청 시 `tdd-orchestrator` 스킬을 사용하라. 진화 리포트는 `/harness-evolve`로 수동 실행 가능.

**변경 이력:** `plan/harness_changelog.md` 의 "하네스: TDD (테스트 주도 개발)" 절 참조.
