# 용어집

<!-- doc-harness:section id="one-liner" hash="7cfef395ae60b89a600725ce57e3c6eb0b5a5f071ff082cc82ca20b1c7218f4a" -->
PortfolioBlog 고유 용어 47개를 코드 이름·경로와 연결한 용어집이다(관리 SPA, 관리·공개 API, 렌더링, 첨부 저장소, 배포 범위).
<!-- /doc-harness:section -->

<!-- doc-harness:section id="summary" hash="5d7e2725054ee981bd9f0d068b405ab328cdfe4a6d31829a27f88d8f9e805aab" -->
## 한 줄 요약

## 한 줄 요약

이 프로젝트 고유의 용어 47개를 코드 이름과 경로에 연결했다. 범위는 관리 SPA, 관리·공개 API, 마크다운 렌더링, 첨부 저장소, 배포 구성이다. 항목은 워크스페이스 산출물(기능 F001~F029, 컴포넌트 목록)에서 가져왔고, SPA 파일 경로 3건과 ApiBodyLimitMiddleware 한도만 코드로 다시 확인했다.
<!-- /doc-harness:section -->

<!-- doc-harness:section id="terms" hash="53998ed84e771866dbd94d35aacf13d3fe1e0f27e3b1a9732a4086aacba6c4ee" -->
## 용어

## 용어

용어는 가나다/알파벳 순이다. 관련 문서 열은 해당 개념을 다루는 생성 문서다.

