using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using PortfolioBlog.Api.Domain;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>
/// 초기 마이그레이션이 적용하는 DB 스키마(테이블·CHECK·UNIQUE 제약·시드 데이터·행 버전)를 실제 MySQL 컨테이너로 검증한다.
/// </summary>
/// <param name="factory">컬렉션이 공유하는 컨테이너를 바탕으로 클래스 전용 DB를 갖는 <see cref="ApiFactory"/> 클래스 픽스처.</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 실행된다. <see cref="ApiFactory"/>가 호스팅하는 인메모리 TestServer가
/// 실제 MySQL 컨테이너에 TCP로 접속하므로 DB I/O는 실제 네트워크 왕복을 수반한다.</description></item>
/// <item><description><b>Memory Policy:</b> 팩토리는 <see cref="IClassFixture{TFixture}"/>로 클래스 단위 1회 생성·공유된다.
/// 각 테스트 메서드는 <c>await using</c>으로 자신의 DI 스코프와 <c>AppDbContext</c>를 스코프 종료 시 해제한다.</description></item>
/// <item><description><b>Concurrency:</b> 팩토리·HttpClient는 Thread-safe하나, 스코프 안 <c>AppDbContext</c>는 단일 스레드 전용이며
/// 테스트 간에는 클래스별 고유 DB로 격리되어 데이터 간섭이 없다.</description></item>
/// <item><description><b>Blocking:</b> 모든 DB 접근은 <c>await</c>로 비동기 대기하며 동기 블로킹이 없다.</description></item>
/// </list>
/// </remarks>
[Collection("mysql")]
public sealed class DatabaseSchemaTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const int CheckViolation = 3819;
    private const int UniqueViolation = 1062;

    private static Post NewPost(string slug) => new()
    {
        Slug = slug, Title = "제목", Summary = "", ContentMarkdown = "본문",
        CreatedAt = DbClock.UtcNow(), UpdatedAt = DbClock.UtcNow(),
    };

    // MySQL 예외의 오류 번호만 필요한 여러 테스트가 "스코프 열기 → 변경 적용 → SaveChangesAsync → 실패 시 오류 번호 추출"을
    // 반복하므로 공통 절차를 private 헬퍼로 묶어 테스트 본문을 단언에 집중시킨다.
    private async Task<int?> SaveAndGetErrorNumberAsync(Action<AppDbContext> arrange)
    {
        using var client = factory.CreateClient(); // 호스트 기동 → Migrate()
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        arrange(db);
        try { await db.SaveChangesAsync(); return null; }
        catch (DbUpdateException ex) when (ex.InnerException is MySqlException mysql) { return mysql.Number; }
    }

    /// <summary>
    /// 앱 시작 시 <c>InitialCreate</c> 마이그레이션이 적용되고, <see cref="AdminState"/> 시드가 <c>SessionEpoch=1</c>로 존재하며,
    /// <see cref="Post"/>를 저장·재조회하면 <see cref="DbClock"/> 절삭 덕분에 <see cref="Post.CreatedAt"/>이 정확히 일치하고
    /// <see cref="Post.Version"/>이 추가 시 1인지(<see cref="PostVersionInterceptor"/>) 검증한다.
    /// </summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 이 테스트 전용 스코프·<c>AppDbContext</c>만 사용하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 스코프·<c>Post</c> 인스턴스 각 1개. <c>AsNoTracking()</c> 조회는 변경 추적기 항목을 남기지 않는다.</description></item>
    /// <item><description><b>Blocking:</b> 모든 DB 호출을 <c>await</c>로 비동기 대기하며 동기 블로킹이 없다.</description></item>
    /// </list>
    /// </remarks>
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
        Assert.Equal(1u, loaded.Version); // 추가 시 PostVersionInterceptor가 1로 둔다
    }

    /// <summary>슬러그 형식 CHECK 제약(<c>CK_Posts_Slug_Format</c>)이 대문자·공백·선행 하이픈·연속 하이픈을 거부하는지 검증한다.</summary>
    /// <param name="slug">DB CHECK 제약을 위반하도록 고른 잘못된 슬러그 값.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 케이스마다 독립된 스코프를 사용하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="SaveAndGetErrorNumberAsync"/> 호출당 스코프·<c>Post</c> 인스턴스 각 1개.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. <c>SaveChangesAsync</c> 실패를 <c>await</c>로 대기한다.</description></item>
    /// </list>
    /// </remarks>
    [Theory]
    [InlineData("Bad-Slug")]
    [InlineData("bad slug")]
    [InlineData("-bad")]
    [InlineData("bad--slug")]
    public async Task Post_InvalidSlug_IsRejectedByCheckConstraint(string slug) =>
        Assert.Equal(CheckViolation, await SaveAndGetErrorNumberAsync(db => db.Posts.Add(NewPost(slug))));

    /// <summary><see cref="Post.SeriesOrder"/>만 설정하고 <see cref="Post.SeriesId"/>는 비운 글이 <c>CK_Posts_Series_Pair</c>로 거부되는지 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 이 테스트 전용 스코프만 사용하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 스코프·<c>Post</c> 인스턴스 각 1개.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. <c>SaveChangesAsync</c> 실패를 <c>await</c>로 대기한다.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task Post_SeriesOrderWithoutSeries_IsRejected() =>
        Assert.Equal(CheckViolation, await SaveAndGetErrorNumberAsync(db =>
        {
            var p = NewPost("order-without-series");
            p.SeriesOrder = 1;
            db.Posts.Add(p);
        }));

    /// <summary>동일 슬러그로 두 번째 글을 저장하면 <c>IX_Posts_Slug</c> 고유 인덱스 위반으로 거부되는지 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 순차적으로 두 스코프를 여닫으며 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 호출마다 스코프·<c>Post</c> 인스턴스 각 1개, 총 2세트.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 두 번의 <c>SaveChangesAsync</c>를 순차 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task Post_DuplicateSlug_IsRejectedByUniqueIndex()
    {
        Assert.Null(await SaveAndGetErrorNumberAsync(db => db.Posts.Add(NewPost("dup-slug"))));
        Assert.Equal(UniqueViolation, await SaveAndGetErrorNumberAsync(db => db.Posts.Add(NewPost("dup-slug"))));
    }

    /// <summary>이름에 '/'가 포함된 태그가 <c>CK_Tags_Name_NoSlash</c>로 거부되는지 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 이 테스트 전용 스코프만 사용하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 스코프·<see cref="Tag"/> 인스턴스 각 1개.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. <c>SaveChangesAsync</c> 실패를 <c>await</c>로 대기한다.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task Tag_SlashInName_IsRejected() =>
        Assert.Equal(CheckViolation, await SaveAndGetErrorNumberAsync(db =>
            db.Tags.Add(new Tag { Name = "a/b", NormalizedName = "a/b" })));

    /// <summary><see cref="AdminState.Id"/>가 1이 아닌 두 번째 행이 <c>CK_AdminState_Single</c>로 거부되는지 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 이 테스트 전용 스코프만 사용하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 스코프·<see cref="AdminState"/> 인스턴스 각 1개.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. <c>SaveChangesAsync</c> 실패를 <c>await</c>로 대기한다.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task AdminState_SecondRow_IsRejected() =>
        Assert.Equal(CheckViolation, await SaveAndGetErrorNumberAsync(db =>
            db.AdminStates.Add(new AdminState { Id = 2, SessionEpoch = 1 })));
}
