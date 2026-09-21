# 기술 블로그 공개 사이트 구현 계획 (Plan 2B/4)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 방문자가 읽는 공개 표면 전체(글 목록·글·태그·시리즈·검색 Razor 페이지, Atom·sitemap·robots)를 `PortfolioBlog.Api`에 추가하고, 2A가 넘긴 숙제(렌더 비용 상한, 전역 보안 헤더·호스트 제한, 공개·업로드 속도 제한과 체인 순서, `statement_timeout`, 관리 JSON 본문 256KB, 첨부 정합성, 시간 의존 테스트)를 같은 브랜치에서 닫는다.

**Architecture:** 공개 페이지는 스크립트가 전혀 없는 서버 렌더링(Razor Pages, GET/HEAD 전용, 공개 호스트에만 매칭)이다. 읽기 경로는 쓰기가 DB 수준에서 불가능한 별도 연결(`PublicDbContext`: `statement_timeout` + `default_transaction_read_only=on`)을 쓴다. 마크다운 렌더링은 프로세스 전역 `RenderGate`(동시 실행 상한 + 대기 시간 상한) 뒤에서만 일어나고, 공개 글은 `(PostId, xmin)` 키의 메모리 캐시 + 단일 비행(single-flight)으로 글 버전당 한 번만 렌더링한다. 저장 경로가 같은 게이트를 거치며 캐시를 선채움한다.

**Tech Stack:** .NET SDK 10.0.303, ASP.NET Core Minimal API + Razor Pages(`Microsoft.NET.Sdk.Web`에 포함 — **새 NuGet 패키지 없음**), EF Core 10 + Npgsql 10.0.3, `System.Threading.RateLimiting`, `Microsoft.Extensions.Caching.Memory`(공유 프레임워크), `System.Xml.XmlWriter`, xUnit 2.9.3, Testcontainers.PostgreSql 4.15.0(PostgreSQL 17), AngleSharp(테스트의 HTML 파싱 — HtmlSanitizer의 전이 의존성).

**Spec:** `plan/tech_blog_0920.md`(승인됨) — 3.1(표면), 3.4(공개 경로), 3.5(글 상세 요청), 3.6(응답 헤더), 3.7(자원 제한), 3.8(고아 파일), 6절의 "출력"·"자원" 필수 테스트. 선행 계획: `docs/superpowers/plans/2026-09-20-tech-blog-backend-core.md`(규칙 1~4), `docs/superpowers/plans/2026-09-21-tech-blog-content-pipeline.md`(규칙 5~9, "Plan 2B로 넘기는 항목"), 실행 보고서 `plan/tech_blog_2a_report_0921.md` 6~8절. 기준 커밋: master `669f325`. 후속: Plan 3(관리 SPA), Plan 4(배포).

## Global Constraints

