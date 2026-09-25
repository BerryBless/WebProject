# 설치·실행·디버깅

<!-- doc-harness:section id="one-liner" hash="aad8da92e1545f6226abeba2eacd6e6161484a3aa72b7f518f93e6e4e7a0e9e8" -->
.NET 10 SDK·Node 24·Docker가 있으면 `dotnet run`(API)과 `npm run dev`(관리 SPA)로 띄우고, `dotnet test`·`npm test`·`deploy/smoke/run.sh`로 검증한다.
<!-- /doc-harness:section -->

<!-- doc-harness:section id="summary" hash="ac60ba6ea13534663e21312d9615ee9454cac42cb4022fdd8ff371787eb9f356" -->
## 한 줄 요약

준비물은 .NET 10 SDK, Node.js 24, Docker(Testcontainers·배포 스모크용)다. 백엔드는 `dotnet run --project PortfolioBlog.Api`, 프런트엔드는 `PortfolioBlog.Web`에서 `npm run dev`로 띄운다.
<!-- /doc-harness:section -->

<!-- doc-harness:section id="prerequisites" hash="3aaa86992f78de5730fa4b2d98e617806cda6a1372d8adc5ae6cd94a0a8fd0fb" -->
## 준비물

| 구분 | 버전 | 근거 |
|---|---|---|
| .NET SDK | 10.0.401(빌드), 런타임 aspnet 10.0.12 | PortfolioBlog.Api/Dockerfile |
| Node.js | 24 (컨테이너 24.21.0-alpine, CI node-version 24) | PortfolioBlog.Web/Dockerfile, .github/workflows/ci.yml |
| Docker | .NET 통합 테스트(Testcontainers.PostgreSql), E2E, 배포 스모크에 필요 | PortfolioBlog.Api.Tests.csproj, deploy/smoke/run.sh |

NuGet 버전은 루트 `Directory.Packages.props`에서 중앙 관리한다(전이 의존성 고정 포함).
<!-- /doc-harness:section -->

<!-- doc-harness:section id="run" hash="0e42665c3cae96010fa9f56377170341acb8a9a444c3bc769667999cfb2a3ea6" -->
## 실행

백엔드와 프런트엔드는 각각 따로 띄운다. `ASPNETCORE_ENVIRONMENT=Development`일 때 `appsettings.Development.json`이 개발 값을 제공한다(Production 이미지에서는 이 파일이 삭제된다). 개발용 프로필은 `PortfolioBlog.Api/Properties/launchSettings.json`에 있다.

```bash
# 백엔드
dotnet restore
dotnet build
dotnet run --project PortfolioBlog.Api

# 프런트엔드 (PortfolioBlog.Web)
npm ci
npm run dev        # vite 개발 서버
npm run build      # tsc -b && vite build
npm run preview

# 운영형 스택(caddy·api·postgres)
docker compose -f deploy/docker-compose.yml up -d
```

운영 스택 실행에 필요한 환경변수와 절차는 [16_DEPLOYMENT](16_DEPLOYMENT.md), [05_CONFIGURATION](05_CONFIGURATION.md) 참고. 백업/복원은 `deploy/backup.sh`, `deploy/restore.sh`(pg_dump/pg_restore + 첨부 tar)다. 비밀번호 해시 생성 CLI는 [F023](features/F023_HASH_PASSWORD_CLI.md) 참고.
<!-- /doc-harness:section -->

<!-- doc-harness:section id="test" hash="37ac7a7d738f6d965dba1f87b8b740d2c9b05d1394518127a684f274211e3cab" -->
## 테스트

| 대상 | 명령 | 사전 조건 |
|---|---|---|
| .NET (xUnit + WebApplicationFactory) | `dotnet test` | Docker 실행 중(Testcontainers가 PostgreSQL 기동) |
| 프런트 린트/타입/단위 | `npm run lint`, `npm run typecheck`, `npm test` | `npm ci` |
| Playwright E2E | `npm run e2e:prepare` 후 `npm run e2e` | 개발 인증서·비밀번호 해시 준비(scripts/e2e-prepare.mjs), 로컬은 Docker, CI는 `E2E_SKIP_DOCKER=1`과 Postgres 서비스 컨테이너 |
| 배포 스택 스모크 | `bash deploy/smoke/run.sh` | Docker compose, `*.localhost` 도메인, 별도 `.env.smoke` |

