# MySQL 전환 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** PostgreSQL을 MySQL 8.4로 완전히 교체하되, 기존 보안 통제(공개 읽기 전용 롤·세션 제한·권고 잠금·낙관적 동시성·CHECK·오류→HTTP 매핑)를 MySQL 수단으로 다시 구현하고 테스트로 다시 증명한다.

**Architecture:** EF Core 10 + Oracle `MySql.EntityFrameworkCore` 10.0.9. PG 전용 수단은 인터셉터 3종(공개 세션, READ COMMITTED 트랜잭션, Post 버전), `GET_LOCK`, 단일 오류 분류기(`DbErrorClassifier`), `SHOW GRANTS` 기반 권한 검증으로 대체한다. 순수 로직은 먼저 TDD로 만든다(Task 1). 이어서 컴파일 단위인 "전환"을 두 태스크로 나눠 실행한다(Task 2: 프로덕션 코드, Task 3: 테스트 기반). 그다음 보안 통제별로 MySQL 동작을 증명하는 테스트를 새로 쓴다(Task 4~8). 마지막은 배포·CI·문서다(Task 9~11).

**Tech Stack:** .NET 10, EF Core 10.0.12, MySql.EntityFrameworkCore 10.0.9(MySql.Data 커넥터), MySQL 8.4 LTS, Testcontainers.MySql 4.15.0, xUnit 2.9, Docker Compose, Playwright.

**Spec:** `plan/mysql_migration_0926.md` — 대체표 D1~D19, 판정 R1~R6, 오류 번호표 2.4절. 실행자는 이 계획과 스펙을 함께 읽는다.

## Global Constraints

- 패키지 버전은 `Directory.Packages.props`에서만 관리한다. `MySql.EntityFrameworkCore` **10.0.9**, `Testcontainers.MySql` **4.15.0**, EF Core **10.0.12**(변경 없음).
- MySQL 이미지: 테스트는 `mysql:8.4`, 운영(compose)은 Task 0에서 확인한 최신 8.4 패치 태그(`mysql:8.4.<x>`)로 고정한다.
- 서버 인자(운영·테스트 공통): `--transaction-isolation=READ-COMMITTED --character-set-server=utf8mb4 --collation-server=utf8mb4_0900_ai_ci --local-infile=0 --innodb-lock-wait-timeout=10`. 운영은 여기에 `--secure-file-priv=NULL --require-secure-transport=ON`을 더한다.
- 연결 문자열: `SslMode=Required` 이상. **`AllowPublicKeyRetrieval=true` 금지.** 앱은 두 연결 모두에 `ConnectionReset=true`를 강제한다(`DataServiceCollectionExtensions.WithSessionReset`).
- 식별자 열 콜레이션은 `utf8mb4_bin`이다: Posts.Slug, Series.Slug, Tags.NormalizedName, Attachments.Sha256, Attachments.ContentType, Attachments.StoragePath.
- DB 정규식에는 `$`가 아니라 `\z`를 쓰고, 대소문자 구분 플래그 `'c'`를 명시한다. ICU의 `$`는 끝의 `\n` 앞에서도 매칭되기 때문이다(`SlugRules.cs:13-14`와 같은 이유).
- `INSERT IGNORE` 금지. 중복 무시는 `ON DUPLICATE KEY UPDATE \`Id\` = \`Id\``로 한다.
- 주석 규칙은 CLAUDE.md의 "인터페이스 및 API 문서화 규칙"을 따른다. public 클래스·메서드는 `<summary>`와 3항목 `<remarks>`, 테스트 메서드는 `<summary>`만 둔다. 이 계획의 코드 블록에서는 지면상 주석을 줄인 곳이 있지만, **커밋하는 코드에는 규칙대로 채운다.**
- 커밋 접두사는 한국어(`추가:`/`수정:`/`리팩토링:`/`테스트:`/`의존성:`/`문서:`)다. 마지막 줄은 `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`. 작업 브랜치는 `feat/mysql-migration`이다.
- 테스트 실행 명령: `dotnet test PortfolioBlog.slnx --filter "FullyQualifiedName~<클래스>"`. Docker Desktop이 떠 있어야 한다.
- EF `SqlQueryRaw<long>`로 서버 변수·함수를 읽을 때는 `CAST(... AS SIGNED)`로 감싼다. `CONNECTION_ID()`·`@@max_execution_time`은 BIGINT UNSIGNED이고 `@@transaction_read_only`는 INT라, 그대로 두면 `long` 읽기에서 캐스트 예외가 날 수 있다.

---

## 파일 구조

| 파일 | 책임 | 태스크 |
|---|---|---|
| `PortfolioBlog.Api/Infrastructure/Data/DbErrorClassifier.cs` (신규) | MySQL 오류 번호 → `DbErrorKind`, 예외 체인 분류 | 1 |
| `PortfolioBlog.Api/Infrastructure/Data/DbLockTimeoutException.cs` (신규) | `GET_LOCK` 타임아웃을 나타내는 예외 | 1 |
| `PortfolioBlog.Api/Infrastructure/Data/UtcDateTimeOffsetConverter.cs` (신규) | `DateTimeOffset`(UTC만) ↔ `DATETIME(6)` | 1 |
| `PortfolioBlog.Api/Infrastructure/Data/PublicRoleGrants.cs` | 공개 사용자 GRANT와 `SHOW GRANTS` 검증 (순수 판정 `Violations`는 Task 1, DB 부분은 Task 2) | 1·2 |
| `PortfolioBlog.Api/Infrastructure/Storage/AttachmentLock.cs` | `GET_LOCK` 잠금 (순수 `NameFor`는 Task 1) | 1·2 |
| `PortfolioBlog.Api/Infrastructure/Data/PublicSessionInterceptor.cs` (신규) | 공개 연결을 열 때 read_only·max_execution_time·lock_wait_timeout 설정 | 2 |
| `PortfolioBlog.Api/Infrastructure/Data/ReadCommittedTransactionInterceptor.cs` (신규) | 격리 수준을 지정하지 않은 트랜잭션을 READ COMMITTED로 시작 | 2 |
| `PortfolioBlog.Api/Infrastructure/Data/PostVersionInterceptor.cs` (신규) | Post 추가 시 Version=1, 수정 시 +1 | 2 |
| `AppDbContext.cs`, `DataServiceCollectionExtensions.cs`, `PublicDbContext.cs`, `DbConflict.cs`, `TagResolver.cs`, `PublicQueries.cs`, `LikePattern.cs`, `DbClock.cs` | 매핑·등록·SQL 교체 | 2 |
| `SeriesEndpoints.cs`, `PostEndpoints.cs`, `AttachmentEndpoints.cs`, `AttachmentJanitor.cs`, `OverloadExceptionHandler.cs`, `StartupValidation.cs`, `TextRules.cs`, `Program.cs` | 호출부 교체 | 2 |
| `Infrastructure/Data/Migrations/*` | 삭제 후 InitialCreate 재생성 | 2 |
| `PortfolioBlog.Api.Tests/Infrastructure/MySqlContainerFixture.cs` (신규, Postgres 픽스처 대체) | 컨테이너, 사용자 생성·삭제 | 3 |
| `PortfolioBlog.Api.Tests/Infrastructure/ApiFactory.cs` | 팩토리별 DB와 공개 사용자, 풀 정리 | 3 |
| 테스트 재작성·신규 | 아래 각 태스크 | 3~8 |
| `deploy/*` | compose·init·백업·복원·스모크 | 9 |
| `.github/workflows/ci.yml`, `PortfolioBlog.Web/scripts/e2e-prepare.mjs`, `playwright.config.ts` | CI·E2E | 10 |
| `README.md`, `docs/*`, `CLAUDE.md`, `AGENTS.md`, `deploy/OPERATIONS.md` | 문서 | 11 |

---

### Task 0: 브랜치와 스파이크(go/no-go)

**목적:** Oracle 프로바이더가 이 계획의 가정을 실제로 만족하는지 버리는 코드로 확인한다. **하나라도 실패하면 멈추고** 사용자에게 보고한다(후퇴안: Pomelo + EF 9, 스펙 2.1절).

**Files:**
- Create: `_workspace/mysql-spike/Spike.csproj`, `_workspace/mysql-spike/Program.cs`, `_workspace/mysql-spike/report.md`. `_workspace/`는 git 추적 대상이 아니다.

- [ ] **Step 1: 브랜치 생성**

```powershell
git switch -c feat/mysql-migration
```

- [ ] **Step 2: 스파이크 프로젝트 작성**

`_workspace/mysql-spike/Spike.csproj`. 중앙 패키지 관리를 끄고 버전을 직접 적는다. 루트 `Directory.Packages.props`의 영향을 받지 않게 하기 위해서다.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MySql.EntityFrameworkCore" Version="10.0.9" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Relational" Version="10.0.12" />
    <PackageReference Include="Testcontainers.MySql" Version="4.15.0" />
  </ItemGroup>
</Project>
```

`_workspace/mysql-spike/Program.cs`. 각 S 항목의 결과를 `PASS`/`FAIL`과 관측값으로 출력한다.

```csharp
using System.Data;
using System.Diagnostics;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using Testcontainers.MySql;

await using var container = new MySqlBuilder("mysql:8.4").WithUsername("root").WithPassword("spike-root")
    .WithCommand("--transaction-isolation=READ-COMMITTED", "--character-set-server=utf8mb4", "--local-infile=0")
    .Build();
await container.StartAsync();
var root = new MySqlConnectionStringBuilder(container.GetConnectionString()) { SslMode = MySqlSslMode.Required, Database = "spike", ConnectionReset = true }.ConnectionString;
var report = new List<string>();
void R(string id, bool ok, string note) { var line = $"{id} {(ok ? "PASS" : "FAIL")} {note}"; report.Add(line); Console.WriteLine(line); }

// S1 스키마: 콜레이션·CHECK(REGEXP_LIKE+\z)·내림차순 인덱스·Guid 매핑·DATETIME(6)
await using (var db = new SpikeDb(root)) { await db.Database.EnsureDeletedAsync(); await db.Database.EnsureCreatedAsync(); }
var ddl = (string)(await Scalar(root, "", fallback: true))!; // fallback: SHOW CREATE TABLE Items의 2열
R("S1", ddl.Contains("utf8mb4_bin") && ddl.Contains("DESC") && ddl.Contains("char(36)") && ddl.Contains("datetime(6)") && ddl.Contains("REGEXP_LIKE"), "SHOW CREATE TABLE Items:\n" + ddl);

// S2 CHECK 동작: "abc\n"은 거부되고 "abc-def"는 허용되어야 한다. 오류 번호 3819 확인
R("S2a", await ErrNo(root, "INSERT INTO Items (Id,Slug,CreatedAt,Version) VALUES (UUID(),'abc\\n',NOW(6),1)") == 3819, "슬러그 끝 개행 거부");
R("S2b", await ErrNo(root, "INSERT INTO Items (Id,Slug,CreatedAt,Version) VALUES (UUID(),'abc-def',NOW(6),1)") == 0, "정상 슬러그 허용");
R("S2c", await ErrNo(root, "INSERT INTO Items (Id,Slug,CreatedAt,Version) VALUES (UUID(),'abc-def',NOW(6),1)") == 1062, "중복 1062");
R("S2d", await ErrNo(root, "INSERT INTO Items (Id,Slug,CreatedAt,Version) VALUES (UUID(),'ABC-DEF',NOW(6),1)") == 3819, "대문자는 'c' 플래그로 거부");

// S3 공개 세션: read_only 쓰기 번호, max_execution_time 번호(쿼리별), MDL 대기 lock_wait_timeout 번호
await using (var c = await Open(root))
{
    await Exec(c, "SET SESSION transaction_read_only = ON, max_execution_time = 200, lock_wait_timeout = 1");
    R("S3a", await ErrNoOn(c, "DELETE FROM Items") == 1792, "읽기 전용 쓰기");
    var sleep = await ErrNoOn(c, "SELECT SLEEP(2)");
    var cross = await ErrNoOn(c, "SELECT COUNT(*) FROM information_schema.COLUMNS a, information_schema.COLUMNS b, information_schema.COLUMNS c");
    R("S3b", cross == 3024, $"교차 조인 → {cross}, SLEEP → {sleep} (테스트는 3024를 내는 쿼리를 쓴다)");
}
await using (var holder = await Open(root))
{
    await Exec(holder, "LOCK TABLES Items WRITE");
    await using var c = await Open(root);
    await Exec(c, "SET SESSION transaction_read_only = ON, max_execution_time = 200, lock_wait_timeout = 1");
    var sw = Stopwatch.StartNew();
    var n = await ErrNoOn(c, "SELECT COUNT(*) FROM Items");
    R("S3c", n is 1205 or 3024 && sw.Elapsed < TimeSpan.FromSeconds(3), $"MDL 대기 → {n}, {sw.ElapsedMilliseconds}ms");
    await Exec(holder, "UNLOCK TABLES");
}

// S4 권한: 없는 테이블 GRANT(1146 예상), SHOW GRANTS 형식, GRANT OPTION 사용자로 부여 가능 여부
await Exec(root, "CREATE USER 'pub'@'%' IDENTIFIED BY 'p' REQUIRE SSL; CREATE USER 'app'@'%' IDENTIFIED BY 'a' REQUIRE SSL;" +
                 "GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, DROP, INDEX, REFERENCES ON `spike`.* TO 'app'@'%' WITH GRANT OPTION");
var app = new MySqlConnectionStringBuilder(root) { UserID = "app", Password = "a" }.ConnectionString;
var pub = new MySqlConnectionStringBuilder(root) { UserID = "pub", Password = "p" }.ConnectionString;
R("S4a", await ErrNo(app, "GRANT SELECT ON `spike`.`Nope` TO 'pub'@'%'") == 1146, "없는 테이블 GRANT");
R("S4b", await ErrNo(app, "GRANT SELECT ON `spike`.`Items` TO 'pub'@'%'") == 0, "GRANT OPTION 사용자가 부여");
var grants = await Lines(pub, "SHOW GRANTS");
R("S4c", grants.Contains("GRANT USAGE ON *.* TO `pub`@`%`") && grants.Contains("GRANT SELECT ON `spike`.`Items` TO `pub`@`%`"), string.Join(" | ", grants));
R("S4d", await ErrNo(pub, "DELETE FROM Items") == 1142, "공개 사용자 DELETE 권한 거부 번호");
R("S4e", await ErrNo(app, "SELECT 1 INTO OUTFILE '/tmp/x'") is 1045 or 1227 or 1290, $"FILE 권한 없음 → {await ErrNo(app, "SELECT 1 INTO OUTFILE '/tmp/y'")}");

// S5 FK 번호, CHECK와 FK RESTRICT 동시 사용 가능 여부(에러 3823이면 설계 수정 필요)
R("S5", await ErrNo(root, "CREATE TABLE P (Id char(36) PRIMARY KEY); CREATE TABLE C (Id char(36) PRIMARY KEY, PId char(36) NULL, Ord int NULL," +
      " CONSTRAINT CK_Pair CHECK ((PId IS NULL) = (Ord IS NULL)), CONSTRAINT FK_C FOREIGN KEY (PId) REFERENCES P(Id) ON DELETE RESTRICT)") == 0, "CHECK 열에 FK RESTRICT");
await Exec(root, "INSERT INTO P VALUES ('p1'); INSERT INTO C VALUES ('c1','p1',1)");
R("S5b", await ErrNo(root, "DELETE FROM P") == 1451, "부모 삭제 1451");
R("S5c", await ErrNo(root, "INSERT INTO C VALUES ('c2','zz',1)") == 1452, "자식 삽입 1452");

// S6 GET_LOCK: 이름 64자 한계, 타임아웃 반환 0, 반환 타입, ConnectionReset=true 풀 반납 후 잠금 해제
var name = "att:12345678:" + new string('a', 48);
await using (var a = await Open(root)) await using (var b = await Open(root))
{
    var got = await ScalarOn(a, $"SELECT CAST(GET_LOCK('{name}', 10) AS SIGNED)");
    var other = await ScalarOn(b, $"SELECT CAST(GET_LOCK('{name}', 0) AS SIGNED)");
    R("S6a", got is long g && g == 1 && other is long o && o == 0, $"획득 {got} / 경쟁 {other} (타입 {got?.GetType().Name})");
}
var single = new MySqlConnectionStringBuilder(root) { MaximumPoolSize = 1 }.ConnectionString;
await using (var a = await Open(single)) await ScalarOn(a, $"SELECT GET_LOCK('{name}', 0)"); // 해제하지 않고 풀에 반납
await using (var a = await Open(single)) R("S6b", Convert.ToInt64(await ScalarOn(a, $"SELECT IS_FREE_LOCK('{name}')")) == 1, "ConnectionReset=true 재대여 시 잠금 해제");

// S7 격리 수준: 인자 없는 BeginTransaction()이 보내는 수준
await using (var c = await Open(root))
{
    await using var tx = await c.BeginTransactionAsync();
    R("S7", true, $"BeginTransaction() IsolationLevel={tx.IsolationLevel}, @@transaction_isolation={await ScalarOn(c, "SELECT @@transaction_isolation", tx)}");
}

// S8 MySqlException 생성자(테스트에서 만들 수 있는가)
var ctor = typeof(MySqlException).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, [typeof(string), typeof(int)]);
R("S8", ctor is not null, ctor is null ? "생성자 없음 → 테스트는 실제 서버 오류를 쓴다" : "(string,int) 생성자 존재");

// S9 EF 기능: ExecuteUpdate(열+1), ExecuteDelete, Like+escape, Version 동시성 토큰
await using (var db = new SpikeDb(root))
{
    db.Items.Add(new Item { Id = Guid.CreateVersion7(), Slug = "x-1", CreatedAt = DateTime.UtcNow, Version = 1 });
    await db.SaveChangesAsync();
    var n = await db.Items.Where(i => i.Slug == "x-1").ExecuteUpdateAsync(u => u.SetProperty(i => i.Version, i => i.Version + 1));
    var like = await db.Items.CountAsync(i => EF.Functions.Like(i.Slug, "x\\-%", "\\"));
    var del = await db.Items.Where(i => i.Slug == "x-1").ExecuteDeleteAsync();
    R("S9", n == 1 && like >= 0 && del == 1, $"update {n}, like {like}, delete {del}");
}

// S10 비동기 논블로킹: SLEEP(1) 200건 동시 실행 중 스레드 풀 스레드 수 증가
ThreadPool.SetMinThreads(8, 8);
var before = ThreadPool.ThreadCount;
var pooled = new MySqlConnectionStringBuilder(root) { MaximumPoolSize = 250 }.ConnectionString;
await Exec(root, "SET GLOBAL max_connections = 400");
var sw2 = Stopwatch.StartNew();
var peak = 0;
var sampler = Task.Run(async () => { while (sw2.Elapsed < TimeSpan.FromSeconds(4)) { peak = Math.Max(peak, ThreadPool.ThreadCount); await Task.Delay(50); } });
await Task.WhenAll(Enumerable.Range(0, 200).Select(async _ => { await using var c = await Open(pooled); await ScalarOn(c, "SELECT SLEEP(1)"); }));
await sampler;
// 판정은 경과 시간으로 한다: 진짜 비동기면 SLEEP(1) 200건이 약 1~3초(+TLS 핸드셰이크)에 끝난다. sync-over-async면 스레드 풀이 초당 1~2개씩만
// 늘어 200/스레드 수 ≈ 10초 이상이 걸린다. 스레드 최대치는 참고용으로만 기록한다(주입 속도가 느려 blocking이어도 60 아래로 나올 수 있다).
R("S10", sw2.Elapsed < TimeSpan.FromSeconds(5), $"경과 {sw2.ElapsedMilliseconds}ms, 스레드 {before}→최대 {peak}");

// S11 max_connections·isolation 서버 인자 반영
R("S11", (string)(await Scalar(root, "SELECT @@GLOBAL.transaction_isolation"))! == "READ-COMMITTED", "서버 인자 반영");

// S12 연결 문자열 키워드: DefaultCommandTimeout 이름, 알 수 없는 키 예외 타입과 메시지(값 노출 여부)
try { _ = new MySqlConnectionStringBuilder("Server=db.example;Bogus Keyword=1"); R("S12", false, "예외 없음"); }
catch (Exception ex) { R("S12", !ex.Message.Contains("db.example"), $"{ex.GetType().Name}: {ex.Message}"); }

File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "report.md"), report);

static async Task<MySqlConnection> Open(string cs) { var c = new MySqlConnection(cs); await c.OpenAsync(); return c; }
static async Task Exec(object target, string sql) { if (target is string cs) { await using var c = await Open(cs); await Exec(c, sql); return; } await using var cmd = new MySqlCommand(sql, (MySqlConnection)target); await cmd.ExecuteNonQueryAsync(); }
static async Task<int> ErrNo(string cs, string sql) { await using var c = await Open(cs); return await ErrNoOn(c, sql); }
static async Task<int> ErrNoOn(MySqlConnection c, string sql) { try { await Exec(c, sql); return 0; } catch (MySqlException ex) { return ex.Number; } }
static async Task<object?> ScalarOn(MySqlConnection c, string sql, MySqlTransaction? tx = null) { await using var cmd = new MySqlCommand(sql, c, tx); return await cmd.ExecuteScalarAsync(); }
static async Task<object?> Scalar(string cs, string sql, bool fallback = false)
{
    await using var c = await Open(cs);
    if (fallback) { await using var cmd = new MySqlCommand("SHOW CREATE TABLE Items", c); await using var r = await cmd.ExecuteReaderAsync(); await r.ReadAsync(); return r.GetString(1); }
    return await ScalarOn(c, sql);
}
static async Task<List<string>> Lines(string cs, string sql) { await using var c = await Open(cs); await using var cmd = new MySqlCommand(sql, c); await using var r = await cmd.ExecuteReaderAsync(); var l = new List<string>(); while (await r.ReadAsync()) l.Add(r.GetString(0)); return l; }

