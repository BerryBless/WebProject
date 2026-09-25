# 기술 부채

<!-- doc-harness:section id="summary" hash="5cbccb5534a449ba8137f76e82ab797bb445debd43e09e5d3af5945bc4d5ddaa" -->
## 한 줄 요약

코드 마커 검색에서 운영 코드의 TODO/FIXME/HACK은 없었다. 유일한 'TODO'는 MarkdownRendererTests.cs:291의 테스트 입력 문자열이다. 확인된 확정 결함은 적고, 대부분은 '처리하지 않고 전파'하는 실패 경로와 클라이언트 충돌 처리의 문구 불일치다. 잘 되어 있는 부분: (1) 57014·55P03·RenderBusyException을 OverloadExceptionHandler가 503+Retry-After로 일원화한다. (2) 관리 표면은 AdminSurfaceMiddleware가 호스트·IP·CSRF·Origin을 본문 읽기 전에 거부하고 속도 제한 예산도 쓰지 않는다. (3) 동시성 충돌(xmin, 23505, 23503)은 409로 변환하고 트랜잭션은 dispose 롤백에 맡긴다. (4) StartupValidation이 설정 오류로 기동을 막는다. (5) 렌더 슬롯·강조 시간·중첩 깊이·입력 크기에 상한이 있다. (6) 공개 DB 컨텍스트는 읽기 전용이다. 이 분석은 주어진 힌트와 코드 마커에 기반하며 줄 번호는 힌트에 명시된 것만 적었다.

확인된 문제 0 · 잠재 위험 11 · 개선 제안 3
<!-- /doc-harness:section -->

<!-- doc-harness:section id="confirmed" hash="2a8441c89de7c0806b48f2d1dffab7ed2c6c21793962a5578b34bcc14468a25f" -->
## 확인된 문제(CONFIRMED_ISSUE)

_(없음)_
<!-- /doc-harness:section -->

<!-- doc-harness:section id="potential" hash="f66408d04bf35d50fc174134cd8e68e21fd692ea6e81f6c56e97c6d7c5778eef" -->
## 잠재 위험(POTENTIAL_RISK)

