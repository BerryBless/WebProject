# PortfolioBlog — 보안 최우선 기술 블로그

단일 작성자용 기술 블로그입니다. 방문자에게는 **스크립트 없는 서버 렌더링 HTML**만 내보내고, 글쓰기는 **별도 서브도메인 + IP 화이트리스트 + 비밀번호 세션** 뒤에 둡니다.

> **현재 상태: 1단계(관리 API·접근 제어)·2A단계(마크다운 파이프라인·미리보기·이미지 첨부)·2B단계(공개 페이지·검색·Atom·sitemap·보안 헤더·속도 제한·첨부 정합성) 완료(`feature/blog-public-site` 브랜치, 최종 리뷰·병합 대기), 관리 에디터는 구현 전.** 도메인·DB 제약, 접근 제어(호스트·IP·CSRF), 비밀번호 로그인·세션, 글·시리즈·태그 관리 API, 마크다운 렌더링·미리보기·이미지 첨부, 방문자용 공개 페이지(서버 렌더링)·검색·Atom 피드·sitemap이 구현·테스트되었습니다. 관리 에디터 SPA, 배포 구성은 아직 없습니다. 전체 스펙은 [`plan/tech_blog_0920.md`](plan/tech_blog_0920.md), 2A단계 실행 결과는 [`plan/tech_blog_2a_report_0921.md`](plan/tech_blog_2a_report_0921.md), **이어서 작업하는 방법은 [`plan/resume_guide_0921.md`](plan/resume_guide_0921.md)**에 있습니다.

## 무엇을 만드나

| | |
|---|---|
| 기능 | 글 CRUD, 태그, 시리즈(연재 묶음), 이미지 첨부, 마크다운 에디터, 코드 하이라이팅, 검색, Atom 피드, SEO(Open Graph·sitemap) |
| 발행 모델 | 초안 상태 없음. **명시적 저장 = 즉시 공개** |
| 사용자 | 작성자 1명. 회원·댓글 없음 |
| 스택 | ASP.NET Core 10(최소 API + Razor Pages), EF Core 10 + PostgreSQL, Markdig, ColorCode.HTML(코드 하이라이팅), HtmlSanitizer, React 19 + Vite + CodeMirror 6(관리 에디터 전용), Caddy, Docker Compose |

## 보안 설계 요약

설계 선택이 갈릴 때마다 편의보다 공격 표면 축소를 택했습니다.

| 결정 | 이유 |
|---|---|
| 공개 페이지는 서버 렌더링, JS 없음 | CSP를 `default-src 'none'`까지 조일 수 있어 저장형 XSS가 들어와도 실행되지 않습니다. npm 공급망 사고가 나도 방문자는 영향을 받지 않습니다 |
| 관리 화면은 `admin.<도메인>`으로 분리 | 경로 분리(`/admin`)는 origin 격리가 아닙니다. 서브도메인으로 나누면 세션 쿠키가 공개 호스트로 전송되지 않고, 공개 도메인에는 `/api` 자체가 존재하지 않습니다 |
| 쓰기 = 화이트리스트 IP **AND** 비밀번호 세션 | 네트워크 위치 단일 요소에 의존하지 않습니다. 로그인 엔드포인트가 허용 IP에서만 열리므로 비밀번호 대입 표면이 없습니다 |
| 접근 검사는 요청 본문을 읽기 전에 완료 | 미인증 요청이 10MB 업로드 본문을 서버에 버퍼링시키지 못합니다 |
| 마크다운은 3겹으로 정제 | raw HTML 비활성 + URL 정책 → 최종 HTML 허용 목록 정제 → CSP |
| HTML을 저장하지 않고 요청마다 렌더링 | 렌더러 보안 수정이 과거 글 전체에 즉시 적용됩니다 |
| 세션 폐기 | 절대 수명 12시간. 비밀번호를 바꾸면 기존 세션이 자동 폐기되고, 로그아웃은 모든 세션을 폐기합니다 |

설계 초안은 OpenAI Codex CLI로 교차 검토했고(지적 23건), 수용한 항목과 의견이 갈린 4건의 근거를 스펙 2.6절에 기록했습니다.