sealed class Item { public Guid Id { get; set; } public string Slug { get; set; } = ""; public DateTime CreatedAt { get; set; } public uint Version { get; set; } }
sealed class SpikeDb(string cs) : DbContext
{
    public DbSet<Item> Items => Set<Item>();
    protected override void OnConfiguring(DbContextOptionsBuilder o) => o.UseMySQL(cs);
    protected override void OnModelCreating(ModelBuilder b) => b.Entity<Item>(e =>
    {
        e.ToTable("Items", t => t.HasCheckConstraint("CK_Items_Slug", "REGEXP_LIKE(`Slug`, '^[a-z0-9]+(-[a-z0-9]+)*\\\\z', 'c')"));
        e.Property(x => x.Slug).HasMaxLength(100).UseCollation("utf8mb4_bin");
        e.Property(x => x.CreatedAt).HasColumnType("datetime(6)");
        e.Property(x => x.Version).IsConcurrencyToken();
        e.HasIndex(x => x.Slug).IsUnique();
        e.HasIndex(x => new { x.CreatedAt, x.Id }).IsDescending(true, false);
    });
}
```

> CHECK 식에서 C# 문자열 `\\\\z`는 SQL 텍스트 `\\z`가 되고, MySQL 문자열 리터럴 해석을 거쳐 정규식 `\z`가 된다. S2a가 이 연쇄를 검증한다.

- [ ] **Step 3: 실행과 판정**

```powershell
dotnet run --project _workspace/mysql-spike/Spike.csproj
```

**go 조건:** S1·S2a~d·S3a·S3b·S3c·S4b~e·S5·S5b·S5c·S6a·S6b·S9·S10·S11이 PASS여야 한다. S4a·S7·S8·S12는 관측값을 기록만 한다(설계가 그 결과에 의존하지 않는다).

**관측값에 따른 계획 조정**(report.md에 결정을 적는다):
- S3b: `SELECT SLEEP`이 3024를 내지 않으면 Task 5의 시간 초과 테스트는 교차 조인 쿼리를 쓴다(계획 코드가 이미 그렇게 되어 있다).
- S3c: MDL 대기 오류 번호(1205 또는 3024)를 2.4절 표에 반영한다. 둘 다 `DbErrorClassifier`가 503으로 매핑한다.
- S4a가 0이면(없는 테이블 GRANT 허용) R1의 근거 ①을 스펙에서 정정한다. 설계는 그대로 둔다.
- S4e 번호를 `deploy/smoke/run.sh`의 거부 판정 문구에 반영한다.
- S5 실패(3823)면 `CK_Posts_Series_Pair`와 FK RESTRICT 공존이 불가능하다. 멈추고 보고한다.
- S7: `IsolationLevel`이 `RepeatableRead`면 D7의 트랜잭션 인터셉터가 **필수**다. 아니어도 넣는다(이중 방어).
- S8 실패면 Task 7의 `MySqlErrors.Create`는 실제 서버 오류를 캡처하는 방식으로 대체한다(Task 7 Step 1의 대안 코드 참조).
- S12: 알 수 없는 키 예외 타입을 `StartupValidation.CheckConnection`의 catch 조건에 반영한다. 메시지에 값이 드러나면 이미 계획대로 메시지를 버린다.

**report.md 형식:** 항목별 한 줄(`S<n> PASS|FAIL 관측값`)과 마지막 "판정: go/no-go" 줄. no-go면 이후 태스크를 진행하지 않는다.

- [ ] **Step 4: 운영 이미지 패치 태그 확인**

```powershell
docker pull mysql:8.4; docker image inspect mysql:8.4 --format '{{index .Config.Env}}' | Select-String MYSQL_VERSION
```

결과 버전(예: `8.4.6`)을 report.md에 적는다. Task 9에서 `mysql:8.4.<x>`로 고정한다.

---

### Task 1: 순수 부품 — 오류 분류기, 잠금 이름, 권한 판정, UTC 변환기

**목적:** DB 없이 검증 가능한 부품을 먼저 TDD로 만든다. 이 태스크 동안 PG 코드는 그대로 두므로 두 프로바이더 패키지가 잠시 공존한다.

**Files:**
- Modify: `Directory.Packages.props` (`MySql.EntityFrameworkCore` 10.0.9 추가, Npgsql은 유지)
- Modify: `PortfolioBlog.Api/PortfolioBlog.Api.csproj` (`<PackageReference Include="MySql.EntityFrameworkCore" />` 추가)
- Create: `PortfolioBlog.Api/Infrastructure/Data/DbErrorClassifier.cs`, `DbLockTimeoutException.cs`, `UtcDateTimeOffsetConverter.cs`
- Modify: `PortfolioBlog.Api/Infrastructure/Storage/AttachmentLock.cs` (`NameFor` 추가, 기존 코드 유지)
- Modify: `PortfolioBlog.Api/Infrastructure/Data/PublicRoleGrants.cs` (`Violations` 추가, 기존 코드 유지)
- Test: `PortfolioBlog.Api.Tests/Infrastructure/DbErrorClassifierTests.cs`, `AttachmentLockNameTests.cs`, `PublicGrantViolationsTests.cs`, `UtcDateTimeOffsetConverterTests.cs`

**Interfaces:**
- Produces:
  - `enum DbErrorKind { Other, UniqueViolation, ForeignKeyViolation, CheckViolation, QueryTimeout, LockTimeout, Deadlock, PermissionDenied, ReadOnly }`
  - `static DbErrorKind DbErrorClassifier.KindOf(int number)`, `static DbErrorKind DbErrorClassifier.Classify(Exception? exception)`
  - `sealed class DbLockTimeoutException(string message) : Exception`
  - `internal static string AttachmentLock.NameFor(string databaseName, string sha256)`
  - `static IReadOnlyList<string> PublicRoleGrants.Violations(IEnumerable<string> showGrantsLines, string database, IReadOnlyList<string> readableTables)`
  - `sealed class UtcDateTimeOffsetConverter : ValueConverter<DateTimeOffset, DateTime>`

- [ ] **Step 1: 패키지 추가**

`Directory.Packages.props`의 `<ItemGroup>`에 추가한다:
```xml
    <PackageVersion Include="MySql.EntityFrameworkCore" Version="10.0.9" />
```
`PortfolioBlog.Api.csproj`의 Npgsql 참조 옆에 추가한다:
```xml
    <PackageReference Include="MySql.EntityFrameworkCore" />
```
실행: `dotnet build PortfolioBlog.slnx -warnaserror` → 성공(경고 0).

- [ ] **Step 2: 실패하는 테스트 작성**

`PortfolioBlog.Api.Tests/Infrastructure/DbErrorClassifierTests.cs`:
```csharp
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>MySQL 오류 번호와 예외 체인을 HTTP 매핑이 쓰는 의미 분류로 바꾸는 규칙을 고정한다.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>픽스처:</b> 없음(순수 함수).</description></item>
/// <item><description><b>병렬 실행:</b> 안전. 공유 상태 없음.</description></item>
/// <item><description><b>외부 자원:</b> 없음.</description></item>
/// </list>
/// </remarks>
public sealed class DbErrorClassifierTests
{
    /// <summary>스펙 2.4절 번호표가 그대로 분류되는지 확인한다. 표를 바꾸면 이 테스트가 먼저 깨진다.</summary>
    [Theory]
    [InlineData(1062, DbErrorKind.UniqueViolation)]
    [InlineData(1451, DbErrorKind.ForeignKeyViolation)]
    [InlineData(1452, DbErrorKind.ForeignKeyViolation)]
    [InlineData(3819, DbErrorKind.CheckViolation)]
    [InlineData(3024, DbErrorKind.QueryTimeout)]
    [InlineData(1205, DbErrorKind.LockTimeout)]
    [InlineData(1213, DbErrorKind.Deadlock)]
    [InlineData(1142, DbErrorKind.PermissionDenied)]
    [InlineData(1143, DbErrorKind.PermissionDenied)]
    [InlineData(1044, DbErrorKind.PermissionDenied)]
    [InlineData(1227, DbErrorKind.PermissionDenied)]
    [InlineData(1792, DbErrorKind.ReadOnly)]
    [InlineData(1064, DbErrorKind.Other)]
    [InlineData(0, DbErrorKind.Other)]
    public void KindOf_MapsTheSpecTable(int number, DbErrorKind expected) => Assert.Equal(expected, DbErrorClassifier.KindOf(number));

    /// <summary>앱이 직접 던지는 GET_LOCK 타임아웃은 감싸여 있어도 LockTimeout이다(OverloadExceptionHandler가 체인 어디서든 찾는다).</summary>
    [Fact]
    public void Classify_FindsLockTimeout_AnywhereInTheChain() =>
        Assert.Equal(DbErrorKind.LockTimeout, DbErrorClassifier.Classify(new InvalidOperationException("outer", new DbLockTimeoutException("wait"))));

    /// <summary>DB와 무관한 예외와 null은 Other다(과대 분류로 500이 503이 되지 않는다).</summary>
    [Fact]
    public void Classify_UnrelatedOrNull_IsOther()
    {
        Assert.Equal(DbErrorKind.Other, DbErrorClassifier.Classify(new InvalidOperationException("x")));
        Assert.Equal(DbErrorKind.Other, DbErrorClassifier.Classify(null));
    }
}
```

`PortfolioBlog.Api.Tests/Infrastructure/AttachmentLockNameTests.cs`:
```csharp
using PortfolioBlog.Api.Infrastructure.Storage;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>GET_LOCK 이름 규칙(스펙 D5·R3)을 고정한다.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>픽스처:</b> 없음.</description></item>
/// <item><description><b>병렬 실행:</b> 안전.</description></item>
/// <item><description><b>외부 자원:</b> 없음.</description></item>
/// </list>
/// </remarks>
public sealed class AttachmentLockNameTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>MySQL 잠금 이름 한도(64자) 안에 들어간다. 넘으면 GET_LOCK이 오류를 낸다.</summary>
    [Fact]
    public void Name_FitsTheMySqlLimit() => Assert.True(AttachmentLock.NameFor("blog_test_" + new string('f', 32), Sha).Length <= 64);

    /// <summary>잠금 이름은 서버 전역이므로 DB가 다르면 같은 SHA라도 이름이 달라야 한다(테스트의 DB별 격리).</summary>
    [Fact]
    public void DifferentDatabases_GetDifferentNames() =>
        Assert.NotEqual(AttachmentLock.NameFor("blog", Sha), AttachmentLock.NameFor("blog_test_1", Sha));

    /// <summary>같은 입력은 같은 이름이다(프로세스·인스턴스가 달라도). string.GetHashCode처럼 프로세스별 시드가 있는 해시를 쓰면 깨진다.</summary>
    [Fact]
    public void SameInput_IsDeterministic()
    {
        Assert.Equal(AttachmentLock.NameFor("blog", Sha), AttachmentLock.NameFor("blog", Sha));
        Assert.Equal("att:def53e95:", AttachmentLock.NameFor("blog", Sha)[..13]); // SHA-256("blog") 앞 8자(printf blog | sha256sum으로 확인)
    }

    /// <summary>이름이 SHA의 앞 48자를 담는다. 서로 다른 내용이 같은 잠금을 쓰는 일은 192비트 충돌뿐이다.</summary>
    [Fact]
    public void Name_CarriesTheShaPrefix() => Assert.EndsWith(":" + Sha[..48], AttachmentLock.NameFor("blog", Sha), StringComparison.Ordinal);
}
```

`PortfolioBlog.Api.Tests/Infrastructure/PublicGrantViolationsTests.cs`:
```csharp
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>공개 사용자의 SHOW GRANTS 출력이 허용 집합과 정확히 같은지 판정하는 규칙(스펙 D3·R6)을 고정한다.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>픽스처:</b> 없음(문자열 입력).</description></item>
/// <item><description><b>병렬 실행:</b> 안전.</description></item>
/// <item><description><b>외부 자원:</b> 없음.</description></item>
/// </list>
/// </remarks>
public sealed class PublicGrantViolationsTests
{
    private static readonly string[] Tables = ["Posts", "Series"];
    private const string Usage = "GRANT USAGE ON *.* TO `pub`@`%`";

    private static IReadOnlyList<string> Check(params string[] lines) => PublicRoleGrants.Violations(lines, "blog", Tables);

    /// <summary>정확히 USAGE + 허용 테이블 SELECT면 위반이 없다.</summary>
    [Fact]
    public void ExactSet_HasNoViolations() =>
        Assert.Empty(Check(Usage, "GRANT SELECT ON `blog`.`Posts` TO `pub`@`%`", "GRANT SELECT ON `blog`.`Series` TO `pub`@`%`"));

    /// <summary>Windows MySQL(lower_case_table_names=1)은 테이블 이름을 소문자로 보고한다. 이름은 대소문자 무시로 비교한다.</summary>
    [Fact]
    public void TableNames_AreComparedCaseInsensitively() =>
        Assert.Empty(Check(Usage, "GRANT SELECT ON `blog`.`posts` TO `pub`@`%`", "GRANT SELECT ON `blog`.`series` TO `pub`@`%`"));

    /// <summary>허용 테이블이 빠지면 누락으로 보고한다(공개 페이지가 1142로 깨지기 전에 기동에서 드러낸다).</summary>
    [Fact]
    public void MissingTable_IsAViolation() =>
        Assert.Contains(Check(Usage, "GRANT SELECT ON `blog`.`Posts` TO `pub`@`%`"), v => v.Contains("`blog`.`Series`", StringComparison.Ordinal));

    /// <summary>초과 권한은 전부 위반이다: 쓰기 권한, DB 단위 권한, 비허용 테이블, 다른 DB, 전역 권한, GRANT OPTION, 롤 부여.</summary>
    [Theory]
    [InlineData("GRANT SELECT, INSERT ON `blog`.`Posts` TO `pub`@`%`")]
    [InlineData("GRANT SELECT ON `blog`.* TO `pub`@`%`")]
    [InlineData("GRANT SELECT ON `blog`.`AdminState` TO `pub`@`%`")]
    [InlineData("GRANT SELECT ON `other`.`Posts` TO `pub`@`%`")]
    [InlineData("GRANT PROCESS ON *.* TO `pub`@`%`")]
    [InlineData("GRANT SELECT ON `blog`.`Series` TO `pub`@`%` WITH GRANT OPTION")]
    [InlineData("GRANT `admin_role`@`%` TO `pub`@`%`")]
    public void AnyExtraGrant_IsAViolation(string extra) =>
        Assert.Contains(extra, Check(Usage, "GRANT SELECT ON `blog`.`Posts` TO `pub`@`%`", "GRANT SELECT ON `blog`.`Series` TO `pub`@`%`", extra));
}
```

`PortfolioBlog.Api.Tests/Infrastructure/UtcDateTimeOffsetConverterTests.cs`:
```csharp
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>DATETIME(6)에 오프셋이 저장되지 않으므로 UTC만 받는 변환 규칙(스펙 D12)을 고정한다.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>픽스처:</b> 없음.</description></item>
/// <item><description><b>병렬 실행:</b> 안전.</description></item>
/// <item><description><b>외부 자원:</b> 없음.</description></item>
/// </list>
/// </remarks>
public sealed class UtcDateTimeOffsetConverterTests
{
    private static readonly UtcDateTimeOffsetConverter Converter = new();

    /// <summary>UTC 값은 마이크로초까지 왕복한다(DbClock이 절삭한 값 기준).</summary>
    [Fact]
    public void Utc_RoundTrips()
    {
        var value = DbClock.UtcNow();
        var stored = (DateTime)Converter.ConvertToProvider(value)!;
        Assert.Equal(DateTimeKind.Utc, DateTime.SpecifyKind(stored, DateTimeKind.Utc).Kind);
        Assert.Equal(value, (DateTimeOffset)Converter.ConvertFromProvider(stored)!);
    }

    /// <summary>오프셋이 0이 아닌 값은 조용히 변환하지 않고 거부한다(시간대가 섞이면 정렬·캐시 키가 어긋난다).</summary>
    [Fact]
    public void NonUtcOffset_IsRejected() =>
        Assert.Throws<InvalidOperationException>(() => Converter.ConvertToProvider(new DateTimeOffset(2026, 9, 26, 9, 0, 0, TimeSpan.FromHours(9))));

    /// <summary>DB에서 읽은 값(Kind=Unspecified)을 UTC로 해석한다.</summary>
    [Fact]
    public void FromProvider_TreatsValueAsUtc() =>
        Assert.Equal(TimeSpan.Zero, ((DateTimeOffset)Converter.ConvertFromProvider(new DateTime(2026, 9, 26, 0, 0, 0, DateTimeKind.Unspecified))!).Offset);
}
```

- [ ] **Step 3: 실패 확인**

실행: `dotnet test PortfolioBlog.slnx --filter "FullyQualifiedName~DbErrorClassifierTests|FullyQualifiedName~AttachmentLockNameTests|FullyQualifiedName~PublicGrantViolationsTests|FullyQualifiedName~UtcDateTimeOffsetConverterTests"`
예상: **빌드 실패**(`DbErrorClassifier`, `NameFor`, `Violations`, `UtcDateTimeOffsetConverter` 미정의).

- [ ] **Step 4: 구현**

`PortfolioBlog.Api/Infrastructure/Data/DbLockTimeoutException.cs`:
```csharp
namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>DB 사용자 잠금(<c>GET_LOCK</c>) 대기가 상한을 넘었다. MySQL은 이 경우 오류가 아니라 0을 반환하므로 앱이 직접 던진다.</summary>
/// <param name="message">운영 로그용 설명(비밀값 없음).</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 불변 예외 객체. 생성 후 공유해도 안전하다.</description></item>
/// <item><description><b>Memory Allocation:</b> 예외 인스턴스 1개.</description></item>
/// <item><description><b>Blocking:</b> 생성자는 즉시 반환한다.</description></item>
/// </list>
/// </remarks>
public sealed class DbLockTimeoutException(string message) : Exception(message);
```

`PortfolioBlog.Api/Infrastructure/Data/DbErrorClassifier.cs`:
```csharp
using MySql.Data.MySqlClient;

namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>호출부가 DB 오류 번호 대신 쓰는 의미 분류.</summary>
public enum DbErrorKind
{
    /// <summary>아래 어느 것에도 해당하지 않는다(500으로 남는다).</summary>
    Other,
    /// <summary>유니크 인덱스 위반(1062). 409.</summary>
    UniqueViolation,
    /// <summary>외래 키 위반(1451 부모 삭제, 1452 자식 삽입). 409.</summary>
    ForeignKeyViolation,
    /// <summary>CHECK 제약 위반(3819). 앱 검증 누락이므로 500.</summary>
    CheckViolation,
    /// <summary>실행 시간 상한 초과(3024, max_execution_time). 503.</summary>
    QueryTimeout,
    /// <summary>잠금 대기 상한 초과(1205, 또는 앱의 <see cref="DbLockTimeoutException"/>). 503.</summary>
    LockTimeout,
    /// <summary>교착으로 인한 롤백(1213). 503.</summary>
    Deadlock,
    /// <summary>권한 거부(1142·1143·1044·1227). 공개 경로가 허용 밖 테이블에 닿았다는 뜻이므로 500.</summary>
    PermissionDenied,
    /// <summary>읽기 전용 트랜잭션에서의 쓰기(1792). 500.</summary>
    ReadOnly,
}

/// <summary>MySQL 오류 번호를 한곳에서 <see cref="DbErrorKind"/>로 바꾼다. 번호는 스펙 2.4절(스파이크 실측)과 같아야 한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 정적 클래스.</description></item>
/// <item><description><b>Memory Allocation:</b> Zero-allocation(예외 체인을 읽기만 한다).</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
/// </list>
/// </remarks>
public static class DbErrorClassifier
{
    /// <summary>오류 번호 하나를 분류한다.</summary>
    /// <param name="number"><see cref="MySqlException.Number"/> 값.</param>
    /// <returns>분류. 모르는 번호는 <see cref="DbErrorKind.Other"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 순수 함수.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    public static DbErrorKind KindOf(int number) => number switch
    {
        1062 => DbErrorKind.UniqueViolation,
        1451 or 1452 => DbErrorKind.ForeignKeyViolation,
        3819 => DbErrorKind.CheckViolation,
        3024 => DbErrorKind.QueryTimeout,
        1205 => DbErrorKind.LockTimeout,
        1213 => DbErrorKind.Deadlock,
        1142 or 1143 or 1044 or 1227 => DbErrorKind.PermissionDenied,
        1792 => DbErrorKind.ReadOnly,
        _ => DbErrorKind.Other,
    };

