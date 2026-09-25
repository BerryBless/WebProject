# 테스트

<!-- doc-harness:section id="one-liner" hash="a789f68eb741578e11109dd9e9d782fe1b2ec5067cc4df03f5847f4ee12d167a" -->
테스트는 .NET(xUnit+Testcontainers), 웹(Vitest·Playwright), 배포 스택 스모크(node:test) 3계층이며, .github/workflows/ci.yml 한 파일의 4개 잡이 모두 실행한다. 정확한 테스트 개수는 UNKNOWN이다.
<!-- /doc-harness:section -->

<!-- doc-harness:section id="summary" hash="c8e7dd280cee41de04a8bdbd3576d14ac9e09f1a98a82992489d8b49f66cea0d" -->
## 한 줄 요약

테스트는 세 계층이다. (1) .NET 통합·인프라 테스트(xUnit, WebApplicationFactory, Testcontainers.PostgreSql), (2) 웹 Vitest 단위/컴포넌트 테스트와 Playwright E2E, (3) 배포 스택 스모크(`deploy/smoke/smoke.test.mjs`, node:test). 테스트 개수는 워크스페이스 산출물에서 확인하지 못해 UNKNOWN이다.
<!-- /doc-harness:section -->

<!-- doc-harness:section id="key-points" hash="45aa39d2b386e00207c9169cf9c8044262f62bbc88a7e4986f4de7216f722828" -->
## 핵심 내용

| 대상 | 위치 | 프레임워크 | 실행 명령 | 외부 자원 |
|---|---|---|---|---|
| API(.NET) | `PortfolioBlog.Api.Tests/` (`Features/`, `Infrastructure/`) | xUnit, WebApplicationFactory, Testcontainers.PostgreSql | `dotnet test --configuration Release` (CI 기준) | Docker(Testcontainers가 PostgreSQL 컨테이너 기동) |
| 웹 단위/컴포넌트 | `PortfolioBlog.Web/src/test/`, `*.test.ts(x)` | Vitest (`vitest.config.ts`) | `npm test` (= `vitest run`) | 없음 |
| 웹 E2E | `PortfolioBlog.Web/e2e/admin.spec.ts` | Playwright (`playwright.config.ts`, `playwright.stack.config.ts`) | `npm run e2e:prepare` 후 `npm run e2e` (CI 기준) | PostgreSQL(CI는 포트 5433 서비스 컨테이너, `E2E_SKIP_DOCKER=1`), 브라우저 chromium·firefox, 빌드된 API |
| 배포 스택 | `deploy/smoke/smoke.test.mjs` | node:test | `SMOKE_E2E=1 bash deploy/smoke/run.sh` | Docker 이미지·Caddyfile·compose 스택 |

스모크는 허용/비허용 IP, 헤더, 업로드 한도, 백업·복원 리허설, DB 롤 권한을 검사한다. 관련 기능: [F028](features/F028_DEPLOY_SMOKE_TEST.md).

정적 검사(`npm run lint`=oxlint, `npm run typecheck`=`tsc -b`)도 테스트 게이트로 CI에서 돈다.
<!-- /doc-harness:section -->

<!-- doc-harness:section id="details" hash="ab5c677859cadba6afe79d0ee0522fdf5cefd5b694cd3272d5612b8004ed83f6" -->
## 상세 내용

## 상세 내용

