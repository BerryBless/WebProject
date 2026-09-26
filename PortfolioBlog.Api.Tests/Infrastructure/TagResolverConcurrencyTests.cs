using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary><see cref="TagResolver.ResolveIdsAsync"/>의 동시 생성(스펙 D8)을 실제 MySQL에서 검증한다. 순수 함수 검증은 DB 없는 <see cref="TagResolverTests"/>에 둔다.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>픽스처 공유:</b> 클래스 픽스처 ApiFactory 하나(DB 1개). 태그 이름은 이 클래스 전용 접두사(<c>동시-</c>)를 쓴다.</description></item>
/// <item><description><b>병렬 실행:</b> 컬렉션 내 직렬. 테스트 안에서는 16개 트랜잭션을 의도적으로 동시에 돌린다(각자 별도 스코프·연결).</description></item>
/// <item><description><b>외부 자원:</b> Docker MySQL. InnoDB의 중복 키 검사 잠금과 교착 감지(1213)를 실제로 거친다.</description></item>
/// </list>
/// </remarks>
[Collection("mysql")]
public sealed class TagResolverConcurrencyTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
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
}
