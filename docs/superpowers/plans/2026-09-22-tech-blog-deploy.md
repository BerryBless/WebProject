# 기술 블로그 4단계: 배포(Docker Compose · Caddy · 백업/복원 · 운영 절차) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 1~3단계에서 만든 공개 사이트·관리 API·관리 SPA를 서버 한 대에 올릴 수 있는 배포 구성(이미지 2개, compose, Caddyfile, DB 롤, 백업·복원, 운영 문서)을 만들고, 그 구성을 **운영과 같은 이미지·같은 Caddyfile로 띄워 공격해 보는 스모크 테스트**를 CI에 붙인다.

**Architecture:** 인터넷 → Caddy(TLS 종단, 고정 IP, 사이트 2개) → api(비루트·읽기 전용 루트 FS·포트 미공개) → postgres(내부 전용 네트워크). 관리 SPA는 Caddy 이미지에 구워 정적 서빙하고 `/api/*`·`/attachments/*`만 백엔드로 보낸다. 앱은 슈퍼유저로 DB에 붙지 않으며, 공개 페이지는 `SELECT`만 가진 별도 롤로 조회한다(2B의 잔여 위험 "읽기 전용은 세션이 끌 수 있다"를 닫는다). 스모크는 compose 네트워크 안의 고정 IP 컨테이너 둘(허용 목록 안·밖)에서 Node 테스트 러너로 찌른다.

**Tech Stack:** Docker Compose v2, Caddy 2.11.4, PostgreSQL 17.11, .NET 10(`aspnet:10.0.12-noble-chiseled-extra`), Node 24.21.0(SPA 빌드·스모크 러너), bash(백업·복원·스모크 오케스트레이터), Playwright(스택 대상 E2E), GitHub Actions.

**Spec:** `plan/tech_blog_0920.md` — 3.1(신뢰 경계), 3.3(접근 제어), 3.6(응답 헤더), 3.7(자원 제한), 3.10(배포), 7절(쓰기 권한 없는 DB 롤). 인계: `plan/resume_guide_0921.md` 3절, `plan/tech_blog_2b_report_0921.md` 6·8절, `plan/tech_blog_3_report_0922.md` 6·8절.

**범위 밖(이 계획이 하지 않는 것):** 실제 서버·도메인에 올리는 일(사용자의 서버·DNS 정보가 필요한 대외 작업이다 — `deploy/OPERATIONS.md`가 그 절차다), CDN·로드밸런서 앞단, 다중 인스턴스, 모니터링·알림, 이미지 레지스트리 푸시, WebKit 검증, "노션처럼" 개편(스펙 7절 TODO).

## Global Constraints