## 아키텍처

### 신뢰 경계

```mermaid
flowchart TB
    V["방문자 (임의 IP)"]
    A["작성자 (화이트리스트 IP)"]

    subgraph caddy["Caddy (TLS 종단)"]
        PUB["공개 도메인"]
        ADM["admin 서브도메인"]
        G0{"원본 IP가<br/>허용 목록 안?"}
        SPA["관리 SPA 정적 파일"]
    end

    subgraph api["api 컨테이너 (외부 포트 없음)"]
        RZ["공개 Razor Pages<br/>GET 전용, 스크립트 없음"]
        MW["관리 표면 미들웨어<br/>호스트 · IP · CSRF 헤더 · Origin"]
        AUTH["쿠키 인증 + 세션 검증"]
        EP["관리 API"]
    end

    DB[("postgres")]
    FS[("attachments 볼륨")]

    V --> PUB
    A --> PUB
    A --> ADM
    PUB -->|"/api*"| X1["404"]
    PUB -->|"그 외"| RZ
    ADM --> G0
    G0 -->|"아니오"| X2["404"]
    G0 -->|"예, 정적 경로"| SPA
    G0 -->|"예, /api/*"| MW
    MW --> AUTH --> EP
    RZ --> DB
    RZ --> FS
    EP --> DB
    EP --> FS
```

| | 공개 표면 (`<도메인>`) | 관리 표면 (`admin.<도메인>`) |
|---|---|---|
| 경로 | `/`, `/posts/{slug}`, `/tags/{tag}`, `/series/{slug}`, `/search`, `/feed.xml`, `/sitemap.xml`, `/attachments/…` | `/`(에디터 SPA), `/api/*`, `/attachments/…`(GET/HEAD, 읽기 전용) |
| 누가 | 누구나, GET만 | 화이트리스트 IP AND 로그인 세션 |
| JS | 없음 | `script-src 'self'` |
| 상태 변경 | 불가능(엔드포인트가 이 호스트에 없음) | 전부 여기에만 |

허용되지 않은 `Host` 헤더로 접속하면 **본문 없는 400**이 나옵니다(설정된 두 origin — `Site:PublicOrigin`·`Site:AdminOrigin` — 의 호스트만 허용합니다).

### 관리 API 접근 판정

방어선은 3겹입니다: Caddy의 IP 차단 → 앱의 호스트·IP·CSRF 검사 → 로그인 세션. 아래 판정은 전부 요청 본문을 읽기 전에 끝납니다.

```mermaid
flowchart TD
    S["/api/* 요청"] --> H{"Host == 관리 호스트?"}
    H -->|"아니오"| R404["404"]
    H -->|"예"| IP{"원본 IP가<br/>허용 CIDR 안?"}
    IP -->|"아니오"| R403a["403"]
    IP -->|"예"| XH{"X-Requested-With 헤더?"}
    XH -->|"없음"| R403b["403"]
    XH -->|"있음"| M{"GET/HEAD?"}
    M -->|"아니오"| O{"Origin == 관리 origin?"}
    O -->|"아니오"| R403c["403"]
    O -->|"예"| L
    M -->|"예"| L{"로그인 불필요?<br/>(login, me)"}
    L -->|"예"| RL["속도 제한 → 엔드포인트"]
    L -->|"아니오"| C{"쿠키 유효?<br/>절대 만료 · 비밀번호 지문 · 세션 epoch"}
    C -->|"아니오"| R401["401"]
    C -->|"예"| E["바인딩 → 엔드포인트"]
```

### 로그인과 세션

아이디 없이 비밀번호만 받습니다. 서버에는 해시만 환경변수로 주입합니다.

