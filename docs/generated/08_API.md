# API

<!-- doc-harness:section id="summary" hash="c047737076f8655e2f9ba00ca6add5f7d6044bb578a0a81d08a337468bf9b613" -->
## 한 줄 요약

HTTP 엔드포인트 32개, 호스트 admin / both / public. 인증·부작용·오류를 표로 정리했다.
<!-- /doc-harness:section -->

<!-- doc-harness:section id="table" hash="8ff16d47e36defc6edb33986d67b6b4285a88d04f8c4fa19cf0c3d3ce154eba6" -->
## 엔드포인트

| Method | Path | Host | 호출자 | 인증 | 요청 | 응답 | 오류 | 기능 | 코드 |
|---|---|---|---|---|---|---|---|---|---|
| DELETE | `/api/attachments/{id:guid}` | admin | 관리 SPA 첨부 화면 | 세션 필수 | 경로 id | 204 | 404 없음/이미 삭제; 503 + Retry-After: 잠금 대기 초과; 401; 404·403: AdminSurfaceMiddleware(비관리 호스트 404, IP·X-Requested-With·Origin 위반 403) | F009, F018 | `PortfolioBlog.Api/Features/Attachments/AttachmentEndpoints.cs` AttachmentEndpoints.DeleteAsync |
| GET | `/api/attachments` | admin | 관리 SPA 첨부 화면 | 세션 필수 | 쿼리 skip(≥0), take(1~200, 기본 50) | 200 PagedAttachmentsDto(최신순+total) | 400; 401; 404·403: AdminSurfaceMiddleware(비관리 호스트 404, IP·X-Requested-With 위반 403) | F009, F018 | `PortfolioBlog.Api/Features/Attachments/AttachmentEndpoints.cs` AttachmentEndpoints.ListAsync |
| POST | `/api/attachments` | admin | 관리 SPA 첨부 화면·에디터 | 세션 필수. RateLimitPolicy.Upload(upload-concurrency, upload-global 창) | multipart 필드 file. RequestSizeLimit = MaxBytes+1MB. antiforgery는 이 엔드포인트만 비활성(CSRF는 미들웨어가 방어). | 201 Location + AttachmentDto(신규) / 200 기존 첨부(동일 sha256) | 400; 413; 415; 429 + Retry-After; 503 + Retry-After: 잠금 대기 10초 초과(55P03); 401; 404·403: AdminSurfaceMiddleware(비관리 호스트 404, IP·X-Requested-With·Origin 위반 403) | F009, F018, F019 | `PortfolioBlog.Api/Features/Attachments/AttachmentEndpoints.cs` AttachmentEndpoints.UploadAsync |
| POST | `/api/auth/login` | admin | 관리 SPA 로그인 화면 | AllowAnonymous. RateLimitPolicy.Login(login-concurrency, login-ip, login-global 창) | JSON LoginRequest{password}. 헤더 X-Requested-With, Origin=관리 origin 필요. | 204 + __Host- 세션 쿠키(비영속). 실패 401 ProblemDetails. | 400: password 누락/256자 초과; 401: 비밀번호 불일치; 429 + Retry-After: 로그인 속도·동시성 한도 초과; 404: 관리 호스트가 아님; 403: 허용 IP 밖 / X-Requested-With 누락 / Origin이 관리 origin과 다름(POST); 위 404·403은 AdminSurfaceMiddleware의 ProblemDetails, no-store | F001, F018, F019 | `PortfolioBlog.Api/Features/Auth/AuthEndpoints.cs` AuthEndpoints.LoginAsync |
| POST | `/api/auth/logout` | admin | 관리 SPA | AdminCookie 세션 필수 | 본문 없음. X-Requested-With, Origin 필요. | 204 | 401: 세션 없음(인가 정책); 404: 관리 호스트가 아님; 403: 허용 IP 밖 / X-Requested-With 누락 / Origin 불일치; 404·403은 AdminSurfaceMiddleware의 ProblemDetails, no-store | F001, F018 | `PortfolioBlog.Api/Features/Auth/AuthEndpoints.cs` AuthEndpoints.LogoutAsync |
| GET | `/api/auth/me` | admin | 관리 SPA(세션 확인) | AllowAnonymous. 핸들러가 AdminCookie 스킴을 직접 AuthenticateAsync로 평가 | 본문 없음. 헤더 X-Requested-With: XMLHttpRequest 필요. | 200 AuthStatusDto(Succeeded 여부). 세션이 없어도 200이며 값만 false. | 404: 관리 호스트가 아님(AdminSurfaceMiddleware); 403: 허용 IP 밖 / X-Requested-With 누락(AdminSurfaceMiddleware); GET이므로 Origin 검사는 하지 않음; 오류 본문은 ProblemDetails, Cache-Control: no-store | F001, F018 | `PortfolioBlog.Api/Features/Auth/AuthEndpoints.cs` AuthEndpoints.MapAuthEndpoints (GET /me 람다) |
| DELETE | `/api/posts/{id:guid}` | admin | 관리 SPA 글 목록 | 세션 필수 | 경로 id, 필수 쿼리 version(uint) | 204 | 400 version 누락; 404 글 없음; 409 version 불일치; 401; 404·403: AdminSurfaceMiddleware(비관리 호스트 404, IP·X-Requested-With·Origin 위반 403) | F002, F018 | `PortfolioBlog.Api/Features/Posts/PostEndpoints.cs` PostEndpoints.DeleteAsync |
| GET | `/api/posts/{id:guid}` | admin | 관리 SPA 에디터 | 세션 필수 | 경로 id(guid) | 200 PostDetailDto(Version 포함) / 404 | 404: 글 없음 / 관리 호스트가 아님(미들웨어); 401 세션 없음; 403: 허용 IP 밖 / X-Requested-With 누락(미들웨어) | F003, F006, F018 | `PortfolioBlog.Api/Features/Posts/PostEndpoints.cs` PostEndpoints.GetAsync |
| PUT | `/api/posts/{id:guid}` | admin | 관리 SPA 에디터(충돌 해결 포함) | 세션 필수 | JSON UpsertPostRequest, Version 필수(xmin). slug 변경 불가. | 200 갱신된 PostDetailDto | 400; 404 글 없음; 409 오래된 version(DbUpdateConcurrencyException 포함)/참조 삭제 경쟁; 503 + Retry-After: RenderBusyException 또는 57014/55P03; 401; 404·403: AdminSurfaceMiddleware(비관리 호스트 404, IP·X-Requested-With·Origin 위반 403) | F003, F006, F011, F018 | `PortfolioBlog.Api/Features/Posts/PostEndpoints.cs` PostEndpoints.UpdateAsync |
| GET | `/api/posts` | admin | 관리 SPA 글 목록 | 세션 필수 | 쿼리 q(≤100자), skip(≥0), take(1~200, 기본 50) | 200 PagedPostsDto(요약 목록+total) | 400 ValidationProblem; 401 세션 없음; 404: 관리 호스트가 아님(AdminSurfaceMiddleware); 403: 허용 IP 밖 / X-Requested-With 누락(AdminSurfaceMiddleware); 오류 본문 ProblemDetails, no-store | F002, F018 | `PortfolioBlog.Api/Features/Posts/PostEndpoints.cs` PostEndpoints.ListAsync |
| POST | `/api/posts` | admin | 관리 SPA 에디터 | 세션 필수 | JSON UpsertPostRequest(slug, title, summary, contentMarkdown, seriesId, seriesOrder, tagNames) | 201 Location=/api/posts/{id} + PostDetailDto | 400 검증 실패/중첩 과다; 409 slug 중복 또는 참조(시리즈·태그) 경쟁 삭제; 503 + Retry-After: RenderGate 슬롯 대기 초과(RenderBusyException) 또는 DB 57014/55P03; 401; 404: 관리 호스트가 아님(미들웨어); 403: 허용 IP 밖 / X-Requested-With 누락 / Origin 불일치(미들웨어) | F003, F011, F018 | `PortfolioBlog.Api/Features/Posts/PostEndpoints.cs` PostEndpoints.CreateAsync |
| POST | `/api/preview` | admin | 관리 SPA 에디터(디바운스 미리보기) | 세션 필수. RateLimitPolicy.Preview(preview-concurrency, preview-global 창) | JSON PreviewRequest{markdown} | 200 PreviewResponse{Html} | 400; 429 + Retry-After; 503 + Retry-After: RenderGate 대기 초과; 401; 404·403: AdminSurfaceMiddleware(비관리 호스트 404, IP·X-Requested-With·Origin 위반 403) | F004, F011, F019 | `PortfolioBlog.Api/Features/Preview/PreviewEndpoints.cs` PreviewEndpoints.Render |
| DELETE | `/api/series/{id:guid}` | admin | 관리 SPA | 세션 필수 | 경로 id | 204 | 404 없음(트랜잭션 롤백); 409 FK 위반(참조 경쟁); 401; 404·403: AdminSurfaceMiddleware(비관리 호스트 404, IP·X-Requested-With·Origin 위반 403) | F007, F018 | `PortfolioBlog.Api/Features/Series/SeriesEndpoints.cs` SeriesEndpoints.DeleteAsync |
| GET | `/api/series/{id:guid}` | admin | 관리 SPA | 세션 필수 | 경로 id | 200 SeriesDetailDto(소속 글 정렬 목록) / 404 | 404 시리즈 없음; 401; 404·403: AdminSurfaceMiddleware(비관리 호스트 404, IP·X-Requested-With 위반 403) | F007, F018 | `PortfolioBlog.Api/Features/Series/SeriesEndpoints.cs` SeriesEndpoints.GetAsync |
| PUT | `/api/series/{id:guid}` | admin | 관리 SPA | 세션 필수 | JSON UpsertSeriesRequest. slug 변경 불가. | 200 SeriesDto | 400; 404 없음 또는 저장 직전·재조회 직전 삭제 경쟁; 401; 404·403: AdminSurfaceMiddleware(비관리 호스트 404, IP·X-Requested-With·Origin 위반 403) | F007, F018 | `PortfolioBlog.Api/Features/Series/SeriesEndpoints.cs` SeriesEndpoints.UpdateAsync |
| GET | `/api/series` | admin | 관리 SPA | 세션 필수 | 없음 | 200 SeriesDto[](제목순, 글 수 포함) | 401; 404·403: AdminSurfaceMiddleware(비관리 호스트 404, IP·X-Requested-With 위반 403) | F007, F018 | `PortfolioBlog.Api/Features/Series/SeriesEndpoints.cs` SeriesEndpoints.ListAsync |
| POST | `/api/series` | admin | 관리 SPA | 세션 필수 | JSON UpsertSeriesRequest(slug,title,description) | 201 Location + SeriesDto | 400; 409 slug 중복(경쟁 포함); 401; 404·403: AdminSurfaceMiddleware(비관리 호스트 404, IP·X-Requested-With·Origin 위반 403) | F007, F018 | `PortfolioBlog.Api/Features/Series/SeriesEndpoints.cs` SeriesEndpoints.CreateAsync |
| DELETE | `/api/tags/{id:guid}` | admin | 관리 SPA | 세션 필수 | 경로 id | 204 | 404 없음; 401; 404·403: AdminSurfaceMiddleware(비관리 호스트 404, IP·X-Requested-With·Origin 위반 403) | F008, F018 | `PortfolioBlog.Api/Features/Tags/TagEndpoints.cs` TagEndpoints.MapTagEndpoints (DELETE 람다) |
| GET | `/api/tags` | admin | 관리 SPA | 세션 필수 | 없음 | 200 TagDto[](NormalizedName순, 글 수 포함) | 401; 404·403: AdminSurfaceMiddleware(비관리 호스트 404, IP·X-Requested-With 위반 403) | F008, F018 | `PortfolioBlog.Api/Features/Tags/TagEndpoints.cs` TagEndpoints.MapTagEndpoints (GET 람다) |
| GET | `/attachments/{id:guid}/{fileName}` | both | 공개 사이트 글 본문의 <img>, 관리 SPA 미리보기 iframe, 크롤러, 링크 점검기 | AllowAnonymous. RateLimitPolicy.PublicAsset(IP별). RequireHost 없음, 미들웨어는 /api만 검사하므로 공개·관리 두 호스트 모두 응답(코드 주석이 두 사용처를 명시). | 경로 id만 조회 키. fileName은 장식이며 무시. If-None-Match 지원. Range 미지원. | 200 파일 스트림(ContentType, ETag=sha256, Last-Modified, Cache-Control: public,max-age=31536000,immutable) / 304 / 404. 모든 응답에 nosniff와 SandboxCsp. | 404 행 없음 또는 파일 열기 실패(FileNotFound/DirectoryNotFound); 503 + Retry-After: statement_timeout 57014(OverloadExceptionHandler); 429 + Retry-After; 400: 허용 Host 밖(HostFiltering) | F010 | `PortfolioBlog.Api/Features/Attachments/PublicAttachmentEndpoints.cs` PublicAttachmentEndpoints.GetAsync |
| HEAD | `/attachments/{id:guid}/{fileName}` | both | 캐시·링크 점검기 | AllowAnonymous, PublicAsset 한도 | GET과 같은 핸들러(MapMethods GET,HEAD) | GET과 같은 상태·헤더, 본문 없음(프레임워크가 생략) | 404; 503 + Retry-After(57014); 429 | F010 | `PortfolioBlog.Api/Features/Attachments/PublicAttachmentEndpoints.cs` PublicAttachmentEndpoints.GetAsync |
| GET | `/css/site.css` | both | 공개 Razor 페이지 | 없음(정적 파일 미들웨어, 속도 제한 엔드포인트 메타데이터 없음) | 없음 | 200 정적 파일, Cache-Control public,max-age=3600 | 404 파일 없음 | F020 | `PortfolioBlog.Api/Program.cs` UseStaticFiles |
| GET | `/health` | both | 컨테이너 헬스체크(HealthCheckCommand), 모니터링 | 없음(RequireHost 없음, /api 밖). RateLimitPolicy.PublicAsset | 없음 | 200 HealthResponse{Status:"Healthy", GeneratedAt}. 의존성 점검 없이 상수. | 429 + Retry-After: PublicAsset 한도 초과; 400: 허용되지 않은 Host(HostFiltering, 본문 없음) | F022 | `PortfolioBlog.Api/Program.cs` Program.cs MapGet("/health") |
| GET | `/css/highlight.css` | public | 공개 페이지 <link> | AllowAnonymous. PublicAsset 한도 | 없음(GET·HEAD) | 200 text/css, Cache-Control public,max-age=86400 | 429 + Retry-After | F017 | `PortfolioBlog.Api/Pages/SiteEndpoints.cs` SiteEndpoints.MapPublicSiteEndpoints (강조 CSS 람다) |
| GET | `/feed.xml` | public | 피드 구독기 | AllowAnonymous. PublicPage 한도 | 없음(GET·HEAD) | 200 application/atom+xml, Cache-Control public,max-age=300. 최신 PublicQueries.FeedSize개. | 429 + Retry-After; 503 + Retry-After: statement_timeout 57014(OverloadExceptionHandler) | F017 | `PortfolioBlog.Api/Pages/SiteEndpoints.cs` SiteEndpoints.FeedAsync |
| GET | `/` | public | 브라우저·크롤러 | 없음. PublicPageConvention이 GET/HEAD·공개 호스트·RateLimitPolicy.PublicPage를 건다. | 쿼리 page(추정, 세부 미확인) | 200 HTML 최신 글 목록 | 404 범위 밖 쪽(추정); 429 + Retry-After; 503 + Retry-After: statement_timeout 57014; 비공개 호스트 404, 비허용 메서드 405 | F012 | `PortfolioBlog.Api/Pages/Index.cshtml.cs` IndexModel.OnGetAsync |
| GET | `/posts/{slug}` | public | 브라우저·크롤러 | 없음. PublicPage 한도 | 경로 slug | 200 HTML(본문은 RenderedPostCache 히트 또는 미스 시 렌더). 두 경우 BodyHtml=null인 200: 렌더러 거부(MarkdownTooComplexException), 메타 조회 직후 경쟁 삭제. | 404 slug 형식 밖/글 없음; 503 + Retry-After: 캐시 미스 때 GetOrRenderAsync의 RenderGate 슬롯 대기 초과(RenderBusyException); 503 + Retry-After: statement_timeout 57014; 200(본문 없음): MarkdownTooComplexException 또는 경쟁 삭제; 429 + Retry-After | F011, F013 | `PortfolioBlog.Api/Pages/Post.cshtml.cs` PostModel.OnGetAsync |
| GET | `/robots.txt` | public | 크롤러 | AllowAnonymous. PublicAsset 한도 | 없음(GET·HEAD) | 200 text/plain: Allow 전체 + Sitemap URL | 429 + Retry-After | F017 | `PortfolioBlog.Api/Pages/SiteEndpoints.cs` SiteEndpoints.MapPublicSiteEndpoints (robots 람다) |
| GET | `/search` | public | 브라우저 | 없음. RateLimitPolicy.Search: search-concurrency와 search-ip 창에 더해 PublicPage와 같은 page-ip 창에도 계산되어 공개 페이지 IP 한도를 함께 소모한다. | 쿼리 q(트림 후 2~100자, 하나만), page(상한 50) | 200 HTML(빈 폼 또는 결과). noindex. | 400; 404; 429 + Retry-After; 503 + Retry-After: statement_timeout 57014 | F014, F019 | `PortfolioBlog.Api/Pages/Search.cshtml.cs` SearchModel.OnGetAsync |
| GET | `/series/{slug}` | public | 브라우저·크롤러 | 없음. PublicPage 한도 | 경로 slug(page 쿼리 여부는 미열람) | 200 HTML 시리즈 글 목록 | 404 시리즈 없음(추정); 429 + Retry-After; 503 + Retry-After: statement_timeout 57014 | F015 | `PortfolioBlog.Api/Pages/Series.cshtml.cs` SeriesPageModel.OnGetAsync |
| GET | `/sitemap.xml` | public | 검색 엔진 크롤러 | AllowAnonymous. PublicPage 한도 | 없음(GET·HEAD, HEAD도 GET과 같은 작업 수행) | 200 application/xml, Cache-Control public,max-age=300. 글·태그·시리즈 각 최대 SitemapMax건. | 429 + Retry-After; 503 + Retry-After: statement_timeout 57014 | F017 | `PortfolioBlog.Api/Pages/SiteEndpoints.cs` SiteEndpoints.SitemapAsync |
| GET | `/tags/{tag}` | public | 브라우저·크롤러 | 없음. PublicPage 한도 | 경로 tag(퍼센트 인코딩) | 200 HTML 태그별 글 목록 | 404 태그 없음/형식 밖(추정); 429 + Retry-After; 503 + Retry-After: statement_timeout 57014 | F016 | `PortfolioBlog.Api/Pages/Tag.cshtml.cs` TagPageModel.OnGetAsync |
<!-- /doc-harness:section -->

