# ADR-007 신뢰 프록시를 Caddy 고정 IP 하나로 제한

<!-- doc-harness:section id="summary" hash="207fa57919620014defb35e8b45d4233f70b79c4aeeeab48f9670f099318f6e4" -->
## 결정

ForwardedHeaders는 KnownIPNetworks와 KnownProxies를 비우고 Proxy:TrustedIp(172.30.0.2)만 넣으며 ForwardLimit=1이다. TrustedIp가 비어 있으면 미들웨어 자체를 등록하지 않는다.

상태: CONFIRMED
<!-- /doc-harness:section -->

<!-- doc-harness:section id="rationale" hash="52bef2363911524577efc69a1b2181a05ceafa57c6dcc70197f3c19b0dceb4d5" -->
## 근거

코드 주석에 따르면 목록이 둘 다 비어 있으면 ForwardedHeadersMiddleware가 송신자 검사를 생략해, X-Forwarded-For 위조로 IP 허용 목록을 우회할 수 있다.

코드 근거: `PortfolioBlog.Api/Infrastructure/Access/AccessServiceCollectionExtensions.cs` (37-75), `deploy/docker-compose.yml` (42-45,70)
<!-- /doc-harness:section -->

<!-- doc-harness:section id="related" hash="0f998da0a227b7a48fa6f319da09156836edc58ec60f61d1512cb17c087d413e" -->
## 관련 문서

- [../02_ARCHITECTURE](../02_ARCHITECTURE.md)
<!-- /doc-harness:section -->