    /// <summary>예외 자신과 <see cref="Exception.InnerException"/> 체인을 훑어 처음 만나는 DB 오류를 분류한다.</summary>
    /// <param name="exception">분류할 예외. null이면 <see cref="DbErrorKind.Other"/>.</param>
    /// <returns>분류.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 예외 객체를 읽기만 한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. SaveChanges 경로는 <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>이 감싸고 ExecuteUpdate·원시 SQL 경로는 감싸지 않으므로 체인을 끝까지 본다.</description></item>
    /// </list>
    /// </remarks>
    public static DbErrorKind Classify(Exception? exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is DbLockTimeoutException) return DbErrorKind.LockTimeout;
            if (e is MySqlException mysql) return KindOf(mysql.Number);
        }
        return DbErrorKind.Other;
    }
}
```

`PortfolioBlog.Api/Infrastructure/Data/UtcDateTimeOffsetConverter.cs`:
```csharp
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary><see cref="DateTimeOffset"/>(오프셋 0만)을 MySQL <c>DATETIME(6)</c>용 UTC <see cref="DateTime"/>으로 바꾼다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. EF가 모델당 하나를 공유하며 변환 식은 무상태다.</description></item>
/// <item><description><b>Memory Allocation:</b> 값 형식 변환이라 추가 힙 할당 없음(예외 경로 제외).</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
/// </list>
/// DATETIME에는 오프셋이 없다. 앱은 <see cref="DbClock.UtcNow"/>만 쓰므로 0이 아닌 오프셋은 버그다. 조용히 UTC로 바꾸지 않고 예외로 드러낸다.
/// </remarks>
public sealed class UtcDateTimeOffsetConverter() : ValueConverter<DateTimeOffset, DateTime>(
    value => ToUtc(value),
    value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)))
{
    private static DateTime ToUtc(DateTimeOffset value) =>
        value.Offset == TimeSpan.Zero ? value.UtcDateTime : throw new InvalidOperationException("DB에는 UTC(오프셋 0) 시각만 저장한다.");
}
```

`AttachmentLock.cs`에 기존 `KeyFor` 아래로 추가한다(`using System.Security.Cryptography; using System.Text;` 추가):
```csharp
    /// <summary>MySQL <c>GET_LOCK</c> 이름을 만든다: <c>att:</c> + DB 이름 SHA-256 앞 8자 + <c>:</c> + 내용 SHA 앞 48자(총 61자).</summary>
    /// <param name="databaseName">현재 연결의 DB 이름. 잠금 이름이 서버 전역이라 DB별로 나눈다.</param>
    /// <param name="sha256">첨부 내용의 소문자 16진 SHA-256(64자).</param>
    /// <returns>64자 이하의 잠금 이름.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 순수 함수.</description></item>
    /// <item><description><b>Memory Allocation:</b> UTF-8 바이트 배열·해시 32B·문자열 2개(수십 바이트). 업로드·삭제당 한 번이라 풀링하지 않는다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    internal static string NameFor(string databaseName, string sha256)
    {
        var dbTag = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(databaseName)))[..8];
        return $"att:{dbTag}:{sha256[..48]}";
    }
```

`PublicRoleGrants.cs`에 추가한다(기존 PG 코드 유지):
```csharp
    [GeneratedRegex("^GRANT (?<privs>.+?) ON (?<obj>\\S+) TO ")]
    private static partial Regex GrantLine();

    /// <summary>공개 사용자의 <c>SHOW GRANTS</c> 출력이 {USAGE ON *.*} ∪ {현재 DB 허용 테이블 SELECT}와 정확히 같은지 판정한다.</summary>
    /// <param name="showGrantsLines">공개 연결에서 실행한 <c>SHOW GRANTS</c>의 각 행.</param>
    /// <param name="database">현재 DB 이름.</param>
    /// <param name="readableTables">SELECT를 허용할 테이블 목록.</param>
    /// <returns>위반 설명 목록. 비어 있으면 통과.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 순수 함수.</description></item>
    /// <item><description><b>Memory Allocation:</b> 기대 집합 HashSet 1개와 위반 목록. 기동 시 한 번만 호출된다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// 테이블 이름은 대소문자를 무시하고 비교한다(Windows MySQL은 소문자로 보고). 롤 부여 행(<c>ON</c> 없음)·<c>WITH GRANT OPTION</c>·다른 DB·전역 권한은 모두 위반이다.
    /// </remarks>
    public static IReadOnlyList<string> Violations(IEnumerable<string> showGrantsLines, string database, IReadOnlyList<string> readableTables)
    {
        var expected = readableTables.Select(t => $"`{database}`.`{t}`").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var violations = new List<string>();
        foreach (var line in showGrantsLines)
        {
            var match = GrantLine().Match(line);
            if (!match.Success || line.Contains(" WITH GRANT OPTION", StringComparison.Ordinal)) { violations.Add(line); continue; }
            var privileges = match.Groups["privs"].Value;
            var target = match.Groups["obj"].Value;
            if (privileges == "USAGE" && target == "*.*") continue;
            if (privileges == "SELECT" && expected.Contains(target)) { seen.Add(target); continue; }
            violations.Add(line);
        }
        violations.AddRange(expected.Where(t => !seen.Contains(t)).Select(t => $"누락: SELECT ON {t}"));
        return violations;
    }
```

- [ ] **Step 5: 통과 확인**

실행: Step 3과 같은 명령. 예상: 전부 PASS. 이어서 `dotnet test PortfolioBlog.slnx` 전체를 실행한다. 기존 PG 테스트도 전부 PASS여야 한다(아직 PG 경로를 건드리지 않았다).

- [ ] **Step 6: 커밋**

```powershell
git add Directory.Packages.props PortfolioBlog.Api PortfolioBlog.Api.Tests
git commit -m "추가: MySQL 전환에 쓸 오류 분류기·잠금 이름·권한 판정·UTC 변환기" -m "PG 경로를 바꾸기 전에 DB 없이 검증되는 부품을 먼저 고정한다" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: 전환 ① — 프로덕션 코드를 MySQL로 (Npgsql 제거)

**목적:** API 프로젝트에서 Npgsql을 없애고 MySQL로 기동되게 한다. 테스트 프로젝트는 Task 3에서 고치므로, 이 태스크의 완료 기준은 **API 빌드 + 로컬 MySQL로 실제 기동·마이그레이션·헬스 200**이다.

**Files:**
- Modify: `Directory.Packages.props` (Npgsql 줄 삭제), `PortfolioBlog.Api/PortfolioBlog.Api.csproj` (Npgsql 참조 삭제)
- Create: `Infrastructure/Data/PublicSessionInterceptor.cs`, `ReadCommittedTransactionInterceptor.cs`, `PostVersionInterceptor.cs`
- Modify: `AppDbContext.cs`, `DataServiceCollectionExtensions.cs`, `PublicDbContext.cs`, `PublicRoleGrants.cs`, `DbConflict.cs`, `TagResolver.cs`, `PublicQueries.cs:89-92`, `LikePattern.cs`(주석), `DbClock.cs`(주석), `Features/Posts/PostEndpoints.cs:70-81`, `Features/Series/SeriesEndpoints.cs:188-218`, `Features/Attachments/AttachmentEndpoints.cs:168`, `Infrastructure/Storage/AttachmentLock.cs`, `Infrastructure/Storage/AttachmentJanitor.cs:126-137`, `Infrastructure/Web/OverloadExceptionHandler.cs:46-70`, `Infrastructure/Access/StartupValidation.cs`, `Contracts/TextRules.cs`(주석), `Program.cs:82-88`, `appsettings.Development.json`
- Delete + Create: `Infrastructure/Data/Migrations/*` → `dotnet ef migrations add InitialCreate`

**Interfaces:**
- Consumes (Task 1): `DbErrorClassifier`, `DbErrorKind`, `DbLockTimeoutException`, `AttachmentLock.NameFor`, `PublicRoleGrants.Violations`, `UtcDateTimeOffsetConverter`
- Produces:
  - `public static string DataServiceCollectionExtensions.WithSessionReset(string connectionString)` — `ConnectionReset=true`를 강제한 문자열. 테스트가 풀을 비울 때 같은 키로 쓴다.
  - `public sealed class PublicSessionInterceptor(int maxExecutionMs) : DbConnectionInterceptor`
  - `public sealed class ReadCommittedTransactionInterceptor : DbTransactionInterceptor`
  - `public sealed class PostVersionInterceptor : SaveChangesInterceptor`
  - `public static void PublicRoleGrants.Apply(AppDbContext admin, PublicDbContext publicDb)` (시그니처 변경)
  - `public static string PublicRoleGrants.RoleOf(string publicConnectionString)` (MySQL 파싱)
  - `internal static Task<IAsyncDisposable> AttachmentLock.HoldAsync(AppDbContext db, string sha256, int waitSeconds, CancellationToken ct)` + 기존 public 오버로드(10초)
  - `public const int AttachmentLock.WaitSeconds = 10`
  - `AppDbContext.SlugPatternSql`(DB용, `\z` 앵커) — `SlugPattern`(기존, 문서·앱 참조용)은 유지

- [ ] **Step 1: 패키지 교체**

`Directory.Packages.props`에서 `Npgsql.EntityFrameworkCore.PostgreSQL` 줄을 지운다. `PortfolioBlog.Api.csproj`에서 Npgsql `PackageReference`를 지운다.

- [ ] **Step 2: 인터셉터 3종 작성**

`Infrastructure/Data/PublicSessionInterceptor.cs`:
```csharp
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>공개 조회 연결이 열릴 때마다 세션을 읽기 전용으로 두고, SELECT 실행 시간과 메타데이터 잠금 대기에 상한을 건다(스펙 D2).</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 불변 SQL 문자열만 갖고 컨텍스트 간에 공유된다. 콜백은 연결을 연 호출 스레드(요청 스레드)에서 실행된다.</description></item>
/// <item><description><b>Memory Allocation:</b> 연결 열기당 명령 객체 1개. SQL 문자열은 생성자에서 한 번 만든다.</description></item>
/// <item><description><b>Blocking:</b> 비동기 경로는 DB 왕복 1회를 await한다(연결을 열 때마다 1회, 스펙 R2). 동기 경로는 동기 왕복이다.</description></item>
/// </list>
/// MySql.Data에는 PG의 시작 매개변수가 없어 연결마다 설정한다. 연결 문자열에 <c>ConnectionReset=true</c>가 강제되어 있어(풀에서 꺼낼 때 세션 리셋)
/// 이전 대여자가 바꾼 세션 값이 남지 않고, 이 인터셉터가 리셋 직후 다시 설정한다. <c>lock_wait_timeout</c>(초, 최소 1)은 메타데이터 잠금 대기
/// (예: 누군가 <c>LOCK TABLES</c>)를 끊는다. InnoDB 일반 SELECT는 행 잠금을 기다리지 않으므로 이것이 공개 경로의 유일한 무한 대기 지점이다.
/// </remarks>
public sealed class PublicSessionInterceptor : DbConnectionInterceptor
{
    private readonly string _sql;

    /// <summary>세션 설정 SQL을 만든다.</summary>
    /// <param name="maxExecutionMs">SELECT 실행 상한(밀리초, 100~60000 — StartupValidation이 먼저 검증한다).</param>
    /// <exception cref="ArgumentOutOfRangeException">범위를 벗어났을 때. 정수만 SQL에 들어가므로 주입 여지는 없지만 범위 밖 값은 설정 오류다.</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 생성 후 불변.</description></item>
    /// <item><description><b>Memory Allocation:</b> SQL 문자열 1개.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    public PublicSessionInterceptor(int maxExecutionMs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxExecutionMs, 100);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxExecutionMs, 60_000);
        var lockWaitSeconds = Math.Max(1, (maxExecutionMs + 999) / 1000);
        _sql = string.Create(CultureInfo.InvariantCulture,
            $"SET SESSION transaction_read_only = ON, max_execution_time = {maxExecutionMs}, lock_wait_timeout = {lockWaitSeconds}");
    }

    /// <inheritdoc />
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = _sql;
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = _sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
```
(`<inheritdoc />` 메서드도 커밋 전에 3항목 `<remarks>`를 채운다. Thread Context는 호출 스레드, 할당은 명령 1개, 블로킹은 동기/비동기 왕복 1회다.)

`Infrastructure/Data/ReadCommittedTransactionInterceptor.cs`:
```csharp
using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>격리 수준을 지정하지 않은 모든 트랜잭션(SaveChanges 암묵 트랜잭션, <c>BeginTransactionAsync()</c>)을 READ COMMITTED로 시작한다(스펙 D7).</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 무상태. 콜백은 트랜잭션을 시작한 호출 스레드에서 실행된다.</description></item>
/// <item><description><b>Memory Allocation:</b> 트랜잭션 객체 1개(원래도 만들어질 것을 대신 만든다). 추가 할당 없음.</description></item>
/// <item><description><b>Blocking:</b> 비동기 경로는 <c>BeginTransactionAsync</c>를 await한다. 추가 왕복은 없다(원래 시작 문장을 대체).</description></item>
/// </list>
/// 기존 동시성 설계(SeriesEndpoints의 FOR UPDATE, TagResolver의 서수 삽입)는 전부 READ COMMITTED를 전제로 한다. MySql.Data의 인자 없는
/// <c>BeginTransaction()</c>이 서버 기본값과 무관하게 REPEATABLE READ를 보낼 수 있어(스파이크 S7) 서버 설정만으로는 보장되지 않는다.
/// 호출부가 격리 수준을 명시하면 그대로 둔다.
/// </remarks>
public sealed class ReadCommittedTransactionInterceptor : DbTransactionInterceptor
{
    /// <inheritdoc />
    public override InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result) =>
        eventData.IsolationLevel == IsolationLevel.Unspecified
            ? InterceptionResult<DbTransaction>.SuppressWithResult(connection.BeginTransaction(IsolationLevel.ReadCommitted))
            : result;

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection, TransactionStartingEventData eventData,
        InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default) =>
        eventData.IsolationLevel == IsolationLevel.Unspecified
            ? InterceptionResult<DbTransaction>.SuppressWithResult(await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
            : result;
}
```

`Infrastructure/Data/PostVersionInterceptor.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PortfolioBlog.Api.Domain;

namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>PG <c>xmin</c>을 대신하는 앱 관리 행 버전(스펙 D4): 추가되는 Post는 1, 수정되는 Post는 원래 값 + 1.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe(무상태). 컨텍스트 자체는 스레드 안전하지 않으므로 SaveChanges를 호출한 스레드에서만 그 컨텍스트를 만진다.</description></item>
/// <item><description><b>Memory Allocation:</b> 추적 중인 Post 엔트리 열거자 1개. DetectChanges 비용은 SaveChanges가 어차피 치르는 것을 앞당길 뿐이다.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
/// </list>
/// SaveChanges는 인터셉터를 부른 <b>뒤에</b> DetectChanges를 하므로, 여기서 먼저 DetectChanges를 불러야 속성 설정으로 바뀐 Post가 Modified로 보인다.
/// 호출부가 <c>OriginalValue</c>에 클라이언트 버전을 넣어 두면(PostEndpoints) WHERE는 그 값으로, SET은 +1로 나간다. 버전이 달라졌으면 0행이 갱신되어
/// <see cref="DbUpdateConcurrencyException"/>이 된다. 벌크 경로(<c>ExecuteUpdateAsync</c>)는 이 인터셉터를 거치지 않으므로 호출부가 직접 올린다.
/// 이 규칙은 <c>PostVersionTests.EveryPostsBulkUpdate_BumpsVersion</c>이 소스 스캔으로 강제한다.
/// </remarks>
public sealed class PostVersionInterceptor : SaveChangesInterceptor
{
    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Bump(eventData.Context);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Bump(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void Bump(DbContext? context)
    {
        if (context is null) return;
        context.ChangeTracker.DetectChanges();
        foreach (var entry in context.ChangeTracker.Entries<Post>())
        {
            if (entry.State == EntityState.Added) entry.Entity.Version = 1;
            else if (entry.State == EntityState.Modified)
            {
                var version = entry.Property(p => p.Version);
                version.CurrentValue = version.OriginalValue + 1;
            }
        }
    }
}
```

- [ ] **Step 3: 등록 교체 — `DataServiceCollectionExtensions.cs`**

`using MySql.Data.MySqlClient;`를 추가하고 `AddBlogData` 본문을 교체한다:
```csharp
        services.AddSingleton<ReadCommittedTransactionInterceptor>();
        services.AddSingleton<PostVersionInterceptor>();
        services.AddDbContext<AppDbContext>((sp, o) => o
            .UseMySQL(WithSessionReset(RequireConnectionString(sp)))
            .AddInterceptors(sp.GetRequiredService<ReadCommittedTransactionInterceptor>(), sp.GetRequiredService<PostVersionInterceptor>()));
        services.AddDbContext<PublicDbContext>((sp, o) => o
            .UseMySQL(WithSessionReset(PublicOrDefaultConnectionString(sp)))
            .AddInterceptors(new PublicSessionInterceptor(sp.GetRequiredService<IOptions<PublicOptions>>().Value.StatementTimeoutMs))
            // 공개 경로는 추적할 이유가 없다: 변경 추적기 할당을 없애고, 실수로 엔티티를 고쳐도 저장 대상이 되지 않는다.
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));
        return services;
```
클래스에 추가한다(XML 주석 3항목 포함):
```csharp
    /// <summary>풀에서 꺼낼 때마다 세션을 리셋하도록 <c>ConnectionReset=true</c>를 강제한 연결 문자열을 만든다.</summary>
    /// <param name="connectionString">설정의 연결 문자열.</param>
    /// <returns>정규화된 연결 문자열(풀 키). 테스트가 풀을 비울 때도 이 값을 쓴다.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 순수 함수.</description></item>
    /// <item><description><b>Memory Allocation:</b> 빌더 1개와 결과 문자열 1개. 컨텍스트 옵션을 만들 때만 호출된다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// 리셋이 필요한 이유는 세 가지다. ① 공개 세션 설정(read_only 등)이 같은 풀을 쓰는 관리 연결로 새지 않게 한다(Development는 Public이 없으면 Default를 공유한다).
    /// ② 해제에 실패한 <c>GET_LOCK</c>이 다음 대여 때 풀린다(스파이크 S6b). ③ 공개 세션이 스스로 끈 read_only가 다음 대여로 이어지지 않는다.
    /// 대가는 풀 대여마다 COM_RESET_CONNECTION 왕복 1회다.
    /// </remarks>
    public static string WithSessionReset(string connectionString) =>
        new MySqlConnectionStringBuilder(connectionString) { ConnectionReset = true }.ConnectionString;
```

- [ ] **Step 4: 매핑 교체 — `AppDbContext.cs`**

상수를 추가한다(`SlugPattern` 아래):
```csharp
    /// <summary>DB CHECK용 slug 정규식. ICU의 <c>$</c>는 끝의 <c>\n</c> 앞에서도 매칭되므로 끝 앵커로 <c>\z</c>를 쓴다. SQL 문자열 리터럴 안에 들어가므로 백슬래시를 두 번 쓴다.</summary>
    public const string SlugPatternSql = "^[a-z0-9]+(-[a-z0-9]+)*\\\\z";

    /// <summary>식별자 열(유니크 비교가 바이트 단위여야 하는 열)의 콜레이션.</summary>
    public const string BinaryCollation = "utf8mb4_bin";
```
`OnModelCreating` 앞에 추가한다:
```csharp
    /// <summary>모든 <see cref="DateTimeOffset"/>을 UTC <c>DATETIME(6)</c>로 저장한다(스펙 D12).</summary>
    /// <param name="configurationBuilder">EF 규약 빌더.</param>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>().HaveColumnType("datetime(6)");
```
`OnModelCreating`의 변경 줄. 나머지(인덱스·FK·HasData)는 그대로 둔다.
```csharp
        // Post
            e.Property(x => x.Slug).HasMaxLength(SlugMax).UseCollation(BinaryCollation);
            e.Property(x => x.Version).IsConcurrencyToken(); // PG xmin 대체: 앱이 올린다(PostVersionInterceptor, 벌크 경로는 호출부)
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Posts_Slug_Format", $"REGEXP_LIKE(`Slug`, '{SlugPatternSql}', 'c')");
                t.HasCheckConstraint("CK_Posts_Title_NotBlank", "CHAR_LENGTH(TRIM(`Title`)) > 0");
                t.HasCheckConstraint("CK_Posts_Content_Size", $"LENGTH(`ContentMarkdown`) <= {ContentMaxBytes}"); // LENGTH = 바이트
                t.HasCheckConstraint("CK_Posts_Series_Pair", "(`SeriesId` IS NULL) = (`SeriesOrder` IS NULL)");
                t.HasCheckConstraint("CK_Posts_SeriesOrder_Positive", "`SeriesOrder` IS NULL OR `SeriesOrder` > 0");
            });
        // Series
            e.Property(x => x.Slug).HasMaxLength(SlugMax).UseCollation(BinaryCollation);
            e.ToTable("Series", t =>
            {
                t.HasCheckConstraint("CK_Series_Slug_Format", $"REGEXP_LIKE(`Slug`, '{SlugPatternSql}', 'c')");
                t.HasCheckConstraint("CK_Series_Title_NotBlank", "CHAR_LENGTH(TRIM(`Title`)) > 0");
            });
        // Tag
            e.Property(x => x.NormalizedName).HasMaxLength(TagMax).UseCollation(BinaryCollation);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Tags_Name_NotBlank", "CHAR_LENGTH(TRIM(`Name`)) > 0");
                t.HasCheckConstraint("CK_Tags_Name_NoSlash", "LOCATE('/', `Name`) = 0");
            });
        // AdminState
            e.ToTable("AdminState", t => t.HasCheckConstraint("CK_AdminState_Single", $"`Id` = {AdminState.SingletonId}"));
        // Attachment
            e.Property(x => x.ContentType).HasMaxLength(20).UseCollation(BinaryCollation);
            e.Property(x => x.StoragePath).HasMaxLength(80).UseCollation(BinaryCollation);
            e.Property(x => x.Sha256).HasMaxLength(64).UseCollation(BinaryCollation);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Attachments_Size", "`SizeBytes` BETWEEN 1 AND 10485760");
                t.HasCheckConstraint("CK_Attachments_Sha256", "REGEXP_LIKE(`Sha256`, '^[0-9a-f]{64}\\\\z', 'c')");
                t.HasCheckConstraint("CK_Attachments_ContentType", "`ContentType` IN ('image/png', 'image/jpeg', 'image/gif', 'image/webp')");
                t.HasCheckConstraint("CK_Attachments_FileName_NotBlank", "CHAR_LENGTH(TRIM(`FileName`)) > 0");
            });
```