| 용어 | 뜻 | 코드 이름/경로 | 관련 문서 |
|---|---|---|---|
| AdminCredential | 관리자 비밀번호 해시 검증 대상. 로그인 시 비밀번호를 이것으로 검증한다. | AdminCredential (PortfolioBlog.Api/Infrastructure/Access) | [F001](features/F001_ADMIN_AUTH_SESSION.md) |
| AdminOrigin | 안전하지 않은 메서드의 Origin 검사 기준이 되는 관리 사이트 origin. | AdminSurfaceMiddleware (PortfolioBlog.Api/Infrastructure/Access) | [F018](features/F018_ADMIN_SURFACE_ACCESS_CONTROL.md) |
| AdminSession 쿠키 | 관리자 세션 쿠키 `__Host-AdminSession`(HttpOnly, Secure, SameSite Strict, 슬라이딩 만료 없음). | AuthServiceCollectionExtensions (PortfolioBlog.Api/Infrastructure/Access) | [F001](features/F001_ADMIN_AUTH_SESSION.md), [13_SECURITY](13_SECURITY.md) |
| AdminState / SessionEpoch | 관리자 상태 엔티티와 세션 무효화 기준 값. 요청마다 조회해 세션을 재검증한다. | AdminStates (AppDbContext DbSet), SessionValidator | [F001](features/F001_ADMIN_AUTH_SESSION.md), [07_DATA_MODEL](07_DATA_MODEL.md) |
| AdminSurfaceMiddleware | /api 요청을 본문 읽기 전에 호스트·IP·CSRF 헤더·Origin 순으로 거르는 미들웨어. | PortfolioBlog.Api/Infrastructure/Access | [F018](features/F018_ADMIN_SURFACE_ACCESS_CONTROL.md) |
| ApiBodyLimitMiddleware | 인가 뒤에 API 요청 본문을 256KB(JsonLimitBytes = 262,144바이트)로 제한하는 미들웨어. 선언 길이 초과 또는 읽는 도중 초과 시 413을 낸다. | PortfolioBlog.Api/Infrastructure/Web/ApiBodyLimitMiddleware.cs | [F020](features/F020_WEB_PIPELINE_PROTECTION.md) |
| ApiError | 관리 SPA가 실패 응답을 바꾸는 오류 클래스. 네트워크 오류는 status 0. client.ts는 이를 던지기만 한다. | PortfolioBlog.Web/src/api/errors.ts | [F029](features/F029_ADMIN_SPA_SHELL.md) |
| AppDbContext | 관리용 EF Core 컨텍스트(blog_app 롤). 마이그레이션 주체. | PortfolioBlog.Api/Infrastructure/Data/AppDbContext.cs | [07_DATA_MODEL](07_DATA_MODEL.md) |
| Attachment | 첨부 이미지 엔티티. | AppDbContext.Attachments | [F009](features/F009_ADMIN_ATTACHMENT_MANAGEMENT.md) |
| AttachmentJanitor | 6시간마다 고아 첨부 파일과 오래된 임시 파일을 정리하는 BackgroundService. | PortfolioBlog.Api/Infrastructure/Storage/AttachmentJanitor.cs | [F024](features/F024_ATTACHMENT_JANITOR.md) |
| AttachmentLock | sha256 키 DB 세션 잠금으로 업로드·삭제·청소를 직렬화(lock_timeout 10초, 55P03). | PortfolioBlog.Api/Infrastructure/Storage | [F009](features/F009_ADMIN_ATTACHMENT_MANAGEMENT.md) |
| blog_app / blog_public | 관리용·공개 읽기용 PostgreSQL 롤. | deploy/postgres-init/10-roles.sh, PublicRoleGrants | [F021](features/F021_STARTUP_BOOTSTRAP.md), [F026](features/F026_COMPOSE_DEPLOYMENT.md) |
| Caddy 에지 | TLS/ACME, 공개·관리 도메인 분기, 관리 도메인 IP 허용 목록, SPA 서빙을 맡는 리버스 프록시. | deploy/Caddyfile | [F025](features/F025_EDGE_PROXY_TLS.md) |
| ConflictPanel | 409 발생 시 서버본과 내 본문을 나란히 보여 주는 SPA 화면. | PortfolioBlog.Web (편집 화면) | [F006](features/F006_POST_EDIT_CONFLICT.md) |
| 내용 주소(content-addressed) 저장 | 첨부를 SHA-256 해시 기준으로 저장하는 방식. | FileSystemAttachmentStore | [F009](features/F009_ADMIN_ATTACHMENT_MANAGEMENT.md) |
| 낙관적 동시성(version/xmin) | PUT 요청의 version이 현재 xmin과 다르면 409. | PUT /api/posts/{id} | [F006](features/F006_POST_EDIT_CONFLICT.md) |
| 동시 실행 제한기 / 고정 창 제한기 | 속도 제한 체인의 두 종류. 동시 실행 제한기가 고정 1분 창보다 앞에 온다. | RateLimitingExtensions | [F019](features/F019_RATE_LIMITING.md) |
| 임시본(draft) | 편집 중 글 필드를 localStorage에 글별로 보관한 값(baseVersion·savedAt 포함). | PortfolioBlog.Web (편집 화면) | [F005](features/F005_LOCAL_DRAFT_RECOVERY.md) |
| healthcheck | API를 `healthcheck` 인자로 실행해 로컬 /health를 3초 타임아웃으로 호출하는 CLI. | HealthCheckCommand | [F022](features/F022_HEALTH_CHECK.md) |
| hash-password | 관리자 비밀번호 해시를 출력하는 CLI 인자. 값은 Admin:PasswordHash에 넣는다. | HashPasswordCommand | [F023](features/F023_HASH_PASSWORD_CLI.md) |
| HostFiltering | 두 origin 호스트만 허용하는 호스트 필터. | Program.cs | [02_ARCHITECTURE](02_ARCHITECTURE.md) |
| IpAllowlistAdminAccessPolicy | 허용 IP 목록으로 관리 접근을 판정하는 IAdminAccessPolicy 구현. | PortfolioBlog.Api/Infrastructure/Access | [F018](features/F018_ADMIN_SURFACE_ACCESS_CONTROL.md) |
| MarkdownRenderer | 마크다운을 정제된 HTML로 만드는 렌더러(RenderDetailed → RenderedMarkdown). | PortfolioBlog.Api/Infrastructure/Markdown | [F011](features/F011_MARKDOWN_RENDERING_CACHE.md) |
| MarkdownTooComplexException | 너무 복잡한 마크다운일 때 던져지며, 공개 상세는 본문 없이 페이지를 낸다. | PostModel (PortfolioBlog.Api/Pages) | [F013](features/F013_PUBLIC_POST_DETAIL.md) |
| OverloadExceptionHandler | 57014·55P03과 RenderBusyException을 503 + Retry-After 5로 바꾸는 핸들러. | PortfolioBlog.Api/Infrastructure/Web | [F020](features/F020_WEB_PIPELINE_PROTECTION.md), [10_ERROR_HANDLING](10_ERROR_HANDLING.md) |
| Post / PostTag / Series / Tag | 글·글-태그 연결·시리즈·태그 엔티티. | AppDbContext DbSet | [07_DATA_MODEL](07_DATA_MODEL.md) |
| PublicDbContext | AppDbContext를 상속한 공개 읽기 전용 컨텍스트. statement_timeout·읽기 전용 트랜잭션이 걸리고 SaveChanges는 항상 예외. | PortfolioBlog.Api/Infrastructure/Data | [F021](features/F021_STARTUP_BOOTSTRAP.md) |
| PublicPageConvention | 공개 Razor 페이지에 GET/HEAD 전용·공개 호스트 전용·속도 제한 메타데이터를 거는 규약. | PortfolioBlog.Api/Pages | [F012](features/F012_PUBLIC_HOME_LIST.md) |
| PublicQueries | 공개 글 목록·검색 등 공개 조회 모음. | PortfolioBlog.Api/Infrastructure/Data | [F012](features/F012_PUBLIC_HOME_LIST.md), [F014](features/F014_PUBLIC_SEARCH.md) |
| PublicRoleGrants | 마이그레이션 직후 공개 롤 권한을 REVOKE 후 읽기 테이블 SELECT만 GRANT하는 단계. | PortfolioBlog.Api/Infrastructure | [F021](features/F021_STARTUP_BOOTSTRAP.md) |
| RateLimitMetadata | 엔드포인트에 붙는 속도 제한 종류(Login·Preview·Upload·Search·PublicPage·PublicAsset). | RateLimitingExtensions | [F019](features/F019_RATE_LIMITING.md) |
| RenderBusyException | RenderGate 대기 초과 시 던지는 예외(503). | PortfolioBlog.Api/Infrastructure/Markdown | [F011](features/F011_MARKDOWN_RENDERING_CACHE.md) |
| RenderedPostCache | (PostId, xmin) 키의 렌더 결과 캐시. 정상 24시간, 강조 누락 2분, 동시 미스는 한 번의 렌더로 합침. | PortfolioBlog.Api/Infrastructure/Markdown | [F011](features/F011_MARKDOWN_RENDERING_CACHE.md), [14_PERFORMANCE](14_PERFORMANCE.md) |
| RenderGate | SemaphoreSlim으로 프로세스 전체 동시 렌더 수를 제한하는 관문. | PortfolioBlog.Api/Infrastructure/Markdown | [F011](features/F011_MARKDOWN_RENDERING_CACHE.md) |
| RequireAuth | 보호 화면 접근을 세션 확인 결과로 가르는 SPA 라우트 래퍼. 라우트 정의는 app/routes.tsx. | PortfolioBlog.Web/src/auth/RequireAuth.tsx, PortfolioBlog.Web/src/app/routes.tsx | [F001](features/F001_ADMIN_AUTH_SESSION.md), [F029](features/F029_ADMIN_SPA_SHELL.md) |
| SandboxCsp / PublicCsp | 첨부 응답용 sandbox CSP와 일반 응답용 CSP. | SecurityHeadersMiddleware (PortfolioBlog.Api/Infrastructure/Web) | [F010](features/F010_PUBLIC_ATTACHMENT_SERVING.md), [F020](features/F020_WEB_PIPELINE_PROTECTION.md) |
| SessionValidator | 요청마다 SessionEpoch를 조회해 세션을 재검증하고 실패하면 SignOut하는 검증기. | PortfolioBlog.Api/Infrastructure/Access | [F001](features/F001_ADMIN_AUTH_SESSION.md) |
| SiteEndpoints | 공개 호스트의 /feed.xml·/sitemap.xml·/robots.txt·/css/highlight.css 엔드포인트. | PortfolioBlog.Api/Pages/SiteEndpoints.cs | [F017](features/F017_PUBLIC_SITE_FEEDS.md) |
| StartupValidation | 마이그레이션 전에 I/O 없이 설정을 검증하는 단계. | PortfolioBlog.Api/Infrastructure | [F021](features/F021_STARTUP_BOOTSTRAP.md) |
| tools 서비스 | 백업용 compose 프로필 서비스(network_mode none, read_only). 첨부 볼륨을 tar로 뜬다. | deploy/docker-compose.yml, deploy/backup.sh | [F027](features/F027_BACKUP_RESTORE.md) |
| X-Requested-With | 관리 API 요청에 요구되는 CSRF 방어 헤더(XMLHttpRequest). SPA가 모든 요청에 붙인다. | client.ts, AdminSurfaceMiddleware | [F018](features/F018_ADMIN_SURFACE_ACCESS_CONTROL.md), [F029](features/F029_ADMIN_SPA_SHELL.md) |
<!-- /doc-harness:section -->

