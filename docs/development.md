# 개발 환경

로컬에서 띄우고, 고치고, 검증하는 방법입니다. 테스트의 구성과 실행은 [테스트](testing.md)에 따로 있습니다.

관련 문서: [아키텍처](architecture.md) · [설정 키](configuration.md) · [재개 가이드](../plan/resume_guide_0921.md)

## 준비물

| 도구 | 용도 |
|---|---|
| .NET SDK 10.0.303+ | 빌드·테스트·실행 |
| Docker Desktop | 통합 테스트(Testcontainers), E2E의 PostgreSQL, 배포 스모크 |
| Node.js 24 | 관리 SPA(react-router 8이 22.22 이상을 요구) |
| PowerShell 7 | 하네스 스크립트(`scripts/*.ps1`) |
| `gh`(GitHub CLI) | PR 생성·CI 확인 |
| Codex CLI | 교차 검증 하네스를 쓸 때만 |

```powershell
git clone https://github.com/BerryBless/WebProject.git
cd WebProject
Copy-Item scripts/git-hooks/commit-msg .git/hooks/   # 커밋 메시지 형식 훅
dotnet build PortfolioBlog.slnx -c Release           # 경고 0 / 오류 0
dotnet test  PortfolioBlog.slnx -c Release           # 591개 (Docker 필요)
```

## API + 공개 페이지 띄우기

1. 개발용 PostgreSQL을 띄웁니다(`appsettings.Development.json`의 연결 문자열과 맞춥니다):
   ```powershell
   docker run -d --name blog-dev-pg -e POSTGRES_PASSWORD=changeme -e POSTGRES_DB=blog_dev -p 5432:5432 postgres:17-alpine
   ```
2. 관리자 비밀번호 해시를 user-secrets에 넣습니다(저장소에는 남지 않습니다):
   ```powershell
   dotnet user-secrets init --project PortfolioBlog.Api
   dotnet run --project PortfolioBlog.Api -- hash-password        # 입력은 화면에 보이지 않습니다
   dotnet user-secrets set "Admin:PasswordHash" "<해시>" --project PortfolioBlog.Api
   ```
3. 개발 인증서를 신뢰하고 https 프로필로 띄웁니다(세션 쿠키가 `Secure`라 http로는 로그인이 유지되지 않습니다):
   ```powershell
   dotnet dev-certs https --trust
   dotnet run --project PortfolioBlog.Api --launch-profile https
   ```

`appsettings.Development.json`은 `Site:PublicOrigin`·`Site:AdminOrigin`을 둘 다 `https://localhost:7198`로 두므로 한 주소가 공개·관리 역할을 겸합니다(Development에서만 허용되는 예외입니다). `https://localhost:7198/`을 열면 공개 첫 쪽이 보이고, 관리 API 호출 예시는 [`PortfolioBlog.Api/PortfolioBlog.Api.http`](../PortfolioBlog.Api/PortfolioBlog.Api.http)에 있습니다(상태 확인·로그인·글 생성·목록·미리보기·첨부 업로드 + 공개 페이지·태그·검색·피드·sitemap).

### Docker 없이 띄우기

Docker가 필요한 것은 개발용 PostgreSQL 하나뿐입니다. Windows에 PostgreSQL을 직접 설치하면 나머지 절차는 같습니다. API가 시작할 때 마이그레이션을 적용하므로 스키마를 따로 만들 필요는 없습니다.

1. PostgreSQL 17을 설치합니다. 설치 중 정한 `postgres` 비밀번호를 기억해 둡니다.
   ```powershell
   winget install PostgreSQL.PostgreSQL.17
   ```
2. 개발 DB를 만듭니다.
   ```powershell
   & "C:\Program Files\PostgreSQL\17\bin\createdb.exe" -U postgres blog_dev
   ```
3. 설치 때 정한 비밀번호가 `appsettings.Development.json`의 값과 다르면, 파일을 고치지 말고 user-secrets로 덮어씁니다.
   ```powershell
   dotnet user-secrets init --project PortfolioBlog.Api
   dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Port=5432;Database=blog_dev;Username=postgres;Password=<설치 때 정한 비밀번호>" --project PortfolioBlog.Api
   ```
4. 위 2·3단계(관리자 비밀번호 해시, https 프로필 실행)를 그대로 합니다. `https://localhost:7198/`이 공개 첫 쪽입니다.

관리 SPA도 Docker 없이 아래 절차 그대로 뜹니다. 다만 `dotnet test`(Testcontainers)와 E2E·배포 스모크는 여전히 Docker가 필요합니다.

## 관리 SPA 띄우기

```powershell
cd PortfolioBlog.Web
npm ci
npm run certs   # .NET 개발 인증서를 .certs/로 내보냅니다 — SPA도 HTTPS로 떠야 합니다
```

백엔드를 **SPA의 출처로** 띄웁니다(변경 요청의 Origin 검사가 `Site:AdminOrigin`과 SPA 출처가 같아야 통과합니다):

```powershell
$env:Site__AdminOrigin = 'https://localhost:5173'
dotnet run --project PortfolioBlog.Api --launch-profile https
```

