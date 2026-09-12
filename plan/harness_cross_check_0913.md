# 나머지 하네스 5종 Claude↔Codex 교차 점검 (2026-09-13)

## 1. 배경 및 목적

코드 리뷰(`plan/code_review_harness_fix_0912.md`)·GC 가드(`plan/gc_guard_harness_fix_0912.md`) 하네스 교정 후, 남은 5개 하네스(동시성 가드·파이프라인 아키텍처·TDD·Git 자동 커밋·Codex 교차 검증)를 같은 방식으로 점검했다. 각 하네스마다 **Claude 독립 검토(general-purpose 서브에이전트, 읽기 전용)** 와 **실제 Codex CLI(`codex exec -s read-only`, stdin 프롬프트)** 를 동일 프롬프트로 실행하고 결과를 대조했다.

- Codex 실행 증빙: 1차(09-12 22:30, 5개 병렬)는 전부 사용량 한도로 실패(각 ~85k 토큰 소모 후 `usage limit`). 2차(09-13 01:04~01:22, 순차)에서 5개 모두 exit 0, 출력 10~16KB.
- 프롬프트: 스크래치패드 `codex_{harness}_prompt.txt`(공통 런타임 사실 + 하네스별 파일 목록·도메인 검증 항목). 이 문서는 두 결과의 합집합이며, 표의 C/X 열은 Claude/Codex 발견 여부다.
- **이 문서는 점검 결과다. 수정은 아직 적용하지 않았다.**

## 2. 공통 결함 (5개 하네스 전부 또는 대부분)

| # | 결함 | 해당 하네스 | C | X |
|---|------|------------|:-:|:-:|
| G1 | 팀 시대 프로토콜 잔재: 형제 간 SendMessage, 공유 작업 목록 claim, 응답 대기, "10분 타임아웃" (실행 불가) | 동시성·파이프라인·TDD·cross(planner/reviewer) | ✓ | ✓ |
| G2 | `tools:` 미지정 에이전트 → 상위 도구 전부 상속(Edit·Agent 포함): deadlock-analyzer, tdd 3종, pipeline-supervisor·io-loop-designer·thread-dispatcher-designer, codex-adapter·cross-implementer | 동시성·TDD·파이프라인·cross | ✓ | ✓ |
| G3 | run_dir·meta.json(해시)·`_rN` 라운드 없음 → 부분 재실행이 옛 결과(검증·승인·테스트 로그)를 새 코드에 붙임 | 5종 전부 | ✓ | ✓ |
| G4 | 결정적 점수 산식·판정 우선순위·JSON 구조 검증 없음; `modified` 판정 감점 누락 | 동시성·파이프라인(판정 공백) | ✓ | ✓ |
| G5 | 생성·검사하는 코드에 CLAUDE.md 필수 주석(`<remarks>` 3항목, 선언부 메커니즘 `//`)이 없고 게이트에서도 검사 안 함 | 동시성·파이프라인·TDD·cross(리뷰 축) | ✓ | ✓ |
| G6 | Stop 훅과의 상호작용: 파이프라인 중간에 사용자 확인으로 턴이 끝나면 훅이 미검증 상태를 폴백 메시지로 커밋·푸시 | Git·cross·(TDD 승격 시) | ✓ | ✓ |

## 3. 하네스별 결함표

### 3-1. 동시성 가드 (concurrency-guard) — Claude 18건 / Codex 19건

