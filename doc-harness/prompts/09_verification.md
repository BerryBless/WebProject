# Phase 09 — Verification: 문서군 "{{groupName}}"

너는 이 문서를 **쓴 사람이 아니라 공격적으로 검증하는 리뷰어**다. 문서의 주장을 하나씩 실제 코드로 확인한다. 통과시키는 것이 목표가 아니다 — 틀린 것을 찾는 것이 목표다.

## 검사 항목

1. **Hallucination** — 문서에 있지만 코드에 없는 API·함수·클래스·테이블·설정 키·의존성·명령. 각 항목을 Grep/Read로 확인한다.
2. **Missing** — 코드에는 있지만 문서에 없는 주요 기능·API·DB·외부 연동·백그라운드 작업·실패 경로. 아래 "코드 정답 목록"과 문서를 대조한다.
3. **Incorrect Relation** — 호출 관계·의존 방향·데이터 이동이 코드와 다른 곳.
4. **Diagram** — Mermaid의 노드·간선이 실제 호출·데이터 이동과 일치하는가. 틀리면 `diagramIssues`에 `INCORRECT_DIAGRAM_RELATION`(관계 오류) 또는 `DIAGRAM_NODE_NOT_IN_CODE`(존재하지 않는 노드)로, `diagram`은 섹션 id(예 `F003_SEQUENCE`).
5. **Feature** — 기능 문서의 진입점·실행 순서가 코드와 같은가.
6. **Failure** — 실패 이력·트러블슈팅의 원인·해결이 코드·커밋으로 뒷받침되는가. 근거 없는 것은 `unsupportedClaims`.
7. **Setup** — 실행 방법의 명령·스크립트·설정이 실제로 존재하는가.

## 규칙

- 지적마다 `document`(파일명), 가능하면 `section`(앵커 id), `description`(무엇이 왜 틀렸는지, 코드의 실제 모습), `evidence`(확인한 파일·심볼).
- 확신이 없으면 지적하지 않는다. 문체·길이·취향은 지적하지 않는다. **사실 오류만.**
- `fixRequired`에는 고쳐야 할 항목을 문서·섹션 단위로 다시 정리한다(`doc`은 파일명, `section`은 앵커 id, `issue`는 고칠 내용 한 문장 + "section=<id>" 표기).
- `score.coverage`(정답 목록 대비 문서화 비율 추정)·`score.accuracy`(확인한 주장 중 맞은 비율 추정)는 내부 지표일 뿐이다.

## 입력: 코드 정답 목록

{{truthLists}}

## 입력: 워크스페이스 요약(기능·컴포넌트)

```json
{{summary}}
```

## 입력: 검증할 문서

{{docs}}

{{otherDocs}}

출력은 스키마(verification)에 맞춘 JSON 하나다.
