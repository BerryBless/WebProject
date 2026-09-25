# 의존성

<!-- doc-harness:section id="one-liner" hash="4b7d8f5c7595a8713e64b7e59d63431e9bdf8e99a0822272514a130aa936916e" -->
백엔드 NuGet 버전은 Directory.Packages.props로 중앙 관리·전이 고정하고, 프런트엔드는 package.json과 package-lock.json으로 관리한다.
<!-- /doc-harness:section -->

<!-- doc-harness:section id="summary" hash="193b38e2cce8ffe6bc4b8d04022dc39ef43d91de854bfd72e8a6214d03883936" -->
## 한 줄 요약

백엔드(.NET 10)는 `Directory.Packages.props`에서 모든 NuGet 버전을 중앙 관리하고 전이 의존성까지 고정한다(`CentralPackageTransitivePinningEnabled`). 프런트엔드는 `PortfolioBlog.Web/package.json`과 `package-lock.json`으로 관리한다.
<!-- /doc-harness:section -->

<!-- doc-harness:section id="packages" hash="1a49a754490a2dcf491e3c56ddda136f4313000e5a9eee73fc2a1d60ff12630c" -->
## 패키지

### 백엔드 (PortfolioBlog.Api)

| 패키지 | 버전 | 용도 |
|---|---|---|
| Markdig | 1.4.0 | 마크다운 렌더링 |
| ColorCode.HTML | 2.0.15 | 코드 블록 하이라이팅 |
| HtmlSanitizer | 9.2.1039 | 렌더링된 HTML 정제 |
| Microsoft.AspNetCore.OpenApi | 10.0.11 | OpenAPI 문서 |
| Microsoft.EntityFrameworkCore.Design | 10.0.12 | EF Core 디자인타임(마이그레이션) |
| Microsoft.EntityFrameworkCore.Relational | 10.0.12 | EF Core 관계형 공통 |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3 | PostgreSQL 공급자 |

### 테스트 (PortfolioBlog.Api.Tests)

| 패키지 | 버전 | 용도 |
|---|---|---|
| xunit | 2.9.3 | 테스트 프레임워크 |
| xunit.runner.visualstudio | 3.1.4 | 테스트 러너 |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.12 | 통합 테스트 호스트 |
| Microsoft.NET.Test.Sdk | 17.14.1 | 테스트 SDK |
| Testcontainers.PostgreSql | 4.15.0 | PostgreSQL 컨테이너 |
| coverlet.collector | 6.0.4 | 커버리지 수집 |

### 프런트엔드 (PortfolioBlog.Web/package.json)

| 패키지 | 버전 | 용도 |
|---|---|---|
| react / react-dom | 19.2.8 | UI |
| react-router | 8.4.0 | 라우팅 |
| @tanstack/react-query | 5.103.2 | 서버 상태 |
| codemirror (+ @codemirror/lang-markdown·state·view) | 6.0.2 계열 | 마크다운 에디터 |
| vite | 8.3.0 | 번들러(개발) |
| vitest | 5.0.1 | 단위 테스트(개발) |
| playwright | 1.63.0 | E2E(개발) |
| tailwindcss | 4.3.3 | 스타일(개발) |
| oxlint | 1.81.0 | 린트(개발) |
| typescript | 6.0.2 | 타입체크(개발) |
<!-- /doc-harness:section -->

<!-- doc-harness:section id="policy" hash="f04ed4a8d9f42ebcaa43292731793b00f295112dd9cf7596d571218725db8465" -->
## 버전 정책

- 백엔드: `ManagePackageVersionsCentrally=true`로 버전은 `Directory.Packages.props`에만 둔다.
- `CentralPackageTransitivePinningEnabled=true`로 전이 의존성도 고정한다. Design 패키지가 끌어오는 EF Core 버전과 테스트 프로젝트의 전이 버전이 어긋나 CS1705가 났던 문제의 처방이다(파일 내 주석).
- 백엔드 `packages.lock.json`은 저장소에 없다. NuGet 잠금 파일은 사용하지 않는다.
- 프런트엔드: `PortfolioBlog.Web/package-lock.json`이 존재한다.
<!-- /doc-harness:section -->

<!-- doc-harness:section id="evidence" hash="81c514d758eb44d03f94552a6d4f8d9d4d485ec896941fecfb66eaa00d634909" -->
## 코드 근거

| 파일 | 근거 |
|---|---|
| `Directory.Packages.props` | 중앙 관리 플래그, 전이 고정, 백엔드·테스트 패키지 버전(1-23행) |
| `PortfolioBlog.Api/PortfolioBlog.Api.csproj` | 백엔드 패키지 참조(21-31행) |
| `PortfolioBlog.Api.Tests/PortfolioBlog.Api.Tests.csproj` | 테스트 패키지 참조(10-17행) |
| `PortfolioBlog.Web/package.json` | 프런트엔드 의존성(17-44행) |
| `PortfolioBlog.Web/package-lock.json` | 프런트엔드 lock 파일 존재 |
<!-- /doc-harness:section -->

<!-- doc-harness:section id="caveats" hash="2010acd8ec5dd43951b576656dc6579f3137af6233fd62adf85e592b19ca7eab" -->
## 주의사항

- `.csproj`에는 버전을 직접 쓰지 말고 `Directory.Packages.props`를 수정한다.
- EF Core 계열 버전을 올릴 때는 Design·Relational·Mvc.Testing 버전 정합을 확인한다(CS1705 전력).
- Microsoft.AspNetCore.OpenApi(10.0.11)는 다른 ASP.NET/EF 패키지(10.0.12)와 패치 버전이 다르다.
<!-- /doc-harness:section -->

<!-- doc-harness:section id="related" hash="e44617bd735495b3016f317e5e743b292a200ec5c8a098c47355e430151638bd" -->
## 관련 문서

- [04_SETUP_AND_RUN](04_SETUP_AND_RUN.md)
- [15_TESTING](15_TESTING.md)
- [16_DEPLOYMENT](16_DEPLOYMENT.md)
<!-- /doc-harness:section -->