```mermaid
sequenceDiagram
    autonumber
    actor W as 작성자 브라우저
    participant C as Caddy (admin 사이트)
    participant M as 관리 표면 미들웨어
    participant A as Auth 엔드포인트
    participant D as postgres

    W->>C: POST /api/auth/login {password}
    C->>C: 원본 IP 검사 (밖이면 404)
    C->>M: 프록시
    M->>M: 호스트 · IP · 헤더 · Origin 검사
    M->>A: 속도 제한 통과 시
    Note over A: IP별 5회/분 + 전역 20회/분<br/>+ 해시 검증 동시 실행 2
    A->>A: PBKDF2 해시 검증
    alt 불일치
        A-->>W: 401
    else 일치
        A->>D: 세션 epoch 조회
        A-->>W: 204 + __Host- 쿠키<br/>(HttpOnly, Secure, SameSite=Strict)
    end

    W->>C: PUT /api/posts/{id} (쿠키)
    C->>M: 프록시
    M->>A: 검사 통과
    A->>A: 발급 후 12시간 이내?<br/>비밀번호 지문 일치?
    A->>D: epoch 일치?
    alt 하나라도 불일치
        A-->>W: 401
    end
```

### 마크다운 파이프라인

공개 페이지와 에디터 미리보기가 같은 파이프라인을 씁니다(구현 완료: `Infrastructure/Markdown`, `POST /api/preview`). 전체가 DB 의존 없는 순수 함수라 XSS 공격 코퍼스를 단위 테스트로 돌립니다.

```mermaid
flowchart LR
    MD["마크다운 원문<br/>(≤ 200KB)"] --> P["Markdig 파싱<br/>raw HTML 비활성<br/>확장 허용 목록"]
    P --> U["URL 정책<br/>링크: http(s) · mailto · 상대경로<br/>이미지: 자체 첨부만"]
    U --> HL["서버 측 코드 하이라이팅<br/>CSS 클래스만 출력"]
    HL --> SAN["HTML 허용 목록 정제"]
    SAN --> PAGE["공개 페이지<br/>CSP default-src 'none'"]
    SAN --> PRE["미리보기<br/>sandbox iframe"]
```

렌더 비용은 입력 크기가 아니라 **시간**으로 제한합니다. 코드 강조는 줄 400자·문서 60,000자 예산 안에서만 시도하고, 정규식 매치 타임아웃 250ms와 렌더당 강조 시간 2,000ms를 넘긴 블록은 이스케이프한 일반 코드블록으로 떨어집니다(닫히지 않은 `/*` 뒤의 코드처럼 1KB 남짓한 입력으로 강조기가 수십 초 걸리는 경우를 막습니다). 128단계를 넘는 중첩은 필드 키가 있는 400으로 거부하고, 글은 저장 전에 한 번 렌더링해 "저장은 됐는데 공개 페이지가 열리지 않는" 상태를 막습니다. Markdig 파서 자체의 비용(적대적 200KB 입력에서 수 초)은 남아 있어 2B단계에서 렌더 결과 캐시로 덮습니다.

### 데이터 모델

```mermaid
erDiagram
    Series ||--o{ Post : "0..1 소속"
    Post ||--o{ PostTag : ""
    Tag ||--o{ PostTag : ""
    Post {
        uuid Id PK
        text Slug UK "직접 입력, 생성 후 불변"
        text Title
        text Summary
        text ContentMarkdown
        uuid SeriesId FK
        int SeriesOrder
        timestamptz CreatedAt "발행일"
        timestamptz UpdatedAt
    }
    Series {
        uuid Id PK
        text Slug UK
        text Title
        text Description
    }
    Tag {
        uuid Id PK
        text Name
        text NormalizedName UK
    }
    PostTag {
        uuid PostId PK
        uuid TagId PK
    }
    Attachment {
        uuid Id PK
        text FileName
        text ContentType
        bigint SizeBytes
        text Sha256 UK "내용 주소 저장"
    }
```