| 심각도 | 위치 | 문제 | C | X |
|-------|------|------|:-:|:-:|
| high | 오케스트레이터 Phase 1 B | `find … > combined_source.txt`가 **저장소 루트**에 파일 생성 → ignore 안 됨 → Stop 훅이 커밋. 파일 경계·줄번호 없음, bin/obj/_workspace 미제외 | ✓ | ✓ |
| high | 에이전트 4종·스킬 4종 | enforcer→auditor `audit-these-locks`, analyzer↔reviewer `review-requested`/`reanalyze` 형제 통신. reviewer의 "재분석 타임아웃" 분기는 타이머 없음 | ✓ | ✓ |
| high | Phase 4 / reviewer 스키마 | `needs_reanalysis`가 프롬프트에선 배열, reviewer에선 bool+`reanalysis_targets` | ✓ | ✓ |
| high | deadlock-review 점수 | `modified`·`additional` 감점 누락(`additional` 변수 선언만) | ✓ | ✓ |
| high | Phase 5 | 판정 임계값 없음, 고정 가중치 0.35/0.30/0.35에 실패 도메인 처리 없음, lock 도메인 2개는 산식 없음 | ✓ | ✓ |
| high | deadlock Pattern 1 | "ASP.NET Core는 SynchronizationContext 존재 → 즉각 데드락" 오답. Core엔 컨텍스트 없음(스레드풀 기아가 실제 위험). 이 저장소 핸들러의 `.Result`가 무조건 CRITICAL→BLOCK | ✓ | ✓ |
| high | lock-free 교체 표 / CLAUDE.md 예시 | Channel·ConcurrentDictionary를 "Lock-Free"로 일반화. BoundedChannel·CD 쓰기는 내부 lock 사용. `ConcurrentQueue + lock → 큐 단독`은 복합 원자성 상실 | △ | ✓ |
| medium | deadlock-analyzer 프론트매터 | `tools:` 없음 → Edit·Agent 상속 | ✓ | ✓ |
| medium | Phase 1 A | master 빈 diff, 작업 트리 미수집, 빈 `$BASE` | ✓ | ✓ |
| medium | deadlock Pattern 5 | ConfigureAwait 정규식 `[^(ConfigureAwait]`가 문자 집합 부정이라 `await client…` 대부분 누락. public/internal 접근성만으로 라이브러리 판정 → ASP.NET Core 엔드포인트·xUnit까지 MEDIUM | ✓ | ✓ |
| medium | lock-justification 탐지 | `lock\s*\(.*\)\s*\{`가 Allman 스타일(이 저장소 기본)을 전부 놓쳐 조용히 100점. `_rwLock` 필드명 하드코딩 | ✓ | ✓ |
| medium | 스킬 4종 | .NET 9+ `System.Threading.Lock`/`EnterScope` 미탐지(.NET 10 대상). Monitor.TryEnter·Mutex.WaitOne 누락 | ✓ | ✓ |
| medium | lock-free ↔ deadlock | SemaphoreSlim: enforcer는 제거 대상, deadlock 수정안은 `SemaphoreSlim(1,1)` 권장 → 상호 모순 | ✓ | ✗ |
| medium | deadlock Pattern 3 | SemaphoreSlim 소유권을 "취득 후 5~20줄 내 try/finally"로 판정 → 긴 정상 구역 오탐, timeout false 후 Release 오류 누락 | ✗ | ✓ |
| medium | deadlock Pattern 2·7·8 | `lock` 안 await는 컴파일 오류인데 데드락으로 분류, 토큰 미전파를 무한 대기로 단정 | △ | ✓ |
| medium | lock-free ABA·RWLS·Interlocked | 관리 참조 CAS를 ABA로 오설명, bool 필드에 int CAS, `UpgradeableReadLock` 설명 오류, TPL Dataflow "실험적" 오기, ConcurrentBag 범용 권고 | ✓ | ✓ |
| medium | Phase 3 필요 락 공유 | enforcer 완료 시점에 따라 auditor 결과가 달라지는 경쟁 조건 | ✓ | ✗ |
| medium | 에이전트 3종 "이전 산출물 변경분만" | 변경 목록 없이 옛 결과 승계 | ✓ | ✓ |
| medium | [LOCK-REQUIRED] 계약 | 에이전트 medium vs 스킬 low 심각도 불일치, Accepted 예시가 자체 0~5줄 규칙 위반, `<remarks>` Blocking과의 정합성 미검사 | ✓ | ✓ |
| low | 실행 모드·에러표 | `depends_on`, "세션당 팀 1개", 도달 불가 타임아웃 행, Phase 번호 어긋남 | ✓ | ✓ |
| low | CLAUDE.md ↔ AGENTS.md | 동시성 절 09-12 이력 행이 CLAUDE.md에 없음 | ✓ | ✗ |

### 3-2. 파이프라인 아키텍처 (pipeline-architect) — Claude 21건 / Codex 19건