<!-- doc-harness:section id="details" hash="0a92219dfadd3a3a3811ea3be8ec1c7592b9eee27284c8107c7b6155ec2dc836" -->
## 상세



### DELETE /api/attachments/{id:guid}

- 검증: guid 라우트 제약
- 부작용: 파일 삭제(실패 시 고아 파일로 남고 AttachmentJanitor가 정리); 감사 로그
- DB 변경: Attachments DELETE(ExecuteDeleteAsync, 잠금 안)
- 상태: CONFIRMED · 근거: `PortfolioBlog.Api/Features/Attachments/AttachmentEndpoints.cs` AttachmentEndpoints.DeleteAsync (198-213)

### GET /api/attachments

- 검증: skip·take 범위 밖 400
- 부작용: 없음
- DB 변경: Attachments 조회
- 상태: CONFIRMED · 근거: `PortfolioBlog.Api/Features/Attachments/AttachmentEndpoints.cs` AttachmentEndpoints.ListAsync (81-93)

### POST /api/attachments

- 검증: file 없음·빈 파일 400; 10MB 초과 413; 시그니처 판정 실패·손상 415; 표시 이름 정리(DisplayName)
- 부작용: 메타데이터 제거 후 내용 주소 파일 저장; 감사 로그(id·sha256·크기)
- DB 변경: Attachments insert(sha256 잠금 AttachmentLock 안에서)
- 상태: CONFIRMED · 근거: `PortfolioBlog.Api/Features/Attachments/AttachmentEndpoints.cs` AttachmentEndpoints.UploadAsync (42-51,113-179)

