# WebProject 프로젝트

## 프로젝트 개요

**목표:** (작성 예정) — 이 저장소는 `ClaudeCodeStudy`의 하네스 구성(에이전트·스킬·훅·CI·Codex 협업)을 그대로 이식해 시작한 새 솔루션이다. 솔루션 이름은 언제든 바뀔 수 있으므로 하네스 스크립트는 저장소 루트를 자동 인식한다(`CLAUDE_PROJECT_DIR` → 스크립트 위치 순).

**구성(2026-09-11):** `WebProject.sln`(.NET 10) 아래 `WebProject.Api`(ASP.NET Core 최소 API, `Microsoft.NET.Sdk.Web`)와 `WebProject.Api.Tests`(xUnit + `Microsoft.AspNetCore.Mvc.Testing`)가 있다. 프로젝트를 추가하면 이 절과 CI(`.github/workflows/ci.yml`)를 함께 갱신할 것.

**하네스 검증:** `pwsh scripts/harness-audit.ps1` 이 에이전트·스킬·미러 구조를 검사한다. 하네스 파일을 고치면 실행해 PASS를 확인할 것.

**경로 규칙:** 절대 경로(`E:\project\...`)를 설정·스크립트에 하드코딩하지 않는다. Stop 훅은 `$env:CLAUDE_PROJECT_DIR`, PowerShell 스크립트는 `$PSScriptRoot` 기준으로 루트를 계산한다. Codex 세션은 현재 작업 디렉토리(저장소 루트) 기준 상대 경로를 쓴다.

**작업 디렉토리 규칙:** 하네스 산출물은 `_workspace/<하네스명>/` 하위에만 쓴다(code-review·gc-guard·concurrency-guard·pipeline·tdd·git·cross). 새 실행 시 자기 하위 디렉토리만 `_workspace/<하네스명>_{타임스탬프}/`로 보관 이동하고, `_workspace/` 루트나 다른 하네스 디렉토리는 건드리지 않는다. `.gitignore`의 `_workspace/*` 규칙으로 `cross/` 외에는 커밋되지 않는다.


**Git 훅:** `scripts/git-hooks/commit-msg`가 커밋 메시지 접두사 형식을 강제한다. 새로 클론하면 `Copy-Item scripts/git-hooks/commit-msg .git/hooks/`(PowerShell) 또는 `cp scripts/git-hooks/commit-msg .git/hooks/`로 설치할 것.

## 하네스: Git 자동 커밋 & 푸시 (Git Automator)

**목표:** 보안 검증 → 한국어 커밋 메시지 자동 생성 → 안전한 커밋 & 푸시를 파이프라인으로 자동화한다.

**트리거:** `/commitandpush`, 커밋해줘, 푸시해줘, 변경사항 올려줘, 깃 커밋 요청 시 `commitandpush` 스킬을 사용하라.

**커밋 규칙 (Codex 세션 전용 — 필수 행동 규칙):**
`auto-commit.ps1` Stop 훅은 **Claude Code 전용**이라 Codex 세션에서는 실행되지 않는다. 커밋 요청 시 Codex는 아래 형식으로 **직접 `git commit`** 한다.
- 형식: `{접두사}: {제목}` (접두사: 추가/수정/버그수정/리팩토링/문서/테스트/의존성)
- 제목: 50자 이내, 파일명 나열 금지, WHY 중심
- 본문(선택): `- ` 항목 나열
- 마지막 줄(필수): `Co-Authored-By: Codex <noreply@openai.com>`

**주의:** `.git/auto_commit_msg.txt`는 Claude Code Stop 훅의 전달 채널이므로 Codex는 이 파일을 절대 생성·수정하지 않는다 (남겨두면 다음 Claude 세션의 훅이 엉뚱한 메시지로 커밋한다).

**변경 이력:**
| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-06-03 | 초기 구성 | 전체 | Git 자동 커밋&푸시 파이프라인 구축 |
| 2026-06-03 | 파일 기반 메시지 전달로 재설계 | auto-commit.ps1 | nested claude -p 콜드스타트/stdin 취약성으로 폴백 빈발 |
| 2026-09-10 | Codex 직접 커밋 규칙으로 분기 | AGENTS.md | Stop 훅은 Claude Code 전용이라 Codex 세션에서 메시지 파일이 잔류하는 문제 방지 |
| 2026-09-12 | 산출물 경로를 `_workspace/git/` 하위로 격리 | commitandpush·git-*.md | 모든 하네스가 `_workspace/` 루트를 공유해 산출물 파일명이 충돌하고 Phase 0 전체 이동이 타 하네스 산출물을 파괴(code-review 하네스 교차 검토에서 확인) |

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

## 하네스: 종합 코드 리뷰

**목표:** 아키텍처·보안·성능·스타일 4개 에이전트가 병렬로 코드를 감사하고 단일 리포트로 통합한다.

**트리거:** 코드 리뷰, PR 검토, 코드 감사, 종합 리뷰 요청 시 `code-review-orchestrator` 스킬을 사용하라. 단순 질문(개념 설명 등)은 직접 응답 가능.

