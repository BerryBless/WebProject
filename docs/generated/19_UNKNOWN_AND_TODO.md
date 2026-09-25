# 확인하지 못한 것과 TODO

<!-- doc-harness:section id="summary" hash="605cf038d7feca73e33ccce555a6910790f5dc4710d9911ff0b62e5762244658" -->
## 한 줄 요약

분석이 확인하지 못한 항목 211개. 새 개발자가 코드를 고치기 전에 직접 확인해야 하는 목록이다.
<!-- /doc-harness:section -->

<!-- doc-harness:section id="unknowns" hash="1f00851e2fae09787857c309e66cb744fb1553446ec3431f306868fe168507af" -->
## UNKNOWN 목록



### Inventory

- appsettings.json·.env의 구체적 값(도메인, 비밀번호 등)은 규칙에 따라 조사하지 않았다.
- 외부 이메일/알림/분석/결제 등 서드파티 API 연동 여부는 코드에서 확인되지 않아 UNKNOWN이다.
- 메시지 큐/브로커 사용 여부는 코드에서 확인되지 않아 UNKNOWN이다.
- doc-harness/ 디렉터리의 실제 동작 방식은 세션 규칙상 분석 대상에서 제외되어 조사하지 않았다.
- PortfolioBlog.Web/.e2e/env.json의 구체적 내용은 조사하지 않았다(값 노출 회피 및 범위 밖).

### Architecture

- StartupValidation.Validate가 실제로 검사하는 항목(운영에서 ConnectionStrings:Public 필수 여부 등)은 이 단계에서 본문을 읽지 않아 주석 수준으로만 확인했다.
- IpAllowlistAdminAccessPolicy와 CidrList의 구체적 판정 로직(IPv4-mapped IPv6 처리 등)은 확인하지 않았다.
- MarkdownRenderer 내부의 위생화(HtmlSanitizer·HtmlAllowlist·UrlPolicy) 적용 순서와 BoundedHighlighting 시간 예산은 이 단계의 범위 밖이라 읽지 않았다.
- ApiBodyLimitMiddleware의 실제 바이트 한도 값과 초과 시 상태 코드는 확인하지 않았다(주석상 413 추정).
- RenderingOptions·PublicOptions·AdminOptions·AttachmentOptions의 기본값(동시성, 분당 한도, CacheMegabytes, StatementTimeoutMs, JanitorEnabled)은 appsettings 값을 옮기지 않는다는 규칙 때문에 확인하지 않았다.
- CI 워크플로(.github/workflows/ci.yml)의 잡 구성과, 스모크 오버레이(docker-compose.smoke.yml)가 CI에서 실행되는지는 재검증하지 않았다.
- SPA의 queryClient(재시도·staleTime 정책)와 RequireAuth의 /api/auth/me 처리 방식, drafts.ts의 브라우저 저장 방식은 확인하지 않았다.
- /health는 DB 등 외부 의존성을 점검하지 않으므로, compose의 'api healthy'는 프로세스 생존만 뜻하고 DB 가용성은 뜻하지 않는다(코드상 사실, 운영 영향은 미평가).
- ErrorPipelineTests 등 테스트가 파이프라인 순서의 어떤 불변식을 고정하는지는 이 단계에서 읽지 않았다.

### 기능 발견

- 정답 목록의 엔드포인트 경로는 그룹 접두사가 빠진 형태다. 코드(ApiEndpoints.cs의 MapGroup("/api")와 각 모듈의 MapGroup)로 /api/auth·/api/posts·/api/series·/api/tags·/api/attachments·/api/preview 접두사를 확인해 기능에 배정했다.
- AdminStates.SessionEpoch를 올리는 경로(예: 전체 세션 무효화)가 로그아웃 외에 따로 있는지는 이 단계에서 확인하지 않았다.
- PortfolioBlog.Api/Dockerfile·PortfolioBlog.Web/Dockerfile의 존재는 Inventory 근거에 기댔고, 이 단계에서 Glob으로 직접 재확인하지 않았다.
- 공개 글 목록에서 '공개(발행)' 여부를 가르는 기준(초안/예약 발행 등)은 PublicQueries를 읽지 않아 확인하지 못했다.
- 영향 분석 힌트가 F006(글 저장 충돌 감지·비교 해결)을 영향 기능으로 골랐지만, 변경된 코드 파일 PortfolioBlog.Api/Features/Tags/TagEndpoints.cs는 F006의 relatedFiles에 없고 DbConflict·Conflict 참조도 없다(Grep으로 확인). 그래서 F006을 changedFeatureIds에서 뺐다. 힌트가 F006을 고른 근거는 확인하지 못했다.
- PortfolioBlog.Api/Features/Tags/TagEndpoints.cs:5의 변경은 주석 한 줄('// doc-harness incremental check (temporary, reverted after the test)')뿐이다. 엔드포인트 GET /api/tags(33행)와 DELETE /api/tags/{id:guid}(37행)는 그대로 있다. F008은 관련 파일이 바뀌어 재검증 대상에 넣었으며, 동작은 바뀌지 않았을 것으로 추론한다(INFERRED).

### F001 관리자 로그인·세션 확인·로그아웃

- 검증 지적 사항(F006·F008 행의 의존 목록, F002·F007·F011·F018 행과 의존 표의 불일치)은 모두 다른 기능의 의존 색인 표에 관한 것이다. F001의 의존 목록과는 관련이 없다. 이 세션은 F001만 분석하므로 해당 행은 여기서 고칠 수 없다. F001의 의존(F018 AdminSurfaceMiddleware, F019 로그인 속도 제한, F020 ErrorResponses·OverloadExceptionHandler·ApiBodyLimitMiddleware, F021 StartupValidation, F029 request·queryClient)은 코드로 다시 확인해 유지했다. 지적 내용 자체(모든 /api 요청이 Program.cs 104행의 AdminSurfaceMiddleware와 43·100행의 예외 처리기를 거친다)는 이번에 읽은 Program.cs에서 사실로 확인했다.
- 입력으로 받은 이전 분석이 diagrams 중간에서 잘려, 이전 history 항목이 있었는지 확인할 수 없었다. 이번 재검증에서는 동작 변경이 없어 새 history 항목을 추가하지 않았다.
- OverloadExceptionHandler가 처리하지 않는 예외(예: AdminState 행이 없을 때의 InvalidOperationException, DB 연결 실패)에 대해 UseExceptionHandler + AddProblemDetails 조합이 실제로 어떤 상태 코드와 본문을 내는지는 테스트로 확인하지 못했다. 500 ProblemDetails일 것으로 추론한다.
- Data Protection 키를 교체하거나 잃어버렸을 때(KeysPath 볼륨 유실 등) 기존 세션이 어떻게 되는지는 복호화 실패 → 미인증 경로로 추론할 뿐, 코드나 테스트로 확인하지 않았다.
- PortfolioBlog.Web/src/test/auth.test.tsx의 개별 테스트 이름과 줄 범위는 이번 세션에서 다시 읽지 않았다(파일이 있는지만 확인했다).

### F002 관리 글 목록 조회·삭제

- 관리 연결(ConnectionStrings:Default)에 statement_timeout 같은 서버·연결 옵션이 들어 있는지는 확인하지 않았다(설정 값을 보지 않는 규칙). 코드상 AppDbContext 등록(DataServiceCollectionExtensions.cs 32행)에는 timeout 옵션이 없다. 그래서 OverloadExceptionHandler의 57014 → 503 경로가 관리 목록 조회에서 실제로 발생할 수 있는지는 알 수 없다.
- 처리되지 않은 비과부하 DB 예외의 최종 응답이 500 ProblemDetails라는 것은 AddProblemDetails + UseExceptionHandler의 프레임워크 기본 동작에서 추론했다. 이 기능을 대상으로 한 테스트는 찾지 못했다.
- DELETE에 version=abc처럼 uint로 해석할 수 없는 값을 보내면 400이 된다는 것은 ThrowOnBadRequest=false 설정에서 추론했다. 이 경우를 직접 검증하는 테스트는 찾지 못했다.
- ContentMarkdown ILIKE 검색의 실제 실행 계획(순차 스캔 여부)과 글 수에 따른 응답 시간은 확인하지 못했다.
- 검증 지적 중 F006·F008 행의 의존 표 불일치(F006 → F001·F003·F005·F007·F008·F011·F018·F020·F029, F008 → F001·F018·F020·F029)는 이 세션(F002 단일 기능)의 출력 스키마로 고칠 수 없다. 그 행들은 해당 기능의 분석이나 의존 표를 생성하는 단계에서 반영해야 한다. 다만 지적의 근거 자체는 코드와 맞다. 모든 /api 요청은 Program.cs 104행의 UseMiddleware<AdminSurfaceMiddleware>(F018)를 거치고, 잡지 않은 예외는 43행 AddExceptionHandler<OverloadExceptionHandler>와 100행 UseExceptionHandler(F020)가 처리한다. 이 코드로 /api를 쓰는 기능이 F018·F020에 의존한다는 점은 확인했다.
- CONTRADICTION 지적(의존 표의 F002 행이 F001·F029뿐)에 대해: 이 문서의 F002 의존은 코드로 다시 확인해 F001·F018·F020·F029로 유지했다. 기능 간 의존 표의 F002 행을 이 목록에 맞춰야 한다. /api/posts에는 RateLimitMetadata가 없어(RateLimitingExtensions.cs 129행 Matches) F019는 넣지 않았다. 삭제가 RenderedPostCache를 건드리지 않으므로 F011도 넣지 않았다.

### F003 글 작성·수정(마크다운 에디터)

- 검증에서 지적된 문제는 모두 F003 행이 아니라 다른 기능 행에 관한 것이다: 기능 간 의존 색인 표의 F002·F006·F007·F008·F011·F018 행. 이 세션은 F003 하나만 분석하므로 그 행들을 고칠 수 없다. F003 쪽에서 코드로 확인한 것은 다음과 같다. 모든 /api 요청은 Program.cs 104줄 UseMiddleware<AdminSurfaceMiddleware>(F018)와 43·100줄 OverloadExceptionHandler/UseExceptionHandler(F020)를 거친다. ApiEndpoints.cs 39줄의 RequireAuthorization(F001)도 적용된다. 따라서 지적 내용(/api를 쓰는 기능은 F018·F020에 의존)은 코드와 맞는다. 색인 표 수정은 해당 기능의 분석이나 종합 단계에서 처리해야 한다.
- F003의 dependencies(F001, F004, F005, F006, F007, F008, F009, F011, F018, F020, F029)는 코드로 다시 확인해 유지했다. F019(속도 제한)는 UseRateLimiter를 거치기는 하지만 POST/PUT /api/posts가 무제한 'none' 파티션으로 가므로 의존에서 뺐다.
- 입력으로 받은 이전 분석 JSON이 diagrams 중간에서 잘려 이전 history 항목이 있었는지 확인할 수 없다. 이번 재검증에서 F003의 동작 변경은 없어 새 history 항목은 추가하지 않았다.
- TagEndpoints.cs 5줄의 주석 'doc-harness incremental check (temporary, reverted after the test)'은 작업 트리 변경으로 보인다. F003 동작과는 무관하다.
- EF Core의 await using 트랜잭션 해제 시 자동 롤백 동작은 코드에서 직접 볼 수 없다(프레임워크 동작 추론).
- CHECK 위반 등 처리되지 않은 DbUpdateException이 최종적으로 어떤 응답(500 본문 형태)이 되는지는 ErrorResponses·UseStatusCodePages 조합을 실행해 보지 않아 확인하지 못했다.

### F004 마크다운 실시간 미리보기

- RenderGate 슬롯을 기다리는 중 클라이언트가 abort하면 OperationCanceledException이 난다. 앱 코드에는 이를 전용으로 처리하는 곳이 없으므로(grep 확인) 프레임워크 기본 예외 처리로 간다. 이때 서버가 최종적으로 기록하는 상태 코드와 로그 수준은 실측 근거가 없어 알 수 없다.
- 운영 환경에서 Rendering:Concurrency·QueueTimeoutMs와 Admin:PreviewPerMinute·PreviewConcurrency의 실제 값(appsettings·환경변수)은 확인하지 않았다. 코드 기본값은 각각 2, 5000ms, 60, 2다.
- 잘못된 JSON이나 비 JSON Content-Type에 대해 프레임워크가 돌려주는 정확한 응답 본문(ProblemDetails 여부)과 415 여부는 테스트로 확인하지 않았다.
- 검증 지적(기능 간 의존 표 불일치)은 F001~F003 문서의 의존 목록과 색인 표를 대상으로 한다. 이 세션의 출력 범위(F004) 밖이라 여기서 고칠 수 없다. F004의 의존 목록은 코드로 다시 확인했다: F001 세션 인가·noteAuthFailure, F003 PostEditorPage가 호스트, F010 iframe 이미지 로드(E2E), F011 RenderGate·MarkdownRenderer, F018 AdminSurfaceMiddleware·CSRF 헤더, F019 Preview 속도 제한, F020 OverloadExceptionHandler·ApiBodyLimitMiddleware, F021 StartupValidation의 Preview·Rendering 설정 검사, F029 request·ErrorNotice.

### F005 편집 임시본 자동 보관·복원

- 복원된 fields에 딸려 요청 본문에 실리는 `baseVersion`·`savedAt`을 서버 역직렬화가 실제로 무시하는지는 요청이나 테스트로 확인하지 않았다. `UpsertPostRequest`에 해당 속성이 없고 API 코드에 알 수 없는 속성을 거부하는 설정이 없다는 점만 확인했다.
- `window.localStorage` 접근 자체가 예외를 던지는 브라우저 환경에서 편집 화면이 실제로 오류 화면(RouteError)으로 가는지는 재현하지 않았다.
- 여러 탭에서 같은 키로 동시에 편집할 때 실제 사용자에게 미치는 영향은 측정하지 않았다.
- 기존 글 onSuccess에서 fieldsRef 기준 `unchanged`와 setFields 업데이터의 prev 기준 판단이 실제로 갈라지는 타이밍이 생기는지는 재현하지 않았다.
- 검증에서 지적된 문제는 모두 다른 기능 행에 대한 것이다. F006 행의 의존 목록(F001·F003·F005·F007·F008·F011·F018·F020·F029), F008 행의 의존 목록(F001·F018·F020·F029), F002·F007·F011·F018 행이 대상이다. 이 세션의 출력은 F005 기능 객체 하나뿐이어서 그 표 행을 여기서 고칠 수 없다. F005의 의존은 코드로 다시 확인해 F003·F006·F029로 둔다. F003은 PostEditorPage·posts.create/update, F006은 ConflictPanel·onTakeServer의 clearDraft, F029는 routes.tsx 지연 로딩·noteAuthFailure·request다. 참고로 F006이 F005에 의존한다는 지적은 ConflictPanel 문구와 onTakeServer의 clearDraft로 보아 코드와 맞는다.

### F006 글 저장 충돌 감지·비교 해결