### POST /api/auth/login

- 검증: password null이거나 256자 초과면 400 ValidationProblem; AdminCredential.Verify로 비밀번호 검증
- 부작용: 성공 시 SignInAsync로 쿠키 발급(지문·SessionEpoch 클레임); 성공·실패를 RemoteIp만 남기고 로그(비밀번호 미기록)
- DB 변경: AdminStates에서 SessionEpoch 읽기만(변경 없음)
- 상태: CONFIRMED · 근거: `PortfolioBlog.Api/Features/Auth/AuthEndpoints.cs` AuthEndpoints.LoginAsync (48-99), `PortfolioBlog.Api/Infrastructure/Web/RateLimitingExtensions.cs` BuildChain (88-101)

### POST /api/auth/logout

- 검증: 세션 필수(그룹 인가 정책)
- 부작용: 쿠키 삭제(SignOutAsync); 전 세션 폐기(SessionEpoch+1); 감사 로그
- DB 변경: AdminStates.SessionEpoch = SessionEpoch + 1 (ExecuteUpdateAsync)
- 상태: CONFIRMED · 근거: `PortfolioBlog.Api/Features/Auth/AuthEndpoints.cs` AuthEndpoints.LogoutAsync (49,115-123)

### GET /api/auth/me

- 검증: AdminSurfaceMiddleware 통과(호스트·IP·X-Requested-With)
- 부작용: 없음(읽기)
- DB 변경: 세션 검증 시 SessionValidator가 epoch 조회(값 확인은 별도 파일)
- 상태: CONFIRMED · 근거: `PortfolioBlog.Api/Features/Auth/AuthEndpoints.cs` MapAuthEndpoints (37-50), `PortfolioBlog.Api/Infrastructure/Access/AdminSurfaceMiddleware.cs` AdminSurfaceMiddleware.InvokeAsync (67-96)