**변경 이력:**
| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-06-02 | 초기 구성 | 전체 | 종합 코드 리뷰 하네스 구축 |
| 2026-06-09 | 보안 가드 감사 | security-reviewer | 해킹·DDoS 공격 표면 점검 (리포트 plan/security_audit_0609.md) |
| 2026-09-11 | TeamCreate 의존 제거, Agent 팬아웃 방식으로 재작성, 리뷰어 tools 제한 | code-review-orchestrator·reviewer 4종 | 하네스 전수조사: 이 빌드에 팀 도구가 없어 실행 불가 |
| 2026-09-12 | 전용 run_dir(`_workspace/code-review/<run_id>/`) 격리, 팀 시대 프로토콜(SendMessage·claim) 제거, 기본 브랜치 빈 diff 폴백, 원본 diff 보존, 결정적 점수 산식·재정규화·판정 우선순위, JSON 구조 검증, 트리거 축소, 스킬 체크리스트 오류 교정 | code-review-orchestrator·reviewer 4종·review 스킬 4종·.codex/agents toml | Claude↔Codex 교차 검토(plan/code_review_harness_fix_0912.md) 16건 |

---

## 하네스: 동시성 가드 (.NET 10 고성능 서버)

**목표:** Lock-Free 설계 강제·락 정당화 주석 감사·데드락 정적 분석(생성-검증)을 에이전트 팀으로 조율하고 단일 동시성 리포트를 생성한다.

**트리거:** 동시성 검사, 락 감사, 데드락 분석, Lock-Free 검증, async 데드락, 컨텐션 분석 요청 시 `concurrency-guard-orchestrator` 스킬을 사용하라.

**변경 이력:**
| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-06-02 | 초기 구성 | 전체 | .NET 10 고성능 서버 동시성 하네스 구축 |
| 2026-09-12 | 산출물 경로를 `_workspace/concurrency-guard/` 하위로 격리, 새 실행 시 자기 디렉토리만 보관 이동 | concurrency-guard-orchestrator·전용 스킬 4종·에이전트 4종 | 모든 하네스가 `_workspace/` 루트를 공유해 산출물 파일명이 충돌하고 Phase 0 전체 이동이 타 하네스 산출물을 파괴(code-review 하네스 교차 검토에서 확인) |

---

## 하네스: GC 가드 (.NET 10 메모리 최적화)

**목표:** 힙 할당 스캐너·풀링 강제자 병렬 감사 → 교차 검증으로 GC 압력 유발 패턴을 제거하고 ValueTask·Span·ArrayPool을 올바르게 적용한다.

**트리거:** GC 억제, 힙 할당 감사, 메모리 최적화, ArrayPool 검사, ValueTask 검증, boxing 탐지, GC 압력 분석 요청 시 `gc-guard-orchestrator` 스킬을 사용하라.

**변경 이력:**
| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-06-02 | 초기 구성 | 전체 | .NET 10 서버 GC 억제 메모리 최적화 하네스 구축 |
| 2026-09-12 | 산출물 경로를 `_workspace/gc-guard/` 하위로 격리, 새 실행 시 자기 디렉토리만 보관 이동 | gc-guard-orchestrator·전용 스킬 3종·에이전트 3종 | 모든 하네스가 `_workspace/` 루트를 공유해 산출물 파일명이 충돌하고 Phase 0 전체 이동이 타 하네스 산출물을 파괴(code-review 하네스 교차 검토에서 확인) |

---

## 하네스: 파이프라인 아키텍처 (.NET 10 고성능 IO)

**목표:** System.IO.Pipelines 기반 Zero-copy IO 루프와 Channel<T> 락-프리 디스패처를 감독자 패턴으로 설계하고 부하 테스트 감사까지 수행한다.

**트리거:** Pipelines 설계, IO 루프 구현, 디스패처 설계, Zero-copy 서버, PipeReader 설계, Channel 디스패처 요청 시 `pipeline-architect-orchestrator` 스킬을 사용하라.

**변경 이력:**
| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-06-02 | 초기 구성 | 전체 | .NET 10 고성능 IO 파이프라인 아키텍처 하네스 구축 |
| 2026-09-12 | 산출물 경로를 `_workspace/pipeline/` 하위로 격리, 새 실행 시 자기 디렉토리만 보관 이동 | pipeline-architect-orchestrator·전용 스킬 3종·에이전트 4종 | 모든 하네스가 `_workspace/` 루트를 공유해 산출물 파일명이 충돌하고 Phase 0 전체 이동이 타 하네스 산출물을 파괴(code-review 하네스 교차 검토에서 확인) |

---

## 하네스: TDD (테스트 주도 개발)

**목표:** 요구사항 입력 시 Red(실패 테스트)→Green(최소 구현)→Refactor(검증·리팩토링) 사이클을 에이전트 팀으로 완주하고, harness-evolve로 명세 대비 최종 코드의 진화 델타를 포착한다.

**트리거:** TDD, 테스트 먼저 작성, Red-Green-Refactor, TDD 사이클, 기능 구현(TDD) 요청 시 `tdd-orchestrator` 스킬을 사용하라. 진화 리포트는 `/harness-evolve`로 수동 실행 가능.

**변경 이력:**
| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-06-02 | 초기 구성 | 전체 | TDD Red-Green-Refactor 하네스 구축 (harness-evolve 포함) |
| 2026-09-12 | 산출물·TddSession.csproj 경로를 `_workspace/tdd/` 하위로 격리, 새 사이클 시 자기 디렉토리만 보관 이동 | tdd-orchestrator·tdd 단계 스킬 3종·harness-evolve·에이전트 3종 | 모든 하네스가 `_workspace/` 루트를 공유해 산출물 파일명이 충돌하고 Phase 0 전체 이동이 타 하네스 산출물을 파괴(code-review 하네스 교차 검토에서 확인) |