**CI 파이프라인** (`.github/workflows/ci.yml`, push는 master·feature/**, PR은 master 대상)

| 잡 | 선행 | 내용 |
|---|---|---|
| test | 없음 | .NET 10.0.x, restore → Release build → `dotnet test`(단위+Testcontainers 통합) |
| web | 없음 | Node 24, `npm ci`, `npm audit --omit=dev --audit-level=high`, lint, typecheck, `npm test`, build |
| web-e2e | test, web | postgres:17-alpine 서비스, API 빌드, Playwright 브라우저 설치, `e2e:prepare`, `e2e`. 실패 시 `playwright-traces` 업로드(7일) |
| deploy-smoke | test, web | timeout 30분, `SMOKE_E2E=1 bash deploy/smoke/run.sh`. 실패 시 `test-results`와 `deploy/smoke/caddy.log`를 `deploy-smoke-traces`로 업로드(7일) |

**커버리지 갭** (17_TECH_DEBT 기준)

- DEBT004(POTENTIAL_RISK): 에디터 `Editor.save.onError`가 409의 원인(version 불일치, 참조 삭제 경쟁, 태그 삭제로 인한 오탐 StaleVersion)을 구분하지 않고 ConflictPanel을 띄운다. 서버 `PostEndpoints.UpdateAsync`(215-233행)도 사라진 PostTag 행 때문에 `DbUpdateConcurrencyException`이 나면 Version이 바뀌지 않았는데 '먼저 수정되었습니다'가 나갈 수 있다. EF 동작 추론이며 재현 테스트가 없다. 제안: 409에 원인 구분 값을 넣고 태그 삭제 경쟁 재현 테스트를 추가한다.
- DEBT014(IMPROVEMENT): 전용 테스트가 없거나 추론에 그친 경로가 있다. version 쿼리 비숫자 값의 400, `PreviewRequest` 잘못된 JSON·415 응답 본문 형태, 태그 삭제 경쟁 StaleVersion 오탐, highlight.css의 관리 호스트 404, 429 본문이 HTML이라는 점이다. 제안: 해당 경로의 통합 테스트를 추가해 추론을 확정한다.

**불안정 테스트**: 워크스페이스 산출물에 기록된 flaky 테스트는 없다(UNKNOWN: 실제 CI 이력은 확인하지 못함). 스모크 주석은 허용 IP가 러너에서 다르게 나오는 "첫 Linux 실행" 실패 모드를 예상하며, 이때 `caddy.log`로 원인을 읽는다.
<!-- /doc-harness:section -->

<!-- doc-harness:section id="evidence" hash="60e67a2e1f9223fc67c3144dd9bb793d426def24b947b74555192f14cfefa8b7" -->
## 코드 근거

## 코드 근거

| 파일 | 근거 내용 |
|---|---|
| `.github/workflows/ci.yml` | 4개 잡, 명령, 아티팩트 업로드 |
| `PortfolioBlog.Web/package.json` | `lint`, `typecheck`, `test`, `build` 스크립트 |
| `PortfolioBlog.Api.Tests/PortfolioBlog.Api.Tests.csproj` | .NET 테스트 프로젝트 |
| `PortfolioBlog.Api.Tests/Features/AuthEndpointsTests.cs` | 엔드포인트 테스트 예 |
| `PortfolioBlog.Api.Tests/Infrastructure/DatabaseSchemaTests.cs` | 인프라 테스트 예 |
| `PortfolioBlog.Web/vitest.config.ts`, `PortfolioBlog.Web/src/test/editor.test.tsx` | Vitest 설정과 예 |
| `PortfolioBlog.Web/e2e/admin.spec.ts`, `playwright.config.ts`, `playwright.stack.config.ts` | Playwright E2E |
| `deploy/smoke/smoke.test.mjs`, `deploy/smoke/run.sh` | 배포 스모크 |
| `PortfolioBlog.Web/src/pages/PostEditorPage.tsx` (`Editor.save.onError`) | DEBT004 |
| `PortfolioBlog.Api/Features/Posts/PostEndpoints.cs` (`UpdateAsync` 215-233) | DEBT004 |
| `PortfolioBlog.Api/Infrastructure/Data/DbConflict.cs` (`DbConflict`) | DEBT004 |
| `PortfolioBlog.Api/Features/Preview/PreviewEndpoints.cs` (`Render`) | DEBT014 |
| `PortfolioBlog.Api/Infrastructure/Web/RateLimitingExtensions.cs` | DEBT014 |
<!-- /doc-harness:section -->

<!-- doc-harness:section id="caveats" hash="34619ac7ff78c18e6350bd1b1fbb42f4c3555498d8335f599b550d4741ddfeaa" -->
## 주의사항

- .NET 통합 테스트는 Docker가 필요하다(Testcontainers). Docker 없는 환경에서는 실패할 수 있다.
- web-e2e와 deploy-smoke는 test·web 잡이 통과해야 시작된다.
- E2E·스모크 trace에는 로그인 본문과 쿠키가 담기지만, 실행마다 새로 만든 값이라는 것이 ci.yml 주석의 전제다.
- `npm audit --omit=dev --audit-level=high`가 web 잡에 있어 의존성 취약점만으로도 CI가 실패할 수 있다.
<!-- /doc-harness:section -->

<!-- doc-harness:section id="related" hash="a410e5a0c70387f83684ef2e31395b51649d1224162013e1b88ae76609af3b7c" -->
## 관련 문서

- [04_SETUP_AND_RUN](04_SETUP_AND_RUN.md)
- [16_DEPLOYMENT](16_DEPLOYMENT.md)
- [17_TECH_DEBT](17_TECH_DEBT.md)
- [F028 배포 스모크](features/F028_DEPLOY_SMOKE_TEST.md)
<!-- /doc-harness:section -->

<!-- doc-harness:section id="unknowns" hash="2eba98f8e9c6dc80a566737367d36c940285ccb869d8fca72e20cb9eb3b99f50" -->
## 확인하지 못한 것

- 테스트 개수(.NET·Vitest·Playwright·smoke 각각)는 확인하지 못함
- 불안정(flaky) 테스트의 실제 CI 이력은 확인하지 못함
- 커버리지 측정·임계값 설정 여부는 확인하지 못함
<!-- /doc-harness:section -->