- 대상 프레임워크 `net10.0`, `Nullable=enable`, `ImplicitUsings=enable`. 빌드는 **경고 0·오류 0**(Razor 생성 코드 포함). 솔루션 `PortfolioBlog.slnx`. 네임스페이스는 `PortfolioBlog.Api.*`, 테스트는 `PortfolioBlog.Api.Tests.*`.
- 의존 방향: `Features`·`Pages` → `Infrastructure` → `Contracts` → `Domain`. `Features` 간, `Features`↔`Pages` 간 직접 참조 금지.
- **주석 규칙(CLAUDE.md "적용 범위" 표):** 인터페이스·public 클래스와 그 메서드(생성자 포함)·확장 메서드·미들웨어·엔드포인트 클래스·PageModel·테스트 클래스에는 한국어 `<summary>` + `<remarks>`의 `<b>[성능 및 동시성 제약 조건]</b>` 3항목. 자동 속성·상수·enum 멤버·DTO record·옵션 속성·테스트 메서드는 `<summary>`만. 메모리·네트워크·동시성 타입(`SemaphoreSlim`, `MemoryCache`, `ConcurrentDictionary`, `Lazy<Task>`, `Stream`, `RateLimiter`, `XmlWriter`가 쓰는 `MemoryStream` 등) 선언부에는 "왜 이 타입인가"를 내부 동작 근거로 인라인 `//` 주석. **이 계획의 코드 블록은 대표 주석만 보이므로 구현 시 표에 맞게 채운다.** 고치는 파일의 상용구 `<remarks>`는 그때 함께 정리한다.
- **선행 규칙 1~9(전부 구속력 있음):** (1) 속도 제한 파티션은 원시 경로가 아니라 엔드포인트 메타데이터로 고른다. (2) 앱 검증과 DB 제약에 같은 정규식 문자열을 공유하지 않는다 — .NET은 `\A…\z`. (3) `ExecuteUpdateAsync`/`ExecuteDeleteAsync`의 DB 오류는 감싸이지 않은 `PostgresException`이다. (4) NUL은 소스에서 C# 이스케이프(백슬래시-0)로만 표기하고 6글자 유니코드 이스케이프를 코드·주석·문서·셸 어디에도 쓰지 않는다. 커밋 전에 변경 파일의 0x00 바이트를 검사한다. (5) 비용 상한은 크기가 아니라 **시간**으로 건다. (6) 파서는 기본 거부 + 모양 검사. (7) 주석의 성능·동작 주장은 **측정한 것만** 쓴다. (8) 테스트가 **실패할 수 있는지** 확인한다(구현 전에 실패를 본다). (9) 문자열은 코드 포인트 경계에서 자르고, 자른 뒤 앞 단계 불변식을 다시 확인한다.
- 오류 응답: `/api`·`/attachments`·`/health`는 `ProblemDetails`, 그 밖의 공개 경로는 고정 HTML 오류 페이지(사용자 입력을 반사하지 않는다). 잘못된 입력은 400/404이며 **500이 되어서는 안 된다**. DB에 닿는 모든 문자열(쿼리 문자열·경로 값 포함)은 NUL을 거부한다(`TextRules`).
- 접근 계약은 그대로다: `/api/*`는 관리 호스트·허용 IP·`X-Requested-With`·(변경 요청) Origin·세션을 **본문을 읽기 전에** 통과해야 한다. `/api` 밖의 라우트는 전부 GET/HEAD 전용이며 `AccessMatrixTests.PublicAllowlist`에 명시적으로 올린다. 공개 페이지·피드 라우트는 **공개 호스트에만** 매칭된다(`/attachments`·`/health`만 양쪽 호스트).
- 공개 HTML에는 스크립트·인라인 스타일·외부 자원이 없다. `Html.Raw`는 `MarkdownRenderer` 출력에만 쓴다. 절대 URL은 요청 Host가 아니라 `Site:PublicOrigin`으로 만든다. 사이트 CSS에 **id 선택자를 쓰지 않는다**(제목 id는 작성자 텍스트에서 나온다).
- 설정은 `builder.Build()` 이후에만 읽는다(`IOptions<T>` 지연 바인딩). 보안·한도 설정 오류는 `StartupValidation`에서 시작 실패로 처리한다.
- 비밀번호·쿠키·요청 본문·글 본문·검색어·파일 내용은 로그에 남기지 않는다.
- 커밋 메시지: `{접두사}: {제목}`(접두사: 추가|수정|버그수정|리팩토링|문서|테스트|의존성), 끝 줄은 정확히 `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`(자기 모델 이름으로 바꾸지 않는다). 구현 에이전트는 `.git/auto_commit_msg.txt`를 만들지 않는다. 푸시·amend 금지.
- 새 `.cs`·`.cshtml`·`.css` 파일은 CRLF(저장소 작업 트리 규약, `git ls-files --eol`로 확인). 실제 비밀번호·해시·도메인·IP를 커밋하지 않는다. 절대 경로 하드코딩 금지.
- 테스트 실행 전 Docker를 실행해 둔다. 전체: `dotnet test PortfolioBlog.slnx -c Release`. 시작 기준선: **412개 통과, 경고 0**. 각 Task의 기대 개수는 적지 않는다 — 구현자가 **실제 수**를 보고한다(2A 정오표 #16). 로컬에서 1개가 `Database.Migrate()` 15초 연결 타임아웃으로 실패하면 재실행한다(Windows Docker Desktop 환경 문제, `plan/resume_guide_0921.md` 5절).
- EF 도구는 이 계획부터 컨텍스트가 둘이므로 항상 `--context AppDbContext`를 붙인다. **이 계획은 마이그레이션을 추가하지 않는다**(스키마 변경 없음).

**사전 스파이크로 확인한 사실(2026-09-21, 이 저장소의 SDK로 Kestrel + PostgreSQL 17에 실행. 계획의 주장은 아래 측정에만 근거한다):**

| # | 확인한 것 | 결과 |
|---|---|---|
| S1 | Razor Pages 엔드포인트의 `RoutePattern.RawText` | `@page "/"` → `''`(빈 문자열, `/`가 아니다), `@page "/posts/{slug}"` → `posts/{slug}`(**앞 슬래시 없음**). `@page "/"`로 선언하면 선택자가 하나뿐이라 `/Index`는 404 |
| S2 | `IPageRouteModelConvention`으로 `selector.EndpointMetadata`에 `HttpMethodMetadata(["GET","HEAD"])`·`HostAttribute`·임의 메타데이터를 넣으면 | 라우팅이 그대로 집행한다: `POST /` → **405 + `Allow: GET,HEAD`**, 다른 호스트 → 404, `HEAD /` → 200. GET 응답에 `Set-Cookie` 없음(antiforgery 쿠키는 발급되지 않는다) |
| S3 | `AddOptions<HostFilteringOptions>().Configure(o => o.AllowedHosts = [...])` | 목록 밖 Host → 프레임워크의 **400**(`text/html` 고정 본문). 설정 파일의 `AllowedHosts: "*"`보다 우선한다(기본 PostConfigure는 목록이 비었을 때만 채운다) |
| S4 | `/api` 본문 상한: `Content-Length` 초과는 미들웨어가 413, 길이 없는(chunked) 본문은 `Request.Body`를 감싼 스트림이 `BadHttpRequestException(413)`을 던지면 | 최소 API JSON 바인딩이 그 상태 코드를 그대로 응답한다 → **둘 다 413 ProblemDetails**(`ThrowOnBadRequest=false`) |
| S5 | `PartitionedRateLimiter.CreateChained` 순서 | **동시성 → 고정 창** 순서면 동시성 거부가 창 허용량을 쓰지 않는다(거부 5회 동안 창 잔여 2 → 2). 현재 순서(창 → 동시성)는 거부마다 창을 1씩 소모한다(2 → 1 → 0). 창이 거부하면 체인이 앞에서 빌린 동시성 임대를 반납한다(잔여 1 유지). 동시성 거부 임대에는 `RetryAfter` 메타데이터가 **없고** 창 거부에는 있다(60) |
| S6 | Npgsql 연결 문자열 `Options=-c statement_timeout=300 -c default_transaction_read_only=on` | `SHOW`로 둘 다 확인. `SELECT pg_sleep(3)` → **57014, 304ms**. `CREATE TABLE` → **25006**. 세션에서 `SET statement_timeout = 0` 후 풀을 한 바퀴 돌아도 300ms로 돌아온다(시작 매개변수가 리셋 기준) |
| S7 | `pg_advisory_xact_lock(hashtextextended('abc', 0))` | 다른 세션이 잡고 있으면 `lock_timeout='500ms'`에서 "canceling statement due to lock timeout"(55P03), 풀리면 즉시 획득 |
| S8 | `System.Text.Json`에 짝 없는 서로게이트 이스케이프 | `JsonException` — 요청 본문으로는 DB까지 가지 못한다(바인딩 400) |
| S9 | `XmlWriter`에 U+0001·짝 없는 서로게이트·U+FFFE | 전부 `ArgumentException`. **제목에 제어 문자가 든 글 하나가 피드 전체를 영구 500으로 만들 수 있다** → 출력 직전에 정리한다(`XmlConvert.IsXmlChar`는 U+0001·U+FFFE에 `false`) |
| S10 | `HtmlClassFormatter(StyleDictionary.DefaultLight/Dark).GetCSSString()` | 약 1.9KB. `body{background-color:#FFFFFFFF;}`와 `.plainText{color:#000000;color:#FFFFFF;}`(라이브러리 버그: 배경색이 `color`로 한 번 더 나온다 → 밝은 테마에서 흰 글자)로 시작한다 → **두 규칙은 버린다**. `<`·`url(`·id 선택자 없음. 강조 출력은 `<div class="csharp"><pre>\n<span class="keyword">…` |
| S11 | `ConfigureKestrel(o => o.AddServerHeader = false)` | 응답에 `Server` 헤더 없음. 옵션 값은 `IOptions<KestrelServerOptions>`로 읽힌다(TestServer에서도 확인 가능) |
| S12 | **이 개발 PC의 AdGuard** | 평문 HTTP(`http://127.0.0.1`)로 받은 HTML `<head>`에 `<script src="//local.adguard.org…">`를 **주입한다**. 앱이 낸 것이 아니다. 실제 호스트를 HTTP로 찔러 보는 리뷰에서 이걸 XSS로 오판하지 말 것 — TestServer(인메모리)는 영향이 없고, 실제 호스트 확인은 응답 바이트를 서버 쪽에서 보거나 AdGuard를 끄고 한다 |

**질문 없이 추천안으로 정한 설계 결정(승인 시 뒤집을 수 있다):**

| # | 결정 | 이유 | 대안 |
|---|---|---|---|
| D1 | 렌더 **게이트와 캐시를 둘 다** 둔다(핸드오프는 "캐시 또는 게이트") | 캐시만으로는 글 저장·미리보기·캐시 미스의 동시 렌더를 못 막고, 게이트만으로는 방문마다 수 초짜리 렌더가 반복된다 | 게이트만 |
| D2 | 캐시는 프로세스 메모리(64MB 상한), 키 `(PostId, xmin)`, 정상 24시간·시간 예산을 넘긴 렌더 2분 | 재배포하면 비워지므로 "렌더러 보안 수정이 과거 글 전체에 즉시 적용"(스펙 3.2)이 유지된다. 정책 버전 키는 필요 없다 | DB에 HTML 저장(스펙이 금지) |
| D3 | 게이트 대기 5초 초과·`statement_timeout`·잠금 대기 초과는 **503 + `Retry-After: 5`** | 과부하를 500과 구분한다. 방문자는 재시도하면 된다 | 500 |
| D4 | 공개 읽기는 `AppDbContext`를 상속한 `PublicDbContext` + 별도 연결 문자열(별도 풀) | `default_transaction_read_only=on`으로 공개 경로의 쓰기가 DB에서 불가능해진다(보안 최우선). 모델 구성을 복제하지 않는다 | 연결 인터셉터로 `SET`(풀 공유 시 관리 연결에 새는 위험) |
| D5 | `AllowedHosts` = 설정의 두 호스트뿐. `localhost`를 넣지 않는다 | 더 엄격한 쪽. **Plan 4의 컨테이너 헬스체크는 `Host: <공개 호스트>` 헤더를 붙여야 한다**(안 붙이면 400) | `localhost` 허용 |
| D6 | 관리 JSON 본문 256KB는 **직렬화 후 바이트** 기준으로 집행 | 스펙 3.7의 값. 결과: 따옴표·역슬래시·개행이 대부분인 본문은 200KB 미만에서도 413이 될 수 있고, 비 ASCII를 `\uXXXX`로 이스케이프하는 클라이언트(.NET 기본 인코더)는 한글 본문이 6배로 부푼다. 브라우저 `JSON.stringify`는 이스케이프하지 않는다(Plan 3) | 512KB |
| D7 | 잘못된 `page`(숫자 아님·0·상한 초과·결과 없는 쪽)는 **404**, 잘못된 검색어는 검색 페이지를 **400**으로 렌더 | 공개 HTML에 입력 반사를 최소화한다. 검색어는 Razor가 인코딩해 입력란에만 되돌린다 | 1쪽으로 보정 |
| D8 | 첨부 GET·`/health`·`robots.txt`·`highlight.css`에는 별도 정책 `PublicAsset`(IP별 600회/분) | 이미지 30장짜리 글 한 번이 페이지 한도(120)를 다 쓰면 안 된다. 관리자 쿠키를 실은 수제 요청의 세션 DB 조회도 이 한도가 상한이다 | 페이지 한도에 합산 |
| D9 | 업로드: 전역 30회/분 + 동시 2 | 메타데이터 제거·해시가 동기 I/O다(2A 핸드오프 "업로드 속도 제한") | 무제한 |
| D10 | 첨부 삭제·업로드·청소를 같은 내용(sha256) 단위 **세션 advisory lock**으로 직렬화 | "삭제 커밋 → 같은 내용 재업로드 INSERT → 삭제의 파일 지우기" 순서로 파일 없는 행이 남는 경쟁을 닫는다. 세션 잠금이라 "행 삭제 커밋 → 파일 삭제"가 잠금 안에서 끝난다 | 트랜잭션 잠금(커밋 전에 파일을 지워야 한다) |
| D11 | 표 정렬 클래스는 **하지 않는다** | 핸드오프가 "필요하면"이라 했고 렌더러 AST를 또 고쳐야 한다(YAGNI) | 허용 클래스 추가 |
| D12 | 코드 강조 CSS는 파일이 아니라 `GET /css/highlight.css` 엔드포인트가 라이브러리에서 생성 | 클래스 이름이 ColorCode 버전과 항상 일치한다. 손으로 쓴 파일은 조용히 어긋난다 | 정적 파일 + 골든 테스트 |

---

## 파일 구조 (이 계획이 만드는/바꾸는 파일)

```
PortfolioBlog.Api/
  Program.cs                                          # 수정: 옵션·Razor Pages·게이트/캐시·두 DbContext·미들웨어 순서·Kestrel Server 헤더·워밍업
  appsettings.json                                    # 수정: Site(Title·Description·Author), Public, Rendering 섹션
  Infrastructure/Web/PublicOptions.cs                 # 신규: 공개 표면 한도·statement_timeout
  Infrastructure/Web/RateLimitPolicy.cs               # 수정: PublicPage·PublicAsset·Search·Upload
  Infrastructure/Web/RateLimitingExtensions.cs        # 수정: 체인 재정렬(동시성 먼저), 새 정책, Retry-After
  Infrastructure/Web/SecurityHeadersMiddleware.cs     # 신규: 전역 보안 헤더(OnStarting)
  Infrastructure/Web/ErrorResponses.cs                # 신규: 경로별 오류 본문(ProblemDetails / 고정 HTML)
  Infrastructure/Web/OverloadExceptionHandler.cs      # 신규: 57014·55P03·RenderBusy → 503
  Infrastructure/Web/ApiBodyLimitMiddleware.cs        # 신규: 관리 JSON 256KB + LengthLimitedStream
  Infrastructure/Web/PublicUrls.cs                    # 신규: 공개 경로 생성(태그 인코딩, "."·".." 제외)
  Infrastructure/Web/XmlText.cs                       # 신규: XML 1.0에 못 들어가는 문자 제거
  Infrastructure/Access/SiteOptions.cs                # 수정: Title·Description·Author
  Infrastructure/Access/AdminOptions.cs               # 수정: UploadPerMinute·UploadConcurrency
  Infrastructure/Access/StartupValidation.cs          # 수정: 새 한도 범위 검사
  Infrastructure/Markdown/MarkdownRenderer.cs         # 수정: TimeProvider 주입, RenderDetailed(첫 이미지·시간 초과 여부)
  Infrastructure/Markdown/HighlightingCodeBlockRenderer.cs   # 수정: TimeProvider, TimedOut
  Infrastructure/Markdown/RenderingOptions.cs         # 신규
  Infrastructure/Markdown/RenderGate.cs               # 신규: 전역 동시 실행 상한 + 대기 상한
  Infrastructure/Markdown/RenderedPostCache.cs        # 신규: (PostId, xmin) 캐시 + 단일 비행
  Infrastructure/Markdown/HighlightCss.cs             # 신규: ColorCode 스타일 → CSS(밝은/어두운)
  Infrastructure/Data/AppDbContext.cs                 # 수정: sealed 해제, 파생용 protected 생성자
  Infrastructure/Data/PublicDbContext.cs              # 신규: 읽기 전용 컨텍스트 + 연결 문자열 조립
  Infrastructure/Data/DataServiceCollectionExtensions.cs   # 신규: 두 컨텍스트 등록(연결 문자열 가드 이전)
  Infrastructure/Data/PublicModels.cs                 # 신규: 공개 프로젝션 record(Version은 캐시 키로만)
  Infrastructure/Data/PublicQueries.cs                # 신규: 목록·글·태그·시리즈·검색·피드·sitemap 조회
  Infrastructure/Storage/AttachmentLock.cs            # 신규: sha256 단위 세션 advisory lock
  Infrastructure/Storage/AttachmentJanitor.cs         # 신규: 오래된 .tmp·고아 파일 정리, 파일 없는 행 보고
  Infrastructure/Storage/AttachmentOptions.cs         # 수정: JanitorEnabled
  Infrastructure/Storage/FileSystemAttachmentStore.cs # 수정: Exists, 루트 열거용 접근자
  Features/Posts/PostEndpoints.cs                     # 수정: 게이트 경유 렌더 + 캐시 선채움
  Features/Preview/PreviewEndpoints.cs                # 수정: 게이트 경유
  Features/Attachments/AttachmentEndpoints.cs         # 수정: Upload 정책, advisory lock
  Features/Attachments/PublicAttachmentEndpoints.cs   # 수정: PublicAsset 정책, 주석 사실화
  Pages/_ViewImports.cshtml, _ViewStart.cshtml        # 신규
  Pages/Shared/_Layout.cshtml, _PostList.cshtml, _Pager.cshtml   # 신규
  Pages/PublicPageConvention.cs                       # 신규: GET/HEAD·공개 호스트·속도 제한 메타데이터
  Pages/PublicPageModel.cs, PageHead.cs, PageNumber.cs, PagerModel.cs   # 신규
  Pages/Index.cshtml(.cs), Post.cshtml(.cs), Tag.cshtml(.cs), Series.cshtml(.cs), Search.cshtml(.cs)   # 신규
  Pages/SiteEndpoints.cs                              # 신규: /feed.xml /sitemap.xml /robots.txt /css/highlight.css
  wwwroot/css/site.css                                # 신규(wwwroot의 유일한 파일)
PortfolioBlog.Api.Tests/
  Infrastructure/ApiFactory.cs                        # 수정: 새 한도 기본값, 청소 끔, 공개 풀 정리, ConnectionString
  Infrastructure/SteppingTimeProvider.cs, PublicSeed.cs, HtmlDoc.cs         # 신규(테스트 도구)
  Infrastructure/RateLimitChainTests.cs, RenderGateTests.cs, RenderedPostCacheTests.cs, HighlightCssTests.cs,
                 XmlTextTests.cs, PublicUrlsTests.cs, PageNumberTests.cs, PublicDbContextTests.cs, PublicQueriesTests.cs,
                 AttachmentJanitorTests.cs, CheckConstraintCoverageTests.cs     # 신규
  Infrastructure/MarkdownRendererTests.cs             # 수정: 시간 의존 테스트를 주입 시계로 교체
  Features/PublicRateLimitTests.cs, SecurityHeadersTests.cs, HostFilteringTests.cs, ApiBodyLimitTests.cs,
           ErrorPipelineTests.cs, PublicPagesTests.cs, SearchPageTests.cs, FeedAndSitemapTests.cs,
           AttachmentIntegrityTests.cs, ValidationWithinDbConstraintsTests.cs  # 신규
  Features/AccessMatrixTests.cs                       # 수정: 공개 허용 목록, 공개 호스트 제한 닫힌 세계
  HealthEndpointTests.cs                              # 수정: 공개 호스트 클라이언트 사용(호스트 필터)
plan/tech_blog_0920.md, README.md, CLAUDE.md, AGENTS.md, PortfolioBlog.Api/PortfolioBlog.Api.http   # 수정(Task 9)
```

미들웨어 순서(이 계획이 끝났을 때):

```
HostFiltering(프레임워크, 두 호스트) → SecurityHeaders → TrustedForwardedHeaders → ExceptionHandler(+OverloadExceptionHandler)
→ StatusCodePages(ErrorResponses) → StaticFiles → AdminSurface → RateLimiter → Authentication → Authorization → ApiBodyLimit → 엔드포인트
```

---

### Task 1: 속도 제한 — 체인 재정렬 · 공개/검색/업로드 정책 · 옵션 · 테스트 기본값

**Files:**
- Create: `PortfolioBlog.Api/Infrastructure/Web/PublicOptions.cs`
- Modify: `PortfolioBlog.Api/Infrastructure/Web/RateLimitPolicy.cs`, `PortfolioBlog.Api/Infrastructure/Web/RateLimitingExtensions.cs`
- Modify: `PortfolioBlog.Api/Infrastructure/Access/AdminOptions.cs`, `PortfolioBlog.Api/Infrastructure/Access/StartupValidation.cs`
- Modify: `PortfolioBlog.Api/Features/Attachments/AttachmentEndpoints.cs`(업로드에 `Upload` 정책), `PortfolioBlog.Api/Features/Attachments/PublicAttachmentEndpoints.cs`(`PublicAsset` 정책), `PortfolioBlog.Api/Program.cs`(`/health`에 `PublicAsset`, `PublicOptions` 등록), `PortfolioBlog.Api/appsettings.json`
- Modify: `PortfolioBlog.Api.Tests/Infrastructure/ApiFactory.cs`
- Test: `PortfolioBlog.Api.Tests/Infrastructure/RateLimitChainTests.cs`, `PortfolioBlog.Api.Tests/Features/PublicRateLimitTests.cs`

**Interfaces:**
- Consumes: `RateLimitMetadata(RateLimitPolicy)`, `ClientIp.PartitionKey(IPAddress?)`, `AdminOptions`, `ApiFactory(pg, settings)`.
- Produces:
  - `enum RateLimitPolicy { Login, Preview, PublicPage, PublicAsset, Search, Upload }`
  - `PublicOptions { PagePerIpPerMinute=120, AssetPerIpPerMinute=600, SearchPerIpPerMinute=20, SearchConcurrency=4, StatementTimeoutMs=3000 }`, `PublicOptions.SectionName = "Public"`
  - `AdminOptions.UploadPerMinute=30`, `AdminOptions.UploadConcurrency=2`
  - `RateLimitingExtensions.BuildChain(AdminOptions, PublicOptions) : PartitionedRateLimiter<HttpContext>`(internal), `RateLimitingExtensions.RetryAfterSeconds(RateLimitLease) : int`(internal), `RateLimitingExtensions.ConcurrencyRetryAfterSeconds = 5`
  - Task 5·6이 Razor 페이지에 `RateLimitMetadata(PublicPage)`·`RateLimitMetadata(Search)`를 붙인다. `Search` 요청은 검색 창(20)과 페이지 창(120)에 **둘 다** 계산된다.

- [ ] **Step 1: 실패하는 체인 단위 테스트 작성**

`PortfolioBlog.Api.Tests/Infrastructure/RateLimitChainTests.cs`:

```csharp
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using PortfolioBlog.Api.Infrastructure.Access;
using PortfolioBlog.Api.Infrastructure.Web;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>속도 제한 체인의 순서와 파티션을 호스트 없이 검증한다(동시 실행 거부를 HTTP로 결정적으로 재현할 수 없어서 체인을 직접 친다).</summary>
public sealed class RateLimitChainTests
{
    private static DefaultHttpContext Request(RateLimitPolicy policy, string ip = "203.0.113.9")
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        ctx.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new RateLimitMetadata(policy)), policy.ToString()));
        return ctx;
    }

    /// <summary>동시 실행 거부가 분당 허용량을 쓰지 않는지 증명한다. 창이 먼저인 옛 순서에서는 거부 5회가 창 3을 다 써서 마지막 단언이 실패한다.</summary>
    [Fact]
    public void ConcurrencyRejection_DoesNotConsumeTheMinuteWindow()
    {
        using var chain = RateLimitingExtensions.BuildChain(
            new AdminOptions { PreviewConcurrency = 1, PreviewPerMinute = 3 }, new PublicOptions());

        var held = chain.AttemptAcquire(Request(RateLimitPolicy.Preview));
        Assert.True(held.IsAcquired);
        for (var i = 0; i < 5; i++)
        {
            using var rejected = chain.AttemptAcquire(Request(RateLimitPolicy.Preview));
            Assert.False(rejected.IsAcquired);
            Assert.Equal(RateLimitingExtensions.ConcurrencyRetryAfterSeconds, RateLimitingExtensions.RetryAfterSeconds(rejected));
        }
        held.Dispose();

        for (var i = 0; i < 2; i++)
        {
            using var ok = chain.AttemptAcquire(Request(RateLimitPolicy.Preview));
            Assert.True(ok.IsAcquired, $"창 허용량이 동시 실행 거부에 소모됐다(남은 {2 - i}회째에서 거부).");
        }
        using var overWindow = chain.AttemptAcquire(Request(RateLimitPolicy.Preview));
        Assert.False(overWindow.IsAcquired);
        Assert.InRange(RateLimitingExtensions.RetryAfterSeconds(overWindow), 1, 60);
    }

    /// <summary>창이 거부하면 앞에서 빌린 동시 실행 임대가 반납되는지 증명한다(반납되지 않으면 동시 1이 영구히 막힌다).</summary>
    [Fact]
    public void WindowRejection_ReturnsTheConcurrencyPermit()
    {
        using var chain = RateLimitingExtensions.BuildChain(
            new AdminOptions { UploadConcurrency = 1, UploadPerMinute = 1 }, new PublicOptions());
        chain.AttemptAcquire(Request(RateLimitPolicy.Upload)).Dispose();
        for (var i = 0; i < 3; i++)
        {
            using var rejected = chain.AttemptAcquire(Request(RateLimitPolicy.Upload));
            Assert.False(rejected.IsAcquired);
            Assert.InRange(RateLimitingExtensions.RetryAfterSeconds(rejected), 1, 60); // 매번 "창" 거부여야 한다(동시성 거부면 5)
        }
    }

    /// <summary>검색은 검색 창과 페이지 창에 둘 다 계산되고, 파티션은 IP별이다.</summary>
    [Fact]
    public void Search_CountsTowardThePageWindow_PerIp()
    {
        using var chain = RateLimitingExtensions.BuildChain(new AdminOptions(),
            new PublicOptions { PagePerIpPerMinute = 3, SearchPerIpPerMinute = 100, SearchConcurrency = 8 });
        for (var i = 0; i < 3; i++) Assert.True(Acquire(chain, RateLimitPolicy.Search));
        Assert.False(Acquire(chain, RateLimitPolicy.PublicPage));                          // 같은 IP: 페이지 창 소진
        Assert.True(Acquire(chain, RateLimitPolicy.PublicPage, ip: "198.51.100.7"));       // 다른 IP는 영향 없음
        Assert.True(Acquire(chain, RateLimitPolicy.PublicAsset));                          // 다른 정책은 영향 없음
    }

    /// <summary>검색 창 거부는 페이지 창을 쓰지 않는다(검색 창이 체인에서 앞이다).</summary>
    [Fact]
    public void SearchWindowRejection_DoesNotConsumeThePageWindow()
    {
        using var chain = RateLimitingExtensions.BuildChain(new AdminOptions(),
            new PublicOptions { PagePerIpPerMinute = 3, SearchPerIpPerMinute = 1, SearchConcurrency = 8 });
        Assert.True(Acquire(chain, RateLimitPolicy.Search));                               // 페이지 창 1 사용
        for (var i = 0; i < 5; i++) Assert.False(Acquire(chain, RateLimitPolicy.Search));
        Assert.True(Acquire(chain, RateLimitPolicy.PublicPage));
        Assert.True(Acquire(chain, RateLimitPolicy.PublicPage));
        Assert.False(Acquire(chain, RateLimitPolicy.PublicPage));
    }

    /// <summary>정책 메타데이터가 없는 요청은 어떤 제한기에도 걸리지 않는다.</summary>
    [Fact]
    public void RequestWithoutPolicy_IsNeverLimited()
    {
        using var chain = RateLimitingExtensions.BuildChain(new AdminOptions(), new PublicOptions { PagePerIpPerMinute = 1 });
        for (var i = 0; i < 50; i++)
        {
            using var lease = chain.AttemptAcquire(new DefaultHttpContext());
            Assert.True(lease.IsAcquired);
        }
    }

    private static bool Acquire(PartitionedRateLimiter<HttpContext> chain, RateLimitPolicy policy, string ip = "203.0.113.9")
    {
        using var lease = chain.AttemptAcquire(Request(policy, ip));
        return lease.IsAcquired;
    }
}
```

- [ ] **Step 2: 실패 확인**

Run: `dotnet build PortfolioBlog.slnx -c Release`
Expected: 컴파일 오류 — `RateLimitPolicy.Upload`·`PublicOptions`·`BuildChain`·`RetryAfterSeconds`가 없다.

- [ ] **Step 3: 옵션·정책·체인 구현**

`PortfolioBlog.Api/Infrastructure/Web/PublicOptions.cs`:

```csharp
namespace PortfolioBlog.Api.Infrastructure.Web;

/// <summary>설정 섹션 <c>Public</c>. 인증 없는 공개 표면의 자원 예산(스펙 3.7).</summary>
public sealed class PublicOptions
{
    /// <summary>설정 섹션 이름.</summary>
    public const string SectionName = "Public";

    /// <summary>공개 페이지·피드·sitemap의 IP별 분당 한도. 검색 요청도 여기에 함께 계산된다.</summary>
    public int PagePerIpPerMinute { get; set; } = 120;

    /// <summary>첨부 GET·<c>/health</c>·<c>robots.txt</c>·<c>highlight.css</c>의 IP별 분당 한도. 이미지가 많은 글 한 번이 페이지 한도를 다 쓰지 않게 분리했다.</summary>
    public int AssetPerIpPerMinute { get; set; } = 600;

    /// <summary><c>/search</c>의 IP별 분당 한도.</summary>
    public int SearchPerIpPerMinute { get; set; } = 20;

    /// <summary>동시에 실행할 수 있는 검색 수(전역). <c>ILIKE</c> 전체 스캔이 DB 연결을 독점하지 못하게 묶는다.</summary>
    public int SearchConcurrency { get; set; } = 4;

    /// <summary>공개 조회 연결의 <c>statement_timeout</c>(밀리초). 100~60000.</summary>
    public int StatementTimeoutMs { get; set; } = 3000;
}
```

`RateLimitPolicy.cs` — enum에 추가(기존 `Login`·`Preview` 아래, 요약의 "Plan 2B가 … 추가한다" 문장은 지운다):

```csharp
    /// <summary>공개 HTML 페이지·Atom·sitemap: IP별 고정 창. <see cref="Search"/> 요청도 이 창에 함께 계산된다.</summary>
    PublicPage,

    /// <summary>첨부 GET·<c>/health</c>·<c>robots.txt</c>·<c>highlight.css</c>: IP별 고정 창(페이지보다 넉넉하다).</summary>
    PublicAsset,

    /// <summary>공개 검색: IP별 고정 창 + 전역 동시 실행 제한, 그리고 <see cref="PublicPage"/> 창.</summary>
    Search,

    /// <summary>첨부 업로드: 전역 고정 창 + 동시 실행 제한(메타데이터 제거·해시가 동기 I/O다).</summary>
    Upload,
```

`AdminOptions.cs` — 끝에 추가:

```csharp
    /// <summary>첨부 업로드의 분당 전역 한도.</summary>
    public int UploadPerMinute { get; set; } = 30;

    /// <summary>동시에 실행할 수 있는 업로드 수(메타데이터 제거·해시는 요청 스레드를 막는 동기 I/O다).</summary>
    public int UploadConcurrency { get; set; } = 2;
```

`RateLimitingExtensions.cs` — `FallbackRetryAfterSeconds`를 지우고 `AddAppRateLimiting`·`Window`를 아래로 바꾼다(`Matches`·`Concurrency`는 그대로). `using PortfolioBlog.Api.Infrastructure.Access;`는 유지:

```csharp
    /// <summary>동시 실행 제한에 걸렸을 때의 <c>Retry-After</c>(초). 동시성 제한기는 재시도 시점을 주지 않는다(실측) — 분 단위 창과 달리 곧 풀리므로 짧게 준다.</summary>
    public const int ConcurrencyRetryAfterSeconds = 5;

    /// <summary>고정 창 거부의 <c>Retry-After</c> 상한(초) = 창 길이.</summary>
    private const int MaxRetryAfterSeconds = 60;

    public static IServiceCollection AddAppRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<AdminOptions>, IOptions<PublicOptions>>((o, admin, pub) =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = static (context, _) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = RetryAfterSeconds(context.Lease).ToString(CultureInfo.InvariantCulture);
                return ValueTask.CompletedTask;
            };
            o.GlobalLimiter = BuildChain(admin.Value, pub.Value);
        });
        return services;
    }

    /// <summary>거부된 임대에서 <c>Retry-After</c> 초를 고른다: 고정 창은 창 종료까지(1~60), 동시 실행 제한은 <see cref="ConcurrencyRetryAfterSeconds"/>.</summary>
    internal static int RetryAfterSeconds(RateLimitLease lease) =>
        lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? Math.Clamp((int)Math.Ceiling(retryAfter.TotalSeconds), 1, MaxRetryAfterSeconds)
            : ConcurrencyRetryAfterSeconds;

    /// <summary>체인을 만든다. <b>동시 실행 제한기를 고정 창보다 앞에</b> 둔다: CreateChained는 앞에서부터 임대를 빌리고, 뒤가 거부하면 앞의 임대를
    /// Dispose한다(실측). 동시성 임대는 Dispose로 반납되지만 고정 창에는 반환이 없으므로, 창이 앞이면 동시 실행 거부마다 분당 허용량이 1씩 사라진다.</summary>
    internal static PartitionedRateLimiter<HttpContext> BuildChain(AdminOptions admin, PublicOptions pub) =>
        PartitionedRateLimiter.CreateChained(
            Concurrency(RateLimitPolicy.Login, "login-concurrency", admin.LoginConcurrency),
            Concurrency(RateLimitPolicy.Preview, "preview-concurrency", admin.PreviewConcurrency),
            Concurrency(RateLimitPolicy.Upload, "upload-concurrency", admin.UploadConcurrency),
            Concurrency(RateLimitPolicy.Search, "search-concurrency", pub.SearchConcurrency),
            Window(ctx => Matches(ctx, RateLimitPolicy.Login), ctx => "login-ip:" + Ip(ctx), admin.LoginPerIpPerMinute),
            Window(ctx => Matches(ctx, RateLimitPolicy.Login), _ => "login-global", admin.LoginGlobalPerMinute),
            Window(ctx => Matches(ctx, RateLimitPolicy.Preview), _ => "preview-global", admin.PreviewPerMinute),
            Window(ctx => Matches(ctx, RateLimitPolicy.Upload), _ => "upload-global", admin.UploadPerMinute),
            // 검색 창이 페이지 창보다 앞: 검색 한도에 걸린 요청이 페이지 허용량까지 깎지 않는다.
            Window(ctx => Matches(ctx, RateLimitPolicy.Search), ctx => "search-ip:" + Ip(ctx), pub.SearchPerIpPerMinute),
            Window(ctx => Matches(ctx, RateLimitPolicy.PublicPage) || Matches(ctx, RateLimitPolicy.Search), ctx => "page-ip:" + Ip(ctx), pub.PagePerIpPerMinute),
            Window(ctx => Matches(ctx, RateLimitPolicy.PublicAsset), ctx => "asset-ip:" + Ip(ctx), pub.AssetPerIpPerMinute));

    private static string Ip(HttpContext ctx) => ClientIp.PartitionKey(ctx.Connection.RemoteIpAddress);

    internal static PartitionedRateLimiter<HttpContext> Window(Func<HttpContext, bool> applies, Func<HttpContext, string> key, int permitsPerMinute) =>
        PartitionedRateLimiter.Create<HttpContext, string>(ctx => applies(ctx)
            ? RateLimitPartition.GetFixedWindowLimiter(key(ctx), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true,
            })
            : RateLimitPartition.GetNoLimiter("none"));
```

클래스 `<remarks>`의 Memory Allocation 문장을 사실에 맞게 고친다: "공개 정책의 IP 파티션은 방문자 IP 수만큼 생기고, 프레임워크가 유휴 파티션을 주기적으로 걷어 낸다. IPv6는 /64로 묶는다(`ClientIp`)." 옛 주석 "뒤 제한기가 거부해도 앞에서 빌린 permit은 돌아오지 않는다 … 의도된 보수적 동작이다"는 **삭제**한다(이제 거짓이다).

`StartupValidation.Validate` — `PublicOptions`를 읽고 Preview 검사 아래에 추가:

```csharp
        var pub = services.GetRequiredService<IOptions<PublicOptions>>().Value;
        if (admin.UploadPerMinute < 1 || admin.UploadConcurrency < 1)
        {
            throw new InvalidOperationException("Admin:UploadPerMinute·UploadConcurrency 는 1 이상이어야 합니다.");
        }
        if (pub.PagePerIpPerMinute < 1 || pub.AssetPerIpPerMinute < 1 || pub.SearchPerIpPerMinute < 1 || pub.SearchConcurrency < 1)
        {
            throw new InvalidOperationException("Public:PagePerIpPerMinute·AssetPerIpPerMinute·SearchPerIpPerMinute·SearchConcurrency 는 1 이상이어야 합니다.");
        }
        if (pub.StatementTimeoutMs is < 100 or > 60_000)
        {
            throw new InvalidOperationException("Public:StatementTimeoutMs 는 100~60000 이어야 합니다.");
        }
```

`Program.cs`: `builder.Services.AddAppRateLimiting();` **앞**에 `builder.Services.Configure<PublicOptions>(builder.Configuration.GetSection(PublicOptions.SectionName));`, `/health` 매핑에 `.WithMetadata(new RateLimitMetadata(RateLimitPolicy.PublicAsset))`.
`AttachmentEndpoints.MapAttachmentEndpoints`: 업로드에 `.WithMetadata(new RateLimitMetadata(RateLimitPolicy.Upload))`.
`PublicAttachmentEndpoints.MapPublicAttachmentEndpoints`: `.WithMetadata(new RateLimitMetadata(RateLimitPolicy.PublicAsset))`. 클래스 주석의 "(공개 속도 제한은 Plan 2B에서 이 표면을 마저 제한한다)"를 "이 비용은 `PublicAsset` 한도(IP별, 기본 600회/분)가 상한이다"로 바꾼다.
`appsettings.json`: `"Public": {}` 섹션을 추가하지 않는다(기본값이 스펙 값이다) — 대신 README 설정 표(Task 9)에 키를 적는다.

`ApiFactory.ConfigureWebHost` — 미리보기 기본값 아래에 추가(한도 테스트만 작은 값으로 덮어쓴다):

```csharp
        builder.UseSetting("Admin:UploadPerMinute", "100000");
        builder.UseSetting("Admin:UploadConcurrency", "64");
        builder.UseSetting("Public:PagePerIpPerMinute", "100000");
        builder.UseSetting("Public:AssetPerIpPerMinute", "100000");
        builder.UseSetting("Public:SearchPerIpPerMinute", "100000");
        builder.UseSetting("Public:SearchConcurrency", "64");
```

- [ ] **Step 4: 단위 테스트 통과 확인**

Run: `dotnet test PortfolioBlog.slnx -c Release --filter "FullyQualifiedName~RateLimitChainTests"`
Expected: 5개 통과. **규칙 8 확인:** `BuildChain`의 인자 순서를 잠깐 옛 순서(Window들을 Concurrency 앞)로 바꿔 `ConcurrencyRejection_DoesNotConsumeTheMinuteWindow`가 실패하는 것을 본 뒤 되돌린다.

- [ ] **Step 5: HTTP 통합 테스트 작성·통과**

`PortfolioBlog.Api.Tests/Features/PublicRateLimitTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>공개·업로드 속도 제한이 실제 파이프라인에 걸려 있는지 검증한다. 테스트마다 격리된 팩토리(자체 제한기 상태)를 만든다.</summary>
[Collection("postgres")]
public sealed class PublicRateLimitTests(PostgresContainerFixture pg)
{
    /// <summary>공개 자산 한도는 IP별이다: 같은 IP의 3번째는 429 + Retry-After, 다른 IP는 통과.</summary>
    [Fact]
    public async Task PublicAsset_IsLimitedPerIp()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?> { ["Public:AssetPerIpPerMinute"] = "2" });
        using var client = factory.CreatePublicClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        using var third = await client.GetAsync("/health");
        Assert.Equal((HttpStatusCode)429, third.StatusCode);
        Assert.InRange(int.Parse(third.Headers.GetValues("Retry-After").Single()), 1, 60);

        using var other = factory.CreatePublicClient();
        other.DefaultRequestHeaders.Remove(RemoteIpStartupFilter.HeaderName);
        other.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, "198.51.100.200");
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/health")).StatusCode);
    }

    /// <summary>첨부 GET(없는 id의 404도)이 자산 한도에 계산된다 — 404를 무한히 두드려 DB 조회를 일으킬 수 없다.</summary>
    [Fact]
    public async Task AttachmentGet_CountsTowardTheAssetLimit_EvenWhen404()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?> { ["Public:AssetPerIpPerMinute"] = "1" });
        using var client = factory.CreatePublicClient();
        var url = $"/attachments/{Guid.NewGuid()}/x.png";
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url)).StatusCode);
        Assert.Equal((HttpStatusCode)429, (await client.GetAsync(url)).StatusCode);
    }

    /// <summary>업로드 분당 한도: 2번째 업로드는 본문 처리 전에 429.</summary>
    [Fact]
    public async Task Upload_IsLimitedPerMinute()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?> { ["Admin:UploadPerMinute"] = "1" });
        using var client = await factory.CreateLoggedInClientAsync();
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images", "exif-text.png"));
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/attachments", Form(bytes))).StatusCode);
        Assert.Equal((HttpStatusCode)429, (await client.PostAsync("/api/attachments", Form(bytes))).StatusCode);
    }

    /// <summary>한도 설정이 0이면 시작이 실패한다.</summary>
    [Fact]
    public void ZeroLimit_FailsStartup()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?> { ["Public:SearchConcurrency"] = "0" });
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Public:", ex.ToString(), StringComparison.Ordinal);
    }

    private static MultipartFormDataContent Form(byte[] bytes)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new MultipartFormDataContent { { part, "file", "a.png" } };
    }
}
```

Run: `dotnet test PortfolioBlog.slnx -c Release --filter "FullyQualifiedName~PublicRateLimitTests"`
Expected: 4개 통과.

- [ ] **Step 6: 전체 회귀 + 커밋**

Run: `dotnet build PortfolioBlog.slnx -c Release`(경고 0) → `dotnet test PortfolioBlog.slnx -c Release`(전부 통과, 실제 개수 보고)

```bash
git add -A
git commit -m "수정: 동시 실행 거부가 분당 허용량을 깎던 속도 제한 체인을 재정렬하고 공개·검색·업로드 정책 추가

- 동시 실행 제한기를 고정 창 앞으로(거부가 창을 소모하지 않고, 창 거부는 동시성 임대를 반납)
- 동시 실행 거부의 Retry-After를 60초에서 5초로
- PublicPage·PublicAsset·Search·Upload 정책과 Public 설정 섹션, 시작 시 범위 검증
- 첨부 GET·/health는 PublicAsset, 업로드는 Upload 정책

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: 전역 보안 헤더 · 호스트 제한 · 오류 응답 · 관리 JSON 본문 256KB

**Files:**
- Create: `PortfolioBlog.Api/Infrastructure/Web/SecurityHeadersMiddleware.cs`, `ErrorResponses.cs`, `OverloadExceptionHandler.cs`, `ApiBodyLimitMiddleware.cs`(같은 폴더)
- Modify: `PortfolioBlog.Api/Program.cs`
- Modify: `PortfolioBlog.Api.Tests/HealthEndpointTests.cs`(기본 클라이언트 → `CreatePublicClient()`)
- Test: `PortfolioBlog.Api.Tests/Features/SecurityHeadersTests.cs`, `HostFilteringTests.cs`, `ApiBodyLimitTests.cs`, `ErrorPipelineTests.cs`

**Interfaces:**
- Consumes: `SiteOptions.HostOf`, `AdminSurfaceMiddleware`, `IProblemDetailsService`.
- Produces:
  - `SecurityHeadersMiddleware.PublicCsp`(const string, 스펙 3.6의 공개 HTML CSP 그대로), `SecurityHeadersMiddleware.PermissionsPolicy`(const string)
  - `ErrorResponses.WriteAsync(HttpContext http, int status) : Task`, `ErrorResponses.HandleStatusCodeAsync(StatusCodeContext) : Task`
  - `OverloadExceptionHandler : IExceptionHandler`, `OverloadExceptionHandler.IsOverload(Exception) : bool`(internal), `OverloadExceptionHandler.RetryAfterSeconds = 5` — Task 3이 `RenderBusyException`을 `IsOverload`에 추가한다
  - `ApiBodyLimitMiddleware.JsonLimitBytes = 262_144`

**함정(스파이크·2A에서 확인):** 예외 처리 미들웨어는 오류 응답을 쓰기 전에 `Response.Clear()`로 헤더를 비운다 → 헤더를 `next` 호출 전에 직접 넣으면 500 응답에서 사라진다. `Response.OnStarting` 콜백으로 **전송 직전에** 넣는다(콜백은 `Clear()`에 지워지지 않는다). 첨부 핸들러는 자기 CSP(`default-src 'none'; sandbox`)를 먼저 넣으므로 미들웨어는 **없을 때만** 넣는다.

- [ ] **Step 1: 실패하는 테스트 작성**

`PortfolioBlog.Api.Tests/Features/ErrorPipelineTests.cs`(호스트 없는 미니 파이프라인 — 앱에는 일부러 예외를 던지는 엔드포인트가 없다):

```csharp
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PortfolioBlog.Api.Infrastructure.Web;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>보안 헤더·과부하 매핑·오류 본문을 실제 미들웨어 조합으로 검증한다. DB·Docker가 필요 없다.</summary>
public sealed class ErrorPipelineTests
{
    private static async Task<WebApplication> StartAsync(RequestDelegate terminal)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<OverloadExceptionHandler>();
        var app = builder.Build();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages(ErrorResponses.HandleStatusCodeAsync);
        app.Run(terminal);
        await app.StartAsync();
        return app;
    }

    /// <summary>처리되지 않은 예외의 500에도 보안 헤더가 남는다(직접 헤더 설정이면 Response.Clear()에 지워져 실패한다).</summary>
    [Fact]
    public async Task UnhandledException_500_StillCarriesSecurityHeaders()
    {
        await using var app = await StartAsync(_ => throw new InvalidOperationException("boom"));
        using var res = await app.GetTestClient().GetAsync("/api/x");
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(SecurityHeadersMiddleware.PublicCsp, res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.DoesNotContain("boom", await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>statement_timeout(57014)과 잠금 대기 초과(55P03)는 503 + Retry-After가 된다. 다른 SQL 오류는 500 그대로다.</summary>
    [Theory]
    [InlineData("57014", 503)]
    [InlineData("55P03", 503)]
    [InlineData("23505", 500)]
    public async Task PostgresOverload_MapsTo503(string sqlState, int expected)
    {
        await using var app = await StartAsync(_ => throw new PostgresException("simulated", "ERROR", "ERROR", sqlState));
        using var res = await app.GetTestClient().GetAsync("/posts/x");
        Assert.Equal(expected, (int)res.StatusCode);
        Assert.Equal(expected == 503, res.Headers.Contains("Retry-After"));
    }

    /// <summary>본문 없는 404: 공개 경로는 고정 HTML, /api는 ProblemDetails. 어느 쪽도 요청 경로를 본문에 반사하지 않는다.</summary>
    [Theory]
    [InlineData("/posts/%3Cscript%3Ealert(1)%3C/script%3E", "text/html")]
    [InlineData("/api/%3Cscript%3E", "application/problem+json")]
    public async Task EmptyStatus_GetsBodyByPath_WithoutReflectingInput(string path, string mediaType)
    {
        await using var app = await StartAsync(ctx => { ctx.Response.StatusCode = 404; return Task.CompletedTask; });
        using var res = await app.GetTestClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal(mediaType, res.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("script", await res.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }
}
```

`PortfolioBlog.Api.Tests/Features/SecurityHeadersTests.cs`:

```csharp
using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Infrastructure.Web;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>모든 응답이 스펙 3.6의 헤더를 지니는지 실제 앱 파이프라인으로 검증한다.</summary>
[Collection("postgres")]
public sealed class SecurityHeadersTests(ApiFactory factory, PostgresContainerFixture pg) : IClassFixture<ApiFactory>
{
    /// <summary>200·라우트 제약 실패 404·관리 게이트 거부 404가 전부 같은 헤더를 지닌다(2A가 남긴 "제약 실패 404에는 nosniff가 없다").</summary>
    [Theory]
    [InlineData("/health", 200)]
    [InlineData("/attachments/not-a-guid/x.png", 404)]
    [InlineData("/no-such-path", 404)]
    [InlineData("/api/posts", 404)] // 공개 호스트의 /api
    public async Task EveryResponse_CarriesBaselineHeaders(string path, int status)
    {
        using var client = factory.CreatePublicClient();
        using var res = await client.GetAsync(path);
        Assert.Equal(status, (int)res.StatusCode);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(SecurityHeadersMiddleware.PublicCsp, res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("strict-origin-when-cross-origin", res.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal(SecurityHeadersMiddleware.PermissionsPolicy, res.Headers.GetValues("Permissions-Policy").Single());
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.False(res.Headers.Contains("Set-Cookie"));
    }

    /// <summary>CSP 문자열이 스펙 3.6과 글자 그대로 같다(지시문이 하나라도 빠지면 실패).</summary>
    [Fact]
    public void PublicCsp_MatchesTheSpec() =>
        Assert.Equal("default-src 'none'; img-src 'self'; style-src 'self'; font-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'",
            SecurityHeadersMiddleware.PublicCsp);

    /// <summary>첨부 404는 자기 CSP(sandbox)를 유지한다 — 전역 미들웨어가 덮어쓰지 않는다.</summary>
    [Fact]
    public async Task AttachmentResponses_KeepTheirOwnSandboxCsp()
    {
        using var client = factory.CreatePublicClient();
        using var res = await client.GetAsync($"/attachments/{Guid.NewGuid()}/x.png");
        Assert.Equal("default-src 'none'; sandbox", res.Headers.GetValues("Content-Security-Policy").Single());
    }

    /// <summary>HSTS는 Development가 아닐 때만 붙는다(로컬 http 개발을 막지 않는다).</summary>
    [Fact]
    public async Task Hsts_OnlyOutsideDevelopment()
    {
        using (var dev = factory.CreatePublicClient())
        using (var res = await dev.GetAsync("/health"))
        {
            Assert.False(res.Headers.Contains("Strict-Transport-Security"));
        }

        using var production = new ApiFactory(pg, new Dictionary<string, string?>
        {
            ["Test:Environment"] = "Production", ["Proxy:TrustedIp"] = "172.30.0.2",
        });
        using var client = production.CreatePublicClient();
        using var prodRes = await client.GetAsync("/health");
        Assert.Equal("max-age=31536000; includeSubDomains", prodRes.Headers.GetValues("Strict-Transport-Security").Single());
    }

    /// <summary>Kestrel의 Server 헤더가 꺼져 있다(TestServer는 Kestrel이 아니므로 옵션 값으로 확인한다. 실제 응답은 최종 리뷰가 실제 호스트에서 본다).</summary>
    [Fact]
    public void KestrelServerHeader_IsDisabled()
    {
        using var _ = factory.CreateClient();
        Assert.False(factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.AddServerHeader);
    }
}
```

> 주의: Production 팩토리는 `TrustedIp`가 설정되어 ForwardedHeaders가 켜진다. `RemoteIpStartupFilter`가 넣는 원본 IP는 그대로 쓰이므로 `/health`에는 영향이 없다(`StartupValidationTests.Production_ValidSettings_Starts`와 같은 설정 + `Admin:PasswordHash`는 ApiFactory 기본값).

`PortfolioBlog.Api.Tests/Features/HostFilteringTests.cs`:

```csharp
using System.Net;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>설정된 두 호스트 밖의 Host 헤더는 앱 코드에 닿기 전에 400이다(2A가 남긴 AllowedHosts=*).</summary>
[Collection("postgres")]
public sealed class HostFilteringTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData("blog.test", 200)]
    [InlineData("admin.test", 200)]
    [InlineData("BLOG.TEST", 200)]
    [InlineData("evil.test", 400)]
    [InlineData("blog.test.evil.test", 400)]
    [InlineData("localhost", 400)]
    public async Task Host_IsRestrictedToTheTwoConfiguredHosts(string host, int expected)
    {
        using var client = factory.CreatePublicClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/health");
        req.Headers.Host = host;
        using var res = await client.SendAsync(req);
        Assert.Equal(expected, (int)res.StatusCode);
    }
}
```

`PortfolioBlog.Api.Tests/Features/ApiBodyLimitTests.cs`:

```csharp
using System.Net;
using System.Text;
using PortfolioBlog.Api.Infrastructure.Web;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>관리 JSON 본문 256KB 상한(스펙 3.7). TestServer는 Kestrel의 MaxRequestBodySize를 집행하지 않으므로 앱 미들웨어가 직접 센다.</summary>
[Collection("postgres")]
public sealed class ApiBodyLimitTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static string Json(int markdownChars) => "{\"markdown\":\"" + new string('a', markdownChars) + "\"}";

    /// <summary>Content-Length가 상한을 넘으면 본문을 읽지 않고 413.</summary>
    [Fact]
    public async Task DeclaredLength_OverLimit_Is413()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        using var res = await client.PostAsync("/api/preview", new StringContent(Json(300_000), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>길이를 선언하지 않은(chunked) 본문도 읽는 도중 상한에서 끊긴다 — Content-Length 검사만 있으면 이 테스트가 실패한다.</summary>
    [Fact]
    public async Task UndeclaredLength_OverLimit_Is413()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        var content = new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes(Json(300_000))));
        content.Headers.ContentType = new("application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/preview") { Content = content };
        req.Headers.TransferEncodingChunked = true;
        using var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
    }

    /// <summary>상한 바로 아래는 통과한다(200KB 본문 검증이 여전히 도달 가능하다): 210,000자는 200KB 검증에 걸려 400, 413이 아니다.</summary>
    [Fact]
    public async Task UnderLimit_ReachesValidation()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        using var res = await client.PostAsync("/api/preview", new StringContent(Json(210_000), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.True(Json(210_000).Length < ApiBodyLimitMiddleware.JsonLimitBytes);
    }

    /// <summary>세션이 없으면 큰 본문이어도 401이 먼저다(접근 계약이 본문 크기 검사보다 앞).</summary>
    [Fact]
    public async Task NoSession_Gets401_NotA413()
    {
        using var client = factory.CreateAdminClient();
        using var res = await client.PostAsync("/api/preview", new StringContent(Json(300_000), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // HttpClient가 길이를 미리 알 수 없게 하는 스트림(Length 미지원).
    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }
}
```

> 업로드(multipart, 자체 11MB 상한)는 기존 `AttachmentEndpointsTests`가 계속 통과해야 한다 — 256KB 상한이 업로드에 걸리면 그 테스트들이 413으로 실패한다(회귀 방지 장치).

- [ ] **Step 2: 실패 확인**

Run: `dotnet build PortfolioBlog.slnx -c Release`
Expected: 컴파일 오류 — `SecurityHeadersMiddleware`·`ErrorResponses`·`OverloadExceptionHandler`·`ApiBodyLimitMiddleware`가 없다.

- [ ] **Step 3: 구현**

`SecurityHeadersMiddleware.cs`:

```csharp
namespace PortfolioBlog.Api.Infrastructure.Web;

/// <summary>모든 응답(라우트 제약 실패 404·예외 500 포함)에 기준 보안 헤더를 붙인다(스펙 3.6).</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, IHostEnvironment environment)
{
    /// <summary>공개 HTML의 CSP(스펙 3.6). 스크립트 출처가 아예 없다. JSON·XML·오류 응답에도 같은 값을 쓴다(더 엄격해서 나쁠 것이 없다).</summary>
    public const string PublicCsp =
        "default-src 'none'; img-src 'self'; style-src 'self'; font-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";

    /// <summary>브라우저 기능 전부 비활성.</summary>
    public const string PermissionsPolicy =
        "accelerometer=(), autoplay=(), camera=(), display-capture=(), encrypted-media=(), fullscreen=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), midi=(), payment=(), picture-in-picture=(), publickey-credentials-get=(), screen-wake-lock=(), usb=(), xr-spatial-tracking=()";

    // HSTS는 TLS 종단(Caddy) 뒤에서만 의미가 있다. Development는 http 프로필로도 뜨므로 붙이지 않는다.
    private readonly bool _hsts = !environment.IsDevelopment();

    public Task InvokeAsync(HttpContext context)
    {
        // OnStarting: 예외 처리 미들웨어가 Response.Clear()로 헤더를 비운 뒤에도 전송 직전에 다시 실행된다(직접 넣으면 500에서 사라진다).
        context.Response.OnStarting(static state =>
        {
            var (headers, hsts) = ((IHeaderDictionary, bool))state;
            headers.XContentTypeOptions = "nosniff";
            headers.TryAdd("Content-Security-Policy", PublicCsp); // 첨부 핸들러가 넣은 sandbox CSP는 덮어쓰지 않는다
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Permissions-Policy"] = PermissionsPolicy;
            headers.XFrameOptions = "DENY";
            if (hsts) headers.StrictTransportSecurity = "max-age=31536000; includeSubDomains";
            return Task.CompletedTask;
        }, (context.Response.Headers, _hsts));
        return next(context);
    }
}
```

> `Cross-Origin-Resource-Policy`는 **붙이지 않는다**: 미리보기 iframe의 이미지는 관리 오리진에서 읽히고, `same-origin`이면 Plan 3의 미리보기가 깨진다(2A 핸드오프).

`ErrorResponses.cs`:

```csharp
using System.Net;
using Microsoft.AspNetCore.Diagnostics;

namespace PortfolioBlog.Api.Infrastructure.Web;

/// <summary>본문 없는 오류 응답의 본문을 경로에 따라 쓴다: 기계가 읽는 표면은 ProblemDetails, 사람이 보는 공개 표면은 고정 HTML.</summary>
public static class ErrorResponses
{
    private static readonly PathString[] MachinePrefixes = [new("/api"), new("/attachments"), new("/health"), new("/openapi")];

    /// <summary><c>UseStatusCodePages</c> 콜백.</summary>
    public static Task HandleStatusCodeAsync(StatusCodeContext context) =>
        WriteAsync(context.HttpContext, context.HttpContext.Response.StatusCode);

    /// <summary>상태 코드에 맞는 본문을 쓴다. 요청의 어떤 값도 본문에 넣지 않는다(반사 없음).</summary>
    public static async Task WriteAsync(HttpContext http, int status)
    {
        if (MachinePrefixes.Any(p => http.Request.Path.StartsWithSegments(p)))
        {
            await http.RequestServices.GetRequiredService<IProblemDetailsService>()
                .TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = { Status = status } });
            return;
        }
        http.Response.ContentType = "text/html; charset=utf-8";
        if (HttpMethods.IsHead(http.Request.Method)) return;
        await http.Response.WriteAsync(Html(status));
    }

    private static string Html(int status)
    {
        var message = status switch
        {
            400 => "요청을 이해할 수 없습니다.",
            404 => "페이지를 찾을 수 없습니다.",
            405 => "허용되지 않는 요청 방식입니다.",
            429 => "요청이 너무 많습니다. 잠시 후 다시 시도해 주세요.",
            503 => "서버가 바쁩니다. 잠시 후 다시 시도해 주세요.",
            _ => "요청을 처리하지 못했습니다.",
        };
        // status는 정수, message는 위 상수뿐이다. HtmlEncode는 "나중에 누가 입력값을 넣어도" 안전하도록 남긴 이중 방어다.
        return $"""
            <!doctype html>
            <html lang="ko"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="robots" content="noindex"><title>{status}</title><link rel="stylesheet" href="/css/site.css"></head>
            <body><main class="error-page"><h1>{status}</h1><p>{WebUtility.HtmlEncode(message)}</p><p><a href="/">처음으로</a></p></main></body></html>
            """;
    }
}
```

`OverloadExceptionHandler.cs`:

```csharp
using Microsoft.AspNetCore.Diagnostics;
using Npgsql;

namespace PortfolioBlog.Api.Infrastructure.Web;

/// <summary>과부하로 포기한 요청을 500이 아니라 503 + Retry-After로 돌려준다: statement_timeout(57014), 잠금 대기 초과(55P03).</summary>
public sealed class OverloadExceptionHandler : IExceptionHandler
{
    /// <summary>503 응답의 <c>Retry-After</c>(초).</summary>
    public const int RetryAfterSeconds = 5;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (!IsOverload(exception)) return false;
        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        httpContext.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await ErrorResponses.WriteAsync(httpContext, StatusCodes.Status503ServiceUnavailable);
        return true;
    }

    // 클라이언트가 끊어서 취소된 명령은 Npgsql이 OperationCanceledException으로 바꾸므로, 여기 오는 57014는 statement_timeout뿐이다.
    internal static bool IsOverload(Exception exception) =>
        exception is PostgresException { SqlState: "57014" or "55P03" };
}
```

`ApiBodyLimitMiddleware.cs`:

```csharp
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;

namespace PortfolioBlog.Api.Infrastructure.Web;

/// <summary><c>/api</c>의 JSON 본문을 256KB로 제한한다(스펙 3.7). 자체 상한을 선언한 엔드포인트(업로드의 <c>RequestSizeLimit</c>)는 건드리지 않는다.</summary>
public sealed class ApiBodyLimitMiddleware(RequestDelegate next)
{
    /// <summary>관리 JSON 본문 상한(직렬화 후 바이트).</summary>
    public const long JsonLimitBytes = 262_144;

    private static readonly PathString ApiPrefix = new("/api");

    public Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(ApiPrefix)
            || context.GetEndpoint()?.Metadata.GetMetadata<IRequestSizeLimitMetadata>() is not null)
        {
            return next(context);
        }
        if (context.Request.ContentLength > JsonLimitBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return ErrorResponses.WriteAsync(context, StatusCodes.Status413PayloadTooLarge);
        }
        // Kestrel에서는 서버가 직접 끊는다(TestServer에는 이 기능이 없어 null이다).
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature) feature.MaxRequestBodySize = JsonLimitBytes;
        // 길이를 선언하지 않은(chunked) 본문: 읽는 쪽에서 센다. 최소 API의 JSON 바인딩은 BadHttpRequestException의 상태 코드를 그대로 응답한다(실측 413).
        context.Request.Body = new LengthLimitedStream(context.Request.Body, JsonLimitBytes);
        return next(context);
    }

    // Stream 래퍼: 복사 없이 안쪽 스트림의 읽기를 그대로 넘기며 누적 바이트만 센다(버퍼를 새로 잡지 않는다).
    private sealed class LengthLimitedStream(Stream inner, long limit) : Stream
    {
        private long _total;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int read)
        {
            _total += read;
            return _total > limit
                ? throw new BadHttpRequestException("요청 본문이 상한을 넘었습니다.", StatusCodes.Status413PayloadTooLarge)
                : read;
        }
    }
}
```

`Program.cs`:

```csharp
// (using 추가) Microsoft.AspNetCore.HostFiltering, Microsoft.Extensions.Options

// Kestrel: 서버 제품명 헤더를 내지 않는다.
builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);
// 호스트 필터: 설정 파일의 AllowedHosts("*") 대신 설정된 두 origin의 호스트만 받는다(지연 바인딩 — Build 이후 첫 해석).
// 프레임워크의 기본 PostConfigure는 목록이 비어 있을 때만 "*"로 채우므로 이 값이 이긴다(실측).
builder.Services.AddOptions<HostFilteringOptions>().Configure<IOptions<SiteOptions>>((o, site) =>
{
    o.AllowedHosts = new[] { SiteOptions.HostOf(site.Value.PublicOrigin), SiteOptions.HostOf(site.Value.AdminOrigin) }
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    o.AllowEmptyHosts = false;
});
builder.Services.AddExceptionHandler<OverloadExceptionHandler>();
```

파이프라인(기존 `app.UseTrustedForwardedHeaders();`부터 `app.UseAuthorization();`까지를 교체):

```csharp
app.UseMiddleware<SecurityHeadersMiddleware>(); // 맨 앞: 뒤의 어떤 미들웨어가 응답을 끝내도 헤더가 붙는다
app.UseTrustedForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages(ErrorResponses.HandleStatusCodeAsync);
app.UseMiddleware<AdminSurfaceMiddleware>();
app.UseRateLimiter();      // IP 검사 뒤: 외부 요청이 로그인 한도를 소진하지 못한다
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<ApiBodyLimitMiddleware>(); // 인가 뒤: 세션 없는 요청은 크기와 무관하게 401이 먼저다
```

`appsettings.json`의 `"AllowedHosts": "*"` 줄은 **삭제**한다(더는 쓰이지 않는 값이 "전부 허용"처럼 보이면 안 된다).
`HealthEndpointTests`: `_factory.CreateClient()`로 요청을 보내는 곳을 `_factory.CreatePublicClient()`로 바꾼다(기본 클라이언트의 Host는 `localhost`라 이제 400이다). 다른 테스트에서 같은 이유로 실패가 나오면 같은 방식으로 고친다 — 호스트 기동만 하려는 `using var _ = factory.CreateClient();`는 요청을 보내지 않으므로 그대로 둔다.

- [ ] **Step 4: 통과 확인**

Run: `dotnet test PortfolioBlog.slnx -c Release --filter "FullyQualifiedName~ErrorPipelineTests|FullyQualifiedName~SecurityHeadersTests|FullyQualifiedName~HostFilteringTests|FullyQualifiedName~ApiBodyLimitTests"`
Expected: 전부 통과. **규칙 8 확인:** `SecurityHeadersMiddleware`를 잠깐 "OnStarting 없이 직접 설정"으로 바꿔 `UnhandledException_500_StillCarriesSecurityHeaders`가 실패하는 것을 본 뒤 되돌린다.

- [ ] **Step 5: 전체 회귀 + 커밋**

Run: `dotnet build PortfolioBlog.slnx -c Release`(경고 0) → `dotnet test PortfolioBlog.slnx -c Release`

```bash
git add -A
git commit -m "추가: 전역 보안 헤더와 호스트 제한, 과부하 503, 관리 JSON 본문 256KB 상한

