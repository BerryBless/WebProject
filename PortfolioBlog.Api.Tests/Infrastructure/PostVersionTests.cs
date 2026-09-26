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
