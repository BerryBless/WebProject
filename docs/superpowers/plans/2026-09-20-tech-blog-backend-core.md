# 기술 블로그 백엔드 코어 구현 계획 (Plan 1/4)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** PostgreSQL 위에 글·시리즈·태그 관리 API와 "관리 호스트 + 화이트리스트 IP + CSRF 헤더 + 비밀번호 세션" 접근 제어를 갖춘 `PortfolioBlog.Api`를 완성한다(스펙 1단계).

**Architecture:** 단일 ASP.NET Core 10 프로젝트를 기능 폴더로 나누고 `Features → Infrastructure → Contracts → Domain` 방향으로만 의존한다. `/api/*` 요청은 전용 미들웨어가 **본문을 읽기 전에** 호스트·IP·CSRF 헤더·Origin을 검사하고, 그 뒤 쿠키 인증(`ValidatePrincipal`에서 절대 만료·비밀번호 지문·세션 epoch 검증)과 인가 정책이 세션을 강제한다. 테스트는 Testcontainers로 띄운 실제 PostgreSQL에 대해 `WebApplicationFactory<Program>`으로 실행한다.

**Tech Stack:** .NET SDK 10.0.303, ASP.NET Core Minimal API, EF Core 10 + `Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3`, `Microsoft.EntityFrameworkCore.Design 10.0.12`, 프레임워크 내장 `Microsoft.AspNetCore.Identity.PasswordHasher<T>`(PBKDF2-HMAC-SHA512, 추가 패키지 없음), xUnit 2.9.3, `Testcontainers.PostgreSql 4.15.0`, PostgreSQL 17(`postgres:17-alpine`).

**Spec:** `plan/tech_blog_0920.md` (승인됨). 이 계획은 1단계만 다룬다. 후속: Plan 2(마크다운 파이프라인·첨부·공개 Razor 페이지·피드·보안 헤더), Plan 3(관리 SPA), Plan 4(Docker·Caddy·CI·운영 절차).

## Global Constraints

- 대상 프레임워크 `net10.0`, `Nullable=enable`, `ImplicitUsings=enable`. 빌드는 **경고 0·오류 0**을 유지한다.
- 솔루션 파일은 `PortfolioBlog.slnx`. 네임스페이스: 새 코드는 `PortfolioBlog.Api.*`(`.Domain`, `.Contracts`, `.Infrastructure.Data`, `.Infrastructure.Access`, `.Features.<이름>`), 테스트는 `PortfolioBlog.Api.Tests.*`.
- 의존 방향: `Features → Infrastructure → Contracts → Domain`. `Features` 간 직접 참조 금지.
- **주석 규칙(CLAUDE.md, 필수):** 모든 public 타입·메서드·인터페이스에 한국어 XML 문서 주석을 달고 `<remarks>`에 `<b>[성능 및 동시성 제약 조건]</b>` 목록으로 **Thread Safety / Memory Allocation / Blocking** 3항목을 기재한다. 메모리·네트워크·동시성 타입(`SemaphoreSlim`, `RateLimiter`, `IPNetwork[]`, `HttpClient` 등) 선언부에는 "왜 이 타입인가"를 **내부 동작 근거**로 인라인 `//` 주석을 단다. 이 계획의 코드 블록은 분량상 대표 주석만 보이므로 **구현 시 모든 public 멤버에 아래 템플릿을 채운다.**

```csharp
/// <summary>(한 문장 역할)</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> (Thread-safe / Not Thread-safe + 이유)</description></item>
/// <item><description><b>Memory Allocation:</b> (힙 할당 여부·규모, 버퍼 소유권·생명주기)</description></item>
/// <item><description><b>Blocking:</b> (즉시 반환 / 동기 블로킹 / 비동기 Non-blocking)</description></item>
/// </list>
/// </remarks>
```

- DB에 저장하는 시각은 항상 `DbClock.UtcNow()`(UTC + 마이크로초 절삭). ID는 `Guid.CreateVersion7()`.
- 오류 응답은 전부 `ProblemDetails`. 바인딩 실패는 예외가 아니라 400(`RouteHandlerOptions.ThrowOnBadRequest=false`). API는 로그인 페이지로 리다이렉트하지 않는다(401/403).
- **설정은 `builder.Build()` 이후에만 읽는다**(`IOptions<T>` 지연 바인딩 또는 `app.Configuration`). `WebApplicationFactory`의 `UseSetting` 값이 `Build()` 전에는 보이지 않을 수 있기 때문이다. `AddXxx(o => ...)` 람다 안에서 설정이 필요하면 `AddOptions<T>().Configure<IOptions<U>>(...)` 패턴을 쓴다.
- 접근 계약(스펙 3.3): `/api/*`는 (1) Host == 관리 호스트 아니면 404, (2) 원본 IP가 허용 CIDR 밖이면 403, (3) `X-Requested-With: XMLHttpRequest` 없으면 403, (4) GET/HEAD가 아닌데 `Origin != Site:AdminOrigin`이면 403, (5) `login`·`me` 외에는 세션 없으면 401. 전부 본문 바인딩 전에 끝난다. CORS는 등록하지 않는다. `/api` 응답은 `Cache-Control: no-store`.
- 미들웨어 순서: `ForwardedHeaders`(신뢰 프록시 설정 시에만) → `AdminSurfaceMiddleware` → `RateLimiter` → `Authentication` → `Authorization` → 엔드포인트. **IP 검사가 속도 제한보다 앞**이어야 외부 요청이 로그인 전역 한도를 소진시키지 못한다.
- 비밀번호·쿠키·요청 본문은 로그에 남기지 않는다. 로그인 실패 로그에는 원본 IP만 남긴다.
- 커밋 메시지: `.git/hooks/commit-msg`가 `{접두사}: {제목}` 형식을 강제한다(접두사: 추가|수정|버그수정|리팩토링|문서|테스트|의존성). 각 커밋 끝에 `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>` 줄을 넣는다.
- 테스트 실행 전 **Docker를 실행**해 둔다. 전체: `dotnet test PortfolioBlog.slnx`. 일부: `dotnet test PortfolioBlog.Api.Tests --filter "FullyQualifiedName~<클래스명>"`.
- 절대 경로 하드코딩 금지. 저장소 루트 상대 경로만 사용. 실제 비밀번호·해시·도메인·IP를 커밋하지 않는다(테스트는 RFC 5737 문서용 대역 `203.0.113.0/24`, `198.51.100.0/24`와 `*.test` 호스트를 쓴다).

---

## 파일 구조 (이 계획이 만드는/바꾸는 파일)

```
PortfolioBlog.Api/
  Program.cs                                   # 수정: 서비스 등록 + 시작 검증 + Migrate + 미들웨어 순서 + MapApiEndpoints
  PortfolioBlog.Api.csproj                     # 수정: EF Core·Npgsql 패키지
  PortfolioBlog.Api.http                       # 수정: 로그인·글 생성 예시
  appsettings.json / appsettings.Development.json
  Domain/Post.cs, Series.cs, Tag.cs, PostTag.cs, AdminState.cs
  Contracts/ValidationErrors.cs, PostDtos.cs, SeriesDtos.cs, TagDtos.cs, AuthDtos.cs
  Infrastructure/Data/AppDbContext.cs, DbClock.cs, DbConflict.cs, LikePattern.cs, SlugRules.cs, TagResolver.cs, PostQueries.cs, Migrations/*
  Infrastructure/Access/CidrList.cs                     # 공백 구분 CIDR 파서 + 포함 판정
  Infrastructure/Access/SiteOptions.cs, AdminOptions.cs, ProxyOptions.cs
  Infrastructure/Access/IAdminAccessPolicy.cs, IpAllowlistAdminAccessPolicy.cs
  Infrastructure/Access/AdminSurfaceMiddleware.cs       # 호스트·IP·CSRF 헤더·Origin·no-store
  Infrastructure/Access/StartupValidation.cs            # 설정 fail-fast
  Infrastructure/Access/AdminCredential.cs              # 해시 검증 + 지문
  Infrastructure/Access/SessionRules.cs                 # 순수 판정 함수
  Infrastructure/Access/SessionValidator.cs             # ValidatePrincipal 핸들러
  Infrastructure/Access/HashPasswordCommand.cs          # `-- hash-password` CLI
  Infrastructure/Access/AccessServiceCollectionExtensions.cs  # AddAdminAccess / UseTrustedForwardedHeaders
  Features/ApiEndpoints.cs
  Features/Auth/AuthEndpoints.cs
  Features/Posts/PostEndpoints.cs, PostValidation.cs
  Features/Series/SeriesEndpoints.cs, SeriesValidation.cs
  Features/Tags/TagEndpoints.cs
PortfolioBlog.Api.Tests/
  PortfolioBlog.Api.Tests.csproj               # 수정: Testcontainers.PostgreSql
  HealthEndpointTests.cs                       # 수정: ApiFactory 사용
  Infrastructure/PostgresContainerFixture.cs, ApiFactory.cs, RemoteIpStartupFilter.cs, MutableTimeProvider.cs, TestJson.cs
  Infrastructure/DatabaseSchemaTests.cs, CidrListTests.cs, SessionRulesTests.cs, AdminCredentialTests.cs, LikePatternTests.cs, TagResolverTests.cs
  Features/AdminSurfaceTests.cs, ForwardedHeadersTests.cs, StartupValidationTests.cs
  Features/AuthEndpointsTests.cs, PostEndpointsTests.cs, SeriesEndpointsTests.cs, TagEndpointsTests.cs, AccessMatrixTests.cs
.github/workflows/ci.yml                       # 수정(Task 7): ubuntu-latest (Testcontainers는 Linux 컨테이너가 필요)
```

---

### Task 1: 기본 설정 · 도메인 · DbContext · 초기 마이그레이션 · 테스트 DB 픽스처

**Files:**
- Modify: `PortfolioBlog.Api/Program.cs`, `PortfolioBlog.Api/PortfolioBlog.Api.csproj`, `PortfolioBlog.Api/appsettings.json`, `PortfolioBlog.Api/appsettings.Development.json`
- Create: `PortfolioBlog.Api/Domain/{Post,Series,Tag,PostTag,AdminState}.cs`, `PortfolioBlog.Api/Infrastructure/Data/{AppDbContext,DbClock}.cs`, `PortfolioBlog.Api/Infrastructure/Data/Migrations/*`(생성)
- Modify: `PortfolioBlog.Api.Tests/PortfolioBlog.Api.Tests.csproj`, `PortfolioBlog.Api.Tests/HealthEndpointTests.cs`
- Create: `PortfolioBlog.Api.Tests/Infrastructure/{PostgresContainerFixture,ApiFactory,TestJson}.cs`
- Test: `PortfolioBlog.Api.Tests/Infrastructure/DatabaseSchemaTests.cs`

**Interfaces:**
- Produces: `PortfolioBlog.Api.Domain.{Post, Series, Tag, PostTag, AdminState}`; `AppDbContext`(DbSet `Posts, Series, Tags, PostTags, AdminStates`); `DbClock.UtcNow() : DateTimeOffset`; 테스트 `PostgresContainerFixture`, 컬렉션 이름 `"postgres"`, `ApiFactory`(public 생성자 `(PostgresContainerFixture)`, internal 생성자 `(PostgresContainerFixture, IReadOnlyDictionary<string,string?>)`, `CreateScope() : AsyncServiceScope`), `TestJson.Options`.
- `Post.Version`은 `uint`이며 PostgreSQL 시스템 컬럼 `xmin`에 매핑된다(마이그레이션에 컬럼이 생기지 않는 것이 정상).

- [ ] **Step 1: 패키지·도구 설치**

```bash
dotnet add PortfolioBlog.Api package Npgsql.EntityFrameworkCore.PostgreSQL --version 10.0.3
dotnet add PortfolioBlog.Api package Microsoft.EntityFrameworkCore.Design --version 10.0.12
dotnet add PortfolioBlog.Api.Tests package Testcontainers.PostgreSql --version 4.15.0
dotnet tool install -g dotnet-ef --version 10.0.12
```
(`dotnet-ef`가 이미 있으면 `dotnet tool update -g dotnet-ef --version 10.0.12`.)

- [ ] **Step 2: 도메인 파일 작성** — `PortfolioBlog.Api/Domain/`. 각 클래스에 Global Constraints의 XML 주석 템플릿을 적용한다(엔티티 공통: Thread Safety = Not Thread-safe, DbContext 스코프 안 단일 스레드 / Memory = 인스턴스당 힙 1개 + 컬렉션 / Blocking = 즉시 반환).

```csharp
// Domain/Post.cs
namespace PortfolioBlog.Api.Domain;

/// <summary>블로그 글. 발행 상태가 없으며 저장 즉시 공개된다. <see cref="CreatedAt"/>이 발행일이자 정렬 기준이다.</summary>
public sealed class Post
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    /// <summary>공개 URL 식별자. <c>^[a-z0-9]+(-[a-z0-9]+)*$</c>, 최대 100자, 생성 후 불변.</summary>
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    /// <summary>목록 발췌·meta description·OG·Atom 공용 요약(최대 300자).</summary>
    public string Summary { get; set; } = string.Empty;
    /// <summary>마크다운 원문(UTF-8 200KB 이하). HTML은 저장하지 않고 요청 시 렌더링한다.</summary>
    public string ContentMarkdown { get; set; } = string.Empty;
    public Guid? SeriesId { get; set; }
    public Series? Series { get; set; }
    /// <summary>시리즈 안 순서(양수, 중복 허용). <see cref="SeriesId"/>와 함께 있거나 함께 없어야 한다(DB CHECK).</summary>
    public int? SeriesOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>낙관적 동시성 토큰. PostgreSQL 시스템 컬럼 xmin(행을 마지막으로 쓴 트랜잭션 ID)에 매핑된다.</summary>
    public uint Version { get; set; }
    public List<PostTag> PostTags { get; } = new();
}

// Domain/Series.cs
namespace PortfolioBlog.Api.Domain;

/// <summary>연재 묶음. 글은 0~1개 시리즈에 속한다.</summary>
public sealed class Series
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<Post> Posts { get; } = new();
}

// Domain/Tag.cs
namespace PortfolioBlog.Api.Domain;

public sealed class Tag
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    /// <summary>표시용 이름(원문 대소문자 유지, 1~50자, '/' 금지).</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>트림 + 연속 공백 1개 + NFC + 소문자. 유일 인덱스 대상이자 공개 URL 키.</summary>
    public string NormalizedName { get; set; } = string.Empty;
    public List<PostTag> PostTags { get; } = new();
}

// Domain/PostTag.cs
namespace PortfolioBlog.Api.Domain;

public sealed class PostTag
{
    public Guid PostId { get; set; }
    public Post Post { get; set; } = null!;
    public Guid TagId { get; set; }
    public Tag Tag { get; set; } = null!;
}

// Domain/AdminState.cs
namespace PortfolioBlog.Api.Domain;

/// <summary>단일 행(Id=1) 관리 상태. <see cref="SessionEpoch"/>를 올리면 그 전에 발급된 모든 세션 쿠키가 무효가 된다.</summary>
public sealed class AdminState
{
    public const int SingletonId = 1;
    public int Id { get; set; } = SingletonId;
    public int SessionEpoch { get; set; } = 1;
}
```

- [ ] **Step 3: DbClock · AppDbContext 작성** — `PortfolioBlog.Api/Infrastructure/Data/`

```csharp
// DbClock.cs
namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>DB에 저장되는 모든 시각의 단일 출처. UTC이며 마이크로초로 절삭한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 무상태.</description></item>
/// <item><description><b>Memory Allocation:</b> Zero-allocation(struct 반환).</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
/// </list>
/// Npgsql은 timestamptz를 마이크로초 단위로 기록하므로 100ns 틱을 미리 절삭해야 "저장 전 값 == 재조회 값"이 성립한다.
/// </remarks>
public static class DbClock
{
    private const long TicksPerMicrosecond = 10;

    public static DateTimeOffset UtcNow()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Ticks - now.Ticks % TicksPerMicrosecond, TimeSpan.Zero);
    }
}
```

```csharp
// AppDbContext.cs
using Microsoft.EntityFrameworkCore;
using PortfolioBlog.Api.Domain;

namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>기술 블로그의 EF Core DbContext.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. 요청 스코프당 1개 인스턴스이며 동시 사용 금지.</description></item>
/// <item><description><b>Memory Allocation:</b> 변경 추적기가 로드한 엔티티 그래프를 스코프 종료까지 보유한다. 읽기 전용 조회는 <c>AsNoTracking()</c> + 프로젝션을 쓴다(목록에서 본문 200KB를 끌어오지 않기 위해).</description></item>
/// <item><description><b>Blocking:</b> 모든 I/O는 async API로 Non-blocking. 동기 <c>SaveChanges()</c> 사용 금지(시작 시 <c>Migrate()</c>만 예외).</description></item>
/// </list>
/// 앱 검증과 별개로 핵심 불변식은 DB 제약(CHECK·UNIQUE)으로 한 번 더 막는다.
/// </remarks>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public const string SlugPattern = "^[a-z0-9]+(-[a-z0-9]+)*$";
    public const int SlugMax = 100;
    public const int TitleMax = 200;
    public const int SummaryMax = 300;
    public const int ContentMaxBytes = 204_800;
    public const int SeriesDescriptionMax = 1000;
    public const int TagMax = 50;

    public DbSet<Post> Posts => Set<Post>();
    public DbSet<Series> Series => Set<Series>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<PostTag> PostTags => Set<PostTag>();
    public DbSet<AdminState> AdminStates => Set<AdminState>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Post>(e =>
        {
            e.Property(x => x.Slug).HasMaxLength(SlugMax);
            e.Property(x => x.Title).HasMaxLength(TitleMax);
            e.Property(x => x.Summary).HasMaxLength(SummaryMax);
            e.Property(x => x.Version).IsRowVersion(); // Npgsql: uint + IsRowVersion → xmin 시스템 컬럼
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => new { x.CreatedAt, x.Id }).IsDescending(true, false);
            e.HasIndex(x => new { x.SeriesId, x.SeriesOrder, x.CreatedAt, x.Id });
            // Restrict: 시리즈 삭제는 앱이 한 트랜잭션에서 SeriesId·SeriesOrder를 함께 비운 뒤 수행한다(SET NULL은 CK_Posts_Series_Pair 위반).
            e.HasOne(x => x.Series).WithMany(s => s.Posts).HasForeignKey(x => x.SeriesId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Posts_Slug_Format", $"\"Slug\" ~ '{SlugPattern}'");
                t.HasCheckConstraint("CK_Posts_Title_NotBlank", "length(btrim(\"Title\")) > 0");
                t.HasCheckConstraint("CK_Posts_Content_Size", $"octet_length(\"ContentMarkdown\") <= {ContentMaxBytes}");
                t.HasCheckConstraint("CK_Posts_Series_Pair", "(\"SeriesId\" IS NULL) = (\"SeriesOrder\" IS NULL)");
                t.HasCheckConstraint("CK_Posts_SeriesOrder_Positive", "\"SeriesOrder\" IS NULL OR \"SeriesOrder\" > 0");
            });
        });
        b.Entity<Series>(e =>
        {
            e.Property(x => x.Slug).HasMaxLength(SlugMax);
            e.Property(x => x.Title).HasMaxLength(TitleMax);
            e.Property(x => x.Description).HasMaxLength(SeriesDescriptionMax);
            e.HasIndex(x => x.Slug).IsUnique();
            e.ToTable("Series", t =>
            {
                t.HasCheckConstraint("CK_Series_Slug_Format", $"\"Slug\" ~ '{SlugPattern}'");
                t.HasCheckConstraint("CK_Series_Title_NotBlank", "length(btrim(\"Title\")) > 0");
            });
        });
        b.Entity<Tag>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(TagMax);
            e.Property(x => x.NormalizedName).HasMaxLength(TagMax);
            e.HasIndex(x => x.NormalizedName).IsUnique();
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Tags_Name_NotBlank", "length(btrim(\"Name\")) > 0");
                t.HasCheckConstraint("CK_Tags_Name_NoSlash", "position('/' in \"Name\") = 0");
            });
        });
        b.Entity<PostTag>(e =>
        {
            e.HasKey(x => new { x.PostId, x.TagId });
            e.HasIndex(x => new { x.TagId, x.PostId });
            e.HasOne(x => x.Post).WithMany(p => p.PostTags).HasForeignKey(x => x.PostId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Tag).WithMany(t => t.PostTags).HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<AdminState>(e =>
        {
            e.ToTable("AdminState", t => t.HasCheckConstraint("CK_AdminState_Single", $"\"Id\" = {AdminState.SingletonId}"));
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasData(new AdminState { Id = AdminState.SingletonId, SessionEpoch = 1 });
        });
    }
}
```

- [ ] **Step 4: Program.cs를 다음으로 교체** (`UseHttpsRedirection` 제거 — TLS는 Caddy가 종료한다. `HealthResponse`·`Program` 선언과 그 XML 주석은 기존 그대로 파일 끝에 둔다.)

```csharp
using Microsoft.EntityFrameworkCore;
using PortfolioBlog.Api.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
// ProblemDetails: 400/401/403/404/409/429 등 모든 오류 응답을 RFC 9457 형식으로 통일한다.
builder.Services.AddProblemDetails();
// 바인딩 실패(JSON 파싱 오류·잘못된 쿼리 값)를 예외(Development 기본값)가 아니라 항상 400으로 응답한다.
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = false);
// 연결 문자열은 람다 안에서(=Build 이후 첫 해석 시점에) 읽는다. Global Constraints의 "설정은 Build 이후에만" 규칙.
builder.Services.AddDbContext<AppDbContext>((sp, o) =>
    o.UseNpgsql(sp.GetRequiredService<IConfiguration>().GetConnectionString("Default")
        ?? throw new InvalidOperationException("ConnectionStrings:Default 설정이 없습니다.")));

var app = builder.Build();

// 단일 인스턴스 배포이므로 시작 시 마이그레이션을 적용한다(스펙 3.10).
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", static () =>
    // DateTimeOffset.UtcNow: 로컬 타임존 변환(tzdata/레지스트리 조회)을 거치지 않고 시스템 UTC 틱을
    // 그대로 읽으므로 오프셋 0 이 보장되고 Now 보다 호출 비용이 낮다.
    new HealthResponse("Healthy", DateTimeOffset.UtcNow))
    .WithName("GetHealth");

app.Run();

// (이 아래 HealthResponse record 와 public partial class Program 선언은 기존 파일 내용을 그대로 유지)
```

- [ ] **Step 5: 설정 파일** — `appsettings.json`을 다음으로 교체:

```json
{
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } },
  "AllowedHosts": "*",
  "ConnectionStrings": { "Default": "" }
}
```

`appsettings.Development.json`을 다음으로 교체(로컬 개발 컨테이너 전용 플레이스홀더 비밀번호 `changeme`. 실제 배포 비밀값은 Plan 4의 `deploy/.env`(gitignore)에만 둔다):

```json
{
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } },
  "ConnectionStrings": { "Default": "Host=localhost;Port=5432;Database=blog_dev;Username=postgres;Password=changeme" }
}
```

- [ ] **Step 6: 마이그레이션 생성**

```bash
dotnet ef migrations add InitialCreate --project PortfolioBlog.Api --output-dir Infrastructure/Data/Migrations
```
Expected: `PortfolioBlog.Api/Infrastructure/Data/Migrations/`에 `*_InitialCreate.cs`, `AppDbContextModelSnapshot.cs` 생성. 생성된 `Up()`에 `Posts`·`Series`·`Tags`·`PostTags`·`AdminState` 테이블, CHECK 제약 10개, `AdminState` 시드 `(1, 1)`이 있고 **`xmin` 컬럼 추가는 없어야** 한다. 디자인 타임에는 DB에 연결하지 않으므로 Postgres가 떠 있지 않아도 된다.

- [ ] **Step 7: 테스트 인프라 작성** — `PortfolioBlog.Api.Tests/Infrastructure/`

```csharp
// PostgresContainerFixture.cs
using Testcontainers.PostgreSql;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>테스트 컬렉션 전체가 공유하는 PostgreSQL 컨테이너.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 시작 후 읽기 전용(연결 문자열)만 노출한다.</description></item>
/// <item><description><b>Memory Allocation:</b> 컨테이너 핸들 1개. 컬렉션 종료 시 Dispose로 컨테이너 제거.</description></item>
/// <item><description><b>Blocking:</b> InitializeAsync는 이미지 pull·기동을 비동기 대기(최초 수십 초).</description></item>
/// </list>
/// </remarks>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    // PostgreSqlContainer: Docker API로 컨테이너를 기동하고 컨테이너 안에서 pg_isready를 반복 실행하는 대기 전략으로
    // 준비 완료를 판정하므로 sleep 기반 폴링 없이 연결 가능한 시점을 정확히 얻는다.
    // 4.15.0에서 매개변수 없는 PostgreSqlBuilder()는 obsolete이므로 이미지를 생성자 인수로 준다(경고 0 유지).
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresContainerFixture>;
```

```csharp
// ApiFactory.cs
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>테스트 클래스마다 고유한 데이터베이스를 갖는 인메모리 호스트 팩토리.</summary>
/// <remarks>기본 생성자(xUnit 주입)는 설정 오버라이드 없이 만든다. 다른 설정이 필요한 테스트는
/// <c>new ApiFactory(pg, settings)</c>로 직접 만들고 <c>using</c>으로 해제한다.</remarks>
public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly IReadOnlyDictionary<string, string?> _settings;

    public ApiFactory(PostgresContainerFixture pg) : this(pg, new Dictionary<string, string?>()) { }

    // xUnit 2.x는 클래스 픽스처에 public 인스턴스 생성자가 정확히 하나여야 한다. 설정 오버라이드용은 internal로 둔다.
    internal ApiFactory(PostgresContainerFixture pg, IReadOnlyDictionary<string, string?> settings)
    {
        // 클래스마다 새 DB 이름을 써서 테스트 간 데이터 간섭을 없앤다. Migrate()가 DB를 생성한다.
        var csb = new NpgsqlConnectionStringBuilder(pg.ConnectionString)
        {
            Database = "blog_test_" + Guid.NewGuid().ToString("N"),
        };
        _connectionString = csb.ToString();
        _settings = settings;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", _connectionString);
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }
    }

    /// <summary>호출자가 소유하는 DI 스코프. <c>await using var scope = factory.CreateScope();</c></summary>
    public AsyncServiceScope CreateScope() => Services.CreateAsyncScope();
}
```

```csharp
// TestJson.cs
using System.Text.Json;

namespace PortfolioBlog.Api.Tests.Infrastructure;

public static class TestJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
```

- [ ] **Step 8: 실패하는 테스트 작성** — `PortfolioBlog.Api.Tests/Infrastructure/DatabaseSchemaTests.cs`

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PortfolioBlog.Api.Domain;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

[Collection("postgres")]
public sealed class DatabaseSchemaTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string CheckViolation = "23514";
    private const string UniqueViolation = "23505";

    private static Post NewPost(string slug) => new()
    {
        Slug = slug, Title = "제목", Summary = "", ContentMarkdown = "본문",
        CreatedAt = DbClock.UtcNow(), UpdatedAt = DbClock.UtcNow(),
    };

    private async Task<string?> SaveAndGetSqlStateAsync(Action<AppDbContext> arrange)
    {
        using var client = factory.CreateClient(); // 호스트 기동 → Migrate()
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        arrange(db);
        try { await db.SaveChangesAsync(); return null; }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg) { return pg.SqlState; }
    }

    [Fact]
    public async Task Startup_AppliesMigration_SeedsAdminState_AndRoundTripsPost()
    {
        using var client = factory.CreateClient();
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("InitialCreate", StringComparison.Ordinal));
        Assert.Equal(1, (await db.AdminStates.AsNoTracking().SingleAsync()).SessionEpoch);

        var post = NewPost("round-trip");
        db.Posts.Add(post);
        await db.SaveChangesAsync();

        var loaded = await db.Posts.AsNoTracking().SingleAsync(p => p.Id == post.Id);
        Assert.Equal(post.CreatedAt, loaded.CreatedAt); // DbClock 마이크로초 절삭 덕분에 정확히 같다
        Assert.NotEqual(0u, loaded.Version);            // xmin
    }

    [Theory]
    [InlineData("Bad-Slug")]
    [InlineData("bad slug")]
    [InlineData("-bad")]
    [InlineData("bad--slug")]
    public async Task Post_InvalidSlug_IsRejectedByCheckConstraint(string slug) =>
        Assert.Equal(CheckViolation, await SaveAndGetSqlStateAsync(db => db.Posts.Add(NewPost(slug))));

    [Fact]
    public async Task Post_SeriesOrderWithoutSeries_IsRejected() =>
        Assert.Equal(CheckViolation, await SaveAndGetSqlStateAsync(db =>
        {
            var p = NewPost("order-without-series");
            p.SeriesOrder = 1;
            db.Posts.Add(p);
        }));

    [Fact]
    public async Task Post_DuplicateSlug_IsRejectedByUniqueIndex()
    {
        Assert.Null(await SaveAndGetSqlStateAsync(db => db.Posts.Add(NewPost("dup-slug"))));
        Assert.Equal(UniqueViolation, await SaveAndGetSqlStateAsync(db => db.Posts.Add(NewPost("dup-slug"))));
    }

    [Fact]
    public async Task Tag_SlashInName_IsRejected() =>
        Assert.Equal(CheckViolation, await SaveAndGetSqlStateAsync(db =>
            db.Tags.Add(new Tag { Name = "a/b", NormalizedName = "a/b" })));

    [Fact]
    public async Task AdminState_SecondRow_IsRejected() =>
        Assert.Equal(CheckViolation, await SaveAndGetSqlStateAsync(db =>
            db.AdminStates.Add(new AdminState { Id = 2, SessionEpoch = 1 })));
}
```

- [ ] **Step 9: 테스트 실행**

Run: `dotnet test PortfolioBlog.Api.Tests --filter "FullyQualifiedName~DatabaseSchemaTests"`
Expected: 9개 PASS(Theory 4 + Fact 5). 컨테이너 기동 실패 시 Docker 실행 여부 확인. 이 시점에 `HealthEndpointTests`는 `WebApplicationFactory<Program>`을 직접 써서 연결 문자열이 없어 **기동 실패**한다 → Step 10.

- [ ] **Step 10: HealthEndpointTests를 ApiFactory로 전환** — 클래스 선언 위에 `[Collection("postgres")]` 추가, `IClassFixture<WebApplicationFactory<Program>>` → `IClassFixture<ApiFactory>`, 필드·생성자 매개변수 타입을 `ApiFactory`로 변경, `using PortfolioBlog.Api.Tests.Infrastructure;` 추가. 본문과 주석은 유지하되 "인메모리 TestServer" 설명은 그대로 맞다.

Run: `dotnet test PortfolioBlog.slnx`
Expected: 전부 PASS(Health 2 + Schema 9), 경고 0.

- [ ] **Step 11: 커밋**

```bash
git add -A
git commit -m "추가: 블로그 도메인·DB 제약·초기 마이그레이션과 Postgres 테스트 픽스처

- Post/Series/Tag/PostTag/AdminState, 핵심 불변식은 CHECK·UNIQUE 제약으로 DB에서도 강제
- Post.Version을 xmin에 매핑(낙관적 동시성), DbClock으로 마이크로초 절삭
- Testcontainers PostgreSQL 컬렉션 픽스처 + 클래스별 고유 DB ApiFactory

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---
### Task 2: CIDR 목록 파서 · 옵션 · IP 허용 정책

**Files:**
- Create: `PortfolioBlog.Api/Infrastructure/Access/{CidrList,SiteOptions,AdminOptions,ProxyOptions,IAdminAccessPolicy,IpAllowlistAdminAccessPolicy}.cs`
- Test: `PortfolioBlog.Api.Tests/Infrastructure/CidrListTests.cs`

**Interfaces:**
- Produces:
  - `CidrList.Parse(string? spaceSeparated) : CidrList` — 빈 값이면 빈 목록, 항목이 하나라도 잘못되면 `FormatException`. `bool Contains(IPAddress? ip)`, `int Count`.
  - `SiteOptions { string PublicOrigin; string AdminOrigin; }` 섹션 `"Site"`, `static string HostOf(string origin)`.
  - `AdminOptions { string AllowedCidrs; string PasswordHash; int LoginPerIpPerMinute = 5; int LoginGlobalPerMinute = 20; int LoginConcurrency = 2; int SessionHours = 12; }` 섹션 `"Admin"`.
  - `ProxyOptions { string TrustedIp; }` 섹션 `"Proxy"`.
  - `IAdminAccessPolicy.IsAllowed(HttpContext) : bool`, 구현 `IpAllowlistAdminAccessPolicy(IOptions<AdminOptions>)`.

- [ ] **Step 1: 실패하는 테스트 작성** — `PortfolioBlog.Api.Tests/Infrastructure/CidrListTests.cs`

```csharp
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Infrastructure.Access;

namespace PortfolioBlog.Api.Tests.Infrastructure;

public sealed class CidrListTests
{
    [Theory]
    [InlineData("192.168.0.0/16", "192.168.10.5", true)]
    [InlineData("192.168.0.0/16", "10.0.0.1", false)]
    [InlineData("203.0.113.7/32", "203.0.113.7", true)]
    [InlineData("203.0.113.7/32", "203.0.113.8", false)]
    [InlineData("203.0.113.0/24", "203.0.113.255", true)]   // 경계 주소
    [InlineData("203.0.113.0/24", "203.0.114.0", false)]
    [InlineData("::1/128", "::1", true)]
    [InlineData("2001:db8::/32", "2001:db8:1::5", true)]
    [InlineData("2001:db8::/32", "2001:db9::1", false)]
    public void Contains_MatchesCidr(string cidrs, string ip, bool expected) =>
        Assert.Equal(expected, CidrList.Parse(cidrs).Contains(IPAddress.Parse(ip)));

    [Fact]
    public void Parse_SplitsOnAnyWhitespace_SameSyntaxAsCaddyRemoteIp()
    {
        var list = CidrList.Parse("  203.0.113.0/24 \t 2001:db8::/32\n::1/128 ");
        Assert.Equal(3, list.Count);
        Assert.True(list.Contains(IPAddress.Parse("::1")));
    }

    [Fact]
    public void Contains_Ipv4MappedIpv6_MatchesIpv4Cidr() =>
        // Kestrel 듀얼스택 소켓은 IPv4 클라이언트를 ::ffff:a.b.c.d 로 보고한다.
        Assert.True(CidrList.Parse("192.168.0.0/16").Contains(IPAddress.Parse("::ffff:192.168.1.20")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_Empty_DeniesEveryone(string? raw)
    {
        var list = CidrList.Parse(raw);
        Assert.Equal(0, list.Count);
        Assert.False(list.Contains(IPAddress.Loopback));
    }

    [Fact]
    public void Contains_Null_IsFalse() => Assert.False(CidrList.Parse("0.0.0.0/0").Contains(null));

    [Theory]
    [InlineData("not-a-cidr")]
    [InlineData("203.0.113.0/24,198.51.100.0/24")] // 쉼표 구분은 허용하지 않는다(문법은 공백 하나로 통일)
    [InlineData("203.0.113.0/33")]
    [InlineData("203.0.113.0/24 oops")]
    public void Parse_InvalidEntry_Throws(string raw) =>
        Assert.Throws<FormatException>(() => CidrList.Parse(raw));

    [Fact]
    public void Policy_UsesConnectionRemoteIp()
    {
        var policy = new IpAllowlistAdminAccessPolicy(Options.Create(new AdminOptions { AllowedCidrs = "203.0.113.0/24" }));
        var allowed = new DefaultHttpContext();
        allowed.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
        var denied = new DefaultHttpContext();
        denied.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.7");

        Assert.True(policy.IsAllowed(allowed));
        Assert.False(policy.IsAllowed(denied));
        Assert.False(policy.IsAllowed(new DefaultHttpContext())); // RemoteIpAddress == null
    }
}
```

- [ ] **Step 2: 실패 확인**

Run: `dotnet test PortfolioBlog.Api.Tests --filter "FullyQualifiedName~CidrListTests"`
Expected: 컴파일 오류(타입 없음).

- [ ] **Step 3: 구현** — `PortfolioBlog.Api/Infrastructure/Access/`

```csharp
// CidrList.cs
using System.Net;

namespace PortfolioBlog.Api.Infrastructure.Access;

/// <summary>공백으로 구분한 CIDR 목록. Caddy <c>remote_ip</c> 매처와 같은 문법이라 환경변수 하나를 두 계층이 공유할 수 있다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 생성 후 불변 배열만 읽는다.</description></item>
/// <item><description><b>Memory Allocation:</b> <see cref="Contains"/>는 Zero-allocation. 단 IPv4-mapped IPv6 입력은 <c>MapToIPv4()</c>로 IPAddress 1개를 할당한다.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
/// </list>
/// 잘못된 항목을 조용히 무시하지 않는다: 하나라도 파싱에 실패하면 <see cref="FormatException"/>으로 시작을 막는다(설정 오타가 "아무도 못 들어옴" 또는 "의도보다 넓게 열림"으로 숨는 것을 방지).
/// </remarks>
public sealed class CidrList
{
    // System.Net.IPNetwork: readonly struct라 배열에 인라인 저장되어 캐시 지역성이 좋고, Contains()는 프리픽스 비트 비교만 수행한다.
    private readonly IPNetwork[] _networks;

    private CidrList(IPNetwork[] networks) => _networks = networks;

    public int Count => _networks.Length;

    public static CidrList Parse(string? spaceSeparated)
    {
        if (string.IsNullOrWhiteSpace(spaceSeparated))
        {
            return new CidrList([]);
        }
        // separator null = 모든 공백 문자(스페이스·탭·개행)로 분리.
        var parts = spaceSeparated.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var networks = new IPNetwork[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!IPNetwork.TryParse(parts[i], out networks[i]))
            {
                throw new FormatException($"CIDR 형식이 아닙니다: '{parts[i]}'. 공백으로 구분한 CIDR만 허용합니다(예: \"203.0.113.0/24 2001:db8::/32\").");
            }
        }
        return new CidrList(networks);
    }

    public bool Contains(IPAddress? ip)
    {
        if (ip is null)
        {
            return false;
        }
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }
        foreach (var network in _networks)
        {
            if (network.Contains(ip))
            {
                return true;
            }
        }
        return false;
    }
}
```

```csharp
// SiteOptions.cs
namespace PortfolioBlog.Api.Infrastructure.Access;

/// <summary>설정 섹션 <c>Site</c>. 절대 URL 생성과 관리 호스트 판정의 고정 기준(요청 Host 헤더를 신뢰하지 않기 위함).</summary>
public sealed class SiteOptions
{
    public const string SectionName = "Site";
    /// <summary>예: <c>https://blog.example.com</c>. Plan 2의 canonical·Atom·sitemap이 쓴다.</summary>
    public string PublicOrigin { get; set; } = string.Empty;
    /// <summary>예: <c>https://admin.example.com</c>. <c>/api</c>의 Host·Origin 검사 기준.</summary>
    public string AdminOrigin { get; set; } = string.Empty;

    /// <summary>origin에서 호스트 이름만 뽑는다(포트·스킴 제외). 형식이 틀리면 <see cref="FormatException"/>.</summary>
    public static string HostOf(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.AbsolutePath == "/" && origin == uri.GetLeftPart(UriPartial.Authority)
            ? uri.Host
            : throw new FormatException($"origin 형식이 아닙니다: '{origin}'. 'https://host[:port]' 형태여야 하며 경로·끝 슬래시를 붙이지 않습니다.");
}
```

```csharp
// AdminOptions.cs
namespace PortfolioBlog.Api.Infrastructure.Access;

/// <summary>설정 섹션 <c>Admin</c>.</summary>
public sealed class AdminOptions
{
    public const string SectionName = "Admin";
    /// <summary>공백 구분 CIDR. 비어 있으면 아무도 관리 표면에 접근할 수 없다(안전 기본값).</summary>
    public string AllowedCidrs { get; set; } = string.Empty;
    /// <summary><c>hash-password</c> 명령이 출력한 PBKDF2 해시. 비어 있으면 로그인은 항상 실패한다.</summary>
    public string PasswordHash { get; set; } = string.Empty;
    public int LoginPerIpPerMinute { get; set; } = 5;
    public int LoginGlobalPerMinute { get; set; } = 20;
    /// <summary>동시에 실행할 수 있는 해시 검증 수(PBKDF2는 CPU 바운드).</summary>
    public int LoginConcurrency { get; set; } = 2;
    /// <summary>세션 절대 수명(시간). sliding 연장 없음.</summary>
    public int SessionHours { get; set; } = 12;
}
```

```csharp
// ProxyOptions.cs
namespace PortfolioBlog.Api.Infrastructure.Access;

/// <summary>설정 섹션 <c>Proxy</c>. 관리자 허용 CIDR과는 목적이 다른 별도 설정이다.</summary>
public sealed class ProxyOptions
{
    public const string SectionName = "Proxy";
    /// <summary>X-Forwarded-For를 믿을 단 하나의 프록시(Caddy 컨테이너 고정 IP). 비어 있으면 헤더를 전혀 믿지 않는다.</summary>
    public string TrustedIp { get; set; } = string.Empty;
}
```

```csharp
// IAdminAccessPolicy.cs
namespace PortfolioBlog.Api.Infrastructure.Access;

/// <summary>현재 요청의 원본 IP가 관리 표면에 접근할 수 있는지 판정한다.</summary>
/// <param name="context">ForwardedHeaders 미들웨어가 원본 IP를 보정한 뒤의 요청 컨텍스트</param>
/// <returns>허용 CIDR 안이면 <c>true</c></returns>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> 요청 파이프라인 스레드에서 미들웨어가 호출한다. 요청 본문을 읽기 전에 실행된다.</description></item>
/// <item><description><b>Memory Policy:</b> 구현은 판정 경로에서 힙 할당을 하지 않아야 한다(IPv4-mapped 정규화 1건 제외).</description></item>
/// <item><description><b>Concurrency:</b> 구현은 Thread-safe(불변)여야 한다. 싱글턴으로 등록된다. 즉시 반환(Non-blocking), I/O 금지.</description></item>
/// </list>
/// </remarks>
public interface IAdminAccessPolicy
{
    bool IsAllowed(HttpContext context);
}
```

```csharp
// IpAllowlistAdminAccessPolicy.cs
using Microsoft.Extensions.Options;

namespace PortfolioBlog.Api.Infrastructure.Access;

/// <summary><c>Admin:AllowedCidrs</c>에 원본 IP가 속할 때만 허용한다. 생성자에서 CIDR을 파싱하므로 설정 오류는 첫 해석 시점에 예외가 된다.</summary>
public sealed class IpAllowlistAdminAccessPolicy(IOptions<AdminOptions> options) : IAdminAccessPolicy
{
    private readonly CidrList _allowed = CidrList.Parse(options.Value.AllowedCidrs);

    public bool IsAllowed(HttpContext context) => _allowed.Contains(context.Connection.RemoteIpAddress);
}
```

- [ ] **Step 4: 통과 확인**

Run: `dotnet test PortfolioBlog.Api.Tests --filter "FullyQualifiedName~CidrListTests"`
Expected: 20개 PASS.

- [ ] **Step 5: 커밋**