### DELETE /api/posts/{id:guid}

- 검증: version 없으면 400
- 부작용: 감사 로그
- DB 변경: Posts DELETE WHERE xmin=version(PostTags는 DB cascade)
- 상태: CONFIRMED · 근거: `PortfolioBlog.Api/Features/Posts/PostEndpoints.cs` PostEndpoints.DeleteAsync (263-285)

### GET /api/posts/{id:guid}

- 검증: id는 guid 라우트 제약
- 부작용: 없음
- DB 변경: Posts 조회
- 상태: CONFIRMED · 근거: `PortfolioBlog.Api/Features/Posts/PostEndpoints.cs` PostEndpoints.GetAsync (101-102)

### PUT /api/posts/{id:guid}

- 검증: PostValidation.Validate; slug가 현재와 다르면 400; version 누락 400; seriesId 존재 확인; version 불일치 시 렌더보다 먼저 409; MarkdownTooComplexException이면 400
- 부작용: 렌더 캐시 선채움; 감사 로그
- DB 변경: 트랜잭션: Posts update(WHERE xmin=Version), PostTags 차집합 삭제·추가, Tags upsert
- 상태: CONFIRMED · 근거: `PortfolioBlog.Api/Features/Posts/PostEndpoints.cs` PostEndpoints.UpdateAsync (187-245)