| 심각도 | 위치 | 문제 | C | X |
|-------|------|------|:-:|:-:|
| high | load-test-audit 판정 | 스킬은 3단계(BLOCK/RC/APPROVE), 에이전트·감독자·오케스트레이터는 `APPROVE|BLOCK` 이진. **HIGH=1 케이스 미정의**. 0911 실행에서 HIGH 2건인데 APPROVE 보고됨 | ✓ | ✓ |
| high | io-loop ReadPipeAsync | 불완전 프레임 시 `examined = seqReader.Position`(== consumed) → 같은 버퍼 즉시 재반환 → **CPU 100% 스핀** | ✓ | ✓ |
| high | io-loop ReadPipeAsync | `SequenceReader<byte>`(ref struct)를 await 너머로 보존 → **컴파일 실패** | ✗ | ✓ |
| high | 시나리오·io-loop·load-test 영역 4 | `ParsedMessage.Payload = ReadOnlySequence<byte>`를 Channel로 넘긴 뒤 `AdvanceTo` → **use-after-return**. 감사는 유일한 해법(풀 버퍼 1회 복사)을 Zero-copy 위반으로 감점 | ✓ | ✓ |
| high | thread-dispatch IThreadPoolWorkItem | `readonly struct : IThreadPoolWorkItem`을 "힙 할당 0"이라 주장 → 인터페이스 매개변수라 **박싱**. 재큐잉이 BoundedChannel 백프레셔를 무력화 | ✓ | ✓ |
| high | io-loop SAEA 패턴 | `saea.SetBuffer((byte*)ptr, len)` 오버로드 **존재하지 않음**(컴파일 불가). `SetBuffer(Memory<byte>)`면 Pin 불필요. 에이전트 원칙(SAEA 재사용 강제)과 스킬("ReceiveAsync(Memory)면 불필요") 상충 | ✓ | ✓ |
| high | 오케스트레이터 전체 | **`dotnet build` 게이트 없음**, 검증용 csproj 없음. 템플릿 자체 컴파일 불가(`_dispatcher` 미선언, nullable 불일치). 0911은 감독자 재량으로 빌드 | ✓ | ✓ |
| high | io-loop 백프레셔 표 | `PauseWriterThreshold < 최대 프레임`이면 **파이프 교착**. "저지연 4KB/2KB/1KB" 행이 이를 무시 | ✓ | ✗ |
| high | io-loop ProcessConnectionAsync | "한쪽 완료 시 다른 쪽도 종료"는 거짓. 리더 실패 후 `ReceiveAsync` 대기 중이면 `WhenAll` 정지. 상호 취소 없음 | ✓ | ✓ |
| high | load-test-auditor 에러 처리 | 입력 파일 누락 시 "미검토" 표시 후 **APPROVE 가능** | ✗ | ✓ |
| high | 에이전트 4종·스킬 3종 | 형제 SendMessage 협의, "계약 미수신 시 대기", "스텁으로 설계"(계약 무시), claim | ✓ | ✓ |
| high | Phase 0 | 부분 재실행이 감사·04 문서를 재생성하지 않아 옛 APPROVE가 새 코드에 붙음 | ✓ | ✓ |
| medium | thread-dispatch | `BoundedChannelFullMode.Drop` **존재하지 않는 enum 멤버** | ✗ | ✓ |
| medium | thread-dispatcher-designer / 감사 | "Channel은 lock-free" 오답(BoundedChannel 내부 lock). `lock_free: true` 고정 보고 | ✗ | ✓ |
| medium | io-loop FillPipe/ReadPipe | 예외 시 `CompleteAsync()`(예외 없이) → 잘린 스트림이 정상 EOF로. `IsCanceled` 미처리. EOF 잔여 부분 프레임 조용히 폐기 | ✓ | ✓ |
| medium | thread-dispatch 완료 흐름 | 연결 N개가 디스패처 공유 시 첫 연결 종료가 채널을 닫음. `Complete()` 2회 예외(`TryComplete` 필요). 워커 루프가 핸들러 OCE로 조용히 감소 | ✓ | ✓ |
| medium | load-test 시나리오 2·영역 1 | `Pipe.Dispose` 검사(`Pipe`는 IDisposable 아님). `Complete`를 try/finally 위치로만 판정, `CompleteAsync`/`TryComplete` 미인식 | ✓ | ✓ |
| medium | 감독자 프롬프트·Phase 4 | 감독자 한 줄 보고를 검증 없이 수용. "10분 유휴 시 부분 산출물로 진행" → 미종료 워커와 동시 쓰기 | ✓ | ✓ |
| medium | 인터페이스 이름 | `IParsedMessageConsumer` / `_dispatcher.DispatchAsync` / `IMessageHandler.Handle` 세 가지. 계약 템플릿 없음 | ✓ | ✗ |
| medium | 스킬 3종·감독자 게이트 | CLAUDE.md 주석 규칙 미강제, 템플릿 자체 위반 | ✓ | ✓ |
| low | thread-dispatch 용어 | `UnsafeQueueUserWorkItem`이 생략하는 것은 ExecutionContext(보안 컨텍스트 아님). `SingleReader=true`는 힌트일 뿐 | ✓ | ✗ |
| low | 저장소 | 0911 산출물이 `_workspace/` 루트에 고아로 남음. Codex 미러에 `Agent(...)` 절차 복제 | ✓ | ✗ |