- [ ] **Step 5: `PublicDbContext.cs` 정리**

`using Npgsql;`와 `BuildConnectionString` 메서드(주석 포함)를 지운다. 클래스 remarks의 PG 서술(25006, pg_backend_pid, Npgsql 리셋)은 다음 요지로 바꾼다: "쓰기 금지는 세 겹이다 — ① 이 클래스의 SaveChanges 예외, ② `PublicSessionInterceptor`의 `transaction_read_only=ON`(1792), ③ 공개 사용자의 테이블 단위 SELECT 권한(1142). 세션 값은 `ConnectionReset=true`로 대여마다 리셋되고 인터셉터가 다시 설정한다(측정: `PublicDbContextTests`)."

- [ ] **Step 6: `PublicRoleGrants.cs` 교체**

`using Npgsql;`를 `using MySql.Data.MySqlClient;`로 바꾼다. `ReadableTables`, `GrantLine`, `Violations`(Task 1)는 유지한다. 나머지는 아래로 교체한다(`BuildStatements`·`QuoteIdentifier`·기존 `Apply` 삭제).
```csharp
    // MySQL 사용자 이름 한도 32자. 소문자·숫자·밑줄만 허용해 GRANT 문장에 인용 없이 넣을 수 있게 한다.
    [GeneratedRegex("^[a-z_][a-z0-9_]{0,31}\\z")]
    private static partial Regex UserNamePattern();

    // DB 이름은 백틱 인용하지만, 인용 탈출을 원천 차단하려고 형식도 제한한다.
    [GeneratedRegex("^[A-Za-z0-9_]{1,64}\\z")]
    private static partial Regex DatabaseNamePattern();

    public static string RoleOf(string publicConnectionString)
    {
        MySqlConnectionStringBuilder parsed;
        try
        {
            parsed = new MySqlConnectionStringBuilder(publicConnectionString);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            // 예외 메시지에 연결 문자열 조각이 들어갈 수 있어 옮기지 않는다(StartupValidationTests가 비노출을 고정).
            throw new InvalidOperationException("설정 ConnectionStrings:Public 이(가) 잘못되었습니다(연결 문자열 형식).", ex);
        }
        var user = parsed.UserID;
        return !string.IsNullOrEmpty(user) && UserNamePattern().IsMatch(user)
            ? user
            : throw new InvalidOperationException("ConnectionStrings:Public 의 User ID 는 소문자·숫자·밑줄로 된 32자 이하 사용자 이름이어야 합니다.");
    }

    public static IReadOnlyList<string> BuildGrantStatements(string user, string database)
    {
        if (!UserNamePattern().IsMatch(user)) throw new ArgumentException("검증되지 않은 사용자 이름", nameof(user));
        if (!DatabaseNamePattern().IsMatch(database)) throw new ArgumentException("검증되지 않은 DB 이름", nameof(database));
        return ReadableTables.Select(table => $"GRANT SELECT ON `{database}`.`{table}` TO '{user}'@'%'").ToList();
    }

    public static void Apply(AppDbContext admin, PublicDbContext publicDb)
    {
        var user = RoleOf(publicDb.Database.GetConnectionString()!);
        var adminUser = new MySqlConnectionStringBuilder(admin.Database.GetConnectionString()).UserID;
        if (string.Equals(user, adminUser, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ConnectionStrings:Public 의 User ID 가 ConnectionStrings:Default 와 같습니다 — 공개 조회는 별도의 읽기 전용 사용자여야 합니다.");
        }
        var database = admin.Database.GetDbConnection().Database;
        foreach (var statement in BuildGrantStatements(user, database))
        {
            // 문장은 형식 검증된 식별자와 상수 목록으로만 조립된다(사용자 입력 없음). GRANT 대상은 SQL 매개변수가 될 수 없다.
#pragma warning disable EF1002
            admin.Database.ExecuteSqlRaw(statement);
#pragma warning restore EF1002
        }
        var lines = new List<string>();
        publicDb.Database.OpenConnection(); // 인터셉터가 공개 세션 설정을 건다. SHOW GRANTS는 자기 자신에 대해 항상 허용된다.
        try
        {
            using var command = publicDb.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SHOW GRANTS";
            using var reader = command.ExecuteReader();
            while (reader.Read()) lines.Add(reader.GetString(0));
        }
        finally
        {
            publicDb.Database.CloseConnection();
        }
        var violations = Violations(lines, database, ReadableTables);
        if (violations.Count > 0)
        {
            throw new InvalidOperationException("공개 조회 사용자의 권한이 허용 목록과 다릅니다(자동 회수하지 않음 — 운영자가 REVOKE 후 재기동): " + string.Join(" | ", violations));
        }
    }
```
모든 public 멤버에 XML 주석을 단다. `Apply` remarks의 요지: Thread Safety는 기동 시 단일 스레드, Memory는 문장 5개와 행 목록, Blocking은 **동기** DB 왕복 5+2회(기동 경로 전용). R1(GRANT OPTION 근거)과 R6(자동 회수하지 않음)을 참조한다.

- [ ] **Step 7: 호출부 교체**

`Program.cs:82-88`:
```csharp
using (var scope = app.Services.CreateScope())
{
    var adminDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    adminDb.Database.Migrate();
    var publicConnection = app.Configuration.GetConnectionString("Public");
    if (!string.IsNullOrWhiteSpace(publicConnection)) PublicRoleGrants.Apply(adminDb, scope.ServiceProvider.GetRequiredService<PublicDbContext>());
}
```

`DbConflict.cs` — 상수 두 개와 `using Npgsql;`를 지운다:
```csharp
    public static bool IsConstraintRace(DbUpdateException ex) =>
        DbErrorClassifier.Classify(ex) is DbErrorKind.UniqueViolation or DbErrorKind.ForeignKeyViolation;
```

`TagResolver.cs:112` — 삽입 SQL(주석의 "PostgreSQL이 데드락(40P01)" → "InnoDB가 교착(1213)"):
```csharp
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO `Tags` (`Id`, `Name`, `NormalizedName`) VALUES ({id}, {display}, {normalized}) ON DUPLICATE KEY UPDATE `Id` = `Id`", ct);
```
(`INSERT IGNORE` 금지 이유를 인라인 주석으로 남긴다: 잘림·CHECK 위반까지 경고로 삼킨다.)

`SeriesEndpoints.cs` — `using Npgsql;`를 지우고 `DeleteAsync` 본문 세 곳을 바꾼다:
```csharp
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM `Series` WHERE `Id` = {id} FOR UPDATE", ct);

        // CK_Posts_Series_Pair 때문에 두 필드를 같은 UPDATE에서 함께 비운다. UpdatedAt은 건드리지 않는다(글 내용이 바뀐 게 아니다).
        // Version은 올린다: 시리즈 내비게이션이 렌더 결과에 들어가므로 RenderedPostCache 키가 바뀌어야 한다(PG에서는 xmin이 자동으로 바뀌었다).
        await db.Posts.Where(p => p.SeriesId == id)
            .ExecuteUpdateAsync(u => u
                .SetProperty(p => p.SeriesId, (Guid?)null)
                .SetProperty(p => p.SeriesOrder, (int?)null)
                .SetProperty(p => p.Version, p => p.Version + 1), ct);
        ...
        catch (Exception ex) when (DbErrorClassifier.Classify(ex) == DbErrorKind.ForeignKeyViolation)
```
(FOR UPDATE 주석의 PG 용어 "FOR KEY SHARE"는 "InnoDB는 자식 INSERT의 FK 검사 때 부모 행에 공유 잠금(S)을 건다"로 바꾼다.)

`PostEndpoints.cs:70-81`과 `PublicQueries.cs:89-92` — `EF.Functions.ILike` → `EF.Functions.Like`(인자 그대로). 주석의 "ILIKE"와 "22021"은 "LIKE(검색 열은 utf8mb4_0900_ai_ci라 대소문자 무시)"와 "NUL은 정책상 거부(스펙 D15)"로 바꾼다.

`AttachmentEndpoints.cs:168` — `using Npgsql;`를 지운다:
```csharp
            catch (DbUpdateException ex) when (DbErrorClassifier.Classify(ex) == DbErrorKind.UniqueViolation)
```
같은 파일 110·195행 주석의 "SqlState 55P03"을 "`DbLockTimeoutException`(GET_LOCK 10초)"으로 바꾼다.

`AttachmentJanitor.cs:126-137` — `using Npgsql;`를 지운다:
```csharp
    // 잠금 대기 초과만 좁게 본다(OverloadExceptionHandler.IsOverload는 실행 시간 초과·렌더 포화까지 포함하는 더 넓은 판정이다).
    private static bool IsLockTimeout(Exception exception) => DbErrorClassifier.Classify(exception) == DbErrorKind.LockTimeout;
```

`OverloadExceptionHandler.cs:64-70` — `using Npgsql;`를 지운다:
```csharp
    internal static bool IsOverload(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is RenderBusyException) return true;
        }
        return DbErrorClassifier.Classify(exception) is DbErrorKind.QueryTimeout or DbErrorKind.LockTimeout or DbErrorKind.Deadlock;
    }
```
remarks의 57014·55P03을 3024·1205·`DbLockTimeoutException`·1213으로 바꾼다. **교착(1213)을 503으로 올리는 것은 새 결정이다**(PG 40P01은 500이었다). 교착은 재시도하면 성공하는 일시 과부하라 Retry-After가 맞다. 이 결정은 스펙 2.4절에 이미 있다.

`AttachmentLock.cs` — 파일 전체를 교체한다(`NameFor`는 Task 1 것을 유지하고, `KeyFor`는 삭제한다. 테스트 사용처는 Task 6에서 교체한다):
```csharp
    /// <summary>기본 잠금 대기 상한(초). PG 판의 lock_timeout 10초와 같다.</summary>
    public const int WaitSeconds = 10;

    public static Task<IAsyncDisposable> HoldAsync(AppDbContext db, string sha256, CancellationToken ct) => HoldAsync(db, sha256, WaitSeconds, ct);

    internal static async Task<IAsyncDisposable> HoldAsync(AppDbContext db, string sha256, int waitSeconds, CancellationToken ct)
    {
        // 연결을 명시적으로 열어 둔다: 사용자 잠금은 "잡은 세션"에 묶이므로, 잡은 뒤의 모든 EF 명령이 같은 연결을 써야 한다.
        await db.Database.OpenConnectionAsync(ct);
        string name;
        try
        {
            name = NameFor(db.Database.GetDbConnection().Database, sha256);
            // CAST(... AS SIGNED): GET_LOCK의 반환 타입을 BIGINT로 고정해 long?로 읽는다(1=획득, 0=타임아웃, NULL=오류).
            var acquired = await db.Database.SqlQuery<long?>($"SELECT CAST(GET_LOCK({name}, {waitSeconds}) AS SIGNED) AS `Value`").SingleAsync(ct);
            if (acquired != 1) throw new DbLockTimeoutException($"첨부 잠금 대기 {waitSeconds}초 초과");
        }
        catch
        {
            await db.Database.CloseConnectionAsync();
            throw;
        }
        return new Releaser(db, name);
    }

    private sealed class Releaser(AppDbContext db, string name) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                try
                {
                    // 요청이 취소됐어도 잠금은 풀어야 하므로 취소 토큰을 넘기지 않는다.
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT RELEASE_LOCK({name})", CancellationToken.None);
                }
                catch
                {
                    // 해제 실패 시: 연결이 끊겼으면 세션 종료와 함께 잠금도 풀린다. 연결이 살아 있으면 풀에 반납되고,
                    // 다음 대여 때 ConnectionReset=true의 COM_RESET_CONNECTION이 잠금을 푼다(스파이크 S6b, AttachmentIntegrityTests가 측정).
                    // 그때까지 같은 내용의 요청은 WaitSeconds 뒤 503을 받는다. 다시 던지면 원래 예외를 가리므로 삼킨다.
                }
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }
```
클래스·메서드 XML 주석은 PG 서술(55P03·pg_advisory·Npgsql 리셋 추론)을 GET_LOCK 기준으로 다시 쓴다. Blocking 항목은 "비동기 대기, 상한 `waitSeconds` 초 뒤 `DbLockTimeoutException`"이다.

`StartupValidation.cs` — `using Npgsql;`를 `using MySql.Data.MySqlClient;`로 바꾼다. 같은 사용자 비교의 `new NpgsqlConnectionStringBuilder(connectionString).Username`은 `new MySqlConnectionStringBuilder(connectionString).UserID`로 바꾼다. `CheckConnectionString`을 교체한다:
```csharp
    private static MySqlConnectionStringBuilder CheckConnectionString(string key, string connectionString, int statementTimeoutMs, bool requireTls)
    {
        MySqlConnectionStringBuilder parsed;
        try
        {
            parsed = new MySqlConnectionStringBuilder(connectionString);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            // 메시지를 옮기지 않는다: 커넥터 예외 메시지에 연결 문자열 조각(호스트 등)이 들어갈 수 있다.
            throw new InvalidOperationException($"설정 {key} 이(가) 잘못되었습니다(연결 문자열 형식).", ex);
        }
        if (parsed.AllowPublicKeyRetrieval)
        {
            throw new InvalidOperationException($"{key} 에 AllowPublicKeyRetrieval=true 를 쓸 수 없습니다 — 공개키를 바꿔치기해 비밀번호를 빼낼 수 있습니다. SslMode=Required 로 연결하세요.");
        }
        if (requireTls && parsed.SslMode is not (MySqlSslMode.Required or MySqlSslMode.VerifyCA or MySqlSslMode.VerifyFull))
        {
            throw new InvalidOperationException($"Development가 아닌 환경에서는 {key} 의 SslMode 가 Required 이상이어야 합니다.");
        }
        // DefaultCommandTimeout(초, 0=무한)이 max_execution_time(밀리초)보다 먼저 끊기면 클라이언트 취소가 DB 시간 제한(3024)보다 먼저 나서
        // OverloadExceptionHandler의 503 매핑이 깨진다.
        if (parsed.DefaultCommandTimeout != 0 && parsed.DefaultCommandTimeout * 1000L <= statementTimeoutMs)
        {
            throw new InvalidOperationException($"{key} 의 Default Command Timeout(초)이 Public:StatementTimeoutMs(밀리초)보다 커야 합니다.");
        }
        return parsed;
    }
```
두 호출부에 `requireTls: !environment.IsDevelopment()`를 넘긴다. Options 관련 주석과 분기는 지운다. 파일 끝의 `// 공개 조회가 관리 롤(테이블 소유자)로 돌면 default_transaction_read_only만 남는다` 주석은 "공개 조회가 관리 사용자로 돌면 세션 read_only만 남는다 — 세션이 스스로 끌 수 있다"로 바꾼다.

`TextRules.cs`, `DbClock.cs`, `LikePattern.cs`의 주석: PG·Npgsql 서술을 MySQL 기준으로 바꾼다. NUL은 "정책상 거부(스펙 D15)"로, DbClock은 "DATETIME(6)은 마이크로초까지 저장"으로, LikePattern은 "`EF.Functions.Like(column, pattern, LikePattern.Escape)`"로 쓴다.

`appsettings.Development.json`:
```json
    "Default": "Server=localhost;Port=3306;Database=blog_dev;User ID=root;Password=changeme;SslMode=Required"
```

- [ ] **Step 8: 마이그레이션 재생성**

```powershell
Remove-Item -Recurse -Force PortfolioBlog.Api/Infrastructure/Data/Migrations
dotnet ef migrations add InitialCreate --project PortfolioBlog.Api --output-dir Infrastructure/Data/Migrations
```
생성된 `*_InitialCreate.cs`를 열어 다음을 눈으로 확인하고, 하나라도 없으면 멈춘다:
- `collation: "utf8mb4_bin"`이 6개 열에 있다.
- `CK_Posts_Slug_Format`의 SQL에 `\\z`와 `'c'`가 있다.
- `(CreatedAt, Id)` 인덱스에 `descending: new[] { true, false }`가 있다.
- Guid 열이 `char(36)`이다. `DateTimeOffset` 열이 `datetime(6)`이다.
- `AdminState` 시드 `InsertData`가 있다.

- [ ] **Step 9: 빌드와 실제 기동 확인**

```powershell
dotnet build PortfolioBlog.Api -warnaserror
docker rm -f pb-dev-mysql 2>$null
docker run -d --name pb-dev-mysql -e MYSQL_ROOT_PASSWORD=changeme -p 127.0.0.1:3306:3306 mysql:8.4 --transaction-isolation=READ-COMMITTED --character-set-server=utf8mb4 --local-infile=0
# 준비 대기: 아래가 1을 출력할 때까지 반복
docker exec pb-dev-mysql mysql -h 127.0.0.1 -uroot -pchangeme -N -e "SELECT 1"
$env:ASPNETCORE_ENVIRONMENT = "Development"; dotnet run --project PortfolioBlog.Api --no-build
```
다른 터미널에서 실행한다:
```powershell
curl.exe -sk https://localhost:<launchSettings의 https 포트>/health   # 예상: 200
docker exec pb-dev-mysql mysql -uroot -pchangeme -e "SHOW CREATE TABLE blog_dev.Posts\G"
```
예상: `utf8mb4_bin`, `CHECK (regexp_like(...))`, `KEY ... (CreatedAt DESC, Id)`가 보인다. 서버를 끈다.

**두 번째 go/no-go 관문:** 스파이크는 `EnsureCreated`만 썼으므로 프로바이더의 마이그레이션 경로(이력 테이블·마이그레이션 잠금)는 여기서 처음 실행된다. `Migrate()`가 **매핑이 아니라 프로바이더 때문에** 실패하면(예: 이력 테이블 DDL 오류, 마이그레이션 잠금 미지원) 우회 패치를 하지 말고 멈춘 뒤 보고한다. 그것은 Pomelo+EF9 후퇴 판정 사안이다.

- [ ] **Step 10: 잔존 검사와 커밋**

```powershell
rg -n "Npgsql|PostgresException|pg_advisory|ILike|SqlState|xmin" PortfolioBlog.Api --glob '!**/Migrations/**'
```
예상: 이력 설명 주석 외 0건이다. 남은 주석이 현재 동작을 설명하는 것처럼 읽히면 고친다.

```powershell
git add -A PortfolioBlog.Api Directory.Packages.props
git commit -m "수정: 저장소를 PostgreSQL에서 MySQL로 교체(프로덕션 코드)" -m "- PG 전용 통제를 인터셉터·GET_LOCK·SHOW GRANTS 검증·오류 분류기로 재구현" -m "- 테스트 기반 교체는 다음 커밋(이 커밋에서 테스트 프로젝트는 빌드되지 않는다)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: 전환 ② — 테스트 기반(MySQL 픽스처·팩토리)과 이식 가능한 테스트 복구

**목적:** 테스트 프로젝트를 MySQL 기반으로 컴파일·실행되게 한다. PG 고유 동작을 단언하던 테스트는 **이 태스크에서 삭제**하고 Task 4~8이 MySQL 기준으로 새로 쓴다. 삭제 목록은 아래에 고정한다. 리뷰어는 여기서 빠진 단언이 Task 4~8에 모두 돌아오는지 대조한다.

**Files:**
- Delete: `PortfolioBlog.Api.Tests/Infrastructure/PostgresContainerFixture.cs`
- Create: `PortfolioBlog.Api.Tests/Infrastructure/MySqlContainerFixture.cs`
- Modify: `PortfolioBlog.Api.Tests/PortfolioBlog.Api.Tests.csproj`, `Directory.Packages.props`, `ApiFactory.cs`, 테스트 30개 파일의 컬렉션·픽스처 이름
- Delete(재작성 대상): `Infrastructure/PublicDbContextTests.cs`(→ Task 5), `Infrastructure/PublicRoleGrantsTests.cs`(→ Task 4), `AttachmentIntegrityTests.cs`의 `HoldAsync_KeepsSubsequentEfCommandsOnTheSameSession_AndUnlockReleasesTheKey`·`SessionAdvisoryLock_WithShortLockTimeout_FailsWith55P03_WhileHeldByAnotherSession`·`SetLockTimeout_DoesNotLeakToTheNextUserOfThePooledConnection`·`ClosingAPooledConnection_WithoutAnExplicitUnlock_DelaysReleaseUntilThePhysicalConnectionIsNextUsed`(→ Task 6), `ErrorPipelineTests.cs`의 `PostgresOverload_MapsTo503`·`WrappedPostgresOverload_MapsTo503_ButNotOverWidened`(→ Task 8), `AttachmentEndpointsTests.cs`의 `PublicGet_WhenTheTableIsLocked_Returns503WithRetryAfter`(→ Task 5), `StartupValidationTests.cs`의 `AnyEnvironment_ConnectionStringHasOptions_Fails`·`PublicConnectionString_WithOptions_Fails`(Options 개념이 없어져 폐기, 대체 테스트는 Task 8)
- Modify(기계적 이식): `DatabaseSchemaTests.cs`, `CheckConstraintCoverageTests.cs`, `StartupValidationTests.cs`(연결 문자열 문법)

**Interfaces:**
- Consumes: `DataServiceCollectionExtensions.WithSessionReset` (Task 2)
- Produces:
  - `MySqlContainerFixture`: `string ConnectionString`, `(string User, string Password) CreateUser(string? grantSql = null)`, `void DropUser(string user)`, `void Execute(string sql)`, `const string AppPrivileges`
  - `[CollectionDefinition("mysql")] MySqlCollection`
  - `ApiFactory(MySqlContainerFixture mysql)`, `internal string ConnectionString`, `internal string PublicConnectionString`, `internal string PublicUser`, `internal string DatabaseName`

- [ ] **Step 1: 패키지**

`Directory.Packages.props`: `Testcontainers.PostgreSql` → `<PackageVersion Include="Testcontainers.MySql" Version="4.15.0" />`. 테스트 csproj의 참조 이름도 바꾼다.

- [ ] **Step 2: 픽스처 작성**

`PortfolioBlog.Api.Tests/Infrastructure/MySqlContainerFixture.cs`:
```csharp
using MySql.Data.MySqlClient;
using Testcontainers.MySql;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>테스트 전체가 공유하는 MySQL 8.4 컨테이너와 사용자 관리 도우미.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>픽스처 공유:</b> "mysql" 컬렉션의 모든 클래스가 한 컨테이너를 공유한다. DB는 팩토리마다 새로 만든다(ApiFactory).</description></item>
/// <item><description><b>병렬 실행:</b> 같은 컬렉션이라 클래스 간 직렬. 사용자 이름은 무작위라 충돌하지 않는다.</description></item>
/// <item><description><b>외부 자원:</b> Docker. 서버 인자는 운영(compose)과 같다(Global Constraints). 단 require_secure_transport는 켜지 않는다(부정 테스트가 필요할 때 개별로 확인).</description></item>
/// </list>
/// </remarks>
public sealed class MySqlContainerFixture : IAsyncLifetime
{
    /// <summary>테스트 전용 더미 비밀번호(실제 비밀번호 아님).</summary>
    public const string RootSecret = "dummy-root-0926";