- 모든 응답(제약 실패 404·예외 500 포함)에 CSP·nosniff·Referrer-Policy·Permissions-Policy, 운영에서 HSTS
- AllowedHosts를 설정된 두 호스트로 제한, Kestrel Server 헤더 제거
- statement_timeout·잠금 대기 초과는 503 + Retry-After, 공개 경로의 오류는 입력을 반사하지 않는 고정 HTML
- /api JSON 본문은 선언 길이·chunked 모두 256KB에서 413

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: 렌더 비용 — 주입 가능한 시계 · 렌더 게이트 · 렌더 캐시 · 저장/미리보기 경로 · 워밍업

**Files:**
- Modify: `PortfolioBlog.Api/Infrastructure/Markdown/MarkdownRenderer.cs`, `HighlightingCodeBlockRenderer.cs`
- Create: `PortfolioBlog.Api/Infrastructure/Markdown/RenderingOptions.cs`, `RenderGate.cs`, `RenderedPostCache.cs`
- Modify: `PortfolioBlog.Api/Features/Posts/PostEndpoints.cs`, `PortfolioBlog.Api/Features/Preview/PreviewEndpoints.cs`, `PortfolioBlog.Api/Infrastructure/Web/OverloadExceptionHandler.cs`, `PortfolioBlog.Api/Infrastructure/Access/StartupValidation.cs`, `PortfolioBlog.Api/Program.cs`
- Test: `PortfolioBlog.Api.Tests/Infrastructure/SteppingTimeProvider.cs`, `RenderGateTests.cs`, `RenderedPostCacheTests.cs`; Modify `MarkdownRendererTests.cs`, `PortfolioBlog.Api.Tests/Features/ErrorPipelineTests.cs`

**Interfaces:**
- Consumes: `MarkdownRenderer.Render`, `MarkdownTooComplexException`, `OverloadExceptionHandler.IsOverload`.
- Produces:
  - `public sealed record RenderedMarkdown(string Html, string? FirstImageUrl, bool HighlightTimedOut)`
  - `MarkdownRenderer()`(시스템 시계), `MarkdownRenderer(TimeProvider clock)`, `RenderedMarkdown RenderDetailed(string markdown)`, `string Render(string markdown)`(= `RenderDetailed(markdown).Html`)
  - `HighlightingCodeBlockRenderer(TimeProvider clock)`, `bool TimedOut { get; }`
  - `RenderingOptions { Concurrency=2, QueueTimeoutMs=5000, CacheMegabytes=64 }`, `SectionName = "Rendering"`
  - `RenderGate.RenderAsync(string markdown, CancellationToken ct) : Task<RenderedMarkdown>`, `RenderGate.RenderCount : long`(internal, 테스트 관측용), `RenderBusyException`
  - `RenderedPostCache.TryGet(Guid postId, uint version, out RenderedMarkdown rendered) : bool`, `GetOrRenderAsync(Guid postId, uint version, string markdown, CancellationToken ct) : Task<RenderedMarkdown>`, `Store(Guid postId, uint version, RenderedMarkdown rendered)`

**배경(측정값, 2A 보고서 6절):** 렌더링은 동기·취소 불가다. 코드 강조는 렌더당 약 2.25초가 상한이지만 Markdig 파서는 적대적 200KB에서 인라인 약 8.5초, 블록 약 6.6초다. 평범한 153KB 글도 1.1~1.3초다. 그래서 (a) 동시에 도는 렌더 수를 프로세스 전체에서 묶고(미리보기·저장·공개 페이지 공통), (b) 공개 글은 버전당 한 번만 렌더링한다.

- [ ] **Step 1: 시간 의존 테스트를 주입 시계로 교체(실패하는 테스트)**

`PortfolioBlog.Api.Tests/Infrastructure/SteppingTimeProvider.cs`:

```csharp
namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary><see cref="GetTimestamp"/>를 부를 때마다 고정 간격만큼 전진하는 가짜 시계. 기계 속도와 무관하게 "시간 예산 소진"을 재현한다.</summary>
internal sealed class SteppingTimeProvider(long stepMilliseconds) : TimeProvider
{
    // Interlocked: 렌더러가 다른 스레드에서 읽어도 찢어지지 않게 한다(테스트는 단일 스레드지만 시계 계약을 지킨다).
    private long _now;

    /// <summary>1틱 = 1ms.</summary>
    public override long TimestampFrequency => 1000;

    public override long GetTimestamp() => Interlocked.Add(ref _now, stepMilliseconds);
}
```

`MarkdownRendererTests.cs`: `Render_ManyFastBlocks_AreStoppedByTheRenderClock`를 **삭제**하고(약 6배 빠른 기계에서 거짓 실패한다 — 2A 보고서 7절) 아래로 바꾼다. 그 테스트를 가리키던 `Render_ThreeExponentialBlocks…` 요약의 `<see cref>`도 새 이름으로 고친다:

```csharp
    /// <summary>강조 시간 예산이 "렌더 1회에 걸친 누적"임을 기계 속도와 무관하게 증명한다. 시계가 호출마다 50ms 전진하므로 블록 하나가
    /// 최소 100ms(시작·종료 두 번)를 쓰고, 60블록이면 2,000ms 예산을 반드시 넘는다. 블록마다 예산이 따로면 마지막 블록도 강조되어 실패한다.</summary>
    [Fact]
    public void Render_TimeBudget_IsCumulativePerRender_Deterministically()
    {
        var renderer = new MarkdownRenderer(new SteppingTimeProvider(50));
        var markdown = string.Concat(Enumerable.Repeat("```csharp\nvar a = 1;\n```\n\n", 60));

        var result = renderer.RenderDetailed(markdown);

        var pres = Parse(result.Html).QuerySelectorAll("pre");
        Assert.Equal(60, pres.Length);
        Assert.NotNull(pres[0].QuerySelector("span.keyword"));  // 첫 블록은 강조됨
        Assert.Equal("code", pres[^1].FirstElementChild?.LocalName); // 마지막 블록은 평문 경로
        Assert.Null(pres[^1].QuerySelector("span"));
        Assert.True(result.HighlightTimedOut);

        // 같은 렌더러의 다음 렌더는 새 예산으로 시작한다.
        var again = renderer.RenderDetailed("```csharp\nvar a = 1;\n```\n");
        Assert.False(again.HighlightTimedOut);
        Assert.NotNull(Parse(again.Html).QuerySelector("span.keyword"));
    }

    /// <summary>실제 시계에서는 같은 문서가 전부 강조되고 시간 초과 표시가 없다(가짜 시계 테스트가 입력 때문에 통과한 것이 아님을 보인다).</summary>
    [Fact]
    public void Render_SameDocument_OnSystemClock_IsFullyHighlighted()
    {
        var result = new MarkdownRenderer().RenderDetailed(string.Concat(Enumerable.Repeat("```csharp\nvar a = 1;\n```\n\n", 60)));
        Assert.False(result.HighlightTimedOut);
        Assert.Equal(60, Parse(result.Html).QuerySelectorAll("span.keyword").Length);
    }

    /// <summary>첫 이미지는 URL 정책을 통과한 것만 돌려준다(외부 이미지는 링크가 풀리므로 후보가 아니다).</summary>
    [Fact]
    public void RenderDetailed_FirstImageUrl_IsTheFirstAllowedAttachment()
    {
        const string attachment = "/attachments/01234567-89ab-cdef-0123-456789abcdef/a.png";
        var result = new MarkdownRenderer().RenderDetailed($"![x](https://evil.test/p.png)\n\n![y]({attachment})\n");
        Assert.Equal(attachment, result.FirstImageUrl);
        Assert.Null(new MarkdownRenderer().RenderDetailed("글만 있다").FirstImageUrl);
    }
```

Run: `dotnet build PortfolioBlog.slnx -c Release` → Expected: 컴파일 오류(`MarkdownRenderer(TimeProvider)`·`RenderDetailed` 없음).

- [ ] **Step 2: 렌더러에 시계 주입 + 상세 결과**

`HighlightingCodeBlockRenderer.cs`:
- 선언을 `public sealed class HighlightingCodeBlockRenderer(TimeProvider clock) : HtmlObjectRenderer<CodeBlock>`로 바꾸고 `using System.Diagnostics;`를 지운다.
- 속성 추가: `/// <summary>이 렌더에서 시간 때문에(시간 예산 소진·매치 타임아웃) 강조를 포기한 블록이 하나라도 있었는가. 길이 예산·모르는 언어는 결정적이라 포함하지 않는다.</summary> public bool TimedOut { get; private set; }`
- `Write` 본문 교체(예산 판정을 둘로 나눈다):

```csharp
        if (!withinLengthBudget)
        {
            WritePlainEscaped(renderer, code);
            return;
        }
        if (_highlightMilliseconds >= MaxHighlightMilliseconds)
        {
            TimedOut = true;
            WritePlainEscaped(renderer, code);
            return;
        }

        // TimeProvider.GetTimestamp(): 시스템 시계에서는 Stopwatch의 고해상도 틱을 그대로 읽는다(가상 호출 1회가 더 붙을 뿐). 테스트는 전진하는 가짜 시계를 넣는다.
        var startTicks = clock.GetTimestamp();
        var elapsedBeforeThisBlock = _highlightMilliseconds;
        try
        {
            var deadlineParser = new DeadlineLanguageParser(SharedParser,
                () => elapsedBeforeThisBlock + clock.GetElapsedTime(startTicks).TotalMilliseconds >= MaxHighlightMilliseconds);
            var html = new HtmlClassFormatter(languageParser: deadlineParser).GetHtmlString(code.ToString(), language!);
            _highlightedLength += code.Length;
            renderer.Write(html);
            renderer.Write("\n");
        }
        catch (RegexMatchTimeoutException)
        {
            TimedOut = true;
            WritePlainEscaped(renderer, code);
        }
        catch (HighlightBudgetExceededException)
        {
            TimedOut = true;
            WritePlainEscaped(renderer, code);
        }
        finally
        {
            _highlightMilliseconds += clock.GetElapsedTime(startTicks).TotalMilliseconds;
        }
```

> 정규식 매치 타임아웃(250ms)은 .NET 정규식 엔진이 실제 시계로 재므로 주입 대상이 아니다. 주석에 그렇게 적는다.

`MarkdownRenderer.cs`:

```csharp
/// <summary>렌더 1회의 결과.</summary>
/// <param name="Html">허용 목록만 남은 정제된 HTML.</param>
/// <param name="FirstImageUrl">본문에서 URL 정책을 통과한 첫 이미지의 경로(<c>/attachments/…</c>). 없으면 <c>null</c>. OG 이미지에 쓴다.</param>
/// <param name="HighlightTimedOut">시간 때문에 강조를 포기한 코드블록이 있었는가. 호출부가 이 결과를 오래 캐시하지 않도록 알린다.</param>
public sealed record RenderedMarkdown(string Html, string? FirstImageUrl, bool HighlightTimedOut);
```

클래스에 시계 필드와 생성자 둘, `RenderDetailed`를 추가하고 `Render`는 위임으로 바꾼다:

```csharp
    private readonly TimeProvider _clock;

    /// <summary>시스템 시계로 만든다.</summary>
    public MarkdownRenderer() : this(TimeProvider.System) { }

    /// <summary>강조 시간 예산을 잴 시계를 지정한다(테스트가 결정적 시계를 넣는다).</summary>
    public MarkdownRenderer(TimeProvider clock) => _clock = clock;

    public string Render(string markdown) => RenderDetailed(markdown).Html;

    public RenderedMarkdown RenderDetailed(string markdown)
    {
        // (기존 Render의 null·크기 검사 그대로)
        try
        {
            var document = Markdig.Markdown.Parse(markdown, _pipeline);
            ApplyUrlPolicy(document);
            HeadingIds.Assign(document);
            // ApplyUrlPolicy 뒤라서 남아 있는 이미지는 전부 정책을 통과한 자체 첨부다.
            var firstImage = document.Descendants<LinkInline>().FirstOrDefault(static l => l.IsImage)?.Url;

            using var writer = new StringWriter();
            var renderer = new HtmlRenderer(writer);
            _pipeline.Setup(renderer);
            if (renderer.ObjectRenderers.FindExact<CodeBlockRenderer>() is { } builtIn) renderer.ObjectRenderers.Remove(builtIn);
            var highlighter = new HighlightingCodeBlockRenderer(_clock);
            renderer.ObjectRenderers.Add(highlighter);
            renderer.Render(document);
            writer.Flush();
            return new RenderedMarkdown(_sanitizer.Sanitize(writer.ToString()), firstImage, highlighter.TimedOut);
        }
        catch (ArgumentException ex)
        {
            throw new MarkdownTooComplexException("마크다운 구조가 너무 깊게 중첩됐습니다(중첩 한도 128).", ex);
        }
    }
```

`new HighlightingCodeBlockRenderer()`를 쓰는 다른 곳(테스트 포함)은 `new HighlightingCodeBlockRenderer(TimeProvider.System)`으로 고친다(`Grep`으로 찾는다).

`Program.cs`: `builder.Services.AddSingleton<MarkdownRenderer>();` →

```csharp
// 시스템 시계를 명시한다: 통합 테스트는 DI의 TimeProvider를 세션 만료용 가짜 시계로 바꾸는데, 렌더 시간 예산은 그 영향을 받으면 안 된다.
builder.Services.AddSingleton(_ => new MarkdownRenderer(TimeProvider.System));
```

Run: `dotnet test PortfolioBlog.slnx -c Release --filter "FullyQualifiedName~MarkdownRendererTests"` → Expected: 전부 통과.

- [ ] **Step 3: 게이트·캐시 실패 테스트**

`PortfolioBlog.Api.Tests/Infrastructure/RenderGateTests.cs`:

```csharp
using PortfolioBlog.Api.Infrastructure.Markdown;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>렌더 게이트의 동시 실행 상한과 대기 상한을 가짜 렌더 함수로 결정적으로 검증한다.</summary>
public sealed class RenderGateTests
{
    /// <summary>슬롯이 차 있으면 대기 시간 뒤 <see cref="RenderBusyException"/>이고, 슬롯이 풀리면 다시 받는다.</summary>
    [Fact]
    public async Task FullGate_RejectsAfterQueueTimeout_ThenRecovers()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var gate = new RenderGate(md =>
        {
            if (md == "slow") { entered.Set(); release.Wait(TimeSpan.FromSeconds(30)); }
            return new RenderedMarkdown(md, null, false);
        }, concurrency: 1, queueTimeout: TimeSpan.FromMilliseconds(50));

        var slow = Task.Run(() => gate.RenderAsync("slow", CancellationToken.None));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

        await Assert.ThrowsAsync<RenderBusyException>(() => gate.RenderAsync("second", CancellationToken.None));

        release.Set();
        await slow;
        Assert.Equal("third", (await gate.RenderAsync("third", CancellationToken.None)).Html);
        Assert.Equal(2, gate.RenderCount); // 거부된 요청은 렌더링하지 않았다
    }

    /// <summary>렌더가 예외를 던져도 슬롯이 반납된다(반납되지 않으면 두 번째 호출이 RenderBusyException이 된다).</summary>
    [Fact]
    public async Task ThrowingRender_ReleasesTheSlot()
    {
        using var gate = new RenderGate(_ => throw new MarkdownTooComplexException("x", new ArgumentException()), 1, TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<MarkdownTooComplexException>(() => gate.RenderAsync("a", CancellationToken.None));
        await Assert.ThrowsAsync<MarkdownTooComplexException>(() => gate.RenderAsync("b", CancellationToken.None));
    }
}
```

`PortfolioBlog.Api.Tests/Infrastructure/RenderedPostCacheTests.cs`:

```csharp
using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Infrastructure.Markdown;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>글 버전당 한 번만 렌더링되는지(캐시 + 단일 비행) 검증한다.</summary>
public sealed class RenderedPostCacheTests
{
    private static readonly IOptions<RenderingOptions> Options = Microsoft.Extensions.Options.Options.Create(new RenderingOptions());

    /// <summary>같은 글을 동시에 10번 요청해도 렌더는 한 번이다. 단일 비행이 없으면 렌더 수가 10(또는 게이트 거부)이 되어 실패한다.</summary>
    [Fact]
    public async Task ConcurrentRequests_ForTheSameVersion_RenderOnce()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var gate = new RenderGate(md => { entered.Set(); release.Wait(TimeSpan.FromSeconds(30)); return new RenderedMarkdown("<p>" + md + "</p>", null, false); },
            concurrency: 2, queueTimeout: TimeSpan.FromSeconds(30));
        using var cache = new RenderedPostCache(gate, Options);
        var id = Guid.NewGuid();

        var calls = Enumerable.Range(0, 10).Select(_ => cache.GetOrRenderAsync(id, 7, "본문", CancellationToken.None)).ToArray();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, gate.RenderCount); // 렌더가 아직 끝나지 않은 시점: 나머지 9개는 같은 작업에 매달려 있다
        release.Set();

        var results = await Task.WhenAll(calls);
        Assert.All(results, r => Assert.Same(results[0], r));
        Assert.Equal(1, gate.RenderCount);

        Assert.True(cache.TryGet(id, 7, out var hit));
        Assert.Same(results[0], hit);
        Assert.False(cache.TryGet(id, 8, out _)); // 버전이 바뀌면 미스
    }

    /// <summary>한 호출자의 취소는 그 호출자만 끝내고 공유 렌더는 계속된다.</summary>
    [Fact]
    public async Task CallerCancellation_DoesNotCancelTheSharedRender()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var gate = new RenderGate(md => { entered.Set(); release.Wait(TimeSpan.FromSeconds(30)); return new RenderedMarkdown(md, null, false); }, 1, TimeSpan.FromSeconds(30));
        using var cache = new RenderedPostCache(gate, Options);
        var id = Guid.NewGuid();
        using var cts = new CancellationTokenSource();

        var cancelled = cache.GetOrRenderAsync(id, 1, "a", cts.Token);
        var patient = cache.GetOrRenderAsync(id, 1, "a", CancellationToken.None);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        release.Set();
        Assert.Equal("a", (await patient).Html);
    }

    /// <summary>렌더 실패(과부하)는 캐시되지 않는다 — 다음 요청이 다시 시도한다.</summary>
    [Fact]
    public async Task FailedRender_IsNotCached()
    {
        var attempts = 0;
        using var gate = new RenderGate(md => Interlocked.Increment(ref attempts) == 1 ? throw new InvalidOperationException("첫 시도 실패") : new RenderedMarkdown(md, null, false), 1, TimeSpan.FromSeconds(5));
        using var cache = new RenderedPostCache(gate, Options);
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetOrRenderAsync(id, 1, "a", CancellationToken.None));
        Assert.Equal("a", (await cache.GetOrRenderAsync(id, 1, "a", CancellationToken.None)).Html);
    }
}
```

`ErrorPipelineTests.PostgresOverload_MapsTo503` 아래에 추가:

```csharp
    /// <summary>렌더 게이트가 가득 차 포기한 요청도 503 + Retry-After다.</summary>
    [Fact]
    public async Task RenderBusy_MapsTo503()
    {
        await using var app = await StartAsync(_ => throw new PortfolioBlog.Api.Infrastructure.Markdown.RenderBusyException());
        using var res = await app.GetTestClient().GetAsync("/api/preview");
        Assert.Equal(503, (int)res.StatusCode);
        Assert.True(res.Headers.Contains("Retry-After"));
    }
```

Run: `dotnet build PortfolioBlog.slnx -c Release` → Expected: 컴파일 오류(`RenderGate`·`RenderedPostCache`·`RenderBusyException` 없음).

- [ ] **Step 4: 게이트·캐시 구현**

`RenderingOptions.cs`:

```csharp
namespace PortfolioBlog.Api.Infrastructure.Markdown;

/// <summary>설정 섹션 <c>Rendering</c>. 마크다운 렌더링의 CPU·메모리 예산.</summary>
public sealed class RenderingOptions
{
    /// <summary>설정 섹션 이름.</summary>
    public const string SectionName = "Rendering";

    /// <summary>프로세스 전체에서 동시에 도는 렌더 수(미리보기·글 저장·공개 페이지 합산). 1~64.</summary>
    public int Concurrency { get; set; } = 2;

    /// <summary>슬롯을 기다리는 최대 시간(밀리초). 넘으면 503. 1~60000.</summary>
    public int QueueTimeoutMs { get; set; } = 5000;

    /// <summary>렌더 결과 캐시의 메모리 상한(MB, HTML 문자열 기준). 1~1024.</summary>
    public int CacheMegabytes { get; set; } = 64;
}
```

`RenderGate.cs`:

```csharp
using Microsoft.Extensions.Options;

namespace PortfolioBlog.Api.Infrastructure.Markdown;

/// <summary>렌더 슬롯을 <see cref="RenderingOptions.QueueTimeoutMs"/> 안에 얻지 못했다. 호출부(예외 처리기)가 503 + Retry-After로 바꾼다.</summary>
public sealed class RenderBusyException() : Exception("렌더 슬롯을 제때 얻지 못했습니다.");

/// <summary>모든 마크다운 렌더링이 지나는 문. 렌더링은 동기·취소 불가 CPU 작업이라(최악 수 초) 동시에 도는 수를 프로세스 전체에서 묶는다.</summary>
public sealed class RenderGate : IDisposable
{
    private readonly Func<string, RenderedMarkdown> _render;

    // SemaphoreSlim: WaitAsync가 대기자를 스레드 점유 없이 내부 큐(연결 리스트)에 세워 두는 관리형 세마포어다. 기다리는 요청은 스레드 풀 스레드를 쓰지 않는다.
    private readonly SemaphoreSlim _slots;
    private readonly TimeSpan _queueTimeout;

    // long + Interlocked: 테스트가 "렌더가 실제로 몇 번 돌았나"를 락 없이 관측한다.
    private long _renderCount;

    public RenderGate(MarkdownRenderer renderer, IOptions<RenderingOptions> options)
        : this(renderer.RenderDetailed, options.Value.Concurrency, TimeSpan.FromMilliseconds(options.Value.QueueTimeoutMs)) { }

    /// <summary>테스트용: 렌더 함수를 바꿔 끼운다(DI는 public 생성자만 본다).</summary>
    internal RenderGate(Func<string, RenderedMarkdown> render, int concurrency, TimeSpan queueTimeout)
    {
        _render = render;
        _slots = new SemaphoreSlim(concurrency, concurrency);
        _queueTimeout = queueTimeout;
    }

    /// <summary>지금까지 실제로 실행한 렌더 수(거부된 요청 제외).</summary>
    internal long RenderCount => Interlocked.Read(ref _renderCount);

    /// <summary>슬롯을 얻어 렌더링한다. 슬롯 대기만 비동기·취소 가능하고, 렌더 자체는 호출 스레드(대기했다면 스레드 풀 스레드)에서 동기로 돈다.</summary>
    /// <exception cref="RenderBusyException">대기 상한 안에 슬롯을 얻지 못했다.</exception>
    /// <exception cref="MarkdownTooComplexException">입력의 중첩이 너무 깊다(렌더러가 던진 것을 그대로 전파).</exception>
    public async Task<RenderedMarkdown> RenderAsync(string markdown, CancellationToken ct)
    {
        if (!await _slots.WaitAsync(_queueTimeout, ct)) throw new RenderBusyException();
        try
        {
            Interlocked.Increment(ref _renderCount);
            return _render(markdown);
        }
        finally
        {
            _slots.Release();
        }
    }

    public void Dispose() => _slots.Dispose();
}
```

`RenderedPostCache.cs`:

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace PortfolioBlog.Api.Infrastructure.Markdown;

/// <summary>공개 글의 렌더 결과를 <c>(PostId, xmin)</c>으로 캐시하고, 같은 키의 동시 미스를 렌더 한 번으로 합친다(단일 비행).</summary>
/// <remarks>프로세스 메모리에만 둔다 — 재배포하면 비워지므로 렌더러 보안 수정이 과거 글 전체에 즉시 적용된다(스펙 3.2). HTML을 DB에 저장하지 않는다.</remarks>
public sealed class RenderedPostCache : IDisposable
{
    /// <summary>정상 렌더의 캐시 수명. 키에 xmin이 들어 있어 수정되면 어차피 미스다 — 이 값은 죽은 항목이 남는 시간의 상한이다.</summary>
    public static readonly TimeSpan NormalLifetime = TimeSpan.FromHours(24);

    /// <summary>시간 때문에 강조가 빠진 렌더의 수명. 서버가 한가해지면 곧 제대로 된 결과로 바뀐다.</summary>
    public static readonly TimeSpan DegradedLifetime = TimeSpan.FromMinutes(2);

    private readonly RenderGate _gate;

    // MemoryCache(SizeLimit): 항목마다 Size를 주면 합계가 상한을 넘을 때 우선순위·LRU로 비운다. 공용 IMemoryCache가 아닌 전용 인스턴스라 다른 용도와 예산이 섞이지 않는다.
    private readonly MemoryCache _cache;

    // ConcurrentDictionary<키, Lazy<Task>>: GetOrAdd는 값 팩토리를 여러 번 부를 수 있지만 저장되는 Lazy는 하나고, 그 하나의 Value만 실행된다 → 키당 렌더 1회.
    private readonly ConcurrentDictionary<(Guid, uint), Lazy<Task<RenderedMarkdown>>> _inflight = new();