### 3-3. TDD (tdd-orchestrator) — Claude 15건 / Codex 12건

| 심각도 | 위치 | 문제 | C | X |
|-------|------|------|:-:|:-:|
| high | TddSession.csproj 템플릿 | `03_qa/Src`에 파일 **하나**만 있어도 `01_analyst/Src`·`02_builder/Src` **전체** Remove → 부분 리팩토링·누적 사이클·재작업 루프에서 CS0246 또는 옛 코드 검증 | ✓ | ✓ |
| high | tdd-red/green 템플릿 | 테스트 `namespace TddSession.Tests` vs 스텁 `TddSession.Src`인데 `using` 없음 → **템플릿 그대로면 컴파일 불가** | ✓ | ✓ |
| high | 에이전트 3종 | `tools:` 없음(All tools). analyst→builder→qa SendMessage, claim, "질문 목록 전달하고 대기" | ✓ | ✓ |
| high | Phase 2·3 | "재실패 시 수집 실패로 표기하고 계속"(순차 파이프라인에 부적합). Red 단계에 실제 `dotnet test` 실행·"빌드 성공+전원 실패" 증빙 없음 | ✓ | ✓ |
| high | Phase 4·5 | 산출물이 gitignore된 `_workspace/tdd/`에만 남고 **실제 프로젝트 승격 절차 없음**. `WebProject.Api` ProjectReference 없어 기존 코드 대상 TDD 불가 | ✓ | ✓ |
| medium | Phase 0·1 | 새 사이클마다 디렉토리 이동하는데 csproj는 "처음 실행 시만 생성" → 2회차부터 csproj 없음. bin/obj 이동 시 잠금. 레거시 산출물이 `_workspace/` 루트에 잔존 | ✓ | ✗ |
| medium | refactor Step 2·5 / harness-evolve | `tee`가 test_results.txt를 덮어쓰고 회귀 실행은 미기록 → evolve가 "최종 결과"로 리팩토링 **전** 로그를 읽음. `pipefail` 없어 종료 코드 소실. 판정 토큰이 영문(`Passed:`)인데 환경 출력은 한국어. 시도 횟수 기록 없음 | ✓ | ✓ |
| medium | refactor Step 5 | 회귀 실패 시 "롤백"의 구체 행위 없음 → 깨진 `03_qa/Src`가 이후 모든 빌드를 가림 | ✓ | ✓ |
| medium | green/refactor 체크리스트 | CLAUDE.md 주석 규칙 미강제. Gold Plating 금지가 문서화를 "과잉"으로 뺄 여지 | ✓ | ✓ |
| medium | harness-evolve Δ3 예시 | `checked(a+b)`는 동작 변경(새 Red 사이클)인데 Refactor 예시로 | ✓ | ✓ |
| low | tdd-analyst / green 예시 | `Assert.Multiple`은 xUnit 2.9에 없음(NUnit). `Dictionary.GetOrAdd`는 존재하지 않음 | ✓ | ✗ |
| low | 오케스트레이터 에러 흐름 예시 | `int a / b`는 b==0에서 자체적으로 DivideByZeroException → 설명한 FAIL이 발생하지 않음 | ✗ | ✓ |
| low | 패키지 버전·경로 기준 | 템플릿 17.11.1/2.9.0/2.8.2 vs 실제 Tests 17.14.1/2.9.3/3.1.4. `$CLAUDE_PROJECT_DIR`/`{project_root}`/상대경로 혼용 | ✓ | △ |

