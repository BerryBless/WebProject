# Phase I02 — Change Classification(증분)

마지막 문서화 이후 바뀐 파일을 **의미 단위**로 분류한다. 파일 변경을 "파일이 바뀌었다"로만 보지 않는다.

## 분류(파일마다 1개 이상)

`NEW_FEATURE` `FEATURE_CHANGE` `BUG_FIX` `REFACTOR` `API_CHANGE` `DATA_MODEL_CHANGE` `CONFIG_CHANGE` `DEPENDENCY_CHANGE` `ARCHITECTURE_CHANGE` `ERROR_HANDLING_CHANGE` `PERFORMANCE_CHANGE` `SECURITY_CHANGE` `TEST_CHANGE` `DEPLOYMENT_CHANGE` `DOCUMENTATION_ONLY` `UNKNOWN`

- `significance`: 동작·구조·운영에 의미 있으면 `MAJOR`/`MINOR`, 이름·주석·포맷·문서만이면 `TRIVIAL`.
- `possibleFeatures`: 아래 기존 기능 목록에서 이 변경이 속할 만한 기능 id. 어디에도 안 맞고 새 진입점(엔드포인트·페이지·화면·백그라운드 작업·스크립트)이 생겼으면 `NEW_FEATURE`로 표시하고 비워 둔다.
- hunk만으로 판단이 안 되면 파일을 Read해서 확인한다. 그래도 모르면 `UNKNOWN`.
- `changelogCandidates`: 구조/동작/운영에 의미 있는 변화만 묶어서(파일 단위가 아니라 변화 단위로) 제목·설명·영향을 적는다. Git 로그 복사가 아니다.

## 입력: 기존 기능 목록

```json
{{features}}
```

## 입력: 변경 파일과 hunk

{{changes}}

## 입력: 세션 맥락

{{sessionContext}}

출력은 스키마(classification)에 맞춘 JSON 하나다. `items`에는 입력의 모든 변경 파일이 있어야 한다.