새 터미널에서 `npm run dev` → `https://localhost:5173`. `/api/*`·`/attachments/*`는 `https://localhost:7198`으로 프록시됩니다(접두사 매칭이 아니라 `^/api/`·`^/attachments/` 정규식입니다 — SPA 화면 주소 `/attachments`가 백엔드로 끌려가면 안 됩니다).

정적 검사: `npm run lint`(oxlint) · `npm run typecheck`(`tsc -b`).

## EF Core 마이그레이션

컨텍스트가 둘(`AppDbContext`·`PublicDbContext`)이라 `--context`가 필요합니다. 마이그레이션은 관리 컨텍스트에만 만듭니다.

```powershell
dotnet ef migrations add <이름> --project PortfolioBlog.Api --context AppDbContext
```

## 미리보기 CSS 스냅숏 갱신

에디터 미리보기 iframe에 넣는 CSS는 `PortfolioBlog.Api/wwwroot/css/site.css`(와 서버가 만드는 강조 CSS)의 **사본**이며 `PortfolioBlog.Web/public/preview/{site,highlight}.css`에 있습니다. 공개 CSS를 바꾸면 사본도 갱신해야 합니다(안 하면 `PreviewCssSnapshotTests`가 실패합니다):

```powershell
$env:UPDATE_PREVIEW_SNAPSHOTS = '1'
dotnet test PortfolioBlog.Api.Tests -c Release --filter "FullyQualifiedName~PreviewCssSnapshotTests"
Remove-Item Env:UPDATE_PREVIEW_SNAPSHOTS
dotnet test PortfolioBlog.Api.Tests -c Release --filter "FullyQualifiedName~PreviewCssSnapshotTests"   # 통과 확인
```

갱신 실행은 파일을 쓴 뒤 **의도적으로 실패로 끝납니다** — 환경변수를 켜 둔 채로 두면 드리프트 검사가 항상 통과해 버리기 때문입니다.

## 코드 규칙

- 인터페이스·public 메서드·미들웨어·엔드포인트 핸들러에는 XML 문서 주석과 3항목 `<remarks>`(Thread Safety / Memory Allocation / Blocking)를 답니다. **내용 없는 상용구는 금지**입니다 — 실제 제약을 가립니다. 적용 범위 표는 [`CLAUDE.md`](../CLAUDE.md)에 있습니다.
- 네트워크·메모리 관련 타입 선언에는 "왜 이 타입인가"를 **내부 동작 메커니즘**을 근거로 인라인 주석에 적습니다.
- 커밋 메시지는 `{접두사}: {제목}`(추가/수정/버그수정/리팩토링/문서/테스트/의존성, 50자 이내, WHY 중심). `scripts/git-hooks/commit-msg`가 강제합니다.
- 설계나 아키텍처 결정이 끝나면 `plan/<기능명>_<MMDD>.md`에 문서를 남깁니다.

## 자주 밟는 함정

| 함정 | 증상 | 대처 |
|---|---|---|
| 세션 쿠키가 `Secure` | http로 띄우면 로그인이 유지되지 않는다 | 항상 https 프로필·HTTPS 개발 서버 |
| `Site:AdminOrigin` 불일치 | 로그인은 되는데 저장이 403 | SPA 출처와 글자 그대로 같게 |
| 로컬 간헐 테스트 실패 | 591개 중 1개가 정확히 15초 만에 실패(`Database.Migrate()`의 연결 타임아웃) | 재실행. Windows Docker Desktop에서만, Linux CI에서는 없음 |
| Razor Pages의 `page` | 핸들러 매개변수 `int? page`가 예약 라우트 값을 바인딩하려다 실패 | `Request.Query["page"]`를 직접 읽는다 |
| MVC 바인딩의 공백 → null | `/tags/%20` 같은 경로가 `string` 매개변수를 null로 만들어 500 | 핸들러 매개변수는 `string?`, 첫 줄에서 `IsNullOrWhiteSpace` → 404 |
| Razor와 한글 | `@Model.Total건`은 컴파일되지 않는다(한글을 식별자로 읽는다) | `@(Model.Total)건` |
| EF Core 10 런타임 모델 | `db.Model.GetCheckConstraints()`가 예외 | `db.GetService<IDesignTimeModel>().Model` |
| `Accepts` 메타데이터 | Content-Type이 안 맞으면 라우팅이 인가보다 먼저 415 | 접근 테스트는 엔드포인트가 받는 형식으로 보낸다 |
| 프레임워크 기본값 | 호스트 필터 400이 HTML 본문을 보내고, publish가 `wwwroot`에 `.gz`·`.br`를 만든다(둘 다 TestServer에서는 안 보인다) | 둘 다 껐다. 새 기능을 켤 때는 publish 출력과 실제 호스트 응답을 본다 |
| 로컬 HTTP 프록시·필터 | 평문 HTTP 응답의 HTML·CSP 헤더가 변조될 수 있다(이 기계의 AdGuard에서 실측) | 실제 호스트 프로브는 HTTPS로 |
| 줄 끝 | 작업 트리는 CRLF(`autocrlf=true`), 일부 문서는 LF | 파일의 기존 스타일을 유지, 한 파일 안에서 섞지 않는다 |