### 3-4. Git 자동 커밋 (commitandpush + Stop 훅) — Claude 17건 / Codex 14건

| 심각도 | 위치 | 문제 | C | X |
|-------|------|------|:-:|:-:|
| high | SKILL Phase 0·1·2 / push-controller | **파이프라인 중간의 사용자 확인(y/n)이 턴을 끝내 Stop 훅이 먼저 감사 없이 폴백 메시지로 커밋·푸시**. 이력에 증거(`8b00b44`, `7f1535d` 폴백 커밋) | ✓ | ✓ |
| high | auto-commit.ps1 2단계 | `.example`이 status 문자열 **어디에든** 있으면 민감 파일 검사 전체 해제(실측). `\.env(\s|$)`가 `.env.local`/`.env.production` 미탐지. 내용 스캔 전무 | ✓ | ✓ |
| high | security-patterns.md | `password\s*[=:]`가 JSON `"Password": "…"`(ASP.NET Core appsettings)에 **매치 안 됨**(실측). Npgsql `Host=…;Password=`, `AccountKey=`, `ENCRYPTED PRIVATE KEY` 누락 | ✓ | ✓ |
| high | auto-commit.ps1 1·4·5단계 | `.git/auto_commit_msg.txt` 잔류 경로 구조적: 변경 없음/스테이지 0건이면 읽지도 지우지도 않음 → 다음 턴 무관한 변경이 그 메시지로 커밋 | ✓ | ✓ |
| high | auto-commit.ps1 전체 | git 명령 종료 코드 미검사, push 실패 `Out-Null`로 영구 무음, `add` 실패 후 부분 index 커밋 가능 | △ | ✓ |
| high | settings.json `asyncRewake` | 비동기 훅 실행 간 상호 배제 없음 → 이전 훅과 다음 턴 스킬이 같은 index·메시지 파일을 동시에 다룸 | ✗ | ✓ |
| high | push-controller pre-commit 대응 | 커밋 실패 시 새 커밋이 없는데 amend 조건이 HEAD만 확인 → **이전 별개 커밋에 합쳐질 수 있음** | ✗ | ✓ |
| high | SKILL Phase 0 재실행 | 스테이지 변경 확인 없이 이전 PASS·메시지 재사용 → 보안 게이트 우회 | ✓ | ✓ |
| high | security-patterns 허용 예외 | 주석 처리·`*.example`의 **실제** 비밀값도 PASS/경고만 → Git 이력엔 동일하게 남음 | ✓ | ✓ |
| medium | auto-commit.ps1 5~7단계 vs commit-msg 훅 | 훅 판정 `^(…):`는 `수정:제목` 통과, commit-msg는 `: .+` 요구 → 거부. 메시지 파일은 이미 삭제, 실패는 `exit 0`으로 무음 | ✓ | ✓ |
| medium | auto-commit.ps1 2단계 exit 2 | Stop 훅 exit 2는 정지 차단이고 stderr가 사유인데 stdout JSON 출력, `stop_hook_active` 미확인 | ✓ | ✗ |
| medium | SKILL ↔ auditor ↔ push-controller 계약 | auditor는 PASS/FAIL만인데 SKILL은 WARN 분기(도달 불가). `edit` 분기가 파일을 다시 쓰지 않음. `00_scope.txt` 생성자 없음 | ✓ | ✓ |
| medium | push-controller | "사용자 확인 요구"를 서브에이전트가 직접 할 수 없음. `git commit -m "$(cat …)"` 대신 `-F`. commit-msg 거부 대응 없음. 보호 브랜치 감지 방법 없음. push만 실패한 경우 재시도 경로 없음 | ✓ | ✓ |
| medium | auditor | 30,000자 Bash 출력 한계로 대용량 diff 조용히 부분 스캔 | ✓ | ✗ |
| medium | `.codex/agents/git-*.toml` | 잡종 서명 `Codex Fable 5.1 <noreply@anthropic.com>`, 존재하지 않는 `.Codex/skills/…` 경로. 미러 SKILL은 Codex에 없는 Agent 파이프라인 지시 | ✓ | ✗ |
| medium | 메시지 BOM | 훅은 텍스트로 읽지만 스킬은 `cat`→`-m` 그대로 → BOM이 첫 글자로 남아 commit-msg 정규식에 걸림 | ✗ | ✓ |
| low | 폴백 메시지 | "외 0개 파일 변경", 파일명 나열(가이드 위반), `core.quotepath`로 비ASCII 경로 깨짐, 폴백 커밋이 writer 스타일 학습 오염 | ✓ | ✓ |
| low | security-patterns 오탐 | GUID 패턴이 `.sln`/`.csproj` 전부에 매치, `*.cer`/`.pub`(공개 자료), 이메일 MEDIUM이 봇 서명에 걸림, 테스트 픽스처 예외 없음 | ✓ | ✗ |