    public RenderedPostCache(RenderGate gate, IOptions<RenderingOptions> options)
    {
        _gate = gate;
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = (long)options.Value.CacheMegabytes * 1024 * 1024 });
    }

    public bool TryGet(Guid postId, uint version, out RenderedMarkdown rendered)
    {
        if (_cache.TryGetValue((postId, version), out RenderedMarkdown? hit) && hit is not null)
        {
            rendered = hit;
            return true;
        }
        rendered = null!;
        return false;
    }

    /// <summary>캐시에 있으면 그것을, 없으면 (같은 키의 동시 호출과 합쳐) 한 번 렌더링해 돌려준다.</summary>
    /// <param name="ct">이 호출자의 대기만 취소한다. 공유 렌더는 다른 호출자를 위해 계속된다.</param>
    public Task<RenderedMarkdown> GetOrRenderAsync(Guid postId, uint version, string markdown, CancellationToken ct)
    {
        if (TryGet(postId, version, out var hit)) return Task.FromResult(hit);
        var key = (postId, version);
        var lazy = _inflight.GetOrAdd(key, k => new Lazy<Task<RenderedMarkdown>>(() => RenderAndStoreAsync(k, markdown)));
        return lazy.Value.WaitAsync(ct);
    }

    /// <summary>이미 렌더링한 결과를 넣는다(글 저장 경로가 저장 전 확인용으로 한 렌더를 버리지 않고 선채움한다).</summary>
    public void Store(Guid postId, uint version, RenderedMarkdown rendered) =>
        _cache.Set((postId, version), rendered, new MemoryCacheEntryOptions
        {
            Size = (long)rendered.Html.Length * sizeof(char) + 256,
            AbsoluteExpirationRelativeToNow = rendered.HighlightTimedOut ? DegradedLifetime : NormalLifetime,
        });

    private async Task<RenderedMarkdown> RenderAndStoreAsync((Guid PostId, uint Version) key, string markdown)
    {
        // 즉시 양보한다: 렌더는 동기 작업이라, 양보하지 않으면 Lazy 팩토리가 렌더가 끝날 때까지 반환하지 않고
        // 같은 키의 다른 호출자가 Lazy.Value에서 (스레드를 점유한 채) 동기로 막힌다.
        await Task.Yield();
        try
        {
            var rendered = await _gate.RenderAsync(markdown, CancellationToken.None);
            Store(key.PostId, key.Version, rendered);
            return rendered;
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    public void Dispose() => _cache.Dispose();
}
```

`OverloadExceptionHandler.IsOverload`:

```csharp
    internal static bool IsOverload(Exception exception) =>
        exception is PostgresException { SqlState: "57014" or "55P03" } or RenderBusyException;
```

(`using PortfolioBlog.Api.Infrastructure.Markdown;` 추가, 클래스 요약에 "렌더 게이트 대기 초과"를 더한다.)

`StartupValidation.Validate`: `RenderingOptions`를 읽어 `Concurrency` 1~64, `QueueTimeoutMs` 1~60000, `CacheMegabytes` 1~1024가 아니면 `"Rendering:Concurrency·QueueTimeoutMs·CacheMegabytes 범위 오류"` 메시지로 `InvalidOperationException`.

`Program.cs`(렌더러 등록 아래):

```csharp
builder.Services.Configure<RenderingOptions>(builder.Configuration.GetSection(RenderingOptions.SectionName));
builder.Services.AddSingleton<RenderGate>();
builder.Services.AddSingleton<RenderedPostCache>();
```

마이그레이션 블록 뒤, 파이프라인 구성 앞:

```csharp
// 워밍업: 첫 렌더에는 ColorCode 등의 정적 초기화(실측 약 185ms)가 붙는다. 첫 방문자가 아니라 시작 시점에 낸다.
app.Services.GetRequiredService<MarkdownRenderer>().Render("```csharp\nvar warm = 1;\n```\n");
```

Run: `dotnet test PortfolioBlog.slnx -c Release --filter "FullyQualifiedName~RenderGateTests|FullyQualifiedName~RenderedPostCacheTests|FullyQualifiedName~ErrorPipelineTests"` → Expected: 전부 통과.
**규칙 8 확인:** `GetOrRenderAsync`를 잠깐 "단일 비행 없이 매번 `_gate.RenderAsync`"로 바꿔 `ConcurrentRequests_ForTheSameVersion_RenderOnce`가 실패하는 것을 본 뒤 되돌린다. `await Task.Yield()`를 지우면 같은 테스트가 (첫 호출에서 막혀) 시간 초과로 실패하는 것도 확인한다.

- [ ] **Step 5: 저장·미리보기 경로를 게이트 뒤로**

`PostEndpoints.cs`:
- `CreateAsync`·`UpdateAsync`의 매개변수 `MarkdownRenderer renderer`를 `RenderGate gate, RenderedPostCache cache`로 바꾼다.
- `TryRenderOrAddError`를 아래로 교체하고 두 호출부를 `var rendered = await RenderOrAddErrorAsync(gate, req.ContentMarkdown!, errors, ct); if (rendered is null) return TypedResults.ValidationProblem(errors.ToDictionary());`로 바꾼다:

```csharp
    /// <summary>저장 전에 본문을 게이트 뒤에서 실제로 렌더링해 저장 가능한지 확인한다. 결과는 버리지 않고 저장 뒤 캐시에 넣는다.</summary>
    /// <returns>렌더 결과. 중첩이 너무 깊어 <paramref name="errors"/>에 오류를 넣었으면 <c>null</c>.</returns>
    /// <exception cref="RenderBusyException">렌더 슬롯을 제때 얻지 못했다(예외 처리기가 503으로 바꾼다).</exception>
    private static async Task<RenderedMarkdown?> RenderOrAddErrorAsync(RenderGate gate, string contentMarkdown, ValidationErrors errors, CancellationToken ct)
    {
        try
        {
            return await gate.RenderAsync(contentMarkdown, ct);
        }
        catch (MarkdownTooComplexException)
        {
            errors.Add("contentMarkdown", "마크다운 구조가 너무 깊게 중첩됐습니다. 중첩을 줄여주세요.");
            return null;
        }
    }
```

- 생성: `var dto = await PostQueries.GetDetailAsync(db, post.Id, ct);` 다음에 `if (dto is not null) cache.Store(dto.Id, dto.Version, rendered);`
- 수정: 마지막을 `var updated = await PostQueries.GetDetailAsync(db, post.Id, ct); if (updated is not null) cache.Store(updated.Id, updated.Version, rendered); return TypedResults.Ok(updated);`
- 두 핸들러의 "남는 위험: … 이건 여기서 막지 못한다" 주석을 사실로 고친다: "Markdig 파서의 초선형 비용은 이 요청에도 그대로 들지만, `RenderGate`가 프로세스 전체의 동시 렌더 수를 묶으므로 저장 요청 여러 개가 CPU를 동시에 물지 못한다."

`PreviewEndpoints.cs`: 핸들러를 `private static async Task<IResult> Render(PreviewRequest request, RenderGate gate, CancellationToken ct)`로 바꾸고 `renderer.Render(request.Markdown!)`를 `(await gate.RenderAsync(request.Markdown!, ct)).Html`로 바꾼다. 클래스 주석에 "미리보기 전용 동시 실행 제한(2) 위에 전역 게이트가 한 겹 더 있다"를 추가.

- [ ] **Step 6: 전체 회귀 + 커밋**

Run: `dotnet build PortfolioBlog.slnx -c Release`(경고 0) → `dotnet test PortfolioBlog.slnx -c Release`

```bash
git add -A
git commit -m "추가: 렌더 동시 실행 게이트와 글 버전별 렌더 캐시, 시간 예산 테스트를 주입 시계로 교체

- 모든 렌더(미리보기·글 저장·공개 페이지)가 전역 게이트를 지나고 대기 초과는 503
- (PostId, xmin) 캐시 + 단일 비행으로 글 버전당 렌더 1회, 저장 경로가 캐시를 선채움
- 시간 때문에 강조가 빠진 렌더는 2분만 캐시
- 빠른 기계에서 거짓 실패하던 시간 의존 테스트를 결정적 가짜 시계로 교체, 시작 시 렌더러 워밍업

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: 공개 읽기 전용 DbContext · `statement_timeout` · 공개 프로젝션 조회

**Files:**
- Modify: `PortfolioBlog.Api/Infrastructure/Data/AppDbContext.cs`
- Create: `PortfolioBlog.Api/Infrastructure/Data/PublicDbContext.cs`, `DataServiceCollectionExtensions.cs`, `PublicModels.cs`, `PublicQueries.cs`
- Modify: `PortfolioBlog.Api/Program.cs`, `PortfolioBlog.Api.Tests/Infrastructure/ApiFactory.cs`
- Test: `PortfolioBlog.Api.Tests/Infrastructure/PublicSeed.cs`(도구), `PublicDbContextTests.cs`, `PublicQueriesTests.cs`

**Interfaces:**
- Consumes: `PublicOptions.StatementTimeoutMs`, `LikePattern.Contains/Escape`, `TagResolver.ResolveIdsAsync`, `DbClock.UtcNow`.
- Produces:
  - `PublicDbContext : AppDbContext`(쓰기 불가), `PublicDbContext.BuildConnectionString(string baseConnectionString, int statementTimeoutMs) : string`
  - `services.AddBlogData()`(두 컨텍스트 등록, 기존 연결 문자열 가드 메시지 유지)
  - record: `PublicTag(string Name, string NormalizedName)`, `PublicPostSummary(string Slug, string Title, string Summary, DateTimeOffset CreatedAt, PublicTag[] Tags)`, `PublicPage<T>(IReadOnlyList<T> Items, int Page, int Total)`(+ `LastPage`), `PublicLink(string Slug, string Title)`, `PublicPostMeta(Guid Id, uint Version, string Slug, string Title, string Summary, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, PublicTag[] Tags, PublicLink? Series, PublicLink? Previous, PublicLink? Next)`, `PublicContent(string Markdown, uint Version)`, `PublicSeriesEntry(int Order, string Slug, string Title, DateTimeOffset CreatedAt)`, `PublicSeries(string Slug, string Title, string Description, IReadOnlyList<PublicSeriesEntry> Posts)`, `PublicFeedEntry(Guid Id, string Slug, string Title, string Summary, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)`, `PublicSitemap(IReadOnlyList<(string Slug, DateTimeOffset UpdatedAt)> Posts, IReadOnlyList<string> TagKeys, IReadOnlyList<string> SeriesSlugs)`
  - `PublicQueries.PageSize = 20`, `SeriesMax = 500`, `FeedSize = 20`, `SitemapMax = 10_000`
  - `PublicQueries.LatestAsync(db, page, ct)`, `ByTagAsync(db, normalizedName, page, ct) : Task<(PublicTag Tag, PublicPage<PublicPostSummary> Posts)?>`, `SearchAsync(db, term, page, ct)`, `GetPostAsync(db, slug, ct) : Task<PublicPostMeta?>`, `GetContentAsync(db, id, ct) : Task<PublicContent?>`, `GetSeriesAsync(db, slug, ct) : Task<PublicSeries?>`, `FeedAsync(db, ct)`, `SitemapAsync(db, ct)` — 첫 인자는 전부 `PublicDbContext`
  - 테스트 도구: `PublicSeed.PostAsync(factory, slug, title, markdown = "본문", summary = "", tags = null, seriesId = null, seriesOrder = null, createdAt = null) : Task<Post>`, `PublicSeed.SeriesAsync(factory, slug, title, description = "") : Task<Domain.Series>`, `ApiFactory.ConnectionString`(internal)

**설계 메모:** `Version`(xmin)은 `PublicPostMeta`·`PublicContent`에만 있고 **캐시 키로만** 쓴다. 페이지·피드 어디에도 출력하지 않는다(2A 핸드오프 "공개용 프로젝션은 `Version`을 노출하지 않는다"). 글 본문(최대 200KB)은 캐시 미스일 때만 `GetContentAsync`로 따로 읽는다 — 캐시 히트 요청이 매번 200KB를 DB에서 끌어오지 않게.

- [ ] **Step 1: 실패하는 테스트**

`PortfolioBlog.Api.Tests/Infrastructure/PublicSeed.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using PortfolioBlog.Api.Domain;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>공개 표면 테스트용 시드. 관리 API를 거치지 않고 DbContext로 직접 넣는다(빠르고, 앱 검증이 막는 데이터도 넣을 수 있다).</summary>
internal static class PublicSeed
{
    public static async Task<Post> PostAsync(ApiFactory factory, string slug, string title, string markdown = "본문", string summary = "",
        string[]? tags = null, Guid? seriesId = null, int? seriesOrder = null, DateTimeOffset? createdAt = null)
    {
        using var _ = factory.CreateClient(); // 호스트 기동 → Migrate()
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tagIds = await TagResolver.ResolveIdsAsync(db, tags, CancellationToken.None);
        var at = createdAt ?? DbClock.UtcNow();
        var post = new Post
        {
            Slug = slug, Title = title, Summary = summary, ContentMarkdown = markdown,
            SeriesId = seriesId, SeriesOrder = seriesOrder, CreatedAt = at, UpdatedAt = at,
        };
        foreach (var tagId in tagIds) post.PostTags.Add(new PostTag { TagId = tagId });
        db.Posts.Add(post);
        await db.SaveChangesAsync();
        return post;
    }

    public static async Task<Series> SeriesAsync(ApiFactory factory, string slug, string title, string description = "")
    {
        using var _ = factory.CreateClient();
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var series = new Series { Slug = slug, Title = title, Description = description };
        db.Series.Add(series);
        await db.SaveChangesAsync();
        return series;
    }
}
```

`PortfolioBlog.Api.Tests/Infrastructure/PublicDbContextTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PortfolioBlog.Api.Domain;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>공개 조회 연결이 DB 수준에서 시간 제한·읽기 전용인지 실제 PostgreSQL로 검증한다.</summary>
[Collection("postgres")]
public sealed class PublicDbContextTests(PostgresContainerFixture pg)
{
    private static readonly Dictionary<string, string?> FastTimeout = new() { ["Public:StatementTimeoutMs"] = "200" };

    /// <summary>느린 문장은 statement_timeout에서 57014로 끊긴다. 2초짜리 pg_sleep이 1.5초 안에 끝나야 한다(타임아웃이 없으면 2초를 다 기다린다).</summary>
    [Fact]
    public async Task SlowStatement_IsCancelledByStatementTimeout()
    {
        using var factory = new ApiFactory(pg, FastTimeout);
        using var _ = factory.CreateClient();
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PublicDbContext>();

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("SELECT pg_sleep(2)"));
        Assert.Equal("57014", ex.SqlState);
        Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(1.5));
    }

    /// <summary>같은 앱의 관리 컨텍스트는 제한을 받지 않는다(설정이 관리 연결로 새지 않았다).</summary>
    [Fact]
    public async Task AdminContext_IsNotAffected()
    {
        using var factory = new ApiFactory(pg, FastTimeout);
        using var _ = factory.CreateClient();
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_sleep(0.5)"); // 200ms를 넘지만 통과해야 한다
    }

    /// <summary>공개 연결로는 어떤 쓰기도 할 수 없다: SQL은 25006, SaveChanges는 앱에서 막는다.</summary>
    [Fact]
    public async Task PublicContext_CannotWrite()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        using var _ = factory.CreateClient();
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PublicDbContext>();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM \"Tags\""));
        Assert.Equal("25006", ex.SqlState);
        var viaEf = await Assert.ThrowsAsync<PostgresException>(() => db.Tags.ExecuteDeleteAsync());
        Assert.Equal("25006", viaEf.SqlState);

        db.Tags.Add(new Tag { Name = "x", NormalizedName = "x" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    /// <summary>연결 문자열에 원래 있던 Options는 버리지 않고 보존하지도 않는다 — 공개 연결의 Options는 항상 이 두 설정이다(조용한 덮어쓰기를 문서화하는 테스트).</summary>
    [Fact]
    public void BuildConnectionString_SetsExactlyTheTwoStartupOptions()
    {
        var built = new NpgsqlConnectionStringBuilder(PublicDbContext.BuildConnectionString("Host=h;Database=d;Username=u;Password=dummy;Options=-c work_mem=1MB", 3000));
        Assert.Equal("-c statement_timeout=3000 -c default_transaction_read_only=on", built.Options);
        Assert.Equal("d", built.Database);
    }
}
```

`PortfolioBlog.Api.Tests/Infrastructure/PublicQueriesTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>공개 조회의 정렬·페이지·태그·시리즈 이웃·검색 이스케이프를 실제 PostgreSQL로 검증한다. 테스트마다 격리된 DB를 쓴다.</summary>
[Collection("postgres")]
public sealed class PublicQueriesTests(PostgresContainerFixture pg)
{
    private static async Task<T> QueryAsync<T>(ApiFactory factory, Func<PublicDbContext, Task<T>> query)
    {
        await using var scope = factory.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<PublicDbContext>());
    }

    /// <summary>최신순 20개씩, 같은 시각이면 Id로 안정 정렬, 태그는 정규화명 순.</summary>
    [Fact]
    public async Task Latest_PagesNewestFirst()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        var t0 = DbClock.UtcNow().AddDays(-1);
        for (var i = 0; i < 25; i++) await PublicSeed.PostAsync(factory, $"post-{i:00}", $"글 {i}", tags: i == 24 ? new[] { "b", "A" } : null, createdAt: t0.AddMinutes(i));

        var first = await QueryAsync(factory, db => PublicQueries.LatestAsync(db, 1, CancellationToken.None));
        Assert.Equal(25, first.Total);
        Assert.Equal(2, first.LastPage);
        Assert.Equal(20, first.Items.Count);
        Assert.Equal("post-24", first.Items[0].Slug);
        Assert.Equal(new[] { "A", "b" }, first.Items[0].Tags.Select(t => t.Name));

        var second = await QueryAsync(factory, db => PublicQueries.LatestAsync(db, 2, CancellationToken.None));
        Assert.Equal(new[] { "post-04", "post-03", "post-02", "post-01", "post-00" }, second.Items.Select(p => p.Slug));
    }

    /// <summary>시리즈 이웃은 (SeriesOrder, CreatedAt, Id) 순이며 순서가 같아도 안정적이다. 시리즈 밖 글은 이웃이 없다.</summary>
    [Fact]
    public async Task GetPost_ResolvesSeriesNeighbors_InStableOrder()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        var series = await PublicSeed.SeriesAsync(factory, "net-internals", ".NET 내부");
        var t0 = DbClock.UtcNow().AddDays(-1);
        await PublicSeed.PostAsync(factory, "part-a", "A", seriesId: series.Id, seriesOrder: 1, createdAt: t0);
        await PublicSeed.PostAsync(factory, "part-b", "B", seriesId: series.Id, seriesOrder: 2, createdAt: t0.AddMinutes(2)); // 순서 2가 둘
        await PublicSeed.PostAsync(factory, "part-c", "C", seriesId: series.Id, seriesOrder: 2, createdAt: t0.AddMinutes(1));
        await PublicSeed.PostAsync(factory, "loner", "혼자");

        var c = await QueryAsync(factory, db => PublicQueries.GetPostAsync(db, "part-c", CancellationToken.None));
        Assert.Equal("part-a", c!.Previous?.Slug);
        Assert.Equal("part-b", c.Next?.Slug);           // 같은 순서 2 안에서는 먼저 쓴 c가 앞
        Assert.Equal("net-internals", c.Series?.Slug);

        var loner = await QueryAsync(factory, db => PublicQueries.GetPostAsync(db, "loner", CancellationToken.None));
        Assert.Null(loner!.Series);
        Assert.Null(loner.Previous);
        Assert.Null(await QueryAsync(factory, db => PublicQueries.GetPostAsync(db, "no-such", CancellationToken.None)));
    }

    /// <summary>태그는 정규화명으로 찾고, 검색어의 %·_·\는 글자 그대로다.</summary>
    [Fact]
    public async Task ByTag_And_Search_UseNormalizedNames_AndLiteralWildcards()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        await PublicSeed.PostAsync(factory, "sharp", "C# 12", tags: ["C#"]);
        await PublicSeed.PostAsync(factory, "pct", "100% 완료", markdown: "경로는 a_b 이다");
        await PublicSeed.PostAsync(factory, "plain", "100 완료", markdown: "경로는 aXb 이다");

        var tagged = await QueryAsync(factory, db => PublicQueries.ByTagAsync(db, "c#", 1, CancellationToken.None));
        Assert.Equal("C#", tagged!.Value.Tag.Name);
        Assert.Equal(new[] { "sharp" }, tagged.Value.Posts.Items.Select(p => p.Slug));
        Assert.Null(await QueryAsync(factory, db => PublicQueries.ByTagAsync(db, "없는태그", 1, CancellationToken.None)));

        Assert.Equal(new[] { "pct" }, (await QueryAsync(factory, db => PublicQueries.SearchAsync(db, "100%", 1, CancellationToken.None))).Items.Select(p => p.Slug));
        Assert.Equal(new[] { "pct" }, (await QueryAsync(factory, db => PublicQueries.SearchAsync(db, "a_b", 1, CancellationToken.None))).Items.Select(p => p.Slug));
        Assert.Equal(2, (await QueryAsync(factory, db => PublicQueries.SearchAsync(db, "완료", 1, CancellationToken.None))).Total);
    }
}
```

Run: `dotnet build PortfolioBlog.slnx -c Release` → Expected: 컴파일 오류(`PublicDbContext`·`PublicQueries` 없음).

- [ ] **Step 2: 컨텍스트와 등록**

`AppDbContext.cs`: `public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)`를 아래로 바꾼다(본문 그대로). 클래스 `<remarks>`에 "EF 도구는 컨텍스트가 둘이라 `--context AppDbContext`가 필요하다. 마이그레이션은 이 타입에만 속한다"를 추가:

```csharp
public class AppDbContext : DbContext
{
    /// <summary>관리(읽기·쓰기) 컨텍스트.</summary>
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    /// <summary>파생 컨텍스트용. 같은 모델 구성(<see cref="OnModelCreating"/>)을 공유한다.</summary>
    protected AppDbContext(DbContextOptions options) : base(options) { }
```

`PublicDbContext.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>인증 없는 공개 조회 전용 컨텍스트. 모델은 <see cref="AppDbContext"/>와 같고, 연결이 다르다: 문장 시간 제한 + 읽기 전용 트랜잭션.</summary>
/// <remarks>
/// 쓰기 금지는 두 겹이다: 앱에서 <c>SaveChanges</c>가 예외를 던지고, <c>ExecuteUpdate/Delete</c>·원시 SQL처럼 그 길을 지나지 않는 쓰기는
/// PostgreSQL이 <c>default_transaction_read_only=on</c>으로 거부한다(SqlState 25006, 실측). 이 컨텍스트로 <c>Migrate()</c>를 호출하지 않는다.
/// </remarks>
public sealed class PublicDbContext(DbContextOptions<PublicDbContext> options) : AppDbContext(options)
{
    /// <summary>관리 연결 문자열에서 공개 조회용 연결 문자열을 만든다. 연결 문자열이 다르므로 Npgsql이 풀을 따로 만든다 — 설정이 관리 연결로 새지 않는다.</summary>
    /// <param name="baseConnectionString"><c>ConnectionStrings:Default</c>. 여기에 <c>Options</c>가 있었다면 **대체된다**.</param>
    /// <param name="statementTimeoutMs">문장 하나의 최대 실행 시간(밀리초).</param>
    public static string BuildConnectionString(string baseConnectionString, int statementTimeoutMs) =>
        new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            // 시작 매개변수로 주면 세션 기본값이 된다: 풀에서 재사용될 때의 리셋도 이 값으로 돌아온다(실측).
            Options = $"-c statement_timeout={statementTimeoutMs} -c default_transaction_read_only=on",
            ApplicationName = "PortfolioBlog.Public",
        }.ConnectionString;

    public override int SaveChanges(bool acceptAllChangesOnSuccess) => throw ReadOnly();

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) => throw ReadOnly();

    private static InvalidOperationException ReadOnly() => new("PublicDbContext는 읽기 전용입니다. 쓰기는 AppDbContext로 합니다.");
}
```

`DataServiceCollectionExtensions.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Infrastructure.Web;

namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>두 DbContext를 등록한다. 연결 문자열은 람다 안에서(= Build 이후 첫 해석 시점에) 읽는다.</summary>
public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddBlogData(this IServiceCollection services)
    {
        services.AddDbContext<AppDbContext>((sp, o) => o.UseNpgsql(RequireConnectionString(sp)));
        services.AddDbContext<PublicDbContext>((sp, o) => o
            .UseNpgsql(PublicDbContext.BuildConnectionString(RequireConnectionString(sp), sp.GetRequiredService<IOptions<PublicOptions>>().Value.StatementTimeoutMs))
            // 공개 경로는 추적할 이유가 없다: 변경 추적기 할당을 없애고, 실수로 엔티티를 고쳐도 저장 대상이 되지 않는다.
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));
        return services;
    }

    // appsettings.json의 기본값이 빈 문자열이라 ??로는 걸러지지 않는다(Plan 1 정오표). 메시지는 ConnectionStringGuardTests가 고정한다.
    private static string RequireConnectionString(IServiceProvider sp)
    {
        var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString("Default");
        return string.IsNullOrWhiteSpace(connectionString)
            ? throw new InvalidOperationException("ConnectionStrings:Default 설정이 없습니다.")
            : connectionString;
    }
}
```

`Program.cs`: 기존 `builder.Services.AddDbContext<AppDbContext>(…)` 블록 전체를 `builder.Services.AddBlogData();`로 바꾼다(`PublicOptions` 등록보다 뒤여도 된다 — 람다는 지연 실행).

`ApiFactory.cs`:
- `internal string ConnectionString => _connectionString;` 추가(Task 8의 잠금 테스트가 쓴다).
- `Dispose`의 풀 정리를 두 풀 모두로:

```csharp
        // 공개 조회 풀도 닫는다(연결 문자열이 달라 풀이 따로다). 시간 제한 값이 연결 문자열의 일부라 앱과 같은 값으로 조립해야 같은 풀을 가리킨다.
        var timeout = _settings.TryGetValue("Public:StatementTimeoutMs", out var raw) && raw is not null ? int.Parse(raw, System.Globalization.CultureInfo.InvariantCulture) : 3000;
        foreach (var cs in new[] { _connectionString, PublicDbContext.BuildConnectionString(_connectionString, timeout) })
        {
            using var connection = new NpgsqlConnection(cs);
            NpgsqlConnection.ClearPool(connection);
        }
```

- [ ] **Step 3: 프로젝션과 조회**

`PublicModels.cs`: Interfaces에 적은 record를 그대로 선언한다(각각 `<summary>` + `<param>`). `PublicPage<T>`만 계산 속성이 있다:

```csharp
/// <summary>공개 목록의 한 쪽.</summary>
/// <param name="Items">이 쪽의 항목.</param>
/// <param name="Page">1부터 시작하는 쪽 번호.</param>
/// <param name="Total">필터에 맞는 전체 건수.</param>
public sealed record PublicPage<T>(IReadOnlyList<T> Items, int Page, int Total)
{
    /// <summary>마지막 쪽 번호(항목이 없어도 1).</summary>
    public int LastPage => Math.Max(1, (Total + PublicQueries.PageSize - 1) / PublicQueries.PageSize);
}
```

`PublicQueries.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using PortfolioBlog.Api.Domain;

namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>공개 페이지·피드가 쓰는 조회. 전부 <see cref="PublicDbContext"/>(시간 제한·읽기 전용)로만 돈다.</summary>
public static class PublicQueries
{
    /// <summary>목록 한 쪽의 글 수(스펙 3.4).</summary>
    public const int PageSize = 20;

    /// <summary>시리즈 한 개에서 읽는 글 수의 상한(시리즈 쪽·이전/다음 계산 공통).</summary>
    public const int SeriesMax = 500;

    /// <summary>Atom 피드의 항목 수.</summary>
    public const int FeedSize = 20;

    /// <summary>sitemap에 싣는 종류별 URL 수의 상한.</summary>
    public const int SitemapMax = 10_000;

    public static Task<PublicPage<PublicPostSummary>> LatestAsync(PublicDbContext db, int page, CancellationToken ct) =>
        PageAsync(db.Posts, page, ct);

    public static async Task<(PublicTag Tag, PublicPage<PublicPostSummary> Posts)?> ByTagAsync(PublicDbContext db, string normalizedName, int page, CancellationToken ct)
    {
        var tag = await db.Tags.Where(t => t.NormalizedName == normalizedName)
            .Select(t => new { t.Name, t.NormalizedName }).SingleOrDefaultAsync(ct);
        if (tag is null) return null;
        var posts = await PageAsync(db.Posts.Where(p => p.PostTags.Any(pt => pt.Tag.NormalizedName == normalizedName)), page, ct);
        return (new PublicTag(tag.Name, tag.NormalizedName), posts);
    }

    /// <summary>제목·요약·본문 <c>ILIKE</c>. <paramref name="term"/>의 메타문자는 글자 그대로 취급된다. 비용 상한은 연결의 statement_timeout과 검색 속도 제한이다.</summary>
    public static Task<PublicPage<PublicPostSummary>> SearchAsync(PublicDbContext db, string term, int page, CancellationToken ct)
    {
        var pattern = LikePattern.Contains(term);
        return PageAsync(db.Posts.Where(p => EF.Functions.ILike(p.Title, pattern, LikePattern.Escape)
                                          || EF.Functions.ILike(p.Summary, pattern, LikePattern.Escape)
                                          || EF.Functions.ILike(p.ContentMarkdown, pattern, LikePattern.Escape)), page, ct);
    }

    /// <summary>본문을 뺀 글 메타데이터 + 시리즈 이웃. 본문은 캐시 미스일 때만 <see cref="GetContentAsync"/>로 따로 읽는다.</summary>
    public static async Task<PublicPostMeta?> GetPostAsync(PublicDbContext db, string slug, CancellationToken ct)
    {
        var row = await db.Posts.Where(p => p.Slug == slug)
            .Select(p => new
            {
                p.Id, p.Version, p.Slug, p.Title, p.Summary, p.CreatedAt, p.UpdatedAt, p.SeriesId,
                SeriesSlug = p.Series != null ? p.Series.Slug : null,
                SeriesTitle = p.Series != null ? p.Series.Title : null,
                Tags = p.PostTags.OrderBy(pt => pt.Tag.NormalizedName).Select(pt => new { pt.Tag.Name, pt.Tag.NormalizedName }).ToArray(),
            })
            .SingleOrDefaultAsync(ct);
        if (row is null) return null;

        PublicLink? previous = null, next = null;
        if (row.SeriesId is { } seriesId)
        {
            var siblings = await db.Posts.Where(p => p.SeriesId == seriesId)
                .OrderBy(p => p.SeriesOrder).ThenBy(p => p.CreatedAt).ThenBy(p => p.Id)
                .Take(SeriesMax).Select(p => new { p.Id, p.Slug, p.Title }).ToListAsync(ct);
            var index = siblings.FindIndex(s => s.Id == row.Id);
            if (index > 0) previous = new PublicLink(siblings[index - 1].Slug, siblings[index - 1].Title);
            if (index >= 0 && index < siblings.Count - 1) next = new PublicLink(siblings[index + 1].Slug, siblings[index + 1].Title);
        }
        return new PublicPostMeta(row.Id, row.Version, row.Slug, row.Title, row.Summary, row.CreatedAt, row.UpdatedAt,
            row.Tags.Select(t => new PublicTag(t.Name, t.NormalizedName)).ToArray(),
            row.SeriesSlug is null ? null : new PublicLink(row.SeriesSlug, row.SeriesTitle!), previous, next);
    }

    /// <summary>본문과 그 본문의 버전을 **한 문장으로** 읽는다(메타데이터를 읽은 뒤 글이 수정됐어도 캐시 키와 내용이 어긋나지 않는다).</summary>
    public static async Task<PublicContent?> GetContentAsync(PublicDbContext db, Guid id, CancellationToken ct)
    {
        var row = await db.Posts.Where(p => p.Id == id).Select(p => new { p.ContentMarkdown, p.Version }).SingleOrDefaultAsync(ct);
        return row is null ? null : new PublicContent(row.ContentMarkdown, row.Version);
    }

    public static async Task<PublicSeries?> GetSeriesAsync(PublicDbContext db, string slug, CancellationToken ct)
    {
        var series = await db.Series.Where(s => s.Slug == slug).Select(s => new { s.Id, s.Slug, s.Title, s.Description }).SingleOrDefaultAsync(ct);
        if (series is null) return null;
        var posts = await db.Posts.Where(p => p.SeriesId == series.Id)
            .OrderBy(p => p.SeriesOrder).ThenBy(p => p.CreatedAt).ThenBy(p => p.Id)
            .Take(SeriesMax).Select(p => new { Order = p.SeriesOrder ?? 0, p.Slug, p.Title, p.CreatedAt }).ToListAsync(ct);
        return new PublicSeries(series.Slug, series.Title, series.Description,
            posts.Select(p => new PublicSeriesEntry(p.Order, p.Slug, p.Title, p.CreatedAt)).ToList());
    }

    public static async Task<IReadOnlyList<PublicFeedEntry>> FeedAsync(PublicDbContext db, CancellationToken ct)
    {
        var rows = await db.Posts.OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id).Take(FeedSize)
            .Select(p => new { p.Id, p.Slug, p.Title, p.Summary, p.CreatedAt, p.UpdatedAt }).ToListAsync(ct);
        return rows.Select(p => new PublicFeedEntry(p.Id, p.Slug, p.Title, p.Summary, p.CreatedAt, p.UpdatedAt)).ToList();
    }

    public static async Task<PublicSitemap> SitemapAsync(PublicDbContext db, CancellationToken ct)
    {
        var posts = await db.Posts.OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id).Take(SitemapMax)
            .Select(p => new { p.Slug, p.UpdatedAt }).ToListAsync(ct);
        // 글이 하나도 없는 태그는 빈 목록 쪽이라 싣지 않는다.
        var tags = await db.Tags.Where(t => t.PostTags.Any()).OrderBy(t => t.NormalizedName).Take(SitemapMax).Select(t => t.NormalizedName).ToListAsync(ct);
        var series = await db.Series.OrderBy(s => s.Slug).Take(SitemapMax).Select(s => s.Slug).ToListAsync(ct);
        return new PublicSitemap(posts.Select(p => (p.Slug, p.UpdatedAt)).ToList(), tags, series);
    }

    private static async Task<PublicPage<PublicPostSummary>> PageAsync(IQueryable<Post> query, int page, CancellationToken ct)
    {
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .Select(p => new
            {
                p.Slug, p.Title, p.Summary, p.CreatedAt,
                Tags = p.PostTags.OrderBy(pt => pt.Tag.NormalizedName).Select(pt => new { pt.Tag.Name, pt.Tag.NormalizedName }).ToArray(),
            })
            .ToListAsync(ct);
        return new PublicPage<PublicPostSummary>(
            rows.Select(r => new PublicPostSummary(r.Slug, r.Title, r.Summary, r.CreatedAt, r.Tags.Select(t => new PublicTag(t.Name, t.NormalizedName)).ToArray())).ToList(),
            page, total);
    }
}
```

> 컬렉션 하위 질의의 프로젝션은 익명 형식으로 받고 메모리에서 record로 바꾼다(기존 `PostQueries`와 같은 방식 — 중첩 컬렉션 안의 생성자 프로젝션에 기대지 않는다).

- [ ] **Step 4: 통과 확인**

Run: `dotnet test PortfolioBlog.slnx -c Release --filter "FullyQualifiedName~PublicDbContextTests|FullyQualifiedName~PublicQueriesTests|FullyQualifiedName~ConnectionStringGuardTests"`
Expected: 전부 통과. `Latest_PagesNewestFirst`의 태그 순서 단언이 `A, b`인 이유: 정렬 키가 정규화명(`a` < `b`)이기 때문이다.
**규칙 8 확인:** `BuildConnectionString`에서 `Options`를 잠깐 빼고 `SlowStatement…`·`PublicContext_CannotWrite`가 실패하는 것을 본 뒤 되돌린다.

- [ ] **Step 5: 전체 회귀 + 커밋**

Run: `dotnet build PortfolioBlog.slnx -c Release`(경고 0) → `dotnet test PortfolioBlog.slnx -c Release`

```bash
git add -A
git commit -m "추가: 공개 조회 전용 DbContext(statement_timeout·읽기 전용 트랜잭션)와 공개 프로젝션 조회

- 공개 경로의 쓰기를 앱(SaveChanges 예외)과 DB(default_transaction_read_only) 두 겹으로 차단
- 별도 연결 문자열·풀이라 시간 제한이 관리 연결로 새지 않음
- 목록·태그·검색·글·시리즈·피드·sitemap 조회. xmin은 캐시 키로만 쓰고 출력하지 않음

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: 공개 Razor 페이지 — 레이아웃 · 목록 · 글 · 태그 · 시리즈 · CSS · 접근 매트릭스

**Files:**
- Create: `PortfolioBlog.Api/Pages/PublicPageConvention.cs`, `PublicPageModel.cs`, `PageHead.cs`, `PageNumber.cs`, `PagerModel.cs`, `SiteEndpoints.cs`
- Create: `PortfolioBlog.Api/Pages/_ViewImports.cshtml`, `_ViewStart.cshtml`, `Shared/_Layout.cshtml`, `Shared/_PostList.cshtml`, `Shared/_TagList.cshtml`, `Shared/_Pager.cshtml`
- Create: `PortfolioBlog.Api/Pages/Index.cshtml`(+`.cs`), `Post.cshtml`(+`.cs`), `Tag.cshtml`(+`.cs`), `Series.cshtml`(+`.cs`)
- Create: `PortfolioBlog.Api/Infrastructure/Web/PublicUrls.cs`, `PublicFormat.cs`, `PortfolioBlog.Api/Infrastructure/Markdown/HighlightCss.cs`, `PortfolioBlog.Api/wwwroot/css/site.css`
- Modify: `PortfolioBlog.Api/Infrastructure/Access/SiteOptions.cs`, `StartupValidation.cs`, `PortfolioBlog.Api/Program.cs`, `PortfolioBlog.Api/appsettings.json`
- Test: `PortfolioBlog.Api.Tests/Infrastructure/HtmlDoc.cs`(도구), `PublicUrlsTests.cs`, `PageNumberTests.cs`, `HighlightCssTests.cs`, `PortfolioBlog.Api.Tests/Features/PublicPagesTests.cs`; Modify `Features/AccessMatrixTests.cs`

**Interfaces:**
- Consumes: `PublicDbContext`, `PublicQueries.*`, `RenderedPostCache`, `RenderGate.RenderCount`, `RateLimitMetadata`, `SlugRules.IsValid`, `TagResolver.Normalize`, `TextRules.ContainsNul`, `ErrorResponses`(404 본문), `PublicSeed`.
- Produces:
  - `SiteOptions.Title`(기본 `"Blog"`), `Description`, `Author`
  - `PublicUrls.Post(slug)`, `PublicUrls.Series(slug)`, `PublicUrls.Tag(normalizedName) : string?`(`"."`·`".."`는 `null`)
  - `PublicFormat.Rfc3339(DateTimeOffset)`, `PublicFormat.DisplayDate(DateTimeOffset)`
  - `PageNumber.TryRead(IQueryCollection query, int max, out int page) : bool`
  - `PagerModel(string BasePath, string? Query, int Page, int LastPage)` + `Href(int page)`
  - `PublicPageModel`(추상, `Site`, `SetHead(title, description, path, ogType = "website", imagePath = null, noIndex = false)`), `PageHead` record, `PublicPageModel.HeadKey = "Head"`
  - `PublicPageConvention(string publicHost)` — 모든 페이지 선택자에 GET/HEAD·공개 호스트·속도 제한 메타데이터. `/Search` 페이지는 `RateLimitPolicy.Search`, 나머지는 `PublicPage`
  - `SiteEndpoints.MapPublicSiteEndpoints(this WebApplication)`, `SiteEndpoints.HighlightCssPattern = "/css/highlight.css"` — Task 7이 피드·sitemap·robots를 여기에 더한다
  - `HighlightCss.Value : string`
  - 테스트 도구 `HtmlDoc.Parse(string) : IHtmlDocument`, `HtmlDoc.GetAsync(client, url, expected = 200) : Task<IHtmlDocument>`

**함정(스파이크 S1·S2 + Razor Pages 고유):**
1. **`page`는 Razor Pages의 예약 라우트 키다**(현재 페이지 경로 `/Index`가 들어 있다). 핸들러 매개변수 `int? page`는 쿼리 문자열이 아니라 그 값을 바인딩하려다 실패한다 → `Request.Query["page"]`를 직접 읽는다(`PageNumber`). 링크도 `asp-route-page`를 쓰지 않고 문자열로 만든다.
2. **태그 헬퍼를 쓰지 않는다**(`_ViewImports`에 `@addTagHelper` 없음). 폼은 GET뿐이고 antiforgery 토큰·쿠키가 끼어들 여지를 없앤다.
3. 페이지는 `@page "/"`처럼 **절대 템플릿**으로 선언한다(선택자 하나, `/Index` 별칭 없음). `RawText`는 앞 슬래시가 없다(`''`, `posts/{slug}`).
4. `Series`는 `PortfolioBlog.Api.Domain.Series`·네임스페이스 `Features.Series`와 이름이 겹친다 → PageModel 클래스는 `SeriesPageModel`, 태그는 `TagPageModel`로 짓는다.

- [ ] **Step 1: 순수 함수 단위 테스트(실패)**

`PortfolioBlog.Api.Tests/Infrastructure/PublicUrlsTests.cs`:

```csharp
using PortfolioBlog.Api.Infrastructure.Web;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>공개 경로 생성. 태그는 경로 세그먼트 하나로 안전하게 인코딩되어야 한다.</summary>
public sealed class PublicUrlsTests
{
    [Theory]
    [InlineData("c#", "/tags/c%23")]
    [InlineData(".net", "/tags/.net")]
    [InlineData("a b", "/tags/a%20b")]
    [InlineData("100%", "/tags/100%25")]
    [InlineData("a?b&c=d", "/tags/a%3Fb%26c%3Dd")]
    [InlineData("a\\b", "/tags/a%5Cb")]
    [InlineData("한글", "/tags/%ED%95%9C%EA%B8%80")]
    public void Tag_IsOnePathSegment(string normalized, string expected) => Assert.Equal(expected, PublicUrls.Tag(normalized));

    /// <summary>"."과 ".."은 URL 정규화가 다른 경로로 바꿔 버리므로 링크를 만들지 않는다.</summary>
    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void Tag_DotSegments_HaveNoLink(string normalized) => Assert.Null(PublicUrls.Tag(normalized));
}
```

`PortfolioBlog.Api.Tests/Infrastructure/PageNumberTests.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using PortfolioBlog.Api.Pages;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>쪽 번호 파싱은 기본 거부다: ASCII 숫자 1~4자리, 1..max, 값 하나.</summary>
public sealed class PageNumberTests
{
    private static IQueryCollection Query(params string[] values) =>
        new QueryCollection(values.Length == 0
            ? new Dictionary<string, StringValues>()
            : new Dictionary<string, StringValues> { ["page"] = new StringValues(values) });

    [Fact]
    public void Absent_IsPageOne()
    {
        Assert.True(PageNumber.TryRead(Query(), 500, out var page));
        Assert.Equal(1, page);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("500", 500)]
    [InlineData("007", 7)]
    public void Valid(string raw, int expected)
    {
        Assert.True(PageNumber.TryRead(Query(raw), 500, out var page));
        Assert.Equal(expected, page);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("501")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1.0")]
    [InlineData(" 1")]
    [InlineData("１")]      // 전각 숫자: char.IsDigit는 참이지만 받지 않는다
    [InlineData("99999")]
    public void Invalid(string raw) => Assert.False(PageNumber.TryRead(Query(raw), 500, out _));

    [Fact]
    public void RepeatedParameter_IsInvalid() => Assert.False(PageNumber.TryRead(Query("1", "2"), 500, out _));
}
```

`PortfolioBlog.Api.Tests/Infrastructure/HighlightCssTests.cs`:

```csharp
using PortfolioBlog.Api.Infrastructure.Markdown;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>강조 CSS는 라이브러리 출력에서 클래스 규칙만 남긴 것이다(기본 거부).</summary>
public sealed class HighlightCssTests
{
    [Fact]
    public void Css_ContainsOnlyClassRules_InLightAndDark()
    {
        var css = HighlightCss.Value;
        Assert.Contains(".keyword{", css, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-color-scheme: dark){", css, StringComparison.Ordinal);
        Assert.DoesNotContain("body", css, StringComparison.Ordinal);        // 라이브러리의 body 배경 규칙은 버린다
        Assert.DoesNotContain(".plainText", css, StringComparison.Ordinal);  // color가 두 번 나오는 버그 규칙(밝은 테마에서 흰 글자)
        foreach (var banned in new[] { "<", "url(", "@import", "expression", "javascript:" })
        {
            Assert.DoesNotContain(banned, css, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(css.Count(c => c == '{'), css.Count(c => c == '}'));
    }

    /// <summary>렌더러가 실제로 내는 클래스가 CSS에 있다(라이브러리 버전이 바뀌어 이름이 달라지면 실패).</summary>
    [Fact]
    public void Css_CoversTheClassesTheRendererEmits()
    {
        var html = new MarkdownRenderer().Render("```csharp\n// c\nvar s = \"x\";\n```\n");
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(html, "<span class=\"([A-Za-z]+)\""))
        {
            Assert.Contains("." + m.Groups[1].Value + "{", HighlightCss.Value, StringComparison.Ordinal);
        }
    }
}
```

Run: `dotnet build PortfolioBlog.slnx -c Release` → Expected: 컴파일 오류.

- [ ] **Step 2: 순수 함수 구현**

`PublicUrls.cs`:

```csharp
namespace PortfolioBlog.Api.Infrastructure.Web;

/// <summary>공개 사이트의 경로를 만든다. 절대 URL이 필요하면 <c>Site:PublicOrigin</c>을 앞에 붙인다(요청 Host를 쓰지 않는다).</summary>
public static class PublicUrls
{
    /// <summary>slug는 <c>[a-z0-9-]</c>뿐이라 인코딩이 필요 없다.</summary>
    public static string Post(string slug) => "/posts/" + slug;

    public static string Series(string slug) => "/series/" + slug;

    /// <summary>태그 쪽 경로. 정규화명을 경로 세그먼트 하나로 인코딩한다(<c>#</c>·<c>?</c>·<c>%</c>·공백·역슬래시 포함).</summary>
    /// <returns>경로. 이름이 정확히 <c>"."</c> 또는 <c>".."</c>이면 <c>null</c> — 클라이언트와 서버의 점 세그먼트 정규화가 다른 쪽을 가리키게 만든다.</returns>
    public static string? Tag(string normalizedName) =>
        normalizedName is "." or ".." ? null : "/tags/" + Uri.EscapeDataString(normalizedName);
}
```

`PublicFormat.cs`:

```csharp
using System.Globalization;

namespace PortfolioBlog.Api.Infrastructure.Web;

/// <summary>공개 출력의 날짜 형식. 문화권과 서버 시간대에 의존하지 않는다.</summary>
public static class PublicFormat
{
    /// <summary>RFC 3339 UTC(<c>2026-09-21T03:04:05Z</c>). HTML <c>time[datetime]</c>·Atom·sitemap 공용.</summary>
    public static string Rfc3339(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>사람이 읽는 날짜(<c>2026-09-21</c>, UTC 기준).</summary>
    public static string DisplayDate(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
```

`Pages/PageNumber.cs`:

```csharp
using System.Globalization;

namespace PortfolioBlog.Api.Pages;

/// <summary><c>?page=</c>를 직접 읽는다. Razor Pages에서 <c>page</c>는 예약 라우트 키라 모델 바인딩으로는 읽을 수 없다.</summary>
public static class PageNumber
{
    /// <returns>없으면 1쪽으로 <c>true</c>. 값이 하나이고 ASCII 숫자 1~4자리이며 1..<paramref name="max"/>이면 <c>true</c>. 그 밖은 전부 <c>false</c>(호출부가 404).</returns>
    public static bool TryRead(IQueryCollection query, int max, out int page)
    {
        page = 1;
        if (!query.TryGetValue("page", out var values)) return true;
        if (values.Count != 1) return false;
        var raw = values[0];
        if (string.IsNullOrEmpty(raw) || raw.Length > 4) return false;
        foreach (var c in raw)
        {
            if (c is < '0' or > '9') return false; // char.IsDigit는 전각·다른 문자 체계의 숫자도 참이다
        }
        page = int.Parse(raw, NumberStyles.None, CultureInfo.InvariantCulture);
        return page >= 1 && page <= max;
    }
}
```

`Infrastructure/Markdown/HighlightCss.cs`:

```csharp
using ColorCode;
using ColorCode.Styling;

namespace PortfolioBlog.Api.Infrastructure.Markdown;

/// <summary>코드 강조용 CSS를 ColorCode의 스타일 사전에서 만든다. 파일로 두지 않는 이유: 클래스 이름이 라이브러리 버전과 항상 일치해야 한다.</summary>
public static class HighlightCss
{
    // Lazy<string>: 첫 요청에서 한 번만 만들고(약 4KB) 이후는 같은 문자열을 돌려준다. 기본 모드(ExecutionAndPublication)라 동시 첫 요청도 한 번만 계산한다.
    private static readonly Lazy<string> Cached = new(Build);

    /// <summary>밝은 테마 규칙 + <c>prefers-color-scheme: dark</c> 안의 어두운 테마 규칙.</summary>
    public static string Value => Cached.Value;

    private static string Build() =>
        ClassRules(StyleDictionary.DefaultLight) + "\n@media (prefers-color-scheme: dark){" + ClassRules(StyleDictionary.DefaultDark) + "}\n";

    // 기본 거부: 클래스 선택자로 시작하는 규칙만 남긴다. 라이브러리가 내는 body{background-color…}와
    // .plainText{color:…;color:…}(배경색을 color로 한 번 더 쓰는 버그 — 밝은 테마에서 글자가 흰색이 된다, 실측)는 버린다.
    private static string ClassRules(StyleDictionary styles) =>
        string.Concat(new HtmlClassFormatter(styles).GetCSSString()
            .Split('}', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static rule => rule.StartsWith('.') && !rule.StartsWith(".plainText{", StringComparison.Ordinal))
            .Select(static rule => rule + "}"));
}
```

Run: `dotnet test PortfolioBlog.slnx -c Release --filter "FullyQualifiedName~PublicUrlsTests|FullyQualifiedName~PageNumberTests|FullyQualifiedName~HighlightCssTests"` → Expected: 전부 통과.

- [ ] **Step 3: 페이지 통합 테스트(실패)**

`PortfolioBlog.Api.Tests/Infrastructure/HtmlDoc.cs`:

```csharp
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>응답 HTML을 브라우저와 같은 규칙(HTML5 파서)으로 읽는다. 문자열 검색으로 "태그가 없다"를 단언하지 않기 위한 도구.</summary>
internal static class HtmlDoc
{
    public static IHtmlDocument Parse(string html) => new HtmlParser().ParseDocument(html);

    public static async Task<IHtmlDocument> GetAsync(HttpClient client, string url, System.Net.HttpStatusCode expected = System.Net.HttpStatusCode.OK)
    {
        using var res = await client.GetAsync(url);
        Assert.Equal(expected, res.StatusCode);
        Assert.Equal("text/html", res.Content.Headers.ContentType?.MediaType);
        return Parse(await res.Content.ReadAsStringAsync());
    }
}
```

`PortfolioBlog.Api.Tests/Features/PublicPagesTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Infrastructure.Data;
using PortfolioBlog.Api.Infrastructure.Markdown;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>공개 Razor 페이지를 실제 파이프라인 + PostgreSQL로 검증한다. 목록 쪽수처럼 DB 전체에 의존하는 테스트는 격리된 팩토리를 만든다.</summary>
[Collection("postgres")]
public sealed class PublicPagesTests(ApiFactory factory, PostgresContainerFixture pg) : IClassFixture<ApiFactory>
{
    private const string Attachment = "/attachments/01234567-89ab-cdef-0123-456789abcdef/diagram.png";

    /// <summary>목록은 최신순 20개씩이고, 잘못된 쪽 번호는 전부 404다(보정하지 않는다).</summary>
    [Fact]
    public async Task Index_ListsNewestFirst_Paginates_AndRejectsBadPages()
    {
        using var isolated = new ApiFactory(pg, new Dictionary<string, string?>());
        var t0 = DbClock.UtcNow().AddDays(-1);
        for (var i = 0; i < 21; i++) await PublicSeed.PostAsync(isolated, $"p-{i:00}", $"글 {i}", createdAt: t0.AddMinutes(i));
        using var client = isolated.CreatePublicClient();

        var first = await HtmlDoc.GetAsync(client, "/");
        var titles = first.QuerySelectorAll("ul.post-list > li a.post-title").Select(a => a.TextContent).ToArray();
        Assert.Equal(20, titles.Length);
        Assert.Equal("글 20", titles[0]);
        Assert.Equal("/?page=2", first.QuerySelector("nav.pager a[rel=next]")?.GetAttribute("href"));

        var second = await HtmlDoc.GetAsync(client, "/?page=2");
        Assert.Equal(new[] { "글 0" }, second.QuerySelectorAll("ul.post-list > li a.post-title").Select(a => a.TextContent));
        Assert.Equal("/", second.QuerySelector("nav.pager a[rel=prev]")?.GetAttribute("href"));

        foreach (var bad in new[] { "/?page=0", "/?page=abc", "/?page=501", "/?page=3", "/?page=1&page=2", "/?page=" })
        {
            using var res = await client.GetAsync(bad);
            Assert.True(HttpStatusCode.NotFound == res.StatusCode, $"{bad}: {(int)res.StatusCode}");
        }
    }

    /// <summary>글 쪽: 본문은 정제된 HTML, 제목·요약은 인코딩, 머리 정보의 절대 URL은 PUBLIC_ORIGIN, 실행 가능한 것은 0개.</summary>
    [Fact]
    public async Task Post_RendersSafeHtml_AndHeadUsesPublicOrigin()
    {
        const string title = "<b>굵게</b> & \"따옴표\"";
        await PublicSeed.PostAsync(factory, "safe-post", title, summary: "요약 <i>x</i>",
            markdown: $"<script>alert(1)</script>\n\n![그림]({Attachment})\n\n```csharp\nvar a = 1;\n```\n\n[나쁜 링크](javascript:alert(1))", tags: ["C#"]);
        using var client = factory.CreatePublicClient();

        var doc = await HtmlDoc.GetAsync(client, "/posts/safe-post");

        Assert.Equal(title, doc.QuerySelector("article h1")?.TextContent);
        Assert.Null(doc.QuerySelector("article h1 b"));
        Assert.Empty(doc.QuerySelectorAll("script, style, iframe, object, embed, [style], [onerror], [onclick]"));
        Assert.DoesNotContain(doc.QuerySelectorAll("a[href]"), a => a.GetAttribute("href")!.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(doc.QuerySelector("div.article-body div.csharp span.keyword"));
        Assert.Equal(Attachment, doc.QuerySelector("div.article-body img")?.GetAttribute("src"));
        Assert.Equal("/tags/c%23", doc.QuerySelector("ul.tag-list a")?.GetAttribute("href"));

        Assert.Equal($"{title} · Blog", doc.Title);
        Assert.Equal("요약 <i>x</i>", doc.QuerySelector("meta[name=description]")?.GetAttribute("content"));
        Assert.Equal(ApiFactory.PublicOrigin + "/posts/safe-post", doc.QuerySelector("link[rel=canonical]")?.GetAttribute("href"));
        Assert.Equal(ApiFactory.PublicOrigin + "/posts/safe-post", doc.QuerySelector("meta[property='og:url']")?.GetAttribute("content"));
        Assert.Equal("article", doc.QuerySelector("meta[property='og:type']")?.GetAttribute("content"));
        Assert.Equal(ApiFactory.PublicOrigin + Attachment, doc.QuerySelector("meta[property='og:image']")?.GetAttribute("content"));
        Assert.Equal(new[] { "/css/site.css", "/css/highlight.css" }, doc.QuerySelectorAll("link[rel=stylesheet]").Select(l => l.GetAttribute("href")));
    }

    /// <summary>이미지 없는 글은 og:image를 아예 내지 않는다(스펙 3.4).</summary>
    [Fact]
    public async Task Post_WithoutImage_HasNoOgImage()
    {
        await PublicSeed.PostAsync(factory, "no-image", "그림 없음", markdown: "글만");
        using var client = factory.CreatePublicClient();
        Assert.Null((await HtmlDoc.GetAsync(client, "/posts/no-image")).QuerySelector("meta[property='og:image']"));
    }

    /// <summary>없는 글·형식 밖 slug는 고정 HTML 404이고 요청 값을 반사하지 않는다.</summary>
    [Theory]
    [InlineData("/posts/no-such-post")]
    [InlineData("/posts/Bad_Slug")]
    [InlineData("/posts/%3Cscript%3Ealert(1)%3C%2Fscript%3E")]
    [InlineData("/tags/no-such-tag")]
    [InlineData("/series/no-such-series")]
    public async Task Missing_Is404Html_WithoutReflection(string path)
    {
        using var client = factory.CreatePublicClient();
        var doc = await HtmlDoc.GetAsync(client, path, HttpStatusCode.NotFound);
        Assert.Equal("/", doc.QuerySelector("main a")?.GetAttribute("href"));
        Assert.Empty(doc.QuerySelectorAll("script"));
        Assert.DoesNotContain("alert", doc.DocumentElement.OuterHtml, StringComparison.Ordinal);
    }

    /// <summary>공개 페이지는 GET/HEAD 전용이고 공개 호스트에서만 열린다.</summary>
    [Fact]
    public async Task Pages_AreReadOnly_AndBoundToThePublicHost()
    {
        using var client = factory.CreatePublicClient();
        using var post = await client.PostAsync("/", new StringContent("x"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);

        using var admin = factory.CreateAdminClient();
        using var onAdminHost = await admin.GetAsync("/");
        Assert.Equal(HttpStatusCode.NotFound, onAdminHost.StatusCode);
    }

    /// <summary>같은 버전의 글은 한 번만 렌더링되고, 관리 API로 수정하면 저장 때 한 렌더가 캐시에 들어가 공개 쪽은 다시 렌더링하지 않는다.</summary>
    [Fact]
    public async Task Post_IsRenderedOncePerVersion_AndSavePrimesTheCache()
    {
        var seeded = await PublicSeed.PostAsync(factory, "cached-post", "캐시", markdown: "처음 본문");
        var gate = factory.Services.GetRequiredService<RenderGate>();
        using var client = factory.CreatePublicClient();

        var before = gate.RenderCount;
        await HtmlDoc.GetAsync(client, "/posts/cached-post");
        await HtmlDoc.GetAsync(client, "/posts/cached-post");
        Assert.Equal(before + 1, gate.RenderCount);

        using var admin = await factory.CreateLoggedInClientAsync();
        var current = await admin.GetFromJsonAsync<PostDetailDto>($"/api/posts/{seeded.Id}", TestJson.Options);
        using var put = await admin.PutAsJsonAsync($"/api/posts/{seeded.Id}", new
        {
            slug = "cached-post", title = "캐시", summary = "", contentMarkdown = "바뀐 본문", tagNames = Array.Empty<string>(), version = current!.Version,
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(before + 2, gate.RenderCount); // 저장 전 확인 렌더 1회

        var updated = await HtmlDoc.GetAsync(client, "/posts/cached-post");
        Assert.Contains("바뀐 본문", updated.QuerySelector("div.article-body")!.TextContent, StringComparison.Ordinal);
        Assert.Equal(before + 2, gate.RenderCount); // 공개 쪽은 선채움된 캐시를 썼다
    }

    /// <summary>태그 쪽은 URL 인코딩된 이름·대소문자 변형 모두 같은 정규화명으로 찾고, 시리즈 쪽은 순서대로, 글 쪽은 이전/다음을 보인다.</summary>
    [Fact]
    public async Task Tag_And_Series_Pages()
    {
        var series = await PublicSeed.SeriesAsync(factory, "gc-series", "GC 연재", "설명 <u>x</u>");
        var t0 = DbClock.UtcNow().AddDays(-2);
        await PublicSeed.PostAsync(factory, "gc-1", "GC 1편", tags: ["Tag Page C#"], seriesId: series.Id, seriesOrder: 1, createdAt: t0);
        await PublicSeed.PostAsync(factory, "gc-2", "GC 2편", seriesId: series.Id, seriesOrder: 2, createdAt: t0.AddMinutes(1));
        using var client = factory.CreatePublicClient();

        foreach (var url in new[] { "/tags/Tag%20Page%20C%23", "/tags/tag%20page%20c%23", "/tags/TAG%20%20PAGE%20C%23" })
        {
            var tagDoc = await HtmlDoc.GetAsync(client, url);
            Assert.Equal(new[] { "GC 1편" }, tagDoc.QuerySelectorAll("ul.post-list a.post-title").Select(a => a.TextContent));
            Assert.Equal(ApiFactory.PublicOrigin + "/tags/tag%20page%20c%23", tagDoc.QuerySelector("link[rel=canonical]")?.GetAttribute("href"));
        }

        var seriesDoc = await HtmlDoc.GetAsync(client, "/series/gc-series");
        Assert.Equal(new[] { "GC 1편", "GC 2편" }, seriesDoc.QuerySelectorAll("ol.series-posts a").Select(a => a.TextContent));
        Assert.Null(seriesDoc.QuerySelector("u")); // 설명은 인코딩된다

        var firstPost = await HtmlDoc.GetAsync(client, "/posts/gc-1");
        Assert.Equal("/series/gc-series", firstPost.QuerySelector("p.series-box a")?.GetAttribute("href"));
        Assert.Null(firstPost.QuerySelector("nav.post-nav a[rel=prev]"));
        Assert.Equal("/posts/gc-2", firstPost.QuerySelector("nav.post-nav a[rel=next]")?.GetAttribute("href"));
    }

    /// <summary>정적 CSS와 생성 CSS가 서빙되고, 사이트 CSS에는 id 선택자가 없으며(제목 id는 작성자 텍스트에서 나온다), wwwroot에는 그 파일 하나뿐이다.</summary>
    [Fact]
    public async Task Stylesheets_AreServed_SiteCssHasNoIdSelectors_AndWwwrootIsClosed()
    {
        using var client = factory.CreatePublicClient();
        using var site = await client.GetAsync("/css/site.css");
        Assert.Equal(HttpStatusCode.OK, site.StatusCode);
        Assert.Equal("text/css", site.Content.Headers.ContentType?.MediaType);
        using var highlight = await client.GetAsync("/css/highlight.css");
        Assert.Equal("text/css", highlight.Content.Headers.ContentType?.MediaType);
        Assert.Contains(".keyword{", await highlight.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var webRoot = factory.Services.GetRequiredService<IWebHostEnvironment>().WebRootPath;
        var files = Directory.EnumerateFiles(webRoot, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(webRoot, f).Replace('\\', '/')).Order().ToArray();
        Assert.Equal(new[] { "css/site.css" }, files);

        // 주석과 선언 블록({…}, 안쪽부터 반복 제거)을 지우면 선택자와 @규칙 머리만 남는다. 거기에 '#'이 있으면 id 선택자다.
        var css = Regex.Replace(await site.Content.ReadAsStringAsync(), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        string previous;
        do { previous = css; css = Regex.Replace(css, @"\{[^{}]*\}", string.Empty); } while (css != previous);
        Assert.DoesNotContain('#', css);
    }
}
```

`AccessMatrixTests.cs`:
- `PublicAllowlist`에 추가: `""`(목록 — Razor 페이지의 `RawText`에는 앞 슬래시가 없다), `"posts/{slug}"`, `"tags/{tag}"`, `"series/{slug}"`, `"/css/highlight.css"`. 요약의 "Plan 2B가 공개 페이지들을 추가한다"를 지운다.
- 새 테스트(공개 호스트 제한의 닫힌 세계):

```csharp
    /// <summary>양쪽 호스트에서 열려도 되는 <c>/api</c> 밖 라우트. 여기에 없는 공개 라우트는 전부 공개 호스트에만 매칭되어야 한다.</summary>
    private static readonly string[] SharedBetweenHosts = ["/health", "/openapi/{documentName}.json", "/attachments/{id:guid}/{fileName}"];

    /// <summary>공개 페이지·피드 라우트에 공개 호스트 제한을 빠뜨리면(그러면 관리 origin에서도 렌더링된다) 여기서 잡힌다.</summary>
    [Fact]
    public void EveryPublicRoute_ExceptTheSharedOnes_IsBoundToThePublicHost()
    {
        using var _ = factory.CreateClient();
        var publicHost = new Uri(ApiFactory.PublicOrigin).Host;
        var bound = 0;
        foreach (var endpoint in factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var raw = endpoint.RoutePattern.RawText ?? string.Empty;
            if (IsUnderApi(raw) || SharedBetweenHosts.Contains(raw, StringComparer.Ordinal)) continue;
            var hosts = endpoint.Metadata.GetMetadata<IHostMetadata>()?.Hosts ?? [];
            Assert.True(hosts.SequenceEqual([publicHost]), $"'{raw}': 공개 호스트 제한이 없다(실제: {string.Join(",", hosts)})");
            bound++;
        }
        Assert.True(bound >= 5, $"검사된 공개 라우트가 너무 적다: {bound}");
    }
```

Run: `dotnet build PortfolioBlog.slnx -c Release` → Expected: 컴파일은 되지만(테스트는 HTTP만 쓴다) `PublicPagesTests`가 전부 404로 실패한다. 실행해서 **실패를 확인**한다: `dotnet test PortfolioBlog.slnx -c Release --filter "FullyQualifiedName~PublicPagesTests"`.

- [ ] **Step 4: 옵션·규약·기반 클래스**

`SiteOptions.cs` — 속성 추가(각각 `<summary>`만. 같은 파일의 `PublicOrigin`·`AdminOrigin`에 붙은 상용구 `<remarks>`는 이참에 지운다 — 자동 속성이다):

```csharp
    /// <summary>사이트 이름. <c>&lt;title&gt;</c>·머리글·Atom 피드 제목에 쓴다. 비울 수 없다.</summary>
    public string Title { get; set; } = "Blog";

    /// <summary>사이트 한 줄 소개. 첫 쪽의 meta description과 Atom subtitle. 비어 있으면 생략한다.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Atom 피드의 작성자 이름. 비어 있으면 <see cref="Title"/>을 쓴다.</summary>
    public string Author { get; set; } = string.Empty;
```

`StartupValidation.Validate`: `if (string.IsNullOrWhiteSpace(site.Title)) throw new InvalidOperationException("설정 Site:Title 은(는) 비울 수 없습니다.");`
`appsettings.json`: `"Site": { "PublicOrigin": "", "AdminOrigin": "", "Title": "Blog", "Description": "", "Author": "" }`.

`Pages/PublicPageConvention.cs`:

```csharp
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Routing;
using PortfolioBlog.Api.Infrastructure.Web;

namespace PortfolioBlog.Api.Pages;

/// <summary>모든 Razor 페이지 엔드포인트에 공개 표면의 계약을 메타데이터로 건다: GET/HEAD 전용, 공개 호스트 전용, 속도 제한 정책.</summary>
/// <remarks>Razor 페이지는 기본적으로 모든 HTTP 메서드·모든 호스트에 매칭된다. 선택자의 엔드포인트 메타데이터에 넣으면 라우팅이 직접 집행한다
/// (실측: POST → 405 + Allow, 다른 호스트 → 404). 페이지마다 특성을 다는 방식은 새 페이지에서 빠뜨릴 수 있어 규약으로 한 번에 건다 —
/// 빠뜨린 경우는 <c>AccessMatrixTests</c>의 닫힌 세계 검사가 잡는다.</remarks>
public sealed class PublicPageConvention(string publicHost) : IPageRouteModelConvention
{
    public void Apply(PageRouteModel model)
    {
        var policy = string.Equals(model.ViewEnginePath, "/Search", StringComparison.Ordinal) ? RateLimitPolicy.Search : RateLimitPolicy.PublicPage;
        foreach (var selector in model.Selectors)
        {
            selector.EndpointMetadata.Add(new HttpMethodMetadata(["GET", "HEAD"]));
            selector.EndpointMetadata.Add(new HostAttribute(publicHost));
            selector.EndpointMetadata.Add(new RateLimitMetadata(policy));
        }
    }
}
```

`Pages/PageHead.cs`:

```csharp
namespace PortfolioBlog.Api.Pages;

/// <summary>레이아웃의 <c>&lt;head&gt;</c>가 쓰는 값. 전부 Razor가 인코딩해 출력한다.</summary>
/// <param name="Title"><c>&lt;title&gt;</c>과 og:title.</param>
/// <param name="SiteTitle">머리글·og:site_name·피드 링크 제목.</param>
/// <param name="Description">meta description·og:description. 비어 있으면 생략.</param>
/// <param name="CanonicalUrl"><c>Site:PublicOrigin</c>으로 만든 절대 URL(요청 Host가 아니다).</param>
/// <param name="OgType"><c>website</c> 또는 <c>article</c>.</param>
/// <param name="OgImageUrl">절대 URL. 없으면 og:image를 내지 않는다.</param>
/// <param name="NoIndex">검색 결과 쪽처럼 색인하지 말아야 하는 쪽.</param>
public sealed record PageHead(string Title, string SiteTitle, string? Description, string CanonicalUrl, string OgType, string? OgImageUrl, bool NoIndex);
```

`Pages/PublicPageModel.cs`:

```csharp
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Infrastructure.Access;

namespace PortfolioBlog.Api.Pages;

/// <summary>공개 페이지 모델의 공통 기반: 사이트 설정과 머리 정보 설정.</summary>
public abstract class PublicPageModel(IOptions<SiteOptions> site) : PageModel
{
    /// <summary>레이아웃이 머리 정보를 읽는 ViewData 키.</summary>
    public const string HeadKey = "Head";

    protected SiteOptions Site { get; } = site.Value;

    /// <param name="path">이 쪽의 정식 경로(<c>/</c>로 시작). 절대 URL은 <c>Site:PublicOrigin</c>을 붙여 만든다.</param>
    /// <param name="imagePath"><c>/attachments/…</c> 경로. 렌더러의 URL 정책을 통과한 값만 넘긴다.</param>
    protected void SetHead(string? title, string? description, string path, string ogType = "website", string? imagePath = null, bool noIndex = false) =>
        ViewData[HeadKey] = new PageHead(
            string.IsNullOrEmpty(title) ? Site.Title : $"{title} · {Site.Title}", Site.Title,
            string.IsNullOrWhiteSpace(description) ? null : description,
            Site.PublicOrigin + path, ogType, imagePath is null ? null : Site.PublicOrigin + imagePath, noIndex);
}
```

`Pages/PagerModel.cs`:

```csharp
namespace PortfolioBlog.Api.Pages;

/// <summary>이전/다음 링크. <c>asp-route-page</c>는 Razor Pages의 예약 키와 충돌하므로 문자열로 만든다.</summary>
/// <param name="BasePath">쪽 번호를 뺀 경로(<c>/</c>, <c>/tags/c%23</c>, <c>/search</c>). 이미 인코딩된 값.</param>
/// <param name="Query">검색어(원문). 없으면 <c>null</c>.</param>
public sealed record PagerModel(string BasePath, string? Query, int Page, int LastPage)
{
    public string Href(int page)
    {
        var q = Query is null ? string.Empty : "q=" + Uri.EscapeDataString(Query) + "&";
        return page == 1 && Query is null ? BasePath : $"{BasePath}?{q}page={page}";
    }
}
```

`Pages/SiteEndpoints.cs`:

```csharp
using System.Text;
using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Infrastructure.Access;
using PortfolioBlog.Api.Infrastructure.Markdown;
using PortfolioBlog.Api.Infrastructure.Web;

namespace PortfolioBlog.Api.Pages;

/// <summary>HTML이 아닌 공개 엔드포인트. 전부 GET/HEAD·공개 호스트 전용이며 <c>AccessMatrixTests.PublicAllowlist</c>에 올라 있다.</summary>
public static class SiteEndpoints
{
    /// <summary>코드 강조 CSS 경로.</summary>
    public const string HighlightCssPattern = "/css/highlight.css";

    public static void MapPublicSiteEndpoints(this WebApplication app)
    {
        var publicHost = SiteOptions.HostOf(app.Services.GetRequiredService<IOptions<SiteOptions>>().Value.PublicOrigin);

        app.MapMethods(HighlightCssPattern, ["GET", "HEAD"], static (HttpContext http) =>
            {
                http.Response.Headers.CacheControl = "public, max-age=86400";
                return TypedResults.Text(HighlightCss.Value, "text/css", Encoding.UTF8);
            })
            .RequireHost(publicHost).AllowAnonymous()
            .WithMetadata(new RateLimitMetadata(RateLimitPolicy.PublicAsset)).WithName("GetHighlightCss");
    }
}
```

`Program.cs`:

```csharp
// (using) Microsoft.AspNetCore.Mvc.RazorPages, PortfolioBlog.Api.Pages
builder.Services.AddRazorPages();
// 규약은 설정(공개 호스트)이 필요하므로 옵션 지연 구성으로 단다(Build 이후 첫 해석).
builder.Services.AddOptions<RazorPagesOptions>().Configure<IOptions<SiteOptions>>((o, site) =>
    o.Conventions.Add(new PublicPageConvention(SiteOptions.HostOf(site.Value.PublicOrigin))));
```

파이프라인: `app.UseStatusCodePages(…);` 다음 줄에

```csharp
// 정적 파일은 wwwroot/css/site.css 하나뿐이다. 엔드포인트가 이미 매칭된 요청은 이 미들웨어가 건드리지 않는다.
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = static ctx => ctx.Context.Response.Headers.CacheControl = "public, max-age=3600" });
```

매핑: `app.MapPublicAttachmentEndpoints();` 다음에 `app.MapRazorPages();`와 `app.MapPublicSiteEndpoints();`.

- [ ] **Step 5: 뷰**

`Pages/_ViewImports.cshtml`(태그 헬퍼를 등록하지 않는다):

```cshtml
@using PortfolioBlog.Api.Pages
@using PortfolioBlog.Api.Infrastructure.Data
@using PortfolioBlog.Api.Infrastructure.Web
@namespace PortfolioBlog.Api.Pages
```

`Pages/_ViewStart.cshtml`:

```cshtml
@{
    Layout = "_Layout";
}
```

`Pages/Shared/_Layout.cshtml`:

```cshtml
@{
    var head = (PageHead)ViewData[PublicPageModel.HeadKey]!;
}
<!doctype html>
<html lang="ko">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>@head.Title</title>
    @if (head.Description is not null)
    {
        <meta name="description" content="@head.Description">
        <meta property="og:description" content="@head.Description">
    }
    @if (head.NoIndex)
    {
        <meta name="robots" content="noindex">
    }
    <link rel="canonical" href="@head.CanonicalUrl">
    <link rel="alternate" type="application/atom+xml" title="@head.SiteTitle" href="/feed.xml">
    <meta property="og:site_name" content="@head.SiteTitle">
    <meta property="og:title" content="@head.Title">
    <meta property="og:type" content="@head.OgType">
    <meta property="og:url" content="@head.CanonicalUrl">
    @if (head.OgImageUrl is not null)
    {
        <meta property="og:image" content="@head.OgImageUrl">
    }
    <link rel="stylesheet" href="/css/site.css">
    <link rel="stylesheet" href="/css/highlight.css">
</head>
<body>
    <header class="site-header">
        <a class="site-title" href="/">@head.SiteTitle</a>
        <nav class="site-nav"><a href="/search">검색</a> <a href="/feed.xml">피드</a></nav>
    </header>
    <main>
        @RenderBody()
    </main>
    <footer class="site-footer">@head.SiteTitle</footer>
</body>
</html>
```

`Pages/Shared/_TagList.cshtml`:

```cshtml
@model IReadOnlyList<PublicTag>
@if (Model.Count > 0)
{
    <ul class="tag-list">
        @foreach (var tag in Model)
        {
            var href = PublicUrls.Tag(tag.NormalizedName);
            <li>
                @if (href is null)
                {
                    <span class="tag">@tag.Name</span>
                }
                else
                {
                    <a class="tag" href="@href">@tag.Name</a>
                }
            </li>
        }
    </ul>
}
```

`Pages/Shared/_PostList.cshtml`:

```cshtml
@model IReadOnlyList<PublicPostSummary>
@if (Model.Count == 0)
{
    <p class="empty">글이 없습니다.</p>
}
else
{
    <ul class="post-list">
        @foreach (var post in Model)
        {
            <li>
                <a class="post-title" href="@PublicUrls.Post(post.Slug)">@post.Title</a>
                <time datetime="@PublicFormat.Rfc3339(post.CreatedAt)">@PublicFormat.DisplayDate(post.CreatedAt)</time>
                @if (post.Summary.Length > 0)
                {
                    <p class="post-summary">@post.Summary</p>
                }
                @await Html.PartialAsync("_TagList", post.Tags)
            </li>
        }
    </ul>
}
```

`Pages/Shared/_Pager.cshtml`:

```cshtml
@model PagerModel
@if (Model.LastPage > 1)
{
    <nav class="pager">
        @if (Model.Page > 1)
        {
            <a rel="prev" href="@Model.Href(Model.Page - 1)">이전</a>
        }
        <span>@Model.Page / @Model.LastPage</span>
        @if (Model.Page < Model.LastPage)
        {
            <a rel="next" href="@Model.Href(Model.Page + 1)">다음</a>
        }
    </nav>
}
```

`Pages/Index.cshtml`:

```cshtml
@page "/"
@model IndexModel
<h1 class="list-title">최신 글</h1>
@await Html.PartialAsync("_PostList", Model.Posts.Items)
@await Html.PartialAsync("_Pager", Model.Pager)
```

`Pages/Index.cshtml.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Infrastructure.Access;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Pages;

/// <summary>공개 첫 쪽: 최신 글 목록(20개씩, 상한 500쪽).</summary>
public sealed class IndexModel(PublicDbContext db, IOptions<SiteOptions> site) : PublicPageModel(site)
{
    /// <summary>쪽 번호 상한(스펙 3.4). OFFSET 비용의 상한이기도 하다.</summary>
    public const int MaxPage = 500;

    public PublicPage<PublicPostSummary> Posts { get; private set; } = null!;

    public PagerModel Pager => new("/", null, Posts.Page, Math.Min(Posts.LastPage, MaxPage));

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!PageNumber.TryRead(Request.Query, MaxPage, out var page)) return NotFound();
        Posts = await PublicQueries.LatestAsync(db, page, ct);
        if (page > 1 && Posts.Items.Count == 0) return NotFound();
        SetHead(page == 1 ? null : $"{page}쪽", Site.Description, page == 1 ? "/" : $"/?page={page}");
        return Page();
    }
}
```

`Pages/Post.cshtml`:

```cshtml
@page "/posts/{slug}"
@model PostModel
<article>
    <header class="article-header">
        <h1>@Model.Meta.Title</h1>
        <p class="article-meta">
            <time datetime="@PublicFormat.Rfc3339(Model.Meta.CreatedAt)">@PublicFormat.DisplayDate(Model.Meta.CreatedAt)</time>
            @if (Model.Meta.UpdatedAt > Model.Meta.CreatedAt)
            {
                <span class="updated">수정 <time datetime="@PublicFormat.Rfc3339(Model.Meta.UpdatedAt)">@PublicFormat.DisplayDate(Model.Meta.UpdatedAt)</time></span>
            }
        </p>
        @await Html.PartialAsync("_TagList", Model.Meta.Tags)
        @if (Model.Meta.Series is { } series)
        {
            <p class="series-box">시리즈 <a href="@PublicUrls.Series(series.Slug)">@series.Title</a></p>
        }
    </header>
    @if (Model.BodyHtml is null)
    {
        <p class="notice">이 글의 본문을 표시할 수 없습니다.</p>
    }
    else
    {
        @* Html.Raw는 여기 한 곳뿐이다: MarkdownRenderer가 URL 정책·허용 목록 정제를 끝낸 출력만 받는다. *@
        <div class="article-body">@Html.Raw(Model.BodyHtml)</div>
    }
    <nav class="post-nav">
        @if (Model.Meta.Previous is { } previous)
        {
            <a rel="prev" href="@PublicUrls.Post(previous.Slug)">이전 편: @previous.Title</a>
        }
        @if (Model.Meta.Next is { } next)
        {
            <a rel="next" href="@PublicUrls.Post(next.Slug)">다음 편: @next.Title</a>
        }
    </nav>
</article>
```

`Pages/Post.cshtml.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Infrastructure.Access;
using PortfolioBlog.Api.Infrastructure.Data;
using PortfolioBlog.Api.Infrastructure.Markdown;
using PortfolioBlog.Api.Infrastructure.Web;

namespace PortfolioBlog.Api.Pages;

/// <summary>글 상세. 본문은 캐시 미스일 때만 DB에서 읽고, 렌더는 버전당 한 번이다.</summary>
public sealed class PostModel(PublicDbContext db, RenderedPostCache cache, IOptions<SiteOptions> site) : PublicPageModel(site)
{
    public PublicPostMeta Meta { get; private set; } = null!;

    /// <summary>정제된 본문 HTML. 렌더러가 거부한 글(중첩 한도 초과 — 렌더러 규칙이 저장 이후에 엄격해진 경우)이면 <c>null</c>.</summary>
    public string? BodyHtml { get; private set; }

    public async Task<IActionResult> OnGetAsync(string slug, CancellationToken ct)
    {
        // 형식 밖 slug(대문자·밑줄·NUL·100자 초과)는 DB에 가지 않는다.
        if (!SlugRules.IsValid(slug)) return NotFound();
        var meta = await PublicQueries.GetPostAsync(db, slug, ct);
        if (meta is null) return NotFound();

        var rendered = cache.TryGet(meta.Id, meta.Version, out var hit) ? hit : await RenderAsync(meta.Id, ct);
        BodyHtml = rendered?.Html;
        Meta = meta;
        SetHead(meta.Title, meta.Summary, PublicUrls.Post(meta.Slug), "article", rendered?.FirstImageUrl);
        return Page();
    }

    private async Task<RenderedMarkdown?> RenderAsync(Guid id, CancellationToken ct)
    {
        var content = await PublicQueries.GetContentAsync(db, id, ct);
        if (content is null) return null; // 메타데이터를 읽은 직후 삭제됐다 — 본문 없는 쪽으로 보여 준다(다음 요청은 404)
        try
        {
            return await cache.GetOrRenderAsync(id, content.Version, content.Markdown, ct);
        }
        catch (MarkdownTooComplexException)
        {
            return null;
        }
    }
}
```

`Pages/Tag.cshtml`:

```cshtml
@page "/tags/{tag}"
@model TagPageModel
<h1 class="list-title">태그: @Model.Tag.Name</h1>
@await Html.PartialAsync("_PostList", Model.Posts.Items)
@await Html.PartialAsync("_Pager", Model.Pager)
```

`Pages/Tag.cshtml.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Infrastructure.Access;
using PortfolioBlog.Api.Infrastructure.Data;
using PortfolioBlog.Api.Infrastructure.Web;

namespace PortfolioBlog.Api.Pages;

/// <summary>태그별 글 목록. 경로 값은 라우팅이 URL 디코딩한 뒤의 문자열이며, 저장 때와 같은 규칙으로 정규화해 찾는다.</summary>
public sealed class TagPageModel(PublicDbContext db, IOptions<SiteOptions> site) : PublicPageModel(site)
{
    public PublicTag Tag { get; private set; } = null!;

    public PublicPage<PublicPostSummary> Posts { get; private set; } = null!;

    public PagerModel Pager => new(PublicUrls.Tag(Tag.NormalizedName)!, null, Posts.Page, Math.Min(Posts.LastPage, IndexModel.MaxPage));

    public async Task<IActionResult> OnGetAsync(string tag, CancellationToken ct)
    {
        // 정규화 전에 길이를 먼저 자른다: 정규화(NFC)는 입력 길이에 비례하는 작업이다. 공백 축소 여지를 두고 상한의 4배까지만 받는다.
        if (tag.Length > AppDbContext.TagMax * 4 || TextRules.ContainsNul(tag)) return NotFound();
        var key = TagResolver.Normalize(tag);
        if (key.Length is 0 or > AppDbContext.TagMax || PublicUrls.Tag(key) is not { } path) return NotFound();
        if (!PageNumber.TryRead(Request.Query, IndexModel.MaxPage, out var page)) return NotFound();

        var found = await PublicQueries.ByTagAsync(db, key, page, ct);
        if (found is null || (page > 1 && found.Value.Posts.Items.Count == 0)) return NotFound();
        (Tag, Posts) = found.Value;
        SetHead($"태그: {Tag.Name}", null, page == 1 ? path : $"{path}?page={page}");
        return Page();
    }
}
```

`Pages/Series.cshtml`:

```cshtml
@page "/series/{slug}"
@model SeriesPageModel
<h1 class="list-title">시리즈: @Model.Series.Title</h1>
@if (Model.Series.Description.Length > 0)
{
    <p class="series-description">@Model.Series.Description</p>
}
<ol class="series-posts">
    @foreach (var entry in Model.Series.Posts)
    {
        <li>
            <a href="@PublicUrls.Post(entry.Slug)">@entry.Title</a>
            <time datetime="@PublicFormat.Rfc3339(entry.CreatedAt)">@PublicFormat.DisplayDate(entry.CreatedAt)</time>
        </li>
    }
</ol>
```

`Pages/Series.cshtml.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Infrastructure.Access;
using PortfolioBlog.Api.Infrastructure.Data;
using PortfolioBlog.Api.Infrastructure.Web;

namespace PortfolioBlog.Api.Pages;

/// <summary>시리즈 설명 + 순서대로 글 목록(상한 <see cref="PublicQueries.SeriesMax"/>).</summary>
public sealed class SeriesPageModel(PublicDbContext db, IOptions<SiteOptions> site) : PublicPageModel(site)
{
    public PublicSeries Series { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(string slug, CancellationToken ct)
    {
        if (!SlugRules.IsValid(slug)) return NotFound();
        var series = await PublicQueries.GetSeriesAsync(db, slug, ct);
        if (series is null) return NotFound();
        Series = series;
        SetHead($"시리즈: {series.Title}", series.Description, PublicUrls.Series(series.Slug));
        return Page();
    }
}
```

`wwwroot/css/site.css`(id 선택자·`@import`·외부 URL 없음. 색은 변수로 두고 어두운 테마는 `prefers-color-scheme`으로만 바꾼다):

```css
:root {
  --bg: #ffffff; --fg: #1f2328; --muted: #656d76; --line: #d0d7de; --accent: #0969da; --code-bg: #f6f8fa;
  color-scheme: light dark;
}
@media (prefers-color-scheme: dark) {
  :root { --bg: #0d1117; --fg: #e6edf3; --muted: #8d96a0; --line: #30363d; --accent: #4493f8; --code-bg: #161b22; }
}
* { box-sizing: border-box; }
html { font-size: 16px; }
body {
  margin: 0 auto; max-width: 46rem; padding: 0 1rem 3rem; background: var(--bg); color: var(--fg);
  font-family: system-ui, -apple-system, "Segoe UI", "Noto Sans KR", sans-serif; line-height: 1.7;
}
a { color: var(--accent); text-decoration: none; }
a:hover { text-decoration: underline; }
time { color: var(--muted); font-size: 0.875rem; }
.site-header { display: flex; justify-content: space-between; align-items: baseline; padding: 1.5rem 0; border-bottom: 1px solid var(--line); margin-bottom: 2rem; }
.site-title { font-weight: 700; font-size: 1.25rem; color: var(--fg); }
.site-nav a { margin-left: 1rem; }
.site-footer { margin-top: 4rem; padding-top: 1rem; border-top: 1px solid var(--line); color: var(--muted); font-size: 0.875rem; }
.list-title { font-size: 1.25rem; margin: 0 0 1.5rem; }
.post-list, .series-posts { padding: 0; margin: 0; }
.post-list { list-style: none; }
.post-list > li { padding: 1rem 0; border-bottom: 1px solid var(--line); }
.post-title { display: block; font-size: 1.125rem; font-weight: 600; }
.post-summary { margin: 0.25rem 0 0; color: var(--muted); }
.series-posts { padding-left: 1.5rem; }
.series-posts > li { padding: 0.25rem 0; }
.series-posts time { margin-left: 0.5rem; }
.tag-list { list-style: none; display: flex; flex-wrap: wrap; gap: 0.5rem; padding: 0; margin: 0.5rem 0 0; }
.tag { font-size: 0.8125rem; padding: 0.125rem 0.5rem; border: 1px solid var(--line); border-radius: 1rem; }
.pager { display: flex; justify-content: center; gap: 1.5rem; margin-top: 2rem; color: var(--muted); }
.empty, .notice { color: var(--muted); }
.article-header h1 { font-size: 1.75rem; line-height: 1.3; margin: 0 0 0.5rem; }
.article-meta { margin: 0; }
.updated { margin-left: 0.75rem; color: var(--muted); font-size: 0.875rem; }
.series-box { margin: 1rem 0 0; padding: 0.5rem 0.75rem; border-left: 3px solid var(--accent); background: var(--code-bg); }
.article-body { margin-top: 2rem; overflow-wrap: anywhere; }
.article-body img { max-width: 100%; height: auto; }
.article-body pre { background: var(--code-bg); padding: 1rem; overflow-x: auto; border-radius: 6px; font-size: 0.875rem; line-height: 1.5; }
.article-body code { font-family: ui-monospace, "Cascadia Code", Consolas, monospace; }
.article-body :not(pre) > code { background: var(--code-bg); padding: 0.125rem 0.25rem; border-radius: 4px; font-size: 0.875em; }
.article-body blockquote { margin: 1rem 0; padding: 0 1rem; border-left: 3px solid var(--line); color: var(--muted); }
.article-body table { border-collapse: collapse; display: block; overflow-x: auto; }
.article-body th, .article-body td { border: 1px solid var(--line); padding: 0.375rem 0.75rem; }
.article-body hr { border: 0; border-top: 1px solid var(--line); margin: 2rem 0; }
.article-body .contains-task-list { list-style: none; padding-left: 1rem; }
.article-body .footnotes { margin-top: 3rem; font-size: 0.875rem; color: var(--muted); }
.post-nav { display: flex; justify-content: space-between; gap: 1rem; margin-top: 3rem; padding-top: 1rem; border-top: 1px solid var(--line); }
.search-form { display: flex; gap: 0.5rem; margin-bottom: 1.5rem; }
.search-form input { flex: 1; padding: 0.5rem; border: 1px solid var(--line); border-radius: 6px; background: var(--bg); color: var(--fg); font: inherit; }
.search-form button { padding: 0.5rem 1rem; border: 1px solid var(--line); border-radius: 6px; background: var(--code-bg); color: var(--fg); font: inherit; }
.error-page { text-align: center; padding-top: 4rem; }
```

- [ ] **Step 6: 통과 확인**

Run: `dotnet test PortfolioBlog.slnx -c Release --filter "FullyQualifiedName~PublicPagesTests|FullyQualifiedName~AccessMatrixTests|FullyQualifiedName~SecurityHeadersTests"`
Expected: 전부 통과. 빌드 경고 0(Razor 생성 코드의 nullable 경고 포함).
**규칙 8 확인:** (a) `PublicPageConvention`에서 `HostAttribute` 줄을 잠깐 빼면 `EveryPublicRoute_…_IsBoundToThePublicHost`와 `Pages_AreReadOnly_AndBoundToThePublicHost`가 실패한다. (b) `HttpMethodMetadata` 줄을 빼면 `EveryRouteOutsideApi_IsOnThePublicAllowlist_AndReadOnly`가 실패한다. (c) `PostModel`에서 캐시를 건너뛰고 매번 `gate`로 렌더하게 바꾸면 `Post_IsRenderedOncePerVersion…`이 실패한다. 셋 다 보고 되돌린다.

- [ ] **Step 7: 전체 회귀 + 커밋**

Run: `dotnet build PortfolioBlog.slnx -c Release`(경고 0) → `dotnet test PortfolioBlog.slnx -c Release`

```bash
git add -A
git commit -m "추가: 스크립트 없는 공개 Razor 페이지(목록·글·태그·시리즈)와 사이트 CSS

- 규약 하나로 모든 페이지를 GET/HEAD·공개 호스트 전용·속도 제한 대상으로 고정, 접근 매트릭스가 닫힌 세계로 검사
- 글 본문은 캐시 미스일 때만 DB에서 읽고 버전당 한 번 렌더링
- 머리 정보의 절대 URL은 PUBLIC_ORIGIN, og:image는 URL 정책을 통과한 첫 첨부
- 잘못된 쪽 번호·slug·태그는 입력을 반사하지 않는 404
- 코드 강조 CSS는 ColorCode 스타일에서 생성(클래스 규칙만), 사이트 CSS는 id 선택자 없음

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: 공개 검색 페이지

**Files:**
- Create: `PortfolioBlog.Api/Pages/Search.cshtml`, `Search.cshtml.cs`
- Test: `PortfolioBlog.Api.Tests/Features/SearchPageTests.cs`; Modify `Features/AccessMatrixTests.cs`(`PublicAllowlist`에 `"search"`)

**Interfaces:**
- Consumes: `PublicQueries.SearchAsync`, `PageNumber.TryRead`, `PagerModel`, `PublicPageModel.SetHead(noIndex: true)`, `TextRules.ContainsNul`, `PublicPageConvention`(`/Search` → `RateLimitPolicy.Search`), `HtmlDoc`, `PublicSeed`.
- Produces: `SearchModel.MinLength = 2`, `MaxLength = 100`, `MaxPage = 50`(스펙 3.7).

**동작:** `q`가 없거나 공백뿐이면 빈 폼(200). `q`가 하나이고 트림 후 2~100자이면 검색. 그 밖(1자, 100자 초과, NUL, `q` 반복)은 **400**으로 같은 페이지를 렌더하고 안내문을 보인다 — 너무 길거나 NUL이 든 값은 입력란에 되돌리지 않는다. 쪽 번호 오류는 404. 결과 쪽은 `noindex`.

- [ ] **Step 1: 실패하는 테스트**

`PortfolioBlog.Api.Tests/Features/SearchPageTests.cs`:

```csharp
using System.Net;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PortfolioBlog.Api.Infrastructure.Web;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>공개 검색: 입력 경계, 와일드카드 리터럴 처리, 반사 인코딩, 자원 한도.</summary>
[Collection("postgres")]
public sealed class SearchPageTests(ApiFactory factory, PostgresContainerFixture pg) : IClassFixture<ApiFactory>
{
    /// <summary>검색어 없이 열면 빈 폼이다. GET 폼이며 숨은 필드(antiforgery 토큰)가 없다.</summary>
    [Fact]
    public async Task Empty_ShowsTheFormOnly()
    {
        using var client = factory.CreatePublicClient();
        var doc = await HtmlDoc.GetAsync(client, "/search");
        var form = doc.QuerySelector("form.search-form")!;
        Assert.Equal("get", form.GetAttribute("method"));
        Assert.Equal("/search", form.GetAttribute("action"));
        Assert.Empty(form.QuerySelectorAll("input[type=hidden]"));
        Assert.Null(doc.QuerySelector("ul.post-list"));
        Assert.Equal("noindex", doc.QuerySelector("meta[name=robots]")?.GetAttribute("content"));
    }

    /// <summary>제목·요약·본문에서 찾고, % 와 _ 는 글자 그대로다.</summary>
    [Fact]
    public async Task Finds_InTitleSummaryAndBody_WithLiteralWildcards()
    {
        await PublicSeed.PostAsync(factory, "s-title", "검색대상제목 100% 완료");
        await PublicSeed.PostAsync(factory, "s-summary", "다른 글", summary: "요약 속 검색대상요약");
        await PublicSeed.PostAsync(factory, "s-body", "또 다른 글 100 완료", markdown: "본문 속 검색대상본문 a_b");
        using var client = factory.CreatePublicClient();

        async Task<string[]> SlugsAsync(string q) =>
            (await HtmlDoc.GetAsync(client, "/search?q=" + Uri.EscapeDataString(q)))
                .QuerySelectorAll("ul.post-list a.post-title").Select(a => a.GetAttribute("href")!).Order().ToArray();

        Assert.Equal(new[] { "/posts/s-title" }, await SlugsAsync("검색대상제목"));
        Assert.Equal(new[] { "/posts/s-summary" }, await SlugsAsync("검색대상요약"));
        Assert.Equal(new[] { "/posts/s-body" }, await SlugsAsync("검색대상본문"));
        Assert.Equal(new[] { "/posts/s-title" }, await SlugsAsync("100%"));   // %가 와일드카드면 s-body도 나온다
        Assert.Equal(new[] { "/posts/s-body" }, await SlugsAsync("a_b"));
        Assert.Empty(await SlugsAsync("%%"));
    }

    /// <summary>검색어는 입력란에 인코딩되어 되돌아올 뿐, 요소가 되지 않는다.</summary>
    [Fact]
    public async Task Query_IsReflectedOnlyAsAnEncodedInputValue()
    {
        const string q = "<script>alert(1)</script>\"><img src=x onerror=alert(1)>";
        using var client = factory.CreatePublicClient();
        var doc = await HtmlDoc.GetAsync(client, "/search?q=" + Uri.EscapeDataString(q));
        Assert.Equal(q, doc.QuerySelector("form.search-form input[name=q]")?.GetAttribute("value"));
        Assert.Empty(doc.QuerySelectorAll("script, img, [onerror]"));
    }

    /// <summary>경계 밖 검색어는 400(안내문), 쪽 번호 오류는 404. 길이 초과·NUL 값은 입력란에 되돌리지 않는다.</summary>
    [Theory]
    [InlineData("/search?q=a", 400, "a")]
    [InlineData("/search?q=%20a%20", 400, "a")]
    [InlineData("/search?q=ab&q=cd", 400, "")]
    [InlineData("/search?q=ab%00cd", 400, "")]
    [InlineData("/search?q=zzqq&page=51", 404, null)]
    [InlineData("/search?q=zzqq&page=x", 404, null)]
    [InlineData("/search?q=zzqq&page=2", 404, null)] // 결과가 없는 쪽
    public async Task InvalidInput(string url, int status, string? echoed)
    {
        using var client = factory.CreatePublicClient();
        var doc = await HtmlDoc.GetAsync(client, url, (HttpStatusCode)status);
        if (echoed is null) return;
        Assert.NotNull(doc.QuerySelector("p.notice"));
        Assert.Equal(echoed, doc.QuerySelector("form.search-form input[name=q]")?.GetAttribute("value"));
    }

    /// <summary>100자는 되고 101자는 400이며 되돌리지 않는다.</summary>
    [Fact]
    public async Task LengthBoundary()
    {
        using var client = factory.CreatePublicClient();
        await HtmlDoc.GetAsync(client, "/search?q=" + new string('z', 100));
        var tooLong = await HtmlDoc.GetAsync(client, "/search?q=" + new string('z', 101), HttpStatusCode.BadRequest);
        Assert.Equal(string.Empty, tooLong.QuerySelector("form.search-form input[name=q]")?.GetAttribute("value"));
    }

    /// <summary>검색 페이지 엔드포인트에 검색 정책이 붙어 있고(파일 이름이 바뀌어 규약이 빗나가면 실패), 한도를 넘으면 429 HTML + Retry-After다. 다른 페이지는 영향이 없다.</summary>
    [Fact]
    public async Task Search_HasItsOwnRateLimit()
    {
        using var isolated = new ApiFactory(pg, new Dictionary<string, string?> { ["Public:SearchPerIpPerMinute"] = "2" });
        using var client = isolated.CreatePublicClient();
        var endpoint = isolated.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().Single(e => e.RoutePattern.RawText == "search");
        Assert.Equal(RateLimitPolicy.Search, endpoint.Metadata.GetMetadata<RateLimitMetadata>()?.Policy);

        await HtmlDoc.GetAsync(client, "/search?q=ab");
        await HtmlDoc.GetAsync(client, "/search?q=ab");
        using var third = await client.GetAsync("/search?q=ab");
        Assert.Equal((HttpStatusCode)429, third.StatusCode);
        Assert.True(third.Headers.Contains("Retry-After"));
        Assert.Equal("text/html", third.Content.Headers.ContentType?.MediaType);
        await HtmlDoc.GetAsync(client, "/"); // 페이지 한도는 남아 있다
    }
}
```

> `isolated.Services`는 호스트를 기동한다(`CreatePublicClient()`가 먼저 호출되므로 이미 떠 있다).

Run: `dotnet test PortfolioBlog.slnx -c Release --filter "FullyQualifiedName~SearchPageTests"` → Expected: 전부 실패(404).

- [ ] **Step 2: 구현**

`Pages/Search.cshtml`:

```cshtml
@page "/search"
@model SearchModel
<h1 class="list-title">검색</h1>
<form class="search-form" method="get" action="/search" role="search">
    <input type="search" name="q" value="@Model.Query" maxlength="@SearchModel.MaxLength" aria-label="검색어">
    <button type="submit">검색</button>
</form>
@if (Model.Error is not null)
{
    <p class="notice">@Model.Error</p>
}
else if (Model.Posts is not null)
{
    <p class="search-count">@Model.Posts.Total건</p>
    @await Html.PartialAsync("_PostList", Model.Posts.Items)
    @await Html.PartialAsync("_Pager", Model.Pager)
}
```

`Pages/Search.cshtml.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Infrastructure.Access;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Pages;

/// <summary>공개 검색. 비용 상한은 세 겹이다: 검색 속도 제한(IP별 분당 + 전역 동시 실행), 쪽 번호 상한, 연결의 statement_timeout.</summary>
public sealed class SearchModel(PublicDbContext db, IOptions<SiteOptions> site) : PublicPageModel(site)
{
    /// <summary>검색어 최소 길이(트림 후, UTF-16 단위). 한 글자 검색은 거의 모든 글에 맞아 전체 스캔 + 큰 결과만 만든다.</summary>
    public const int MinLength = 2;

    /// <summary>검색어 최대 길이(스펙 3.4).</summary>
    public const int MaxLength = 100;

    /// <summary>검색 결과 쪽 번호 상한(스펙 3.7).</summary>
    public const int MaxPage = 50;

    /// <summary>입력란에 되돌릴 값. 길이 초과·NUL·반복 매개변수면 빈 문자열.</summary>
    public string Query { get; private set; } = string.Empty;

    public string? Error { get; private set; }

    public PublicPage<PublicPostSummary>? Posts { get; private set; }

    public PagerModel Pager => new("/search", Query, Posts!.Page, Math.Min(Posts.LastPage, MaxPage));

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        SetHead("검색", null, "/search", noIndex: true);
        var values = Request.Query["q"];
        if (values.Count > 1) return Invalid("검색어는 하나만 보낼 수 있습니다.");
        var term = values.Count == 0 ? null : values[0]?.Trim();
        if (string.IsNullOrEmpty(term)) return Page();
        if (term.Length > MaxLength || TextRules.ContainsNul(term)) return Invalid($"검색어는 {MinLength}~{MaxLength}자여야 합니다.");
        Query = term;
        if (term.Length < MinLength) return Invalid($"검색어는 {MinLength}~{MaxLength}자여야 합니다.");
        if (!PageNumber.TryRead(Request.Query, MaxPage, out var page)) return NotFound();

        Posts = await PublicQueries.SearchAsync(db, term, page, ct);
        return page > 1 && Posts.Items.Count == 0 ? NotFound() : Page();
    }

    // 본문이 있는 400: StatusCodePages는 본문이 이미 쓰인 응답을 건드리지 않는다.
    private PageResult Invalid(string message)
    {
        Error = message;
        Response.StatusCode = StatusCodes.Status400BadRequest;
        return Page();
    }
}
```

`AccessMatrixTests.PublicAllowlist`에 `"search"` 추가.

- [ ] **Step 3: 통과 확인 + 회귀 + 커밋**

Run: `dotnet test PortfolioBlog.slnx -c Release --filter "FullyQualifiedName~SearchPageTests|FullyQualifiedName~AccessMatrixTests"` → 전부 통과.
**규칙 8 확인:** `PublicQueries.SearchAsync`에서 `LikePattern.Contains(term)`을 잠깐 `"%" + term + "%"`로 바꾸면 `Finds_…_WithLiteralWildcards`의 `100%` 단언이 실패한다. 되돌린다.
Run: `dotnet build PortfolioBlog.slnx -c Release`(경고 0) → `dotnet test PortfolioBlog.slnx -c Release`

```bash
git add -A
git commit -m "추가: 공개 검색 페이지(검색어 2~100자, 쪽 상한 50, 전용 속도 제한)

- 경계 밖 검색어는 400 안내, 쪽 번호 오류는 404, 너무 긴 값·NUL은 입력란에 되돌리지 않음
- LIKE 메타문자는 글자 그대로, 결과 쪽은 noindex
- 검색은 검색 한도(IP별 분당 + 전역 동시 실행)와 페이지 한도에 함께 계산

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: Atom 피드 · sitemap · robots

**Files:**
- Create: `PortfolioBlog.Api/Infrastructure/Web/XmlText.cs`
- Modify: `PortfolioBlog.Api/Pages/SiteEndpoints.cs`
- Test: `PortfolioBlog.Api.Tests/Infrastructure/XmlTextTests.cs`, `PortfolioBlog.Api.Tests/Features/FeedAndSitemapTests.cs`; Modify `Features/AccessMatrixTests.cs`(`PublicAllowlist`에 세 경로)

**Interfaces:**
- Consumes: `PublicQueries.FeedAsync/SitemapAsync`, `PublicUrls`, `PublicFormat.Rfc3339`, `SiteOptions.Title/Description/Author/PublicOrigin`, `RateLimitPolicy.PublicPage/PublicAsset`.
- Produces: `XmlText.Clean(string) : string`, `SiteEndpoints.FeedPattern = "/feed.xml"`, `SitemapPattern = "/sitemap.xml"`, `RobotsPattern = "/robots.txt"`.

**배경(스파이크 S9):** `XmlWriter`는 XML 1.0에 넣을 수 없는 문자(U+0001 같은 C0 제어 문자, U+FFFE, 짝 없는 서로게이트)를 만나면 `ArgumentException`을 던진다. 앱 검증은 제목에서 NUL만 거부하므로, 제어 문자가 든 제목 하나가 피드·sitemap을 **모든 방문자에게 영구 500**으로 만들 수 있다. 출력 직전에 그런 문자를 버린다(`CheckCharacters`는 켜 둔다 — 정리 함수가 틀려도 깨진 XML이 나가지는 않는다).

- [ ] **Step 1: 실패하는 테스트**

`PortfolioBlog.Api.Tests/Infrastructure/XmlTextTests.cs`:

```csharp
using PortfolioBlog.Api.Infrastructure.Web;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>XML 1.0에 넣을 수 없는 문자만 버린다. 제어 문자는 소스에 이스케이프로 쓰지 않고 (char) 캐스트로 만든다(선행 규칙 4의 연장).</summary>
public sealed class XmlTextTests
{
    [Fact]
    public void CleanInput_IsReturnedAsIs()
    {
        const string text = "탭\t줄바꿈\n이모지😀 <&\"'> ]]>";
        Assert.Same(text, XmlText.Clean(text));
    }

    [Fact]
    public void InvalidCharacters_AreDropped()
    {
        var high = ((char)0xD83D).ToString();
        var low = ((char)0xDE00).ToString();
        Assert.Equal("ab", XmlText.Clean("a" + (char)1 + "b"));
        Assert.Equal("ab", XmlText.Clean("a" + (char)0x1F + "b"));
        Assert.Equal("ab", XmlText.Clean("a" + (char)0xFFFE + "b"));
        Assert.Equal("ab", XmlText.Clean("a" + high + "b"));        // 짝 없는 상위 서로게이트
        Assert.Equal("ab", XmlText.Clean("a" + low + "b"));         // 짝 없는 하위 서로게이트
        Assert.Equal("ab", XmlText.Clean("ab" + high));             // 문자열 끝의 상위 서로게이트
        Assert.Equal("a" + high + low + "b", XmlText.Clean("a" + high + low + "b"));
    }

    /// <summary>정리한 결과는 XmlWriter가 예외 없이 받는다(정리 함수와 작성기의 판정이 어긋나면 실패).</summary>
    [Fact]
    public void CleanedText_IsAcceptedByXmlWriter()
    {
        var every = string.Concat(Enumerable.Range(1, 0xFFFF).Select(i => (char)i));
        var sb = new System.Text.StringBuilder();
        using (var xml = System.Xml.XmlWriter.Create(sb)) xml.WriteElementString("t", XmlText.Clean(every));
    }
}
```

`PortfolioBlog.Api.Tests/Features/FeedAndSitemapTests.cs`:

```csharp
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PortfolioBlog.Api.Infrastructure.Data;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>Atom·sitemap·robots: 항상 유효한 XML, 절대 URL은 PUBLIC_ORIGIN, 공개 호스트 전용.</summary>
[Collection("postgres")]
public sealed class FeedAndSitemapTests(PostgresContainerFixture pg)
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Sitemap = "http://www.sitemaps.org/schemas/sitemap/0.9";

    /// <summary>특수 문자·제어 문자가 든 제목이 있어도 피드는 유효한 XML이고, 항목 id는 글 Id 기반이며, 링크는 요청 헤더가 아니라 설정값을 쓴다.</summary>
    [Fact]
    public async Task Feed_IsValidAtom_EvenWithHostileTitles_AndUsesPublicOrigin()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?> { ["Site:Title"] = "블로그 <&>", ["Site:Author"] = "작성자" });
        var hostile = "제목 <&\"'> ]]> " + (char)1 + "끝";
        var post = await PublicSeed.PostAsync(factory, "hostile-title", hostile, summary: "요약 <b>");
        using var client = factory.CreatePublicClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "evil.test");

        using var res = await client.GetAsync("/feed.xml");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/atom+xml", res.Content.Headers.ContentType?.MediaType);
        var feed = XDocument.Parse(await res.Content.ReadAsStringAsync()).Root!;

        Assert.Equal(Atom + "feed", feed.Name);
        Assert.Equal("블로그 <&>", feed.Element(Atom + "title")?.Value);
        Assert.Equal(ApiFactory.PublicOrigin + "/", feed.Element(Atom + "id")?.Value);
        Assert.Equal("작성자", feed.Element(Atom + "author")?.Element(Atom + "name")?.Value);
        var entry = feed.Elements(Atom + "entry").Single();
        Assert.Equal("제목 <&\"'> ]]> 끝", entry.Element(Atom + "title")?.Value);
        Assert.Equal($"urn:uuid:{post.Id}", entry.Element(Atom + "id")?.Value);
        Assert.Equal("요약 <b>", entry.Element(Atom + "summary")?.Value);
        Assert.Matches(@"\A\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ\z", entry.Element(Atom + "published")!.Value);
        Assert.All(feed.Descendants(Atom + "link"), l => Assert.StartsWith(ApiFactory.PublicOrigin + "/", l.Attribute("href")!.Value, StringComparison.Ordinal));
        Assert.DoesNotContain("evil.test", feed.ToString(), StringComparison.Ordinal);
    }

    /// <summary>피드는 최신 20개뿐이고 글이 없어도 유효하다.</summary>
    [Fact]
    public async Task Feed_HasAtMostTwentyNewestEntries_AndIsValidWhenEmpty()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        using var client = factory.CreatePublicClient();
        var empty = XDocument.Parse(await client.GetStringAsync("/feed.xml")).Root!;
        Assert.Empty(empty.Elements(Atom + "entry"));
        Assert.NotNull(empty.Element(Atom + "updated"));

        var t0 = DbClock.UtcNow().AddDays(-1);
        for (var i = 0; i < 22; i++) await PublicSeed.PostAsync(factory, $"f-{i:00}", $"피드 {i}", createdAt: t0.AddMinutes(i));
        var feed = XDocument.Parse(await client.GetStringAsync("/feed.xml")).Root!;
        var titles = feed.Elements(Atom + "entry").Select(e => e.Element(Atom + "title")!.Value).ToArray();
        Assert.Equal(20, titles.Length);
        Assert.Equal("피드 21", titles[0]);
    }

    /// <summary>sitemap은 첫 쪽·글·태그·시리즈를 PUBLIC_ORIGIN 절대 URL로 싣고, 링크를 만들 수 없는 태그(".")와 글 없는 태그는 뺀다.</summary>
    [Fact]
    public async Task Sitemap_ListsEverything_WithEncodedAbsoluteUrls()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        var series = await PublicSeed.SeriesAsync(factory, "sm-series", "시리즈");
        await PublicSeed.PostAsync(factory, "sm-post", "글 <&>", tags: ["C#", "."], seriesId: series.Id, seriesOrder: 1);
        using var client = factory.CreatePublicClient();

        using var res = await client.GetAsync("/sitemap.xml");
        Assert.Equal("application/xml", res.Content.Headers.ContentType?.MediaType);
        var locs = XDocument.Parse(await res.Content.ReadAsStringAsync()).Root!.Elements(Sitemap + "url").Select(u => u.Element(Sitemap + "loc")!.Value).ToArray();

        Assert.Equal(new[]
        {
            ApiFactory.PublicOrigin + "/", ApiFactory.PublicOrigin + "/posts/sm-post",
            ApiFactory.PublicOrigin + "/tags/c%23", ApiFactory.PublicOrigin + "/series/sm-series",
        }, locs);
    }

    /// <summary>robots.txt는 sitemap 위치를 PUBLIC_ORIGIN으로 알린다. 세 경로 모두 관리 호스트에서는 404다.</summary>
    [Fact]
    public async Task Robots_PointsToTheSitemap_AndAllThreeArePublicHostOnly()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        using var client = factory.CreatePublicClient();
        using var robots = await client.GetAsync("/robots.txt");
        Assert.Equal("text/plain", robots.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"User-agent: *\nAllow: /\nSitemap: {ApiFactory.PublicOrigin}/sitemap.xml\n", await robots.Content.ReadAsStringAsync());

        using var admin = factory.CreateAdminClient();
        foreach (var path in new[] { "/feed.xml", "/sitemap.xml", "/robots.txt" })
        {
            using var res = await admin.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }
    }
}
```

Run: `dotnet build PortfolioBlog.slnx -c Release` → Expected: 컴파일 오류(`XmlText` 없음).

- [ ] **Step 2: 구현**

`Infrastructure/Web/XmlText.cs`:

```csharp
using System.Text;
using System.Xml;

namespace PortfolioBlog.Api.Infrastructure.Web;

/// <summary>XML 1.0 문서에 넣을 수 없는 문자를 버린다. 이스케이프(<c>&amp;lt;</c> 등)는 <see cref="XmlWriter"/>가 하므로 여기서 하지 않는다.</summary>
public static class XmlText
{
    /// <returns>전부 유효하면 <paramref name="value"/> 그대로(할당 없음). 아니면 유효한 문자만 남긴 새 문자열.</returns>
    public static string Clean(string value)
    {
        var firstBad = IndexOfInvalid(value, 0);
        if (firstBad < 0) return value;

        var sb = new StringBuilder(value.Length);
        var start = 0;
        for (var bad = firstBad; bad >= 0; bad = IndexOfInvalid(value, start))
        {
            sb.Append(value, start, bad - start);
            start = bad + 1;
        }
        return sb.Append(value, start, value.Length - start).ToString();
    }

    private static int IndexOfInvalid(string value, int from)
    {
        for (var i = from; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++; // 올바른 쌍은 통째로 유효하다
                continue;
            }
            // 여기까지 온 서로게이트는 짝이 없다. XmlConvert.IsXmlChar는 서로게이트 단독에 false를 돌려준다.
            if (!XmlConvert.IsXmlChar(c)) return i;
        }
        return -1;
    }
}
```

`Pages/SiteEndpoints.cs` — 상수와 매핑·핸들러 추가(`using System.Xml;`, `Microsoft.Extensions.Options`, `PortfolioBlog.Api.Infrastructure.Data`):

```csharp
    public const string FeedPattern = "/feed.xml";
    public const string SitemapPattern = "/sitemap.xml";
    public const string RobotsPattern = "/robots.txt";

    private const string AtomNamespace = "http://www.w3.org/2005/Atom";
    private const string SitemapNamespace = "http://www.sitemaps.org/schemas/sitemap/0.9";
    private static readonly string[] GetAndHead = ["GET", "HEAD"];

    // UTF8Encoding(false): BOM 없이. CheckCharacters 기본값(true)을 유지한다 — XmlText.Clean이 틀려도 깨진 XML 대신 예외가 난다.
    private static readonly XmlWriterSettings XmlSettings = new() { Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false) };
```

`MapPublicSiteEndpoints` 안(HighlightCss 매핑도 `GetAndHead`를 쓰도록 고친다):

```csharp
        app.MapMethods(FeedPattern, GetAndHead, FeedAsync).RequireHost(publicHost).AllowAnonymous()
            .WithMetadata(new RateLimitMetadata(RateLimitPolicy.PublicPage)).WithName("GetFeed");
        app.MapMethods(SitemapPattern, GetAndHead, SitemapAsync).RequireHost(publicHost).AllowAnonymous()
            .WithMetadata(new RateLimitMetadata(RateLimitPolicy.PublicPage)).WithName("GetSitemap");
        app.MapMethods(RobotsPattern, GetAndHead, static (IOptions<SiteOptions> site) =>
                TypedResults.Text($"User-agent: *\nAllow: /\nSitemap: {site.Value.PublicOrigin}{SitemapPattern}\n", "text/plain", Encoding.UTF8))
            .RequireHost(publicHost).AllowAnonymous()
            .WithMetadata(new RateLimitMetadata(RateLimitPolicy.PublicAsset)).WithName("GetRobots");
```

핸들러:

```csharp
    /// <summary>Atom 1.0 최신 20개. 항목 id는 글 Id 기반 URN이라 slug·도메인이 바뀌어도 구독기의 "읽음" 상태가 유지된다.</summary>
    private static async Task<IResult> FeedAsync(HttpContext http, PublicDbContext db, IOptions<SiteOptions> siteOptions, CancellationToken ct)
    {
        var site = siteOptions.Value;
        var entries = await PublicQueries.FeedAsync(db, ct);

        // MemoryStream: XmlWriter는 동기 Write/Flush를 쓴다. 응답 스트림에 직접 쓰면 Kestrel이 동기 I/O로 거부하므로 메모리에 만든 뒤 한 번에 보낸다(20개 항목, 수십 KB).
        using var buffer = new MemoryStream();
        using (var xml = XmlWriter.Create(buffer, XmlSettings))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("feed", AtomNamespace);
            xml.WriteElementString("title", AtomNamespace, XmlText.Clean(site.Title));
            if (!string.IsNullOrWhiteSpace(site.Description)) xml.WriteElementString("subtitle", AtomNamespace, XmlText.Clean(site.Description));
            xml.WriteElementString("id", AtomNamespace, site.PublicOrigin + "/");
            WriteLink(xml, "self", "application/atom+xml", site.PublicOrigin + FeedPattern);
            WriteLink(xml, "alternate", "text/html", site.PublicOrigin + "/");
            // 글이 없을 때도 필수 요소인 updated가 있어야 한다: 고정값(유닉스 기원)을 쓴다 — "지금"을 쓰면 구독기가 매번 바뀐 피드로 본다.
            xml.WriteElementString("updated", AtomNamespace, PublicFormat.Rfc3339(entries.Count == 0 ? DateTimeOffset.UnixEpoch : entries.Max(e => e.UpdatedAt)));
            xml.WriteStartElement("author", AtomNamespace);
            xml.WriteElementString("name", AtomNamespace, XmlText.Clean(string.IsNullOrWhiteSpace(site.Author) ? site.Title : site.Author));
            xml.WriteEndElement();
            foreach (var entry in entries)
            {
                xml.WriteStartElement("entry", AtomNamespace);
                xml.WriteElementString("id", AtomNamespace, $"urn:uuid:{entry.Id:D}");
                xml.WriteElementString("title", AtomNamespace, XmlText.Clean(entry.Title));
                WriteLink(xml, "alternate", "text/html", site.PublicOrigin + PublicUrls.Post(entry.Slug));
                xml.WriteElementString("published", AtomNamespace, PublicFormat.Rfc3339(entry.CreatedAt));
                xml.WriteElementString("updated", AtomNamespace, PublicFormat.Rfc3339(entry.UpdatedAt));
                if (entry.Summary.Length > 0)
                {
                    xml.WriteStartElement("summary", AtomNamespace);
                    xml.WriteAttributeString("type", "text");
                    xml.WriteString(XmlText.Clean(entry.Summary));
                    xml.WriteEndElement();
                }
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
        }
        http.Response.Headers.CacheControl = "public, max-age=300";
        return TypedResults.Bytes(buffer.ToArray(), "application/atom+xml; charset=utf-8");
    }

    private static async Task<IResult> SitemapAsync(HttpContext http, PublicDbContext db, IOptions<SiteOptions> siteOptions, CancellationToken ct)
    {
        var origin = siteOptions.Value.PublicOrigin;
        var map = await PublicQueries.SitemapAsync(db, ct);

        using var buffer = new MemoryStream();
        using (var xml = XmlWriter.Create(buffer, XmlSettings))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("urlset", SitemapNamespace);
            WriteUrl(xml, origin + "/", null);
            foreach (var (slug, updatedAt) in map.Posts) WriteUrl(xml, origin + PublicUrls.Post(slug), updatedAt);
            foreach (var key in map.TagKeys)
            {
                if (PublicUrls.Tag(key) is { } path) WriteUrl(xml, origin + path, null);
            }
            foreach (var slug in map.SeriesSlugs) WriteUrl(xml, origin + PublicUrls.Series(slug), null);
            xml.WriteEndElement();
        }
        http.Response.Headers.CacheControl = "public, max-age=300";
        return TypedResults.Bytes(buffer.ToArray(), "application/xml; charset=utf-8");
    }

    private static void WriteLink(XmlWriter xml, string rel, string type, string href)
    {
        xml.WriteStartElement("link", AtomNamespace);
        xml.WriteAttributeString("rel", rel);
        xml.WriteAttributeString("type", type);
        xml.WriteAttributeString("href", href);
        xml.WriteEndElement();
    }

    private static void WriteUrl(XmlWriter xml, string loc, DateTimeOffset? lastModified)
    {
        xml.WriteStartElement("url", SitemapNamespace);
        xml.WriteElementString("loc", SitemapNamespace, loc); // 경로는 slug([a-z0-9-])와 퍼센트 인코딩된 태그뿐이라 XML에 못 들어갈 문자가 없다
        if (lastModified is { } at) xml.WriteElementString("lastmod", SitemapNamespace, PublicFormat.Rfc3339(at));
        xml.WriteEndElement();
    }
```

`AccessMatrixTests.PublicAllowlist`에 `"/feed.xml"`, `"/sitemap.xml"`, `"/robots.txt"` 추가.

- [ ] **Step 3: 통과 확인 + 회귀 + 커밋**

Run: `dotnet test PortfolioBlog.slnx -c Release --filter "FullyQualifiedName~XmlTextTests|FullyQualifiedName~FeedAndSitemapTests|FullyQualifiedName~AccessMatrixTests"` → 전부 통과.
**규칙 8 확인:** `FeedAsync`의 제목에서 `XmlText.Clean`을 잠깐 빼면 `Feed_IsValidAtom_EvenWithHostileTitles…`가 **500**으로 실패한다(이 방어가 실제로 필요하다는 증거). 되돌린다.
Run: `dotnet build PortfolioBlog.slnx -c Release`(경고 0) → `dotnet test PortfolioBlog.slnx -c Release`

```bash
git add -A
git commit -m "추가: Atom 피드·sitemap·robots (XmlWriter, PUBLIC_ORIGIN 고정, 공개 호스트 전용)

- 제어 문자가 든 제목 하나가 피드를 영구 500으로 만들지 못하게 XML에 못 들어가는 문자를 출력 직전에 제거
- 항목 id는 글 Id 기반 URN, 절대 URL은 요청 Host·X-Forwarded-Host가 아니라 설정값
- sitemap은 링크를 만들 수 없는 태그와 글 없는 태그를 제외

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 8: 첨부 정합성 — 내용 단위 잠금 · 고아 파일 청소

**Files:**
- Create: `PortfolioBlog.Api/Infrastructure/Storage/AttachmentLock.cs`, `AttachmentJanitor.cs`
- Modify: `PortfolioBlog.Api/Infrastructure/Storage/AttachmentOptions.cs`, `FileSystemAttachmentStore.cs`, `PortfolioBlog.Api/Features/Attachments/AttachmentEndpoints.cs`, `PortfolioBlog.Api/Program.cs`, `PortfolioBlog.Api.Tests/Infrastructure/ApiFactory.cs`
- Test: `PortfolioBlog.Api.Tests/Features/AttachmentIntegrityTests.cs`, `PortfolioBlog.Api.Tests/Infrastructure/AttachmentJanitorTests.cs`

**Interfaces:**
- Consumes: `FileSystemAttachmentStore.SaveAsync/PhysicalPath/TryDelete`, `OverloadExceptionHandler`(55P03 → 503), `ApiFactory.ConnectionString`, `ApiFactory.AttachmentsRoot`.
- Produces:
  - `AttachmentLock.HoldAsync(AppDbContext db, string sha256, CancellationToken ct) : Task<IAsyncDisposable>`, `AttachmentLock.KeyFor(string sha256) : string`(internal)
  - `FileSystemAttachmentStore.Exists(string storagePath) : bool`, `EnumerateTempFiles() : IEnumerable<string>`, `EnumerateStoredFiles() : IEnumerable<(string StoragePath, string Sha256)>`
  - `AttachmentOptions.JanitorEnabled`(기본 `true`), `AttachmentJanitor.SweepOnceAsync(DateTimeOffset now, CancellationToken ct) : Task<SweepResult>`, `record SweepResult(int TempFilesDeleted, int OrphanFilesDeleted, int RowsMissingFiles)`, `AttachmentJanitor.MinimumAge = 1시간`, `Interval = 6시간`

**닫으려는 경쟁(2A 핸드오프 "미검증"):** 삭제는 "행 삭제 커밋 → 파일 삭제" 순서다. 그 사이에 같은 내용이 다시 업로드되면 — 업로드는 파일이 이미 있으므로 옮기지 않고 새 행만 넣는다 — 뒤이은 파일 삭제가 **방금 넣은 행의 파일**을 지운다(파일 없는 행). 같은 구조의 경쟁이 청소 잡과 업로드 사이에도 생긴다. 세 경로가 같은 sha256에 대해 **세션 advisory lock**(스파이크 S7)을 잡고, 업로드는 잠금 안에서 파일 존재를 다시 확인해 없으면 다시 놓는다(`IFormFile`은 다시 열 수 있다). 세션 잠금을 쓰는 이유: 트랜잭션 잠금은 커밋에 풀리므로 "커밋 → 파일 삭제"를 잠금 안에 넣을 수 없다.

- [ ] **Step 1: 실패하는 잠금 테스트**

`PortfolioBlog.Api.Tests/Features/AttachmentIntegrityTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Infrastructure.Data;
using PortfolioBlog.Api.Infrastructure.Storage;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>같은 내용의 삭제·업로드가 하나의 잠금으로 직렬화되는지 검증한다. 테스트가 그 잠금을 직접 쥐고 요청이 기다리는 것을 본다(경쟁을 확률에 맡기지 않는다).</summary>
[Collection("postgres")]
public sealed class AttachmentIntegrityTests(PostgresContainerFixture pg)
{
    private static readonly byte[] Png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images", "exif-text.png"));

    private static MultipartFormDataContent Form()
    {
        var part = new ByteArrayContent(Png);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new MultipartFormDataContent { { part, "file", "a.png" } };
    }

    private static async Task<AttachmentDto> UploadAsync(HttpClient client)
    {
        using var res = await client.PostAsync("/api/attachments", Form());
        Assert.Contains(res.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK });
        return (await res.Content.ReadFromJsonAsync<AttachmentDto>(TestJson.Options))!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, string key)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("k", key);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, string key)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("k", key);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>삭제와 같은 내용의 업로드는 그 내용의 잠금을 기다리고, 끝나면 잠금을 돌려준다. 잠금이 없는 구현에서는 두 요청이 즉시 끝나 실패한다.</summary>
    [Fact]
    public async Task DeleteAndUpload_OfTheSameContent_WaitForTheContentLock_AndReleaseIt()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        using var client = await factory.CreateLoggedInClientAsync();
        var uploaded = await UploadAsync(client);
        var key = AttachmentLock.KeyFor(uploaded.Sha256);

        await using var holder = new NpgsqlConnection(factory.ConnectionString);
        await holder.OpenAsync();
        await ExecuteAsync(holder, "SELECT pg_advisory_lock(hashtextextended(@k, 0))", key); // void를 돌려주므로 스칼라로 읽지 않는다

        var delete = client.DeleteAsync($"/api/attachments/{uploaded.Id}");
        var upload = client.PostAsync("/api/attachments", Form());
        var firstDone = await Task.WhenAny(delete, upload, Task.Delay(TimeSpan.FromMilliseconds(800)));
        Assert.True(firstDone != delete && firstDone != upload, "잠금을 쥐고 있는데 요청이 끝났다 — 잠금을 쓰지 않는다.");

        Assert.True(await ScalarAsync<bool>(holder, "SELECT pg_advisory_unlock(hashtextextended(@k, 0))", key));
        using var deleteRes = await delete;
        using var uploadRes = await upload;
        Assert.Equal(HttpStatusCode.NoContent, deleteRes.StatusCode);
        Assert.Contains(uploadRes.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK });

        // 두 요청이 잠금을 돌려줬다: 다른 세션이 바로 잡을 수 있다.
        Assert.True(await ScalarAsync<bool>(holder, "SELECT pg_try_advisory_lock(hashtextextended(@k, 0))", key));
        await AssertEveryRowHasItsFileAsync(factory);
    }

    /// <summary>삭제와 재업로드를 계속 교차시켜도 "파일 없는 행"이 생기지 않는다(불변식 검사 — 위 테스트가 메커니즘을, 이 테스트가 결과를 본다).</summary>
    [Fact]
    public async Task InterleavedDeleteAndReupload_NeverLeavesARowWithoutItsFile()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        using var client = await factory.CreateLoggedInClientAsync();
        for (var round = 0; round < 15; round++)
        {
            var current = await UploadAsync(client);
            var delete = client.DeleteAsync($"/api/attachments/{current.Id}");
            var upload = client.PostAsync("/api/attachments", Form());
            await Task.WhenAll(delete, upload);
            using var deleteRes = await delete;
            using var uploadRes = await upload;
            Assert.Equal(HttpStatusCode.NoContent, deleteRes.StatusCode);
            Assert.Contains(uploadRes.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK });
            await AssertEveryRowHasItsFileAsync(factory);
        }
    }

    private static async Task AssertEveryRowHasItsFileAsync(ApiFactory factory)
    {
        await using var scope = factory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<FileSystemAttachmentStore>();
        foreach (var path in await scope.ServiceProvider.GetRequiredService<AppDbContext>().Attachments.AsNoTracking().Select(a => a.StoragePath).ToListAsync())
        {
            Assert.True(File.Exists(store.PhysicalPath(path)), $"파일 없는 행: {path}");
        }
    }
}
```

Run: `dotnet build PortfolioBlog.slnx -c Release` → Expected: 컴파일 오류(`AttachmentLock` 없음). 구현 Step 2에서 `AttachmentLock`만 먼저 만들고(엔드포인트는 아직 쓰지 않는 상태) 첫 테스트가 **"요청이 끝났다"로 실패**하는 것을 확인한 뒤 Step 3으로 간다.

- [ ] **Step 2: 잠금·저장소 접근자**

`Infrastructure/Storage/AttachmentLock.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Infrastructure.Storage;

/// <summary>같은 내용(sha256)의 첨부를 건드리는 작업(업로드의 행 삽입, 삭제, 청소)을 PostgreSQL 세션 advisory lock으로 직렬화한다.</summary>
/// <remarks>
/// DB와 파일 시스템은 한 트랜잭션이 아니다. 잠금이 "행 상태 변경 + 파일 조작"을 한 덩어리로 만든다. 세션 잠금이라 트랜잭션 커밋 뒤의 파일 삭제까지 잠금 안에 둘 수 있다.
/// 잠금은 <see cref="IAsyncDisposable.DisposeAsync"/>에서 명시적으로 푼다. 프로세스가 죽거나 연결이 끊기면 세션 종료와 함께 서버가 푼다.
/// 대기 상한은 10초이며 넘으면 SqlState 55P03 — <c>OverloadExceptionHandler</c>가 503으로 바꾼다.
/// </remarks>
public static class AttachmentLock
{
    /// <summary>잠금 키 문자열. 다른 용도의 advisory lock과 겹치지 않게 접두사를 둔다. 테스트가 같은 키를 직접 잡는다.</summary>
    internal static string KeyFor(string sha256) => "attachment:" + sha256;

    public static async Task<IAsyncDisposable> HoldAsync(AppDbContext db, string sha256, CancellationToken ct)
    {
        var key = KeyFor(sha256);
        // 연결을 명시적으로 열어 둔다: 세션 잠금은 "잡은 연결"에 묶이므로, 잡은 뒤의 모든 EF 명령이 같은 연결을 써야 한다(열어 두지 않으면 EF가 명령마다 풀에서 새로 빌린다).
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync("SET lock_timeout = '10s'", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock(hashtextextended({key}, 0))", ct);
        }
        catch
        {
            await db.Database.CloseConnectionAsync();
            throw;
        }
        return new Releaser(db, key);
    }

    private sealed class Releaser(AppDbContext db, string key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                // 요청이 취소됐어도 잠금은 풀어야 하므로 취소 토큰을 넘기지 않는다.
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock(hashtextextended({key}, 0))", CancellationToken.None);
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }
}
```

`FileSystemAttachmentStore.cs` — 추가(각각 표준 주석):

```csharp
    /// <summary>저장 루트 기준 상대 경로의 파일이 지금 있는가.</summary>
    public bool Exists(string storagePath) => File.Exists(PhysicalPath(storagePath));

    /// <summary><c>.tmp</c> 밑의 임시 파일 전체 경로.</summary>
    public IEnumerable<string> EnumerateTempFiles() => Directory.Exists(_temp) ? Directory.EnumerateFiles(_temp) : [];

    /// <summary>내용 주소 규칙(<c>{sha[..2]}/{sha}.{확장자}</c>)에 **모양이 맞는** 파일만 열거한다. 규칙 밖의 것(임시 폴더, 시작 확인 파일, 사람이 둔 파일)은 청소 대상이 아니다(기본 거부).</summary>
    public IEnumerable<(string StoragePath, string Sha256)> EnumerateStoredFiles()
    {
        if (!Directory.Exists(_root)) yield break;
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            var bucket = Path.GetFileName(directory);
            if (bucket.Length != 2 || !IsLowerHex(bucket)) continue;
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(file);
                if (name.Length is < 66 or > 70 || name[64] != '.') continue;
                var sha = name[..64];
                var extension = name[65..];
                if (!IsLowerHex(sha) || !sha.StartsWith(bucket, StringComparison.Ordinal) || !extension.All(char.IsAsciiLetterOrDigit)) continue;
                yield return ($"{bucket}/{name}", sha);
            }
        }
    }

    private static bool IsLowerHex(string value) => value.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
```

- [ ] **Step 3: 업로드·삭제를 잠금 안으로**

`AttachmentEndpoints.UploadAsync` — `stored`를 얻은 뒤(기존 try/catch 그대로)부터 끝까지를 교체:

```csharp
        // 무거운 일(수신·메타데이터 제거·해시)은 잠금 밖에서 끝냈다. 잠금 안에서는 "파일 확인 + 행 조회/삽입"만 한다.
        await using (await AttachmentLock.HoldAsync(db, stored.Sha256, ct))
        {
            if (!store.Exists(stored.StoragePath))
            {
                // 잠금을 기다리는 사이 같은 내용의 삭제·청소가 파일을 지웠다. IFormFile은 프레임워크가 버퍼링해 둔 것이라 다시 열 수 있다.
                await using var again = file.OpenReadStream();
                stored = await store.SaveAsync(again, ct);
            }

            var existing = await db.Attachments.AsNoTracking().SingleOrDefaultAsync(a => a.Sha256 == stored.Sha256, ct);
            if (existing is not null) return TypedResults.Ok(ToDto(existing));

            var attachment = new Attachment
            {
                FileName = DisplayName(file.FileName, stored.Kind),
                ContentType = ImageSignature.ContentType(stored.Kind),
                SizeBytes = stored.SizeBytes, StoragePath = stored.StoragePath, Sha256 = stored.Sha256, CreatedAt = DbClock.UtcNow(),
            };
            db.Attachments.Add(attachment);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: DbConflict.UniqueViolation })
            {
                // 잠금 아래에서는 일어나지 않아야 한다. 잠금을 거치지 않는 경로(수동 SQL 등)에 대한 방어로 남긴다.
                db.ChangeTracker.Clear();
                return TypedResults.Ok(ToDto(await db.Attachments.AsNoTracking().SingleAsync(a => a.Sha256 == stored.Sha256, ct)));
            }
            loggers.CreateLogger("PortfolioBlog.Api.Audit").LogInformation(
                "첨부 업로드. AttachmentId={AttachmentId} Sha256={Sha256} SizeBytes={SizeBytes}", attachment.Id, attachment.Sha256, attachment.SizeBytes);
            var dto = ToDto(attachment);
            return TypedResults.Created(dto.Url, dto);
        }
```

`AttachmentEndpoints.DeleteAsync` 본문 교체:

```csharp
        var row = await db.Attachments.AsNoTracking().Where(a => a.Id == id).Select(a => new { a.Sha256, a.StoragePath }).SingleOrDefaultAsync(ct);
        if (row is null) return TypedResults.NotFound();

        var logger = loggers.CreateLogger("PortfolioBlog.Api.Audit");
        await using (await AttachmentLock.HoldAsync(db, row.Sha256, ct))
        {
            // ExecuteDeleteAsync: 자동 커밋되는 DELETE 한 문장. Sha256이 UNIQUE라 이 행이 그 파일의 유일한 참조다.
            if (await db.Attachments.Where(a => a.Id == id).ExecuteDeleteAsync(ct) == 0) return TypedResults.NotFound(); // 잠금을 기다리는 사이 다른 탭이 지웠다
            // 행을 먼저 지운다: 파일 삭제가 실패해도 남는 것은 참조 없는 파일뿐이고(청소 잡이 치운다), 반대 순서는 깨진 링크를 만든다.
            if (!store.TryDelete(row.StoragePath)) logger.LogWarning("첨부 파일 삭제 실패(고아 파일). AttachmentId={AttachmentId} Sha256={Sha256}", id, row.Sha256);
        }
        logger.LogInformation("첨부 삭제. AttachmentId={AttachmentId} Sha256={Sha256}", id, row.Sha256);
        return TypedResults.NoContent();
```

두 핸들러의 `<remarks>` Concurrency 항목을 새 동작으로 고친다(유니크 위반 경쟁·`DbUpdateConcurrencyException` 서술 삭제, 잠금·대기 상한 10초·503 추가). `using PortfolioBlog.Api.Infrastructure.Storage;`는 이미 있다.

Run: `dotnet test PortfolioBlog.slnx -c Release --filter "FullyQualifiedName~AttachmentIntegrityTests|FullyQualifiedName~AttachmentEndpointsTests"` → 전부 통과.

- [ ] **Step 4: 청소 잡 테스트(실패) → 구현**

`PortfolioBlog.Api.Tests/Infrastructure/AttachmentJanitorTests.cs`:

```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Infrastructure.Storage;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>청소 잡은 "오래됐고, 내용 주소 모양이고, 참조하는 행이 없는" 파일만 지운다. 백그라운드 실행은 끄고 <c>SweepOnceAsync</c>를 직접 부른다.</summary>
[Collection("postgres")]
public sealed class AttachmentJanitorTests(PostgresContainerFixture pg)
{
    private static async Task<AttachmentDto> UploadAsync(HttpClient client, string fixture)
    {
        var part = new ByteArrayContent(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images", fixture)));
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var res = await client.PostAsync("/api/attachments", new MultipartFormDataContent { { part, "file", fixture } });
        return (await res.Content.ReadFromJsonAsync<AttachmentDto>(TestJson.Options))!;
    }

    [Fact]
    public async Task Sweep_DeletesOnlyOldUnreferencedContentFiles_AndReportsMissingOnes()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        using var client = await factory.CreateLoggedInClientAsync();
        var store = factory.Services.GetRequiredService<FileSystemAttachmentStore>();
        var old = DateTime.UtcNow - AttachmentJanitor.MinimumAge - TimeSpan.FromMinutes(5);
        var root = factory.AttachmentsRoot;

        // 1) 참조되는 오래된 파일 → 남는다
        var kept = await UploadAsync(client, "exif-text.png");
        var keptPath = Directory.EnumerateFiles(root, kept.Sha256 + ".*", SearchOption.AllDirectories).Single();
        File.SetLastWriteTimeUtc(keptPath, old);
        // 2) 행은 있는데 파일이 없다 → 보고만 한다
        var broken = await UploadAsync(client, "exif-gps.jpg");
        File.Delete(Directory.EnumerateFiles(root, broken.Sha256 + ".*", SearchOption.AllDirectories).Single());
        // 3) 참조 없는 파일: 오래된 것은 지우고 새것은 둔다(진행 중인 업로드일 수 있다)
        string Orphan(char fill, DateTime? stamp)
        {
            var sha = new string(fill, 64);
            var path = Path.Combine(root, sha[..2], sha + ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [1, 2, 3]);
            if (stamp is { } at) File.SetLastWriteTimeUtc(path, at);
            return path;
        }
        var oldOrphan = Orphan('a', old);
        var freshOrphan = Orphan('b', null);
        // 4) 임시 파일: 오래된 것만
        Directory.CreateDirectory(Path.Combine(root, ".tmp"));
        var oldTemp = Path.Combine(root, ".tmp", "dead.upload");
        var freshTemp = Path.Combine(root, ".tmp", "live.upload");
        File.WriteAllBytes(oldTemp, [1]);
        File.WriteAllBytes(freshTemp, [1]);
        File.SetLastWriteTimeUtc(oldTemp, old);
        // 5) 모양이 규칙 밖인 오래된 파일 → 건드리지 않는다
        var foreign = Path.Combine(root, "aa", "README.txt");
        File.WriteAllText(foreign, "사람이 둔 파일");
        File.SetLastWriteTimeUtc(foreign, old);

        var result = await factory.Services.GetRequiredService<AttachmentJanitor>().SweepOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(new SweepResult(TempFilesDeleted: 1, OrphanFilesDeleted: 1, RowsMissingFiles: 1), result);
        Assert.True(File.Exists(keptPath));
        Assert.False(File.Exists(oldOrphan));
        Assert.True(File.Exists(freshOrphan));
        Assert.False(File.Exists(oldTemp));
        Assert.True(File.Exists(freshTemp));
        Assert.True(File.Exists(foreign));
        Assert.True(store.Exists(Path.GetRelativePath(root, keptPath).Replace('\\', '/'))); // Exists 접근자가 실제 경로 규칙과 맞는다
    }
}
```

`AttachmentOptions.cs`: `/// <summary>고아 파일 청소 잡을 돌릴지. 테스트는 끈다(파일 시각을 조작하는 테스트와 백그라운드 실행이 섞이지 않게).</summary> public bool JanitorEnabled { get; set; } = true;`
`ApiFactory.ConfigureWebHost`: `builder.UseSetting("Attachments:JanitorEnabled", "false");`

`Infrastructure/Storage/AttachmentJanitor.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Infrastructure.Storage;

/// <summary>청소 한 번의 결과.</summary>
/// <param name="TempFilesDeleted">지운 오래된 임시 파일 수.</param>
/// <param name="OrphanFilesDeleted">지운 참조 없는 첨부 파일 수.</param>
/// <param name="RowsMissingFiles">파일이 없는 행 수(고치지 못한다 — 같은 내용을 다시 올리면 복구된다).</param>
public sealed record SweepResult(int TempFilesDeleted, int OrphanFilesDeleted, int RowsMissingFiles);

/// <summary>시작 직후와 6시간마다: 죽은 업로드가 남긴 임시 파일과, 행 삽입이 실패했거나 파일 삭제가 실패해 남은 참조 없는 파일을 지운다.</summary>
public sealed class AttachmentJanitor(IServiceScopeFactory scopes, FileSystemAttachmentStore store, IOptions<AttachmentOptions> options,
    TimeProvider clock, ILogger<AttachmentJanitor> logger) : BackgroundService
{
    /// <summary>이보다 새 파일은 건드리지 않는다: 진행 중인 업로드의 임시 파일·방금 놓인 파일일 수 있다. 업로드 한 번은 이 시간 안에 끝난다(10MB).</summary>
    public static readonly TimeSpan MinimumAge = TimeSpan.FromHours(1);

    /// <summary>실행 간격.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.JanitorEnabled) return;
        await Task.Yield(); // 호스트 시작을 막지 않는다(첫 await 전까지는 StartAsync가 기다린다)
        // PeriodicTimer: 틱 사이에 스레드를 점유하지 않는 비동기 타이머. 실행이 길어져도 틱이 겹치지 않는다(다음 WaitForNextTickAsync에서야 다시 돈다).
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                var result = await SweepOnceAsync(clock.GetUtcNow(), stoppingToken);
                logger.LogInformation("첨부 청소. Temp={Temp} Orphans={Orphans} MissingFiles={Missing}", result.TempFilesDeleted, result.OrphanFilesDeleted, result.RowsMissingFiles);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "첨부 청소 실패. 다음 주기에 다시 시도한다.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<SweepResult> SweepOnceAsync(DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now.UtcDateTime - MinimumAge;
        var temp = 0;
        foreach (var file in store.EnumerateTempFiles())
        {
            if (File.GetLastWriteTimeUtc(file) < cutoff && TryDeleteFile(file)) temp++;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var orphans = 0;
        foreach (var (storagePath, sha) in store.EnumerateStoredFiles())
        {
            ct.ThrowIfCancellationRequested();
            if (File.GetLastWriteTimeUtc(store.PhysicalPath(storagePath)) >= cutoff) continue;
            // Sha256에는 UNIQUE 인덱스가 있다(StoragePath에는 없다) — 파일 수만큼 도는 조회라 인덱스를 타야 한다.
            if (await db.Attachments.AnyAsync(a => a.Sha256 == sha, ct)) continue;
            await using (await AttachmentLock.HoldAsync(db, sha, ct))
            {
                // 잠금 안에서 다시 확인: 방금 같은 내용의 업로드가 행을 넣었을 수 있다(그 업로드는 이 파일을 "이미 있음"으로 보고 옮기지 않았다).
                if (!await db.Attachments.AnyAsync(a => a.Sha256 == sha, ct) && store.TryDelete(storagePath)) orphans++;
            }
        }

        var missing = 0;
        await foreach (var path in db.Attachments.AsNoTracking().OrderBy(a => a.Id).Select(a => a.StoragePath).AsAsyncEnumerable().WithCancellation(ct))
        {
            if (!store.Exists(path)) missing++;
        }
        if (missing > 0) logger.LogWarning("파일이 없는 첨부 행 {Count}건. 같은 이미지를 다시 올리면 복구된다.", missing);
        return new SweepResult(temp, orphans, missing);
    }

    private bool TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("임시 파일 정리 실패. TempFile={TempFile}", Path.GetFileName(path));
            return false;
        }
    }
}
```

`Program.cs`(저장소 등록 아래):

```csharp
builder.Services.AddSingleton<AttachmentJanitor>();
builder.Services.AddHostedService(static sp => sp.GetRequiredService<AttachmentJanitor>());
```

Run: `dotnet test PortfolioBlog.slnx -c Release --filter "FullyQualifiedName~AttachmentJanitorTests"` → 통과.
**규칙 8 확인:** `EnumerateStoredFiles`의 모양 검사를 잠깐 없애면(모든 파일을 돌려주면) `README.txt`가 지워져 실패한다. `cutoff` 비교를 없애면 `freshOrphan`이 지워져 실패한다. 되돌린다.

- [ ] **Step 5: 전체 회귀 + 커밋**

Run: `dotnet build PortfolioBlog.slnx -c Release`(경고 0) → `dotnet test PortfolioBlog.slnx -c Release`

```bash
git add -A
git commit -m "버그수정: 같은 내용의 첨부 삭제·재업로드 교차로 파일 없는 행이 남던 경쟁을 닫고 고아 파일 청소 추가

- 업로드의 행 삽입·삭제·청소를 sha256 단위 세션 advisory lock으로 직렬화(대기 10초 초과는 503)
- 업로드는 잠금 안에서 파일 존재를 다시 확인하고 없으면 다시 놓음
- 청소 잡: 1시간 넘은 임시 파일과 참조 없는 내용 주소 파일만 삭제(모양이 규칙 밖인 파일은 건드리지 않음), 파일 없는 행은 경고

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 9: 앱 검증 ⊆ DB 제약 · CHECK 제약 커버리지 · 문서

**Files:**
- Test: `PortfolioBlog.Api.Tests/Features/ValidationWithinDbConstraintsTests.cs`, `PortfolioBlog.Api.Tests/Infrastructure/CheckConstraintCoverageTests.cs`
- Modify: `plan/tech_blog_0920.md`, `README.md`, `CLAUDE.md`, `AGENTS.md`, `PortfolioBlog.Api/PortfolioBlog.Api.http`
- (테스트가 결함을 드러내면) Modify: 해당 검증 코드 — 아래 Step 2 참조

**Interfaces:**
- Consumes: 관리 API(`POST /api/posts`, `POST /api/series`), `AppDbContext.Model`, `ApiBodyLimitMiddleware.JsonLimitBytes`.
- Produces: 없음(검증과 문서).

**목적:** 앱 검증을 통과한 입력이 DB 제약에 걸리면 400이 아니라 **500**이 된다(Plan 1 정오표의 slug 개행 사례). 길이 단위가 다른 곳(.NET `string.Length`는 UTF-16 단위, PostgreSQL `varchar(n)`은 문자, 본문은 바이트), 공백 판정이 다른 곳(.NET `IsNullOrWhiteSpace` vs `btrim`)의 **경계값**을 실제 API로 밀어 넣어 "통과 = 201, 거부 = 400, 어느 쪽도 500 아님"을 고정한다. 그리고 모델의 모든 CHECK 제약이 테스트로 덮여 있는지 닫힌 세계로 검사한다.

- [ ] **Step 1: 경계값 테스트 작성·실행**

`PortfolioBlog.Api.Tests/Features/ValidationWithinDbConstraintsTests.cs`:

```csharp
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>앱 검증이 허용하는 입력 집합이 DB 제약이 허용하는 집합 안에 있는지(⊆) 경계값으로 검증한다. 어떤 입력도 500이 되어서는 안 된다.</summary>
[Collection("postgres")]
public sealed class ValidationWithinDbConstraintsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    // 비 ASCII를 \uXXXX로 부풀리지 않는 인코더: .NET 기본 인코더는 한글 한 글자를 6바이트로 만들어 200KB 본문이 관리 JSON 상한(256KB)을 넘는다.
    // 브라우저의 JSON.stringify는 이스케이프하지 않는다 — 실제 클라이언트와 같은 바이트 수로 보낸다.
    private static readonly JsonSerializerOptions Relaxed = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static int _slug;
    private static string NextSlug() => $"bound-{Interlocked.Increment(ref _slug):000}";

    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body, Relaxed), Encoding.UTF8, "application/json");

    private static string Repeat(string unit, int count) => string.Concat(Enumerable.Repeat(unit, count));

    private static readonly string Emoji = char.ConvertFromUtf32(0x1F600); // UTF-16 2단위, PostgreSQL 1문자, UTF-8 4바이트

    public static TheoryData<string, object> Accepted() => new()
    {
        { "제목 200자(한글)", new { title = Repeat("가", 200) } },
        { "제목 이모지 100개(UTF-16 200단위)", new { title = Repeat(Emoji, 100) } },
        { "제목 앞뒤 공백", new { title = "   앞뒤 공백   " } },
        { "제목이 폭 없는 공백 하나(.NET 기준 공백 아님)", new { title = ((char)0x200B).ToString() } },
        { "요약 300자", new { title = "t", summary = Repeat("가", 300) } },
        { "본문 정확히 204,800바이트(한글 3바이트)", new { title = "t", contentMarkdown = Repeat("가", 68_266) + "aa" } },
        { "slug 100자", new { title = "t", slug = new string('a', 100) } },
        { "태그 50자 × 20개", new { title = "t", tagNames = Enumerable.Range(0, 20).Select(i => Repeat("가", 48) + i.ToString("00")).ToArray() } },
        { "태그 안의 전각 공백", new { title = "t", tagNames = new[] { "a" + (char)0x3000 + "b" } } },
    };

    public static TheoryData<string, object> Rejected() => new()
    {
        { "제목 201자", new { title = Repeat("가", 201) } },
        { "제목이 NEL 하나(.NET 기준 공백)", new { title = ((char)0x85).ToString() } },
        { "제목에 NUL", new { title = "a" + '\0' + "b" } },
        { "요약 301자", new { title = "t", summary = Repeat("가", 301) } },
        { "본문 204,801바이트", new { title = "t", contentMarkdown = Repeat("가", 68_266) + "aaa" } },
        { "slug 101자", new { title = "t", slug = new string('a', 101) } },
        { "slug 끝 개행", new { title = "t", slug = "abc\n" } },
        { "태그 51자", new { title = "t", tagNames = new[] { Repeat("가", 51) } } },
        { "태그 21개", new { title = "t", tagNames = Enumerable.Range(0, 21).Select(i => "tag" + i).ToArray() } },
    };

    private async Task<HttpStatusCode> PostAsync(object overrides)
    {
        using var client = await factory.CreateLoggedInClientAsync();
        var body = new Dictionary<string, object?> { ["slug"] = NextSlug(), ["title"] = "t", ["summary"] = "", ["contentMarkdown"] = "본문" };
        foreach (var property in overrides.GetType().GetProperties()) body[property.Name] = property.GetValue(overrides);
        using var res = await client.PostAsync("/api/posts", Json(body));
        return res.StatusCode;
    }

    [Theory]
    [MemberData(nameof(Accepted))]
    public async Task InputsTheAppAccepts_AreAcceptedByTheDatabase(string label, object overrides) =>
        Assert.True(HttpStatusCode.Created == await PostAsync(overrides), label);

    [Theory]
    [MemberData(nameof(Rejected))]
    public async Task InputsOutsideTheLimits_Are400_Never500(string label, object overrides) =>
        Assert.True(HttpStatusCode.BadRequest == await PostAsync(overrides), label);

    /// <summary>짝 없는 서로게이트는 JSON 바인딩이 거부한다(DB에 닿으면 Npgsql이 인코딩 예외로 500을 낸다 — 2A 정오표 #10). 원시 JSON으로 보낸다.</summary>
    [Fact]
    public async Task LoneSurrogate_InJson_Is400()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        var raw = "{\"slug\":\"" + NextSlug() + "\",\"title\":\"a\\uD800b\",\"summary\":\"\",\"contentMarkdown\":\"x\"}";
        using var res = await client.PostAsync("/api/posts", new StringContent(raw, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    /// <summary>시리즈도 같은 경계: 설명 1000자는 되고 1001자는 400.</summary>
    [Fact]
    public async Task Series_DescriptionBoundary()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        using var ok = await client.PostAsync("/api/series", Json(new { slug = NextSlug(), title = Repeat("가", 200), description = Repeat("가", 1000) }));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        using var tooLong = await client.PostAsync("/api/series", Json(new { slug = NextSlug(), title = "t", description = Repeat("가", 1001) }));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }
}
```

Run: `dotnet test PortfolioBlog.slnx -c Release --filter "FullyQualifiedName~ValidationWithinDbConstraintsTests"`

- [ ] **Step 2: 결과 처리**

이 테스트들은 "지금 코드가 이미 ⊆를 만족한다"는 **가설의 검증**이다(계획 작성 시 코드를 읽어 세운 가설: `string.Length` ≥ PostgreSQL 문자 수이므로 길이 검증은 항상 DB보다 엄격하거나 같다).
- 전부 통과하면 그대로 다음 Step으로 간다.
- **하나라도 500이면 그것이 이 Task의 본론이다.** 응답 본문·로그에서 SqlState와 제약 이름을 확인하고, 앱 검증 쪽을 DB 제약 이상으로 엄격하게 고친다(DB 제약을 느슨하게 하지 않는다). 필드 키가 있는 400이 되도록 `PostValidation`/`SeriesValidation`/`TagResolver.Validate`에 규칙을 추가하고, 그 입력을 `Rejected`로 옮긴다. 검증 규칙이 바뀌면 스펙 3.2 표도 고친다.
- 201을 기대한 입력이 **400**이면 가설(앱이 그 입력을 허용한다)이 틀린 것이다. 앱 검증이 더 엄격한 것은 ⊆를 깨지 않으므로 그 입력을 `Rejected`로 옮기고 라벨에 이유를 적는다. 코드를 바꾸지 않는다.

- [ ] **Step 3: CHECK 제약 닫힌 세계 테스트**

`PortfolioBlog.Api.Tests/Infrastructure/CheckConstraintCoverageTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PortfolioBlog.Api.Domain;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>모델에 선언된 모든 CHECK 제약이 실제로 위반을 거부하는지 검증한다. 제약을 추가하면서 여기에 위반 사례를 안 넣으면 실패한다(닫힌 세계).</summary>
[Collection("postgres")]
public sealed class CheckConstraintCoverageTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static Post NewPost(string slug) => new()
    {
        Slug = slug, Title = "제목", Summary = "", ContentMarkdown = "본문", CreatedAt = DbClock.UtcNow(), UpdatedAt = DbClock.UtcNow(),
    };

    private static Attachment NewAttachment(char fill) => new()
    {
        FileName = "a.png", ContentType = "image/png", SizeBytes = 10, StoragePath = $"{fill}{fill}/x.png",
        Sha256 = new string(fill, 64), CreatedAt = DbClock.UtcNow(),
    };

    /// <summary>제약 이름 → 그 제약 하나만 위반하는 행을 추가하는 동작.</summary>
    private static readonly Dictionary<string, Action<AppDbContext>> Violations = new(StringComparer.Ordinal)
    {
        ["CK_Posts_Slug_Format"] = db => db.Posts.Add(NewPost("Bad_Slug")),
        ["CK_Posts_Title_NotBlank"] = db => { var p = NewPost("ck-title"); p.Title = "   "; db.Posts.Add(p); },
        ["CK_Posts_Content_Size"] = db => { var p = NewPost("ck-size"); p.ContentMarkdown = new string('a', AppDbContext.ContentMaxBytes + 1); db.Posts.Add(p); },
        ["CK_Posts_Series_Pair"] = db => { var p = NewPost("ck-pair"); p.SeriesOrder = 1; db.Posts.Add(p); },
        ["CK_Posts_SeriesOrder_Positive"] = db =>
        {
            var s = new Series { Slug = "ck-series", Title = "시리즈", Description = "" };
            var p = NewPost("ck-order"); p.Series = s; p.SeriesOrder = 0;
            db.Posts.Add(p);
        },
        ["CK_Series_Slug_Format"] = db => db.Series.Add(new Series { Slug = "-bad", Title = "t", Description = "" }),
        ["CK_Series_Title_NotBlank"] = db => db.Series.Add(new Series { Slug = "ck-series-title", Title = " ", Description = "" }),
        ["CK_Tags_Name_NotBlank"] = db => db.Tags.Add(new Tag { Name = " ", NormalizedName = "ck-blank" }),
        ["CK_Tags_Name_NoSlash"] = db => db.Tags.Add(new Tag { Name = "a/b", NormalizedName = "ck-slash" }),
        ["CK_AdminState_Single"] = db => db.AdminStates.Add(new AdminState { Id = 2, SessionEpoch = 1 }),
        ["CK_Attachments_Size"] = db => { var a = NewAttachment('1'); a.SizeBytes = 0; db.Attachments.Add(a); },
        ["CK_Attachments_Sha256"] = db => { var a = NewAttachment('2'); a.Sha256 = new string('G', 64); db.Attachments.Add(a); },
        ["CK_Attachments_ContentType"] = db => { var a = NewAttachment('3'); a.ContentType = "image/svg+xml"; db.Attachments.Add(a); },
        ["CK_Attachments_FileName_NotBlank"] = db => { var a = NewAttachment('4'); a.FileName = " "; db.Attachments.Add(a); },
    };

    public static IEnumerable<object[]> ConstraintNames() => Violations.Keys.Select(static name => new object[] { name });

    [Fact]
    public void EveryDeclaredCheckConstraint_HasAViolationCase()
    {
        using var _ = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var declared = scope.ServiceProvider.GetRequiredService<AppDbContext>().Model.GetEntityTypes()
            .SelectMany(e => e.GetCheckConstraints()).Select(c => c.Name!).Distinct().Order(StringComparer.Ordinal);
        Assert.Equal(declared, Violations.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(ConstraintNames))]
    public async Task Violation_IsRejected_ByExactlyThatConstraint(string constraint)
    {
        using var _ = factory.CreateClient();
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Violations[constraint](db);
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal("23514", pg.SqlState);
        Assert.Equal(constraint, pg.ConstraintName);
    }
}
```

Run: `dotnet test PortfolioBlog.slnx -c Release --filter "FullyQualifiedName~CheckConstraintCoverageTests"` → 전부 통과. `ConstraintName` 단언이 핵심이다: "다른 제약에 먼저 걸려서 통과"하는 가짜 통과를 막는다. 실패하면 위반 사례가 **그 제약 하나만** 어기도록 고친다.

- [ ] **Step 4: 문서**

`plan/tech_blog_0920.md`:
- 3.3 미들웨어 순서 문장을 이 계획의 실제 순서로 교체: `호스트 필터(설정된 두 호스트) → 보안 헤더 → ForwardedHeaders → 예외 처리 → 상태 코드 본문 → 정적 파일 → AdminSurfaceMiddleware → 속도 제한 → 쿠키 인증 → 인가 → 관리 JSON 본문 상한 → 엔드포인트`.
- 3.4 공개 표: `/`에 "잘못된 `page`(숫자 아님·0·상한 초과·결과 없는 쪽)는 404", `/search`에 "경계 밖 `q`는 400(안내문), 결과 쪽은 `noindex`", 표 아래에 "공개 페이지·피드·sitemap·robots·`/css/highlight.css`는 공개 호스트에만 매칭된다(관리 호스트에서는 404). `/attachments`·`/health`만 양쪽" 추가. OG 이미지 문장을 "렌더러의 URL 정책을 통과한 본문 첫 이미지"로.
- 3.6 표: "관리 API" 행의 CSP를 공개와 같은 값으로, 모든 행 공통으로 `X-Frame-Options: DENY`, HSTS는 Development 제외, `Server` 헤더 없음. 표 아래에 "헤더는 전송 직전(`OnStarting`)에 붙는다 — 라우트 제약 실패 404와 예외 500에도 실린다. 호스트 필터의 400(프레임워크 고정 본문)만 예외다. `Cross-Origin-Resource-Policy`는 붙이지 않는다(미리보기 iframe의 이미지가 관리 오리진에서 읽힌다)" 추가.
- 3.7 표: "(Plan 2B 예정)" 표기를 전부 지우고 행 추가 — `첨부 GET·/health·robots·highlight.css | IP별 600회/분`, `업로드 | 전역 30회/분 + 동시 2`, `렌더링 | 프로세스 전역 동시 2, 슬롯 대기 5초 초과 시 503. 공개 글은 (PostId, xmin) 메모리 캐시(64MB, 정상 24시간·시간 예산 초과 렌더 2분) + 단일 비행`, `과부하 응답 | statement_timeout·잠금 대기·렌더 슬롯 대기 초과는 503 + Retry-After: 5`. JSON 본문 행에 "직렬화 후 바이트 기준. 이스케이프가 많은 본문은 200KB 미만에서도 413이 될 수 있다" 추가. 속도 제한 문단에 "동시 실행 제한기는 고정 창보다 앞이다: 동시 실행 거부가 분당 허용량을 쓰지 않고 `Retry-After`는 5초다" 추가. DB 행을 "공개 조회는 별도 연결(`statement_timeout` 3초 + `default_transaction_read_only=on`)"로.
- 3.8: "고아 파일 정리는 확장 포인트로 둔다"를 "청소 잡이 1시간 넘은 임시 파일과 참조 없는 내용 주소 파일을 6시간마다 지운다. 업로드의 행 삽입·삭제·청소는 sha256 단위 세션 advisory lock으로 직렬화한다"로 교체.
- 7절: "렌더 캐시", "첨부 고아 파일 정리 잡" 항목 삭제. 추가: "표 정렬(지금은 sanitizer가 `style`을 지운다 — 허용 클래스로 바꾸는 렌더러 수정 필요)", "렌더 캐시의 다중 인스턴스 공유(지금은 프로세스 메모리)".
- 8절 표: Plan 2B 행을 `docs/superpowers/plans/2026-09-21-tech-blog-public-site.md`로.

`README.md`: "로드맵"의 2B를 완료로, "시작하기"에 공개 페이지 확인 주소(`https://localhost:7198/`)와 새 설정 표(`Site:Title/Description/Author`, `Public:*`, `Rendering:*`, `Admin:Upload*`, `Attachments:JanitorEnabled` — 기본값·의미), "EF 마이그레이션은 `dotnet ef migrations add <이름> --project PortfolioBlog.Api --context AppDbContext`" 한 줄, "저장소 구조"에 `Pages/`·`wwwroot/`.
`CLAUDE.md`·`AGENTS.md`(함께 고친다): "구성" 절의 `PortfolioBlog.Api` 설명 끝에 "공개 페이지는 `Pages/`(GET/HEAD·공개 호스트 전용 규약), 정적 파일은 `wwwroot/css/site.css` 하나" 추가.
`PortfolioBlog.Api.http`: 공개 표면 요청 예시 추가(`GET {{host}}/`, `/posts/{slug}`, `/tags/C%23`, `/search?q=`, `/feed.xml`, `/sitemap.xml`, `/robots.txt`).

