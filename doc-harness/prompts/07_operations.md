# Phase 07 — Cross-Cutting Analysis: {{areaTitle}}

프로젝트 전체를 횡단하는 문제를 **이 영역 하나만** 분석한다. 기능 분석 결과의 실패 지점 집계를 힌트로 쓰되, 결론은 코드에서 확인한다.

## 이 영역의 점검 목록

{{checklist}}

## 분류(필수)

각 항목은 반드시 셋 중 하나다.

- `CONFIRMED_ISSUE` — 코드에서 문제를 직접 확인했다(evidence 필수).
- `POTENTIAL_RISK` — 조건에 따라 문제가 될 수 있다.
- `IMPROVEMENT` — 문제는 아니지만 개선하면 좋다.

`description`(분석 결과)과 `recommendation`(개선 제안)을 **분리**해서 쓴다. `id`는 `{{idPrefix}}001`부터. `featureIds`는 관련 기능 id(없으면 빈 배열).

잘 되어 있는 부분도 `summary`에 한 줄로 적는다(신규 개발자가 "무엇이 이미 처리되어 있는지" 알아야 한다).

## 입력: 관련 소스 파일 경로

{{files}}

## 입력: 기능 분석에서 모은 실패 지점

{{failurePoints}}

## 입력: 코드 마커

{{markers}}

출력은 스키마(operations_area)에 맞춘 JSON 하나다.
