# Phase 03 — Feature Discovery

목표: **사용자/시스템 관점의 기능 목록**을 만든다. 클래스 하나 = 기능 하나가 아니다.

잘못된 분류: `AuthController`, `AuthService`, `AuthRepository`
올바른 분류: `관리자 로그인`, `세션 갱신`, `로그아웃`

## 규칙

- 기능은 "누가 무엇을 하면 무엇이 일어난다"로 설명할 수 있어야 한다. 엔드포인트·페이지·SPA 화면·백그라운드 작업·CLI/스크립트·배포 절차가 후보다.
- 상한 {{maxFeatures}}개. 비슷한 것은 합치고, 핵심(CORE)·보조(SUPPORTING)·기반(INFRA)으로 `importance`를 매긴다.
- `{{toolingDirs}}` 아래의 개발 도구(하네스·스킬·훅)는 기능이 아니다. `excludedCandidates`에 이유와 함께 적는다.
- `id`는 `F001`부터 순서대로. `slug`는 대문자 스네이크(예: `ADMIN_LOGIN`). `analysisStatus`는 `PENDING`, `status`는 `ACTIVE`.
- `entryPoints`는 `GET /api/posts`·`Razor /post/{slug}`·`SPA /editor`·`CLI deploy/backup.sh` 같은 형식.
- `relatedFiles`는 그 기능을 구현하는 파일 경로(실존해야 함). 최소 1개.
- `dependencies`는 다른 기능의 id.
- 아래 "코드에서 추출한 정답 목록"에 있는 엔드포인트·페이지·SPA 라우트는 **모두 어떤 기능에든 속해야 한다**. 어디에도 속하지 않는 것이 있으면 `unknowns`에 남긴다.

## 입력: Inventory 발췌

```json
{{inventory}}
```

## 입력: 아키텍처 컴포넌트

```json
{{components}}
```

## 입력: 코드에서 추출한 정답 목록

{{truthLists}}

출력은 스키마(features)에 맞춘 JSON 하나다.
