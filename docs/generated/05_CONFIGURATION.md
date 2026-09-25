# 설정

<!-- doc-harness:section id="one-liner" hash="3ee9883e5d93916a49d48ea95e03e414af256c98e4092b835ac48f8e39b67a42" -->
API 설정은 appsettings.json(키 골격) → appsettings.{Environment}.json → 환경변수(`Section__Key`) 순으로 덮어쓰며, 운영은 deploy/.env → docker-compose.yml → Dockerfile ENV로 주입되고 StartupValidation이 시작 시 검증한다.
<!-- /doc-harness:section -->

<!-- doc-harness:section id="summary" hash="0df7c0dc1cdc5ae3c1b63935abbad13b0b4db866482043bfefb05ce7e56aaa72" -->
## 한 줄 요약

설정 소스는 세 겹이다. (1) `PortfolioBlog.Api/appsettings.json`은 키 골격과 빈 값, (2) 개발 환경에서는 `appsettings.Development.json`이 덮어쓰고 Production 이미지 빌드 시 이 파일은 삭제된다, (3) 운영은 `deploy/.env`가 `docker-compose.yml`의 environment(`Section__Key` 형식)로 전달된다. 표준 ASP.NET Core 구성 규칙상 환경변수가 JSON보다 우선한다(INFERRED: 기본 호스트 빌더 사용, 별도 구성 코드는 확인하지 않음). 검증은 `StartupValidation.Validate`가 DB 마이그레이션보다 먼저 수행하며, 하나라도 틀리면 프로세스 시작을 막는다.

프런트엔드 개발서버(`PortfolioBlog.Web/vite.config.ts`)는 `BLOG_API_ORIGIN`과 `.certs/dev.pem·key`를 읽고, CI e2e와 배포 스모크는 각각 별도 환경변수·`.env.smoke`를 쓴다.
<!-- /doc-harness:section -->

<!-- doc-harness:section id="keys" hash="db7759435ac3ed22b9ed7eaa05d1cae0751bfcd589512544348050e88b3980c5" -->
## 설정 키

값(비밀번호·해시)은 적지 않는다. 기본값은 Options 클래스의 초기값 또는 appsettings.json 기준이다.

### API 설정 키

| 키 | 의미 | 기본값 | 필수 여부 | 읽는 코드 |
|---|---|---|---|---|
| `ConnectionStrings:Default` | 관리(쓰기) DB 연결 | 빈 문자열 | 필수(비면 컨텍스트 해석 시 `ConnectionStrings:Default 설정이 없습니다.`) | `DataServiceCollectionExtensions`, `StartupValidation` |
| `ConnectionStrings:Public` | 공개 조회용 읽기 전용 롤 연결 | 키 없음(appsettings.json에도 없음) | Development 외 필수 | `DataServiceCollectionExtensions`, `StartupValidation`, `PublicRoleGrants`, `Program.cs` |
| `Site:PublicOrigin` | 공개 사이트 origin | 빈 문자열 | 필수(형식 검증) | `SiteOptions`, `StartupValidation` |
| `Site:AdminOrigin` | 관리 사이트 origin | 빈 문자열 | 필수(형식 검증) | `SiteOptions`, `StartupValidation` |
| `Site:Title` | 사이트 제목 | `Blog` | 비울 수 없음 | `SiteOptions`, `StartupValidation` |
| `Site:Description` | 사이트 설명 | 빈 문자열 | 선택 | `SiteOptions` |
| `Site:Author` | 저자 | 빈 문자열 | 선택 | `SiteOptions` |
| `Admin:AllowedCidrs` | 관리 화면 접근 허용 CIDR 목록(공백 구분 형식이 개발 JSON에 쓰임) | 빈 문자열 | Development 외 1개 이상 필수 | `AdminOptions`, `CidrList.Parse` |
| `Admin:PasswordHash` | 관리자 비밀번호 해시(Base64) | 빈 문자열 | Development 외 필수 | `AdminOptions`, `StartupValidation` |
| `Admin:LoginPerIpPerMinute` | 로그인 IP별 분당 한도 | 5 | 선택 | `AdminOptions` |
| `Admin:LoginGlobalPerMinute` | 로그인 전역 분당 한도 | 20 | 선택 | `AdminOptions` |
| `Admin:LoginConcurrency` | 로그인 동시 처리 수 | 2 | 선택 | `AdminOptions` |
| `Admin:SessionHours` | 세션 유지 시간(시) | 12 | 선택 | `AdminOptions` |
| `Admin:PreviewPerMinute` | 미리보기 분당 한도 | 60 | 선택 | `AdminOptions` |
| `Admin:PreviewConcurrency` | 미리보기 동시 처리 수 | 2 | 선택 | `AdminOptions` |
| `Admin:UploadPerMinute` | 업로드 분당 한도 | 30 | 선택 | `AdminOptions` |
| `Admin:UploadConcurrency` | 업로드 동시 처리 수 | 2 | 선택 | `AdminOptions` |
| `Proxy:TrustedIp` | 신뢰하는 프록시의 단일 IP(CIDR 아님) | 빈 문자열 | Development 외 필수 | `ProxyOptions`, `StartupValidation` |
| `Attachments:RootPath` | 첨부 저장 루트 | 빈 문자열 | 모든 환경 필수 | `AttachmentOptions`, `FileSystemAttachmentStore` |
| `Attachments:JanitorEnabled` | 첨부 정리 작업 활성화 | true | 선택 | `AttachmentOptions` |
| `Public:PagePerIpPerMinute` | 공개 페이지 IP별 분당 한도 | 120 | 선택 | `PublicOptions` |
| `Public:AssetPerIpPerMinute` | 공개 자산 IP별 분당 한도 | 600 | 선택 | `PublicOptions` |
| `Public:SearchPerIpPerMinute` | 검색 IP별 분당 한도 | 20 | 선택 | `PublicOptions` |
| `Public:SearchConcurrency` | 검색 동시 처리 수 | 4 | 선택 | `PublicOptions` |
| `Public:StatementTimeoutMs` | 공개 연결 DB statement_timeout(ms) | 3000 | 선택 | `PublicOptions`, `StartupValidation` |
| `Rendering:Concurrency` | 마크다운 렌더 동시 수 | 2 | 선택 | `RenderingOptions` |
| `Rendering:QueueTimeoutMs` | 렌더 대기 제한(ms) | 5000 | 선택 | `RenderingOptions` |
| `Rendering:CacheMegabytes` | 렌더 캐시 크기(MB) | 64 | 선택 | `RenderingOptions` |
| `DataProtection:KeysPath` | Data Protection 키 저장 경로 | 키 없음(Dockerfile ENV가 `/data/dpkeys` 지정) | Development 외 필수(절대 경로) | `AuthServiceCollectionExtensions.DataProtectionKeysPathKey` |
| `Logging:LogLevel:*` | 로그 수준 | Default=Information, Microsoft.AspNetCore=Warning, Microsoft.EntityFrameworkCore.Database.Command=Warning | 선택 | 프레임워크 |