스모크 검증 항목(허용/비허용 IP, 헤더, 업로드 한도, 백업·복원 리허설, DB 롤 권한)은 deploy/smoke/smoke.test.mjs(node:test)에 있다. CI는 test·web·web-e2e·deploy-smoke 4개 잡을 돌린다. 자세한 내용은 [15_TESTING](15_TESTING.md).
<!-- /doc-harness:section -->

<!-- doc-harness:section id="debug" hash="a95badcbba87086fe78ce2ae364e6cf61cdbba3ddda130d7e12bc274e23a64ed" -->
## 디버깅

- 개발 프로필: `PortfolioBlog.Api/Properties/launchSettings.json`, 설정: `appsettings.Development.json`.
- 컨테이너 로그: `docker compose -f deploy/docker-compose.yml logs -f api`(API 이미지는 셸이 없는 chiseled 이미지라 `docker exec`로 셸 진입 불가).
- 프런트 진단: `npm run typecheck`, `npm run lint`.
- 로그 파일 위치·로깅 설정 키: UNKNOWN(이 문서 작성 시 코드로 확인하지 못함).

문제 유형별 대응은 [12_TROUBLESHOOTING](12_TROUBLESHOOTING.md) 참고.
<!-- /doc-harness:section -->

<!-- doc-harness:section id="evidence" hash="ff41f2fd7500f75ea779779c6d81153787b4f6a44993e2f7af6a836d22a71b61" -->
## 코드 근거

| 주장 | 근거 |
|---|---|
| .NET 10 SDK/런타임 | PortfolioBlog.Api/Dockerfile |
| Node 24, CI 잡 구성 | PortfolioBlog.Web/Dockerfile, .github/workflows/ci.yml |
| npm 스크립트 | PortfolioBlog.Web/package.json (scripts) |
| E2E 준비 | PortfolioBlog.Web/scripts/e2e-prepare.mjs |
| 백업/복원 | deploy/backup.sh, deploy/restore.sh |
| 스모크 | deploy/smoke/run.sh, deploy/smoke/smoke.test.mjs, deploy/docker-compose.smoke.yml |
| 운영 compose | deploy/docker-compose.yml |
| 테스트 프로젝트 | PortfolioBlog.Api.Tests/PortfolioBlog.Api.Tests.csproj |
| 개발 프로필 | PortfolioBlog.Api/Properties/launchSettings.json |
<!-- /doc-harness:section -->

<!-- doc-harness:section id="caveats" hash="63c554127d65676b43b3cde3b16d476c60e16d84b2bd47dce6ca77ceadff2186" -->
## 주의사항

- `dotnet test`와 스모크는 Docker 없이는 동작하지 않는다.
- E2E는 로컬(Testcontainers)과 CI(`E2E_SKIP_DOCKER=1`, `E2E_PG_PORT`/`E2E_PG_PASSWORD`)의 DB 기동 방식이 다르다.
- Production 이미지에는 appsettings.Development.json이 없으므로 운영 값은 환경변수·시크릿으로 주입해야 한다.
- `dotnet run` 실제 포트와 필요한 로컬 DB 연결 방식은 이 문서에서 확인하지 못했다(UNKNOWN).
<!-- /doc-harness:section -->

<!-- doc-harness:section id="related" hash="95aa936e8c164213f82c180cfc2504cdd7628cdd7d1506e327ce9cb0e4f21dd5" -->
## 관련 문서

- [05_CONFIGURATION](05_CONFIGURATION.md)
- [15_TESTING](15_TESTING.md)
- [16_DEPLOYMENT](16_DEPLOYMENT.md)
- [12_TROUBLESHOOTING](12_TROUBLESHOOTING.md)
- [F028 배포 스모크](features/F028_DEPLOY_SMOKE_TEST.md)
<!-- /doc-harness:section -->

<!-- doc-harness:section id="unknowns" hash="1caf5497ae7407101008b6d722c6a0735107c05073a5d474657d7debee34b76c" -->
## 확인하지 못한 것

- dotnet run 시 실제 리슨 포트와 로컬 개발용 DB 연결 방식
- API 로그 파일 위치와 로깅 설정 키
- docker compose 운영 스택에 필요한 .env 키 목록(05_CONFIGURATION에서 확인 필요)
<!-- /doc-harness:section -->