- **보안이 최우선.** 선택지가 갈리면 더 엄격한 쪽. 이 계획의 코드는 전부 저장소 밖 복사본에서 실제로 빌드·기동·공격해 본 것이다(아래 스파이크 표). 그래도 **계획 코드가 틀렸을 수 있다** — 리뷰어는 직접 띄워서 찌르고 잰다.
- 실제 비밀번호·해시·도메인·IP를 커밋하지 않는다. 예시는 `example.com`·`*.example.test`·`203.0.113.0/24`·`2001:db8::/32`·`192.0.2.0/24`만. 비밀값 자리는 `changeme`·`dummy`·`example`·`${…}` 중 하나를 포함해야 한다(Stop 훅의 비밀값 스캐너가 `Password=…` 꼴을 막는다).
- 비밀번호·쿠키·요청 본문은 어떤 로그에도 남기지 않는다. 예외 메시지에 연결 문자열 값을 넣지 않는다(설정 키만).
- 절대 경로(`E:\…`, `/home/…`)를 설정·스크립트에 하드코딩하지 않는다. 스크립트는 자기 위치 기준으로 움직인다(`cd "$(dirname "$0")"`).
- NUL 문자는 C#의 백슬래시-0 이스케이프나 `String.fromCharCode(0)`으로만 쓴다. 6글자 유니코드 이스케이프 표기는 코드·주석·문서·셸 어디에도 쓰지 않는다.
- 줄 끝: `PortfolioBlog.Api`·`PortfolioBlog.Api.Tests`의 새 `.cs`는 **CRLF**. `deploy/**`, 모든 `Dockerfile`, `.dockerignore`, `*.sh`는 **LF**(Task 2가 `.gitattributes`로 강제한다 — 컨테이너에 바인드 마운트되는 셸 스크립트가 CRLF면 뜨지 않는다). 기존 파일은 그 파일의 줄 끝을 유지한다.
- C# XML 문서 주석은 CLAUDE.md "적용 범위" 표대로. 내용 없는 상용구 `<remarks>` 금지. 테스트 메서드는 `<summary>`만(무엇을 증명하는지, 왜 그 입력인지).
- 네트워크·메모리 타입 선언에는 내부 동작을 근거로 한 인라인 주석.
- 이미지 태그는 **정확한 버전**으로 고정한다: `mcr.microsoft.com/dotnet/sdk:10.0.401`, `mcr.microsoft.com/dotnet/aspnet:10.0.12-noble-chiseled-extra`, `node:24.21.0-alpine`, `caddy:2.11.4-alpine`, `postgres:17.11-alpine`. 새 npm·NuGet 패키지는 추가하지 않는다.
- 커밋 메시지: `{접두사}: {제목}`(접두사 추가/수정/버그수정/리팩토링/문서/테스트/의존성, 제목 50자 이내·WHY 중심·파일명 나열 금지), 본문은 `- ` 항목, 마지막 줄은 정확히 `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
- 매 커밋 전: `dotnet build PortfolioBlog.slnx -c Release` 경고 0·오류 0, 해당 테스트 통과. 기준선: .NET **591**, Vitest **188**, E2E **8**.
- 포트: 스모크 스택은 루프백 `8081`·`8443`과 서브넷 `172.30.0.0/24`를 쓴다. 기존 E2E는 `7198`·`4173`·`5433`. 둘은 겹치지 않지만, 같은 기계에서 스모크 둘을 동시에 돌리지 않는다.
- 이 PC의 AdGuard는 평문 HTTP 응답을 고친다. 호스트에서 찌를 때는 HTTPS만 믿는다(스모크는 컨테이너 안에서 돌아 영향이 없다).

## 스파이크 — 계획을 쓰기 전에 실제로 잰 것 (2026-09-22, Docker Desktop 29.5.3 / Windows 11)

| # | 무엇을 쟀나 | 결과 → 계획에 반영된 것 |
|---|---|---|
| S1 | 기준 이미지의 실제 버전 | SDK 10.0.401, ASP.NET 10.0.12(chiseled-extra: 기본 사용자 1654, 셸 없음), Caddy 2.11.4, PostgreSQL 17.11, Node 24.21.0 → 태그 고정 |
| S2 | `dotnet publish -c Release` 출력 | EF 디자인 타임 어셈블리는 **publish 출력에 없다**(인계 항목의 우려는 build 출력 얘기였다). `wwwroot`는 `css/site.css` 하나. `web.config`·apphost가 섞여 나온다 → 지우고, 모양을 Dockerfile의 빌드 단계에서 `test`로 고정 |
| S3 | chiseled + `read_only` + `tmpfs /tmp` + `cap_drop: ALL` | 앱이 뜨고 10MiB 업로드가 된다(multipart 버퍼는 `/tmp`). 빈 named volume은 이미지의 `/data/*`(1654, 0700)로 초기화된다. Data Protection 키가 `/data/dpkeys`에 남는다 |
| S4 | Caddy에 고정 IP `172.30.0.2` | api가 먼저 떠서 그 주소를 받아 가면 caddy가 `Address already in use`로 못 뜬다(실측) → `ip_range: 172.30.0.128/25`로 동적 할당을 위쪽 절반에 가둔다 |
| S5 | `*.localhost` 사이트 | Caddy가 자동으로 내부 CA 인증서를 쓴다(설정 분기 불필요). 루트 인증서는 `caddy_data`의 `caddy/pki/authorities/local/root.crt` → 스모크가 그것으로 **TLS를 검증**한다(`-k` 아님) |
| S6 | 호스트에서 공개 포트로 들어온 요청의 `remote_ip` | Docker Desktop에서는 `172.30.0.1`(게이트웨이). 호스트 NAT 동작에 기대면 "비허용 IP" 검사가 환경마다 달라진다 → 스모크는 **고정 IP 컨테이너 둘**(`.10` 허용, `.11` 비허용)에서 찌른다. 브라우저 E2E만 호스트에서 들어오므로 스모크 허용 목록에 `172.30.0.1/32`를 넣는다 |
| S7 | 공개 사이트의 `/api` 차단을 표기 변형으로 우회 | `/API/posts`, `/%61pi/posts`, `//api/posts`, `/x/../api/posts`(정규화 없이 전송) 전부 Caddy의 본문 없는 404 |
| S8 | `Server`·`Via` 헤더 제거 | 즉시 실행되는 `header -Server`는 `file_server`의 404와 오류 경로에서 다시 붙고, `header >Via ""`는 빈 `Via:`를 남긴다(실측) → `defer`가 든 블록에서 `-Server`·`-Via`. 포트 80의 자동 리다이렉트(308)에는 `Server: Caddy`가 남는다 — 수용 |
| S9 | 관리 사이트의 헤더 위치 | SPA용 `header` 블록을 `reverse_proxy` **뒤**에 두면 백엔드 응답의 CSP(첨부의 `default-src 'none'; sandbox`)가 그대로다. `/assets/*`를 `handle`로만 가르면 **없는 파일의 404에 `immutable` 1년 캐시**가 붙는다(실측) → `file` 매처로 있는 파일에만 |
| S10 | Caddy를 지난 업로드 한도 | 10.2MB → 201, 10.7MB → 앱의 413(`첨부가 너무 큽니다`), 12.5MB(길이 선언) → 프레임워크 413, 12MiB chunked → 413, 9000자 URL → 414, 공개 사이트에 100KB POST → Caddy 413. `request_body`는 `11MiB`(=11,534,336 — `11MB`는 11,000,000이라 프레임워크 값과 어긋난다) |
| S11 | Caddy 액세스 로그 | `Cookie`·`Set-Cookie`가 `REDACTED`. 로그인 비밀번호 문자열 0건 |
| S12 | DB 롤 | 마이그레이션은 슈퍼유저가 아닌 소유자 롤로 된다. `ALTER DEFAULT PRIVILEGES`로 일괄 부여하면 공개 롤이 **`AdminState`·마이그레이션 이력까지 읽는다**(실측) → 앱이 시작할 때 허용 테이블 5개만 명시 부여. 테스트 팩토리 전체를 `SELECT` 전용 롤로 돌려도 기존 591개가 전부 통과(읽기 전용 위반의 SQLSTATE `25006`이 권한 오류보다 먼저다) |
| S13 | 테스트 팩토리마다 Data Protection 키 폴더를 따로 주기 | `AuthEndpointsTests.HashRotation_…ControlFactory…`가 깨진다(두 호스트가 같은 키 링을 공유한다고 전제) → 전 팩토리 공유 폴더 |
| S14 | 백업 → `down -v` → 복원 | Git Bash가 `/data` 같은 인자를 Windows 경로로 바꾼다 → `MSYS_NO_PATHCONV=1`. 새 볼륨에 tools(1654)가 쓰려면 **api 컨테이너를 먼저 "만들기만"** 해야 볼륨이 1654 소유로 초기화된다. `pg_restore --clean --if-exists --single-transaction` 성공, 첨부 sha256 일치 |
| S15 | 기존 Playwright E2E를 스택에 | Node 쪽 `request` 픽스처는 Windows에서 `*.localhost`를 못 푼다(`ENOTFOUND`) → 그 두 테스트를 브라우저 기반으로. 첨부 목록에 큰 이미지가 남아 있으면 Firefox가 중단된 로드를 `Image corrupt or truncated` 콘솔 오류로 찍는다(간헐) → 스모크는 자기가 올린 큰 첨부를 지운다. 고친 스펙으로 스택 8/8, 기존 구성 8/8 |
| S16 | `deploy/smoke/run.sh` 전체(E2E 포함) | 첫 완주에서 exit 0: 스모크 8 + 5, DB 롤 검사, 복원 리허설, 복원된 스택에서 스모크 8 재통과, E2E 8 |

## 설계 결정 (질문 없이 추천안으로 — 보고서의 "내린 판정"에 옮긴다)

| # | 결정 | 버린 대안 | 이유 |
|---|---|---|---|
| D1 | 런타임 이미지는 `aspnet:…-noble-chiseled-extra` | 일반 `aspnet`(셸·apt 있음), chiseled(ICU 없음) | 셸이 없으면 RCE 뒤의 발판이 준다. `extra`는 ICU를 넣어 한글 비교·정규화가 개발·테스트와 같게 돈다 |
| D2 | 모든 서비스 `cap_drop: ALL` + `no-new-privileges`, api·caddy는 `read_only` | 기본값 | caddy는 `NET_BIND_SERVICE`만, postgres는 초기화에 필요한 5개만 되돌린다 |
| D3 | 네트워크 둘: `edge`(caddy·api), `db`(api·postgres, `internal`) | 단일 네트워크 | caddy가 뚫려도 DB 포트에 닿지 않는다 |
| D4 | DB 롤 셋: `postgres`(운영자 전용) · `blog_app`(소유자, 앱) · `blog_public`(공개 조회, `SELECT` 5개 테이블). 부여는 **앱이 시작마다** 한다 | init 스크립트의 `DEFAULT PRIVILEGES` | S12. 허용 목록이 코드와 함께 버전 관리되고 테스트된다 |
| D5 | Development가 아니면 `ConnectionStrings:Public`과 `DataProtection:KeysPath`(절대 경로)가 **필수** | 선택 | 빠뜨리면 조용히 약해지는 설정은 시작 실패로 만든다(이 저장소의 일관된 규칙) |
| D6 | 헬스체크는 앱의 CLI 경로(`healthcheck`) | 이미지에 curl 추가, bash `/dev/tcp` | chiseled에는 셸이 없다. Host 헤더 요구사항을 C# 테스트로 고정할 수 있다 |
| D7 | Caddyfile과 SPA를 caddy 이미지에 굽는다. `admin off` | 바인드 마운트 + `caddy reload` | 서버에 고칠 수 있는 설정 파일을 두지 않는다. 빌드에서 `caddy validate` |
| D8 | 관리 사이트 헤더 = `admin-headers.ts` 다섯 개 + HSTS(`includeSubDomains`, 백엔드와 같은 값) + `Cross-Origin-Opener-Policy: same-origin` | COOP 생략 | 팝업을 쓰지 않는 SPA라 비용이 없다. 일치는 Vitest(정적)와 스모크(실제 응답)가 본다 |
| D9 | 스모크 러너는 `node --test`(컨테이너), HTTP는 `node:http(s)` | curl+bash, fetch | 경로를 정규화하지 않고 보내야 한다. Node 24는 `admin-headers.ts`를 그대로 import한다(타입 제거 내장) |
| D10 | 백업은 무중단, **DB 먼저 → 첨부 나중** | api 정지 후 백업 | 내용 주소 파일은 덮어써지지 않는다. 어긋남은 "행 없는 파일"(청소 잡이 지움) 쪽으로만 난다 |
| D11 | `restore.sh`는 `--yes` 없이는 아무것도 하지 않는다 | 확인 프롬프트 | 비대화형(CI 리허설)에서도 같은 경로를 쓴다 |
| D12 | 이미지는 정확한 버전 **태그**로 고정(다이제스트 아님) | `@sha256:` 고정 | 다이제스트는 기준 OS의 보안 패치 재빌드까지 막는데, 자동 갱신 도구가 아직 없다 → 잔여 위험으로 기록 |
| D13 | SPA 이미지 빌드에 `npm audit`(high 이상이면 실패), `NPM_AUDIT=off` 탈출구 | 빌드에서 생략 | 배포 시점의 게이트. 무관한 긴급 수정을 막을 때만 끈다(운영 문서에 명시) |
| D14 | 스모크 도메인은 `blog.localhost`·`admin.blog.localhost` | hosts 파일 수정, 외부 와일드카드 DNS | 브라우저가 루프백으로 풀고 Caddy가 내부 CA를 쓴다 — 외부 의존 0 |
| D15 | 실제 서버 배포는 하지 않는다 | — | 대외 작업. 절차는 `OPERATIONS.md`, 검증은 스모크가 대신한다 |

## 파일 구조

```
.dockerignore                                   # 신규: 허용 목록 방식(전부 제외 후 필요한 것만)
.gitattributes                                  # 신규: deploy/**·Dockerfile·*.sh는 LF
.gitignore                                      # 수정: deploy/backups/, deploy/smoke/backups/
.github/workflows/ci.yml                        # 수정: deploy-smoke 잡
PortfolioBlog.Api/
├─ Dockerfile                                   # 신규
├─ Program.cs                                   # 수정: healthcheck CLI 경로, 마이그레이션 뒤 권한 부여
├─ Infrastructure/Data/PublicRoleGrants.cs      # 신규: 공개 롤 권한(허용 테이블 SELECT)
├─ Infrastructure/Data/DataServiceCollectionExtensions.cs   # 수정: 공개 컨텍스트가 ConnectionStrings:Public을 쓴다
├─ Infrastructure/Web/HealthCheckCommand.cs     # 신규
└─ Infrastructure/Access/StartupValidation.cs   # 수정: Public 연결 검사, 비개발 환경 필수 2건
PortfolioBlog.Api.Tests/
├─ Infrastructure/PostgresContainerFixture.cs   # 수정: 공개 롤 생성
├─ Infrastructure/ApiFactory.cs                 # 수정: 모든 팩토리가 공개 롤·공유 키 폴더를 쓴다
├─ Infrastructure/PublicRoleGrantsTests.cs      # 신규
├─ Infrastructure/HealthCheckCommandTests.cs    # 신규
└─ Features/StartupValidationTests.cs           # 수정
PortfolioBlog.Web/
├─ Dockerfile                                   # 신규: node 빌드 → caddy 이미지
├─ playwright.stack.config.ts                   # 신규: 띄워져 있는 스택을 대상으로 하는 E2E 설정
├─ e2e/admin.spec.ts                            # 수정: 출처를 환경변수로, request 픽스처 테스트 2개를 브라우저 기반으로
└─ src/test/caddyfile.test.ts                   # 신규: Caddyfile 헤더 == admin-headers.ts + HSTS
deploy/
├─ Caddyfile · docker-compose.yml · .env.example · OPERATIONS.md
├─ postgres-init/10-roles.sh                    # 최초 초기화: 롤 셋과 DB
├─ backup.sh · restore.sh
├─ docker-compose.smoke.yml                     # 스모크 전용 덮어쓰기(고정 IP 클라이언트 둘)
└─ smoke/run.sh · smoke/smoke.test.mjs
```

**기존 소비자 목록(기반을 바꾸는 Task 1이 건드리는 것의 사용처):** `ConnectionStrings:Default`로 공개 연결을 조립하던 곳은 `DataServiceCollectionExtensions.AddBlogData` 한 곳이다. `PublicDbContext`를 주입받는 곳(`PublicQueries`, `Pages/*`, `SiteEndpoints`, `Features/Attachments`의 공개 GET)은 그대로다 — 연결 문자열만 바뀐다. `ApiFactory.Dispose`가 공개 풀을 비울 때 같은 문자열을 조립해야 한다(바꿨다). `PublicDbContextTests`는 `factory.ConnectionString`(관리 연결)으로 공개 연결을 직접 조립하므로 영향이 없다(S12에서 통과 확인).

---

### Task 1: 백엔드 — 공개 조회 전용 DB 롤, 시작 검증 강화, 헬스체크 CLI

**Files:**
- Create: `PortfolioBlog.Api/Infrastructure/Data/PublicRoleGrants.cs`, `PortfolioBlog.Api/Infrastructure/Web/HealthCheckCommand.cs`
- Modify: `PortfolioBlog.Api/Program.cs`, `PortfolioBlog.Api/Infrastructure/Data/DataServiceCollectionExtensions.cs`, `PortfolioBlog.Api/Infrastructure/Access/StartupValidation.cs`
- Test: `PortfolioBlog.Api.Tests/Infrastructure/PublicRoleGrantsTests.cs`(신규), `PortfolioBlog.Api.Tests/Infrastructure/HealthCheckCommandTests.cs`(신규), `PortfolioBlog.Api.Tests/Features/StartupValidationTests.cs`, `PortfolioBlog.Api.Tests/Infrastructure/PostgresContainerFixture.cs`, `PortfolioBlog.Api.Tests/Infrastructure/ApiFactory.cs`
- Docs: `README.md`의 설정 키 표

**Interfaces:**
- Consumes: `PublicDbContext.BuildConnectionString(string, int)`, `AuthServiceCollectionExtensions.DataProtectionKeysPathKey`(`"DataProtection:KeysPath"`), `HashPasswordCommand`의 CLI 분기 패턴(Program.cs 맨 위).
- Produces(이후 Task가 기댄다):
  - 설정 키 `ConnectionStrings:Public` — compose가 `ConnectionStrings__Public`으로 준다. Development가 아니면 필수.
  - `DataProtection:KeysPath` — Development가 아니면 절대 경로 필수.
  - CLI: `dotnet PortfolioBlog.Api.dll healthcheck` → 종료 코드 0/1. 환경변수 `Site__PublicOrigin`, `ASPNETCORE_HTTP_PORTS`를 읽는다.
  - `PublicRoleGrants.ReadableTables` = `Posts, Series, Tags, PostTags, Attachments`. 스모크(Task 3)는 공개 롤이 `AdminState`를 **읽지 못한다**는 데 기댄다.

- [ ] **Step 1: 실패하는 테스트를 먼저 쓴다 — 새 테스트 파일 둘**

`PortfolioBlog.Api.Tests/Infrastructure/PublicRoleGrantsTests.cs`:

````csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>공개 조회 전용 롤의 권한: 앱이 시작하며 부여한 것이 "허용 테이블의 SELECT"뿐인지 실제 PostgreSQL에서 확인한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 테스트마다 자기 <see cref="ApiFactory"/>(= 자기 DB)를 만든다. 롤은 컨테이너 전역이지만 권한은 DB별이라 서로 간섭하지 않는다.</description></item>
/// <item><description><b>Memory Allocation:</b> 테스트당 호스트 1개와 Npgsql 연결 1~2개.</description></item>
/// <item><description><b>Blocking:</b> 공유 PostgreSQL 컨테이너(<c>postgres</c> 컬렉션)에 대한 실제 I/O.</description></item>
/// </list>
/// </remarks>
[Collection("postgres")]
public sealed class PublicRoleGrantsTests(PostgresContainerFixture pg)
{
    private static string AsPublicRole(ApiFactory factory) =>
        new NpgsqlConnectionStringBuilder(factory.ConnectionString)
        {
            Username = PostgresContainerFixture.PublicRole,
            Password = PostgresContainerFixture.PublicRoleSecret,
            Pooling = false, // 이 테스트의 연결이 팩토리의 풀 정리(ClearPool) 대상 밖에 남지 않게 한다
        }.ToString();

    private static async Task<string?> SqlStateOfAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        try { await command.ExecuteNonQueryAsync(); return null; }
        catch (PostgresException ex) { return ex.SqlState; }
    }

    /// <summary>DI가 주는 공개 컨텍스트는 실제로 공개 롤로 접속한다. 이 단언이 없으면 <c>ConnectionStrings:Public</c>을 읽는 배선이 끊겨도
    /// 다른 테스트가 전부 통과한다(관리 롤은 모든 것을 읽을 수 있으므로).</summary>
    [Fact]
    public async Task PublicDbContext_ConnectsAsThePublicRole()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        using var _ = factory.CreateClient();
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PublicDbContext>();
        var user = await db.Database.SqlQueryRaw<string>("SELECT current_user::text AS \"Value\"").SingleAsync();
        Assert.Equal(PostgresContainerFixture.PublicRole, user);
    }

    /// <summary>공개 롤은 허용 테이블을 읽을 수 있고, 읽기 전용 설정을 스스로 꺼도 쓰지 못한다.
    /// <c>SET default_transaction_read_only = off</c>를 먼저 실행하는 이유: 그 설정은 세션이 끌 수 있는 심층 방어일 뿐이고(스펙 3.7),
    /// 이 테스트가 증명하려는 것은 그것이 꺼진 뒤에도 남는 경계(권한)다.</summary>
    [Fact]
    public async Task PublicRole_CanReadAllowedTables_ButCannotWrite_EvenAfterDisablingReadOnly()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        using var _ = factory.CreateClient(); // 호스트 기동 → Migrate → 권한 부여
        await using var connection = new NpgsqlConnection(AsPublicRole(factory));
        await connection.OpenAsync();

        foreach (var table in PublicRoleGrants.ReadableTables)
        {
            Assert.Null(await SqlStateOfAsync(connection, $"SELECT count(*) FROM \"{table}\""));
        }
        Assert.Null(await SqlStateOfAsync(connection, "SET default_transaction_read_only = off"));
        const string insufficientPrivilege = "42501";
        Assert.Equal(insufficientPrivilege, await SqlStateOfAsync(connection, "DELETE FROM \"Posts\""));
        Assert.Equal(insufficientPrivilege, await SqlStateOfAsync(connection, "INSERT INTO \"Tags\" (\"Id\", \"Name\", \"NormalizedName\") VALUES (gen_random_uuid(), 'probe', 'probe')"));
        Assert.Equal(insufficientPrivilege, await SqlStateOfAsync(connection, "UPDATE \"Attachments\" SET \"FileName\" = 'x'"));
        Assert.Equal(insufficientPrivilege, await SqlStateOfAsync(connection, "TRUNCATE \"PostTags\""));
    }

    /// <summary>허용 목록 밖의 테이블(세션 폐기 카운터, 마이그레이션 이력)은 읽지도 못한다.
    /// 이 단언이 <c>ALTER DEFAULT PRIVILEGES</c>식 일괄 부여로 되돌아가는 것을 막는다.</summary>
    [Theory]
    [InlineData("AdminState")]
    [InlineData("__EFMigrationsHistory")]
    public async Task PublicRole_CannotReadTablesOutsideTheAllowlist(string table)
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        using var _ = factory.CreateClient();
        await using var connection = new NpgsqlConnection(AsPublicRole(factory));
        await connection.OpenAsync();
        Assert.Equal("42501", await SqlStateOfAsync(connection, $"SELECT * FROM \"{table}\""));
    }

    /// <summary>손으로 넓혀 둔 권한은 다음 시작에서 회수된다(전부 회수 → 허용 목록만 부여).</summary>
    [Fact]
    public async Task Apply_RevokesGrantsOutsideTheAllowlist()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        using var _ = factory.CreateClient();
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using (var owner = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(factory.ConnectionString) { Pooling = false }.ToString()))
        {
            await owner.OpenAsync();
            Assert.Null(await SqlStateOfAsync(owner, $"GRANT SELECT, DELETE ON \"AdminState\" TO {PostgresContainerFixture.PublicRole}"));
        }

        PublicRoleGrants.Apply(db, AsPublicRole(factory));

        await using var connection = new NpgsqlConnection(AsPublicRole(factory));
        await connection.OpenAsync();
        Assert.Equal("42501", await SqlStateOfAsync(connection, "SELECT * FROM \"AdminState\""));
        Assert.Null(await SqlStateOfAsync(connection, "SELECT count(*) FROM \"Posts\""));
    }

    /// <summary>문장은 검증된 롤 이름과 상수 테이블 목록으로만 조립된다. 첫 문장이 회수여야 옛 권한이 남지 않는다.</summary>
    [Fact]
    public void BuildStatements_RevokesFirst_ThenGrantsSelectPerAllowedTable()
    {
        var statements = PublicRoleGrants.BuildStatements("blog_public");
        Assert.Equal("REVOKE ALL ON ALL TABLES IN SCHEMA public FROM blog_public", statements[0]);
        Assert.Equal("GRANT USAGE ON SCHEMA public TO blog_public", statements[1]);
        Assert.Equal(PublicRoleGrants.ReadableTables.Select(t => $"GRANT SELECT ON \"{t}\" TO blog_public"), statements.Skip(2));
        Assert.DoesNotContain(statements, s => s.Contains("AdminState", StringComparison.Ordinal));
    }

    /// <summary>롤 이름은 SQL 매개변수가 될 수 없어 문장에 직접 들어간다 — 따옴표·공백·대문자·세미콜론이 든 이름은 DB에 닿기 전에 거부한다.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("Blog_Public")]
    [InlineData("blog public")]
    [InlineData("blog_public; DROP TABLE \"Posts\"")]
    [InlineData("blog\"public")]
    [InlineData("1blog")]
    public void RoleOf_RejectsNamesThatAreNotPlainLowercaseIdentifiers(string username)
    {
        var connectionString = new NpgsqlConnectionStringBuilder { Host = "db.example", Database = "blog", Username = username }.ToString();
        Assert.Throws<InvalidOperationException>(() => PublicRoleGrants.RoleOf(connectionString));
        Assert.Throws<ArgumentException>(() => PublicRoleGrants.BuildStatements(username));
    }
}
````

`PortfolioBlog.Api.Tests/Infrastructure/HealthCheckCommandTests.cs`:

````csharp
using System.Net;
using PortfolioBlog.Api.Infrastructure.Web;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>컨테이너 헬스체크 CLI의 계약: 어디로, 어떤 Host 헤더로 부르고, 무엇을 종료 코드 0으로 치는가.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 테스트마다 자기 핸들러·버퍼를 만든다. 공유 상태 없음 — 병렬 실행 안전.</description></item>
/// <item><description><b>Memory Allocation:</b> 테스트당 가짜 핸들러 1개와 <see cref="StringWriter"/> 1개.</description></item>
/// <item><description><b>Blocking:</b> 네트워크·DB를 쓰지 않는다(전송 계층을 가짜로 바꾼다).</description></item>
/// </list>
/// </remarks>
public sealed class HealthCheckCommandTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen = request;
            return Task.FromResult(respond(request));
        }
    }

    private static Func<string, string?> Env(params (string Key, string Value)[] pairs) =>
        key => pairs.FirstOrDefault(p => p.Key == key).Value;

    /// <summary>요청은 루프백의 앱 포트로 가고 Host 헤더는 공개 호스트 이름이다 — 호스트 필터가 <c>localhost</c>를 400으로 거부하기 때문이다(스펙 3.10).</summary>
    [Fact]
    public async Task Healthy_ReturnsZero_AndCallsLoopbackWithThePublicHostHeader()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var error = new StringWriter();

        var code = await HealthCheckCommand.RunAsync(Env(("Site__PublicOrigin", "https://blog.example.test")), handler, error);

        Assert.Equal(0, code);
        Assert.Equal(new Uri("http://127.0.0.1:8080/health"), handler.Seen!.RequestUri);
        Assert.Equal("blog.example.test", handler.Seen.Headers.Host);
        Assert.Equal(string.Empty, error.ToString());
    }

    /// <summary><c>ASPNETCORE_HTTP_PORTS</c>가 여러 개면 첫 번째를 쓴다.</summary>
    [Fact]
    public async Task UsesTheFirstConfiguredHttpPort()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await HealthCheckCommand.RunAsync(Env(("Site__PublicOrigin", "https://blog.example.test"), ("ASPNETCORE_HTTP_PORTS", "9090;9091")), handler, new StringWriter());
        Assert.Equal(9090, handler.Seen!.RequestUri!.Port);
    }

    /// <summary>2xx가 아니면 1이다. 400은 Host 헤더가 틀렸을 때, 503은 과부하일 때 실제로 오는 값이다.</summary>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task NonSuccessStatus_ReturnsOne(HttpStatusCode status)
    {
        var error = new StringWriter();
        var code = await HealthCheckCommand.RunAsync(Env(("Site__PublicOrigin", "https://blog.example.test")), new StubHandler(_ => new HttpResponseMessage(status)), error);
        Assert.Equal(1, code);
        Assert.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>연결 실패(앱이 아직 안 떴다)는 예외가 아니라 종료 코드 1이다 — Docker는 종료 코드만 본다.</summary>
    [Fact]
    public async Task ConnectionFailure_ReturnsOne_WithoutThrowing()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        Assert.Equal(1, await HealthCheckCommand.RunAsync(Env(("Site__PublicOrigin", "https://blog.example.test")), handler, new StringWriter()));
    }

    /// <summary>공개 origin 설정이 없거나 절대 URI가 아니면 요청을 보내지 않고 1이다.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("blog.example.test")]
    public async Task MissingOrRelativeOrigin_ReturnsOne_WithoutSending(string? origin)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var env = origin is null ? Env() : Env(("Site__PublicOrigin", origin));
        Assert.Equal(1, await HealthCheckCommand.RunAsync(env, handler, new StringWriter()));
        Assert.Null(handler.Seen);
    }
}
````

- [ ] **Step 2: 테스트 기반을 바꾼다 — 픽스처가 공개 롤을 만들고, 모든 팩토리가 그 롤로 공개 조회를 한다**

`PostgresContainerFixture.cs`:

````diff
--- a/PortfolioBlog.Api.Tests/Infrastructure/PostgresContainerFixture.cs
+++ b/PortfolioBlog.Api.Tests/Infrastructure/PostgresContainerFixture.cs
@@ -36,7 +36,23 @@
     /// <item><description><b>Blocking:</b> 비동기 Non-blocking 대기. 최초 이미지 pull 시 수십 초가 걸릴 수 있다.</description></item>
     /// </list>
     /// </remarks>
-    public Task InitializeAsync() => _container.StartAsync();
+    /// <summary>공개 조회 전용 롤. 운영의 <c>blog_public</c>에 해당한다 — 권한은 앱이 시작할 때 부여한다(PublicRoleGrants).</summary>
+    public const string PublicRole = "blog_public_test";
+
+    /// <summary><see cref="PublicRole"/>의 비밀번호. 테스트 컨테이너 안에서만 쓰이는 dummy 값이다.</summary>
+    public const string PublicRoleSecret = "dummy-public-role";
+
+    /// <summary>컨테이너를 띄우고 공개 조회 전용 롤을 만든다.</summary>
+    /// <returns>컨테이너가 연결을 받을 수 있고 롤이 생긴 뒤 완료되는 작업.</returns>
+    public async Task InitializeAsync()
+    {
+        await _container.StartAsync();
+        // 롤은 클러스터 전역이라 컨테이너당 한 번만 만든다. 팩토리별 DB에 대한 권한은 각 앱 인스턴스가 시작하며 부여한다.
+        await using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
+        await connection.OpenAsync();
+        await using var command = new Npgsql.NpgsqlCommand($"CREATE ROLE {PublicRole} LOGIN PASSWORD '{PublicRoleSecret}' NOSUPERUSER NOCREATEDB NOCREATEROLE", connection);
+        await command.ExecuteNonQueryAsync();
+    }
 
     /// <summary>컨테이너를 정지하고 제거한다.</summary>
     /// <returns>정리가 끝나면 완료되는 작업.</returns>
````

`ApiFactory.cs`(필드 1개, 생성자 1줄, 설정 3줄, 공유 키 폴더 속성, `Dispose`의 풀 정리 대상):

````diff
--- a/PortfolioBlog.Api.Tests/Infrastructure/ApiFactory.cs
+++ b/PortfolioBlog.Api.Tests/Infrastructure/ApiFactory.cs
@@ -43,6 +43,7 @@
     private static readonly string PasswordHash = AdminCredential.Hash(Password);
 
     private readonly string _connectionString;
+    private readonly string _publicConnectionString;
     private readonly IReadOnlyDictionary<string, string?> _settings;
     private readonly string? _attachmentsRootPathOverride;
 
@@ -54,6 +55,10 @@
 
     /// <summary>이 팩토리 인스턴스 전용 첨부 저장 루트(임시 디렉터리 밑, 인스턴스마다 고유). 팩토리가 해제되면 재귀적으로 지워진다.</summary>
     public string AttachmentsRoot { get; } = Path.Combine(Path.GetTempPath(), "portfolioblog-tests", Guid.NewGuid().ToString("N"));
+
+    /// <summary>모든 팩토리가 함께 쓰는 Data Protection 키 폴더(절대 경로). 팩토리마다 따로 두면 한 팩토리가 발급한 쿠키를 다른 팩토리가
+    /// 풀지 못해, 두 호스트가 같은 키 링을 공유한다고 전제하는 세션 테스트(해시 교체 대조군)가 깨진다. 지우지 않는다(키 파일 몇 KB).</summary>
+    public static string DataProtectionKeysRoot { get; } = Path.Combine(Path.GetTempPath(), "portfolioblog-tests", "dpkeys-shared");
 
     /// <summary>xUnit이 클래스 픽스처로 주입하는 기본 생성자. 설정 오버라이드가 없다.</summary>
     /// <param name="pg">컬렉션이 공유하는 PostgreSQL 컨테이너 fixture.</param>
@@ -79,6 +84,7 @@
             Database = "blog_test_" + Guid.NewGuid().ToString("N"),
         };
         _connectionString = csb.ToString();
+        _publicConnectionString = new NpgsqlConnectionStringBuilder(_connectionString) { Username = PostgresContainerFixture.PublicRole, Password = PostgresContainerFixture.PublicRoleSecret }.ToString();
         _settings = settings;
         _attachmentsRootPathOverride = attachmentsRootTrailingSeparator is { } separator ? AttachmentsRoot + separator : null;
     }
@@ -86,6 +92,9 @@
     protected override void ConfigureWebHost(IWebHostBuilder builder)
     {
         builder.UseSetting("ConnectionStrings:Default", _connectionString);
+        builder.UseSetting("ConnectionStrings:Public", _publicConnectionString);
+        // Development가 아닌 환경의 시작 검증이 요구한다. 첨부 루트 안에 두지 않는다(청소 잡 테스트가 그 폴더를 훑는다). 전 팩토리 공유.
+        builder.UseSetting("DataProtection:KeysPath", DataProtectionKeysRoot);
         // 개별 테스트의 _settings가 아래에서 덮어쓸 수 있도록 기본값을 루프 앞에 먼저 넣는다.
         builder.UseSetting("Site:PublicOrigin", PublicOrigin);
         builder.UseSetting("Site:AdminOrigin", AdminOrigin);
@@ -248,7 +257,7 @@
                 && int.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                 ? parsed
                 : new PublicOptions().StatementTimeoutMs;
-            foreach (var cs in new[] { _connectionString, PublicDbContext.BuildConnectionString(_connectionString, timeout) })
+            foreach (var cs in new[] { _connectionString, PublicDbContext.BuildConnectionString(_publicConnectionString, timeout) })
             {
                 using var connection = new NpgsqlConnection(cs);
                 NpgsqlConnection.ClearPool(connection);
````

`StartupValidationTests.cs` 끝에 테스트 5개를 더한다:

````diff
--- a/PortfolioBlog.Api.Tests/Features/StartupValidationTests.cs
+++ b/PortfolioBlog.Api.Tests/Features/StartupValidationTests.cs
@@ -213,5 +213,34 @@
         var shortCommandTimeout = new NpgsqlConnectionStringBuilder(pg.ConnectionString) { CommandTimeout = 1 }.ConnectionString;
         AssertStartupFails(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = shortCommandTimeout }, "ConnectionStrings:Default");
     }
+
+    /// <summary><c>Development</c>가 아닌 환경에서 공개 조회 전용 연결(<c>ConnectionStrings:Public</c>)이 없으면 시작이 실패한다.
+    /// 없으면 공개 페이지가 테이블 소유자 롤로 돌고, 남는 방어는 세션이 스스로 끌 수 있는 <c>default_transaction_read_only</c>뿐이다.</summary>
+    [Fact]
+    public void Production_MissingPublicConnectionString_Fails() =>
+        AssertStartupFails(Production(s => s["ConnectionStrings:Public"] = ""), "ConnectionStrings:Public");
+
+    /// <summary><c>Development</c>가 아닌 환경에서 Data Protection 키 경로가 없거나 상대 경로면 시작이 실패한다.
+    /// 상대 경로("keys")를 따로 보는 이유: 컨테이너의 작업 디렉터리(읽기 전용 루트 FS) 아래를 가리키게 된다.</summary>
+    [Theory]
+    [InlineData("")]
+    [InlineData("keys")]
+    public void Production_MissingOrRelativeDataProtectionKeysPath_Fails(string path) =>
+        AssertStartupFails(Production(s => s["DataProtection:KeysPath"] = path), "DataProtection:KeysPath");
+
+    /// <summary>공개 연결 문자열에 <c>Options</c>가 있으면 환경과 무관하게 시작이 실패하고, 메시지는 그 키를 가리킨다(값은 넣지 않는다).</summary>
+    [Fact]
+    public void PublicConnectionString_WithOptions_Fails() =>
+        AssertStartupFails(new Dictionary<string, string?> { ["ConnectionStrings:Public"] = "Host=db.example;Database=blog;Username=blog_public_test;Options=-c work_mem=1MB" }, "ConnectionStrings:Public 에 Options");
+
+    /// <summary>공개 연결의 사용자가 관리 연결과 같으면 시작이 실패한다. "postgres"인 이유: 테스트 컨테이너의 관리 연결 사용자가 그 이름이다.</summary>
+    [Fact]
+    public void PublicConnectionString_SameUserAsDefault_Fails() =>
+        AssertStartupFails(new Dictionary<string, string?> { ["ConnectionStrings:Public"] = "Host=db.example;Database=blog;Username=postgres" }, "별도의 읽기 전용 롤");
+
+    /// <summary>공개 연결의 사용자 이름이 평범한 소문자 식별자가 아니면 DB에 닿기 전에 시작이 실패한다(GRANT 문장에 직접 들어가는 값이다).</summary>
+    [Fact]
+    public void PublicConnectionString_WithUnsafeRoleName_Fails() =>
+        AssertStartupFails(new Dictionary<string, string?> { ["ConnectionStrings:Public"] = "Host=db.example;Database=blog;Username='blog public'" }, "ConnectionStrings:Public 의 Username");
 }
 
````

- [ ] **Step 3: 컴파일이 실패하는 것을 확인한다**

Run: `dotnet build PortfolioBlog.slnx -c Release`
Expected: `PublicRoleGrants`·`HealthCheckCommand`가 없다는 CS0103/CS0246 오류.

- [ ] **Step 4: 구현 — 새 파일 둘**

`PortfolioBlog.Api/Infrastructure/Data/PublicRoleGrants.cs`:

````csharp
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>공개 조회 전용 DB 롤(<c>ConnectionStrings:Public</c>의 사용자)에게 공개 페이지가 읽는 테이블의 <c>SELECT</c>만 부여한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 공유 가변 상태가 없다. <see cref="Apply"/>는 시작 스레드에서 마이그레이션 직후 1회만 호출된다(단일 인스턴스 배포 — 스펙 3.10).</description></item>
/// <item><description><b>Memory Allocation:</b> 시작 시 SQL 문자열 몇 개(테이블 수 + 3)만 할당한다.</description></item>
/// <item><description><b>Blocking:</b> <see cref="Apply"/>는 동기 DB 왕복(트랜잭션 1개)이다. 요청 처리 경로에서 부르지 않는다.</description></item>
/// </list>
/// 테이블 목록을 EF 모델에서 뽑지 않는 이유: <see cref="PublicDbContext"/>는 <see cref="AppDbContext"/>를 상속해 모델에
/// <c>AdminState</c>(세션 폐기 카운터)와 마이그레이션 이력까지 들어 있다. 공개 롤이 읽어도 되는 것은 아래 목록뿐이다.
/// </remarks>
public static partial class PublicRoleGrants
{
    /// <summary>공개 롤이 읽을 수 있는 테이블. 공개 페이지·피드·첨부 GET이 실제로 조회하는 것만 둔다.</summary>
    public static readonly IReadOnlyList<string> ReadableTables = ["Posts", "Series", "Tags", "PostTags", "Attachments"];

    /// <summary>따옴표 없이 쓸 수 있는 소문자 식별자만 롤 이름으로 받는다. 롤 이름은 SQL 매개변수가 될 수 없어 문장에 직접 들어가기 때문이다.</summary>
    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex RoleNamePattern();

    /// <summary>공개 연결 문자열에서 롤 이름을 꺼내 검증한다.</summary>
    /// <param name="publicConnectionString"><c>ConnectionStrings:Public</c> 값.</param>
    /// <returns>검증된 롤 이름.</returns>
    /// <exception cref="InvalidOperationException">사용자 이름이 없거나 <c>^[a-z_][a-z0-9_]{0,62}$</c>에 맞지 않을 때. 메시지에 연결 문자열 값은 넣지 않는다.</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 순수 함수다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 연결 문자열 파서 1개와 결과 문자열.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음 — <c>StartupValidation</c>에서도 부를 수 있다.</description></item>
    /// </list>
    /// </remarks>
    public static string RoleOf(string publicConnectionString)
    {
        var role = new NpgsqlConnectionStringBuilder(publicConnectionString).Username;
        return !string.IsNullOrEmpty(role) && RoleNamePattern().IsMatch(role)
            ? role
            : throw new InvalidOperationException("ConnectionStrings:Public 의 Username 은 소문자·숫자·밑줄로 된 롤 이름이어야 합니다.");
    }

    /// <summary>롤에 적용할 문장을 순서대로 만든다: 전부 회수 → 스키마 사용 → 허용 테이블 <c>SELECT</c>.</summary>
    /// <param name="role"><see cref="RoleOf"/>가 검증한 롤 이름.</param>
    /// <returns>한 트랜잭션에서 순서대로 실행할 SQL 문장들.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 순수 함수다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 문장 수만큼의 문자열과 배열 1개.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// 먼저 전부 회수하는 이유: 목록에서 빠진 테이블의 옛 권한이나 손으로 준 권한이 남지 않게 한다.
    /// </remarks>
    public static IReadOnlyList<string> BuildStatements(string role)
    {
        if (!RoleNamePattern().IsMatch(role)) throw new ArgumentException("검증되지 않은 롤 이름", nameof(role));
        var statements = new List<string>
        {
            $"REVOKE ALL ON ALL TABLES IN SCHEMA public FROM {role}",
            $"GRANT USAGE ON SCHEMA public TO {role}",
        };
        foreach (var table in ReadableTables)
        {
            statements.Add($"GRANT SELECT ON \"{table.Replace("\"", "\"\"")}\" TO {role}");
        }
        return statements;
    }

    /// <summary>관리 연결(테이블 소유자)로 권한을 다시 맞춘다. 마이그레이션 직후에 부른다 — 새 테이블이 생긴 뒤여야 한다.</summary>
    /// <param name="db">테이블 소유자 롤로 접속하는 관리 컨텍스트.</param>
    /// <param name="publicConnectionString"><c>ConnectionStrings:Public</c> 값.</param>
    /// <exception cref="InvalidOperationException">공개 롤이 관리 롤과 같을 때(권한을 회수하면 앱이 자기 테이블을 못 읽는다), 또는 롤 이름이 잘못됐을 때.</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Not Thread-safe(<paramref name="db"/>가 단일 스레드 전용). 시작 시 1회 호출.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="BuildStatements"/>의 문자열들.</description></item>
    /// <item><description><b>Blocking:</b> 동기 블로킹. 트랜잭션 하나에서 문장 몇 개를 실행한다 — 중간에 실패하면 권한은 이전 상태로 남는다.</description></item>
    /// </list>
    /// </remarks>
    public static void Apply(AppDbContext db, string publicConnectionString)
    {
        var role = RoleOf(publicConnectionString);
        var owner = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()).Username;
        if (string.Equals(role, owner, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ConnectionStrings:Public 의 Username 이 ConnectionStrings:Default 와 같습니다 — 공개 조회는 별도의 읽기 전용 롤이어야 합니다.");
        }
        using var transaction = db.Database.BeginTransaction();
        foreach (var statement in BuildStatements(role))
        {
            // 문장은 검증된 식별자와 상수 목록으로만 조립된다(사용자 입력 없음). GRANT의 대상 식별자는 SQL 매개변수가 될 수 없다.
#pragma warning disable EF1002
            db.Database.ExecuteSqlRaw(statement);
#pragma warning restore EF1002
        }
        transaction.Commit();
    }
}
````

`PortfolioBlog.Api/Infrastructure/Web/HealthCheckCommand.cs`:

````csharp
namespace PortfolioBlog.Api.Infrastructure.Web;

/// <summary>컨테이너 헬스체크용 CLI 경로(<c>dotnet PortfolioBlog.Api.dll healthcheck</c>). 같은 컨테이너의 <c>/health</c>를 한 번 부르고 종료 코드로 답한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 공유 상태가 없다. 헬스체크마다 새 프로세스로 1회 실행된다.</description></item>
/// <item><description><b>Memory Allocation:</b> <see cref="HttpClient"/> 1개와 요청·응답 객체. 웹 호스트는 만들지 않는다.</description></item>
/// <item><description><b>Blocking:</b> 비동기. 최대 <see cref="TimeoutSeconds"/>초 뒤에는 반드시 끝난다.</description></item>
/// </list>
/// 이 명령이 필요한 이유: 운영 이미지(chiseled)에는 셸도 curl도 없고, 호스트 필터가 설정된 두 호스트만 받으므로
/// <c>Host: localhost</c>로는 본문 없는 400이 온다(스펙 3.10). 그래서 앱 자신이 공개 호스트 이름을 Host 헤더에 실어 부른다.
/// </remarks>
public static class HealthCheckCommand
{
    /// <summary>CLI 인수 이름.</summary>
    public const string Name = "healthcheck";

    /// <summary>요청 하나의 시간 상한(초). compose의 healthcheck <c>timeout</c>(5초)보다 짧아야 Docker가 아니라 이 명령이 먼저 실패를 보고한다.</summary>
    public const int TimeoutSeconds = 3;

    /// <summary><c>/health</c>를 부르고 2xx면 0, 그 밖의 모든 경우(설정 오류·연결 실패·시간 초과·비 2xx)는 1을 돌려준다.</summary>
    /// <param name="env">환경변수 조회 함수. <c>Site__PublicOrigin</c>(필수)과 <c>ASPNETCORE_HTTP_PORTS</c>(없으면 8080)를 읽는다.</param>
    /// <param name="handler">HTTP 전송 계층. 호출자가 소유권을 넘긴다 — 이 메서드가 해제한다.</param>
    /// <param name="error">실패 이유를 한 줄로 쓸 곳(<c>docker inspect</c>의 헬스 로그에 남는다). 비밀값은 쓰지 않는다.</param>
    /// <returns>프로세스 종료 코드: 0(정상) 또는 1.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 호출마다 독립적이다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 요청·응답 객체와 URI 문자열. <paramref name="handler"/>는 반환 전에 해제된다.</description></item>
    /// <item><description><b>Blocking:</b> 비동기(Non-blocking). 예외를 밖으로 던지지 않는다 — 헬스체크의 실패는 종료 코드로만 말한다.</description></item>
    /// </list>
    /// </remarks>
    public static async Task<int> RunAsync(Func<string, string?> env, HttpMessageHandler handler, TextWriter error)
    {
        using var http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
        try
        {
            if (!Uri.TryCreate(env("Site__PublicOrigin"), UriKind.Absolute, out var origin))
            {
                error.WriteLine("healthcheck: Site__PublicOrigin 이 없거나 절대 URI가 아닙니다.");
                return 1;
            }
            // ASPNETCORE_HTTP_PORTS는 "8080;8081"처럼 여러 개일 수 있다. 첫 번째만 쓴다.
            var port = (env("ASPNETCORE_HTTP_PORTS") ?? "8080").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "8080";
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/health");
            request.Headers.Host = origin.Host;
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode) return 0;
            error.WriteLine($"healthcheck: /health 가 {(int)response.StatusCode} 을(를) 돌려줬습니다.");
            return 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            error.WriteLine($"healthcheck: {ex.GetType().Name}");
            return 1;
        }
    }
}
````

- [ ] **Step 5: 구현 — 기존 파일 셋**

`DataServiceCollectionExtensions.cs`(`AddBlogData`의 `<remarks>`에도 "공개 컨텍스트는 `ConnectionStrings:Public`이 있으면 그 롤로 접속한다"를 한 줄 더한다):

````diff
--- a/PortfolioBlog.Api/Infrastructure/Data/DataServiceCollectionExtensions.cs
+++ b/PortfolioBlog.Api/Infrastructure/Data/DataServiceCollectionExtensions.cs
@@ -30,7 +30,7 @@
     {
         services.AddDbContext<AppDbContext>((sp, o) => o.UseNpgsql(RequireConnectionString(sp)));
         services.AddDbContext<PublicDbContext>((sp, o) => o
-            .UseNpgsql(PublicDbContext.BuildConnectionString(RequireConnectionString(sp), sp.GetRequiredService<IOptions<PublicOptions>>().Value.StatementTimeoutMs))
+            .UseNpgsql(PublicDbContext.BuildConnectionString(PublicOrDefaultConnectionString(sp), sp.GetRequiredService<IOptions<PublicOptions>>().Value.StatementTimeoutMs))
             // 공개 경로는 추적할 이유가 없다: 변경 추적기 할당을 없애고, 실수로 엔티티를 고쳐도 저장 대상이 되지 않는다.
             .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));
         return services;
@@ -41,6 +41,17 @@
     /// <returns><c>ConnectionStrings:Default</c> 값.</returns>
     /// <exception cref="InvalidOperationException"><c>ConnectionStrings:Default</c>가 없거나 공백뿐이다.</exception>
     // appsettings.json의 기본값이 빈 문자열이라 ??로는 걸러지지 않는다(Plan 1 정오표). 메시지는 ConnectionStringGuardTests가 고정한다.
+    /// <summary>공개 조회 컨텍스트가 쓸 연결 문자열. <c>ConnectionStrings:Public</c>(읽기 전용 롤)이 있으면 그것을, 없으면 관리 연결을 쓴다.</summary>
+    /// <param name="sp">연결 문자열을 읽을 서비스 프로바이더.</param>
+    /// <returns>공개 연결의 바탕이 되는 연결 문자열(시작 옵션은 <see cref="PublicDbContext.BuildConnectionString"/>이 붙인다).</returns>
+    /// <exception cref="InvalidOperationException">둘 다 비어 있을 때(<see cref="RequireConnectionString"/>).</exception>
+    // 없을 때 관리 연결로 물러나는 것은 Development·테스트 편의다. Development가 아닌 환경에서는 StartupValidation이 Public을 필수로 요구한다.
+    private static string PublicOrDefaultConnectionString(IServiceProvider sp)
+    {
+        var publicConnectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString("Public");
+        return string.IsNullOrWhiteSpace(publicConnectionString) ? RequireConnectionString(sp) : publicConnectionString;
+    }
+
     private static string RequireConnectionString(IServiceProvider sp)
     {
         var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString("Default");
````

`StartupValidation.cs`(연결 문자열 검사를 `CheckConnectionString`으로 뽑아 두 키에 적용, 공개 롤 이름·동일 사용자 검사, 비개발 환경 필수 2건. `Validate`의 `<summary>`·`<exception>`에도 새 조건을 반영한다):

````diff
--- a/PortfolioBlog.Api/Infrastructure/Access/StartupValidation.cs
+++ b/PortfolioBlog.Api/Infrastructure/Access/StartupValidation.cs
@@ -78,21 +78,22 @@
         }
         // 연결 문자열이 비어 있으면 기존 가드(DataServiceCollectionExtensions.RequireConnectionString, 컨텍스트가 처음 해석될 때)가
         // 그대로 처리한다 — 여기서는 값이 있을 때만, 그 값이 공개 연결 조립과 실제로 합쳐지는지를 시작 시점에 미리 확인한다.
-        var connectionString = services.GetRequiredService<IConfiguration>().GetConnectionString("Default");
+        var configuration = services.GetRequiredService<IConfiguration>();
+        var connectionString = configuration.GetConnectionString("Default");
+        var publicConnectionString = configuration.GetConnectionString("Public");
         if (!string.IsNullOrWhiteSpace(connectionString))
         {
-            // BuildConnectionString은 순수 파싱·문자열 조립이라 I/O가 없다 — 이 클래스의 "I/O 없음" 계약을 지킨다.
-            // Options가 이미 있으면 여기서 던지므로, 그 조용한 덮어쓰기가 첫 공개 요청이 아니라 시작 시점에 드러난다.
-            PublicDbContext.BuildConnectionString(connectionString, pub.StatementTimeoutMs);
-
-            // CommandTimeout(초, 0=무한)이 statement_timeout(밀리초)보다 먼저 끊기면 클라이언트가 DB보다 먼저 취소해버려서
-            // OverloadExceptionHandler가 기대하는 57014(DB 시간제한) 대신 클라이언트 취소 예외가 난다 — 503 매핑 설계가 깨진다.
-            var commandTimeoutSeconds = new NpgsqlConnectionStringBuilder(connectionString).CommandTimeout;
-            if (commandTimeoutSeconds != 0 && commandTimeoutSeconds * 1000L <= pub.StatementTimeoutMs)
+            CheckConnectionString("ConnectionStrings:Default", connectionString, pub.StatementTimeoutMs);
+        }
+        if (!string.IsNullOrWhiteSpace(publicConnectionString))
+        {
+            CheckConnectionString("ConnectionStrings:Public", publicConnectionString, pub.StatementTimeoutMs);
+            // 롤 이름은 GRANT 문장에 직접 들어간다(PublicRoleGrants) — 형식을 DB 접속 전에 확인한다.
+            var publicRole = PublicRoleGrants.RoleOf(publicConnectionString);
+            if (!string.IsNullOrWhiteSpace(connectionString)
+                && string.Equals(publicRole, new NpgsqlConnectionStringBuilder(connectionString).Username, StringComparison.Ordinal))
             {
-                throw new InvalidOperationException(
-                    "ConnectionStrings:Default 의 Command Timeout(초)이 Public:StatementTimeoutMs(밀리초)보다 커야 합니다 — " +
-                    "그렇지 않으면 클라이언트 취소가 DB의 statement_timeout보다 먼저 발생합니다.");
+                throw new InvalidOperationException("ConnectionStrings:Public 의 Username 이 ConnectionStrings:Default 와 같습니다 — 공개 조회는 별도의 읽기 전용 롤이어야 합니다.");
             }
         }
         var rendering = services.GetRequiredService<IOptions<RenderingOptions>>().Value;
@@ -123,6 +124,42 @@
             // 상대 경로는 콘텐츠 루트(배포 시 작업 디렉터리)에 따라 달라져 운영에서는 의도치 않은 위치를 가리키기 쉽다.
             // appsettings.Development.json은 로컬 상대 경로(.data/attachments)를 그대로 쓰므로 Development만 예외로 허용한다.
             Require(Path.IsPathFullyQualified(attachments.RootPath), "Attachments:RootPath");
+            // 공개 조회가 관리 롤(테이블 소유자)로 돌면 default_transaction_read_only만 남는다 — 그것은 세션이 스스로 끌 수 있다(스펙 3.7).
+            Require(!string.IsNullOrWhiteSpace(publicConnectionString), "ConnectionStrings:Public");
+            // 키가 컨테이너의 임시 위치에 생기면 재시작할 때마다 모든 세션이 조용히 끊긴다(읽기 전용 루트 FS에서는 메모리에만 남는다).
+            Require(Path.IsPathFullyQualified(configuration[AuthServiceCollectionExtensions.DataProtectionKeysPathKey] ?? string.Empty), AuthServiceCollectionExtensions.DataProtectionKeysPathKey);
+        }
+    }
+
+    /// <summary>연결 문자열 하나가 공개 연결 조립과 합쳐지는지, 클라이언트 시간 제한이 DB 시간 제한보다 긴지 확인한다.</summary>
+    /// <param name="key">예외 메시지에 넣을 설정 키. 값(비밀번호 포함 가능)은 메시지에 넣지 않는다.</param>
+    /// <param name="connectionString">검사할 연결 문자열.</param>
+    /// <param name="statementTimeoutMs"><c>Public:StatementTimeoutMs</c>.</param>
+    /// <exception cref="InvalidOperationException"><c>Options</c>가 들어 있거나 <c>Command Timeout</c>(초)×1000이 <paramref name="statementTimeoutMs"/> 이하일 때.</exception>
+    /// <remarks>
+    /// <b>[성능 및 동시성 제약 조건]</b>
+    /// <list type="bullet">
+    /// <item><description><b>Thread Safety:</b> 정적 메서드로 공유 상태가 없다.</description></item>
+    /// <item><description><b>Memory Allocation:</b> 연결 문자열 파서 1개.</description></item>
+    /// <item><description><b>Blocking:</b> 동기 실행. 순수 파싱이라 I/O가 없다 — 이 클래스의 "I/O 없음" 계약을 지킨다.</description></item>
+    /// </list>
+    /// </remarks>
+    private static void CheckConnectionString(string key, string connectionString, int statementTimeoutMs)
+    {
+        var parsed = new NpgsqlConnectionStringBuilder(connectionString);
+        // Options가 이미 있으면 공개 연결의 시작 옵션(statement_timeout·default_transaction_read_only)과 합칠 수 없다.
+        // 첫 공개 요청이 아니라 시작 시점에 드러낸다.
+        if (!string.IsNullOrEmpty(parsed.Options))
+        {
+            throw new InvalidOperationException($"{key} 에 Options 를 넣을 수 없습니다 — 공개 조회 연결의 시작 옵션과 합칠 수 없습니다.");
+        }
+        // CommandTimeout(초, 0=무한)이 statement_timeout(밀리초)보다 먼저 끊기면 클라이언트가 DB보다 먼저 취소해버려서
+        // OverloadExceptionHandler가 기대하는 57014(DB 시간제한) 대신 클라이언트 취소 예외가 난다 — 503 매핑 설계가 깨진다.
+        if (parsed.CommandTimeout != 0 && parsed.CommandTimeout * 1000L <= statementTimeoutMs)
+        {
+            throw new InvalidOperationException(
+                $"{key} 의 Command Timeout(초)이 Public:StatementTimeoutMs(밀리초)보다 커야 합니다 — " +
+                "그렇지 않으면 클라이언트 취소가 DB의 statement_timeout보다 먼저 발생합니다.");
         }
     }
 
````

`Program.cs`(CLI 분기 하나, 마이그레이션 뒤 권한 부여. 최상위 문에 `await`가 생겨 진입점이 비동기가 된다 — `WebApplicationFactory`는 영향이 없다, 스파이크에서 618개 통과로 확인):

````diff
--- a/PortfolioBlog.Api/Program.cs
+++ b/PortfolioBlog.Api/Program.cs
@@ -16,6 +16,13 @@
 if (args is [HashPasswordCommand.Name])
 {
     return HashPasswordCommand.Run(Console.In, Console.Out, Console.Error, interactive: !Console.IsInputRedirected);
+}
+
+// 컨테이너 헬스체크 경로: 웹 호스트를 만들지 않고 같은 컨테이너의 /health를 한 번 부른 뒤 종료 코드로 답한다.
+if (args is [HealthCheckCommand.Name])
+{
+    // SocketsHttpHandler: 프로세스가 요청 하나로 끝나므로 연결 풀 수명을 관리할 필요가 없다. RunAsync가 해제한다.
+    return await HealthCheckCommand.RunAsync(Environment.GetEnvironmentVariable, new SocketsHttpHandler(), Console.Error);
 }
 
 var builder = WebApplication.CreateBuilder(args);
@@ -74,7 +81,10 @@
 // 단일 인스턴스 배포이므로 시작 시 마이그레이션을 적용한다(스펙 3.10).
 using (var scope = app.Services.CreateScope())
 {
-    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
+    var adminDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
+    adminDb.Database.Migrate();
+    var publicConnection = app.Configuration.GetConnectionString("Public");
+    if (!string.IsNullOrWhiteSpace(publicConnection)) PublicRoleGrants.Apply(adminDb, publicConnection);
 }
 
 // 워밍업: 첫 렌더에는 ColorCode 등의 정적 초기화(실측 약 185ms — 2A 단계 실측, plan/resume_guide_0921.md)가 붙는다. 첫 방문자가 아니라 시작 시점에 낸다.
````

- [ ] **Step 6: 전체 테스트**

Run: `dotnet build PortfolioBlog.slnx -c Release && dotnet test PortfolioBlog.slnx -c Release --no-build`
Expected: 경고 0·오류 0, **618개 통과**(591 + 27). 15초 만에 1건이 연결 타임아웃으로 실패하면 재실행한다(재개 가이드 5절의 로컬 간헐 현상).

- [ ] **Step 7: 사보타주로 새 테스트가 실제로 무는지 확인한다(각각 되돌린다)**

| 바꿔 볼 것 | 실패해야 하는 테스트 |
|---|---|
| `ReadableTables`에 `"AdminState"` 추가 | `PublicRole_CannotReadTablesOutsideTheAllowlist("AdminState")`, `BuildStatements_…` |
| `BuildStatements`의 `REVOKE` 줄 삭제 | `Apply_RevokesGrantsOutsideTheAllowlist`, `BuildStatements_…` |
| `PublicOrDefaultConnectionString`이 항상 관리 연결을 돌려주게 | `PublicDbContext_ConnectsAsThePublicRole` **하나만**(스파이크 실측: 617 통과·1 실패 — 관리 롤은 모든 것을 읽을 수 있어 다른 테스트는 이 배선이 끊겨도 통과한다) |
| `HealthCheckCommand`에서 `request.Headers.Host` 줄 삭제 | `Healthy_ReturnsZero_AndCallsLoopbackWithThePublicHostHeader` |
| `StartupValidation`의 `Require(… "ConnectionStrings:Public")` 삭제 | `Production_MissingPublicConnectionString_Fails` |

- [ ] **Step 8: README 설정 키 표**

`README.md`의 `ConnectionStrings:Default` 행 아래에 두 행을 더한다:

```markdown
| `ConnectionStrings:Public` | (빈 값 — Development가 아니면 시작 실패) | 공개 페이지 조회 전용 연결. **테이블 소유자가 아닌 별도 롤**이어야 한다(`Username`은 소문자·숫자·밑줄, 관리 연결과 같으면 시작 실패). 앱이 시작할 때마다 이 롤의 권한을 `Posts`·`Series`·`Tags`·`PostTags`·`Attachments`의 `SELECT`로 다시 맞춘다. `Options`·`Command Timeout` 규칙은 `Default`와 같다. 비어 있으면(개발) 관리 연결로 조회한다 |
| `DataProtection:KeysPath` | (빈 값 — Development가 아니면 시작 실패) | 세션 쿠키 암호화 키를 둘 **절대 경로**. 컨테이너에서는 `dpkeys` 볼륨(`/data/dpkeys`). 비어 있으면(개발) 프레임워크 기본 위치 |
```

- [ ] **Step 9: 커밋**

```bash
git add PortfolioBlog.Api PortfolioBlog.Api.Tests README.md
git commit -F <메시지 파일>   # 제목 예: "추가: 공개 조회를 쓰기 권한 없는 DB 롤로 분리하고 컨테이너 헬스체크 경로를 둠"
```

---

### Task 2: Caddyfile과 이미지 둘

**Files:**
- Create: `.dockerignore`, `.gitattributes`, `deploy/Caddyfile`, `PortfolioBlog.Api/Dockerfile`, `PortfolioBlog.Web/Dockerfile`, `PortfolioBlog.Web/src/test/caddyfile.test.ts`

**Interfaces:**
- Consumes: Task 1의 CLI `healthcheck`·`hash-password`, `PortfolioBlog.Web/admin-headers.ts`의 `ADMIN_SECURITY_HEADERS`.
- Produces: 저장소 루트를 컨텍스트로 하는 두 이미지. api: 사용자 1654, 포트 8080, `/data/attachments`·`/data/dpkeys`(1654, 0700), 환경 기본값 `ASPNETCORE_ENVIRONMENT=Production`·`Attachments__RootPath`·`DataProtection__KeysPath`. caddy: `/etc/caddy/Caddyfile`, SPA는 `/srv`, 환경변수 `DOMAIN`·`ADMIN_DOMAIN`·`ADMIN_ALLOWED_CIDRS`·`ACME_EMAIL`, 백엔드 주소 `api:8080`.

- [ ] **Step 1: 줄 끝 규칙과 빌드 컨텍스트**

`.gitattributes`(새 파일, LF):

```
# 컨테이너 안에서 실행·해석되는 파일은 체크아웃 설정(autocrlf)과 무관하게 LF여야 한다.
# CRLF가 섞이면 바인드 마운트된 셸 스크립트의 셔뱅이 깨지고 psql heredoc이 틀어진다.
*.sh            text eol=lf
deploy/**       text eol=lf
**/Dockerfile   text eol=lf
.dockerignore   text eol=lf
```

`.dockerignore`(새 파일):

````
# 허용 목록 방식: 전부 제외하고 이미지 빌드에 필요한 것만 되살린다.
**
!Directory.Packages.props
!PortfolioBlog.Api/**
!PortfolioBlog.Web/**
!deploy/Caddyfile
PortfolioBlog.Api/bin/
PortfolioBlog.Api/obj/
PortfolioBlog.Api/.data/
PortfolioBlog.Api/appsettings.Development.json
PortfolioBlog.Api/Properties/
PortfolioBlog.Api/*.http
PortfolioBlog.Web/node_modules/
PortfolioBlog.Web/dist/
PortfolioBlog.Web/.certs/
PortfolioBlog.Web/.e2e/
PortfolioBlog.Web/test-results/
PortfolioBlog.Web/playwright-report/
**/.env
**/.env.*
````

- [ ] **Step 2: 실패하는 테스트 — Caddyfile의 헤더가 정본과 같은가**

`PortfolioBlog.Web/src/test/caddyfile.test.ts`:

````ts
import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'
import { ADMIN_SECURITY_HEADERS } from '../../admin-headers.ts'

// 운영에서 관리 SPA의 보안 헤더를 붙이는 것은 Caddy다(deploy/Caddyfile). 정본은 admin-headers.ts이고 Caddyfile은 그 사본이다.
// 이 테스트는 사본이 정본과 글자 그대로 같은지, 그리고 정본에 의도적으로 없는 HSTS가 Caddyfile에는 있는지 본다.
// 실제 응답의 헤더는 deploy/smoke(컨테이너 스택)가 본다 — 여기는 Docker 없이 매 커밋 도는 빠른 검사다.
const caddyfile = readFileSync(join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..', 'deploy', 'Caddyfile'), 'utf8')

/** 관리 사이트 블록에서 `이름 "값"` 꼴의 헤더 줄을 모은다. 값에 큰따옴표가 들어가는 헤더는 없다(있으면 아래 개수 단언이 깨진다). */
function adminSiteHeaders(): Map<string, string> {
  const start = caddyfile.indexOf('{$ADMIN_DOMAIN} {')
  expect(start, '관리 사이트 블록').toBeGreaterThan(-1)
  const headers = new Map<string, string>()
  for (const line of caddyfile.slice(start).split(/\r?\n/)) {
    const match = /^\s*([A-Za-z][A-Za-z-]+) "([^"]*)"\s*$/.exec(line)
    if (match) headers.set(match[1]!, match[2]!)
  }
  return headers
}

describe('deploy/Caddyfile의 관리 사이트 헤더', () => {
  it('admin-headers.ts의 모든 헤더가 같은 값으로 들어 있다', () => {
    const headers = adminSiteHeaders()
    for (const [name, value] of Object.entries(ADMIN_SECURITY_HEADERS)) {
      expect(headers.get(name), name).toBe(value)
    }
  })

  it('정본에 없는 HSTS와 COOP를 Caddy가 더한다(백엔드의 HSTS와 같은 값)', () => {
    const headers = adminSiteHeaders()
    expect(ADMIN_SECURITY_HEADERS['Strict-Transport-Security']).toBeUndefined()
    expect(headers.get('Strict-Transport-Security')).toBe('max-age=31536000; includeSubDomains')
    expect(headers.get('Cross-Origin-Opener-Policy')).toBe('same-origin')
  })

  it('보안 헤더 블록은 백엔드 프록시보다 뒤에 있다(첨부의 sandbox CSP를 덮어쓰지 않는다)', () => {
    const admin = caddyfile.slice(caddyfile.indexOf('{$ADMIN_DOMAIN} {'))
    const proxy = admin.indexOf('reverse_proxy api:8080')
    const csp = admin.indexOf('Content-Security-Policy "')
    expect(proxy).toBeGreaterThan(-1)
    expect(csp).toBeGreaterThan(proxy)
  })

  it('허용 IP 검사는 관리 사이트의 다른 어떤 처리보다 앞이고, 전부 route 블록 안에 있다', () => {
    const admin = caddyfile.slice(caddyfile.indexOf('{$ADMIN_DOMAIN} {'))
    const route = admin.indexOf('route {')
    const denied = admin.indexOf('respond @denied 404')
    expect(route).toBeGreaterThan(-1)
    expect(denied).toBeGreaterThan(route)
    for (const directive of ['reverse_proxy', 'file_server', 'try_files', 'root *']) {
      expect(admin.indexOf(directive), directive).toBeGreaterThan(denied)
    }
    expect(admin).toContain('@denied not remote_ip {$ADMIN_ALLOWED_CIDRS}')
  })

  it('백엔드로 가는 경로는 /api/*와 /attachments/*뿐이다(접두사 매칭이면 SPA의 /attachments 화면이 백엔드로 간다)', () => {
    expect(caddyfile).toContain('@backend path /api/* /attachments/*')
    expect(caddyfile).not.toMatch(/path [^\n]*\/attachments\*/)
  })
})
````

Run: `cd PortfolioBlog.Web && npx vitest run src/test/caddyfile.test.ts`
Expected: FAIL — `deploy/Caddyfile`이 없다(ENOENT).

- [ ] **Step 3: `deploy/Caddyfile`**(탭 들여쓰기 — `caddy fmt`의 형식)

````caddyfile
# 기술 블로그의 앞단. 사이트 2개: 공개 도메인과 관리 서브도메인(스펙 3.1·3.10).
# 값은 전부 환경변수로 받는다(deploy/.env). 이 파일은 caddy 이미지에 구워지므로 고치면 이미지를 다시 빌드한다.
{
	# 관리 API(기본 localhost:2019)를 끈다. 설정 변경은 컨테이너 재시작으로만 한다.
	admin off
	email {$ACME_EMAIL}
	servers {
		# compose가 TCP 80·443만 공개한다. h3(UDP)를 광고하지 않는다.
		protocols h1 h2
		timeouts {
			read_header 10s
			# 11MiB 업로드가 느린 회선에서도 끝나도록(약 92KB/s 이상).
			read_body 120s
			idle 2m
		}
	}
}

# 공개 사이트: 누구나, 읽기 전용. /api는 이 호스트에 존재하지 않는다.
{$DOMAIN} {
	log
	route {
		# defer: reverse_proxy가 응답에 더하는 Via와 오류 경로의 Server까지, 응답을 쓰기 직전에 지운다.
		header {
			-Server
			-Via
			defer
		}
		@api path /api /api/*
		respond @api 404
		# 공개 표면은 GET·HEAD뿐이다. 본문은 받을 이유가 없다.
		request_body {
			max_size 64KB
		}
		encode zstd gzip
		reverse_proxy api:8080
	}
}

# 관리 사이트: 허용 IP에서만 보인다. 허용 목록 밖에서는 모든 경로가 404다.
{$ADMIN_DOMAIN} {
	log
	route {
		header {
			-Server
			-Via
			defer
		}
		@denied not remote_ip {$ADMIN_ALLOWED_CIDRS}
		respond @denied 404

		# 백엔드로 가는 것은 이 둘뿐이다. 접두사(/attachments*)로 가르면 SPA 화면 주소 /attachments가 백엔드로 끌려간다.
		# 이 응답들의 보안 헤더는 백엔드가 붙인다(첨부의 sandbox CSP를 덮어쓰면 안 된다) — 아래 header 블록보다 앞이어야 한다.
		@backend path /api/* /attachments/*
		handle @backend {
			# 프레임워크의 multipart 상한(첨부 10MiB + 프레이밍 1MiB)과 같은 값.
			request_body {
				max_size 11MiB
			}
			reverse_proxy api:8080
		}

		# 여기부터는 정적 SPA. 앞의 다섯 값의 정본은 PortfolioBlog.Web/admin-headers.ts다(caddyfile.test.ts가 글자 그대로 비교한다).
		# HSTS는 그 파일에 의도적으로 없다 — 여기서 더한다. 값은 백엔드의 HSTS와 같다.
		header {
			Content-Security-Policy "default-src 'none'; script-src 'self'; style-src-elem 'self' 'unsafe-inline'; style-src-attr 'none'; img-src 'self'; connect-src 'self'; font-src 'self'; frame-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'"
			X-Content-Type-Options "nosniff"
			X-Frame-Options "DENY"
			Referrer-Policy "strict-origin-when-cross-origin"
			Permissions-Policy "accelerometer=(), autoplay=(), camera=(), display-capture=(), encrypted-media=(), fullscreen=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), midi=(), payment=(), picture-in-picture=(), publickey-credentials-get=(), screen-wake-lock=(), usb=(), xr-spatial-tracking=()"
			Strict-Transport-Security "max-age=31536000; includeSubDomains"
			Cross-Origin-Opener-Policy "same-origin"
		}
		root * /srv
		encode zstd gzip

		# 해시가 붙은 빌드 산출물: 있으면 영구 캐시, 없으면 404(index.html로 돌리지 않는다 — 배포 뒤 옛 청크 요청의 증상이 분명해진다).
		@asset_hit {
			path /assets/*
			file
		}
		handle @asset_hit {
			header Cache-Control "public, max-age=31536000, immutable"
			file_server
		}
		@asset_miss path /assets/*
		respond @asset_miss 404

		# 그 밖의 경로는 SPA 라우트다. index.html은 매번 재검증한다.
		header Cache-Control "no-cache"
		try_files {path} /index.html
		file_server
	}
}
````

Run: `cd PortfolioBlog.Web && npx vitest run src/test/caddyfile.test.ts` → 5개 통과. 이어서 `npm run lint && npm run typecheck && npm test` → **193개**.

- [ ] **Step 4: `PortfolioBlog.Api/Dockerfile`**

````dockerfile
# syntax=docker/dockerfile:1
# 빌드 컨텍스트는 저장소 루트다(Directory.Packages.props가 필요하다): docker build -f PortfolioBlog.Api/Dockerfile .
# 이미지 태그는 정확한 버전으로 고정한다. 올릴 때는 deploy/OPERATIONS.md의 "이미지 버전 올리기"를 따른다.
FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /src
# 복원 계층을 소스 변경과 분리한다(패키지 버전은 Directory.Packages.props가 전이 의존성까지 고정한다).
COPY Directory.Packages.props ./
COPY PortfolioBlog.Api/PortfolioBlog.Api.csproj PortfolioBlog.Api/
RUN dotnet restore PortfolioBlog.Api/PortfolioBlog.Api.csproj
COPY PortfolioBlog.Api/ PortfolioBlog.Api/
# UseAppHost=false: 네이티브 실행 파일을 만들지 않는다(진입점은 dotnet PortfolioBlog.Api.dll 하나).
RUN dotnet publish PortfolioBlog.Api/PortfolioBlog.Api.csproj -c Release -o /app --no-restore -p:UseAppHost=false \
    && rm -f /app/web.config /app/appsettings.Development.json
# 게시 결과가 기대한 모양인지 빌드에서 확인한다. 최종 이미지에는 셸이 없어 거기서는 볼 수 없다.
# - wwwroot는 css/site.css 하나여야 한다(csproj의 CompressionEnabled=false가 지워지면 .gz·.br가 돌아와 공개 경로가 늘어난다).
# - EF 디자인 타임 어셈블리가 섞이면 안 된다.
RUN test "$(cd /app/wwwroot && find . -type f | sort)" = "./css/site.css" \
    && test -z "$(find /app -name 'Microsoft.EntityFrameworkCore.Design*' -o -name 'Microsoft.CodeAnalysis*' -o -name '*.Development.json')"
# 볼륨 마운트 지점. 빈 named volume은 처음 마운트될 때 이 디렉터리의 소유자·권한으로 초기화된다.
RUN mkdir -p /data/attachments /data/dpkeys && chmod 700 /data/attachments /data/dpkeys

# chiseled: 셸·패키지 관리자가 없고 기본 사용자가 비루트(1654)다. extra: ICU·tzdata 포함(한글 정렬·정규화가 개발 환경과 같게 동작한다).
FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-noble-chiseled-extra AS final
WORKDIR /app
COPY --from=build /app ./
COPY --from=build --chown=1654:1654 /data /data
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    Attachments__RootPath=/data/attachments \
    DataProtection__KeysPath=/data/dpkeys
USER 1654
EXPOSE 8080
ENTRYPOINT ["dotnet", "PortfolioBlog.Api.dll"]
````

- [ ] **Step 5: `PortfolioBlog.Web/Dockerfile`**

````dockerfile
# syntax=docker/dockerfile:1
# 관리 SPA를 빌드해 Caddy 이미지에 굽는다. 빌드 컨텍스트는 저장소 루트다(deploy/Caddyfile도 함께 들어간다):
#   docker build -f PortfolioBlog.Web/Dockerfile .
FROM node:24.21.0-alpine AS build
WORKDIR /web
COPY PortfolioBlog.Web/package.json PortfolioBlog.Web/package-lock.json ./
# npm ci: package-lock.json과 글자 그대로 같은 트리만 설치한다(버전은 전부 고정돼 있다).
RUN npm ci
COPY PortfolioBlog.Web/ ./
# 배포되는 의존성에 high 이상 취약점이 있으면 이미지를 만들지 않는다(CI의 web 잡과 같은 기준).
# 급한 재배포를 새 권고가 막을 때만: --build-arg NPM_AUDIT=off (deploy/OPERATIONS.md 참고).
ARG NPM_AUDIT=on
RUN if [ "$NPM_AUDIT" = "on" ]; then npm audit --omit=dev --audit-level=high; fi
RUN npm run build

FROM caddy:2.11.4-alpine AS final
COPY deploy/Caddyfile /etc/caddy/Caddyfile
COPY --from=build /web/dist /srv
# 문법 오류를 배포가 아니라 빌드에서 잡는다. 환경변수 자리는 검사용 값으로 채운다.
RUN DOMAIN=blog.example.test ADMIN_DOMAIN=admin.blog.example.test ADMIN_ALLOWED_CIDRS=192.0.2.0/24 ACME_EMAIL=ops@example.test \
    caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile
````

- [ ] **Step 6: 두 이미지를 빌드하고 모양을 확인한다**(저장소 루트에서)

```bash
docker build -f PortfolioBlog.Api/Dockerfile -t pb-task2-api .
docker build -f PortfolioBlog.Web/Dockerfile -t pb-task2-caddy .
docker image inspect pb-task2-api --format '{{.Config.User}}'          # 1654
docker run --rm --entrypoint /bin/sh pb-task2-api -c true; echo $?     # 0이 아니어야 한다(셸 없음)
printf 'dummy-value-for-check\n' | docker run --rm -i pb-task2-api hash-password | tail -n 1   # base64 한 줄
docker run --rm pb-task2-caddy ls /srv                                 # index.html, assets, preview
docker run --rm pb-task2-caddy caddy version                           # v2.11.4
docker rmi pb-task2-api pb-task2-caddy
```

사보타주(각각 되돌린다): `PortfolioBlog.Api.csproj`에서 `<CompressionEnabled>false</CompressionEnabled>`를 지우고 api 이미지를 빌드 → `test … = "./css/site.css"` 단계에서 **빌드 실패**해야 한다. `deploy/Caddyfile`의 `respond @denied 404`를 `respon @denied 404`로 바꾸고 caddy 이미지를 빌드 → `caddy validate` 단계에서 빌드 실패해야 한다. Caddyfile의 CSP에서 `; frame-ancestors 'none'`을 빼면 `caddyfile.test.ts`가 실패해야 한다.

- [ ] **Step 7: 커밋**

```bash
git add .dockerignore .gitattributes deploy/Caddyfile PortfolioBlog.Api/Dockerfile PortfolioBlog.Web/Dockerfile PortfolioBlog.Web/src/test/caddyfile.test.ts
git commit -F <메시지 파일>   # 제목 예: "추가: 비루트·무셸 API 이미지와 관리 SPA를 품은 Caddy 이미지"
```

---

### Task 3: compose, DB 롤 초기화, 스모크 테스트

**Files:**
- Create: `deploy/docker-compose.yml`, `deploy/postgres-init/10-roles.sh`, `deploy/.env.example`, `deploy/docker-compose.smoke.yml`, `deploy/smoke/smoke.test.mjs`, `deploy/smoke/run.sh`
- Modify: `.gitignore`

**Interfaces:**
- Consumes: Task 2의 두 Dockerfile·Caddyfile, Task 1의 설정 키와 CLI.
- Produces: compose 서비스 `caddy`(172.30.0.2)·`api`·`postgres`·`tools`(프로필 `tools`, Task 4가 쓴다), 볼륨 `pgdata`·`attachments`·`dpkeys`·`caddy_data`·`caddy_config`, 스모크 서비스 `smoke-allowed`(172.30.0.10)·`smoke-denied`(172.30.0.11), `smoke.test.mjs`의 `ROLE` = `allowed`·`denied`·`seed`·`verify-restore`(뒤 둘은 Task 4가 쓴다). `run.sh`가 내보내는 환경: `COMPOSE_PROJECT_NAME=pb-smoke`, `COMPOSE_FILE`, `COMPOSE_ENV_FILES=smoke/.env.smoke`.

- [ ] **Step 1: `.gitignore`에 생성물 규칙을 먼저 넣는다**(Stop 훅이 `git add -A`를 한다)

"기술 블로그" 블록의 `deploy/.env` 아래에:

```
deploy/backups/
deploy/smoke/backups/
```

(`deploy/smoke/.env.smoke`는 기존 `.env.*` 규칙이 이미 막는다 — `git check-ignore deploy/smoke/.env.smoke`로 확인한다.)

- [ ] **Step 2: `deploy/postgres-init/10-roles.sh`**

````bash
#!/bin/sh
# 빈 pgdata에서 처음 뜰 때 한 번만 실행된다(공식 이미지의 /docker-entrypoint-initdb.d 규약).
# 앱은 슈퍼유저(postgres)로 접속하지 않는다:
#   blog_app    — DB·스키마 소유자. 마이그레이션과 관리 API가 쓴다. 슈퍼유저가 아니므로 COPY ... PROGRAM 같은 서버 측 실행이 불가능하다.
#   blog_public — 공개 페이지 조회 전용. 여기서는 접속 권한만 준다. 테이블별 SELECT는 앱이 시작할 때마다 다시 맞춘다
#                 (PortfolioBlog.Api의 PublicRoleGrants — 허용 테이블 목록이 코드와 함께 버전 관리된다).
# 비밀번호는 psql 변수로 넘겨 SQL 문자열 리터럴로 안전하게 인용한다(:'name'). heredoc이 따옴표('SQL')라 셸은 아래 본문을 건드리지 않는다
# — 줄 끝 주석의 ${…}는 어느 환경변수의 값인지 적은 표기일 뿐이다(저장소의 비밀값 스캐너가 자리표시자로 인식한다).
set -eu
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres \
  -v app_pw="$BLOG_APP_PASSWORD" -v public_pw="$BLOG_PUBLIC_PASSWORD" <<'SQL'
CREATE ROLE blog_app LOGIN PASSWORD :'app_pw' NOSUPERUSER NOCREATEDB NOCREATEROLE;       -- 값: ${BLOG_APP_PASSWORD}
CREATE ROLE blog_public LOGIN PASSWORD :'public_pw' NOSUPERUSER NOCREATEDB NOCREATEROLE; -- 값: ${BLOG_PUBLIC_PASSWORD}
CREATE DATABASE blog OWNER blog_app;
REVOKE ALL ON DATABASE blog FROM PUBLIC;
GRANT CONNECT ON DATABASE blog TO blog_public;
\connect blog
REVOKE ALL ON SCHEMA public FROM PUBLIC;
ALTER SCHEMA public OWNER TO blog_app;
SQL
````

- [ ] **Step 3: `deploy/docker-compose.yml`**

````yaml
name: portfolioblog

x-hardening: &hardening
  restart: unless-stopped
  security_opt:
    - no-new-privileges:true
  cap_drop:
    - ALL
  logging:
    driver: json-file
    options:
      max-size: "10m"
      max-file: "5"

services:
  caddy:
    <<: *hardening
    build:
      context: ..
      dockerfile: PortfolioBlog.Web/Dockerfile
    cap_add:
      - NET_BIND_SERVICE
    read_only: true
    ports:
      - "${HTTP_BIND:-0.0.0.0:80}:80"
      - "${HTTPS_BIND:-0.0.0.0:443}:443"
    environment:
      DOMAIN: ${DOMAIN:?}
      ADMIN_DOMAIN: ${ADMIN_DOMAIN:?}
      ADMIN_ALLOWED_CIDRS: ${ADMIN_ALLOWED_CIDRS:?}
      ACME_EMAIL: ${ACME_EMAIL:?}
    volumes:
      - caddy_data:/data
      - caddy_config:/config
    networks:
      edge:
        ipv4_address: 172.30.0.2
    depends_on:
      api:
        condition: service_healthy

  api:
    <<: *hardening
    build:
      context: ..
      dockerfile: PortfolioBlog.Api/Dockerfile
    read_only: true
    tmpfs:
      - /tmp:mode=1777,size=64m
    environment:
      ConnectionStrings__Default: "Host=postgres;Database=blog;Username=blog_app;Password=${BLOG_APP_PASSWORD:?};Command Timeout=30"
      ConnectionStrings__Public: "Host=postgres;Database=blog;Username=blog_public;Password=${BLOG_PUBLIC_PASSWORD:?};Command Timeout=30"
      Site__PublicOrigin: ${PUBLIC_ORIGIN:?}
      Site__AdminOrigin: ${ADMIN_ORIGIN:?}
      Site__Title: ${SITE_TITLE:-Blog}
      Site__Description: ${SITE_DESCRIPTION:-}
      Site__Author: ${SITE_AUTHOR:-}
      Admin__AllowedCidrs: ${ADMIN_ALLOWED_CIDRS:?}
      Admin__PasswordHash: ${ADMIN_PASSWORD_HASH:?}
      Proxy__TrustedIp: 172.30.0.2
    volumes:
      - attachments:/data/attachments
      - dpkeys:/data/dpkeys
    networks:
      - edge
      - db
    healthcheck:
      test: ["CMD", "dotnet", "PortfolioBlog.Api.dll", "healthcheck"]
      interval: 30s
      timeout: 5s
      retries: 3
      start_period: 40s
      start_interval: 2s
    depends_on:
      postgres:
        condition: service_healthy

  postgres:
    <<: *hardening
    image: postgres:17.11-alpine
    cap_add:
      - CHOWN
      - DAC_OVERRIDE
      - FOWNER
      - SETGID
      - SETUID
    environment:
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:?}
      BLOG_APP_PASSWORD: ${BLOG_APP_PASSWORD:?}
      BLOG_PUBLIC_PASSWORD: ${BLOG_PUBLIC_PASSWORD:?}
    volumes:
      - pgdata:/var/lib/postgresql/data
      - ./postgres-init:/docker-entrypoint-initdb.d:ro
    networks:
      - db
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U blog_app -d blog"]
      interval: 10s
      timeout: 5s
      retries: 12

  # 백업·복원 전용(backup.sh·restore.sh가 `docker compose run`으로만 띄운다). 평소에는 뜨지 않는다.
  tools:
    profiles: ["tools"]
    image: postgres:17.11-alpine
    user: "1654:1654"
    network_mode: none
    read_only: true
    security_opt:
      - no-new-privileges:true
    cap_drop:
      - ALL
    entrypoint: ["/bin/sh", "-c"]
    volumes:
      - attachments:/data/attachments

networks:
  edge:
    ipam:
      config:
        - subnet: 172.30.0.0/24
          gateway: 172.30.0.1
          ip_range: 172.30.0.128/25
  db:
    internal: true

volumes:
  pgdata:
  attachments:
  dpkeys:
  caddy_data:
  caddy_config:
````

- [ ] **Step 4: `deploy/.env.example`**

````
# 복사해서 deploy/.env 로 쓴다(그 파일은 .gitignore 대상이다 — 절대 커밋하지 않는다). 권한은 600.
# 아래 값은 전부 자리표시자(example/changeme)다. 실제 값으로 바꾸지 않으면 뜨지 않거나 안전하지 않다.

# 공개 도메인과 관리 서브도메인. 둘 다 이 서버의 IP를 가리키는 DNS A/AAAA 레코드가 있어야 Caddy가 인증서를 받는다.
DOMAIN=blog.example.com
ADMIN_DOMAIN=admin.blog.example.com
# 앱이 절대 URL(피드·sitemap)과 Origin 검사에 쓰는 값. 위 도메인과 같은 호스트여야 하고 반드시 https다.
PUBLIC_ORIGIN=https://blog.example.com
ADMIN_ORIGIN=https://admin.blog.example.com

# 관리 표면에 들어올 수 있는 주소(CIDR, 공백으로 구분). Caddy와 앱이 같은 값을 읽는다. 0.0.0.0/0 같은 값은 넣지 않는다.
ADMIN_ALLOWED_CIDRS=203.0.113.7/32 2001:db8::/64
# Let's Encrypt가 만료·문제 알림을 보낼 주소.
ACME_EMAIL=ops@example.com

# 사이트 표기(선택).
SITE_TITLE=Blog
SITE_DESCRIPTION=
SITE_AUTHOR=

# DB 비밀번호 셋. 서로 다른 무작위 값으로: openssl rand -base64 24 | tr -dc 'A-Za-z0-9'
# 연결 문자열에 그대로 들어가므로 영문·숫자만 쓴다(';'·'='·공백·따옴표 금지).
# 주의: postgres는 "빈 데이터 볼륨에서 처음 뜰 때"만 이 값으로 롤을 만든다. 나중에 바꾸려면 OPERATIONS.md의 절차를 따른다.
POSTGRES_PASSWORD=changeme-superuser
BLOG_APP_PASSWORD=changeme-app
BLOG_PUBLIC_PASSWORD=changeme-public

# 관리자 비밀번호의 해시(비밀번호 자체가 아니다). 만드는 법:
#   docker compose build api && docker run --rm -it portfolioblog-api hash-password   (입력은 화면에 보이지 않는다)
ADMIN_PASSWORD_HASH=changeme-paste-the-hash-here

# 바인드 주소(선택). 기본은 모든 인터페이스의 80·443이다.
# HTTP_BIND=0.0.0.0:80
# HTTPS_BIND=0.0.0.0:443
````

- [ ] **Step 5: 스모크 덮어쓰기와 테스트**

`deploy/docker-compose.smoke.yml`:

````yaml
# 스모크 테스트 전용 덮어쓰기. 운영에 쓰지 않는다. deploy/smoke/run.sh가 기본 파일과 함께 읽는다.
services:
  api:
    environment:
      # Playwright E2E는 브라우저 둘이 같은 IP에서 여러 번 로그인한다. 운영 기본값(IP당 5회/분)은 그대로 두고 여기서만 올린다.
      Admin__LoginPerIpPerMinute: "40"
      Admin__LoginGlobalPerMinute: "80"

  # 허용 목록 안의 클라이언트(172.30.0.10)와 밖의 클라이언트(172.30.0.11). 호스트의 NAT 동작에 기대지 않으려고 컨테이너에서 찌른다.
  smoke-allowed: &smoke
    profiles: ["smoke"]
    image: node:24.21.0-alpine
    read_only: true
    cap_drop:
      - ALL
    security_opt:
      - no-new-privileges:true
    working_dir: /smoke
    command: ["node", "--test", "--test-reporter=spec", "/smoke/smoke.test.mjs"]
    environment:
      ROLE: ${SMOKE_ROLE:-allowed}
      DOMAIN: ${DOMAIN:?}
      ADMIN_DOMAIN: ${ADMIN_DOMAIN:?}
      PUBLIC_ORIGIN: ${PUBLIC_ORIGIN:?}
      ADMIN_ORIGIN: ${ADMIN_ORIGIN:?}
      SMOKE_ADMIN_PASSWORD: ${SMOKE_ADMIN_PASSWORD:?}
      # Caddy가 *.localhost에 내부 CA로 발급한 인증서를 검증한다(-k로 건너뛰지 않는다).
      NODE_EXTRA_CA_CERTS: /caddy-data/caddy/pki/authorities/local/root.crt
    extra_hosts:
      - "${DOMAIN}:172.30.0.2"
      - "${ADMIN_DOMAIN}:172.30.0.2"
    volumes:
      - ./smoke:/smoke:ro
      - ../PortfolioBlog.Web/admin-headers.ts:/web/admin-headers.ts:ro
      - caddy_data:/caddy-data:ro
    networks:
      edge:
        ipv4_address: 172.30.0.10

  smoke-denied:
    <<: *smoke
    environment:
      ROLE: denied
      DOMAIN: ${DOMAIN:?}
      ADMIN_DOMAIN: ${ADMIN_DOMAIN:?}
      PUBLIC_ORIGIN: ${PUBLIC_ORIGIN:?}
      ADMIN_ORIGIN: ${ADMIN_ORIGIN:?}
      SMOKE_ADMIN_PASSWORD: ${SMOKE_ADMIN_PASSWORD:?}
      NODE_EXTRA_CA_CERTS: /caddy-data/caddy/pki/authorities/local/root.crt
    networks:
      edge:
        ipv4_address: 172.30.0.11
````

`deploy/smoke/smoke.test.mjs`:

````js
// 배포 스택(Caddy + api + postgres) 스모크 테스트. compose 네트워크 안의 컨테이너에서 돈다(docker-compose.smoke.yml).
// ROLE=allowed: 관리 허용 목록 안의 IP(172.30.0.10). ROLE=denied: 밖의 IP(172.30.0.11).
// fetch를 쓰지 않는다: Host·경로를 정규화 없이 그대로 보내야 하는 검사가 있다(node:http는 경로를 손대지 않는다).
import assert from 'node:assert/strict'
import http from 'node:http'
import https from 'node:https'
import net from 'node:net'
import { test } from 'node:test'
import zlib from 'node:zlib'
import { createHash } from 'node:crypto'
import { ADMIN_SECURITY_HEADERS } from '/web/admin-headers.ts'

const { ROLE, DOMAIN, ADMIN_DOMAIN, PUBLIC_ORIGIN, ADMIN_ORIGIN, SMOKE_ADMIN_PASSWORD } = process.env
for (const [key, value] of Object.entries({ ROLE, DOMAIN, ADMIN_DOMAIN, PUBLIC_ORIGIN, ADMIN_ORIGIN, SMOKE_ADMIN_PASSWORD })) {
  assert.ok(value, `환경변수 ${key}가 필요합니다`)
}
const XRW = { 'X-Requested-With': 'XMLHttpRequest' }
const PUBLIC_CSP = "default-src 'none'; img-src 'self'; style-src 'self'; font-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'"
const HSTS = 'max-age=31536000; includeSubDomains'
const MIB = 1024 * 1024

/** 요청 하나를 보내고 상태·헤더·본문을 돌려준다. path는 정규화 없이 그대로 나간다. */
function send({ host, path = '/', method = 'GET', headers = {}, body, chunks, tls = true, port, servername }) {
  return new Promise((resolve, reject) => {
    const options = { host, port: port ?? (tls ? 443 : 80), path, method, headers: { Host: host, ...headers }, servername: servername ?? host }
    const request = (tls ? https : http).request(options, response => {
      const parts = []
      response.on('data', part => parts.push(part))
      response.on('end', () => resolve({ status: response.statusCode, headers: response.headers, body: Buffer.concat(parts) }))
      response.on('error', reject)
    })
    request.on('error', reject)
    request.setTimeout(60_000, () => request.destroy(new Error(`시간 초과: ${method} ${host}${path}`)))
    if (chunks) { for (const part of chunks) request.write(part); request.end() } // Content-Length 없이 → chunked
    else request.end(body)
  })
}

const pub = (path, extra = {}) => send({ host: DOMAIN, path, ...extra })
const adm = (path, extra = {}) => send({ host: ADMIN_DOMAIN, path, ...extra })

function pngChunk(type, data) {
  const head = Buffer.alloc(4); head.writeUInt32BE(data.length)
  const typed = Buffer.concat([Buffer.from(type, 'latin1'), data])
  const crc = Buffer.alloc(4); crc.writeUInt32BE(zlib.crc32(typed) >>> 0)
  return Buffer.concat([head, typed, crc])
}

/** 대략 approxBytes 크기의 유효한 PNG. 픽셀이 난수라 압축되지 않는다(크기 경계 검사용). seed가 다르면 내용(sha)도 다르다. */
function makePng(approxBytes, seed) {
  const width = 256, rowBytes = 1 + width * 3, rows = Math.max(1, Math.floor(approxBytes / rowBytes))
  const raw = Buffer.alloc(rows * rowBytes)
  let state = seed >>> 0
  for (let i = 0; i < raw.length; i++) { state = (Math.imul(state, 1664525) + 1013904223) >>> 0; raw[i] = i % rowBytes === 0 ? 0 : state >>> 24 }
  const header = Buffer.alloc(13); header.writeUInt32BE(width, 0); header.writeUInt32BE(rows, 4); header.set([8, 2, 0, 0, 0], 8)
  return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), pngChunk('IHDR', header),
    pngChunk('IDAT', zlib.deflateSync(raw, { level: 0 })), pngChunk('IEND', Buffer.alloc(0))])
}

function multipart(fileName, bytes) {
  const boundary = `----smoke${Date.now().toString(16)}`
  const head = Buffer.from(`--${boundary}\r\nContent-Disposition: form-data; name="file"; filename="${fileName}"\r\nContent-Type: image/png\r\n\r\n`)
  const tail = Buffer.from(`\r\n--${boundary}--\r\n`)
  return { contentType: `multipart/form-data; boundary=${boundary}`, body: Buffer.concat([head, bytes, tail]) }
}

function assertEmpty404(response, label) {
  assert.equal(response.status, 404, label)
  assert.equal(response.body.length, 0, `${label}: Caddy의 404는 본문이 없다(본문이 있으면 백엔드까지 닿은 것이다)`)
  assert.equal(response.headers['content-security-policy'], undefined, `${label}: 백엔드 응답이 아니어야 한다`)
}

function assertNoProductHeaders(response, label) {
  assert.equal(response.headers.server, undefined, `${label}: Server 헤더`)
  assert.equal(response.headers.via, undefined, `${label}: Via 헤더`)
}

if (ROLE === 'allowed') {
  test('공개 사이트: 페이지·헬스가 뜨고 백엔드의 보안 헤더가 그대로 온다', async () => {
    const health = await pub('/health')
    assert.equal(health.status, 200)
    const home = await pub('/')
    assert.equal(home.status, 200)
    assert.equal(home.headers['content-security-policy'], PUBLIC_CSP)
    assert.equal(home.headers['strict-transport-security'], HSTS)
    assert.equal(home.headers['x-content-type-options'], 'nosniff')
    assert.equal(home.headers['x-frame-options'], 'DENY')
    assertNoProductHeaders(home, '공개 /')
    const css = await pub('/css/site.css')
    assert.equal(css.status, 200)
    for (const path of ['/css/site.css.gz', '/css/site.css.br', '/web.config', '/appsettings.json']) {
      assert.equal((await pub(path)).status, 404, path)
    }
  })

  test('공개 사이트: /api는 어떤 표기로도 Caddy가 404로 끊는다', async () => {
    for (const path of ['/api', '/api/', '/api/posts', '/API/posts', '/Api/auth/login', '/%61pi/posts', '/api%2fposts', '//api/posts', '/x/../api/posts', '/./api/posts']) {
      for (const method of ['GET', 'POST']) {
        const response = await pub(path, { method, headers: XRW })
        assert.equal(response.status, 404, `${method} ${path}`)
        assertNoProductHeaders(response, `${method} ${path}`)
      }
    }
    assertEmpty404(await pub('/api/posts', { headers: XRW }), '/api/posts')
    assertEmpty404(await pub('/API/posts', { headers: XRW }), '/API/posts')
  })

  test('공개 사이트: 평문 HTTP는 HTTPS로 넘긴다', async () => {
    const response = await pub('/', { tls: false })
    assert.equal(response.status, 308)
    assert.match(response.headers.location, new RegExp(`^https://${DOMAIN.replaceAll('.', '\\.')}/`))
  })

  test('공개 사이트: 본문이 큰 요청과 긴 요청 줄은 5xx 없이 거부된다', async () => {
    const big = await pub('/', { method: 'POST', body: Buffer.alloc(100 * 1024, 0x61), headers: { 'Content-Type': 'text/plain' } })
    assert.equal(big.status, 413)
    const long = await pub(`/search?q=${'a'.repeat(9000)}`)
    assert.ok([414, 431].includes(long.status), `긴 요청 줄: ${long.status}`)
  })

  test('관리 사이트: 정적 SPA에 admin-headers.ts의 값 + HSTS가 붙는다', async () => {
    const home = await adm('/')
    assert.equal(home.status, 200)
    assert.match(home.headers['content-type'], /^text\/html/)
    for (const [name, value] of Object.entries(ADMIN_SECURITY_HEADERS)) {
      assert.equal(home.headers[name.toLowerCase()], value, name)
    }
    assert.equal(home.headers['strict-transport-security'], HSTS)
    assert.equal(home.headers['cross-origin-opener-policy'], 'same-origin')
    assert.equal(home.headers['cache-control'], 'no-cache')
    assertNoProductHeaders(home, '관리 /')
  })

  test('관리 사이트: SPA 화면 주소는 index.html, /assets의 없는 파일은 404', async () => {
    const index = (await adm('/')).body.toString('utf8')
    for (const path of ['/attachments', '/posts/new', '/series', '/login?next=%2Ftags']) {
      const response = await adm(path)
      assert.equal(response.status, 200, path)
      assert.equal(response.body.toString('utf8'), index, `${path}는 SPA 문서여야 한다(백엔드로 가면 안 된다)`)
    }
    const script = /src="(\/assets\/[^"]+\.js)"/.exec(index)?.[1]
    assert.ok(script, 'index.html에 /assets 스크립트가 있어야 한다')
    const hit = await adm(script, { headers: { 'Accept-Encoding': 'gzip' } })
    assert.equal(hit.status, 200)
    assert.equal(hit.headers['cache-control'], 'public, max-age=31536000, immutable')
    assert.equal(hit.headers['content-encoding'], 'gzip')
    assert.equal(hit.headers['content-security-policy'], ADMIN_SECURITY_HEADERS['Content-Security-Policy'])
    const miss = await adm('/assets/missing-0000.js')
    assert.equal(miss.status, 404)
    assert.equal(miss.headers['cache-control'], undefined, '404를 영구 캐시하게 두면 안 된다')
    assertNoProductHeaders(miss, '/assets 404')
  })

  test('관리 API: CSRF 헤더 검사와 백엔드 헤더가 Caddy를 지나도 그대로다', async () => {
    assert.equal((await adm('/api/auth/me')).status, 403)
    const me = await adm('/api/auth/me', { headers: XRW })
    assert.equal(me.status, 200)
    assert.deepEqual(JSON.parse(me.body.toString('utf8')), { authenticated: false })
    assert.equal(me.headers['content-security-policy'], PUBLIC_CSP, 'API 응답의 CSP는 백엔드 값이다(SPA용 CSP로 덮이면 안 된다)')
    assert.equal(me.headers['cache-control'], 'no-store')
    assertNoProductHeaders(me, '/api/auth/me')
    assert.equal((await adm('/api/posts', { headers: XRW })).status, 401)
  })

  test('글쓰기 전 과정: 로그인 → 첨부 → 글 → 공개 페이지·피드 → 정리 → 로그아웃', async () => {
    const json = { ...XRW, Origin: ADMIN_ORIGIN, 'Content-Type': 'application/json' }
    const wrong = await adm('/api/auth/login', { method: 'POST', headers: json, body: JSON.stringify({ password: 'wrong-dummy-value' }) })
    assert.equal(wrong.status, 401)
    const noOrigin = await adm('/api/auth/login', { method: 'POST', headers: { ...XRW, 'Content-Type': 'application/json' }, body: JSON.stringify({ password: SMOKE_ADMIN_PASSWORD }) })
    assert.equal(noOrigin.status, 403)

    const login = await adm('/api/auth/login', { method: 'POST', headers: json, body: JSON.stringify({ password: SMOKE_ADMIN_PASSWORD }) })
    assert.equal(login.status, 204)
    const setCookie = login.headers['set-cookie']?.find(value => value.startsWith('__Host-AdminSession='))
    assert.ok(setCookie, '세션 쿠키')
    for (const attribute of ['secure', 'httponly', 'samesite=strict', 'path=/']) {
      assert.ok(setCookie.toLowerCase().includes(attribute), `쿠키 속성 ${attribute}`)
    }
    assert.ok(!/domain=/i.test(setCookie), '__Host- 쿠키에는 Domain이 없어야 한다')
    const Cookie = setCookie.split(';')[0]
    const authed = { ...XRW, Origin: ADMIN_ORIGIN, Cookie }

    // 클라이언트가 보낸 X-Forwarded-For는 Caddy가 버린다 — 허용 목록 밖 주소를 적어 보내도 판정은 실제 주소(허용)로 난다.
    assert.equal((await adm('/api/posts', { headers: { ...authed, 'X-Forwarded-For': '203.0.113.9' } })).status, 200)

    const tiny = multipart('smoke.png', makePng(2_000, 1))
    const uploaded = await adm('/api/attachments', { method: 'POST', headers: { ...authed, 'Content-Type': tiny.contentType }, body: tiny.body })
    assert.ok([200, 201].includes(uploaded.status), `업로드: ${uploaded.status}`)
    const attachment = JSON.parse(uploaded.body.toString('utf8'))

    const slug = `smoke-${Date.now().toString(36)}`
    const created = await adm('/api/posts', {
      method: 'POST', headers: { ...authed, 'Content-Type': 'application/json' },
      body: JSON.stringify({ slug, title: '스모크 <글> & 제목', summary: '배포 스모크', contentMarkdown: `본문 ![그림](${attachment.url})`, tagNames: [], seriesId: null, seriesOrder: null }),
    })
    assert.equal(created.status, 201, created.body.toString('utf8'))
    const post = JSON.parse(created.body.toString('utf8'))

    try {
      const page = await pub(`/posts/${slug}`)
      assert.equal(page.status, 200)
      assert.ok(page.body.toString('utf8').includes(`<img src="${attachment.url}"`), '공개 페이지에 첨부 이미지')
      for (const host of [DOMAIN, ADMIN_DOMAIN]) {
        const image = await send({ host, path: attachment.url })
        assert.equal(image.status, 200, `${host} 첨부`)
        assert.equal(image.headers['content-security-policy'], "default-src 'none'; sandbox", `${host} 첨부 CSP(덮어쓰이면 안 된다)`)
        assert.equal(image.headers['content-type'], 'image/png')
        assert.equal(image.headers['cache-control'], 'public, max-age=31536000, immutable')
      }
      const feed = (await pub('/feed.xml')).body.toString('utf8')
      assert.ok(feed.includes(`${PUBLIC_ORIGIN}/posts/${slug}`), '피드의 절대 URL은 설정된 공개 origin이다')
      assert.ok(!feed.includes('http://api') && !feed.includes(':8080'), '피드에 내부 주소가 새면 안 된다')
    } finally {
      assert.equal((await adm(`/api/posts/${post.id}?version=${post.version}`, { method: 'DELETE', headers: authed })).status, 204)
      assert.equal((await adm(`/api/attachments/${attachment.id}`, { method: 'DELETE', headers: authed })).status, 204)
    }

    // 업로드 크기 경계: Caddy의 상한(11MiB)이 앱의 상한(10MiB)보다 먼저 정상 업로드를 끊지 않는다.
    const nearLimit = multipart('near.png', makePng(10 * MIB - 64 * 1024, 2))
    const accepted = await adm('/api/attachments', { method: 'POST', headers: { ...authed, 'Content-Type': nearLimit.contentType }, body: nearLimit.body })
    assert.equal(accepted.status, 201, '10MiB 바로 아래는 통과해야 한다')
    assert.equal((await adm(`/api/attachments/${JSON.parse(accepted.body.toString('utf8')).id}`, { method: 'DELETE', headers: authed })).status, 204)
    const overApp = multipart('over.png', makePng(10 * MIB + 256 * 1024, 3))
    const rejected = await adm('/api/attachments', { method: 'POST', headers: { ...authed, 'Content-Type': overApp.contentType }, body: overApp.body })
    assert.equal(rejected.status, 413)
    assert.equal(JSON.parse(rejected.body.toString('utf8')).title, '첨부가 너무 큽니다', '10MiB를 조금 넘으면 앱이 설명과 함께 거부한다')
    const huge = multipart('huge.png', makePng(12 * MIB, 4))
    const cut = await adm('/api/attachments', { method: 'POST', headers: { ...authed, 'Content-Type': huge.contentType }, chunks: [huge.body.subarray(0, MIB), huge.body.subarray(MIB)] })
    assert.equal(cut.status, 413, '길이를 알리지 않은(chunked) 12MiB도 5xx 없이 413')

    assert.equal((await adm('/api/auth/logout', { method: 'POST', headers: authed })).status, 204)
    assert.equal((await adm('/api/posts', { headers: authed })).status, 401, '로그아웃 뒤에는 복사해 둔 쿠키가 통하지 않는다')
  })
}