- 409 응답의 detail을 클라이언트가 원인별(stale version / 참조 삭제)로 구분하려는 의도가 있었는지는 코드와 문서로 확인하지 못했다.
- 시리즈 수정·순서 변경 같은 다른 시리즈 작업이 Posts 행을 갱신하는지(Version을 바꾸는지)는 SeriesEndpoints 핸들러를 읽지 않아 확인하지 못했다. 시리즈 삭제는 테스트로, 태그 삭제는 코드와 문서 주석으로 확인했다.
- TagEndpoints 클래스 문서 주석(현재 14-15행)이 언제 추가됐는지는 Git 이력을 볼 수 없어 확인하지 못했다. 이 문서의 TagEndpoints 줄 번호는 5행 임시 주석('temporary, reverted after the test')이 있는 현재 파일 기준이다. 임시 주석을 되돌리면 문서 주석은 13-14행, DeleteTag는 36-41행이 된다.

### F007 시리즈 관리

- 처리되지 않은 일반 예외(과부하가 아닌 DB 오류)가 UseExceptionHandler와 AddProblemDetails 조합에서 정확히 어떤 500 본문이 되는지는 프레임워크 기본 동작에 달려 있어, 이 저장소 코드로는 확인하지 못했다.
- 관리용 AppDbContext 연결에 lock_timeout이나 statement_timeout을 설정하는 코드는 찾지 못했다. 따라서 삭제의 FOR UPDATE 잠금 대기에 상한이 있는지는 운영 DB 설정에 달려 있으며, 확인하지 못했다.
- 공개 시리즈 페이지(F015)와 RenderedPostCache가 시리즈 정보를 서버 쪽에 캐시하는지, 그래서 시리즈 수정·삭제가 공개 쪽에 얼마나 늦게 반영되는지는 조사하지 않았다.
- 검증 지적(F006 행을 F001·F003·F005·F007·F008·F011·F018·F020·F029로, F008 행을 F001·F018·F020·F029로, F002 행을 F001·F018·F020·F029로 보정)은 다른 기능의 의존 목록과 기능 간 의존 표에 관한 것이다. 이 세션은 F007 feature 객체만 출력할 수 있어 직접 반영하지 못했다. 코드로 확인한 근거는 다음과 같다. 모든 /api 요청은 Program.cs 104행 AdminSurfaceMiddleware(F018)와 43·100행 OverloadExceptionHandler/UseExceptionHandler(F020)를 거친다. F006→F007은 SeriesEndpoints.DeleteAsync의 ExecuteUpdateAsync가 xmin Version(AppDbContext 96행)을 바꾸는 점으로 뒷받침된다. 따라서 지적은 코드와 맞는다. F007 자신의 의존은 F001·F018·F020·F029로 유지했다(F019는 속도 제한 메타데이터가 없어 제외, F021은 기동 전제일 뿐 호출 경로 의존이 아니라 제외).

### F008 태그 관리

- ResolveIdsAsync의 사전 조회(existing)와 최종 Id 조회 사이에 태그가 동시에 삭제되면, 글이 그 태그 없이 조용히 저장될 수 있다. 코드 정황에서 한 추론이며 재현 테스트는 찾지 못했다.
- UpdateAsync가 PostTags를 로드(189행)한 뒤 SaveChangesAsync 전에 태그 삭제가 커밋되는 경우를 추론했다. 이때 RemoveAll이 이미 cascade로 사라진 PostTag를 지우려 하면 EF가 DbUpdateConcurrencyException을 던지고 StaleVersion 409가 나갈 수 있다. EF Core의 영향 행 검사 동작에 기댄 추론이며 코드나 테스트로 확인하지 못했다.
- 엔드포인트 밖으로 전파된 일반 DB 예외(57014/55P03 외)가 어떤 응답이 되는지는 코드로 끝까지 확인하지 못했다. Program.cs가 AddProblemDetails와 UseExceptionHandler()를 쓰므로 500 ProblemDetails로 보이지만, 프레임워크 기본 동작에 기댄 추론이다.
- SaveChangesAsync 실패로 409를 반환할 때 await using 트랜잭션이 해제되면서 롤백되어, 이번 요청이 INSERT한 태그도 사라진다고 보았다. 이는 EF Core/Npgsql 트랜잭션 Dispose 동작에 기댄 추론이며 이를 검증하는 테스트는 찾지 못했다.
- 입력 기능의 dependencies는 F001·F029뿐이었다. 그러나 /api/tags 요청은 모두 AdminSurfaceMiddleware(Program.cs 104행, F018)를 거치고, 오류는 UseExceptionHandler·OverloadExceptionHandler·UseStatusCodePages(Program.cs 43·100·101행, F020)가 처리한다. 이를 코드로 확인해 의존을 F001·F018·F020·F029로 보정했다.
- 검증 지적(F008_FLOW에 UpdateAsync 글 조회·404 노드가 없음)을 PostEndpoints.cs 189-190행으로 다시 확인했다. 코드는 검증 전에 SingleOrDefaultAsync로 글을 조회하고 null이면 NotFound를 반환한다. 다이어그램에는 LoadPost(Posts.Include PostTags SingleOrDefaultAsync)와 NotFound404(404 NotFound) 노드가 검증 노드 앞에 있다. nodes 표에도 두 노드와 코드 위치를 적었고, 요약 문장도 실제 다이어그램 구조에 맞게 고쳤다.

### F009 첨부 이미지 업로드·목록·삭제

- 이전 분석 입력이 diagrams 중간에서 잘려 이전 history 항목과 원래 F009_FLOW 본문을 볼 수 없었다. 현재 코드에서 동작 변경은 확인되지 않아 history에 새 항목을 추가하지 않았다. 검증이 지적한 과복잡(간선 42/40) 문제는 업로드용 F009_FLOW와 삭제용 F009_FLOW_DELETE로 나눠 해소했다.
- Linux에서 서빙 중인 파일을 삭제할 때의 동작은 코드 주석상 측정되지 않았다(Windows에서만 실측).
- UNLOCK 실패 시 잠금 해제가 늦어지는 원인이 Npgsql 내부 세션 리셋 지연이라는 설명은 코드 주석의 추론이다. Npgsql 내부는 확인하지 않았다.
- 운영 배포에서 Admin:UploadPerMinute·UploadConcurrency를 기본값(30·2)에서 바꾸는지는 이 세션에서 확인하지 않았다.
- ImageSignature.Detect와 MetadataStripper의 형식별 세부 파싱 규칙은 이번 세션에서 줄 단위로 다시 읽지 않았다(이전 분석과 코드 주석 기준).

### F010 첨부 이미지 공개 제공

