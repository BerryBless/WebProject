# ADR-006 과부하는 500이 아니라 503 + Retry-After로

<!-- doc-harness:section id="summary" hash="db7d300d17a2976a60514f9e5d87504134a440fbaf23755496a378948bb20ef1" -->
## 결정

RenderGate가 동시 렌더 수를 전역으로 제한하고, 속도 제한은 대기열 0으로 즉시 429를 낸다. statement_timeout(57014)·lock_timeout(55P03)·RenderBusyException은 OverloadExceptionHandler가 503 + Retry-After 5로 바꾼다.

상태: CONFIRMED
<!-- /doc-harness:section -->

<!-- doc-harness:section id="rationale" hash="e1833879f744e1866559b4e0b20c6da71f60c20639bdd99306d596dc7f812253" -->
## 근거

렌더는 동기·취소 불가 CPU 작업이라(주석상 최악 수 초) 스레드 풀 고갈을 막아야 한다. 포기한 요청은 재시도 가능하다는 신호를 준다.

코드 근거: `PortfolioBlog.Api/Infrastructure/Markdown/RenderGate.cs` (16-24), `PortfolioBlog.Api/Infrastructure/Web/OverloadExceptionHandler.cs` (8-70), `PortfolioBlog.Api/Infrastructure/Web/RateLimitingExtensions.cs` (85-101)
<!-- /doc-harness:section -->

<!-- doc-harness:section id="related" hash="0f998da0a227b7a48fa6f319da09156836edc58ec60f61d1512cb17c087d413e" -->
## 관련 문서

- [../02_ARCHITECTURE](../02_ARCHITECTURE.md)
<!-- /doc-harness:section -->