### 3-5. Codex 교차 검증 (cross-verify + codex 스킬) — Claude 16건 / Codex 15건

| 심각도 | 위치 | 문제 | C | X |
|-------|------|------|:-:|:-:|
| high | invoke-codex.ps1 try/catch | `$ErrorActionPreference='Stop'` 아래 `Write-Error`가 예외로 승격 → catch가 meta를 덮어써 **`timeout`/`empty-output` 상태가 절대 기록되지 않음**, exit_code도 -1로 소실(pwsh 7 재현) | ✓ | ✓ |
| high | codex 스킬 호출 패턴 1·3·4 | argv 프롬프트가 비TTY 툴에서 stdin 대기로 **멈춤**(09-12 실측). `invoke-codex.ps1`은 이미 `-` stdin 방식 | ✓ | ✓ |
| high | codex-adapter 역할 2 | "PowerShell 툴 timeout = TimeoutSec+60초"가 툴 상한 600s 초과(660s/960s) → 툴이 pwsh를 죽여 meta 부재 + 고아 codex 프로세스 | ✓ | ✗ |
| high | codex-adapter / meta 계약 | meta에 입력·출력 해시·thread_id 없음, 검증은 `status==success && out_bytes>0`뿐 → **Write 보유 어댑터가 출력과 meta를 직접 써도 탐지 불가** | ✓ | ✓ |
| high | SKILL Phase 0·1·2·3 사용자 확인 | 확인 시점마다 턴 종료 → Stop 훅이 반쯤 구현된 코드·부분 run 디렉토리를 폴백 커밋·푸시. 기준 커밋이 확인 전 HEAD라 사용자 변경이 diff에 섞임 | ✓ | ✓ |
| high | SKILL Phase 3.1 | `git diff <base>`에 `_workspace/cross/` 제외 없음 → 중간 커밋 후 이전 계획·리뷰·`30_diff.patch` 자체가 리뷰 입력에 재유입 | ✓ | ✓ |
| high | Phase 0 / 데이터 프로토콜 | 입력 해시 없이 기존 run 재사용. `_r2` 규칙 모호, 구현자 입력은 고정명 `13_final_plan.md`라 2라운드 계획 무시 가능 | ✓ | ✓ |
| medium | prompts/plan.md·review.md | Codex가 read-only 샌드박스로 `_workspace/cross/<run>/10_claude_plan.md`를 **읽을 수 있음** → 독립성 비대칭(Claude 측만 `*codex*` 열람 금지) | ✓ | ✗ |
| medium | Phase 3.5~6 / reverify | 재검토용 diff 파일 미정의(`30_diff.patch` 재사용 → 옛 코드 승인). 수정할 결함이 없을 때 `32_fix_notes.md` 없어 최종 승인 경로 불완전 | ✓ | ✓ |
| medium | Phase 3.1 신규 파일 | untracked 파일은 경로·요지만 → 새 파일만 있는 변경은 빈 patch | ✗ | ✓ |
| medium | invoke-codex.ps1 인자·경로 | `Start-Process -ArgumentList` 배열이 공백 경로 미인용. `-o` 상대 경로가 `-C`와 다른 기준. meta `cmd` 미기록 | ✓ | ✓ |
| medium | Test-QuotaError | stderr(.err)만 검사, 시도별 로그 미분리 → 이전 quota 로그로 새 실패를 quota 오분류, 비quota 오류가 Claude 단독 폴백으로 | ✓ | ✓ |
| medium | `.codex/agents/cross-*.toml` | 기계 치환 미러("Codex가 Codex를 호출", `.Codex/skills/…` 경로)가 Codex 측에 재귀 에이전트 노출. `.agents/skills` 제외 정책이 `.codex/agents`엔 없음 | ✓ | ✓ |
| medium | cross-planner/reviewer tools | `Bash, Write, SendMessage, Skill` → 읽기 전용 아님. Skill로 `codex` 스킬 호출해 어댑터 밖 Codex 트리거 가능 | ✓ | ✓ |
| medium | CLAUDE.md 작업 디렉토리 규칙 ↔ cross | cross에 "보관 이동" 적용 시 `_workspace/cross_<ts>/`는 ignore → 추적 중 감사 기록이 git에서 삭제 | ✓ | ✗ |
| medium | codex 스킬 | `resume --last`가 다른 하네스 세션을 이어갈 수 있음. `codex exec review`를 "리포 전체"로 오설명(`--uncommitted/--base/--commit` 구분 필요) | ✗ | ✓ |
| low | prompts/final_check ↔ cross-planner | Claude 측 final-check에 `12_plan_adjudication.md` 입력 없음, `VERDICT:` 고정 토큰 없어 양측 파싱 상이 | ✓ | ✗ |
| low | usage.md·시나리오 | 예시가 자체 트리거 조건(Codex 키워드) 불충족, EchoPacket 등 타 저장소 예시 | ✓ | ✓ |

