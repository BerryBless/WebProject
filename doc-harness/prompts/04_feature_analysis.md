# Phase 04 — Feature Deep Analysis: {{featureId}} {{featureName}}

이 세션은 **기능 하나만** 분석한다. 프로젝트 전체를 다시 훑지 말고, 관련 파일에서 시작해 호출 관계를 따라가며 필요한 파일만 Read한다.

## 분석 항목(모두 채운다. 해당 없음은 빈 배열, 모르면 unknowns)

목적 · 진입점 · 호출 관계 · **실제 실행 순서**(executionFlow: step 번호, 컴포넌트=실제 클래스명, 파일, 심볼, 설명) · 관련 파일/클래스/함수(relatedCode: role은 entry/service/data/dto/validation/render/config/test 중) · API · 입력/출력 · DTO · 엔티티 · DB 접근(databaseAccess: entity, operation=SELECT/INSERT/UPDATE/DELETE/DDL) · 캐시 · 파일 · 네트워크 · 외부 API · 이벤트 · 스레드/비동기 · 상태 변화(stateTransitions) · 검증(validation) · 오류 · 타임아웃 · 재시도 · 폴백 · 롤백 · 엣지 케이스 · 로깅.

**실패 경로를 정상 경로만큼 조사한다.** `failurePoints`에는 "어디서(where) 어떤 조건(condition)에 어떻게 처리(handling)되는지"를 적는다. 처리 코드가 없으면 `handling: "처리 없음(예외 전파)"`, status는 `POTENTIAL_ISSUE`.

## Diagram 선택(필요한 것만)

| 상황 | Diagram | id |
|---|---|---|
| 여러 컴포넌트를 순서대로 호출 | sequence | `{{featureId}}_SEQUENCE` |
| 조건/분기/재시도가 복잡 | flowchart | `{{featureId}}_FLOW` |
| 데이터가 변환·이동(입력→DTO→엔티티→DB) | dataflow | `{{featureId}}_DATAFLOW` |
| 상태를 가짐 | state | `{{featureId}}_STATE` |
| 객체 관계가 복잡 | class | `{{featureId}}_CLASS` |

- participant·노드 이름은 **실제 클래스·파일 이름**(예: `PostEndpoints`, `MarkdownPipeline`, `AppDbContext`). `Controller`·`Service`·`DB` 금지.
- `nodes[]`에 주요 노드마다 `code`(파일 경로, 실존) 적기. `nodes[].node` 문자열은 mermaid 본문에 그대로 있어야 한다.
- 노드 25·간선 40 초과 금지. 복잡하면 `{{featureId}}_FLOW_ERROR`처럼 Level을 나눈다.
- `summary`(결론 1~2문장)·`details`(상세) 필수. style/classDef/linkStyle 금지.

## 이력·의존에 관한 안내

- Git 이력은 별도 단계(Failure History)가 다룬다. 이 세션에서는 git 명령을 쓸 수 없고 쓸 필요도 없다. **최초 분석이면 `history`는 빈 배열**이며 이는 정상이다(unknowns에 적지 않는다).
- `feature.dependencies`는 아래 "기능 id 색인"의 id로 채운다. 코드로 확인한 의존만 넣고, 입력에 있던 id가 코드로 확인되지 않으면 뺀다.

## 재분석 규칙(이전 분석이 있을 때)

- 이전 분석은 **검증 대상이지 사실이 아니다.** 현재 코드가 Source of Truth다. 틀린 부분은 고친다.
- 동작·설계에 의미 있는 변경이 확인되면 `history`에 항목 1개를 추가한다: `date: {{today}}`, before/after(구조를 한 문장씩), `reason`(claim, 커밋·코드 근거), `impact`, `significance: MAJOR|MINOR`. 사소한 변경(이름·주석·포맷)은 `TRIVIAL`로 넣거나 생략한다.
- 이전 `history` 항목은 **그대로 유지**한다(삭제·재작성 금지).

## 입력: 기능

```json
{{feature}}
```

## 입력: 관련 파일(먼저 읽을 것)

{{relatedFiles}}

## 입력: 아키텍처 컴포넌트

```json
{{components}}
```

## 입력: 기능 id 색인(dependencies 매핑용)

{{featureIndex}}

## 입력: 이전 분석(재검증 대상)

{{previous}}

## 입력: 이 기능과 겹치는 변경(증분 실행일 때)

{{changeHints}}

## 입력: 세션 맥락

{{sessionContext}}

출력은 스키마(feature)에 맞춘 JSON 하나다. `feature` 객체는 입력 기능을 기반으로 하되 `relatedFiles`·`entryPoints`·`dependencies`를 코드로 확인해 보정하고, `analysisStatus`는 `SUCCESS`, `status`는 코드에서 실제로 동작하면 `ACTIVE`.