### appsettings.Development.json이 덮어쓰는 키

`Logging:LogLevel:Default`, `Logging:LogLevel:Microsoft.AspNetCore`, `ConnectionStrings:Default`, `Site:PublicOrigin`, `Site:AdminOrigin`(두 origin은 같은 로컬 https 값), `Admin:AllowedCidrs`(루프백 IPv4·IPv6), `Attachments:RootPath`(상대 경로 `.data/attachments`). 개발 JSON에는 `ConnectionStrings:Public`이 없다(확정) — 개발에서는 `Default`로 폴백한다. 개발 JSON은 `Microsoft.EntityFrameworkCore.Database.Command` 로그 수준을 다시 정의하지 않으므로 기본 JSON의 Warning이 유지된다.

### 운영 배포 환경변수(deploy/.env.example의 키)

`DOMAIN`, `ADMIN_DOMAIN`, `PUBLIC_ORIGIN`, `ADMIN_ORIGIN`, `ADMIN_ALLOWED_CIDRS`, `ACME_EMAIL`, `SITE_TITLE`, `SITE_DESCRIPTION`, `SITE_AUTHOR`, `POSTGRES_PASSWORD`, `BLOG_APP_PASSWORD`, `BLOG_PUBLIC_PASSWORD`, `ADMIN_PASSWORD_HASH`.

`docker-compose.yml`이 이를 `ConnectionStrings__Default`/`__Public`, `Site__*`, `Admin__AllowedCidrs`, `Admin__PasswordHash`로 매핑하며 `PUBLIC_ORIGIN`·`ADMIN_ORIGIN`·`ADMIN_ALLOWED_CIDRS`·`ADMIN_PASSWORD_HASH`·비밀번호류는 `:?`로 필수 표시된다. `Proxy__TrustedIp`는 compose에 고정값(172.30.0.2)으로 들어 있고, 두 연결 문자열은 `Command Timeout=30`을 포함한다. `Attachments__RootPath=/data/attachments`, `DataProtection__KeysPath=/data/dpkeys`, `ASPNETCORE_ENVIRONMENT=Production`은 Dockerfile ENV다.

### 기타 환경

| 대상 | 키 | 비고 |
|---|---|---|
| 프런트엔드 개발서버 | `BLOG_API_ORIGIN`, `.certs/dev.pem`, `.certs/dev.key` | `PortfolioBlog.Web/vite.config.ts` |
| CI web-e2e | `E2E_SKIP_DOCKER=1`, `E2E_PG_PORT`, `E2E_PG_PASSWORD` 등 | `.github/workflows/ci.yml`, CI Postgres 서비스 컨테이너 사용 |
| 배포 스모크 | `deploy/smoke/.env.smoke` | `run.sh`가 생성, `*.localhost` 도메인, 운영 `.env`와 별개 |
<!-- /doc-harness:section -->