### GET /api/posts

- 검증: skip<0, take 범위 밖, q에 NUL 또는 100자 초과 → 400
- 부작용: 없음
- DB 변경: Posts 조회(q 있으면 Title·Summary·ContentMarkdown ILIKE)
- 상태: CONFIRMED · 근거: `PortfolioBlog.Api/Features/Posts/PostEndpoints.cs` PostEndpoints.ListAsync (64-86)

### POST /api/posts

- 검증: PostValidation.Validate; seriesId 존재 확인; RenderGate 렌더 가능성 확인 — MarkdownTooComplexException이면 400; slug 중복 사전 검사
- 부작용: 저장 뒤 RenderedPostCache에 (Id, Version) 선채움(본문이 같을 때만); 감사 로그
- DB 변경: 트랜잭션: Tags upsert, Posts insert, PostTags insert
- 상태: CONFIRMED · 근거: `PortfolioBlog.Api/Features/Posts/PostEndpoints.cs` PostEndpoints.CreateAsync (124-164), `PortfolioBlog.Api/Infrastructure/Web/OverloadExceptionHandler.cs` IsOverload (63-70)

### POST /api/preview

- 검증: markdown null 400(빈 문자열 허용); NUL 포함 400; UTF-8 기준 MarkdownRenderer.MaxInputBytes 초과 400; MarkdownTooComplexException이면 400
- 부작용: 서버에 보관하지 않음, 본문 로그 미기록
- DB 변경: 없음
- 상태: CONFIRMED · 근거: `PortfolioBlog.Api/Features/Preview/PreviewEndpoints.cs` PreviewEndpoints.Render (36-77)