    /// <summary>테스트 전용 더미 비밀번호(실제 비밀번호 아님).</summary>
    public const string UserSecret = "dummy-user-0926";

    /// <summary>관리 사용자 권한. <c>deploy/mysql-init/10-users.sh</c>와 글자 그대로 같아야 한다(<c>DeployInitScriptTests</c>가 대조).</summary>
    public const string AppPrivileges = "SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, DROP, INDEX, REFERENCES";

    // MySqlContainer: Docker API로 컨테이너를 띄우고 컨테이너 안에서 실제 쿼리가 성공할 때까지 기다리는 대기 전략이라 sleep 폴링이 필요 없다.
    // max-connections=500: 팩토리마다 DB가 달라 연결 문자열(=풀)이 팩토리 수만큼 생긴다. 팩토리 Dispose가 풀을 비우지만 병렬 시점 여유를 둔다.
    private readonly MySqlContainer _container = new MySqlBuilder("mysql:8.4")
        .WithUsername("root")
        .WithPassword(RootSecret)
        .WithCommand("--transaction-isolation=READ-COMMITTED", "--character-set-server=utf8mb4", "--collation-server=utf8mb4_0900_ai_ci",
            "--local-infile=0", "--innodb-lock-wait-timeout=10", "--max-connections=500")
        .Build();

    /// <summary>root 연결 문자열(TLS 필수). DB 이름은 비어 있지 않다(Testcontainers 기본 DB). 팩토리가 Database를 바꿔 쓴다.</summary>
    public string ConnectionString => new MySqlConnectionStringBuilder(_container.GetConnectionString()) { SslMode = MySqlSslMode.Required }.ConnectionString;

    /// <inheritdoc />
    public Task InitializeAsync() => _container.StartAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>root로 SQL을 실행한다(동기 — 팩토리 생성자·Dispose에서 쓴다).</summary>
    /// <param name="sql">실행할 SQL. 테스트 코드가 만든 상수·무작위 식별자만 넣는다.</param>
    public void Execute(string sql)
    {
        using var connection = new MySqlConnection(ConnectionString);
        connection.Open();
        using var command = new MySqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    /// <summary>무작위 이름의 TLS 필수 사용자를 만든다. 공개 사용자는 팩토리마다 따로 만든다: 한 사용자가 여러 DB 권한을 가지면 SHOW GRANTS 엄격 검증이 깨진다.</summary>
    /// <param name="grantSql">만든 뒤 실행할 GRANT 문(사용자 자리는 <c>{user}</c>). null이면 권한 없음(USAGE).</param>
    /// <returns>사용자 이름과 비밀번호.</returns>
    public (string User, string Password) CreateUser(string? grantSql = null)
    {
        var user = "u_" + Guid.NewGuid().ToString("N")[..20]; // 22자 ≤ 32
        Execute($"CREATE USER '{user}'@'%' IDENTIFIED BY '{UserSecret}' REQUIRE SSL");
        if (grantSql is not null) Execute(grantSql.Replace("{user}", $"'{user}'@'%'", StringComparison.Ordinal));
        return (user, UserSecret);
    }

    /// <summary>사용자를 지운다(없으면 무시).</summary>
    /// <param name="user">사용자 이름.</param>
    public void DropUser(string user) => Execute($"DROP USER IF EXISTS '{user}'@'%'");
}

/// <summary>MySQL 컨테이너를 공유하는 테스트 컬렉션.</summary>
[CollectionDefinition("mysql")]
public sealed class MySqlCollection : ICollectionFixture<MySqlContainerFixture>;
```

- [ ] **Step 3: 이름 일괄 교체**

```powershell
Remove-Item PortfolioBlog.Api.Tests/Infrastructure/PostgresContainerFixture.cs
Get-ChildItem PortfolioBlog.Api.Tests -Recurse -Filter *.cs | ForEach-Object {
  $t = Get-Content $_.FullName -Raw
  $n = $t -replace '\[Collection\("postgres"\)\]', '[Collection("mysql")]' -replace 'PostgresContainerFixture', 'MySqlContainerFixture' -replace '\bpg\b', 'mysql'
  if ($n -ne $t) { Set-Content $_.FullName $n -NoNewline -Encoding utf8 }
}
```
`\bpg\b`는 `pg_`(밑줄은 단어 문자)와 매치되지 않는다. 교체 뒤 `git diff --stat`으로 30개 안팎 파일만 바뀌었는지 확인한다.

- [ ] **Step 4: `ApiFactory.cs` 교체**

`using Npgsql;` → `using MySql.Data.MySqlClient;`. 필드와 생성자:
```csharp
    private readonly MySqlContainerFixture _mysql;
    private readonly string _connectionString;
    private readonly string _publicConnectionString;
    ...
    internal string ConnectionString => _connectionString;
    internal string PublicConnectionString => _publicConnectionString;
    internal string PublicUser { get; }
    internal string DatabaseName { get; }

    public ApiFactory(MySqlContainerFixture mysql) : this(mysql, new Dictionary<string, string?>()) { }

    internal ApiFactory(MySqlContainerFixture mysql, IReadOnlyDictionary<string, string?> settings, char? attachmentsRootTrailingSeparator = null)
    {
        _mysql = mysql;
        // 클래스마다 새 DB를 써서 테스트 간 데이터 간섭을 없앤다. Migrate()가 DB를 만든다(root라 CREATE DATABASE 가능).
        DatabaseName = "blog_test_" + Guid.NewGuid().ToString("N");
        _connectionString = new MySqlConnectionStringBuilder(mysql.ConnectionString) { Database = DatabaseName }.ConnectionString;
        // 공개 사용자는 팩토리마다 새로 만든다(권한 없음으로 시작 → 앱이 기동하며 GRANT 후 SHOW GRANTS로 검증).
        (PublicUser, var secret) = mysql.CreateUser();
        _publicConnectionString = new MySqlConnectionStringBuilder(_connectionString) { UserID = PublicUser, Password = secret }.ConnectionString;
        _settings = settings;
        _attachmentsRootPathOverride = attachmentsRootTrailingSeparator is { } separator ? AttachmentsRoot + separator : null;
    }
```
`Dispose(bool)`의 풀 정리를 교체한다:
```csharp
        try
        {
            // 풀은 연결 문자열별 프로세스 전역 상태다. 앱이 쓴 것과 같은 키(WithSessionReset 정규화)로 비워야 유휴 연결이 서버에서 즉시 닫힌다.
            foreach (var cs in new[] { _connectionString, _publicConnectionString })
            {
                using var connection = new MySqlConnection(DataServiceCollectionExtensions.WithSessionReset(cs));
                MySqlConnection.ClearPool(connection);
            }
            _mysql.DropUser(PublicUser);
        }
        finally
        {
            if (Directory.Exists(AttachmentsRoot)) Directory.Delete(AttachmentsRoot, recursive: true);
        }
```
(설정에서 `ConnectionStrings:Public`을 덮어쓴 테스트도 `PublicUser`는 만들어졌으므로 삭제해도 안전하다.)

- [ ] **Step 5: 재작성 대상 삭제**

위 Files의 삭제 목록대로 파일·메서드를 지운다. 메서드를 지운 파일은 쓰이지 않게 된 `using`과 private 헬퍼도 지운다.

- [ ] **Step 6: 기계적 이식 3개 파일**

`DatabaseSchemaTests.cs`:
```csharp
    private const int CheckViolation = 3819;
    private const int UniqueViolation = 1062;
    ...
    private async Task<int?> SaveAndGetErrorNumberAsync(Action<AppDbContext> arrange)
    {
        using var client = factory.CreateClient(); // 호스트 기동 → Migrate()
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        arrange(db);
        try { await db.SaveChangesAsync(); return null; }
        catch (DbUpdateException ex) when (ex.InnerException is MySqlException mysql) { return mysql.Number; }
    }
```
`SaveAndGetSqlStateAsync` 호출은 모두 `SaveAndGetErrorNumberAsync`로 바꾼다. `Assert.NotEqual(0u, loaded.Version); // xmin`은 `Assert.Equal(1u, loaded.Version); // 추가 시 PostVersionInterceptor가 1로 둔다`로 바꾼다.

`CheckConstraintCoverageTests.cs`의 `Violation_IsRejected_ByExactlyThatConstraint` 끝부분:
```csharp
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var mysql = Assert.IsType<MySqlException>(ex.InnerException);
        Assert.Equal(3819, mysql.Number);
        // MySQL은 제약 이름을 별도 속성으로 주지 않는다. 메시지 "Check constraint 'X' is violated."에서 확인한다.
        Assert.Contains($"'{constraint}'", mysql.Message, StringComparison.Ordinal);
```

`StartupValidationTests.cs`의 연결 문자열 리터럴: `Host=` → `Server=`, `Username=` → `User ID=`. `new NpgsqlConnectionStringBuilder(mysql.ConnectionString) { CommandTimeout = 1 }` → `new MySqlConnectionStringBuilder(mysql.ConnectionString) { DefaultCommandTimeout = 1 }`. `PublicConnectionString_SameUserAsDefault_Fails`의 기대 문구는 "별도의 읽기 전용 사용자"다. `PublicConnectionString_WithUnsafeRoleName_Fails`의 기대 문구는 "ConnectionStrings:Public 의 User ID"다. `Username=postgres`는 `User ID=root`로 바꾼다. 테스트 팩토리의 Default 사용자가 root이기 때문이다. 리터럴 연결 문자열에는 모두 `;SslMode=Required`를 붙인다. 이 테스트들은 `Test:Environment`를 주지 않으면 Development로 돌아 TLS 검사를 받지 않지만, `Production(...)` 헬퍼와 섞일 때 SslMode 오류가 의도한 단언보다 먼저 나는 것을 막는다.

- [ ] **Step 7: 전체 실행**

```powershell
dotnet build PortfolioBlog.slnx -warnaserror
dotnet test PortfolioBlog.slnx
```
예상: 빌드 경고 0. 실패가 있으면 원인별로 분류해 고친다(`superpowers:systematic-debugging`). 예상되는 실패 원인과 대처:
- 검색 대소문자 테스트: 열 콜레이션을 확인한다(`SHOW FULL COLUMNS`).
- 시간 비교 테스트: `DbClock` 절삭과 `datetime(6)`을 확인한다.
- 태그 교착: 1213이 반복되면 Task 7에서 다룬다. 여기서는 실패 테스트 이름만 기록한다.

**이 태스크의 완료 기준: 남은 테스트 전부 PASS.**

- [ ] **Step 8: 커밋**

```powershell
git add -A PortfolioBlog.Api.Tests Directory.Packages.props
git commit -m "테스트: 테스트 기반을 MySQL 컨테이너로 교체하고 PG 고유 단언을 걷어냄" -m "- 공개 사용자를 팩토리마다 만들어 SHOW GRANTS 엄격 검증과 공존" -m "- 걷어낸 단언은 이후 커밋에서 MySQL 동작 기준으로 다시 증명" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: 공개 사용자 권한 — GRANT 적용과 SHOW GRANTS 검증 증명

**목적:** 스펙 D3·R1·R6을 실제 서버에서 증명한다. PG 판 `PublicRoleGrantsTests`의 의도(공개 사용자로 접속, 허용 테이블 읽기, 쓰기 불가, 비허용 테이블 불가, 비루트 관리자로 적용 가능)를 되살리고, 초과 권한이 있으면 기동하지 않는 것을 추가로 증명한다.

**Files:**
- Create: `PortfolioBlog.Api.Tests/Infrastructure/PublicRoleGrantsTests.cs`

**Interfaces:**
- Consumes: `ApiFactory.PublicConnectionString`·`PublicUser`·`DatabaseName`·`ConnectionString`, `MySqlContainerFixture.CreateUser`·`Execute`·`AppPrivileges`, `PublicRoleGrants.ReadableTables`, `DbErrorClassifier.KindOf`

- [ ] **Step 1: 테스트 작성**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MySql.Data.MySqlClient;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>공개 조회 사용자의 권한 경계(스펙 D3): 앱이 기동하며 5개 테이블 SELECT만 주고, SHOW GRANTS로 그 집합을 검증한다.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>픽스처 공유:</b> "mysql" 컬렉션 컨테이너. 테스트마다 팩토리(DB·공개 사용자)를 새로 만든다.</description></item>
/// <item><description><b>병렬 실행:</b> 컬렉션 내 직렬.</description></item>
/// <item><description><b>외부 자원:</b> Docker MySQL.</description></item>
/// </list>
/// </remarks>
[Collection("mysql")]
public sealed class PublicRoleGrantsTests(MySqlContainerFixture mysql)
{
    private static async Task<int> ErrorNumberAsync(string connectionString, string sql)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        try { await command.ExecuteNonQueryAsync(); return 0; }
        catch (MySqlException ex) { return ex.Number; }
    }

    /// <summary>공개 컨텍스트는 실제로 공개 사용자로 접속한다(관리 사용자로 새지 않는다).</summary>
    [Fact]
    public async Task PublicDbContext_ConnectsAsThePublicUser()
    {
        using var factory = new ApiFactory(mysql);
        using var _ = factory.CreateClient();
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PublicDbContext>();
        var current = await db.Database.SqlQueryRaw<string>("SELECT CURRENT_USER() AS `Value`").SingleAsync();
        Assert.Equal($"{factory.PublicUser}@%", current);
    }

    /// <summary>허용 5개 테이블은 읽히고, 세션 read_only를 스스로 꺼도 쓰기는 권한(1142)으로 막힌다. 두 겹 중 권한 겹의 증명이다.</summary>
    [Fact]
    public async Task PublicUser_ReadsAllowedTables_ButCannotWrite_EvenAfterDisablingReadOnly()
    {
        using var factory = new ApiFactory(mysql);
        using var _ = factory.CreateClient();
        foreach (var table in PublicRoleGrants.ReadableTables)
        {
            Assert.Equal(0, await ErrorNumberAsync(factory.PublicConnectionString, $"SELECT COUNT(*) FROM `{table}`"));
        }
        var number = await ErrorNumberAsync(factory.PublicConnectionString, "SET SESSION transaction_read_only = OFF; DELETE FROM `Tags`");
        Assert.Equal(DbErrorKind.PermissionDenied, DbErrorClassifier.KindOf(number));
        Assert.Equal(DbErrorKind.PermissionDenied, DbErrorClassifier.KindOf(await ErrorNumberAsync(factory.PublicConnectionString, "CREATE TABLE smoke_t (i int)")));
    }

    /// <summary>허용 목록 밖 테이블(세션 에포크, 마이그레이션 이력)은 읽을 수 없다.</summary>
    [Theory]
    [InlineData("AdminState")]
    [InlineData("__EFMigrationsHistory")]
    public async Task PublicUser_CannotReadTablesOutsideTheAllowlist(string table)
    {
        using var factory = new ApiFactory(mysql);
        using var _ = factory.CreateClient();
        Assert.Equal(DbErrorKind.PermissionDenied, DbErrorClassifier.KindOf(await ErrorNumberAsync(factory.PublicConnectionString, $"SELECT * FROM `{table}`")));
    }

    /// <summary>누군가 공개 사용자에게 초과 권한을 주면 다음 기동이 실패한다(자동 회수하지 않고 드러낸다, R6). 메시지에 문제 권한이 담긴다.</summary>
    [Fact]
    public void ExtraGrant_MakesStartupFail()
    {
        using var factory = new ApiFactory(mysql);
        using (factory.CreateClient()) { } // 1차 기동: DB·테이블 생성, 정상 권한
        mysql.Execute($"GRANT INSERT ON `{factory.DatabaseName}`.`Posts` TO '{factory.PublicUser}'@'%'");
        using var second = new ApiFactory(mysql, new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = factory.ConnectionString,
            ["ConnectionStrings:Public"] = factory.PublicConnectionString,
        });
        var ex = Assert.ThrowsAny<Exception>(() => second.CreateClient());
        Assert.Contains("GRANT SELECT, INSERT ON", ex.ToString(), StringComparison.Ordinal);
    }

    /// <summary>운영과 같은 권한(root 아님, blog.* 한정 GRANT OPTION)의 관리 사용자로도 적용이 된다. deploy init 스크립트와 같은 권한 문자열을 쓴다.</summary>
    [Fact]
    public void Apply_Works_WithTheDeployPrivileges_NotRoot()
    {
        using var probe = new ApiFactory(mysql);
        mysql.Execute($"CREATE DATABASE `{probe.DatabaseName}`");
        var (appUser, appSecret) = mysql.CreateUser($"GRANT {MySqlContainerFixture.AppPrivileges} ON `{probe.DatabaseName}`.* TO {{user}} WITH GRANT OPTION");
        try
        {
            using var factory = new ApiFactory(mysql, new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = new MySqlConnectionStringBuilder(probe.ConnectionString) { UserID = appUser, Password = appSecret }.ConnectionString,
                ["ConnectionStrings:Public"] = probe.PublicConnectionString,
            });
            using var client = factory.CreateClient(); // Migrate + GRANT + 검증이 예외 없이 끝나야 한다
        }
        finally
        {
            mysql.DropUser(appUser);
        }
    }
}
```
(`Apply_Works_WithTheDeployPrivileges_NotRoot`에서 `probe.CreateClient()`를 부르지 않는 이유: DB는 root로 미리 만들어 두고, 마이그레이션은 비루트 사용자가 하게 해야 운영 조건과 같다.)

- [ ] **Step 2: 실행 → PASS 확인**

`dotnet test PortfolioBlog.slnx --filter "FullyQualifiedName~PublicRoleGrantsTests"`. 예상: 전부 PASS. 실패하면 Task 2의 `PublicRoleGrants` 구현을 고친다. 운영 권한 목록이 부족하면(예: EF 마이그레이션 잠금에 `LOCK TABLES`가 필요) `AppPrivileges`와 Task 9의 init 스크립트를 **함께** 고친다.

- [ ] **Step 3: 변이 확인(테스트가 통제를 실제로 지키는지)**

`PublicRoleGrants.Violations`의 `WITH GRANT OPTION`·초과 권한 분기를 임시로 `continue`로 바꾼다. `ExtraGrant_MakesStartupFail`이 **FAIL**하는지 확인하고 되돌린다. `BuildGrantStatements`에서 `Posts`를 임시로 빼고 `PublicUser_ReadsAllowedTables…`가 FAIL하는지 확인하고 되돌린다.

- [ ] **Step 4: 커밋**

```powershell
git add PortfolioBlog.Api.Tests/Infrastructure/PublicRoleGrantsTests.cs PortfolioBlog.Api
git commit -m "테스트: 공개 사용자 권한 경계를 MySQL에서 다시 증명" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: 공개 세션 — 읽기 전용·실행 시간 상한·리셋 증명

**목적:** 스펙 D2를 증명한다. PG 판 `PublicDbContextTests`와 `AttachmentEndpointsTests.PublicGet_WhenTheTableIsLocked_Returns503WithRetryAfter`의 의도를 되살린다.

**Files:**
- Create: `PortfolioBlog.Api.Tests/Infrastructure/PublicDbContextTests.cs`
- Modify: `PortfolioBlog.Api.Tests/Features/AttachmentEndpointsTests.cs` (잠긴 테이블 테스트 재추가)

- [ ] **Step 1: 테스트 작성**

`PublicDbContextTests.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MySql.Data.MySqlClient;
using PortfolioBlog.Api.Domain;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>공개 조회 세션 통제(스펙 D2): 읽기 전용, SELECT 실행 상한, 풀 재대여 시 리셋.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>픽스처 공유:</b> "mysql" 컬렉션 컨테이너, 테스트마다 팩토리.</description></item>
/// <item><description><b>병렬 실행:</b> 컬렉션 내 직렬.</description></item>
/// <item><description><b>외부 자원:</b> Docker MySQL.</description></item>
/// </list>
/// </remarks>
[Collection("mysql")]
public sealed class PublicDbContextTests(MySqlContainerFixture mysql)
{
    private static readonly Dictionary<string, string?> FastTimeout = new() { ["Public:StatementTimeoutMs"] = "200" };