```bash
git add -A
git commit -m "추가: 공백 구분 CIDR 파서와 관리 표면 IP 허용 정책

- Caddy remote_ip와 같은 문법으로 통일해 환경변수 하나를 두 계층이 공유
- 잘못된 항목은 무시하지 않고 FormatException(설정 오타가 조용히 숨는 것 방지)
- IPv4-mapped IPv6 정규화

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: 관리 표면 미들웨어 · 신뢰 프록시 · 시작 검증 · `/api/auth/me`

**Files:**
- Create: `PortfolioBlog.Api/Infrastructure/Access/{AdminSurfaceMiddleware,StartupValidation,AccessServiceCollectionExtensions}.cs`
- Create: `PortfolioBlog.Api/Contracts/AuthDtos.cs`, `PortfolioBlog.Api/Features/ApiEndpoints.cs`, `PortfolioBlog.Api/Features/Auth/AuthEndpoints.cs`
- Modify: `PortfolioBlog.Api/Program.cs`, `appsettings.json`, `appsettings.Development.json`
- Create: `PortfolioBlog.Api.Tests/Infrastructure/RemoteIpStartupFilter.cs`; Modify: `ApiFactory.cs`
- Test: `PortfolioBlog.Api.Tests/Features/{AdminSurfaceTests,ForwardedHeadersTests,StartupValidationTests}.cs`

**Interfaces:**
- Consumes: `CidrList`, `SiteOptions`, `AdminOptions`, `ProxyOptions`, `IAdminAccessPolicy`(Task 2).
- Produces:
  - `AdminSurfaceMiddleware`(상수 `CsrfHeaderName = "X-Requested-With"`, `CsrfHeaderValue = "XMLHttpRequest"`).
  - `AccessServiceCollectionExtensions.AddAdminAccess(this IServiceCollection, IConfiguration)`, `UseTrustedForwardedHeaders(this WebApplication)`.
  - `StartupValidation.Validate(IServiceProvider, IHostEnvironment)` — 실패 시 `InvalidOperationException`(메시지에 설정 키 포함).
  - `ApiEndpoints.MapApiEndpoints(this WebApplication) : RouteGroupBuilder`(그룹 `/api`), `AuthEndpoints.MapAuthEndpoints(this RouteGroupBuilder)`, `AuthStatusDto(bool Authenticated)`.
  - 테스트: `ApiFactory.AdminOrigin = "https://admin.test"`, `PublicOrigin = "https://blog.test"`, `AllowedIp = "203.0.113.9"`, `OutsiderIp = "198.51.100.7"`, `CreateAdminClient(bool handleCookies = true)`, `CreatePublicClient()`, `RemoteIpStartupFilter.HeaderName = "X-Test-Remote-Ip"`.

- [ ] **Step 1: 테스트 인프라** — `RemoteIpStartupFilter.cs` 생성, `ApiFactory.cs` 수정

```csharp
// PortfolioBlog.Api.Tests/Infrastructure/RemoteIpStartupFilter.cs
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>요청 헤더 <c>X-Test-Remote-Ip</c> 값을 <c>Connection.RemoteIpAddress</c>에 넣는 테스트 전용 미들웨어를
/// 앱 파이프라인 **앞**에 등록한다(TestServer는 RemoteIpAddress가 null이다. IStartupFilter는 Program.cs의 미들웨어보다 먼저 실행된다).</summary>
public sealed class RemoteIpStartupFilter : IStartupFilter
{
    public const string HeaderName = "X-Test-Remote-Ip";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((ctx, nextMiddleware) =>
        {
            if (ctx.Request.Headers.TryGetValue(HeaderName, out var raw) && IPAddress.TryParse(raw.ToString(), out var ip))
            {
                ctx.Connection.RemoteIpAddress = ip;
            }
            return nextMiddleware(ctx);
        });
        next(app);
    };
}
```

`ApiFactory.cs`에 추가(using `Microsoft.Extensions.DependencyInjection`, `PortfolioBlog.Api.Infrastructure.Access`):

```csharp
    public const string AdminOrigin = "https://admin.test";
    public const string PublicOrigin = "https://blog.test";
    public const string AllowedIp = "203.0.113.9";     // Admin:AllowedCidrs 안
    public const string OutsiderIp = "198.51.100.7";   // 밖

    // ConfigureWebHost 안, _settings 루프 **앞**에 기본값을 넣는다(개별 테스트의 settings가 덮어쓸 수 있도록).
    builder.UseSetting("Site:PublicOrigin", PublicOrigin);
    builder.UseSetting("Site:AdminOrigin", AdminOrigin);
    builder.UseSetting("Admin:AllowedCidrs", "203.0.113.0/24");
    // _settings 루프 뒤:
    if (_settings.TryGetValue("Test:Environment", out var env) && env is not null)
    {
        builder.UseEnvironment(env);
    }
    builder.ConfigureServices(services => services.AddTransient<IStartupFilter, RemoteIpStartupFilter>());

    /// <summary>관리 호스트로 가는 클라이언트. 허용 IP·CSRF 헤더·Origin을 기본으로 붙인다. 부재를 검증하는 테스트는 직접 제거한다.
    /// https 주소를 쓰는 이유: 세션 쿠키가 Secure라서 http 주소에서는 CookieContainer가 쿠키를 돌려보내지 않는다.</summary>
    public HttpClient CreateAdminClient(bool handleCookies = true)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(AdminOrigin),
            HandleCookies = handleCookies,
            AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.Add(AdminSurfaceMiddleware.CsrfHeaderName, AdminSurfaceMiddleware.CsrfHeaderValue);
        client.DefaultRequestHeaders.Add("Origin", AdminOrigin);
        client.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, AllowedIp);
        return client;
    }

    /// <summary>공개 호스트로 가는 클라이언트(임의의 외부 방문자).</summary>
    public HttpClient CreatePublicClient()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(PublicOrigin), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, OutsiderIp);
        return client;
    }
```

- [ ] **Step 2: 실패하는 테스트 작성** — `PortfolioBlog.Api.Tests/Features/AdminSurfaceTests.cs`

```csharp
using System.Net;
using System.Net.Http.Json;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Infrastructure.Access;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

[Collection("postgres")]
public sealed class AdminSurfaceTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Me = "/api/auth/me";

    [Fact]
    public async Task AllowedIp_WithHeader_OnAdminHost_Returns200_NoStore()
    {
        using var client = factory.CreateAdminClient();
        using var res = await client.GetAsync(Me);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.False((await res.Content.ReadFromJsonAsync<AuthStatusDto>(TestJson.Options))!.Authenticated);
        Assert.True(res.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task OutsiderIp_Returns403()
    {
        using var client = factory.CreateAdminClient();
        client.DefaultRequestHeaders.Remove(RemoteIpStartupFilter.HeaderName);
        client.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, ApiFactory.OutsiderIp);
        using var res = await client.GetAsync(Me);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task NoRemoteIp_Returns403()
    {
        using var client = factory.CreateAdminClient();
        client.DefaultRequestHeaders.Remove(RemoteIpStartupFilter.HeaderName);
        using var res = await client.GetAsync(Me);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task MissingCsrfHeader_Returns403_EvenOnGet()
    {
        using var client = factory.CreateAdminClient();
        client.DefaultRequestHeaders.Remove(AdminSurfaceMiddleware.CsrfHeaderName);
        using var res = await client.GetAsync(Me);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Theory]
    [InlineData("/api/auth/me")]
    [InlineData("/API/auth/me")]   // 대소문자 변형
    [InlineData("/api")]           // 그룹 루트 자체
    [InlineData("/api/")]
    public async Task PublicHost_ApiPaths_Return404_EvenFromAllowedIp(string path)
    {
        using var client = factory.CreatePublicClient();
        client.DefaultRequestHeaders.Remove(RemoteIpStartupFilter.HeaderName);
        client.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, ApiFactory.AllowedIp);
        client.DefaultRequestHeaders.Add(AdminSurfaceMiddleware.CsrfHeaderName, AdminSurfaceMiddleware.CsrfHeaderValue);
        using var res = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Theory]
    [InlineData(null)]                       // Origin 없음
    [InlineData("https://evil.test")]        // 다른 origin
    [InlineData("https://blog.test")]        // 같은 사이트의 공개 origin도 거부
    [InlineData("http://admin.test")]        // 스킴이 다르면 다른 origin
    public async Task UnsafeMethod_WithWrongOrigin_Returns403(string? origin)
    {
        using var client = factory.CreateAdminClient();
        client.DefaultRequestHeaders.Remove("Origin");
        if (origin is not null) client.DefaultRequestHeaders.Add("Origin", origin);
        using var res = await client.PostAsJsonAsync("/api/does-not-exist", new { });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task UnsafeMethod_WithAdminOrigin_PassesMiddleware()
    {
        using var client = factory.CreateAdminClient();
        using var res = await client.PostAsJsonAsync("/api/does-not-exist", new { });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode); // 미들웨어는 통과했고 엔드포인트가 없을 뿐이다
    }

    [Fact]
    public async Task Health_IsPublic_OnAnyHost_FromAnyIp()
    {
        using var client = factory.CreatePublicClient();
        using var res = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
```

`PortfolioBlog.Api.Tests/Features/ForwardedHeadersTests.cs`:

```csharp
using System.Net;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>X-Forwarded-For는 설정된 단 하나의 프록시 IP가 보낸 경우에만 믿는다.</summary>
[Collection("postgres")]
public sealed class ForwardedHeadersTests(PostgresContainerFixture pg)
{
    private const string Proxy = "172.30.0.2";        // Caddy 컨테이너 고정 IP
    private const string OtherContainer = "172.30.0.9"; // 같은 compose 네트워크의 다른 컨테이너

    private static async Task<HttpStatusCode> GetMeAsync(ApiFactory factory, string remoteIp, string? forwardedFor)
    {
        using var client = factory.CreateAdminClient();
        client.DefaultRequestHeaders.Remove(RemoteIpStartupFilter.HeaderName);
        client.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, remoteIp);
        if (forwardedFor is not null) client.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);
        using var res = await client.GetAsync("/api/auth/me");
        return res.StatusCode;
    }

    [Fact]
    public async Task TrustedProxy_ForwardedFor_IsHonored()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?> { ["Proxy:TrustedIp"] = Proxy });
        Assert.Equal(HttpStatusCode.OK, await GetMeAsync(factory, Proxy, ApiFactory.AllowedIp));
        Assert.Equal(HttpStatusCode.Forbidden, await GetMeAsync(factory, Proxy, ApiFactory.OutsiderIp));
    }

    [Fact]
    public async Task TrustedProxy_SpoofedLeftmostEntry_IsIgnored()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?> { ["Proxy:TrustedIp"] = Proxy });
        // 공격자가 보낸 "허용 IP"가 맨 왼쪽, Caddy가 붙인 실제 IP가 맨 오른쪽. ForwardLimit=1이라 오른쪽 하나만 본다.
        Assert.Equal(HttpStatusCode.Forbidden,
            await GetMeAsync(factory, Proxy, $"{ApiFactory.AllowedIp}, {ApiFactory.OutsiderIp}"));
    }

    [Fact]
    public async Task UntrustedSender_EvenInSameNetwork_ForwardedForIsIgnored()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?> { ["Proxy:TrustedIp"] = Proxy });
        Assert.Equal(HttpStatusCode.Forbidden, await GetMeAsync(factory, OtherContainer, ApiFactory.AllowedIp));
        Assert.Equal(HttpStatusCode.Forbidden, await GetMeAsync(factory, ApiFactory.OutsiderIp, ApiFactory.AllowedIp));
    }

    [Fact]
    public async Task NoTrustedProxy_MiddlewareNotRegistered_ForwardedForIgnored()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        // 신뢰 목록이 비면 ASP.NET 기본 동작은 "모든 헤더를 믿음"이다. 그래서 미들웨어를 아예 등록하지 않는다.
        Assert.Equal(HttpStatusCode.Forbidden, await GetMeAsync(factory, ApiFactory.OutsiderIp, ApiFactory.AllowedIp));
        Assert.Equal(HttpStatusCode.OK, await GetMeAsync(factory, ApiFactory.AllowedIp, null));
    }
}
```

`PortfolioBlog.Api.Tests/Features/StartupValidationTests.cs`:

```csharp
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>설정 오류는 조용히 넘어가지 않고 시작을 막는다.</summary>
[Collection("postgres")]
public sealed class StartupValidationTests(PostgresContainerFixture pg)
{
    private static Dictionary<string, string?> Production(Action<Dictionary<string, string?>> mutate)
    {
        var s = new Dictionary<string, string?>
        {
            ["Test:Environment"] = "Production",
            ["Proxy:TrustedIp"] = "172.30.0.2",
            // 형식(base64)만 맞는 더미 값. 실제 해시·비밀번호를 테스트 코드에 넣지 않는다.
            ["Admin:PasswordHash"] = Convert.ToBase64String(new byte[61]),
        };
        mutate(s);
        return s;
    }

    private void AssertStartupFails(Dictionary<string, string?> settings, string expectedKey)
    {
        using var factory = new ApiFactory(pg, settings);
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains(expectedKey, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Production_ValidSettings_Starts()
    {
        using var factory = new ApiFactory(pg, Production(_ => { }));
        using var client = factory.CreateClient();
    }

    [Fact]
    public void Production_MissingTrustedProxy_Fails() =>
        AssertStartupFails(Production(s => s["Proxy:TrustedIp"] = ""), "Proxy:TrustedIp");

    [Fact]
    public void Production_EmptyAllowedCidrs_Fails() =>
        AssertStartupFails(Production(s => s["Admin:AllowedCidrs"] = ""), "Admin:AllowedCidrs");

    [Fact]
    public void Production_MissingPasswordHash_Fails() =>
        AssertStartupFails(Production(s => s["Admin:PasswordHash"] = ""), "Admin:PasswordHash");

    [Fact]
    public void AnyEnvironment_InvalidCidr_Fails() =>
        AssertStartupFails(new Dictionary<string, string?> { ["Admin:AllowedCidrs"] = "203.0.113.0/24,198.51.100.0/24" }, "Admin:AllowedCidrs");

    [Fact]
    public void AnyEnvironment_InvalidTrustedProxy_Fails() =>
        AssertStartupFails(new Dictionary<string, string?> { ["Proxy:TrustedIp"] = "172.30.0.0/16" }, "Proxy:TrustedIp");

    [Fact]
    public void AnyEnvironment_OriginWithPath_Fails() =>
        AssertStartupFails(new Dictionary<string, string?> { ["Site:AdminOrigin"] = "https://admin.test/" }, "Site:AdminOrigin");
}
```

- [ ] **Step 3: 실패 확인**

Run: `dotnet test PortfolioBlog.Api.Tests --filter "FullyQualifiedName~AdminSurfaceTests|FullyQualifiedName~ForwardedHeadersTests|FullyQualifiedName~StartupValidationTests"`
Expected: 컴파일 오류(`AdminSurfaceMiddleware`, `AuthStatusDto` 없음).

- [ ] **Step 4: 미들웨어 구현** — `PortfolioBlog.Api/Infrastructure/Access/AdminSurfaceMiddleware.cs`

```csharp
using Microsoft.Extensions.Options;

namespace PortfolioBlog.Api.Infrastructure.Access;

/// <summary><c>/api</c> 요청을 본문을 읽기 전에 걸러 낸다: 관리 호스트가 아니면 404, 허용 IP가 아니면 403,
/// CSRF 헤더가 없으면 403, 안전하지 않은 메서드인데 Origin이 관리 origin이 아니면 403.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> 요청 파이프라인 스레드. 엔드포인트 실행(=인수 바인딩·본문 읽기)보다 앞이므로 거부된 요청의 본문은 읽히지 않는다.
/// 최소 API의 엔드포인트 필터는 바인딩 **이후**에 실행되기 때문에 필터가 아니라 미들웨어로 구현한다.</description></item>
/// <item><description><b>Memory Policy:</b> 통과 경로는 Zero-allocation(문자열 비교만). 거부 시 ProblemDetails 결과 1개 할당.</description></item>
/// <item><description><b>Concurrency:</b> Thread-safe. 생성 시 고정한 불변 필드만 읽는다. Non-blocking.</description></item>
/// </list>
/// IP 허용만으로는 CSRF를 막지 못한다(허용 네트워크 안의 브라우저가 악성 사이트를 열면 그 요청도 허용 IP에서 온다).
/// 브라우저는 교차 출처 요청에 커스텀 헤더를 붙이려면 CORS 프리플라이트를 통과해야 하고 이 앱은 CORS를 등록하지 않으므로
/// <c>X-Requested-With</c> 필수 검사로 교차 출처 폼 POST·multipart가 차단된다. Origin 검사는 같은 사이트의 다른 origin(공개 도메인)을 막는 2차 방어다.
/// </remarks>
public sealed class AdminSurfaceMiddleware
{
    public const string CsrfHeaderName = "X-Requested-With";
    public const string CsrfHeaderValue = "XMLHttpRequest";
    private static readonly PathString ApiPrefix = new("/api");

    private readonly RequestDelegate _next;
    private readonly IAdminAccessPolicy _policy;
    private readonly string _adminHost;
    private readonly string _adminOrigin;

    public AdminSurfaceMiddleware(RequestDelegate next, IAdminAccessPolicy policy, IOptions<SiteOptions> site)
    {
        _next = next;
        _policy = policy;
        _adminOrigin = site.Value.AdminOrigin;
        _adminHost = SiteOptions.HostOf(_adminOrigin);
    }

    public Task InvokeAsync(HttpContext context)
    {
        // StartsWithSegments는 기본이 OrdinalIgnoreCase이고 세그먼트 경계를 지킨다("/apix"는 불일치, "/API/x"는 일치). 라우팅의 대소문자 무시와 같은 기준이다.
        if (!context.Request.Path.StartsWithSegments(ApiPrefix))
        {
            return _next(context);
        }

        context.Response.Headers.CacheControl = "no-store";

        if (!string.Equals(context.Request.Host.Host, _adminHost, StringComparison.OrdinalIgnoreCase))
        {
            return Reject(context, StatusCodes.Status404NotFound, "찾을 수 없음", null);
        }
        if (!_policy.IsAllowed(context))
        {
            return Reject(context, StatusCodes.Status403Forbidden, "접근 거부", "이 네트워크에서는 관리 기능을 쓸 수 없습니다.");
        }
        if (!string.Equals(context.Request.Headers[CsrfHeaderName], CsrfHeaderValue, StringComparison.Ordinal))
        {
            return Reject(context, StatusCodes.Status403Forbidden, "교차 출처 요청 거부", $"{CsrfHeaderName}: {CsrfHeaderValue} 헤더가 필요합니다.");
        }
        var method = context.Request.Method;
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method)
            && !string.Equals(context.Request.Headers.Origin, _adminOrigin, StringComparison.Ordinal))
        {
            return Reject(context, StatusCodes.Status403Forbidden, "교차 출처 요청 거부", "Origin 헤더가 관리 origin과 일치해야 합니다.");
        }
        return _next(context);
    }

    private static Task Reject(HttpContext context, int status, string title, string? detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail).ExecuteAsync(context);
}
```

- [ ] **Step 5: 시작 검증 · 서비스 등록 구현**

```csharp
// Infrastructure/Access/StartupValidation.cs
using System.Net;
using Microsoft.Extensions.Options;

namespace PortfolioBlog.Api.Infrastructure.Access;

/// <summary>보안 관련 설정을 시작 시점에 한 번 검증한다. 하나라도 틀리면 예외로 프로세스 시작을 막는다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 시작 스레드에서 1회 호출. 공유 상태 없음.</description></item>
/// <item><description><b>Memory Allocation:</b> 시작 시 1회성 할당만.</description></item>
/// <item><description><b>Blocking:</b> 동기, I/O 없음. DB 마이그레이션보다 **앞**에서 호출해 설정 오류가 DB 접속 오류에 가려지지 않게 한다.</description></item>
/// </list>
/// </remarks>
public static class StartupValidation
{
    public static void Validate(IServiceProvider services, IHostEnvironment environment)
    {
        var site = services.GetRequiredService<IOptions<SiteOptions>>().Value;
        var admin = services.GetRequiredService<IOptions<AdminOptions>>().Value;
        var proxy = services.GetRequiredService<IOptions<ProxyOptions>>().Value;

        Check("Site:PublicOrigin", () => SiteOptions.HostOf(site.PublicOrigin));
        Check("Site:AdminOrigin", () => SiteOptions.HostOf(site.AdminOrigin));
        var cidrs = Check("Admin:AllowedCidrs", () => CidrList.Parse(admin.AllowedCidrs));
        if (proxy.TrustedIp.Length > 0)
        {
            Check("Proxy:TrustedIp", () => IPAddress.TryParse(proxy.TrustedIp, out var ip)
                ? ip
                : throw new FormatException($"단일 IP 주소여야 합니다(CIDR 아님): '{proxy.TrustedIp}'"));
        }
        if (admin.PasswordHash.Length > 0)
        {
            Check("Admin:PasswordHash", () => Convert.FromBase64String(admin.PasswordHash));
        }
        if (admin.LoginPerIpPerMinute < 1 || admin.LoginGlobalPerMinute < 1 || admin.LoginConcurrency < 1 || admin.SessionHours < 1)
        {
            throw new InvalidOperationException("Admin:LoginPerIpPerMinute·LoginGlobalPerMinute·LoginConcurrency·SessionHours 는 1 이상이어야 합니다.");
        }

        if (environment.IsProduction())
        {
            // 운영은 반드시 프록시(Caddy) 뒤에서 돈다. 빠뜨리면 모든 요청의 원본 IP가 Caddy 주소가 되어 IP 검사가 무의미해진다.
            Require(proxy.TrustedIp.Length > 0, "Proxy:TrustedIp");
            Require(cidrs.Count > 0, "Admin:AllowedCidrs");
            Require(admin.PasswordHash.Length > 0, "Admin:PasswordHash");
        }
    }

    private static T Check<T>(string key, Func<T> parse)
    {
        try { return parse(); }
        catch (FormatException ex) { throw new InvalidOperationException($"설정 {key} 이(가) 잘못되었습니다: {ex.Message}", ex); }
    }

    private static void Require(bool ok, string key)
    {
        if (!ok) throw new InvalidOperationException($"Production 환경에서는 설정 {key} 이(가) 필수입니다.");
    }
}
```

```csharp
// Infrastructure/Access/AccessServiceCollectionExtensions.cs
using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace PortfolioBlog.Api.Infrastructure.Access;

public static class AccessServiceCollectionExtensions
{
    /// <summary>Site·Admin·Proxy 옵션, IP 허용 정책, ForwardedHeaders 옵션을 등록한다. 설정 값은 전부 지연 바인딩이다.</summary>
    public static IServiceCollection AddAdminAccess(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SiteOptions>(configuration.GetSection(SiteOptions.SectionName));
        services.Configure<AdminOptions>(configuration.GetSection(AdminOptions.SectionName));
        services.Configure<ProxyOptions>(configuration.GetSection(ProxyOptions.SectionName));
        services.AddSingleton<IAdminAccessPolicy, IpAllowlistAdminAccessPolicy>();

        services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<ProxyOptions>>((o, proxy) =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // ForwardLimit=1: 직전 홉(Caddy)이 붙인 맨 오른쪽 항목 하나만 본다. 그 왼쪽은 클라이언트가 위조할 수 있다.
            o.ForwardLimit = 1;
            // 기본값(루프백)을 지우고 Caddy 고정 IP 하나만 신뢰한다. .NET 10에서 KnownNetworks는 obsolete라 KnownIPNetworks를 쓴다.
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
            if (IPAddress.TryParse(proxy.Value.TrustedIp, out var ip))
            {
                o.KnownProxies.Add(ip);
            }
        });
        return services;
    }

    /// <summary>신뢰 프록시가 설정된 경우에만 ForwardedHeaders 미들웨어를 등록한다.</summary>
    /// <remarks>
    /// ForwardedHeadersMiddleware는 KnownIPNetworks와 KnownProxies가 <b>둘 다 비어 있으면 송신자 검사를 생략</b>하고 모든 X-Forwarded-For를 신뢰한다.
    /// 그래서 설정이 비었을 때 미들웨어를 등록하면 외부에서 헤더를 위조해 IP 검사를 우회할 수 있다. 비어 있으면 아예 넣지 않는다.
    /// </remarks>
    public static WebApplication UseTrustedForwardedHeaders(this WebApplication app)
    {
        if (app.Services.GetRequiredService<IOptions<ProxyOptions>>().Value.TrustedIp.Length > 0)
        {
            app.UseForwardedHeaders();
        }
        return app;
    }
}
```

- [ ] **Step 6: `/api/auth/me` + 엔드포인트 배선**

```csharp
// Contracts/AuthDtos.cs
namespace PortfolioBlog.Api.Contracts;

