# PARA 노트앱 홈페이지 설계 (2026-09-17)

## 1. 배경 및 목적

- **무엇:** 개인 홈페이지의 1차 범위로 PARA(Projects / Areas / Resources / Archive) 방식 노트앱을 만든다. 도메인 루트가 곧 PARA 대시보드다.
- **왜:** 노션에 흩어진 PARA 노트를 내 서버(우분투 + Docker)로 옮겨 소유하고, 이후 홈페이지(소개·블로그 등)를 같은 인프라 위에 얹기 위해서다.
- **누가:** 단일 사용자. **화이트리스트 IP에서만 쓰기**, 그 외 IP는 전부 읽기 전용. 공개/비공개 플래그 없음. 로그인은 추후 확장 포인트로만 남긴다.
- **해결하는 문제:** (1) 노션 종속 탈피 — 노션 내보내기 zip을 가져오고, 노션이 읽을 수 있는 zip으로 되돌릴 수 있어야 한다. (2) 어디서나 읽되 내 네트워크에서만 쓰는 접근 모델. (3) 컨테이너 3개(`caddy`·`api`·`postgres`)로 끝나는 단순 배포.
- **출발점:** 저장소는 `dotnet new webapi` 템플릿 수준(`/weatherforecast`, `/health`)이고 DB·프론트·Docker·인증이 없다.

## 2. 설계 결정

### 2.1 전체 구조

| 후보 | 장점 | 단점 | 채택 |
|---|---|---|---|
| **A. 단일 API 프로젝트 + 기능 폴더(Vertical Slice) + Caddy** | 현재 저장소 형태를 그대로 키움. 파일 수 최소. 컨테이너 3개 | 경계 규율을 사람이 지켜야 함 | **채택** |
| B. 클린 아키텍처 4프로젝트 | 경계 강제, 다중 클라이언트에 유리 | 단일 사용자 노트앱엔 과함. DI 배선·파일 수 3배 | 기각 |
| C. Kestrel이 SPA 직접 서빙(Caddy 없음) | 컨테이너 2개 | HTTPS 자동 발급을 직접 구현. 후속 프론트 얹을 때 결국 프록시 필요 | 기각 |

### 2.2 기술 선택