<!-- doc-harness:section id="validation" hash="54b3a3c74436daf0f352b13eda734efd679662bf383936408fddb8d695c20762" -->
## 시작 시 검증

`StartupValidation.Validate(app.Services, environment)`가 DB 마이그레이션보다 먼저 동기 실행되며, 위반 시 설정 키를 담은 `InvalidOperationException`으로 프로세스 시작을 막는다. 비밀번호 등 값은 메시지에 넣지 않는다.

### 모든 환경

| 검증 | 실패 조건 |
|---|---|
| `Site:PublicOrigin`·`Site:AdminOrigin` 형식 | `SiteOptions.HostOf`가 FormatException/ArgumentException |
| `Site:Title` | 공백 |
| `Admin:AllowedCidrs` 형식 | `CidrList.Parse` 실패(빈 값 자체는 통과, 아래 운영 검증에서 걸림) |
| `Proxy:TrustedIp` | 값이 있는데 단일 IP가 아님(CIDR 불가) |
| `Admin:PasswordHash` | 값이 있는데 Base64가 아님 |
| Admin 한도류 | `LoginPerIpPerMinute`·`LoginGlobalPerMinute`·`LoginConcurrency`·`SessionHours`·`PreviewPerMinute`·`PreviewConcurrency`·`UploadPerMinute`·`UploadConcurrency` 중 1 미만 |
| Public 한도류 | `PagePerIpPerMinute`·`AssetPerIpPerMinute`·`SearchPerIpPerMinute`·`SearchConcurrency` 중 1 미만 |
| `Public:StatementTimeoutMs` | 100~60000 범위 밖 |
| 연결 문자열 형식(`Default`, `Public` 각각, 값이 있을 때) | Npgsql 파싱 실패, `Options` 포함, `Command Timeout`(초)×1000이 StatementTimeoutMs 이하(0=무한은 통과) |
| `ConnectionStrings:Public` 롤 이름 형식 | `PublicRoleGrants.RoleOf`가 Username이 소문자·숫자·밑줄 롤 이름이 아니라고 판정(GRANT 문장에 직접 들어가므로 DB 접속 전에 확인) |
| `Public`과 `Default`의 Username 동일 | 공개 조회는 별도 읽기 전용 롤이어야 함 |
| `Rendering:*` 범위 | Concurrency 1~64, QueueTimeoutMs 1~60000, CacheMegabytes 1~1024 |
| `Attachments:RootPath` | 공백. 이어서 `FileSystemAttachmentStore` 생성으로 경로 계산 오류도 시작 시 확인 |

### Development가 아닌 모든 환경(Staging·오타난 이름 포함)

| 검증 | 실패 조건 |
|---|---|
| `Proxy:TrustedIp` | 비어 있음 |
| `Admin:AllowedCidrs` | 파싱 결과 0개 |
| `Admin:PasswordHash` | 비어 있음 |
| `Site:AdminOrigin` | `PublicOrigin`과 동일(대소문자 무시) — 서브도메인 격리 붕괴 |
| `Site:PublicOrigin`·`Site:AdminOrigin` 스킴 | https가 아님 |
| `Attachments:RootPath` | 절대 경로가 아님 |
| `ConnectionStrings:Public` | 비어 있음 |
| `DataProtection:KeysPath` | 절대 경로가 아니거나 누락 |
<!-- /doc-harness:section -->

<!-- doc-harness:section id="evidence" hash="308a00235e4ec7a1534f78778747c566d16d086ce1e92e67eaf75310d33a1e11" -->
## 코드 근거