/// <summary>현재 요청의 로그인 여부. 관리 SPA가 로그인 화면으로 보낼지 결정하는 근거.</summary>
public sealed record AuthStatusDto(bool Authenticated);
```

```csharp
// Features/Auth/AuthEndpoints.cs
using PortfolioBlog.Api.Contracts;

namespace PortfolioBlog.Api.Features.Auth;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth");
        auth.MapGet("/me", (HttpContext ctx) => TypedResults.Ok(new AuthStatusDto(ctx.User.Identity?.IsAuthenticated ?? false)))
            .WithName("GetAuthStatus");
    }
}
```

```csharp
// Features/ApiEndpoints.cs
using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Features.Auth;
using PortfolioBlog.Api.Infrastructure.Access;

namespace PortfolioBlog.Api.Features;

/// <summary><c>/api</c> 그룹을 만들고 각 기능의 엔드포인트를 등록한다.</summary>
public static class ApiEndpoints
{
    public static RouteGroupBuilder MapApiEndpoints(this WebApplication app)
    {
        var adminHost = SiteOptions.HostOf(app.Services.GetRequiredService<IOptions<SiteOptions>>().Value.AdminOrigin);
        // RequireHost: 미들웨어의 호스트 검사와 같은 규칙을 라우팅에도 걸어 둔다(미들웨어 순서를 잘못 바꿔도 공개 호스트에서는 매칭되지 않는다).
        var api = app.MapGroup("/api").RequireHost(adminHost);
        api.MapAuthEndpoints();
        return api;
    }
}
```

Program.cs 수정:
- using 추가: `PortfolioBlog.Api.Features`, `PortfolioBlog.Api.Infrastructure.Access`.
- `builder.Services.AddDbContext<...>` 다음 줄: `builder.Services.AddAdminAccess(builder.Configuration);`
- `var app = builder.Build();` **바로 다음**, 마이그레이션 블록 **앞**:

```csharp
// 설정 오류가 DB 접속 오류에 가려지지 않도록 마이그레이션보다 먼저 검증한다.
StartupValidation.Validate(app.Services, app.Environment);
```

- `app.UseExceptionHandler();` **앞**에 `app.UseTrustedForwardedHeaders();`
- `app.UseStatusCodePages();` 다음에 `app.UseMiddleware<AdminSurfaceMiddleware>();`
- `app.MapGet("/health", ...)` 다음에 `app.MapApiEndpoints();`

`appsettings.json`에 추가:

```json
"Site": { "PublicOrigin": "", "AdminOrigin": "" },
"Admin": { "AllowedCidrs": "", "PasswordHash": "" },
"Proxy": { "TrustedIp": "" }
```

`appsettings.Development.json`에 추가(개발은 한 호스트에서 공개·관리를 같이 띄운다. `https` 실행 프로필 기준):

```json
"Site": { "PublicOrigin": "https://localhost:7198", "AdminOrigin": "https://localhost:7198" },
"Admin": { "AllowedCidrs": "127.0.0.1/32 ::1/128" }
```

- [ ] **Step 7: 통과 확인**

Run: `dotnet test PortfolioBlog.slnx`
Expected: 전부 PASS(AdminSurface 14, ForwardedHeaders 4, StartupValidation 7 포함), 경고 0.
`StartupValidationTests`가 "예외가 나지 않음"으로 실패하면 `StartupValidation.Validate` 호출이 `builder.Build()` 뒤·`Migrate()` 앞에 있는지 확인한다.

- [ ] **Step 8: 커밋**

```bash
git add -A
git commit -m "추가: 본문을 읽기 전에 호스트·IP·CSRF를 거르는 관리 표면 미들웨어

- 엔드포인트 필터는 바인딩 이후 실행되므로 미들웨어로 구현(미인증 업로드 본문 버퍼링 차단)
- X-Forwarded-For는 Caddy 고정 IP 하나만 신뢰, 미설정 시 미들웨어 미등록
- 보안 설정 오류는 시작 실패로 처리(Production은 프록시·CIDR·비밀번호 해시 필수)

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---
### Task 4: 비밀번호 로그인 · 세션 폐기 · 로그인 속도 제한

**Files:**
- Create: `PortfolioBlog.Api/Infrastructure/Access/{AdminCredential,SessionRules,SessionValidator,HashPasswordCommand,AuthServiceCollectionExtensions}.cs`
- Modify: `PortfolioBlog.Api/Contracts/AuthDtos.cs`, `PortfolioBlog.Api/Features/Auth/AuthEndpoints.cs`, `PortfolioBlog.Api/Features/ApiEndpoints.cs`, `PortfolioBlog.Api/Program.cs`
- Create: `PortfolioBlog.Api.Tests/Infrastructure/MutableTimeProvider.cs`; Modify: `ApiFactory.cs`
- Test: `PortfolioBlog.Api.Tests/Infrastructure/{AdminCredentialTests,SessionRulesTests}.cs`, `PortfolioBlog.Api.Tests/Features/AuthEndpointsTests.cs`

**Interfaces:**
- Consumes: `AdminOptions`, `AppDbContext.AdminStates`, `AdminSurfaceMiddleware`(Task 1~3).
- Produces:
  - `AdminCredential(IOptions<AdminOptions>)`: `bool Verify(string password)`, `string Fingerprint`, `static string Hash(string password)`.
  - `SessionRules.IsValid(DateTimeOffset? issuedUtc, DateTimeOffset now, TimeSpan lifetime, string? ticketFingerprint, string currentFingerprint, string? ticketEpoch, int currentEpoch) : bool`, 상수 `SessionRules.FingerprintClaim = "pwd"`, `EpochClaim = "epoch"`.
  - `AuthServiceCollectionExtensions.AddAdminAuth(this IServiceCollection)`, 상수 `AuthServiceCollectionExtensions.Scheme = "AdminCookie"`, `CookieName = "__Host-AdminSession"`, `PolicyName = "Admin"`, `LoginPath = "/api/auth/login"`.
  - 엔드포인트 `POST /api/auth/login`(`LoginRequest(string? Password)` → 204 | 400 | 401 | 429), `POST /api/auth/logout`(204, 세션 필수), `GET /api/auth/me`.
  - `MapApiEndpoints`가 돌려주는 `/api` 그룹은 이제 `.RequireAuthorization("Admin")`이다. **이후 Task의 엔드포인트는 기본이 세션 필수**이며 익명 허용은 `login`·`me`뿐이다.
  - 테스트: `ApiFactory.Password`, `ApiFactory.Clock`(`MutableTimeProvider`), `CreateLoggedInClientAsync()`, `LoginAndGetCookieAsync()`.

- [ ] **Step 1: 단위 테스트 작성**

```csharp
// PortfolioBlog.Api.Tests/Infrastructure/AdminCredentialTests.cs
using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Infrastructure.Access;

namespace PortfolioBlog.Api.Tests.Infrastructure;

public sealed class AdminCredentialTests
{
    private static AdminCredential Create(string hash) => new(Options.Create(new AdminOptions { PasswordHash = hash }));

    [Fact]
    public void Verify_CorrectPassword_True_WrongPassword_False()
    {
        var credential = Create(AdminCredential.Hash("correct horse battery staple"));
        Assert.True(credential.Verify("correct horse battery staple"));
        Assert.False(credential.Verify("correct horse battery stapl"));
        Assert.False(credential.Verify(""));
    }

    [Fact]
    public void Verify_EmptyHash_AlwaysFalse() => Assert.False(Create("").Verify("anything"));

    [Fact]
    public void Hash_IsSalted_SamePasswordGivesDifferentHashes() =>
        Assert.NotEqual(AdminCredential.Hash("pw-123456"), AdminCredential.Hash("pw-123456"));

    [Fact]
    public void Fingerprint_ChangesWhenHashChanges_AndDoesNotRevealHash()
    {
        var hashA = AdminCredential.Hash("a-password");
        var a = Create(hashA);
        var b = Create(AdminCredential.Hash("b-password"));
        Assert.NotEqual(a.Fingerprint, b.Fingerprint);
        Assert.Equal(a.Fingerprint, Create(hashA).Fingerprint);
        Assert.Equal(32, a.Fingerprint.Length); // 16바이트 hex
        Assert.DoesNotContain(a.Fingerprint, hashA, StringComparison.OrdinalIgnoreCase);
    }
}
```

```csharp
// PortfolioBlog.Api.Tests/Infrastructure/SessionRulesTests.cs
using PortfolioBlog.Api.Infrastructure.Access;

namespace PortfolioBlog.Api.Tests.Infrastructure;

public sealed class SessionRulesTests
{
    private static readonly DateTimeOffset Issued = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    private static bool IsValid(DateTimeOffset? issued = null, TimeSpan? age = null, string? fp = "f1", string? epoch = "3") =>
        SessionRules.IsValid(issued ?? Issued, Issued + (age ?? TimeSpan.FromHours(1)), Lifetime, fp, "f1", epoch, 3);

    [Fact] public void Fresh_Matching_IsValid() => Assert.True(IsValid());
    [Fact] public void ExactlyAtLifetime_IsInvalid() => Assert.False(IsValid(age: Lifetime));
    [Fact] public void PastLifetime_IsInvalid() => Assert.False(IsValid(age: TimeSpan.FromHours(13)));
    [Fact] public void IssuedInFuture_IsInvalid() => Assert.False(IsValid(age: TimeSpan.FromMinutes(-5)));
    [Fact] public void MissingIssuedUtc_IsInvalid() =>
        Assert.False(SessionRules.IsValid(null, Issued, Lifetime, "f1", "f1", "3", 3));
    [Fact] public void FingerprintMismatch_IsInvalid() => Assert.False(IsValid(fp: "f2"));   // 비밀번호가 바뀜
    [Fact] public void MissingFingerprint_IsInvalid() => Assert.False(IsValid(fp: null));
    [Fact] public void OlderEpoch_IsInvalid() => Assert.False(IsValid(epoch: "2"));            // 로그아웃으로 폐기됨
    [Fact] public void NonNumericEpoch_IsInvalid() => Assert.False(IsValid(epoch: "x"));
    [Fact] public void MissingEpoch_IsInvalid() => Assert.False(IsValid(epoch: null));
}
```

- [ ] **Step 2: 실패 확인**

Run: `dotnet test PortfolioBlog.Api.Tests --filter "FullyQualifiedName~AdminCredentialTests|FullyQualifiedName~SessionRulesTests"`
Expected: 컴파일 오류.

- [ ] **Step 3: 자격 증명 · 세션 규칙 구현** — `PortfolioBlog.Api/Infrastructure/Access/`

```csharp
// AdminCredential.cs
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace PortfolioBlog.Api.Infrastructure.Access;

/// <summary>단일 작성자 비밀번호의 검증기. 서버에는 해시만 있다(<c>Admin:PasswordHash</c>).</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 생성 후 불변. <see cref="PasswordHasher{TUser}"/>는 무상태다.</description></item>
/// <item><description><b>Memory Allocation:</b> <see cref="Verify"/>는 호출당 수백 바이트(디코딩 버퍼·파생 키)를 할당한다.</description></item>
/// <item><description><b>Blocking:</b> <see cref="Verify"/>는 CPU 바운드 동기 연산(PBKDF2-HMAC-SHA512 10만 회, 수십 ms). 호출부가 동시 실행 수를 속도 제한기로 묶는다.</description></item>
/// </list>
/// 프레임워크 내장 해셔를 쓰는 이유: 추가 패키지(공급망 표면) 없이 솔트·반복 횟수·알고리즘 버전이 해시 문자열에 함께 저장되고, 비교가 고정 시간이다.
/// </remarks>
public sealed class AdminCredential
{
    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object User = new();
    private readonly string _hash;

    public AdminCredential(IOptions<AdminOptions> options)
    {
        _hash = options.Value.PasswordHash;
        // 지문: 해시 문자열의 SHA-256 앞 16바이트. 쿠키 티켓에 넣어 "비밀번호가 바뀌면 기존 세션 무효"를 DB 상태 없이 구현한다. 해시 자체는 쿠키에 넣지 않는다.
        Fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_hash)).AsSpan(0, 16));
    }

    public string Fingerprint { get; }

    public bool Verify(string password)
    {
        if (_hash.Length == 0)
        {
            return false; // 해시 미설정 = 로그인 불가(fail closed)
        }
        return Hasher.VerifyHashedPassword(User, _hash, password) != PasswordVerificationResult.Failed;
    }

    public static string Hash(string password) => Hasher.HashPassword(User, password);
}
```

```csharp
// SessionRules.cs
using System.Globalization;

namespace PortfolioBlog.Api.Infrastructure.Access;

/// <summary>세션 티켓이 아직 유효한지 판정하는 순수 함수. 시계·DB·설정에 의존하지 않아 단위 테스트로 전 경우를 고정한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 무상태.</description></item>
/// <item><description><b>Memory Allocation:</b> Zero-allocation.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
/// </list>
/// </remarks>
public static class SessionRules
{
    public const string FingerprintClaim = "pwd";
    public const string EpochClaim = "epoch";

    public static bool IsValid(DateTimeOffset? issuedUtc, DateTimeOffset now, TimeSpan lifetime,
        string? ticketFingerprint, string currentFingerprint, string? ticketEpoch, int currentEpoch)
    {
        if (issuedUtc is not { } issued || issued > now || now - issued >= lifetime)
        {
            return false; // 절대 수명. sliding 연장이 없으므로 발급 시각만 본다.
        }
        if (!string.Equals(ticketFingerprint, currentFingerprint, StringComparison.Ordinal))
        {
            return false; // 비밀번호(해시)가 바뀌었다
        }
        return int.TryParse(ticketEpoch, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) && epoch == currentEpoch;
    }
}
```

Run: `dotnet test PortfolioBlog.Api.Tests --filter "FullyQualifiedName~AdminCredentialTests|FullyQualifiedName~SessionRulesTests"`
Expected: 14개 PASS.

- [ ] **Step 4: 통합 테스트 인프라** — `MutableTimeProvider.cs` 생성, `ApiFactory.cs` 수정

```csharp
// PortfolioBlog.Api.Tests/Infrastructure/MutableTimeProvider.cs
namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>테스트가 앞으로 돌릴 수 있는 시계. 세션 절대 만료 검증용.</summary>
public sealed class MutableTimeProvider : TimeProvider
{
    // Interlocked로 읽고 쓰는 틱: 테스트 스레드가 바꾼 값을 서버 스레드가 찢어짐 없이 관측한다.
    private long _offsetTicks;

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

    public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);
}
```

`ApiFactory.cs`에 추가(using `System.Net`, `System.Net.Http.Json`, `Microsoft.Extensions.DependencyInjection.Extensions`, `PortfolioBlog.Api.Infrastructure.Access`):

```csharp
    public const string Password = "dummy-test-password-0920"; // 테스트 전용 더미 값(실제 비밀번호 아님)
    // PBKDF2 10만 회라 해시 생성이 수십 ms 걸린다. 프로세스당 한 번만 만든다.
    private static readonly string PasswordHash = AdminCredential.Hash(Password);

    public MutableTimeProvider Clock { get; } = new();

    // ConfigureWebHost의 기본값 블록에 추가(_settings 루프 앞):
    builder.UseSetting("Admin:PasswordHash", PasswordHash);
    // 로그인 테스트가 서로의 한도를 소진하지 않도록 기본 한도를 크게 둔다. 속도 제한 테스트만 작은 값으로 덮어쓴다.
    builder.UseSetting("Admin:LoginPerIpPerMinute", "1000");
    builder.UseSetting("Admin:LoginGlobalPerMinute", "1000");
    builder.UseSetting("Admin:LoginConcurrency", "64");

    // ConfigureServices 람다 안에 추가:
    services.RemoveAll<TimeProvider>();
    services.AddSingleton<TimeProvider>(Clock);

    /// <summary>로그인한 관리 클라이언트(쿠키 컨테이너가 세션 쿠키를 들고 있다).</summary>
    public async Task<HttpClient> CreateLoggedInClientAsync()
    {
        var client = CreateAdminClient();
        using var res = await client.PostAsJsonAsync("/api/auth/login", new { password = Password });
        if (res.StatusCode != HttpStatusCode.NoContent)
        {
            throw new InvalidOperationException($"테스트 로그인 실패: {(int)res.StatusCode}");
        }
        return client;
    }

    /// <summary>로그인하고 <c>name=value</c> 형태의 쿠키 헤더 값을 돌려준다. "복사해 둔 쿠키 재사용" 시나리오용.</summary>
    public async Task<string> LoginAndGetCookieAsync()
    {
        using var client = CreateAdminClient(handleCookies: false);
        using var res = await client.PostAsJsonAsync("/api/auth/login", new { password = Password });
        var setCookie = res.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(AuthServiceCollectionExtensions.CookieName + "=", StringComparison.Ordinal));
        return setCookie.Split(';', 2)[0];
    }
```

- [ ] **Step 5: 실패하는 통합 테스트 작성** — `PortfolioBlog.Api.Tests/Features/AuthEndpointsTests.cs`

```csharp
using System.Net;
using System.Net.Http.Json;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Infrastructure.Access;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

[Collection("postgres")]
public sealed class AuthEndpointsTests(ApiFactory factory, PostgresContainerFixture pg) : IClassFixture<ApiFactory>
{
    private const string Login = "/api/auth/login";
    private const string Logout = "/api/auth/logout";
    private const string Me = "/api/auth/me";

    private static async Task<bool> IsAuthenticatedAsync(HttpClient client, string? cookie = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Me);
        if (cookie is not null) req.Headers.Add("Cookie", cookie);
        using var res = await client.SendAsync(req);
        return (await res.Content.ReadFromJsonAsync<AuthStatusDto>(TestJson.Options))!.Authenticated;
    }

    [Fact]
    public async Task Login_CorrectPassword_Returns204_AndHardenedCookie()
    {
        using var client = factory.CreateAdminClient(handleCookies: false);
        using var res = await client.PostAsJsonAsync(Login, new { password = ApiFactory.Password });

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        var cookie = res.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant();
        Assert.StartsWith("__host-adminsession=", cookie);
        Assert.Contains("; path=/", cookie);
        Assert.Contains("; secure", cookie);
        Assert.Contains("; httponly", cookie);
        Assert.Contains("; samesite=strict", cookie);
        Assert.DoesNotContain("domain=", cookie);   // __Host- 접두사 요건
        Assert.DoesNotContain("expires=", cookie);  // 세션 쿠키. 수명은 서버가 티켓 발급 시각으로 강제한다.
    }

    [Theory]
    [InlineData("wrong-password")]
    [InlineData("")]
    public async Task Login_WrongPassword_Returns401_NoCookie(string password)
    {
        using var client = factory.CreateAdminClient(handleCookies: false);
        using var res = await client.PostAsJsonAsync(Login, new { password });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.False(res.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Login_MissingOrOversizedPassword_Returns400()
    {
        using var client = factory.CreateAdminClient(handleCookies: false);
        using var missing = await client.PostAsJsonAsync(Login, new { });
        using var oversized = await client.PostAsJsonAsync(Login, new { password = new string('a', 257) });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
    }

    [Fact]
    public async Task Me_ReflectsSession()
    {
        using var anonymous = factory.CreateAdminClient();
        Assert.False(await IsAuthenticatedAsync(anonymous));
        using var loggedIn = await factory.CreateLoggedInClientAsync();
        Assert.True(await IsAuthenticatedAsync(loggedIn));
    }

    [Fact]
    public async Task ProtectedEndpoint_WithoutSession_Returns401_NotRedirect()
    {
        using var client = factory.CreateAdminClient();
        using var res = await client.PostAsync(Logout, null);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Null(res.Headers.Location);
    }

    [Fact]
    public async Task Logout_RevokesEverySession_IncludingCopiedCookies()
    {
        var stolen = await factory.LoginAndGetCookieAsync();       // 다른 곳에 복사해 둔 쿠키
        using var probe = factory.CreateAdminClient(handleCookies: false);
        Assert.True(await IsAuthenticatedAsync(probe, stolen));

        using var owner = await factory.CreateLoggedInClientAsync();
        using var res = await owner.PostAsync(Logout, null);
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        Assert.False(await IsAuthenticatedAsync(probe, stolen));   // epoch가 올라 서버가 거부한다
        Assert.False(await IsAuthenticatedAsync(owner));
    }

    [Fact]
    public async Task Session_ExpiresAfterAbsoluteLifetime_NoSliding()
    {
        using var isolated = new ApiFactory(pg, new Dictionary<string, string?>()); // 시계를 돌리므로 다른 테스트와 호스트를 나눈다
        var cookie = await isolated.LoginAndGetCookieAsync();
        using var probe = isolated.CreateAdminClient(handleCookies: false);

        isolated.Clock.Advance(TimeSpan.FromHours(11));
        Assert.True(await IsAuthenticatedAsync(probe, cookie));    // 활동이 있어도 수명은 늘지 않는다
        isolated.Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        Assert.False(await IsAuthenticatedAsync(probe, cookie));
    }

    [Fact]
    public async Task Login_IsRateLimitedPerIp()
    {
        using var limited = new ApiFactory(pg, new Dictionary<string, string?> { ["Admin:LoginPerIpPerMinute"] = "3" });
        using var client = limited.CreateAdminClient(handleCookies: false);
        for (var i = 0; i < 3; i++)
        {
            using var res = await client.PostAsJsonAsync(Login, new { password = "wrong" });
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        }
        using var fourth = await client.PostAsJsonAsync(Login, new { password = ApiFactory.Password });
        Assert.Equal(HttpStatusCode.TooManyRequests, fourth.StatusCode); // 맞는 비밀번호라도 한도 초과면 거부

        // 다른 허용 IP는 자기 몫의 한도를 가진다.
        client.DefaultRequestHeaders.Remove(RemoteIpStartupFilter.HeaderName);
        client.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, "203.0.113.200");
        using var other = await client.PostAsJsonAsync(Login, new { password = ApiFactory.Password });
        Assert.Equal(HttpStatusCode.NoContent, other.StatusCode);
    }

    [Fact]
    public async Task Login_GlobalLimit_AppliesAcrossIps()
    {
        using var limited = new ApiFactory(pg, new Dictionary<string, string?> { ["Admin:LoginGlobalPerMinute"] = "2" });
        using var client = limited.CreateAdminClient(handleCookies: false);
        for (var i = 0; i < 3; i++)
        {
            client.DefaultRequestHeaders.Remove(RemoteIpStartupFilter.HeaderName);
            client.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, $"203.0.113.{10 + i}");
            using var res = await client.PostAsJsonAsync(Login, new { password = "wrong" });
            Assert.Equal(i < 2 ? HttpStatusCode.Unauthorized : HttpStatusCode.TooManyRequests, res.StatusCode);
        }
    }

    [Fact]
    public async Task Login_FromOutsiderIp_Returns403_AndDoesNotConsumeRateLimit()
    {
        using var limited = new ApiFactory(pg, new Dictionary<string, string?> { ["Admin:LoginGlobalPerMinute"] = "1" });
        using var outsider = limited.CreateAdminClient(handleCookies: false);
        outsider.DefaultRequestHeaders.Remove(RemoteIpStartupFilter.HeaderName);
        outsider.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, ApiFactory.OutsiderIp);
        for (var i = 0; i < 5; i++)
        {
            using var res = await outsider.PostAsJsonAsync(Login, new { password = ApiFactory.Password });
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }
        using var author = limited.CreateAdminClient(handleCookies: false);
        using var ok = await author.PostAsJsonAsync(Login, new { password = ApiFactory.Password });
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode); // 외부 요청이 작성자의 로그인 한도를 소진하지 못한다
    }
}
```