첨부는 파일 시그니처로 PNG/JPEG/GIF/WebP만 허용하고(SVG 불가, 클라이언트가 보낸 파일 이름·Content-Type은 믿지 않음), 디코딩 없이 컨테이너 구조만 읽어 EXIF·GPS·XMP·IPTC·주석·썸네일을 제거한 뒤 내용 주소(SHA-256)로 저장합니다. 제거기는 네 형식 모두 **기본 거부**입니다 — 허용한 블록만 남기고 그 모양(크기·서브블록 구조)까지 검사하며, 파일 끝에 덧붙인 데이터는 버립니다. 디코더 없이는 닫을 수 없는 표면(ICC 프로파일 본문, GIF 픽셀 데이터 체인 등)은 스펙 3.8절에 잔여 표면으로 적어 두었고 10MB 상한·시그니처 기반 Content-Type·`nosniff`·`Content-Security-Policy: default-src 'none'; sandbox`로 완화합니다. 공개 `GET/HEAD /attachments/{id}/{fileName}`은 강한 ETag(SHA-256)와 `immutable` 캐시로 응답합니다. 동시 수정은 PostgreSQL `xmin` 동시성 토큰으로 감지합니다(409).

## 저장소 구조

```
PortfolioBlog.slnx
├─ PortfolioBlog.Api/          # ASP.NET Core 10 — 관리 API(최소 API) + 공개 페이지(Razor Pages)
│  ├─ Pages/                # 공개 Razor 페이지(GET/HEAD·공개 호스트 전용 규약, PublicPageConvention)
│  └─ wwwroot/               # 정적 파일 — css/site.css 하나뿐
├─ PortfolioBlog.Api.Tests/    # xUnit + WebApplicationFactory + Testcontainers PostgreSQL
├─ PortfolioBlog.Web/          # 관리 에디터 SPA — React 19 + Vite (예정)
├─ deploy/                  # docker-compose · Caddyfile · 운영 절차 (예정)
├─ plan/                    # 설계 문서
└─ .claude/ .agents/ .codex/ scripts/   # 개발 하네스
```

## 로드맵

| 단계 | 내용 | 상태 |
|---|---|---|
| 설계 | 스펙 작성, Codex 교차 검토 반영 | 완료 |
| 0 | 솔루션 정리(`PortfolioBlog`로 개명, `.slnx` 전환, 템플릿 잔재·샘플 프로젝트 제거) | 완료 |
| 1 | 도메인·DB 제약, 접근 제어(호스트·IP·CSRF), 비밀번호 로그인·세션 폐기, 글·시리즈·태그 관리 API | 완료 |
| 2 | 마크다운 파이프라인, 첨부, 공개 Razor 페이지, 검색, Atom, sitemap, 보안 헤더, 속도 제한 | 2A 완료(마크다운 파이프라인·미리보기·첨부) / 2B 완료(공개 페이지·검색·피드·보안 헤더·속도 제한·첨부 정합성, `feature/blog-public-site` 브랜치, 최종 리뷰·병합 대기) |
| 3 | 관리 에디터 SPA | 예정 |
| 4 | Docker Compose · Caddy · CI · 백업/복원 절차 | 예정 |

## 시작하기

```powershell
git clone https://github.com/BerryBless/WebProject.git
cd WebProject
Copy-Item scripts/git-hooks/commit-msg .git/hooks/
dotnet build PortfolioBlog.slnx
dotnet test  PortfolioBlog.slnx
```

.NET 10 SDK가 필요합니다. 1단계부터는 통합 테스트가 Testcontainers로 실제 PostgreSQL을 띄우므로 Docker도 필요합니다. 현재 테스트는 591개이며 Release 빌드는 경고 0입니다. Windows Docker Desktop에서는 드물게 테스트 1개가 DB 연결 타임아웃으로 실패할 수 있습니다 — 다시 실행하면 통과합니다([재개 가이드](plan/resume_guide_0921.md) 5절).

### 로컬 실행 (API)

관리 API와 공개 페이지를 직접 띄워보려면(관리 에디터 SPA는 아직 없으므로 관리 쪽은 `.http` 요청이나 REST 클라이언트로 호출합니다):

