# ADR-001 공개 사이트는 JS 없는 서버 렌더링, 관리 화면은 별도 서브도메인의 SPA

<!-- doc-harness:section id="summary" hash="dee8ab239a063c6bda0fc0b01d5c84ddb1fb974e628a92bbf56089069f5da916" -->
## 결정

공개 표면은 PortfolioBlog.Api의 Razor Pages와 SiteEndpoints(공개 호스트 전용, GET/HEAD 전용)로 렌더링한다. 관리 표면은 React SPA(PortfolioBlog.Web)를 Caddy가 관리 도메인에서 정적으로 서빙하고 /api만 백엔드로 보낸다.

상태: CONFIRMED
<!-- /doc-harness:section -->

<!-- doc-harness:section id="rationale" hash="9c89718761fbc213f2e392d4d217066ebedcdcf8e6b1f11b70349b20b46a51d5" -->
## 근거

공개 HTML의 CSP에 script-src가 전혀 없어(PublicCsp) 공개 표면에서 스크립트 실행 여지를 없앤다. 관리 기능은 호스트 단위로 분리해 IP 허용 목록과 __Host- 쿠키로 격리한다. RequireHost가 라우팅 수준에서도 두 표면이 섞이지 않게 막는다.

코드 근거: `PortfolioBlog.Api/Infrastructure/Web/SecurityHeadersMiddleware.cs` (15-16), `PortfolioBlog.Api/Pages/PublicPageConvention.cs` (32-41), `PortfolioBlog.Api/Features/ApiEndpoints.cs` (37-39), `deploy/Caddyfile` (19-20,78-143)
<!-- /doc-harness:section -->

<!-- doc-harness:section id="related" hash="0f998da0a227b7a48fa6f319da09156836edc58ec60f61d1512cb17c087d413e" -->
## 관련 문서

- [../02_ARCHITECTURE](../02_ARCHITECTURE.md)
<!-- /doc-harness:section -->