### DELETE /api/series/{id:guid}

- 검증: 없음
- 부작용: 소속 글의 SeriesId·SeriesOrder를 함께 비움; 감사 로그
- DB 변경: 트랜잭션: SELECT ... FOR UPDATE, Posts UPDATE(시리즈 해제), Series DELETE
- 상태: CONFIRMED · 근거: `PortfolioBlog.Api/Features/Series/SeriesEndpoints.cs` SeriesEndpoints.DeleteAsync (192-223)

### GET /api/series/{id:guid}

- 검증: guid 라우트 제약
- 부작용: 없음
- DB 변경: Series·Posts 조회
- 상태: CONFIRMED · 근거: `PortfolioBlog.Api/Features/Series/SeriesEndpoints.cs` SeriesEndpoints.GetAsync (81-91)

### PUT /api/series/{id:guid}

- 검증: SeriesValidation.Validate; slug가 다르면 400
- 부작용: 감사 로그
- DB 변경: Series update(title, description)
- 상태: CONFIRMED · 근거: `PortfolioBlog.Api/Features/Series/SeriesEndpoints.cs` SeriesEndpoints.UpdateAsync (146-172)

### GET /api/series

- 검증: 없음
- 부작용: 없음
- DB 변경: Series 조회
- 상태: CONFIRMED · 근거: `PortfolioBlog.Api/Features/Series/SeriesEndpoints.cs` SeriesEndpoints.ListAsync (65-66)

### POST /api/series

- 검증: SeriesValidation.Validate; slug 중복 사전 검사
- 부작용: 감사 로그
- DB 변경: Series insert
- 상태: CONFIRMED · 근거: `PortfolioBlog.Api/Features/Series/SeriesEndpoints.cs` SeriesEndpoints.CreateAsync (108-126)

### DELETE /api/tags/{id:guid}

- 검증: guid 라우트 제약
- 부작용: 감사 로그
- DB 변경: Tags DELETE(PostTags 링크는 FK cascade, 글 행·Version 불변)
- 상태: CONFIRMED · 근거: `PortfolioBlog.Api/Features/Tags/TagEndpoints.cs` MapTagEndpoints (37-42)

### GET /api/tags

- 검증: 없음
- 부작용: 없음
- DB 변경: Tags 조회
- 상태: CONFIRMED · 근거: `PortfolioBlog.Api/Features/Tags/TagEndpoints.cs` MapTagEndpoints (30-36)

### GET /attachments/{id:guid}/{fileName}

- 검증: id는 guid 라우트 제약
- 부작용: 없음
- DB 변경: PublicDbContext로 Attachments 읽기(읽기 전용 연결, statement_timeout)
- 상태: CONFIRMED · 근거: `PortfolioBlog.Api/Features/Attachments/PublicAttachmentEndpoints.cs` MapPublicAttachmentEndpoints / GetAsync (9,36,50-54,81-119), `PortfolioBlog.Api/Program.cs` HostFilteringOptions (34-42)

### HEAD /attachments/{id:guid}/{fileName}

