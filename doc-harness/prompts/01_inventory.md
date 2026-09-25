# Phase 01 — Inventory(지도 만들기)

이 단계의 목표는 **프로젝트에 무엇이 존재하는지** 파악하는 것이다. 기능을 상세 분석하지 않는다. 지도만 만든다.

## 조사 항목

언어 · 프레임워크 · 런타임 · 진입점(Entry point) · 디렉터리 역할 · 소스 위치 · 설정 파일 · 환경(개발/운영 프로필) · 빌드 · 의존성(패키지 매니페스트) · 데이터베이스 · 캐시 · 큐 · 외부 API/시스템 · 컨테이너(Docker/Compose) · CI/CD · 테스트 · 스크립트 · 마이그레이션 · 정적 자원 · 개발 도구(하네스·훅·스킬).

각 항목은 `{ text, status, evidence[] }` claim이다. `text`는 "무엇이 어디에 있고 무슨 역할인지" 한 문장. evidence는 실제 파일 경로.

## 규칙

- 매니페스트(`*.csproj`, `package.json`, `Directory.Packages.props`, compose, CI yml, Dockerfile)를 **직접 Read**해서 프레임워크·버전·의존성을 확인한다. 추측 금지.
- 설정 파일은 키 이름만 언급한다. 값(비밀번호·연결 문자열·IP)은 출력하지 않는다.
- `tooling` 항목에는 `{{toolingDirs}}` 아래의 개발 도구(에이전트·스킬·훅·스크립트·설계 문서)를 요약한다. 이들은 제품 기능이 아니다.
- `directories`에는 최상위와 2단계 디렉터리의 역할을 적는다.
- 프로젝트 이름·한 줄 설명은 솔루션 파일과 README에서 확인하되 README는 INFERRED다.

## 입력: 파일 트리(경로만)

```text
{{fileTree}}
```

## 입력: 매니페스트·배포·CI 파일(먼저 읽을 것)

{{manifests}}

출력은 스키마(inventory)에 맞춘 JSON 하나다.