| 파일 | 심볼 | 근거 내용 |
|---|---|---|
| `PortfolioBlog.Api/Infrastructure/Access/StartupValidation.cs` | `StartupValidation.Validate`, `CheckConnectionString`, `Require` | 검증 조건 전체 |
| `PortfolioBlog.Api/Infrastructure/Data/PublicRoleGrants.cs` | `PublicRoleGrants.RoleOf` | 롤 이름 형식 검증 |
| `PortfolioBlog.Api/Infrastructure/Access/AdminOptions.cs` | `AdminOptions` | Admin 기본값 |
| `PortfolioBlog.Api/Infrastructure/Access/SiteOptions.cs` | `SiteOptions` | Site 기본값 |
| `PortfolioBlog.Api/Infrastructure/Access/ProxyOptions.cs` | `ProxyOptions.TrustedIp` | Proxy 기본값 |
| `PortfolioBlog.Api/Infrastructure/Web/PublicOptions.cs` | `PublicOptions` | Public 기본값 |
| `PortfolioBlog.Api/Infrastructure/Markdown/RenderingOptions.cs` | `RenderingOptions` | Rendering 기본값 |
| `PortfolioBlog.Api/Infrastructure/Storage/AttachmentOptions.cs` | `AttachmentOptions` | 첨부 기본값 |
| `PortfolioBlog.Api/Infrastructure/Access/AuthServiceCollectionExtensions.cs` | `DataProtectionKeysPathKey` | `DataProtection:KeysPath` 키 이름 |
| `PortfolioBlog.Api/Infrastructure/Data/DataServiceCollectionExtensions.cs` | 연결 문자열 조회 | `ConnectionStrings:Default` 누락 시 예외 |
| `PortfolioBlog.Api/appsettings.json`, `PortfolioBlog.Api/appsettings.Development.json` | — | 키 골격과 개발 덮어쓰기 |
| `PortfolioBlog.Api/Dockerfile` | ENV(27-30행) | Production, Attachments·DataProtection 경로 |
| `deploy/docker-compose.yml` | environment(61-70행) | 환경변수 매핑 |
| `deploy/.env.example` | — | 운영 환경변수 키 목록 |
| `PortfolioBlog.Web/vite.config.ts` | — | `BLOG_API_ORIGIN`, 개발 인증서 |
<!-- /doc-harness:section -->

<!-- doc-harness:section id="caveats" hash="36851bf69ebf713cee9935f25443271be84ffbb953cb9c7243ecdd5e811253bd" -->
## 주의사항

- `deploy/.env.example`은 존재한다(키 13개, 위 목록). 이전에 UNKNOWN으로 남겼던 항목은 해소됐다. 03_DIRECTORY_STRUCTURE.md의 deploy/ 트리에는 이 파일이 빠져 있어 그쪽 보완이 필요하다.
- `ConnectionStrings:Public`은 개발용 JSON에 없다(확정). 개발에서는 `Default`로 폴백하는 것으로 보이며, 롤 분리(읽기 전용) 보호는 개발에서 적용되지 않는다. 운영은 필수다.
- 개발 JSON은 두 origin을 같은 값으로 두는데, Development 외에서는 시작 실패다.
- `Admin:AllowedCidrs` 개발 값은 공백 구분 목록이다. 구분자 규칙은 `CidrList.Parse` 소관이며 이 문서 작성 중 코드로 재확인하지 않았다(UNKNOWN).
- `Command Timeout`은 `Public:StatementTimeoutMs`(기본 3000ms)보다 커야 한다. 개발 `Default`는 Command Timeout을 지정하지 않아 Npgsql 기본값에 따르며, 이 값이 검증을 통과하는지는 Npgsql 기본값에 의존한다(확인 안 함).
- 두 연결 문자열에 `Options`를 넣으면 시작 실패한다.
- `Proxy:TrustedIp`는 CIDR이 아닌 단일 IP다. compose는 172.30.0.2로 고정한다.
- 환경 이름이 `Development`가 아니면 전부 운영 규칙이 적용된다.
- 환경변수 우선순위는 표준 ASP.NET Core 규칙에 의한 추론이다(INFERRED).
<!-- /doc-harness:section -->

<!-- doc-harness:section id="related" hash="97d60c2734b37eaf24d570829a292eb831cdf995fa48b8f1365bc1e6ef04a9cc" -->
## 관련 문서

- [04_SETUP_AND_RUN](04_SETUP_AND_RUN.md)
- [13_SECURITY](13_SECURITY.md)
- [16_DEPLOYMENT](16_DEPLOYMENT.md)
- [features/F021_STARTUP_BOOTSTRAP](features/F021_STARTUP_BOOTSTRAP.md)
- [features/F026_COMPOSE_DEPLOYMENT](features/F026_COMPOSE_DEPLOYMENT.md)
- [features/F028_DEPLOY_SMOKE_TEST](features/F028_DEPLOY_SMOKE_TEST.md)
<!-- /doc-harness:section -->

<!-- doc-harness:section id="unknowns" hash="81b5f3cec6646d3984f772905aa001981d14600f91268ae3a87e6ae9f7fefe2c" -->
## 확인하지 못한 것

- Admin:AllowedCidrs의 구분자 규칙(CidrList.Parse)은 이번에 코드로 재확인하지 않았다.
- ConnectionStrings:Default에 Command Timeout이 없을 때 Npgsql 기본값과 StatementTimeoutMs 검증의 상호작용은 확인하지 않았다.
- 환경변수가 JSON보다 우선한다는 점은 표준 호스트 빌더 규칙에 따른 추론이며 구성 소스 등록 코드는 읽지 않았다.
<!-- /doc-harness:section -->