| ID | 제목 | 설명 | 권고 | 기능 | 근거 |
|---|---|---|---|---|---|
| DEBT001 | 57014·55P03 외 DB 예외는 엔드포인트 차원의 처리 없이 500으로 전파된다 | 관리 API(Auth, Posts, Series, Tags, Attachments), 공개 페이지, 피드, sitemap 어디에도 DB 연결 실패나 CHECK 위반(23514) 같은 예외를 잡는 코드가 없다. OverloadExceptionHandler는 57014·55P03과 RenderBusyException만 503으로 바꾼다. 그 밖의 예외는 UseExceptionHandler 기본 처리(500)로 넘어간다. 공개 HTML 경로에서 이 500의 본문이 고정 HTML인지 ProblemDetails인지는 코드로 확정하지 못했다. SessionValidator의 SingleAsync도 같은 성격이라, DB 장애 때 쿠키가 붙은 모든 관리 요청과 /me가 오류 응답이 될 수 있다. | 500 응답의 본문 형식을 경로 종류(HTML/API)별로 테스트로 고정한다. 인증 단계의 DB 장애가 /me 계약(항상 200)을 깨는지 정책을 정하고, 필요하면 SessionValidator에서 명시적으로 처리한다. | F001, F002, F003, F008, F009, F010, F012, F013, F014, F015, F016, F017 | `PortfolioBlog.Api/Infrastructure/Web/OverloadExceptionHandler.cs` OverloadExceptionHandler.IsOverload, `PortfolioBlog.Api/Infrastructure/Access/SessionValidator.cs` SessionValidator.ValidateAsync, `PortfolioBlog.Api.Tests/Features/ErrorPipelineTests.cs` (67) |
| DEBT002 | 렌더 실행은 취소·타임아웃이 없는 동기 CPU 작업이다 | RenderGate는 슬롯 획득까지만 시간 제한(QueueTimeoutMs)을 둔다. 슬롯을 얻은 뒤 Markdig 파싱은 취소할 수 없고 타임아웃도 없다. 적대적 입력이면 문서상 최대 약 8.5초 동안 슬롯과 스레드를 점유한다. 방어는 동시성 제한(기본 2)과 분당 한도, 입력 크기·중첩 상한뿐이다. 이 슬롯은 미리보기, 저장, 공개 렌더가 공유하므로 미리보기 남용이 공개 글 렌더 지연으로 번질 수 있다. 캐시 저장이 조용히 거부되는 경우에도 같은 비용이 반복된다. | 미리보기와 공개 렌더의 슬롯 예산을 분리하거나, 렌더 시간 상한을 측정 지표로 남기는 것을 검토한다. | F004, F011, F013 | `PortfolioBlog.Api/Infrastructure/Markdown/RenderGate.cs` RenderGate.RenderAsync, `PortfolioBlog.Api/Infrastructure/Markdown/MarkdownRenderer.cs` MarkdownRenderer.RenderDetailed (138-143) |
| DEBT004 | 에디터의 409 처리가 원인과 무관하게 status만 보고 분기해 안내가 어긋날 수 있다 | Editor.save.onError는 409이면 원인(version 불일치, 참조 삭제 제약 경쟁, 태그 삭제로 인한 오탐 StaleVersion)을 구분하지 않고 최신본을 재조회해 ConflictPanel을 띄운다. 이때 서버 detail은 숨겨지고, '내 내용 유지' 후 재저장하면 같은 FK 원인으로 다시 실패할 수 있다. 반대로 새 글(postId null)의 409는 ConflictPanel 없이 일반 오류로만 표시된다. 서버 쪽에서도 UpdateAsync가 사라진 PostTag 행 때문에 DbUpdateConcurrencyException을 내면 글 Version이 바뀌지 않았는데 '먼저 수정되었습니다' 문구가 나갈 수 있다(EF 동작 추론, 재현 테스트 없음). | 409 응답에 원인 구분 값(예: 에러 코드)을 넣어 클라이언트가 version 충돌과 참조 경쟁을 나누어 처리하게 한다. 태그 삭제 경쟁은 재현 테스트를 추가해 추론을 확정한다. | F003, F006, F008 | `PortfolioBlog.Web/src/pages/PostEditorPage.tsx` Editor.save.onError, `PortfolioBlog.Api/Features/Posts/PostEndpoints.cs` PostEndpoints.UpdateAsync (215-233), `PortfolioBlog.Api/Infrastructure/Data/DbConflict.cs` DbConflict |
| DEBT005 | 로그아웃이 영향 행 수를 확인하지 않아 실패해도 204를 반환한다 | LogoutAsync의 ExecuteUpdateAsync가 0행을 갱신해도(AdminState 행 부재) epoch가 오르지 않아 다른 세션이 폐기되지 않는데 204와 쿠키 삭제만 수행한다. 행 부재는 CHECK 제약과 HasData 시드로 줄여 두었으나 처리 코드는 없다. | 영향 행이 0이면 로그를 남기거나 오류로 처리한다. | F001 | `PortfolioBlog.Api/Features/Auth/AuthEndpoints.cs` AuthEndpoints.LogoutAsync |
| DEBT006 | 글 저장 경로에서 비싼 렌더가 값싼 검사보다 먼저 실행되고, 커밋 이후 재조회 실패에 대한 처리가 없다 | CreateAsync는 slug 중복 사전 검사를 렌더 뒤에 실행하므로 중복 요청도 렌더 CPU를 먼저 쓴다. 또 tx.CommitAsync 이후 GetDetailAsync 재조회가 예외를 던지거나 null이면 행은 이미 저장·공개되었는데 클라이언트는 실패나 빈 본문을 받는다. 생성이라면 재시도가 slug 409가 된다. TagResolver.ResolveIdsAsync 호출은 UpdateAsync의 try 블록 밖이라 엔드포인트의 catch가 적용되지 않는다. | slug 사전 검사를 렌더 앞으로 옮기고, 커밋 후 재조회 실패를 별도로 다룬다(예: 201/200과 Location만 반환). | F003, F006, F008 | `PortfolioBlog.Api/Features/Posts/PostEndpoints.cs` PostEndpoints.CreateAsync (133-136), `PortfolioBlog.Api/Features/Posts/PostEndpoints.cs` PostEndpoints.UpdateAsync (213-226) |
| DEBT007 | 첨부 업로드의 보상 삭제 부재와 잠금 해제 실패의 침묵 | 잠금 밖에서 내용 주소 파일을 옮긴 뒤 행 INSERT 없이 요청이 끝나면 참조 없는 파일이 남는다. AttachmentJanitor가 1시간 경과 후 6시간 주기로 정리한다. 23505 폴백에서 행이 사라졌다면 SingleAsync가 InvalidOperationException으로 500이 된다. Releaser.DisposeAsync는 pg_advisory_unlock 실패를 로그 없이 삼켜 같은 sha256 요청이 503을 받을 수 있다. 공개 조회 쪽은 StoragePath가 루트 밖이면 try 밖에서 500이 되고, UnauthorizedAccess·기타 IO 예외도 처리가 없다. | unlock 실패 로그를 남기고, 23505 폴백은 SingleOrDefault로 바꿔 재시도 또는 409로 처리한다. 매직 값(1시간, 6시간, 10초)은 옵션으로 노출 여부를 점검한다. | F009, F010 | `PortfolioBlog.Api/Features/Attachments/AttachmentEndpoints.cs` AttachmentEndpoints.UploadAsync, `PortfolioBlog.Api/Infrastructure/Storage/AttachmentLock.cs` AttachmentLock.Releaser.DisposeAsync, `PortfolioBlog.Api/Infrastructure/Storage/AttachmentJanitor.cs` AttachmentJanitor, `PortfolioBlog.Api/Features/Attachments/PublicAttachmentEndpoints.cs` PublicAttachmentEndpoints.GetAsync |
| DEBT008 | MarkdownRenderer의 입력 크기 검사가 try 밖이며 공개 경로는 DB 제약에 의존한다 | null이거나 MaxInputBytes를 넘는 입력은 MarkdownTooComplexException이 아닌 일반 예외를 던진다. 저장·미리보기는 사전 검증으로 막고 공개 PostModel은 이를 잡지 않으며, DB CHECK(CK_Posts_Content_Size)가 같은 한도를 강제하는 데 의존한다. 제약이 우회되거나 한도가 어긋나면 공개 글이 500이 된다. | 공개 경로에서도 크기 초과를 렌더 불가 폴백으로 통일하거나, 한도 상수를 코드와 마이그레이션이 공유함을 테스트로 고정한다. | F011, F013 | `PortfolioBlog.Api/Infrastructure/Markdown/MarkdownRenderer.cs` MarkdownRenderer.RenderDetailed, `PortfolioBlog.Api/Pages/Post.cshtml.cs` PostModel.RenderAsync |
| DEBT009 | slug·페이지 상한 등 규칙이 .NET·DB·클라이언트에 중복되어 서로 어긋날 수 있다 | slug 패턴은 SlugRules(.NET), DB CHECK(CK_Posts_Slug_Format, CK_Series_Slug_Format)에 이중으로 있고, 클라이언트 validation.ts에도 형식 검증이 있을 것으로 보인다. sitemap·피드의 XML 안전성이 이 일치에 의존한다. 페이지 상한은 페이지별로 다르다(홈·태그 500, 검색 50)이고 PublicQueries.PageAsync가 심층 방어로 다시 검사한다. 본문·설명 길이 한도도 서버 TextRules와 클라이언트가 각각 정의한다. | 한도 상수를 한 곳에서 생성·공유하거나, 서버-클라이언트-DB 값 일치를 검증하는 테스트를 둔다. | F012, F013, F014, F016, F017 | `PortfolioBlog.Api/Infrastructure/Data/SlugRules.cs` SlugRules.IsValid, `PortfolioBlog.Api/Pages/PageNumber.cs` PageNumber.TryRead, `PortfolioBlog.Web/src/lib/validation.ts`, `PortfolioBlog.Api/Contracts/TextRules.cs` |
| DEBT010 | Lazy<string> HighlightCss가 첫 실패를 캐시할 수 있다 | Lazy 기본 모드는 예외를 캐시하므로 첫 Build()가 실패하면 재시작 전까지 /css/highlight.css가 계속 500일 수 있다(추론, 미측정). | 실패를 캐시하지 않는 모드(LazyThreadSafetyMode.PublicationOnly)나 기동 시 사전 빌드를 검토한다. | F017 | `PortfolioBlog.Api/Infrastructure/Markdown/HighlightCss.cs` HighlightCss.Value |
| DEBT011 | drafts.ts의 기본 인자 window.localStorage가 try 밖이며 clearDraft 실패는 삼켜진다 | 저장소 접근 자체가 예외를 던지는 브라우저 설정에서는 기본 인자가 try 블록 밖에서 평가되어 Editor 렌더가 실패하고 RouteError로 갈 것으로 추론한다(미재현). clearDraft의 removeItem 실패는 삼켜져 새 글 임시본이 남고, 복원 후 재저장하면 slug 409 중복 생성으로 이어질 수 있다. 코드 주석이 '미검증 잔여 위험'으로 인정한다. flushDraft는 saveDraft 실패 반환값을 무시한다. | storage 획득을 try 안으로 옮기고, 종료 시점 저장 실패를 사용자에게 알린다. | F005 | `PortfolioBlog.Web/src/lib/drafts.ts` loadDraft/saveDraft/clearDraft, `PortfolioBlog.Web/src/pages/PostEditorPage.tsx` flushDraft |
| DEBT012 | previewCsp가 렌더 중 예외를 던질 수 있다 | window.location.origin이 ORIGIN_PATTERN에 맞지 않으면 Error를 던지며, PreviewPane의 useMemo 안이라 렌더 중 예외가 되어 오류 경계로 전파된다. | 예외 대신 안전한 기본 CSP 또는 미리보기 비활성 상태로 폴백한다. | F004 | `PortfolioBlog.Web/src/lib/previewDoc.ts` previewCsp, `PortfolioBlog.Web/src/components/PreviewPane.tsx` |
<!-- /doc-harness:section -->

