# 프로젝트 개요

<!-- doc-harness:section id="one-liner" hash="6aabfa22544dbbd29d43b9e8cf30085234fd28ce4f0ae4ff06747cbc4d03c216" -->
PortfolioBlog는 단일 작성자용 보안 중심 기술 블로그로, JS 없는 서버 렌더링 공개 사이트(Razor Pages)와 별도 서브도메인·IP 허용 목록·비밀번호 세션 뒤의 관리 React SPA를 ASP.NET Core 10 + PostgreSQL 17 백엔드와 Docker Compose + Caddy로 배포한다.
<!-- /doc-harness:section -->

<!-- doc-harness:section id="summary" hash="210b5bc8728115cb58e56d659d057f44ae17a5176b05bc954b0ef08ff6482341" -->
## 한 줄 요약

PortfolioBlog는 한 명의 작성자가 운영하는 보안 중심 기술 블로그다. 공개 사이트(방문자용)는 JS 없는 서버 렌더링 Razor Pages이고, 관리 표면(작성자용)은 별도 관리 도메인에서 IP 허용 목록과 비밀번호 세션으로 보호되는 React SPA다. 두 표면은 하나의 ASP.NET Core 10 API 프로세스를 공유하며 Caddy가 호스트별로 분기한다. (README 기준 설명은 INFERRED, 구성 요소는 코드로 CONFIRMED)
<!-- /doc-harness:section -->

<!-- doc-harness:section id="key-points" hash="4a2d08c77c5a4379f4317c87141b1cb9383c85ae913ce1dde128504e4de798a8" -->
## 핵심 내용

**목적**: 마크다운으로 글을 쓰고, 정제된 HTML로 안전하게 공개하는 개인 기술 블로그.

**사용자**: 방문자(공개 글 열람·검색·피드 구독)와 단일 관리자(글·시리즈·태그·첨부 이미지 관리).

**구성 요소**

| 구성 요소 | 경로 | 역할 |
|---|---|---|
| 백엔드 API | PortfolioBlog.Api | 관리용 최소 API(Features/), 공개 Razor Pages(Pages/), 인프라(Infrastructure/Access,Data,Markdown,Storage,Web) |
| API 테스트 | PortfolioBlog.Api.Tests | xUnit + WebApplicationFactory + Testcontainers.PostgreSql |
| 관리 SPA | PortfolioBlog.Web | React 19 + Vite + CodeMirror 6, Vitest/Playwright 테스트 |
| 운영 배포 | deploy | docker-compose.yml/.smoke.yml, Caddyfile, backup.sh/restore.sh, postgres-init, smoke, OPERATIONS.md |
| CI | .github/workflows | ci.yml |
| 문서 | docs, plan | 제품·아키텍처·보안 문서와 설계·진행 기록 |

**기술 스택**

| 영역 | 기술 |
|---|---|
| 백엔드 언어·런타임 | C#, .NET 10(net10.0), aspnet:10.0.12 런타임 |
| 백엔드 프레임워크 | ASP.NET Core 10(Microsoft.NET.Sdk.Web), 최소 API + Razor Pages, EF Core 10 |
| DB | PostgreSQL 17 |
| 마크다운 | Markdig, ColorCode.HTML, HtmlSanitizer |
| 프런트엔드 | TypeScript, React 19, Vite 8, @tanstack/react-query, react-router, CodeMirror 6, Tailwind CSS 4 |
| 프런트 런타임 | Node.js 24 (빌드·스모크 클라이언트) |
| 배포 | Docker Compose, Caddy(TLS/ACME, 호스트별 프록시), POSIX shell 스크립트 |
| 개발용 로컬 도구 | PowerShell 스크립트(scripts/) |
<!-- /doc-harness:section -->

<!-- doc-harness:section id="details" hash="33df7c836b1f9e4329c2ba61aa8da0e14c009b42a68e72862e0244ae5e54f246" -->
## 상세 내용

