# ADR-008 내용 주소 첨부 저장과 DB 세션 잠금 기반 직렬화

<!-- doc-harness:section id="summary" hash="32849d8a61430591b6d368c964506fe67d6fb24049d45b312389a12150cfdf74" -->
## 결정

첨부는 SHA-256 내용 주소 파일로 저장하고, 행 삽입·삭제·고아 청소를 AttachmentLock.HoldAsync(sha256) 안에서 직렬화한다. 주기적 AttachmentJanitor가 남은 임시·고아 파일을 정리한다.

상태: CONFIRMED
<!-- /doc-harness:section -->

<!-- doc-harness:section id="rationale" hash="3e02a30fbc3d4feb395d6322eb3b8a6985022d6e27505a9f03559fb8f2396d7a" -->
## 근거

같은 내용의 업로드와 삭제·청소가 겹쳐도 참조 중인 파일을 지우지 않게 하고, 행 삽입 실패나 파일 삭제 실패로 남은 파일을 나중에 회수한다.

코드 근거: `PortfolioBlog.Api/Infrastructure/Storage/AttachmentJanitor.cs` (14,95-115), `PortfolioBlog.Api/Features/Attachments/AttachmentEndpoints.cs` (107-108,142,193-204)
<!-- /doc-harness:section -->

<!-- doc-harness:section id="related" hash="0f998da0a227b7a48fa6f319da09156836edc58ec60f61d1512cb17c087d413e" -->
## 관련 문서

- [../02_ARCHITECTURE](../02_ARCHITECTURE.md)
<!-- /doc-harness:section -->
