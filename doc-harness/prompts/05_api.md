# Phase 05b — API / Interface Analysis

목표: 프로젝트가 밖으로 드러내는 **모든 인터페이스**를 표로 만든다. HTTP(API·Razor 페이지·정적 파일·피드), WebSocket, RPC, 큐, 이벤트, IPC, CLI 스크립트.

## 엔드포인트마다

`method` · `path` · `host`(어느 호스트/사이트에서 서빙되는지: 예 `public`, `admin`, `both`) · `caller`(누가 호출: SPA/브라우저/크롤러/스크립트) · `file`·`symbol` · `request`(본문·쿼리·헤더 요약) · `response` · `validation` · `authentication`(세션·IP 허용·없음) · `sideEffects` · `dbChanges` · `errors`(상태 코드와 조건) · `featureIds` · `status` · `evidence`.

- 아래 "코드에서 추출한 엔드포인트"는 **전부** 표에 있어야 한다. 정규식이 못 잡은 엔드포인트(예: `MapGroup` 접두사, Razor 규약 라우트, 정적 파일)도 코드를 읽어 추가한다.
- `MapGroup`으로 접두사가 붙으면 최종 경로로 적는다.
- Razor 페이지는 `method: "GET"`, `path`는 실제 라우트.
- HTTP가 아닌 인터페이스(스크립트·컨테이너 헬스체크 등)는 `otherInterfaces`에 claim으로.

## 입력: 엔드포인트·페이지 파일(먼저 읽을 것)

{{apiFiles}}

## 입력: 코드에서 추출한 정답 목록

{{truthLists}}

## 입력: 기능 목록(featureIds 매핑용)

```json
{{features}}
```

출력은 스키마(api)에 맞춘 JSON 하나다.
