using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using PortfolioBlog.Api.Domain;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>모델에 선언된 모든 CHECK 제약이 실제로 위반을 거부하는지 검증한다. 제약을 추가하면서 여기에 위반 사례를 안 넣으면 실패한다(닫힌 세계).</summary>
/// <param name="factory">컬렉션이 공유하는 컨테이너를 바탕으로 클래스 전용 DB를 갖는 <see cref="ApiFactory"/> 클래스 픽스처.</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 실행되며, <see cref="ApiFactory"/>가 호스팅하는 인메모리 TestServer가 실제 MySQL 컨테이너에 TCP로 접속하므로 DB I/O는 실제 네트워크 왕복을 수반한다.</description></item>
/// <item><description><b>Memory Policy:</b> <paramref name="factory"/>는 클래스 픽스처로 1회 생성·공유된다. 각 케이스는 <c>await using</c>으로 자신의 DI 스코프와 <c>AppDbContext</c>를 스코프 종료 시 해제한다.</description></item>
/// <item><description><b>Concurrency:</b> <c>mysql</c> 컬렉션에 속해 같은 컬렉션의 다른 테스트 클래스와 순차 실행된다. 케이스마다 고유 slug·이름을 써서 서로 간섭하지 않는다.</description></item>
/// </list>
/// </remarks>
[Collection("mysql")]
public sealed class CheckConstraintCoverageTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>CHECK 제약과 무관한 필수 필드를 채운 최소 유효 글을 만든다(각 위반 사례가 필드 하나만 바꿔 특정 제약만 어기게 하는 기준점).</summary>
    private static Post NewPost(string slug) => new()
    {
        Slug = slug, Title = "제목", Summary = "", ContentMarkdown = "본문", CreatedAt = DbClock.UtcNow(), UpdatedAt = DbClock.UtcNow(),
    };

    /// <summary>CHECK 제약과 무관한 필수 필드를 채운 최소 유효 첨부를 만든다(<paramref name="fill"/>로 slug 없는 Sha256·경로 값을 고유하게 채운다).</summary>
    private static Attachment NewAttachment(char fill) => new()
    {
        FileName = "a.png", ContentType = "image/png", SizeBytes = 10, StoragePath = $"{fill}{fill}/x.png",
        Sha256 = new string(fill, 64), CreatedAt = DbClock.UtcNow(),
    };

    /// <summary>제약 이름 → 그 제약 하나만 위반하는 행을 추가하는 동작.</summary>
    private static readonly Dictionary<string, Action<AppDbContext>> Violations = new(StringComparer.Ordinal)
    {
        ["CK_Posts_Slug_Format"] = db => db.Posts.Add(NewPost("Bad_Slug")),
        ["CK_Posts_Title_NotBlank"] = db => { var p = NewPost("ck-title"); p.Title = "   "; db.Posts.Add(p); },
        ["CK_Posts_Content_Size"] = db => { var p = NewPost("ck-size"); p.ContentMarkdown = new string('a', AppDbContext.ContentMaxBytes + 1); db.Posts.Add(p); },
        ["CK_Posts_Series_Pair"] = db => { var p = NewPost("ck-pair"); p.SeriesOrder = 1; db.Posts.Add(p); },
        ["CK_Posts_SeriesOrder_Positive"] = db =>
        {
            var s = new Series { Slug = "ck-series", Title = "시리즈", Description = "" };
            var p = NewPost("ck-order"); p.Series = s; p.SeriesOrder = 0;
            db.Posts.Add(p);
        },
        ["CK_Series_Slug_Format"] = db => db.Series.Add(new Series { Slug = "-bad", Title = "t", Description = "" }),
        ["CK_Series_Title_NotBlank"] = db => db.Series.Add(new Series { Slug = "ck-series-title", Title = " ", Description = "" }),
        ["CK_Tags_Name_NotBlank"] = db => db.Tags.Add(new Tag { Name = " ", NormalizedName = "ck-blank" }),
        ["CK_Tags_Name_NoSlash"] = db => db.Tags.Add(new Tag { Name = "a/b", NormalizedName = "ck-slash" }),
        ["CK_AdminState_Single"] = db => db.AdminStates.Add(new AdminState { Id = 2, SessionEpoch = 1 }),
        ["CK_Attachments_Size"] = db => { var a = NewAttachment('1'); a.SizeBytes = 0; db.Attachments.Add(a); },
        ["CK_Attachments_Sha256"] = db => { var a = NewAttachment('2'); a.Sha256 = new string('G', 64); db.Attachments.Add(a); },
        ["CK_Attachments_ContentType"] = db => { var a = NewAttachment('3'); a.ContentType = "image/svg+xml"; db.Attachments.Add(a); },
        ["CK_Attachments_FileName_NotBlank"] = db => { var a = NewAttachment('4'); a.FileName = " "; db.Attachments.Add(a); },
    };

    /// <summary><see cref="Violations"/>에 등록된 제약 이름 전체를 <see cref="Violation_IsRejected_ByExactlyThatConstraint"/>의 이론 데이터로 돌려준다.</summary>
    /// <returns>제약 이름 하나씩을 담은 <c>object[]</c> 시퀀스.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> xUnit이 테스트 실행 전 이론 데이터 수집 단계에서 호출한다(테스트 스레드와 별개).</description></item>
    /// <item><description><b>Memory Policy:</b> <see cref="Violations"/>의 키 수만큼 <c>object[]</c> 배열을 지연 열거로 할당한다(즉시 전체를 리스트로 모으지 않는다).</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public static IEnumerable<object[]> ConstraintNames() => Violations.Keys.Select(static name => new object[] { name });

    /// <summary>선언된 CHECK 제약 집합과 <see cref="Violations"/>의 키 집합이 정확히 일치하는지 확인한다(닫힌 세계 — 제약을 추가하고 위반 사례를 빠뜨리면 실패).</summary>
    [Fact]
    public void EveryDeclaredCheckConstraint_HasAViolationCase()
    {
        using var _ = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        // AppDbContext.Model(런타임 모델)은 시작 성능을 위해 CHECK 제약 등 마이그레이션 전용 메타데이터를 들어내는 "읽기 최적화 모델"이라
        // GetCheckConstraints()를 호출하면 InvalidOperationException을 던진다(실측: "please use DbContext.GetService<IDesignTimeModel>().Model").
        // 설계 시점 모델은 그 메타데이터를 그대로 갖고 있으므로 DI가 구성한 IDesignTimeModel 서비스로 우회한다.
        var designTimeModel = scope.ServiceProvider.GetRequiredService<AppDbContext>().GetService<IDesignTimeModel>().Model;
        var declared = designTimeModel.GetEntityTypes()
            .SelectMany(e => e.GetCheckConstraints()).Select(c => c.Name!).Distinct().Order(StringComparer.Ordinal);
        Assert.Equal(declared, Violations.Keys.Order(StringComparer.Ordinal));
    }

    /// <summary><paramref name="constraint"/>의 위반 사례가 오류 번호 3819(ER_CHECK_CONSTRAINT_VIOLATED)로 거부되고, 그 예외 메시지의 제약 이름이 바로 <paramref name="constraint"/>인지 확인한다(다른 제약에 먼저 걸려 통과하는 가짜 통과를 막는다).</summary>
    /// <param name="constraint">위반시킬 CHECK 제약 이름.</param>
    [Theory]
    [MemberData(nameof(ConstraintNames))]
    public async Task Violation_IsRejected_ByExactlyThatConstraint(string constraint)
    {
        using var _ = factory.CreateClient();
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Violations[constraint](db);
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var mysql = Assert.IsType<MySqlException>(ex.InnerException);
        Assert.Equal(3819, mysql.Number);
        // MySQL은 제약 이름을 별도 속성으로 주지 않는다. 서버 메시지 "Check constraint 'X' is violated."에서 확인한다
        // (메시지는 서버가 만들고 MySqlConnector는 그대로 Message에 싣는다).
        Assert.Contains($"'{constraint}'", mysql.Message, StringComparison.Ordinal);
    }
}
