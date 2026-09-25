# Phase I08 — Diagram 갱신 판정: {{diagramId}}

문서에 있는 **기존 Mermaid**(사람이 고쳤을 수 있음)와 새 분석 결과, 코드 변경을 비교해 다이어그램을 그대로 둘지 고칠지 판정한다.

## 규칙

- 기존 다이어그램이 현재 코드와 여전히 맞으면 `decision: "UNCHANGED"`, `mermaid`는 기존 것을 **바이트 그대로** 돌려준다.
- 호출 순서·분기·데이터 이동·상태·컴포넌트/DB 관계가 바뀌었으면 `decision: "UPDATED"`, **필요한 부분만** 고친 mermaid를 돌려준다. 통째로 다시 그리지 않는다(사람이 수정한 배치·이름을 최대한 보존).
- `changedEdges`에 바뀐 간선/노드를 `A --> B: 추가`처럼 나열. `nodes`에는 결과 mermaid의 주요 노드와 코드 경로.
- 실제 클래스·파일 이름, style/classDef/linkStyle 금지, 노드 25·간선 40 이하.
- 판단이 서지 않으면 관련 코드를 Read해 확인한다.

## 기존 Mermaid(문서 원본)

```mermaid
{{existing}}
```

## 새 분석이 제안한 Mermaid(참고 — 사실이 아니라 후보)

```mermaid
{{proposed}}
```

## 새 분석의 실행 흐름·데이터 흐름 요약

{{analysisSummary}}

## 코드 변경(hunk)

{{changeHints}}

출력은 스키마(diagram_update)에 맞춘 JSON 하나다.
