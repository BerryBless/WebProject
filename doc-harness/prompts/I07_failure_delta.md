# Phase I07 — Failure / Decision Delta(증분)

마지막 문서화 이후의 변경에서 **실패 → 수정 → 다시 실패 → workaround → 현재 구현** 패턴과 의미 있는 구현 변화를 찾는다.

## 규칙

- 기존 실패 기록은 **삭제하거나 덮어쓰지 않는다.** 새 사례는 `newFailures`에 `{{nextFailureId}}`부터 번호를 이어 붙이고, 기존 사례에 새 사실(예: 최종 해결)이 생겼으면 같은 id로 `updatedFailures`에 전체를 다시 적는다.
- 커밋 메시지만으로 원인을 단정하지 않는다(`causeConfidence`). 없는 실패를 만들지 않는다.
- `troubleshooting`: 장애 재발 시 도움이 되는 항목만. `id`는 `TS` + 실패 id 번호(예 `TS007`) 또는 새 번호. 증상 → 원인 → 확인 방법 → 해결 → 관련 코드.
- `changelog`: **구조/동작/운영에 의미 있는 변화만**(Git 로그 복사 금지). `significance: TRIVIAL`은 넣지 않는다. `relatedDocs`는 `features/F###_SLUG.md`, `02_ARCHITECTURE.md` 같은 문서 파일명.
- 세션 맥락은 최하위 근거다. 코드·커밋으로 확인되지 않은 내용은 `unknowns`에 남긴다.

## 입력: 기존 실패 기록 id·제목

{{existingFailures}}

## 입력: 변경 분류 요약

{{classification}}

## 입력: 변경 파일과 hunk 발췌

{{changes}}

## 입력: Git 이력 발췌(baseline 이후)

{{gitHistory}}

## 입력: 코드 마커(변경 파일)

{{markers}}

## 입력: 세션 맥락

{{sessionContext}}

`discoveredAt`은 `{{today}}`. 출력은 스키마(failure_delta)에 맞춘 JSON 하나다.