## 4. 대조 요약

| 하네스 | Claude | Codex | 합집합(중복 제거) | Codex 단독 주요 발견 |
|-------|:-----:|:-----:|:----:|------|
| 동시성 가드 | 18 | 19 | 21 | SemaphoreSlim 줄 간격 판정, lock 안 await=컴파일 오류 오분류, Channel/CD "Lock-Free" 일반화 |
| 파이프라인 | 21 | 19 | 22 | `SequenceReader` await 보존 컴파일 실패, `FullMode.Drop` 미존재, 감사자 입력 누락 시 APPROVE |
| TDD | 15 | 12 | 16 | 정수 나눗셈 예시 오류 |
| Git | 17 | 14 | 19 | asyncRewake 상호 배제 없음, pre-commit 실패 시 잘못된 amend, BOM 경로 차이 |
| cross-verify | 16 | 15 | 18 | 신규 파일만 있는 변경 빈 patch, `resume --last` 오연결, `exec review` 오설명 |

의견이 충돌한 항목은 없었다. Codex는 기술 오답(.NET API 존재 여부·컴파일 가능성)에서, Claude 측은 실행 흐름·저장소 상태(실측 명령 결과·git 이력 증거)에서 각각 더 많이 잡았다.

## 5. 권장 수정 순서

1. **Git 자동 커밋** — 매 턴 실행되는 경로라 영향 범위가 가장 넓다. 사용자 확인 중 Stop 훅 선행 커밋 차단(센티널 또는 진행 상태 파일), 메시지 파일 소비 순서, `.example` 필터 버그, appsettings 비밀 패턴, 종료 코드·push 실패 노출.
2. **cross-verify / codex 스킬** — `invoke-codex.ps1` 오류 상태 덮어쓰기, argv 호출 예시, 툴 타임아웃 상한, meta 해시 증빙, diff에서 `_workspace/cross` 제외, `.codex/agents` 재귀 에이전트 제거.
3. **동시성 가드** — 저장소 루트 `combined_source.txt`(즉시 커밋 위험), 코드 리뷰·GC와 같은 골격 이식, ASP.NET Core 컨텍스트·정규식·`System.Threading.Lock` 교정.
4. **파이프라인** — 컴파일 게이트(검증 csproj + `dotnet build`), 판정 계약 통일, 템플릿의 스핀 루프·소유권·박싱·존재하지 않는 API 교정.
5. **TDD** — csproj 파일 단위 덮어쓰기, 네임스페이스 템플릿, Red 증빙, 결과 로그 누적, 승격 절차.

## 6. 변경 파일

없음(점검만 수행). Codex 원문은 스크래치패드 `codex_{concurrency,pipeline,tdd,git,cross}_out.md`, Claude 원문은 서브에이전트 최종 응답(이 문서에 통합).