Run: `dotnet test PortfolioBlog.Api.Tests --filter "FullyQualifiedName~AuthEndpointsTests"`
Expected: 컴파일 오류(`AuthServiceCollectionExtensions` 없음).

- [ ] **Step 6: 인증 서비스 등록 구현** — `PortfolioBlog.Api/Infrastructure/Access/`

```csharp
// SessionValidator.cs
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Domain;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Infrastructure.Access;

/// <summary>쿠키 티켓을 요청마다 서버 상태와 대조한다(절대 만료·비밀번호 지문·세션 epoch). 어긋나면 주체를 거부하고 쿠키를 지운다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> 인증 미들웨어가 요청 스레드에서 호출한다. 쿠키가 없는 요청에서는 호출되지 않는다.</description></item>
/// <item><description><b>Memory Policy:</b> 요청당 DbContext 스코프 조회 1회(단일 행 int 프로젝션).</description></item>
/// <item><description><b>Concurrency:</b> Thread-safe(무상태). Non-blocking: DB 조회를 await 한다. 관리 트래픽은 작성자 1명이라 캐시를 두지 않는다 — 캐시가 있으면 로그아웃 직후에도 폐기된 쿠키가 잠시 통한다.</description></item>
/// </list>
/// </remarks>
public static class SessionValidator
{
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var services = context.HttpContext.RequestServices;
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var lifetime = TimeSpan.FromHours(services.GetRequiredService<IOptions<AdminOptions>>().Value.SessionHours);
        var credential = services.GetRequiredService<AdminCredential>();
        var epoch = await services.GetRequiredService<AppDbContext>().AdminStates.AsNoTracking()
            .Where(s => s.Id == AdminState.SingletonId)
            .Select(s => s.SessionEpoch)
            .SingleAsync(context.HttpContext.RequestAborted);

        var valid = SessionRules.IsValid(
            context.Properties.IssuedUtc, now, lifetime,
            context.Principal?.FindFirst(SessionRules.FingerprintClaim)?.Value, credential.Fingerprint,
            context.Principal?.FindFirst(SessionRules.EpochClaim)?.Value, epoch);

        if (!valid)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(AuthServiceCollectionExtensions.Scheme);
        }
    }
}
```

```csharp
// AuthServiceCollectionExtensions.cs
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace PortfolioBlog.Api.Infrastructure.Access;

public static class AuthServiceCollectionExtensions
{
    public const string Scheme = "AdminCookie";
    public const string CookieName = "__Host-AdminSession";
    public const string PolicyName = "Admin";
    public const string LoginPath = "/api/auth/login";
    public const string DataProtectionKeysPathKey = "DataProtection:KeysPath";

    /// <summary>쿠키 인증·인가 정책·로그인 속도 제한·Data Protection을 등록한다.</summary>
    public static IServiceCollection AddAdminAuth(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<AdminCredential>();

        services.AddAuthentication(Scheme).AddCookie(Scheme);
        services.AddOptions<CookieAuthenticationOptions>(Scheme).Configure<TimeProvider, IOptions<AdminOptions>>((o, clock, admin) =>
        {
            o.TimeProvider = clock;
            // __Host- 접두사: 브라우저가 Secure + Path=/ + Domain 미지정을 강제한다 → 형제 서브도메인(공개 도메인)이 이 쿠키를 덮어쓰거나 받을 수 없다.
            o.Cookie.Name = CookieName;
            o.Cookie.Path = "/";
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.IsEssential = true;
            o.ExpireTimeSpan = TimeSpan.FromHours(admin.Value.SessionHours);
            o.SlidingExpiration = false;
            // API는 로그인 HTML로 리다이렉트하지 않는다.
            o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
            o.Events.OnValidatePrincipal = SessionValidator.ValidateAsync;
        });
        services.AddAuthorizationBuilder().AddPolicy(PolicyName, p => p.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser());

        // 키 경로가 설정된 경우(운영)에만 파일에 영속화한다. 없으면 프레임워크 기본 위치를 쓴다(개발·테스트).
        services.AddDataProtection().SetApplicationName("PortfolioBlog.Api");
        services.AddOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>().Configure<IConfiguration>((o, cfg) =>
        {
            var path = cfg[DataProtectionKeysPathKey];
            if (!string.IsNullOrWhiteSpace(path))
            {
                o.XmlRepository = new Microsoft.AspNetCore.DataProtection.Repositories.FileSystemXmlRepository(
                    new DirectoryInfo(path), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
            }
        });

        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<AdminOptions>>((o, adminOptions) =>
        {
            var admin = adminOptions.Value;
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // 체인: 세 제한기를 모두 통과해야 한다. 로그인이 아닌 요청은 NoLimiter 파티션으로 빠진다. Plan 2가 공개 검색·미리보기 제한기를 이 체인에 추가한다.
            o.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                // FixedWindow: 창마다 카운터 하나만 두는 O(1) 제한기. 허용 IP 수가 적어 파티션 수도 작다.
                PartitionedRateLimiter.Create<HttpContext, string>(ctx => IsLogin(ctx)
                    ? RateLimitPartition.GetFixedWindowLimiter("login-ip:" + ctx.Connection.RemoteIpAddress, _ => Window(admin.LoginPerIpPerMinute))
                    : RateLimitPartition.GetNoLimiter("none")),
                PartitionedRateLimiter.Create<HttpContext, string>(ctx => IsLogin(ctx)
                    ? RateLimitPartition.GetFixedWindowLimiter("login-global", _ => Window(admin.LoginGlobalPerMinute))
                    : RateLimitPartition.GetNoLimiter("none")),
                // Concurrency: PBKDF2 검증은 CPU 바운드라 동시에 도는 수를 묶는다. 임대는 요청이 끝날 때 미들웨어가 반납한다. 대기열 0 = 초과분 즉시 429.
                PartitionedRateLimiter.Create<HttpContext, string>(ctx => IsLogin(ctx)
                    ? RateLimitPartition.GetConcurrencyLimiter("login-concurrency", _ => new ConcurrencyLimiterOptions { PermitLimit = admin.LoginConcurrency, QueueLimit = 0 })
                    : RateLimitPartition.GetNoLimiter("none")));
        });
        return services;
    }

    private static bool IsLogin(HttpContext ctx) =>
        HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.Path.Equals(LoginPath, StringComparison.OrdinalIgnoreCase);

    private static FixedWindowRateLimiterOptions Window(int permitsPerMinute) => new()
    {
        PermitLimit = permitsPerMinute,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
        AutoReplenishment = true,
    };
}
```

- [ ] **Step 7: 엔드포인트 구현**

`Contracts/AuthDtos.cs`에 추가:

```csharp
/// <summary>로그인 요청. 아이디가 없다(단일 작성자). 누락을 400으로 돌려주기 위해 nullable로 받는다.</summary>
public sealed record LoginRequest(string? Password);
```

`Features/Auth/AuthEndpoints.cs`를 다음으로 교체:

```csharp
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Domain;
using PortfolioBlog.Api.Infrastructure.Access;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Features.Auth;

public static class AuthEndpoints
{
    public const int PasswordMaxLength = 256;

    public static void MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth");
        auth.MapGet("/me", (HttpContext ctx) => TypedResults.Ok(new AuthStatusDto(ctx.User.Identity?.IsAuthenticated ?? false)))
            .AllowAnonymous().WithName("GetAuthStatus");
        auth.MapPost("/login", LoginAsync).AllowAnonymous().WithName("Login");
        auth.MapPost("/logout", LogoutAsync).WithName("Logout");
    }

    private static async Task<IResult> LoginAsync(LoginRequest request, HttpContext http, AdminCredential credential,
        AppDbContext db, TimeProvider clock, ILoggerFactory loggers, CancellationToken ct)
    {
        if (request.Password is null || request.Password.Length > PasswordMaxLength)
        {
            // 길이 상한: 해시 입력을 제한해 거대한 본문으로 PBKDF2 시간을 늘리는 공격을 막는다.
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["password"] = [$"비밀번호는 필수이며 {PasswordMaxLength}자 이하여야 합니다."],
            });
        }
        var logger = loggers.CreateLogger("PortfolioBlog.Api.Auth");
        if (!credential.Verify(request.Password))
        {
            logger.LogWarning("관리자 로그인 실패. RemoteIp={RemoteIp}", http.Connection.RemoteIpAddress); // 비밀번호·본문은 기록하지 않는다
            return TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "로그인 실패");
        }

        var epoch = await db.AdminStates.AsNoTracking().Where(s => s.Id == AdminState.SingletonId).Select(s => s.SessionEpoch).SingleAsync(ct);
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "admin"),
            new Claim(SessionRules.FingerprintClaim, credential.Fingerprint),
            new Claim(SessionRules.EpochClaim, epoch.ToString(CultureInfo.InvariantCulture)),
        ], AuthServiceCollectionExtensions.Scheme);
        // IsPersistent=false: 브라우저를 닫으면 사라지는 세션 쿠키. IssuedUtc를 주입한 시계로 고정해 절대 만료 판정의 기준으로 쓴다.
        await http.SignInAsync(AuthServiceCollectionExtensions.Scheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = false, IssuedUtc = clock.GetUtcNow() });
        logger.LogInformation("관리자 로그인 성공. RemoteIp={RemoteIp}", http.Connection.RemoteIpAddress);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, AppDbContext db, ILoggerFactory loggers, CancellationToken ct)
    {
        // 단일 작성자 정책: 로그아웃은 "모든 세션 폐기"다. 원자적 증가라 동시 로그아웃에도 안전하다.
        await db.AdminStates.Where(s => s.Id == AdminState.SingletonId)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.SessionEpoch, s => s.SessionEpoch + 1), ct);
        await http.SignOutAsync(AuthServiceCollectionExtensions.Scheme);
        loggers.CreateLogger("PortfolioBlog.Api.Auth").LogInformation("관리자 로그아웃(전 세션 폐기). RemoteIp={RemoteIp}", http.Connection.RemoteIpAddress);
        return TypedResults.NoContent();
    }
}
```

`Features/ApiEndpoints.cs`: 그룹 생성 줄을 다음으로 바꾼다.

```csharp
        var api = app.MapGroup("/api").RequireHost(adminHost).RequireAuthorization(AuthServiceCollectionExtensions.PolicyName);
```

- [ ] **Step 8: `hash-password` CLI** — `PortfolioBlog.Api/Infrastructure/Access/HashPasswordCommand.cs`

```csharp
using System.Text;

namespace PortfolioBlog.Api.Infrastructure.Access;

/// <summary><c>dotnet run --project PortfolioBlog.Api -- hash-password</c>: 비밀번호를 에코 없이 입력받아 <c>Admin:PasswordHash</c>에 넣을 해시를 출력한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 단일 스레드 CLI 경로. 웹 호스트를 만들지 않는다.</description></item>
/// <item><description><b>Memory Allocation:</b> 입력 버퍼와 해시 문자열. 비밀번호를 인수로 받지 않는 이유: 명령줄 인수는 셸 기록과 프로세스 목록에 남는다.</description></item>
/// <item><description><b>Blocking:</b> 콘솔 입력을 동기 대기한다.</description></item>
/// </list>
/// </remarks>
public static class HashPasswordCommand
{
    public const string Name = "hash-password";
    public const int MinLength = 12;

    public static int Run(TextReader input, TextWriter output, TextWriter error, bool interactive)
    {
        if (interactive) error.Write("새 관리자 비밀번호: ");
        var password = interactive ? ReadHidden() : input.ReadLine();
        if (interactive) error.WriteLine();
        if (password is null || password.Length < MinLength || password.Length > 256)
        {
            error.WriteLine($"비밀번호는 {MinLength}~256자여야 합니다.");
            return 1;
        }
        output.WriteLine(AdminCredential.Hash(password));
        return 0;
    }

    private static string ReadHidden()
    {
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) return sb.ToString();
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
    }
}
```

`AdminCredentialTests.cs`에 테스트 추가:

```csharp
    [Fact]
    public void HashPasswordCommand_PipedInput_PrintsVerifiableHash_AndRejectsShortPassword()
    {
        var output = new StringWriter();
        Assert.Equal(0, HashPasswordCommand.Run(new StringReader("a-long-enough-password\n"), output, TextWriter.Null, interactive: false));
        Assert.True(Create(output.ToString().Trim()).Verify("a-long-enough-password"));

        Assert.Equal(1, HashPasswordCommand.Run(new StringReader("short\n"), TextWriter.Null, new StringWriter(), interactive: false));
    }
```

- [ ] **Step 9: Program.cs 배선**

- 파일 맨 위(`var builder` 앞)에 추가:

```csharp
// CLI 경로: 웹 호스트를 만들지 않고 해시만 출력하고 끝낸다.
if (args is [HashPasswordCommand.Name])
{
    return HashPasswordCommand.Run(Console.In, Console.Out, Console.Error, interactive: !Console.IsInputRedirected);
}
```

- `app.Run();` 다음 줄에 `return 0;` 추가(최상위 문에 값 반환 `return`이 생겼으므로 끝도 int를 반환해야 한다).
- `builder.Services.AddAdminAccess(...)` 다음 줄: `builder.Services.AddAdminAuth();` (설정은 전부 지연 바인딩이라 `IConfiguration`을 받지 않는다)
- 미들웨어 순서를 다음으로 맞춘다(Global Constraints의 순서):

```csharp
app.UseTrustedForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<AdminSurfaceMiddleware>();
app.UseRateLimiter();      // IP 검사 뒤: 외부 요청이 로그인 한도를 소진하지 못한다
app.UseAuthentication();
app.UseAuthorization();
```

- [ ] **Step 10: 통과 확인**

Run: `dotnet test PortfolioBlog.slnx`
Expected: 전부 PASS(AuthEndpoints 11 포함), 경고 0.
`Session_ExpiresAfterAbsoluteLifetime_NoSliding`의 11시간 시점 단언이 실패하면 쿠키 핸들러의 `TimeProvider`가 주입되지 않은 것이다 → `AddOptions<CookieAuthenticationOptions>(Scheme)`의 스킴 이름이 `AddCookie(Scheme)`와 같은지 확인한다.

- [ ] **Step 11: CLI 수동 확인**

Run: `echo "a-long-enough-password" | dotnet run --project PortfolioBlog.Api -- hash-password`
Expected: `AQAAAA`로 시작하는 base64 한 줄, 종료 코드 0. DB 접속 시도 없음.

- [ ] **Step 12: 커밋**

```bash
git add -A
git commit -m "추가: 아이디 없는 비밀번호 로그인과 서버 측 세션 폐기

- 프레임워크 내장 PBKDF2 해셔 사용(추가 패키지 없음), 해시만 설정으로 주입
- __Host- 쿠키, 절대 수명 12시간, 티켓의 비밀번호 지문·세션 epoch를 요청마다 대조
- 로그아웃은 epoch 증가로 복사된 쿠키까지 전부 폐기
- 로그인 속도 제한(IP별·전역·동시 실행)을 IP 검사 뒤에 배치

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---
### Task 5: 글 관리 API (계약 · 검증 · 태그 해석 · 낙관적 동시성)

**Files:**
- Create: `PortfolioBlog.Api/Contracts/{ValidationErrors,PostDtos}.cs`
- Create: `PortfolioBlog.Api/Infrastructure/Data/{LikePattern,DbConflict,SlugRules,TagResolver,PostQueries}.cs`
- Create: `PortfolioBlog.Api/Features/Posts/{PostValidation,PostEndpoints}.cs`; Modify: `PortfolioBlog.Api/Features/ApiEndpoints.cs`
- Test: `PortfolioBlog.Api.Tests/Infrastructure/{LikePatternTests,TagResolverTests}.cs`, `PortfolioBlog.Api.Tests/Features/PostEndpointsTests.cs`

**Interfaces:**
- Consumes: `AppDbContext`(상수 `SlugPattern`, `SlugMax`, `TitleMax`, `SummaryMax`, `ContentMaxBytes`, `TagMax`), `DbClock`, `/api` 그룹(세션 필수), `ApiFactory.CreateLoggedInClientAsync()`.
- Produces:
  - `ValidationErrors`: `Add(string field, string message) : ValidationErrors`, `bool Any`, `ToDictionary() : Dictionary<string,string[]>`.
  - `PostSummaryDto(Guid Id, string Slug, string Title, string Summary, string[] Tags, Guid? SeriesId, int? SeriesOrder, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, uint Version)`
  - `PostDetailDto(Guid Id, string Slug, string Title, string Summary, string ContentMarkdown, string[] Tags, Guid? SeriesId, int? SeriesOrder, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, uint Version)`
  - `PagedPostsDto(PostSummaryDto[] Items, int Total)`
  - `UpsertPostRequest(string? Slug, string? Title, string? Summary, string? ContentMarkdown, string[]? TagNames, Guid? SeriesId, int? SeriesOrder, uint? Version)`
  - `LikePattern.Contains(string term) : string`, 상수 `LikePattern.Escape = "\\"`.
  - `DbConflict.IsConstraintRace(DbUpdateException) : bool`, `DbConflict.Problem(string detail) : ProblemHttpResult`.
  - `TagResolver.Normalize(string) : string`, `TagResolver.Display(string) : string`, `TagResolver.Validate(IEnumerable<string>?, ValidationErrors, string field)`, `TagResolver.ResolveIdsAsync(AppDbContext, IEnumerable<string>?, CancellationToken) : Task<List<Guid>>`, 상수 `TagResolver.MaxTagsPerPost = 20`.
  - `PostQueries.GetDetailAsync(AppDbContext, Guid, CancellationToken) : Task<PostDetailDto?>`, `PostQueries.ListAsync(IQueryable<Post>, int skip, int take, CancellationToken) : Task<PostSummaryDto[]>`(최신순 정렬·본문 제외 프로젝션).
  - `SlugRules.IsValid(string) : bool`(`Infrastructure/Data/SlugRules.cs`, Task 6이 재사용).
  - `PostEndpoints.MapPostEndpoints(this RouteGroupBuilder)`: `GET /api/posts`, `GET /api/posts/{id:guid}`, `POST /api/posts`, `PUT /api/posts/{id:guid}`, `DELETE /api/posts/{id:guid}?version=`.

- [ ] **Step 1: 순수 함수 단위 테스트 작성**

```csharp
// PortfolioBlog.Api.Tests/Infrastructure/LikePatternTests.cs
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

public sealed class LikePatternTests
{
    [Theory]
    [InlineData("abc", "%abc%")]
    [InlineData("100%", @"%100\%%")]
    [InlineData("a_c", @"%a\_c%")]
    [InlineData(@"c:\temp", @"%c:\\temp%")]
    [InlineData(@"\%_", @"%\\\%\_%")]
    public void Contains_EscapesLikeMetacharacters(string term, string expected) =>
        Assert.Equal(expected, LikePattern.Contains(term));
}
```

```csharp
// PortfolioBlog.Api.Tests/Infrastructure/TagResolverTests.cs
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

public sealed class TagResolverTests
{
    [Theory]
    [InlineData("  ASP.NET   Core ", "asp.net core")]
    [InlineData("C#", "c#")]
    [InlineData("cafe\u0301", "caf\u00e9")]           // NFC: 결합 문자 → 단일 코드 포인트
    public void Normalize_TrimsCollapsesLowercasesAndComposes(string raw, string expected) =>
        Assert.Equal(expected, TagResolver.Normalize(raw));

    [Fact]
    public void Display_KeepsCase_ButCollapsesWhitespace() =>
        Assert.Equal("ASP.NET Core", TagResolver.Display("  ASP.NET \t Core "));

    [Fact]
    public void Validate_ReportsSlashTooLongAndTooMany()
    {
        var errors = new ValidationErrors();
        TagResolver.Validate(["ok", "a/b", new string('x', 51)], errors, "tagNames");
        TagResolver.Validate(Enumerable.Range(0, 21).Select(i => $"t{i}"), errors, "many");

        var dict = errors.ToDictionary();
        Assert.Equal(2, dict["tagNames"].Length);
        Assert.Single(dict["many"]);
    }

    [Fact]
    public void Validate_IgnoresBlankEntries_AndCountsDistinctNormalizedNames()
    {
        var errors = new ValidationErrors();
        TagResolver.Validate(Enumerable.Repeat("Same", 30).Append("  "), errors, "tagNames");
        Assert.False(errors.Any);
    }
}
```

Run: `dotnet test PortfolioBlog.Api.Tests --filter "FullyQualifiedName~LikePatternTests|FullyQualifiedName~TagResolverTests"`
Expected: 컴파일 오류.

- [ ] **Step 2: Contracts 작성** — `PortfolioBlog.Api/Contracts/`

```csharp
// ValidationErrors.cs
namespace PortfolioBlog.Api.Contracts;