- 검증: id guid 제약
- 부작용: 없음
- DB 변경: GET과 동일한 읽기
- 상태: CONFIRMED · 근거: `PortfolioBlog.Api/Features/Attachments/PublicAttachmentEndpoints.cs` MapPublicAttachmentEndpoints (47-54)

### GET /css/site.css

- 검증: 없음
- 부작용: 없음
- DB 변경: 없음
- 상태: INFERRED · 근거: `PortfolioBlog.Api/Program.cs` UseStaticFiles (102-103)

### GET /health

- 검증: 없음
- 부작용: 없음
- DB 변경: 없음
- 상태: CONFIRMED · 근거: `PortfolioBlog.Api/Program.cs` MapGet /health (115-120)

### GET /css/highlight.css

- 검증: 없음
- 부작용: 없음
- DB 변경: 없음
- 상태: CONFIRMED · 근거: `PortfolioBlog.Api/Pages/SiteEndpoints.cs` MapPublicSiteEndpoints (66-72)

### GET /feed.xml

- 검증: XmlText.Clean으로 무효 문자 제거
- 부작용: 없음
- DB 변경: PublicDbContext: 피드 SELECT 1문장
- 상태: CONFIRMED · 근거: `PortfolioBlog.Api/Pages/SiteEndpoints.cs` SiteEndpoints.FeedAsync (74-75,99-143), `PortfolioBlog.Api/Infrastructure/Web/OverloadExceptionHandler.cs` IsOverload (63-70)

### GET /

- 검증: 쪽 번호 검증(세부는 Index.cshtml.cs 미열람)
- 부작용: 없음
- DB 변경: PublicDbContext 읽기
- 상태: CONFIRMED · 근거: `PortfolioBlog.Api/Pages/Index.cshtml.cs` IndexModel.OnGetAsync (19,49), `PortfolioBlog.Api/Pages/PublicPageConvention.cs` PublicPageConvention.Apply (32-41), `PortfolioBlog.Api/Infrastructure/Web/OverloadExceptionHandler.cs` IsOverload (63-70)

### GET /posts/{slug}

- 검증: 공백뿐이거나 SlugRules 형식 밖이면 DB 조회 없이 404
- 부작용: 캐시 미스 시 (Id, Version) 키로 렌더 결과 캐시
- DB 변경: PublicDbContext: GetPostAsync(메타), 미스 시 GetContentAsync(본문)
- 상태: CONFIRMED · 근거: `PortfolioBlog.Api/Pages/Post.cshtml.cs` PostModel.OnGetAsync / RenderAsync (27-29,43-74), `PortfolioBlog.Api/Infrastructure/Markdown/RenderGate.cs` RenderGate.RenderAsync (88)

### GET /robots.txt

- 검증: 없음
- 부작용: 없음
- DB 변경: 없음
- 상태: CONFIRMED · 근거: `PortfolioBlog.Api/Pages/SiteEndpoints.cs` MapPublicSiteEndpoints (78-81)

### GET /search

- 검증: q 반복 → 400 본문 있는 HTML; 길이 밖·NUL·2자 미만 → 400 본문 있는 HTML; page 형식·상한 밖이거나 page>1인데 결과 없음 → 404
- 부작용: 없음
- DB 변경: PublicDbContext: COUNT 1회 + 목록 SELECT 1회
- 상태: CONFIRMED · 근거: `PortfolioBlog.Api/Pages/Search.cshtml.cs` SearchModel.OnGetAsync (65-88), `PortfolioBlog.Api/Infrastructure/Web/RateLimitingExtensions.cs` BuildChain (93,99-100), `PortfolioBlog.Api/Pages/PublicPageConvention.cs` PublicPageConvention.Apply (34)

### GET /series/{slug}

- 검증: slug 검증 세부는 미열람
- 부작용: 없음
- DB 변경: PublicDbContext 읽기
- 상태: CONFIRMED · 근거: `PortfolioBlog.Api/Pages/Series.cshtml.cs` SeriesPageModel.OnGetAsync (20,37)

### GET /sitemap.xml

- 검증: 없음
- 부작용: 없음
- DB 변경: PublicDbContext: 3문장 순차 SELECT
- 상태: CONFIRMED · 근거: `PortfolioBlog.Api/Pages/SiteEndpoints.cs` SiteEndpoints.SitemapAsync (76-77,160-181)

### GET /tags/{tag}

- 검증: tag 검증 세부는 미열람(PublicUrls.Tag이 null을 돌릴 수 있음)
- 부작용: 없음
- DB 변경: PublicDbContext 읽기
- 상태: CONFIRMED · 근거: `PortfolioBlog.Api/Pages/Tag.cshtml.cs` TagPageModel.OnGetAsync (21,57)
<!-- /doc-harness:section -->

