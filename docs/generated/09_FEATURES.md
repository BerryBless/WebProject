# 기능 목록

<!-- doc-harness:section id="summary" hash="aa5f318ece1b332f09883e3c4128822641f959f348cfe1b6f5a854bcd9818b91" -->
## 한 줄 요약

기능 29개(CORE 8 · SUPPORTING 11 · INFRA 10). 각 기능의 흐름·다이어그램·실패 지점은 개별 문서에 있다.
<!-- /doc-harness:section -->

<!-- doc-harness:section id="list" hash="c4c7182ef0d32ef4d39d6938be7bff8d8eedda9d4cf2660609a858bbd7d19ef7" -->
## 기능 목록

| ID | 이름 | 중요도 | 상태 | 진입점 | 요약 |
|---|---|---|---|---|---|
| <a id="f001"></a>[F001](features/F001_ADMIN_AUTH_SESSION.md) | 관리자 로그인·세션 확인·로그아웃 | CORE | ACTIVE | `SPA /login`, `GET /api/auth/me`, `POST /api/auth/login`, `POST /api/auth/logout` | 결론: F001은 관리자 인증을 쿠키 세션 하나로 처리하고, 쿠키가 붙은 요청마다 DB로 세션을 다시 검증한다. 이번 재검증에서는 AuthEndpoints·SessionValidator·SessionRules·AdminCredential·AdminOptions·AuthServiceCol |
| <a id="f002"></a>[F002](features/F002_ADMIN_POST_LIST.md) | 관리 글 목록 조회·삭제 | CORE | ACTIVE | `SPA /`, `GET /api/posts`, `DELETE /api/posts/{id:guid}` | 결론: F002는 실제로 동작하는 핵심 기능이다. 이번 재검증에서 PostEndpoints.cs, PostsPage.tsx, PostQueries.cs, endpoints.ts, client.ts, queryClient.ts, Program.cs, ApiEndpoints.cs를 다시 읽 |
| <a id="f003"></a>[F003](features/F003_ADMIN_POST_EDITOR.md) | 글 작성·수정(마크다운 에디터) | CORE | ACTIVE | `SPA /posts/new`, `SPA /posts/:id`, `GET /api/posts/{id:guid}`, `POST /api/posts`, `PUT /api/posts/{id:guid}` | 결론: F003은 실제로 동작하는 핵심 기능이다(CONFIRMED). 이번 재검증에서 이전 분석을 현재 코드와 다시 대조했고, 동작 변경은 없었다. PostEndpoints.cs·PostEditorPage.tsx·PostValidation.cs·TagResolver.cs·DbConfli |
| <a id="f004"></a>[F004](features/F004_MARKDOWN_PREVIEW.md) | 마크다운 실시간 미리보기 | SUPPORTING | ACTIVE | `POST /api/preview`, `SPA /posts/new`, `SPA /posts/:id` | 결론: 미리보기는 SPA 쪽 호출 조절과 서버 쪽 방어가 겹친 구조다. 서버 방어는 클라이언트 조절 없이도 스스로 성립한다. 서버에는 아무것도 저장하지 않는다.  SPA의 PreviewPane이 하는 일: - 500ms 디바운스 - 요청 사이 최소 1500ms 간격 - Retry-Aft |
| <a id="f005"></a>[F005](features/F005_LOCAL_DRAFT_RECOVERY.md) | 편집 임시본 자동 보관·복원 | SUPPORTING | ACTIVE | `SPA /posts/new`, `SPA /posts/:id` | 결론: F005는 서버 없이 브라우저 안에서만 동작한다. 기능 전체를 `lib/drafts.ts`(load·save·clear·sameFields와 모양 검사 isDraft)와 `PostEditorPage.tsx`의 `Editor` 컴포넌트가 맡는다. 이번 재검증에서 이전 분석의 줄 번 |
| <a id="f006"></a>[F006](features/F006_POST_EDIT_CONFLICT.md) | 글 저장 충돌 감지·비교 해결 | SUPPORTING | ACTIVE | `PUT /api/posts/{id:guid}`, `SPA /posts/:id` | 결론: 충돌 감지는 서버의 낙관적 동시성이 맡고, 해결은 클라이언트에서 사람이 직접 고른다. 이번 재검증에서 이 기능의 동작 변화는 확인되지 않았다. PostEndpoints.UpdateAsync, DbConflict, PostEditorPage, ConflictPanel의 줄 번호와  |
| <a id="f007"></a>[F007](features/F007_ADMIN_SERIES_MANAGEMENT.md) | 시리즈 관리 | SUPPORTING | ACTIVE | `SPA /series`, `GET /api/series`, `GET /api/series/{id:guid}`, `POST /api/series`, `PUT /api/series/{id:guid}`, `DELETE /api/series/{id:guid}` | 결론: 시리즈 관리는 지금 동작하는 관리용 CRUD 기능이다. 이번 재검증에서도 코드는 이전 분석과 일치했다. 이 기능의 파일은 직접 변경되지 않았고, 작업 트리에서 변경된 API 파일은 TagEndpoints.cs뿐이다. 서버는 SeriesEndpoints의 정적 핸들러 5개(List |
| <a id="f008"></a>[F008](features/F008_ADMIN_TAG_MANAGEMENT.md) | 태그 관리 | SUPPORTING | ACTIVE | `SPA /tags`, `GET /api/tags`, `DELETE /api/tags/{id:guid}` | 태그 관리 API는 조회(GET /api/tags)와 삭제(DELETE /api/tags/{id:guid}) 두 개다. 태그를 만드는 API는 따로 없고, 글 저장(POST /api/posts, PUT /api/posts/{id}) 트랜잭션 안에서 TagResolver.ResolveId |
| <a id="f009"></a>[F009](features/F009_ADMIN_ATTACHMENT_MANAGEMENT.md) | 첨부 이미지 업로드·목록·삭제 | CORE | ACTIVE | `SPA /attachments`, `GET /api/attachments`, `POST /api/attachments`, `DELETE /api/attachments/{id:guid}` | 결론: F009는 실제로 동작하는 관리 전용 첨부 기능이다. 이전 분석을 현재 코드로 다시 확인했고, 동작상 달라진 점은 없다. 검증에서 지적된 F009_FLOW 과복잡(간선 42개) 문제는 다이어그램을 업로드 흐름(F009_FLOW)과 삭제 흐름(F009_FLOW_DELETE)으로 나 |
| <a id="f010"></a>[F010](features/F010_PUBLIC_ATTACHMENT_SERVING.md) | 첨부 이미지 공개 제공 | CORE | ACTIVE | `GET /attachments/{id:guid}/{fileName}`, `HEAD /attachments/{id:guid}/{fileName}` | 결론: F010은 상태 없는 정적 최소 API 핸들러 하나(PublicAttachmentEndpoints.GetAsync)로 구현된 공개 첨부 읽기 전용 경로이며, 두 호스트 모두에서 열린다. 처리 순서는 다음과 같다. ① Program.cs가 app 루트(/api 그룹 밖)에 MapM |
| <a id="f011"></a>[F011](features/F011_MARKDOWN_RENDERING_CACHE.md) | 마크다운 렌더링·렌더 결과 캐시 | CORE | ACTIVE | `POST /api/posts`, `PUT /api/posts/{id:guid}`, `POST /api/preview`, `Razor /posts/{slug}` | 결론: F011은 공용 렌더링 인프라다. 현재 코드를 다시 확인한 결과 이전 분석과 동작 차이는 없다. 렌더링은 MarkdownRenderer.RenderDetailed 한 곳에서 동기로 실행되며, 도중에 취소할 수 없다. 요청 경로는 모두 RenderGate(SemaphoreSlim, |
| <a id="f012"></a>[F012](features/F012_PUBLIC_HOME_LIST.md) | 공개 홈(최신 글 목록) | CORE | ACTIVE | `Razor /` | 결론: F012는 요청마다 DB 문장 2개(COUNT + OFFSET/LIMIT SELECT)를 실행하는 무상태·무캐시 SSR 목록 페이지입니다. 입력은 `?page` 하나뿐이고, 기본 거부 방식으로 검증합니다(ASCII 숫자 1~4자리, 값 1개, 1..500). 실패 응답은 대부분  |
| <a id="f013"></a>[F013](features/F013_PUBLIC_POST_DETAIL.md) | 공개 글 상세 보기 | CORE | ACTIVE | `Razor /posts/{slug}` | 결론: F013은 동작 중인 기능이며, PostModel.OnGetAsync 하나가 모든 처리를 맡는다. 처리는 다섯 단계로 진행된다. (1) slug 검증: null·공백 여부와 SlugRules 형식을 본다. (2) 메타 조회: PublicQueries.GetPostAsync가 SE |
| <a id="f014"></a>[F014](features/F014_PUBLIC_SEARCH.md) | 공개 글 검색 | SUPPORTING | ACTIVE | `Razor /search` | 결론: 공개 검색은 Razor 페이지 한 개(SearchModel)와 정적 쿼리 한 개(PublicQueries.SearchAsync)로 이뤄진 읽기 전용 기능이다. 비용 상한은 네 겹이다. 1. 엔드포인트 메타데이터 기반 속도 제한: 전역 동시 실행 4, IP별 분당 검색 20회, I |
| <a id="f015"></a>[F015](features/F015_PUBLIC_SERIES_PAGE.md) | 공개 시리즈별 글 목록 | SUPPORTING | ACTIVE | `Razor /series/{slug}` | 결론: F015는 읽기 전용 기능이다. Razor 페이지 SeriesPageModel(/series/{slug}) 하나와 정적 조회 PublicQueries.GetSeriesAsync 하나로 이뤄진다. PublicPageConvention이 붙인 메타데이터가 GET/HEAD 전용, 공개 |
| <a id="f016"></a>[F016](features/F016_PUBLIC_TAG_PAGE.md) | 공개 태그별 글 목록 | SUPPORTING | ACTIVE | `Razor /tags/{tag}` | 결론: F016은 읽기 전용 Razor 페이지 하나(TagPageModel)와 정적 조회 헬퍼(PublicQueries.ByTagAsync → PageAsync)로 구현된 동작 중인 기능이다. 요청 1건마다 순차 DB 문장 3개를 실행한다: 태그 SELECT, COUNT, 목록 SELE |
| <a id="f017"></a>[F017](features/F017_PUBLIC_SITE_FEEDS.md) | 피드·사이트맵·robots·코드 강조 CSS 제공 | SUPPORTING | ACTIVE | `GET /feed.xml`, `GET /sitemap.xml`, `GET /robots.txt`, `GET /css/highlight.css` | 결론: F017은 공개 사이트가 HTML 말고 내보내는 자원 5개다. 등록 방식은 두 가지로 나뉜다. (1) 정적 클래스 SiteEndpoints가 GET/HEAD 엔드포인트 4개(/feed.xml·/sitemap.xml·/robots.txt·/css/highlight.css)를 등록한 |
| <a id="f018"></a>[F018](features/F018_ADMIN_SURFACE_ACCESS_CONTROL.md) | 관리 표면 접근 통제(호스트·IP 허용 목록·CSRF 헤더·Origin) | INFRA | ACTIVE | `/api/* (미들웨어)`, `Caddy {$ADMIN_DOMAIN}` | 결론: 관리 표면(/api)은 두 계층으로 막는다. (1) 에지: Caddy가 관리 도메인 전체(HTTPS·평문 HTTP)에 remote_ip 허용 목록을 걸어 목록 밖이면 404를 낸다. 공개 도메인의 /api는 무조건 404로 끊는다. (2) 앱: AdminSurfaceMiddlew |
| <a id="f019"></a>[F019](features/F019_RATE_LIMITING.md) | 요청 속도 제한 | INFRA | ACTIVE | `전역 미들웨어 (UseRateLimiter)` | 결론: 속도 제한은 ASP.NET Core 기본 RateLimiter 미들웨어의 GlobalLimiter 하나로 구현된다. RateLimitingExtensions.BuildChain이 PartitionedRateLimiter.CreateChained로 제한기 11개를 묶고, 모든 요청 |
| <a id="f020"></a>[F020](features/F020_WEB_PIPELINE_PROTECTION.md) | 보안 헤더·오류 응답·과부하 처리·API 본문 제한 | INFRA | ACTIVE | `전역 미들웨어 (Program.cs 파이프라인)` | 결론: F020은 Program.cs 파이프라인에 걸린 방어 요소 네 개이고, 이번 재검증에서 현재 코드 동작은 이전 분석과 같았다. F020 파일(SecurityHeadersMiddleware·ErrorResponses·OverloadExceptionHandler·ApiBodyLimi |
| <a id="f021"></a>[F021](features/F021_STARTUP_BOOTSTRAP.md) | 앱 기동 부트스트랩(설정 검증·마이그레이션·공개 DB 롤 권한) | INFRA | ACTIVE | `CLI dotnet PortfolioBlog.Api.dll` | 결론: 기동 부트스트랩은 fail-fast로 설계됐다. Program.cs는 builder.Build()(73행) 뒤, app.Run()(126행) 전에 다섯 단계를 동기로 실행한다. ① StartupValidation.Validate(76행)가 설정을 I/O 없이 검증한다. 순서는 S |
| <a id="f022"></a>[F022](features/F022_HEALTH_CHECK.md) | 헬스체크 | INFRA | ACTIVE | `GET /health`, `CLI dotnet PortfolioBlog.Api.dll healthcheck` | 결론: 헬스체크는 액티브 프로브가 아니라 생존(liveness) 확인이다. /health는 DB 같은 의존성을 보지 않고 항상 상수 "Healthy"를 돌려준다. 그래도 이 응답이 나온다는 것은 기동 부트스트랩(설정 검증·첨부 루트 쓰기 가능 확인·마이그레이션·공개 롤 권한·렌더 워밍업 |
| <a id="f023"></a>[F023](features/F023_HASH_PASSWORD_CLI.md) | 관리자 비밀번호 해시 생성 CLI | INFRA | ACTIVE | `CLI dotnet PortfolioBlog.Api.dll hash-password` | 결론: F023은 웹 호스트·DI·DB·네트워크를 쓰지 않는 단일 스레드 동기 CLI 경로이며 실제로 동작한다. Program.cs 맨 위의 `args is [HashPasswordCommand.Name]` 분기가 `HashPasswordCommand.Run(Console.In, Con |
| <a id="f024"></a>[F024](features/F024_ATTACHMENT_JANITOR.md) | 고아 첨부 파일 정리(백그라운드) | SUPPORTING | ACTIVE | `BackgroundService AttachmentJanitor` | 결론: F024의 AttachmentJanitor는 싱글턴이면서 호스티드 서비스로 등록되어 있고, 앱 기동 직후와 6시간마다 SweepOnceAsync를 실행한다. 코드상 실제로 동작하는 기능이다(ACTIVE). 스윕 한 번은 세 단계로 이루어진다. (1) 저장 루트의 .tmp 밑 모든 |
| <a id="f025"></a>[F025](features/F025_EDGE_PROXY_TLS.md) | 에지 프록시·TLS·관리 SPA 정적 서빙 | INFRA | ACTIVE | `Caddy {$DOMAIN}`, `Caddy {$ADMIN_DOMAIN}` | 결론: 이 기능은 애플리케이션 코드가 아니다. 선언형 Caddy 설정(deploy/Caddyfile)과, 그 설정을 관리 SPA 빌드 산출물과 함께 굽는 멀티스테이지 이미지(PortfolioBlog.Web/Dockerfile)로 구현된다. 재검증 결과 이전 분석의 구조·동작·줄 범위는  |
| <a id="f026"></a>[F026](features/F026_COMPOSE_DEPLOYMENT.md) | Docker Compose 운영 배포 구성 | INFRA | ACTIVE | `CLI docker compose -f deploy/docker-compose.yml up` | 결론: F026은 실행 코드가 아니라 선언형 배포 구성이다. docker-compose.yml, Dockerfile 2개, postgres 초기화 스크립트로 이뤄진다. 이 구성이 넣어 주는 환경변수는 앱 코드가 소비한다(StartupValidation, HealthCheckCommand |
| <a id="f027"></a>[F027](features/F027_BACKUP_RESTORE.md) | DB·첨부 백업과 복원 | INFRA | ACTIVE | `CLI deploy/backup.sh`, `CLI deploy/restore.sh` | 결론: F027은 애플리케이션 코드가 아니다. deploy/ 아래 두 bash 스크립트가 docker compose 서비스(postgres·tools·api·caddy)를 조작해 수행하는 운영 절차다. backup.sh는 무중단으로 동작한다. DB 덤프 → 첨부 tar 순서로 일관성을  |
| <a id="f028"></a>[F028](features/F028_DEPLOY_SMOKE_TEST.md) | 배포 스모크 검증 | INFRA | ACTIVE | `CLI deploy/smoke/run.sh` | 결론: F028은 deploy/smoke/run.sh가 이끄는 순차 bash 파이프라인이다.  - **스택 기동**: 운영 compose에 스모크 오버레이를 얹어 COMPOSE_PROJECT_NAME=pb-smoke 스택을 빌드하고 `up -d --wait`로 기동한다. healthch |
| <a id="f029"></a>[F029](features/F029_ADMIN_SPA_SHELL.md) | 관리 SPA 셸(라우팅·API 클라이언트·오류/없는 화면) | SUPPORTING | ACTIVE | `SPA *`, `SPA main.tsx` | 결론: F029는 관리 SPA의 공통 셸이다. 이번 재검증은 이 기능 파일이 직접 바뀌어서가 아니라, 의존 기능과 의존 표의 정합성을 다시 맞추려고 한 것이다. 관련 파일(main.tsx, App.tsx, routes.tsx, queryClient.ts, Layout.tsx, Route |
<!-- /doc-harness:section -->

<!-- doc-harness:section id="dependencies" hash="fde0e5aacb992dea15110fcc4bea92241179e644c668c4736c2a919d7b895352" -->
## 기능 간 의존

기능이 많아 표로 적는다(다이어그램 복잡도 상한 25/40).

| 기능 | 의존하는 기능 |
|---|---|
| F001 관리자 로그인·세션 확인·로그아웃 | F018, F019, F029 |
| F002 관리 글 목록 조회·삭제 | F001, F029 |
| F003 글 작성·수정(마크다운 에디터) | F001, F004, F005, F006, F009, F011, F029 |
| F004 마크다운 실시간 미리보기 | F001, F011, F010, F019 |
| F005 편집 임시본 자동 보관·복원 | F003 |
| F006 글 저장 충돌 감지·비교 해결 | F003 |
| F007 시리즈 관리 | F001, F029 |
| F008 태그 관리 | F001, F029 |
| F009 첨부 이미지 업로드·목록·삭제 | F001, F019, F029 |
| F010 첨부 이미지 공개 제공 | F009, F019 |
| F012 공개 홈(최신 글 목록) | F019, F020 |
| F013 공개 글 상세 보기 | F011, F010, F019, F020 |
| F014 공개 글 검색 | F019, F020 |
| F015 공개 시리즈별 글 목록 | F019, F020 |
| F016 공개 태그별 글 목록 | F019, F020 |
| F017 피드·사이트맵·robots·코드 강조 CSS 제공 | F019 |
| F018 관리 표면 접근 통제(호스트·IP 허용 목록·CSRF 헤더·Origin) | F025 |
| F024 고아 첨부 파일 정리(백그라운드) | F009 |
| F026 Docker Compose 운영 배포 구성 | F021, F022, F025 |
| F027 DB·첨부 백업과 복원 | F026 |
| F028 배포 스모크 검증 | F026, F027, F018 |
| F029 관리 SPA 셸(라우팅·API 클라이언트·오류/없는 화면) | F025 |
<!-- /doc-harness:section -->

<!-- doc-harness:section id="excluded" hash="ea4112954049462f9ccdffebad5daa5867786d4dc2f4a11e26dc37ef90eaefeb" -->
## 기능으로 세지 않은 것

- .claude/ · .agents/ · .codex/ 에이전트·스킬 정의 — AI 개발 하네스(개발 도구)이며 제품 기능이 아니다.
- scripts/auto-commit.ps1 · scripts/harness-audit.ps1 · scripts/hooks/guard-write-scope.ps1 — 자동 커밋·하네스 감사·쓰기 범위 훅으로, 개발 도구 스크립트다.
- .github/workflows/ci.yml (GitHub Actions CI) — .github/ 아래 개발·CI 도구로 분류 규칙상 기능에서 제외한다(배포 스모크 검증 F028은 deploy/ 아래 운영 절차로 따로 다룬다).
- plan/ · docs/ 문서 — 설계·보고·운영 문서로 기능 구현이 아니다.
- doc-harness/ — 문서화 하네스로 이 세션의 분석 대상에서 제외된 디렉터리다.
- PortfolioBlog.Web/scripts/e2e-prepare.mjs — E2E 테스트 실행 전 인증서·해시를 준비하는 테스트 보조 스크립트다.
- RemoteIpStartupFilter (PortfolioBlog.Api.Tests) — TestServer용 테스트 전용 미들웨어로 운영 코드에 없다.
<!-- /doc-harness:section -->
