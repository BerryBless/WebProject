# ADR-002 관리 표면의 다층 방어 (Caddy IP 게이트 + 앱 필터 + 쿠키 + CSRF)

<!-- doc-harness:section id="summary" hash="ade218bf45a90e93bc9027c0ddec274ab9461b663f7bc126370232c924a25f67" -->
## 결정

관리 도메인은 Caddy에서 remote_ip로 1차 차단(404)한다. 앱에서는 AdminSurfaceMiddleware가 호스트·IP·X-Requested-With·Origin을 본문 읽기 전에 검사하고, RequireHost와 Admin 인가 정책, SessionValidator의 epoch·지문 검증을 차례로 적용한다.

상태: CONFIRMED
<!-- /doc-harness:section -->

<!-- doc-harness:section id="rationale" hash="3677542cdd2c378d4027e8af89ae291369835e3affc82f9360f299fef0d96962" -->
## 근거

코드 주석에 따르면 IP 허용만으로는 CSRF를 막지 못한다. 이 앱은 CORS를 등록하지 않으므로 커스텀 헤더를 필수로 하면 교차 출처 요청이 막히고, Origin 검사는 같은 사이트의 다른 origin(공개 도메인)을 막는 2차 방어다. 쿠키는 __Host- 접두사와 SameSite Strict로 형제 서브도메인의 덮어쓰기와 전송을 막는다.

코드 근거: `PortfolioBlog.Api/Infrastructure/Access/AdminSurfaceMiddleware.cs` (5-18,67-96), `PortfolioBlog.Api/Infrastructure/Access/AuthServiceCollectionExtensions.cs` (54-72), `deploy/Caddyfile` (87-88,169-170)
<!-- /doc-harness:section -->

<!-- doc-harness:section id="related" hash="0f998da0a227b7a48fa6f319da09156836edc58ec60f61d1512cb17c087d413e" -->
## 관련 문서

- [../02_ARCHITECTURE](../02_ARCHITECTURE.md)
<!-- /doc-harness:section -->