<!-- doc-harness:section id="other" hash="49b1d2c82d70fcec3339d00010fe0953177c524efb5135dc55094719ae3017df" -->
## HTTP 밖의 인터페이스

| 내용 | 상태 | 근거 |
|---|---|---|
| CLI `dotnet PortfolioBlog.Api.dll hash-password`: 웹 호스트를 만들지 않고 비밀번호 해시를 출력하고 종료한다(HashPasswordCommand.Run). | CONFIRMED | `PortfolioBlog.Api/Program.cs` HashPasswordCommand.Run (15-19) |
| CLI `dotnet PortfolioBlog.Api.dll healthcheck`: 같은 컨테이너의 /health를 한 번 호출하고 종료 코드로 답한다(컨테이너 헬스체크용). | CONFIRMED | `PortfolioBlog.Api/Program.cs` HealthCheckCommand.RunAsync (21-26) |
| BackgroundService AttachmentJanitor: 고아 첨부 파일 정리(AddHostedService). 정리 주기 등 세부는 이 세션에서 열람하지 않았다. | INFERRED | `PortfolioBlog.Api/Program.cs` AddHostedService AttachmentJanitor (66-67) |
| 개발 환경 전용 OpenAPI 문서 엔드포인트(MapOpenApi): Development에서만 등록된다. 경로 세부는 프레임워크 기본값으로 이 세션에서 확인하지 않았다. | CONFIRMED | `PortfolioBlog.Api/Program.cs` app.MapOpenApi (110-113) |
| 기동 시 부트스트랩: StartupValidation.Validate, 첨부 루트 쓰기 검사, 마이그레이션(Migrate), Public 연결 문자열이 있으면 PublicRoleGrants.Apply, 마크다운 렌더러 워밍업. | CONFIRMED | `PortfolioBlog.Api/Program.cs` top-level statements (75-91) |
| 관리 표면 방어(미들웨어): /api 요청에 Cache-Control: no-store를 걸고, 관리 호스트가 아니면 404, 허용 IP 밖이면 403, X-Requested-With: XMLHttpRequest가 없으면 403, GET·HEAD가 아닌데 Origin이 관리 origin과 다르면 403을 ProblemDetails로 돌려준다. 순서는 호스트, IP, CSRF 헤더, Origin이다. | CONFIRMED | `PortfolioBlog.Api/Infrastructure/Access/AdminSurfaceMiddleware.cs` AdminSurfaceMiddleware.InvokeAsync (67-96) |
| 과부하 매핑: PostgresException 57014·55P03 또는 RenderBusyException이면 OverloadExceptionHandler가 503 + Retry-After 5초로 바꾼다. | CONFIRMED | `PortfolioBlog.Api/Infrastructure/Web/OverloadExceptionHandler.cs` OverloadExceptionHandler (20,35-70) |
| Razor 페이지 공통 규약: 모든 페이지에 GET/HEAD 전용, 공개 호스트 전용, 속도 제한 정책(/Search만 Search, 나머지 PublicPage)을 라우팅 메타데이터로 건다. | CONFIRMED | `PortfolioBlog.Api/Pages/PublicPageConvention.cs` PublicPageConvention.Apply (32-41) |
| 관리 SPA 라우트(/login, /, /posts/new, /posts/:id, /series, /tags, /attachments)는 PortfolioBlog.Web 클라이언트 라우터가 처리하며 Caddy가 정적 서빙한다. 서버 엔드포인트가 아니다. | INFERRED | `PortfolioBlog.Web/src/app/routes.tsx` routes |
<!-- /doc-harness:section -->

<!-- doc-harness:section id="unknowns" hash="57cd379ca7d917b8ddf0631d52b95cc228a9e15eba1e0311a13d88f2c3135dd3" -->
## 확인하지 못한 것

- Index·Series·Tag 페이지의 page 쿼리 검증과 404 조건 세부(Index.cshtml.cs, Series.cshtml.cs, Tag.cshtml.cs 본문)는 이 세션에서 열람하지 않았다.
- ApiBodyLimitMiddleware의 /api 본문 한도 초과 응답 코드는 열람하지 않아 오류 열에 넣지 않았다.
- AttachmentJanitor의 정리 주기와 조건은 열람하지 않았다.
- /css/site.css 이외 정적 파일 존재 여부는 wwwroot를 열람하지 않아 확인하지 못했다(Program.cs 주석은 site.css 하나뿐이라 함).
<!-- /doc-harness:section -->

<!-- doc-harness:section id="related" hash="e3d850d681227030d7732b6acbc60121d3b5651a74814efb8b728fb62fd82eb6" -->
## 관련 문서

- [09_FEATURES](09_FEATURES.md)
- [07_DATA_MODEL](07_DATA_MODEL.md)
- [13_SECURITY](13_SECURITY.md)
<!-- /doc-harness:section -->