**공개 표면과 관리 표면의 분리.** 공개 도메인은 Caddy가 GET·HEAD만 API로 프록시하고 /api는 404로 막는다. 관리 도메인은 /api/*·/attachments/*만 API로 보내고 나머지는 빌드된 SPA(/srv)를 서빙한다. API 쪽에서도 AdminSurfaceMiddleware가 관리 호스트·허용 IP·X-Requested-With 헤더·Origin을 검사한다(F018, F025).

**공개 조회와 관리 쓰기의 DB 분리.** 공개 조회는 statement_timeout·읽기 전용 트랜잭션이 걸린 PublicDbContext를 쓰고, 기동 시 PublicRoleGrants가 공개 롤(blog_public)에 읽기 테이블 SELECT만 재부여한다(F021).

**주요 기능 묶음**

| 묶음 | 기능 | 상세 |
|---|---|---|
| 관리 SPA | 로그인·글 CRUD·미리보기·임시본·충돌 해결·시리즈·태그·첨부 | F001~F009, F029 |
| 렌더링 | MarkdownRenderer, RenderGate, RenderedPostCache | F011 |
| 공개 사이트 | 홈·글 상세·검색·시리즈·태그·피드·사이트맵 | F012~F017, F010 |
| 횡단 관심사 | 접근 통제·속도 제한·보안 헤더·오류 처리 | F018~F020 |
| 기동·운영 CLI | 부트스트랩, healthcheck, hash-password | F021~F023 |
| 백그라운드 | AttachmentJanitor | F024 |
| 배포 | Caddy, Compose, 백업/복원, 스모크 | F025~F028 |

**개발 도구(하네스)는 제품과 분리되어 있다.** .claude(에이전트·스킬), .agents(미러된 스킬), .codex(Codex 에이전트 설정), scripts/(auto-commit.ps1, harness-audit.ps1, hooks/guard-write-scope.ps1), doc-harness/(문서 생성 하네스)는 AI 개발 워크플로용이며 제품 런타임 기능이 아니다. CLAUDE.md·AGENTS.md도 개발 도구 규칙 문서다. doc-harness는 이 분석의 대상에서 제외되어 내용은 확인하지 않았다(INFERRED).
<!-- /doc-harness:section -->

<!-- doc-harness:section id="evidence" hash="78ea94df0d6c587c58c414d58ae3115a4b80f52f2d49df3c1f6099a1950eb023" -->
## 코드 근거

| 주장 | 근거 파일 | 상태 |
|---|---|---|
| 프로젝트 설명·솔루션 구성 | README.md(1-18), PortfolioBlog.slnx(1-5) | INFERRED |
| 백엔드 .NET 10, Web SDK | PortfolioBlog.Api/PortfolioBlog.Api.csproj(1-6) | CONFIRMED |
| 마크다운 라이브러리 | PortfolioBlog.Api/PortfolioBlog.Api.csproj(22-24) | CONFIRMED |
| 백엔드 런타임 이미지 | PortfolioBlog.Api/Dockerfile(4,23) | CONFIRMED |
| 프런트 의존성 | PortfolioBlog.Web/package.json(17-40) | CONFIRMED |
| Vite·Tailwind 플러그인 | PortfolioBlog.Web/vite.config.ts(3-4,30) | CONFIRMED |
| 프런트 TypeScript | PortfolioBlog.Web/tsconfig.json, PortfolioBlog.Web/src/main.tsx | CONFIRMED |
| Node 24 | PortfolioBlog.Web/Dockerfile(4), .github/workflows/ci.yml(38-40) | CONFIRMED |
| 운영 스크립트 | deploy/backup.sh | CONFIRMED |
| 로컬 도구 스크립트 | scripts/auto-commit.ps1, scripts/harness-audit.ps1, scripts/hooks/guard-write-scope.ps1 | CONFIRMED |
| AI 하네스 정의 | .claude/agents/architecture-reviewer.md, .claude/skills/tdd-orchestrator/SKILL.md | CONFIRMED |
| Codex 대응 설정 | .codex/agents/architecture-reviewer.toml, CLAUDE.md, AGENTS.md | INFERRED |
| 교차 검증 스킬 | .claude/skills/cross-verify/scripts/invoke-codex.ps1 | CONFIRMED |
| 진행 문서 | docs/superpowers/plans/2026-09-23-doc-harness.md, plan/resume_guide_0921.md | CONFIRMED |
<!-- /doc-harness:section -->

<!-- doc-harness:section id="caveats" hash="e430f45d6edc132121950f904646404577765b0da992341787e14a23b92aecef" -->
## 주의사항

- **.claude/skills와 .agents/skills는 동일 목록이 아니다.** .agents/skills는 .claude/skills의 일부만 미러한다. codex와 cross-verify 스킬은 .claude/skills에만 있고 .agents/skills에는 없다. 스킬을 추가·수정할 때 두 위치의 차이를 전제로 하고, 목록이 같다고 가정하지 않는다. (03_DIRECTORY_STRUCTURE.md 트리에서도 확인된다.)
- .claude/agents와 .codex/agents/*.toml은 파일명이 대응하지만 대응 관계는 INFERRED다.
- 프로젝트 설명 중 공개/관리 분리 의도는 README 기준(INFERRED)이며, 실제 동작은 각 기능 문서와 코드로 확인해야 한다.
- 공개 조회는 PublicDbContext(읽기 전용, statement_timeout)를 쓰므로 공개 페이지에서 쓰기 경로를 추가하려면 이 분리를 깨지 않도록 주의한다.
- doc-harness, docs, plan, .claude, .agents, .codex, scripts는 제품 배포물이 아니다. 제품 변경과 섞어 해석하지 않는다.
<!-- /doc-harness:section -->

<!-- doc-harness:section id="related" hash="93398b9b2eba1baa069ef1ba6d82dfd4337ea58bb8982287ec2870f2af87ca9b" -->
## 관련 문서

- [00_EXECUTIVE_SUMMARY](00_EXECUTIVE_SUMMARY.md)
- [02_ARCHITECTURE](02_ARCHITECTURE.md)
- [03_DIRECTORY_STRUCTURE](03_DIRECTORY_STRUCTURE.md)
- [04_SETUP_AND_RUN](04_SETUP_AND_RUN.md)
- [06_DEPENDENCIES](06_DEPENDENCIES.md)
- [08_API](08_API.md)
- [09_FEATURES](09_FEATURES.md)
- [13_SECURITY](13_SECURITY.md)
- [16_DEPLOYMENT](16_DEPLOYMENT.md)
<!-- /doc-harness:section -->

<!-- doc-harness:section id="unknowns" hash="db33b48e183e5f4234896a8be2f4bf4601169ab6a2e011d05db88e3177f1df18" -->
## 확인하지 못한 것

- doc-harness 디렉터리의 실제 구성과 동작(분석 대상 제외)
- README.md에 기술된 공개/관리 분리 의도의 세부는 이 문서에서 직접 코드로 재검증하지 않음
- PostgreSQL 17 버전 명시 위치(compose 이미지 태그 등)는 이 문서 작성 중 직접 확인하지 않음
<!-- /doc-harness:section -->