<!-- doc-harness:section id="improvement" hash="ecf9aadba241e7c707f12641ff5869082d047135b49b4df9e5ea2c3697e0ad23" -->
## 개선 제안(IMPROVEMENT)

| ID | 제목 | 설명 | 권고 | 기능 | 근거 |
|---|---|---|---|---|---|
| DEBT003 | 렌더 캐시 저장 거부와 공개 페이지의 렌더 실패 폴백이 로그 없이 조용히 일어난다 | MemoryCache SizeLimit을 넘으면 Set이 예외 없이 거부되며 로그가 없다. PostModel.RenderAsync도 MarkdownTooComplexException을 잡아 null 폴백하지만 로그를 남기지 않는다. 운영자는 캐시가 계속 거부되어 렌더 비용이 반복되는지, 저장된 글이 렌더 불가가 되었는지 알 수 없다. | 캐시 거부와 렌더 폴백에 카운터 또는 경고 로그를 추가한다. | F011, F013 | `PortfolioBlog.Api/Infrastructure/Markdown/RenderedPostCache.cs` RenderedPostCache.Store, `PortfolioBlog.Api/Pages/Post.cshtml.cs` PostModel.RenderAsync |
| DEBT013 | 취소(OperationCanceledException)의 최종 응답이 코드로 확정되지 않았다 | RenderGate 대기, RenderedPostCache 대기, DB 조회 등에서 요청 취소가 전파되지만 앱에는 전용 처리가 없고 OverloadExceptionHandler는 취소와 57014의 경합을 '미검증'으로 남겼다. 최종 상태 코드와 로그 수준을 확인하지 못했다. | 취소 요청이 500으로 기록·집계되지 않는지 테스트로 확인하고 필요하면 예외 처리기에서 명시적으로 분류한다. | F004, F011, F013, F014 | `PortfolioBlog.Api/Infrastructure/Web/OverloadExceptionHandler.cs` OverloadExceptionHandler.TryHandleAsync, `PortfolioBlog.Api/Infrastructure/Markdown/RenderedPostCache.cs` RenderedPostCache.GetOrRenderAsync |
| DEBT014 | 일부 경로에 전용 테스트가 없거나 추론에 그친다 | version 쿼리 비숫자 값의 400은 전용 테스트가 없다(F002). PreviewRequest 잘못된 JSON·415 응답 본문 형태는 프레임워크 동작 추론이다. 태그 삭제 경쟁으로 인한 StaleVersion 오탐과 highlight.css의 관리 호스트 404도 재현 테스트가 없다. 429 본문이 HTML인 것도 추론이다(F012). | 해당 경로의 통합 테스트를 추가해 추론을 확정한다. | F002, F004, F008, F012 | `PortfolioBlog.Api/Features/Preview/PreviewEndpoints.cs` PreviewEndpoints.Render, `PortfolioBlog.Api/Features/Posts/PostEndpoints.cs` PostEndpoints.DeleteAsync, `PortfolioBlog.Api/Infrastructure/Web/RateLimitingExtensions.cs` |
<!-- /doc-harness:section -->

<!-- doc-harness:section id="related" hash="08f5095107d4ab7f6827e95cfd91aa6dd16d3a3a71f5d8e778a60a8498ac007a" -->
## 관련 문서

- [14_PERFORMANCE](14_PERFORMANCE.md)
- [13_SECURITY](13_SECURITY.md)
- [10_ERROR_HANDLING](10_ERROR_HANDLING.md)
- [19_UNKNOWN_AND_TODO](19_UNKNOWN_AND_TODO.md)
<!-- /doc-harness:section -->