if (ROLE === 'denied') {
  test('관리 사이트: 허용 목록 밖에서는 모든 경로가 본문 없는 404다(X-Forwarded-For를 위조해도)', async () => {
    const forged = { ...XRW, 'X-Forwarded-For': '172.30.0.10', 'X-Real-IP': '172.30.0.10', Forwarded: 'for=172.30.0.10' }
    for (const path of ['/', '/login', '/index.html', '/attachments', '/assets/index.js', '/api/auth/me', '/api/posts', '/attachments/a/b.png', '/health']) {
      assertEmpty404(await adm(path, { headers: forged }), `GET ${path}`)
    }
    const login = await adm('/api/auth/login', { method: 'POST', headers: { ...forged, Origin: ADMIN_ORIGIN, 'Content-Type': 'application/json' }, body: JSON.stringify({ password: SMOKE_ADMIN_PASSWORD }) })
    assertEmpty404(login, 'POST /api/auth/login')
  })

  test('관리 사이트: 공개 도메인의 TLS 이름(SNI)으로 들어와 Host만 관리 호스트로 바꿔도 404다', async () => {
    const response = await send({ host: DOMAIN, servername: DOMAIN, path: '/api/auth/me', headers: { ...XRW, Host: ADMIN_DOMAIN } })
    assert.equal(response.status, 404)
    assert.equal(response.body.length, 0)
  })

  test('공개 사이트는 허용 목록 밖에서도 보인다', async () => {
    assert.equal((await pub('/')).status, 200)
    assert.equal((await pub('/health')).status, 200)
  })

  test('Caddy를 건너뛰고 api에 직접 붙어도 위조한 X-Forwarded-For는 통하지 않는다', async () => {
    // 이 컨테이너는 신뢰 프록시(Caddy의 고정 IP)가 아니므로 앱은 전달 헤더를 버리고 실제 주소(허용 목록 밖)로 판정한다.
    const response = await send({ host: 'api', port: 8080, tls: false, path: '/api/auth/me', headers: { ...XRW, Host: ADMIN_DOMAIN, 'X-Forwarded-For': '172.30.0.10', 'X-Forwarded-Proto': 'https' } })
    assert.equal(response.status, 403)
  })

  test('DB는 edge 네트워크에서 닿지 않는다', async () => {
    const outcome = await new Promise(resolve => {
      const socket = net.connect({ host: 'postgres', port: 5432 })
      socket.setTimeout(3000, () => { socket.destroy(); resolve('timeout') })
      socket.on('connect', () => { socket.destroy(); resolve('connected') })
      socket.on('error', error => resolve(error.code))
    })
    assert.notEqual(outcome, 'connected')
  })
}