    // 스파이크 S3b: SLEEP은 3024 대신 1을 반환할 수 있어, 행을 실제로 훑는 교차 조인으로 실행 시간 초과를 만든다.
    private const string SlowSelect = "SELECT COUNT(*) AS `Value` FROM information_schema.COLUMNS a, information_schema.COLUMNS b, information_schema.COLUMNS c";

    /// <summary>공개 컨텍스트의 느린 SELECT는 max_execution_time으로 끊기고(3024) 분류기가 QueryTimeout으로 본다.</summary>
    [Fact]
    public async Task SlowSelect_IsCancelledByMaxExecutionTime()
    {
        using var factory = new ApiFactory(mysql, FastTimeout);
        using var _ = factory.CreateClient();
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PublicDbContext>();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.SqlQueryRaw<long>(SlowSelect).ToListAsync());
        Assert.Equal(DbErrorKind.QueryTimeout, DbErrorClassifier.Classify(ex));
        Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2));
    }

    /// <summary>관리 컨텍스트에는 실행 상한이 없다(관리 작업이 공개 설정에 끊기지 않는다).</summary>
    [Fact]
    public async Task AdminContext_IsNotAffected()
    {
        using var factory = new ApiFactory(mysql, FastTimeout);
        using var _ = factory.CreateClient();
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0L, await db.Database.SqlQueryRaw<long>("SELECT CAST(@@SESSION.max_execution_time AS SIGNED) AS `Value`").SingleAsync());
        Assert.Equal(0L, await db.Database.SqlQueryRaw<long>("SELECT CAST(@@SESSION.transaction_read_only AS SIGNED) AS `Value`").SingleAsync());
    }

    /// <summary>세션 겹 단독 증명: 관리 사용자 연결(권한은 충분)에 공개 인터셉터만 붙여도 쓰기가 1792로 막힌다. SaveChanges는 앱 겹이 먼저 막는다.</summary>
    [Fact]
    public async Task SessionLayer_Alone_BlocksWrites()
    {
        using var factory = new ApiFactory(mysql);
        using var _ = factory.CreateClient();
        var options = new DbContextOptionsBuilder<PublicDbContext>()
            .UseMySQL(DataServiceCollectionExtensions.WithSessionReset(factory.ConnectionString))
            .AddInterceptors(new PublicSessionInterceptor(3000)).Options;
        await using var db = new PublicDbContext(options);
        var raw = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM `Tags`"));
        Assert.Equal(DbErrorKind.ReadOnly, DbErrorClassifier.Classify(raw));
        var bulk = await Assert.ThrowsAnyAsync<Exception>(() => db.Tags.ExecuteDeleteAsync());
        Assert.Equal(DbErrorKind.ReadOnly, DbErrorClassifier.Classify(bulk));
        db.Tags.Add(new Tag { Name = "x", NormalizedName = "x" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    /// <summary>
    /// 공개 세션이 read_only를 스스로 꺼도 풀 재대여 때 리셋되고 인터셉터가 다시 켠다. 같은 물리 연결(CONNECTION_ID)을 재사용했는지도 확인해야
    /// "리셋"을 측정한 것이 된다. 또 같은 풀을 관리 연결이 빌리면 read_only가 꺼져 있다(Development에서 Default를 공유하는 경우).
    /// </summary>
    [Fact]
    public async Task EscapedReadOnly_IsResetOnReuse_AndNeverLeaksToAdmin()
    {
        using var factory = new ApiFactory(mysql);
        using var _ = factory.CreateClient();
        var single = new MySqlConnectionStringBuilder(DataServiceCollectionExtensions.WithSessionReset(factory.ConnectionString)) { MaximumPoolSize = 1 }.ConnectionString;
        var options = new DbContextOptionsBuilder<PublicDbContext>().UseMySQL(single).AddInterceptors(new PublicSessionInterceptor(3000)).Options;
        try
        {
            long firstId;
            await using (var db = new PublicDbContext(options))
            {
                await db.Database.OpenConnectionAsync();
                firstId = await db.Database.SqlQueryRaw<long>("SELECT CAST(CONNECTION_ID() AS SIGNED) AS `Value`").SingleAsync();
                await db.Database.ExecuteSqlRawAsync("SET SESSION transaction_read_only = OFF");
                Assert.Equal(0L, await db.Database.SqlQueryRaw<long>("SELECT CAST(@@SESSION.transaction_read_only AS SIGNED) AS `Value`").SingleAsync()); // 기준선: 이스케이프가 통한다
            }
            await using (var db = new PublicDbContext(options))
            {
                await db.Database.OpenConnectionAsync();
                Assert.Equal(firstId, await db.Database.SqlQueryRaw<long>("SELECT CAST(CONNECTION_ID() AS SIGNED) AS `Value`").SingleAsync());
                Assert.Equal(1L, await db.Database.SqlQueryRaw<long>("SELECT CAST(@@SESSION.transaction_read_only AS SIGNED) AS `Value`").SingleAsync());
            }
            await using (var admin = new MySqlConnection(single)) // 인터셉터 없는 관리 측 대여
            {
                await admin.OpenAsync();
                await using var command = new MySqlCommand("SELECT @@SESSION.transaction_read_only, @@SESSION.max_execution_time", admin);
                await using var reader = await command.ExecuteReaderAsync();
                await reader.ReadAsync();
                Assert.Equal(0L, Convert.ToInt64(reader.GetValue(0)));
                Assert.Equal(0L, Convert.ToInt64(reader.GetValue(1)));
            }
        }
        finally
        {
            using var clear = new MySqlConnection(single);
            MySqlConnection.ClearPool(clear);
        }
    }

    /// <summary>범위 밖 상한 값은 인터셉터 생성에서 거부된다(정수만 SQL에 들어가지만 설정 오류는 드러낸다).</summary>
    [Theory]
    [InlineData(99)]
    [InlineData(60_001)]
    public void Interceptor_RejectsOutOfRangeTimeouts(int ms) => Assert.Throws<ArgumentOutOfRangeException>(() => new PublicSessionInterceptor(ms));
}
```

`AttachmentEndpointsTests.cs`에 다시 추가한다(`using MySql.Data.MySqlClient;`). MySQL에서는 일반 SELECT가 행 잠금을 기다리지 않으므로 **메타데이터 잠금**(`LOCK TABLES … WRITE`)으로 대기를 만든다.
```csharp
    /// <summary>테이블이 메타데이터 잠금으로 막혀도 공개 GET은 무한 대기하지 않고 503 + Retry-After가 된다(lock_wait_timeout/max_execution_time → 분류기).</summary>
    [Fact]
    public async Task PublicGet_WhenTheTableIsLocked_Returns503WithRetryAfter()
    {
        using var factory = new ApiFactory(mysql, FastPublicTimeout);
        using var admin = await factory.CreateLoggedInClientAsync();
        var dto = await UploadAsync(admin, Fixture("exif-text.png"), "a.png");

        // 관리 연결 문자열로 연 별도 연결이라 앱의 풀과 물리 연결을 공유하지 않는다. LOCK TABLES는 이 세션이 UNLOCK하거나 끝날 때까지 유지된다.
        await using var holder = new MySqlConnection(factory.ConnectionString);
        await holder.OpenAsync();
        await using (var lockCommand = new MySqlCommand("LOCK TABLES `Attachments` WRITE", holder)) await lockCommand.ExecuteNonQueryAsync();
        try
        {
            using var visitor = factory.CreatePublicClient();
            using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(5)); // 상한이 걸리지 않은 구현이 finally까지 붙잡는 교착을 막는다
            using var res = await visitor.GetAsync(dto.Url, bounded.Token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(OverloadExceptionHandler.RetryAfterSeconds), res.Headers.RetryAfter?.Delta);
        }
        finally
        {
            await using var unlock = new MySqlCommand("UNLOCK TABLES", holder);
            await unlock.ExecuteNonQueryAsync();
        }
    }
```
(`FastPublicTimeout`의 값이 1000ms 이하면 lock_wait_timeout은 1초다. 5초 상한 안에 끝난다.)

- [ ] **Step 2: 실행 → PASS**

`dotnet test PortfolioBlog.slnx --filter "FullyQualifiedName~PublicDbContextTests|FullyQualifiedName~PublicGet_WhenTheTableIsLocked"`

- [ ] **Step 3: 변이 확인**

`PublicSessionInterceptor`의 SQL에서 `transaction_read_only = ON,`을 임시로 지우고 `SessionLayer_Alone_BlocksWrites`·`EscapedReadOnly…`가 FAIL하는지 본다. `lock_wait_timeout` 부분을 지우고 잠긴 테이블 테스트가 FAIL(5초 취소)하는지 본다. `WithSessionReset`을 `ConnectionReset = false`로 바꾸고 `EscapedReadOnly…`의 관리 측 단언이 FAIL하는지 본다. 확인 후 모두 되돌린다.

- [ ] **Step 4: 커밋**

```powershell
git add PortfolioBlog.Api.Tests PortfolioBlog.Api
git commit -m "테스트: 공개 세션의 읽기 전용·실행 상한·리셋을 MySQL에서 다시 증명" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: 첨부 잠금 — GET_LOCK 세션 고정·타임아웃·해제 증명

**목적:** 스펙 D5·R3을 증명한다. PG 판 `AttachmentIntegrityTests`의 삭제한 4개 테스트의 의도를 되살린다. 잠근 뒤의 EF 명령이 같은 세션을 쓰는지, 경쟁 시 타임아웃이 되는지, 해제가 되는지, 해제 실패 시 풀 재대여에서 풀리는지를 확인한다.

**Files:**
- Modify: `PortfolioBlog.Api.Tests/Features/AttachmentIntegrityTests.cs` (테스트 4개 추가)
- Modify: 삭제된 `AttachmentLock.KeyFor`를 참조하던 테스트(있으면 `NameFor`로)

- [ ] **Step 1: 테스트 작성**

`AttachmentIntegrityTests.cs` 클래스 안에 추가한다(`using MySql.Data.MySqlClient; using PortfolioBlog.Api.Infrastructure.Storage;`):
```csharp
    private const string LockSha = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    private static async Task<long> ScalarAsync(MySqlConnection connection, string sql)
    {
        await using var command = new MySqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>잠금을 쥔 동안 같은 컨텍스트의 EF 명령은 잠근 세션(CONNECTION_ID)을 쓰고, 해제하면 다른 세션이 즉시 잡을 수 있다.</summary>
    [Fact]
    public async Task HoldAsync_KeepsEfCommandsOnTheLockingSession_AndReleaseFreesTheName()
    {
        using var factory = new ApiFactory(mysql);
        using var _ = factory.CreateClient();
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var name = AttachmentLock.NameFor(factory.DatabaseName, LockSha);
        await using var observer = new MySqlConnection(factory.ConnectionString);
        await observer.OpenAsync();

        await using (await AttachmentLock.HoldAsync(db, LockSha, CancellationToken.None))
        {
            var holder = await ScalarAsync(observer, $"SELECT IS_USED_LOCK('{name}')");
            var efSession = await db.Database.SqlQueryRaw<long>("SELECT CAST(CONNECTION_ID() AS SIGNED) AS `Value`").SingleAsync();
            Assert.Equal(holder, efSession);
        }
        Assert.Equal(1, await ScalarAsync(observer, $"SELECT IS_FREE_LOCK('{name}')"));
    }

    /// <summary>다른 세션이 쥐고 있으면 대기 상한 뒤 DbLockTimeoutException(→ 503)이 나고, 실패 경로에서 연결을 닫는다.</summary>
    [Fact]
    public async Task HoldAsync_WhenHeldElsewhere_TimesOutAsLockTimeout()
    {
        using var factory = new ApiFactory(mysql);
        using var _ = factory.CreateClient();
        var name = AttachmentLock.NameFor(factory.DatabaseName, LockSha);
        await using var other = new MySqlConnection(factory.ConnectionString);
        await other.OpenAsync();
        Assert.Equal(1, await ScalarAsync(other, $"SELECT GET_LOCK('{name}', 0)"));

        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ex = await Assert.ThrowsAsync<DbLockTimeoutException>(() => AttachmentLock.HoldAsync(db, LockSha, waitSeconds: 1, CancellationToken.None));
        Assert.Equal(DbErrorKind.LockTimeout, DbErrorClassifier.Classify(ex));
        Assert.Equal(System.Data.ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    /// <summary>잠금 이름은 DB별이다: 다른 DB(다른 팩토리)의 같은 SHA 잠금과 서로 기다리지 않는다(R3).</summary>
    [Fact]
    public async Task SameSha_InAnotherDatabase_DoesNotContend()
    {
        using var first = new ApiFactory(mysql);
        using var second = new ApiFactory(mysql);
        using var c1 = first.CreateClient();
        using var c2 = second.CreateClient();
        await using var s1 = first.CreateScope();
        await using var s2 = second.CreateScope();
        await using (await AttachmentLock.HoldAsync(s1.ServiceProvider.GetRequiredService<AppDbContext>(), LockSha, CancellationToken.None))
        await using (await AttachmentLock.HoldAsync(s2.ServiceProvider.GetRequiredService<AppDbContext>(), LockSha, waitSeconds: 1, CancellationToken.None))
        {
            // 두 번째 획득이 1초 대기 없이 성공해야 여기에 도달한다
        }
    }

    /// <summary>
    /// 해제하지 않고 풀에 반납된 잠금은 같은 물리 연결이 다시 대여될 때 ConnectionReset으로 풀린다(Releaser의 해제 실패 경로가 기대는 성질, 스파이크 S6b).
    /// 반납 직후에는 아직 쥐어져 있을 수 있으므로(리셋은 대여 시점) 재대여 뒤를 단언한다.
    /// </summary>
    [Fact]
    public async Task UnreleasedLock_IsFreedWhenThePooledConnectionIsReused()
    {
        using var factory = new ApiFactory(mysql);
        using var _ = factory.CreateClient();
        var name = AttachmentLock.NameFor(factory.DatabaseName, LockSha);
        var single = new MySqlConnectionStringBuilder(DataServiceCollectionExtensions.WithSessionReset(factory.ConnectionString)) { MaximumPoolSize = 1 }.ConnectionString;
        try
        {
            long id;
            await using (var c = new MySqlConnection(single))
            {
                await c.OpenAsync();
                id = await ScalarAsync(c, "SELECT CONNECTION_ID()");
                Assert.Equal(1, await ScalarAsync(c, $"SELECT GET_LOCK('{name}', 0)"));
            }
            await using (var c = new MySqlConnection(single))
            {
                await c.OpenAsync();
                Assert.Equal(id, await ScalarAsync(c, "SELECT CONNECTION_ID()"));
                Assert.Equal(1, await ScalarAsync(c, $"SELECT IS_FREE_LOCK('{name}')"));
            }
        }
        finally
        {
            using var clear = new MySqlConnection(single);
            MySqlConnection.ClearPool(clear);
        }
    }
```

- [ ] **Step 2: 실행 → PASS**

`dotnet test PortfolioBlog.slnx --filter "FullyQualifiedName~AttachmentIntegrityTests|FullyQualifiedName~AttachmentJanitorTests|FullyQualifiedName~AttachmentEndpointsTests"`. 기존 API 수준 무결성 테스트 3개도 함께 PASS여야 한다.

- [ ] **Step 3: 변이 확인**

`NameFor`에서 DB 태그를 임시로 빼고 `SameSha_InAnotherDatabase_DoesNotContend`가 FAIL(1초 타임아웃)하는지 확인한다. `HoldAsync`의 `OpenConnectionAsync`를 지우고 첫 테스트가 FAIL하는지 확인한다. 둘 다 되돌린다.

- [ ] **Step 4: 커밋**

```powershell
git add PortfolioBlog.Api.Tests PortfolioBlog.Api
git commit -m "테스트: 첨부 잠금(GET_LOCK)의 세션 고정·타임아웃·DB별 격리·재대여 해제를 증명" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: 행 버전·격리 수준·태그 동시성 증명

**목적:** 스펙 D4(xmin 대체), D7(READ COMMITTED), D8(태그 upsert)을 증명한다.

**Files:**
- Create: `PortfolioBlog.Api.Tests/Infrastructure/PostVersionTests.cs`, `IsolationLevelTests.cs`
- Modify: `PortfolioBlog.Api.Tests/Infrastructure/TagResolverTests.cs` (동시 생성 테스트가 없으면 추가)

- [ ] **Step 1: 테스트 작성**

`PostVersionTests.cs`:
```csharp
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortfolioBlog.Api.Domain;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>앱 관리 행 버전(스펙 D4): 추가 1, 수정 +1, 낡은 버전은 동시성 예외, 벌크 갱신도 버전을 올린다.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>픽스처 공유:</b> 클래스 픽스처 ApiFactory 하나(DB 1개). 테스트마다 다른 slug를 쓴다.</description></item>
/// <item><description><b>병렬 실행:</b> 컬렉션 내 직렬.</description></item>
/// <item><description><b>외부 자원:</b> Docker MySQL, 소스 스캔은 저장소 파일 읽기.</description></item>
/// </list>
/// </remarks>
[Collection("mysql")]
public sealed class PostVersionTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<Guid> SeedAsync(string slug, Series? series = null)
    {
        using var _ = factory.CreateClient();
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var post = new Post { Slug = slug, Title = "t", Summary = "", ContentMarkdown = "c", CreatedAt = DbClock.UtcNow(), UpdatedAt = DbClock.UtcNow() };
        if (series is not null) { post.Series = series; post.SeriesOrder = 1; }
        db.Posts.Add(post);
        await db.SaveChangesAsync();
        return post.Id;
    }

    private async Task<uint> VersionOfAsync(Guid id)
    {
        await using var scope = factory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Posts.AsNoTracking().Where(p => p.Id == id).Select(p => p.Version).SingleAsync();
    }

    /// <summary>추가는 1, 속성 수정 저장은 정확히 +1이다.</summary>
    [Fact]
    public async Task Add_IsOne_Modify_IncrementsByOne()
    {
        var id = await SeedAsync("ver-basic");
        Assert.Equal(1u, await VersionOfAsync(id));
        await using (var scope = factory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var post = await db.Posts.SingleAsync(p => p.Id == id);
            post.Title = "t2";
            await db.SaveChangesAsync();
        }
        Assert.Equal(2u, await VersionOfAsync(id));
    }

    /// <summary>클라이언트가 가진 낡은 버전으로 저장하면 동시성 예외다(PostEndpoints의 409 경로).</summary>
    [Fact]
    public async Task StaleOriginalVersion_ThrowsConcurrencyException()
    {
        var id = await SeedAsync("ver-stale");
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var post = await db.Posts.SingleAsync(p => p.Id == id);
        db.Entry(post).Property(p => p.Version).OriginalValue = 99;
        post.Title = "t3";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());
    }

    /// <summary>시리즈 삭제(글의 시리즈를 벌크로 비움)도 글 버전을 올린다. 올리지 않으면 렌더 캐시가 시리즈 내비게이션이 남은 HTML을 낸다.</summary>
    [Fact]
    public async Task DeletingTheSeries_BumpsThePostVersion()
    {
        var series = new Series { Slug = "ver-series", Title = "s", Description = "" };
        var id = await SeedAsync("ver-in-series", series);
        using var admin = await factory.CreateLoggedInClientAsync();
        using var res = await admin.DeleteAsync($"/api/series/{series.Id}");
        Assert.Equal(System.Net.HttpStatusCode.NoContent, res.StatusCode);
        Assert.Equal(2u, await VersionOfAsync(id));
    }

    /// <summary>
    /// 아키텍처 규칙: Posts에 대한 모든 <c>ExecuteUpdateAsync</c>와 원시 <c>UPDATE `Posts`</c>는 Version을 함께 올려야 한다(인터셉터를 거치지 않는 경로).
    /// 새 벌크 갱신을 추가하면서 버전을 빠뜨리면 이 테스트가 파일 이름과 함께 실패한다.
    /// </summary>
    [Fact]
    public void EveryPostsBulkUpdate_BumpsVersion()
    {
        var api = Path.Combine(RepoRoot(), "PortfolioBlog.Api");
        var sep = Path.DirectorySeparatorChar;
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(api, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{sep}Migrations{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            var statements = Regex.Matches(text, @"\.Posts\b[^;]*?ExecuteUpdateAsync\([^;]*;", RegexOptions.Singleline).Select(m => m.Value)
                .Concat(Regex.Matches(text, @"UPDATE\s+`?Posts`?[^;""]*", RegexOptions.IgnoreCase).Select(m => m.Value));
            offenders.AddRange(statements.Where(s => !s.Contains("Version", StringComparison.Ordinal)).Select(s => $"{Path.GetFileName(file)}: {s[..Math.Min(100, s.Length)]}"));
        }
        Assert.Empty(offenders);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PortfolioBlog.slnx"))) return dir.FullName;
        }
        throw new InvalidOperationException("PortfolioBlog.slnx를 찾지 못했습니다.");
    }
}
```

`IsolationLevelTests.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MySql.Data.MySqlClient;
using PortfolioBlog.Api.Domain;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>격리 수준(스펙 D7): 앱의 트랜잭션은 READ COMMITTED로 동작한다. 트랜잭션 안에서 다른 세션의 커밋이 보여야 한다.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>픽스처 공유:</b> 클래스 픽스처 ApiFactory 하나.</description></item>
/// <item><description><b>병렬 실행:</b> 컬렉션 내 직렬.</description></item>
/// <item><description><b>외부 자원:</b> Docker MySQL. 서버 기본값을 REPEATABLE READ로 되돌린 세션에서도 앱 인터셉터가 READ COMMITTED를 보장하는지 본다.</description></item>
/// </list>
/// </remarks>
[Collection("mysql")]
public sealed class IsolationLevelTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>
    /// 서버 설정에 기대지 않는다: 세션 기본값을 REPEATABLE READ로 바꾼 뒤 BeginTransactionAsync()(격리 미지정)를 연다.
    /// 첫 읽기 뒤 다른 세션이 값을 바꿔 커밋하면, READ COMMITTED라면 두 번째 읽기에 새 값이 보인다(REPEATABLE READ면 옛 값).
    /// </summary>
    [Fact]
    public async Task UnspecifiedTransaction_IsReadCommitted_EvenIfSessionDefaultIsRepeatableRead()
    {
        using var _ = factory.CreateClient();
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("SET SESSION transaction_isolation = 'REPEATABLE-READ'");
        await using var tx = await db.Database.BeginTransactionAsync();
        var before = await db.AdminStates.AsNoTracking().Select(a => a.SessionEpoch).SingleAsync();

        await using (var other = new MySqlConnection(factory.ConnectionString))
        {
            await other.OpenAsync();
            await using var bump = new MySqlCommand("UPDATE `AdminState` SET `SessionEpoch` = `SessionEpoch` + 1", other);
            await bump.ExecuteNonQueryAsync();
        }

        var after = await db.AdminStates.AsNoTracking().Select(a => a.SessionEpoch).SingleAsync();
        Assert.Equal(before + 1, after);
        await tx.RollbackAsync();
    }
}
```

`TagResolverTests.cs`에 동시 생성 테스트가 없으면 추가한다(있으면 MySQL에서 통과하는지만 본다):
```csharp
    /// <summary>같은 새 태그 집합을 반대 순서로 동시에 저장해도 교착 없이 모두 성공하고 태그는 하나씩만 생긴다(서수 순서 삽입 + ON DUPLICATE KEY).</summary>
    [Fact]
    public async Task ConcurrentSaves_OfTheSameNewTags_AllSucceed_WithoutDuplicates()
    {
        using var _ = factory.CreateClient();
        var names = Enumerable.Range(0, 8).Select(i => $"동시-{i}").ToArray();
        async Task SaveAsync(IEnumerable<string> order)
        {
            await using var scope = factory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync();
            await TagResolver.ResolveIdsAsync(db, order, CancellationToken.None);
            await tx.CommitAsync();
        }
        await Task.WhenAll(Enumerable.Range(0, 16).Select(i => SaveAsync(i % 2 == 0 ? names : names.Reverse())));
        await using var check = factory.CreateScope();
        var count = await check.ServiceProvider.GetRequiredService<AppDbContext>().Tags.CountAsync(t => t.NormalizedName.StartsWith("동시-"));
        Assert.Equal(names.Length, count);
    }