/// <summary>필드별 검증 오류를 모아 <c>TypedResults.ValidationProblem</c>에 넘길 사전을 만든다. 필드 키는 JSON 속성명(camelCase)을 쓴다.</summary>
public sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);
    public bool Any => _errors.Count > 0;
    public ValidationErrors Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list)) { list = new List<string>(1); _errors[field] = list; }
        list.Add(message);
        return this;
    }
    public Dictionary<string, string[]> ToDictionary() => _errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal);
}
```

```csharp
// PostDtos.cs
namespace PortfolioBlog.Api.Contracts;

/// <summary>목록용 글 요약. 본문을 싣지 않는다(목록 한 페이지가 200KB × N이 되지 않게).</summary>
public sealed record PostSummaryDto(Guid Id, string Slug, string Title, string Summary, string[] Tags,
    Guid? SeriesId, int? SeriesOrder, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, uint Version);

public sealed record PostDetailDto(Guid Id, string Slug, string Title, string Summary, string ContentMarkdown, string[] Tags,
    Guid? SeriesId, int? SeriesOrder, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, uint Version);

/// <summary>Total은 필터 적용 후 전체 건수.</summary>
public sealed record PagedPostsDto(PostSummaryDto[] Items, int Total);

/// <summary>생성·수정 공용 요청. 필수 필드도 nullable로 받아 누락을 바인딩 예외가 아닌 필드별 400으로 돌려준다.
/// <c>TagNames</c>·<c>SeriesId</c>·<c>SeriesOrder</c>는 연결을 통째로 교체한다(null = 없음). <c>Version</c>은 수정에서만 필수.</summary>
public sealed record UpsertPostRequest(string? Slug, string? Title, string? Summary, string? ContentMarkdown,
    string[]? TagNames, Guid? SeriesId, int? SeriesOrder, uint? Version);
```

- [ ] **Step 3: 데이터 헬퍼 작성** — `PortfolioBlog.Api/Infrastructure/Data/`

```csharp
// LikePattern.cs
namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>사용자 입력을 <c>ILIKE</c>의 "포함" 패턴으로 바꾼다. 메타문자(<c>\ % _</c>)를 이스케이프해 입력이 와일드카드로 해석되지 않게 한다.</summary>
/// <remarks>값은 항상 매개변수로 전달되므로 SQL 주입과는 별개의 문제다 — 여기서 막는 것은 "<c>%</c> 한 글자로 전체 테이블 스캔"과 의도와 다른 매칭이다.
/// Thread-safe(무상태) / 결과 문자열 1개 할당 / 즉시 반환.</remarks>
public static class LikePattern
{
    public const string Escape = "\\";

    public static string Contains(string term) =>
        "%" + term.Replace("\\", "\\\\", StringComparison.Ordinal)
                  .Replace("%", "\\%", StringComparison.Ordinal)
                  .Replace("_", "\\_", StringComparison.Ordinal) + "%";
}
```

```csharp
// DbConflict.cs
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>"검증 → 저장" 사이의 경쟁으로 DB 제약에 걸렸는지 판정한다: 유니크(23505, 같은 slug 동시 생성)와 FK(23503, 검증 직후 시리즈가 삭제됨).</summary>
/// <remarks>단일 작성자라도 탭 두 개로 발생한다. 서버가 재시도하지 않는 이유: 실패한 SaveChanges 뒤 변경 추적기 정리가 복잡하고 클라이언트 재시도가 더 투명하다.
/// Thread-safe(무상태) / 충돌 시 ProblemDetails 1개 할당 / 즉시 반환.</remarks>
public static class DbConflict
{
    public const string UniqueViolation = "23505";
    public const string ForeignKeyViolation = "23503";

    public static bool IsConstraintRace(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: UniqueViolation or ForeignKeyViolation };

    public static ProblemHttpResult Problem(string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "충돌", detail: detail);
}
```

```csharp
// TagResolver.cs
using System.Text;
using Microsoft.EntityFrameworkCore;
using PortfolioBlog.Api.Contracts;

namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>태그 이름 목록을 정규화·중복 제거하고, 없는 태그는 <c>ON CONFLICT DO NOTHING</c>으로 만들어 Id 목록을 돌려준다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. 전달된 DbContext의 스코프(와 열려 있는 트랜잭션) 안에서만 호출한다.</description></item>
/// <item><description><b>Memory Allocation:</b> 태그 수(최대 20)만큼의 문자열·리스트.</description></item>
/// <item><description><b>Blocking:</b> Non-blocking. 새 태그마다 INSERT 1회 + 조회 1회를 await 한다.</description></item>
/// </list>
/// "조회 → 없으면 EF로 Add" 방식은 두 요청이 같은 새 태그를 동시에 만들 때 한쪽이 유니크 위반으로 실패한다.
/// <c>ON CONFLICT (NormalizedName) DO NOTHING</c>은 그 경쟁을 DB가 흡수하므로 정상적인 동시 저장을 작성자에게 409로 돌려주지 않는다.
/// </remarks>
public static class TagResolver
{
    public const int MaxTagsPerPost = 20;

    /// <summary>표시용: 트림 + 연속 공백 1개 + NFC. 대소문자는 유지한다.</summary>
    public static string Display(string raw) =>
        string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Normalize(NormalizationForm.FormC);

    /// <summary>유일성 키: <see cref="Display"/> + 불변 문화권 소문자.</summary>
    public static string Normalize(string raw) => Display(raw).ToLowerInvariant();

    public static void Validate(IEnumerable<string>? names, ValidationErrors errors, string field)
    {
        if (names is null) return;
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in names)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var display = Display(raw);
            if (display.Length > AppDbContext.TagMax) errors.Add(field, $"태그는 {AppDbContext.TagMax}자 이하여야 합니다: {display[..20]}…");
            else if (display.Contains('/')) errors.Add(field, $"태그에 '/'를 쓸 수 없습니다: {display}");
            else distinct.Add(display.ToLowerInvariant());
        }
        if (distinct.Count > MaxTagsPerPost) errors.Add(field, $"태그는 글당 {MaxTagsPerPost}개까지입니다.");
    }

    public static async Task<List<Guid>> ResolveIdsAsync(AppDbContext db, IEnumerable<string>? names, CancellationToken ct)
    {
        var wanted = new Dictionary<string, string>(StringComparer.Ordinal); // normalized → display(먼저 나온 표기 우선)
        foreach (var raw in names ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var display = Display(raw);
            wanted.TryAdd(display.ToLowerInvariant(), display);
        }
        if (wanted.Count == 0) return [];

        var keys = wanted.Keys.ToArray();
        var existing = await db.Tags.AsNoTracking().Where(t => keys.Contains(t.NormalizedName)).Select(t => t.NormalizedName).ToListAsync(ct);
        foreach (var (normalized, display) in wanted)
        {
            if (existing.Contains(normalized)) continue;
            var id = Guid.CreateVersion7();
            // 보간 값은 전부 매개변수로 전달된다(ExecuteSqlInterpolated). 테이블·컬럼 이름만 리터럴이다.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""INSERT INTO "Tags" ("Id", "Name", "NormalizedName") VALUES ({id}, {display}, {normalized}) ON CONFLICT ("NormalizedName") DO NOTHING""", ct);
        }
        return await db.Tags.AsNoTracking().Where(t => keys.Contains(t.NormalizedName)).Select(t => t.Id).ToListAsync(ct);
    }
}
```

```csharp
// SlugRules.cs
using System.Text.RegularExpressions;

namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>slug 형식 규칙. DB CHECK 제약(<see cref="AppDbContext.SlugPattern"/>)과 같은 정규식을 쓴다. 글·시리즈가 공유하므로 Features가 아니라 여기에 둔다.</summary>
/// <remarks>Thread-safe(무상태) / Zero-allocation / 즉시 반환.</remarks>
public static partial class SlugRules
{
    // GeneratedRegex: 컴파일 타임에 소스 생성된 매처라 런타임 Regex 구성·JIT 비용이 없고, 이 패턴은 역추적 폭발이 없는 단순 반복이다.
    [GeneratedRegex(AppDbContext.SlugPattern, RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool IsValid(string slug) => slug.Length <= AppDbContext.SlugMax && Pattern().IsMatch(slug);
}
```

```csharp
// PostQueries.cs
using Microsoft.EntityFrameworkCore;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Domain;

namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>글 → DTO 프로젝션. 모든 응답이 같은 조회 경로를 거치므로 "저장 직후 응답"과 "재조회 응답"이 항상 같다.</summary>
/// <remarks>Not Thread-safe(전달된 DbContext 스코프) / 프로젝션 결과만 할당(엔티티 추적 없음) / Non-blocking.
/// 다대다 링크는 DB가 순서를 보장하지 않으므로 태그는 항상 정규화 이름 순으로 정렬한다(클라이언트·테스트가 의존하는 계약).</remarks>
public static class PostQueries
{
    public static async Task<PostDetailDto?> GetDetailAsync(AppDbContext db, Guid id, CancellationToken ct)
    {
        var row = await db.Posts.AsNoTracking().Where(p => p.Id == id)
            .Select(p => new
            {
                p.Id, p.Slug, p.Title, p.Summary, p.ContentMarkdown, p.SeriesId, p.SeriesOrder, p.CreatedAt, p.UpdatedAt, p.Version,
                Tags = p.PostTags.OrderBy(pt => pt.Tag.NormalizedName).Select(pt => pt.Tag.Name).ToArray(),
            })
            .SingleOrDefaultAsync(ct);
        return row is null ? null : new PostDetailDto(row.Id, row.Slug, row.Title, row.Summary, row.ContentMarkdown, row.Tags,
            row.SeriesId, row.SeriesOrder, row.CreatedAt, row.UpdatedAt, row.Version);
    }

    public static async Task<PostSummaryDto[]> ListAsync(IQueryable<Post> query, int skip, int take, CancellationToken ct)
    {
        var rows = await query.AsNoTracking()
            .OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id)
            .Skip(skip).Take(take)
            .Select(p => new
            {
                p.Id, p.Slug, p.Title, p.Summary, p.SeriesId, p.SeriesOrder, p.CreatedAt, p.UpdatedAt, p.Version,
                Tags = p.PostTags.OrderBy(pt => pt.Tag.NormalizedName).Select(pt => pt.Tag.Name).ToArray(),
            })
            .ToListAsync(ct);
        return rows.Select(r => new PostSummaryDto(r.Id, r.Slug, r.Title, r.Summary, r.Tags, r.SeriesId, r.SeriesOrder, r.CreatedAt, r.UpdatedAt, r.Version)).ToArray();
    }
}
```

Run: `dotnet test PortfolioBlog.Api.Tests --filter "FullyQualifiedName~LikePatternTests|FullyQualifiedName~TagResolverTests"`
Expected: 11개 PASS.

- [ ] **Step 4: 실패하는 통합 테스트 작성** — `PortfolioBlog.Api.Tests/Features/PostEndpointsTests.cs`

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Domain;
using PortfolioBlog.Api.Infrastructure.Data;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

[Collection("postgres")]
public sealed class PostEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static UpsertPostRequest Request(string slug, string title = "제목", string content = "본문", string[]? tags = null,
        Guid? seriesId = null, int? seriesOrder = null, uint? version = null, string? summary = "요약") =>
        new(slug, title, summary, content, tags, seriesId, seriesOrder, version);

    private static async Task<PostDetailDto> CreateAsync(HttpClient client, UpsertPostRequest request)
    {
        using var res = await client.PostAsJsonAsync("/api/posts", request);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<PostDetailDto>(TestJson.Options))!;
    }

    private static async Task<Dictionary<string, string[]>> ErrorsAsync(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var problem = await res.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(TestJson.Options);
        return new Dictionary<string, string[]>(problem!.Errors);
    }

    private async Task<Guid> SeedSeriesAsync(string slug)
    {
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var series = new Series { Slug = slug, Title = "시리즈" };
        db.Series.Add(series);
        await db.SaveChangesAsync();
        return series.Id;
    }

    [Fact]
    public async Task Create_Returns201_WithLocation_SortedTags_AndRoundTrips()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        using var res = await client.PostAsJsonAsync("/api/posts", Request("create-ok", tags: ["  Zeta ", "alpha", "ALPHA", "C#"]));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var created = (await res.Content.ReadFromJsonAsync<PostDetailDto>(TestJson.Options))!;
        Assert.Equal($"/api/posts/{created.Id}", res.Headers.Location?.OriginalString);
        Assert.Equal(["alpha", "C#", "Zeta"], created.Tags);            // 정규화 이름 순, 중복("ALPHA")은 먼저 나온 표기로 합쳐진다
        Assert.Equal(created.CreatedAt, created.UpdatedAt);
        Assert.NotEqual(0u, created.Version);

        var fetched = (await client.GetFromJsonAsync<PostDetailDto>($"/api/posts/{created.Id}", TestJson.Options))!;
        Assert.Equal(created.Tags, fetched.Tags);
        Assert.Equal(created, fetched with { Tags = created.Tags });     // record의 배열 멤버는 참조 비교라 Tags를 맞춘 뒤 나머지 전체를 비교한다
    }

    [Fact]
    public async Task Create_InvalidFields_Returns400_WithFieldKeys()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        var tooBig = new string('가', 70_000); // '가'는 UTF-8 3바이트 → 210,000바이트 > 204,800
        using var res = await client.PostAsJsonAsync("/api/posts",
            new UpsertPostRequest("Bad Slug", "   ", new string('s', 301), tooBig, ["a/b"], null, 3, null));

        var errors = await ErrorsAsync(res);
        Assert.Contains("slug", errors.Keys);
        Assert.Contains("title", errors.Keys);
        Assert.Contains("summary", errors.Keys);
        Assert.Contains("contentMarkdown", errors.Keys);
        Assert.Contains("tagNames", errors.Keys);
        Assert.Contains("seriesOrder", errors.Keys); // seriesId 없이 seriesOrder만 있음
    }

    [Fact]
    public async Task Create_MissingRequiredFields_Returns400_NotBindingException()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        using var res = await client.PostAsJsonAsync("/api/posts", new { });
        var errors = await ErrorsAsync(res);
        Assert.Contains("slug", errors.Keys);
        Assert.Contains("title", errors.Keys);
        Assert.Contains("contentMarkdown", errors.Keys);
    }

    [Fact]
    public async Task Create_MalformedJson_Returns400()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        using var res = await client.PostAsync("/api/posts", new StringContent("{not json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Create_UnknownSeries_Returns400_AndSeriesOrderMustBePositive()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        using var unknown = await client.PostAsJsonAsync("/api/posts", Request("unknown-series", seriesId: Guid.NewGuid(), seriesOrder: 1));
        Assert.Contains("seriesId", (await ErrorsAsync(unknown)).Keys);

        var seriesId = await SeedSeriesAsync("series-for-order");
        using var zero = await client.PostAsJsonAsync("/api/posts", Request("zero-order", seriesId: seriesId, seriesOrder: 0));
        Assert.Contains("seriesOrder", (await ErrorsAsync(zero)).Keys);

        var ok = await CreateAsync(client, Request("in-series", seriesId: seriesId, seriesOrder: 2));
        Assert.Equal(seriesId, ok.SeriesId);
        Assert.Equal(2, ok.SeriesOrder);
    }

    [Fact]
    public async Task Create_DuplicateSlug_Returns409()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        await CreateAsync(client, Request("dup"));
        using var res = await client.PostAsJsonAsync("/api/posts", Request("dup"));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Update_ReplacesFieldsAndTags_BumpsVersion_KeepsCreatedAt()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        var created = await CreateAsync(client, Request("update-ok", tags: ["one", "two"]));

        using var res = await client.PutAsJsonAsync($"/api/posts/{created.Id}",
            Request("update-ok", title: "새 제목", content: "새 본문", tags: ["two", "three"], version: created.Version));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var updated = (await res.Content.ReadFromJsonAsync<PostDetailDto>(TestJson.Options))!;

        Assert.Equal("새 제목", updated.Title);
        Assert.Equal("새 본문", updated.ContentMarkdown);
        Assert.Equal(["three", "two"], updated.Tags);
        Assert.Equal(created.CreatedAt, updated.CreatedAt);
        Assert.True(updated.UpdatedAt >= created.UpdatedAt);
        Assert.NotEqual(created.Version, updated.Version);
    }

    [Fact]
    public async Task Update_SlugChange_MissingVersion_Return400_StaleVersion_Returns409()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        var created = await CreateAsync(client, Request("immutable-slug"));

        using var slugChange = await client.PutAsJsonAsync($"/api/posts/{created.Id}", Request("other-slug", version: created.Version));
        Assert.Contains("slug", (await ErrorsAsync(slugChange)).Keys);

        using var noVersion = await client.PutAsJsonAsync($"/api/posts/{created.Id}", Request("immutable-slug"));
        Assert.Contains("version", (await ErrorsAsync(noVersion)).Keys);

        using var first = await client.PutAsJsonAsync($"/api/posts/{created.Id}", Request("immutable-slug", title: "탭 A", version: created.Version));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var stale = await client.PutAsJsonAsync($"/api/posts/{created.Id}", Request("immutable-slug", title: "탭 B", version: created.Version));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var current = await client.GetFromJsonAsync<PostDetailDto>($"/api/posts/{created.Id}", TestJson.Options);
        Assert.Equal("탭 A", current!.Title); // 오래된 탭이 덮어쓰지 못했다
    }

    [Fact]
    public async Task Delete_RequiresCurrentVersion_ThenReturns204_AndKeepsTags()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        var created = await CreateAsync(client, Request("delete-me", tags: ["survivor-tag"]));

        using var noVersion = await client.DeleteAsync($"/api/posts/{created.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, noVersion.StatusCode);
        using var stale = await client.DeleteAsync($"/api/posts/{created.Id}?version={created.Version + 1}");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var ok = await client.DeleteAsync($"/api/posts/{created.Id}?version={created.Version}");
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

        using var gone = await client.GetAsync($"/api/posts/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        using var again = await client.DeleteAsync($"/api/posts/{created.Id}?version={created.Version}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.Tags.AnyAsync(t => t.NormalizedName == "survivor-tag")); // 글 삭제는 링크만 지운다
    }

    [Fact]
    public async Task List_PagesNewestFirst_SearchesTitleSummaryContent_AndTreatsWildcardsLiterally()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        var marker = "lst" + Guid.NewGuid().ToString("N")[..8];
        await CreateAsync(client, Request($"{marker}-1", title: $"{marker} 첫째", content: "abc"));
        await CreateAsync(client, Request($"{marker}-2", title: $"{marker} 둘째", content: "a_c 100% 리터럴"));
        await CreateAsync(client, Request($"{marker}-3", title: $"{marker} 셋째", content: "본문", summary: "특별한요약어"));

        var page = await client.GetFromJsonAsync<PagedPostsDto>($"/api/posts?q={marker}&skip=0&take=2", TestJson.Options);
        Assert.Equal(3, page!.Total);
        Assert.Equal([$"{marker}-3", $"{marker}-2"], page.Items.Select(p => p.Slug)); // 최신순

        var second = await client.GetFromJsonAsync<PagedPostsDto>($"/api/posts?q={marker}&skip=2&take=2", TestJson.Options);
        Assert.Equal([$"{marker}-1"], second!.Items.Select(p => p.Slug));

        var bySummary = await client.GetFromJsonAsync<PagedPostsDto>("/api/posts?q=특별한요약어", TestJson.Options);
        Assert.Equal([$"{marker}-3"], bySummary!.Items.Select(p => p.Slug));

        // "a_c"가 와일드카드였다면 "abc"(-1)도 걸린다. 리터럴로만 매칭되어야 한다.
        var literal = await client.GetFromJsonAsync<PagedPostsDto>($"/api/posts?q={Uri.EscapeDataString("a_c")}", TestJson.Options);
        Assert.Equal([$"{marker}-2"], literal!.Items.Where(p => p.Slug.StartsWith(marker, StringComparison.Ordinal)).Select(p => p.Slug));
        var percent = await client.GetFromJsonAsync<PagedPostsDto>($"/api/posts?q={Uri.EscapeDataString("100%")}", TestJson.Options);
        Assert.Equal([$"{marker}-2"], percent!.Items.Where(p => p.Slug.StartsWith(marker, StringComparison.Ordinal)).Select(p => p.Slug));
    }

    [Theory]
    [InlineData("/api/posts?take=abc")]
    [InlineData("/api/posts?skip=-1")]
    [InlineData("/api/posts?take=0")]
    [InlineData("/api/posts?take=201")]
    public async Task List_InvalidPaging_Returns400(string url)
    {
        using var client = await factory.CreateLoggedInClientAsync();
        using var res = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task List_QueryTooLong_Returns400()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        using var res = await client.GetAsync("/api/posts?q=" + new string('q', 101));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task ConcurrentCreates_WithSameNewTag_BothSucceed_AndTagIsSingle()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        var tag = "race-" + Guid.NewGuid().ToString("N")[..8];

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            client.PostAsJsonAsync("/api/posts", Request($"{tag}-{i}", tags: [tag]))));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        foreach (var r in results) r.Dispose();

        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.Tags.CountAsync(t => t.NormalizedName == tag));
        Assert.Equal(6, await db.PostTags.CountAsync(pt => pt.Tag.NormalizedName == tag));
    }
}
```

Run: `dotnet test PortfolioBlog.Api.Tests --filter "FullyQualifiedName~PostEndpointsTests"`
Expected: 전부 FAIL(404 — 엔드포인트 없음).

- [ ] **Step 5: 검증 구현** — `PortfolioBlog.Api/Features/Posts/PostValidation.cs`

```csharp
using System.Text;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Features.Posts;

/// <summary><see cref="UpsertPostRequest"/>의 형식 검증(DB 조회 없음). 의미 검증(시리즈 존재·slug 불변·version)은 엔드포인트가 한다.</summary>
/// <remarks>Thread-safe(무상태) / 오류가 있을 때만 할당 / 즉시 반환. 본문 크기는 글자 수가 아니라 UTF-8 바이트로 잰다(DB CHECK와 같은 단위).</remarks>
public static class PostValidation
{
    public static ValidationErrors Validate(UpsertPostRequest req)
    {
        var errors = new ValidationErrors();

        if (string.IsNullOrEmpty(req.Slug)) errors.Add("slug", "slug는 필수입니다.");
        else if (!SlugRules.IsValid(req.Slug)) errors.Add("slug", $"slug는 소문자·숫자·하이픈만 쓰고 {AppDbContext.SlugMax}자 이하여야 합니다(예: my-first-post).");

        if (string.IsNullOrWhiteSpace(req.Title)) errors.Add("title", "제목은 비울 수 없습니다.");
        else if (req.Title.Trim().Length > AppDbContext.TitleMax) errors.Add("title", $"제목은 {AppDbContext.TitleMax}자 이하여야 합니다.");

        if ((req.Summary?.Trim().Length ?? 0) > AppDbContext.SummaryMax) errors.Add("summary", $"요약은 {AppDbContext.SummaryMax}자 이하여야 합니다.");

        if (req.ContentMarkdown is null) errors.Add("contentMarkdown", "본문은 필수입니다(빈 문자열은 허용).");
        else if (Encoding.UTF8.GetByteCount(req.ContentMarkdown) > AppDbContext.ContentMaxBytes)
            errors.Add("contentMarkdown", $"본문은 UTF-8 기준 {AppDbContext.ContentMaxBytes / 1024}KB 이하여야 합니다.");

        TagResolver.Validate(req.TagNames, errors, "tagNames");

        if ((req.SeriesId is null) != (req.SeriesOrder is null)) errors.Add("seriesOrder", "seriesId와 seriesOrder는 함께 주거나 함께 생략해야 합니다.");
        else if (req.SeriesOrder is <= 0) errors.Add("seriesOrder", "seriesOrder는 1 이상이어야 합니다.");

        return errors;
    }
}
```

- [ ] **Step 6: 엔드포인트 구현** — `PortfolioBlog.Api/Features/Posts/PostEndpoints.cs`

```csharp
using Microsoft.EntityFrameworkCore;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Domain;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Features.Posts;

/// <summary>글 관리 API. 그룹에 걸린 호스트·IP·CSRF·세션 검사를 통과한 요청만 도달한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 핸들러는 무상태 정적 메서드. 요청마다 스코프된 DbContext를 받는다.</description></item>
/// <item><description><b>Memory Allocation:</b> 목록은 본문 없이 프로젝션한다. 상세·저장은 본문(최대 200KB) 문자열을 1~2개 보유한다.</description></item>
/// <item><description><b>Blocking:</b> 모든 DB I/O는 async. 저장은 "태그 upsert + 글 + 링크"를 한 트랜잭션으로 묶어 부분 공개를 막는다(저장 즉시 공개이므로).</description></item>
/// </list>
/// </remarks>
public static class PostEndpoints
{
    public const int DefaultTake = 50;
    public const int MaxTake = 200;
    public const int MaxQueryLength = 100;

    public static void MapPostEndpoints(this RouteGroupBuilder api)
    {
        var posts = api.MapGroup("/posts");
        posts.MapGet("", ListAsync).WithName("ListPosts");
        posts.MapGet("/{id:guid}", GetAsync).WithName("GetPost");
        posts.MapPost("", CreateAsync).WithName("CreatePost");
        posts.MapPut("/{id:guid}", UpdateAsync).WithName("UpdatePost");
        posts.MapDelete("/{id:guid}", DeleteAsync).WithName("DeletePost");
    }

    private static async Task<IResult> ListAsync(AppDbContext db, string? q, int? skip, int? take, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (skip is < 0) errors.Add("skip", "skip은 0 이상이어야 합니다.");
        if (take is < 1 or > MaxTake) errors.Add("take", $"take는 1~{MaxTake}여야 합니다.");
        var term = q?.Trim();
        if (term?.Length > MaxQueryLength) errors.Add("q", $"검색어는 {MaxQueryLength}자 이하여야 합니다.");
        if (errors.Any) return TypedResults.ValidationProblem(errors.ToDictionary());

        IQueryable<Post> query = db.Posts;
        if (!string.IsNullOrEmpty(term))
        {
            var pattern = LikePattern.Contains(term);
            query = query.Where(p => EF.Functions.ILike(p.Title, pattern, LikePattern.Escape)
                                  || EF.Functions.ILike(p.Summary, pattern, LikePattern.Escape)
                                  || EF.Functions.ILike(p.ContentMarkdown, pattern, LikePattern.Escape));
        }
        var total = await query.CountAsync(ct);
        var items = await PostQueries.ListAsync(query, skip ?? 0, take ?? DefaultTake, ct);
        return TypedResults.Ok(new PagedPostsDto(items, total));
    }

    private static async Task<IResult> GetAsync(Guid id, AppDbContext db, CancellationToken ct) =>
        await PostQueries.GetDetailAsync(db, id, ct) is { } dto ? TypedResults.Ok(dto) : TypedResults.NotFound();

    private static async Task<IResult> CreateAsync(UpsertPostRequest req, AppDbContext db, ILoggerFactory loggers, CancellationToken ct)
    {
        var errors = PostValidation.Validate(req);
        await ValidateSeriesAsync(db, req, errors, ct);
        if (errors.Any) return TypedResults.ValidationProblem(errors.ToDictionary());

        if (await db.Posts.AnyAsync(p => p.Slug == req.Slug, ct)) return DbConflict.Problem($"slug '{req.Slug}'는 이미 쓰이고 있습니다.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var tagIds = await TagResolver.ResolveIdsAsync(db, req.TagNames, ct);
        var now = DbClock.UtcNow();
        var post = new Post
        {
            Slug = req.Slug!, Title = req.Title!.Trim(), Summary = req.Summary?.Trim() ?? string.Empty,
            ContentMarkdown = req.ContentMarkdown!, SeriesId = req.SeriesId, SeriesOrder = req.SeriesOrder,
            CreatedAt = now, UpdatedAt = now,
        };
        foreach (var tagId in tagIds) post.PostTags.Add(new PostTag { TagId = tagId });
        db.Posts.Add(post);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbConflict.IsConstraintRace(ex))
        {
            return DbConflict.Problem("같은 slug가 방금 만들어졌거나 참조한 시리즈·태그가 방금 삭제되었습니다. 다시 조회한 뒤 저장하세요.");
        }
        await tx.CommitAsync(ct);

        loggers.CreateLogger("PortfolioBlog.Api.Audit").LogInformation("글 생성. PostId={PostId} Slug={Slug}", post.Id, post.Slug); // 본문은 기록하지 않는다
        var dto = await PostQueries.GetDetailAsync(db, post.Id, ct);
        return TypedResults.Created($"/api/posts/{post.Id}", dto);
    }

    private static async Task<IResult> UpdateAsync(Guid id, UpsertPostRequest req, AppDbContext db, ILoggerFactory loggers, CancellationToken ct)
    {
        var post = await db.Posts.Include(p => p.PostTags).SingleOrDefaultAsync(p => p.Id == id, ct);
        if (post is null) return TypedResults.NotFound();

        var errors = PostValidation.Validate(req);
        // slug 불변: 공개 URL과 Atom 항목의 안정성을 위해 생성 후에는 바꿀 수 없다(스펙 3.2).
        if (req.Slug is not null && req.Slug != post.Slug) errors.Add("slug", "slug는 생성 후 바꿀 수 없습니다.");
        if (req.Version is null) errors.Add("version", "수정에는 조회 때 받은 version이 필요합니다.");
        await ValidateSeriesAsync(db, req, errors, ct);
        if (errors.Any) return TypedResults.ValidationProblem(errors.ToDictionary());

        if (post.Version != req.Version) return StaleVersion();
        // 읽은 뒤 저장 전까지의 경쟁도 잡도록 UPDATE의 WHERE xmin = ... 비교값을 클라이언트가 본 버전으로 고정한다.
        db.Entry(post).Property(p => p.Version).OriginalValue = req.Version!.Value;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var wanted = await TagResolver.ResolveIdsAsync(db, req.TagNames, ct);
        // 링크는 차집합만 지우고 더한다(전부 지웠다 다시 넣으면 같은 복합 키의 Deleted·Added 엔티티가 추적기에서 충돌한다).
        post.PostTags.RemoveAll(pt => !wanted.Contains(pt.TagId));
        foreach (var tagId in wanted.Where(tagId => post.PostTags.All(pt => pt.TagId != tagId)))
        {
            post.PostTags.Add(new PostTag { PostId = post.Id, TagId = tagId });
        }
        post.Title = req.Title!.Trim();
        post.Summary = req.Summary?.Trim() ?? string.Empty;
        post.ContentMarkdown = req.ContentMarkdown!;
        post.SeriesId = req.SeriesId;
        post.SeriesOrder = req.SeriesOrder;
        post.UpdatedAt = DbClock.UtcNow(); // 항상 바뀌므로 태그만 고쳐도 Posts 행이 갱신되어 xmin 비교가 실행된다
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return StaleVersion();
        }
        catch (DbUpdateException ex) when (DbConflict.IsConstraintRace(ex))
        {
            return DbConflict.Problem("참조한 시리즈·태그가 방금 삭제되었습니다. 다시 조회한 뒤 저장하세요.");
        }
        await tx.CommitAsync(ct);

        loggers.CreateLogger("PortfolioBlog.Api.Audit").LogInformation("글 수정. PostId={PostId} Slug={Slug}", post.Id, post.Slug);
        return TypedResults.Ok(await PostQueries.GetDetailAsync(db, post.Id, ct));
    }

    private static async Task<IResult> DeleteAsync(Guid id, uint? version, AppDbContext db, ILoggerFactory loggers, CancellationToken ct)
    {
        if (version is null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["version"] = ["삭제에는 조회 때 받은 version 쿼리 값이 필요합니다."] });
        }
        var post = await db.Posts.SingleOrDefaultAsync(p => p.Id == id, ct);
        if (post is null) return TypedResults.NotFound();
        if (post.Version != version) return StaleVersion();

        db.Entry(post).Property(p => p.Version).OriginalValue = version.Value;
        db.Posts.Remove(post);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return StaleVersion();
        }
        loggers.CreateLogger("PortfolioBlog.Api.Audit").LogInformation("글 삭제. PostId={PostId} Slug={Slug}", post.Id, post.Slug);
        return TypedResults.NoContent();
    }

    private static async Task ValidateSeriesAsync(AppDbContext db, UpsertPostRequest req, ValidationErrors errors, CancellationToken ct)
    {
        if (req.SeriesId is { } seriesId && !await db.Series.AnyAsync(s => s.Id == seriesId, ct))
        {
            errors.Add("seriesId", "존재하지 않는 시리즈입니다.");
        }
    }

    private static IResult StaleVersion() =>
        DbConflict.Problem("다른 곳에서 이 글이 먼저 수정되었습니다. 최신 내용을 다시 불러온 뒤 저장하세요.");
}
```

`List<PostTag>.RemoveAll`은 EF가 추적하는 컬렉션에서 항목을 빼는 것이며, `SaveChanges`의 `DetectChanges`가 빠진 링크를 Deleted로 표시한다(필수 관계의 고아 삭제).

`Features/ApiEndpoints.cs`: `api.MapAuthEndpoints();` 다음 줄에 `api.MapPostEndpoints();` 추가, using `PortfolioBlog.Api.Features.Posts`.

- [ ] **Step 7: 통과 확인**

Run: `dotnet test PortfolioBlog.slnx`
Expected: 전부 PASS(PostEndpoints 16 포함), 경고 0.
`Update_ReplacesFieldsAndTags...`에서 빠진 태그 링크가 남아 있으면 `RemoveAll` 뒤 고아 삭제가 동작하지 않은 것이다 → `RemoveAll` 대신 `db.PostTags.RemoveRange(post.PostTags.Where(pt => !wanted.Contains(pt.TagId)).ToList())`로 명시 삭제한다.

- [ ] **Step 8: 커밋**

```bash
git add -A
git commit -m "추가: 글 관리 API와 xmin 기반 낙관적 동시성

- 저장 즉시 공개이므로 태그 upsert·글·링크를 한 트랜잭션으로 묶어 부분 공개 차단
- 새 태그는 ON CONFLICT DO NOTHING으로 만들어 동시 저장을 409로 돌려주지 않음
- slug 불변, 수정·삭제는 version 필수(오래된 탭이 덮어쓰지 못함)
- 검색어의 LIKE 메타문자 이스케이프, 목록은 본문 없이 프로젝션

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---
### Task 6: 시리즈 관리 API

**Files:**
- Create: `PortfolioBlog.Api/Contracts/SeriesDtos.cs`, `PortfolioBlog.Api/Features/Series/{SeriesValidation,SeriesEndpoints}.cs`; Modify: `PortfolioBlog.Api/Features/ApiEndpoints.cs`
- Test: `PortfolioBlog.Api.Tests/Features/SeriesEndpointsTests.cs`

**Interfaces:**
- Consumes: `AppDbContext`, `ValidationErrors`, `SlugRules.IsValid`, `DbConflict`, `PostDetailDto`·`UpsertPostRequest`(Task 5), `ApiFactory.CreateLoggedInClientAsync()`.
- Produces:
  - `SeriesDto(Guid Id, string Slug, string Title, string Description, int PostCount)`
  - `SeriesPostDto(Guid Id, string Slug, string Title, int SeriesOrder)`, `SeriesDetailDto(SeriesDto Series, SeriesPostDto[] Posts)`
  - `UpsertSeriesRequest(string? Slug, string? Title, string? Description)`
  - `SeriesEndpoints.MapSeriesEndpoints(this RouteGroupBuilder)`: `GET /api/series`, `GET /api/series/{id:guid}`, `POST /api/series`, `PUT /api/series/{id:guid}`, `DELETE /api/series/{id:guid}`.
- 네임스페이스 주의: 기능 폴더 이름이 도메인 타입 `Series`와 같다. 엔드포인트 파일의 네임스페이스는 `PortfolioBlog.Api.Features.Series`이므로 그 안에서 도메인 타입은 `Domain.Series`로 한정해 쓴다.

- [ ] **Step 1: 실패하는 테스트 작성** — `PortfolioBlog.Api.Tests/Features/SeriesEndpointsTests.cs`

```csharp
using System.Net;
using System.Net.Http.Json;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

[Collection("postgres")]
public sealed class SeriesEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static async Task<SeriesDto> CreateSeriesAsync(HttpClient client, string slug, string title = "연재")
    {
        using var res = await client.PostAsJsonAsync("/api/series", new UpsertSeriesRequest(slug, title, "설명"));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.NotNull(res.Headers.Location);
        return (await res.Content.ReadFromJsonAsync<SeriesDto>(TestJson.Options))!;
    }