| 항목 | 결정 | 근거 |
|---|---|---|
| 프론트 | React 19 + TypeScript + Vite (`WebProject.Web/`) | 생태계 최대, 마크다운 에디터 선택지 풍부, 홈페이지 확장 시 안전 |
| 백엔드 | ASP.NET Core 10 최소 API (`WebProject.Api`) | 기존 프로젝트 |
| DB | PostgreSQL + EF Core 10 (Npgsql) | 다중 사용자·전문 검색 확장 대비 |
| HTTPS | Caddy (Let's Encrypt 자동) | 설정 10줄, 정적 파일 + `/api` 프록시 겸용 |
| 노션 연동 | 내보내기 zip 가져오기 / 노션 Import용 zip 내보내기 | API 토큰·외부 통신 없음, 이사 시나리오에 적합 |
| 첨부 | 이미지, 도커 볼륨 저장, 메타는 DB | 노션 zip에 이미지가 함께 들어오므로 필수 |

### 2.3 데이터 모델 결정

- **Archive는 4번째 `Kind`.** 보관 = `Kind → Archive, PreviousKind ← 원래 Kind`, 복원은 반대. 보관 항목은 `Kind=Archive`로 PUT 하면 제목·상태 등 필드 수정이 가능하고, 종류 변경은 복원 후에만 된다.
- **영역 불변식(Codex 검증 반영).** `AreaId`는 "영역" 또는 "보관된 영역(`Kind=Archive && PreviousKind=Area`)"만 가리킨다. 보관된 영역도 유효한 소속 대상이다(과거 소속 사실 보존). 다른 항목이 참조 중인 영역은 Area가 아닌 종류로 바꿀 수 없다(400).
- **상태·마감일은 모든 Kind에 허용.** 프로젝트 전용으로 두지 않는다.
- **노트 ↔ 항목 다대다.** 노션 Relation 대응. 연결 0개 노트는 Inbox.
- **태그**(항목·노트), **할 일(Task)** 개체 포함. 할 일에는 태그 없음. 태그는 표시명 `Name`(원문)과 정규화명 `NormalizedName`(소문자·공백 정리, 유일)을 분리 저장하고, 응답의 태그 배열은 정규화명 순으로 정렬한다.
- **삭제는 하드 삭제.** 항목 삭제 시 노트는 남고 링크만 제거, 할 일은 함께 삭제.
- `NotionId`(노션 파일명의 32hex)로 재가져오기 멱등성 확보.

### 2.4 접근 제어 결정

- `IWriteAccessPolicy.CanWrite(HttpContext)` 한 인터페이스로 추상화. 1차 구현 `IpAllowlistWriteAccessPolicy`(설정 `WriteAccess:AllowedCidrs`). 로그인 도입 시 "IP 허용 OR 인증 사용자" 구현으로 교체하면 엔드포인트는 변경 없음.
- 쓰기 엔드포인트 그룹에 **엔드포인트 필터**로 부착, 거부 시 `403 ProblemDetails`. ASP.NET 인증 미들웨어는 등록하지 않는다(인증 스킴 없이 `RequireAuthorization`을 쓰면 챌린지 시점에 예외).
- **CSRF 방어(Codex 검증 반영).** IP 허용은 CSRF를 막지 못한다(허용 네트워크 안의 브라우저가 악성 사이트를 열면 그 요청도 허용 IP에서 온다). 모든 쓰기 요청은 `X-Requested-With: XMLHttpRequest` 헤더가 필수이며 같은 필터가 검사한다. 브라우저는 교차 출처 요청에 커스텀 헤더를 붙이려면 CORS 프리플라이트가 필요하고 이 API는 CORS를 열지 않으므로 교차 출처 폼 POST·multipart가 차단된다. SPA의 fetch 래퍼가 헤더를 항상 붙인다.
- Caddy 뒤 실제 IP: `ForwardedHeaders`(`X-Forwarded-For`)를 **compose 내부 네트워크에서 온 요청만** 신뢰(`KnownIPNetworks`, 설정 `ForwardedHeaders:KnownNetworks`). **주의:** ASP.NET의 이 미들웨어는 신뢰 목록이 비어 있으면 검사를 생략하고 모든 헤더를 믿으므로, 설정이 비어 있을 때는 미들웨어를 아예 등록하지 않는다(Codex 검증 반영). 통합 테스트로 신뢰/비신뢰/빈 설정 세 경우를 고정한다.
- enum은 JSON 문자열만 허용(`allowIntegerValues:false`)하고 서버가 `Enum.IsDefined`로 재검증한다. 바인딩 실패는 항상 400.
- `GET /api/me → { canWrite }`로 SPA가 읽기 전용 모드를 렌더링.

### 2.5 Codex 교차 검증 반영 (2026-09-17)

설계 스펙과 백엔드 구현 계획을 Codex CLI(read-only)로 검증하고, Claude가 재검증한 뒤 반영했다. 설계 골격(데이터 모델·API 표면·다대다·삭제 규칙)은 이상 없음 판정. 반영한 결정:

| 지적 | 결정 |
|---|---|
| 신뢰 프록시 목록이 비면 ForwardedHeaders가 헤더 위조를 통과시킴 | 설정 없으면 미들웨어 미등록, 통합 테스트로 고정 |
| IP 화이트리스트는 CSRF 방어가 아님 | 쓰기 요청에 `X-Requested-With` 헤더 필수(사용자 결정) |
| 참조 중인 영역의 보관·종류 변경 시 AreaId 불변식 깨짐 | 보관된 영역도 유효 소속으로 인정, 참조 중 종류 변경만 거부(사용자 결정) |
| 보관 항목 PUT 불능, enum 정수 허용, 태그·파일명 길이 미검증, 동시 생성 유니크 충돌 500 | 검증 로직 수정, 409 응답 |
| 첨부 고아 파일(확장자별)·이동 경쟁·LOH 버퍼 | 시그니처 기반 확장자, overwrite:false 이동, 64KB 버퍼 |
| toggle 비멱등, `POST /api/tasks` Location 대상 없음, 태그 순서 미보장, 노트 목록 절단 | PUT에 isDone, 단건 GET 추가, 태그 정렬 계약, skip/take 페이지네이션 |
| 보류 | 첨부 미참조 정리, 태그 Color API, DB readiness, 바인딩 전 정책 검사 → 7절 확장 포인트 |

반영본에 대한 Codex 2차 검증은 사용량 한도로 중단됨(2026-09-18 00:38 KST 이후 재실행 예정). 구현 계획 문서 끝의 반영표 참조.

## 3. 컴포넌트 구조

```
WebProject.sln
├─ WebProject.Api/                    # ASP.NET Core 10 최소 API
│  ├─ Program.cs                      # 서비스 등록 + MapApiEndpoints() 호출만
│  ├─ Domain/                         # 엔티티·enum (Item, Note, TaskItem, Tag, Attachment, 링크 테이블)
│  ├─ Contracts/                      # DTO·검증 오류 빌더 (Features ↔ Domain 사이 공유 계약)
│  ├─ Infrastructure/
│  │  ├─ Data/AppDbContext.cs, Migrations/
│  │  ├─ Access/IWriteAccessPolicy.cs, IpAllowlistWriteAccessPolicy.cs, RequireWriteAccessFilter.cs
│  │  └─ Storage/IAttachmentStore.cs, FileSystemAttachmentStore.cs
│  └─ Features/                       # 기능별 수직 슬라이스 (Endpoints + DTO + 검증)
│     ├─ Me/  Dashboard/  Items/  Notes/  Tasks/  Tags/  Attachments/
│     └─ Notion/  (NotionZipReader, NotionCsvMapper, NotionMarkdownParser, NotionImporter, NotionExporter)
├─ WebProject.Api.Tests/              # xUnit + WebApplicationFactory + Testcontainers.PostgreSql
│  └─ Fixtures/                       # 노션 샘플 zip
├─ WebProject.Web/                    # React 19 + TS + Vite + Tailwind v4
│  └─ src/ api/  pages/  components/  lib/
├─ deploy/                            # docker-compose.yml, Caddyfile, .env.example
└─ plan/para_notes_0917.md            # 이 문서
```

의존 방향: `Features → Infrastructure → Contracts → Domain`. `Features` 간 직접 참조 없음(공유 로직은 `Infrastructure`, 공유 DTO는 `Contracts`). 노션 파서 3종(`ZipReader`·`CsvMapper`·`MarkdownParser`)은 DB 의존 없는 순수 함수라 픽스처만으로 단위 테스트한다.

### 3.1 데이터 모델

| 테이블 | 컬럼 | 비고 |
|---|---|---|
| `Item` | `Id`(Guid v7), `Kind`(Project/Area/Resource/Archive), `PreviousKind?`, `Title`, `Description`, `Status`(Planned/Active/OnHold/Done), `DueDate?`, `AreaId?`(→ Kind=Area인 Item), `NotionId?`, `SortOrder`, `CreatedAt`, `UpdatedAt` | Area는 자기 자신을 `AreaId`로 못 가짐. Guid v7은 시간순이라 B-tree 단편화가 적음 |
| `Note` | `Id`, `Title`, `ContentMarkdown`, `NotionId?`, `CreatedAt`, `UpdatedAt` | 연결 0개면 Inbox |
| `NoteItemLink` | `NoteId`, `ItemId` (복합 PK) | 다대다 |
| `TaskItem` | `Id`, `Title`, `IsDone`, `CompletedAt?`, `DueDate?`, `ItemId?`, `SortOrder`, `NotionId?`, `CreatedAt`, `UpdatedAt` | `ItemId` null = 독립 할 일. 항목 삭제 시 cascade |
| `Tag` | `Id`, `Name`(표시명, 원문), `NormalizedName`(소문자·공백 정리, 유일), `Color?` | `Color` 설정 API는 1차 범위 밖(예약 필드) |
| `ItemTag`, `NoteTag` | 복합 PK | |
| `Attachment` | `Id`, `FileName`, `ContentType`, `SizeBytes`, `StoragePath`, `Sha256`, `CreatedAt` | 본체는 볼륨 `{sha[..2]}/{sha}.{ext}` (내용 주소, 확장자·ContentType은 파일 시그니처에서 유도). 같은 Sha256 재사용. 마크다운에서 `/api/attachments/{id}/{fileName}` 참조 |

검색 1차는 제목·본문 `ILIKE`. `tsvector` 생성 컬럼은 확장 포인트.

### 3.2 API 표면 (`/api`, JSON, DTO는 `record`)

| 영역 | 읽기(누구나) | 쓰기(화이트리스트) |
|---|---|---|
| 상태 | `GET /api/me` | |
| 대시보드 | `GET /api/dashboard` (Active 프로젝트·마감 임박 할 일·최근 노트 10개·영역 목록) | |
| 항목 | `GET /api/items?kind=&areaId=&tag=`, `GET /api/items/{id}` | `POST`, `PUT /api/items/{id}`, `DELETE`, `POST …/archive`, `POST …/restore` |
| 노트 | `GET /api/notes?itemId=&tag=&q=&inbox=&skip=&take=` → `{items, total}`(take 기본 50·최대 200), `GET /api/notes/{id}` | `POST`, `PUT`(본문 `itemIds`·`tagNames`로 연결 통째 교체), `DELETE` |
| 할 일 | `GET /api/tasks?itemId=&done=`, `GET /api/tasks/{id}` | `POST`, `PUT`(`isDone` 포함, 멱등), `DELETE`, `POST …/toggle`(편의용 비멱등) |
| 태그 | `GET /api/tags` | `DELETE /api/tags/{id}` (생성은 저장 시 이름으로 자동) |
| 첨부 | `GET /api/attachments/{id}/{fileName}` | `POST /api/attachments` (multipart, 시그니처로 PNG/JPEG/GIF/WebP 판정, 10MB) |
| 노션 | `GET /api/notion/export` (zip) | `POST /api/notion/import/analyze`, `POST /api/notion/import` |

오류는 전부 `ProblemDetails`(400 검증 / 403 쓰기 거부·CSRF 헤더 없음 / 404 / 409 동시 생성 충돌 / 413). `/health`는 유지(compose 헬스체크). 템플릿 잔재 `/weatherforecast`와 그 테스트 3파일은 삭제한다.

### 3.3 프론트엔드

- 라우트: `/` 대시보드, `/projects` `/areas` `/resources` `/archive` `/inbox`, `/items/:id`, `/notes/:id`, `/settings`(가져오기/내보내기).
- 라이브러리: react-router 7, TanStack Query 5, Tailwind v4, CodeMirror 6(마크다운) + react-markdown(미리보기).
- 이미지 붙여넣기/드롭 → `/api/attachments` 업로드 → 커서에 `![](url)` 삽입.
- `useMe().canWrite=false`면 편집 UI 숨김 + 읽기 전용 배지. 403 응답은 토스트. fetch 래퍼는 모든 요청에 `X-Requested-With: XMLHttpRequest`를 붙인다(CSRF 방어 계약).
- 개발 시 Vite 프록시 `/api → http://localhost:5055`, 배포 시 Caddy 프록시.

### 3.4 노션 가져오기 / 내보내기

노션 "Markdown & CSV" zip 구조: 데이터베이스 = `이름 <32hex>.csv` + 동명 폴더, 페이지 = `제목 <32hex>.md`, 이미지 = `제목 <32hex>/파일`. `.md`는 `# 제목`, `속성: 값` 줄들, 빈 줄, 본문 순.

**가져오기 2단계** (사용자 구조를 추측으로 밀어붙이지 않기 위해):
1. `analyze`: 임시 폴더에 해제 → CSV별 열·행 수·샘플과 **제안 매핑** 반환. 파일명 휴리스틱(project/프로젝트 → Project, area/영역 → Area, resource/자원 → Resource, task/할 일 → Task, 그 외 → Note). 열 휴리스틱(Status/상태, Date/Due/마감, Area/영역, Tags/태그, Project/Resource 관계열). 상태 문자열 매핑표(Not started/In progress/Done …, 미매핑 → Planned + 경고).
2. `import`: 사용자가 고친 매핑으로 실행. 순서 태그 → Area → Project/Resource → Task → Note(.md 파싱, 속성 헤더 제거, 이미지 링크를 첨부 업로드 후 재작성) → 관계 해석(`NotionId` 우선, 제목 폴백). 같은 `NotionId`는 갱신(멱등). 결과 리포트(생성/갱신/건너뜀 + 경고).
- CSV 없는 zip은 `.md` 전부 Inbox 노트. 중첩 하위 페이지는 평탄화해 상위 항목에 연결.
- Zip Slip 방어: 경로 정규화 후 해제 루트 밖이면 거부. 임시 폴더는 완료 후 삭제.

**내보내기**: `Projects.csv Areas.csv Resources.csv Archive.csv Tasks.csv Notes.csv` + `Notes/<제목>.md`(속성 헤더 + 본문) + `Notes/<제목>/이미지`. 노션 Import 메뉴로 올릴 수 있는 형식. **한계:** 노션 CSV 가져오기는 Relation을 텍스트 열로만 복원한다(노션 제약). 관계는 노션에서 다시 연결해야 한다.

### 3.5 배포

```
deploy/docker-compose.yml   # caddy, api, postgres / volumes: pgdata, attachments, caddy_data
deploy/Caddyfile            # {$DOMAIN} { handle /api/* { reverse_proxy api:8080 } handle { root * /srv; try_files {path} /index.html; file_server } }
deploy/.env.example         # DOMAIN, POSTGRES_PASSWORD, WRITE_ALLOWED_CIDRS
WebProject.Api/Dockerfile   # sdk:10.0 빌드 → aspnet:10.0 런타임, 비루트, /data/attachments
WebProject.Web/Dockerfile   # node:22 빌드 → caddy:2 이미지에 dist 복사(/srv)
```

- API 시작 시 `Database.Migrate()`(단일 인스턴스라 안전). 설정은 환경변수(`ConnectionStrings__Default`, `WriteAccess__AllowedCidrs__0`, `Attachments__RootPath`).
- 헬스체크: postgres `pg_isready`, api `/health`, `depends_on: condition: service_healthy`.
- 백업 = 볼륨 3개.

## 4. 핵심 API

```csharp
// Infrastructure/Access/IWriteAccessPolicy.cs
/// <summary>현재 요청이 쓰기(생성·수정·삭제)를 수행할 수 있는지 판정한다.</summary>
/// <remarks>Thread-safe(무상태). Zero-allocation. 즉시 반환(Non-blocking).
/// 1차 구현은 IP 화이트리스트, 로그인 도입 시 "IP 허용 OR 인증 사용자" 구현으로 교체한다.</remarks>
public interface IWriteAccessPolicy { bool CanWrite(HttpContext context); }

// Program.cs — 쓰기 그룹에만 필터 부착
var api = app.MapGroup("/api");
var write = api.MapGroup("").AddEndpointFilter<RequireWriteAccessFilter>();
api.MapItemReadEndpoints();  write.MapItemWriteEndpoints();

// Features/Notes — 연결 통째 교체 DTO
public sealed record UpsertNoteRequest(string Title, string ContentMarkdown, Guid[] ItemIds, string[] TagNames);

// Features/Notion — 2단계 가져오기
POST /api/notion/import/analyze  (multipart zip)          → ImportAnalysis { csvs: [{ name, columns, rowCount, suggestedKind, columnMap }], warnings }
POST /api/notion/import          (multipart zip + mapping) → ImportReport   { created, updated, skipped, warnings }
```

```ts
// WebProject.Web/src/api/me.ts — 읽기 전용 모드 근거
export const useMe = () => useQuery({ queryKey: ['me'], queryFn: () => api.get<{ canWrite: boolean }>('/api/me') });
```

## 5. 변경 파일 목록

| 단계 | 신규/수정 | 내용 |
|---|---|---|
| 0 | `plan/para_notes_0917.md`, `CLAUDE.md`, `AGENTS.md`, `.gitignore` | 이 문서, 구성 절·플랜 표 갱신, `WebProject.Web/dist/`·`deploy/.env` 제외 |
| 0 | `WebProject.Api/Program.cs`, `WebProject.Api.Tests/WeatherForecast*.cs`(3파일 삭제) | 템플릿 잔재 제거, `/health`·`HealthEndpointTests` 유지 |
| 1 | `WebProject.Api/Domain/*`, `Contracts/*`, `Infrastructure/Data/*`, `Infrastructure/Access/*`, `Features/{Me,Items,Tags}/*`, csproj 패키지(EF Core·Npgsql) | 엔티티·DTO·마이그레이션·항목/태그 API·접근 제어 |
| 1 | `WebProject.Api.Tests/PostgresFixture.cs`, `Items*Tests.cs`, `CidrTests.cs`, csproj(Testcontainers) | 실제 Postgres 통합 테스트 |
| 2 | `Features/{Notes,Tasks,Attachments,Dashboard}/*`, `Infrastructure/Storage/*` + 테스트 | 노트·할 일·첨부·대시보드 |
| 3 | `WebProject.Web/**` | SPA 전체 |
| 4 | `Features/Notion/*`, `WebProject.Api.Tests/Fixtures/*.zip`, SPA `/settings` | 노션 가져오기/내보내기 |
| 5 | `deploy/*`, `WebProject.Api/Dockerfile`, `WebProject.Web/Dockerfile`, `.github/workflows/ci.yml`, `README.md` | 배포·CI(`ubuntu-latest` + web 잡) |

## 6. 빌드 검증

```powershell
dotnet build WebProject.sln -c Release
dotnet test  WebProject.sln -c Release            # Docker Desktop 필요(Testcontainers)
cd WebProject.Web; npm ci; npx tsc --noEmit; npm run build
cd deploy; docker compose up --build -d; curl -f http://localhost/health
```

- 화이트리스트 밖에서 `POST /api/items` → 403, 안에서 → 201.
- 노션 픽스처 zip을 2회 가져오기 → 건수 동일(멱등).
- 내보낸 zip을 노션 Import로 올려 페이지·DB 생성 확인(수동).

## 7. 향후 확장 포인트

- **로그인:** `IWriteAccessPolicy` 구현 교체 + ASP.NET Identity/쿠키. 엔드포인트 변경 없음.
- **노션 API 동기화:** 토큰 등록 후 주기 동기화(현재는 zip 라운드트립만).
- **전문 검색:** `tsvector` 생성 컬럼 + GIN 인덱스.
- **홈페이지 확장:** 랜딩·블로그를 Caddy 하위 경로 또는 별도 정적 사이트로.
- **공개 노트 플래그:** 노트 단위 공개/비공개(현재는 전부 읽기 공개).
- **첨부 미참조 정리:** 노트 저장 취소로 남은 첨부를 유예 기간 뒤 정리하는 잡. **태그 Color 설정 API.** **DB readiness 헬스체크** 분리. **바인딩 전 쓰기 정책 검사**(업로드 본문 버퍼링 전 거부).

## 8. 구현 계획 문서

| 계획 | 파일 | 범위 |
|---|---|---|
| Plan 1 | `docs/superpowers/plans/2026-09-17-para-notes-backend.md` | 0~2단계: 도메인·DB·접근 제어·항목/노트/할 일/태그/첨부/대시보드 API |
| Plan 2 | (Plan 1 완료 후 작성) | 3단계 SPA |
| Plan 3 | (Plan 2 완료 후 작성) | 4단계 노션 가져오기/내보내기 |
| Plan 4 | (Plan 3 완료 후 작성) | 5단계 Docker·Caddy·CI |
