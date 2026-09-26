using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
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

    /// <summary>끝 공백만 다른 두 정규화 태그 이름(<c>'abc'</c>·<c>'abc '</c>)이 둘 다 저장된다. PAD SPACE 콜레이션(<c>utf8mb4_bin</c>)이었다면
    /// 비교 전에 끝 공백을 채워 같은 값으로 보고 유니크 위반(1062)이 났다 — 스펙 D10의 "바이트 비교"는 NO PAD(<c>utf8mb4_0900_bin</c>)여야 성립한다.
    /// 값에 테스트 이름을 넣은 이유: 이 클래스는 픽스처 DB를 다른 케이스와 공유하므로 다른 태그와 겹치지 않게 한다.</summary>
    [Fact]
    public async Task TrailingSpaceVariants_AreDistinctUniqueValues()
    {
        using var _ = factory.CreateClient();
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Tags.Add(new Tag { Name = "nopad-abc", NormalizedName = "nopad-abc" });
        db.Tags.Add(new Tag { Name = "nopad-abc ", NormalizedName = "nopad-abc " });
        await db.SaveChangesAsync();
        Assert.Equal(2, await db.Tags.CountAsync(t => t.NormalizedName.StartsWith("nopad-abc")));
    }

    /// <summary>모델이 선언한 식별자 열 6개가 실제 스키마에서 <c>utf8mb4_0900_bin</c>(NO PAD)이다(마이그레이션이 콜레이션을 빠뜨리지 않았다).
    /// 상수가 아니라 리터럴과 비교하는 이유: 상수를 PAD SPACE 콜레이션으로 되돌리면 이 테스트가 같이 따라가 통과해 버리지 않게 한다.</summary>
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
            "SELECT CONCAT(c.COLLATION_NAME, ':', l.PAD_ATTRIBUTE) FROM information_schema.COLUMNS c " +
            "JOIN information_schema.COLLATIONS l ON l.COLLATION_NAME = c.COLLATION_NAME " +
            "WHERE c.TABLE_SCHEMA = DATABASE() AND c.TABLE_NAME = @t AND c.COLUMN_NAME = @c", connection);
        command.Parameters.AddWithValue("@t", table);
        command.Parameters.AddWithValue("@c", column);
        Assert.Equal("utf8mb4_0900_bin:NO PAD", (string?)await command.ExecuteScalarAsync());
    }

    /// <summary>공개 검색은 대소문자를 무시한다(<c>utf8mb4_0900_ai_ci</c> 콜레이션의 <c>LIKE</c>).</summary>
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
