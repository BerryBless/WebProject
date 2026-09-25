# ADR-003 공개 조회 전용 DB 롤과 읽기 전용 컨텍스트

<!-- doc-harness:section id="summary" hash="dcf7fe564a6ec338cce3492aca4e25460befce3358e143a09b952273f8685f53" -->
## 결정

공개 경로는 PublicDbContext(blog_public 롤, statement_timeout, default_transaction_read_only=on, SaveChanges 금지, NoTracking)만 쓴다. 기동할 때마다 PublicRoleGrants가 공개 롤 권한을 허용 테이블의 SELECT로 다시 맞춘다.

상태: CONFIRMED
<!-- /doc-harness:section -->

<!-- doc-harness:section id="rationale" hash="cbe4daf9d73c7d008e34a2b601c44babd9a4cba6bf8917328e37f1b9feffc25a" -->
## 근거

쓰기 금지를 앱 예외, DB 세션 옵션, DB 권한의 세 겹으로 둔다. 별도 연결 문자열이라 Npgsql 풀도 분리돼, 관리 작업이 테이블을 잠가도 공개 요청은 시간 제한 안에 503으로 끝나고 관리 풀을 잡아먹지 않는다.

코드 근거: `PortfolioBlog.Api/Infrastructure/Data/PublicDbContext.cs` (6-28,46-62,76-91), `PortfolioBlog.Api/Infrastructure/Data/PublicRoleGrants.cs` (7-21,117-138), `PortfolioBlog.Api/Features/Attachments/PublicAttachmentEndpoints.cs` (79-81)
<!-- /doc-harness:section -->

<!-- doc-harness:section id="related" hash="0f998da0a227b7a48fa6f319da09156836edc58ec60f61d1512cb17c087d413e" -->
## 관련 문서

- [../02_ARCHITECTURE](../02_ARCHITECTURE.md)
<!-- /doc-harness:section -->