// 복원 리허설: seed가 글과 첨부를 남기고, 백업 → 볼륨 삭제 → 복원 뒤에 verify-restore가 같은 내용이 돌아왔는지 본다.
const REHEARSAL_SLUG = 'restore-rehearsal'
const rehearsalPng = () => makePng(200_000, 7)
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex')

if (ROLE === 'seed') {
  test('복원 리허설용 글과 첨부를 남긴다', async () => {
    const base = { ...XRW, Origin: ADMIN_ORIGIN }
    const login = await adm('/api/auth/login', { method: 'POST', headers: { ...base, 'Content-Type': 'application/json' }, body: JSON.stringify({ password: SMOKE_ADMIN_PASSWORD }) })
    assert.equal(login.status, 204)
    const authed = { ...base, Cookie: login.headers['set-cookie'].find(value => value.startsWith('__Host-AdminSession=')).split(';')[0] }
    const form = multipart('rehearsal.png', rehearsalPng())
    const uploaded = await adm('/api/attachments', { method: 'POST', headers: { ...authed, 'Content-Type': form.contentType }, body: form.body })
    assert.ok([200, 201].includes(uploaded.status), `업로드: ${uploaded.status}`)
    const attachment = JSON.parse(uploaded.body.toString('utf8'))
    assert.equal(attachment.sha256, sha256(rehearsalPng()), '메타데이터가 없는 PNG는 바이트 그대로 저장된다(verify-restore가 이 값에 기댄다)')
    const created = await adm('/api/posts', {
      method: 'POST', headers: { ...authed, 'Content-Type': 'application/json' },
      body: JSON.stringify({ slug: REHEARSAL_SLUG, title: '복원 리허설', summary: '백업에서 돌아와야 하는 글', contentMarkdown: `![그림](${attachment.url})`, tagNames: ['복원'], seriesId: null, seriesOrder: null }),
    })
    assert.equal(created.status, 201, created.body.toString('utf8'))
  })
}