- Linux(운영 컨테이너)에서 FileShare.Delete로 연 파일을 스트리밍하는 도중 TryDelete가 실행될 때의 동작은 측정하지 않았다. 코드 주석이 Windows에서만 실측했다고 밝힌다.
- Caddy 공개 사이트의 'encode zstd gzip'이 image/* 응답에 적용되어 Content-Length나 ETag에 영향을 주는지는 확인하지 못했다(Caddy 기본 MIME 매처에 의존).
- 응답 헤더 전송 뒤 본문 복사 중 I/O 오류가 나거나 클라이언트가 연결을 끊었을 때의 최종 상태와 로깅은 프레임워크 동작에 의존하며, 이 저장소에서 검증하지 않았다.
- If-Modified-Since, If-Match/If-Unmodified-Since(412) 같은 ETag 외 조건부 헤더의 처리는 FileStreamHttpResult의 프레임워크 동작으로 추정할 뿐, 테스트로 확인하지 않았다.
- 304 응답에 Cache-Control이 실리는지는 코드 순서상 그럴 것이라고 추론만 했다. 해당 테스트는 이를 단언하지 않는다.
- 500·503 응답의 CSP가 PublicCsp로 바뀌는지는 SecurityHeadersMiddleware 주석(예외 처리의 Response.Clear 후 OnStarting 재실행)에 근거한 추론이며, 첨부 경로에서 직접 측정하지 않았다.
- GUID 형식이 아닌 id 요청(엔드포인트 미매칭)이 asset-ip 창에 계산되지 않는지는 Matches 코드로 추론했을 뿐 테스트로 확인하지 않았다.

### F011 마크다운 렌더링·렌더 결과 캐시

- 배포 설정(appsettings*.json, deploy/, .env*, docker-compose)에서 'Rendering' 섹션을 찾지 못했다. 참조하는 곳은 Program.cs 59행의 바인딩과 StartupValidation 103-107행뿐이다. 운영에서 기본값(Concurrency=2, QueueTimeoutMs=5000, CacheMegabytes=64)을 그대로 쓰는지, 환경 변수로 덮어쓰는지는 확인하지 못했다.
- 주석에 적힌 성능 실측값(인라인 파서 약 8,486ms, 블록 파서 약 6,567ms, ColorCode 초기화 약 185ms, 캐시 항목 오버헤드 약 290바이트, SizeLimit 초과 시 Set이 조용히 거부된다는 관찰 등)은 코드로 재현·검증하지 않았다.
- RenderedPostCache에는 글을 삭제할 때 항목을 명시적으로 무효화하는 경로가 없다(PostEndpoints.DeleteAsync는 cache를 받지 않는다). 따라서 삭제된 글의 항목은 만료(최대 24시간)나 압축 전까지 메모리에 남는다. 다만 메타 조회가 404를 내므로 노출되지는 않는다(PostModel 흐름에서 추론).
- 이번 검증 지적 6건은 모두 색인 문서의 '기능 간 의존' 표에서 F006·F008·F002·F007·F018 행에 관한 것이며, F011 자체 분석의 범위 밖이다. 이 가운데 F011과 관련된 사실만 코드로 다시 확인했다. (1) F006은 F011을 쓴다: PostEndpoints가 RenderGate와 RenderedPostCache.Store를 사용한다(PostEndpoints.cs 124·162·187·243행). 이는 F006→F011 방향의 역의존이므로 F011의 dependencies에는 넣지 않는다. (2) 지적 가운데 'F011 행이 의존 표에 없다'는 내용은 색인 쪽 문제다. F011 자체의 의존은 코드로 다시 확인해 두 가지로 유지한다. F020: Program.cs 43·100행의 OverloadExceptionHandler가 RenderBusyException을 503으로 바꾼다. F021: StartupValidation.cs 103-107행이 Rendering 옵션을 검증한다.

### F012 공개 홈(최신 글 목록)

- `/`에서 과부하가 아닌 예외(DB 연결 실패, 공개 롤 권한 부족 등)가 났을 때 500 응답의 본문 형식. `AddProblemDetails()`가 등록돼 있고 `UseExceptionHandler()`가 `UseStatusCodePages`보다 바깥이라 고정 HTML이 아니라 ProblemDetails일 수 있지만, 실제 결과는 확인하지 못했습니다.
- WebApplication이 암묵적으로 추가하는 라우팅(UseRouting)의 파이프라인 내 정확한 위치. Program.cs에는 `UseRouting` 호출이 없고, `RateLimitingExtensions.Matches`가 `ctx.GetEndpoint()`에 의존하므로 속도 제한보다 앞에서 매칭된다고 추론만 했습니다.
- `OperationCanceledException`(클라이언트 연결 중단)이 났을 때의 최종 응답 코드와 로그 수준. 프레임워크 동작에 맡겨져 있습니다.
- COUNT(*)와 OFFSET 쿼리의 실제 실행 계획. `(CreatedAt DESC, Id)` 인덱스를 실제로 쓰는지, 행 수가 커질 때 비용이 어떻게 되는지 측정 근거를 찾지 못했습니다.
- 운영 환경에 설정된 `Public:PagePerIpPerMinute`·`Public:StatementTimeoutMs` 값. 코드 기본값(PagePerIpPerMinute=120)만 확인했고 설정 파일 값은 확인하지 않았습니다.

### F013 공개 글 상세 보기

- /posts/{slug} 경로에서 처리되지 않은 예외(23505 같은 기타 DB 오류, 연결 실패)는 500이 되며, 이는 테스트로 확인된다. 그러나 500 응답 본문이 problem+json인지, 고정 HTML인지, 빈 본문인지는 확인하지 못했다. 해당 테스트는 최소 파이프라인에서 상태 코드와 Retry-After만 검사한다.
- 클라이언트가 요청을 취소하면 OperationCanceledException이 난다. 이때의 최종 상태 코드와 로깅은 프레임워크 동작이라 확인하지 못했다.
- 운영 환경의 RenderingOptions(Concurrency, QueueTimeoutMs, CacheMegabytes)와 PublicOptions(PagePerIpPerMinute, StatementTimeoutMs) 실제 설정값은 확인하지 않았다. 코드 기본값만 확인했다.
- ExceptionHandlerMiddleware가 처리한 예외와 처리하지 않은 예외를 각각 어떤 로그 수준으로 남기는지는 실행 환경에서 확인하지 않았다.
- 검증 지적 중 'AuthServiceCollectionExtensions.cs도 ILogger를 쓴다'는 부분은 코드와 맞지 않는다. 이 파일에는 ILogger가 없고, 82행에서 NullLoggerFactory.Instance(로그를 버리는 팩터리)를 넘길 뿐이다. 그래서 logging 항목에는 'ILogger로 로그를 남기지 않는다'고 따로 적었다. AttachmentJanitor.cs 누락 지적은 맞아서 반영했다.

### F014 공개 글 검색

- 클라이언트 연결 끊김이나 요청 취소로 CountAsync·ToListAsync가 취소될 때의 최종 상태 코드와 로깅은 확인하지 못했다. 앱에는 취소 전용 처리가 없고, OverloadExceptionHandler 주석도 취소와 57014의 경합을 미검증으로 남겼다.
- 57014·55P03이 아닌 DB 예외(연결 실패, 인증 실패, 풀 고갈)의 응답 본문 형식은 확인하지 못했다. UseExceptionHandler가 UseStatusCodePages 바깥에 있어서 고정 HTML 오류 페이지가 붙는지 코드만으로 확정할 수 없다.
- 운영 DB에 Posts의 Title·Summary·ContentMarkdown ILIKE용 인덱스(pg_trgm 등)가 있는지는 확인하지 못했다. 저장소 코드와 마이그레이션 검색에서는 발견하지 못했다.
- 운영 환경의 Public:SearchPerIpPerMinute·SearchConcurrency·PagePerIpPerMinute·StatementTimeoutMs 실제 값은 확인하지 않았다. 코드 기본값만 기록했다.
- 운영에서 속도 제한 파티션 IP가 실제 방문자 IP인지(UseTrustedForwardedHeaders 동작)와 ClientIp.PartitionKey의 IPv6 /64 묶음은 주석으로만 확인했다.

### F015 공개 시리즈별 글 목록

- OverloadExceptionHandler가 처리하지 않는 일반 예외(DB 연결 실패 등)가 공개 HTML 경로에서 어떤 본문 형식으로 나가는지 알 수 없다. 후보는 ProblemDetails JSON 또는 빈 본문이다. UseExceptionHandler()가 경로/핸들러 없이 AddProblemDetails와 함께 등록되어 있어 코드만으로 최종 본문을 확정할 수 없다.
- Public:PagePerIpPerMinute의 운영 환경 실제 값은 확인하지 않았다(appsettings 값은 옮기지 않음). 코드 기본값은 120(PublicOptions.cs 18행)이다.
- 클라이언트 취소(OperationCanceledException) 시 응답 상태 코드·로그 형태는 프레임워크 기본 동작을 따르며, 이 저장소에서 확인하지 않았다.
- 시리즈 소속 글이 500건을 넘는 상황에 대한 테스트나 운영 요구가 있는지 코드·테스트에서 확인되지 않았다.

### F016 공개 태그별 글 목록

- 클라이언트 연결 종료로 CancellationToken이 취소될 때(OperationCanceledException) 최종 응답 상태와 로깅 동작은 코드에서 확인하지 못했다.
- 57014·55P03이 아닌 DB 예외(연결 실패, 공개 롤 권한 누락 등)가 공개 경로에서 날 때 응답 본문 형식(AddProblemDetails + UseExceptionHandler 조합의 ProblemDetails인지, ErrorResponses의 HTML인지)을 확인하지 못했다.
- 경로 세그먼트 안의 인코딩된 슬래시(%2F)가 {tag} 라우트 값으로 어떻게 디코딩·매칭되는지 확인하지 못했다. 태그 이름에는 CK_Tags_Name_NoSlash 제약으로 '/'가 없다.
- 공개 전용 DB 롤(ConnectionStrings:Public, PublicRoleGrants)을 설정했을 때 Tags/Posts/PostTags SELECT 권한이 이 조회를 모두 덮는지는 이 세션에서 PublicRoleGrants를 읽지 않아 확인하지 못했다.

### F017 피드·사이트맵·robots·코드 강조 CSS 제공

- 클라이언트가 요청을 취소해 ct가 취소될 때 발생하는 OperationCanceledException이 어떤 상태 코드와 로그로 처리되는지 확인하지 못했다. 이 기능 코드에는 해당 처리가 없다.
- HighlightCss.Build가 실제로 예외를 던질 수 있는 조건과, 예외가 캐시되는 상태(faulted Lazy)가 이 앱에서 재현되는지는 측정되지 않았다.
- 최대 규모(URL 30,001개)에서 sitemap 문서의 실제 크기, 메모리 사용량, 직렬화 시간은 측정되지 않았다. 코드 주석도 '수 MB 추정'이라고만 적고 있다.
- UseExceptionHandler 기본 경로가 내는 500 응답의 본문 형식은 확인하지 않았다. Program.cs가 AddProblemDetails를 등록하므로 ProblemDetails일 가능성이 있지만 실측하지 않았다.
- HEAD 요청에서 프레임워크가 본문 전송을 실제로 생략하는지는 측정되지 않았다(코드 주석에도 미측정이라고 적혀 있다).
- 관리 호스트로 /css/site.css를 요청했을 때 앱이 200을 주는지는 테스트로 확인되지 않았다. 코드상 호스트 조건이 없어서 200으로 추론했을 뿐이다.
- site.css 응답에 SecurityHeadersMiddleware의 헤더(CSP·nosniff 등)가 실제로 붙는지를 직접 검사하는 테스트는 찾지 못했다.
- 검증 지적 반영: 이전 분석은 /css/site.css(UseStaticFiles, wwwroot/css/site.css, max-age=3600)를 빠뜨렸다. 코드(Program.cs 102-103행)로 확인해 진입점·실행 순서·엣지 케이스·다이어그램에 추가했다. 이 기능 코드 자체가 바뀐 것은 아니어서 history 항목은 추가하지 않았다.

### F018 관리 표면 접근 통제(호스트·IP 허용 목록·CSRF 헤더·Origin)

- Caddy reverse_proxy가 외부 클라이언트가 보낸 X-Forwarded-For를 덮어쓰는지 덧붙이는지는 Caddyfile만으로 확인할 수 없다(기본 동작에 의존). 앱이 ForwardLimit=1로 맨 오른쪽 값만 보므로 판정 결과에는 영향이 없을 것으로 추론한다.
- Caddy의 ADMIN_DOMAIN과 api의 Site__AdminOrigin(ADMIN_ORIGIN)이 서로 일치하는지는 코드 어디에서도 검사하지 않는다. 운영 .env에서 두 값이 어긋나도 잡아낼 장치가 없다.
- 거부 이벤트를 앱 로그에 남기지 않는 것이 의도인지(Caddy 접근 로그로 충분하다고 본 것인지)는 코드 주석에 근거가 없다.
- 관리 도메인의 bare /api 요청이 SPA 폴백(index.html 200)으로 처리된다는 것은 Caddy 매처 규칙으로 추론한 것이다. 실측 테스트는 찾지 못했다.
- 검증 지적(F006 행 → F001·F003·F005·F007·F008·F011·F018·F020·F029, F008 행 → F001·F018·F020·F029)은 색인 문서 '기능 간 의존' 표의 수정 사항이라 F018 산출물 범위 밖이다. F018 쪽에서 코드로 다시 확인한 사실은 다음과 같다. 모든 /api 요청은 Program.cs 104행 UseMiddleware<AdminSurfaceMiddleware>를 거치고, 그 바깥에 100행 UseExceptionHandler가 있다. /api 그룹(ApiEndpoints.cs 39-45)에는 PostEndpoints(F003·F006의 PUT 포함)와 TagEndpoints(F008)가 들어 있다. 따라서 F006·F008이 F018에 의존한다는 지적은 코드와 맞다. F018 자신의 의존은 F025(Caddy 에지)와 F021(StartupValidation 설정 검증)로 유지했다. F020(SecurityHeadersMiddleware 등)은 F018 거부 응답을 감싸는 바깥 미들웨어일 뿐 F018 코드가 호출하지 않으므로 의존에 넣지 않았다.

### F019 요청 속도 제한

- 프레임워크 RateLimiter 미들웨어가 거부 시 남기는 로그가 있는지, 있다면 어떤 레벨인지(앱 코드에는 거부 로깅이 없다)
- IP 파티션 10초 유휴 정리, ConcurrencyLimiter 내부 락 같은 프레임워크 내부 동작은 코드 주석의 실측 기록에만 근거한다. 저장소 안에서 재현 코드를 확인할 수 없다.
- 429 응답 본문(ProblemDetails/HTML)이 실제로 UseStatusCodePages로 채워지는지 단언하는 테스트를 찾지 못했다(테스트는 상태 코드와 Retry-After만 확인한다)
- 운영 환경(appsettings·.env)이 기본값과 다른 한도를 쓰는지 여부. 스모크 오버레이의 로그인 한도 외에는 확인하지 않았다.
- 앞쪽 고정 창 허용량이 뒤쪽 고정 창 거부로 소모되는 방향(login-global 거부 → login-ip 소모, page-ip 거부 → search-ip 소모)의 실제 영향은 테스트로 검증되지 않았다
- 검증 지적(F001·F002·F003의 의존 목록과 기능 간 의존 표 불일치)은 다른 기능의 문서와 색인 표에 관한 것이라, 이 F019 분석 출력에서 고칠 대상이 없다. F019의 dependencies(F018·F020·F021)는 코드로 다시 확인했다. F018 근거는 AdminSurfaceMiddleware 순서(Program.cs 104-105)와 UseTrustedForwardedHeaders다. F020 근거는 UseStatusCodePages → ErrorResponses(Program.cs 101)다. F021 근거는 StartupValidation 한도 검증(62-78)이다.

### F020 보안 헤더·오류 응답·과부하 처리·API 본문 제한

- 과부하가 아닌 예외로 생긴 500의 본문 형식을 확인하지 못했다. 공개 HTML 경로에서도 ProblemDetails가 되는지는 UseExceptionHandler()와 AddProblemDetails 조합의 프레임워크 기본 동작에 달려 있다. 테스트는 /api/x 경로만 본다.
- ExceptionHandlerMiddleware가 IExceptionHandler가 처리한 예외(503)도 로그로 남기는지 확인하지 못했다.
- IProblemDetailsService.TryWriteAsync가 false를 반환하는 조건(Accept 헤더 협상)에서 오류 응답 본문이 비는지 실측 근거가 없다.
- Kestrel이 앱 도달 전에 거부한 응답에서 보안 헤더가 빠지는 범위는 코드 주석상 측정하지 않았다.
- 최소 API 바인딩 밖에서 LengthLimitedStream의 BadHttpRequestException이 전파될 때 최종 상태 코드가 413인지 500인지 확인하지 못했다.
- 57014가 statement_timeout 이외 원인(클라이언트 취소 경합)으로 나올 수 있는지는 주석에 '미검증'으로 남아 있다.
- MachinePrefixes 경로의 HEAD 요청에서 ProblemDetails 본문이 어떻게 처리되는지는 프레임워크에 맡겨져 있어 확인하지 못했다.
- 익명 POST /api/auth/login의 256KB 초과 본문이 413을 받는다는 점은 코드 순서(AuthEndpoints.cs 48행 AllowAnonymous, Program.cs 104-108, ApiBodyLimitMiddleware.cs 40-51)로 확인했다. 다만 이 경우를 직접 검사하는 테스트는 ApiBodyLimitTests에 없다(테스트 6건 중 해당 없음).
- 검증 지적(기능 간 의존 표의 F006 행이 F003만, F008 행이 F001·F029만 싣고 있어 각 기능 문서와 어긋난다는 점, 그리고 F002·F007·F011·F018 행의 같은 종류 불일치)은 색인 표와 다른 기능 문서의 문제라 이 세션(F020) 출력으로는 고칠 수 없다. 코드로 보면 지적의 방향은 맞다. 모든 /api 요청은 Program.cs 104행의 AdminSurfaceMiddleware(F018)를 거치고, Program.cs 100-101행의 UseExceptionHandler·UseStatusCodePages(F020)에 감싸인다. 그래서 F006·F008→F020 의존은 성립한다. 다만 한 가지 보정이 필요하다. TagEndpoints의 목록·삭제는 AppDbContext를 쓰는데, 관리 풀에는 statement_timeout도 lock_timeout도 없다(PublicAttachmentEndpoints.cs 74행 주석, statement_timeout 설정은 PublicDbContext.cs 59행과 DataServiceCollectionExtensions.cs 34행의 공개 연결에만 있음). 그래서 태그 엔드포인트에서 57014/55P03→503 경로는 현실적으로 생기지 않는다. F008의 F020 의존은 과부하 매핑이 아니라 StatusCodePages→ErrorResponses(404 등 ProblemDetails 본문)와 파이프라인 위치에 근거한다. F006은 PostEndpoints 저장 경로의 RenderBusyException(RenderGate)→503 매핑으로 F020에 의존한다(PostEndpoints.cs 314행). 이 보정은 F020 자신의 dependencies(F001·F009·F010·F011·F018·F021)를 바꾸지 않는다. F019는 본문 없는 429를 ErrorResponses에 맡기는 F019→F020 방향의 사용 관계라 F020 의존에 넣지 않았다.
- 작업 트리의 TagEndpoints.cs 5행 변경은 '// doc-harness incremental check (temporary, reverted after the test)' 주석 한 줄이다. F020 동작에 영향이 없어 history에 넣지 않았다.

### F021 앱 기동 부트스트랩(설정 검증·마이그레이션·공개 DB 롤 권한)

- Migrate()와 PublicRoleGrants.Apply가 관리 연결의 Command Timeout(compose 30초)을 그대로 따르는지, 긴 마이그레이션이 이 시간을 넘으면 어떻게 되는지 확인하지 못했다.
- DB에 공개 롤(blog_public)이 없을 때 Apply의 REVOKE가 내는 구체적 SqlState(예: 42704)를 코드나 테스트로 확인하지 못했다(PostgreSQL 동작에서 추론).
- 기동 실패 시 처리되지 않은 예외가 어떤 형식으로 로그에 남는지 확인하지 못했다(appsettings의 Logging 설정, EF Core 마이그레이션 로그 수준).
- 설정 오류로 기동 실패가 반복될 때 compose restart: unless-stopped가 백오프 말고 어떻게 동작하는지, 운영 절차가 이 상황을 어떻게 다루는지 이 세션에서 확인하지 않았다.
- 여러 인스턴스가 동시에 기동할 때의 EF 마이그레이션 잠금과 권한 적용 경합은 코드가 다루지 않으며, 실제 동작도 확인하지 못했다(단일 인스턴스 전제).
- 이번 검증 지적(F006 행을 F001·F003·F005·F007·F008·F011·F018·F020·F029로, F008 행을 F001·F018·F020·F029로 보정; F002·F007·F011·F018 행의 의존 표와 기능 문서 요약 불일치)은 모두 다른 기능의 의존 표 행에 관한 것이다. F021의 코드나 dependencies와는 관계가 없다. 기능 단위 세션인 F021 분석은 다른 기능 행을 고칠 수 없으므로 해당 보정은 의존 표 생성 단계나 그 기능들의 분석에서 반영해야 한다. 참고로 지적 내용 자체는 코드와 맞는다. Program.cs 104행 UseMiddleware<AdminSurfaceMiddleware>(F018), 43행 AddExceptionHandler<OverloadExceptionHandler>, 100행 UseExceptionHandler, 101행 UseStatusCodePages(ErrorResponses.HandleStatusCodeAsync)(F020)가 모든 /api 요청 경로에 걸려 있다.
- F021의 dependencies는 코드로 실제 호출·사용을 확인한 기능만 유지했다. F009는 FileSystemAttachmentStore 생성자·EnsureRootIsWritable(Program.cs 79행, StartupValidation.cs 114행)이다. F011은 MarkdownRenderer.Render 워밍업(Program.cs 91행)이다. F018은 SiteOptions.HostOf·CidrList.Parse·Admin/Proxy 옵션(StartupValidation.cs 48-61행)이다. F026은 compose 환경 변수와 10-roles.sh가 롤·연결 문자열을 제공하는 것이다. F001(DataProtectionKeysPathKey 상수·PasswordHash)과 F019/F020(PublicOptions 한도·StatementTimeoutMs)의 설정도 검증하지만, 해당 기능 코드를 호출하지는 않으므로 의존으로 넣지 않았다.

### F022 헬스체크

- 포트 문자열이 비정상일 때(예: ASPNETCORE_HTTP_PORTS=abc) new HttpRequestMessage·SendAsync가 실제로 어떤 예외를 던지는지, 그 예외가 catch 필터에 잡히는지는 실행해서 확인하지 않았다.
- unhealthy가 된 api 컨테이너를 재시작하는 별도 장치(autoheal 등)는 저장소에서 찾지 못했다. deploy/OPERATIONS.md 153행은 unhealthy/재시작 반복 증상의 확인 명령(logs, docker inspect .State.Health)만 안내한다. unhealthy일 때 자동 조치가 있는지는 확인하지 못했다.
- starting→unhealthy 전이 시각(start_period 이후 30s 간격 검사 3회)과 caddy가 depends_on 실패로 기동하지 않는 동작은 Docker 엔진·Compose의 규칙에서 추론했다. 저장소 안의 테스트나 스모크로는 검증하지 않는다.
- 관리 도메인에서 허용 IP로 /health를 요청했을 때 실제로 SPA index.html(200)이 나오는지는 Caddyfile 구조로 추론했을 뿐이고, 스모크 테스트는 이를 검증하지 않는다.
- HostFiltering 구성을 기능 색인의 F018과 F020 중 어느 기능이 소유하는지는 코드만으로 판단할 수 없어 dependencies에 F018을 넣지 않았다.
- HEAD /health 요청의 처리는 확인하지 않았다. MapGet은 GET만 매칭하고, Caddy 공개 블록은 HEAD도 통과시킨다.

### F023 관리자 비밀번호 해시 생성 CLI

- `docker run`을 -i 없이 실행해 표준 입력이 연결되지 않았을 때 `Console.IsInputRedirected` 값과 이후 동작(ReadLine null로 종료 코드 1인지, ReadKey 예외인지)은 코드만으로 확정할 수 없다.
- TTY가 없는데 `IsInputRedirected`가 false로 판정되는 환경에서 `Console.ReadKey`가 던지는 예외의 정확한 종류와 종료 코드는 이 저장소에서 측정 근거를 찾지 못했다.
- 해시 알고리즘(PBKDF2-HMAC-SHA512, 10만 회)과 출력 길이의 근거는 코드 주석과 프레임워크 기본값뿐이다. 코드가 `PasswordHasherOptions`를 명시적으로 설정하지 않으므로 실제 값은 대상 .NET 버전의 기본값을 따른다.
- 대화형 CLI 경로를 직접 검증하는 자동 테스트는 없다. 테스트는 interactive: false인 파이프 경로만 다룬다.

### F024 고아 첨부 파일 정리(백그라운드)

- 55P03 잠금 대기 초과 분기가 실제로 어떻게 동작하는지는 테스트로 확인되지 않았다(코드 주석이 미검증이라고 밝힌다).
- UNLOCK 실패 시 advisory lock이 늦게 풀린다는 설명은 개발 PC에서의 측정과 Npgsql 내부에 대한 추론에 근거한다. 운영 Linux 컨테이너에서의 동작은 확인하지 못했다.
- OpenConnectionAsync가 실패할 때 EF Core·Npgsql이 부분적으로 열린 연결을 내부에서 정리하는지는 라이브러리 내부 동작이라 코드로 확인할 수 없다. HoldAsync 자체는 이 경우 CloseConnectionAsync를 호출하지 않는다.
- 운영 환경에서 '파일이 없는 첨부 행' 경고나 '첨부 청소 실패' LogError를 모니터링하거나 알림으로 보내는지는 코드에서 확인할 수 없다.
- 운영 볼륨의 파일 수와 스윕 1회 소요 시간(파일마다 동기 I/O와 DB 조회 1회)에 대한 측정 근거가 없다.
- Host의 BackgroundServiceExceptionBehavior 설정은 코드에서 찾지 못했다(기본값으로 추정). 취소가 원인이 아닌 OperationCanceledException이 ExecuteAsync 밖으로 나갈 때 호스트가 어떻게 반응하는지는 확인하지 못했다.
- 운영 컨테이너에서 파일 mtime을 기록하는 시계와 TimeProvider.System 사이에 차이가 있는지(볼륨 마운트 등)는 확인할 수 없다.

### F025 에지 프록시·TLS·관리 SPA 정적 서빙

- ACME 발급이 실패했을 때 Caddy가 재시도하는 간격·횟수와 실제 발급자(Let's Encrypt/ZeroSSL 등)는 Caddyfile에 명시돼 있지 않다. Caddy 기본값에 의존하므로 저장소 코드로는 확인할 수 없다.
- 관리 도메인의 ACME HTTP-01 챌린지가 @denied IP 게이트보다 먼저 처리되는지는 확인하지 못했다. 근거는 Caddyfile 주석(66-67행)과 운영 문서의 확인 절차(deploy/OPERATIONS.md 51행)뿐이다. 스모크는 *.localhost 내부 CA를 쓰므로 이 경로를 검증하지 않는다.
- reverse_proxy에는 헬스체크·재시도(lb_try_duration 등)·타임아웃 옵션이 없다. 업스트림 응답이 지연될 때의 동작은 Caddy 기본값을 따르며, 구체적인 값은 확인하지 못했다.
- IPv6 클라이언트에 대한 remote_ip 판정은 확인되지 않았다. deploy/.env.example 12행의 예시 값에 2001:db8::/64가 있어 IPv6 사용을 의도한 것으로 보이지만, compose 네트워크는 IPv4 서브넷만 정의한다. 실제 IPv6 경로에서의 동작은 검증되지 않았다.
- encode zstd gzip이 공개 첨부 이미지 등 어떤 Content-Type까지 압축하는지는 Caddy 기본 매처에 달려 있어 확인하지 못했다.
- 검증에서 지적된 항목(F002·F006·F007·F008·F011·F018의 의존 표 행 불일치)은 모두 다른 기능의 색인 행에 관한 것이다. F025의 dependencies(F018·F020·F026·F029·F009·F010)는 이 세션에서 코드로 다시 확인했고 그대로 둔다.

### F026 Docker Compose 운영 배포 구성

- 필요한 Docker Engine/Compose 최소 버전이 저장소에 명시돼 있지 않다. healthcheck start_interval 같은 신규 옵션을 지원하는지도 확인할 수 없다.
- 입력 진입점처럼 `-f deploy/docker-compose.yml`로 다른 작업 디렉터리에서 실행할 때 compose가 .env를 어디서 찾는지 확인하지 못했다. OPERATIONS.md는 deploy/ 안에서 실행하라고만 한다.
- 10-roles.sh가 실패했을 때 postgres 공식 이미지가 컨테이너를 어떻게 종료·재시도하는지, 부분 초기화된 pgdata가 남는지는 이미지 동작이라 코드로 확인할 수 없다.
- postgres 서비스의 cap_add(CHOWN·DAC_OVERRIDE·FOWNER·SETGID·SETUID)가 이미지 엔트리포인트의 어떤 동작에 필요한지 저장소 안에 근거가 없다.
- 운영 환경의 Docker 데몬이 원본 IP를 보존하는지(userland-proxy 사용 여부)는 호스트 설정 문제라 확인할 수 없다.
- 연결 문자열 파싱이 실패할 때 Npgsql ArgumentException 메시지에 연결 문자열 값(비밀번호 포함 가능)이 들어가는지 저장소 코드로 확인할 수 없다.
- api 이미지의 /app이 --chown 없이 복사되어 root 소유가 되는 것은 Docker COPY 기본 동작에 근거한 추론이다. 실제 이미지의 소유권은 확인하지 못했다.

### F027 DB·첨부 백업과 복원

- pg_dump·pg_restore가 postgres 컨테이너 안에서 비밀번호 없이 -U postgres로 접속되는 근거(로컬 소켓 trust 등 공식 이미지 기본 pg_hba 설정)는 저장소 코드로 직접 확인하지 못했다.
- pg_restore -l만으로 데이터 영역이 잘린 덤프를 검출할 수 있는지는 확인하지 못했다.
- 실행 중인 DB와 채워진 볼륨 위로 덮어쓰는 복원(특히 더 새로운 마이그레이션이 적용된 DB로 복원)의 실제 결과는 자동 검증이 없어 확인하지 못했다.
- docker compose up --wait에 --wait-timeout이 없을 때 compose 버전별 최대 대기 동작은 확인하지 못했다(healthcheck 설정에 묶인다고만 판단).
- 백업 파일의 오프사이트 복사·보관 기간 정리는 스크립트 밖이라 실제 운영에서 수행되는지 알 수 없다.

### F028 배포 스모크 검증

- SMOKE_E2E=1일 때 실제 브라우저 요청의 remote_ip가 모든 호스트 환경(Linux 러너, Windows의 Docker Desktop)에서 172.30.1.1인지는 코드로 확인할 수 없다. ci.yml 주석도 러너에서 허용 IP가 다르게 나올 수 있는 '첫 Linux 실행' 실패 모드를 언급한다.
- smoke.test.mjs가 /web/admin-headers.ts(.ts)를 import할 수 있는 근거가 node:24 이미지의 기본 타입 제거 기능인지는 저장소 코드만으로 확정할 수 없다.
- e2e/admin.spec.ts는 파일이 있는 것만 확인했다. 본문 시나리오의 세부 단언은 이번 세션에서 읽지 않았다.
- Caddyfile의 각 route·handle_errors 블록이 스모크 단언과 줄 단위로 일치하는지는 F025 범위라 전부 대조하지 않았다(remote_ip·request_body·handle_errors·@nonread 위치만 확인).

### F029 관리 SPA 셸(라우팅·API 클라이언트·오류/없는 화면)

- redirect:'error'로 fetch가 거부될 때 브라우저가 던지는 예외 이름은 코드로 확인할 수 없다(TypeError로 추정). AbortError가 아니면 ApiError(0)로 바뀐다는 것만 확인했다.
- hydrateFallbackElement(Loading)가 비SSR createBrowserRouter에서 첫 진입 lazy 라우트를 로딩하는 동안 실제로 표시되는지는 확인하지 못했다. react-router 8.4.0 내부 동작에 달려 있다.
- auth.me 자체가 401로 실패할 때, QueryCache onError의 setQueryData가 오류 상태를 성공 상태({authenticated:false})로 덮어써 곧바로 로그인 화면으로 이동하는지는 코드로 직접 확인하지 못했다. TanStack Query 내부 실행 순서에 달려 있다.
- RouteError가 lazy 청크 실패 외에 어떤 오류까지 받는지는 확인하지 못했다. 테스트는 lazy 실패 한 가지만 다룬다.
- StrictMode 개발 모드에서 useState 초기화 함수가 두 번 호출된다는 근거는 저장소 밖의 React 문서다. 그래서 해당 엣지 케이스는 INFERRED로 두었다.
- 의존 보정 근거: Program.cs 100-108행에서 모든 /api 요청이 UseExceptionHandler(OverloadExceptionHandler)·UseStatusCodePages(ErrorResponses)·AdminSurfaceMiddleware·ApiBodyLimitMiddleware를 거친다. client.ts가 CSRF 헤더를 맞추고 describeError가 403·413·503(Retry-After)를 문구로 바꾸므로 F018·F020을 의존에 넣었다. F001(RequireAuth·auth 엔드포인트)과 F025(Caddy SPA 폴백)는 유지했다. F019 속도 제한은 RateLimitMetadata가 login·preview·upload 엔드포인트에만 붙어 있어 셸 공통 의존이 아니라고 보고 뺐다(429 문구 처리는 응답 소비일 뿐이다).
- 검증 지적 가운데 F006·F008·F002·F007·F011·F018 행의 의존 표 불일치는 다른 기능의 문서와 색인 표에 관한 것이다. F029 분석 범위 밖이라 여기서는 고치지 않았다. F029 자신의 의존 목록만 같은 기준(/api 공통 파이프라인 F018·F020 포함)으로 맞췄다.

### 데이터 모델

- Attachment와 Post 사이의 참조는 FK가 아니라 마크다운 본문 URL이다. 고아 판정 기준의 세부(AttachmentJanitor)는 이 분석에서 다시 읽지 않았다.
- InitialCreate 마이그레이션 본문 전체와 AppDbContextModelSnapshot은 이번 재검증에서 줄 단위로 대조하지 않았다(AppDbContext 모델 정의 기준으로 기술).
- Public 롤의 실제 DB 권한 상태는 런타임 DB에 의존하므로 코드만으로 확인할 수 없다(테스트 PublicRoleGrantsTests가 검증하는 것으로 추정).

### API

- Index·Series·Tag 페이지의 page 쿼리 검증과 404 조건 세부(Index.cshtml.cs, Series.cshtml.cs, Tag.cshtml.cs 본문)는 이 세션에서 열람하지 않았다.
- ApiBodyLimitMiddleware의 /api 본문 한도 초과 응답 코드는 열람하지 않아 오류 열에 넣지 않았다.
- AttachmentJanitor의 정리 주기와 조건은 열람하지 않았다.
- /css/site.css 이외 정적 파일 존재 여부는 wwwroot를 열람하지 않아 확인하지 못했다(Program.cs 주석은 site.css 하나뿐이라 함).

### 실패 이력

- Codex 2차 교차 검증에서 제기된 PARA 백엔드 계획 결함은 해당 코드가 삭제되어 현재 코드와 대조하지 못했다.
- 2A 계획 결함 16건과 2B 결함 23건의 개별 원인은 diff가 잘려 확인하지 못했다.
- 로컬 Windows Docker Desktop의 간헐 15초 연결 타임아웃 원인은 미확인이다.
- doc-harness F003·F011이 2회 시도된 구체적 실패 원인은 로그가 없어 알 수 없다.
- plan/doc_harness_0923.md 7절이 기록한 doc-harness 실전 결함 12건(특히 12번: 수정 루프 회차 이력이 resume 시 복원되지 않아 LLM 재검증 비용 반복)은 하네스 자체(doc-harness/)의 문제이고 분석 대상이 아니다. 이번 델타에는 해당 코드 변경이나 커밋이 없어 새 실패로 기록하지 않는다. 기존 FAIL012와의 관계도 확인되지 않았다.
- TagEndpoints.cs의 추가 한 줄은 주석뿐이고 임시 점검용이라고 적혀 있다. 되돌려졌는지는 이 입력으로 확인할 수 없다.

### 오류 처리

- 공개 페이지에서 기본 500 응답 본문이 고정 HTML인지 ProblemDetails인지 UNKNOWN
- OperationCanceledException의 최종 상태 코드와 로그 수준 UNKNOWN
- 헤더 전송 뒤 첨부 파일 읽기 오류 시 연결 동작 UNKNOWN
- EF가 cascade로 사라진 PostTag 삭제를 DbUpdateConcurrencyException으로 보고하는지 재현되지 않음
- 전역 로깅 구성과 알림 체계는 이 분석 범위에서 확인하지 않음

### 보안

- ClientIp와 ForwardedHeaders가 Caddy 뒤에서 실제 원본 IP를 올바르게 산출하는지는 이번 분석에서 코드를 직접 읽지 않아 확인하지 못했다.
- HtmlAllowlist·UrlPolicy의 정제 규칙 세부(허용 태그·속성·스킴)는 파일을 직접 읽어 검증하지 않았다.
- Caddyfile과 배포 헤더는 입력 목록에 없어 에지 측 방어를 확인하지 못했다.
- 500 응답 본문에 예외 세부가 노출되는지는 확인하지 못했다.

### 성능

- 실제 데이터 규모(글 수·본문 크기)와 DB 인덱스 사용 여부는 코드만으로 측정하지 못했다(EXPLAIN 미실행).
- Caddy가 피드·사이트맵·첨부 응답을 캐시하는지와 첨부 200 응답의 Cache-Control 헤더 설정은 확인하지 못했다.
- DB 연결 풀 크기(Maximum Pool Size)와 Rendering:Concurrency 운영값이 실제 동시 부하에 충분한지 알 수 없다.
- RenderGate.cs와 PublicAttachmentEndpoints.cs는 직접 읽지 않고 기능 분석 결과에 의존했다.

### 기술 부채

- 500 응답 본문이 공개 HTML 경로에서 고정 HTML인지 ProblemDetails인지
- 요청 취소(OperationCanceledException)가 최종적으로 어떤 상태 코드·로그가 되는지
- 응답 헤더 전송 후 파일 읽기 I/O 오류의 동작
- 클라이언트 validation.ts와 서버 TextRules·SlugRules의 한도가 실제로 일치하는지(이번 분석에서 파일 본문을 직접 열지 않음)
- 테스트 프로젝트의 커버리지 전체 범위(입력에는 일부 테스트 마커만 있음)
<!-- /doc-harness:section -->

<!-- doc-harness:section id="potential" hash="58f64ad1925983ba0e1cf517d2a9644ad930c387474521193e31d6e9d459c9a4" -->
## 기능 분석이 표시한 잠재 문제(POTENTIAL_ISSUE)

- F001: AuthEndpoints.LoginAsync의 AdminStates SingleAsync — AdminState 행이 없거나 여러 개, DB 연결 실패, 요청 취소 (처리 없음(예외 전파). UseExceptionHandler가 예외를 받는다. OverloadExceptionHandler.IsOverload는 InnerException 체인에 있는 PostgresException(57014·55P03)만 503 + Retry-After 5로 바꾼다. 그 밖의 예외(행이 없을 때의 InvalidOperationException, 연결 실패 등)는 기본 예외 처리로 넘어간다. 행이 없어질 위험은 CHECK 제약과 HasData 시드로 줄여 두었다.)
- F001: SessionValidator.ValidateAsync의 AdminStates SingleAsync — DB 장애나 행이 없는 상태에서 쿠키가 붙은 요청이 들어옴(/me 포함) (처리 없음(예외 전파). 쿠키가 붙은 관리 요청은 모두 UseAuthentication 단계에서 실패하고, UseExceptionHandler가 오류 응답을 만든다. 57014·55P03만 503이 된다. /me는 원래 항상 200을 돌려주는 계약이지만 이 경우에는 오류 응답이 나갈 수 있다.)
- F001: AuthEndpoints.LogoutAsync — ExecuteUpdateAsync가 0행을 갱신함(AdminState 행이 없음) (갱신된 행 수를 확인하지 않는다. epoch가 오르지 않아 다른 세션은 폐기되지 않았는데도, 호출한 브라우저의 쿠키만 지우고 204를 돌려준다.)
- F001: AuthEndpoints.LogoutAsync — epoch UPDATE 중 DB 예외 또는 요청 취소 (처리 없음(예외 전파). SignOutAsync까지 가지 못해 쿠키가 남는다. 57014·55P03만 503이 되고 나머지는 기본 예외 처리 응답이다. SPA의 Layout은 ErrorNotice를 보여 준다. UPDATE와 쿠키 삭제는 한 단위로 묶여 있지 않다.)
- F001: Layout.logout(SPA) — 세션이 이미 만료되었거나 폐기된 상태에서 로그아웃을 눌러 401을 받음 (MutationCache.onError의 noteAuthFailure가 ME_KEY를 false로 바꿔 로그인 화면으로 보낸다. 하지만 onSuccess가 실행되지 않으므로 removeQueries와 getMutationCache().clear()도 실행되지 않는다. 앞 사용자의 쿼리 캐시와 mutation 기록이 그대로 남는다.)
- F002: PostEndpoints.ListAsync (CountAsync / PostQueries.ListAsync) — DB 연결 실패·타임아웃 등 DB 예외 (핸들러는 처리하지 않고(예외 전파) 전역 UseExceptionHandler로 넘긴다. 예외 체인에 PostgresException 57014·55P03이 있으면 OverloadExceptionHandler가 503과 Retry-After를 쓴다. 그 밖의 예외는 AddProblemDetails 기반의 기본 500 응답이 된다(프레임워크 기본 동작에서 추론).)
- F002: PostEndpoints.DeleteAsync (SaveChangesAsync) — 동시성 외의 DbUpdateException 또는 DB 연결 예외 (처리 없음(예외 전파). 전역 예외 처리기가 받아, 체인에 57014·55P03이 있으면 503+Retry-After, 없으면 기본 500을 낸다. 명시적 트랜잭션이 없으므로 SaveChangesAsync 내부 트랜잭션이 롤백되는 것으로 추론한다(EF 기본 동작).)
- F003: PostEndpoints.CreateAsync / UpdateAsync SaveChangesAsync — 23505·23503·동시성 충돌이 아닌 DbUpdateException(예: CHECK 위반 23514) (처리 없음(예외 전파). InnerException 체인에 57014·55P03이 있으면 OverloadExceptionHandler가 503으로 바꾸고, 그 밖에는 기본 예외 처리로 넘어간다.)
- F003: PostEndpoints.CreateAsync / UpdateAsync (커밋 이후) — tx.CommitAsync 뒤 PostQueries.GetDetailAsync 재조회가 예외를 던지거나(연결 끊김·ct 취소) null을 반환함 (처리 없음. 행은 이미 저장·공개됐는데 클라이언트는 실패 응답이나 빈 본문을 받을 수 있다. 생성의 경우 다시 저장하면 slug 사전 검사에서 409가 난다.)
- F003: Editor.save.onError / 렌더 — 서버 400의 필드 키가 version이거나, 화면에 FieldError 자리가 없는 키임 (parseFieldErrors가 키를 그대로 옮기므로 오류는 fieldErrors.version에 들어간다. 그런데 400이면 ErrorNotice가 숨겨지고 version용 FieldError도 없어 아무 안내가 보이지 않는다. 다만 수정 화면의 baseline.version은 항상 서버값이라 실제로 일어날 가능성은 낮다.)
- F004: RenderGate.RenderAsync (렌더 실행 중) — 적대적 입력 때문에 Markdig 파서가 초선형 시간을 씀(문서상 최대 약 8.5초) (렌더 자체는 취소할 수 없는 동기 CPU 작업이고 타임아웃도 없다. 그동안 슬롯과 요청 스레드를 점유한다. 방어 수단은 동시성 제한(미리보기 2, 전역 게이트)과 분당 한도뿐이다.)
- F004: previewCsp (previewDoc.ts) — window.location.origin이 ORIGIN_PATTERN에 맞지 않음. 허용 형태는 http/https, 영숫자·점·하이픈 호스트 또는 IPv6 리터럴, 선택 포트다 (Error를 던진다. PreviewPane의 useMemo 안에서 호출되므로 렌더 중 예외가 된다. 컴포넌트에 별도 catch가 없어 React 오류 경계로 전파된다.)
- F005: drafts.ts clearDraft — removeItem 예외 (예외를 삼키고 임시본을 그대로 둔다. 새 글 생성 뒤 'new' 임시본이 남으면, 그것을 복원해 다시 저장할 때 중복 생성(slug 409)으로 이어질 수 있다. 코드 주석도 이를 '미검증 잔여 위험'으로 인정한다.)
- F005: drafts.ts loadDraft/saveDraft/clearDraft의 기본 인자 `storage = window.localStorage` — `window.localStorage` 속성 접근 자체가 예외를 던지는 환경(저장소 접근이 차단된 브라우저 설정 등) (처리 없음(예외 전파). 기본 인자는 try 블록 밖에서 평가된다. 그래서 `Editor`의 useState 초기화에서 부르는 loadDraft가 예외를 던지면 렌더가 실패하고, 루트 errorElement(RouteError)로 갈 것으로 추론한다. 재현하지는 않았다.)
- F006: TagResolver.ResolveIdsAsync (UpdateAsync 213행) — 태그 SELECT·INSERT 중 PostgresException(타임아웃 57014, 잠금 대기 55P03, 그 밖의 DB 오류) (이 호출은 try 블록(226행)보다 앞에 있어 엔드포인트의 catch 두 개 어디에도 걸리지 않는다. 57014·55P03은 OverloadExceptionHandler가 503으로 바꾼다. 그 밖의 오류는 UseExceptionHandler의 일반 처리로 넘어간다(엔드포인트 차원의 처리는 없고 예외가 전파된다). 트랜잭션은 dispose되면서 롤백된다.)
- F006: Editor.save.onError — 409의 원인이 version 불일치가 아니라 제약 경쟁(참조 삭제)인 경우 (status만 보고 똑같이 최신본을 다시 조회해 ConflictPanel('다른 곳에서 이 글이 먼저 수정되었습니다')을 띄운다. conflict가 있으면 ErrorNotice가 숨겨지므로 서버의 detail 문구는 화면에 나오지 않는다. '내 내용 유지' 뒤 다시 저장하면 같은 FK 원인으로 또 실패할 수 있다.)
- F007: SeriesEndpoints.CreateAsync / UpdateAsync / DeleteAsync / ListAsync / GetAsync — 유니크·FK·동시성·과부하가 아닌 기타 DbUpdateException, 23503이 아닌 PostgresException, DB 연결 오류 (처리 없음(예외 전파). UseExceptionHandler와 AddProblemDetails가 최종 처리하며, 과부하가 아니면 500 ProblemDetails가 될 것으로 본다. DeleteAsync에서 커밋 전에 난 예외는 await using tx dispose로 롤백된다.)
- F008: PostEndpoints.UpdateAsync (RemoveAll → SaveChangesAsync) — UpdateAsync가 트랜잭션 밖에서 post.PostTags를 로드한 뒤(189행) SaveChanges 전에 DELETE /api/tags가 그 태그를 지우고 커밋한 경우다. cascade가 링크를 먼저 지우므로, 요청의 wanted에 옛 태그 Id가 없으면 RemoveAll(215행)이 이미 사라진 PostTag 행을 DELETE 대상으로 표시한다. (EF가 영향 행 0을 DbUpdateConcurrencyException으로 보고하면 230-233행 catch가 StaleVersion()(409 '다른 곳에서 이 글이 먼저 수정되었습니다')을 반환한다. 그러나 글 Version은 실제로 바뀌지 않았으므로 안내 문구가 원인과 다르다. EF 동작에 기댄 추론이며 재현 테스트는 없다.)
- F008: TagResolver.ResolveIdsAsync — 기존 태그 조회(existing)와 최종 Id 조회 사이에 다른 요청이 그 태그를 삭제하고 커밋함 (따로 처리하지 않는다. 최종 SELECT가 그 태그를 찾지 못하면 반환 목록에서 빠지고, 글은 오류 없이 그 태그 없이 저장될 수 있다. 코드 정황에서 한 추론이며 재현 테스트는 없다.)
- F009: AttachmentEndpoints.UploadAsync (잠금 대기 초과·INSERT 실패·요청 취소 이후) — 잠금 밖에서 내용 주소 파일을 옮긴 뒤 행 INSERT 없이 요청이 끝남 (보상 삭제가 없어 참조 없는 파일로 남는다. AttachmentJanitor(최소 1시간 경과, 6시간 주기)가 정리하거나, 같은 내용을 다시 올리면 그 파일을 재사용한다.)
- F009: AttachmentEndpoints.UploadAsync (SaveChangesAsync 23505 폴백) — Sha256 유니크 위반(잠금을 거치지 않는 경로에 대한 방어) (ChangeTracker.Clear 후 SingleAsync로 기존 행을 조회해 200을 반환한다. 그 사이 행이 사라지면 InvalidOperationException으로 500이 된다(처리 없음))
- F009: AttachmentEndpoints.UploadAsync (SaveChangesAsync 기타 예외) — 23505가 아닌 DbUpdateException(CHECK 위반·연결 오류·statement_timeout 등) (처리 없음(예외 전파). 내부 예외가 57014·55P03이면 503, 그 밖에는 500이 된다)
- F009: AttachmentLock.Releaser.DisposeAsync — pg_advisory_unlock 실행 실패 (원래 예외를 보존하려고 로그 없이 삼키고 CloseConnectionAsync를 호출한다. 연결이 건강하면 그 물리 연결이 다시 쓰일 때까지 잠금 해제가 늦어진다. 그동안 같은 sha256 요청은 503을 받을 수 있다(코드 주석의 측정과 추론).)
- F009: FileSystemAttachmentStore.PhysicalPath (TryDelete·Exists·SaveAsync 경유) — StoragePath가 저장 루트를 벗어남(DB 값 손상) (InvalidOperationException을 던진다. TryDelete는 이 예외를 잡지 않으므로, 삭제 경로에서는 행이 이미 지워진 뒤 500으로 전파된다(처리 없음))
- F010: PublicAttachmentEndpoints.GetAsync — PublicDbContext 조회 — DB 접속 실패·네트워크 오류 등 PostgresException이 아닌 NpgsqlException이거나, SqlState가 57014·55P03이 아닌 서버 오류인 경우. (처리 없음(예외 전파). IsOverload가 false를 반환하므로 기본 예외 처리로 500이 된다(503 아님).)
- F010: FileSystemAttachmentStore.PhysicalPath — DB의 StoragePath가 손상돼 정규화 경로가 저장 루트 밖을 가리키는 경우. (처리 없음(예외 전파). 핸들러 try 블록 밖에서 InvalidOperationException('첨부 경로가 저장 루트를 벗어난다.')이 던져져 500이 된다. 루트 밖 파일은 읽지 않는다.)
- F010: PublicAttachmentEndpoints.GetAsync — new FileStream — UnauthorizedAccessException(권한), 공유 위반 등 기타 IOException, 디스크 오류. (처리 없음(예외 전파). 500이 된다.)
- F011: MarkdownRenderer.RenderDetailed — 입력이 null이거나 UTF-8 기준 MaxInputBytes를 넘음 (try 블록 밖에서 ArgumentNullException이나 ArgumentException을 던지며, MarkdownTooComplexException으로 바꾸지 않는다(예외 전파). 저장·미리보기 경로는 사전 검증으로 막는다. 공개 경로는 DB 제약(CK_Posts_Content_Size)에만 기대고, PostModel은 이 예외를 잡지 않는다. 따라서 제약을 우회한 데이터가 있으면 500이 된다.)
- F012: PublicQueries.PageAsync의 DB 접근 — 57014/55P03이 아닌 Npgsql·EF 예외. 예: DB 연결 실패, 공개 롤 GRANT 누락으로 인한 권한 부족 (처리 없음(예외 전파). `UseExceptionHandler()`의 기본 처리로 500이 됩니다. `AddProblemDetails()`가 등록돼 있고 `UseExceptionHandler`가 `UseStatusCodePages`보다 바깥에 있으므로, 본문은 고정 HTML이 아니라 ProblemDetails일 가능성이 있습니다(미확인).)
- F013: PublicQueries (PostgreSQL) — 그 밖의 DB 오류(연결 실패, 57014·55P03 외의 SqlState) (처리 없음(예외 전파). OverloadExceptionHandler가 false를 돌려주면 UseExceptionHandler의 기본 경로가 500으로 응답한다. ErrorPipelineTests는 GET /posts/x에서 23505가 500(Retry-After 없음)이 되는지 확인하지만, 500 응답 본문의 형식은 검사하지 않는다)
- F014: PublicQueries.PageAsync / PublicDbContext — 57014·55P03이 아닌 DB 예외(연결 실패, 인증 실패, 풀 고갈 등) (처리 없음(예외 전파). OverloadExceptionHandler는 false를 반환하므로 프레임워크 기본 예외 처리(500)로 넘어간다. 응답 본문 형식은 코드로 확정하지 못했다.)
- F015: PublicQueries.GetSeriesAsync (DB 연결) — DB 연결 실패 또는 기타 Npgsql/EF 예외 (처리 없음(예외 전파). OverloadExceptionHandler가 false를 반환하므로 UseExceptionHandler 기본 처리(500)로 넘어간다. 기능 수준의 폴백·재시도는 없다.)
- F016: PublicDbContext 조회(연결 실패, 권한 오류 등 57014/55P03 외의 DB 예외) — DB 연결 불가, 공개 롤 권한 누락 등 (처리 없음(예외 전파). UseExceptionHandler의 기본 처리로 500이 된다고 보지만, 이 경로의 응답 본문 형식(ProblemDetails인지 HTML인지)은 확인하지 못했다.)
- F017: SiteEndpoints.FeedAsync / SitemapAsync — DB 연결 실패처럼 과부하로 분류되지 않는 DB 예외 (처리 없음(예외 전파). 핸들러에 try/catch가 없어 UseExceptionHandler 기본 처리(500)로 넘어간다. 재시도나 폴백은 없다.)
- F017: SiteEndpoints.SitemapAsync / FeedAsync (XmlWriter, CheckCharacters=true) — Clean을 거치지 않는 값(PublicOrigin, slug)에 XML 무효 문자가 들어간 경우 (처리 없음(예외 전파). XmlWriter가 ArgumentException을 던져 문서 전체가 500이 된다. 다만 입력은 세 겹으로 제한된다. slug는 API 저장 경로의 SlugRules와 DB CHECK 제약(CK_Posts_Slug_Format·CK_Series_Slug_Format)이 문자 집합을 막고, PublicOrigin은 시작 시 SiteOptions.HostOf가 정규 origin 형식을 강제한다. 태그는 Uri.EscapeDataString으로 인코딩된다. 그래서 정상 데이터에서는 사실상 발생하지 않는다.)
- F017: HighlightCss.Value (Lazy<string>) — 첫 Build()가 예외를 던지는 경우(ColorCode 출력 이상 등) (처리 없음(예외 전파). Lazy 기본 모드는 예외를 캐시하므로, 프로세스를 재시작할 때까지 /css/highlight.css 요청이 계속 500이 될 수 있다. .NET 동작에서 추론했고 측정하지 않았다.)
- F019: StartupValidation.Validate / UseTrustedForwardedHeaders / ClientIp.PartitionKey — Proxy:TrustedIp가 비어 있다 (Development가 아닌 모든 환경에서는 Require(proxy.TrustedIp.Length > 0, "Proxy:TrustedIp")가 기동을 막는다. 따라서 값이 빈 상태는 Development에서만 생긴다. 그때는 UseForwardedHeaders가 등록되지 않아 RemoteIpAddress가 직접 연결한 상대의 IP가 된다. Development를 프록시 뒤에서 돌리면 모든 방문자가 IP 파티션 하나를 공유하며, 이를 막는 별도 처리는 없다.)
- F020: Kestrel (앱 도달 전) — 요청 줄 길이 초과 등 서버가 직접 거부하는 요청 (보안 헤더가 붙지 않는다. 주석에 '이 저장소에서 측정하지는 않았다'고 적혀 있다.)
- F020: OverloadExceptionHandler.IsOverload — AggregateException이 내부 예외를 여러 개 가짐 (InnerException 체인만 훑으므로 InnerExceptions의 첫 요소만 본다. 주석은 AggregateException을 만드는 코드 경로가 현재 없다고 판단하지만 실측하지는 않았다고 적는다.)
- F020: ApiBodyLimitMiddleware.LengthLimitedStream — 최소 API 바인딩 밖에서 본문을 직접 읽는 /api 핸들러가 상한을 넘음 (처리 없음(예외 전파). BadHttpRequestException이 UseExceptionHandler까지 올라갔을 때 413이 되는지 500이 되는지 코드로 확인하지 못했다.)
- F020: ErrorResponses.WriteAsync — IProblemDetailsService.TryWriteAsync가 false를 반환함(예: 요청 Accept가 JSON을 허용하지 않아 기록기가 쓰지 못함) (반환값을 확인하지 않아 오류 응답 본문이 빈 채로 나갈 수 있다. 기록기 선택 조건은 프레임워크 동작이라 코드로 확인하지 못했다.)
- F021: Program.cs adminDb.Database.Migrate() — DB에 접속할 수 없거나(postgres 미기동, 인증 실패, 네트워크 오류) 마이그레이션 DDL이 실패함 (처리 없음(예외 전파). 코드에 재시도나 대기가 없어 프로세스가 종료된다. 운영에서는 compose의 restart: unless-stopped와 depends_on service_healthy에 기대는 것으로 보인다.)
- F021: PublicRoleGrants.Apply 트랜잭션 안(SqlQueryRaw / ExecuteSqlRaw 루프) — PostgresException이 난다. 예: 공개 롤이 DB에 없어 REVOKE ... FROM role이 실패, 권한 부족(42501) 등 (처리 없음(예외 전파). using var transaction이 Commit 없이 Dispose되어 권한 변경은 롤백되고, 기동이 실패한다. 이미 커밋된 마이그레이션은 되돌리지 않는다. 롤이 없을 때의 구체적 오류 코드는 코드로 확인하지 못했다(PostgreSQL 동작에서 추론).)
- F021: Program.cs MarkdownRenderer.Render 워밍업 — 렌더러 정적 초기화나 렌더 중 예외. Render는 입력이 크기를 넘으면 ArgumentException, 중첩 한도를 넘으면 MarkdownTooComplexException을 던질 수 있다. 다만 워밍업 입력은 짧은 상수다. (처리 없음(예외 전파). 입력이 상수 코드 블록이라 실패할 가능성은 낮지만 try/catch가 없다.)
- F021: PublicDbContext.BuildConnectionString (첫 공개 요청 때 지연 실행) — 기반 연결 문자열(Public, 또는 폴백된 Default)에 Options가 있음 (InvalidOperationException을 던진다. 그런데 메시지는 항상 'ConnectionStrings:Default'를 가리키므로, 기반이 Public일 때는 틀린 키가 표시된다. StartupValidation이 같은 조건을 올바른 키로 먼저 거르므로 정상 기동 경로에서는 드러나지 않는다.)
- F021: PublicRoleGrants.Apply 소유 테이블 필터(tableowner = current_user) — public 스키마에 관리 롤이 아닌 롤이 소유한 테이블이 있고, 그 테이블에 blog_public 권한이 붙어 있음 (그 테이블은 회수 대상에서 빠져 권한이 그대로 남는다(fail-open). 기동은 성공한다. 10-roles.sh는 'public 스키마의 객체는 전부 blog_app 소유'라는 불변식을 운영 규칙으로 문서화하고 있다.)
- F021: PublicRoleGrants.Apply 대상 범위(pg_tables) — public 스키마의 뷰·머티리얼라이즈드 뷰·시퀀스·함수에 대한 권한 (pg_tables만 조회하므로 이 객체들은 회수·부여 대상이 아니다. 현재 마이그레이션에는 뷰가 없다.)
- F022: HealthCheckCommand.RunAsync (catch 필터) — HttpRequestException·TaskCanceledException·UriFormatException 이외의 예외(예: 비정상 포트 문자열로 요청 생성·전송 단계에서 다른 예외가 나는 경우) (처리 없음(예외 전파). 미처리 예외로 프로세스가 비정상 종료된다. 종료 코드가 0이 아니므로 Docker는 여전히 실패로 판정하는 것으로 보인다. 다만 XML 주석의 '예외를 밖으로 던지지 않는다' 계약과 어긋나고, stderr에는 한 줄 요약 대신 스택 트레이스가 남는다.)
- F023: HashPasswordCommand.ReadHidden — `Console.IsInputRedirected`가 false인데 실제 콘솔이 없는 환경(TTY 없는 컨테이너 등)에서 `Console.ReadKey` 호출 (처리 없음(예외 전파). .NET은 이 경우 InvalidOperationException을 던지는 것으로 알려져 있으나 이 저장소에서 측정한 근거는 없다)
- F023: HashPasswordCommand.Run (output.WriteLine / error.Write) — stdout·stderr 쓰기 실패(파이프 소비자 조기 종료 등 IOException) (처리 없음(예외 전파))
- F023: StartupValidation.Validate (CLI 밖) — base64로는 유효하지만 PasswordHasher 형식이 아닌 값 (시작 검증은 base64 디코딩만 확인하므로 통과한다. 이후 로그인은 Verify가 Failed를 반환해 불가능할 것으로 보인다(프레임워크 동작, 미측정))
- F024: AttachmentLock.HoldAsync (52행, try 블록 밖) — db.Database.OpenConnectionAsync(ct)가 실패(DB 연결 불가, 풀 고갈, 취소 등) (처리 없음(예외 전파). OpenConnectionAsync는 try 앞에 있어서 HoldAsync의 catch와 CloseConnectionAsync를 거치지 않고 예외가 호출부로 그대로 전파된다. 55P03이 아니므로 IsLockTimeout 필터도 통과하지 못한다. 스윕 전체가 중단되고 ExecuteAsync가 LogError를 남긴다. 취소로 인한 OperationCanceledException이면 루프가 종료된다.)
- F024: AttachmentJanitor.SweepOnceAsync → FileSystemAttachmentStore.TryDelete — 고아 파일 삭제가 IO 또는 권한 오류로 실패해 false 반환 (orphans를 증가시키지 않을 뿐, Janitor 쪽에는 로그가 없다. 삭제 실패가 반복돼도 LogInformation의 Orphans 수치가 기대보다 작게 나오는 것 말고는 드러나지 않는다.)
- F024: AttachmentJanitor.SweepOnceAsync 누락 진단 루프 → FileSystemAttachmentStore.Exists → PhysicalPath — DB의 StoragePath 값이 손상돼 저장 루트 밖을 가리킴. StoragePath에는 HasMaxLength(80) 외에 CHECK 제약이 없다(CHECK는 Size·Sha256·ContentType·FileName에만 있다). (처리 없음(예외 전파). PhysicalPath가 InvalidOperationException을 던져 SweepOnceAsync 전체가 실패하고, ExecuteAsync가 LogError를 남긴다. 그런 행이 남아 있는 한 매 주기 같은 실패가 반복되어 누락 경고와 결과 로그가 계속 사라진다. 앞 단계의 파일 삭제는 이미 수행된 상태다.)
- F025: {$ADMIN_DOMAIN} @denied remote_ip — Docker 사용자 공간 프록시 등으로 remote_ip가 게이트웨이(172.30.0.1)로 보임 (Caddyfile 안에는 처리가 없다. 이 경우 허용 목록이 무의미해지거나 작성자가 잠길 수 있어, 운영 문서가 사전 확인 절차를 둔다.)
- F026: PortfolioBlog.Api/Program.cs 시작 시퀀스 — StartupValidation 위반, 첨부 루트 쓰기 불가, Migrate/PublicRoleGrants.Apply 실패(DB 접속·권한 오류) (try/catch가 없어 예외가 전파되고 프로세스가 종료된다. restart: unless-stopped가 컨테이너를 재시작하므로 원인이 남아 있으면 재시작 루프가 된다. 운영자는 docker compose logs api로 첫 예외 메시지의 설정 키를 확인한다.)
- F026: api 컨테이너 런타임 — 기동 후 api가 unhealthy가 됐지만 프로세스는 살아 있음 (restart 정책은 종료된 컨테이너만 재시작하고, depends_on은 기동 시점만 게이트한다. 그래서 caddy는 계속 api로 프록시한다. 자동 복구 처리는 없다.)
- F026: caddy 서비스 — caddy 자체가 비정상(인증서 발급 실패 등) (caddy에는 healthcheck가 없다. OPERATIONS.md는 running 상태와 로그(grep acme/certificate)로 확인하라고 안내한다.)
- F026: api /tmp tmpfs — Admin__UploadConcurrency를 기본값 2보다 올렸는데 tmpfs size=64m은 그대로 둠 (compose 주석에 따르면 셋째 업로드부터 tmpfs가 고갈돼 400이 난다. 두 값을 맞추는지 자동으로 검증하지 않는다.)
- F027: deploy/backup.sh 실행 중 신호 — SIGINT·SIGTERM(Ctrl-C, cron 강제 종료)으로 중단 (ERR trap은 신호에 반응하지 않아 평문 덤프가 든 부분 디렉터리가 남을 수 있다. 다만 SHA256SUMS가 없으므로 restore.sh의 sha256sum -c가 이 디렉터리를 거부한다.)
- F027: deploy/restore.sh tools find -delete && tar -xf — pg_restore 커밋 후 첨부 교체 중 실패 (안내 메시지만 출력하고 롤백하지 않는다. DB는 이미 백업 내용이고 첨부는 비어 있거나 일부만 풀린 상태다. fail()이 권하는 'docker compose up -d로 이전 상태로 돌아간다'는 이 시점 이후에는 성립하지 않는다(이전 DB와 첨부가 이미 사라짐). 같은 백업으로 재실행해야 한다.)
- F027: deploy/restore.sh 최종 docker compose up -d --wait — api가 healthy가 되지 못함(예: 기동 시 Migrate() 실패, 비밀번호 불일치) (compose가 오류를 반환하고 fail()이 재실행을 안내한다. 원인이 설정이나 스키마라면 재실행으로는 해결되지 않는다. 대기 시간은 compose healthcheck(api: interval 30s·retries 3·start_period 40s, postgres: interval 10s·retries 12)에 묶인다.)
- F028: run.sh 이미지 검사(셸 없음) — docker run --entrypoint /bin/sh가 셸이 없어서가 아닌 다른 이유(데몬 오류, 이미지 없음 등)로 실패 (실패 이유를 가리지 않고 '셸 없음'으로 통과시킨다. 같은 파일의 deny()는 종료 코드가 아니라 메시지로 판정해 거짓 통과를 막는데, 이 검사는 그러지 않는다.)
- F029: PortfolioBlog.Web/src/components/RouteError.tsx — 청크 로드 실패가 아닌 일반 렌더 버그로 오류 경계에 도달한다 (청크 실패 때와 같은 '새 버전이 배포됐을 수 있습니다' 문구를 보여 준다. 원인을 구분하거나 기록하지 않으므로, 같은 버그라면 새로고침해도 반복된다.)
- F029: PortfolioBlog.Web/src/main.tsx — index.html에 #root가 없다 (처리 없음(예외 전파). 비null 단언(!) 때문에 createRoot(null)이 호출되어 예외가 난다. 현재 index.html에는 #root가 있다.)
<!-- /doc-harness:section -->

<!-- doc-harness:section id="related" hash="ce676436aa38762354815ad61763bb122847c8228f207d510009df499c648eb2" -->
## 관련 문서

- [17_TECH_DEBT](17_TECH_DEBT.md)
- [11_FAILURE_HISTORY](11_FAILURE_HISTORY.md)
<!-- /doc-harness:section -->

<!-- doc-harness:section id="residual" hash="a90dae8a3d7f8630dc61a733b06c32b15a7f330febfda93eb60e6c4b8688193c" -->
## 검증 잔여 지적(사람 확인 필요)

검증 루프가 최대 반복 후에도 해결하지 못한 LLM 검증자의 지적 61건. 코드가 맞고 문서가 틀렸을 수도, 지적이 틀렸을 수도 있다 — 다음 `문서화` 실행 전에 확인한다.

| 문서 | 지적 |
|---|---|
| 00_EXECUTIVE_SUMMARY.md #EXEC_DATAFLOW | 코드 근거 표의 경로를 바로잡는다. Caddyfile→`deploy/Caddyfile`, AdminSurfaceMiddleware→`PortfolioBlog.Api/Infrastructure/Access/AdminSurfaceMiddleware.cs`, MarkdownRenderer→`PortfolioBlog.Api/Infrastructure/Markdown/MarkdownRenderer.cs`, AppDbContext→`PortfolioBlog.Api/Infrastructure/Data/AppDbContext.cs`. |
| 03_DIRECTORY_STRUCTURE.md #roles | scripts 행을 고친다. `hooks/guard-write-scope.ps1`은 Claude Code PreToolUse 훅이고 Git 훅은 `git-hooks/commit-msg`라고 적는다. |
| 03_DIRECTORY_STRUCTURE.md #tree | 파일 트리에 빠진 `scripts/git-hooks/commit-msg`와 `deploy/.env.example`을 추가한다. |
| 08_API.md #table | POST/PUT /api/posts 오류 열의 'DB 57014/55P03 → 503'을 빼거나 '관리 연결에는 timeout이 설정되어 있지 않아 발생 경로 미확인'으로 고치고, 503 원인은 RenderBusyException으로 한정한다. |
| 08_API.md #table | JSON 본문을 받는 /api 엔드포인트(login, posts POST/PUT, series POST/PUT, preview)의 오류 열에 ApiBodyLimitMiddleware의 413(256KB 초과)을 추가하고, unknowns의 해당 항목을 지운다. |
| 08_API.md #table | GET /, /tags/{tag}, /series/{slug} 행의 '추정·미확인' 표기를 코드 기준으로 확정한다: /와 /tags는 page 쿼리(상한 500, 형식 밖·상한 초과·page>1 빈 결과는 404), /series는 page 쿼리가 없고 slug가 공백·형식 밖이거나 시리즈가 없으면 404. |
| 08_API.md #details | GET /, GET /series/{slug}, GET /tags/{tag} 상세는 '미열람'이라고 적으면서 상태를 CONFIRMED로 두었다. 코드에서 확인한 검증 규칙(PageNumber.TryRead, SlugRules.IsValid, TagMax·NUL·PublicUrls.Tag 검사)으로 바꿔 쓴다. |
| 08_API.md #table | /css/highlight.css, /feed.xml, /sitemap.xml, /robots.txt 행의 오류 열에 'RequireHost(publicHost) 때문에 관리 호스트에서는 404'를 추가한다. |
| 08_API.md #table | GET /api/auth/me 응답을 'AuthStatusDto{Authenticated}(값은 AuthenticateAsync 결과의 Succeeded)'로 고쳐 DTO 필드 이름이 Authenticated임을 밝힌다. |
| features/F008_ADMIN_TAG_MANAGEMENT.md #F008_FLOW | mermaid 본문에 LoadPost(Posts.Include PostTags SingleOrDefaultAsync)→NotFound404, PostValidation 400, version StaleVersion 409, RenderOrAddErrorAsync 400/503, Create slug AnyAsync 409 노드를 넣고 `PostEndpoints --> Validate`·`Validate -->\|통과\| BeginTransaction` 직행 간선을 제거해 설명 문단·코드 근거 표와 일치시킨다(노드 수가 넘치면 ResolveIdsAsync 내부 노드는 F008_FLOW_RESOLVE로 넘긴다). |
| features/F008_ADMIN_TAG_MANAGEMENT.md #summary | F008_FLOW 다이어그램 구조를 설명하는 문장(LoadPost·NotFound404 노드, 직행 간선 없음)을 실제 수정된 다이어그램과 맞추거나, 다이어그램을 고치지 않는다면 삭제한다. |
| features/F008_ADMIN_TAG_MANAGEMENT.md #unknowns | '다이어그램에는 LoadPost와 NotFound404 노드가 검증 노드 앞에 있다'는 수정 완료 주장이 현재 다이어그램과 다르므로 다이어그램 수정 후에만 남기거나 제거한다. |
| 09_FEATURES.md #dependencies | F008 행의 의존 기능을 F008 문서·코드와 같게 F001, F018, F020, F029로 맞춘다. |
| 13_SECURITY.md #evidence | SiteEndpoints 근거 경로를 `PortfolioBlog.Api/Features/SiteEndpoints.cs`에서 `PortfolioBlog.Api/Pages/SiteEndpoints.cs`로 고친다. |
| 13_SECURITY.md #details | '보안 헤더가 모든 응답에 붙고'를 고친다. HostFiltering 400과 Kestrel 거부 응답에는 헤더가 없고, 대신 HostFiltering 400은 본문도 없다는 점을 반영한다. |
| 13_SECURITY.md #unknowns | HostFiltering과 SecurityHeadersMiddleware의 상대 위치는 Program.cs 93-108행으로 확정되므로 UNKNOWN에서 빼고 실제 순서를 적는다. |
| 10_ERROR_HANDLING.md #key-points | ErrorResponses의 ProblemDetails 대상 경로를 `/api`·`/attachments`·`/health`·`/openapi`로 바로잡고, 공개 첨부 404는 ProblemDetails라는 점을 반영한다(404 행과 ERR014 포함). |
| 10_ERROR_HANDLING.md #ERR_FLOW | Program 노드의 코드 근거를 UseExceptionHandler(Program.cs 100행)로 고친다. HttpResponse 노드는 ErrorResponses.cs에 연결하지 말고 추상 노드임을 밝히거나 없앤다. |
| 10_ERROR_HANDLING.md #unknowns | F017 이후 실패 경로를 반영하지 못했다는 항목은 본문 ERR012 등과 모순되므로 지우거나 고친다. |
| 14_PERFORMANCE.md #key-points | '동기 파일 I/O는 기동 probe와 AttachmentJanitor에만'을 고친다. 업로드 SaveAsync, 삭제 TryDelete, 공개 첨부 FileStream 열기에도 동기 파일 I/O가 있다. |
| 14_PERFORMANCE.md #key-points | '미리보기 200KB'는 본문 상한이 아니라 markdown 필드 UTF-8 크기 검증(초과 시 400)이다. 미리보기 요청 본문 상한은 256KB(413)다. |
| 18_GLOSSARY.md #one-liner | 용어 수 47개를 실제 표 행 수(41)에 맞추고, '가나다/알파벳 순'이라는 설명을 지우거나 표를 실제로 정렬한다. |
| 18_GLOSSARY.md #terms | MarkdownTooComplexException의 코드 경로를 `PortfolioBlog.Api/Infrastructure/Markdown/MarkdownTooComplexException.cs`로 고친다. |
| 19_UNKNOWN_AND_TODO.md #unknowns | 코드로 이미 확인된 항목을 UNKNOWN 목록에서 빼거나 확인됨으로 옮긴다. 해당 항목은 ApiBodyLimit 262,144바이트·413, 첨부 Cache-Control, Caddyfile 에지 방어, AttachmentJanitor 6시간·1시간, 옵션 코드 기본값, CI 잡 구성이다. |
| 00_EXECUTIVE_SUMMARY.md #EXEC_DATAFLOW | [hallucination:NONEXISTENT_PATH] 코드 근거 표에서 Caddyfile의 코드 경로를 저장소 루트의 `Caddyfile`로 적었지만 루트에는 그런 파일이 없다. 실제 파일은 `deploy/Caddyfile`이다. |
| features/F008_ADMIN_TAG_MANAGEMENT.md #summary | [hallucination:DIAGRAM_DESCRIPTION_MISMATCH] 요약은 'F008_FLOW 다이어그램에서 UpdateAsync는 먼저 글 조회 노드(LoadPost)로 가고, 거기서 404 노드(NotFound404)로 갈라진 뒤에야 검증 노드(PostValidation)에 닿는다. 검증 400, version 409, 렌더 400/503, slug 409도 각각 별도 노드다. PostEndpoints에서 검증으로 곧장 가는 간선이나, 검증에서 트랜잭션으로 곧장 가는 간선은 없다'고 적는다. 실제 F008_FLOW mermaid 본문에는 LoadPost·NotFound404·PostValidation·version 409·렌더 400/503·slug 409 노드가 하나도 없고, 오히려 `PostEndpoints --> Validate[TagResolver.Validate]`와 `Validate -->\|통과\| BeginTransaction` 간선이 그대로 있다. 요약이 존재하지 않는 다이어그램 구조를 설명한다. |
| features/F008_ADMIN_TAG_MANAGEMENT.md #unknowns | [hallucination:DIAGRAM_DESCRIPTION_MISMATCH] unknowns의 마지막 항목은 '다이어그램에는 LoadPost(Posts.Include PostTags SingleOrDefaultAsync)와 NotFound404(404 NotFound) 노드가 검증 노드 앞에 있다'고 수정 완료를 주장하지만, 현재 F008_FLOW mermaid에는 두 노드가 없다. 수정이 반영되지 않았는데 반영됐다고 적었다. |
| features/F008_ADMIN_TAG_MANAGEMENT.md #F008_FLOW | [hallucination:DIAGRAM_DESCRIPTION_MISMATCH] F008_FLOW의 설명 문단('UpdateAsync는 ...글을 먼저 조회한다(LoadPost). null이면 NotFound404로 끝나고 ... PostEndpoints에서 검증으로, 또는 검증에서 트랜잭션으로 곧장 가는 간선은 없다')과 코드 근거 표(Posts.Include PostTags SingleOrDefaultAsync, 404 NotFound, post.Version 비교, RenderOrAddErrorAsync, OverloadExceptionHandler 503, Posts.AnyAsync Slug, StaleVersion)가 mermaid 본문에 없는 노드·간선을 설명한다. |
| 13_SECURITY.md #evidence | [hallucination:NONEXISTENT_FILE_PATH] 근거 표에 `PortfolioBlog.Api/Features/SiteEndpoints.cs \| 71-80`이 있지만 이 경로의 파일은 없다. SiteEndpoints는 `PortfolioBlog.Api/Pages/SiteEndpoints.cs`에 있다. RequireHost(publicHost) 호출(71·74·76·80행)은 그 파일에 있다. |
| 03_DIRECTORY_STRUCTURE.md #tree | [missing:MISSING_FILE] 파일 트리에 실제로 있는 `scripts/git-hooks/commit-msg`(커밋 메시지 접두사를 강제하는 Git commit-msg 훅)가 빠져 있다. roles 표의 scripts 설명에도 이 파일이 없다. |
| 03_DIRECTORY_STRUCTURE.md #tree | [missing:MISSING_FILE] deploy/ 트리에 compose가 참조하는 환경변수 예시 파일 `deploy/.env.example`이 없다(F026 관련 파일이기도 하다). 값을 옮기지 않고 파일명만 적는 것은 허용된다. |
| 08_API.md #table | [missing:MISSING_FAILURE_PATH] ApiBodyLimitMiddleware는 자체 크기 상한 메타데이터가 없는 /api 엔드포인트(로그인, 글 POST/PUT, 시리즈 POST/PUT, 미리보기 등)의 JSON 본문을 256KB(JsonLimitBytes=262_144)로 제한합니다. Content-Length가 넘으면 읽기 전에 413을 돌려주고, chunked 본문은 읽는 도중 BadHttpRequestException(413)으로 끊습니다. 업로드처럼 RequestSizeLimit을 가진 엔드포인트와 매칭 라우트가 없는 요청은 제외됩니다. 문서는 이 413을 '확인하지 못한 것'으로만 남기고 오류 열에서는 뺐습니다. |
| 08_API.md #table | [missing:MISSING_DETAIL] GET / 행은 page 쿼리와 404 조건을 '추정/미확인'으로 적었지만 코드로 확인됩니다. PageNumber.TryRead(Request.Query, MaxPage=500)가 실패하거나(형식 밖 또는 500쪽 초과) page>1인데 결과가 없으면 404입니다. 조회는 PublicQueries.LatestAsync가 COUNT 1회 + 목록 SELECT 1회를 합니다. |
| 08_API.md #table | [missing:MISSING_DETAIL] GET /tags/{tag}도 page 쿼리를 받습니다(PageNumber.TryRead, 상한 IndexModel.MaxPage=500). 태그가 공백뿐일 때, TagMax*4 초과·NUL 포함일 때, 정규화 후 길이가 0이거나 TagMax 초과일 때, PublicUrls.Tag가 null일 때, 태그가 없을 때, page>1인데 결과가 없을 때 모두 404입니다. 문서는 요청 열에 page를 적지 않았고 404 조건을 '추정'으로 남겼습니다. |
| 08_API.md #table | [missing:MISSING_DETAIL] GET /series/{slug}는 page 쿼리를 받지 않습니다(PageNumber 호출 없음). slug가 공백뿐이거나, SlugRules.IsValid를 통과하지 못하거나, 시리즈가 없으면 404입니다. 문서는 'page 쿼리 여부는 미열람', '404 시리즈 없음(추정)'으로 남겼습니다. |
| 08_API.md #table | [missing:MISSING_FAILURE_PATH] /css/highlight.css, /feed.xml, /sitemap.xml, /robots.txt는 RequireHost(publicHost)로 등록되어 있습니다. 관리 호스트 요청은 라우팅에서 매칭되지 않아 404입니다. Razor 페이지 행에는 '비공개 호스트 404'가 있지만 이 네 행의 오류 열에는 빠져 있습니다. |
| 09_FEATURES.md #dependencies | [missing:INCONSISTENT_DEPENDENCY] 09_FEATURES 의존 표는 F008의 의존을 'F001, F029'로만 적지만, F008 문서와 코드는 /api/tags 요청이 모두 AdminSurfaceMiddleware(F018)를 거치고 오류가 UseExceptionHandler·OverloadExceptionHandler·UseStatusCodePages(F020)로 처리됨을 보인다. F008 문서의 의존(F001·F018·F020·F029)과 목록 문서가 서로 다르다. |
| 10_ERROR_HANDLING.md #key-points | [missing:MISSING_PATH_RULE] ErrorResponses가 ProblemDetails를 쓰는 경로 접두사는 `/api`만이 아니라 MachinePrefixes 네 개(`/api`, `/attachments`, `/health`, `/openapi`)다. 공개 첨부 GET의 본문 없는 404(TypedResults.NotFound)는 고정 HTML이 아니라 ProblemDetails를 받는다. 문서는 이 규칙을 빠뜨렸다. |
| 00_EXECUTIVE_SUMMARY.md #EXEC_DATAFLOW | [relation:WRONG_CODE_MAPPING] 코드 근거 표가 AdminSurfaceMiddleware를 `PortfolioBlog.Api/Program.cs`에 연결한다. Program.cs는 이 미들웨어를 등록만 한다(`app.UseMiddleware<AdminSurfaceMiddleware>()`). 구현은 `PortfolioBlog.Api/Infrastructure/Access/AdminSurfaceMiddleware.cs`에 있다. |
| 00_EXECUTIVE_SUMMARY.md #EXEC_DATAFLOW | [relation:WRONG_CODE_MAPPING] 코드 근거 표가 MarkdownRenderer를 `RenderGate.cs`에 연결한다. RenderGate.cs는 생성자에서 `renderer.RenderDetailed`를 참조할 뿐이다. MarkdownRenderer 클래스와 `RenderDetailed`는 `PortfolioBlog.Api/Infrastructure/Markdown/MarkdownRenderer.cs`에 정의돼 있다. |
| 00_EXECUTIVE_SUMMARY.md #EXEC_DATAFLOW | [relation:WRONG_CODE_MAPPING] 코드 근거 표가 AppDbContext를 `PostEndpoints.cs`에 연결한다. PostEndpoints.cs는 AppDbContext를 주입받아 쓰는 쪽이다. 클래스 정의는 `PortfolioBlog.Api/Infrastructure/Data/AppDbContext.cs`에 있다. |
| 03_DIRECTORY_STRUCTURE.md #roles | [relation:WRONG_DESCRIPTION] scripts 행이 `hooks/guard-write-scope.ps1`을 'Git 훅 스크립트'라고 설명한다. 이 파일은 Claude Code의 PreToolUse 훅으로, 서브에이전트의 Write/Edit 범위를 제한한다. 실제 Git 훅은 `scripts/git-hooks/commit-msg`다. |
| features/F008_ADMIN_TAG_MANAGEMENT.md #F008_FLOW | [relation:CALL_ORDER] 다이어그램은 PostEndpoints가 곧장 검증으로 가고 검증 통과 시 곧장 BeginTransactionAsync로 간다고 그린다. 실제 코드: UpdateAsync는 검증 전에 Posts.Include(PostTags).SingleOrDefaultAsync로 글을 조회해 없으면 404(189-190), 검증(192-197) 뒤 post.Version != req.Version이면 StaleVersion 409(201), RenderOrAddErrorAsync(206-207, 400 또는 RenderBusyException→503) 후에야 트랜잭션(212)을 연다. CreateAsync도 검증(126-128) → 렌더(133-134) → slug 사전 검사 409(136) → 트랜잭션(138) 순이다. |
| 10_ERROR_HANDLING.md #key-points | [relation:INCORRECT_BEHAVIOR] 문서는 '/api는 ProblemDetails, 공개 경로는 고정 HTML'로 나눈다(전역 핸들러 표, 404 행, ERR014). 코드에서는 `/attachments`·`/health`·`/openapi`도 ProblemDetails다. 그래서 공개 표면인 첨부 404는 HTML이 아니다. |
| 13_SECURITY.md #details | [relation:OVERSTATED_CLAIM] '이미 잘 처리된 것'에 '보안 헤더가 모든 응답에 붙고'라고 적혀 있다. 코드 주석에 따르면 HostFiltering 시작 필터의 400은 SecurityHeadersMiddleware보다 바깥에서 만들어진다. 그래서 이 응답에는 보안 헤더가 붙지 않는다(HostFilteringTests.RejectedHost_400_HasNoBody_AndNoSecurityHeaders로 실측). 대신 IncludeFailureMessage=false로 본문을 없앴다. Kestrel이 앱 도달 전에 거부하는 응답에도 헤더가 없다. |
| 14_PERFORMANCE.md #key-points | [relation:INCORRECT_BEHAVIOR] '동기 파일 I/O는 기동 probe와 AttachmentJanitor에만 있음'은 틀렸다. 요청 경로에도 동기 파일 I/O가 있다. 업로드 경로 FileSystemAttachmentStore.SaveAsync는 Directory.CreateDirectory, 동기 FileStream(FileOptions.Asynchronous 없음), File.Exists, File.Move를 쓴다. 삭제 경로 TryDelete는 File.Delete를 쓴다. 공개 첨부 GET은 FileStream 생성자로 파일을 동기로 연다. |
| 14_PERFORMANCE.md #key-points | [relation:INCORRECT_BEHAVIOR] '요청 본문 상한 \| 기본 256KB, 미리보기 200KB'라고 적혀 있다. 미리보기에는 별도 본문 상한(RequestSizeLimit)이 없고 ApiBodyLimitMiddleware의 256KB(413)가 똑같이 적용된다. 200KB는 markdown 필드의 UTF-8 바이트 수 검증이다. 이를 넘으면 400 ValidationProblem이 나온다. |
| 18_GLOSSARY.md #one-liner | [relation:INCORRECT_COUNT] one-liner와 요약은 '용어 47개'라고 하지만 용어 표의 행은 41개다. 또 '용어는 가나다/알파벳 순'이라고 하지만 실제 순서가 아니다. 예를 들어 ConflictPanel 뒤에 '내용 주소'·'낙관적 동시성'·'동시 실행 제한기'·'임시본'이 오고, 그다음에 healthcheck가 온다. |
| 18_GLOSSARY.md #terms | [relation:WRONG_CODE_LOCATION] MarkdownTooComplexException 행의 코드 이름·경로가 'PostModel (PortfolioBlog.Api/Pages)'로 되어 있다. 예외 클래스는 `PortfolioBlog.Api/Infrastructure/Markdown/MarkdownTooComplexException.cs`에 있다. PostModel은 이 예외를 잡아 쓰는 곳일 뿐이다. |
| 19_UNKNOWN_AND_TODO.md #unknowns | [relation:STALE_UNKNOWN] 코드와 다른 생성 문서로 이미 확인된 사항이 UNKNOWN으로 남아 있다. (1) ApiBodyLimitMiddleware의 한도·상태 코드는 JsonLimitBytes=262,144, 초과 시 413이다(Architecture·API 절). (2) 첨부 200 응답의 Cache-Control은 public, max-age=31536000, immutable이다(성능 절). (3) Caddyfile 에지 방어는 13_SECURITY가 인용한다(보안 절). (4) AttachmentJanitor 주기·조건은 Interval 6시간, MinimumAge 1시간이다(API 절). (5) 옵션 기본값은 appsettings가 아니라 *Options.cs 코드에 있다(Architecture 절). (6) CI 잡 구성은 15_TESTING이 확인했다. |
| 13_SECURITY.md #unknowns | [relation:STALE_UNKNOWN] '미들웨어 등록 순서(HostFiltering과 SecurityHeadersMiddleware의 상대 위치)를 확인하지 못했다'고 적었지만 코드로 확정할 수 있다. HostFiltering은 프레임워크 시작 필터라서 SecurityHeadersMiddleware(Program.cs 98행, 앱 미들웨어 맨 앞)보다 바깥이다. 이어지는 순서는 UseTrustedForwardedHeaders → UseExceptionHandler → UseStatusCodePages → UseStaticFiles → AdminSurfaceMiddleware → UseRateLimiter → UseAuthentication → UseAuthorization → ApiBodyLimitMiddleware다. |
| 13_SECURITY.md #evidence | [relation:CONTRADICTION] 근거 표에는 사이트 엔드포인트의 RequireHost 근거가 `PortfolioBlog.Api/Features/SiteEndpoints.cs` (71-80)로 적혀 있다. 그러나 03_DIRECTORY_STRUCTURE(트리의 PortfolioBlog.Api/Pages/SiteEndpoints.cs), 08_API, 18_GLOSSARY는 모두 `PortfolioBlog.Api/Pages/SiteEndpoints.cs`로 적는다. 코드에서도 SiteEndpoints.cs는 Pages 아래에만 있다(Features 아래에는 없음). 66-80행에서 MapMethods(...).RequireHost(publicHost)로 공개 호스트 제약을 건다. 틀린 쪽은 13_SECURITY의 경로다. |
| 11_FAILURE_HISTORY.md #FAIL005 | [relation:CONTRADICTION] FAIL005의 현재 workaround(요약 표와 본문)는 '운영 public 서브넷은 자동 할당(호스트 LAN과 겹치면 기동 실패, 운영 문서에 기재)'라고 적는다. 갱신된 00_EXECUTIVE_SUMMARY(status)는 이 겹침 서술이 docs/worklog.md·plan 기록에서 옮긴 것이고 deploy/OPERATIONS.md에는 없다고 적는다. 코드와 문서를 확인한 결과 deploy/docker-compose.yml 128-129행은 public 네트워크에 ipam이 없다고 적고 있다. deploy/OPERATIONS.md에서는 '서브넷'·'LAN'·'겹치' 서술이 검색되지 않는다(3절은 remote_ip가 게이트웨이로 보이는 문제만 다룬다). '운영 문서에 기재'라는 출처는 docs/worklog.md 778행의 주장일 뿐 실제 운영 문서와 맞지 않는다. 00 쪽이 맞다. |
| 00_EXECUTIVE_SUMMARY.md #status | [relation:CONTRADICTION] 현재 상태 절은 '알려진 확정 문제는 검색 성능 한 건이다'라고 하며 PERF001을 그 확정 문제로 든다. 그러나 갱신된 14_PERFORMANCE는 PERF001(검색 ILIKE 풀스캔)을 '조건부 위험(POTENTIAL_RISK)'으로 분류한다. 갱신된 10_ERROR_HANDLING은 확정 결함(CONFIRMED_ISSUE)으로 PERF001이 아니라 ERR004(에디터 409 처리)를 든다. 코드상 PublicQueries.SearchAsync의 ILIKE 스캔은 존재하지만 문서 체계에서는 잠재 위험이다. 반면 PostEditorPage.tsx의 409 분기는 코드로 확인된다(217-225, 307). 00의 '확정 문제 한 건 = 검색 성능' 서술은 두 문서와 모순된다. |
| 17_TECH_DEBT.md #confirmed | [relation:CONTRADICTION] 17_TECH_DEBT는 확인된 문제(CONFIRMED_ISSUE)를 '없음'으로 두고, 에디터 409 처리 문제를 DEBT004(POTENTIAL_RISK)로 분류한다(15_TESTING도 이를 인용). 같은 문제를 갱신된 10_ERROR_HANDLING은 ERR004 CONFIRMED_ISSUE로 분류해 두 문서가 서로 맞지 않는다. 코드 확인 결과는 다음과 같다. PostEditorPage.tsx의 onError는 `error.status === 409 && postId !== null`만 보고 최신본을 재조회해 ConflictPanel을 띄운다(221-222). conflict가 설정되면 ErrorNotice가 렌더되지 않아 서버 detail이 숨겨진다(307). 따라서 'status만으로 분기하고 detail을 숨긴다'는 핵심 동작은 코드로 확정된다. 추론에 그치는 것은 태그 삭제 경쟁으로 생기는 StaleVersion 오탐 부분뿐이다. |
| 00_EXECUTIVE_SUMMARY.md #EXEC_DATAFLOW | [diagram:INCORRECT_DIAGRAM_RELATION] 다이어그램의 노드·간선 순서(PostEndpoints가 RenderGate→AppDbContext→PostQueries→RenderedPostCache를 직접 호출)는 코드와 맞다. 그러나 노드 코드 근거 표에 틀린 경로가 네 개 있다. Caddyfile은 없는 루트 경로 `Caddyfile`로, AdminSurfaceMiddleware는 Program.cs로, MarkdownRenderer는 RenderGate.cs로, AppDbContext는 PostEndpoints.cs로 연결돼 있다. |
| features/F008_ADMIN_TAG_MANAGEMENT.md #F008_FLOW | [diagram:INCORRECT_DIAGRAM_RELATION] 간선 `PostEndpoints --> Validate`와 `Validate -->\|통과\| BeginTransaction`이 코드와 다르다. UpdateAsync의 글 조회·404(189-190), version 사전 비교 409(201), RenderOrAddErrorAsync 400/503(133-134, 206-207), CreateAsync의 slug 사전 검사 409(136)가 검증과 트랜잭션 사이·앞에 있는데 다이어그램에 빠져 있다. 또한 검증 노드 라벨이 TagResolver.Validate인데 실제 검증 단계는 PostValidation.Validate(+ ValidateSeriesAsync, 수정 시 slug·version 검사)이며 TagResolver.Validate는 그 일부다. |
| 10_ERROR_HANDLING.md #ERR_FLOW | [diagram:INCORRECT_DIAGRAM_RELATION] 설명은 Program 노드를 'UseExceptionHandler 기본 처리(500)'라고 하는데, 코드 근거 표는 이 노드를 `Program.cs (UseStatusCodePages)`에 연결한다. '그 밖의 예외' 간선이 도착하는 곳은 Program.cs 100행 UseExceptionHandler다. UseStatusCodePages(101행)가 아니다. |
| 10_ERROR_HANDLING.md #ERR_FLOW | [diagram:DIAGRAM_ABSTRACT_NODE] HttpResponse 노드는 코드의 클래스나 파일이 아니라 추상 개념이다. 그런데 코드 근거 표는 이 노드를 `ErrorResponses.cs`에 연결해 ErrorResponses 노드와 겹친다. AdminSurfaceMiddleware·DbConflict는 ErrorResponses를 거치지 않는다고 설명하면서 같은 파일을 근거로 댄 것은 모순이다. |
| 08_API.md #table | [unsupported:UNSUPPORTED_FAILURE_PATH] POST /api/posts와 PUT /api/posts/{id:guid}의 오류 열에 'DB 57014/55P03 → 503'이 있지만, 이 엔드포인트들이 쓰는 관리 연결(AppDbContext)에는 statement_timeout이 설정되어 있지 않습니다. statement_timeout은 PublicDbContext 연결에만 붙고, StartupValidation은 연결 문자열의 Options를 금지합니다. lock_timeout은 AttachmentLock 안에서만 SET되며, deploy 스크립트에도 설정이 없습니다. OverloadExceptionHandler가 이 코드를 503으로 바꾸는 것은 사실이지만, 글 저장 경로에서 이 예외가 나는 코드 경로는 확인되지 않습니다. 이 엔드포인트들에서 확인된 503 원인은 RenderBusyException뿐입니다. |
| 10_ERROR_HANDLING.md #unknowns | [unsupported:SELF_CONTRADICTION] unknowns에는 '워크스페이스 발췌가 F016에서 잘려 F017 이후 기능의 실패 경로는 반영하지 못함'이라고 적혀 있다. 하지만 본문 ERR012(HighlightCss, F017), ERR008(F009 janitor 연계), 기동 실패 경로(F021), AttachmentJanitor(F024)는 F017 이후 기능의 실패 경로를 이미 다룬다. |
<!-- /doc-harness:section -->
