using Microsoft.Extensions.DependencyInjection;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>공개 조회의 정렬·페이지·태그·시리즈 이웃·검색 이스케이프를 실제 PostgreSQL로 검증한다. 테스트마다 격리된 DB를 쓴다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 실행된다. 컬렉션 픽스처 <see cref="PostgresContainerFixture"/>(컨테이너 자체)만 공유하고,
/// 각 테스트 케이스는 <c>new ApiFactory(pg, …)</c>로 자신만의 DB를 만들어 <c>using</c>으로 해제한다.</description></item>
/// <item><description><b>Memory Policy:</b> 케이스마다 <see cref="ApiFactory"/>·시드 데이터(<see cref="PublicSeed"/>)·조회 결과 프로젝션을 각각 새로 할당한다.</description></item>
/// <item><description><b>Concurrency:</b> 케이스 간 공유 가변 상태가 없으므로(DB가 케이스마다 다름) 병렬 실행에 안전하다.</description></item>
/// <item><description><b>Blocking:</b> 비동기 Non-blocking. 시드·조회 왕복을 모두 <c>await</c>한다.</description></item>
/// </list>
/// </remarks>
[Collection("postgres")]
public sealed class PublicQueriesTests(PostgresContainerFixture pg)
{
    private static async Task<T> QueryAsync<T>(ApiFactory factory, Func<PublicDbContext, Task<T>> query)
    {
        await using var scope = factory.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<PublicDbContext>());
    }

    /// <summary>최신순 20개씩, 같은 시각이면 Id로 안정 정렬, 태그는 정규화명 순.</summary>
    [Fact]
    public async Task Latest_PagesNewestFirst()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        var t0 = DbClock.UtcNow().AddDays(-1);
        for (var i = 0; i < 25; i++) await PublicSeed.PostAsync(factory, $"post-{i:00}", $"글 {i}", tags: i == 24 ? new[] { "b", "A" } : null, createdAt: t0.AddMinutes(i));

        var first = await QueryAsync(factory, db => PublicQueries.LatestAsync(db, 1, CancellationToken.None));
        Assert.Equal(25, first.Total);
        Assert.Equal(2, first.LastPage);
        Assert.Equal(20, first.Items.Count);
        Assert.Equal("post-24", first.Items[0].Slug);
        Assert.Equal(new[] { "A", "b" }, first.Items[0].Tags.Select(t => t.Name));

        var second = await QueryAsync(factory, db => PublicQueries.LatestAsync(db, 2, CancellationToken.None));
        Assert.Equal(new[] { "post-04", "post-03", "post-02", "post-01", "post-00" }, second.Items.Select(p => p.Slug));
    }

    /// <summary>시리즈 이웃은 (SeriesOrder, CreatedAt, Id) 순이며 순서가 같아도 안정적이다. 시리즈 밖 글은 이웃이 없다.</summary>
    [Fact]
    public async Task GetPost_ResolvesSeriesNeighbors_InStableOrder()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        var series = await PublicSeed.SeriesAsync(factory, "net-internals", ".NET 내부");
        var t0 = DbClock.UtcNow().AddDays(-1);
        await PublicSeed.PostAsync(factory, "part-a", "A", seriesId: series.Id, seriesOrder: 1, createdAt: t0);
        await PublicSeed.PostAsync(factory, "part-b", "B", seriesId: series.Id, seriesOrder: 2, createdAt: t0.AddMinutes(2)); // 순서 2가 둘
        await PublicSeed.PostAsync(factory, "part-c", "C", seriesId: series.Id, seriesOrder: 2, createdAt: t0.AddMinutes(1));
        await PublicSeed.PostAsync(factory, "loner", "혼자");

        var c = await QueryAsync(factory, db => PublicQueries.GetPostAsync(db, "part-c", CancellationToken.None));
        Assert.Equal("part-a", c!.Previous?.Slug);
        Assert.Equal("part-b", c.Next?.Slug);           // 같은 순서 2 안에서는 먼저 쓴 c가 앞
        Assert.Equal("net-internals", c.Series?.Slug);

        var loner = await QueryAsync(factory, db => PublicQueries.GetPostAsync(db, "loner", CancellationToken.None));
        Assert.Null(loner!.Series);
        Assert.Null(loner.Previous);
        Assert.Null(await QueryAsync(factory, db => PublicQueries.GetPostAsync(db, "no-such", CancellationToken.None)));
    }

    /// <summary>태그는 정규화명으로 찾고, 검색어의 %·_·\는 글자 그대로다.</summary>
    [Fact]
    public async Task ByTag_And_Search_UseNormalizedNames_AndLiteralWildcards()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        await PublicSeed.PostAsync(factory, "sharp", "C# 12", tags: ["C#"]);
        await PublicSeed.PostAsync(factory, "pct", "100% 완료", markdown: "경로는 a_b 이다");
        await PublicSeed.PostAsync(factory, "plain", "100 완료", markdown: "경로는 aXb 이다");

        var tagged = await QueryAsync(factory, db => PublicQueries.ByTagAsync(db, "c#", 1, CancellationToken.None));
        Assert.Equal("C#", tagged!.Value.Tag.Name);
        Assert.Equal(new[] { "sharp" }, tagged.Value.Posts.Items.Select(p => p.Slug));
        Assert.Null(await QueryAsync(factory, db => PublicQueries.ByTagAsync(db, "없는태그", 1, CancellationToken.None)));

        Assert.Equal(new[] { "pct" }, (await QueryAsync(factory, db => PublicQueries.SearchAsync(db, "100%", 1, CancellationToken.None))).Items.Select(p => p.Slug));
        Assert.Equal(new[] { "pct" }, (await QueryAsync(factory, db => PublicQueries.SearchAsync(db, "a_b", 1, CancellationToken.None))).Items.Select(p => p.Slug));
        Assert.Equal(2, (await QueryAsync(factory, db => PublicQueries.SearchAsync(db, "완료", 1, CancellationToken.None))).Total);
    }

    /// <summary>0·음수·오버플로 경계의 page는 조용히 틀린 결과(예: 음수 OFFSET이 0으로 취급돼 1쪽이 나옴)를 내는 대신 즉시 예외로 거부된다(Fix round 1, 심층 방어).</summary>
    [Fact]
    public async Task Latest_OutOfRangePage_Throws()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?>());
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PublicDbContext>();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PublicQueries.LatestAsync(db, 0, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PublicQueries.LatestAsync(db, -1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PublicQueries.LatestAsync(db, int.MaxValue, CancellationToken.None));
    }
}
