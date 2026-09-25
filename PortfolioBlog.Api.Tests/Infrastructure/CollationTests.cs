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