1. 개발용 Postgres를 띄웁니다(`appsettings.Development.json`의 연결 문자열과 맞춤): `docker run -d --name blog-dev-pg -e POSTGRES_PASSWORD=changeme -e POSTGRES_DB=blog_dev -p 5432:5432 postgres:17-alpine`
2. 관리자 비밀번호 해시를 user-secrets에 저장합니다(저장소에는 남지 않습니다): `dotnet user-secrets init --project PortfolioBlog.Api` 후 `dotnet run --project PortfolioBlog.Api -- hash-password`로 해시를 뽑아 `dotnet user-secrets set "Admin:PasswordHash" "<해시>" --project PortfolioBlog.Api`.
3. `dotnet dev-certs https --trust`로 개발 인증서를 신뢰한 뒤 `dotnet run --project PortfolioBlog.Api --launch-profile https`로 실행합니다(세션 쿠키가 Secure라 https 프로필이 필요합니다).

`appsettings.Development.json`은 `Site:PublicOrigin`·`Site:AdminOrigin`을 둘 다 `https://localhost:7198`로 두므로, 같은 주소가 공개·관리 양쪽 역할을 겸합니다. 앱이 뜨면 **`https://localhost:7198/`**을 브라우저로 열어 공개 첫 쪽(최신 글 목록)을 볼 수 있습니다. 관리 API 호출은 `PortfolioBlog.Api/PortfolioBlog.Api.http`에 상태 확인·로그인·글 생성·글 목록·미리보기·첨부 업로드 예시 요청이 있고, 같은 파일 뒤쪽에 공개 페이지·태그·검색·피드·sitemap·robots 요청 예시도 있습니다.

EF Core 마이그레이션 도구는 컨텍스트가 둘(`AppDbContext`·`PublicDbContext`)이라 `--context AppDbContext`가 필요합니다: `dotnet ef migrations add <이름> --project PortfolioBlog.Api --context AppDbContext`.

### 관리 SPA

`PortfolioBlog.Web`은 관리 에디터 전용 React 19 + TypeScript + Vite SPA입니다. Node 24가 필요합니다(다른 버전은 검증하지 않았습니다).

```powershell
cd PortfolioBlog.Web
npm ci
npm run certs   # .NET 개발 인증서를 .certs/로 내보냅니다(node scripts/e2e-prepare.mjs certs) — SPA도 세션 쿠키가 Secure라 HTTPS로 떠야 합니다
```

백엔드를 이 SPA의 출처로 띄운 뒤(세션 쿠키의 Origin 검사가 `Site:AdminOrigin`과 SPA 출처가 같아야 통과합니다):

```powershell
$env:Site__AdminOrigin = 'https://localhost:5173'
dotnet run --project PortfolioBlog.Api --launch-profile https
```

새 터미널에서 `npm run dev`로 개발 서버를 띄웁니다(`https://localhost:5173`, `/api`·`/attachments`는 `https://localhost:7198`으로 프록시됩니다).

단위 테스트: `npm test`(Vitest, 179개 — jsdom, 실제 백엔드 없이 컴포넌트·유틸리티를 검사합니다). 정적 검사: `npm run lint`(oxlint) · `npm run typecheck`(`tsc -b`).

E2E(Playwright, 실제 백엔드 + PostgreSQL + production 빌드 + 배포용 보안 헤더 — Docker Desktop 필요):

```powershell
npm run e2e:prepare   # 개발 인증서, 버려질 PostgreSQL 컨테이너(pb-e2e-pg), 버려질 관리자 비밀번호의 해시를 준비합니다
npm run e2e           # Chromium·Firefox에서 8개 테스트를 순차 실행합니다(재시도 없음)
docker rm -f pb-e2e-pg   # 끝나면 정리합니다
```

미리보기 iframe에 넣는 공개 사이트 CSS는 `PortfolioBlog.Api/wwwroot/css/site.css`(와 서버가 만드는 강조 CSS)의 스냅숏이며, 사본은 `PortfolioBlog.Web/public/preview/{site,highlight}.css`에 있습니다(`PortfolioBlog.Web/src/lib/previewDoc.ts`는 이 파일들을 참조할 뿐입니다). 공개 사이트 CSS를 바꾸면 이 스냅숏도 갱신해야 합니다:

```powershell
$env:UPDATE_PREVIEW_SNAPSHOTS = '1'
dotnet test PortfolioBlog.Api.Tests -c Release --filter "FullyQualifiedName~PreviewCssSnapshotTests"
Remove-Item Env:UPDATE_PREVIEW_SNAPSHOTS
dotnet test PortfolioBlog.Api.Tests -c Release --filter "FullyQualifiedName~PreviewCssSnapshotTests"   # 통과를 확인
```