    private static async Task<PostDetailDto> CreatePostAsync(HttpClient client, string slug, Guid seriesId, int order)
    {
        using var res = await client.PostAsJsonAsync("/api/posts", new UpsertPostRequest(slug, slug, "", "본문", null, seriesId, order, null));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<PostDetailDto>(TestJson.Options))!;
    }

    [Fact]
    public async Task Create_Get_List_RoundTrip_WithPostCount()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        var series = await CreateSeriesAsync(client, "series-round-trip");
        await CreatePostAsync(client, "srt-post-1", series.Id, 1);

        var list = await client.GetFromJsonAsync<SeriesDto[]>("/api/series", TestJson.Options);
        Assert.Equal(1, list!.Single(s => s.Id == series.Id).PostCount);
    }

    [Fact]
    public async Task Detail_OrdersPostsBySeriesOrder_ThenCreatedAt_AllowingDuplicateOrders()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        var series = await CreateSeriesAsync(client, "series-ordering");
        await CreatePostAsync(client, "so-third", series.Id, 3);
        await CreatePostAsync(client, "so-first", series.Id, 1);
        await CreatePostAsync(client, "so-dup-a", series.Id, 2);
        await CreatePostAsync(client, "so-dup-b", series.Id, 2); // 같은 순서값 허용: 나중에 만든 글이 뒤

        var detail = await client.GetFromJsonAsync<SeriesDetailDto>($"/api/series/{series.Id}", TestJson.Options);
        Assert.Equal(["so-first", "so-dup-a", "so-dup-b", "so-third"], detail!.Posts.Select(p => p.Slug));
    }

    [Fact]
    public async Task Create_Invalid_Returns400_Duplicate_Returns409()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        using var invalid = await client.PostAsJsonAsync("/api/series", new UpsertSeriesRequest("Bad Slug", " ", new string('d', 1001)));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await invalid.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Http.HttpValidationProblemDetails>(TestJson.Options))!.Errors;
        Assert.Contains("slug", errors.Keys);
        Assert.Contains("title", errors.Keys);
        Assert.Contains("description", errors.Keys);

        await CreateSeriesAsync(client, "series-dup");
        using var dup = await client.PostAsJsonAsync("/api/series", new UpsertSeriesRequest("series-dup", "다른 제목", ""));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }

    [Fact]
    public async Task Update_ChangesTitleAndDescription_ButNotSlug()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        var series = await CreateSeriesAsync(client, "series-update");

        using var ok = await client.PutAsJsonAsync($"/api/series/{series.Id}", new UpsertSeriesRequest("series-update", "새 제목", "새 설명"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var updated = (await ok.Content.ReadFromJsonAsync<SeriesDto>(TestJson.Options))!;
        Assert.Equal("새 제목", updated.Title);
        Assert.Equal("새 설명", updated.Description);

        using var slugChange = await client.PutAsJsonAsync($"/api/series/{series.Id}", new UpsertSeriesRequest("series-renamed", "새 제목", ""));
        Assert.Equal(HttpStatusCode.BadRequest, slugChange.StatusCode);

        using var missing = await client.PutAsJsonAsync($"/api/series/{Guid.NewGuid()}", new UpsertSeriesRequest("whatever", "제목", ""));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Delete_KeepsPosts_AndClearsBothSeriesFields()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        var series = await CreateSeriesAsync(client, "series-delete");
        var post = await CreatePostAsync(client, "sd-post", series.Id, 1);

        using var res = await client.DeleteAsync($"/api/series/{series.Id}");
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        var after = await client.GetFromJsonAsync<PostDetailDto>($"/api/posts/{post.Id}", TestJson.Options);
        Assert.Null(after!.SeriesId);
        Assert.Null(after.SeriesOrder);                      // FK SET NULL만 썼다면 CK_Posts_Series_Pair 위반으로 삭제 자체가 실패했을 것이다
        Assert.NotEqual(post.Version, after.Version);        // 글 행이 바뀌었으므로 열려 있던 에디터 탭의 저장은 409가 된다

        using var gone = await client.GetAsync($"/api/series/{series.Id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        using var again = await client.DeleteAsync($"/api/series/{series.Id}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }
}
```

Run: `dotnet test PortfolioBlog.Api.Tests --filter "FullyQualifiedName~SeriesEndpointsTests"`
Expected: 컴파일 오류(`SeriesDto` 없음).

- [ ] **Step 2: Contracts · 검증 구현**

```csharp
// Contracts/SeriesDtos.cs
namespace PortfolioBlog.Api.Contracts;

public sealed record SeriesDto(Guid Id, string Slug, string Title, string Description, int PostCount);
public sealed record SeriesPostDto(Guid Id, string Slug, string Title, int SeriesOrder);
public sealed record SeriesDetailDto(SeriesDto Series, SeriesPostDto[] Posts);
/// <summary>생성·수정 공용 요청. slug는 글과 같은 규칙이며 생성 후 불변이다(공개 URL <c>/series/{slug}</c>).</summary>
public sealed record UpsertSeriesRequest(string? Slug, string? Title, string? Description);
```

```csharp
// Features/Series/SeriesValidation.cs
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Features.Series;

/// <summary><see cref="UpsertSeriesRequest"/>의 형식 검증. Thread-safe(무상태) / 오류 시에만 할당 / 즉시 반환.</summary>
public static class SeriesValidation
{
    public static ValidationErrors Validate(UpsertSeriesRequest req)
    {
        var errors = new ValidationErrors();
        if (string.IsNullOrEmpty(req.Slug)) errors.Add("slug", "slug는 필수입니다.");
        else if (!SlugRules.IsValid(req.Slug)) errors.Add("slug", $"slug는 소문자·숫자·하이픈만 쓰고 {AppDbContext.SlugMax}자 이하여야 합니다.");
        if (string.IsNullOrWhiteSpace(req.Title)) errors.Add("title", "제목은 비울 수 없습니다.");
        else if (req.Title.Trim().Length > AppDbContext.TitleMax) errors.Add("title", $"제목은 {AppDbContext.TitleMax}자 이하여야 합니다.");
        if ((req.Description?.Trim().Length ?? 0) > AppDbContext.SeriesDescriptionMax) errors.Add("description", $"설명은 {AppDbContext.SeriesDescriptionMax}자 이하여야 합니다.");
        return errors;
    }
}
```

- [ ] **Step 3: 엔드포인트 구현** — `PortfolioBlog.Api/Features/Series/SeriesEndpoints.cs`

```csharp
using Microsoft.EntityFrameworkCore;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Features.Series;

/// <summary>시리즈 관리 API.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 핸들러는 무상태 정적 메서드. 요청마다 스코프된 DbContext를 받는다.</description></item>
/// <item><description><b>Memory Allocation:</b> 목록·상세 모두 프로젝션(글 본문을 읽지 않는다).</description></item>
/// <item><description><b>Blocking:</b> 모든 DB I/O는 async. 삭제는 "소속 글의 두 필드 비우기 + 시리즈 삭제"를 한 트랜잭션으로 묶는다.</description></item>
/// </list>
/// </remarks>
public static class SeriesEndpoints
{
    public static void MapSeriesEndpoints(this RouteGroupBuilder api)
    {
        var series = api.MapGroup("/series");
        series.MapGet("", ListAsync).WithName("ListSeries");
        series.MapGet("/{id:guid}", GetAsync).WithName("GetSeries");
        series.MapPost("", CreateAsync).WithName("CreateSeries");
        series.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateSeries");
        series.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteSeries");
    }

    private static IQueryable<SeriesDto> Project(IQueryable<Domain.Series> query) =>
        query.Select(s => new SeriesDto(s.Id, s.Slug, s.Title, s.Description, s.Posts.Count));

    private static async Task<IResult> ListAsync(AppDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await Project(db.Series.AsNoTracking().OrderBy(s => s.Title).ThenBy(s => s.Id)).ToArrayAsync(ct));

    private static async Task<IResult> GetAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
        var series = await Project(db.Series.AsNoTracking().Where(s => s.Id == id)).SingleOrDefaultAsync(ct);
        if (series is null) return TypedResults.NotFound();
        // 순서값 중복을 허용하므로 (SeriesOrder, CreatedAt, Id)로 안정 정렬한다. IX_Posts_SeriesId_SeriesOrder_CreatedAt_Id 인덱스가 이 정렬을 받친다.
        var posts = await db.Posts.AsNoTracking().Where(p => p.SeriesId == id)
            .OrderBy(p => p.SeriesOrder).ThenBy(p => p.CreatedAt).ThenBy(p => p.Id)
            .Select(p => new SeriesPostDto(p.Id, p.Slug, p.Title, p.SeriesOrder!.Value))
            .ToArrayAsync(ct);
        return TypedResults.Ok(new SeriesDetailDto(series, posts));
    }

    private static async Task<IResult> CreateAsync(UpsertSeriesRequest req, AppDbContext db, ILoggerFactory loggers, CancellationToken ct)
    {
        var errors = SeriesValidation.Validate(req);
        if (errors.Any) return TypedResults.ValidationProblem(errors.ToDictionary());
        if (await db.Series.AnyAsync(s => s.Slug == req.Slug, ct)) return DbConflict.Problem($"slug '{req.Slug}'는 이미 쓰이고 있습니다.");

        var series = new Domain.Series { Slug = req.Slug!, Title = req.Title!.Trim(), Description = req.Description?.Trim() ?? string.Empty };
        db.Series.Add(series);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbConflict.IsConstraintRace(ex))
        {
            return DbConflict.Problem("같은 slug의 시리즈가 방금 만들어졌습니다.");
        }
        loggers.CreateLogger("PortfolioBlog.Api.Audit").LogInformation("시리즈 생성. SeriesId={SeriesId} Slug={Slug}", series.Id, series.Slug);
        return TypedResults.Created($"/api/series/{series.Id}", new SeriesDto(series.Id, series.Slug, series.Title, series.Description, 0));
    }

    private static async Task<IResult> UpdateAsync(Guid id, UpsertSeriesRequest req, AppDbContext db, ILoggerFactory loggers, CancellationToken ct)
    {
        var series = await db.Series.SingleOrDefaultAsync(s => s.Id == id, ct);
        if (series is null) return TypedResults.NotFound();

        var errors = SeriesValidation.Validate(req);
        if (req.Slug is not null && req.Slug != series.Slug) errors.Add("slug", "slug는 생성 후 바꿀 수 없습니다.");
        if (errors.Any) return TypedResults.ValidationProblem(errors.ToDictionary());

        series.Title = req.Title!.Trim();
        series.Description = req.Description?.Trim() ?? string.Empty;
        await db.SaveChangesAsync(ct);
        loggers.CreateLogger("PortfolioBlog.Api.Audit").LogInformation("시리즈 수정. SeriesId={SeriesId}", series.Id);
        return TypedResults.Ok(await Project(db.Series.AsNoTracking().Where(s => s.Id == id)).SingleAsync(ct));
    }

    private static async Task<IResult> DeleteAsync(Guid id, AppDbContext db, ILoggerFactory loggers, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // CK_Posts_Series_Pair 때문에 두 필드를 같은 UPDATE에서 함께 비운다. UpdatedAt은 건드리지 않는다(글 내용이 바뀐 게 아니다).
        await db.Posts.Where(p => p.SeriesId == id)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.SeriesId, (Guid?)null).SetProperty(p => p.SeriesOrder, (int?)null), ct);
        var deleted = await db.Series.Where(s => s.Id == id).ExecuteDeleteAsync(ct);
        if (deleted == 0) return TypedResults.NotFound(); // tx는 dispose에서 롤백된다(비운 글이 있었다면 원복)
        await tx.CommitAsync(ct);
        loggers.CreateLogger("PortfolioBlog.Api.Audit").LogInformation("시리즈 삭제. SeriesId={SeriesId}", id);
        return TypedResults.NoContent();
    }
}
```

`Features/ApiEndpoints.cs`: `api.MapPostEndpoints();` 다음 줄에 `api.MapSeriesEndpoints();` 추가, using `PortfolioBlog.Api.Features.Series`. (`ApiEndpoints.cs`는 도메인 타입 `Series`를 쓰지 않으므로 이름 충돌이 없다.)

- [ ] **Step 4: 통과 확인**

Run: `dotnet test PortfolioBlog.slnx`
Expected: 전부 PASS(SeriesEndpoints 5 포함), 경고 0.

- [ ] **Step 5: 커밋**

```bash
git add -A
git commit -m "추가: 시리즈 관리 API