if (ROLE === 'verify-restore') {
  test('복원된 스택에 글과 첨부가 바이트 그대로 돌아왔다', async () => {
    const page = await pub(`/posts/${REHEARSAL_SLUG}`)
    assert.equal(page.status, 200)
    const url = /<img src="(\/attachments\/[^"]+)"/.exec(page.body.toString('utf8'))?.[1]
    assert.ok(url, '복원된 글에 첨부 이미지가 있어야 한다')
    const image = await pub(url)
    assert.equal(image.status, 200)
    assert.equal(sha256(image.body), sha256(rehearsalPng()))
    assert.equal((await pub(`/tags/${encodeURIComponent('복원')}`)).status, 200)
  })
}
````

- [ ] **Step 6: 오케스트레이터 `deploy/smoke/run.sh`**(이 Task의 판: 복원 리허설과 E2E 단계는 Task 4·5가 `step "통과"` 앞에 끼워 넣는다)

````bash
#!/usr/bin/env bash
# 배포 스택 스모크: 이미지 빌드 → 기동 → 접근·헤더·한도 검사(허용/비허용 IP) → DB 롤 검사 → 백업·삭제·복원 리허설 → (선택) 브라우저 E2E.
# 운영과 같은 compose·Caddyfile·이미지를 쓴다. 다른 것은 이름(pb-smoke), 루프백 포트, *.localhost 도메인(Caddy 내부 CA), 버려질 비밀값뿐이다.
# 사용법: deploy/smoke/run.sh          환경변수: SMOKE_E2E=1(Playwright까지), SMOKE_KEEP=1(끝나도 스택을 남긴다)
set -euo pipefail
export MSYS_NO_PATHCONV=1
here="$(cd "$(dirname "$0")" && pwd)"
deploy="$(dirname "$here")"
cd "$deploy"