```

- [ ] **Step 2: 실행**

`dotnet test PortfolioBlog.slnx --filter "FullyQualifiedName~PostVersionTests|FullyQualifiedName~IsolationLevelTests|FullyQualifiedName~TagResolverTests"`

**태그 동시성 테스트가 1213으로 실패하면**(스펙 D8의 판정 지점): `TagResolver.ResolveIdsAsync`에서 삽입 루프 전체를 1213에 한해 최대 3회 재시도한다. 재시도 단위는 호출자 트랜잭션이다. 1213은 InnoDB가 트랜잭션 전체를 롤백한 상태라 호출부 수준에서 다시 해야 한다. 그러므로 재시도는 `PostEndpoints`의 저장 트랜잭션을 감싸는 헬퍼로 구현한다. 구현이 커지면 이 태스크를 멈추고 설계를 보고한다. 대안은 교착을 503으로 두는 것이다(이미 Task 2에서 매핑됨). 이 경우 테스트는 "모두 성공 또는 503 분류, 중복 없음"으로 완화하고 그 판정을 스펙 7절에 기록한다.

- [ ] **Step 3: 변이 확인**

`ReadCommittedTransactionInterceptor` 등록을 임시로 빼고 `IsolationLevelTests`가 FAIL하는지 본다. `SeriesEndpoints`의 `.SetProperty(p => p.Version, …)`를 빼고 `DeletingTheSeries_BumpsThePostVersion`과 `EveryPostsBulkUpdate_BumpsVersion`이 둘 다 FAIL하는지 본다. 되돌린다.

- [ ] **Step 4: 커밋**

```powershell
git add PortfolioBlog.Api.Tests PortfolioBlog.Api
git commit -m "테스트: 앱 관리 행 버전·READ COMMITTED 보장·태그 동시 생성을 증명" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 8: 오류 매핑·콜레이션·스키마·설정 검증 증명

**목적:** 스펙 D10·D11·D16·D17을 증명하고, Task 3에서 걷어낸 ErrorPipeline·StartupValidation 단언을 되살린다.

**Files:**
- Create: `PortfolioBlog.Api.Tests/Infrastructure/MySqlErrors.cs`, `CollationTests.cs`, `DeployInitScriptTests.cs`
- Modify: `Features/ErrorPipelineTests.cs`, `Features/StartupValidationTests.cs`, `Infrastructure/CheckConstraintCoverageTests.cs`

- [ ] **Step 1: 테스트 작성**

`MySqlErrors.cs`(테스트 전용, 스파이크 S8 PASS 전제):
```csharp
using System.Reflection;
using MySql.Data.MySqlClient;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>오류 파이프라인 테스트가 던질 <see cref="MySqlException"/>을 번호로 만든다. 공개 생성자가 없어 비공개 (string, int) 생성자를 쓴다(스파이크 S8).</summary>
internal static class MySqlErrors
{
    private static readonly ConstructorInfo Ctor = typeof(MySqlException).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, [typeof(string), typeof(int)])
        ?? throw new InvalidOperationException("MySqlException(string, int) 생성자가 없다 — 스파이크 S8 대안을 쓴다.");

    /// <summary>지정 번호의 예외를 만든다.</summary>
    /// <param name="number">MySQL 오류 번호.</param>
    /// <returns>던질 예외.</returns>
    public static MySqlException Create(int number) => (MySqlException)Ctor.Invoke([$"simulated {number}", number]);
}
```
S8이 FAIL이었다면 대안: `Create`를 컨테이너에서 실제 오류를 한 번 일으켜 캡처한 예외로 구현한다(1062: 중복 PK 삽입, 3024: `SET SESSION max_execution_time=1` 후 교차 조인, 1205: `innodb_lock_wait_timeout=1` 세션에서 잠긴 행 UPDATE, 1213: 두 연결이 두 행을 교차로 UPDATE). `ErrorPipelineTests`를 `[Collection("mysql")]`로 두고 픽스처에서 캡처한다.

`ErrorPipelineTests.cs`에 되살린다:
```csharp
    /// <summary>실행 시간 초과·잠금 대기·교착은 503 + Retry-After, 중복 키는 500으로 남는다(과대 분류 금지).</summary>
    [Theory]
    [InlineData(3024, 503)]
    [InlineData(1205, 503)]
    [InlineData(1213, 503)]
    [InlineData(1062, 500)]
    public async Task MySqlOverload_MapsTo503(int number, int expected)
    {
        await using var app = await StartAsync(_ => throw MySqlErrors.Create(number));
        using var res = await app.GetTestClient().GetAsync("/posts/x");
        Assert.Equal(expected, (int)res.StatusCode);
        Assert.Equal(expected == 503, res.Headers.Contains("Retry-After"));
    }

    /// <summary>SaveChanges 경로처럼 감싸여 와도 같게 매핑된다.</summary>
    [Theory]
    [InlineData(1205, 503)]
    [InlineData(1062, 500)]
    public async Task WrappedMySqlOverload_MapsTo503_ButNotOverWidened(int number, int expected)
    {
        await using var app = await StartAsync(_ => throw new DbUpdateException("save failed", MySqlErrors.Create(number)));
        using var res = await app.GetTestClient().GetAsync("/posts/x");
        Assert.Equal(expected, (int)res.StatusCode);
        Assert.Equal(expected == 503, res.Headers.Contains("Retry-After"));
    }

    /// <summary>앱이 직접 던지는 GET_LOCK 타임아웃도 503이다.</summary>
    [Fact]
    public async Task AppLockTimeout_MapsTo503()
    {
        await using var app = await StartAsync(_ => throw new DbLockTimeoutException("wait"));
        using var res = await app.GetTestClient().GetAsync("/posts/x");
        Assert.Equal(503, (int)res.StatusCode);
    }
```

`CollationTests.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MySql.Data.MySqlClient;
using PortfolioBlog.Api.Domain;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>콜레이션(스펙 D10): 식별자 열은 바이트 비교(유니크 의미 보존), 검색 열은 대소문자 무시.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>픽스처 공유:</b> 클래스 픽스처 ApiFactory 하나.</description></item>
/// <item><description><b>병렬 실행:</b> 컬렉션 내 직렬.</description></item>
/// <item><description><b>외부 자원:</b> Docker MySQL.</description></item>
/// </list>
/// </remarks>
[Collection("mysql")]
public sealed class CollationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>ai_ci였다면 충돌했을 두 정규화 태그 이름(악센트만 다름)이 둘 다 저장된다.</summary>
    [Fact]
    public async Task AccentVariants_AreDistinctUniqueValues()
    {
        using var _ = factory.CreateClient();
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Tags.Add(new Tag { Name = "cafe", NormalizedName = "cafe" });
        db.Tags.Add(new Tag { Name = "café", NormalizedName = "café" });
        await db.SaveChangesAsync();
    }

    /// <summary>모델이 선언한 식별자 열 6개가 실제 스키마에서 utf8mb4_bin이다(마이그레이션이 콜레이션을 빠뜨리지 않았다).</summary>
    [Theory]
    [InlineData("Posts", "Slug")]
    [InlineData("Series", "Slug")]
    [InlineData("Tags", "NormalizedName")]
    [InlineData("Attachments", "Sha256")]
    [InlineData("Attachments", "ContentType")]
    [InlineData("Attachments", "StoragePath")]
    public async Task IdentifierColumns_AreBinaryCollated(string table, string column)
    {
        using var _ = factory.CreateClient();
        await using var connection = new MySqlConnection(factory.ConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(
            "SELECT COLLATION_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t AND COLUMN_NAME = @c", connection);
        command.Parameters.AddWithValue("@t", table);
        command.Parameters.AddWithValue("@c", column);
        Assert.Equal(AppDbContext.BinaryCollation, (string?)await command.ExecuteScalarAsync());
    }

    /// <summary>공개 검색은 대소문자를 무시한다(PG ILIKE와 같은 사용자 경험).</summary>
    [Fact]
    public async Task Search_IsCaseInsensitive()
    {
        using var _ = factory.CreateClient();
        await using (var scope = factory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Posts.Add(new Post { Slug = "coll-search", Title = "MySQL Migration", Summary = "", ContentMarkdown = "x", CreatedAt = DbClock.UtcNow(), UpdatedAt = DbClock.UtcNow() });
            await db.SaveChangesAsync();
        }
        await using var read = factory.CreateScope();
        var pub = read.ServiceProvider.GetRequiredService<AppDbContext>();
        var pattern = LikePattern.Contains("mysql migration");
        Assert.Equal(1, await pub.Posts.CountAsync(p => EF.Functions.Like(p.Title, pattern, LikePattern.Escape)));
    }
}
```

`CheckConstraintCoverageTests.cs`에 추가한다(끝 개행 앵커 회귀):
```csharp
    /// <summary>DB 정규식이 끝의 개행을 허용하지 않는다(ICU의 $ 함정, \z 사용). slug·sha256 둘 다.</summary>
    [Theory]
    [InlineData("CK_Posts_Slug_Format")]
    [InlineData("CK_Attachments_Sha256")]
    public async Task TrailingNewline_IsRejected(string constraint)
    {
        using var _ = factory.CreateClient();
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (constraint == "CK_Posts_Slug_Format") db.Posts.Add(NewPost("abc\n"));
        else { var a = NewAttachment('5'); a.Sha256 = new string('a', 63) + "\n"; db.Attachments.Add(a); }
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains($"'{constraint}'", Assert.IsType<MySqlException>(ex.InnerException).Message, StringComparison.Ordinal);
    }
```
(`sha256` 63자 + `\n` = 64자라 길이 제한이 아니라 정규식으로 거부되는지 본다.)

`StartupValidationTests.cs`에 추가한다(Options 테스트 대체):
```csharp
    /// <summary>AllowPublicKeyRetrieval=true는 어느 환경에서도 거부된다(공개키 바꿔치기로 비밀번호 노출, 스펙 D17).</summary>
    [Fact]
    public void AnyEnvironment_AllowPublicKeyRetrieval_Fails()
    {
        var unsafeCs = new MySqlConnectionStringBuilder(mysql.ConnectionString) { AllowPublicKeyRetrieval = true }.ConnectionString;
        AssertStartupFails(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = unsafeCs }, "AllowPublicKeyRetrieval");
    }

    /// <summary>Development가 아니면 TLS 없는 연결(SslMode=Preferred/None)을 거부한다.</summary>
    [Theory]
    [InlineData(MySqlSslMode.Preferred)]
    [InlineData(MySqlSslMode.Disabled)]
    public void Production_WeakSslMode_Fails(MySqlSslMode mode) =>
        AssertStartupFails(Production(s => s["ConnectionStrings:Default"] = new MySqlConnectionStringBuilder(mysql.ConnectionString) { SslMode = mode }.ConnectionString), "SslMode");
```
(`AssertStartupFails`와 `Production`은 파일의 기존 헬퍼다. `MySqlSslMode.Disabled`가 없는 버전이면 `None`을 쓴다.)

`DeployInitScriptTests.cs`:
```csharp
namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>배포 init 스크립트의 관리 사용자 권한이 테스트가 검증한 권한(<see cref="MySqlContainerFixture.AppPrivileges"/>)과 같다(드리프트 방지).</summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>픽스처:</b> 없음(파일 읽기).</description></item>
/// <item><description><b>병렬 실행:</b> 안전.</description></item>
/// <item><description><b>외부 자원:</b> 저장소의 deploy/mysql-init/10-users.sh.</description></item>
/// </list>
/// </remarks>
public sealed class DeployInitScriptTests
{
    /// <summary>스크립트가 같은 권한 문자열과 GRANT OPTION, TLS 강제를 담고 있고 FILE·SUPER 같은 전역 권한은 없다.</summary>
    [Fact]
    public void InitScript_GrantsExactlyTheTestedPrivileges()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PortfolioBlog.slnx"))) root = root.Parent;
        var script = File.ReadAllText(Path.Combine(root!.FullName, "deploy", "mysql-init", "10-users.sh"));
        Assert.Contains($"GRANT {MySqlContainerFixture.AppPrivileges} ON `blog`.* TO 'blog_app'@'%' WITH GRANT OPTION", script, StringComparison.Ordinal);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(script, "REQUIRE SSL").Count);
        Assert.DoesNotContain("ON *.*", script, StringComparison.Ordinal);
    }
}
```
(이 테스트는 Task 9에서 스크립트를 만들기 전까지 실패한다. **그래서 이 파일은 Task 9 Step 1에서 커밋한다**. Task 8에서는 작성만 하고 커밋에서 뺀다.)

- [ ] **Step 2: 실행 → PASS(DeployInitScriptTests 제외)**

`dotnet test PortfolioBlog.slnx --filter "FullyQualifiedName~ErrorPipelineTests|FullyQualifiedName~CollationTests|FullyQualifiedName~CheckConstraintCoverageTests|FullyQualifiedName~StartupValidationTests"`

- [ ] **Step 3: 전체 회귀**

`dotnet test PortfolioBlog.slnx`. `DeployInitScriptTests`를 뺀 전부가 PASS여야 한다. Task 3에서 걷어낸 단언 목록과 Task 4~8의 새 테스트를 대조해 빠진 의도가 없는지 확인한다.

- [ ] **Step 4: 커밋**

```powershell
git add PortfolioBlog.Api.Tests ':!PortfolioBlog.Api.Tests/Infrastructure/DeployInitScriptTests.cs' PortfolioBlog.Api
git commit -m "테스트: 오류→HTTP 매핑·콜레이션·정규식 앵커·연결 보안 설정을 MySQL에서 증명" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 9: 배포 — compose·init·백업/복원·스모크

**목적:** 운영 스택을 MySQL로 바꾸고, 기존 스모크가 확인하던 권한 경계·망 격리·복원 리허설을 MySQL로 다시 통과시킨다.

**Files:**
- Delete: `deploy/postgres-init/`
- Create: `deploy/mysql-init/10-users.sh`
- Modify: `deploy/docker-compose.yml`, `deploy/.env.example`, `deploy/backup.sh`, `deploy/restore.sh`, `deploy/smoke/run.sh`, `deploy/smoke/smoke.test.mjs`
- Commit: `PortfolioBlog.Api.Tests/Infrastructure/DeployInitScriptTests.cs` (Task 8에서 작성)

- [ ] **Step 1: init 스크립트**

`deploy/mysql-init/10-users.sh`. 실행 비트 없이 두면 공식 이미지 엔트리포인트가 이 파일을 **source**하므로 `docker_process_sql`을 쓸 수 있다. source되므로 `set -eu`가 엔트리포인트 자신의 셸에 남지 않도록 본문 전체를 서브셸 `( … )`로 감싼다. 셸 함수는 서브셸로 이어지므로 `docker_process_sql`은 그대로 쓸 수 있다.
```sh
#!/bin/sh
# 빈 데이터 볼륨에서 처음 뜰 때 한 번만 실행된다(공식 이미지의 /docker-entrypoint-initdb.d 규약, source로 실행되어 docker_process_sql 사용 가능).
# 본문을 서브셸로 감싼다: source된 파일의 set -eu가 엔트리포인트 셸에 남으면 이후 엔트리포인트 코드의 미설정 변수 참조가 깨진다.
# 앱은 root로 접속하지 않는다:
#   blog_app    — blog DB 한정 권한 + GRANT OPTION. 마이그레이션과 관리 API가 쓴다. 전역 권한(FILE·SUPER·PROCESS·CREATE USER)이 없다.
#                 GRANT OPTION은 앱이 기동할 때 blog_public에 허용 테이블 SELECT를 주기 위한 것이다(스펙 R1: 순증 위험 없음 분석).
#   blog_public — 여기서는 접속만(USAGE). 테이블별 SELECT는 앱(PublicRoleGrants)이 마이그레이션 직후 주고 SHOW GRANTS로 검증한다.
# 권한 문자열은 PortfolioBlog.Api.Tests의 MySqlContainerFixture.AppPrivileges와 같아야 한다(DeployInitScriptTests가 대조).
# 비밀번호는 영문·숫자만 허용한다(아래 검사). 그래서 sed 치환과 SQL 문자열 리터럴이 깨지지 않는다. heredoc은 따옴표('SQL')라 셸이 본문을 건드리지 않는다.
# 순서 보장: init 동안 서버는 --skip-networking 임시 서버로 뜨므로 TCP 헬스체크(compose)가 실패한다 — api는 init이 끝난 뒤에만 뜬다.
(
  set -eu
  case "$BLOG_APP_PASSWORD$BLOG_PUBLIC_PASSWORD" in *[!A-Za-z0-9]*) echo "BLOG_APP_PASSWORD·BLOG_PUBLIC_PASSWORD는 영문·숫자만 쓴다" >&2; exit 1;; esac
  sql=$(cat <<'SQL'
CREATE DATABASE blog CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
CREATE USER 'blog_app'@'%' IDENTIFIED BY '@APP_PW@' REQUIRE SSL;
CREATE USER 'blog_public'@'%' IDENTIFIED BY '@PUBLIC_PW@' REQUIRE SSL;
GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, DROP, INDEX, REFERENCES ON `blog`.* TO 'blog_app'@'%' WITH GRANT OPTION;
SQL
  )
  printf '%s\n' "$sql" | sed -e "s/@APP_PW@/$BLOG_APP_PASSWORD/" -e "s/@PUBLIC_PW@/$BLOG_PUBLIC_PASSWORD/" | docker_process_sql --database=mysql
) || exit 1
```
(영문·숫자 검사로 `sed` 치환의 특수문자 문제를 원천 차단한다. heredoc에 이스케이프가 없어 파일 안 텍스트가 `DeployInitScriptTests`의 기대 문자열과 글자 그대로 같다. 서브셸 실패는 `|| exit 1`로 엔트리포인트에 전달한다. 스크립트가 source되는 문맥이라 `exit`는 init 전체를 실패시키고, 이것이 의도한 동작이다.)

- [ ] **Step 2: compose**

`deploy/docker-compose.yml`의 api 환경:
```yaml
      ConnectionStrings__Default: "Server=mysql;Database=blog;User ID=blog_app;Password=${BLOG_APP_PASSWORD:?};SslMode=Required;Default Command Timeout=30"
      ConnectionStrings__Public: "Server=mysql;Database=blog;User ID=blog_public;Password=${BLOG_PUBLIC_PASSWORD:?};SslMode=Required;Default Command Timeout=30"