갱신 실행은 파일을 쓴 뒤 **의도적으로 실패로 끝납니다**(환경변수를 끄지 않은 채로 두면 드리프트 검사가 항상 통과해 버리는 것을 막기 위해서입니다) — 환경변수를 끄고 다시 돌려 통과를 확인하세요.

### 설정 키

`appsettings.json`·`appsettings.Development.json`·환경변수·user-secrets로 채웁니다. 기본값은 코드(`SiteOptions`·`PublicOptions`·`RenderingOptions`·`AdminOptions`·`AttachmentOptions`)에서 확인한 값입니다.

| 키 | 기본값 | 의미 |
|---|---|---|
| `Site:Title` | `Blog`(공백 불가 — 시작 실패) | `<title>`·머리글·Atom 피드 제목 |
| `Site:Description` | (빈 값) | 첫 쪽 meta description·Atom subtitle. 비어 있으면 생략 |
| `Site:Author` | (빈 값) | Atom 작성자 이름. 비어 있으면 `Site:Title`을 쓴다 |
| `Public:PagePerIpPerMinute` | `120` | 공개 페이지·Atom·sitemap의 IP별 분당 한도 |
| `Public:AssetPerIpPerMinute` | `600` | 첨부 GET·`/health`·`robots.txt`·`highlight.css`의 IP별 분당 한도 |
| `Public:SearchPerIpPerMinute` | `20` | `/search`의 IP별 분당 한도 |
| `Public:SearchConcurrency` | `4` | 검색의 전역 동시 실행 한도 |
| `Public:StatementTimeoutMs` | `3000` | 공개 조회 연결의 `statement_timeout`(밀리초). 100~60000 밖이면 시작 실패 |
| `ConnectionStrings:Default` | (빈 값 — 없으면 시작 실패) | 관리 연결 문자열. **`Options`를 넣을 수 없다**(공개 조회 연결이 `statement_timeout`·`default_transaction_read_only`를 시작 옵션으로 붙이므로 합칠 수 없어 시작 실패). `Command Timeout`(초)을 지정하면 ×1000이 `Public:StatementTimeoutMs`보다 **커야** 한다 — 어기면 시작 실패(클라이언트 취소가 DB의 `statement_timeout`보다 먼저 나면 503 매핑이 깨진다). `Command Timeout=0`(무한)은 이 검사에서 제외 |
| `Rendering:Concurrency` | `2` | 프로세스 전체 동시 렌더 수(미리보기·저장·공개 페이지 합산) |
| `Rendering:QueueTimeoutMs` | `5000` | 렌더 슬롯 대기 상한(밀리초). 넘으면 503 |
| `Rendering:CacheMegabytes` | `64` | 렌더 결과 캐시의 메모리 상한(MB) |
| `Admin:LoginPerIpPerMinute` | `5` | 분당 IP별 로그인 시도 제한 횟수 |
| `Admin:LoginGlobalPerMinute` | `20` | 분당 전체 로그인 시도 제한 횟수 |
| `Admin:LoginConcurrency` | `2` | 동시에 실행할 수 있는 해시 검증 수(PBKDF2는 CPU 바운드) |
| `Admin:SessionHours` | `12` | 세션 절대 수명(시간). sliding 연장 없음 |
| `Admin:UploadPerMinute` | `30` | 첨부 업로드의 분당 전역 한도 |
| `Admin:UploadConcurrency` | `2` | 첨부 업로드의 동시 실행 한도 |
| `Attachments:JanitorEnabled` | `true` | 첨부 고아 파일 청소 잡 실행 여부(테스트는 끈다). 켜면 **시작 직후 1회 + 이후 6시간마다** 스윕한다: 마지막 쓰기가 **1시간 안**인 파일은 진행 중인 업로드일 수 있어 건드리지 않고, 내용 주소 모양(`ab/<sha256>.<ext>`)이 아닌 파일과 심볼릭 링크·정션도 건너뛴다. 지우는 것은 오래된 `.tmp` 파일과 참조하는 행이 없는 오래된 첨부 파일뿐이며, 삭제 직전 같은 내용의 잠금 안에서 DB를 다시 조회한다. `파일이 없는 첨부 행 N건` 경고는 **그 반대 방향의 불일치**(DB 행은 있는데 파일이 없다)를 알리는 진단이며 청소 잡이 고치지 않는다 — 같은 이미지를 다시 올리면 복구된다 |