export COMPOSE_PROJECT_NAME=pb-smoke
export COMPOSE_PATH_SEPARATOR=:
export COMPOSE_FILE=docker-compose.yml:docker-compose.smoke.yml
export COMPOSE_ENV_FILES=smoke/.env.smoke

random() { head -c 18 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 20; }
admin_password="smoke-$(random)"

write_env() { # $1 = 관리자 비밀번호 해시
  umask 077
  cat > smoke/.env.smoke <<EOF
DOMAIN=blog.localhost
ADMIN_DOMAIN=admin.blog.localhost
PUBLIC_ORIGIN=https://blog.localhost:8443
ADMIN_ORIGIN=https://admin.blog.localhost:8443
ADMIN_ALLOWED_CIDRS=172.30.0.10/32 172.30.0.1/32
ACME_EMAIL=smoke@example.test
HTTP_BIND=127.0.0.1:8081
HTTPS_BIND=127.0.0.1:8443
POSTGRES_PASSWORD=${pg_password}
BLOG_APP_PASSWORD=${app_password}
BLOG_PUBLIC_PASSWORD=${public_password}
ADMIN_PASSWORD_HASH=${1}
SMOKE_ADMIN_PASSWORD=${admin_password}
EOF
}
pg_password="$(random)"; app_password="$(random)"; public_password="$(random)"

cleanup() {
  status=$?
  if [ "${SMOKE_KEEP:-0}" != "1" ]; then
    docker compose down -v --remove-orphans > /dev/null 2>&1 || true
    rm -rf smoke/.env.smoke smoke/backups
  fi
  exit $status
}
trap cleanup EXIT

step() { printf '\n=== %s\n' "$1"; }

step "이미지 빌드"
write_env "pending-dummy" # 빌드에는 값이 필요 없지만 compose가 변수 존재를 요구한다
docker compose down -v --remove-orphans > /dev/null 2>&1 || true
docker compose build

step "이미지 검사(api: 비루트·셸 없음)"
test "$(docker image inspect pb-smoke-api --format '{{.Config.User}}')" = "1654"
if docker run --rm --entrypoint /bin/sh pb-smoke-api -c true > /dev/null 2>&1; then echo "api 이미지에 셸이 있다" >&2; exit 1; fi

step "관리자 비밀번호 해시 생성(이미지의 hash-password 명령)"
hash="$(printf '%s\n' "$admin_password" | docker run --rm -i pb-smoke-api hash-password | tail -n 1)"
test -n "$hash"
write_env "$hash"

step "기동"
docker compose up -d --wait
test "$(docker inspect pb-smoke-api-1 --format '{{.HostConfig.ReadonlyRootfs}}')" = "true"

step "스모크: 허용 IP"
SMOKE_ROLE=allowed docker compose run --rm -T smoke-allowed
step "스모크: 비허용 IP"
docker compose run --rm -T smoke-denied

step "DB 롤: 앱은 슈퍼유저가 아니고, 공개 롤은 읽기만 한다"
psql_as() { docker compose exec -T -e PGPASSWORD="$2" postgres psql -h 127.0.0.1 -U "$1" -d blog -v ON_ERROR_STOP=1 -tA -c "$3"; }
test "$(psql_as postgres "$pg_password" "select count(*) from pg_roles where rolname in ('blog_app','blog_public') and not rolsuper and not rolcreaterole and not rolcreatedb")" = "2"
psql_as blog_public "$public_password" 'select count(*) from "Posts"' > /dev/null
for sql in 'delete from "Posts"' 'set default_transaction_read_only = off; delete from "Posts"' 'create table smoke_t(i int)' 'select * from "AdminState"' 'select * from "__EFMigrationsHistory"'; do
  if psql_as blog_public "$public_password" "$sql" > /dev/null 2>&1; then echo "blog_public이 해서는 안 되는 일을 했다: $sql" >&2; exit 1; fi
