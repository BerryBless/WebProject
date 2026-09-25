# ADR-005 렌더 HTML은 DB에 저장하지 않고 프로세스 메모리에만 캐시

<!-- doc-harness:section id="summary" hash="d4d6f99694394b5b9e2fe30826055d24c8c19575acbbebf4306758fbe985e187" -->
## 결정

RenderedPostCache가 (PostId, xmin) 키로 전용 MemoryCache에 렌더 결과를 보관한다(정상 24시간, 강조가 빠진 결과 2분). 같은 키의 동시 미스는 단일 비행으로 합친다.

상태: CONFIRMED
<!-- /doc-harness:section -->

<!-- doc-harness:section id="rationale" hash="2ef6020c275d430ff82b16f0752b2a318dbb89ef2c50c90731d644465460b2df" -->
## 근거

코드 주석에 따르면 재배포 시 캐시가 비워지므로 렌더러 보안 수정이 과거 글 전체에 즉시 적용된다(스펙 3.2). xmin이 키에 들어 있어 글을 수정하면 자동으로 미스가 난다.

코드 근거: `PortfolioBlog.Api/Infrastructure/Markdown/RenderedPostCache.cs` (7-23,95-101)
<!-- /doc-harness:section -->

<!-- doc-harness:section id="related" hash="0f998da0a227b7a48fa6f319da09156836edc58ec60f61d1512cb17c087d413e" -->
## 관련 문서

- [../02_ARCHITECTURE](../02_ARCHITECTURE.md)
<!-- /doc-harness:section -->
