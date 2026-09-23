# PortfolioBlog — 보안 최우선 기술 블로그

단일 작성자용 기술 블로그입니다. 방문자에게는 **스크립트 없는 서버 렌더링 HTML**만 내보내고, 글쓰기는 **별도 서브도메인 + IP 허용 목록 + 비밀번호 세션** 뒤에 둡니다.

> **현재 상태 (2026-09-23):** 공개 사이트와 관리 에디터가 **동작합니다** — 도메인·DB 제약, 접근 제어(호스트·IP·CSRF), 로그인·세션, 관리 API, 마크다운 파이프라인·미리보기·이미지 첨부, 공개 페이지·검색·Atom·sitemap·보안 헤더, 관리 에디터 SPA까지 master에 병합됐습니다(.NET 테스트 591개 · Vitest 188개 · 브라우저 E2E 8개, Release 빌드 경고 0).
> **배포 구성(4단계: Docker Compose + Caddy)도 PR #5로 master에 병합됐습니다**(.NET 테스트 625개 · Vitest 194개 · 브라우저 E2E 8개 · 스택 E2E 8개, 배포 스모크 통과) → [배포 구성](docs/deployment.md) · [재개 가이드](plan/resume_guide_0921.md).

## 무엇을 만드나

| | |
|---|---|
| 기능 | 글 CRUD, 태그, 시리즈(연재 묶음), 이미지 첨부, 마크다운 에디터, 코드 하이라이팅, 검색, Atom 피드, SEO(Open Graph·sitemap) |
| 발행 모델 | 초안 상태 없음. **명시적 저장 = 즉시 공개** |
| 사용자 | 작성자 1명. 회원·댓글 없음 |
| 공개 표면 | ASP.NET Core 10 Razor Pages(서버 렌더링, JS 없음) + Markdig · ColorCode.HTML · HtmlSanitizer |
| 관리 표면 | 최소 API + React 19 · Vite · CodeMirror 6 SPA (`admin.<도메인>` 전용) |
| 데이터 | EF Core 10 + PostgreSQL 17, 첨부는 내용 주소(SHA-256) 파일 저장 |
| 배포 | Caddy + Docker Compose (4단계, master에 병합됨 — PR #5) |

## 왜 이렇게 만들었나

선택이 갈릴 때마다 편의보다 공격 표면 축소를 택했습니다.

- **공개 페이지에 JS가 없습니다** → CSP를 `default-src 'none'`까지 조일 수 있어 저장형 XSS가 들어와도 실행되지 않고, npm 공급망 사고가 방문자에게 닿지 않습니다.
- **관리 화면이 서브도메인으로 분리돼 있습니다** → 세션 쿠키가 공개 호스트로 가지 않고, 공개 도메인에는 `/api` 자체가 존재하지 않습니다.
- **쓰기 = 허용 IP AND 비밀번호 세션** → 네트워크 위치 하나에 의존하지 않고, 로그인 엔드포인트는 허용 IP에서만 열립니다.
- **접근 검사가 요청 본문을 읽기 전에 끝납니다** → 미인증 요청이 업로드 본문을 서버에 버퍼링시키지 못합니다.
- **HTML을 저장하지 않고 요청마다 렌더링합니다** → 렌더러의 보안 수정이 과거 글 전체에 즉시 적용됩니다.

자세한 근거와 수용한 잔여 위험: [보안 설계](docs/security.md).

## 로드맵

| 단계 | 내용 | 상태 |
|---|---|---|
| 설계 | 스펙 작성, Codex 교차 검토 반영 | 완료 |
| 0 | 솔루션 정리(`PortfolioBlog`로 개명, `.slnx` 전환, 템플릿 잔재 제거) | 완료 |
| 1 | 도메인·DB 제약, 접근 제어(호스트·IP·CSRF), 로그인·세션 폐기, 글·시리즈·태그 관리 API | 완료 |
| 2 | 마크다운 파이프라인·첨부(2A), 공개 페이지·검색·Atom·sitemap·보안 헤더·속도 제한(2B) | 완료 |
| 3 | 관리 에디터 SPA(글·시리즈·태그·첨부, sandbox 미리보기, 실제 백엔드 E2E) | 완료 |
| 4 | Docker Compose · Caddy · DB 롤 분리 · 백업/복원 · 배포 스모크 | **완료(병합)** — PR #5 |
| 이후 | 글쓰기·읽기 경험 개선(노션식 편집·보기) | 설계 전 |

단계별로 무엇을 만들고 어떤 결함을 어디서 잡았는지: [진행 기록](docs/history.md).

## 빠른 시작

```powershell
git clone https://github.com/BerryBless/WebProject.git
cd WebProject
Copy-Item scripts/git-hooks/commit-msg .git/hooks/
dotnet build PortfolioBlog.slnx -c Release   # 경고 0 / 오류 0
dotnet test  PortfolioBlog.slnx -c Release   # 625개 — Docker 필요(Testcontainers)
```

.NET 10 SDK와 Docker가 필요하고, 관리 SPA를 띄우려면 Node 24가 필요합니다. 실제로 띄워 보는 절차(개발용 PostgreSQL, 비밀번호 해시, HTTPS 프로필, SPA 개발 서버)는 [개발 환경](docs/development.md)에 있습니다.

> Windows Docker Desktop에서는 드물게 테스트 1개가 DB 연결 타임아웃으로 실패합니다 — 다시 실행하면 통과합니다([테스트](docs/testing.md)).

## 문서

| 문서 | 내용 |
|---|---|
| [아키텍처](docs/architecture.md) | 신뢰 경계와 라우팅, 미들웨어 순서, 접근 판정, 로그인·세션, 마크다운 파이프라인, 데이터 모델, 코드 지도 |
| [보안 설계](docs/security.md) | 결정과 근거, 접근 계약, 응답 헤더·CSP, 자원 제한, 첨부 처리, 관리 SPA의 경계, 수용한 잔여 위험 |
| [개발 환경](docs/development.md) | 준비물, 로컬 실행(API·SPA), 마이그레이션, 미리보기 스냅숏 갱신, 코드 규칙, 자주 밟는 함정 |
| [테스트](docs/testing.md) | 테스트 지형과 실행, 이 저장소의 테스트 규칙(사보타주), 필수 통과 항목, CI, 알려진 문제 |
| [설정 키](docs/configuration.md) | 전체 설정 키와 기본값, 시작 시 검증되는 조건 |
| [배포 구성](docs/deployment.md) | 목표 토폴로지, 이미지·compose·Caddy, DB 롤 분리, 스모크, 운영 확인 항목 |
| [진행 기록](docs/history.md) | 단계별로 만든 것과 발견한 결함, 이 저장소가 일하는 방식 |
| [작업일지](docs/worklog.md) | 단계별 고민과 판정, 틀렸던 것, 사용자 흐름·시퀀스·처리 흐름 다이어그램(Mermaid) |
| [개발 하네스](docs/harness.md) | AI 협업 구성(에이전트·스킬·훅·CI·Codex 교차 검증) |

설계·계획 원본:

| 문서 | 내용 |
|---|---|
| [`plan/tech_blog_0920.md`](plan/tech_blog_0920.md) | **전체 설계 스펙**(구속력 있는 기준): 설계 결정과 대안 비교, 접근 계약, 헤더·CSP, 자원 제한, 배포, 필수 테스트, Codex 검토 반영표 |
| [`plan/resume_guide_0921.md`](plan/resume_guide_0921.md) | **작업 재개 가이드**: 현재 상태, 5분 점검, 다음 작업과 남은 결함, 실행이 끊겼을 때 복구 |
| [`plan/tech_blog_2a_report_0921.md`](plan/tech_blog_2a_report_0921.md) · [`2b`](plan/tech_blog_2b_report_0921.md) · [`3`](plan/tech_blog_3_report_0922.md) | 단계별 실행 보고서: 검증 근거, 계획 결함과 교훈, 내린 판정, 수용한 잔여 위험 |
| [`docs/superpowers/plans/`](docs/superpowers/plans/) | 단계별 구현 계획(스파이크 측정값·설계 결정·작업별 TDD 단계) |
| [`plan/para_notes_0917.md`](plan/para_notes_0917.md) | 폐기된 이전 설계(PARA 노트앱). 결정 이력 보존용 |
| [`CLAUDE.md`](CLAUDE.md) / [`AGENTS.md`](AGENTS.md) | 프로젝트 규칙 (Claude Code / Codex) |

## 저장소 구조

```
PortfolioBlog.slnx
├─ PortfolioBlog.Api/          # ASP.NET Core 10 — 관리 API(최소 API) + 공개 페이지(Razor Pages)
├─ PortfolioBlog.Api.Tests/    # xUnit + WebApplicationFactory + Testcontainers PostgreSQL
├─ PortfolioBlog.Web/          # 관리 에디터 SPA — React 19 + Vite + CodeMirror 6
├─ deploy/                     # docker-compose · Caddyfile · 운영 절차
├─ docs/                       # 이 문서들 + 구현 계획 (docs/generated/ 는 문서화 하네스 생성물)
├─ doc-harness/                # 문서화 하네스 — `문서화` 한마디로 docs/generated 생성·증분 갱신
├─ plan/                       # 설계 스펙 · 실행 보고서 · 재개 가이드
└─ .claude/ .agents/ .codex/ scripts/   # 개발 하네스
```

## 개발 하네스

설계와 구현은 Claude Code가 진행하고 OpenAI Codex CLI가 read-only로 교차 검증합니다. 에이전트 25종·스킬 27종, 커밋 메시지 형식 훅, 쓰기 범위 훅, 자동 커밋의 비밀값 스캐너, 구조 감사 스크립트, 그리고 코드를 근거로 기술 문서를 생성·증분 갱신하는 문서화 하네스(`doc-harness/`, 트리거 `문서화`)가 들어 있습니다 → [개발 하네스](docs/harness.md).
