# PARA 노트앱 백엔드 API 구현 계획 (Plan 1/4)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** PostgreSQL 위에 PARA 항목·노트·할 일·태그·첨부·대시보드 REST API와 IP 화이트리스트 쓰기 제어를 갖춘 `WebProject.Api`를 완성한다(스펙 0~2단계).

**Architecture:** 단일 ASP.NET Core 10 최소 API 프로젝트를 기능 폴더(`Features/*`)로 나누고, `Domain`(엔티티) ← `Contracts`(DTO) ← `Infrastructure`(EF Core·접근 제어·파일 저장) ← `Features`(엔드포인트) 방향으로만 의존한다. 쓰기 엔드포인트는 `/api` 쓰기 그룹에 엔드포인트 필터로 `IWriteAccessPolicy`를 강제한다. 테스트는 Testcontainers로 띄운 실제 PostgreSQL에 대해 `WebApplicationFactory<Program>`로 실행한다.

**Tech Stack:** .NET SDK 10.0.303, ASP.NET Core Minimal API, EF Core 10 + `Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3`, `Microsoft.EntityFrameworkCore.Design 10.0.12`, xUnit 2.9.3, `Testcontainers.PostgreSql 4.15.0`, PostgreSQL 17(도커 이미지 `postgres:17-alpine`).

**Spec:** `plan/para_notes_0917.md` (승인됨). 후속 계획: Plan 2 SPA(`WebProject.Web`), Plan 3 노션 가져오기/내보내기(`Features/Notion`), Plan 4 Docker·Caddy·CI.

## Global Constraints

- 대상 프레임워크 `net10.0`, `Nullable=enable`, `ImplicitUsings=enable`. 빌드는 **경고 0·오류 0**을 유지한다(`dotnet build -warnaserror`는 쓰지 않되 새 경고를 남기지 않는다).
- 네임스페이스: 새 코드는 전부 `WebProject.Api.*` 아래(`WebProject.Api.Domain`, `.Contracts`, `.Infrastructure.Data`, `.Infrastructure.Access`, `.Infrastructure.Storage`, `.Features.<이름>`). 테스트는 `WebProject.Api.Tests.*`.
- 의존 방향: `Features → Infrastructure → Contracts → Domain`. `Features` 간 직접 참조 금지(공유 로직은 `Infrastructure`, 공유 DTO는 `Contracts`).
- **주석 규칙(CLAUDE.md, 필수):** 모든 public 타입·메서드·인터페이스에 한국어 XML 문서 주석을 달고 `<remarks>`에 `<b>[성능 및 동시성 제약 조건]</b>` 목록으로 **Thread Safety / Memory Allocation / Blocking** 3항목을 반드시 기재한다. 메모리·네트워크 관련 타입(`Stream`, `ArrayPool`, `SemaphoreSlim`, `Channel<T>`, `IncrementalHash`, `HttpClient` 등) 선언부에는 "왜 이 타입인가"를 **내부 동작 근거**로 인라인 `//` 주석을 단다. 이 계획의 코드 블록은 분량상 대표 주석만 보이지만, **구현 시 모든 public 멤버에 아래 템플릿을 채워 넣는다.**

```csharp
/// <summary>(한 문장 역할)</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> (Thread-safe / Not Thread-safe + 이유, 콜백이면 실행 스레드 컨텍스트)</description></item>
/// <item><description><b>Memory Allocation:</b> (힙 할당 여부·규모, 버퍼 소유권·생명주기)</description></item>
/// <item><description><b>Blocking:</b> (즉시 반환 / 동기 블로킹 / 비동기 Non-blocking)</description></item>
/// </list>
/// </remarks>
```

- 시각은 항상 `DateTimeOffset.UtcNow`(Npgsql `timestamptz`는 오프셋 0만 허용). ID는 `Guid.CreateVersion7()`.
- JSON: enum은 문자열만(`JsonStringEnumConverter(allowIntegerValues: false)`, 정수 입력은 400), 속성명 camelCase(기본값). 오류 응답은 전부 `ProblemDetails`. 바인딩 실패는 예외 대신 400(`RouteHandlerOptions.ThrowOnBadRequest=false`).
- 모든 쓰기 요청(POST/PUT/DELETE)은 `X-Requested-With: XMLHttpRequest` 헤더가 필수다(CSRF 방어, 없으면 403). 테스트 클라이언트는 `ApiFactory.CreateClient()`가 자동으로 붙인다.
- Codex 교차 검증(2026-09-17) 반영 사항은 문서 끝 "Codex 교차 검증 반영" 절 참조.
- 커밋 메시지: `.git/hooks/commit-msg`가 `{접두사}: {제목}` 형식을 강제한다(접두사: 추가|수정|버그수정|리팩토링|문서|테스트|의존성). 각 커밋 끝에 `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>` 줄을 넣는다.
- 테스트 실행 전 **Docker Desktop을 실행**해 둔다(Testcontainers가 `npipe:////./pipe/dockerDesktopLinuxEngine`에 연결). 실행 명령: `dotnet test WebProject.sln`. 특정 테스트만: `dotnet test WebProject.Api.Tests --filter "FullyQualifiedName~<클래스명>"`.
- 절대 경로 하드코딩 금지. 저장소 루트 상대 경로만 사용.

---

## 파일 구조 (이 계획이 만드는/바꾸는 파일)

```
WebProject.Api/
  Program.cs                                   # 수정: 서비스 등록 + Migrate + MapApiEndpoints
  WebProject.Api.csproj                        # 수정: EF Core·Npgsql 패키지
  appsettings.json / appsettings.Development.json  # 수정: ConnectionStrings, WriteAccess, Attachments, ForwardedHeaders
  Domain/ItemKind.cs, ItemStatus.cs, Item.cs, Note.cs, NoteItemLink.cs, TaskItem.cs, Tag.cs, ItemTag.cs, NoteTag.cs, Attachment.cs
  Contracts/ItemDtos.cs, NoteDtos.cs, TaskDtos.cs, TagDtos.cs, AttachmentDtos.cs, DashboardDtos.cs, MeDto.cs, ValidationErrors.cs
  Infrastructure/Data/AppDbContext.cs, TagResolver.cs, DtoMapping.cs, UniqueViolation.cs, Migrations/*
  Infrastructure/Access/IWriteAccessPolicy.cs, WriteAccessOptions.cs, IpAllowlistWriteAccessPolicy.cs, RequireWriteAccessFilter.cs, WriteAccessServiceCollectionExtensions.cs
  Infrastructure/Storage/IAttachmentStore.cs, AttachmentOptions.cs, FileSystemAttachmentStore.cs, ImageSignature.cs
  Features/ApiEndpoints.cs
  Features/Me/MeEndpoints.cs
  Features/Items/ItemEndpoints.cs, ItemValidation.cs
  Features/Tags/TagEndpoints.cs
  Features/Notes/NoteEndpoints.cs, NoteValidation.cs
  Features/Tasks/TaskEndpoints.cs
  Features/Attachments/AttachmentEndpoints.cs
  Features/Dashboard/DashboardEndpoints.cs
WebProject.Api.Tests/
  WebProject.Api.Tests.csproj                  # 수정: Testcontainers.PostgreSql
  HealthEndpointTests.cs                       # 유지
  WeatherForecast*.cs (3파일)                  # 삭제
  Infrastructure/PostgresContainerFixture.cs, ApiFactory.cs, FakeWriteAccessPolicy.cs, RemoteIpStartupFilter.cs, TestJson.cs
  Infrastructure/IpAllowlistWriteAccessPolicyTests.cs, FileSystemAttachmentStoreTests.cs, ImageSignatureTests.cs
  Features/MeEndpointsTests.cs, ForwardedHeadersTests.cs, ItemEndpointsTests.cs, TagEndpointsTests.cs, NoteEndpointsTests.cs, TaskEndpointsTests.cs, AttachmentEndpointsTests.cs, DashboardEndpointsTests.cs
.github/workflows/ci.yml                       # 수정(Task 10): ubuntu-latest
```

---

### Task 1: 템플릿 잔재 제거 + JSON/ProblemDetails 기본 설정

**Files:**
- Modify: `WebProject.Api/Program.cs` (전체 교체)
- Delete: `WebProject.Api.Tests/WeatherForecastTests.cs`, `WebProject.Api.Tests/WeatherForecastRangeTests.cs`, `WebProject.Api.Tests/WeatherForecastEndpointTests.cs`
- Test: `WebProject.Api.Tests/HealthEndpointTests.cs` (기존, 변경 없음)

**Interfaces:**
- Produces: `HealthResponse(string Status, DateTimeOffset GeneratedAt)` 유지, `public partial class Program` 유지, `/health` 이름 `GetHealth` 유지.

- [ ] **Step 1: 템플릿 테스트 3파일 삭제**

```bash
git rm WebProject.Api.Tests/WeatherForecastTests.cs WebProject.Api.Tests/WeatherForecastRangeTests.cs WebProject.Api.Tests/WeatherForecastEndpointTests.cs
```

- [ ] **Step 2: Program.cs를 다음으로 교체** (`WeatherForecast` 레코드·`/weatherforecast`·`UseHttpsRedirection` 제거. HTTPS는 Caddy가 종료하므로 API는 HTTP만 서빙한다.)

```csharp
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
// ProblemDetails: 400/403/404/413 등 모든 오류 응답을 RFC 9457 형식으로 통일한다.
builder.Services.AddProblemDetails();
// enum을 문자열("Project")로만 주고받는다. allowIntegerValues:false 로 "Kind": 99 같은 미정의 정수 입력을 역직렬화 단계에서 거부한다.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
// 바인딩 실패(잘못된 enum 문자열·JSON 파싱 오류 등)를 예외(Development 기본값)가 아니라 항상 400 으로 응답한다.
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = false);

var app = builder.Build();

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

/// <summary>/health 응답 계약.</summary>
/// <param name="Status">서비스 상태 문자열(항상 "Healthy")</param>
/// <param name="GeneratedAt">응답 생성 시각(UTC, 오프셋 0)</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 불변 record 이며 공유 가변 상태가 없다.</description></item>
/// <item><description><b>Memory Allocation:</b> 요청당 record 인스턴스 1개 힙 할당. <paramref name="Status"/> 는 상수 문자열 인터닝으로 추가 할당 없음.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking).</description></item>
/// </list>
/// </remarks>
public record HealthResponse(string Status, DateTimeOffset GeneratedAt);

/// <summary>통합 테스트(<c>WebApplicationFactory&lt;Program&gt;</c>)가 진입점을 참조할 수 있도록 노출한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 상태 없음.</description></item>
/// <item><description><b>Memory Allocation:</b> 없음.</description></item>
/// <item><description><b>Blocking:</b> 해당 없음.</description></item>
/// </list>
/// </remarks>
public partial class Program { }
```

- [ ] **Step 3: 빌드·테스트로 회귀 확인**

Run: `dotnet test WebProject.sln`
Expected: 빌드 경고 0, `HealthEndpointTests` 2개 PASS, 실패 0.

- [ ] **Step 4: 커밋**