## 문서

| 문서 | 내용 |
|---|---|
| [`plan/tech_blog_0920.md`](plan/tech_blog_0920.md) | 전체 설계 스펙: 설계 결정과 대안 비교, 접근 계약, 응답 헤더·CSP, 자원 제한, 배포, 필수 테스트, Codex 검토 반영표 |
| [`docs/superpowers/plans/2026-09-20-tech-blog-backend-core.md`](docs/superpowers/plans/2026-09-20-tech-blog-backend-core.md) | 1단계 구현 계획: TDD 단계별 작업 7개(도메인·접근 제어·로그인·관리 API) |
| [`docs/superpowers/plans/2026-09-21-tech-blog-content-pipeline.md`](docs/superpowers/plans/2026-09-21-tech-blog-content-pipeline.md) | 2A단계 구현 계획: 마크다운 파이프라인·미리보기·이미지 첨부 |
| [`plan/tech_blog_2a_report_0921.md`](plan/tech_blog_2a_report_0921.md) | 2A단계 실행 보고서: 검증 근거, 계획 결함 16건과 교훈, 내린 판정 12건, 수용한 잔여 위험, 알려진 문제 |
| [`plan/resume_guide_0921.md`](plan/resume_guide_0921.md) | **작업 재개 가이드**: 현재 상태, 5분 점검, 다음 작업(Plan 2B)과 입력 자료, 실행이 끊겼을 때 복구, 자주 밟는 함정, 문서·코드 지도 |
| [`plan/para_notes_0917.md`](plan/para_notes_0917.md) | 폐기된 이전 설계(PARA 노트앱). 결정 이력 보존용 |
| [`plan/harness_changelog.md`](plan/harness_changelog.md) | 개발 하네스 변경 이력 |
| [`CLAUDE.md`](CLAUDE.md) / [`AGENTS.md`](AGENTS.md) | 프로젝트 규칙 (Claude Code / Codex) |

## 개발 하네스

이 저장소는 `ClaudeCodeStudy`의 AI 협업 하네스를 이식해 시작했습니다. 설계와 구현을 Claude Code가 진행하고 OpenAI Codex CLI가 read-only로 교차 검증합니다.

| 구성 | 위치 | 역할 |
|------|------|------|
| Claude 에이전트 25종 | `.claude/agents/` | 코드 리뷰·동시성·GC·파이프라인·TDD·교차 검증 전문 에이전트 |
| Claude 스킬 26종 | `.claude/skills/` | `/commitandpush`, `code-review-orchestrator`, `cross-verify`, `codex` 등 |
| Codex 미러 | `.agents/skills/`, `.codex/` | Codex CLI가 동일 규칙을 읽도록 한 미러(`cross-verify`·`codex`는 의도적으로 제외) |
| Stop 훅 자동 커밋 | `.claude/settings.json`, `scripts/auto-commit.ps1` | 턴 종료 시 `.git/auto_commit_msg.txt`를 읽어 커밋·푸시 |
| 커밋 메시지 훅 | `scripts/git-hooks/commit-msg` | `{접두사}: {제목}` 형식 강제 (클론 후 `.git/hooks/`에 복사) |
| 쓰기 범위 훅 | `scripts/hooks/guard-write-scope.ps1` | 감사·리뷰 전용 에이전트가 자기 작업 디렉터리 밖에 쓰지 못하게 차단 |
| 하네스 감사 | `scripts/harness-audit.ps1` | 에이전트·스킬·미러 구조 8개 항목 검사 |
| CI | `.github/workflows/ci.yml` | push/PR 시 restore → build → test (.NET 10) |