- 순서값 중복을 허용하고 (SeriesOrder, CreatedAt, Id)로 안정 정렬
- 삭제 시 소속 글의 SeriesId·SeriesOrder를 한 트랜잭션에서 함께 비움(CHECK 제약 유지)
- slug 규칙은 Infrastructure의 SlugRules를 글과 공유(Features 간 참조 없음)

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: 태그 API · 전 엔드포인트 접근 매트릭스 · 문서 · CI

**Files:**
- Create: `PortfolioBlog.Api/Contracts/TagDtos.cs`, `PortfolioBlog.Api/Features/Tags/TagEndpoints.cs`; Modify: `PortfolioBlog.Api/Features/ApiEndpoints.cs`
- Modify: `PortfolioBlog.Api/PortfolioBlog.Api.http`, `.github/workflows/ci.yml`, `README.md`, `plan/tech_blog_0920.md`
- Test: `PortfolioBlog.Api.Tests/Features/{TagEndpointsTests,AccessMatrixTests}.cs`

**Interfaces:**
- Consumes: Task 1~6 전부.
- Produces: `TagDto(Guid Id, string Name, string NormalizedName, int PostCount)`, `TagEndpoints.MapTagEndpoints(this RouteGroupBuilder)`: `GET /api/tags`, `DELETE /api/tags/{id:guid}`.

- [ ] **Step 1: 실패하는 테스트 작성**

```csharp
// PortfolioBlog.Api.Tests/Features/TagEndpointsTests.cs
using System.Net;
using System.Net.Http.Json;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

[Collection("postgres")]
public sealed class TagEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task List_ReturnsTagsSortedByNormalizedName_WithPostCounts_AndDeleteRemovesLinksOnly()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        using var a = await client.PostAsJsonAsync("/api/posts", new UpsertPostRequest("tag-post-a", "A", "", "본문", ["Zebra", "apple"], null, null, null));
        using var b = await client.PostAsJsonAsync("/api/posts", new UpsertPostRequest("tag-post-b", "B", "", "본문", ["apple"], null, null, null));
        var postA = (await a.Content.ReadFromJsonAsync<PostDetailDto>(TestJson.Options))!;

        var tags = (await client.GetFromJsonAsync<TagDto[]>("/api/tags", TestJson.Options))!;
        Assert.Equal(["apple", "zebra"], tags.Select(t => t.NormalizedName));
        Assert.Equal("Zebra", tags[1].Name);
        Assert.Equal([2, 1], tags.Select(t => t.PostCount));

        using var deleted = await client.DeleteAsync($"/api/tags/{tags[0].Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var after = await client.GetFromJsonAsync<PostDetailDto>($"/api/posts/{postA.Id}", TestJson.Options);
        Assert.Equal(["Zebra"], after!.Tags);                 // 글은 남고 링크만 사라진다
        using var again = await client.DeleteAsync($"/api/tags/{tags[0].Id}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }
}
```

```csharp
// PortfolioBlog.Api.Tests/Features/AccessMatrixTests.cs
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PortfolioBlog.Api.Infrastructure.Access;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>등록된 **모든** <c>/api</c> 엔드포인트를 라우트 테이블에서 열거해 접근 계약을 검사한다.
/// 새 엔드포인트를 추가하면서 보호를 빠뜨리면(그룹 밖에 매핑, AllowAnonymous 오용) 이 테스트가 잡는다.</summary>
[Collection("postgres")]
public sealed partial class AccessMatrixTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly string[] AnonymousAllowed = ["/api/auth/login", "/api/auth/me"];

    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex RouteParameter();

    private sealed record Target(string Method, string Path, bool AllowsAnonymous);

    private List<Target> Targets()
    {
        using var _ = factory.CreateClient(); // 호스트 기동
        var targets = new List<Target>();
        foreach (var endpoint in factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var raw = endpoint.RoutePattern.RawText ?? string.Empty;
            if (!raw.StartsWith("/api", StringComparison.OrdinalIgnoreCase)) continue;
            var path = RouteParameter().Replace(raw, Guid.Empty.ToString());
            var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                targets.Add(new Target(method, path, anonymous));
            }
        }
        return targets;
    }

    // 일부러 깨진 JSON을 보낸다: 접근 검사가 바인딩보다 앞이면 400이 아니라 401/403/404가 나와야 한다.
    private static HttpRequestMessage Build(Target t) => new(new HttpMethod(t.Method), t.Path)
    {
        Content = t.Method is "POST" or "PUT" ? new StringContent("{broken", Encoding.UTF8, "application/json") : null,
    };

    private static async Task AssertAllAsync(HttpClient client, IEnumerable<Target> targets, HttpStatusCode expected)
    {
        foreach (var t in targets)
        {
            using var req = Build(t);
            using var res = await client.SendAsync(req);
            Assert.True(expected == res.StatusCode, $"{t.Method} {t.Path}: 기대 {(int)expected}, 실제 {(int)res.StatusCode}");
        }
    }

    [Fact]
    public void RouteTable_ContainsExpectedSurface_AndOnlyLoginAndMeAreAnonymous()
    {
        var targets = Targets();
        Assert.True(targets.Count >= 15, $"열거된 /api 엔드포인트가 너무 적다: {targets.Count}");
        Assert.Equal(AnonymousAllowed, targets.Where(t => t.AllowsAnonymous).Select(t => t.Path).Distinct().Order());
    }

    [Fact]
    public async Task OutsiderIp_Gets403_OnEveryEndpoint_BeforeBinding()
    {
        using var client = factory.CreateAdminClient();
        client.DefaultRequestHeaders.Remove(RemoteIpStartupFilter.HeaderName);
        client.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, ApiFactory.OutsiderIp);
        await AssertAllAsync(client, Targets(), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task MissingCsrfHeader_Gets403_OnEveryEndpoint_EvenWithSession()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        client.DefaultRequestHeaders.Remove(AdminSurfaceMiddleware.CsrfHeaderName);
        await AssertAllAsync(client, Targets(), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task MissingOrigin_Gets403_OnEveryUnsafeEndpoint_EvenWithSession()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        client.DefaultRequestHeaders.Remove("Origin");
        await AssertAllAsync(client, Targets().Where(t => t.Method is not ("GET" or "HEAD")), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PublicHost_Gets404_OnEveryEndpoint_EvenFromAllowedIp()
    {
        using var client = factory.CreatePublicClient();
        client.DefaultRequestHeaders.Remove(RemoteIpStartupFilter.HeaderName);
        client.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, ApiFactory.AllowedIp);
        client.DefaultRequestHeaders.Add(AdminSurfaceMiddleware.CsrfHeaderName, AdminSurfaceMiddleware.CsrfHeaderValue);
        client.DefaultRequestHeaders.Add("Origin", ApiFactory.AdminOrigin);
        await AssertAllAsync(client, Targets(), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task NoSession_Gets401_OnEveryProtectedEndpoint_BeforeBinding()
    {
        using var client = factory.CreateAdminClient();
        await AssertAllAsync(client, Targets().Where(t => !t.AllowsAnonymous), HttpStatusCode.Unauthorized);
    }
}
```

Run: `dotnet test PortfolioBlog.Api.Tests --filter "FullyQualifiedName~TagEndpointsTests|FullyQualifiedName~AccessMatrixTests"`
Expected: `TagEndpointsTests` 컴파일 오류(`TagDto` 없음).

- [ ] **Step 2: 태그 API 구현**

```csharp
// Contracts/TagDtos.cs
namespace PortfolioBlog.Api.Contracts;

/// <summary><c>NormalizedName</c>은 공개 URL <c>/tags/{tag}</c>의 키다(Plan 2).</summary>
public sealed record TagDto(Guid Id, string Name, string NormalizedName, int PostCount);
```

```csharp
// Features/Tags/TagEndpoints.cs
using Microsoft.EntityFrameworkCore;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Features.Tags;

/// <summary>태그 조회·삭제. 생성은 글 저장 시 이름으로 자동이라 별도 엔드포인트가 없다.</summary>
/// <remarks>Thread Safety: 무상태 정적 핸들러 / Memory: 프로젝션만 / Blocking: async DB I/O.
/// 삭제는 FK cascade로 <c>PostTags</c> 링크만 함께 지운다. 글 행은 바뀌지 않으므로 글의 version도 그대로다.</remarks>
public static class TagEndpoints
{
    public static void MapTagEndpoints(this RouteGroupBuilder api)
    {
        var tags = api.MapGroup("/tags");
        tags.MapGet("", async (AppDbContext db, CancellationToken ct) => TypedResults.Ok(
            await db.Tags.AsNoTracking().OrderBy(t => t.NormalizedName)
                .Select(t => new TagDto(t.Id, t.Name, t.NormalizedName, t.PostTags.Count)).ToArrayAsync(ct)))
            .WithName("ListTags");
        tags.MapDelete("/{id:guid}", async (Guid id, AppDbContext db, ILoggerFactory loggers, CancellationToken ct) =>
        {
            if (await db.Tags.Where(t => t.Id == id).ExecuteDeleteAsync(ct) == 0) return (IResult)TypedResults.NotFound();
            loggers.CreateLogger("PortfolioBlog.Api.Audit").LogInformation("태그 삭제. TagId={TagId}", id);
            return TypedResults.NoContent();
        }).WithName("DeleteTag");
    }
}
```

`Features/ApiEndpoints.cs`: `api.MapSeriesEndpoints();` 다음 줄에 `api.MapTagEndpoints();` 추가, using `PortfolioBlog.Api.Features.Tags`.

- [ ] **Step 3: 통과 확인**

Run: `dotnet test PortfolioBlog.slnx`
Expected: 전부 PASS, 경고 0. `AccessMatrixTests`가 특정 엔드포인트에서 400을 받으면 그 엔드포인트가 `/api` 그룹(`MapApiEndpoints`가 돌려준 `RouteGroupBuilder`) 밖에 매핑됐거나 미들웨어 순서가 Global Constraints와 다른 것이다.

- [ ] **Step 4: `.http` 예시 갱신** — `PortfolioBlog.Api/PortfolioBlog.Api.http`를 다음으로 교체(비밀번호는 넣지 않는다. 실행 시 직접 채운다):

```
@Host = https://localhost:7198
@Password = (여기에 로컬 비밀번호 — 커밋 금지)

### 상태
GET {{Host}}/health

### 로그인 (쿠키는 REST 클라이언트가 보관)
POST {{Host}}/api/auth/login
Content-Type: application/json
X-Requested-With: XMLHttpRequest
Origin: {{Host}}

{ "password": "{{Password}}" }

### 글 생성
POST {{Host}}/api/posts
Content-Type: application/json
X-Requested-With: XMLHttpRequest
Origin: {{Host}}

{ "slug": "hello-world", "title": "첫 글", "summary": "요약", "contentMarkdown": "# 안녕", "tagNames": ["dotnet"] }

### 글 목록
GET {{Host}}/api/posts?take=10
X-Requested-With: XMLHttpRequest
```

- [ ] **Step 5: CI를 Linux로 전환** — `.github/workflows/ci.yml`의 `runs-on: windows-latest`를 `runs-on: ubuntu-latest`로 바꾼다. 이유: Testcontainers는 Linux 컨테이너가 필요한데 GitHub의 Windows 러너는 Linux 컨테이너를 돌리지 못한다. `ubuntu-latest`에는 Docker가 기본 설치되어 있어 다른 변경은 필요 없다.

- [ ] **Step 6: 문서 갱신**

- `README.md` 로드맵 표의 1단계 행 상태를 `완료`로 바꾸고, "시작하기" 절 끝에 아래 "로컬 실행" 절의 내용을 요약해 넣는다. "현재 상태" 배너를 "1단계(관리 API·접근 제어) 완료, 공개 페이지·에디터는 구현 전"으로 고친다.
- `plan/tech_blog_0920.md` 8절 표의 Plan 1 행을 `docs/superpowers/plans/2026-09-20-tech-blog-backend-core.md` · "완료"로 바꾼다.
- `CLAUDE.md`·`AGENTS.md`는 프로젝트 구성이 바뀌지 않았으므로 수정하지 않는다.

- [ ] **Step 7: 전체 회귀 + 하네스 감사**

```bash
dotnet build PortfolioBlog.slnx -c Release
dotnet test PortfolioBlog.slnx -c Release
pwsh scripts/harness-audit.ps1
```
Expected: 빌드 경고 0·오류 0, 테스트 전부 PASS, 하네스 감사 8/8.

- [ ] **Step 8: 커밋**

```bash
git add -A
git commit -m "추가: 태그 API와 전 엔드포인트 접근 매트릭스 테스트

- 라우트 테이블을 열거해 모든 /api 엔드포인트의 403·404·401을 바인딩 전 단계에서 검증
- 익명 허용은 login·me뿐임을 테스트로 고정(새 엔드포인트의 보호 누락 방지)
- CI를 ubuntu-latest로 전환(Testcontainers는 Linux 컨테이너 필요)

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## 로컬 실행 (API)

```bash
# 1) 개발용 Postgres (appsettings.Development.json의 연결 문자열과 일치)
docker run -d --name blog-dev-pg -e POSTGRES_PASSWORD=changeme -e POSTGRES_DB=blog_dev -p 5432:5432 postgres:17-alpine

# 2) 관리자 비밀번호 해시를 user-secrets에 저장(저장소에 남지 않는다)
dotnet user-secrets init --project PortfolioBlog.Api
dotnet run --project PortfolioBlog.Api -- hash-password        # 에코 없이 입력 → 해시 한 줄 출력
dotnet user-secrets set "Admin:PasswordHash" "<출력된 해시>" --project PortfolioBlog.Api

# 3) 실행 (https 프로필: Secure 쿠키 때문에 https가 필요하다)
dotnet dev-certs https --trust
dotnet run --project PortfolioBlog.Api --launch-profile https
```

`dotnet user-secrets init`은 csproj에 `UserSecretsId`를 추가한다. 이 변경은 커밋해도 된다(ID일 뿐 비밀값이 아니다).

## 이 계획이 다루지 않는 것 (후속 계획)

| 스펙 항목 | 계획 |
|---|---|
| 마크다운 파이프라인·`/api/preview`·첨부(EXIF 제거)·공개 Razor 페이지·검색·Atom·sitemap | Plan 2 |
| 공개·첨부 응답의 CSP 등 보안 헤더, 공개 페이지·검색 속도 제한, `statement_timeout` | Plan 2 |
| 관리 API JSON 본문 256KB 상한 | Plan 4 — Kestrel `MaxRequestBodySize`와 Caddy `request_body`로 건다(TestServer는 본문 크기 제한을 강제하지 않아 이 계획의 테스트로는 검증할 수 없다). 1단계에서는 필드별 상한(본문 200KB·태그 20개 등)이 실질 방어다 |
| `AllowedHosts`를 실제 도메인으로 제한, `dpkeys` 볼륨·권한, 배포 후 원본 IP 확인 | Plan 4 |

## Self-Review 결과

**스펙 대비 점검 (1단계 범위)**

| 스펙 | 구현 Task |
|---|---|
| 3.2 테이블·제약·인덱스(Attachment 제외 — Plan 2) | Task 1 (`DatabaseSchemaTests`) |
| 3.2 slug 불변·직접 입력, 하드 삭제, 요청 시 렌더링(HTML 미저장) | Task 5·6 |
| 3.2 `xmin` 동시성·`version` 409, 태그 `ON CONFLICT DO NOTHING`, 한 트랜잭션 공개 | Task 5 |
| 3.2 시리즈 삭제 트랜잭션, 순서 중복 허용·안정 정렬 | Task 6 |
| 3.3 접근 계약표(호스트·IP·CSRF 헤더·Origin·세션), 본문 읽기 전 거부 | Task 3·4, Task 7(`AccessMatrixTests`) |
| 3.3 CIDR 공백 구분·fail-fast·IPv4-mapped, 신뢰 프록시 = 고정 IP 하나, Production 필수 설정 | Task 2·3 |
| 3.3 비밀번호 전용 로그인, 내장 PBKDF2, `hash-password` CLI, `__Host-` 쿠키, 절대 12시간, 지문·epoch 폐기 | Task 4 |
| 3.3 Data Protection 키 경로·애플리케이션 이름 | Task 4(경로 설정 지원), 볼륨·권한은 Plan 4 |
| 3.4 관리 API: auth·posts·series·tags, 응답 코드 400/401/403/404/409/429 | Task 4~7 |
| 3.7 로그인 속도 제한(IP별·전역·동시 실행), 영구 잠금 없음 | Task 4 |
| 2.6 감사 로그(로그인 실패·관리 변경, 비밀번호·본문 제외) | Task 4~7 (`PortfolioBlog.Api.Auth`·`.Audit` 로거) |

**스펙에서 바꾼 점 (스펙 문서에도 반영함)**

1. **미들웨어 순서:** 스펙 3.3은 "속도 제한 → 관리 표면 미들웨어"였으나 **관리 표면 미들웨어 → 속도 제한**으로 바꿨다. 속도 제한이 앞이면 허용 IP 밖의 요청이 로그인 전역 한도를 소진해 작성자의 로그인을 막을 수 있다(`Login_FromOutsiderIp_..._DoesNotConsumeRateLimit` 테스트로 고정).
2. **`UpsertPostRequest`의 필수 필드를 nullable로:** 누락을 바인딩 예외(본문 없는 400)가 아니라 필드별 검증 오류로 돌려주기 위해서다.
3. **`Attachment` 테이블은 이 계획의 마이그레이션에 넣지 않는다:** 첨부는 2단계 기능이라 Plan 2가 자기 마이그레이션으로 추가한다(쓰지 않는 테이블을 먼저 만들지 않는다).

**자리표시자·타입 일관성:** "TBD/TODO" 없음. `PostQueries.ListAsync`·`TagResolver.ResolveIdsAsync`·`DbConflict.Problem`·`AuthServiceCollectionExtensions.{Scheme,CookieName,PolicyName}`·`AdminSurfaceMiddleware.{CsrfHeaderName,CsrfHeaderValue}`·`ApiFactory.{AdminOrigin,PublicOrigin,AllowedIp,OutsiderIp,Password,Clock}`는 정의한 Task와 사용하는 Task에서 이름·시그니처가 같다.

**사전 스파이크로 확인한 가정(2026-09-20, .NET SDK 10.0.303 + Npgsql EF 10.0.3):** `PasswordHasher<T>`는 `Microsoft.NET.Sdk.Web`에서 추가 패키지 없이 쓸 수 있다. `uint` + `IsRowVersion()`은 컬럼을 만들지 않고 `xmin`에 매핑된다. `HasCheckConstraint`·`IsDescending`·`HasData`가 기대한 DDL을 낸다. `PartitionedRateLimiter.CreateChained` + `AddOptions<RateLimiterOptions>().Configure<T>()` 조합이 컴파일된다.