```bash
git add -A WebProject.Api WebProject.Api.Tests
git commit -m "리팩토링: 템플릿 잔재 제거 및 ProblemDetails·enum 문자열 JSON 기본 설정

- /weatherforecast 엔드포인트와 테스트 3파일 삭제(PARA 노트앱과 무관)
- HTTPS 리다이렉션 제거(TLS는 Caddy가 종료)

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: 도메인 엔티티 · AppDbContext · 초기 마이그레이션 · 테스트 DB 픽스처

**Files:**
- Modify: `WebProject.Api/WebProject.Api.csproj`, `WebProject.Api/Program.cs`, `WebProject.Api/appsettings.json`, `WebProject.Api/appsettings.Development.json`
- Create: `WebProject.Api/Domain/*.cs` (10파일), `WebProject.Api/Infrastructure/Data/AppDbContext.cs`, `WebProject.Api/Infrastructure/Data/Migrations/*`(생성)
- Modify: `WebProject.Api.Tests/WebProject.Api.Tests.csproj`
- Create: `WebProject.Api.Tests/Infrastructure/PostgresContainerFixture.cs`, `ApiFactory.cs`, `FakeWriteAccessPolicy.cs`(Task 3에서 인터페이스 생김 → 여기서는 빈 파일 만들지 말고 Task 3에서 생성), `TestJson.cs`
- Test: `WebProject.Api.Tests/Infrastructure/DatabaseMigrationTests.cs`

**Interfaces:**
- Produces: `WebProject.Api.Domain.{ItemKind, ItemStatus, Item, Note, NoteItemLink, TaskItem, Tag, ItemTag, NoteTag, Attachment}`, `WebProject.Api.Infrastructure.Data.AppDbContext` (DbSet `Items, Notes, NoteItemLinks, Tasks, Tags, ItemTags, NoteTags, Attachments`), 테스트 `ApiFactory`(`CreateClient()`, `CreateScope()`, 설정 오버라이드 생성자), `PostgresContainerFixture`, 컬렉션 이름 `"postgres"`.

- [ ] **Step 1: 패키지 추가**

```bash
dotnet add WebProject.Api package Npgsql.EntityFrameworkCore.PostgreSQL --version 10.0.3
dotnet add WebProject.Api package Microsoft.EntityFrameworkCore.Design --version 10.0.12
dotnet add WebProject.Api.Tests package Testcontainers.PostgreSql --version 4.15.0
dotnet tool install -g dotnet-ef --version 10.0.12
```
(`dotnet-ef`가 이미 있으면 `dotnet tool update -g dotnet-ef --version 10.0.12`.)

- [ ] **Step 2: 도메인 파일 작성** — `WebProject.Api/Domain/` 아래. 각 파일에 Global Constraints의 XML 주석 템플릿을 적용한다(엔티티: Thread Safety = Not Thread-safe, EF 변경 추적 단위 스코프 내 단일 스레드 사용 / Memory = 인스턴스당 힙 1개 + 컬렉션 지연 할당 / Blocking = 즉시 반환).

```csharp
// Domain/ItemKind.cs
namespace WebProject.Api.Domain;
/// <summary>PARA 카테고리. Archive는 보관 상태를 나타내는 4번째 종류다.</summary>
public enum ItemKind { Project = 0, Area = 1, Resource = 2, Archive = 3 }

// Domain/ItemStatus.cs
namespace WebProject.Api.Domain;
/// <summary>항목 진행 상태. 모든 Kind에 적용된다.</summary>
public enum ItemStatus { Planned = 0, Active = 1, OnHold = 2, Done = 3 }

// Domain/Item.cs
namespace WebProject.Api.Domain;
public sealed class Item
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public ItemKind Kind { get; set; }
    /// <summary>Kind가 Archive일 때 복원 대상 종류. 그 외에는 null.</summary>
    public ItemKind? PreviousKind { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ItemStatus Status { get; set; } = ItemStatus.Planned;
    public DateOnly? DueDate { get; set; }
    /// <summary>소속 영역(Kind=Area인 Item). 영역 자신은 null.</summary>
    public Guid? AreaId { get; set; }
    public Item? Area { get; set; }
    /// <summary>노션 페이지 ID(32 hex). 재가져오기 멱등성 키.</summary>
    public string? NotionId { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<NoteItemLink> NoteLinks { get; } = new();
    public List<TaskItem> Tasks { get; } = new();
    public List<ItemTag> ItemTags { get; } = new();
}

// Domain/Note.cs
namespace WebProject.Api.Domain;
public sealed class Note
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Title { get; set; } = string.Empty;
    public string ContentMarkdown { get; set; } = string.Empty;
    public string? NotionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<NoteItemLink> ItemLinks { get; } = new();
    public List<NoteTag> NoteTags { get; } = new();
}

// Domain/NoteItemLink.cs
namespace WebProject.Api.Domain;
public sealed class NoteItemLink
{
    public Guid NoteId { get; set; }
    public Note Note { get; set; } = null!;
    public Guid ItemId { get; set; }
    public Item Item { get; set; } = null!;
}

// Domain/TaskItem.cs
namespace WebProject.Api.Domain;
public sealed class TaskItem
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Title { get; set; } = string.Empty;
    public bool IsDone { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateOnly? DueDate { get; set; }
    /// <summary>소속 항목. null이면 독립 할 일.</summary>
    public Guid? ItemId { get; set; }
    public Item? Item { get; set; }
    public int SortOrder { get; set; }
    public string? NotionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// Domain/Tag.cs
namespace WebProject.Api.Domain;
public sealed class Tag
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    /// <summary>표시용 이름(원문 대소문자 유지).</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>소문자·공백 정규화 이름. 유일 인덱스 대상.</summary>
    public string NormalizedName { get; set; } = string.Empty;
    public string? Color { get; set; }
    public List<ItemTag> ItemTags { get; } = new();
    public List<NoteTag> NoteTags { get; } = new();
}

// Domain/ItemTag.cs
namespace WebProject.Api.Domain;
public sealed class ItemTag
{
    public Guid ItemId { get; set; }
    public Item Item { get; set; } = null!;
    public Guid TagId { get; set; }
    public Tag Tag { get; set; } = null!;
}

// Domain/NoteTag.cs
namespace WebProject.Api.Domain;
public sealed class NoteTag
{
    public Guid NoteId { get; set; }
    public Note Note { get; set; } = null!;
    public Guid TagId { get; set; }
    public Tag Tag { get; set; } = null!;
}

// Domain/Attachment.cs
namespace WebProject.Api.Domain;
public sealed class Attachment
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    /// <summary>저장소 루트 기준 상대 경로(예: ab/&lt;sha256&gt;.png, 내용 해시 기반).</summary>
    public string StoragePath { get; set; } = string.Empty;
    /// <summary>내용 해시(소문자 hex 64자). 같은 해시는 재사용한다.</summary>
    public string Sha256 { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
```

- [ ] **Step 3: AppDbContext 작성** — `WebProject.Api/Infrastructure/Data/AppDbContext.cs`

```csharp
using Microsoft.EntityFrameworkCore;
using WebProject.Api.Domain;

namespace WebProject.Api.Infrastructure.Data;

/// <summary>PARA 노트앱의 EF Core DbContext.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. 요청 스코프당 1개 인스턴스이며 동시 사용 금지.</description></item>
/// <item><description><b>Memory Allocation:</b> 변경 추적기가 로드한 엔티티 그래프를 스코프 종료까지 보유한다. 읽기 전용 조회는 <c>AsNoTracking()</c>을 쓴다.</description></item>
/// <item><description><b>Blocking:</b> 모든 I/O는 async API로 Non-blocking. 동기 <c>SaveChanges()</c> 사용 금지.</description></item>
/// </list>
/// </remarks>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Item> Items => Set<Item>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<NoteItemLink> NoteItemLinks => Set<NoteItemLink>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<ItemTag> ItemTags => Set<ItemTag>();
    public DbSet<NoteTag> NoteTags => Set<NoteTag>();
    public DbSet<Attachment> Attachments => Set<Attachment>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Item>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.NotionId).HasMaxLength(32);
            e.HasIndex(x => x.NotionId).IsUnique();
            e.HasIndex(x => new { x.Kind, x.SortOrder });
            // 영역 삭제 시 소속 항목의 AreaId만 null로 (항목은 남김)
            e.HasOne(x => x.Area).WithMany().HasForeignKey(x => x.AreaId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<Note>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.NotionId).HasMaxLength(32);
            e.HasIndex(x => x.NotionId).IsUnique();
            e.HasIndex(x => x.UpdatedAt);
        });
        b.Entity<NoteItemLink>(e =>
        {
            e.HasKey(x => new { x.NoteId, x.ItemId });
            e.HasOne(x => x.Note).WithMany(n => n.ItemLinks).HasForeignKey(x => x.NoteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Item).WithMany(i => i.NoteLinks).HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<TaskItem>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.NotionId).HasMaxLength(32);
            e.HasIndex(x => x.NotionId).IsUnique();
            e.HasOne(x => x.Item).WithMany(i => i.Tasks).HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Tag>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(50);
            e.Property(x => x.NormalizedName).HasMaxLength(50);
            e.Property(x => x.Color).HasMaxLength(16);
            e.HasIndex(x => x.NormalizedName).IsUnique();
        });
        b.Entity<ItemTag>(e =>
        {
            e.HasKey(x => new { x.ItemId, x.TagId });
            e.HasOne(x => x.Item).WithMany(i => i.ItemTags).HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Tag).WithMany(t => t.ItemTags).HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<NoteTag>(e =>
        {
            e.HasKey(x => new { x.NoteId, x.TagId });
            e.HasOne(x => x.Note).WithMany(n => n.NoteTags).HasForeignKey(x => x.NoteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Tag).WithMany(t => t.NoteTags).HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Attachment>(e =>
        {
            e.Property(x => x.FileName).HasMaxLength(255);
            e.Property(x => x.ContentType).HasMaxLength(100);
            e.Property(x => x.StoragePath).HasMaxLength(300);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.HasIndex(x => x.Sha256).IsUnique();
        });
    }
}
```

- [ ] **Step 4: Program.cs에 DbContext 등록 + 시작 시 마이그레이션** — `builder.Services.ConfigureHttpJsonOptions(...)` 다음 줄에 추가, `var app = builder.Build();` 다음에 Migrate 블록 추가. 파일 상단 using에 `using Microsoft.EntityFrameworkCore;`, `using WebProject.Api.Infrastructure.Data;` 추가.

```csharp
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException("ConnectionStrings:Default 설정이 없습니다.")));
```

```csharp
// 단일 인스턴스 배포이므로 시작 시 마이그레이션을 적용한다(스펙 3.5). 다중 인스턴스로 바뀌면 별도 마이그레이션 잡으로 분리한다.
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
}
```

- [ ] **Step 5: 설정 파일** — `appsettings.json`에 다음 키 추가(`AllowedHosts` 아래):

```json
"ConnectionStrings": { "Default": "" }
```

`appsettings.Development.json`에 추가:

```json
"ConnectionStrings": { "Default": "Host=localhost;Port=5432;Database=para_dev;Username=postgres;Password=changeme" }
```
(개발용 로컬 컨테이너 비밀번호는 `changeme`로 통일한다. Stop 훅의 비밀값 스캔이 `Password=...` 패턴을 잡지만 `changeme`는 플레이스홀더로 예외 처리된다. 실제 배포 비밀번호는 Plan 4의 `deploy/.env`(gitignore)에만 둔다.)

- [ ] **Step 6: 마이그레이션 생성**

```bash
dotnet ef migrations add InitialCreate --project WebProject.Api --output-dir Infrastructure/Data/Migrations
```
Expected: `WebProject.Api/Infrastructure/Data/Migrations/` 에 `*_InitialCreate.cs`, `AppDbContextModelSnapshot.cs` 생성. 오류 "Unable to create a 'DbContext'"가 나면 `appsettings.Development.json`의 연결 문자열이 비어 있지 않은지 확인(설계 시점엔 연결하지 않고 문자열만 필요).

- [ ] **Step 7: 테스트 인프라 작성** — `WebProject.Api.Tests/Infrastructure/`

```csharp
// PostgresContainerFixture.cs
using Testcontainers.PostgreSql;

namespace WebProject.Api.Tests.Infrastructure;

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
    // PostgreSqlContainer: Docker API로 컨테이너를 기동하고 컨테이너 안에서 pg_isready 를 반복 실행하는 대기 전략으로
    // 준비 완료를 판정하므로 sleep 기반 폴링 없이 연결 가능한 시점을 정확히 얻는다.
    // 4.15.0에서 매개변수 없는 PostgreSqlBuilder() 는 obsolete 이므로 이미지를 생성자 인수로 준다(경고 0 유지).
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
using WebProject.Api.Infrastructure.Data;

namespace WebProject.Api.Tests.Infrastructure;

/// <summary>테스트 클래스마다 고유한 데이터베이스·첨부 폴더를 갖는 인메모리 호스트 팩토리.</summary>
/// <remarks>기본 생성자(xUnit 주입)는 설정 오버라이드 없이 만든다. 특정 테스트가 다른 설정(프록시 신뢰 네트워크 등)이 필요하면
/// <c>new ApiFactory(pg, settings)</c>로 직접 만들고 <c>using</c>으로 해제한다.</remarks>
public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly IReadOnlyDictionary<string, string?> _settings;

    public string AttachmentsRoot { get; }

    public ApiFactory(PostgresContainerFixture pg) : this(pg, new Dictionary<string, string?>()) { }

    public ApiFactory(PostgresContainerFixture pg, IReadOnlyDictionary<string, string?> settings)
    {
        // 클래스마다 새 DB 이름을 써서 테스트 간 데이터 간섭을 없앤다. Migrate()가 DB를 생성한다.
        var csb = new NpgsqlConnectionStringBuilder(pg.ConnectionString)
        {
            Database = "para_test_" + Guid.NewGuid().ToString("N"),
        };
        _connectionString = csb.ToString();
        _settings = settings;
        AttachmentsRoot = Path.Combine(Path.GetTempPath(), "para-tests", Guid.NewGuid().ToString("N"));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", _connectionString);
        builder.UseSetting("Attachments:RootPath", AttachmentsRoot);
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }
    }

    /// <summary>호출자가 소유하는 DI 스코프를 만든다. <c>await using var scope = factory.CreateScope();</c> 뒤
    /// <c>scope.ServiceProvider.GetRequiredService&lt;AppDbContext&gt;()</c>로 DbContext를 얻는다(스코프 누수 방지).</summary>
    public AsyncServiceScope CreateScope() => Services.CreateAsyncScope();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(AttachmentsRoot))
        {
            Directory.Delete(AttachmentsRoot, recursive: true);
        }
    }
}
```

```csharp
// TestJson.cs
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebProject.Api.Tests.Infrastructure;

public static class TestJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}
```

- [ ] **Step 8: 실패하는 테스트 작성** — `WebProject.Api.Tests/Infrastructure/DatabaseMigrationTests.cs`

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebProject.Api.Domain;
using WebProject.Api.Infrastructure.Data;

namespace WebProject.Api.Tests.Infrastructure;

[Collection("postgres")]
public sealed class DatabaseMigrationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Startup_AppliesInitialMigration_AndCanRoundTripItem()
    {
        using var client = factory.CreateClient(); // 호스트 기동 → Migrate() 실행
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var applied = await db.Database.GetAppliedMigrationsAsync();
        Assert.Contains(applied, m => m.EndsWith("InitialCreate", StringComparison.Ordinal));

        var item = new Item { Kind = ItemKind.Project, Title = "마이그레이션 검증", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.Items.Add(item);
        await db.SaveChangesAsync();

        var loaded = await db.Items.AsNoTracking().SingleAsync(i => i.Id == item.Id);
        Assert.Equal("마이그레이션 검증", loaded.Title);
        Assert.Equal(ItemKind.Project, loaded.Kind);
    }
}
```

- [ ] **Step 9: 테스트 실행(실패 확인 → 통과)**

Run: `dotnet test WebProject.Api.Tests --filter "FullyQualifiedName~DatabaseMigrationTests"`
Expected: Step 2~7을 다 적용했다면 PASS. 컨테이너 기동 실패 시 Docker Desktop 실행 여부 확인. `HealthEndpointTests`는 `WebApplicationFactory<Program>`를 직접 쓰므로 연결 문자열이 없어 **기동 실패**한다 → Step 10.

- [ ] **Step 10: HealthEndpointTests를 ApiFactory로 전환** — `IClassFixture<WebApplicationFactory<Program>>` → `[Collection("postgres")]` + `IClassFixture<ApiFactory>`, 생성자 매개변수 타입 `ApiFactory`, `using WebProject.Api.Tests.Infrastructure;` 추가. 본문·주석은 유지.

Run: `dotnet test WebProject.sln`
Expected: 전부 PASS(Health 2 + Migration 1).

- [ ] **Step 11: 커밋**

```bash
git add -A
git commit -m "추가: PARA 도메인 엔티티·EF Core DbContext·초기 마이그레이션과 Postgres 테스트 픽스처

- Item/Note/TaskItem/Tag/Attachment 및 링크 테이블, 시작 시 Migrate
- Testcontainers PostgreSQL 컬렉션 픽스처 + 클래스별 고유 DB ApiFactory

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: 쓰기 접근 제어 (IP 화이트리스트) + `/api/me` + ForwardedHeaders

**Files:**
- Create: `WebProject.Api/Infrastructure/Access/IWriteAccessPolicy.cs`, `WriteAccessOptions.cs`, `IpAllowlistWriteAccessPolicy.cs`, `RequireWriteAccessFilter.cs`, `WriteAccessServiceCollectionExtensions.cs`
- Create: `WebProject.Api/Contracts/MeDto.cs`, `WebProject.Api/Features/Me/MeEndpoints.cs`, `WebProject.Api/Features/ApiEndpoints.cs`
- Modify: `WebProject.Api/Program.cs`, `appsettings.json`, `appsettings.Development.json`
- Create: `WebProject.Api.Tests/Infrastructure/FakeWriteAccessPolicy.cs`; Modify `ApiFactory.cs`
- Create: `WebProject.Api.Tests/Infrastructure/RemoteIpStartupFilter.cs`
- Test: `WebProject.Api.Tests/Infrastructure/IpAllowlistWriteAccessPolicyTests.cs`, `WebProject.Api.Tests/Features/MeEndpointsTests.cs`, `WebProject.Api.Tests/Features/ForwardedHeadersTests.cs`

**Interfaces:**
- Produces: `IWriteAccessPolicy.CanWrite(HttpContext) : bool`; `RequireWriteAccessFilter : IEndpointFilter`(정책 거부 → 403, `X-Requested-With: XMLHttpRequest` 헤더 없음 → 403 CSRF 방어); `WriteAccessServiceCollectionExtensions.UseTrustedForwardedHeaders(this WebApplication)`; `ApiEndpoints.MapApiEndpoints(this IEndpointRouteBuilder)`가 `(RouteGroupBuilder read, RouteGroupBuilder write)` 두 그룹을 만들고 각 Feature의 `Map<X>Endpoints(read, write)`를 호출; 테스트 `ApiFactory.WriteAccess.Allow` (bool) 스위치, `ApiFactory.CreateClient()`는 `X-Requested-With` 기본 헤더를 붙인다, 설정 키 `"Test:UseRealWritePolicy"="true"`면 가짜 정책으로 바꾸지 않는다.

- [ ] **Step 1: 정책 단위 테스트 작성** — `WebProject.Api.Tests/Infrastructure/IpAllowlistWriteAccessPolicyTests.cs`

```csharp
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using WebProject.Api.Infrastructure.Access;

namespace WebProject.Api.Tests.Infrastructure;

public sealed class IpAllowlistWriteAccessPolicyTests
{
    private static IpAllowlistWriteAccessPolicy Create(params string[] cidrs) =>
        new(Options.Create(new WriteAccessOptions { AllowedCidrs = cidrs }));

    private static HttpContext ContextFrom(string ip)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        return ctx;
    }

    [Theory]
    [InlineData("192.168.0.0/16", "192.168.10.5", true)]
    [InlineData("192.168.0.0/16", "10.0.0.1", false)]
    [InlineData("203.0.113.7/32", "203.0.113.7", true)]
    [InlineData("203.0.113.7/32", "203.0.113.8", false)]
    [InlineData("::1/128", "::1", true)]
    [InlineData("2001:db8::/32", "2001:db8:1::5", true)]
    public void CanWrite_MatchesCidr(string cidr, string ip, bool expected)
    {
        Assert.Equal(expected, Create(cidr).CanWrite(ContextFrom(ip)));
    }

    [Fact]
    public void CanWrite_Ipv4MappedIpv6_MatchesIpv4Cidr()
    {
        // Kestrel은 듀얼스택 소켓에서 IPv4 클라이언트를 ::ffff:a.b.c.d 로 보고한다.
        Assert.True(Create("192.168.0.0/16").CanWrite(ContextFrom("::ffff:192.168.1.20")));
    }

    [Fact]
    public void CanWrite_EmptyAllowlist_DeniesEveryone()
    {
        Assert.False(Create().CanWrite(ContextFrom("127.0.0.1")));
    }

    [Fact]
    public void CanWrite_NoRemoteAddress_Denies()
    {
        Assert.False(Create("0.0.0.0/0").CanWrite(new DefaultHttpContext()));
    }

    [Fact]
    public void Ctor_InvalidCidr_Throws()
    {
        Assert.Throws<FormatException>(() => Create("not-a-cidr"));
    }
}
```

- [ ] **Step 2: 실패 확인**

Run: `dotnet test WebProject.Api.Tests --filter "FullyQualifiedName~IpAllowlistWriteAccessPolicyTests"`
Expected: 컴파일 오류(타입 없음).

- [ ] **Step 3: 접근 제어 구현** — `WebProject.Api/Infrastructure/Access/`

```csharp
// IWriteAccessPolicy.cs
namespace WebProject.Api.Infrastructure.Access;

/// <summary>현재 요청이 쓰기(생성·수정·삭제·업로드·가져오기)를 수행할 수 있는지 판정한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 구현은 반드시 Thread-safe(무상태 또는 불변)여야 한다. 싱글턴으로 등록되어 모든 요청 스레드에서 동시 호출된다.</description></item>
/// <item><description><b>Memory Allocation:</b> 구현은 요청당 힙 할당 없이(Zero-allocation) 판정해야 한다.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O·DB 조회 금지. 로그인 도입 시에도 인증 결과는 HttpContext.User에서 읽는다.</description></item>
/// </list>
/// 1차 구현은 <see cref="IpAllowlistWriteAccessPolicy"/>. 로그인 도입 시 "IP 허용 OR 인증 사용자" 구현으로 교체한다(스펙 7절).
/// </remarks>
public interface IWriteAccessPolicy
{
    bool CanWrite(HttpContext context);
}
```

```csharp
// WriteAccessOptions.cs
namespace WebProject.Api.Infrastructure.Access;

/// <summary>설정 섹션 <c>WriteAccess</c>. 쓰기를 허용할 CIDR 목록.</summary>
public sealed class WriteAccessOptions
{
    public const string SectionName = "WriteAccess";
    /// <summary>예: ["192.168.0.0/16", "::1/128"]. 비어 있으면 아무도 쓸 수 없다(안전 기본값).</summary>
    public string[] AllowedCidrs { get; set; } = [];
}
```

```csharp
// IpAllowlistWriteAccessPolicy.cs
using System.Net;
using Microsoft.Extensions.Options;

namespace WebProject.Api.Infrastructure.Access;

/// <summary>요청 원격 IP가 허용 CIDR 중 하나에 속할 때만 쓰기를 허용한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 생성자에서 파싱한 불변 배열만 읽는다.</description></item>
/// <item><description><b>Memory Allocation:</b> 판정 경로 Zero-allocation. IPv4-mapped IPv6 → IPv4 변환(<c>MapToIPv4</c>)만 예외적으로 IPAddress 1개 할당.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환. 네트워크·I/O 없음.</description></item>
/// </list>
/// </remarks>
public sealed class IpAllowlistWriteAccessPolicy : IWriteAccessPolicy
{
    // System.Net.IPNetwork: readonly struct라 배열에 인라인 저장되어 캐시 지역성이 좋고, Contains()는 프리픽스 비트 비교만 수행한다.
    private readonly IPNetwork[] _networks;

    public IpAllowlistWriteAccessPolicy(IOptions<WriteAccessOptions> options)
    {
        _networks = options.Value.AllowedCidrs
            .Select(c => IPNetwork.Parse(c.Trim()))
            .ToArray();
    }

    public bool CanWrite(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
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
// RequireWriteAccessFilter.cs
namespace WebProject.Api.Infrastructure.Access;

/// <summary>쓰기 엔드포인트 그룹에 부착되어 (1) CSRF 방어용 커스텀 헤더가 없거나 (2) <see cref="IWriteAccessPolicy"/>가 거부하면 403 ProblemDetails를 반환한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 무상태이며 요청 서비스에서 정책을 조회만 한다.</description></item>
/// <item><description><b>Memory Allocation:</b> 허용 경로는 할당 없음. 거부 시 ProblemDetails 결과 1개 할당.</description></item>
/// <item><description><b>Blocking:</b> Non-blocking. 다음 필터/핸들러를 await 한다.</description></item>
/// </list>
/// IP 화이트리스트는 CSRF를 막지 못한다(허용 네트워크 안의 브라우저가 악성 사이트를 열면 그 요청도 허용 IP에서 온다).
/// 브라우저는 교차 출처 요청에 커스텀 헤더를 붙이려면 CORS 프리플라이트를 통과해야 하고 이 API는 CORS를 열지 않으므로,
/// <c>X-Requested-With: XMLHttpRequest</c> 헤더 필수 검사만으로 교차 출처 폼 POST·multipart 업로드가 차단된다.
/// </remarks>
public sealed class RequireWriteAccessFilter : IEndpointFilter
{
    public const string CsrfHeaderName = "X-Requested-With";
    public const string CsrfHeaderValue = "XMLHttpRequest";

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!string.Equals(http.Request.Headers[CsrfHeaderName], CsrfHeaderValue, StringComparison.Ordinal))
        {
            return ValueTask.FromResult<object?>(Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "교차 출처 요청 거부",
                detail: $"쓰기 요청에는 {CsrfHeaderName}: {CsrfHeaderValue} 헤더가 필요합니다."));
        }
        var policy = http.RequestServices.GetRequiredService<IWriteAccessPolicy>();
        if (!policy.CanWrite(http))
        {
            return ValueTask.FromResult<object?>(Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "쓰기 권한 없음",
                detail: "이 네트워크에서는 읽기만 가능합니다."));
        }
        return next(context);
    }
}
```

```csharp
// WriteAccessServiceCollectionExtensions.cs
using Microsoft.AspNetCore.HttpOverrides;
// System.Net 을 using 하면 Microsoft.AspNetCore.HttpOverrides.IPNetwork(obsolete)와 이름이 충돌하므로 별칭으로만 쓴다.
using IPNetwork = System.Net.IPNetwork;

namespace WebProject.Api.Infrastructure.Access;

public static class WriteAccessServiceCollectionExtensions
{
    /// <summary>설정 키 <c>ForwardedHeaders:KnownNetworks</c>(CIDR 배열). Caddy 컨테이너가 속한 compose 네트워크만 적는다.</summary>
    public const string KnownNetworksKey = "ForwardedHeaders:KnownNetworks";

    /// <summary>WriteAccess 옵션·IP 화이트리스트 정책·ForwardedHeaders 옵션을 등록한다.</summary>
    public static IServiceCollection AddWriteAccess(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WriteAccessOptions>(configuration.GetSection(WriteAccessOptions.SectionName));
        services.AddSingleton<IWriteAccessPolicy, IpAllowlistWriteAccessPolicy>();

        var known = configuration.GetSection(KnownNetworksKey).Get<string[]>() ?? [];
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // .NET 10: KnownNetworks/HttpOverrides.IPNetwork 는 obsolete. KnownIPNetworks(System.Net.IPNetwork)만 사용한다.
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
            foreach (var cidr in known)
            {
                o.KnownIPNetworks.Add(IPNetwork.Parse(cidr.Trim()));
            }
        });
        return services;
    }

    /// <summary>신뢰 프록시 네트워크가 설정된 경우에만 ForwardedHeaders 미들웨어를 등록한다.</summary>
    /// <remarks>
    /// ForwardedHeadersMiddleware 는 KnownIPNetworks 와 KnownProxies 가 <b>둘 다 비어 있으면 송신자 검사를 생략</b>하고
    /// 모든 X-Forwarded-For 를 신뢰한다(소스: <c>checkKnownIps = KnownIPNetworks.Count > 0 || KnownProxies.Count > 0</c>).
    /// 따라서 설정이 비었을 때 미들웨어를 등록하면 외부에서 헤더를 위조해 화이트리스트를 우회할 수 있다.
    /// 프록시 없이 직접 노출(개발·테스트)하면 미들웨어를 아예 넣지 않아 Connection.RemoteIpAddress 만 쓰게 한다.
    /// </remarks>
    public static WebApplication UseTrustedForwardedHeaders(this WebApplication app)
    {
        var known = app.Configuration.GetSection(KnownNetworksKey).Get<string[]>() ?? [];
        if (known.Length > 0)
        {
            app.UseForwardedHeaders();
        }
        return app;
    }
}
```

- [ ] **Step 4: 정책 테스트 통과 확인**

Run: `dotnet test WebProject.Api.Tests --filter "FullyQualifiedName~IpAllowlistWriteAccessPolicyTests"`
Expected: 10개 PASS.

- [ ] **Step 5: `/api/me` + 엔드포인트 배선**

```csharp
// Contracts/MeDto.cs
namespace WebProject.Api.Contracts;
/// <summary>현재 요청자의 능력. SPA가 읽기 전용 모드를 결정하는 근거.</summary>
public sealed record MeDto(bool CanWrite);
```

```csharp
// Features/Me/MeEndpoints.cs
using WebProject.Api.Contracts;
using WebProject.Api.Infrastructure.Access;

namespace WebProject.Api.Features.Me;

public static class MeEndpoints
{
    public static void MapMeEndpoints(RouteGroupBuilder read, RouteGroupBuilder write)
    {
        read.MapGet("/me", (HttpContext ctx, IWriteAccessPolicy policy) => TypedResults.Ok(new MeDto(policy.CanWrite(ctx))))
            .WithName("GetMe");
    }
}
```

```csharp
// Features/ApiEndpoints.cs
using WebProject.Api.Features.Me;
using WebProject.Api.Infrastructure.Access;

namespace WebProject.Api.Features;

/// <summary>/api 아래 읽기 그룹과 쓰기 그룹(쓰기 접근 필터 부착)을 만들고 각 기능의 엔드포인트를 등록한다.</summary>
public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapApiEndpoints(this IEndpointRouteBuilder app)
    {
        var read = app.MapGroup("/api");
        var write = app.MapGroup("/api").AddEndpointFilter<RequireWriteAccessFilter>();

        MeEndpoints.MapMeEndpoints(read, write);
        // 이후 Task에서 한 줄씩 추가: ItemEndpoints.MapItemEndpoints(read, write); ...
        return app;
    }
}
```

Program.cs: `builder.Services.AddDbContext<...>` 다음에 `builder.Services.AddWriteAccess(builder.Configuration);`, `app.UseExceptionHandler();` **앞**에 `app.UseTrustedForwardedHeaders();`(설정이 비면 미들웨어 미등록), `app.MapGet("/health", ...)` 다음에 `app.MapApiEndpoints();`. using에 `WebProject.Api.Features`, `WebProject.Api.Infrastructure.Access` 추가.

`appsettings.json`에 추가:
```json
"WriteAccess": { "AllowedCidrs": [] },
"ForwardedHeaders": { "KnownNetworks": [] }
```
`appsettings.Development.json`에 추가:
```json
"WriteAccess": { "AllowedCidrs": [ "127.0.0.1/32", "::1/128" ] }
```

- [ ] **Step 6: 테스트용 가짜 정책 + ApiFactory 교체**

```csharp
// WebProject.Api.Tests/Infrastructure/FakeWriteAccessPolicy.cs
using Microsoft.AspNetCore.Http;
using WebProject.Api.Infrastructure.Access;

namespace WebProject.Api.Tests.Infrastructure;

public sealed class FakeWriteAccessPolicy : IWriteAccessPolicy
{
    // volatile: 테스트 스레드가 바꾼 값을 서버 스레드가 캐시 없이 즉시 관측하도록 한다.
    private volatile bool _allow = true;
    public bool Allow { get => _allow; set => _allow = value; }
    public bool CanWrite(HttpContext context) => _allow;
}
```

`ApiFactory.cs`에 다음을 추가한다(using `Microsoft.Extensions.DependencyInjection.Extensions`, `WebProject.Api.Infrastructure.Access`, `Microsoft.AspNetCore.Hosting`):

```csharp
    public FakeWriteAccessPolicy WriteAccess { get; } = new();

    /// <summary>설정 "Test:UseRealWritePolicy"="true" 이면 IP 화이트리스트 실제 구현을 그대로 둔다(프록시 경계 테스트용).</summary>
    private bool UseRealWritePolicy => _settings.TryGetValue("Test:UseRealWritePolicy", out var v) && v == "true";

    // ConfigureWebHost 끝에 추가:
    builder.ConfigureServices(services =>
    {
        // 테스트가 X-Test-Remote-Ip 헤더로 원격 IP를 흉내 낼 수 있게 파이프라인 맨 앞에 미들웨어를 꽂는다(TestServer 는 RemoteIpAddress 가 null).
        services.AddTransient<IStartupFilter, RemoteIpStartupFilter>();
        if (!UseRealWritePolicy)
        {
            services.RemoveAll<IWriteAccessPolicy>();
            services.AddSingleton<IWriteAccessPolicy>(WriteAccess);
        }
    });

    /// <summary>모든 테스트 클라이언트에 CSRF 방어 헤더를 기본으로 붙인다. 헤더 부재를 검증하는 테스트는 직접 제거한다.</summary>
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Add(RequireWriteAccessFilter.CsrfHeaderName, RequireWriteAccessFilter.CsrfHeaderValue);
    }
```

```csharp
// WebProject.Api.Tests/Infrastructure/RemoteIpStartupFilter.cs
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace WebProject.Api.Tests.Infrastructure;

/// <summary>요청 헤더 <c>X-Test-Remote-Ip</c> 값을 <c>Connection.RemoteIpAddress</c>에 넣는 테스트 전용 미들웨어를
/// 앱 파이프라인 **앞**에 등록한다(IStartupFilter 는 Program.cs 의 미들웨어보다 먼저 실행된다).</summary>
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

- [ ] **Step 7: `/api/me` 통합 테스트** — `WebProject.Api.Tests/Features/MeEndpointsTests.cs`

```csharp
using System.Net.Http.Json;
using WebProject.Api.Contracts;
using WebProject.Api.Tests.Infrastructure;

namespace WebProject.Api.Tests.Features;

[Collection("postgres")]
public sealed class MeEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task GetMe_ReflectsWriteAccessPolicy()
    {
        using var client = factory.CreateClient();

        factory.WriteAccess.Allow = true;
        var allowed = await client.GetFromJsonAsync<MeDto>("/api/me", TestJson.Options);
        Assert.True(allowed!.CanWrite);

        factory.WriteAccess.Allow = false;
        var denied = await client.GetFromJsonAsync<MeDto>("/api/me", TestJson.Options);
        Assert.False(denied!.CanWrite);
    }
}
```

- [ ] **Step 8: 프록시 신뢰 경계·CSRF 헤더 통합 테스트** — `WebProject.Api.Tests/Features/ForwardedHeadersTests.cs`. 실제 `IpAllowlistWriteAccessPolicy`를 쓰는 팩토리를 직접 만들어 X-Forwarded-For 처리 경계를 검증한다.

```csharp
using System.Net;
using System.Net.Http.Json;
using WebProject.Api.Contracts;
using WebProject.Api.Infrastructure.Access;
using WebProject.Api.Tests.Infrastructure;

namespace WebProject.Api.Tests.Features;

[Collection("postgres")]
public sealed class ForwardedHeadersTests(PostgresContainerFixture pg)
{
    private const string AllowedClient = "203.0.113.9";   // 화이트리스트에 있는 실제 클라이언트
    private const string Proxy = "10.0.0.5";              // Caddy 컨테이너(신뢰 네트워크 안)
    private const string Outsider = "198.51.100.7";       // 인터넷의 임의 호스트

    private static Dictionary<string, string?> Settings(params string[] knownNetworks)
    {
        var s = new Dictionary<string, string?>
        {
            ["Test:UseRealWritePolicy"] = "true",
            ["WriteAccess:AllowedCidrs:0"] = "203.0.113.0/24",
        };
        for (var i = 0; i < knownNetworks.Length; i++) s[$"ForwardedHeaders:KnownNetworks:{i}"] = knownNetworks[i];
        return s;
    }

    private static async Task<bool> CanWriteAsync(ApiFactory factory, string remoteIp, string? forwardedFor)
    {
        using var client = factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        req.Headers.Add(RemoteIpStartupFilter.HeaderName, remoteIp);
        if (forwardedFor is not null) req.Headers.Add("X-Forwarded-For", forwardedFor);
        using var res = await client.SendAsync(req);
        return (await res.Content.ReadFromJsonAsync<MeDto>(TestJson.Options))!.CanWrite;
    }

    [Fact]
    public async Task TrustedProxy_ForwardedFor_IsHonored()
    {
        using var factory = new ApiFactory(pg, Settings("10.0.0.0/8"));
        Assert.True(await CanWriteAsync(factory, Proxy, AllowedClient));
        Assert.False(await CanWriteAsync(factory, Proxy, Outsider));
    }

    [Fact]
    public async Task UntrustedSender_ForwardedFor_IsIgnored()
    {
        using var factory = new ApiFactory(pg, Settings("10.0.0.0/8"));
        // 신뢰 네트워크 밖에서 온 위조 헤더는 무시되고 실제 연결 IP(외부)로 판정된다.
        Assert.False(await CanWriteAsync(factory, Outsider, AllowedClient));
    }

    [Fact]
    public async Task NoKnownNetworks_MiddlewareNotRegistered_ForwardedForIgnored()
    {
        using var factory = new ApiFactory(pg, Settings());
        // KnownNetworks 가 비어 있으면 미들웨어가 등록되지 않아 헤더 위조로 우회할 수 없다(ASP.NET 기본 동작은 반대이므로 반드시 검증).
        Assert.False(await CanWriteAsync(factory, Outsider, AllowedClient));
        Assert.True(await CanWriteAsync(factory, AllowedClient, null));
    }

    [Fact(Skip = "Task 4에서 /api/items POST 가 생기면 Skip 제거")]
    public async Task WriteWithoutCsrfHeader_Returns403_EvenFromAllowedIp()
    {
        using var factory = new ApiFactory(pg, Settings());
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Remove(RequireWriteAccessFilter.CsrfHeaderName);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/items")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(RemoteIpStartupFilter.HeaderName, AllowedClient);
        using var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
```

- [ ] **Step 9: 전체 테스트**

Run: `dotnet test WebProject.sln`
Expected: 전부 PASS, 경고 0.

- [ ] **Step 10: 커밋**

```bash
git add -A
git commit -m "추가: IP 화이트리스트 쓰기 접근 정책과 /api/me 엔드포인트

- IWriteAccessPolicy 추상화(추후 로그인으로 교체), 쓰기 그룹 엔드포인트 필터 403 + X-Requested-With CSRF 방어
- ForwardedHeaders 는 신뢰 프록시 네트워크가 설정된 경우에만 등록(빈 설정 시 헤더 위조 우회 차단)

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: Contracts · TagResolver · DtoMapping · Items 엔드포인트

**Files:**
- Create: `WebProject.Api/Contracts/ValidationErrors.cs`, `ItemDtos.cs`, `NoteDtos.cs`, `TaskDtos.cs`, `TagDtos.cs`
- Create: `WebProject.Api/Infrastructure/Data/TagResolver.cs`, `DtoMapping.cs`, `UniqueViolation.cs`
- Create: `WebProject.Api/Features/Items/ItemEndpoints.cs`, `ItemValidation.cs`
- Modify: `WebProject.Api.Tests/Features/ForwardedHeadersTests.cs` (Skip 제거)
- Modify: `WebProject.Api/Features/ApiEndpoints.cs`
- Test: `WebProject.Api.Tests/Features/ItemEndpointsTests.cs`

**Interfaces:**
- Produces:
  - `ItemSummaryDto(Guid Id, ItemKind Kind, ItemKind? PreviousKind, string Title, string Description, ItemStatus Status, DateOnly? DueDate, Guid? AreaId, string? AreaTitle, string[] Tags, int SortOrder, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)`
  - `ItemDetailDto(ItemSummaryDto Item, NoteSummaryDto[] Notes, TaskDto[] Tasks)`
  - `UpsertItemRequest(ItemKind Kind, string Title, string? Description, ItemStatus Status, DateOnly? DueDate, Guid? AreaId, string[]? TagNames, int SortOrder)`
  - `NoteSummaryDto(Guid Id, string Title, string Excerpt, Guid[] ItemIds, string[] Tags, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)`
  - `NoteItemRefDto(Guid Id, ItemKind Kind, string Title)`, `NoteDetailDto(Guid Id, string Title, string ContentMarkdown, NoteItemRefDto[] Items, string[] Tags, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)`, `UpsertNoteRequest(string Title, string? ContentMarkdown, Guid[]? ItemIds, string[]? TagNames)`, `PagedNotesDto(NoteSummaryDto[] Items, int Total)`
  - `TaskDto(Guid Id, string Title, bool IsDone, DateTimeOffset? CompletedAt, DateOnly? DueDate, Guid? ItemId, int SortOrder, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)`, `UpsertTaskRequest(string Title, DateOnly? DueDate, Guid? ItemId, int SortOrder, bool IsDone = false)`
  - `TagDto(Guid Id, string Name, string? Color, int ItemCount, int NoteCount)`
  - `TagResolver.ResolveAsync(AppDbContext db, IEnumerable<string>? names, CancellationToken ct) : Task<List<Tag>>`, `TagResolver.Validate(IEnumerable<string>? names, ValidationErrors errors, string field)` (50자 초과·공백만 → 오류), `TagResolver.MaxLength = 50`
  - `UniqueViolation.Is(DbUpdateException) : bool` (PostgreSQL 23505 판정) — 동시 생성 경쟁은 409 `ProblemDetails`로 응답
  - 응답의 `Tags` 배열은 항상 **정규화 이름 오름차순**(`StringComparer.Ordinal`)으로 정렬된다(입력 순서 보존 안 함)
  - `DtoMapping.ToSummary(this Item)`, `ToSummary(this Note)`, `ToDetail(this Note)`, `ToDto(this TaskItem)` (모두 Include 로드된 네비게이션 전제)
  - `ValidationErrors` 빌더 → `TypedResults.ValidationProblem(errors)`

- [ ] **Step 1: 실패하는 Items 테스트 작성** — `WebProject.Api.Tests/Features/ItemEndpointsTests.cs`

```csharp
using System.Net;
using System.Net.Http.Json;
using WebProject.Api.Contracts;
using WebProject.Api.Domain;
using WebProject.Api.Tests.Infrastructure;

namespace WebProject.Api.Tests.Features;

[Collection("postgres")]
public sealed class ItemEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static UpsertItemRequest NewItem(ItemKind kind, string title, Guid? areaId = null, string[]? tags = null) =>
        new(kind, title, "설명", ItemStatus.Active, new DateOnly(2026, 12, 31), areaId, tags, 0);

    private async Task<ItemSummaryDto> CreateAsync(HttpClient client, UpsertItemRequest req)
    {
        var res = await client.PostAsJsonAsync("/api/items", req, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<ItemSummaryDto>(TestJson.Options))!;
    }

    [Fact]
    public async Task Post_CreatesItem_WithTagsAndArea_ReturnsCreated()
    {
        using var client = factory.CreateClient();
        var area = await CreateAsync(client, NewItem(ItemKind.Area, "건강"));
        var project = await CreateAsync(client, NewItem(ItemKind.Project, "마라톤 준비", area.Id, ["운동", "2026"]));

        Assert.Equal(ItemKind.Project, project.Kind);
        Assert.Equal(area.Id, project.AreaId);
        Assert.Equal("건강", project.AreaTitle);
        Assert.Equal(["2026", "운동"], project.Tags); // 응답 태그는 정규화 이름 순 정렬(입력 순서 아님)
        Assert.Null(project.PreviousKind);
    }

    [Fact]
    public async Task Post_UndefinedEnumValue_IsRejectedWith400()
    {
        using var client = factory.CreateClient();
        // allowIntegerValues:false + ThrowOnBadRequest=false → 역직렬화 실패가 400으로 온다.
        var res = await client.PostAsync("/api/items", new StringContent(
            "{\"kind\":99,\"title\":\"x\",\"status\":\"Active\",\"sortOrder\":0}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        var unknownName = await client.PostAsync("/api/items", new StringContent(
            "{\"kind\":\"Galaxy\",\"title\":\"x\",\"status\":\"Active\",\"sortOrder\":0}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, unknownName.StatusCode);
    }

    [Fact]
    public async Task Post_TagLongerThan50_IsRejected()
    {
        using var client = factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/items", NewItem(ItemKind.Project, "긴 태그", tags: [new string('t', 51)]), TestJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Post_ArchiveKind_IsRejected()
    {
        using var client = factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/items", NewItem(ItemKind.Archive, "잘못된 생성"), TestJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Post_AreaIdPointingToNonArea_IsRejected()
    {
        using var client = factory.CreateClient();
        var project = await CreateAsync(client, NewItem(ItemKind.Project, "프로젝트 A"));
        var res = await client.PostAsJsonAsync("/api/items", NewItem(ItemKind.Resource, "자원 B", project.Id), TestJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Post_EmptyTitle_IsRejected()
    {
        using var client = factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/items", NewItem(ItemKind.Project, "   "), TestJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Get_List_FiltersByKindAndTag()
    {
        using var client = factory.CreateClient();
        var p = await CreateAsync(client, NewItem(ItemKind.Project, "필터 대상", tags: ["필터태그"]));
        await CreateAsync(client, NewItem(ItemKind.Resource, "다른 종류", tags: ["필터태그"]));

        var byKind = await client.GetFromJsonAsync<ItemSummaryDto[]>("/api/items?kind=Project", TestJson.Options);
        Assert.Contains(byKind!, i => i.Id == p.Id);
        Assert.All(byKind!, i => Assert.Equal(ItemKind.Project, i.Kind));

        var byTag = await client.GetFromJsonAsync<ItemSummaryDto[]>("/api/items?tag=필터태그", TestJson.Options);
        Assert.Equal(2, byTag!.Count(i => i.Tags.Contains("필터태그") && (i.Title == "필터 대상" || i.Title == "다른 종류")));
    }

    [Fact]
    public async Task Get_Detail_IncludesEmptyNotesAndTasks_And404WhenMissing()
    {
        using var client = factory.CreateClient();
        var p = await CreateAsync(client, NewItem(ItemKind.Project, "상세 조회"));

        var detail = await client.GetFromJsonAsync<ItemDetailDto>($"/api/items/{p.Id}", TestJson.Options);
        Assert.Equal(p.Id, detail!.Item.Id);
        Assert.Empty(detail.Notes);
        Assert.Empty(detail.Tasks);

        var missing = await client.GetAsync($"/api/items/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Put_UpdatesFields_AndReplacesTags()
    {
        using var client = factory.CreateClient();
        var p = await CreateAsync(client, NewItem(ItemKind.Project, "수정 전", tags: ["a", "b"]));

        var res = await client.PutAsJsonAsync($"/api/items/{p.Id}",
            new UpsertItemRequest(ItemKind.Project, "수정 후", "새 설명", ItemStatus.Done, null, null, ["b", "c"], 5), TestJson.Options);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var updated = (await res.Content.ReadFromJsonAsync<ItemSummaryDto>(TestJson.Options))!;

        Assert.Equal("수정 후", updated.Title);
        Assert.Equal(ItemStatus.Done, updated.Status);
        Assert.Null(updated.DueDate);
        Assert.Equal(["b", "c"], updated.Tags);
        Assert.Equal(5, updated.SortOrder);
        Assert.True(updated.UpdatedAt >= p.UpdatedAt);
    }

    [Fact]
    public async Task Put_SelfAsArea_IsRejected()
    {
        using var client = factory.CreateClient();
        var area = await CreateAsync(client, NewItem(ItemKind.Area, "자기 참조"));
        var res = await client.PutAsJsonAsync($"/api/items/{area.Id}", NewItem(ItemKind.Area, "자기 참조", area.Id), TestJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Archive_ThenRestore_RoundTripsKind()
    {
        using var client = factory.CreateClient();
        var p = await CreateAsync(client, NewItem(ItemKind.Resource, "보관 대상"));

        var archived = await (await client.PostAsync($"/api/items/{p.Id}/archive", null)).Content.ReadFromJsonAsync<ItemSummaryDto>(TestJson.Options);
        Assert.Equal(ItemKind.Archive, archived!.Kind);
        Assert.Equal(ItemKind.Resource, archived.PreviousKind);

        var twice = await client.PostAsync($"/api/items/{p.Id}/archive", null);
        Assert.Equal(HttpStatusCode.BadRequest, twice.StatusCode);

        var restored = await (await client.PostAsync($"/api/items/{p.Id}/restore", null)).Content.ReadFromJsonAsync<ItemSummaryDto>(TestJson.Options);
        Assert.Equal(ItemKind.Resource, restored!.Kind);
        Assert.Null(restored.PreviousKind);

        var notArchived = await client.PostAsync($"/api/items/{p.Id}/restore", null);
        Assert.Equal(HttpStatusCode.BadRequest, notArchived.StatusCode);
    }

    [Fact]
    public async Task Put_ArchivedItem_CanEditFields_ButCannotChangeKind()
    {
        using var client = factory.CreateClient();
        var p = await CreateAsync(client, NewItem(ItemKind.Project, "보관 후 수정"));
        await client.PostAsync($"/api/items/{p.Id}/archive", null);

        // 보관 상태에서 Kind=Archive 로 보내면 제목·상태 수정이 가능해야 한다.
        var ok = await client.PutAsJsonAsync($"/api/items/{p.Id}",
            new UpsertItemRequest(ItemKind.Archive, "보관 중 제목 수정", null, ItemStatus.Done, null, null, null, 0), TestJson.Options);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var edited = (await ok.Content.ReadFromJsonAsync<ItemSummaryDto>(TestJson.Options))!;
        Assert.Equal("보관 중 제목 수정", edited.Title);
        Assert.Equal(ItemKind.Archive, edited.Kind);
        Assert.Equal(ItemKind.Project, edited.PreviousKind); // PreviousKind 는 PUT 으로 바뀌지 않는다

        var res = await client.PutAsJsonAsync($"/api/items/{p.Id}", NewItem(ItemKind.Resource, "종류 변경 시도"), TestJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task ArchivedArea_RemainsValidParent_ButKindChangeIsRejectedWhileReferenced()
    {
        using var client = factory.CreateClient();
        var area = await CreateAsync(client, NewItem(ItemKind.Area, "참조되는 영역"));
        var project = await CreateAsync(client, NewItem(ItemKind.Project, "영역 소속 프로젝트", area.Id));

        // 보관된 영역은 여전히 유효한 소속 대상이다(과거 소속 사실 보존).
        var archived = await client.PostAsync($"/api/items/{area.Id}/archive", null);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        var stillOk = await client.PutAsJsonAsync($"/api/items/{project.Id}", NewItem(ItemKind.Project, "영역 소속 프로젝트", area.Id), TestJson.Options);
        Assert.Equal(HttpStatusCode.OK, stillOk.StatusCode);
        var newChild = await client.PostAsJsonAsync("/api/items", NewItem(ItemKind.Resource, "보관 영역에 새 자원", area.Id), TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, newChild.StatusCode);

        // 복원 후 영역을 Project 로 바꾸려 하면 참조가 있으므로 거부된다.
        await client.PostAsync($"/api/items/{area.Id}/restore", null);
        var kindChange = await client.PutAsJsonAsync($"/api/items/{area.Id}", NewItem(ItemKind.Project, "영역→프로젝트"), TestJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, kindChange.StatusCode);
    }

    [Fact]
    public async Task Delete_RemovesItem_Then404()
    {
        using var client = factory.CreateClient();
        var p = await CreateAsync(client, NewItem(ItemKind.Project, "삭제 대상"));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/items/{p.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/items/{p.Id}")).StatusCode);
    }

    [Fact]
    public async Task WriteEndpoints_Return403_WhenPolicyDenies()
    {
        using var client = factory.CreateClient();
        var p = await CreateAsync(client, NewItem(ItemKind.Project, "403 검증"));
        factory.WriteAccess.Allow = false;
        try
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/items", NewItem(ItemKind.Project, "x"), TestJson.Options)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/items/{p.Id}", NewItem(ItemKind.Project, "x"), TestJson.Options)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/items/{p.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/items/{p.Id}/archive", null)).StatusCode);
            // 읽기는 계속 허용
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/items/{p.Id}")).StatusCode);
        }
        finally
        {
            factory.WriteAccess.Allow = true;
        }
    }
}
```

- [ ] **Step 2: 실패 확인**

Run: `dotnet test WebProject.Api.Tests --filter "FullyQualifiedName~ItemEndpointsTests"`
Expected: 컴파일 오류(Contracts 없음).

- [ ] **Step 3: Contracts 작성** — `WebProject.Api/Contracts/` (각 record에 XML 주석 템플릿: Thread-safe 불변 / 인스턴스 1개 + 배열 / 즉시 반환)

```csharp
// ValidationErrors.cs
namespace WebProject.Api.Contracts;

/// <summary>필드별 검증 오류를 모아 <c>TypedResults.ValidationProblem</c>에 넘길 사전을 만든다.</summary>
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
// ItemDtos.cs
using WebProject.Api.Domain;
namespace WebProject.Api.Contracts;

public sealed record ItemSummaryDto(Guid Id, ItemKind Kind, ItemKind? PreviousKind, string Title, string Description, ItemStatus Status,
    DateOnly? DueDate, Guid? AreaId, string? AreaTitle, string[] Tags, int SortOrder, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record ItemDetailDto(ItemSummaryDto Item, NoteSummaryDto[] Notes, TaskDto[] Tasks);

/// <summary>생성·수정 공용 요청. TagNames는 null이면 태그 없음으로 간주(전체 교체).</summary>
public sealed record UpsertItemRequest(ItemKind Kind, string Title, string? Description, ItemStatus Status,
    DateOnly? DueDate, Guid? AreaId, string[]? TagNames, int SortOrder);
```

```csharp
// NoteDtos.cs
using WebProject.Api.Domain;
namespace WebProject.Api.Contracts;

public sealed record NoteSummaryDto(Guid Id, string Title, string Excerpt, Guid[] ItemIds, string[] Tags, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record NoteItemRefDto(Guid Id, ItemKind Kind, string Title);
public sealed record NoteDetailDto(Guid Id, string Title, string ContentMarkdown, NoteItemRefDto[] Items, string[] Tags, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
/// <summary>생성·수정 공용 요청. ItemIds·TagNames는 연결을 통째로 교체한다(null = 빈 배열).</summary>
public sealed record UpsertNoteRequest(string Title, string? ContentMarkdown, Guid[]? ItemIds, string[]? TagNames);
/// <summary>노트 목록 페이지. Total 은 필터 적용 후 전체 건수.</summary>
public sealed record PagedNotesDto(NoteSummaryDto[] Items, int Total);
```

```csharp
// TaskDtos.cs
namespace WebProject.Api.Contracts;

public sealed record TaskDto(Guid Id, string Title, bool IsDone, DateTimeOffset? CompletedAt, DateOnly? DueDate, Guid? ItemId, int SortOrder, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
/// <summary>IsDone 은 멱등 경로(PUT)로 완료 상태를 지정할 때 쓴다. toggle 은 편의용 비멱등 경로.</summary>
public sealed record UpsertTaskRequest(string Title, DateOnly? DueDate, Guid? ItemId, int SortOrder, bool IsDone = false);
```

```csharp
// TagDtos.cs
namespace WebProject.Api.Contracts;
public sealed record TagDto(Guid Id, string Name, string? Color, int ItemCount, int NoteCount);
```

- [ ] **Step 4: TagResolver · DtoMapping** — `WebProject.Api/Infrastructure/Data/`

```csharp
// TagResolver.cs
using Microsoft.EntityFrameworkCore;
using WebProject.Api.Contracts;
using WebProject.Api.Domain;

namespace WebProject.Api.Infrastructure.Data;

/// <summary>태그 이름 목록을 정규화·중복 제거하고 기존 태그는 조회, 없는 태그는 추적 컨텍스트에 추가(저장은 호출자)해 입력 순서대로 반환한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. 전달된 DbContext의 스코프 안에서만 호출한다.</description></item>
/// <item><description><b>Memory Allocation:</b> 정규화 문자열·결과 리스트 할당. 태그 수(수십 개)만큼만.</description></item>
/// <item><description><b>Blocking:</b> Non-blocking. DB 조회 1회를 await 한다. 같은 요청에서 새 태그를 두 번 만들지 않도록 로컬 추적 항목도 검색한다.</description></item>
/// </list>
/// </remarks>
public static class TagResolver
{
    public const int MaxLength = 50;

    public static string Normalize(string name) =>
        string.Join(' ', name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    /// <summary>정규화 후 50자를 넘는 태그가 있으면 <paramref name="errors"/>에 추가한다(DB 제약 위반 → 500 대신 400).</summary>
    public static void Validate(IEnumerable<string>? names, ValidationErrors errors, string field)
    {
        if (names is null) return;
        foreach (var raw in names)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (Normalize(raw).Length > MaxLength)
                errors.Add(field, $"태그는 {MaxLength}자 이하여야 합니다: {raw}");
        }
    }

    public static async Task<List<Tag>> ResolveAsync(AppDbContext db, IEnumerable<string>? names, CancellationToken ct)
    {
        var result = new List<Tag>();
        if (names is null) return result;

        var wanted = new List<(string Display, string Normalized)>();
        foreach (var raw in names)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var display = string.Join(' ', raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
            var normalized = display.ToLowerInvariant();
            if (wanted.Any(w => w.Normalized == normalized)) continue;
            wanted.Add((display, normalized));
        }
        if (wanted.Count == 0) return result;

        var keys = wanted.Select(w => w.Normalized).ToArray();
        var existing = await db.Tags.Where(t => keys.Contains(t.NormalizedName)).ToListAsync(ct);
        foreach (var (display, normalized) in wanted)
        {
            var tag = existing.FirstOrDefault(t => t.NormalizedName == normalized)
                ?? db.Tags.Local.FirstOrDefault(t => t.NormalizedName == normalized);
            if (tag is null)
            {
                tag = new Tag { Name = display, NormalizedName = normalized };
                db.Tags.Add(tag);
            }
            result.Add(tag);
        }
        return result;
    }
}
```

```csharp
// DtoMapping.cs
using WebProject.Api.Contracts;
using WebProject.Api.Domain;

namespace WebProject.Api.Infrastructure.Data;

/// <summary>엔티티 → DTO 변환. 네비게이션(Area, ItemTags.Tag, NoteTags.Tag, ItemLinks)은 호출자가 Include로 로드해야 한다.</summary>
public static class DtoMapping
{
    private const int ExcerptLength = 200;

    public static ItemSummaryDto ToSummary(this Item i) => new(
        i.Id, i.Kind, i.PreviousKind, i.Title, i.Description, i.Status, i.DueDate, i.AreaId, i.Area?.Title,
        SortedTags(i.ItemTags.Select(t => t.Tag)), i.SortOrder, i.CreatedAt, i.UpdatedAt);

    public static NoteSummaryDto ToSummary(this Note n) => new(
        n.Id, n.Title, Excerpt(n.ContentMarkdown), n.ItemLinks.Select(l => l.ItemId).Order().ToArray(),
        SortedTags(n.NoteTags.Select(t => t.Tag)), n.CreatedAt, n.UpdatedAt);

    public static NoteDetailDto ToDetail(this Note n) => new(
        n.Id, n.Title, n.ContentMarkdown,
        n.ItemLinks.Select(l => new NoteItemRefDto(l.Item.Id, l.Item.Kind, l.Item.Title)).OrderBy(r => r.Title, StringComparer.Ordinal).ThenBy(r => r.Id).ToArray(),
        SortedTags(n.NoteTags.Select(t => t.Tag)), n.CreatedAt, n.UpdatedAt);

    /// <summary>다대다 링크는 DB가 순서를 보장하지 않으므로 응답은 항상 정규화 이름 순으로 정렬한다(테스트·클라이언트가 의존하는 계약).</summary>
    private static string[] SortedTags(IEnumerable<Tag> tags) =>
        tags.OrderBy(t => t.NormalizedName, StringComparer.Ordinal).Select(t => t.Name).ToArray();

    public static TaskDto ToDto(this TaskItem t) => new(
        t.Id, t.Title, t.IsDone, t.CompletedAt, t.DueDate, t.ItemId, t.SortOrder, t.CreatedAt, t.UpdatedAt);

    private static string Excerpt(string markdown)
    {
        var s = markdown.AsSpan().Trim();
        return s.Length <= ExcerptLength ? s.ToString() : string.Concat(s[..ExcerptLength], "…");
    }
}
```

```csharp
// UniqueViolation.cs
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace WebProject.Api.Infrastructure.Data;

/// <summary>"조회 후 없으면 추가" 경쟁(태그 동시 생성, 같은 해시 첨부 동시 업로드)으로 유니크 인덱스에 걸렸는지 판정한다.</summary>
/// <remarks>단일 사용자라도 여러 탭·병렬 업로드로 발생할 수 있다. 호출자는 true 이면 409 ProblemDetails("다시 시도")로 응답한다.
/// 재시도를 서버가 대신 하지 않는 이유: 실패한 SaveChanges 뒤 변경 추적기 상태 정리가 복잡하고, 클라이언트 재시도가 더 단순·투명하다.</remarks>
public static class UniqueViolation
{
    public const string PostgresUniqueViolationState = "23505";

    public static bool Is(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresUniqueViolationState };

    public static ProblemHttpResult Conflict() => TypedResults.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "동시 생성 충돌",
        detail: "같은 이름의 태그 또는 같은 내용의 첨부가 동시에 만들어졌습니다. 요청을 다시 보내 주세요.");
}
```

- [ ] **Step 5: Items 검증 + 엔드포인트** — `WebProject.Api/Features/Items/`

```csharp
// ItemValidation.cs
using Microsoft.EntityFrameworkCore;
using WebProject.Api.Contracts;
using WebProject.Api.Domain;
using WebProject.Api.Infrastructure.Data;

namespace WebProject.Api.Features.Items;

/// <summary>UpsertItemRequest의 형식·의미 검증. 오류가 있으면 필드별 메시지를 반환한다.</summary>
public static class ItemValidation
{
    public const int TitleMax = 200;
    public const int DescriptionMax = 2000;

    /// <param name="existing">수정 시 대상 엔티티, 생성 시 null</param>
    /// <remarks>
    /// Kind 규칙: 생성 시 Archive 금지. 수정 시 기존이 Archive 면 요청 Kind 도 Archive 여야 하고(필드만 수정), 기존이 Archive 가 아니면 요청 Kind 는 Archive 가 될 수 없다(보관은 /archive).
    /// 영역 불변식: AreaId 는 "Kind=Area" 또는 "Kind=Archive && PreviousKind=Area"(보관된 영역) 인 항목만 가리킨다. 다른 항목이 참조 중인 영역은 Area 가 아닌 Kind 로 바꿀 수 없다.
    /// </remarks>
    public static async Task<ValidationErrors> ValidateAsync(AppDbContext db, UpsertItemRequest req, Item? existing, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (!Enum.IsDefined(req.Kind)) errors.Add(nameof(req.Kind), "정의되지 않은 Kind 입니다.");
        if (!Enum.IsDefined(req.Status)) errors.Add(nameof(req.Status), "정의되지 않은 Status 입니다.");
        if (string.IsNullOrWhiteSpace(req.Title)) errors.Add(nameof(req.Title), "제목은 비울 수 없습니다.");
        else if (req.Title.Trim().Length > TitleMax) errors.Add(nameof(req.Title), $"제목은 {TitleMax}자 이하여야 합니다.");
        if ((req.Description?.Length ?? 0) > DescriptionMax) errors.Add(nameof(req.Description), $"설명은 {DescriptionMax}자 이하여야 합니다.");
        TagResolver.Validate(req.TagNames, errors, nameof(req.TagNames));

        var isArchived = existing is { Kind: ItemKind.Archive };
        if (existing is null && req.Kind == ItemKind.Archive)
            errors.Add(nameof(req.Kind), "Archive 종류로 직접 만들 수 없습니다. 생성 후 /archive 를 호출하세요.");
        if (existing is not null && !isArchived && req.Kind == ItemKind.Archive)
            errors.Add(nameof(req.Kind), "보관은 /archive 엔드포인트로 수행합니다.");
        if (isArchived && req.Kind != ItemKind.Archive)
            errors.Add(nameof(req.Kind), "보관된 항목의 종류는 복원 후에만 바꿀 수 있습니다.");

        // 유효 Kind = 요청 Kind, 단 보관 항목은 PreviousKind 기준으로 영역 여부를 판단한다.
        var effectiveKind = isArchived ? existing!.PreviousKind : req.Kind;
        if (effectiveKind == ItemKind.Area && req.AreaId is not null)
            errors.Add(nameof(req.AreaId), "영역은 다른 영역에 속할 수 없습니다.");

        // 참조 중인 영역의 종류 변경 금지(보관은 허용: 보관된 영역도 유효한 소속 대상).
        if (existing is not null && existing.Kind == ItemKind.Area && req.Kind != ItemKind.Area
            && await db.Items.AnyAsync(i => i.AreaId == existing.Id, ct))
            errors.Add(nameof(req.Kind), "다른 항목이 소속된 영역의 종류는 바꿀 수 없습니다. 먼저 소속을 해제하세요.");

        if (req.AreaId is Guid areaId)
        {
            if (existing is not null && areaId == existing.Id)
                errors.Add(nameof(req.AreaId), "자기 자신을 영역으로 지정할 수 없습니다.");
            else if (!await db.Items.AnyAsync(i => i.Id == areaId
                         && (i.Kind == ItemKind.Area || (i.Kind == ItemKind.Archive && i.PreviousKind == ItemKind.Area)), ct))
                errors.Add(nameof(req.AreaId), "AreaId는 영역(또는 보관된 영역)이어야 합니다.");
        }
        return errors;
    }
}
```

```csharp
// ItemEndpoints.cs
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using WebProject.Api.Contracts;
using WebProject.Api.Domain;
using WebProject.Api.Infrastructure.Data;

namespace WebProject.Api.Features.Items;

/// <summary>/api/items 읽기·쓰기 엔드포인트.</summary>
public static class ItemEndpoints
{
    public static void MapItemEndpoints(RouteGroupBuilder read, RouteGroupBuilder write)
    {
        read.MapGet("/items", ListAsync).WithName("ListItems");
        read.MapGet("/items/{id:guid}", GetAsync).WithName("GetItem");
        write.MapPost("/items", CreateAsync).WithName("CreateItem");
        write.MapPut("/items/{id:guid}", UpdateAsync).WithName("UpdateItem");
        write.MapDelete("/items/{id:guid}", DeleteAsync).WithName("DeleteItem");
        write.MapPost("/items/{id:guid}/archive", ArchiveAsync).WithName("ArchiveItem");
        write.MapPost("/items/{id:guid}/restore", RestoreAsync).WithName("RestoreItem");
    }

    private static IQueryable<Item> WithSummaryIncludes(AppDbContext db) =>
        db.Items.AsNoTracking().Include(i => i.Area).Include(i => i.ItemTags).ThenInclude(t => t.Tag);

    private static async Task<Ok<ItemSummaryDto[]>> ListAsync(AppDbContext db, ItemKind? kind, Guid? areaId, string? tag, CancellationToken ct)
    {
        var q = WithSummaryIncludes(db);
        if (kind is not null) q = q.Where(i => i.Kind == kind);
        if (areaId is not null) q = q.Where(i => i.AreaId == areaId);
        if (!string.IsNullOrWhiteSpace(tag))
        {
            var norm = TagResolver.Normalize(tag);
            q = q.Where(i => i.ItemTags.Any(t => t.Tag.NormalizedName == norm));
        }
        var items = await q.OrderBy(i => i.Kind).ThenBy(i => i.SortOrder).ThenBy(i => i.Title).ToListAsync(ct);
        return TypedResults.Ok(items.Select(i => i.ToSummary()).ToArray());
    }

    private static async Task<Results<Ok<ItemDetailDto>, NotFound>> GetAsync(AppDbContext db, Guid id, CancellationToken ct)
    {
        var item = await WithSummaryIncludes(db).FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item is null) return TypedResults.NotFound();

        var notes = await db.Notes.AsNoTracking()
            .Where(n => n.ItemLinks.Any(l => l.ItemId == id))
            .Include(n => n.ItemLinks).Include(n => n.NoteTags).ThenInclude(t => t.Tag)
            .OrderByDescending(n => n.UpdatedAt).ToListAsync(ct);
        var tasks = await db.Tasks.AsNoTracking().Where(t => t.ItemId == id)
            .OrderBy(t => t.IsDone).ThenBy(t => t.SortOrder).ThenBy(t => t.DueDate).ToListAsync(ct);

        return TypedResults.Ok(new ItemDetailDto(item.ToSummary(), notes.Select(n => n.ToSummary()).ToArray(), tasks.Select(t => t.ToDto()).ToArray()));
    }

    private static async Task<Results<Created<ItemSummaryDto>, ValidationProblem, ProblemHttpResult>> CreateAsync(AppDbContext db, UpsertItemRequest req, CancellationToken ct)
    {
        var errors = await ItemValidation.ValidateAsync(db, req, existing: null, ct);
        if (errors.Any) return TypedResults.ValidationProblem(errors.ToDictionary());

        var now = DateTimeOffset.UtcNow;
        var item = new Item { CreatedAt = now };
        Apply(item, req, now);
        item.ItemTags.AddRange((await TagResolver.ResolveAsync(db, req.TagNames, ct)).Select(t => new ItemTag { Item = item, Tag = t }));
        db.Items.Add(item);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (UniqueViolation.Is(ex)) { return UniqueViolation.Conflict(); }

        var created = await WithSummaryIncludes(db).FirstAsync(i => i.Id == item.Id, ct);
        return TypedResults.Created($"/api/items/{item.Id}", created.ToSummary());
    }

    private static async Task<Results<Ok<ItemSummaryDto>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateAsync(AppDbContext db, Guid id, UpsertItemRequest req, CancellationToken ct)
    {
        var item = await db.Items.Include(i => i.ItemTags).FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item is null) return TypedResults.NotFound();

        var errors = await ItemValidation.ValidateAsync(db, req, item, ct);
        if (errors.Any) return TypedResults.ValidationProblem(errors.ToDictionary());

        Apply(item, req, DateTimeOffset.UtcNow);
        // Clear() 후 같은 (ItemId, TagId)를 다시 Add하면 EF가 Deleted 항목과 키 충돌을 일으키므로 차집합으로 동기화한다.
        var tags = await TagResolver.ResolveAsync(db, req.TagNames, ct);
        var desired = tags.Select(t => t.Id).ToHashSet();
        item.ItemTags.RemoveAll(link => !desired.Contains(link.TagId));
        foreach (var tag in tags)
        {
            if (!item.ItemTags.Any(link => link.TagId == tag.Id)) item.ItemTags.Add(new ItemTag { Item = item, Tag = tag });
        }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (UniqueViolation.Is(ex)) { return UniqueViolation.Conflict(); }

        var updated = await WithSummaryIncludes(db).FirstAsync(i => i.Id == id, ct);
        return TypedResults.Ok(updated.ToSummary());
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(AppDbContext db, Guid id, CancellationToken ct)
    {
        var deleted = await db.Items.Where(i => i.Id == id).ExecuteDeleteAsync(ct);
        return deleted == 0 ? TypedResults.NotFound() : TypedResults.NoContent();
    }

    private static async Task<Results<Ok<ItemSummaryDto>, NotFound, ProblemHttpResult>> ArchiveAsync(AppDbContext db, Guid id, CancellationToken ct)
    {
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item is null) return TypedResults.NotFound();
        if (item.Kind == ItemKind.Archive)
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "이미 보관된 항목입니다.");

        item.PreviousKind = item.Kind;
        item.Kind = ItemKind.Archive;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok((await WithSummaryIncludes(db).FirstAsync(i => i.Id == id, ct)).ToSummary());
    }

    private static async Task<Results<Ok<ItemSummaryDto>, NotFound, ProblemHttpResult>> RestoreAsync(AppDbContext db, Guid id, CancellationToken ct)
    {
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item is null) return TypedResults.NotFound();
        if (item.Kind != ItemKind.Archive)
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "보관되지 않은 항목입니다.");

        item.Kind = item.PreviousKind ?? ItemKind.Resource;
        item.PreviousKind = null;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok((await WithSummaryIncludes(db).FirstAsync(i => i.Id == id, ct)).ToSummary());
    }

    private static void Apply(Item item, UpsertItemRequest req, DateTimeOffset now)
    {
        // 보관 항목은 검증에서 Kind 변경이 막히므로 여기서는 Archive가 아닐 때만 Kind를 반영한다.
        if (item.Kind != ItemKind.Archive) item.Kind = req.Kind;
        item.Title = req.Title.Trim();
        item.Description = req.Description?.Trim() ?? string.Empty;
        item.Status = req.Status;
        item.DueDate = req.DueDate;
        item.AreaId = req.AreaId;
        item.SortOrder = req.SortOrder;
        item.UpdatedAt = now;
    }
}
```

`Features/ApiEndpoints.cs`의 `MeEndpoints.MapMeEndpoints(read, write);` 아래에 `ItemEndpoints.MapItemEndpoints(read, write);` 추가(using `WebProject.Api.Features.Items`).

- [ ] **Step 6: 테스트 통과 확인**

Run: `dotnet test WebProject.Api.Tests --filter "FullyQualifiedName~ItemEndpointsTests"`
Expected: 15개 PASS. (반환 유니온 `Results<...>`에 결과 타입이 빠지면 500이 아니라 **컴파일 오류**가 난다.)
그다음 `ForwardedHeadersTests.WriteWithoutCsrfHeader_Returns403_EvenFromAllowedIp`의 `Skip`을 제거하고 `dotnet test WebProject.Api.Tests --filter "FullyQualifiedName~ForwardedHeadersTests"` → 4개 PASS.

- [ ] **Step 7: 전체 테스트 + 커밋**

Run: `dotnet test WebProject.sln` → 전부 PASS.

```bash
git add -A
git commit -m "추가: PARA 항목(Item) CRUD·보관·복원 API와 태그 해석기

- Contracts DTO, DtoMapping, TagResolver(정규화·멱등 생성)
- Kind=Archive 직접 생성 금지, 영역 자기참조·비영역 AreaId 검증

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: Tags 엔드포인트

**Files:**
- Create: `WebProject.Api/Features/Tags/TagEndpoints.cs`
- Modify: `WebProject.Api/Features/ApiEndpoints.cs`
- Test: `WebProject.Api.Tests/Features/TagEndpointsTests.cs`

**Interfaces:**
- Consumes: `TagDto`, `AppDbContext`, `UpsertItemRequest`(테스트 데이터 생성용).
- Produces: `GET /api/tags → TagDto[]`(이름 오름차순), `DELETE /api/tags/{id} → 204|404`.

- [ ] **Step 1: 실패하는 테스트**

```csharp
using System.Net;
using System.Net.Http.Json;
using WebProject.Api.Contracts;
using WebProject.Api.Domain;
using WebProject.Api.Tests.Infrastructure;

namespace WebProject.Api.Tests.Features;

[Collection("postgres")]
public sealed class TagEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task List_ReturnsTagsWithCounts_CaseInsensitiveMerge()
    {
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/items", new UpsertItemRequest(ItemKind.Project, "태그 A", null, ItemStatus.Active, null, null, ["Dotnet", "web"], 0), TestJson.Options);
        await client.PostAsJsonAsync("/api/items", new UpsertItemRequest(ItemKind.Resource, "태그 B", null, ItemStatus.Active, null, null, ["dotnet"], 0), TestJson.Options);

        var tags = await client.GetFromJsonAsync<TagDto[]>("/api/tags", TestJson.Options);
        var dotnet = Assert.Single(tags!, t => t.Name == "Dotnet");   // 최초 표기 유지
        Assert.Equal(2, dotnet.ItemCount);
        Assert.Equal(0, dotnet.NoteCount);
        Assert.DoesNotContain(tags!, t => t.Name == "dotnet");
    }

    [Fact]
    public async Task Delete_RemovesTag_AndDetachesFromItems()
    {
        using var client = factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/items", new UpsertItemRequest(ItemKind.Project, "삭제 태그", null, ItemStatus.Active, null, null, ["지울태그"], 0), TestJson.Options);
        var item = (await res.Content.ReadFromJsonAsync<ItemSummaryDto>(TestJson.Options))!;
        var tag = (await client.GetFromJsonAsync<TagDto[]>("/api/tags", TestJson.Options))!.Single(t => t.Name == "지울태그");

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/tags/{tag.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/tags/{tag.Id}")).StatusCode);

        var reloaded = await client.GetFromJsonAsync<ItemDetailDto>($"/api/items/{item.Id}", TestJson.Options);
        Assert.DoesNotContain("지울태그", reloaded!.Item.Tags);
    }
}
```

- [ ] **Step 2: 실패 확인** — Run: `dotnet test WebProject.Api.Tests --filter "FullyQualifiedName~TagEndpointsTests"` → 404로 FAIL.

- [ ] **Step 3: 구현**

```csharp
// Features/Tags/TagEndpoints.cs
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using WebProject.Api.Contracts;
using WebProject.Api.Infrastructure.Data;

namespace WebProject.Api.Features.Tags;

/// <summary>/api/tags 목록·삭제. 생성은 항목·노트 저장 시 이름으로 자동 수행된다.</summary>
public static class TagEndpoints
{
    public static void MapTagEndpoints(RouteGroupBuilder read, RouteGroupBuilder write)
    {
        read.MapGet("/tags", ListAsync).WithName("ListTags");
        write.MapDelete("/tags/{id:guid}", DeleteAsync).WithName("DeleteTag");
    }

    private static async Task<Ok<TagDto[]>> ListAsync(AppDbContext db, CancellationToken ct)
    {
        var tags = await db.Tags.AsNoTracking()
            .OrderBy(t => t.NormalizedName)
            .Select(t => new TagDto(t.Id, t.Name, t.Color, t.ItemTags.Count(), t.NoteTags.Count()))
            .ToArrayAsync(ct);
        return TypedResults.Ok(tags);
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(AppDbContext db, Guid id, CancellationToken ct)
    {
        var deleted = await db.Tags.Where(t => t.Id == id).ExecuteDeleteAsync(ct);
        return deleted == 0 ? TypedResults.NotFound() : TypedResults.NoContent();
    }
}
```
`ApiEndpoints.cs`에 `TagEndpoints.MapTagEndpoints(read, write);` 추가.

- [ ] **Step 4: 통과 확인 + 커밋**

Run: `dotnet test WebProject.sln` → 전부 PASS.

```bash
git add -A
git commit -m "추가: 태그 목록·삭제 API (항목·노트 사용 수 포함)

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: Notes 엔드포인트

**Files:**
- Create: `WebProject.Api/Features/Notes/NoteEndpoints.cs`, `NoteValidation.cs`
- Modify: `WebProject.Api/Features/ApiEndpoints.cs`
- Test: `WebProject.Api.Tests/Features/NoteEndpointsTests.cs`

**Interfaces:**
- Consumes: `UpsertNoteRequest`, `NoteSummaryDto`, `NoteDetailDto`, `TagResolver`, `DtoMapping`.
- Produces: `GET /api/notes?itemId=&tag=&q=&inbox=&skip=&take=` → `PagedNotesDto`(UpdatedAt 내림차순·Id 보조 정렬, take 기본 50·최대 200, skip 기본 0), `GET /api/notes/{id}`, `POST /api/notes`(201), `PUT /api/notes/{id}`, `DELETE`. `q` 의 `%`·`_`·`\` 는 이스케이프해 문자 그대로 검색한다. 유니크 충돌 → 409.

- [ ] **Step 1: 실패하는 테스트**

```csharp
using System.Net;
using System.Net.Http.Json;
using WebProject.Api.Contracts;
using WebProject.Api.Domain;
using WebProject.Api.Tests.Infrastructure;

namespace WebProject.Api.Tests.Features;

[Collection("postgres")]
public sealed class NoteEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static async Task<ItemSummaryDto> CreateItemAsync(HttpClient client, ItemKind kind, string title)
    {
        var res = await client.PostAsJsonAsync("/api/items", new UpsertItemRequest(kind, title, null, ItemStatus.Active, null, null, null, 0), TestJson.Options);
        return (await res.Content.ReadFromJsonAsync<ItemSummaryDto>(TestJson.Options))!;
    }

    private static async Task<NoteDetailDto> CreateNoteAsync(HttpClient client, UpsertNoteRequest req)
    {
        var res = await client.PostAsJsonAsync("/api/notes", req, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<NoteDetailDto>(TestJson.Options))!;
    }

    [Fact]
    public async Task Post_LinksToMultipleItems_AndTags()
    {
        using var client = factory.CreateClient();
        var project = await CreateItemAsync(client, ItemKind.Project, "P1");
        var area = await CreateItemAsync(client, ItemKind.Area, "A1");

        var note = await CreateNoteAsync(client, new UpsertNoteRequest("회의록", "# 본문\n내용", [project.Id, area.Id], ["회의"]));

        Assert.Equal(2, note.Items.Length);
        Assert.Contains(note.Items, i => i.Id == project.Id && i.Kind == ItemKind.Project);
        Assert.Contains(note.Items, i => i.Id == area.Id && i.Kind == ItemKind.Area);
        Assert.Equal(["회의"], note.Tags);
    }

    [Fact]
    public async Task Post_UnknownItemId_IsRejected()
    {
        using var client = factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/notes", new UpsertNoteRequest("잘못된 링크", "", [Guid.NewGuid()], null), TestJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Get_List_FiltersByItem_Inbox_Tag_AndQuery()
    {
        using var client = factory.CreateClient();
        var item = await CreateItemAsync(client, ItemKind.Resource, "R1");
        var linked = await CreateNoteAsync(client, new UpsertNoteRequest("연결된 노트", "고유한단어ABC", [item.Id], ["목록태그"]));
        var inbox = await CreateNoteAsync(client, new UpsertNoteRequest("인박스 노트", "다른 내용", null, null));

        var byItem = (await client.GetFromJsonAsync<PagedNotesDto>($"/api/notes?itemId={item.Id}", TestJson.Options))!.Items;
        Assert.Single(byItem, n => n.Id == linked.Id);
        Assert.DoesNotContain(byItem, n => n.Id == inbox.Id);

        var inboxOnly = (await client.GetFromJsonAsync<PagedNotesDto>("/api/notes?inbox=true", TestJson.Options))!.Items;
        Assert.Contains(inboxOnly, n => n.Id == inbox.Id);
        Assert.DoesNotContain(inboxOnly, n => n.Id == linked.Id);

        var byTag = (await client.GetFromJsonAsync<PagedNotesDto>("/api/notes?tag=목록태그", TestJson.Options))!.Items;
        Assert.Single(byTag, n => n.Id == linked.Id);

        var byQuery = (await client.GetFromJsonAsync<PagedNotesDto>("/api/notes?q=단어abc", TestJson.Options))!.Items;
        Assert.Single(byQuery, n => n.Id == linked.Id);
    }

    [Fact]
    public async Task Get_List_Paginates_AndEscapesLikeWildcards()
    {
        using var client = factory.CreateClient();
        var item = await CreateItemAsync(client, ItemKind.Area, "페이지 영역");
        for (var i = 0; i < 5; i++)
            await CreateNoteAsync(client, new UpsertNoteRequest($"페이지 노트 {i}", i == 0 ? "100% 확실" : "100점 만점", [item.Id], null));

        var page1 = await client.GetFromJsonAsync<PagedNotesDto>($"/api/notes?itemId={item.Id}&skip=0&take=2", TestJson.Options);
        var page2 = await client.GetFromJsonAsync<PagedNotesDto>($"/api/notes?itemId={item.Id}&skip=2&take=2", TestJson.Options);
        Assert.Equal(5, page1!.Total);
        Assert.Equal(2, page1.Items.Length);
        Assert.Equal(2, page2!.Items.Length);
        Assert.Empty(page1.Items.Select(n => n.Id).Intersect(page2.Items.Select(n => n.Id)));

        var tooBig = await client.GetFromJsonAsync<PagedNotesDto>($"/api/notes?itemId={item.Id}&take=9999", TestJson.Options);
        Assert.Equal(5, tooBig!.Items.Length); // take 는 200 으로 잘리고 Total 은 그대로

        // '%' 를 문자 그대로 검색. 다른 4개 노트도 "100"을 포함하므로 이스케이프가 없으면(%100%% 패턴) 5개 전부 매칭되어 실패한다.
        var escaped = await client.GetFromJsonAsync<PagedNotesDto>($"/api/notes?itemId={item.Id}&q=100%25", TestJson.Options);
        Assert.Single(escaped!.Items);
    }

    [Fact]
    public async Task Put_ReplacesLinksAndTags_And_Delete_Then404()
    {
        using var client = factory.CreateClient();
        var a = await CreateItemAsync(client, ItemKind.Project, "PA");
        var b = await CreateItemAsync(client, ItemKind.Project, "PB");
        var note = await CreateNoteAsync(client, new UpsertNoteRequest("교체 전", "x", [a.Id], ["t1"]));

        var res = await client.PutAsJsonAsync($"/api/notes/{note.Id}", new UpsertNoteRequest("교체 후", "y", [b.Id], ["t2"]), TestJson.Options);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var updated = (await res.Content.ReadFromJsonAsync<NoteDetailDto>(TestJson.Options))!;
        Assert.Equal("교체 후", updated.Title);
        Assert.Equal([b.Id], updated.Items.Select(i => i.Id).ToArray());
        Assert.Equal(["t2"], updated.Tags);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/notes/{note.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/notes/{note.Id}")).StatusCode);
    }

    [Fact]
    public async Task DeletingItem_KeepsNote_ButRemovesLink()
    {
        using var client = factory.CreateClient();
        var item = await CreateItemAsync(client, ItemKind.Project, "삭제될 항목");
        var note = await CreateNoteAsync(client, new UpsertNoteRequest("살아남는 노트", "", [item.Id], null));

        await client.DeleteAsync($"/api/items/{item.Id}");

        var reloaded = await client.GetFromJsonAsync<NoteDetailDto>($"/api/notes/{note.Id}", TestJson.Options);
        Assert.Empty(reloaded!.Items);
    }

    [Fact]
    public async Task Excerpt_IsTruncatedTo200Chars()
    {
        using var client = factory.CreateClient();
        var note = await CreateNoteAsync(client, new UpsertNoteRequest("긴 노트", new string('가', 500), null, null));
        var list = (await client.GetFromJsonAsync<PagedNotesDto>("/api/notes?inbox=true", TestJson.Options))!.Items;
        var summary = list.Single(n => n.Id == note.Id);
        Assert.Equal(201, summary.Excerpt.Length); // 200자 + '…'
    }
}
```

- [ ] **Step 2: 실패 확인** — Run: `dotnet test WebProject.Api.Tests --filter "FullyQualifiedName~NoteEndpointsTests"` → 404 FAIL.

- [ ] **Step 3: 구현**

```csharp
// Features/Notes/NoteValidation.cs
using Microsoft.EntityFrameworkCore;
using WebProject.Api.Contracts;
using WebProject.Api.Infrastructure.Data;

namespace WebProject.Api.Features.Notes;

public static class NoteValidation
{
    public const int TitleMax = 200;

    public static async Task<ValidationErrors> ValidateAsync(AppDbContext db, UpsertNoteRequest req, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (string.IsNullOrWhiteSpace(req.Title)) errors.Add(nameof(req.Title), "제목은 비울 수 없습니다.");
        else if (req.Title.Trim().Length > TitleMax) errors.Add(nameof(req.Title), $"제목은 {TitleMax}자 이하여야 합니다.");
        TagResolver.Validate(req.TagNames, errors, nameof(req.TagNames));

        var ids = (req.ItemIds ?? []).Distinct().ToArray();
        if (ids.Length > 0)
        {
            var found = await db.Items.Where(i => ids.Contains(i.Id)).Select(i => i.Id).ToListAsync(ct);
            var missing = ids.Except(found).ToArray();
            if (missing.Length > 0)
                errors.Add(nameof(req.ItemIds), "존재하지 않는 항목: " + string.Join(", ", missing));
        }
        return errors;
    }
}
```

```csharp
// Features/Notes/NoteEndpoints.cs
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using WebProject.Api.Contracts;
using WebProject.Api.Domain;
using WebProject.Api.Infrastructure.Data;

namespace WebProject.Api.Features.Notes;

/// <summary>/api/notes 읽기·쓰기 엔드포인트. 항목 연결·태그는 요청 배열로 통째 교체한다.</summary>
public static class NoteEndpoints
{
    private const int DefaultTake = 50;
    private const int MaxTake = 200;

    public static void MapNoteEndpoints(RouteGroupBuilder read, RouteGroupBuilder write)
    {
        read.MapGet("/notes", ListAsync).WithName("ListNotes");
        read.MapGet("/notes/{id:guid}", GetAsync).WithName("GetNote");
        write.MapPost("/notes", CreateAsync).WithName("CreateNote");
        write.MapPut("/notes/{id:guid}", UpdateAsync).WithName("UpdateNote");
        write.MapDelete("/notes/{id:guid}", DeleteAsync).WithName("DeleteNote");
    }

    private static IQueryable<Note> WithDetailIncludes(AppDbContext db) =>
        db.Notes.AsNoTracking().Include(n => n.ItemLinks).ThenInclude(l => l.Item).Include(n => n.NoteTags).ThenInclude(t => t.Tag);

    private static async Task<Ok<PagedNotesDto>> ListAsync(AppDbContext db, Guid? itemId, string? tag, string? q, bool? inbox, int? skip, int? take, CancellationToken ct)
    {
        var query = db.Notes.AsNoTracking().Include(n => n.ItemLinks).Include(n => n.NoteTags).ThenInclude(t => t.Tag).AsQueryable();
        if (itemId is not null) query = query.Where(n => n.ItemLinks.Any(l => l.ItemId == itemId));
        if (inbox == true) query = query.Where(n => !n.ItemLinks.Any());
        if (!string.IsNullOrWhiteSpace(tag))
        {
            var norm = TagResolver.Normalize(tag);
            query = query.Where(n => n.NoteTags.Any(t => t.Tag.NormalizedName == norm));
        }
        if (!string.IsNullOrWhiteSpace(q))
        {
            // ILIKE: PostgreSQL 대소문자 무시 패턴 검색(1차, tsvector는 확장 포인트). %·_·\ 는 이스케이프해 문자 그대로 찾는다.
            var pattern = "%" + EscapeLike(q.Trim()) + "%";
            query = query.Where(n => EF.Functions.ILike(n.Title, pattern, "\\") || EF.Functions.ILike(n.ContentMarkdown, pattern, "\\"));
        }
        var total = await query.CountAsync(ct);
        var s = Math.Max(0, skip ?? 0);
        var t = Math.Clamp(take ?? DefaultTake, 1, MaxTake);
        // (UpdatedAt, Id) 복합 정렬로 같은 시각의 행이 페이지 사이에서 중복·누락되지 않게 한다.
        var notes = await query.OrderByDescending(n => n.UpdatedAt).ThenByDescending(n => n.Id).Skip(s).Take(t).ToListAsync(ct);
        return TypedResults.Ok(new PagedNotesDto(notes.Select(n => n.ToSummary()).ToArray(), total));
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static async Task<Results<Ok<NoteDetailDto>, NotFound>> GetAsync(AppDbContext db, Guid id, CancellationToken ct)
    {
        var note = await WithDetailIncludes(db).FirstOrDefaultAsync(n => n.Id == id, ct);
        return note is null ? TypedResults.NotFound() : TypedResults.Ok(note.ToDetail());
    }

    private static async Task<Results<Created<NoteDetailDto>, ValidationProblem, ProblemHttpResult>> CreateAsync(AppDbContext db, UpsertNoteRequest req, CancellationToken ct)
    {
        var errors = await NoteValidation.ValidateAsync(db, req, ct);
        if (errors.Any) return TypedResults.ValidationProblem(errors.ToDictionary());

        var now = DateTimeOffset.UtcNow;
        var note = new Note { CreatedAt = now };
        await ApplyAsync(db, note, req, now, ct);
        db.Notes.Add(note);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (UniqueViolation.Is(ex)) { return UniqueViolation.Conflict(); }

        var created = await WithDetailIncludes(db).FirstAsync(n => n.Id == note.Id, ct);
        return TypedResults.Created($"/api/notes/{note.Id}", created.ToDetail());
    }

    private static async Task<Results<Ok<NoteDetailDto>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateAsync(AppDbContext db, Guid id, UpsertNoteRequest req, CancellationToken ct)
    {
        var note = await db.Notes.Include(n => n.ItemLinks).Include(n => n.NoteTags).FirstOrDefaultAsync(n => n.Id == id, ct);
        if (note is null) return TypedResults.NotFound();

        var errors = await NoteValidation.ValidateAsync(db, req, ct);
        if (errors.Any) return TypedResults.ValidationProblem(errors.ToDictionary());

        await ApplyAsync(db, note, req, DateTimeOffset.UtcNow, ct);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (UniqueViolation.Is(ex)) { return UniqueViolation.Conflict(); }

        var updated = await WithDetailIncludes(db).FirstAsync(n => n.Id == id, ct);
        return TypedResults.Ok(updated.ToDetail());
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(AppDbContext db, Guid id, CancellationToken ct)
    {
        var deleted = await db.Notes.Where(n => n.Id == id).ExecuteDeleteAsync(ct);
        return deleted == 0 ? TypedResults.NotFound() : TypedResults.NoContent();
    }

    /// <summary>필드 반영 + 항목 연결·태그를 요청 배열과 차집합으로 동기화한다(Clear 후 재추가 시 EF 키 충돌 방지).</summary>
    private static async Task ApplyAsync(AppDbContext db, Note note, UpsertNoteRequest req, DateTimeOffset now, CancellationToken ct)
    {
        note.Title = req.Title.Trim();
        note.ContentMarkdown = req.ContentMarkdown ?? string.Empty;
        note.UpdatedAt = now;

        var desiredItems = (req.ItemIds ?? []).Distinct().ToHashSet();
        note.ItemLinks.RemoveAll(l => !desiredItems.Contains(l.ItemId));
        foreach (var itemId in desiredItems)
        {
            if (!note.ItemLinks.Any(l => l.ItemId == itemId)) note.ItemLinks.Add(new NoteItemLink { Note = note, ItemId = itemId });
        }

        var tags = await TagResolver.ResolveAsync(db, req.TagNames, ct);
        var desiredTags = tags.Select(t => t.Id).ToHashSet();
        note.NoteTags.RemoveAll(l => !desiredTags.Contains(l.TagId));
        foreach (var tag in tags)
        {
            if (!note.NoteTags.Any(l => l.TagId == tag.Id)) note.NoteTags.Add(new NoteTag { Note = note, Tag = tag });
        }
    }
}
```
`ApiEndpoints.cs`에 `NoteEndpoints.MapNoteEndpoints(read, write);` 추가.

- [ ] **Step 4: 통과 확인 + 커밋**

Run: `dotnet test WebProject.sln` → 전부 PASS.

```bash
git add -A
git commit -m "추가: 노트 CRUD API — 다중 항목 연결·태그 교체·Inbox·페이지네이션·ILIKE 검색

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: Tasks(할 일) 엔드포인트

**Files:**
- Create: `WebProject.Api/Features/Tasks/TaskEndpoints.cs`
- Modify: `WebProject.Api/Features/ApiEndpoints.cs`
- Test: `WebProject.Api.Tests/Features/TaskEndpointsTests.cs`

**Interfaces:**
- Consumes: `TaskDto`, `UpsertTaskRequest`, `DtoMapping.ToDto`.
- Produces: `GET /api/tasks?itemId=&done=`, `GET /api/tasks/{id}`(201 Created 의 Location 대상), `POST /api/tasks`(201), `PUT /api/tasks/{id}`(IsDone 포함, 멱등), `DELETE`, `POST /api/tasks/{id}/toggle`(편의용 비멱등 — 클라이언트는 자동 재시도 금지).

- [ ] **Step 1: 실패하는 테스트**

```csharp
using System.Net;
using System.Net.Http.Json;
using WebProject.Api.Contracts;
using WebProject.Api.Domain;
using WebProject.Api.Tests.Infrastructure;

namespace WebProject.Api.Tests.Features;

[Collection("postgres")]
public sealed class TaskEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static async Task<ItemSummaryDto> CreateItemAsync(HttpClient client, string title)
    {
        var res = await client.PostAsJsonAsync("/api/items", new UpsertItemRequest(ItemKind.Project, title, null, ItemStatus.Active, null, null, null, 0), TestJson.Options);
        return (await res.Content.ReadFromJsonAsync<ItemSummaryDto>(TestJson.Options))!;
    }

    private static async Task<TaskDto> CreateTaskAsync(HttpClient client, UpsertTaskRequest req)
    {
        var res = await client.PostAsJsonAsync("/api/tasks", req, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<TaskDto>(TestJson.Options))!;
    }

    [Fact]
    public async Task Post_CreatesTask_LinkedToItem_And_StandaloneAllowed()
    {
        using var client = factory.CreateClient();
        var item = await CreateItemAsync(client, "할 일 프로젝트");
        var linked = await CreateTaskAsync(client, new UpsertTaskRequest("연결 할 일", new DateOnly(2026, 10, 1), item.Id, 0));
        var standalone = await CreateTaskAsync(client, new UpsertTaskRequest("독립 할 일", null, null, 0));

        Assert.Equal(item.Id, linked.ItemId);
        Assert.False(linked.IsDone);
        Assert.Null(standalone.ItemId);
    }

    [Fact]
    public async Task Post_UnknownItem_IsRejected()
    {
        using var client = factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/tasks", new UpsertTaskRequest("잘못됨", null, Guid.NewGuid(), 0), TestJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Get_ById_ReturnsTask_And404WhenMissing()
    {
        using var client = factory.CreateClient();
        var created = await CreateTaskAsync(client, new UpsertTaskRequest("단건 조회", null, null, 0));
        var fetched = await client.GetFromJsonAsync<TaskDto>($"/api/tasks/{created.Id}", TestJson.Options);
        Assert.Equal(created.Id, fetched!.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/tasks/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Put_IsDone_IsIdempotent_AndSetsCompletedAtOnce()
    {
        using var client = factory.CreateClient();
        var task = await CreateTaskAsync(client, new UpsertTaskRequest("PUT 완료", null, null, 0));

        var first = (await (await client.PutAsJsonAsync($"/api/tasks/{task.Id}", new UpsertTaskRequest("PUT 완료", null, null, 0, IsDone: true), TestJson.Options))
            .Content.ReadFromJsonAsync<TaskDto>(TestJson.Options))!;
        Assert.True(first.IsDone);
        Assert.NotNull(first.CompletedAt);

        var second = (await (await client.PutAsJsonAsync($"/api/tasks/{task.Id}", new UpsertTaskRequest("PUT 완료", null, null, 0, IsDone: true), TestJson.Options))
            .Content.ReadFromJsonAsync<TaskDto>(TestJson.Options))!;
        Assert.Equal(first.CompletedAt, second.CompletedAt); // 이미 완료면 CompletedAt 유지

        var reopened = (await (await client.PutAsJsonAsync($"/api/tasks/{task.Id}", new UpsertTaskRequest("PUT 완료", null, null, 0, IsDone: false), TestJson.Options))
            .Content.ReadFromJsonAsync<TaskDto>(TestJson.Options))!;
        Assert.False(reopened.IsDone);
        Assert.Null(reopened.CompletedAt);
    }

    [Fact]
    public async Task Toggle_FlipsDone_AndSetsCompletedAt()
    {
        using var client = factory.CreateClient();
        var task = await CreateTaskAsync(client, new UpsertTaskRequest("토글", null, null, 0));

        var done = await (await client.PostAsync($"/api/tasks/{task.Id}/toggle", null)).Content.ReadFromJsonAsync<TaskDto>(TestJson.Options);
        Assert.True(done!.IsDone);
        Assert.NotNull(done.CompletedAt);

        var undone = await (await client.PostAsync($"/api/tasks/{task.Id}/toggle", null)).Content.ReadFromJsonAsync<TaskDto>(TestJson.Options);
        Assert.False(undone!.IsDone);
        Assert.Null(undone.CompletedAt);
    }

    [Fact]
    public async Task List_FiltersByItemAndDone_OrderedOpenFirst()
    {
        using var client = factory.CreateClient();
        var item = await CreateItemAsync(client, "목록 프로젝트");
        var t1 = await CreateTaskAsync(client, new UpsertTaskRequest("완료될 것", null, item.Id, 0));
        var t2 = await CreateTaskAsync(client, new UpsertTaskRequest("열린 것", null, item.Id, 1));
        await client.PostAsync($"/api/tasks/{t1.Id}/toggle", null);

        var all = await client.GetFromJsonAsync<TaskDto[]>($"/api/tasks?itemId={item.Id}", TestJson.Options);
        Assert.Equal([t2.Id, t1.Id], all!.Select(t => t.Id).ToArray());

        var open = await client.GetFromJsonAsync<TaskDto[]>($"/api/tasks?itemId={item.Id}&done=false", TestJson.Options);
        Assert.Equal([t2.Id], open!.Select(t => t.Id).ToArray());
    }

    [Fact]
    public async Task Put_Updates_And_Delete_Then404_And_ItemDeleteCascades()
    {
        using var client = factory.CreateClient();
        var item = await CreateItemAsync(client, "캐스케이드");
        var task = await CreateTaskAsync(client, new UpsertTaskRequest("수정 전", null, item.Id, 0));

        var res = await client.PutAsJsonAsync($"/api/tasks/{task.Id}", new UpsertTaskRequest("수정 후", new DateOnly(2026, 11, 11), item.Id, 3), TestJson.Options);
        var updated = (await res.Content.ReadFromJsonAsync<TaskDto>(TestJson.Options))!;
        Assert.Equal("수정 후", updated.Title);
        Assert.Equal(new DateOnly(2026, 11, 11), updated.DueDate);

        await client.DeleteAsync($"/api/items/{item.Id}");
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/tasks/{task.Id}")).StatusCode);
    }
}
```

- [ ] **Step 2: 실패 확인** — Run: `dotnet test WebProject.Api.Tests --filter "FullyQualifiedName~TaskEndpointsTests"` → FAIL.

- [ ] **Step 3: 구현**

```csharp
// Features/Tasks/TaskEndpoints.cs
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using WebProject.Api.Contracts;
using WebProject.Api.Domain;
using WebProject.Api.Infrastructure.Data;

namespace WebProject.Api.Features.Tasks;

/// <summary>/api/tasks 읽기·쓰기 엔드포인트. 완료 상태는 toggle로만 바뀐다.</summary>
public static class TaskEndpoints
{
    private const int TitleMax = 200;

    public static void MapTaskEndpoints(RouteGroupBuilder read, RouteGroupBuilder write)
    {
        read.MapGet("/tasks", ListAsync).WithName("ListTasks");
        read.MapGet("/tasks/{id:guid}", GetAsync).WithName("GetTask");
        write.MapPost("/tasks", CreateAsync).WithName("CreateTask");
        write.MapPut("/tasks/{id:guid}", UpdateAsync).WithName("UpdateTask");
        write.MapDelete("/tasks/{id:guid}", DeleteAsync).WithName("DeleteTask");
        write.MapPost("/tasks/{id:guid}/toggle", ToggleAsync).WithName("ToggleTask");
    }

    private static async Task<Ok<TaskDto[]>> ListAsync(AppDbContext db, Guid? itemId, bool? done, CancellationToken ct)
    {
        var q = db.Tasks.AsNoTracking().AsQueryable();
        if (itemId is not null) q = q.Where(t => t.ItemId == itemId);
        if (done is not null) q = q.Where(t => t.IsDone == done);
        var tasks = await q.OrderBy(t => t.IsDone).ThenBy(t => t.DueDate == null).ThenBy(t => t.DueDate).ThenBy(t => t.SortOrder).ToListAsync(ct);
        return TypedResults.Ok(tasks.Select(t => t.ToDto()).ToArray());
    }

    private static async Task<Results<Ok<TaskDto>, NotFound>> GetAsync(AppDbContext db, Guid id, CancellationToken ct)
    {
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        return task is null ? TypedResults.NotFound() : TypedResults.Ok(task.ToDto());
    }

    private static async Task<ValidationErrors> ValidateAsync(AppDbContext db, UpsertTaskRequest req, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (string.IsNullOrWhiteSpace(req.Title)) errors.Add(nameof(req.Title), "제목은 비울 수 없습니다.");
        else if (req.Title.Trim().Length > TitleMax) errors.Add(nameof(req.Title), $"제목은 {TitleMax}자 이하여야 합니다.");
        if (req.ItemId is Guid itemId && !await db.Items.AnyAsync(i => i.Id == itemId, ct))
            errors.Add(nameof(req.ItemId), "존재하지 않는 항목입니다.");
        return errors;
    }

    private static async Task<Results<Created<TaskDto>, ValidationProblem>> CreateAsync(AppDbContext db, UpsertTaskRequest req, CancellationToken ct)
    {
        var errors = await ValidateAsync(db, req, ct);
        if (errors.Any) return TypedResults.ValidationProblem(errors.ToDictionary());

        var now = DateTimeOffset.UtcNow;
        var task = new TaskItem { CreatedAt = now };
        Apply(task, req, now);
        db.Tasks.Add(task);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/tasks/{task.Id}", task.ToDto());
    }

    private static async Task<Results<Ok<TaskDto>, NotFound, ValidationProblem>> UpdateAsync(AppDbContext db, Guid id, UpsertTaskRequest req, CancellationToken ct)
    {
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (task is null) return TypedResults.NotFound();
        var errors = await ValidateAsync(db, req, ct);
        if (errors.Any) return TypedResults.ValidationProblem(errors.ToDictionary());

        Apply(task, req, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(task.ToDto());
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(AppDbContext db, Guid id, CancellationToken ct)
    {
        var deleted = await db.Tasks.Where(t => t.Id == id).ExecuteDeleteAsync(ct);
        return deleted == 0 ? TypedResults.NotFound() : TypedResults.NoContent();
    }

    private static async Task<Results<Ok<TaskDto>, NotFound>> ToggleAsync(AppDbContext db, Guid id, CancellationToken ct)
    {
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (task is null) return TypedResults.NotFound();
        var now = DateTimeOffset.UtcNow;
        task.IsDone = !task.IsDone;
        task.CompletedAt = task.IsDone ? now : null;
        task.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(task.ToDto());
    }

    private static void Apply(TaskItem task, UpsertTaskRequest req, DateTimeOffset now)
    {
        task.Title = req.Title.Trim();
        task.DueDate = req.DueDate;
        task.ItemId = req.ItemId;
        task.SortOrder = req.SortOrder;
        if (task.IsDone != req.IsDone)
        {
            // 완료 전환 시에만 CompletedAt 을 찍고, 같은 값의 반복 PUT 은 시각을 바꾸지 않는다(멱등).
            task.IsDone = req.IsDone;
            task.CompletedAt = req.IsDone ? now : null;
        }
        task.UpdatedAt = now;
    }
}
```
`ApiEndpoints.cs`에 `TaskEndpoints.MapTaskEndpoints(read, write);` 추가.

- [ ] **Step 4: 통과 확인 + 커밋**

Run: `dotnet test WebProject.sln` → 전부 PASS.

```bash
git add -A
git commit -m "추가: 할 일(Task) CRUD·단건 조회·토글 API — 항목 연결 선택, 항목 삭제 시 함께 삭제

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 8: 첨부(이미지) 저장소 + 엔드포인트

**Files:**
- Create: `WebProject.Api/Infrastructure/Storage/IAttachmentStore.cs`, `AttachmentOptions.cs`, `FileSystemAttachmentStore.cs`, `ImageSignature.cs`, `AttachmentStorageServiceCollectionExtensions.cs`
- Create: `WebProject.Api/Contracts/AttachmentDtos.cs`, `WebProject.Api/Features/Attachments/AttachmentEndpoints.cs`
- Modify: `WebProject.Api/Program.cs`, `appsettings.json`, `WebProject.Api/Features/ApiEndpoints.cs`
- Test: `WebProject.Api.Tests/Features/AttachmentEndpointsTests.cs`, `WebProject.Api.Tests/Infrastructure/FileSystemAttachmentStoreTests.cs`, `WebProject.Api.Tests/Infrastructure/ImageSignatureTests.cs`

**Interfaces:**
- Produces:
  - `IAttachmentStore.StoreAsync(Stream content, string extension, CancellationToken) : Task<StoredFile>`; `IAttachmentStore.OpenRead(string relativePath) : Stream?`
  - `readonly record struct StoredFile(string RelativePath, string Sha256, long SizeBytes)`
  - `AttachmentOptions { RootPath (기본 "data/attachments"), MaxBytes (기본 10_485_760) }` 섹션 `Attachments`. 허용 형식은 선언된 Content-Type 이 아니라 **파일 시그니처**(`ImageSignature.Detect`)로 판정한다: PNG/JPEG/GIF/WebP.
  - `ImageSignature.Detect(ReadOnlySpan<byte> head) : (string ContentType, string Extension)?` — 확장자·저장 Content-Type 은 여기서 유도(같은 바이트 → 같은 경로 보장)
  - `AttachmentDto(Guid Id, string FileName, string ContentType, long SizeBytes, string Url)`; `POST /api/attachments`(multipart 필드명 `file`) → 201(신규)/200(중복 재사용)/400(이미지 아님·파일명 255자 초과)/413; `GET /api/attachments/{id}/{fileName}`
  - 마크다운 참조 URL 형식: `/api/attachments/{id}/{fileName}` (Plan 3 노션 가져오기가 사용)

- [ ] **Step 1: 저장소 단위 테스트** — `WebProject.Api.Tests/Infrastructure/FileSystemAttachmentStoreTests.cs`

```csharp
using System.Text;
using Microsoft.Extensions.Options;
using WebProject.Api.Infrastructure.Storage;

namespace WebProject.Api.Tests.Infrastructure;

public sealed class FileSystemAttachmentStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "para-store-tests", Guid.NewGuid().ToString("N"));

    private FileSystemAttachmentStore Create() =>
        new(Options.Create(new AttachmentOptions { RootPath = _root }));

    [Fact]
    public async Task StoreAsync_WritesContentAddressedFile_AndReturnsHash()
    {
        var store = Create();
        var bytes = Encoding.UTF8.GetBytes("hello attachment");
        var stored = await store.StoreAsync(new MemoryStream(bytes), ".txt", CancellationToken.None);

        Assert.Equal(bytes.Length, stored.SizeBytes);
        Assert.Equal(64, stored.Sha256.Length);
        Assert.EndsWith(stored.Sha256 + ".txt", stored.RelativePath, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_root, stored.RelativePath)));

        await using var read = store.OpenRead(stored.RelativePath);
        Assert.NotNull(read);
        using var reader = new StreamReader(read!);
        Assert.Equal("hello attachment", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task StoreAsync_SameContentTwice_ReusesFile()
    {
        var store = Create();
        var a = await store.StoreAsync(new MemoryStream([1, 2, 3]), ".bin", CancellationToken.None);
        var b = await store.StoreAsync(new MemoryStream([1, 2, 3]), ".bin", CancellationToken.None);
        Assert.Equal(a.RelativePath, b.RelativePath);
        Assert.Single(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task StoreAsync_ConcurrentSameContent_BothSucceed_SingleFile()
    {
        var store = Create();
        var tasks = Enumerable.Range(0, 8).Select(_ => store.StoreAsync(new MemoryStream([9, 9, 9, 9]), ".bin", CancellationToken.None));
        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.Equal(results[0].RelativePath, r.RelativePath));
        Assert.Single(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "tmp"))); // 임시 파일 잔류 없음
    }

    [Fact]
    public void OpenRead_PathTraversal_ReturnsNull()
    {
        var store = Create();
        Assert.Null(store.OpenRead("../../outside.txt"));
    }

    [Fact]
    public void Ctor_RootWithTrailingSeparator_StillOpensFiles()
    {
        var withSlash = new FileSystemAttachmentStore(Options.Create(new AttachmentOptions { RootPath = _root + Path.DirectorySeparatorChar }));
        Directory.CreateDirectory(Path.Combine(_root, "ab"));
        File.WriteAllBytes(Path.Combine(_root, "ab", "x.bin"), [1]);
        using var s = withSlash.OpenRead("ab/x.bin");
        Assert.NotNull(s);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
```

- [ ] **Step 2: 실패 확인** — Run: `dotnet test WebProject.Api.Tests --filter "FullyQualifiedName~FileSystemAttachmentStoreTests"` → 컴파일 오류.

`WebProject.Api.Tests/Infrastructure/ImageSignatureTests.cs` 도 함께 작성한다:

```csharp
using WebProject.Api.Infrastructure.Storage;

namespace WebProject.Api.Tests.Infrastructure;

public sealed class ImageSignatureTests
{
    [Fact]
    public void Detect_Png()
    {
        byte[] head = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
        Assert.Equal(("image/png", ".png"), ImageSignature.Detect(head)!.Value);
    }

    [Fact]
    public void Detect_Jpeg_Gif_Webp()
    {
        Assert.Equal(("image/jpeg", ".jpg"), ImageSignature.Detect([0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0])!.Value);
        Assert.Equal(("image/gif", ".gif"), ImageSignature.Detect("GIF89a\0\0\0\0\0\0"u8)!.Value);
        Assert.Equal(("image/webp", ".webp"), ImageSignature.Detect("RIFF\0\0\0\0WEBP"u8)!.Value);
    }

    [Fact]
    public void Detect_Unknown_ReturnsNull()
    {
        Assert.Null(ImageSignature.Detect("hello world!"u8));
        Assert.Null(ImageSignature.Detect([0x89, 0x50]));
    }
}
```

- [ ] **Step 3: 저장소 구현** — `WebProject.Api/Infrastructure/Storage/`

```csharp
// IAttachmentStore.cs
namespace WebProject.Api.Infrastructure.Storage;

/// <summary>저장된 파일의 위치·해시·크기.</summary>
public readonly record struct StoredFile(string RelativePath, string Sha256, long SizeBytes);

/// <summary>첨부 파일 본체 저장소. 내용 해시 기반(content-addressed)이라 같은 내용은 한 번만 저장된다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 구현은 Thread-safe여야 한다(싱글턴, 동시 업로드).</description></item>
/// <item><description><b>Memory Allocation:</b> <paramref name="content"/> 스트림 소유권은 호출자에게 있으며 메서드는 끝까지 읽기만 한다. 구현은 고정 크기 버퍼로 스트리밍해야 하며 전체 파일을 메모리에 올리면 안 된다.</description></item>
/// <item><description><b>Blocking:</b> StoreAsync는 비동기 파일 I/O. OpenRead는 핸들 열기만 동기 수행(즉시 반환).</description></item>
/// </list>
/// </remarks>
public interface IAttachmentStore
{
    Task<StoredFile> StoreAsync(Stream content, string extension, CancellationToken ct);
    /// <summary>루트 밖 경로·없는 파일이면 null.</summary>
    Stream? OpenRead(string relativePath);
}
```

```csharp
// AttachmentOptions.cs
namespace WebProject.Api.Infrastructure.Storage;

/// <summary>설정 섹션 <c>Attachments</c>.</summary>
public sealed class AttachmentOptions
{
    public const string SectionName = "Attachments";
    /// <summary>저장 루트. 상대 경로면 ContentRootPath 기준. 도커에서는 /data/attachments 볼륨.</summary>
    public string RootPath { get; set; } = "data/attachments";
    /// <summary>업로드 최대 바이트(기본 10MB).</summary>
    public long MaxBytes { get; set; } = 10L * 1024 * 1024;
}
```
(허용 형식 목록은 옵션이 아니라 `ImageSignature`가 고정한다: PNG·JPEG·GIF·WebP. SVG는 스크립트 실행 위험으로 제외.)

```csharp
// ImageSignature.cs
namespace WebProject.Api.Infrastructure.Storage;

/// <summary>파일 선두 바이트(매직 넘버)로 이미지 형식을 판정한다. 요청의 Content-Type 은 신뢰하지 않는다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 순수 함수.</description></item>
/// <item><description><b>Memory Allocation:</b> Zero-allocation. 반환 튜플의 문자열은 상수 인터닝.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
/// </list>
/// </remarks>
public static class ImageSignature
{
    /// <summary>판정에 필요한 최소 선두 길이(WebP: RIFF????WEBP = 12바이트).</summary>
    public const int HeaderLength = 12;

    public static (string ContentType, string Extension)? Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 8 && head[..8].SequenceEqual([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            return ("image/png", ".png");
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
            return ("image/jpeg", ".jpg");
        if (head.Length >= 6 && (head[..6].SequenceEqual("GIF87a"u8) || head[..6].SequenceEqual("GIF89a"u8)))
            return ("image/gif", ".gif");
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
            return ("image/webp", ".webp");
        return null;
    }
}
```

```csharp
// FileSystemAttachmentStore.cs
using System.Buffers;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace WebProject.Api.Infrastructure.Storage;

/// <summary>로컬 파일 시스템(도커 볼륨) 저장소. 경로는 {sha256[..2]}/{sha256}{ext} (내용 주소).</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 임시 파일명이 Guid라 동시 업로드가 충돌하지 않고, 같은 내용의 동시 이동은 "대상 이미 존재"만 정상 중복으로 흡수한다.</description></item>
/// <item><description><b>Memory Allocation:</b> 64KB 대여 버퍼 1개(ArrayPool)로 스트리밍. 파일 크기와 무관하게 상수 메모리.</description></item>
/// <item><description><b>Blocking:</b> StoreAsync는 FileStream 비동기 I/O. OpenRead는 핸들만 연다.</description></item>
/// </list>
/// </remarks>
public sealed class FileSystemAttachmentStore : IAttachmentStore
{
    // ArrayPool<byte>.Shared 는 2의 거듭제곱 버킷이라 81920 을 요청하면 131072 배열(LOH 임계 85,000B 초과)을 돌려준다.
    // 65536 은 정확히 버킷 크기이므로 LOH 에 올라가지 않는다.
    private const int BufferSize = 65536;
    private readonly string _root;

    public FileSystemAttachmentStore(IOptions<AttachmentOptions> options)
    {
        // 끝의 구분자를 제거해 두어야 OpenRead 의 접두사 검사(_root + 구분자)가 "//" 로 끝나지 않는다.
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Value.RootPath));
        Directory.CreateDirectory(_root);
    }

    public async Task<StoredFile> StoreAsync(Stream content, string extension, CancellationToken ct)
    {
        var tmpDir = Path.Combine(_root, "tmp");
        Directory.CreateDirectory(tmpDir);
        var tmpPath = Path.Combine(tmpDir, Guid.NewGuid().ToString("N"));

        // ArrayPool<byte>.Shared: 동일 스레드 TLS 슬롯 → 공유 버킷 순으로 재사용하므로 업로드마다 새 배열을 만들지 않는다.
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        // IncrementalHash: 스트리밍 청크마다 상태를 갱신하므로 파일 전체를 메모리에 올리지 않고 SHA-256을 계산한다.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
        try
        {
            // FileStream(useAsync: true): Windows에서는 OVERLAPPED I/O, Linux에서는 스레드풀 위임으로 요청 스레드를 점유하지 않는다.
            await using (var file = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                int read;
                while ((read = await content.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    size += read;
                }
            }
        }
        catch
        {
            File.Delete(tmpPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var sha = Convert.ToHexStringLower(hash.GetHashAndReset());
        // 경로가 내용 해시(+시그니처에서 유도한 확장자)만으로 결정되므로 같은 내용은 언제 올려도 같은 파일 하나로 수렴한다.
        var relative = $"{sha[..2]}/{sha}{extension.ToLowerInvariant()}";
        var finalPath = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        try
        {
            // overwrite:false 이동. 두 업로드가 동시에 여기 오면 한쪽만 성공하고 다른 쪽은 IOException 이 난다.
            File.Move(tmpPath, finalPath, overwrite: false);
        }
        catch (IOException) when (File.Exists(finalPath))
        {
            // 경쟁에서 진 쪽: 같은 내용이 이미 있으므로 정상 중복. 그 외 IOException 은 그대로 전파한다.
        }
        finally
        {
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
        }
        return new StoredFile(relative, sha, size);
    }

    public Stream? OpenRead(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(_root, relativePath));
        // 정규화된 경로가 루트 아래가 아니면 경로 탈출 시도 → 거부. Windows 는 대소문자 무시 파일시스템이라 비교도 맞춘다.
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, comparison) || !File.Exists(full))
        {
            return null;
        }
        return new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
    }
}
```
경로 규칙 `{sha[..2]}/{sha}{ext}`: 같은 내용은 항상 같은 경로로 수렴하므로 엔드포인트가 저장 후 DB에서 같은 `Sha256`을 찾으면 기존 레코드를 그대로 돌려주면 되고 고아 파일이 남지 않는다.

```csharp
// AttachmentStorageServiceCollectionExtensions.cs
namespace WebProject.Api.Infrastructure.Storage;

public static class AttachmentStorageServiceCollectionExtensions
{
    public static IServiceCollection AddAttachmentStorage(this IServiceCollection services, IConfiguration configuration, IHostEnvironment env)
    {
        services.Configure<AttachmentOptions>(configuration.GetSection(AttachmentOptions.SectionName));
        services.PostConfigure<AttachmentOptions>(o =>
        {
            if (!Path.IsPathRooted(o.RootPath)) o.RootPath = Path.Combine(env.ContentRootPath, o.RootPath);
        });
        services.AddSingleton<IAttachmentStore, FileSystemAttachmentStore>();
        return services;
    }
}
```
Program.cs: `builder.Services.AddWriteAccess(...)` 다음에 `builder.Services.AddAttachmentStorage(builder.Configuration, builder.Environment);` (using `WebProject.Api.Infrastructure.Storage`). `appsettings.json`에 `"Attachments": { "RootPath": "data/attachments", "MaxBytes": 10485760 }` 추가. `.gitignore`에 `WebProject.Api/data/` 추가.

- [ ] **Step 4: 저장소 테스트 통과 확인** — Run: `dotnet test WebProject.Api.Tests --filter "FullyQualifiedName~FileSystemAttachmentStoreTests"` → 3개 PASS.

- [ ] **Step 5: 엔드포인트 통합 테스트** — `WebProject.Api.Tests/Features/AttachmentEndpointsTests.cs`

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using WebProject.Api.Contracts;
using WebProject.Api.Tests.Infrastructure;

namespace WebProject.Api.Tests.Features;

[Collection("postgres")]
public sealed class AttachmentEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    // 1x1 PNG (67 bytes)
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private static MultipartFormDataContent Form(byte[] bytes, string contentType, string fileName)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { content, "file", fileName } };
    }

    [Fact]
    public async Task Upload_ThenDownload_RoundTrips_AndDedupes()
    {
        using var client = factory.CreateClient();

        var first = await client.PostAsync("/api/attachments", Form(Png, "image/png", "dot.png"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var dto = (await first.Content.ReadFromJsonAsync<AttachmentDto>(TestJson.Options))!;
        Assert.Equal("dot.png", dto.FileName);
        Assert.Equal($"/api/attachments/{dto.Id}/dot.png", dto.Url);

        var second = await client.PostAsync("/api/attachments", Form(Png, "image/png", "same.png"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var dup = (await second.Content.ReadFromJsonAsync<AttachmentDto>(TestJson.Options))!;
        Assert.Equal(dto.Id, dup.Id);

        // 저장 Content-Type 은 선언값이 아니라 시그니처에서 유도된다.
        Assert.Equal("image/png", dto.ContentType);

        var download = await client.GetAsync(dto.Url);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("image/png", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await download.Content.ReadAsByteArrayAsync());
        Assert.Contains("immutable", download.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Upload_RejectsNonImage_AndOversize()
    {
        using var client = factory.CreateClient();
        var text = await client.PostAsync("/api/attachments", Form([1, 2, 3], "text/plain", "a.txt"));
        Assert.Equal(HttpStatusCode.BadRequest, text.StatusCode);

        // Content-Type 을 image/png 로 속여도 시그니처가 아니면 거부한다.
        var spoofed = await client.PostAsync("/api/attachments", Form("not an image"u8.ToArray(), "image/png", "fake.png"));
        Assert.Equal(HttpStatusCode.BadRequest, spoofed.StatusCode);

        var longName = await client.PostAsync("/api/attachments", Form(Png, "image/png", new string('n', 300) + ".png"));
        Assert.Equal(HttpStatusCode.BadRequest, longName.StatusCode);

        var big = await client.PostAsync("/api/attachments", Form(new byte[10 * 1024 * 1024 + 1], "image/png", "big.png"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.StatusCode);
    }

    [Fact]
    public async Task Download_UnknownId_404_And_Upload403WhenDenied()
    {
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/attachments/{Guid.NewGuid()}/x.png")).StatusCode);

        factory.WriteAccess.Allow = false;
        try
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/attachments", Form(Png, "image/png", "d.png"))).StatusCode);
        }
        finally { factory.WriteAccess.Allow = true; }
    }
}
```

- [ ] **Step 6: 실패 확인** — Run: `dotnet test WebProject.Api.Tests --filter "FullyQualifiedName~AttachmentEndpointsTests"` → 컴파일 오류(AttachmentDto 없음).

- [ ] **Step 7: DTO + 엔드포인트 구현**

```csharp
// Contracts/AttachmentDtos.cs
namespace WebProject.Api.Contracts;
public sealed record AttachmentDto(Guid Id, string FileName, string ContentType, long SizeBytes, string Url);
```

```csharp
// Features/Attachments/AttachmentEndpoints.cs
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebProject.Api.Contracts;
using WebProject.Api.Domain;
using WebProject.Api.Infrastructure.Data;
using WebProject.Api.Infrastructure.Storage;

namespace WebProject.Api.Features.Attachments;

/// <summary>/api/attachments 업로드(이미지만)·다운로드.</summary>
public static class AttachmentEndpoints
{
    public static void MapAttachmentEndpoints(RouteGroupBuilder read, RouteGroupBuilder write)
    {
        read.MapGet("/attachments/{id:guid}/{fileName}", DownloadAsync).WithName("GetAttachment");
        write.MapPost("/attachments", UploadAsync)
            .WithName("UploadAttachment")
            // 최소 API 폼 바인딩은 기본으로 안티포저리를 요구한다. 쿠키 인증이 없고 쓰기는 IP 정책이 막으므로 비활성화한다.
            .DisableAntiforgery()
            // Kestrel 본문 한도(멀티파트 오버헤드 여유 1MB). TestServer는 강제하지 않으므로 아래 Length 검사가 실제 판정이다.
            .WithMetadata(new RequestSizeLimitAttribute(11L * 1024 * 1024));
    }

    private static async Task<Results<Created<AttachmentDto>, Ok<AttachmentDto>, ValidationProblem, ProblemHttpResult>> UploadAsync(
        IFormFile file, AppDbContext db, IAttachmentStore store, IOptions<AttachmentOptions> options, CancellationToken ct)
    {
        var opt = options.Value;
        if (file.Length > opt.MaxBytes)
            return TypedResults.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "파일이 너무 큽니다.", detail: $"최대 {opt.MaxBytes} 바이트");

        var fileName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "image";
        if (fileName.Length > 255)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["파일명은 255자 이하여야 합니다."] });

        // file.OpenReadStream(): 요청 본문을 디스크/메모리 버퍼링한 seekable 스트림. 소유권은 이 using 블록에 있다.
        StoredFile stored;
        string contentType;
        await using (var content = file.OpenReadStream())
        {
            // 선두 12바이트로 실제 형식을 판정한다(선언된 Content-Type 은 신뢰하지 않음). async 메서드라 stackalloc 은 쓸 수 없어 12바이트 배열 1개를 할당한다.
            var head = await ReadHeadAsync(content, ImageSignature.HeaderLength, ct);
            var detected = ImageSignature.Detect(head);
            if (detected is null)
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["PNG·JPEG·GIF·WebP 이미지만 업로드할 수 있습니다."] });
            contentType = detected.Value.ContentType;
            content.Position = 0;
            stored = await store.StoreAsync(content, detected.Value.Extension, ct);
        }

        var existing = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(a => a.Sha256 == stored.Sha256, ct);
        if (existing is not null)
        {
            return TypedResults.Ok(ToDto(existing));
        }

        var attachment = new Attachment
        {
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = stored.SizeBytes,
            StoragePath = stored.RelativePath,
            Sha256 = stored.Sha256,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Attachments.Add(attachment);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (UniqueViolation.Is(ex))
        {
            // 같은 해시를 동시에 올린 경쟁: 먼저 들어간 레코드를 돌려준다(파일은 같은 경로 하나뿐이므로 고아 없음).
            db.Entry(attachment).State = EntityState.Detached;
            var winner = await db.Attachments.AsNoTracking().FirstAsync(a => a.Sha256 == stored.Sha256, ct);
            return TypedResults.Ok(ToDto(winner));
        }
        var dto = ToDto(attachment);
        return TypedResults.Created(dto.Url, dto);
    }

    /// <summary>스트림 선두 최대 <paramref name="count"/> 바이트를 읽는다(짧은 파일은 그만큼만). 호출자가 Position 을 되돌린다.</summary>
    private static async Task<byte[]> ReadHeadAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var total = 0;
        while (total < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total, count - total), ct);
            if (n == 0) break;
            total += n;
        }
        return total == count ? buffer : buffer[..total];
    }

    private static async Task<Results<FileStreamHttpResult, NotFound>> DownloadAsync(Guid id, string fileName, AppDbContext db, IAttachmentStore store, HttpResponse response, CancellationToken ct)
    {
        var attachment = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (attachment is null) return TypedResults.NotFound();
        var stream = store.OpenRead(attachment.StoragePath);
        if (stream is null) return TypedResults.NotFound();

        // 내용 주소 저장이라 같은 URL의 내용은 절대 바뀌지 않는다 → 1년 immutable 캐시.
        response.Headers.CacheControl = "public, max-age=31536000, immutable";
        return TypedResults.File(stream, attachment.ContentType, enableRangeProcessing: true);
    }

    private static AttachmentDto ToDto(Attachment a) =>
        new(a.Id, a.FileName, a.ContentType, a.SizeBytes, $"/api/attachments/{a.Id}/{Uri.EscapeDataString(a.FileName)}");

}
```
`ApiEndpoints.cs`에 `AttachmentEndpoints.MapAttachmentEndpoints(read, write);` 추가.

- [ ] **Step 8: 통과 확인 + 커밋**

Run: `dotnet test WebProject.sln` → 전부 PASS.

```bash
git add -A
git commit -m "추가: 이미지 첨부 업로드·다운로드 API와 내용 해시 기반 파일 저장소

- ArrayPool 스트리밍 SHA-256, 동일 내용 재사용(동시 업로드 경쟁 흡수), 경로 탈출 거부
- 파일 시그니처(PNG/JPEG/GIF/WebP)로 형식 판정·10MB 한도, immutable 캐시 헤더

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 9: 대시보드 엔드포인트

**Files:**
- Create: `WebProject.Api/Contracts/DashboardDtos.cs`, `WebProject.Api/Features/Dashboard/DashboardEndpoints.cs`
- Modify: `WebProject.Api/Features/ApiEndpoints.cs`
- Test: `WebProject.Api.Tests/Features/DashboardEndpointsTests.cs`

**Interfaces:**
- Produces: `DashboardDto(ItemSummaryDto[] ActiveProjects, TaskDto[] UpcomingTasks, NoteSummaryDto[] RecentNotes, ItemSummaryDto[] Areas)`; `GET /api/dashboard`.
- 규칙: ActiveProjects = Kind=Project & Status=Active, SortOrder·Title 순. UpcomingTasks = 미완료 & DueDate 있음, DueDate 오름차순 10개. RecentNotes = UpdatedAt 내림차순 10개. Areas = Kind=Area 전부.

- [ ] **Step 1: 실패하는 테스트**

```csharp
using System.Net.Http.Json;
using WebProject.Api.Contracts;
using WebProject.Api.Domain;
using WebProject.Api.Tests.Infrastructure;

namespace WebProject.Api.Tests.Features;

[Collection("postgres")]
public sealed class DashboardEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Get_AggregatesActiveProjects_UpcomingTasks_RecentNotes_Areas()
    {
        using var client = factory.CreateClient();
        async Task<ItemSummaryDto> Item(ItemKind k, string t, ItemStatus s) =>
            (await (await client.PostAsJsonAsync("/api/items", new UpsertItemRequest(k, t, null, s, null, null, null, 0), TestJson.Options))
                .Content.ReadFromJsonAsync<ItemSummaryDto>(TestJson.Options))!;

        var active = await Item(ItemKind.Project, "활성 프로젝트", ItemStatus.Active);
        var planned = await Item(ItemKind.Project, "계획 프로젝트", ItemStatus.Planned);
        var area = await Item(ItemKind.Area, "영역 1", ItemStatus.Active);

        await client.PostAsJsonAsync("/api/tasks", new UpsertTaskRequest("마감 있음", new DateOnly(2026, 10, 5), active.Id, 0), TestJson.Options);
        await client.PostAsJsonAsync("/api/tasks", new UpsertTaskRequest("마감 없음", null, active.Id, 0), TestJson.Options);
        var noteRes = await client.PostAsJsonAsync("/api/notes", new UpsertNoteRequest("최근 노트", "x", [active.Id], null), TestJson.Options);
        var note = (await noteRes.Content.ReadFromJsonAsync<NoteDetailDto>(TestJson.Options))!;

        var dash = await client.GetFromJsonAsync<DashboardDto>("/api/dashboard", TestJson.Options);

        Assert.Contains(dash!.ActiveProjects, p => p.Id == active.Id);
        Assert.DoesNotContain(dash.ActiveProjects, p => p.Id == planned.Id);
        Assert.Contains(dash.Areas, a => a.Id == area.Id);
        Assert.Contains(dash.UpcomingTasks, t => t.Title == "마감 있음");
        Assert.DoesNotContain(dash.UpcomingTasks, t => t.Title == "마감 없음");
        Assert.True(dash.UpcomingTasks.Length <= 10);
        Assert.Contains(dash.RecentNotes, n => n.Id == note.Id);
        Assert.True(dash.RecentNotes.Length <= 10);
    }
}
```

- [ ] **Step 2: 실패 확인** — Run: `dotnet test WebProject.Api.Tests --filter "FullyQualifiedName~DashboardEndpointsTests"` → 컴파일 오류.

- [ ] **Step 3: 구현**

```csharp
// Contracts/DashboardDtos.cs
namespace WebProject.Api.Contracts;
public sealed record DashboardDto(ItemSummaryDto[] ActiveProjects, TaskDto[] UpcomingTasks, NoteSummaryDto[] RecentNotes, ItemSummaryDto[] Areas);
```

```csharp
// Features/Dashboard/DashboardEndpoints.cs
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using WebProject.Api.Contracts;
using WebProject.Api.Domain;
using WebProject.Api.Infrastructure.Data;

namespace WebProject.Api.Features.Dashboard;

/// <summary>/api/dashboard — 홈 화면 집계(활성 프로젝트·마감 임박 할 일·최근 노트·영역).</summary>
public static class DashboardEndpoints
{
    private const int Take = 10;

    public static void MapDashboardEndpoints(RouteGroupBuilder read, RouteGroupBuilder write)
    {
        read.MapGet("/dashboard", GetAsync).WithName("GetDashboard");
    }

    private static async Task<Ok<DashboardDto>> GetAsync(AppDbContext db, CancellationToken ct)
    {
        var items = db.Items.AsNoTracking().Include(i => i.Area).Include(i => i.ItemTags).ThenInclude(t => t.Tag);

        var activeProjects = await items.Where(i => i.Kind == ItemKind.Project && i.Status == ItemStatus.Active)
            .OrderBy(i => i.SortOrder).ThenBy(i => i.Title).ToListAsync(ct);
        var areas = await items.Where(i => i.Kind == ItemKind.Area)
            .OrderBy(i => i.SortOrder).ThenBy(i => i.Title).ToListAsync(ct);
        var upcoming = await db.Tasks.AsNoTracking().Where(t => !t.IsDone && t.DueDate != null)
            .OrderBy(t => t.DueDate).ThenBy(t => t.SortOrder).Take(Take).ToListAsync(ct);
        var recent = await db.Notes.AsNoTracking().Include(n => n.ItemLinks).Include(n => n.NoteTags).ThenInclude(t => t.Tag)
            .OrderByDescending(n => n.UpdatedAt).Take(Take).ToListAsync(ct);

        return TypedResults.Ok(new DashboardDto(
            activeProjects.Select(i => i.ToSummary()).ToArray(),
            upcoming.Select(t => t.ToDto()).ToArray(),
            recent.Select(n => n.ToSummary()).ToArray(),
            areas.Select(i => i.ToSummary()).ToArray()));
    }
}
```
`ApiEndpoints.cs`에 `DashboardEndpoints.MapDashboardEndpoints(read, write);` 추가.

- [ ] **Step 4: 통과 확인 + 커밋**

Run: `dotnet test WebProject.sln` → 전부 PASS.

```bash
git add -A
git commit -m "추가: PARA 대시보드 집계 API (활성 프로젝트·마감 할 일·최근 노트·영역)

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 10: 마무리 — 전체 회귀·OpenAPI 확인·문서 갱신

**Files:**
- Modify: `plan/para_notes_0917.md` (5절 변경 파일 표 0~2단계 "완료" 표시)
- Modify: `.github/workflows/ci.yml` (`runs-on: ubuntu-latest` — Testcontainers 는 리눅스 Docker 가 필요하고 배포 대상도 우분투. Plan 4 에서 web 잡을 덧붙인다)
- Modify: `CLAUDE.md`, `AGENTS.md` (구성 절: 로컬 실행 방법 한 줄 — Postgres 필요)
- Modify: `README.md` (있으면 "로컬 실행" 절 추가, 없으면 생성)

- [ ] **Step 1: 전체 빌드·테스트**

Run: `dotnet build WebProject.sln -c Release` → 경고 0. Run: `dotnet test WebProject.sln -c Release` → 전부 PASS.

- [ ] **Step 2: 로컬 실행 스모크(수동)** — Docker로 Postgres를 띄우고 API를 실행해 OpenAPI 문서가 모든 엔드포인트를 나열하는지 본다.

```bash
docker run -d --name para-dev-pg -e POSTGRES_PASSWORD=changeme -e POSTGRES_DB=para_dev -p 5432:5432 postgres:17-alpine
dotnet run --project WebProject.Api &      # 포그라운드 프로세스이므로 백그라운드로 띄운다(또는 별도 터미널)
sleep 8
curl -s http://localhost:5055/openapi/v1.json | python -c "import json,sys; [print(p) for p in sorted(json.load(sys.stdin)['paths'])]"
curl -s http://localhost:5055/api/me
kill %1; docker rm -f para-dev-pg
```
Expected: `/api/me`, `/api/dashboard`, `/api/items`, `/api/items/{id}`, `/api/items/{id}/archive`, `/api/items/{id}/restore`, `/api/notes`, `/api/notes/{id}`, `/api/tasks`, `/api/tasks/{id}`, `/api/tasks/{id}/toggle`, `/api/tags`, `/api/tags/{id}`, `/api/attachments`, `/api/attachments/{id}/{fileName}`, `/health` 출력. `/api/me` → `{"canWrite":true}` (Development 화이트리스트에 127.0.0.1). OpenAPI JSON 에서 `kind` 스키마가 `"enum": ["Project","Area","Resource","Archive"]` 문자열인지도 확인한다.

- [ ] **Step 3: 문서 갱신** — `plan/para_notes_0917.md` 5절 표의 0·1·2단계 행 끝에 `(완료 YYYY-MM-DD)`를 적는다(3절 트리의 `Contracts/`와 의존 방향은 이미 반영됨). `README.md`에 아래 절을 추가한다.

```markdown
## 로컬 실행 (API)

1. PostgreSQL: `docker run -d --name para-dev-pg -e POSTGRES_PASSWORD=changeme -e POSTGRES_DB=para_dev -p 5432:5432 postgres:17-alpine`
2. API: `dotnet run --project WebProject.Api` → http://localhost:5055 (OpenAPI: /openapi/v1.json)
3. 테스트: Docker Desktop 실행 후 `dotnet test WebProject.sln` (Testcontainers가 Postgres를 자동 기동)
```

- [ ] **Step 4: CI 를 리눅스로 전환** — `.github/workflows/ci.yml` 의 `runs-on: windows-latest` 를 `runs-on: ubuntu-latest` 로 바꾼다. GitHub 호스트 우분투 러너에는 Docker 가 기본 설치되어 Testcontainers 가 동작한다. 푸시 후 Actions 에서 `test` 잡이 초록인지 확인한다.

- [ ] **Step 5: 커밋**

```bash
git add -A
git commit -m "문서: PARA 백엔드 API 완료 반영 — 컴포넌트 트리·로컬 실행 방법·CI 리눅스 전환

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Self-Review 결과

- **스펙 커버리지(0~2단계):** 데이터 모델 8테이블(Task 2) ✔, 접근 제어 4요소(Task 3) ✔, API 표면 — me/dashboard/items/notes/tasks/tags/attachments ✔ (notion은 Plan 3), `/weatherforecast` 삭제·`/health` 유지(Task 1) ✔, ProblemDetails(Task 1) ✔, 삭제 규칙(항목 삭제 → 노트 유지·할 일 cascade: Task 6·7 테스트) ✔, ILIKE 검색(Task 6) ✔, 첨부 Sha256 재사용·URL 형식(Task 8) ✔, Testcontainers 통합·정책 가짜 교체(Task 2·3) ✔.
- **CI:** Task 10 에서 `ubuntu-latest` 로 전환한다(Codex 권고 반영). Task 2~9 사이의 푸시에서는 CI 의 Testcontainers 테스트가 Docker 부재로 실패하므로 그 구간의 빨간 CI 는 예상된 상태다.
- **타입 일관성:** `TagResolver.ResolveAsync(AppDbContext, IEnumerable<string>?, CancellationToken)` — Task 4·6 동일. `DtoMapping.ToSummary/ToDetail/ToDto` — Task 4·6·7·9 동일. `ApiFactory.WriteAccess.Allow` — Task 3·4·8 동일. `Map<X>Endpoints(RouteGroupBuilder read, RouteGroupBuilder write)` 시그니처 전 Feature 동일.
- **플레이스홀더:** 없음. XML 주석은 Global Constraints 템플릿을 모든 public 멤버에 적용(구현자 책임).

## Codex 교차 검증 반영 (2026-09-17)

Codex(`codex exec`, read-only, thread `01a0aef2-2e62-7931-88d4-7fbda5da083a`)가 스펙·계획을 정적 검증한 결과를 Claude 가 재검증해 반영했다. 원문은 세션 스크래치패드 `codex_out.md`.

| 구분 | 지적 | 반영 위치 |
|---|---|---|
| 보안 | KnownNetworks·KnownProxies 가 둘 다 비면 ForwardedHeaders 가 송신자 검사를 **생략**해 XFF 위조로 화이트리스트 우회 가능 | Task 3 `UseTrustedForwardedHeaders`(설정 없으면 미등록) + `ForwardedHeadersTests` |
| 보안 | IP 허용은 CSRF 방어가 아님(허용 네트워크의 브라우저가 악성 사이트를 열면 본문 없는 POST 유발 가능) | Task 3 `RequireWriteAccessFilter` 의 `X-Requested-With` 필수 검사, `ApiFactory.ConfigureClient` |
| 컴파일 | `System.Net.IPNetwork` 와 `HttpOverrides.IPNetwork` 이름 충돌, .NET 10 에서 `KnownNetworks` obsolete | Task 3 별칭 using + `KnownIPNetworks` |
| 논리 | 보관 항목은 어떤 PUT 도 통과 못함 | Task 4 `ItemValidation`(기존 Archive 면 Kind=Archive 요청만 허용) + 테스트 |
| 무결성 | 참조 중인 영역을 보관·종류 변경하면 AreaId 가 비영역을 가리킴 | Task 4: 보관된 영역도 유효 소속(사용자 결정 c), 참조 중 종류 변경은 400 |
| 검증 | `JsonStringEnumConverter` 기본값이 정수 허용 → 미정의 enum 저장 | Task 1 `allowIntegerValues:false`, Task 4 `Enum.IsDefined`, 테스트 |
| 검증 | 태그 50자·파일명 255자 초과가 DB 예외(500) | Task 4 `TagResolver.Validate`, Task 8 파일명 검사 |
| 동시성 | 태그·첨부 "조회 후 추가" 경쟁이 유니크 충돌 500 | `UniqueViolation` 헬퍼 → 409(태그) / 승자 레코드 반환(첨부) |
| 저장소 | 같은 해시·다른 확장자 → 고아 파일; `File.Exists→Move` 경쟁; `Rent(81920)` 은 LOH 버킷 | Task 8 시그니처 기반 확장자, overwrite:false 이동 + 존재 시 흡수, 64KB 버퍼 |
| 저장소 | `OpenRead` 루트 끝 구분자·Windows 대소문자 | Task 8 `TrimEndingDirectorySeparator`, 플랫폼별 비교 + 테스트 |
| 계약 | `POST /api/tasks` 의 Location 이 가리키는 GET 없음; toggle 비멱등 | Task 7 `GET /api/tasks/{id}`, PUT 에 `IsDone` |
| 계약 | 응답 태그 순서가 보장 없음 | `DtoMapping.SortedTags` 정규화 이름 순 정렬 계약 |
| 계약 | 노트 목록 200개 절단, 페이지 없음; ILIKE 와일드카드 미이스케이프 | Task 6 `skip/take` + `PagedNotesDto`, `EscapeLike` |
| 테스트 | Development 의 `ThrowOnBadRequest` 로 바인딩 실패가 500 이 될 수 있음; fixture scope 누수; Testcontainers obsolete 생성자 | Task 1 `ThrowOnBadRequest=false`, Task 2 `CreateScope()`, `PostgreSqlBuilder("postgres:17-alpine")` |
| 운영 | CI 리눅스 전환을 Plan 4 까지 미루면 Plan 1 회귀 검증 불가 | Task 10 Step 4 |
| 보류 | 첨부 미참조 정리, 태그 Color API, DB readiness, 바인딩 전 정책 검사 | Plan 1 범위 밖(스펙 7절 확장 포인트) |

**2차 검증 상태:** 반영본에 대한 Codex 2차 검증(thread `01a0af0d-815c-7913-8291-efd758bce008`)은 실행 중 Codex 사용량 한도(`status=quota`, 재시도 가능 시각 2026-09-18 00:38 KST)로 산출물 없이 중단됐다. 한도 해제 후 `scratchpad/codex_prompt2.md` 프롬프트로 재실행해 이 표를 갱신할 것. 구현 착수 전 2차 검증 결과를 먼저 반영하는 편이 안전하다.