```
`depends_on: postgres:`는 `mysql:`로 바꾼다. `postgres` 서비스는 다음으로 교체한다(`<x>`는 Task 0 Step 4의 패치 버전):
```yaml
  mysql:
    <<: *hardening
    image: mysql:8.4.<x>
    # 공식 엔트리포인트가 데이터 디렉터리 소유권을 맞추고 mysql 사용자로 내려간다(gosu). PG 판과 같은 최소 능력만 준다.
    cap_add:
      - CHOWN
      - DAC_OVERRIDE
      - FOWNER
      - SETGID
      - SETUID
    # 설정 파일을 마운트하지 않는다: MySQL은 world-writable 설정 파일을 조용히 무시한다(스펙 D19). 서버 설정은 전부 여기 인자로.
    command:
      - --transaction-isolation=READ-COMMITTED
      - --character-set-server=utf8mb4
      - --collation-server=utf8mb4_0900_ai_ci
      - --local-infile=0
      - --secure-file-priv=NULL
      - --require-secure-transport=ON
      - --innodb-lock-wait-timeout=10
    environment:
      MYSQL_ROOT_PASSWORD: ${MYSQL_ROOT_PASSWORD:?}
      BLOG_APP_PASSWORD: ${BLOG_APP_PASSWORD:?}
      BLOG_PUBLIC_PASSWORD: ${BLOG_PUBLIC_PASSWORD:?}
    volumes:
      - mysqldata:/var/lib/mysql
      - ./mysql-init:/docker-entrypoint-initdb.d:ro
    tmpfs:
      - /var/run/mysqld:mode=1777,size=1m
      - /tmp:mode=1777,size=64m
    networks:
      - db
    healthcheck:
      # mysqladmin ping은 인증 실패에도 0을 반환하므로 앱 사용자로 실제 쿼리를 한다. -h 127.0.0.1(TCP)이라 init 중 임시 서버(--skip-networking)에서는 실패한다.
      # 비밀번호는 명령줄이 아니라 MYSQL_PWD로 넘긴다(컨테이너 안 ps에 보이지 않게). $$는 compose 보간을 피해 컨테이너 셸이 펼치게 한다.
      test: ["CMD-SHELL", "MYSQL_PWD=\"$$BLOG_APP_PASSWORD\" mysql -h 127.0.0.1 -u blog_app --ssl-mode=REQUIRED -N -e 'SELECT 1' blog"]
      interval: 10s
      timeout: 5s
      retries: 12
      start_period: 60s
```
`tools` 서비스 이미지는 `mysql:8.4.<x>`로 바꾼다. 볼륨 `pgdata:`는 `mysqldata:`로 바꾼다. `deploy/docker-compose.smoke.yml`에서 서비스 이름 `postgres`를 참조하는 곳이 있으면 함께 바꾼다(`rg -n postgres deploy`).

- [ ] **Step 3: `.env.example`**

```ini
# DB 비밀번호 셋. 서로 다른 무작위 값으로: openssl rand -base64 24 | tr -dc 'A-Za-z0-9'
# 연결 문자열과 init SQL에 그대로 들어가므로 영문·숫자만 쓴다(init 스크립트가 검사한다).
# 주의: mysql은 "빈 데이터 볼륨에서 처음 뜰 때"만 이 값으로 사용자를 만든다. 나중에 바꾸려면 OPERATIONS.md의 절차를 따른다.
MYSQL_ROOT_PASSWORD=changeme-root
BLOG_APP_PASSWORD=changeme-app
BLOG_PUBLIC_PASSWORD=changeme-public
```
(`changeme-root`의 `-`는 root 비밀번호라 init SQL에 들어가지 않는다. app·public 예시 값은 검사를 통과하도록 `changemeapp`·`changemepublic`으로 바꾼다.)

- [ ] **Step 4: 백업·복원**

`backup.sh`의 덤프·검증 줄:
```bash
# --single-transaction: InnoDB 일관 스냅숏(서비스 무중단). --no-tablespaces: PROCESS 권한 없이도 덤프. 비밀번호는 컨테이너 안 환경변수에서 읽는다.
docker compose exec -T mysql sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" exec mysqldump -uroot --single-transaction --no-tablespaces --set-gtid-purged=OFF --databases blog' > "$dest/blog.sql"
...
# 읽을 수 있는 백업인지 확인: mysqldump는 정상 종료 시 마지막 줄에 완료 표식을 쓴다(잘린 덤프에는 없다).
tail -n 1 "$dest/blog.sql" | grep -q '^-- Dump completed'
(cd "$dest" && sha256sum blog.sql attachments.tar > SHA256SUMS)
```
`restore.sh`:
```bash
docker compose up -d --wait mysql
# DB를 지우고 덤프로 다시 만든다(덤프는 --databases라 CREATE DATABASE와 USE를 담는다). 권한은 mysql 시스템 DB에 있어 DROP DATABASE로 사라지지 않고,
# 공개 사용자 테이블 권한은 api가 기동하며 다시 적용·검증한다. mysql 가져오기는 단일 트랜잭션이 아니다 — 중간에 실패하면 이 스크립트를 다시 실행한다(멱등).
docker compose exec -T mysql sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" exec mysql -uroot -e "DROP DATABASE IF EXISTS blog"'
docker compose exec -T mysql sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" exec mysql -uroot' < "$src/blog.sql"
```
파일 머리 주석의 "postgres 초기화 스크립트가 롤과 빈 DB를 만들고"는 "mysql init 스크립트가 사용자와 빈 DB를 만들고"로 바꾼다. `blog.dump`는 `blog.sql`로 바꾼다.

- [ ] **Step 5: 스모크**

`smoke/run.sh`: `pg_password`를 `mysql_root_password`로, `.env`에 쓰는 키 `POSTGRES_PASSWORD`를 `MYSQL_ROOT_PASSWORD`로 바꾼다. 앱·공개 비밀번호를 만드는 `random`이 영문·숫자만 내는지 확인한다. DB 롤 단계(100-117행)를 교체한다:
```bash
step "DB 사용자: 앱은 전역 권한이 없고, 공개 사용자는 허용 테이블 읽기만 한다"
# 복원 리허설 뒤에 돈다: 복원은 DB를 지우고 다시 만든다. 권한 경계가 조용히 열릴 수 있는 지점은 정확히 복원 직후다.
# -h mysql(네트워크 주소)로 붙어 TLS·비밀번호 인증을 실제로 거친다(소켓 접속은 인증 경로가 다르다).
mysql_as() { docker compose exec -T -e MYSQL_PWD="$2" mysql mysql -h mysql -u "$1" --ssl-mode=REQUIRED -D blog -N -B -e "$3"; }
# 부정 검사는 종료 코드가 아니라 메시지로 판정한다: "0이 아닌 종료 코드"에는 연결 실패·SQL 오타도 섞여 거짓 통과를 만든다.
deny() { # $1 사용자 $2 비밀번호 $3 SQL
  out="$(mysql_as "$1" "$2" "$3" 2>&1)" && { echo "허용돼서는 안 되는 문장이 성공했다: $3" >&2; exit 1; }
  case "$out" in *"denied"*|*"ERROR 1290"*) ;; *) echo "거부됐지만 이유가 권한이 아니다: $out" >&2; exit 1;; esac
}
test "$(mysql_as root "$mysql_root_password" "select count(*) from mysql.user where User in ('blog_app','blog_public') and Super_priv='N' and File_priv='N' and Process_priv='N' and Create_user_priv='N' and Grant_priv='N' and ssl_type='ANY'")" = "2"
test "$(mysql_as root "$mysql_root_password" "select concat(@@local_infile, ':', ifnull(@@secure_file_priv,'NULL'), ':', @@require_secure_transport, ':', @@global.transaction_isolation)")" = "0:NULL:1:READ-COMMITTED"
mysql_as blog_public "$public_password" 'select count(*) from `Posts`' > /dev/null
for sql in 'delete from `Posts`' 'set session transaction_read_only = off; delete from `Posts`' 'create table smoke_t(i int)' 'select * from `AdminState`' 'select * from `__EFMigrationsHistory`' 'select * from mysql.user' 'use mysql'; do
  deny blog_public "$public_password" "$sql"
done
deny blog_app "$app_password" "select 1 into outfile '/tmp/smoke'"
# TLS 없는 접속은 서버가 거부한다(require_secure_transport).
out="$(docker compose exec -T -e MYSQL_PWD="$app_password" mysql mysql -h mysql -u blog_app --ssl-mode=DISABLED -e 'select 1' 2>&1)" && { echo "비TLS 접속이 허용됐다" >&2; exit 1; }
case "$out" in *"insecure transport"*) ;; *) echo "비TLS 거부 이유가 예상과 다르다: $out" >&2; exit 1;; esac
```
(`deny`의 `ERROR 1290`은 `secure_file_priv`로 인한 INTO OUTFILE 거부 번호일 수 있다. 스파이크 S4e 관측값에 맞춘다.)

`smoke/smoke.test.mjs:313` — `net.connect({ host: 'mysql', port: 3306 })`, 테스트 이름은 'DB는 edge 네트워크에서 닿지 않는다'(그대로).

- [ ] **Step 6: 스모크 실행**

```powershell
bash deploy/smoke/run.sh
```
예상: 마지막 "통과". 실패하면 단계 이름으로 원인을 좁힌다. 특히 볼 것:
- mysql 컨테이너 기동 권한(cap_add, tmpfs) — `docker compose logs mysql`로 확인한다.
- `blog_app` 권한 부족으로 마이그레이션 실패 — `AppPrivileges`와 init 스크립트를 **함께** 고치고 Task 4 테스트를 다시 돈다.

- [ ] **Step 7: 커밋**

```powershell
dotnet test PortfolioBlog.slnx --filter "FullyQualifiedName~DeployInitScriptTests"   # PASS
git add -A deploy PortfolioBlog.Api.Tests/Infrastructure/DeployInitScriptTests.cs
git commit -m "수정: 배포 스택을 MySQL로 교체하고 권한 경계·망 격리 스모크를 다시 통과" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 10: CI와 브라우저 E2E

**Files:**
- Modify: `.github/workflows/ci.yml:54-68`, `PortfolioBlog.Web/scripts/e2e-prepare.mjs`, `PortfolioBlog.Web/playwright.config.ts:13-20`

- [ ] **Step 1: e2e-prepare.mjs**

`ensurePostgres`를 `ensureMySql`로 교체하고, 환경변수 `E2E_PG_*`는 `E2E_DB_*`로 바꾼다:
```js
function ensureMySql(port, password) {
  if (process.env.E2E_SKIP_DOCKER === '1') return // CI: 워크플로의 services.mysql을 쓴다
  const name = 'pb-e2e-mysql'
  spawnSync('docker', ['rm', '-f', name], { stdio: 'ignore' }) // 앞 실행의 데이터로 시작하지 않는다
  execFileSync('docker', ['run', '-d', '--name', name, '-e', `MYSQL_ROOT_PASSWORD=${password}`, '-e', 'MYSQL_DATABASE=blog_e2e',
    '-p', `127.0.0.1:${port}:3306`, 'mysql:8.4', '--transaction-isolation=READ-COMMITTED', '--character-set-server=utf8mb4', '--local-infile=0'], { stdio: 'inherit' })
  // TCP(-h 127.0.0.1)로 실제 쿼리를 한다: init 중 임시 서버는 네트워크를 닫아 두므로 이것이 성공하면 init이 끝난 것이다.
  for (let i = 0; i < 120; i++) {
    if (spawnSync('docker', ['exec', '-e', `MYSQL_PWD=${password}`, name, 'mysql', '-h', '127.0.0.1', '-uroot', '-N', '-e', 'SELECT 1', 'blog_e2e'], { stdio: 'ignore' }).status === 0) return
    Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 500)
  }
  throw new Error('MySQL 컨테이너가 준비되지 않았습니다.')
}
```
본문의 `const port = process.env.E2E_DB_PORT ?? '3307'`, `const dbPassword = process.env.E2E_DB_PASSWORD ?? randomBytes(12).toString('hex')`, `ensureMySql(port, dbPassword)`를 바꾸고, `env.json` 키 `pgPassword`를 `dbPassword`로 바꾼다. 파일 머리 주석의 "PostgreSQL 컨테이너"도 바꾼다.

- [ ] **Step 2: playwright.config.ts**

```ts
const prepared = JSON.parse(readFileSync(envFile, 'utf8')) as { port: string; dbPassword: string; adminPassword: string; hash: string }
...
const connection = ['Server=localhost', `Port=${prepared.port}`, 'Database=blog_e2e', 'User ID=root', `Password=${prepared.dbPassword}`, 'SslMode=Required'].join(';')
```

- [ ] **Step 3: ci.yml web-e2e**

```yaml
    services:
      mysql:
        image: mysql:8.4
        env:
          MYSQL_ROOT_PASSWORD: e2e-ci-dummy
          MYSQL_DATABASE: blog_e2e
        ports:
          - 3307:3306
        # 서비스 컨테이너에는 명령 인자를 줄 수 없다 — READ COMMITTED는 앱 인터셉터가 보장한다(스펙 D7).
        options: >-
          --health-cmd "mysql -h 127.0.0.1 -uroot -pe2e-ci-dummy -N -e 'SELECT 1' blog_e2e"
          --health-interval 5s --health-timeout 5s --health-retries 24
    env:
      E2E_SKIP_DOCKER: '1'
      E2E_DB_PORT: '3307'
      E2E_DB_PASSWORD: e2e-ci-dummy
```
`test` 잡은 Testcontainers를 쓰므로 바꿀 것이 없다(이미지 이름은 코드에 있다).

- [ ] **Step 4: 로컬 E2E**

```powershell
cd PortfolioBlog.Web; npm run e2e:prepare; npm run e2e
```
예상: Chromium·Firefox 전부 PASS.

- [ ] **Step 5: 커밋과 푸시, CI 확인**

```powershell
git add -A .github PortfolioBlog.Web
git commit -m "수정: CI와 브라우저 E2E의 DB를 MySQL로 교체" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git push -u origin feat/mysql-migration
gh run watch
```
예상: test·web·web-e2e·deploy-smoke 네 잡 모두 green.

---

### Task 11: 문서와 인계

**Files:**
- Modify: `README.md`, `docs/architecture.md`, `docs/security.md`, `docs/development.md`, `docs/testing.md`, `docs/configuration.md`, `docs/deployment.md`, `docs/history.md`, `docs/worklog.md`, `deploy/OPERATIONS.md`, `CLAUDE.md`, `AGENTS.md`, `plan/mysql_migration_0926.md`(상태·실측 반영), `plan/resume_guide_0921.md`

- [ ] **Step 1: 사람이 읽는 문서 갱신**

```powershell
rg -n -i "postgres|npgsql|psql|pg_|xmin|ilike|5432" README.md docs --glob '!docs/generated/**' deploy/OPERATIONS.md CLAUDE.md AGENTS.md
```
나온 곳을 모두 MySQL 기준으로 고친다. 핵심 내용:
- `docs/development.md` 로컬 절차(46-65행)를 교체한다. Docker 방식은 Task 2 Step 9의 `docker run` 명령이다. 설치 방식은 `winget install Oracle.MySQL`, `my.ini`에 `transaction-isolation=READ-COMMITTED`와 `character-set-server=utf8mb4`, `dotnet user-secrets set "ConnectionStrings:Default" "Server=localhost;Port=3306;Database=blog_dev;User ID=root;Password=<값>;SslMode=Required"`이다. Windows 설치본은 `lower_case_table_names=1`이 기본이라 테이블 이름이 소문자로 보인다(권한 검증은 대소문자 무시로 처리됨)는 주의를 붙인다.
- `docs/security.md`와 `docs/architecture.md`: 대체표 D2·D3·D4·D5·D7·D17을 요약해 옮긴다.
- `docs/deployment.md:126` 알려진 공백("blog_public이 postgres·template1에 CONNECT")은 **해소됨**으로 바꾼다(R5, 스모크가 `use mysql` 거부를 확인).
- `deploy/OPERATIONS.md`에 적는다:
  - 세션 리셋 명령은 `UPDATE AdminState SET SessionEpoch = SessionEpoch + 1`(mysql 클라이언트)이다.
  - 비밀번호 교체는 `ALTER USER 'blog_app'@'%' IDENTIFIED BY '…'` 후 `.env` 갱신과 `docker compose up -d`로 한다.
  - 초과 권한으로 기동이 실패하면(R6) `SHOW GRANTS FOR 'blog_public'@'%'`와 `REVOKE`로 처리한다.
  - **마이그레이션 전 백업**(D18: DDL 자동 커밋)을 한다.
  - 8.4 패치 업그레이드 절차를 둔다.
- `docs/history.md`·`docs/worklog.md`: MySQL 전환 항목을 추가한다(날짜·브랜치·PR).

- [ ] **Step 2: CLAUDE.md·AGENTS.md 구성 절**

- `PortfolioBlog.Web` 줄의 "실제 백엔드 + PostgreSQL"을 "실제 백엔드 + MySQL"로 바꾼다.
- `deploy/` 줄의 "caddy·api·postgres", "`postgres-init`(DB 롤 셋 …)", "`web-e2e` 잡(서비스 컨테이너 PostgreSQL …)"을 MySQL 기준으로 바꾼다.
- 플랜 표 `plan/mysql_migration_0926.md` 행의 "(설계, 구현 전)"을 "(구현 완료, PR #N)"으로 바꾸고, `plan/mysql_migration_impl_0926.md` 행을 추가한다.
- AGENTS.md에도 같은 변경을 적용한다(미러 동기화 규칙).

- [ ] **Step 3: 스펙 갱신**

`plan/mysql_migration_0926.md`:
- 상태를 "구현 완료"로 바꾼다.
- 2.4절 번호를 "예상"에서 "실측"으로 바꾼다(Task 0 report.md 반영).
- 7절에 구현 중 새로 내린 판정을 추가한다(예: 태그 교착 처리 방식, S3 관측).

- [ ] **Step 4: 생성 문서(doc-harness)**

메시지 전체가 `문서화`인 요청으로 `doc-harness` 스킬을 실행해 `docs/generated/`를 증분 갱신한다. ADR-003은 스펙 8절 ADR-011로 대체하고 ADR-008은 개정한다. 실행 증빙은 `doc-harness/workspace/runs/<run>/run.json`이다. 비용이 크면(과거 증분 $149) 실행 전에 사용자에게 알린다.

- [ ] **Step 5: 최종 검증**

```powershell
dotnet build PortfolioBlog.slnx -warnaserror
dotnet test PortfolioBlog.slnx
cd PortfolioBlog.Web; npm run lint; npm run typecheck; npm test; npm run build; cd ..
bash deploy/smoke/run.sh
rg -n "Npgsql|PostgresException|pg_advisory|ILike|xmin" PortfolioBlog.Api PortfolioBlog.Api.Tests deploy .github PortfolioBlog.Web/scripts PortfolioBlog.Web/playwright.config.ts
pwsh scripts/harness-audit.ps1
```
예상: 전부 통과하고 잔존 검색은 0건이다(주석의 이력 언급은 허용하되 현재 동작처럼 읽히면 안 된다). 하네스 감사는 CLAUDE.md·AGENTS.md 미러 동기화 PASS여야 한다.

- [ ] **Step 6: 커밋·PR**

```powershell
git add -A README.md docs deploy/OPERATIONS.md CLAUDE.md AGENTS.md plan
git commit -m "문서: MySQL 전환을 운영·개발·보안 문서와 설계 기록에 반영" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git push
gh pr create --title "MySQL 8.4로 저장소 교체" --body-file <스펙 요약 + 태스크별 결과 + 수용한 잔여 위험 + 🤖 Generated with [Claude Code](https://claude.com/claude-code)>
```
그 뒤 `superpowers:finishing-a-development-branch`로 병합 방식을 정한다.

---

## 자체 점검 결과(작성자)

**스펙 대비 누락 점검:**

| 스펙 항목 | 태스크 |
|---|---|
| D1 | 2 |
| D2 | 2·5 |
| D3·R1·R6 | 1·2·4 |
| D4 | 2·7 |
| D5·R3 | 1·2·6 |
| D6 | 2 |
| D7 | 2·7 |
| D8 | 2·7 |
| D9 | 2·8 |
| D10 | 2·8 |
| D11 | 2·3·8 |
| D12 | 1·2 |
| D13 | 0·2 |
| D14 | 0·2 |
| D15 | 2 |
| D16 | 1·2·8 |
| D17 | 2·8·9 |
| D18 | 11 |
| D19 | 9 |
| R2 | 2 |
| R4 | 0·3·9 |
| R5 | 9 |
| ADR-011 | 11 |

**Task 3에서 걷어낸 단언의 복귀처:**

| 걷어낸 단언 | 복귀 태스크 |
|---|---|
| PublicRoleGrantsTests | 4 |
| PublicDbContextTests | 5 |
| 잠긴 테이블 503 | 5 |
| 잠금 4종 | 6 |
| ErrorPipeline 2종 | 8 |
| Options 2종 | 폐기, AllowPublicKeyRetrieval·SslMode 검사로 대체(8) |

**알려진 불확실성**(Task 0이 판정): Oracle 프로바이더의 비동기 품질(S10), `UseCollation`·`IsDescending`·Like escape의 DDL/SQL 반영(S1·S9), MDL 대기 오류 번호(S3c), `MySqlException` 생성자(S8).