Run: `pwsh scripts/harness-audit.ps1` → PASS 8/8(`CLAUDE.md`·`AGENTS.md` 미러 동기화 검사 포함).

- [ ] **Step 5: 전체 회귀 + 커밋**

Run: `dotnet build PortfolioBlog.slnx -c Release`(경고 0) → `dotnet test PortfolioBlog.slnx -c Release`

```bash
git add -A
git commit -m "테스트: 앱 검증이 DB 제약 안에 있음을 경계값으로 고정하고 모든 CHECK 제약을 닫힌 세계로 검사

- 길이 단위(UTF-16·문자·바이트)와 공백 판정이 갈리는 경계에서 통과는 201, 거부는 400, 500 없음
- 모델에 선언된 CHECK 제약 14개가 각각 자기 이름으로 위반을 거부하는지 확인
- 스펙 3.3·3.4·3.6·3.7·3.8과 README·CLAUDE.md·AGENTS.md를 2B 구현에 맞춰 갱신

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## 실행 순서와 최종 리뷰

- 순서는 **1 → 2 → 3 → 4 → 5 → 6 → 7 → 8 → 9** 고정이다. 의존: 2→1(`RateLimitMetadata` 정책), 3→2(`OverloadExceptionHandler`), 4→1(`PublicOptions`), 5→2·3·4, 6·7→5, 8→2(55P03→503)·4(`ApiFactory.ConnectionString`), 9→2(256KB). 8은 5~7과 파일이 겹치지 않지만 `Program.cs`·`ApiFactory.cs`를 함께 고치므로 병렬로 돌리지 않는다.
- 브랜치: `feature/blog-public-site`. 컨트롤러는 실행 중 `.git/harness_commit_in_progress` 센티널을 유지하고(작업마다 `touch`), 매 커밋마다 Release 빌드 경고 0·전체 테스트·커밋 트레일러·0x00 바이트·새 파일의 줄 끝(`git ls-files --eol`)을 **직접** 확인한다(`plan/resume_guide_0921.md` 4절 — 구현자 보고는 검증 대상이다).
- 작업별 리뷰어에게: "계획의 코드 자체가 틀렸을 수 있다 — 직접 공격하고 측정하라." 특히 보라고 지시할 것:
  - Task 1: 체인에서 한 요청이 두 창(검색+페이지)에 걸릴 때의 소모 순서, 파티션 키에 들어가는 IP가 ForwardedHeaders 뒤의 값인지.
  - Task 2: 헤더가 **모든** 종료 경로(호스트 필터 400 제외)에 실리는지, 413 경로가 접근 계약(401/403/404)을 앞지르지 않는지, `LengthLimitedStream`이 업로드에 걸리지 않는지.
  - Task 3: 게이트 슬롯 누수(예외·취소 경로), 단일 비행의 실패 전파, `MemoryCache` 크기 계산이 실제 메모리와 어긋나는 정도(측정), 저장 경로가 선채움한 버전이 실제 `xmin`과 같은지.
  - Task 4: 공개 컨텍스트로 쓸 수 있는 길이 남아 있는지(`ExecuteSqlRaw`, 저장 프로시저, `SET default_transaction_read_only = off` 가능 여부 — **세션에서 끌 수 있다면 방어 깊이의 한계로 문서화**), 두 풀의 연결 수.
  - Task 5: 공격 코퍼스를 **페이지를 통해** 다시 돌리기(제목·요약·태그·시리즈 설명·검색어·경로 값 각각에 `<script>`·따옴표·`]]>`·제어 문자), CSP 위반이 없는지(인라인 스타일·외부 자원 0개), HEAD 응답, 아주 긴 경로 값.
  - Task 7: XML 주입(제목·요약·사이트 제목), 빈 DB, `XmlText.Clean`과 `XmlWriter` 판정의 불일치.
  - Task 8: 잠금이 풀리지 않는 경로(예외·취소·연결 끊김), 청소 잡이 진행 중인 업로드의 파일을 지울 수 있는 창, 디렉터리 링크·이상한 파일 이름.
- 최종 리뷰(브랜치 전체): 2A처럼 **실제 Production 호스트**(Kestrel + PostgreSQL)를 띄워 HTTP로 찌른다. 확인 목록: 모든 공개 경로의 응답 헤더(`Server` 없음 포함), 관리 호스트에서 공개 경로 404, 목록 밖 Host 400, 300KB JSON 413(본문을 다 읽기 전), 적대적 200KB 글 하나를 저장한 뒤 그 글을 동시 50회 요청했을 때 렌더 1회·나머지는 대기 후 같은 결과, 검색 남용 시 429. **스파이크 S12 주의:** 이 PC에서 평문 HTTP로 받은 HTML에는 AdGuard가 `<script>`를 주입한다 — 서버 쪽 바이트로 확인하거나 AdGuard를 끄고 본다.
- 끝나면: PR → CI(ubuntu-latest, 첫 Linux 실행을 게이트로 취급) → squash 병합 → 보고서 `plan/tech_blog_2b_report_<MMDD>.md`("내린 판정" 표 포함) → `plan/resume_guide_0921.md`·`CLAUDE.md`/`AGENTS.md` 플랜 표 갱신.

## 이 계획이 다루지 않는 것

| 항목 | 어디서 |
|---|---|
| 관리 에디터 SPA. 미리보기 iframe 이미지의 `img-src` 실제 브라우저 확인. 클라이언트는 `JSON.stringify`(비 ASCII 비이스케이프)로 보내야 256KB 안에 200KB 한글 본문이 들어간다(D6) | Plan 3 |
| 컨테이너 헬스체크에 `Host: <공개 호스트>` 헤더(D5 — 없으면 400). Caddy `request_body` 11MB. 첨부 삭제가 캐시 사본을 회수하지 못한다는 운영 메모. 저장 볼륨은 앱 시작 전에 마운트. multipart 버퍼링이 임시 폴더에 쓴다. Release 출력의 EF 디자인 타임 어셈블리 제거. `DataProtection:KeysPath` 필수화 | Plan 4 |
| 표 정렬(D11), 렌더 캐시의 인스턴스 간 공유, 전문 검색(`tsvector`/`pg_trgm`), 마이그레이션 전용 DB 역할 | 스펙 7절 |
| 공개 페이지의 예외 500 본문은 공개 경로에서도 고정 HTML이 아니라 프레임워크 ProblemDetails다(`UseExceptionHandler` 기본). 과부하(503)만 경로별 본문을 쓴다 | 필요해지면 `IExceptionHandler` 하나 더 |
| 로컬 간헐 테스트 실패(`Migrate()` 15초 연결 타임아웃, Windows Docker Desktop). 이 계획은 팩토리당 풀이 둘이 되므로 **악화 여부를 실행 중에 관찰**하고, 악화되면 공개 풀에 `Maximum Pool Size`를 걸거나 테스트 클래스 간 DB 공유를 늘린다 | 관찰 후 판단 |

## Self-Review 결과

**스펙 대비 점검(2B 범위)**

| 스펙 | 구현 Task |
|---|---|
| 3.1 공개 표면: GET·HEAD만, JS 없음, 상태 변경 엔드포인트 없음 | Task 5(`PublicPageConvention` + `AccessMatrixTests` 닫힌 세계 2종) |
| 3.4 `/`(20개, 상한 500)·`/posts/{slug}`(본문·태그·시리즈 이전/다음·title·description·OG·canonical)·`/tags/{tag}`(정규화명, URL 인코딩)·`/series/{slug}` | Task 4(조회), Task 5 |
| 3.4 `/search?q=`(2~100자, `%_\` 이스케이프, 매개변수화) | Task 4(`SearchAsync`), Task 6 |
| 3.4 `/feed.xml`(Atom, 최신 20, Id 기반 URN, published/updated, Summary text)·`/sitemap.xml`·`/robots.txt`, 절대 URL은 `PUBLIC_ORIGIN`, OG 이미지 | Task 7, Task 5(OG), Task 3(`FirstImageUrl`) |
| 3.5 글 상세 요청 흐름(속도 제한 → AsNoTracking 조회 → Render → 헤더) | Task 1·4·5·2 |
| 3.6 공개 HTML·관리 API 헤더, 2.6 Codex ⑯(`XmlWriter`·`AllowedHosts`) | Task 2, Task 7 |
| 3.7 공개 120/분, 검색 20/분·동시 4·`q`·`page` 50, DB `statement_timeout` 3초, JSON 256KB | Task 1, Task 6, Task 4, Task 2 |
| 3.8 고아 파일 정리(확장 포인트였던 것) | Task 8 |
| 6절 필수 테스트 "자원"(검색어 경계·LIKE 와일드카드·`page` 상한), "출력"(Atom·sitemap 유효 XML, 위조 Host가 아닌 `PUBLIC_ORIGIN`, CSP·nosniff) | Task 6, Task 7, Task 2 |
| 2A 핸드오프: 렌더 캐시/게이트(저장 경로 포함)·워밍업 / id 선택자 금지 / JSON 상한·업로드 속도 제한·`RequestSizeLimit` 확인 / 체인 순서·Retry-After / 고아 정리·삭제-업로드 교차 / 쿠키 실은 수제 요청의 상한 / `AllowedHosts`·`Server` 헤더 / 제약 실패 404의 헤더 / 시간 의존 테스트 / 앱 검증 ⊆ DB 제약·CHECK 커버리지 / 공개 프로젝션에 `Version` 미노출 | Task 3 / 5 / 2·1 / 1 / 8 / 1(`PublicAsset`) / 2 / 2 / 3 / 9 / 4 |

**스펙과 다르게 정한 것(Task 9가 스펙에 반영한다):** 미들웨어 순서에 호스트 필터·정적 파일·본문 상한이 들어감, 관리 API에도 CSP, `PublicAsset`·`Upload` 한도 신설, 렌더 캐시를 확장 포인트에서 본 구현으로, 고아 파일 청소를 확장 포인트에서 본 구현으로. 위 "설계 결정" 표 D1~D12 참조.

**자리표시자:** "TBD/TODO" 없음. Task 9 Step 2는 "결과에 따라 분기"지만 분기마다 할 일과 기준이 적혀 있다(가설 검증 Task의 본질). Task 9 Step 4의 문서 수정은 바꿀 문장을 항목별로 적었다.

**타입 일관성:** `RateLimitPolicy.{PublicPage,PublicAsset,Search,Upload}`, `PublicOptions.{PagePerIpPerMinute,AssetPerIpPerMinute,SearchPerIpPerMinute,SearchConcurrency,StatementTimeoutMs}`, `RateLimitingExtensions.{BuildChain,RetryAfterSeconds,ConcurrencyRetryAfterSeconds}`, `SecurityHeadersMiddleware.{PublicCsp,PermissionsPolicy}`, `ErrorResponses.{WriteAsync,HandleStatusCodeAsync}`, `OverloadExceptionHandler.{IsOverload,RetryAfterSeconds}`, `ApiBodyLimitMiddleware.JsonLimitBytes`, `RenderedMarkdown(Html, FirstImageUrl, HighlightTimedOut)`, `MarkdownRenderer.RenderDetailed`, `RenderGate.{RenderAsync,RenderCount}`, `RenderBusyException`, `RenderedPostCache.{TryGet,GetOrRenderAsync,Store}`, `PublicDbContext.BuildConnectionString`, `AddBlogData`, `PublicQueries.{LatestAsync,ByTagAsync,SearchAsync,GetPostAsync,GetContentAsync,GetSeriesAsync,FeedAsync,SitemapAsync,PageSize,SeriesMax,FeedSize,SitemapMax}`, `PublicUrls.{Post,Series,Tag}`, `PublicFormat.{Rfc3339,DisplayDate}`, `PageNumber.TryRead`, `PagerModel.Href`, `PublicPageModel.{SetHead,HeadKey,Site}`, `IndexModel.MaxPage`, `SearchModel.{MinLength,MaxLength,MaxPage}`, `SiteEndpoints.{MapPublicSiteEndpoints,HighlightCssPattern,FeedPattern,SitemapPattern,RobotsPattern}`, `HighlightCss.Value`, `XmlText.Clean`, `AttachmentLock.{HoldAsync,KeyFor}`, `FileSystemAttachmentStore.{Exists,EnumerateTempFiles,EnumerateStoredFiles}`, `AttachmentJanitor.{SweepOnceAsync,MinimumAge,Interval}`, `SweepResult`, 테스트 도구 `PublicSeed.{PostAsync,SeriesAsync}`·`HtmlDoc.{Parse,GetAsync}`·`SteppingTimeProvider`·`ApiFactory.ConnectionString` — 정의한 Task와 쓰는 Task에서 이름·시그니처가 같다.

**이 계획 자체의 알려진 불확실성(실행 중 가장 먼저 의심할 곳 — 스파이크로 확인하지 못했다):**
1. 호스트 필터가 `WebApplicationFactory`(TestServer)에서도 도는가 — Kestrel에서만 실측했다(S3). 안 돌면 `HostFilteringTests`가 200으로 실패한다 → 실제 호스트에서 확인하는 테스트로 바꾸지 말고 원인(시작 필터 등록)을 먼저 본다.
2. `TransferEncodingChunked` 요청이 TestServer에서 `ContentLength == null`로 도착하는가(S4는 Kestrel 실측).
3. `AttachmentLock`의 연결 고정: `OpenConnectionAsync` 뒤의 `ExecuteDeleteAsync`·`SaveChangesAsync`가 같은 물리 연결을 쓰는가(EF 문서상 그렇다). `pg_try_advisory_lock` 단언이 검증한다.
4. `PublicDbContext`가 `AppDbContext`의 모델 캐시·마이그레이션 어셈블리와 간섭하지 않는가(`Migrate()`는 `AppDbContext`로만 부른다). 시작 시 EF 경고가 새로 나오면 멈추고 보고한다.
5. Razor 생성 코드의 빌드 경고(nullable), 테스트 호스트에서 `wwwroot` 해석(콘텐츠 루트).
6. Task 9 Step 1의 "Accepted" 가설 각 항목.