<!-- doc-harness:section id="related" hash="86074abbe0f892e8c4293076aa408ba4be3818286e099cc94b83314e2d322e54" -->
## 관련 문서

## 관련 문서

- [00_EXECUTIVE_SUMMARY](00_EXECUTIVE_SUMMARY.md)
- [02_ARCHITECTURE](02_ARCHITECTURE.md)
- [05_CONFIGURATION](05_CONFIGURATION.md)
- [07_DATA_MODEL](07_DATA_MODEL.md)
- [08_API](08_API.md)
- [09_FEATURES](09_FEATURES.md)
- [10_ERROR_HANDLING](10_ERROR_HANDLING.md)
- [13_SECURITY](13_SECURITY.md)
- [16_DEPLOYMENT](16_DEPLOYMENT.md)
<!-- /doc-harness:section -->

<!-- doc-harness:section id="unknowns" hash="09913dcc280c4161949a1903754e9782897bfd0d9fbd63e15c5cef58cf45d4fd" -->
## 확인하지 못한 것

- 용어 정의 대부분은 워크스페이스 산출물에 근거하며 이 단계에서 코드를 다시 읽어 검증하지 않았다. 코드로 재확인한 것은 RequireAuth·ApiError·routes 경로와 ApiBodyLimitMiddleware 한도(262,144바이트, 413)뿐이다.
<!-- /doc-harness:section -->