done
if psql_as blog_app "$app_password" "copy (select 1) to program 'true'" > /dev/null 2>&1; then echo "blog_app이 서버 프로그램을 실행했다" >&2; exit 1; fi

step "통과"
````

```bash
git add deploy .gitignore
git update-index --chmod=+x deploy/smoke/run.sh deploy/postgres-init/10-roles.sh
```

- [ ] **Step 7: 돌린다**

Run: `bash deploy/smoke/run.sh`
Expected: 마지막 줄 `=== 통과`, exit 0. 중간에 `ℹ pass 8`(허용 IP)·`ℹ pass 5`(비허용 IP). 첫 실행은 이미지 빌드로 수 분 걸린다. 끝나면 `docker ps -a --filter name=pb-smoke`가 비어 있고 `deploy/smoke/.env.smoke`가 없어야 한다.

- [ ] **Step 8: 사보타주 — 스모크가 실제로 무는지**(하나씩 바꾸고 `run.sh`가 **실패**하는지 본 뒤 되돌린다)

| 바꿔 볼 것 | 실패해야 하는 곳 |
|---|---|
| Caddyfile: `respond @denied 404` 줄 삭제 | 비허용 IP: "모든 경로가 본문 없는 404" |
| Caddyfile: `@backend path /api/* /attachments/*` → `/api/* /attachments*` | 허용 IP: "SPA 화면 주소는 index.html" + `caddyfile.test.ts` |
| Caddyfile: 관리 사이트의 `header { … }` 보안 헤더 블록을 `@backend` 처리 **앞으로** 이동 | 허용 IP: "API 응답의 CSP는 백엔드 값" 또는 첨부 CSP |
| Caddyfile: `max_size 11MiB` → `5MiB` | 허용 IP: "10MiB 바로 아래는 통과해야 한다" |
| compose: api의 `Proxy__TrustedIp`를 `172.30.0.11`로 | 허용 IP 쪽이 403으로 무너지거나, 비허용 IP의 "직접 붙어도 위조한 X-Forwarded-For는 통하지 않는다" |
| compose: `ConnectionStrings__Public`의 사용자를 `blog_app`으로 | api가 시작을 거부(기동 단계에서 실패) |
| `10-roles.sh`: `blog_app`에 `SUPERUSER` | "DB 롤" 단계 |

- [ ] **Step 9: 커밋**

```bash
git commit -F <메시지 파일>   # 제목 예: "추가: 배포 compose와 허용·비허용 IP에서 찌르는 스택 스모크"
```

---

### Task 4: 백업·복원과 운영 문서

**Files:**
- Create: `deploy/backup.sh`, `deploy/restore.sh`, `deploy/OPERATIONS.md`
- Modify: `deploy/smoke/run.sh`(복원 리허설 단계)

**Interfaces:**
- Consumes: Task 3의 `tools` 서비스(사용자 1654, 네트워크 없음, `attachments` 볼륨), `smoke.test.mjs`의 `seed`·`verify-restore` 모드, `run.sh`가 내보내는 `COMPOSE_*` 환경(스크립트는 그냥 `docker compose`를 부른다 — 운영에서는 `deploy/.env`, 스모크에서는 그 환경변수가 대상을 정한다).
- Produces: `backup.sh [루트]` → 마지막 줄에 만든 디렉터리 경로. `restore.sh --yes <디렉터리>`.

- [ ] **Step 1: `deploy/backup.sh`**

````bash
#!/usr/bin/env bash
# DB 덤프와 첨부 파일을 한 디렉터리에 백업한다. 서비스는 멈추지 않는다.
# 사용법: ./backup.sh [백업 루트(기본 ./backups)]  → 만든 디렉터리 경로를 마지막 줄에 출력한다.
#
# 순서가 곧 일관성이다: DB를 먼저 덤프하고 파일을 나중에 묶는다. 첨부는 내용 주소 파일이라 덮어써지지 않으므로,
# 그 사이에 올라온 파일은 "행 없는 파일"(복원 뒤 청소 잡이 지운다)이 될 뿐이고 "파일 없는 행"은 그 사이에 첨부를 지웠을 때만 생긴다.
# dpkeys(세션 암호화 키)와 caddy_data(인증서)는 백업하지 않는다 — 복원하면 다시 로그인하고 인증서는 다시 발급된다.
set -euo pipefail
export MSYS_NO_PATHCONV=1 # Windows Git Bash에서 /data 같은 컨테이너 경로가 바뀌지 않게 한다(Linux에서는 영향 없음)
cd "$(dirname "$0")"
umask 077

root="${1:-backups}"
dest="$root/$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p "$dest"

docker compose exec -T postgres pg_dump -U postgres -Fc blog > "$dest/blog.dump"
docker compose --profile tools run --rm -T --no-deps tools 'tar -C /data/attachments --exclude=./.tmp -cf - .' > "$dest/attachments.tar"

# 읽을 수 있는 백업인지 그 자리에서 확인한다(빈 파일·잘린 파일을 백업이라고 믿지 않는다).
docker compose exec -T postgres pg_restore -l < "$dest/blog.dump" > /dev/null
tar -tf "$dest/attachments.tar" > /dev/null
(cd "$dest" && sha256sum blog.dump attachments.tar > SHA256SUMS)

echo "$dest"
````

- [ ] **Step 2: `deploy/restore.sh`**

````bash
#!/usr/bin/env bash
# backup.sh가 만든 디렉터리에서 DB와 첨부를 되돌린다. 지금 있는 DB 내용과 첨부 파일을 전부 지우고 덮어쓴다.
# 사용법: ./restore.sh --yes <백업 디렉터리>
# 새 서버(빈 볼륨)에서도 그대로 쓴다: postgres 초기화 스크립트가 롤과 빈 DB를 만들고, 이 스크립트가 내용을 채운다.
set -euo pipefail
export MSYS_NO_PATHCONV=1
cd "$(dirname "$0")"

if [ "${1:-}" != "--yes" ] || [ -z "${2:-}" ]; then
  echo "사용법: $0 --yes <백업 디렉터리>   (현재 DB와 첨부를 전부 덮어쓴다)" >&2
  exit 2
fi
src="$(cd "$2" && pwd)"
(cd "$src" && sha256sum -c SHA256SUMS)

# 쓰는 쪽을 먼저 멈춘다. 없는 서비스를 멈추는 것은 오류가 아니다.
docker compose stop caddy api
docker compose up -d --wait postgres
docker compose exec -T postgres pg_restore -U postgres -d blog --clean --if-exists --single-transaction < "$src/blog.dump"

# api 컨테이너를 "만들기만" 한다: 빈 attachments 볼륨은 이때 이미지의 /data/attachments(소유자 1654, 0700)로 초기화된다.
# 이 단계를 건너뛰면 tools(1654)가 root 소유의 새 볼륨에 쓰지 못한다.
docker compose up --no-start api
docker compose --profile tools run --rm -T --no-deps tools 'find /data/attachments -mindepth 1 -delete && tar -C /data/attachments -xf -' < "$src/attachments.tar"

docker compose up -d --wait
echo "복원 완료: $src"
````

- [ ] **Step 3: `run.sh`의 `step "통과"` 바로 앞에 복원 리허설을 끼운다**

````bash
step "복원 리허설: 글·첨부 생성 → 백업 → 볼륨 삭제 → 복원 → 확인"
SMOKE_ROLE=seed docker compose run --rm -T smoke-allowed
backup_dir="$(./backup.sh smoke/backups | tail -n 1)"
docker compose down -v --remove-orphans
./restore.sh --yes "$backup_dir"
SMOKE_ROLE=verify-restore docker compose run --rm -T smoke-allowed
SMOKE_ROLE=allowed docker compose run --rm -T smoke-allowed # 복원된 스택에서도 전 과정이 돈다(새 dpkeys·권한 재부여 포함)
````

```bash
git add deploy && git update-index --chmod=+x deploy/backup.sh deploy/restore.sh
```

- [ ] **Step 4: 돌린다**

Run: `bash deploy/smoke/run.sh`
Expected: `=== 복원 리허설…` 아래에 `ℹ pass 1`(seed) → `복원 완료: …` → `ℹ pass 1`(verify-restore) → `ℹ pass 8`(복원된 스택에서 전 과정) → `=== 통과`.

- [ ] **Step 5: 사보타주**(각각 되돌린다)

| 바꿔 볼 것 | 기대 |
|---|---|
| `restore.sh`에서 `docker compose up --no-start api` 줄 삭제 | tar 풀기가 권한 오류로 실패(S14) |
| `backup.sh`의 tar 줄을 `: > "$dest/attachments.tar"`로 | 백업 단계의 `tar -tf` 검증은 빈 tar를 통과시킬 수 있다 — 그렇다면 `verify-restore`가 첨부 404로 실패해야 한다. 어느 쪽에서든 **`run.sh`는 실패**해야 한다 |
| `restore.sh --yes` 없이 호출 | 아무것도 하지 않고 exit 2 |
| 백업 뒤 `blog.dump`에 1바이트 덧붙이기 | `sha256sum -c`에서 실패 |

- [ ] **Step 6: `deploy/OPERATIONS.md`**

````markdown
# 운영 절차 (PortfolioBlog)

이 문서의 명령은 전부 서버의 `deploy/` 디렉터리에서 실행한다. 구조와 그 이유는 `plan/tech_blog_0920.md` 3.10절, 이 구성을 만든 과정과 측정 결과는 `plan/tech_blog_4_report_*.md`에 있다.

```
인터넷 ──▶ caddy (80·443, TLS 종단, 고정 IP 172.30.0.2)
              ├─ 공개 도메인      ──▶ api:8080   (/api* 는 Caddy가 404)
              └─ 관리 서브도메인  ──▶ 허용 IP만: /api/*·/attachments/* → api:8080, 그 밖은 SPA 정적 파일
           api (포트 미공개, 비루트·읽기 전용 루트 FS) ──▶ postgres (포트 미공개, 내부 전용 네트워크)
볼륨: pgdata · attachments · dpkeys · caddy_data · caddy_config
```

## 1. 최초 배포

준비물: Linux 서버(고정 공인 IP), Docker Engine + compose 플러그인, 공개·관리 도메인의 DNS 레코드(이 서버를 가리켜야 인증서가 발급된다), 방화벽에서 TCP 80·443만 개방.

```bash
git clone <저장소> && cd <저장소>/deploy
cp .env.example .env && chmod 600 .env
$EDITOR .env                                   # 도메인·허용 CIDR·비밀번호 셋. 설명은 파일 안에 있다
docker compose build api
docker run --rm -it portfolioblog-api hash-password   # 관리자 비밀번호 입력(화면에 보이지 않음) → 출력된 해시를 .env의 ADMIN_PASSWORD_HASH에
docker compose up -d --build
docker compose ps                              # api·postgres가 healthy, caddy가 running
```

앱은 시작할 때 스스로 한다: 설정 검증(틀리면 어떤 키가 문제인지 말하고 종료) → DB 마이그레이션 → 공개 조회 롤(`blog_public`)의 권한을 허용 테이블의 `SELECT`로 다시 맞춤.

배포 전에 같은 구성을 로컬이나 CI에서 검증하려면 `deploy/smoke/run.sh`를 돌린다(운영과 같은 이미지·Caddyfile·compose를 `*.localhost` 도메인으로 띄워 접근 통제·헤더·한도·백업 복원까지 본다).

## 2. 배포 직후 반드시 확인할 것

| 확인 | 방법 | 기대 |
|---|---|---|
| 공개 사이트 | 브라우저로 `https://<공개 도메인>/` | 글 목록, 자물쇠(유효한 인증서) |
| 관리 표면이 밖에서 안 보인다 | **허용 목록 밖의 회선**(휴대폰 테더링 등)에서 `curl -s -o /dev/null -w '%{http_code}\n' https://<관리 도메인>/` 와 `/api/auth/me`, `/login` | 전부 `404` |
| 공개 도메인에 관리 API가 없다 | `curl -s -o /dev/null -w '%{http_code}\n' https://<공개 도메인>/api/posts` | `404` |
| **원본 IP가 보인다** | 허용 회선에서 관리 사이트를 한 번 연 뒤 `docker compose logs caddy \| grep '"remote_ip"' \| tail -n 3` | `remote_ip`가 **내 공인 IP**다. `172.30.0.1`(Docker 게이트웨이)이면 3절을 본다 |
| 로그인 | `https://<관리 도메인>/`에서 로그인, 글 하나 저장, 공개 사이트에서 확인 | 저장 즉시 공개 |
| 헬스 | `docker inspect --format '{{.State.Health.Status}}' portfolioblog-api-1` | `healthy` |

## 3. 원본 IP가 게이트웨이 주소로 보일 때

관리 표면의 IP 허용 목록은 Caddy가 보는 `remote_ip`와 앱이 Caddy에게서 받는 `X-Forwarded-For`에 달려 있다. Docker가 연결을 사용자 공간 프록시로 중계하면 모든 클라이언트가 `172.30.0.1`로 보여 **허용 목록이 무의미해지거나(그 주소를 허용했다면 전 세계 허용) 작성자가 잠긴다.**

- IPv4만 그렇다면: Docker 데몬이 iptables를 관리하고 있는지 확인한다(`/etc/docker/daemon.json`에 `"iptables": false`가 있으면 안 된다).
- IPv6 접속만 그렇다면: Docker의 IPv6 NAT가 꺼진 것이다. 가장 간단한 처방은 관리 도메인에 **AAAA 레코드를 두지 않는 것**이다. IPv6가 필요하면 Docker의 `ip6tables`를 켜고 다시 확인한다.
- `ADMIN_ALLOWED_CIDRS`에 `172.30.0.0/24`나 게이트웨이 주소를 넣어 "해결"하지 않는다. 그것은 허용 목록을 끄는 것과 같다.
- 앞단에 CDN·로드밸런서를 두면 이 구성은 그대로 쓸 수 없다(`trusted_proxies` + `client_ip` 재설계가 필요하다 — 스펙 3.10).

## 4. 업데이트(재배포)

```bash
./backup.sh                       # 마이그레이션은 자동으로 되돌려지지 않는다. 먼저 백업한다
git pull --ff-only
docker compose up -d --build      # 이미지를 다시 빌드하고 바뀐 컨테이너만 교체한다
docker compose ps && docker compose logs --tail 50 api
```

Caddyfile과 관리 SPA는 caddy 이미지에 구워져 있다 — 고치면 위 명령이 이미지를 다시 만든다. SPA 빌드는 배포되는 의존성에 high 이상 취약점이 있으면 실패한다. 먼저 의존성을 올리는 것이 정답이고, 무관한 긴급 수정을 당장 내보내야 할 때만 `docker compose build --build-arg NPM_AUDIT=off caddy`를 쓴다.

되돌리기: `git checkout <이전 커밋> && docker compose up -d --build`. 새 버전이 DB 마이그레이션을 적용했다면 코드만 되돌려서는 안 맞을 수 있다 — 그때는 5절의 복원을 쓴다.

## 5. 백업과 복원

```bash
./backup.sh [백업 루트]            # 기본 ./backups/<UTC 시각>/ 에 blog.dump · attachments.tar · SHA256SUMS
./restore.sh --yes <백업 디렉터리>  # 현재 DB와 첨부를 전부 지우고 덮어쓴다
```

- 백업은 서비스를 멈추지 않는다. DB를 먼저 덤프하고 첨부를 나중에 묶는다 — 첨부는 내용 주소 파일이라 덮어써지지 않으므로, 그 사이에 올라온 파일은 복원 뒤 청소 잡이 지우는 "행 없는 파일"이 될 뿐이다.
- **백업하지 않는 것:** `dpkeys`(세션 암호화 키 — 복원하면 다시 로그인하면 된다), `caddy_data`(인증서 — 다시 발급된다), `.env`(비밀값 — 비밀번호 관리자 등 별도의 안전한 곳에 보관한다. 없으면 복원한 DB에 앱이 접속하지 못한다).
- 백업 디렉터리에는 글 전체와 첨부가 평문으로 들어 있다. 권한은 700/600으로 만들어진다. **서버 밖으로도 복사한다**(서버가 죽으면 서버 안의 백업도 죽는다).
- 매일 새벽 백업 예: `crontab -e` → `17 3 * * * cd /srv/blog/deploy && ./backup.sh /srv/blog-backups >> /var/log/blog-backup.log 2>&1` (오래된 백업 정리는 `find /srv/blog-backups -maxdepth 1 -mtime +30 -exec rm -rf {} +`).
- 새 서버로 옮길 때: 1절대로 `.env`까지 준비하고(같은 DB 비밀번호 셋) `docker compose build` 뒤, `up` 대신 `./restore.sh --yes <백업>`을 실행한다. 빈 볼륨에서 postgres가 롤과 빈 DB를 만들고 스크립트가 내용을 채운 뒤 전체를 띄운다.
- **복원 리허설:** `deploy/smoke/run.sh`가 매번 한다(글·첨부 생성 → 백업 → 볼륨 삭제 → 복원 → 바이트 단위 확인). 운영 백업 파일로 직접 해 보려면 다른 기계에서 위 "새 서버" 절차를 따른다.

## 6. 관리자 비밀번호 변경

```bash
docker run --rm -it portfolioblog-api hash-password     # 새 해시
$EDITOR .env                                            # ADMIN_PASSWORD_HASH 교체
docker compose up -d api
```

해시가 바뀌면 기존 세션은 전부 자동으로 무효가 된다(쿠키에 든 해시 지문이 맞지 않는다).

## 7. 세션 긴급 폐기

쿠키가 유출됐다고 의심되면, 빠른 순서대로:

1. 관리 화면에서 **로그아웃** — 모든 기기의 세션이 함께 끊긴다(세션 epoch 증가).
2. 화면에 들어갈 수 없으면: `docker compose exec -T postgres psql -U postgres -d blog -c 'UPDATE "AdminState" SET "SessionEpoch" = "SessionEpoch" + 1'`
3. 비밀번호까지 새 나갔다면 6절로 비밀번호를 바꾼다(세션도 함께 폐기된다).

## 8. 허용 IP 변경

`.env`의 `ADMIN_ALLOWED_CIDRS`를 고치고 `docker compose up -d caddy api` — **두 컨테이너 모두** 그 값을 읽는다. 잘못된 CIDR이면 api가 시작을 거부한다(로그에 `Admin:AllowedCidrs`). 집 회선의 IP가 바뀌어 잠겼다면 서버에 SSH로 들어가 같은 절차를 밟는다.

## 9. DB 비밀번호 변경

postgres는 빈 데이터 볼륨에서 처음 뜰 때만 `.env`의 값으로 롤을 만든다. 나중에 바꾸려면 DB 안에서 먼저 바꾼다.

```bash
docker compose exec -T postgres psql -U postgres -c "ALTER ROLE blog_app PASSWORD '<새 값>'"   # blog_public·postgres도 같은 방식
$EDITOR .env                       # 같은 값으로
docker compose up -d api
```

## 10. 이미지 버전 올리기

태그는 정확한 버전으로 고정돼 있다. 올릴 곳:

| 이미지 | 파일 |
|---|---|
| `mcr.microsoft.com/dotnet/sdk`, `mcr.microsoft.com/dotnet/aspnet:<버전>-noble-chiseled-extra` | `PortfolioBlog.Api/Dockerfile` |
| `node`, `caddy` | `PortfolioBlog.Web/Dockerfile` |
| `postgres`(서비스와 `tools` 두 곳) | `deploy/docker-compose.yml` |
| `node`(스모크 클라이언트) | `deploy/docker-compose.smoke.yml` |

올린 뒤에는 `deploy/smoke/run.sh`를 통과시킨다(CI의 `deploy-smoke` 잡이 같은 것을 돌린다). PostgreSQL의 **주 버전**(17 → 18)은 데이터 디렉터리 형식이 달라 태그만 바꾸면 뜨지 않는다 — 백업 → 새 버전으로 빈 볼륨에서 복원한다.

## 11. 로그

- `docker compose logs -f api|caddy|postgres`. 컨테이너마다 10MB × 5개로 돌려 쓴다(그 이상은 사라진다 — 보존이 필요하면 외부 수집기를 붙인다).
- Caddy 액세스 로그는 `Cookie`·`Set-Cookie`·`Authorization` 값을 `REDACTED`로 남긴다(기본 동작, 실측). 앱은 비밀번호·쿠키·요청 본문을 기록하지 않는다. 로그인 실패는 IP만 남는다.
- 과부하(503) 한 건은 스택 포함 약 50줄이다. 검색어·본문은 남지 않는다.

## 12. 문제 해결

