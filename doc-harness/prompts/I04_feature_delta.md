# Phase I04 — Feature Delta(신규·변경·삭제 기능 판정)

변경 파일과 분류를 보고 **기존 기능 목록에 없는 새 기능**이 구현되었는지, 기존 기능이 바뀌었는지, 제거되었는지 판정한다.

## 신규 기능 탐지

새 API · 새 command/스크립트 · 새 UI action/화면 · 새 background worker · 새 event handler · 새 scheduler · 새 external integration. 발견하면 `newFeatures`에 기능 요약을 만든다(`id`는 아래 예약 id를 순서대로 사용, `slug` 대문자 스네이크, `analysisStatus: PENDING`, `status: ACTIVE`, `relatedFiles`는 실존 경로). 클래스 하나 = 기능 하나가 아니다. 기존 기능의 확장이면 신규가 아니라 `changedFeatureIds`다.

## 기존 기능 변경

관련 파일이 바뀐 기능은 모두 `changedFeatureIds`에 넣는다(재분석은 현재 코드 기준으로 전체 흐름을 다시 검증한다). 영향 분석이 이미 고른 기능 목록을 참고하되, 코드를 보고 빠진 것을 더한다.

## 삭제 판정

관련 파일이 전부 삭제됐거나 진입점이 코드에서 사라진 기능은 `removedFeatures`에 처분을 적는다: `REMOVED`(완전 제거) · `DEPRECATED`(코드는 있으나 사용 중단 표시) · `MIGRATED`(대체 기능으로 이전, `replacedBy`) · `PARTIAL`(일부 코드만 제거, 기능은 유지). 반드시 코드로 확인하고 `reason`에 근거를 남긴다. 확인이 안 되면 넣지 않고 `unknowns`에 남긴다.

## 입력: 기존 기능 목록

```json
{{features}}
```

## 입력: 예약된 신규 id

{{reservedIds}}

## 입력: 변경 분류

{{classification}}

## 입력: 영향 분석 결과(힌트)

```json
{{impact}}
```

## 입력: 변경 파일

{{changes}}

출력은 스키마(feature_delta)에 맞춘 JSON 하나다.