| 증상 | 볼 곳 |
|---|---|
| api가 `unhealthy`/재시작 반복 | `docker compose logs --tail 100 api` — 설정 오류면 첫 예외 메시지에 설정 키가 있다. 헬스체크 자체의 출력은 `docker inspect --format '{{json .State.Health}}' portfolioblog-api-1` |
| 인증서가 안 나온다 | `docker compose logs caddy \| grep -i acme` — DNS가 이 서버를 가리키는지, 80·443이 열려 있는지 |
| 관리 사이트가 허용 회선에서도 404 | 2·3절의 원본 IP 확인. `.env`의 CIDR에 내 현재 공인 IP가 있는지 |
| 로그인은 되는데 저장이 403 | `.env`의 `ADMIN_ORIGIN`이 브라우저 주소창의 출처와 글자 그대로 같은지(스킴·호스트, 포트는 443이면 생략) |
| 업로드가 413 | 이미지는 10MB까지다. Caddy의 상한(11MiB)은 그보다 크게 잡혀 있다 |
| 컨테이너를 다시 만들 때 `Address already in use` | 고정 IP(172.30.0.2)를 다른 컨테이너가 잡았다 — `docker compose down && docker compose up -d`(볼륨은 지워지지 않는다) |
````

문서의 명령 가운데 이 기계에서 확인할 수 있는 것은 실제로 실행해 본다(`SMOKE_KEEP=1 bash deploy/smoke/run.sh`로 스택을 남긴 뒤 `COMPOSE_*` 환경을 같은 값으로 내보내고): 7절의 `UPDATE "AdminState"…`가 실행되고 그 뒤 기존 세션 쿠키가 401이 되는지, 11절의 로그에 `REDACTED`가 보이는지, 12절의 `docker inspect … .State.Health`. 확인이 끝나면 `docker compose down -v`와 `rm -rf deploy/smoke/.env.smoke deploy/smoke/backups`.

- [ ] **Step 7: 커밋**

```bash
git commit -F <메시지 파일>   # 제목 예: "추가: 무중단 백업·복원 스크립트와 매번 도는 복원 리허설, 운영 절차 문서"
```

---

### Task 5: 스택 대상 브라우저 E2E와 CI

**Files:**
- Create: `PortfolioBlog.Web/playwright.stack.config.ts`
- Modify: `PortfolioBlog.Web/e2e/admin.spec.ts`, `deploy/smoke/run.sh`, `.github/workflows/ci.yml`, `PortfolioBlog.Web/tsconfig.node.json`(include에 새 설정 파일)

**Interfaces:**
- Consumes: `run.sh`의 `$admin_password`·`$deploy`, 스모크 스택의 `https://admin.blog.localhost:8443`, 스모크 허용 목록의 `172.30.0.1/32`(호스트 브라우저가 그 주소로 보인다 — S6).
- Produces: 환경변수 `E2E_SPA_ORIGIN`·`E2E_ADMIN_PASSWORD`, `SMOKE_E2E=1`.

- [ ] **Step 1: 스펙을 두 구성 모두에서 돌 수 있게 고친다**

````diff
--- a/PortfolioBlog.Web/e2e/admin.spec.ts
+++ b/PortfolioBlog.Web/e2e/admin.spec.ts
@@ -4,9 +4,10 @@
 // production 빌드 + 실제 보안 헤더 + 실제 백엔드. 단위 테스트가 볼 수 없는 것만 본다:
 // 진짜 CodeMirror, 진짜 CSP, 진짜 쿠키·Origin 검사, 진짜 sandbox iframe.
 const PASSWORD = process.env.E2E_ADMIN_PASSWORD!
-// playwright.config.ts의 SPA_ORIGIN과 같은 값. 여기서 다시 import하지 않는 이유: 그 모듈은 로드 시 .e2e/env.json을
+// 기본값은 playwright.config.ts의 SPA_ORIGIN과 같은 값. 여기서 다시 import하지 않는 이유: 그 모듈은 로드 시 .e2e/env.json을
 // 요구하고 부수효과(스크래치 첨부 디렉터리 생성)가 있다 — 이 스펙 파일은 config의 부수효과에 기대지 않는다.
-const SPA_ORIGIN = 'https://localhost:4173'
+// E2E_SPA_ORIGIN: 컨테이너 스택을 대상으로 돌 때(playwright.stack.config.ts) deploy/smoke/run.sh가 넣는다.
+const SPA_ORIGIN = process.env.E2E_SPA_ORIGIN ?? 'https://localhost:4173'
 // 1x1 PNG
 const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64')
 
@@ -36,22 +37,30 @@
   await page.getByRole('button', { name: '로그인' }).click()
 }
 
-test('문서 응답에 배포될 보안 헤더가 붙는다', async ({ request }) => {
-  const response = await request.get('/')
-  const csp = response.headers()['content-security-policy']
+// 아래 두 테스트는 Playwright의 Node 쪽 요청(request 픽스처)이 아니라 브라우저를 쓴다: 컨테이너 스택을 대상으로 돌 때
+// (playwright.stack.config.ts) 주소가 *.localhost인데, 그 이름은 브라우저만 루프백으로 풀고 Node(getaddrinfo)는 OS에 따라 풀지 못한다.
+test('문서 응답에 배포될 보안 헤더가 붙는다', async ({ page }) => {
+  const response = (await page.goto('/'))!
+  const headers = response.headers()
+  const csp = headers['content-security-policy']
   expect(csp).toContain("default-src 'none'")
   expect(csp).toContain("script-src 'self'")
   expect(csp).not.toContain("script-src 'self' 'unsafe")
   expect(csp).toContain("style-src-attr 'none'")
-  expect(response.headers()['x-content-type-options']).toBe('nosniff')
+  expect(headers['x-content-type-options']).toBe('nosniff')
   // 위 4개는 정본의 성질(뭐가 있고 뭐가 없어야 하는지)을 검사한다. 이 단언은 배포되는 값 자체가
   // admin-headers.ts의 ADMIN_CSP와 글자 그대로 같은지 본다 — 정본과 실제 응답이 갈라지면 여기서 걸린다.
   expect(csp).toBe(ADMIN_CSP)
 })
 
-test('세션이 없으면 API는 401이고, CSRF 헤더가 없으면 403이다(화면을 우회해도 서버가 막는다)', async ({ request }) => {
-  expect((await request.get('/api/posts', { headers: { 'X-Requested-With': 'XMLHttpRequest' } })).status()).toBe(401)
-  expect((await request.get('/api/posts')).status()).toBe(403)
+test('세션이 없으면 API는 401이고, CSRF 헤더가 없으면 403이다(화면을 우회해도 서버가 막는다)', async ({ page }) => {
+  await page.goto('/login')
+  const statuses = await page.evaluate(async () => {
+    const withHeader = await fetch('/api/posts', { headers: { 'X-Requested-With': 'XMLHttpRequest' } })
+    const withoutHeader = await fetch('/api/posts')
+    return [withHeader.status, withoutHeader.status]
+  })
+  expect(statuses).toEqual([401, 403])
 })
 
 // 오픈 리다이렉트 방지(src/lib/safeNext.ts)를 실제 브라우저 내비게이션으로 증명한다. 단위 테스트는 메모리 라우터(jsdom)로
````

- [ ] **Step 2: `PortfolioBlog.Web/playwright.stack.config.ts`**

````ts
import { defineConfig, devices } from '@playwright/test'

const origin = process.env.E2E_SPA_ORIGIN
if (!origin || !process.env.E2E_ADMIN_PASSWORD) throw new Error('E2E_SPA_ORIGIN과 E2E_ADMIN_PASSWORD가 필요합니다.')

export default defineConfig({
  testDir: 'e2e',
  timeout: 90_000,
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: process.env.CI ? [['github'], ['list']] : 'list',
  use: { baseURL: origin, ignoreHTTPSErrors: true, trace: 'retain-on-failure' },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
    { name: 'firefox', use: { ...devices['Desktop Firefox'] } },
  ],
})
````

`tsconfig.node.json`의 `include` 배열에 `"playwright.stack.config.ts"`를 더한다(타입 검사·린트 대상에 들어가게).

- [ ] **Step 3: 기존 E2E가 그대로 통과하는지 먼저 본다**

Run: `cd PortfolioBlog.Web && npm run lint && npm run typecheck && npm run e2e:prepare && npm run e2e; docker rm -f pb-e2e-pg`
Expected: **8 passed**(Chromium 4 + Firefox 4).

- [ ] **Step 4: `run.sh`의 `step "통과"` 바로 앞(복원 리허설 뒤)에 E2E 단계를 끼운다**

````bash
if [ "${SMOKE_E2E:-0}" = "1" ]; then
  step "브라우저 E2E(Chromium·Firefox): Caddy가 주는 실제 헤더 아래에서 SPA 전 과정"
  (cd "$deploy/../PortfolioBlog.Web" && E2E_SPA_ORIGIN=https://admin.blog.localhost:8443 E2E_ADMIN_PASSWORD="$admin_password" npx playwright test -c playwright.stack.config.ts)
fi
````

Run: `SMOKE_E2E=1 bash deploy/smoke/run.sh`
Expected: `8 passed` 뒤 `=== 통과`. 이 실행이 증명하는 것: Caddy가 주는 실제 헤더 아래에서 CodeMirror·미리보기 iframe·첨부가 CSP 위반 0건으로 돌고, SPA의 `/attachments` 화면이 새로고침에도 SPA로 남는다.

- [ ] **Step 5: CI 잡**

`.github/workflows/ci.yml`의 `web-e2e` 잡 뒤에:

```yaml
  deploy-smoke:
    runs-on: ubuntu-latest
    needs: [test, web]
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-node@v4
        with:
          node-version: '24'
          cache: npm
          cache-dependency-path: PortfolioBlog.Web/package-lock.json
      - name: Install web dependencies (Playwright)
        working-directory: PortfolioBlog.Web
        run: npm ci
      - name: Install browsers
        working-directory: PortfolioBlog.Web
        run: npx playwright install --with-deps chromium firefox
      # 운영과 같은 이미지·Caddyfile·compose를 띄워: 접근 통제(허용·비허용 IP), 헤더, 한도, DB 롤, 백업→삭제→복원, 브라우저 E2E.
      - name: Deployment stack smoke
        run: SMOKE_E2E=1 bash deploy/smoke/run.sh
      # trace의 비밀번호·쿠키는 이 실행을 위해 run.sh가 새로 만든 버려질 값이고, 스택은 러너의 루프백에만 바인딩된다.
      - uses: actions/upload-artifact@v4
        if: failure()
        with:
          name: deploy-smoke-traces
          path: PortfolioBlog.Web/test-results
          retention-days: 7
```

- [ ] **Step 6: 커밋**

```bash
git add PortfolioBlog.Web deploy/smoke/run.sh .github/workflows/ci.yml
git commit -F <메시지 파일>   # 제목 예: "테스트: Caddy가 서빙하는 실제 스택에서 브라우저 E2E를 돌리고 CI에 배포 스모크를 붙임"
```

**첫 Linux CI 실행은 게이트다.** 이 계획이 Linux에서 재지 못한 것: (1) 호스트 → 공개 포트 요청의 `remote_ip`가 Linux에서도 `172.30.0.1`인가(아니면 E2E의 로그인이 404로 깨진다 — 그때는 실패한 잡의 Caddy 로그에서 실제 주소를 읽어 스모크 허용 목록을 고친다. 운영 설정은 건드리지 않는다), (2) 러너의 Chromium·Firefox가 `*.localhost`를 루프백으로 푸는가.

---

### Task 6: 문서 — 스펙을 as-built로, README·CLAUDE.md·AGENTS.md

**Files:**
- Modify: `plan/tech_blog_0920.md`, `README.md`, `CLAUDE.md`, `AGENTS.md`

- [ ] **Step 1: 스펙 `plan/tech_blog_0920.md`**
  - 3.10: 파일 목록을 실제 것으로(`.env.example`의 변수 전체 — `PUBLIC_ORIGIN`·`ADMIN_ORIGIN`·`ACME_EMAIL`·`BLOG_APP_PASSWORD`·`BLOG_PUBLIC_PASSWORD` 포함, `postgres-init/`, `backup.sh`·`restore.sh`, `docker-compose.smoke.yml`·`smoke/`, 볼륨에 `caddy_config`, 이미지 `node:24`·chiseled). Caddyfile 발췌를 실제 파일의 구조로 바꾸고(지시문 전체를 옮기지 말고 `deploy/Caddyfile`을 가리킨다) 스파이크에서 확정된 사실을 적는다: `defer`로 `Server`·`Via` 제거, 헤더 블록은 백엔드 프록시 뒤, `/assets`의 없는 파일은 404, `request_body 11MiB`, `ip_range`로 고정 IP 보호, 네트워크 둘, 헬스체크는 `dotnet PortfolioBlog.Api.dll healthcheck`, 환경변수에 `ConnectionStrings__Public`.
  - 3.6: 관리 SPA 행에 "Caddy가 HSTS(`max-age=31536000; includeSubDomains`)와 `Cross-Origin-Opener-Policy: same-origin`을 더한다. 일치는 `caddyfile.test.ts`(정적)와 `deploy/smoke`(실제 응답)가 본다".
  - 3.7의 DB 행과 7절: "쓰기 권한 없는 DB 롤"이 **구현됐다**로 고친다(롤 셋, 앱이 시작마다 허용 테이블 5개만 부여, Development가 아니면 필수). 7절에서 그 항목을 지우고 "마이그레이션 전용 롤 분리"는 남긴다.
  - 5절 4단계 행, 6절 빌드 검증에 `bash deploy/smoke/run.sh`, 8절 Plan 4 행(`docs/superpowers/plans/2026-09-22-tech-blog-deploy.md` · 진행 중 — 완료 표기와 PR 번호는 컨트롤러가 병합 뒤에 넣는다).
- [ ] **Step 2: `README.md`** — "배포" 절: 구성 그림 한 장(OPERATIONS.md의 것), `cp .env.example .env` → 해시 → `docker compose up -d --build` 요약과 `deploy/OPERATIONS.md` 링크, "배포 구성 검증: `bash deploy/smoke/run.sh`(Docker 필요, `SMOKE_E2E=1`이면 브라우저까지)". 로드맵의 4단계 상태.
- [ ] **Step 3: `CLAUDE.md`와 `AGENTS.md`**(같은 문장으로 — 미러) — "구성" 절의 `deploy/` 항목에서 "(예정, 4단계)"를 지우고 실제 내용으로: compose(caddy·api·postgres + `tools`)·Caddyfile·`postgres-init`(DB 롤 셋)·`backup.sh`/`restore.sh`·`smoke/`(운영과 같은 이미지로 띄워 찌르는 스모크, CI `deploy-smoke`)·`OPERATIONS.md`. CI 잡 목록에 `deploy-smoke`. "새 이미지 태그·Caddyfile을 고치면 `bash deploy/smoke/run.sh`를 통과시킬 것" 한 줄.
- [ ] **Step 4:** `pwsh scripts/harness-audit.ps1` → PASS 8/8. 커밋(제목 예: "문서: 배포 구성을 스펙과 README에 as-built로 반영").

---

## 최종 검증(전 Task 완료 후, 컨트롤러)

```bash
dotnet build PortfolioBlog.slnx -c Release && dotnet test PortfolioBlog.slnx -c Release --no-build   # 618
(cd PortfolioBlog.Web && npm run lint && npm run typecheck && npm test && npm run build)             # Vitest 193
(cd PortfolioBlog.Web && npm run e2e:prepare && npm run e2e; docker rm -f pb-e2e-pg)                 # E2E 8
SMOKE_E2E=1 bash deploy/smoke/run.sh                                                                 # === 통과
pwsh scripts/harness-audit.ps1                                                                       # 8/8
```

최종 리뷰(가장 유능한 모델)는 `SMOKE_KEEP=1`로 남긴 스택을 **직접 공격**한다: 관리 사이트 우회(Host·SNI·경로 표기·메서드·HTTP/1.0·절대 URI 요청 줄), 공개 사이트에서 상태 변경 시도, 컨테이너 탈출 관점의 compose 설정(쓰기 가능한 경로, 권한, 네트워크 도달성), 백업 산출물의 권한과 내용, 로그에 남는 값, `.env.example`·문서의 실제 비밀값 유무.

---

## 구현 중 발견해 고친 계획 결함 (2026-09-22, Task 1~3 실행·리뷰에서)

이 계획의 코드 블록은 저장소 밖 복사본에서 실제로 빌드·기동해 본 것이지만, **리뷰가 그 위에서 더 찾아냈다.** 아래 항목의 코드 블록을 그대로 다시 쓰지 말 것 — 브랜치 `feature/blog-deploy`의 커밋에 이미 반영됐거나(Task 1), 재개하면 바로 고칠 목록에 있다(Task 2·3). 재개 절차와 각 항목의 판정은 **`plan/resume_guide_0921.md` 3절**에 있다.

**Task 1(반영 완료 — `7efab39`·`88310ee`)**
- `BuildStatements`의 `REVOKE ALL … FROM {role}`은 **`PUBLIC` 의사 롤에 준 권한을 회수하지 못한다**(실측: `GRANT … TO PUBLIC` 뒤 공개 롤이 `AdminState`를 읽고 썼다). → `FROM PUBLIC`도 회수한다.
- `REVOKE … ON ALL TABLES IN SCHEMA public`은 **관리 롤이 비 superuser인 운영 형태에서 남의 소유 테이블이 하나만 있어도 42501로 기동을 막는다**(운영의 `blog_app`이 정확히 그 형태다). → 회수 대상을 `pg_tables`에서 얻은 **자기 소유 테이블**로 한정한다(`BuildStatements(role, ownedTables)`).
- 테스트 하네스가 superuser 연결을 쓰기 때문에 위 두 결함이 초록으로 지나간다 → 픽스처에 비 superuser 소유자 롤을 두고 그 롤로 `Apply`하는 통합 테스트가 있어야 진짜 회귀 가드가 된다.
- `PostgresContainerFixture`·`DataServiceCollectionExtensions`의 diff 삽입 위치가 남의 XML 문서 블록 안쪽이라 문서가 어긋난다. 롤 이름 정규식은 `$`가 아니라 `\z`. 헬스체크는 스킴이 `http`/`https`이고 호스트가 있을 때만 보낸다.

**Task 2(재개하면 고칠 것)**
- `caddyfile.test.ts`의 위치 단언(인덱스·탭 깊이)은 **보안 헤더 블록을 관리 `route` 끝으로 옮기는 변경을 잡지 못한다** — 5/5 통과하면서 실제 응답의 헤더 7개가 사라진다(실측).
- 폴백 없는 사이트 구성이라 **두 도메인 밖 Host에 `Server: Caddy`가 남는다**(:443 유효 SNI + 미매칭 Host → 200 빈 응답).
- `handle_errors`가 만드는 502·413에는 보안 헤더가 붙지 않는다. `@dot`(점 파일 404)이 `/.well-known/*`까지 막는다.
- 판정 유지(조치 없음): `caddyfile.test.ts`는 `.gitattributes`의 `eol=lf` 밖이다(TS 파일이라 줄 끝과 무관 — 다시 제기하지 말 것). 스모크 클라이언트의 keep-alive 에이전트가 간헐적으로 이상 응답을 보이지만 서버 측 desync는 없음을 생소켓으로 확인했다.

**Task 3(재개하면 고칠 것)**
- `edge` 네트워크가 `internal`이 아니라 **api 컨테이너가 인터넷으로 나갈 수 있다**(실측). → caddy만 외부에 두는 3망 구성.
- `run.sh`의 DB 롤 검사가 `psql -h 127.0.0.1`이라 **pg_hba의 `trust` 줄을 타 비밀번호를 검증하지 않는다**(틀린 비밀번호로 superuser 접속 성공). 부정 검사도 "0 아닌 종료 코드 = 통과"라 오타·연결 실패까지 통과한다. → `-h postgres` + SQLSTATE·메시지 판정.
- caddy 컨테이너가 root로 돈다. 첨부는 내용 주소라 재업로드가 200이므로 스모크의 `201` 단언은 비멱등이다.

**계획이 맞았던 것(실측으로 확인, 되돌리지 말 것)**
- `ip_range: 172.30.0.128/25`가 caddy의 고정 IP를 지킨다(없으면 동적 컨테이너가 `.2`를 가져간다).
- **ACME HTTP-01은 명시 `http://` 사이트 블록·IP 허용 목록과 공존한다** — 로컬 ACME CA를 허용 목록 밖에 두고 실제 발급까지 확인했다.
- `db` 네트워크 `internal`, api의 호스트 포트 없음, `read_only`·`cap_drop: ALL`·`no-new-privileges`, 로그 회전, `trap EXIT`의 비밀 파일 삭제, `:'var'` 인용의 안전성, publish 출력에 EF 디자인 타임 어셈블리 없음.
